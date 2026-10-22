package wechat

import (
	"bytes"
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"io"
	"net"
	"net/http"
	"net/url"
	"strings"
	"sync"
	"time"

	"golang.org/x/sync/singleflight"
)

// MPConfig is the official account's identity plus the API base.
//
// AppID and AppSecret are the *official account's*, not the website app's.
// The two are different applications on the open platform with different
// AppIDs, and a client wired with the wrong pair does not fail loudly at
// start-up: it acquires a token for the other application and then fails on
// the first call that cares which application it is. The service's config
// therefore refuses to start when the two AppIDs are equal — the one mistake
// that is silent at every other layer.
//
// APIBase is normally https://api.weixin.qq.com. Timeout is the per-request
// ceiling; zero means defaultTimeout, the same default the website app's
// client uses.
type MPConfig struct {
	AppID     string
	AppSecret string
	APIBase   string
	Timeout   time.Duration

	// DailyCallLimit is how many API calls one day of this application may
	// make. WeChat's published ceiling is 2000 per day per account, and a
	// client that quietly runs past it gets its access token revoked with no
	// explanation, which is a failure the operator cannot diagnose from the
	// symptom. So the budget is tracked here and refused locally, loudly,
	// before the call that would have tripped it.
	//
	// Zero means the default ceiling rather than "unlimited": a client with
	// no budget is a client that will be revoked eventually.
	DailyCallLimit int
}

// mpDailyCallLimit is WeChat's published per-day ceiling for an official
// account's API calls.
const mpDailyCallLimit = 2000

// mpTokenSkew is how long before the stated expiry a token is treated as
// already expired.
//
// A token is valid for 7200 seconds. Spending the last minutes of that window
// is how a call ends in 40001 "invalid credential" on a token that was valid
// when the request was built — and the second call that follows, with a
// freshly-minted token, succeeds, so the log shows a failure that a retry
// "fixed" and no explanation of why it happened. Five minutes of margin is
// enough to cover a slow request and short enough that the budget is not
// meaningfully wasted.
const mpTokenSkew = 5 * time.Minute

// MPClient is the official account's client.
//
// It exists for the unionid bridge: the website app's sign-in returns an
// OpenID and a UnionID but no nickname and no avatar, and the nickname and
// avatar live on the official account, reachable only by turning the UnionID
// into that account's OpenID first.
//
// The interesting part of this client is not the calls — they are two GETs —
// but the access token. An official account's access_token is a shared
// resource: one per application, valid for 7200 seconds, and every call that
// uses it counts against a daily budget that is also per application. So the
// client keeps one token, refreshes it when it is about to expire, and
// refuses to spend the daily budget on a call it cannot make.
//
// The client is safe for concurrent use and is meant to be created once: the
// connection pool and the token are the whole point, and a client per call
// would re-acquire a token per call until WeChat revoked it.
type MPClient struct {
	appID     string
	appSecret string
	apiBase   string
	http      *http.Client
	now       func() time.Time

	dailyLimit int

	// mu guards the fields below it. The token is read on every call and
	// written on every refresh, so it is behind a lock rather than a
	// sync/atomic: the value is a string plus a deadline, and an atomic
	// pointer to a struct would be the same amount of code with more to
	// forget.
	mu           sync.Mutex
	token        string
	tokenExpires time.Time
	dayStart     time.Time
	callsToday   int

	// refresh collapses concurrent token acquisitions into one. Without it,
	// a burst of sign-ins arriving together would each mint its own token,
	// and WeChat keeps only the newest one per application — so every token
	// but the last would be dead on arrival, and the calls that used them
	// would fail with 40001 for no reason a reader could see.
	refresh singleflight.Group
}

// NewMPClient builds a client with a pooled, time-bounded transport. It does
// not use http.DefaultClient: that one has no timeout at all, which turns a
// stalled connection into a sign-in that hangs until the user's patience runs
// out.
func NewMPClient(cfg MPConfig) *MPClient {
	timeout := cfg.Timeout
	if timeout <= 0 {
		timeout = defaultTimeout
	}
	limit := cfg.DailyCallLimit
	if limit <= 0 {
		limit = mpDailyCallLimit
	}
	transport := &http.Transport{
		Proxy:                 http.ProxyFromEnvironment,
		DialContext:           (&net.Dialer{Timeout: 5 * time.Second, KeepAlive: 30 * time.Second}).DialContext,
		ForceAttemptHTTP2:     true,
		MaxIdleConns:          64,
		MaxIdleConnsPerHost:   16,
		IdleConnTimeout:       90 * time.Second,
		TLSHandshakeTimeout:   5 * time.Second,
		ExpectContinueTimeout: time.Second,
	}
	return &MPClient{
		appID:      cfg.AppID,
		appSecret:  cfg.AppSecret,
		apiBase:    strings.TrimRight(cfg.APIBase, "/"),
		http:       &http.Client{Transport: transport, Timeout: timeout},
		now:        time.Now,
		dailyLimit: limit,
	}
}

// ---------------------------------------------------------------------------
// Errors
// ---------------------------------------------------------------------------

// QuotaError is the daily call budget running out.
//
// It is not transient in the sense the rest of this package uses the word —
// retrying in a second will not help, because the budget resets on the day
// boundary and not before. But it is also not a failure of the call itself:
// a bridge that cannot reach the official account degrades to signing the
// buyer in without a nickname, which is the state the service was in before
// the bridge existed and is perfectly usable. So the caller logs it and
// carries on, and the distinction the error type draws is exactly that:
// "WeChat will not answer today" rather than "the call is broken".
type QuotaError struct {
	Op    string
	Limit int
}

func (e *QuotaError) Error() string {
	return fmt.Sprintf("wechat: %s: the official account's daily call budget (%d) is exhausted", e.Op, e.Limit)
}

// Transient reports false: the budget does not come back until the day
// boundary, so no amount of retrying within this call changes anything.
func (e *QuotaError) Transient() bool { return false }

// ---------------------------------------------------------------------------
// Access token
// ---------------------------------------------------------------------------

// accessToken returns a token that is good for at least one more call.
//
// The token is cached until mpTokenSkew before its stated expiry, and a
// refresh is collapsed so that concurrent callers share one acquisition.
func (c *MPClient) accessToken(ctx context.Context) (string, error) {
	if token, ok := c.cachedToken(); ok {
		return token, nil
	}

	// The refresh is shared, so it must not die with the caller that happened
	// to start it. A sign-in that is cancelled — the buyer closed the window —
	// would otherwise cancel the acquisition for every other sign-in waiting
	// on it, and they would all fail for a reason that has nothing to do with
	// them. The values are kept, the cancellation is not, and the client's own
	// timeout bounds the request instead.
	flightCtx, cancel := context.WithTimeout(context.WithoutCancel(ctx), c.http.Timeout)
	defer cancel()

	value, err, _ := c.refresh.Do("token", func() (any, error) {
		// Another caller may have refreshed while this one queued behind the
		// flight, so the cache is checked again inside it.
		if token, ok := c.cachedToken(); ok {
			return token, nil
		}
		return c.fetchToken(flightCtx)
	})
	if err != nil {
		return "", err
	}
	token, _ := value.(string)
	if token == "" {
		return "", &MalformedError{Op: "acquire access token", Body: []byte(`{"access_token":""}`)}
	}
	return token, nil
}

// cachedToken returns the cached token when it is still good for one more
// call.
func (c *MPClient) cachedToken() (string, bool) {
	c.mu.Lock()
	defer c.mu.Unlock()
	if c.token == "" {
		return "", false
	}
	if !c.now().Add(mpTokenSkew).Before(c.tokenExpires) {
		return "", false
	}
	return c.token, true
}

// invalidateToken drops the cached token. It is called when WeChat says the
// token is no longer valid: an application may hold only one live token, so
// acquiring another one — by this client or by an operator at the console —
// retires the one in hand.
func (c *MPClient) invalidateToken() {
	c.mu.Lock()
	defer c.mu.Unlock()
	c.token = ""
	c.tokenExpires = time.Time{}
}

// fetchToken asks WeChat for an access token.
func (c *MPClient) fetchToken(ctx context.Context) (string, error) {
	const op = "acquire access token"
	if c.appID == "" || c.appSecret == "" {
		return "", &APIError{Op: op, Status: http.StatusBadRequest, ErrCode: -1, ErrMsg: "the official account's credentials are not configured"}
	}
	q := url.Values{}
	q.Set("grant_type", "client_credential")
	q.Set("appid", c.appID)
	q.Set("secret", c.appSecret)

	// No errcode/errmsg fields: checkWechatError has already turned a failure
	// into an APIError before this struct is parsed, so a body that carried
	// them would never reach it.
	var out struct {
		AccessToken string `json:"access_token"`
		ExpiresIn   int64  `json:"expires_in"`
	}
	if err := c.do(ctx, op, http.MethodGet, "/cgi-bin/token", q, nil, &out); err != nil {
		return "", err
	}
	if out.AccessToken == "" {
		// A 200 with no token and no error is a response we cannot act on.
		// Treating it as success would hand out an empty credential and fail
		// on the call that uses it, one layer further from the cause.
		return "", &MalformedError{Op: op, Body: []byte(`{"access_token":""}`)}
	}
	expires := time.Duration(out.ExpiresIn) * time.Second
	if expires <= 0 {
		// WeChat always states the lifetime. A response without it is not
		// trusted with the default: caching a token for a guessed window is
		// how a client ends up using a token that expired mid-call.
		expires = 7200 * time.Second
	}
	c.mu.Lock()
	c.token = out.AccessToken
	c.tokenExpires = c.now().Add(expires)
	c.mu.Unlock()
	return out.AccessToken, nil
}

// reserveCall spends one unit of the daily budget, or refuses.
//
// The counter is per calendar day in UTC, not per token: WeChat's ceiling is
// described per account, and a new token does not undo the calls already made
// today. Counting per token would understate the account's usage by the
// number of tokens it has had.
//
// The call is counted before it is made, not after it succeeds. A call that
// fails still cost a slot on WeChat's side, and a budget that only counted
// successes would let a client with a broken endpoint spend the day's
// allowance on failures and then be told the budget was fine.
//
// Acquiring the token does not spend it: the ceiling is on the calls made
// *with* a token, and counting the acquisition would make a client that
// refreshes often look busier than it is.
func (c *MPClient) reserveCall(op string) error {
	c.mu.Lock()
	defer c.mu.Unlock()
	now := c.now().UTC()
	day := time.Date(now.Year(), now.Month(), now.Day(), 0, 0, 0, 0, time.UTC)
	if !day.Equal(c.dayStart) {
		c.dayStart = day
		c.callsToday = 0
	}
	if c.callsToday >= c.dailyLimit {
		return &QuotaError{Op: op, Limit: c.dailyLimit}
	}
	c.callsToday++
	return nil
}

// ---------------------------------------------------------------------------
// Public surface
// ---------------------------------------------------------------------------

// MPUserInfo is what the official account says about a person.
//
// The fields are the account's, not WeChat's: headimgurl does not travel
// under its own name, because the address it carries is a lease that expires
// within days rather than a place the bytes live.
type MPUserInfo struct {
	OpenID    string
	UnionID   string
	Nickname  string
	AvatarURL string
	Sex       int
	Language  string
	City      string
	Province  string
	Country   string
	Subscribe bool
}

// GetUserInfo reads the official account's view of a person, given that
// account's own OpenID for them.
//
// The OpenID here is the *official account's* OpenID, which is not the
// website app's OpenID for the same person — the two differ by application,
// and only the UnionID is common to both. Turning a UnionID into this
// account's OpenID is the step in front of this one.
func (c *MPClient) GetUserInfo(ctx context.Context, openID string) (MPUserInfo, error) {
	const op = "fetch official account user info"
	if strings.TrimSpace(openID) == "" {
		return MPUserInfo{}, &APIError{Op: op, Status: http.StatusBadRequest, ErrCode: -1, ErrMsg: "empty openid"}
	}
	q := url.Values{}
	q.Set("openid", openID)
	q.Set("lang", "zh_CN")

	var out struct {
		OpenID     string `json:"openid"`
		UnionID    string `json:"unionid"`
		Nickname   string `json:"nickname"`
		HeadImgURL string `json:"headimgurl"`
		Sex        int    `json:"sex"`
		Language   string `json:"language"`
		City       string `json:"city"`
		Province   string `json:"province"`
		Country    string `json:"country"`
		Subscribe  int    `json:"subscribe"`
	}
	if err := c.call(ctx, op, "/cgi-bin/user/info", q, &out); err != nil {
		return MPUserInfo{}, err
	}
	return MPUserInfo{
		OpenID:    out.OpenID,
		UnionID:   out.UnionID,
		Nickname:  out.Nickname,
		AvatarURL: out.HeadImgURL,
		Sex:       out.Sex,
		Language:  out.Language,
		City:      out.City,
		Province:  out.Province,
		Country:   out.Country,
		Subscribe: out.Subscribe != 0,
	}, nil
}

// call performs one API call with a valid access token.
//
// It spends the daily budget and retries once when WeChat says the token is
// no longer valid. The retry is not redundant with the refresh: an
// application may hold only one live access token, so a token acquired
// anywhere else — by an operator, by another deployment, by an earlier call
// that raced — retires the one in hand, and the only way to find out is to
// make the call and be told.
func (c *MPClient) call(ctx context.Context, op, path string, q url.Values, out any) error {
	for attempt := 0; attempt < 2; attempt++ {
		token, err := c.accessToken(ctx)
		if err != nil {
			return err
		}
		if err := c.reserveCall(op); err != nil {
			return err
		}
		q.Set("access_token", token)
		err = c.do(ctx, op, http.MethodGet, path, q, nil, out)
		if err == nil {
			return nil
		}
		var apiErr *APIError
		if attempt == 0 && errors.As(err, &apiErr) && apiErr.ErrCode == 40001 {
			c.invalidateToken()
			continue
		}
		return err
	}
	// Unreachable in practice: the loop returns on the first attempt unless
	// the token was rejected, and the retry either succeeds or returns its own
	// error. It is here so the function has a return on every path.
	return &APIError{Op: op, Status: http.StatusUnauthorized, ErrCode: 40001, ErrMsg: "the official account rejected the access token twice"}
}

// do performs one HTTP call and hands back the parsed body only if the body
// itself says the call worked.
//
// The status code is read for context and for the transient decision, but it
// is never the sole arbiter: WeChat answers many errors with 200, and one
// that did not would make every failure a parse error instead of the
// specific thing that went wrong.
func (c *MPClient) do(ctx context.Context, op, method, path string, q url.Values, body any, out any) error {
	endpoint := c.apiBase + path
	if len(q) > 0 {
		endpoint += "?" + q.Encode()
	}
	var reader io.Reader
	if body != nil {
		encoded, err := json.Marshal(body)
		if err != nil {
			return &MalformedError{Op: op, Cause: err}
		}
		reader = bytes.NewReader(encoded)
	}
	req, err := http.NewRequestWithContext(ctx, method, endpoint, reader)
	if err != nil {
		return &TransportError{Op: op, Cause: err}
	}
	req.Header.Set("Accept", "application/json")
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}

	resp, err := c.http.Do(req)
	if err != nil {
		// The URL carries the AppSecret in its query string, and net/http
		// puts the whole URL in the error it wraps. TransportError exposes
		// only a safe classification, so the secret never reaches a log.
		return &TransportError{Op: op, Cause: err}
	}
	defer resp.Body.Close()

	raw, err := io.ReadAll(io.LimitReader(resp.Body, maxBodyBytes))
	if err != nil {
		return &TransportError{Op: op, Cause: err}
	}
	if apiErr := checkWechatError(op, resp.StatusCode, raw); apiErr != nil {
		return apiErr
	}
	if out == nil {
		return nil
	}
	if err := json.Unmarshal(raw, out); err != nil {
		return &MalformedError{Op: op, Body: raw, Cause: err}
	}
	return nil
}

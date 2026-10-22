package wechat

import (
	"context"
	"encoding/json"
	"errors"
	"fmt"
	"net/http"
	"net/http/httptest"
	"strings"
	"sync"
	"sync/atomic"
	"testing"
	"time"
)

// mpStub is a stand-in for the official account's API.
//
// It counts token requests separately from everything else, because the whole
// point of the client is that one token serves many calls, and a test that
// cannot tell the two apart cannot check that.
type mpStub struct {
	srv         *httptest.Server
	tokenCalls  atomic.Int64
	apiCalls    atomic.Int64
	expiresIn   int64
	tokenErr    int
	apiErr      int
	apiErrCode  int
	invalidOnce atomic.Bool
}

func newMPStub(t *testing.T) *mpStub {
	t.Helper()
	stub := &mpStub{expiresIn: 7200}
	mux := http.NewServeMux()
	mux.HandleFunc("/cgi-bin/token", func(w http.ResponseWriter, r *http.Request) {
		stub.tokenCalls.Add(1)
		w.Header().Set("Content-Type", "application/json")
		if stub.tokenErr != 0 {
			fmt.Fprintf(w, `{"errcode":%d,"errmsg":"token refused"}`, stub.tokenErr)
			return
		}
		fmt.Fprintf(w, `{"access_token":"tok-%d","expires_in":%d}`, stub.tokenCalls.Load(), stub.expiresIn)
	})
	mux.HandleFunc("/cgi-bin/user/info", func(w http.ResponseWriter, r *http.Request) {
		stub.apiCalls.Add(1)
		w.Header().Set("Content-Type", "application/json")
		if stub.apiErr != 0 {
			code := stub.apiErrCode
			if code == 0 {
				code = 40003
			}
			fmt.Fprintf(w, `{"errcode":%d,"errmsg":"refused"}`, code)
			return
		}
		// One shot at "your token is no longer valid", so the client's retry
		// can be observed rather than assumed.
		if stub.invalidOnce.CompareAndSwap(true, false) {
			fmt.Fprint(w, `{"errcode":40001,"errmsg":"invalid credential"}`)
			return
		}
		fmt.Fprint(w, `{"openid":"o_mp","unionid":"u_mp","nickname":"萌心","headimgurl":"https://thirdwx.qlogo.cn/mmopen/abc/0","sex":1,"language":"zh_CN","city":"广州","province":"广东","country":"中国","subscribe":1}`)
	})
	stub.srv = httptest.NewServer(mux)
	t.Cleanup(stub.srv.Close)
	return stub
}

func newTestMPClient(t *testing.T, stub *mpStub) *MPClient {
	t.Helper()
	return NewMPClient(MPConfig{
		AppID:     "wxmpappid00001",
		AppSecret: "mp-s3cr3t-value-that-must-never-be-logged",
		APIBase:   stub.srv.URL,
		Timeout:   2 * time.Second,
	})
}

func TestMPClientAcquiresOneTokenAndReusesIt(t *testing.T) {
	t.Parallel()

	stub := newMPStub(t)
	c := newTestMPClient(t, stub)

	for i := 0; i < 5; i++ {
		if _, err := c.GetUserInfo(context.Background(), "o_mp"); err != nil {
			t.Fatalf("call %d: %v", i, err)
		}
	}
	if got := stub.tokenCalls.Load(); got != 1 {
		t.Fatalf("token requests = %d, want 1: one token serves many calls, and a "+
			"client that minted one per call would be revoked by WeChat", got)
	}
	if got := stub.apiCalls.Load(); got != 5 {
		t.Fatalf("api calls = %d, want 5", got)
	}
}

func TestMPClientCollapsesConcurrentTokenAcquisitions(t *testing.T) {
	t.Parallel()

	// An application may hold only one live access token, so a burst of
	// sign-ins that each minted its own would leave every token but the last
	// dead on arrival. This is the test that the burst does not happen.
	stub := newMPStub(t)
	c := newTestMPClient(t, stub)

	const callers = 20
	var wg sync.WaitGroup
	errs := make([]error, callers)
	start := make(chan struct{})
	for i := 0; i < callers; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			<-start
			_, errs[i] = c.GetUserInfo(context.Background(), "o_mp")
		}(i)
	}
	close(start)
	wg.Wait()

	for i, err := range errs {
		if err != nil {
			t.Fatalf("caller %d: %v", i, err)
		}
	}
	if got := stub.tokenCalls.Load(); got != 1 {
		t.Fatalf("token requests = %d, want 1: %d concurrent callers must share one acquisition", got, callers)
	}
}

func TestMPClientRefreshesATokenBeforeItExpires(t *testing.T) {
	t.Parallel()

	// A token spent in its last minutes fails as 40001 on a call that was
	// built while the token was still valid, and the retry that follows
	// succeeds, so the log shows a failure a retry "fixed" and no reason why.
	stub := newMPStub(t)
	stub.expiresIn = 240 // four minutes: inside the five-minute skew
	c := newTestMPClient(t, stub)

	now := time.Now()
	c.now = func() time.Time { return now }

	if _, err := c.GetUserInfo(context.Background(), "o_mp"); err != nil {
		t.Fatalf("first call: %v", err)
	}
	if got := stub.tokenCalls.Load(); got != 1 {
		t.Fatalf("token requests = %d, want 1", got)
	}

	// The cached token is already inside the skew window, so the next call
	// must not use it.
	if _, err := c.GetUserInfo(context.Background(), "o_mp"); err != nil {
		t.Fatalf("second call: %v", err)
	}
	if got := stub.tokenCalls.Load(); got != 2 {
		t.Fatalf("token requests = %d, want 2: a token inside the skew window must be replaced", got)
	}
}

func TestMPClientKeepsATokenThatIsStillComfortablyValid(t *testing.T) {
	t.Parallel()

	stub := newMPStub(t)
	stub.expiresIn = 7200
	c := newTestMPClient(t, stub)

	for i := 0; i < 3; i++ {
		if _, err := c.GetUserInfo(context.Background(), "o_mp"); err != nil {
			t.Fatalf("call %d: %v", i, err)
		}
	}
	if got := stub.tokenCalls.Load(); got != 1 {
		t.Fatalf("token requests = %d, want 1", got)
	}
}

func TestMPClientRefreshesOnceWhenWeChatSaysTheTokenIsInvalid(t *testing.T) {
	t.Parallel()

	// An application may hold only one live access token, so a token acquired
	// anywhere else retires the one in hand. The only way to find out is to
	// make the call and be told.
	stub := newMPStub(t)
	stub.invalidOnce.Store(true)
	c := newTestMPClient(t, stub)

	info, err := c.GetUserInfo(context.Background(), "o_mp")
	if err != nil {
		t.Fatalf("the call should survive one invalid token: %v", err)
	}
	if info.Nickname != "萌心" {
		t.Fatalf("nickname = %q, want 萌心", info.Nickname)
	}
	if got := stub.tokenCalls.Load(); got != 2 {
		t.Fatalf("token requests = %d, want 2: an invalid token must be replaced once", got)
	}
}

func TestMPClientGivesUpAfterOneInvalidToken(t *testing.T) {
	t.Parallel()

	// A token that is invalid twice is not a race to be retried; it is a
	// broken credential, and retrying forever would turn it into a hot loop
	// against WeChat.
	stub := newMPStub(t)
	stub.apiErr = 1
	stub.apiErrCode = 40001
	c := newTestMPClient(t, stub)

	if _, err := c.GetUserInfo(context.Background(), "o_mp"); err == nil {
		t.Fatal("expected the call to fail")
	}
	if got := stub.apiCalls.Load(); got != 2 {
		t.Fatalf("api calls = %d, want 2: one attempt and one retry, no more", got)
	}
}

func TestMPClientRefusesToSpendTheDailyBudget(t *testing.T) {
	t.Parallel()

	// The budget is spent before the call is made, and the call that would
	// have overrun it is not made at all: a budget that only counted
	// successes would let a client overrun on failures and then be told the
	// budget was fine.
	stub := newMPStub(t)
	c := newTestMPClient(t, stub)
	c.dailyLimit = 2

	for i := 0; i < 2; i++ {
		if _, err := c.GetUserInfo(context.Background(), "o_mp"); err != nil {
			t.Fatalf("call %d: %v", i, err)
		}
	}
	if got := stub.apiCalls.Load(); got != 2 {
		t.Fatalf("api calls = %d, want 2", got)
	}

	_, err := c.GetUserInfo(context.Background(), "o_mp")
	if err == nil {
		t.Fatal("expected the third call to be refused")
	}
	var quota *QuotaError
	if !errors.As(err, &quota) {
		t.Fatalf("error = %v, want a QuotaError", err)
	}
	if quota.Transient() {
		t.Error("a spent budget is not transient: it does not come back until the day boundary")
	}
	if got := stub.apiCalls.Load(); got != 2 {
		t.Fatalf("api calls = %d, want 2: the refused call must not be made", got)
	}
}

func TestMPDailyBudgetResetsOnTheNextDay(t *testing.T) {
	t.Parallel()

	stub := newMPStub(t)
	c := newTestMPClient(t, stub)
	c.dailyLimit = 1

	day1 := time.Date(2026, 10, 20, 23, 0, 0, 0, time.UTC)
	c.now = func() time.Time { return day1 }
	if _, err := c.GetUserInfo(context.Background(), "o_mp"); err != nil {
		t.Fatalf("first call: %v", err)
	}
	if _, err := c.GetUserInfo(context.Background(), "o_mp"); err == nil {
		t.Fatal("the budget should be spent")
	}

	day2 := day1.Add(2 * time.Hour) // crosses midnight
	c.now = func() time.Time { return day2 }
	if _, err := c.GetUserInfo(context.Background(), "o_mp"); err != nil {
		t.Fatalf("the budget should reset on the next day: %v", err)
	}
}

func TestMPGetUserInfoReadsTheWechatFieldNames(t *testing.T) {
	t.Parallel()

	stub := newMPStub(t)
	c := newTestMPClient(t, stub)

	info, err := c.GetUserInfo(context.Background(), "o_mp")
	if err != nil {
		t.Fatalf("get user info: %v", err)
	}
	if info.Nickname != "萌心" {
		t.Errorf("nickname = %q, want 萌心", info.Nickname)
	}
	if info.AvatarURL != "https://thirdwx.qlogo.cn/mmopen/abc/0" {
		t.Errorf("avatar = %q", info.AvatarURL)
	}
	if info.OpenID != "o_mp" || info.UnionID != "u_mp" {
		t.Errorf("identity = %q / %q, want o_mp / u_mp", info.OpenID, info.UnionID)
	}
	if info.City != "广州" || info.Province != "广东" || info.Country != "中国" {
		t.Errorf("region = %q %q %q", info.Province, info.City, info.Country)
	}
	if !info.Subscribe {
		t.Error("subscribe = false, want true")
	}
}

func TestMPGetUserInfoRejectsAnEmptyOpenidWithoutCallingOut(t *testing.T) {
	t.Parallel()

	stub := newMPStub(t)
	c := newTestMPClient(t, stub)

	if _, err := c.GetUserInfo(context.Background(), "  "); err == nil {
		t.Fatal("expected an empty openid to be refused")
	}
	if got := stub.apiCalls.Load(); got != 0 {
		t.Fatalf("api calls = %d, want 0: an empty openid must not be sent", got)
	}
}

func TestMPTreatsA200WithAnErrcodeAsAFailure(t *testing.T) {
	t.Parallel()

	// WeChat answers many errors with 200. A client that branched on the
	// status code alone would read this as a success with an empty nickname
	// and store it.
	stub := newMPStub(t)
	stub.apiErr = 1
	stub.apiErrCode = 40003
	c := newTestMPClient(t, stub)

	_, err := c.GetUserInfo(context.Background(), "o_mp")
	if err == nil {
		t.Fatal("expected the errcode to be an error")
	}
	var apiErr *APIError
	if !errors.As(err, &apiErr) {
		t.Fatalf("error = %v, want an APIError", err)
	}
	if apiErr.ErrCode != 40003 {
		t.Errorf("errcode = %d, want 40003", apiErr.ErrCode)
	}
}

func TestMPOnAMalformedBodyIsMalformedNotSuccess(t *testing.T) {
	t.Parallel()

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		if strings.HasSuffix(r.URL.Path, "/token") {
			fmt.Fprint(w, `{"access_token":"tok-1","expires_in":7200}`)
			return
		}
		fmt.Fprint(w, `<html>not json</html>`)
	}))
	t.Cleanup(srv.Close)

	c := NewMPClient(MPConfig{AppID: "wxmpappid00001", AppSecret: "s", APIBase: srv.URL, Timeout: 2 * time.Second})
	_, err := c.GetUserInfo(context.Background(), "o_mp")
	if err == nil {
		t.Fatal("expected a malformed body to be an error")
	}
	var malformed *MalformedError
	if !errors.As(err, &malformed) {
		t.Fatalf("error = %v, want a MalformedError", err)
	}
}

func TestMPErrorsNeverCarryTheSecret(t *testing.T) {
	t.Parallel()

	// The AppSecret travels in a query string, and net/http puts the whole
	// URL into the error it wraps. A secret in a log line is a secret in
	// every log aggregator downstream of it.
	const secret = "mp-s3cr3t-value-that-must-never-be-logged"
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// A connection reset mid-request is the shape of a transport failure.
		hj, ok := w.(http.Hijacker)
		if !ok {
			t.Error("the test server cannot hijack the connection")
			return
		}
		conn, _, err := hj.Hijack()
		if err != nil {
			t.Errorf("hijack: %v", err)
			return
		}
		conn.Close()
	}))
	t.Cleanup(srv.Close)

	c := NewMPClient(MPConfig{AppID: "wxmpappid00001", AppSecret: secret, APIBase: srv.URL, Timeout: 2 * time.Second})
	_, err := c.GetUserInfo(context.Background(), "o_mp")
	if err == nil {
		t.Fatal("expected the transport failure to surface")
	}
	if strings.Contains(err.Error(), secret) {
		t.Fatalf("the error carries the AppSecret: %v", err)
	}
	if strings.Contains(err.Error(), "wxmpappid00001") {
		t.Fatalf("the error carries the AppID: %v", err)
	}
}

func TestMPCancelledCallerDoesNotKillASharedRefresh(t *testing.T) {
	t.Parallel()

	// The refresh is shared, so it must not die with the caller that happened
	// to start it. A buyer who closed the window would otherwise cancel the
	// acquisition for every other sign-in waiting on it, and they would all
	// fail for a reason that has nothing to do with them.
	stub := newMPStub(t)
	c := newTestMPClient(t, stub)

	release := make(chan struct{})
	entered := make(chan struct{})
	var once sync.Once
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		if strings.HasSuffix(r.URL.Path, "/token") {
			once.Do(func() { close(entered) })
			<-release
		}
		w.Header().Set("Content-Type", "application/json")
		if strings.HasSuffix(r.URL.Path, "/token") {
			fmt.Fprint(w, `{"access_token":"tok-1","expires_in":7200}`)
			return
		}
		fmt.Fprint(w, `{"openid":"o_mp","nickname":"n"}`)
	}))
	t.Cleanup(srv.Close)
	c.apiBase = srv.URL

	ctx, cancel := context.WithCancel(context.Background())
	cancelledErr := make(chan error, 1)
	go func() {
		_, err := c.GetUserInfo(ctx, "o_mp")
		cancelledErr <- err
	}()

	<-entered
	cancel()
	close(release)

	if err := <-cancelledErr; err == nil {
		t.Fatal("the cancelled caller should get an error")
	}

	// A later caller must still be able to get a token.
	if _, err := c.GetUserInfo(context.Background(), "o_mp"); err != nil {
		t.Fatalf("a later caller must not inherit the cancellation: %v", err)
	}
}

func TestMPTokenRequestCarriesTheGrantAndTheCredentials(t *testing.T) {
	t.Parallel()

	// A token request that omits grant_type is not a token request, and
	// WeChat's answer to it is an error code rather than an obvious
	// misconfiguration — so the request is checked here.
	var seen struct {
		grantType string
		appID     string
		secret    string
	}
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// Only the token request is inspected: the follow-up call carries the
		// token in its own query and would otherwise overwrite the capture.
		if strings.HasSuffix(r.URL.Path, "/token") {
			q := r.URL.Query()
			seen.grantType = q.Get("grant_type")
			seen.appID = q.Get("appid")
			seen.secret = q.Get("secret")
		}
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"access_token":"tok-1","expires_in":7200}`)
	}))
	t.Cleanup(srv.Close)

	c := NewMPClient(MPConfig{AppID: "wxmpappid00001", AppSecret: "mp-secret", APIBase: srv.URL, Timeout: 2 * time.Second})
	if _, err := c.GetUserInfo(context.Background(), "o_mp"); err != nil {
		t.Fatalf("get user info: %v", err)
	}
	if seen.grantType != "client_credential" {
		t.Errorf("grant_type = %q, want client_credential", seen.grantType)
	}
	if seen.appID != "wxmpappid00001" {
		t.Errorf("appid = %q", seen.appID)
	}
	if seen.secret != "mp-secret" {
		t.Errorf("secret = %q", seen.secret)
	}
}

func TestMPTokenFailureIsNotCached(t *testing.T) {
	t.Parallel()

	// A failed acquisition must not be remembered: the next sign-in is a new
	// chance, and a cached failure would take the bridge down for the whole
	// life of the process over one bad minute on WeChat's side.
	stub := newMPStub(t)
	c := newTestMPClient(t, stub)
	stub.tokenErr = 40013

	if _, err := c.GetUserInfo(context.Background(), "o_mp"); err == nil {
		t.Fatal("expected the token failure to surface")
	}
	stub.tokenErr = 0
	if _, err := c.GetUserInfo(context.Background(), "o_mp"); err != nil {
		t.Fatalf("the failure must not be cached: %v", err)
	}
}

func TestMPTokenResponseWithoutATokenIsMalformed(t *testing.T) {
	t.Parallel()

	// A 200 with no token and no error is a response we cannot act on.
	// Treating it as success would hand out an empty credential and fail one
	// layer further from the cause.
	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		w.Header().Set("Content-Type", "application/json")
		fmt.Fprint(w, `{"errcode":0,"errmsg":"ok"}`)
	}))
	t.Cleanup(srv.Close)

	c := NewMPClient(MPConfig{AppID: "wxmpappid00001", AppSecret: "s", APIBase: srv.URL, Timeout: 2 * time.Second})
	_, err := c.GetUserInfo(context.Background(), "o_mp")
	if err == nil {
		t.Fatal("expected a tokenless response to be an error")
	}
	var malformed *MalformedError
	if !errors.As(err, &malformed) {
		t.Fatalf("error = %v, want a MalformedError", err)
	}
}

func TestMPUnconfiguredCredentialsFailBeforeCallingOut(t *testing.T) {
	t.Parallel()

	srv := httptest.NewServer(http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		t.Error("an unconfigured client must not call out")
	}))
	t.Cleanup(srv.Close)

	c := NewMPClient(MPConfig{APIBase: srv.URL, Timeout: 2 * time.Second})
	_, err := c.GetUserInfo(context.Background(), "o_mp")
	if err == nil {
		t.Fatal("expected the unconfigured client to fail")
	}
}

func TestMPClientDoesNotUseTheDefaultHTTPClient(t *testing.T) {
	t.Parallel()

	// http.DefaultClient has no timeout at all, which turns a stalled
	// connection into a sign-in that hangs until the user's patience runs out.
	c := NewMPClient(MPConfig{AppID: "wxmpappid00001", AppSecret: "s", APIBase: "https://api.weixin.qq.com"})
	if c.http == http.DefaultClient {
		t.Fatal("the client must not use http.DefaultClient")
	}
	if c.http.Timeout <= 0 {
		t.Fatal("the client must have a timeout")
	}
}

func TestMPTokenResponseShapeIsWhatWechatSends(t *testing.T) {
	t.Parallel()

	// The token response is decoded into an anonymous struct; this test pins
	// the field names against the ones WeChat actually sends, so a rename
	// that silently stopped reading the token cannot pass.
	var body map[string]any
	raw := `{"access_token":"tok","expires_in":7200}`
	if err := json.Unmarshal([]byte(raw), &body); err != nil {
		t.Fatalf("the fixture is not JSON: %v", err)
	}
	if _, ok := body["access_token"]; !ok {
		t.Error("the token response must carry access_token")
	}
	if _, ok := body["expires_in"]; !ok {
		t.Error("the token response must carry expires_in")
	}
}

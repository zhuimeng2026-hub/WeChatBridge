package auth

import (
	"bytes"
	"encoding/base64"
	"encoding/json"
	"errors"
	"io"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"os"
	"path/filepath"
	"regexp"
	"strings"
	"testing"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
	"github.com/freestylefly/WeChatBridge/server/internal/token"
	"github.com/freestylefly/WeChatBridge/server/internal/wechat"
)

func init() { gin.SetMode(gin.TestMode) }

// isoPattern is the client's UtcIso8601Converter format, exactly. A bare
// time.Time would marshal as RFC 3339 with an offset — "+08:00" or "+00:00" —
// which on the other end is a JsonException rather than a wrong-looking string
// a human would catch.
var isoPattern = regexp.MustCompile(`^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}Z$`)

func newTestServer(t *testing.T) (*harness, *gin.Engine) {
	t.Helper()
	h := newHarness(t)
	engine := gin.New()
	h.svc.RegisterRoutes(engine)
	return h, engine
}

func doJSON(t *testing.T, engine *gin.Engine, method, path string, body any, headers map[string]string) (*httptest.ResponseRecorder, map[string]any) {
	t.Helper()

	var reader io.Reader
	if body != nil {
		raw, err := json.Marshal(body)
		if err != nil {
			t.Fatalf("marshal request: %v", err)
		}
		reader = bytes.NewReader(raw)
	}
	req := httptest.NewRequest(method, path, reader)
	if body != nil {
		req.Header.Set("Content-Type", "application/json")
	}
	for k, v := range headers {
		req.Header.Set(k, v)
	}

	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)

	out := map[string]any{}
	if rec.Body.Len() > 0 && strings.HasPrefix(rec.Header().Get("Content-Type"), "application/json") {
		if err := json.Unmarshal(rec.Body.Bytes(), &out); err != nil {
			t.Fatalf("%s %s: response is not JSON: %s", method, path, rec.Body.String())
		}
	}
	return rec, out
}

func createTicket(t *testing.T, engine *gin.Engine, installID string) map[string]any {
	t.Helper()
	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/session",
		map[string]any{"installId": installID, "deviceId": "device-1"}, nil)
	if rec.Code != http.StatusCreated {
		t.Fatalf("POST /v1/auth/session = %d: %s", rec.Code, rec.Body.String())
	}
	return body
}

// ---------------------------------------------------------------------------
// Shapes
// ---------------------------------------------------------------------------

// Every response this service writes is camelCase, because the client's
// JsonOptions are. Go's default is the Go field name, so a missing tag is not
// a cosmetic problem — it is a field the client never binds.
func TestEveryResponseIsCamelCase(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)

	created := createTicket(t, engine, "install-1")
	for _, key := range []string{"ticket", "authorizeUrl", "qrImageUrl", "expiresAt"} {
		if _, ok := created[key]; !ok {
			t.Errorf("create session response is missing %q; got keys %v", key, keysOf(created))
		}
	}

	if _, err := h.svc.HandleCallback(t.Context(), "auth-code", created["ticket"].(string)); err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}

	_, poll := doJSON(t, engine, http.MethodGet, "/v1/auth/session/"+created["ticket"].(string), nil, nil)
	for _, key := range []string{"status", "accessToken", "refreshToken", "expiresAt", "account"} {
		if _, ok := poll[key]; !ok {
			t.Errorf("poll response is missing %q; got keys %v", key, keysOf(poll))
		}
	}
	account, _ := poll["account"].(map[string]any)
	for _, key := range []string{"id", "nickname", "avatarUrl", "plan", "createdAt", "lastLoginAt", "wechatBound"} {
		if _, ok := account[key]; !ok {
			t.Errorf("account is missing %q; got keys %v", key, keysOf(account))
		}
	}

	access := poll["accessToken"].(string)
	refresh := poll["refreshToken"].(string)

	_, refreshed := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	for _, key := range []string{"status", "accessToken", "refreshToken", "expiresAt", "account"} {
		if _, ok := refreshed[key]; !ok {
			t.Errorf("refresh response is missing %q; got keys %v", key, keysOf(refreshed))
		}
	}

	bearer := map[string]string{"Authorization": "Bearer " + access}
	_, acct := doJSON(t, engine, http.MethodGet, "/v1/account", nil, bearer)
	for _, key := range []string{"id", "nickname", "avatarUrl", "plan", "createdAt", "lastLoginAt", "wechatBound"} {
		if _, ok := acct[key]; !ok {
			t.Errorf("GET /v1/account is missing %q; got keys %v", key, keysOf(acct))
		}
	}

	// And nothing anywhere is PascalCase or snake_case. The error envelope is
	// excluded: internal/httpx owns that shape and request_id is deliberately
	// snake_case there, per a decision made in that package.
	assertAllLowerCamel(t, "", created)
	assertAllLowerCamel(t, "", poll)
	assertAllLowerCamel(t, "", refreshed)
}

func TestEveryTimestampIsTheClientsFormat(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	created := createTicket(t, engine, "install-1")
	if got := created["expiresAt"].(string); !isoPattern.MatchString(got) {
		t.Errorf("create expiresAt = %q, want yyyy-MM-ddTHH:mm:ssZ", got)
	}
	// The clock is UTC in the harness; if the formatter ever forgot to
	// convert, this is where it would show as a +00:00 suffix.
	if strings.Contains(created["expiresAt"].(string), "+") {
		t.Errorf("expiresAt carries an offset: %q", created["expiresAt"])
	}

	if _, err := h.svc.HandleCallback(t.Context(), "auth-code", created["ticket"].(string)); err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}
	_, poll := doJSON(t, engine, http.MethodGet, "/v1/auth/session/"+created["ticket"].(string), nil, nil)
	if got := poll["expiresAt"].(string); !isoPattern.MatchString(got) {
		t.Errorf("poll expiresAt = %q", got)
	}
	for _, key := range []string{"createdAt", "lastLoginAt"} {
		account := poll["account"].(map[string]any)
		if got := account[key].(string); !isoPattern.MatchString(got) {
			t.Errorf("account.%s = %q", key, got)
		}
	}
}

// ---------------------------------------------------------------------------
// Ticket endpoints
// ---------------------------------------------------------------------------

func TestPollReturnsExpiredForATicketThatIsGone(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	rec, body := doJSON(t, engine, http.MethodGet, "/v1/auth/session/nosuchticket", nil, nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200: a poll must answer with a state, not a 404", rec.Code)
	}
	if body["status"] != StatusExpired {
		t.Errorf("status = %v, want expired", body["status"])
	}
}

func TestTheQREndpoint404sForATicketThatDoesNotExist(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	req := httptest.NewRequest(http.MethodGet, "/v1/auth/session/nosuchticket/qr", nil)
	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)
	if rec.Code != http.StatusNotFound {
		t.Errorf("status = %d, want 404", rec.Code)
	}
}

func TestTheOutstandingTicketLimitIsNotSomethingARefreshCanHit(t *testing.T) {
	t.Parallel()

	// Every page load mints a ticket, so this used to be the shape of a seller
	// who refreshed four times inside one ticket lifetime: 429, 「待处理的二维码
	// 过多」, and no way forward until the TTL ran out. The oldest ticket is
	// retired instead, so the newest request always wins.
	_, engine := newTestServer(t)
	for n := 0; n < 8; n++ {
		rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/session",
			map[string]any{"installId": "install-1"}, nil)
		if rec.Code != http.StatusCreated {
			t.Fatalf("refresh %d: status = %d, want 201: %v", n, rec.Code, body)
		}
		if body["ticket"] == "" {
			t.Fatalf("refresh %d: no ticket in the response", n)
		}
	}
}

func TestTooManyOutstandingTicketsAnswers429(t *testing.T) {
	t.Parallel()

	// The 429 still exists, and it is now narrower: it is only reachable when
	// every live ticket is an authorised sign-in. The 429's own shape — the
	// error code and the Retry-After the client backs off with — is unchanged.
	h, engine := newTestServer(t)
	var tickets []string
	for n := 0; n < 3; n++ {
		tickets = append(tickets, createTicket(t, engine, "install-1")["ticket"].(string))
	}
	for _, ticket := range tickets {
		h.store.forceAuthSessionState(ticket, store.AuthAuthorized)
	}

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/session",
		map[string]any{"installId": "install-1"}, nil)
	if rec.Code != http.StatusTooManyRequests {
		t.Fatalf("status = %d, want 429", rec.Code)
	}
	errObj, _ := body["error"].(map[string]any)
	if errObj["code"] != "rate_limited" {
		t.Errorf("error code = %v, want rate_limited", errObj["code"])
	}
	if rec.Header().Get("Retry-After") == "" {
		t.Error("a 429 without Retry-After gives the client nothing to back off with")
	}
}

// ---------------------------------------------------------------------------
// The redirect
// ---------------------------------------------------------------------------

// §15.1 step 7. The callback is a browser-facing GET and answers 200 with no
// redirect. A redirect here is the reflexive browser-shaped answer and it
// breaks the flow, because the destination is a page in a browser the desktop
// process has never heard of. The poller is the channel.
//
// The body used to be empty, and that was the bug this test now pins the other
// way: a 200 with nothing in it renders as a blank white page in the browser
// the buyer is holding. The status code and the absence of a redirect are
// unchanged; only the body grew.
func TestTheCallbackAnswers200WithNoRedirectAndAPageThatSaysSomething(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	created := createTicket(t, engine, "install-1")
	ticket := created["ticket"].(string)

	req := httptest.NewRequest(http.MethodGet, CallbackPath+"?code=auth-code&state="+ticket, nil)
	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)

	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	if loc := rec.Header().Get("Location"); loc != "" {
		t.Fatalf("the callback issued a redirect to %q; the desktop client cannot follow that and the poller is the channel", loc)
	}
	if ct := rec.Header().Get("Content-Type"); !strings.HasPrefix(ct, "text/html") {
		t.Errorf("Content-Type = %q, want text/html — a JSON envelope here renders as raw text in a browser", ct)
	}
	body := rec.Body.Bytes()
	if len(bytes.TrimSpace(body)) == 0 {
		t.Fatal("the callback wrote an empty body; the buyer gets a blank white page")
	}
	// The machine code is what ties the transfer to the licence, so it has to
	// be on this page or nobody can reconcile the two by hand.
	if !bytes.Contains(body, []byte("install-1")) {
		t.Errorf("the page does not carry the machine code the buyer must put in the transfer note: %s", body)
	}
	if rec.Header().Get("Cache-Control") != "no-store" {
		t.Errorf("Cache-Control = %q, want no-store; the page holds a machine code and a payment instruction",
			rec.Header().Get("Cache-Control"))
	}

	// And the scan really did complete — the page is not covering for a failure.
	poll, err := h.svc.Poll(t.Context(), ticket)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if poll.Status != StatusAuthorized {
		t.Fatalf("the callback reported success but the ticket is %q", poll.Status)
	}
}

// The page is served to a browser straight off WeChat, so it must be HTML on
// every path — a JSON envelope from the error branches renders as raw text.
func TestTheCallbackAnswersEveryFailureWithAPageNotAnEnvelope(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name  string
		setup func(*harness)
		state string
		want  int
	}{
		{
			name: "forged state",
			// Whether the ticket was forged, spent or expired must stay
			// indistinguishable: this reply is also an oracle if it is not.
			state: "forged",
			want:  http.StatusBadRequest,
		},
		{
			name: "wechat rejected the code",
			setup: func(h *harness) {
				h.wc.exchangeHandler = func(rw http.ResponseWriter, r *http.Request) {
					_, _ = rw.Write([]byte(`{"errcode":40029,"errmsg":"invalid code"}`))
				}
			},
			want: http.StatusBadRequest,
		},
		{
			name: "wechat is down",
			setup: func(h *harness) {
				h.wc.exchangeHandler = func(rw http.ResponseWriter, r *http.Request) {
					rw.WriteHeader(http.StatusInternalServerError)
					_, _ = rw.Write([]byte(`{"errcode":-1,"errmsg":"system error"}`))
				}
			},
			want: http.StatusServiceUnavailable,
		},
	}

	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			t.Parallel()

			h, engine := newTestServer(t)
			if tc.setup != nil {
				tc.setup(h)
			}
			state := tc.state
			if state == "" {
				state = createTicket(t, engine, "install-1")["ticket"].(string)
			}

			req := httptest.NewRequest(http.MethodGet, CallbackPath+"?code=auth-code&state="+state, nil)
			rec := httptest.NewRecorder()
			engine.ServeHTTP(rec, req)

			if rec.Code != tc.want {
				t.Errorf("status = %d, want %d", rec.Code, tc.want)
			}
			if ct := rec.Header().Get("Content-Type"); !strings.HasPrefix(ct, "text/html") {
				t.Errorf("Content-Type = %q, want text/html", ct)
			}
			body := rec.Body.String()
			if strings.TrimSpace(body) == "" {
				t.Fatal("the failure branch wrote an empty body")
			}
			// Not a leaked envelope either: the point of the page is that it
			// reads like an explanation, not like an error envelope with a
			// stack trace in it.
			if strings.Contains(body, `"error"`) {
				t.Errorf("the failure page is still the JSON envelope: %s", body)
			}
			for _, leak := range []string{"unknown ticket", "already used", "forged", "not found"} {
				if strings.Contains(strings.ToLower(body), leak) {
					t.Errorf("the page leaks why the state failed (%q): %s", leak, body)
				}
			}
		})
	}
}

// A retryable failure must not tell the buyer to scan again. Their code is
// still good — saying otherwise sends them to redo work that changes nothing.
func TestTheOutagePageDoesNotTellTheBuyerToScanAgain(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	h.wc.exchangeHandler = func(rw http.ResponseWriter, r *http.Request) {
		rw.WriteHeader(http.StatusInternalServerError)
		_, _ = rw.Write([]byte(`{"errcode":-1,"errmsg":"system error"}`))
	}
	ticket := createTicket(t, engine, "install-1")["ticket"].(string)

	req := httptest.NewRequest(http.MethodGet, CallbackPath+"?code=auth-code&state="+ticket, nil)
	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)

	body := rec.Body.String()
	if strings.Contains(body, "重新扫码") || strings.Contains(body, "refresh") {
		t.Errorf("the outage page tells the buyer to re-scan, but the ticket and their code are both still good: %s", body)
	}
	if session, _ := h.store.authSessionByTicket(ticket); session.State != "pending" {
		t.Error("a WeChat outage consumed the ticket")
	}
}

// install_id is whatever the client put in the request body, so it is
// attacker-chosen at the moment it is stored. An attacker who gets a victim to
// scan their ticket would otherwise be running script on the same origin the
// buyer types into.
func TestTheCallbackPageEscapesTheMachineCode(t *testing.T) {
	t.Parallel()

	payload := `<script>alert(1)</script>`
	_, engine := newTestServer(t)
	ticket := createTicket(t, engine, payload)["ticket"].(string)

	req := httptest.NewRequest(http.MethodGet, CallbackPath+"?code=auth-code&state="+ticket, nil)
	rec := httptest.NewRecorder()
	engine.ServeHTTP(rec, req)

	body := rec.Body.String()
	if strings.Contains(body, "<script>alert(1)</script>") {
		t.Fatalf("the machine code was interpolated into the page unescaped: %s", body)
	}
	if !strings.Contains(body, "&lt;script&gt;") {
		t.Errorf("the machine code is not on the page at all, escaped or otherwise: %s", body)
	}
}

// The one assertion that matters most on this page, and the one a tag-presence
// check misses: the <img src> has to carry the image.
//
// html/template rewrites a data: URI it considers unsafe to "#ZgotmplZ". The
// page still compiles, still contains an <img, still passes a tag check — and
// shows the buyer a broken frame where the QR code should be.
func TestTheCallbackPageCarriesTheImageItself(t *testing.T) {
	t.Parallel()

	dir := t.TempDir()
	path := filepath.Join(dir, "code.png")
	png := []byte("\x89PNG\r\n\x1a\n" + strings.Repeat("x", 64))
	if err := os.WriteFile(path, png, 0o600); err != nil {
		t.Fatal(err)
	}

	code := LoadPaymentCode(path, "aiwomeishi", "")
	page, err := code.Render(pageAuthorized, "install-1")
	if err != nil {
		t.Fatalf("Render: %v", err)
	}

	body := string(page)
	if strings.Contains(body, "#ZgotmplZ") {
		t.Fatal("the image src was filtered out by html/template; the page would show a broken frame")
	}
	if !strings.Contains(body, `src="data:image/png;base64,`) {
		t.Errorf("the image is not inlined as a data URI: %s", body)
	}
	if !strings.Contains(body, base64.StdEncoding.EncodeToString(png)) {
		t.Error("the inlined image is not the bytes that were read from disk")
	}
	// The seller's contact details travel with it.
	if !strings.Contains(body, "aiwomeishi") {
		t.Errorf("the page lost the seller's contact details: %s", body)
	}
}

// A deployment with no payment code must say so. An empty frame where the QR
// should be reads as a broken page; a sentence reads as a temporary condition.
func TestTheCallbackPageSaysWhenNoPaymentCodeIsConfigured(t *testing.T) {
	t.Parallel()

	code := LoadPaymentCode("", "aiwomeishi", "")
	if code.Configured() {
		t.Fatal("an empty path reported itself configured")
	}
	page, err := code.Render(pageAuthorized, "install-1")
	if err != nil {
		t.Fatalf("Render: %v", err)
	}
	if !bytes.Contains(page, []byte("尚未配置")) {
		t.Errorf("an unconfigured payment code rendered without saying so: %s", page)
	}
	if bytes.Contains(page, []byte("<img")) {
		t.Errorf("an unconfigured payment code rendered an image element: %s", page)
	}
}

// A WeChat id the seller typed can be mistyped by the buyer, so the QR is the
// better primary path — but it cannot be generated from the id, and a
// deployment without one still has to reach the buyer through the text.
func TestTheContactBlockFallsBackToTheTextWhenNoQrisConfigured(t *testing.T) {
	t.Parallel()

	dir := t.TempDir()
	payment := filepath.Join(dir, "pay.png")
	if err := os.WriteFile(payment, []byte("\x89PNG\r\n\x1a\n"+strings.Repeat("x", 32)), 0o600); err != nil {
		t.Fatal(err)
	}

	code := LoadPaymentCode(payment, "aiwomeishi", "")
	page, err := code.Render(pageAuthorized, "install-1")
	if err != nil {
		t.Fatalf("Render: %v", err)
	}

	body := string(page)
	if !strings.Contains(body, "aiwomeishi") {
		t.Errorf("with no contact QR the buyer still has to be able to add the seller: %s", body)
	}
	if strings.Count(body, "<img") != 1 {
		t.Errorf("expected only the payment image; the contact block added another or dropped one: %s", body)
	}
}

// A path that is set but is not an image is a typo. It must be loud and it must
// be survivable: the operator sees it in the startup log and on the page, and
// the service still logs people in. Failing startup instead would let a missing
// receipt image take down the whole auth flow — which is the one thing this
// page's failure notice exists to prevent.
func TestAnUnusableImageIsReportedButNotFatal(t *testing.T) {
	t.Parallel()

	path := filepath.Join(t.TempDir(), "code.jpg")
	if err := os.WriteFile(path, []byte("<html>not an image</html>"), 0o600); err != nil {
		t.Fatal(err)
	}

	code := LoadPaymentCode(path, "aiwomeishi", "")
	if code.Configured() {
		t.Error("a file that is not an image was accepted as a payment code")
	}
	problems := code.Problems()
	if len(problems) != 1 || !strings.Contains(problems[0], "not an image") {
		t.Errorf("Problems() = %v, want one that names the file and says it is not an image", problems)
	}

	// And it still renders: the page a buyer gets has to say what is wrong.
	page, err := code.Render(pageAuthorized, "install-1")
	if err != nil {
		t.Fatalf("Render: %v", err)
	}
	if !strings.Contains(string(page), "尚未配置") {
		t.Errorf("an unusable image produced neither a picture nor an explanation: %s", page)
	}
}

// The two kinds of "no image" are different, and only one of them is a fault.
// An empty path means nobody configured it, which is a normal state; a path
// that was set and is not there means the operator believes a file is in a
// place it is not, and that is a typo worth a line in the log. Both leave the
// service running.
func TestAnUnsetImageIsSilentButAMissingOneIsAProblem(t *testing.T) {
	t.Parallel()

	unset := LoadPaymentCode("", "aiwomeishi", "")
	if problems := unset.Problems(); len(problems) != 0 {
		t.Errorf("an unset path reported %v; nobody configuring an optional image is not a fault", problems)
	}
	if unset.Configured() {
		t.Error("an unset path reported itself configured")
	}

	missing := LoadPaymentCode(filepath.Join(t.TempDir(), "nope.png"), "aiwomeishi", "")
	problems := missing.Problems()
	if len(problems) != 1 || !strings.Contains(problems[0], "nope.png") {
		t.Errorf("Problems() = %v, want one naming the path that was configured but is not there", problems)
	}
	if missing.Configured() {
		t.Error("a missing file reported itself configured")
	}
}

// ---------------------------------------------------------------------------
// Refresh
// ---------------------------------------------------------------------------

func signInViaHTTP(t *testing.T, engine *gin.Engine) (access, refresh string) {
	t.Helper()
	created := createTicket(t, engine, "install-1")
	ticket := created["ticket"].(string)

	req := httptest.NewRequest(http.MethodGet, CallbackPath+"?code=auth-code&state="+ticket, nil)
	engine.ServeHTTP(httptest.NewRecorder(), req)

	_, poll := doJSON(t, engine, http.MethodGet, "/v1/auth/session/"+ticket, nil, nil)
	return poll["accessToken"].(string), poll["refreshToken"].(string)
}

func TestRefreshAcceptsTheTokenInABody(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	_, refresh := signInViaHTTP(t, engine)

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d: %s", rec.Code, rec.Body.String())
	}
	if body["status"] != StatusAuthorized {
		t.Errorf("status = %v, want authorized", body["status"])
	}
	if body["accessToken"] == refresh {
		t.Error("the refresh response handed back the token it was given")
	}
}

// §8.11 asked whether this endpoint takes a body field or a header. It takes
// either, so neither choice can be the one that breaks the client.
func TestRefreshAlsoAcceptsTheTokenAsABearer(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	_, refresh := signInViaHTTP(t, engine)

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh", nil,
		map[string]string{"Authorization": "Bearer " + refresh})
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d: %s", rec.Code, rec.Body.String())
	}
	if body["status"] != StatusAuthorized {
		t.Errorf("status = %v, want authorized", body["status"])
	}
}

func TestRefreshReplayAnswers409AndRevokesTheChain(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	_, refresh := signInViaHTTP(t, engine)

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("the first rotation = %d", rec.Code)
	}

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	// §15.3: reuse is a 409, and the client treats 409 as terminal, which is
	// what forces the user back to the QR.
	if rec.Code != http.StatusConflict {
		t.Fatalf("the replay = %d, want 409: %s", rec.Code, rec.Body.String())
	}
	errObj, _ := body["error"].(map[string]any)
	if errObj == nil || errObj["code"] == nil {
		t.Fatalf("expected the standard error envelope, got %s", rec.Body.String())
	}
	if h.store.revokeCount() == 0 {
		t.Fatal("the chain was not revoked")
	}
}

func TestATransientRefreshAnswers503AndNotExpired(t *testing.T) {
	t.Parallel()

	// This is the assertion that keeps paying customers signed in. A 503 is
	// one of the four errors the client's poller treats as "keep waiting"; a
	// status:"expired" body is how it decides to clear its stored refresh
	// token. Getting this backwards signs people out over a database blip.
	h, engine := newTestServer(t)
	_, refresh := signInViaHTTP(t, engine)
	h.store.setRotateErr(errors.New("connection refused"))

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	if rec.Code != http.StatusServiceUnavailable {
		t.Fatalf("status = %d, want 503: %s", rec.Code, rec.Body.String())
	}
	if body["status"] == StatusExpired {
		t.Fatal("the response told the client the session is over")
	}
	if _, ok := body["accessToken"]; ok {
		t.Error("a transient response handed out a token")
	}
	if _, ok := body["refreshToken"]; ok {
		t.Error("a transient response echoed the refresh token")
	}

	// And the retry works, which is only true because the stored token
	// survived the failure.
	h.store.setRotateErr(nil)
	rec, body = doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	if rec.Code != http.StatusOK || body["status"] != StatusAuthorized {
		t.Fatalf("the retry after the blip = %d %v", rec.Code, body["status"])
	}
}

func TestRefreshingAnExpiredTokenAnswersTheExpiredStateWord(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": "wcb1_never-existed"}, nil)
	if rec.Code != http.StatusOK {
		t.Fatalf("status = %d, want 200", rec.Code)
	}
	if body["status"] != StatusExpired {
		t.Errorf("status = %v, want expired", body["status"])
	}
}

// ---------------------------------------------------------------------------
// Bearer auth
// ---------------------------------------------------------------------------

func TestAccountRequiresABearerToken(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	for _, path := range []string{"/v1/account"} {
		rec, _ := doJSON(t, engine, http.MethodGet, path, nil, nil)
		if rec.Code != http.StatusUnauthorized {
			t.Errorf("GET %s with no token = %d, want 401", path, rec.Code)
		}
		for _, bad := range []string{"", "Bearer ", "Bearer not-a-token", "Basic " + testSecret} {
			rec, _ = doJSON(t, engine, http.MethodGet, path, nil, map[string]string{"Authorization": bad})
			if rec.Code != http.StatusUnauthorized {
				t.Errorf("GET %s with %q = %d, want 401", path, bad, rec.Code)
			}
		}
	}
}

func TestAForgedBearerTokenIsRefused(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	h.store.seedAccount("acct-1")

	other, _ := token.NewIssuer("ffffffffffffffffffffffffffffffffffffffff", time.Hour, 24*time.Hour)
	forged, _ := other.Issue("acct-1", "")

	for _, raw := range []string{
		forged.Access.Token,
		"eyJhbGciOiJub25lIn0.eyJzdWIiOiJhY2N0LTEifQ.",
		strings.Repeat("a", 200),
	} {
		rec, _ := doJSON(t, engine, http.MethodGet, "/v1/account", nil,
			map[string]string{"Authorization": "Bearer " + raw})
		if rec.Code != http.StatusUnauthorized {
			t.Errorf("a forged token gave %d, want 401", rec.Code)
		}
	}
}

func TestAnExpiredAccessTokenIsRefused(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	access, _ := signInViaHTTP(t, engine)
	h.clock = h.clock.Add(2 * testAccessTTL)

	rec, _ := doJSON(t, engine, http.MethodGet, "/v1/account", nil,
		map[string]string{"Authorization": "Bearer " + access})
	if rec.Code != http.StatusUnauthorized {
		t.Errorf("an expired token gave %d, want 401", rec.Code)
	}
}

func TestTheBearerSchemeIsCaseInsensitive(t *testing.T) {
	t.Parallel()

	_, engine := newTestServer(t)
	access, _ := signInViaHTTP(t, engine)
	for _, scheme := range []string{"Bearer ", "bearer ", "BEARER "} {
		rec, _ := doJSON(t, engine, http.MethodGet, "/v1/account", nil,
			map[string]string{"Authorization": scheme + access})
		if rec.Code == http.StatusUnauthorized {
			t.Errorf("the %q scheme was rejected; RFC 7235 says it is case-insensitive", strings.TrimSpace(scheme))
		}
	}
}

// ---------------------------------------------------------------------------
// Revocation
// ---------------------------------------------------------------------------

func TestRevokeWithARefreshTokenEndsTheSession(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	access, refresh := signInViaHTTP(t, engine)

	rec, _ := doJSON(t, engine, http.MethodDelete, "/v1/auth/session",
		map[string]any{"refreshToken": refresh}, nil)
	if rec.Code != http.StatusNoContent {
		t.Fatalf("status = %d, want 204: %s", rec.Code, rec.Body.String())
	}

	claims, _ := h.svc.issuer.Verify(access)
	if !h.store.isRevoked(claims.ChainID) {
		t.Fatal("the chain is still live")
	}
	rec, _ = doJSON(t, engine, http.MethodGet, "/v1/account", nil,
		map[string]string{"Authorization": "Bearer " + access})
	if rec.Code != http.StatusUnauthorized && rec.Code != http.StatusOK {
		t.Fatalf("unexpected status after revocation: %d", rec.Code)
	}
	rec, body := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh",
		map[string]any{"refreshToken": refresh}, nil)
	if body["status"] != StatusExpired {
		t.Errorf("a revoked session still refreshed: %v", body["status"])
	}
}

// §8.11 asked whether DELETE revokes the refresh or the access token. It
// accepts either, and an expired access token still works for the purpose —
// signing out is exactly when the token is most likely to be dead, and a
// server that then refuses is a server that leaves people stuck.
func TestRevokeWithAnExpiredAccessTokenStillSignsOut(t *testing.T) {
	t.Parallel()

	h, engine := newTestServer(t)
	access, _ := signInViaHTTP(t, engine)
	claims, _ := h.svc.issuer.Verify(access)

	h.clock = h.clock.Add(2 * testAccessTTL)

	rec, _ := doJSON(t, engine, http.MethodDelete, "/v1/auth/session", nil,
		map[string]string{"Authorization": "Bearer " + access})
	if rec.Code != http.StatusNoContent {
		t.Fatalf("status = %d, want 204", rec.Code)
	}
	if !h.store.isRevoked(claims.ChainID) {
		t.Fatal("an expired access token could not revoke its chain")
	}
}

func TestRevokeWithNothingIsStill204(t *testing.T) {
	t.Parallel()

	// Signing out twice, or on a machine that never signed in, is not an
	// error. Making it one produces a scary dialog for the user and buys
	// nothing.
	_, engine := newTestServer(t)
	rec, _ := doJSON(t, engine, http.MethodDelete, "/v1/auth/session", nil, nil)
	if rec.Code != http.StatusNoContent {
		t.Errorf("status = %d, want 204", rec.Code)
	}
}

// ---------------------------------------------------------------------------
// Logging
// ---------------------------------------------------------------------------

// The secret never reaches a log line. This runs the whole flow against a
// logger that keeps everything in memory and then greps it.
func TestNothingSensitiveReachesTheLog(t *testing.T) {
	t.Parallel()

	var sink bytes.Buffer
	wc := newWechatStub(t)
	st := newFakeStore()
	issuer, _ := token.NewIssuer(testSecret, testAccessTTL, testRefreshTTL)
	client := wechat.NewClient(wechat.Config{
		AppID: testAppID, AppSecret: testAppSecret,
		AuthURL: "https://open.weixin.qq.com/connect/qrconnect",
		APIBase: wc.srv.URL, Timeout: 5 * time.Second,
	})

	clock := time.Now().UTC()
	issuer.SetClock(func() time.Time { return clock })

	svc, err := NewService(Config{
		PublicBaseURL: testPublicBase, AuthSessionTTL: testSessionTTL,
	}, Options{
		Store: st, WeChat: client, Issuer: issuer,
		Handoff: NewMemoryHandoff(func() time.Time { return clock }),
		Now:     func() time.Time { return clock },
		Logger:  slog.New(slog.NewJSONHandler(&sink, nil)),
	})
	if err != nil {
		t.Fatalf("NewService: %v", err)
	}

	created, err := svc.CreateSession(t.Context(), "install-1", "device-1")
	if err != nil {
		t.Fatalf("CreateSession: %v", err)
	}
	if _, err := svc.HandleCallback(t.Context(), "auth-code", created.Ticket); err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}
	poll, err := svc.Poll(t.Context(), created.Ticket)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if _, err := svc.Refresh(t.Context(), poll.RefreshToken); err != nil {
		t.Fatalf("Refresh: %v", err)
	}

	logged := sink.String()
	if logged == "" {
		t.Fatal("the flow produced no log lines at all, so this test proves nothing")
	}
	forbidden := map[string]string{
		testAppSecret:     "the WeChat app secret",
		poll.AccessToken:  "an access token",
		poll.RefreshToken: "a refresh token",
		"oTESTOPENID":     "an openid",
	}
	for secret, what := range forbidden {
		if secret == "" {
			continue
		}
		if strings.Contains(logged, secret) {
			t.Errorf("%s was written to the log:\n%s", what, logged)
		}
	}
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

func keysOf(m map[string]any) []string {
	out := make([]string, 0, len(m))
	for k := range m {
		out = append(out, k)
	}
	return out
}

// assertAllLowerCamel walks a decoded response and fails on any key that is
// not lowerCamelCase. It catches a PascalCase key and a snake_case key alike,
// which is what a missing struct tag produces and a single-field assertion
// would miss.
func assertAllLowerCamel(t *testing.T, path string, v any) {
	t.Helper()
	switch typed := v.(type) {
	case map[string]any:
		// The error envelope is httpx's, not ours. Its request_id is
		// snake_case on purpose, decided in that package, and re-deciding it
		// here would break the client for the sake of a tidy test.
		if _, isErrorEnvelope := typed["error"]; isErrorEnvelope && len(typed) == 1 {
			return
		}
		for key, child := range typed {
			if !isLowerCamel(key) {
				t.Errorf("%s.%s is not lowerCamelCase", path, key)
			}
			assertAllLowerCamel(t, path+"."+key, child)
		}
	case []any:
		for _, child := range typed {
			assertAllLowerCamel(t, path+"[]", child)
		}
	}
}

func isLowerCamel(s string) bool {
	if s == "" {
		return false
	}
	if s[0] < 'a' || s[0] > 'z' {
		return false
	}
	for _, r := range s {
		switch {
		case r >= 'a' && r <= 'z', r >= 'A' && r <= 'Z', r >= '0' && r <= '9':
		default:
			return false
		}
	}
	return true
}

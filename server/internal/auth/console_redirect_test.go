package auth

import (
	"net/http"
	"net/url"
	"strings"
	"testing"
)

func TestConsoleCallbackRedirectsToBusinessConsoleWithoutConsumingTokens(t *testing.T) {
	t.Parallel()
	for _, prefix := range []string{"", "/wechat_auth"} {
		t.Run(prefix, func(t *testing.T) {
			h, engine := newTestServer(t)
			h.svc.cfg.PublicBaseURL = testPublicBase + prefix
			created := createTicket(t, engine, "wcbadmin-0123456789abcdef0123456789abcdef")
			ticket := created["ticket"].(string)
			query := url.Values{"code": {"auth-code"}, "state": {ticket}, "returnTo": {"https://untrusted.invalid/"}}
			rec, _ := doJSON(t, engine, http.MethodGet, CallbackPath+"?"+query.Encode(), nil, nil)
			if rec.Code != http.StatusSeeOther {
				t.Fatalf("callback status = %d, want 303", rec.Code)
			}
			location := rec.Header().Get("Location")
			if want := testPublicBase + prefix + "/admin/"; location != want {
				t.Fatalf("redirect = %q, want %q", location, want)
			}
			if strings.Contains(location, ticket) || strings.Contains(location, "auth-code") {
				t.Fatal("callback exposed login credentials in the redirect URL")
			}
			if rec.Header().Get("Cache-Control") != "no-store" || rec.Header().Get("Referrer-Policy") != "no-referrer" {
				t.Fatal("redirect must not cache credentials or expose the callback referrer")
			}
			result, err := h.svc.Poll(t.Context(), ticket)
			if err != nil || result.Status != StatusAuthorized || result.AccessToken == "" || result.RefreshToken == "" {
				t.Fatalf("the returning console cannot collect credentials: %+v, %v", result, err)
			}
		})
	}
}

func TestFailedConsoleCallbackDoesNotRedirect(t *testing.T) {
	t.Parallel()
	h, engine := newTestServer(t)
	created := createTicket(t, engine, "wcbadmin-0123456789abcdef0123456789abcdef")
	h.wc.exchangeHandler = func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write([]byte(`{"errcode":40029,"errmsg":"invalid code"}`))
	}
	for _, state := range []string{"unknown", created["ticket"].(string)} {
		rec, _ := doJSON(t, engine, http.MethodGet, CallbackPath+"?code=invalid&state="+state+"&returnTo=/admin/", nil, nil)
		if rec.Code != http.StatusBadRequest || rec.Header().Get("Location") != "" {
			t.Fatal("an invalid state or code must not be treated as successful console login")
		}
	}
}

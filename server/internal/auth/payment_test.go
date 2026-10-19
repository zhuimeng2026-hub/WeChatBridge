package auth

import (
	"strings"
	"testing"
)

// The page must be inert.
//
// Its URL is one-shot: the redirect that lands here spends the ticket, so any
// second request for the same URL is answered 「登录已失效」. An auto-refresh is
// therefore not a refresh — five seconds after the payment code appears, it
// replaces the code with an error page and takes the buyer's only copy of the
// machine code with it.
//
// This shipped once and only reproduced against a real WeChat code, which is
// why it needs a test rather than a remember-to-not-do-it.
func TestTheAuthorizedPageNeverReRequestsItself(t *testing.T) {
	t.Parallel()

	code := LoadPaymentCode("", "aiwomeishi", "")
	page, err := code.Render(pageAuthorized, "install-1")
	if err != nil {
		t.Fatalf("Render: %v", err)
	}
	body := string(page)

	for _, self := range []string{"location.reload", "setTimeout", "setInterval", "<script", "window.location", "history.go"} {
		if strings.Contains(body, self) {
			t.Errorf("the page contains %q; re-requesting a one-shot URL turns the payment code into an error page", self)
		}
	}
	// A meta refresh would do the same thing more quietly.
	if strings.Contains(body, "http-equiv=\"refresh\"") || strings.Contains(body, "http-equiv='refresh'") {
		t.Error("the page carries a meta refresh, which has the same effect as location.reload()")
	}
}

// The failure pages are equally one-shot URLs and must be just as inert.
func TestTheFailurePageNeverReRequestsItself(t *testing.T) {
	t.Parallel()

	code := LoadPaymentCode("", "aiwomeishi", "")
	for _, kind := range []pageKind{pageInvalidState, pageInvalidCode, pageUpstreamDown, pageFailed} {
		page, err := code.Render(kind, "")
		if err != nil {
			t.Fatalf("Render(%d): %v", kind, err)
		}
		if strings.Contains(string(page), "reload") || strings.Contains(string(page), "<script") {
			t.Errorf("failure page %d re-requests itself", kind)
		}
	}
}

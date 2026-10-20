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

// A seller's sign-in must not land on the buyer's payment page.
//
// The console authenticates through the same frozen endpoints as the client, so
// its ticket comes back to the same redirect_uri — and the browser that just
// scanned it is the seller's. Showing them a payment code at that moment reads
// as "you owe someone money" while they are trying to get into their own
// dashboard. The status code, and the fact that this is a 200 page rather than
// a redirect, are unchanged: only the body differs.
func TestTheAuthorizedPageForTheConsoleCarriesNoPaymentInstructions(t *testing.T) {
	t.Parallel()

	const consoleInstallID = "wcbadmin-" + "0123456789abcdef0123456789abcdef"

	code := LoadPaymentCode("", "aiwomeishi", "")
	page, err := code.Render(pageAuthorized, consoleInstallID)
	if err != nil {
		t.Fatalf("Render: %v", err)
	}
	body := string(page)

	if !strings.Contains(body, "登录成功") {
		t.Error("the console page does not say the sign-in worked")
	}
	if !strings.Contains(body, "可以关掉") {
		t.Error("the console page does not tell the seller what to do with this tab")
	}
	// The machine code is the buyer's half of a manual payment match. On a
	// console sign-in there is no purchase, so echoing it is pure noise — and
	// it is the one field that came from an unauthenticated ticket.
	if strings.Contains(body, consoleInstallID) {
		t.Error("the console page echoes a machine code; a console sign-in buys nothing")
	}
	if strings.Contains(body, "付款") || strings.Contains(body, "收款码") {
		t.Error("the console page carries payment instructions")
	}
}

// The console branch is keyed on an exact install id, not a prefix. Otherwise
// a buyer who picked "wcbadmin-…" would be shown a page that says the sign-in
// is done and to close the tab — which is advice that loses a real payment.
func TestOnlyTheExactConsoleInstallIDGetsTheConsolePage(t *testing.T) {
	t.Parallel()

	code := LoadPaymentCode("", "aiwomeishi", "")
	for _, id := range []string{
		"wcbadmin-nothex",
		"wcbadmin-0123456789abcdef0123456789abcde",   // one short
		"wcbadmin-0123456789abcdef0123456789abcdef0", // one long
		"prefix-wcbadmin-0123456789abcdef0123456789abcdef",
	} {
		page, err := code.Render(pageAuthorized, id)
		if err != nil {
			t.Fatalf("Render(%q): %v", id, err)
		}
		if strings.Contains(string(page), "这个标签页可以关掉") {
			t.Errorf("install id %q got the console page; only the console's own id may", id)
		}
	}
}

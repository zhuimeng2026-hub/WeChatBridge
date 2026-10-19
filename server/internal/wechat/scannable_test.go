package wechat

import (
	"errors"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"
	"time"
)

// A trimmed copy of the real qrconnect page: enough to be realistic about the
// three places the path appears, and about the fact that the surrounding markup
// is full of other /connect/ URLs that are not the login code.
const qrconnectPage = `<!DOCTYPE html><html><head><title>微信登录</title></head><body>
<script src="https://res.wx.qq.com/open/js/qrconnect-1.0.0.js"></script>
<img class="js_qrcode_img web_qrcode_img" src="/connect/qrcode/071PV0bL1Lv5000M">
<input type="hidden" id="uuid" value="071PV0bL1Lv5000M">
<a href="/connect/qrconnect?appid=other">换一个</a>
</body></html>`

// The resolver is the one piece of this package that reads an HTML page, so
// these tests are mostly about what it does with a page it cannot use: a 200
// with nothing to extract is WeChat's normal shape for "this code is gone", and
// treating it as an empty result would silently hand the caller a dead image.
//
// The stub also stands in for the host, not just the page. An authorize URL
// built with the real AuthURL reaches open.weixin.qq.com, and that URL is
// exactly the one a phone cannot finish a sign-in with — so a test that
// fetched it would be testing the network and asserting against whatever
// WeChat answered that day.
func newResolverClient(t *testing.T, handler http.HandlerFunc) (*Client, string) {
	t.Helper()
	srv := httptest.NewServer(handler)
	t.Cleanup(srv.Close)
	c := NewClient(Config{AppID: "wxappid0001", AuthURL: srv.URL + "/connect/qrconnect"})
	return c, srv.URL
}

func TestScannableQRURLPullsTheLoginCodeOutOfTheAuthorizePage(t *testing.T) {
	t.Parallel()

	var gotUA string
	c, origin := newResolverClient(t, func(rw http.ResponseWriter, r *http.Request) {
		gotUA = r.Header.Get("User-Agent")
		rw.Header().Set("Content-Type", "text/html")
		_, _ = rw.Write([]byte(qrconnectPage))
	})

	got, err := c.ScannableQRURL(t.Context(), c.AuthorizeURL("ticket-abc", "https://api.example.com/cb"))
	if err != nil {
		t.Fatalf("ScannableQRURL: %v", err)
	}

	// Absolute, and rooted at the host the page came from — the path in the
	// markup is relative, and a relative string handed to the QR encoder would
	// produce a code that resolves against nothing.
	want := origin + "/connect/qrcode/071PV0bL1Lv5000M"
	if got != want {
		t.Errorf("ScannableQRURL = %q, want %q", got, want)
	}

	// The UA is the whole reason this function can work at all: WeChat serves
	// a different page to a mobile UA and the mobile one has no qrcode img in
	// it. Without this the next test would pass for the wrong reason.
	if !strings.Contains(gotUA, "Windows NT 10.0") {
		t.Errorf("User-Agent = %q, want a desktop UA", gotUA)
	}
}

func TestScannableQRURLResolvesAgainstTheHostThatAnswered(t *testing.T) {
	t.Parallel()

	srv := httptest.NewServer(http.HandlerFunc(func(rw http.ResponseWriter, r *http.Request) {
		_, _ = rw.Write([]byte(qrconnectPage))
	}))
	t.Cleanup(srv.Close)

	c := NewClient(Config{AppID: "wxappid0001", AuthURL: srv.URL})
	got, err := c.ScannableQRURL(t.Context(), srv.URL+"/connect/qrconnect?state=t")
	if err != nil {
		t.Fatalf("ScannableQRURL: %v", err)
	}
	if want := srv.URL + "/connect/qrcode/071PV0bL1Lv5000M"; got != want {
		t.Errorf("ScannableQRURL = %q, want %q", got, want)
	}
}

func TestScannableQRURLReportsAPageWithNoLoginCodeAsMalformed(t *testing.T) {
	t.Parallel()

	// What WeChat answers when the code has expired, and what an interception
	// or a captive portal looks like: 200, HTML, nothing to use. This must not
	// be reported as an empty success.
	c, origin := newResolverClient(t, func(rw http.ResponseWriter, r *http.Request) {
		_, _ = rw.Write([]byte(`<html><body><h1 class="msg_title">二维码已失效</h1></body></html>`))
	})

	_, err := c.ScannableQRURL(t.Context(), origin+"/connect/qrconnect?state=t")
	if err == nil {
		t.Fatal("want an error for a page with no login code, got nil")
	}
	var malformed *MalformedError
	if !errors.As(err, &malformed) {
		t.Fatalf("error is %T, want *MalformedError", err)
	}
	// Not transient on purpose: a page that consistently lacks the path is a
	// broken open-platform configuration or a changed page, and retrying it is
	// not progress.
	if IsTransient(err) {
		t.Error("IsTransient = true, want false: a consistently wrong page needs a human")
	}
}

func TestScannableQRURLReportsANonOKStatus(t *testing.T) {
	t.Parallel()

	c, origin := newResolverClient(t, func(rw http.ResponseWriter, r *http.Request) {
		rw.WriteHeader(http.StatusBadGateway)
		_, _ = rw.Write([]byte(`<html>502</html>`))
	})

	if _, err := c.ScannableQRURL(t.Context(), origin+"/connect/qrconnect?state=t"); err == nil {
		t.Fatal("want an error for a 502, got nil")
	}
}

func TestScannableQRURLReportsAConnectionFailureAsTransient(t *testing.T) {
	t.Parallel()

	srv := httptest.NewServer(http.HandlerFunc(func(http.ResponseWriter, *http.Request) {}))
	c := NewClient(Config{AppID: "wxappid0001", AuthURL: srv.URL})
	srv.Close() // nothing is listening any more

	_, err := c.ScannableQRURL(t.Context(), srv.URL+"/connect/qrconnect?state=t")
	if err == nil {
		t.Fatal("want an error when the host is gone, got nil")
	}
	// This is the case the login page's fallback exists for: WeChat having a
	// bad minute is worth retrying, unlike a page that is consistently wrong.
	if !IsTransient(err) {
		t.Errorf("IsTransient = false for %v, want true", err)
	}
}

func TestScannableQRURLDoesNotFetchAnythingForAnEmptyAuthorizeURL(t *testing.T) {
	t.Parallel()

	var called bool
	c, _ := newResolverClient(t, func(rw http.ResponseWriter, r *http.Request) {
		called = true
		_, _ = rw.Write([]byte(qrconnectPage))
	})

	if _, err := c.ScannableQRURL(t.Context(), ""); err == nil {
		t.Fatal("want an error for an empty authorize URL, got nil")
	}
	if called {
		t.Error("the request was sent for an empty authorize URL")
	}
}

func TestScannableQRURLAbandonsASlowPageAtItsOwnDeadline(t *testing.T) {
	t.Parallel()

	// A person is looking at a login page waiting for this image. The shared
	// client allows 10s for a server-to-server call nobody watches; here the
	// wait has to end sooner, and the caller has to be told rather than left
	// holding a socket that will not close.
	release := make(chan struct{})
	defer close(release)

	c, origin := newResolverClient(t, func(rw http.ResponseWriter, r *http.Request) {
		select {
		case <-release:
		case <-r.Context().Done():
		}
	})
	c.http.Timeout = scannableQRTimeout * 2 // the page never arrives on its own

	start := time.Now()
	_, err := c.ScannableQRURL(t.Context(), origin+"/connect/qrconnect?state=t")
	elapsed := time.Since(start)

	if err == nil {
		t.Fatal("want an error for a page that never arrives, got nil")
	}
	if elapsed > scannableQRTimeout+time.Second {
		t.Errorf("gave up after %v, want about %v", elapsed, scannableQRTimeout)
	}
}

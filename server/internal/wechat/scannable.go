package wechat

import (
	"context"
	"errors"
	"io"
	"net/http"
	"regexp"
	"time"
)

// This file exists because of one fact about the platform's QR sign-in: the URL
// we encode into the QR image is not the URL WeChat recognises as a login.
//
// AuthorizeURL builds https://open.weixin.qq.com/connect/qrconnect?…, and that
// page is the *desktop browser's* view of the flow. A phone that scans a QR
// containing it treats the string as an ordinary link and opens it in-app,
// landing on a page built for a desktop and unable to complete the sign-in —
// the callback never arrives and the whole flow looks like "the scan did
// nothing". Probed live 2026-10-19: the open platform and nginx both counted
// zero callbacks while sellers were scanning.
//
// The code that does work is issued by WeChat itself and lives one hop deeper:
// /connect/qrcode/<uuid>, rendered into that same page as the src of
// <img class="js_qrcode_img web_qrcode_img">. WeChat recognises that one as a
// login code, and it is the image WeChat serves at that path (a 470x470 JPEG,
// measured). There is no API for it — the page is the only source — so this
// fetches the page and pulls the path out of it.
//
// Fetching it here rather than in a browser is what keeps the QR image
// on-screen usable: every client that renders /v1/auth/session/{t}/qr gets a
// code the phone accepts, with no second tab and no second scan. The cost is
// one outbound request per image, which is why the deadline below is short.

// desktopUserAgent is load-bearing, not decoration. WeChat serves a different
// page per User-Agent, and the mobile one has no <img class="js_qrcode_img"> in
// it — which is the very thing this function is looking for. A default Go UA
// is a plain "Go-http-client/1.1", so the request would come back 200 with
// nothing to extract and the caller would silently fall back to the dead code.
// Desktop Chrome is named because it is what a seller's browser would send.
const desktopUserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 " +
	"(KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36"

// scannableQRTimeout bounds the page fetch. The shared client already carries
// defaultTimeout (10s), which is the right ceiling for a server-to-server API
// call nobody is watching. This one is different: a human is staring at a login
// page waiting for an image, and ten seconds of nothing reads as a hang. Five
// is comfortably above the ~0.3s this page actually takes.
const scannableQRTimeout = 5 * time.Second

// qrcodePathRE pulls the uuid out of the src attribute. The attribute is
// required in the pattern: the path also appears in the page's JavaScript, and
// anchoring on src="…" is what makes "three occurrences of the same string" in
// the real page unambiguous. The character class is the uuid alphabet WeChat
// mints ([A-Za-z0-9_-]); it cannot run past a quote or a slash, so a malformed
// page yields no match rather than a wrong match.
var qrcodePathRE = regexp.MustCompile(`src="(/connect/qrcode/[A-Za-z0-9_-]+)"`)

// ScannableQRURL returns the /connect/qrcode/<uuid> URL for one authorize URL —
// the string that can be encoded into a QR image and actually scanned.
//
// It fetches the page authorizeURL points at, so it inherits everything that
// URL carries: the appid, the redirect_uri and the state. The uuid WeChat mints
// is bound to that page load, so the resulting scan redirects to the same
// redirect_uri with the same state as the authorize URL it came from. That is
// what keeps the ticket — and therefore the polling client — the same ticket.
//
// Every failure is an error rather than an empty string, and the caller decides
// what to do: a login image has no correct answer to fall back to other than
// the authorize URL it already had, and that decision belongs to the endpoint
// serving the PNG, not here.
func (c *Client) ScannableQRURL(ctx context.Context, authorizeURL string) (string, error) {
	const op = "scannable qr"
	if authorizeURL == "" {
		return "", &MalformedError{Op: op, Cause: errors.New("empty authorize url")}
	}

	ctx, cancel := context.WithTimeout(ctx, scannableQRTimeout)
	defer cancel()

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, authorizeURL, nil)
	if err != nil {
		return "", &TransportError{Op: op, Cause: err}
	}
	req.Header.Set("User-Agent", desktopUserAgent)
	req.Header.Set("Accept", "text/html")

	resp, err := c.http.Do(req)
	if err != nil {
		return "", &TransportError{Op: op, Cause: err}
	}
	defer resp.Body.Close()

	// maxBodyBytes rather than a new constant: this page measured 43,833 bytes,
	// larger than every JSON response in this package, but well inside the 1 MiB
	// ceiling that already exists to stop an unbounded body from being read.
	body, err := io.ReadAll(io.LimitReader(resp.Body, maxBodyBytes))
	if err != nil {
		return "", &TransportError{Op: op, Cause: err}
	}
	if resp.StatusCode != http.StatusOK {
		// Not an APIError: there is no errcode to branch on in an HTML page,
		// and the status is all there is to report.
		return "", &MalformedError{
			Op:    op,
			Body:  body,
			Cause: errors.New("unexpected status from the authorize page"),
		}
	}

	m := qrcodePathRE.FindSubmatch(body)
	if m == nil {
		// A 200 with no uuid in it is the shape WeChat's own failure pages take
		// ("二维码已失效", a maintenance page, a captcha). MalformedError rather
		// than a nil result because this is not transient and retrying a
		// consistently wrong page is not progress — it needs a human to look at
		// the open-platform configuration.
		return "", &MalformedError{
			Op:    op,
			Body:  body,
			Cause: errors.New("the authorize page carries no /connect/qrcode/ path"),
		}
	}

	// Resolve against the URL the response actually came from rather than
	// against c.authURL. The path in the page is relative, and the request may
	// have been redirected; resp.Request.URL is where the img would point.
	ref, err := resp.Request.URL.Parse(string(m[1]))
	if err != nil {
		return "", &MalformedError{Op: op, Body: body, Cause: err}
	}
	return ref.String(), nil
}

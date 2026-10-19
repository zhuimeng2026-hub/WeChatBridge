package avatar

import (
	"bytes"
	"context"
	"errors"
	"net/http"
	"net/http/httptest"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"testing"
	"time"
)

// A minimal valid PNG: the eight-byte signature followed by an IHDR chunk.
// The package never decodes the image, so this only has to be shaped like one
// closely enough that a future decoder added to a test would not immediately
// fail for an unrelated reason.
var pngBytes = append([]byte{0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A},
	[]byte{0x00, 0x00, 0x00, 0x0D, 'I', 'H', 'D', 'R'}...)

var jpegBytes = []byte{0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 'J', 'F', 'I', 'F', 0x00, 0x01}

// newTestCache builds a cache over a temporary directory, pointed at a test
// server, with the host allow-list opened up — httptest serves on 127.0.0.1,
// which wechatHosts would refuse.
//
// It returns the cache and the server's own base URL. The tests fetch *that*
// URL rather than a plausible-looking qlogo.cn one: pointing a test at a real
// host makes it depend on somebody else's uptime, and a test suite that has
// started making outbound requests to prove a feature works is a suite nobody
// can run on a machine without internet.
func newTestCache(t *testing.T, h http.Handler, opts ...Option) (*Cache, string) {
	t.Helper()
	// TLS rather than plain HTTP: the cache refuses anything that is not
	// https, and a test that had to loosen that rule to run would be testing a
	// configuration the product never ships.
	srv := httptest.NewTLSServer(h)
	t.Cleanup(srv.Close)

	base := []Option{
		WithHTTPClient(srv.Client()),
		WithHostAllow(func(*url.URL) bool { return true }),
	}
	c, err := New(t.TempDir(), append(base, opts...)...)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	return c, srv.URL
}

func TestFetchStoresByDigestAndReadsBack(t *testing.T) {
	t.Parallel()

	served := pngBytes
	c, base := newTestCache(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// Announce something the bytes are not. The stored MIME must follow
		// the bytes, not this header.
		w.Header().Set("Content-Type", "text/html; charset=utf-8")
		_, _ = w.Write(served)
	}))

	res, err := c.Fetch(context.Background(), base+"/a/0")
	if err != nil {
		t.Fatalf("Fetch: %v", err)
	}
	want := DigestOf(served)
	if res.Digest != want {
		t.Errorf("digest = %q, want %q", res.Digest, want)
	}
	if res.MIME != MIMEPNG {
		t.Errorf("mime = %q, want %q — it must come from the bytes", res.MIME, MIMEPNG)
	}
	if res.ByteSize != len(served) {
		t.Errorf("byte size = %d, want %d", res.ByteSize, len(served))
	}
	if res.Unchanged {
		t.Error("the first fetch cannot have found the file already there")
	}

	got, err := c.Open(res.Digest, res.MIME)
	if err != nil {
		t.Fatalf("Open: %v", err)
	}
	if !bytes.Equal(got, served) {
		t.Error("the bytes read back are not the bytes served")
	}
	// The name is the digest plus an extension a human recognises.
	if filepath.Base(res.Path) != want+".png" {
		t.Errorf("file name = %q, want %q", filepath.Base(res.Path), want+".png")
	}
}

func TestFetchRefusesBytesThatAreNotAnImage(t *testing.T) {
	t.Parallel()

	c, base := newTestCache(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// A captive portal answering an image request. This is the case the
		// Content-Type check would have missed.
		w.Header().Set("Content-Type", "image/png")
		_, _ = w.Write([]byte("<html><body>请先登录</body></html>"))
	}))

	if _, err := c.Fetch(context.Background(), base+"/a/0"); !errors.Is(err, ErrUnsupported) {
		t.Fatalf("err = %v, want ErrUnsupported", err)
	}
}

func TestFetchRefusesABodyOverTheCap(t *testing.T) {
	t.Parallel()

	c, base := newTestCache(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write(append(pngBytes, bytes.Repeat([]byte{0x00}, 4096)...))
	}), WithMaxBytes(64))

	if _, err := c.Fetch(context.Background(), base+"/a/0"); !errors.Is(err, ErrTooLarge) {
		t.Fatalf("err = %v, want ErrTooLarge", err)
	}
}

func TestFetchRefusesANonHTTPSURL(t *testing.T) {
	t.Parallel()

	// This one keeps the DEFAULT host allow-list on purpose. A test that opened
	// the allow-list to reach a local server cannot then claim to prove the
	// allow-list works, and the allow-list is the control that stops this
	// service fetching whatever an upstream tells it to.
	c, err := New(t.TempDir())
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	for _, raw := range []string{
		"http://wx.qlogo.cn/a/0",         // the right host, the wrong scheme
		"file:///etc/passwd",             // not a URL a picture comes from
		"https://evil.example.com/a.png", // the wrong host entirely
		"https://wx.qlogo.cn.evil.example/a.png",
	} {
		if _, err := c.Fetch(context.Background(), raw); err == nil {
			t.Errorf("Fetch(%q) was allowed; it must be refused", raw)
		}
	}
}

func TestARefusedURLIsNeverRequested(t *testing.T) {
	t.Parallel()

	// The local server counts hits. The allow-list is opened so the URL points
	// at it, and the scheme is then downgraded: the request must never leave.
	var hits int
	c, base := newTestCache(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		hits++
		_, _ = w.Write(pngBytes)
	}))
	if _, err := c.Fetch(context.Background(), strings.Replace(base, "https://", "http://", 1)+"/a/0"); err == nil {
		t.Fatal("plain http must be refused even against an allowed host")
	}
	if hits != 0 {
		t.Errorf("the server was reached %d times; a refused URL must not be fetched", hits)
	}
}

func TestWechatHostsAcceptsTheCDNAndNothingElse(t *testing.T) {
	t.Parallel()

	allow := func(raw string) bool {
		u, err := url.Parse(raw)
		return err == nil && wechatHosts(u)
	}
	for _, good := range []string{
		"https://wx.qlogo.cn/a/0",
		"https://thirdwx.qlogo.cn/a/0",
		"https://mmbiz.qpic.cn/a/0",
		"https://weixin.qq.com/a/0",
		"https://res.wx.qq.com/a/0",
	} {
		if !allow(good) {
			t.Errorf("wechatHosts refused %q", good)
		}
	}
	for _, bad := range []string{
		"https://qlogo.cn.attacker.example/a.png",
		"https://notweixin.qq.com/a.png",
		"https://evil.example.com/a.png",
		"https://wx.qlogo.cn.evil.example/a.png",
		// A subdomain-shaped host must not pass on a suffix match against a
		// different domain entirely.
		"https://notqlogo.cn.attacker.example/a.png",
	} {
		if allow(bad) {
			t.Errorf("wechatHosts allowed %q", bad)
		}
	}
}

func TestFetchTwiceReportsUnchangedAndRewritesNothing(t *testing.T) {
	t.Parallel()

	var served = append([]byte(nil), jpegBytes...)
	c, base := newTestCache(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		_, _ = w.Write(served)
	}))

	first, err := c.Fetch(context.Background(), base+"/a/0")
	if err != nil {
		t.Fatalf("first Fetch: %v", err)
	}
	// WeChat hands out a different URL for the same picture; the digest is what
	// decides the file, not the URL.
	second, err := c.Fetch(context.Background(), base+"/a/1")
	if err != nil {
		t.Fatalf("second Fetch: %v", err)
	}
	if first.Digest != second.Digest {
		t.Errorf("the same bytes hashed to %q and %q", first.Digest, second.Digest)
	}
	if !second.Unchanged {
		t.Error("the second fetch of identical bytes should report Unchanged")
	}

	// A changed picture is a new file, not an overwrite.
	served = append(append([]byte(nil), jpegBytes...), 0x42)
	third, err := c.Fetch(context.Background(), base+"/a/2")
	if err != nil {
		t.Fatalf("third Fetch: %v", err)
	}
	if third.Digest == first.Digest || third.Unchanged {
		t.Error("changed bytes must produce a new digest and a new file")
	}
	if _, err := os.Stat(first.Path); err != nil {
		t.Errorf("the earlier avatar was removed: %v", err)
	}
}

func TestOpenRefusesADigestThatIsNotOne(t *testing.T) {
	t.Parallel()

	dir := t.TempDir()
	c, err := New(dir)
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	// The digest becomes part of a file name. A value containing a separator
	// must never reach filepath.Join, and the database's CHECK constraint is
	// not the only thing standing between a row and a path.
	for _, bad := range []string{
		"../../../etc/passwd",
		"..",
		"nothexbut64charslongaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
		strings.Repeat("A", 64), // uppercase: the constraint is lowercase hex
		"",
	} {
		if _, err := c.Open(bad, MIMEPNG); err == nil {
			t.Errorf("Open accepted digest %q", bad)
		}
	}
}

func TestOpenReportsAMissingFileAsNotExist(t *testing.T) {
	t.Parallel()

	c, err := New(t.TempDir())
	if err != nil {
		t.Fatalf("New: %v", err)
	}
	// A row whose file was swept must not look like a server fault: the
	// handler answers 404 and the client draws a blank, which is the state the
	// database was already describing.
	_, err = c.Open(DigestOf(pngBytes), MIMEPNG)
	if !errors.Is(err, os.ErrNotExist) {
		t.Fatalf("err = %v, want os.ErrNotExist", err)
	}
}

func TestDisabledCacheDoesNothingAndSaysSo(t *testing.T) {
	t.Parallel()

	// Empty dir is the "operator did not configure avatars" deployment. It must
	// be usable, not a startup failure, and every call must name the reason.
	c, err := New("")
	if err != nil {
		t.Fatalf("New(\"\") must be allowed: %v", err)
	}
	if !c.Disabled() {
		t.Fatal("an empty directory must report Disabled")
	}
	if _, err := c.Fetch(context.Background(), "https://wx.qlogo.cn/a/0"); !errors.Is(err, ErrDisabled) {
		t.Errorf("Fetch err = %v, want ErrDisabled", err)
	}
	if _, err := c.Open(DigestOf(pngBytes), MIMEPNG); !errors.Is(err, ErrDisabled) {
		t.Errorf("Open err = %v, want ErrDisabled", err)
	}
	// A nil pointer is the same deployment reached a different way.
	var nilCache *Cache
	if !nilCache.Disabled() {
		t.Error("a nil Cache must report Disabled")
	}
	if _, err := nilCache.Fetch(context.Background(), "https://wx.qlogo.cn/a/0"); !errors.Is(err, ErrDisabled) {
		t.Errorf("nil Fetch err = %v, want ErrDisabled", err)
	}
}

func TestNewRefusesADirectoryItCannotCreate(t *testing.T) {
	t.Parallel()

	// A path under a file, not a directory. Silently degrading to "no avatars"
	// here would leave an operator with a configured service that quietly keeps
	// serving URLs that expire.
	blocked := filepath.Join(t.TempDir(), "a-file")
	if err := os.WriteFile(blocked, []byte("x"), 0o600); err != nil {
		t.Fatal(err)
	}
	if _, err := New(filepath.Join(blocked, "avatars")); err == nil {
		t.Fatal("New must fail when the directory cannot be created")
	}
}

func TestFetchHonoursTheContextAndTheStatus(t *testing.T) {
	t.Parallel()

	slow, slowBase := newTestCache(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		select {
		case <-r.Context().Done():
		case <-time.After(2 * time.Second):
		}
	}))
	ctx, cancel := context.WithTimeout(context.Background(), 50*time.Millisecond)
	defer cancel()
	if _, err := slow.Fetch(ctx, slowBase+"/a/0"); err == nil {
		t.Fatal("a cancelled fetch must fail; a sign-in cannot wait on the CDN")
	}

	failing, failingBase := newTestCache(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		// WeChat answers 404 for a picture the person has removed. That is an
		// answer, not a crash.
		w.WriteHeader(http.StatusNotFound)
	}))
	if _, err := failing.Fetch(context.Background(), failingBase+"/a/0"); err == nil {
		t.Fatal("a 404 must be an error, not an empty avatar")
	} else if !strings.Contains(err.Error(), "404") {
		t.Errorf("err = %v, want it to name the status", err)
	}
}

func TestFetchSendsAnAcceptHeaderWeCanDecode(t *testing.T) {
	t.Parallel()

	var gotAccept string
	c, base := newTestCache(t, http.HandlerFunc(func(w http.ResponseWriter, r *http.Request) {
		gotAccept = r.Header.Get("Accept")
		_, _ = w.Write(pngBytes)
	}))
	if _, err := c.Fetch(context.Background(), base+"/a/0"); err != nil {
		t.Fatalf("Fetch: %v", err)
	}
	if !strings.Contains(gotAccept, MIMEJPEG) || !strings.Contains(gotAccept, MIMEPNG) {
		t.Errorf("Accept = %q, want it to name both formats we accept", gotAccept)
	}
}

func TestSniffRecognisesOnlyTheTwoAcceptedFormats(t *testing.T) {
	t.Parallel()

	cases := []struct {
		name string
		in   []byte
		want string
		ok   bool
	}{
		{"png", pngBytes, MIMEPNG, true},
		{"jpeg", jpegBytes, MIMEJPEG, true},
		{"gif", []byte("GIF89a...."), "", false},
		{"webp", []byte("RIFF....WEBPVP8 "), "", false},
		{"svg", []byte(`<svg xmlns="http://www.w3.org/2000/svg"/>`), "", false},
		{"empty", nil, "", false},
		{"one byte", []byte{0xFF}, "", false},
	}
	for _, tc := range cases {
		got, ok := sniff(tc.in)
		if got != tc.want || ok != tc.ok {
			t.Errorf("sniff(%s) = %q,%v; want %q,%v", tc.name, got, ok, tc.want, tc.ok)
		}
	}
}

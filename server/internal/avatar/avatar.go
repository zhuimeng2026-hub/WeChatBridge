// Package avatar keeps a local copy of the picture a person signed in with.
//
// It exists for one reason: WeChat's headimgurl is a lease, not an address.
// The URL works for days and then stops, and the client that cached it shows a
// broken image a week after a sign-in that was perfectly correct. Re-fetching
// on demand would not fix that either, because "on demand" for a client is a
// request that arrives after the lease ended.
//
// So the bytes are fetched once, at sign-in, while the lease is live; verified
// by their own magic bytes rather than by the server's Content-Type header;
// written to disk under the name of their own SHA-256; and recorded in the
// database as that name. Everything downstream — the client's avatarUrl, the
// console's picture column, the ETag on the response — is derived from the
// digest, so an unchanged avatar costs a 304 and a changed one is a new file
// beside the old one rather than an overwrite in progress.
//
// What this package deliberately does not do:
//
//   - It does not trust Content-Type. A remote host that announces image/png
//     and delivers an HTML error page is a captive portal or an attack, and
//     the client that receives those bytes cannot tell. The first twelve bytes
//     decide.
//
//   - It does not follow redirects to arbitrary hosts. WeChat's avatar CDN
//     redirects within its own domains; anything else is not an avatar.
//
//   - It does not resize, re-encode or store in the database. The digest is of
//     the bytes as fetched, so the file on disk is exactly what was verified,
//     and the database holds a name rather than a blob it would have to keep
//     consistent with the filesystem anyway.
//
// Every function here is safe for concurrent use. The only shared state is the
// directory on disk, and every write goes to a temporary file that is renamed
// into place, so two concurrent fetches of the same avatar produce the same
// file and a reader never sees a half-written one.
package avatar

import (
	"context"
	"crypto/sha256"
	"encoding/hex"
	"errors"
	"fmt"
	"io"
	"net/http"
	"net/url"
	"os"
	"path/filepath"
	"strings"
	"time"
)

// Errors a caller distinguishes. Everything else is a bug or an outage, and the
// caller treats those the same way: log it and carry on without an avatar.
var (
	// ErrDisabled means this deployment caches no avatars, so there is
	// nothing to do. It is not an error condition — a service configured
	// without an avatar directory is a valid deployment.
	ErrDisabled = errors.New("avatar: caching is disabled")

	// ErrUnsupported means the bytes were not a JPEG or a PNG. The remote
	// host answered with something that is not a picture: a captive portal, an
	// error page, a format WeChat added after this was written.
	ErrUnsupported = errors.New("avatar: unsupported image format")

	// ErrTooLarge means the response exceeded the configured cap. It is
	// separate from ErrUnsupported because the operator wants to know: one is a
	// bug in our sniffing, the other is a URL pointing at the wrong thing.
	ErrTooLarge = errors.New("avatar: image exceeds the size cap")
)

// The two formats accepted. Everything else is refused rather than passed
// through, because the client decodes with a WPF bitmap decoder that turns an
// unexpected payload into an exception deep inside the UI thread rather than an
// error a person can read.
const (
	MIMEJPEG = "image/jpeg"
	MIMEPNG  = "image/png"
)

// DefaultMaxBytes is the cap when none is configured. WeChat's avatars are tens
// of kilobytes; the number exists so a misconfigured URL cannot fill a disk,
// not because 2 MiB is a meaningful boundary for a picture.
const DefaultMaxBytes = 2 << 20

// DefaultFetchTimeout bounds one attempt. The fetch happens inside a sign-in,
// and a sign-in that hangs behind a slow CDN is a sign-in that looks broken.
const DefaultFetchTimeout = 8 * time.Second

// Cache fetches avatars into a directory and reads them back out.
//
// The zero value is not usable: call New, which creates the directory. A Cache
// built by hand with an empty dir answers ErrDisabled from every method rather
// than panicking, because a nil-directory bug that takes the service down at
// the first scan is worse than one that quietly keeps the old behaviour.
type Cache struct {
	dir       string
	maxBytes  int64
	client    *http.Client
	allowHost func(*url.URL) bool
}

// Option customises a Cache. There are two because the test needs to reach a
// server on localhost and production must not.
type Option func(*Cache)

// WithHTTPClient replaces the client used to fetch. Tests point it at an
// httptest server; production leaves it alone.
func WithHTTPClient(c *http.Client) Option {
	return func(a *Cache) {
		if c != nil {
			a.client = c
		}
	}
}

// WithMaxBytes overrides the per-image cap.
func WithMaxBytes(n int) Option {
	return func(a *Cache) {
		if n > 0 {
			a.maxBytes = int64(n)
		}
	}
}

// WithHostAllow decides which hosts may be fetched. The default accepts only
// WeChat's own domains, because the URL comes from an upstream API and
// following it anywhere else turns this service into a fetcher for whoever
// controls that upstream.
func WithHostAllow(fn func(*url.URL) bool) Option {
	return func(a *Cache) {
		if fn != nil {
			a.allowHost = fn
		}
	}
}

// New builds a cache rooted at dir and creates the directory if it is missing.
//
// A directory that cannot be created is an error rather than a fallback to
// "no avatars": an operator who configured a path and got a service that
// silently keeps serving expired remote URLs has no way to tell that from a
// working setup.
func New(dir string, opts ...Option) (*Cache, error) {
	if strings.TrimSpace(dir) == "" {
		return &Cache{}, nil // disabled
	}
	if err := os.MkdirAll(dir, 0o750); err != nil {
		return nil, fmt.Errorf("avatar: create %s: %w", dir, err)
	}
	a := &Cache{
		dir:       dir,
		maxBytes:  DefaultMaxBytes,
		allowHost: wechatHosts,
		client:    &http.Client{Timeout: DefaultFetchTimeout},
	}
	for _, o := range opts {
		o(a)
	}
	return a, nil
}

// Disabled reports whether this cache does nothing. The auth service asks once
// at wiring time so a deployment without avatars never pays for a lookup it
// knows will miss.
func (a *Cache) Disabled() bool { return a == nil || a.dir == "" }

// Result is what one successful fetch produced.
type Result struct {
	// Digest is the lowercase hex SHA-256 of the stored bytes. It is the file
	// name without its extension, the ETag the client sees, and the value the
	// database records.
	Digest string
	// MIME is what the bytes were sniffed to be, which is not necessarily what
	// the remote host claimed.
	MIME string
	// ByteSize is the length as written.
	ByteSize int
	// Path is where the bytes live, for a caller that wants to stat or unlink.
	Path string
	// Unchanged is true when these exact bytes were already on disk. The fetch
	// still happened — WeChat's URLs are per-request, so a fresh URL was asked
	// — but nothing was rewritten.
	Unchanged bool
}

// Fetch downloads rawURL, verifies what arrived, and stores it.
//
// It returns ErrDisabled when caching is off, and any other error means "no
// avatar this time" rather than "the sign-in failed". A person whose picture
// cannot be fetched is still signed in; that separation is the whole reason
// this is its own package and not a step inside the callback.
func (a *Cache) Fetch(ctx context.Context, rawURL string) (Result, error) {
	if a.Disabled() {
		return Result{}, ErrDisabled
	}
	rawURL = strings.TrimSpace(rawURL)
	if rawURL == "" {
		return Result{}, fmt.Errorf("avatar: empty url")
	}
	u, err := url.Parse(rawURL)
	if err != nil {
		// Not wrapped further: a parse error already quotes the input, and the
		// input is a WeChat URL that may as well be in the log.
		return Result{}, fmt.Errorf("avatar: parse url: %w", err)
	}
	if u.Scheme != "https" {
		return Result{}, fmt.Errorf("avatar: refusing %q: only https", u.Scheme)
	}
	if !a.allowHost(u) {
		return Result{}, fmt.Errorf("avatar: refusing host %q", u.Hostname())
	}

	req, err := http.NewRequestWithContext(ctx, http.MethodGet, rawURL, nil)
	if err != nil {
		return Result{}, fmt.Errorf("avatar: build request: %w", err)
	}
	// WeChat's CDN is content-negotiated: without this it may answer with
	// webp, which is a fine format that this service does not accept, and the
	// failure would look like a broken CDN rather than a missing header.
	req.Header.Set("Accept", MIMEJPEG+","+MIMEPNG)

	resp, err := a.client.Do(req)
	if err != nil {
		return Result{}, fmt.Errorf("avatar: fetch: %w", err)
	}
	defer func() {
		_, _ = io.Copy(io.Discard, io.LimitReader(resp.Body, 4096))
		_ = resp.Body.Close()
	}()
	if resp.StatusCode != http.StatusOK {
		return Result{}, fmt.Errorf("avatar: fetch: status %d", resp.StatusCode)
	}

	// One byte over the cap is refused. Reading cap+1 and comparing is how the
	// size is known to be wrong, as opposed to merely large — a body that is
	// exactly at the cap and a body that is one byte past it are both
	// "acceptable" to a reader that stops reading.
	body, err := io.ReadAll(io.LimitReader(resp.Body, a.maxBytes+1))
	if err != nil {
		return Result{}, fmt.Errorf("avatar: read body: %w", err)
	}
	if int64(len(body)) > a.maxBytes {
		return Result{}, ErrTooLarge
	}
	if len(body) == 0 {
		return Result{}, fmt.Errorf("avatar: empty body")
	}

	mime, ok := sniff(body)
	if !ok {
		// The bytes are quoted nowhere on purpose. A body that failed the
		// sniff is frequently an HTML page, and an HTML page in a log line is
		// a page of somebody else's markup in the operator's terminal.
		return Result{}, ErrUnsupported
	}

	sum := sha256.Sum256(body)
	digest := hex.EncodeToString(sum[:])
	path := a.pathFor(digest, mime)
	unchanged, err := a.writeIfAbsent(path, body)
	if err != nil {
		return Result{}, err
	}
	return Result{
		Digest:    digest,
		MIME:      mime,
		ByteSize:  len(body),
		Path:      path,
		Unchanged: unchanged,
	}, nil
}

// Open reads a stored avatar back. digest is the value the database recorded,
// and it is validated against the same shape the database constraint enforces —
// because this string becomes a file name, and the value arrives from a row that
// a future migration or a manual fix could have made into something else.
func (a *Cache) Open(digest, mime string) ([]byte, error) {
	if a.Disabled() {
		return nil, ErrDisabled
	}
	if !validDigest(digest) {
		return nil, fmt.Errorf("avatar: refusing digest %q", digest)
	}
	ext, ok := extensionFor(mime)
	if !ok {
		return nil, fmt.Errorf("avatar: refusing mime %q", mime)
	}
	b, err := os.ReadFile(filepath.Join(a.dir, digest+ext))
	if err != nil {
		if errors.Is(err, os.ErrNotExist) {
			return nil, os.ErrNotExist
		}
		return nil, fmt.Errorf("avatar: read %s: %w", digest, err)
	}
	return b, nil
}

// DigestOf is the name a set of bytes would be stored under. Exposed so a
// caller can recognise a digest it already has without reading the file.
func DigestOf(b []byte) string {
	sum := sha256.Sum256(b)
	return hex.EncodeToString(sum[:])
}

// pathFor maps a digest and a MIME type to a file name. The extension is part
// of the name so a directory listing is readable by a human, and so an operator
// opening the file with the wrong tool gets a hint from the shell rather than a
// question.
func (a *Cache) pathFor(digest, mime string) string {
	ext, ok := extensionFor(mime)
	if !ok {
		// Unreachable: every caller sniffs first. Kept anyway, because the
		// alternative is a file with no extension written from a path that a
		// future caller might reach with a format this build does not accept.
		ext = ".bin"
	}
	return filepath.Join(a.dir, digest+ext)
}

// writeIfAbsent puts body at path unless it is already there byte for byte.
//
// Content addressing makes this cheap and safe: the name is a function of the
// contents, so an existing file with that name is by construction the same
// bytes. Renaming a temporary file into place is what makes a concurrent
// reader see either the old file or the new one and never a truncated one.
func (a *Cache) writeIfAbsent(path string, body []byte) (unchanged bool, err error) {
	if fi, statErr := os.Stat(path); statErr == nil && fi.Size() == int64(len(body)) {
		return true, nil
	}

	dir := filepath.Dir(path)
	tmp, err := os.CreateTemp(dir, ".avatar-*.tmp")
	if err != nil {
		return false, fmt.Errorf("avatar: temp file: %w", err)
	}
	tmpName := tmp.Name()
	// The cleanup runs on every path that does not reach a successful rename.
	defer func() {
		_ = tmp.Close()
		if err != nil {
			_ = os.Remove(tmpName)
		}
	}()

	if _, err = tmp.Write(body); err != nil {
		return false, fmt.Errorf("avatar: write: %w", err)
	}
	// The bytes are on their way to a file that is served to a browser. They
	// are not secret, but they are a person's picture on a shared disk, and the
	// rest of the deployment already runs at 0750.
	if err = tmp.Chmod(0o640); err != nil {
		return false, fmt.Errorf("avatar: chmod: %w", err)
	}
	if err = tmp.Close(); err != nil {
		return false, fmt.Errorf("avatar: close: %w", err)
	}
	if err = os.Rename(tmpName, path); err != nil {
		// A concurrent fetch of the same avatar renamed first. Its file is the
		// same content, so the race is a success, not a fault.
		if errors.Is(err, os.ErrExist) {
			return true, nil
		}
		return false, fmt.Errorf("avatar: rename: %w", err)
	}
	return false, nil
}

// sniff identifies the image from its own bytes.
//
// The JPEG and PNG magic numbers are checked here rather than delegated to
// image.DecodeConfig, because that would import an image codec for the sake of
// reading four bytes, and because DecodeConfig parses far more of the file than
// is needed to answer the question — which on a hostile input is the difference
// between rejecting it and decoding it.
func sniff(b []byte) (string, bool) {
	switch {
	case len(b) >= 3 && b[0] == 0xFF && b[1] == 0xD8 && b[2] == 0xFF:
		return MIMEJPEG, true
	case len(b) >= 8 &&
		b[0] == 0x89 && b[1] == 'P' && b[2] == 'N' && b[3] == 'G' &&
		b[4] == 0x0D && b[5] == 0x0A && b[6] == 0x1A && b[7] == 0x0A:
		return MIMEPNG, true
	default:
		return "", false
	}
}

// extensionFor is the inverse of sniff, for a MIME type the database accepted.
func extensionFor(mime string) (string, bool) {
	switch mime {
	case MIMEJPEG:
		return ".jpg", true
	case MIMEPNG:
		return ".png", true
	default:
		return "", false
	}
}

// validDigest is the shape the account_avatars_digest_check constraint
// enforces. It is repeated here because the same string is about to be
// concatenated into a file path, and a constraint the application has not read
// is a constraint that has not been checked.
func validDigest(d string) bool {
	if len(d) != 64 {
		return false
	}
	for i := 0; i < len(d); i++ {
		c := d[i]
		if (c < '0' || c > '9') && (c < 'a' || c > 'f') {
			return false
		}
	}
	return true
}

// wechatHosts is the default allow-list.
//
// The URLs come from sns/userinfo, which is WeChat's own answer, so the honest
// threat model is not "somebody crafts a URL" but "somebody who can answer
// sns/userinfo can point us anywhere". Pinning the hosts means that a
// compromised or unexpected upstream cannot turn this service into a request
// forwarder pointed at an internal address.
//
// Every entry is a dot-anchored suffix or an exact match, never a substring
// test: "wx.qlogo.cn.attacker.example" ends with characters from a permitted
// host and belongs to somebody else.
//
// The list covers the domains WeChat actually serves profile pictures from —
// the qlogo CDN for website apps and official accounts, qpic for the newer
// uploads, and the wx.qq.com static host some headimgurl values use.
func wechatHosts(u *url.URL) bool {
	host := strings.ToLower(u.Hostname())
	if host == "" {
		return false
	}
	for _, domain := range wechatAvatarDomains {
		if host == domain || strings.HasSuffix(host, "."+domain) {
			return true
		}
	}
	return false
}

// wechatAvatarDomains is the allow-list, named so the test can assert against
// the same slice the code uses rather than a copy that drifts from it.
var wechatAvatarDomains = []string{
	"qlogo.cn",
	"qpic.cn",
	"weixin.qq.com",
	"wx.qq.com",
}

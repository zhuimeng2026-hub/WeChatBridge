package auth

import (
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

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/avatar"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The avatar work has three shapes worth testing, and they live at different
// layers:
//
//   - The cache itself is tested in internal/avatar. What is left here is what
//     this package decides: when to fetch, when to skip, what to tell the
//     client, and what to answer when there is nothing to show.
//
//   - A sign-in must not become slower because of a picture, so every failure
//     in this file asserts the sign-in still completed. A test that only checked
//     the avatar would pass while the feature quietly logged people out.
//
//   - The TTL is the only thing standing between "cached once" and "refetched
//     on every sign-in", and it is invisible until somebody counts requests.

// A PNG-shaped blob. The service never decodes it, so this only has to be
// shaped enough for the sniff to accept.
var avatarPNG = append([]byte{0x89, 'P', 'N', 'G', 0x0D, 0x0A, 0x1A, 0x0A},
	[]byte{0x00, 0x00, 0x00, 0x0D, 'I', 'H', 'D', 'R'}...)

// avatarHarness is the standard harness plus a picture server and a cache that
// can reach it. The WeChat stub's own host is what Fetch would refuse, so the
// allow-list is opened here — this package's decision is about *when* to fetch,
// and internal/avatar already owns the decision about *what* may be fetched.
type avatarHarness struct {
	*harness

	cache     *avatar.Cache
	cacheDir  string
	served    []byte
	hits      int
	srv       *httptest.Server
	account   string
	sourceURL string
}

func newAvatarHarness(t *testing.T) *avatarHarness {
	t.Helper()

	ah := &avatarHarness{served: append([]byte(nil), avatarPNG...)}
	mux := http.NewServeMux()
	mux.HandleFunc("/pic", func(rw http.ResponseWriter, r *http.Request) {
		ah.hits++
		_, _ = rw.Write(ah.served)
	})
	ah.srv = httptest.NewTLSServer(mux)
	t.Cleanup(ah.srv.Close)

	ah.cacheDir = t.TempDir()
	cache, err := avatar.New(ah.cacheDir,
		avatar.WithHTTPClient(ah.srv.Client()),
		avatar.WithHostAllow(func(*url.URL) bool { return true }),
	)
	if err != nil {
		t.Fatalf("avatar.New: %v", err)
	}
	ah.cache = cache

	// Rebuild the service so it holds the cache. The stub's profile handler is
	// set afterwards by each test; the default answer is "here is a picture".
	base := newHarness(t)
	ah.harness = base
	base.svc.avatarCache = cache
	base.svc.cfg.AvatarRefreshTTL = time.Hour

	return ah
}

// signInWithPicture runs a scan where WeChat does hand over a profile, which is
// the only path that reaches the avatar code at all.
func (ah *avatarHarness) signInWithPicture(t *testing.T, installID string) {
	t.Helper()
	picURL := ah.srv.URL + "/pic"
	if ah.sourceURL != "" {
		picURL = ah.sourceURL
	}
	ah.wc.profileHandler = func(rw http.ResponseWriter, r *http.Request) {
		rw.Header().Set("Content-Type", "application/json")
		_, _ = rw.Write([]byte(`{"nickname":"小明",` +
			`"headimgurl":"` + picURL + `","openid":"oTESTOPENID"}`))
	}

	created, err := ah.svc.CreateSession(context.Background(), installID, "device-1")
	if err != nil {
		t.Fatalf("CreateSession: %v", err)
	}
	if _, err := ah.svc.HandleCallback(context.Background(), "auth-code", created.Ticket); err != nil {
		t.Fatalf("HandleCallback: %v", err)
	}

	poll, err := ah.svc.Poll(context.Background(), created.Ticket)
	if err != nil {
		t.Fatalf("Poll: %v", err)
	}
	if poll.AccessToken == "" {
		t.Fatal("the scan produced no access token; the sign-in itself failed")
	}
	if poll.Account == nil {
		t.Fatal("the scan produced no account")
	}
	ah.account = poll.Account.ID
}

// ---------------------------------------------------------------------------
// Fetching on sign-in
// ---------------------------------------------------------------------------

func TestASignInStoresThePictureItWasHanded(t *testing.T) {
	t.Parallel()

	ah := newAvatarHarness(t)
	ah.signInWithPicture(t, "install-1")

	if ah.hits != 1 {
		t.Errorf("the picture was fetched %d times, want 1", ah.hits)
	}
	rec, err := ah.store.GetAvatar(context.Background(), ah.account)
	if err != nil {
		t.Fatalf("the picture was not recorded: %v", err)
	}
	if rec.Digest != avatar.DigestOf(ah.served) {
		t.Errorf("digest = %q, want %q", rec.Digest, avatar.DigestOf(ah.served))
	}
	if rec.MIME != avatar.MIMEPNG {
		t.Errorf("mime = %q, want %q", rec.MIME, avatar.MIMEPNG)
	}
	if rec.SourceURL == "" {
		t.Error("the source URL was not kept; a later refresh has nowhere to retry from")
	}
	if rec.FetchedAt.IsZero() {
		t.Error("FetchedAt is zero, so the TTL can never expire this copy")
	}
}

func TestTheClientIsGivenThisServicesAddressNotWechats(t *testing.T) {
	t.Parallel()

	ah := newAvatarHarness(t)
	ah.signInWithPicture(t, "install-1")

	dto := ah.svc.accountDTO(context.Background(), ah.mustAccount(t))
	want := testPublicBase + AvatarPath(ah.account) + "?v=" + avatar.DigestOf(ah.served)
	if dto.AvatarURL != want {
		t.Errorf("avatarUrl = %q, want %q", dto.AvatarURL, want)
	}
	// The whole point: the address must not be WeChat's, because that one
	// stops working and the client keeps whatever URL it is given.
	if strings.Contains(dto.AvatarURL, "qlogo") || strings.Contains(dto.AvatarURL, "127.0.0.1") {
		t.Errorf("avatarUrl = %q still points off this service", dto.AvatarURL)
	}
}

func TestTheTTLStopsASecondSignInFromRefetching(t *testing.T) {
	t.Parallel()

	ah := newAvatarHarness(t)
	ah.signInWithPicture(t, "install-1")
	if ah.hits != 1 {
		t.Fatalf("setup fetched %d times, want 1", ah.hits)
	}

	// A second sign-in within the TTL: WeChat's URL is a new one every time, so
	// only the stored fetched_at stops this from being a request per sign-in.
	ah.signInWithPicture(t, "install-2")
	if ah.hits != 1 {
		t.Errorf("a second sign-in inside the TTL fetched again (%d hits); "+
			"sign-in must not cost a CDN request per scan", ah.hits)
	}
}

func TestAPictureOlderThanTheTTLIsReplaced(t *testing.T) {
	t.Parallel()

	ah := newAvatarHarness(t)
	ah.signInWithPicture(t, "install-1")
	first := avatar.DigestOf(ah.served)

	// The person changed their picture and enough time passed.
	ah.served = append(append([]byte(nil), avatarPNG...), 0x7F)
	ah.clock = ah.clock.Add(2 * time.Hour)
	ah.signInWithPicture(t, "install-2")

	if ah.hits != 2 {
		t.Fatalf("a stale picture was not refetched (%d hits)", ah.hits)
	}
	rec, err := ah.store.GetAvatar(context.Background(), ah.account)
	if err != nil {
		t.Fatal(err)
	}
	if rec.Digest == first {
		t.Error("the record still points at the old picture")
	}
}

func TestReplacingTheWechatAvatarUpdatesTheLocalCacheImmediately(t *testing.T) {
	t.Parallel()
	ah := newAvatarHarness(t)
	ah.signInWithPicture(t, "install-1")
	firstURL := ah.svc.accountDTO(t.Context(), ah.mustAccount(t)).AvatarURL
	ah.served = append(append([]byte(nil), avatarPNG...), 0x42)
	ah.sourceURL = ah.srv.URL + "/pic?revision=2"
	// No clock advance: a changed source must bypass the usual refresh TTL.
	ah.signInWithPicture(t, "install-2")
	updatedURL := ah.svc.accountDTO(t.Context(), ah.mustAccount(t)).AvatarURL
	if ah.hits != 2 || updatedURL == firstURL {
		t.Fatal("a changed WeChat avatar did not replace the cached image and URL")
	}
	ah.signInWithPicture(t, "install-3")
	if ah.hits != 2 || ah.svc.accountDTO(t.Context(), ah.mustAccount(t)).AvatarURL != updatedURL {
		t.Fatal("an unchanged avatar should reuse its cached bytes and version")
	}
}

func TestAvatarCachingUsesTheContentVersionAndRevalidatesLegacyURLs(t *testing.T) {
	t.Parallel()
	ah := newAvatarHarness(t)
	ah.signInWithPicture(t, "install-1")
	engine := gin.New()
	ah.svc.RegisterRoutes(engine)
	path := AvatarPath(ah.account)
	digest := avatar.DigestOf(ah.served)
	for _, tc := range []struct {
		suffix, cache string
		status        int
	}{
		{"?v=" + digest, "public, max-age=31536000, immutable", http.StatusOK},
		{"", "no-cache", http.StatusOK},
		{"?v=previous-avatar", "no-store", http.StatusGone},
	} {
		rec, _ := doJSON(t, engine, http.MethodGet, path+tc.suffix, nil, nil)
		if rec.Code != tc.status || rec.Header().Get("Cache-Control") != tc.cache {
			t.Errorf("%s: status=%d cache=%q", tc.suffix, rec.Code, rec.Header().Get("Cache-Control"))
		}
	}
	rec, _ := doJSON(t, engine, http.MethodGet, path, nil, map[string]string{"If-None-Match": `"` + digest + `"`})
	if rec.Code != http.StatusNotModified || rec.Body.Len() != 0 {
		t.Fatal("an unchanged avatar must reuse the browser's local bytes")
	}
}

// ---------------------------------------------------------------------------
// Failures that must not cost anybody their sign-in
// ---------------------------------------------------------------------------

func TestAPictureThatCannotBeFetchedStillLeavesTheSignInIntact(t *testing.T) {
	t.Parallel()

	ah := newAvatarHarness(t)
	// The stub answers the picture request with something that is not a picture.
	ah.srv.Config.Handler = http.HandlerFunc(func(rw http.ResponseWriter, r *http.Request) {
		_, _ = rw.Write([]byte("<html>not an avatar</html>"))
	})
	ah.signInWithPicture(t, "install-1")

	// No record, and the client is told there is no picture rather than being
	// pointed at a URL that will not resolve.
	if _, err := ah.store.GetAvatar(context.Background(), ah.account); !errors.Is(err, store.ErrNotFound) {
		t.Errorf("err = %v, want ErrNotFound", err)
	}
	if dto := ah.svc.accountDTO(context.Background(), ah.mustAccount(t)); dto.AvatarURL != "" {
		t.Errorf("avatarUrl = %q, want empty when there is no picture", dto.AvatarURL)
	}
}

func TestADeploymentWithoutAnAvatarDirectoryIsUnchanged(t *testing.T) {
	t.Parallel()

	// The default harness: no cache at all. This is the deployment that has not
	// set WCB_AVATAR_DIR, and it must behave exactly as it did before.
	h := newHarness(t)
	_, poll := h.signIn(t, "install-1")
	if poll.Account == nil || poll.Account.AvatarURL != "" {
		t.Fatalf("expected no avatar URL, got %+v", poll.Account)
	}
	if _, err := h.store.GetAvatar(context.Background(), poll.Account.ID); !errors.Is(err, store.ErrNotFound) {
		t.Errorf("err = %v, want ErrNotFound", err)
	}
}

// ---------------------------------------------------------------------------
// The endpoint
// ---------------------------------------------------------------------------

func TestAvatarServesTheStoredBytesAndAnswersNoContentWhenThereAreNone(t *testing.T) {
	t.Parallel()

	ah := newAvatarHarness(t)
	ah.signInWithPicture(t, "install-1")

	rec, body, ok, err := ah.svc.Avatar(context.Background(), ah.account)
	if err != nil || !ok {
		t.Fatalf("Avatar: ok=%v err=%v", ok, err)
	}
	if rec.MIME != avatar.MIMEPNG {
		t.Errorf("mime = %q", rec.MIME)
	}
	if string(body) != string(ah.served) {
		t.Error("the served bytes are not the bytes the fetch stored")
	}

	// An account nobody ever fetched a picture for.
	_, _, ok, err = ah.svc.Avatar(context.Background(), "no-such-account")
	if err != nil {
		t.Errorf("an unknown account must not be an error: %v", err)
	}
	if ok {
		t.Error("an unknown account reported a picture")
	}
}

func TestARecordedButMissingFileIsBlankRatherThanAFault(t *testing.T) {
	t.Parallel()

	ah := newAvatarHarness(t)
	ah.signInWithPicture(t, "install-1")

	rec, err := ah.store.GetAvatar(context.Background(), ah.account)
	if err != nil {
		t.Fatal(err)
	}
	// The row outlives its file whenever an operator sweeps the directory.
	// Delete the bytes behind the record, exactly as a sweep would.
	if err := os.Remove(filepath.Join(ah.cacheDir, rec.Digest+".png")); err != nil {
		t.Fatalf("remove the cached file: %v", err)
	}

	_, _, ok, err := ah.svc.Avatar(context.Background(), ah.account)
	if err != nil {
		t.Errorf("a swept file must read as no picture, got %v", err)
	}
	if ok {
		t.Error("a swept file still reported a picture")
	}
}

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

func (ah *avatarHarness) mustAccount(t *testing.T) store.Account {
	t.Helper()
	if ah.account == "" {
		t.Fatal("no account: call signInWithPicture first")
	}
	a, err := ah.store.GetAccount(context.Background(), ah.account)
	if err != nil {
		t.Fatalf("GetAccount: %v", err)
	}
	return a
}

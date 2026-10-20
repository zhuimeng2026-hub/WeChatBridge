package auth

import (
	"context"
	"errors"
	"net/http"
	"strings"
	"sync"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

func TestQRRequiresTheOfficialBrowserFlow(t *testing.T) {
	t.Parallel()
	_, engine := newTestServer(t)
	created := createTicket(t, engine, "install-1")
	rec, body := doJSON(t, engine, http.MethodGet, "/v1/auth/session/"+created["ticket"].(string)+"/qr", nil, nil)
	if rec.Code != http.StatusConflict {
		t.Fatalf("QR status = %d; a standalone image cannot complete the browser's OAuth redirect", rec.Code)
	}
	errBody, _ := body["error"].(map[string]any)
	message, _ := errBody["message"].(string)
	if errBody["code"] != "conflict" || !strings.Contains(message, "authorizeUrl") {
		t.Fatalf("expected browser authorization guidance, got %v", body)
	}
}

func TestQRRejectsExpiredAndCompletedSessions(t *testing.T) {
	t.Parallel()
	for _, state := range []store.AuthSessionState{store.AuthExpired, store.AuthAuthorized, store.AuthRejected} {
		t.Run(string(state), func(t *testing.T) {
			h, engine := newTestServer(t)
			created := createTicket(t, engine, "install-1")
			ticket := created["ticket"].(string)
			h.store.forceAuthSessionState(ticket, state)
			rec, _ := doJSON(t, engine, http.MethodGet, "/v1/auth/session/"+ticket+"/qr", nil, nil)
			if rec.Code != http.StatusGone {
				t.Fatalf("status = %d, want 410", rec.Code)
			}
		})
	}
	t.Run("pending past deadline", func(t *testing.T) {
		h, engine := newTestServer(t)
		created := createTicket(t, engine, "install-1")
		h.clock = h.clock.Add(testSessionTTL + time.Second)
		rec, _ := doJSON(t, engine, http.MethodGet, "/v1/auth/session/"+created["ticket"].(string)+"/qr", nil, nil)
		if rec.Code != http.StatusGone {
			t.Fatalf("status = %d, want 410", rec.Code)
		}
	})
}

func TestAuthResponsesAreNotCachedOrUsedAsReferrers(t *testing.T) {
	t.Parallel()
	_, engine := newTestServer(t)
	rec, created := doJSON(t, engine, http.MethodPost, "/v1/auth/session", map[string]any{"installId": "install-1"}, nil)
	check := func(name string, recCache, recReferrer string) {
		t.Helper()
		if recCache != "no-store" || recReferrer != "no-referrer" {
			t.Errorf("%s: Cache-Control=%q, Referrer-Policy=%q", name, recCache, recReferrer)
		}
	}
	check("create", rec.Header().Get("Cache-Control"), rec.Header().Get("Referrer-Policy"))
	ticket := created["ticket"].(string)
	callback, _ := doJSON(t, engine, http.MethodGet, CallbackPath+"?code=auth-code&state="+ticket, nil, nil)
	check("callback", callback.Header().Get("Cache-Control"), callback.Header().Get("Referrer-Policy"))
	polled, tokens := doJSON(t, engine, http.MethodGet, "/v1/auth/session/"+ticket, nil, nil)
	check("poll", polled.Header().Get("Cache-Control"), polled.Header().Get("Referrer-Policy"))
	if tokens["accessToken"] == nil {
		t.Fatal("poll did not deliver credentials")
	}
	account, _ := doJSON(t, engine, http.MethodGet, "/v1/account", nil, map[string]string{"Authorization": "Bearer " + tokens["accessToken"].(string)})
	check("account", account.Header().Get("Cache-Control"), account.Header().Get("Referrer-Policy"))
	refreshed, _ := doJSON(t, engine, http.MethodPost, "/v1/auth/refresh", map[string]any{"refreshToken": tokens["refreshToken"]}, nil)
	check("refresh", refreshed.Header().Get("Cache-Control"), refreshed.Header().Get("Referrer-Policy"))
}

// A slow INSERT exposes the race between reserving a slot and persisting it:
// another request must not evict the reservation as a nonexistent ticket.
type blockedTicketStore struct {
	store.AuthStore
	once    sync.Once
	started chan struct{}
	release chan struct{}
}

func (s *blockedTicketStore) CreateAuthSession(ctx context.Context, sess store.AuthSession) error {
	if sess.InstallID == "slow-install" {
		s.once.Do(func() { close(s.started); <-s.release })
	}
	return s.AuthStore.CreateAuthSession(ctx, sess)
}

func TestConcurrentCreationKeepsTheTicketBudgetThroughTheInsert(t *testing.T) {
	t.Parallel()
	h := newHarness(t)
	h.svc.cfg.MaxOutstandingTickets = 1
	blocked := &blockedTicketStore{AuthStore: h.store, started: make(chan struct{}), release: make(chan struct{})}
	h.svc.store = blocked
	var release sync.Once
	unblock := func() { release.Do(func() { close(blocked.release) }) }
	defer unblock()
	first := make(chan error, 1)
	go func() { _, err := h.svc.CreateSession(t.Context(), "slow-install", "device"); first <- err }()
	<-blocked.started
	second := make(chan error, 1)
	go func() { _, err := h.svc.CreateSession(t.Context(), "slow-install", "device"); second <- err }()
	other := make(chan error, 1)
	go func() { _, err := h.svc.CreateSession(t.Context(), "other-install", "device"); other <- err }()
	select {
	case err := <-other:
		if err != nil {
			t.Fatal(err)
		}
	case <-time.After(time.Second):
		t.Fatal("a slow install blocked unrelated installs")
	}
	select {
	case err := <-second:
		t.Errorf("second creation overtook an unpersisted reservation: %v", err)
		unblock()
		if err := <-first; err != nil {
			t.Fatal(err)
		}
	case <-time.After(100 * time.Millisecond):
		unblock()
		if err := <-first; err != nil {
			t.Fatal(err)
		}
		if err := <-second; err != nil {
			t.Fatal(err)
		}
	}
	h.store.mu.Lock()
	defer h.store.mu.Unlock()
	live := 0
	for _, sess := range h.store.authSessions {
		if sess.InstallID == "slow-install" {
			live++
		}
	}
	if live != 1 {
		t.Errorf("live tickets = %d, budget = 1", live)
	}
}

func TestConsumedLoginsReleaseTheirTicketBudget(t *testing.T) {
	t.Parallel()
	h := newHarness(t)
	for n := 0; n < 5; n++ {
		h.signIn(t, "install-1")
	}
	if _, err := h.svc.CreateSession(t.Context(), "install-1", "device"); err != nil {
		t.Fatalf("completed sign-ins blocked a new login: %v", err)
	}
}

func TestWaitingTicketCreationCanBeCancelled(t *testing.T) {
	t.Parallel()
	h := newHarness(t)
	blocked := &blockedTicketStore{AuthStore: h.store, started: make(chan struct{}), release: make(chan struct{})}
	h.svc.store = blocked
	var release sync.Once
	unblock := func() { release.Do(func() { close(blocked.release) }) }
	defer unblock()
	first := make(chan error, 1)
	go func() { _, err := h.svc.CreateSession(t.Context(), "slow-install", "device"); first <- err }()
	<-blocked.started
	ctx, cancel := context.WithCancel(t.Context())
	defer cancel()
	waiting := make(chan error, 1)
	started := make(chan struct{})
	go func() { close(started); _, err := h.svc.CreateSession(ctx, "slow-install", "device"); waiting <- err }()
	<-started
	cancel()
	select {
	case err := <-waiting:
		if !errors.Is(err, context.Canceled) {
			t.Fatalf("cancelled creation = %v", err)
		}
	case <-time.After(time.Second):
		t.Fatal("cancelled request kept waiting for another install request's I/O")
	}
	unblock()
	if err := <-first; err != nil {
		t.Fatal(err)
	}
	if _, err := h.svc.CreateSession(t.Context(), "slow-install", "device"); err != nil {
		t.Fatal(err)
	}
}

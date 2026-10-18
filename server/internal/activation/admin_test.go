package activation

import (
	"context"
	"crypto/ecdsa"
	"crypto/x509"
	"encoding/base64"
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/license"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// testSigner is the issuing key the admin half signs with. The customer
// endpoints never touch it; the admin ones cannot work without it.
func testSigner(t *testing.T) *license.Signer {
	t.Helper()
	key, err := license.GenerateKey()
	if err != nil {
		t.Fatalf("GenerateKey: %v", err)
	}
	signer, err := license.NewSigner(key)
	if err != nil {
		t.Fatalf("NewSigner: %v", err)
	}
	return signer
}

// testAccountByID answers with one account per id — the guard reads its
// openid — and a miss for anything else, the way a deleted account would
// answer.
func testAccountByID(accountID string) func(context.Context, store.ID) (store.Account, error) {
	return func(_ context.Context, id store.ID) (store.Account, error) {
		if string(id) != accountID {
			return store.Account{}, store.ErrNotFound
		}
		return store.Account{
			ID:       id,
			OpenID:   "openid-" + accountID,
			Nickname: "用户" + accountID,
		}, nil
	}
}

// newAdminEngine mounts only the admin half, behind a bearer middleware that
// stands in for auth.RequireAuth, with the given account signed in and the
// given allowlist in force.
func newAdminEngine(t *testing.T, st store.ActivationStore, accountID string, adminOpenIDs []string) *gin.Engine {
	t.Helper()
	engine, _ := newAdminEngineKeyed(t, st, accountID, adminOpenIDs)
	return engine
}

// newAdminEngineKeyed is newAdminEngine plus the signer, for the test that
// verifies an issued licence with the client's own verifier — the real judge
// of whether this package and the C# side agree.
func newAdminEngineKeyed(t *testing.T, st store.ActivationStore, accountID string, adminOpenIDs []string) (*gin.Engine, *license.Signer) {
	t.Helper()
	signer := testSigner(t)
	svc, err := NewService(Options{
		Store:        st,
		AccountOf:    testAccountReader(accountID),
		Signer:       signer,
		AccountByID:  testAccountByID(accountID),
		AdminOpenIDs: adminOpenIDs,
	})
	if err != nil {
		t.Fatalf("NewService: %v", err)
	}
	engine := gin.New()
	svc.RegisterAdminRoutes(engine, testAuth(accountID))
	return engine, signer
}

// signerPublicKey reads the SPKI back out of the signer — the same base64
// string the client embeds as a compile-time constant.
func signerPublicKey(t *testing.T, signer *license.Signer) *ecdsa.PublicKey {
	t.Helper()
	b64, err := signer.PublicBase64()
	if err != nil {
		t.Fatalf("PublicBase64: %v", err)
	}
	der, err := base64.StdEncoding.DecodeString(b64)
	if err != nil {
		t.Fatalf("public key is not base64: %v", err)
	}
	pub, err := x509.ParsePKIXPublicKey(der)
	if err != nil {
		t.Fatalf("ParsePKIXPublicKey: %v", err)
	}
	key, ok := pub.(*ecdsa.PublicKey)
	if !ok {
		t.Fatalf("public key is %T, want *ecdsa.PublicKey", pub)
	}
	return key
}

func ptr64(v int64) *int64 { return &v }

func seedPending(st *fakeActivationStore, accountID, installID string, amountFen *int64) store.ActivationRequest {
	return st.seed(store.ActivationRequest{
		RequestID:    "act_" + installID,
		InstallID:    installID,
		AccountID:    accountID,
		AnchorDigest: "anchor-" + installID,
		AmountFen:    amountFen,
		Status:       store.ActivationPending,
	})
}

// seedConfirmed installs a confirmed request and its live binding, the state
// a reissue or a revoke starts from.
func seedConfirmed(st *fakeActivationStore, accountID, installID, licenseID string) store.ActivationRequest {
	now := time.Now().UTC()
	envelope := license.Envelope{
		Version:   license.CurrentVersion,
		Subject:   installID,
		Plan:      license.PlanBuyout,
		IssuedAt:  now.Unix(),
		NotBefore: ptr64(now.Unix()),
	}
	raw, err := envelope.JSON()
	if err != nil {
		panic(err)
	}
	r := st.seed(store.ActivationRequest{
		RequestID:    "act_" + installID,
		InstallID:    installID,
		AccountID:    accountID,
		AmountFen:    ptr64(29800),
		Status:       store.ActivationConfirmed,
		LicenseID:    licenseID,
		EnvelopeJSON: raw,
		ConfirmedAt:  &now,
	})
	st.seedBinding(store.LicenceBinding{
		LicenseID: licenseID,
		InstallID: installID,
		AccountID: accountID,
		RequestID: r.RequestID,
	})
	return r
}

// ---------------------------------------------------------------------------
// The guard: D5 says the console has no password of its own.
// ---------------------------------------------------------------------------

func TestAdminGuard(t *testing.T) {
	st := newFakeStore()
	seedPending(st, "acct-admin", "install-1", ptr64(29800))

	t.Run("an empty allowlist is a server misconfiguration, not a permission", func(t *testing.T) {
		engine := newAdminEngine(t, st, "acct-admin", nil)
		rec, _ := doJSON(t, engine, http.MethodGet, "/v1/admin/requests", nil)
		if rec.Code != http.StatusServiceUnavailable {
			t.Fatalf("status = %d, want 503", rec.Code)
		}
	})

	t.Run("a signed-in buyer is not an administrator", func(t *testing.T) {
		engine := newAdminEngine(t, st, "acct-buyer", []string{"openid-acct-admin"})
		rec, _ := doJSON(t, engine, http.MethodGet, "/v1/admin/requests", nil)
		if rec.Code != http.StatusForbidden {
			t.Fatalf("status = %d, want 403", rec.Code)
		}
	})

	t.Run("the allowlisted account passes", func(t *testing.T) {
		engine := newAdminEngine(t, st, "acct-admin", []string{"openid-acct-admin"})
		rec, out := doJSON(t, engine, http.MethodGet, "/v1/admin/requests", nil)
		if rec.Code != http.StatusOK {
			t.Fatalf("status = %d, want 200", rec.Code)
		}
		if _, ok := out["items"]; !ok {
			t.Fatalf("response has no items: %s", rec.Body.String())
		}
	})
}

// ---------------------------------------------------------------------------
// The queue.
// ---------------------------------------------------------------------------

func TestAdminList(t *testing.T) {
	st := newFakeStore()
	seedPending(st, "acct-admin", "install-1", ptr64(29800))
	seedPending(st, "acct-admin", "install-2", nil) // a buyer who never stated an amount
	seedConfirmed(st, "acct-admin", "install-3", "lic_old")

	engine := newAdminEngine(t, st, "acct-admin", []string{"openid-acct-admin"})

	t.Run("default view is the whole queue, newest first", func(t *testing.T) {
		rec, out := doJSON(t, engine, http.MethodGet, "/v1/admin/requests", nil)
		if rec.Code != http.StatusOK {
			t.Fatalf("status = %d, want 200", rec.Code)
		}
		items := out["items"].([]any)
		if len(items) != 3 {
			t.Fatalf("len(items) = %d, want 3", len(items))
		}
		first := items[0].(map[string]any)
		if first["requestId"] != "act_install-3" {
			t.Fatalf("first requestId = %v, want the newest act_install-3", first["requestId"])
		}
		// The nickname comes from the store's accounts join — the fake
		// stubs it as "Nick"; the point is that it arrived at all.
		if first["nickname"] != "Nick" {
			t.Fatalf("nickname = %v, want the join's Nick", first["nickname"])
		}
		if first["licenseId"] != "lic_old" {
			t.Fatalf("licenseId = %v, want lic_old", first["licenseId"])
		}
		createdAt, ok := first["createdAt"].(string)
		if !ok {
			t.Fatalf("createdAt is not a string: %v", first["createdAt"])
		}
		// The console parses this with a plain Date; UTC RFC 3339 without
		// fractional seconds is what the mock's stamp() emits.
		if _, err := time.Parse(time.RFC3339, createdAt); err != nil {
			t.Fatalf("createdAt %q does not parse as RFC 3339: %v", createdAt, err)
		}
		if !strings.HasSuffix(createdAt, "Z") {
			t.Fatalf("createdAt %q is not UTC", createdAt)
		}
	})

	t.Run("an unstated amount is null, not zero", func(t *testing.T) {
		_, out := doJSON(t, engine, http.MethodGet, "/v1/admin/requests?status=pending", nil)
		items := out["items"].([]any)
		if len(items) != 2 {
			t.Fatalf("pending items = %d, want 2", len(items))
		}
		// Newest first: install-2 is the row with no amount.
		first := items[0].(map[string]any)
		if v, ok := first["amountFen"]; !ok || v != nil {
			t.Fatalf("amountFen = %v (%T), want JSON null", v, v)
		}
	})

	t.Run("status filters", func(t *testing.T) {
		_, out := doJSON(t, engine, http.MethodGet, "/v1/admin/requests?status=confirmed", nil)
		items := out["items"].([]any)
		if len(items) != 1 {
			t.Fatalf("confirmed items = %d, want 1", len(items))
		}
	})

	t.Run("a garbage filter is a 400, not an empty list", func(t *testing.T) {
		rec, _ := doJSON(t, engine, http.MethodGet, "/v1/admin/requests?status=paid", nil)
		if rec.Code != http.StatusBadRequest {
			t.Fatalf("status = %d, want 400", rec.Code)
		}
	})
}

// ---------------------------------------------------------------------------
// Confirm — the money moment.
// ---------------------------------------------------------------------------

func TestAdminConfirm(t *testing.T) {
	t.Run("signs a perpetual full-entitlement licence and installs the binding", func(t *testing.T) {
		st := newFakeStore()
		seedPending(st, "acct-admin", "install-1", ptr64(29800))
		engine, signer := newAdminEngineKeyed(t, st, "acct-admin", []string{"openid-acct-admin"})

		rec, out := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm",
			map[string]string{"requestId": "act_install-1"})
		if rec.Code != http.StatusOK {
			t.Fatalf("status = %d, want 200: %s", rec.Code, rec.Body.String())
		}
		licenseID, ok := out["licenseId"].(string)
		if !ok || !strings.HasPrefix(licenseID, "lic_") {
			t.Fatalf("licenseId = %v, want a lic_ id", out["licenseId"])
		}

		// The binding is live and points back at the request.
		b, err := st.LiveBindingByInstallID(context.Background(), "install-1")
		if err != nil {
			t.Fatalf("LiveBindingByInstallID: %v", err)
		}
		if b.LicenseID != licenseID || b.RequestID != "act_install-1" {
			t.Fatalf("binding = %+v, want %s linked to act_install-1", b, licenseID)
		}

		// The row the buyer's poll will read.
		r, err := st.GetActivationRequest(context.Background(), "act_install-1")
		if err != nil {
			t.Fatalf("GetActivationRequest: %v", err)
		}
		if r.Status != store.ActivationConfirmed || r.LicenseID != licenseID {
			t.Fatalf("row = %+v, want confirmed with %s", r, licenseID)
		}
		envelope, err := license.Parse(r.EnvelopeJSON)
		if err != nil {
			t.Fatalf("stored envelope does not parse: %v", err)
		}
		if envelope.Subject != "install-1" {
			t.Fatalf("subject = %q, want install-1", envelope.Subject)
		}
		if envelope.Plan != license.PlanBuyout {
			t.Fatalf("plan = %q, want buyout", envelope.Plan)
		}
		if envelope.ExpiresAt != nil {
			t.Fatalf("expiresAt = %v, want nil — a buy-out licence has no expiry", *envelope.ExpiresAt)
		}
		for _, bit := range buyoutEntitlements() {
			if !license.HasEntitlement(envelope, bit) {
				t.Fatalf("licence is missing entitlement %s", bit)
			}
		}
		// The client's own verifier is the real judge: a licence this
		// package signs must pass the offline check the customer runs,
		// against the SPKI the client would embed.
		if err := license.Verify(envelope, signerPublicKey(t, signer), "install-1",
			time.Now().UTC(), 48*time.Hour); err != nil {
			t.Fatalf("Verify: %v", err)
		}
	})

	t.Run("confirming twice issues once", func(t *testing.T) {
		st := newFakeStore()
		seedPending(st, "acct-admin", "install-1", ptr64(29800))
		engine := newAdminEngine(t, st, "acct-admin", []string{"openid-acct-admin"})

		_, first := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm",
			map[string]string{"requestId": "act_install-1"})
		rec, second := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm",
			map[string]string{"requestId": "act_install-1"})
		if rec.Code != http.StatusOK {
			t.Fatalf("second confirm status = %d, want 200", rec.Code)
		}
		if first["licenseId"] != second["licenseId"] {
			t.Fatalf("second confirm issued a different licence: %v vs %v",
				first["licenseId"], second["licenseId"])
		}
	})

	t.Run("a machine already holding a live licence is a conflict", func(t *testing.T) {
		st := &strictConfirmStore{}
		// Somebody else's licence is live on this machine, and this
		// account's registration is still pending. The real schema's
		// partial unique index refuses the second binding; the plain fake
		// does not model it, so this wrapper stands in for the index.
		st.seedBinding(store.LicenceBinding{
			LicenseID: "lic_somebody_else", InstallID: "install-1", AccountID: "acct-other",
		})
		seedPending(&st.fakeActivationStore, "acct-admin", "install-1", ptr64(29800))
		engine := newAdminEngine(t, st, "acct-admin", []string{"openid-acct-admin"})

		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm",
			map[string]string{"requestId": "act_install-1"})
		if rec.Code != http.StatusConflict {
			t.Fatalf("status = %d, want 409 — the machine is already licensed", rec.Code)
		}
	})

	t.Run("an unknown 单号 is a 404", func(t *testing.T) {
		engine := newAdminEngine(t, newFakeStore(), "acct-admin", []string{"openid-acct-admin"})
		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm",
			map[string]string{"requestId": "act_nope"})
		if rec.Code != http.StatusNotFound {
			t.Fatalf("status = %d, want 404", rec.Code)
		}
	})

	t.Run("a rejected row is not confirmed by a second click", func(t *testing.T) {
		st := newFakeStore()
		st.seed(store.ActivationRequest{
			RequestID: "act_rejected", AccountID: "acct-admin", InstallID: "install-1",
			Status: store.ActivationRejected,
		})
		engine := newAdminEngine(t, st, "acct-admin", []string{"openid-acct-admin"})

		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm",
			map[string]string{"requestId": "act_rejected"})
		if rec.Code != http.StatusConflict {
			t.Fatalf("status = %d, want 409", rec.Code)
		}
	})

	t.Run("a missing 单号 is a 400, not a 404 the seller misreads", func(t *testing.T) {
		engine := newAdminEngine(t, newFakeStore(), "acct-admin", []string{"openid-acct-admin"})
		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm", map[string]string{})
		if rec.Code != http.StatusBadRequest {
			t.Fatalf("status = %d, want 400", rec.Code)
		}
	})

	t.Run("losing the race to another seller still answers one payment", func(t *testing.T) {
		racing := &racingStore{}
		racing.seed(store.ActivationRequest{
			RequestID: "act_install-1", InstallID: "install-1",
			AccountID: "acct-admin", Status: store.ActivationPending,
		})
		engine := newAdminEngine(t, racing, "acct-admin", []string{"openid-acct-admin"})

		rec, out := doJSON(t, engine, http.MethodPost, "/v1/admin/confirm",
			map[string]string{"requestId": "act_install-1"})
		if rec.Code != http.StatusOK {
			t.Fatalf("status = %d, want 200", rec.Code)
		}
		if out["licenseId"] != "lic_winner" {
			t.Fatalf("licenseId = %v, want the winner's lic_winner", out["licenseId"])
		}
	})
}

// strictConfirmStore stands in for the partial unique indexes the plain fake
// does not model: a second live binding for the same machine, or for the same
// account, is a conflict at confirm time.
type strictConfirmStore struct {
	fakeActivationStore
}

func (s *strictConfirmStore) ConfirmActivationRequest(ctx context.Context, requestID, licenseID string, envelopeJSON []byte, at time.Time) (store.ActivationRequest, error) {
	r, err := s.GetActivationRequest(ctx, requestID)
	if err != nil {
		return store.ActivationRequest{}, err
	}
	if _, err := s.LiveBindingByInstallID(ctx, r.InstallID); err == nil {
		return store.ActivationRequest{}, store.ErrConflict
	}
	bindings, err := s.ListBindings(ctx)
	if err != nil {
		return store.ActivationRequest{}, err
	}
	for _, b := range bindings {
		if b.AccountID == r.AccountID && b.RevokedAt == nil {
			return store.ActivationRequest{}, store.ErrConflict
		}
	}
	return s.fakeActivationStore.ConfirmActivationRequest(ctx, requestID, licenseID, envelopeJSON, at)
}

// racingStore models two sellers clicking confirm on one row: the other
// seller's transaction lands first, this one reports a conflict, and the
// handler must re-read rather than guess.
type racingStore struct {
	fakeActivationStore
}

func (r *racingStore) ConfirmActivationRequest(_ context.Context, requestID, _ string, _ []byte, at time.Time) (store.ActivationRequest, error) {
	r.mu.Lock()
	defer r.mu.Unlock()
	for i := range r.requests {
		row := &r.requests[i]
		if row.RequestID != requestID {
			continue
		}
		now := at.UTC()
		row.Status = store.ActivationConfirmed
		row.LicenseID = "lic_winner"
		row.ConfirmedAt = &now
		r.seq++
		r.bindings = append(r.bindings, fakeBinding{LicenceBinding: store.LicenceBinding{
			LicenseID: "lic_winner",
			InstallID: row.InstallID,
			AccountID: row.AccountID,
			RequestID: requestID,
			IssuedAt:  now,
		}, seq: r.seq})
		return row.ActivationRequest, store.ErrConflict
	}
	return store.ActivationRequest{}, store.ErrNotFound
}

// ---------------------------------------------------------------------------
// Reissue — a re-sign, not a new purchase.
// ---------------------------------------------------------------------------

func TestAdminReissue(t *testing.T) {
	t.Run("retires the old binding and installs the new one", func(t *testing.T) {
		st := newFakeStore()
		seedConfirmed(st, "acct-admin", "install-1", "lic_old")
		engine := newAdminEngine(t, st, "acct-admin", []string{"openid-acct-admin"})

		rec, out := doJSON(t, engine, http.MethodPost, "/v1/admin/reissue",
			map[string]string{"installId": "install-1"})
		if rec.Code != http.StatusOK {
			t.Fatalf("status = %d, want 200: %s", rec.Code, rec.Body.String())
		}
		newID, ok := out["licenseId"].(string)
		if !ok || newID == "lic_old" {
			t.Fatalf("licenseId = %v, want a fresh id", out["licenseId"])
		}

		live, err := st.LiveBindingByInstallID(context.Background(), "install-1")
		if err != nil {
			t.Fatalf("LiveBindingByInstallID: %v", err)
		}
		if live.LicenseID != newID {
			t.Fatalf("live binding = %s, want %s", live.LicenseID, newID)
		}
		bindings, err := st.ListBindings(context.Background())
		if err != nil {
			t.Fatalf("ListBindings: %v", err)
		}
		if len(bindings) != 2 {
			t.Fatalf("len(bindings) = %d, want 2 (old retired, new live)", len(bindings))
		}
		for _, b := range bindings {
			if b.LicenseID == "lic_old" && b.RevokedAt == nil {
				t.Fatal("the old binding is still live after a reissue")
			}
		}
	})

	t.Run("an outstanding registration is confirmed with the new licence", func(t *testing.T) {
		st := newFakeStore()
		// The buyer re-registered on this machine (lost the file); the
		// seller reissues, and the poller learns about it.
		seedPending(st, "acct-admin", "install-1", ptr64(29800))
		engine := newAdminEngine(t, st, "acct-admin", []string{"openid-acct-admin"})

		rec, out := doJSON(t, engine, http.MethodPost, "/v1/admin/reissue",
			map[string]string{"installId": "install-1"})
		if rec.Code != http.StatusOK {
			t.Fatalf("status = %d, want 200: %s", rec.Code, rec.Body.String())
		}
		r, err := st.GetActivationRequest(context.Background(), "act_install-1")
		if err != nil {
			t.Fatalf("GetActivationRequest: %v", err)
		}
		if r.Status != store.ActivationConfirmed || r.LicenseID != out["licenseId"] {
			t.Fatalf("row = %+v, want confirmed with %v", r, out["licenseId"])
		}
	})

	t.Run("a machine this service never heard of is a 404", func(t *testing.T) {
		engine := newAdminEngine(t, newFakeStore(), "acct-admin", []string{"openid-acct-admin"})
		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reissue",
			map[string]string{"installId": "install-nope"})
		if rec.Code != http.StatusNotFound {
			t.Fatalf("status = %d, want 404", rec.Code)
		}
	})

	t.Run("a second live licence on another machine is a conflict", func(t *testing.T) {
		st := &strictReissueStore{}
		// The account already holds a live licence on install-1 and has an
		// open registration on install-2. One live binding per account: the
		// machine change is revoke first, then reissue — a seller decision.
		seedConfirmed(&st.fakeActivationStore, "acct-admin", "install-1", "lic_live")
		seedPending(&st.fakeActivationStore, "acct-admin", "install-2", ptr64(29800))
		engine := newAdminEngine(t, st, "acct-admin", []string{"openid-acct-admin"})

		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reissue",
			map[string]string{"installId": "install-2"})
		if rec.Code != http.StatusConflict {
			t.Fatalf("status = %d, want 409 — one live binding per account", rec.Code)
		}
	})
}

// strictReissueStore stands in for the account-side partial unique index:
// reissuing onto a machine while the account holds a live licence elsewhere
// is a conflict.
type strictReissueStore struct {
	fakeActivationStore
}

func (s *strictReissueStore) ReissueLicence(ctx context.Context, installID, licenseID string, envelopeJSON []byte, at time.Time) (store.ActivationRequest, error) {
	bindings, err := s.ListBindings(ctx)
	if err != nil {
		return store.ActivationRequest{}, err
	}
	for _, b := range bindings {
		if b.RevokedAt != nil || b.InstallID == installID {
			continue
		}
		// A live binding on another machine: whose account is it? If it is
		// the account this machine belongs to, the index refuses.
		r, rerr := s.latestRequestForInstall(installID)
		if rerr == nil && r.AccountID == b.AccountID {
			return store.ActivationRequest{}, store.ErrConflict
		}
	}
	return s.fakeActivationStore.ReissueLicence(ctx, installID, licenseID, envelopeJSON, at)
}

// latestRequestForInstall reads the machine's most recent request, whoever
// filed it — the same view the upsert's conflict path uses.
func (s *strictReissueStore) latestRequestForInstall(installID string) (store.ActivationRequest, error) {
	rows, err := s.ListActivationRequests(context.Background(), "")
	if err != nil {
		return store.ActivationRequest{}, err
	}
	for _, v := range rows {
		if v.InstallID == installID {
			return v.ActivationRequest, nil
		}
	}
	return store.ActivationRequest{}, store.ErrNotFound
}

// ---------------------------------------------------------------------------
// Revoke — a ledger fact, not an un-signing.
// ---------------------------------------------------------------------------

func TestAdminRevoke(t *testing.T) {
	t.Run("revokes and is idempotent", func(t *testing.T) {
		st := newFakeStore()
		seedConfirmed(st, "acct-admin", "install-1", "lic_live")
		engine := newAdminEngine(t, st, "acct-admin", []string{"openid-acct-admin"})

		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/revoke",
			map[string]string{"licenseId": "lic_live"})
		if rec.Code != http.StatusOK {
			t.Fatalf("status = %d, want 200: %s", rec.Code, rec.Body.String())
		}
		if body := strings.TrimSpace(rec.Body.String()); body != "{}" {
			t.Fatalf("body = %q, want an empty object", body)
		}

		rec, _ = doJSON(t, engine, http.MethodPost, "/v1/admin/revoke",
			map[string]string{"licenseId": "lic_live"})
		if rec.Code != http.StatusOK {
			t.Fatalf("second revoke status = %d, want 200 — already revoked is a success", rec.Code)
		}

		if _, err := st.LiveBindingByInstallID(context.Background(), "install-1"); err == nil {
			t.Fatal("the binding is still live after a revoke")
		}
	})

	t.Run("a missing licence id is a 400", func(t *testing.T) {
		engine := newAdminEngine(t, newFakeStore(), "acct-admin", []string{"openid-acct-admin"})
		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/revoke", map[string]string{})
		if rec.Code != http.StatusBadRequest {
			t.Fatalf("status = %d, want 400 for a missing id", rec.Code)
		}
	})

	t.Run("a licence this service never issued is a 404", func(t *testing.T) {
		engine := newAdminEngine(t, newFakeStore(), "acct-admin", []string{"openid-acct-admin"})
		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/revoke",
			map[string]string{"licenseId": "lic_ghost"})
		if rec.Code != http.StatusNotFound {
			t.Fatalf("status = %d, want 404", rec.Code)
		}
	})
}

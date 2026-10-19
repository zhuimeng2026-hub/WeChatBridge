package admin

import (
	"net/http"
	"strings"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// A seller must be able to say no.
//
// The rejected state existed in the store, in the buyer's poller and in the
// ledger — and nothing could produce it. A buyer who paid and whom the seller
// refused had a row sitting at pending forever and a window that never closed.
func TestRejectFlipsAPendingRequestAndAnswersNoContent(t *testing.T) {
	t.Parallel()

	st := newFakeStore()
	seedSellerAccount(t, st)
	seedPendingRequest(t, st, "req-1", "install-1", nil)
	engine, _ := newTestEngineWithSigner(t, st, adminAccountID)

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reject", map[string]any{"requestId": "req-1"})
	if rec.Code != http.StatusNoContent {
		t.Fatalf("status = %d, want 204", rec.Code)
	}
	if body := rec.Body.String(); strings.TrimSpace(body) != "" {
		t.Errorf("204 carried a body: %s", body)
	}

	got, err := st.GetActivationRequest(t.Context(), "req-1")
	if err != nil {
		t.Fatalf("GetActivationRequest: %v", err)
	}
	if got.Status != store.ActivationRejected {
		t.Errorf("status = %q, want rejected", got.Status)
	}
	if got.RejectedAt == nil {
		t.Error("rejected_at was not written; a ledger that records the refusal but not when answers half the question")
	}
	if got.RejectedAt != nil && !got.RejectedAt.Equal(testNow) {
		t.Errorf("rejected_at = %v, want the injected clock %v", got.RejectedAt, testNow)
	}
}

// No licence is written and no binding is touched. Refusing this order is not
// revoking an earlier one, and a rejected request never had either.
func TestRejectWritesNoLicence(t *testing.T) {
	t.Parallel()

	st := newFakeStore()
	seedSellerAccount(t, st)
	seedPendingRequest(t, st, "req-1", "install-1", nil)
	engine, _ := newTestEngineWithSigner(t, st, adminAccountID)

	doJSON(t, engine, http.MethodPost, "/v1/admin/reject", map[string]any{"requestId": "req-1"})

	got, err := st.GetActivationRequest(t.Context(), "req-1")
	if err != nil {
		t.Fatal(err)
	}
	if got.LicenseID != "" {
		t.Errorf("license_id = %q, want empty", got.LicenseID)
	}
	if len(got.EnvelopeJSON) != 0 {
		t.Errorf("a rejected request grew licence bytes: %q", got.EnvelopeJSON)
	}
	if got.ConfirmedAt != nil {
		t.Errorf("confirmed_at = %v, want nil", got.ConfirmedAt)
	}
	if n := len(st.bindings); n != 0 {
		t.Errorf("a rejected request created %d binding(s)", n)
	}
}

// The two actions are not interchangeable — one stops a purchase, the other
// takes back something already granted — so the API refuses to treat one as the
// other and says which button would work.
func TestRejectingAConfirmedRequestPointsAtRevoke(t *testing.T) {
	t.Parallel()

	st := newFakeStore()
	seedSellerAccount(t, st)
	// Seeded already confirmed, not seeded pending and flipped afterwards:
	// the fake's seed appends, so a second call would leave two rows and the
	// lookup would return the pending one.
	confirmedAt := testNow.Add(time.Minute)
	st.seedRequest(store.ActivationRequest{
		RequestID:    "req-1",
		AccountID:    buyerAccountID,
		InstallID:    "install-1",
		AnchorDigest: "sha256:anchor",
		Status:       store.ActivationConfirmed,
		LicenseID:    "lic-1",
		CreatedAt:    testNow,
		ConfirmedAt:  &confirmedAt,
	})
	engine, _ := newTestEngineWithSigner(t, st, adminAccountID)

	rec, body := doJSON(t, engine, http.MethodPost, "/v1/admin/reject", map[string]any{"requestId": "req-1"})
	if rec.Code != http.StatusConflict {
		t.Fatalf("status = %d, want 409 — a licence already exists, so /revoke is the action that means something", rec.Code)
	}
	if msg, _ := body["error"].(map[string]any)["message"].(string); !strings.Contains(msg, "作废") {
		t.Errorf("the 409 should name the action that would work, got %q", msg)
	}

	// And it must not have quietly flipped the row.
	got, _ := st.GetActivationRequest(t.Context(), "req-1")
	if got.Status != store.ActivationConfirmed {
		t.Errorf("status = %q, want it left confirmed", got.Status)
	}
}

func TestRejectRejectsABlankRequestID(t *testing.T) {
	t.Parallel()

	st := newFakeStore()
	seedSellerAccount(t, st)
	engine, _ := newTestEngineWithSigner(t, st, adminAccountID)

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reject", map[string]any{"requestId": "   "})
	if rec.Code != http.StatusBadRequest {
		t.Errorf("status = %d, want 400", rec.Code)
	}
}

func TestRejectRejectsAMalformedBody(t *testing.T) {
	t.Parallel()

	st := newFakeStore()
	seedSellerAccount(t, st)
	engine, _ := newTestEngineWithSigner(t, st, adminAccountID)

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reject", []int{1, 2})
	if rec.Code != http.StatusBadRequest {
		t.Errorf("status = %d, want 400", rec.Code)
	}
}

func TestRejectOfAnUnknownRequestIs404(t *testing.T) {
	t.Parallel()

	st := newFakeStore()
	seedSellerAccount(t, st)
	engine, _ := newTestEngineWithSigner(t, st, adminAccountID)

	rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reject", map[string]any{"requestId": "no-such-request"})
	if rec.Code != http.StatusNotFound {
		t.Errorf("status = %d, want 404", rec.Code)
	}
}

// Rejecting frees the machine immediately: the partial unique index covers only
// pending rows, so a refused buyer can register again with no extra step. This is
// the behaviour a seller wants — refusing an order must not lock the buyer out.
func TestRejectFreesTheMachineForAFreshRegistration(t *testing.T) {
	t.Parallel()

	st := newFakeStore()
	seedSellerAccount(t, st)
	seedPendingRequest(t, st, "req-1", "install-1", nil)
	engine, _ := newTestEngineWithSigner(t, st, adminAccountID)

	doJSON(t, engine, http.MethodPost, "/v1/admin/reject", map[string]any{"requestId": "req-1"})

	if _, err := st.CreateActivationRequest(t.Context(), store.ActivationRequest{
		RequestID: "req-2", AccountID: buyerAccountID, InstallID: "install-1", CreatedAt: testNow,
	}); err != nil {
		t.Fatalf("registering again after a refusal should be allowed: %v", err)
	}
}

// Refusing twice is not an error the seller needs to see: a stale console
// showing a row that was refused a moment ago is a misclick, not a fault.
func TestRejectIsIdempotent(t *testing.T) {
	t.Parallel()

	st := newFakeStore()
	seedSellerAccount(t, st)
	seedPendingRequest(t, st, "req-1", "install-1", nil)
	engine, _ := newTestEngineWithSigner(t, st, adminAccountID)

	first, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reject", map[string]any{"requestId": "req-1"})
	second, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reject", map[string]any{"requestId": "req-1"})
	if first.Code != http.StatusNoContent {
		t.Fatalf("first refuse = %d, want 204", first.Code)
	}
	// The second is a conflict, not a 204: the row is already terminal and
	// saying "refused" again would claim an outcome this call did not produce.
	if second.Code != http.StatusConflict {
		t.Errorf("second refuse = %d, want 409", second.Code)
	}
}

// The console's bearer gate is not per-endpoint; a new one must not become the
// way around it.
func TestRejectObeysTheSameWhitelistAsEveryOtherAdminCall(t *testing.T) {
	t.Parallel()

	for _, accountID := range []string{"", "someone-else"} {
		st := newFakeStore()
		seedSellerAccount(t, st)
		seedPendingRequest(t, st, "req-1", "install-1", nil)
		engine, _ := newTestEngineWithSigner(t, st, accountID)

		rec, _ := doJSON(t, engine, http.MethodPost, "/v1/admin/reject", map[string]any{"requestId": "req-1"})
		if rec.Code != http.StatusUnauthorized && rec.Code != http.StatusForbidden {
			t.Errorf("account %q got %d, want 401 or 403", accountID, rec.Code)
		}
	}
}

package postgres

import (
	"context"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// TestLatestActivationRequestPrefersTheLiveLicence is the production incident
// in §11.7 M-5, expressed as five accounts whose answers must not move.
//
// It runs against the real migrations rather than a hand-copied schema: the
// rule depends on two partial unique indexes and the liveness of a binding, and
// a fixture that only approximates those would pass a query that is wrong
// about exactly the thing under test.
func TestLatestActivationRequestPrefersTheLiveLicence(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	old := time.Date(2026, 10, 1, 10, 0, 0, 0, time.UTC)
	mid := time.Date(2026, 10, 15, 10, 0, 0, 0, time.UTC)
	revokedAt := old.Add(9 * 24 * time.Hour)

	acct1 := seedAccount(t, r, "open-m5-1")
	// The incident: an older confirmed request whose licence is the machine's
	// live binding, and a newer pending row from the buyer tapping "I have
	// ordered" a second time. The old rule answered the newer row; the whole
	// point is that it must now answer the confirmed one.
	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act_old", AccountID: acct1, InstallID: "inst-1",
		Status: store.ActivationConfirmed, LicenseID: "lic_old", CreatedAt: old,
	})
	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act_new", AccountID: acct1, InstallID: "inst-1",
		Status: store.ActivationPending, CreatedAt: mid,
	})
	mustInsertBinding(t, r, "lic_old", "inst-1", acct1, old, nil)

	acct2 := seedAccount(t, r, "open-m5-2")
	// A first purchase: no confirmed row, no live licence, so the poller keeps
	// waiting on the pending row exactly as it always did.
	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act_p1", AccountID: acct2, InstallID: "inst-2",
		Status: store.ActivationPending, CreatedAt: mid,
	})

	acct3 := seedAccount(t, r, "open-m5-3")
	// The licence was revoked and the buyer bought again. A plain "confirmed
	// beats pending" rule would resurrect the tombstone here, and the buyer
	// could never buy a second time.
	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act_c1", AccountID: acct3, InstallID: "inst-3",
		Status: store.ActivationConfirmed, LicenseID: "lic_c1", CreatedAt: old,
	})
	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act_c2", AccountID: acct3, InstallID: "inst-3",
		Status: store.ActivationPending, CreatedAt: mid,
	})
	mustInsertBinding(t, r, "lic_c1", "inst-3", acct3, old, &revokedAt)

	acct4 := seedAccount(t, r, "open-m5-4")
	// Revoked, and nothing pending. The dead row is still the account's
	// history and must keep reading as confirmed.
	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act_d1", AccountID: acct4, InstallID: "inst-4",
		Status: store.ActivationConfirmed, LicenseID: "lic_d1", CreatedAt: old,
	})
	mustInsertBinding(t, r, "lic_d1", "inst-4", acct4, old, &revokedAt)

	acct5 := seedAccount(t, r, "open-m5-5")
	// A reissue: two confirmed rows, the older one's binding revoked. The live
	// one must win.
	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act_e1", AccountID: acct5, InstallID: "inst-5",
		Status: store.ActivationConfirmed, LicenseID: "lic_e1", CreatedAt: old,
	})
	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act_e2", AccountID: acct5, InstallID: "inst-5",
		Status: store.ActivationConfirmed, LicenseID: "lic_e2", CreatedAt: mid,
	})
	mustInsertBinding(t, r, "lic_e1", "inst-5", acct5, old, &revokedAt)
	mustInsertBinding(t, r, "lic_e2", "inst-5", acct5, mid, nil)

	cases := []struct {
		account, wantID  string
		wantStatus       store.ActivationStatus
		wantLicense, why string
	}{
		{acct1, "act_old", store.ActivationConfirmed, "lic_old", "the live licence must beat a newer registration"},
		{acct2, "act_p1", store.ActivationPending, "", "a first purchase is unaffected"},
		{acct3, "act_c2", store.ActivationPending, "", "a dead licence must not shadow a purchase that can still be signed"},
		{acct4, "act_d1", store.ActivationConfirmed, "lic_d1", "a revoked licence alone still reads as confirmed"},
		{acct5, "act_e2", store.ActivationConfirmed, "lic_e2", "after a reissue the poller waits on the live one"},
	}
	for _, tc := range cases {
		t.Run(tc.why, func(t *testing.T) {
			got, err := r.LatestActivationRequest(ctx, tc.account)
			if err != nil {
				t.Fatalf("latest: %v", err)
			}
			if got.RequestID != tc.wantID || got.Status != tc.wantStatus || got.LicenseID != tc.wantLicense {
				t.Fatalf("got %s/%s/%q, want %s/%s/%q",
					got.RequestID, got.Status, got.LicenseID,
					tc.wantID, tc.wantStatus, tc.wantLicense)
			}
		})
	}
}

// mustInsertRequest files one activation_requests row directly.
//
// It does NOT go through CreateActivationRequest, and that is the point: the
// Store's upsert deliberately writes status='pending' and license_id=” on
// insert, because registering a request as "already confirmed" is not something
// a buyer may do. These fixtures need rows the Store would not let them reach —
// a confirmed row with a revoked binding, mostly — and reaching them through the
// public method would mean silently getting pending rows and wondering why the
// test failed.
func mustInsertRequest(t *testing.T, r *realDB, req store.ActivationRequest) {
	t.Helper()
	confirmedAt := any(nil)
	if req.Status == store.ActivationConfirmed {
		confirmedAt = req.CreatedAt
	}
	r.exec(t, `INSERT INTO activation_requests
		(request_id, account_id, install_id, status, license_id, created_at, confirmed_at)
		VALUES ($1,$2,$3,$4,$5,$6,$7)`,
		req.RequestID, req.AccountID, req.InstallID,
		string(req.Status), req.LicenseID, req.CreatedAt, confirmedAt)
}

// mustInsertBinding writes a binding row directly, because the Store only
// creates them as a side effect of confirming or reissuing — and the point of
// these fixtures is states the Store would not let us reach in that order.
func mustInsertBinding(t *testing.T, r *realDB, licenseID, installID, accountID string, issued time.Time, revoked *time.Time) {
	t.Helper()
	r.exec(t, `INSERT INTO licence_bindings
		(license_id, install_id, account_id, issued_at, revoked_at) VALUES ($1,$2,$3,$4,$5)`,
		licenseID, installID, accountID, issued, revoked)
}

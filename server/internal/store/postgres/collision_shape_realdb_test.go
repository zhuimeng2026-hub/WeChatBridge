package postgres

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// TestTheProductionCollisionShapeIsAlwaysTheStandingState covers the one shape
// that is actually the incident, and the one the rest of this file pointedly
// does not cover.
//
// Every other test here isolates a single index: one of them uses a different
// account, the other a different machine, because doing otherwise makes the
// answer depend on which index PostgreSQL checks first. That is good hygiene
// for those tests — but it leaves the production case uncovered, and the
// production case is the one 9bc1260 was written for:
//
//   - the buyer already holds a licence for THIS machine, and
//   - it belongs to THIS account.
//
// Both partial unique indexes are violated at once —
// licence_bindings_live_idx (one live binding per machine) and
// licence_bindings_live_account_idx (one live binding per account) — and
// PostgreSQL names whichever it reached first. That order is the order the
// migration happened to create them in, and nothing in the schema declares it.
// Reverse those two CREATE INDEX statements and a store that classified by
// constraint name alone would answer ErrConflict here, which the console shows
// as " somebody beat you to it, refresh and try again" about a state that no
// amount of retrying can clear — the exact failure M-5 was a production
// incident for.
//
// So this test pins the SENTINEL, not merely that the write was refused. If it
// ever accepts ErrConflict, it has stopped testing the thing it exists for.
func TestTheProductionCollisionShapeIsAlwaysTheStandingState(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-prodshape")
	base := time.Date(2026, 10, 1, 10, 0, 0, 0, time.UTC)

	// The buyer's existing, valid licence — same machine, same account.
	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act-prod-old", AccountID: acct, InstallID: "inst-prod",
		Status: store.ActivationConfirmed, LicenseID: "lic-prod-old", CreatedAt: base,
	})
	mustInsertBinding(t, r, "lic-prod-old", "inst-prod", acct, base, nil)

	// The buyer taps "我已订购" again. Both indexes are now violated by the
	// insert this will attempt.
	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act-prod-new", AccountID: acct, InstallID: "inst-prod",
		Status: store.ActivationPending, CreatedAt: base.Add(time.Hour),
	})

	_, err := r.ConfirmActivationRequest(ctx, "act-prod-new", "lic-prod-new", []byte(`{"v":2}`), base.Add(2*time.Hour))
	if err == nil {
		t.Fatal("the second registration on a licensed machine was confirmed")
	}
	if !errors.Is(err, store.ErrLiveBindingExists) {
		t.Fatalf("want store.ErrLiveBindingExists, got %v — this is the shape the whole M-5 fix is "+
			"about, and answering ErrConflict here puts the seller back on a button that never works", err)
	}

	// And the refusal must have left the ledger alone: the original licence is
	// still the machine's live one, and nothing was minted.
	live, err := r.LiveBindingByInstallID(ctx, "inst-prod")
	if err != nil {
		t.Fatalf("live binding: %v", err)
	}
	if live.LicenseID != "lic-prod-old" {
		t.Errorf("live binding = %q, want lic-prod-old — a refused confirm must not disturb it", live.LicenseID)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM licence_bindings WHERE install_id = $1`, "inst-prod"); n != 1 {
		t.Errorf("bindings for this machine = %d, want 1", n)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM activation_requests WHERE request_id = $1 AND status = 'pending'`,
		"act-prod-new"); n != 1 {
		t.Errorf("the request should still be pending after a refusal, rows = %d", n)
	}
}

// TestTheRaceShapeStaysARace is the counterpart, and it exists so the test above
// cannot be satisfied by simply always answering the standing-state error.
//
// Two different machines, one account: the account-scoped index is violated,
// the install-scoped one is not. That is a genuine collision between two
// pending requests, the loser of which must be able to re-read and find the
// winner's licence. Answering ErrLiveBindingExists here would tell the seller
// to reissue a machine that needs no reissuing.
func TestTheRaceShapeStaysARace(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-raceshape")
	base := time.Date(2026, 10, 1, 10, 0, 0, 0, time.UTC)

	// A live licence on a DIFFERENT machine of the same account.
	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act-race-old", AccountID: acct, InstallID: "inst-other",
		Status: store.ActivationConfirmed, LicenseID: "lic-race-old", CreatedAt: base,
	})
	mustInsertBinding(t, r, "lic-race-old", "inst-other", acct, base, nil)

	mustInsertRequest(t, r, store.ActivationRequest{
		RequestID: "act-race-new", AccountID: acct, InstallID: "inst-new",
		Status: store.ActivationPending, CreatedAt: base.Add(time.Hour),
	})

	_, err := r.ConfirmActivationRequest(ctx, "act-race-new", "lic-race-new", []byte(`{"v":2}`), base.Add(2*time.Hour))
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict, got %v — a different machine's collision is a race, not a reissue", err)
	}
	if errors.Is(err, store.ErrLiveBindingExists) {
		t.Fatal("reported as a machine needing a reissue; that machine needs no reissue")
	}
}

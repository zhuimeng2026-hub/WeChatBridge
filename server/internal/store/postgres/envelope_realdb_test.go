package postgres

import (
	"context"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// seedAccount creates an account so activation rows have something to point at.
// The activation_requests FK to accounts is real, and a fixture that dodged it
// would be testing a schema nobody deploys.
func seedAccount(t *testing.T, r *realDB, openID string) store.ID {
	t.Helper()
	acct, created, err := r.UpsertAccountByOpenID(context.Background(), openID, "union-"+openID, "nick-"+openID, "")
	if err != nil {
		t.Fatalf("seed account: %v", err)
	}
	if !created {
		t.Fatalf("account %s already exists; the schema is not isolated", openID)
	}
	return acct.ID
}

// TestEnvelopeBytesSurviveTheRoundTrip pins the schema decision behind the
// whole licence chain: envelope_json is bytea, not jsonb.
//
// jsonb reorders object keys. The buyer's licence is an opaque token that
// verification recomputes a canonical form from, and the seller's signature was
// taken over the exact bytes that were stored. A column that "helpfully"
// normalises them on the way in and out would hand every buyer a token whose
// bytes differ from the ones signed — and, because canonicalisation is
// deterministic, it would differ in a way that only shows up as a signature
// that mysteriously fails on some buyers and not others.
//
// This is the one property in the package that no amount of pgxmock can check,
// because a mock never runs the column's type.
func TestEnvelopeBytesSurviveTheRoundTrip(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-envelope")
	now := time.Now().UTC().Truncate(time.Microsecond)

	// Deliberately NOT alphabetical, and deliberately not what any Go JSON
	// encoder would produce. If anything normalises these bytes, the keys come
	// back sorted and this fails.
	original := []byte(`{"z":"last","v":1,"sub":"install-1","ent":["PRO"],"a":"first","sig":"AAAA"}`)

	if _, err := r.CreateActivationRequest(ctx, store.ActivationRequest{
		RequestID: "act-env", AccountID: acct, InstallID: "install-1",
		CreatedAt: now,
	}); err != nil {
		t.Fatalf("create request: %v", err)
	}
	if _, err := r.ConfirmActivationRequest(ctx, "act-env", "lic-env", original, now); err != nil {
		t.Fatalf("confirm: %v", err)
	}

	got, err := r.GetActivationRequest(ctx, "act-env")
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	if string(got.EnvelopeJSON) != string(original) {
		t.Fatalf("envelope bytes changed in storage:\n got %s\nwant %s", got.EnvelopeJSON, original)
	}

	// The buyer's poller reads it through a different query; if any projection
	// round-tripped it through a JSON type the bytes would change there too.
	latest, err := r.LatestActivationRequest(ctx, acct)
	if err != nil {
		t.Fatalf("read latest: %v", err)
	}
	if string(latest.EnvelopeJSON) != string(original) {
		t.Fatalf("envelope bytes changed on the poller's path:\n got %s\nwant %s", latest.EnvelopeJSON, original)
	}
}

// TestConfirmedRequestRoundTripsThroughTheConsoleList is the same bytes one
// more hop out. ListActivationRequests joins accounts, and a join is exactly
// where a projection starts growing or losing columns.
func TestConfirmedRequestRoundTripsThroughTheConsoleList(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-list")
	now := time.Now().UTC().Truncate(time.Microsecond)
	original := []byte(`{"m":"middle","v":1,"sub":"install-2","sig":"BBBB"}`)

	if _, err := r.CreateActivationRequest(ctx, store.ActivationRequest{
		RequestID: "act-list", AccountID: acct, InstallID: "install-2", CreatedAt: now,
	}); err != nil {
		t.Fatalf("create: %v", err)
	}
	if _, err := r.ConfirmActivationRequest(ctx, "act-list", "lic-list", original, now); err != nil {
		t.Fatalf("confirm: %v", err)
	}

	rows, err := r.ListActivationRequests(ctx, store.ActivationConfirmed)
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	if len(rows) != 1 {
		t.Fatalf("rows = %d, want 1", len(rows))
	}
	if string(rows[0].EnvelopeJSON) != string(original) {
		t.Fatalf("envelope bytes changed on the console's path:\n got %s\nwant %s", rows[0].EnvelopeJSON, original)
	}
	if rows[0].Nickname != "nick-open-list" {
		t.Errorf("the join lost the account columns: nickname = %q", rows[0].Nickname)
	}
}

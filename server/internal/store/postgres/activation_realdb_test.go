package postgres

import (
	"context"
	"os"
	"strings"
	"testing"
	"time"

	"github.com/jackc/pgx/v5"
)

// The tests in this package run on pgxmock, which asserts that a query string
// looks right and never executes it. That is fine for the queries whose
// meaning is "did we send the right shape", and useless for the ones whose
// meaning is "does this return the right row" — this file's query is exactly
// the latter, and its bug (a newer pending row shadowing a confirmed one) was
// invisible to every test in the tree: the fake store implements whatever rule
// the fake is written to, so the suite stays green whether the SQL is right or
// wrong. Reverting latestActivationRequestSQL to its old form leaves the whole
// package passing.
//
// So the semantics get pinned against a real PostgreSQL. Set
// WCB_TEST_DATABASE_URL to run these; without it they skip, so CI — which has
// no database — is unaffected. This is a narrow slice of the real-database
// coverage A-⑧ describes, not that work.
const testDSNEnv = "WCB_TEST_DATABASE_URL"

// ddl is the shape these tests need, copied from migrations/0001_init.up.sql.
// Only the columns the query touches are here; a fixture this small keeps the
// point of the test legible.
const latestRequestDDL = `
CREATE TABLE activation_requests (
	request_id    text PRIMARY KEY,
	account_id    text NOT NULL,
	install_id    text NOT NULL,
	anchor_digest text NOT NULL DEFAULT '',
	amount_fen    bigint,
	status        text NOT NULL DEFAULT 'pending',
	envelope_json bytea,
	license_id    text NOT NULL DEFAULT '',
	created_at    timestamptz NOT NULL DEFAULT now(),
	confirmed_at  timestamptz,
	rejected_at   timestamptz
);
CREATE TABLE licence_bindings (
	license_id text PRIMARY KEY,
	install_id text NOT NULL,
	account_id text NOT NULL,
	request_id text,
	issued_at  timestamptz NOT NULL DEFAULT now(),
	revoked_at timestamptz
);
CREATE UNIQUE INDEX licence_bindings_live_idx
	ON licence_bindings (install_id) WHERE revoked_at IS NULL;
CREATE UNIQUE INDEX licence_bindings_live_account_idx
	ON licence_bindings (account_id) WHERE revoked_at IS NULL;
`

func liveTestStore(t *testing.T) (*Store, pgx.Tx) {
	t.Helper()
	dsn := os.Getenv(testDSNEnv)
	if dsn == "" {
		t.Skipf("%s is not set; skipping the real-database test", testDSNEnv)
	}
	ctx := context.Background()
	s, err := New(ctx, dsn)
	if err != nil {
		t.Skipf("cannot reach the test database: %v", err)
	}
	t.Cleanup(func() { _ = s.Close() })

	// Everything runs inside one transaction that is rolled back, so the test
	// cannot leave rows behind even if the fixture is wrong.
	tx, err := s.db.Begin(ctx)
	if err != nil {
		t.Fatalf("begin: %v", err)
	}
	t.Cleanup(func() { _ = tx.Rollback(context.Background()) })

	// Its own schema, because the database this points at is a real one that
	// may already carry the production tables. The DDL is deliberately the
	// shape these tests need, not the whole 0001 migration.
	schema := "wcb_test_" + strings.ReplaceAll(t.Name(), "/", "_")
	if _, err := tx.Exec(ctx, "CREATE SCHEMA "+schema); err != nil {
		t.Fatalf("create schema: %v", err)
	}
	if _, err := tx.Exec(ctx, "SET LOCAL search_path TO "+schema); err != nil {
		t.Fatalf("set search_path: %v", err)
	}
	if _, err := tx.Exec(ctx, latestRequestDDL); err != nil {
		t.Fatalf("create fixture tables: %v", err)
	}
	return s, tx
}

// requestRow is one row of the poller's read, in the shape the assertions read
// it back in.
type requestRow struct {
	id        string
	status    string
	licenseID string
}

func latestRequestFor(t *testing.T, tx pgx.Tx, accountID string) requestRow {
	t.Helper()
	row := tx.QueryRow(context.Background(),
		strings.ReplaceAll(latestActivationRequestSQL, "$1", "'"+accountID+"'"))
	var r requestRow
	var licenseID *string
	if err := row.Scan(&r.id, new(string), new(string), new(string), new(*int64),
		&r.status, new(*[]byte), &licenseID, new(time.Time), new(*time.Time), new(*time.Time)); err != nil {
		t.Fatalf("scan latest request for %s: %v", accountID, err)
	}
	if licenseID != nil {
		r.licenseID = *licenseID
	}
	return r
}

func seed(t *testing.T, tx pgx.Tx, q string, args ...any) {
	t.Helper()
	if _, err := tx.Exec(context.Background(), q, args...); err != nil {
		t.Fatalf("seed %q: %v", q, err)
	}
}

// seedRequest writes one activation_requests row. `at` fixes created_at, since
// which row is newest is the entire question under test.
func seedRequest(t *testing.T, tx pgx.Tx, id, account, install, status, licenseID string, at time.Time) {
	t.Helper()
	seed(t, tx, `INSERT INTO activation_requests
		(request_id, account_id, install_id, status, license_id, created_at, confirmed_at)
		VALUES ($1,$2,$3,$4,$5,$6,$7)`,
		id, account, install, status, licenseID, at,
		nullableIf(status == "confirmed", at))
}

func seedBinding(t *testing.T, tx pgx.Tx, licenseID, install, account string, issued time.Time, revoked *time.Time) {
	t.Helper()
	seed(t, tx, `INSERT INTO licence_bindings
		(license_id, install_id, account_id, issued_at, revoked_at) VALUES ($1,$2,$3,$4,$5)`,
		licenseID, install, account, issued, revoked)
}

func nullableIf(cond bool, v time.Time) *time.Time {
	if cond {
		return &v
	}
	return nil
}

// TestLatestActivationRequestAgainstARealDatabase is the production incident in
// §11.7 M-5, expressed as five accounts whose answers must not move.
func TestLatestActivationRequestAgainstARealDatabase(t *testing.T) {
	_, tx := liveTestStore(t)
	old := time.Date(2026, 10, 1, 10, 0, 0, 0, time.UTC)
	mid := time.Date(2026, 10, 15, 10, 0, 0, 0, time.UTC)
	revokedAt := old.Add(9 * 24 * time.Hour)

	// acct-1 — the incident. An older confirmed request whose licence is the
	// machine's live binding, and a newer pending row from the buyer tapping
	// "I have ordered" a second time. The old rule answered `new`; the whole
	// point is that it must now answer `old`.
	seedRequest(t, tx, "act_old", "acct-1", "inst-1", "confirmed", "lic_old", old)
	seedRequest(t, tx, "act_new", "acct-1", "inst-1", "pending", "", mid)
	seedBinding(t, tx, "lic_old", "inst-1", "acct-1", old, nil)

	// acct-2 — a first purchase. No confirmed row, no live licence, so the
	// poller must keep waiting on the pending row exactly as before.
	seedRequest(t, tx, "act_p1", "acct-2", "inst-2", "pending", "", mid)

	// acct-3 — the licence was revoked and the buyer bought again. A plain
	// "confirmed beats pending" rule would resurrect the tombstone here and
	// the buyer could never buy a second time.
	seedRequest(t, tx, "act_c1", "acct-3", "inst-3", "confirmed", "lic_c1", old)
	seedRequest(t, tx, "act_c2", "acct-3", "inst-3", "pending", "", mid)
	seedBinding(t, tx, "lic_c1", "inst-3", "acct-3", old, &revokedAt)

	// acct-4 — revoked, and nothing pending. The dead row is still the
	// account's history and must keep reading as confirmed.
	seedRequest(t, tx, "act_d1", "acct-4", "inst-4", "confirmed", "lic_d1", old)
	seedBinding(t, tx, "lic_d1", "inst-4", "acct-4", old, &revokedAt)

	// acct-5 — a reissue: two confirmed rows, the older one's binding revoked.
	// The live one must win.
	seedRequest(t, tx, "act_e1", "acct-5", "inst-5", "confirmed", "lic_e1", old)
	seedRequest(t, tx, "act_e2", "acct-5", "inst-5", "confirmed", "lic_e2", mid)
	seedBinding(t, tx, "lic_e1", "inst-5", "acct-5", old, &revokedAt)
	seedBinding(t, tx, "lic_e2", "inst-5", "acct-5", mid, nil)

	cases := []struct {
		account, wantID, wantStatus, wantLicense, why string
	}{
		{"acct-1", "act_old", "confirmed", "lic_old", "the live licence must beat a newer registration"},
		{"acct-2", "act_p1", "pending", "", "a first purchase is unaffected"},
		{"acct-3", "act_c2", "pending", "", "a dead licence must not shadow a purchase that can still be signed"},
		{"acct-4", "act_d1", "confirmed", "lic_d1", "a revoked licence alone still reads as confirmed"},
		{"acct-5", "act_e2", "confirmed", "lic_e2", "after a reissue the poller waits on the live one"},
	}
	for _, tc := range cases {
		t.Run(tc.why, func(t *testing.T) {
			got := latestRequestFor(t, tx, tc.account)
			if got.id != tc.wantID || got.status != tc.wantStatus || got.licenseID != tc.wantLicense {
				t.Fatalf("%s: got %s/%s/%q, want %s/%s/%q",
					tc.account, got.id, got.status, got.licenseID,
					tc.wantID, tc.wantStatus, tc.wantLicense)
			}
		})
	}
}

package postgres

// Real-database tests for the activation path's semantics.
//
// The harness's own comment says why this file exists: pgxmock asserts that a
// query string looks right and never runs it. So everything that lives in the
// DATABASE'S EXECUTION — which unique index fired, how many rows a guarded
// UPDATE actually touched, which of two concurrent sellers won, what order a
// result set comes back in — has never been observed. §11.7 M-5 is the
// standing proof of the gap: a query whose entire purpose was "which row does
// the poller wait on" was reverted to the buggy version and the whole package
// stayed green, because the test for it was a fake that implemented whatever
// rule the fake was written to.
//
// Every test below names the invariant it protects and what breaks without it.
// The two rules the rest of the repository lives by are:
//
//  1. Nothing that must happen exactly once is implemented as "SELECT then
//     INSERT". It is a conditional UPDATE that reports how many rows it
//     touched, or a unique index. The application reads the row count; it
//     never reads the row and then decides.
//  2. A collision is not one event. "Somebody beat you to it" (retryable,
//     self-healing) and "this was already true before you arrived" (never
//     retryable, needs a human) get different sentinels, because collapsing
//     them produced a confirm button that answered 409 forever.
//
// These tests are the first place either rule is actually executed.

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgtype"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

// int64Ptr is a convenience for the optional amount field.
func int64Ptr(v int64) *int64 { return &v }

// execErr runs raw SQL and hands back the driver's error instead of failing
// the test.
//
// The harness's exec() is right for fixtures and wrong for the assertions
// below: "the database refused this" is the EXPECTED outcome in most of
// these tests, and a helper that fatals on error cannot express that.
func execErr(ctx context.Context, r *realDB, sql string, args ...any) error {
	_, err := r.db.Exec(ctx, sql, args...)
	return err
}

// uniqueConstraintOf pulls the constraint name out of a unique violation.
//
// Asserting on the NAME and not merely on "some unique index fired" is the
// point. The whole error-classification story in errors.go is a mapping from
// constraint name to sentinel — licence_bindings_live_idx means the machine
// already holds a licence, licence_bindings_live_account_idx means a genuine
// race between two machines of one account. A test that only checked SQLSTATE
// 23505 would stay green while the classification quietly went to the wrong
// branch, which is exactly the bug 9bc1260 was about.
func uniqueConstraintOf(t *testing.T, err error) string {
	t.Helper()
	var pgErr *pgconn.PgError
	if !errors.As(err, &pgErr) {
		t.Fatalf("want a PostgreSQL error, got %v", err)
	}
	if pgErr.Code != pgUniqueViolation {
		t.Fatalf("want SQLSTATE %s, got %s (%v)", pgUniqueViolation, pgErr.Code, err)
	}
	return pgErr.ConstraintName
}

// bindingRevokedAt reads a binding's revocation date straight out of the
// table, so a test can assert on the date without going through the Store's
// own projections.
func bindingRevokedAt(t *testing.T, r *realDB, licenseID string) *time.Time {
	t.Helper()
	var ts pgtype.Timestamptz
	err := r.db.QueryRow(context.Background(),
		`SELECT revoked_at FROM licence_bindings WHERE license_id = $1`, licenseID).Scan(&ts)
	if err != nil {
		t.Fatalf("read revoked_at of %s: %v", licenseID, err)
	}
	return nullTime(ts)
}

// insertPendingRequest files a pending request for a machine that no other
// test in this schema knows about.
//
// It goes through the Store rather than raw SQL because the pending row IS
// the thing the seller confirms; a hand-written INSERT could produce a row
// the Store would never have created.
func insertPendingRequest(t *testing.T, r *realDB, accountID, installID, requestID string, at time.Time) {
	t.Helper()
	_, err := r.CreateActivationRequest(context.Background(), store.ActivationRequest{
		RequestID: requestID, AccountID: accountID, InstallID: installID,
		CreatedAt: at,
	})
	if err != nil {
		t.Fatalf("create request %s: %v", requestID, err)
	}
}

// ---------------------------------------------------------------------------
// 1. activation_requests_live_install_idx — one live registration per machine
// ---------------------------------------------------------------------------

// TestASecondPendingRegistrationForOneMachineIsImpossible is the schema rule
// the whole registration path is built on: UNIQUE (install_id) WHERE
// status = 'pending'.
//
// It matters because CreateActivationRequest's ON CONFLICT clause names this
// exact index as its conflict target. Without the index the upsert would not
// merely misbehave — the clause would not resolve to any index at all, and
// the seller's console would grow a second line for one machine, each line
// asking the seller for a decision about a purchase that only happened once.
//
// The test writes the second row with raw SQL on purpose. Going through the
// Store would exercise the upsert's DO UPDATE branch, which is a different
// rule (see the upsert test below) and would hide the index itself.
func TestASecondPendingRegistrationForOneMachineIsImpossible(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-live-install")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	r.exec(t, `INSERT INTO activation_requests
		(request_id, account_id, install_id, status, created_at)
		VALUES ($1, $2, $3, 'pending', $4)`, "act-sem-a", acct, "inst-sem-1", base)

	err := execErr(ctx, r, `INSERT INTO activation_requests
		(request_id, account_id, install_id, status, created_at)
		VALUES ($1, $2, $3, 'pending', $4)`, "act-sem-b", acct, "inst-sem-1", base.Add(time.Hour))
	if err == nil {
		t.Fatal("two pending registrations for one machine were accepted")
	}
	if got := uniqueConstraintOf(t, err); got != "activation_requests_live_install_idx" {
		t.Fatalf("constraint = %q, want activation_requests_live_install_idx", got)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM activation_requests WHERE install_id = $1`, "inst-sem-1"); n != 1 {
		t.Fatalf("rows for the machine = %d, want 1", n)
	}
}

// TestConfirmingARegistrationFreesTheMachineForANewOne pins the half of the
// partial index that is easy to forget: it is PARTIAL.
//
// The index only covers pending rows, so a machine that has been answered —
// confirmed or rejected — is free to register again, because the next
// purchase is a new row and not a conflict. A plain UNIQUE(install_id) would
// make every buyer's second purchase an error, and the seller would have no
// button that can produce the answer.
func TestConfirmingARegistrationFreesTheMachineForANewOne(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-freed-by-confirm")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, acct, "inst-sem-2", "act-sem-c", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-c", "lic-sem-c", []byte(`{"v":1}`), base); err != nil {
		t.Fatalf("confirm: %v", err)
	}

	// A second purchase on the same machine. It must be allowed: the index
	// does not cover confirmed rows.
	r.exec(t, `INSERT INTO activation_requests
		(request_id, account_id, install_id, status, created_at)
		VALUES ($1, $2, $3, 'pending', $4)`, "act-sem-d", acct, "inst-sem-2", base.Add(time.Hour))

	if n := r.queryInt(t, `SELECT count(*) FROM activation_requests
		WHERE install_id = $1 AND status = 'pending'`, "inst-sem-2"); n != 1 {
		t.Fatalf("pending rows for the machine = %d, want 1", n)
	}
}

// TestRejectingARegistrationFreesTheMachineForANewOne is the same freedom
// granted by the other terminal state.
//
// The migration that added rejected_at says it in so many words: "refusing
// an order must not lock the buyer out of trying again". The buyer whose
// transfer never arrived has to be able to register a second time, and the
// path that brings them back is a fresh registration — the partial index
// means no extra step, no manual release.
func TestRejectingARegistrationFreesTheMachineForANewOne(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-freed-by-reject")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, acct, "inst-sem-3", "act-sem-e", base)
	if _, err := r.RejectActivationRequest(ctx, "act-sem-e", base); err != nil {
		t.Fatalf("reject: %v", err)
	}

	r.exec(t, `INSERT INTO activation_requests
		(request_id, account_id, install_id, status, created_at)
		VALUES ($1, $2, $3, 'pending', $4)`, "act-sem-f", acct, "inst-sem-3", base.Add(time.Hour))

	if n := r.queryInt(t, `SELECT count(*) FROM activation_requests
		WHERE install_id = $1 AND status = 'pending'`, "inst-sem-3"); n != 1 {
		t.Fatalf("pending rows for the machine = %d, want 1", n)
	}
}

// TestARejectedRowCannotExistWithoutItsDate protects the CHECK constraint
// the reject flip is written to satisfy.
//
// activation_requests_rejected_is_dated says status <> 'rejected' OR
// rejected_at IS NOT NULL. The flip writes both in one statement, so the
// constraint can never be made to hold one without the other — and a ledger
// that records that a request was rejected but not WHEN answers half the
// question a support conversation asks.
func TestARejectedRowCannotExistWithoutItsDate(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-rejected-dated")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	err := execErr(ctx, r, `INSERT INTO activation_requests
		(request_id, account_id, install_id, status, created_at)
		VALUES ($1, $2, $3, 'rejected', $4)`, "act-sem-g", acct, "inst-sem-4", base)
	if err == nil {
		t.Fatal("a rejected row with no rejection date was accepted")
	}
	var pgErr *pgconn.PgError
	if !errors.As(err, &pgErr) {
		t.Fatalf("want a PostgreSQL error, got %v", err)
	}
	if pgErr.ConstraintName != "activation_requests_rejected_is_dated" {
		t.Fatalf("constraint = %q, want activation_requests_rejected_is_dated", pgErr.ConstraintName)
	}
}

// TestCreateActivationRequestKeepsOneLivePendingRowPerMachine is the Store's
// side of the same index: the upsert that makes a second "buy" tap refresh
// the row instead of queueing a second line.
//
// The load-bearing detail is the request_id. The UPDATE branch deliberately
// keeps the row's ORIGINAL request_id, because a caller that echoed back the
// id it minted would hand the customer a 单号 the seller's console has never
// heard of — on exactly the flow where the customer quotes that number for
// support.
func TestCreateActivationRequestKeepsOneLivePendingRowPerMachine(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-upsert")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	first, err := r.CreateActivationRequest(ctx, store.ActivationRequest{
		RequestID: "act-sem-h", AccountID: acct, InstallID: "inst-sem-5", CreatedAt: base,
	})
	if err != nil {
		t.Fatalf("first registration: %v", err)
	}

	second, err := r.CreateActivationRequest(ctx, store.ActivationRequest{
		RequestID: "act-sem-i", AccountID: acct, InstallID: "inst-sem-5", CreatedAt: base.Add(time.Hour),
	})
	if err != nil {
		t.Fatalf("second registration: %v", err)
	}
	if second.RequestID != first.RequestID {
		t.Fatalf("request id changed on the upsert path: %q then %q", first.RequestID, second.RequestID)
	}
	if !second.CreatedAt.Equal(first.CreatedAt) {
		t.Fatalf("created_at moved on the upsert path: %v then %v", first.CreatedAt, second.CreatedAt)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM activation_requests WHERE install_id = $1`, "inst-sem-5"); n != 1 {
		t.Fatalf("rows for the machine = %d, want 1", n)
	}
}

// TestCreateActivationRequestDoesNotAdoptAStrangerMachine is the account
// predicate on the same upsert.
//
// The account predicate is what keeps a stranger's machine code from being
// adopted into somebody else's purchase. When it refuses, the caller is
// handed the row it refused to touch — so the service can say plainly whose
// purchase is in flight instead of inventing a conflict or, worse, taking
// over a purchase that was paid for by somebody else.
//
// "Refuses" has to mean refuses: the stranger's registration must not even
// REFRESH the owner's row. The predicate sits on the DO UPDATE clause, so
// without it the stranger's anchor and amount would be written over the
// owner's — the row would still belong to the owner, but it would now
// describe the stranger's purchase. The anchor and amount are what the
// seller's console shows beside the request, so that is a purchase quietly
// rewritten rather than adopted, and it is just as wrong.
func TestCreateActivationRequestDoesNotAdoptAStrangerMachine(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	owner := seedAccount(t, r, "open-sem-owner")
	stranger := seedAccount(t, r, "open-sem-stranger")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, owner, "inst-sem-6", "act-sem-j", base)
	// The owner's own registration details, which the stranger must not
	// overwrite.
	r.exec(t, `UPDATE activation_requests SET anchor_digest = $2, amount_fen = $3
		WHERE request_id = $1`, "act-sem-j", "owner-anchor", 8800)

	got, err := r.CreateActivationRequest(ctx, store.ActivationRequest{
		RequestID: "act-sem-k", AccountID: stranger, InstallID: "inst-sem-6",
		AnchorDigest: "stranger-anchor", AmountFen: int64Ptr(199), CreatedAt: base.Add(time.Hour),
	})
	if err != nil {
		t.Fatalf("stranger registration: %v", err)
	}
	if got.AccountID != owner {
		t.Fatalf("the stranger's registration was adopted: account = %q, want %q", got.AccountID, owner)
	}
	if got.RequestID != "act-sem-j" {
		t.Fatalf("the refused row was not handed back: request = %q", got.RequestID)
	}
	if got.AnchorDigest != "owner-anchor" {
		t.Fatalf("the stranger's registration rewrote the owner's row: anchor = %q, want owner-anchor", got.AnchorDigest)
	}
	if got.AmountFen == nil || *got.AmountFen != 8800 {
		t.Fatalf("the stranger's registration rewrote the owner's amount: %v, want 8800", got.AmountFen)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM activation_requests WHERE install_id = $1`, "inst-sem-6"); n != 1 {
		t.Fatalf("rows for the machine = %d, want 1", n)
	}
}

// ---------------------------------------------------------------------------
// 2. licence_bindings_live_account_idx — one live licence per account
// ---------------------------------------------------------------------------

// TestASecondLiveLicenceForOneAccountIsRefusedAsAConflict is the account
// side of the binding index, and it pins WHICH sentinel answers it.
//
// licence_bindings_live_account_idx is UNIQUE (account_id) WHERE
// revoked_at IS NULL. One account can hold two pending requests for two
// different machines, so two sellers confirming at the same time is an
// ordinary race there — and a race is ErrConflict: the loser re-reads and
// finds out who won.
//
// The two machines are different on purpose. The install-scoped index cannot
// fire here (the second machine has no live binding), so the account-scoped
// index is the only one that can — and the test does not depend on the order
// in which PostgreSQL happens to check two indexes.
func TestASecondLiveLicenceForOneAccountIsRefusedAsAConflict(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-account-live")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	// The account already holds a live licence, on machine A.
	insertPendingRequest(t, r, acct, "inst-sem-a1", "act-sem-l", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-l", "lic-sem-a1", []byte(`{"v":1}`), base); err != nil {
		t.Fatalf("confirm the first machine: %v", err)
	}

	// A second machine of the SAME account registers.
	insertPendingRequest(t, r, acct, "inst-sem-a2", "act-sem-m", base.Add(time.Hour))
	_, err := r.ConfirmActivationRequest(ctx, "act-sem-m", "lic-sem-a2", []byte(`{"v":2}`), base.Add(time.Hour))
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict, got %v", err)
	}
	if errors.Is(err, store.ErrLiveBindingExists) {
		t.Fatalf("the account-scoped collision was reported as a machine-scoped one: %v", err)
	}

	// The failed confirm must have left nothing behind: the transaction rolled
	// back, so the request is still pending and no second binding exists.
	req, err := r.GetActivationRequest(ctx, "act-sem-m")
	if err != nil {
		t.Fatalf("read the request back: %v", err)
	}
	if req.Status != store.ActivationPending {
		t.Fatalf("the request was answered by a failed confirm: status = %q", req.Status)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM licence_bindings WHERE account_id = $1`, acct); n != 1 {
		t.Fatalf("bindings for the account = %d, want 1", n)
	}
}

// TestConfirmingOnAMachineThatAlreadyHoldsALicenceIsALiveBindingExists is
// the install-scoped half, and it pins the OTHER sentinel.
//
// licence_bindings_live_idx is UNIQUE (install_id) WHERE revoked_at IS NULL.
// Inside a single confirm this cannot be a race — the pending-request index
// guarantees one pending request per install id, and the confirm path takes
// a row lock and re-checks the status before inserting. So the only way to
// land here is a machine that already holds a live licence from an EARLIER
// request: the buyer registered again.
//
// Retrying can never succeed, and the seller is not the one who is late. That
// is why this is ErrLiveBindingExists and not ErrConflict: 9bc1260 exists
// because the old code answered "somebody else got there first, refresh and
// try again" about a state that no amount of refreshing could ever change.
//
// The second account is deliberate: with the same account, the account-scoped
// index would also be violated, and the test would then be asserting on the
// order in which PostgreSQL checks two indexes rather than on the rule.
func TestConfirmingOnAMachineThatAlreadyHoldsALicenceIsALiveBindingExists(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	holder := seedAccount(t, r, "open-sem-machine-holder")
	other := seedAccount(t, r, "open-sem-machine-other")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, holder, "inst-sem-i1", "act-sem-n", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-n", "lic-sem-i1", []byte(`{"v":1}`), base); err != nil {
		t.Fatalf("confirm the first registration: %v", err)
	}

	// A pending registration for the SAME machine, filed by another account.
	// The pending index permits it (the first request is confirmed, not
	// pending); the binding index will not.
	insertPendingRequest(t, r, other, "inst-sem-i1", "act-sem-o", base.Add(time.Hour))
	_, err := r.ConfirmActivationRequest(ctx, "act-sem-o", "lic-sem-i2", []byte(`{"v":2}`), base.Add(time.Hour))
	if !errors.Is(err, store.ErrLiveBindingExists) {
		t.Fatalf("want store.ErrLiveBindingExists, got %v", err)
	}
	if errors.Is(err, store.ErrConflict) {
		t.Fatalf("the machine-scoped collision was reported as a race: %v", err)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM licence_bindings WHERE install_id = $1`, "inst-sem-i1"); n != 1 {
		t.Fatalf("bindings for the machine = %d, want 1", n)
	}
}

// TestABuyerWhoRegistersAgainOnTheSameMachineCannotBeConfirmedTwice is the
// production shape of the same collision: the SAME account, the SAME machine.
//
// The seller who sees this is looking at a buyer who already paid. The
// question the store has to answer is "is this a race or a standing fact",
// and the answer is: a standing fact, because within one confirm the pending
// index allows only one pending request per install id and the confirm path
// holds the row lock. The request stays pending, no second licence is
// minted, and the account still holds exactly one live licence.
//
// Which of the two sentinels PostgreSQL's error carries depends on the order
// in which it checks the two partial indexes (both are violated here), and
// that order is an implementation detail of the planner. So this test pins
// the OBSERVABLE state — one live licence, request unanswered, no new
// binding — and leaves the sentinel to the two tests above, which isolate one
// index each.
func TestABuyerWhoRegistersAgainOnTheSameMachineCannotBeConfirmedTwice(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-again")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, acct, "inst-sem-r1", "act-sem-p", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-p", "lic-sem-p", []byte(`{"v":1}`), base); err != nil {
		t.Fatalf("confirm the first purchase: %v", err)
	}

	// The buyer taps "I have ordered" a second time. The pending index is
	// free, so this row is filed and accepted.
	insertPendingRequest(t, r, acct, "inst-sem-r1", "act-sem-q", base.Add(time.Hour))
	_, err := r.ConfirmActivationRequest(ctx, "act-sem-q", "lic-sem-q", []byte(`{"v":2}`), base.Add(time.Hour))
	if err == nil {
		t.Fatal("the second registration on a licensed machine was confirmed")
	}
	if !errors.Is(err, store.ErrConflict) && !errors.Is(err, store.ErrLiveBindingExists) {
		t.Fatalf("want a store sentinel, got %v", err)
	}
	// Which one it is depends on the order in which PostgreSQL checks the two
	// partial indexes, and both are violated here. Recorded so a change in
	// that order is visible in the log rather than silently flipping the
	// seller's advice from "reissue" to "409".
	//
	// Today the machine-scoped index is reported first, because it is the
	// older of the two and PostgreSQL checks them in creation order — so the
	// seller gets ErrLiveBindingExists and the admin service answers "this
	// machine already holds a live licence, reissue it". That is the right
	// advice for this case, but it is the right advice by ACCIDENT: nothing
	// in the schema declares that the machine-scoped index is checked first,
	// and reordering the two CREATE INDEX statements in the migration would
	// turn this into a plain 409 — the "somebody beat you to it, refresh and
	// try again" answer that can never become true. A negative control
	// confirms this test cannot see the difference (the account-scoped index
	// fires and the observable state is the same), which is why the two
	// per-index tests above exist and why neither of them lets the other
	// index into the picture.
	t.Logf("the same-account-same-machine collision was reported as: %v", err)

	if n := r.queryInt(t, `SELECT count(*) FROM licence_bindings
		WHERE account_id = $1 AND revoked_at IS NULL`, acct); n != 1 {
		t.Fatalf("live bindings for the account = %d, want 1", n)
	}
	req, err := r.GetActivationRequest(ctx, "act-sem-q")
	if err != nil {
		t.Fatalf("read the request back: %v", err)
	}
	if req.Status != store.ActivationPending {
		t.Fatalf("the tombstone row was answered: status = %q", req.Status)
	}
}

// ---------------------------------------------------------------------------
// 3. The conditional UPDATE guards
// ---------------------------------------------------------------------------

// TestConfirmingAnAlreadyConfirmedRequestChangesNothing is the guard
// question: how many rows did the guarded UPDATE actually touch, and what
// happened to the row it did not touch.
//
// confirmFlipSQL is WHERE request_id = $1 AND status = 'pending'. On a row
// that is no longer pending it touches ZERO rows, and the Store reads that
// zero as ErrConflict — "somebody else got there first", not "nothing
// happened".
//
// The bytes of the winning licence must survive untouched. ECDSA picks a
// random k per signature, so re-signing would hand the same licence a
// different token on every poll; the envelope was written once and stays
// written.
//
// A note on what actually answers this call, because it is not the obvious
// thing: the conflict is raised by the STATUS CHECK under the row lock, not
// by confirmFlipSQL's own `AND status = 'pending'`. The flip is never
// reached — the row is already confirmed, so the transaction returns before
// it writes anything. The flip's guard is the second line of defence, for
// the case where the state changed through a path that bypassed the lock,
// and it is measured directly by
// TestThePendingGuardIsWhatZeroesTheSecondAnswer.
func TestConfirmingAnAlreadyConfirmedRequestChangesNothing(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-double-confirm")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)
	envelope := []byte(`{"v":1,"sig":"AAAA"}`)

	insertPendingRequest(t, r, acct, "inst-sem-d1", "act-sem-r", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-r", "lic-sem-r", envelope, base); err != nil {
		t.Fatalf("confirm: %v", err)
	}

	_, err := r.ConfirmActivationRequest(ctx, "act-sem-r", "lic-sem-r2", []byte(`{"v":1,"sig":"BBBB"}`), base.Add(time.Hour))
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict, got %v", err)
	}

	got, err := r.GetActivationRequest(ctx, "act-sem-r")
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	if got.Status != store.ActivationConfirmed {
		t.Fatalf("status = %q, want confirmed", got.Status)
	}
	if got.LicenseID != "lic-sem-r" {
		t.Fatalf("license id = %q, want lic-sem-r", got.LicenseID)
	}
	if string(got.EnvelopeJSON) != string(envelope) {
		t.Fatalf("the envelope was rewritten:\n got %s\nwant %s", got.EnvelopeJSON, envelope)
	}
	if got.ConfirmedAt == nil || !got.ConfirmedAt.Equal(base) {
		t.Fatalf("confirmed_at = %v, want %v", got.ConfirmedAt, base)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM licence_bindings WHERE request_id = $1`, "act-sem-r"); n != 1 {
		t.Fatalf("bindings for the request = %d, want 1", n)
	}
}

// TestRejectingAnAlreadyConfirmedRequestChangesNothing is the other
// direction of the same guard, and it is a business rule, not a race.
//
// A seller pressing refuse on a request that was already signed is not
// losing a race; they are trying to undo an issued licence by the wrong
// button. Refusing an already-issued licence is a conflict, and the answer
// is "revoke the licence" (RevokeBinding takes a licence id, not a request
// id) — not "reject".
//
// The rejected_at column must stay NULL: a row that says "rejected" and
// "confirmed" at the same time is a ledger that answers two questions with
// two different truths.
func TestRejectingAnAlreadyConfirmedRequestChangesNothing(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-reject-confirmed")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, acct, "inst-sem-d2", "act-sem-s", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-s", "lic-sem-s", []byte(`{"v":1}`), base); err != nil {
		t.Fatalf("confirm: %v", err)
	}

	_, err := r.RejectActivationRequest(ctx, "act-sem-s", base.Add(time.Hour))
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict, got %v", err)
	}

	got, err := r.GetActivationRequest(ctx, "act-sem-s")
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	if got.Status != store.ActivationConfirmed {
		t.Fatalf("status = %q, want confirmed", got.Status)
	}
	if got.RejectedAt != nil {
		t.Fatalf("rejected_at = %v, want NULL", got.RejectedAt)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM licence_bindings WHERE revoked_at IS NOT NULL`); n != 0 {
		t.Fatalf("a refused request revoked %d licences; refusing an order is not a revocation", n)
	}
}

// TestRejectingAnAlreadyRejectedRequestChangesNothing closes the state
// machine: rejected is terminal, and the flip guard is what keeps it
// terminal.
//
// The rejection date must not move either. A ledger that re-dated the
// refusal on every second press would lose the fact that the seller answered
// at a particular moment.
func TestRejectingAnAlreadyRejectedRequestChangesNothing(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-double-reject")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, acct, "inst-sem-d3", "act-sem-t", base)
	if _, err := r.RejectActivationRequest(ctx, "act-sem-t", base); err != nil {
		t.Fatalf("reject: %v", err)
	}

	_, err := r.RejectActivationRequest(ctx, "act-sem-t", base.Add(time.Hour))
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("want store.ErrConflict, got %v", err)
	}

	got, err := r.GetActivationRequest(ctx, "act-sem-t")
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	if got.Status != store.ActivationRejected {
		t.Fatalf("status = %q, want rejected", got.Status)
	}
	if got.RejectedAt == nil || !got.RejectedAt.Equal(base) {
		t.Fatalf("rejected_at = %v, want %v", got.RejectedAt, base)
	}
}

// TestThePendingGuardIsWhatZeroesTheSecondAnswer measures the guard
// directly, by running the Store's own statement and reading the row count.
//
// This is the question no mock could answer: how many rows did the guarded
// UPDATE actually touch? The test runs it twice — once against a row that is
// no longer pending (zero rows) and once against the same row with the
// predicate removed (one row) — so it demonstrates that the zero comes from
// the `AND status = 'pending'` clause and not from something else, such as
// the request id not matching.
//
// Without that clause, two sellers pressing the same button would each
// overwrite the other's licence id and envelope, and the buyer's poller
// would see a licence that flips under it.
func TestThePendingGuardIsWhatZeroesTheSecondAnswer(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-guard")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, acct, "inst-sem-g1", "act-sem-u", base)
	if _, err := r.RejectActivationRequest(ctx, "act-sem-u", base); err != nil {
		t.Fatalf("reject: %v", err)
	}

	// The guarded statement, on a row that is not pending: zero rows.
	tag, err := r.db.Exec(ctx, confirmFlipSQL, "act-sem-u", "lic-sem-u", []byte(`{"v":1}`), base.Add(time.Hour))
	if err != nil {
		t.Fatalf("guarded flip: %v", err)
	}
	if tag.RowsAffected() != 0 {
		t.Fatalf("the guarded flip touched %d rows, want 0", tag.RowsAffected())
	}
	still, err := r.GetActivationRequest(ctx, "act-sem-u")
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	if still.Status != store.ActivationRejected {
		t.Fatalf("the guarded flip changed the row: status = %q", still.Status)
	}

	// The same statement with the predicate removed: one row. This is the
	// counterfactual that makes the zero above meaningful — it shows the
	// guard, not the request id, is what stopped the write.
	tag, err = r.db.Exec(ctx, `UPDATE activation_requests
		SET status = 'confirmed', license_id = $2, envelope_json = $3, confirmed_at = $4
		WHERE request_id = $1`, "act-sem-u", "lic-sem-u", []byte(`{"v":1}`), base.Add(time.Hour))
	if err != nil {
		t.Fatalf("unguarded flip: %v", err)
	}
	if tag.RowsAffected() != 1 {
		t.Fatalf("the unguarded flip touched %d rows, want 1 — the test cannot show what the guard does", tag.RowsAffected())
	}
}

// ---------------------------------------------------------------------------
// 4. The FOR UPDATE lock: two sellers, one licence
// ---------------------------------------------------------------------------

// TestTwoSellersRacingOnOneRequestProduceExactlyOneLicence is the reason
// confirmLockSQL takes a row lock.
//
// Two sellers looking at the same pending row and both pressing confirm is
// not a corner case — it is what happens when one of them leaves the console
// open. Exactly one licence may come out: the licence is money, and the
// buyer's poller reads a single envelope.
//
// The loser's answer is ErrConflict and nothing else. The winner's row must
// be intact: its licence id and its envelope are the ones that were signed,
// because ECDSA picks a random k per signature and a re-sign would hand the
// same licence a different token on every poll.
func TestTwoSellersRacingOnOneRequestProduceExactlyOneLicence(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-race")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, acct, "inst-sem-race", "act-sem-race", base)

	// Widen the window in which the two sellers overlap.
	//
	// Without this, the race is a race in name only: the two goroutines are
	// released together but the first one can finish before the second one
	// has even issued its statement, and then the second one simply reads a
	// confirmed row and gets ErrConflict for a reason that has nothing to do
	// with the lock. A test that passes for the wrong reason is worse than no
	// test, because it keeps passing after the lock is removed.
	//
	// The trigger makes every binding insert take a moment, so both
	// transactions are genuinely in flight at the same time. It is a
	// stand-in for the real thing — a confirm is a network round trip and a
	// signature, and it is not instantaneous in production either.
	r.exec(t, `CREATE FUNCTION race_slow() RETURNS trigger AS $$
BEGIN
    PERFORM pg_sleep(0.3);
    RETURN NEW;
END;
$$ LANGUAGE plpgsql`)
	r.exec(t, `CREATE TRIGGER race_slow_binding
    BEFORE INSERT ON licence_bindings
    FOR EACH ROW EXECUTE FUNCTION race_slow()`)

	envelopes := [][]byte{[]byte(`{"who":"a"}`), []byte(`{"who":"b"}`)}
	licenses := []string{"lic-sem-race-a", "lic-sem-race-b"}

	start := make(chan struct{})
	errs := make([]error, 2)
	var wg sync.WaitGroup
	for i := 0; i < 2; i++ {
		wg.Add(1)
		go func(i int) {
			defer wg.Done()
			<-start
			_, errs[i] = r.ConfirmActivationRequest(ctx, "act-sem-race", licenses[i], envelopes[i], base)
		}(i)
	}
	close(start)
	wg.Wait()

	winners, losers := 0, 0
	for i, err := range errs {
		switch {
		case err == nil:
			winners++
		case errors.Is(err, store.ErrConflict):
			losers++
		default:
			t.Fatalf("seller %d got %v, want nil or store.ErrConflict", i, err)
		}
	}
	if winners != 1 || losers != 1 {
		t.Fatalf("winners = %d, losers = %d; want exactly one of each", winners, losers)
	}

	if n := r.queryInt(t, `SELECT count(*) FROM licence_bindings WHERE install_id = $1`, "inst-sem-race"); n != 1 {
		t.Fatalf("bindings for the machine = %d, want exactly 1", n)
	}

	got, err := r.GetActivationRequest(ctx, "act-sem-race")
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	if got.Status != store.ActivationConfirmed {
		t.Fatalf("status = %q, want confirmed", got.Status)
	}
	// The row must carry the winner's licence AND the winner's envelope. A
	// half-state — the licence id from one seller and the bytes from the
	// other — would be a licence that verifies on nobody's machine.
	var winner int
	if string(got.EnvelopeJSON) == string(envelopes[0]) {
		winner = 0
	} else if string(got.EnvelopeJSON) == string(envelopes[1]) {
		winner = 1
	} else {
		t.Fatalf("the stored envelope is neither seller's: %s", got.EnvelopeJSON)
	}
	if got.LicenseID != licenses[winner] {
		t.Fatalf("license id = %q, want %q (the envelope belongs to the other seller)", got.LicenseID, licenses[winner])
	}
	bind := r.queryStrings(t, `SELECT license_id FROM licence_bindings WHERE install_id = $1`, "inst-sem-race")
	if len(bind) != 1 || bind[0] != licenses[winner] {
		t.Fatalf("the binding names %v, want [%s]", bind, licenses[winner])
	}
}

// ---------------------------------------------------------------------------
// 5. Reissue
// ---------------------------------------------------------------------------

// TestReissueRetiresTheOldLicenceAndConfirmsThePendingRequest is the
// machine-change path with an open registration.
//
// A reissue is two facts in one transaction: the old binding is retired and
// the new one is installed. Either both happened or neither did. The old
// binding is not deleted — it is dated, because the honest answer to "is this
// licence still valid" is a date, not a deletion.
//
// The pending request is what the buyer's poller is watching, so the reissue
// confirms THAT row rather than filing a new one: the reissue has to be
// visible to the client that is waiting for it.
func TestReissueRetiresTheOldLicenceAndConfirmsThePendingRequest(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-reissue-pending")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)
	newEnvelope := []byte(`{"v":2,"sig":"NEW"}`)

	// The earlier purchase, with its live licence.
	insertPendingRequest(t, r, acct, "inst-sem-rs1", "act-sem-old", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-old", "lic-sem-old", []byte(`{"v":1}`), base); err != nil {
		t.Fatalf("confirm the first purchase: %v", err)
	}
	// The buyer registered again; that row is the one the poller waits on.
	insertPendingRequest(t, r, acct, "inst-sem-rs1", "act-sem-new", base.Add(time.Hour))

	got, err := r.ReissueLicence(ctx, "inst-sem-rs1", "lic-sem-new-license", newEnvelope, base.Add(2*time.Hour))
	if err != nil {
		t.Fatalf("reissue: %v", err)
	}
	if got.RequestID != "act-sem-new" {
		t.Fatalf("the reissue confirmed %q, want the pending row act-sem-new", got.RequestID)
	}
	if got.Status != store.ActivationConfirmed {
		t.Fatalf("status = %q, want confirmed", got.Status)
	}
	if got.LicenseID != "lic-sem-new-license" {
		t.Fatalf("license id = %q, want lic-sem-new-license", got.LicenseID)
	}
	if string(got.EnvelopeJSON) != string(newEnvelope) {
		t.Fatalf("envelope = %s, want %s", got.EnvelopeJSON, newEnvelope)
	}

	// The old binding is dated, not deleted.
	if n := r.queryInt(t, `SELECT count(*) FROM licence_bindings WHERE license_id = $1`, "lic-sem-old"); n != 1 {
		t.Fatal("the old binding row was deleted; a revocation is a date, not a deletion")
	}
	revoked := bindingRevokedAt(t, r, "lic-sem-old")
	if revoked == nil {
		t.Fatal("the old binding was not revoked")
	}
	if !revoked.Equal(base.Add(2 * time.Hour)) {
		t.Fatalf("revoked_at = %v, want %v", revoked, base.Add(2*time.Hour))
	}

	// The old REQUEST row keeps its own history. A reissue does not rewrite
	// the past: the row that produced the old licence still names that
	// licence and still carries that licence's bytes, because the buyer may
	// still be holding it and the ledger has to be able to explain it.
	old, err := r.GetActivationRequest(ctx, "act-sem-old")
	if err != nil {
		t.Fatalf("read the old request back: %v", err)
	}
	if old.Status != store.ActivationConfirmed || old.LicenseID != "lic-sem-old" {
		t.Fatalf("the old request was rewritten: %q/%q", old.Status, old.LicenseID)
	}
	if string(old.EnvelopeJSON) != `{"v":1}` {
		t.Fatalf("the old envelope was rewritten: %s", old.EnvelopeJSON)
	}

	// The new binding is live and points at the request it was produced by.
	binding, err := r.LiveBindingByInstallID(ctx, "inst-sem-rs1")
	if err != nil {
		t.Fatalf("read the live binding: %v", err)
	}
	if binding.LicenseID != "lic-sem-new-license" || binding.RequestID != "act-sem-new" {
		t.Fatalf("the live binding is %s/%s, want lic-sem-new-license/act-sem-new",
			binding.LicenseID, binding.RequestID)
	}

	// And the buyer's poller can actually find the reissue. A licence nobody
	// can poll for is a licence nobody collects.
	latest, err := r.LatestActivationRequest(ctx, acct)
	if err != nil {
		t.Fatalf("latest: %v", err)
	}
	if latest.RequestID != "act-sem-new" {
		t.Fatalf("the poller waits on %q, want act-sem-new", latest.RequestID)
	}
	if string(latest.EnvelopeJSON) != string(newEnvelope) {
		t.Fatalf("the poller reads %s, want %s", latest.EnvelopeJSON, newEnvelope)
	}
}

// TestReissueWithoutAPendingRequestFilesItsOwnConfirmedRow is the other
// half of the reissue: the machine has a licence but no open registration.
//
// The reissue then has to file a request row of its own, or the buyer would
// have a licence and no way of learning about it. The new row copies the
// machine's earlier registration — anchor and amount — so the seller's
// console keeps showing what was bought.
func TestReissueWithoutAPendingRequestFilesItsOwnConfirmedRow(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-reissue-fresh")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)
	newEnvelope := []byte(`{"v":2,"sig":"NEW"}`)

	insertPendingRequest(t, r, acct, "inst-sem-rs2", "act-sem-prev", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-prev", "lic-sem-prev", []byte(`{"v":1}`), base); err != nil {
		t.Fatalf("confirm the first purchase: %v", err)
	}
	// The earlier registration's details, which the fresh row must copy.
	r.exec(t, `UPDATE activation_requests SET anchor_digest = $2, amount_fen = $3 WHERE request_id = $1`,
		"act-sem-prev", "anchor-abc", 9900)

	got, err := r.ReissueLicence(ctx, "inst-sem-rs2", "lic-sem-fresh", newEnvelope, base.Add(time.Hour))
	if err != nil {
		t.Fatalf("reissue: %v", err)
	}
	if got.RequestID == "" {
		t.Fatal("the reissue filed no request row; the buyer's poller would never learn about it")
	}
	if got.RequestID == "act-sem-prev" {
		t.Fatal("the reissue reused the confirmed row instead of filing its own")
	}
	if got.Status != store.ActivationConfirmed {
		t.Fatalf("status = %q, want confirmed", got.Status)
	}
	if got.LicenseID != "lic-sem-fresh" {
		t.Fatalf("license id = %q, want lic-sem-fresh", got.LicenseID)
	}
	if string(got.EnvelopeJSON) != string(newEnvelope) {
		t.Fatalf("envelope = %s, want %s", got.EnvelopeJSON, newEnvelope)
	}
	if got.AnchorDigest != "anchor-abc" {
		t.Fatalf("anchor = %q, want the earlier registration's anchor", got.AnchorDigest)
	}
	if got.AmountFen == nil || *got.AmountFen != 9900 {
		t.Fatalf("amount = %v, want 9900", got.AmountFen)
	}

	// The row really is in the table, and the binding points at it.
	if n := r.queryInt(t, `SELECT count(*) FROM activation_requests WHERE request_id = $1 AND status = 'confirmed'`,
		got.RequestID); n != 1 {
		t.Fatal("the fresh confirmed row is not in the table")
	}
	binding, err := r.LiveBindingByInstallID(ctx, "inst-sem-rs2")
	if err != nil {
		t.Fatalf("read the live binding: %v", err)
	}
	if binding.RequestID != got.RequestID {
		t.Fatalf("the binding points at %q, want %q", binding.RequestID, got.RequestID)
	}
	if bindingRevokedAt(t, r, "lic-sem-prev") == nil {
		t.Fatal("the old binding was not retired")
	}
}

// TestReissueForAMachineNeverSeenIsNotFound is the boundary of the reissue.
//
// ErrNotFound means there is nothing to reissue: no live binding and no
// request of any kind for that machine. The seller is looking at a machine
// code this service has never heard of, and a reissue that invented a
// licence out of thin air would be a support call nobody can explain.
//
// The negative half matters as much: the failed reissue must not have
// written anything. A reissue that half-succeeded would leave a licence
// nobody paid for.
func TestReissueForAMachineNeverSeenIsNotFound(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	_, err := r.ReissueLicence(ctx, "inst-sem-unknown", "lic-sem-unknown", []byte(`{"v":1}`), base)
	if !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM licence_bindings`); n != 0 {
		t.Fatalf("the failed reissue wrote %d bindings, want 0", n)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM activation_requests`); n != 0 {
		t.Fatalf("the failed reissue wrote %d requests, want 0", n)
	}
}

// ---------------------------------------------------------------------------
// 6. Revoke
// ---------------------------------------------------------------------------

// TestRevokeKeepsTheRowAndDatesIt is the shape of a revocation.
//
// The row stays. "Is this licence still valid" is answered by a date, not by
// the absence of a row, because a ledger that deletes what it revoked cannot
// answer "when was this licence revoked" or "who held it" — and the seller
// quotes licence ids in support conversations.
func TestRevokeKeepsTheRowAndDatesIt(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-revoke")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, acct, "inst-sem-rv1", "act-sem-w", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-w", "lic-sem-w", []byte(`{"v":1}`), base); err != nil {
		t.Fatalf("confirm: %v", err)
	}

	revokedAt := base.Add(time.Hour)
	if err := r.RevokeBinding(ctx, "lic-sem-w", revokedAt); err != nil {
		t.Fatalf("revoke: %v", err)
	}

	if n := r.queryInt(t, `SELECT count(*) FROM licence_bindings WHERE license_id = $1`, "lic-sem-w"); n != 1 {
		t.Fatal("the revoked binding row was deleted")
	}
	got := bindingRevokedAt(t, r, "lic-sem-w")
	if got == nil || !got.Equal(revokedAt) {
		t.Fatalf("revoked_at = %v, want %v", got, revokedAt)
	}
	if _, err := r.LiveBindingByInstallID(ctx, "inst-sem-rv1"); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("the machine still holds a live licence: %v", err)
	}
	// The ledger page still shows it: everything ever issued, newest first.
	rows, err := r.ListBindings(ctx)
	if err != nil {
		t.Fatalf("list bindings: %v", err)
	}
	if len(rows) != 1 || rows[0].LicenseID != "lic-sem-w" {
		t.Fatalf("the ledger lost the revoked licence: %+v", rows)
	}
}

// TestRevokingTwiceDoesNotMoveTheDate is the idempotence of a revocation.
//
// Revoking twice is a misclick on a stale console. It succeeds — the second
// call finds nothing to change and the licence stays revoked — but the
// recorded date stays the FIRST one. A revocation date that moves forward
// every time a stale console is refreshed would misreport when the licence
// actually died, which is the one thing the date is for.
func TestRevokingTwiceDoesNotMoveTheDate(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-revoke-twice")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, acct, "inst-sem-rv2", "act-sem-x", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-x", "lic-sem-x", []byte(`{"v":1}`), base); err != nil {
		t.Fatalf("confirm: %v", err)
	}

	first := base.Add(time.Hour)
	if err := r.RevokeBinding(ctx, "lic-sem-x", first); err != nil {
		t.Fatalf("first revoke: %v", err)
	}
	second := base.Add(48 * time.Hour)
	if err := r.RevokeBinding(ctx, "lic-sem-x", second); err != nil {
		t.Fatalf("second revoke: %v", err)
	}

	got := bindingRevokedAt(t, r, "lic-sem-x")
	if got == nil {
		t.Fatal("the licence is not revoked")
	}
	if !got.Equal(first) {
		t.Fatalf("revoked_at = %v, want the first date %v", got, first)
	}
}

// TestARevocationDatedBeforeTheIssueIsRefused is the CHECK constraint
// underneath the date.
//
// licence_bindings_revoked_is_dated: revoked_at IS NULL OR revoked_at >=
// issued_at. A revocation dated before the licence existed is a bug in
// whatever wrote it, not something to store — and storing it would make the
// ledger claim a licence was dead before it was born.
//
// The row must survive the refusal untouched: a rejected revocation is not a
// revocation.
func TestARevocationDatedBeforeTheIssueIsRefused(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-sem-revoke-backwards")
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	insertPendingRequest(t, r, acct, "inst-sem-rv3", "act-sem-y", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-y", "lic-sem-y", []byte(`{"v":1}`), base); err != nil {
		t.Fatalf("confirm: %v", err)
	}

	err := r.RevokeBinding(ctx, "lic-sem-y", base.Add(-time.Hour))
	if err == nil {
		t.Fatal("a revocation dated before the licence was issued was accepted")
	}
	if got := bindingRevokedAt(t, r, "lic-sem-y"); got != nil {
		t.Fatalf("revoked_at = %v, want NULL — the refused revocation must not have written", got)
	}
	if _, err := r.LiveBindingByInstallID(ctx, "inst-sem-rv3"); err != nil {
		t.Fatalf("the licence is no longer live after a refused revocation: %v", err)
	}
}

// TestRevokingALicenceThatNeverExistedIsNotFound is the other half of the
// revoke's zero-rows case.
//
// Zero rows updated is two different stories — already revoked, or never
// existed — and they get different answers. Revoking twice is a misclick on
// a stale console and succeeds; revoking a licence this service never issued
// is a 404, because the seller quoted an id that means nothing here.
func TestRevokingALicenceThatNeverExistedIsNotFound(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	err := r.RevokeBinding(ctx, "lic-sem-never", base)
	if !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

// ---------------------------------------------------------------------------
// 7. The implicit order of the console's table
// ---------------------------------------------------------------------------

// TestTheConsoleListIsNewestFirst pins the ORDER BY.
//
// The seller's console lists the requests, and the newest one is the one the
// buyer is waiting about; a list sorted the other way makes the seller work
// from the bottom of the page. The sort lives in the query, not in the
// client, so it is the query's ORDER BY that has to be right.
//
// The rows have DISTINCT created_at values on purpose. The order of rows
// with EQUAL keys is genuinely unspecified in SQL, and a test that pinned it
// would be pinning an accident of the current plan — it would fail the day
// PostgreSQL chose a different index, with nothing having actually broken.
func TestTheConsoleListIsNewestFirst(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	acctA := seedAccount(t, r, "open-sem-order-a")
	acctB := seedAccount(t, r, "open-sem-order-b")
	acctC := seedAccount(t, r, "open-sem-order-c")

	insertPendingRequest(t, r, acctA, "inst-sem-o1", "act-sem-oldest", base)
	insertPendingRequest(t, r, acctB, "inst-sem-o2", "act-sem-middle", base.Add(time.Hour))
	insertPendingRequest(t, r, acctC, "inst-sem-o3", "act-sem-newest", base.Add(2*time.Hour))

	rows, err := r.ListActivationRequests(ctx, "")
	if err != nil {
		t.Fatalf("list: %v", err)
	}
	want := []string{"act-sem-newest", "act-sem-middle", "act-sem-oldest"}
	if len(rows) != len(want) {
		t.Fatalf("rows = %d, want %d", len(rows), len(want))
	}
	for i, id := range want {
		if rows[i].RequestID != id {
			t.Fatalf("row %d is %q, want %q (the list is not newest-first)", i, rows[i].RequestID, id)
		}
	}

	// The filtered list keeps the same order — the status filter narrows
	// the set, it does not reorder it.
	rows, err = r.ListActivationRequests(ctx, store.ActivationPending)
	if err != nil {
		t.Fatalf("list pending: %v", err)
	}
	if len(rows) != len(want) {
		t.Fatalf("pending rows = %d, want %d", len(rows), len(want))
	}
	for i, id := range want {
		if rows[i].RequestID != id {
			t.Fatalf("pending row %d is %q, want %q", i, rows[i].RequestID, id)
		}
	}
}

// TestTheConsoleListFiltersByStatus is the filter half of the same query.
//
// The console has a "pending" tab and a ledger of everything that was
// answered. A filter that leaked the other status would put unconfirmed
// requests in the seller's queue — the seller would then confirm a request
// that had already been rejected.
func TestTheConsoleListFiltersByStatus(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	acctA := seedAccount(t, r, "open-sem-filter-a")
	acctB := seedAccount(t, r, "open-sem-filter-b")

	insertPendingRequest(t, r, acctA, "inst-sem-f1", "act-sem-pending", base)
	insertPendingRequest(t, r, acctB, "inst-sem-f2", "act-sem-confirmed", base.Add(time.Hour))
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-confirmed", "lic-sem-f", []byte(`{"v":1}`), base.Add(time.Hour)); err != nil {
		t.Fatalf("confirm: %v", err)
	}

	rows, err := r.ListActivationRequests(ctx, store.ActivationPending)
	if err != nil {
		t.Fatalf("list pending: %v", err)
	}
	if len(rows) != 1 || rows[0].RequestID != "act-sem-pending" {
		t.Fatalf("pending rows = %+v, want only act-sem-pending", rows)
	}

	rows, err = r.ListActivationRequests(ctx, store.ActivationConfirmed)
	if err != nil {
		t.Fatalf("list confirmed: %v", err)
	}
	if len(rows) != 1 || rows[0].RequestID != "act-sem-confirmed" {
		t.Fatalf("confirmed rows = %+v, want only act-sem-confirmed", rows)
	}
}

// TestTheBindingLedgerIsNewestFirst is the same ordering question on the
// other table the seller reads.
//
// The ledger page is "everything this account holds, newest first", and it
// is the page a seller consults when a buyer says "I paid but the app says
// no licence" — the newest row is the one they are asking about.
func TestTheBindingLedgerIsNewestFirst(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	base := time.Date(2026, 10, 20, 12, 0, 0, 0, time.UTC)

	acctA := seedAccount(t, r, "open-sem-ledger-a")
	acctB := seedAccount(t, r, "open-sem-ledger-b")

	insertPendingRequest(t, r, acctA, "inst-sem-l1", "act-sem-l1", base)
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-l1", "lic-sem-l1", []byte(`{"v":1}`), base); err != nil {
		t.Fatalf("confirm the first licence: %v", err)
	}
	insertPendingRequest(t, r, acctB, "inst-sem-l2", "act-sem-l2", base.Add(time.Hour))
	if _, err := r.ConfirmActivationRequest(ctx, "act-sem-l2", "lic-sem-l2", []byte(`{"v":2}`), base.Add(time.Hour)); err != nil {
		t.Fatalf("confirm the second licence: %v", err)
	}

	rows, err := r.ListBindings(ctx)
	if err != nil {
		t.Fatalf("list bindings: %v", err)
	}
	if len(rows) != 2 {
		t.Fatalf("rows = %d, want 2", len(rows))
	}
	if rows[0].LicenseID != "lic-sem-l2" || rows[1].LicenseID != "lic-sem-l1" {
		t.Fatalf("ledger order = %s, %s; want lic-sem-l2, lic-sem-l1",
			rows[0].LicenseID, rows[1].LicenseID)
	}
}

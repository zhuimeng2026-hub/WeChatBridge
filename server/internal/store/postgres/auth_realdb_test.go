package postgres

// Real-database tests for the sign-in path: the auth sessions, the one-time
// handoff, the account upsert and the token chain.
//
// Every property in this file is decided by the database and merely asked
// about by the Go code: a conditional DELETE that may or may not match a row,
// a DELETE ... RETURNING that has to be read exactly once, a row lock that
// decides which of two callers wins, and two nullable hash columns whose NULL
// is a designed state rather than a missing value.
//
// The pgxmock suite can only check the shape of a statement. It cannot see
// that a guarded DELETE touched zero rows instead of one, that a second reader
// found a row the first reader had already deleted, or that scanning a NULL
// column into the wrong Go type turns a sign-in into a 503. Those are exactly
// the failures this file exists to catch, and they are not hypothetical: the
// last four fixes on this branch were all of this class.
//
// One naming note, because it is a trap for anyone reading the schema later.
// The token chain's two hash columns live on the ACCOUNTS row, not on
// auth_sessions. auth_sessions is the QR ticket and has no token columns at
// all; the "one live session per account" convergence moved the chain onto
// accounts. The NULL-hash family below is therefore about
// accounts.refresh_token_hash and accounts.prev_refresh_token_hash.

import (
	"context"
	"errors"
	"sync"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// ---------------------------------------------------------------------------
// The handoff — read-once, and the row really goes away
// ---------------------------------------------------------------------------

// TestTakeAuthHandoffIsReadOnce is the security property of the whole
// sign-in bridge.
//
// The handoff is what carries the plaintext tokens from the instance that
// served the WeChat redirect to the instance that serves the client's poll.
// It is read-once by construction: the statement is DELETE ... RETURNING, so
// the credentials exist on exactly one row and that row is consumed by the
// first reader. A second reader must find nothing.
//
// This is the one place in the service where a usable token sits in the
// database, so "read once" is not an optimisation — it is the property that
// makes a leaked database dump a set of expired credentials rather than a set
// of live ones. And it is exactly the kind of property pgxmock cannot check:
// the mock answers whatever the fake was written to answer, and the fake
// implements "DELETE ... RETURNING" by returning the row it was told to
// return, every time.
func TestTakeAuthHandoffIsReadOnce(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-handoff-once")

	if err := r.PutAuthHandoff(ctx, store.AuthHandoff{
		Ticket:          "ticket-once",
		AccessToken:     "access-plaintext",
		RefreshToken:    "refresh-plaintext",
		AccessExpiresAt: time.Now().UTC().Add(time.Hour),
		AccountID:       acct,
		ExpiresAt:       time.Now().UTC().Add(time.Hour),
	}); err != nil {
		t.Fatalf("put handoff: %v", err)
	}

	got, ok, err := r.TakeAuthHandoff(ctx, "ticket-once")
	if err != nil {
		t.Fatalf("first take: %v", err)
	}
	if !ok {
		t.Fatal("the first take found nothing; the handoff was never written")
	}
	if got.AccessToken != "access-plaintext" || got.RefreshToken != "refresh-plaintext" {
		t.Fatalf("the handoff lost the credentials: %+v", got)
	}
	if got.AccountID != acct {
		t.Fatalf("AccountID = %q, want %q", got.AccountID, acct)
	}

	// The second take is the attacker's take, or the client's retry after a
	// response that never arrived. Either way it must come back empty.
	again, ok2, err2 := r.TakeAuthHandoff(ctx, "ticket-once")
	if err2 != nil {
		t.Fatalf("the second take failed loudly instead of answering empty: %v", err2)
	}
	if ok2 {
		t.Fatalf("the second take handed out the credentials again: %+v", again)
	}
	if again.AccessToken != "" || again.RefreshToken != "" {
		t.Fatalf("a refused take still returned credentials: %+v", again)
	}
}

// TestTakeAuthHandoffDeletesTheRowItConsumed separates "the row is gone" from
// "the row is marked as taken".
//
// The two are indistinguishable to a reader that only looks at the returned
// bool, and they are not equally safe. A flagged row still holds the
// plaintext tokens in the table — every future reader has to be trusted to
// honour the flag, and a dump of the table still contains live credentials
// for as long as the row survives. A deleted row holds nothing.
//
// The proof that the row was deleted rather than flagged is that the same
// ticket can be written again: a row that merely carried a flag would still
// be there, and the second write would fail on the primary key.
func TestTakeAuthHandoffDeletesTheRowItConsumed(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-handoff-delete")

	put := func(access, refresh string) {
		t.Helper()
		if err := r.PutAuthHandoff(ctx, store.AuthHandoff{
			Ticket:          "ticket-delete",
			AccessToken:     access,
			RefreshToken:    refresh,
			AccessExpiresAt: time.Now().UTC().Add(time.Hour),
			AccountID:       acct,
			ExpiresAt:       time.Now().UTC().Add(time.Hour),
		}); err != nil {
			t.Fatalf("put handoff: %v", err)
		}
	}

	put("access-1", "refresh-1")
	if _, ok, err := r.TakeAuthHandoff(ctx, "ticket-delete"); err != nil || !ok {
		t.Fatalf("first take: ok=%v err=%v", ok, err)
	}

	if n := r.queryInt(t, `SELECT count(*) FROM auth_handoffs WHERE ticket = $1`, "ticket-delete"); n != 0 {
		t.Fatalf("%d rows left for a consumed ticket; the row was flagged, not deleted", n)
	}

	// The row is really gone, so the same ticket can be written again. If it
	// had only been flagged, this write would fail on the primary key.
	put("access-2", "refresh-2")
	got, ok, err := r.TakeAuthHandoff(ctx, "ticket-delete")
	if err != nil || !ok {
		t.Fatalf("second round: ok=%v err=%v", ok, err)
	}
	if got.AccessToken != "access-2" {
		t.Fatalf("the second round handed out the first round's credentials: %+v", got)
	}
}

// TestTakeAuthHandoffLeavesOtherTicketsAlone is the blast radius of the
// read-once delete.
//
// The statement deletes by ticket, and a bug that turned it into "delete the
// oldest handoff" or "delete everything for this account" would still pass a
// test that only looks at one ticket. Two live handoffs, one taken, one
// untouched.
func TestTakeAuthHandoffLeavesOtherTicketsAlone(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-handoff-neighbours")

	for _, ticket := range []string{"ticket-a", "ticket-b"} {
		if err := r.PutAuthHandoff(ctx, store.AuthHandoff{
			Ticket:          ticket,
			AccessToken:     "access-" + ticket,
			RefreshToken:    "refresh-" + ticket,
			AccessExpiresAt: time.Now().UTC().Add(time.Hour),
			AccountID:       acct,
			ExpiresAt:       time.Now().UTC().Add(time.Hour),
		}); err != nil {
			t.Fatalf("put %s: %v", ticket, err)
		}
	}

	if _, ok, err := r.TakeAuthHandoff(ctx, "ticket-a"); err != nil || !ok {
		t.Fatalf("take ticket-a: ok=%v err=%v", ok, err)
	}

	other, ok, err := r.TakeAuthHandoff(ctx, "ticket-b")
	if err != nil || !ok {
		t.Fatalf("ticket-b was taken by ticket-a's take: ok=%v err=%v", ok, err)
	}
	if other.AccessToken != "access-ticket-b" {
		t.Fatalf("ticket-b carries the wrong credentials: %+v", other)
	}
}

// TestPutAuthHandoffSweepsTheExpiredRows is what keeps the table from growing
// for the life of the deployment.
//
// A scan that nobody ever collected leaves its row behind forever. The sweep
// runs inside PutAuthHandoff — before the insert, so it never deletes the row
// it is about to write — and it is the only thing that reclaims the rows of
// scans that were completed and abandoned.
func TestPutAuthHandoffSweepsTheExpiredRows(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-handoff-sweep")

	// An abandoned scan: written, and its ticket expires without anybody
	// polling for it.
	if err := r.PutAuthHandoff(ctx, store.AuthHandoff{
		Ticket:          "ticket-abandoned",
		AccessToken:     "access-old",
		RefreshToken:    "refresh-old",
		AccessExpiresAt: time.Now().UTC().Add(-time.Minute),
		AccountID:       acct,
		ExpiresAt:       time.Now().UTC().Add(-time.Minute),
	}); err != nil {
		t.Fatalf("put the abandoned handoff: %v", err)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM auth_handoffs WHERE ticket = $1`, "ticket-abandoned"); n != 1 {
		t.Fatalf("the abandoned handoff was not written; the sweep ran before the insert")
	}

	// The next sign-in sweeps it.
	if err := r.PutAuthHandoff(ctx, store.AuthHandoff{
		Ticket:          "ticket-fresh",
		AccessToken:     "access-new",
		RefreshToken:    "refresh-new",
		AccessExpiresAt: time.Now().UTC().Add(time.Hour),
		AccountID:       acct,
		ExpiresAt:       time.Now().UTC().Add(time.Hour),
	}); err != nil {
		t.Fatalf("put the fresh handoff: %v", err)
	}

	if n := r.queryInt(t, `SELECT count(*) FROM auth_handoffs WHERE ticket = $1`, "ticket-abandoned"); n != 0 {
		t.Fatalf("the expired handoff survived the sweep; the table grows for the life of the deployment")
	}
	if n := r.queryInt(t, `SELECT count(*) FROM auth_handoffs WHERE ticket = $1`, "ticket-fresh"); n != 1 {
		t.Fatalf("the fresh handoff was swept; the sweep must not touch the row it just wrote")
	}
}

// ---------------------------------------------------------------------------
// The auth session — the state guard on the delete
// ---------------------------------------------------------------------------

// TestDeleteAuthSessionRemovesAPendingTicket is the ordinary case: a ticket
// nobody will finish with is retired, and the caller is told a row went away.
func TestDeleteAuthSessionRemovesAPendingTicket(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	mustSeedAuthSession(t, r, "ticket-pending", "inst-1", time.Now().UTC().Add(10*time.Minute))

	removed, err := r.DeleteAuthSession(ctx, "ticket-pending")
	if err != nil {
		t.Fatalf("delete: %v", err)
	}
	if !removed {
		t.Fatal("the pending ticket was not removed")
	}
	if n := r.queryInt(t, `SELECT count(*) FROM auth_sessions WHERE ticket = $1`, "ticket-pending"); n != 0 {
		t.Fatalf("%d rows left for a deleted ticket", n)
	}
}

// TestDeleteAuthSessionRefusesAnAuthorisedTicket is the guard.
//
// The state guard lives in the SQL rather than in Go on purpose, and the
// reason is a race: the caller is evicting the oldest of several live tickets
// without holding a lock against the WeChat callback, and a ticket that has
// been authorised in the meantime is a sign-in somebody completed and may not
// have polled for yet. A Go check followed by a DELETE are two statements, and
// the authorisation can land between them. Deleting an authorised session is
// the one outcome worse than the row living a few more minutes: the client is
// left at a QR code that will never resolve, with no way to tell why.
//
// So the guard has to hold against the real database, not against a Go
// intention. This is the test that says the row survives.
func TestDeleteAuthSessionRefusesAnAuthorisedTicket(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-delete-guard")
	mustSeedAuthSession(t, r, "ticket-authorised", "inst-1", time.Now().UTC().Add(10*time.Minute))

	if _, err := r.CompleteAuthSession(ctx, "ticket-authorised", "open-delete-guard", "union-open-delete-guard", "Nick", "", "inst-1"); err != nil {
		t.Fatalf("complete: %v", err)
	}

	removed, err := r.DeleteAuthSession(ctx, "ticket-authorised")
	if err != nil {
		t.Fatalf("delete: %v", err)
	}
	if removed {
		t.Fatal("the delete reported that it removed an authorised session")
	}

	state, accountID := authSessionRow(t, r, "ticket-authorised")
	if state != string(store.AuthAuthorized) {
		t.Fatalf("state = %q, want %q — the authorised session was deleted", state, store.AuthAuthorized)
	}
	if accountID != acct {
		t.Fatalf("account_id = %q, want %q — the authorised session lost its account", accountID, acct)
	}
}

// TestDeleteAuthSessionReportsAMissForAnUnknownTicket keeps the two answers
// apart. "Nothing was there" is a normal answer, not a failure, and the
// caller uses it to decide whether it had anything to clean up.
func TestDeleteAuthSessionReportsAMissForAnUnknownTicket(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	removed, err := r.DeleteAuthSession(ctx, "ticket-never-existed")
	if err != nil {
		t.Fatalf("delete: %v", err)
	}
	if removed {
		t.Fatal("the delete reported that it removed a ticket that never existed")
	}
}

// ---------------------------------------------------------------------------
// Completing a session — atomicity, account resolution, and the ticket's
// one-time property
// ---------------------------------------------------------------------------

// TestCompleteAuthSessionCreatesTheAccountForANewOpenID is the first
// sign-in: the ticket is spent and the account comes into being, in one
// transaction.
func TestCompleteAuthSessionCreatesTheAccountForANewOpenID(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	mustSeedAuthSession(t, r, "ticket-new", "inst-new", time.Now().UTC().Add(10*time.Minute))

	acct, err := r.CompleteAuthSession(ctx, "ticket-new", "open-new", "union-new", "Nick", "https://wx.qlogo.cn/a/0", "inst-new")
	if err != nil {
		t.Fatalf("complete: %v", err)
	}
	if acct.ID == "" {
		t.Fatal("the completion returned no account")
	}
	if acct.OpenID != "open-new" || acct.Nickname != "Nick" {
		t.Fatalf("the account did not come back with the profile the callback carried: %+v", acct)
	}

	state, accountID := authSessionRow(t, r, "ticket-new")
	if state != string(store.AuthAuthorized) {
		t.Fatalf("state = %q, want %q", state, store.AuthAuthorized)
	}
	if accountID != acct.ID {
		t.Fatalf("the session names account %q, want %q", accountID, acct.ID)
	}
}

// TestCompleteAuthSessionResolvesTheSameAccountOnAReturnVisit is what makes
// the account the identity and the ticket the event.
//
// The second sign-in of the same person must land on the same account row.
// If it created a second one, the person would have two accounts, two licence
// bindings and two polling clients, and the seller would be looking at two
// buyers where there is one.
func TestCompleteAuthSessionResolvesTheSameAccountOnAReturnVisit(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	mustSeedAuthSession(t, r, "ticket-first", "inst-1", time.Now().UTC().Add(10*time.Minute))
	first, err := r.CompleteAuthSession(ctx, "ticket-first", "open-return", "union-return", "Nick", "", "inst-1")
	if err != nil {
		t.Fatalf("first completion: %v", err)
	}

	mustSeedAuthSession(t, r, "ticket-second", "inst-2", time.Now().UTC().Add(10*time.Minute))
	second, err := r.CompleteAuthSession(ctx, "ticket-second", "open-return", "union-return", "Nick Two", "", "inst-2")
	if err != nil {
		t.Fatalf("second completion: %v", err)
	}

	if second.ID != first.ID {
		t.Fatalf("the return visit produced a second account: %q then %q", first.ID, second.ID)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM accounts WHERE open_id = $1`, "open-return"); n != 1 {
		t.Fatalf("%d account rows for one open id", n)
	}
}

// TestCompleteAuthSessionKeepsTheInstallIDOfTheSession pins the identity
// inheritance.
//
// The ticket remembers the anonymous identity the client had before it ever
// signed in, and that identity is what ties a purchase made before signing in
// to the account it belongs to. A caller that supplies no install id must not
// be allowed to choose one, and a caller that supplies one must not be able
// to overwrite the session's — the row is the authority.
func TestCompleteAuthSessionKeepsTheInstallIDOfTheSession(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	mustSeedAuthSession(t, r, "ticket-install", "inst-from-ticket", time.Now().UTC().Add(10*time.Minute))

	acct, err := r.CompleteAuthSession(ctx, "ticket-install", "open-install", "union-install", "Nick", "", "")
	if err != nil {
		t.Fatalf("complete: %v", err)
	}
	if acct.InstallID != "inst-from-ticket" {
		t.Fatalf("InstallID = %q, want %q — the session's anonymous identity was lost", acct.InstallID, "inst-from-ticket")
	}
}

// TestCompleteAuthSessionRefusesASpentTicket is the one-time property of the
// QR ticket, and it is a conflict rather than a miss: the row is right there,
// it has simply already been used.
//
// The distinction matters to the caller. A miss says "that ticket does not
// exist"; a conflict says "you are replaying a ticket that worked". The
// service answers the second with "this QR code has already been used" and
// the first with "start again", and they are different words for different
// events.
func TestCompleteAuthSessionRefusesASpentTicket(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	mustSeedAuthSession(t, r, "ticket-spent", "inst-1", time.Now().UTC().Add(10*time.Minute))

	if _, err := r.CompleteAuthSession(ctx, "ticket-spent", "open-spent", "union-spent", "Nick", "", "inst-1"); err != nil {
		t.Fatalf("first completion: %v", err)
	}

	_, err := r.CompleteAuthSession(ctx, "ticket-spent", "open-spent", "union-spent", "Nick", "", "inst-1")
	if !errors.Is(err, store.ErrConflict) {
		t.Fatalf("replaying a spent ticket: err = %v, want store.ErrConflict", err)
	}
}

// TestCompleteAuthSessionLeavesNoAccountForAnUnknownTicket is the other half
// of atomicity: a completion that cannot find its ticket must not leave an
// account behind.
//
// The account resolution and the ticket flip are one transaction because each
// one alone is a lie the other cannot repair. Here the failure is before
// anything is written, and the point is that there is still nothing written.
func TestCompleteAuthSessionLeavesNoAccountForAnUnknownTicket(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	_, err := r.CompleteAuthSession(ctx, "ticket-nowhere", "open-nowhere", "union-nowhere", "Nick", "", "inst-1")
	if !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("completing an unknown ticket: err = %v, want store.ErrNotFound", err)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM accounts WHERE open_id = $1`, "open-nowhere"); n != 0 {
		t.Fatalf("%d account rows were created for a ticket that does not exist", n)
	}
}

// TestCompleteAuthSessionLeavesNoPartialStateWhenTheAccountUpsertFails is
// the case the transaction exists for.
//
// The account resolution happens BEFORE the ticket flip, so a failure in the
// middle leaves the question: did the account get created? If the transaction
// were not one, the answer would be "yes, and the ticket is still pending" —
// a person who signed in, with an account, whose QR code never resolves.
//
// The failure is provoked with a real constraint rather than a mock: a second
// account claiming the same union id violates a partial unique index. That
// is the kind of failure that only a real database can produce, and it lands
// in the middle of the transaction, after the ticket has been locked.
func TestCompleteAuthSessionLeavesNoPartialStateWhenTheAccountUpsertFails(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	// The union id is taken by somebody else. The partial unique index
	// accounts_union_id_key refuses the second claim.
	seedAccount(t, r, "open-owner")

	mustSeedAuthSession(t, r, "ticket-conflict", "inst-1", time.Now().UTC().Add(10*time.Minute))
	_, err := r.CompleteAuthSession(ctx, "ticket-conflict", "open-claimer", "union-open-owner", "Nick", "", "inst-1")
	if err == nil {
		t.Fatal("the completion accepted a union id that is already taken")
	}

	// The account must not exist: the upsert was rolled back with the
	// transaction.
	if n := r.queryInt(t, `SELECT count(*) FROM accounts WHERE open_id = $1`, "open-claimer"); n != 0 {
		t.Fatalf("%d account rows survived a failed completion", n)
	}
	// And the ticket must still be pending, with no account named: an
	// authorised session without an account is the lie the schema forbids.
	state, accountID := authSessionRow(t, r, "ticket-conflict")
	if state != string(store.AuthPending) {
		t.Fatalf("state = %q, want %q — a failed completion flipped the ticket", state, store.AuthPending)
	}
	if accountID != "" {
		t.Fatalf("account_id = %q, want empty — a failed completion left the ticket pointing at an account", accountID)
	}
}

// TestCompleteAuthSessionLeavesNoAccountWhenTheSessionCannotBeAuthorised is
// the rollback, in the only shape that can actually exercise it.
//
// The account is resolved BEFORE the ticket is flipped, so the failure that
// matters is the one that comes after the resolution: the account exists, the
// ticket is still pending, and the person who signed in has an account whose
// sign-in never completes. A failure BEFORE the first write — a ticket that
// does not exist, an account that cannot be created — proves nothing about
// the rollback, because there is nothing to roll back.
//
// So the failure is provoked inside the transaction, on the flip itself, by a
// constraint the test adds to its own throwaway schema. The constraint is
// added NOT VALID, because the ticket has to exist before the constraint
// can refuse to be flipped.
func TestCompleteAuthSessionLeavesNoAccountWhenTheSessionCannotBeAuthorised(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	mustSeedAuthSession(t, r, "ticket-flip-refused", "inst-1", time.Now().UTC().Add(10*time.Minute))
	r.exec(t, `ALTER TABLE auth_sessions ADD CONSTRAINT refuse_this_flip
		CHECK (state <> 'authorized' OR ticket <> 'ticket-flip-refused') NOT VALID`)

	_, err := r.CompleteAuthSession(ctx, "ticket-flip-refused", "open-partial", "union-partial", "Nick", "", "inst-1")
	if err == nil {
		t.Fatal("the completion reported success although the flip was refused")
	}

	// The account was created before the flip, so the rollback is the only
	// thing that can have removed it.
	if n := r.queryInt(t, `SELECT count(*) FROM accounts WHERE open_id = $1`, "open-partial"); n != 0 {
		t.Fatalf("%d account rows survived a failed flip; the transaction is not atomic", n)
	}
	state, accountID := authSessionRow(t, r, "ticket-flip-refused")
	if state != string(store.AuthPending) {
		t.Fatalf("state = %q, want %q", state, store.AuthPending)
	}
	if accountID != "" {
		t.Fatalf("account_id = %q, want empty", accountID)
	}
}

// TestCompleteAuthSessionAuthorisesATicketExactlyOnce is the row lock.
//
// Two polling clients, or a client and a replayed redirect, can reach the
// same ticket at the same moment. The FOR UPDATE in the lock statement is
// what makes "check the state, then change it" one operation rather than
// two, and the row count of the conditional UPDATE is what says the flip
// actually happened.
//
// This is the classic case a mock cannot judge: the mock has no rows, so it
// has no locks, so "only one caller wins" is a property of the fake.
func TestCompleteAuthSessionAuthorisesATicketExactlyOnce(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	mustSeedAuthSession(t, r, "ticket-race", "inst-race", time.Now().UTC().Add(10*time.Minute))

	const racers = 4
	type outcome struct {
		acct store.Account
		err  error
	}
	results := make(chan outcome, racers)
	start := make(chan struct{})
	var wg sync.WaitGroup
	for i := 0; i < racers; i++ {
		wg.Add(1)
		go func() {
			defer wg.Done()
			<-start
			acct, err := r.CompleteAuthSession(ctx, "ticket-race", "open-race", "union-race", "Nick", "", "inst-race")
			results <- outcome{acct, err}
		}()
	}
	close(start)
	wg.Wait()
	close(results)

	wins, conflicts := 0, 0
	for res := range results {
		switch {
		case res.err == nil:
			wins++
		case errors.Is(res.err, store.ErrConflict):
			conflicts++
		default:
			t.Fatalf("unexpected error from a racer: %v", res.err)
		}
	}
	if wins != 1 {
		t.Fatalf("%d racers completed the ticket, want exactly 1", wins)
	}
	if conflicts != racers-1 {
		t.Fatalf("%d racers got a conflict, want %d", conflicts, racers-1)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM accounts WHERE open_id = $1`, "open-race"); n != 1 {
		t.Fatalf("%d account rows for one open id after a race", n)
	}
}

// TestTheSchemaRefusesAnAuthorisedSessionWithoutAnAccount is the constraint
// that makes a half-finished write impossible rather than merely unlikely.
//
// The store writes the state and the account id in one statement, so it
// cannot produce this row. The constraint is what says so even if some future
// writer, or a manual fix, tries: a session that claims to be authorised
// while naming nobody is a lie the polling client would happily believe.
func TestTheSchemaRefusesAnAuthorisedSessionWithoutAnAccount(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)

	// The Store cannot produce this row, so the fixture is written by hand.
	// r.exec fails the test on error, so the constraint violation is caught
	// by a direct Exec instead.
	_, err := r.db.Exec(context.Background(), `
		INSERT INTO auth_sessions (ticket, state, install_id, account_id, expires_at)
		VALUES ('ticket-lie', 'authorized', 'inst-1', NULL, now() + interval '10 minutes')`)
	if err == nil {
		t.Fatal("the schema accepted an authorised session that names no account")
	}
}

// ---------------------------------------------------------------------------
// The account upsert
// ---------------------------------------------------------------------------

// TestUpsertAccountByOpenIDReportsCreationOnce pins the answer the caller
// uses to tell a first sign-in from a return visit.
//
// The answer comes from xmax = 0 in the RETURNING list, which is a fact about
// the heap tuple rather than about a second query, and it is the kind of
// thing a mock can only ever agree with.
func TestUpsertAccountByOpenIDReportsCreationOnce(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	first, created, err := r.UpsertAccountByOpenID(ctx, "open-upsert", "union-upsert", "Nick", "https://wx.qlogo.cn/a/0")
	if err != nil {
		t.Fatalf("first upsert: %v", err)
	}
	if !created {
		t.Fatal("the first upsert did not report a creation")
	}

	second, created2, err := r.UpsertAccountByOpenID(ctx, "open-upsert", "union-upsert", "Nick", "https://wx.qlogo.cn/a/0")
	if err != nil {
		t.Fatalf("second upsert: %v", err)
	}
	if created2 {
		t.Fatal("the return visit reported a creation")
	}
	if second.ID != first.ID {
		t.Fatalf("the return visit produced a second account: %q then %q", first.ID, second.ID)
	}
	if n := r.queryInt(t, `SELECT count(*) FROM accounts WHERE open_id = $1`, "open-upsert"); n != 1 {
		t.Fatalf("%d account rows for one open id", n)
	}
}

// TestUpsertAccountByOpenIDUpdatesTheProfileOnAReturnVisit is the reason the
// upsert is an upsert and not a plain insert.
//
// A person who changed their nickname or their picture must see the change,
// and the refresh path (which re-derives the profile from WeChat on every
// sign-in) is what carries it.
func TestUpsertAccountByOpenIDUpdatesTheProfileOnAReturnVisit(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	if _, _, err := r.UpsertAccountByOpenID(ctx, "open-profile", "", "Old Nick", "https://wx.qlogo.cn/old"); err != nil {
		t.Fatalf("first upsert: %v", err)
	}
	acct, created, err := r.UpsertAccountByOpenID(ctx, "open-profile", "union-profile", "New Nick", "https://wx.qlogo.cn/new")
	if err != nil {
		t.Fatalf("second upsert: %v", err)
	}
	if created {
		t.Fatal("the return visit reported a creation")
	}
	if acct.Nickname != "New Nick" {
		t.Fatalf("Nickname = %q, want %q", acct.Nickname, "New Nick")
	}
	if acct.AvatarURL != "https://wx.qlogo.cn/new" {
		t.Fatalf("AvatarURL = %q, want %q", acct.AvatarURL, "https://wx.qlogo.cn/new")
	}
	// The union id is filled in on the return visit: it is only known once
	// the open platform has been asked.
	if acct.UnionID != "union-profile" {
		t.Fatalf("UnionID = %q, want %q", acct.UnionID, "union-profile")
	}
}

// TestUpsertAccountByOpenIDKeepsTheProfileWhenTheNewOneIsEmpty is the
// reason the UPDATE half is a CASE rather than a plain assignment.
//
// The token-refresh path re-derives the account with an empty nickname and
// an empty avatar URL, because the refresh does not ask WeChat for the
// profile. A plain assignment would wipe the stored nickname and the stored
// picture on every single refresh, and the sign-in would appear to lose the
// user's name a minute after they typed it.
func TestUpsertAccountByOpenIDKeepsTheProfileWhenTheNewOneIsEmpty(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	if _, _, err := r.UpsertAccountByOpenID(ctx, "open-keep", "union-keep", "Nick", "https://wx.qlogo.cn/keep"); err != nil {
		t.Fatalf("first upsert: %v", err)
	}
	acct, _, err := r.UpsertAccountByOpenID(ctx, "open-keep", "", "", "")
	if err != nil {
		t.Fatalf("second upsert: %v", err)
	}
	if acct.Nickname != "Nick" {
		t.Fatalf("Nickname = %q, want %q — an empty profile wiped the stored one", acct.Nickname, "Nick")
	}
	if acct.AvatarURL != "https://wx.qlogo.cn/keep" {
		t.Fatalf("AvatarURL = %q, want the stored one — an empty profile wiped the stored one", acct.AvatarURL)
	}
	if acct.UnionID != "union-keep" {
		t.Fatalf("UnionID = %q, want %q", acct.UnionID, "union-keep")
	}
}

// TestUpsertAccountByOpenIDNeverDemotesAPaidAccount is the one column the
// upsert is forbidden to write.
//
// Signing in must not demote somebody who paid. The plan is insert-only: the
// UPDATE half does not mention it, so an account that a seller upgraded stays
// upgraded through every sign-in the buyer makes.
func TestUpsertAccountByOpenIDNeverDemotesAPaidAccount(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	acct, _, err := r.UpsertAccountByOpenID(ctx, "open-paid", "union-paid", "Nick", "")
	if err != nil {
		t.Fatalf("first upsert: %v", err)
	}
	// The seller upgrades the account.
	r.exec(t, `UPDATE accounts SET plan = 'buyout' WHERE id = $1`, acct.ID)

	again, _, err := r.UpsertAccountByOpenID(ctx, "open-paid", "union-paid", "Nick", "")
	if err != nil {
		t.Fatalf("second upsert: %v", err)
	}
	if again.Plan != store.PlanBuyout {
		t.Fatalf("Plan = %q, want %q — signing in demoted a paying customer", again.Plan, store.PlanBuyout)
	}
	if p := r.queryStrings(t, `SELECT plan FROM accounts WHERE id = $1`, acct.ID); p[0] != "buyout" {
		t.Fatalf("the stored plan is %q, want buyout", p[0])
	}
}

// TestUpsertAccountByOpenIDRefusesAnEmptyOpenID is the guard on the identity
// itself.
//
// An empty open id would collide with every other empty open id on the unique
// index, turning "WeChat said nothing" into "somebody else's account". The
// error has to happen before the statement, because the statement itself
// would succeed.
func TestUpsertAccountByOpenIDRefusesAnEmptyOpenID(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	if _, _, err := r.UpsertAccountByOpenID(ctx, "", "union-x", "Nick", ""); err == nil {
		t.Fatal("the upsert accepted an empty open id")
	}
	if n := r.queryInt(t, `SELECT count(*) FROM accounts`); n != 0 {
		t.Fatalf("%d account rows exist after an empty open id was refused", n)
	}
}

// ---------------------------------------------------------------------------
// The token chain — the NULL hash family
// ---------------------------------------------------------------------------

// TestCreateSessionLeavesThePreviousHashNull is the first half of the NULL
// family.
//
// A fresh sign-in has no previous generation, and "no previous generation" is
// what a NULL column means. The statement says so explicitly
// (prev_refresh_token_hash = NULL) rather than leaving the column at
// whatever the account's previous session left there — a second sign-in
// replaces the chain, and the old chain's hash must not linger as a token the
// server would still accept.
func TestCreateSessionLeavesThePreviousHashNull(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-chain-null")

	if err := r.CreateSession(ctx, store.Session{
		AccountID:        acct,
		RefreshTokenHash: "hash-live-1",
		RefreshExpiresAt: time.Now().UTC().Add(24 * time.Hour),
		ChainID:          "chain-1",
		CreatedAt:        time.Now().UTC(),
	}); err != nil {
		t.Fatalf("create session: %v", err)
	}

	live, prev, prevNull := accountHashes(t, r, acct)
	if live != "hash-live-1" {
		t.Fatalf("refresh_token_hash = %q, want %q", live, "hash-live-1")
	}
	if !prevNull {
		t.Fatalf("prev_refresh_token_hash = %q, want NULL", prev)
	}
}

// TestGetSessionByRefreshHashReadsASessionThatHasNotRefreshedYet is the
// production bug, in the shape it actually took.
//
// A client that had just signed in and had not refreshed yet presented its
// token, and got a 503. The reason was a column: prev_refresh_token_hash is
// NULL for exactly that client, and the scan read it as a plain string. A
// plain string cannot hold NULL, so the read failed — and a read failure on
// the refresh path is a 503, not a 404, because the store could not tell the
// client whether the token was unknown.
//
// pgxmock could not see this, and could not have: a mock skips SQL NULL
// assignments, including invalid NULL-to-string scans, so its "successful"
// read was a read of a row it had fabricated with a non-null placeholder. The
// real driver is the only thing that can show that this row reads cleanly.
func TestGetSessionByRefreshHashReadsASessionThatHasNotRefreshedYet(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-chain-read")

	expires := time.Now().UTC().Add(24 * time.Hour)
	if err := r.CreateSession(ctx, store.Session{
		AccountID:        acct,
		RefreshTokenHash: "hash-live-1",
		RefreshExpiresAt: expires,
		ChainID:          "chain-1",
		CreatedAt:        time.Now().UTC(),
	}); err != nil {
		t.Fatalf("create session: %v", err)
	}

	sess, err := r.GetSessionByRefreshHash(ctx, "hash-live-1")
	if err != nil {
		t.Fatalf("reading a session that has not refreshed yet: %v", err)
	}
	if sess.AccountID != acct {
		t.Fatalf("AccountID = %q, want %q", sess.AccountID, acct)
	}
	if sess.RefreshTokenHash != "hash-live-1" {
		t.Fatalf("RefreshTokenHash = %q, want the presented hash", sess.RefreshTokenHash)
	}
	if sess.ChainID != "chain-1" {
		t.Fatalf("ChainID = %q, want %q", sess.ChainID, "chain-1")
	}
	if sess.RevokedAt != nil {
		t.Fatalf("RevokedAt = %v, want nil", sess.RevokedAt)
	}
	if !sess.RefreshExpiresAt.Equal(expires.Truncate(time.Microsecond)) {
		t.Fatalf("RefreshExpiresAt = %v, want %v", sess.RefreshExpiresAt, expires)
	}
}

// TestRotateRefreshWorksOnASessionThatHasNotRefreshedYet is the other half
// of the same bug.
//
// The fix for the read landed in the lookup and missed the rotation, and the
// client kept getting 503 from this half: the error only changed its
// wording. The two halves shared a scanner precisely so that a fix could not
// land in one and miss the other — and this is the test that says the shared
// scanner really is shared.
func TestRotateRefreshWorksOnASessionThatHasNotRefreshedYet(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-chain-rotate-null")

	expires := time.Now().UTC().Add(24 * time.Hour)
	if err := r.CreateSession(ctx, store.Session{
		AccountID:        acct,
		RefreshTokenHash: "hash-live-1",
		RefreshExpiresAt: expires,
		ChainID:          "chain-1",
		CreatedAt:        time.Now().UTC(),
	}); err != nil {
		t.Fatalf("create session: %v", err)
	}

	sess, outcome, err := r.RotateRefresh(ctx, "hash-live-1", "hash-live-2", "access-2", time.Now().UTC().Add(time.Hour))
	if err != nil {
		t.Fatalf("rotating a session whose previous hash is NULL: %v", err)
	}
	if outcome != store.RefreshRotated {
		t.Fatalf("outcome = %q, want %q", outcome, store.RefreshRotated)
	}
	if sess.AccountID != acct {
		t.Fatalf("AccountID = %q, want %q", sess.AccountID, acct)
	}
	if sess.RefreshTokenHash != "hash-live-2" {
		t.Fatalf("RefreshTokenHash = %q, want the replacement", sess.RefreshTokenHash)
	}
	if sess.ChainID != "chain-1" {
		t.Fatalf("ChainID = %q, want %q — the rotation changed the chain", sess.ChainID, "chain-1")
	}
}

// TestRotateRefreshMovesTheOldHashIntoThePreviousColumn is the column-level
// assertion.
//
// The reuse signal only exists if the replaced hash is kept. If the rotation
// dropped it, a replayed token would look like an unknown one, and the
// caller's answer would be "clear your session" instead of "kill the family" —
// the difference between a stolen token costing one session and costing the
// legitimate user their account.
//
// The assertion is on the raw columns rather than on the returned struct,
// because the returned struct is the API and the columns are the fact.
func TestRotateRefreshMovesTheOldHashIntoThePreviousColumn(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-chain-columns")

	if err := r.CreateSession(ctx, store.Session{
		AccountID:        acct,
		RefreshTokenHash: "hash-live-1",
		RefreshExpiresAt: time.Now().UTC().Add(24 * time.Hour),
		ChainID:          "chain-1",
		CreatedAt:        time.Now().UTC(),
	}); err != nil {
		t.Fatalf("create session: %v", err)
	}

	if _, outcome, err := r.RotateRefresh(ctx, "hash-live-1", "hash-live-2", "access-2", time.Now().UTC().Add(time.Hour)); err != nil || outcome != store.RefreshRotated {
		t.Fatalf("rotate: outcome=%q err=%v", outcome, err)
	}

	live, prev, prevNull := accountHashes(t, r, acct)
	if live != "hash-live-2" {
		t.Fatalf("refresh_token_hash = %q, want %q", live, "hash-live-2")
	}
	if prevNull {
		t.Fatal("prev_refresh_token_hash is still NULL after a rotation; the reuse signal was thrown away")
	}
	if prev != "hash-live-1" {
		t.Fatalf("prev_refresh_token_hash = %q, want %q", prev, "hash-live-1")
	}
}

// TestRotateRefreshKeepsTheOriginalExpiry is the "sign in again every ninety
// days" promise.
//
// The replacement inherits the old token's expiry rather than receiving a
// new one. A client that refreshes every hour would otherwise hold the chain
// alive forever, and the rule that a session must be re-established
// periodically would be a rule nobody could enforce.
func TestRotateRefreshKeepsTheOriginalExpiry(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-chain-expiry")

	expires := time.Now().UTC().Add(24 * time.Hour)
	if err := r.CreateSession(ctx, store.Session{
		AccountID:        acct,
		RefreshTokenHash: "hash-live-1",
		RefreshExpiresAt: expires,
		ChainID:          "chain-1",
		CreatedAt:        time.Now().UTC(),
	}); err != nil {
		t.Fatalf("create session: %v", err)
	}

	sess, _, err := r.RotateRefresh(ctx, "hash-live-1", "hash-live-2", "access-2", time.Now().UTC().Add(time.Hour))
	if err != nil {
		t.Fatalf("rotate: %v", err)
	}
	if !sess.RefreshExpiresAt.Equal(expires.Truncate(time.Microsecond)) {
		t.Fatalf("RefreshExpiresAt = %v, want the inherited %v", sess.RefreshExpiresAt, expires.Truncate(time.Microsecond))
	}
	live, _, _ := accountHashes(t, r, acct)
	if live != "hash-live-2" {
		t.Fatalf("refresh_token_hash = %q, want %q", live, "hash-live-2")
	}
}

// TestGetSessionByRefreshHashStillFindsTheConsumedToken is what lets the
// caller tell a replay from an unknown token.
//
// The lookup matches either generation. A replayed token must be FOUND — so
// that the rotation path can recognise it and answer with the reuse signal —
// while a token the server never issued must be a miss. If the lookup only
// matched the live hash, a replay would look like a typo, and the caller's
// answer to a typo and to a thief would have to be the same.
func TestGetSessionByRefreshHashStillFindsTheConsumedToken(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-chain-find-old")

	if err := r.CreateSession(ctx, store.Session{
		AccountID:        acct,
		RefreshTokenHash: "hash-live-1",
		RefreshExpiresAt: time.Now().UTC().Add(24 * time.Hour),
		ChainID:          "chain-1",
		CreatedAt:        time.Now().UTC(),
	}); err != nil {
		t.Fatalf("create session: %v", err)
	}
	if _, _, err := r.RotateRefresh(ctx, "hash-live-1", "hash-live-2", "access-2", time.Now().UTC().Add(time.Hour)); err != nil {
		t.Fatalf("rotate: %v", err)
	}

	sess, err := r.GetSessionByRefreshHash(ctx, "hash-live-1")
	if err != nil {
		t.Fatalf("the consumed token is no longer findable: %v", err)
	}
	if sess.AccountID != acct {
		t.Fatalf("AccountID = %q, want %q", sess.AccountID, acct)
	}
	if sess.RefreshTokenHash != "hash-live-1" {
		t.Fatalf("RefreshTokenHash = %q, want the presented hash", sess.RefreshTokenHash)
	}
}

// TestGetSessionByRefreshHashRejectsAnUnknownToken keeps the miss a miss.
//
// A token the server never issued is not a transient condition: the caller
// has to clear its session, not retry.
func TestGetSessionByRefreshHashRejectsAnUnknownToken(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	seedAccount(t, r, "open-chain-unknown")

	if _, err := r.GetSessionByRefreshHash(ctx, "hash-never-issued"); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("err = %v, want store.ErrNotFound", err)
	}
}

// TestRotateRefreshRejectsTheConsumedToken is the reuse signal.
//
// Presenting a token the chain already replaced is theft or a bug, and both
// want the whole chain gone — so it is its own error rather than a flavour
// of "conflict", because the caller's response is to revoke the family and
// not merely to refuse.
//
// The rotation must also leave the row alone: a rejected replay is not a
// rotation, and the live token must still be the live token.
func TestRotateRefreshRejectsTheConsumedToken(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-chain-reuse")

	if err := r.CreateSession(ctx, store.Session{
		AccountID:        acct,
		RefreshTokenHash: "hash-live-1",
		RefreshExpiresAt: time.Now().UTC().Add(24 * time.Hour),
		ChainID:          "chain-1",
		CreatedAt:        time.Now().UTC(),
	}); err != nil {
		t.Fatalf("create session: %v", err)
	}
	if _, _, err := r.RotateRefresh(ctx, "hash-live-1", "hash-live-2", "access-2", time.Now().UTC().Add(time.Hour)); err != nil {
		t.Fatalf("first rotation: %v", err)
	}

	_, outcome, err := r.RotateRefresh(ctx, "hash-live-1", "hash-live-3", "access-3", time.Now().UTC().Add(time.Hour))
	if !errors.Is(err, store.ErrReuseDetected) {
		t.Fatalf("replaying a consumed token: err = %v, want store.ErrReuseDetected", err)
	}
	if outcome != store.RefreshExpired {
		t.Fatalf("outcome = %q, want %q — the caller's local session must go either way", outcome, store.RefreshExpired)
	}

	live, prev, _ := accountHashes(t, r, acct)
	if live != "hash-live-2" {
		t.Fatalf("refresh_token_hash = %q, want %q — a rejected replay rotated the row anyway", live, "hash-live-2")
	}
	if prev != "hash-live-1" {
		t.Fatalf("prev_refresh_token_hash = %q, want %q", prev, "hash-live-1")
	}
}

// TestRotateRefreshAcceptsTheNewToken is the other end of the rotation: the
// replacement is a working token, not a decoration.
func TestRotateRefreshAcceptsTheNewToken(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-chain-accept-new")

	if err := r.CreateSession(ctx, store.Session{
		AccountID:        acct,
		RefreshTokenHash: "hash-live-1",
		RefreshExpiresAt: time.Now().UTC().Add(24 * time.Hour),
		ChainID:          "chain-1",
		CreatedAt:        time.Now().UTC(),
	}); err != nil {
		t.Fatalf("create session: %v", err)
	}
	if _, _, err := r.RotateRefresh(ctx, "hash-live-1", "hash-live-2", "access-2", time.Now().UTC().Add(time.Hour)); err != nil {
		t.Fatalf("first rotation: %v", err)
	}

	if _, outcome, err := r.RotateRefresh(ctx, "hash-live-2", "hash-live-3", "access-3", time.Now().UTC().Add(time.Hour)); err != nil || outcome != store.RefreshRotated {
		t.Fatalf("rotating the replacement: outcome=%q err=%v", outcome, err)
	}

	live, prev, _ := accountHashes(t, r, acct)
	if live != "hash-live-3" {
		t.Fatalf("refresh_token_hash = %q, want %q", live, "hash-live-3")
	}
	if prev != "hash-live-2" {
		t.Fatalf("prev_refresh_token_hash = %q, want %q — the chain keeps exactly one generation", prev, "hash-live-2")
	}
}

// TestCreateSessionReplacesTheAccountOnlySession is the machine-change path.
//
// One account has one live session. A second sign-in replaces it wholesale —
// including the previous hash, which is cleared rather than left behind,
// because the old chain is over and its token must not remain acceptable.
//
// The fixture rotates the first session before the second sign-in, so that
// the account really does have a previous generation to lose. Without the
// rotation the previous hash was never anything but NULL, and a statement
// that simply stopped clearing it would look exactly right.
func TestCreateSessionReplacesTheAccountOnlySession(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-chain-replace")

	if err := r.CreateSession(ctx, store.Session{
		AccountID:        acct,
		RefreshTokenHash: "hash-machine-1",
		RefreshExpiresAt: time.Now().UTC().Add(24 * time.Hour),
		ChainID:          "chain-1",
		CreatedAt:        time.Now().UTC(),
	}); err != nil {
		t.Fatalf("first session: %v", err)
	}
	// One rotation, so the account now carries a previous generation.
	if _, _, err := r.RotateRefresh(ctx, "hash-machine-1", "hash-machine-2", "access-2", time.Now().UTC().Add(time.Hour)); err != nil {
		t.Fatalf("rotate: %v", err)
	}

	if err := r.CreateSession(ctx, store.Session{
		AccountID:        acct,
		RefreshTokenHash: "hash-machine-3",
		RefreshExpiresAt: time.Now().UTC().Add(24 * time.Hour),
		ChainID:          "chain-2",
		CreatedAt:        time.Now().UTC(),
	}); err != nil {
		t.Fatalf("second session: %v", err)
	}

	live, prev, prevNull := accountHashes(t, r, acct)
	if live != "hash-machine-3" {
		t.Fatalf("refresh_token_hash = %q, want %q", live, "hash-machine-3")
	}
	if !prevNull {
		t.Fatalf("prev_refresh_token_hash = %q, want NULL — the replaced chain's token is still acceptable", prev)
	}
	// Neither the replaced chain's live token nor its previous generation may
	// still resolve: the second sign-in replaced the session, it did not
	// extend it.
	for _, spent := range []string{"hash-machine-1", "hash-machine-2"} {
		if _, err := r.GetSessionByRefreshHash(ctx, spent); !errors.Is(err, store.ErrNotFound) {
			t.Fatalf("the replaced machine's token %q is still accepted: err = %v", spent, err)
		}
	}
}

// TestCreateSessionRejectsAnUnknownAccount keeps the two failures apart.
//
// Minting tokens for an account that does not exist is a bug, not a race, and
// the caller has to be able to tell it from one.
func TestCreateSessionRejectsAnUnknownAccount(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	err := r.CreateSession(ctx, store.Session{
		AccountID:        "acct-does-not-exist",
		RefreshTokenHash: "hash-orphan",
		RefreshExpiresAt: time.Now().UTC().Add(24 * time.Hour),
		ChainID:          "chain-1",
		CreatedAt:        time.Now().UTC(),
	})
	if !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("err = %v, want store.ErrNotFound", err)
	}
}

// TestRevokeChainKillsTheSession pins the family kill switch.
//
// Revoking a chain sets the timestamp; the lookup still finds the row, and
// the row says the family is dead. The rotation path checks it before
// anything else, so a revoked chain stays dead.
func TestRevokeChainKillsTheSession(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-chain-revoke")

	if err := r.CreateSession(ctx, store.Session{
		AccountID:        acct,
		RefreshTokenHash: "hash-live-1",
		RefreshExpiresAt: time.Now().UTC().Add(24 * time.Hour),
		ChainID:          "chain-revoked",
		CreatedAt:        time.Now().UTC(),
	}); err != nil {
		t.Fatalf("create session: %v", err)
	}
	if err := r.RevokeChain(ctx, "chain-revoked", time.Now().UTC()); err != nil {
		t.Fatalf("revoke: %v", err)
	}

	sess, err := r.GetSessionByRefreshHash(ctx, "hash-live-1")
	if err != nil {
		t.Fatalf("read the revoked session: %v", err)
	}
	if sess.RevokedAt == nil {
		t.Fatal("the revoked session reads as live")
	}
	if _, _, err := r.RotateRefresh(ctx, "hash-live-1", "hash-live-2", "access-2", time.Now().UTC().Add(time.Hour)); err == nil {
		t.Fatal("a revoked chain was rotated")
	}
}

// ---------------------------------------------------------------------------
// Fixtures and raw reads
// ---------------------------------------------------------------------------

// mustSeedAuthSession files a pending auth_sessions row.
//
// It goes through the Store, because the Store is the only way a session is
// born, and the row it writes is exactly what CompleteAuthSession later
// locks. A hand-written INSERT would test a ticket that no caller could have
// produced.
func mustSeedAuthSession(t *testing.T, r *realDB, ticket, installID string, expiresAt time.Time) {
	t.Helper()
	if err := r.CreateAuthSession(context.Background(), store.AuthSession{
		Ticket:       ticket,
		State:        store.AuthPending,
		InstallID:    installID,
		AuthorizeURL: "https://wx.example/authorize/" + ticket,
		ExpiresAt:    expiresAt,
		CreatedAt:    time.Now().UTC(),
	}); err != nil {
		t.Fatalf("seed auth session %s: %v", ticket, err)
	}
}

// authSessionRow reads the two columns that decide whether a ticket is still
// alive: its state and the account it names.
//
// account_id is read through coalesce because a pending ticket names no
// account, and "no account" and "the account whose id is the empty string"
// are the same thing to this schema — the column is either NULL or a real
// uuid.
func authSessionRow(t *testing.T, r *realDB, ticket string) (state, accountID string) {
	t.Helper()
	err := r.db.QueryRow(context.Background(),
		`SELECT state, coalesce(account_id, '') FROM auth_sessions WHERE ticket = $1`, ticket).
		Scan(&state, &accountID)
	if err != nil {
		t.Fatalf("read auth session %s: %v", ticket, err)
	}
	return state, accountID
}

// accountHashes reads the two hash columns as they are actually stored.
//
// The NULL is reported separately from the value, because the whole point of
// the column is that it is sometimes NULL and sometimes a hash, and a helper
// that flattened both into "" would have made the distinction invisible in
// exactly the place it matters.
func accountHashes(t *testing.T, r *realDB, accountID string) (live, prev string, prevIsNull bool) {
	t.Helper()
	err := r.db.QueryRow(context.Background(),
		`SELECT coalesce(refresh_token_hash, ''), coalesce(prev_refresh_token_hash, ''),
		        prev_refresh_token_hash IS NULL
		 FROM accounts WHERE id = $1`, accountID).
		Scan(&live, &prev, &prevIsNull)
	if err != nil {
		t.Fatalf("read the token chain of %s: %v", accountID, err)
	}
	return live, prev, prevIsNull
}

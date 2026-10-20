package postgres

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"time"

	"github.com/jackc/pgx/v5"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// ---------------------------------------------------------------------------
// Activation requests — the buy-out product's only online path.
//
// The buyer registers a machine, the seller answers once, and the answer is a
// signed licence pinned to that machine. Two of the four writes here are
// transactions because each one is really two facts: a confirmation is a
// flipped row plus the binding it produced, and a reissue is a retired binding
// plus the one that replaces it.
// ---------------------------------------------------------------------------

// activationRequestColumns is the activation_requests projection. Every query
// that returns a request selects exactly these columns in this order; the
// scanners in scan.go depend on it.
//
// The columns carry no table qualifier because every other query that uses
// this list reads activation_requests alone. The one query that joins accounts
// needs the qualified form below — install_id and created_at exist on both
// tables, and an unqualified reference in a join is an ambiguity error
// (SQLSTATE 42702), not a warning.
const activationRequestColumns = `
	request_id, account_id, install_id, anchor_digest, amount_fen,
	status, envelope_json, license_id, created_at, confirmed_at, rejected_at`

// qualifiedActivationRequestColumns is activationRequestColumns with the `r.`
// prefix this table is joined under. Same columns, same order — the scanners
// cannot tell the difference, and a second projection would only be able to
// drift.
const qualifiedActivationRequestColumns = `
	r.request_id, r.account_id, r.install_id, r.anchor_digest, r.amount_fen,
	r.status, r.envelope_json, r.license_id, r.created_at, r.confirmed_at, r.rejected_at`

// createActivationRequestSQL is the idempotent registration. A machine with a
// live request updates that row instead of queueing a second one on the
// seller's console, but only for the same account: the account predicate is
// what keeps a stranger's machine code from being adopted into somebody
// else's purchase, and a refused predicate falls through to the caller's
// fallback read rather than to an error.
//
// RETURNING is load-bearing, not a convenience. The UPDATE branch deliberately
// keeps the row's original request_id — a caller that echoed back the id it
// minted would hand the customer a 单号 the seller's console has never heard
// of, on exactly the flow where the customer quotes that number for support.
const createActivationRequestSQL = `
	INSERT INTO activation_requests
		(request_id, account_id, install_id, anchor_digest, amount_fen, status, created_at)
	VALUES ($1, $2, $3, $4, $5, 'pending', $6)
	ON CONFLICT (install_id) WHERE status = 'pending'
	DO UPDATE SET
		anchor_digest = EXCLUDED.anchor_digest,
		amount_fen    = EXCLUDED.amount_fen
	WHERE activation_requests.account_id = $2
	RETURNING ` + activationRequestColumns

// latestActivationRequestSQL is the buyer's poller's read: the one request
// this customer should be looking at right now.
//
// "The newest row" is the wrong answer for a buy-out product, and the way it
// is wrong is a production incident (design doc §11.7 M-5). A customer who
// already holds a licence taps "I have ordered" a second time; the
// registration upsert only collides with a PENDING row, so the tap files a
// second, newer row; and "newest wins" then waits on that row forever. The
// licence the customer already paid for sits on an older row, and the
// read never looks at it. The new row is a tombstone: it cannot be confirmed
// (the machine's binding slot is taken) and it shadows the valid licence, so
// there is no self-healing path.
//
// The tie-break is therefore the live licence, not the timestamp.
//
//   - The row whose licence is the machine's LIVE binding wins whenever the
//     account holds one. "Live" means exactly what the licence endpoint means
//     by it — the machine's unrevoked binding names this row's licence — so
//     the row the poller waits on is the row the licence endpoint would hand
//     out. The two reads agree by construction, and the client needs them to.
//
//   - Only when the account holds no live licence at all do the ordinary
//     newest-wins rules apply.
//
// That second clause is what keeps a REVOKED licence honest. A customer whose
// licence was revoked and who registers again must see their new pending
// request, not the dead confirmed row — otherwise the same tombstone comes
// back, this time hiding a purchase that can still be confirmed.
//
// Why the live licence wins at all, rather than merely tie-breaking: this is
// a buy-out. licence_bindings_live_account_idx is UNIQUE(account_id) WHERE
// revoked_at IS NULL, so the schema already says one account holds at most
// one live licence, and there is no payment gateway to bill it twice. A
// second registration is not a second purchase — it cannot become one, since
// the binding insert would violate that same index — so letting it outrank
// the licence the customer already holds would hide the only answer that can
// ever progress.
//
// The first-time buyer is untouched: no confirmed row, no live licence, so
// the fallback applies and their pending request surfaces as it always did.
//
// The (account_id, created_at DESC) index still carries the sort, and both
// liveness probes are index lookups on licence_bindings_live_idx.
//
// The shape of the WHERE, in one line each: the first arm admits the row that
// names the machine's live licence; the second arm admits everything, but
// only for an account that holds no live licence at all.
const latestActivationRequestSQL = `
	SELECT ` + activationRequestColumns + `
	FROM activation_requests
	WHERE account_id = $1
	  AND (
		    (status = 'confirmed' AND license_id = (
		        SELECT b.license_id
		        FROM licence_bindings b
		        WHERE b.install_id = activation_requests.install_id
		          AND b.revoked_at IS NULL
		    ))
		    OR NOT EXISTS (
		        SELECT 1
		        FROM activation_requests c
		        WHERE c.account_id = $1
		          AND c.status = 'confirmed'
		          AND EXISTS (
		              SELECT 1
		              FROM licence_bindings b2
		              WHERE b2.install_id = c.install_id
		                AND b2.license_id = c.license_id
		                AND b2.revoked_at IS NULL
		          )
		    )
	      )
	ORDER BY created_at DESC
	LIMIT 1`

const getActivationRequestSQL = `
	SELECT ` + activationRequestColumns + `
	FROM activation_requests
	WHERE request_id = $1`

// requestByInstallIDSQL is the machine's view, and it is the upsert's fallback
// path: the account predicate refused, and the caller needs the row it refused
// to touch so it can say whose purchase is in flight.
const requestByInstallIDSQL = `
	SELECT ` + activationRequestColumns + `
	FROM activation_requests
	WHERE install_id = $1
	ORDER BY created_at DESC
	LIMIT 1`

// listActivationRequestsSQL is the console's table, one query rather than one
// per row: the account columns the seller reads beside each request come along
// for the join. An empty status means every state, which is what the ledger
// page asks for; the (status, created_at DESC) index carries the filtered
// sort, and at this table's size the unfiltered scan is the same plan.
const listActivationRequestsSQL = `
	SELECT ` + qualifiedActivationRequestColumns + `, a.nickname, a.avatar_url
	FROM activation_requests r
	JOIN accounts a ON a.id = r.account_id
	WHERE $1 = '' OR r.status = $1
	ORDER BY r.created_at DESC`

// confirmLockSQL takes the row lock the conditional flip needs. Without it two
// sellers reading the same pending row could both mint.
const confirmLockSQL = `
	SELECT ` + activationRequestColumns + `
	FROM activation_requests
	WHERE request_id = $1
	FOR UPDATE`

const confirmInsertBindingSQL = `
	INSERT INTO licence_bindings (license_id, install_id, account_id, request_id, issued_at)
	VALUES ($1, $2, $3, $4, $5)`

// confirmFlipSQL is the seller's answer. It is conditional on the row still
// being pending, and the guard is not redundant with the lock: it is the line
// a reviewer greps for, and the line that keeps the answer at one licence if
// the lock is ever traded for a weaker read.
// rejectFlipSQL writes the terminal state and its timestamp together, so the
// database's activation_requests_rejected_is_dated constraint can never be
// made to hold one without the other. Conditional on pending for the same
// reason confirm is: two sellers, one row, one winner.
const rejectFlipSQL = `
	UPDATE activation_requests
		SET status = 'rejected', rejected_at = $2
		WHERE request_id = $1 AND status = 'pending'`

const confirmFlipSQL = `
	UPDATE activation_requests
	SET status = 'confirmed', license_id = $2, envelope_json = $3, confirmed_at = $4
	WHERE request_id = $1 AND status = 'pending'`

// CreateActivationRequest records one purchase registration, idempotently per
// machine while a request for it is live.
//
// The returned row is the database's, never the caller's echo: on the upsert
// path the stored request_id and created_at survive, and the refreshed fields
// are the ones the UPDATE touched. A machine whose live request belongs to
// another account produces no row at all — the predicate refused — and the
// caller is handed that row back so it can refuse the purchase in turn.
func (s *Store) CreateActivationRequest(ctx context.Context, r store.ActivationRequest) (store.ActivationRequest, error) {
	created, err := scanActivationRequest(s.db.QueryRow(ctx, createActivationRequestSQL,
		r.RequestID, r.AccountID, r.InstallID, r.AnchorDigest,
		r.AmountFen, utc(r.CreatedAt)))
	if err == nil {
		return created, nil
	}
	if !errors.Is(err, pgx.ErrNoRows) {
		// The partial unique index absorbed the install collision; what is
		// left is a primary-key collision, which is a caller reusing a minted
		// id across machines.
		return store.ActivationRequest{}, uniqueConflict("create activation request", constraintActivationRequestPK, err)
	}
	// The account predicate refused: the machine's live request belongs to
	// somebody else. Hand that row back rather than inventing a conflict —
	// the caller decides what a stranger's pending purchase means.
	return s.GetRequestByInstallID(ctx, r.InstallID)
}

// LatestActivationRequest is the buyer's own view: the request their poller
// is waiting on. Which row that is, when the account has more than one, is
// the rule the query above spells out — the account's live licence wins over
// a newer registration, and a buyer with no live licence gets the newest row
// as before.
func (s *Store) LatestActivationRequest(ctx context.Context, accountID store.ID) (store.ActivationRequest, error) {
	r, err := scanActivationRequest(s.db.QueryRow(ctx, latestActivationRequestSQL, accountID))
	if err != nil {
		return store.ActivationRequest{}, notFound("latest activation request", err)
	}
	return r, nil
}

// GetActivationRequest reads one request by id — the buyer polling a ticket
// they were handed, or the seller re-reading after a conflict.
func (s *Store) GetActivationRequest(ctx context.Context, requestID string) (store.ActivationRequest, error) {
	r, err := scanActivationRequest(s.db.QueryRow(ctx, getActivationRequestSQL, requestID))
	if err != nil {
		return store.ActivationRequest{}, notFound("get activation request", err)
	}
	return r, nil
}

// GetRequestByInstallID reads the machine's most recent request, whoever filed
// it. The buyer's own reads never go through here; this is the upsert's
// fallback and the seller's lookup of a machine code.
func (s *Store) GetRequestByInstallID(ctx context.Context, installID string) (store.ActivationRequest, error) {
	r, err := scanActivationRequest(s.db.QueryRow(ctx, requestByInstallIDSQL, installID))
	if err != nil {
		return store.ActivationRequest{}, notFound("request by install id", err)
	}
	return r, nil
}

// ListActivationRequests returns the console's table, newest first. An empty
// status means every state.
func (s *Store) ListActivationRequests(ctx context.Context, status store.ActivationStatus) ([]store.ActivationRequestView, error) {
	rows, err := s.db.Query(ctx, listActivationRequestsSQL, string(status))
	if err != nil {
		return nil, wrap("list activation requests", err)
	}
	defer rows.Close()

	var out []store.ActivationRequestView
	for rows.Next() {
		v, err := scanActivationRequestView(rows)
		if err != nil {
			return nil, wrap("list activation requests: scan", err)
		}
		out = append(out, v)
	}
	if err := rows.Err(); err != nil {
		return nil, wrap("list activation requests: rows", err)
	}
	return out, nil
}

// ConfirmActivationRequest flips a pending request to confirmed and installs
// its binding, atomically.
//
// The flip is conditional on the request still being pending: two sellers
// pressing the same button produce one licence, and the loser reads
// ErrConflict and re-reads the row — which is the idempotent answer the
// console shows, because the envelope was written once and a re-sign would
// hand the same licence a different token on every poll.
func (s *Store) ConfirmActivationRequest(ctx context.Context, requestID, licenseID string, envelopeJSON []byte, at time.Time) (store.ActivationRequest, error) {
	var result store.ActivationRequest
	now := utc(at)

	err := s.inTx(ctx, func(tx pgx.Tx) error {
		r, err := scanActivationRequest(tx.QueryRow(ctx, confirmLockSQL, requestID))
		if err != nil {
			return notFound("confirm activation request: load", err)
		}
		if r.Status != store.ActivationPending {
			// Already confirmed or rejected. The row is right there, so this
			// is "somebody else got there first", not a miss.
			return store.ErrConflict
		}

		if _, err := tx.Exec(ctx, confirmInsertBindingSQL,
			licenseID, r.InstallID, r.AccountID, requestID, now); err != nil {
			return bindingConflict("confirm activation request: bind", err)
		}

		tag, err := tx.Exec(ctx, confirmFlipSQL, requestID, licenseID, envelopeJSON, now)
		if err != nil {
			return wrap("confirm activation request: flip", err)
		}
		// The row is locked and was pending; zero rows would mean somebody
		// changed the state through a path that bypassed the lock.
		if err := conflictWhenUnchanged("confirm activation request: flip", nil, tag.RowsAffected()); err != nil {
			return err
		}

		r.Status = store.ActivationConfirmed
		r.LicenseID = licenseID
		r.EnvelopeJSON = envelopeJSON
		confirmedAt := now
		r.ConfirmedAt = &confirmedAt
		result = r
		return nil
	})
	if err != nil {
		return store.ActivationRequest{}, err
	}
	return result, nil
}

// reissueRetireSQL retires the machine's live binding, if it has one, and
// reports whose it was. The new binding inherits that account: a reissue is
// the same person on the same machine, not a new purchase. RETURNING yields no
// row when the machine holds nothing, which is the "reissue after a restore"
// case and not an error.
const reissueRetireSQL = `
	UPDATE licence_bindings
	SET revoked_at = $2
	WHERE install_id = $1 AND revoked_at IS NULL
	RETURNING account_id`

// reissueLatestSQL is the machine's most recent request, pending or not. A
// pending one is confirmed with the new licence — that is the buyer's poller
// learning about the reissue. A non-pending one is the earlier registration
// the reissue row copies its anchor and amount from, so the seller's console
// keeps showing what was bought. No row at all is only an error when the
// machine also holds no live binding.
const reissueLatestSQL = `
	SELECT ` + activationRequestColumns + `
	FROM activation_requests
	WHERE install_id = $1
	ORDER BY created_at DESC
	LIMIT 1`

// reissueInsertRequestSQL files the reissue as a confirmed request of its own
// when the machine has no open registration. A licence with no row is a
// licence nobody can poll for, and a licence nobody can poll for is a licence
// nobody collects.
const reissueInsertRequestSQL = `
	INSERT INTO activation_requests
		(request_id, account_id, install_id, anchor_digest, amount_fen,
		 status, license_id, envelope_json, created_at, confirmed_at)
	VALUES ($1, $2, $3, $4, $5, 'confirmed', $6, $7, $8, $9)`

// newRequestID mints the 单号 a seller-side reissue row is filed under. The
// buyer never quotes this one — it exists so the poller has a row to read —
// but it carries the same act_ prefix the mock and the client logs use, so a
// support conversation reads the same on either side of the cutover.
func newRequestID() (string, error) {
	var buf [12]byte
	if _, err := rand.Read(buf[:]); err != nil {
		return "", err
	}
	return "act_" + hex.EncodeToString(buf[:]), nil
}

// ReissueLicence retires the machine's live binding — if any — and installs a
// new one, atomically, and always leaves a confirmed request row carrying the
// new licence's envelope: the machine's pending request confirmed if there is
// one, otherwise a fresh row that copies the earlier registration's anchor and
// amount.
//
// ErrNotFound means there is nothing to reissue: no live binding and no
// request of any kind for that machine. The seller is looking at a machine
// code this service has never heard of, and a reissue that invented a licence
// out of thin air would be a support call nobody can explain.
// RejectActivationRequest flips a pending request to rejected and records when.
//
// No licence is written and no binding is touched: a rejected request never had
// either, and if the machine already holds a live licence from an earlier order
// that licence is unaffected. Refusing one order is not a revocation —
// RevokeBinding is what does that, and it takes a licence id, not a request id.
//
// The conditional flip is the whole point. A seller pressing refuse and confirm
// at the same moment produces one winner; the loser gets ErrConflict and
// re-reads the row to see which outcome it was. Same shape as confirm.
func (s *Store) RejectActivationRequest(ctx context.Context, requestID string, at time.Time) (store.ActivationRequest, error) {
	var result store.ActivationRequest
	now := utc(at)

	err := s.inTx(ctx, func(tx pgx.Tx) error {
		r, err := scanActivationRequest(tx.QueryRow(ctx, confirmLockSQL, requestID))
		if err != nil {
			return notFound("reject activation request: load", err)
		}
		tag, err := tx.Exec(ctx, rejectFlipSQL, requestID, now)
		if err != nil {
			return wrap("reject activation request: flip", err)
		}
		// The row was locked and we read its state; zero rows affected means it
		// changed underneath us, which is the concurrent-winner case, not a miss.
		if err := conflictWhenUnchanged("reject activation request: flip", nil, tag.RowsAffected()); err != nil {
			return err
		}
		r.Status = store.ActivationRejected
		rejectedAt := now
		r.RejectedAt = &rejectedAt
		result = r
		return nil
	})
	if err != nil {
		return store.ActivationRequest{}, err
	}
	return result, nil
}

func (s *Store) ReissueLicence(ctx context.Context, installID, licenseID string, envelopeJSON []byte, at time.Time) (store.ActivationRequest, error) {
	var result store.ActivationRequest
	now := utc(at)

	err := s.inTx(ctx, func(tx pgx.Tx) error {
		var oldAccount string
		if err := tx.QueryRow(ctx, reissueRetireSQL, installID, now).Scan(&oldAccount); err != nil &&
			!errors.Is(err, pgx.ErrNoRows) {
			return wrap("reissue licence: retire old binding", err)
		}

		latest, latestErr := scanActivationRequest(tx.QueryRow(ctx, reissueLatestSQL, installID))
		if latestErr != nil && !errors.Is(latestErr, pgx.ErrNoRows) {
			return wrap("reissue licence: load machine's request", latestErr)
		}
		hasRequest := latestErr == nil
		hasPending := hasRequest && latest.Status == store.ActivationPending

		account := oldAccount
		if account == "" && hasRequest {
			account = latest.AccountID
		}
		if account == "" {
			return store.ErrNotFound
		}

		// The binding links back to the request that produced the licence:
		// the pending row it confirms, or the fresh row the reissue files.
		freshID := ""
		if !hasPending {
			minted, err := newRequestID()
			if err != nil {
				return wrap("reissue licence: mint request id", err)
			}
			freshID = minted
		}
		requestID := freshID
		if hasPending {
			requestID = latest.RequestID
		}
		if _, err := tx.Exec(ctx, confirmInsertBindingSQL,
			licenseID, installID, account, requestID, now); err != nil {
			return bindingConflict("reissue licence: bind", err)
		}

		if !hasPending {
			// No open request: the reissue itself is the answer, and it gets
			// a row of its own. The anchor and amount are the machine's
			// earlier registration when there is one, so the console keeps
			// showing what was bought; a machine this service has never seen
			// a registration for still gets a row, with an empty anchor.
			anchor, amount := "", (*int64)(nil)
			if hasRequest {
				anchor, amount = latest.AnchorDigest, latest.AmountFen
			}
			if _, err := tx.Exec(ctx, reissueInsertRequestSQL,
				freshID, account, installID, anchor, amount,
				licenseID, envelopeJSON, now, now); err != nil {
				return wrap("reissue licence: file request", err)
			}
			confirmedAt := now
			result = store.ActivationRequest{
				RequestID:    freshID,
				AccountID:    account,
				InstallID:    installID,
				AnchorDigest: anchor,
				AmountFen:    amount,
				Status:       store.ActivationConfirmed,
				EnvelopeJSON: envelopeJSON,
				LicenseID:    licenseID,
				CreatedAt:    now,
				ConfirmedAt:  &confirmedAt,
			}
			return nil
		}

		tag, err := tx.Exec(ctx, confirmFlipSQL, latest.RequestID, licenseID, envelopeJSON, now)
		if err != nil {
			return wrap("reissue licence: confirm pending", err)
		}
		if err := conflictWhenUnchanged("reissue licence: confirm pending", nil, tag.RowsAffected()); err != nil {
			return err
		}

		latest.Status = store.ActivationConfirmed
		latest.LicenseID = licenseID
		latest.EnvelopeJSON = envelopeJSON
		confirmedAt := now
		latest.ConfirmedAt = &confirmedAt
		result = latest
		return nil
	})
	if err != nil {
		return store.ActivationRequest{}, err
	}
	return result, nil
}

const revokeBindingSQL = `
	UPDATE licence_bindings
	SET revoked_at = $2
	WHERE license_id = $1 AND revoked_at IS NULL`

const bindingExistsSQL = `
	SELECT license_id
	FROM licence_bindings
	WHERE license_id = $1`

// RevokeBinding marks a licence revoked. The row stays: the honest answer to
// "is this licence still valid" is a date, not a deletion.
//
// Zero rows updated is two different stories — already revoked, or never
// existed — and they get different answers. Revoking twice is a misclick on a
// stale console and succeeds; revoking a licence this service never issued is
// a 404.
func (s *Store) RevokeBinding(ctx context.Context, licenseID string, at time.Time) error {
	now := utc(at)
	tag, err := s.db.Exec(ctx, revokeBindingSQL, licenseID, now)
	if err != nil {
		return wrap("revoke binding", err)
	}
	if tag.RowsAffected() > 0 {
		return nil
	}

	var existing string
	switch err := s.db.QueryRow(ctx, bindingExistsSQL, licenseID).Scan(&existing); {
	case errors.Is(err, pgx.ErrNoRows):
		return store.ErrNotFound
	case err != nil:
		return wrap("revoke binding: re-read", err)
	default:
		return nil
	}
}

// ---------------------------------------------------------------------------
// Licence bindings — which machines a licence is pinned to.
// ---------------------------------------------------------------------------

// licenceBindingColumns is the licence_bindings projection, in the order the
// scanners expect. Unqualified for the same reason as
// activationRequestColumns: only single-table queries use it.
const licenceBindingColumns = `
	license_id, install_id, account_id, request_id, issued_at, revoked_at`

// qualifiedLicenceBindingColumns is the b.-prefixed form, for the ledger's
// join against accounts — install_id and issued_at are on both tables.
const qualifiedLicenceBindingColumns = `
	b.license_id, b.install_id, b.account_id, b.request_id, b.issued_at, b.revoked_at`

const liveBindingSQL = `
	SELECT ` + licenceBindingColumns + `
	FROM licence_bindings
	WHERE install_id = $1 AND revoked_at IS NULL`

const listBindingsSQL = `
	SELECT ` + qualifiedLicenceBindingColumns + `, a.nickname, a.open_id
	FROM licence_bindings b
	JOIN accounts a ON a.id = b.account_id
	ORDER BY b.issued_at DESC`

// LiveBindingByInstallID finds the licence a machine currently holds.
func (s *Store) LiveBindingByInstallID(ctx context.Context, installID string) (store.LicenceBinding, error) {
	b, err := scanLicenceBinding(s.db.QueryRow(ctx, liveBindingSQL, installID))
	if err != nil {
		return store.LicenceBinding{}, notFound("live binding by install id", err)
	}
	return b, nil
}

// ListBindings is the ledger page: everything ever issued, newest first, with
// the person it belongs to.
func (s *Store) ListBindings(ctx context.Context) ([]store.LicenceBindingView, error) {
	rows, err := s.db.Query(ctx, listBindingsSQL)
	if err != nil {
		return nil, wrap("list bindings", err)
	}
	defer rows.Close()

	var out []store.LicenceBindingView
	for rows.Next() {
		v, err := scanLicenceBindingView(rows)
		if err != nil {
			return nil, wrap("list bindings: scan", err)
		}
		out = append(out, v)
	}
	if err := rows.Err(); err != nil {
		return nil, wrap("list bindings: rows", err)
	}
	return out, nil
}

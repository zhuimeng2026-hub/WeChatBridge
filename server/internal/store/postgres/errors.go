package postgres

import (
	"errors"
	"fmt"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// Constraint names the application reasons about. They are created in
// migrations/0001_init.up.sql; renaming one there means renaming it here, and
// a mismatch is a silent loss of error classification rather than a build
// failure, which is why they live in one place.
//
// The orders, redeem_codes and refresh_tokens names are gone with the
// payment rewrite; what is left is the sign-in path and the buy-out path.
const (
	constraintActivationRequestPK = "activation_requests_pkey"
	constraintLicenceBindingPK    = "licence_bindings_pkey"
	constraintBindingLiveInstall  = "licence_bindings_live_idx"
	constraintBindingLiveAccount  = "licence_bindings_live_account_idx"
	constraintAuthSessionTicket   = "auth_sessions_pkey"
	constraintAccountOpenID       = "accounts_open_id_key"
)

// PostgreSQL SQLSTATE codes this package distinguishes.
const (
	pgUniqueViolation     = "23505"
	pgForeignKeyViolation = "23503"
)

// notFound turns the driver's "no rows" into the store's miss.
//
// It is the only place pgx.ErrNoRows is matched. Everything else — a dropped
// connection, a syntax error, a permission failure — keeps its own identity,
// because "the database is down" and "that request does not exist" are answers
// that must never be confused: the first is a page from the operator, the
// second is an HTTP 404.
func notFound(op string, err error) error {
	if errors.Is(err, pgx.ErrNoRows) {
		return store.ErrNotFound
	}
	return wrap(op, err)
}

// conflictWhenUnchanged maps the outcome of a conditional UPDATE.
//
// Zero rows does not mean "nothing happened" here — it means the row is not in
// the state the caller required, which is a different answer from "no such
// row" and the caller is the only party that knows which of the two it is
// looking at. It returns ErrConflict, and callers that need to tell the two
// apart run a follow-up read first (see ConfirmActivationRequest).
func conflictWhenUnchanged(op string, err error, affected int64) error {
	if err != nil {
		return wrap(op, err)
	}
	if affected == 0 {
		return store.ErrConflict
	}
	return nil
}

// uniqueConflict classifies a unique-constraint violation that the caller has
// declared meaningful — "another sign-in already created this account".
//
// Only the named constraint becomes ErrConflict. A violation of any other
// index, and every other driver error, comes back wrapped and intact: a code
// path that answers "somebody beat you to it" when the real problem is a
// corrupted data file is worse than no classification at all, because it hides
// the failure behind a 409 that reads like normal traffic.
//
// The driver's Detail and Hint fields are deliberately not copied into the
// message: PostgreSQL formats them as `Key (col)=(value) already exists`, and
// the value is sometimes a code or a token hash the operator has no business
// reading in a log.
func uniqueConflict(op, constraint string, err error) error {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) && pgErr.Code == pgUniqueViolation {
		if pgErr.ConstraintName == constraint {
			return store.ErrConflict
		}
		return wrap(op, fmt.Errorf("unique violation on %s: %w", pgErr.ConstraintName, err))
	}
	return wrap(op, err)
}

// bindingConflict classifies the three ways a licence binding insert can
// collide — and it does NOT lump them together, because they are not the same
// event.
//
//   - licence_bindings_pkey: the same licence id twice. That is a genuine race
//     on a random id, and the loser retries.
//   - licence_bindings_live_account_idx: a second live binding for one
//     account. Also a real race: one account can hold pending requests for two
//     different machines, so two sellers confirming at once is ordinary.
//   - licence_bindings_live_install_idx: a second live binding for one
//     machine. This one CANNOT be a race. Only one pending request per install
//     id may exist at a time (activation_requests_live_install_idx), and the
//     confirm path takes a row lock and re-checks the status before inserting,
//     so a concurrent confirm of the same request is already excluded. The
//     only way to land here is that the machine held a live licence from an
//     earlier, already-confirmed request.
//
// The last one used to be reported as a plain conflict, which told the seller
// "somebody else got there first, refresh and try again" about a state that
// no amount of retrying could ever change.
func bindingConflict(op string, err error) error {
	var pgErr *pgconn.PgError
	if errors.As(err, &pgErr) && pgErr.Code == pgUniqueViolation {
		switch pgErr.ConstraintName {
		case constraintBindingLiveInstall:
			return store.ErrLiveBindingExists
		case constraintLicenceBindingPK, constraintBindingLiveAccount:
			return store.ErrConflict
		}
		return wrap(op, fmt.Errorf("unique violation on %s: %w", pgErr.ConstraintName, err))
	}
	return wrap(op, err)
}

// wrap adds the operation's identity without ever adding a value. Every driver
// error that leaves this package goes through here, so no raw pgx error reaches
// the HTTP layer unwrapped.
func wrap(op string, err error) error {
	if err == nil {
		return nil
	}
	if errors.Is(err, pgx.ErrNoRows) {
		// Reaching here means a caller used wrap on a read that expected a row;
		// turning it into a miss here keeps that mistake from surfacing as a
		// 500 with a driver message.
		return store.ErrNotFound
	}
	return fmt.Errorf("postgres: %s: %w", op, err)
}

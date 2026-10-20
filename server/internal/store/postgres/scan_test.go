package postgres

import (
	"fmt"
	"testing"
	"time"

	"github.com/jackc/pgx/v5/pgtype"
)

// The regression this file exists for is one that pgxmock could not have
// caught: the mock asserts SQL strings and never runs them, so it never had a
// NULL to hand back. The bug was in the *scan*, not in the query — a column
// that the schema allows to be NULL was read into a plain string, and the
// driver refused it. The row was found; reading it failed.
//
// So the test builds the row itself. fakeRow assigns values to the scan
// destinations the way the driver does, including the NULL.

type fakeRow struct {
	values []any
	err    error
}

func (r fakeRow) Scan(dest ...any) error {
	if r.err != nil {
		return r.err
	}
	if len(dest) != len(r.values) {
		return errWrongArity(len(dest), len(r.values))
	}
	for i, d := range dest {
		if err := assign(d, r.values[i]); err != nil {
			return err
		}
	}
	return nil
}

// scanError is what the fake driver reports. The messages are the driver's
// own, because a test that fails with "arity mismatch" when the real fault is
// a NULL teaches the reader nothing.
type scanError struct{ msg string }

func (e scanError) Error() string { return e.msg }

func errWrongArity(want, got int) error {
	return scanError{msg: fmt.Sprintf("wrong number of scan destinations: got %d, want %d", got, want)}
}

// assign writes one column into one scan destination, the way pgx does: a
// NULL into a nullable destination is a valid-but-empty value, and a NULL
// into a plain string is the error this file is about.
func assign(dest any, value any) error {
	switch d := dest.(type) {
	case *string:
		if value == nil {
			return errScanNull
		}
		s, ok := value.(string)
		if !ok {
			return errScanType
		}
		*d = s
	case *pgtype.Text:
		if value == nil {
			*d = pgtype.Text{}
			return nil
		}
		s, ok := value.(string)
		if !ok {
			return errScanType
		}
		*d = pgtype.Text{String: s, Valid: true}
	case *time.Time:
		if value == nil {
			return errScanNull
		}
		t, ok := value.(time.Time)
		if !ok {
			return errScanType
		}
		*d = t
	case *pgtype.Timestamptz:
		if value == nil {
			*d = pgtype.Timestamptz{}
			return nil
		}
		t, ok := value.(time.Time)
		if !ok {
			return errScanType
		}
		*d = pgtype.Timestamptz{Time: t, Valid: true}
	default:
		return errScanType
	}
	return nil
}

var (
	errScanNull = scanError{msg: "cannot scan NULL into a non-nullable destination"}
	errScanType = scanError{msg: "cannot scan this value into that destination"}
)

func TestScanSessionReadsAnAccountThatHasNotRefreshedYet(t *testing.T) {
	// The state every account is in right after signing in: the live hash is
	// set and the previous one is NULL, because a fresh sign-in has no
	// previous generation. Reading this row used to fail with
	// "cannot scan NULL into *string", and the client's refresh answered 503.
	expires := time.Date(2027, 1, 18, 6, 58, 22, 0, time.UTC)
	row := fakeRow{values: []any{
		"acct-1",           // id
		"chain-1",          // chain_id
		"live-hash",        // refresh_token_hash
		nil,                // prev_refresh_token_hash — NULL by design
		expires,            // refresh_expires_at
		nil,                // session_revoked_at
	}}

	sess, err := scanSession(row, "presented-hash")
	if err != nil {
		t.Fatalf("scanSession: %v", err)
	}
	if sess.AccountID != "acct-1" {
		t.Errorf("AccountID = %q, want acct-1", sess.AccountID)
	}
	if sess.ChainID != "chain-1" {
		t.Errorf("ChainID = %q, want chain-1", sess.ChainID)
	}
	if sess.RefreshTokenHash != "presented-hash" {
		t.Errorf("RefreshTokenHash = %q, want the presented hash", sess.RefreshTokenHash)
	}
	if !sess.RefreshExpiresAt.Equal(expires) {
		t.Errorf("RefreshExpiresAt = %v, want %v", sess.RefreshExpiresAt, expires)
	}
	if sess.RevokedAt != nil {
		t.Errorf("RevokedAt = %v, want nil", sess.RevokedAt)
	}
}

func TestScanSessionReadsARowThatHasRotatedOnce(t *testing.T) {
	// After one rotation both generations are present, and the row must read
	// the same way — the fix is not "the NULL case happens to work".
	expires := time.Date(2027, 1, 18, 6, 58, 22, 0, time.UTC)
	row := fakeRow{values: []any{
		"acct-1",
		"chain-1",
		"live-hash",
		"prev-hash",
		expires,
		nil,
	}}

	if _, err := scanSession(row, "presented-hash"); err != nil {
		t.Fatalf("scanSession: %v", err)
	}
}

func TestScanSessionReportsAnUnreadableRow(t *testing.T) {
	// The scanner must still fail when the row really is unreadable. A fix
	// that swallowed the error would turn a database fault into a silent
	// "your session is fine".
	row := fakeRow{values: []any{
		"acct-1",
		42, // chain_id, where a string belongs
		"live-hash",
		nil,
		time.Date(2027, 1, 18, 6, 58, 22, 0, time.UTC),
		nil,
	}}

	if _, err := scanSession(row, "presented-hash"); err == nil {
		t.Fatal("want an error for a row that does not match the projection")
	}
}

func TestScanSessionAcceptsANullLiveHash(t *testing.T) {
	// Both hash columns are nullable and neither is read: the WHERE clause
	// matched the row on one of them, and the paths that care which one ask
	// the database again under a lock. So a NULL in either is read as "not
	// there" rather than as a failure — the column says "this generation does
	// not exist", which is an answer, not a fault.
	expires := time.Date(2027, 1, 18, 6, 58, 22, 0, time.UTC)
	row := fakeRow{values: []any{
		"acct-1",
		"chain-1",
		nil, // refresh_token_hash
		nil, // prev_refresh_token_hash
		expires,
		nil,
	}}

	sess, err := scanSession(row, "presented-hash")
	if err != nil {
		t.Fatalf("scanSession: %v", err)
	}
	if sess.ChainID != "chain-1" {
		t.Errorf("ChainID = %q, want chain-1", sess.ChainID)
	}
}

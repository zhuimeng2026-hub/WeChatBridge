package postgres

import (
	"context"
	"regexp"
	"testing"

	"github.com/jackc/pgx/v5"
	"github.com/jackc/pgx/v5/pgconn"
	"github.com/jackc/pgx/v5/pgtype"
	"github.com/pashagolub/pgxmock/v3"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// pgxmock skips SQL NULL assignments, including invalid NULL-to-string scans.
// Use pgx's real wire decoder for both reads of a newly created token chain.
type refreshWireRow struct{}

func (refreshWireRow) Scan(dest ...any) error {
	fields := []pgconn.FieldDescription{
		{Name: "id", DataTypeOID: pgtype.TextOID},
		{Name: "chain_id", DataTypeOID: pgtype.TextOID},
		{Name: "refresh_token_hash", DataTypeOID: pgtype.TextOID},
		{Name: "prev_refresh_token_hash", DataTypeOID: pgtype.TextOID},
		{Name: "refresh_expires_at", DataTypeOID: pgtype.TimestamptzOID},
		{Name: "session_revoked_at", DataTypeOID: pgtype.TimestamptzOID},
	}
	values := [][]byte{
		[]byte("acct-1"), []byte("chain-1"), []byte("old-hash"), nil,
		[]byte(testLater.Format("2006-01-02 15:04:05.999999999Z07")), nil,
	}
	return pgx.ScanRow(pgtype.NewMap(), fields, values, dest...)
}

type refreshWireDB struct{ db }

func (d refreshWireDB) QueryRow(context.Context, string, ...any) pgx.Row {
	return refreshWireRow{}
}

func (d refreshWireDB) Begin(ctx context.Context) (pgx.Tx, error) {
	tx, err := d.db.Begin(ctx)
	if err != nil {
		return nil, err
	}
	return refreshWireTx{tx}, nil
}

type refreshWireTx struct{ pgx.Tx }

func (tx refreshWireTx) QueryRow(context.Context, string, ...any) pgx.Row {
	return refreshWireRow{}
}

func TestFirstRefreshReadsSQLNullPreviousHash(t *testing.T) {
	s, _ := newMock(t)
	s.db = refreshWireDB{s.db}
	sess, err := s.GetSessionByRefreshHash(context.Background(), "old-hash")
	if err != nil {
		t.Fatalf("lookup of a new session with NULL previous hash: %v", err)
	}
	if sess.AccountID != "acct-1" || sess.RefreshTokenHash != "old-hash" || sess.RevokedAt != nil {
		t.Fatalf("unexpected session: %+v", sess)
	}
}

func TestFirstRefreshRotatesSQLNullPreviousHash(t *testing.T) {
	s, mock := newMock(t)
	s.db = refreshWireDB{s.db}
	mock.ExpectBegin()
	mock.ExpectExec(regexp.QuoteMeta(rotateReplaceSQL)).
		WithArgs("old-hash", "new-hash", "acct-1", testLater).
		WillReturnResult(pgxmock.NewResult("UPDATE", 1))
	mock.ExpectCommit()

	sess, outcome, err := s.RotateRefresh(context.Background(), "old-hash", "new-hash", "new-access", testLater)
	if err != nil || outcome != store.RefreshRotated {
		t.Fatalf("first rotation with NULL previous hash: outcome=%q err=%v", outcome, err)
	}
	if sess.RefreshTokenHash != "new-hash" || sess.ChainID != "chain-1" || !sess.RefreshExpiresAt.Equal(testLater) {
		t.Fatalf("rotation lost session state: %+v", sess)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatal(err)
	}
}

package postgres

import (
	"context"
	"errors"
	"regexp"
	"testing"

	"github.com/pashagolub/pgxmock/v3"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The avatar statements are the one place in this package where a silent
// mistake is possible rather than a loud one. A scanner whose destinations are
// in a different order from the column list still compiles, still runs, and
// still returns a row; it just fills the wrong fields with each other's values.
// pgx's own type checking catches a few of those and not the rest.
//
// So these tests pin the two things a mock can see: which arguments go out, and
// what the scanner makes of the row that comes back. Together they are enough
// to catch a reordered column list and a swapped $1/$2, which are the mistakes
// that actually happen.

const testDigest = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"

func TestPutAvatarSendsTheRecordInColumnOrder(t *testing.T) {
	s, mock := newMock(t)

	mock.ExpectExec(regexp.QuoteMeta("INSERT INTO account_avatars")).
		WithArgs("acct-1", testDigest, "image/png", 4096, "https://wx.qlogo.cn/a/0", testNow).
		WillReturnResult(pgxmock.NewResult("INSERT", 1))

	err := s.PutAvatar(context.Background(), store.Avatar{
		AccountID: "acct-1",
		Digest:    testDigest,
		MIME:      "image/png",
		ByteSize:  4096,
		SourceURL: "https://wx.qlogo.cn/a/0",
		FetchedAt: testNow,
	})
	if err != nil {
		t.Fatalf("PutAvatar: %v", err)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("the statement did not match: %v", err)
	}
}

// The upsert is what makes a changed picture a replacement rather than a
// duplicate. Without ON CONFLICT the second sign-in after somebody changes
// their picture would fail on the primary key, and the failure would land in
// the avatar refresh — which is written to be non-fatal, so it would be a
// silently stale picture rather than a visible error.
func TestPutAvatarReplacesRatherThanDuplicates(t *testing.T) {
	s, mock := newMock(t)

	mock.ExpectExec(regexp.QuoteMeta("ON CONFLICT (account_id) DO UPDATE")).
		WithArgs("acct-1", testDigest, "image/png", 4096, "", testNow).
		WillReturnResult(pgxmock.NewResult("INSERT", 1))

	if err := s.PutAvatar(context.Background(), store.Avatar{
		AccountID: "acct-1",
		Digest:    testDigest,
		MIME:      "image/png",
		ByteSize:  4096,
		FetchedAt: testNow,
	}); err != nil {
		t.Fatalf("PutAvatar: %v", err)
	}
	if err := mock.ExpectationsWereMet(); err != nil {
		t.Fatalf("the statement did not match: %v", err)
	}
}

func TestGetAvatarReadsEveryColumnIntoItsOwnField(t *testing.T) {
	s, mock := newMock(t)

	mock.ExpectQuery(regexp.QuoteMeta("FROM account_avatars")).
		WithArgs("acct-1").
		WillReturnRows(pgxmock.NewRows([]string{
			"account_id", "digest", "mime", "byte_size", "source_url", "fetched_at",
		}).AddRow("acct-1", testDigest, "image/jpeg", 2048, "https://wx.qlogo.cn/a/9", testNow))

	a, err := s.GetAvatar(context.Background(), "acct-1")
	if err != nil {
		t.Fatalf("GetAvatar: %v", err)
	}

	// Each field is asserted separately and against a distinct value. A
	// reordered scanner would put the digest in the mime and the byte size in
	// the URL, and "the row came back" would still be true.
	if a.AccountID != "acct-1" {
		t.Errorf("AccountID = %q", a.AccountID)
	}
	if a.Digest != testDigest {
		t.Errorf("Digest = %q, want %q", a.Digest, testDigest)
	}
	if a.MIME != "image/jpeg" {
		t.Errorf("MIME = %q, want image/jpeg", a.MIME)
	}
	if a.ByteSize != 2048 {
		t.Errorf("ByteSize = %d, want 2048", a.ByteSize)
	}
	if a.SourceURL != "https://wx.qlogo.cn/a/9" {
		t.Errorf("SourceURL = %q", a.SourceURL)
	}
	if !a.FetchedAt.Equal(testNow) {
		t.Errorf("FetchedAt = %v, want %v", a.FetchedAt, testNow)
	}
}

// An account with no picture is the common case on a deployment where avatars
// were only just switched on, and the caller branches on ErrNotFound. A raw
// driver error here would become a 500 on an image URL.
func TestGetAvatarReportsNoRowAsAMiss(t *testing.T) {
	s, mock := newMock(t)

	mock.ExpectQuery(regexp.QuoteMeta("FROM account_avatars")).
		WithArgs("acct-x").
		WillReturnError(noRows)

	if _, err := s.GetAvatar(context.Background(), "acct-x"); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("want store.ErrNotFound, got %v", err)
	}
}

// source_url defaults to empty rather than being NULL in practice, but the
// column is nullable in the schema and a manual fix could leave it that way.
// The domain type is a plain string, so the scanner has to absorb it.
func TestGetAvatarAbsentsANullSourceURL(t *testing.T) {
	s, mock := newMock(t)

	mock.ExpectQuery(regexp.QuoteMeta("FROM account_avatars")).
		WithArgs("acct-1").
		WillReturnRows(pgxmock.NewRows([]string{
			"account_id", "digest", "mime", "byte_size", "source_url", "fetched_at",
		}).AddRow("acct-1", testDigest, "image/png", 10, nil, testNow))

	a, err := s.GetAvatar(context.Background(), "acct-1")
	if err != nil {
		t.Fatalf("GetAvatar: %v", err)
	}
	if a.SourceURL != "" {
		t.Errorf("SourceURL = %q, want empty for a NULL column", a.SourceURL)
	}
}

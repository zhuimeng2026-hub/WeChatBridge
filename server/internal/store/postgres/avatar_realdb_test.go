package postgres

// Real-database tests for the local avatar copy.
//
// The avatar table is small and its statements are simple, which is exactly
// why it is worth testing for real: the properties that matter here are not
// in the SQL but in the schema around it. The upsert is unconditional, so
// nothing in the statement stops a person from changing their picture — the
// replacement has to work because the primary key is the account, and the
// digest is what the client's ETag and the file name are built from.
//
// The other half is what the schema refuses. The digest is used to build a
// path, the mime is what the bytes were sniffed to be rather than what the
// server claimed, and the byte size is what proves the read was not
// truncated. Each of those is a CHECK constraint, and a constraint that is
// never exercised is a constraint that was deleted by accident.

import (
	"context"
	"errors"
	"testing"
	"time"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// avatarDigestB is a second valid digest, so the replacement tests can tell
// "the row was updated" from "the row was inserted again".
const avatarDigestB = "fedcba9876543210fedcba9876543210fedcba9876543210fedcba9876543210"

// TestPutAvatarReplacesTheRecordedCopy is the reason the statement is an
// unconditional upsert.
//
// A person changing their WeChat picture is the expected case, not a
// conflict. If the second write did not replace the first, the refresh
// would fail on the primary key — and the refresh is written to be
// non-fatal, so the failure would land as a silently stale picture rather
// than as a visible error. The client would keep drawing the avatar the
// person had last year.
func TestPutAvatarReplacesTheRecordedCopy(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-avatar-replace")

	first := store.Avatar{
		AccountID: acct,
		Digest:    testDigest,
		MIME:      "image/png",
		ByteSize:  100,
		SourceURL: "https://wx.qlogo.cn/a/old",
		FetchedAt: time.Now().UTC(),
	}
	if err := r.PutAvatar(ctx, first); err != nil {
		t.Fatalf("put the first copy: %v", err)
	}

	second := store.Avatar{
		AccountID: acct,
		Digest:    avatarDigestB,
		MIME:      "image/jpeg",
		ByteSize:  200,
		SourceURL: "https://wx.qlogo.cn/a/new",
		FetchedAt: time.Now().UTC().Add(time.Minute),
	}
	if err := r.PutAvatar(ctx, second); err != nil {
		t.Fatalf("put the second copy: %v", err)
	}

	got, err := r.GetAvatar(ctx, acct)
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	if got.Digest != avatarDigestB {
		t.Fatalf("Digest = %q, want %q — the old picture is still the recorded one", got.Digest, avatarDigestB)
	}
	// The whole record is replaced, not merged: a stale mime or a stale byte
	// count would describe bytes that are no longer on disk.
	if got.MIME != "image/jpeg" {
		t.Errorf("MIME = %q, want image/jpeg", got.MIME)
	}
	if got.ByteSize != 200 {
		t.Errorf("ByteSize = %d, want 200", got.ByteSize)
	}
	if got.SourceURL != "https://wx.qlogo.cn/a/new" {
		t.Errorf("SourceURL = %q, want the new one", got.SourceURL)
	}
	if !got.FetchedAt.Equal(second.FetchedAt.Truncate(time.Microsecond)) {
		t.Errorf("FetchedAt = %v, want %v", got.FetchedAt, second.FetchedAt)
	}

	// One row, not two. The primary key is the account, and a second row
	// would be a second picture with nothing saying which one is current.
	if n := r.queryInt(t, `SELECT count(*) FROM account_avatars WHERE account_id = $1`, acct); n != 1 {
		t.Fatalf("%d rows for one account", n)
	}
}

// TestTheSameDigestIsARecordForEachAccount separates the two identifiers.
//
// The digest is a file name and an ETag; the account is the key. Two people
// whose pictures hash to the same bytes share a file on disk, and each of
// them still needs a row that says which file is theirs.
func TestTheSameDigestIsARecordForEachAccount(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acctA := seedAccount(t, r, "open-avatar-a")
	acctB := seedAccount(t, r, "open-avatar-b")

	for _, acct := range []string{acctA, acctB} {
		if err := r.PutAvatar(ctx, store.Avatar{
			AccountID: acct,
			Digest:    testDigest,
			MIME:      "image/png",
			ByteSize:  10,
			FetchedAt: time.Now().UTC(),
		}); err != nil {
			t.Fatalf("put avatar for %s: %v", acct, err)
		}
	}

	if n := r.queryInt(t, `SELECT count(*) FROM account_avatars WHERE digest = $1`, testDigest); n != 2 {
		t.Fatalf("%d rows for one digest, want 2 — the digest is the file name, not the key", n)
	}
	for _, acct := range []string{acctA, acctB} {
		got, err := r.GetAvatar(ctx, acct)
		if err != nil {
			t.Fatalf("read %s: %v", acct, err)
		}
		if got.Digest != testDigest {
			t.Fatalf("Digest for %s = %q, want %q", acct, got.Digest, testDigest)
		}
	}
}

// TestGetAvatarReportsAMissForAnAccountWithoutAPicture is the ordinary
// answer.
//
// ErrNotFound is not a fault here: not every deployment keeps avatars, and
// not every sign-in produces one. The caller draws a blank picture, and a 500
// on an image URL would be a broken image with a worse story.
func TestGetAvatarReportsAMissForAnAccountWithoutAPicture(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-avatar-missing")

	if _, err := r.GetAvatar(ctx, acct); !errors.Is(err, store.ErrNotFound) {
		t.Fatalf("err = %v, want store.ErrNotFound", err)
	}
}

// TestGetAvatarReadsANullSourceURLAsEmpty is the nullable column, for real.
//
// The pgxmock suite has a test for this (TestGetAvatarAbsentsANullSourceURL)
// and the mock cannot actually exercise it: a mock skips SQL NULL
// assignments, so its "NULL" was a fabricated row and the scan never met a
// real NULL. The real driver is the only thing that can show that the
// scanner absorbs one.
//
// The domain type is a plain string, so a NULL has to be absorbed rather than
// surfaced as a failure: the caller compares the recorded source URL against
// the one WeChat just gave it to decide whether the copy is stale, and a
// failure to read the row would turn "the picture might be old" into a 500.
//
// The shipped schema declares source_url NOT NULL, so this state is
// unreachable through the Store — the test says so explicitly first, and
// then relaxes the constraint in its own throwaway schema to reach it
// anyway. The mock's comment ("the column is nullable in the schema") is
// wrong about the schema, and the right place to say so is a test that has
// actually looked.
func TestGetAvatarReadsANullSourceURLAsEmpty(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-avatar-null-source")

	if err := r.PutAvatar(ctx, store.Avatar{
		AccountID: acct,
		Digest:    testDigest,
		MIME:      "image/png",
		ByteSize:  10,
		SourceURL: "https://wx.qlogo.cn/a/0",
		FetchedAt: time.Now().UTC(),
	}); err != nil {
		t.Fatalf("put avatar: %v", err)
	}

	// The shipped constraint refuses a NULL, so the Store can never write
	// one. Only a manual fix or an older row could hold it.
	if _, err := r.db.Exec(context.Background(),
		`UPDATE account_avatars SET source_url = NULL WHERE account_id = $1`, acct); err == nil {
		t.Fatal("the schema accepted a NULL source_url; the constraint is gone and the mock's claim was right")
	}

	// Relaxed in this test's own schema, which is dropped afterwards.
	r.exec(t, `ALTER TABLE account_avatars ALTER COLUMN source_url DROP NOT NULL`)
	r.exec(t, `UPDATE account_avatars SET source_url = NULL WHERE account_id = $1`, acct)

	got, err := r.GetAvatar(ctx, acct)
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	if got.SourceURL != "" {
		t.Fatalf("SourceURL = %q, want empty for a NULL column", got.SourceURL)
	}
	if got.Digest != testDigest {
		t.Fatalf("the rest of the record did not survive the NULL: %+v", got)
	}
}

// TestPutAvatarStoresAnEmptySourceURLAsEmpty is the ordinary case: a
// deployment that knows where the picture came from records it, and one that
// does not records an empty string rather than a NULL.
func TestPutAvatarStoresAnEmptySourceURLAsEmpty(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-avatar-empty-source")

	if err := r.PutAvatar(ctx, store.Avatar{
		AccountID: acct,
		Digest:    testDigest,
		MIME:      "image/png",
		ByteSize:  10,
		FetchedAt: time.Now().UTC(),
	}); err != nil {
		t.Fatalf("put avatar: %v", err)
	}

	got, err := r.GetAvatar(ctx, acct)
	if err != nil {
		t.Fatalf("read back: %v", err)
	}
	if got.SourceURL != "" {
		t.Fatalf("SourceURL = %q, want empty", got.SourceURL)
	}
}

// TestPutAvatarIsRefusedForAnUnknownAccount is the foreign key.
//
// The row points at a file on disk. A row that points at an account that does
// not exist points at nothing anybody can ask for, and the account id is what
// the request path is built from.
func TestPutAvatarIsRefusedForAnUnknownAccount(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()

	err := r.PutAvatar(ctx, store.Avatar{
		AccountID: "acct-does-not-exist",
		Digest:    testDigest,
		MIME:      "image/png",
		ByteSize:  10,
		FetchedAt: time.Now().UTC(),
	})
	if err == nil {
		t.Fatal("the avatar was recorded for an account that does not exist")
	}
	if n := r.queryInt(t, `SELECT count(*) FROM account_avatars`); n != 0 {
		t.Fatalf("%d avatar rows survived a refused write", n)
	}
}

// TestTheAvatarSchemaRefusesADigestThatIsNotALowercaseSHA is the constraint
// that keeps a path out of the database.
//
// The digest is used to build a file name, so the check runs on every write
// rather than being sanitised on every read. "64 lowercase hex characters" is
// the whole rule: a digest that is uppercase, short, or contains a path
// separator is not a digest.
func TestTheAvatarSchemaRefusesADigestThatIsNotALowercaseSHA(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-avatar-digest")

	bad := []struct {
		name, digest string
	}{
		{"too short", "abc123"},
		{"uppercase", "0123456789ABCDEF0123456789abcdef0123456789abcdef0123456789abcdef"},
		{"not hex", "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg"},
		{"a path", "../../../../etc/passwd"},
	}
	for _, tc := range bad {
		t.Run(tc.name, func(t *testing.T) {
			err := r.PutAvatar(ctx, store.Avatar{
				AccountID: acct,
				Digest:    tc.digest,
				MIME:      "image/png",
				ByteSize:  10,
				FetchedAt: time.Now().UTC(),
			})
			if err == nil {
				t.Fatalf("the schema accepted the digest %q", tc.digest)
			}
		})
	}
}

// TestTheAvatarSchemaRefusesAMimeItDoesNotAccept is the "sniffed, not
// claimed" rule, enforced where it cannot be forgotten.
//
// The mime is what the bytes were sniffed to be, never what the server
// claimed in Content-Type: a remote host that says image/png and sends HTML
// is an attack or a captive portal, and storing the claim would hand the
// client bytes its decoder will choke on.
func TestTheAvatarSchemaRefusesAMimeItDoesNotAccept(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-avatar-mime")

	err := r.PutAvatar(ctx, store.Avatar{
		AccountID: acct,
		Digest:    testDigest,
		MIME:      "text/html",
		ByteSize:  10,
		FetchedAt: time.Now().UTC(),
	})
	if err == nil {
		t.Fatal("the schema accepted text/html as an avatar")
	}
}

// TestTheAvatarSchemaRefusesAnEmptyImage is the truncated read.
//
// The byte size is kept because a truncated read is otherwise invisible until
// somebody looks at the file. Zero bytes is not a picture.
func TestTheAvatarSchemaRefusesAnEmptyImage(t *testing.T) {
	t.Parallel()
	r := newRealDB(t)
	ctx := context.Background()
	acct := seedAccount(t, r, "open-avatar-size")

	err := r.PutAvatar(ctx, store.Avatar{
		AccountID: acct,
		Digest:    testDigest,
		MIME:      "image/png",
		ByteSize:  0,
		FetchedAt: time.Now().UTC(),
	})
	if err == nil {
		t.Fatal("the schema accepted a zero-byte avatar")
	}
}

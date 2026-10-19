package postgres

import (
	"context"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// The local avatar copy. It lives in its own file rather than in auth.go
// because it deliberately does not touch the account projection: accounts is
// read by every query in the auth package, and widening it for a field only
// one handler needs would slow down sign-in to serve a picture.
//
// Two statements, both unconditional. There is no conditional-update-with-a-
// row-count here on purpose: nothing about an avatar has to happen exactly
// once, and a person changing their picture is not a conflict with themselves.

const putAvatarSQL = `
	INSERT INTO account_avatars (account_id, digest, mime, byte_size, source_url, fetched_at)
	VALUES ($1, $2, $3, $4, $5, $6)
	ON CONFLICT (account_id) DO UPDATE SET
		digest     = EXCLUDED.digest,
		mime       = EXCLUDED.mime,
		byte_size  = EXCLUDED.byte_size,
		source_url = EXCLUDED.source_url,
		fetched_at = EXCLUDED.fetched_at`

const getAvatarSQL = `
	SELECT account_id, digest, mime, byte_size, source_url, fetched_at
	FROM account_avatars
	WHERE account_id = $1`

// PutAvatar records the local copy. The caller has already written the bytes
// to disk under the digest's name, so a failure here leaves an unreferenced
// file rather than a row pointing at nothing — the safe direction to fail in,
// and why the write-then-record order is not the other way round.
func (s *Store) PutAvatar(ctx context.Context, a store.Avatar) error {
	if _, err := s.db.Exec(ctx, putAvatarSQL,
		a.AccountID, a.Digest, a.MIME, a.ByteSize, a.SourceURL, utc(a.FetchedAt)); err != nil {
		return wrap("put avatar", err)
	}
	return nil
}

// GetAvatar returns the recorded copy, or ErrNotFound when there is none. A
// miss here is normal — a deployment with no avatar directory, or a sign-in
// that produced no picture — and callers are expected to treat it as a blank
// avatar rather than a fault.
func (s *Store) GetAvatar(ctx context.Context, accountID store.ID) (store.Avatar, error) {
	a, err := scanAvatar(s.db.QueryRow(ctx, getAvatarSQL, accountID))
	if err != nil {
		return store.Avatar{}, notFound("get avatar", err)
	}
	return a, nil
}

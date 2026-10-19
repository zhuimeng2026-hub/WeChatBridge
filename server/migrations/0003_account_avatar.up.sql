-- 0003_account_avatar.up.sql — a local copy of the WeChat avatar.
--
-- Why this table exists at all, given accounts already has avatar_url:
--
-- WeChat's headimgurl points at wx.qlogo.cn and that URL is good for days,
-- not forever. A client that stored it and rendered it a week later got a
-- broken image, and the fix "log in again" is not available to a person who
-- signed in once. So the bytes are fetched once, verified by content sniffing,
-- written to disk under WCB_AVATAR_DIR, and only the fingerprint of those
-- bytes goes in this table. The row answers three questions and nothing else:
-- which file on disk, what content type, and when it was fetched.
--
-- It is a separate table rather than three more columns on accounts on
-- purpose. accounts is the projection every query in the auth package reads
-- (accountColumns), so growing it makes every scan in the service carry three
-- values that only one handler reads. This way the avatar path costs one extra
-- query for the one request that needs a picture, and the account projection
-- stays exactly as wide as it was.
--
-- No BEGIN/COMMIT: internal/migrate (cmd/wcb/migrate.go) wraps each file in
-- its own transaction together with its bookkeeping row.

CREATE TABLE account_avatars (
    account_id text        PRIMARY KEY REFERENCES accounts (id) ON DELETE CASCADE,

    -- The SHA-256 of the bytes, lowercase hex. This is the cache key and the
    -- file name, so it is also the ETag the client sees: an unchanged avatar
    -- is a 304, and a changed one is a new file under a new name with the old
    -- one left for the operator to sweep.
    digest      text        NOT NULL,

    -- Exactly what was sniffed off the wire, never what the server claimed in
    -- Content-Type. A remote host that says image/png and sends HTML is an
    -- attack or a captive portal, and storing the claim would hand the client
    -- bytes its decoder will choke on.
    mime        text        NOT NULL,

    -- The byte count as written. Fetched for the size cap and kept because a
    -- truncated read is otherwise invisible until somebody looks at the file.
    byte_size   integer     NOT NULL,

    -- The WeChat URL these bytes came from. Kept so a refresh can be
    -- attempted against the same source, and so an operator can tell "we
    -- never fetched one" from "we fetched one that has since been deleted
    -- from disk".
    source_url  text        NOT NULL DEFAULT '',

    fetched_at  timestamptz NOT NULL,

    -- A digest is a digest: 64 hex characters. Enforced here rather than in
    -- the application because this string is used to build a file path, and a
    -- CHECK that runs on every write is cheaper than sanitising on every read.
    CONSTRAINT account_avatars_digest_check
        CHECK (digest ~ '^[0-9a-f]{64}$'),

    -- Only the two formats the avatar fetch accepts. Anything else was
    -- rejected before it got here, and a constraint that restates the
    -- application rule is the one that still holds when the application is
    -- changed and the rule is not.
    CONSTRAINT account_avatars_mime_check
        CHECK (mime IN ('image/jpeg', 'image/png')),

    CONSTRAINT account_avatars_byte_size_check
        CHECK (byte_size > 0)
);

-- fetched_at is read on every sign-in to decide whether the copy is older
-- than the refresh TTL. Without this the table is scanned on every login,
-- which is the one request that must not get slower as accounts accumulate.
CREATE INDEX account_avatars_fetched_at_idx ON account_avatars (fetched_at);

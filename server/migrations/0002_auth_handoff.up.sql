-- 0002_auth_handoff.up.sql — shared token bridge between the WeChat redirect
-- and the client's poll.
--
-- The sign-in mints the tokens in the redirect handler (instance A) and the
-- desktop client collects them by polling (instance B, behind a load balancer).
-- The handoff must therefore live in shared storage, not in one process's
-- memory, or a scan completed on A is uncollectable on B and the client hangs
-- at the QR code forever.
--
-- This relaxes the long-standing rule that the database never holds a usable
-- token. It is the correct trade now: the tokens already travel to the client
-- over HTTPS and are written to its disk, and the row is transient — deleted on
-- first take and swept once its ticket expires. The alternative, an in-memory
-- handoff, breaks the moment two processes are involved, which is the normal
-- case behind a load balancer.
--
-- No BEGIN/COMMIT here: internal/migrate wraps each file in its own transaction
-- together with the bookkeeping row.

CREATE TABLE auth_handoffs (
    ticket            text        PRIMARY KEY,
    access_token      text        NOT NULL,
    refresh_token     text        NOT NULL,
    access_expires_at timestamptz NOT NULL,
    account_id        text        NOT NULL REFERENCES accounts (id) ON DELETE CASCADE,
    expires_at        timestamptz NOT NULL,
    created_at        timestamptz NOT NULL DEFAULT now()
);

-- The poll looks up by ticket; the sweep and any cleanup read by expiry.
CREATE INDEX auth_handoffs_expires_at_idx ON auth_handoffs (expires_at);

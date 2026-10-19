-- 0003_activation_rejected.up.sql — a seller has to be able to say no.
--
-- The four admin endpoints could confirm, reissue and revoke, but nothing could
-- produce a rejected row. ActivationState.Rejected existed, the client's poll
-- loop already handled it, and no code path ever set it — so a buyer who paid
-- and whom the seller refused (wrong machine code, a transfer that never
-- arrived, a duplicated order) simply had a row sitting at pending forever and
-- a window that never closed.
--
-- Adding the endpoint needs somewhere to put the timestamp. A ledger that
-- records that a request was rejected, but not when, answers half the question
-- a support conversation asks.
--
-- NULL here means "not rejected", which is why this is additive and needs no
-- backfill: every existing row is already in that state.
--
-- The partial unique index activation_requests_live_install_idx is on
-- status = 'pending', so rejecting frees the machine for a fresh registration
-- with no extra step. That is the behaviour a seller wants: refusing an order
-- must not lock the buyer out of trying again.
--
-- No BEGIN/COMMIT: internal/migrate wraps each file with its bookkeeping row.

ALTER TABLE activation_requests
    ADD COLUMN rejected_at timestamptz;

-- The flip writes status and rejected_at together, so nothing should be able to
-- hold one without the other. A confirmed row is complete by the existing
-- activation_requests_confirmed_is_complete; this is the same guarantee for
-- the other terminal state.
ALTER TABLE activation_requests
    ADD CONSTRAINT activation_requests_rejected_is_dated
    CHECK (status <> 'rejected' OR rejected_at IS NOT NULL);

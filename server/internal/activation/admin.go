package activation

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"fmt"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
	"github.com/freestylefly/WeChatBridge/server/internal/license"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// Sentinel errors the admin handlers map onto specific replies.
var (
	// ErrRequestNotFound is a 单号 or licence id the ledger has never held.
	ErrRequestNotFound = errors.New("activation: request not found")

	// ErrRequestRejected is the seller saying no. It is an answer, not a
	// failure, and it is final: refreshing the console will not change it.
	ErrRequestRejected = errors.New("activation: request was rejected")

	// ErrRequestNotPending is an action whose precondition the row no longer
	// meets — usually two sellers racing one confirm, or a machine whose
	// account already holds a live licence elsewhere. The loser refreshes.
	ErrRequestNotPending = errors.New("activation: request is not in the required state")
)

// buyoutEntitlements is the whole set, and that is the product decision
// (2026-10-18): a buy-out licence unlocks every capability the client knows.
// There is no "buyout basic" — the price is the price and the licence is
// perpetual.
func buyoutEntitlements() []string {
	return []string{
		license.EntitlementPro,
		license.EntitlementPDF,
		license.EntitlementExport,
		license.EntitlementScene,
		license.EntitlementHistory,
	}
}

// newLicenseID mints the id the ledger, the console and the status poll all
// quote. The prefix is what the mock used, so a support conversation reads
// the same on either side of the cutover.
func newLicenseID() (string, error) {
	var buf [12]byte
	if _, err := rand.Read(buf[:]); err != nil {
		return "", err
	}
	return "lic_" + hex.EncodeToString(buf[:]), nil
}

// AdminGuard admits only allowlisted administrators.
//
// D5 (design §11.7, confirmed 2026-10-18): the seller's console has no
// password of its own. An administrator is a WeChat account on the
// WCB_ADMIN_OPENIDS allowlist that has completed the same scan-code login
// as any buyer — one credential system, sessions that expire, and no second
// secret to leak. The cost is that losing the WeChat account loses admin,
// which is a support path, not a security hole.
//
// An empty allowlist is a deployment mistake, not a policy: every admin call
// answers 503 saying so, because the alternative — admitting every signed-in
// buyer to the confirm button — turns a misconfiguration into issued
// licences.
func (s *Service) AdminGuard() gin.HandlerFunc {
	return func(c *gin.Context) {
		if len(s.adminOpenIDs) == 0 {
			s.log.Warn("activation: admin endpoint called with an empty allowlist")
			// Unavailable, not Forbidden: this is the server's
			// misconfiguration, and a 403 would send the seller looking for
			// a permission problem on their own account.
			httpx.Fail(c, httpx.Unavailable("管理后台尚未配置管理员名单，请联系运维。"))
			return
		}
		accountID, ok := s.accountOf(c)
		if !ok {
			httpx.Fail(c, httpx.Unauthorized("缺少访问令牌。"))
			return
		}
		acct, err := s.accountByID(c.Request.Context(), store.ID(accountID))
		if err != nil {
			if errors.Is(err, store.ErrNotFound) {
				// The token verified but its account is gone. Fail closed,
				// and say nothing about why.
				httpx.Fail(c, httpx.Forbidden("当前账号不是管理员。"))
				return
			}
			httpx.Fail(c, httpx.FromStore(err))
			return
		}
		if !s.isAdmin(acct.OpenID) {
			httpx.Fail(c, httpx.Forbidden("当前账号不是管理员。"))
			return
		}
	}
}

// isAdmin is an exact match, not a prefix or a case-insensitive one: a WeChat
// openid is a fixed opaque string, and fuzzy matching an allowlist is how
// "oX3f..." admin starts admitting "oX3F...".
func (s *Service) isAdmin(openID string) bool {
	for _, id := range s.adminOpenIDs {
		if id == openID {
			return true
		}
	}
	return false
}

// RegisterAdminRoutes mounts the seller's four endpoints.
//
// The bearer middleware is passed in for the same reason RegisterRoutes takes
// it: the composition root owns what is mounted. The guard rides along behind
// it, so an admin route is requireAuth + allowlist and nothing else.
func (s *Service) RegisterAdminRoutes(r gin.IRouter, requireAuth gin.HandlerFunc) {
	group := r.Group("/v1/admin", requireAuth, s.AdminGuard())
	group.GET("/requests", s.handleAdminList)
	group.POST("/confirm", s.handleAdminConfirm)
	group.POST("/reissue", s.handleAdminReissue)
	group.POST("/revoke", s.handleAdminRevoke)
}

// AdminListItem is one row of the seller's queue. The nickname and avatar
// come along from the store's join — one query for the whole table, because
// a queue that fires a profile read per row is a queue that falls over the
// first time the seller has a busy day.
type AdminListItem struct {
	RequestID    string
	InstallID    string
	AnchorDigest string
	Nickname     string
	AvatarURL    string
	// AmountFen is what the buyer says they were asked to pay. Nullable on
	// the wire exactly as it is in the database: the amount is advisory and
	// a buyer who never stated one is not an error.
	AmountFen *int64
	CreatedAt time.Time
	Status    store.ActivationStatus
	LicenseID string
}

// AdminList reads the queue, newest first.
func (s *Service) AdminList(ctx context.Context, status store.ActivationStatus) ([]AdminListItem, error) {
	views, err := s.store.ListActivationRequests(ctx, status)
	if err != nil {
		return nil, err
	}
	out := make([]AdminListItem, 0, len(views))
	for _, v := range views {
		out = append(out, AdminListItem{
			RequestID:    v.RequestID,
			InstallID:    v.InstallID,
			AnchorDigest: v.AnchorDigest,
			Nickname:     v.Nickname,
			AvatarURL:    v.AvatarURL,
			AmountFen:    v.AmountFen,
			CreatedAt:    v.CreatedAt,
			Status:       v.Status,
			LicenseID:    v.LicenseID,
		})
	}
	return out, nil
}

// AdminConfirm is the money moment: the seller has matched the transfer to a
// machine code, and this signs the perpetual licence that machine will verify
// offline forever after.
//
// It is idempotent by request id — a second click on an already-confirmed row
// returns the licence id that was issued, because the seller double-clicking
// is not a reason to issue two licences for one payment, and a 409 there would
// read as "something went wrong" on a screen that is in fact correct. Two
// sellers racing one row is the store's transaction to settle (one flip, one
// binding); the loser re-reads and gets the winner's answer.
func (s *Service) AdminConfirm(ctx context.Context, requestID string) (string, error) {
	r, err := s.store.GetActivationRequest(ctx, requestID)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return "", ErrRequestNotFound
		}
		return "", err
	}
	switch r.Status {
	case store.ActivationConfirmed:
		return r.LicenseID, nil
	case store.ActivationRejected:
		// The seller already said no to this registration. A confirm is not
		// an appeal.
		return "", ErrRequestRejected
	}

	envelope, err := s.issueBuyout(r.InstallID)
	if err != nil {
		return "", err
	}
	raw, err := envelope.JSON()
	if err != nil {
		return "", fmt.Errorf("activation: marshal envelope: %w", err)
	}
	licenseID, err := newLicenseID()
	if err != nil {
		return "", fmt.Errorf("activation: mint licence id: %w", err)
	}

	confirmed, err := s.store.ConfirmActivationRequest(ctx, requestID, licenseID, raw, s.now())
	if err != nil {
		if errors.Is(err, store.ErrConflict) {
			// Lost the race, or a live binding already pins this machine or
			// this account. Re-read rather than assume: if the winner
			// confirmed, this is still one payment and one answer.
			if fresh, rerr := s.store.GetActivationRequest(ctx, requestID); rerr == nil &&
				fresh.Status == store.ActivationConfirmed {
				return fresh.LicenseID, nil
			}
			return "", ErrRequestNotPending
		}
		return "", err
	}
	return confirmed.LicenseID, nil
}

// AdminReissue re-signs a machine's licence: same subject, same plan, same
// entitlements, fresh issue time. The store retires the machine's live
// binding and installs the new one in one transaction, and if the buyer has
// an outstanding registration for this machine it is confirmed with the new
// licence — which is how their poller learns about the reissue at all.
//
// Two honest notes on what this is not. It is not a machine migration for a
// buyer who already holds a live licence elsewhere: the one-live-binding-
// per-account index makes that a conflict, and moving a licence to a new
// machine is revoke-then-reissue, a seller decision by design. And the old
// envelope keeps verifying wherever it already landed — offline verification
// has no revocation channel, which §6 of the design document says out loud
// rather than pretending otherwise.
func (s *Service) AdminReissue(ctx context.Context, installID string) (string, error) {
	envelope, err := s.issueBuyout(installID)
	if err != nil {
		return "", err
	}
	raw, err := envelope.JSON()
	if err != nil {
		return "", fmt.Errorf("activation: marshal envelope: %w", err)
	}
	licenseID, err := newLicenseID()
	if err != nil {
		return "", fmt.Errorf("activation: mint licence id: %w", err)
	}

	if _, err := s.store.ReissueLicence(ctx, installID, licenseID, raw, s.now()); err != nil {
		if errors.Is(err, store.ErrNotFound) {
			// No live binding and no open registration for this machine: a
			// reissue that invented a licence out of thin air would be a
			// support call nobody can explain.
			return "", ErrRequestNotFound
		}
		if errors.Is(err, store.ErrConflict) {
			// The account already holds a live licence on another machine.
			return "", ErrRequestNotPending
		}
		return "", err
	}
	return licenseID, nil
}

// AdminRevoke marks a licence revoked in the ledger.
//
// What it does not do is un-sign anything: the envelope the customer already
// downloaded keeps verifying offline, forever, because the client checks a
// signature and not a phone call. What revocation buys is the ledger (support
// can say a machine's licence is dead), the bindings (the machine is free to
// be reissued), and the next poll — the customer half of this package reads
// the live binding, so a revoked licence is not re-handed-out here either.
// Revoking twice succeeds — the store tells "already revoked" from "never
// issued", and only the second is a 404.
func (s *Service) AdminRevoke(ctx context.Context, licenseID string) error {
	if err := s.store.RevokeBinding(ctx, licenseID, s.now()); err != nil {
		if errors.Is(err, store.ErrNotFound) {
			return ErrRequestNotFound
		}
		return err
	}
	return nil
}

// issueBuyout signs one perpetual, full-entitlement licence for a machine.
func (s *Service) issueBuyout(installID string) (license.Envelope, error) {
	envelope, err := s.signer.Build(license.IssueParams{
		Subject:      installID,
		Plan:         license.PlanBuyout,
		Entitlements: buyoutEntitlements(),
		IssuedAt:     s.now(),
		// Lifetime left zero on purpose: a buy-out licence has no expiry,
		// and that absence is the product.
	})
	if err != nil {
		return license.Envelope{}, fmt.Errorf("activation: sign buyout licence: %w", err)
	}
	return envelope, nil
}

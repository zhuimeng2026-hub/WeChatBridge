// Package admin is the seller's console: the four endpoints that turn a
// buyer's registration into a signed licence.
//
//	GET  /v1/admin/requests?status=pending   -> the table the seller works from
//	POST /v1/admin/confirm   {requestId}     -> {licenseId}
//	POST /v1/admin/reject    {requestId}     -> {}
//	POST /v1/admin/reissue   {installId}     -> {licenseId}
//	POST /v1/admin/revoke    {licenseId}     -> {}
//
// Authentication is the buyer's own. D5's separate console password is gone
// (design §11.7, M-1): the admin API takes the same WeChat sign-in bearer
// token the activation API takes, and the WCB_ADMIN_OPENIDS whitelist decides
// which signed-in accounts are sellers. A token that verifies but whose
// account is not on the list is a 403, not a 401 — the credential was fine,
// the identity behind it is simply not a seller.
//
// Three rules shape the handlers:
//
//   - The seller's identity comes from the token, never from the body, for
//     the same reason the buyer's does.
//   - Confirm is idempotent per request. Two sellers, one button, one
//     licence: the second press reads back the licence the first one minted
//     rather than minting a second.
//   - An envelope is built and signed exactly once per licence. ECDSA picks a
//     fresh k per signature, so re-signing the same claims would hand the
//     buyer a different token on every poll; the store writes the bytes once
//     and the buyer's licence endpoint returns those bytes.
//
// One confirm outcome is a standing state rather than a failure: a machine
// that already holds a live licence cannot be confirmed again, and the answer
// names reissue — the only action that can give that buyer what they came
// for. "Refresh and try again" would be true of neither, and it leaves the
// buyer stuck while the seller retries a button that can never succeed.
//
// One behaviour is deliberately stricter than the mock this package was
// written against: an unknown ?status= value is a 400, not an empty list.
// The mock filters permissively, and a seller who typo'd "comfirmed" would be
// told there is nothing to confirm — the one wrong answer this API must not
// give.
package admin

import (
	"context"
	"crypto/rand"
	"encoding/hex"
	"errors"
	"log/slog"
	"strings"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
	"github.com/freestylefly/WeChatBridge/server/internal/license"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
	"github.com/freestylefly/WeChatBridge/server/internal/token"
)

// ErrRequestRejected is a confirm of a request the seller already said no to.
// It is an answer, not a failure: the row stays for the ledger, and the path
// that revives that machine is a reissue.
var ErrRequestRejected = errors.New("admin: request was rejected")

// ErrRequestConfirmed is a refuse aimed at a request that already carries a
// licence. The two actions are not interchangeable — one stops a purchase, the
// other takes back something that was already granted — so the API refuses to
// treat one as the other.
var ErrRequestConfirmed = errors.New("admin: request is already confirmed")

// ErrLiveLicenceHeld is a confirm aimed at a machine that already holds a live
// licence.
//
// It is a standing state, not a lost race, and that difference is the whole
// reason it has its own name. The store reports the two separately
// (store.ErrLiveBindingExists) because a lost race is self-healing — the loser
// reads back the licence the winner minted — while this one cannot change no
// matter how many times the button is pressed: the machine's licence was
// granted by an earlier, already-confirmed request, and the buyer has simply
// registered the same machine again.
//
// The seller's way forward is a reissue, so the answer has to say so. A
// generic "refresh and try again" is the worst possible reply here: it is
// true of neither, it names no action, and it leaves the buyer stuck on
// "登记中" while the seller retries forever.
var ErrLiveLicenceHeld = errors.New("admin: machine already holds a live licence")

// Service is the console's logic. It is safe for concurrent use.
type Service struct {
	auth       store.AuthStore
	activation store.ActivationStore
	signer     *license.Signer

	// admins is the whitelist as a set. An empty set is a wiring error, not a
	// console that trusts everybody: NewService refuses it.
	admins map[string]struct{}

	log            *slog.Logger
	now            func() time.Time
	buyoutLifetime time.Duration

	// accountOf reads the account a verified bearer token belongs to. It is
	// auth's to define and this package's to call, exactly as in the
	// activation package: production passes auth.AccountIDFrom.
	accountOf func(*gin.Context) (string, bool)
}

// Options are the wiring knobs. The stores, the signer, the account reader
// and a non-empty whitelist are all required: without the signer there is no
// mint, and without a whitelist there is no seller.
type Options struct {
	Auth       store.AuthStore
	Activation store.ActivationStore
	Signer     *license.Signer

	// AdminOpenIDs is the WeChat OpenID whitelist (config WCB_ADMIN_OPENIDS).
	// At least one entry is required.
	AdminOpenIDs []string

	Logger *slog.Logger
	// Now defaults to the wall clock. Tests set it so a licence can be dated
	// without sleeping.
	Now func() time.Time
	// BuyoutLifetime is zero unless an operator configured expiry days. Zero
	// is the product's definition: a buy-out licence has no expiry.
	BuyoutLifetime time.Duration
	// AccountOf reads the authenticated account off a request. Production
	// passes auth.AccountIDFrom; a test passes whatever it needs the claims
	// to say.
	AccountOf func(*gin.Context) (string, bool)
}

// NewService wires the console together.
func NewService(opts Options) (*Service, error) {
	if opts.Auth == nil {
		return nil, errors.New("admin: auth store is required")
	}
	if opts.Activation == nil {
		return nil, errors.New("admin: activation store is required")
	}
	if opts.Signer == nil {
		return nil, errors.New("admin: signer is required")
	}
	if opts.AccountOf == nil {
		return nil, errors.New("admin: account reader is required")
	}
	if len(opts.AdminOpenIDs) == 0 {
		// The console is the mint. A deployment that mounts it without a
		// whitelist is one where any signed-in account can sign licences —
		// loud at wiring time, never at request time.
		return nil, errors.New("admin: at least one admin OpenID is required")
	}
	admins := make(map[string]struct{}, len(opts.AdminOpenIDs))
	for _, openID := range opts.AdminOpenIDs {
		if trimmed := strings.TrimSpace(openID); trimmed != "" {
			admins[trimmed] = struct{}{}
		}
	}
	if len(admins) == 0 {
		return nil, errors.New("admin: admin OpenID whitelist is empty after trimming")
	}
	if opts.Logger == nil {
		opts.Logger = slog.Default()
	}
	if opts.Now == nil {
		opts.Now = func() time.Time { return time.Now().UTC() }
	}
	return &Service{
		auth:           opts.Auth,
		activation:     opts.Activation,
		signer:         opts.Signer,
		admins:         admins,
		log:            opts.Logger,
		now:            opts.Now,
		buyoutLifetime: opts.BuyoutLifetime,
		accountOf:      opts.AccountOf,
	}, nil
}

// RegisterRoutes mounts the four endpoints.
//
// The bearer middleware is passed in rather than constructed here, for the
// same reason the activation package takes one: the composition root owns
// what is mounted and in what order. The whitelist check behind it is this
// package's own.
func (s *Service) RegisterRoutes(r gin.IRouter, requireAuth gin.HandlerFunc) {
	group := r.Group("/v1/admin", requireAuth, s.requireAdmin())
	group.GET("/requests", s.handleRequests)
	group.POST("/confirm", s.handleConfirm)
	group.POST("/reject", s.handleReject)
	group.POST("/reissue", s.handleReissue)
	group.POST("/revoke", s.handleRevoke)
}

// requireAdmin resolves the token's account and checks it against the
// whitelist. Every admin endpoint sits behind it, so the check lives here
// once rather than in four handlers that could drift apart.
func (s *Service) requireAdmin() gin.HandlerFunc {
	return func(c *gin.Context) {
		accountID, ok := s.accountOf(c)
		if !ok {
			httpx.Fail(c, httpx.Unauthorized("缺少访问令牌。"))
			c.Abort()
			return
		}
		account, err := s.auth.GetAccount(c.Request.Context(), accountID)
		if err != nil {
			// A token for an account that no longer exists verifies but
			// identifies nobody: that is a 403, the same answer a
			// non-whitelisted account gets. Anything else is a real fault.
			if errors.Is(err, store.ErrNotFound) {
				s.log.Info("admin API refused: the token's account no longer exists",
					"account_id", accountID, "path", c.FullPath())
				httpx.Fail(c, httpx.Forbidden("没有管理员权限。"))
			} else {
				httpx.Fail(c, httpx.Internal(err))
			}
			c.Abort()
			return
		}
		if !s.isAdmin(account.OpenID) {
			// Somebody signed in and reached for the mint. Low-traffic by
			// nature, and exactly the event an operator wants in the log.
			s.log.Info("admin API refused: account is not on the whitelist",
				"account_id", accountID, "open_id", account.OpenID, "path", c.FullPath())
			httpx.Fail(c, httpx.Forbidden("没有管理员权限。"))
			c.Abort()
			return
		}
		c.Next()
	}
}

func (s *Service) isAdmin(openID string) bool {
	_, ok := s.admins[strings.TrimSpace(openID)]
	return ok
}

// Confirm signs the licence for a pending request.
//
// The read-then-write is deliberately not one transaction: the seller is a
// person reading a list, and the race worth caring about is two people
// pressing the same button. The store's conditional flip settles that — the
// loser of the race reads a conflict, re-reads the row, and returns the
// licence the winner minted. One request, one licence, whichever seller got
// there first.
//
// The store's two refusals are not interchangeable, and this method keeps them
// apart. A conflict means somebody else won a race: the loser has an answer
// (the winner's licence) and returns it. A live binding already existing
// means the machine holds a licence from an earlier confirmed request: there
// is no answer to read back, no retry can change it, and the only button that
// can give that buyer what they came for is reissue.
func (s *Service) Confirm(ctx context.Context, requestID string) (string, error) {
	r, err := s.activation.GetActivationRequest(ctx, requestID)
	if err != nil {
		return "", err
	}
	switch r.Status {
	case store.ActivationConfirmed:
		// Already signed: the same licence, not a second one. This is also
		// where a double-click lands.
		return r.LicenseID, nil
	case store.ActivationRejected:
		return "", ErrRequestRejected
	}

	licenseID, err := newLicenseID()
	if err != nil {
		return "", err
	}
	now := s.now()
	envelopeJSON, err := s.signEnvelope(r.InstallID, now)
	if err != nil {
		return "", err
	}

	confirmed, err := s.activation.ConfirmActivationRequest(ctx, requestID, licenseID, envelopeJSON, now)
	if err != nil {
		// Checked before the conflict below, and deliberately not folded into
		// it. The live-binding collision is not a race: the machine's licence
		// was granted by an earlier request, so re-reading the row finds a
		// request that is still pending and no licence to return. Swallowing
		// it into the generic conflict is what made this button answer 409
		// forever.
		if errors.Is(err, store.ErrLiveBindingExists) {
			s.log.Info("confirm refused: the machine already holds a live licence",
				"request", token.Fingerprint(requestID),
				"install_id", token.Fingerprint(r.InstallID),
				"next_action", "reissue")
			return "", ErrLiveLicenceHeld
		}
		if errors.Is(err, store.ErrConflict) {
			// The flip refused: somebody else confirmed between our read and
			// our write. Their licence is the answer to this button.
			if latest, rerr := s.activation.GetActivationRequest(ctx, requestID); rerr == nil &&
				latest.Status == store.ActivationConfirmed {
				return latest.LicenseID, nil
			}
		}
		return "", err
	}
	return confirmed.LicenseID, nil
}

// Reject refuses an order: the transfer never arrived, the machine code does
// not match, the buyer changed their mind.
//
// It writes no licence and revokes nothing. A rejected request never had a
// licence, and if that machine still holds a live one from an earlier order it
// keeps it — refusing this order is not revoking that licence. Revoking is what
// Revoke does, and it takes a licence id.
//
// The row stays for the ledger. The path that brings that machine back is a
// fresh registration: the partial unique index on pending requests means
// rejecting frees the machine immediately, with no extra step.
func (s *Service) Reject(ctx context.Context, requestID string) error {
	_, err := s.activation.RejectActivationRequest(ctx, requestID, s.now())
	if err != nil {
		// Already confirmed is the one worth naming. Confirming after the fact
		// is Revoke's job, and saying "rejected" there would be a lie about
		// which of the two actually happened.
		if errors.Is(err, store.ErrConflict) {
			return ErrRequestConfirmed
		}
		return err
	}
	s.log.Info("request rejected", "request", token.Fingerprint(requestID))
	return nil
}

// Reissue is the machine-change path: retire whatever licence the machine
// holds and sign a fresh one for the same install id.
//
// The store files a confirmed request row carrying the new envelope — the
// pending request for that machine if there is one, otherwise a fresh row
// copying the machine's earlier registration. That row is how the buyer's
// poller learns about the reissue at all: a licence nobody can poll for is a
// licence nobody collects. ErrNotFound means the machine was never heard of.
func (s *Service) Reissue(ctx context.Context, installID string) (string, error) {
	licenseID, err := newLicenseID()
	if err != nil {
		return "", err
	}
	now := s.now()
	envelopeJSON, err := s.signEnvelope(installID, now)
	if err != nil {
		return "", err
	}
	reissued, err := s.activation.ReissueLicence(ctx, installID, licenseID, envelopeJSON, now)
	if err != nil {
		return "", err
	}
	return reissued.LicenseID, nil
}

// Revoke marks a licence dead. The row stays — the honest answer to "is this
// licence still valid" is a date, not a deletion — and a second revoke of
// the same licence succeeds, because a misclick on a stale console is not an
// error the seller needs to see. ErrNotFound means no such licence.
func (s *Service) Revoke(ctx context.Context, licenseID string) error {
	return s.activation.RevokeBinding(ctx, licenseID, s.now())
}

// signEnvelope builds and signs the buy-out licence for one machine, and
// renders the exact bytes the store keeps.
//
// The plan and the entitlement are the product, not per-request inputs: the
// convergence deleted every other plan, so a licence this API signs is a
// buy-out licence the way the mock's was. The subject is the install id —
// the machine, not the account — because a licence follows the machine
// through a machine change, which is the whole point of reissue.
func (s *Service) signEnvelope(installID string, now time.Time) ([]byte, error) {
	envelope, err := s.signer.Build(license.IssueParams{
		Subject: installID,
		Plan:    license.PlanBuyout,
		// A buy-out licence unlocks every capability the client knows
		// (§11.7 M-6). The order here carries no meaning: the canonical
		// payload sorts and de-duplicates the bits before signing.
		Entitlements: []string{
			license.EntitlementPro,
			license.EntitlementPDF,
			license.EntitlementExport,
			license.EntitlementScene,
			license.EntitlementHistory,
		},
		Lifetime: s.buyoutLifetime,
		IssuedAt: now,
	})
	if err != nil {
		return nil, err
	}
	return envelope.JSON()
}

// newLicenseID mints the id the seller quotes in a support conversation. The
// prefix is the mock's and the client logs'; the entropy is ours.
func newLicenseID() (string, error) {
	var buf [12]byte
	if _, err := rand.Read(buf[:]); err != nil {
		return "", err
	}
	return "lic_" + hex.EncodeToString(buf[:]), nil
}

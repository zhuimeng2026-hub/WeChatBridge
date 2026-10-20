package admin

import (
	"errors"
	"io"
	"net/http"
	"strings"
	"time"

	"github.com/gin-gonic/gin"

	"github.com/freestylefly/WeChatBridge/server/internal/httpx"
	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// ---------------------------------------------------------------------------
// Request and response bodies
//
// Field names are the contract (design §11.3, mock/server.js, the console's
// api.js). Go's default marshalling is the Go field name, so a missing tag
// here is a field the console never binds — the same rule, and the same
// reason, as the activation package's.
// ---------------------------------------------------------------------------

// requestItem is one row of the seller's table. createdAt is RFC 3339 in
// UTC with no fractional seconds, which is what the mock's stamp() rendered
// and what the console parses.
type requestItem struct {
	RequestID    string `json:"requestId"`
	InstallID    string `json:"installId"`
	AnchorDigest string `json:"anchorDigest"`
	Nickname     string `json:"nickname"`
	AmountFen    *int64 `json:"amountFen"`
	CreatedAt    string `json:"createdAt"`
	Status       string `json:"status"`
	// LicenseID is the licence this row produced, and the empty string for a
	// row the seller has not confirmed yet.
	//
	// It was missing here for as long as this handler existed, and the console
	// spent that time keeping its own copy of it in localStorage — a browser's
	// recollection of what the server already knew. The cost was not cosmetic:
	// a confirmed row whose licence number the console could not recall had no
	// revoke button at all, so a seller could see that a licence existed and be
	// unable to kill it. Opening a second browser, or clearing site data, was
	// enough to put it there permanently.
	//
	// The store has always carried the value (ActivationRequest.LicenseID); only
	// this projection dropped it. Added under design §11.7 M-2.
	LicenseID string `json:"licenseId"`
}

type requestsResponse struct {
	Items []requestItem `json:"items"`
}

type confirmRequest struct {
	RequestID string `json:"requestId"`
}
type rejectRequest struct {
	RequestID string `json:"requestId"`
}

type reissueRequest struct {
	InstallID string `json:"installId"`
}

type revokeRequest struct {
	LicenseID string `json:"licenseId"`
}

type licenseResponse struct {
	LicenseID string `json:"licenseId"`
}

// ---------------------------------------------------------------------------
// Handlers
// ---------------------------------------------------------------------------

// handleRequests is the console's table. An absent status means every state,
// which is what the ledger page asks for; a status that is not one of the
// three the product has is a 400 rather than a silently empty list — see the
// package comment.
func (s *Service) handleRequests(c *gin.Context) {
	want := strings.TrimSpace(c.Query("status"))
	status := store.ActivationStatus(want)
	if want != "" && !knownStatus(status) {
		httpx.Fail(c, httpx.BadRequest("状态筛选值不正确。"))
		return
	}

	views, err := s.activation.ListActivationRequests(c.Request.Context(), status)
	if err != nil {
		httpx.Fail(c, httpx.FromStore(err))
		return
	}

	items := make([]requestItem, 0, len(views))
	for _, v := range views {
		items = append(items, requestItem{
			RequestID:    v.RequestID,
			InstallID:    v.InstallID,
			AnchorDigest: v.AnchorDigest,
			Nickname:     v.Nickname,
			AmountFen:    v.AmountFen,
			CreatedAt:    v.CreatedAt.UTC().Format(time.RFC3339),
			Status:       string(v.Status),
			LicenseID:    v.LicenseID,
		})
	}
	httpx.OK(c, http.StatusOK, requestsResponse{Items: items})
}

func (s *Service) handleReject(c *gin.Context) {
	var body rejectRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	requestID := strings.TrimSpace(body.RequestID)
	if requestID == "" {
		httpx.Fail(c, httpx.BadRequest("请求号缺失。"))
		return
	}

	if err := s.Reject(c.Request.Context(), requestID); err != nil {
		switch {
		case errors.Is(err, store.ErrNotFound):
			httpx.Fail(c, httpx.NotFound("请求的内容不存在。"))
		case errors.Is(err, ErrRequestConfirmed):
			// Distinct from a rejected row, and the distinction is the point:
			// a licence already exists here, so what the seller wants is
			// /revoke, not /reject. Saying "already rejected" would send them
			// to a button that cannot do what they need.
			httpx.Fail(c, httpx.Conflict("该请求已签发许可证，不能拒绝；请改用作废。"))
		default:
			httpx.Fail(c, httpx.FromStore(err))
		}
		return
	}

	c.Status(http.StatusNoContent)
}

func (s *Service) handleConfirm(c *gin.Context) {
	var body confirmRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	requestID := strings.TrimSpace(body.RequestID)
	if requestID == "" {
		httpx.Fail(c, httpx.BadRequest("请求号缺失。"))
		return
	}

	licenseID, err := s.Confirm(c.Request.Context(), requestID)
	if err != nil {
		switch {
		case errors.Is(err, store.ErrNotFound):
			httpx.Fail(c, httpx.NotFound("请求的内容不存在。"))
		case errors.Is(err, ErrRequestRejected):
			httpx.Fail(c, httpx.Conflict("该购买请求已被拒绝，无法确认。"))
		case errors.Is(err, ErrLiveLicenceHeld):
			// The machine already holds a live licence from an earlier
			// confirmed request, so this order cannot be confirmed — not now,
			// not after a refresh, not ever by pressing this button again.
			// The reply has to name the action that does work, in the same
			// register as handleReject's "请改用作废": the seller's next click
			// has to be the right button, or the buyer stays stuck on "登记中"
			// while the console keeps offering them the wrong one.
			httpx.Fail(c, httpx.Conflict("该机器已有有效许可证，请改用重签（reissue）。"))
		default:
			// Includes the lost race the service could not reconcile: the
			// row moved again between the re-read and here.
			httpx.Fail(c, httpx.FromStore(err))
		}
		return
	}

	httpx.OK(c, http.StatusOK, licenseResponse{LicenseID: licenseID})
}

func (s *Service) handleReissue(c *gin.Context) {
	var body reissueRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	installID := strings.TrimSpace(body.InstallID)
	if installID == "" {
		httpx.Fail(c, httpx.BadRequest("机器码缺失。"))
		return
	}

	licenseID, err := s.Reissue(c.Request.Context(), installID)
	if err != nil {
		if errors.Is(err, store.ErrNotFound) {
			httpx.Fail(c, httpx.NotFound("该机器没有可重签的购买记录。"))
			return
		}
		httpx.Fail(c, httpx.FromStore(err))
		return
	}

	httpx.OK(c, http.StatusOK, licenseResponse{LicenseID: licenseID})
}

func (s *Service) handleRevoke(c *gin.Context) {
	var body revokeRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	licenseID := strings.TrimSpace(body.LicenseID)
	if licenseID == "" {
		httpx.Fail(c, httpx.BadRequest("许可证号缺失。"))
		return
	}

	if err := s.Revoke(c.Request.Context(), licenseID); err != nil {
		if errors.Is(err, store.ErrNotFound) {
			httpx.Fail(c, httpx.NotFound("许可证不存在。"))
			return
		}
		httpx.Fail(c, httpx.FromStore(err))
		return
	}

	// The mock's empty object, field for field: the console reads the status
	// code and nothing else.
	httpx.OK(c, http.StatusOK, struct{}{})
}

// knownStatus reports whether a status filter names a state the product
// actually has. Rejected is a seller's no; the row stays, and the ledger
// page filters on it.
func knownStatus(s store.ActivationStatus) bool {
	switch s {
	case store.ActivationPending, store.ActivationConfirmed, store.ActivationRejected:
		return true
	}
	return false
}

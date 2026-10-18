package activation

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
// Admin bodies
//
// Same rule as the customer half: every field name is pinned with a tag,
// because the other side of this contract is a static page in another
// language (branch 2's admin-console) written against server/mock/server.js.
// ---------------------------------------------------------------------------

type adminListResponse struct {
	Items []adminItem `json:"items"`
}

type adminItem struct {
	RequestID    string `json:"requestId"`
	InstallID    string `json:"installId"`
	AnchorDigest string `json:"anchorDigest"`
	Nickname     string `json:"nickname"`
	// AvatarURL is additive to the mock's item shape (it had none): the
	// console's queue rows read better with a face, and the store's join
	// already carries it. A consumer that ignores unknown fields is
	// unaffected.
	AvatarURL string `json:"avatarUrl,omitempty"`
	// AmountFen serialises as null when the buyer never stated one — the
	// database's own shape, and the console renders a blank cell.
	AmountFen *int64 `json:"amountFen"`
	// CreatedAt is RFC 3339 in UTC with no fractional seconds — the shape
	// the mock's stamp() emits and the console's Date parser reads.
	CreatedAt string `json:"createdAt"`
	Status    string `json:"status"`
	// LicenseID lets the console offer a revoke button on a confirmed row
	// without a second fetch. Also additive to the mock's shape.
	LicenseID string `json:"licenseId,omitempty"`
}

type adminActionRequest struct {
	RequestID string `json:"requestId"`
	InstallID string `json:"installId"`
	LicenseID string `json:"licenseId"`
}

type adminLicenseResponse struct {
	LicenseID string `json:"licenseId"`
}

// validAdminStatus is the filter vocabulary. Anything else is a 400 here
// rather than an empty list there: the console's own bug should be loud.
func validAdminStatus(s store.ActivationStatus) bool {
	switch s {
	case store.ActivationPending, store.ActivationConfirmed, store.ActivationRejected:
		return true
	}
	return false
}

// stamp renders a queue timestamp the way the mock does: UTC, RFC 3339, no
// fractional seconds. A locale- or zone-dependent format here is a console
// that shows the seller a confirm time in the wrong hour.
func stamp(t time.Time) string {
	return t.UTC().Format(time.RFC3339)
}

func (s *Service) handleAdminList(c *gin.Context) {
	status := store.ActivationStatus(strings.TrimSpace(c.Query("status")))
	if status != "" && !validAdminStatus(status) {
		httpx.Fail(c, httpx.BadRequest("状态筛选值不正确。"))
		return
	}

	items, err := s.AdminList(c.Request.Context(), status)
	if err != nil {
		httpx.Fail(c, httpx.FromStore(err))
		return
	}

	out := adminListResponse{Items: make([]adminItem, 0, len(items))}
	for _, it := range items {
		out.Items = append(out.Items, adminItem{
			RequestID:    it.RequestID,
			InstallID:    it.InstallID,
			AnchorDigest: it.AnchorDigest,
			Nickname:     it.Nickname,
			AvatarURL:    it.AvatarURL,
			AmountFen:    it.AmountFen,
			CreatedAt:    stamp(it.CreatedAt),
			Status:       string(it.Status),
			LicenseID:    it.LicenseID,
		})
	}
	httpx.OK(c, http.StatusOK, out)
}

func (s *Service) handleAdminConfirm(c *gin.Context) {
	var body adminActionRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	requestID := strings.TrimSpace(body.RequestID)
	if requestID == "" {
		httpx.Fail(c, httpx.BadRequest("缺少登记单号。"))
		return
	}

	licenseID, err := s.AdminConfirm(c.Request.Context(), requestID)
	if err != nil {
		failAdminAction(c, err, "该登记已被拒绝，不能确认。", "该登记状态已变化，请刷新后重试。")
		return
	}
	httpx.OK(c, http.StatusOK, adminLicenseResponse{LicenseID: licenseID})
}

func (s *Service) handleAdminReissue(c *gin.Context) {
	var body adminActionRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	installID := strings.TrimSpace(body.InstallID)
	if installID == "" {
		httpx.Fail(c, httpx.BadRequest("缺少机器码。"))
		return
	}

	licenseID, err := s.AdminReissue(c.Request.Context(), installID)
	if err != nil {
		failAdminAction(c, err, "该账号在另一台机器上还有生效中的许可证，请先作废再重签。",
			"该机器没有可重签的许可证或登记，请刷新后重试。")
		return
	}
	httpx.OK(c, http.StatusOK, adminLicenseResponse{LicenseID: licenseID})
}

func (s *Service) handleAdminRevoke(c *gin.Context) {
	var body adminActionRequest
	if err := c.ShouldBindJSON(&body); err != nil && !errors.Is(err, io.EOF) {
		httpx.Fail(c, httpx.BadRequest("请求体格式不正确。"))
		return
	}
	licenseID := strings.TrimSpace(body.LicenseID)
	if licenseID == "" {
		httpx.Fail(c, httpx.BadRequest("缺少许可证编号。"))
		return
	}

	if err := s.AdminRevoke(c.Request.Context(), licenseID); err != nil {
		failAdminAction(c, err, "该许可证已作废。", "该许可证状态已变化，请刷新后重试。")
		return
	}
	// 200 with an empty object, matching the mock: revoke is a state flip,
	// not a resource, and there is nothing to hand back.
	httpx.OK(c, http.StatusOK, gin.H{})
}

// failAdminAction maps the admin sentinels onto replies. Rejected, taken and
// stale are all 409s but they are different facts, and the seller should not
// have to guess which one they are looking at: a rejected row survives a
// refresh, a stale screen does not, and a taken machine means look elsewhere.
func failAdminAction(c *gin.Context, err error, rejectedMsg, staleMsg string) {
	switch {
	case errors.Is(err, ErrRequestNotFound):
		httpx.Fail(c, httpx.NotFound("请求的内容不存在。"))
	case errors.Is(err, ErrRequestRejected):
		httpx.Fail(c, httpx.Conflict(rejectedMsg))
	case errors.Is(err, ErrRequestNotPending):
		httpx.Fail(c, httpx.Conflict(staleMsg))
	default:
		httpx.Fail(c, httpx.FromStore(err))
	}
}

package httpx

import (
	"bytes"
	"log/slog"
	"net/http"
	"net/http/httptest"
	"strings"
	"testing"

	"github.com/gin-gonic/gin"
)

func TestRequestLogsDoNotExposeLoginTickets(t *testing.T) {
	t.Parallel()
	for _, tc := range []struct {
		name, path string
		panic      bool
	}{
		{"poll", "/prefix/v1/auth/session/private-login-ticket", false},
		{"panic", "/prefix/v1/auth/session/private-login-ticket", true},
		{"unmatched", "/prefix/v1/auth/session/private-login-ticket/unknown", false},
	} {
		t.Run(tc.name, func(t *testing.T) {
			var output bytes.Buffer
			logger := slog.New(slog.NewTextHandler(&output, nil))
			engine := gin.New()
			engine.Use(Logger(logger), RecoveryMiddleware(logger))
			engine.GET("/prefix/v1/auth/session/:ticket", func(c *gin.Context) {
				if tc.panic {
					panic("simulated failure")
				}
				c.Status(http.StatusOK)
			})
			engine.ServeHTTP(httptest.NewRecorder(), httptest.NewRequest(http.MethodGet, tc.path, nil))
			if strings.Contains(output.String(), "private-login-ticket") {
				t.Fatal("request log exposes a login ticket")
			}
			if tc.name != "unmatched" && !strings.Contains(output.String(), "/prefix/v1/auth/session/:ticket") {
				t.Fatal("request log lost the route template needed for diagnosis")
			}
		})
	}
}

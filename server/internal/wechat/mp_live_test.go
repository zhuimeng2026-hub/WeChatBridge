package wechat

import (
	"context"
	"errors"
	"os"
	"testing"
	"time"
)

// The live test exists because the stub server cannot answer the question
// that matters most about this client: whether the real WeChat API still
// speaks the shape it is written against. Every other test here proves the
// client's own logic — the caching, the single flight, the budget, the
// redaction — against a server this repository controls, and a stub cannot
// catch an endpoint that has been renamed.
//
// It is skipped unless the official account's credentials are in the
// environment, so CI — which has no WeChat account and must not spend a real
// account's daily budget — never runs it.
//
// The call it makes is one that is expected to fail: an OpenID that does not
// exist asks WeChat to validate an OpenID, and the answer to that question
// (40003 invalid openid) is the proof that the credential, the token and the
// endpoint all work. A successful user-info lookup would need a real
// follower, and the point of the test is the plumbing, not the person.
const (
	liveMPAppIDEnv     = "WCB_MP_APP_ID"
	liveMPAppSecretEnv = "WCB_MP_APP_SECRET"
)

func TestMPClientAgainstTheRealWechat(t *testing.T) {
	appID := os.Getenv(liveMPAppIDEnv)
	secret := os.Getenv(liveMPAppSecretEnv)
	if appID == "" || secret == "" {
		t.Skipf("%s and %s are not set; skipping the live WeChat test", liveMPAppIDEnv, liveMPAppSecretEnv)
	}

	c := NewMPClient(MPConfig{
		AppID:     appID,
		AppSecret: secret,
		APIBase:   "https://api.weixin.qq.com",
		Timeout:   10 * time.Second,
	})

	ctx, cancel := context.WithTimeout(context.Background(), 30*time.Second)
	defer cancel()

	// The token is the part with the interesting behaviour, so it is fetched
	// on its own first: a failure here is a credential problem and the rest
	// of the test would only report it a step further away.
	token, err := c.accessToken(ctx)
	if err != nil {
		t.Fatalf("acquire the official account's access token: %v", err)
	}
	if token == "" {
		t.Fatal("the access token is empty")
	}

	// A second call must reuse it rather than mint another one: an
	// application holds one live token, and a second acquisition retires the
	// first.
	again, err := c.accessToken(ctx)
	if err != nil {
		t.Fatalf("re-read the access token: %v", err)
	}
	if again != token {
		t.Fatal("the access token was re-acquired instead of reused")
	}

	// Now the endpoint. A non-existent OpenID must be refused by WeChat with
	// 40003, which means the request reached the interface and the interface
	// checked the OpenID — as opposed to 40066, which means the interface was
	// never dispatched, or 40001, which means the token was not accepted.
	_, err = c.GetUserInfo(ctx, "o_this_openid_does_not_exist")
	if err == nil {
		t.Fatal("expected WeChat to refuse a non-existent openid")
	}
	var apiErr *APIError
	if !errors.As(err, &apiErr) {
		t.Fatalf("error = %v, want an APIError", err)
	}
	if apiErr.ErrCode != 40003 {
		t.Fatalf("errcode = %d (%s), want 40003: 40066 means the interface is not "+
			"dispatched for this account, 40001 means the token was rejected",
			apiErr.ErrCode, apiErr.ErrMsg)
	}
}

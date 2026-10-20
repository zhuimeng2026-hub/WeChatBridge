package httpx

import (
	"errors"
	"net/http"
	"testing"

	"github.com/freestylefly/WeChatBridge/server/internal/store"
)

// TestFromStoreMapping pins the store-to-wire translation for every sentinel
// the mapping knows about.
//
// The one that matters most is ErrLiveBindingExists. The store separated it
// from ErrConflict so that "this machine already holds a licence" stops being
// reported as "somebody else got there first" — but that separation is only
// worth anything if the translation carries it through. A sentinel that falls
// through to the default arm becomes a 500, and a 500 tells the operator their
// server is broken when the seller merely pressed the wrong button.
func TestFromStoreMapping(t *testing.T) {
	cases := []struct {
		name   string
		err    error
		status int
		code   string
	}{
		{"no error at all", nil, http.StatusOK, ""},
		{"nothing is there", store.ErrNotFound, http.StatusNotFound, CodeNotFound},
		{"a spent credential presented twice", store.ErrReuseDetected, http.StatusUnauthorized, CodeUnauthorized},
		{"somebody else won the race", store.ErrConflict, http.StatusConflict, CodeConflict},
		{
			"the machine already holds a live licence",
			store.ErrLiveBindingExists,
			http.StatusConflict,
			CodeConflict,
		},
	}
	for _, tc := range cases {
		t.Run(tc.name, func(t *testing.T) {
			got := FromStore(tc.err)
			if tc.err == nil {
				if got != nil {
					t.Fatalf("want nil, got %+v", got)
				}
				return
			}
			if got.Status != tc.status {
				t.Fatalf("status %d, want %d", got.Status, tc.status)
			}
			if got.Code != tc.code {
				t.Errorf("code %q, want %q", got.Code, tc.code)
			}
		})
	}

	t.Run("the live binding case is never a 500", func(t *testing.T) {
		// Stated separately from the table above because it is the assertion
		// that would fail first if someone re-merged the two sentinels.
		if got := FromStore(store.ErrLiveBindingExists); got.Status >= 500 {
			t.Fatalf("a seller-actionable state answered %d — it reports a server fault that did not happen", got.Status)
		}
	})

	t.Run("wrapped sentinels still translate", func(t *testing.T) {
		// Store methods add their operation's identity before returning, so the
		// mapping is reached through errors.Is rather than == in practice.
		wrapped := errors.Join(errors.New("confirm activation request: bind"), store.ErrLiveBindingExists)
		if got := FromStore(wrapped); got.Status != http.StatusConflict {
			t.Fatalf("wrapped sentinel gave %d, want 409", got.Status)
		}
	})

	t.Run("an unknown error stays a 500", func(t *testing.T) {
		// The other half of the contract: the new case must not have been
		// added by widening the default arm.
		if got := FromStore(errors.New("connection refused")); got.Status != http.StatusInternalServerError {
			t.Fatalf("unknown error gave %d, want 500", got.Status)
		}
	})
}

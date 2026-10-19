package auth

import (
	"os"
	"path/filepath"
	"strings"
	"testing"
)

// writePNG drops a valid image into dir. black is the side of the square drawn
// on it, so two calls differ in what the image *shows*, not merely in how much
// whitespace surrounds it — the loader trims margins away, so a fixture that
// only differs in padding renders identically and proves nothing.
func writePNG(t *testing.T, dir, name string, black int) string {
	t.Helper()
	const frame = 200
	path := filepath.Join(dir, name)
	if err := os.WriteFile(path, card(t, frame, (frame-black)/2), 0o600); err != nil {
		t.Fatal(err)
	}
	return path
}

// The whole point: replacing the file changes the page, with no restart.
func TestReplacingTheFileChangesThePage(t *testing.T) {
	t.Parallel()

	dir := t.TempDir()
	path := writePNG(t, dir, "pay.png", 0)
	code := LoadPaymentCode(path, "aiwomeishi", "")

	before, err := code.Render(pageAuthorized, "install-1")
	if err != nil {
		t.Fatal(err)
	}
	first := extractImage(before)

	writePNG(t, dir, "pay.png", 80) // visibly different content
	after, err := code.Render(pageAuthorized, "install-1")
	if err != nil {
		t.Fatal(err)
	}
	second := extractImage(after)

	if first == second {
		t.Fatal("replacing the file did not change the page; the image is still being read once at startup")
	}
	if second == "" {
		t.Fatal("the page has no image after the replacement")
	}
}

// An editor truncates before it writes. A buyer must not see a blank frame in
// the middle of that window, and the operator must not have to restart.
func TestAFailedReloadKeepsTheLastGoodImage(t *testing.T) {
	t.Parallel()

	dir := t.TempDir()
	path := writePNG(t, dir, "pay.png", 0)
	code := LoadPaymentCode(path, "aiwomeishi", "")

	good, err := code.Render(pageAuthorized, "install-1")
	if err != nil {
		t.Fatal(err)
	}
	want := extractImage(good)

	// The window an editor opens on a file.
	if err := os.WriteFile(path, []byte("<html>half-written"), 0o600); err != nil {
		t.Fatal(err)
	}

	during, err := code.Render(pageAuthorized, "install-1")
	if err != nil {
		t.Fatal(err)
	}
	if got := extractImage(during); got != want {
		t.Errorf("a broken file replaced the working image with %q; a buyer mid-payment must keep seeing the code", got)
	}
	if p := code.Problems(); len(p) == 0 {
		t.Error("the failure was not reported at all; a silent fallback to a stale image is worse than a loud one")
	}

	// And it heals on its own once the file settles.
	writePNG(t, dir, "pay.png", 60)
	after, err := code.Render(pageAuthorized, "install-1")
	if err != nil {
		t.Fatal(err)
	}
	if extractImage(after) == want {
		t.Error("the reload never recovered after the file became valid again")
	}
}

// A missing file must not be fatal either, and must not blank the page.
func TestADeletedFileKeepsServingTheLastGoodImage(t *testing.T) {
	t.Parallel()

	dir := t.TempDir()
	path := writePNG(t, dir, "pay.png", 0)
	code := LoadPaymentCode(path, "aiwomeishi", "")

	good, _ := code.Render(pageAuthorized, "install-1")
	want := extractImage(good)

	if err := os.Remove(path); err != nil {
		t.Fatal(err)
	}
	page, err := code.Render(pageAuthorized, "install-1")
	if err != nil {
		t.Fatal(err)
	}
	if got := extractImage(page); got != want {
		t.Errorf("deleting the file emptied the page (%q)", got)
	}
}

// Render is called from concurrent requests and refresh writes both slots.
func TestConcurrentRenderAndRefresh(t *testing.T) {
	t.Parallel()

	dir := t.TempDir()
	path := writePNG(t, dir, "pay.png", 0)
	code := LoadPaymentCode(path, "aiwomeishi", "")

	done := make(chan struct{})
	for i := 0; i < 8; i++ {
		go func() {
			defer func() { done <- struct{}{} }()
			for j := 0; j < 50; j++ {
				page, err := code.Render(pageAuthorized, "install-1")
				if err != nil {
					t.Errorf("Render: %v", err)
					return
				}
				if extractImage(page) == "" {
					t.Error("a concurrent render produced a page with no image")
					return
				}
			}
		}()
	}
	for i := 0; i < 8; i++ {
		<-done
	}
}

// extractImage pulls the inlined image out of a rendered page, so the tests
// compare what the buyer would actually see rather than internal state.
func extractImage(page []byte) string {
	body := string(page)
	const open = `src="data:image/`
	i := strings.Index(body, open)
	if i < 0 {
		return ""
	}
	rest := body[i+len(open):]
	j := strings.IndexByte(rest, '"')
	if j < 0 {
		return ""
	}
	return rest[:j]
}

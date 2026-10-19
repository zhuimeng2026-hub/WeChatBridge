package auth

import (
	"bytes"
	"image"
	"image/color"
	"image/jpeg"
	"image/png"
	"testing"
)

// card builds a white frame with a solid square of "content" inside it, which
// is the shape every image the seller exports has: a card on a screenshot.
func card(t *testing.T, size, inset int) []byte {
	t.Helper()
	img := image.NewRGBA(image.Rect(0, 0, size, size))
	for i := range img.Pix {
		img.Pix[i] = 0xff
	}
	black := color.NRGBA{0, 0, 0, 0xff}
	for y := inset; y < size-inset; y++ {
		for x := inset; x < size-inset; x++ {
			img.Set(x, y, black)
		}
	}
	var buf bytes.Buffer
	if err := png.Encode(&buf, img); err != nil {
		t.Fatal(err)
	}
	return buf.Bytes()
}

func dimensions(t *testing.T, raw []byte) (int, int) {
	t.Helper()
	cfg, _, err := image.DecodeConfig(bytes.NewReader(raw))
	if err != nil {
		t.Fatalf("decode: %v", err)
	}
	return cfg.Width, cfg.Height
}

// The point of the whole file. What the seller exports is a screenshot of a
// card; what the page needs is the card.
func TestTrimMarginsCropsTheScreenshotToTheCard(t *testing.T) {
	t.Parallel()

	out, err := trimMargins(card(t, 400, 100))
	if err != nil {
		t.Fatalf("trimMargins: %v", err)
	}
	w, h := dimensions(t, out)
	if w >= 400 || h >= 400 {
		t.Errorf("trimmed to %dx%d, want smaller than the 400x400 it was given", w, h)
	}
	if want := 200 + 2*marginPad; w != want {
		t.Errorf("width = %d, want %d (the 200px content plus two pads)", w, want)
	}
	if w != h {
		t.Errorf("a square card should stay square: %dx%d", w, h)
	}
}

// A tight image must come back byte-identical rather than re-encoded: there is
// nothing to gain, and re-encoding a payment QR that does not need it is one
// more chance to lose quality.
func TestTrimMarginsLeavesATightImageUntouched(t *testing.T) {
	t.Parallel()

	in := card(t, 200, marginPad)
	out, err := trimMargins(in)
	if err != nil {
		t.Fatalf("trimMargins: %v", err)
	}
	if !bytes.Equal(in, out) {
		t.Error("an already-tight image was re-encoded instead of passed through")
	}
}

// A blank image is not a card; cropping it to nothing would show the buyer a
// picture of no QR code, which is worse than showing the one they already have.
func TestTrimMarginsRefusesToCropAnImageAway(t *testing.T) {
	t.Parallel()

	in := card(t, 300, 150) // inset 150 on a 300 frame leaves nothing
	if len(in) == 0 {
		t.Fatal("test fixture is empty")
	}
	blank := image.NewRGBA(image.Rect(0, 0, 300, 300))
	for i := range blank.Pix {
		blank.Pix[i] = 0xff
	}
	var buf bytes.Buffer
	if err := png.Encode(&buf, blank); err != nil {
		t.Fatal(err)
	}

	out, err := trimMargins(buf.Bytes())
	if err != nil {
		t.Fatalf("trimMargins: %v", err)
	}
	if w, h := dimensions(t, out); w != 300 || h != 300 {
		t.Errorf("a blank image was cropped to %dx%d, want it left at 300x300", w, h)
	}
}

// Anything that is not decodable is handed back untouched rather than dropped:
// the page showing a padded card beats the page showing nothing.
func TestTrimMarginsPassesThroughWhatItCannotDecode(t *testing.T) {
	t.Parallel()

	if _, err := trimMargins([]byte("not an image at all")); err == nil {
		t.Error("trimMargins claimed success on bytes that are not an image")
	}
}

// A JPEG source stays a JPEG. Re-encoding it as PNG would grow every page the
// seller serves for no benefit.
func TestTrimMarginsKeepsTheSourceFormat(t *testing.T) {
	t.Parallel()

	var buf bytes.Buffer
	img := image.NewRGBA(image.Rect(0, 0, 300, 300))
	for i := range img.Pix {
		img.Pix[i] = 0xff
	}
	for y := 80; y < 220; y++ {
		for x := 80; x < 220; x++ {
			img.Set(x, y, color.NRGBA{0, 0, 0, 0xff})
		}
	}
	if err := jpeg.Encode(&buf, img, nil); err != nil {
		t.Fatal(err)
	}

	out, err := trimMargins(buf.Bytes())
	if err != nil {
		t.Fatalf("trimMargins: %v", err)
	}
	if _, format, err := image.DecodeConfig(bytes.NewReader(out)); err != nil {
		t.Fatalf("decode: %v", err)
	} else if format != "jpeg" {
		t.Errorf("format = %q, want jpeg", format)
	}
}

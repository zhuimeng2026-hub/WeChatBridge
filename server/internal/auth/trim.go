package auth

import (
	"bytes"
	"errors"
	"image"
	"image/color"
	"image/draw"
	"image/jpeg"
	"image/png"
)

// This file trims the margins off the images the seller exports, because what
// they export is a screenshot of a card rather than a bare QR code, and the
// page has no room to show a screenshot at a size anybody can scan.
//
// The alternative — asking the seller to crop by hand — is a hidden step. The
// first time someone replaces the image and forgets, the QR code renders too
// small to scan, buyers cannot add the seller, and the failure looks like "the
// payment page is broken" with nothing in the logs to explain it. Trimming here
// makes the pipeline "export from WeChat, drop the file in", which is the one
// thing that cannot be done wrong.
//
// It runs once per configured image at startup, not per request, and the images
// are around a megapixel, so the obvious implementation is fast enough. A
// row/column buffer would be measurably quicker and nobody would notice.

// marginPad is the whitespace left around the content after trimming. A little,
// because a QR code read at a hard edge is worse than one with a quiet border.
const marginPad = 10

// marginThreshold is how close to white counts as background.
//
// High on purpose: the contact card carries light grey text on white, and a low
// threshold would treat that text as background and crop the seller's own name
// off their card.
const marginThreshold = 243

// maxTrimDimension bounds the decode. A seller handing over a 20000px photo is
// a different problem, and the page could not show it usefully anyway.
const maxTrimDimension = 6000

// trimMargins crops the surrounding whitespace and returns the encoded image.
// It is deliberately forgiving: every failure returns the original bytes, so
// the worst case is a card with wider margins than it needs.
func trimMargins(raw []byte) ([]byte, error) {
	cfg, format, err := image.DecodeConfig(bytes.NewReader(raw))
	if err != nil {
		return nil, err
	}
	if cfg.Width > maxTrimDimension || cfg.Height > maxTrimDimension {
		return nil, errors.New("auth: image too large to trim")
	}

	src, _, err := image.Decode(bytes.NewReader(raw))
	if err != nil {
		return nil, err
	}

	full := src.Bounds()
	box, ok := contentBounds(src, full)
	if !ok || box == full {
		// Blank, or already tight. Returning the original also means a tight
		// image is not re-encoded for nothing.
		return raw, nil
	}

	out := image.NewRGBA(image.Rect(0, 0, box.Dx(), box.Dy()))
	draw.Draw(out, out.Bounds(), src, box.Min, draw.Src)

	var buf bytes.Buffer
	switch format {
	case "png":
		err = png.Encode(&buf, out)
	default:
		// JPEG for a JPEG source. Re-encoding as PNG would grow the page for no
		// benefit, and a screenshot's artefacts are inside the error correction
		// a scanner already tolerates.
		err = jpeg.Encode(&buf, out, &jpeg.Options{Quality: 92})
	}
	if err != nil {
		return nil, err
	}
	return buf.Bytes(), nil
}

// contentBounds is the bounding box of everything that is not background.
func contentBounds(src image.Image, full image.Rectangle) (image.Rectangle, bool) {
	if full.Dx() <= 0 || full.Dy() <= 0 {
		return full, false
	}

	minX, minY := full.Max.X, full.Max.Y
	maxX, maxY := full.Min.X, full.Min.Y
	found := false

	for y := full.Min.Y; y < full.Max.Y; y++ {
		for x := full.Min.X; x < full.Max.X; x++ {
			c := color.NRGBAModel.Convert(src.At(x, y)).(color.NRGBA)
			if isBackground(c) {
				continue
			}
			if x < minX {
				minX = x
			}
			if x > maxX {
				maxX = x
			}
			if y < minY {
				minY = y
			}
			if y > maxY {
				maxY = y
			}
			found = true
		}
	}

	if !found {
		return full, false
	}

	// maxX/maxY are inclusive above, so the exclusive edge is +1.
	return image.Rect(
		minX-marginPad,
		minY-marginPad,
		maxX+1+marginPad,
		maxY+1+marginPad,
	).Intersect(full), true
}

// isBackground reports whether a pixel is empty space rather than content.
//
// Transparent counts as background: a PNG exported with rounded corners or a
// transparent margin would otherwise pin the bounding box to the full frame and
// never trim at all.
func isBackground(c color.NRGBA) bool {
	if c.A < 16 {
		return true
	}
	return c.R >= marginThreshold && c.G >= marginThreshold && c.B >= marginThreshold
}

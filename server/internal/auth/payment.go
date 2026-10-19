package auth

import (
	"bytes"
	"encoding/base64"
	"fmt"
	"html/template"
	"net/http"
	"os"
	"strings"
	"sync"
	"time"
)

// PaymentCode is the seller's payment QR as the callback page shows it.
//
// It is read once at startup rather than per request: the file is static, and
// a page that renders differently on two consecutive loads is a page nobody can
// support. A deployment that configures none still gets a page — one that says
// so — because the alternative is the empty white frame this whole file exists
// to replace.
type PaymentCode struct {
	// pay is the payment image inlined as a data: URL. See renderPage for why it
	// is not an <img src> pointing at a route.
	pay picture
	// contact is the seller's own WeChat QR, same inlining, same reasoning.
	//
	// A separate file from the payment code because it is a different artefact
	// with a different owner and a different lifecycle: WeChat issues the
	// payment code and the seller replaces it when it expires, while this one
	// the seller exports from their own profile and keeps until they change
	// accounts. Bundling them would mean replacing the payment code to rotate a
	// contact detail.
	//
	// It cannot be generated from the WeChat id. A personal WeChat QR encodes
	// wxp://dl/business/?t=<token>, and that token is signed by WeChat — there
	// is no way to mint one here, so this is a file the seller exports.
	contact picture
	weChat  string

	// The paths are kept so a changed file is picked up without a restart. See
	// refresh.
	payPath     string
	contactPath string

	// mu guards the cached pictures. The callback page is rendered from
	// concurrent requests, and refresh writes to both slots.
	mu sync.Mutex
}

// LoadPaymentCode reads the images and pairs them with the seller's WeChat id.
//
// Neither path is required, and **an image problem is never a returned error**.
// That is the whole design rule, and it is the opposite of the instinct that
// makes a missing file fatal:
//
//   - A page that cannot show the payment code still has to render, because the
//     one thing it must always be able to do is explain itself. An operator
//     whose receipt image has not been exported yet must still be able to log
//     people in — otherwise the failure takes down the very flow the failure
//     notice belongs to.
//   - Killing auth because a .png is absent converts "this deployment cannot
//     take money through one channel" into "nobody can log in at all". The
//     second is much worse and much harder to diagnose from a phone.
//
// So an unusable image degrades to the "not configured" state on the page, and
// reports a Problem the caller logs at WARN. A typo in the path therefore still
// gets noticed — at startup, in the log, on the page — without taking the
// service down.
//
// There is deliberately no price parameter. The payment code is a WeChat
// fixed-amount code: the amount lives inside the image, and a second copy of
// it as page text would be a second source of truth that drifts the first time
// the seller changes price and forgets this file. The page points at the image
// instead of restating it.
func LoadPaymentCode(path, weChat, contactPath string) *PaymentCode {
	p := &PaymentCode{
		weChat:      strings.TrimSpace(weChat),
		payPath:     strings.TrimSpace(path),
		contactPath: strings.TrimSpace(contactPath),
	}
	p.refreshLocked()
	return p
}

// refresh re-reads the configured images if they changed on disk.
//
// It runs per request, which is what lets an operator replace the payment code
// by dropping a file in and nothing else — no restart, and therefore no window
// where the service is down to publish a new price.
//
// Three things keep that cheap and safe:
//
//   - It stats first and skips the read when size and mtime are unchanged. The
//     expensive part is the trim, which walks every pixel, so doing that per
//     request would be waste for a page that is served a handful of times a day.
//
//   - **A failed reload keeps the last good image.** An editor that truncates
//     before writing, an upload caught mid-flight, a permissions slip: any of
//     these must not blank the page a buyer is looking at. The failure is
//     recorded and reported, but the bytes that worked are still served, and
//     the next request tries again — so it heals by itself once the file
//     settles, without anyone restarting anything.
//
//   - A file that stays broken is reported once, not once per request. Otherwise
//     a misconfigured path becomes a log flood that trains people to ignore it.
func (p *PaymentCode) refresh() {
	if p == nil {
		return
	}
	p.mu.Lock()
	defer p.mu.Unlock()
	p.refreshLocked()
}

// refreshLocked does the work of refresh; the caller holds p.mu.
func (p *PaymentCode) refreshLocked() {
	p.pay = loadPicture(p.payPath, "payment code", p.pay)
	p.contact = loadPicture(p.contactPath, "contact code", p.contact)
}

// fileVersion identifies a file version cheaply. Size plus mtime is enough: a real
// edit to an image changes both, and a same-size write within the same
// filesystem timestamp granularity is not a thing an operator does by hand.
type fileVersion struct {
	size    int64
	modTime time.Time
	set     bool
}

// picture is one configured image and whatever went wrong reading it.
//
// Exactly one of URI and Problem is ever set — except after a failed reload,
// where URI deliberately keeps its previous value. See loadPicture.
type picture struct {
	URI     string
	Problem string
	version fileVersion
}

func (p picture) usable() bool { return p.URI != "" }

// loadPicture returns the current image, re-reading only if the file changed,
// and never returning worse than what already worked.
func loadPicture(path, what string, prev picture) picture {
	path = strings.TrimSpace(path)
	if path == "" {
		return picture{}
	}

	// Keep the last good bytes across a failed load. This is the whole reason
	// prev is carried in.
	keep := func(problem string) picture {
		return picture{URI: prev.URI, Problem: problem, version: prev.version}
	}

	info, err := os.Stat(path)
	if err != nil {
		return keep(fmt.Sprintf("%s: %v", path, err))
	}
	now := fileVersion{size: info.Size(), modTime: info.ModTime(), set: true}
	if prev.version == now && (prev.URI != "" || prev.Problem != "") {
		return prev
	}

	raw, err := os.ReadFile(path)
	if err != nil {
		return keep(fmt.Sprintf("%s: %v", path, err))
	}
	if len(raw) == 0 {
		return keep(fmt.Sprintf("%s: empty file", path))
	}

	// Sniffed rather than taken from the extension. The file the seller drops
	// in is often a .jpg of a screenshot on one day and a .png export the next,
	// and a wrong Content-Type here would make the browser refuse a perfectly
	// good image — which reads as a broken page, not a wrong file.
	kind := http.DetectContentType(raw)
	if !strings.HasPrefix(kind, "image/") {
		return keep(fmt.Sprintf("%s: %s, not an image", path, kind))
	}

	trimmed, terr := trimMargins(raw)
	if terr == nil {
		raw = trimmed
	}
	// A trim failure is not worth reporting: the untrimmed image still renders,
	// just with more whitespace around it than it needs.

	return picture{
		URI:     "data:" + kind + ";base64," + base64.StdEncoding.EncodeToString(raw),
		version: now,
	}
}

// Problems lists why a configured image could not be loaded, for the startup
// log. Empty when both are fine or unset.
func (p *PaymentCode) Problems() []string {
	if p == nil {
		return nil
	}
	var out []string
	for _, i := range []picture{p.pay, p.contact} {
		if i.Problem != "" {
			out = append(out, i.Problem)
		}
	}
	return out
}

// Configured reports whether the payment image is usable.
func (p *PaymentCode) Configured() bool { return p != nil && p.pay.usable() }

// maxInstallIDShown bounds the machine code on the page.
//
// install_id is whatever the client put in the request body — it is not
// authenticated at the moment it is stored. Without a bound, a caller could
// hand over a megabyte of it and every callback would render a megabyte of
// page. Truncating is safe here in a way it would not be on an API response:
// the code is read off the desktop window beside it, not retyped from this
// page.
const maxInstallIDShown = 128

// pageKind is which of the callback's outcomes a page is for.
//
// They are separate kinds rather than a message string because the three mean
// different things to the person holding the phone, and saying the wrong one
// costs them something: an expired ticket and an unavailable upstream both
// look like a failure from out here, but only the first is fixed by re-scanning.
type pageKind int

const (
	pageAuthorized pageKind = iota
	pageInvalidState
	pageInvalidCode
	pageUpstreamDown
	pageFailed
)

var pageCopy = map[pageKind]struct{ title, detail, hint string }{
	pageAuthorized: {
		title:  "授权成功",
		detail: "微信流买断版 · 一次买断，永久离线可用",
		hint:   "付款完成后回到电脑，在桌面应用里点「我已付款」。",
	},
	pageInvalidState: {
		title:  "登录已失效",
		detail: "这个二维码已经被用过，或者已经超时。",
		hint:   "请回到电脑，刷新二维码后重新扫码。",
	},
	pageInvalidCode: {
		title:  "授权码无效",
		detail: "微信没有接受这次授权。",
		hint:   "请回到电脑，重新扫码。",
	},
	pageUpstreamDown: {
		title:  "微信服务暂时不可用",
		detail: "这次授权没能完成，是微信那边的问题，不是你的操作。",
		// The one page that must not say "scan again": the ticket is untouched
		// and the code the user is holding is still good, so telling them to
		// start over is telling them to do work that changes nothing.
		hint: "请稍后在电脑端重试。",
	},
	pageFailed: {
		title:  "授权没有完成",
		detail: "服务器这边出了点问题。",
		hint:   "请回到电脑重试；若反复出现，请联系开发者。",
	},
}

// pageModel is what the template sees.
type pageModel struct {
	Kind      pageKind
	Title     string
	Detail    string
	Hint      string
	InstallID string
	// Image is a template.URL, not a string, and that type is load-bearing.
	//
	// html/template filters <img src> through a URL-scheme allowlist that does
	// not include data:, and silently rewrites a rejected value to "#ZgotmplZ".
	// The result compiles, passes every test that only checks for an <img tag,
	// and ships a broken image to the one page whose entire job is to display a
	// QR code. template.URL opts this one value out of the filter.
	//
	// Safe to opt out because the value is not attacker-controlled: it is
	// base64.StdEncoding over a file the operator configured, plus a media type
	// from http.DetectContentType's fixed set. Both alphabets are closed — no
	// quote, no angle bracket, no scheme that can carry markup. Every other
	// field stays a string and stays escaped.
	Image template.URL
	// Contact is template.URL for the same reason Image is: a data: URI that
	// html/template would otherwise rewrite to "#ZgotmplZ".
	Contact template.URL
	WeChat  string
}

// Render builds the page for one outcome.
//
// installID is the machine code the licence will be bound to; it is echoed so
// the buyer can put it in the transfer note, which is the only link between a
// WeChat payment and a licence the seller can match by hand. It comes from the
// ticket, never from the query string: a payment page that takes its machine
// code from a URL is a page where anyone can ask for someone else's licence to
// be issued.
//
// Every field goes through html/template, which escapes it. install_id is
// attacker-chosen at ticket creation, so this is a real injection surface and
// not a theoretical one — an attacker who gets a victim to scan their ticket
// would otherwise be running script on this origin, which is the same origin
// the buyer types credentials into.
func (p *PaymentCode) Render(kind pageKind, installID string) ([]byte, error) {
	// Re-read before rendering: this is the point of the whole mechanism, and it
	// has to happen before the fields below are copied out.
	p.refresh()

	c := pageCopy[kind]

	m := pageModel{
		Kind:   kind,
		Title:  c.title,
		Detail: c.detail,
		Hint:   c.hint,
	}
	if p != nil {
		m.Image = template.URL(p.pay.URI)
		m.Contact = template.URL(p.contact.URI)
		m.WeChat = p.weChat
	}
	if kind == pageAuthorized {
		m.InstallID = installID
		if len(m.InstallID) > maxInstallIDShown {
			m.InstallID = m.InstallID[:maxInstallIDShown] + "…"
		}
	}

	var buf bytes.Buffer
	if err := pageTemplate.Execute(&buf, m); err != nil {
		return nil, fmt.Errorf("auth: render callback page: %w", err)
	}
	return buf.Bytes(), nil
}

// pageTemplate is deliberately self-contained and deliberately scriptless: no
// stylesheet link, no font, no script of any kind.
//
// External resources are out for the same reason as everywhere else on a
// browser-facing page — a payment page that renders half-styled is a page whose
// QR code may be the part that did not load.
//
// **There is no auto-refresh, and there must never be one.** This URL is
// one-shot: the redirect that lands here spends the ticket, so a second request
// for it is answered 「登录已失效」. A `location.reload()` therefore does not
// refresh anything — after five seconds it replaces the payment code with an
// error page, and the buyer's only copy of the machine code with it. It looked
// harmless in development because nothing had actually scanned anything; it
// only shows up against a real WeChat code, which is the same lesson the login
// flow keeps teaching.
//
// The page is static, and that is the point: its content does not depend on
// anything the desktop app is doing. The buyer's next step is in the other
// window, and telling them so is all this page has to do.
var pageTemplate = template.Must(template.New("callback").Parse(`<!DOCTYPE html>
<html lang="zh-CN">
<head>
<meta charset="utf-8">
<meta name="viewport" content="width=device-width, initial-scale=1">
<title>{{.Title}} · 微信流</title>
<style>
  :root { color-scheme: light; }
  * { box-sizing: border-box; }
  body {
    margin: 0; padding: 32px 16px 56px;
    font: 15px/1.6 -apple-system, "PingFang SC", "Microsoft YaHei", "Segoe UI", sans-serif;
    color: #1c1c1e; background: #f2f2f7;
    display: flex; flex-direction: column; align-items: center;
  }
  .card {
    width: 100%; max-width: 460px; background: #fff; border-radius: 14px;
    padding: 28px 24px; box-shadow: 0 1px 3px rgba(0,0,0,.08);
  }
  .ok { font-size: 22px; font-weight: 600; margin: 0 0 6px; color: #1a7f37; }
  .bad { font-size: 22px; font-weight: 600; margin: 0 0 6px; color: #b3261e; }
  .detail { color: #6b6b70; margin: 0 0 22px; }
  figure { margin: 0 0 20px; text-align: center; }
  figure img {
    max-width: 100%; max-height: 420px; width: auto; height: auto;
    border-radius: 8px; display: block; margin: 0 auto;
  }
  .note { font-size: 12px; color: #8e8e93; text-align: center; margin: 0 0 20px; }
  .row { border-top: 1px solid #ececef; padding: 14px 0 0; margin-top: 4px; }
  .label { font-size: 12px; color: #8e8e93; margin: 0 0 6px; }
  .code {
    font: 13px/1.5 ui-monospace, SFMono-Regular, Consolas, monospace;
    word-break: break-all; background: #f7f7f9; border-radius: 6px;
    padding: 10px 12px; user-select: all;
  }
  .contact { font-size: 16px; font-weight: 600; }
  .contact-figure { margin: 10px 0 0; text-align: center; }
  /* Sized by height, never forced to a square. What the seller exports is a
     portrait card, and width+height together squash a tall image into an
     unreadable QR code. */
  .contact-figure img { max-height: 300px; width: auto; border-radius: 6px; }
  .hint {
    margin: 20px 0 0; padding: 12px 14px; background: #eef4ff;
    border-radius: 8px; color: #1a3a6b; font-size: 14px;
  }
  .noconfig {
    margin: 0; padding: 16px; background: #fff7e6; border: 1px solid #ffe0a3;
    border-radius: 8px; color: #8a5a00; font-size: 14px;
  }
  .foot { margin: 22px 0 0; font-size: 12px; color: #a0a0a6; text-align: center; }
</style>
</head>
<body>
  <div class="card">
    {{if eq .Kind 0}}<h1 class="ok">{{.Title}}</h1>{{else}}<h1 class="bad">{{.Title}}</h1>{{end}}
    <p class="detail">{{.Detail}}</p>

    {{if eq .Kind 0}}
      {{if .Image}}
        <figure><img src="{{.Image}}" alt="微信收款码"></figure>
        <p class="note">请使用微信扫描上方二维码支付。金额以收款码内显示为准。</p>
      {{else}}
        <p class="noconfig">授权已经成功，但收款码尚未配置，暂时无法付款。<br>
        请回到电脑端联系开发者。</p>
      {{end}}

      {{if .InstallID}}
      <div class="row">
        <p class="label">你的机器码 —— 付款时请在转账备注里填上它</p>
        <div class="code">{{.InstallID}}</div>
      </div>
      {{end}}

      {{if or .WeChat .Contact}}
      <div class="row">
        <p class="label">开发者微信 —— 扫码添加，付款后有问题直接找他</p>
        {{if .Contact}}
        <div class="contact-figure"><img src="{{.Contact}}" alt="开发者微信二维码"></div>
        {{end}}
        {{if .WeChat}}
        <p class="contact" style="text-align:center; margin-top:8px">{{.WeChat}}</p>
        {{end}}
      </div>
      {{end}}

      <p class="hint">{{.Hint}}</p>
    {{else}}
      <p class="hint">{{.Hint}}</p>
    {{end}}

    <p class="foot">本页面由微信流授权服务生成 · 可以安全地关闭</p>
  </div>
</body>
</html>
`))

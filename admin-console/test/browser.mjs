// 浏览器级全流程验证（可选工具，console 本身零依赖）。
//
// 依赖：playwright-core（已装于本机 node workspace）+ 系统 Chrome。
// 运行：先起契约 mock（node server/mock/server.js --port 8901），再：
//   NODE_PATH=C:/Users/Admin/.workbuddy/binaries/node/workspace/node_modules \
//   node test/browser.mjs
//
// 验证的是 smoke.mjs 覆盖不到的另一半：DOM 接线、渲染、弹窗、三态 toast。
import { readFileSync, mkdirSync } from "node:fs";
import { createServer } from "node:http";
import { fileURLToPath } from "node:url";
import path from "node:path";
import { createRequire } from "node:module";

// ESM import 不认 NODE_PATH，用 createRequire 走 CJS 解析找到 playwright-core。
const require = createRequire(import.meta.url);
const { chromium } = require("playwright-core");

const here = path.dirname(fileURLToPath(import.meta.url));
const consoleDir = path.resolve(here, "..");
const MOCK = "http://127.0.0.1:8901";
const shots = path.resolve(consoleDir, "../output/playwright/admin-console");
mkdirSync(shots, { recursive: true });

// 模拟生产同源反向代理，既验证 /admin/，也验证 /wechat_auth/admin/。
async function startConsoleServer() {
  const server = createServer(async (req, res) => {
    const pathname = new URL(req.url, "http://localhost").pathname.replace(/^\/wechat_auth/, "");
    if (pathname.startsWith("/v1/") || pathname === "/healthz") {
      const body = [];
      for await (const chunk of req) body.push(chunk);
      try {
        const upstream = await fetch(MOCK + req.url.replace(/^\/wechat_auth/, ""), {
          method: req.method,
          headers: { "Content-Type": "application/json", ...(req.headers.authorization ? { Authorization: req.headers.authorization } : {}) },
          body: body.length ? Buffer.concat(body) : undefined,
        });
        res.writeHead(upstream.status, { "Content-Type": upstream.headers.get("content-type") || "application/json" });
        res.end(Buffer.from(await upstream.arrayBuffer()));
      } catch (_) { res.writeHead(502); res.end(); }
      return;
    }
    const relative = pathname.replace(/^\/admin\//, "") || "index.html";
    if (!["index.html", "js/config.js", "js/api.js", "js/app.js", "css/style.css"].includes(relative)) {
      res.writeHead(404); res.end(); return;
    }
    res.setHeader("Content-Type", relative.endsWith(".js") ? "text/javascript" : relative.endsWith(".css") ? "text/css" : "text/html; charset=utf-8");
    res.end(readFileSync(path.join(consoleDir, relative)));
  });
  await new Promise((resolve) => server.listen(0, "127.0.0.1", resolve));
  return { server, origin: "http://127.0.0.1:" + server.address().port };
}

let passed = 0, failed = 0;
function check(name, cond, detail) {
  if (cond) { passed++; console.log("  ✓ " + name); }
  else { failed++; console.log("  ✗ " + name + (detail ? " — " + detail : "")); }
}

async function main() {
  // mock 必须已在 8901 就绪（脚本不打 /mock/reset 之外的假设）。
  const health = await fetch(MOCK + "/healthz").then((r) => r.json()).catch(() => null);
  if (!health || health.status !== "ok") {
    console.error("契约 mock 未就绪，请先：node server/mock/server.js --port 8901");
    process.exit(2);
  }
  await fetch(MOCK + "/mock/reset", { method: "POST" });

  const { server, origin } = await startConsoleServer();
  const pageURL = origin + "/admin/";
  const browser = await chromium.launch({ channel: "chrome", headless: true });
  const page = await browser.newPage({ viewport: { width: 1280, height: 900 } });

  const pageErrors = [];
  page.on("pageerror", (e) => pageErrors.push(String(e)));

  try {
    console.log("[1] 登录视图（微信开放平台官方授权）");
    const requests = [];
    page.on("request", (req) => requests.push(req.url()));
    await page.goto(pageURL);
    await page.waitForSelector("#login-authorize:not([hidden])", { timeout: 8000 });
    const authorizeURL = new URL(await page.getAttribute("#login-authorize", "href"));
    check("授权按钮指向微信官方域名", authorizeURL.origin === "https://open.weixin.qq.com");
    check("授权页在新标签页打开", await page.getAttribute("#login-authorize", "target") === "_blank");
    check("不再加载不可用的普通链接二维码", !requests.some((url) => /\/qr$/.test(url)) && await page.locator("#login-qr").count() === 0);
    check("登录状态提示打开官方授权页", (await page.textContent("#login-status")).includes("打开微信授权页"));
    check("默认请求同源 API", requests.includes(origin + "/v1/auth/session"));
    check("安装标识已生成本地", /^wcbadmin-[0-9a-f]{32}$/.test(await page.textContent("#login-install-id")));
    await page.screenshot({ path: path.join(shots, "shot-login.png") });

    console.log("\n[2] 模拟卖家扫码 → 进入应用");
    const ticket = authorizeURL.searchParams.get("state");
    const scan = await fetch(MOCK + "/mock/scan/" + ticket, { method: "POST" }).then((r) => r.json());
    check("mock 扫码授权成功", scan.status === "authorized");
    await page.waitForSelector("#view-app:not([hidden])", { timeout: 8000 });
    check("应用视图已展示", await page.isVisible("#view-app"));
    check("当前卖家身份已显示", (await page.textContent("#current-account")).includes("已登录"));

    console.log("\n[3] 待确认列表");
    await page.waitForFunction(() => document.querySelectorAll("#pending-body tr:not(.row-empty)").length > 0, null, { timeout: 8000 });
    check("徽标计数 = 2", (await page.textContent("#pending-count")) === "2");
    check("表格 2 行", (await page.locator("#pending-body tr:not(.row-empty)").count()) === 2);
    const firstRowText = await page.textContent("#pending-body tr:not(.row-empty)");
    check("行内含机器码/金额/操作", firstRowText.includes("9f2c1d4e7a3b5c60") && firstRowText.includes("¥38.00"));
    await page.screenshot({ path: path.join(shots, "shot-pending.png") });

    console.log("\n[4] 确认签发（弹窗防误点 → 成功态）");
    await page.locator("#pending-body tr:not(.row-empty)").first().getByRole("button", { name: "确认签发" }).click();
    await page.waitForSelector("#modal-mask:not([hidden])", { timeout: 4000 });
    check("确认弹窗展示目标事实", (await page.textContent("#modal-body")).includes("9f2c1d4e7a3b5c60"));
    await page.click("#modal-ok");
    await page.waitForSelector(".toast-ok", { timeout: 8000 });
    const toastText = await page.textContent(".toast-ok");
    check("成功 toast 含许可证号", /lic_[0-9a-f]{12}/.test(toastText), toastText);
    await page.waitForFunction(() => document.getElementById("pending-count").textContent === "1", null, { timeout: 8000 });
    check("确认后列表自动收敛为 1", (await page.textContent("#pending-count")) === "1");

    console.log("\n[5] 换机重签（剩余登记）");
    await page.locator("#pending-body tr:not(.row-empty)").first().getByRole("button", { name: "换机重签" }).click();
    await page.waitForSelector("#modal-mask:not([hidden])", { timeout: 4000 });
    await page.click("#modal-ok");
    await page.waitForSelector(".toast-ok >> nth=1", { timeout: 8000 });
    check("重签成功 toast", (await page.textContent(".toast-ok >> nth=1")).includes("已换机重签"));

    console.log("\n[6] 已签发台账（许可证号由服务端返回，§11.7 M-2）");
    await page.click("#tab-ledger");
    await page.waitForFunction(() => document.querySelectorAll("#ledger-body tr:not(.row-empty)").length > 0, null, { timeout: 8000 });
    const ledgerText = await page.textContent("#ledger-table");
    check("台账含已签发的许可证号", /lic_[0-9a-f]{12}/.test(ledgerText), ledgerText.slice(0, 200));
    check("本地操作日志已记录 2 条（确认 + 重签）", (await page.locator("#action-log li").count()) === 2);
    check("日志含两类动作标签",
      (await page.textContent("#action-log")).includes("确认签发") &&
      (await page.textContent("#action-log")).includes("换机重签"));
    await page.screenshot({ path: path.join(shots, "shot-ledger.png") });

    // 修复的核心回归：清掉本机日志再刷新。修复之前，这一步会让台账的
    // 许可证号列整体退化成「—」，且作废按钮随之消失——而服务端从头到尾
    // 都知道那个号。旧实现下这条断言必然失败，所以它真的在守着什么。
    console.log("\n[6b] 清空本机日志后许可证号仍在（这条在修复前必然失败）");
    await page.evaluate(() => {
      try { localStorage.removeItem("wcb_admin_action_log"); } catch (_) {}
    });
    await page.reload({ waitUntil: "domcontentloaded" });
    await page.waitForSelector("#tab-ledger", { timeout: 8000 });
    await page.click("#tab-ledger");
    await page.waitForFunction(() => document.querySelectorAll("#ledger-body tr:not(.row-empty)").length > 0, null, { timeout: 8000 });
    const ledgerAfterWipe = await page.textContent("#ledger-table");
    check("日志被清空后台账仍有许可证号", /lic_[0-9a-f]{12}/.test(ledgerAfterWipe), ledgerAfterWipe.slice(0, 200));
    check("台账行仍有作废按钮",
      (await page.locator("#ledger-body button", { hasText: "作废" }).count()) > 0);

    console.log("\n[7] 登录终态、权限及部署路径回归");
    await page.click("#btn-logout");
    await page.waitForSelector("#login-authorize:not([hidden])");
    check("退出清除当前凭证", await page.evaluate(() => sessionStorage.getItem("wcb_admin_tokens")) === null);
    await page.route("**/v1/auth/session/*", (route) => route.fulfill({ json: { status: "rejected" } }));
    await page.click("#login-refresh");
    await page.waitForFunction(() => document.getElementById("login-status").textContent.includes("被拒绝"));
    check("拒绝授权后移除旧链接", await page.getAttribute("#login-authorize", "href") === null);
    await page.unroute("**/v1/auth/session/*");

    await page.route("**/v1/auth/session/*", (route) => route.fulfill({ json: { status: "expired" } }));
    await page.click("#login-refresh");
    await page.waitForFunction(() => document.getElementById("login-status").textContent.includes("已过期"));
    check("过期停止授权并可重新获取", !await page.isVisible("#login-authorize") && await page.isVisible("#login-refresh"));
    await page.unroute("**/v1/auth/session/*");

    let refreshCalls = 0;
    await page.route("**/v1/auth/refresh", (route) => { refreshCalls++; return route.continue(); });
    await page.route("**/v1/admin/requests?*", (route) => route.fulfill({ status: 403, json: { error: { code: "forbidden", message: "无管理员权限" } } }));
    await page.click("#login-refresh");
    await page.waitForSelector("#login-authorize:not([hidden])");
    const forbiddenTicket = new URL(await page.getAttribute("#login-authorize", "href")).searchParams.get("state");
    await fetch(MOCK + "/mock/scan/" + forbiddenTicket, { method: "POST" });
    await page.waitForFunction(() => document.getElementById("login-status").textContent.includes("没有管理员权限"));
    check("非管理员不能进入后台", !await page.isVisible("#view-app"));
    check("403 不会续期且清除凭证", refreshCalls === 0 && await page.evaluate(() => sessionStorage.getItem("wcb_admin_tokens")) === null);
    await page.unroute("**/v1/admin/requests?*");

    await page.evaluate(() => localStorage.setItem("wcb_admin_baseurl_override", "http://127.0.0.1:8901"));
    await page.goto(origin + "/wechat_auth/admin/index.html");
    await page.waitForSelector("#login-authorize:not([hidden])");
    check("带路径前缀部署使用对应同源 API，忽略遗留跨源覆盖", requests.includes(origin + "/wechat_auth/v1/auth/session"));
    const prefixTicket = new URL(await page.getAttribute("#login-authorize", "href")).searchParams.get("state");
    await fetch(MOCK + "/mock/scan/" + prefixTicket, { method: "POST" });
    await page.waitForSelector("#view-app:not([hidden])");
    check("带前缀部署可完整登录", (await page.textContent("#current-server")) === origin + "/wechat_auth");

    console.log("\n[7b] 跨次加载的会话恢复与暂时故障");
    const savedBefore503 = await page.evaluate(() => sessionStorage.getItem("wcb_admin_tokens"));
    await page.route("**/v1/admin/requests?*", (route) => route.fulfill({ status: 503, json: { error: { code: "unavailable", message: "暂时不可用" } } }));
    await page.reload();
    await page.waitForFunction(() => document.getElementById("login-status").textContent.includes("暂时无法验证"));
    check("身份验证暂时故障保留登录凭证", await page.evaluate(() => sessionStorage.getItem("wcb_admin_tokens")) === savedBefore503);
    await page.unroute("**/v1/admin/requests?*");
    await page.click("#login-refresh");
    await page.waitForSelector("#view-app:not([hidden])");
    check("重试身份验证无需再次扫码", await page.isVisible("#view-app"));

    // 契约 mock 原样回传 refresh token；这里模拟真实服务端的凭证轮换。
    await page.unroute("**/v1/auth/refresh");
    await page.route("**/v1/auth/refresh", async (route) => {
      const response = await route.fetch();
      const payload = await response.json();
      payload.refreshToken += "-rotated";
      await route.fulfill({ response, json: payload });
    });
    const oldRefresh = await page.evaluate(() => {
      const t = JSON.parse(sessionStorage.getItem("wcb_admin_tokens"));
      t.expiresAt = new Date(0).toISOString();
      sessionStorage.setItem("wcb_admin_tokens", JSON.stringify(t));
      return t.refreshToken;
    });
    await page.reload();
    await page.waitForSelector("#view-app:not([hidden])");
    check("恢复过期 access token 时先续期并保存轮换凭证", await page.evaluate(() => JSON.parse(sessionStorage.getItem("wcb_admin_tokens")).refreshToken) !== oldRefresh);

    await page.route("**/v1/auth/refresh", (route) => route.fulfill({ json: { status: "expired" } }));
    await page.evaluate(() => {
      const t = JSON.parse(sessionStorage.getItem("wcb_admin_tokens"));
      t.expiresAt = new Date(0).toISOString();
      sessionStorage.setItem("wcb_admin_tokens", JSON.stringify(t));
    });
    await page.reload();
    await page.waitForFunction(() => document.getElementById("login-status").textContent.includes("已失效"));
    check("refresh token 过期清除凭证且可重新登录", !await page.isVisible("#view-app") && await page.evaluate(() => sessionStorage.getItem("wcb_admin_tokens")) === null);

    console.log("\n[7c] 本地超时及旧轮询竞态");
    const edge = await browser.newPage();
    edge.on("pageerror", (err) => pageErrors.push(String(err)));
    try {
      await edge.route("**/v1/auth/session", (route) => route.fulfill({ json: {
        ticket: "bad-link", authorizeUrl: "javascript:alert(1)",
        expiresAt: new Date(Date.now() + 60000).toISOString(),
      } }));
      await edge.goto(pageURL);
      await edge.waitForFunction(() => document.getElementById("login-status").textContent.includes("失败"));
      check("拒绝无效授权链接并保留重试入口", await edge.getAttribute("#login-authorize", "href") === null && await edge.isVisible("#login-refresh"));
      await edge.unroute("**/v1/auth/session");
      await edge.route("**/v1/auth/session", (route) => route.fulfill({ json: {
        ticket: "short-lived", authorizeUrl: "https://open.weixin.qq.com/connect/qrconnect?state=short-lived",
        expiresAt: new Date(Date.now() + 800).toISOString(),
      } }));
      await edge.route("**/v1/auth/session/*", (route) => route.fulfill({ json: { status: "pending" } }));
      await edge.click("#login-refresh");
      await edge.waitForFunction(() => document.getElementById("login-status").textContent.includes("已过期"));
      check("即使服务端持续 pending，本地倒计时到期也移除授权链接", await edge.getAttribute("#login-authorize", "href") === null);
      await edge.unroute("**/v1/auth/session");
      await edge.unroute("**/v1/auth/session/*");

      let releaseOldPoll, oldPollSeen;
      const oldGate = new Promise((resolve) => { releaseOldPoll = resolve; });
      const oldSeen = new Promise((resolve) => { oldPollSeen = resolve; });
      let oldTicket;
      await edge.route("**/v1/auth/session/*", async (route) => {
        if (!oldTicket) {
          oldTicket = new URL(route.request().url()).pathname.split("/").pop();
          oldPollSeen();
          await oldGate;
          await route.fulfill({ json: { status: "authorized", accessToken: "stale-access", refreshToken: "stale-refresh" } });
        } else await route.continue();
      });
      await edge.click("#login-refresh");
      await oldSeen;
      await edge.waitForSelector("#login-authorize:not([hidden])");
      await edge.click("#login-refresh");
      await edge.waitForFunction((old) => {
        const link = document.getElementById("login-authorize");
        return !link.hidden && new URL(link.href).searchParams.get("state") !== old;
      }, oldTicket);
      const response = edge.waitForResponse((res) => res.url().endsWith("/" + oldTicket));
      releaseOldPoll();
      await (await response).finished();
      await edge.evaluate(() => new Promise(requestAnimationFrame));
      check("刷新登录后旧授权结果不能覆盖新会话", !await edge.isVisible("#view-app") && await edge.evaluate(() => sessionStorage.getItem("wcb_admin_tokens")) === null);
    } finally { await edge.close(); }

    console.log("\n[8] 页面无 JS 异常");
    check("无 pageerror", pageErrors.length === 0, pageErrors.join("; "));
  } finally {
    await browser.close();
    await new Promise((resolve) => server.close(resolve));
  }

  console.log("\n========================================");
  console.log("通过 " + passed + " 项，失败 " + failed + " 项");
  if (failed) process.exitCode = 1;
}

main().catch((err) => {
  console.error("浏览器验证崩溃：" + (err && err.stack ? err.stack : err));
  process.exit(1);
});

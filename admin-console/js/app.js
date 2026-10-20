// admin-console 应用逻辑。
//
// 结构：顶部是引导与会话恢复，中间三块视图（登录 / 待确认+台账+设置），
// 底部是通用组件（toast 三态反馈、确认弹窗、剪贴板、格式化）。
// 所有 DOM 操作集中在这里；api.js 不碰 DOM，test/smoke.mjs 不碰 DOM。
(function () {
  "use strict";

  // ------------------------------------------------------------------
  // 存储键与配置
  // ------------------------------------------------------------------

  var LS_INSTALL_ID = "wcb_admin_install_id";
  var LS_BASEURL_OVERRIDE = "wcb_admin_baseurl_override";
  var LS_ACTION_LOG = "wcb_admin_action_log";
  var SS_TOKENS = "wcb_admin_tokens";
  var SS_LOGIN = "wcb_admin_login";

  var CONFIG = window.WCB_CONFIG || { baseURL: "", autoRefreshMs: 10000, actionLogLimit: 200 };

  function defaultBaseURL() {
    // 本地文件仅供开发；线上始终从管理目录的上一级访问 API。
    if (location.protocol === "file:") return CONFIG.baseURL || "http://127.0.0.1:8901";
    return CONFIG.baseURL || new URL("../", location.href).href.replace(/\/+$/, "");
  }

  function allowedBaseURL(value) {
    try {
      var url = new URL(value);
      return /^https?:$/.test(url.protocol) && !url.username && !url.password &&
        !url.search && !url.hash && (location.protocol === "file:" || url.origin === location.origin);
    } catch (_) { return false; }
  }

  function effectiveBaseURL() {
    var override = "";
    try { override = localStorage.getItem(LS_BASEURL_OVERRIDE) || ""; } catch (_) {}
    return allowedBaseURL(override.trim()) ? override.trim().replace(/\/+$/, "") : defaultBaseURL();
  }

  var api = new WCB_CONSOLE.ConsoleApi(effectiveBaseURL());

  // ------------------------------------------------------------------
  // 小工具
  // ------------------------------------------------------------------

  function $(id) { return document.getElementById(id); }

  function esc(s) {
    return String(s == null ? "" : s)
      .replace(/&/g, "&amp;").replace(/</g, "&lt;").replace(/>/g, "&gt;")
      .replace(/"/g, "&quot;").replace(/'/g, "&#39;");
  }

  // 金额：接口给「分」，卖家看「元」。买家没填金额时是 null——那是正常答案。
  function fmtAmount(fen) {
    if (fen === null || fen === undefined) return "—";
    return "¥" + (Number(fen) / 100).toFixed(2);
  }

  // 时间：契约是 UTC ISO（"2006-01-02T15:04:05Z" 布局），卖家在本地时区看。
  function fmtTime(iso) {
    if (!iso) return "—";
    var d = new Date(iso);
    if (isNaN(d.getTime())) return esc(iso);
    var p = function (n) { return (n < 10 ? "0" : "") + n; };
    return d.getFullYear() + "-" + p(d.getMonth() + 1) + "-" + p(d.getDate()) +
      " " + p(d.getHours()) + ":" + p(d.getMinutes()) + ":" + p(d.getSeconds());
  }

  function copyText(text) {
    function fallback() {
      var ta = document.createElement("textarea");
      ta.value = text;
      ta.style.position = "fixed";
      ta.style.opacity = "0";
      document.body.appendChild(ta);
      ta.select();
      var ok = false;
      try { ok = document.execCommand("copy"); } catch (_) {}
      document.body.removeChild(ta);
      return ok;
    }
    if (navigator.clipboard && navigator.clipboard.writeText) {
      return navigator.clipboard.writeText(text).then(function () { return true; }, fallback);
    }
    return Promise.resolve(fallback());
  }

  // ------------------------------------------------------------------
  // 三态反馈：成功 / 失败 / 网络错误
  // ------------------------------------------------------------------

  var TOAST_TITLES = { ok: "成功", err: "操作失败", net: "网络错误" };

  function showToast(kind, title, detail) {
    var root = $("toast-root");
    var el = document.createElement("div");
    el.className = "toast toast-" + kind;
    var t = document.createElement("div");
    t.className = "toast-title";
    t.textContent = title || TOAST_TITLES[kind] || kind;
    el.appendChild(t);
    if (detail) {
      var d = document.createElement("div");
      d.className = "toast-detail";
      d.textContent = detail;
      el.appendChild(d);
    }
    root.appendChild(el);
    var ttl = kind === "ok" ? 6000 : 10000;
    setTimeout(function () {
      el.style.opacity = "0";
      el.style.transition = "opacity .25s";
      setTimeout(function () { el.remove(); }, 260);
    }, ttl);
  }

  // 任一会话内错误 → 三态之一。requestId 必须亮出来，那是服务端日志的钥匙。
  function reportError(scope, err) {
    if (err && err.name === "NetworkError") {
      // 服务端没回话有三种原因：地址写错、服务没起、以及浏览器跨源拦截。
      // 第三种最阴——开发者本机同源调试时永远不会遇到，只有卖家会撞上，
      // 而卖家看到的只是一句「Failed to fetch」。所以这里要把跨源点出来。
      showToast("net", "网络错误：" + scope, err.message +
        "（请检查服务端地址及运行状态；管理页面与接口应同源部署）");
    } else if (err && err.name === "ApiError") {
      var detail = "HTTP " + err.status + " · " + err.code + "：" + (err.message || "");
      if (err.requestId) detail += "（request_id: " + err.requestId + "）";
      showToast("err", scope + "失败", detail);
    } else {
      showToast("err", scope + "失败", (err && err.message) || String(err));
    }
  }

  // ------------------------------------------------------------------
  // 确认弹窗（确认签发 / 换机重签 / 作废都是不可逆动作，防误点）
  // ------------------------------------------------------------------

  var modalResolve = null;

  function showConfirm(title, rows) {
    // rows: [[label, value], ...]
    $("modal-title").textContent = title;
    var body = $("modal-body");
    body.innerHTML = "";
    if (rows && rows.length) {
      var dl = document.createElement("dl");
      rows.forEach(function (r) {
        var dt = document.createElement("dt");
        dt.textContent = r[0];
        var dd = document.createElement("dd");
        dd.textContent = r[1];
        dl.appendChild(dt);
        dl.appendChild(dd);
      });
      body.appendChild(dl);
    }
    $("modal-mask").hidden = false;
    return new Promise(function (resolve) { modalResolve = resolve; });
  }

  function closeModal(result) {
    $("modal-mask").hidden = true;
    if (modalResolve) { modalResolve(result); modalResolve = null; }
  }

  $("modal-ok").addEventListener("click", function () { closeModal(true); });
  $("modal-cancel").addEventListener("click", function () { closeModal(false); });
  $("modal-mask").addEventListener("click", function (e) {
    if (e.target === $("modal-mask")) closeModal(false);
  });
  document.addEventListener("keydown", function (e) {
    if (e.key === "Escape" && !$("modal-mask").hidden) closeModal(false);
  });

  function modalOpen() { return !$("modal-mask").hidden; }

  // ------------------------------------------------------------------
  // 本地操作日志（台账的许可证号来源，见设计文档 §11.7 候选增量）
  // ------------------------------------------------------------------

  function readLog() {
    try {
      var raw = localStorage.getItem(LS_ACTION_LOG);
      var arr = raw ? JSON.parse(raw) : [];
      return Array.isArray(arr) ? arr : [];
    } catch (_) { return []; }
  }

  function writeLog(entries) {
    try { localStorage.setItem(LS_ACTION_LOG, JSON.stringify(entries.slice(0, CONFIG.actionLogLimit))); } catch (_) {}
  }

  function appendLog(action, target, ok, detail) {
    var entries = readLog();
    entries.unshift({
      t: new Date().toISOString(),
      action: action,        // confirm | reissue | revoke
      target: target || "",
      ok: !!ok,
      detail: detail || "",
    });
    writeLog(entries);
    renderLog();
  }

  var ACTION_LABELS = { confirm: "确认签发", reissue: "换机重签", revoke: "作废" };

  function renderLog() {
    var ul = $("action-log");
    var entries = readLog();
    if (!entries.length) {
      ul.innerHTML = '<li><span class="log-target">本机还没有操作记录。确认 / 重签 / 作废的结果会记在这里。</span></li>';
      return;
    }
    ul.innerHTML = "";
    entries.forEach(function (e) {
      var li = document.createElement("li");
      var time = document.createElement("span");
      time.className = "log-time";
      time.textContent = fmtTime(e.t);
      var act = document.createElement("span");
      act.className = "log-action";
      act.textContent = ACTION_LABELS[e.action] || e.action;
      var target = document.createElement("span");
      target.className = "log-target";
      target.textContent = e.target;
      target.title = e.target;
      var res = document.createElement("span");
      res.className = "log-result " + (e.ok ? "log-ok" : "log-fail");
      res.textContent = e.ok ? "成功" + (e.detail ? " · " + e.detail : "") : "失败 · " + (e.detail || "");
      li.appendChild(time);
      li.appendChild(act);
      li.appendChild(target);
      li.appendChild(res);
      ul.appendChild(li);
    });
  }

  // 某台机器的最新本机许可证号（确认动作按 requestId 匹配）。
  function localLicenseForRequest(requestId) {
    var entries = readLog();
    for (var i = 0; i < entries.length; i++) {
      var e = entries[i];
      if (e.ok && e.action === "confirm" && e.requestId === requestId && e.licenseId) {
        return e.licenseId;
      }
    }
    return null;
  }

  // ------------------------------------------------------------------
  // 登录视图：微信扫码
  // ------------------------------------------------------------------

  var loginTicket = null;
  var loginTimer = null;
  var countdownTimer = null;
  var loginExpiresAt = 0;
  var loginGeneration = 0;

  function installId() {
    var id = "";
    try { id = localStorage.getItem(LS_INSTALL_ID) || ""; } catch (_) {}
    if (!id) {
      id = "wcbadmin-" + randomHex(16);
      try { localStorage.setItem(LS_INSTALL_ID, id); } catch (_) {}
    }
    return id;
  }

  function randomHex(bytes) {
    var arr = new Uint8Array(bytes);
    (window.crypto || window.msCrypto).getRandomValues(arr);
    var out = "";
    for (var i = 0; i < arr.length; i++) out += (arr[i] < 16 ? "0" : "") + arr[i].toString(16);
    return out;
  }

  function showLogin() {
    stopAutoRefresh();
    stopLoginPolling();
    $("view-app").hidden = true;
    $("view-login").hidden = false;
    $("login-install-id").textContent = installId();
    startLogin();
  }

  function setLoginStatus(text, opts) {
    $("login-status").textContent = text;
    var showRefresh = !!(opts && opts.refresh);
    $("login-refresh").hidden = !showRefresh;
  }

  async function startLogin() {
    stopLoginPolling();
    clearPendingLogin();
    var generation = loginGeneration;
    hideAuthorizeLink();
    $("login-refresh").textContent = "重新获取登录链接";
    $("login-countdown").hidden = true;
    setLoginStatus("正在准备微信登录…");
    try {
      var res = await api.auth.createSession(installId(), "admin-console");
      if (generation !== loginGeneration) return;
      var authorizeURL = new URL(res.authorizeUrl);
      if (authorizeURL.protocol !== "https:" || authorizeURL.hostname !== "open.weixin.qq.com" ||
          authorizeURL.port || authorizeURL.username || authorizeURL.password || !res.ticket ||
          !Number.isFinite(Date.parse(res.expiresAt)) || Date.parse(res.expiresAt) <= Date.now()) {
        throw new Error("服务端返回的微信授权链接或有效期无效，请检查开放平台配置。");
      }
      loginTicket = res.ticket;
      loginExpiresAt = Date.parse(res.expiresAt);
      $("login-authorize").href = authorizeURL.href;
      $("login-authorize").hidden = false;
      setLoginStatus("请打开微信授权页扫码，完成后将自动进入后台。", { refresh: true });
      startCountdown();
      pollLogin(loginTicket, generation);
    } catch (err) {
      if (generation !== loginGeneration) return;
      setLoginStatus("准备微信登录失败，请重试。", { refresh: true });
      reportError("登录", err);
    }
  }

  function hideAuthorizeLink() {
    $("login-authorize").hidden = true;
    $("login-authorize").removeAttribute("href");
  }

  function endLogin(message) {
    stopLoginPolling();
    clearPendingLogin();
    hideAuthorizeLink();
    $("login-countdown").hidden = true;
    setLoginStatus(message, { refresh: true });
  }

  function startCountdown() {
    stopCountdown();
    $("login-countdown").hidden = false;
    var tick = function () {
      var left = Math.max(0, Math.ceil((loginExpiresAt - Date.now()) / 1000));
      $("login-countdown").textContent = left > 0 ? "登录链接有效时间剩余 " + left + " 秒" : "";
      if (left <= 0) endLogin("登录链接已过期，请重新获取。");
    };
    countdownTimer = setInterval(tick, 1000);
    tick();
  }

  function stopCountdown() {
    if (countdownTimer) { clearInterval(countdownTimer); countdownTimer = null; }
  }

  async function pollLogin(ticket, generation) {
    if (!ticket || ticket !== loginTicket || generation !== loginGeneration) return;
    try {
      var res = await api.auth.pollSession(ticket);
      if (generation !== loginGeneration || ticket !== loginTicket) return;
      if (res.status === "authorized") {
        await onAuthorized(res);
        return;
      }
      if (res.status === "expired") {
        endLogin("登录链接已过期，请重新获取。");
        return;
      }
      // rejected 是终态：这一张码不会再变成 authorized。不停轮询的话，
      // 服务端每次都答 rejected，而状态栏一直停在「等待微信扫码…」——
      // 卖家看到的是一个永远不会到来的登录。
      if (res.status === "rejected") {
        endLogin("本次授权被拒绝，请重新获取登录链接。");
        return;
      }
      // pending：网络抖动不放弃，有效期内继续等待。
      loginTimer = setTimeout(function () { pollLogin(ticket, generation); }, 2000);
    } catch (err) {
      if (generation !== loginGeneration || ticket !== loginTicket) return;
      setLoginStatus("暂时无法查询登录结果，正在重试…", { refresh: true });
      loginTimer = setTimeout(function () { pollLogin(ticket, generation); }, 3000);
    }
  }

  function stopLoginPolling() {
    loginGeneration++;
    loginTicket = null;
    if (loginTimer) { clearTimeout(loginTimer); loginTimer = null; }
    stopCountdown();
  }

  async function onAuthorized(res) {
    stopLoginPolling();
    clearPendingLogin();
    var tokens = {
      accessToken: res.accessToken,
      refreshToken: res.refreshToken,
      expiresAt: res.expiresAt,
      account: res.account || null,
    };
    await verifyAdmin(tokens);
  }

  // 管理列表本身就是服务端的权限检查，403 不触发令牌刷新。
  async function verifyAdmin(tokens) {
    hideAuthorizeLink();
    $("login-countdown").hidden = true;
    setLoginStatus("正在验证管理员身份…");
    if (!tokens || !tokens.accessToken || !tokens.refreshToken) {
      clearTokens();
      api.setTokens(null);
      setLoginStatus("未取得完整登录凭证，请重新登录。", { refresh: true });
      return;
    }
    saveTokens(tokens);
    api.setTokens(tokens);
    try {
      var result = await api.admin.listRequests("pending");
      if (!result || !Array.isArray(result.items)) {
        throw new Error("管理接口返回格式无效，请检查服务端地址和反向代理配置。");
      }
      enterApp(result.items);
    } catch (err) {
      if (err && err.name === "ApiError" && [401, 403, 404].indexOf(err.status) !== -1) {
        clearTokens();
        api.setTokens(null);
        var messages = {
          401: "登录状态已失效，请重新登录。",
          403: "当前微信没有管理员权限，请使用管理员微信重新登录。",
          404: "管理接口未启用或地址配置有误，请检查服务端配置。",
        };
        setLoginStatus(messages[err.status], { refresh: true });
        if (err.status === 403) {
          api.auth.revoke(tokens.refreshToken).catch(function () {});
        }
      } else {
        // 网络/服务暂时故障保留凭证，可直接重试权限验证。
        $("login-refresh").textContent = "重试验证";
        setLoginStatus("暂时无法验证管理员身份，请重试。", { refresh: true });
      }
      reportError("验证管理员身份", err);
    }
  }

  $("login-refresh").addEventListener("click", function () {
    var tokens = loadTokens();
    if (tokens && tokens.accessToken) verifyAdmin(tokens);
    else startLogin();
  });

  // Navigation leaves this document, so persist the one-time ticket in this
  // tab before opening WeChat. The callback returns to /admin/ without placing
  // credentials in a URL; that page resumes this exact session instead of
  // creating a new ticket. Stopping the old poller prevents double delivery.
  $("login-authorize").addEventListener("click", function (event) {
    if (!loginTicket || Date.now() >= loginExpiresAt) {
      event.preventDefault();
      endLogin("登录链接已过期，请重新获取。");
      return;
    }
    try {
      sessionStorage.setItem(SS_LOGIN, JSON.stringify({
        ticket: loginTicket, expiresAt: loginExpiresAt, baseURL: api.baseURL,
      }));
    } catch (_) {
      event.preventDefault();
      setLoginStatus("浏览器无法保存登录会话，请允许本站使用会话存储后重试。", { refresh: true });
      return;
    }
    stopLoginPolling();
  });

  function clearPendingLogin() {
    try { sessionStorage.removeItem(SS_LOGIN); } catch (_) {}
  }

  function loadPendingLogin() {
    try {
      var value = JSON.parse(sessionStorage.getItem(SS_LOGIN));
      if (value && typeof value.ticket === "string" && value.ticket &&
          Number.isFinite(value.expiresAt) && value.baseURL === api.baseURL) return value;
    } catch (_) {}
    return null;
  }

  function resumeLogin(pending) {
    stopLoginPolling();
    $("login-install-id").textContent = installId();
    hideAuthorizeLink();
    $("login-refresh").textContent = "重新获取登录链接";
    if (pending.expiresAt <= Date.now()) {
      endLogin("登录链接已过期，请重新获取。");
      return;
    }
    loginTicket = pending.ticket;
    loginExpiresAt = pending.expiresAt;
    setLoginStatus("正在完成微信登录并进入后台…", { refresh: true });
    startCountdown();
    pollLogin(loginTicket, loginGeneration);
  }

  // ------------------------------------------------------------------
  // 会话存取（令牌只进 sessionStorage，不写 localStorage）
  // ------------------------------------------------------------------

  function saveTokens(tokens) {
    try { sessionStorage.setItem(SS_TOKENS, JSON.stringify(tokens)); } catch (_) {}
  }

  function loadTokens() {
    try {
      var raw = sessionStorage.getItem(SS_TOKENS);
      return raw ? JSON.parse(raw) : null;
    } catch (_) { return null; }
  }

  function clearTokens() {
    try { sessionStorage.removeItem(SS_TOKENS); } catch (_) {}
  }

  // 续期后把新令牌写回存储——api.js 只负责换， app.js 负责记住。
  api.onTokensChanged(function (tokens) { saveTokens(tokens); });

  // ------------------------------------------------------------------
  // 应用视图
  // ------------------------------------------------------------------

  var inFlight = 0;
  var refreshTimer = null;
  var activeTab = "pending";

  function enterApp(pendingItems) {
    $("view-login").hidden = true;
    $("view-app").hidden = false;
    var tokens = loadTokens();
    var nick = tokens && tokens.account ? tokens.account.nickname : "";
    $("current-account").textContent = nick ? "已登录：" + nick : "已登录";
    $("current-server").textContent = api.baseURL;
    renderLog();
    if (pendingItems) renderPending(pendingItems);
    else refreshAll();
    startAutoRefresh();
  }

  async function refreshAll() {
    await Promise.all([refreshPending(), activeTab === "ledger" ? refreshLedger() : Promise.resolve()]);
  }

  function startAutoRefresh() {
    stopAutoRefresh();
    if (!$("auto-refresh").checked) return;
    refreshTimer = setInterval(function () {
      if (activeTab !== "pending" || modalOpen() || document.hidden || inFlight > 0) return;
      refreshPending();
    }, CONFIG.autoRefreshMs || 10000);
  }

  function stopAutoRefresh() {
    if (refreshTimer) { clearInterval(refreshTimer); refreshTimer = null; }
  }

  $("auto-refresh").addEventListener("change", startAutoRefresh);
  document.addEventListener("visibilitychange", function () {
    if (!document.hidden && !$("view-app").hidden && activeTab === "pending") refreshPending();
  });

  // ---------------- 页签 ----------------

  function switchTab(name) {
    activeTab = name;
    $("tab-pending").classList.toggle("is-active", name === "pending");
    $("tab-ledger").classList.toggle("is-active", name === "ledger");
    $("tab-settings").classList.toggle("is-active", name === "settings");
    $("tab-pending").setAttribute("aria-selected", name === "pending");
    $("tab-ledger").setAttribute("aria-selected", name === "ledger");
    $("tab-settings").setAttribute("aria-selected", name === "settings");
    $("panel-pending").hidden = name !== "pending";
    $("panel-ledger").hidden = name !== "ledger";
    $("panel-settings").hidden = name !== "settings";
    if (name === "pending") refreshPending();
    if (name === "ledger") refreshLedger();
  }

  $("tab-pending").addEventListener("click", function () { switchTab("pending"); });
  $("tab-ledger").addEventListener("click", function () { switchTab("ledger"); });
  $("tab-settings").addEventListener("click", function () { switchTab("settings"); });
  $("btn-refresh").addEventListener("click", refreshPending);

  // ---------------- 待确认列表 ----------------

  async function refreshPending() {
    inFlight++;
    try {
      var res = await api.admin.listRequests("pending");
      var items = (res && res.items) || [];
      renderPending(items);
    } catch (err) {
      if (err && err.name === "ApiError" && err.status === 401) { forceRelogin(); return; }
      renderPending([]);
      reportError("加载待确认列表", err);
    } finally {
      inFlight--;
    }
  }

  function renderPending(items) {
    $("pending-count").textContent = String(items.length);
    var tbody = $("pending-body");
    tbody.innerHTML = "";
    if (!items.length) {
      tbody.innerHTML = '<tr class="row-empty"><td colspan="6">暂无待确认登记。买家完成购买登记后会出现在这里。</td></tr>';
      return;
    }
    items.forEach(function (it) {
      tbody.appendChild(pendingRow(it));
    });
  }

  function pendingRow(it) {
    var tr = document.createElement("tr");

    var tdTime = document.createElement("td");
    tdTime.textContent = fmtTime(it.createdAt);
    tr.appendChild(tdTime);

    var tdInstall = document.createElement("td");
    tdInstall.innerHTML = '<span class="mono">' + esc(it.installId) + "</span> ";
    tdInstall.appendChild(copyButton(it.installId));
    tr.appendChild(tdInstall);

    var tdAnchor = document.createElement("td");
    tdAnchor.innerHTML = '<span class="mono" title="' + esc(it.anchorDigest) + '">' + esc(it.anchorDigest) + "</span>";
    tr.appendChild(tdAnchor);

    var tdNick = document.createElement("td");
    tdNick.textContent = it.nickname || "—";
    tr.appendChild(tdNick);

    var tdAmount = document.createElement("td");
    tdAmount.className = "col-amount";
    var amt = document.createElement("span");
    amt.className = "amount";
    amt.textContent = fmtAmount(it.amountFen);
    tdAmount.appendChild(amt);
    tr.appendChild(tdAmount);

    var tdOps = document.createElement("td");
    tdOps.className = "col-ops";
    var ops = document.createElement("div");
    ops.className = "ops";
    ops.appendChild(actionButton("确认签发", "btn-primary btn-sm", function () { doConfirm(it); }));
    ops.appendChild(actionButton("拒绝", "btn-sm", function () { doReject(it); }));
    ops.appendChild(actionButton("换机重签", "btn-sm", function () { doReissue(it.installId); }));
    tdOps.appendChild(ops);
    tr.appendChild(tdOps);

    return tr;
  }

  function actionButton(label, cls, onClick) {
    var b = document.createElement("button");
    b.type = "button";
    b.className = "btn " + cls;
    b.textContent = label;
    b.addEventListener("click", onClick);
    return b;
  }

  function copyButton(text) {
    var b = document.createElement("button");
    b.type = "button";
    b.className = "copy-btn";
    b.textContent = "复制";
    b.title = "复制 " + text;
    b.addEventListener("click", function () {
      copyText(text).then(function (ok) {
        showToast(ok ? "ok" : "err", ok ? "已复制" : "复制失败", ok ? text : "请手动选中复制");
      });
    });
    return b;
  }

  // ---------------- 台账 ----------------

  async function refreshLedger() {
    inFlight++;
    try {
      var res = await api.admin.listRequests("confirmed");
      var items = (res && res.items) || [];
      renderLedger(items);
    } catch (err) {
      if (err && err.name === "ApiError" && err.status === 401) { forceRelogin(); return; }
      renderLedger([]);
      reportError("加载台账", err);
    } finally {
      inFlight--;
    }
  }

  function renderLedger(items) {
    var tbody = $("ledger-body");
    tbody.innerHTML = "";
    if (!items.length) {
      tbody.innerHTML = '<tr class="row-empty"><td colspan="6">还没有已签发的登记。</td></tr>';
      return;
    }
    items.forEach(function (it) {
      tbody.appendChild(ledgerRow(it));
    });
  }

  function ledgerRow(it) {
    // 服务端给的许可证号是权威答案（设计文档 §11.7 M-2）。本机日志只作为
    // 兜底：对着一次早于该修复的部署时，它仍然是唯一记得这个号的地方。
    //
    // 顺序不能反过来。本机日志记的是「这台浏览器点过确认时服务端回了什么」，
    // 换机重签之后那条记录就过期了——而重签的返回值当时只落进了 detail 字段，
    // 没有任何结构化字段让这里能认出来。先读日志会让台账显示一张已经被替换掉
    // 的旧票，并让「作废」按钮指向它。
    var licenseId = it.licenseId || localLicenseForRequest(it.requestId);
    var tr = document.createElement("tr");

    var tdTime = document.createElement("td");
    tdTime.textContent = fmtTime(it.createdAt);
    tr.appendChild(tdTime);

    var tdInstall = document.createElement("td");
    tdInstall.innerHTML = '<span class="mono">' + esc(it.installId) + "</span> ";
    tdInstall.appendChild(copyButton(it.installId));
    tr.appendChild(tdInstall);

    var tdLicense = document.createElement("td");
    if (licenseId) {
      tdLicense.innerHTML = '<span class="mono">' + esc(licenseId) + "</span> ";
      tdLicense.appendChild(copyButton(licenseId));
    } else {
      tdLicense.innerHTML = '<span class="mono" style="color:var(--text-dim)">—</span>';
    }
    tr.appendChild(tdLicense);

    var tdNick = document.createElement("td");
    tdNick.textContent = it.nickname || "—";
    tr.appendChild(tdNick);

    var tdAmount = document.createElement("td");
    tdAmount.className = "col-amount";
    var amt = document.createElement("span");
    amt.className = "amount";
    amt.textContent = fmtAmount(it.amountFen);
    tdAmount.appendChild(amt);
    tr.appendChild(tdAmount);

    var tdOps = document.createElement("td");
    tdOps.className = "col-ops";
    var ops = document.createElement("div");
    ops.className = "ops";
    ops.appendChild(actionButton("换机重签", "btn-sm", function () { doReissue(it.installId); }));
    if (licenseId) {
      ops.appendChild(actionButton("作废", "btn-danger btn-sm", function () { doRevoke(licenseId); }));
    } else {
      var hint = document.createElement("span");
      hint.className = "toolbar-note";
      hint.textContent = "许可证号未知，请用手动操作区";
      ops.appendChild(hint);
    }
    tdOps.appendChild(ops);
    tr.appendChild(tdOps);

    return tr;
  }

  // ---------------- 三个管理动作 ----------------

  async function doReject(it) {
    // 二次确认里写清后果：拒绝之后这台机器可以立刻重新登记，而这笔订单
    // 永远不会签发。卖家面对的是"钱没到/机器码对不上"，不是"稍后再看看"。
    var ok = await showConfirm("确认拒绝这张登记？", [
      ["机器码", it.installId],
      ["微信昵称", it.nickname || "—"],
      ["期望金额", fmtAmount(it.amountFen)],
      ["登记时间", fmtTime(it.createdAt)],
      ["后果", "永不签发；该机器可立即重新登记"],
    ]);
    if (!ok) return;
    inFlight++;
    try {
      await api.admin.reject(it.requestId);
      appendLog("reject", it.requestId, true, "");
      showToast("ok", "已拒绝", "登记 " + it.requestId + " 不会再签发许可证");
      refreshPending();
      refreshLedger();
    } catch (err) {
      appendLog("reject", it.requestId, false, errMessage(err));
      reportError("拒绝登记", err);
      if (err && err.name === "ApiError" && err.status === 401) forceRelogin();
    } finally {
      inFlight--;
    }
  }

  async function doConfirm(it) {
    var ok = await showConfirm("确认签发这张登记？", [
      ["机器码", it.installId],
      ["硬件锚摘要", it.anchorDigest || "—"],
      ["微信昵称", it.nickname || "—"],
      ["期望金额", fmtAmount(it.amountFen)],
      ["登记时间", fmtTime(it.createdAt)],
    ]);
    if (!ok) return;
    inFlight++;
    try {
      var res = await api.admin.confirm(it.requestId);
      appendLog("confirm", it.requestId, true, res.licenseId);
      // 日志里带上 requestId↔licenseId 的对应，台账页靠它补许可证号列。
      patchLastLog({ requestId: it.requestId, licenseId: res.licenseId });
      showToast("ok", "已确认签发", "许可证号 " + res.licenseId + "（登记 " + it.requestId + "）");
      refreshPending();
      refreshLedger();
    } catch (err) {
      appendLog("confirm", it.requestId, false, errMessage(err));
      reportError("确认签发", err);
      if (err && err.name === "ApiError" && err.status === 401) forceRelogin();
    } finally {
      inFlight--;
    }
  }

  async function doReissue(installID) {
    if (!installID) return;
    var ok = await showConfirm("对这台机器换机重签？", [
      ["机器码", installID],
      ["说明", "作废该账号当前有效绑定，签发新票；买家重新登记后由轮询获知"],
    ]);
    if (!ok) return;
    inFlight++;
    try {
      var res = await api.admin.reissue(installID);
      appendLog("reissue", installID, true, res.licenseId);
      showToast("ok", "已换机重签", "新许可证号 " + res.licenseId + "（机器码 " + installID + "）");
      refreshPending();
      refreshLedger();
    } catch (err) {
      appendLog("reissue", installID, false, errMessage(err));
      reportError("换机重签", err);
      if (err && err.name === "ApiError" && err.status === 401) forceRelogin();
    } finally {
      inFlight--;
    }
  }

  async function doRevoke(licenseID) {
    if (!licenseID) return;
    var ok = await showConfirm("作废这个许可证？", [
      ["许可证号", licenseID],
      ["说明", "数据库中标记作废，已发票据在本地仍可验签直到客户端更新"],
    ]);
    if (!ok) return;
    inFlight++;
    try {
      await api.admin.revoke(licenseID);
      appendLog("revoke", licenseID, true, "");
      showToast("ok", "已作废", "许可证 " + licenseID + " 已标记作废");
      refreshPending();
      refreshLedger();
    } catch (err) {
      appendLog("revoke", licenseID, false, errMessage(err));
      reportError("作废", err);
      if (err && err.name === "ApiError" && err.status === 401) forceRelogin();
    } finally {
      inFlight--;
    }
  }

  function errMessage(err) {
    if (!err) return "";
    if (err.name === "NetworkError") return "网络错误";
    if (err.name === "ApiError") return err.status + " " + err.code + (err.requestId ? " · " + err.requestId : "");
    return err.message || String(err);
  }

  // appendLog 之后把 requestId/licenseId 补进刚写的那条（台账匹配用）。
  function patchLastLog(fields) {
    var entries = readLog();
    if (!entries.length) return;
    var e = entries[0];
    e.requestId = fields.requestId;
    e.licenseId = fields.licenseId;
    writeLog(entries);
    renderLog();
  }

  // ---------------- 手动操作区 ----------------

  $("btn-manual-reissue").addEventListener("click", function () {
    var v = $("manual-reissue-install").value.trim();
    if (!v) { showToast("err", "请先填写机器码", "换机重签需要 installId"); return; }
    doReissue(v);
  });

  $("btn-manual-revoke").addEventListener("click", function () {
    var v = $("manual-revoke-license").value.trim();
    if (!v) { showToast("err", "请先填写许可证号", "作废需要 licenseId"); return; }
    doRevoke(v);
  });

  // ---------------- 设置 ----------------

  $("settings-baseurl").value = (function () {
    try { return localStorage.getItem(LS_BASEURL_OVERRIDE) || ""; } catch (_) { return ""; }
  })();

  $("btn-save-baseurl").addEventListener("click", function () {
    var v = $("settings-baseurl").value.trim();
    if (v && !allowedBaseURL(v)) {
      showToast("err", "服务端地址无效", "请使用同源 HTTP(S) 地址，不包含账号、密码、查询参数或片段。");
      return;
    }
    // 切换 API 时不能向新地址发送旧服务端签发的凭证。
    clearTokens();
    try {
      if (v) localStorage.setItem(LS_BASEURL_OVERRIDE, v);
      else localStorage.removeItem(LS_BASEURL_OVERRIDE);
    } catch (_) {}
    location.reload();
  });

  $("btn-reset-baseurl").addEventListener("click", function () {
    clearTokens();
    try { localStorage.removeItem(LS_BASEURL_OVERRIDE); } catch (_) {}
    location.reload();
  });

  $("btn-test-conn").addEventListener("click", async function () {
    var out = $("settings-conn-result");
    out.hidden = false;
    out.textContent = "正在测试 " + api.baseURL + " …";
    try {
      var res = await api.health();
      out.textContent = "连接正常（/healthz → " + (res.status || "ok") + "）。地址：" + api.baseURL;
    } catch (err) {
      out.textContent = "连接失败：" + errMessage(err) + "。地址：" + api.baseURL;
    }
  });

  // ---------------- 退出与失效 ----------------

  $("btn-logout").addEventListener("click", async function () {
    stopAutoRefresh();
    // 带上 refreshToken：服务端要靠它定位会话链，不带就静默跳过作废。
    var t = loadTokens();
    try { await api.auth.revoke(t && t.refreshToken); } catch (_) { /* 服务端不在也要能退出 */ }
    clearTokens();
    api.setTokens(null);
    showLogin();
  });

  // 两层续期都失败：令牌彻底死了，回登录页，别让卖家对着一张空表格点。
  function forceRelogin() {
    clearTokens();
    api.setTokens(null);
    showToast("err", "登录状态已失效", "请重新扫码登录");
    showLogin();
  }

  // ------------------------------------------------------------------
  // 引导
  // ------------------------------------------------------------------

  var restored = loadTokens();
  var pendingLogin = loadPendingLogin();
  if (restored && restored.accessToken) {
    clearPendingLogin();
    $("login-install-id").textContent = installId();
    verifyAdmin(restored);
  } else if (pendingLogin) {
    resumeLogin(pendingLogin);
  } else {
    showLogin();
  }
})();

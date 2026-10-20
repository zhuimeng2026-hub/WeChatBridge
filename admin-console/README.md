# 卖家管理后台（admin-console）

买断授权产品的卖家端：一个**零依赖、零构建**的纯静态页面。买家完成购买登记后，
卖家在这里核对事实（机器码 + 金额 + 时间，「是否真到账」以微信零账为准），
一键**确认签发 / 换机重签 / 作废**。

契约依据：`docs/design/buyout-flow-convergence.zh-CN.md` §11.3（API）、§11.7（鉴权变更）。

## 快速开始

```bash
# 1. 起契约 mock
node server/mock/server.js --port 8901

# 2. 直接用浏览器打开本目录的 index.html（双击即可，file:// 可用）
#    本地文件默认连接 http://127.0.0.1:8901，仅供开发
```

登录：创建会话 → 点击「微信扫码登录」→ 当前标签页打开微信官方登录二维码 →
管理员微信扫码授权 → 回调自动跳转到业务后台 → 接续原登录会话、验证管理员权限并显示业务界面。
临时登录票只存当前标签页的 sessionStorage；跳转地址不携带票或令牌。
管理端成功回调返回 303 到 `${WCB_PUBLIC_BASE_URL}/admin/`，支持路径前缀；
Windows 客户端的回调仍保留原购买说明页面。

升级时须同时部署 `admin-console/` 静态文件和 Go 服务，避免新旧流程混用。
管理页面不再展示 `/qr` 返回的普通链接二维码，避免微信将其识别成手机网页而无法登录。
mock 只模拟登录接口；本地验证由测试脚本调用 `/mock/scan`，不会完成真实微信授权。

旧 `/v1/auth/session/:ticket/qr` 已停用：有效待授权会话返回 **409 conflict**，
提示打开 `authorizeUrl`；已失效或完成的会话返回 410，未知会话返回 404。
`qrImageUrl` 字段仅为兼容旧响应结构保留。不要单独抓取微信页面中的二维码：
没有页面中等待扫码并跳转的逻辑，就不会交付回调所需的授权码。
审查与验证记录见 [开放平台登录审查](../docs/design/wechat-open-platform-review-2026-10-20.zh-CN.md)。

## 部署环境变量（.env）

微信相关参数沿用服务端 `server/internal/config/config.go` 的配置，由部署环境的
`.env` 注入 **Go 服务进程**（例如 systemd `EnvironmentFile` 或容器 `--env-file`）。
服务端读取进程环境；静态网页不会读取 `.env`，也无需构建时替换或填写任何微信凭据。

| 环境变量 | 用途 |
| --- | --- |
| `WCB_WECHAT_APP_ID` | 微信开放平台应用 AppID |
| `WCB_WECHAT_APP_SECRET` | 应用 AppSecret，仅服务端使用 |
| `WCB_PUBLIC_BASE_URL` | 对外 HTTPS 接口地址，可包含路径前缀，无尾斜杠 |
| `WCB_WECHAT_AUTH_URL` | 授权入口，沿用已验证的部署值；默认 `https://open.weixin.qq.com/connect/qrconnect` |
| `WCB_WECHAT_API_BASE` | 微信 API 地址，默认 `https://api.weixin.qq.com` |
| `WCB_ADMIN_OPENIDS` | 管理员 OpenID，多个用英文逗号分隔 |
| `WCB_TOKEN_SIGNING_SECRET` | 服务端令牌签名密钥，至少 32 字节 |
| `WCB_BASE_PATH` | Go 服务路由前缀；代理转发时保留前缀则填写，剥离前缀则留空 |

服务端原有的数据库和许可证签名配置仍须提供：`WCB_DATABASE_URL`、
`WCB_LICENSE_PRIVATE_KEY_PATH`；生产环境使用 `WCB_ENV=prod`。
微信授权回调为 `${WCB_PUBLIC_BASE_URL}/v1/auth/wechat/callback`，沿用开放平台已配置的域名。
不要将 `.env`、AppSecret 或签名密钥放入本静态目录。

将页面部署到 `/admin/`（或 `/prefix/admin/`），API 部署到同源 `/v1/*`
（或 `/prefix/v1/*`）。访问管理目录时需以 `/` 结尾，或使用 `index.html`。
页面从管理目录的上一级推导 API 地址，因此部署时不需要修改 `config.js`。

## 配置

| 项 | 位置 | 说明 |
| --- | --- | --- |
| 服务端地址 | `js/config.js` 的 `baseURL` | 默认留空，自动使用管理页面上一级的同源接口；本地文件默认连接 mock |
| 自动刷新间隔 | `js/config.js` 的 `autoRefreshMs` | 待确认列表轮询，默认 10 秒 |
| 运行时覆盖 | 页面「设置」页 | 写本浏览器 localStorage；HTTP 页面仅接受同源地址，并忽略遗留跨源覆盖；切换地址会清除当前登录凭证 |

**部署约束：页面必须与服务端同源。** 服务端目前不带任何 CORS 响应头
（`internal/` 下无 CORS 中间件），`baseURL` 指向别的源时浏览器会在预检阶段
拦掉请求。使用同源反向代理即可，管理接口不需要开放通配 CORS。


## 功能

- **当前账号**：右上角显示微信昵称，真实头像加载成功后才显示；无头像或加载失败时不展示默认头像。
  头像通过内容版本地址长期缓存在浏览器本地，内容不变时复用缓存，更换头像后使用新版本地址。
- **扫码登录**：复用 auth 端点，通过官方授权页扫码，本页轮询取得双令牌。
  令牌只存 sessionStorage，退出登录时请求服务端撤销会话；恢复页面时先验证权限。
  到期前 60 秒或收到 401 时续期，并发请求共用一次轮换；续期过期或重放时重新登录，
  网络/503 暂时故障保留凭证并可重试。拒绝授权、链接过期均停止轮询并移除旧链接。
  导航到官方授权页前保存临时会话并停止旧轮询；回调返回时续用原票，不创建第二张票。
- **待确认列表**：登记时间 / 机器码 installId（可复制）/ 硬件锚摘要 / 微信昵称 /
  期望金额；10 秒自动刷新 + 手动刷新。
- **管理动作**（均带确认弹窗防误点）：
  - 确认签发 `POST /v1/admin/confirm {requestId}` → 展示返回的许可证号；
  - 拒绝登记 `POST /v1/admin/reject {requestId}`；
  - 换机重签 `POST /v1/admin/reissue {installId}`；
  - 作废 `POST /v1/admin/revoke {licenseId}`。
  - 列表之外的机器码 / 许可证号：页面底部「手动操作」区直接输入执行。
- **已签发台账**：confirmed 登记列表 + 本机操作日志（localStorage）。
- **三态反馈**：成功（绿）/ 操作失败（红，含 HTTP 状态、错误码、request_id）/
  网络错误（黄，提示检查 mock 是否启动）。

## 已知边界

1. **许可证号列**：优先显示管理接口返回的 `licenseId`，本机操作日志仅作兜底。
2. **mock 与真实服务端的重签差异**：真实 `store.ReissueLicence` 会一并确认该
   install_id 的待处理登记（买家轮询当即看到 confirmed）；mock 不翻转登记态。
   控制台行为以契约为准。
3. **管理员身份**：真实服务端按 `WCB_ADMIN_OPENIDS` 白名单判定；非管理员得到 403，
   页面不进入后台，也不通过刷新令牌重试。白名单为空时管理路由不挂载（404），
   页面提示检查服务端配置。mock 不执行真实微信身份校验，浏览器测试模拟 403。

## 验证（出口标准）

以下命令从仓库根目录执行：

```powershell
# A. 续期与并发回归（零依赖）
node --test admin-console/test/auth.mjs

# A. 契约冒烟（Node，驱动与浏览器相同的 js/api.js）
$env:WCB_MOCK_SCRIPT = (Resolve-Path server/mock/server.js).Path
node admin-console/test/smoke.mjs

# B. 浏览器全流程（需 playwright-core + 系统 Chrome；mock 先起在 8901）
$env:NODE_PATH = '<playwright-core 所在 node_modules>'
node admin-console/test/browser.mjs
```

覆盖登录、权限拒绝、会话恢复、过期、旧请求竞态、并发续期、暂时故障重试、
同源及路径前缀部署，以及签发/重签/作废/台账。浏览器截图输出到
`output/playwright/admin-console/shot-*.png`。测试不代替生产微信应用的真实扫码验收。

## 文件

```
admin-console/
├── index.html        登录视图 + 应用外壳（待确认 / 台账 / 设置）
├── css/style.css     浅色主题，无构建
├── js/config.js      baseURL 等配置（UMD，Node 可读同一份）
├── js/api.js         契约客户端（UMD：浏览器挂 window，Node 可 require）
├── js/app.js         DOM 逻辑（唯一碰页面的文件）
└── test/             auth.mjs（续期）/ smoke.mjs（契约）/ browser.mjs（UI）
```

## 安全说明

- 令牌只在 sessionStorage，不写入 localStorage；关闭标签页清除本地凭证。
- 管理接口使用 Bearer + 服务端 OpenID 白名单；前端权限检查不代替服务端校验。
- 微信授权链接只接受 HTTPS 的 `open.weixin.qq.com` 地址，登录结果仅从服务端轮询取得。
- 所有错误提示都不回显令牌本身；request_id 是故意展示的——排查时那是服务端日志的钥匙。

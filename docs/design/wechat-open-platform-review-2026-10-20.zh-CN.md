# 微信开放平台登录审查与修复（2026-10-20）

审查范围：远程 `feat/pg-client-integration` 的 `381b77d`、`78650a9`、`034c288`，
以及管理端与服务端的登录接口兼容性。

## 保留的改动

- 网站应用授权使用 `snsapi_login`，符合[微信网站登录官方文档](https://developers.weixin.qq.com/doc/oplatform/Website_App/WeChat_Login/Wechat_Login.html)。
  这是网站应用的协议要求，不能把 `snsapi_userinfo` 当作认证后可随意切换的替代值。
- 管理端打开官方授权页；微信页面完成等待扫码、获取授权码和浏览器跳转，服务端校验 state、交换 code，原客户端轮询结果。
  [官方 WxLogin 脚本](https://res.wx.qq.com/connect/zh_CN/htmledition/js/wxLogin.js)也是在浏览器中加载官方授权页。
- 管理端回调隐藏购买提示，属于展示逻辑。`wcbadmin-*` 标识不授予权限，管理接口仍按服务端 OpenID 白名单校验。
- 数据库删除旧登录票时保留 `state = 'pending'` 条件，保护已经完成授权但尚未交付凭证的会话。

## 修复的问题

1. **独立二维码不能完成浏览器授权链路。** 新增的 `ScannableQRURL` 只抓取二维码路径，
   没有运行微信页面等待扫码及跳转的逻辑；重新编码图片不会触发 code 回调。
   上游失败后再返回普通授权链接二维码，同样让用户停在 pending。
   删除抓取模块及成功 PNG 兜底，统一使用已有的官方浏览器流程。
2. **票额检查与写入之间存在竞态。** 原实现释放全局锁后才删除旧票、预留和写入新票，
   多请求可能共同选择一张旧票，或把尚未写入的新预留误认为已删除，最终超出限额。
   现在同一 installId 的创建从预留到持久化按序执行，不同 installId 不共用 I/O 锁，等待可取消，空闲锁会回收。
3. **成功登录也可能导致后续 429。** 原实现只尝试最旧一张；它已授权时，即使后面存在 pending 票也直接拒绝。
   凭证交付后也未释放票额。现在继续寻找最旧 pending 票，并在成功交付凭证后释放对应预留。
   所有票都已授权且尚待交付时仍返回 429，不删除这些会话。
4. **认证响应缺少缓存限制。** 会话创建、轮询和续期响应都携带敏感数据，但原先仅回调 HTML 禁止缓存。
   现在认证路由及账号接口统一返回 `Cache-Control: no-store`、`Referrer-Policy: no-referrer`，错误响应也覆盖。
5. **注释把显示资料和授权 scope 混为一谈。** `snsapi_login` 不意味着 `FetchProfile` 必然返回零值。
   保留实际 userinfo 请求及失败降级，昵称头像以接口结果为准，权限始终使用已验证的 OpenID。
6. **日志可能泄露凭据。** 微信 HTTP 请求的网络错误通常包装为 `url.Error`，其文本包含带有 AppSecret、
   code、OAuth token 或 OpenID 的 URL；原 `TransportError.Error` 将其直接输出。
   现在仅输出失败类别，保留 `Unwrap` 供 `errors.Is/As` 判断。请求日志与 panic 日志使用路由模板，
   不记录会话 URL 中的 ticket；未匹配路由也不输出原始路径。
7. **回调页面图片缓存存在数据竞争。** `Render` 刷新图片时持锁，读取缓存时却未持锁，
   `TestConcurrentRenderAndRefresh` 在 race 检查中失败。图片快照及状态读取现已使用同一把锁。
   管理端回调文案也改为等待管理员身份验证，避免仅完成微信授权就声称已进入管理后台。

## 接口迁移

`POST /v1/auth/session` 的字段保持兼容，客户端必须使用 `authorizeUrl`。
旧 `qrImageUrl` 仅保留字段；`GET /v1/auth/session/:ticket/qr` 返回：

| 会话状态 | 响应 |
| --- | --- |
| 有效且待授权 | 409 `conflict`，提示在浏览器打开 `authorizeUrl` |
| 已过期或已完成 | 410 `gone` |
| 不存在 | 404 |

当前管理端和 Windows `AccountController.StartAsync` 已使用浏览器授权，无需新增 `.env` 参数。
依赖独立 PNG 登录的旧客户端必须升级。mock 和管理端契约测试同步这些响应。

## 验证

新增回归在修复前复现了：独立 QR 错误返回 200、敏感认证响应未限制缓存、
同设备并发创建留下超额数据库记录，以及连续完成登录后仍因旧预留返回 429。
同时覆盖已授权票保护、寻找后续 pending 票、旧路由状态与取消等待。
本地自动检查不替代部署后的真实微信扫码验收。

修复后的验证结果：

- `go test ./...` 通过。
- `go test -race ./internal/auth ./internal/wechat ./internal/store/postgres ./internal/httpx` 通过。
- 管理端登录续期回归 7 项、接口冒烟 28 项、浏览器流程 36 项全部通过。
- 凭据错误日志与 HTTP 登录票日志的新增回归在修复前失败、修复后通过。

# 进度看板：feat/unionid-bridge（worktree `C:\WeChatBridge\.worktrees\login-perf-and-ux`）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2。
> 本分支 2026-10-19 20:30 自 `feat/login-perf-and-ux` HEAD=`739846e` 拉出（含方案 A+B 客户端 + scope 回滚）。

## 这条分支是什么

**服务端 unionid 桥接：在网站应用 `snsapi_login` 只返 openid+unionid、拿不到昵称头像的前提下，
用同主体已认证公众号，经 `unionid → 公众号 openid → user/info` 反查昵称头像并落库。**

代码域（待开工）：`server/internal/wechat/mp.go`（新增公众号客户端）+ `server/internal/auth`（回调接桥接）+ `server/internal/config`（新增 env 校验）。

服务端代码**尚未开始**。当前阶段 = **等待用户从后台收集凭证与接口权限**（清单见 `docs/design/unionid-bridge-inputs.zh-CN.md`）。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| U-① | 列清 unionid 桥接所需凭证与权限清单 | ✅ | 本 commit | `docs/design/unionid-bridge-inputs.zh-CN.md`，5 类待收集项 + 2 个坑预警 |
| U-② | 用户从后台收集并确认 5 类输入 | ⬜ | — | AppID（可明文）/ AppSecret（仅部署机 env）/ 同开放平台绑定 / user/getid 可用。见 inputs 文档 §7 状态表 |
| U-③ | 公众号客户端 `mp.go`（AppID/Secret + access_token 中控） | ⬜ | — | 7200s 过期、2000 次/日上限、单飞刷新；env `WCB_MP_APP_ID` / `WCB_MP_APP_SECRET` |
| U-④ | `HandleCallback` 接桥接：unionid → getid → user/info → 落库 | ⬜ | — | 写 `store.Account.Nickname/AvatarURL`；失败降级只落 openid+unionid，不阻断登录 |
| U-⑤ | `config.go` 新增 env 必填校验 | ⬜ | — | 与现有网站应用 `WCB_WECHAT_*` 并列 |
| U-⑥ | 复用 `account_avatars` 内容寻址缓存公众号头像（非新工作） | ⬜ | — | 头像 URL 是租约，落本地 sha256 才能长久可用 |

## 验证（计划）

- `go build ./...` / `go test ./internal/wechat/... ./internal/auth/...` 全绿
- 真机：网站应用扫码 → 服务端日志确认拿到 unionid → 桥接拿到昵称头像 → `store.Account` 落库非空
- 降级路径：故意用无效 AppSecret，确认登录仍通、仅昵称头像留空

## 后续 / 风险

- **先确认 `user/getid` 可用性**：微信已把该接口收敛到认证服务号，若不可用则改走公众号 OAuth（UX 不同，需用户再授权一次）。见 inputs 文档 §6。
- 头像租约问题见 §6，复用既有 avatar 包即可，不必新建。
- 不破坏现有 `snsapi_login` 登录路径；桥接是"增强"不是"改造"。

# 进度看板：feat/unionid-bridge（worktree `C:\WeChatBridge\.worktrees\login-perf-and-ux`）

> **归档（2026-10-23）。** 分支 `feat/unionid-bridge` 已删除，成果由 tag
> `archive/feat-unionid-bridge` 钉住。其中 U-⓪ 与 U-③ **已并入 `main`**
> （merge commit `1c05257`）。
>
> 但**表里那三行 ⬜ 至今未落地，而且都还卡着**——U-④ 被 `cgi-bin/user/getid` 这个
> **根本不存在的路径**卡住，U-⑥ / U-⑦ 待办。本看板从此是历史记录，不是活的进度源；
> **但那三行 ⬜ 仍然是待办，别因为「归档」两个字当成已完成。**



> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2。
> 本分支 2026-10-19 20:30 自 `feat/login-perf-and-ux` HEAD=`739846e` 拉出（含方案 A+B 客户端 + scope 回滚）。

## 这条分支是什么

**服务端 unionid 桥接：在网站应用 `snsapi_login` 只返 openid+unionid、拿不到昵称头像的前提下，
用同主体已认证公众号，经 `unionid → 公众号 openid → user/info` 反查昵称头像并落库。**

代码域（待开工）：`server/internal/wechat/mp.go`（新增公众号客户端）+ `server/internal/auth`（回调接桥接）+ `server/internal/config`（新增 env 校验）。

服务端代码**尚未开始**。当前阶段 = **输入已齐（U-② ✅），等开工**——开工第一步不是写代码，
而是把 envctl 共享层的 `wechat-mp` 接到部署机 env（inputs §7.2）。⚠️ 键名对不上：
共享层是 `WX_MP_APPID`，服务端要 `WCB_MP_APP_ID`，`envctl sync` **不会**改名，两行必须先声明进 `.env` 才受管。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| U-① | 列清 unionid 桥接所需凭证与权限清单 | ✅ | 本 commit | `docs/design/unionid-bridge-inputs.zh-CN.md`，5 类待收集项 + 2 个坑预警 |
| U-② | 用户从后台收集并确认 5 类输入 | ✅ | 本 commit | 5 项全就位。**公众号凭据不用现取**——`/opt/envctl` 共享层 `wechat-mp` 组已有（AppID `wxde10b66d0c930b78`），网站应用侧交叉核对为 `wx0ebd7da2e72e6be2`。其中"已认证 / 同开放平台绑定 / getid 可用"三条为**用户口述、机器无法验证**，最终以 U-④ 真机日志为准。见 inputs 文档 §7、§7.1 |
| U-⓪ | **token 响应里的用户信息（主路径）** | ✅ | 本 commit | **2026-10-20 用户提供的新事实**：开放平台用 `sns_login` 登录时，换 token 的响应里**会带简单用户信息**（nickname / headimgurl）。scope 决定的是 `/sns/userinfo` 答不答，**不决定换 token 时已经说了什么**。代码原先**根本没解析这两个字段**（`tokenResponse` 里没有）——微信给了也丢掉。现已解析并落库：session 是第一来源，profile 调用只能改进、不能抹掉 |
| U-③ | 公众号客户端 `mp.go`（AppID/Secret + access_token 中控） | ✅ | 本 commit | `server/internal/wechat/mp.go`。access_token 缓存 + 提前 5 分钟续期、**单飞刷新**（一个应用只持有一个有效 token，并发换 token 会让除最后一个外全部当场失效）、**日预算 2000 次**本地拦下、40001 后换 token 重试一次。`cgi-bin/user/info`（openid→昵称头像）**真机实测可用**。真机测试 `mp_live_test.go` 用共享层凭据实跑通过 |
| U-④ | `HandleCallback` 接桥接：unionid → openid → user/info → 落库 | ⬜ | — | **降级为「补拉路径」**：U-⓪ 若在真机上确实带回昵称头像，桥接就不是昵称头像的必需品了，只在 token 响应没带的那部分账号上有用。**本身仍被 unionid→openid 卡住**：该接口对本公众号**未派发**（`40066`），且 §0 原文写的 `cgi-bin/user/getid` 路径根本不存在。取证过程与三条出路见 inputs §9 |
| U-⑤ | `config.go` 新增 env 必填校验 | 🟡 | 本 commit | env 字段与**防填反校验**已加（`WCB_MP_APP_ID` 不得等于网站应用的 AppID——那是最静默的一种错法）。**「必填」这一条待定**：强制必填会让没配这对凭据的部署直接起不来，而桥接是增强不是命脉，留待与部署接线一起定 |
| U-⑥ | 复用 `account_avatars` 内容寻址缓存公众号头像（非新工作） | ⬜ | — | 头像 URL 是租约，落本地 sha256 才能长久可用 |
| U-⑦ | 可复用能力 `GetUserInfoByUnionID`（unionid→openid→user/info） | ⬜ | — | 不只登录回调内联，供补拉 / admin 查人；是否暴露端点待确认（见 inputs §8） |

## 验证（计划）

- `go build ./...` / `go test ./internal/wechat/... ./internal/auth/...` 全绿
- 真机：网站应用扫码 → 服务端日志确认拿到 unionid → 桥接拿到昵称头像 → `store.Account` 落库非空
- 降级路径：故意用无效 AppSecret，确认登录仍通、仅昵称头像留空

### U-③ 已完成的验证

- `go build/vet/test ./...` 10 包全绿（新增 `internal/wechat` 15 个用例、`internal/config` 5 个子用例）
- **真机**：`mp_live_test.go` 用 `/opt/envctl` 共享层的公众号凭据实跑——
  换到真 token（`expires_in: 7200`）、二次调用**复用**同一 token（没有重新换）、
  `cgi-bin/user/info` 派发并回 `40003 invalid openid`（说明接口活着且在校验 openid）。
  该测试 env 门控，CI 不跑。
- **接口可用性实证**：约三十条候选路径 + 标定过的判别器（404=分组不存在，
  40066=分组下没有这个 action），结论见 inputs §9。**原文的 `cgi-bin/user/getid`
  路径不存在**，规范路径 `cgi-bin/user/info/get` 对本公众号未派发。

### U-⓪ 已完成的验证

- `go build/vet/test ./...` 10 包全绿（新增 `internal/wechat` 2 个 + `internal/auth` 4 个用例）
- **反向对照**：把 `HandleCallback` 里的 session 播种回退成空 profile，
  两个新用例如期变红（`nickname = "", want 小红`）——证明它们不是空转的。
- **真机判据已就位**：回调现在打一条日志（`token_nickname` / `token_avatar`
  等布尔值，不打人名），**下一次真机扫码扫一眼日志就知道这条主路径成不成立**。

## 后续 / 风险

- **下一步只需一次真机扫码**：部署当前 main，扫码一次，看服务日志里的
  `token_nickname`。**true = 主路径成立，昵称头像这件事到此为止，桥接不用做**；
  false = 微信对这个应用不给，才需要回到 §9.3 那三条出路。判读方式见 inputs §10.3。
- **U-④ 仍卡在 unionid→openid 这一步**（inputs §9.3）：该接口对本公众号未派发。
  公众号**是服务号**（`account_type: 2`）、认证权限也齐（菜单/二维码/客服/标签都能派发），
  所以不是「类型不对」也不是「没认证」。出路三条：去后台开接口（最省事，开通后用
  §9.1 的判别器复验）／人工查一次官方文档确认规范路径（文档域名在本环境不可访问）／
  改走公众号 OAuth 二次授权（UX 不同）。**但 U-⓪ 成立后，这一步只影响「token 响应
  没带信息」的那部分账号。**
- **U-② 里「getid 可用」是用户口述、机器无法验证的一条**——2026-10-20 实测下来
  它至少是错的。这正是 inputs §7 那句「最终以 U-④ 真机日志为准」的意思，
  只是真机日志先由接口探测给出了。
- 头像租约问题见 §6，复用既有 avatar 包即可，不必新建——**U-⓪ 带回来的 headimgurl
  同样是租约**，走的是同一条 `refreshAvatar` 缓存链路。
- 不破坏现有 `snsapi_login` 登录路径；桥接是"增强"不是"改造"。


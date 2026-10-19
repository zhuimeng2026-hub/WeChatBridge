# unionid 桥接：需要你从后台收集的凭证与权限清单

> 2026-10-19 建立。分支 `feat/unionid-bridge`（HEAD `739846e`，含方案 A+B 客户端 + scope 回滚 `snsapi_login`）。
> 2026-10-19 更新：**5 项输入已全部就位**，见 §7。服务端代码**尚未开始**，开工前的接线动作见 §7.2。
>
> 本文的原始定位是"输入收集清单"——如果你从微信开放平台 / 公众号后台拿好了凭证，回贴给我（或告知"已就位"），
> 我就写服务端桥接逻辑。事实上 `/opt` 的 envctl 共享层里**已存在一对公众号凭据**，所以多数情况不需要现取。

---

## 0. 为什么需要这些（一句话背景）

`snsapi_login`（网站应用）扫码只返回 `openid` + `unionid`，**拿不到昵称/头像**。
要拿到昵称头像，必须走**同主体的已认证公众号**：用 `unionid` 在公众号侧反查 `openid`，再调公众号
`user/info`。所以除了"已有公众号"这个前提，还要它的 AppID / AppSecret 和对应接口权限。

桥接链路（拿到输入后我会实现）：

```
网站应用 snsapi_login 扫码
   └─ 返 openid + unionid
        └─ 用 unionid 调公众号 cgi-bin/user/getid  →  公众号 openid
             └─ 用公众号 openid 调 cgi-bin/user/info  →  昵称 + 头像
                  └─ 写 store.Account.Nickname / AvatarURL
```

---

## 1. 已具备（你确认过，列出来锁死前提）

- [x] 同主体、已认证的公众号（或小程序）存在
- [x] 该公众号**微信认证已完成**（用户 2026-10-19 确认）
  > 未认证公众号只能拿到 openid，调不了 `user/info` 取昵称头像。这一步是桥接成立的最低门槛。

---

## 2. 必填凭证（服务端调公众号 API 用，走环境变量，明文不入库）

| 项 | 取值示例 | 去哪拿 | 用途 |
| --- | --- | --- | --- |
| 公众号 AppID | `wx............` | 公众号后台「设置与开发 → 基本配置 → 公众号开发信息」 | 换 access_token + 调 user 接口 |
| 公众号 AppSecret | `................` | 同上「AppSecret」点「重置」获取 | 换 access_token（**机密，只走 env**） |

> ⚠️ 这是**公众号**维度，和网站应用 `wx0ebd7da2e72e6be2`（开放平台「网站应用」）是**两套独立凭据**，不要混填。
> 现有服务端配置 `WCB_WECHAT_APP_ID` / `WCB_WECHAT_APP_SECRET` 是网站应用的，公众号要新开一对 env。

---

## 3. 开放平台绑定（决定 unionid 是否一致，桥接能否成立）

- [x] 去 [open.weixin.qq.com](https://open.weixin.qq.com) → 管理中心 → 确认：
  - 网站应用 `wx0ebd7da2e72e6be2` 与上面的公众号，**绑定在同一个开放平台账号**下（用户 2026-10-19 确认）
  - 绑定后，网站应用 `snsapi_login` 扫码**会下发 unionid**
    > 微信规则：同开放平台账号下存在同主体认证公众号 / 小程序时，网站应用扫码也会返回 unionid。
    > 若账号下没有同主体认证公众号，网站应用只返 openid、不返 unionid，桥接直接断。

---

## 4. 接口权限确认（决定能否拿到昵称头像）

| 接口 | 用途 | 需要的权限 / 条件 |
| --- | --- | --- |
| `cgi-bin/token` | 换公众号 access_token | 认证公众号即有 |
| `cgi-bin/user/getid` | **unionid → 公众号 openid**（桥接核心） | 需认证服务号；在「接口权限」确认「获取用户基本信息」已开通 |
| `cgi-bin/user/info` | openid → 昵称 / 头像 / 性别等 | 认证订阅号 / 服务号均有 |

- [x] 在公众号后台「设置与开发 → 接口权限」核对 `user/getid` 是否可用；用户 2026-10-19 确认**可用**，无需申请。

---

## 5. 拿好后怎么交付给我

**不要在这里贴明文 AppSecret。** 建议二选一：

- **A（推荐）**：把公众号 **AppID** 贴给我（可明文），**AppSecret 只填到部署机的 env / secrets**，
  告诉我"已就位"，我来写读取逻辑（env 名先约定为 `WCB_MP_APP_ID` / `WCB_MP_APP_SECRET`）。
- **B**：给我一个**测试号 / 沙箱**的 AppID + AppSecret 先联调，生产值后续替换。

> 2026-10-19：走 **A**，且 AppID 已知（`wxde10b66d0c930b78`）、AppSecret 已在共享层，
> **不需要你手工传递任何明文**。剩下的是把共享层接到部署机 env——见 §7.1 / §7.2。

我拿到后交付（预计 4 块）：

1. `server/internal/wechat/mp.go`：公众号客户端（独立 AppID / Secret + access_token 中控缓存，
   7200s 过期、2000 次 / 日上限，单飞刷新，复用现有 `wechat` 包风格）。
2. `server/internal/auth`：在 `HandleCallback` 拿到 `unionid` 后 → `mp.GetOpenIDByUnionID` →
   `mp.GetUserInfo` → 写 `store.Account.Nickname / AvatarURL`。
3. 配置项 `WCB_MP_APP_ID` / `WCB_MP_APP_SECRET` 加到 `config.go` 必填校验（与现有网站应用配置并列）。
4. **不破坏**现有 `snsapi_login` 路径；桥接失败（如接口不可用）要降级为"只落 openid+unionid"，不影响登录。
5. **新增可复用能力 `GetUserInfoByUnionID`**：把"unionid → 公众号 openid → user/info"封装成独立方法，
   不只登录回调内联用，也供后续按需补拉（库里缺昵称头像时）、admin 侧按 unionid 查人等场景调用。详见 §8。

---

## 6. 两个坑（提前说，避免联调时才踩）

- **头像 URL 是租约不是地址**：`user/info` 返的 `headimgurl` 几天失效。服务端若要长久可用，
  应复用已落地的 `account_avatars` 内容寻址缓存（bytes 落本地、DB 存 sha256）——这部分可复用
  2026-10-19 的 avatar 包，不是新工作，只把来源从"网站应用 sns 头像"换成"公众号头像"。
- **`user/getid` 可能需申请 / 已收敛**：微信近年把"unionid 换 openid"收敛到认证服务号。
  若你的公众号拿不到该接口，备选是走**公众号 OAuth**（`snsapi_userinfo` 在公众号侧是标准能力）
  拉一次授权拿 openid——但要让用户在公众号网页里再授权一次，UX 不同。先确认 `getid` 可用性再决定主路径。

---

## 7. 收集状态

> 2026-10-19 更新：5 项全部就位。**公众号凭据不用去微信后台现取——`/opt` 的 envctl 共享层里已有一对**，
> 只是归属是别的项目，且键名与服务端要读的对不上（见 §7.1，这是最容易填错的一处）。

| 项 | 状态 | 落位 |
| --- | --- | --- |
| 公众号已认证 | ✅ | 用户 2026-10-19 确认（**机器无法验证**） |
| 公众号 AppID | ✅ `wxde10b66d0c930b78` | envctl `wechat-mp` 组 `WX_MP_APPID` |
| 公众号 AppSecret | ✅ 已就位 | envctl `wechat-mp` 组 `WX_MP_APP_SECRET`，**明文不入库不入文档**，只经 env 落到部署机 |
| 网站应用 + 公众号同开放平台绑定 | ✅ | 用户 2026-10-19 确认（**机器无法验证**）；网站应用侧 appid 已交叉核对为 `wx0ebd7da2e72e6be2`，与本文档一致 |
| `user/getid` 接口可用 | ✅ 可用 | 用户 2026-10-19 确认（**机器无法验证**）。§6 的备选路径（公众号 OAuth 二次授权）**不需要启用** |

### 7.1 envctl 键名 → 服务端 env 的映射（写代码前必读）

`/opt/CLAUDE.md` 记着微信**三套命名不可合并**：`WX_APPID`（小程序上传）、`WX_MP_APPID`（公众号）、
`WX_OPEN_APPID`（开放平台）是三个不同应用。本分支要在服务端新增的是**公众号**凭据，
envctl 里对应的源键是：

```
WX_MP_APPID         →  WCB_MP_APP_ID      （本文档 §5 约定的服务端名）
WX_MP_APP_SECRET    →  WCB_MP_APP_SECRET
```

⚠️ **两边名字不同，`envctl sync` 不会帮你改名**。它只做行级替换，且只更新各项目 `.env` 里**已声明**的键——
所以 `WCB_MP_APP_ID` 这两行必须先由 `envctl init` 或手工写进服务端 `.env`、**再**由共享层驱动值，
否则共享层更新时它们根本不在替换范围内。

**两组 appid 必须分开核对，填反了不会报错、只会静默取到另一个应用的 token：**

| 服务端 env | 用途 | 正确来源 | envctl 组 |
| --- | --- | --- | --- |
| `WCB_WECHAT_APP_ID` / `WCB_WECHAT_APP_SECRET` | 网站应用（现有，`snsapi_login`） | `wx0ebd7da2e72e6be2` | `wechat-open`（`WX_OPEN_APPID`） |
| `WCB_MP_APP_ID` / `WCB_MP_APP_SECRET` | 公众号（**新增**，桥接用） | `wxde10b66d0c930b78` | `wechat-mp`（`WX_MP_APPID`） |

> 现状：本机 `/opt` 上**找不到服务端 `.env`**——`grep WCB_WECHAT_APP_ID` 只命中源码与设计文档，
> 部署机在 Windows 侧。所以 §7 表格的"就位"目前只在**共享层**成立，
> 接线（`envctl init` 生成 + 同步到部署机）属于开工第一步，见看板 U-⑤。

### 7.2 开工前剩下的动作

1. 在部署机执行 `envctl init <服务端路径> --use wechat-open,wechat-mp`，确认生成的 `.env` 里
   `WCB_WECHAT_*` 与新增的 `WCB_MP_*` **两组都在**（前者已有，后者新加）。
2. `config.go` 加 `WCB_MP_APP_ID` / `WCB_MP_APP_SECRET` 必填校验（U-⑤）。
3. 真机验证 unionid 真的下发——上面三项 ✅ 中有两条是**用户口述、机器无法验证**的，
   桥接能否成立最终以 U-④ 真机日志为准：`snsapi_login` 回调里拿不到 unionid 就说明同主体绑定那条不成立。

---

## 8. 功能补充：用 unionid 获取用户信息

除登录回调自动补全（§0 链路、§5-2）外，桥接还要对外提供一个**以 unionid 为入口查询用户信息**的能力：

- **方法形态（预计）**：`func (c *MPClient) GetUserInfoByUnionID(ctx, unionid string) (*UserInfo, error)`
  内部顺序：`user/getid(unionid)` → 公众号 openid → `user/info(openid)` → 昵称 / 头像 / 性别 / 地区。
- **调用时机（不限于登录）**：
  - 登录回调（§5-2）内部调用它补全当前用户；
  - **补拉**：库里某 account 只有 openid+unionid、`Nickname/AvatarURL` 为空时，定时或按需触发回填；
  - **admin 查询**：卖家后台按 unionid 直接查某人昵称头像（是否暴露端点见下，待确认）。
- **缓存**：`unionid↔openid` 映射与 `user/info` 结果都应缓存；头像走 §6 的 content-address 落盘，
  昵称等文本短时缓存，避开 access_token 2000 次 / 日上限。
- **待你确认（不影响凭证收集，但影响代码形态）**：
  - 是否需要在 `admin-console` 暴露一个"按 unionid 查用户"的端点？还是仅在服务端内部使用？
  - 补拉是"登录时被动补全"就够，还是要有主动回填任务？

# 进度看板：feat/pg-client-integration（本机 · C:\WeChatBridge 主工作区）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2：只有本分支的属主更新本文件，与 commit 同一时刻。
>
> 2026-10-19 建立。此前本分支**没有看板**——协议要求「看板与代码同一个 commit」，
> 而这条分支已经积累了 29 个不在 `zmuimeng/main` 上的 commit。本表按本分支自己的
> commit 重建，**不复述**另外三条分支看板（`server-convergence` / `admin-console` /
> `client-license-gate`）已记的内容。

## 这条分支是什么

**集成分支**：把三条并行分支（server / admin-console / client-license-gate）的成果合到一处，
并承担它们之间的接缝工作——头像缓存、卖家拒绝端点、兑换码清理、生产域名落地、出包脚本。

代码域：`server/` `admin-console/` `windows/` + 仓库根的构建脚本。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| P-① | 微信头像本地缓存 + 永久 URL | ✅ | `7a16d19` `e9a0a85` `34de4fd` | 微信的 `headimgurl` 是几天就过期的**租约**，不是地址。字节缓存在 `server/internal/avatar/`（内容寻址、魔数嗅探、只允许微信域名、文件名即 ETag）；装配在 `cmd/wcb`，经 `auth.Options.Avatars` 注入。**`34de4fd` 才是让链路真正生效的那一笔**——前两个提交写完了包但没人 new 它 |
| P-② | 卖家「拒绝」端点 | ✅ | `2712d37` | `POST /v1/admin/reject`（设计 §11.7 M-4）。原先卖家只能说「是」和「收」，买家的登记会永远卡在 pending |
| P-③ | 客户端兑换码子系统清理 | ✅ | `3599617` | 服务端 `POST /v1/license/redeem` 早已退役，客户端的 `Redeem/` 目录、en.json 兑换文案、`--redeem` 参数一并删掉。**今天是干净的**——不再有「能开、能发请求、必然失败」的兑换窗 |
| P-④ | 激活服务默认地址落到生产域名 | ✅ | `706a2c3` | `ActivationContract.DefaultBaseAddress` 从 `api.wechatbridge.invalid/` 换成 `https://ocbot.aixifs.com/`，与微信开放平台回调域同域。开发机用 `WECHATBRIDGE_LICENSE_SERVER` 覆盖 |
| P-⑤ | 登录窗首次渲染缺陷 | ✅ | `413d15d` | 按钮永久禁用（XAML 旧文案残留 + 首渲染缺失） |
| P-⑥ | 出包脚本 | ✅ | `020e72f` `6459829` | `windows/scripts/build-app.ps1` 从 `Directory.Build.props` 读版本号，主程序与 ShareTarget 一起 publish，自包含产物少一个运行时文件就报错；`build-app.bat` 是双击壳 |
| P-⑦ | 一键构建并启动 | ✅ | 本 commit | 仓库根 `build-and-run.ps1` + `build-and-run.bat`。见下 |
| P-⑧ | CLAUDE.md 与代码对齐 | ⏳ | — | 见下 |

## P-⑦ 备注：一键构建并启动

`build-and-run.ps1` **不自己拼 `dotnet publish`**，只编排已有脚本：
构建 → 门禁 →（可选）注册分享入口 → 启动。产物
`windows\artifacts\publish\win-x64\WeChatBridge.Windows.exe` 自包含，双击即开，
本机不必装 .NET。

三个非显然的点：

- **Appx 与门禁都走子进程。** `Get-AppxPackage` / `Add-AppxPackage` 只在 Windows
  PowerShell 5.1 上可用（PS7 加载 Appx 模块直接失败，0x80131539）；`Test-NoNetworkCode.ps1`
  结尾是 `exit 0`，而 `exit` 会把调用它的整个脚本一起带走。收尾**查注册状态**同样走
  子进程——不能靠「这轮有没有加 `-Register`」来推，上一轮注册过的话入口一直都在。
- **构建前先停掉跑在产物目录里的进程。** 微信每次分享都留下一个 ShareTarget 常驻进程
  （keeper），它一直占着 `share-target\` 里的 dll；不先停掉，publish 会以
  MSB3021/MSB3027（"文件被 xxx 锁定"）失败。**只按路径匹配**（只停跑在要覆盖的目录里的
  那些），不按进程名一刀切——用户装在别处的正式版不该被构建脚本关掉。
- **`.bat` 必须纯 ASCII**（cmd 按 ANSI 读它），中文全在 `.ps1` 里，而 `.ps1` 是
  UTF-8 with BOM。

**实测**：构建 393 MB + 195.1 MB 自包含 → 门禁 6 个产品程序集 OK → 打包签名注册 →
`--registration-check` 返回真实 `PackageFullName` 与 AUMID → 启动后窗口标题「微信流」。

顺带把 `go build ./...` 会在 `server/` 下留下的 25 MB 二进制 `server/wcb` 加进 `.gitignore`
——它每次都会重新出现，不挡着就会有人手工提交进去。

## P-⑧ 备注：CLAUDE.md 与代码对齐

CLAUDE.md 此前（2026-10-19 09:39）落后于代码，且有几处**会主动误导人**：

| 原文 | 实际 |
| --- | --- |
| `LicensePublicKeys.PlaceholderKeyV1` 是刻意的占位串，填入公钥是发版阻塞项 | 内容**已是真公钥**；名字是历史遗留（文件自己的注释写着「这名字是历史，不是描述」） |
| `PurchaseWindow.SellerWeChat = "（待填写）"`，收款码是占位 | 收款码/联系人码/卖家微信号**已迁到服务端**（`WCB_PAYMENT_CODE_PATH` 等），渲染在微信回调页；C# 侧已无该字段 |
| 服务端删了 redeem，C# 侧 `Redeem/` 还在 | 客户端清理**已完成**（见 P-③） |
| `GET /v1/admin/requests` 不返回 `licenseId` | 已由服务端返回（§11.7 M-2） |
| `GOTOOLCHAIN=go1.26.9` 不能省 | 那是 Linux 镜像机的绕法；本机 Go 1.26.2，裸跑即绿 |
| 本机 PostgreSQL 章节（apt-get / su postgres） | 这台机器**没有** PostgreSQL，`psql` 不在 PATH。已标注为「Linux 部署机做法」 |
| `export PATH=.../dotnet` | dotnet 10.0.402 已在 PATH |

补上的缺口：**远程拓扑**（`origin` 上没有 `server/`、`admin-console/`、`CLAUDE.md`——服务端
只在 fork `zmuimeng` 上）、**Windows 端产品面**（分批收集 / 场景 / 技能库 / Obsidian 沉淀 /
原生 ZIP 读取 / 6 个面板 / 4 步引导）、**服务端三个新包与 17 条路由**、**六张业务表**、
**canonical 向量的 CRLF 坑**、`site/appcast.xml` 装的是别人的 feed。

## 出口标准

- [x] `go build/vet/test ./...` 全绿（10 个包有测试，4 个包无测试文件）
- [x] `dotnet test` Perpetual 全绿（Core 507 + AppTests 49 = 556）
- [x] `dotnet test` Subscription 全绿（Core 506 + AppTests 49 = 555；少的那一项是
      `EditionIsolationTests` 按版本形态编译掉的断言，不是漏测）
- [x] 买断版网络命名空间门禁通过（6 个产品程序集）
- [x] 卖家后台契约冒烟 28/28（对着 `server/mock/server.js`）
- [x] 一键脚本实测：构建 → 门禁 → 注册 → 身份自检 → 启动
- [ ] 真 PostgreSQL 集成测试（A-⑧，**仍归 `server-convergence` 看板**）

## 阻塞项

- **真 PostgreSQL 集成测试仍未做**（A-⑧）：`store/postgres` 的部分唯一索引并发冲突
  （`activation_requests_live_install_idx` 是 `UNIQUE … WHERE status='pending'`）、条件 UPDATE
  实际影响几行、隐式行序——一行都没被真库跑过。pgxmock 只断言查询字符串，从不执行它。
  本机没有 PostgreSQL，而**主线仍是 PG 版**（SQLite 版在 `feat/sqlite-migration` 待命）。

## 备注

- 本分支与 `zmuimeng/feat/pg-client-integration` **0/0 同步**。
- `origin`（`freestylefly/WeChatBridge`）上**没有** `server/`、`admin-console/` 与
  `CLAUDE.md`——服务端只存在于 fork。推 origin 会因无写权限失败。
- 另一条 worktree `C:/WeChatBridge/.worktrees/client-license-gate` 归 `feat/client-license-gate`，
  与本分支互不影响；它的看板由那边维护，本分支不改。

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## 仓库是什么

一个产品、三个实现，同仓库并行演进：

| 目录 | 技术栈 | 角色 |
| --- | --- | --- |
| `macos/` | SwiftPM（Swift 6 / Swift 5） | macOS 端，主程序 + 12 个 Share Extension |
| `windows/` | .NET 10 + WPF | Windows 端，主程序 + Share Target 助手 + 安装/便携两条分发路线 |
| `server/` | Go 1.26 + gin | 授权服务端（**仅 Windows 版使用；macOS 完全离线，不碰这个服务**） |
| `admin-console/` | 零依赖零构建静态页（UMD + 原生 JS） | 卖家后台。与服务端同源部署，走扫码登录 |
| `shared/` | — | 两端共用的资产与契约，**唯一不允许出现平台特定代码的目录** |
| `site/` | — | 发布物：`appcast.xml`（Sparkle 更新源）+ 逐版本发布说明，由 `pages.yml` 推到 GitHub Pages |

产品分**买断版（Perpetual）**与**订阅版（Subscription）**两种形态，是同一份源码树构建出的两个产品，不是两套代码。当前发布的是买断版免费档。

代码里的注释就是规格。每个决策点几乎都写了「为什么这样、以及不这样会坏在哪」，改动行为时请同步更新注释——其中若干条是承重的（见下）。

## 核心数据流

这是理解整个仓库最重要的一件事，其余细节都是它的分支：

```
微信「合并转发」
   │
   ├─ macOS   Share Extension（沙盒、无网络）─┐
   └─ Windows Share Target（稀疏 MSIX 提供身份）─┤
                                             ▼
                            Inbox/Staging/<batchId>/  ← 校验 ZIP、原子暂存
                                             │  Directory.Move / rename（同卷原子发布）
                                             ▼
                            Inbox/Ready/<batchId>/
                            ├── manifest.json   批次事实（schemaVersion=1, createdAt ISO-8601 UTC Z）
                            ├── intent.json     一次性转发意图，90 秒新鲜度窗口
                            ├── state.json      投递结果回写（delivered/failed/expired/copied）
                            └── files/          原始 ZIP 与媒体
                                             ▼
                            主程序 InboxWatcher → 记录页 + 串行投递队列
                                             ▼
                            激活目标应用 → 写剪贴板 → 聚焦输入框 → ⌘V / Ctrl+V
```

三条贯穿全仓库的规则：

1. **分享侧只负责「收」，不负责「送」。** Share Extension / ShareTarget 只做校验 ZIP → 原子写入 → 剪贴板兜底 → 唤醒主程序。投递一律由主程序串行完成。
2. **剪贴板在投递前先写兜底内容。** 任何失败都必须让用户离一次 Ctrl+V 只有一步（macOS 的 `fallBack(_:)`、.NET 的 `DeliveryEngine.Fail()`）。
3. **intent 一次性：消费即删除。** `ConsumeIntent` 读后即删，所以重扫、重启、第二个窗口都不可能重放一次转发。

## 常用命令

### 这台机器上能跑什么（Linux，2026-10-22 实测）

**只有 Go 服务端与 Node 那一半能在这台机器上跑。** `dotnet` 与 `swift` 都不在 PATH，
所以 Windows 端与 macOS 端的编译/测试**在本机完全跑不了**——那两节命令要在
Windows（装 .NET 10 SDK）或 macOS（装 Xcode）上执行，本机只能读不能跑。

本机可用：Go 1.26.2（`/usr/local/go`）、Node v22.22.1、psql 客户端 14、
Chromium 152（snap）。**PostgreSQL 服务端跑在 127.0.0.1:5433**
（`psql` 只有客户端没有 `postgres` 二进制，别指望能自建实例）。

**`go.mod` 写 `go 1.26`，本机装的就是 1.26.2，所以裸跑 `go build` / `go test` 不会去下载
toolchain**，`GOTOOLCHAIN=auto` 保持默认即可。（`GOTOOLCHAIN=go1.26.9` 那条是**镜像机**
的绕法——发行版装的是 Go 1.22；模块缓存里确实有
`toolchain@v0.0.1-go1.26.9.linux-amd64`，显式指定能命中缓存。）

### macOS（需 macOS + Xcode；本机是 Linux，跑不了）

```bash
cd macos
swift test                                    # 全部测试（XCTest，非 Swift Testing）
swift test --filter BatchStateTests           # 单个测试类/方法，正则匹配
swift Scripts/check-localizations.swift       # 中英文资源四重校验（CI 步骤）
Scripts/check-release-config.sh               # Bundle ID / App Group / entitlements / 12 槽位校验
mise run verify                               # build+test+i18n+release-config+bundle 全套
CONFIG=release Scripts/make-app.sh            # 装配 dist/微信流.app（主程序 + 12 个 appex）
Scripts/install-dev-build.sh                  # 装到 ~/Applications，让扩展真正注册
```

### Windows（需 Windows + .NET 10 SDK；本机是 Linux，跑不了）

```bash
# dotnet 10.0.402 SDK 在 Windows 开发机的 PATH 上，不需要 export。
# Edition 必须同时传给 restore / build / test，只传给 build 会让测试跑在另一个 edition 上
dotnet restore windows/WeChatBridge.Windows.sln -p:WeChatBridgeEdition=Perpetual
dotnet build   windows/WeChatBridge.Windows.sln -c Release --no-restore -p:WeChatBridgeEdition=Perpetual
dotnet test    windows/WeChatBridge.Windows.sln -c Release --no-build -p:WeChatBridgeEdition=Perpetual
dotnet test    windows/WeChatBridge.Windows.sln -c Release -p:WeChatBridgeEdition=Perpetual \
                 --filter "FullyQualifiedName~DeviceAnchorTests"      # 单个测试类

# 买断版网络命名空间门禁（与 CI 同一条，本地提前跑省事）
powershell -ExecutionPolicy Bypass -File windows/scripts/Test-NoNetworkCode.ps1 -Edition Perpetual
```

**出包走 `windows/scripts/build-app.ps1`，不要手拼 `dotnet publish`。** 它从
`Directory.Build.props` 把 `<Version>` 读回来（五个调用点曾各自硬编码，静默漂移），
`ValidateSet` 锁死 `-Configuration Release` / `-Runtime win-x64` / `-Edition`，
主程序与 ShareTarget **两个**工程一起 publish（只出主 exe 是静默失败：分享面板直接不出现），
发布后再断言两个 exe 的 `FileVersion` 与四个运行时文件（`hostfxr.dll` `hostpolicy.dll`
`coreclr.dll` `PresentationFramework.dll`）都在——自包含产物少一个文件就是用户双击后
弹「缺少 .NET 运行时」。默认自包含；`-FrameworkDependent` 是显式选择。
`build-app.bat` 是给人双击的壳，`.ps1` 才是源码真相（CI 直接调 `.ps1`）。

**仓库根的 `build-and-run.ps1` 是给人用的总入口**：构建 → 门禁 →（可选）注册分享入口 → 启动，
产出 `windows\artifacts\publish\win-x64\WeChatBridge.Windows.exe`（自包含，双击即开，本机不必装
.NET）。根目录的 `build-and-run.bat` 是它的双击壳（`.bat` 必须**纯 ASCII**，因为 cmd 按 ANSI
读它；中文全在 `.ps1` 里，而 `.ps1` 是 UTF-8 with BOM）。它不自己拼 `dotnet publish`，只编排已有脚本。三个要点：

- **Appx 与门禁都在独立进程里跑。** `Get-AppxPackage` / `Add-AppxPackage` 只在 Windows
  PowerShell 5.1 上可用（PS7 加载 Appx 模块直接失败，0x80131539）；`Test-NoNetworkCode.ps1`
  结尾是 `exit 0`，而 `exit` 会把调用它的整个脚本一起带走。两者都必须用
  `powershell.exe -File` 走子进程。**收尾时查注册状态也是走子进程**——不能靠「这次有没有加
  `-Register`」来推，上一轮注册过的话入口一直都在。
- **构建前会先停掉跑在产物目录里的进程。** 微信每次分享都留下一个 ShareTarget 常驻进程
  （keeper），它一直占着 `share-target\` 里的 dll；不先停掉，publish 会以
  MSB3021/MSB3027（"文件被 xxx 锁定"）失败。只按**路径**匹配（只停跑在要覆盖的目录里的那些），
  不按进程名一刀切——用户装在别处的正式版不该被构建脚本关掉。
- **`.bat` 在 `NoDefaultCurrentDirectoryInExePath=1` 的机器上不能靠裸名字调用。** 该设置让 cmd
  不再搜当前目录，于是报「不是内部或外部命令」——**文件明明就在眼前**。双击不受影响；
  命令行里要用 `.\build-and-run.bat`。仓库里原有的 `windows/scripts/build-app.bat` 同样如此。

`-Register` 才会把入口注册进微信的「转发到其他应用」；不注册的话 App 能开，但转发菜单里没有它。

### Go 服务端

```bash
cd server
go build ./... && go vet ./... && go test ./...     # 实测 11 个包全绿（14 个包，3 个无测试）
go test ./internal/license/ -run TestVerify -v     # 单包单测试
```

`go test ./...` 全绿**不需要**真实 PostgreSQL、微信凭据或网络——每个外部依赖都在接口后面，测试用 pgxmock / fake store 顶替。**但 pgxmock 只断言查询字符串，从不执行它**：`store/postgres` 里那些真正要紧的语义——部分唯一索引的并发冲突（`activation_requests_live_install_idx` 是 `UNIQUE … WHERE status='pending'`）、条件 UPDATE 守卫实际影响几行、隐式行序——在 pgxmock 下**隐形**（fake store 实现的是 fake 自己写的规则，SQL 改回有 bug 的那一版整包照样绿）。这条缝由真库测试补上，见下节。

**这条缝不是理论上的。** §11.7 M-5 发出去的查询，其全部意义就是「轮询器该等哪一行」，而**把 SQL 改回含 bug 的那一版，整个包依然全绿**——因为测它的是一个 fake，而 fake 实现什么规则就验证什么规则。

**主线仍是 PostgreSQL 版。** SQLite 版（评估文档 `docs/design/sqlite-migration.zh-CN.md`
结论是「可行、范围比预期小」，领域层 182 个业务测试零改动，只重写 store 包的方言）
的成果由 **tag `sqlite-migration-2026-10-19`** 钉住——**分支本身已从远端删除**：
`git branch feat/sqlite-migration sqlite-migration-2026-10-19` 即可取回，
切换手册在 `docs/design/sqlite-switchover.zh-CN.md`。切库是单独一条分支的活。

#### 本机 PostgreSQL（供 A-⑧ 集成测试）

**本机有一个现成的 PostgreSQL 跑在 127.0.0.1:5433**（不是默认的 5432！），库 `wcb`、
角色 `wcb`，凭据在仓库根的 `.env` 里（`.env` 被 gitignore，mode 600）。七张表已由服务端自己
跑完迁移建好（`accounts` / `auth_sessions` / `activation_requests` / `licence_bindings` /
`auth_handoffs` / `account_avatars` + `schema_migrations` 台账）。

```bash
# 真库测试：设了 WCB_TEST_DATABASE_URL 才跑，否则整包 skip（CI 没库，也不受影响）
cd server && WCB_TEST_DATABASE_URL="postgres://wcb:<密码>@127.0.0.1:5433/wcb?sslmode=disable" \
  go test ./internal/store/postgres/ -count=1 -v     # 本机实测 198 项通过 / 0 失败
```

`store/postgres` 里文件名带 `_realdb_test.go` 的测试对着**真实 PostgreSQL** 跑。
三个实现决定（`realdb_harness_test.go`）：

- **用 `migrations.FS` 的真 `.up.sql` 建库，不用手抄 DDL。** 手抄件是 schema 的第二份描述，
  两者不一致那天，继续变绿的是手抄件。
- **每个测试一个独立 schema**（迁移 + 全新表，结束 `DROP SCHEMA CASCADE`），所以
  `t.Parallel()` 是真并行，不靠「清空表」那种让套件对顺序敏感的隔离方式。
- **fixture 常要直插 SQL。** `CreateActivationRequest` 刻意把 `status` 写成 `'pending'`——
  公共方法到不了「已确认但已吊销」这类状态，用 `mustInsertRequest` / `mustInsertBinding`。

**A-⑧ 只覆盖了 `store/postgres` 一层。** `auth` / `activation` / `admin` 打的仍是 fake store，
所以「把 SQL 改回含 bug 的那一版整个包依然全绿」这件事对它们**依然成立**；
`cmd/wcb` 装配层与迁移器本身仍无真库端到端。缺口登记在 `docs/progress/pg-client-integration.md`。

**起服务端**（读仓库根的 `.env`）：

```bash
cd server && set -a && . ../.env && set +a && go run ./cmd/wcb
```

`.env` 里已配好 `WCB_DATABASE_URL`（指 5433）、`WCB_MP_APP_ID` / `WCB_MP_APP_SECRET`、
`WCB_LICENSE_PRIVATE_KEY_PATH`（`/opt/wcb-secrets/license-private.pem`，mode 600）、
`WCB_AVATAR_DIR`（`/opt/wcb-data/avatars`）、`WCB_HTTP_ADDR=:8099`。
**`WCB_ADMIN_OPENIDS` 是空的，所以 admin 路由整个不挂载**（404 而非全信任，见下文）——
本机起服务端时这是预期行为，不是故障。

**⚠️ 本机已经有一个 `wcb-bin` 跑在 8099 上**（PID 见 `ss -ltnp | grep 8099`，
二进制 `/tmp/wcb-bin`，日志 `/tmp/wcb.log`）。**别用 `go run` 去抢这个端口**——会得到
`bind: address already in use`。更不要 `> /tmp/wcb.log` 重定向：**那会截断正在被服务写的日志**
（服务以 `O_APPEND` 打开，所以它会继续写，但历史没了）。要重启就停掉旧进程再起。

监听地址是 `WCB_HTTP_ADDR`（默认 `:8080`），**不是** `WCB_ADDR`。

**常驻占用实测 85 MB**（PG 空闲态，6 个进程 RSS 求和，共享内存被重复计入，实际更低）。在 3.6 GiB / 2 vCPU 的机器上约占 `available` 的 3%，不必为它调 `shared_buffers`。

#### 两台 Linux 机器的 PostgreSQL 别搞混

**这台（`/opt/WeChatBridge` 所在机）** 与 **「Linux 部署机」** 是**两台不同的机器**，
端口、口令、PG 版本、Go 版本全都不同。写文档或跑测试时先确认自己在哪台上：

| | 本机 | Linux 部署机 |
|---|---|---|
| PG | **15.18**，端口 **5433** | 16.15，端口 5432 |
| 库 | `wcb`（服务端自用） | `wcb` + 专用 `wcb_verify` |
| 口令 | `wcb_local_dev_only` | `wcb` |
| Go | **1.26.2**（裸跑即可，不需 `GOTOOLCHAIN`） | **默认 1.22.2**，必须显式 `GOTOOLCHAIN=go1.26.9` |

本机 5432 是**空的**，5433 上跑着的是 Alpine 编译的 PG 15.18——**照抄部署机那条命令
（`127.0.0.1:5432/wcb_verify` + 口令 `wcb`）在本机会连不上**。本机用上面那条
（`:5433/wcb` + `.env` 里的口令）。

**注意真库测试有两条「不存在」的风险**：不设 `WCB_TEST_DATABASE_URL` 时**整包 skip**，
所以「测试绿」可能只是「没跑」——**看 `-v` 输出里有没有 `--- PASS` 行，别只看 `ok` 那一行**。
另外真库覆盖**只对 PG 写**：SQLite 那条线把 store 整体重写了，PG 的 `_realdb_test.go`
在切库时会随 `store/postgres/` 一起消失。

### 契约 mock 与卖家后台（Windows 端的前后端，不在 CI 里）

```bash
node server/mock/server.js --port 8901     # 零依赖 Node，契约 mock；字段/状态码/错误信封与真服务端逐字节一致
cd admin-console && node test/smoke.mjs     # 契约冒烟 34 项（实测全过）；mock 必须在跑
cd admin-console && node test/browser.mjs   # 浏览器级，需 playwright-core + 本机 Chrome（可选）
```

**mock 默认端口 8901 在本机已被别的进程占着**（一个无关的 Python 服务）。换端口即可，
但 `smoke.mjs` 打的是 `config.baseURL`（默认 `http://127.0.0.1:8901`），所以**换端口要同时
告诉它**，否则它会连到占用者上然后报「未找到可用的契约 mock」：

```bash
node server/mock/server.js --port 8917 &
cd admin-console && WCB_TEST_BASE_URL=http://127.0.0.1:8917 node test/smoke.mjs
```

或者让它自己拉起 mock（`WCB_MOCK_SCRIPT` 指向 `server/mock/server.js`）。

`browser.mjs` 在本机跑不了：`playwright-core` 没装（`npm ls` 是空的），而 Chromium 152 在。
它是**可选**的——`smoke.mjs` 覆盖的是契约，`browser.mjs` 覆盖的是 DOM 接线 / 渲染 / 弹窗。

mock 的诚实边界写在 `server/mock/README.md`：签名与 QR PNG 都是占位，任意非空 bearer/admin 密码都接受。真实 handler 已在 `feat/server-convergence` 落地，**mock 该删了但还留着**——它是当前唯一能在无 PostgreSQL 的机器上跑通客户端全流程的手段。

`admin-console/` 的约束比代码更重要：**页面必须与服务端同源**。服务端不带任何 CORS 响应头，`baseURL` 指向别的源时浏览器在预检阶段就拦掉全部请求，页面只得到一句 `Failed to fetch`。改后端地址改 `js/config.js` 的 `baseURL` 一行；**不要为了省事给管理接口开 `*` 通配 CORS**。

## 架构要点

### macOS：12 个入口 = 1 个二进制

`WeChatBridgeCore`（静态库，Swift 6，带 `-application-extension`）← `WeChatBridgeApp`（Swift 5）+ `WeChatBridgeShare`（Swift 5）。Sparkle **故意不链进 appex**（appex 里的 framework 是第二份要签名公证的东西），`check-release-config.sh` 会 grep `Package.swift` 强制这一点。

macOS 的分享菜单只在构建期由已签名的 appex 条目决定，不能按已安装 App 动态生长。所以「十二个入口」是十二个 **appex 包**，但它们是同一个 `WeChatBridgeShare` 二进制的十二份拷贝——`Scripts/make-app.sh` 遍历 `Scripts/share-slots.sh` 里的槽位表，改写 `CFBundleIdentifier` / `DKShareAction` / `DKAppGroupIdentifier`。`check-release-config.sh` 里 `${#SHARE_SLOTS[@]} -ne 12` 直接硬失败。

**`Core/ShareAction.swift` 的 `bundleIdentifierSuffix` 与 `Scripts/share-slots.sh` 的槽位表是同一张表的两个半边，必须一起读。** 检查脚本双向断言：每个槽位对应真实 action，每个 action 都有槽位。

其他几个不读代码看不出来的点：

- **ShareViewController 必须挂一个 1×1 的 `alphaValue = 0` 视图。** 注释里记着实测史：viewless 的 `NSExtensionRequestHandling` 会在字节已拷贝、批次未提交之间 SIGTRAP。
- **主程序故意不用 SwiftUI `App` 生命周期**，走 `AppDelegate`——防止状态恢复复活一个空窗口。
- **主应用故意不进沙盒，扩展必须进沙盒**，两个不对称都被脚本断言。原因：沙盒内任何 `pluginkit` 调用都必然失败（pkd 拒绝沙盒客户端），而「入口」设置页整个建在 pluginkit 上。
- 串行化在 `ActionRunner.enqueueExclusive`（`private var pending: Task?` 链式 await）。等待队列之后**必须重新检查** `arrival.isFresh`，不能只在读盘时查一次。

### Windows 端：产品面远不止「转发一条消息」

Core 里的功能包（`windows/src/WeChatBridge.Windows.Core/`）：

| 包 | 做什么 | 非显然的点 |
| --- | --- | --- |
| `Delivery/` | 激活目标 → 写剪贴板 → 聚焦输入框 → 粘贴 | `DeliveryFailureKind` 是穷举的失败分类（目标不存在 / 进程或 AUMID 没解析出来 / 目标始终没进前台 / 剪贴板被拒 / `SendInput` 短写） |
| `Collections/` | **分批收集**：连续收原始 ZIP，收齐后一次选目标与场景 | 原始 ZIP 永远留在 `Ready`，**不合并也不重写**；集合有 `Collecting/Draft/Delivering/Delivered/Retry` 五态 |
| `Scenes/` | 按群聊绑定提示词与适用 Agent | JSON 与 macOS 的 `Scene.swift` 逐字一致，macOS 导出的场景 Windows 原样能导；`SceneStore` 同时读 macOS 的**2001 纪元秒**与 Windows 的 ISO-8601 两种时间写法 |
| `Skills/` | 应用自管的技能库（`SKILL.md`） | `SkillPackage` 的 SHA-256 摘要与 macOS `SkillInstaller` **逐字节相同**；库状态有 `Diverged`，**分叉的库永不覆盖** |
| `Obsidian/` | 沉淀成 Markdown 笔记 / 写进文件夹 | 写进 Obsidian 时**同时留一份原始 ZIP 的持久副本**，将来解析器改了还能重建笔记，不必再问微信要 |
| `Archive/` | 微信原生 ZIP 的内存读取 | 不落盘、不起辅助进程；ZIP64 / 加密 / 未知压缩**一律 fail closed**；TXT 条目与展开后总体积都有上限 |
| `Identity/` | 群身份 | 按**发送者集合**认群，不按标题——「改过名的群还是那群人：标题栏里那个名字是标签，发送者集合才是身份」 |
| `LocalChatImporter.cs`（Core 根目录） | 用户选的文件 / 粘贴的文本 | 走**同一条** Inbox 通道，但不激活打包也不碰剪贴板 |

**分享路径永远不许把自家窗口顶到前台。** 投递引擎正在把目标 App 顶到前台，自己再抢一次焦点
就把 Ctrl+V 抢没了——所以 `--background` 启动的实例从不 `window.Show()`，且
`ForegroundEventName` 与 `ChangeEventName` 是**两个**命名事件。隐藏窗口不触发 `Loaded`，
后台实例必须**显式加载**（漏过一次，2026-09-25 实测两批分享永远送不出去）。

**界面是 6 个面板**（`Panes/`，由 `AppTab` 枚举驱动）：记录 / 通用 / 入口 / 场景 / 技能中心 / 关于。
**引导是 4 步**（`Onboarding/`），**便携版直接跳过引导**——引导会叫未注册的用户重装、
给 removable drive 兑现不了的「开机自启」选项，并列出十二个在分享菜单注册前毫无意义的入口。

### Windows：Core 是零依赖，两个应用都只能单向依赖它

```
WeChatBridge.Windows ──► Core ──► (仅 BCL)
WeChatBridge.ShareTarget ──► Core
                     └────► (Link 主项目的 11 个源文件，无 ProjectReference)
```

`WeChatBridge.Windows.Core` 是 `net10.0`（**不是** `-windows`）、无 WPF、**零 NuGet 引用**——这个保证是显式的，由测试守着。

ShareTarget 从主程序 Link 了 11 个文件（XAML 选入口浮层等），让同一个面板在助手宿主里长得一模一样。代价：AppTests 对两个项目引用都设 `ReferenceOutputAssembly="false"`（否则 CS0433 类型二义），改用 `CopyShareTargetForProcessTests` 复制 exe 跑进程级测试。

**分享注册**：稀疏 MSIX 只提供身份、程序在包外（`uap10:AllowExternalContent`）。`AppxManifest.xml` 的 `DisplayName` 里**绝不能出现连续的「微信 / WeChat / Weixin」**——微信会直接隐藏这类目标，所以全仓库写的是微`<U+2060>`信流。

**keeper / stub 选举**：Windows 每次分享都起一个新助手进程（不上 WindowsAppSDK 无法避免）。`StubForwarder` 先用 400ms 超时试连命名管道，失败就自己当 keeper 常驻；成功则 stub 转发后立刻退出，warm keeper 负责浮层与提交。`--registration-check` 在 `Program.Main` 最前面处理完就退出——它 P/Invoke `GetCurrentPackageFullName` 证伪「注册记录 ≠ 外部 exe 真的有身份」。

### 版本形态：`WeChatBridgeEdition` 是 MSBuild 属性

定义在 `windows/Directory.Build.props`。**不是常量、不是 item**，这样 `build-installer.ps1` 才能按条件反查对应的 `<Version>`。

四个载体（Version / 编译常量 / 产物名 / MSI 包名）在同一个条件 `PropertyGroup` 里，改一个不改其余的会串。`ValidateWeChatBridgeEdition` 对任何别的取值报 **WCB0001**（拼错会静默地用 Perpetual 的源码构建出 Subscription 的名字）。

源码选择在 `Directory.Build.targets` 里用 `Compile Remove="Cloud\**\*.cs"` 等实现——必须是 `.targets` 的 Remove 而不是 `.props` 的条件 Include，因为 `.targets` 在项目体之后、`Sdk.targets` 之前导入，是 Remove 还能作用于 SDK 隐式 glob 的唯一时点。

`WcbEditionStampIsACompileInput` 存在的理由：不这么做，紧跟着构建 Subscription 会把 Perpetual 的程序集留在 `bin` 里戴着新名字，同时静默破坏版本区分和上面的 Remove。

### 服务端：许可证是唯一的信任根

```
auth/      微信扫码登录（redirect 落在服务端，桌面进程永远拿不到 code）
           + handoff.go：微信回调（实例 A 铸令牌）与客户端轮询（实例 B 取令牌）之间的
             一次性桥。Take 是 read-once（第二次返回 false），明文令牌只存在于这张
             过桥行里（store 只存哈希），取出即删。**进程内 handoff 对多进程不正确**，
             所以生产走 StoreHandoff——这条同时是「服务架构上禁止多实例」的根据。
           + payment.go：卖家收款码 / 卖家微信号 / 联系人码，渲染成微信回调页。
             **图片有问题绝不报错**（LoadPaymentCode 永远成功，坏图降级成「未配置」并
             记一条 Problem）——为了缺一张 png 就让所有人登不了录是本末倒置。
             坏图重载保留上一张好图；**页面绝不能自动刷新**（回调 URL 是一次性的，
             reload 会把收款码换成错误页，买家就永远拿不到机器码）。
           + 头像：决策逻辑在 service.go（refreshAvatar / avatarIsStale / Avatar），
             字节缓存是独立的 avatar/ 包。
token/     access = HS256 JWT；refresh = 不透明 wcb1_ 串（自包含的 refresh 只能靠「不认」作废）
license/   ECDSA P-256 签发，客户端只嵌 SPKI 公钥
activation/ 买断版客户侧三端点：登记 → 轮询 → 取票（无支付网关）
admin/     卖家控制台（Bearer + OpenID 白名单）：列表 / 确认 / 拒绝 / 重签 / 作废
avatar/    微信头像的内容寻址本地缓存。**只按魔数嗅探类型，不看 Content-Type**
           （说 image/png 却吐 HTML 的是门户劫持）；只走 https 且只允许微信域名
           （点锚定后缀，不是子串）；文件名 = 字节的 SHA-256，所以摘要即 ETag。
           「头像是租约不是地址」——微信的 headimgurl 几天就过期。
wechat/    微信 API 客户端。client.go = 网站应用（换 token / 取资料），
           mp.go = **公众号**客户端（unionid 桥接用），qr.go = 二维码 PNG。
           **mp.go 的 access_token 是单飞刷新**（一个应用只持有一个有效 token，
           并发换 token 会让除最后一个外全部当场失效）+ 提前 5 分钟续期 +
           **日预算 2000 次本地拦下**（微信的公布上限，超了 token 被吊销且无从诊断）。
           mp_live_test.go 是 env 门控的真机测试（`WCB_MP_APP_ID` / `WCB_MP_APP_SECRET`），
           CI 不跑——它花的是真账号的日预算。
httpx/     错误信封与 request-id 中间件（**`{error:{code,message,request_id}}`**
           + `X-Request-Id` 头），全仓库的错误形状都从这里出。
config/    全部配置来自环境变量，**没有 flag**。每个字段都有「本地能跑、线上明显不对」
           的默认值，所以半配置部署在启动时就炸，而不是在第一个客户身上。
cmd/wcb/   唯一入口：装配、迁移执行、优雅关闭
store/     接口在 store.go，实现在 store/postgres
```

`billing/`（微信支付 Native）与 `redeem/`（兑换码）已随兑换码退役删除（`86abca3 [A-③]`）。**没有支付网关**：买家自己转账，卖家人工核对微信零账后在后台一键发证。

**路由一共 17 个**，`/healthz` 之外的都挂在 `cfg.BasePath`（`WCB_BASE_PATH`，如 `/wechat_auth`）
下。`/v1/admin/*` 在 `WCB_ADMIN_OPENIDS` 为空时**整个不挂载**（404 而非全信任）。
`GET /v1/accounts/:id/avatar` **刻意不鉴权**——WPF 的 `Image` 控件发不出 `Authorization` 头；
安全性靠「路径是 UUID（无从枚举）+ 身体本来就是公开的微信头像 + 不带任何账号信息」三条撑着。
没有 `WriteTimeout`（写到一半超时的许可证是坏许可证）。

#### 微信的三套凭据不可混用（最容易静默出错的一处）

微信开放平台有**三个不同的应用**：`WCB_WECHAT_APP_ID`（**网站应用**，扫码登录用）、
`WCB_MP_APP_ID`（**公众号**，unionid 桥接用）、以及小程序 appid。**三者 appid 各不相同。**

`WCB_MP_APP_ID` / `WCB_MP_APP_SECRET` 是**成对**的：只给一个会在启动时被拒（这半种配置根本
不可能工作）。**它们还是可选的**——桥接是增强不是命脉，没配这对凭据的部署照样能登录，
只是没有昵称头像。**强制必填会让一个能用的服务因为一个非必需功能起不来。**

`config.validate` 里有一条**防填反**校验：`WCB_MP_APP_ID` **不得等于**网站应用的 AppID。
理由是那种错法**在任何其他层都是静默的**——服务起得来、登录正常、然后拿错了应用去问昵称，
对方给出一个完全合法但属于另一个账号的答案。

**主路径已经不需要桥接了。** 开放平台用 `sns_login` 登录时，**换 token 的响应里就带
nickname / headimgurl**（2026-10-20 用户提供的实测事实）。代码原先**根本没解析这两个字段**
——微信给了也丢掉。现在解析了，且 `HandleCallback` 里**session 是第一来源**：
`FetchProfile` 的调用**只能改进、不能抹掉**（一个答不出东西的调用不许擦掉登录时已经拿到的）。
`scope` 决定的是 `/sns/userinfo` 答不答，**不决定换 token 时已经说了什么**。

**日志里不打人名。** 回调打的是 `token_nickname` / `token_avatar` 这类布尔值——
昵称是一个人的名字，属于 `accounts` 表那一行，不属于日志。

**表已经不止 4 张。** `0001_init.up.sql` 建 4 张（`accounts` / `auth_sessions` /
`activation_requests` / `licence_bindings`），之后又加了三处迁移：
`0002_auth_handoff`（`auth_handoffs`）、`0003_activation_rejected`（`activation_requests.rejected_at`）、
`0004_account_avatar`（`account_avatars`）——**六张业务表 + 一张 `schema_migrations` 台账**。
台账表由迁移器自己建，每个 `.up.sql` 与它的台账行在**同一个事务**里，崩溃留下的是
「没应用」而不是「应用了一半」。`envelope_json` 是 `bytea` 不是 `jsonb`——jsonb 会重排键，
键序一变签名就对不上。

### 客户端侧：Activation 三件套（`windows/`）

`Core/Activation/`（AccountService / ActivationService / PollSchedule / GateState）+ `Windows/Activation/`（LoginWindow / PurchaseWindow / 两个 Controller / `HttpActivationTransport`）。注意 `HttpActivationTransport` 在 **WPF 工程**里而不在 Core——Core 的 `System.Net.*` 白名单只有三条 `NetworkInformation`，放一个 `HttpClient` 进去 `EditionIsolationTests` 当场红。

启动门禁是 `ActivationGate.Decide` 这个**纯函数**：`Licensed`/`Portable` 不开页，`Anonymous` 开登录页，`SignedIn` 直接接购买页，`Rejected`/`LicenseUnverifiable` 提示联系卖家。

**门禁不锁任何功能。** 转发对所有人可用，`LicensingBootstrap.IsShareDeliveryAvailable` 恒为 true 并有测试守着；全仓库没有一处 UI 因 `IsPro` 为 false 而禁用。这是产品决策（买断版卖的是能力不是锁），改之前先看 `GateState.SignedIn` 的文档注释。`ActivationGateway` 全部惰性装配——已授权用户每次启动**连 `HttpClient` 都不构造**，因为隐私承诺写的是「0 次网络请求」。

**签名从不是对 JSON 文本签的**，而是对规范化后的 8 行字符串签（域分隔符 `wechatbridge.license.v1`，空段用 `-` 占位，**无尾随换行**）。ent 排序去重，因为 map 迭代或没写 ORDER BY 的 SELECT 每次返回顺序都不同。

**跨语言的东西一律是复制而非共享**：许可证规范化向量 + SHA-256、时间戳格式、wire 枚举字符串，两端各有一份并各有一对测试。`go test ./...` 全绿**不代表**客户端还验得过——唯一无法被编译器检查的跨语言契约就是这个 canonical 格式，所以两端各自钉死了同一个常量与同一个摘要。

（兑换码归一化表随兑换码退役一起没了，客户端的清理也已完成——`windows/src` 下已经没有
`Redeem/` 目录，`en.json` 里没有兑换文案，`--redeem` 参数也没了。）

**canonical 向量有一个共享文件，但两端读它的方式故意不同。** `shared/License/license-test-vectors.json`
由 `server/cmd/license-vectors` 生成，**Go 侧直接读**（`server/internal/license/vectors_test.go`）；
**C# 侧不读文件**，把向量逐字节写在 `LicensingCanonicalPayloadTests.cs` 里。原因是文件头那句注释：
*「一个由被测实现生成的向量什么都证明不了」*——所以 C# 那边必须是手写的字面量。
这带来一个必须知道的坑：**仓库没有 `.gitattributes`，`core.autocrlf` 是开的**，Windows 上
checkout 出来的字面量带 `\r\n`，于是 `ReplaceLineEndings("\n")` 不是装饰而是必需——不发它，
签名照样完全有效、字段照样全解析得出，只是客户端永远不同意服务端。

**签名是 64 字节的 raw `r‖s`，不是 DER。** `internal/license/crypto.go` 的 `signRaw` 手工 `FillBytes` 拼两个 32 字节再 base64；`verifyRaw` 的注释写明它自己做 malleability 检查。任何用 `openssl dgst -verify` 之类的通用工具去验这张许可证都会得到 `Error verifying data`——那条命令要 DER（P-256 通常 70–72 字节）。手工验证时先把 raw 转成 `SEQUENCE{INTEGER r, INTEGER s}` 再验，否则会误判成事故。

顺带记下 canonical 的确切形状（`license.go:115`），每行都带 `key=` 前缀，漏掉就会算出不同的字节：

```
wechatbridge.license.v1
v=1
sub=…
plan=…
iat=…
nbf=…
exp=-          ← 空段占位 Absent
ent=PRO        ← 大写、排序、去重后逗号连接
```

无尾随换行。客户端 `IsPro` 恒为 true 的前提是这三件事同时成立：内嵌公钥与签发私钥配对、canonical 逐字节一致、raw 签名被正确拆回 `r‖s`。

## 不看代码看不出来的约定（红线）

**用户数据只有三个根，其余全部由 `AppLayout` 派生。** 新增落盘点必须走它，否则便携版会把一部分状态写到 U 盘、一部分写到本机——这在拔盘之前完全看不出来。`LayoutWiringTests.NoSourceFileResolvesLocalAppDataOutsideTheAllowlist` 守着这条。

**Core 不得命名任何网络类型。** `EditionIsolationTests` 用 `System.Reflection.Metadata` 读编译产物，按**精确集合相等**断言 Core 的 `System.Net.*` TypeRef 只有三条 `NetworkInformation`（离线恢复用的 MAC 锚点）。`Test-NoNetworkCode.ps1` 的白名单已含 `System.Net.Http` / `Http.Headers` / `Primitives`，所以 HTTP 动词放 WPF 工程即可，两个白名单都不用动。脚本里写着「若要加第四条，先停下来」——那是产品决策，不是测试修复。

**本地的中文字符串 .ps1 必须是 UTF-8 with BOM。** PowerShell 5.1 会把无 BOM 的文件按系统 ANSI 代码页（中文 Windows 上是 GBK）读，静默毁掉字符串而不报错。反过来 `.bat` 必须纯 ASCII——cmd 同样按 ANSI 读。`Directory.Build.props` 也带中文，同理。`LayoutWiringTests` 会守住 `FirstRun.ps1` 的头三个字节。

**本地化是单向的。** `L10n.Text("中文")` 的 key 就是中文字符串本身（miss 时原样返回，所以每个用户可见中文字符串都是自己的翻译 key），英文只存在于 `Core/Localization/en.json`（72KB，EmbeddedResource）。XAML 侧用 `{loc:Loc Key='中文'}`。加新文案 = 改代码里的中文 + 在 en.json 补一条，`LocalizationTests` 会断言两边的复合格式占位符索引集合完全一致。

**`tests/…/TempInbox.cs` 是绝不能碰真实 `%LOCALAPPDATA%` 的 fixture。** 所有测试经由它拿一个临时 Inbox 根。

**函数内部的顺序就是正确性论证。** `auth.Service.HandleCallback` 是「先落凭据再完成 ticket」。admin 的三条更细：**envelope 每张许可证只签一次**（ECDSA 每次签随机取 k，重签同一份 claims 会让买家每次轮询拿到不同的 token，store 写一次字节，买家的 `/licence` 端点只回放那份字节）；**confirm 按 requestId 幂等**（两个卖家同时点一次，只铸一张票，第二次读回第一张）；**卖家的身份只来自 token，永不来自 body**。每处都有注释说明，不要「顺手简化」。

**错误码的严格程度不统一是有意的。** `admin` 比它写的时候所对的 mock 更严：未知的 `?status=` 是 400 而不是空列表——卖家把 "confirmed" 打成 "comfirmed" 时被告知「没有待确认的东西」，是这个 API 最不能给出的答案。`auth` 的 state 无效一律 400 且**故意不说是伪造、已用还是过期**，告诉攻击者是哪一种就等于送了他一个探针。上游（微信）不可用是 503 `service_unavailable` 而**不是**「凭据已过期」——ticket 原封不动、浏览器拿到可重试的答案；一次网络抖动把付费用户登出是这个端点能有的最坏故障。

**admin 的 403 不是 401。** 鉴权走买家同一套微信扫码 bearer token，`WCB_ADMIN_OPENIDS` 白名单决定谁在后面。token 验得过但账号不在名单上是 403：凭据没问题，背后的身份不是卖家。空白名单是接线错误，`NewService` 直接拒绝——`cmd/wcb` 干脆不挂载 admin 路由（404 而非全信任）。

**卖家有四个动词，不是两个。** `confirm` / `reject` / `reissue` / `revoke`。`reject`（§11.7 M-4）
是后补的：原先卖家只能说「是」和「收」，不能说「不」——买家的登记会永远卡在 pending。

## CI 门禁的排序是有意的

`.github/workflows/ci.yml` 的 Windows job 跑 `Perpetual | Subscription` 双矩阵且 `fail-fast: false`（一个腿红不能取消另一个，否则 Subscription 的日志会因 Perpetual 失败而消失——而这恰好是这个矩阵存在的意义）。

**`Test-NoNetworkCode.ps1` 必须排在所有签名/发布步骤之前。** 签名是对一个持有密钥的服务的请求，不是对产物的批处理：一旦买断版二进制上了签名服务，事后再发现版本形态标错就不是重建，而是吊销——签名过的产物已经分发，用户机器上的 Authenticode 签名里已经烙死了版本字符串。这条线以下新增的任何东西都属于「发布候选」，需要先确认吊销预案成立。

**当前 CI 不测 Go 服务端，也不测 `admin-console`**（`ci.yml` 只有 macOS 与 Windows 两个 job）。服务端的全绿、后台的冒烟全绿都只在你本地成立。另有两条不在 `ci.yml` 里的 workflow：`windows-sign.yml`（手动 dispatch，走 SignPath）与 `pages.yml`（`site/**` 推到 GitHub Pages，即 macOS 的 Sparkle 更新源）。

## 并行开发协议

**早期那轮已经合完了**（`487231c` server、`0762a85` admin，`feat/client-license-gate` 与 `feat/windows-portable-usb` 也都已在 `main` 的祖先里）。协议本身仍是下一次并行开工时的规矩，规则见 `docs/design/progress-sync-protocol.zh-CN.md` 与 `branch-work-assignment.zh-CN.md`：

- **一个分支同一时刻只允许一台机器写。** 交接只能先确认对方 24 小时内不会回来。
  **⚠️ 这条在本机被常态违反**——`/opt/WeChatBridge` 长期有多个独立 Claude 会话同时跑，
  动 `main` 前先查：`for p in $(pgrep -x claude); do printf "%s %s\n" "$p" "$(readlink /proc/$p/cwd)"; done`
  外加 `git log --oneline --since='30 minutes ago'`（有你没做过的提交 = 有人也在写）。
  2026-10-20 实测发生过：另一个会话把分支合进 `main`，`main` 在对话期间凭空前进。
- **看板路径按分支隔离**：`docs/progress/<分支名>.md`，只有各自主人写，git 层面零冲突。**看板与代码同一个 commit**——「先 push 代码回头补看板」，补的那次永远不来。
- **commit 带任务编号**：`feat(server): [A-③] …`，`git log --oneline` 即进度流水。子任务粒度，不攒大 commit。
- **契约冻结**：license canonical 格式、auth 端点、httpx 错误码体系禁止私改。发现缺字段 → 先改 `docs/design/buyout-flow-convergence.zh-CN.md` §11.3 并推送，各分支跟进。§11.7 是缺陷与变更登记册。
- **工作区用 git worktree 隔离**（历史上真出过未跟踪文件跨分支的事故）。stash 栈在主检出与所有 worktree 间共享，`git stash` / `git stash pop` 会和其他会话抢——用 WIP commit，或 `git stash push -u -m "<唯一标签>"` 后立刻记下 SHA、用 `apply <sha>` 不用 `pop`。
- 沙箱类环境里 git 网络操作会**静默失败或超时**（特征是 `Empty reply from server`，同时 curl 正常——别误判成凭据问题），必须在沙箱外跑。

看板本身就是进度源，别只看文档：`docs/progress/*.md` 里 ⬜ / ⏳ 的行是**代码尚未落地**。
最新的一块工作是 **`unionid-bridge`**（看板 `docs/progress/unionid-bridge.md`；分支已归档，
成果在 tag `archive/feat-unionid-bridge`，但 U-⓪ 与 U-③ 已经合进 `main`）：U-⓪ 与 U-③ 已落地，
**U-④（unionid→openid 那一跳）卡在接口未派发**——`cgi-bin/user/getid` 这个路径**根本不存在**，
而主路径（换 token 的响应自带昵称头像）一旦在真机成立，桥接就不必做了。
**下一步是一次真机扫码看日志里的 `token_nickname`**，不是写代码。

## 已知缺口（别当成新 bug 去排查）

- **`origin` 是 fork `zhuimeng2026-hub/WeChatBridge`，`upstream` 才是 `freestylefly/WeChatBridge`。**
  **方向别记反了**（本文件 2026-10-22 之前记反了）：`origin/main` 上**有** `server/` `admin-console/`
  `CLAUDE.md`；`upstream/main` 上**没有**——上游只同步 `macos/` `windows/` `shared/` `site/` `docs/`。
  所以「推 origin 会因为没有写权限而失败」是**旧**说法：现在推 origin 推到的是 fork，推得上去。
  **`upstream` 必须长期保留**：它是与 freestylefly 对比的唯一持久锚点（`git fetch upstream &&
  git rev-list --left-right --count main...upstream/main`），删掉就只能重新 `ls-remote`。
  清理 remote 时只处理 `origin`。当前 `main` 领先 `upstream/main` 137 个提交。
- **`site/appcast.xml` 现在装的是「Dukou」这个别的产品的 feed。** 里面零条 WeChatBridge 记录，
  每一条都指向 `github.com/qzz0518/Dukou` 的 dmg，签名也是用我们不持有的密钥签的。
  `site/WeChatBridge-0.1.13.md`～`0.1.15.md` 的发布说明在，但**不在 feed 里**。
  换句话说，macOS 的 Sparkle 更新源目前是断的——`pages.yml` 照发不误，发的是别人的更新源。
- **当前分支是 `main`，不是 `feat/pg-client-integration`。** 看板目录现在有**八块**
  （`server-convergence` / `admin-console` / `client-license-gate` / `client-license-gate.resume` /
  `pg-client-integration` / `unionid-bridge` / `delivery-quota` / `login-perf-and-ux`），
  所以「docs/progress/ 下没有当前分支的看板」这句已过期。
  **但看板多不等于分支多**：2026-10-22 做过一轮分支清理，`feat/client-license-gate`、
  `feat/delivery-quota`、`feat/pg-client-integration`、`feat/unionid-bridge` 都已归档，
  成果由 tag `archive/<分支名>` 钉住。**这些分支对应的看板从此是历史记录，不再是活的进度源**——
  只有还在远端存在的分支（`main` 与 `feat/pg-client-integration`）的看板是活的。
  远端分支清单：`git branch -r | grep origin/`。
  **`delivery-quota` 那块会推翻一条已登记的产品决策**（「门禁不锁任何功能」），
  但它卡在 Q-① 六条口径未拍板上——**在拍板之前，那条决策仍然有效**。
- **`windows/src` 下不存在 `Cloud/`、`CloudQr/`、`Billing/` 目录。** 版本隔离机制全部建好并被 CI 守着，但订阅形态的源码还没有落地，所以 `Test-NoNetworkCode.ps1` 在 Subscription 下会打印 SKIPPED。
- **`LicensePublicKeys.PlaceholderKeyV1` 这个名字是历史遗留，内容已经是真公钥。**
  2026-10-19 起编译进客户端的就是服务端现役签发私钥的另一半（私钥在服务端
  `/etc/wechatbridge/license-ec.pk8.pem`，PKCS#8 / mode 600），端到端验签实测通过。
  **名字里的 "Placeholder" 不描述内容**——文件自己的注释写着「改名被刻意留在换值那次之外，
  因为文件当时要交给另一个开发者，改名会多一次 diff；这名字是历史，不是描述」。
  看到 "Placeholder" 就当成占位串跳过，是这个仓库里最容易犯的误判。
- **收款码与卖家微信号现在在服务端，不在客户端。** `C#` 侧已经没有 `SellerWeChat` 这个字段了；
  收款码 / 联系人码 / 卖家微信号分别由 `WCB_PAYMENT_CODE_PATH` / `WCB_CONTACT_QR_PATH` /
  `WCB_SELLER_WECHAT` 提供，渲染在微信回调页上。客户端那张损坏的占位收款图已删除。
  **金额不作为页面文本重述**——金额活在微信固定金额收款码的图像里，重述一遍就是第二份会漂移的事实源。
- **兑换码的客户端清理已完成。** `windows/src` 下没有 `Redeem/` 目录，`en.json` 里没有兑换文案，
  `App.xaml.cs` 的 `--redeem` 参数没有了。服务端 `POST /v1/license/redeem` 早已退役。
  只剩 `ActivationContract.cs` 的注释里还提了一句 `RedeemEndpoint.BaseAddress`——那是一句悬空的
  注释（它引用的类型已经不在了），不是活代码。
- **`BuyoutProductID` 与 `httpx.CodeWrongProduct` 目前没有任何消费方**：字段被读进 `Config` 就没人用，错误码定义了也没有处理器返回。两者都是兑换码时代的残留，产品校验随兑换码一起退役了。默认值是 `wechatbridge-windows-buyout`，**但今天既没有服务端校验它，也没有客户端拿它比对**——别以为存在这条校验路径。
  同理，`httpx` 里还留着 `CodeCodeRedeemed` / `CodeCodeExpired` / `CodeCodeRevoked` /
  `CodeInstallMismatch` 四个兑换码时代的错误码，**同样没有任何处理器会返回它们**。
  「错误码定义了」不等于「这条错误路径存在」。
- **Go 与 C# 的许可证信封仍有一处不一致**：base64（Go 用 std）对 base64url（C# `Base64Url`）。目前靠 C# 解码器的宽松映射兜住，实测可 roundtrip；一旦有人给 C# 加严格字母表校验就会当场炸。时间戳那处（int64 vs `DateTimeOffset`）已由 `b822761` 修复。成因与排查路径见 `windows/docs/LICENCE-ENVELOPE-WIRE-CONTRACT.zh-CN.md`。
- **`server/mock/server.js` 该删了但还留着**（`server/mock/README.md` 自己写着「真实 handler 落地后本文件删除」）。它现在是有用的——**本机有真库，所以它的独占领地只剩「无库时跑通客户端全流程」这一条**；但它的签名是占位，envelope 过不了客户端内嵌公钥。**它跟真服务端逐字节一致的部分是路由、字段名、状态码、错误信封**；签名与二维码 PNG 是占位。
- **`admin-console` 的台账页许可证号列现在由服务端返回**（`GET /v1/admin/requests` 的 item 带
  `licenseId`，设计文档 §11.7 M-2 补齐）。本机 localStorage 日志降级成兜底，不是唯一来源。
  卖家还有一个此前没有的路径：`POST /v1/admin/reject`（§11.7 M-4）——原先卖家只能说「是」和「收」，
  不能说「不」。
- **`Tests/FolderDeliveryTests.swift` 的 11 个测试不在任何 target 里**，`swift test` 从不编译它们。
- **`App.swift` / `ShareEntryProbe.swift` / `AppLink.swift` / `ShareFailure.swift` 引用的 ADR-0005 / ADR-0006 在仓库里不存在。**
- **`ci.yml` 只在 `[main, feat/windows-port]` 上触发。** 当前分支就是 `main`，所以**推上去会跑**；
  但 CI **只有 macOS 与 Windows 两个 job，不测 Go 服务端，也不测 `admin-console`**——
  服务端的全绿、后台的冒烟全绿都只在你本地成立。
- **macOS 端自 2026-10-04 起基本停更**（`git log -- macos/` 的最后一条），而 `server/` 与
  `windows/` 每天都在动。macOS 的命令在本机（Linux）上**完全跑不了**——需要 macOS + Xcode。
  macOS 侧现在主要是 CI 里的那个 job 还在守着。

# 进度看板：feat/login-perf-and-ux（worktree `C:\WeChatBridge\.worktrees\login-perf-and-ux`）

> **归档（2026-10-23）。** 分支 `feat/login-perf-and-ux` 已不存在，它记录的 worktree
> `C:\WeChatBridge\.worktrees\login-perf-and-ux` 也不存在了。
> 其成果**已在 `main` 上**（登录链路轮询加速与 UX 改善，见 `87c2f92`）。
> 本看板从此是历史记录，不是活的进度源。



> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2。
> 本分支 2026-10-19 19:14 自 `feat/pg-client-integration` HEAD=`386f3f0` 拉出。

## 这条分支是什么

**客户端登录链路优化（轮询节奏 + UX 改善）。** 服务端不动、协议不动、登录状态机不动。
针对「我已付款 → 未登录 → LoginWindow → 打开系统浏览器 → 跳 `open.weixin.qq.com`」的体验断裂点，
在「微信开放平台必须用 PC 浏览器」这条硬约束下，做两件事让"扫码确认→桌面感知"更短更顺。

代码域：`windows/src/WeChatBridge.Windows.Core/Activation/PollSchedule.cs` +
`windows/src/WeChatBridge.Windows/Activation/LoginWindow.{xaml,xaml.cs}` +
对应测试。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| L-① | `PollSchedule.Login` 节奏调激进 | ✅ | 本 commit | `1.5s/10s/×1.5` → `500ms/3s/×1.5`。扫码确认→桌面"完成"按钮亮起的延迟上限从 10s 降到 3s。`PollSchedule.Activation` 不动（5s/10s/×1.4）——两个字段是独立 static 实例，无交叉影响 |
| L-② | `LoginWindow` 复制链接按钮 | ✅ | 本 commit | XAML 新增 `CopyButton`，`OnCopyClicked` 调 `Clipboard.SetText(url)`，复制成功覆盖 `StatusText="已复制到剪贴板"`，失败时 `Debug.WriteLine` + 友好提示"复制失败，请手动选中链接"。**XAML 缩进保持原"行首"风格**——避免 diff 因缩进而膨胀 |
| L-③ | 点链接后自动最小化 + 完成时自动还原 | ✅ | 本 commit | `OnAuthorizeClicked` 在 `Process.Start` 之后 `WindowState = Minimized`（`catch` 块**不**最小化）；`Render()` 在 `Stage==Done` 且 `WindowState==Minimized` 时还原为 `Normal`+`Activate()`。**双重护栏**：`catch` 不最小化（让用户看见错误可点 Copy）；`WindowState==Minimized` 守卫避免抢焦点用户手动最小化的窗口 |
| L-④ | `PollScheduleTests` 旧值断言同步 | ✅ | 本 commit | 3 个配置型测试（`TheFirstAttemptWaitsExactlyTheFirstDelay` / `TheLoginScheduleGrowsUntilTheCeilingAndStopsThere` / `AttemptsWithinCountsTheGrowingWaitsItWouldActuallySpend`）的数值断言 + 注释里"1.5s, 2.25s, 3.375s, 5.0625s, 7.59375s, then ten seconds forever"全部更新到新节奏 |

## 验证

- `dotnet build` ：0 警告 0 错误（.NET 10.0.402，net10.0-windows10.0.19041.0）
- `dotnet test windows/tests/WeChatBridge.Windows.Tests` ：**507 / 507 通过**
- `git diff --stat feat/pg-client-integration` ：4 文件 74+/21-

## 后续

- **不合并到 `feat/pg-client-integration` 前**需要：
  1. 用户在真实微信扫码流程里验证：
     - 点链接后是否真的"跳出 + 自动回来"（点链接 → 浏览器被拉起 → 扫码 → 确认 → 桌面窗口自动还原 + Finish 按钮可见）
     - 复制按钮在什么场景下被实际使用（默认浏览器被劫持 / 浏览器进程异常退出等）
  2. 确认文案「已复制到剪贴板」「复制失败，请手动选中链接」通过 i18n 流程（en.json / zh.json 同步）
     - ✅ **已补（合入 main 时）**：本分支新增的三条用户可见文案已进 `en.json`——
       `复制链接` / `把登录链接复制到剪贴板` / `复制失败，请手动选中链接`。
       合并前全仓扫描过：`L10n.Text` 与 XAML `loc:Loc Key` 共 39 条 key 未收录
       （全在 Onboarding / TrayIcon / AboutPane 等处，属既有问题），本分支只贡献
       其中 3 条。`LocalizationTests` 只遍历 `en.json` 里的条目校验占位符，
       **不检查 XAML key 是否收录**，所以漏补不会让测试变红，只会让英文界面
       显示中文——这类洞靠测试发现不了。
- **L-③ 自动还原窗口的副作用**：`Activate()` 会抢焦点。如果用户登录时在做别的事（多屏场景下另一个屏幕在输入），登录完成的瞬间焦点会被劫回。**待用户反馈再决定是否去掉 `Activate()` 调用**——只 `WindowState = Normal` 不抢焦点。
- **L-③ 的另一个副作用（原看板记漏了，合入时补记）**：`WindowState==Minimized` 守卫
  区分不了「我们自己最小化以便让开浏览器」与「用户手动最小化」。所以**用户手动
  最小化的窗口在 Done 时同样会被拽回 Normal 并 `Activate()`**——恰好是用户明确
  表示不想被打断的那一种。原看板把该守卫记成「避免抢焦点用户手动最小化的窗口」，
  与代码实际行为不符。合入时已把 `LoginWindow.xaml.cs` 的注释改成如实描述；
  要真正区分两者需要在自己最小化处置一个标志位（`_minimizedForBrowser`），
  尚未做。
- **L-① 的服务端负载账（合入 main 时补记）**：节奏从 `1.5s/10s/×1.5` 调到
  `500ms/3s/×1.5`，5 分钟 TTL 内的轮询次数从 **33 次变成 102 次（3.1 倍）**。
  服务端对轮询端点**没有自限流**——`httpx.CodeRateLimited` 与
  `IsRateLimited` 处理的是微信上游返回的限流，不是对客户端轮询频率的约束。
  这是用请求量换「扫码确认 → 桌面感知」的延迟上限（10s → 3s）的自觉取舍，
  但并发登录数上来后 auth 服务的 QPS 会同步上去，而这条分支又恰好与
  「刚登录的账号无法续期」「轮换路径也有第二份扫描——修好一半的 503」
  （`feat/pg-client-integration`）同期落地，auth 服务近期本就出过问题。
  值得盯一眼真实 QPS，必要时给轮询端点加服务端退避。
- **不涉及** 服务端任何代码、回调页、`AuthTokenStore`、`AccountController` 状态机。

# 进度看板：feat/login-perf-and-ux（worktree `C:\WeChatBridge\.worktrees\login-perf-and-ux`）

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
- **L-③ 自动还原窗口的副作用**：`Activate()` 会抢焦点。如果用户登录时在做别的事（多屏场景下另一个屏幕在输入），登录完成的瞬间焦点会被劫回。**待用户反馈再决定是否去掉 `Activate()` 调用**——只 `WindowState = Normal` 不抢焦点。
- **不涉及** 服务端任何代码、回调页、`AuthTokenStore`、`AccountController` 状态机。

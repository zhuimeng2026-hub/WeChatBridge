# feat/m1-desktop-beta-readiness

M1 桌面版 beta 就绪度：从 `origin/main` 拉平后的全量测试，把测出来的缺陷修掉，
把测试报告与代码一起推到远端。

Owner: 本会话；基线 `origin/main` = `505e6d4`。

| 任务 | 状态 | 证据 |
| --- | --- | --- |
| M1-0 与远端拉平，建分支 | ✅ | local `main` rebase 到 `origin/main`（4 个本地提交与远端重复的已并入远端那一版），分支 `feat/m1-desktop-beta-readiness` |
| M1-1 Windows 双形态构建 + 测试 | ✅ | 见下方「测试报告」 |
| M1-2 买断版网络命名空间门禁 | ✅ | `Test-NoNetworkCode.ps1 -Edition Perpetual` → OK |
| M1-3 Go 服务端测试 | ✅ | 12 个包全绿 |
| M1-4 卖家后台契约冒烟 | ✅ | 34 项全过（mock 跑在 127.0.0.1:8917） |
| M1-5 投递：认不出的目标不再抢前台 | ✅ | `WindowsForwardTarget.OwnsForeground` + `DeliveryEngineTests` |
| M1-6 激活：409 不再被当成「购买成功」 | ✅ | `ActivationFailure.LicenceNotReady/Conflict` + `ActivationPurchaseRegression.cs` |
| M1-7 激活：畸形 JSON 不再让窗口卡死 | ✅ | `ActivationContract.JsonPayload` + `AccountServiceTests` / `ActivationServiceTests` |
| M1-8 Obsidian：重复转发不再累积重复消息 | ✅ | `ObsidianNote.MessageKey` + `ObsidianNoteTests` |
| M1-9 工作台：附件列举失败不再清空笔记 | ✅ | `Panes/WorkbenchNote.cs` + `WorkbenchNoteTests.cs` |
| M1-10 知识库：一个读不到的子目录不再中断整库列举 | ✅ | `ObsidianVaultWorkbench.ReadableEntries` + `ObsidianVaultWorkbenchTests` |
| M1-11 知识库：保存失败不再留下上千个空文件 | ✅ | `SaveNewAnalysis` + `AFailingWriteLeavesNoPartialNoteBehind` |
| M1-12 Inbox：`state.json`/`manifest.json` 改原子写 | ✅ | 新增 `Core/AtomicFile.cs` + `AtomicStoreTests.cs` |
| M1-13 收集台账损坏不再拖垮整个应用 | ✅ | `CollectionService.ResolveLedger` + `CollectionLedgerRecoveryTests.cs` |
| M1-14 ShareTarget 不再杀掉另一套安装的 keeper | ✅ | `AppLayout.IsSameInstallation` + `InstallationScopeTests.cs` |
| M1-15 英文界面漏中文（60+ 处） | ✅ | `en.json` 补 60 余条 + 代码接线 + `LocalizationCoverageTests.cs` 静态守卫 |
| M1-16 文档对齐校验器 | ✅ | `verify-doc-alignment.sh` C1.1 改成读「origin 上的远端分支：N条」 |
| M1-17 知识库内的目录/文件仍用中文名 | ⬜ | 属产品决定：改名字会让既有库里的链接与文件对不上 |
| M1-18 视觉还原（富文本气泡 / 应用内模型回复流 / 附件内嵌预览） | ⬜ | 沿用 `docs/progress/feat/obsidian-workbench-variants.md` 的 V-1~V-3 |

## 测试报告

**环境**：Windows 10 Home 19045；.NET SDK 10.0.402；Go 1.26.5；Node v22.22.2。
**分支**：`feat/m1-desktop-beta-readiness`（基线 `505e6d4` = `origin/main`）。

### 构建与测试

| 命令 | 结果 |
| --- | --- |
| `dotnet build windows/WeChatBridge.Windows.sln -c Release -p:WeChatBridgeEdition=Perpetual` | 已成功生成，**0 警告 / 0 错误** |
| `dotnet test windows/WeChatBridge.Windows.sln -c Release --no-build -p:WeChatBridgeEdition=Perpetual` | **通过 665 / 失败 0**（Core 581 + AppTests 84） |
| `dotnet build … -p:WeChatBridgeEdition=Subscription` | 已成功生成，0 警告 / 0 错误 |
| `dotnet test … -p:WeChatBridgeEdition=Subscription` | 通过（Core 少一项形态隔离用例） |
| `powershell -NoProfile -File windows/scripts/Test-NoNetworkCode.ps1 -Edition Perpetual` | **OK**：7 个产物程序集没有越界的网络类型 |
| `cd server && go test ./...` | 12 个包全绿（3 个包无测试） |
| `cd admin-console && node test/smoke.mjs`（mock 在 8917） | **通过 34 项 / 失败 0** |
| `bash scripts/verify-doc-alignment.sh` | PASS 18 / WARN 2 / FAIL 0（WARN 是「已删除的 worktree」的历史记述，预期） |

macOS 端本机跑不了（需要 macOS + Xcode），由 CI 的 macOS job 守着。

### 测出来的缺陷与修法

**一、投递引擎会把 ZIP 贴进任意前台窗口**（`DeliveryEngine.cs` / `WindowsForwardTarget.cs`）

目标既没有窗口句柄也没有进程号时（打包应用启动失败），`OwnsForeground` 旧写法是
「不是我们自己的进程就算」，于是 `WaitUntilForeground` 第一次轮询就通过，
350 ms 后 Ctrl+V 落到用户当时的前台窗口——代码编辑器、聊天软件、密码框都算。
现在认不出归属就返回 false，走前台超时那条诚实路径：投递失败，
但剪贴板里已经有兜底内容，用户离手动粘贴只差一步。
回归用例 `ATargetWeCannotIdentifyNeverClaimsTheWindowThatHappensToBeInFront`
做过变异验证：把判断改回旧语义，用例立刻变红。

**二、服务端的 409 被客户端当成「购买成功」**（`ActivationService.cs` / `ActivationController.cs`）

`ActivationOutcome.Succeeded` 原本只看 `Failure == None`，而 409（授权未确认 /
许可证被吊销 / 属于另一台机器 / 本机已有在途登记）被映射成 `None`。
于是客户看到「授权成功，感谢购买。」与「完成」按钮，许可证文件却从未落盘，
下次启动又变回未授权。现在 `Succeeded` 要求「无错误 **且** 拿到了许可证」，
409 有自己的失败种类；「许可证还在签发」按**暂停**处理——继续轮询，不进「完成」，
也不触发已授权回调。

**三、畸形 JSON 让登录/购买窗口卡死**（`ActivationContract.cs` / `AccountService.cs`）

`JsonElement.GetProperty` 缺字段抛的是 `KeyNotFoundException`，
`GetDateTimeOffset()` 遇到 null 抛 `InvalidOperationException`，
而那些 `catch (JsonException)` 一个都接不住。异常从 fire-and-forget 的
`_ = StartAsync()` 里逃出去，窗口停在「正在获取登录链接…」，按钮禁用，无报错无重试。
服务端 DTO 里 `expiresAt` 就是可空指针，所以这是可达路径。
现在改用 `TryGetProperty` + `ValueKind` 判读，畸形应答映射成
`MalformedResponse`，界面报错并给出重试。

**四、Obsidian 笔记：重复转发会把消息越积越多**（`ObsidianNote.cs`）

去重键由消息正文算出，而写入端自己插在正文里的「追加归档：」标记
落在**上一条消息的正文里**，且没有被 `MessageKey` 剔除，于是那条消息
从第三次转发起再也匹配不上，每转发一次就多重复一条。
现在标记行从消息正文里剔除（标记本身仍然显示在笔记里）。

**五、工作台：附件列举失败会清空刚读到的笔记**（`Panes/WorkbenchNote.cs`）

笔记正文与附件列表在同一个 `try` 里，`catch` 把正文也清了。
现在附件列举单独兜底：正文留着，只清附件列表，状态栏报「读取附件失败」。

**六、一个读不到的子目录让整个知识库列不出来**（`ObsidianVaultWorkbench.cs`）

`Directory.EnumerateDirectories` 遇到一个无权访问的目录就抛，整次列举作废。
现在逐个目录兜底：读不到的那一个跳过，其余照常列出。

**七、保存失败会在用户库里留下最多 1000 个空 .md**（`ObsidianVaultWorkbench.cs`）

`FileMode.CreateNew` 先建文件再写；写失败（磁盘满、杀软锁）留下 0 字节文件，
而 catch 的过滤条件 `File.Exists(candidate)` 恰好为真，于是换名字再试，
循环 1000 次，真正的笔记一次也没写成。现在先判名是否被占，写失败就删掉这次
留下的半成品并抛错。

**八、`state.json` / `manifest.json` 原地覆写**（`Core/AtomicFile.cs`）

仓库里其它落盘点都是「暂存 + 原子改名」，只有这两个是裸 `File.WriteAllText`。
中途崩溃或磁盘满会留下截断的文件：截断的 `state.json` 会让已经投送过的批次
被当成没处理过，而 `intent.json` 已被消费掉，于是记成「过期」。
现在三个写入点都走同一个 `AtomicFile.WriteAllText`。

**九、`collections.json` 损坏会让整个应用起不来**（`BatchCollection.cs` / `CollectionService.cs` / `MainViewModel.cs` / `TrayIconService.cs`）

台账解析抛异常 → `CollectionService` 构造抛 → `Reload()` 吞进 `InboxFailure`，
而此时历史还没发布，记录页永远是空的；托盘菜单在 `WndProc` 里读台账，
异常直接逃进 WPF 消息循环。现在台账读不出来不再致命：原文件**一个字节都不动**，
活动台账挪到 `collections.recovered.json`（下次启动继续用），
界面弹一条「收集记录无法读取，原始文件已保留。」，托盘路径加了兜底。

**十、ShareTarget 的清理扫描会杀掉另一套安装的常驻进程**（`AppLayout.cs` / `SharePipe.cs`）

`KillOtherHelpers` 只按可执行文件名杀进程，忽略安装目录与协调后缀——
而整套协调机制（`AppLayout.CoordinationSuffix`、带后缀的管道与互斥体）
就是为了让便携版与安装版互不驱逐。现在只杀**同一安装**的助手进程。

**十一、英文界面漏中文（60 余处）**（`en.json` + 各处代码）

`L10n.Text` 查不到就原样返回中文，于是英文系统上：引导向导整页、托盘菜单、
「关于/退出」、投递失败原因、登录与购买全流程、技能导入报错、文件夹投递报错、
归档读取报错、笔记请求头、XAML 的 `StringFormat` 标签、拾取器的关闭按钮提示，
全是中文。补齐了 60 余条翻译并把对应代码接上；`XAML StringFormat` 走
`{loc:Loc Key='…'}`（以 `{` 开头的 key 要写 `{}` 转义，否则 XAML 会把 `{0}`
当标记扩展解析）。三条旧译文里还留着中文产品名「微信流」，一并改成 WeChatBridge。

顺带加了一个**静态守卫** `LocalizationCoverageTests`：它扫描 `windows/src` 里
每一个 `L10n.Text(...)` / `L10n.Format($"…")` 调用点，把查表用的 key 重建出来
（格式说明符也算进 key——`$"{x:MM-dd}"` 的查表 key 是 `{0:MM-dd}`），
断言 `en.json` 里真的有这条。switch 分支、三元分支里的字面量也算，
因为守卫读的是整个实参而不只是括号后第一个字面量。
这条守卫一上来就抓到 49 条缺 key，之后每一条新写的本地化都会被它盯着。

### 本轮没有动的

- 知识库里的目录名（「微信流」「附件」「31.微信会话」）与文件名兜底仍是中文：
  改名会让既有库里的链接与文件对不上，属产品决定。
- macOS 端本机无法编译与测试。
- `ActivationService.SendAsync` 的超时没有转成 `ApiTimeoutException`
  （`AccountService` 有转），激活轮询超时会被当成取消静默结束——已登记，未在本轮修。

### 一条观察：`ShareTargetKeeperTests.LoserOfKeeperElectionExitsInsteadOfLingering`

机器内存吃紧的那一轮里，这条用例红过一次（那轮 AppTests 整体耗时 41 s，
平时 12 s），单独重跑与整包重跑都是绿的（3 s / 25 s）。
它会真的拉起 ShareTarget 进程并等 keeper 选举，属于**对机器负载敏感**的用例，
不是本轮改动引入的回归——但它值得记一笔：CI 的 Windows 机器负载高时，
这条用例也可能偶尔变红。

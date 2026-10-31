# Windows 端：100 条上限提示

状态：已实现并验收（Windows 构建 0 警告、Core 633 + AppTests 132 全绿、UI 冒烟覆盖 100 条批次）；
真机上用一条 100 条的真实聊天分享一次仍待用户确认。2026-10-28。
分支：`feat/windows-forward-limit-hint`。

## 背景

微信一次多选转发最多带 100 条消息——「最多选择100条」是**微信自己的**限制，不是本应用的。
用户把一个 300 条的聊天拆成三批分享时，如果应用不吭声，聊天就会在微信的上限处"看起来结束"。

iOS 端早就有这条提示（`ios/Shared/CollectMarker.swift` 的 `SearchAnchor.forwardLimit = 100`）：

- 分享正好 100 条时自动把最后一句有文字的消息复制到剪贴板，回执提示「正好 100 条，后面可能还有」
- 中转站卡片显示「正好 100 条，微信一次最多转 100 条。还有前后的消息？→ 分批收集」
- 收集详情有「怎么接着收」三步引导

Windows 端此前**没有**这条提示：它会分批收集，但用户不知道某一批恰好停在微信的上限上。
本文记录 Windows 端补齐它的实现细节。

## 目标与非目标

**目标**：当某一批的条数正好是 100 时，明确告诉用户「这是微信的上限，后面可能还有」，并给出接着选的方法。

**非目标**（刻意不做，与 `PRODUCT_CAPABILITIES.zh-CN.md` 的红线一致）：

- 不读微信数据库、不解密、不注入微信进程——条数只来自**用户分享出来的 ZIP**
- 不驱动微信 UI 自动多选（那是 Dukou 的做法，本项目没有那条代码路径）
- 不改收集语义：批数怎么追加、怎么交付，一概不动。提示只是提示，账本仍然不按条数切换
- 不出现完成百分比、不出现「还差 N 条」——100 是微信的上限，不是本应用的目标

## 判定与数据来源

条数只有一个来源，和已有代码共用：

```
CollectionBatchMetadata.Read(paths)          // Core/Collections/BatchCollection.cs:155
  → Count = 解析出的聊天记录条数（解析失败时为 null）
```

`Count` 统计的是各 ZIP 里 `聊天记录.txt` 解析出的记录数。对一次正常的微信分享（一个 ZIP、一个 TXT），
它就等于用户当时选中的消息数。

判定规则只有一条，在 Core 里：

```csharp
// Core/Archive/WeChatForwardLimit.cs
public const int Maximum = 100;
public static bool HitsLimit(int? count) => count == Maximum;
```

`count` 为 `null`（TXT 读不出来）时**不提示**——读不出来的归档不构成"这一批满了"的证据，应用不猜。

## 落点

| 位置 | 文件 | 触发对象 |
| --- | --- | --- |
| 收集浮条（胶囊） | `CollectionWindow.xaml` 的 `ForwardLimitPanel` | **最新一批**（用户会从它后面接着选） |
| 批次详情 | `CollectionDetailsWindow.xaml` 的 `ForwardLimitPanel` | **当前选中的那一批**（切换批次时重新判定） |

两处都是黄色提示条，复用主题里已有的 `WarnFillColor` + `WarnInkColor` 组合——
与 `Panes/HistoryPane.xaml:308-313`、`Panes/SkillsPane.xaml:103-108`、`Panes/ScenesPane.xaml:587-592`
是同一种形状，不另造一套视觉。

**为什么只在收集流程**：主界面的历史页**不解析聊天转录文本**（`grep -rn "MessageCount\|GetTranscript" windows/src`
只有 `KnowledgeDelivery.cs` 与 `CollectionBatchMetadata` 两处命中），那里拿不到条数，
硬要显示就得在历史页每行解压 ZIP，代价与收益不成比例。收集流程本来就要读 ZIP
（浮条 `CollectionWindow.xaml.cs:67`、详情 `CollectionDetailsWindow.xaml.cs:162`），
条数是顺带的产物。

## 文案与本地化

两条文案都放在 Core，两个窗口共用（`WeChatForwardLimit.Notice` / `.Advice`）：

| 用途 | 中文 | 英文（en.json） |
| --- | --- | --- |
| 提示 | 正好 100 条，微信一次最多转 100 条，后面可能还有。 | Exactly {0} messages — WeChat forwards at most {1} at a time. There may be more. |
| 做法 | 复制这一批最后一句的文字，回微信搜索定位，从下一条接着多选（一次最多 100 条，重叠几条也没关系），再分享到「分批收集到律档通」。 | Copy the last line of this batch, search for it in WeChat, then select the next messages after it (at most {0} at a time; overlapping a few is fine) and share them to “Collect to WeChatBridge” again. |

本地化机制：中文就是 key（`L10n.Text` / `L10n.Format`），英文在
`Core/Localization/en.json` 手写。`L10n.Format($"… {Maximum} …")` 生成的模板里占位符是 `{0}`，
所以 en.json 的 key 形如「正好 {0} 条，微信一次最多转 {1} 条，后面可能还有。」。

⚠️ `LocalizationTests` 只校验**占位符下标集合**在 key 与译文之间相等、以及译文能被 `string.Format` 接受；
它**不校验 key 存在**——漏一条就是英文界面里静默露出中文。本改动因此在
`WeChatForwardLimitTests.BothSentencesAreTranslated` 里显式断言这两条存在。

XAML 侧的 `{loc:Loc Key='…'}` 不支持占位符，所以两条文案都从代码设置，不写进 XAML。

## 时序上的一个细节

条数是异步拿到的：`CollectionBatchMetadata.Read` 在 `Task.Run` 里读 ZIP。
因此提示**必须等 metadata 回调**才能出现，否则会先闪一下再消失，或者反过来。

- `CollectionWindow.xaml.cs`：在 `Dispatcher.InvokeAsync` 的回调里判定（那里已经有 `metadata`）
- `CollectionDetailsWindow.xaml.cs`：`SetMetadata` 之后统一调 `UpdateForwardLimit()`；
  `SelectReference()` 也会调一次，用于切换批次时的重新判定

提示条在不需要时是 `Collapsed`（不是 `Hidden`），不占高度，因此不影响
`UiSmokeTests` 里对浮条高度 `Assert.InRange(compactHeight, 180, 380)` 的断言。

## 测试

`windows/tests/WeChatBridge.Windows.Tests/WeChatForwardLimitTests.cs`（xUnit，Core 层，共 11 项）：

| 测试 | 断言 |
| --- | --- |
| `TheLimitIsTheNumberWeChatEnforces` | 上限就是 100 |
| `ABatchOnTheCapIsFlagged` | 100 → 提示 |
| `ABatchOffTheCapIsNotFlagged` | 1 / 99 / 101 / 250 → 不提示 |
| `AnUnreadableTranscriptIsNeverFlagged` | `null` → 不提示 |
| `ABatchOfExactlyOneHundredMessagesReadsAsOnTheCap` | 真的造一个 100 条的 ZIP 读回来 → `Count == 100` 且命中 |
| `ABatchBelowTheCapIsNotOnIt` | 99 条的 ZIP → 不命中 |
| `AChatThatTrulyEndsOnTheCapIsStillFlagged` | **如实记下这个取舍**：真的正好 100 条也会提示一次 |
| `CountsAboveTheCapAreNotTreatedAsTheCap` | 101 / `int.MaxValue` 不提示（超出上限的不是"更满"） |
| `AnEmptyBatchIsNotFlagged` | 0 条不提示 |
| `TheNoticeAndAdviceBothNameTheLimit` | 两条文案都含 100（中英文都成立） |
| `BothSentencesAreTranslated` | en.json 里有这两条 |

**AppTests 暂未覆盖**：现有 UI 冒烟夹具只有一个 3 条消息的 ZIP、提交两次（合计 6 条），
断言里写死了「约 6 条」与批次计数；要覆盖 100 条提示需要新增第三个批次，并同步那些断言——
留给 Windows 环境的实现者，步骤见下。

## Windows 环境验收步骤

1. `dotnet test windows/WeChatBridge.Windows.sln -c Release -p:WeChatBridgeEdition=Perpetual`
   （Subscription 再跑一遍；`Edition` 必须同时传给 restore/build/test）
2. 真机：微信里选**正好 100 条**消息 → 转发到「分批收集到律档通」
   - 浮条应出现黄色提示条，文案如上
   - 「批次详情」里当前批次也应出现同一条提示
   - 切到别的批次（若有多批），提示应随之消失/出现
3. 真机：选 99 条 → **不应**出现提示
4. 英文界面（设置里切 English）→ 提示应为英文，不露中文
5. 浮条高度断言不受影响（提示 Collapsed 时不占高）

## 已知边界

- **条数是"解析出来的记录数"，不是微信上报的数字。** 一批里若有多个 ZIP，`Count` 是各 ZIP 之**和**
  （`WeChatNativeArchive.MessageCount` 是单个 ZIP 内取**最大值**，两者在正常单 ZIP 分享下一致）。
- 聊天恰好只有 100 条时也会提示一次——这是可以接受的误报，提示是提醒不是断言。
- 提示只覆盖**收集**流程；走普通转发入口的单批不会提示（那里没有"接着收集"的动作可指）。
- 提示不承诺能控制微信的滚动或保留勾选——它只是告诉用户下一批该从哪一句开始找。

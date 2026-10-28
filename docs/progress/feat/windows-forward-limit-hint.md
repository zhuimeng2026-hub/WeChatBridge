# feat/windows-forward-limit-hint

Owner: Claude（按用户指派，在本机实现并推送；Windows 环境的编译与真机验收另行安排）。

目标：Windows 端补齐 iOS 已有的「100 条上限提示」——某一批正好 100 条时告诉用户这是微信的上限、
后面可能还有，并给出接着选的方法。设计与实现细节见 `docs/design/windows-forward-limit-hint.zh-CN.md`。

| Task | Status | Evidence |
| --- | --- | --- |
| W-0 判定与文案集中在 Core（`WeChatForwardLimit`） | ✅ | `Core/Archive/WeChatForwardLimit.cs` |
| W-1 收集浮条：最新一批正好 100 条时出提示条 | ✅ | `CollectionWindow.xaml` / `.xaml.cs` |
| W-2 批次详情：当前选中批次正好 100 条时出提示条 | ✅ | `CollectionDetailsWindow.xaml` / `.xaml.cs` |
| W-3 英文表补两条译文（key 含占位符，校验下标集合） | ✅ | `Core/Localization/en.json` |
| W-4 Core 测试：100 命中 / 99·101 不命中 / null 不命中 / 真实 100 条 ZIP / 译文存在 | ✅ | `WeChatForwardLimitTests.cs` |
| W-5 设计文档 + `batch-collection.md` 的例外条目 | ✅ | `docs/design/windows-forward-limit-hint.zh-CN.md` |
| W-QA Windows 编译与测试（Perpetual + Subscription） | ✅ | CI run 40594730563：macOS / Windows Perpetual / Windows Subscription 三 job 全绿；Windows Core 617/617、AppTests 84/84 |
| W-QA2 UI 冒烟覆盖 100 条批次 | ✅ | `UiSmokeTests.cs`：第三个批次夹具正好 100 条（`Transcript(100)`），断言浮条提示条可见、文案逐字相符、批次详情切到 100 条批出现同一条、切回 3 条批时收起 |
| 顺带修掉 CI 的三个既有失败 | ✅ | 两个 `ObsidianNoteTests`（归档来源的新契约）、两个 ps1 补 UTF-8 BOM、macOS 附件清单让一条断言越界 |
| Remote push | ✅ | 代码由 NPC 以 `bda534a` 合进 main；本分支随后变基到 main，只剩看板补充，**已删**（留 tag `archive/feat-windows-forward-limit-hint`） |
| PR 与 CI | ✅ | CNB PR #3 已合并；GitHub 侧 run 40594730563 全绿 |
| CNB 镜像与评审 issue | ✅ | issue #1 已关闭（completed）：https://cnb.cool/mengxa.com/WeChatBridge/-/issues/1 |
| CNB PR（评审载体） | ✅ | PR #3 已合并：https://cnb.cool/mengxa.com/WeChatBridge/-/pulls/3 ；**代码评审对象是已落地的 `bda534a`** |

## 归档 tag 与分支清理（2026-10-28）

清理已并入 main 的分支，规矩是**删之前先打归档 tag**（`archive/<分支名>`，斜杠换横杠）——
成果由 tag 记着，不由一条已经没用的分支记着。四个 tag 都已推到 origin 与 CNB 两端：

| 归档 tag | 指向 | 那条分支是什么 |
| --- | --- | --- |
| `archive/codex-attachment-categories` | `29f1680` | 附件分类 |
| `archive/feat-m1-desktop-beta-readiness` | `2144a9b` | M1 桌面版 beta 就绪度 |
| `archive/feat-windows-forward-limit-hint` | `619b073` | 本条 issue 的实现 + 顺带修掉的三个 CI 既有失败 |
| `archive/fix-windows-archive-merge-brand-20261027` | `1bb1e8f` | 归档来源 + 律档通打包；那 7 笔被 squash 成主干的这一笔 |

恢复任何一条：`git push origin archive/<tag>:refs/heads/<分支名>`

**判「是否已并入主干」要比内容，不要只比 SHA**：`fix/windows-archive-merge-brand-20261027` 的 7 个提交
在 `git log` 里看着像没并入，但 `1bb1e8f` 是把同一批改动 squash 的一笔——两边碰过的 5 个文件内容**逐字相同**
（`git diff 1bb1e8f db3c1b6 -- <那些文件>` 为空），唯一的差别是主干后来给两个 `.ps1` 补了 UTF-8 BOM。
所以这个归档 tag 指向 `1bb1e8f`（主干里承载这份成果的那一笔），而不是分支 tip `db3c1b6`——
与仓库其它归档 tag 一致（指向的都是 main 的祖先，`verify-doc-alignment.sh` 的 C2 逐条判 PASS）。
分支 tip 那串 SHA 随分支删除而不再可达，**成果一条没丢**。
`git cherry` 对这种 squash 后的主干提交不灵（patch-id 对不上），要看的是
「分支碰过的那些文件在主干里是不是已经一样」。

清理后两端都只剩三条在途分支，都还有 main 里没有的工作：
`codex/desktop-archive-design`（多 11 笔）、`feat/sqlite-migration`（多 8 笔）、
`codex/lvdangtong-wechat-share`（多 4 笔，2026-10-28 新起）。

## CNB 上的状态（2026-10-28，完结）

- **评审 issue #1 已关闭**（completed）：https://cnb.cool/mengxa.com/WeChatBridge/-/issues/1
- **代码由 NPC 的 `bda534a` 合进 main**，与我的实现**逐字相同**（`git diff bda534a 7643c9e -- windows/src/` 为空）；
  NPC 版另加 3 个测试用例（正好 100 仍提示的取舍说明、超上限不算在上限、空批不算），并更新了 CLAUDE.md 与看板。
- **PR #3 已合并**：最早是 NPC 把 `main` 合进源分支解冲突（取并集），我随后把分支变基到 `main`
  （冲突一律取 NPC 的版本），代码提交因与 `bda534a` 等价而被丢弃，只剩看板补充。PR 合入后分支已删，
  留 tag `archive/feat-windows-forward-limit-hint`。
- **CNB main 与 GitHub fork 的 main 同为 `619b073`**。CLAUDE.md 那条「origin 优先、cnb 跟随」的单向规则
  已按实情改写（工作面在 CNB、CI 在 fork，写入方向是双向的）。
- **CI 已全绿**：run 40594730563 三个 job 全过（macOS / Windows Perpetual / Windows Subscription），
  Windows Core 617/617、AppTests 84/84。此前从 10-25 起连续的三个既有失败（两个 `ObsidianNoteTests`
  的归档来源契约、两个 ps1 漏 UTF-8 BOM、macOS 附件清单让一条断言越界）一并修掉——**没有改任何生产逻辑**，
  只改了测试断言与两个打包脚本的 BOM。
- **CNB 的坑：用 git 推上去的新分支不会进 CNB 的分支库。** `POST /{repo}/-/pulls` 会报
  `invalid commit revision refs/heads/<分支>`（`main` 等镜像建立时就有的分支能解析）。
  要先用 CNB 自己的接口把分支登记出来：
  ```bash
  POST https://api.cnb.cool/mengxa.com/WeChatBridge/-/git/branches
  {"name": "<分支>", "start_point": "<提交哈希>"}
  ```
  登记完 PR 才建得起来（`POST /{repo}/-/pulls`，`head`/`base`/`title`/`body`）。

## 变基到改名后的主干（2026-10-27）

分支最初基于 `505e6d4`，而 `origin/main` 随后前进到 `0e26897`——产品已改名为「律档通」，
分享入口名随之从「分批收集到微信流」变成「分批收集到律档通」。因此变基到新主干，并跟着改了两处：

- 提示文案里的入口名改为「分批收集到律档通」（`WeChatForwardLimit.Advice` 与 en.json 的 key）
- 上游新增的 `LocalizationCoverageTests` 只认 `L10n.Format($"…")` 同一行的写法（它从调用点后面
  重建 lookup key），所以两条文案都压成单行，让这条测试真的覆盖到它们

变基后与 `origin/main` 的差异仍只有本分支的 10 个文件。

## CNB issue 正文（待发布）

目标：`mengxa.com/WeChatBridge` 的 issue，要求评审本次推送的代码质量。

> ## 评审请求：Windows 端 100 条上限提示
>
> **评审对象**
>
> - 仓库：`mengxa.com/WeChatBridge`（CNB 私有镜像，权威是 GitHub fork `zhuimeng2026-hub/WeChatBridge`）
> - 分支：`feat/windows-forward-limit-hint`
> - 提交：`7643c9e` feat(windows): [W-0~W-5] 补齐 100 条上限提示（`d172b05` 只更新看板）
> - 基线：`main` = `0e26897`
> - 对照：https://github.com/zhuimeng2026-hub/WeChatBridge/compare/main...feat/windows-forward-limit-hint
>
> **这次改了什么**
>
> 微信一次多选转发最多 100 条。一批正好 100 条时，聊天会在微信的上限处看起来结束。
> iOS 端早有这条提示（`SearchAnchor.forwardLimit`），Windows 端此前没有。
>
> - Core 新增 `WeChatForwardLimit`：上限常量、命中判定（`count == 100`；转录读不出来时 `count` 为 `null`，不猜）、两条中英文案
> - 收集浮条：最新一批正好 100 条时出黄色提示条
> - 批次详情：当前选中批次正好 100 条时出同一条提示条，切批次时重新判定
> - `en.json` 补两条译文
> - Core 测试 8 项（含真实 100 条 ZIP）
>
> 条数仍只来自用户分享出来的 ZIP：不读微信数据库、不注入微信进程、不改收集语义、不出现完成百分比。
>
> **特别想被挑的地方**
>
> 1. 判定用「正好 100」而不是「>= 100」——微信的上限是硬边界，但恰好 100 条的聊天也会误报一次；这个取舍是否合理
> 2. 提示只落在收集流程，主界面历史页不解析转录文本（那里拿不到条数）——范围划得对不对
> 3. 文案是否越界成了「收集目标」——`batch-collection.md` 原写「不显示 100 条为上限或目标」，这次是有意的例外，已在该文件记明
> 4. 提示条在 metadata 异步到达后才出现、`Collapsed` 时不占高，是否影响既有 UI 冒烟断言
> 5. Core 用例断言了 en.json 里两条译文存在（`LocalizationCoverageTests` 只校验占位符，不校验存在性）——是否够
> 6. 本机是 Linux，Windows 端编译与测试**未跑过**；AppTests 未覆盖 100 条场景（现有夹具只有 6 条，断言写死了「约 6 条」）
>
> **附**
>
> - 设计文档：`docs/design/windows-forward-limit-hint.zh-CN.md`
> - 看板：`docs/progress/feat/windows-forward-limit-hint.md`

PR 链接（点开即建，并触发 CI）：
https://github.com/zhuimeng2026-hub/WeChatBridge/pull/new/feat/windows-forward-limit-hint

## W-QA2：UI 冒烟覆盖 100 条批次（2026-10-28）

此前 AppTests 的收集夹具只有两个 3 条批次，提示条那条路径只有 Core 单测兜着，UI 层没人碰过——
评审时列的第 4 点（「提示条出现后是否影响既有 UI 冒烟断言」）正是这个缺口。

改法：

- 夹具增加第三个批次，恰好 100 条（`Transcript(WeChatForwardLimit.Maximum)`，与 Core 测试同一种报文格式），
  收集摘要的断言随之从「约 6 条」改成「约 106 条」（3 + 3 + 100）。
- 浮条：断言 `ForwardLimitPanel` 可见、`ForwardLimitNotice` / `ForwardLimitAdvice` 与 Core 文案逐字相符。
- 批次详情：断言三个标签页；打开时停在 100 条那批 → 提示条可见；切到 3 条批 → 收起；切回 100 条批 → 再次出现。
  探测完把选中项切回第二批，后面那段改名流程仍落在原来的批次上。
- `References.Count` 的断言由 3 改成 4：100 条批的「前 2 条 + 后 2 条」不再重叠（6 条批时有重叠所以是 3）。

**高度断言需要放宽，这是实测出来的，不是猜的**：提示条把紧凑浮条撑高了——原本上限 380，
实测变成 **386（中文）/ 442（英文）**，英文的「怎么接着收」那句更长、多折了一行。
两条 `InRange` 的上限从 380 提到 460，并注明这两个实测值；上限的作用是拦住「浮条撑满屏幕」，
不是钉死像素高度。

实测：Perpetual 全量 633 + 132 全绿（`dotnet test -c Release -p:WeChatBridgeEdition=Perpetual`）。

## 已知取舍

- 提示只落在**收集**流程：主界面历史页不解析聊天转录文本，那里拿不到条数（`GetTranscript` 仅
  `KnowledgeDelivery` 与 `CollectionBatchMetadata` 两处调用）。
- 条数是**解析出的记录数**，不是微信上报的数字；TXT 读不出来时为 `null`，不提示。
- 100 是微信的上限，不是本应用的收集目标——不改收集语义、不出现完成百分比。
- iOS 端同名能力在 `ios/Shared/CollectMarker.swift` 的 `SearchAnchor.forwardLimit`；两端共用同一个数字。

## 构建与测试

本机是 Linux，`dotnet` 不在 PATH，Windows 端编译/测试在此跑不了（见 CLAUDE.md「这台机器上能跑什么」）。
按用户指派，依赖安装与构建交给 Windows 环境：

```bash
dotnet restore windows/WeChatBridge.Windows.sln -p:WeChatBridgeEdition=Perpetual
dotnet build   windows/WeChatBridge.Windows.sln -c Release --no-restore -p:WeChatBridgeEdition=Perpetual
dotnet test    windows/WeChatBridge.Windows.sln -c Release --no-build -p:WeChatBridgeEdition=Perpetual
# Subscription 再来一遍；Edition 必须同时传给 restore/build/test
```

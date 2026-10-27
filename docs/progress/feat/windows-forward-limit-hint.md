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
| W-QA Windows 编译与测试（Perpetual + Subscription） | ⏳ | 本机无 .NET SDK，按用户要求不在此安装；交给 Windows 环境 |
| W-QA2 UI 冒烟覆盖 100 条批次 | ⬜ | 现有夹具只有 6 条，需新增第三个批次并同步「约 6 条」等断言 |
| Remote push | ✅ | `7643c9e` → `origin/feat/windows-forward-limit-hint`（fork `zhuimeng2026-hub`） |
| PR 与 CI | ⏳ | 本机存的 GitHub token 只有 push 权限，建不了 PR；需开 PR 才会跑 CI（`ci.yml` 只认 push 到 main / `feat/windows-port`，或 PR 到 main） |
| CNB 镜像与评审 issue | ✅ | issue #1：https://cnb.cool/mengxa.com/WeChatBridge/-/issues/1 ；镜像分支 `feat/windows-forward-limit-hint` = `8a38d65`，与 origin 一致 |
| CNB PR（评审载体） | ✅ | PR #3：https://cnb.cool/mengxa.com/WeChatBridge/-/pulls/3 （head `feat/windows-forward-limit-hint` → base `main`，4 个提交）；状态 conflict，见下 |

## CNB 上的状态（2026-10-28）

- **评审 issue #1 已开**，CodeBuddy NPC 接到后自行实现并合进了 **CNB main**：`bda534a feat(windows): 补齐 100 条上限提示`，
  之后的 `9986270` 记了分支数。两版**代码文件逐字相同**（`git diff bda534a 7643c9e -- windows/src/` 为空），
  NPC 版另加 3 个测试用例（正好 100 仍提示的取舍说明、超上限不算在上限、空批不算），并更新了 CLAUDE.md 与看板。
- **PR #3 因此显示 conflict**——是重复实现，不是分歧。评审对着 PR #3 的 diff 即可（等价于评审已落地的 `bda534a`）。
  已在 issue #1 里说明。
- **CNB 的坑：用 git 推上去的新分支不会进 CNB 的分支库。** `POST /{repo}/-/pulls` 会报
  `invalid commit revision refs/heads/<分支>`（`main` 等镜像建立时就有的分支能解析）。
  要先用 CNB 自己的接口把分支登记出来：
  ```bash
  POST https://api.cnb.cool/mengxa.com/WeChatBridge/-/git/branches
  {"name": "<分支>", "start_point": "<提交哈希>"}
  ```
  登记完 PR 才建得起来（`POST /{repo}/-/pulls`，`head`/`base`/`title`/`body`）。
- CNB main 的 tip 是 `9986270`，与 GitHub fork 的 `origin/main`（`0e26897`）**已分叉**：
  NPC 的实现只在 CNB main 上，fork 的 main 还没有。CLAUDE.md 写的是「origin 优先、cnb 跟随」，
  这个方向要有人统一。

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

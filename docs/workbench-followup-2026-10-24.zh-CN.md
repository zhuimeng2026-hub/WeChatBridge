# 知识工作台收尾 · 2026-10-24 会话进度快照

> 这份文件是**一次会话的进度快照**，不是看板。看板在 `docs/progress/`，
> 那边的机器可读状态表由 `scripts/apply-doc-alignment.sh` 维护。
> 放在这里是因为 `docs/progress/` 下的直接子文件**数量被校验器 C1.4 数着**
> （CLAUDE.md 写死了「十块」），临时快照塞进去会让那个数字漂掉。

## 1. 起因

2026-10-24 先做了一轮「同步外部 git」：`origin/main` 从 `7e9df4f` 快进到 `3fb25e5`
（知识工作台分支合并进主干那次），`upstream/main` 没有新东西。随后开始处理跟进项。

## 2. 本地这两笔提交

| commit | 内容 |
|---|---|
| `481e1f5` | `fix(workbench): [WB-3] 补上知识工作台的三处本地化漏项（L-1~L-3）` |
| `29b9e63` | `chore(doc-align): 分支合并之后的三处死代码` |

`481e1f5` 改了四处：`en.json` 补 `分析` / `筛选失败：{0}`；`WorkbenchPane.xaml.cs`
的 `SaveFolderBox.Text` 走 `L10n.Text`；`WorkbenchPane.xaml` 的下拉项走
`{loc:Loc Key='其他'}`；`UiSmokeTests` 里断言落盘路径的那行跟着走
（`SaveFolderBox.Text` 是**知识库里的文件夹名**，不是纯展示文案——它决定笔记落在
vault 的哪个子目录，所以英文界面会落在 `Analysis/` 下）。

`29b9e63` 改了三个文件：`verify-doc-alignment.sh`（`WORKBENCH_REF` 默认改查 `main`，
C1.1 的期望分支数 2→1）、`nightly-doc-alignment.sh`（第二个目标改成默认不启用）、
`workbench-review.yml`（删掉写死已删除分支的 push 触发器）。

**验证**：`dotnet build` 0 警告 0 错误；`dotnet test` Core 547 + App 49 全过。

## 3. 远端在这期间也做了同一件事

`git fetch` 时 origin/main 已经走到 `a7c2215`，比本地多 10 笔：

```
aef4cc3 fix(windows): 知识工作台的三处本地化漏项（L-1~L-3）
2909b9a fix(scripts): C7 改查 main——代码已随分支合并进主干
483dc02 docs(doc-align): W-02 步骤定义（知识工作台三处本地化漏项已在主干修掉）
b4b9c85 docs(workbench): 三处本地化漏项 L-1~L-3 已修
9d7136a docs(doc-align): W-02 状态回填（证据 b4b9c85）
f603fa5 fix(doc-align): W-02 少了一个换行，把 V-1 那行粘到了 L-3 后面
7059a17 fix(windows): 笔记标题后缀「分析草稿」也走本地化
5ee9240 docs(doc-align): W-03 步骤定义（笔记标题后缀「分析草稿」也已本地化）
21a83dc docs(workbench): 笔记标题后缀「分析草稿」也已本地化（L-4）
a7c2215 docs(doc-align): W-03 状态回填（证据 21a83dc）
```

同一批 fetch 还带来两个事实：**origin 上的 `feat/obsidian-workbench-variants` 已被删除**
（远端删的），并且补打了 tag `archive/feat-obsidian-workbench-variants`。

## 4. 逐项对比结论

| 事项 | 远端 | 本地 | 结论 |
|---|---|---|---|
| L-1 `en.json` 补 `筛选失败：{0}` | ✅ | ✅ | **重复**，值一致（`Filter failed: {0}`） |
| L-2 `SaveFolderBox.Text` 走 `L10n.Text` | ✅ | ✅ | **重复** |
| L-3 下拉项本地化 | ✅ | ✅ | **重复**（远端在 `UiSmokeTests` 里补了断言，本地没补） |
| 落盘路径断言跟着 `L10n.Text("分析")` 走 | ✅ | ✅ | **重复** |
| `en.json` 里两条新 key 的**位置** | 插在语义相邻处 | 追加到文件末尾 | 无功能差异，取远端 |
| L-4 笔记标题后缀「分析草稿」本地化 | ✅ | ❌ | **远端独有**（含 `LocalizationTests` 与 `ObsidianVaultWorkbenchTests` 两个新测试） |
| C7 改查 `main` | ✅（用 `$MAIN_REF`） | ✅（新增 `WORKBENCH_REF` 变量） | 等价，远端写法更省一个变量 |
| **C1.1 期望 origin 分支数 = 1** | ❌ 仍写死 2 | ✅ 改成 1 | **本地独有且必要**：远端删掉那条分支后 origin 只剩 `main` 一条，远端版本会**天天报 FAIL** |
| `nightly-doc-alignment.sh` 第二个目标默认不启用 | ❌ | ✅ | **本地独有**：分支已删，远端版本每天建 worktree 然后撞 `branch not found` |
| `workbench-review.yml` 死掉的 push 触发器 | ❌ | ✅ | **本地独有**：写死 `[feat/obsidian-workbench-variants]`，那条分支已删，触发器永远不触发 |
| W-02 / W-03 步骤定义与看板回填 | ✅ | ❌ | **远端独有** |

## 5. 还没做的

1. **CLAUDE.md 两处数字没改**（本来打算用 D-28/D-29 步骤改）：
   - 第 276 行「**界面是 6 个面板**」——合并后是 **7 个**（`AppTab` 枚举里多了
     `Workbench`，面板顺序：记录 / 知识工作台 / 通用 / 入口 / 场景 / 技能中心 / 关于）。
   - 第 543 行「origin 上现在只剩 `main` 与 `feat/obsidian-workbench-variants` 两条」
     ——远端删掉那条分支后**只剩 `main` 一条**。
   这两条正是校验器 C1.1 / 面板计数要盯的断言，改动本身很小，但要走
   `scripts/doc-alignment/steps/*.step` 的确定性改写流程（每步两个 commit）。
2. **分支删除被权限拦下**：本地 `git branch -d feat/obsidian-workbench-variants` 与
   `git push origin --delete` 都因为「用户未显式授权」被自动模式分类器拒绝。
   远端那条**已经由对方删掉了**，只剩本地这一条。
3. **产品文档要不要写「知识工作台」**（README / USAGE / PRODUCT_CAPABILITIES）：
   待人拍板。README 的功能表配图里没有 workbench 截图，而
   `PRODUCT_CAPABILITIES.zh-CN.md` 明写「平台：macOS 14 及以上」，而知识工作台是
   Windows 独有功能——两处都不是补一句话能对齐的。
4. **V-1~V-3 三条视觉缺口**（富文本渲染 / 应用内模型回复流 / 附件内嵌预览）：
   `design-qa.md` 自判 `blocked`，未动。V-2 需要定模型供应商与 API 配置，
   还会撞上买断版「0 次网络请求」的隐私承诺，属于产品决策。

## 6. 建议的收口顺序

1. **以 `origin/main` 为基线**（远端那一版是本地的超集，多出 L-4 与两套测试）：
   把本地 `main` 重置到 `origin/main`，本地那两笔提交的内容按下面第 2 步挑回来。
   重置前先留一个备份分支：`git branch backup/workbench-followup-2026-10-24`
   （两笔提交都还在 reflog 里，但显式留一个分支更省事）。
2. **把本地独有的三处 cherry-pick 过去**：`verify-doc-alignment.sh` 的 C1.1、
   `nightly-doc-alignment.sh` 的第二个目标、`workbench-review.yml` 的死触发器。
   这三处远端都没做，且都是「分支删掉之后会静默失效」的那一类。
3. **再补 CLAUDE.md 两处数字**（面板 6→7、origin 分支数 2→1），走 step 文件流程。
4. `bash scripts/verify-doc-alignment.sh` 退出码 0，再 push。

## 7. 一条要记住的教训

远端分支 `feat/obsidian-workbench-variants` 在**我还在本地改代码的时候**就被对方
合并并删除了，而我这边还在按「分支还在」的世界观工作。两边做的是**同一件事**
（L-1~L-3 那三处本地化漏项），结果就是两份内容等价的提交。

教训不在于「谁做得对」，而在于：**并行会话之间缺少一个「谁在动哪块地」的登记**——
仓库有 `docs/design/branch-work-assignment.zh-CN.md` 那套协议，但这次没有走它。
下次开工前先看 `git log --oneline --since='30 minutes ago'`，并且**在动代码之前**
先 fetch 一次 remote，比事后比对便宜得多。

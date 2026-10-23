# 文档对齐清单（2026-10-23）

> 这份清单回答一个问题：**仓库现状类文档，跟代码的实际状态差了多少？**
> 机器可读的状态表在 `doc-alignment.resume.md`，步骤定义在
> `scripts/doc-alignment/steps/*.step`，执行器是 `scripts/apply-doc-alignment.sh`。

## 1. 背景

2026-10-23 做了一轮分支清理（本地与远端共删掉 6 条分支、1 个 worktree），
文档没跟上。清理本身是机械的，但它把一批**「看起来还活着、实际上早就没了」**的东西
暴露了出来——这正是看板最容易腐烂的方式。

清点后的三档事实：

| 档 | 例子 | 危险程度 |
|---|---|---|
| **文档指向不存在的对象** | `progress-sync-protocol.zh-CN.md` 说「远端是 `fork` 不是 `origin`」——**而 `origin` 恰恰是我们的 fork**，`upstream` 才是上游 freestyleFly。照着那份文档操作会把代码推到上游仓库 | 高 |
| **看板引用的证据查不到** | `server-convergence.md` 的 A-⑤/A-⑥/A-⑦ 三行引用的 commit（`42ccdc8` / `ad457b6` / `ca2289c`）**在仓库里根本不存在**——不在 `main`，不在任何 archive tag。`git show` 直接报 bad object | 中 |
| **看板自相矛盾** | `client-license-gate.md` 说 S1–S4 ✅，`client-license-gate.resume.md` 把同四步标成 ⬜；后者还指向一个**已删除的目录** | 中 |

## 2. 怎么跑

```bash
# 干活的（确定性改写 + 每步一个 commit）
bash scripts/apply-doc-alignment.sh --target main
bash scripts/apply-doc-alignment.sh --target feat/obsidian-workbench-variants

# 只看会做什么，不动手
bash scripts/apply-doc-alignment.sh --target main --dry-run

# 验收（只读，零依赖，可单独挂定时任务）
bash scripts/verify-doc-alignment.sh
```

执行器**一次触发把能做的都做完**，每步一个独立 commit，步与步之间互不依赖——
所以中途被打断只丢当前那一步，重跑从断点继续。

### 夜间无人值守（Linux）

**真正执行的是一台 Linux 主机**：克隆本仓库，装好 git，挂在 cron 上跑。
`scripts/nightly-doc-alignment.sh` 是启动器，它做三件事：建 workbench worktree
（分支还在才建——2026-10-24 那条分支归档之后就没了）→ 各目标分支各跑一遍执行器 →
报告各分支领先 origin 多少提交。

挂 cron：

```cron
0 1 * * * /path/to/repo/scripts/nightly-doc-alignment.sh >> /path/to/log 2>&1
```

**默认不推 origin。** 提交留在本地等人看过；要推就
`PUSH=1 scripts/nightly-doc-alignment.sh`。无人值守的作业不该自己决定把东西
发出去——某一步改错了，那笔内容就带着 commit hash 进了远端历史，撤不干净。

**它不安装、不修改任何定时任务**——cron 那行由人写。它在清单跑空时也只提示，
不去动 crontab。（脚本是幂等的：清单空了它每次跑都是空操作，留着也不会做错事，
只是每天醒来一次。）

**异地部署**：脚本**不写死任何路径**——仓库根从脚本自身位置推导（`scripts/` 的上一级），
worktree 目录从仓库根的同级推导（`<repo>` → `<repo>-wb`）。整个仓库拷到别的机器、
别的目录、换个用户都能直接用，只需重新挂一次 cron。

**前置条件**（不满足时启动器会拒绝跑，并说明原因）：

| 条件 | 为什么是硬条件 |
|---|---|
| 主检出停在 `main` | 执行器要求目标分支已检出；一个分支不能同时被两个工作树占用 |
| 两个工作区都**没有未提交的改动** | 脏工作区下「改坏了就回滚」会误伤别人的改动——2026-10-23 清 `D:\WeChatBridge-C` 时就是这么丢了三份测试文件 |
| workbench 分支已推送到 origin | 启动器要推它 |

**行尾**：仓库没有 `.gitattributes` 且开发机 `core.autocrlf` 开着，Windows 上
checkout 出来是 CRLF。**提交进仓库的 blob 是 LF（CR 字节为 0）**，所以 Linux 上
clone 出来就是 LF，脚本原生可跑，步骤定义与目标 `.md` 的行尾也一致——执行器里的
`normalize_body` 会按**目标文件**的约定归一化，两边都能对上。

## 3. 步骤明细

完整的「现状 → 依据 → 改法 → 验收」在 `scripts/doc-alignment/steps/*.step` 每个文件里。
下面是索引：

### main 分支（D-01 ~ D-28）

| ID | 文件 | 改什么 | 为什么 |
|---|---|---|---|
| D-01 | `client-license-gate.resume.md` | 顶部加退役说明 | 它依赖的 worktree 已删，路径全部失效 |
| D-02 | `client-license-gate.md` | 第 18 行改写 | S5 缺口曾是「差一次移植」，现在**是空的**——能补它的测试文件随 worktree 丢了 |
| D-03~05 | `server-convergence.md` | 三个证据 commit 改指主干等价提交 | `42ccdc8`/`ad457b6`/`ca2289c` 查不到；等价物是 `3fc5702`/`f19e72e`/`19d4d5a` |
| D-06 | `server-convergence.md` | 补「决策缺口」一节 | 「买断即全功能」五枚 entitlement **从未登记进 §11.7**，主干只发 PRO 一枚。今天无功能差异，但这是决策缺口不是实现缺口 |
| D-07 | `delivery-quota.md` | 补「决策现状」一节 | Q-① 未拍板，所以「门禁不锁任何功能」**仍然有效**；原看板容易被读成「已经推翻了」 |
| D-08~11 | `admin-console.md` / `pg-client-integration.md` / `unionid-bridge.md` / `login-perf-and-ux.md` | 各加归档标记 | 分支已删。**归档 ≠ 完成**：`unionid-bridge.md` 里那三行 ⬜ 仍待办 |
| D-12 | `progress-sync-protocol.zh-CN.md` | 修正远端方向 | **把 `fork`/`origin` 写反了**，照着操作会推到上游 |
| D-13 | 同上 | worktree 表标注已删除 | |
| D-14~15 | `client-license-gate-progress.zh-CN.md` | 去掉已删目录的路径 | |
| D-16 | `branch-work-assignment.zh-CN.md` | 标注为历史执行记录 | 那轮三分支并行早已结束，但协议本身仍有效 |
| D-17 | `.gitignore` | 忽略 `/chat-exports/` | 真实微信聊天导出（zip + 聊天记录报告 + 微信图片），误 `git add -A` 就会把私人对话提交进仓库 |
| D-18 | `CLAUDE.md` | 加校验器入口 | |
| D-19 | `.gitattributes`（新建） | 给 `*.sh` / `*.step` 钉死 LF | 夜间作业跑在 Linux 上，CRLF 脚本会直接 `$'\r': command not found` |
| D-20 | `CLAUDE.md` | 不再写死 main 领先 upstream 的提交数 | 写死的数字在下一笔提交之后就过期了 |
| D-21 | `CLAUDE.md` | 看板块数对齐到十块 | |
| D-22 | `CLAUDE.md` | 「上游内容全在 main 里」改成内容比对口径 | **提交数会骗人**：十一条 PR 分支里有五条的 tip 不在 `upstream/main` 历史里（报 12 个提交），但内容全在 main 里 |
| D-23 | `doc-alignment.md` | 看板里同一条 upstream 断言同步改 | 看板的断言同样不能靠提交数验证——同一处假象，两个文件各写一遍 |
| D-24 | `CLAUDE.md` | 知识工作台从「在飞分支」改成已合进主干 | 分支 2026-10-24 合并后，「随该分支合并才进主干」这句话已经过期 |
| D-25 | `CLAUDE.md` | 讲清子目录里那块看板为什么不被 C1.4 数到 | 校验器只数 `docs/progress/` 的直接子文件，所以「十块」与实际十一块并不矛盾 |
| D-26 | `doc-alignment.md` | workbench 分支的合并决策改成已合并 | CI run 39777985455 已核实为 success，决策不再是待决项 |
| D-27 | `doc-alignment.md` | 出口标准对齐到 D-27，W-01 目标分支改成 main | 看板已随分支进主干，W-01 还在跑旧分支上就没有意义了 |
| D-28 | `CLAUDE.md` | origin 分支数改成一条 | workbench 分支归档删除后远端只剩 main，校验器 C1.1 立刻红了——它当场抓到了这次漂移 |

### workbench 看板（W-01 · 目标分支已改为 `main`）

| ID | 文件 | 改什么 | 为什么 |
|---|---|---|---|
| W-01 | `docs/progress/feat/obsidian-workbench-variants.md` | 状态自洽化 + 补 6 行 ⬜ | 三处本地化漏项（L-1~L-3）+ 三条视觉缺口（V-1~V-3）此前在看板上**一个标记都没有**——而仓库惯例是 ⬜/⏳ 才表示「代码尚未落地」 |
| W-02 | `docs/progress/feat/obsidian-workbench-variants.md` | L-1~L-3 从 ⬜ 改成 ✅ | 三处本地化漏项已在主干修掉（`aef4cc3`），看板上的 ⬜ 得跟着落地，否则它指着一件已经做完的事 |
| W-03 | `docs/progress/feat/obsidian-workbench-variants.md` | 补 L-4 一行 | 笔记标题后缀「分析草稿」是同类漏项但此前没登记，修掉了得留痕 |

## 4. 需要人拍板的（执行器不许动）

这些不是「改文档」能解决的，是产品决定。执行器**故意不碰**它们：

| 事项 | 现状 | 待决 |
|---|---|---|
| **买断版发几枚 entitlement** | 主干只发 PRO 一枚；已归档分支里是 PRO/PDF/EXPORT/SCENE/HISTORY 五枚 | 改成五枚并登记进 §11.7，还是明确记录维持 PRO-only 的理由 |
| **workbench 分支合不合并** | **已合进 `main`**（2026-10-24）：CI run 39777985455 对带代码的提交 `5ab4785` 是 success——用 `curl https://api.github.com/repos/<repo>/actions/runs/39777985455` 核实的（`gh` 未登录，`gh run view` 会先去要认证） | 剩下的三处本地化漏项（L-1~L-3）修不修——它们已作为 ⬜ 行登记在主干上的看板上 |
| **`workbench-review.yml` 的 push 触发器** | 写死 `[feat/obsidian-workbench-variants]`，合并后成死代码 | 删掉 push 触发 / 整个删掉 / 并进 `ci.yml` |
| **README / USAGE / PRODUCT_CAPABILITIES 要不要写「知识工作台」** | 现在零命中，但分支自己的交付文档把它当客户入口 | 分支合了再写，还是现在就补 |
| **旧计划任务 `WeChatBridge-Resume-LicenseGate`** | 指向已删的 worktree，每次触发只在「找不到清单」退出 1 | 停用（推荐）/ 删除 |
| **upstream 的 12 条远端跟踪引用** | 十二条 = `upstream/main` 本体 + 十一条上游 PR 分支。其中五条的 tip 不在 `upstream/main` 的历史里（`git rev-list --count main..<branch>` 报 12 个提交），但**内容全在 main 里**——这是 SHA 层面的假象：上游合并之后分支又被推了同内容不同 SHA 的提交，加上 `macos/` 目录重组搬了路径。判据要用 patch-id / blob 比对，**不能用提交数**（详见 CLAUDE.md 同段） | 保留（CLAUDE.md 要求 upstream 作对比锚点），只在本地层面 prune |

## 5. 已经做掉的部分（2026-10-23）

这些在写清单之前就改了，随同一批提交推上去：

- `CLAUDE.md` 五处：看板/分支清单改成清理后的实况；补「归档 tag ≠ 已并入 main」这条陷阱；
  `upstream` 领先数 137→147；`ci.yml` 触发条件改成与分支无关的表述；
  mock 的真实 handler 位置从已删分支改成 `server/internal/admin/`；
  并行协议里补了「删 worktree 前先看 `status --porcelain`，`--force` 会抹掉别的会话的未跟踪文件」。
- 分支清理本身：删掉 `D:/WeChatBridge-C` worktree 与 `feat/client-license-gate`、
  `docs/windows-local-verification`、`feat/admin-console`、`feat/windows-portable-usb`、
  `feat/server-convergence` 五条本地分支；补打 `archive/feat-windows-portable-usb` tag。
  **待办**：`git push origin --delete feat/server-convergence`（远端那条还在）。

## 6. 校验器查什么

`scripts/verify-doc-alignment.sh` 只读、零依赖、可独立挂定时任务。查的是
**可被代码证伪的具体断言**，不是文风：

| 组 | 查什么 |
|---|---|
| C1 | `CLAUDE.md` 写死的计数（远端分支数、领先 upstream 的提交数、看板块数）是否还等于实测值 |
| C2 | 每个 archive tag 的内容**是否真的并入了 main**——「被 tag 钉住」和「已并入」是两件事 |
| C3 | 看板**任务表**里引用的 commit 是不是 main 的祖先（正文里「被覆盖的对方提交」那种取证材料不算） |
| C4 | 看板提到的文件路径在主干上是否真的存在 |
| C5 | 已知缺口与实际是否对得上（S5 缺口、兑换码退役、admin 路由条件挂载、entitlement 枚数） |
| C6 | 仓库现状类文档是否还引用已删除的 worktree 路径 |
| C7 | workbench 分支的三处本地化漏项是否还在 |

**实现上踩过的坑（值得记下来）**：Git Bash 默认 locale 不是 UTF-8 感知的，
`grep -oE '[一二三四五六七八九十]+'` 会退化成按**字节**匹配，抽出的是乱码而不是「八」。
凡是要匹配中文，一律用固定字符串（`grep -F`）或逐个字面量比对。

## 7. 出口标准

- [ ] `bash scripts/apply-doc-alignment.sh --target main` 跑完，`doc-alignment.resume.md` 里 D-01~D-27 全 ✅
- [ ] `bash scripts/apply-doc-alignment.sh --target main` 跑完，W-01 与 W-02 ✅（看板已随分支合并进主干，目标分支从 `feat/obsidian-workbench-variants` 改成 `main`）
- [ ] `bash scripts/verify-doc-alignment.sh` 退出码 0（FAIL 清零；WARN 是已登记的已知缺口，应当逐条有出处）
- [ ] `main` push（workbench 分支归档后只剩这一个目标）
# 文档对齐：机器可读状态表（doc-alignment）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2。
> **这份表由 `scripts/apply-doc-alignment.sh` 读写**，人不要手改状态列——
> 执行器每做完一步会把状态改成 ✅ 并回填 commit 短 hash。
>
> 人类可读的版本在 `docs/progress/doc-alignment.md`（每一步的现状、依据、改法、验收）。
> 步骤定义（精确的「找什么→改成什么」）在 `scripts/doc-alignment/steps/*.step`。
>
> 状态：⬜ 未做 · ⏳ 进行中 · ✅ 已提交 · 🔴 失败（已回滚，不阻塞其余步骤）

| ID | 事项 | 状态 | 备注 |
|---|---|---|---|
|   D-01   |   `client-license-gate.resume.md` 标注退役   | ✅ | a8a059f |
|  D-02  |  `client-license-gate.md` 第 18 行改写（S5 缺口现状）  | 🔴 | 应用失败，已回滚 |
|  D-03  |  `server-convergence.md` A-⑤ 证据 commit 改指主干等价提交  | 🔴 | 应用失败，已回滚 |
|  D-04  |  `server-convergence.md` A-⑥ 证据 commit 改指主干等价提交  | 🔴 | 应用失败，已回滚 |
|  D-05  |  `server-convergence.md` A-⑦ 证据 commit 改指主干等价提交  | 🔴 | 应用失败，已回滚 |
|   D-06   |   `server-convergence.md` 补登 entitlement 决策缺口   | ✅ | 4bd603e |
|   D-07   |   `delivery-quota.md` 写明原决策在拍板前仍有效   | ✅ | 9dfc2d5 |
|   D-08   |   `admin-console.md` 加归档标记   | ✅ | 6937781 |
|   D-09   |   `pg-client-integration.md` 加归档标记   | ✅ | 568f352 |
|   D-10   |   `unionid-bridge.md` 加归档标记   | ✅ | f0a4f70 |
|   D-11   |   `login-perf-and-ux.md` 加归档标记   | ✅ | b3b8672 |
|  D-12  |  `progress-sync-protocol.zh-CN.md` 修正 origin/upstream 方向  | 🔴 | 应用失败，已回滚 |
|  D-13  |  `progress-sync-protocol.zh-CN.md` worktree 表标注已删除  | 🔴 | 应用失败，已回滚 |
|  D-14  |  `client-license-gate-progress.zh-CN.md` worktree 表去路径  | 🔴 | 应用失败，已回滚 |
|  D-15  |  `client-license-gate-progress.zh-CN.md` 正文去路径  | 🔴 | 应用失败，已回滚 |
|   D-16   |   `branch-work-assignment.zh-CN.md` 标注为历史执行记录   | ✅ | fb50d3e |
| D-17 | `.gitignore` 忽略 `/chat-exports/` | ⬜ | 真实微信聊天导出 |
| D-18 | `CLAUDE.md` 加校验器入口 | ⬜ | |
| W-01 | workbench 看板状态自洽化 + 补未落地项 | ⬜ | 三处本地化漏项 + 三条视觉缺口 |

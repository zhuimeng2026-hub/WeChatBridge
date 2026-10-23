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
| D-01 | `client-license-gate.resume.md` 标注退役 | ✅ | 72c5934 |
| D-02 | `client-license-gate.md` 第 18 行改写（S5 缺口现状） | ✅ | 6726632 |
| D-03 | `server-convergence.md` A-⑤ 证据 commit 改指主干等价提交 | ✅ | f55cdad |
| D-04 | `server-convergence.md` A-⑥ 证据 commit 改指主干等价提交 | ✅ | 111ce59 |
| D-05 | `server-convergence.md` A-⑦ 证据 commit 改指主干等价提交 | ✅ | 3a19bb0 |
| D-06 | `server-convergence.md` 补登 entitlement 决策缺口 | ✅ | 2cd96c4 |
| D-07 | `delivery-quota.md` 写明原决策在拍板前仍有效 | ✅ | 667250f |
| D-08 | `admin-console.md` 加归档标记 | ✅ | b19cffd |
| D-09 | `pg-client-integration.md` 加归档标记 | ✅ | 682f20d |
| D-10 | `unionid-bridge.md` 加归档标记 | ✅ | 15441a1 |
| D-11 | `login-perf-and-ux.md` 加归档标记 | ✅ | 3f40a59 |
| D-12 | `progress-sync-protocol.zh-CN.md` 修正 origin/upstream 方向 | ✅ | 6ae50dd |
| D-13 | `progress-sync-protocol.zh-CN.md` worktree 表标注已删除 | ✅ | bbc96d6 |
| D-14 | `client-license-gate-progress.zh-CN.md` worktree 表去路径 | ✅ | b2d8a06 |
| D-15 | `client-license-gate-progress.zh-CN.md` 正文去路径 | 🔴 | 应用失败，已回滚 |
| D-16 | `branch-work-assignment.zh-CN.md` 标注为历史执行记录 | ✅ | 79cf94e |
| D-17 | `.gitignore` 忽略 `/chat-exports/` | ✅ | d45bbc4 |
| D-18 | `CLAUDE.md` 加校验器入口 | ✅ | 4d8c85d |
| D-19 | `.gitattributes` 给 `*.sh` / `*.step` 钉死 LF | ✅ | 6a1a6aa |
| W-01 | workbench 看板状态自洽化 + 补未落地项 | ⬜ | 三处本地化漏项 + 三条视觉缺口 |

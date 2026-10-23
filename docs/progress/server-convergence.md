# 进度看板：feat/server-convergence（机器 A）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2：只有机器 A 更新本文件，与 commit 同一时刻。

## ⚠️ 主线裁决（2026-10-18 22:30，用户拍板）

两台机器曾平行推进本分支并分叉：另一方（git 身份 zhuimeng2025）完成 5 表版 A-④ + A-⑤，本会话完成 4 表版 A-④。**用户裁决：以 4 表为主线**（依据：`branch-work-assignment.zh-CN.md` §2 任务 4 明文 accounts/auth_sessions/activation_requests/licence_bindings 四张表 + 用户此前拍板「本会话接管」）。本分支已 force-with-lease 推送 `53796e1`。

被覆盖的对方提交（**未丢失**，在其本地 clone 与 GitHub 对象库中按 SHA 可取，A-⑤ 的 HTTP 包将移植到 4 表 store）：

| SHA | 内容 |
|---|---|
| `3a9ab08` | docs: A-⑤ 完成，A-⑥ 进行中；§11.7 补登 R-1/R-2 |
| `885ab40` | feat: [A-⑤] activation HTTP 包（3 端点 + 17 测试 + R-1 修复） |
| `2a51cf9` | docs: A-④ 完成，A-⑤ 进行中 |
| `b133ce4` | feat: [A-④] store 11 表 → 5 表（sessions/refresh_tokens 双表 + bindings 并入 requests） |

移植口径：A-⑤ 的服务层/handler/契约字段**原样搬**（其三侧对齐结论有效），store 调用适配我方 9 方法；R-2 口径（status 透传三态、revoked 不再出票）适用并采纳；R-1（upsert 幻写 requestId）在我方「插入即单播、无 upsert」设计下不存在，登记册将注明。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| A-① | `server/` 入 git（基线） | ✅ | `446dc5f` | 50 文件 16,670 行；含三个 billing latent 问题修复 |
| A-② | API 契约 mock（`server/mock/`） | ✅ | `6a3285a` | 14 项 curl 实测通过；真实 handler 落地后删除 |
| A-③ | 删 billing/redeem 包 + config 瘦身 | ✅ | `86abca3` | 13 文件 -5,438 行；build/vet/test 全绿；已推 fork |
| A-④ | store 12 表 → 4 表 + migrations 重写 | ✅ | `88c07c9` | 4 表契约（AuthStore 9 + ActivationStore 9 + Close）；ConfirmActivationRequest/ReissueLicence 两事务；26 文件 +1754/-2321；build/vet/test 全绿；activation_test 22 用例 |
| A-⑤ | activation 包（登记/状态/取票） | ✅ | `3fc5702` | 4 表线上移植对方 A-⑤：HTTP 包 3 端点原样搬 + store upsert 化（ON CONFLICT 保号刷新 + RETURNING 防幻写 + 跨账号回落）；reissue 无 pending 时新插已确认行（复制 anchor/amount）；licence 判活走 LiveBindingByInstallID；18 测试 + 迁移加 live_install 部分索引；§11.7 补登 R-1/R-2/移植记录；build/vet/test 9 包全绿 |
| A-⑥ | admin handler（列表/确认/重签/作废） | ✅ | `f19e72e` | Bearer + WCB_ADMIN_OPENIDS 白名单（403 非 401；§11.7 M-1 落地）；confirm Build+Issue 落 envelope 且幂等（双卖家竞态返赢家票）；reissue 委托 store 换机路径；revoke 幂等；item 契约字段 + RFC3339；29 测试；与 mock 两处严格化已登记 I-1 |
| A-⑦ | cmd/main.go 装配 + healthz + 优雅关闭 + embed 迁移 | ✅ | `19d4d5a` | cmd/wcb：config→migrate→store→signer→services→routes→listen 全链；embed 迁移 + schema_migrations 簿记（每版独立事务含簿记行）；license.LoadPrivateKey 遍历 PEM 块（兼容 openssl ecparam 双块产出，已登记踩坑）；healthz liveness；SIGINT/SIGTERM 优雅关闭 10s；admin 空白名单不挂载（404 而非全信任）；启动门禁实测：无配置 exit 1、库不可达干净失败且不回显密码；真冒烟待 DB（沙箱无 postgres，并入 E/F 外部阻塞） |
| A-⑧ | 全量单测 + 集成测试 | ⏳ | `f9a205b` `813b205` `bfd6348` `4b16081` | **store/postgres 真库覆盖已落地**（`_realdb_test.go` 系列，`WCB_TEST_DATABASE_URL` 开关，不设即 skip 故 CI 无影响）：骨架用**真 .up.sql** 建库、每测试独立 schema、结束 DROP CASCADE。已覆盖 ①条件 UPDATE 守卫实际影响几行（另测去掉谓词会改成 1 行）②两条部分唯一索引 ③FOR UPDATE 串行化两个卖家（加触发器拉长重叠，否则假绿）④reissue 三分支与 revoke CHECK ⑤隐式行序 ⑥handoff 只读一次、session 状态守卫、CompleteAuthSession 原子性 ⑦**NULL hash 一族**（轮换路径四次生产事故的根）⑧envelope bytea 往返（jsonb 会重排键）。**并因此修掉一个真隐患**：签发失败的分类曾依赖两条 CREATE INDEX 的先后顺序（见 §11.7 M-5）。**未完**：store 外（auth/activation/admin 包）仍只有 fake store，`cmd/wcb` 装配层与迁移器本身（`planMigrations` 已有纯函数测试）仍无真库端到端 |

## 出口标准

- [ ] `go build/vet/test ./...` 全绿
- [ ] 一条命令起服务（含迁移），`/healthz` 返回 200
- [ ] activation/admin 集成测试通过（内存 store 端到端：登记→确认→取票→验签）

## 阻塞项

- **真冒烟（起服务 + 迁移实跑 + /healthz 200）阻塞于可用 PostgreSQL**：沙箱内无 postgres 实例（探测确认）。启动门禁已实测（无配置 exit 1、库不可达干净失败且不回显密码），其余留待首次部署联调，与 §11.5 的 E/F 外部输入阻塞同类。


## 决策缺口（2026-10-23 补登）

**「买断即全功能」的五枚 entitlement 从未登记。** 已归档的 `feat/server-convergence`
分支（tag `archive/feat-server-convergence`，**内容从未并入 main**）在 A-⑥ 里让管理员
签发的许可证带五枚 entitlement：PRO / PDF / EXPORT / SCENE / HISTORY，出处是当时的用户裁决。

但主干上 `server/internal/admin/admin.go` 只发 `EntitlementPro` 一枚，而
`docs/design/buyout-flow-convergence.zh-CN.md` §11.7 里**零命中**——
这条产品决策**从来没有被登记进任何设计文档**。

今天不产生功能差异：服务端验签只要求 ent 非空（`server/internal/license/license.go`），
客户端只认一个 bool `IsPro`，macOS 侧同样没有 ent 分级。所以这**不是实现缺口，是决策缺口**——
换一个人读代码，会以为「买断版只值 PRO 一枚」是既定事实。

**待拍板**：(a) 改成五枚并登记进 §11.7；还是 (b) 明确记录维持 PRO-only 的理由。
**在拍板之前，两边都别当它是既定事实。**

# 进度看板：投递配额（未授权 10 次 Obsidian）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2。口径见 `docs/design/delivery-quota.zh-CN.md`。
>
> 2026-10-19 建档：**代码一行未动**。本看板由 `docs/design/delivery-quota.zh-CN.md` §12 的
> 6 条待拍板口径细化而来。逐项状态 ⬜ / ⏳ / ✅。
>
> **本看板描述的改动会推翻一条已登记的产品决策**（门禁不锁任何功能），
> 见设计文档第 2 节列出的四处承重表述。Q-⑧ 是它们的对账，不是可选项。

---

## 0. 阻塞项

| # | 事项 | 状态 |
| --- | --- | --- |
| Q-① | 设计文档 §12 的 6 条口径拍板 | ⬜ **阻塞 Q-④ 之后全部** |

未拍板就写代码，等于把「一次」的定义焊死在实现里——后面每一条都要返工。

## 1. 任务表

| # | 任务 | 落点 | 状态 |
| --- | --- | --- | --- |
| Q-① | 拍板 6 条口径 | `docs/design/delivery-quota.zh-CN.md` §12 | ⬜ |
| Q-② | `DeliveryQuotaStore`：读 / 递增 / 剩余 / 已用尽 | 新增 `Core/Licensing/DeliveryQuotaStore.cs` | ⬜ |
| Q-③ | Store 单测：边界 0/9/10/10+、文件损坏、原子写 | `windows/tests/…/DeliveryQuotaStoreTests.cs` | ⬜ |
| Q-④ | Windows 判定点接入（**只需 2 处**） | `MainViewModel.cs:858`、`MainViewModel.Collections.cs:197` | ⬜ |
| Q-⑤ | 判定行为测试 | `windows/tests/…/DeliveryQuotaTests.cs` | ⬜ |
| Q-⑥ | UI：通用页剩余次数、第 10 次提示、用尽 toast + 跳购买 | `Panes/GeneralPane.xaml{,.cs}`、`en.json` | ⬜ |
| Q-⑦ | macOS 对等实现 + 测试 | `macos/Sources/…/DeliveryQuota.swift`、`macos/Tests/` | ⬜ |
| Q-⑧ | 推翻决策的四处表述对账 + 3 个现有测试 | 见下表 | ⬜ |
| Q-⑨ | 端到端实跑：未授权连转 11 次 + 授权后无限 | 真机 | ⬜ |

### Q-④ 为什么只有 2 处

投递有 4 个调用点，但按口径只有 2 处需要插入检查：

| 调用点 | 计入 | 动作 |
| --- | --- | --- |
| `MainViewModel.cs:858`（Obsidian/Folder 同分支） | ✅ | 按 `action == ShareAction.Obsidian` 条件插入 |
| `MainViewModel.Collections.cs:197` | ✅ | 插入 |
| `MainViewModel.Collections.cs:186`（`FolderDelivery.Save`） | ❌ | 不动 |
| `MainViewModel.LocalImport.cs:35` | ❌ | 不动 |

`KnowledgeDelivery.Deliver` / `FolderDelivery.Save` **不需要改签名**——参数里已有
`configDirectory` 注入（供测试用），判定在调用方做。

### Q-⑧ 对账清单

| 位置 | 动作 |
| --- | --- |
| `CLAUDE.md`「红线」段 | 「门禁不锁任何功能…转发对所有人可用」→ 改写为带配额的表述 |
| `Core/Activation/GateState.cs:33` | `SignedIn` 注释 |
| `Services/ActivationGate.cs:34-37` | 类注释「It does not lock anything」 |
| `Services/LicensingBootstrap.cs:136` | `IsShareDeliveryAvailable => true` 的语义与 doc comment |
| `tests/…/LicensingBootstrapTests.cs:112/122/132` | 3 个测试**由绿转红是预期的**，改它们 |

## 2. 依赖顺序

```
Q-① ──► Q-② ──► Q-③
              ├──► Q-④ ──► Q-005 ──► Q-⑥
              └──► Q-007
                       Q-⑧（可与 Q-004~Q-007 并行）
                            Q-⑨（全部完成后）
```

Q-② 与 Q-⑦ 可并行（两端各写各的，跨语言契约按仓库惯例**复制而非共享**，各自钉死同一常量与同一测试向量）。

## 3. 关键约束（实现时容易踩）

- **落盘点必须走 `AppLayout`**：`delivery-quota.json` 放 `AppLayout.Config` 下，否则便携版会把一部分状态写到 U 盘、一部分写到本机。`LayoutWiringTests` 会守这条。
- **进程内 `lock` 即可**：主进程有单实例互斥（`App.xaml.cs:76`），ShareTarget 助手不做投递。**不要**上文件级 CAS，那是过度设计。
- **写文件先临时文件再 rename**（原子），读-改-写包在同一个锁里。
- **本地导入走 `Task.Run`**，可能与分享转发并发——同一把锁覆盖。
- **不引入网络调用**：判定读 `LicensingBootstrap.Current.Resolution.IsPro`，已授权用户仍满足「0 次网络请求」。
- **en.json 两侧占位符索引集合必须一致**，`LocalizationTests` 会断言。

## 4. 本机验证限制

本机（Linux）**没有 dotnet**，`macos/` 也编译不了（需 macOS + Xcode）。因此：

- Q-002 / Q-004~Q-006 / Q-008 只能在 Windows 上 build/test，依赖 CI（`ci.yml` 的 Windows job，Perpetual 双矩阵）或一台 Windows 机。
- Q-007 需要 macOS。
- Q-001 / Q-002（纯逻辑）可以先写，但**「编译通过」不等于验证通过**——本看板的完成判据一律以真机跑过一次为准。

> 这条对应记忆 `wechatbridge-needs-real-device-scan-to-verify`：编译通过、单测全绿都证明不了功能可用。

## 5. 完成判据

| # | 判据 |
| --- | --- |
| Q-001 | 设计文档 §12 六条全部有明确结论，且已回填进文档 |
| Q-002 | Store 通过 Q-003 全部单测；损坏文件 / 版本不符按「0 次已用」降级而非抛异常 |
| Q-004 | 未授权连投 11 次：前 10 次正常产出笔记，第 11 次不写盘、弹提示并跳购买页 |
| Q-006 | 通用页常驻显示「已用 N / 10」；第 10 次成功时提示一次 |
| Q-007 | macOS 行为与 Windows **逐条一致**（同一测试向量） |
| Q-008 | 4 处表述已改、3 个测试已按新语义重写；**改的是测试，不是绕过** |
| Q-009 | 真机跑通「未授权 11 次 + 授权后无限」全流程 |

## 6. 已知取舍（不再讨论，写在这里备查）

- **计数明文可重置**：删 `delivery-quota.json` 即回到 10 次。这是**有意接受**的——定位是转化引导不是 DRM。见设计文档第 11 节。
- **便携版也限 10 次**：否则「安装版未授权 10 次」是空壳——拿 U 盘版就无限，口径自相矛盾。便携版用尽后的 CTA 必须指向「安装完整版」，因为便携版永远签不出许可证。

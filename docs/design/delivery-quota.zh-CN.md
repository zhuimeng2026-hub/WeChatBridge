# 未授权用户的投递配额：口径定义（待拍板，未落地）

> 2026-10-19 设计稿，**代码一行未动**。范围：`windows/`、`macos/`、`shared/`。
> 本文只定义口径，不含实现。落地前需按第 12 节逐条拍板。

---

## 1. 结论先说

给未授权用户设 **10 次 Obsidian 投递**的免费额度，授权后无限。这不是技术问题，是**推翻一条已登记的产品决策**，所以先把口径写死再动代码。

**技术难度低**：约 300–400 行改动 + 15–20 个测试，1–2 人日（含 macOS 对等实现）。难点全在口径与取舍。

**配额定位为转化引导，不是 DRM。** 计数明文落盘、删文件即重置——这是本方案成立的前提，见第 11 节。

## 2. 这次改动推翻了什么

四处已登记的「不锁功能」表述，改动时必须同步更新，不能顺手删：

| 位置 | 现表述 |
| --- | --- |
| `CLAUDE.md`「红线」段 | 「门禁不锁任何功能…转发对所有人可用」 |
| `GateState.SignedIn` 注释 | 「nothing is locked behind a licence they have not paid for」 |
| `ActivationGate` 类注释 | 「The gate is a path to buy, not a paywall.」 |
| `LicensingBootstrap.IsShareDeliveryAvailable` | `=> true`（`LicensingBootstrap.cs:136`） |

另有 3 个测试**显式断言**未授权仍可转发，`LicensingBootstrapTests.cs:112/122/132`，会由绿转红——这是有意的，改它们而不是绕过。

## 3. 计数单位：什么算「一次」

**一次 = 一个投递批次（batch）成功写入 ≥ 1 个文件。**

三个候选口径与取舍：

| 口径 | 一次转发 20 张图 | 评价 |
| --- | --- | --- |
| 按笔记数 | 算 20 次 | ❌ 把批量能力变成惩罚 |
| 按文件数 | 算 20 次 | ❌ 同上 |
| **按批次** | **算 1 次** | ✅ 与用户心智一致：「我转了一次」 |

**失败不扣。** 只有成功写入 ≥ 1 个文件才递增。`NotConfigured`（没选库）与 `UnreadableArchive`（压缩包坏了）都不扣——这两个不是用户「用掉」的机会，扣了只会被当成故障。

## 4. 哪些入口计入

代码事实：Obsidian 与 Folder 在 `MainViewModel.cs:845` 是**同一个分支**（`action is ShareAction.Obsidian or ShareAction.Folder`），且投递共有 4 个调用点。

| 入口 | 位置 | 是否计入 | 理由 |
| --- | --- | --- | --- |
| 分享转发 → Obsidian | `MainViewModel.cs:858` | ✅ 1 次/批 | 目标链路本体 |
| 合集投递 → Obsidian | `MainViewModel.Collections.cs:197` | ✅ 按合集内**批次数** | 5 个批次 = 5 次 |
| 分享转发 → Folder | 同 `:845` 分支 | ❌ | 见下 |
| 合集保存原件 | `MainViewModel.Collections.cs:186` | ❌ | 属 Folder |
| 本地导入 | `MainViewModel.LocalImport.cs:35` | ❌ | 导入用户已有文件，不产生新内容 |

**Folder 不计入**，因为它只存原始 ZIP、**不产出笔记**——它不是 Obsidian 的替代品，放开不构成绕过路径。如果反过来 Folder 无限制，它就成了「换个目标就无限」的口子。

**本地导入不计入**，因为它不走微信分享链路，卖的不是它。

## 5. 授权后

一旦 `LicenseResolution.IsPro == true`，配额检查**立即停止**，且**不清零已有计数**（无需清零——授权后永远不再检查）。

判定读的是 `LicensingBootstrap.Current.Resolution.IsPro`，即已授权用户仍然满足「0 次网络请求」：配额判定完全离线，不引入任何网络调用。

## 6. 计数存储

```
<AppLayout.Config>/delivery-quota.json
```

明文 JSON，落在 `AppLayout.Config` 下——满足「所有落盘点必须由 `AppLayout` 派生」的红线，便携版自然把状态写在 U 盘上，`LayoutWiringTests.NoSourceFileResolvesLocalAppDataOutsideTheAllowlist` 天然覆盖。

```json
{ "version": 1, "usedObsidianDeliveries": 3, "firstUsedAt": "...", "lastUsedAt": "..." }
```

写文件先写临时文件再 rename（原子），读-改-写包在一个进程内 `lock` 里。

## 7. 并发

主进程有单实例互斥（`App.xaml.cs:76` 的 `Local\WeChatBridge.Windows.Main` + `CoordinationSuffix`），且 ShareTarget 助手只负责暂存与唤醒、**不做投递**，所以全部 Obsidian 投递都发生在这一个进程里。

结论：**进程内 `lock` 即可，不需要文件级 CAS。** 唯一需要注意的是本地导入走 `Task.Run`，可能与分享转发并发——同一个锁覆盖即可。

## 8. 便携版

**推荐：两端都限。**

理由是一条反证：**若便携版不限，「安装版未授权 10 次」就是个空壳**——任何人拿 U 盘版就得到无限转发，口径自相矛盾。所以要么都限，要么都不限；只限安装版等于没限。

便携版用尽后的 CTA 必须是「安装完整版并激活」，**不能是「去购买」**：便携版 `LicenseRuntimeOptions.Portable` 在读许可证之前就 return，**永远签不出许可证**，给一个它无法完成的引导是死胡同。

## 9. UI 口径

- **通用页常驻**显示「已用 N / 10」，与现有授权入口并列（`GeneralPane.xaml:130` 那组按钮附近）。
- **第 10 次成功投递时**提示一次，告知额度已用尽。
- **第 11 次起**：不投递，toast + 跳转购买页（复用 `ActivationGate.OpenManually`，不新开窗口类型）。
- 文案一律中文写在代码里，`en.json` 补对应条目，`LocalizationTests` 会断言两边占位符索引集合一致。

## 10. 两端一致性

`macos/Sources/WeChatBridgeApp/KnowledgeDelivery.swift` 是同一套东西。**只改 Windows 会造成「一端限、一端不限」，用户在 macOS 上白嫖。** 要限就两端一起限，第 4 节的口径两端逐条对应。

跨语言部分（计数文件格式、时间戳格式）按仓库既有惯例**复制而非共享**，两端各一份常量与测试——与许可证规范化向量同一套路。

## 11. 明确不做的事

**不做防篡改。** 明文 JSON，删文件即重置。

这是有意的：定位是转化引导，不是付费墙。要升级成真付费墙需要绑 `DeviceAnchor` + DPAPI + MAC 锚点，成本翻倍，且便携版换机后天然必须重置——那是一个独立的立项，不在本方案内。

需要知道的风险：这套计数在国产桌面软件里基本是公开的。**接受它。**

## 12. 待产品拍板

| # | 问题 | 本文推荐 |
| --- | --- | --- |
| 1 | 「一次」按什么计 | 按批次，成功才计（第 3 节） |
| 2 | Folder / 本地导入算不算 | 都不算（第 4 节） |
| 3 | 便携版限不限 | 限，CTA 指向安装（第 8 节） |
| 4 | 失败扣不扣 | 不扣（第 3 节） |
| 5 | 是否接受可重置 | 接受（第 11 节） |
| 6 | 合集投递按批次数扣 | 是（5 批 = 5 次） |

## 13. 落地时的代码落点

| 项 | 量 |
| --- | --- |
| `DeliveryQuotaStore`（Core）+ 测试 | ~50 行 + 5 测试 |
| 4 个调用点前插入检查（实际只需 2 处：`MainViewModel.cs:858` 与 `Collections.cs:197`） | ~30 行 |
| 用尽后的 toast + 跳购买页 + `en.json` 2 条 | ~40 行 |
| 改 `IsShareDeliveryAvailable` 语义 + 3 个现有测试 + 4 处文档注释 | ~30 行 |
| macOS 对等实现 | 同量级 |

`KnowledgeDelivery.Deliver` / `FolderDelivery.Save` 的参数里**已有 `configDirectory` 注入**（供测试用），加配额不需要改签名。

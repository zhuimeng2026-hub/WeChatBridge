# 进度看板：feat/client-license-gate（机器 C）

> 规则见 `docs/design/progress-sync-protocol.zh-CN.md` §2：只有机器 C 更新本文件，与 commit 同一时刻。
>
> 2026-10-18 校准：此前看板未随 `d48827f` 更新，C-①/③/⑤ 的数据层与控制器其实已落地。
> 本表按 `docs/design/client-license-gate-progress.zh-CN.md` 的 0–19 细目重新核对代码后重写。
> 逐项状态 ⬜ / ⏳ / ✅，⏳ = 已有代码但未收尾或未验证。

## 任务表

| # | 任务 | 状态 | commit | 备注 |
|---|---|---|---|---|
| C-① | 微信登录（session → QR PNG → 轮询 → 存双令牌） | ✅ | `98061ea` | `LoginWindow` 已提交、编译通过 |
| C-② | 机器码展示（installId 主键 + 硬件锚摘要辅证） | ✅ | `7232d39` | 购买窗内的可复制机器码 + 锚摘要；通用页另有一行常驻显示 |
| C-③ | 购买（收款码静态图 + 登记 + 轮询 + 取票） | ⏳ | `7232d39` | `PurchaseWindow` 已写；**待产品方补齐收款码与开发者微信** |
| C-④ | 启动门禁集成（未登录→登录页；已登录未授权→引导购买） | ✅ | 待提交 | `ActivationGate`；**不锁任何功能**，见下 |
| C-⑤ | 轮询退避 + 卖家拒绝/超时提示 | ⏳ | `4ce2420` `d48827f` | `PollSchedule` + 控制器失败分支已写；退避单测已提交（S1），但激活/登录侧单测 10 红（见下「测试真实状态」） |
| C-⑥ | 出口 = `dotnet build/test` 通过 + mock 全流程 | ⬜ | — | build 已绿；**test 10 失败**；mock 全流程未跑 |

## 细目状态（对照 `client-license-gate-progress.zh-CN.md`）

| # | 任务 | 状态 |
|---|---|---|
| 0–9 | 契约 DTO / 传输抽象 / 退避 / 令牌 / 账户 / 激活 / 状态机 / Http 动词 / 惰性装配 | ✅ `4ce2420` |
| 10 | `AccountController`（取票+取图+等扫码合一，取消为终态） | ✅ `d48827f` |
| 11 | `ActivationController`（登记+轮询+取票） | ✅ `d48827f` |
| 12 | 登录窗 UI | ✅ `98061ea` |
| 13 | 购买窗 UI | ✅ 代码完成，⏳ 两处占位符待补 |
| 14 | 机器码展示控件 | ✅ 可复制 TextBox + 复制按钮，嵌在购买窗内；通用页另有常驻一行 |
| 15 | 启动门禁接入 | ✅ `ActivationGate` + `App.xaml.cs` + 通用页 |
| 16 | 新文案进 `en.json` | ✅ 40 条，登录 + 购买 + 两个控制器 |
| 17 | Core 单测（退避 / 时间戳 / 登录 / 激活） | ⏳ 代码已写（3 文件 3156 行未提交），**8红 2 黄**，见下|
| 18 | AppTests：`HttpActivationTransport` handler 注入 | ✅ `ActivationTransportTests.cs` 678 行，**81 测试全绿** |
| 19 | 出口 | ⬜ |

## 门禁不锁任何东西（与任务书措辞不同）

任务 15 写的是「已登录未授权→功能限制模式」，但设计文档自己的注释否掉了这个说法：

- `GateState.SignedIn` 的文档注释：*"The customer can buy; **nothing is locked behind a
  licence they have not paid for**."*
- `LicensingBootstrap.IsShareDeliveryAvailable => true`，恒为 true，有测试守着
- 全仓库 grep `IsPro`：只被兑换窗用来显示「已经是 Pro」，**没有任何 UI 真的受它限制**

所以按「引导购买」而非「上锁」实现。`ActivationGate.Decide` 是纯函数，快照决定开哪一页：

| 状态 | 开什么 |
| --- | --- |
| `Licensed` / `Portable` | 什么都不开 |
| `Anonymous` | 登录页（没账号，登记无从挂靠） |
| `SignedIn` | 购买页 |
| `Rejected` / `LicenseUnverifiable` | 提示联系卖家——这两样登录都修不好 |

两个刻意的选择：

**登录成功后直接接购买页。** 两页回答的是同一个问题（「这怎么买」），刚证明自己有微信的
用户不该还要自己找下一步。

**只主动弹一次。** `AppSettings.ActivationGateShown` 记的是「已经弹过」而不是「拒绝过」
也不是「买过」——一个弹窗第二次出现就不再被阅读，常驻入口改放在通用页。任何 upsell 都
不该在每次启动时重复索取注意力。

**这两条是我的判断，不是文档规定的。** 若产品上要求每次启动都提示，把
`ShowAtStartup` 里读 `ActivationGateShown` 那一行去掉即可。

## 购买窗的流程与两处待补

窗口自己多持有一个 `_confirmed`，用来把收款码挡在「用户看过金额和绑定机器」之后：

```
[1] 待确认   机器码（可复制）· 硬件锚摘要 · 开发者微信 · [确认购买]
[2] 待付款   ← 收款码在这一步才出现，配「转账请备注：<机器码>」
[3] 等待卖家  点「我已付款」→ ActivationController.RegisterAsync() → 退避轮询
[4] 完成 / 拒绝 / 失败
```

**确认 ≠ 登记。** 「确认购买」只让收款码露出来，不发任何请求；真正登记发生在用户点
「我已付款」。所以开窗即关不会在卖家台账上留痕。

两处占位符待产品方补：

| 位置 | 现状 |
|---|---|
| `PurchaseWindow.xaml.cs` 的 `SellerWeChat` 常量 | `（待填写）`。单一来源，同时用作售后联系方式与购买条款 |
| `Assets/PaymentCodePlaceholder.png` | 670 字节占位图，替换为卖家收款码 |

**机器码同时是转账备注。** 卖家那头唯一能把一笔微信转账对上一张许可证的线索就是它，
所以窗内给了独立可复制控件（`GateSmall` 样式 + 等宽字体），并明确提示「没备注的转账
需要人工核对，会慢一些」。

## 测试真实状态（2026-10-20 复核，此前记录有误）

⚠️ **本节推翻上一版「当前状态：绿」的记录。** 下面那份2026-10-18 的全绿表只覆盖到
已提交的代码；S2–S5 的测试写完后**从未被运行过**，补上缺失的括号才第一次真正跑起来。

实测（Perpetual）：

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release -p:WeChatBridgeEdition=Perpetual` | ✅ 0 警告 0 错误 |
| `dotnet test -p:WeChatBridgeEdition=Perpetual`（Tests 工程） | ❌ **失败 10 / 通过 649** |
| `dotnet test -p:WeChatBridgeEdition=Perpetual`（AppTests 工程） | ✅ 通过 81 |
| `Test-NoNetworkCode.ps1 -Edition Perpetual` | 未跑 |

### 括号事故

`AccountServiceTests.cs`缺 `public sealed class AccountServiceTests` 的闭合括号
（125 开 / 124 闭），整个 Tests 工程编译不过——S2–S5 的测试**一个都没跑过**。
补上后 build 转绿，失败才暴露出来。

> 附带纠正：`ActivationServiceTests.cs` 大括号「75/74 不平衡」是统计假象——
> `[InlineData("{ this is not json")]` 里的 `{` 落在字符串字面量内，编译器不视作代码。

### 10 个失败分两类

**A 类·契约探针缺线名（8 个，全在 `ActivationContractTests`）**

`TimestampProbe` / `NullableTimestampProbe` 是裸record，没有
`[property: JsonPropertyName("expiresAt")]`。而 `ActivationJson.Options` 设了
`PropertyNamingPolicy = null` + 大小写敏感，wire 上的 `expiresAt` 匹配不上
`ExpiresAt`，转换器**根本没被调用** → 时间戳全返回 `0001-01-01`，
期望抛 `JsonException` 的三个用例也因此不抛。

受影响：`UnixSecondsAreReadAsAnInstant`、`Rfc3339IsStillRead`、
`QuotedUnixSecondsAreStillUnixSeconds`(×2)、
`SomethingThatIsNotATimestampIsRejectedRatherThanGuessed`(×3)、
`PropertyNamesAreCaseSensitive`。

**这是未提交契约改动（补 14 个 `JsonPropertyName`）的配套测试没跟上**——
生产代码已钉死小写线名，测试探针还是裸 record。属机械修复，风险低。

**B 类·实现与测试期望不符（2 个，`ActivationServiceTests`）——需产品/设计决策**

| 测试 | 期望 | 实现实际 |
|---|---|---|
| `ACancelledCallIsNotSent` | 取消后一个请求都不发 | `ActivationService.SendAsync:303` 只做 linked token，**不预检**；测试替身在 `SendAsync` 里先记请求再 `ThrowIfCancellationRequested`，故记下了请求 |
| `NothingBeforeTheCollectCanEverWriteALicence` | `status.Confirmed == true` | `RegisterAsync:158` 与 `CheckStatusAsync:197` 都返回 `ActivationOutcome.Failed(...)` 再 `with { Confirmed = true }` 补丁 |

这两条**不是补括号能解决的**：要么改生产代码（加取消预检、确认态独立建模），
要么改测试期望。属于 C 分支的设计决策，未擅自改动。

### 修复路径

A 类 8 个是契约改动的机械配套，可直接修；B 类 2 个待决策。修完 A 类预计剩 2 红。

---

## 2026-10-18 的历史记录（已被上节推翻）

当时编译与测试全绿，但范围仅限**已提交**代码：

| 检查 | 结果 |
|---|---|
| `dotnet build -c Release -p:WeChatBridgeEdition=Perpetual` | ✅ 0 警告 0 错误 |
| `dotnet test -p:WeChatBridgeEdition=Perpetual` | ✅ 497 + 45 通过 |
| `dotnet test -p:WeChatBridgeEdition=Subscription` | ✅ 496 + 45 通过（少一条为 edition 条件测试） |
| `Test-NoNetworkCode.ps1 -Edition Perpetual` | ✅ |

修了三处，都是让工作区从红回到绿所必需的：

1. **`LoginWindow.xaml:49-56`** — Border 塞了 `QrImage` 与 `QrPlaceholder` 两个子元素，
   WPF 的 Border 只接受一个。图片与占位文字本就要叠放切换，套一层 Grid。
2. **`LoginWindow.xaml.cs:128`** — `BitmapFrame` 没有 `CanFree`，该守卫不编译。改成
   直接 `Freeze()`：frame 由本方法 `using` 掉的 MemoryStream 构造且 `OnLoad` 已把像素
   读入，没有 freeze 会损坏的共享状态，而 `Freeze` 对已冻结对象本就是空操作。
3. **`LicensingCanonicalPayloadTests.SharedCanonicalVector`** — 见下，这是本次最值得记的一条。

## ⚠️ 跨语言契约踩坑：CRLF 污染字节精确向量

`LicensingCanonicalPayloadTests.TheClientProducesTheSharedVectorExactly` 红了，期望含
`\r\n` 而实际产出 `\n`。

**不是生产代码的错。** 同一文件的 `TheSharedVectorHasTheDocumentedShapeAndDigest` 用
`Vector().Bytes`（生产输出）断言 109 字节与摘要 `0897d8…`，**它一直是绿的**——证明生产端
canonical 与 Go 侧钉死的摘要一致。

真因是 `core.autocrlf=true` 且仓库**没有 `.gitattributes`**：

```
git ls-files --eol → i/lf  w/crlf
```

C# raw string literal 保留文件里的行尾，所以 Windows 检出的字面量每个换行都是 `\r\n`。
这类失败最阴的地方在于**签名照样完全有效、字段照样全解析**，只是客户端永远和服务端对不上。

修法：字面量末尾加 `.ReplaceLineEndings("\n")`，仍然是手写的、不由被测实现生成
（该文件开头的注释明确要求这一点），但不再受检出方式影响。`const` 因此改为
`static ... { get; }`。

教训：**任何钉死字节的向量都不能直接放在源文件字面量里**。要么归一化，要么加
`.gitattributes` 强制 `*.cs text eol=lf`（后者是仓库级决定，需与另两条分支协调）。

## 出口标准

- [ ] `dotnet build` 通过 —— ✅ **已达成**（0 警告 0 错误，2026-10-20）
- [ ] `dotnet test` 通过 —— ❌ **失败 10 / 通过 649**（A 类 8 可修，B 类 2 待决策）
- [ ] 对着 mock 走通：扫码登录 → 机器码展示 → 登记 → 确认 → 取票 → 验签缓存
- [ ] 真服务端签名对下的终态验证（任务 17，走 `AppTests` 已有的 `TestLicenseIssuer`）

## 红线（禁改）

`windows/src/WeChatBridge.Windows.Core/Licensing/` 下 LicenseResolver.cs / LicenseVerifier.cs / InstallIdentityStore.cs / DeviceAnchor.cs 四个文件——契约级冻结，只读复用。

## 阻塞项

无外部阻塞（契约已冻结）。

范围外但影响出口的两项，等机器 A：

- 🔴 `LicensePublicKeys.PlaceholderKeyV1` 仍是占位串，发版前必须换真公钥；未换前一切票据验不过，`IsPro` 恒 false。这是设计上的 fail-closed，不是 bug。
  - **2026-10-20 复核**：A 分支只有导出**机制**（`server/internal/license/license.go:186` `PublicBase64()`），
    仓库内**无任何真公钥**，`server/` 连 `package main` 都没有，
    `WCB_LICENSE_PRIVATE_KEY_PATH`（`config.go:96`）是**无人消费的空配置槽**。
    A 提供机制，没提供物料。
  - 编码兼容性已核查**不是**阻塞：A 输出标准 base64 带 padding，SPKI P-256 为 91 字节 → 124 字符，
    `124 % 4 == 0`；`Base64Url.TryDecode` 不追加 padding，`Convert.FromBase64String` 接受 `+/`。可直接粘贴。
- 🟡 `server/mock/` 不在本分支树上，mock 全流程需借主检出跑：`node /d/WeChatBridge/server/mock/server.js --port 8901`，客户端 `export WECHATBRIDGE_LICENSE_SERVER=http://127.0.0.1:8901/`。

## 本分支功能未被主干覆盖的核查（2026-10-20）

有人问「C 做的功能是不是主干已经有了、那就可以删掉」。**核查结论：不能删。**
扫遍全仓库 20 个分支（本地 + origin + fork），`windows/.../Core/Activation/` 与
`PurchaseWindow`/`LoginWindow`/`Services/ActivationGate.cs` **仅本分支与fork 同名分支命中**。

先分清两条「主干」：

| 目录 | `main` | A `feat/server-convergence` | 本分支 |
|---|---|---|---|
| `Core/Licensing/`（11 文件） | ❌ 0 | ✅ 有 | ✅ |
| `Windows/Redeem/`（4 文件） | ❌ 0 | ✅ 有 | ✅ |
| `server/` | ❌ 0 | ✅ 有 | — |
| `Core/Activation/`（7 文件） | ❌ 0 | ❌ 0 | ✅ |
| `Windows/Activation/`（7 文件） | ❌ 0 | ❌ 0 | ✅ |

**`main` 停在 `12e5647`，整个买断授权体系一行都没进 main**（连基线 `Licensing/`、
`Redeem/` 都没有）。A 与 B 分支同样未合入 main。

按设计文档，A（地基，改 store/migrations）先合主干，C 再 rebase 后合。
下表以 A 为实际开发主干：

| # | 功能 | 本分支 | A 主干 | 状态 |
|---|---|---|---|---|
| C-① | 微信扫码登录（登录窗/二维码/轮询/DPAPI 会话/静默续期） | ✅ 14 文件 | ❌ 0 | **未实现** |
| C-② | 机器码展示（installId + 硬件锚摘要） | ✅ | ❌ 0 | **未实现** |
| C-③ | 购买窗四态 + 收款码 + 卖家微信 | ✅ | ❌ 0 | **未实现** |
| C-④ | 启动门禁（六态决策 + 弹窗引导 + 通用页入口） | ✅ | ❌ 0 | **未实现** |
| C-⑤ | 轮询退避 `PollSchedule` + 失败提示 | ✅ | ❌ 0 | **未实现** |
| — | `server/internal/auth`（6 个 `/v1/auth/*` + 微信 qrconnect） | — | ✅ 7 文件 | 已实现（**对端**，非覆盖） |
| — | `server/internal/activation`（3 端点 + admin 4 端点） | — | ✅ 7 文件 | 已实现（**对端**） |
| — | `server/internal/license`（签发/验签） | — | ✅ 3 文件 | 已实现（**对端**） |

**A 有的是服务端对端，不是 C 的替代品。** 删掉本分支，A 服务端就成了零调用方。
跨分支契约已核查对齐：**9/9 端点、20/20 字段名**（含本分支未提交补的 14 个
`JsonPropertyName`）与 A 的 Go json tag 逐字匹配，激活链路时间戳（Unix 秒）
也对齐。**无契约阻塞。**

两处上报的契约缺陷（`52841b7`）**A 侧均未修**，但都不阻塞激活闭环：

- **4.1 信封时间戳**（Go `int64` ↔ C# 无 converter）：新激活链路已由
  `TimestampConverter` 规避；老兑换码路径（`RedeemService.cs:42-45`）仍坏。
  需拍板「服务端改 wire」还是「客户端加转换器」。
- **4.2 签名字母表**（Go标准 base64 ↔ 客户端 base64url）：实测推理**不会导致随机验签失败**
  ——64 字节 → 88 字符，`88 % 4 == 0`，`Base64Url.TryDecode` 不追加 padding，
  `Convert.FromBase64String` 接受 `+/`。属**规格漂移**（文档写 url、实现是 std），建议收敛但不阻塞。

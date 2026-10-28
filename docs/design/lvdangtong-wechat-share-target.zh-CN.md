# 微信转发菜单增加「律档通」：评估、实施与验收

日期：2026-10-28。目标平台：Windows。目标位置：微信选择消息后的转发弹窗 → 更多／转发到其他应用，与 WorkBuddy 并列。

## 1. 可行性结论与边界

**补充修正：WorkBuddy 还实现了独立的 URL + WM_COPYDATA 专用通道，并非只依赖下述系统枚举 API。** 本机缺少 TransferTargetWatcher 只能确认当前标准枚举路径有系统阻碍，不能推导出所有 Windows 10 接入都不可能或必须升级系统。实际参数和对律档通的限制见 [WorkBuddy 参数核查](../exploration/workbuddy-windows-wechat-share-parameters.zh-CN.md)。

**当前机器无法靠修改本项目的 ShareTarget 注册实现目标：本机是 Windows 10 22H2（19045.6466），缺少微信当前枚举助手使用的 TransferTargetWatcher API。** 在具备该 API 的系统上，Windows `windows.shareTarget` 是现有接入路径，但仍需要实际微信菜单及接收验收，不能把系统升级视为充分保证。详见第 7 节的新证据与操作顺序。

Windows ShareTarget 契约用于注册独立分享目标，微信生成用户选中的聊天归档后，系统激活本项目的 ShareTarget 助手。此路径无需修改 WorkBuddy，也无需将聊天先发送给 WorkBuddy。这不是微信聊天数据库读取接口，只能接收用户主动分享的内容。

项目已经实现接收、ZIP 校验、原子落盘和唤醒主程序；远程主线 a62485b 已使用「律档通」显示名。旧主检出 feat/pg-client-integration 和本机已注册包仍使用旧名称，不能以它们代表最新产品。

**微信 4.2.0.7 的本轮菜单验收已失败。** 用户在新包注册、退出重开微信后确认仍然只有 WorkBuddy，并提供了真实菜单截图。Windows ShareTarget 注册路径可用，不等于已经证明可以进入该微信版本的目标位置。旧记录在微信 4.1.15.13 有成功样本，也有另一机器反复不显示的失败样本。包状态 Ok、身份探针通过、Windows 分享面板可见，均不能替代微信菜单和真实文件接收验收。不得把本文当成腾讯对第三方入口兼容性的承诺。

微软文档：

- https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/integrate-sharesheet-receive
- https://learn.microsoft.com/en-us/windows/apps/develop/windows-integration/receive-shared-data

## 2. 2026-10-28 修改前检查

| 项目 | 结果 |
| --- | --- |
| 操作系统（后续补查确认） | Windows 10 Pro 22H2，Build 19045.6466 |
| 微信 | 4.2.0.7 |
| WorkBuddy 分享包 | WorkBuddyShareTarget，5.9.3.0，Status=Ok |
| 本项目分享包 | WeChatBridge.Windows.ShareTarget，1.0.8.0，Status=Ok |
| 本项目注册指向 | C:\WeChatBridge\windows\artifacts\publish\win-x64 |
| 当前显示名 | 微信流（旧包实际含 U+2060） |
| 接收助手身份探针 | ExitCode=0，AUMID 后缀为 !Share.Hub |
| 分享包检查 | 格式、EXE 身份、内外 PRI、图标全部通过 |
| 微信菜单与真实转发 | 用户反馈只有 WorkBuddy；本轮需要重新验收 |

不要把 `%LOCALAPPDATA%\WeChatBridge\App` 中缺少助手认定为根因：系统当前指向上表中的开发发布目录，该目录中的助手存在。

## 3. 实现原则

1. 仅保留一个 `Application Id="Share.Hub"`，菜单显示名严格为「律档通」。不改 Package Name、Publisher、AUMID、数据目录或已有账户信息。
2. 保留项目现有 `SupportsAnyFileType` 与 `StorageItems / Text / URI / Bitmap` 声明，接收端仍按实际能力校验负载。此配置来自本项目和本机 WorkBuddy 的兼容记录，不宣称为所有微信版本的规则。
3. 使用现有 build-app.ps1、pack-msix.ps1 和 register-dev.ps1，不再新建第二条打包链。
4. 开发注册在替换旧注册前检查分享包资产及助手内嵌身份，注册后检查 Status 和实际进程身份。只有这些检查通过才报告注册成功。
5. 构建脚本必须区分「系统注册成功」与「微信菜单已验收」，不得输出未经验证的完成结论。
6. 不修改、删除或冒充 WorkBuddyShareTarget；不注入微信进程，不复制其包身份。

## 4. 实际操作步骤

### 4.1 获取正确代码并隔离工作区

本机 `origin` 是 freestylefly 上游，`zmuimeng` 才是 zhuimeng2026-hub/WeChatBridge。本机远程名称与 CLAUDE.md 中其他机器的名称不同，以 `git remote -v` 为准。

```powershell
Set-Location C:\WeChatBridge
git status --short
git remote -v
git fetch zmuimeng --prune
git worktree add .worktrees\lvdangtong-wechat-share -b codex/lvdangtong-wechat-share zmuimeng/main
Set-Location C:\WeChatBridge\.worktrees\lvdangtong-wechat-share
```

如果工作区已存在，直接进入它，不重复执行 worktree add。不要切换、清空、暂存或提交主检出的未完成修改。

### 4.2 先保存现有注册信息

以下 Appx 命令必须使用 Windows PowerShell 5.1 (`powershell.exe`)，不能使用 PS7 的 Appx 模块。

```powershell
powershell.exe -NoProfile -Command 'Get-AppxPackage | Where-Object { $_.Name -in "WeChatBridge.Windows.ShareTarget", "WorkBuddyShareTarget" } | Select-Object Name,PackageFullName,PackageFamilyName,Status,InstallLocation | Format-List'
```

记录本项目原注册的 ExternalLocation、旧 MSIX 和 `resources.pri`。更换注册前保留这些文件。分享包升级只替换本项目的注册，不操作 WorkBuddy。

本轮快照保存在隔离工作区的 `.artifacts/share-registration-before.json`，不提交 Git。原产物目录未覆盖，可直接用于回退。

### 4.3 构建与测试

需要 .NET 10 SDK、Windows SDK（makepri.exe、makeappx.exe、mt.exe、signtool.exe）及可用的项目签名证书。已有证书可复用，不要导出私钥或提交任何证书私密材料。

```powershell
dotnet restore windows\WeChatBridge.Windows.sln -p:WeChatBridgeEdition=Perpetual
dotnet build windows\WeChatBridge.Windows.sln -c Release --no-restore -p:WeChatBridgeEdition=Perpetual
dotnet test windows\WeChatBridge.Windows.sln -c Release --no-build -p:WeChatBridgeEdition=Perpetual
powershell.exe -NoProfile -ExecutionPolicy Bypass -File .\build-and-run.ps1 -Edition Perpetual -NoRun
```

新发布目录在当前隔离工作区的 `windows\artifacts\publish\win-x64`。构建输出不能覆盖旧的已注册产物，以便回退。买断版网络命名空间门禁必须通过；build-and-run.ps1 默认会执行它。

### 4.4 注册「律档通」

```powershell
$publishRoot = Join-Path $PWD 'windows\artifacts\publish\win-x64'
powershell.exe -NoProfile -ExecutionPolicy Bypass -File windows\scripts\register-dev.ps1 -InstallRoot $publishRoot -Edition Perpetual
powershell.exe -NoProfile -ExecutionPolicy Bypass -File windows\scripts\Test-SharePackage.ps1 -InstallRoot $publishRoot
& (Join-Path $publishRoot 'share-target\WeChatBridge.ShareTarget.exe') --registration-check
powershell.exe -NoProfile -Command '$p=Get-AppxPackage -Name WeChatBridge.Windows.ShareTarget; $p | Select-Object Name,Version,Status; (Get-AppxPackageManifest -Package $p).Package.Applications.Application.VisualElements | Select-Object DisplayName'
```

也可在初次构建时使用 `build-and-run.ps1 -Edition Perpetual -Register -NoRun` 完成构建、门禁和注册。注册输出必须明确仍需微信验收。

**外部目录是运行时依赖。** 本轮注册使用隔离工作区的发布目录；注册保持期间不要删除该工作区或其产物。今后迁移到正式安装目录时，要带齐主程序、分享助手、资源和匹配 MSIX，并重新注册 ExternalLocation。

### 4.5 微信菜单与接收验收

1. 先保存微信输入框草稿并结束正在进行的操作，再从微信托盘正常退出，重新打开微信。旧记录显示 WeChatAppEx 可能缓存枚举结果；不要在没有保存状态时强制杀进程。
2. 在微信的转发弹窗进入「更多／转发到其他应用」，确认 WorkBuddy 仍在，同时出现独立的「律档通」。仅列出包或在资源管理器分享面板看到它不算通过。
3. 用自己可控的测试聊天记录，选择少量不含隐私的消息，合并转发到「律档通」。不要把真实客户、案件或其他私人聊天用于公开验收。
4. 确认接收窗口出现，`%LOCALAPPDATA%\WeChatBridge\Inbox\Ready` 新增完整批次，原始 ZIP 可读取，主程序能显示记录。
5. 检查 WorkBuddy 入口仍然存在。验证时不选择 WorkBuddy，不将测试之外的聊天发给其他应用。
6. 原始截图和聊天 ZIP 只保存在本地忽略目录，不提交 Git；文档只记录版本、时间、通过项和失败现象。

### 4.6 不显示时的定位顺序

1. 核对注册清单 DisplayName 是否确实为「律档通」、Application Id 是否为 Share.Hub。
2. 运行 Test-SharePackage 和身份探针；检查当前注册 ExternalLocation 是否指向实际存在的新发布目录。
3. 用资源管理器的系统分享面板作对照；若系统可见而微信不可见，记录为微信宿主枚举／缓存兼容性未通过，不继续盲改包名和身份。
4. 采集诊断：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File windows\scripts\Collect-ShareDiagnostics.ps1 -AppRoot $publishRoot -OutputDirectory .artifacts
```

5. 与 `docs/design/windows-share-packaging-fix.md` 的失败记录对照。不能仅凭 `windows/docs/SHARE_TARGET_ENUMERATION.zh-CN.md` 的单一根因说法，断言所有机器问题已解决。

### 4.7 回退

如果新注册不可用，用原签名 MSIX 和原 ExternalLocation 恢复本项目包。因为版本相同，必要时仅移除本项目包再重新注册。不要移除 WorkBuddy，不删除 Inbox、Config、账户或许可证文件。恢复后运行旧助手的身份探针，并由用户重新打开微信。

### 4.8 提交与推送

只暂存本任务的文档和源码；检查 staged diff 无诊断、聊天、签名材料和已有未完成修改。推送独立分支到本机的 zmuimeng 远程，避免把旧主检出分支或上游误推为主线。

```powershell
git diff --check
git diff --cached --stat
git commit -m 'fix(windows): [W-SHARE] verify share registration'
git push -u zmuimeng HEAD:refs/heads/codex/lvdangtong-wechat-share
```

## 5. 本轮结果

具体脚本改动由较低成本的 `gpt-6-luna` 子代理实现，主线程负责评估、复核、构建、签名注册及结果记录。

| 验证 | 结果 |
| --- | --- |
| Perpetual restore / build / test | 0 警告、0 错误；核心 617 + 应用 84 项通过 |
| Subscription restore / build / test | 0 警告、0 错误；核心 616 + 应用 84 项通过 |
| Perpetual 自包含发布 | 主程序与分享助手均生成，版本 1.0.8 |
| 网络命名空间门禁 | 通过（项目现有白名单） |
| Windows PowerShell 5.1 语法解析 | 两份修改的脚本均通过；保留 UTF-8 BOM |
| 真实打包负例 | 使用错误 EXE 身份的助手；预检拒绝，退出码 1，原项目注册和 WorkBuddy 注册保持不变 |
| 预检失败的诊断文本 | 原 trap 的 bare throw 会隐藏为 ScriptHalted；改为 throw $_ 后保留具体错误 |
| 正式本地注册流程 | 签名有效、资源与身份预检通过，注册退出码 0 |
| 新注册显示名 | 严格为「律档通」，没有零宽字符 |
| 新注册状态与进程身份 | Status=Ok；PackageFullName 与 AUMID 匹配，!Share.Hub |
| WorkBuddy 对照 | 包全名及完整注册清单与操作前一致 |
| 微信 4.2.0.7 菜单显示 | 失败；用户确认退出重开后仍只有 WorkBuddy，并提供真实菜单截图 |
| 微信真实聊天 ZIP 接收 | 未验收；不能用上述自动检查替代 |

代码改动仅涉及 `windows/scripts/register-dev.ps1` 与 `build-and-run.ps1`。显示名及接收链路已在远程主线实现，本轮复用，不通过冒充 WorkBuddy 身份解决可见性。

交付分支为 `codex/lvdangtong-wechat-share`，目标远程为本机 `zmuimeng`（GitHub 仓库 `zhuimeng2026-hub/WeChatBridge`）。注册检查及操作文档已在提交 `4b70464` 推送。本分支没有修复用户反馈的微信菜单显示问题，不应作为该问题已解决的版本发布。

## 6. 用户截图明确的两个入口层级

用户于 2026-10-28 提供的截图中，「其他应用」展开后同时存在：

```text
其他应用
├── 腾讯WorkBuddy  推广          ← 用户红箭头指向的项目
└── 选择电脑中的应用 >
    └── WorkBuddy               ← 另一层应用列表
```

这两处不能混为一个入口。Windows ShareTarget 契约是本项目接收系统分享的路径，不能据此断言第三方应用能添加、排序或替换微信自身的「推广」项目。截图能确认界面层级与标签，但不能证明微信内部的列表生成实现。

WorkBuddy 官方更新日志 5.5.0（2026-09-01）记载支持通过 macOS 和 Windows 系统分享入口接收微信聊天记录：https://www.workbuddy.cn/docs/workbuddy/Changelog 。这只能支持系统分享能力存在，不能作为普通第三方可获得微信上层推广位置的证据。

用户随后明确：下方「选择电脑中的应用」子菜单即可，并授权优先处理更容易的路径。因此本任务以该子菜单为目标，不要求与上层推广项并列。

两个层级的技术边界：

1. 上层「腾讯WorkBuddy 推广」是否为微信控制的内置／运营项目，是否存在公开第三方接入渠道；未找到渠道前不能承诺仅修改本项目可实现。
2. 下层「选择电脑中的应用」如何枚举、过滤 Windows 接收应用；当前匹配分享格式、正确包身份和中性名称已实测不足，不能继续作为根因结论。
3. 如果必须进入上层位置，先证明存在合法可用的接入机制，再做对应实现；不冒充 WorkBuddy，不修改或注入微信程序。

原截图含私人联系人信息，仅用于本地核查，不复制进仓库。截图后再次尝试界面捕获仍超时；用户提供的截图和验收反馈是本轮界面事实依据。

## 7. 系统版本阻碍：微信当前枚举助手依赖新 API

### 7.1 可重复验证的事实

1. 本机注册表 `HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion` 的 ProductName 为 Windows 10 Pro，DisplayVersion 为 22H2，CurrentBuild 为 19045，UBR 为 6466。不能继续拿旧文档的 Windows 11 测试环境代表当前机器。
2. 微信插件目录 `WinWeixinShare\2007\extracted\shareSender.exe` 存在且腾讯数字签名有效。只读静态字符串包含 `Windows.ApplicationModel.DataTransfer.TransferTargetWatcher`、`TransferTarget` 和 `TransferTargetDiscoveryOptions`。
3. 同一程序中的明确提示为：`TransferTarget is not supported on this system (requires Windows 11 Build 26100.7015+).`
4. 在当前用户下用 Windows PowerShell 5.1 查询 WinRT 元数据：`TransferTargetWatcher` 不存在，旧 `DataTransferManager` 存在。
5. 微信主程序静态字符串还包括 `RequestCachedShareTargets`、`sharesender_query_failed`、`system_targets_empty`、`system_targets_ready`、`_fallback_private_apps` 等。这些是系统枚举及回退机制的线索，不是对某次执行分支的运行日志证明；不能仅凭这些字符串断言唯一根因或 WorkBuddy 的确切回退规则。

最小复查命令（只读，不启动分享、不读取聊天）：

```powershell
powershell.exe -NoProfile -Command '[pscustomobject]@{ TransferTargetWatcher=[Windows.Foundation.Metadata.ApiInformation,Windows.Foundation,ContentType=WindowsRuntime]::IsTypePresent("Windows.ApplicationModel.DataTransfer.TransferTargetWatcher"); LegacyDataTransferManager=[Windows.Foundation.Metadata.ApiInformation,Windows.Foundation,ContentType=WindowsRuntime]::IsTypePresent("Windows.ApplicationModel.DataTransfer.DataTransferManager") } | ConvertTo-Json -Compress'
```

本机返回：

```json
{"TransferTargetWatcher":false,"LegacyDataTransferManager":true}
```

对腾讯 `shareSender.exe` 做过一次无参数、有时限的启动探针；10 秒内未退出，已终止此次探针进程，没有取得可用于断言的退出结果，不能把它写成运行成功或明确返回系统不支持。

微软官方依据：

- [TransferTargetWatcher API](https://learn.microsoft.com/en-us/uwp/api/windows.applicationmodel.datatransfer.transfertargetwatcher?view=winrt-26100)：属于 Windows 11 24H2 / UniversalApiContract v19。
- [微软 TransferTarget 示例](https://github.com/microsoft/Windows-classic-samples/blob/main/Samples/TransferTarget/VBAScript/README.md)：运行前提明确为 Windows 11 Build 26100.7015 或更高版本。

### 7.2 对可行性的修正

当前微信安装的枚举助手依赖的系统能力，在本机实际不存在。这是已确认的前置阻碍，不能通过重复签名、改中性名称、替换包名或加注册表项补齐。不应把旧式系统 Share Sheet 支持等同于新 TransferTargetWatcher 支持，也不应仅凭“另一台 Windows 11 机器成功”推断 Windows 10 可用。

本项目继续保留原 Windows 10 的运行及分享接收能力；不把包的 MinVersion 全局抬高，不认定所有历史微信版本都只支持 Windows 11。问题限定为当前微信 4.2.0.7 / WinWeixinShare 2007 / 当前主机的目标枚举路径。

### 7.3 实际落地顺序

本节针对现有 Windows ShareTarget 系统枚举路径。另有 WorkBuddy 专用 URL/消息通道，其是否允许第三方登记尚未确认，不能把 Windows 11 说成唯一可行路线。

1. **当前 Windows 10：**运行诊断记录上述 API 缺失，停止反复注册。保留已注册的「律档通」和旧产物作为回退；不能宣称已经完成微信菜单接入。
2. **具备条件的 Windows 11 24H2 或更新机器：**确认已安装包含 TransferTarget 支持的累积更新，且上述能力探针返回 true。不要只看“Windows 11”名称或安装了新 SDK；SDK 不会替系统补齐运行时 API。
3. 在该机器执行第 4.3、4.4 节的构建和注册步骤。正常退出重开微信，进入「其他应用 → 选择电脑中的应用」，检查独立的「律档通」项目。
4. 用自控测试记录验证真实 ZIP 接收，再记录系统完整 Build、微信版本、插件版本和验收结果。API 存在但仍不显示时继续定位，不能预先勾选成功。
5. 不自动升级当前操作系统。若当前机器必须保持 Windows 10，且当前微信没有兼容的系统枚举路径，则仅修改本项目无法保证实现指定菜单入口。既有手动导入/WorkBuddy 收件箱监听属于不同路径，不能作为本需求已经完成的替代证明。

### 7.4 已落实的诊断与验证

由 `gpt-6-luna` 在现有 `windows/scripts/Collect-ShareDiagnostics.ps1` 新增独立的系统枚举 API 检查节，输出实际 Windows Build/UBR、TransferTargetWatcher、TransferTargetDiscoveryOptions 与旧 DataTransferManager 的可用性。没有新增第二条安装链，也没有自动修改系统或提高应用包的最低系统要求。

本轮完整诊断命令：

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File windows\scripts\Collect-ShareDiagnostics.ps1 -AppRoot 'C:\WeChatBridge\.worktrees\lvdangtong-wechat-share\windows\artifacts\publish\win-x64' -OutputDirectory .artifacts
```

实际退出码 0，结果：

```text
WindowsBuild=19045.6466
TransferTargetWatcherPresent=False
TransferTargetDiscoveryOptionsPresent=False
LegacyDataTransferManagerPresent=True
```

Windows PowerShell 5.1 语法及执行通过；现有 LayoutWiringTests 11 项通过，包含中文 PowerShell 文件的 UTF-8 BOM 检查。诊断 ZIP 保存在本地 `.artifacts/WeChatBridge-Diagnostics-20261028-095451.zip`，没有提交进 Git。它只检查安装/注册/程序/系统信息，不收集聊天内容。

结论：注册检查已有实现，菜单问题在当前 Windows 10 主机尚未解决；后续应在具备新 API 的系统继续验收，不能把该诊断改动当作菜单显示修复。

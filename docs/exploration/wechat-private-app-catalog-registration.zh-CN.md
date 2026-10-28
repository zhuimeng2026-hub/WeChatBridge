# 微信私有应用目录与 ShareTarget 登记线索

日期：2026-10-28。静态分析对象：本机微信 4.2.0.7 的 `Weixin.dll`。

## 结论范围

Weixin.dll 4.2.0.7 内有一个包含 Yuanbao、WorkBuddy 的内置应用目录初始化器。当前 `BuildInstalledPrivateApps` 从该目录取候选项，逐项经过可用性检查，成功才加入其结果。getter 的这条路径只按已有 key 覆盖记录；未命中的 override 会被跳过，未看到插入新 key 的控制流。

这说明此处的目录构建不是“扫描 Windows 任意已注册程序并把它们全加入”。它不能证明微信所有其他入口、服务器配置或未来版本都不存在动态新增渠道。律档通当前没有出现在这个已知内置目录中；本机微信菜单验收仍失败，目标尚未实现。

## 文件与复查方式

本机文件：`C:\Program Files\Tencent\Weixin\4.2.0.7\Weixin.dll`，FileVersion/ProductVersion 均为 `4.2.0.7`。

本次核验 SHA-256：

```text
9BEB1051449E768417D770915646815A5BD1899CC2779F758069820794358EE0
```

PE image base 为 `0x180000000`。以下命令只读反汇编指定 RVA 对应的函数范围，不会写入或修改微信文件：

```powershell
$dll = 'C:\Program Files\Tencent\Weixin\4.2.0.7\Weixin.dll'
$objdump = 'C:\mingw64\mingw64\bin\objdump.exe'
Get-FileHash -LiteralPath $dll -Algorithm SHA256
& $objdump -d -Mintel --start-address=0x1822fc2b0 --stop-address=0x1822fc594 $dll
& $objdump -d -Mintel --start-address=0x18652b280 --stop-address=0x18652b8ae $dll
& $objdump -d -Mintel --start-address=0x1822fd0a0 --stop-address=0x1822fd375 $dll
```

本文只记录必要的控制流地址与字符串，不复制腾讯程序的完整反汇编或代码。

## 当前版本中可直接核对的事实

### 内置目录初始化

- 初始化器 VA `0x1822fc2b0–0x1822fc594`（RVA `0x22FC2B0–0x22FC594`）通过线程安全静态初始化构造两个默认应用记录。
- 记录键含 `application_yuanbao` 与 `application_workbuddy`。
- WorkBuddy 记录的可见字面量含 `WorkBuddy`、`WorkBuddy.exe`、`workbuddy://wechat/share`、图标资源、版本 `5.4.0` 和数字标识。
- 目录存入全局对象；`0x1822fd0a0` 是该目录的 getter/合并路径之一。

这能证明本机这个二进制内置了上述记录；不能单凭静态记录断言当前菜单点击一定使用这一路径，也不能确定服务器是否会提供后续数据。

### Getter 对 override 的处理

在 `0x1822fd0a0` 的惰性合并分支中，代码复制 base 目录，然后遍历 override 容器。每项先按 key 在 base map 中查找：

- 未命中时跳过该 override；
- 命中时调用覆盖逻辑更新已有记录；
- 观察到的该分支没有向 map 插入未知 key 的路径。

因此，这个 getter 的已见合并规则是“覆盖已知应用记录”，不是“允许任意新 key 注册”。结论仅限此函数和 4.2.0.7。

### BuildInstalledPrivateApps 的候选筛选

`BuildInstalledPrivateApps` VA `0x18652b280–0x18652b8ae`（RVA `0x652B280–0x652B8AE`）的关键调用顺序：

1. `0x18652b3d4` 调 `0x1822fd0a0`，参数 `CL=1`，取得内置候选目录。
2. 函数遍历返回的目录项。
3. `0x18652b59a` 对每项调用可用性检查函数 `0x1822ff4f0`。
4. 检查返回值后执行 `test al,1`；只有满足条件才调用追加逻辑 `0x180a30140`。
5. 检查不通过的项跳过并继续遍历。

这个函数建立的是“已知目录项中的可用项”列表。它本身没有表现为枚举任意注册程序并把未登记程序纳入目录。

## 安装可用性检查：已知与未知

`0x1822ff4f0` 的可用性检查路径会进入 `0x1822fe090`。后者会调用 `0x1822fe6d0` 复制多个字符串字段，并按 key 检查传入映射；未命中时还调用其他较大的 helper，包括 `0x18062a270`。

这部分内部 helper 尚未完整确认。尤其不能把 `0x18062a270` 仅凭字符串或片段反汇编定性为“读取 Windows 协议注册表”或“查询安装路径”。当前可支持的结论只是目录项经过单项可用性 gate；具体安装检测实现未知。

## 不同接入路径不能混为一谈

- **标准 Windows ShareTarget**：MSIX/Appx 的 `windows.shareTarget` 契约，由 Windows 的分享目标激活机制处理。正确包身份、`Status=Ok`、manifest 与运行时身份探针说明系统注册条件，但不保证微信当前私有目录或菜单出现应用。
- **微信私有应用目录**：当前静态分析发现了已知 key 的目录、override 和可用性筛选。WorkBuddy 在这里是微信内置关联记录，不能当成任何普通第三方仅注册 Appx 或 Scheme 后即可出现的成功范例。
- **应用自己的 Scheme**：注册 `lvdangtong://` 可能帮助应用接收协议启动；它本身不会在上述 getter 中创建新的 `app_key`，也不等同于腾讯将该应用加入目录。

因此，新增独立目录项仍需要对应的目录接入机制。覆盖 WorkBuddy 的身份或 scheme 会影响其原有接收行为，不能作为“保留 WorkBuddy 并新增律档通”的实现。

## 外部交叉参考

[ChatPCBridge README](https://github.com/bwjoke/ChatPCBridge) 记录其 Windows ShareTarget 入口实测环境为 Windows 11 25H2（26200 系列）和微信 4.1.15，并明确不承诺所有 Windows 10/11 组合可用。这是另一项目对标准 Windows 分享路径的特定环境实测，不证明 Windows 10 通用可见，也不证明微信私有目录的第三方登记规则。

当前机器是 Windows 10 Pro 22H2 Build `19045.6466`。腾讯 `shareSender.exe` 的提示和 `TransferTargetWatcher` API 探针另见本次诊断记录；这属于标准系统目标枚举助手的系统要求，不应推广为所有微信版本的唯一接收路径。

## 尚未确认

- 未找到公开的第三方自助登记 API、申请门户或可验证的普通第三方接入流程。
- 未全面逆向微信的远端目录配置、其他进程通信/回退路径或所有 UI 分支。
- 未动态跟踪用户在当前微信菜单的一次点击实际走私有目录还是系统 ShareTarget。
- WorkBuddy 的内置记录是否允许腾讯之外的应用申请新增，以及审批所需材料，均未知。
- 当前律档通未在用户实际微信 4.2.0.7 菜单中显示；真实用户目标仍未实现。

## 建议向腾讯询问的登记信息

以下是待确认字段清单，不代表腾讯存在公开受理流程；本轮不会代用户发送询问：

- 产品显示名：律档通。
- 自有 Windows 可执行文件及用途：ShareTarget helper 与主程序各自的文件名、产品版本。
- 独立 URL Scheme：建议申请/确认 `lvdangtong://`，不覆盖任何现有 Scheme。
- 签名发布者、证书/签名校验方式及版本更新策略。
- 接收能力：Windows ShareTarget、StorageItems、微信生成的 ZIP 归档及运行时校验。
- 所需 Appx Package Name、Application Id 或其他腾讯侧应用标识（如适用）。
- `app_key` 的分配方式、目录元数据格式、安装可用性检查字段及审核/测试流程。

自定义一个 `app_key` 字符串不会使它进入上述目录；需要微信方确认受支持的登记机制及标识要求。登录扫码、微信账号授权或本机包注册成功均不能证明已获得菜单目录接入。

## 后续路径

1. 由腾讯确认是否存在目录登记流程及所需字段；未确认前不继续盲改显示名、manifest 格式或 Appx 身份。
2. 在具有 `TransferTargetWatcher` 的 Windows 11 环境测试标准 ShareTarget 路径，作为独立对照，不声称它解决私有目录登记。
3. 保持 WorkBuddy 与登录/许可证数据原样；不修改微信二进制、不注入或 Hook 微信，也不冒充 WorkBuddy。
4. 只有完成微信菜单人工验收和受控 ZIP 接收后，才记录律档通已显示/已接收。

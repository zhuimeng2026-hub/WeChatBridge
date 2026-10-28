# WorkBuddy Windows 微信转发参数核查

日期：2026-10-28。对象：本机 WorkBuddy 5.9.3，微信 4.2.0.7，Windows 10 Pro 22H2 / 19045.6466。

## 结论

WorkBuddy 已安装程序明确包含两套接收通道：Windows ShareTarget，以及 `workbuddy://wechat/share` 配合 `WM_COPYDATA` 的专用通道。后者不使用 TransferTargetWatcher，因而“本机没有 TransferTargetWatcher”不能推导成“WorkBuddy 无法在本机接收微信转发”或“所有接入都必须升级 Windows 11”。

本轮仅只读检查程序安装文件、注册清单与公开资料，没有触发微信真实聊天分享、没有读取聊天记录、没有更改 WorkBuddy 或微信注册。因此可以确认代码支持的机制与参数，但尚未追踪用户当前点击菜单时实际使用哪条通道。

## 1. 证据来源

- `resources/app.asar` 内的 `main/index.js`：命令行入口解析、微信/企微 URL 解析、Protobuf 编解码、Windows 消息传输、临时文件交付及注册自修复。
- `resources/app.asar.unpacked/native/wechat-copydata-decoder/index.js` 与 `package.json`：原生插件用于解析 Electron 窗口消息中的 COPYDATASTRUCT。
- `share-target/WorkBuddyShareTarget.exe` 的静态字符串：WinRT 分享激活、StorageItems、ZIP 校验、`--wb-share-target=`。
- `share-target/scripts/register.ps1`：安装时注册稀疏 MSIX。
- 当前用户 Appx 注册清单和 `HKCU\Software\Classes\workbuddy`：系统身份、URL 启动配置。
- 微信 `Weixin.dll` 的静态字符串：WorkBuddy 应用目录元数据、`workbuddy://wechat/share`、WorkBuddy.exe，以及系统目标查询与回退标识。

只记录参数语义和必要常量，不将第三方程序代码、原始聊天、真实握手 URL 或诊断日志复制进 Git。

## 2. 标准 Windows ShareTarget 通道

| 参数 | 本机值 / 行为 |
| --- | --- |
| 分享包名 | WorkBuddyShareTarget |
| 分享包版本 | 5.9.3.0 |
| PackageFamilyName | WorkBuddyShareTarget_fv0hcek9q9tbw |
| Application Id | WorkBuddyShareTarget |
| AUMID | WorkBuddyShareTarget_fv0hcek9q9tbw!WorkBuddyShareTarget |
| 接收程序 | share-target\WorkBuddyShareTarget.exe |
| 扩展 | windows.shareTarget |
| 文件类型 | SupportsAnyFileType |
| 数据格式 | StorageItems、Text、URI、Bitmap |
| 运行时验证 | 接收助手要求 StorageItems，查找并复制 ZIP |
| 向主程序交接 | --wb-share-target= 加本地文件路径 |
| 注册最低系统 Build | 19041（来自 WorkBuddy 自修复代码） |
| 注册操作 | Add-AppxPackage -Path <MSIX> -ExternalLocation <安装目录> |
| 安装修复 | 主程序发现包不存在时尝试自修复注册 |
| 安装脚本环境参数 | WB_SHT_INSTDIR、WB_SHT_LOG |
| 注册操作超时 | 60 秒 |

示意交接形式（本轮未执行）：

```text
WorkBuddy.exe --wb-share-target=<本地聊天归档路径>
```

主程序也识别直接传入的已有 `.zip` 文件路径，但这属于应用接收文件的能力，不是向微信菜单登记新项目的 API。

## 3. 专用 URL + WM_COPYDATA 通道

### 3.1 URL 参数与注册

| 参数 | 解析规则 |
| --- | --- |
| Scheme | workbuddy: |
| Host | wechat 或 wecom |
| Path | /share，可接受尾随 / |
| wxsource | chatmsg |
| scene | sessionselect |
| key | 请求关联标识；Windows 请求/回复需匹配 |
| 进程/窗口描述参数 | certify_info、agent_certify_info、app_info、cert，按顺序尝试 |
| 描述参数编码 | Base64 / URL-safe Base64，内容按 Protobuf 解析 |
| URL 启动命令 | "<安装目录>\WorkBuddy.exe" "%1" |
| URL 注册位置 | HKCU\Software\Classes\workbuddy\shell\open\command |

示意 URL（占位值不是实际可用凭据或聊天请求）：

```text
workbuddy://wechat/share?wxsource=chatmsg&scene=sessionselect&key=<请求标识>&certify_info=<Base64进程窗口描述>
```

识别入口接受两类条件：`wxsource` 与 `scene` 均匹配，或者 URL 同时包含 `key` 和某个进程/窗口描述参数。存在进程/窗口描述参数时，Windows 端选择专用消息通道；未走该通道时，代码还保留读取剪贴板 ZIP 的兼容路径。

### 3.2 消息传输常量

| 参数 | 值 |
| --- | --- |
| Windows 消息 | WM_COPYDATA，74 / 0x004A |
| COPYDATASTRUCT.dwData | 4034109776 / 0xF073A150 |
| 发送方法 | SendMessageTimeoutW |
| 超时标记 | SMTO_ABORTIFHUNG = 2 |
| 默认请求超时 | 5000 ms |
| REQUEST_DATA | 1 |
| REPLY_DATA | 2 |
| RESULT_DATA | 3 |
| 自报进程名默认值 | WorkBuddy.exe |
| 自报 appVer 默认值 | workbuddy-dev（程序内常量，不是安装版本） |

AgentCertifyInfo 的 Protobuf 字段：1=processId，2=hwndId，3=processName（bytes），4=appVer（bytes）。

NotifyAgentMsg 的 Protobuf 字段：1=cmd，2=key，3=appInfo，4=path，5=md5。

语义顺序：接收 URL 中的微信端窗口描述 → 建立自身接收窗口消息钩子 → 向微信窗口发送 REQUEST_DATA → 校验 REPLY_DATA 的 cmd、key 和文件路径 → 复制并导入聊天 ZIP → 发送 RESULT_DATA 通知处理结束。消息中的路径由这次分享交互提供，不能把它解释为任意读取微信历史数据库的能力。

源码还有 `WORKBUDDY_WECHAT_HISTORY_WIN_LOOPBACK_URL` / `loopback_url` 的替代传输入口，用于将消息封装成 HTTP `/request`、`/result`；本轮未启用，不把它视为微信菜单接入必需参数或默认公网接口。

## 4. 为什么 Windows 10 上仍然可能显示 WorkBuddy

WM_COPYDATA 是长期存在的 Win32 进程间消息机制，不要求新的 TransferTargetWatcher API。微软文档的最低客户端系统为 Windows 2000：https://learn.microsoft.com/en-us/windows/win32/dataxchg/wm-copydata 。

微信自身还包含 WorkBuddy 的应用元数据：`app_key`、`install_path`、`scheme`、`win_exe_name`、`display_name` 等，并有 `_fallback_private_apps` 等查询回退线索。结合 WorkBuddy 实际实现的专用 URL/消息通道，可以解释它为何具备独立于新系统枚举 API 的接收能力。**这是基于安装代码的机制分析；没有动态跟踪证明用户截图中的一次点击必然走该分支。**

公开产品资料同样说明 WorkBuddy 可从微信接收聊天归档，但没有公开承诺普通第三方可自行加入微信的应用目录：[腾讯产品介绍](https://developer.cloud.tencent.com/article/2760988)。

## 5. 对律档通的意义

后续已继续追踪微信应用目录调用链，详见 [微信私有应用目录登记规则核查](wechat-private-app-catalog-registration.zh-CN.md)。当前版本所查的私有应用候选分支从内置目录取项，再做可用性过滤，未发现扫描 Windows 注册信息新增任意应用的逻辑。

1. 律档通现有 ShareTarget 对应上述第一条通道，尚未实现第二条 WorkBuddy 专用交互。
2. 接收 URL、Protobuf 和 WM_COPYDATA 的技术能力可独立评估，但**实现接收协议与进入微信应用列表是两件事**。
3. 关键未解问题是：微信的自有应用目录是否允许登记律档通的 app_key、安装标识、Scheme 和接收入口。仅在 Windows 注册 `lvdangtong:` 不会自动证明微信菜单会增加它。
4. 不应通过覆盖 workbuddy:、更换 WorkBuddy.exe 或冒充其身份来声称实现“新增并列入口”，那不能满足用户要求保留 WorkBuddy 并新增律档通。
5. Windows 11 是现有标准系统枚举路径的下一种可测试环境，不能再被描述成微信转发的唯一实现路线；专用通道的第三方登记能力仍需单独确认。

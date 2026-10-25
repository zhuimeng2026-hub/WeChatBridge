# 聊天附件分类归档

## 范围与行为

单批次沉淀到文件夹、Obsidian，以及 Windows 批量保存到文件夹，统一生成聊天 Markdown、保留原始输入，并将 ZIP 内附件按类型保存。只有附件、没有聊天 TXT 的 ZIP 同样处理；解析聊天文本失败不再阻断附件提取。普通文件作为附件分类保存。

目录为 `<目标>/<配置子目录>/<聊天名或原文件名>.md` 与 `附件/`。附件目录内使用 `图片/`、`Word文档/`、`电子表格/`、`视频/`、`其他/`、`原始归档/`。只创建实际用到的类别。原始 ZIP 放入原始归档目录，普通文件保留其原始字节并进入对应类别。

分类按大小写无关的最后一个扩展名判断：图片 png/jpg/jpeg/gif/bmp/webp/tif/tiff/heic/heif/avif/svg/ico；Word doc/docx/docm/dot/dotx/dotm/rtf；表格 xls/xlsx/xlsm/xlsb/xlt/xltx/xltm/csv/tsv/ods/et；视频 mp4/mov/m4v/avi/mkv/wmv/flv/webm/mpg/mpeg/3gp/ts；其余进入其他。名称无扩展名或未知类型不猜测内容。

聊天 TXT 只排除已被选作聊天正文的那个 ZIP 条目；其他 TXT 附件照常保存。正文候选必须名为 `聊天记录.txt` 或具有已解析的消息，普通 README/TXT 不当作聊天正文。保持现有同名文件编号、不覆盖已有文件的策略；不同 ZIP 子目录里同名附件必须全部保留，附件清单中不能因字典键重复而丢失。Markdown 保存相对于附件根目录的路径，并列出所有提取附件（含聊天正文未引用的附件），不生成虚构的聊天正文。已有 Markdown 和旧附件保持有效，不迁移旧文件。

## 安全、失败与兼容

沿用 WeChatNativeArchive 的路径、软链接、大小、CRC 等校验，不使用系统无约束解压。遇到损坏或不支持的 ZIP 时保留原件并生成回退说明，不误报已成功提取全部附件。取消操作继续传播；不可把 OperationCanceledException 当作解析失败吞掉。没有 ZIP 的普通附件不尝试当 ZIP 解压。

不改变剪贴板或投递到其他应用的原始文件流程。批量保存到文件夹的说明文案和测试与新行为同步。macOS 同步分类与无正文处理；在 Windows 环境只可做静态检查，实际 Swift 测试留待 macOS 验证。

## 验收

Windows 使用临时目录构造 ZIP 验证四种类别、大小写扩展名、其他类型、无聊天文本、聊天文本与附件、独立文件、嵌套同名附件、重复保存不覆盖、Markdown 所有链接指向实际文件；损坏 ZIP、路径穿越与取消不得削弱现有保护。验证批量入口新行为。运行 Core/AppTests 基础测试与 Release 构建，并记录现有失败与环境限制。

## 实现与验证结果

2026-10-25：Windows 与 macOS 的分类归档、无正文 ZIP 提取、独立附件保存与 Markdown 附件清单已实现。Windows 批量保存入口及中英文说明已同步。代码实现由 gpt-6-luna 子代理完成，主代理负责设计、审核和验收。

审核后修正了以下问题：Office 文档内部也是 ZIP，因此只将 `.zip` 文件作为聊天归档；独立视频等文件不整体载入内存；未新增消息时仍补充新附件清单；生成的清单、警告和追加归档信息不参与消息身份比较，防止重复消息；部分附件提取失败时保留成功保存的清单并明确提示；损坏 ZIP 的回退说明不声称附件提取成功。

本机验证结果：

- `dotnet restore windows/WeChatBridge.Windows.sln -p:WeChatBridgeEdition=Perpetual` 成功。
- Release 解决方案构建成功，0 个警告、0 个错误。
- Windows Core 全量测试 522/522 通过。
- Windows AppTests 全量测试 49/49 通过，包含批量分类归档和缺失目录可重试。
- 最终回退文案及对应断言调整后，KnowledgeDeliveryTests / ObsidianNoteTests 19/19 复跑通过。
- `Test-NoNetworkCode.ps1 -Edition Perpetual` 与 `git diff --check` 通过。
- macOS 代码已静态审核并补充 Swift 回归测试；本机没有可运行 macOS/Xcode 的环境，未执行 Swift 构建或测试。

功能分支：`codex/attachment-categories`。使用独立工作树，不包含原工作区未提交的登录相关改动。

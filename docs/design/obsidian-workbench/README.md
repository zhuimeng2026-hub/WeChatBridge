# WeChatBridge · Obsidian 知识工作台三方案

需求日期：2026-10-22。顺序以实际展示的设计稿为准，编码按 **3 → 2 → 1** 串行。

| 编号 | 方案 | 实施说明 | 适合的展示重点 |
| --- | --- | --- | --- |
| 3 | 阅读优先 | [03-reading-first](03-reading-first/implementation.md) | 原文阅读、选段处理、底部 AI 抽屉 |
| 2 | 案件工作区 | [02-case-workspace](02-case-workspace/implementation.md) | 案件选材、原文与分析草稿对照 |
| 1 | 资料工作台 | [01-document-workbench](01-document-workbench/implementation.md) | 文件树、原文和 AI 助手三栏 |

每个方案单独保存原始参考稿与具体实现方法，三个布局共享同一知识库服务与工作状态，供客户在一个客户端中切换比较。此次实际编码以 Windows WPF 客户端为目标；macOS 保留原有行为。

- [共同技术设计](architecture.md)
- [客户演示流程](customer-demo.md)
- [交付与验证状态](delivery-status.md)

原始设计稿用于视觉目标；原生客户端的现有窗体边框、导航和字体规范优先保持一致。不要把原稿上的 Codex 模型选择框解释成已经配置好 API。

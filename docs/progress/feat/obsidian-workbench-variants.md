# feat/obsidian-workbench-variants

Owner: Codex; implementation delegated to gpt-6-luna at the user’s request.

| Task | Status | Evidence |
| --- | --- | --- |
| WB-0 preserve three references and implementation plans | ✅ complete | c3c8a3f |
| WB-3 reading-first + shared vault service | ✅ source implemented | 7ac08aa |
| WB-2 case workspace | ✅ source implemented | 905860c |
| WB-1 document workbench | ✅ source implemented | 4477af9 |
| WB-QA CI and screenshot workflow | ✅ passed | run 39777985455; source 5ab4785 |
| WB-QA native visual comparison | ⏳ native layout reviewed; full mock fidelity blocked | design-qa.md |
| Remote push / CI start | ✅ complete | user approved; published via connector; run 39776531598 |
| L-1 `en.json` 缺 `筛选失败：{0}` | ⬜ | `WorkbenchPane.xaml.cs:408` |
| L-2 `SaveFolderBox.Text` 硬编码 `分析` | ⬜ | `WorkbenchPane.xaml.cs:60` |
| L-3 下拉项 `Content="其他"` 未本地化 | ⬜ | `WorkbenchPane.xaml:163` |
| V-1 富文本 / 聊天气泡渲染 | ⬜ | `design-qa.md` |
| V-2 应用内模型回复流 | ⬜ | `design-qa.md` |
| V-3 附件内嵌预览 | ⬜ | `design-qa.md` |

All dependency installs, builds and tests are reserved for GitHub Actions per user preference. Source review only in this environment. Public branch now exists; both self-contained review builds and evidence artifacts are available from run 39777985455.

## CI round 1

2026-10-22: user approved the public push. Branch publication succeeded through the GitHub connector; run 39776282888 found missing System.IO imports in the WPF pane. Added the explicit import; awaiting the next CI run. The earlier publication block is resolved.

## CI round 2 and screenshot review

Run 39776531598 passed both editions: Perpetual 546 Core + 49 App tests, Subscription 545 Core + 49 App tests. Both edition checks and self-contained builds succeeded. Initial native screenshots exposed missing pane-level font inheritance and stretched toolbar buttons. The next source commit fixes those display issues and adds 1000x700 captures plus request/provenance assertions. Awaiting the final source CI run.

## Final source verification

Run 39777985455 passed both editions and produced customer review builds plus 12 pane captures per edition (3 layouts × 2 viewports × 2 languages). Request export and saved provenance assertions passed. Full mock fidelity remains explicitly blocked for the documented plain-Markdown/external-model scope; native layout/controls were reviewed. Documentation-only closeout is marked [skip ci].
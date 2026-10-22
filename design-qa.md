# Obsidian workbench design QA

final result: blocked

## Scope and outcome

Windows build/tests and native layout review passed for the current implementation. Full fidelity to the three original generated concepts has not passed. This is a functional native review build, with the previously documented plain-Markdown reader and clipboard-based external-model workflow.

## Evidence and normalization

- Source: `docs/design/obsidian-workbench/{03-reading-first,02-case-workspace,01-document-workbench}/reference.png`, each 1487×1058.
- Final source revision: 5ab478582a2c0b02f6f45fee4ee94a2763f77643.
- Final CI: [https://github.com/zhuimeng2026-hub/WeChatBridge/actions/runs/39777985455](https://github.com/zhuimeng2026-hub/WeChatBridge/actions/runs/39777985455); Perpetual 546 Core + 49 App tests, Subscription 545 Core + 49 App tests, all passed. Both edition gates and self-contained publishes passed.
- Captures: Artifacts `workbench-evidence-Perpetual` and `workbench-evidence-Subscription`, `ui-preview/workbench-{reading-first,case-workspace,document-workbench}[-compact]-{zh-Hans,en}.png`.
- Viewports: 1488×1056 at 96 DPI, and 1000×700 at 96 DPI. Source concepts were resized by 1–2 px only to align the full-view comparison canvas.
- Compared every source and final Chinese pane capture together in side-by-side inputs. Compact Chinese captures were also inspected individually. These captures are native panes, while source images show a complete concept window; existing Windows shell/navigation is outside the pane capture. Sample message content differs, so no pixel identity or copy identity is claimed.

## Fixed during review

- [P2, fixed] Pane screenshots inherited default text instead of the Window typography. Set Microsoft YaHei UI / Segoe UI explicitly, 14 px base / 15 px body, layout rounding and ClearType. Final screenshots show readable body text.
- [P2, fixed] Top toolbar buttons stretched to the full header height. Centered controls with compact minimum height. Final large and compact captures show compact controls.
- [P2, fixed] Side assistant header risked label/tool collisions. Reflowed it into two rows, wrapped explanatory text, and kept the footer in compact rows.
- [P2, fixed] Search was an unlabeled empty box. Added a visible empty-state hint and retained its tooltip.

## Remaining fidelity gaps

- [P1] Rendered transcript and analysis presentation. Concepts show chat bubbles/avatars, rich headings, inline source chips and richer answer presentation. Current reader and result editor display Markdown text. Implement a rendered transcript/Markdown view with source anchors to close this gap.
- [P1] In-app model response flow. Current explicit copy/paste workflow is functional and tested; API configuration, streaming responses and automatic return to the pane are not implemented. Keep the UI notice until an actual provider flow is built.
- [P2] Inline attachments. Current workbench discovers exported wiki-link attachments and reveals them in Explorer; the original concepts include richer PDF/image previews. Add those previews before full concept acceptance.

## Required surfaces

- Typography: corrected native inheritance/body size and checked readable Chinese captures; complete source typography parity is not claimed.
- Spacing/layout: three distinct compositions and compact primary controls inspected. Native pane and full concept window have different shell framing.
- Colors/tokens: retained existing gray-green application tokens and green save action; no claim of full accessibility contrast certification.
- Assets: original references preserved unchanged. Rendered avatars/preview assets from the concepts are not implemented in the text-first pane.
- Copy/content: demonstration and pasted results remain labeled; no template is advertised as an actual model response. CI fixtures are fictional; source and fixture prose differ.

## Completion gate

The branch may be reviewed as native implementation source and CI output. Full Product Design visual handoff remains blocked until the above fidelity gaps are implemented and new screenshots are compared. No automatic approval/publication block remains.

# feat/obsidian-workbench-variants

Owner: Codex; implementation delegated to gpt-6-luna at the user’s request.

| Task | Status | Evidence |
| --- | --- | --- |
| WB-0 preserve three references and implementation plans | complete | 9053dd9 |
| WB-3 reading-first + shared vault service | source implemented; runtime validation pending | this commit; Windows source reviewed statically |
| WB-2 case workspace | pending | follows WB-3 checkpoint |
| WB-1 document workbench | pending | follows WB-2 checkpoint |
| WB-QA CI and screenshot workflow | prepared; not executed | .github/workflows/workbench-review.yml |
| WB-QA native visual comparison | blocked | design-qa.md |
| Remote push / CI start | blocked | automatic review rejected public branch publication; awaiting explicit user approval after local work |

All dependency installs, builds and tests are reserved for GitHub Actions per user preference. Source review only in this environment. No public branch or review build is claimed to exist.

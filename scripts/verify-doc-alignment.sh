#!/usr/bin/env bash
#
# 文档与代码实况的一致性校验器。
#
# 设计约束（决定了它为什么能挂定时任务无人值守跑）：
#   · **只读**：不写仓库、不改分支、不联网、不删任何东西。
#   · **零依赖**：POSIX sh + git + grep，不装包。
#   · **退出码有意义**：0 = 全绿；1 = 有 FAIL；2 = 用法错误。
#   · **幂等**：跑一万次结果一样。
#
# 它查的不是「文档好不好看」，而是**文档里那些可被代码证伪的具体断言**——
# 看板引用的 commit 是不是 main 的祖先、看板提到的文件是不是真的存在、
# CLAUDE.md 写死的计数是不是还等于实测值。看板最大的失败模式不是写得难看，
# 而是**看起来还活着、实际上那行代码早就没了**。
#
# 用法：
#   scripts/verify-doc-alignment.sh              # 人看的报告
#   scripts/verify-doc-alignment.sh --quiet      # 只报 FAIL/WARN
#   scripts/verify-doc-alignment.sh --report FILE # 额外写一份报告
#
# 退出码：0 全绿 / 1 有 FAIL / 2 用法错误
#
# ⚠️ 实现约束：**不要用 [一二三四五六七八九十] 这类多字节字符类**。
#    Git Bash 的默认 locale 不是 UTF-8 感知的，字符类会退化成按字节匹配，
#    抽出的是乱码而不是「八」。凡是要匹配中文，一律用固定字符串（grep -F）
#    或逐个字面量比对。这一点本身也踩过一次坑，值得写下来。
#
# 历史：2026-10-23 随「文档对齐」这项工作落盘。清单见
#       docs/progress/doc-alignment.md

set -u

QUIET=0
REPORT=""
while [ $# -gt 0 ]; do
  case "$1" in
    --quiet)  QUIET=1 ;;
    --report) shift; REPORT="${1:-}" ;;
    -h|--help) sed -n '2,40p' "$0"; exit 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

REPO="$(cd "$(dirname "$0")/.." && pwd)"
cd "$REPO" || exit 2

FAIL=0
PASS=0
WARN=0

say() { # say <LEVEL> <check-id> <message>
  local level="$1" id="$2"; shift 2
  case "$level" in
    FAIL) FAIL=$((FAIL + 1)) ;;
    WARN) WARN=$((WARN + 1)) ;;
    *)    PASS=$((PASS + 1)) ;;
  esac
  if [ "$QUIET" -eq 1 ] && [ "$level" = "PASS" ]; then return; fi
  printf '%-4s %-8s %s\n' "$level" "$id" "$*"
}

have() { command -v "$1" >/dev/null 2>&1; }

if ! have git; then
  echo "FATAL  git not found on PATH" >&2
  exit 2
fi

MAIN_REF="${MAIN_REF:-main}"

echo "=== 文档与代码实况一致性校验 ==="
echo "repo : $REPO"
echo "main : $(git rev-parse --short "$MAIN_REF" 2>/dev/null || echo '<missing>')"
echo

# ─────────────────────────────────────────────────────────────────────────────
# C1  CLAUDE.md 写死的计数是否还等于实测值
# ─────────────────────────────────────────────────────────────────────────────

# C1.1 origin 上的远端分支条数（symbolic 的 origin/HEAD 不算一条分支）
if git rev-parse --verify --quiet origin/main >/dev/null; then
  origin_branches="$(git for-each-ref --format='%(symref)' refs/remotes/origin/ \
                    | grep -c '^$')"
  if [ "$origin_branches" -eq 2 ]; then
    say PASS C1.1 "origin 远端分支数 = 2（与 CLAUDE.md 的陈述一致）"
  else
    say FAIL C1.1 "origin 远端分支数 = $origin_branches，CLAUDE.md 写的是 2 —— 数字漂了"
  fi

  # C1.2 origin 上是否还挂着已归档的旧分支
  stale_origin="$(git for-each-ref --format='%(refname:short)' refs/remotes/origin/ \
                  | grep -E 'server-convergence|client-license-gate|admin-console|windows-portable-usb' || true)"
  if [ -z "$stale_origin" ]; then
    say PASS C1.2 "origin 上没有已归档分支的残留 tracking ref"
  else
    say FAIL C1.2 "origin 上仍残留已归档分支：$(echo "$stale_origin" | tr '\n' ' ')"
  fi
else
  say WARN C1.1 "没有 origin/main 的本地引用，跳过远端分支检查"
fi

# C1.3 upstream/main 是否已完全包含在 main 里
#
# 这里**不查具体数字**。领先多少个提交每推一笔就变，CLAUDE.md 里写死一个数
# 只会让这个检查天天报漂移——那是噪音不是信号。要查的是那个不会变的事实：
# upstream/main 是不是已经整个被 main 吃进去了（反向计数为 0）。
if git rev-parse --verify --quiet upstream/main >/dev/null; then
  counts="$(git rev-list --left-right --count "$MAIN_REF"...upstream/main)"
  behind="$(echo "$counts" | awk '{print $1}')"
  ahead="$(echo "$counts" | awk '{print $2}')"
  if [ "$ahead" = "0" ]; then
    say PASS C1.3 "upstream/main 已完全包含在 $MAIN_REF 里（领先 $behind 个提交）"
  else
    say FAIL C1.3 "upstream/main 还有 $ahead 个提交不在 $MAIN_REF 里——fork 与上游已经分叉"
  fi
else
  say WARN C1.3 "没有 upstream/main 的本地引用，跳过包含关系检查"
fi

# C1.4 看板块数（用固定字符串逐个字面量比，避开多字节字符类）
board_count="$(git ls-tree --name-only "$MAIN_REF" -- docs/progress/ | grep -c '\.md$')"
claimed_boards=""
for w in 一 二 三 四 五 六 七 八 九 十 十一 十二; do
  if grep -qF "看板目录在" CLAUDE.md && grep -qF "有${w}块" CLAUDE.md; then
    claimed_boards="$w"
    break
  fi
done
case "$claimed_boards" in
  一) n=1 ;; 二) n=2 ;; 三) n=3 ;; 四) n=4 ;; 五) n=5 ;; 六) n=6 ;;
  七) n=7 ;; 八) n=8 ;; 九) n=9 ;; 十) n=10 ;; 十一) n=11 ;; 十二) n=12 ;; *) n=-1 ;;
esac
if [ "$n" -lt 0 ]; then
  say FAIL C1.4 "CLAUDE.md 里找不到「看板目录在 main 上有 N 块」的表述"
elif [ "$board_count" -eq "$n" ]; then
  say PASS C1.4 "main 上看板块数 = $board_count（与 CLAUDE.md 一致）"
else
  say FAIL C1.4 "main 上看板块数 = $board_count，CLAUDE.md 写的是 $n"
fi

# ─────────────────────────────────────────────────────────────────────────────
# C2  归档 tag：区分「已并入 main」与「只是被 tag 钉住」
#
#     这条最容易踩。看到 archive/xxx 就以为成果进了 main，会漏掉整整一块工作。
# ─────────────────────────────────────────────────────────────────────────────

for t in $(git tag -l 'archive/*'); do
  if git merge-base --is-ancestor "$t" "$MAIN_REF" 2>/dev/null; then
    say PASS C2 "归档 tag $t 的内容已在 $MAIN_REF 里"
  else
    say WARN C2 "归档 tag $t 钉住的内容**从未并入 $MAIN_REF** —— 成果只存在于 tag 里"
  fi
done

# ─────────────────────────────────────────────────────────────────────────────
# C3  看板「任务表」里引用的证据 commit 是不是 main 的祖先
#
#     只看任务表行（`| A-① |` / `| 17 |` 这种），不看正文里提到的历史提交
#     ——正文里有一张「被覆盖的对方提交」表，那些 SHA 本来就不在 main 上，
#     是有意保留的取证材料，不是漂移。
# ─────────────────────────────────────────────────────────────────────────────

for board in $(git ls-tree --name-only "$MAIN_REF" -- docs/progress/ | grep '\.md$'); do
  base="$(basename "$board")"
  hashes="$(mktemp)"
  git show "$MAIN_REF:$board" 2>/dev/null \
    | grep -E '^\| ([A-Z]-|[0-9]+ \|)' \
    | grep -oE '\b[0-9a-f]{7,40}\b' \
    | sort -u > "$hashes" 2>/dev/null || true
  while read -r h; do
    [ -n "$h" ] || continue
    git cat-file -e "$h^{commit}" 2>/dev/null || continue
    if ! git merge-base --is-ancestor "$h" "$MAIN_REF" 2>/dev/null; then
      say FAIL C3 "$base 任务表引用的 commit $h 不在 $MAIN_REF 的历史里"
    fi
  done < "$hashes"
  rm -f "$hashes"
done

# ─────────────────────────────────────────────────────────────────────────────
# C4  看板里提到的文件路径是不是真的存在
# ─────────────────────────────────────────────────────────────────────────────

for board in $(git ls-tree --name-only "$MAIN_REF" -- docs/progress/ | grep '\.md$'); do
  base="$(basename "$board")"
  paths="$(mktemp)"
  git show "$MAIN_REF:$board" 2>/dev/null \
    | grep -oE '`(windows|server|macos|shared|admin-console)/[A-Za-z0-9_./-]+\.(cs|go|swift|js|ps1|json|md)`' \
    | tr -d '`' | sort -u > "$paths" 2>/dev/null || true
  while read -r p; do
    [ -n "$p" ] || continue
    case "$p" in *'…'*|*'...'*) continue ;; esac
    if ! git cat-file -e "$MAIN_REF:$p" 2>/dev/null; then
      say FAIL C4 "$base 提到的主干文件不存在：$p"
    fi
  done < "$paths"
  rm -f "$paths"
done

# ─────────────────────────────────────────────────────────────────────────────
# C5  未完成行（⬜ / ⏳ / 🔴）是否与「实际缺口」对得上
# ─────────────────────────────────────────────────────────────────────────────

if git cat-file -e "$MAIN_REF:windows/tests/WeChatBridge.Windows.AppTests/ActivationTransportTests.cs" 2>/dev/null; then
  say PASS C5.1 "S5 缺口已补：ActivationTransportTests.cs 在主干上"
else
  say WARN C5.1 "S5 缺口仍在：主干上没有 ActivationTransportTests.cs（看板的 ⬜ 是对的）"
fi

if [ -d "windows/src/WeChatBridge.Windows.Core/Redeem" ] || [ -d "windows/src/WeChatBridge.Windows/Redeem" ]; then
  say FAIL C5.2 "windows/src 下仍有 Redeem/ 目录，与「兑换码已退役」的记载矛盾"
else
  say PASS C5.2 "windows/src 下无 Redeem/ 目录，与记载一致"
fi

if git show "$MAIN_REF:server/cmd/wcb/main.go" 2>/dev/null | grep -q 'AdminEnabled()'; then
  say PASS C5.3 "cmd/wcb 里 admin 路由按 AdminEnabled() 条件挂载"
else
  say FAIL C5.3 "cmd/wcb 里找不到 admin 路由的条件挂载逻辑"
fi

# C5.4 「买断即全功能五枚 entitlement」的决策缺口（见清单 D-3）
if git show "$MAIN_REF:server/internal/admin/admin.go" 2>/dev/null | grep -q 'EntitlementPro'; then
  only_pro=1
  for e in EntitlementPdf EntitlementExport EntitlementScene EntitlementHistory; do
    git show "$MAIN_REF:server/internal/admin/admin.go" 2>/dev/null | grep -q "$e" && only_pro=0
  done
  if [ "$only_pro" -eq 1 ]; then
    say WARN C5.4 "admin 仍只发 EntitlementPro 一枚；「买断即全功能五枚」的裁决尚未登记（清单 D-3）"
  else
    say PASS C5.4 "admin 已发多枚 entitlement"
  fi
fi

# ─────────────────────────────────────────────────────────────────────────────
# C6  仓库现状类文档不得把已删除的 worktree 当成还在用
#
#     这里只能做到「提醒」：文档里**提到**一个已删的 worktree 常常是应该的
#     （历史记录、写反了的协议文档、步骤定义里的旧文）。所以这不是 FAIL——
#     它要人看一眼，确认那些提及是在说「已删除」而不是「照着它操作」。
#
#     工具目录本身排除在外：步骤定义里必然含有旧文。
# ─────────────────────────────────────────────────────────────────────────────

for ghost in 'D:/WeChatBridge-C' 'D:\WeChatBridge-C' 'D:/WeChatBridge-B' 'D:\WeChatBridge-B'; do
  hits=""
  for f in $(git ls-files); do
    case "$f" in
      scripts/verify-doc-alignment.sh|scripts/apply-doc-alignment.sh) continue ;;
      scripts/doc-alignment/*) continue ;;
      docs/progress/doc-alignment.md) continue ;;
    esac
    if git show "$MAIN_REF:$f" 2>/dev/null | grep -qF "$ghost"; then
      hits="$hits $f"
    fi
  done
  if [ -z "$hits" ]; then
    say PASS C6 "没有跟踪文件提到已删除的 worktree $ghost"
  else
    say WARN C6 "以下文件提到已删除的 worktree $ghost —— 确认它们是在说「已删除」而不是「照着操作」：$hits"
  fi
done

# ─────────────────────────────────────────────────────────────────────────────
# C7  知识工作台的三处本地化漏项
#
#     这一组查的是 **main**：知识工作台的代码 2026-10-24 已随分支合并进主干，
#     所以缺陷修在 main 上。查一条已经没人再跑的分支，等于拿一份旧副本当真相——
#     修复在主干，检查却对着一份没人再跑的代码报 WARN，既看不见真正的回归，
#     也拦不住任何东西。
# ─────────────────────────────────────────────────────────────────────────────

if git rev-parse --verify --quiet "$MAIN_REF" >/dev/null; then
  wb_pane="$MAIN_REF:windows/src/WeChatBridge.Windows/Panes/WorkbenchPane.xaml.cs"
  wb_xaml="$MAIN_REF:windows/src/WeChatBridge.Windows/Panes/WorkbenchPane.xaml"
  wb_en="$MAIN_REF:windows/src/WeChatBridge.Windows.Core/Localization/en.json"

  # 这三条曾经是**代码缺陷**，不是文档缺陷——它们要改的是 WorkbenchPane，不是这份
  # 文档。所以记 WARN 而不是 FAIL：文档对齐这项工作本身不负责修它们，
  # 它们在知识工作台自己的看板上以 ⬜ 挂着（见 W-01、W-02）。
  if git show "$wb_en" 2>/dev/null | grep -qF '筛选失败：{0}'; then
    say PASS C7.1 "en.json 已含「筛选失败：{0}」"
  else
    say WARN C7.1 "en.json 缺「筛选失败：{0}」——英文界面会漏中文（WorkbenchPane.xaml.cs:408）"
  fi

  if git show "$wb_pane" 2>/dev/null | grep -qF 'SaveFolderBox.Text = "分析";'; then
    say WARN C7.2 "SaveFolderBox.Text 仍硬编码中文「分析」（未走 L10n.Text）"
  else
    say PASS C7.2 "SaveFolderBox.Text 已本地化"
  fi

  if git show "$wb_xaml" 2>/dev/null | grep -qF 'ComboBoxItem Content="其他"'; then
    say WARN C7.3 "WorkbenchPane.xaml 的 ComboBoxItem 仍硬编码 Content=其他"
  else
    say PASS C7.3 "下拉项「其他」已本地化"
  fi
else
  say WARN C7 "$MAIN_REF 不存在，跳过知识工作台本地化检查"
fi

# ─────────────────────────────────────────────────────────────────────────────
# 汇总
# ─────────────────────────────────────────────────────────────────────────────

echo
echo "=== 汇总：PASS $PASS / WARN $WARN / FAIL $FAIL ==="

if [ -n "$REPORT" ]; then
  {
    echo "# 文档一致性校验报告"
    echo
    echo "- 时间：$(date '+%Y-%m-%d %H:%M:%S')"
    echo "- 仓库：$REPO"
    echo "- main：$(git rev-parse --short "$MAIN_REF" 2>/dev/null)"
    echo "- 结果：PASS $PASS / WARN $WARN / FAIL $FAIL"
  } > "$REPORT"
  echo "报告已写入 $REPORT"
fi

[ "$FAIL" -eq 0 ] || exit 1
exit 0

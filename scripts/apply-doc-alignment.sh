#!/usr/bin/env bash
#
# 文档对齐执行器：按步骤表把文档改成与代码实况一致。
#
# ── 为什么是确定性改写，不是让模型自由发挥 ─────────────────────────────────
# 清单里的每一条都是「找什么 → 改成什么」的精确替换。凌晨无人值守跑的时候，
# 确定性脚本有三个模型替代不了的性质：
#   · **幂等**：跑第二次是空操作，不会把文档改坏。
#   · **可验收**：每条都自带一条 VERIFY 命令，改完当场验，验不过就回滚。
#   · **不烧额度**：整个夜间任务零 LLM 调用，失败也不会有「额度用光」这种
#     与任务本身无关的失败原因。
# 一条都做不动时，标 🔴 并**继续下一条**——不让一条坏步骤堵死后面所有步骤。
#
# ── 步骤表格式 ─────────────────────────────────────────────────────────────
# 每步一个文件，`scripts/doc-alignment/steps/<ID>.step`：
#
#   ID:      D-01
#   TARGET:  main                    # 目标分支（决定跑在哪个 worktree）
#   FILE:    docs/progress/xxx.md    # 要改的文件（相对仓库根）
#   OP:      prepend | append | replace
#   VERIFY:  grep -qF '...' docs/progress/xxx.md
#   COMMIT:  docs(client): xxx
#   ---BEGIN---
#   ……正文……
#   ---END---
#
#   OP=prepend  把正文插到文件首行（标题行）之后
#   OP=append   把正文追加到文件末尾
#   OP=create   新建文件（文件已存在就报错，不覆盖）
#   OP=replace  正文里用一行 ---SPLIT--- 分成「旧文」与「新文」两段，
#               整段精确替换；旧文必须**恰好出现一次**，否则报错回滚
#
# ── 用法 ───────────────────────────────────────────────────────────────────
#   scripts/apply-doc-alignment.sh --target main
#   scripts/apply-doc-alignment.sh --target main --dry-run
#   scripts/apply-doc-alignment.sh --target main --steps D-01,D-04
#
# ── 硬约束 ─────────────────────────────────────────────────────────────────
#   · 只改 FILE 里写死的那一个文件，绝不碰别的。
#   · 一步一个 commit，commit message 带步骤号（仓库约定：`git log` 即进度流水）。
#   · 看板状态表与文档改动**同一个 commit**（CLAUDE.md 的规矩：看板与代码同一个 commit）。
#     实现方式是 amend：先提交，再用真实 hash 回填状态表，再 amend 一次。
#   · 不联网、不 push、不删分支、不改 git 配置。
#   · VERIFY 不过 → `git checkout --` 回滚该文件，标 🔴，继续下一条。
#
# 历史：2026-10-23 落盘。清单见 docs/progress/doc-alignment.md

set -u

REPO="$(cd "$(dirname "$0")/.." && pwd)"
STEPDIR="$REPO/scripts/doc-alignment/steps"
RESUME="$REPO/docs/progress/doc-alignment.resume.md"

TARGET=""
DRYRUN=0
ONLY=""
while [ $# -gt 0 ]; do
  case "$1" in
    --target) shift; TARGET="${1:-}" ;;
    --dry-run) DRYRUN=1 ;;
    --steps)  shift; ONLY="${1:-}" ;;
    -h|--help) sed -n '2,45p' "$0"; exit 2 ;;
    *) echo "unknown argument: $1" >&2; exit 2 ;;
  esac
  shift
done

if [ -z "$TARGET" ]; then
  echo "用法：$0 --target <branch> [--dry-run] [--steps D-01,D-04]" >&2
  exit 2
fi
[ -d "$STEPDIR" ] || { echo "找不到步骤目录 $STEPDIR" >&2; exit 2; }
[ -f "$RESUME" ] || { echo "找不到状态表 $RESUME" >&2; exit 2; }

cd "$REPO" || exit 2

current_branch="$(git rev-parse --abbrev-ref HEAD 2>/dev/null || echo '<none>')"
if [ "$current_branch" != "$TARGET" ]; then
  echo "FATAL  当前分支是 $current_branch，不是 $TARGET" >&2
  echo "       执行器只在目标分支已检出的 worktree 里跑，不会自己切分支。" >&2
  exit 2
fi

if ! git diff --quiet 2>/dev/null || [ -n "$(git status --porcelain --untracked-files=no)" ]; then
  echo "FATAL  工作区不干净。执行器要求干净起点——脏工作区下回滚会误伤别人的改动。" >&2
  git status --short >&2
  exit 2
fi

PASSED=0
FAILED=0
SKIPPED=0
DRYTAG=""
[ "$DRYRUN" -eq 1 ] && DRYTAG=" (dry-run)"

log() { printf '%s %s\n' "$(date '+%H:%M:%S')" "$*"; }

# ── 读一个 .step 文件的字段 ────────────────────────────────────────────────
#
# ⚠️ 必须去掉行尾的 \r。仓库 core.autocrlf=true 且没有 .gitattributes，
#    工作区里所有文件都是 CRLF；字段尾部挂着一个 \r 的话，
#    `eval "$verify"` 会把文件名变成 "file\r" —— 命令看起来对，结果永远匹配不上。
step_field() { # step_field <file> <KEY>
  sed -n "s/^$2:[[:space:]]*//p" "$1" | head -1 | tr -d '\r'
}

step_body() { # step_body <file>  —— 取 ---BEGIN--- 与 ---END--- 之间的正文
  # ⚠️ 先剥掉行尾的 CR 再匹配。`^---BEGIN---$` 这种行尾锚点在 CRLF 上匹配
  #    不上——步骤定义文件在 Windows 上 checkout 出来就是 CRLF（仓库当时
  #    没有 .gitattributes），正文于是整段取空，后面每一步都找不到旧文。
  awk '{ sub(/\r$/, ""); print }' "$1" \
    | awk '/^---BEGIN---$/{on=1;next} /^---END---$/{on=0} on'
}

# ── 行尾归一 ───────────────────────────────────────────────────────────────
#
# 见下面「行尾」那一节的说明：匹配与拼接全程在 LF 空间里做，最后按目标文件
# 的原约定一次性写回。这里不再有「把正文归一成目标约定」这一步——那正是第一版
# 的错误所在。

# ── 状态表读写 ─────────────────────────────────────────────────────────────
# 状态表行格式： | D-01 | 说明 | ⬜ | 备注 |
#
# ⚠️ 取状态列**不要用 [⬜⏳✅🔴] 这种多字节字符类**：Git Bash 的默认 locale
#    不是 UTF-8 感知的，字符类会退化成按字节匹配，认不出 ✅，于是把已完成的行
#    当成未完成重跑一遍。改成用 awk 按 | 切出第 4 个字段，整串比较。
resume_status() { # resume_status <ID>
  grep -E "^\| *$1 *\|" "$RESUME" 2>/dev/null | head -1 \
    | awk -F'|' '{ s=$4; gsub(/^[ \t]+/, "", s); gsub(/[ \t\r]+$/, "", s); print s }'
}

resume_note() { # resume_note <ID> —— 取备注列
  grep -E "^\| *$1 *\|" "$RESUME" 2>/dev/null | head -1 \
    | awk -F'|' '{ s=$5; gsub(/^[ \t]+/, "", s); gsub(/[ \t\r]+$/, "", s); print s }'
}

resume_mark() { # resume_mark <ID> <mark> <note>
  # note 传空串 = 保留原有备注。
  local id="$1" mark="$2" note="$3" tmp eol
  if [ "$DRYRUN" -eq 1 ]; then return; fi   # dry-run 不许写盘
  tmp="$(mktemp)"
  # 状态表也是 CRLF 的，重写时要把行尾补回去，否则改过的行会变成 LF，
  # 文件变成混合行尾（git diff 会整片飘红）
  if grep -q $'\r$' "$RESUME" 2>/dev/null; then eol='\r'; else eol=''; fi
  # 顺带把**整张表**规范化：分隔符两侧的空白一律去掉再拼回去。
  # 只整理被改的那一行是不够的——早先那版只在重写单行时处理，别的行仍留着
  # 手写时的原样，表格于是长短不齐。
  awk -v id="$id" -v mark="$mark" -v note="$note" -v eol="$eol" '
    function trim(s) { gsub(/^[ \t]+/, "", s); gsub(/[ \t]+$/, "", s); return s }
    BEGIN { done = 0 }
    {
      line = $0
      if (line ~ /\r$/) { sub(/\r$/, "", line); tail = "\r" } else { tail = "" }

      if (line ~ /^\| *[DW]-[0-9]+ *\|/) {
        n = split(line, f, "|")
        if (n >= 5) {
          k = trim(f[2])
          desc = trim(f[3])
          st = trim(f[4])
          nt  = trim(f[5])
          if (k == id) {
            st = mark
            if (note != "") nt = note
            done = 1
          }
          printf "| %s | %s | %s | %s |%s\n", k, desc, st, nt, eol
          next
        }
      }
      print line tail
    }
  ' "$RESUME" > "$tmp" && mv "$tmp" "$RESUME"
}

# ── 行尾 ─────────────────────────────────────────────────────────────────────
#
# 这个仓库没有 .gitattributes（直到 D-19 补上），开发机 core.autocrlf 开着，
# 于是同一份文档里可能同时有 CRLF 和 LF 的行。第一版执行器是「把正文归一成
# 目标文件的约定」——结果正文里的 `^---SPLIT---$` 这种行尾锚点在 CRLF 下
# 匹配不上，拆分失败，旧文里混进了分隔符和后半段，替换一律 NOTFOUND，
# 而报错完全看不出是这个原因。
#
# 正确的做法：**全程在 LF 空间里做匹配与拼接，最后一次性按目标文件的原约定
# 写回**。这样不管文件当前是纯 CRLF、纯 LF 还是混着的，匹配都稳；写回之后
# 文件还会顺手变得行尾一致。
#
# 文件的约定看**第一行**有没有 CR：文档是 CRLF 出身的，追加的内容在文件尾部，
# 首行最能代表它原本的约定。

file_is_crlf() { # file_is_crlf <file>
  local n
  n="$(head -1 "$1" 2>/dev/null | tr -cd '\r' | wc -c | tr -d '[:space:]')"
  case "$n" in ''|*[!0-9]*) return 1 ;; esac
  [ "$n" -gt 0 ]
}

to_lf() { # to_lf <in> <out> —— 去掉每行行尾的 CR
  awk '{ sub(/\r$/, ""); print }' "$1" > "$2"
}

write_back() { # write_back <target> <tmp-lf-content> —— 按目标文件原约定写回
  local target="$1" tmp="$2"
  if file_is_crlf "$target"; then
    awk '{ print $0 "\r" }' "$tmp" > "$target.wcb" && mv "$target.wcb" "$target"
  else
    mv "$tmp" "$target"
  fi
}

# ── 三种操作 ───────────────────────────────────────────────────────────────
apply_prepend() { # apply_prepend <file> <body-file>
  local target="$1" body="$2" tmp blf
  tmp="$(mktemp)"; blf="$(mktemp)"
  to_lf "$body" "$blf"
  {
    awk '{ sub(/\r$/, ""); print }' "$target" | head -1
    echo ""
    cat "$blf"
    echo ""
    awk '{ sub(/\r$/, ""); print }' "$target" | tail -n +2
  } > "$tmp"
  rm -f "$blf"
  write_back "$target" "$tmp"
}

apply_append() { # apply_append <file> <body-file>
  local target="$1" body="$2" tmp blf
  tmp="$(mktemp)"; blf="$(mktemp)"
  to_lf "$body" "$blf"
  {
    awk '{ sub(/\r$/, ""); print }' "$target"
    echo ""
    cat "$blf"
  } > "$tmp"
  rm -f "$blf"
  write_back "$target" "$tmp"
}

apply_create() { # apply_create <file> <body-file>
  # 建新文件。已存在就报错——「create」撞上一个已存在的文件，说明步骤定义
  # 与仓库实况脱节了，静默覆盖会毁掉别人的东西。
  local target="$1" body="$2"
  if [ -e "$target" ]; then
    echo "文件已存在，拒绝覆盖：$target"
    return 1
  fi
  # 新文件一律用 LF：仓库里存的是 LF，.gitattributes 也会钉住。
  to_lf "$body" "$target"
}

apply_replace() { # apply_replace <file> <body-file>
  # 正文用 ---SPLIT--- 分成旧文/新文；旧文必须恰好出现一次。
  #
  # 实现上把整个文件 slurp 成一个字符串再做 index/substr 替换——比逐行比对
  # 少得多出错面：多行旧文天然支持，唯一性一次判完，不需要「读几行再吐回去」
  # 那种容易写错的回退逻辑。
  local target="$1" body="$2" oldfile newfile out rc bodylf tgtlf
  oldfile="$(mktemp)"; newfile="$(mktemp)"; out="$(mktemp)"
  bodylf="$(mktemp)"; tgtlf="$(mktemp)"

  # ⚠️ 拆分必须在**归一之后的 LF 空间**里做。`^---SPLIT---$` 这种行尾锚点
  #    在 CRLF 上匹配不上——第一版就是在这栽的：旧文里混进了分隔符和后半段，
  #    替换一律 NOTFOUND，而报错信息完全看不出是这个原因。
  to_lf "$body" "$bodylf"
  awk '/^---SPLIT---$/{exit} {print}' "$bodylf" > "$oldfile"
  awk 'seen{print} /^---SPLIT---$/{seen=1}' "$bodylf" > "$newfile"

  if [ ! -s "$oldfile" ]; then
    rm -f "$oldfile" "$newfile" "$out" "$out.err" "$bodylf" "$tgtlf"
    echo "replace 的旧文为空"
    return 1
  fi

  # 目标也归一到 LF，匹配与拼接都在 LF 空间里做。
  to_lf "$target" "$tgtlf"

  # ⚠️ awk 的 -v 赋值必须写在程序文本**之前**。放后面的话 awk 会把 -v 当成
  #    普通参数，变量全成空串，报 "expression for `<' redirection has null
  #    string value"——这个报错完全不指向真正的原因，值得记一笔。
  awk -v oldf="$oldfile" -v newf="$newfile" -v targetf="$tgtlf" -v outf="$out" '
    function slurp(f,   line, s) {
      s = ""
      while ((getline line < f) > 0) { s = s line "\n" }
      close(f)
      sub(/\n$/, "", s)
      return s
    }
    BEGIN {
      o = slurp(oldf); n = slurp(newf)
      t = slurp(targetf)
      idx = index(t, o)
      if (idx == 0) { print "NOTFOUND" > "/dev/stderr"; exit 1 }
      rest = substr(t, idx + length(o))
      if (index(rest, o) > 0) { print "NOTUNIQUE" > "/dev/stderr"; exit 1 }
      printf("%s", (substr(t, 1, idx - 1) n rest)) > outf
    }
  ' 2>"$out.err"
  rc=$?

  if [ $rc -ne 0 ]; then
    msg="$(cat "$out.err" 2>/dev/null || echo '未知错误')"
    rm -f "$oldfile" "$newfile" "$out" "$out.err" "$bodylf" "$tgtlf"
    echo "替换失败：$msg"
    return 1
  fi
  rm -f "$oldfile" "$newfile" "$out.err" "$bodylf" "$tgtlf"
  write_back "$target" "$out"
  return 0
}

# ── 主循环 ─────────────────────────────────────────────────────────────────
log "=== 文档对齐执行器：target=$TARGET$DRYTAG ==="

for stepfile in "$STEPDIR"/*.step; do
  [ -e "$stepfile" ] || continue
  id="$(step_field "$stepfile" ID)"
  tgt="$(step_field "$stepfile" TARGET)"
  file="$(step_field "$stepfile" FILE)"
  op="$(step_field "$stepfile" OP)"
  verify="$(step_field "$stepfile" VERIFY)"
  commitmsg="$(step_field "$stepfile" COMMIT)"

  [ -n "$id" ] || { log "SKIP  $(basename "$stepfile")：没有 ID"; continue; }
  [ "$tgt" = "$TARGET" ] || continue
  if [ -n "$ONLY" ]; then
    case ",$ONLY," in *",$id,"*) ;; *) continue ;; esac
  fi

  status="$(resume_status "$id")"
  if [ "$status" = "✅" ]; then
    # 备注里应该是**证据 hash**。缺了、或只剩「已满足」，就按步骤的 commit
    # message 找回当初那笔提交填回去——状态表要能自愈，不能靠人记得填。
    cur="$(resume_note "$id")"
    if [ -z "$cur" ] || [ "$cur" = "已满足" ]; then
      h="$(git log --format='%h' --fixed-strings --grep="$commitmsg" -1 2>/dev/null | head -1)"
      if [ -n "$h" ]; then
        resume_mark "$id" "✅" "$h"
        log "FIX   $id 回填证据 hash $h"
      fi
    fi
    SKIPPED=$((SKIPPED + 1))
    log "SKIP  $id 已是 ✅"
    continue
  fi

  if [ "$op" = "create" ]; then
    if [ -e "$file" ]; then
      FAILED=$((FAILED + 1))
      log "FAIL  $id 是 create 操作，但文件已存在：$file"
      resume_mark "$id" "🔴" "文件已存在，拒绝覆盖"
      continue
    fi
  elif [ ! -f "$file" ]; then
    FAILED=$((FAILED + 1))
    log "FAIL  $id 目标文件不存在：$file"
    resume_mark "$id" "🔴" "目标文件不存在"
    continue
  fi

  # 已经改过了？（VERIFY 已通过）→ 不重复改，只补状态与证据。
  # 状态表里必须有**证据 hash**——「看起来做过了」不是证据。重跑时按步骤的
  # commit message 找回当初那笔提交填回去，所以状态表能自愈。
  if [ -n "$verify" ] && eval "$verify" >/dev/null 2>&1; then
    h="$(git log --format='%h' --fixed-strings --grep="$commitmsg" -1 2>/dev/null | head -1)"
    [ -n "$h" ] || h="已满足"
    log "OK    $id 已经满足验收条件（提交 $h）"
    resume_mark "$id" "✅" "$h"
    SKIPPED=$((SKIPPED + 1))
    continue
  fi

  if [ "$DRYRUN" -eq 1 ]; then
    log "DRY   $id 将对 $file 执行 $op"
    continue
  fi

  bodyfile="$(mktemp)"
  step_body "$stepfile" > "$bodyfile"

  case "$op" in
    prepend) apply_prepend "$file" "$bodyfile" ;;
    append)  apply_append  "$file" "$bodyfile" ;;
    replace) apply_replace "$file" "$bodyfile" ;;
    create)  apply_create  "$file" "$bodyfile" ;;
    *) echo "未知的 OP：$op"; rm -f "$bodyfile"; FAILED=$((FAILED+1)); continue ;;
  esac
  rc=$?
  rm -f "$bodyfile"

  if [ $rc -ne 0 ]; then
    git checkout -- "$file" 2>/dev/null
    FAILED=$((FAILED + 1))
    log "FAIL  $id 应用失败，已回滚（$file）"
    resume_mark "$id" "🔴" "应用失败，已回滚"
    continue
  fi

  if [ -n "$verify" ] && ! eval "$verify" >/dev/null 2>&1; then
    git checkout -- "$file" 2>/dev/null
    FAILED=$((FAILED + 1))
    log "FAIL  $id 验收命令没通过，已回滚：$verify"
    resume_mark "$id" "🔴" "验收未通过，已回滚"
    continue
  fi

  # 提交：先提交改动，拿到真实 hash，回填状态表，再 amend —— 一次提交里既有
  # 文档改动也有看板更新（CLAUDE.md：看板与代码同一个 commit）。
  resume_mark "$id" "✅" "本次提交"
  git add -- "$file" "$RESUME" >/dev/null 2>&1
  if ! git commit -q -m "${commitmsg:-docs: [$id] 文档对齐}" >/dev/null 2>&1; then
    FAILED=$((FAILED + 1))
    log "FAIL  $id 提交失败"
    git checkout -- "$RESUME" 2>/dev/null
    continue
  fi
  hash="$(git rev-parse --short HEAD)"
  resume_mark "$id" "✅" "$hash"
  git add -- "$RESUME" >/dev/null 2>&1
  git commit -q --amend --no-edit >/dev/null 2>&1
  PASSED=$((PASSED + 1))
  log "OK    $id → $hash  ($file)"
done

# 收尾：状态表可能因为「回填证据 hash」被改过但没提交。它必须提交——
# 否则下次运行时工作区不干净，执行器会拒绝运行（那是刻意的硬前提）。
if [ "$DRYRUN" -ne 1 ] && ! git diff --quiet -- "$RESUME" 2>/dev/null; then
  git add -- "$RESUME" >/dev/null 2>&1
  if git commit -q -m "docs(doc-align): 状态表回填证据 hash" >/dev/null 2>&1; then
    log "FIX   状态表的证据 hash 已单独提交"
  else
    log "WARN  状态表有未提交的改动，下次运行会因工作区不干净而拒绝" 'WARN'
  fi
fi

log "=== 完成：成功 $PASSED / 失败 $FAILED / 跳过 $SKIPPED ==="
log "=== 剩余未完成项 ==="
grep -E '^\| *[A-Z]-[0-9]+ *\|' "$RESUME" | grep -E '[⬜⏳]' || echo "（无）"

[ "$FAILED" -eq 0 ] || exit 1
exit 0

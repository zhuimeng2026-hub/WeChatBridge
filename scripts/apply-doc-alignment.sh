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
  awk '/^---BEGIN---$/{on=1;next} /^---END---$/{on=0} on' "$1"
}

# ── 行尾归一 ───────────────────────────────────────────────────────────────
#
# 步骤定义与目标文件必须用同一种行尾约定，否则精确匹配必然 NOTFOUND。
# 以**目标文件**为准，把正文转成它的约定。
normalize_body() { # normalize_body <target-file> <body-file>
  local target="$1" body="$2"
  if grep -q $'\r$' "$target" 2>/dev/null; then
    awk '{ sub(/\r$/, ""); print $0 "\r" }' "$body" > "$body.n" && mv "$body.n" "$body"
  else
    awk '{ sub(/\r$/, ""); print }' "$body" > "$body.n" && mv "$body.n" "$body"
  fi
}

# ── 状态表读写 ─────────────────────────────────────────────────────────────
# 状态表行格式： | D-01 | 说明 | ⬜ | 备注 |
resume_status() { # resume_status <ID>
  grep -E "^\| *$(printf '%s' "$1" | sed 's/[][\\.*^$/]/\\&/g') *\|" "$RESUME" 2>/dev/null \
    | grep -oE '[⬜⏳✅🔴]' | head -1
}

resume_mark() { # resume_mark <ID> <mark> <note>
  local id="$1" mark="$2" note="$3" tmp eol
  tmp="$(mktemp)"
  # 状态表也是 CRLF 的，重写时要把行尾补回去，否则改过的行会变成 LF，
  # 文件变成混合行尾（git diff 会整片飘红）
  if grep -q $'\r$' "$RESUME" 2>/dev/null; then eol='\r'; else eol=''; fi
  awk -v id="$id" -v mark="$mark" -v note="$note" -v eol="$eol" '
    BEGIN { done = 0 }
    {
      if (!done && $0 ~ ("^\\| *" id " *\\|")) {
        # 保留前两列，重建状态列与备注列
        n = split($0, f, "|")
        if (n >= 5) {
          gsub(/\r$/, "", f[2]); gsub(/\r$/, "", f[3])
          printf "| %s | %s | %s | %s |%s\n", f[2], f[3], mark, note, eol
          done = 1
          next
        }
      }
      print
    }
  ' "$RESUME" > "$tmp" && mv "$tmp" "$RESUME"
}

# ── 三种操作 ───────────────────────────────────────────────────────────────
apply_prepend() { # apply_prepend <file> <body-file>
  local target="$1" body="$2" tmp
  tmp="$(mktemp)"
  {
    head -1 "$target"
    echo ""
    cat "$body"
    echo ""
    tail -n +2 "$target"
  } > "$tmp" && mv "$tmp" "$target"
}

apply_append() { # apply_append <file> <body-file>
  local target="$1" body="$2"
  {
    echo ""
    cat "$body"
  } >> "$target"
}

apply_replace() { # apply_replace <file> <body-file>
  # 正文用 ---SPLIT--- 分成旧文/新文；旧文必须恰好出现一次。
  #
  # 实现上把整个文件 slurp 成一个字符串再做 index/substr 替换——比逐行比对
  # 少得多出错面：多行旧文天然支持，唯一性一次判完，不需要「读几行再吐回去」
  # 那种容易写错的回退逻辑。
  local target="$1" body="$2" oldfile newfile out rc
  oldfile="$(mktemp)"; newfile="$(mktemp)"; out="$(mktemp)"
  awk '/^---SPLIT---$/{exit} {print}' "$body" > "$oldfile"
  awk 'seen{print} /^---SPLIT---$/{seen=1}' "$body" > "$newfile"

  if [ ! -s "$oldfile" ]; then
    rm -f "$oldfile" "$newfile" "$out"
    echo "replace 的旧文为空"
    return 1
  fi

  awk '
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
      printf "%s", substr(t, 1, idx - 1) n rest > outf
    }
  ' -v oldf="$oldfile" -v newf="$newfile" -v targetf="$target" -v outf="$out" 2>/tmp/wcb_replace_err.$$
  rc=$?

  if [ $rc -ne 0 ]; then
    msg="$(cat /tmp/wcb_replace_err.$$ 2>/dev/null || echo '未知错误')"
    rm -f /tmp/wcb_replace_err.$$ "$oldfile" "$newfile" "$out"
    echo "替换失败：$msg"
    return 1
  fi
  rm -f /tmp/wcb_replace_err.$$ "$oldfile" "$newfile"
  mv "$out" "$target"
  return 0
}

# ── 主循环 ─────────────────────────────────────────────────────────────────
log "=== 文档对齐执行器：target=$TARGET${DRYRUN:+ (dry-run)} ==="

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
    SKIPPED=$((SKIPPED + 1))
    log "SKIP  $id 已是 ✅"
    continue
  fi

  if [ ! -f "$file" ]; then
    FAILED=$((FAILED + 1))
    log "FAIL  $id 目标文件不存在：$file"
    resume_mark "$id" "🔴" "目标文件不存在"
    continue
  fi

  # 已经改过了？（VERIFY 已通过）→ 直接标 ✅，不重复改
  if [ -n "$verify" ] && eval "$verify" >/dev/null 2>&1; then
    log "OK    $id 已经满足验收条件，直接标记"
    resume_mark "$id" "✅" "已满足"
    SKIPPED=$((SKIPPED + 1))
    continue
  fi

  if [ "$DRYRUN" -eq 1 ]; then
    log "DRY   $id 将对 $file 执行 $op"
    continue
  fi

  bodyfile="$(mktemp)"
  step_body "$stepfile" > "$bodyfile"
  normalize_body "$file" "$bodyfile"

  case "$op" in
    prepend) apply_prepend "$file" "$bodyfile" ;;
    append)  apply_append  "$file" "$bodyfile" ;;
    replace) apply_replace "$file" "$bodyfile" ;;
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

log "=== 完成：成功 $PASSED / 失败 $FAILED / 跳过 $SKIPPED ==="
log "=== 剩余未完成项 ==="
grep -E '^\| *[A-Z]-[0-9]+ *\|' "$RESUME" | grep -E '[⬜⏳]' || echo "（无）"

[ "$FAILED" -eq 0 ] || exit 1
exit 0

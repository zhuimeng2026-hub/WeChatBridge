#!/usr/bin/env bash
#
# 文档对齐夜间执行器（Linux）
#
# 真正执行的是一台 Linux 主机：克隆本仓库，装好 git，挂在 cron 上跑。
# 它做三件事：在 main 上跑一遍执行器 →（可选）给 WB_BRANCH 指定的分支也跑一遍 →
# 只在**确实有提交**时推 origin。
#
# 2026-10-24 起第二个目标默认关闭：workbench 分支合并后已删除，步骤全挂 main。
#
# ── 异地部署 ─────────────────────────────────────────────────────────────────
# 本脚本**不写死任何路径**：仓库根从脚本自身位置推导（scripts/ 的上一级），
# worktree 目录从仓库根的同级推导。所以整个仓库拷到别的机器、放到别的目录、
# 换个用户都能直接用，只需重新挂一次 cron。
#
# 挂 cron（crontab -e）：
#   0 1 * * * /bin/bash /path/to/repo/scripts/nightly-doc-alignment.sh >> /path/to/log 2>&1
#
# **用 /bin/bash 显式调用**：本仓库的 .sh 在 git 里是 100644（没有可执行位），
# 直接当命令执行会 Permission denied——2026-10-24 实测踩过。
# 路径一律写绝对路径，别依赖 cwd，也别指望 cron 的 PATH 里有你的工具。
#
# 幂等：所有步骤做完后再跑一次，什么都不做直接退出。所以每天跑一次是安全的。
#
# **脏工作区 = 跳过，不是失败。** 执行器要求干净起点；有人正在这个仓库里改东西时，
# 那一半目标会被跳过（返回码 10），本脚本照样 exit 0。明天夜里再来。
#
# **退出码如实反映失败**：有目标真挂了（执行器报错、VERIFY 不过）就 exit 1，
# cron 才看得出「昨夜没干成」。
#
# **默认不推 origin。** 提交留在本地等人看过；要推就 `PUSH=1 scripts/nightly-doc-alignment.sh`。
# 无人值守的作业不该自己决定把东西发出去——某一步改错了，那笔内容就带着
# commit hash 进了远端历史，撤不干净。
#
# 这个脚本**不安装、不修改任何定时任务**——cron 那行由人写。它在清单跑空时
# 也只提示，不去动 cron。
#
# ── 行尾 ─────────────────────────────────────────────────────────────────────
# 本文件必须是 LF。仓库没有 .gitattributes 且开发机 core.autocrlf 开着，
# Windows 上 checkout 出来是 CRLF——直接跑会报 `$'\r': command not found`。
# 提交进仓库的 blob 是 LF（CR 字节为 0），所以 Linux 上 clone 出来就是对的。

set -u

# ── 路径全部从脚本自身推导，不写死 ────────────────────────────────────────────
# $REPO_ROOT/scripts/nightly-doc-alignment.sh → REPO_ROOT 是仓库根
SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$SCRIPT_DIR/.." && pwd)"
REPO_NAME="$(basename "$REPO")"
PARENT_DIR="$(cd "$REPO/.." && pwd)"
# worktree 放在仓库同级：<父目录>/<仓库名>-wb
WB_WORKTREE="${WB_WORKTREE:-$PARENT_DIR/$REPO_NAME-wb}"
# 2026-10-24：workbench 分支合并后已删除，它那条线上的步骤（D-xx 与 W-01）
# 现在全挂在 main 上。所以**第二个目标默认不启用**——没有分支可跑的时候，
# 建一个 worktree 然后每天撞一次「branch not found」只是噪音。
# 要给别的分支跑步骤时用环境变量指定：WB_BRANCH=xxx。
WB_BRANCH="${WB_BRANCH:-}"
LOG_DIR="${LOG_DIR:-$REPO/.artifacts/doc-alignment}"
LOG_FILE="$LOG_DIR/nightly-$(date '+%Y-%m-%d').log"

mkdir -p "$LOG_DIR"

log() { printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" | tee -a "$LOG_FILE"; }

banner() { log ""; log "===== $* ====="; }

# ── 执行器只接受「目标分支已检出 + 工作区干净」的起点 ──────────────────────────
# 这两件事都不满足时它会拒绝运行——那是刻意的：脏工作区下「改坏了就回滚」
# 会误伤别人的改动。2026-10-23 清一个 worktree 时就是这么丢掉了三份测试文件。
#
# 返回码分两类，别混：
#   10 = 跳过（起点不满足，等明天再来；不是失败）
#   其他非零 = 真失败（执行器报错、VERIFY 不过……）
# 之前这两种都返回 1，脚本结尾又固定 exit 0 —— 两个目标全挂，cron 也看不见失败。
run_target() {
  local path="$1" branch="$2"

  banner "target = $branch ($path)"

  if [ ! -d "$path" ]; then
    log "工作区不存在：$path"
    return 10
  fi

  local current
  current="$(git -C "$path" rev-parse --abbrev-ref HEAD 2>/dev/null || echo '<none>')"
  if [ "$current" != "$branch" ]; then
    log "工作区 $path 当前在 $current，不是 $branch；执行器要求目标分支已检出。"
    return 10
  fi

  local dirty
  dirty="$(git -C "$path" status --porcelain --untracked-files=no)"
  if [ -n "$dirty" ]; then
    log "工作区 $path 不干净，执行器拒绝运行（脏工作区下回滚会误伤别人的改动）："
    while IFS= read -r line; do log "  $line"; done <<< "$dirty"
    log "→ 跳过（不是失败）：另有人在改这个工作区，明天夜里再来。"
    return 10
  fi

  # 分支上没有 doc-alignment 运行时 = 这条分支根本没参与过这套流程。
  # 工具链和状态表都只存在于 main；分支是从 main 拉出去的老分支，
  # 压根没有 scripts/ 目录（实测 2026-10-24）。这时候不是「作业坏了」，
  # 是「这条分支还没被接进来」——记成跳过，把接法写在日志里。
  if [ ! -f "$path/scripts/apply-doc-alignment.sh" ] || [ ! -f "$path/docs/progress/doc-alignment.resume.md" ]; then
    local pend
    pend="$(pending_for_branch "$branch")"
    if [ "$pend" -eq 0 ]; then
      log "该工作区没有 doc-alignment 运行时，但状态表里 TARGET 指向这条分支的步骤都已应用——本目标无事可做，跳过。"
      return 10
    fi
    log "该工作区没有 doc-alignment 运行时（缺 scripts/apply-doc-alignment.sh 或 docs/progress/doc-alignment.resume.md），本目标跳过。"
    log "→ 还有 $pend 项 TARGET 指向这条分支的步骤没应用：得人工应用，或把 main 的 scripts/ 与状态表嫁接进这条分支。"
    return 10
  fi

  bash "$path/scripts/apply-doc-alignment.sh" --target "$branch" 2>&1 | while IFS= read -r line; do
    log "  $line"
  done
  return "${PIPESTATUS[0]}"
}

# ── 数「还剩几项没应用」───────────────────────────────────────────────────────
#
# ⚠️ 不要用 `grep -E '.*[⬜⏳]'` 这种多字节字符类。cron 的环境是干净的（没有
# LANG/LC_ALL），grep 的方括号表达式按**字节**解释，`[⬜⏳]` 退化成字节集合，
# 于是带中文的行几乎全被命中——同一个文件，C locale 下数出 24，UTF-8 locale 下
# 才是 1（2026-10-24 实测）。执行器自己早就踩过这个坑，它取状态列是逐格精确
# 比对（apply-doc-alignment.sh:126 的注释写着这件事）。
# 这里按 | 切列后逐格比对状态字符串，字节语义确定，跟 locale 无关。
count_open() { # count_open <状态表>
  awk -F'|' '
    /^[|][[:space:]]*[A-Z]-[0-9]+[[:space:]]*[|]/ {
      s = $4
      gsub(/^[ \t]+/, "", s); gsub(/[ \t]+$/, "", s)
      if (s != "✅") n++
    }
    END { print n + 0 }
  ' "$1"
}

# ── 某条分支还有没有待应用的步骤 ──────────────────────────────────────────────
#
# 步骤登记表在 main 的状态表里；每个 .step 文件自己写着 TARGET（改哪条分支）。
# 「TARGET 指向 X 且状态还没 ✅」= X 上还有活要干。
pending_for_branch() { # pending_for_branch <分支>
  local br="$1" f id tgt st n=0
  for f in "$REPO"/scripts/doc-alignment/steps/*.step; do
    [ -f "$f" ] || continue
    id="$(sed -n 's/^ID:[[:space:]]*//p' "$f" | head -1 | tr -d '\r')"
    tgt="$(sed -n 's/^TARGET:[[:space:]]*//p' "$f" | head -1 | tr -d '\r')"
    [ "$tgt" = "$br" ] || continue
    st="$(status_of "$id")"
    [ "$st" = "✅" ] || n=$((n + 1))
  done
  echo "$n"
}

status_of() { # status_of <ID>  —— 状态表里那一行的状态列
  awk -F'|' -v id="$1" '
    /^[|][[:space:]]*[A-Z]-[0-9]+[[:space:]]*[|]/ {
      k = $2
      gsub(/^[ \t]+/, "", k); gsub(/[ \t]+$/, "", k)
      if (k == id) {
        s = $4
        gsub(/^[ \t]+/, "", s); gsub(/[ \t]+$/, "", s)
        print s
        exit
      }
    }' "$REPO/docs/progress/doc-alignment.resume.md"
}

# ── 把工具链送到目标工作区 ──────────────────────────────────────────────────
# 执行器从**它自己所在的位置**推导仓库根（REPO=脚本的上一级），步骤目录也在那儿；
# 所以要改哪个工作区的文件，就得用哪个工作区里的那份执行器。
# 而步骤登记表（scripts/doc-alignment/steps/*.step）只存在于 main——
# workbench 分支是从 main 拉出去的老分支，压根没有 scripts/ 目录（实测 2026-10-24），
# 于是那边直接 127「No such file」。
#
# 这里把 main 的 scripts/ 复制进目标工作区当**运行时依赖**，不提交进那条分支：
#   · 步骤登记表永远是 main 上那一份（新的 .step 落地后，下一夜自动生效）；
#   · 分支上不会多出一份会过期的工具链副本。
# 复制出来的是未跟踪文件——执行器的脏检查用 `--untracked-files=no`，看不见它们，
# 所以不会因为这个多出来的 scripts/ 而拒绝运行。
sync_tooling() {
  local path="$1"
  [ "$path" = "$REPO" ] && return 0

  # 分支自己已经跟踪了 scripts/ 的话不动它——那份可能比 main 的新。
  if git -C "$path" ls-files --error-unmatch scripts/ >/dev/null 2>&1; then
    log "  $path 自己跟踪了 scripts/，不覆盖。"
    return 0
  fi

  rm -rf "$path/scripts"
  cp -a "$REPO/scripts" "$path/scripts"
  log "  已把 main 的 scripts/ 复制进 $path（未跟踪，运行时依赖，不提交进分支）。"
}

log "=== 文档对齐夜间任务开始 ==="
log "仓库：$REPO"
log "日志：$LOG_FILE"

if [ ! -f "$REPO/scripts/apply-doc-alignment.sh" ]; then
  log "找不到执行器 scripts/apply-doc-alignment.sh，退出。"
  exit 1
fi

# ── workbench 分支用独立 worktree ────────────────────────────────────────────
# 主检出留给 main（执行器要求目标分支已检出，一个分支不能同时被两个工作树占用）。
#
# 分支可能已经不在了：2026-10-24 知识工作台那条分支合进 main 之后打了
# archive tag 就删掉了，它自己的步骤（W-01~W-03）也把 TARGET 改到了 main。
# 所以这一段是**有分支才做**，不是无条件做——否则夜间作业每天去 worktree add
# 一条不存在的分支，失败之后还接着往下跑。
TARGETS=("$REPO:main")
if git -C "$REPO" rev-parse --verify --quiet "refs/heads/$WB_BRANCH" >/dev/null; then
  if [ ! -d "$WB_WORKTREE" ]; then
    log "创建 workbench worktree：$WB_WORKTREE"
    git -C "$REPO" worktree add "$WB_WORKTREE" "$WB_BRANCH" 2>&1 | while IFS= read -r line; do log "  $line"; done
  fi
  # ── 给 workbench 工作区补上工具链 ────────────────────────────────────────
  banner "同步工具链到 workbench 工作区"
  sync_tooling "$WB_WORKTREE"
  TARGETS+=("$WB_WORKTREE:$WB_BRANCH")
else
  log "workbench 分支 $WB_BRANCH 不存在（已归档），只跑 main。"
fi

# ── 跑各目标 ────────────────────────────────────────────────────────────────
rc_main=0
rc_wb=0
run_target "$REPO" main      || rc_main=$?
for t in "${TARGETS[@]:1}"; do
  run_target "${t%%:*}" "${t##*:}" || rc_wb=$?
done

# ── 推送 ──────────────────────────────────────────────────────────────────────
# **默认不推。** 推送是对外操作，一个无人值守的夜间作业不该自己决定把东西
# 发出去——万一某一步改错了，那笔错的内容就带着 commit hash 进了远端历史。
# 提交本身留在本地等人看过；要推就显式开：
#
#     PUSH=1 scripts/nightly-doc-alignment.sh
#
# cron 里默认不开。开了之后也仍然只在**确实有提交**时才推。
TARGETS="$REPO:main"
[ -n "$WB_BRANCH" ] && TARGETS="$TARGETS $WB_WORKTREE:$WB_BRANCH"

if [ "${PUSH:-0}" = "1" ]; then
  for target in "${TARGETS[@]}"; do
    path="${target%%:*}"
    branch="${target##*:}"
    ahead="$(git -C "$path" rev-list --count "origin/$branch..$branch" 2>/dev/null || echo 0)"
    case "$ahead" in
      ''|*[!0-9]*) ahead=0 ;;
    esac
    if [ "$ahead" -gt 0 ]; then
      banner "push $branch（领先 $ahead 个提交）"
      git -C "$path" push origin "$branch" 2>&1 | while IFS= read -r line; do log "  $line"; done
    else
      log "$branch 没有待推送的提交，跳过。"
    fi
  done
else
  banner "推送已跳过（PUSH 未开启）"
  for target in "${TARGETS[@]}"; do
    path="${target%%:*}"
    branch="${target##*:}"
    ahead="$(git -C "$path" rev-list --count "origin/$branch..$branch" 2>/dev/null || echo 0)"
    case "$ahead" in
      ''|*[!0-9]*) ahead=0 ;;
    esac
    log "$branch 领先 origin $ahead 个提交——提交留在本地，要推就 PUSH=1 再跑一次。"
  done
fi

# ── 收尾：清单空了只提示，不动 cron ───────────────────────────────────────────
#
# 「还剩几项没应用」只认 **main 上那一份状态表**（docs/progress/doc-alignment.resume.md）——
# 它是步骤登记表本身：D-xx 与 W-xx 全列在一张表上，TARGET 指向谁就应用在谁的工作区。
# workbench 分支里那份同名文件是分支自己的副本，停在拉出去那一刻的状态
# （实测 2026-10-24：分支上 D-01…D-23 仍标着 ⬜，而它们早就在 main 上应用完了），
# 把它算进来会得出「还剩 24 项」这种假数。分支那份只作参考打印。
open=0
board="$REPO/docs/progress/doc-alignment.resume.md"
if [ -f "$board" ]; then
  open="$(count_open "$board")"
  log "状态表（main）：尚有 $open 项未应用。"
fi
wb_board="$WB_WORKTREE/docs/progress/doc-alignment.resume.md"
if [ -n "$WB_BRANCH" ] && [ -f "$wb_board" ]; then
  n="$(count_open "$wb_board")"
  log "分支上的状态表副本：$n 项未勾（分支快照，可能陈旧，不计入上面的数）。"
fi

# ── 退出码：真失败才非零 ─────────────────────────────────────────────────────
# 10 = 跳过（起点不满足，等明天），不算失败。
# 之前不管两个目标成没成都 exit 0 —— 挂了 cron 也看不见，只能翻日志才发现。
failed=0
for rc in "$rc_main" "$rc_wb"; do
  case "$rc" in
    0|10) ;;
    *) failed=1 ;;
  esac
done

log "=== 结束：main 返回 $rc_main，workbench 返回 $rc_wb（没有该分支时为 0），尚有 $open 项未完成 ==="
if [ "$open" -eq 0 ]; then
  log "清单已跑空。本脚本幂等，留着不会做错事，只是每天醒来一次——"
  log "想撤掉 cron 就把 crontab 里那一行删掉。"
fi
if [ "$failed" -ne 0 ]; then
  log "有目标真失败了（返回码非 0 且非 10）；本脚本以非零退出，cron 能看见。"
  exit 1
fi
exit 0

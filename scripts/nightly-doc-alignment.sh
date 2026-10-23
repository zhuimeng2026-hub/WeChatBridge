#!/usr/bin/env bash
#
# 文档对齐夜间执行器（Linux）
#
# 真正执行的是一台 Linux 主机：克隆本仓库，装好 git，挂在 cron 上跑。
# 它做三件事：建 workbench worktree（没有就建）→ 两条分支各跑一遍执行器 →
# 只在**确实有提交**时推 origin。
#
# ── 异地部署 ─────────────────────────────────────────────────────────────────
# 本脚本**不写死任何路径**：仓库根从脚本自身位置推导（scripts/ 的上一级），
# worktree 目录从仓库根的同级推导。所以整个仓库拷到别的机器、放到别的目录、
# 换个用户都能直接用，只需重新挂一次 cron。
#
# 挂 cron（crontab -e）：
#   0 1 * * * /path/to/repo/scripts/nightly-doc-alignment.sh >> /path/to/log 2>&1
#
# 幂等：所有步骤做完后再跑一次，什么都不做直接退出。所以每天跑一次是安全的。
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
WB_BRANCH="${WB_BRANCH:-feat/obsidian-workbench-variants}"
LOG_DIR="${LOG_DIR:-$REPO/.artifacts/doc-alignment}"
LOG_FILE="$LOG_DIR/nightly-$(date '+%Y-%m-%d').log"

mkdir -p "$LOG_DIR"

log() { printf '%s %s\n' "$(date '+%Y-%m-%d %H:%M:%S')" "$*" | tee -a "$LOG_FILE"; }

banner() { log ""; log "===== $* ====="; }

# ── 执行器只接受「目标分支已检出 + 工作区干净」的起点 ──────────────────────────
# 这两件事都不满足时它会拒绝运行——那是刻意的：脏工作区下「改坏了就回滚」
# 会误伤别人的改动。2026-10-23 清一个 worktree 时就是这么丢掉了三份测试文件。
run_target() {
  local path="$1" branch="$2"

  banner "target = $branch ($path)"

  if [ ! -d "$path" ]; then
    log "工作区不存在：$path"
    return 1
  fi

  local current
  current="$(git -C "$path" rev-parse --abbrev-ref HEAD 2>/dev/null || echo '<none>')"
  if [ "$current" != "$branch" ]; then
    log "工作区 $path 当前在 $current，不是 $branch；执行器要求目标分支已检出。"
    return 1
  fi

  local dirty
  dirty="$(git -C "$path" status --porcelain --untracked-files=no)"
  if [ -n "$dirty" ]; then
    log "工作区 $path 不干净，执行器拒绝运行（脏工作区下回滚会误伤别人的改动）："
    while IFS= read -r line; do log "  $line"; done <<< "$dirty"
    return 1
  fi

  bash "$path/scripts/apply-doc-alignment.sh" --target "$branch" 2>&1 | while IFS= read -r line; do
    log "  $line"
  done
  return "${PIPESTATUS[0]}"
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
if [ ! -d "$WB_WORKTREE" ]; then
  log "创建 workbench worktree：$WB_WORKTREE"
  git -C "$REPO" worktree add "$WB_WORKTREE" "$WB_BRANCH" 2>&1 | while IFS= read -r line; do log "  $line"; done
fi

# ── 跑两条分支 ────────────────────────────────────────────────────────────────
rc_main=0
rc_wb=0
run_target "$REPO" main      || rc_main=$?
run_target "$WB_WORKTREE" "$WB_BRANCH" || rc_wb=$?

# ── 推送 ──────────────────────────────────────────────────────────────────────
# 没 push = 没发生（仓库协议）。但推送是对外操作，所以只在**确实有提交**时才做。
for target in "$REPO:main" "$WB_WORKTREE:$WB_BRANCH"; do
  path="${target%%:*}"
  branch="${target##*:}"
  ahead="$(git -C "$path" rev-list --count "$branch..origin/$branch" 2>/dev/null || echo 0)"
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

# ── 收尾：清单空了只提示，不动 cron ───────────────────────────────────────────
open=0
for target in "$REPO" "$WB_WORKTREE"; do
  board="$target/docs/progress/doc-alignment.resume.md"
  if [ -f "$board" ]; then
    n="$(grep -cE '^\| [DW]-[0-9]+ \|.*[⬜⏳]' "$board" || true)"
    case "$n" in ''|*[!0-9]*) n=0 ;; esac
    open=$((open + n))
    log "$(basename "$target")：尚有 $n 项未完成。"
  fi
done

log "=== 结束：main 返回 $rc_main，workbench 返回 $rc_wb，尚有 $open 项未完成 ==="
if [ "$open" -eq 0 ]; then
  log "清单已跑空。本脚本幂等，留着不会做错事，只是每天醒来一次——"
  log "想撤掉 cron 就把 crontab 里那一行删掉。"
fi
exit 0

# 文档对齐夜间执行器
#
# 由 Windows 任务计划程序在 01:00 触发。跑仓库里的确定性执行器
# （scripts/apply-doc-alignment.sh），把文档改成与代码实况一致。
#
# ── 异地部署 ─────────────────────────────────────────────────────────────────
# 本脚本**不写死任何路径**：仓库根从脚本自身位置推导（scripts/ 的上一级），
# worktree 目录从仓库根的同级推导。所以整个仓库拷到另一台机器、放到别的
# 盘符或别的目录下都能直接用，只需重新注册一次计划任务。
#
# 注册计划任务（一次性触发，之后由本脚本重排）。用你自己的仓库路径：
#   $Repo = 'D:\WeChatBridge'
#   Register-ScheduledTask -TaskName 'WeChatBridge-DocAlignment' `
#     -Action (New-ScheduledTaskAction -Execute 'powershell.exe' `
#       -Argument "-NoProfile -ExecutionPolicy Bypass -File `"$Repo\scripts\nightly-doc-alignment.ps1`"") `
#     -Trigger (New-ScheduledTaskTrigger -Once -At (Get-Date).Date.AddDays(1).AddHours(1)) `
#     -Settings (New-ScheduledTaskSettingsSet -StartWhenAvailable)
#
# 为什么不用模型改文档：清单里每一条都是「找什么 → 改成什么」的精确替换。
# 确定性执行有三个模型替代不了的性质——幂等（跑第二次是空操作）、可验收
# （每步自带 VERIFY，验不过就回滚）、不烧额度（整个夜间任务零 LLM 调用，
# 不会因为「额度用光」这种与任务本身无关的原因失败）。
#
# 幂等：所有步骤做完后再跑一次，什么都不做直接退出。所以每天跑一次是安全的。
#
# 这个脚本**不安装、不修改任何计划任务**——注册命令写在下面的注释里，由人执行。
# 清单跑空时它也只提示，不去动账号级的任务状态。
#
# 注意：这个文件带中文，PS 5.1 在中文 Windows 上会把无 BOM 的 .ps1 按 GBK 读，
# 静默毁掉字符串而不报错。改完务必保持 UTF-8 with BOM。

$ErrorActionPreference = 'Continue'

# ── 路径全部从脚本自身推导，不写死 ────────────────────────────────────────────
# $PSScriptRoot = <仓库>\scripts ；仓库根是它的上一级。
$Repo        = Split-Path -Parent $PSScriptRoot
$RepoName   = Split-Path -Leaf $Repo
$ParentDir   = Split-Path -Parent $Repo
# worktree 放在仓库同级：<父目录>\<仓库名>-wb
$WbWorktree  = Join-Path $ParentDir ($RepoName + '-wb')
$WbBranch    = 'feat/obsidian-workbench-variants'
$LogDir      = Join-Path $env:USERPROFILE '.claude\logs'
$Log         = Join-Path $LogDir ('doc-alignment-{0:yyyy-MM-dd}.log' -f (Get-Date))
$TaskName    = 'WeChatBridge-DocAlignment'

New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

function Write-Log([string]$Message, [string]$Level = 'INFO') {
    $line = '{0:yyyy-MM-dd HH:mm:ss} [{1}] {2}' -f (Get-Date), $Level, $Message
    Add-Content -Path $Log -Value $line -Encoding UTF8
    Write-Output $line
}

function Write-Banner([string]$Text) {
    Write-Log ''
    Write-Log "===== $Text ====="
}

# 清单跑空时**只提示，不代劳**。
#
# 早先这里会自己去 Disable-ScheduledTask，但那是改账号级的任务状态——一个
# 夜间脚本不该自作主张停掉用户的计划任务。现在只把该跑的命令打出来，
# 要不要停由人决定。（脚本是幂等的：清单空了它每次跑都是空操作，
# 留着也不会做错事，只是每天醒来一次。）
function Report-WhenExhausted([int]$OpenSteps) {
    if ($OpenSteps -gt 0) { return }
    Write-Log '清单里已无可执行步骤（本脚本是幂等的，留着不会做错事）。' 'NOTICE'
    Write-Log "若不想让它继续唤醒机器，自己跑：Disable-ScheduledTask -TaskName $TaskName" 'NOTICE'
    Write-Log "若之后往清单里加了步骤：Enable-ScheduledTask -TaskName $TaskName" 'NOTICE'
}

# 执行器只接受「目标分支已检出 + 工作区干净」的起点。这两件事都不满足时
# 它会拒绝运行——那是刻意的：脏工作区下回滚会误伤别人的改动。
function Invoke-Target([string]$Path, [string]$Branch) {
    Write-Banner "target = $Branch ($Path)"

    if (-not (Test-Path $Path)) {
        Write-Log "工作区不存在：$Path" 'ERROR'
        return 1
    }

    $current = (& git -C $Path rev-parse --abbrev-ref HEAD 2>$null)
    if ($current -ne $Branch) {
        Write-Log "工作区 $Path 当前在 $current，不是 $Branch；执行器要求目标分支已检出。" 'ERROR'
        return 1
    }

    $dirty = (& git -C $Path status --porcelain --untracked-files=no)
    if ($dirty) {
        Write-Log "工作区 $Path 不干净，执行器拒绝运行（脏工作区下回滚会误伤别人的改动）：" 'ERROR'
        foreach ($line in $dirty) { Write-Log "  $line" 'ERROR' }
        return 1
    }

    # bash 在 Git Bash 里；不在 PATH 上时用它的绝对路径兜底。
    $bash = (Get-Command bash -ErrorAction SilentlyContinue)
    if (-not $bash) {
        $bashPath = 'C:\Program Files\Git\bin\bash.exe'
        if (-not (Test-Path $bashPath)) {
            Write-Log '找不到 bash（Git Bash 未安装或在 PATH 之外）。' 'ERROR'
            return 1
        }
        $bash = $bashPath
    }

    & $bash "$Path/scripts/apply-doc-alignment.sh" --target $Branch 2>&1 | ForEach-Object {
        Write-Log "  $_"
    }
    return $LASTEXITCODE
}

Write-Log '=== 文档对齐夜间任务开始 ==='
Write-Log "仓库：$Repo"

if (-not (Test-Path (Join-Path $Repo 'scripts\apply-doc-alignment.sh'))) {
    Write-Log '找不到执行器 scripts/apply-doc-alignment.sh，退出。' 'ERROR'
    exit 1
}

# ── workbench 分支用独立 worktree ────────────────────────────────────────────
# 主检出留给 main（执行器要求目标分支已检出，一个分支不能同时被两个工作树占用）。
if (-not (Test-Path $WbWorktree)) {
    Write-Log "创建 workbench worktree：$WbWorktree" 'NOTICE'
    & git -C $Repo worktree add $WbWorktree $WbBranch 2>&1 | ForEach-Object { Write-Log "  $_" }
}

# ── 跑两条分支 ────────────────────────────────────────────────────────────────
$rcMain = Invoke-Target $Repo 'main'
$rcWb   = Invoke-Target $WbWorktree $WbBranch

# ── 推送 ──────────────────────────────────────────────────────────────────────
# 没 push = 没发生（仓库协议）。但推送是对外操作，所以只在**确实有提交**时才做。
foreach ($t in @(@{ Path = $Repo; Branch = 'main' }, @{ Path = $WbWorktree; Branch = $WbBranch })) {
    $ahead = (& git -C $t.Path rev-list --count "$($t.Branch)..origin/$($t.Branch)" 2>$null)
    if ($ahead -match '^\d+$' -and [int]$ahead -gt 0) {
        Write-Banner "push $($t.Branch)（领先 $ahead 个提交）"
        & git -C $t.Path push origin $t.Branch 2>&1 | ForEach-Object { Write-Log "  $_" }
    }
    else {
        Write-Log "$($t.Branch) 没有待推送的提交，跳过。" 'INFO'
    }
}

# ── 收尾：清单空了就把计划任务停掉 ────────────────────────────────────────────
$open = 0
foreach ($t in @(@{ Path = $Repo; Branch = 'main' }, @{ Path = $WbWorktree; Branch = $WbBranch })) {
    $board = Join-Path $t.Path 'docs\progress\doc-alignment.resume.md'
    if (Test-Path $board) {
        $n = (Select-String -Path $board -Pattern '^\| [DW]-[0-9]+ \|.*[⬜⏳]' -AllMatches | Measure-Object).Count
        $open += $n
        Write-Log "$($t.Branch)：尚有 $n 项未完成。"
    }
}

Write-Log "=== 结束：main 返回 $rcMain，workbench 返回 $rcWb，尚有 $open 项未完成 ==="
Report-WhenExhausted $open
exit 0

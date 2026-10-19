<#
.SYNOPSIS
    一键构建微信流 Windows 客户端，构建成功后直接把它跑起来。

.DESCRIPTION
    产物是 windows\artifacts\publish\win-x64\WeChatBridge.Windows.exe——
    自包含（自带 .NET 运行时），**双击就能打开窗口**，本机不需要装 .NET。

    这一层刻意不自己拼 dotnet publish：真正出包的逻辑只有
    windows\scripts\build-app.ps1 一份（版本号从 Directory.Build.props 读回来，
    主程序与 ShareTarget 一起出，自包含产物少一个运行时文件就报错）。这里只做
    编排——把已有的脚本按顺序调起来，再把结果告诉用户。

    用户数据落在 %LOCALAPPDATA%\WeChatBridge（Inbox / Config / Skills /
    SkillBackups 四个根都由 AppLayout 从这一个根派生）。

.PARAMETER Edition
    买断版（Perpetual，默认）或订阅版（Subscription）。同一个源码树出两个产品。

.PARAMETER OutputDirectory
    产物落哪儿。默认 windows\artifacts\publish\win-x64。

.PARAMETER Register
    额外注册微信「转发到其他应用」里的分享入口——**没有这一步，App 能开，但
    微信的转发菜单里不会出现「微信流」**。这需要稀疏 MSIX、开发证书与
    Windows SDK，且 Appx 相关的 cmdlet 只在 Windows PowerShell 5.1 上可用
    （PowerShell 7 加载 Appx 模块会失败，0x80131539），所以默认不做，
    要用请显式加 -Register。

.PARAMETER NoRun
    只构建不启动。

.PARAMETER SkipNetworkCheck
    跳过买断版「不含网络代码」门禁（与 CI 同一条）。留着它的理由是它很便宜，
    而且能在发布前抓到最危险的那类错：版本形态标错。

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build-and-run.ps1

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\build-and-run.ps1 -Register
#>
[CmdletBinding()]
param(
    [ValidateSet('Perpetual', 'Subscription')]
    [string]$Edition = 'Perpetual',

    [string]$OutputDirectory,

    [switch]$Register,

    [switch]$NoRun,

    [switch]$SkipNetworkCheck
)

$ErrorActionPreference = 'Stop'

# $PSScriptRoot 在某些宿主下是空的（PowerShell 自己的 -File 配合某些 host 配置
# 就是），$MyInvocation.MyCommand.Path 两种情况都可靠。显式回落一次。
$scriptPath = $PSScriptRoot
if (-not $scriptPath) { $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path }
$repositoryRoot = [IO.Path]::GetFullPath($scriptPath)

$windowsScripts = Join-Path $repositoryRoot 'windows\scripts'
$buildScript = Join-Path $windowsScripts 'build-app.ps1'
$networkCheckScript = Join-Path $windowsScripts 'Test-NoNetworkCode.ps1'
$registerScript = Join-Path $windowsScripts 'register-dev.ps1'
$certificateScript = Join-Path $windowsScripts 'new-dev-certificate.ps1'
$unregisterScript = Join-Path $windowsScripts 'unregister-dev.ps1'

foreach ($required in @($buildScript, $networkCheckScript, $registerScript, $certificateScript)) {
    if (-not (Test-Path -LiteralPath $required)) { throw "找不到脚本：$required" }
}

if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $repositoryRoot 'windows\artifacts\publish\win-x64'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

# Appx 与签名相关的工具链只在 Windows PowerShell 5.1 上齐备：
# Get-AppxPackage / Add-AppxPackage 在 PowerShell 7 里加载 Appx 模块会直接失败。
# 门禁脚本结尾是 exit 0，exit 会把调用它的整个脚本一起带走——所以门禁也走
# 独立进程，顺带让退出码变得可读。
$windowsPowerShell = Join-Path $env:SystemRoot 'System32\WindowsPowerShell\v1.0\powershell.exe'
if (-not (Test-Path -LiteralPath $windowsPowerShell)) {
    throw "找不到 Windows PowerShell 5.1：$windowsPowerShell"
}

$stepIndex = 0
$stepTotal = 2
if (-not $SkipNetworkCheck) { $stepTotal++ }
if ($Register) { $stepTotal++ }
function Write-Step([string]$message) {
    $script:stepIndex++
    Write-Host ("==> [{0}/{1}] {2}" -f $script:stepIndex, $script:stepTotal, $message) -ForegroundColor Cyan
}

# 产物目录里的文件被跑着的进程锁着时，publish 会以 MSB3021/MSB3027 失败
# （"文件被 xxx 锁定"）。Windows 每次分享都会留下一个 ShareTarget 常驻进程
# （keeper），它一直占着 share-target 目录里的 dll。
#
# 只停**跑在我们要覆盖的目录里**的那些进程。仓库里的
# windows\portable\Register-ShareTarget.ps1 是按进程名一刀切的——那是便携版
# 由 App 内部拉起注册时的做法，那里不能杀自己（它带 -SkipProcessKill 正是
# 这个原因）。这里是外部构建脚本，按路径精确匹配更安全：用户装在别处的正式
# 版不该被一个构建脚本关掉。
function Stop-ProductRunningFrom([string]$Root) {
    $prefix = $Root.TrimEnd('\') + '\'
    $locked = @(
        Get-Process -Name 'WeChatBridge.ShareTarget', 'WeChatBridge.Windows' -ErrorAction SilentlyContinue |
            Where-Object {
                $path = $null
                try { $path = $_.Path } catch { $path = $null }   # 权限不足读不到 Path
                $path -and $path.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)
            }
    )
    foreach ($process in $locked) {
        Write-Host ("  正在停止 {0}（PID {1}）——它锁着产物目录里的文件。" -f $process.ProcessName, $process.Id) -ForegroundColor Yellow
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        # 句柄不是立刻释放的，等它真的退出再让 publish 去覆盖。
        $null = $process.WaitForExit(5000)
    }
}

Write-Host "微信流 Windows 客户端 · $Edition" -ForegroundColor White
Write-Host "仓库：$repositoryRoot"
Write-Host "产物：$OutputDirectory"
Write-Host ''

# ---------------------------------------------------------------- 构建
Write-Step '构建自包含产物'
Stop-ProductRunningFrom $OutputDirectory
& $buildScript -Edition $Edition -OutputDirectory $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "构建失败（退出码 $LASTEXITCODE）。" }

# ---------------------------------------------------------------- 门禁
if (-not $SkipNetworkCheck) {
    Write-Step '网络命名空间门禁（与 CI 同一条）'
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $networkCheckScript `
        -Edition $Edition -PublishDirectory $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "网络命名空间门禁未通过（退出码 $LASTEXITCODE）。" }
}

# ---------------------------------------------------------------- 分享入口
if ($Register) {
    Write-Step '注册微信「转发到其他应用」分享入口'

    # 开发证书没有就现造一个。new-dev-certificate.ps1 会把叶子证书装进
    # CurrentUser\TrustedPeople，签名才验得过——否则 register-dev.ps1 会在
    # 验签那一步以「签名不被信任」失败。
    $certificate = Get-ChildItem Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
        Where-Object Subject -eq 'CN=WeChatBridge Windows Dev' |
        Sort-Object NotAfter -Descending | Select-Object -First 1
    if (-not $certificate) {
        Write-Host '  未找到开发证书，正在创建……'
        & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $certificateScript
        if ($LASTEXITCODE -ne 0) { throw "开发证书创建失败（退出码 $LASTEXITCODE）。" }
    }

    # 安装根就是产物目录：稀疏 MSIX 通过 ExternalLocation 指向它，
    # 里面必须同时有主程序与 share-target\WeChatBridge.ShareTarget.exe，
    # 还要有图标资源（manifest 的 logo 按外部位置解析，缺了分享菜单里
    # 会静默地少一项）。
    & $windowsPowerShell -NoProfile -ExecutionPolicy Bypass -File $registerScript `
        -InstallRoot $OutputDirectory -Edition $Edition
    if ($LASTEXITCODE -ne 0) { throw "分享入口注册失败（退出码 $LASTEXITCODE）。" }
}

# ---------------------------------------------------------------- 启动
$exe = Join-Path $OutputDirectory 'WeChatBridge.Windows.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "没有找到可执行文件：$exe" }

if (-not $NoRun) {
    Write-Step '启动'
    # 工作目录给成产物目录：AppLayout 用 AppContext.BaseDirectory 判便携模式，
    # 那里有 portable.flag 才是便携版；这里没有，所以是安装版，数据走
    # %LOCALAPPDATA%。工作目录给错不影响判定，但给对了日志与相对路径都自然。
    Start-Process -FilePath $exe -WorkingDirectory $OutputDirectory
}

# ---------------------------------------------------------------- 交代
# 分享入口的真实状态要查，不能靠「这次有没有加 -Register」来推——上一轮注册过
# 的话，这一轮不加 -Register，入口也还在。Appx cmdlet 只在 Windows PowerShell
# 5.1 上可用（PS7 加载 Appx 模块直接失败），所以查询同样走子进程。
function Get-RegisteredSharePackage {
    $command = 'Get-AppxPackage -Name "WeChatBridge.Windows.ShareTarget" -ErrorAction SilentlyContinue | ForEach-Object { $_.PackageFullName }'
    $output = & $windowsPowerShell -NoProfile -Command $command 2>$null
    if ($LASTEXITCODE -ne 0) { return $null }
    $name = $output | Select-Object -First 1
    if ($name) { return $name.Trim() }
    return $null
}

$sharePackage = Get-RegisteredSharePackage
$dataRoot = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)) 'WeChatBridge'
Write-Host ''
Write-Host '完成。' -ForegroundColor Green
Write-Host "  可执行文件 : $exe"
Write-Host "  双击运行   : 上面这个 exe（自包含，本机不需要装 .NET）"
Write-Host "  数据目录   : $dataRoot"
if ($sharePackage) {
    Write-Host "  分享入口   : 已注册（$sharePackage）"
    Write-Host "                微信「转发到其他应用」里应该有「微信流」。取消注册："
    Write-Host "                powershell -ExecutionPolicy Bypass -File `"$unregisterScript`""
} else {
    Write-Host "  分享入口   : 未注册。微信「转发到其他应用」里不会有「微信流」，要加："
    Write-Host "                powershell -ExecutionPolicy Bypass -File `".\build-and-run.ps1`" -Register"
}
Write-Host ''

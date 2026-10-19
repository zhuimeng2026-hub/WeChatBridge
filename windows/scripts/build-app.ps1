[CmdletBinding()]
param(
    # Which of the two products to build. It selects the <Version> from
    # Directory.Build.props, so the caller cannot disagree with the file.
    [ValidateSet('Perpetual', 'Subscription')]
    [string]$Edition = 'Perpetual',

    # Release config only: anything else is a local-developer question and
    # a wrong answer to "is this build a release?" must be loud, not silent.
    [ValidateSet('Release')]
    [string]$Configuration = 'Release',

    # One architecture for now. arm64 is a separate code path, not a flag.
    [ValidateSet('win-x64')]
    [string]$Runtime = 'win-x64',

    # --self-contained is the only mode that produces an offline-runnable
    # payload, which is what this product ships. A framework-dependent
    # build answered with a "missing .NET runtime" dialog is the failure
    # this default exists to prevent.
    [switch]$FrameworkDependent,

    # Where the published files land. Resolved in the body: the param
    # block runs before $PSScriptRoot is reliable, and a default that
    # depends on a path must use the same source the body does.
    [string]$OutputDirectory,

    # Skip the restore step when the caller knows inputs have not changed.
    # Restore is what most of the wall clock goes to on a clean run; the
    # package publish is the only one that has to happen every time.
    [switch]$SkipRestore,

    # Pass -Configuration Debug to dotnet without exposing -c, the same
    # way every other script here does. Quiet banner because the script
    # name is the title and the version is in the props file.
    [switch]$Quiet = $true
)
$ErrorActionPreference = 'Stop'

# $PSScriptRoot is empty when this script is invoked through some shells
# (notably PowerShell's own -File with certain host configurations).
# $MyInvocation.MyCommand.Path is reliable and resolves to the same thing
# in every other case. Fall back to it explicitly.
$scriptPath = $PSScriptRoot
if (-not $scriptPath) { $scriptPath = Split-Path -Parent $MyInvocation.MyCommand.Path }
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $scriptPath '..\..'))
if (-not $OutputDirectory) {
    $OutputDirectory = Join-Path $scriptPath '..\artifacts\publish\win-x64'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

# The product version lives in Directory.Build.props. Five call sites used to
# hardcode it independently and drifted silently; this one reads it back out
# by the same edition the packaging script does, so a release cannot bump
# one without the other.
#
# -Encoding UTF8 is mandatory: the props file carries Chinese display names,
# and Windows PowerShell 5.1 would otherwise decode it as ANSI/GBK.
$propsPath = Join-Path $repositoryRoot 'windows\Directory.Build.props'
[xml]$props = Get-Content -LiteralPath $propsPath -Raw -Encoding UTF8
$editionGroups = @($props.Project.PropertyGroup | Where-Object { $_.WeChatBridgeEdition -eq $Edition })
if (-not $editionGroups) {
    throw "Directory.Build.props has no PropertyGroup conditioned on WeChatBridgeEdition='$Edition'."
}
$productVersion = $editionGroups | ForEach-Object { $_.Version } | Where-Object { $_ } | Select-Object -First 1
if (-not $productVersion) {
    throw "Directory.Build.props defines no <Version> for edition '$Edition'."
}
if ($Edition -eq 'Perpetual' -and [version]$productVersion -lt [version]'1.0.1') {
    throw "Perpetual version must be at least 1.0.1: existing Windows application files already use version 1.0.0."
}

# The two projects ship together: the main window process plus the Share
# Target helper that registers as a Windows share handler. Building only
# the main exe is a build that the consumer cannot use, and a build that
# stops at the main exe is the only failure mode a missing helper
# produces (silent: the share pane just does not appear).
$mainProject = Join-Path $repositoryRoot 'windows\src\WeChatBridge.Windows\WeChatBridge.Windows.csproj'
$helperProject = Join-Path $repositoryRoot 'windows\src\WeChatBridge.ShareTarget\WeChatBridge.ShareTarget.csproj'
foreach ($p in @($mainProject, $helperProject)) {
    if (-not (Test-Path -LiteralPath $p)) { throw "Project not found: $p" }
}

# Self-contained is the default because this product is sold as one
# double-click-able thing, and the "double-click, get a missing-runtime
# dialog" failure has happened to enough people that turning it off
# should be a conscious choice, not a hidden one.
$selfContained = -not $FrameworkDependent
$selfContainedFlag = if ($selfContained) { 'true' } else { 'false' }

# The helper manifest carries the publisher subject, which depends on the
# signing certificate. The installer script rewrites it on the fly; the
# publish script does not need to, because the manifest's default
# publisher is read straight back at packaging time. We pass the
# prebaked manifest path only when the caller has one, and otherwise let
# the project fall back to its source file. Quietly not passing it is
# the right answer: this script builds, the installer packages.
$helperManifest = $null
$helperManifestSource = Join-Path $repositoryRoot 'windows\share-target.manifest'
if (Test-Path -LiteralPath $helperManifestSource) {
    $helperManifest = $helperManifestSource
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$helperOutput = Join-Path $OutputDirectory 'share-target'
New-Item -ItemType Directory -Path $helperOutput -Force | Out-Null

$nologo = if ($Quiet) { '--nologo' } else { '' }
$commonPublishArgs = @(
    '-c', $Configuration
    '-r', $Runtime
    '--self-contained', $selfContainedFlag
    "-p:Version=$productVersion"
    "-p:WeChatBridgeEdition=$Edition"
    $nologo
) | Where-Object { $_ -ne '' }

if (-not $SkipRestore) {
    & dotnet restore $mainProject --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Restore failed.' }
}

# The main project owns the WPF process and the bulk of the managed
# dependencies. The helper is a small console-free WinRT component; the
# two share WeChatBridge.Windows.Core, which is restored by the main
# project's restore.
& dotnet publish $mainProject -o $OutputDirectory @commonPublishArgs
if ($LASTEXITCODE -ne 0) { throw 'Main publish failed.' }

# Helper is published second because the main publish builds the Core
# DLL the helper links against, and dotnet picks it up from the same
# bin output anyway. Doing them in this order is a no-op for speed and
# a clear log line for whoever runs the script.
$helperArgs = @($commonPublishArgs)
if ($helperManifest) {
    $helperArgs += "-p:ApplicationManifest=$helperManifest"
}
& dotnet publish $helperProject -o $helperOutput @helperArgs
if ($LASTEXITCODE -ne 0) { throw 'ShareTarget publish failed.' }

# A self-contained payload must not silently become a framework-dependent
# installer. Three files whose absence or wrong version means a customer
# double-clicks and gets a runtime dialog instead of the window.
$expectedMainVersion = [version]"$productVersion.0"
$mainExe = Get-Item -LiteralPath (Join-Path $OutputDirectory 'WeChatBridge.Windows.exe')
if ([version]$mainExe.VersionInfo.FileVersion -ne $expectedMainVersion) {
    throw "Main file version is $($mainExe.VersionInfo.FileVersion), expected $expectedMainVersion."
}
$helperExe = Get-Item -LiteralPath (Join-Path $helperOutput 'WeChatBridge.ShareTarget.exe')
if ([version]$helperExe.VersionInfo.FileVersion -ne $expectedMainVersion) {
    throw "Helper file version is $($helperExe.VersionInfo.FileVersion), expected $expectedMainVersion."
}
if ($selfContained) {
    foreach ($runtimeFile in @('hostfxr.dll','hostpolicy.dll','coreclr.dll','PresentationFramework.dll')) {
        if (-not (Test-Path (Join-Path $OutputDirectory $runtimeFile))) {
            throw "Missing embedded runtime: $runtimeFile (self-contained build did not produce it)"
        }
    }
}

# A self-contained Windows desktop build is 150-200 MB of framework
# files per output directory. Telling the operator how big is the one
# piece of output that is useful before they decide what to do with
# the directory. Each per-project directory carries its own runtime
# copy, so the two numbers are reported separately rather than summed
# (summing would be a misleading "the build is X MB" when the share
# handler and the main app are independent payloads).
foreach ($dir in @($OutputDirectory, (Join-Path $OutputDirectory 'share-target'))) {
    if (Test-Path -LiteralPath $dir) {
        $bytes = (Get-ChildItem $dir -Recurse -File | Measure-Object -Property Length -Sum).Sum
        $mb = [math]::Round($bytes / 1MB, 1)
        $label = if ($dir -eq $OutputDirectory) { 'main' } else { 'share-target' }
        Write-Output "$label : $dir ($mb MB)"
    }
}
Write-Output "Edition: $Edition"
Write-Output "Version: $productVersion"
Write-Output "Self-contained: $selfContained"

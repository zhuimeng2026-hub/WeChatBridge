[CmdletBinding()]
param([Parameter(Mandatory = $true)][string]$InstallRoot)
$ErrorActionPreference = 'Stop'
$InstallRoot = [IO.Path]::GetFullPath($InstallRoot)
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead((Join-Path $InstallRoot 'Lvdangtong.ShareTarget.msix'))
try {
    $entry = $archive.GetEntry('AppxManifest.xml')
    $reader = [IO.StreamReader]::new($entry.Open())
    try { [xml]$manifest = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $application = @($manifest.Package.Applications.Application | Where-Object Id -eq 'Share.Hub')
    if ($application.Count -ne 1) { throw 'Expected exactly one Share.Hub application.' }
    $namespaces = [Xml.XmlNamespaceManager]::new($manifest.NameTable)
    $namespaces.AddNamespace('uap', 'http://schemas.microsoft.com/appx/manifest/uap/windows10')
    $shareTargets = $application[0].SelectNodes('./*[local-name()="Extensions"]/uap:Extension[@Category="windows.shareTarget"]/uap:ShareTarget', $namespaces)
    if ($shareTargets.Count -ne 1 -or -not $shareTargets[0].SelectSingleNode('uap:SupportedFileTypes/uap:SupportsAnyFileType', $namespaces)) {
        throw 'Share target must declare SupportsAnyFileType; narrow file-type lists are filtered out during Weixin share-target enumeration.'
    }
    foreach ($format in @('StorageItems', 'Text', 'URI', 'Bitmap')) {
        if (-not $shareTargets[0].SelectSingleNode("uap:DataFormat[text()='$format']", $namespaces)) {
            throw "Share target is missing DataFormat $format; a single StorageItems-only declaration is filtered out by the Weixin host."
        }
    }
    $helper = Join-Path $InstallRoot $application[0].Executable
    # Same lookup order as pack-msix.ps1's Resolve-SdkTool: PATH, then an
    # explicit override, then the extracted SDK.BuildTools payload, then a real
    # Windows SDK install. mt.exe is absent from PATH on SDK-less dev machines.
    $mt = Get-Command mt.exe -ErrorAction SilentlyContinue
    $mtPath = if ($mt) { $mt.Source } else {
        $roots = @(
            $env:WCB_SDK_BIN,
            (Join-Path $env:LOCALAPPDATA 'WeChatBridge-tools\extracted\bin'),
            "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
        ) | Where-Object { $_ -and (Test-Path $_) }
        $found = $null
        foreach ($root in $roots) {
            $direct = Join-Path $root 'mt.exe'
            if (Test-Path $direct) { $found = $direct; break }
            $candidate = Get-ChildItem (Join-Path $root "*\x64\mt.exe") -ErrorAction SilentlyContinue |
                Sort-Object FullName -Descending | Select-Object -First 1
            if ($candidate) { $found = $candidate.FullName; break }
        }
        $found
    }
    if (-not $mtPath) { throw 'Windows SDK mt.exe is required for package identity verification.' }
    $extracted = Join-Path ([IO.Path]::GetTempPath()) ("WeChatBridge-manifest-$([Guid]::NewGuid().ToString('N')).xml")
    try {
        & $mtPath "-inputresource:$helper;#1" "-out:$extracted" -nologo
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract the helper EXE manifest.' }
        [xml]$native = Get-Content -LiteralPath $extracted -Raw -Encoding UTF8
        $identity = $native.assembly.msix
        if (-not $identity -or $identity.publisher -cne $manifest.Package.Identity.Publisher -or
            $identity.packageName -cne $manifest.Package.Identity.Name -or
            $identity.applicationId -cne $application[0].Id) {
            throw 'Helper EXE identity does not match the MSIX identity.'
        }
    } finally { if (Test-Path -LiteralPath $extracted) { Remove-Item -LiteralPath $extracted -Force } }
    $pri = $archive.GetEntry('resources.pri')
    if (-not $pri) { throw 'MSIX resource index is missing.' }
    $hash = [Security.Cryptography.SHA256]::Create()
    $stream = $pri.Open()
    try { $packageHash = [BitConverter]::ToString($hash.ComputeHash($stream)) }
    finally { $stream.Dispose() }
    $externalPri = Join-Path $InstallRoot 'resources.pri'
    if (-not (Test-Path -LiteralPath $externalPri)) { throw 'External resource index is missing.' }
    try { $externalHash = [BitConverter]::ToString($hash.ComputeHash([IO.File]::ReadAllBytes($externalPri))) }
    finally { $hash.Dispose() }
    if ($packageHash -cne $externalHash) { throw 'External resource index differs from the MSIX.' }
    foreach ($asset in @($manifest.Package.Properties.Logo,
        $application[0].VisualElements.Square44x44Logo,
        $application[0].VisualElements.Square150x150Logo)) {
        if (-not (Test-Path -LiteralPath (Join-Path $InstallRoot $asset))) { throw "External visual asset is missing: $asset" }
    }
    Write-Output 'Share package data formats, identity, external resource index and visual assets verified.'
} finally { $archive.Dispose() }

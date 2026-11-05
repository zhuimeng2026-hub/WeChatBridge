[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Get-Process -Name 'Lvdangtong.ShareTarget', 'Lvdangtong.Windows' -ErrorAction SilentlyContinue |
    Stop-Process -Force -ErrorAction SilentlyContinue
Get-AppxPackage -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in 'Lvdangtong.ShareTarget', 'ChatBridge.Windows.ShareTarget' } |
    Remove-AppxPackage -ErrorAction SilentlyContinue

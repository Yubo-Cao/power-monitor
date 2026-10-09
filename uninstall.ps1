<#
Removes the Power Monitor app, collector task and Start menu entry.
The recorded data (%LOCALAPPDATA%\PowerMonitor) is kept unless -RemoveData.
Run from a normal PowerShell:  .\uninstall.ps1 [-RemoveData]
#>
param([switch]$RemoveData)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$taskName = 'Power Monitor Collector'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\PowerMonitor'
if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
}
Get-Process PowerMonitor, PowerMonitorCollector -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase) } |
    Stop-Process -Force
Start-Sleep -Milliseconds 500
Remove-Item -Force -ErrorAction SilentlyContinue (Join-Path ([Environment]::GetFolderPath('Programs')) '电源监控.lnk')
Remove-Item -Recurse -Force -ErrorAction SilentlyContinue $installRoot
if ($RemoveData) { Remove-Item -Recurse -Force -ErrorAction SilentlyContinue (Join-Path $env:LOCALAPPDATA 'PowerMonitor') }
"removed$(if ($RemoveData) { ' (with data)' })"

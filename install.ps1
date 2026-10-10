<#
Builds Power Monitor and installs it for the current user:
  %LOCALAPPDATA%\Programs\PowerMonitor\   app + collector (one self-contained folder)
  task "Power Monitor Collector"          collector + tray icon at logon (+ 5-minute watchdog)
  Start menu "电源监控"                    the app
Data stays in %LOCALAPPDATA%\PowerMonitor\power.db across reinstalls.

Needs the .NET 10 SDK:  winget install Microsoft.DotNet.SDK.10
Run from a normal PowerShell, no admin:  .\install.ps1
A shell started by an MSIX-packaged app gets its %LOCALAPPDATA% writes redirected
into the package; run the installer from an ordinary shell or a scheduled task.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$taskName = 'Power Monitor Collector'
$installRoot = Join-Path $env:LOCALAPPDATA 'Programs\PowerMonitor'
$collector = Join-Path $installRoot 'PowerMonitorCollector.exe'
$app = Join-Path $installRoot 'PowerMonitor.exe'
$dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { throw ".NET SDK not found at $dotnet" }

$staging = Join-Path ([IO.Path]::GetTempPath()) "power-monitor-publish-$PID"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
try {
    # The collector is NativeAOT (one native exe, ~6 MB working set). That needs the
    # MSVC linker (VS "Desktop development with C++"), found through vswhere; without
    # it, fall back to a JIT build (same behaviour, ~25 MB more memory).
    $env:PATH = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer;$env:PATH"
    $collectorProject = Join-Path $PSScriptRoot 'src\PowerMonitor.Collector'
    & $dotnet publish $collectorProject -c Release -r win-x64 -p:DebugType=none -o $staging --nologo -v quiet
    if ($LASTEXITCODE) {
        Write-Warning 'NativeAOT publish failed (no MSVC toolchain?); installing a JIT collector instead.'
        & $dotnet publish $collectorProject -c Release -r win-x64 --self-contained -p:PublishAot=false `
            -p:PublishReadyToRun=true -p:DebugType=none -o $staging --nologo -v quiet
        if ($LASTEXITCODE) { throw "dotnet publish PowerMonitor.Collector failed ($LASTEXITCODE)" }
    }
    & $dotnet publish (Join-Path $PSScriptRoot 'src\PowerMonitor.App') -c Release -r win-x64 --self-contained `
        -p:PublishReadyToRun=true -p:DebugType=none -o $staging --nologo -v quiet
    if ($LASTEXITCODE) { throw "dotnet publish PowerMonitor.App failed ($LASTEXITCODE)" }

    # Stop only our own processes, matched by their installed path.
    if (Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    }
    Get-Process PowerMonitor, PowerMonitorCollector -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -and $_.Path.StartsWith($installRoot, [StringComparison]::OrdinalIgnoreCase) } |
        Stop-Process -Force
    Start-Sleep -Milliseconds 500

    New-Item -ItemType Directory -Force -Path $installRoot | Out-Null
    robocopy $staging $installRoot /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
    if ($LASTEXITCODE -ge 8) { throw "robocopy failed ($LASTEXITCODE)" }
} finally {
    Remove-Item -Recurse -Force -LiteralPath $staging -ErrorAction SilentlyContinue
}

# The collector is a WinExe (no window), so the task runs it directly. It is
# single-instance (named mutex): the watchdog trigger only restarts it if it died.
$identity = [Security.Principal.WindowsIdentity]::GetCurrent().Name
$action = New-ScheduledTaskAction -Execute $collector -WorkingDirectory $installRoot
$logon = New-ScheduledTaskTrigger -AtLogOn -User $identity
$watchdog = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes 5)
$principal = New-ScheduledTaskPrincipal -UserId $identity -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1)
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger @($logon, $watchdog) -Principal $principal `
    -Settings $settings -Description 'Power Monitor: battery, RAPL and per-app energy samples into %LOCALAPPDATA%\PowerMonitor\power.db.' `
    -Force | Out-Null
Start-ScheduledTask -TaskName $taskName

# WScript.Shell cannot save to a non-ASCII path from a task: save, then rename.
$programs = [Environment]::GetFolderPath('Programs')
$shortcut = Join-Path $programs '电源监控.lnk'
$asciiShortcut = Join-Path $programs 'PowerMonitor.lnk'
$shell = New-Object -ComObject WScript.Shell
$link = $shell.CreateShortcut($asciiShortcut)
$link.TargetPath = $app
$link.WorkingDirectory = $installRoot
$link.IconLocation = "$app,0"
$link.Description = '电量、续航、各应用耗电与电池健康'
$link.Save()
Move-Item -Force -LiteralPath $asciiShortcut -Destination $shortcut

Start-Sleep -Seconds 15
$db = Join-Path $env:LOCALAPPDATA 'PowerMonitor\power.db'
[pscustomobject]@{
    Task = (Get-ScheduledTask -TaskName $taskName).State.ToString()
    Collector = [bool](Get-Process PowerMonitorCollector -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq $collector })
    Database = $db
    DatabaseExists = Test-Path $db
    Log = [string[]]@(Get-Content (Join-Path $env:LOCALAPPDATA 'PowerMonitor\collector.log') -Tail 3 -ErrorAction SilentlyContinue)
    Shortcut = $shortcut
} | ConvertTo-Json

<#
.SYNOPSIS
  Registers a Windows scheduled task that runs "WeatherFeeds.exe --collect" every N minutes.

.DESCRIPTION
  Run this once, as an administrator, on the server that hosts the share. The task runs whether or not
  anyone is logged on. Results go to collector.log and collector-status.json in the history folder.

  -Account chooses who the task runs as:
    NetworkService (default)  Built-in, no password, least privilege. The script grants it Modify on the
                              history folder. Use the server's LOCAL path for -ExePath.
    System                    Built-in, no password, full control of the server. Only use it if the
                              WeatherFeeds folder can't be changed by non-administrators.
    User                      A Windows account you enter (needed if the history folder is on another machine).

.EXAMPLE
  .\install-collector.ps1 -ExePath D:\Shares\WeatherFeeds\WeatherFeeds.exe
.EXAMPLE
  .\install-collector.ps1 -ExePath D:\Shares\WeatherFeeds\WeatherFeeds.exe -Account System -EveryMinutes 15
#>
param(
  [string]$ExePath = (Join-Path $PSScriptRoot 'WeatherFeeds.exe'),
  [ValidateRange(10, 360)][int]$EveryMinutes = 30,
  [ValidateSet('NetworkService', 'System', 'User')][string]$Account = 'NetworkService',
  [string]$TaskName = 'WeatherFeeds Collector'
)
$ErrorActionPreference = 'Stop'

if (-not ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
  throw 'Run this from an elevated PowerShell (Run as administrator).'
}
$ExePath = (Resolve-Path $ExePath).ProviderPath
$dir = Split-Path $ExePath
$sharedPath = Join-Path $dir 'WeatherFeeds.shared.json'
if (-not (Test-Path $sharedPath)) { throw "Missing $sharedPath - copy WeatherFeeds.shared.sample.json there, rename it and fill in contactEmail." }

# Same comment-tolerant parse the app does: drop // comment lines before reading the JSON.
$cfg = ((Get-Content $sharedPath) | Where-Object { $_ -notmatch '^\s*//' }) -join "`n" | ConvertFrom-Json
if (-not $cfg.contactEmail -or $cfg.contactEmail -notmatch '@' -or $cfg.contactEmail -like '*example.com') {
  throw "Set a real contactEmail in $sharedPath first (NWS requires one)."
}
$history = if ($cfg.historyFolder) { [IO.Path]::GetFullPath((Join-Path $dir $cfg.historyFolder)) } else { throw "Set historyFolder in $sharedPath." }

if ($Account -ne 'User' -and ($ExePath.StartsWith('\\') -or $history.StartsWith('\\'))) {
  throw "Built-in accounts can't reliably write to network paths. Use the server's local path for -ExePath (e.g. D:\Shares\WeatherFeeds\WeatherFeeds.exe) and keep historyFolder relative, or use -Account User."
}

New-Item -ItemType Directory -Force $history | Out-Null
$action   = New-ScheduledTaskAction -Execute $ExePath -Argument '--collect' -WorkingDirectory $dir
$trigger  = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes)
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 20) -MultipleInstances IgnoreNew `
              -StartWhenAvailable -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 5)

switch ($Account) {
  'NetworkService' {
    # Least privilege: allow writing the history folder and nothing else.
    icacls $history /grant '*S-1-5-20:(OI)(CI)M' | Out-Null      # S-1-5-20 = NT AUTHORITY\NETWORK SERVICE
    $principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\NETWORK SERVICE' -LogonType ServiceAccount -RunLevel Limited
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
    $who = 'NETWORK SERVICE'
  }
  'System' {
    $principal = New-ScheduledTaskPrincipal -UserId 'NT AUTHORITY\SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings -Principal $principal -Force | Out-Null
    $who = 'SYSTEM'
  }
  'User' {
    $cred = Get-Credential -Message "Windows account the collector runs as (needs write access to $history)"
    Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
      -User $cred.UserName -Password $cred.GetNetworkCredential().Password -RunLevel Limited -Force | Out-Null
    $who = $cred.UserName
  }
}

Write-Host "Registered '$TaskName': runs every $EveryMinutes minutes as $who, writing to $history." -ForegroundColor Green
Write-Host "Test it now:   Start-ScheduledTask -TaskName '$TaskName'"
Write-Host "Then check:    (Get-ScheduledTaskInfo -TaskName '$TaskName').LastTaskResult   # 0 = OK, see collector.log otherwise"

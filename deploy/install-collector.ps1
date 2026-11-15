<#
.SYNOPSIS
  Registers a Windows scheduled task that runs "WeatherFeeds.exe --collect" every N minutes.

.DESCRIPTION
  Run this once, as an administrator, on the always-on server. The task runs whether or not anyone is
  logged on, under the Windows account you enter, which needs write access to the shared history folder.
  Results go to collector.log and collector-status.json in that folder.

.EXAMPLE
  .\install-collector.ps1 -ExePath \\server\WeatherFeeds\WeatherFeeds.exe -EveryMinutes 30
#>
param(
  [string]$ExePath = (Join-Path $PSScriptRoot 'WeatherFeeds.exe'),
  [ValidateRange(10, 360)][int]$EveryMinutes = 30,
  [string]$TaskName = 'WeatherFeeds Collector'
)
$ErrorActionPreference = 'Stop'

if (-not (Test-Path $ExePath)) { throw "WeatherFeeds.exe not found at $ExePath" }
$shared = Join-Path (Split-Path $ExePath) 'WeatherFeeds.shared.json'
if (-not (Test-Path $shared)) { throw "Missing $shared - copy WeatherFeeds.shared.sample.json there, rename it and fill in contactEmail." }

$cred = Get-Credential -Message "Windows account the collector runs as (needs write access to the history folder)"

$action   = New-ScheduledTaskAction -Execute $ExePath -Argument '--collect' -WorkingDirectory (Split-Path $ExePath)
$trigger  = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(1) -RepetitionInterval (New-TimeSpan -Minutes $EveryMinutes)
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Minutes 20) -MultipleInstances IgnoreNew `
              -StartWhenAvailable -RestartCount 2 -RestartInterval (New-TimeSpan -Minutes 5)

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
  -User $cred.UserName -Password $cred.GetNetworkCredential().Password -RunLevel Limited -Force | Out-Null

Write-Host "Registered '$TaskName': runs every $EveryMinutes minutes as $($cred.UserName)." -ForegroundColor Green
Write-Host "Run it now with:  Start-ScheduledTask -TaskName '$TaskName'"

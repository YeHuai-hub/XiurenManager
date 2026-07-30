param(
    [int]$IntervalMinutes = 5,
    [int]$BatchSize = 20,
    [int]$MinimumAgeMinutes = 30,
    [string]$TaskName = "XiurenManager-ResourceMigration"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$migrationScript = Join-Path $PSScriptRoot "migrate-library-batch.ps1"
if (!(Test-Path -LiteralPath $migrationScript)) {
    throw "Migration script not found: $migrationScript"
}

$arguments = @(
    "-NoProfile",
    "-ExecutionPolicy Bypass",
    "-File `"$migrationScript`"",
    "-BatchSize $([Math]::Max(1, $BatchSize))",
    "-MinimumAgeMinutes $([Math]::Max(1, $MinimumAgeMinutes))"
) -join " "

$action = New-ScheduledTaskAction `
    -Execute "powershell.exe" `
    -Argument $arguments `
    -WorkingDirectory $root
$trigger = New-ScheduledTaskTrigger `
    -Once `
    -At (Get-Date).AddMinutes(1) `
    -RepetitionInterval (New-TimeSpan -Minutes ([Math]::Max(1, $IntervalMinutes)))
$principal = New-ScheduledTaskPrincipal `
    -UserId ([Security.Principal.WindowsIdentity]::GetCurrent().Name) `
    -LogonType Interactive `
    -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew

Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Principal $principal `
    -Settings $settings `
    -Description "Batch migration from the legacy Xiuren directory to the categorized resource library." `
    -Force | Out-Null

Get-ScheduledTask -TaskName $TaskName |
    Select-Object TaskName, State

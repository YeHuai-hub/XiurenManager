param(
    [string]$Executable = "",
    [switch]$KeepTestData
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$Executable) {
    $Executable = Join-Path $projectRoot "src\XiurenManager\bin\Release\net8.0-windows\XiurenManager.exe"
}
$base = Join-Path "E:\WorkSpace" ("XiurenDatabaseTest-" + [Guid]::NewGuid().ToString("N"))
$recoverRoot = Join-Path $base "recover"
$failRoot = Join-Path $base "fail-closed"
$oldDataRoot = $env:XIUREN_DATA_ROOT

try {
    foreach ($root in @($recoverRoot, $failRoot)) {
        [IO.Directory]::CreateDirectory((Join-Path $root "data")) | Out-Null
        [IO.Directory]::CreateDirectory((Join-Path $root "config")) | Out-Null
        $libraryRoot = Join-Path $root "empty-library"
        [IO.Directory]::CreateDirectory($libraryRoot) | Out-Null
        [ordered]@{
            DownloadRoot = $libraryRoot
            ArchiveRoot = (Join-Path $root "offline-archive")
            StorageManagementEnabled = $false
            LibraryCategories = @("TestCategory")
            LegacyDownloadRoots = @()
        } | ConvertTo-Json | Set-Content -LiteralPath (
            Join-Path $root "config\settings.json") -Encoding UTF8
    }
    $seed = '{"Resources":[],"Jobs":[],"LocalFiles":[]}'
    [IO.File]::WriteAllText((Join-Path $recoverRoot "data\xiuren.db"), $seed)
    [IO.File]::WriteAllText((Join-Path $failRoot "data\xiuren.db"), ([char]0).ToString() * 4096)

    $env:XIUREN_DATA_ROOT = $recoverRoot
    $seedRun = Start-Process -FilePath $Executable -ArgumentList "--scan-local" -Wait -PassThru
    $lastGood = Join-Path $recoverRoot "data\xiuren.db.last-good"
    if (!(Test-Path -LiteralPath $lastGood)) {
        throw "The first durable save did not create xiuren.db.last-good."
    }
    [IO.File]::WriteAllText(
        (Join-Path $recoverRoot "data\xiuren.db"),
        ([char]0).ToString() * 8192)
    $recoveryRun = Start-Process -FilePath $Executable -ArgumentList "--migrate-catalog" -Wait -PassThru
    $recovered = Get-Content -LiteralPath (Join-Path $recoverRoot "data\xiuren.db") -Raw |
        ConvertFrom-Json

    $env:XIUREN_DATA_ROOT = $failRoot
    $failRun = Start-Process -FilePath $Executable -ArgumentList "--migrate-catalog" -PassThru
    $failedPromptly = $failRun.WaitForExit(15000)
    if (!$failedPromptly) {
        Stop-Process -Id $failRun.Id -Force -ErrorAction SilentlyContinue
    }

    $result = [ordered]@{
        SeedExitCode = $seedRun.ExitCode
        RecoveryExitCode = $recoveryRun.ExitCode
        LastGoodCreated = Test-Path -LiteralPath $lastGood
        MainDatabaseRecovered = $null -ne $recovered.Resources
        RecoveryNoticeCreated = Test-Path -LiteralPath (
            Join-Path $recoverRoot "data\database-recovery-latest.txt")
        DamagedCopyPreserved = @(Get-ChildItem (
            Join-Path $recoverRoot "data\backups") -Filter "xiuren.db.startup-corrupt-*" -File).Count -eq 1
        MissingBackupExitedPromptly = $failedPromptly
        MissingBackupExitCode = if ($failedPromptly) { $failRun.ExitCode } else { -1 }
        TestRoot = $base
    }
    $result | ConvertTo-Json
    if ($seedRun.ExitCode -ne 0 -or $recoveryRun.ExitCode -ne 0 -or
        !$result.LastGoodCreated -or !$result.MainDatabaseRecovered -or
        !$result.RecoveryNoticeCreated -or !$result.DamagedCopyPreserved -or
        !$result.MissingBackupExitedPromptly -or $result.MissingBackupExitCode -ne 5) {
        throw "Database durability integration test failed."
    }
}
finally {
    $env:XIUREN_DATA_ROOT = $oldDataRoot
    if (!$KeepTestData -and [IO.Directory]::Exists($base)) {
        [IO.Directory]::Delete($base, $true)
    }
}

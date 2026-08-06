param(
    [int]$BatchSize = 20,
    [int]$MinimumAgeMinutes = 30
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$installed = [IO.Directory]::EnumerateFiles(
    "E:\Apps",
    "XiurenManager.exe",
    [IO.SearchOption]::AllDirectories) | Select-Object -First 1
$built = Join-Path $root "src\XiurenManager\bin\Release\net8.0-windows\XiurenManager.exe"
$executable = if ($installed -and (Test-Path -LiteralPath $installed)) { $installed } else { $built }
if (!(Test-Path -LiteralPath $executable)) {
    throw "XiurenManager.exe was not found."
}

$process = Start-Process `
    -FilePath $executable `
    -ArgumentList "--migrate-storage-batch" `
    -Wait `
    -PassThru `
    -WindowStyle Hidden
if ($process.ExitCode -ne 0) {
    throw "Storage migration failed with exit code $($process.ExitCode)."
}

param(
    [string]$Executable = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (!$Executable) {
    $Executable = Join-Path $root "src\XiurenManager\bin\Release\net8.0-windows\XiurenManager.exe"
}
$base = Join-Path $root ("tmp\storage-test-" + [Guid]::NewGuid().ToString("N"))
$archiveBase = Join-Path "E:\WorkSpace\XiurenStorageTests" ([Guid]::NewGuid().ToString("N"))
$dataRoot = Join-Path $base "data-root"
$localRoot = Join-Path $base "local"
$archiveRoot = Join-Path $archiveBase "archive"
$setRoot = Join-Path $localRoot "TestCategory\CompleteModel\SetA"
$blockedSet = Join-Path $localRoot "TestCategory\BlockedModel\SetB"
@(
    (Join-Path $dataRoot "config"),
    (Join-Path $dataRoot "data"),
    (Join-Path $dataRoot "logs"),
    $setRoot,
    $blockedSet,
    $archiveRoot
) | ForEach-Object { [IO.Directory]::CreateDirectory($_) | Out-Null }

[IO.File]::WriteAllBytes(
    (Join-Path $setRoot "image.jpg"),
    [byte[]](0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 4, 5, 6, 7, 8))
[IO.File]::WriteAllText(
    (Join-Path $setRoot "video.mp4"),
    "synthetic-video-content")
[IO.File]::WriteAllText(
    (Join-Path $blockedSet "archive.zip.BaiduPCS-Go-downloading"),
    "partial")

$settings = [ordered]@{
    DownloadRoot = $localRoot
    ArchiveRoot = $archiveRoot
    StorageManagementEnabled = $false
    LocalHotBudgetGB = 50
    LocalReserveGB = 50
    ArchiveReserveGB = 50
    MigrationBatchGB = 1
    StorageCheckMinutes = 15
    PinnedLocalModels = @()
    LibraryCategories = @("TestCategory")
    LegacyDownloadRoots = @()
    ImageExts = @(".jpg")
    VideoExts = @(".mp4")
    ArchiveExts = @(".zip")
}
$settings | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $dataRoot "config\settings.json") -Encoding UTF8

$db = [ordered]@{
    Resources = @([ordered]@{
        PostId = "1"
        Title = "SetA"
        Model = "CompleteModel"
        Category = "TestCategory"
        LocalDir = $setRoot
        Status = "Ready"
        DownloadStatus = "Downloaded"
        ExtractStatus = "Extracted"
    })
    Jobs = @()
    LocalFiles = @(
        [ordered]@{
            Category = "TestCategory"
            Model = "CompleteModel"
            Title = "SetA"
            LocalDir = $setRoot
            ImageCount = 1
            VideoCount = 1
            InvalidVideoCount = 0
            TotalBytes = 35
            LastScanned = (Get-Date).ToString("s")
        },
        [ordered]@{
            Category = "TestCategory"
            Model = "BlockedModel"
            Title = "SetB"
            LocalDir = $blockedSet
            ImageCount = 0
            VideoCount = 0
            InvalidVideoCount = 0
            TotalBytes = 7
            LastScanned = (Get-Date).ToString("s")
        }
    )
}
$db | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $dataRoot "data\xiuren.db") -Encoding UTF8

$favorites = @([ordered]@{
    LocalDir = $setRoot
    Model = "CompleteModel"
    Title = "SetA"
    Score = 3
    Tags = @("test")
    UpdatedAt = (Get-Date).ToString("s")
})
(ConvertTo-Json -InputObject $favorites -Depth 10) |
    Set-Content -LiteralPath (Join-Path $dataRoot "data\favorites.json") -Encoding UTF8

$oldDataRoot = $env:XIUREN_DATA_ROOT
$env:XIUREN_DATA_ROOT = $dataRoot
try {
    $process = Start-Process `
        -FilePath $Executable `
        -ArgumentList @("--migrate-storage-model", "CompleteModel") `
        -Wait `
        -PassThru
}
finally {
    $env:XIUREN_DATA_ROOT = $oldDataRoot
}

$env:XIUREN_DATA_ROOT = $dataRoot
try {
    $blockedProcess = Start-Process `
        -FilePath $Executable `
        -ArgumentList @("--migrate-storage-model", "BlockedModel") `
        -Wait `
        -PassThru
}
finally {
    $env:XIUREN_DATA_ROOT = $oldDataRoot
}

$targetSet = Join-Path $archiveRoot "TestCategory\CompleteModel\SetA"
$loadedDb = Get-Content `
    -LiteralPath (Join-Path $dataRoot "data\xiuren.db") `
    -Raw `
    -Encoding UTF8 | ConvertFrom-Json
$loadedFavorites = Get-Content `
    -LiteralPath (Join-Path $dataRoot "data\favorites.json") `
    -Raw `
    -Encoding UTF8 | ConvertFrom-Json
$completeLocal = @($loadedDb.LocalFiles | Where-Object Model -eq "CompleteModel")[0]
$completeResource = @($loadedDb.Resources | Where-Object Model -eq "CompleteModel")[0]
$completeFavorite = @($loadedFavorites | Where-Object Model -eq "CompleteModel")[0]
$migrationState = Get-Content `
    -LiteralPath (Join-Path $dataRoot "data\storage-migration-state.json") `
    -Raw `
    -Encoding UTF8 | ConvertFrom-Json
$result = [ordered]@{
    ExitCode = $process.ExitCode
    SourceRemoved = !(Test-Path -LiteralPath (Join-Path $localRoot "TestCategory\CompleteModel"))
    TargetExists = Test-Path -LiteralPath $targetSet
    TargetFileCount = @(Get-ChildItem -LiteralPath $targetSet -File -Recurse).Count
    DbPathUpdated = [string]$completeLocal.LocalDir -eq $targetSet
    DbTierUpdated = [string]$completeLocal.StorageTier -eq "NAS"
    ResourcePathUpdated = [string]$completeResource.LocalDir -eq $targetSet
    FavoritePathUpdated = [string]$completeFavorite.LocalDir -eq $targetSet
    BlockedSourcePreserved = Test-Path -LiteralPath $blockedSet
    BlockedTargetAbsent = !(Test-Path -LiteralPath (Join-Path $archiveRoot "TestCategory\BlockedModel"))
    BlockedMoveRejected = [string]$migrationState.Status -eq "Failed" -and
        ![string]::IsNullOrWhiteSpace([string]$migrationState.LastError)
    TestRoot = $base
    ArchiveTestRoot = $archiveBase
}
$failed = @($result.GetEnumerator() | Where-Object {
    $_.Key -notin @("ExitCode", "TargetFileCount", "TestRoot") -and !$_.Value
})
if ($process.ExitCode -ne 0 -or $blockedProcess.ExitCode -ne 0 -or
    $result.TargetFileCount -ne 2 -or $failed.Count -gt 0) {
    $result | ConvertTo-Json
    throw "Storage migration integration test failed."
}
$result | ConvertTo-Json

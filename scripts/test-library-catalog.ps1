param(
    [string]$Executable = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (!$Executable) {
    $Executable = Join-Path $root "src\XiurenManager\bin\Debug\net8.0-windows\XiurenManager.exe"
}
$base = Join-Path "E:\WorkSpace" ("XiurenCatalogTest-" + [Guid]::NewGuid().ToString("N"))
$dataRoot = Join-Path $base "_Tool"
$libraryRoot = Join-Path $base "library"
$presentSet = Join-Path $libraryRoot "TestCategory\ModelA\SetPresent"
$missingSet = Join-Path $libraryRoot "TestCategory\ModelA\SetMissing"
@(
    (Join-Path $dataRoot "config"),
    (Join-Path $dataRoot "data"),
    (Join-Path $dataRoot "logs"),
    $presentSet
) | ForEach-Object { [IO.Directory]::CreateDirectory($_) | Out-Null }

$jpeg = [Convert]::FromBase64String(
    "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EB//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EB//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EB//2Q==")
[IO.File]::WriteAllBytes((Join-Path $presentSet "cover.jpg"), $jpeg)
[IO.File]::WriteAllBytes((Join-Path $presentSet "second.jpg"), $jpeg)

$settings = [ordered]@{
    DownloadRoot = $libraryRoot
    ArchiveRoot = (Join-Path $base "offline-nas")
    StorageManagementEnabled = $false
    LibraryCategories = @("TestCategory")
    LegacyDownloadRoots = @()
    ImageExts = @(".jpg")
    VideoExts = @(".mp4")
    ArchiveExts = @(".zip")
}
$settings | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $dataRoot "config\settings.json") -Encoding UTF8

$resource = [ordered]@{
    PostId = "1001"
    Title = "SetPresent"
    Model = "ModelA"
    Category = "TestCategory"
    DetailUrl = "https://example.invalid/1001.html"
    PanUrl = "https://pan.example.invalid/share"
    PanPassword = "abcd"
    ExtractPassword = "secret"
    LocalDir = $presentSet
    Status = "Done"
    DownloadStatus = "Downloaded"
    ExtractStatus = "Extracted"
}
$database = [ordered]@{
    Resources = @($resource)
    Jobs = @()
    LocalFiles = @(
        [ordered]@{
            Category = "TestCategory"
            Model = "ModelA"
            Title = "SetPresent"
            LocalDir = $presentSet
            StorageTier = "本地"
            ImageCount = 2
            VideoCount = 0
            TotalBytes = $jpeg.Length * 2
            LastScanned = (Get-Date).ToString("s")
        },
        [ordered]@{
            Category = "TestCategory"
            Model = "ModelA"
            Title = "SetMissing"
            LocalDir = $missingSet
            StorageTier = "本地"
            ImageCount = 12
            VideoCount = 1
            TotalBytes = 123456
            LastScanned = (Get-Date).AddDays(-1).ToString("s")
        }
    )
}
$database | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath (Join-Path $dataRoot "data\xiuren.db") -Encoding UTF8
"[]" | Set-Content -LiteralPath (Join-Path $dataRoot "data\favorites.json") -Encoding UTF8

$oldDataRoot = $env:XIUREN_DATA_ROOT
$env:XIUREN_DATA_ROOT = $dataRoot
try {
    $first = Start-Process -FilePath $Executable -ArgumentList "--scan-local" -Wait -PassThru
}
finally {
    $env:XIUREN_DATA_ROOT = $oldDataRoot
}

$ledgerPath = Join-Path $dataRoot "data\library-ledger-v1.json"
$ledger = Get-Content -LiteralPath $ledgerPath -Raw -Encoding UTF8 | ConvertFrom-Json
$present = @($ledger.Items | Where-Object Title -eq "SetPresent")[0]
$missing = @($ledger.Items | Where-Object Title -eq "SetMissing")[0]
$manifestPath = Join-Path $dataRoot ("data\manifests\" + $present.SetId.Substring(0, 2) + "\" + $present.SetId + ".json")
$coverPath = Join-Path $dataRoot ("cache\covers\" + $present.SetId + ".jpg")
$ids = @($ledger.Items | ForEach-Object SetId)

Remove-Item -LiteralPath $coverPath -Force -ErrorAction SilentlyContinue
$env:XIUREN_DATA_ROOT = $dataRoot
try {
    $warmCover = Start-Process -FilePath $Executable -ArgumentList @("--warm-cover", $presentSet) -Wait -PassThru
    $warmViewer = Start-Process -FilePath $Executable -ArgumentList @(
        "--warm-viewer-image",
        (Join-Path $presentSet "cover.jpg")
    ) -Wait -PassThru
}
finally {
    $env:XIUREN_DATA_ROOT = $oldDataRoot
}

Remove-Item -LiteralPath (Join-Path $presentSet "second.jpg") -Force
$env:XIUREN_DATA_ROOT = $dataRoot
try {
    $second = Start-Process -FilePath $Executable -ArgumentList "--scan-local" -Wait -PassThru
}
finally {
    $env:XIUREN_DATA_ROOT = $oldDataRoot
}
$ledgerAfterPartial = Get-Content -LiteralPath $ledgerPath -Raw -Encoding UTF8 | ConvertFrom-Json
$presentAfterPartial = @($ledgerAfterPartial.Items | Where-Object SetId -eq $present.SetId)[0]

Remove-Item -LiteralPath $presentSet -Recurse -Force
$env:XIUREN_DATA_ROOT = $dataRoot
try {
    $third = Start-Process -FilePath $Executable -ArgumentList "--scan-local" -Wait -PassThru
}
finally {
    $env:XIUREN_DATA_ROOT = $oldDataRoot
}
$ledgerAfterDelete = Get-Content -LiteralPath $ledgerPath -Raw -Encoding UTF8 | ConvertFrom-Json
$presentAfterDelete = @($ledgerAfterDelete.Items | Where-Object SetId -eq $present.SetId)[0]

$missingToDelete = @($ledgerAfterDelete.Items | Where-Object Title -eq "SetMissing")[0]
$missingToDelete.Availability = "Deleted"
$missingToDelete.AvailabilityReason = "test deletion"
$ledgerAfterDelete | ConvertTo-Json -Depth 20 |
    Set-Content -LiteralPath $ledgerPath -Encoding UTF8
$env:XIUREN_DATA_ROOT = $dataRoot
try {
    $fourth = Start-Process -FilePath $Executable -ArgumentList "--scan-local" -Wait -PassThru
}
finally {
    $env:XIUREN_DATA_ROOT = $oldDataRoot
}
$ledgerAfterDeletedScan = Get-Content -LiteralPath $ledgerPath -Raw -Encoding UTF8 | ConvertFrom-Json
$deletedAfterScan = @($ledgerAfterDeletedScan.Items | Where-Object SetId -eq $missingToDelete.SetId)[0]

$result = [ordered]@{
    FirstExitCode = $first.ExitCode
    SecondExitCode = $second.ExitCode
    ThirdExitCode = $third.ExitCode
    FourthExitCode = $fourth.ExitCode
    WarmCoverExitCode = $warmCover.ExitCode
    WarmViewerExitCode = $warmViewer.ExitCode
    ItemCountPreserved = @($ledger.Items).Count -eq 2 -and @($ledgerAfterDelete.Items).Count -eq 2
    UniqueStableIds = $ids.Count -eq (@($ids | Sort-Object -Unique)).Count -and
        ![string]::IsNullOrWhiteSpace([string]$present.SetId)
    PresentWasIndexed = [string]$present.Availability -eq "Available" -and
        (Test-Path -LiteralPath $manifestPath)
    VisibleCoverCreatedOnDemand = $warmCover.ExitCode -eq 0 -and
        (Test-Path -LiteralPath $coverPath) -and
        (Get-Item -LiteralPath $coverPath).Length -gt 0
    ViewerImageCacheReused = $warmViewer.ExitCode -eq 0
    MissingHistoryPreserved = [string]$missing.Availability -eq "Missing" -and
        [int]$missing.ImageCount -eq 12 -and [int]$missing.VideoCount -eq 1
    SourceMetadataPreserved = [string]$present.SourcePostId -eq "1001" -and
        [string]$present.SourceUrl -eq "https://example.invalid/1001.html" -and
        [string]$present.PanPassword -eq "abcd" -and
        [string]$present.ExtractPassword -eq "secret"
    PartialLossDetected = [string]$presentAfterPartial.Availability -eq "Partial" -and
        [int]$presentAfterPartial.ImageCount -eq 1 -and
        [int]$presentAfterPartial.ExpectedImageCount -eq 2
    DeletedDirectoryRetained = [string]$presentAfterDelete.Availability -eq "Missing" -and
        [string]$presentAfterDelete.SetId -eq [string]$present.SetId -and
        [int]$presentAfterDelete.ImageCount -eq 1 -and
        [int]$presentAfterDelete.ExpectedImageCount -eq 2
    ExplicitDeletedStatusPreserved = [string]$deletedAfterScan.Availability -eq "Deleted"
    TestRoot = $base
}
$failed = @($result.GetEnumerator() | Where-Object {
    $_.Key -notin @(
        "FirstExitCode",
        "SecondExitCode",
        "ThirdExitCode",
        "FourthExitCode",
        "WarmCoverExitCode",
        "WarmViewerExitCode",
        "TestRoot"
    ) -and !$_.Value
})
if ($first.ExitCode -ne 0 -or $second.ExitCode -ne 0 -or $third.ExitCode -ne 0 -or
    $fourth.ExitCode -ne 0 -or $warmCover.ExitCode -ne 0 -or
    $warmViewer.ExitCode -ne 0 -or $failed.Count -gt 0) {
    $result | ConvertTo-Json
    throw "Library catalog integration test failed."
}
$result | ConvertTo-Json

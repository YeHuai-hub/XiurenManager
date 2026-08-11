param([string]$Executable = "")

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
if (!$Executable) {
    $Executable = Join-Path $root "src\XiurenManager\bin\Debug\net8.0-windows\XiurenManager.exe"
}
$base = Join-Path "E:\WorkSpace" ("XiurenMergeTest-" + [Guid]::NewGuid().ToString("N"))
$dataRoot = Join-Path $base "_Tool"
$libraryRoot = Join-Path $base "library"
$modelRoot = Join-Path $libraryRoot "TestCategory\ModelA"
$upper = Join-Path $modelRoot "Photo Set - 1"
$middle = Join-Path $modelRoot "Photo Set - 2"
$lower = Join-Path $modelRoot "Photo Set - 3"
$conflictOne = Join-Path $modelRoot "Conflict Set - 1"
$conflictTwo = Join-Path $modelRoot "Conflict Set - 2"
$conflictTarget = Join-Path $modelRoot "Conflict Set Complete"
$ledgerOne = Join-Path $modelRoot "Ledger Part - 1"
$ledgerTwo = Join-Path $modelRoot "Ledger Part - 2"
@((Join-Path $dataRoot "config"), (Join-Path $dataRoot "data"),
  (Join-Path $dataRoot "logs"), $upper, $middle, $lower,
  $conflictOne, $conflictTwo, $conflictTarget, $ledgerOne, $ledgerTwo) |
    ForEach-Object { [IO.Directory]::CreateDirectory($_) | Out-Null }

$jpeg = [Convert]::FromBase64String(
    "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EB//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EB//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EB//2Q==")
foreach ($directory in @($upper, $middle, $lower)) {
    [IO.File]::WriteAllBytes((Join-Path $directory "same-name.jpg"), $jpeg)
}
[IO.File]::WriteAllBytes((Join-Path $conflictOne "one.jpg"), $jpeg)
[IO.File]::WriteAllBytes((Join-Path $conflictTwo "two.jpg"), $jpeg)
[IO.File]::WriteAllBytes((Join-Path $ledgerOne "one.jpg"), $jpeg)
[IO.File]::WriteAllBytes((Join-Path $ledgerTwo "two.jpg"), $jpeg)

([ordered]@{
    DownloadRoot = $libraryRoot
    ArchiveRoot = (Join-Path $base "offline-nas")
    StorageManagementEnabled = $false
    LibraryCategories = @("TestCategory")
    LegacyDownloadRoots = @()
    ImageExts = @(".jpg")
    VideoExts = @(".mp4")
    ArchiveExts = @(".zip")
}) | ConvertTo-Json -Depth 20 |
    Set-Content -LiteralPath (Join-Path $dataRoot "config\settings.json") -Encoding UTF8

$parts = @(
    [ordered]@{ Id = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; Title = "Photo Set - 1"; Path = $upper; Score = 1; Tags = @("indoor", "white") },
    [ordered]@{ Id = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"; Title = "Photo Set - 2"; Path = $middle; Score = 2; Tags = @("white", "closeup") },
    [ordered]@{ Id = "cccccccccccccccccccccccccccccccc"; Title = "Photo Set - 3"; Path = $lower; Score = 3; Tags = @("video", "indoor") }
)
$resources = @()
$localFiles = @()
$favorites = @()
for ($index = 0; $index -lt $parts.Count; $index++) {
    $part = $parts[$index]
    $resources += [ordered]@{
        PostId = "100$index"; Title = $part.Title; Model = "ModelA"; Category = "TestCategory"
        DetailUrl = "https://example.invalid/$index.html"; PanUrl = "https://pan.example.invalid/$index"
        PanPassword = "p$index"; ExtractPassword = "e$index"; LocalDir = $part.Path
        Status = "Done"; DownloadStatus = "Downloaded"; ExtractStatus = "Extracted"
    }
    $localFiles += [ordered]@{
        SetId = $part.Id; SourcePostId = "100$index"; SourceUrl = "https://example.invalid/$index.html"
        PanUrl = "https://pan.example.invalid/$index"; PanPassword = "p$index"; ExtractPassword = "e$index"
        Category = "TestCategory"; Model = "ModelA"; Title = $part.Title; LocalDir = $part.Path
        ImageCount = 1; VideoCount = 0; TotalBytes = $jpeg.Length
        LastScanned = (Get-Date).ToString("s"); Availability = "Available"
    }
    $favorites += [ordered]@{
        SetId = $part.Id; LocalDir = $part.Path; Model = "ModelA"; Title = $part.Title
        Score = $part.Score; Tags = $part.Tags; UpdatedAt = (Get-Date).ToString("s")
    }
}
foreach ($conflict in @(
    [ordered]@{ Id = "dddddddddddddddddddddddddddddddd"; Title = "Conflict Set - 1"; Path = $conflictOne },
    [ordered]@{ Id = "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"; Title = "Conflict Set - 2"; Path = $conflictTwo }
)) {
    $resources += [ordered]@{
        PostId = $conflict.Id.Substring(0, 4); Title = $conflict.Title; Model = "ModelA"; Category = "TestCategory"
        DetailUrl = "https://example.invalid/conflict/$($conflict.Id).html"; LocalDir = $conflict.Path; Status = "Done"
    }
    $localFiles += [ordered]@{
        SetId = $conflict.Id; Category = "TestCategory"; Model = "ModelA"; Title = $conflict.Title
        LocalDir = $conflict.Path; ImageCount = 1; TotalBytes = $jpeg.Length
        LastScanned = (Get-Date).ToString("s"); Availability = "Available"
    }
}
foreach ($ledgerPart in @(
    [ordered]@{ Id = "ffffffffffffffffffffffffffffffff"; Title = "Ledger Part - 1"; Path = $ledgerOne },
    [ordered]@{ Id = "11111111111111111111111111111111"; Title = "Ledger Part - 2"; Path = $ledgerTwo }
)) {
    $localFiles += [ordered]@{
        SetId = $ledgerPart.Id; Category = "TestCategory"; Model = "ModelA"; Title = $ledgerPart.Title
        LocalDir = $ledgerPart.Path; ImageCount = 1; TotalBytes = $jpeg.Length
        LastScanned = (Get-Date).ToString("s"); Availability = "Available"
    }
}
$localFiles += [ordered]@{
    SetId = "22222222222222222222222222222222"; Category = "TestCategory"; Model = "ModelA"
    Title = "Ledger Conflict Complete"; LocalDir = (Join-Path $modelRoot "missing-ledger-conflict")
    ImageCount = 4; LastScanned = (Get-Date).ToString("s"); Availability = "Missing"
}
([ordered]@{ Resources = $resources; Jobs = @(); LocalFiles = $localFiles }) |
    ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $dataRoot "data\xiuren.db") -Encoding UTF8
$favorites | ConvertTo-Json -Depth 20 |
    Set-Content -LiteralPath (Join-Path $dataRoot "data\favorites.json") -Encoding UTF8

$requestPath = Join-Path $base "merge-request.json"
([ordered]@{ Title = "Photo Set Complete"; SourceDirectories = @($lower, $upper, $middle) }) |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $requestPath -Encoding UTF8
$conflictRequestPath = Join-Path $base "conflict-request.json"
([ordered]@{ Title = "Conflict Set Complete"; SourceDirectories = @($conflictOne, $conflictTwo) }) |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $conflictRequestPath -Encoding UTF8
$ledgerConflictRequestPath = Join-Path $base "ledger-conflict-request.json"
([ordered]@{ Title = "Ledger Conflict Complete"; SourceDirectories = @($ledgerOne, $ledgerTwo) }) |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ledgerConflictRequestPath -Encoding UTF8

$oldDataRoot = $env:XIUREN_DATA_ROOT
$env:XIUREN_DATA_ROOT = $dataRoot
try {
    $merge = Start-Process -FilePath $Executable -ArgumentList @("--merge-sets-test", $requestPath) -Wait -PassThru
    $conflictMerge = Start-Process -FilePath $Executable -ArgumentList @("--merge-sets-test", $conflictRequestPath) -Wait -PassThru
    $ledgerConflictMerge = Start-Process -FilePath $Executable -ArgumentList @("--merge-sets-test", $ledgerConflictRequestPath) -Wait -PassThru

    $recoveryStaging = Join-Path $modelRoot ".merge-recovery-test"
    [IO.Directory]::CreateDirectory($recoveryStaging) | Out-Null
    $recoveryChild = Join-Path $recoveryStaging "01 - Ledger Part - 1"
    [IO.Directory]::Move($ledgerOne, $recoveryChild)
    $ledgerSource = @($localFiles | Where-Object Title -eq "Ledger Part - 1")[0]
    ([ordered]@{
        Schema = "xiuren-set-merge/v1"
        Target = (Join-Path $modelRoot "Recovery Never Created")
        Staging = $recoveryStaging
        CreatedAt = (Get-Date).ToString("s")
        Parts = @([ordered]@{
            Source = $ledgerSource
            OriginalDirectory = $ledgerOne
            ChildName = "01 - Ledger Part - 1"
        })
    }) | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath (Join-Path $dataRoot "data\set-merge-transaction.json") -Encoding UTF8
    $scan = Start-Process -FilePath $Executable -ArgumentList "--scan-local" -Wait -PassThru
}
finally {
    $env:XIUREN_DATA_ROOT = $oldDataRoot
}

$target = Join-Path $modelRoot "Photo Set Complete"
$ledger = Get-Content -LiteralPath (Join-Path $dataRoot "data\library-ledger-v1.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$database = Get-Content -LiteralPath (Join-Path $dataRoot "data\xiuren.db") -Raw -Encoding UTF8 | ConvertFrom-Json
$favoriteRows = @(Get-Content -LiteralPath (Join-Path $dataRoot "data\favorites.json") -Raw -Encoding UTF8 | ConvertFrom-Json)
$merged = @($ledger.Items | Where-Object Title -eq "Photo Set Complete")[0]
$history = @($ledger.Items | Where-Object { $_.Title -like "Photo Set - *" })
$manifestPath = Join-Path $dataRoot ("data\manifests\" + $merged.SetId.Substring(0, 2) + "\" + $merged.SetId + ".json")
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$sidecarPath = @(Get-ChildItem -LiteralPath $target -Filter "*.md" -File)[0].FullName
$sidecar = Get-Content -LiteralPath $sidecarPath -Raw -Encoding UTF8
$expectedParts = @("01 - Photo Set - 1", "02 - Photo Set - 2", "03 - Photo Set - 3")
$actualParts = @(Get-ChildItem -LiteralPath $target -Directory | Sort-Object Name | ForEach-Object Name)
$resourcePaths = @($database.Resources | ForEach-Object LocalDir)
$duplicates = @($expectedParts | Where-Object { !(Test-Path -LiteralPath (Join-Path $target "$_\same-name.jpg")) })
$missingResourcePaths = @($expectedParts | Where-Object { $resourcePaths -notcontains (Join-Path $target $_) })

$result = [ordered]@{
    MergeExitCode = $merge.ExitCode
    ScanExitCode = $scan.ExitCode
    ConflictExitCode = $conflictMerge.ExitCode
    LedgerConflictExitCode = $ledgerConflictMerge.ExitCode
    OrderedPartsPreserved = (@(Compare-Object $expectedParts $actualParts).Count -eq 0)
    DuplicateNamesPreserved = $duplicates.Count -eq 0
    SourceDirectoriesRemoved = !(Test-Path $upper) -and !(Test-Path $middle) -and !(Test-Path $lower)
    MergedCatalogAvailable = [string]$merged.Availability -eq "Available" -and [int]$merged.ImageCount -eq 3
    MergedPartsPersisted = @($merged.MergedParts).Count -eq 3 -and @($manifest.MergedParts).Count -eq 3
    SourceHistoryArchived = $history.Count -eq 3 -and @($history | Where-Object Availability -ne "Deleted").Count -eq 0
    ResourcePathsRelocated = $missingResourcePaths.Count -eq 0
    FavoriteScoreMerged = $favoriteRows.Count -eq 1 -and [int]$favoriteRows[0].Score -eq 6
    FavoriteTagsMerged = @($favoriteRows[0].Tags | Sort-Object -Unique).Count -eq 4
    SidecarListsAllParts = $sidecar.Contains("Photo Set - 1") -and $sidecar.Contains("Photo Set - 2") -and $sidecar.Contains("Photo Set - 3")
    ConflictRejectedWithoutMoves = $conflictMerge.ExitCode -eq 2 -and
        (Test-Path $conflictOne) -and (Test-Path $conflictTwo) -and (Test-Path $conflictTarget)
    LedgerConflictRejected = $ledgerConflictMerge.ExitCode -eq 2 -and
        (Test-Path $ledgerOne) -and (Test-Path $ledgerTwo) -and
        !(Test-Path (Join-Path $modelRoot "Ledger Conflict Complete"))
    InterruptedMergeRecovered = (Test-Path $ledgerOne) -and
        !(Test-Path $recoveryStaging) -and
        !(Test-Path (Join-Path $dataRoot "data\set-merge-transaction.json"))
    TestRoot = $base
}
$failed = @($result.GetEnumerator() | Where-Object {
    $_.Key -notin @("MergeExitCode", "ScanExitCode", "ConflictExitCode", "LedgerConflictExitCode", "TestRoot") -and !$_.Value
})
if ($merge.ExitCode -ne 0 -or $scan.ExitCode -ne 0 -or
    $conflictMerge.ExitCode -ne 2 -or $ledgerConflictMerge.ExitCode -ne 2 -or
    $failed.Count -gt 0) {
    $result | ConvertTo-Json
    throw "Set merge integration test failed."
}
$result | ConvertTo-Json

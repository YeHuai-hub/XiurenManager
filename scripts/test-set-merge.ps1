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
$resources += [ordered]@{
    PostId = "resource-conflict"; Title = "Resource Conflict Complete"; Model = "ModelA"; Category = "TestCategory"
    DetailUrl = "https://example.invalid/resource-conflict.html"; LocalDir = ""; Status = "Ready"
}
$favorites += [ordered]@{
    SetId = "favorite-conflict"; LocalDir = (Join-Path $modelRoot "missing-favorite-conflict")
    Model = "ModelA"; Title = "Favorite Conflict Complete"; Score = 9; Tags = @("conflict")
    UpdatedAt = (Get-Date).ToString("s")
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
$resourceConflictRequestPath = Join-Path $base "resource-conflict-request.json"
([ordered]@{ Title = "Resource Conflict Complete"; SourceDirectories = @($ledgerOne, $ledgerTwo) }) |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $resourceConflictRequestPath -Encoding UTF8
$favoriteConflictRequestPath = Join-Path $base "favorite-conflict-request.json"
([ordered]@{ Title = "Favorite Conflict Complete"; SourceDirectories = @($ledgerOne, $ledgerTwo) }) |
    ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $favoriteConflictRequestPath -Encoding UTF8
$expectedParts = @("01 - Photo Set - 1", "02 - Photo Set - 2", "03 - Photo Set - 3")

$oldDataRoot = $env:XIUREN_DATA_ROOT
$env:XIUREN_DATA_ROOT = $dataRoot
try {
    $merge = Start-Process -FilePath $Executable -ArgumentList @("--merge-sets-test", $requestPath) -Wait -PassThru
    $conflictMerge = Start-Process -FilePath $Executable -ArgumentList @("--merge-sets-test", $conflictRequestPath) -Wait -PassThru
    $ledgerConflictMerge = Start-Process -FilePath $Executable -ArgumentList @("--merge-sets-test", $ledgerConflictRequestPath) -Wait -PassThru
    $resourceConflictMerge = Start-Process -FilePath $Executable -ArgumentList @("--merge-sets-test", $resourceConflictRequestPath) -Wait -PassThru
    $favoriteConflictMerge = Start-Process -FilePath $Executable -ArgumentList @("--merge-sets-test", $favoriteConflictRequestPath) -Wait -PassThru

    $databaseBeforeRepair = Get-Content -LiteralPath (Join-Path $dataRoot "data\xiuren.db") -Raw -Encoding UTF8 | ConvertFrom-Json
    for ($index = 0; $index -lt 3; $index++) {
        @($databaseBeforeRepair.Resources | Where-Object PostId -eq "100$index")[0].LocalDir = $parts[$index].Path
    }
    $databaseBeforeRepair | ConvertTo-Json -Depth 30 |
        Set-Content -LiteralPath (Join-Path $dataRoot "data\xiuren.db") -Encoding UTF8
    $favorites | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath (Join-Path $dataRoot "data\favorites.json") -Encoding UTF8
    $commitJournalParts = @()
    for ($index = 0; $index -lt 3; $index++) {
        $source = @($localFiles | Where-Object Title -eq $parts[$index].Title)[0]
        $commitJournalParts += [ordered]@{
            Source = $source
            OriginalDirectory = $parts[$index].Path
            ChildName = $expectedParts[$index]
        }
    }
    ([ordered]@{
        Schema = "xiuren-set-merge/v1"
        Target = (Join-Path $modelRoot "Photo Set Complete")
        Staging = (Join-Path $modelRoot ".merge-unused")
        CreatedAt = (Get-Date).ToString("s")
        Parts = $commitJournalParts
    }) | ConvertTo-Json -Depth 30 |
        Set-Content -LiteralPath (Join-Path $dataRoot "data\set-merge-transaction.json") -Encoding UTF8
    $committedRecovery = Start-Process -FilePath $Executable -ArgumentList "--migrate-catalog" -Wait -PassThru
    $databaseAfterRepair = Get-Content -LiteralPath (Join-Path $dataRoot "data\xiuren.db") -Raw -Encoding UTF8 | ConvertFrom-Json
    $repairedResources = @($databaseAfterRepair.Resources | Where-Object { $_.PostId -in @("1000", "1001", "1002") })
    $repairedFavorites = @(Get-Content -LiteralPath (Join-Path $dataRoot "data\favorites.json") -Raw -Encoding UTF8 |
        ConvertFrom-Json | ForEach-Object { $_ })
    $repairedMergedFavorite = @($repairedFavorites | Where-Object Title -eq "Photo Set Complete")[0]
    $committedRecoveryRepaired = $committedRecovery.ExitCode -eq 0 -and
        @($repairedResources | Where-Object { $_.LocalDir -notlike "*Photo Set Complete*" }).Count -eq 0 -and
        [int]$repairedMergedFavorite.Score -eq 6 -and
        !(Test-Path (Join-Path $dataRoot "data\set-merge-transaction.json"))

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
        Parts = @(
            [ordered]@{
                Source = $ledgerSource
                OriginalDirectory = $ledgerOne
                ChildName = "01 - Ledger Part - 1"
            },
            [ordered]@{
                Source = @($localFiles | Where-Object Title -eq "Ledger Part - 2")[0]
                OriginalDirectory = $ledgerTwo
                ChildName = "02 - Ledger Part - 2"
            }
        )
    }) | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath (Join-Path $dataRoot "data\set-merge-transaction.json") -Encoding UTF8
    $scan = Start-Process -FilePath $Executable -ArgumentList "--scan-local" -Wait -PassThru
    "{invalid" | Set-Content -LiteralPath (Join-Path $dataRoot "data\set-merge-transaction.json") -Encoding ASCII
    $corruptJournalScan = Start-Process -FilePath $Executable -ArgumentList "--scan-local" -Wait -PassThru
    $corruptJournalPreserved = Test-Path (Join-Path $dataRoot "data\set-merge-transaction.json")
    "{}" | Set-Content -LiteralPath (Join-Path $dataRoot "data\set-merge-transaction.json") -Encoding ASCII
    $semanticJournalScan = Start-Process -FilePath $Executable -ArgumentList "--scan-local" -Wait -PassThru
    $semanticJournalPreserved = Test-Path (Join-Path $dataRoot "data\set-merge-transaction.json")
    Remove-Item -LiteralPath (Join-Path $dataRoot "data\set-merge-transaction.json") -Force
}
finally {
    $env:XIUREN_DATA_ROOT = $oldDataRoot
}

$target = Join-Path $modelRoot "Photo Set Complete"
$ledger = Get-Content -LiteralPath (Join-Path $dataRoot "data\library-ledger-v1.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$database = Get-Content -LiteralPath (Join-Path $dataRoot "data\xiuren.db") -Raw -Encoding UTF8 | ConvertFrom-Json
$favoriteRows = @(Get-Content -LiteralPath (Join-Path $dataRoot "data\favorites.json") -Raw -Encoding UTF8 |
    ConvertFrom-Json | ForEach-Object { $_ })
$mergedFavorite = @($favoriteRows | Where-Object Title -eq "Photo Set Complete")[0]
$merged = @($ledger.Items | Where-Object Title -eq "Photo Set Complete")[0]
$history = @($ledger.Items | Where-Object { $_.Title -like "Photo Set - *" })
$manifestPath = Join-Path $dataRoot ("data\manifests\" + $merged.SetId.Substring(0, 2) + "\" + $merged.SetId + ".json")
$manifest = Get-Content -LiteralPath $manifestPath -Raw -Encoding UTF8 | ConvertFrom-Json
$sidecarPath = @(Get-ChildItem -LiteralPath $target -Filter "*.md" -File)[0].FullName
$sidecar = Get-Content -LiteralPath $sidecarPath -Raw -Encoding UTF8
$actualParts = @(Get-ChildItem -LiteralPath $target -Directory | Sort-Object Name | ForEach-Object Name)
$resourcePaths = @($database.Resources | ForEach-Object LocalDir)
$duplicates = @($expectedParts | Where-Object { !(Test-Path -LiteralPath (Join-Path $target "$_\same-name.jpg")) })
$missingResourcePaths = @($expectedParts | Where-Object { $resourcePaths -notcontains (Join-Path $target $_) })

$result = [ordered]@{
    MergeExitCode = $merge.ExitCode
    ScanExitCode = $scan.ExitCode
    ConflictExitCode = $conflictMerge.ExitCode
    LedgerConflictExitCode = $ledgerConflictMerge.ExitCode
    ResourceConflictExitCode = $resourceConflictMerge.ExitCode
    FavoriteConflictExitCode = $favoriteConflictMerge.ExitCode
    CorruptJournalExitCode = $corruptJournalScan.ExitCode
    SemanticJournalExitCode = $semanticJournalScan.ExitCode
    CommittedRecoveryExitCode = $committedRecovery.ExitCode
    OrderedPartsPreserved = (@(Compare-Object $expectedParts $actualParts).Count -eq 0)
    DuplicateNamesPreserved = $duplicates.Count -eq 0
    SourceDirectoriesRemoved = !(Test-Path $upper) -and !(Test-Path $middle) -and !(Test-Path $lower)
    MergedCatalogAvailable = [string]$merged.Availability -eq "Available" -and [int]$merged.ImageCount -eq 3
    MergedPartsPersisted = @($merged.MergedParts).Count -eq 3 -and @($manifest.MergedParts).Count -eq 3
    SourceHistoryArchived = $history.Count -eq 3 -and @($history | Where-Object Availability -ne "Deleted").Count -eq 0
    ResourcePathsRelocated = $missingResourcePaths.Count -eq 0
    FavoriteScoreMerged = [int]$mergedFavorite.Score -eq 6
    FavoriteTagsMerged = @($mergedFavorite.Tags | Sort-Object -Unique).Count -eq 4
    SidecarListsAllParts = $sidecar.Contains("Photo Set - 1") -and $sidecar.Contains("Photo Set - 2") -and $sidecar.Contains("Photo Set - 3")
    ConflictRejectedWithoutMoves = $conflictMerge.ExitCode -eq 2 -and
        (Test-Path $conflictOne) -and (Test-Path $conflictTwo) -and (Test-Path $conflictTarget)
    LedgerConflictRejected = $ledgerConflictMerge.ExitCode -eq 2 -and
        (Test-Path $ledgerOne) -and (Test-Path $ledgerTwo) -and
        !(Test-Path (Join-Path $modelRoot "Ledger Conflict Complete"))
    ResourceConflictRejected = $resourceConflictMerge.ExitCode -eq 2 -and
        !(Test-Path (Join-Path $modelRoot "Resource Conflict Complete"))
    FavoriteConflictRejected = $favoriteConflictMerge.ExitCode -eq 2 -and
        !(Test-Path (Join-Path $modelRoot "Favorite Conflict Complete"))
    InterruptedMergeRecovered = (Test-Path $ledgerOne) -and
        !(Test-Path $recoveryStaging) -and
        !(Test-Path (Join-Path $dataRoot "data\set-merge-transaction.json"))
    CorruptJournalFailsClosed = $corruptJournalScan.ExitCode -eq 4 -and $corruptJournalPreserved
    SemanticJournalFailsClosed = $semanticJournalScan.ExitCode -eq 4 -and $semanticJournalPreserved
    CommittedMetadataRecovered = $committedRecoveryRepaired
    TestRoot = $base
}
$failed = @($result.GetEnumerator() | Where-Object {
    $_.Key -notin @("MergeExitCode", "ScanExitCode", "ConflictExitCode", "LedgerConflictExitCode",
        "ResourceConflictExitCode", "FavoriteConflictExitCode", "CorruptJournalExitCode",
        "SemanticJournalExitCode", "CommittedRecoveryExitCode", "TestRoot") -and !$_.Value
})
if ($merge.ExitCode -ne 0 -or $scan.ExitCode -ne 0 -or
    $conflictMerge.ExitCode -ne 2 -or $ledgerConflictMerge.ExitCode -ne 2 -or
    $resourceConflictMerge.ExitCode -ne 2 -or $favoriteConflictMerge.ExitCode -ne 2 -or
    $corruptJournalScan.ExitCode -ne 4 -or
    $semanticJournalScan.ExitCode -ne 4 -or
    $committedRecovery.ExitCode -ne 0 -or
    $failed.Count -gt 0) {
    $result | ConvertTo-Json
    throw "Set merge integration test failed."
}
$result | ConvertTo-Json

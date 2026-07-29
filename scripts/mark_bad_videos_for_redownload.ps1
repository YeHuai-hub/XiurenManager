param(
    [string]$ModelDir,
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"

$toolRoot = Split-Path -Parent $PSScriptRoot
$downloadRoot = Split-Path -Parent $toolRoot
if ([string]::IsNullOrWhiteSpace($ModelDir)) {
    throw "Pass -ModelDir explicitly, for example: -ModelDir <download-root>\<model-name>"
}

$dbPath = Join-Path $toolRoot "data\xiuren.db"
$videoExts = @(".mp4", ".m4v", ".mov")
$mediaExts = @(".jpg", ".jpeg", ".png", ".webp", ".gif", ".bmp", ".tif", ".tiff", ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".flv", ".ts")

function Test-ValidVideo([string]$Path) {
    $ext = [System.IO.Path]::GetExtension($Path).ToLowerInvariant()
    if ($videoExts -notcontains $ext) { return $true }
    try {
        $stream = [System.IO.File]::OpenRead($Path)
        try {
            if ($stream.Length -lt 16) { return $false }
            $length = [Math]::Min([int64]4096, $stream.Length)
            $buffer = New-Object byte[] $length
            $read = $stream.Read($buffer, 0, $length)
            $header = [System.Text.Encoding]::ASCII.GetString($buffer, 0, $read)
            return $header.Contains("ftyp") -or $header.Contains("moov") -or $header.Contains("mdat")
        }
        finally {
            $stream.Dispose()
        }
    }
    catch {
        return $false
    }
}

function Get-SetDir([string]$FilePath) {
    $root = [System.IO.Path]::GetFullPath($ModelDir).TrimEnd('\') + '\'
    $full = [System.IO.Path]::GetFullPath($FilePath)
    if (-not $full.StartsWith($root, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "File is not inside model directory: $FilePath"
    }
    $relative = $full.Substring($root.Length)
    $first = $relative.Split([System.IO.Path]::DirectorySeparatorChar, [System.StringSplitOptions]::RemoveEmptyEntries)[0]
    return Join-Path $ModelDir $first
}

function Remove-EmptyDirs([string]$Root) {
    Get-ChildItem -LiteralPath $Root -Directory -Recurse -Force |
        Sort-Object FullName -Descending |
        ForEach-Object {
            if (-not (Get-ChildItem -LiteralPath $_.FullName -Force | Select-Object -First 1)) {
                if (-not $DryRun) { Remove-Item -LiteralPath $_.FullName -Force }
            }
        }
}

if (-not (Test-Path -LiteralPath $ModelDir -PathType Container)) {
    throw "Model directory not found: $ModelDir"
}
if (-not (Test-Path -LiteralPath $dbPath -PathType Leaf)) {
    throw "Database not found: $dbPath"
}

$badVideos = Get-ChildItem -LiteralPath $ModelDir -File -Recurse -Force |
    Where-Object { $videoExts -contains $_.Extension.ToLowerInvariant() } |
    Where-Object { -not (Test-ValidVideo $_.FullName) }

$badSetDirs = @($badVideos | ForEach-Object { Get-SetDir $_.FullName } | Sort-Object -Unique)

$deletedFiles = 0
foreach ($setDir in $badSetDirs) {
    $mediaFiles = Get-ChildItem -LiteralPath $setDir -File -Recurse -Force |
        Where-Object { $mediaExts -contains $_.Extension.ToLowerInvariant() }
    foreach ($file in $mediaFiles) {
        if (-not $DryRun) { Remove-Item -LiteralPath $file.FullName -Force }
        $deletedFiles++
    }
    if (-not $DryRun) { Remove-EmptyDirs $setDir }
}

$db = Get-Content -LiteralPath $dbPath -Raw -Encoding UTF8 | ConvertFrom-Json
$changedResources = 0
$changedStats = 0
foreach ($setDir in $badSetDirs) {
    $title = Split-Path -Leaf $setDir
    foreach ($r in @($db.Resources)) {
        $localMatch = -not [string]::IsNullOrWhiteSpace($r.LocalDir) -and ($r.LocalDir -ieq $setDir)
        $titleMatch = ($r.Model -eq (Split-Path -Leaf $ModelDir)) -and ($r.Title -eq $title)
        if ($localMatch -or $titleMatch) {
            $r.Status = "Ready"
            $r.DownloadStatus = ""
            $r.ExtractStatus = ""
            $r.Error = "Local video validation failed; queued for redownload"
            $changedResources++
        }
    }
}

if ($db.LocalFiles) {
    $before = @($db.LocalFiles).Count
    $db.LocalFiles = @($db.LocalFiles | Where-Object { $badSetDirs -notcontains $_.LocalDir })
    $changedStats = $before - @($db.LocalFiles).Count
}

if (-not $DryRun) {
    $backup = "$dbPath.bad-video-backup-$(Get-Date -Format yyyyMMddHHmmss)"
    Copy-Item -LiteralPath $dbPath -Destination $backup -Force
    $db | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath $dbPath -Encoding UTF8
}

[pscustomobject]@{
    ModelDir = $ModelDir
    BadVideoFiles = @($badVideos).Count
    BadSetDirs = @($badSetDirs).Count
    DeletedMediaFiles = $deletedFiles
    ChangedResources = $changedResources
    RemovedStatRows = $changedStats
    DryRun = [bool]$DryRun
} | Format-List

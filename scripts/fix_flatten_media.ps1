$ErrorActionPreference = 'Stop'

$root = 'F:\秀人'
$settingsPath = 'F:\秀人\_Tool\config\settings.json'
$dbPath = 'F:\秀人\_Tool\data\xiuren.db'

$settings = Get-Content -LiteralPath $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
$mediaExts = @($settings.ImageExts + $settings.VideoExts)

$active = @(
    'F:\秀人\杨晨晨\【限时】永久专享人气女神【杨晨晨】最新11V合集 5.2G',
    'F:\秀人\杨晨晨\[XIAOYU语画界]2023.07.21 VOL.1075 杨晨晨Yome[88+1P／771MB]',
    'F:\秀人\杨晨晨\[XIAOYU语画界]2023.07.07 VOL.1065 杨晨晨Yome[85+1P／641MB]',
    'F:\秀人\杨晨晨\[XiuRen秀人网] 2023.06.13 No.6907 杨晨晨Yome [86+1P]'
)

$candidates = @()
Get-ChildItem -LiteralPath $root -Directory |
    Where-Object { $_.Name -ne '_Tool' } |
    ForEach-Object { Get-ChildItem -LiteralPath $_.FullName -Directory -ErrorAction SilentlyContinue } |
    ForEach-Object {
        $top = $_.FullName
        if ($active -contains $top) { return }
        if (Get-ChildItem -LiteralPath $top -Recurse -File -Filter '*.BaiduPCS-Go-downloading' -ErrorAction SilentlyContinue) { return }

        $topMedia = @(Get-ChildItem -LiteralPath $top -File -ErrorAction SilentlyContinue |
            Where-Object { $mediaExts -contains $_.Extension.ToLowerInvariant() }).Count
        $deepMediaFiles = @(Get-ChildItem -LiteralPath $top -Recurse -File -ErrorAction SilentlyContinue |
            Where-Object { $mediaExts -contains $_.Extension.ToLowerInvariant() })

        if ($topMedia -eq 0 -and $deepMediaFiles.Count -gt 0) {
            $candidates += $top
        }
    }

$moved = 0
$deleted = 0
$dirsDeleted = 0

foreach ($dir in $candidates) {
    $resolved = (Resolve-Path -LiteralPath $dir -ErrorAction Stop).Path
    if (-not $resolved.StartsWith($root + '\', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "路径越界: $resolved"
    }
    if ($resolved.StartsWith('F:\秀人\_Tool', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "拒绝处理工具目录: $resolved"
    }

    $media = @(Get-ChildItem -LiteralPath $resolved -Recurse -File -ErrorAction SilentlyContinue |
        Where-Object { $mediaExts -contains $_.Extension.ToLowerInvariant() })
    foreach ($f in $media) {
        if ($f.DirectoryName -eq $resolved) { continue }
        $target = Join-Path $resolved $f.Name
        if (Test-Path -LiteralPath $target) {
            $base = [System.IO.Path]::GetFileNameWithoutExtension($f.Name)
            $target = Join-Path $resolved ($base + '_' + ([Guid]::NewGuid().ToString('N').Substring(0, 6)) + $f.Extension)
        }
        Move-Item -LiteralPath $f.FullName -Destination $target
        $moved++
    }

    foreach ($f in Get-ChildItem -LiteralPath $resolved -Recurse -File -ErrorAction SilentlyContinue) {
        if (-not ($mediaExts -contains $f.Extension.ToLowerInvariant())) {
            Remove-Item -LiteralPath $f.FullName -Force
            $deleted++
        }
    }

    Get-ChildItem -LiteralPath $resolved -Recurse -Directory -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending |
        ForEach-Object {
            if (-not (Get-ChildItem -LiteralPath $_.FullName -Force -ErrorAction SilentlyContinue)) {
                Remove-Item -LiteralPath $_.FullName -Force
                $dirsDeleted++
            }
        }
}

$updated = 0
if (Test-Path -LiteralPath $dbPath) {
    $bak = 'F:\秀人\_Tool\data\xiuren.db.bak-20260719-flatten-media'
    Copy-Item -LiteralPath $dbPath -Destination $bak -Force
    $db = Get-Content -LiteralPath $dbPath -Raw -Encoding UTF8 | ConvertFrom-Json
    foreach ($r in $db.Resources) {
        if ($candidates -contains $r.LocalDir) {
            $r.DownloadStatus = 'Downloaded'
            $r.ExtractStatus = 'Extracted'
            $r.Error = ''
            $updated++
        }
    }
    $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
    [System.IO.File]::WriteAllText($dbPath, ($db | ConvertTo-Json -Depth 20), $utf8NoBom)
}

[PSCustomObject]@{
    Directories = $candidates.Count
    MovedMedia = $moved
    DeletedFiles = $deleted
    DeletedDirs = $dirsDeleted
    DbUpdated = $updated
}

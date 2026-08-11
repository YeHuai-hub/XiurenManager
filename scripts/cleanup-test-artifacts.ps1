param(
    [string]$WorkspaceRoot = "E:\WorkSpace",
    [switch]$WhatIf
)

$ErrorActionPreference = "Stop"
$root = [IO.Path]::GetFullPath($WorkspaceRoot).TrimEnd('\')
if (![IO.Directory]::Exists($root)) {
    throw "工作目录不存在: $root"
}

$namePattern = '^Xiuren(CatalogTest|MergeTest|StorageTests$|Manager-(build|empty-test|final-safety|render-test|video-test))'
$targets = @(Get-ChildItem -LiteralPath $root -Directory | Where-Object {
    $_.Name -match $namePattern
})
$projectRoot = Split-Path -Parent $PSScriptRoot
$localTempRoot = Join-Path $projectRoot "tmp"
$localNamePattern = '^(storage-test|download-root-test|incremental-scan|job-audit|offline-root-test|root-probe)-'
$localTargets = if ([IO.Directory]::Exists($localTempRoot)) {
    @(Get-ChildItem -LiteralPath $localTempRoot -Directory | Where-Object {
        $_.Name -match $localNamePattern
    })
} else {
    @()
}
$deleted = 0
$bytes = 0L

foreach ($target in $targets) {
    $path = [IO.Path]::GetFullPath($target.FullName).TrimEnd('\')
    $parent = [IO.Directory]::GetParent($path)
    if ($null -eq $parent -or $parent.FullName.TrimEnd('\') -ne $root) {
        throw "拒绝清理越界路径: $path"
    }

    $size = (Get-ChildItem -LiteralPath $path -File -Recurse -Force -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum
    if ($null -ne $size) {
        $bytes += [long]$size
    }
    if (!$WhatIf) {
        [IO.Directory]::Delete($path, $true)
        $deleted++
    }
}

foreach ($target in $localTargets) {
    $path = [IO.Path]::GetFullPath($target.FullName).TrimEnd('\')
    $parent = [IO.Directory]::GetParent($path)
    if ($null -eq $parent -or
        $parent.FullName.TrimEnd('\') -ne [IO.Path]::GetFullPath($localTempRoot).TrimEnd('\')) {
        throw "拒绝清理越界路径: $path"
    }

    $size = (Get-ChildItem -LiteralPath $path -File -Recurse -Force -ErrorAction SilentlyContinue |
        Measure-Object Length -Sum).Sum
    if ($null -ne $size) {
        $bytes += [long]$size
    }
    if (!$WhatIf) {
        [IO.Directory]::Delete($path, $true)
        $deleted++
    }
}

[pscustomobject]@{
    Matched = $targets.Count + $localTargets.Count
    Deleted = $deleted
    ReclaimedGB = [math]::Round($bytes / 1GB, 2)
    BackupRetained = [IO.Directory]::Exists((Join-Path $root "XiurenManager-backups"))
    Mode = if ($WhatIf) { "Preview" } else { "Deleted" }
} | ConvertTo-Json

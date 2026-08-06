param(
    [string]$ExpectedSource = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$stateFile = Join-Path $root "data\storage-migration-state.json"
$state = Get-Content -LiteralPath $stateFile -Raw -Encoding UTF8 |
    ConvertFrom-Json
$source = [IO.Path]::GetFullPath([string]$state.SourcePath).TrimEnd('\')
$destination = [string]$state.DestinationPath

if (!$ExpectedSource) {
    throw "ExpectedSource is required."
}
$expected = [IO.Path]::GetFullPath($ExpectedSource).TrimEnd('\')
if ($source -ne $expected) {
    throw "Migration source does not match the explicitly approved path: $source"
}
if ($state.Phase -ne "DatabaseUpdated") {
    throw "Migration is not in the database-updated cleanup phase: $($state.Phase)"
}
if (!(Test-Path -LiteralPath $source) -or
    !(Test-Path -LiteralPath $destination)) {
    throw "The migration source or NAS destination is missing."
}

$mismatches = 0
Get-ChildItem -LiteralPath $source -Recurse -File -Force |
    ForEach-Object {
        $relative = $_.FullName.Substring($source.Length).TrimStart('\')
        $target = Join-Path $destination $relative
        if (!(Test-Path -LiteralPath $target) -or
            (Get-Item -LiteralPath $target).Length -ne $_.Length -or
            (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash -ne
            (Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash) {
            $mismatches++
        }
    }
if ($mismatches -ne 0) {
    throw "Remaining source files do not match the NAS destination: $mismatches"
}

Get-ChildItem -LiteralPath $source -Recurse -Force |
    Sort-Object { $_.FullName.Length } -Descending |
    ForEach-Object {
        [IO.File]::SetAttributes($_.FullName, [IO.FileAttributes]::Normal)
    }
[IO.File]::SetAttributes($source, [IO.FileAttributes]::Normal)
Remove-Item -LiteralPath $source -Recurse -Force

if (Test-Path -LiteralPath $source) {
    throw "The verified source directory could not be removed: $source"
}
Write-Output "Recovered migration cleanup: $source"

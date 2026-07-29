param(
    [switch]$SkipPublish
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\XiurenManager\XiurenManager.csproj"
$publish = Join-Path $root "publish-v3"
$script = Join-Path $root "installer\XiurenManager.Private.iss"
$outputDir = Join-Path $root "artifacts\installer"

if (!$SkipPublish) {
    Get-Process XiurenManager -ErrorAction SilentlyContinue | Stop-Process -Force
    & dotnet publish $project -c Release -r win-x64 --self-contained true --no-restore -o $publish -v:q
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed with exit code $LASTEXITCODE"
    }
}

$compiler = @(
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 7\ISCC.exe"),
    "$env:ProgramFiles\Inno Setup 7\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 7\ISCC.exe",
    (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { $_ -and (Test-Path -LiteralPath $_) } | Select-Object -First 1

if (!$compiler) {
    throw "Inno Setup ISCC.exe was not found."
}

& $compiler $script
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE"
}

$output = Get-ChildItem -LiteralPath $outputDir -Filter "*Setup-3.1.1.exe" |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (!$output) {
    throw "The compiled installer was not found in $outputDir"
}

$hash = (Get-FileHash -LiteralPath $output.FullName -Algorithm SHA256).Hash
$manifest = "$hash *$($output.Name)"
Set-Content -LiteralPath ($output.FullName + ".sha256") -Value $manifest -Encoding ASCII

Get-Item -LiteralPath $output.FullName | Select-Object FullName, Length, LastWriteTime
Write-Host "SHA256: $hash"

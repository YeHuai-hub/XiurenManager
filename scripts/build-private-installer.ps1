param(
    [switch]$SkipPublish,
    [string]$PublishDir = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\XiurenManager\XiurenManager.csproj"
$buildRoot = if ($env:XIUREN_BUILD_ROOT) {
    [Environment]::ExpandEnvironmentVariables($env:XIUREN_BUILD_ROOT)
} elseif (Test-Path -LiteralPath "E:\WorkSpace") {
    "E:\WorkSpace\XiurenManager-build"
} else {
    $root
}
$publish = if ($PublishDir) { $PublishDir } else { Join-Path $buildRoot "publish-v3" }
$script = Join-Path $root "installer\XiurenManager.Private.iss"
$outputDir = Join-Path $root "artifacts\installer"

if (!$SkipPublish) {
    Get-Process XiurenManager -ErrorAction SilentlyContinue | Stop-Process -Force
    $fullRoot = [IO.Path]::GetFullPath($root).TrimEnd('\')
    $fullPublish = [IO.Path]::GetFullPath($publish).TrimEnd('\')
    $fullBuildRoot = [IO.Path]::GetFullPath($buildRoot).TrimEnd('\')
    $insideTool = $fullPublish.StartsWith($fullRoot + "\", [StringComparison]::OrdinalIgnoreCase)
    $insideBuild = $fullPublish.StartsWith($fullBuildRoot + "\", [StringComparison]::OrdinalIgnoreCase)
    if (!$insideTool -and !$insideBuild) {
        throw "Refusing to clean an unexpected publish directory: $fullPublish"
    }
    if (Test-Path -LiteralPath $fullPublish) {
        Remove-Item -LiteralPath $fullPublish -Recurse -Force
    }
    & dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish -v:q
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

& $compiler "/DPublishDir=$publish" $script
if ($LASTEXITCODE -ne 0) {
    throw "Inno Setup compilation failed with exit code $LASTEXITCODE"
}

$output = Get-ChildItem -LiteralPath $outputDir -Filter "*Setup-3.5.4.exe" |
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

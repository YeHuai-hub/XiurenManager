$ErrorActionPreference = 'Stop'

$root = 'F:\秀人\杨晨晨'
$settings = Get-Content -LiteralPath 'F:\秀人\_Tool\config\settings.json' -Raw -Encoding UTF8 | ConvertFrom-Json
$videoExts = @($settings.VideoExts)

$rows = @()
foreach ($f in Get-ChildItem -LiteralPath $root -Recurse -File -ErrorAction SilentlyContinue) {
    if (-not ($videoExts -contains $f.Extension.ToLowerInvariant())) { continue }
    $headBytes = [byte[]](Get-Content -LiteralPath $f.FullName -Encoding Byte -TotalCount 64)
    $head = -join ($headBytes | ForEach-Object { if ($_ -ge 32 -and $_ -le 126) { [char]$_ } else { '.' } })
    $looksMp4 = $head -match 'ftyp|moov|mdat'
    $verySmall = $f.Length -lt 10MB
    $rows += [PSCustomObject]@{
        Name = $f.Name
        SizeMB = [Math]::Round($f.Length / 1MB, 2)
        LooksMp4 = $looksMp4
        VerySmall = $verySmall
        Header = $head
        Path = $f.FullName
    }
}

$rows | Sort-Object LooksMp4, SizeMB | Select-Object -First 80

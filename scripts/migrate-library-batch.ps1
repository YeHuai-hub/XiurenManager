param(
    [string]$SourceRoot = "",
    [string]$LibraryRoot = "",
    [string]$Category = "",
    [int]$BatchSize = 20,
    [int]$MinimumAgeMinutes = 30,
    [string]$StateFile = "",
    [string]$LogFile = "",
    [switch]$DryRun
)

$ErrorActionPreference = "Stop"
$xiurenName = [string]::Concat([char]0x79C0, [char]0x4EBA)
$resourceName = [string]::Concat([char]0x8D44, [char]0x6E90)
$toolRoot = Split-Path -Parent $PSScriptRoot
if (!$SourceRoot) { $SourceRoot = "F:\$xiurenName" }
if (!$LibraryRoot) { $LibraryRoot = "F:\$resourceName" }
if (!$Category) { $Category = $xiurenName }
if (!$StateFile) { $StateFile = Join-Path $toolRoot "data\library-migration-state.json" }
if (!$LogFile) { $LogFile = Join-Path $toolRoot "logs\library-migration.log" }

$excludedDirectories = @(
    "_Tool",
    ".git",
    ".agents",
    ".codex",
    "System Volume Information",
    "`$RECYCLE.BIN"
)
$temporaryPatterns = @(
    "*.BaiduPCS-Go-downloading",
    "*.aria2",
    "*.part",
    "*.download",
    "*.tmp"
)
$mutex = [Threading.Mutex]::new($false, "Local\XiurenManagerLibraryMigration")
$hasMutex = $false

function Write-MigrationLog([string]$Message) {
    $line = "[{0:yyyy-MM-dd HH:mm:ss}] {1}" -f (Get-Date), $Message
    $directory = Split-Path -Parent $LogFile
    if ($directory) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    [IO.File]::AppendAllText(
        $LogFile,
        $line + [Environment]::NewLine,
        [Text.Encoding]::UTF8)
}

function New-MigrationState {
    return [ordered]@{
        Status = "Pending"
        TotalMoved = 0
        TotalConflicts = 0
        LastRunAt = ""
        LastMoved = 0
        Remaining = 0
        LastError = ""
    }
}

function Load-State {
    if (!(Test-Path -LiteralPath $StateFile)) {
        return New-MigrationState
    }

    try {
        $value = Get-Content -LiteralPath $StateFile -Raw -Encoding UTF8 |
            ConvertFrom-Json
        return [ordered]@{
            Status = [string]$value.Status
            TotalMoved = [int]$value.TotalMoved
            TotalConflicts = [int]$value.TotalConflicts
            LastRunAt = [string]$value.LastRunAt
            LastMoved = [int]$value.LastMoved
            Remaining = [int]$value.Remaining
            LastError = [string]$value.LastError
        }
    }
    catch {
        Write-MigrationLog "State read failed; continuing from directory state: $($_.Exception.Message)"
        return New-MigrationState
    }
}

function Save-State($State) {
    $directory = Split-Path -Parent $StateFile
    if ($directory) {
        [IO.Directory]::CreateDirectory($directory) | Out-Null
    }
    $temp = $StateFile + ".tmp"
    $State | ConvertTo-Json | Set-Content -LiteralPath $temp -Encoding UTF8
    if (Test-Path -LiteralPath $StateFile) {
        $backup = $StateFile + ".bak"
        [IO.File]::Replace($temp, $StateFile, $backup)
        if (Test-Path -LiteralPath $backup) {
            [IO.File]::Delete($backup)
        }
    }
    else {
        [IO.File]::Move($temp, $StateFile)
    }
}

function Has-TemporaryFile([string]$Directory) {
    foreach ($pattern in $temporaryPatterns) {
        $match = Get-ChildItem `
            -LiteralPath $Directory `
            -File `
            -Recurse `
            -Filter $pattern `
            -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($match) {
            return $true
        }
    }
    return $false
}

try {
    $hasMutex = $mutex.WaitOne(0)
    if (!$hasMutex) {
        exit 0
    }

    $source = [IO.Path]::GetFullPath($SourceRoot).TrimEnd("\")
    $library = [IO.Path]::GetFullPath($LibraryRoot).TrimEnd("\")
    if (!$source -or !$library -or $source -eq $library) {
        throw "SourceRoot and LibraryRoot must be different valid paths."
    }
    if (!(Test-Path -LiteralPath $source)) {
        throw "Source root does not exist: $source"
    }

    $categoryRoot = Join-Path $library $Category
    [IO.Directory]::CreateDirectory($categoryRoot) | Out-Null
    $state = Load-State
    $cutoff = (Get-Date).AddMinutes(-[Math]::Max(1, $MinimumAgeMinutes))
    $allSets = @(
        Get-ChildItem -LiteralPath $source -Directory -Force |
            Where-Object {
                $excludedDirectories -notcontains $_.Name -and
                !$_.Name.StartsWith(".")
            } |
            ForEach-Object {
                $model = $_
                Get-ChildItem `
                    -LiteralPath $model.FullName `
                    -Directory `
                    -Force `
                    -ErrorAction SilentlyContinue |
                    ForEach-Object {
                        [pscustomobject]@{
                            Model = $model.Name
                            ModelDirectory = $model.FullName
                            Set = $_
                        }
                    }
            } |
            Sort-Object { $_.Set.LastWriteTimeUtc }
    )

    $moved = 0
    $conflicts = 0
    foreach ($entry in $allSets) {
        if ($moved -ge [Math]::Max(1, $BatchSize)) {
            break
        }
        if ($entry.Set.LastWriteTime -gt $cutoff) {
            continue
        }
        if (Has-TemporaryFile $entry.Set.FullName) {
            continue
        }

        $targetModel = Join-Path $categoryRoot $entry.Model
        $targetSet = Join-Path $targetModel $entry.Set.Name
        if (Test-Path -LiteralPath $targetSet) {
            $conflicts++
            Write-MigrationLog "Skipped target conflict: $($entry.Set.FullName) -> $targetSet"
            continue
        }

        if ($DryRun) {
            Write-MigrationLog "[DryRun] $($entry.Set.FullName) -> $targetSet"
        }
        else {
            [IO.Directory]::CreateDirectory($targetModel) | Out-Null
            [IO.Directory]::Move($entry.Set.FullName, $targetSet)
            $modelEntries = Get-ChildItem `
                -LiteralPath $entry.ModelDirectory `
                -Force `
                -ErrorAction SilentlyContinue |
                Select-Object -First 1
            if (!$modelEntries) {
                [IO.Directory]::Delete($entry.ModelDirectory)
            }
            Write-MigrationLog "Moved: $($entry.Set.FullName) -> $targetSet"
        }
        $moved++
    }

    $actualMoved = if ($DryRun) { 0 } else { $moved }
    $remaining = [Math]::Max(0, $allSets.Count - $actualMoved)
    if ($DryRun) {
        $state.Status = "DryRun"
    }
    elseif ($remaining -eq 0) {
        $state.Status = "Completed"
    }
    else {
        $state.Status = "Running"
    }
    if (!$DryRun) {
        $state.TotalMoved = [int]$state.TotalMoved + $moved
    }
    $state.TotalConflicts = [int]$state.TotalConflicts + $conflicts
    $state.LastRunAt = (Get-Date).ToString("s")
    $state.LastMoved = $actualMoved
    $state.Remaining = $remaining
    $state.LastError = ""
    Save-State $state
    if ($DryRun) {
        Write-MigrationLog "Dry run complete: candidates=$moved conflicts=$conflicts remaining=$remaining"
    }
    else {
        Write-MigrationLog "Batch complete: moved=$moved conflicts=$conflicts remaining=$remaining"
    }
}
catch {
    $message = $_.Exception.Message
    Write-MigrationLog "Migration failed: $message"
    try {
        $state = Load-State
        $state.Status = "Failed"
        $state.LastRunAt = (Get-Date).ToString("s")
        $state.LastError = $message
        Save-State $state
    }
    catch { }
    throw
}
finally {
    if ($hasMutex) {
        $mutex.ReleaseMutex()
    }
    $mutex.Dispose()
}

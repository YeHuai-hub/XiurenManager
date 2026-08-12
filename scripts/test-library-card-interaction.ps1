param(
    [string]$Executable = "",
    [switch]$KeepTestData
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
if (!$Executable) {
    $Executable = Join-Path $projectRoot "src\XiurenManager\bin\Release\net8.0-windows\XiurenManager.exe"
}

$base = Join-Path "E:\WorkSpace" ("XiurenLibraryInteractionTest-" + [Guid]::NewGuid().ToString("N"))
$dataRoot = Join-Path $base "_Tool"
$libraryRoot = Join-Path $base "library"
$modelRoot = Join-Path $libraryRoot "TestCategory\ModelA"
$oldDataRoot = $env:XIUREN_DATA_ROOT
$process = $null

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class XiurenTestMouse {
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint flags, uint dx, uint dy, uint data, UIntPtr extraInfo);
    public static void Click() {
        mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
        mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
    }
}
"@

function Get-ProcessWindows([int]$ProcessId) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::ProcessIdProperty,
        $ProcessId)
    return @([System.Windows.Automation.AutomationElement]::RootElement.FindAll(
        [System.Windows.Automation.TreeScope]::Children,
        $condition))
}

function Wait-Element($Root, [string]$Name, [int]$TimeoutSeconds = 20) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    do {
        $element = $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($null -ne $element) { return $element }
        Start-Sleep -Milliseconds 200
    } while ((Get-Date) -lt $deadline)
    throw "Timed out waiting for UI element: $Name"
}

function Find-Element($Root, [string]$Name) {
    $condition = New-Object System.Windows.Automation.PropertyCondition(
        [System.Windows.Automation.AutomationElement]::NameProperty,
        $Name)
    return $Root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $condition)
}

function Click-Element($Element, [switch]$Double) {
    $rect = $Element.Current.BoundingRectangle
    if ($rect.Width -le 0 -or $rect.Height -le 0) {
        throw "UI element has no clickable bounds: $($Element.Current.Name)"
    }
    [XiurenTestMouse]::SetCursorPos(
        [int]($rect.Left + $rect.Width / 2),
        [int]($rect.Top + $rect.Height / 2)) | Out-Null
    [XiurenTestMouse]::Click()
    if ($Double) {
        Start-Sleep -Milliseconds 80
        [XiurenTestMouse]::Click()
    }
}

function Activate-Element($Element) {
    $pattern = $null
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.SelectionItemPattern]::Pattern,
            [ref]$pattern)) {
        ([System.Windows.Automation.SelectionItemPattern]$pattern).Select()
        return
    }
    if ($Element.TryGetCurrentPattern(
            [System.Windows.Automation.InvokePattern]::Pattern,
            [ref]$pattern)) {
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        return
    }
    Click-Element $Element
}

try {
    [IO.Directory]::CreateDirectory((Join-Path $dataRoot "config")) | Out-Null
    [IO.Directory]::CreateDirectory((Join-Path $dataRoot "data")) | Out-Null
    [IO.Directory]::CreateDirectory($modelRoot) | Out-Null

    $jpeg = [Convert]::FromBase64String(
        "/9j/4AAQSkZJRgABAQEASABIAAD/2wBDAP//////////////////////////////////////////////////////////////////////////////////////2wBDAf//////////////////////////////////////////////////////////////////////////////////////wAARCAABAAEDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAX/xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oADAMBAAIQAxAAAAF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABBQJ//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAwEBPwF//8QAFBEBAAAAAAAAAAAAAAAAAAAAAP/aAAgBAgEBPwF//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQAGPwJ//8QAFBABAAAAAAAAAAAAAAAAAAAAAP/aAAgBAQABPyF//9oADAMBAAIAAwAAABD/xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAEDAQE/EB//xAAUEQEAAAAAAAAAAAAAAAAAAAAA/9oACAECAQE/EB//xAAUEAEAAAAAAAAAAAAAAAAAAAAA/9oACAEBAAE/EB//2Q==")
    $localFiles = @()
    foreach ($index in 1..3) {
        $title = "Test Set $index"
        $setPath = Join-Path $modelRoot $title
        [IO.Directory]::CreateDirectory($setPath) | Out-Null
        [IO.File]::WriteAllBytes((Join-Path $setPath "cover.jpg"), $jpeg)
        $localFiles += [ordered]@{
            SetId = ("{0:d32}" -f $index)
            Category = "TestCategory"
            Model = "ModelA"
            Title = $title
            LocalDir = $setPath
            ImageCount = 1
            VideoCount = 0
            TotalBytes = $jpeg.Length
            LastScanned = (Get-Date).ToString("s")
            Availability = "Available"
        }
    }

    [ordered]@{
        DownloadRoot = $libraryRoot
        ArchiveRoot = (Join-Path $base "offline-archive")
        StorageManagementEnabled = $false
        LibraryCategories = @("TestCategory")
        LegacyDownloadRoots = @()
    } | ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath (Join-Path $dataRoot "config\settings.json") -Encoding UTF8
    [ordered]@{ Resources = @(); Jobs = @(); LocalFiles = $localFiles } |
        ConvertTo-Json -Depth 20 |
        Set-Content -LiteralPath (Join-Path $dataRoot "data\xiuren.db") -Encoding UTF8

    $env:XIUREN_DATA_ROOT = $dataRoot
    $process = Start-Process -FilePath $Executable -PassThru
    $deadline = (Get-Date).AddSeconds(30)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
    } while (!$process.HasExited -and $process.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline)
    if ($process.HasExited -or $process.MainWindowHandle -eq 0) {
        throw "The test application did not show its main window."
    }

    $libraryName = -join @(0x5A92, 0x4F53, 0x5E93 | ForEach-Object { [char]$_ })
    $mergeName = -join @(0x5408, 0x5E76 | ForEach-Object { [char]$_ })
    $mainWindow = [System.Windows.Automation.AutomationElement]::FromHandle(
        [IntPtr]$process.MainWindowHandle)
    if ($null -eq $mainWindow) { throw "The test application main window was not found." }
    $libraryItem = Wait-Element $mainWindow $libraryName
    $deadline = (Get-Date).AddSeconds(30)
    $first = $null
    do {
        Activate-Element $libraryItem
        Start-Sleep -Milliseconds 500
        $first = Find-Element $mainWindow "Test Set 1"
    } while ($null -eq $first -and (Get-Date) -lt $deadline)
    if ($null -eq $first) { throw "The media library did not finish loading test cards." }

    Click-Element $first
    Start-Sleep -Milliseconds 500
    $singleClickStayedInLibrary = @(Get-ProcessWindows $process.Id).Count -eq 1

    Click-Element (Wait-Element $mainWindow "Test Set 2")
    Start-Sleep -Milliseconds 300
    $mergeButton = Wait-Element $mainWindow ($mergeName + " (2)")
    $twoCardsSelected = $mergeButton.Current.IsEnabled

    Click-Element (Wait-Element $mainWindow "Test Set 3") -Double
    $deadline = (Get-Date).AddSeconds(10)
    do {
        Start-Sleep -Milliseconds 200
        $windows = @(Get-ProcessWindows $process.Id)
        $viewerLogged = @(Get-ChildItem (Join-Path $dataRoot "logs") -File -ErrorAction SilentlyContinue |
            Select-String -SimpleMatch "ModelA / Test Set 3" -Quiet).Count -gt 0
    } while ($windows.Count -lt 2 -and !$viewerLogged -and (Get-Date) -lt $deadline)
    $doubleClickOpenedViewer = $windows.Count -ge 2 -or $viewerLogged

    [ordered]@{
        SingleClickStayedInLibrary = $singleClickStayedInLibrary
        TwoCardsSelected = $twoCardsSelected
        DoubleClickOpenedViewer = $doubleClickOpenedViewer
        TestRoot = $base
    } | ConvertTo-Json

    if (!$singleClickStayedInLibrary -or !$twoCardsSelected -or !$doubleClickOpenedViewer) {
        throw "Library card interaction test failed."
    }
}
finally {
    if ($null -ne $process -and !$process.HasExited) {
        Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        $process.WaitForExit(5000) | Out-Null
    }
    $env:XIUREN_DATA_ROOT = $oldDataRoot
    if (!$KeepTestData -and [IO.Directory]::Exists($base)) {
        [IO.Directory]::Delete($base, $true)
    }
}

param(
    [int]$FirstWaitSeconds = 12,
    [int]$SecondWaitSeconds = 100,
    [string]$SelectTab = '',
    [string]$Name = 'desktop',
    [switch]$Maximize,
    [switch]$NoLaunch
)
# FirstWaitSeconds / SecondWaitSeconds: wait before each screenshot.
# SelectTab: name of the TabItem to select; empty = leave the current page as-is.
# Name: output file name prefix so repeated captures do not overwrite each other.
# Maximize: maximise the window first, so long pages fit without scrolling.
# NoLaunch: capture an ALREADY RUNNING window instead of starting the dev build.

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes

Add-Type @'
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
    public const int SW_RESTORE = 9;
    public const int SW_MAXIMIZE = 3;

    // Returned as an int[] so PowerShell never has to marshal an out struct by ref.
    public static int[] GetRect(IntPtr h) {
        RECT r;
        if (!GetWindowRect(h, out r)) { return null; }
        return new int[] { r.L, r.T, r.R - r.L, r.B - r.T };
    }
}
'@

$env:DOTNET_ROLL_FORWARD = 'LatestMajor'
$dll = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\src\ClapBack.Desktop\bin\Release\net8.0\ClapBack.Desktop.dll'))
if (-not (Test-Path $dll)) { throw "build output not found: $dll" }

$outDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\docs\gui'))
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$proc = $null
if (-not $NoLaunch) {
    $proc = Start-Process -FilePath 'dotnet' -ArgumentList @($dll) -PassThru -WindowStyle Hidden
}

function Find-ClapBackWindow {
    return Get-Process | Where-Object { $_.MainWindowTitle -match 'ClapBack' } | Select-Object -First 1
}

# Select a TabItem through UI Automation. The dictionary page is not the default
# page, so without switching to it the screenshot would never show it.
function Select-TabByName([string]$name) {
    if ([string]::IsNullOrWhiteSpace($name)) { return }
    $win = Find-ClapBackWindow
    if ($null -eq $win) { Write-Output 'TAB=SKIP'; return }

    $root = [System.Windows.Automation.AutomationElement]::FromHandle($win.MainWindowHandle)
    $controlType = [System.Windows.Automation.ControlType]::TabItem
    $property = [System.Windows.Automation.AutomationElement]::ControlTypeProperty
    $cond = New-Object System.Windows.Automation.PropertyCondition($property, $controlType)
    $items = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond)
    foreach ($item in $items) {
        if ($item.Current.Name -ne $name) { continue }
        $pattern = $item.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern)
        $pattern.Select()
        Write-Output ('TAB=' + $name)
        return
    }
    Write-Output ('TAB=NOTFOUND:' + $name)
}

function Capture([string]$name) {
    $win = Find-ClapBackWindow
    if ($null -eq $win) { Write-Output ('SKIP=' + $name); return }
    $hwnd = $win.MainWindowHandle

    [Win32]::ShowWindow($hwnd, [Win32]::SW_RESTORE) | Out-Null
    if ($Maximize) { [Win32]::ShowWindow($hwnd, [Win32]::SW_MAXIMIZE) | Out-Null }
    [Win32]::SetForegroundWindow($hwnd) | Out-Null
    Start-Sleep -Seconds 2

    $rect = [Win32]::GetRect($hwnd)
    if ($null -eq $rect) { Write-Output ('SKIP=' + $name + ' (no rect)'); return }
    Write-Output ('RECT=' + ($rect -join ','))

    # Grab the whole primary screen, then crop to the window rectangle.
    # Two reasons for this shape:
    #   * PrintWindow hands back a solid black frame for this GPU-composited window.
    #   * CopyFromScreen can only read screen coordinates, and the window sits
    #     wherever it happens to sit -- so capture wide, crop after.
    # The (Point, Point, Size) overload is the one that actually binds in this
    # PowerShell; every hand-built (int,int,int,int,Size) attempt failed to resolve.
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $full = New-Object System.Drawing.Bitmap($screen.Width, $screen.Height)
    $g = [System.Drawing.Graphics]::FromImage($full)
    $g.CopyFromScreen($screen.Location, [System.Drawing.Point]::Empty, $screen.Size)
    $g.Dispose()

    $x = [Math]::Max([int]$rect[0], 0)
    $y = [Math]::Max([int]$rect[1], 0)
    $w = [Math]::Min([Math]::Max([int]$rect[2], 1), $full.Width - $x)
    $h = [Math]::Min([Math]::Max([int]$rect[3], 1), $full.Height - $y)
    if ($w -le 0 -or $h -le 0) {
        $full.Dispose()
        Write-Output ('SKIP=' + $name + ' (window off-screen)')
        return
    }

    $crop = New-Object System.Drawing.Rectangle($x, $y, $w, $h)
    $bmp = $full.Clone($crop, $full.PixelFormat)
    $full.Dispose()

    $out = Join-Path $outDir $name
    $bmp.Save($out, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Output ('SCREENSHOT=' + $out)
}

try {
    Start-Sleep -Seconds $FirstWaitSeconds
    Select-TabByName $SelectTab
    Capture ($Name + '-screenshot.png')

    # Second capture observes the final state of first-run provisioning.
    Start-Sleep -Seconds $SecondWaitSeconds
    Select-TabByName $SelectTab
    Capture ($Name + '-provisioning.png')
}
finally {
    # With -NoLaunch the window we photographed is somebody else's process (possibly
    # the packaged exe under test) — never kill it, or we lose the very thing being debugged.
    if (-not $NoLaunch) {
        Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
        Get-Process -Name 'ClapBack.Desktop' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
    }
}

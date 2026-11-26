param([string]$Exe, [string]$Folder, [string]$Data, [string]$Shots)

# Real-window check of the window frame: Coder keeps the native frame (caption and min/max boxes, which
# Windows needs to animate minimize and maximize); its own title bar must still be what shows (no native
# caption, no Avalonia-drawn one), and maximize, minimize and restore must keep working. Needs no focus.
. "$PSScriptRoot\SmokeCommon.ps1"
$ErrorActionPreference = 'Stop'
if (-not ('WinStyle' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class WinStyle {
  [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsZoomed(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr h, out RECT r);
  public struct RECT { public int L, T, R, B; }
}
"@
}

# Average brightness of the window's top 6 pixel rows in the middle: Coder's dark title bar, never a light native caption.
function Top-Brightness([string]$shot) {
  Shot $shot
  $bmp = [System.Drawing.Bitmap]::FromFile((Join-Path $Shots "$shot.png"))
  try {
    $sum = 0; $n = 0
    foreach ($x in 300..700) { foreach ($y in 8..14) { $c = $bmp.GetPixel($x, $y); $sum += ($c.R + $c.G + $c.B) / 3; $n++ } }
    return $sum / $n
  } finally { $bmp.Dispose() }
}

Start-Coder $Exe $Data $Folder
try {
  Start-Sleep -Seconds 2
  $style = [WinStyle]::GetWindowLongPtr($script:hwnd, -16).ToInt64()
  Check 'window has caption and min/max boxes (animations)' ((($style -band 0x00C00000) -eq 0x00C00000) -and (($style -band 0x00030000) -eq 0x00030000)) ('style=0x{0:X}' -f $style)

  $w = New-Object Win+RECT; [void][Win]::GetWindowRect($script:hwnd, [ref]$w)
  $c = New-Object WinStyle+RECT; [void][WinStyle]::GetClientRect($script:hwnd, [ref]$c)
  $captionGap = ($w.B - $w.T) - ($c.B - $c.T)
  Check 'no native caption above the client area' ($captionGap -le 16) "window-client height difference=$captionGap"
  $top = Top-Brightness 'chrome-normal'
  Check 'title bar is the app''s own (dark)' ($top -lt 60) "brightness=$([int]$top)"

  [void][WinStyle]::ShowWindow($script:hwnd, 3); Start-Sleep -Milliseconds 900 # maximize
  Check 'maximizes' ([WinStyle]::IsZoomed($script:hwnd))
  $top = Top-Brightness 'chrome-maximized'
  Check 'maximized title bar is the app''s own' ($top -lt 60) "brightness=$([int]$top)"

  [void][WinStyle]::ShowWindow($script:hwnd, 6); Start-Sleep -Milliseconds 900 # minimize
  Check 'minimizes' ([WinStyle]::IsIconic($script:hwnd))
  [void][WinStyle]::ShowWindow($script:hwnd, 9); Start-Sleep -Milliseconds 900 # restore
  Check 'restores' (-not [WinStyle]::IsIconic($script:hwnd))
  Check 'still running' (Alive)
}
finally {
  Stop-Coder
}

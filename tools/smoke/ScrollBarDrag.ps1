param([string]$Exe, [string]$Folder, [string]$Data, [string]$Shots)

# Real-mouse check of dragging the editor's scroll bar: the thumb must stay under the pointer while it
# moves, as with any scroll bar. This one moves the real mouse, so the window comes to the front and the
# pointer is borrowed for a few seconds; don't touch the mouse while it runs.
. "$PSScriptRoot\SmokeCommon.ps1"
$ErrorActionPreference = 'Stop'

if (-not ('Mouse' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Mouse {
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint flags, int dx, int dy, uint data, UIntPtr extra);
  public const uint LeftDown = 0x0002, LeftUp = 0x0004;
}
"@
}
[void][Mouse]::SetProcessDPIAware() # screen pixels, the same as UI Automation's

function Editor-Thumb() {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::Thumb)
  $thumbs = @($script:root.FindAll([System.Windows.Automation.TreeScope]::Descendants, $cond) | ForEach-Object { $_.Current.BoundingRectangle } | Where-Object { $_.Height -gt $_.Width -and $_.Height -gt 0 })
  # The editor's vertical thumb is the rightmost tall one.
  return $thumbs | Sort-Object -Property X -Descending | Select-Object -First 1
}

Start-Coder $Exe $Data $Folder
try {
  Start-Sleep -Seconds 2
  Invoke 'Search files and commands'
  Set-Text 'Command palette input' 'PieceTable.cs'
  Post-Enter
  Start-Sleep -Seconds 1

  $thumb = Editor-Thumb
  Check 'editor scroll bar thumb found' ($null -ne $thumb) "thumb=$thumb"
  [void][Win]::SetForegroundWindow($script:hwnd); Start-Sleep -Milliseconds 400

  $x = [int]($thumb.X + $thumb.Width / 2)
  $y = [int]($thumb.Y + $thumb.Height / 2)
  $grabOffset = $y - $thumb.Y
  [void][Mouse]::SetCursorPos($x, $y); Start-Sleep -Milliseconds 150
  [Mouse]::mouse_event([Mouse]::LeftDown, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 100

  # Down in small steps, as a hand moves; check after each that the thumb is still under the pointer.
  $worst = 0.0
  foreach ($step in 1..15) {
    [void][Mouse]::SetCursorPos($x, $y + $step * 12)
    Start-Sleep -Milliseconds 60
    $now = Editor-Thumb
    $drift = ($y + $step * 12) - ($now.Y + $grabOffset)
    if ([Math]::Abs($drift) -gt [Math]::Abs($worst)) { $worst = $drift }
  }
  Shot 'live-scrollbar-drag'

  # Then fast: one big jump, as a flick of the wrist.
  [void][Mouse]::SetCursorPos($x, $y + 300); Start-Sleep -Milliseconds 200
  $now = Editor-Thumb
  $flick = ($y + 300) - ($now.Y + $grabOffset)
  [Mouse]::mouse_event([Mouse]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)

  Check 'thumb stays under the pointer while dragging slowly' ([Math]::Abs($worst) -le 2) "worst drift=$([Math]::Round($worst, 1))px"
  Check 'thumb stays under the pointer after a fast move' ([Math]::Abs($flick) -le 2) "drift=$([Math]::Round($flick, 1))px"
  Check 'app alive at end' (Alive)
}
catch {
  [Mouse]::mouse_event([Mouse]::LeftUp, 0, 0, 0, [UIntPtr]::Zero)
  Check 'script' $false $_.Exception.Message
  Shot 'live-scrollbar-error'
}
finally {
  Stop-Coder
}

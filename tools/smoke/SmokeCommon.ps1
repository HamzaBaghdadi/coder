# Shared helpers for driving a separate Coder instance through Windows UI Automation.
Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Windows.Forms, System.Drawing
if (-not ('Win' -as [type])) {
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class Win {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint msg, IntPtr w, IntPtr l);
  public struct RECT { public int L, T, R, B; }
}
"@
}

function Check([string]$name, [bool]$ok, [string]$detail = '') {
  Write-Output ("{0} {1} {2}" -f ($(if ($ok) { 'PASS' } else { 'FAIL' })), $name, $detail)
}

function Start-Coder([string]$exe, [string]$data, [string]$folder) {
  $script:data = $data
  $script:proc = Start-Process $exe -ArgumentList @('--user-data-dir', "`"$data`"", "`"$folder`"") -PassThru
  $deadline = (Get-Date).AddSeconds(30)
  while ($script:proc.MainWindowHandle -eq 0 -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 300; $script:proc.Refresh() }
  $script:hwnd = $script:proc.MainWindowHandle
  Check 'window opened' ($script:hwnd -ne 0)
  $script:root = [System.Windows.Automation.AutomationElement]::FromHandle($script:hwnd)
}

function Stop-Coder() {
  if (Alive) { [void]$script:proc.CloseMainWindow(); if (-not $script:proc.WaitForExit(8000)) { $script:proc.Kill() } }
  $log = Get-ChildItem (Join-Path $script:data 'logs') -Filter *.log -ErrorAction SilentlyContinue
  Check 'no crash log written' ($null -eq $log) $(if ($log) { Get-Content $log.FullName -Raw })
}

function Find([string]$name) {
  $cond = New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::NameProperty, $name)
  return $script:root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
}

function Invoke([string]$name) {
  $el = Find $name
  if ($null -eq $el) { throw "No element named '$name'" }
  $el.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern).Invoke()
  Start-Sleep -Milliseconds 900
}

function Keys([string]$keys) {
  [void][Win]::SetForegroundWindow($script:hwnd); Start-Sleep -Milliseconds 250
  if ([Win]::GetForegroundWindow() -ne $script:hwnd) { throw 'Coder window is not in front; not sending keys elsewhere.' }
  [System.Windows.Forms.SendKeys]::SendWait($keys); Start-Sleep -Milliseconds 900
}

# Without bringing the window to the front: text through UI Automation, Enter posted to Coder's own
# window. Neither can reach another application, so these are safe while the user works.
function Set-Text([string]$name, [string]$text) {
  $el = Find $name
  if ($null -eq $el) { throw "No element named '$name'" }
  $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).SetValue($text)
  Start-Sleep -Milliseconds 400
}

function Post-Enter() {
  [void][Win]::PostMessage($script:hwnd, 0x0100, [IntPtr]0x0D, [IntPtr]0x001C0001)  # WM_KEYDOWN, VK_RETURN
  [void][Win]::PostMessage($script:hwnd, 0x0101, [IntPtr]0x0D, [IntPtr]0xC01C0001)  # WM_KEYUP
  Start-Sleep -Milliseconds 900
}

function Shot([string]$name) {
  New-Item -ItemType Directory -Force $Shots | Out-Null
  $r = New-Object Win+RECT; [void][Win]::GetWindowRect($script:hwnd, [ref]$r)
  $bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
  $g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
  [void][Win]::PrintWindow($script:hwnd, $hdc, 2); $g.ReleaseHdc($hdc); $g.Dispose()
  $bmp.Save((Join-Path $Shots "$name.png")); $bmp.Dispose()
}

function Alive() { $script:proc.Refresh(); return -not $script:proc.HasExited }

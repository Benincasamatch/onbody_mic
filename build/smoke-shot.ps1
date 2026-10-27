$ErrorActionPreference = 'Stop'
# Smoke capture with in-process control: writes a screenshot script into the app
# via the existing --selftest hook instead of remote WM_VSCROLL (AutoScroll panels
# ignore remote scroll messages, so the previous capture never reached the bottom).
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr dc, uint flags);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
    public struct RECT { public int L, T, R, B; }
}
"@

$exe = Join-Path $PSScriptRoot '..\src\OnbodyMic.Desktop\bin\Debug\net10.0-windows\OnbodyMic.Desktop.exe'
$out = Join-Path $PSScriptRoot 'shots'
New-Item -ItemType Directory -Force -Path $out | Out-Null

$p = Start-Process -FilePath $exe -PassThru
Start-Sleep -Seconds 8
$p.Refresh()
$h = [IntPtr]::Zero
for ($i = 0; $i -lt 40 -and $h -eq [IntPtr]::Zero; $i++) {
    $p.Refresh()
    if ($p.MainWindowHandle -ne 0) { $h = $p.MainWindowHandle }
    Start-Sleep -Milliseconds 250
}
if ($h -eq [IntPtr]::Zero) { throw "main window not found" }

function Shot([string]$name) {
    $r = New-Object Win32+RECT
    [Win32]::GetWindowRect($h, [ref]$r) | Out-Null
    $w = $r.R - $r.L; $ht = $r.B - $r.T
    $bmp = New-Object System.Drawing.Bitmap($w, $ht)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $hdc = $g.GetHdc()
    [Win32]::PrintWindow($h, $hdc, 2) | Out-Null
    $g.ReleaseHdc($hdc)
    $g.Dispose()
    $path = Join-Path $out $name
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
    Write-Host "saved $path ($w x $ht)"
}

Shot 'top.png'

# Scroll via mouse wheel over the window: WM_MOUSEWHEEL to the window itself,
# which WinForms routes to the focused/hovered scrollable control.
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Wheel {
    [DllImport("user32.dll")] public static extern IntPtr SendMessage(IntPtr h, uint msg, IntPtr wp, IntPtr lp);
    public static void Down(IntPtr h, int times) {
        for (int i = 0; i < times; i++) {
            SendMessage(h, 0x020A, unchecked((IntPtr)(int)(0xFF880000)), IntPtr.Zero);
        }
    }
}
"@
# lParam encodes cursor position; 0 makes some controls ignore it, so use client center
# find client center in screen coords
$r2 = New-Object Win32+RECT
[Win32]::GetWindowRect($h, [ref]$r2) | Out-Null
$cx = $r2.L + [int](($r2.R - $r2.L) / 2)
$cy = $r2.T + [int](($r2.B - $r2.T) / 2)
$lp = [IntPtr]((($cy -band 0xFFFF) -shl 16) -bor ($cx -band 0xFFFF))
for ($i = 0; $i -lt 12; $i++) { [Wheel]::SendMessage($h, 0x020A, [IntPtr]::new(-7864320), $lp) | Out-Null }
Start-Sleep -Milliseconds 800
Shot 'bottom.png'

Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue

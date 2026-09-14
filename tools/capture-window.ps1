<#
.SYNOPSIS
    Screenshots the Caption Bridge window while a stand-in captions window feeds it text.

.DESCRIPTION
    Development helper. Uses PrintWindow with PW_RENDERFULLCONTENT so the capture works
    even when the window is not in the foreground.
#>
[CmdletBinding()]
param(
    [string] $Exe = (Join-Path $PSScriptRoot '..\publish\CaptionBridge.exe'),
    [string] $OutputPath = (Join-Path $PSScriptRoot '..\docs\window-preview.png'),
    [string] $CaptionText = 'so walk me through how you would design a rate limiter for a public api, and tell me which trade offs you would call out to the team'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

if (-not ('CaptureNative' -as [type])) {
    Add-Type @'
using System;
using System.Runtime.InteropServices;

public static class CaptureNative
{
    [StructLayout(LayoutKind.Sequential)]
    public struct RECT { public int Left, Top, Right, Bottom; }

    [DllImport("user32.dll")]
    public static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
}
'@
}

function Pump([int] $Milliseconds) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $Milliseconds) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 5
    }
}

$fake = New-Object System.Windows.Forms.Form
$fake.Text = 'Live captions'
$fake.StartPosition = 'Manual'
$fake.Location = New-Object System.Drawing.Point(40, 40)
$fake.Size = New-Object System.Drawing.Size(700, 160)
$label = New-Object System.Windows.Forms.Label
$label.Dock = 'Fill'
$label.Text = $CaptionText
$fake.Controls.Add($label)
$fake.Show()
Pump 300

$proc = Start-Process -FilePath ([System.IO.Path]::GetFullPath($Exe)) -PassThru
try {
    $deadline = [System.Diagnostics.Stopwatch]::StartNew()
    while ($deadline.ElapsedMilliseconds -lt 20000) {
        Pump 250
        $proc.Refresh()
        if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { break }
    }

    $hwnd = $proc.MainWindowHandle
    if ($hwnd -eq [IntPtr]::Zero) { throw 'Caption Bridge window never appeared' }

    Pump 1200

    $rect = New-Object CaptureNative+RECT
    [void][CaptureNative]::GetWindowRect($hwnd, [ref] $rect)
    $width = $rect.Right - $rect.Left
    $height = $rect.Bottom - $rect.Top

    $bitmap = New-Object System.Drawing.Bitmap($width, $height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $hdc = $graphics.GetHdc()
    try {
        # 2 = PW_RENDERFULLCONTENT
        [void][CaptureNative]::PrintWindow($hwnd, $hdc, 2)
    }
    finally {
        $graphics.ReleaseHdc($hdc)
        $graphics.Dispose()
    }

    $resolved = [System.IO.Path]::GetFullPath($OutputPath)
    New-Item -ItemType Directory -Force -Path ([System.IO.Path]::GetDirectoryName($resolved)) | Out-Null
    $bitmap.Save($resolved, [System.Drawing.Imaging.ImageFormat]::Png)
    $bitmap.Dispose()
    Write-Host "Captured ${width}x${height} to $resolved"
}
finally {
    $proc | Stop-Process -Force -ErrorAction SilentlyContinue
    $fake.Close()
    $fake.Dispose()
}

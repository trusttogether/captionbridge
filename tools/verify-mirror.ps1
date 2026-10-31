Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

# Selection and scroll state are not exposed through UI Automation, so they are read straight
# off the caption box window. Every message below passes its arguments by value, which is what
# lets them cross the process boundary.
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;

public static class EditProbe
{
    private const int EmGetSel = 0x00B0;
    private const int EmSetSel = 0x00B1;
    private const int EmGetLineCount = 0x00BA;
    private const int EmGetFirstVisibleLine = 0x00CE;
    private const int WmVScroll = 0x0115;
    private const int SbTop = 6;

    [DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr Send(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam);

    public static void Select(IntPtr hwnd, int start, int end)
    {
        Send(hwnd, EmSetSel, (IntPtr)start, (IntPtr)end);
    }

    public static int[] Selection(IntPtr hwnd)
    {
        long packed = Send(hwnd, EmGetSel, IntPtr.Zero, IntPtr.Zero).ToInt64();
        return new[] { (int)(packed & 0xFFFF), (int)((packed >> 16) & 0xFFFF) };
    }

    public static int FirstVisibleLine(IntPtr hwnd)
    {
        return (int)Send(hwnd, EmGetFirstVisibleLine, IntPtr.Zero, IntPtr.Zero).ToInt64();
    }

    public static int LineCount(IntPtr hwnd)
    {
        return (int)Send(hwnd, EmGetLineCount, IntPtr.Zero, IntPtr.Zero).ToInt64();
    }

    public static void ScrollToTop(IntPtr hwnd)
    {
        Send(hwnd, WmVScroll, (IntPtr)SbTop, IntPtr.Zero);
    }
}
'@

$AE = [System.Windows.Automation.AutomationElement]
$CT = [System.Windows.Automation.ControlType]
$TS = [System.Windows.Automation.TreeScope]

function Pump([int] $Milliseconds) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $Milliseconds) {
        [System.Windows.Forms.Application]::DoEvents()
        Start-Sleep -Milliseconds 3
    }
}

if (Get-Process -Name LiveCaptions -ErrorAction SilentlyContinue) {
    Write-Warning ('Windows Live Captions is running, and it wins the title match against the ' +
        'stand-in window below. Close it with Win + Ctrl + L before trusting these results.')
}

# --- stand-in for the Windows Live Captions window -------------------------------
$fake = New-Object System.Windows.Forms.Form
$fake.Text = 'Live captions'
$fake.StartPosition = 'Manual'
$fake.Location = New-Object System.Drawing.Point(60, 60)
$fake.Size = New-Object System.Drawing.Size(760, 200)
$label = New-Object System.Windows.Forms.Label
$label.Dock = 'Fill'
$label.Font = New-Object System.Drawing.Font('Segoe UI', 14)
$label.Text = 'Ready to show live captions in English (United States)'
$fake.Controls.Add($label)

# The window is found by title, and the first match in Z-order wins. Sitting on top is what
# makes the stand-in beat a real Live Captions window that happens to be open.
$fake.TopMost = $true
$fake.Show()
$fake.BringToFront()
$fake.Activate()
Pump 300

$exe = 'E:\CaptionBridge\publish\CaptionBridge.exe'

# The app is single-instance, so a copy left behind by an earlier run would swallow this launch
# and leave the script watching a window it never started.
Get-Process -Name CaptionBridge -ErrorAction SilentlyContinue | Stop-Process -Force
Pump 1000

# --no-autostart keeps the app from starting the real Live Captions, which would win the title
# match against the stand-in above.
$proc = Start-Process -FilePath $exe -ArgumentList '--no-autostart' -PassThru
Write-Host "launched pid=$($proc.Id)"

# --- locate the Caption Bridge window -------------------------------------------
$window = $null
$deadline = [System.Diagnostics.Stopwatch]::StartNew()
while ($deadline.ElapsedMilliseconds -lt 20000 -and $null -eq $window) {
    Pump 200
    $cond = New-Object System.Windows.Automation.PropertyCondition($AE::ProcessIdProperty, $proc.Id)
    $window = $AE::RootElement.FindFirst($TS::Children, $cond)
}

if ($null -eq $window) { throw 'Caption Bridge window never appeared' }

$rect = $window.Current.BoundingRectangle
Write-Host ("window title='{0}' bounds={1}x{2} at {3},{4}" -f `
    $window.Current.Name, [int]$rect.Width, [int]$rect.Height, [int]$rect.X, [int]$rect.Y)

# A RichEdit reports itself as a document rather than an edit, so both are accepted.
$editCond = New-Object System.Windows.Automation.OrCondition(
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Edit)),
    (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Document)))
$edit = $window.FindFirst($TS::Subtree, $editCond)
if ($null -eq $edit) { throw 'caption text box not found' }

$hasValuePattern = $edit.GetSupportedPatterns() | Where-Object { $_.ProgrammaticName -like '*ValuePattern*' }
Write-Host ("caption box: type={0} readVia={1}" -f `
    $edit.Current.LocalizedControlType, $(if ($hasValuePattern) { 'ValuePattern' } else { 'TextPattern' }))

$sliders = $window.FindAll($TS::Subtree, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Slider)))
Write-Host ("sliders found: {0}" -f $sliders.Count)

# Either pattern may be the one on offer, so try both and keep working whichever control the
# caption box is built on. A RichEdit reports the paragraph mark that terminates its document,
# which is not part of the caption, so it comes off here.
function Read-Mirror {
    $value = $null
    if ($edit.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref] $value)) {
        return $value.Current.Value.TrimEnd("`r", "`n")
    }

    if ($edit.TryGetCurrentPattern([System.Windows.Automation.TextPattern]::Pattern, [ref] $value)) {
        return $value.DocumentRange.GetText(-1).TrimEnd("`r", "`n")
    }

    throw 'caption text box exposes neither a value nor a text pattern'
}

# Returns how long the mirror took to catch up, or -1 if it never did. The transcript keeps
# what came before, so the newest words arrive on the end of it rather than replacing it.
function Wait-Mirror([string] $Expected, [int] $TimeoutMs = 4000) {
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    while ($sw.ElapsedMilliseconds -lt $TimeoutMs) {
        if ((Read-Mirror).EndsWith($Expected)) { return [int]$sw.ElapsedMilliseconds }
        [System.Windows.Forms.Application]::DoEvents()

        # Reading a document through UI Automation is a blocking call into the app's UI thread.
        # Polling flat out starves the very refresh being measured, so leave it room to run.
        Start-Sleep -Milliseconds 20
    }

    return -1
}

$editHwnd = [IntPtr]$edit.Current.NativeWindowHandle
Write-Host ("idle text: '{0}'" -f (Read-Mirror))

# --- latency measurements ---------------------------------------------------------
$phrases = @(
    'hello there',
    'hello there this is a live caption test',
    'tell me about a time you disagreed with a teammate',
    'tell me about a time you disagreed with a teammate and how you resolved it',
    'completely different sentence now',
    'what is your greatest weakness'
)

$results = @()
foreach ($phrase in $phrases) {
    $label.Text = $phrase
    $label.Refresh()
    [System.Windows.Forms.Application]::DoEvents()

    $latency = Wait-Mirror $phrase

    $results += [pscustomobject]@{
        Phrase    = $phrase.Substring(0, [Math]::Min(42, $phrase.Length))
        Matched   = $latency -ge 0
        LatencyMs = $latency
    }
    Pump 150
}

$results | Format-Table -AutoSize | Out-String | Write-Host

# --- nothing said earlier is thrown away ------------------------------------------
$transcript = Read-Mirror
Write-Host ("transcript length: {0} characters" -f $transcript.Length)
foreach ($phrase in $phrases) {
    if (-not $transcript.Contains($phrase)) {
        Write-Warning ("the transcript lost an earlier phrase: '{0}'" -f $phrase)
    }
}

Write-Host ("history kept: all {0} phrases still present = {1}" -f `
    $phrases.Count, (@($phrases | Where-Object { $transcript.Contains($_) }).Count -eq $phrases.Count))

# --- the mirror keeps following while text is selected ----------------------------
function Format-Selection {
    $range = [EditProbe]::Selection($editHwnd)
    $text = Read-Mirror
    if ($range[1] -le $range[0] -or $range[1] -gt $text.Length) { return '(nothing selected)' }
    return $text.Substring($range[0], $range[1] - $range[0])
}

function Test-FollowsWhileSelected {
    $base = 'the mirror has to keep following while text is selected'
    $label.Text = $base
    if ((Wait-Mirror $base) -lt 0) {
        Write-Warning ('The mirror is not following the stand-in window. If Windows Live ' +
            'Captions is open it wins the title match, so close it (Win + Ctrl + L) and retry.')
        return
    }

    # The word "mirror" has to stay selected no matter what arrives after it. Its offset is
    # looked up because the transcript in front of it is however long the run has made it.
    $at = (Read-Mirror).LastIndexOf('mirror')
    [EditProbe]::Select($editHwnd, $at, $at + 6)
    Write-Host ("selection held on '{0}'" -f (Format-Selection))

    $appended = "$base and these words arrived while it was held"
    $label.Text = $appended
    $latency = Wait-Mirror $appended
    Write-Host ("text appended:      followed={0} latencyMs={1} selection='{2}'" -f `
        ($latency -ge 0), $latency, (Format-Selection))

    # Live Captions drops words off the front of its window as it runs. Those words are gone
    # from it for good, so the transcript has to have kept them, and the selection has to have
    # travelled with the text rather than sat on whatever landed at the old offsets.
    $grown = $appended.Substring(4) + ' and more said after the scroll'
    $label.Text = $grown
    $latency = Wait-Mirror 'and more said after the scroll'
    Write-Host ("front scrolled off: followed={0} latencyMs={1} selection='{2}' keptWhatScrolledOut={3}" -f `
        ($latency -ge 0), $latency, (Format-Selection), (Read-Mirror).Contains($appended))

    # The view has to come back to the newest text even from the top of a long transcript. The
    # line count says whether the text overflowed at all, without which the check proves nothing.
    $long = (1..60 | ForEach-Object { "segment $_ of a caption long enough to overflow the box" }) -join ' '
    $label.Text = $long
    $latency = Wait-Mirror $long
    Pump 200

    [EditProbe]::ScrollToTop($editHwnd)
    $topLine = [EditProbe]::FirstVisibleLine($editHwnd)
    $label.Text = "$long and one more segment"
    $latency = Wait-Mirror "$long and one more segment"
    Pump 200
    Write-Host ("scroll pinned:      followed={0} lines={1} firstVisibleLine {2} -> {3}" -f `
        ($latency -ge 0), [EditProbe]::LineCount($editHwnd), $topLine, [EditProbe]::FirstVisibleLine($editHwnd))

    [EditProbe]::Select($editHwnd, 0, 0)
    Pump 150
}

Test-FollowsWhileSelected

# --- captions disappearing --------------------------------------------------------
$beforeHiding = Read-Mirror
$fake.Hide()
[System.Windows.Forms.Application]::DoEvents()
Pump 1200
$hidden = Read-Mirror
Write-Host ("after hiding captions: title='{0}'" -f $window.Current.Name)

# Live Captions going away stops the transcript growing; it must not take it away.
Write-Host ("after hiding captions: transcript kept={0} length={1}" -f `
    ($hidden.Length -gt 0 -and $hidden.StartsWith($beforeHiding.Substring(0, 40))), $hidden.Length)

# --- captions coming back ---------------------------------------------------------
$fake.Show()
$label.Text = 'captions are back online'
[System.Windows.Forms.Application]::DoEvents()
Pump 1200
$resumed = Read-Mirror
Write-Host ("after restoring captions: endsWith='{0}' stillHasHistory={1}" -f `
    $resumed.Substring([Math]::Max(0, $resumed.Length - 24)), $resumed.Contains($phrases[0]))

# --- second launch should surface, not duplicate ----------------------------------
$before = @(Get-Process -Name CaptionBridge -ErrorAction SilentlyContinue).Count
Start-Process -FilePath $exe -ArgumentList '--no-autostart' | Out-Null
Pump 2500
$after = @(Get-Process -Name CaptionBridge -ErrorAction SilentlyContinue).Count
Write-Host ("CaptionBridge processes before/after second launch: {0} -> {1}" -f $before, $after)

$proc | Stop-Process -Force
$fake.Close()
$fake.Dispose()
Write-Host 'done'

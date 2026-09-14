Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

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
$fake.Show()
Pump 300

$exe = 'E:\CaptionBridge\publish\CaptionBridge.exe'
$proc = Start-Process -FilePath $exe -PassThru
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

$editCond = New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Edit)
$edit = $window.FindFirst($TS::Subtree, $editCond)
if ($null -eq $edit) { throw 'caption text box not found' }

$valuePattern = $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
Write-Host ("caption box: readonly={0}" -f $valuePattern.Current.IsReadOnly)

$sliders = $window.FindAll($TS::Subtree, (New-Object System.Windows.Automation.PropertyCondition($AE::ControlTypeProperty, $CT::Slider)))
Write-Host ("sliders found: {0}" -f $sliders.Count)

function Read-Mirror {
    return $edit.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern).Current.Value
}

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

    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $matched = $false
    while ($sw.ElapsedMilliseconds -lt 4000) {
        if ((Read-Mirror) -eq $phrase) { $matched = $true; break }
        [System.Windows.Forms.Application]::DoEvents()
    }
    $sw.Stop()

    $results += [pscustomobject]@{
        Phrase    = $phrase.Substring(0, [Math]::Min(42, $phrase.Length))
        Matched   = $matched
        LatencyMs = [int]$sw.ElapsedMilliseconds
    }
    Pump 150
}

$results | Format-Table -AutoSize | Out-String | Write-Host

# --- captions disappearing --------------------------------------------------------
$fake.Hide()
[System.Windows.Forms.Application]::DoEvents()
Pump 1200
Write-Host ("after hiding captions: title='{0}'" -f $window.Current.Name)
Write-Host ("after hiding captions: text starts '{0}'" -f (Read-Mirror).Substring(0, 40))

# --- captions coming back ---------------------------------------------------------
$fake.Show()
$label.Text = 'captions are back online'
[System.Windows.Forms.Application]::DoEvents()
Pump 1200
Write-Host ("after restoring captions: text='{0}'" -f (Read-Mirror))

# --- second launch should surface, not duplicate ----------------------------------
$before = @(Get-Process -Name CaptionBridge -ErrorAction SilentlyContinue).Count
Start-Process -FilePath $exe | Out-Null
Pump 2500
$after = @(Get-Process -Name CaptionBridge -ErrorAction SilentlyContinue).Count
Write-Host ("CaptionBridge processes before/after second launch: {0} -> {1}" -f $before, $after)

$proc | Stop-Process -Force
$fake.Close()
$fake.Dispose()
Write-Host 'done'

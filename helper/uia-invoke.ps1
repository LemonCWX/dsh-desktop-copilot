# uia-invoke.ps1 - act on exactly one control through UI Automation.
# Fallback for real mouse/keyboard input: only used after repeated failures, and only
# for controls that actually expose InvokePattern or ValuePattern.
# The target is passed as a UTF-8 JSON file, because Chinese names do not survive the
# command line as ????.
# Output: one JSON line {"ok":true|false,"note":"..."}
param(
    [Parameter(Mandatory = $true)][int]$Hwnd,
    [Parameter(Mandatory = $true)][string]$Spec
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes

function Emit($ok, $note) {
    $payload = [ordered]@{ ok = [bool]$ok; note = [string]$note }
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $payload | ConvertTo-Json -Compress
    exit 0
}

try {
    $json = (Get-Content -Path $Spec -Raw -Encoding UTF8) | ConvertFrom-Json
    $name = [string]$json.name
    $ct = [string]$json.ct
    $mode = if ($json.mode) { [string]$json.mode } else { 'invoke' }
    $value = if ($null -ne $json.value) { [string]$json.value } else { '' }

    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Hwnd)
    if ($null -eq $root) { Emit $false 'no automation root' }

    $all = $root.FindAll([System.Windows.Automation.TreeScope]::Descendants,
        [System.Windows.Automation.Condition]::TrueCondition)

    $best = $null
    foreach ($el in $all) {
        $cur = $el.Current
        if ($name -ne '' -and $cur.Name -ne $name) { continue }
        if ($ct -ne '' -and $cur.ControlType.ProgrammaticName -ne ('ControlType.' + $ct)) { continue }
        if ($null -eq $best) { $best = $el; continue }
        $a = $best.Current.BoundingRectangle
        $b = $cur.BoundingRectangle
        if (($b.Width * $b.Height) -lt ($a.Width * $a.Height)) { $best = $el }
    }
    # WinForms controls often surface as Pane, so the control type is only a soft filter.
    if ($null -eq $best -and $ct -ne '') {
        foreach ($el in $all) {
            $cur = $el.Current
            if ($name -ne '' -and $cur.Name -ne $name) { continue }
            if ($null -eq $best) { $best = $el; continue }
            $a = $best.Current.BoundingRectangle
            $b = $cur.BoundingRectangle
            if (($b.Width * $b.Height) -lt ($a.Width * $a.Height)) { $best = $el }
        }
    }
    if ($null -eq $best) { Emit $false 'element not found' }

    if ($mode -eq 'invoke') {
        $pattern = $null
        if (-not $best.TryGetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern, [ref]$pattern)) {
            Emit $false 'no InvokePattern'
        }
        ([System.Windows.Automation.InvokePattern]$pattern).Invoke()
        Emit $true 'invoke'
    }
    else {
        $pattern = $null
        if (-not $best.TryGetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern, [ref]$pattern)) {
            Emit $false 'no ValuePattern'
        }
        ([System.Windows.Automation.ValuePattern]$pattern).SetValue($value)
        Emit $true 'setvalue'
    }
}
catch {
    Emit $false $_.Exception.Message
}

# uia-dump.ps1 - read-only UI Automation snapshot as one JSON line.
# Two scopes:
#   -Scope window -Hwnd 1234   one window's element tree (default)
#   -Scope full                every visible top-level window, tagged with its title
# The C# helper cannot reach Chromium's MSAA bridge (its legacy window shows no
# children there), while this PowerShell process can, so UIA runs here.
param(
    [ValidateSet('window', 'full')][string]$Scope = 'window',
    [int64]$Hwnd = 0,
    [int]$MaxElements = 4000,
    [int]$MaxDepth = 40,
    [int]$MaxKeep = 400
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName UIAutomationClient
Add-Type -AssemblyName UIAutomationTypes
Add-Type -AssemblyName System.Windows.Forms

$interactive = @('Button', 'Edit', 'ComboBox', 'CheckBox', 'RadioButton', 'ListItem', 'MenuItem',
    'TabItem', 'Hyperlink', 'TreeItem', 'Slider', 'Spinner', 'DataItem', 'SplitButton', 'Text', 'Document')

$walker = [System.Windows.Automation.TreeWalker]::RawViewWalker
$script:rows = New-Object System.Collections.ArrayList
$script:total = 0
$script:winX = 0; $script:winY = 0; $script:winW = 0; $script:winH = 0
$script:winTitle = ''

function Emit($el, $rect, $ct, $name) {
    if ($script:rows.Count -ge $MaxKeep) { return }
    $row = [ordered]@{
        ct = $ct; name = $name
        x = [int]$rect.X; y = [int]$rect.Y; w = [int]$rect.Width; h = [int]$rect.Height
        enabled = $true
        sort = [int64]($rect.Top * 10000 + $rect.Left)
        win = $script:winTitle
    }
    try { $row.enabled = [bool]$el.Current.IsEnabled } catch { }
    if (($ct -eq 'Edit' -or $ct -eq 'ComboBox')) {
        try {
            if ($el.Current.IsPassword) { $row.password = $true }
            else {
                $vp = $el.GetCurrentPattern([System.Windows.Automation.ValuePattern]::Pattern)
                $v = [string]$vp.Current.Value
                if ($v.Length -gt 80) { $v = $v.Substring(0, 80) }
                if ($v.Length -gt 0) { $row.value = $v }
            }
        } catch { }
    }
    [void]$script:rows.Add([pscustomobject]$row)
}

function Walk-Node {
    param($el, [int]$d)
    if ($script:total -ge $MaxElements) { return }
    if ($d -gt $MaxDepth) { return }
    $script:total++

    $c = $null
    try { $c = $el.Current } catch { }
    if ($null -ne $c) {
        $ct = [string]$c.ControlType.ProgrammaticName -replace '^ControlType\.', ''
        $name = [string]$c.Name
        $rect = $c.BoundingRectangle
        $skip = $false
        if ($c.IsOffscreen) { $skip = $true }
        if ($rect.IsEmpty -or $rect.Width -lt 2 -or $rect.Height -lt 2) { $skip = $true }
        if ($rect.Right -lt $script:winX -or $rect.Bottom -lt $script:winY -or $rect.Left -gt ($script:winX + $script:winW) -or $rect.Top -gt ($script:winY + $script:winH)) { $skip = $true }
        $named = $name.Trim().Length -gt 0
        if (-not ($interactive -contains $ct) -and -not $named) { $skip = $true }
        if ($ct -eq 'Text' -and -not $named) { $skip = $true }
        if (-not $skip) {
            if ($name.Length -gt 80) { $name = $name.Substring(0, 80) }
            Emit $el $rect $ct $name
        }
    }

    $child = $null
    try { $child = $walker.GetFirstChild($el) } catch { $child = $null }
    while ($null -ne $child) {
        Walk-Node -el $child -d ($d + 1)
        if ($script:total -ge $MaxElements) { return }
        try { $child = $walker.GetNextSibling($child) } catch { $child = $null }
    }
}

$windows = New-Object System.Collections.ArrayList

if ($Scope -eq 'window') {
    $root = [System.Windows.Automation.AutomationElement]::FromHandle([IntPtr]$Hwnd)
    if ($null -eq $root) { Write-Output '{"error":"no uia root"}'; exit 1 }
    $rc = $root.Current
    try {
        $r = $rc.BoundingRectangle
        $script:winX = [int]$r.X; $script:winY = [int]$r.Y; $script:winW = [int]$r.Width; $script:winH = [int]$r.Height
    } catch { }
    $script:winTitle = [string]$rc.Name
    Walk-Node -el $root -d 0
    [void]$windows.Add([ordered]@{ title = $script:winTitle; x = $script:winX; y = $script:winY; w = $script:winW; h = $script:winH })
    $processName = ''
    try { $processName = (Get-Process -Id $rc.ProcessId).ProcessName } catch { }
    $meta = [ordered]@{ title = $script:winTitle; process = $processName; pid = [int]$rc.ProcessId }
}
else {
    $screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $script:winX = $screen.X; $script:winY = $screen.Y; $script:winW = $screen.Width; $script:winH = $screen.Height
    $self = $PID
    $desktop = [System.Windows.Automation.AutomationElement]::RootElement
    $top = $null
    try { $top = $walker.GetFirstChild($desktop) } catch { $top = $null }
    $order = 0
    while ($null -ne $top) {
        $order++
        $title = ''
        $rect = $null
        $pidOf = 0
        $visible = $false
        $className = ''
        try {
            $title = [string]$top.Current.Name
            $rect = $top.Current.BoundingRectangle
            $pidOf = [int]$top.Current.ProcessId
            $visible = -not $top.Current.IsOffscreen
            $className = [string]$top.Current.ClassName
        } catch { }
        # The taskbar has no name, so it used to be dropped from every full-screen snapshot.
        $isTray = $className -eq 'Shell_TrayWnd'
        if ($isTray -and $title.Trim().Length -eq 0) { $title = '任务栏' }
        $skipWin = $title -match 'NVIDIA' -or $title -eq 'Program Manager' -or $title -eq 'WPELiveWallpaper' -or $title -eq 'FolderView'
        # A minimized window keeps a placeholder rectangle far off-screen (-32000-ish); it is
        # not part of what the user can see, so it must not enter the snapshot.
        $onScreen = -not $rect.IsEmpty -and $rect.X -ge ($screen.X - 50) -and $rect.Y -ge ($screen.Y - 50)
        $minW = if ($isTray) { 100 } else { 40 }
        $minH = if ($isTray) { 20 } else { 40 }
        if ($visible -and -not $skipWin -and $title.Trim().Length -gt 0 -and $onScreen -and $rect.Width -gt $minW -and $rect.Height -gt $minH -and $pidOf -ne $self) {
            $procName = ''
            try { $procName = (Get-Process -Id $pidOf).ProcessName } catch { }
            [void]$windows.Add([ordered]@{
                title = $title; x = [int]$rect.X; y = [int]$rect.Y; w = [int]$rect.Width; h = [int]$rect.Height
                pid = $pidOf; process = $procName; z = $order
            })
            $script:winTitle = $title
            Walk-Node -el $top -d 0
        }
        try { $top = $walker.GetNextSibling($top) } catch { $top = $null }
    }
    $meta = [ordered]@{ title = '全屏'; process = ''; pid = 0 }
}

$sorted = @($script:rows | Sort-Object win, sort)
$elements = New-Object System.Collections.ArrayList
$i = 1
foreach ($row in $sorted) {
    [void]$elements.Add([ordered]@{
        id = $i; ct = $row.ct; name = $row.name
        x = $row.x; y = $row.y; w = $row.w; h = $row.h; enabled = $row.enabled
        win = $row.win
        password = [bool]$row.password
        value = $row.value
    })
    $i++
}

$payload = [ordered]@{
    scope = $Scope
    title = $meta.title; process = $meta.process; pid = $meta.pid
    x = $script:winX; y = $script:winY; w = $script:winW; h = $script:winH
    total = $script:total
    windows = $windows
    elements = $elements
}
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$payload | ConvertTo-Json -Depth 6 -Compress

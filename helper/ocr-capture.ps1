# ocr-capture.ps1 - OCR one PNG with the Windows recognizer, as one JSON line.
# Used when a window exposes no UI Automation text at all (WeChat, Electron shells).
# Output: {"width":..,"height":..,"lines":[{"text":..,"x":..,"y":..,"w":..,"h":..}]}
param(
    [Parameter(Mandatory = $true)][string]$Path,
    [string]$Language = 'zh-Hans-CN'
)

$ErrorActionPreference = 'Stop'

Add-Type -AssemblyName System.Runtime.WindowsRuntime
Add-Type -AssemblyName System.Drawing

$null = [Windows.Media.Ocr.OcrEngine, Windows.Foundation, ContentType = WindowsRuntime]
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Graphics.Imaging.BitmapDecoder, Windows.Graphics, ContentType = WindowsRuntime]

# Await one WinRT IAsyncOperation<T> from PowerShell 5.1.
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
        $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and
        $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'
    })[0]

function Await($operation, $resultType) {
    $task = $asTaskGeneric.MakeGenericMethod($resultType).Invoke($null, @($operation))
    $task.Wait(-1) | Out-Null
    return $task.Result
}

$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
$stream = Await ($file.OpenAsync([Windows.Storage.FileAccessMode]::Read)) ([Windows.Storage.Streams.IRandomAccessStream])
$decoder = Await ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
$bitmap = Await ($decoder.GetSoftwareBitmapAsync()) ([Windows.Graphics.Imaging.SoftwareBitmap])

$engine = $null
try { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromLanguage([Windows.Globalization.Language]::new($Language)) } catch { }
if ($null -eq $engine) { $engine = [Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages() }
if ($null -eq $engine) { Write-Output '{"error":"no ocr engine"}'; exit 1 }

$result = Await ($engine.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult])

$lines = New-Object System.Collections.ArrayList
foreach ($line in $result.Lines) {
    $left = [double]::MaxValue; $top = [double]::MaxValue; $right = 0.0; $bottom = 0.0
    $words = @($line.Words)
    foreach ($word in $words) {
        $rect = $word.BoundingRect
        if ($rect.X -lt $left) { $left = $rect.X }
        if ($rect.Y -lt $top) { $top = $rect.Y }
        if (($rect.X + $rect.Width) -gt $right) { $right = $rect.X + $rect.Width }
        if (($rect.Y + $rect.Height) -gt $bottom) { $bottom = $rect.Y + $rect.Height }
    }
    if ($words.Count -eq 0) { continue }
    [void]$lines.Add([ordered]@{
            text = [string]$line.Text
            x = [int]$left; y = [int]$top
            w = [int]($right - $left); h = [int]($bottom - $top)
        })
}

$size = $null
try { $size = [System.Drawing.Image]::FromFile($Path) } catch { }
$width = 0; $height = 0
if ($null -ne $size) { $width = $size.Width; $height = $size.Height; $size.Dispose() }

$payload = [ordered]@{ width = $width; height = $height; lines = $lines }
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$payload | ConvertTo-Json -Depth 5 -Compress

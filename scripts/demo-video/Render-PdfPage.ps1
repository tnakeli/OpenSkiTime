# Renders one page of a PDF to PNG with the Windows PDF engine (Windows.Data.Pdf), for reviewing generated reports and
# for showing them in the demonstration video. Usage: Render-PdfPage.ps1 -Pdf <file.pdf> -Png <file.png> [-Page 0] [-Width 1240]
param(
    [Parameter(Mandatory = $true)][string]$Pdf,
    [Parameter(Mandatory = $true)][string]$Png,
    [int]$Page = 0,
    [int]$Width = 1240
)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Data.Pdf.PdfDocument, Windows.Data.Pdf, ContentType = WindowsRuntime]
$null = [Windows.Storage.Streams.InMemoryRandomAccessStream, Windows.Storage.Streams, ContentType = WindowsRuntime]
$asTask = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' } | Select-Object -First 1
$asAction = [System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {
    $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction' } | Select-Object -First 1
function Await($operation, [Type]$type) { $task = $asTask.MakeGenericMethod($type).Invoke($null, @($operation)); $task.Wait(); $task.Result }
function AwaitAction($action) { $asAction.Invoke($null, @($action)).Wait() }
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync((Resolve-Path $Pdf).Path)) ([Windows.Storage.StorageFile])
$document = Await ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
$pdfPage = $document.GetPage($Page)
$options = New-Object Windows.Data.Pdf.PdfPageRenderOptions
$options.DestinationWidth = $Width
$options.DestinationHeight = [uint32]([math]::Round($Width * $pdfPage.Size.Height / $pdfPage.Size.Width))
$stream = New-Object Windows.Storage.Streams.InMemoryRandomAccessStream
AwaitAction ($pdfPage.RenderToStreamAsync($stream, $options))
$netStream = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($stream.GetInputStreamAt(0))
$output = [System.IO.File]::Create($Png)
try { $netStream.CopyTo($output) } finally { $output.Dispose(); $netStream.Dispose() }
"$Png ($($document.PageCount) page(s))"

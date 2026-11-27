param([string]$Path)
$ErrorActionPreference='Stop'
[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null=[Windows.Storage.StorageFile,Windows.Storage,ContentType=WindowsRuntime]
$null=[Windows.Graphics.Imaging.BitmapDecoder,Windows.Graphics.Imaging,ContentType=WindowsRuntime]
$null=[Windows.Media.Ocr.OcrEngine,Windows.Foundation,ContentType=WindowsRuntime]
$null=[Windows.Data.Pdf.PdfDocument,Windows.Data.Pdf,ContentType=WindowsRuntime]
$methods=[System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object {$_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1}
function Resolve-Operation($op,$type){
 $m=$methods | Where-Object {$_.IsGenericMethod -and $_.GetGenericArguments().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1'} | Select-Object -First 1
 $task=$m.MakeGenericMethod($type).Invoke($null,@($op))
 $task.GetAwaiter().GetResult()
}
function Read-Image($stream){
 $decoder=Resolve-Operation ([Windows.Graphics.Imaging.BitmapDecoder]::CreateAsync($stream)) ([Windows.Graphics.Imaging.BitmapDecoder])
 $transform=[Windows.Graphics.Imaging.BitmapTransform,Windows.Graphics.Imaging,ContentType=WindowsRuntime]::new()
 $scale=[Math]::Min(1,[Windows.Media.Ocr.OcrEngine]::MaxImageDimension/[Math]::Max($decoder.PixelWidth,$decoder.PixelHeight))
 $transform.ScaledWidth=[uint32][Math]::Max(1,$decoder.PixelWidth*$scale)
 $transform.ScaledHeight=[uint32][Math]::Max(1,$decoder.PixelHeight*$scale)
 $bitmap=Resolve-Operation ($decoder.GetSoftwareBitmapAsync('Bgra8','Premultiplied',$transform,'RespectExifOrientation','DoNotColorManage')) ([Windows.Graphics.Imaging.SoftwareBitmap,Windows.Graphics.Imaging,ContentType=WindowsRuntime])
 try {
 $result=Resolve-Operation ($ocr.RecognizeAsync($bitmap)) ([Windows.Media.Ocr.OcrResult,Windows.Foundation,ContentType=WindowsRuntime])
 $result.Lines | ForEach-Object {$_.Text}
 } finally {$bitmap.Dispose()}
}
$ocr=[Windows.Media.Ocr.OcrEngine]::TryCreateFromUserProfileLanguages()
if (!$ocr){throw 'Windows-OCR-Sprachpaket erforderlich.'}
$file=Resolve-Operation ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Path)) ([Windows.Storage.StorageFile])
if($file.FileType -eq '.pdf'){
 $pdf=Resolve-Operation ([Windows.Data.Pdf.PdfDocument]::LoadFromFileAsync($file)) ([Windows.Data.Pdf.PdfDocument])
 for($i=0;$i -lt $pdf.PageCount;$i++){
 $page=$pdf.GetPage($i)
 $stream=[Windows.Storage.Streams.InMemoryRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime]::new()
 try {
 $options=[Windows.Data.Pdf.PdfPageRenderOptions,Windows.Data.Pdf,ContentType=WindowsRuntime]::new()
 $options.DestinationWidth=1800
 $action=$page.RenderToStreamAsync($stream,$options)
 $m=$methods | Where-Object {$_.GetParameters()[0].ParameterType.Name -eq 'IAsyncAction'} | Select-Object -First 1
 $m.Invoke($null,@($action)).GetAwaiter().GetResult()
 $stream.Seek(0)
 Read-Image $stream
 ''
 } finally {$stream.Dispose();$page.Dispose()}
 }
}else{
 $stream=Resolve-Operation ($file.OpenAsync('Read')) ([Windows.Storage.Streams.IRandomAccessStream,Windows.Storage.Streams,ContentType=WindowsRuntime])
 try {Read-Image $stream} finally {$stream.Dispose()}
}

param([string]$Video, [string]$OutDir, [double]$Step = 5, [int]$Width = 960, [double]$From = 0, [double]$To = -1)
Add-Type -AssemblyName System.Runtime.WindowsRuntime
$null = [Windows.Storage.StorageFile, Windows.Storage, ContentType = WindowsRuntime]
$null = [Windows.Media.Editing.MediaComposition, Windows.Media.Editing, ContentType = WindowsRuntime]
$null = [Windows.Media.Editing.MediaClip, Windows.Media.Editing, ContentType = WindowsRuntime]
$asTaskGeneric = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
function Await($op, [Type]$t) { $m = $asTaskGeneric.MakeGenericMethod($t); $task = $m.Invoke($null, @($op)); $task.Wait(-1) | Out-Null; $task.Result }
New-Item -ItemType Directory -Force $OutDir | Out-Null
$file = Await ([Windows.Storage.StorageFile]::GetFileFromPathAsync($Video)) ([Windows.Storage.StorageFile])
$clip = Await ([Windows.Media.Editing.MediaClip]::CreateFromFileAsync($file)) ([Windows.Media.Editing.MediaClip])
$comp = New-Object Windows.Media.Editing.MediaComposition
[System.Collections.Generic.ICollection[Windows.Media.Editing.MediaClip]].GetMethod("Add").Invoke($comp.Clips, @($clip)) | Out-Null
$dur = $clip.OriginalDuration.TotalSeconds
if ($To -lt 0 -or $To -gt $dur) { $To = $dur - 0.1 }
"duration $dur"
$h = [int]($Width * 9 / 16)
for ($t = $From; $t -le $To; $t += $Step) {
  $stream = Await ($comp.GetThumbnailAsync([TimeSpan]::FromSeconds($t), $Width, $h, [Windows.Media.Editing.VideoFramePrecision]::NearestFrame)) ([Windows.Graphics.Imaging.ImageStream])
  $net = [System.IO.WindowsRuntimeStreamExtensions]::AsStreamForRead($stream)
  $path = Join-Path $OutDir ("f_{0:0000.0}.jpg" -f $t)
  $fs = [System.IO.File]::Create($path); $net.CopyTo($fs); $fs.Close(); $net.Close()
}
"done"

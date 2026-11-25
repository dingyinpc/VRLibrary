# 2026-11-08: 从 Unity 产出的 APK 里剔除会崩溃的 libPxrPlatform.so,保留 Java 类,然后重签。
# 用法: powershell -File Tools/apk-strip-pxr.ps1
param(
    [string]$In  = "E:\Unity xianmu\VRlibrary\Builds\VRlibrary.apk",
    [string]$Out = "E:\Unity xianmu\VRlibrary\Builds\VRlibrary-pxrstripped.apk"
)
$ErrorActionPreference = "Stop"

$sdk = "E:\unity\2022.3.42f1c1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK"
$java = "E:\unity\2022.3.42f1c1\Editor\Data\PlaybackEngines\AndroidPlayer\OpenJDK"
$zipalign  = Join-Path $sdk "build-tools\34.0.0\zipalign.exe"
$apksigner = Join-Path $sdk "build-tools\34.0.0\apksigner.bat"
$ks = Join-Path $env:USERPROFILE ".android\debug.keystore"

$tmp = [IO.Path]::GetTempFileName() + ".apk"
Copy-Item $In $tmp -Force

Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::Open($tmp, 'Update')
try {
    $hit = @($zip.Entries | Where-Object { $_.FullName -eq "lib/arm64-v8a/libPxrPlatform.so" })
    foreach ($e in $hit) { $e.Delete() }
    Write-Output ("剔除条目数 = " + $hit.Count)
} finally { $zip.Dispose() }

if (Test-Path $Out) { Remove-Item $Out -Force }
& $zipalign -f -p 4 $tmp $Out | Out-Null
if ($LASTEXITCODE -ne 0) { throw "zipalign 失败" }

$env:JAVA_HOME = $java
& $apksigner sign --ks $ks --ks-pass pass:android --key-pass pass:android --ks-key-alias androiddebugkey $Out 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "apksigner 失败" }

& $apksigner verify --print-certs $Out 2>&1 | Select-Object -First 2 | ForEach-Object { $_.ToString() }
Remove-Item $tmp -Force -ErrorAction SilentlyContinue

Add-Type -AssemblyName System.IO.Compression.FileSystem
$z2 = [IO.Compression.ZipFile]::OpenRead($Out)
try {
    $left = @($z2.Entries | Where-Object { $_.FullName -match "libPxrPlatform\.so" }).Count
    Write-Output ("成品内 libPxrPlatform.so = " + $left + " (应为 0)")
    Write-Output ("成品大小 = " + [math]::Round((Get-Item $Out).Length/1MB,1) + " MB")
} finally { $z2.Dispose() }

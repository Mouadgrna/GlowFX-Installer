# Builds installer\GlowFXInstaller.exe with the C# compiler that ships with Windows (.NET Framework 4.8).
# Nothing else needs to be installed. Run:  powershell -ExecutionPolicy Bypass -File app\build.ps1
$ErrorActionPreference = 'Stop'
$fw  = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
$app = $PSScriptRoot
$out = Join-Path (Split-Path $app -Parent) 'installer'
New-Item -ItemType Directory -Force $out | Out-Null

& "$fw\csc.exe" /nologo /target:winexe /optimize+ /warn:4 `
  "/out:$out\GlowFXInstaller.exe" `
  "/win32icon:$app\app.ico" `
  "/resource:$app\MainWindow.xaml,MainWindow.xaml" `
  "/resource:$app\app.ico,app.ico" `
  "/r:$wpf\PresentationFramework.dll" "/r:$wpf\PresentationCore.dll" "/r:$wpf\WindowsBase.dll" `
  "/r:$fw\System.Xaml.dll" "/r:$fw\System.Net.Http.dll" "/r:$fw\System.Web.Extensions.dll" `
  /r:System.dll /r:System.Core.dll `
  "$app\GlowFXInstaller.cs"
if ($LASTEXITCODE -ne 0) { throw "Build failed" }
Write-Host "Built $out\GlowFXInstaller.exe"

# Rebuilds EditReady.exe with the C# compiler that ships with Windows (.NET Framework 4.8). No installs needed.
#   .\build.ps1                      -> EditReady.exe next to this script
#   .\build.ps1 -Out dist\EditReady.exe
param([string]$Out = "EditReady.exe")
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not (Test-Path "$here\EditReady.ico")) { & "$here\gen_icon.ps1" }
$fw  = "C:\Windows\Microsoft.NET\Framework64\v4.0.30319"
$wpf = "$fw\WPF"
Push-Location $here
try {
    $dir = Split-Path -Parent $Out
    if ($dir -and -not (Test-Path $dir)) { New-Item -ItemType Directory -Force $dir | Out-Null }
    & "$fw\csc.exe" /nologo /target:winexe /optimize+ /codepage:65001 `
        "/out:$Out" /win32icon:EditReady.ico `
        "/resource:MainWindow.xaml,MainWindow.xaml" "/resource:EditReady.ico,EditReady.ico" `
        "/r:$wpf\PresentationFramework.dll" "/r:$wpf\PresentationCore.dll" "/r:$wpf\WindowsBase.dll" "/r:$fw\System.Xaml.dll" `
        EditReady.cs
    if ($LASTEXITCODE -eq 0) { Write-Host "Built $Out" }
} finally { Pop-Location }

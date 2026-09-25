# Builds Study Stash for Windows: the app, a zip of it for the installer and auto-update, and the two
# installers for people who download it by hand: Study-Stash-Laptop-Setup.exe and
# Study-Stash-Library-Setup.exe (the same app; the library's copy is marked as such).
#   powershell -File windows\build.ps1 [-Version 0.4.0] [-Out dist]
# Needs the .NET SDK, and Inno Setup 6 for the Setup.exe (skipped, with a note, when it's missing).
# Neither is signed: a downloaded Setup.exe gets Windows' SmartScreen question once (More info, then Run
# anyway). The installer fetches the zip with its own code instead, which Windows doesn't flag.
param([string]$Version = "", [string]$Out = "dist")
$ErrorActionPreference = "Stop"
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Version) {
  $Version = (Select-String -Path (Join-Path $Here "..\granola_share\__init__.py") -Pattern '__version__ = "(.*)"').Matches[0].Groups[1].Value
}
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$App = Join-Path $Out "Study Stash"
Remove-Item -Recurse -Force $App -ErrorAction SilentlyContinue

dotnet publish (Join-Path $Here "StudyStash.csproj") -c Release -o $App "-p:Version=$Version" --nologo
if ($LASTEXITCODE -ne 0) { throw "the build failed" }
Remove-Item (Join-Path $App "Microsoft.Web.WebView2.Wpf.dll") -ErrorAction SilentlyContinue  # a WPF piece it doesn't use

$Zip = Join-Path $Out "Study-Stash-windows.zip"
Remove-Item $Zip -ErrorAction SilentlyContinue
Add-Type -AssemblyName System.IO.Compression.FileSystem  # forward slashes in the zip, unlike Compress-Archive on 5.1
[System.IO.Compression.ZipFile]::CreateFromDirectory($App, $Zip)

$Iscc = @((Get-Command iscc -ErrorAction SilentlyContinue).Source,
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($Iscc) {
  foreach ($Role in "laptop", "library") {
    & $Iscc "/Qp" "/DAppVersion=$Version" "/DSource=$App" "/DRole=$Role" "/O$Out" (Join-Path $Here "setup.iss")
    if ($LASTEXITCODE -ne 0) { throw "the $Role installer didn't build" }
  }
} else {
  Write-Host "Inno Setup isn't installed, so there are no Setup.exe installers (choco install innosetup)."
}
Write-Host "Built ${Version}:"
Get-ChildItem $Out -File | Format-Table Name, Length -AutoSize

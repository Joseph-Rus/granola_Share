# Builds Study Stash for Windows: a self-contained publish for x64 and arm64 (so it needs no .NET install),
# pruned to the runtime each processor needs, and, when Inno Setup 6 is installed, both roles' Setup.exe
# (D2): Study-Stash-Laptop-Setup.exe and Study-Stash-Library-Setup.exe, the same app with a different
# study-stash.ini role. One AppId, so installing the other role's Setup.exe over an existing install just
# rewrites the ini.
#   powershell -File windows\build.ps1 [-Out dist\windows]
# Needs the .NET SDK, and Inno Setup 6 for the installers (skipped, with a note, when it's missing:
# choco install innosetup). Neither is signed, so a downloaded Setup.exe gets Windows' SmartScreen
# question once (More info, then Run anyway).
param([string]$Out = "dist\windows")
$ErrorActionPreference = "Stop"
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path

$Version = (Select-String -Path (Join-Path $Here "..\engine\Directory.Build.props") -Pattern '<StudyStashVersion>(.*)</StudyStashVersion>').Matches[0].Groups[1].Value
if (-not $Version) { throw "engine\Directory.Build.props has no StudyStashVersion" }

New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path

# D6: the Whisper model is never bundled.
function Test-NoModel([string]$Dir) {
  $Bad = Get-ChildItem $Dir -Recurse -File -Filter "*.bin" -ErrorAction SilentlyContinue | Where-Object { $_.Length -gt 1MB }
  if ($Bad) { throw "the published app has a bundled model: $($Bad.FullName -join ', ')" }
}

# Whisper.net.Runtime ships every platform's natives under runtimes\; a processor only needs its own tree
# (plus, for x64, the Vulkan build under runtimes\vulkan\win-x64 - arm64 has no Vulkan whisper build).
function Remove-UnneededRuntimes([string]$Dir, [string]$Rid) {
  $Runtimes = Join-Path $Dir "runtimes"
  if (-not (Test-Path $Runtimes)) { return }
  Get-ChildItem $Runtimes -Directory | Where-Object { $_.Name -notin @($Rid, "vulkan") } | Remove-Item -Recurse -Force
  $Vulkan = Join-Path $Runtimes "vulkan"
  if (Test-Path $Vulkan) {
    if ($Rid -eq "win-x64") {
      Get-ChildItem $Vulkan -Directory | Where-Object { $_.Name -ne "win-x64" } | Remove-Item -Recurse -Force
    } else {
      Remove-Item -Recurse -Force $Vulkan
    }
  }
}

foreach ($Rid in "win-x64", "win-arm64") {
  $Dest = Join-Path $Out $Rid
  Remove-Item -Recurse -Force $Dest -ErrorAction SilentlyContinue
  dotnet publish (Join-Path $Here "..\engine\src\StudyStash.App") -c Release -r $Rid --self-contained true `
    -p:DebugType=None -o $Dest --nologo -v quiet
  if ($LASTEXITCODE -ne 0) { throw "publishing $Rid failed" }
  Remove-UnneededRuntimes $Dest $Rid
  Test-NoModel $Dest
}

$Iscc = @((Get-Command iscc -ErrorAction SilentlyContinue).Source,
          "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
if ($Iscc) {
  foreach ($Role in "laptop", "library") {
    & $Iscc "/Qp" "/DAppVersion=$Version" "/DSource=$Out" "/DRole=$Role" "/O$Out" (Join-Path $Here "setup.iss")
    if ($LASTEXITCODE -ne 0) { throw "the $Role installer didn't build" }
  }
} else {
  Write-Host "Inno Setup isn't installed, so there are no Setup.exe installers (choco install innosetup)."
}
Write-Host "Built ${Version}:"
Get-ChildItem $Out -File | Format-Table Name, Length -AutoSize

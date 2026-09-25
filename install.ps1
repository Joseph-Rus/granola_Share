# granola-share one-line installer (Windows PowerShell).
#   The computer that keeps the library:
#     $env:GRANOLA_SHARE_ROLE='server'; irm https://raw.githubusercontent.com/Joseph-Rus/study-stash/main/install.ps1 | iex
#   Your laptop (the library's setup prints this line with the address and password filled in):
#     $env:GRANOLA_SHARE_SERVER='http://mac-mini:8787'; $env:GRANOLA_SHARE_KEY='pw'; irm .../install.ps1 | iex
#
# Installs granola-share from the newest release as one ready-made folder with its own Python (the
# python.org build, with every package it needs) in AppData\Local\Programs\granola-share, plus the
# `granola-share` command. No admin rights, and nothing else to install: no Python, no uv (whose
# Python install fails under OneDrive's Files On-Demand). Safe to rerun: it updates in place, and
# setup keeps your answers.
#   GRANOLA_SHARE_VERSION=v0.4.2   the release to install (default: the newest)
#   GRANOLA_SHARE_BUNDLE=<zip>     install this Study-Stash-helper-windows.zip instead (a path or URL)
#   GRANOLA_SHARE_NO_SETUP, GRANOLA_SHARE_HOME, GRANOLA_SHARE_TERMINAL: as in install.sh
& {
  $ErrorActionPreference = "Stop"
  $ProgressPreference = "SilentlyContinue"  # Windows PowerShell's progress bar slows downloads to a crawl
  $Role = if ($env:GRANOLA_SHARE_ROLE) { $env:GRANOLA_SHARE_ROLE } else { "client" }
  $Slug = "Joseph-Rus/study-stash"
  $HomeDir = if ($env:GRANOLA_SHARE_HOME) { $env:GRANOLA_SHARE_HOME } else { Join-Path $env:USERPROFILE ".granola-share" }
  $Dir = Join-Path $env:LOCALAPPDATA "Programs\granola-share"
  $Bin = Join-Path $env:USERPROFILE ".local\bin"
  New-Item -ItemType Directory -Force -Path $HomeDir, $Bin | Out-Null

  function Fail($Message) {
    Write-Host ""
    Write-Host "granola-share install failed: $Message" -ForegroundColor Red
    Write-Host "Rerunning the same command is safe. Help: https://github.com/$Slug#troubleshooting"
    throw $Message
  }

  Write-Host "== granola-share installer ($Role) =="

  # 1. Which release.
  $Bundle = $env:GRANOLA_SHARE_BUNDLE
  $What = $Bundle
  if (-not $Bundle) {
    $Ref = $env:GRANOLA_SHARE_VERSION
    if (-not $Ref -or $Ref -eq "main") {
      try { $Ref = (Invoke-RestMethod "https://api.github.com/repos/$Slug/releases/latest" -TimeoutSec 20).tag_name }
      catch { Fail "couldn't reach GitHub to find the newest release ($($_.Exception.Message))" }
    }
    $Bundle = "https://github.com/$Slug/releases/download/$Ref/Study-Stash-helper-windows.zip"
    $What = $Ref
  }

  # 2. Download it.
  Write-Host "Downloading granola-share ($What)..."
  $Zip = Join-Path $env:TEMP "study-stash-helper.zip"
  try {
    if ($Bundle -match "^https?://") { Invoke-WebRequest $Bundle -OutFile $Zip -UseBasicParsing }
    else { Copy-Item $Bundle $Zip -Force }
  } catch {
    if ("$($_.Exception.Message)" -match "404") { Fail "$What has no Windows download yet. Try again in a few minutes." }
    Fail "couldn't download $Bundle ($($_.Exception.Message))"
  }

  # 3. Windows can't replace files in use, so stop every running copy: the background services, and any
  # granola-share command still open. Only Python and granola-share.exe, never a window or shell that
  # merely mentions the name.
  $Ours = { ($_.Name -like "python*" -or $_.Name -eq "granola-share.exe") -and $_.ProcessId -ne $PID -and
            ($_.CommandLine -like "*granola_share*" -or $_.CommandLine -like "*granola-share*") }
  $Running = @(Get-CimInstance Win32_Process | Where-Object -FilterScript $Ours)
  $Running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  if ($Running.Count) { Start-Sleep -Seconds 2 }

  # 4. Unpack beside the old copy, then swap them.
  Write-Host "Installing it in $Dir..."
  $New = "$Dir.new"
  $Old = "$Dir.old"
  Remove-Item -Recurse -Force $New, $Old -ErrorAction SilentlyContinue
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  [System.IO.Compression.ZipFile]::ExtractToDirectory($Zip, $New)
  Remove-Item $Zip -ErrorAction SilentlyContinue
  if (-not (Test-Path "$New\python\python.exe")) { Fail "the download didn't unpack (it has no python\python.exe)" }
  if (Test-Path $Dir) {
    try { Rename-Item $Dir (Split-Path $Old -Leaf) }
    catch {
      Remove-Item -Recurse -Force $New -ErrorAction SilentlyContinue
      Fail "a file in $Dir is still in use. Restart this PC, then run the same line again."
    }
  }
  Rename-Item $New (Split-Path $Dir -Leaf)
  Remove-Item -Recurse -Force $Old -ErrorAction SilentlyContinue
  $Py = Join-Path $Dir "python\python.exe"

  # 5. The granola-share command: granola-share.cmd for PowerShell and cmd, and a script for Git Bash.
  #    0.4.1 and before used uv: its granola-share.exe would win over the .cmd, so it goes.
  Remove-Item (Join-Path $Bin "granola-share.exe") -Force -ErrorAction SilentlyContinue
  foreach ($Tools in (Join-Path $env:APPDATA "uv\tools\granola-share"), (Join-Path $env:LOCALAPPDATA "uv\tools\granola-share")) {
    Remove-Item -Recurse -Force $Tools -ErrorAction SilentlyContinue
  }
  Set-Content -Path (Join-Path $Bin "granola-share.cmd") -Value "@`"$Py`" -m granola_share.cli %*" -Encoding ASCII
  Set-Content -Path (Join-Path $Bin "granola-share") -Encoding ASCII -NoNewline `
    -Value ("#!/bin/sh`nexec `"" + ($Py -replace "\\", "/") + "`" -m granola_share.cli `"`$@`"`n")
  $UserPath = [Environment]::GetEnvironmentVariable("Path", "User")
  if (-not (($UserPath -split ";") -contains $Bin)) {
    [Environment]::SetEnvironmentVariable("Path", $(if ($UserPath) { "$Bin;$UserPath" } else { $Bin }), "User")
    Write-Host "The granola-share command works in new PowerShell windows."
  }
  $Version = & $Py -m granola_share.cli --version
  if ($LASTEXITCODE -ne 0) { Fail "it installed, but doesn't start: $Py" }
  Write-Host "Installed $Version."

  # 6. Background services that were set up come back, running this copy.
  $Startup = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup"
  foreach ($Cmd in @(Get-ChildItem $Startup -Filter "granola-share-*.cmd" -ErrorAction SilentlyContinue)) {
    & $Py -m granola_share.cli --home $HomeDir autostart install --role ($Cmd.BaseName -replace "^granola-share-", "") | Out-Null
  }

  # 7. Setup.
  if ($env:GRANOLA_SHARE_NO_SETUP -eq "1") {
    Write-Host "Skipping setup. Next: granola-share setup (the library) or granola-share client open (your laptop)."
    return
  }
  Write-Host ""
  if ($Role -ne "server" -and -not $env:GRANOLA_SHARE_TERMINAL) {
    # The laptop sets up in the Study Stash app: this starts the background service, adds the app and its
    # Start Menu entry, and opens its setup page with the library's address and password filled in.
    & $Py -m granola_share.cli --home $HomeDir client open --install
    if ($LASTEXITCODE -ne 0) { Fail "Study Stash didn't start (log: $HomeDir\logs\client.log)" }
    Write-Host "Setup continues in the Study Stash window. Later, open Study Stash from the Start Menu."
    return
  }
  if ($Role -eq "server") { & $Py -m granola_share.cli --home $HomeDir setup }
  else { & $Py -m granola_share.cli --home $HomeDir client setup }
}

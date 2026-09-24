# granola-share one-line installer (Windows PowerShell).
#   The computer that keeps the library:
#     $env:GRANOLA_SHARE_ROLE='server'; irm https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.ps1 | iex
#   Your laptop (the library's setup prints this line with the address and password filled in):
#     $env:GRANOLA_SHARE_SERVER='http://mac-mini:8787'; $env:GRANOLA_SHARE_KEY='pw'; irm .../install.ps1 | iex
#
# Installs the `granola-share` command with uv (no admin rights, no git, its own Python),
# from the newest release. Safe to rerun: it updates in place and setup keeps your answers.
# Same variables as install.sh: GRANOLA_SHARE_VERSION, GRANOLA_SHARE_SRC, GRANOLA_SHARE_NO_SETUP, GRANOLA_SHARE_HOME,
# GRANOLA_SHARE_TERMINAL (laptop: answer setup in the terminal instead of the browser).
& {
  $ErrorActionPreference = "Stop"
  $Role = if ($env:GRANOLA_SHARE_ROLE) { $env:GRANOLA_SHARE_ROLE } else { "client" }
  $Slug = "Joseph-Rus/granola_Share"
  $HomeDir = if ($env:GRANOLA_SHARE_HOME) { $env:GRANOLA_SHARE_HOME } else { Join-Path $env:USERPROFILE ".granola-share" }
  $Log = Join-Path $HomeDir "install.log"
  New-Item -ItemType Directory -Force -Path $HomeDir | Out-Null

  function Fail($Message) {
    Write-Host ""
    Write-Host "granola-share install failed: $Message" -ForegroundColor Red
    Write-Host "Rerunning the same command is safe. Help: https://github.com/$Slug#troubleshooting"
    throw $Message
  }
  # Windows PowerShell 5.1 turns a native tool's progress on stderr into a terminating error under "Stop".
  function Invoke-Quiet([scriptblock]$Block) {
    $saved = $ErrorActionPreference
    $ErrorActionPreference = "Continue"
    try { & $Block } finally { $ErrorActionPreference = $saved }
  }

  Write-Host "== granola-share installer ($Role) =="

  # 1. uv: installs Python and the app into your user folder.
  $OrigPath = $env:Path
  $env:Path = "$env:USERPROFILE\.local\bin;$env:Path"
  if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
    Write-Host "Installing uv (Python manager)..."
    Invoke-Quiet { powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://astral.sh/uv/install.ps1 | iex" *> $Log }
    $env:Path = "$env:USERPROFILE\.local\bin;$env:Path"
    if (-not (Get-Command uv -ErrorAction SilentlyContinue)) { Fail "could not install uv (log: $Log)" }
  }

  # 2. Which version.
  if ($env:GRANOLA_SHARE_SRC) {
    $Src = $env:GRANOLA_SHARE_SRC
    $What = "local copy at $Src"
  } else {
    $Ref = $env:GRANOLA_SHARE_VERSION
    if (-not $Ref) {
      try { $Ref = (Invoke-RestMethod "https://api.github.com/repos/$Slug/releases/latest" -TimeoutSec 20).tag_name } catch { $Ref = "" }
    }
    if (-not $Ref -or $Ref -eq "main") {
      $Src = "granola-share @ https://github.com/$Slug/archive/refs/heads/main.tar.gz"; $What = "latest main"
    } else {
      $Src = "granola-share @ https://github.com/$Slug/archive/refs/tags/$Ref.tar.gz"; $What = $Ref
    }
  }

  # 3. The command itself. Windows can't replace files in use, so stop a running copy first.
  $Running = @(Get-CimInstance Win32_Process | Where-Object { $_.CommandLine -like "*granola_share.cli*" })
  $Running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  if ($Running.Count) { Start-Sleep -Seconds 2 }
  Write-Host "Installing granola-share ($What)..."
  $env:UV_PYTHON_PREFERENCE = "only-managed"
  Invoke-Quiet { uv tool install --force --python 3.12 --reinstall-package granola-share --refresh-package granola-share $Src *> $Log }
  $Code = $LASTEXITCODE
  Remove-Item Env:UV_PYTHON_PREFERENCE -ErrorAction SilentlyContinue
  if ($Code -ne 0) { Get-Content $Log -Tail 20; Fail "uv could not install granola-share (full log: $Log)" }
  $BinDir = (Invoke-Quiet { uv tool dir --bin } | Select-Object -First 1)
  if (-not $BinDir) { $BinDir = "$env:USERPROFILE\.local\bin" }
  $Exe = Join-Path $BinDir.Trim() "granola-share.exe"
  if (-not (Test-Path $Exe)) { Fail "installed, but $Exe is missing (log: $Log)" }
  Write-Host "Installed $(& $Exe --version)."
  if (-not (($OrigPath -split ";") -contains $BinDir.Trim())) {
    Invoke-Quiet { uv tool update-shell *> $null }  # adds it to PATH for new windows
    Write-Host "The granola-share command works in new PowerShell windows."
  }

  # 4. Setup.
  $Startup = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs\Startup"
  if ($env:GRANOLA_SHARE_NO_SETUP -eq "1") {
    # put back whatever was running before the update
    Get-ChildItem $Startup -Filter "granola-share-*.cmd" -ErrorAction SilentlyContinue | ForEach-Object { cmd /c $_.FullName }
    Write-Host "Skipping setup. Next: granola-share setup (the library) or granola-share client open (your laptop)."
    return
  }
  Write-Host ""
  if ($Role -ne "server" -and -not $env:GRANOLA_SHARE_TERMINAL) {
    # The laptop sets up in the browser: start the background service, add the Start Menu entry, open setup.
    & $Exe --home $HomeDir client open --install
    if ($LASTEXITCODE -ne 0) { Fail "Granola Share didn't start (log: $HomeDir\logs\client.log)" }
    Write-Host "Setup continues in your browser. Later, open Granola Share from the Start Menu."
    return
  }
  if ($Role -eq "server") { & $Exe --home $HomeDir setup } else { & $Exe --home $HomeDir client setup }
}

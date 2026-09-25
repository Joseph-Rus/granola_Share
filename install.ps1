# granola-share one-line installer (Windows PowerShell).
#   The computer that keeps the library:
#     $env:GRANOLA_SHARE_ROLE='server'; irm https://raw.githubusercontent.com/Joseph-Rus/study-stash/main/install.ps1 | iex
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
  $Slug = "Joseph-Rus/study-stash"
  $HomeDir = if ($env:GRANOLA_SHARE_HOME) { $env:GRANOLA_SHARE_HOME } else { Join-Path $env:USERPROFILE ".granola-share" }
  $Log = Join-Path $HomeDir "install.log"
  New-Item -ItemType Directory -Force -Path $HomeDir | Out-Null

  function Fail($Message) {
    Write-Host ""
    Write-Host "granola-share install failed: $Message" -ForegroundColor Red
    Write-Host "Rerunning the same command is safe. Help: https://github.com/$Slug#troubleshooting"
    throw $Message
  }
  # Runs a program with everything it prints going to the log. (Windows PowerShell shows whatever a
  # program writes to stderr as red errors, even uv's "Downloading..." progress.)
  function Invoke-Logged([string]$File, [string]$Arguments) {
    $psi = New-Object System.Diagnostics.ProcessStartInfo
    $psi.FileName = $File
    $psi.Arguments = $Arguments
    $psi.UseShellExecute = $false
    $psi.CreateNoWindow = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError = $true
    $p = [System.Diagnostics.Process]::Start($psi)
    $err = $p.StandardError.ReadToEndAsync()
    $out = $p.StandardOutput.ReadToEnd()
    $p.WaitForExit()
    Set-Content -Path $Log -Value ($out + $err.Result) -Encoding UTF8
    return $p.ExitCode
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

  # 3. The command itself. Windows can't replace files in use, so stop every running copy first: the
  # background services, and any granola-share command still open (say, one an older version left
  # stuck). Only Python and granola-share.exe itself, never a window or shell that mentions the name.
  $Ours = { ($_.Name -like "python*" -or $_.Name -eq "granola-share.exe") -and $_.ProcessId -ne $PID -and
            ($_.CommandLine -like "*granola_share*" -or $_.CommandLine -like "*granola-share*") }
  $Running = @(Get-CimInstance Win32_Process | Where-Object -FilterScript $Ours)
  $Running | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
  if ($Running.Count) { Start-Sleep -Seconds 2 }
  Write-Host "Installing granola-share ($What)..."
  # uv keeps its Python and tools in AppData\Local, which OneDrive never syncs: in AppData\Roaming (its
  # default), OneDrive's Files On-Demand blocks the link uv makes to Python ("untrusted mount point",
  # os error 448). A copy installed in Roaming before stays there, since its services point at it.
  $UvEnv = @{ UV_PYTHON_PREFERENCE = "only-managed" }
  if (-not (Test-Path (Join-Path $env:APPDATA "uv\tools\granola-share"))) {
    if (-not $env:UV_PYTHON_INSTALL_DIR) { $UvEnv.UV_PYTHON_INSTALL_DIR = Join-Path $env:LOCALAPPDATA "uv\python" }
    if (-not $env:UV_TOOL_DIR) { $UvEnv.UV_TOOL_DIR = Join-Path $env:LOCALAPPDATA "uv\tools" }
  }
  $UvEnv.GetEnumerator() | ForEach-Object { Set-Item "Env:$($_.Key)" $_.Value }
  $Code = Invoke-Logged (Get-Command uv).Source "tool install --force --python 3.12 --reinstall-package granola-share --refresh-package granola-share `"$Src`""
  $UvEnv.Keys | ForEach-Object { Remove-Item "Env:$_" -ErrorAction SilentlyContinue }
  if ($Code -ne 0) {
    Get-Content $Log -Tail 20
    if (Select-String -Path $Log -Pattern "os error 448|untrusted mount point" -Quiet) {
      Fail "Windows blocked a link uv makes (this happens with OneDrive's Files On-Demand). Set the user environment variables UV_PYTHON_INSTALL_DIR and UV_TOOL_DIR to folders outside OneDrive, then run the same line again (log: $Log)"
    }
    if (Select-String -Path $Log -Pattern "os error (5|32)|Access is denied|used by another process" -Quiet) {
      Fail "a file it needs is still in use. Restart this PC, then run the same line again (log: $Log)"
    }
    Fail "uv could not install granola-share (full log: $Log)"
  }
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
    # (Start-Process, not `cmd /c`: the service would hold on to the output PowerShell waits on.)
    Get-ChildItem $Startup -Filter "granola-share-*.cmd" -ErrorAction SilentlyContinue |
      ForEach-Object { Start-Process -FilePath $_.FullName -WindowStyle Hidden }
    Write-Host "Skipping setup. Next: granola-share setup (the library) or granola-share client open (your laptop)."
    return
  }
  Write-Host ""
  if ($Role -ne "server" -and -not $env:GRANOLA_SHARE_TERMINAL) {
    # The laptop sets up in the Study Stash app: this starts the background service, adds the app and its
    # Start Menu entry, and opens its setup page with the library's address and password filled in.
    & $Exe --home $HomeDir client open --install
    if ($LASTEXITCODE -ne 0) { Fail "Study Stash didn't start (log: $HomeDir\logs\client.log)" }
    Write-Host "Setup continues in the Study Stash window. Later, open Study Stash from the Start Menu."
    return
  }
  if ($Role -eq "server") { & $Exe --home $HomeDir setup } else { & $Exe --home $HomeDir client setup }
}

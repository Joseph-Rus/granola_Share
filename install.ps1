# granola-share one-line installer (Windows PowerShell).
#   Friend's laptop:  irm https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.ps1 | iex
#   Pool server:      $env:GRANOLA_SHARE_ROLE="server"; irm https://raw.githubusercontent.com/Joseph-Rus/granola_Share/main/install.ps1 | iex
$ErrorActionPreference = "Stop"
$Role = if ($env:GRANOLA_SHARE_ROLE) { $env:GRANOLA_SHARE_ROLE } else { "client" }
$Repo = if ($env:GRANOLA_SHARE_REPO) { $env:GRANOLA_SHARE_REPO } else { "https://github.com/Joseph-Rus/granola_Share" }
$HomeDir = if ($env:GRANOLA_SHARE_HOME) { $env:GRANOLA_SHARE_HOME } else { Join-Path $env:USERPROFILE ".granola-share" }
$App = Join-Path $HomeDir "app"
$Venv = Join-Path $HomeDir "venv"
New-Item -ItemType Directory -Force -Path $HomeDir | Out-Null

Write-Host "== granola-share installer ($Role) =="

if (-not (Get-Command uv -ErrorAction SilentlyContinue)) {
  Write-Host "Installing uv (Python manager)..."
  irm https://astral.sh/uv/install.ps1 | iex
  $env:Path = "$env:USERPROFILE\.local\bin;$env:Path"
}

if (Test-Path (Join-Path $App ".git")) {
  Write-Host "Updating app..."
  git -C $App pull --ff-only -q
} elseif (Get-Command git -ErrorAction SilentlyContinue) {
  Write-Host "Downloading app..."
  git clone -q $Repo $App
} else {
  Write-Host "Downloading app (zip)..."
  $Zip = Join-Path $HomeDir "app.zip"
  $Tmp = Join-Path $HomeDir "app-tmp"
  Remove-Item -Recurse -Force $App, $Zip, $Tmp -ErrorAction SilentlyContinue
  Invoke-WebRequest "$Repo/archive/refs/heads/main.zip" -OutFile $Zip
  Expand-Archive $Zip -DestinationPath $Tmp
  Move-Item (Get-ChildItem $Tmp | Select-Object -First 1).FullName $App
  Remove-Item -Recurse -Force $Zip, $Tmp
}

Write-Host "Setting up Python..."
uv venv --python 3.12 -q $Venv
$Py = Join-Path $Venv "Scripts\python.exe"
uv pip install -q --python $Py -e $App

Write-Host ""
$Exe = Join-Path $Venv "Scripts\granola-share.exe"
if ($Role -eq "server") { & $Exe --home $HomeDir setup } else { & $Exe --home $HomeDir client setup }

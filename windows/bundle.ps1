# Builds Study-Stash-helper-windows.zip: granola-share in one ready-made folder with its own Python
# (python.org's embeddable build, signed by the Python Software Foundation) and every package it needs.
# Windows installs this, so nothing runs uv or installs Python on your PC (uv's Python install fails
# under OneDrive's Files On-Demand).
#   powershell -File windows\bundle.ps1 [-Out dist] [-PythonVersion 3.13.15]
# Needs a Python of the same minor version on this machine (CI's setup-python) to build the packages.
param([string]$Out = "dist", [string]$PythonVersion = "3.13.15")
$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$Here = Split-Path -Parent $MyInvocation.MyCommand.Path
$Root = (Resolve-Path (Join-Path $Here "..")).Path
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$Out = (Resolve-Path $Out).Path
$Stage = Join-Path $Out "helper"
Remove-Item -Recurse -Force $Stage -ErrorAction SilentlyContinue
$Py = Join-Path $Stage "python"
New-Item -ItemType Directory -Force -Path $Py | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

# 1. Python itself.
$Embed = Join-Path $Out "python-embed.zip"
Invoke-WebRequest "https://www.python.org/ftp/python/$PythonVersion/python-$PythonVersion-embed-amd64.zip" -OutFile $Embed
[System.IO.Compression.ZipFile]::ExtractToDirectory($Embed, $Py)
Remove-Item $Embed

# 2. The embeddable build ignores site-packages until its ._pth file lists it and imports site
#    (which also runs the .pth files packages like pywin32 rely on).
$Pth = Get-ChildItem $Py -Filter "python3*._pth" | Select-Object -First 1
$StdLib = (Get-ChildItem $Py -Filter "python3*.zip" | Select-Object -First 1).Name
Set-Content -Path $Pth.FullName -Value @($StdLib, ".", "Lib\site-packages", "import site") -Encoding ASCII

# 3. granola-share and its packages, built for this Python's version (same ABI as the build machine's).
$Want = ($PythonVersion -split "\.")[0..1] -join "."
$Have = (& python -c "import sys; print('%d.%d' % sys.version_info[:2])").Trim()
if ($Have -ne $Want) { throw "Building for Python $Want needs Python $Want here, not $Have" }
$Site = Join-Path $Py "Lib\site-packages"
& python -m pip install --disable-pip-version-check --no-warn-script-location --target $Site $Root
if ($LASTEXITCODE -ne 0) { throw "pip couldn't install granola-share" }
Remove-Item -Recurse -Force (Join-Path $Site "bin") -ErrorAction SilentlyContinue  # launchers for the build machine's Python
Set-Content -Path (Join-Path $Py "study-stash-bundle.txt") -Encoding ASCII `
  -Value "granola-share with its own Python (windows/bundle.ps1). An update replaces this whole folder."

# 4. The zip the installer and auto-update download.
$Zip = Join-Path $Out "Study-Stash-helper-windows.zip"
Remove-Item $Zip -ErrorAction SilentlyContinue
[System.IO.Compression.ZipFile]::CreateFromDirectory($Stage, $Zip)
Write-Host ("Built {0} ({1:N0} MB)" -f $Zip, ((Get-Item $Zip).Length / 1MB))

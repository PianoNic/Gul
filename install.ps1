$ErrorActionPreference = 'Stop'

$repo = 'PianoNic/Gul'
$installDir = if ($env:GUL_INSTALL_DIR) { $env:GUL_INSTALL_DIR } else { Join-Path $env:LOCALAPPDATA 'Programs\Gul' }

$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x64' }
$asset = "gul-win-$arch.exe"
$url = "https://github.com/$repo/releases/latest/download/$asset"

New-Item -ItemType Directory -Force -Path $installDir | Out-Null
$dest = Join-Path $installDir 'gul.exe'

Write-Host "Installing gul (win-$arch) from the latest release..."
Invoke-WebRequest -Uri $url -OutFile $dest

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if (($userPath -split ';') -notcontains $installDir) {
    [Environment]::SetEnvironmentVariable('Path', "$userPath;$installDir", 'User')
    Write-Host "Added $installDir to your user PATH (restart your terminal to pick it up)."
}

Write-Host "Installed to $dest"
Write-Host "Next: gul remote https://gul.example.com"

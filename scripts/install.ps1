# Install the latest `shonkor` release binary (Windows) — no .NET SDK required.
# Usage:  irm https://raw.githubusercontent.com/nottherealluckybuddha/Shonkor/main/scripts/install.ps1 | iex
$ErrorActionPreference = 'Stop'

$repo = 'nottherealluckybuddha/Shonkor'
$arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$asset = "shonkor-win-$arch.exe"
$url = "https://github.com/$repo/releases/latest/download/$asset"

$dest = Join-Path $env:LOCALAPPDATA 'Programs\shonkor'
New-Item -ItemType Directory -Force -Path $dest | Out-Null
$exe = Join-Path $dest 'shonkor.exe'

Write-Host "Downloading $asset ..."
Invoke-WebRequest -Uri $url -OutFile $exe
Write-Host "Installed: $exe"

$userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
if ($userPath -notlike "*$dest*") {
    [Environment]::SetEnvironmentVariable('Path', "$userPath;$dest", 'User')
    Write-Host "Added $dest to your user PATH — restart the shell for it to take effect."
}

Write-Host "Next: shonkor mcp install   (register the MCP server in your agent clients)"

#!/usr/bin/env pwsh
<#
.SYNOPSIS
  Rebuild the Shonkor CLI and reinstall it as the global dotnet tool that backs the
  `shonkor` MCP server.

.DESCRIPTION
  Run this after code changes so MCP clients (Claude Code, etc.) pick up the new build.
  The tool is installed into ~/.dotnet/tools (on PATH), independent of the repo's own
  bin/obj build outputs — so day-to-day `dotnet build` / `dotnet run` never disturb it.

  Steps: pack (Release) -> stop any running shonkor MCP process (to unlock the exe) ->
  reinstall the global tool from the freshly built package.

.NOTES
  Stopping the running MCP process disconnects `shonkor` in any live Claude Code session,
  so restart Claude Code (or reconnect MCP) afterwards to pick up the new build.
#>

$ErrorActionPreference = 'Stop'
$repo  = Split-Path -Parent $MyInvocation.MyCommand.Path
$csproj = Join-Path $repo 'src\Shonkor.CLI\Shonkor.CLI.csproj'
$nupkg = Join-Path $repo 'publish\nupkg'

Write-Host '==> Packing Shonkor.CLI (Release)...' -ForegroundColor Cyan
dotnet pack $csproj -c Release -o $nupkg --nologo
if ($LASTEXITCODE -ne 0) { throw 'dotnet pack failed.' }

Write-Host '==> Stopping running shonkor MCP process (unlocks the tool exe)...' -ForegroundColor Cyan
Get-Process -Name 'shonkor' -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 600

Write-Host '==> Reinstalling the global tool...' -ForegroundColor Cyan
# Uninstall + install refreshes the SAME version (0.1.0) with the new build; `dotnet tool
# update` only reacts to a higher version number, so it would be a no-op here.
if (dotnet tool list -g | Select-String -SimpleMatch 'shonkor') {
    dotnet tool uninstall -g Shonkor | Out-Host
}
dotnet tool install -g Shonkor --add-source $nupkg | Out-Host
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet tool install failed. If the exe was locked, close any process using it and re-run.'
}

Write-Host ''
Write-Host 'Done — the shonkor MCP tool is updated.' -ForegroundColor Green
Write-Host 'Restart Claude Code (or reconnect MCP) to pick up the new build.' -ForegroundColor Yellow

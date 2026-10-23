#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Rebuilds the MatTicket container and starts the local stack (docker-compose.local.yml).
.DESCRIPTION
    The data (database, keys, encrypted AI keys) stays in the volumes; -Clean removes them first (empty database: the first visit creates the administrator again).
.EXAMPLE
    ./scripts/redeploy.ps1
.EXAMPLE
    ./scripts/redeploy.ps1 -Clean
#>
param([switch]$Clean)

$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    # Windows PowerShell 5.1 turns the progress lines docker writes to stderr into errors: judge by the exit code.
    $ErrorActionPreference = 'Continue'
    $compose = @('compose', '-f', 'docker-compose.local.yml')
    if ($Clean) {
        docker @compose down -v --remove-orphans
    }

    $env:DOCKER_BUILDKIT = '1'
    docker @compose build --build-arg "BuildDate=$(Get-Date -Format yyyyMMdd)"
    if ($LASTEXITCODE -ne 0) { throw 'docker compose build ist fehlgeschlagen.' }
    docker @compose up -d --remove-orphans
    if ($LASTEXITCODE -ne 0) { throw 'docker compose up ist fehlgeschlagen.' }

    Write-Host ''
    Write-Host 'MatTicket laeuft auf http://localhost:2740 (pgAdmin: http://localhost:2741)' -ForegroundColor Green
}
finally {
    Pop-Location
}

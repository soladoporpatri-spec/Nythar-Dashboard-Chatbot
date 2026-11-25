$ErrorActionPreference = "SilentlyContinue"
Set-Location $PSScriptRoot

Write-Host "==============================================================" -ForegroundColor DarkYellow
Write-Host "  BARBEARIA CLINIC HAIR - INICIALIZADOR (.NET 10 + BAILEYS)" -ForegroundColor Yellow
Write-Host "  Dashboard: http://localhost:5150" -ForegroundColor Cyan
Write-Host "=============================================================="

# Libera portas 5150 e 3100 caso já exista instância em segundo plano
Stop-Process -Name "ClinicHair.WebApi" -Force -ErrorAction SilentlyContinue
Get-NetTCPConnection -LocalPort 5150, 3100 -State Listen -ErrorAction SilentlyContinue | ForEach-Object {
    Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue
}

$ErrorActionPreference = "Stop"
Start-Process -FilePath "node" -ArgumentList "src/ClinicHair.WhatsAppBridge/dist/index.js" -WorkingDirectory $PSScriptRoot -WindowStyle Minimized
Start-Process -FilePath "dotnet" -ArgumentList "run --project src/ClinicHair.WebApi/ClinicHair.WebApi.csproj --urls http://localhost:5150" -WorkingDirectory $PSScriptRoot -WindowStyle Minimized

Start-Sleep -Seconds 3
Start-Process "http://localhost:5150"
Write-Host "Clinic Hair em execução em http://localhost:5150" -ForegroundColor Green

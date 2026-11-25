@echo off
chcp 65001 >nul
title Clinic Hair - Inicializador Completo (.NET 10 + WhatsApp Baileys Bridge)
cd /d "%~dp0"

echo ==============================================================
echo   BARBEARIA CLINIC HAIR - INICIANDO SISTEMA COMPLETO
echo   Admin: Itamar ^| Senha Inicial: 311208
echo ==============================================================
echo.

echo [0/3] Liberando portas 5150 e 3100 (caso exista instancia anterior aberta)...
taskkill /F /IM ClinicHair.WebApi.exe >nul 2>&1
for /f "tokens=5" %%a in ('netstat -aon ^| findstr ":5150" ^| findstr "LISTENING"') do taskkill /F /PID %%a >nul 2>&1
for /f "tokens=5" %%a in ('netstat -aon ^| findstr ":3100" ^| findstr "LISTENING"') do taskkill /F /PID %%a >nul 2>&1

echo [1/3] Iniciando Bridge WhatsApp TypeScript (@whiskeysockets/baileys) na porta 3100...
start "Clinic Hair - WhatsApp Baileys Bridge" cmd /k "cd /d "%~dp0src\ClinicHair.WhatsAppBridge" && node dist/index.js"

echo [2/3] Iniciando WebApp e API C# (.NET 10 LTS) em http://localhost:5150...
start "Clinic Hair - WebApp .NET 10" cmd /k "cd /d "%~dp0" && dotnet run --project src/ClinicHair.WebApi/ClinicHair.WebApi.csproj --urls http://localhost:5150"

echo [3/3] Aguardando 4 segundos e abrindo a Dashboard no navegador...
timeout /t 4 /nobreak >nul
start "" "http://localhost:5150"

echo.
echo Sistema iniciado com sucesso!
exit /b 0

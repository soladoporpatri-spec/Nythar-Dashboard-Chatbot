<#
  Watchdog Clinic Hair — mantém o sistema no ar sozinho.
  - Sobe a API (.NET) se não estiver rodando (a API, por sua vez, supervisiona e religa o WhatsApp Bridge).
  - A cada 15s consulta http://127.0.0.1:5150/health/live; após 3 falhas seguidas derruba a árvore de processos e religa.
  - Backoff em crash-loop (5s → 60s) para não consumir CPU se houver erro persistente.
  - Log em storage/logs/watchdog.log
  Uso: powershell -ExecutionPolicy Bypass -File watchdog-clinichair.ps1
#>
$ErrorActionPreference = 'Continue'
Set-Location $PSScriptRoot

$Port        = 5150
$HealthUrl   = "http://127.0.0.1:$Port/health/live"
$Project     = 'src/ClinicHair.WebApi/ClinicHair.WebApi.csproj'
$LogDir      = Join-Path $PSScriptRoot 'src/ClinicHair.WebApi/storage/logs'
$LogFile     = Join-Path $LogDir 'watchdog.log'
New-Item -ItemType Directory -Force -Path $LogDir | Out-Null

# Instância única do watchdog (evita dois watchdogs brigando)
$mutex = New-Object System.Threading.Mutex($false, 'Global\ClinicHairWatchdog')
if (-not $mutex.WaitOne(0)) { Write-Host 'Watchdog já está em execução.'; exit 0 }

function Write-Log([string]$msg) {
    $line = "{0} {1}" -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), $msg
    Write-Host $line
    try {
        if ((Test-Path $LogFile) -and (Get-Item $LogFile).Length -gt 2MB) { Move-Item $LogFile "$LogFile.old" -Force }
        Add-Content -Path $LogFile -Value $line
    } catch {}
}

function Test-Health {
    try {
        $r = Invoke-WebRequest -Uri $HealthUrl -UseBasicParsing -TimeoutSec 3
        return $r.StatusCode -eq 200
    } catch { return $false }
}

function Stop-Api([System.Diagnostics.Process]$proc) {
    if ($proc -and -not $proc.HasExited) { & taskkill /PID $proc.Id /T /F | Out-Null }
    # Garante a porta livre mesmo se a instância veio de outro lugar
    Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | ForEach-Object {
        Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue
    }
}

function Start-Api {
    Write-Log "Iniciando API Clinic Hair em http://localhost:$Port ..."
    return Start-Process -FilePath 'dotnet' `
        -ArgumentList "run --project $Project --urls http://localhost:$Port" `
        -WorkingDirectory $PSScriptRoot -WindowStyle Hidden -PassThru
}

$api = $null
$failures = 0
$crashStreak = 0
$lastStart = [DateTime]::MinValue

Write-Log 'Watchdog iniciado.'
try {
    while ($true) {
        if (Test-Health) {
            $failures = 0
            if ($crashStreak -gt 0 -and ((Get-Date) - $lastStart).TotalMinutes -gt 2) { $crashStreak = 0 }
        }
        else {
            $apiAlive = $api -and -not $api.HasExited
            $portBusy = [bool](Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue)
            $starting = $apiAlive -and (((Get-Date) - $lastStart).TotalSeconds -lt 90)  # tolerância de inicialização

            if (-not $starting) {
                $failures++
                if (-not $apiAlive -and -not $portBusy) {
                    $failures = 3   # processo morto: religa já
                }
                if ($failures -ge 3) {
                    Write-Log "API fora do ar (falhas=$failures). Religando..."
                    Stop-Api $api
                    $backoff = [Math]::Min(5 * [Math]::Pow(2, [Math]::Min($crashStreak, 4)), 60)
                    if (((Get-Date) - $lastStart).TotalSeconds -lt 60) { $crashStreak++ }
                    Start-Sleep -Seconds $backoff
                    $api = Start-Api
                    $lastStart = Get-Date
                    $failures = 0
                }
            }
        }
        Start-Sleep -Seconds 15
    }
}
finally {
    $mutex.ReleaseMutex() | Out-Null
}

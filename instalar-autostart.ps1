<#
  Registra o watchdog do Clinic Hair para iniciar sozinho a cada login do Windows
  e religar automaticamente se for encerrado por falha. Execute UMA vez:
    powershell -ExecutionPolicy Bypass -File instalar-autostart.ps1
  Para remover:
    powershell -ExecutionPolicy Bypass -File instalar-autostart.ps1 -Remover
#>
param([switch]$Remover)

$TaskName = 'ClinicHair-Watchdog'
$script   = Join-Path $PSScriptRoot 'watchdog-clinichair.ps1'

if ($Remover) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
    Write-Host "Tarefa '$TaskName' removida." -ForegroundColor Yellow
    exit 0
}

$action   = New-ScheduledTaskAction -Execute 'powershell.exe' `
    -Argument "-NoProfile -WindowStyle Hidden -ExecutionPolicy Bypass -File `"$script`"" `
    -WorkingDirectory $PSScriptRoot
$trigger  = New-ScheduledTaskTrigger -AtLogOn -User $env:USERNAME
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
    -StartWhenAvailable -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) -MultipleInstances IgnoreNew

Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
    -Description 'Mantém o sistema Clinic Hair (API + WhatsApp Bridge) sempre no ar.' -Force | Out-Null

Write-Host "Autostart instalado: '$TaskName' inicia no login e religa o sistema sozinho." -ForegroundColor Green
Write-Host "Para iniciar agora sem reiniciar o PC: Start-ScheduledTask -TaskName $TaskName"

using ClinicHair.WebApi.Services;

namespace ClinicHair.WebApi.Workers;

/// <summary>
/// Worker em segundo plano que verifica a cada 45 segundos os agendamentos confirmados
/// faltando até 1 hora para o horário marcado e dispara o lembrete automático no WhatsApp.
/// </summary>
public sealed class Reminder1hWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<Reminder1hWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(45));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var orchestrator = scope.ServiceProvider.GetRequiredService<IWhatsAppOrchestratorService>();
                int sent = await orchestrator.ProcessDue1hRemindersAsync(stoppingToken);
                if (sent > 0)
                {
                    logger.LogInformation("Reminder1hWorker: {SentCount} lembrete(s) de 1h antes enviado(s) pelo WhatsApp.", sent);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao processar lembretes de 1 hora antes no Reminder1hWorker.");
            }
        }
    }
}

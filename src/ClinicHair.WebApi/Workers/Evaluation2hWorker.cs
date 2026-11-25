using ClinicHair.WebApi.Services;

namespace ClinicHair.WebApi.Workers;

/// <summary>
/// Worker em segundo plano que verifica agendamentos após 2 horas do horário marcado
/// e envia a enquete perguntando se o cliente deseja avaliar (abrindo janela de 1h para arquivo .txt).
/// </summary>
public sealed class Evaluation2hWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<Evaluation2hWorker> logger) : BackgroundService
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
                int sent = await orchestrator.ProcessDue2hEvaluationsAsync(stoppingToken);
                if (sent > 0)
                {
                    logger.LogInformation("Evaluation2hWorker: {SentCount} enquete(s) de avaliação (2h após) enviada(s) pelo WhatsApp.", sent);
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Erro ao processar enquetes de avaliação de 2 horas no Evaluation2hWorker.");
            }
        }
    }
}

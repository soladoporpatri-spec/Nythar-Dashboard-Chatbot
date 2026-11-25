using ClinicHair.WebApi.Services;

namespace ClinicHair.WebApi.Workers;

/// <summary>
/// Worker em segundo plano que executa limpeza e compactação periódica (GZip) de arquivos .txt antigos
/// e expurga eventos de webhook obsoletos para otimizar espaço em disco e banco de dados.
/// </summary>
public sealed class EvaluationStorageCleanupWorker(
    IServiceScopeFactory scopeFactory,
    ILogger<EvaluationStorageCleanupWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken))
                {
                    break;
                }

                await using var scope = scopeFactory.CreateAsyncScope();
                var dashboardService = scope.ServiceProvider.GetRequiredService<IDashboardService>();
                var stats = await dashboardService.TriggerStorageCleanupAsync(stoppingToken);

                logger.LogInformation(
                    "Limpeza periódica de avaliações .txt concluída. Arquivos ativos: {ActiveTxt}, Compactados: {Compacted}, Espaço em disco: {Bytes} bytes.",
                    stats.ActiveTxtFilesOnDisk,
                    stats.CompactedEvaluations,
                    stats.TotalBytesOnDisk);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Erro durante o ciclo de limpeza periódica de arquivos .txt de avaliação.");
            }
        }
    }
}

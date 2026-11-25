using ClinicHair.WebApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Workers;

/// <summary>
/// Backup automático do banco SQLite local (VACUUM INTO = cópia consistente, mesmo com a API em uso).
/// - 1º backup ~1 minuto após subir, depois a cada 6 horas.
/// - Mantém os 14 backups mais recentes em storage/backups.
/// - Com MySQL ativo o backup deve ser feito pelo próprio servidor MySQL (mysqldump); o worker apenas registra aviso.
/// </summary>
public sealed class DatabaseBackupWorker(
    IServiceScopeFactory scopeFactory,
    IWebHostEnvironment env,
    IConfiguration config,
    ILogger<DatabaseBackupWorker> logger) : BackgroundService
{
    private const int KeepLast = 14;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromHours(6));
        do
        {
            try
            {
                await RunBackupAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Falha ao gerar backup automático do banco (tentará novamente no próximo ciclo).");
            }
        }
        while (await WaitSafeAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitSafeAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    /// <summary>
    /// Teste de restauração (item 87): abre a cópia somente-leitura, roda integrity_check e lê as tabelas principais.
    /// Um backup que não abre é renomeado para ".corrompido" e não conta na retenção.
    /// </summary>
    private void VerifyBackup(string path)
    {
        try
        {
            using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Mode=ReadOnly;Pooling=False");
            conn.Open();
            using var check = conn.CreateCommand();
            check.CommandText = "PRAGMA integrity_check;";
            var result = check.ExecuteScalar()?.ToString();
            using var count = conn.CreateCommand();
            count.CommandText = "SELECT (SELECT COUNT(*) FROM Stores), (SELECT COUNT(*) FROM Appointments), (SELECT COUNT(*) FROM Customers);";
            using var reader = count.ExecuteReader();
            reader.Read();
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase) || reader.GetInt64(0) == 0)
            {
                throw new InvalidOperationException($"integrity_check={result}");
            }
            logger.LogInformation("Backup verificado (restauração de teste OK): {Stores} loja(s), {Appts} agendamento(s), {Customers} cliente(s).",
                reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "BACKUP INVÁLIDO: {Path} não passou no teste de restauração.", path);
            try { File.Move(path, path + ".corrompido", overwrite: true); } catch { }
        }
    }

    private async Task RunBackupAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicHairDbContext>();

        if (!db.Database.IsSqlite())
        {
            logger.LogDebug("Banco MySQL em uso: backup deve ser feito via mysqldump no servidor.");
            return;
        }

        var backupDir = Path.Combine(ClinicHair.WebApi.Infrastructure.Security.StoragePaths.Root(env, config), "backups");
        Directory.CreateDirectory(backupDir);

        var target = Path.Combine(backupDir, $"clinichair_{DateTime.Now:yyyyMMdd_HHmmss}.db");
        var escaped = target.Replace("'", "''");

#pragma warning disable EF1002 // caminho gerado internamente (não vem de entrada do usuário) e escapado acima
        await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{escaped}'", ct);
#pragma warning restore EF1002

        logger.LogInformation("Backup do banco gerado: {Path}", target);
        VerifyBackup(target);

        foreach (var old in new DirectoryInfo(backupDir)
                     .GetFiles("clinichair_*.db")
                     .OrderByDescending(f => f.CreationTimeUtc)
                     .Skip(KeepLast))
        {
            try { old.Delete(); } catch { /* arquivo em uso: remove no próximo ciclo */ }
        }
    }
}

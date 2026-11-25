using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Services;
using ClinicHair.WebApi.Services.Features;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Workers;

/// <summary>
/// Lê a agenda externa (endereço secreto iCal do Google Agenda) de cada profissional a cada 10 minutos (item 95).
/// Os compromissos ficam em memória e a disponibilidade os trata como horários ocupados. Somente leitura.
/// </summary>
public sealed class CalendarSyncWorker(IServiceScopeFactory scopeFactory, IHttpClientFactory httpFactory, ILogger<CalendarSyncWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(10));
        do
        {
            try { await SyncAsync(stoppingToken); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "CalendarSyncWorker: falha no ciclo."); }
        }
        while (await Next(timer, stoppingToken));
    }

    private static async Task<bool> Next(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { return false; }
    }

    private async Task SyncAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicHairDbContext>();
        var barbers = await db.Barbers.AsNoTracking().Where(b => b.IsActive && b.CalendarIcsUrl != null).ToListAsync(ct);
        if (barbers.Count == 0) return;
        var client = httpFactory.CreateClient("calendar");
        var from = DateTime.Now.Date.AddDays(-1);
        var to = from.AddDays(62);
        foreach (var b in barbers)
        {
            try
            {
                var ics = await client.GetStringAsync(b.CalendarIcsUrl, ct);
                var events = IcsParser.Parse(ics, from, to);
                ExternalCalendarCache.Set(b.Id, events);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning("CalendarSyncWorker: não foi possível ler a agenda externa de {Barber}: {Error}", b.Name, ex.Message);
            }
        }
    }
}

/// <summary>
/// Manutenção (a cada minuto, com tarefas de hora em hora):
/// alerta de WhatsApp desconectado (item 88), rotação de logs (item 92) e cobrança/inadimplência SaaS (itens 72 e 73).
/// </summary>
public sealed class MaintenanceWorker(IServiceScopeFactory scopeFactory, IConfiguration config, ILogger<MaintenanceWorker> logger) : BackgroundService
{
    private DateTimeOffset? _bridgeDownSince;
    private bool _bridgeAlertSent;
    private DateTimeOffset _lastHourly = DateTimeOffset.MinValue;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(45), stoppingToken); } catch (OperationCanceledException) { return; }
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        do
        {
            try
            {
                await CheckBridgeAsync(stoppingToken);
                if (DateTimeOffset.UtcNow - _lastHourly >= TimeSpan.FromHours(1))
                {
                    _lastHourly = DateTimeOffset.UtcNow;
                    await HourlyAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { logger.LogError(ex, "MaintenanceWorker: falha no ciclo."); }
        }
        while (await Next(timer, stoppingToken));
    }

    private static async Task<bool> Next(PeriodicTimer t, CancellationToken ct)
    {
        try { return await t.WaitForNextTickAsync(ct); } catch (OperationCanceledException) { return false; }
    }

    private async Task CheckBridgeAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var bridge = scope.ServiceProvider.GetRequiredService<IWhatsAppBridgeClient>();
        var live = await bridge.GetBridgeLiveStatusAsync(ct);
        bool up = live is not null && string.Equals(live.Status, "CONNECTED", StringComparison.OrdinalIgnoreCase);
        var realtime = scope.ServiceProvider.GetRequiredService<IDashboardRealtimeNotifier>();
        if (up)
        {
            if (_bridgeAlertSent)
            {
                realtime.Publish("whatsapp", "O WhatsApp voltou a ficar conectado.", "WhatsApp reconectado");
                await LogAsync(scope, "INFO", "WhatsApp reconectado após queda.", ct);
            }
            _bridgeDownSince = null;
            _bridgeAlertSent = false;
            return;
        }

        _bridgeDownSince ??= DateTimeOffset.UtcNow;
        int minutes = config.GetValue("Alerts:BridgeDownMinutes", 5);
        if (!_bridgeAlertSent && DateTimeOffset.UtcNow - _bridgeDownSince >= TimeSpan.FromMinutes(minutes))
        {
            _bridgeAlertSent = true;
            var status = live?.Status ?? "OFFLINE";
            realtime.Publish("whatsapp", $"O WhatsApp está fora do ar há mais de {minutes} min (status: {status}). O bot não está respondendo clientes.", "⚠️ WhatsApp desconectado");
            await LogAsync(scope, "ALERTA", $"WhatsApp fora do ar há mais de {minutes} min (status {status}).", ct);
            logger.LogWarning("ALERTA: WhatsApp fora do ar há mais de {Minutes} min (status {Status}).", minutes, status);
        }
    }

    private static async Task LogAsync(AsyncServiceScope scope, string level, string message, CancellationToken ct)
    {
        var db = scope.ServiceProvider.GetRequiredService<ClinicHairDbContext>();
        db.SystemLogEntries.Add(new SystemLogEntry { Level = level, Category = "WhatsAppBridge", Message = message, CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    private async Task HourlyAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicHairDbContext>();

        // Rotação: logs de sistema 90 dias, eventos de webhook 30 dias, tokens revogados já expirados
        int logDays = Math.Clamp(config.GetValue("Retention:SystemLogDays", 90), 7, 3650);
        var logCut = DateTimeOffset.UtcNow.AddDays(-logDays);
        var oldLogs = await db.SystemLogEntries.AsTracking().Where(l => l.CreatedAt < logCut).OrderBy(l => l.Id).Take(5000).ToListAsync(ct);
        if (oldLogs.Count > 0) db.SystemLogEntries.RemoveRange(oldLogs);
        var webhookCut = DateTimeOffset.UtcNow.AddDays(-30);
        var oldEvents = await db.InboxWebhookEvents.AsTracking().Where(e => e.ProcessedAtUtc < webhookCut).Take(5000).ToListAsync(ct);
        if (oldEvents.Count > 0) db.InboxWebhookEvents.RemoveRange(oldEvents);
        var now = DateTimeOffset.UtcNow;
        var expired = await db.RevokedTokens.AsTracking().Where(t => t.ExpiresAtUtc < now).Take(5000).ToListAsync(ct);
        if (expired.Count > 0) db.RevokedTokens.RemoveRange(expired);
        if (oldLogs.Count + oldEvents.Count + expired.Count > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Rotação: {Logs} log(s), {Events} evento(s) de webhook e {Tokens} token(s) expirados removidos.", oldLogs.Count, oldEvents.Count, expired.Count);
        }

        // SaaS: cobrança automática e política de atraso
        if (config.GetValue("SaaS:AutoBillingEnabled", true))
        {
            var saas = scope.ServiceProvider.GetRequiredService<ISaasService>();
            int charges = await saas.GenerateDueChargesAsync(ct);
            int changed = await saas.ApplyOverduePolicyAsync(config.GetValue("SaaS:AutoSuspendEnabled", false), config.GetValue("SaaS:SuspendAfterDays", 10), ct);
            if (charges + changed > 0) logger.LogInformation("SaaS: {Charges} cobrança(s) gerada(s), {Changed} loja(s) atualizadas.", charges, changed);
        }
    }
}

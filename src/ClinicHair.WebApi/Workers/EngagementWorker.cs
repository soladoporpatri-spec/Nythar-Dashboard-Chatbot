using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Services;
using ClinicHair.WebApi.Services.Features;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Workers;

/// <summary>
/// Rotinas de relacionamento (a cada minuto): lembrete de 24h + reenvio, aniversários,
/// envio controlado de campanhas e relatório mensal ao dono no dia 1.
/// Nada é enviado com o bot pausado ou com o WhatsApp desconectado.
/// </summary>
public sealed class EngagementWorker(IServiceScopeFactory scopeFactory, ILogger<EngagementWorker> logger) : BackgroundService
{
    private const int CampaignMessagesPerTick = 3; // ~180 mensagens/hora, espaçadas para não parecer spam

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(60));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (!await timer.WaitForNextTickAsync(stoppingToken)) break;
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "EngagementWorker: erro no ciclo de relacionamento.");
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var sp = scope.ServiceProvider;
        var orchestrator = sp.GetRequiredService<IWhatsAppOrchestratorService>();

        int reminders = await orchestrator.ProcessDue24hRemindersAsync(ct);
        if (reminders > 0) logger.LogInformation("EngagementWorker: {Count} lembrete(s) de 24h enviados.", reminders);

        var db = sp.GetRequiredService<ClinicHairDbContext>();
        var bridge = sp.GetRequiredService<IWhatsAppBridgeClient>();
        var live = await bridge.GetBridgeLiveStatusAsync(ct);
        if (live is null || !string.Equals(live.Status, "CONNECTED", StringComparison.OrdinalIgnoreCase)) return;

        var now = DateTime.Now;
        var stores = await db.Stores.AsTracking().Where(s => s.IsActive && !s.BotPaused).ToListAsync(ct);
        foreach (var store in stores)
        {
            if (now.Hour is >= 9 and < 20)
            {
                await SendBirthdaysAsync(db, bridge, store, now, ct);
                var lifecycle = sp.GetRequiredService<ISubscriptionLifecycle>();
                int renewals = await lifecycle.SendRenewalRemindersAsync(store, bridge, ct);
                if (renewals > 0) logger.LogInformation("EngagementWorker: {Count} lembrete(s) de renovação de assinatura enviados.", renewals);
                await SendCampaignBatchAsync(db, bridge, store.Id, ct);
            }

            if (store.MonthlyReportEnabled && now.Day == 1 && now.Hour >= 8)
            {
                var key = now.ToString("yyyy-MM");
                if (store.LastMonthlyReportSent != key)
                {
                    var target = Jid(store.OwnerPhone) ?? Jid(store.ConnectedPhone);
                    if (target is not null)
                    {
                        var reports = sp.GetRequiredService<IReportsService>();
                        var text = await reports.BuildMonthlySummaryAsync(store.Id, DateOnly.FromDateTime(now.AddMonths(-1)), ct);
                        await bridge.SendTextAsync(target, text, ct);
                    }
                    store.LastMonthlyReportSent = key;
                    await db.SaveChangesAsync(ct);
                    logger.LogInformation("EngagementWorker: relatório mensal enviado para a loja {Store}.", store.Name);
                }
            }
        }
    }

    private async Task SendBirthdaysAsync(ClinicHairDbContext db, IWhatsAppBridgeClient bridge, Domain.Entities.Store store, DateTime now, CancellationToken ct)
    {
        if (!store.BirthdayEnabled) return;
        var suffix = now.ToString("-MM-dd");
        var year = now.Year;
        var list = await db.Customers.AsTracking()
            .Where(c => c.StoreId == store.Id && !c.IsAnonymized && c.MarketingConsent != "OptOut"
                && c.BirthDate != null && c.BirthDate.EndsWith(suffix)
                && (c.LastBirthdayGreetingYear == null || c.LastBirthdayGreetingYear < year))
            .Take(10)
            .ToListAsync(ct);

        foreach (var c in list)
        {
            var jid = Jid(c.Phone);
            if (jid is not null)
            {
                var first = c.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? c.Name;
                var msg = store.BirthdayTemplate.Replace("{Nome}", first).Replace("{Loja}", store.Name);
                try { await bridge.SendTextAsync(jid, msg, ct); }
                catch (Exception ex) { logger.LogWarning(ex, "Falha ao enviar parabéns para o cliente {Id}.", c.Id); continue; }
            }
            c.LastBirthdayGreetingYear = year;
            await db.SaveChangesAsync(ct);
        }
    }

    private async Task SendCampaignBatchAsync(ClinicHairDbContext db, IWhatsAppBridgeClient bridge, int storeId, CancellationToken ct)
    {
        var campaign = await db.Campaigns.AsTracking()
            .Where(c => c.StoreId == storeId && (c.Status == "Queued" || c.Status == "Sending"))
            .OrderBy(c => c.Id)
            .FirstOrDefaultAsync(ct);
        if (campaign is null) return;

        campaign.Status = "Sending";
        var batch = await db.CampaignRecipients.AsTracking()
            .Where(r => r.CampaignId == campaign.Id && r.Status == "Pending")
            .OrderBy(r => r.Id)
            .Take(CampaignMessagesPerTick)
            .ToListAsync(ct);

        foreach (var r in batch)
        {
            // Reconfere o consentimento no momento do envio (o cliente pode ter respondido SAIR depois)
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CustomerId, ct);
            var jid = Jid(customer?.Phone ?? r.Phone);
            if (customer is null || customer.IsAnonymized || customer.MarketingConsent == "OptOut" || jid is null)
            {
                r.Status = "Skipped";
                continue;
            }

            var first = customer.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? customer.Name;
            var text = campaign.Message.Replace("{Nome}", first) + CrmService.OptOutFooter;
            try
            {
                await bridge.SendTextAsync(jid, text, ct);
                r.Status = "Sent";
                r.SentAt = DateTimeOffset.UtcNow;
                campaign.SentCount++;
            }
            catch (Exception ex)
            {
                r.Status = "Failed";
                campaign.FailedCount++;
                logger.LogWarning(ex, "Campanha {Campaign}: falha ao enviar para o cliente {Customer}.", campaign.Id, r.CustomerId);
            }
        }

        await db.SaveChangesAsync(ct);

        if (!await db.CampaignRecipients.AnyAsync(r => r.CampaignId == campaign.Id && r.Status == "Pending", ct))
        {
            campaign.Status = "Done";
            campaign.FinishedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }
    }

    private static string? Jid(string? phone)
    {
        var digits = PhoneIdentity.Canonical(phone);
        return digits.Length >= 10 ? $"{digits}@s.whatsapp.net" : null;
    }
}

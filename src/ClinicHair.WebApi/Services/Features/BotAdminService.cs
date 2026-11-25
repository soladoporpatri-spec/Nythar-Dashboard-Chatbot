using System.Text.Json;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Services.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Services.Features;

/// <summary>Painel do bot: automações, fila de atendimento humano, conversas, respostas rápidas e saúde do bridge.</summary>
public interface IBotAdminService
{
    Task<BotAutomationSettingsDto> GetSettingsAsync(CancellationToken ct);
    Task<BotAutomationSettingsDto> SaveSettingsAsync(BotAutomationSettingsDto request, CancellationToken ct);
    Task<IReadOnlyList<ConversationSummaryDto>> GetConversationsAsync(string? filter, CancellationToken ct);
    Task<ConversationDetailDto?> GetConversationAsync(string phone, CancellationToken ct);
    Task<ConversationSummaryDto?> SetTakeoverAsync(string phone, TakeoverRequest request, CancellationToken ct);
    Task<ConversationSummaryDto?> ResolveAsync(string phone, CancellationToken ct);
    Task<ConversationDetailDto?> ReplyAsync(string phone, string text, CancellationToken ct);
    Task<IReadOnlyList<QuickReplyDto>> GetQuickRepliesAsync(CancellationToken ct);
    Task<QuickReplyDto> UpsertQuickReplyAsync(int? id, UpsertQuickReplyRequest request, CancellationToken ct);
    Task<bool> DeleteQuickReplyAsync(int id, CancellationToken ct);
    Task<BridgeHealthDto> GetHealthAsync(CancellationToken ct);
}

public sealed class BotAdminService(
    ClinicHairDbContext db,
    ICurrentStore current,
    IAuditLogger audit,
    IWhatsAppBridgeClient bridge,
    IWhatsAppOrchestratorService orchestrator) : IBotAdminService
{
    public async Task<BotAutomationSettingsDto> GetSettingsAsync(CancellationToken ct)
        => ToDto(await StoreQueries.LoadAsync(db, current.StoreId, false, ct));

    public async Task<BotAutomationSettingsDto> SaveSettingsAsync(BotAutomationSettingsDto r, CancellationToken ct)
    {
        var store = await StoreQueries.LoadAsync(db, current.StoreId, true, ct);

        ValidateTime(r.BotPauseStart, "início da pausa");
        ValidateTime(r.BotPauseEnd, "fim da pausa");
        if (r.BotScheduleEnabled && (string.IsNullOrWhiteSpace(r.BotPauseStart) || string.IsNullOrWhiteSpace(r.BotPauseEnd)))
        {
            throw new ArgumentException("Informe o início e o fim da pausa programada.");
        }
        if (!string.IsNullOrWhiteSpace(r.HolidayNoticeUntil) && !DateOnly.TryParseExact(r.HolidayNoticeUntil, "yyyy-MM-dd", out _))
        {
            throw new ArgumentException("Data final do aviso inválida.");
        }
        if (!string.IsNullOrWhiteSpace(r.MapLink)
            && (!Uri.TryCreate(r.MapLink.Trim(), UriKind.Absolute, out var mapUri) || mapUri.Scheme is not ("http" or "https")))
        {
            throw new ArgumentException("O link do mapa precisa começar com http:// ou https://");
        }

        store.BotScheduleEnabled = r.BotScheduleEnabled;
        store.BotPauseStart = Clean(r.BotPauseStart, 5);
        store.BotPauseEnd = Clean(r.BotPauseEnd, 5);
        store.BotPauseOutsideOpeningHours = r.BotPauseOutsideOpeningHours;
        store.WelcomeMessage = Clean(r.WelcomeMessage, 1000);
        store.HolidayNoticeMessage = Clean(r.HolidayNoticeMessage, 1000);
        store.HolidayNoticeUntil = Clean(r.HolidayNoticeUntil, 10);
        store.Address = Clean(r.Address, 300);
        store.MapLink = Clean(r.MapLink, 500);
        store.Reminder24hEnabled = r.Reminder24hEnabled;
        store.Reminder24hTemplate = Clean(r.Reminder24hTemplate, 1000) ?? store.Reminder24hTemplate;
        store.ReminderResendHours = Math.Clamp(r.ReminderResendHours, 0, 23);
        store.MinCancelHours = Math.Clamp(r.MinCancelHours, 0, 72);
        store.AntiSpamMaxPerMinute = Math.Clamp(r.AntiSpamMaxPerMinute, 0, 60);
        store.BirthdayEnabled = r.BirthdayEnabled;
        store.BirthdayTemplate = Clean(r.BirthdayTemplate, 1000) ?? store.BirthdayTemplate;
        store.MonthlyReportEnabled = r.MonthlyReportEnabled;
        store.OwnerPhone = Clean(r.OwnerPhone, 30);
        store.MonthlyRevenueGoal = Math.Max(0, r.MonthlyRevenueGoal);
        store.MonthlyAppointmentsGoal = Math.Max(0, r.MonthlyAppointmentsGoal);
        store.UpdatedAt = DateTimeOffset.UtcNow;

        audit.Stage("Automação", "Atualizou automações do bot, avisos e metas da loja.");
        await db.SaveChangesAsync(ct);
        return ToDto(store);
    }

    public async Task<IReadOnlyList<ConversationSummaryDto>> GetConversationsAsync(string? filter, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var q = db.ConversationSessions.AsNoTracking().Where(s => s.StoreId == storeId);
        q = filter switch
        {
            "queue" => q.Where(s => s.AwaitingHuman),
            "takeover" => q.Where(s => s.HumanTakeover),
            _ => q
        };

        // SQLite não ordena DateTimeOffset no banco: ordena em memória (são poucas sessões por loja).
        var sessions = (await q.ToListAsync(ct)).OrderByDescending(s => s.UpdatedAtUtc).Take(80).ToList();
        var names = await LoadNamesAsync(storeId, sessions.Select(s => s.CustomerPhoneJid), ct);
        return sessions.Select(s => ToSummary(s, names)).ToList();
    }

    public async Task<ConversationDetailDto?> GetConversationAsync(string phone, CancellationToken ct)
    {
        var session = await FindSessionAsync(phone, tracking: false, ct);
        if (session is null) return null;
        var names = await LoadNamesAsync(session.StoreId, [session.CustomerPhoneJid], ct);
        return new ConversationDetailDto(ToSummary(session, names), History(session));
    }

    public async Task<ConversationSummaryDto?> SetTakeoverAsync(string phone, TakeoverRequest request, CancellationToken ct)
    {
        var session = await FindSessionAsync(phone, tracking: true, ct);
        if (session is null) return null;

        session.HumanTakeover = request.Enabled;
        session.HumanTakeoverUntilUtc = request.Enabled
            ? DateTimeOffset.UtcNow.AddHours(Math.Clamp(request.Hours ?? 2, 1, 72))
            : null;
        if (!request.Enabled)
        {
            session.AwaitingHuman = false;
            session.AwaitingHumanSinceUtc = null;
        }
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;

        audit.Stage("WhatsApp", request.Enabled
            ? $"Assumiu a conversa com {session.CustomerPhoneJid} (bot silenciado para este cliente)."
            : $"Devolveu a conversa com {session.CustomerPhoneJid} para o bot.");
        await db.SaveChangesAsync(ct);
        var names = await LoadNamesAsync(session.StoreId, [session.CustomerPhoneJid], ct);
        return ToSummary(session, names);
    }

    public async Task<ConversationSummaryDto?> ResolveAsync(string phone, CancellationToken ct)
    {
        var session = await FindSessionAsync(phone, tracking: true, ct);
        if (session is null) return null;
        session.AwaitingHuman = false;
        session.AwaitingHumanSinceUtc = null;
        session.MisunderstoodCount = 0;
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        var names = await LoadNamesAsync(session.StoreId, [session.CustomerPhoneJid], ct);
        return ToSummary(session, names);
    }

    public async Task<ConversationDetailDto?> ReplyAsync(string phone, string text, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Digite a mensagem.");
        if (text.Length > 2000) throw new ArgumentException("Mensagem muito longa (máx. 2000 caracteres).");

        var session = await FindSessionAsync(phone, tracking: false, ct);
        var target = session?.CustomerPhoneJid ?? PhoneIdentity.Canonical(phone);
        if (string.IsNullOrWhiteSpace(target)) throw new ArgumentException("Telefone inválido.");

        await orchestrator.SendAgentMessageAsync(current.StoreId, target, text, current.Username, ct);
        await audit.LogAsync("WhatsApp", $"Respondeu manualmente {target} pelo painel.", ct);
        return await GetConversationAsync(target, ct);
    }

    public async Task<IReadOnlyList<QuickReplyDto>> GetQuickRepliesAsync(CancellationToken ct)
    {
        var storeId = current.StoreId;
        return await db.QuickReplies.AsNoTracking()
            .Where(q => q.StoreId == storeId)
            .OrderBy(q => q.Id)
            .Select(q => new QuickReplyDto(q.Id, q.Keywords, q.Response, q.IsActive))
            .ToListAsync(ct);
    }

    public async Task<QuickReplyDto> UpsertQuickReplyAsync(int? id, UpsertQuickReplyRequest r, CancellationToken ct)
    {
        var keywords = string.Join(", ", (r.Keywords ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(k => k.Length >= 2)
            .Distinct(StringComparer.OrdinalIgnoreCase));
        if (keywords.Length == 0) throw new ArgumentException("Informe ao menos uma palavra-chave (mínimo 2 letras).");
        if (string.IsNullOrWhiteSpace(r.Response)) throw new ArgumentException("Informe a resposta.");

        QuickReply entity;
        if (id is null)
        {
            entity = new QuickReply { StoreId = current.StoreId };
            db.QuickReplies.Add(entity);
        }
        else
        {
            var storeId = current.StoreId;
            entity = await db.QuickReplies.AsTracking().FirstOrDefaultAsync(q => q.Id == id && q.StoreId == storeId, ct)
                ?? throw new KeyNotFoundException("Resposta rápida não encontrada.");
        }

        entity.Keywords = keywords.Length > 300 ? keywords[..300] : keywords;
        entity.Response = r.Response.Trim().Length > 2000 ? r.Response.Trim()[..2000] : r.Response.Trim();
        entity.IsActive = r.IsActive;
        await db.SaveChangesAsync(ct);
        return new QuickReplyDto(entity.Id, entity.Keywords, entity.Response, entity.IsActive);
    }

    public async Task<bool> DeleteQuickReplyAsync(int id, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var entity = await db.QuickReplies.AsTracking().FirstOrDefaultAsync(q => q.Id == id && q.StoreId == storeId, ct);
        if (entity is null) return false;
        db.QuickReplies.Remove(entity);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<BridgeHealthDto> GetHealthAsync(CancellationToken ct)
    {
        var store = await StoreQueries.LoadAsync(db, current.StoreId, false, ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        BridgeLiveStatusDto? live = null;
        try { live = await bridge.GetBridgeLiveStatusAsync(timeout.Token); } catch { /* offline */ }

        var hourAgo = DateTimeOffset.UtcNow.AddHours(-1);
        var events = await db.InboxWebhookEvents.AsNoTracking()
            .Select(e => e.ProcessedAtUtc)
            .ToListAsync(ct); // SQLite não ordena DateTimeOffset no servidor
        var lastWebhook = events.Count > 0 ? events.Max() : (DateTimeOffset?)null;
        var lastHour = events.Count(e => e > hourAgo);

        var awaiting = await db.ConversationSessions.CountAsync(s => s.StoreId == store.Id && s.AwaitingHuman, ct);

        var logs = (await db.SystemLogEntries.AsNoTracking()
                .Where(l => (l.StoreId == store.Id || l.StoreId == null) && (l.Level == "ERROR" || l.Level == "WARN" || l.Level == "WHATSAPP"))
                .OrderByDescending(l => l.Id)
                .Take(8)
                .ToListAsync(ct))
            .Select(l => new HealthLogItem(l.CreatedAt, l.Level, l.Message.Length > 200 ? l.Message[..200] + "…" : l.Message))
            .ToList();

        return new BridgeHealthDto(
            live is not null,
            live?.Status ?? store.WhatsAppStatus,
            lastWebhook,
            lastHour,
            awaiting,
            store.BotPaused,
            WhatsAppOrchestratorService.IsInScheduledPause(store, DateTime.Now),
            logs);
    }

    // ------------------------------------------------------------------ helpers

    private async Task<ConversationSession?> FindSessionAsync(string phone, bool tracking, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var variants = PhoneIdentity.Variants(phone);
        var canonical = PhoneIdentity.Canonical(phone);
        var q = tracking ? db.ConversationSessions.AsTracking() : db.ConversationSessions.AsNoTracking();
        return await q.Where(s => s.StoreId == storeId && (s.CustomerPhoneJid == canonical || variants.Contains(s.CustomerPhoneJid)))
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);
    }

    private async Task<Dictionary<string, string>> LoadNamesAsync(int storeId, IEnumerable<string> phones, CancellationToken ct)
    {
        var wanted = phones.Select(p => PhoneIdentity.Canonical(p)).Where(p => p.Length > 0).ToHashSet();
        var list = await db.Customers.AsNoTracking()
            .Where(c => c.StoreId == storeId)
            .Select(c => new { c.Phone, c.Name })
            .ToListAsync(ct);
        var map = new Dictionary<string, string>();
        foreach (var c in list)
        {
            var key = PhoneIdentity.Canonical(c.Phone);
            if (wanted.Contains(key)) map[key] = c.Name;
        }
        return map;
    }

    private static ConversationSummaryDto ToSummary(ConversationSession s, Dictionary<string, string> names)
    {
        var history = History(s);
        var last = history.LastOrDefault();
        var name = names.GetValueOrDefault(PhoneIdentity.Canonical(s.CustomerPhoneJid)) ?? s.CustomerName;
        bool takeoverActive = s.HumanTakeover && (s.HumanTakeoverUntilUtc is null || s.HumanTakeoverUntilUtc > DateTimeOffset.UtcNow);
        return new ConversationSummaryDto(
            s.CustomerPhoneJid,
            name,
            s.CurrentStep,
            s.AwaitingHuman,
            s.AwaitingHumanSinceUtc,
            takeoverActive,
            takeoverActive ? s.HumanTakeoverUntilUtc : null,
            s.LastInboundAtUtc,
            s.UpdatedAtUtc,
            last is null ? null : (last.Text.Length > 120 ? last.Text[..120] + "…" : last.Text));
    }

    private static IReadOnlyList<BotChatHistoryItem> History(ConversationSession s)
    {
        if (string.IsNullOrWhiteSpace(s.LastBotHistoryJson)) return [];
        try { return JsonSerializer.Deserialize<List<BotChatHistoryItem>>(s.LastBotHistoryJson) ?? []; }
        catch { return []; }
    }

    private static BotAutomationSettingsDto ToDto(Store s) => new(
        s.BotScheduleEnabled, s.BotPauseStart, s.BotPauseEnd, s.BotPauseOutsideOpeningHours,
        s.WelcomeMessage, s.HolidayNoticeMessage, s.HolidayNoticeUntil, s.Address, s.MapLink,
        s.Reminder24hEnabled, s.Reminder24hTemplate, s.ReminderResendHours, s.MinCancelHours, s.AntiSpamMaxPerMinute,
        s.BirthdayEnabled, s.BirthdayTemplate, s.MonthlyReportEnabled, s.OwnerPhone,
        s.MonthlyRevenueGoal, s.MonthlyAppointmentsGoal);

    private static void ValidateTime(string? value, string label)
    {
        if (!string.IsNullOrWhiteSpace(value) && !TimeOnly.TryParseExact(value.Trim(), "HH:mm", out _))
        {
            throw new ArgumentException($"Horário inválido para {label} (use HH:mm).");
        }
    }

    private static string? Clean(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        return t.Length > max ? t[..max] : t;
    }
}

/// <summary>Avisa a lista de espera pelo WhatsApp quando um horário vaga (cancelamento ou falta).</summary>
public interface IWaitlistNotifier
{
    Task<int> NotifySlotFreedAsync(int storeId, string dateIso, int barberId, CancellationToken ct);
}

public sealed class WaitlistNotifier(
    ClinicHairDbContext db,
    IWhatsAppBridgeClient bridge,
    IDashboardRealtimeNotifier realtime,
    ILogger<WaitlistNotifier> logger) : IWaitlistNotifier
{
    public async Task<int> NotifySlotFreedAsync(int storeId, string dateIso, int barberId, CancellationToken ct)
    {
        try
        {
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
            if (store is null) return 0;

            var entries = await db.WaitlistEntries.AsTracking()
                .Where(w => w.StoreId == storeId && w.Date == dateIso && w.Status == "Waiting"
                    && (w.BarberId == null || w.BarberId == 0 || w.BarberId == barberId))
                .OrderBy(w => w.Id)
                .Take(3)
                .ToListAsync(ct);
            if (entries.Count == 0) return 0;

            var dateBr = DateOnly.TryParseExact(dateIso, "yyyy-MM-dd", out var d) ? d.ToString("dd/MM") : dateIso;
            int notified = 0;
            foreach (var w in entries)
            {
                if (!store.BotPaused)
                {
                    var digits = PhoneIdentity.Canonical(w.Phone);
                    if (digits.Length >= 10)
                    {
                        var first = w.CustomerName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
                        await bridge.SendTextAsync($"{digits}@s.whatsapp.net",
                            $"Oi {first}! Vagou um horário na {store.Name} no dia {dateBr}. Quer garantir? Responda *agendar* para ver os horários livres. 💈",
                            ct);
                    }
                }
                w.Status = "Notified";
                w.NotifiedAt = DateTimeOffset.UtcNow;
                notified++;
            }

            await db.SaveChangesAsync(ct);
            realtime.Publish("agenda", $"{notified} cliente(s) da lista de espera avisado(s) sobre a vaga em {dateBr}.", "Lista de espera");
            return notified;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Falha ao avisar a lista de espera ({Date}).", dateIso);
            return 0;
        }
    }
}

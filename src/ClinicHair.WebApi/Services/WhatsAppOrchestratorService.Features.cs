using System.Globalization;
using System.Text;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ClinicHair.WebApi.Services;

/// <summary>
/// Recursos de atendimento do bot: pausa por horário e por conversa, fila humana, respostas rápidas,
/// intenções simples, anti-spam, avisos da loja, pesquisa de satisfação por nota e lembrete de 24h.
/// </summary>
public sealed partial class WhatsAppOrchestratorService
{
    private Store? _activeStore;

    private sealed class RateWindow
    {
        public DateTimeOffset Start;
        public int Count;
    }

    // ---------------------------------------------------------------- Anti-spam

    private bool IsRateLimited(Store store, string phone)
    {
        int max = store.AntiSpamMaxPerMinute;
        if (max <= 0) return false;

        var now = DateTimeOffset.UtcNow;
        var window = _cache.GetOrCreate($"spam:{store.Id}:{phone}", e =>
        {
            e.SlidingExpiration = TimeSpan.FromMinutes(3);
            return new RateWindow { Start = now };
        })!;

        lock (window)
        {
            if (now - window.Start > TimeSpan.FromMinutes(1))
            {
                window.Start = now;
                window.Count = 0;
            }
            window.Count++;
            return window.Count > max;
        }
    }

    // ---------------------------------------------------------------- Pausas e fila humana

    /// <summary>Pausa programada: janela diária (ex.: almoço) e/ou fora do horário de funcionamento.</summary>
    public static bool IsInScheduledPause(Store store, DateTime nowLocal)
    {
        int now = nowLocal.Hour * 60 + nowLocal.Minute;

        if (store.BotScheduleEnabled)
        {
            int s = Features.AvailabilityService.ParseMinutes(store.BotPauseStart, -1);
            int e = Features.AvailabilityService.ParseMinutes(store.BotPauseEnd, -1);
            if (s >= 0 && e >= 0 && s != e)
            {
                bool inside = s < e ? now >= s && now < e : now >= s || now < e; // janela que cruza a meia-noite
                if (inside) return true;
            }
        }

        if (store.BotPauseOutsideOpeningHours)
        {
            var days = Features.AvailabilityService.ParseDays(store.WorkingDaysCsv);
            if (!days.Contains((int)nowLocal.DayOfWeek)) return true;
            int open = Features.AvailabilityService.ParseMinutes(store.OpeningTime, 8 * 60);
            int close = Features.AvailabilityService.ParseMinutes(store.ClosingTime, 20 * 60);
            if (now < open || now >= close) return true;
        }

        return false;
    }

    private Task<bool> ShouldStaySilentAsync(Store store, ConversationSession session, string preview, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;

        if (session.HumanTakeover)
        {
            if (session.HumanTakeoverUntilUtc is null || session.HumanTakeoverUntilUtc > now)
            {
                _realtime.Publish("whatsapp-inbox", $"{Short(preview)}", $"Mensagem de {session.CustomerPhoneJid}");
                _db.ConversationSessions.Update(session);
                return Task.FromResult(true);
            }
            session.HumanTakeover = false; // prazo do atendimento humano expirou: bot volta a responder
            session.HumanTakeoverUntilUtc = null;
        }

        if (store.BotPaused || IsInScheduledPause(store, DateTime.Now))
        {
            if (!session.AwaitingHuman)
            {
                session.AwaitingHuman = true;
                session.AwaitingHumanSinceUtc = now;
            }
            _db.ConversationSessions.Update(session);
            _realtime.Publish("whatsapp-inbox", $"{Short(preview)}", $"Aguardando atendente • {session.CustomerPhoneJid}");
            _logger.LogInformation("Bot pausado: mensagem de {Phone} enviada para a fila de atendimento.", session.CustomerPhoneJid);
            return Task.FromResult(true);
        }

        return Task.FromResult(false);
    }

    private async Task RequestHumanAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        ResetSessionToIdle(session);
        session.AwaitingHuman = true;
        session.AwaitingHumanSinceUtc ??= DateTimeOffset.UtcNow;
        session.MisunderstoodCount = 0;
        await SendOutboundTextAsync(session, "Certo! Avisei a equipe e um atendente vai continuar a conversa por aqui em breve. 🙂", ct);
        _realtime.Publish("whatsapp-inbox", $"{session.CustomerName ?? session.CustomerPhoneJid} pediu para falar com a equipe.", "Cliente aguardando atendente");
    }

    /// <summary>Mensagem enviada por um atendente pelo painel: assume a conversa por 2 horas.</summary>
    public async Task SendAgentMessageAsync(int storeId, string phone, string text, string agentName, CancellationToken ct = default)
    {
        _activeStore = await _db.Stores.FirstOrDefaultAsync(s => s.Id == storeId, ct);
        var session = await GetOrCreateSessionAsync(storeId, phone, ct);
        var body = ApplyStoreVariables(text.Trim());
        var resp = await _bridge.SendTextAsync(FormatWhatsAppJid(session.CustomerPhoneJid), body, ct);

        AppendHistory(session, "outbound", "agent", $"{body}", resp.MessageId, null, null);
        ResetSessionToIdle(session);
        session.AwaitingHuman = false;
        session.AwaitingHumanSinceUtc = null;
        session.HumanTakeover = true;
        session.HumanTakeoverUntilUtc = DateTimeOffset.UtcNow.AddHours(2);
        _db.ConversationSessions.Update(session);
        await _db.SaveChangesAsync(ct);
        _logger.LogInformation("Atendente {Agent} respondeu {Phone} pelo painel.", agentName, session.CustomerPhoneJid);
    }

    // ---------------------------------------------------------------- Palavras-chave e intenções

    private async Task<bool> TryHandleControlKeywordsAsync(Store store, ConversationSession session, string text, CancellationToken ct)
    {
        var n = Normalize(text);

        // Assinatura e fidelidade pelo WhatsApp (itens 61 e 97): ASSINATURA, PAUSAR/CANCELAR/REATIVAR ASSINATURA, PONTOS
        if (session.CurrentStep is "IDLE")
        {
            var reply = await _subscriptionLifecycle.HandleCustomerCommandAsync(store, session.CustomerPhoneJid, n, ct);
            if (reply is not null)
            {
                await SendOutboundTextAsync(session, reply, ct);
                return true;
            }
        }

        if (n is "sair" or "parar" or "stop" or "descadastrar")
        {
            await SetConsentAsync(store, session, "OptOut", ct);
            await SendOutboundTextAsync(session,
                "Pronto! Você não vai mais receber mensagens promocionais. Os lembretes dos seus agendamentos continuam normalmente. Para voltar a receber, envie VOLTAR.",
                ct);
            return true;
        }

        if (n is "voltar" && session.CurrentStep is "IDLE")
        {
            await SetConsentAsync(store, session, "OptIn", ct);
            await SendOutboundTextAsync(session, "Combinado! Você voltará a receber nossas novidades. Para parar, é só enviar SAIR.", ct);
            return true;
        }

        if (n.Contains("atendente") || n is "humano" or "pessoa" || n.Contains("falar com alguem") || n.Contains("falar com a equipe"))
        {
            await RequestHumanAsync(store, session, ct);
            return true;
        }

        return false;
    }

    private async Task SetConsentAsync(Store store, ConversationSession session, string consent, CancellationToken ct)
    {
        var customer = await _customers.FindAsync(store.Id, session.CustomerPhoneJid, tracking: true, ct);
        if (customer is null) return;
        customer.MarketingConsent = consent;
        customer.ConsentUpdatedAt = DateTimeOffset.UtcNow;
        _db.Customers.Update(customer);
    }

    private async Task<bool> TryHandleQuickReplyAsync(Store store, ConversationSession session, string text, CancellationToken ct)
    {
        var n = Normalize(text);
        if (n.Length == 0 || n.Length > 120) return false;

        var replies = await _db.QuickReplies.AsNoTracking()
            .Where(q => q.StoreId == store.Id && q.IsActive)
            .ToListAsync(ct);

        foreach (var reply in replies)
        {
            var keys = reply.Keywords.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Normalize)
                .Where(k => k.Length > 0);
            if (keys.Any(k => ContainsWord(n, k)))
            {
                await SendOutboundTextAsync(session, reply.Response, ct);
                return true;
            }
        }
        return false;
    }

    private async Task<bool> TryHandleIntentAsync(Store store, ConversationSession session, string text, CancellationToken ct)
    {
        var n = Normalize(text);
        if (n.Length == 0 || n.Length > 80) return false;

        if (n.Contains("cancelar") || n.Contains("desmarcar"))
        {
            await HandleStartCancelFlowAsync(store, session, ct);
            return true;
        }

        if (n.Contains("remarcar") || n.Contains("reagendar") || n.Contains("mudar o horario") || n.Contains("trocar o horario") || n.Contains("mudar horario") || n.Contains("trocar horario"))
        {
            await HandleStartRescheduleFlowAsync(store, session, ct);
            return true;
        }

        if (n.Contains("preco") || n.Contains("valor") || n.Contains("quanto custa") || n.Contains("quanto e") || n.Contains("tabela"))
        {
            var services = await _db.ServiceItems.AsNoTracking()
                .Where(s => s.StoreId == store.Id && s.IsActive)
                .OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id)
                .ToListAsync(ct);
            if (services.Count == 0) return false;

            var sb = new StringBuilder($"💈 Valores da {store.Name}:\n");
            foreach (var s in services)
            {
                sb.Append("• ").Append(s.Name).Append(" — R$ ").Append(s.Price.ToString("F2", new CultureInfo("pt-BR"))).Append('\n');
            }
            sb.Append("\nPara agendar, envie *agendar*.");
            await SendOutboundTextAsync(session, sb.ToString(), ct);
            return true;
        }

        if (n.Contains("endereco") || n.Contains("onde fica") || n.Contains("localizacao") || n.Contains("como chego") || n.Contains("onde voces ficam"))
        {
            if (string.IsNullOrWhiteSpace(store.Address) && string.IsNullOrWhiteSpace(store.MapLink)) return false;
            var msg = $"📍 {store.Name}";
            if (!string.IsNullOrWhiteSpace(store.Address)) msg += $"\n{store.Address}";
            if (!string.IsNullOrWhiteSpace(store.MapLink)) msg += $"\n{store.MapLink}";
            await SendOutboundTextAsync(session, msg, ct);
            return true;
        }

        if (n.Contains("agendar") || n.Contains("marcar") || n.Contains("horario disponivel") || n.Contains("tem horario"))
        {
            session.ReschedulingAppointmentId = null;
            session.ServicePageOffset = 0;
            await SendStep2ServicePollAsync(store, session, ct);
            return true;
        }

        return false;
    }

    private static readonly string[] Greetings =
        ["oi", "ola", "bom dia", "boa tarde", "boa noite", "menu", "inicio", "comecar", "hey", "eai", "e ai", "opa", "hello", "oie"];

    private static bool IsGreeting(string text)
    {
        var n = Normalize(text);
        return Greetings.Any(g => n == g || n.StartsWith(g + " ", StringComparison.Ordinal) || n.StartsWith(g + "!", StringComparison.Ordinal));
    }

    private async Task SendMisunderstoodAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        session.MisunderstoodCount++;
        if (session.MisunderstoodCount >= 3)
        {
            await RequestHumanAsync(store, session, ct);
            return;
        }

        await SendOutboundTextAsync(session,
            "Não entendi 😅 Para continuar, escolha uma opção na enquete acima. Se preferir, envie *menu* para recomeçar ou *atendente* para falar com a equipe.",
            ct);
    }

    private async Task SendWelcomeNoticesAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        var todayIso = DateTime.Now.ToString("yyyy-MM-dd");

        if (!string.IsNullOrWhiteSpace(store.HolidayNoticeMessage)
            && (string.IsNullOrWhiteSpace(store.HolidayNoticeUntil) || string.CompareOrdinal(todayIso, store.HolidayNoticeUntil) <= 0)
            && session.LastNoticeDate != todayIso)
        {
            session.LastNoticeDate = todayIso;
            await SendOutboundTextAsync(session, store.HolidayNoticeMessage!, ct);
        }

        if (!string.IsNullOrWhiteSpace(store.WelcomeMessage) && session.CurrentStep == "IDLE")
        {
            var name = await _customers.GetKnownNameAsync(store.Id, session.CustomerPhoneJid, ct);
            var msg = store.WelcomeMessage!.Replace("{Nome}", name ?? "").Replace(" ,", ",").Replace("  ", " ");
            await SendOutboundTextAsync(session, msg, ct);
        }
    }

    // ---------------------------------------------------------------- Regra de antecedência

    private async Task<bool> BlockedByMinNoticeAsync(Store store, ConversationSession session, Appointment appt, string verb, CancellationToken ct)
    {
        if (store.MinCancelHours <= 0) return false;
        if (!TryParseAppointmentDateTime(appt.AppointmentDate, appt.StartTime, out var when)) return false;
        if ((when - DateTime.Now).TotalHours >= store.MinCancelHours) return false;

        ResetSessionToIdle(session);
        session.AwaitingHuman = true;
        session.AwaitingHumanSinceUtc ??= DateTimeOffset.UtcNow;
        await SendOutboundTextAsync(session,
            $"Para {verb} com menos de {store.MinCancelHours}h de antecedência é preciso falar com a equipe. Já avisei um atendente, que vai te responder por aqui. 🙏",
            ct);
        _realtime.Publish("whatsapp-inbox", $"Cliente quer {verb} o horário de {appt.StartTime} (dentro do prazo mínimo).", "Atendimento necessário");
        return true;
    }

    // ---------------------------------------------------------------- Pesquisa de satisfação (nota)

    private static bool TryParseStarRating(string label, out int rating)
    {
        rating = label.Count(c => c == '⭐');
        if (rating is >= 1 and <= 5) return true;
        var digit = label.FirstOrDefault(char.IsDigit);
        rating = digit is >= '1' and <= '5' ? digit - '0' : 0;
        return rating > 0;
    }

    private async Task HandleRatingVoteAsync(Store store, ConversationSession session, Appointment? evalAppt, PollOptionMeta? meta, string label, CancellationToken ct)
    {
        long apptId = evalAppt?.Id ?? 0;
        int rating = 0;
        if (meta?.Value is { } v && v.Split('|') is [var idPart, var ratePart])
        {
            long.TryParse(idPart, out apptId);
            int.TryParse(ratePart, out rating);
        }
        if (rating is < 1 or > 5) TryParseStarRating(label, out rating);

        var appt = await _db.Appointments
            .Include(a => a.Barber)
            .Include(a => a.ServiceItem)
            .FirstOrDefaultAsync(a => a.Id == apptId && a.StoreId == store.Id, ct);

        if (appt is null || rating is < 1 or > 5)
        {
            ResetSessionToIdle(session);
            await SendOutboundTextAsync(session, "Obrigado pelo retorno! Até a próxima. 💈", ct);
            return;
        }

        var existing = await _db.Evaluations.AsTracking().FirstOrDefaultAsync(e => e.AppointmentId == appt.Id, ct);
        var txt = await _txt.AppendOrCreateEvaluationTxtAsync(
            appt.Id,
            appt.AppointmentDate,
            appt.ServiceItem?.Name ?? "Serviço",
            appt.Barber?.Name ?? "Profissional",
            $"Nota: {rating}/5",
            existing?.TxtRelativePath,
            ct);

        var sentiment = rating >= 4 ? "Positive" : rating <= 2 ? "Negative" : null;
        var windowEnd = DateTimeOffset.UtcNow.AddMinutes(30);
        if (existing is null)
        {
            _db.Evaluations.Add(new Evaluation
            {
                StoreId = store.Id,
                AppointmentId = appt.Id,
                CustomerId = appt.CustomerId,
                BarberId = appt.BarberId,
                ServiceItemId = appt.ServiceItemId,
                TxtFileName = txt.FileName,
                TxtRelativePath = txt.RelativePath,
                ConsolidatedText = txt.SanitizedText,
                MessageCount = 1,
                ByteSize = txt.ByteSize,
                Rating = rating,
                Sentiment = sentiment,
                WindowExpiresAtUtc = windowEnd,
                CreatedAt = DateTimeOffset.UtcNow
            });
        }
        else
        {
            existing.Rating = rating;
            existing.Sentiment ??= sentiment;
            existing.ConsolidatedText = string.IsNullOrWhiteSpace(existing.ConsolidatedText)
                ? txt.SanitizedText
                : $"{existing.ConsolidatedText}{Environment.NewLine}{txt.SanitizedText}";
            existing.ByteSize = txt.ByteSize;
            _db.Evaluations.Update(existing);
        }

        // Comentário opcional nos próximos 30 minutos
        session.CurrentStep = "COLLECTING_EVALUATION";
        session.EvaluationAppointmentId = appt.Id;
        session.EvaluationWindowExpiresAtUtc = windowEnd;
        session.ActivePollMessageId = null;
        session.ActivePollMetadataJson = null;

        var thanks = rating >= 4
            ? "Que bom que gostou! Muito obrigado pela nota. ⭐ Se quiser deixar um comentário, é só escrever aqui nos próximos 30 minutos."
            : "Obrigado pela sinceridade. Queremos melhorar: se puder, conte aqui em poucas palavras o que aconteceu (nos próximos 30 minutos).";
        await SendOutboundTextAsync(session, thanks, ct);

        _realtime.Publish("evaluations",
            $"Nota {rating}/5 para {appt.Barber?.Name ?? "Profissional"} ({appt.ServiceItem?.Name ?? "Serviço"})",
            rating <= 2 ? "⚠️ Avaliação baixa" : "Nova avaliação");
    }

    // ---------------------------------------------------------------- Lembrete de 24h + reenvio

    public async Task<int> ProcessDue24hRemindersAsync(CancellationToken ct = default)
    {
        var nowLocal = DateTime.Now;
        if (nowLocal.Hour < 8 || nowLocal.Hour >= 21) return 0; // não incomoda o cliente de madrugada

        var stores = await _db.Stores.AsNoTracking().Where(s => s.Reminder24hEnabled && !s.BotPaused).ToListAsync(ct);
        if (stores.Count == 0) return 0;

        var live = await _bridge.GetBridgeLiveStatusAsync(ct);
        if (live is null || !string.Equals(live.Status, "CONNECTED", StringComparison.OrdinalIgnoreCase)) return 0;

        var dates = new[] { nowLocal.ToString("yyyy-MM-dd"), nowLocal.AddDays(1).ToString("yyyy-MM-dd") };
        int sent = 0;

        foreach (var store in stores)
        {
            _activeStore = store;
            var candidates = await _db.Appointments
                .AsTracking()
                .Include(a => a.Customer)
                .Include(a => a.Barber)
                .Include(a => a.ServiceItem)
                .Where(a => a.StoreId == store.Id && a.Status == "Confirmed" && dates.Contains(a.AppointmentDate) && a.CustomerConfirmedAtUtc == null)
                .ToListAsync(ct);

            foreach (var appt in candidates)
            {
                if (appt.Customer is null || !TryParseAppointmentDateTime(appt.AppointmentDate, appt.StartTime, out var when)) continue;
                var minutesUntil = (when - nowLocal).TotalMinutes;

                bool firstSend = appt.Reminder24hSentAtUtc is null
                    && minutesUntil is > 180 and <= 24 * 60
                    && appt.CreatedAt < DateTimeOffset.UtcNow.AddHours(-2);

                bool resend = appt.Reminder24hSentAtUtc is not null
                    && appt.Reminder24hResentAtUtc is null
                    && store.ReminderResendHours > 0
                    && DateTimeOffset.UtcNow - appt.Reminder24hSentAtUtc.Value >= TimeSpan.FromHours(store.ReminderResendHours)
                    && minutesUntil > 90;

                if (!firstSend && !resend) continue;

                try
                {
                    var session = await GetOrCreateSessionAsync(store.Id, NormalizePhone(appt.Customer.Phone), ct);
                    if (session.HumanTakeover && (session.HumanTakeoverUntilUtc is null || session.HumanTakeoverUntilUtc > DateTimeOffset.UtcNow))
                    {
                        continue; // atendente conversando com o cliente agora
                    }

                    var dateBr = DateOnly.TryParseExact(appt.AppointmentDate, "yyyy-MM-dd", out var d) ? d.ToString("dd/MM") : appt.AppointmentDate;
                    var barberName = appt.Barber?.Name ?? "nosso profissional";
                    string question = firstSend
                        ? store.Reminder24hTemplate
                            .Replace("{Nome}", appt.Customer.Name)
                            .Replace("{Data}", dateBr)
                            .Replace("{Hora}", appt.StartTime)
                            .Replace("{Barbeiro}", barberName)
                            .Replace("{Funcionario}", barberName)
                            .Replace("{Servico}", appt.ServiceItem?.Name ?? "Atendimento")
                        : $"Oi {appt.Customer.Name}! Ainda não recebemos sua confirmação para {dateBr} às {appt.StartTime} com {barberName}. Pode confirmar?";

                    var meta = new List<PollOptionMeta>
                    {
                        new("Vou comparecer ✅", "REMINDER_CONFIRM", appt.Id.ToString()),
                        new("Preciso remarcar 🔄", "REMINDER_RESCHEDULE", appt.Id.ToString()),
                        new("Preciso cancelar ❌", "REMINDER_CANCEL", appt.Id.ToString())
                    };
                    var pollId = await SendOutboundPollAsync(session, question, meta, ct);

                    appt.Reminder24hPollMessageId = pollId;
                    if (firstSend) appt.Reminder24hSentAtUtc = DateTimeOffset.UtcNow;
                    else appt.Reminder24hResentAtUtc = DateTimeOffset.UtcNow;
                    await _db.SaveChangesAsync(ct);
                    sent++;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Falha ao enviar lembrete de 24h do agendamento {Id}.", appt.Id);
                }
            }
        }

        return sent;
    }

    // ---------------------------------------------------------------- Utilitários

    private string ApplyStoreVariables(string text)
    {
        var store = _activeStore;
        if (store is null || string.IsNullOrEmpty(text) || !text.Contains('{')) return text;
        return text
            .Replace("{Endereço}", store.Address ?? "")
            .Replace("{Endereco}", store.Address ?? "")
            .Replace("{Link do mapa}", store.MapLink ?? "")
            .Replace("{LinkMapa}", store.MapLink ?? "")
            .Replace("{Loja}", store.Name);
    }

    private static string Short(string text) => text.Length <= 90 ? text : text[..90] + "…";

    internal static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var formD = text.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(formD.Length);
        foreach (var c in formD)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(c) || c == ' ' ? c : ' ');
        }
        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    private static bool ContainsWord(string normalizedText, string normalizedKey)
    {
        if (normalizedKey.Contains(' ')) return normalizedText.Contains(normalizedKey, StringComparison.Ordinal);
        return normalizedText.Split(' ').Any(w => w == normalizedKey || (normalizedKey.Length >= 5 && w.StartsWith(normalizedKey, StringComparison.Ordinal)));
    }
}

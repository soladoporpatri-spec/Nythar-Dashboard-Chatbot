using System.Globalization;
using System.Text.Json;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;

namespace ClinicHair.WebApi.Services;

/// <summary>
/// Orquestrador da Máquina de Estados de Enquetes do WhatsApp (Passos 1 a 7 + Lembrete 1h + Avaliação 2h em arquivo .txt).
/// </summary>
public interface IWhatsAppOrchestratorService
{
    Task ProcessWebhookEventAsync(WhatsAppWebhookEventPayload payload, CancellationToken ct = default);
    Task<BotSimulationStateResponse> GetSimulationStateAsync(string phone, CancellationToken ct = default);
    Task<BotSimulationStateResponse> ExecuteSimulationActionAsync(BotSimulationRequest request, CancellationToken ct = default);
    Task<int> ProcessDue1hRemindersAsync(CancellationToken ct = default);
    Task<int> ProcessDue2hEvaluationsAsync(CancellationToken ct = default);
    Task<bool> SendReminderForAppointmentAsync(long appointmentId, CancellationToken ct = default);
    Task<bool> SendEvaluationPollForAppointmentAsync(long appointmentId, CancellationToken ct = default);
    Task<int> ProcessDue24hRemindersAsync(CancellationToken ct = default);
    Task SendAgentMessageAsync(int storeId, string phone, string text, string agentName, CancellationToken ct = default);
}

/// <summary>
/// Implementação completa do fluxo de enquetes da Clinic Hair com controle de idempotência,
/// Stale Poll Guard e regra de capacidade de até 3 agendamentos por horário por funcionário.
/// </summary>
public sealed partial class WhatsAppOrchestratorService(
    ClinicHairDbContext db,
    IWhatsAppBridgeClient bridgeClient,
    IEvaluationTxtStorageService txtStorage,
    IDashboardRealtimeNotifier realtime,
    ICustomerDirectory customers,
    ILogger<WhatsAppOrchestratorService> logger,
    IMemoryCache cache,
    Features.IAvailabilityService availability,
    Features.IWaitlistNotifier waitlist,
    Features.ISubscriptionLifecycle subscriptionLifecycle) : IWhatsAppOrchestratorService
{
    // Campos usados pela parte parcial (WhatsAppOrchestratorService.Features.cs)
#pragma warning disable CS9124 // mesmo valor guardado em campo e no parâmetro do construtor primário (intencional)
    private readonly ClinicHairDbContext _db = db;
    private readonly IWhatsAppBridgeClient _bridge = bridgeClient;
    private readonly IEvaluationTxtStorageService _txt = txtStorage;
    private readonly IDashboardRealtimeNotifier _realtime = realtime;
    private readonly ICustomerDirectory _customers = customers;
    private readonly ILogger _logger = logger;
    private readonly IMemoryCache _cache = cache;
    private readonly Features.ISubscriptionLifecycle _subscriptionLifecycle = subscriptionLifecycle;
#pragma warning restore CS9124

    private static readonly CultureInfo PtBr = new("pt-BR");

    public async Task ProcessWebhookEventAsync(WhatsAppWebhookEventPayload payload, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(payload.EventId))
        {
            return;
        }

        // 1. Garantia estrita de Idempotência: ignora eventos duplicados já processados
        var alreadyProcessed = await db.InboxWebhookEvents.AnyAsync(e => e.EventId == payload.EventId, ct);
        if (alreadyProcessed)
        {
            logger.LogDebug("Evento de webhook duplicado ignorado (Idempotência): {EventId}", payload.EventId);
            return;
        }

        db.InboxWebhookEvents.Add(new InboxWebhookEvent
        {
            EventId = payload.EventId,
            EventType = payload.EventType,
            SenderJid = payload.SenderJid ?? string.Empty,
            ProcessedAtUtc = DateTimeOffset.UtcNow
        });

        var store = await db.Stores.AsTracking().OrderBy(s => s.Id).FirstAsync(ct);
        _activeStore = store;

        // Anti-spam: o mesmo número não aciona o bot mais de N vezes por minuto
        if (payload.EventType == "message.text" && IsRateLimited(store, NormalizePhone(payload.SenderJid)))
        {
            await db.SaveChangesAsync(ct);
            logger.LogWarning("Anti-spam: mensagens de {Sender} ignoradas temporariamente.", payload.SenderJid);
            return;
        }

        switch (payload.EventType)
        {
            case "session.qr":
                store.WhatsAppStatus = "QR_READY";
                store.LastQrCodeData = !string.IsNullOrWhiteSpace(payload.QrCodeBase64)
                    ? payload.QrCodeBase64
                    : payload.QrCodeRaw;
                store.LastPairingCode = null;
                store.UpdatedAt = DateTimeOffset.UtcNow;
                db.Stores.Update(store);
                await db.SaveChangesAsync(ct);
                realtime.Publish("whatsapp", "Novo QR Code do WhatsApp disponível.");
                return;

            case "session.pairing_code":
                store.WhatsAppStatus = "PAIRING_CODE_READY";
                store.LastPairingCode = payload.PairingCode;
                if (!string.IsNullOrWhiteSpace(payload.ConnectedPhone))
                {
                    store.ConnectedPhone = payload.ConnectedPhone;
                }
                store.LastQrCodeData = null;
                store.UpdatedAt = DateTimeOffset.UtcNow;
                db.Stores.Update(store);
                await db.SaveChangesAsync(ct);
                realtime.Publish("whatsapp", $"Código de 8 dígitos gerado: {payload.PairingCode}");
                return;

            case "session.status":
                store.WhatsAppStatus = payload.SessionStatus ?? "DISCONNECTED";
                if (!string.IsNullOrWhiteSpace(payload.ConnectedPhone))
                {
                    store.ConnectedPhone = payload.ConnectedPhone;
                }
                if (store.WhatsAppStatus is "CONNECTED" or "DISCONNECTED")
                {
                    store.LastQrCodeData = null;
                    store.LastPairingCode = null;
                }
                store.UpdatedAt = DateTimeOffset.UtcNow;
                db.Stores.Update(store);
                await db.SaveChangesAsync(ct);
                realtime.Publish("whatsapp", $"Status do WhatsApp: {store.WhatsAppStatus}");
                return;

            case "message.text":
                await HandleIncomingTextMessageAsync(
                    store,
                    NormalizePhone(payload.SenderJid),
                    payload.TextMessage ?? string.Empty,
                    ct);
                await db.SaveChangesAsync(ct);
                return;

            case "poll.vote":
                var selectedOption = payload.SelectedOptions?.FirstOrDefault();
                if (string.IsNullOrWhiteSpace(selectedOption))
                {
                    // Se o cliente apenas desmarcou a opção na enquete (voto vazio), ignoramos sem quebrar o estado
                    await db.SaveChangesAsync(ct);
                    return;
                }

                await HandleIncomingPollVoteAsync(
                    store,
                    NormalizePhone(payload.SenderJid),
                    payload.PollMessageId ?? string.Empty,
                    selectedOption,
                    ct);
                await db.SaveChangesAsync(ct);
                return;
        }
    }

    public async Task<BotSimulationStateResponse> GetSimulationStateAsync(string phone, CancellationToken ct = default)
    {
        var normalizedPhone = NormalizePhone(phone);
        var phoneVariants = PhoneIdentity.Variants(phone);
        var store = await db.Stores.FirstAsync(ct);
        var session = await db.ConversationSessions
            .Where(s => s.StoreId == store.Id && (s.CustomerPhoneJid == normalizedPhone || phoneVariants.Contains(s.CustomerPhoneJid)))
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (session is null)
        {
            return new BotSimulationStateResponse(
                normalizedPhone,
                "IDLE",
                null,
                false,
                null,
                []);
        }

        var history = DeserializeHistory(session.LastBotHistoryJson);
        bool evalOpen = session.EvaluationAppointmentId.HasValue
            && session.EvaluationWindowExpiresAtUtc.HasValue
            && session.EvaluationWindowExpiresAtUtc.Value > DateTimeOffset.UtcNow;

        return new BotSimulationStateResponse(
            normalizedPhone,
            session.CurrentStep,
            session.ActivePollMessageId,
            evalOpen,
            session.EvaluationWindowExpiresAtUtc,
            history);
    }

    public async Task<BotSimulationStateResponse> ExecuteSimulationActionAsync(BotSimulationRequest request, CancellationToken ct = default)
    {
        var normalizedPhone = NormalizePhone(request.Phone);
        var store = await db.Stores.AsTracking().OrderBy(st => st.Id).FirstAsync(ct);
        _activeStore = store;

        switch (request.ActionType.ToLowerInvariant())
        {
            case "reset":
                var phoneVariants = PhoneIdentity.Variants(normalizedPhone);
                var existing = await db.ConversationSessions
                    .AsTracking()
                    .Where(s => s.StoreId == store.Id && (s.CustomerPhoneJid == normalizedPhone || phoneVariants.Contains(s.CustomerPhoneJid)))
                    .ToListAsync(ct);
                if (existing.Count > 0)
                {
                    db.ConversationSessions.RemoveRange(existing);
                    await db.SaveChangesAsync(ct);
                }
                break;

            case "text":
                await HandleIncomingTextMessageAsync(store, normalizedPhone, request.Text ?? "Olá", ct);
                await db.SaveChangesAsync(ct);
                break;

            case "vote":
                if (!string.IsNullOrWhiteSpace(request.SelectedOption))
                {
                    await HandleIncomingPollVoteAsync(
                        store,
                        normalizedPhone,
                        request.PollMessageId ?? string.Empty,
                        request.SelectedOption,
                        ct);
                    await db.SaveChangesAsync(ct);
                }
                break;

            case "trigger-1h-reminder":
                var latestApptForReminder = await FindOrCreateSampleAppointmentForPhoneAsync(store.Id, normalizedPhone, ct);
                await SendReminderForAppointmentAsync(latestApptForReminder.Id, ct);
                break;

            case "trigger-2h-evaluation":
                var latestApptForEval = await FindOrCreateSampleAppointmentForPhoneAsync(store.Id, normalizedPhone, ct);
                await SendEvaluationPollForAppointmentAsync(latestApptForEval.Id, ct);
                break;
        }

        return await GetSimulationStateAsync(normalizedPhone, ct);
    }

    private async Task HandleIncomingTextMessageAsync(
        Store store,
        string phone,
        string rawText,
        CancellationToken ct)
    {
        var text = rawText.Trim();
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        var session = await GetOrCreateSessionAsync(store.Id, phone, ct);
        AppendHistory(session, "inbound", "text", text, null, null, null);
        session.LastInboundAtUtc = DateTimeOffset.UtcNow;

        // Bot pausado (geral, por horário ou atendente assumiu esta conversa): registra e coloca na fila humana
        if (await ShouldStaySilentAsync(store, session, text, ct))
        {
            return;
        }

        // Palavras-chave de controle: SAIR / VOLTAR (campanhas) e ATENDENTE (fila humana)
        if (await TryHandleControlKeywordsAsync(store, session, text, ct))
        {
            return;
        }

        // 1. Verifica se o cliente está dentro da janela de 1 hora de Avaliação pós-atendimento (.txt)
        if (session.EvaluationAppointmentId.HasValue &&
            session.EvaluationWindowExpiresAtUtc.HasValue &&
            session.EvaluationWindowExpiresAtUtc.Value > DateTimeOffset.UtcNow)
        {
            await CaptureEvaluationTextMessageAsync(store, session, text, ct);
            return;
        }

        // Se a janela de 1h já expirou, limpa os campos de avaliação
        if (session.CurrentStep == "COLLECTING_EVALUATION")
        {
            session.CurrentStep = "IDLE";
            session.EvaluationAppointmentId = null;
            session.EvaluationWindowExpiresAtUtc = null;
        }

        // 2. Verifica se o cliente está no Passo 5 (Respondendo o seu Nome para prosseguir com o agendamento)
        if (session.CurrentStep == "STEP_5_ASK_NAME")
        {
            // Nome inválido (saudação, comando, número, frase longa): pede de novo em vez de gravar lixo no CRM
            if (!PhoneIdentity.TryCleanName(text, out var cleanName))
            {
                await SendOutboundTextAsync(
                    session,
                    "Não consegui entender seu nome 😅 Por favor, digite apenas o seu nome (ex.: João Silva).",
                    ct);
                return;
            }

            session.CustomerName = cleanName;

            // Garantia de memória: salva nome + telefone AGORA. Mesmo que o cliente desista antes de confirmar,
            // nos próximos agendamentos o sistema já o reconhece e não pergunta o nome de novo.
            await customers.SaveIdentityAsync(store.Id, session.CustomerPhoneJid, cleanName, ct);

            await SendStep6ConfirmationPollAsync(store, session, ct);
            return;
        }

        // 3. Respostas rápidas e intenções simples (preço, endereço, cancelar, remarcar, agendar)
        if (await TryHandleQuickReplyAsync(store, session, text, ct) || await TryHandleIntentAsync(store, session, text, ct))
        {
            session.MisunderstoodCount = 0;
            return;
        }

        // 4. No meio de uma enquete: texto não reconhecido recebe orientação em vez de reiniciar o fluxo
        if (!IsGreeting(text) && session.CurrentStep is not ("IDLE" or "STEP_1_WELCOME") && !string.IsNullOrWhiteSpace(session.ActivePollMessageId))
        {
            await SendMisunderstoodAsync(store, session, ct);
            return;
        }

        // 5. Saudação ou nova conversa: avisos da loja + Enquete Inicial de Boas-Vindas
        session.MisunderstoodCount = 0;
        await SendWelcomeNoticesAsync(store, session, ct);
        await SendStep1WelcomePollAsync(store, session, ct);
    }

    private async Task HandleIncomingPollVoteAsync(
        Store store,
        string phone,
        string pollMessageId,
        string selectedOptionText,
        CancellationToken ct)
    {
        var session = await GetOrCreateSessionAsync(store.Id, phone, ct);
        MarkPollOptionSelectedInHistory(session, pollMessageId, selectedOptionText);
        AppendHistory(session, "inbound", "text", $"🗳️ Votou: {selectedOptionText}", null, null, null);
        session.LastInboundAtUtc = DateTimeOffset.UtcNow;
        session.MisunderstoodCount = 0;

        if (await ShouldStaySilentAsync(store, session, $"🗳️ {selectedOptionText}", ct))
        {
            return;
        }

        // 1. Verifica se o voto pertence a uma Enquete de Avaliação (2h após o horário marcado)
        var evalAppt = !string.IsNullOrWhiteSpace(pollMessageId)
            ? await db.Appointments
                .Include(a => a.Customer)
                .FirstOrDefaultAsync(a => a.EvalPollMessageId == pollMessageId, ct)
            : null;

        var optionsMeta = DeserializePollMeta(session.ActivePollMetadataJson);
        var matchedMeta = optionsMeta.FirstOrDefault(o =>
            string.Equals(o.Label.Trim(), selectedOptionText.Trim(), StringComparison.OrdinalIgnoreCase));

        // Pesquisa curta de satisfação (nota de 1 a 5)
        if (matchedMeta?.ActionCode == "EVAL_RATE" || (evalAppt is not null && TryParseStarRating(selectedOptionText, out _)))
        {
            await HandleRatingVoteAsync(store, session, evalAppt, matchedMeta, selectedOptionText, ct);
            return;
        }

        if (evalAppt is not null || matchedMeta?.ActionCode is "EVAL_YES" or "EVAL_NO")
        {
            long targetApptId = evalAppt?.Id ?? long.Parse(matchedMeta?.Value ?? "0");
            bool wantsToEvaluate = matchedMeta?.ActionCode == "EVAL_YES"
                || selectedOptionText.StartsWith("Sim", StringComparison.OrdinalIgnoreCase)
                || selectedOptionText.Contains("Sim", StringComparison.OrdinalIgnoreCase);

            if (wantsToEvaluate && targetApptId > 0)
            {
                session.CurrentStep = "COLLECTING_EVALUATION";
                session.EvaluationAppointmentId = targetApptId;
                session.EvaluationWindowExpiresAtUtc = DateTimeOffset.UtcNow.AddHours(1);
                session.ActivePollMessageId = null;
                session.ActivePollMetadataJson = null;

                await SendOutboundTextAsync(session, store.EvaluationWindowOpenMessage, ct);
            }
            else
            {
                session.CurrentStep = "IDLE";
                session.EvaluationAppointmentId = null;
                session.EvaluationWindowExpiresAtUtc = null;
                session.ActivePollMessageId = null;
                session.ActivePollMetadataJson = null;

                await SendOutboundTextAsync(session, "Agradecemos a sua visita à Clinic Hair! Até a próxima.", ct);
            }
            return;
        }

        // 1B. Verifica se o voto pertence a uma Enquete de Lembrete 1h Antes (Confirmar / Remarcar / Cancelar)
        var reminderAppt = !string.IsNullOrWhiteSpace(pollMessageId)
            ? await db.Appointments
                .AsTracking()
                .Include(a => a.Customer)
                .Include(a => a.Barber)
                .Include(a => a.ServiceItem)
                .FirstOrDefaultAsync(a => a.ReminderPollMessageId == pollMessageId || a.Reminder24hPollMessageId == pollMessageId, ct)
            : null;

        if (reminderAppt is not null || matchedMeta?.ActionCode is "REMINDER_CONFIRM" or "REMINDER_RESCHEDULE" or "REMINDER_CANCEL")
        {
            long remApptId = reminderAppt?.Id ?? long.Parse(matchedMeta?.Value ?? "0");
            if (matchedMeta?.ActionCode == "REMINDER_CANCEL" || selectedOptionText.Contains("Cancelar", StringComparison.OrdinalIgnoreCase))
            {
                await CancelAppointmentByCustomerAsync(store, session, remApptId, ct);
                return;
            }
            if (matchedMeta?.ActionCode == "REMINDER_RESCHEDULE" || selectedOptionText.Contains("Remarcar", StringComparison.OrdinalIgnoreCase))
            {
                await StartRescheduleForAppointmentAsync(store, session, remApptId, ct);
                return;
            }

            var confirmedAppt = reminderAppt ?? await db.Appointments.AsTracking().Include(a => a.Customer)
                .FirstOrDefaultAsync(a => a.Id == remApptId && a.StoreId == store.Id, ct);
            if (confirmedAppt is not null)
            {
                confirmedAppt.CustomerConfirmedAtUtc = DateTimeOffset.UtcNow;
                realtime.Publish("agenda", $"{confirmedAppt.Customer?.Name ?? "Cliente"} confirmou presença ({confirmedAppt.StartTime}).", "Presença confirmada");
            }
            ResetSessionToIdle(session);
            await SendOutboundTextAsync(session, "Perfeito! Sua presença está confirmada. Te esperamos!", ct);
            return;
        }

        // 2. Stale Poll Guard: Se o pollMessageId informado não for o da enquete ativa atual, ignora votos antigos
        if (!string.IsNullOrWhiteSpace(pollMessageId) &&
            !string.IsNullOrWhiteSpace(session.ActivePollMessageId) &&
            !string.Equals(pollMessageId, session.ActivePollMessageId, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogInformation("Stale Poll Guard: Voto ignorado na enquete antiga {OldPollId} (Ativa: {ActivePollId})",
                pollMessageId, session.ActivePollMessageId);
            return;
        }

        string actionCode = matchedMeta?.ActionCode ?? InferActionCodeFromLabel(session.CurrentStep, selectedOptionText);
        string? actionValue = matchedMeta?.Value;

        if (actionCode == "TALK_HUMAN")
        {
            await RequestHumanAsync(store, session, ct);
            return;
        }

        // Tratamento universal de Encerrar / Cancelar
        if (actionCode is "CLOSE" or "CANCEL_BOOKING")
        {
            ResetSessionToIdle(session);
            await SendOutboundTextAsync(session, "Atendimento encerrado. Quando precisar agendar na Clinic Hair, é só mandar uma mensagem!", ct);
            return;
        }

        switch (session.CurrentStep)
        {
            case "STEP_1_WELCOME":
                if (actionCode == "GOTO_SERVICES")
                {
                    session.ReschedulingAppointmentId = null;
                    session.ServicePageOffset = 0;
                    await SendStep2ServicePollAsync(store, session, ct);
                }
                else if (actionCode == "GOTO_RESCHEDULE")
                {
                    await HandleStartRescheduleFlowAsync(store, session, ct);
                }
                else if (actionCode == "GOTO_CANCEL")
                {
                    await HandleStartCancelFlowAsync(store, session, ct);
                }
                break;

            case "STEP_1B_CANCEL_SELECT":
                if (actionCode == "BACK_TO_WELCOME")
                {
                    await SendStep1WelcomePollAsync(store, session, ct);
                }
                else if (actionCode == "CONFIRM_CANCEL_APPT" && long.TryParse(actionValue, out long cancelApptId))
                {
                    await CancelAppointmentByCustomerAsync(store, session, cancelApptId, ct);
                }
                break;

            case "STEP_1C_RESCHEDULE_SELECT":
                if (actionCode == "BACK_TO_WELCOME")
                {
                    await SendStep1WelcomePollAsync(store, session, ct);
                }
                else if (actionCode == "SELECT_RESCHEDULE_APPT" && long.TryParse(actionValue, out long reschedApptId))
                {
                    await StartRescheduleForAppointmentAsync(store, session, reschedApptId, ct);
                }
                break;

            case "STEP_2_SERVICE":
                if (actionCode == "BACK_TO_WELCOME")
                {
                    await SendStep1WelcomePollAsync(store, session, ct);
                }
                else if (actionCode == "MORE_SERVICES")
                {
                    session.ServicePageOffset += 9;
                    await SendStep2ServicePollAsync(store, session, ct);
                }
                else if (actionCode == "SELECT_SERVICE" && int.TryParse(actionValue, out int serviceId))
                {
                    session.SelectedServiceId = serviceId;
                    var activeBarbers = await db.Barbers
                        .Where(b => b.StoreId == store.Id && b.IsActive)
                        .OrderBy(b => b.Id)
                        .ToListAsync(ct);

                    if (activeBarbers.Count > 1)
                    {
                        await SendStep2BBarberPollAsync(store, session, activeBarbers, ct);
                    }
                    else
                    {
                        var singleBarberId = activeBarbers.FirstOrDefault()?.Id ?? 1;
                        session.SelectedBarberId = singleBarberId;
                        session.AssignedBarberId = singleBarberId;
                        await SendStep3DatePollAsync(store, session, ct);
                    }
                }
                break;

            case "STEP_2B_BARBER":
                if (actionCode == "BACK_TO_SERVICES")
                {
                    session.ServicePageOffset = 0;
                    await SendStep2ServicePollAsync(store, session, ct);
                }
                else if (actionCode == "SELECT_BARBER" && int.TryParse(actionValue, out int barberChoice))
                {
                    // barberChoice == 0 significa "Agendar com qualquer um"
                    session.SelectedBarberId = barberChoice;
                    session.AssignedBarberId = barberChoice > 0 ? barberChoice : null;
                    await SendStep3DatePollAsync(store, session, ct);
                }
                break;

            case "STEP_3_DATE":
                if (actionCode == "BACK_FROM_DATE")
                {
                    var activeBarbers = await db.Barbers
                        .Where(b => b.StoreId == store.Id && b.IsActive)
                        .OrderBy(b => b.Id)
                        .ToListAsync(ct);

                    if (activeBarbers.Count > 1)
                    {
                        await SendStep2BBarberPollAsync(store, session, activeBarbers, ct);
                    }
                    else
                    {
                        await SendStep2ServicePollAsync(store, session, ct);
                    }
                }
                else if (actionCode == "SELECT_DATE" && !string.IsNullOrWhiteSpace(actionValue))
                {
                    session.SelectedDate = actionValue;
                    session.TimePageOffset = 0;
                    await SendStep4TimePollAsync(store, session, ct);
                }
                break;

            case "STEP_4_TIME":
                if (actionCode == "BACK_TO_DATE")
                {
                    await SendStep3DatePollAsync(store, session, ct);
                }
                else if (actionCode == "MORE_TIMES")
                {
                    session.TimePageOffset += 8;
                    await SendStep4TimePollAsync(store, session, ct);
                }
                else if (actionCode == "SELECT_TIME" && !string.IsNullOrWhiteSpace(actionValue))
                {
                    session.SelectedTime = actionValue;

                    // Resolve o profissional disponível para o horário escolhido (respeitando < 3 agendamentos na mesma hora)
                    var resolvedBarber = await ResolveAvailableBarberForSlotAsync(
                        store.Id,
                        session.SelectedBarberId ?? 0,
                        session.SelectedDate!,
                        session.SelectedTime,
                        ct,
                        session.SelectedServiceId,
                        session.ReschedulingAppointmentId);

                    if (resolvedBarber is null)
                    {
                        await SendOutboundTextAsync(
                            session,
                            "Ops! Esse horário acabou de ser ocupado. Por favor, escolha outro horário:",
                            ct);
                        session.TimePageOffset = 0;
                        await SendStep4TimePollAsync(store, session, ct);
                        return;
                    }

                    session.AssignedBarberId = resolvedBarber.Id;

                    // Autoatendimento 2.0: Se o cliente já estiver cadastrado no CRM com nome válido, pula a pergunta de nome!
                    // Reconhece o cliente por qualquer grafia do telefone (com/sem 55, com/sem 9º dígito)
                    var knownName = await customers.GetKnownNameAsync(store.Id, session.CustomerPhoneJid, ct);

                    // Rede de segurança: nome válido digitado anteriormente nesta mesma conversa
                    if (knownName is null && !PhoneIdentity.IsPlaceholderName(session.CustomerName)
                        && PhoneIdentity.TryCleanName(session.CustomerName, out var sessionName))
                    {
                        knownName = sessionName;
                        await customers.SaveIdentityAsync(store.Id, session.CustomerPhoneJid, sessionName, ct);
                    }

                    if (knownName is not null)
                    {
                        session.CustomerName = knownName;
                        await SendStep6ConfirmationPollAsync(store, session, ct);
                    }
                    else
                    {
                        await SendStep5AskNameTextAsync(store, session, ct);
                    }
                }
                break;

            case "STEP_6_CONFIRM":
                if (actionCode == "BACK_TO_TIME")
                {
                    session.TimePageOffset = 0;
                    await SendStep4TimePollAsync(store, session, ct);
                }
                else if (actionCode == "CHANGE_NAME")
                {
                    await SendStep5AskNameTextAsync(store, session, ct);
                }
                else if (actionCode == "CONFIRM_BOOKING")
                {
                    await CompleteBookingFromPollAsync(store, session, ct);
                }
                break;
        }
    }

    private async Task SendStep1WelcomePollAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        session.CurrentStep = "STEP_1_WELCOME";
        session.ReschedulingAppointmentId = null;

        var activeAppts = await GetUpcomingConfirmedAppointmentsForPhoneAsync(store.Id, session.CustomerPhoneJid, ct);
        var meta = new List<PollOptionMeta>
        {
            new("Agendamento", "GOTO_SERVICES")
        };

        if (activeAppts.Count > 0)
        {
            meta.Add(new PollOptionMeta("Remarcar meu horário", "GOTO_RESCHEDULE"));
            meta.Add(new PollOptionMeta("Cancelar meu horário", "GOTO_CANCEL"));
        }

        meta.Add(new PollOptionMeta("Falar com a equipe", "TALK_HUMAN"));
        meta.Add(new PollOptionMeta("Encerrar", "CLOSE"));

        await SendOutboundPollAsync(session, store.WelcomePollTitle, meta, ct);
    }

    private async Task<List<Appointment>> GetUpcomingConfirmedAppointmentsForPhoneAsync(
        int storeId,
        string phone,
        CancellationToken ct)
    {
        var customer = await customers.FindAsync(storeId, phone, tracking: false, ct);
        if (customer is null) return [];

        var todayIso = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        return await db.Appointments
            .Include(a => a.Barber)
            .Include(a => a.ServiceItem)
            .Where(a => a.StoreId == storeId
                && a.CustomerId == customer.Id
                && a.Status == "Confirmed"
                && string.Compare(a.AppointmentDate, todayIso) >= 0)
            .OrderBy(a => a.AppointmentDate)
            .ThenBy(a => a.StartTime)
            .ToListAsync(ct);
    }

    private async Task HandleStartCancelFlowAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        var activeAppts = await GetUpcomingConfirmedAppointmentsForPhoneAsync(store.Id, session.CustomerPhoneJid, ct);
        if (activeAppts.Count == 0)
        {
            await SendOutboundTextAsync(session, "Você não possui agendamentos futuros ativos no momento.", ct);
            await SendStep1WelcomePollAsync(store, session, ct);
            return;
        }

        session.CurrentStep = "STEP_1B_CANCEL_SELECT";
        var meta = new List<PollOptionMeta>();
        foreach (var a in activeAppts.Take(8))
        {
            var dateBr = DateOnly.TryParseExact(a.AppointmentDate, "yyyy-MM-dd", out var d) ? d.ToString("dd/MM") : a.AppointmentDate;
            var srvName = a.ServiceItem?.Name ?? "Serviço";
            var brbName = a.Barber?.Name ?? "Profissional";
            meta.Add(new PollOptionMeta($"Cancelar: {dateBr} às {a.StartTime} - {srvName} ({brbName})", "CONFIRM_CANCEL_APPT", a.Id.ToString()));
        }
        meta.Add(new PollOptionMeta("Voltar", "BACK_TO_WELCOME"));
        meta.Add(new PollOptionMeta("Encerrar", "CLOSE"));

        await SendOutboundPollAsync(session, "Qual agendamento você deseja cancelar?", meta, ct);
    }

    private async Task HandleStartRescheduleFlowAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        var activeAppts = await GetUpcomingConfirmedAppointmentsForPhoneAsync(store.Id, session.CustomerPhoneJid, ct);
        if (activeAppts.Count == 0)
        {
            await SendOutboundTextAsync(session, "Você não possui agendamentos futuros ativos para remarcar.", ct);
            await SendStep1WelcomePollAsync(store, session, ct);
            return;
        }

        if (activeAppts.Count == 1)
        {
            await StartRescheduleForAppointmentAsync(store, session, activeAppts[0].Id, ct);
            return;
        }

        session.CurrentStep = "STEP_1C_RESCHEDULE_SELECT";
        var meta = new List<PollOptionMeta>();
        foreach (var a in activeAppts.Take(8))
        {
            var dateBr = DateOnly.TryParseExact(a.AppointmentDate, "yyyy-MM-dd", out var d) ? d.ToString("dd/MM") : a.AppointmentDate;
            var srvName = a.ServiceItem?.Name ?? "Serviço";
            var brbName = a.Barber?.Name ?? "Profissional";
            meta.Add(new PollOptionMeta($"Remarcar: {dateBr} às {a.StartTime} - {srvName} ({brbName})", "SELECT_RESCHEDULE_APPT", a.Id.ToString()));
        }
        meta.Add(new PollOptionMeta("Voltar", "BACK_TO_WELCOME"));
        meta.Add(new PollOptionMeta("Encerrar", "CLOSE"));

        await SendOutboundPollAsync(session, "Qual agendamento você deseja remarcar?", meta, ct);
    }

    private async Task StartRescheduleForAppointmentAsync(Store store, ConversationSession session, long appointmentId, CancellationToken ct)
    {
        var appt = await db.Appointments
            .Include(a => a.Customer)
            .FirstOrDefaultAsync(a => a.Id == appointmentId && a.StoreId == store.Id, ct);

        if (appt is null)
        {
            await SendStep1WelcomePollAsync(store, session, ct);
            return;
        }

        if (await BlockedByMinNoticeAsync(store, session, appt, "remarcar", ct))
        {
            return;
        }

        session.ReschedulingAppointmentId = appt.Id;
        session.SelectedServiceId = appt.ServiceItemId;
        session.SelectedBarberId = appt.BarberId;
        session.AssignedBarberId = appt.BarberId;
        if (!string.IsNullOrWhiteSpace(appt.Customer?.Name))
        {
            session.CustomerName = appt.Customer.Name;
        }

        await SendStep3DatePollAsync(store, session, ct);
    }

    private async Task CancelAppointmentByCustomerAsync(Store store, ConversationSession session, long appointmentId, CancellationToken ct)
    {
        var appt = await db.Appointments
            .AsTracking()
            .Include(a => a.Customer)
            .Include(a => a.Barber)
            .Include(a => a.ServiceItem)
            .FirstOrDefaultAsync(a => a.Id == appointmentId && a.StoreId == store.Id, ct);

        if (appt is null || appt.Status is "Cancelled" or "NoShow")
        {
            ResetSessionToIdle(session);
            await SendOutboundTextAsync(session, "Agendamento não encontrado ou já cancelado.", ct);
            return;
        }

        if (await BlockedByMinNoticeAsync(store, session, appt, "cancelar", ct))
        {
            return;
        }

        appt.Status = "Cancelled";
        db.Appointments.Update(appt);
        await db.SaveChangesAsync(ct);
        await waitlist.NotifySlotFreedAsync(store.Id, appt.AppointmentDate, appt.BarberId, ct);

        var formattedDate = DateOnly.TryParseExact(appt.AppointmentDate, "yyyy-MM-dd", out var d)
            ? d.ToString("dd/MM/yyyy")
            : appt.AppointmentDate;
        var customerName = appt.Customer?.Name ?? session.CustomerName ?? "Cliente";
        var serviceName = appt.ServiceItem?.Name ?? "Serviço";
        var barberName = appt.Barber?.Name ?? "Profissional";

        ResetSessionToIdle(session);
        await SendOutboundTextAsync(
            session,
            $"Pronto, {customerName}! Seu agendamento de {serviceName} do dia {formattedDate} às {appt.StartTime} foi cancelado com sucesso.",
            ct);

        // Avisa o funcionário diretamente no WhatsApp dele
        string barberCancelMsg = $"⚠️ {customerName} cancelou o agendamento de {serviceName} das {appt.StartTime} do dia {formattedDate}.";
        var barberTargetJid = ResolveBarberWhatsAppJid(appt.Barber?.Phone, store.ConnectedPhone);
        if (!string.IsNullOrWhiteSpace(barberTargetJid))
        {
            await bridgeClient.SendTextAsync(barberTargetJid, barberCancelMsg, ct);
        }

        realtime.Publish("agenda", barberCancelMsg, $"Cancelamento • {barberName}");
    }

    private async Task SendStep2ServicePollAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        session.CurrentStep = "STEP_2_SERVICE";
        var services = await db.ServiceItems
            .Where(s => s.StoreId == store.Id && s.IsActive)
            .OrderBy(s => s.DisplayOrder)
            .ThenBy(s => s.Id)
            .ToListAsync(ct);

        if (session.ServicePageOffset >= services.Count)
        {
            session.ServicePageOffset = 0;
        }

        var pageItems = services.Skip(session.ServicePageOffset).Take(9).ToList();
        var meta = new List<PollOptionMeta>();

        foreach (var s in pageItems)
        {
            var priceText = s.Price.ToString("F2", PtBr);
            meta.Add(new PollOptionMeta($"{s.Name} - R$ {priceText}", "SELECT_SERVICE", s.Id.ToString()));
        }

        if (services.Count > 9)
        {
            meta.Add(new PollOptionMeta("Mais opções", "MORE_SERVICES"));
        }

        meta.Add(new PollOptionMeta("Voltar", "BACK_TO_WELCOME"));
        meta.Add(new PollOptionMeta("Encerrar", "CLOSE"));

        await SendOutboundPollAsync(session, store.ServicePollTitle, meta, ct);
    }

    private async Task SendStep2BBarberPollAsync(
        Store store,
        ConversationSession session,
        IReadOnlyList<Barber> activeBarbers,
        CancellationToken ct)
    {
        session.CurrentStep = "STEP_2B_BARBER";
        var meta = new List<PollOptionMeta>();

        foreach (var b in activeBarbers.Take(9))
        {
            meta.Add(new PollOptionMeta(
                $"{b.Name} ({b.WorkStartTime} às {b.WorkEndTime})",
                "SELECT_BARBER",
                b.Id.ToString()));
        }

        meta.Add(new PollOptionMeta("Agendar com qualquer um", "SELECT_BARBER", "0"));
        meta.Add(new PollOptionMeta("Voltar", "BACK_TO_SERVICES"));
        meta.Add(new PollOptionMeta("Encerrar", "CLOSE"));

        await SendOutboundPollAsync(session, store.BarberPollTitle, meta, ct);
    }

    private async Task SendStep3DatePollAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        session.CurrentStep = "STEP_3_DATE";
        var today = DateOnly.FromDateTime(DateTime.Now);
        var tomorrow = today.AddDays(1);

        var blockedDatesList = await db.StoreNonWorkingDays
            .Where(d => d.StoreId == store.Id)
            .Select(d => d.BlockedDate)
            .ToListAsync(ct);
        var blockedDates = blockedDatesList.ToHashSet(StringComparer.Ordinal);

        var storeWorkingDays = ParseWorkingDays(store.WorkingDaysCsv);
        var meta = new List<PollOptionMeta>();

        // Busca os próximos 5 dias de funcionamento que tenham pelo menos 1 horário com < 3 agendamentos
        for (int offset = 0; offset < 21 && meta.Count < 5; offset++)
        {
            var candidate = today.AddDays(offset);
            var iso = candidate.ToString("yyyy-MM-dd");

            if (blockedDates.Contains(iso))
            {
                continue; // Pula dias de Não Funcionamento (marcados em amarelo no Calendário)
            }

            int dayNum = (int)candidate.DayOfWeek; // 0 = Dom, 1 = Seg ... 6 = Sáb
            if (!storeWorkingDays.Contains(dayNum))
            {
                continue;
            }

            var availableSlots = await GetAvailableTimeSlotsForDateAsync(
                store,
                session.SelectedBarberId ?? 0,
                candidate,
                ct,
                session.SelectedServiceId,
                session.ReschedulingAppointmentId);

            if (availableSlots.Count == 0)
            {
                continue;
            }

            string label = candidate == today
                ? $"Hoje ({candidate:dd/MM})"
                : candidate == tomorrow
                    ? $"Amanhã ({candidate:dd/MM})"
                    : $"{candidate:dd/MM}";

            meta.Add(new PollOptionMeta(label, "SELECT_DATE", iso));
        }

        meta.Add(new PollOptionMeta("Voltar", "BACK_FROM_DATE"));
        meta.Add(new PollOptionMeta("Encerrar", "CLOSE"));

        await SendOutboundPollAsync(session, store.DatePollTitle, meta, ct);
    }

    private async Task SendStep4TimePollAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        session.CurrentStep = "STEP_4_TIME";
        if (!DateOnly.TryParseExact(session.SelectedDate, "yyyy-MM-dd", out var selectedDate))
        {
            selectedDate = DateOnly.FromDateTime(DateTime.Now);
            session.SelectedDate = selectedDate.ToString("yyyy-MM-dd");
        }

        var allSlots = await GetAvailableTimeSlotsForDateAsync(
            store,
            session.SelectedBarberId ?? 0,
            selectedDate,
            ct,
            session.SelectedServiceId,
            session.ReschedulingAppointmentId);

        if (allSlots.Count == 0)
        {
            await SendOutboundTextAsync(
                session,
                "Não há mais horários livres nesse dia. Por favor, escolha outro dia:",
                ct);
            await SendStep3DatePollAsync(store, session, ct);
            return;
        }

        if (session.TimePageOffset >= allSlots.Count)
        {
            session.TimePageOffset = 0;
        }

        var pageSlots = allSlots.Skip(session.TimePageOffset).Take(8).ToList();
        var meta = new List<PollOptionMeta>();

        foreach (var slot in pageSlots)
        {
            meta.Add(new PollOptionMeta(slot, "SELECT_TIME", slot));
        }

        if (allSlots.Count > 8)
        {
            meta.Add(new PollOptionMeta("Mais opções", "MORE_TIMES"));
        }

        meta.Add(new PollOptionMeta("Voltar", "BACK_TO_DATE"));
        meta.Add(new PollOptionMeta("Encerrar", "CLOSE"));

        await SendOutboundPollAsync(session, store.TimePollTitle, meta, ct);
    }

    private async Task SendStep5AskNameTextAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        session.CurrentStep = "STEP_5_ASK_NAME";
        session.ActivePollMessageId = null;
        session.ActivePollMetadataJson = null;

        var todayIso = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        bool isToday = string.Equals(session.SelectedDate, todayIso, StringComparison.Ordinal);

        string formattedDate = session.SelectedDate ?? todayIso;
        if (DateOnly.TryParseExact(formattedDate, "yyyy-MM-dd", out var parsedDate))
        {
            formattedDate = parsedDate.ToString("dd/MM");
        }

        string template = isToday ? store.AskNameTodayTemplate : store.AskNameOtherDayTemplate;
        string prompt = template
            .Replace("{Hora}", session.SelectedTime ?? "")
            .Replace("{Data}", formattedDate);

        await SendOutboundTextAsync(session, prompt, ct);
    }

    private async Task SendStep6ConfirmationPollAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        session.CurrentStep = "STEP_6_CONFIRM";

        var service = await db.ServiceItems.FirstOrDefaultAsync(s => s.Id == session.SelectedServiceId, ct)
            ?? await db.ServiceItems.Where(s => s.IsActive).OrderBy(s => s.DisplayOrder).FirstAsync(ct);

        var barberId = session.AssignedBarberId ?? session.SelectedBarberId ?? 1;
        var barber = await db.Barbers.FirstOrDefaultAsync(b => b.Id == barberId, ct)
            ?? await db.Barbers.Where(b => b.IsActive).FirstAsync(ct);

        string displayDate = session.SelectedDate ?? DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        if (DateOnly.TryParseExact(displayDate, "yyyy-MM-dd", out var d))
        {
            displayDate = d.ToString("dd/MM/yyyy");
        }

        var priceStr = service.Price.ToString("F2", PtBr);
        var question = store.ConfirmationPollTemplate
            .Replace("{Nome}", session.CustomerName ?? "Cliente")
            .Replace("{Data}", displayDate)
            .Replace("{Hora}", session.SelectedTime ?? "")
            .Replace("{Funcionario}", barber.Name).Replace("{Barbeiro}", barber.Name)
            .Replace("{Valor}", priceStr)
            .Replace("{Servico}", service.Name);

        var meta = new List<PollOptionMeta>
        {
            new("Confirmar", "CONFIRM_BOOKING"),
            new("Alterar meu nome", "CHANGE_NAME"),
            new("Voltar", "BACK_TO_TIME"),
            new("Cancelar", "CANCEL_BOOKING")
        };

        await SendOutboundPollAsync(session, question, meta, ct);
    }

    private async Task CompleteBookingFromPollAsync(Store store, ConversationSession session, CancellationToken ct)
    {
        var dateIso = session.SelectedDate ?? DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd");
        var timeSlot = session.SelectedTime ?? "09:00";

        // Revalida na hora de confirmar se o profissional ainda possui < 3 agendamentos naquele horário
        int requestedBarberChoice = session.SelectedBarberId ?? session.AssignedBarberId ?? 0;
        var resolvedBarber = await ResolveAvailableBarberForSlotAsync(
            store.Id,
            requestedBarberChoice,
            dateIso,
            timeSlot,
            ct,
            session.SelectedServiceId,
            session.ReschedulingAppointmentId);

        if (resolvedBarber is null)
        {
            await SendOutboundTextAsync(
                session,
                "Ops! Esse horário acabou de ser ocupado. Por favor, selecione outro horário:",
                ct);
            session.TimePageOffset = 0;
            await SendStep4TimePollAsync(store, session, ct);
            return;
        }

        var service = await db.ServiceItems.FirstOrDefaultAsync(s => s.Id == session.SelectedServiceId, ct)
            ?? await db.ServiceItems.Where(s => s.IsActive).OrderBy(s => s.DisplayOrder).FirstAsync(ct);

        var hasValidSessionName = !PhoneIdentity.IsPlaceholderName(session.CustomerName);
        var customerName = hasValidSessionName ? session.CustomerName!.Trim() : "Cliente WhatsApp";
        var customer = await customers.FindAsync(store.Id, session.CustomerPhoneJid, tracking: true, ct);

        if (customer is null)
        {
            customer = new Customer
            {
                StoreId = store.Id,
                Name = customerName,
                Phone = PhoneIdentity.Canonical(session.CustomerPhoneJid) is { Length: > 0 } canon ? canon : session.CustomerPhoneJid,
                TotalAppointments = 1,
                LastAppointmentAt = DateTimeOffset.UtcNow
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            // NUNCA sobrescreve um nome real já salvo por um placeholder (causa de o sistema voltar a perguntar o nome)
            if (hasValidSessionName || PhoneIdentity.IsPlaceholderName(customer.Name))
            {
                customer.Name = customerName;
            }
            customer.LastAppointmentAt = DateTimeOffset.UtcNow;
            if (!session.ReschedulingAppointmentId.HasValue)
            {
                customer.TotalAppointments += 1;
            }
            db.Customers.Update(customer);
        }

        var formattedDate = DateOnly.TryParseExact(dateIso, "yyyy-MM-dd", out var parsedDate)
            ? parsedDate.ToString("dd/MM/yyyy")
            : dateIso;

        // Verifica se é uma Remarcação (Autoatendimento 2.0) ou Novo Agendamento
        if (session.ReschedulingAppointmentId.HasValue)
        {
            var existingAppt = await db.Appointments
                .AsTracking()
                .FirstOrDefaultAsync(a => a.Id == session.ReschedulingAppointmentId.Value && a.StoreId == store.Id, ct);

            if (existingAppt is not null)
            {
                var oldDateBr = DateOnly.TryParseExact(existingAppt.AppointmentDate, "yyyy-MM-dd", out var od)
                    ? od.ToString("dd/MM/yyyy")
                    : existingAppt.AppointmentDate;
                var oldTime = existingAppt.StartTime;

                existingAppt.BarberId = resolvedBarber.Id;
                existingAppt.ServiceItemId = service.Id;
                existingAppt.AppointmentDate = dateIso;
                existingAppt.StartTime = timeSlot;
                existingAppt.PriceCharged = service.Price;
                existingAppt.Status = "Confirmed";
                existingAppt.ReminderSentAtUtc = null;
                existingAppt.ReminderPollMessageId = null;
                db.Appointments.Update(existingAppt);

                ResetSessionToIdle(session);
                await SendOutboundTextAsync(
                    session,
                    $"Pronto, {customer.Name}! Seu agendamento de {service.Name} com {resolvedBarber.Name} foi remarcado para {formattedDate} às {timeSlot}.",
                    ct);

                string barberReschedMsg = $"🔄 {customer.Name} remarcou {service.Name} (de {oldDateBr} às {oldTime}) para às {timeSlot} do dia {formattedDate}.";
                var barberJid = ResolveBarberWhatsAppJid(resolvedBarber.Phone, store.ConnectedPhone);
                if (!string.IsNullOrWhiteSpace(barberJid))
                {
                    await bridgeClient.SendTextAsync(barberJid, barberReschedMsg, ct);
                }

                realtime.Publish("agenda", barberReschedMsg, $"Remarcação • {resolvedBarber.Name}");
                return;
            }
        }

        var appointment = new Appointment
        {
            StoreId = store.Id,
            CustomerId = customer.Id,
            BarberId = resolvedBarber.Id,
            ServiceItemId = service.Id,
            AppointmentDate = dateIso,
            StartTime = timeSlot,
            PriceCharged = service.Price,
            Status = "Confirmed",
            Source = "WhatsAppBot"
        };

        db.Appointments.Add(appointment);

        string confirmedMsg = store.ConfirmedMessageTemplate
            .Replace("{Nome}", customer.Name)
            .Replace("{Funcionario}", resolvedBarber.Name).Replace("{Barbeiro}", resolvedBarber.Name)
            .Replace("{Hora}", timeSlot)
            .Replace("{Data}", formattedDate)
            .Replace("{Servico}", service.Name);

        ResetSessionToIdle(session);
        await SendOutboundTextAsync(session, confirmedMsg, ct);

        // Envia notificação direta da conta conectada para o WhatsApp do funcionário (barbeiro)
        string barberWhatsAppMsg = $"{customer.Name} agendou {service.Name} às {timeSlot} do dia {formattedDate}.";
        var barberTargetJid = ResolveBarberWhatsAppJid(resolvedBarber.Phone, store.ConnectedPhone);
        if (!string.IsNullOrWhiteSpace(barberTargetJid))
        {
            await bridgeClient.SendTextAsync(barberTargetJid, barberWhatsAppMsg, ct);
        }

        realtime.Publish("agenda", barberWhatsAppMsg, $"Novo Agendamento • {resolvedBarber.Name}");
    }

    private async Task CaptureEvaluationTextMessageAsync(
        Store store,
        ConversationSession session,
        string text,
        CancellationToken ct)
    {
        var apptId = session.EvaluationAppointmentId!.Value;
        var appt = await db.Appointments
            .Include(a => a.Barber)
            .Include(a => a.ServiceItem)
            .FirstOrDefaultAsync(a => a.Id == apptId, ct);

        if (appt is null)
        {
            session.CurrentStep = "IDLE";
            session.EvaluationAppointmentId = null;
            session.EvaluationWindowExpiresAtUtc = null;
            return;
        }

        var existingEval = await db.Evaluations
            .AsTracking()
            .FirstOrDefaultAsync(e => e.AppointmentId == appt.Id, ct);

        var barberName = appt.Barber?.Name ?? "Profissional";
        var serviceName = appt.ServiceItem?.Name ?? "Serviço";

        var txtResult = await txtStorage.AppendOrCreateEvaluationTxtAsync(
            appt.Id,
            appt.AppointmentDate,
            serviceName,
            barberName,
            text,
            existingEval?.TxtRelativePath,
            ct);

        if (existingEval is null)
        {
            var newEval = new Evaluation
            {
                StoreId = store.Id,
                AppointmentId = appt.Id,
                CustomerId = appt.CustomerId,
                BarberId = appt.BarberId,
                ServiceItemId = appt.ServiceItemId,
                TxtFileName = txtResult.FileName,
                TxtRelativePath = txtResult.RelativePath,
                ConsolidatedText = txtResult.SanitizedText,
                MessageCount = 1,
                ByteSize = txtResult.ByteSize,
                IsTxtCompacted = false,
                WindowExpiresAtUtc = session.EvaluationWindowExpiresAtUtc!.Value,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Evaluations.Add(newEval);
        }
        else
        {
            existingEval.ConsolidatedText = string.IsNullOrWhiteSpace(existingEval.ConsolidatedText)
                ? txtResult.SanitizedText
                : $"{existingEval.ConsolidatedText}{Environment.NewLine}{txtResult.SanitizedText}";
            existingEval.MessageCount += 1;
            existingEval.ByteSize = txtResult.ByteSize;
            db.Evaluations.Update(existingEval);
        }

        await SendOutboundTextAsync(
            session,
            "Recebemos sua avaliação e salvamos no arquivo .txt da equipe! Se quiser acrescentar mais algum comentário dentro do prazo de 1 hora, basta enviar aqui.",
            ct);
        realtime.Publish("evaluations", $"Nova avaliação .txt recebida ({serviceName} • {barberName})");
    }

    public async Task<int> ProcessDue1hRemindersAsync(CancellationToken ct = default)
    {
        // Só dispara automaticamente com o WhatsApp realmente conectado: se estiver offline, NÃO marca como enviado
        // e tenta de novo no próximo ciclo (a janela de envio permite). Evita lembretes/avaliações perdidos.
        if (await IsBotPausedAsync(ct)) return 0;

        var bridgeLive = await bridgeClient.GetBridgeLiveStatusAsync(ct);
        if (bridgeLive is null || !string.Equals(bridgeLive.Status, "CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        var now = DateTime.Now;
        var todayIso = now.ToString("yyyy-MM-dd");
        var tomorrowIso = now.AddDays(1).ToString("yyyy-MM-dd");

        var candidates = await db.Appointments
            .Include(a => a.Customer)
            .Include(a => a.Barber)
            .Include(a => a.ServiceItem)
            .Where(a => a.Status == "Confirmed"
                && a.ReminderSentAtUtc == null
                && (a.AppointmentDate == todayIso || a.AppointmentDate == tomorrowIso))
            .ToListAsync(ct);

        int sentCount = 0;
        foreach (var appt in candidates)
        {
            if (TryParseAppointmentDateTime(appt.AppointmentDate, appt.StartTime, out var scheduledLocal))
            {
                var minutesUntil = (scheduledLocal - now).TotalMinutes;
                if (minutesUntil >= 0 && minutesUntil <= 60)
                {
                    if (await SendReminderForAppointmentAsync(appt.Id, ct))
                    {
                        sentCount++;
                    }
                }
            }
        }

        return sentCount;
    }

    private async Task<bool> IsBotPausedAsync(CancellationToken ct)
    {
        return await db.Stores.AsNoTracking().OrderBy(s => s.Id).Select(s => s.BotPaused).FirstOrDefaultAsync(ct);
    }

    public async Task<int> ProcessDue2hEvaluationsAsync(CancellationToken ct = default)
    {
        // Só dispara automaticamente com o WhatsApp realmente conectado: se estiver offline, NÃO marca como enviado
        // e tenta de novo no próximo ciclo (a janela de envio permite). Evita lembretes/avaliações perdidos.
        if (await IsBotPausedAsync(ct)) return 0;

        var bridgeLive = await bridgeClient.GetBridgeLiveStatusAsync(ct);
        if (bridgeLive is null || !string.Equals(bridgeLive.Status, "CONNECTED", StringComparison.OrdinalIgnoreCase))
        {
            return 0;
        }
        var now = DateTime.Now;
        var todayIso = now.ToString("yyyy-MM-dd");
        var yesterdayIso = now.AddDays(-1).ToString("yyyy-MM-dd");

        var candidates = await db.Appointments
            .Include(a => a.Customer)
            .Where(a => a.Status != "Cancelled"
                && a.EvalPollSentAtUtc == null
                && (a.AppointmentDate == todayIso || a.AppointmentDate == yesterdayIso))
            .ToListAsync(ct);

        int sentCount = 0;
        foreach (var appt in candidates)
        {
            if (TryParseAppointmentDateTime(appt.AppointmentDate, appt.StartTime, out var scheduledLocal))
            {
                var hoursSinceStart = (now - scheduledLocal).TotalHours;
                if (hoursSinceStart >= 2.0 && hoursSinceStart <= 18.0)
                {
                    if (await SendEvaluationPollForAppointmentAsync(appt.Id, ct))
                    {
                        sentCount++;
                    }
                }
            }
        }

        return sentCount;
    }

    public async Task<bool> SendReminderForAppointmentAsync(long appointmentId, CancellationToken ct = default)
    {
        var appt = await db.Appointments
            .AsTracking()
            .Include(a => a.Customer)
            .Include(a => a.Barber)
            .Include(a => a.ServiceItem)
            .FirstOrDefaultAsync(a => a.Id == appointmentId, ct);

        if (appt is null || appt.Customer is null) return false;

        var store = await db.Stores.FirstOrDefaultAsync(st => st.Id == appt.StoreId, ct) ?? await db.Stores.FirstAsync(ct);
        _activeStore = store;
        var phone = NormalizePhone(appt.Customer.Phone);
        var session = await GetOrCreateSessionAsync(store.Id, phone, ct);

        var msg = store.Reminder1hTemplate
            .Replace("{Nome}", appt.Customer.Name)
            .Replace("{Servico}", appt.ServiceItem?.Name ?? "Atendimento")
            .Replace("{Funcionario}", appt.Barber?.Name ?? "Profissional").Replace("{Barbeiro}", appt.Barber?.Name ?? "Profissional")
            .Replace("{Hora}", appt.StartTime)
            .Replace("{Data}", appt.AppointmentDate);

        await SendOutboundTextAsync(session, msg, ct);

        // Autoatendimento 2.0: Envia também enquete rápida de confirmação/remarcação/cancelamento 1h antes
        var confirmMeta = new List<PollOptionMeta>
        {
            new("Vou comparecer ✅", "REMINDER_CONFIRM", appt.Id.ToString()),
            new("Preciso remarcar 🔄", "REMINDER_RESCHEDULE", appt.Id.ToString()),
            new("Preciso cancelar ❌", "REMINDER_CANCEL", appt.Id.ToString())
        };
        var remPollId = await SendOutboundPollAsync(
            session,
            $"Confirma sua presença às {appt.StartTime} com {appt.Barber?.Name ?? "nosso profissional"}?",
            confirmMeta,
            ct);

        appt.ReminderSentAtUtc = DateTimeOffset.UtcNow;
        appt.ReminderPollMessageId = remPollId;
        db.Appointments.Update(appt);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SendEvaluationPollForAppointmentAsync(long appointmentId, CancellationToken ct = default)
    {
        var appt = await db.Appointments
            .AsTracking()
            .Include(a => a.Customer)
            .Include(a => a.Barber)
            .Include(a => a.ServiceItem)
            .FirstOrDefaultAsync(a => a.Id == appointmentId, ct);

        if (appt is null || appt.Customer is null) return false;

        var store = await db.Stores.FirstOrDefaultAsync(st => st.Id == appt.StoreId, ct) ?? await db.Stores.FirstAsync(ct);
        _activeStore = store;
        var phone = NormalizePhone(appt.Customer.Phone);
        var session = await GetOrCreateSessionAsync(store.Id, phone, ct);

        var question = store.Evaluation2hPollTitle
            .Replace("{Nome}", appt.Customer.Name)
            .Replace("{Funcionario}", appt.Barber?.Name ?? "Profissional").Replace("{Barbeiro}", appt.Barber?.Name ?? "Profissional")
            .Replace("{Servico}", appt.ServiceItem?.Name ?? "Atendimento");

        // Pesquisa curta: uma pergunta com nota de 1 a 5 (comentário opcional depois)
        var meta = new List<PollOptionMeta>
        {
            new("⭐⭐⭐⭐⭐ Excelente", "EVAL_RATE", $"{appt.Id}|5"),
            new("⭐⭐⭐⭐ Bom", "EVAL_RATE", $"{appt.Id}|4"),
            new("⭐⭐⭐ Regular", "EVAL_RATE", $"{appt.Id}|3"),
            new("⭐⭐ Ruim", "EVAL_RATE", $"{appt.Id}|2"),
            new("⭐ Péssimo", "EVAL_RATE", $"{appt.Id}|1")
        };

        var pollMsgId = await SendOutboundPollAsync(session, question, meta, ct);

        appt.EvalPollSentAtUtc = DateTimeOffset.UtcNow;
        appt.EvalPollMessageId = pollMsgId;
        if (appt.Status == "Confirmed")
        {
            appt.Status = "Completed";
        }

        db.Appointments.Update(appt);
        await db.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Horários livres do dia (regra unificada do AvailabilityService, considerando a duração do serviço escolhido).</summary>
    private Task<IReadOnlyList<string>> GetAvailableTimeSlotsForDateAsync(
        Store store,
        int selectedBarberId,
        DateOnly date,
        CancellationToken ct,
        int? serviceId = null,
        long? excludeAppointmentId = null)
        => availability.GetFreeSlotsAsync(store.Id, selectedBarberId, date.ToString("yyyy-MM-dd"), serviceId, ct, excludeAppointmentId);

    private Task<Barber?> ResolveAvailableBarberForSlotAsync(
        int storeId,
        int selectedBarberId,
        string dateIso,
        string slotStr,
        CancellationToken ct,
        int? serviceId = null,
        long? excludeAppointmentId = null)
        => availability.ResolveBarberAsync(storeId, selectedBarberId, dateIso, slotStr, serviceId, ct, excludeAppointmentId);

    private static bool IsSlotInLunchBreak(string? lunchStart, string? lunchEnd, int slotMinutes)
    {
        if (string.IsNullOrWhiteSpace(lunchStart) || string.IsNullOrWhiteSpace(lunchEnd))
        {
            return false;
        }

        if (TimeOnly.TryParseExact(lunchStart.Trim(), "HH:mm", out var ls) &&
            TimeOnly.TryParseExact(lunchEnd.Trim(), "HH:mm", out var le))
        {
            int startMin = ls.Hour * 60 + ls.Minute;
            int endMin = le.Hour * 60 + le.Minute;
            if (endMin > startMin)
            {
                return slotMinutes >= startMin && slotMinutes < endMin;
            }
        }

        return false;
    }

    private async Task<string> SendOutboundPollAsync(
        ConversationSession session,
        string question,
        IReadOnlyList<PollOptionMeta> optionsMeta,
        CancellationToken ct)
    {
        question = ApplyStoreVariables(question);
        var optionLabels = optionsMeta.Select(o => o.Label).ToList();
        var jid = FormatWhatsAppJid(session.CustomerPhoneJid);
        var resp = await bridgeClient.SendPollAsync(jid, question, optionLabels, ct);

        session.ActivePollMessageId = resp.MessageId;
        session.ActivePollMetadataJson = JsonSerializer.Serialize(optionsMeta);
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;

        AppendHistory(session, "outbound", "poll", question, resp.MessageId, optionLabels, null);
        db.ConversationSessions.Update(session);
        return resp.MessageId;
    }

    private async Task SendOutboundTextAsync(
        ConversationSession session,
        string text,
        CancellationToken ct)
    {
        text = ApplyStoreVariables(text);
        var jid = FormatWhatsAppJid(session.CustomerPhoneJid);
        var resp = await bridgeClient.SendTextAsync(jid, text, ct);

        session.UpdatedAtUtc = DateTimeOffset.UtcNow;
        AppendHistory(session, "outbound", "text", text, resp.MessageId, null, null);
        db.ConversationSessions.Update(session);
    }

    private async Task<ConversationSession> GetOrCreateSessionAsync(int storeId, string phone, CancellationToken ct)
    {
        var canonicalPhone = NormalizePhone(phone);
        var phoneVariants = PhoneIdentity.Variants(phone);
        var session = await db.ConversationSessions
            .AsTracking()
            .Where(s => s.StoreId == storeId && (s.CustomerPhoneJid == canonicalPhone || phoneVariants.Contains(s.CustomerPhoneJid)))
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);

        if (session is null)
        {
            session = new ConversationSession
            {
                StoreId = storeId,
                CustomerPhoneJid = canonicalPhone,
                CurrentStep = "IDLE",
                UpdatedAtUtc = DateTimeOffset.UtcNow
            };
            db.ConversationSessions.Add(session);
            await db.SaveChangesAsync(ct);
        }
        else if (session.CustomerPhoneJid != canonicalPhone)
        {
            session.CustomerPhoneJid = canonicalPhone;
        }

        return session;
    }

    private async Task<Appointment> FindOrCreateSampleAppointmentForPhoneAsync(int storeId, string phone, CancellationToken ct)
    {
        var customer = await customers.FindAsync(storeId, phone, tracking: true, ct);

        if (customer is null)
        {
            customer = new Customer
            {
                StoreId = storeId,
                Name = "Cliente Teste WhatsApp",
                Phone = PhoneIdentity.Canonical(phone) is { Length: > 0 } canonSample ? canonSample : phone,
                TotalAppointments = 1,
                LastAppointmentAt = DateTimeOffset.UtcNow
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(ct);
        }

        var latestAppt = await db.Appointments
            .Where(a => a.StoreId == storeId && a.CustomerId == customer.Id && a.Status != "Cancelled")
            .OrderByDescending(a => a.Id)
            .FirstOrDefaultAsync(ct);

        if (latestAppt is not null)
        {
            return latestAppt;
        }

        var barber = await db.Barbers.Where(b => b.StoreId == storeId && b.IsActive).FirstAsync(ct);
        var service = await db.ServiceItems.Where(s => s.StoreId == storeId && s.IsActive).OrderBy(s => s.DisplayOrder).FirstAsync(ct);

        var newAppt = new Appointment
        {
            StoreId = storeId,
            CustomerId = customer.Id,
            BarberId = barber.Id,
            ServiceItemId = service.Id,
            AppointmentDate = DateOnly.FromDateTime(DateTime.Now).ToString("yyyy-MM-dd"),
            StartTime = "14:00",
            PriceCharged = service.Price,
            Status = "Confirmed",
            Source = "WhatsAppBot"
        };
        db.Appointments.Add(newAppt);
        await db.SaveChangesAsync(ct);
        return newAppt;
    }

    private static void ResetSessionToIdle(ConversationSession session)
    {
        session.CurrentStep = "IDLE";
        session.ActivePollMessageId = null;
        session.ActivePollMetadataJson = null;
        session.ReschedulingAppointmentId = null;
        session.ServicePageOffset = 0;
        session.TimePageOffset = 0;
        session.UpdatedAtUtc = DateTimeOffset.UtcNow;
    }

    private static string InferActionCodeFromLabel(string currentStep, string label)
    {
        var l = label.Trim();
        if (l.Contains("Encerrar", StringComparison.OrdinalIgnoreCase)) return "CLOSE";
        if (l.Contains("Falar com a equipe", StringComparison.OrdinalIgnoreCase)) return "TALK_HUMAN";
        if (l.Contains("Remarcar meu horário", StringComparison.OrdinalIgnoreCase)) return "GOTO_RESCHEDULE";
        if (l.Contains("Cancelar meu horário", StringComparison.OrdinalIgnoreCase)) return "GOTO_CANCEL";
        if (l.Contains("Alterar meu nome", StringComparison.OrdinalIgnoreCase)) return "CHANGE_NAME";
        if (l.Contains("Cancelar", StringComparison.OrdinalIgnoreCase)) return "CANCEL_BOOKING";
        if (l.Contains("Confirmar", StringComparison.OrdinalIgnoreCase)) return "CONFIRM_BOOKING";
        if (l.Contains("Mais opções", StringComparison.OrdinalIgnoreCase))
        {
            return currentStep == "STEP_2_SERVICE" ? "MORE_SERVICES" : "MORE_TIMES";
        }
        if (l.Contains("Voltar", StringComparison.OrdinalIgnoreCase))
        {
            return currentStep switch
            {
                "STEP_2_SERVICE" => "BACK_TO_WELCOME",
                "STEP_2B_BARBER" => "BACK_TO_SERVICES",
                "STEP_3_DATE" => "BACK_FROM_DATE",
                "STEP_4_TIME" => "BACK_TO_DATE",
                "STEP_6_CONFIRM" => "BACK_TO_TIME",
                _ => "BACK_TO_WELCOME"
            };
        }
        if (l.Contains("Agendamento", StringComparison.OrdinalIgnoreCase)) return "GOTO_SERVICES";
        return "UNKNOWN";
    }

    private static List<PollOptionMeta> DeserializePollMeta(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<PollOptionMeta>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static List<BotChatHistoryItem> DeserializeHistory(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<BotChatHistoryItem>>(json) ?? [];
        }
        catch
        {
            return [];
        }
    }

    private static void AppendHistory(
        ConversationSession session,
        string direction,
        string kind,
        string text,
        string? pollMessageId,
        IReadOnlyList<string>? pollOptions,
        string? selectedOption)
    {
        var list = DeserializeHistory(session.LastBotHistoryJson);
        list.Add(new BotChatHistoryItem(
            Guid.NewGuid().ToString("N")[..12],
            direction,
            kind,
            text,
            pollMessageId,
            pollOptions,
            selectedOption,
            DateTimeOffset.Now.ToString("HH:mm:ss")));

        if (list.Count > 35)
        {
            list = list.Skip(list.Count - 35).ToList();
        }

        session.LastBotHistoryJson = JsonSerializer.Serialize(list);
    }

    private static void MarkPollOptionSelectedInHistory(ConversationSession session, string pollMessageId, string selectedOption)
    {
        var list = DeserializeHistory(session.LastBotHistoryJson);
        for (int i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i];
            if (item.Kind == "poll" &&
                (string.IsNullOrWhiteSpace(pollMessageId) || item.PollMessageId == pollMessageId))
            {
                list[i] = item with { SelectedOption = selectedOption };
                break;
            }
        }
        session.LastBotHistoryJson = JsonSerializer.Serialize(list);
    }

    private static string NormalizePhone(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "5511999990000";
        var canon = PhoneIdentity.Canonical(raw);
        if (!string.IsNullOrEmpty(canon)) return canon;
        var beforeAt = raw.Split('@')[0].Split(':')[0];
        var digits = new string(beforeAt.Where(char.IsDigit).ToArray());
        return string.IsNullOrEmpty(digits) ? "5511999990000" : digits;
    }

    private static string FormatWhatsAppJid(string phone)
    {
        var digits = NormalizePhone(phone);
        return $"{digits}@s.whatsapp.net";
    }

    private static string? ResolveBarberWhatsAppJid(string? barberPhone, string? storeConnectedPhone)
    {
        var barberDigits = new string((barberPhone ?? "").Where(char.IsDigit).ToArray());
        // Se o telefone do funcionário ainda for o placeholder inicial de seed ou vazio, usa o número conectado da loja
        if ((string.IsNullOrWhiteSpace(barberDigits) || barberDigits.EndsWith("11998112200", StringComparison.Ordinal))
            && !string.IsNullOrWhiteSpace(storeConnectedPhone))
        {
            var connectedDigits = new string(storeConnectedPhone.Where(char.IsDigit).ToArray());
            if (connectedDigits.Length >= 10)
            {
                barberDigits = connectedDigits;
            }
        }

        if (barberDigits.Length < 10)
        {
            return null;
        }

        if (barberDigits.Length is 10 or 11 && !barberDigits.StartsWith("55", StringComparison.Ordinal))
        {
            barberDigits = $"55{barberDigits}";
        }

        return $"{barberDigits}@s.whatsapp.net";
    }

    private static HashSet<int> ParseWorkingDays(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv)) return [1, 2, 3, 4, 5, 6];
        var set = new HashSet<int>();
        foreach (var part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int d)) set.Add(d);
        }
        return set.Count > 0 ? set : [1, 2, 3, 4, 5, 6];
    }

    private static int ParseMinutes(string? hhmm, int fallback)
    {
        if (!string.IsNullOrWhiteSpace(hhmm) && TimeOnly.TryParseExact(hhmm.Trim(), "HH:mm", out var t))
        {
            return t.Hour * 60 + t.Minute;
        }
        return fallback;
    }

    private static bool TryParseAppointmentDateTime(string dateIso, string timeHhMm, out DateTime result)
    {
        if (DateOnly.TryParseExact(dateIso, "yyyy-MM-dd", out var d) &&
            TimeOnly.TryParseExact(timeHhMm, "HH:mm", out var t))
        {
            result = d.ToDateTime(t);
            return true;
        }
        result = default;
        return false;
    }
}

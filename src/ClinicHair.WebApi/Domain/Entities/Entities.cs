namespace ClinicHair.WebApi.Domain.Entities;

/// <summary>
/// Representa uma unidade/loja da barbearia (Multi-Tenant ready, iniciando com Clinic Hair).
/// </summary>
public sealed class Store
{
    public int Id { get; set; }
    public string Name { get; set; } = "Clinic Hair";
    public string OpeningTime { get; set; } = "08:00";
    public string ClosingTime { get; set; } = "20:00";
    public string WorkingDaysCsv { get; set; } = "1,2,3,4,5,6"; // Seg a Sáb
    public int EvaluationRetentionDays { get; set; } = 30;

    // Templates personalizáveis das enquetes e mensagens do WhatsApp
    public string WelcomePollTitle { get; set; } = "Seja bem-vindo à Clinic Hair! Escolha o que fará hoje:";
    public string ServicePollTitle { get; set; } = "Qual vai ser o serviço?";
    public string BarberPollTitle { get; set; } = "Com qual profissional você prefere agendar?";
    public string DatePollTitle { get; set; } = "Escolha o dia para o agendamento:";
    public string TimePollTitle { get; set; } = "Escolha a hora:";
    public string AskNameTodayTemplate { get; set; } = "Entendi! Então para hoje às {Hora}, pode me dizer o seu nome para prosseguir com o agendamento?";
    public string AskNameOtherDayTemplate { get; set; } = "Entendi! Então para o dia {Data} às {Hora}, pode me dizer o seu nome para prosseguir com o agendamento?";
    public string ConfirmationPollTemplate { get; set; } = "Entendi, {Nome}! Então está agendado para {Data}, na hora {Hora}, com {Funcionario}, no custo de R$ {Valor} ({Servico}).";
    public string ConfirmedMessageTemplate { get; set; } = "Entendi! Seu agendamento com {Funcionario} está confirmado. Te aviso faltando uma hora!";
    public string Reminder1hTemplate { get; set; } = "Olá {Nome}! Lembrete da Clinic Hair: seu atendimento ({Servico}) com {Funcionario} é daqui a 1 hora, às {Hora}!";
    public string Evaluation2hPollTitle { get; set; } = "Olá {Nome}! Como foi seu atendimento na Clinic Hair hoje? Deseja avaliar?";
    public string EvaluationWindowOpenMessage { get; set; } = "Muito obrigado! Pelos próximos 60 minutos, toda mensagem que você enviar aqui será registrada na sua avaliação para nossa equipe.";

    // Estado do Bridge WhatsApp
    public string WhatsAppStatus { get; set; } = "DISCONNECTED";
    public string? ConnectedPhone { get; set; }

    // Pausa manual do bot: quando true, o bot não responde clientes nem envia lembretes/avaliações automáticas
    public bool BotPaused { get; set; } = false;
    public string? LastQrCodeData { get; set; }
    public string? LastPairingCode { get; set; }

    // Gestão SaaS / SuperAdministrador (Assinatura, Plano e Status da Loja)
    public string SubscriptionPlan { get; set; } = "Plano Pro WhatsApp";
    public decimal MonthlyFee { get; set; } = 149.90m;
    public string SubscriptionStatus { get; set; } = "Active"; // Active, Pending, Overdue, Suspended
    public string NextDueDate { get; set; } = DateTime.Now.AddDays(30).ToString("yyyy-MM-dd");
    public bool IsActive { get; set; } = true;
    public string? OwnerPhone { get; set; }
    public string? BillingNotes { get; set; }

    // Automação do bot (pausa por horário, avisos, endereço, lembretes, anti-spam)
    public bool BotScheduleEnabled { get; set; }
    public string? BotPauseStart { get; set; }                 // "HH:mm" — início da janela diária de pausa
    public string? BotPauseEnd { get; set; }                   // "HH:mm" — fim da janela diária de pausa
    public bool BotPauseOutsideOpeningHours { get; set; }
    public string? WelcomeMessage { get; set; }
    public string? HolidayNoticeMessage { get; set; }
    public string? HolidayNoticeUntil { get; set; }            // "yyyy-MM-dd"
    public string? Address { get; set; }
    public string? MapLink { get; set; }
    public bool Reminder24hEnabled { get; set; } = true;
    public string Reminder24hTemplate { get; set; } = "Olá {Nome}! Passando para lembrar do seu horário amanhã, {Data} às {Hora}, com {Barbeiro} ({Servico}).";
    public int ReminderResendHours { get; set; } = 4;
    public int MinCancelHours { get; set; }                    // 0 = sem restrição de antecedência
    public int AntiSpamMaxPerMinute { get; set; } = 8;

    // Relacionamento com clientes
    public bool BirthdayEnabled { get; set; }
    public string BirthdayTemplate { get; set; } = "Feliz aniversário, {Nome}! 🎉 A equipe {Loja} deseja um ótimo dia. Venha comemorar com a gente!";
    public bool MonthlyReportEnabled { get; set; } = true;
    public string? LastMonthlyReportSent { get; set; }         // "yyyy-MM"

    // Metas mensais
    public decimal MonthlyRevenueGoal { get; set; }
    public int MonthlyAppointmentsGoal { get; set; }

    // Página pública, PIX, fidelidade, idioma e white-label (itens 59, 66, 94, 96–100)
    public string? PublicSlug { get; set; }                    // usado em /p/{slug}
    public bool PublicBookingEnabled { get; set; }
    public int BookingHorizonDays { get; set; } = 14;          // até quantos dias à frente o público agenda
    public string? PixKey { get; set; }
    public string? PixMerchantName { get; set; }
    public string? PixMerchantCity { get; set; }
    public bool OnlinePixEnabled { get; set; }
    public bool LoyaltyEnabled { get; set; }
    public decimal LoyaltyPointsPerReal { get; set; } = 1m;
    public int LoyaltyRewardPoints { get; set; } = 300;
    public string LoyaltyRewardDescription { get; set; } = "1 corte grátis";
    public string Language { get; set; } = "pt-BR";
    public string? BrandColor { get; set; }
    public string? LogoUrl { get; set; }
    public string? BrandDisplayName { get; set; }
    public int SubscriptionGraceDays { get; set; } = 3;
    public int RenewalReminderDays { get; set; } = 3;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Representa um administrador da Dashboard (ex.: Itamar).
/// </summary>
public sealed class AdminUser
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Username { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string Role { get; set; } = "Admin";
    public bool MustChangePassword { get; set; }
    public int FailedLoginCount { get; set; }
    public DateTimeOffset? LockoutEndUtc { get; set; }
    public int SessionVersion { get; set; }                    // incrementado na troca de senha: invalida todas as sessões
    public DateTimeOffset? PasswordChangedAt { get; set; }
    public string? TotpSecret { get; set; }                    // Base32; segundo fator (obrigatório só se TotpEnabled)
    public bool TotpEnabled { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Token JWT revogado no logout (válido até expirar naturalmente).</summary>
public sealed class RevokedToken
{
    public string Jti { get; set; } = string.Empty;
    public DateTimeOffset ExpiresAtUtc { get; set; }
}

/// <summary>
/// Representa um funcionário/barbeiro registrado na loja, com seu horário individual e limite de 3 agendamentos por hora.
/// </summary>
public sealed class Barber
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Specialty { get; set; } = "Barbeiro Master";
    public string WorkStartTime { get; set; } = "08:00";
    public string WorkEndTime { get; set; } = "19:00";
    public string? LunchStartTime { get; set; } = "12:00"; // Horário de almoço/pausa individual (início)
    public string? LunchEndTime { get; set; } = "13:00";   // Horário de almoço/pausa individual (fim)
    public decimal CommissionPercentage { get; set; } = 50.0m; // Comissão (%) por atendimento
    public string WorkingDaysCsv { get; set; } = "1,2,3,4,5,6";
    public int MaxConcurrentPerSlot { get; set; } = 3; // Regra de negócio: máximo 3 agendamentos para a mesma hora
    public string? CalendarIcsUrl { get; set; }        // agenda externa (Google Calendar ICS secreto) lida como "ocupado"
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Representa um produto ou serviço ofertado nas enquetes do WhatsApp e na Agenda (com cor saturada para o gradiente até branco).
/// </summary>
public sealed class ServiceItem
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public int DurationMinutes { get; set; } = 30;
    public string ColorHex { get; set; } = "#FF6B00"; // Ex: Laranja saturado para Corte R$ 30,00
    public int DisplayOrder { get; set; } = 1;
    public bool IsActive { get; set; } = true;
}

/// <summary>
/// Representa um dia de não funcionamento (marcado em amarelo na sub-aba Calendário).
/// </summary>
public sealed class StoreNonWorkingDay
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string BlockedDate { get; set; } = string.Empty; // Formato ISO "YYYY-MM-DD"
    public string Reason { get; set; } = "Dia de Não Funcionamento";
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Representa um cliente no CRM da barbearia.
/// </summary>
public sealed class Customer
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public int TotalAppointments { get; set; }
    public DateTimeOffset LastAppointmentAt { get; set; } = DateTimeOffset.UtcNow;
    public string? BirthDate { get; set; }                     // "yyyy-MM-dd" (ano pode ser 1900 quando desconhecido)
    public string MarketingConsent { get; set; } = "Unknown";  // Unknown, OptIn, OptOut
    public DateTimeOffset? ConsentUpdatedAt { get; set; }
    public string? InternalNotes { get; set; }
    public bool IsAnonymized { get; set; }
    public int? LastBirthdayGreetingYear { get; set; }
    public int LoyaltyPoints { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Representa um agendamento (realizado via Bot WhatsApp ou Manualmente na Dashboard).
/// </summary>
public sealed class Appointment
{
    public long Id { get; set; }
    public int StoreId { get; set; }
    public int CustomerId { get; set; }
    public int BarberId { get; set; }
    public int ServiceItemId { get; set; }
    public string AppointmentDate { get; set; } = string.Empty; // "YYYY-MM-DD"
    public string StartTime { get; set; } = string.Empty;       // "HH:mm" (de 30 em 30 min)
    public decimal PriceCharged { get; set; }
    public string PaymentMethod { get; set; } = "PIX";          // PIX, Cartão, Dinheiro
    public string Status { get; set; } = "Confirmed";           // Confirmed, Completed, Cancelled
    public string Source { get; set; } = "WhatsAppBot";         // WhatsAppBot, Manual
    public string? Notes { get; set; }
    public DateTimeOffset? ReminderSentAtUtc { get; set; }
    public string? ReminderPollMessageId { get; set; }
    public DateTimeOffset? EvalPollSentAtUtc { get; set; }
    public string? EvalPollMessageId { get; set; }
    public DateTimeOffset? Reminder24hSentAtUtc { get; set; }
    public string? Reminder24hPollMessageId { get; set; }
    public DateTimeOffset? Reminder24hResentAtUtc { get; set; }
    public DateTimeOffset? CustomerConfirmedAtUtc { get; set; }
    public DateTimeOffset? ArrivedAtUtc { get; set; }          // Check-in: cliente chegou
    public DateTimeOffset? ServiceStartedAtUtc { get; set; }   // Check-in: em atendimento
    public DateTimeOffset? CompletedAtUtc { get; set; }        // Check-in: concluído
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public Customer? Customer { get; set; }
    public Barber? Barber { get; set; }
    public ServiceItem? ServiceItem { get; set; }
}

/// <summary>
/// Representa o estado atual da máquina de estados de conversação por enquetes de um cliente no WhatsApp.
/// </summary>
public sealed class ConversationSession
{
    public long Id { get; set; }
    public int StoreId { get; set; }
    public string CustomerPhoneJid { get; set; } = string.Empty; // Ex: "5511991234501@s.whatsapp.net" ou apenas dígitos
    public string CurrentStep { get; set; } = "IDLE";            // IDLE, STEP_1_WELCOME, STEP_2_SERVICE, STEP_2B_BARBER, STEP_3_DATE, STEP_4_TIME, STEP_5_ASK_NAME, STEP_6_CONFIRM, COLLECTING_EVALUATION
    public int? SelectedServiceId { get; set; }
    public int? SelectedBarberId { get; set; }                   // 0 = "Agendar com qualquer um", >0 = Barbeiro específico
    public int? AssignedBarberId { get; set; }                   // Barbeiro efetivamente alocado para o horário escolhido
    public string? SelectedDate { get; set; }                    // "YYYY-MM-DD"
    public string? SelectedTime { get; set; }                    // "HH:mm"
    public int ServicePageOffset { get; set; }
    public int TimePageOffset { get; set; }
    public string? CustomerName { get; set; }
    public string? ActivePollMessageId { get; set; }             // Stale Poll Guard: ID da enquete ativa atual
    public string? ActivePollMetadataJson { get; set; }          // Mapeamento determinístico das opções da enquete ativa
    public long? ReschedulingAppointmentId { get; set; }         // ID do agendamento sendo remarcado via autoatendimento 2.0
    public long? EvaluationAppointmentId { get; set; }           // Agendamento em avaliação dentro da janela de 1 hora
    public DateTimeOffset? EvaluationWindowExpiresAtUtc { get; set; }
    public string? LastBotHistoryJson { get; set; }              // Histórico recente da conversa para visualização/simulador
    public bool HumanTakeover { get; set; }                      // Atendente assumiu: bot não responde esta conversa
    public DateTimeOffset? HumanTakeoverUntilUtc { get; set; }
    public bool AwaitingHuman { get; set; }                      // Fila de atendimento humano
    public DateTimeOffset? AwaitingHumanSinceUtc { get; set; }
    public DateTimeOffset? LastInboundAtUtc { get; set; }
    public string? LastNoticeDate { get; set; }                  // "yyyy-MM-dd" do último aviso de feriado enviado
    public int MisunderstoodCount { get; set; }
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Representa uma avaliação pós-atendimento capturada em arquivo .txt dentro da janela de 1 hora.
/// </summary>
public sealed class Evaluation
{
    public long Id { get; set; }
    public int StoreId { get; set; }
    public long AppointmentId { get; set; }
    public int CustomerId { get; set; }
    public int BarberId { get; set; }
    public int ServiceItemId { get; set; }
    public string TxtFileName { get; set; } = string.Empty;
    public string TxtRelativePath { get; set; } = string.Empty;
    public string ConsolidatedText { get; set; } = string.Empty;
    public int MessageCount { get; set; } = 1;
    public long ByteSize { get; set; }
    public bool IsTxtCompacted { get; set; }
    public string? Sentiment { get; set; } // "Positive" | "Negative" | null
    public int? Rating { get; set; }       // Nota de 1 a 5 da pesquisa curta de satisfação
    public DateTimeOffset WindowExpiresAtUtc { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Tabela de controle de Idempotência para Webhooks e Enquetes do WhatsApp.
/// </summary>
public sealed class InboxWebhookEvent
{
    public string EventId { get; set; } = string.Empty; // SHA-256 determinístico
    public string EventType { get; set; } = string.Empty;
    public string SenderJid { get; set; } = string.Empty;
    public DateTimeOffset ProcessedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>
/// Registro de cobrança de assinatura SaaS de uma loja gerenciada pelo SuperAdministrador (Patricio).
/// </summary>
public sealed class StoreBillingRecord
{
    public long Id { get; set; }
    public int StoreId { get; set; }
    public string StoreName { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public string DueDate { get; set; } = string.Empty; // "YYYY-MM-DD"
    public string Status { get; set; } = "Pending";     // Pending, Paid, Overdue, Cancelled
    public string Description { get; set; } = "Assinatura Mensal - Sistema WhatsApp";
    public string? PaymentLinkOrPix { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? PaidAt { get; set; }
}

/// <summary>
/// Registro de log direto do sistema para auditoria e monitoramento em tempo real pelo SuperAdministrador.
/// </summary>
public sealed class SystemLogEntry
{
    public long Id { get; set; }
    public string Level { get; set; } = "INFO";         // INFO, WARN, ERROR, SECURITY, WHATSAPP, BILLING, STORE
    public string Category { get; set; } = "System";
    public int? StoreId { get; set; }
    public string? StoreName { get; set; }
    public string Message { get; set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

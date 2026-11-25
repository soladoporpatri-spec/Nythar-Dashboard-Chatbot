using System.ComponentModel.DataAnnotations;

namespace ClinicHair.WebApi.Contracts;

/// <summary>Credenciais de login do administrador da Dashboard.</summary>
public sealed record LoginRequest
{
    [Required, MaxLength(100)]
    public required string Username { get; init; }

    [Required, MaxLength(100)]
    public required string Password { get; init; }

    /// <summary>Código de 6 dígitos do aplicativo autenticador (obrigatório quando o segundo fator está ativo).</summary>
    [MaxLength(10)]
    public string? TotpCode { get; init; }
}

/// <summary>Resposta de autenticação da Dashboard (suporta Admin de Loja e SuperAdmin Patricio).</summary>
public sealed record AuthResponse(
    string Token,
    string Username,
    string StoreName,
    DateTimeOffset ExpiresAt,
    string Role = "Admin",
    int StoreId = 1,
    bool MustChangePassword = false);

/// <summary>Representa um agendamento exibido na aba Agenda (com cor do serviço para gradiente até branco).</summary>
public sealed record AppointmentResponse(
    long Id,
    string AppointmentDate,
    string StartTime,
    int CustomerId,
    string CustomerName,
    string CustomerPhone,
    int BarberId,
    string BarberName,
    int ServiceItemId,
    string ServiceName,
    decimal PriceCharged,
    string ServiceColorHex,
    string Status,
    string Source,
    string? Notes,
    int SlotOccupancyCount,
    int BarberMaxPerSlot,
    string PaymentMethod = "PIX");

/// <summary>Payload para criação de agendamento manual na aba Agenda.</summary>
public sealed record CreateManualAppointmentRequest
{
    [Required, MaxLength(120)]
    public required string CustomerName { get; init; }

    [Required, MaxLength(40)]
    public required string CustomerPhone { get; init; }

    [Required]
    public required int BarberId { get; init; }

    [Required]
    public required int ServiceItemId { get; init; }

    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public required string AppointmentDate { get; init; }

    [Required, RegularExpression(@"^\d{2}:\d{2}$")]
    public required string StartTime { get; init; }

    [MaxLength(30)]
    public string PaymentMethod { get; init; } = "PIX";

    [MaxLength(500)]
    public string? Notes { get; init; }
}

/// <summary>Payload para atualizar o status e/ou meio de pagamento de um agendamento.</summary>
public sealed record UpdateAppointmentStatusRequest
{
    [Required, MaxLength(30)]
    public required string Status { get; init; }

    [MaxLength(30)]
    public string? PaymentMethod { get; init; }
}

/// <summary>Resumo de um dia no Calendário mensal (agendamentos ou dia de não funcionamento em amarelo).</summary>
public sealed record CalendarDaySummaryResponse(
    string Date,
    int TotalAppointments,
    bool IsNonWorkingDay,
    string? NonWorkingReason);

/// <summary>Payload para alternar ou definir um dia de não funcionamento (cor amarela no calendário).</summary>
public sealed record ToggleNonWorkingDayRequest
{
    [Required, RegularExpression(@"^\d{4}-\d{2}-\d{2}$")]
    public required string Date { get; init; }

    [MaxLength(200)]
    public string? Reason { get; init; }
}

/// <summary>Card de cliente para a sub-aba Clientes do CRM.</summary>
public sealed record CustomerCrmResponse(
    int Id,
    string Name,
    string Phone,
    int TotalAppointments,
    int EvaluationCount,
    DateTimeOffset LastAppointmentAt);

/// <summary>Avaliação de cliente vinculada (exibida ao clicar no botão azul escuro "Avaliações" no card do cliente).</summary>
public sealed record CustomerEvaluationDetailResponse(
    long Id,
    long AppointmentId,
    string AppointmentDate,
    string StartTime,
    string BarberName,
    string ServiceName,
    string TxtFileName,
    string ConsolidatedText,
    long ByteSize,
    bool IsTxtCompacted,
    string? Sentiment,
    DateTimeOffset CreatedAt);

/// <summary>Avaliação para a sub-aba Avaliações do CRM (sem indicar o cliente).</summary>
public sealed record AnonymousEvaluationResponse(
    long Id,
    string AppointmentDate,
    string BarberName,
    string ServiceName,
    string TxtFileName,
    string ConsolidatedText,
    long ByteSize,
    bool IsTxtCompacted,
    string? Sentiment,
    DateTimeOffset CreatedAt);

/// <summary>Payload para classificar uma avaliação como Positiva ou Negativa.</summary>
public sealed record UpdateEvaluationSentimentRequest
{
    [Required, MaxLength(20)]
    public required string Sentiment { get; init; }
}

/// <summary>Estatísticas de armazenamento dos arquivos .txt de avaliação.</summary>
public sealed record EvaluationStorageStatsResponse(
    int TotalEvaluations,
    int ActiveTxtFilesOnDisk,
    int CompactedEvaluations,
    long TotalBytesOnDisk,
    int RetentionDays);

/// <summary>Dados consolidados da seção Relatórios (faturamento, desempenho por barbeiro, pagamentos e horários).</summary>
public sealed record DashboardReportsResponse(
    string BusiestDayOfWeek,
    string BusiestDateRecord,
    int BusiestDateCount,
    string PeakHourSlot,
    int PeakHourCount,
    double AverageEvaluationsPerCustomer,
    decimal TotalConfirmedRevenue,
    decimal TotalCompletedRevenue,
    decimal AverageTicket,
    int TotalAppointmentsCount,
    IReadOnlyList<BarberPerformanceItem> BarberPerformances,
    IReadOnlyList<PeakHourItem> HourlyDistribution,
    IReadOnlyList<ServiceRevenueItem> ServiceBreakdown,
    IReadOnlyList<PaymentMethodBreakdownItem>? PaymentMethodBreakdown = null);

/// <summary>Desempenho individual por barbeiro nos relatórios (usado também na exportação Excel).</summary>
public sealed record BarberPerformanceItem(
    int BarberId,
    string BarberName,
    string WorkingHours,
    int TotalAppointments,
    int CompletedAppointments,
    decimal RevenueGenerated,
    int EvaluationsReceived);

/// <summary>Resumo financeiro por Meio de Pagamento (PIX, Cartão, Dinheiro).</summary>
public sealed record PaymentMethodBreakdownItem(
    string PaymentMethod,
    int Count,
    decimal TotalAmount);

/// <summary>Distribuição por horário de pico (30 em 30 min).</summary>
public sealed record PeakHourItem(
    string TimeSlot,
    int AppointmentCount);

/// <summary>Receita e volume por serviço.</summary>
public sealed record ServiceRevenueItem(
    int ServiceId,
    string ServiceName,
    string ColorHex,
    int Count,
    decimal TotalRevenue);

/// <summary>Dados de um funcionário/barbeiro na sub-aba Funcionários.</summary>
public sealed record BarberResponse(
    int Id,
    string Name,
    string Phone,
    string Specialty,
    string WorkStartTime,
    string WorkEndTime,
    string WorkingDaysCsv,
    int MaxConcurrentPerSlot,
    bool IsActive,
    string? LunchStartTime = "12:00",
    string? LunchEndTime = "13:00");

/// <summary>Payload para criar ou atualizar funcionário na sub-aba Funcionários.</summary>
public sealed record UpsertBarberRequest
{
    [Required, MaxLength(120)]
    public required string Name { get; init; }

    [MaxLength(40)]
    public string Phone { get; init; } = string.Empty;

    [MaxLength(100)]
    public string Specialty { get; init; } = "Barbeiro";

    [Required, RegularExpression(@"^\d{2}:\d{2}$")]
    public required string WorkStartTime { get; init; }

    [Required, RegularExpression(@"^\d{2}:\d{2}$")]
    public required string WorkEndTime { get; init; }

    [MaxLength(10)]
    public string? LunchStartTime { get; init; } = "12:00";

    [MaxLength(10)]
    public string? LunchEndTime { get; init; } = "13:00";

    [Required, MaxLength(30)]
    public string WorkingDaysCsv { get; init; } = "1,2,3,4,5,6";

    [Range(1, 10)]
    public int MaxConcurrentPerSlot { get; init; } = 3;

    public bool IsActive { get; init; } = true;
}

/// <summary>Dados de um produto/serviço que alimenta as enquetes do WhatsApp e os cards da Agenda.</summary>
public sealed record ServiceItemResponse(
    int Id,
    string Name,
    decimal Price,
    int DurationMinutes,
    string ColorHex,
    int DisplayOrder,
    bool IsActive);

/// <summary>Payload para criar ou editar um produto/serviço.</summary>
public sealed record UpsertServiceItemRequest
{
    [Required, MaxLength(120)]
    public required string Name { get; init; }

    [Range(1, 10000)]
    public required decimal Price { get; init; }

    [Range(15, 240)]
    public int DurationMinutes { get; init; } = 30;

    [Required, RegularExpression(@"^#[0-9A-Fa-f]{6}$")]
    public required string ColorHex { get; init; }

    public int DisplayOrder { get; init; } = 1;
    public bool IsActive { get; init; } = true;
}

/// <summary>Configurações gerais da loja Clinic Hair e textos das enquetes do WhatsApp.</summary>
public sealed record StoreSettingsResponse(
    int Id,
    string Name,
    string OpeningTime,
    string ClosingTime,
    string WorkingDaysCsv,
    int EvaluationRetentionDays,
    string WelcomePollTitle,
    string ServicePollTitle,
    string BarberPollTitle,
    string DatePollTitle,
    string TimePollTitle,
    string AskNameTodayTemplate,
    string AskNameOtherDayTemplate,
    string ConfirmationPollTemplate,
    string ConfirmedMessageTemplate,
    string Reminder1hTemplate,
    string Evaluation2hPollTitle,
    string EvaluationWindowOpenMessage);

/// <summary>Payload para salvar configurações gerais da loja e mensagens das enquetes.</summary>
public sealed record UpdateStoreSettingsRequest
{
    [Required, MaxLength(120)]
    public required string Name { get; init; }

    [Required, RegularExpression(@"^\d{2}:\d{2}$")]
    public required string OpeningTime { get; init; }

    [Required, RegularExpression(@"^\d{2}:\d{2}$")]
    public required string ClosingTime { get; init; }

    [Required, MaxLength(30)]
    public required string WorkingDaysCsv { get; init; }

    [Range(1, 365)]
    public int EvaluationRetentionDays { get; init; } = 30;

    [Required, MaxLength(300)]
    public required string WelcomePollTitle { get; init; }

    [Required, MaxLength(300)]
    public required string ServicePollTitle { get; init; }

    [Required, MaxLength(300)]
    public required string BarberPollTitle { get; init; }

    [Required, MaxLength(300)]
    public required string DatePollTitle { get; init; }

    [Required, MaxLength(300)]
    public required string TimePollTitle { get; init; }

    [Required, MaxLength(500)]
    public required string AskNameTodayTemplate { get; init; }

    [Required, MaxLength(500)]
    public required string AskNameOtherDayTemplate { get; init; }

    [Required, MaxLength(500)]
    public required string ConfirmationPollTemplate { get; init; }

    [Required, MaxLength(500)]
    public required string ConfirmedMessageTemplate { get; init; }

    [Required, MaxLength(500)]
    public required string Reminder1hTemplate { get; init; }

    [Required, MaxLength(300)]
    public required string Evaluation2hPollTitle { get; init; }

    [Required, MaxLength(500)]
    public required string EvaluationWindowOpenMessage { get; init; }
}

/// <summary>Status da conexão do WhatsApp Bridge exibido na aba WhatsApp.</summary>
public sealed record WhatsAppBridgeStatusResponse(
    string Status,
    string? ConnectedPhone,
    string? LastQrCodeData,
    string? LastPairingCode,
    string BridgeEndpoint,
    bool IsBridgeProcessReachable,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<string> ActivePollFlowPreview,
    bool BotPaused = false);

/// <summary>Payload para pausar ou retomar o bot de atendimento do WhatsApp.</summary>
public sealed record SetWhatsAppBotPausedRequest(bool Paused);

/// <summary>Payload para vincular um número de WhatsApp via Código de Pareamento de 8 dígitos.</summary>
public sealed record RequestWhatsAppPairingCodeDto
{
    [Required, MaxLength(40)]
    public required string PhoneNumber { get; init; }
}

// ============================================================================
// CONTRATOS DO PAINEL SUPERADMINISTRADOR (PATRICIO)
// ============================================================================

/// <summary>Visão consolidada de todas as lojas, financeiro SaaS, GMV global e métricas para o SuperAdmin.</summary>
public sealed record SuperAdminOverviewResponse(
    int TotalStores,
    int ActiveStores,
    int PendingOrOverdueStores,
    int ConnectedWhatsAppStores,
    decimal TotalMrrSaaS,
    decimal TotalPaidSaaSRevenue,
    decimal TotalPendingSaaSRevenue,
    int GlobalAppointmentsCount,
    decimal GlobalConfirmedGmv,
    decimal GlobalCompletedGmv,
    decimal GlobalAverageTicket,
    IReadOnlyList<SuperAdminStoreItemDto> Stores);

/// <summary>Resumo detalhado operacional, financeiro e de assinatura de cada loja.</summary>
public sealed record SuperAdminStoreItemDto(
    int Id,
    string Name,
    string AdminUsername,
    string? OwnerPhone,
    string OpeningTime,
    string ClosingTime,
    string WhatsAppStatus,
    string? ConnectedPhone,
    string SubscriptionPlan,
    decimal MonthlyFee,
    string SubscriptionStatus,
    string NextDueDate,
    bool IsActive,
    int BarbersCount,
    int ServicesCount,
    int CustomersCount,
    int TotalAppointments,
    int CompletedAppointments,
    decimal ConfirmedRevenue,
    decimal CompletedRevenue,
    decimal AverageTicket,
    int EvaluationsCount,
    DateTimeOffset UpdatedAt);

/// <summary>Payload para criar uma nova loja no SuperAdmin e entregar pré-configuração completa.</summary>
public sealed record CreateStoreWithPreconfigRequest
{
    [Required, MaxLength(120)]
    public required string StoreName { get; init; }

    [Required, MaxLength(80)]
    public required string AdminUsername { get; init; }

    [Required, MaxLength(80)]
    public required string AdminPassword { get; init; }

    [MaxLength(40)]
    public string? OwnerPhone { get; init; }

    [RegularExpression(@"^\d{2}:\d{2}$")]
    public string OpeningTime { get; init; } = "08:00";

    [RegularExpression(@"^\d{2}:\d{2}$")]
    public string ClosingTime { get; init; } = "20:00";

    [MaxLength(80)]
    public string SubscriptionPlan { get; init; } = "Plano Pro WhatsApp";

    [Range(0, 100000)]
    public decimal MonthlyFee { get; init; } = 149.90m;

    public string? NextDueDate { get; init; }

    /// <summary>Template de pré-configuração: "standard" (Corte 30, Barba 30, Combo 50, Sobrancelha 15), "vip" (Preços VIP + 5 serviços) ou "empty".</summary>
    public string PreconfigTemplate { get; init; } = "standard";

    public decimal CustomCortePrice { get; init; } = 30.00m;
    public decimal CustomBarbaPrice { get; init; } = 30.00m;
    public decimal CustomComboPrice { get; init; } = 50.00m;
    public decimal CustomSobrancelhaPrice { get; init; } = 15.00m;
}

/// <summary>Payload para aplicar/entregar pré-configuração a uma loja existente.</summary>
public sealed record ApplyStorePreconfigRequest
{
    [Required, MaxLength(40)]
    public string Template { get; init; } = "standard";

    public bool ResetPollMessages { get; init; } = true;
}

/// <summary>Payload para atualizar assinatura, mensalidade, status de bloqueio ou senha de uma loja.</summary>
public sealed record UpdateStoreSuperAdminRequest
{
    [Required, MaxLength(120)]
    public required string StoreName { get; init; }

    [MaxLength(40)]
    public string? OwnerPhone { get; init; }

    [Required, MaxLength(80)]
    public required string SubscriptionPlan { get; init; }

    [Range(0, 100000)]
    public decimal MonthlyFee { get; init; }

    [Required, MaxLength(30)]
    public required string SubscriptionStatus { get; init; } // Active, Pending, Overdue, Suspended

    [Required, MaxLength(20)]
    public required string NextDueDate { get; init; }

    public bool IsActive { get; init; } = true;

    [MaxLength(80)]
    public string? NewAdminPassword { get; init; }
}

/// <summary>Payload para gerar e enviar uma cobrança de assinatura para uma loja.</summary>
public sealed record CreateStoreBillingChargeRequest
{
    [Required]
    public required int StoreId { get; init; }

    [Range(1, 100000)]
    public required decimal Amount { get; init; }

    [Required, MaxLength(20)]
    public required string DueDate { get; init; }

    [Required, MaxLength(200)]
    public required string Description { get; init; }

    [MaxLength(500)]
    public string? PaymentLinkOrPix { get; init; }

    public bool SendWhatsAppNotification { get; init; } = true;
}

/// <summary>DTO de fatura/cobrança de assinatura de loja.</summary>
public sealed record StoreBillingRecordDto(
    long Id,
    int StoreId,
    string StoreName,
    decimal Amount,
    string DueDate,
    string Status,
    string Description,
    string? PaymentLinkOrPix,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt);

/// <summary>DTO de linha de log direto do sistema para o SuperAdministrador.</summary>
public sealed record SystemLogEntryDto(
    long Id,
    string Level,
    string Category,
    int? StoreId,
    string? StoreName,
    string Message,
    DateTimeOffset CreatedAt);

namespace ClinicHair.WebApi.Contracts;

// ============================================================ BOT / WHATSAPP (itens 11–24)

public sealed record BotAutomationSettingsDto(
    bool BotScheduleEnabled,
    string? BotPauseStart,
    string? BotPauseEnd,
    bool BotPauseOutsideOpeningHours,
    string? WelcomeMessage,
    string? HolidayNoticeMessage,
    string? HolidayNoticeUntil,
    string? Address,
    string? MapLink,
    bool Reminder24hEnabled,
    string Reminder24hTemplate,
    int ReminderResendHours,
    int MinCancelHours,
    int AntiSpamMaxPerMinute,
    bool BirthdayEnabled,
    string BirthdayTemplate,
    bool MonthlyReportEnabled,
    string? OwnerPhone,
    decimal MonthlyRevenueGoal,
    int MonthlyAppointmentsGoal);

public sealed record ConversationSummaryDto(
    string Phone,
    string? CustomerName,
    string CurrentStep,
    bool AwaitingHuman,
    DateTimeOffset? AwaitingSince,
    bool HumanTakeover,
    DateTimeOffset? TakeoverUntil,
    DateTimeOffset? LastInboundAt,
    DateTimeOffset UpdatedAt,
    string? LastMessage);

public sealed record ConversationDetailDto(ConversationSummaryDto Summary, IReadOnlyList<BotChatHistoryItem> History);

public sealed record TakeoverRequest(bool Enabled, int? Hours);

public sealed record AgentReplyRequest(string Text);

public sealed record QuickReplyDto(int Id, string Keywords, string Response, bool IsActive);

public sealed record UpsertQuickReplyRequest(string Keywords, string Response, bool IsActive = true);

public sealed record BridgeHealthDto(
    bool BridgeOnline,
    string WhatsAppStatus,
    DateTimeOffset? LastWebhookAt,
    int WebhooksLastHour,
    int AwaitingHumanCount,
    bool BotPaused,
    bool ScheduledPauseActive,
    IReadOnlyList<HealthLogItem> RecentIssues);

public sealed record HealthLogItem(DateTimeOffset At, string Level, string Message);

// ============================================================ AGENDA (itens 25–34)

public sealed record AgendaBoardDto(string Date, IReadOnlyList<string> Slots, IReadOnlyList<AgendaBarberColumnDto> Barbers);

public sealed record AgendaBarberColumnDto(
    int Id,
    string Name,
    string WorkStart,
    string WorkEnd,
    string? LunchStart,
    string? LunchEnd,
    int OccupancyPercent,
    IReadOnlyList<AgendaCardDto> Appointments);

public sealed record AgendaCardDto(
    long Id,
    string Date,
    string StartTime,
    string EndTime,
    int BarberId,
    int CustomerId,
    string CustomerName,
    string CustomerPhone,
    string ServiceName,
    string ColorHex,
    decimal Price,
    string Status,
    string Stage,
    bool CustomerConfirmed,
    string PaymentMethod);

public sealed record MoveAppointmentRequest(string Date, string Time, int BarberId, bool NotifyCustomer = true);

public sealed record AppointmentStageRequest(string Stage, string? PaymentMethod);

public sealed record RecurringBlockDto(int Id, int BarberId, string BarberName, int DayOfWeek, string StartTime, string EndTime, string Reason, bool IsActive);

public sealed record UpsertRecurringBlockRequest(int BarberId, int DayOfWeek, string StartTime, string EndTime, string Reason);

public sealed record WaitlistEntryDto(int Id, string CustomerName, string Phone, string Date, int? BarberId, string? BarberName, int? ServiceItemId, string? ServiceName, string? Notes, string Status, DateTimeOffset CreatedAt, DateTimeOffset? NotifiedAt);

public sealed record CreateWaitlistEntryRequest(string CustomerName, string Phone, string Date, int? BarberId, int? ServiceItemId, string? Notes);

public sealed record UpdateWaitlistStatusRequest(string Status);

public sealed record ServiceTimeStatDto(int ServiceId, string ServiceName, int ConfiguredMinutes, int? AverageMinutes, int Samples);

// ============================================================ CRM (itens 35–44)

public sealed record CustomerProfileDto(
    int Id,
    string Name,
    string Phone,
    string? BirthDate,
    string MarketingConsent,
    string? InternalNotes,
    bool IsAnonymized,
    int TotalAppointments,
    decimal TotalSpent,
    decimal AverageTicket,
    string? FirstVisit,
    string? LastVisit,
    string? FavoriteService,
    string? FavoriteBarber,
    int Cancellations,
    int NoShows,
    double? AverageRating,
    IReadOnlyList<CustomerHistoryItemDto> History,
    CustomerSubscriptionDto? Subscription);

public sealed record CustomerHistoryItemDto(long Id, string Date, string Time, string Service, string Barber, decimal Price, string Status, int? Rating);

public sealed record UpdateCustomerRequest(string? Name, string? BirthDate, string? MarketingConsent, string? InternalNotes);

public sealed record AnonymizeCustomerRequest(string Ticket);

public sealed record InactiveCustomerDto(int Id, string Name, string Phone, string? LastVisit, int DaysSince, int TotalAppointments, string MarketingConsent);

public sealed record SegmentFilter(
    int? MinVisits,
    int? MaxVisits,
    int? InactiveDays,
    int? ActiveWithinDays,
    int? ServiceId,
    decimal? MinAverageTicket,
    bool OnlyOptIn = false);

public sealed record SegmentPreviewDto(int Total, int ExcludedOptOut, IReadOnlyList<InactiveCustomerDto> Sample);

public sealed record CreateCampaignRequest(string Name, string Message, SegmentFilter Segment);

public sealed record CampaignDto(int Id, string Name, string Message, string Status, int TotalRecipients, int SentCount, int FailedCount, DateTimeOffset CreatedAt, DateTimeOffset? FinishedAt);

public sealed record DuplicateGroupDto(string Key, IReadOnlyList<DuplicateCustomerDto> Customers);

public sealed record DuplicateCustomerDto(int Id, string Name, string Phone, int TotalAppointments, string? LastVisit);

public sealed record MergeCustomersRequest(int KeepId, IReadOnlyList<int> MergeIds);

public sealed record EvaluationTrendDto(
    IReadOnlyList<TrendPointDto> Monthly,
    IReadOnlyList<BarberTrendDto> ByBarber,
    double? OverallAverage,
    int TotalRatings);

public sealed record TrendPointDto(string Month, double? Average, int Count);

public sealed record BarberTrendDto(int BarberId, string BarberName, double? Average, int Count, IReadOnlyList<TrendPointDto> Monthly);

// ============================================================ RELATÓRIOS (itens 45–54)

public sealed record KpiDto(string Key, string Label, decimal Value, decimal Previous, decimal? DeltaPercent, string Format, bool HigherIsBetter = true);

public sealed record ReportsOverviewDto(
    string From,
    string To,
    string PreviousFrom,
    string PreviousTo,
    IReadOnlyList<KpiDto> Kpis,
    IReadOnlyList<TicketBreakdownDto> ByBarber,
    IReadOnlyList<TicketBreakdownDto> ByService,
    IReadOnlyList<OccupancyDto> OccupancyByBarber,
    IReadOnlyList<WeeklyRevenueDto> WeeklyRevenue,
    GoalsDto Goals,
    OwnerSnapshotDto Owner,
    IReadOnlyList<ServiceTimeStatDto> ServiceTimes,
    IReadOnlyList<PaymentSplitDto> Payments);

public sealed record TicketBreakdownDto(string Name, decimal Revenue, int Count, decimal AverageTicket);

public sealed record OccupancyDto(string Name, int Percent, int BookedMinutes, int AvailableMinutes);

public sealed record WeeklyRevenueDto(string WeekStart, decimal Revenue, int Count);

public sealed record GoalsDto(string Month, decimal RevenueGoal, decimal RevenueActual, int AppointmentsGoal, int AppointmentsActual);

public sealed record OwnerSnapshotDto(decimal TodayRevenue, int TodayCount, decimal WeekRevenue, int WeekCount, decimal MonthRevenue, int MonthCount);

public sealed record PaymentSplitDto(string Method, decimal Amount, int Count);

public sealed record CashClosingDto(
    string Date,
    decimal ExpectedPix,
    decimal ExpectedCard,
    decimal ExpectedCash,
    decimal? CountedPix,
    decimal? CountedCard,
    decimal? CountedCash,
    string? Notes,
    string? ClosedBy,
    DateTimeOffset? ClosedAt,
    int CompletedCount);

public sealed record SaveCashClosingRequest(string Date, decimal CountedPix, decimal CountedCard, decimal CountedCash, string? Notes);

// ============================================================ PLANOS DE ASSINATURA (itens 55–56)

public sealed record CustomerPlanDto(
    int Id,
    string Name,
    string? Description,
    decimal Price,
    IReadOnlyList<int> IncludedServiceIds,
    IReadOnlyList<string> IncludedServiceNames,
    int UsesPerMonth,
    int ValidityDays,
    bool IsActive,
    int ActiveSubscribers,
    int MaxMembers = 1,
    int PriorityBookingDays = 0,
    bool IsPublic = true);

public sealed record UpsertCustomerPlanRequest(
    string Name,
    string? Description,
    decimal Price,
    IReadOnlyList<int> IncludedServiceIds,
    int UsesPerMonth,
    int ValidityDays,
    bool IsActive = true,
    int MaxMembers = 1,
    int PriorityBookingDays = 0,
    bool IsPublic = true);

public sealed record CustomerSubscriptionDto(
    int Id,
    int CustomerId,
    string CustomerName,
    string CustomerPhone,
    int PlanId,
    string PlanName,
    decimal PlanPrice,
    string Status,
    string StartDate,
    string CurrentPeriodEnd,
    int UsesThisPeriod,
    int UsesPerMonth,
    string? Notes,
    decimal CreditBalance = 0,
    IReadOnlyList<SubscriptionMemberDto>? Members = null,
    int MaxMembers = 1);

public sealed record SubscriptionMemberDto(int CustomerId, string Name, string Phone);

public sealed record CreateSubscriptionRequest(int CustomerId, int PlanId, string? StartDate, string? Notes);

public sealed record UpdateSubscriptionStatusRequest(string Status);

public sealed record PlansSummaryDto(int ActivePlans, int ActiveSubscribers, decimal MonthlyRecurringRevenue, int ExpiringIn7Days);

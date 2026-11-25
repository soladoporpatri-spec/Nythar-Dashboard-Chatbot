namespace ClinicHair.WebApi.Contracts;

// ---------------------------------------------------------------- Assinaturas avançadas (57–66)
public sealed record ChangePlanRequest(int NewPlanId);
public sealed record ChangePlanResultDto(CustomerSubscriptionDto Subscription, decimal CreditFromOldPlan, decimal AmountDue, decimal CreditCarried);
public sealed record AddSubscriptionMemberRequest(int CustomerId);
public sealed record PixChargeDto(decimal Amount, string CopyPaste, string Description);
public sealed record PlanBreakdownDto(int PlanId, string PlanName, int Active, decimal MonthlyRevenue);
public sealed record SubscriptionsReportDto(
    int Active, int Paused, int Overdue, int CancelledLast30Days, int NewLast30Days,
    decimal MonthlyRecurringRevenue, decimal ChurnRatePercent, double AverageUsesPerActive,
    IReadOnlyList<PlanBreakdownDto> ByPlan);

// ---------------------------------------------------------------- Público (66, 94, 96, 100)
public sealed record PublicServiceDto(int Id, string Name, decimal Price, int DurationMinutes);
public sealed record PublicBarberDto(int Id, string Name, string Specialty);
public sealed record PublicPlanDto(int Id, string Name, string? Description, decimal Price, int UsesPerMonth, int ValidityDays, int MaxMembers, IReadOnlyList<string> IncludedServices);
public sealed record PublicStoreDto(
    string Slug, string Name, string? Address, string? MapLink, string OpeningTime, string ClosingTime,
    string? BrandColor, string? LogoUrl, string Language, bool BookingEnabled, int BookingHorizonDays, bool OnlinePix,
    IReadOnlyList<PublicServiceDto> Services, IReadOnlyList<PublicBarberDto> Barbers, IReadOnlyList<PublicPlanDto> Plans);
public sealed record PublicBookingRequest(string Name, string Phone, int ServiceId, int BarberId, string Date, string Time, string? Website);
public sealed record PublicBookingResultDto(long AppointmentId, string Date, string Time, string BarberName, string ServiceName, decimal Price, string? PixCopyPaste);
public sealed record PublicSignupRequest(string StoreName, string OwnerName, string OwnerPhone, string? City, string? DesiredPlan, string? Website);

// ---------------------------------------------------------------- Configuração dos recursos novos
public sealed record ExtensionSettingsDto(
    string? PublicSlug, bool PublicBookingEnabled, int BookingHorizonDays,
    string? PixKey, string? PixMerchantName, string? PixMerchantCity, bool OnlinePixEnabled,
    bool LoyaltyEnabled, decimal LoyaltyPointsPerReal, int LoyaltyRewardPoints, string LoyaltyRewardDescription,
    string Language, string? BrandColor, string? LogoUrl, string? BrandDisplayName,
    int SubscriptionGraceDays, int RenewalReminderDays,
    string? SaasTier = null, int MaxBarbers = 0);

// ---------------------------------------------------------------- Fidelidade (97)
public sealed record LoyaltyEntryDto(long Id, int Points, string Reason, DateTimeOffset CreatedAt);
public sealed record LoyaltyBalanceDto(int CustomerId, string CustomerName, int Points, int RewardPoints, string RewardDescription, IReadOnlyList<LoyaltyEntryDto> History);
public sealed record LoyaltyAdjustRequest(int Points, string? Reason);

// ---------------------------------------------------------------- Estoque (99)
public sealed record StockItemDto(int Id, string Name, string? Sku, decimal Quantity, decimal MinQuantity, decimal UnitCost, decimal SalePrice, bool IsActive, bool IsLow);
public sealed record UpsertStockItemRequest(string Name, string? Sku, decimal MinQuantity, decimal UnitCost, decimal SalePrice, bool IsActive = true, decimal? InitialQuantity = null);
public sealed record StockMovementRequest(decimal Delta, string Reason);
public sealed record StockMovementDto(long Id, int StockItemId, string ItemName, decimal Delta, string Reason, string? CreatedBy, DateTimeOffset CreatedAt);

// ---------------------------------------------------------------- Busca global (82)
public sealed record SearchHitDto(string Kind, string Id, string Title, string Subtitle, string Section);

// ---------------------------------------------------------------- SuperAdmin (68–76)
public sealed record TotpSetupDto(string Secret, string OtpAuthUri);
public sealed record TotpCodeRequest(string Code);
public sealed record SupportSessionRequest(int StoreId, string Reason);
public sealed record StoreSignupDto(int Id, string StoreName, string OwnerName, string OwnerPhone, string? City, string DesiredPlan, string Status, string? ReviewNotes, int? CreatedStoreId, DateTimeOffset CreatedAt);
public sealed record ApproveSignupRequest(string AdminUsername, decimal? MonthlyFee);
public sealed record ApproveSignupResultDto(StoreSignupDto Signup, int StoreId, string AdminUsername, string TemporaryPassword);
public sealed record RejectSignupRequest(string? Notes);
public sealed record BarberCalendarDto(int BarberId, string BarberName, bool HasCalendar, DateTimeOffset? LastSync, int Events);
public sealed record SetBarberCalendarRequest(string? IcsUrl);

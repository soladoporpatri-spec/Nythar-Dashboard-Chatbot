namespace ClinicHair.WebApi.Domain.Entities;

/// <summary>Resposta rápida do bot acionada por palavra-chave (ex.: "preço", "endereço").</summary>
public sealed class QuickReply
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Keywords { get; set; } = string.Empty;   // separadas por vírgula
    public string Response { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Bloqueio de horário recorrente semanal (ex.: toda quinta 15h–16h).</summary>
public sealed class RecurringBlock
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public int BarberId { get; set; }                       // 0 = todos os profissionais
    public int DayOfWeek { get; set; }                      // 0 = Domingo ... 6 = Sábado
    public string StartTime { get; set; } = "12:00";
    public string EndTime { get; set; } = "13:00";
    public string Reason { get; set; } = "Bloqueio";
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Cliente na lista de espera para um dia (avisado quando um horário vagar).</summary>
public sealed class WaitlistEntry
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string CustomerName { get; set; } = string.Empty;
    public string Phone { get; set; } = string.Empty;
    public string Date { get; set; } = string.Empty;         // "yyyy-MM-dd"
    public int? BarberId { get; set; }
    public int? ServiceItemId { get; set; }
    public string? Notes { get; set; }
    public string Status { get; set; } = "Waiting";         // Waiting, Notified, Booked, Removed
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? NotifiedAt { get; set; }
}

/// <summary>Campanha de mensagens para um segmento de clientes (envio controlado e com opt-out).</summary>
public sealed class Campaign
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string SegmentJson { get; set; } = "{}";
    public string Status { get; set; } = "Queued";          // Queued, Sending, Done, Cancelled
    public int TotalRecipients { get; set; }
    public int SentCount { get; set; }
    public int FailedCount { get; set; }
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? FinishedAt { get; set; }
}

public sealed class CampaignRecipient
{
    public long Id { get; set; }
    public int CampaignId { get; set; }
    public int CustomerId { get; set; }
    public string Phone { get; set; } = string.Empty;
    public string CustomerName { get; set; } = string.Empty;
    public string Status { get; set; } = "Pending";         // Pending, Sent, Failed, Skipped
    public DateTimeOffset? SentAt { get; set; }
}

/// <summary>Fechamento de caixa diário com conferência por forma de pagamento.</summary>
public sealed class CashClosing
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Date { get; set; } = string.Empty;         // "yyyy-MM-dd"
    public decimal ExpectedPix { get; set; }
    public decimal ExpectedCard { get; set; }
    public decimal ExpectedCash { get; set; }
    public decimal CountedPix { get; set; }
    public decimal CountedCard { get; set; }
    public decimal CountedCash { get; set; }
    public string? Notes { get; set; }
    public string? ClosedBy { get; set; }
    public DateTimeOffset ClosedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Plano de assinatura que a barbearia vende para seus clientes (ex.: 4 cortes/mês).</summary>
public sealed class CustomerPlan
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public decimal Price { get; set; }
    public string IncludedServiceIdsCsv { get; set; } = string.Empty;
    public int UsesPerMonth { get; set; }                   // 0 = ilimitado
    public int ValidityDays { get; set; } = 30;
    public int MaxMembers { get; set; } = 1;                // 2+ = plano família/dupla (usos compartilhados)
    public int PriorityBookingDays { get; set; }            // dias extras de antecedência para assinantes
    public bool IsPublic { get; set; } = true;              // aparece na página pública de planos
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Assinatura de um cliente em um plano da barbearia.</summary>
public sealed class CustomerSubscription
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public int CustomerId { get; set; }
    public int PlanId { get; set; }
    public string Status { get; set; } = "Active";          // Active, Paused, Cancelled, Overdue
    public string StartDate { get; set; } = string.Empty;    // "yyyy-MM-dd"
    public string CurrentPeriodEnd { get; set; } = string.Empty;
    public int UsesThisPeriod { get; set; }
    public decimal CreditBalance { get; set; }              // crédito de pró-rata em trocas de plano
    public string? LastRenewalReminderFor { get; set; }     // período (CurrentPeriodEnd) já lembrado
    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? CancelledAt { get; set; }
}

/// <summary>Membro adicional de uma assinatura família/dupla.</summary>
public sealed class SubscriptionMember
{
    public int Id { get; set; }
    public int SubscriptionId { get; set; }
    public int CustomerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Pedido de cadastro de nova barbearia (onboarding self-service, aprovado pelo SuperAdmin).</summary>
public sealed class StoreSignupRequest
{
    public int Id { get; set; }
    public string StoreName { get; set; } = string.Empty;
    public string OwnerName { get; set; } = string.Empty;
    public string OwnerPhone { get; set; } = string.Empty;
    public string? City { get; set; }
    public string DesiredPlan { get; set; } = "Plano Pro WhatsApp";
    public string Status { get; set; } = "Pending";          // Pending, Approved, Rejected
    public string? ReviewNotes { get; set; }
    public int? CreatedStoreId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? ReviewedAt { get; set; }
}

/// <summary>Produto de estoque (pomada, shampoo, bebidas...).</summary>
public sealed class StockItem
{
    public int Id { get; set; }
    public int StoreId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Sku { get; set; }
    public decimal Quantity { get; set; }
    public decimal MinQuantity { get; set; }
    public decimal UnitCost { get; set; }
    public decimal SalePrice { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class StockMovement
{
    public long Id { get; set; }
    public int StoreId { get; set; }
    public int StockItemId { get; set; }
    public decimal Delta { get; set; }                      // + entrada, - saída
    public string Reason { get; set; } = "Ajuste";          // Entrada, Venda, Uso, Ajuste, Perda
    public string? CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Extrato de pontos de fidelidade.</summary>
public sealed class LoyaltyTransaction
{
    public long Id { get; set; }
    public int StoreId { get; set; }
    public int CustomerId { get; set; }
    public int Points { get; set; }
    public string Reason { get; set; } = string.Empty;
    public long? AppointmentId { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Versão de esquema aplicada (migrações versionadas e idempotentes).</summary>
public sealed class SchemaVersion
{
    public int Version { get; set; }
    public string Description { get; set; } = string.Empty;
    public DateTimeOffset AppliedAt { get; set; } = DateTimeOffset.UtcNow;
}

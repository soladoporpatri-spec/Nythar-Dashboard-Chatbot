using ClinicHair.WebApi.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Infrastructure.Data;

/// <summary>
/// Contexto de banco de dados EF Core 10 otimizado para MySQL 8.4 LTS (com NoTracking por padrão).
/// </summary>
public sealed class ClinicHairDbContext : DbContext
{
    private readonly IHttpContextAccessor? _accessor;

    public ClinicHairDbContext(DbContextOptions<ClinicHairDbContext> options, IHttpContextAccessor? accessor = null)
        : base(options)
    {
        _accessor = accessor;
        ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.NoTracking;
    }

    /// <summary>
    /// Filtro global por loja (item 75): numa requisição de um usuário comum de loja, toda consulta às tabelas da loja
    /// fica presa ao StoreId do token, mesmo que algum código esqueça o Where. É avaliado a cada consulta;
    /// SuperAdmin, workers em segundo plano e webhooks (sem usuário de loja) não são filtrados.
    /// </summary>
    private int? TenantStoreId
    {
        get
        {
            var user = _accessor?.HttpContext?.User;
            if (user?.Identity?.IsAuthenticated != true || user.IsInRole("SuperAdmin")) return null;
            return int.TryParse(user.FindFirst("StoreId")?.Value, out var id) && id > 0 ? id : null;
        }
    }

    public DbSet<Store> Stores => Set<Store>();
    public DbSet<AdminUser> AdminUsers => Set<AdminUser>();
    public DbSet<Barber> Barbers => Set<Barber>();
    public DbSet<ServiceItem> ServiceItems => Set<ServiceItem>();
    public DbSet<StoreNonWorkingDay> StoreNonWorkingDays => Set<StoreNonWorkingDay>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<Appointment> Appointments => Set<Appointment>();
    public DbSet<Evaluation> Evaluations => Set<Evaluation>();
    public DbSet<ConversationSession> ConversationSessions => Set<ConversationSession>();
    public DbSet<InboxWebhookEvent> InboxWebhookEvents => Set<InboxWebhookEvent>();
    public DbSet<StoreBillingRecord> StoreBillingRecords => Set<StoreBillingRecord>();
    public DbSet<SystemLogEntry> SystemLogEntries => Set<SystemLogEntry>();
    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();
    public DbSet<QuickReply> QuickReplies => Set<QuickReply>();
    public DbSet<RecurringBlock> RecurringBlocks => Set<RecurringBlock>();
    public DbSet<WaitlistEntry> WaitlistEntries => Set<WaitlistEntry>();
    public DbSet<Campaign> Campaigns => Set<Campaign>();
    public DbSet<CampaignRecipient> CampaignRecipients => Set<CampaignRecipient>();
    public DbSet<CashClosing> CashClosings => Set<CashClosing>();
    public DbSet<CustomerPlan> CustomerPlans => Set<CustomerPlan>();
    public DbSet<CustomerSubscription> CustomerSubscriptions => Set<CustomerSubscription>();
    public DbSet<SubscriptionMember> SubscriptionMembers => Set<SubscriptionMember>();
    public DbSet<StoreSignupRequest> StoreSignupRequests => Set<StoreSignupRequest>();
    public DbSet<StockItem> StockItems => Set<StockItem>();
    public DbSet<StockMovement> StockMovements => Set<StockMovement>();
    public DbSet<LoyaltyTransaction> LoyaltyTransactions => Set<LoyaltyTransaction>();
    public DbSet<SchemaVersion> SchemaVersions => Set<SchemaVersion>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Store>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(120).IsRequired();
        });

        modelBuilder.Entity<AdminUser>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Username).HasMaxLength(80).IsRequired();
            b.HasIndex(x => new { x.StoreId, x.Username }).IsUnique();
        });

        modelBuilder.Entity<Barber>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(120).IsRequired();
            b.HasIndex(x => new { x.StoreId, x.IsActive });
        });

        modelBuilder.Entity<ServiceItem>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(120).IsRequired();
            b.Property(x => x.Price).HasPrecision(10, 2);
            b.Property(x => x.ColorHex).HasMaxLength(16).IsRequired();
            b.HasIndex(x => new { x.StoreId, x.IsActive, x.DisplayOrder });
        });

        modelBuilder.Entity<StoreNonWorkingDay>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.BlockedDate).HasMaxLength(16).IsRequired();
            b.HasIndex(x => new { x.StoreId, x.BlockedDate }).IsUnique();
        });

        modelBuilder.Entity<Customer>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Name).HasMaxLength(120).IsRequired();
            b.Property(x => x.Phone).HasMaxLength(40).IsRequired();
            b.HasIndex(x => new { x.StoreId, x.Phone }).IsUnique();
        });

        modelBuilder.Entity<Appointment>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.AppointmentDate).HasMaxLength(16).IsRequired();
            b.Property(x => x.StartTime).HasMaxLength(10).IsRequired();
            b.Property(x => x.PriceCharged).HasPrecision(10, 2);
            b.Property(x => x.Status).HasMaxLength(24).IsRequired();
            // Índice composto para verificar rapidamente a regra de até 3 agendamentos por hora por funcionário
            b.HasIndex(x => new { x.StoreId, x.BarberId, x.AppointmentDate, x.StartTime, x.Status });
            b.HasIndex(x => new { x.StoreId, x.AppointmentDate });
            b.HasOne(x => x.Customer).WithMany().HasForeignKey(x => x.CustomerId);
            b.HasOne(x => x.Barber).WithMany().HasForeignKey(x => x.BarberId);
            b.HasOne(x => x.ServiceItem).WithMany().HasForeignKey(x => x.ServiceItemId);
        });

        modelBuilder.Entity<Evaluation>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.StoreId, x.CustomerId });
            b.HasIndex(x => new { x.StoreId, x.CreatedAt });
        });

        modelBuilder.Entity<ConversationSession>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.CustomerPhoneJid).HasMaxLength(80).IsRequired();
            b.Property(x => x.CurrentStep).HasMaxLength(40).IsRequired();
            b.HasIndex(x => new { x.StoreId, x.CustomerPhoneJid }).IsUnique();
        });

        modelBuilder.Entity<InboxWebhookEvent>(b =>
        {
            b.HasKey(x => x.EventId);
            b.Property(x => x.EventId).HasMaxLength(128);
            b.HasIndex(x => x.ProcessedAtUtc);
        });

        modelBuilder.Entity<StoreBillingRecord>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.StoreName).HasMaxLength(120);
            b.Property(x => x.Amount).HasPrecision(10, 2);
            b.Property(x => x.Status).HasMaxLength(32);
            b.HasIndex(x => new { x.StoreId, x.Status });
        });

        modelBuilder.Entity<SystemLogEntry>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Level).HasMaxLength(24);
            b.Property(x => x.Category).HasMaxLength(64);
            b.HasIndex(x => x.CreatedAt);
            b.HasIndex(x => new { x.Level, x.StoreId });
        });

        modelBuilder.Entity<RevokedToken>(b =>
        {
            b.HasKey(x => x.Jti);
            b.Property(x => x.Jti).HasMaxLength(64);
        });

        modelBuilder.Entity<QuickReply>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.StoreId);
        });

        modelBuilder.Entity<RecurringBlock>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.StoreId, x.DayOfWeek });
        });

        modelBuilder.Entity<WaitlistEntry>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.StoreId, x.Date, x.Status });
        });

        modelBuilder.Entity<Campaign>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.StoreId, x.Status });
        });

        modelBuilder.Entity<CampaignRecipient>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.CampaignId, x.Status });
        });

        modelBuilder.Entity<CashClosing>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.ExpectedPix).HasPrecision(10, 2);
            b.Property(x => x.ExpectedCard).HasPrecision(10, 2);
            b.Property(x => x.ExpectedCash).HasPrecision(10, 2);
            b.Property(x => x.CountedPix).HasPrecision(10, 2);
            b.Property(x => x.CountedCard).HasPrecision(10, 2);
            b.Property(x => x.CountedCash).HasPrecision(10, 2);
            b.HasIndex(x => new { x.StoreId, x.Date }).IsUnique();
        });

        modelBuilder.Entity<CustomerPlan>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Price).HasPrecision(10, 2);
            b.HasIndex(x => x.StoreId);
        });

        modelBuilder.Entity<CustomerSubscription>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.StoreId, x.CustomerId });
            b.HasIndex(x => new { x.StoreId, x.Status });
            b.Property(x => x.CreditBalance).HasPrecision(10, 2);
        });

        modelBuilder.Entity<SubscriptionMember>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.SubscriptionId);
        });
        modelBuilder.Entity<StoreSignupRequest>(b => b.HasKey(x => x.Id));
        modelBuilder.Entity<StockItem>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Quantity).HasPrecision(10, 2);
            b.Property(x => x.MinQuantity).HasPrecision(10, 2);
            b.Property(x => x.UnitCost).HasPrecision(10, 2);
            b.Property(x => x.SalePrice).HasPrecision(10, 2);
            b.HasIndex(x => x.StoreId);
        });
        modelBuilder.Entity<StockMovement>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Delta).HasPrecision(10, 2);
            b.HasIndex(x => new { x.StoreId, x.StockItemId });
        });
        modelBuilder.Entity<LoyaltyTransaction>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.StoreId, x.CustomerId });
        });
        modelBuilder.Entity<SchemaVersion>(b => b.HasKey(x => x.Version));

        // Filtro global por loja (defesa em profundidade, item 75)
        modelBuilder.Entity<Barber>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<ServiceItem>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<Customer>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<Appointment>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<QuickReply>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<RecurringBlock>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<WaitlistEntry>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<Campaign>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<CashClosing>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<CustomerPlan>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<CustomerSubscription>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<StockItem>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<StockMovement>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
        modelBuilder.Entity<LoyaltyTransaction>().HasQueryFilter(e => TenantStoreId == null || e.StoreId == TenantStoreId);
    }
}

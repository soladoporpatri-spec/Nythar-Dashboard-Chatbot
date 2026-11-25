using System.Globalization;
using System.Text;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Services;

/// <summary>
/// Interface do painel SuperAdministrador (Patricio) para gestão multi-loja,
/// cobrança de assinaturas, resumo financeiro global, pré-configuração e logs diretos.
/// </summary>
public interface ISuperAdminService
{
    Task<SuperAdminOverviewResponse> GetOverviewAsync(CancellationToken ct);
    Task<SuperAdminStoreItemDto> CreateStoreWithPreconfigAsync(CreateStoreWithPreconfigRequest request, CancellationToken ct);
    Task<SuperAdminStoreItemDto> ApplyPreconfigToStoreAsync(int storeId, ApplyStorePreconfigRequest request, CancellationToken ct);
    Task<SuperAdminStoreItemDto> UpdateStoreAsync(int storeId, UpdateStoreSuperAdminRequest request, CancellationToken ct);
    Task<bool> DeleteStoreAsync(int storeId, CancellationToken ct);
    Task<IReadOnlyList<StoreBillingRecordDto>> GetBillingRecordsAsync(int? storeId, CancellationToken ct);
    Task<StoreBillingRecordDto> CreateBillingChargeAsync(CreateStoreBillingChargeRequest request, CancellationToken ct);
    Task<StoreBillingRecordDto?> MarkBillingPaidAsync(long billingId, CancellationToken ct);
    Task<IReadOnlyList<SystemLogEntryDto>> GetSystemLogsAsync(string? level, int? storeId, string? search, int limit, CancellationToken ct);
    Task AppendLogAsync(string level, string category, string message, int? storeId = null, string? storeName = null, CancellationToken ct = default);
    Task ClearLogsAsync(CancellationToken ct);
    Task<byte[]> ExportLogsTxtAsync(CancellationToken ct);
}

public sealed class SuperAdminService(
    ClinicHairDbContext db,
    IWhatsAppBridgeClient bridgeClient,
    IDashboardRealtimeNotifier realtime) : ISuperAdminService
{
    private static readonly CultureInfo PtBr = new("pt-BR");

    public async Task<SuperAdminOverviewResponse> GetOverviewAsync(CancellationToken ct)
    {
        var stores = await db.Stores.OrderBy(s => s.Id).ToListAsync(ct);
        var admins = await db.AdminUsers.Where(u => u.Role != "SuperAdmin").ToListAsync(ct);
        var barbers = await db.Barbers.ToListAsync(ct);
        var services = await db.ServiceItems.ToListAsync(ct);
        var customers = await db.Customers.ToListAsync(ct);
        var appts = await db.Appointments.Where(a => a.Status != "Cancelled").ToListAsync(ct);
        var evals = await db.Evaluations.ToListAsync(ct);
        var billings = await db.StoreBillingRecords.ToListAsync(ct);

        var storeItems = new List<SuperAdminStoreItemDto>(stores.Count);
        foreach (var s in stores)
        {
            var storeAdmin = admins.FirstOrDefault(a => a.StoreId == s.Id);
            var sAppts = appts.Where(a => a.StoreId == s.Id).ToList();
            var completedAppts = sAppts.Where(a => a.Status == "Completed").ToList();
            var confirmedRev = sAppts.Sum(a => a.PriceCharged);
            var completedRev = completedAppts.Sum(a => a.PriceCharged);
            var avgTicket = sAppts.Count > 0 ? Math.Round(confirmedRev / sAppts.Count, 2) : 0m;

            storeItems.Add(new SuperAdminStoreItemDto(
                s.Id,
                s.Name,
                storeAdmin?.Username ?? "Itamar",
                s.OwnerPhone ?? s.ConnectedPhone,
                s.OpeningTime,
                s.ClosingTime,
                s.WhatsAppStatus,
                s.ConnectedPhone,
                s.SubscriptionPlan,
                s.MonthlyFee,
                s.SubscriptionStatus,
                s.NextDueDate,
                s.IsActive,
                barbers.Count(b => b.StoreId == s.Id),
                services.Count(sv => sv.StoreId == s.Id),
                customers.Count(c => c.StoreId == s.Id),
                sAppts.Count,
                completedAppts.Count,
                confirmedRev,
                completedRev,
                avgTicket,
                evals.Count(e => e.StoreId == s.Id),
                s.UpdatedAt));
        }

        var totalMrr = stores.Where(s => s.IsActive).Sum(s => s.MonthlyFee);
        var totalPaidSaaS = billings.Where(b => b.Status == "Paid").Sum(b => b.Amount);
        var totalPendingSaaS = billings.Where(b => b.Status is "Pending" or "Overdue").Sum(b => b.Amount);

        var globalConfirmedGmv = appts.Sum(a => a.PriceCharged);
        var globalCompletedGmv = appts.Where(a => a.Status == "Completed").Sum(a => a.PriceCharged);
        var globalAvgTicket = appts.Count > 0 ? Math.Round(globalConfirmedGmv / appts.Count, 2) : 0m;

        return new SuperAdminOverviewResponse(
            stores.Count,
            stores.Count(s => s.IsActive && s.SubscriptionStatus == "Active"),
            stores.Count(s => !s.IsActive || s.SubscriptionStatus is "Pending" or "Overdue" or "Suspended"),
            stores.Count(s => s.WhatsAppStatus == "CONNECTED"),
            totalMrr,
            totalPaidSaaS,
            totalPendingSaaS,
            appts.Count,
            globalConfirmedGmv,
            globalCompletedGmv,
            globalAvgTicket,
            storeItems);
    }

    public async Task<SuperAdminStoreItemDto> CreateStoreWithPreconfigAsync(CreateStoreWithPreconfigRequest request, CancellationToken ct)
    {
        var cleanStoreName = request.StoreName.Trim();
        var cleanUsername = request.AdminUsername.Trim();

        var existingUser = await db.AdminUsers.AnyAsync(u => u.Username.ToLower() == cleanUsername.ToLower(), ct);
        if (existingUser)
        {
            throw new InvalidOperationException($"O usuário administrador '{cleanUsername}' já está em uso por outra loja.");
        }

        var dueDate = !string.IsNullOrWhiteSpace(request.NextDueDate)
            ? request.NextDueDate.Trim()
            : DateTime.Now.AddDays(30).ToString("yyyy-MM-dd");

        if (Platform.PasswordPolicy.Validate(request.AdminPassword, cleanUsername) is { } createPwdError)
        {
            throw new ArgumentException($"Senha provisória: {createPwdError}");
        }

        var store = new Store
        {
            Name = cleanStoreName,
            OpeningTime = request.OpeningTime,
            ClosingTime = request.ClosingTime,
            WorkingDaysCsv = "1,2,3,4,5,6",
            EvaluationRetentionDays = 30,
            WhatsAppStatus = "DISCONNECTED",
            SubscriptionPlan = string.IsNullOrWhiteSpace(request.SubscriptionPlan) ? "Plano Pro WhatsApp" : request.SubscriptionPlan.Trim(),
            MonthlyFee = request.MonthlyFee,
            SubscriptionStatus = "Active",
            NextDueDate = dueDate,
            IsActive = true,
            OwnerPhone = request.OwnerPhone?.Trim()
        };

        ApplyDefaultPollTemplatesToStoreEntity(store, cleanStoreName);

        db.Stores.Add(store);
        await db.SaveChangesAsync(ct);

        var hasher = new PasswordHasher<AdminUser>();
        var adminUser = new AdminUser
        {
            StoreId = store.Id,
            Username = cleanUsername,
            Role = "Admin",
            MustChangePassword = true // senha provisória: o dono da loja define a própria no primeiro acesso
        };
        adminUser.PasswordHash = hasher.HashPassword(adminUser, request.AdminPassword);
        db.AdminUsers.Add(adminUser);

        // Cria o primeiro funcionário já configurado com o expediente da loja
        var initialBarber = new Barber
        {
            StoreId = store.Id,
            Name = cleanUsername,
            Phone = request.OwnerPhone?.Trim() ?? string.Empty,
            Specialty = "Master Barber",
            WorkStartTime = request.OpeningTime,
            WorkEndTime = request.ClosingTime,
            WorkingDaysCsv = "1,2,3,4,5,6",
            MaxConcurrentPerSlot = 3,
            IsActive = true
        };
        db.Barbers.Add(initialBarber);

        // Entrega a pré-configuração de serviços escolhida pelo SuperAdmin
        SeedPreconfiguredServicesForStore(
            store.Id,
            request.PreconfigTemplate,
            request.CustomCortePrice,
            request.CustomBarbaPrice,
            request.CustomComboPrice,
            request.CustomSobrancelhaPrice);

        // Registra a primeira fatura da loja
        db.StoreBillingRecords.Add(new StoreBillingRecord
        {
            StoreId = store.Id,
            StoreName = store.Name,
            Amount = store.MonthlyFee,
            DueDate = dueDate,
            Status = "Pending",
            Description = $"Assinatura Inicial - {store.SubscriptionPlan}"
        });

        await db.SaveChangesAsync(ct);

        await AppendLogAsync(
            "STORE",
            "CreateStore",
            $"Nova loja '{store.Name}' criada com pré-configuração '{request.PreconfigTemplate}' (Admin: {cleanUsername} | Mensalidade: R$ {store.MonthlyFee:F2}).",
            store.Id,
            store.Name,
            ct);

        var overview = await GetOverviewAsync(ct);
        return overview.Stores.First(s => s.Id == store.Id);
    }

    public async Task<SuperAdminStoreItemDto> ApplyPreconfigToStoreAsync(int storeId, ApplyStorePreconfigRequest request, CancellationToken ct)
    {
        var store = await db.Stores.AsTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct)
            ?? throw new KeyNotFoundException("Loja não encontrada.");

        if (request.ResetPollMessages)
        {
            ApplyDefaultPollTemplatesToStoreEntity(store, store.Name);
            store.UpdatedAt = DateTimeOffset.UtcNow;
            db.Stores.Update(store);
        }

        var existingServices = await db.ServiceItems.AsTracking().Where(s => s.StoreId == storeId).ToListAsync(ct);
        var presetItems = BuildPresetServices(storeId, request.Template, 30m, 30m, 50m, 15m);

        foreach (var preset in presetItems)
        {
            var match = existingServices.FirstOrDefault(x => string.Equals(x.Name, preset.Name, StringComparison.OrdinalIgnoreCase));
            if (match is null)
            {
                db.ServiceItems.Add(preset);
            }
            else
            {
                match.Price = preset.Price;
                match.DurationMinutes = preset.DurationMinutes;
                match.ColorHex = preset.ColorHex;
                match.DisplayOrder = preset.DisplayOrder;
                match.IsActive = true;
                db.ServiceItems.Update(match);
            }
        }

        await db.SaveChangesAsync(ct);

        await AppendLogAsync(
            "STORE",
            "Preconfig",
            $"Pacote de pré-configuração '{request.Template}' entregue na loja '{store.Name}' pelo SuperAdmin.",
            store.Id,
            store.Name,
            ct);

        var overview = await GetOverviewAsync(ct);
        return overview.Stores.First(s => s.Id == storeId);
    }

    public async Task<SuperAdminStoreItemDto> UpdateStoreAsync(int storeId, UpdateStoreSuperAdminRequest request, CancellationToken ct)
    {
        var store = await db.Stores.AsTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct)
            ?? throw new KeyNotFoundException("Loja não encontrada.");

        store.Name = request.StoreName.Trim();
        store.OwnerPhone = request.OwnerPhone?.Trim();
        store.SubscriptionPlan = request.SubscriptionPlan.Trim();
        store.MonthlyFee = request.MonthlyFee;
        store.SubscriptionStatus = request.SubscriptionStatus;
        store.NextDueDate = request.NextDueDate;
        store.IsActive = request.IsActive;
        store.UpdatedAt = DateTimeOffset.UtcNow;
        db.Stores.Update(store);

        if (!string.IsNullOrWhiteSpace(request.NewAdminPassword))
        {
            var admin = await db.AdminUsers.AsTracking()
                .FirstOrDefaultAsync(u => u.StoreId == storeId && u.Role != "SuperAdmin", ct);
            if (admin is not null)
            {
                if (Platform.PasswordPolicy.Validate(request.NewAdminPassword.Trim(), admin.Username) is { } resetPwdError)
                {
                    throw new ArgumentException($"Nova senha: {resetPwdError}");
                }
                var hasher = new PasswordHasher<AdminUser>();
                admin.PasswordHash = hasher.HashPassword(admin, request.NewAdminPassword.Trim());
                admin.MustChangePassword = true;   // senha provisória definida pelo suporte
                admin.SessionVersion += 1;         // derruba sessões abertas com a senha antiga
                admin.FailedLoginCount = 0;
                admin.LockoutEndUtc = null;
                db.AdminUsers.Update(admin);
            }
        }

        await db.SaveChangesAsync(ct);

        await AppendLogAsync(
            "STORE",
            "UpdateStore",
            $"Loja '{store.Name}' atualizada (Status: {store.SubscriptionStatus} | Ativa: {store.IsActive} | R$ {store.MonthlyFee:F2}).",
            store.Id,
            store.Name,
            ct);

        var overview = await GetOverviewAsync(ct);
        return overview.Stores.First(s => s.Id == storeId);
    }

    public async Task<bool> DeleteStoreAsync(int storeId, CancellationToken ct)
    {
        var totalStores = await db.Stores.CountAsync(ct);
        if (totalStores <= 1)
        {
            throw new InvalidOperationException("Não é permitido excluir a única loja principal do sistema.");
        }

        var store = await db.Stores.AsTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null) return false;

        var evals = await db.Evaluations.AsTracking().Where(e => e.StoreId == storeId).ToListAsync(ct);
        db.Evaluations.RemoveRange(evals);

        var appts = await db.Appointments.AsTracking().Where(a => a.StoreId == storeId).ToListAsync(ct);
        db.Appointments.RemoveRange(appts);

        var sessions = await db.ConversationSessions.AsTracking().Where(s => s.StoreId == storeId).ToListAsync(ct);
        db.ConversationSessions.RemoveRange(sessions);

        var customers = await db.Customers.AsTracking().Where(c => c.StoreId == storeId).ToListAsync(ct);
        db.Customers.RemoveRange(customers);

        var services = await db.ServiceItems.AsTracking().Where(s => s.StoreId == storeId).ToListAsync(ct);
        db.ServiceItems.RemoveRange(services);

        var barbers = await db.Barbers.AsTracking().Where(b => b.StoreId == storeId).ToListAsync(ct);
        db.Barbers.RemoveRange(barbers);

        var blocked = await db.StoreNonWorkingDays.AsTracking().Where(n => n.StoreId == storeId).ToListAsync(ct);
        db.StoreNonWorkingDays.RemoveRange(blocked);

        var admins = await db.AdminUsers.AsTracking().Where(u => u.StoreId == storeId && u.Role != "SuperAdmin").ToListAsync(ct);
        db.AdminUsers.RemoveRange(admins);

        var billings = await db.StoreBillingRecords.AsTracking().Where(b => b.StoreId == storeId).ToListAsync(ct);
        db.StoreBillingRecords.RemoveRange(billings);

        db.Stores.Remove(store);
        await db.SaveChangesAsync(ct);

        await AppendLogAsync("STORE", "DeleteStore", $"Loja '{store.Name}' (ID {storeId}) removida pelo SuperAdmin.", null, store.Name, ct);
        return true;
    }

    public async Task<IReadOnlyList<StoreBillingRecordDto>> GetBillingRecordsAsync(int? storeId, CancellationToken ct)
    {
        var query = db.StoreBillingRecords.AsQueryable();
        if (storeId.HasValue && storeId.Value > 0)
        {
            query = query.Where(b => b.StoreId == storeId.Value);
        }

        var list = await query.OrderByDescending(b => b.Id).Take(100).ToListAsync(ct);
        return list.Select(MapBillingDto).ToList();
    }

    public async Task<StoreBillingRecordDto> CreateBillingChargeAsync(CreateStoreBillingChargeRequest request, CancellationToken ct)
    {
        var store = await db.Stores.AsTracking().FirstOrDefaultAsync(s => s.Id == request.StoreId, ct)
            ?? throw new KeyNotFoundException("Loja não encontrada para cobrança.");

        var record = new StoreBillingRecord
        {
            StoreId = store.Id,
            StoreName = store.Name,
            Amount = request.Amount,
            DueDate = request.DueDate,
            Status = "Pending",
            Description = request.Description.Trim(),
            PaymentLinkOrPix = request.PaymentLinkOrPix?.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.StoreBillingRecords.Add(record);

        store.SubscriptionStatus = "Pending";
        store.NextDueDate = request.DueDate;
        store.UpdatedAt = DateTimeOffset.UtcNow;
        db.Stores.Update(store);

        await db.SaveChangesAsync(ct);

        var formattedDue = DateOnly.TryParseExact(request.DueDate, "yyyy-MM-dd", out var d)
            ? d.ToString("dd/MM/yyyy")
            : request.DueDate;

        var amountText = request.Amount.ToString("F2", PtBr);
        var chargeSummary = $"Cobrança gerada para '{store.Name}': R$ {amountText} (Vencimento: {formattedDue}) — {request.Description}";

        // Se solicitado, envia aviso da cobrança direto para o WhatsApp do responsável da loja
        if (request.SendWhatsAppNotification)
        {
            var targetPhone = !string.IsNullOrWhiteSpace(store.OwnerPhone) ? store.OwnerPhone : store.ConnectedPhone;
            var digits = new string((targetPhone ?? "").Where(char.IsDigit).ToArray());
            if (digits.Length >= 10)
            {
                if (digits.Length is 10 or 11 && !digits.StartsWith("55", StringComparison.Ordinal))
                {
                    digits = $"55{digits}";
                }
                var waMsg = $"💳 *Aviso de Assinatura — {store.Name}*\n\n" +
                            $"Referência: {request.Description}\n" +
                            $"Valor: *R$ {amountText}*\n" +
                            $"Vencimento: *{formattedDue}*" +
                            (!string.IsNullOrWhiteSpace(request.PaymentLinkOrPix)
                                ? $"\n\nPIX / Link de Pagamento:\n{request.PaymentLinkOrPix}"
                                : "");
                await bridgeClient.SendTextAsync($"{digits}@s.whatsapp.net", waMsg, ct);
            }
        }

        realtime.Publish("agenda", chargeSummary, $"Cobrança de Assinatura • {store.Name}");
        await AppendLogAsync("BILLING", "ChargeCreated", chargeSummary, store.Id, store.Name, ct);

        return MapBillingDto(record);
    }

    public async Task<StoreBillingRecordDto?> MarkBillingPaidAsync(long billingId, CancellationToken ct)
    {
        var record = await db.StoreBillingRecords.AsTracking().FirstOrDefaultAsync(b => b.Id == billingId, ct);
        if (record is null) return null;

        record.Status = "Paid";
        record.PaidAt = DateTimeOffset.UtcNow;
        db.StoreBillingRecords.Update(record);

        var store = await db.Stores.AsTracking().FirstOrDefaultAsync(s => s.Id == record.StoreId, ct);
        if (store is not null)
        {
            store.SubscriptionStatus = "Active";
            store.IsActive = true;
            if (DateOnly.TryParseExact(store.NextDueDate, "yyyy-MM-dd", out var currentDue) && currentDue > DateOnly.FromDateTime(DateTime.Now))
            {
                store.NextDueDate = currentDue.AddDays(30).ToString("yyyy-MM-dd");
            }
            else
            {
                store.NextDueDate = DateTime.Now.AddDays(30).ToString("yyyy-MM-dd");
            }
            store.UpdatedAt = DateTimeOffset.UtcNow;
            db.Stores.Update(store);
        }

        await db.SaveChangesAsync(ct);

        await AppendLogAsync(
            "BILLING",
            "PaymentConfirmed",
            $"Pagamento de R$ {record.Amount:F2} confirmado para a loja '{record.StoreName}'. Assinatura renovada (+30 dias).",
            record.StoreId,
            record.StoreName,
            ct);

        return MapBillingDto(record);
    }

    public async Task<IReadOnlyList<SystemLogEntryDto>> GetSystemLogsAsync(
        string? level,
        int? storeId,
        string? search,
        int limit,
        CancellationToken ct)
    {
        var query = db.SystemLogEntries.AsQueryable();

        if (!string.IsNullOrWhiteSpace(level) && !string.Equals(level, "ALL", StringComparison.OrdinalIgnoreCase))
        {
            var lvl = level.Trim().ToUpperInvariant();
            query = query.Where(l => l.Level == lvl);
        }

        if (storeId.HasValue && storeId.Value > 0)
        {
            query = query.Where(l => l.StoreId == storeId.Value);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            query = query.Where(l =>
                l.Message.ToLower().Contains(term) ||
                l.Category.ToLower().Contains(term) ||
                (l.StoreName != null && l.StoreName.ToLower().Contains(term)));
        }

        var list = await query
            .OrderByDescending(l => l.Id)
            .Take(Math.Clamp(limit, 10, 300))
            .ToListAsync(ct);

        return list.Select(l => new SystemLogEntryDto(
            l.Id,
            l.Level,
            l.Category,
            l.StoreId,
            l.StoreName,
            l.Message,
            l.CreatedAt)).ToList();
    }

    public async Task AppendLogAsync(
        string level,
        string category,
        string message,
        int? storeId = null,
        string? storeName = null,
        CancellationToken ct = default)
    {
        db.SystemLogEntries.Add(new SystemLogEntry
        {
            Level = level.ToUpperInvariant(),
            Category = category,
            StoreId = storeId,
            StoreName = storeName,
            Message = message,
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task ClearLogsAsync(CancellationToken ct)
    {
        var all = await db.SystemLogEntries.AsTracking().ToListAsync(ct);
        if (all.Count > 0)
        {
            db.SystemLogEntries.RemoveRange(all);
            await db.SaveChangesAsync(ct);
        }

        await AppendLogAsync("SECURITY", "Logs", "Histórico de logs limpo pelo SuperAdministrador (Patricio).", null, null, ct);
    }

    public async Task<byte[]> ExportLogsTxtAsync(CancellationToken ct)
    {
        var logs = await GetSystemLogsAsync(null, null, null, 300, ct);
        var sb = new StringBuilder();
        sb.AppendLine("================================================================================");
        sb.AppendLine($"RELATÓRIO DE LOGS DO SISTEMA — PAINEL SUPERADMIN (PATRICIO)");
        sb.AppendLine($"Exportado em: {DateTime.Now:dd/MM/yyyy HH:mm:ss}");
        sb.AppendLine("================================================================================");
        sb.AppendLine();

        foreach (var l in logs)
        {
            var storeLabel = string.IsNullOrWhiteSpace(l.StoreName) ? "GLOBAL" : l.StoreName;
            sb.AppendLine($"[{l.CreatedAt.ToLocalTime():dd/MM/yyyy HH:mm:ss}] [{l.Level}] [{l.Category}] [{storeLabel}] {l.Message}");
        }

        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static void ApplyDefaultPollTemplatesToStoreEntity(Store store, string storeName)
    {
        store.WelcomePollTitle = $"Seja bem-vindo à {storeName}! Escolha o que fará hoje:";
        store.ServicePollTitle = "Qual vai ser o serviço?";
        store.BarberPollTitle = "Com qual profissional você prefere agendar?";
        store.DatePollTitle = "Escolha o dia para o agendamento:";
        store.TimePollTitle = "Escolha a hora:";
        store.AskNameTodayTemplate = "Entendi! Então para hoje às {Hora}, pode me dizer o seu nome para prosseguir com o agendamento?";
        store.AskNameOtherDayTemplate = "Entendi! Então para o dia {Data} às {Hora}, pode me dizer o seu nome para prosseguir com o agendamento?";
        store.ConfirmationPollTemplate = "Entendi, {Nome}! Então está agendado para {Data}, na hora {Hora}, com {Funcionario}, no custo de R$ {Valor} ({Servico}).";
        store.ConfirmedMessageTemplate = "Entendi! Seu agendamento com {Funcionario} está confirmado. Te aviso faltando uma hora!";
        store.Reminder1hTemplate = $"Olá {{Nome}}! Lembrete da {storeName}: seu atendimento ({{Servico}}) com {{Funcionario}} é daqui a 1 hora, às {{Hora}}!";
        store.Evaluation2hPollTitle = $"Olá {{Nome}}! Como foi seu atendimento na {storeName} hoje? Deseja avaliar?";
        store.EvaluationWindowOpenMessage = "Muito obrigado! Pelos próximos 60 minutos, toda mensagem que você enviar aqui será registrada na sua avaliação para nossa equipe.";
    }

    private void SeedPreconfiguredServicesForStore(
        int storeId,
        string template,
        decimal cortePrice,
        decimal barbaPrice,
        decimal comboPrice,
        decimal sobrancelhaPrice)
    {
        if (string.Equals(template, "empty", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var items = BuildPresetServices(storeId, template, cortePrice, barbaPrice, comboPrice, sobrancelhaPrice);
        db.ServiceItems.AddRange(items);
    }

    private static List<ServiceItem> BuildPresetServices(
        int storeId,
        string template,
        decimal cortePrice,
        decimal barbaPrice,
        decimal comboPrice,
        decimal sobrancelhaPrice)
    {
        if (string.Equals(template, "vip", StringComparison.OrdinalIgnoreCase))
        {
            return
            [
                new ServiceItem { StoreId = storeId, Name = "Corte VIP", Price = 45.00m, DurationMinutes = 30, ColorHex = "#FF6B00", DisplayOrder = 1, IsActive = true },
                new ServiceItem { StoreId = storeId, Name = "Barba Terapia", Price = 35.00m, DurationMinutes = 30, ColorHex = "#2563EB", DisplayOrder = 2, IsActive = true },
                new ServiceItem { StoreId = storeId, Name = "Combo Corte + Barba VIP", Price = 70.00m, DurationMinutes = 60, ColorHex = "#7C3AED", DisplayOrder = 3, IsActive = true },
                new ServiceItem { StoreId = storeId, Name = "Sobrancelha", Price = 20.00m, DurationMinutes = 30, ColorHex = "#059669", DisplayOrder = 4, IsActive = true },
                new ServiceItem { StoreId = storeId, Name = "Pezinho / Acabamento", Price = 15.00m, DurationMinutes = 30, ColorHex = "#D97706", DisplayOrder = 5, IsActive = true }
            ];
        }

        return
        [
            new ServiceItem { StoreId = storeId, Name = "Corte", Price = cortePrice > 0 ? cortePrice : 30.00m, DurationMinutes = 30, ColorHex = "#FF6B00", DisplayOrder = 1, IsActive = true },
            new ServiceItem { StoreId = storeId, Name = "Barba", Price = barbaPrice > 0 ? barbaPrice : 30.00m, DurationMinutes = 30, ColorHex = "#2563EB", DisplayOrder = 2, IsActive = true },
            new ServiceItem { StoreId = storeId, Name = "Corte + Barba", Price = comboPrice > 0 ? comboPrice : 50.00m, DurationMinutes = 60, ColorHex = "#7C3AED", DisplayOrder = 3, IsActive = true },
            new ServiceItem { StoreId = storeId, Name = "Sobrancelha", Price = sobrancelhaPrice > 0 ? sobrancelhaPrice : 15.00m, DurationMinutes = 30, ColorHex = "#059669", DisplayOrder = 4, IsActive = true }
        ];
    }

    private static StoreBillingRecordDto MapBillingDto(StoreBillingRecord b) => new(
        b.Id,
        b.StoreId,
        b.StoreName,
        b.Amount,
        b.DueDate,
        b.Status,
        b.Description,
        b.PaymentLinkOrPix,
        b.CreatedAt,
        b.PaidAt);
}

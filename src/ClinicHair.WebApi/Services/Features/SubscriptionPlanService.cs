using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Services.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Services.Features;

/// <summary>Planos de assinatura que a barbearia vende aos próprios clientes (ex.: "4 cortes/mês").</summary>
public interface ISubscriptionPlanService
{
    Task<IReadOnlyList<CustomerPlanDto>> GetPlansAsync(CancellationToken ct);
    Task<CustomerPlanDto> UpsertPlanAsync(int? id, UpsertCustomerPlanRequest request, CancellationToken ct);
    Task<bool> DeletePlanAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<CustomerSubscriptionDto>> GetSubscriptionsAsync(string? status, CancellationToken ct);
    Task<CustomerSubscriptionDto> SubscribeAsync(CreateSubscriptionRequest request, CancellationToken ct);
    Task<CustomerSubscriptionDto?> SetStatusAsync(int id, string status, CancellationToken ct);
    Task<CustomerSubscriptionDto?> RenewAsync(int id, CancellationToken ct);
    Task<PlansSummaryDto> GetSummaryAsync(CancellationToken ct);
}

public sealed class SubscriptionPlanService(ClinicHairDbContext db, ICurrentStore current, IAuditLogger audit) : ISubscriptionPlanService
{
    public async Task<IReadOnlyList<CustomerPlanDto>> GetPlansAsync(CancellationToken ct)
    {
        var storeId = current.StoreId;
        var plans = await db.CustomerPlans.AsNoTracking().Where(p => p.StoreId == storeId).OrderBy(p => p.Price).ToListAsync(ct);
        var services = await db.ServiceItems.AsNoTracking().Where(s => s.StoreId == storeId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var counts = (await db.CustomerSubscriptions.AsNoTracking()
                .Where(s => s.StoreId == storeId && s.Status == "Active")
                .Select(s => s.PlanId).ToListAsync(ct))
            .GroupBy(x => x).ToDictionary(g => g.Key, g => g.Count());
        return plans.Select(p => ToDto(p, services, counts.GetValueOrDefault(p.Id))).ToList();
    }

    public async Task<CustomerPlanDto> UpsertPlanAsync(int? id, UpsertCustomerPlanRequest r, CancellationToken ct)
    {
        var storeId = current.StoreId;
        if (string.IsNullOrWhiteSpace(r.Name)) throw new ArgumentException("Informe o nome do plano.");
        if (r.Price <= 0 || r.Price > 100000) throw new ArgumentException("Informe um preço válido.");
        if (r.UsesPerMonth is < 0 or > 100) throw new ArgumentException("Quantidade de usos inválida (0 = ilimitado).");
        if (r.ValidityDays is < 7 or > 366) throw new ArgumentException("Validade deve ficar entre 7 e 366 dias.");

        var validServices = await db.ServiceItems.AsNoTracking().Where(s => s.StoreId == storeId).Select(s => s.Id).ToListAsync(ct);
        var included = (r.IncludedServiceIds ?? []).Where(validServices.Contains).Distinct().ToList();
        if (included.Count == 0) throw new ArgumentException("Selecione ao menos um serviço incluso.");

        CustomerPlan plan;
        if (id is null)
        {
            plan = new CustomerPlan { StoreId = storeId };
            db.CustomerPlans.Add(plan);
        }
        else
        {
            plan = await db.CustomerPlans.AsTracking().FirstOrDefaultAsync(p => p.Id == id && p.StoreId == storeId, ct)
                ?? throw new KeyNotFoundException("Plano não encontrado.");
        }

        plan.Name = r.Name.Trim()[..Math.Min(80, r.Name.Trim().Length)];
        plan.Description = string.IsNullOrWhiteSpace(r.Description) ? null : r.Description.Trim()[..Math.Min(400, r.Description.Trim().Length)];
        plan.Price = Math.Round(r.Price, 2);
        plan.IncludedServiceIdsCsv = string.Join(',', included);
        plan.UsesPerMonth = r.UsesPerMonth;
        plan.ValidityDays = r.ValidityDays;
        plan.IsActive = r.IsActive;
        plan.MaxMembers = Math.Clamp(r.MaxMembers, 1, 6);
        plan.PriorityBookingDays = Math.Clamp(r.PriorityBookingDays, 0, 60);
        plan.IsPublic = r.IsPublic;

        audit.Stage("Planos", $"{(id is null ? "Criou" : "Atualizou")} o plano de assinatura '{plan.Name}' ({plan.Price:C}).");
        await db.SaveChangesAsync(ct);
        return (await GetPlansAsync(ct)).First(p => p.Id == plan.Id);
    }

    public async Task<bool> DeletePlanAsync(int id, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var plan = await db.CustomerPlans.AsTracking().FirstOrDefaultAsync(p => p.Id == id && p.StoreId == storeId, ct);
        if (plan is null) return false;

        bool hasSubscribers = await db.CustomerSubscriptions.AnyAsync(s => s.PlanId == id && s.StoreId == storeId && s.Status != "Cancelled", ct);
        if (hasSubscribers)
        {
            plan.IsActive = false; // mantém o histórico dos assinantes; só deixa de ser oferecido
            audit.Stage("Planos", $"Desativou o plano '{plan.Name}' (possui assinantes).");
        }
        else
        {
            db.CustomerPlans.Remove(plan);
            audit.Stage("Planos", $"Excluiu o plano '{plan.Name}'.");
        }
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<CustomerSubscriptionDto>> GetSubscriptionsAsync(string? status, CancellationToken ct)
    {
        var storeId = current.StoreId;
        await RefreshPeriodsAsync(storeId, ct);
        var q = db.CustomerSubscriptions.AsNoTracking().Where(s => s.StoreId == storeId);
        q = string.IsNullOrWhiteSpace(status) || status == "all" ? q.Where(s => s.Status != "Cancelled") : q.Where(s => s.Status == status);
        var subs = await q.OrderBy(s => s.CurrentPeriodEnd).ToListAsync(ct);
        return await MapAsync(storeId, subs, ct);
    }

    public async Task<CustomerSubscriptionDto> SubscribeAsync(CreateSubscriptionRequest r, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == r.CustomerId && c.StoreId == storeId && !c.IsAnonymized, ct)
            ?? throw new KeyNotFoundException("Cliente não encontrado.");
        var plan = await db.CustomerPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == r.PlanId && p.StoreId == storeId && p.IsActive, ct)
            ?? throw new KeyNotFoundException("Plano não encontrado ou inativo.");

        bool already = await db.CustomerSubscriptions.AnyAsync(s => s.StoreId == storeId && s.CustomerId == customer.Id && (s.Status == "Active" || s.Status == "Paused" || s.Status == "Overdue"), ct);
        if (already) throw new InvalidOperationException($"{customer.Name} já possui uma assinatura ativa. Cancele-a antes de trocar de plano.");

        var start = DateOnly.TryParseExact(r.StartDate, "yyyy-MM-dd", out var sd) ? sd : DateOnly.FromDateTime(DateTime.Now);
        var sub = new CustomerSubscription
        {
            StoreId = storeId,
            CustomerId = customer.Id,
            PlanId = plan.Id,
            Status = "Active",
            StartDate = start.ToString("yyyy-MM-dd"),
            CurrentPeriodEnd = start.AddDays(plan.ValidityDays).ToString("yyyy-MM-dd"),
            Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim()[..Math.Min(300, r.Notes.Trim().Length)]
        };
        db.CustomerSubscriptions.Add(sub);
        audit.Stage("Planos", $"Assinou o plano '{plan.Name}' para o cliente #{customer.Id} ({customer.Name}).");
        await db.SaveChangesAsync(ct);
        return (await MapAsync(storeId, [sub], ct))[0];
    }

    public async Task<CustomerSubscriptionDto?> SetStatusAsync(int id, string status, CancellationToken ct)
    {
        if (status is not ("Active" or "Paused" or "Cancelled" or "Overdue")) throw new ArgumentException("Status inválido.");
        var storeId = current.StoreId;
        var sub = await db.CustomerSubscriptions.AsTracking().FirstOrDefaultAsync(s => s.Id == id && s.StoreId == storeId, ct);
        if (sub is null) return null;

        sub.Status = status;
        sub.CancelledAt = status == "Cancelled" ? DateTimeOffset.UtcNow : null;
        audit.Stage("Planos", $"Alterou a assinatura #{sub.Id} para {status}.");
        await db.SaveChangesAsync(ct);
        return (await MapAsync(storeId, [sub], ct))[0];
    }

    public async Task<CustomerSubscriptionDto?> RenewAsync(int id, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var sub = await db.CustomerSubscriptions.AsTracking().FirstOrDefaultAsync(s => s.Id == id && s.StoreId == storeId, ct);
        if (sub is null) return null;
        if (sub.Status == "Cancelled") throw new InvalidOperationException("Assinatura cancelada não pode ser renovada. Crie uma nova.");
        var plan = await db.CustomerPlans.AsNoTracking().FirstAsync(p => p.Id == sub.PlanId, ct);

        var today = DateOnly.FromDateTime(DateTime.Now);
        var currentEnd = DateOnly.TryParseExact(sub.CurrentPeriodEnd, "yyyy-MM-dd", out var ce) ? ce : today;
        var baseDate = currentEnd > today ? currentEnd : today; // pagamento antecipado soma ao período atual
        sub.CurrentPeriodEnd = baseDate.AddDays(plan.ValidityDays).ToString("yyyy-MM-dd");
        sub.UsesThisPeriod = 0;
        sub.Status = "Active";
        audit.Stage("Planos", $"Registrou pagamento/renovação da assinatura #{sub.Id} até {sub.CurrentPeriodEnd}.");
        await db.SaveChangesAsync(ct);
        return (await MapAsync(storeId, [sub], ct))[0];
    }

    public async Task<PlansSummaryDto> GetSummaryAsync(CancellationToken ct)
    {
        var storeId = current.StoreId;
        await RefreshPeriodsAsync(storeId, ct);
        var plans = await db.CustomerPlans.AsNoTracking().Where(p => p.StoreId == storeId).ToListAsync(ct);
        var active = await db.CustomerSubscriptions.AsNoTracking().Where(s => s.StoreId == storeId && s.Status == "Active").ToListAsync(ct);
        var in7 = DateOnly.FromDateTime(DateTime.Now).AddDays(7).ToString("yyyy-MM-dd");
        decimal mrr = active.Sum(s =>
        {
            var p = plans.FirstOrDefault(x => x.Id == s.PlanId);
            return p is null ? 0 : p.Price * 30m / Math.Max(1, p.ValidityDays);
        });
        return new PlansSummaryDto(plans.Count(p => p.IsActive), active.Count, Math.Round(mrr, 2),
            active.Count(s => string.CompareOrdinal(s.CurrentPeriodEnd, in7) <= 0));
    }

    /// <summary>
    /// Assinaturas ativas com período vencido há mais que a carência da loja passam para "Em atraso"
    /// até o pagamento ser registrado (item 60). Dentro da carência continuam ativas.
    /// </summary>
    private async Task RefreshPeriodsAsync(int storeId, CancellationToken ct)
    {
        var grace = await db.Stores.AsNoTracking().Where(s => s.Id == storeId).Select(s => s.SubscriptionGraceDays).FirstOrDefaultAsync(ct);
        var today = DateTime.Now.AddDays(-Math.Clamp(grace, 0, 30)).ToString("yyyy-MM-dd");
        var due = await db.CustomerSubscriptions.AsTracking()
            .Where(s => s.StoreId == storeId && s.Status == "Active" && string.Compare(s.CurrentPeriodEnd, today) < 0)
            .ToListAsync(ct);
        if (due.Count == 0) return;
        foreach (var s in due) s.Status = "Overdue";
        await db.SaveChangesAsync(ct);
    }

    private async Task<List<CustomerSubscriptionDto>> MapAsync(int storeId, IReadOnlyList<CustomerSubscription> subs, CancellationToken ct)
    {
        var customerIds = subs.Select(s => s.CustomerId).Distinct().ToList();
        var customers = await db.Customers.AsNoTracking().Where(c => customerIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        var plans = await db.CustomerPlans.AsNoTracking().Where(p => p.StoreId == storeId).ToDictionaryAsync(p => p.Id, ct);
        var subIds = subs.Select(s => s.Id).ToList();
        var members = await db.SubscriptionMembers.AsNoTracking().Where(m => subIds.Contains(m.SubscriptionId)).ToListAsync(ct);
        var memberIds = members.Select(m => m.CustomerId).Distinct().ToList();
        var memberCustomers = await db.Customers.AsNoTracking().Where(c => memberIds.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
        return subs.Select(s =>
        {
            var c = customers.GetValueOrDefault(s.CustomerId);
            var p = plans.GetValueOrDefault(s.PlanId);
            var m = members.Where(x => x.SubscriptionId == s.Id)
                .Select(x => memberCustomers.GetValueOrDefault(x.CustomerId))
                .Where(x => x is not null)
                .Select(x => new SubscriptionMemberDto(x!.Id, x.Name, x.Phone))
                .ToList();
            return new CustomerSubscriptionDto(s.Id, s.CustomerId, c?.Name ?? "Cliente", c?.Phone ?? "", s.PlanId, p?.Name ?? "Plano", p?.Price ?? 0,
                s.Status, s.StartDate, s.CurrentPeriodEnd, s.UsesThisPeriod, p?.UsesPerMonth ?? 0, s.Notes, s.CreditBalance, m, p?.MaxMembers ?? 1);
        }).ToList();
    }

    private static CustomerPlanDto ToDto(CustomerPlan p, Dictionary<int, string> services, int subscribers)
    {
        var ids = p.IncludedServiceIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i > 0).ToList();
        return new CustomerPlanDto(p.Id, p.Name, p.Description, p.Price, ids,
            ids.Select(i => services.GetValueOrDefault(i, "Serviço removido")).ToList(),
            p.UsesPerMonth, p.ValidityDays, p.IsActive, subscribers, p.MaxMembers, p.PriorityBookingDays, p.IsPublic);
    }
}

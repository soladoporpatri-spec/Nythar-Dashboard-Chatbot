using System.Globalization;
using System.Security.Cryptography;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Services;
using ClinicHair.WebApi.Services.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Services.Features;

// =====================================================================================================
// ASSINATURAS DO CLIENTE — ciclo de vida (itens 57 a 65)
// =====================================================================================================
public interface ISubscriptionLifecycle
{
    /// <summary>Desconta um uso da assinatura quando um serviço incluso é concluído (item 57). Retorna uma nota ou null.</summary>
    Task<string?> ConsumeUsageAsync(Appointment appt, CancellationToken ct);
    Task<ChangePlanResultDto> ChangePlanAsync(int subscriptionId, int newPlanId, CancellationToken ct);
    Task<CustomerSubscriptionDto> AddMemberAsync(int subscriptionId, int customerId, CancellationToken ct);
    Task<bool> RemoveMemberAsync(int subscriptionId, int customerId, CancellationToken ct);
    Task<SubscriptionsReportDto> GetReportAsync(CancellationToken ct);
    Task<PixChargeDto> GetPixChargeAsync(int subscriptionId, CancellationToken ct);
    /// <summary>Lembretes de renovação com PIX copia-e-cola (itens 58 e 59). Usado pelo EngagementWorker.</summary>
    Task<int> SendRenewalRemindersAsync(Store store, IWhatsAppBridgeClient bridge, CancellationToken ct);
    /// <summary>Comandos do cliente pelo WhatsApp: ASSINATURA, PAUSAR/CANCELAR/REATIVAR ASSINATURA (item 61).</summary>
    Task<string?> HandleCustomerCommandAsync(Store store, string phone, string normalizedText, CancellationToken ct);
    /// <summary>Quantos dias à frente este telefone pode agendar (assinantes têm janela prioritária, item 63).</summary>
    Task<int> GetBookingHorizonDaysAsync(Store store, string? phone, CancellationToken ct);
}

public sealed class SubscriptionLifecycle(
    ClinicHairDbContext db,
    ICurrentStore current,
    IAuditLogger audit,
    ISubscriptionPlanService plans,
    ILogger<SubscriptionLifecycle> logger) : ISubscriptionLifecycle
{
    private static readonly string[] Live = ["Active", "Paused", "Overdue"];

    public async Task<string?> ConsumeUsageAsync(Appointment appt, CancellationToken ct)
    {
        var sub = await FindCoveringSubscriptionAsync(appt.StoreId, appt.CustomerId, ct);
        if (sub is null) return null;
        var plan = await db.CustomerPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
        if (plan is null) return null;
        var included = plan.IncludedServiceIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var i) ? i : 0);
        if (!included.Contains(appt.ServiceItemId)) return null;
        if (plan.UsesPerMonth > 0 && sub.UsesThisPeriod >= plan.UsesPerMonth)
        {
            return $"Assinatura '{plan.Name}' sem saldo neste período ({sub.UsesThisPeriod}/{plan.UsesPerMonth}); cobrado normalmente.";
        }

        sub.UsesThisPeriod += 1;
        appt.PaymentMethod = "Assinatura";
        appt.PriceCharged = 0;
        string saldo = plan.UsesPerMonth == 0 ? "ilimitado" : $"{sub.UsesThisPeriod}/{plan.UsesPerMonth}";
        return $"Coberto pela assinatura '{plan.Name}' (uso {saldo}).";
    }

    private async Task<CustomerSubscription?> FindCoveringSubscriptionAsync(int storeId, int customerId, CancellationToken ct)
    {
        var own = await db.CustomerSubscriptions.AsTracking()
            .FirstOrDefaultAsync(s => s.StoreId == storeId && s.CustomerId == customerId && s.Status == "Active", ct);
        if (own is not null) return own;
        var memberOf = await db.SubscriptionMembers.AsNoTracking().Where(m => m.CustomerId == customerId).Select(m => m.SubscriptionId).ToListAsync(ct);
        if (memberOf.Count == 0) return null;
        return await db.CustomerSubscriptions.AsTracking()
            .FirstOrDefaultAsync(s => s.StoreId == storeId && memberOf.Contains(s.Id) && s.Status == "Active", ct);
    }

    public async Task<ChangePlanResultDto> ChangePlanAsync(int subscriptionId, int newPlanId, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var sub = await db.CustomerSubscriptions.AsTracking().FirstOrDefaultAsync(s => s.Id == subscriptionId && s.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Assinatura não encontrada.");
        if (sub.Status == "Cancelled") throw new InvalidOperationException("Assinatura cancelada não pode trocar de plano.");
        if (sub.PlanId == newPlanId) throw new ArgumentException("O cliente já está neste plano.");
        var oldPlan = await db.CustomerPlans.AsNoTracking().FirstAsync(p => p.Id == sub.PlanId, ct);
        var newPlan = await db.CustomerPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == newPlanId && p.StoreId == storeId && p.IsActive, ct)
            ?? throw new KeyNotFoundException("Novo plano não encontrado ou inativo.");

        // Pró-rata: dias restantes do período atual viram crédito proporcional ao preço do plano antigo
        var today = DateOnly.FromDateTime(DateTime.Now);
        var end = DateOnly.TryParseExact(sub.CurrentPeriodEnd, "yyyy-MM-dd", out var e) ? e : today;
        int remaining = Math.Max(0, end.DayNumber - today.DayNumber);
        decimal credit = sub.Status == "Active"
            ? Math.Round(oldPlan.Price * remaining / Math.Max(1, oldPlan.ValidityDays), 2)
            : 0;
        // Usos já consumidos reduzem o crédito proporcionalmente (evita trocar depois de usar tudo)
        if (oldPlan.UsesPerMonth > 0 && sub.UsesThisPeriod > 0)
        {
            decimal unused = Math.Max(0, oldPlan.UsesPerMonth - sub.UsesThisPeriod) / (decimal)oldPlan.UsesPerMonth;
            credit = Math.Round(Math.Min(credit, oldPlan.Price * unused), 2);
        }
        credit += sub.CreditBalance;
        decimal due = Math.Max(0, newPlan.Price - credit);
        decimal carried = Math.Max(0, credit - newPlan.Price);

        sub.PlanId = newPlan.Id;
        sub.StartDate = today.ToString("yyyy-MM-dd");
        sub.CurrentPeriodEnd = today.AddDays(newPlan.ValidityDays).ToString("yyyy-MM-dd");
        sub.UsesThisPeriod = 0;
        sub.CreditBalance = carried;
        sub.Status = "Active";
        sub.LastRenewalReminderFor = null;
        audit.Stage("Planos", $"Trocou a assinatura #{sub.Id} de '{oldPlan.Name}' para '{newPlan.Name}' (crédito {credit:C}, a pagar {due:C}).");
        await db.SaveChangesAsync(ct);

        var dto = (await plans.GetSubscriptionsAsync("all", ct)).First(s => s.Id == sub.Id);
        return new ChangePlanResultDto(dto, credit, due, carried);
    }

    public async Task<CustomerSubscriptionDto> AddMemberAsync(int subscriptionId, int customerId, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var sub = await db.CustomerSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subscriptionId && s.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Assinatura não encontrada.");
        var plan = await db.CustomerPlans.AsNoTracking().FirstAsync(p => p.Id == sub.PlanId, ct);
        if (plan.MaxMembers <= 1) throw new InvalidOperationException("Este plano é individual. Use um plano família/dupla para adicionar membros.");
        if (customerId == sub.CustomerId) throw new ArgumentException("O titular já faz parte da assinatura.");
        var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == customerId && c.StoreId == storeId && !c.IsAnonymized, ct)
            ?? throw new KeyNotFoundException("Cliente não encontrado.");

        var members = await db.SubscriptionMembers.AsNoTracking().Where(m => m.SubscriptionId == sub.Id).ToListAsync(ct);
        if (members.Any(m => m.CustomerId == customerId)) throw new InvalidOperationException("Cliente já é membro desta assinatura.");
        if (members.Count + 1 >= plan.MaxMembers) throw new InvalidOperationException($"O plano permite no máximo {plan.MaxMembers} pessoas (titular incluso).");
        bool hasOwn = await db.CustomerSubscriptions.AnyAsync(s => s.StoreId == storeId && s.CustomerId == customerId && Live.Contains(s.Status), ct);
        if (hasOwn) throw new InvalidOperationException($"{customer.Name} já tem uma assinatura própria.");

        db.SubscriptionMembers.Add(new SubscriptionMember { SubscriptionId = sub.Id, CustomerId = customerId });
        audit.Stage("Planos", $"Adicionou {customer.Name} à assinatura família #{sub.Id}.");
        await db.SaveChangesAsync(ct);
        return (await plans.GetSubscriptionsAsync("all", ct)).First(s => s.Id == sub.Id);
    }

    public async Task<bool> RemoveMemberAsync(int subscriptionId, int customerId, CancellationToken ct)
    {
        var storeId = current.StoreId;
        bool owns = await db.CustomerSubscriptions.AnyAsync(s => s.Id == subscriptionId && s.StoreId == storeId, ct);
        if (!owns) return false;
        var m = await db.SubscriptionMembers.AsTracking().FirstOrDefaultAsync(x => x.SubscriptionId == subscriptionId && x.CustomerId == customerId, ct);
        if (m is null) return false;
        db.SubscriptionMembers.Remove(m);
        audit.Stage("Planos", $"Removeu o cliente #{customerId} da assinatura #{subscriptionId}.");
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<SubscriptionsReportDto> GetReportAsync(CancellationToken ct)
    {
        var storeId = current.StoreId;
        _ = await plans.GetSummaryAsync(ct); // atualiza status de vencidas
        var allPlans = await db.CustomerPlans.AsNoTracking().Where(p => p.StoreId == storeId).ToListAsync(ct);
        var subs = await db.CustomerSubscriptions.AsNoTracking().Where(s => s.StoreId == storeId).ToListAsync(ct);
        var since = DateTimeOffset.UtcNow.AddDays(-30);
        var active = subs.Where(s => s.Status == "Active").ToList();
        decimal Monthly(CustomerSubscription s)
        {
            var p = allPlans.FirstOrDefault(x => x.Id == s.PlanId);
            return p is null ? 0 : p.Price * 30m / Math.Max(1, p.ValidityDays);
        }
        int cancelled30 = subs.Count(s => s.Status == "Cancelled" && s.CancelledAt >= since);
        int base30 = active.Count + cancelled30;
        var byPlan = allPlans.Select(p =>
        {
            var mine = active.Where(s => s.PlanId == p.Id).ToList();
            return new PlanBreakdownDto(p.Id, p.Name, mine.Count, Math.Round(mine.Sum(Monthly), 2));
        }).OrderByDescending(x => x.Active).ToList();

        return new SubscriptionsReportDto(
            active.Count,
            subs.Count(s => s.Status == "Paused"),
            subs.Count(s => s.Status == "Overdue"),
            cancelled30,
            subs.Count(s => s.CreatedAt >= since),
            Math.Round(active.Sum(Monthly), 2),
            base30 == 0 ? 0 : Math.Round(cancelled30 * 100m / base30, 1),
            active.Count == 0 ? 0 : Math.Round(active.Average(s => s.UsesThisPeriod), 1),
            byPlan);
    }

    public async Task<PixChargeDto> GetPixChargeAsync(int subscriptionId, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var store = await StoreQueries.LoadAsync(db, storeId, false, ct);
        var sub = await db.CustomerSubscriptions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == subscriptionId && s.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Assinatura não encontrada.");
        var plan = await db.CustomerPlans.AsNoTracking().FirstAsync(p => p.Id == sub.PlanId, ct);
        return BuildCharge(store, sub, plan);
    }

    private static PixChargeDto BuildCharge(Store store, CustomerSubscription sub, CustomerPlan plan)
    {
        if (string.IsNullOrWhiteSpace(store.PixKey)) throw new InvalidOperationException("Cadastre a chave PIX da loja em Loja › Recursos para gerar cobranças.");
        decimal amount = Math.Max(0, plan.Price - sub.CreditBalance);
        var code = PixBrCode.Build(store.PixKey!, store.PixMerchantName ?? store.Name, store.PixMerchantCity ?? "BRASIL", amount, $"ASS{sub.Id}P{sub.CurrentPeriodEnd.Replace("-", "")}");
        return new PixChargeDto(amount, code, $"{plan.Name} — renovação até {sub.CurrentPeriodEnd}");
    }

    public async Task<int> SendRenewalRemindersAsync(Store store, IWhatsAppBridgeClient bridge, CancellationToken ct)
    {
        if (store.RenewalReminderDays <= 0) return 0;
        var limit = DateOnly.FromDateTime(DateTime.Now).AddDays(store.RenewalReminderDays).ToString("yyyy-MM-dd");
        var due = await db.CustomerSubscriptions.AsTracking()
            .Where(s => s.StoreId == store.Id && (s.Status == "Active" || s.Status == "Overdue")
                && string.Compare(s.CurrentPeriodEnd, limit) <= 0
                && (s.LastRenewalReminderFor == null || s.LastRenewalReminderFor != s.CurrentPeriodEnd))
            .Take(5)
            .ToListAsync(ct);
        int sent = 0;
        foreach (var sub in due)
        {
            var plan = await db.CustomerPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
            var customer = await db.Customers.AsNoTracking().FirstOrDefaultAsync(c => c.Id == sub.CustomerId, ct);
            sub.LastRenewalReminderFor = sub.CurrentPeriodEnd;
            if (plan is null || customer is null || customer.IsAnonymized) continue;
            var digits = PhoneIdentity.Canonical(customer.Phone);
            if (digits.Length < 10) continue;

            var first = customer.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? customer.Name;
            var endBr = DateOnly.TryParseExact(sub.CurrentPeriodEnd, "yyyy-MM-dd", out var d) ? d.ToString("dd/MM") : sub.CurrentPeriodEnd;
            var msg = $"Olá {first}! Sua assinatura *{plan.Name}* na {store.Name} vence em {endBr}. Valor da renovação: R$ {Math.Max(0, plan.Price - sub.CreditBalance):N2}.";
            if (!string.IsNullOrWhiteSpace(store.PixKey))
            {
                try
                {
                    var charge = BuildCharge(store, sub, plan);
                    msg += "\n\nPIX copia e cola:\n" + charge.CopyPaste;
                }
                catch (Exception ex) { logger.LogWarning(ex, "Falha ao gerar PIX da assinatura {Id}.", sub.Id); }
            }
            msg += "\n\nResponda ASSINATURA para ver seu saldo, ou PAUSAR ASSINATURA / CANCELAR ASSINATURA.";
            try
            {
                await bridge.SendTextAsync($"{digits}@s.whatsapp.net", msg, ct);
                sent++;
            }
            catch (Exception ex) { logger.LogWarning(ex, "Falha ao enviar lembrete de renovação da assinatura {Id}.", sub.Id); }
        }
        if (due.Count > 0) await db.SaveChangesAsync(ct);
        return sent;
    }

    public async Task<string?> HandleCustomerCommandAsync(Store store, string phone, string n, CancellationToken ct)
    {
        bool status = n is "assinatura" or "minha assinatura" or "meu plano" or "plano";
        bool pause = n is "pausar assinatura" or "pausar plano";
        bool cancel = n is "cancelar assinatura" or "cancelar plano";
        bool resume = n is "reativar assinatura" or "retomar assinatura" or "reativar plano";
        bool points = n is "pontos" or "meus pontos" or "fidelidade";
        if (!status && !pause && !cancel && !resume && !points) return null;

        var digits = PhoneIdentity.Canonical(phone);
        var customers = await db.Customers.AsNoTracking().Where(c => c.StoreId == store.Id && !c.IsAnonymized).Select(c => new { c.Id, c.Phone, c.LoyaltyPoints }).ToListAsync(ct);
        var customer = customers.FirstOrDefault(c => PhoneIdentity.Canonical(c.Phone) == digits);
        if (points)
        {
            if (!store.LoyaltyEnabled) return null;
            int pts = customer?.LoyaltyPoints ?? 0;
            int missing = Math.Max(0, store.LoyaltyRewardPoints - pts);
            return missing == 0
                ? $"Você tem *{pts} pontos* e já pode trocar por: {store.LoyaltyRewardDescription}! Avise no balcão no próximo atendimento."
                : $"Você tem *{pts} pontos*. Faltam {missing} para ganhar: {store.LoyaltyRewardDescription}.";
        }
        if (customer is null) return "Não encontrei nenhuma assinatura ligada a este número. Fale com a equipe para conhecer nossos planos!";

        var sub = await db.CustomerSubscriptions.AsTracking()
            .Where(s => s.StoreId == store.Id && s.CustomerId == customer.Id && Live.Contains(s.Status))
            .OrderByDescending(s => s.Id).FirstOrDefaultAsync(ct);
        if (sub is null) return "Você não tem uma assinatura ativa no momento. Fale com a equipe para conhecer nossos planos!";
        var plan = await db.CustomerPlans.AsNoTracking().FirstAsync(p => p.Id == sub.PlanId, ct);
        var endBr = DateOnly.TryParseExact(sub.CurrentPeriodEnd, "yyyy-MM-dd", out var d) ? d.ToString("dd/MM/yyyy") : sub.CurrentPeriodEnd;

        string Log(string what)
        {
            db.SystemLogEntries.Add(new SystemLogEntry { Level = "AUDIT", Category = "Planos", StoreId = store.Id, Message = $"[cliente WhatsApp] {what} a assinatura #{sub.Id}.", CreatedAt = DateTimeOffset.UtcNow });
            return what;
        }

        if (pause)
        {
            if (sub.Status != "Active") return $"Sua assinatura está com status {Label(sub.Status)} e não pode ser pausada agora.";
            sub.Status = "Paused";
            Log("Pausou");
            await db.SaveChangesAsync(ct);
            return $"Pronto, sua assinatura *{plan.Name}* foi pausada. Para voltar, envie REATIVAR ASSINATURA.";
        }
        if (resume)
        {
            if (sub.Status != "Paused") return $"Sua assinatura está {Label(sub.Status)}.";
            sub.Status = "Active";
            Log("Reativou");
            await db.SaveChangesAsync(ct);
            return $"Assinatura *{plan.Name}* reativada! Válida até {endBr}.";
        }
        if (cancel)
        {
            sub.Status = "Cancelled";
            sub.CancelledAt = DateTimeOffset.UtcNow;
            Log("Cancelou");
            await db.SaveChangesAsync(ct);
            return $"Sua assinatura *{plan.Name}* foi cancelada. Sentiremos sua falta! Se mudar de ideia, é só falar com a equipe.";
        }

        string usos = plan.UsesPerMonth == 0 ? "uso ilimitado" : $"{Math.Max(0, plan.UsesPerMonth - sub.UsesThisPeriod)} de {plan.UsesPerMonth} uso(s) restantes";
        return $"Sua assinatura: *{plan.Name}* — {Label(sub.Status)}.\nVálida até {endBr}, {usos}.\n\nComandos: PAUSAR ASSINATURA, REATIVAR ASSINATURA ou CANCELAR ASSINATURA.";
    }

    private static string Label(string status) => status switch
    {
        "Active" => "ativa",
        "Paused" => "pausada",
        "Overdue" => "em atraso",
        "Cancelled" => "cancelada",
        _ => status
    };

    public async Task<int> GetBookingHorizonDaysAsync(Store store, string? phone, CancellationToken ct)
    {
        int baseDays = Math.Clamp(store.BookingHorizonDays, 1, 90);
        var digits = PhoneIdentity.Canonical(phone);
        if (digits.Length < 10) return baseDays;
        var ids = (await db.Customers.AsNoTracking().Where(c => c.StoreId == store.Id).Select(c => new { c.Id, c.Phone }).ToListAsync(ct))
            .Where(c => PhoneIdentity.Canonical(c.Phone) == digits).Select(c => c.Id).ToList();
        if (ids.Count == 0) return baseDays;
        var memberSubs = await db.SubscriptionMembers.AsNoTracking().Where(m => ids.Contains(m.CustomerId)).Select(m => m.SubscriptionId).ToListAsync(ct);
        var planIds = await db.CustomerSubscriptions.AsNoTracking()
            .Where(s => s.StoreId == store.Id && s.Status == "Active" && (ids.Contains(s.CustomerId) || memberSubs.Contains(s.Id)))
            .Select(s => s.PlanId).ToListAsync(ct);
        if (planIds.Count == 0) return baseDays;
        int extra = await db.CustomerPlans.AsNoTracking().Where(p => planIds.Contains(p.Id)).Select(p => p.PriorityBookingDays).DefaultIfEmpty(0).MaxAsync(ct);
        return Math.Clamp(baseDays + extra, 1, 150);
    }
}

/// <summary>Efeitos de concluir um atendimento: desconta a assinatura e credita pontos de fidelidade.</summary>
public interface ICompletionHooks
{
    Task<string?> OnCompletedAsync(Appointment appt, CancellationToken ct);
}

public sealed class CompletionHooks(ISubscriptionLifecycle subscriptions, ILoyaltyService loyalty, ILogger<CompletionHooks> logger) : ICompletionHooks
{
    public async Task<string?> OnCompletedAsync(Appointment appt, CancellationToken ct)
    {
        var notes = new List<string>();
        try
        {
            var sub = await subscriptions.ConsumeUsageAsync(appt, ct);
            if (sub is not null) notes.Add(sub);
            int pts = await loyalty.AwardForAppointmentAsync(appt, ct);
            if (pts > 0) notes.Add($"+{pts} pontos de fidelidade.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Falha ao aplicar assinatura/fidelidade no agendamento {Id}.", appt.Id);
        }
        return notes.Count == 0 ? null : string.Join(" ", notes);
    }
}

// =====================================================================================================
// FIDELIDADE (item 97)
// =====================================================================================================
public interface ILoyaltyService
{
    Task<int> AwardForAppointmentAsync(Appointment appt, CancellationToken ct);
    Task<LoyaltyBalanceDto> GetAsync(int customerId, CancellationToken ct);
    Task<LoyaltyBalanceDto> RedeemAsync(int customerId, CancellationToken ct);
    Task<LoyaltyBalanceDto> AdjustAsync(int customerId, LoyaltyAdjustRequest request, CancellationToken ct);
}

public sealed class LoyaltyService(ClinicHairDbContext db, ICurrentStore current, IAuditLogger audit) : ILoyaltyService
{
    public async Task<int> AwardForAppointmentAsync(Appointment appt, CancellationToken ct)
    {
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == appt.StoreId, ct);
        if (store is null || !store.LoyaltyEnabled) return 0;
        bool already = await db.LoyaltyTransactions.AnyAsync(t => t.AppointmentId == appt.Id && t.Points > 0, ct);
        if (already) return 0;
        // Atendimento coberto por assinatura vale pontos pelo preço do serviço
        decimal basis = appt.PriceCharged;
        if (basis <= 0)
        {
            basis = await db.ServiceItems.AsNoTracking().Where(s => s.Id == appt.ServiceItemId).Select(s => s.Price).FirstOrDefaultAsync(ct);
        }
        int points = (int)Math.Floor(basis * Math.Max(0, store.LoyaltyPointsPerReal));
        if (points <= 0) return 0;
        var customer = await db.Customers.AsTracking().FirstOrDefaultAsync(c => c.Id == appt.CustomerId, ct);
        if (customer is null || customer.IsAnonymized) return 0;
        customer.LoyaltyPoints += points;
        db.LoyaltyTransactions.Add(new LoyaltyTransaction
        {
            StoreId = appt.StoreId, CustomerId = customer.Id, Points = points, AppointmentId = appt.Id,
            Reason = $"Atendimento de {appt.AppointmentDate}"
        });
        return points;
    }

    public async Task<LoyaltyBalanceDto> GetAsync(int customerId, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var store = await StoreQueries.LoadAsync(db, storeId, false, ct);
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId && x.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Cliente não encontrado.");
        var history = await db.LoyaltyTransactions.AsNoTracking()
            .Where(t => t.StoreId == storeId && t.CustomerId == customerId)
            .OrderByDescending(t => t.Id).Take(50)
            .Select(t => new LoyaltyEntryDto(t.Id, t.Points, t.Reason, t.CreatedAt))
            .ToListAsync(ct);
        return new LoyaltyBalanceDto(c.Id, c.Name, c.LoyaltyPoints, store.LoyaltyRewardPoints, store.LoyaltyRewardDescription, history);
    }

    public async Task<LoyaltyBalanceDto> RedeemAsync(int customerId, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var store = await StoreQueries.LoadAsync(db, storeId, false, ct);
        var c = await db.Customers.AsTracking().FirstOrDefaultAsync(x => x.Id == customerId && x.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Cliente não encontrado.");
        if (store.LoyaltyRewardPoints <= 0) throw new InvalidOperationException("Configure a quantidade de pontos do prêmio.");
        if (c.LoyaltyPoints < store.LoyaltyRewardPoints)
            throw new InvalidOperationException($"{c.Name} tem {c.LoyaltyPoints} pontos; o prêmio exige {store.LoyaltyRewardPoints}.");
        c.LoyaltyPoints -= store.LoyaltyRewardPoints;
        db.LoyaltyTransactions.Add(new LoyaltyTransaction { StoreId = storeId, CustomerId = c.Id, Points = -store.LoyaltyRewardPoints, Reason = $"Resgate: {store.LoyaltyRewardDescription}" });
        audit.Stage("Fidelidade", $"Resgatou '{store.LoyaltyRewardDescription}' para {c.Name}.");
        await db.SaveChangesAsync(ct);
        return await GetAsync(customerId, ct);
    }

    public async Task<LoyaltyBalanceDto> AdjustAsync(int customerId, LoyaltyAdjustRequest r, CancellationToken ct)
    {
        if (r.Points == 0 || Math.Abs(r.Points) > 10000) throw new ArgumentException("Informe um ajuste entre -10000 e 10000 pontos.");
        var storeId = current.StoreId;
        var c = await db.Customers.AsTracking().FirstOrDefaultAsync(x => x.Id == customerId && x.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Cliente não encontrado.");
        c.LoyaltyPoints = Math.Max(0, c.LoyaltyPoints + r.Points);
        var reason = string.IsNullOrWhiteSpace(r.Reason) ? "Ajuste manual" : r.Reason.Trim()[..Math.Min(120, r.Reason.Trim().Length)];
        db.LoyaltyTransactions.Add(new LoyaltyTransaction { StoreId = storeId, CustomerId = c.Id, Points = r.Points, Reason = reason });
        audit.Stage("Fidelidade", $"Ajustou {r.Points:+#;-#} pontos de {c.Name} ({reason}).");
        await db.SaveChangesAsync(ct);
        return await GetAsync(customerId, ct);
    }
}

// =====================================================================================================
// ESTOQUE (item 99)
// =====================================================================================================
public interface IStockService
{
    Task<IReadOnlyList<StockItemDto>> GetItemsAsync(CancellationToken ct);
    Task<StockItemDto> UpsertAsync(int? id, UpsertStockItemRequest request, CancellationToken ct);
    Task<bool> DeleteAsync(int id, CancellationToken ct);
    Task<StockItemDto> MoveAsync(int id, StockMovementRequest request, CancellationToken ct);
    Task<IReadOnlyList<StockMovementDto>> GetMovementsAsync(int? itemId, CancellationToken ct);
}

public sealed class StockService(ClinicHairDbContext db, ICurrentStore current, IAuditLogger audit) : IStockService
{
    private static readonly string[] Reasons = ["Entrada", "Venda", "Uso", "Ajuste", "Perda"];

    private static StockItemDto ToDto(StockItem i) => new(i.Id, i.Name, i.Sku, i.Quantity, i.MinQuantity, i.UnitCost, i.SalePrice, i.IsActive, i.IsActive && i.Quantity <= i.MinQuantity);

    public async Task<IReadOnlyList<StockItemDto>> GetItemsAsync(CancellationToken ct)
    {
        var storeId = current.StoreId;
        var items = await db.StockItems.AsNoTracking().Where(i => i.StoreId == storeId).OrderBy(i => i.Name).ToListAsync(ct);
        return items.Select(ToDto).ToList();
    }

    public async Task<StockItemDto> UpsertAsync(int? id, UpsertStockItemRequest r, CancellationToken ct)
    {
        var storeId = current.StoreId;
        if (string.IsNullOrWhiteSpace(r.Name)) throw new ArgumentException("Informe o nome do produto.");
        if (r.MinQuantity < 0 || r.UnitCost < 0 || r.SalePrice < 0) throw new ArgumentException("Valores não podem ser negativos.");
        StockItem item;
        if (id is null)
        {
            item = new StockItem { StoreId = storeId, Quantity = Math.Max(0, r.InitialQuantity ?? 0) };
            db.StockItems.Add(item);
        }
        else
        {
            item = await db.StockItems.AsTracking().FirstOrDefaultAsync(i => i.Id == id && i.StoreId == storeId, ct)
                ?? throw new KeyNotFoundException("Produto não encontrado.");
        }
        var name = r.Name.Trim();
        item.Name = name[..Math.Min(100, name.Length)];
        item.Sku = string.IsNullOrWhiteSpace(r.Sku) ? null : r.Sku.Trim()[..Math.Min(40, r.Sku.Trim().Length)];
        item.MinQuantity = r.MinQuantity;
        item.UnitCost = Math.Round(r.UnitCost, 2);
        item.SalePrice = Math.Round(r.SalePrice, 2);
        item.IsActive = r.IsActive;
        audit.Stage("Estoque", $"{(id is null ? "Cadastrou" : "Atualizou")} o produto '{item.Name}'.");
        await db.SaveChangesAsync(ct);
        if (id is null && item.Quantity > 0)
        {
            db.StockMovements.Add(new StockMovement { StoreId = storeId, StockItemId = item.Id, Delta = item.Quantity, Reason = "Entrada", CreatedBy = current.Username });
            await db.SaveChangesAsync(ct);
        }
        return ToDto(item);
    }

    public async Task<bool> DeleteAsync(int id, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var item = await db.StockItems.AsTracking().FirstOrDefaultAsync(i => i.Id == id && i.StoreId == storeId, ct);
        if (item is null) return false;
        item.IsActive = false; // mantém o histórico de movimentações
        audit.Stage("Estoque", $"Desativou o produto '{item.Name}'.");
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<StockItemDto> MoveAsync(int id, StockMovementRequest r, CancellationToken ct)
    {
        var storeId = current.StoreId;
        if (r.Delta == 0 || Math.Abs(r.Delta) > 100000) throw new ArgumentException("Quantidade inválida.");
        var reason = Reasons.FirstOrDefault(x => x.Equals(r.Reason, StringComparison.OrdinalIgnoreCase)) ?? "Ajuste";
        var item = await db.StockItems.AsTracking().FirstOrDefaultAsync(i => i.Id == id && i.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Produto não encontrado.");
        decimal delta = reason is "Venda" or "Uso" or "Perda" ? -Math.Abs(r.Delta) : reason == "Entrada" ? Math.Abs(r.Delta) : r.Delta;
        if (item.Quantity + delta < 0) throw new InvalidOperationException($"Estoque insuficiente de '{item.Name}' (há {item.Quantity:0.##}).");
        item.Quantity += delta;
        db.StockMovements.Add(new StockMovement { StoreId = storeId, StockItemId = item.Id, Delta = delta, Reason = reason, CreatedBy = current.Username });
        await db.SaveChangesAsync(ct);
        return ToDto(item);
    }

    public async Task<IReadOnlyList<StockMovementDto>> GetMovementsAsync(int? itemId, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var q = db.StockMovements.AsNoTracking().Where(m => m.StoreId == storeId);
        if (itemId.HasValue) q = q.Where(m => m.StockItemId == itemId.Value);
        var list = await q.OrderByDescending(m => m.Id).Take(200).ToListAsync(ct);
        var names = await db.StockItems.AsNoTracking().Where(i => i.StoreId == storeId).ToDictionaryAsync(i => i.Id, i => i.Name, ct);
        return list.Select(m => new StockMovementDto(m.Id, m.StockItemId, names.GetValueOrDefault(m.StockItemId, "Produto"), m.Delta, m.Reason, m.CreatedBy, m.CreatedAt)).ToList();
    }
}

// =====================================================================================================
// BUSCA GLOBAL (item 82)
// =====================================================================================================
public interface ISearchService
{
    Task<IReadOnlyList<SearchHitDto>> SearchAsync(string? q, CancellationToken ct);
}

public sealed class SearchService(ClinicHairDbContext db, ICurrentStore current) : ISearchService
{
    public async Task<IReadOnlyList<SearchHitDto>> SearchAsync(string? q, CancellationToken ct)
    {
        q = (q ?? "").Trim();
        if (q.Length < 2) return [];
        var storeId = current.StoreId;
        var lower = q.ToLower();
        var digits = new string(q.Where(char.IsDigit).ToArray());
        var hits = new List<SearchHitDto>();

        var customers = await db.Customers.AsNoTracking()
            .Where(c => c.StoreId == storeId && !c.IsAnonymized && (c.Name.ToLower().Contains(lower) || (digits.Length >= 3 && c.Phone.Contains(digits))))
            .OrderByDescending(c => c.TotalAppointments).Take(8).ToListAsync(ct);
        hits.AddRange(customers.Select(c => new SearchHitDto("cliente", c.Id.ToString(), c.Name, $"{c.Phone} · {c.TotalAppointments} atendimento(s)", "crm")));

        var custIds = customers.Select(c => c.Id).ToList();
        if (custIds.Count > 0)
        {
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var appts = await db.Appointments.AsNoTracking().Include(a => a.Customer).Include(a => a.ServiceItem)
                .Where(a => a.StoreId == storeId && custIds.Contains(a.CustomerId) && string.Compare(a.AppointmentDate, today) >= 0 && a.Status != "Cancelled")
                .OrderBy(a => a.AppointmentDate).ThenBy(a => a.StartTime).Take(5).ToListAsync(ct);
            hits.AddRange(appts.Select(a => new SearchHitDto("agendamento", a.Id.ToString(),
                $"{a.Customer?.Name} — {a.ServiceItem?.Name}",
                $"{(DateOnly.TryParseExact(a.AppointmentDate, "yyyy-MM-dd", out var d) ? d.ToString("dd/MM") : a.AppointmentDate)} às {a.StartTime}", "agenda:" + a.AppointmentDate)));
        }

        var barbers = await db.Barbers.AsNoTracking().Where(b => b.StoreId == storeId && b.Name.ToLower().Contains(lower)).Take(5).ToListAsync(ct);
        hits.AddRange(barbers.Select(b => new SearchHitDto("profissional", b.Id.ToString(), b.Name, b.Specialty, "loja")));
        var services = await db.ServiceItems.AsNoTracking().Where(s => s.StoreId == storeId && s.Name.ToLower().Contains(lower)).Take(5).ToListAsync(ct);
        hits.AddRange(services.Select(s => new SearchHitDto("serviço", s.Id.ToString(), s.Name, $"R$ {s.Price:N2} · {s.DurationMinutes} min", "loja")));
        var stock = await db.StockItems.AsNoTracking().Where(s => s.StoreId == storeId && s.Name.ToLower().Contains(lower)).Take(5).ToListAsync(ct);
        hits.AddRange(stock.Select(s => new SearchHitDto("produto", s.Id.ToString(), s.Name, $"Estoque: {s.Quantity:0.##}", "loja")));
        return hits;
    }
}

// =====================================================================================================
// CONFIGURAÇÕES DOS RECURSOS NOVOS (PIX, fidelidade, página pública, idioma, white-label)
// =====================================================================================================
public interface IExtensionSettingsService
{
    Task<ExtensionSettingsDto> GetAsync(CancellationToken ct);
    Task<ExtensionSettingsDto> SaveAsync(ExtensionSettingsDto request, CancellationToken ct);
    Task<IReadOnlyList<BarberCalendarDto>> GetCalendarsAsync(CancellationToken ct);
    Task<BarberCalendarDto> SetCalendarAsync(int barberId, string? icsUrl, CancellationToken ct);
}

public sealed class ExtensionSettingsService(ClinicHairDbContext db, ICurrentStore current, IAuditLogger audit) : IExtensionSettingsService
{
    private static ExtensionSettingsDto ToDto(Store s)
    {
        var limits = SaasPlanLimits.For(s.SubscriptionPlan);
        return new ExtensionSettingsDto(s.PublicSlug, s.PublicBookingEnabled, s.BookingHorizonDays, s.PixKey, s.PixMerchantName, s.PixMerchantCity,
            s.OnlinePixEnabled, s.LoyaltyEnabled, s.LoyaltyPointsPerReal, s.LoyaltyRewardPoints, s.LoyaltyRewardDescription,
            s.Language, s.BrandColor, s.LogoUrl, s.BrandDisplayName, s.SubscriptionGraceDays, s.RenewalReminderDays, limits.Tier, limits.MaxBarbers);
    }

    public async Task<ExtensionSettingsDto> GetAsync(CancellationToken ct)
        => ToDto(await StoreQueries.LoadAsync(db, current.StoreId, false, ct));

    public async Task<ExtensionSettingsDto> SaveAsync(ExtensionSettingsDto r, CancellationToken ct)
    {
        var store = await StoreQueries.LoadAsync(db, current.StoreId, true, ct);
        string? slug = string.IsNullOrWhiteSpace(r.PublicSlug) ? null : Slugify(r.PublicSlug);
        if (slug is not null)
        {
            if (slug.Length < 3) throw new ArgumentException("O endereço da página pública precisa de pelo menos 3 letras/números.");
            bool taken = await db.Stores.AnyAsync(s => s.PublicSlug == slug && s.Id != store.Id, ct);
            if (taken) throw new InvalidOperationException("Este endereço de página pública já está em uso por outra loja.");
        }
        var limits = SaasPlanLimits.For(store.SubscriptionPlan);
        if (r.PublicBookingEnabled && !limits.PublicBooking) throw new InvalidOperationException($"O agendamento online não está incluso no plano {limits.Tier}.");
        if (!string.IsNullOrWhiteSpace(r.BrandColor) && !System.Text.RegularExpressions.Regex.IsMatch(r.BrandColor.Trim(), "^#[0-9a-fA-F]{6}$"))
            throw new ArgumentException("Cor da marca deve estar no formato #RRGGBB.");
        if (!string.IsNullOrWhiteSpace(r.LogoUrl) && !(r.LogoUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase) || r.LogoUrl.StartsWith("/")))
            throw new ArgumentException("O logo precisa ser um endereço https://.");
        if (r.OnlinePixEnabled && string.IsNullOrWhiteSpace(r.PixKey)) throw new ArgumentException("Informe a chave PIX para ativar o pagamento online.");

        store.PublicSlug = slug;
        store.PublicBookingEnabled = r.PublicBookingEnabled && slug is not null;
        store.BookingHorizonDays = Math.Clamp(r.BookingHorizonDays, 1, 90);
        store.PixKey = Trim(r.PixKey, 77);
        store.PixMerchantName = Trim(r.PixMerchantName, 25);
        store.PixMerchantCity = Trim(r.PixMerchantCity, 15);
        store.OnlinePixEnabled = r.OnlinePixEnabled;
        store.LoyaltyEnabled = r.LoyaltyEnabled;
        store.LoyaltyPointsPerReal = Math.Clamp(r.LoyaltyPointsPerReal, 0, 100);
        store.LoyaltyRewardPoints = Math.Clamp(r.LoyaltyRewardPoints, 1, 100000);
        store.LoyaltyRewardDescription = Trim(r.LoyaltyRewardDescription, 80) ?? "1 corte grátis";
        store.Language = r.Language == "en" ? "en" : "pt-BR";
        store.BrandColor = Trim(r.BrandColor, 7);
        store.LogoUrl = Trim(r.LogoUrl, 300);
        store.BrandDisplayName = Trim(r.BrandDisplayName, 60);
        store.SubscriptionGraceDays = Math.Clamp(r.SubscriptionGraceDays, 0, 30);
        store.RenewalReminderDays = Math.Clamp(r.RenewalReminderDays, 0, 15);
        store.UpdatedAt = DateTimeOffset.UtcNow;
        audit.Stage("Loja", "Atualizou PIX, fidelidade, página pública, idioma e identidade visual.");
        await db.SaveChangesAsync(ct);
        return ToDto(store);
    }

    public async Task<IReadOnlyList<BarberCalendarDto>> GetCalendarsAsync(CancellationToken ct)
    {
        var storeId = current.StoreId;
        var barbers = await db.Barbers.AsNoTracking().Where(b => b.StoreId == storeId && b.IsActive).OrderBy(b => b.Id).ToListAsync(ct);
        return barbers.Select(b => new BarberCalendarDto(b.Id, b.Name, !string.IsNullOrWhiteSpace(b.CalendarIcsUrl),
            ExternalCalendarCache.LastSyncOf(b.Id), ExternalCalendarCache.CountOf(b.Id))).ToList();
    }

    public async Task<BarberCalendarDto> SetCalendarAsync(int barberId, string? icsUrl, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var b = await db.Barbers.AsTracking().FirstOrDefaultAsync(x => x.Id == barberId && x.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Profissional não encontrado.");
        if (!string.IsNullOrWhiteSpace(icsUrl))
        {
            if (!Uri.TryCreate(icsUrl.Trim(), UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new ArgumentException("Use o endereço secreto https:// no formato iCal (Google Agenda › Configurações › Endereço secreto em formato iCal).");
            b.CalendarIcsUrl = uri.ToString();
        }
        else
        {
            b.CalendarIcsUrl = null;
            ExternalCalendarCache.Clear(b.Id);
        }
        audit.Stage("Agenda", $"{(b.CalendarIcsUrl is null ? "Desconectou" : "Conectou")} a agenda externa de {b.Name}.");
        await db.SaveChangesAsync(ct);
        return new BarberCalendarDto(b.Id, b.Name, b.CalendarIcsUrl is not null, ExternalCalendarCache.LastSyncOf(b.Id), ExternalCalendarCache.CountOf(b.Id));
    }

    private static string? Trim(string? v, int max)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        v = v.Trim();
        return v.Length > max ? v[..max] : v;
    }

    public static string Slugify(string text)
    {
        var normalized = text.Trim().ToLowerInvariant().Normalize(System.Text.NormalizationForm.FormD);
        var sb = new System.Text.StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is >= 'a' and <= 'z' or >= '0' and <= '9') sb.Append(c);
            else if (sb.Length > 0 && sb[^1] != '-') sb.Append('-');
        }
        var slug = sb.ToString().Trim('-');
        return slug.Length > 40 ? slug[..40].Trim('-') : slug;
    }
}

// =====================================================================================================
// PÁGINA PÚBLICA: planos, agendamento online e PIX (itens 66, 94, 96) — anônima, por slug
// =====================================================================================================
public interface IPublicBookingService
{
    Task<PublicStoreDto?> GetStoreAsync(string slug, CancellationToken ct);
    Task<IReadOnlyList<string>?> GetSlotsAsync(string slug, int barberId, string date, int serviceId, CancellationToken ct);
    Task<PublicBookingResultDto> BookAsync(string slug, PublicBookingRequest request, CancellationToken ct);
    Task SignupAsync(PublicSignupRequest request, CancellationToken ct);
}

public sealed class PublicBookingService(
    ClinicHairDbContext db,
    IAvailabilityService availability,
    ICustomerDirectory customers,
    ISubscriptionLifecycle subscriptions,
    IDashboardRealtimeNotifier realtime) : IPublicBookingService
{
    private async Task<Store?> FindAsync(string slug, CancellationToken ct)
    {
        var clean = ExtensionSettingsService.Slugify(slug ?? "");
        if (clean.Length < 3) return null;
        return await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.PublicSlug == clean && s.IsActive, ct);
    }

    public async Task<PublicStoreDto?> GetStoreAsync(string slug, CancellationToken ct)
    {
        var store = await FindAsync(slug, ct);
        if (store is null) return null;
        var services = await db.ServiceItems.AsNoTracking().Where(s => s.StoreId == store.Id && s.IsActive).OrderBy(s => s.DisplayOrder).ToListAsync(ct);
        var barbers = await db.Barbers.AsNoTracking().Where(b => b.StoreId == store.Id && b.IsActive).OrderBy(b => b.Id).ToListAsync(ct);
        var plans = await db.CustomerPlans.AsNoTracking().Where(p => p.StoreId == store.Id && p.IsActive && p.IsPublic).OrderBy(p => p.Price).ToListAsync(ct);
        var names = services.ToDictionary(s => s.Id, s => s.Name);
        return new PublicStoreDto(store.PublicSlug!, store.BrandDisplayName ?? store.Name, store.Address, store.MapLink, store.OpeningTime, store.ClosingTime,
            store.BrandColor, store.LogoUrl, store.Language, store.PublicBookingEnabled, Math.Clamp(store.BookingHorizonDays, 1, 90),
            store.OnlinePixEnabled && !string.IsNullOrWhiteSpace(store.PixKey),
            services.Select(s => new PublicServiceDto(s.Id, s.Name, s.Price, s.DurationMinutes)).ToList(),
            barbers.Select(b => new PublicBarberDto(b.Id, b.Name, b.Specialty)).ToList(),
            plans.Select(p => new PublicPlanDto(p.Id, p.Name, p.Description, p.Price, p.UsesPerMonth, p.ValidityDays, p.MaxMembers,
                p.IncludedServiceIdsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(x => int.TryParse(x, out var i) ? names.GetValueOrDefault(i) : null)
                    .Where(x => x is not null).Select(x => x!).ToList())).ToList());
    }

    public async Task<IReadOnlyList<string>?> GetSlotsAsync(string slug, int barberId, string date, int serviceId, CancellationToken ct)
    {
        var store = await FindAsync(slug, ct);
        if (store is null || !store.PublicBookingEnabled) return null;
        if (!DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d)) return [];
        var today = DateOnly.FromDateTime(DateTime.Now);
        // Janela padrão do público; assinantes ganham dias extras ao informar o telefone no agendamento
        int maxDays = Math.Clamp(store.BookingHorizonDays, 1, 90) + 60;
        if (d < today || d > today.AddDays(maxDays)) return [];
        return await availability.GetFreeSlotsAsync(store.Id, barberId, date, serviceId, ct);
    }

    public async Task<PublicBookingResultDto> BookAsync(string slug, PublicBookingRequest r, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(r.Website)) throw new ArgumentException("Pedido inválido."); // armadilha anti-robô
        var store = await FindAsync(slug, ct) ?? throw new KeyNotFoundException("Página não encontrada.");
        if (!store.PublicBookingEnabled) throw new InvalidOperationException("O agendamento online desta loja está desativado.");

        var name = (r.Name ?? "").Trim();
        if (name.Length is < 2 or > 80) throw new ArgumentException("Informe seu nome.");
        var phone = PhoneIdentity.Canonical(r.Phone);
        if (phone.Length is < 12 or > 13) throw new ArgumentException("Informe um WhatsApp válido com DDD.");
        if (!DateOnly.TryParseExact(r.Date, "yyyy-MM-dd", out var date)) throw new ArgumentException("Data inválida.");

        int horizon = await subscriptions.GetBookingHorizonDaysAsync(store, phone, ct);
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (date < today || date > today.AddDays(horizon))
            throw new InvalidOperationException($"Agendamentos online podem ser feitos para os próximos {horizon} dia(s).");

        var service = await db.ServiceItems.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.ServiceId && s.StoreId == store.Id && s.IsActive, ct)
            ?? throw new KeyNotFoundException("Serviço não encontrado.");

        var existing = await customers.FindAsync(store.Id, phone, tracking: false, ct);
        if (existing is not null)
        {
            var todayIso = today.ToString("yyyy-MM-dd");
            int upcoming = await db.Appointments.CountAsync(a => a.StoreId == store.Id && a.CustomerId == existing.Id
                && string.Compare(a.AppointmentDate, todayIso) >= 0 && a.Status == "Confirmed", ct);
            if (upcoming >= 3) throw new InvalidOperationException("Você já tem 3 agendamentos futuros. Fale com a barbearia para marcar mais.");
        }

        var barber = await availability.ResolveBarberAsync(store.Id, Math.Max(0, r.BarberId), r.Date, r.Time, service.Id, ct);
        if (barber is null)
        {
            var why = r.BarberId > 0 ? await availability.ExplainUnavailableAsync(store.Id, r.BarberId, r.Date, r.Time, service.Id, false, ct) : null;
            throw new InvalidOperationException(why ?? "Este horário acabou de ser ocupado. Escolha outro.");
        }
        if (date.ToDateTime(TimeOnly.MinValue).AddMinutes(AvailabilityService.ParseMinutes(r.Time, 0)) <= DateTime.Now)
            throw new InvalidOperationException("Este horário já passou.");

        var customer = existing is null
            ? await customers.SaveIdentityAsync(store.Id, phone, name, ct)
            : await db.Customers.AsTracking().FirstAsync(c => c.Id == existing.Id, ct);
        customer = await db.Customers.AsTracking().FirstAsync(c => c.Id == customer.Id, ct);
        customer.TotalAppointments += 1;
        customer.LastAppointmentAt = DateTimeOffset.UtcNow;

        var appt = new Appointment
        {
            StoreId = store.Id,
            CustomerId = customer.Id,
            BarberId = barber.Id,
            ServiceItemId = service.Id,
            AppointmentDate = r.Date,
            StartTime = r.Time,
            PriceCharged = service.Price,
            PaymentMethod = "PIX",
            Status = "Confirmed",
            Source = "Site",
            Notes = "Agendado pela página pública"
        };
        db.Appointments.Add(appt);
        db.SystemLogEntries.Add(new SystemLogEntry { Level = "INFO", Category = "Agendamento Online", StoreId = store.Id, StoreName = store.Name, Message = $"Novo agendamento pelo site: {name} em {r.Date} {r.Time} com {barber.Name}.", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);

        string? pix = null;
        if (store.OnlinePixEnabled && !string.IsNullOrWhiteSpace(store.PixKey))
        {
            pix = PixBrCode.Build(store.PixKey!, store.PixMerchantName ?? store.Name, store.PixMerchantCity ?? "BRASIL", service.Price, $"AG{appt.Id}");
        }
        var dateBr = date.ToString("dd/MM");
        realtime.Publish("agenda", $"{name} agendou {service.Name} pelo site para {dateBr} às {r.Time} com {barber.Name}.", "Novo agendamento online");
        return new PublicBookingResultDto(appt.Id, r.Date, r.Time, barber.Name, service.Name, service.Price, pix);
    }

    public async Task SignupAsync(PublicSignupRequest r, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(r.Website)) throw new ArgumentException("Pedido inválido.");
        var storeName = (r.StoreName ?? "").Trim();
        var owner = (r.OwnerName ?? "").Trim();
        var phone = PhoneIdentity.Canonical(r.OwnerPhone);
        if (storeName.Length is < 3 or > 120) throw new ArgumentException("Informe o nome da barbearia.");
        if (owner.Length is < 2 or > 80) throw new ArgumentException("Informe seu nome.");
        if (phone.Length is < 12 or > 13) throw new ArgumentException("Informe um WhatsApp válido com DDD.");
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        bool dup = await db.StoreSignupRequests.AnyAsync(s => s.OwnerPhone == phone && s.CreatedAt >= since, ct);
        if (dup) throw new InvalidOperationException("Já recebemos seu pedido. Entraremos em contato pelo WhatsApp.");
        int pending = await db.StoreSignupRequests.CountAsync(s => s.Status == "Pending", ct);
        if (pending >= 200) throw new InvalidOperationException("Muitos pedidos em análise no momento. Tente novamente mais tarde.");

        db.StoreSignupRequests.Add(new StoreSignupRequest
        {
            StoreName = storeName,
            OwnerName = owner,
            OwnerPhone = phone,
            City = string.IsNullOrWhiteSpace(r.City) ? null : r.City.Trim()[..Math.Min(60, r.City.Trim().Length)],
            DesiredPlan = string.IsNullOrWhiteSpace(r.DesiredPlan) ? "Plano Pro WhatsApp" : r.DesiredPlan.Trim()[..Math.Min(60, r.DesiredPlan.Trim().Length)]
        });
        db.SystemLogEntries.Add(new SystemLogEntry { Level = "SUPERADMIN", Category = "Onboarding", Message = $"Novo pedido de cadastro: '{storeName}' ({owner}).", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        realtime.Publish("superadmin", $"Novo pedido de cadastro: {storeName}", "Onboarding");
    }
}

// =====================================================================================================
// SAAS / SUPERADMIN: onboarding, cobrança automática, suspensão e limites (itens 71 a 74)
// =====================================================================================================
public interface ISaasService
{
    Task<IReadOnlyList<StoreSignupDto>> GetSignupsAsync(string? status, CancellationToken ct);
    Task<ApproveSignupResultDto> ApproveAsync(int id, ApproveSignupRequest request, CancellationToken ct);
    Task<StoreSignupDto?> RejectAsync(int id, string? notes, CancellationToken ct);
    Task<int> GenerateDueChargesAsync(CancellationToken ct);
    Task<int> ApplyOverduePolicyAsync(bool autoSuspend, int suspendAfterDays, CancellationToken ct);
}

public sealed class SaasService(ClinicHairDbContext db, ISuperAdminService superAdmin, ICurrentStore current) : ISaasService
{
    private static StoreSignupDto ToDto(StoreSignupRequest s) => new(s.Id, s.StoreName, s.OwnerName, s.OwnerPhone, s.City, s.DesiredPlan, s.Status, s.ReviewNotes, s.CreatedStoreId, s.CreatedAt);

    public async Task<IReadOnlyList<StoreSignupDto>> GetSignupsAsync(string? status, CancellationToken ct)
    {
        var q = db.StoreSignupRequests.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(status) && status != "all") q = q.Where(s => s.Status == status);
        return (await q.OrderByDescending(s => s.Id).Take(200).ToListAsync(ct)).Select(ToDto).ToList();
    }

    public async Task<ApproveSignupResultDto> ApproveAsync(int id, ApproveSignupRequest r, CancellationToken ct)
    {
        var s = await db.StoreSignupRequests.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct) ?? throw new KeyNotFoundException("Pedido não encontrado.");
        if (s.Status != "Pending") throw new InvalidOperationException("Este pedido já foi analisado.");
        var username = (r.AdminUsername ?? "").Trim();
        if (username.Length is < 3 or > 40 || !username.All(c => char.IsLetterOrDigit(c) || c is '.' or '_' or '-'))
            throw new ArgumentException("Usuário do administrador: 3 a 40 letras, números, ponto, _ ou -.");

        // Senha temporária forte: o dono troca no primeiro login (troca obrigatória)
        const string chars = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789";
        var temp = new string(Enumerable.Range(0, 10).Select(_ => chars[RandomNumberGenerator.GetInt32(chars.Length)]).ToArray()) + RandomNumberGenerator.GetInt32(10, 99);

        var created = await superAdmin.CreateStoreWithPreconfigAsync(new CreateStoreWithPreconfigRequest
        {
            StoreName = s.StoreName,
            AdminUsername = username,
            AdminPassword = temp,
            OwnerPhone = s.OwnerPhone,
            SubscriptionPlan = s.DesiredPlan,
            MonthlyFee = r.MonthlyFee ?? 149.90m,
            NextDueDate = DateTime.Now.AddDays(14).ToString("yyyy-MM-dd"), // 14 dias de teste
            PreconfigTemplate = "standard"
        }, ct);

        s.Status = "Approved";
        s.CreatedStoreId = created.Id;
        s.ReviewedAt = DateTimeOffset.UtcNow;
        s.ReviewNotes = $"Aprovado por {current.Username}";
        db.SystemLogEntries.Add(new SystemLogEntry { Level = "SUPERADMIN", Category = "Onboarding", StoreId = created.Id, StoreName = s.StoreName, Message = $"[{current.Username}] Aprovou o cadastro de '{s.StoreName}' (usuário {username}).", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        return new ApproveSignupResultDto(ToDto(s), created.Id, username, temp);
    }

    public async Task<StoreSignupDto?> RejectAsync(int id, string? notes, CancellationToken ct)
    {
        var s = await db.StoreSignupRequests.AsTracking().FirstOrDefaultAsync(x => x.Id == id, ct);
        if (s is null) return null;
        s.Status = "Rejected";
        s.ReviewedAt = DateTimeOffset.UtcNow;
        s.ReviewNotes = string.IsNullOrWhiteSpace(notes) ? $"Recusado por {current.Username}" : notes.Trim()[..Math.Min(300, notes.Trim().Length)];
        db.SystemLogEntries.Add(new SystemLogEntry { Level = "SUPERADMIN", Category = "Onboarding", Message = $"[{current.Username}] Recusou o cadastro de '{s.StoreName}'.", CreatedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(ct);
        return ToDto(s);
    }

    /// <summary>Gera a cobrança da mensalidade 5 dias antes do vencimento, uma única vez por vencimento (item 72).</summary>
    public async Task<int> GenerateDueChargesAsync(CancellationToken ct)
    {
        var limit = DateTime.Now.AddDays(5).ToString("yyyy-MM-dd");
        var stores = await db.Stores.AsNoTracking().Where(s => s.IsActive && s.MonthlyFee > 0 && string.Compare(s.NextDueDate, limit) <= 0).ToListAsync(ct);
        int created = 0;
        foreach (var st in stores)
        {
            bool exists = await db.StoreBillingRecords.AnyAsync(b => b.StoreId == st.Id && b.DueDate == st.NextDueDate, ct);
            if (exists) continue;
            db.StoreBillingRecords.Add(new StoreBillingRecord
            {
                StoreId = st.Id,
                StoreName = st.Name,
                Amount = st.MonthlyFee,
                DueDate = st.NextDueDate,
                Status = "Pending",
                Description = $"Mensalidade {st.SubscriptionPlan} — vencimento {st.NextDueDate} (gerada automaticamente)",
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.SystemLogEntries.Add(new SystemLogEntry { Level = "BILLING", Category = "SaaS", StoreId = st.Id, StoreName = st.Name, Message = $"Cobrança automática gerada: R$ {st.MonthlyFee:N2} para {st.NextDueDate}.", CreatedAt = DateTimeOffset.UtcNow });
            created++;
        }
        if (created > 0) await db.SaveChangesAsync(ct);
        return created;
    }

    /// <summary>
    /// Cobranças vencidas marcam a loja como "Overdue" (aviso). Com a suspensão automática ligada
    /// (SaaS:AutoSuspendEnabled = true), a loja é suspensa depois de N dias de atraso (item 73).
    /// </summary>
    public async Task<int> ApplyOverduePolicyAsync(bool autoSuspend, int suspendAfterDays, CancellationToken ct)
    {
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var suspendCut = DateTime.Now.AddDays(-Math.Max(1, suspendAfterDays)).ToString("yyyy-MM-dd");
        var overdue = await db.StoreBillingRecords.AsNoTracking()
            .Where(b => (b.Status == "Pending" || b.Status == "Overdue") && string.Compare(b.DueDate, today) < 0)
            .ToListAsync(ct);
        int changed = 0;
        foreach (var group in overdue.GroupBy(b => b.StoreId))
        {
            var store = await db.Stores.AsTracking().FirstOrDefaultAsync(s => s.Id == group.Key, ct);
            if (store is null || !store.IsActive) continue;
            bool isSuperAdminStore = await db.AdminUsers.AnyAsync(u => u.StoreId == store.Id && u.Role == "SuperAdmin", ct);
            if (isSuperAdminStore) continue; // nunca suspende a loja do próprio SuperAdmin

            string oldest = group.Min(b => b.DueDate) ?? today;
            if (autoSuspend && string.Compare(oldest, suspendCut) < 0)
            {
                store.IsActive = false;
                store.SubscriptionStatus = "Suspended";
                db.SystemLogEntries.Add(new SystemLogEntry { Level = "BILLING", Category = "SaaS", StoreId = store.Id, StoreName = store.Name, Message = $"Loja suspensa automaticamente: cobrança vencida desde {oldest}.", CreatedAt = DateTimeOffset.UtcNow });
                changed++;
            }
            else if (store.SubscriptionStatus != "Overdue")
            {
                store.SubscriptionStatus = "Overdue";
                db.SystemLogEntries.Add(new SystemLogEntry { Level = "BILLING", Category = "SaaS", StoreId = store.Id, StoreName = store.Name, Message = $"Aviso: mensalidade vencida desde {oldest}.", CreatedAt = DateTimeOffset.UtcNow });
                changed++;
            }
        }
        if (changed > 0) await db.SaveChangesAsync(ct);
        return changed;
    }
}

using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Services.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Services.Features;

/// <summary>CRM: ficha unificada, consentimento, notas, inativos, segmentos, campanhas, duplicados, tendência e anonimização.</summary>
public interface ICrmService
{
    Task<CustomerProfileDto> GetProfileAsync(int customerId, CancellationToken ct);
    Task<CustomerProfileDto> UpdateCustomerAsync(int customerId, UpdateCustomerRequest request, CancellationToken ct);
    Task<IReadOnlyList<InactiveCustomerDto>> GetInactiveAsync(int days, CancellationToken ct);
    Task<SegmentPreviewDto> PreviewSegmentAsync(SegmentFilter filter, CancellationToken ct);
    Task<IReadOnlyList<CampaignDto>> GetCampaignsAsync(CancellationToken ct);
    Task<CampaignDto> CreateCampaignAsync(CreateCampaignRequest request, CancellationToken ct);
    Task<CampaignDto?> CancelCampaignAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<DuplicateGroupDto>> GetDuplicatesAsync(CancellationToken ct);
    Task<CustomerProfileDto> MergeAsync(MergeCustomersRequest request, CancellationToken ct);
    Task<EvaluationTrendDto> GetEvaluationTrendAsync(CancellationToken ct);
    Task AnonymizeAsync(int customerId, CancellationToken ct);
}

public sealed class CrmService(
    ClinicHairDbContext db,
    ICurrentStore current,
    IAuditLogger audit) : ICrmService
{
    private static readonly string[] Inactive = ["Cancelled", "NoShow"];
    public const string OptOutFooter = "\n\n(Para não receber mais mensagens como esta, responda SAIR.)";

    public async Task<CustomerProfileDto> GetProfileAsync(int customerId, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var c = await db.Customers.AsNoTracking().FirstOrDefaultAsync(x => x.Id == customerId && x.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Cliente não encontrado.");

        var appts = await db.Appointments.AsNoTracking()
            .Include(a => a.Barber).Include(a => a.ServiceItem)
            .Where(a => a.CustomerId == c.Id && a.StoreId == storeId)
            .ToListAsync(ct);
        var ratings = await db.Evaluations.AsNoTracking()
            .Where(e => e.CustomerId == c.Id && e.StoreId == storeId)
            .Select(e => new { e.AppointmentId, e.Rating })
            .ToListAsync(ct);
        var ratingByAppt = ratings.Where(r => r.Rating.HasValue).GroupBy(r => r.AppointmentId).ToDictionary(g => g.Key, g => g.First().Rating);

        var valid = appts.Where(a => !Inactive.Contains(a.Status)).ToList();
        var done = valid.Where(a => string.CompareOrdinal(a.AppointmentDate, Today) <= 0).ToList();
        decimal spent = done.Sum(a => a.PriceCharged);

        var history = appts
            .OrderByDescending(a => a.AppointmentDate).ThenByDescending(a => a.StartTime)
            .Take(25)
            .Select(a => new CustomerHistoryItemDto(a.Id, a.AppointmentDate, a.StartTime, a.ServiceItem?.Name ?? "Serviço",
                a.Barber?.Name ?? "Profissional", a.PriceCharged, a.Status, ratingByAppt.GetValueOrDefault(a.Id)))
            .ToList();

        var rated = ratings.Where(r => r.Rating.HasValue).Select(r => r.Rating!.Value).ToList();
        var subscription = await LoadSubscriptionAsync(c, ct);

        return new CustomerProfileDto(
            c.Id, c.Name, c.Phone, c.BirthDate, c.MarketingConsent, c.InternalNotes, c.IsAnonymized,
            valid.Count, spent, done.Count > 0 ? Math.Round(spent / done.Count, 2) : 0,
            valid.MinBy(a => a.AppointmentDate)?.AppointmentDate,
            done.MaxBy(a => a.AppointmentDate)?.AppointmentDate,
            valid.GroupBy(a => a.ServiceItem?.Name).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key,
            valid.GroupBy(a => a.Barber?.Name).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key,
            appts.Count(a => a.Status == "Cancelled"),
            appts.Count(a => a.Status == "NoShow"),
            rated.Count > 0 ? Math.Round(rated.Average(), 1) : null,
            history,
            subscription);
    }

    public async Task<CustomerProfileDto> UpdateCustomerAsync(int customerId, UpdateCustomerRequest r, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var c = await db.Customers.AsTracking().FirstOrDefaultAsync(x => x.Id == customerId && x.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Cliente não encontrado.");
        if (c.IsAnonymized) throw new InvalidOperationException("Cliente anonimizado não pode ser editado.");

        if (r.Name is not null)
        {
            if (!PhoneIdentity.TryCleanName(r.Name, out var clean)) throw new ArgumentException("Nome inválido.");
            c.Name = clean;
        }
        if (r.BirthDate is not null)
        {
            if (r.BirthDate.Length == 0) c.BirthDate = null;
            else if (DateOnly.TryParseExact(r.BirthDate, "yyyy-MM-dd", out var bd) && bd < DateOnly.FromDateTime(DateTime.Now)) c.BirthDate = r.BirthDate;
            else throw new ArgumentException("Data de nascimento inválida.");
        }
        if (r.MarketingConsent is not null)
        {
            if (r.MarketingConsent is not ("OptIn" or "OptOut" or "Unknown")) throw new ArgumentException("Consentimento inválido.");
            if (c.MarketingConsent != r.MarketingConsent)
            {
                c.MarketingConsent = r.MarketingConsent;
                c.ConsentUpdatedAt = DateTimeOffset.UtcNow;
                audit.Stage("CRM", $"Alterou o consentimento de comunicação do cliente #{c.Id} para {r.MarketingConsent}.");
            }
        }
        if (r.InternalNotes is not null)
        {
            c.InternalNotes = r.InternalNotes.Trim().Length == 0 ? null : r.InternalNotes.Trim()[..Math.Min(2000, r.InternalNotes.Trim().Length)];
        }

        await db.SaveChangesAsync(ct);
        return await GetProfileAsync(customerId, ct);
    }

    public async Task<IReadOnlyList<InactiveCustomerDto>> GetInactiveAsync(int days, CancellationToken ct)
    {
        days = Math.Clamp(days, 7, 730);
        var stats = await LoadStatsAsync(ct);
        return stats.Where(s => s.LastVisit is not null && s.DaysSince >= days && !s.Customer.IsAnonymized)
            .OrderBy(s => s.DaysSince)
            .Select(ToInactive)
            .ToList();
    }

    public async Task<SegmentPreviewDto> PreviewSegmentAsync(SegmentFilter filter, CancellationToken ct)
    {
        var (eligible, excluded) = await ApplySegmentAsync(filter, ct);
        return new SegmentPreviewDto(eligible.Count, excluded, eligible.Take(15).Select(ToInactive).ToList());
    }

    public async Task<IReadOnlyList<CampaignDto>> GetCampaignsAsync(CancellationToken ct)
    {
        var storeId = current.StoreId;
        var list = await db.Campaigns.AsNoTracking().Where(c => c.StoreId == storeId).OrderByDescending(c => c.Id).Take(30).ToListAsync(ct);
        return list.Select(ToDto).ToList();
    }

    public async Task<CampaignDto> CreateCampaignAsync(CreateCampaignRequest r, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(r.Name)) throw new ArgumentException("Dê um nome para a campanha.");
        if (string.IsNullOrWhiteSpace(r.Message) || r.Message.Trim().Length < 10) throw new ArgumentException("Escreva a mensagem (mínimo 10 caracteres).");
        if (r.Message.Length > 1500) throw new ArgumentException("Mensagem muito longa (máx. 1500 caracteres).");

        var storeId = current.StoreId;
        bool running = await db.Campaigns.AnyAsync(c => c.StoreId == storeId && (c.Status == "Queued" || c.Status == "Sending"), ct);
        if (running) throw new InvalidOperationException("Já existe uma campanha em envio. Aguarde terminar ou cancele-a.");

        var (eligible, _) = await ApplySegmentAsync(r.Segment, ct);
        if (eligible.Count == 0) throw new InvalidOperationException("Nenhum cliente elegível neste segmento.");
        if (eligible.Count > 500) throw new InvalidOperationException("Segmento muito grande (máx. 500 clientes por campanha). Refine os filtros.");

        var campaign = new Campaign
        {
            StoreId = storeId,
            Name = r.Name.Trim()[..Math.Min(80, r.Name.Trim().Length)],
            Message = r.Message.Trim(),
            SegmentJson = System.Text.Json.JsonSerializer.Serialize(r.Segment),
            Status = "Queued",
            TotalRecipients = eligible.Count,
            CreatedBy = current.Username
        };
        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct);

        foreach (var s in eligible)
        {
            db.CampaignRecipients.Add(new CampaignRecipient
            {
                CampaignId = campaign.Id,
                CustomerId = s.Customer.Id,
                Phone = s.Customer.Phone,
                CustomerName = s.Customer.Name
            });
        }
        audit.Stage("Campanhas", $"Criou a campanha '{campaign.Name}' para {eligible.Count} cliente(s).");
        await db.SaveChangesAsync(ct);
        return ToDto(campaign);
    }

    public async Task<CampaignDto?> CancelCampaignAsync(int id, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var c = await db.Campaigns.AsTracking().FirstOrDefaultAsync(x => x.Id == id && x.StoreId == storeId, ct);
        if (c is null) return null;
        if (c.Status is "Queued" or "Sending")
        {
            c.Status = "Cancelled";
            c.FinishedAt = DateTimeOffset.UtcNow;
            audit.Stage("Campanhas", $"Cancelou a campanha '{c.Name}'.");
            await db.SaveChangesAsync(ct);
        }
        return ToDto(c);
    }

    public async Task<IReadOnlyList<DuplicateGroupDto>> GetDuplicatesAsync(CancellationToken ct)
    {
        var stats = await LoadStatsAsync(ct);
        var groups = new List<DuplicateGroupDto>();

        // 1) Mesmo telefone (últimos 8 dígitos) — grafias diferentes do mesmo número
        foreach (var g in stats.Where(s => !s.Customer.IsAnonymized)
                     .GroupBy(s => Last8(s.Customer.Phone))
                     .Where(g => g.Key.Length == 8 && g.Count() > 1))
        {
            groups.Add(new DuplicateGroupDto($"Telefone final {g.Key}", g.Select(ToDup).ToList()));
        }

        // 2) Mesmo nome completo com telefones diferentes (sugestão para conferência)
        var already = groups.SelectMany(g => g.Customers.Select(c => c.Id)).ToHashSet();
        foreach (var g in stats.Where(s => !s.Customer.IsAnonymized && !already.Contains(s.Customer.Id) && !PhoneIdentity.IsPlaceholderName(s.Customer.Name))
                     .GroupBy(s => WhatsAppOrchestratorService.Normalize(s.Customer.Name))
                     .Where(g => g.Key.Contains(' ') && g.Count() > 1))
        {
            groups.Add(new DuplicateGroupDto($"Mesmo nome: {g.First().Customer.Name}", g.Select(ToDup).ToList()));
        }

        return groups;
    }

    public async Task<CustomerProfileDto> MergeAsync(MergeCustomersRequest r, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var ids = r.MergeIds.Where(i => i != r.KeepId).Distinct().ToList();
        if (ids.Count == 0) throw new ArgumentException("Selecione os cadastros a mesclar.");

        var keep = await db.Customers.AsTracking().FirstOrDefaultAsync(c => c.Id == r.KeepId && c.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Cadastro principal não encontrado.");
        var others = await db.Customers.AsTracking().Where(c => ids.Contains(c.Id) && c.StoreId == storeId).ToListAsync(ct);
        if (others.Count != ids.Count) throw new KeyNotFoundException("Algum cadastro selecionado não foi encontrado.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);

        foreach (var a in await db.Appointments.AsTracking().Where(a => ids.Contains(a.CustomerId) && a.StoreId == storeId).ToListAsync(ct)) a.CustomerId = keep.Id;
        foreach (var e in await db.Evaluations.AsTracking().Where(e => ids.Contains(e.CustomerId) && e.StoreId == storeId).ToListAsync(ct)) e.CustomerId = keep.Id;
        foreach (var s in await db.CustomerSubscriptions.AsTracking().Where(s => ids.Contains(s.CustomerId) && s.StoreId == storeId).ToListAsync(ct)) s.CustomerId = keep.Id;
        foreach (var cr in await db.CampaignRecipients.AsTracking().Where(x => ids.Contains(x.CustomerId)).ToListAsync(ct)) cr.CustomerId = keep.Id;

        foreach (var o in others)
        {
            keep.TotalAppointments += o.TotalAppointments;
            if (o.LastAppointmentAt > keep.LastAppointmentAt) keep.LastAppointmentAt = o.LastAppointmentAt;
            keep.BirthDate ??= o.BirthDate;
            if (!string.IsNullOrWhiteSpace(o.InternalNotes))
                keep.InternalNotes = string.IsNullOrWhiteSpace(keep.InternalNotes) ? o.InternalNotes : $"{keep.InternalNotes}\n{o.InternalNotes}";
            if (o.MarketingConsent == "OptOut") keep.MarketingConsent = "OptOut"; // respeita sempre o pedido mais restritivo
            if (PhoneIdentity.IsPlaceholderName(keep.Name) && !PhoneIdentity.IsPlaceholderName(o.Name)) keep.Name = o.Name;
            db.Customers.Remove(o);
        }

        audit.Stage("CRM", $"Mesclou {others.Count} cadastro(s) no cliente #{keep.Id} ({keep.Name}).");
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await GetProfileAsync(keep.Id, ct);
    }

    public async Task<EvaluationTrendDto> GetEvaluationTrendAsync(CancellationToken ct)
    {
        var storeId = current.StoreId;
        var evals = (await db.Evaluations.AsNoTracking()
            .Where(e => e.StoreId == storeId)
            .Select(e => new { e.BarberId, e.Rating, e.Sentiment, e.CreatedAt })
            .ToListAsync(ct))
            .Select(e => new RatingPoint(
                e.BarberId,
                // avaliações antigas (texto livre) entram na média pelo sentimento classificado
                e.Rating ?? (e.Sentiment == "Positive" ? 5 : e.Sentiment == "Negative" ? 2 : 0),
                e.CreatedAt.ToLocalTime().ToString("yyyy-MM")))
            .Where(e => e.Score > 0)
            .ToList();

        var months = Enumerable.Range(0, 6).Select(i => DateTime.Now.AddMonths(-5 + i).ToString("yyyy-MM")).ToList();
        List<TrendPointDto> Series(List<RatingPoint> list) => months.Select(m =>
        {
            var inMonth = list.Where(x => x.Month == m).Select(x => x.Score).ToList();
            return new TrendPointDto(m, inMonth.Count > 0 ? Math.Round(inMonth.Average(), 1) : null, inMonth.Count);
        }).ToList();

        var barbers = await db.Barbers.AsNoTracking().Where(b => b.StoreId == storeId).ToListAsync(ct);
        var byBarber = barbers.Select(b =>
        {
            var mine = evals.Where(e => e.BarberId == b.Id).ToList();
            return new BarberTrendDto(b.Id, b.Name, mine.Count > 0 ? Math.Round(mine.Average(e => e.Score), 1) : null, mine.Count, Series(mine));
        }).Where(b => b.Count > 0 || barbers.Count <= 6).ToList();

        return new EvaluationTrendDto(
            Series(evals),
            byBarber,
            evals.Count > 0 ? Math.Round(evals.Average(e => e.Score), 1) : null,
            evals.Count);
    }

    public async Task AnonymizeAsync(int customerId, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var c = await db.Customers.AsTracking().FirstOrDefaultAsync(x => x.Id == customerId && x.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Cliente não encontrado.");
        if (c.IsAnonymized) return;

        var variants = PhoneIdentity.Variants(c.Phone);
        var canonical = PhoneIdentity.Canonical(c.Phone);
        var sessions = await db.ConversationSessions.AsTracking()
            .Where(s => s.StoreId == storeId && (s.CustomerPhoneJid == canonical || variants.Contains(s.CustomerPhoneJid)))
            .ToListAsync(ct);
        db.ConversationSessions.RemoveRange(sessions);

        var waits = await db.WaitlistEntries.AsTracking().Where(w => w.StoreId == storeId && (w.Phone == canonical || variants.Contains(w.Phone))).ToListAsync(ct);
        db.WaitlistEntries.RemoveRange(waits);

        foreach (var cr in await db.CampaignRecipients.AsTracking().Where(x => x.CustomerId == c.Id).ToListAsync(ct))
        {
            cr.Phone = "";
            cr.CustomerName = "Anonimizado";
        }

        c.Name = "Cliente anonimizado";
        c.Phone = $"anon-{c.Id}";
        c.BirthDate = null;
        c.InternalNotes = null;
        c.MarketingConsent = "OptOut";
        c.ConsentUpdatedAt = DateTimeOffset.UtcNow;
        c.IsAnonymized = true;

        audit.Stage("LGPD", $"Anonimizou os dados pessoais do cliente #{c.Id} (histórico financeiro mantido sem identificação).");
        await db.SaveChangesAsync(ct);
    }

    // ------------------------------------------------------------------ segmentação

    internal sealed record CustomerStats(Customer Customer, int Visits, decimal Spent, string? LastVisit, int DaysSince, HashSet<int> ServiceIds)
    {
        public decimal AverageTicket => Visits > 0 ? Spent / Visits : 0;
    }

    private static string Today => DateTime.Now.ToString("yyyy-MM-dd");

    private async Task<List<CustomerStats>> LoadStatsAsync(CancellationToken ct)
    {
        var storeId = current.StoreId;
        var customers = await db.Customers.AsNoTracking().Where(c => c.StoreId == storeId).ToListAsync(ct);
        var today = Today;
        var appts = await db.Appointments.AsNoTracking()
            .Where(a => a.StoreId == storeId && a.Status != "Cancelled" && a.Status != "NoShow" && string.Compare(a.AppointmentDate, today) <= 0)
            .Select(a => new { a.CustomerId, a.AppointmentDate, a.PriceCharged, a.ServiceItemId })
            .ToListAsync(ct);
        var byCustomer = appts.GroupBy(a => a.CustomerId).ToDictionary(g => g.Key, g => g.ToList());
        var todayDate = DateOnly.FromDateTime(DateTime.Now);

        return customers.Select(c =>
        {
            var mine = byCustomer.GetValueOrDefault(c.Id) ?? [];
            var last = mine.MaxBy(a => a.AppointmentDate)?.AppointmentDate;
            int days = last is not null && DateOnly.TryParseExact(last, "yyyy-MM-dd", out var ld) ? todayDate.DayNumber - ld.DayNumber : 9999;
            return new CustomerStats(c, mine.Count, mine.Sum(a => a.PriceCharged), last, days, mine.Select(a => a.ServiceItemId).ToHashSet());
        }).ToList();
    }

    private async Task<(List<CustomerStats> Eligible, int ExcludedOptOut)> ApplySegmentAsync(SegmentFilter f, CancellationToken ct)
    {
        var stats = await LoadStatsAsync(ct);
        var q = stats.Where(s => !s.Customer.IsAnonymized && PhoneIdentity.Canonical(s.Customer.Phone).Length >= 10);
        if (f.MinVisits is int min) q = q.Where(s => s.Visits >= min);
        if (f.MaxVisits is int max) q = q.Where(s => s.Visits <= max);
        if (f.InactiveDays is int inact) q = q.Where(s => s.LastVisit is not null && s.DaysSince >= inact);
        if (f.ActiveWithinDays is int act) q = q.Where(s => s.LastVisit is not null && s.DaysSince <= act);
        if (f.ServiceId is int sid && sid > 0) q = q.Where(s => s.ServiceIds.Contains(sid));
        if (f.MinAverageTicket is decimal t && t > 0) q = q.Where(s => s.AverageTicket >= t);

        var matched = q.ToList();
        int optOut = matched.Count(s => s.Customer.MarketingConsent == "OptOut" || (f.OnlyOptIn && s.Customer.MarketingConsent != "OptIn"));
        var eligible = matched.Where(s => s.Customer.MarketingConsent != "OptOut" && (!f.OnlyOptIn || s.Customer.MarketingConsent == "OptIn")).ToList();
        return (eligible, optOut);
    }

    private async Task<CustomerSubscriptionDto?> LoadSubscriptionAsync(Customer c, CancellationToken ct)
    {
        var sub = await db.CustomerSubscriptions.AsNoTracking()
            .Where(s => s.CustomerId == c.Id && s.StoreId == c.StoreId && s.Status != "Cancelled")
            .OrderByDescending(s => s.Id)
            .FirstOrDefaultAsync(ct);
        if (sub is null) return null;
        var plan = await db.CustomerPlans.AsNoTracking().FirstOrDefaultAsync(p => p.Id == sub.PlanId, ct);
        return new CustomerSubscriptionDto(sub.Id, c.Id, c.Name, c.Phone, sub.PlanId, plan?.Name ?? "Plano", plan?.Price ?? 0,
            sub.Status, sub.StartDate, sub.CurrentPeriodEnd, sub.UsesThisPeriod, plan?.UsesPerMonth ?? 0, sub.Notes);
    }

    private sealed record RatingPoint(int BarberId, int Score, string Month);

    private static string Last8(string phone)
    {
        var digits = new string(phone.Where(char.IsDigit).ToArray());
        return digits.Length >= 8 ? digits[^8..] : "";
    }

    private static InactiveCustomerDto ToInactive(CustomerStats s)
        => new(s.Customer.Id, s.Customer.Name, s.Customer.Phone, s.LastVisit, s.LastVisit is null ? 0 : s.DaysSince, s.Visits, s.Customer.MarketingConsent);

    private static DuplicateCustomerDto ToDup(CustomerStats s)
        => new(s.Customer.Id, s.Customer.Name, s.Customer.Phone, s.Visits, s.LastVisit);

    private static CampaignDto ToDto(Campaign c)
        => new(c.Id, c.Name, c.Message, c.Status, c.TotalRecipients, c.SentCount, c.FailedCount, c.CreatedAt, c.FinishedAt);
}

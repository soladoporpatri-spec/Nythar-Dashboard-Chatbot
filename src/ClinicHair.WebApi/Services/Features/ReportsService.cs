using System.Globalization;
using System.Text;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Services.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Services.Features;

/// <summary>Relatórios com comparativo, cancelamentos/faltas, retorno, ticket, ocupação, metas, visão do dono e caixa.</summary>
public interface IReportsService
{
    Task<ReportsOverviewDto> GetOverviewAsync(string? from, string? to, CancellationToken ct);
    Task<CashClosingDto> GetCashAsync(string? date, CancellationToken ct);
    Task<CashClosingDto> SaveCashAsync(SaveCashClosingRequest request, CancellationToken ct);
    Task<(byte[] Bytes, string FileName)> ExportPeriodCsvAsync(string? from, string? to, CancellationToken ct);
    Task<string> BuildMonthlySummaryAsync(int storeId, DateOnly anyDayOfMonth, CancellationToken ct);
}

public sealed class ReportsService(ClinicHairDbContext db, ICurrentStore current, IAuditLogger audit) : IReportsService
{
    private static readonly CultureInfo Br = new("pt-BR");
    private static readonly string[] Inactive = ["Cancelled", "NoShow"];

    private sealed record Row(long Id, int CustomerId, int BarberId, int ServiceItemId, string Date, string Time, decimal Price, string Status, string Payment);

    public async Task<ReportsOverviewDto> GetOverviewAsync(string? fromIso, string? toIso, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var today = DateOnly.FromDateTime(DateTime.Now);
        var (from, to) = ParseRange(fromIso, toIso, today);
        int days = to.DayNumber - from.DayNumber + 1;
        var prevTo = from.AddDays(-1);
        var prevFrom = prevTo.AddDays(-(days - 1));

        var store = await StoreQueries.LoadAsync(db, storeId, false, ct);
        var barbers = await db.Barbers.AsNoTracking().Where(b => b.StoreId == storeId).ToListAsync(ct);
        var services = await db.ServiceItems.AsNoTracking().Where(s => s.StoreId == storeId).ToListAsync(ct);
        var rows = await LoadRowsAsync(storeId, ct);
        var evals = await db.Evaluations.AsNoTracking().Where(e => e.StoreId == storeId)
            .Select(e => new { e.Rating, e.Sentiment, e.CreatedAt }).ToListAsync(ct);
        var nonWorking = (await db.StoreNonWorkingDays.AsNoTracking().Where(n => n.StoreId == storeId).Select(n => n.BlockedDate).ToListAsync(ct)).ToHashSet();

        KpiSet Compute(DateOnly f, DateOnly t)
        {
            var ef = Min(t, today);
            var inRange = rows.Where(r => In(r.Date, f, t)).ToList();
            var realized = inRange.Where(r => !Inactive.Contains(r.Status) && In(r.Date, f, ef)).ToList();
            decimal revenue = realized.Sum(r => r.Price);

            // Taxa de retorno: clientes atendidos no período que voltaram em até 30 dias
            var valid = rows.Where(r => !Inactive.Contains(r.Status) && string.CompareOrdinal(r.Date, Iso(today)) <= 0).ToList();
            int eligible = 0, returned = 0;
            foreach (var g in realized.GroupBy(r => r.CustomerId))
            {
                var first = DateOnly.ParseExact(g.Min(r => r.Date)!, "yyyy-MM-dd");
                bool came = valid.Any(r => r.CustomerId == g.Key && DateOnly.ParseExact(r.Date, "yyyy-MM-dd") is var d && d > first && d <= first.AddDays(30));
                if (came || first <= today.AddDays(-30)) { eligible++; if (came) returned++; }
            }

            var firstVisit = valid.GroupBy(r => r.CustomerId).ToDictionary(g => g.Key, g => g.Min(r => r.Date)!);
            int newCustomers = firstVisit.Values.Count(d => In(d, f, ef));

            var periodEvals = evals.Select(e => new { Score = e.Rating ?? (e.Sentiment == "Positive" ? 5 : e.Sentiment == "Negative" ? 2 : 0), Day = DateOnly.FromDateTime(e.CreatedAt.ToLocalTime().DateTime) })
                .Where(e => e.Score > 0 && e.Day >= f && e.Day <= t).ToList();

            return new KpiSet(
                revenue,
                realized.Count,
                realized.Count > 0 ? Math.Round(revenue / realized.Count, 2) : 0,
                inRange.Count(r => r.Status == "Cancelled"),
                inRange.Count(r => r.Status == "NoShow"),
                eligible > 0 ? Math.Round(returned * 100m / eligible, 1) : 0,
                newCustomers,
                periodEvals.Count > 0 ? (decimal)Math.Round(periodEvals.Average(e => e.Score), 1) : 0);
        }

        var cur = Compute(from, to);
        var prev = Compute(prevFrom, prevTo);
        var kpis = new List<KpiDto>
        {
            Kpi("revenue", "Faturamento", cur.Revenue, prev.Revenue, "currency"),
            Kpi("appointments", "Atendimentos", cur.Count, prev.Count, "number"),
            Kpi("ticket", "Ticket médio", cur.Ticket, prev.Ticket, "currency"),
            Kpi("newCustomers", "Clientes novos", cur.NewCustomers, prev.NewCustomers, "number"),
            Kpi("returnRate", "Taxa de retorno (30d)", cur.ReturnRate, prev.ReturnRate, "percent"),
            Kpi("rating", "Nota média", cur.Rating, prev.Rating, "rating"),
            Kpi("cancellations", "Cancelamentos", cur.Cancellations, prev.Cancellations, "number", higherIsBetter: false),
            Kpi("noShows", "Faltas", cur.NoShows, prev.NoShows, "number", higherIsBetter: false)
        };

        var effectiveTo = Min(to, today);
        var realizedNow = rows.Where(r => !Inactive.Contains(r.Status) && In(r.Date, from, effectiveTo)).ToList();

        var byBarber = realizedNow.GroupBy(r => r.BarberId)
            .Select(g => Ticket(barbers.FirstOrDefault(b => b.Id == g.Key)?.Name ?? "Profissional", g))
            .OrderByDescending(x => x.Revenue).ToList();
        var byService = realizedNow.GroupBy(r => r.ServiceItemId)
            .Select(g => Ticket(services.FirstOrDefault(s => s.Id == g.Key)?.Name ?? "Serviço", g))
            .OrderByDescending(x => x.Revenue).ToList();

        // Ocupação: minutos reservados x minutos disponíveis (considera capacidade por horário)
        var occupancy = new List<OccupancyDto>();
        foreach (var b in barbers.Where(b => b.IsActive))
        {
            int available = 0;
            for (var d = from; d <= effectiveTo; d = d.AddDays(1))
            {
                if (nonWorking.Contains(Iso(d))) continue;
                if (!AvailabilityService.ParseDays(store.WorkingDaysCsv).Contains((int)d.DayOfWeek)) continue;
                if (!AvailabilityService.ParseDays(b.WorkingDaysCsv).Contains((int)d.DayOfWeek)) continue;
                int ws = AvailabilityService.ParseMinutes(b.WorkStartTime, 480), we = AvailabilityService.ParseMinutes(b.WorkEndTime, 1140);
                int ls = AvailabilityService.ParseMinutes(b.LunchStartTime, -1), le = AvailabilityService.ParseMinutes(b.LunchEndTime, -1);
                int mins = Math.Max(0, we - ws) - (ls >= 0 && le > ls ? Math.Max(0, Math.Min(le, we) - Math.Max(ls, ws)) : 0);
                available += mins * Math.Max(1, b.MaxConcurrentPerSlot);
            }
            int booked = realizedNow.Where(r => r.BarberId == b.Id)
                .Sum(r => RoundSlot(services.FirstOrDefault(s => s.Id == r.ServiceItemId)?.DurationMinutes ?? 30));
            occupancy.Add(new OccupancyDto(b.Name, available > 0 ? (int)Math.Round(Math.Min(100.0, booked * 100.0 / available)) : 0, booked, available));
        }

        // Receita por semana (12 semanas, segunda a domingo)
        var monday = today.AddDays(-(((int)today.DayOfWeek + 6) % 7));
        var weekly = Enumerable.Range(0, 12).Select(i =>
        {
            var ws = monday.AddDays(-7 * (11 - i));
            var we = Min(ws.AddDays(6), today);
            var wk = rows.Where(r => !Inactive.Contains(r.Status) && In(r.Date, ws, we)).ToList();
            return new WeeklyRevenueDto(Iso(ws), wk.Sum(r => r.Price), wk.Count);
        }).ToList();

        var monthStart = new DateOnly(today.Year, today.Month, 1);
        var monthRows = rows.Where(r => !Inactive.Contains(r.Status) && In(r.Date, monthStart, today)).ToList();
        var weekRows = rows.Where(r => !Inactive.Contains(r.Status) && In(r.Date, monday, today)).ToList();
        var todayRows = rows.Where(r => !Inactive.Contains(r.Status) && r.Date == Iso(today)).ToList();

        var goals = new GoalsDto(today.ToString("yyyy-MM"), store.MonthlyRevenueGoal, monthRows.Sum(r => r.Price), store.MonthlyAppointmentsGoal, monthRows.Count);
        var owner = new OwnerSnapshotDto(todayRows.Sum(r => r.Price), todayRows.Count, weekRows.Sum(r => r.Price), weekRows.Count, monthRows.Sum(r => r.Price), monthRows.Count);

        var payments = realizedNow.GroupBy(r => PaymentBucket(r.Payment))
            .Select(g => new PaymentSplitDto(g.Key, g.Sum(r => r.Price), g.Count()))
            .OrderByDescending(p => p.Amount).ToList();

        var serviceTimes = await AgendaService.ComputeServiceTimesAsync(db, storeId, ct);

        return new ReportsOverviewDto(Iso(from), Iso(to), Iso(prevFrom), Iso(prevTo), kpis, byBarber, byService, occupancy, weekly, goals, owner, serviceTimes, payments);
    }

    public async Task<CashClosingDto> GetCashAsync(string? date, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var day = DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d) ? Iso(d) : Iso(DateOnly.FromDateTime(DateTime.Now));
        var completed = (await LoadRowsAsync(storeId, ct)).Where(r => r.Date == day && r.Status == "Completed").ToList();
        var closing = await db.CashClosings.AsNoTracking().FirstOrDefaultAsync(c => c.StoreId == storeId && c.Date == day, ct);

        return new CashClosingDto(
            day,
            completed.Where(r => PaymentBucket(r.Payment) == "PIX").Sum(r => r.Price),
            completed.Where(r => PaymentBucket(r.Payment) == "Cartão").Sum(r => r.Price),
            completed.Where(r => PaymentBucket(r.Payment) == "Dinheiro").Sum(r => r.Price),
            closing?.CountedPix, closing?.CountedCard, closing?.CountedCash, closing?.Notes, closing?.ClosedBy, closing?.ClosedAt,
            completed.Count);
    }

    public async Task<CashClosingDto> SaveCashAsync(SaveCashClosingRequest r, CancellationToken ct)
    {
        var storeId = current.StoreId;
        if (!DateOnly.TryParseExact(r.Date, "yyyy-MM-dd", out var d) || d > DateOnly.FromDateTime(DateTime.Now))
        {
            throw new ArgumentException("Data inválida para fechamento.");
        }
        if (r.CountedPix < 0 || r.CountedCard < 0 || r.CountedCash < 0) throw new ArgumentException("Valores não podem ser negativos.");

        var expected = await GetCashAsync(r.Date, ct);
        var closing = await db.CashClosings.AsTracking().FirstOrDefaultAsync(c => c.StoreId == storeId && c.Date == r.Date, ct);
        if (closing is null)
        {
            closing = new CashClosing { StoreId = storeId, Date = r.Date };
            db.CashClosings.Add(closing);
        }

        closing.ExpectedPix = expected.ExpectedPix;
        closing.ExpectedCard = expected.ExpectedCard;
        closing.ExpectedCash = expected.ExpectedCash;
        closing.CountedPix = r.CountedPix;
        closing.CountedCard = r.CountedCard;
        closing.CountedCash = r.CountedCash;
        closing.Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim()[..Math.Min(500, r.Notes.Trim().Length)];
        closing.ClosedBy = current.Username;
        closing.ClosedAt = DateTimeOffset.UtcNow;

        decimal diff = (r.CountedPix + r.CountedCard + r.CountedCash) - (expected.ExpectedPix + expected.ExpectedCard + expected.ExpectedCash);
        audit.Stage("Caixa", $"Fechou o caixa de {r.Date} (diferença {diff.ToString("C", Br)}).");
        await db.SaveChangesAsync(ct);
        return await GetCashAsync(r.Date, ct);
    }

    public async Task<(byte[] Bytes, string FileName)> ExportPeriodCsvAsync(string? fromIso, string? toIso, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var (from, to) = ParseRange(fromIso, toIso, DateOnly.FromDateTime(DateTime.Now));
        var appts = await db.Appointments.AsNoTracking()
            .Include(a => a.Customer).Include(a => a.Barber).Include(a => a.ServiceItem)
            .Where(a => a.StoreId == storeId && string.Compare(a.AppointmentDate, Iso(from)) >= 0 && string.Compare(a.AppointmentDate, Iso(to)) <= 0)
            .OrderBy(a => a.AppointmentDate).ThenBy(a => a.StartTime)
            .ToListAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("Data;Hora;Cliente;Telefone;Servico;Profissional;Valor;Pagamento;Status");
        foreach (var a in appts)
        {
            sb.Append(DateOnly.ParseExact(a.AppointmentDate, "yyyy-MM-dd").ToString("dd/MM/yyyy")).Append(';')
              .Append(a.StartTime).Append(';')
              .Append(Csv(a.Customer?.Name)).Append(';')
              .Append(Csv(a.Customer?.Phone)).Append(';')
              .Append(Csv(a.ServiceItem?.Name)).Append(';')
              .Append(Csv(a.Barber?.Name)).Append(';')
              .Append(a.PriceCharged.ToString("F2", Br)).Append(';')
              .Append(Csv(a.PaymentMethod)).Append(';')
              .Append(StatusPt(a.Status)).AppendLine();
        }

        audit.Stage("Exportação", $"Exportou atendimentos de {Iso(from)} a {Iso(to)} ({appts.Count} linhas).");
        await db.SaveChangesAsync(ct);
        var bytes = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes(sb.ToString())).ToArray();
        return (bytes, $"ClinicHair_Atendimentos_{from:yyyy-MM-dd}_a_{to:yyyy-MM-dd}.csv");
    }

    public async Task<string> BuildMonthlySummaryAsync(int storeId, DateOnly anyDayOfMonth, CancellationToken ct)
    {
        var from = new DateOnly(anyDayOfMonth.Year, anyDayOfMonth.Month, 1);
        var to = from.AddMonths(1).AddDays(-1);
        var prevFrom = from.AddMonths(-1);
        var prevTo = from.AddDays(-1);

        var store = await StoreQueries.LoadAsync(db, storeId, false, ct);
        var rows = await LoadRowsAsync(storeId, ct);
        var barbers = await db.Barbers.AsNoTracking().Where(b => b.StoreId == storeId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);

        var cur = rows.Where(r => !Inactive.Contains(r.Status) && In(r.Date, from, to)).ToList();
        var prev = rows.Where(r => !Inactive.Contains(r.Status) && In(r.Date, prevFrom, prevTo)).ToList();
        decimal rev = cur.Sum(r => r.Price), prevRev = prev.Sum(r => r.Price);
        int cancels = rows.Count(r => r.Status == "Cancelled" && In(r.Date, from, to));
        int noShows = rows.Count(r => r.Status == "NoShow" && In(r.Date, from, to));
        var top = cur.GroupBy(r => r.BarberId).OrderByDescending(g => g.Sum(r => r.Price)).FirstOrDefault();

        string Delta(decimal now, decimal before) => before == 0 ? "" : $" ({(now >= before ? "▲" : "▼")} {Math.Abs((now - before) * 100 / before):F0}%)";
        var monthName = Br.TextInfo.ToTitleCase(from.ToString("MMMM 'de' yyyy", Br));

        var sb = new StringBuilder();
        sb.AppendLine($"📊 *Resumo de {monthName} — {store.Name}*");
        sb.AppendLine();
        sb.AppendLine($"💰 Faturamento: {rev.ToString("C", Br)}{Delta(rev, prevRev)}");
        sb.AppendLine($"✂️ Atendimentos: {cur.Count}{Delta(cur.Count, prev.Count)}");
        sb.AppendLine($"🎟️ Ticket médio: {(cur.Count > 0 ? rev / cur.Count : 0).ToString("C", Br)}");
        sb.AppendLine($"❌ Cancelamentos: {cancels} • Faltas: {noShows}");
        if (top is not null) sb.AppendLine($"🏆 Destaque: {barbers.GetValueOrDefault(top.Key, "Profissional")} ({top.Sum(r => r.Price).ToString("C", Br)})");
        if (store.MonthlyRevenueGoal > 0) sb.AppendLine($"🎯 Meta: {(rev * 100 / store.MonthlyRevenueGoal):F0}% de {store.MonthlyRevenueGoal.ToString("C", Br)}");
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------ helpers

    private sealed record KpiSet(decimal Revenue, int Count, decimal Ticket, int Cancellations, int NoShows, decimal ReturnRate, int NewCustomers, decimal Rating);

    private async Task<List<Row>> LoadRowsAsync(int storeId, CancellationToken ct)
        => await db.Appointments.AsNoTracking()
            .Where(a => a.StoreId == storeId)
            .Select(a => new Row(a.Id, a.CustomerId, a.BarberId, a.ServiceItemId, a.AppointmentDate, a.StartTime, a.PriceCharged, a.Status, a.PaymentMethod))
            .ToListAsync(ct);

    private static (DateOnly From, DateOnly To) ParseRange(string? fromIso, string? toIso, DateOnly today)
    {
        var from = DateOnly.TryParseExact(fromIso, "yyyy-MM-dd", out var f) ? f : new DateOnly(today.Year, today.Month, 1);
        var to = DateOnly.TryParseExact(toIso, "yyyy-MM-dd", out var t) ? t : today;
        if (to < from) (from, to) = (to, from);
        if (to.DayNumber - from.DayNumber > 366) from = to.AddDays(-366);
        return (from, to);
    }

    private static KpiDto Kpi(string key, string label, decimal value, decimal previous, string format, bool higherIsBetter = true)
    {
        decimal? delta = previous == 0 ? (value == 0 ? 0 : null) : Math.Round((value - previous) * 100 / Math.Abs(previous), 1);
        return new KpiDto(key, label, value, previous, delta, format, higherIsBetter);
    }

    private static TicketBreakdownDto Ticket(string name, IEnumerable<Row> rows)
    {
        var list = rows.ToList();
        var revenue = list.Sum(r => r.Price);
        return new TicketBreakdownDto(name, revenue, list.Count, list.Count > 0 ? Math.Round(revenue / list.Count, 2) : 0);
    }

    internal static string PaymentBucket(string? method)
    {
        var m = (method ?? "").Trim().ToLowerInvariant();
        if (m.Contains("cart") || m.Contains("créd") || m.Contains("déb") || m.Contains("cred") || m.Contains("deb")) return "Cartão";
        if (m.Contains("dinh") || m.Contains("esp")) return "Dinheiro";
        return "PIX";
    }

    private static string StatusPt(string s) => s switch
    {
        "Confirmed" => "Confirmado",
        "Completed" => "Concluído",
        "Cancelled" => "Cancelado",
        "NoShow" => "Falta",
        _ => s
    };

    private static string Csv(string? v)
    {
        var t = (v ?? "").Replace(';', ',').Replace('\n', ' ').Replace('\r', ' ');
        // Evita injeção de fórmulas ao abrir o arquivo no Excel/Sheets
        return t.Length > 0 && "=+-@".Contains(t[0]) ? "'" + t : t;
    }
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");
    private static DateOnly Min(DateOnly a, DateOnly b) => a < b ? a : b;
    private static bool In(string iso, DateOnly f, DateOnly t) => string.CompareOrdinal(iso, Iso(f)) >= 0 && string.CompareOrdinal(iso, Iso(t)) <= 0;
    private static int RoundSlot(int minutes) => (int)Math.Ceiling(Math.Max(30, minutes) / 30.0) * 30;
}

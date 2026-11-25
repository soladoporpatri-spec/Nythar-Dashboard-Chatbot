using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Services.Features;

/// <summary>
/// Regra única de disponibilidade usada pelo bot, pela agenda manual e pela remarcação:
/// expediente da loja e do profissional, dias de folga, almoço, bloqueios recorrentes, dias sem funcionamento,
/// duração real do serviço (ocupa vários blocos de 30 min) e capacidade por horário de cada profissional.
/// </summary>
public interface IAvailabilityService
{
    Task<IReadOnlyList<string>> GetFreeSlotsAsync(int storeId, int barberId, string dateIso, int? serviceId, CancellationToken ct, long? excludeAppointmentId = null);
    Task<Barber?> ResolveBarberAsync(int storeId, int barberChoice, string dateIso, string time, int? serviceId, CancellationToken ct, long? excludeAppointmentId = null);
    Task<string?> ExplainUnavailableAsync(int storeId, int barberId, string dateIso, string time, int? serviceId, bool allowPast, CancellationToken ct, long? excludeAppointmentId = null);
}

public sealed class AvailabilityService(ClinicHairDbContext db) : IAvailabilityService
{
    public const int SlotMinutes = 30;
    private static readonly string[] InactiveStatuses = ["Cancelled", "NoShow"];

    private sealed record DayContext(
        Store Store,
        DateOnly Date,
        bool StoreClosed,
        string? ClosedReason,
        List<Barber> Barbers,
        List<RecurringBlock> Blocks,
        List<(int BarberId, int Start, int End)> Busy,
        int DurationMinutes);

    public async Task<IReadOnlyList<string>> GetFreeSlotsAsync(int storeId, int barberId, string dateIso, int? serviceId, CancellationToken ct, long? excludeAppointmentId = null)
    {
        var ctx = await LoadAsync(storeId, dateIso, serviceId, excludeAppointmentId, ct);
        if (ctx is null || ctx.StoreClosed) return [];

        var candidates = barberId > 0 ? ctx.Barbers.Where(b => b.Id == barberId).ToList() : ctx.Barbers;
        if (candidates.Count == 0) return [];

        int open = ParseMinutes(ctx.Store.OpeningTime, 8 * 60);
        int close = ParseMinutes(ctx.Store.ClosingTime, 20 * 60);
        bool isToday = ctx.Date == DateOnly.FromDateTime(DateTime.Now);
        int nowMin = DateTime.Now.Hour * 60 + DateTime.Now.Minute;

        var result = new List<string>();
        for (int min = open; min < close; min += SlotMinutes)
        {
            if (isToday && min <= nowMin) continue;
            if (candidates.Any(b => Why(ctx, b, min) is null))
            {
                result.Add(Format(min));
            }
        }
        return result;
    }

    public async Task<Barber?> ResolveBarberAsync(int storeId, int barberChoice, string dateIso, string time, int? serviceId, CancellationToken ct, long? excludeAppointmentId = null)
    {
        var ctx = await LoadAsync(storeId, dateIso, serviceId, excludeAppointmentId, ct);
        if (ctx is null || ctx.StoreClosed) return null;
        int min = ParseMinutes(time, -1);
        if (min < 0) return null;

        var candidates = barberChoice > 0 ? ctx.Barbers.Where(b => b.Id == barberChoice).ToList() : ctx.Barbers;
        // "Qualquer um": distribui para quem tem menos atendimentos no dia
        return candidates
            .Where(b => Why(ctx, b, min) is null)
            .OrderBy(b => ctx.Busy.Count(x => x.BarberId == b.Id))
            .ThenBy(b => b.Id)
            .FirstOrDefault();
    }

    public async Task<string?> ExplainUnavailableAsync(int storeId, int barberId, string dateIso, string time, int? serviceId, bool allowPast, CancellationToken ct, long? excludeAppointmentId = null)
    {
        var ctx = await LoadAsync(storeId, dateIso, serviceId, excludeAppointmentId, ct);
        if (ctx is null) return "Data inválida.";
        if (ctx.StoreClosed) return ctx.ClosedReason ?? "A loja não funciona neste dia.";
        int min = ParseMinutes(time, -1);
        if (min < 0) return "Horário inválido.";

        if (!allowPast && ctx.Date.ToDateTime(TimeOnly.MinValue).AddMinutes(min) <= DateTime.Now)
        {
            return "Este horário já passou.";
        }

        var barber = ctx.Barbers.FirstOrDefault(b => b.Id == barberId);
        if (barber is null) return "Profissional não encontrado ou inativo.";
        return Why(ctx, barber, min);
    }

    private static string? Why(DayContext ctx, Barber b, int startMin)
    {
        int dow = (int)ctx.Date.DayOfWeek;
        if (!ParseDays(b.WorkingDaysCsv).Contains(dow)) return $"{b.Name} não trabalha neste dia da semana.";

        int bStart = ParseMinutes(b.WorkStartTime, 8 * 60);
        int bEnd = ParseMinutes(b.WorkEndTime, 19 * 60);
        int storeClose = ParseMinutes(ctx.Store.ClosingTime, 20 * 60);
        int endMin = startMin + Math.Max(SlotMinutes, ctx.DurationMinutes);

        if (startMin < bStart || startMin >= bEnd) return $"Fora do expediente de {b.Name} ({b.WorkStartTime} às {b.WorkEndTime}).";
        if (endMin > bEnd || endMin > storeClose)
        {
            return "O serviço não termina antes do fim do expediente.";
        }

        int lStart = ParseMinutes(b.LunchStartTime, -1);
        int lEnd = ParseMinutes(b.LunchEndTime, -1);
        if (lStart >= 0 && lEnd > lStart && startMin < lEnd && endMin > lStart)
        {
            return $"Conflita com o almoço/pausa de {b.Name} ({b.LunchStartTime} às {b.LunchEndTime}).";
        }

        foreach (var blk in ctx.Blocks)
        {
            if (blk.BarberId != 0 && blk.BarberId != b.Id) continue;
            int s = ParseMinutes(blk.StartTime, -1), e = ParseMinutes(blk.EndTime, -1);
            if (s >= 0 && e > s && startMin < e && endMin > s)
            {
                return $"Horário bloqueado: {blk.Reason} ({blk.StartTime} às {blk.EndTime}).";
            }
        }

        // Capacidade: em cada bloco de 30 min ocupado pelo serviço, o profissional precisa ter vaga
        for (int m = startMin; m < endMin; m += SlotMinutes)
        {
            int slotEnd = m + SlotMinutes;
            int overlapping = ctx.Busy.Count(x => x.BarberId == b.Id && x.Start < slotEnd && x.End > m);
            if (overlapping >= Math.Max(1, b.MaxConcurrentPerSlot))
            {
                return $"{b.Name} já atingiu o limite de {b.MaxConcurrentPerSlot} atendimento(s) às {Format(m)}.";
            }
        }

        return null;
    }

    private async Task<DayContext?> LoadAsync(int storeId, string dateIso, int? serviceId, long? excludeAppointmentId, CancellationToken ct)
    {
        if (!DateOnly.TryParseExact(dateIso, "yyyy-MM-dd", out var date)) return null;
        var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == storeId, ct);
        if (store is null) return null;

        var nonWorking = await db.StoreNonWorkingDays.AsNoTracking()
            .FirstOrDefaultAsync(n => n.StoreId == storeId && n.BlockedDate == dateIso, ct);
        bool storeClosed = nonWorking is not null || !ParseDays(store.WorkingDaysCsv).Contains((int)date.DayOfWeek);
        string? closedReason = nonWorking is not null
            ? $"O dia está marcado como sem funcionamento ({nonWorking.Reason})."
            : storeClosed ? "A loja não abre neste dia da semana." : null;

        var barbers = await db.Barbers.AsNoTracking()
            .Where(b => b.StoreId == storeId && b.IsActive)
            .OrderBy(b => b.Id)
            .ToListAsync(ct);

        int dow = (int)date.DayOfWeek;
        var blocks = await db.RecurringBlocks.AsNoTracking()
            .Where(r => r.StoreId == storeId && r.IsActive && r.DayOfWeek == dow)
            .ToListAsync(ct);

        // Compromissos da agenda externa (Google Calendar via ICS) entram como bloqueios do profissional (item 95)
        foreach (var barber in barbers)
        {
            foreach (var (start, end, title) in ExternalCalendarCache.GetBusy(barber.Id, date))
            {
                blocks.Add(new RecurringBlock { BarberId = barber.Id, DayOfWeek = dow, StartTime = start, EndTime = end, Reason = title, IsActive = true });
            }
        }

        var durations = await db.ServiceItems.AsNoTracking()
            .Where(s => s.StoreId == storeId)
            .ToDictionaryAsync(s => s.Id, s => s.DurationMinutes, ct);

        var appts = await db.Appointments.AsNoTracking()
            .Where(a => a.StoreId == storeId && a.AppointmentDate == dateIso && !InactiveStatuses.Contains(a.Status))
            .Select(a => new { a.Id, a.BarberId, a.StartTime, a.ServiceItemId })
            .ToListAsync(ct);

        var busy = appts
            .Where(a => excludeAppointmentId is null || a.Id != excludeAppointmentId.Value)
            .Select(a =>
            {
                int s = ParseMinutes(a.StartTime, 0);
                int d = Math.Max(SlotMinutes, durations.GetValueOrDefault(a.ServiceItemId, SlotMinutes));
                return (a.BarberId, s, s + RoundUp(d));
            })
            .ToList();

        int duration = serviceId.HasValue ? durations.GetValueOrDefault(serviceId.Value, SlotMinutes) : SlotMinutes;
        return new DayContext(store, date, storeClosed, closedReason, barbers, blocks, busy, RoundUp(Math.Max(SlotMinutes, duration)));
    }

    private static int RoundUp(int minutes) => (int)Math.Ceiling(minutes / (double)SlotMinutes) * SlotMinutes;

    private static string Format(int min) => $"{min / 60:D2}:{min % 60:D2}";

    public static int ParseMinutes(string? hhmm, int fallback)
        => !string.IsNullOrWhiteSpace(hhmm) && TimeOnly.TryParseExact(hhmm.Trim(), "HH:mm", out var t) ? t.Hour * 60 + t.Minute : fallback;

    public static HashSet<int> ParseDays(string? csv)
    {
        var set = new HashSet<int>();
        foreach (var part in (csv ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (int.TryParse(part, out int d)) set.Add(d);
        }
        return set.Count > 0 ? set : [1, 2, 3, 4, 5, 6];
    }
}

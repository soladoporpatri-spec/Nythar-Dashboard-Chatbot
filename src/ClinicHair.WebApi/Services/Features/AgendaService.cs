using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Services.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Services.Features;

/// <summary>Agenda por profissional, remarcação (arrastar e soltar), check-in, bloqueios recorrentes e lista de espera.</summary>
public interface IAgendaService
{
    Task<AgendaBoardDto> GetBoardAsync(string? date, CancellationToken ct);
    Task<AgendaCardDto> MoveAsync(long id, MoveAppointmentRequest request, CancellationToken ct);
    Task<AgendaCardDto> SetStageAsync(long id, AppointmentStageRequest request, CancellationToken ct);
    Task<IReadOnlyList<RecurringBlockDto>> GetBlocksAsync(CancellationToken ct);
    Task<RecurringBlockDto> CreateBlockAsync(UpsertRecurringBlockRequest request, CancellationToken ct);
    Task<bool> DeleteBlockAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<WaitlistEntryDto>> GetWaitlistAsync(string? date, CancellationToken ct);
    Task<WaitlistEntryDto> AddWaitlistAsync(CreateWaitlistEntryRequest request, CancellationToken ct);
    Task<WaitlistEntryDto?> UpdateWaitlistStatusAsync(int id, string status, CancellationToken ct);
    Task<bool> DeleteWaitlistAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<ServiceTimeStatDto>> GetServiceTimesAsync(CancellationToken ct);
}

public sealed class AgendaService(
    ClinicHairDbContext db,
    ICurrentStore current,
    IAuditLogger audit,
    IAvailabilityService availability,
    IWaitlistNotifier waitlist,
    IWhatsAppBridgeClient bridge,
    IDashboardRealtimeNotifier realtime,
    ICompletionHooks completion) : IAgendaService
{
    private static readonly string[] Inactive = ["Cancelled", "NoShow"];

    public async Task<AgendaBoardDto> GetBoardAsync(string? date, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var dateIso = DateOnly.TryParseExact(date, "yyyy-MM-dd", out var d) ? d.ToString("yyyy-MM-dd") : DateTime.Now.ToString("yyyy-MM-dd");
        var store = await StoreQueries.LoadAsync(db, storeId, false, ct);

        var barbers = await db.Barbers.AsNoTracking().Where(b => b.StoreId == storeId && b.IsActive).OrderBy(b => b.Id).ToListAsync(ct);
        var appts = await db.Appointments.AsNoTracking()
            .Include(a => a.Customer).Include(a => a.ServiceItem)
            .Where(a => a.StoreId == storeId && a.AppointmentDate == dateIso && a.Status != "Cancelled")
            .ToListAsync(ct);

        int open = AvailabilityService.ParseMinutes(store.OpeningTime, 8 * 60);
        int close = AvailabilityService.ParseMinutes(store.ClosingTime, 20 * 60);
        var slots = new List<string>();
        for (int m = open; m < close; m += AvailabilityService.SlotMinutes) slots.Add($"{m / 60:D2}:{m % 60:D2}");

        var dow = (int)DateOnly.ParseExact(dateIso, "yyyy-MM-dd").DayOfWeek;
        var columns = barbers.Select(b =>
        {
            var mine = appts.Where(a => a.BarberId == b.Id).OrderBy(a => a.StartTime).ThenBy(a => a.Id).ToList();
            int available = 0;
            if (AvailabilityService.ParseDays(b.WorkingDaysCsv).Contains(dow))
            {
                int ws = AvailabilityService.ParseMinutes(b.WorkStartTime, open), we = AvailabilityService.ParseMinutes(b.WorkEndTime, close);
                int ls = AvailabilityService.ParseMinutes(b.LunchStartTime, -1), le = AvailabilityService.ParseMinutes(b.LunchEndTime, -1);
                available = Math.Max(0, we - ws) - (ls >= 0 && le > ls ? Math.Max(0, Math.Min(le, we) - Math.Max(ls, ws)) : 0);
                available *= Math.Max(1, b.MaxConcurrentPerSlot);
            }
            int booked = mine.Where(a => !Inactive.Contains(a.Status)).Sum(a => Duration(a.ServiceItem));
            int pct = available > 0 ? (int)Math.Round(Math.Min(100.0, booked * 100.0 / available)) : 0;
            return new AgendaBarberColumnDto(b.Id, b.Name, b.WorkStartTime, b.WorkEndTime, b.LunchStartTime, b.LunchEndTime, pct,
                mine.Select(ToCard).ToList());
        }).ToList();

        return new AgendaBoardDto(dateIso, slots, columns);
    }

    public async Task<AgendaCardDto> MoveAsync(long id, MoveAppointmentRequest r, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var appt = await LoadApptAsync(id, ct);
        if (Inactive.Contains(appt.Status) || appt.Status == "Completed")
        {
            throw new InvalidOperationException("Só é possível remarcar agendamentos ativos.");
        }
        if (!DateOnly.TryParseExact(r.Date, "yyyy-MM-dd", out _) || !TimeOnly.TryParseExact(r.Time, "HH:mm", out _))
        {
            throw new ArgumentException("Data ou horário inválido.");
        }

        var reason = await availability.ExplainUnavailableAsync(storeId, r.BarberId, r.Date, r.Time, appt.ServiceItemId, allowPast: false, ct, appt.Id);
        if (reason is not null) throw new InvalidOperationException(reason);

        var oldDate = appt.AppointmentDate;
        var oldTime = appt.StartTime;
        var oldBarber = appt.BarberId;

        appt.AppointmentDate = r.Date;
        appt.StartTime = r.Time;
        appt.BarberId = r.BarberId;
        appt.ReminderSentAtUtc = null;
        appt.ReminderPollMessageId = null;
        appt.Reminder24hSentAtUtc = null;
        appt.Reminder24hResentAtUtc = null;
        appt.Reminder24hPollMessageId = null;
        appt.CustomerConfirmedAtUtc = null;

        var newBarber = await db.Barbers.AsNoTracking().FirstAsync(b => b.Id == r.BarberId, ct);
        audit.Stage("Agenda", $"Remarcou o agendamento #{appt.Id} de {oldDate} {oldTime} para {r.Date} {r.Time} ({newBarber.Name}).");
        await db.SaveChangesAsync(ct);

        await waitlist.NotifySlotFreedAsync(storeId, oldDate, oldBarber, ct);

        var store = await StoreQueries.LoadAsync(db, storeId, false, ct);
        if (r.NotifyCustomer && !store.BotPaused && appt.Customer is not null)
        {
            var digits = PhoneIdentity.Canonical(appt.Customer.Phone);
            if (digits.Length >= 10)
            {
                var dateBr = DateOnly.ParseExact(r.Date, "yyyy-MM-dd").ToString("dd/MM");
                try
                {
                    await bridge.SendTextAsync($"{digits}@s.whatsapp.net",
                        $"Oi {appt.Customer.Name}! Seu horário na {store.Name} foi alterado para {dateBr} às {r.Time} com {newBarber.Name}. Qualquer dúvida, é só responder aqui.",
                        ct);
                }
                catch { /* WhatsApp offline: a remarcação continua válida */ }
            }
        }

        realtime.Publish("agenda", $"Agendamento de {appt.Customer?.Name ?? "cliente"} movido para {r.Time}.", "Agenda atualizada");
        appt.Barber = newBarber;
        return ToCard(appt);
    }

    public async Task<AgendaCardDto> SetStageAsync(long id, AppointmentStageRequest r, CancellationToken ct)
    {
        var appt = await LoadApptAsync(id, ct);
        var now = DateTimeOffset.UtcNow;
        var previous = appt.Status;

        switch ((r.Stage ?? "").ToLowerInvariant())
        {
            case "arrived":
                appt.ArrivedAtUtc ??= now;
                if (appt.Status is "Cancelled" or "NoShow") appt.Status = "Confirmed";
                break;
            case "start":
                appt.ArrivedAtUtc ??= now;
                appt.ServiceStartedAtUtc = now;
                if (appt.Status is "Cancelled" or "NoShow") appt.Status = "Confirmed";
                break;
            case "complete":
                appt.ArrivedAtUtc ??= now;
                appt.CompletedAtUtc = now;
                appt.Status = "Completed";
                if (!string.IsNullOrWhiteSpace(r.PaymentMethod)) appt.PaymentMethod = r.PaymentMethod.Trim();
                if (previous != "Completed")
                {
                    var note = await completion.OnCompletedAsync(appt, ct);
                    if (note is not null) audit.Stage("Agenda", $"Agendamento #{appt.Id}: {note}");
                }
                break;
            case "noshow":
                appt.Status = "NoShow";
                appt.ArrivedAtUtc = null;
                appt.ServiceStartedAtUtc = null;
                appt.CompletedAtUtc = null;
                break;
            case "reset":
                appt.Status = "Confirmed";
                appt.ArrivedAtUtc = null;
                appt.ServiceStartedAtUtc = null;
                appt.CompletedAtUtc = null;
                break;
            default:
                throw new ArgumentException("Etapa inválida.");
        }

        await db.SaveChangesAsync(ct);

        if (appt.Status == "NoShow" && previous != "NoShow")
        {
            await audit.LogAsync("Agenda", $"Marcou falta (no-show) no agendamento #{appt.Id}.", ct);
            await waitlist.NotifySlotFreedAsync(appt.StoreId, appt.AppointmentDate, appt.BarberId, ct);
        }

        realtime.Publish("agenda", $"{appt.Customer?.Name ?? "Cliente"}: {StageLabel(Stage(appt))}", "Check-in");
        return ToCard(appt);
    }

    public async Task<IReadOnlyList<RecurringBlockDto>> GetBlocksAsync(CancellationToken ct)
    {
        var storeId = current.StoreId;
        var names = await db.Barbers.AsNoTracking().Where(b => b.StoreId == storeId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var list = await db.RecurringBlocks.AsNoTracking().Where(r => r.StoreId == storeId && r.IsActive)
            .OrderBy(r => r.DayOfWeek).ThenBy(r => r.StartTime).ToListAsync(ct);
        return list.Select(r => ToDto(r, names)).ToList();
    }

    public async Task<RecurringBlockDto> CreateBlockAsync(UpsertRecurringBlockRequest r, CancellationToken ct)
    {
        var storeId = current.StoreId;
        if (r.DayOfWeek is < 0 or > 6) throw new ArgumentException("Dia da semana inválido.");
        int s = AvailabilityService.ParseMinutes(r.StartTime, -1), e = AvailabilityService.ParseMinutes(r.EndTime, -1);
        if (s < 0 || e <= s) throw new ArgumentException("Informe um intervalo de horário válido (início antes do fim).");
        if (r.BarberId != 0 && !await db.Barbers.AnyAsync(b => b.Id == r.BarberId && b.StoreId == storeId, ct))
        {
            throw new KeyNotFoundException("Profissional não encontrado.");
        }

        var block = new RecurringBlock
        {
            StoreId = storeId,
            BarberId = r.BarberId,
            DayOfWeek = r.DayOfWeek,
            StartTime = r.StartTime.Trim(),
            EndTime = r.EndTime.Trim(),
            Reason = string.IsNullOrWhiteSpace(r.Reason) ? "Bloqueio" : r.Reason.Trim()[..Math.Min(80, r.Reason.Trim().Length)]
        };
        db.RecurringBlocks.Add(block);
        audit.Stage("Agenda", $"Criou bloqueio recorrente ({block.Reason}) {block.StartTime}-{block.EndTime}, dia {block.DayOfWeek}.");
        await db.SaveChangesAsync(ct);
        var names = await db.Barbers.AsNoTracking().Where(b => b.StoreId == storeId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        return ToDto(block, names);
    }

    public async Task<bool> DeleteBlockAsync(int id, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var block = await db.RecurringBlocks.AsTracking().FirstOrDefaultAsync(r => r.Id == id && r.StoreId == storeId, ct);
        if (block is null) return false;
        db.RecurringBlocks.Remove(block);
        audit.Stage("Agenda", $"Removeu bloqueio recorrente ({block.Reason}).");
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<WaitlistEntryDto>> GetWaitlistAsync(string? date, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        var q = db.WaitlistEntries.AsNoTracking().Where(w => w.StoreId == storeId && w.Status != "Removed");
        q = string.IsNullOrWhiteSpace(date) ? q.Where(w => string.Compare(w.Date, today) >= 0) : q.Where(w => w.Date == date);
        var list = await q.OrderBy(w => w.Date).ThenBy(w => w.Id).ToListAsync(ct);
        var barbers = await db.Barbers.AsNoTracking().Where(b => b.StoreId == storeId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var services = await db.ServiceItems.AsNoTracking().Where(s => s.StoreId == storeId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return list.Select(w => ToDto(w, barbers, services)).ToList();
    }

    public async Task<WaitlistEntryDto> AddWaitlistAsync(CreateWaitlistEntryRequest r, CancellationToken ct)
    {
        var storeId = current.StoreId;
        if (string.IsNullOrWhiteSpace(r.CustomerName)) throw new ArgumentException("Informe o nome do cliente.");
        var phone = PhoneIdentity.Canonical(r.Phone);
        if (phone.Length < 10) throw new ArgumentException("Telefone inválido.");
        if (!DateOnly.TryParseExact(r.Date, "yyyy-MM-dd", out var d) || d < DateOnly.FromDateTime(DateTime.Now))
        {
            throw new ArgumentException("Informe uma data de hoje em diante.");
        }

        var entry = new WaitlistEntry
        {
            StoreId = storeId,
            CustomerName = r.CustomerName.Trim(),
            Phone = phone,
            Date = r.Date,
            BarberId = r.BarberId is > 0 ? r.BarberId : null,
            ServiceItemId = r.ServiceItemId is > 0 ? r.ServiceItemId : null,
            Notes = string.IsNullOrWhiteSpace(r.Notes) ? null : r.Notes.Trim()
        };
        db.WaitlistEntries.Add(entry);
        await db.SaveChangesAsync(ct);
        var barbers = await db.Barbers.AsNoTracking().Where(b => b.StoreId == storeId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var services = await db.ServiceItems.AsNoTracking().Where(s => s.StoreId == storeId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return ToDto(entry, barbers, services);
    }

    public async Task<WaitlistEntryDto?> UpdateWaitlistStatusAsync(int id, string status, CancellationToken ct)
    {
        if (status is not ("Waiting" or "Notified" or "Booked" or "Removed")) throw new ArgumentException("Status inválido.");
        var storeId = current.StoreId;
        var entry = await db.WaitlistEntries.AsTracking().FirstOrDefaultAsync(w => w.Id == id && w.StoreId == storeId, ct);
        if (entry is null) return null;
        entry.Status = status;
        await db.SaveChangesAsync(ct);
        var barbers = await db.Barbers.AsNoTracking().Where(b => b.StoreId == storeId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var services = await db.ServiceItems.AsNoTracking().Where(s => s.StoreId == storeId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        return ToDto(entry, barbers, services);
    }

    public async Task<bool> DeleteWaitlistAsync(int id, CancellationToken ct)
    {
        var storeId = current.StoreId;
        var entry = await db.WaitlistEntries.AsTracking().FirstOrDefaultAsync(w => w.Id == id && w.StoreId == storeId, ct);
        if (entry is null) return false;
        db.WaitlistEntries.Remove(entry);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<ServiceTimeStatDto>> GetServiceTimesAsync(CancellationToken ct)
        => await ComputeServiceTimesAsync(db, current.StoreId, ct);

    internal static async Task<IReadOnlyList<ServiceTimeStatDto>> ComputeServiceTimesAsync(ClinicHairDbContext db, int storeId, CancellationToken ct)
    {
        var services = await db.ServiceItems.AsNoTracking().Where(s => s.StoreId == storeId && s.IsActive).OrderBy(s => s.DisplayOrder).ToListAsync(ct);
        var samples = (await db.Appointments.AsNoTracking()
                .Where(a => a.StoreId == storeId && a.ServiceStartedAtUtc != null && a.CompletedAtUtc != null)
                .Select(a => new { a.ServiceItemId, a.ServiceStartedAtUtc, a.CompletedAtUtc })
                .ToListAsync(ct))
            .Select(a => new { a.ServiceItemId, Minutes = (a.CompletedAtUtc!.Value - a.ServiceStartedAtUtc!.Value).TotalMinutes })
            .Where(a => a.Minutes is > 2 and < 240) // descarta cliques acidentais e esquecimentos
            .ToList();

        return services.Select(s =>
        {
            var mine = samples.Where(x => x.ServiceItemId == s.Id).ToList();
            int? avg = mine.Count > 0 ? (int)Math.Round(mine.Average(x => x.Minutes)) : null;
            return new ServiceTimeStatDto(s.Id, s.Name, s.DurationMinutes, avg, mine.Count);
        }).ToList();
    }

    // ------------------------------------------------------------------ helpers

    private async Task<Appointment> LoadApptAsync(long id, CancellationToken ct)
    {
        var storeId = current.StoreId;
        return await db.Appointments.AsTracking()
            .Include(a => a.Customer).Include(a => a.Barber).Include(a => a.ServiceItem)
            .FirstOrDefaultAsync(a => a.Id == id && a.StoreId == storeId, ct)
            ?? throw new KeyNotFoundException("Agendamento não encontrado.");
    }

    private static int Duration(ServiceItem? s)
    {
        int d = Math.Max(AvailabilityService.SlotMinutes, s?.DurationMinutes ?? AvailabilityService.SlotMinutes);
        return (int)Math.Ceiling(d / (double)AvailabilityService.SlotMinutes) * AvailabilityService.SlotMinutes;
    }

    internal static string Stage(Appointment a) => a.Status switch
    {
        "NoShow" => "NoShow",
        "Cancelled" => "Cancelled",
        "Completed" => "Done",
        _ when a.CompletedAtUtc is not null => "Done",
        _ when a.ServiceStartedAtUtc is not null => "InService",
        _ when a.ArrivedAtUtc is not null => "Arrived",
        _ => "Waiting"
    };

    private static string StageLabel(string stage) => stage switch
    {
        "Arrived" => "chegou",
        "InService" => "em atendimento",
        "Done" => "atendimento concluído",
        "NoShow" => "faltou",
        _ => "aguardando"
    };

    private static AgendaCardDto ToCard(Appointment a)
    {
        int start = AvailabilityService.ParseMinutes(a.StartTime, 0);
        int end = start + Duration(a.ServiceItem);
        return new AgendaCardDto(
            a.Id, a.AppointmentDate, a.StartTime, $"{end / 60:D2}:{end % 60:D2}", a.BarberId, a.CustomerId,
            a.Customer?.Name ?? "Cliente", a.Customer?.Phone ?? "", a.ServiceItem?.Name ?? "Serviço",
            a.ServiceItem?.ColorHex ?? "#FF6B00", a.PriceCharged, a.Status, Stage(a), a.CustomerConfirmedAtUtc is not null,
            string.IsNullOrWhiteSpace(a.PaymentMethod) ? "PIX" : a.PaymentMethod);
    }

    private static RecurringBlockDto ToDto(RecurringBlock r, Dictionary<int, string> names)
        => new(r.Id, r.BarberId, r.BarberId == 0 ? "Todos" : names.GetValueOrDefault(r.BarberId, "Profissional"), r.DayOfWeek, r.StartTime, r.EndTime, r.Reason, r.IsActive);

    private static WaitlistEntryDto ToDto(WaitlistEntry w, Dictionary<int, string> barbers, Dictionary<int, string> services)
        => new(w.Id, w.CustomerName, w.Phone, w.Date, w.BarberId,
            w.BarberId is int b ? barbers.GetValueOrDefault(b) : null,
            w.ServiceItemId, w.ServiceItemId is int s ? services.GetValueOrDefault(s) : null,
            w.Notes, w.Status, w.CreatedAt, w.NotifiedAt);
}

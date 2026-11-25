using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ClosedXML.Excel;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Infrastructure.Storage;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

namespace ClinicHair.WebApi.Services;

/// <summary>
/// Interface da camada de serviço da Dashboard Clinic Hair.
/// </summary>
public interface IDashboardService
{
    Task<AuthResponse?> AuthenticateAsync(LoginRequest request, CancellationToken ct);
    Task<IReadOnlyList<AppointmentResponse>> GetAppointmentsAsync(string? date, CancellationToken ct);
    Task<AppointmentResponse> CreateManualAppointmentAsync(CreateManualAppointmentRequest request, CancellationToken ct);
    Task<AppointmentResponse?> UpdateAppointmentStatusAsync(long id, string status, string? paymentMethod, CancellationToken ct);
    Task<IReadOnlyList<string>> GetAvailableSlotsForBarberAsync(int barberId, string date, CancellationToken ct);
    Task<IReadOnlyList<CalendarDaySummaryResponse>> GetCalendarMonthSummaryAsync(int year, int month, CancellationToken ct);
    Task<CalendarDaySummaryResponse> ToggleNonWorkingDayAsync(ToggleNonWorkingDayRequest request, CancellationToken ct);
    Task<IReadOnlyList<CustomerCrmResponse>> GetCrmCustomersAsync(CancellationToken ct);
    Task<IReadOnlyList<CustomerEvaluationDetailResponse>> GetCustomerEvaluationsAsync(int customerId, CancellationToken ct);
    Task<IReadOnlyList<AnonymousEvaluationResponse>> GetAnonymousEvaluationsAsync(CancellationToken ct);
    Task<AnonymousEvaluationResponse?> UpdateEvaluationSentimentAsync(long evaluationId, string sentiment, CancellationToken ct);
    Task<(string FileName, string Content)?> GetEvaluationTxtDownloadAsync(long evaluationId, CancellationToken ct);
    Task<EvaluationStorageStatsResponse> GetEvaluationStorageStatsAsync(CancellationToken ct);
    Task<EvaluationStorageStatsResponse> TriggerStorageCleanupAsync(CancellationToken ct);
    Task<DashboardReportsResponse> GetReportsAsync(CancellationToken ct);
    Task<byte[]> ExportExcelReportAsync(CancellationToken ct);
    Task<byte[]> ExportGoogleSheetsCsvAsync(CancellationToken ct);
    Task<IReadOnlyList<BarberResponse>> GetBarbersAsync(CancellationToken ct);
    Task<BarberResponse> UpsertBarberAsync(int? id, UpsertBarberRequest request, CancellationToken ct);
    Task<bool> DeleteBarberAsync(int id, CancellationToken ct);
    Task<IReadOnlyList<ServiceItemResponse>> GetServicesAsync(CancellationToken ct);
    Task<ServiceItemResponse> UpsertServiceAsync(int? id, UpsertServiceItemRequest request, CancellationToken ct);
    Task<bool> DeleteServiceAsync(int id, CancellationToken ct);
    Task<StoreSettingsResponse> GetStoreSettingsAsync(CancellationToken ct);
    Task<StoreSettingsResponse> UpdateStoreSettingsAsync(UpdateStoreSettingsRequest request, CancellationToken ct);
    Task<WhatsAppBridgeStatusResponse> GetWhatsAppBridgeStatusAsync(CancellationToken ct);
    Task<WhatsAppBridgeStatusResponse> ControlWhatsAppBridgeAsync(string action, CancellationToken ct);
    Task<WhatsAppBridgeStatusResponse> SetWhatsAppBotPausedAsync(bool paused, CancellationToken ct);
    Task<WhatsAppBridgeStatusResponse> RequestWhatsAppPairingCodeAsync(string phoneNumber, CancellationToken ct);
}

/// <summary>
/// Implementação da lógica de negócio da Dashboard Clinic Hair.
/// </summary>
public sealed class DashboardService(
    ClinicHairDbContext db,
    IEvaluationTxtStorageService txtStorage,
    IWhatsAppBridgeClient bridgeClient,
    IDashboardRealtimeNotifier realtime,
    IHttpContextAccessor httpContextAccessor,
    ICustomerDirectory customers,
    IConfiguration config,
    ClinicHair.WebApi.Services.Features.IAvailabilityService availability,
    ClinicHair.WebApi.Services.Features.IWaitlistNotifier waitlist,
    ClinicHair.WebApi.Services.Platform.IAuditLogger audit,
    ClinicHair.WebApi.Services.Features.ICompletionHooks completion) : IDashboardService
{
    private int GetCurrentStoreId()
    {
        var ctx = httpContextAccessor.HttpContext;
        if (ctx is not null)
        {
            // Somente o SuperAdministrador pode "entrar" em outra loja. Lojas comuns ficam presas ao próprio StoreId do token.
            if (ctx.User.IsInRole("SuperAdmin"))
            {
                if (ctx.Request.Headers.TryGetValue("X-Target-Store-Id", out var headerVal)
                    && int.TryParse(headerVal.ToString(), out var headerStoreId)
                    && headerStoreId > 0)
                {
                    return headerStoreId;
                }

                if (ctx.Request.Query.TryGetValue("targetStoreId", out var queryVal)
                    && int.TryParse(queryVal.ToString(), out var queryStoreId)
                    && queryStoreId > 0)
                {
                    return queryStoreId;
                }
            }

            var claimVal = ctx.User.FindFirst("StoreId")?.Value;
            if (int.TryParse(claimVal, out var claimStoreId) && claimStoreId > 0)
            {
                return claimStoreId;
            }
        }

        return 1;
    }

    private async Task<Store> GetCurrentStoreAsync(bool asTracking, CancellationToken ct)
    {
        var storeId = GetCurrentStoreId();
        var query = asTracking ? db.Stores.AsTracking() : db.Stores.AsNoTracking();
        return await query.FirstOrDefaultAsync(s => s.Id == storeId, ct)
            ?? await query.FirstAsync(ct);
    }

    public async Task<AuthResponse?> AuthenticateAsync(LoginRequest request, CancellationToken ct)
    {
        var usernameNormalized = request.Username.Trim();
        var user = await db.AdminUsers
            .FirstOrDefaultAsync(u => u.Username.ToLower() == usernameNormalized.ToLower(), ct);

        if (user is null)
        {
            return null;
        }

        var hasher = new PasswordHasher<AdminUser>();
        var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verify == PasswordVerificationResult.Failed)
        {
            return null;
        }

        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == user.StoreId, ct)
            ?? await db.Stores.FirstAsync(ct);

        if (!string.Equals(user.Role, "SuperAdmin", StringComparison.OrdinalIgnoreCase) && !store.IsActive)
        {
            throw new InvalidOperationException(
                $"O acesso da loja '{store.Name}' encontra-se suspenso (Assinatura: {store.SubscriptionStatus}). Entre em contato com o SuperAdministrador.");
        }

        db.SystemLogEntries.Add(new SystemLogEntry
        {
            StoreId = store.Id,
            StoreName = store.Name,
            Level = "SECURITY",
            Category = "Auth",
            Message = $"Login autenticado com sucesso por '{user.Username}' (Perfil: {user.Role} • Loja: {store.Name}).",
            CreatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync(ct);

        var secret = config["Jwt:SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey não configurado.");
        var expires = DateTimeOffset.UtcNow.AddHours(12);

        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

        var claims = new[]
        {
            new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new Claim(ClaimTypes.Name, user.Username),
            new Claim(ClaimTypes.Role, user.Role),
            new Claim("StoreId", store.Id.ToString())
        };

        var jwt = new JwtSecurityToken(
            issuer: "ClinicHair.WebApi",
            audience: "ClinicHair.Dashboard",
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        var tokenString = new JwtSecurityTokenHandler().WriteToken(jwt);
        return new AuthResponse(tokenString, user.Username, store.Name, expires, user.Role, store.Id);
    }

    public async Task<IReadOnlyList<AppointmentResponse>> GetAppointmentsAsync(string? date, CancellationToken ct)
    {
        var storeId = GetCurrentStoreId();
        var query = db.Appointments
            .Include(a => a.Customer)
            .Include(a => a.Barber)
            .Include(a => a.ServiceItem)
            .Where(a => a.StoreId == storeId)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(date))
        {
            query = query.Where(a => a.AppointmentDate == date);
        }

        var list = await query
            .OrderBy(a => a.AppointmentDate)
            .ThenBy(a => a.StartTime)
            .ToListAsync(ct);

        // Calcula quantos agendamentos ativos cada funcionário possui no mesmo dia e hora (regra de limite 3)
        var slotCounts = list
            .Where(a => a.Status != "Cancelled" && a.Status != "NoShow")
            .GroupBy(a => (a.BarberId, a.AppointmentDate, a.StartTime))
            .ToDictionary(g => g.Key, g => g.Count());

        return list.Select(a =>
        {
            var count = slotCounts.GetValueOrDefault((a.BarberId, a.AppointmentDate, a.StartTime), 0);
            return new AppointmentResponse(
                a.Id,
                a.AppointmentDate,
                a.StartTime,
                a.CustomerId,
                a.Customer?.Name ?? "Cliente",
                a.Customer?.Phone ?? "",
                a.BarberId,
                a.Barber?.Name ?? "Profissional",
                a.ServiceItemId,
                a.ServiceItem?.Name ?? "Serviço",
                a.PriceCharged,
                a.ServiceItem?.ColorHex ?? "#FF6B00",
                a.Status,
                a.Source,
                a.Notes,
                count,
                a.Barber?.MaxConcurrentPerSlot ?? 3,
                string.IsNullOrWhiteSpace(a.PaymentMethod) ? "PIX" : a.PaymentMethod);
        }).ToList();
    }

    public async Task<AppointmentResponse> CreateManualAppointmentAsync(CreateManualAppointmentRequest request, CancellationToken ct)
    {
        var store = await GetCurrentStoreAsync(false, ct);

        // 1. Verifica se o dia está marcado como Não Funcionamento (Amarelo no Calendário)
        var nonWorking = await db.StoreNonWorkingDays
            .FirstOrDefaultAsync(n => n.StoreId == store.Id && n.BlockedDate == request.AppointmentDate, ct);
        if (nonWorking is not null)
        {
            throw new InvalidOperationException(
                $"O dia {request.AppointmentDate} está registrado como Dia de Não Funcionamento ({nonWorking.Reason}).");
        }

        var barber = await db.Barbers.FirstOrDefaultAsync(b => b.Id == request.BarberId && b.StoreId == store.Id, ct)
            ?? throw new KeyNotFoundException("Funcionário não encontrado.");

        var service = await db.ServiceItems.FirstOrDefaultAsync(s => s.Id == request.ServiceItemId && s.StoreId == store.Id, ct)
            ?? throw new KeyNotFoundException("Serviço não encontrado.");

        // 2. Verifica horário de expediente e horário de almoço/pausa do funcionário
        if (string.CompareOrdinal(request.StartTime, barber.WorkStartTime) < 0 ||
            string.CompareOrdinal(request.StartTime, barber.WorkEndTime) >= 0)
        {
            throw new InvalidOperationException(
                $"O horário {request.StartTime} está fora do expediente de {barber.Name} ({barber.WorkStartTime} às {barber.WorkEndTime}).");
        }

        if (!string.IsNullOrWhiteSpace(barber.LunchStartTime) &&
            !string.IsNullOrWhiteSpace(barber.LunchEndTime) &&
            string.CompareOrdinal(request.StartTime, barber.LunchStartTime) >= 0 &&
            string.CompareOrdinal(request.StartTime, barber.LunchEndTime) < 0)
        {
            throw new InvalidOperationException(
                $"O horário {request.StartTime} está dentro do intervalo de almoço/pausa de {barber.Name} ({barber.LunchStartTime} às {barber.LunchEndTime}).");
        }

        // 3. Regra de Capacidade: máximo 3 agendamentos para a mesma hora por funcionário
        var activeInSlot = await db.Appointments.CountAsync(a =>
            a.StoreId == store.Id &&
            a.BarberId == barber.Id &&
            a.AppointmentDate == request.AppointmentDate &&
            a.StartTime == request.StartTime &&
            a.Status != "Cancelled" && a.Status != "NoShow", ct);

        if (activeInSlot >= barber.MaxConcurrentPerSlot)
        {
            throw new InvalidOperationException(
                $"O profissional {barber.Name} já atingiu o limite máximo de {barber.MaxConcurrentPerSlot} agendamentos às {request.StartTime} no dia {request.AppointmentDate}.");
        }

        // 3b. Duração do serviço, bloqueios recorrentes e sobreposição com atendimentos longos
        var unavailable = await availability.ExplainUnavailableAsync(
            store.Id, barber.Id, request.AppointmentDate, request.StartTime, service.Id, allowPast: true, ct);
        if (unavailable is not null)
        {
            throw new InvalidOperationException(unavailable);
        }

        // 4. Busca ou cria cliente no CRM
        var normalizedPhone = PhoneIdentity.Canonical(request.CustomerPhone);
        if (string.IsNullOrWhiteSpace(normalizedPhone))
        {
            normalizedPhone = request.CustomerPhone.Trim();
        }

        var customer = await customers.FindAsync(store.Id, request.CustomerPhone, tracking: true, ct);

        if (customer is null)
        {
            customer = new Customer
            {
                StoreId = store.Id,
                Name = request.CustomerName.Trim(),
                Phone = normalizedPhone,
                TotalAppointments = 1,
                LastAppointmentAt = DateTimeOffset.UtcNow
            };
            db.Customers.Add(customer);
            await db.SaveChangesAsync(ct);
        }
        else
        {
            customer.Name = request.CustomerName.Trim();
            customer.TotalAppointments += 1;
            customer.LastAppointmentAt = DateTimeOffset.UtcNow;
            db.Customers.Update(customer);
        }

        var appointment = new Appointment
        {
            StoreId = store.Id,
            CustomerId = customer.Id,
            BarberId = barber.Id,
            ServiceItemId = service.Id,
            AppointmentDate = request.AppointmentDate,
            StartTime = request.StartTime,
            PriceCharged = service.Price,
            PaymentMethod = string.IsNullOrWhiteSpace(request.PaymentMethod) ? "PIX" : request.PaymentMethod.Trim(),
            Status = "Confirmed",
            Source = "Manual",
            Notes = request.Notes
        };

        db.Appointments.Add(appointment);
        await db.SaveChangesAsync(ct);

        var formattedDate = DateOnly.TryParseExact(request.AppointmentDate, "yyyy-MM-dd", out var parsedDate)
            ? parsedDate.ToString("dd/MM/yyyy")
            : request.AppointmentDate;

        string barberWhatsAppMsg = $"{customer.Name} agendou {service.Name} às {request.StartTime} do dia {formattedDate}.";
        var barberJid = ResolveBarberWhatsAppJid(barber.Phone, store.ConnectedPhone);
        if (!string.IsNullOrWhiteSpace(barberJid))
        {
            await bridgeClient.SendTextAsync(barberJid, barberWhatsAppMsg, ct);
        }

        realtime.Publish("agenda", barberWhatsAppMsg, $"Novo Agendamento • {barber.Name}");

        return new AppointmentResponse(
            appointment.Id,
            appointment.AppointmentDate,
            appointment.StartTime,
            customer.Id,
            customer.Name,
            customer.Phone,
            barber.Id,
            barber.Name,
            service.Id,
            service.Name,
            appointment.PriceCharged,
            service.ColorHex,
            appointment.Status,
            appointment.Source,
            appointment.Notes,
            activeInSlot + 1,
            barber.MaxConcurrentPerSlot,
            appointment.PaymentMethod);
    }

    public async Task<AppointmentResponse?> UpdateAppointmentStatusAsync(long id, string status, string? paymentMethod, CancellationToken ct)
    {
        var scopeStoreId = GetCurrentStoreId();
        var appt = await db.Appointments
            .AsTracking()
            .Include(a => a.Customer)
            .Include(a => a.Barber)
            .Include(a => a.ServiceItem)
            .FirstOrDefaultAsync(a => a.Id == id && a.StoreId == scopeStoreId, ct);

        if (appt is null) return null;

        var previousStatus = appt.Status;
        appt.Status = status;
        if (status == "Completed" && appt.CompletedAtUtc is null) appt.CompletedAtUtc = DateTimeOffset.UtcNow;
        if (status != previousStatus) audit.Stage("Agenda", $"Alterou o agendamento #{appt.Id} de {previousStatus} para {status}.");
        if (!string.IsNullOrWhiteSpace(paymentMethod))
        {
            appt.PaymentMethod = paymentMethod.Trim();
        }
        if (status == "Completed" && previousStatus != "Completed")
        {
            var note = await completion.OnCompletedAsync(appt, ct);
            if (note is not null) audit.Stage("Agenda", $"Agendamento #{appt.Id}: {note}");
        }
        db.Appointments.Update(appt);
        await db.SaveChangesAsync(ct);

        if (status is "Cancelled" or "NoShow" && previousStatus is not ("Cancelled" or "NoShow"))
        {
            await waitlist.NotifySlotFreedAsync(appt.StoreId, appt.AppointmentDate, appt.BarberId, ct);
        }

        // Se o agendamento foi cancelado, avisa o funcionário no WhatsApp
        if (status == "Cancelled" && previousStatus != "Cancelled")
        {
            var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == appt.StoreId, ct);
            var formattedDate = DateOnly.TryParseExact(appt.AppointmentDate, "yyyy-MM-dd", out var d)
                ? d.ToString("dd/MM/yyyy")
                : appt.AppointmentDate;
            var cancelMsg = $"⚠️ {appt.Customer?.Name ?? "Cliente"} teve o agendamento de {appt.ServiceItem?.Name ?? "Serviço"} das {appt.StartTime} do dia {formattedDate} cancelado.";
            var barberJid = ResolveBarberWhatsAppJid(appt.Barber?.Phone, store?.ConnectedPhone);
            if (!string.IsNullOrWhiteSpace(barberJid))
            {
                await bridgeClient.SendTextAsync(barberJid, cancelMsg, ct);
            }
        }

        var activeInSlot = await db.Appointments.CountAsync(a =>
            a.BarberId == appt.BarberId &&
            a.AppointmentDate == appt.AppointmentDate &&
            a.StartTime == appt.StartTime &&
            a.Status != "Cancelled" && a.Status != "NoShow" && a.Status != "NoShow", ct);

        return new AppointmentResponse(
            appt.Id,
            appt.AppointmentDate,
            appt.StartTime,
            appt.CustomerId,
            appt.Customer?.Name ?? "Cliente",
            appt.Customer?.Phone ?? "",
            appt.BarberId,
            appt.Barber?.Name ?? "Profissional",
            appt.ServiceItemId,
            appt.ServiceItem?.Name ?? "Serviço",
            appt.PriceCharged,
            appt.ServiceItem?.ColorHex ?? "#FF6B00",
            appt.Status,
            appt.Source,
            appt.Notes,
            activeInSlot,
            appt.Barber?.MaxConcurrentPerSlot ?? 3,
            string.IsNullOrWhiteSpace(appt.PaymentMethod) ? "PIX" : appt.PaymentMethod);
    }

    public async Task<IReadOnlyList<string>> GetAvailableSlotsForBarberAsync(int barberId, string date, CancellationToken ct)
    {
        var store = await GetCurrentStoreAsync(false, ct);
        var isBlocked = await db.StoreNonWorkingDays.AnyAsync(n => n.StoreId == store.Id && n.BlockedDate == date, ct);
        if (isBlocked)
        {
            return [];
        }

        var barber = await db.Barbers.FirstOrDefaultAsync(b => b.Id == barberId && b.IsActive && b.StoreId == store.Id, ct);
        if (barber is null) return [];

        // Disponibilidade unificada (duração do serviço, almoço, bloqueios recorrentes e capacidade por horário)
        return await availability.GetFreeSlotsAsync(store.Id, barberId, date, null, ct);
    }

    public async Task<IReadOnlyList<CalendarDaySummaryResponse>> GetCalendarMonthSummaryAsync(int year, int month, CancellationToken ct)
    {
        var storeId = GetCurrentStoreId();
        var prefix = $"{year:D4}-{month:D2}-";
        var apptCounts = await db.Appointments
            .Where(a => a.StoreId == storeId && a.AppointmentDate.StartsWith(prefix) && a.Status != "Cancelled" && a.Status != "NoShow")
            .GroupBy(a => a.AppointmentDate)
            .Select(g => new { Date = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Date, x => x.Count, ct);

        var blockedDays = await db.StoreNonWorkingDays
            .Where(n => n.StoreId == storeId && n.BlockedDate.StartsWith(prefix))
            .ToDictionaryAsync(n => n.BlockedDate, n => n.Reason, ct);

        var daysInMonth = DateTime.DaysInMonth(year, month);
        var result = new List<CalendarDaySummaryResponse>(daysInMonth);

        for (int day = 1; day <= daysInMonth; day++)
        {
            var dateStr = $"{year:D4}-{month:D2}-{day:D2}";
            var isBlocked = blockedDays.TryGetValue(dateStr, out var reason);
            var totalAppts = apptCounts.GetValueOrDefault(dateStr, 0);

            result.Add(new CalendarDaySummaryResponse(
                dateStr,
                totalAppts,
                isBlocked,
                reason));
        }

        return result;
    }

    public async Task<CalendarDaySummaryResponse> ToggleNonWorkingDayAsync(ToggleNonWorkingDayRequest request, CancellationToken ct)
    {
        var store = await GetCurrentStoreAsync(false, ct);
        var existing = await db.StoreNonWorkingDays
            .AsTracking()
            .FirstOrDefaultAsync(n => n.StoreId == store.Id && n.BlockedDate == request.Date, ct);

        bool isNowBlocked;
        string? reason = null;

        if (existing is not null)
        {
            db.StoreNonWorkingDays.Remove(existing);
            isNowBlocked = false;
        }
        else
        {
            reason = string.IsNullOrWhiteSpace(request.Reason) ? "Dia de Não Funcionamento" : request.Reason.Trim();
            db.StoreNonWorkingDays.Add(new StoreNonWorkingDay
            {
                StoreId = store.Id,
                BlockedDate = request.Date,
                Reason = reason
            });
            isNowBlocked = true;
        }

        await db.SaveChangesAsync(ct);

        var totalAppts = await db.Appointments
            .CountAsync(a => a.StoreId == store.Id && a.AppointmentDate == request.Date && a.Status != "Cancelled" && a.Status != "NoShow", ct);

        return new CalendarDaySummaryResponse(request.Date, totalAppts, isNowBlocked, reason);
    }

    public async Task<IReadOnlyList<CustomerCrmResponse>> GetCrmCustomersAsync(CancellationToken ct)
    {
        var storeId = GetCurrentStoreId();
        var customers = await db.Customers
            .Where(c => c.StoreId == storeId)
            .OrderByDescending(c => c.TotalAppointments)
            .ThenBy(c => c.Name)
            .ToListAsync(ct);

        var evalCounts = await db.Evaluations
            .Where(e => e.StoreId == storeId)
            .GroupBy(e => e.CustomerId)
            .Select(g => new { CustomerId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CustomerId, x => x.Count, ct);

        return customers.Select(c => new CustomerCrmResponse(
            c.Id,
            c.Name,
            c.Phone,
            c.TotalAppointments,
            evalCounts.GetValueOrDefault(c.Id, 0),
            c.LastAppointmentAt)).ToList();
    }

    public async Task<IReadOnlyList<CustomerEvaluationDetailResponse>> GetCustomerEvaluationsAsync(int customerId, CancellationToken ct)
    {
        var scopeStoreId = GetCurrentStoreId();
        var evals = await db.Evaluations
            .Where(e => e.CustomerId == customerId && e.StoreId == scopeStoreId)
            .OrderByDescending(e => e.Id)
            .ToListAsync(ct);

        var appts = await db.Appointments
            .Include(a => a.Barber)
            .Include(a => a.ServiceItem)
            .Where(a => a.CustomerId == customerId && a.StoreId == scopeStoreId)
            .ToDictionaryAsync(a => a.Id, ct);

        var list = new List<CustomerEvaluationDetailResponse>(evals.Count);
        foreach (var e in evals)
        {
            appts.TryGetValue(e.AppointmentId, out var appt);
            var txtContent = await txtStorage.ReadEvaluationTextAsync(e, ct);
            list.Add(new CustomerEvaluationDetailResponse(
                e.Id,
                e.AppointmentId,
                appt?.AppointmentDate ?? e.CreatedAt.ToString("yyyy-MM-dd"),
                appt?.StartTime ?? "--:--",
                appt?.Barber?.Name ?? "Profissional",
                appt?.ServiceItem?.Name ?? "Serviço",
                e.TxtFileName,
                txtContent,
                e.ByteSize,
                e.IsTxtCompacted,
                e.Sentiment,
                e.CreatedAt));
        }

        return list;
    }

    public async Task<IReadOnlyList<AnonymousEvaluationResponse>> GetAnonymousEvaluationsAsync(CancellationToken ct)
    {
        var storeId = GetCurrentStoreId();
        var evals = await db.Evaluations
            .Where(e => e.StoreId == storeId)
            .OrderByDescending(e => e.Id)
            .ToListAsync(ct);

        var barbers = await db.Barbers.Where(b => b.StoreId == storeId).ToDictionaryAsync(b => b.Id, b => b.Name, ct);
        var services = await db.ServiceItems.Where(s => s.StoreId == storeId).ToDictionaryAsync(s => s.Id, s => s.Name, ct);
        var appts = await db.Appointments.Where(a => a.StoreId == storeId).ToDictionaryAsync(a => a.Id, a => a.AppointmentDate, ct);

        var result = new List<AnonymousEvaluationResponse>(evals.Count);
        foreach (var e in evals)
        {
            var txtContent = await txtStorage.ReadEvaluationTextAsync(e, ct);
            result.Add(new AnonymousEvaluationResponse(
                e.Id,
                appts.GetValueOrDefault(e.AppointmentId, e.CreatedAt.ToString("yyyy-MM-dd")),
                barbers.GetValueOrDefault(e.BarberId, "Profissional"),
                services.GetValueOrDefault(e.ServiceItemId, "Serviço"),
                e.TxtFileName,
                txtContent,
                e.ByteSize,
                e.IsTxtCompacted,
                e.Sentiment,
                e.CreatedAt));
        }

        return result;
    }

    public async Task<AnonymousEvaluationResponse?> UpdateEvaluationSentimentAsync(long evaluationId, string sentiment, CancellationToken ct)
    {
        var scopeStoreId = GetCurrentStoreId();
        var eval = await db.Evaluations.AsTracking().FirstOrDefaultAsync(e => e.Id == evaluationId && e.StoreId == scopeStoreId, ct);
        if (eval is null) return null;

        eval.Sentiment = sentiment is "Positive" or "Negative" ? sentiment : null;
        db.Evaluations.Update(eval);
        await db.SaveChangesAsync(ct);

        var barberName = (await db.Barbers.FirstOrDefaultAsync(b => b.Id == eval.BarberId, ct))?.Name ?? "Profissional";
        var serviceName = (await db.ServiceItems.FirstOrDefaultAsync(s => s.Id == eval.ServiceItemId, ct))?.Name ?? "Serviço";
        var apptDate = (await db.Appointments.FirstOrDefaultAsync(a => a.Id == eval.AppointmentId, ct))?.AppointmentDate
            ?? eval.CreatedAt.ToString("yyyy-MM-dd");
        var txtContent = await txtStorage.ReadEvaluationTextAsync(eval, ct);

        return new AnonymousEvaluationResponse(
            eval.Id,
            apptDate,
            barberName,
            serviceName,
            eval.TxtFileName,
            txtContent,
            eval.ByteSize,
            eval.IsTxtCompacted,
            eval.Sentiment,
            eval.CreatedAt);
    }

    public async Task<(string FileName, string Content)?> GetEvaluationTxtDownloadAsync(long evaluationId, CancellationToken ct)
    {
        var scopeStoreId = GetCurrentStoreId();
        var eval = await db.Evaluations.FirstOrDefaultAsync(e => e.Id == evaluationId && e.StoreId == scopeStoreId, ct);
        if (eval is null) return null;

        var content = await txtStorage.ReadEvaluationTextAsync(eval, ct);
        var fileName = string.IsNullOrWhiteSpace(eval.TxtFileName)
            ? $"avaliacao_{eval.Id}.txt"
            : eval.TxtFileName;

        return (fileName, content);
    }

    public async Task<EvaluationStorageStatsResponse> GetEvaluationStorageStatsAsync(CancellationToken ct)
    {
        var store = await GetCurrentStoreAsync(false, ct);
        var totalEvals = await db.Evaluations.CountAsync(e => e.StoreId == store.Id, ct);
        var compactedCount = await db.Evaluations.CountAsync(e => e.StoreId == store.Id && e.IsTxtCompacted, ct);

        return new EvaluationStorageStatsResponse(
            totalEvals,
            txtStorage.GetActiveTxtFilesCount(),
            compactedCount,
            txtStorage.GetActiveDiskUsageBytes(),
            store.EvaluationRetentionDays);
    }

    public async Task<EvaluationStorageStatsResponse> TriggerStorageCleanupAsync(CancellationToken ct)
    {
        var uncompacted = await db.Evaluations
            .AsTracking()
            .Where(e => !e.IsTxtCompacted)
            .ToListAsync(ct);

        await txtStorage.RunStorageOptimizationAsync(uncompacted, ct);
        db.Evaluations.UpdateRange(uncompacted);

        // Expurga eventos de webhook antigos (> 15 dias) para manter o banco enxuto
        var cutoff = DateTimeOffset.UtcNow.AddDays(-15);
        var allEvents = await db.InboxWebhookEvents.AsTracking().ToListAsync(ct);
        var oldEvents = allEvents.Where(e => e.ProcessedAtUtc < cutoff).ToList();
        if (oldEvents.Count > 0)
        {
            db.InboxWebhookEvents.RemoveRange(oldEvents);
        }

        await db.SaveChangesAsync(ct);
        return await GetEvaluationStorageStatsAsync(ct);
    }

    public async Task<DashboardReportsResponse> GetReportsAsync(CancellationToken ct)
    {
        var storeId = GetCurrentStoreId();
        var appts = await db.Appointments
            .Include(a => a.Barber)
            .Include(a => a.ServiceItem)
            .Where(a => a.StoreId == storeId && a.Status != "Cancelled" && a.Status != "NoShow")
            .ToListAsync(ct);

        var barbers = await db.Barbers.Where(b => b.StoreId == storeId).ToListAsync(ct);
        var services = await db.ServiceItems.Where(s => s.StoreId == storeId).ToListAsync(ct);
        var customersCount = await db.Customers.CountAsync(c => c.StoreId == storeId, ct);
        var evals = await db.Evaluations.Where(e => e.StoreId == storeId).ToListAsync(ct);

        var ptCulture = new CultureInfo("pt-BR");

        // 1. Dia mais cheio
        var byDate = appts
            .GroupBy(a => a.AppointmentDate)
            .OrderByDescending(g => g.Count())
            .FirstOrDefault();

        string busiestDayOfWeek = "Sábado";
        string busiestDateRecord = byDate?.Key ?? DateTime.Now.ToString("yyyy-MM-dd");
        int busiestDateCount = byDate?.Count() ?? 0;

        if (byDate is not null && DateOnly.TryParseExact(byDate.Key, "yyyy-MM-dd", out var parsedDate))
        {
            busiestDayOfWeek = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                ptCulture.DateTimeFormat.GetDayName(parsedDate.DayOfWeek));
        }

        // 2. Horário de pico
        var hourlyGroups = appts
            .GroupBy(a => a.StartTime)
            .Select(g => new PeakHourItem(g.Key, g.Count()))
            .OrderBy(x => x.TimeSlot)
            .ToList();

        var peakSlot = hourlyGroups.OrderByDescending(x => x.AppointmentCount).FirstOrDefault();

        // 3. Média de avaliações por cliente
        double avgEvalsPerCustomer = customersCount > 0
            ? Math.Round((double)evals.Count / customersCount, 2)
            : 0;

        // 4. Financeiro
        decimal totalConfirmedRevenue = appts.Sum(a => a.PriceCharged);
        decimal totalCompletedRevenue = appts.Where(a => a.Status == "Completed").Sum(a => a.PriceCharged);
        decimal averageTicket = appts.Count > 0 ? Math.Round(totalConfirmedRevenue / appts.Count, 2) : 0m;

        // 5. Desempenho por barbeiro
        var barberPerf = barbers.Select(b =>
        {
            var bAppts = appts.Where(a => a.BarberId == b.Id).ToList();
            var bCompleted = bAppts.Count(a => a.Status == "Completed");
            var bRev = bAppts.Sum(a => a.PriceCharged);
            var bEvals = evals.Count(e => e.BarberId == b.Id);
            var lunchInfo = !string.IsNullOrWhiteSpace(b.LunchStartTime) && !string.IsNullOrWhiteSpace(b.LunchEndTime)
                ? $" (Almoço {b.LunchStartTime}-{b.LunchEndTime})"
                : "";
            return new BarberPerformanceItem(
                b.Id,
                b.Name,
                $"{b.WorkStartTime} às {b.WorkEndTime}{lunchInfo}",
                bAppts.Count,
                bCompleted,
                bRev,
                bEvals);
        }).ToList();

        // 6. Receita por Serviço
        var serviceBreakdown = services.Select(s =>
        {
            var sAppts = appts.Where(a => a.ServiceItemId == s.Id).ToList();
            return new ServiceRevenueItem(
                s.Id,
                s.Name,
                s.ColorHex,
                sAppts.Count,
                sAppts.Sum(a => a.PriceCharged));
        }).ToList();

        // 7. Resumo por Meio de Pagamento (PIX, Cartão, Dinheiro)
        var paymentMethods = new[] { "PIX", "Cartão", "Dinheiro" };
        var paymentBreakdown = paymentMethods.Select(pm =>
        {
            var pmAppts = appts.Where(a => string.Equals(
                string.IsNullOrWhiteSpace(a.PaymentMethod) ? "PIX" : a.PaymentMethod,
                pm,
                StringComparison.OrdinalIgnoreCase)).ToList();
            return new PaymentMethodBreakdownItem(
                pm,
                pmAppts.Count,
                pmAppts.Sum(a => a.PriceCharged));
        }).ToList();

        return new DashboardReportsResponse(
            busiestDayOfWeek,
            busiestDateRecord,
            busiestDateCount,
            peakSlot?.TimeSlot ?? "14:00",
            peakSlot?.AppointmentCount ?? 0,
            avgEvalsPerCustomer,
            totalConfirmedRevenue,
            totalCompletedRevenue,
            averageTicket,
            appts.Count,
            barberPerf,
            hourlyGroups,
            serviceBreakdown,
            paymentBreakdown);
    }

    public async Task<byte[]> ExportExcelReportAsync(CancellationToken ct)
    {
        var appointments = await GetAppointmentsAsync(null, ct);
        var customers = await GetCrmCustomersAsync(ct);
        var evaluations = await GetAnonymousEvaluationsAsync(ct);
        var reports = await GetReportsAsync(ct);

        using var workbook = new XLWorkbook();

        // Aba 1: Resumo & Financeiro
        var wsSummary = workbook.Worksheets.Add("Resumo e Financeiro");
        wsSummary.Cell(1, 1).Value = "RELATÓRIO GERENCIAL - CLINIC HAIR";
        wsSummary.Cell(1, 1).Style.Font.Bold = true;
        wsSummary.Cell(3, 1).Value = "Indicador";
        wsSummary.Cell(3, 2).Value = "Valor";
        wsSummary.Range(3, 1, 3, 2).Style.Font.Bold = true;

        wsSummary.Cell(4, 1).Value = "Dia Mais Cheio";
        wsSummary.Cell(4, 2).Value = $"{reports.BusiestDayOfWeek} ({reports.BusiestDateRecord} - {reports.BusiestDateCount} agendamentos)";
        wsSummary.Cell(5, 1).Value = "Horário de Pico";
        wsSummary.Cell(5, 2).Value = $"{reports.PeakHourSlot} ({reports.PeakHourCount} agendamentos)";
        wsSummary.Cell(6, 1).Value = "Média de Avaliações por Cliente";
        wsSummary.Cell(6, 2).Value = reports.AverageEvaluationsPerCustomer;
        wsSummary.Cell(7, 1).Value = "Receita Total Agendada (R$)";
        wsSummary.Cell(7, 2).Value = reports.TotalConfirmedRevenue;
        wsSummary.Cell(8, 1).Value = "Receita Concluída (R$)";
        wsSummary.Cell(8, 2).Value = reports.TotalCompletedRevenue;
        wsSummary.Cell(9, 1).Value = "Ticket Médio (R$)";
        wsSummary.Cell(9, 2).Value = reports.AverageTicket;
        wsSummary.Columns().AdjustToContents();

        // Aba 2: Agenda Completa
        var wsAgenda = workbook.Worksheets.Add("Agenda");
        var headersAgenda = new[] { "ID", "Data", "Hora", "Cliente", "Telefone", "Profissional", "Serviço", "Valor (R$)", "Status", "Origem" };
        for (int i = 0; i < headersAgenda.Length; i++)
        {
            wsAgenda.Cell(1, i + 1).Value = headersAgenda[i];
            wsAgenda.Cell(1, i + 1).Style.Font.Bold = true;
        }
        int row = 2;
        foreach (var a in appointments)
        {
            wsAgenda.Cell(row, 1).Value = a.Id;
            wsAgenda.Cell(row, 2).Value = a.AppointmentDate;
            wsAgenda.Cell(row, 3).Value = a.StartTime;
            wsAgenda.Cell(row, 4).Value = a.CustomerName;
            wsAgenda.Cell(row, 5).Value = a.CustomerPhone;
            wsAgenda.Cell(row, 6).Value = a.BarberName;
            wsAgenda.Cell(row, 7).Value = a.ServiceName;
            wsAgenda.Cell(row, 8).Value = a.PriceCharged;
            wsAgenda.Cell(row, 9).Value = a.Status;
            wsAgenda.Cell(row, 10).Value = a.Source;
            row++;
        }
        wsAgenda.Columns().AdjustToContents();

        // Aba 3: Desempenho por Barbeiro
        var wsBarbers = workbook.Worksheets.Add("Desempenho Barbeiros");
        var headersBarber = new[] { "Profissional", "Expediente", "Total Agendamentos", "Concluídos", "Receita Gerada (R$)", "Avaliações Recebidas" };
        for (int i = 0; i < headersBarber.Length; i++)
        {
            wsBarbers.Cell(1, i + 1).Value = headersBarber[i];
            wsBarbers.Cell(1, i + 1).Style.Font.Bold = true;
        }
        row = 2;
        foreach (var bp in reports.BarberPerformances)
        {
            wsBarbers.Cell(row, 1).Value = bp.BarberName;
            wsBarbers.Cell(row, 2).Value = bp.WorkingHours;
            wsBarbers.Cell(row, 3).Value = bp.TotalAppointments;
            wsBarbers.Cell(row, 4).Value = bp.CompletedAppointments;
            wsBarbers.Cell(row, 5).Value = bp.RevenueGenerated;
            wsBarbers.Cell(row, 6).Value = bp.EvaluationsReceived;
            row++;
        }
        wsBarbers.Columns().AdjustToContents();

        // Aba 4: CRM Clientes
        var wsCrm = workbook.Worksheets.Add("CRM Clientes");
        var headersCrm = new[] { "ID", "Cliente", "Telefone", "Qtd Agendamentos", "Qtd Avaliações", "Último Agendamento" };
        for (int i = 0; i < headersCrm.Length; i++)
        {
            wsCrm.Cell(1, i + 1).Value = headersCrm[i];
            wsCrm.Cell(1, i + 1).Style.Font.Bold = true;
        }
        row = 2;
        foreach (var c in customers)
        {
            wsCrm.Cell(row, 1).Value = c.Id;
            wsCrm.Cell(row, 2).Value = c.Name;
            wsCrm.Cell(row, 3).Value = c.Phone;
            wsCrm.Cell(row, 4).Value = c.TotalAppointments;
            wsCrm.Cell(row, 5).Value = c.EvaluationCount;
            wsCrm.Cell(row, 6).Value = c.LastAppointmentAt.ToString("dd/MM/yyyy HH:mm");
            row++;
        }
        wsCrm.Columns().AdjustToContents();

        // Aba 5: Avaliações Anônimas (.txt)
        var wsEvals = workbook.Worksheets.Add("Avaliacoes Anonimas");
        var headersEval = new[] { "ID", "Data", "Profissional", "Serviço", "Arquivo .txt", "Conteúdo" };
        for (int i = 0; i < headersEval.Length; i++)
        {
            wsEvals.Cell(1, i + 1).Value = headersEval[i];
            wsEvals.Cell(1, i + 1).Style.Font.Bold = true;
        }
        row = 2;
        foreach (var ev in evaluations)
        {
            wsEvals.Cell(row, 1).Value = ev.Id;
            wsEvals.Cell(row, 2).Value = ev.AppointmentDate;
            wsEvals.Cell(row, 3).Value = ev.BarberName;
            wsEvals.Cell(row, 4).Value = ev.ServiceName;
            wsEvals.Cell(row, 5).Value = ev.TxtFileName;
            wsEvals.Cell(row, 6).Value = ev.ConsolidatedText;
            row++;
        }
        wsEvals.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        workbook.SaveAs(ms);
        return ms.ToArray();
    }

    public async Task<byte[]> ExportGoogleSheetsCsvAsync(CancellationToken ct)
    {
        var appointments = await GetAppointmentsAsync(null, ct);
        var reports = await GetReportsAsync(ct);

        var sb = new StringBuilder();
        sb.AppendLine("Secao,Data,Hora,Cliente,Telefone,Profissional,Servico,Valor_RS,Status,Origem");

        foreach (var a in appointments)
        {
            sb.AppendLine($"Agendamento,{EscapeCsv(a.AppointmentDate)},{EscapeCsv(a.StartTime)},{EscapeCsv(a.CustomerName)},{EscapeCsv(a.CustomerPhone)},{EscapeCsv(a.BarberName)},{EscapeCsv(a.ServiceName)},{a.PriceCharged.ToString("F2", CultureInfo.InvariantCulture)},{EscapeCsv(a.Status)},{EscapeCsv(a.Source)}");
        }

        sb.AppendLine();
        sb.AppendLine("Indicador,Detalhe,Valor");
        sb.AppendLine($"Dia Mais Cheio,{EscapeCsv(reports.BusiestDayOfWeek)},{reports.BusiestDateCount}");
        sb.AppendLine($"Horario de Pico,{EscapeCsv(reports.PeakHourSlot)},{reports.PeakHourCount}");
        sb.AppendLine($"Media Avaliacoes por Cliente,Geral,{reports.AverageEvaluationsPerCustomer.ToString(CultureInfo.InvariantCulture)}");
        sb.AppendLine($"Faturamento Total Confirmado,R$,{reports.TotalConfirmedRevenue.ToString("F2", CultureInfo.InvariantCulture)}");

        var preamble = Encoding.UTF8.GetPreamble();
        var body = Encoding.UTF8.GetBytes(sb.ToString());
        var combined = new byte[preamble.Length + body.Length];
        Buffer.BlockCopy(preamble, 0, combined, 0, preamble.Length);
        Buffer.BlockCopy(body, 0, combined, preamble.Length, body.Length);
        return combined;
    }

    public async Task<IReadOnlyList<BarberResponse>> GetBarbersAsync(CancellationToken ct)
    {
        var storeId = GetCurrentStoreId();
        var list = await db.Barbers.Where(b => b.StoreId == storeId).OrderBy(b => b.Id).ToListAsync(ct);
        return list.Select(b => new BarberResponse(
            b.Id,
            b.Name,
            b.Phone,
            b.Specialty,
            b.WorkStartTime,
            b.WorkEndTime,
            b.WorkingDaysCsv,
            b.MaxConcurrentPerSlot,
            b.IsActive,
            b.LunchStartTime,
            b.LunchEndTime)).ToList();
    }

    public async Task<BarberResponse> UpsertBarberAsync(int? id, UpsertBarberRequest request, CancellationToken ct)
    {
        var store = await GetCurrentStoreAsync(false, ct);
        Barber barber;

        if (id.HasValue && id.Value > 0)
        {
            barber = await db.Barbers.AsTracking().FirstOrDefaultAsync(b => b.Id == id.Value && b.StoreId == GetCurrentStoreId(), ct)
                ?? throw new KeyNotFoundException("Funcionário não encontrado.");
            barber.Name = request.Name.Trim();
            barber.Phone = request.Phone.Trim();
            barber.Specialty = request.Specialty.Trim();
            barber.WorkStartTime = request.WorkStartTime;
            barber.WorkEndTime = request.WorkEndTime;
            barber.LunchStartTime = string.IsNullOrWhiteSpace(request.LunchStartTime) ? null : request.LunchStartTime.Trim();
            barber.LunchEndTime = string.IsNullOrWhiteSpace(request.LunchEndTime) ? null : request.LunchEndTime.Trim();
            barber.WorkingDaysCsv = request.WorkingDaysCsv;
            barber.MaxConcurrentPerSlot = request.MaxConcurrentPerSlot;
            barber.IsActive = request.IsActive;
            db.Barbers.Update(barber);
        }
        else
        {
            // Limite de profissionais do plano SaaS da loja (item 74)
            var limits = ClinicHair.WebApi.Services.Features.SaasPlanLimits.For(store.SubscriptionPlan);
            int activeBarbers = await db.Barbers.CountAsync(b => b.StoreId == store.Id && b.IsActive, ct);
            if (activeBarbers >= limits.MaxBarbers)
            {
                throw new InvalidOperationException(
                    $"O plano {limits.Tier} permite até {limits.MaxBarbers} profissional(is) ativo(s). Fale com o suporte para mudar de plano.");
            }

            barber = new Barber
            {
                StoreId = store.Id,
                Name = request.Name.Trim(),
                Phone = request.Phone.Trim(),
                Specialty = request.Specialty.Trim(),
                WorkStartTime = request.WorkStartTime,
                WorkEndTime = request.WorkEndTime,
                LunchStartTime = string.IsNullOrWhiteSpace(request.LunchStartTime) ? null : request.LunchStartTime.Trim(),
                LunchEndTime = string.IsNullOrWhiteSpace(request.LunchEndTime) ? null : request.LunchEndTime.Trim(),
                WorkingDaysCsv = request.WorkingDaysCsv,
                MaxConcurrentPerSlot = request.MaxConcurrentPerSlot,
                IsActive = request.IsActive
            };
            db.Barbers.Add(barber);
        }

        await db.SaveChangesAsync(ct);
        return new BarberResponse(
            barber.Id,
            barber.Name,
            barber.Phone,
            barber.Specialty,
            barber.WorkStartTime,
            barber.WorkEndTime,
            barber.WorkingDaysCsv,
            barber.MaxConcurrentPerSlot,
            barber.IsActive,
            barber.LunchStartTime,
            barber.LunchEndTime);
    }

    public async Task<bool> DeleteBarberAsync(int id, CancellationToken ct)
    {
        var barber = await db.Barbers.AsTracking().FirstOrDefaultAsync(b => b.Id == id && b.StoreId == GetCurrentStoreId(), ct);
        if (barber is null) return false;

        var evals = await db.Evaluations.AsTracking().Where(e => e.BarberId == id).ToListAsync(ct);
        if (evals.Count > 0)
        {
            db.Evaluations.RemoveRange(evals);
        }

        var appts = await db.Appointments.AsTracking().Where(a => a.BarberId == id).ToListAsync(ct);
        if (appts.Count > 0)
        {
            db.Appointments.RemoveRange(appts);
        }

        db.Barbers.Remove(barber);
        audit.Stage("Loja", $"Excluiu o profissional '{barber.Name}' ({appts.Count} agendamento(s) removido(s)).");
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<IReadOnlyList<ServiceItemResponse>> GetServicesAsync(CancellationToken ct)
    {
        var storeId = GetCurrentStoreId();
        var list = await db.ServiceItems.Where(s => s.StoreId == storeId).OrderBy(s => s.DisplayOrder).ThenBy(s => s.Id).ToListAsync(ct);
        return list.Select(s => new ServiceItemResponse(
            s.Id, s.Name, s.Price, s.DurationMinutes, s.ColorHex, s.DisplayOrder, s.IsActive)).ToList();
    }

    public async Task<ServiceItemResponse> UpsertServiceAsync(int? id, UpsertServiceItemRequest request, CancellationToken ct)
    {
        var store = await GetCurrentStoreAsync(false, ct);
        ServiceItem item;

        if (id.HasValue && id.Value > 0)
        {
            item = await db.ServiceItems.AsTracking().FirstOrDefaultAsync(s => s.Id == id.Value && s.StoreId == GetCurrentStoreId(), ct)
                ?? throw new KeyNotFoundException("Serviço não encontrado.");
            item.Name = request.Name.Trim();
            item.Price = request.Price;
            item.DurationMinutes = request.DurationMinutes;
            item.ColorHex = request.ColorHex.ToUpperInvariant();
            item.DisplayOrder = request.DisplayOrder;
            item.IsActive = request.IsActive;
            db.ServiceItems.Update(item);
        }
        else
        {
            item = new ServiceItem
            {
                StoreId = store.Id,
                Name = request.Name.Trim(),
                Price = request.Price,
                DurationMinutes = request.DurationMinutes,
                ColorHex = request.ColorHex.ToUpperInvariant(),
                DisplayOrder = request.DisplayOrder,
                IsActive = request.IsActive
            };
            db.ServiceItems.Add(item);
        }

        await db.SaveChangesAsync(ct);
        return new ServiceItemResponse(
            item.Id, item.Name, item.Price, item.DurationMinutes, item.ColorHex, item.DisplayOrder, item.IsActive);
    }

    public async Task<bool> DeleteServiceAsync(int id, CancellationToken ct)
    {
        var item = await db.ServiceItems.AsTracking().FirstOrDefaultAsync(s => s.Id == id && s.StoreId == GetCurrentStoreId(), ct);
        if (item is null) return false;

        var evals = await db.Evaluations.AsTracking().Where(e => e.ServiceItemId == id).ToListAsync(ct);
        if (evals.Count > 0)
        {
            db.Evaluations.RemoveRange(evals);
        }

        var appts = await db.Appointments.AsTracking().Where(a => a.ServiceItemId == id).ToListAsync(ct);
        if (appts.Count > 0)
        {
            db.Appointments.RemoveRange(appts);
        }

        db.ServiceItems.Remove(item);
        audit.Stage("Loja", $"Excluiu o serviço '{item.Name}' ({appts.Count} agendamento(s) removido(s)).");
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<StoreSettingsResponse> GetStoreSettingsAsync(CancellationToken ct)
    {
        var s = await GetCurrentStoreAsync(false, ct);
        return MapStoreSettings(s);
    }

    public async Task<StoreSettingsResponse> UpdateStoreSettingsAsync(UpdateStoreSettingsRequest request, CancellationToken ct)
    {
        var s = await GetCurrentStoreAsync(true, ct);
        s.Name = request.Name.Trim();
        s.OpeningTime = request.OpeningTime;
        s.ClosingTime = request.ClosingTime;
        s.WorkingDaysCsv = request.WorkingDaysCsv;
        s.EvaluationRetentionDays = request.EvaluationRetentionDays;
        s.WelcomePollTitle = request.WelcomePollTitle;
        s.ServicePollTitle = request.ServicePollTitle;
        s.BarberPollTitle = request.BarberPollTitle;
        s.DatePollTitle = request.DatePollTitle;
        s.TimePollTitle = request.TimePollTitle;
        s.AskNameTodayTemplate = request.AskNameTodayTemplate;
        s.AskNameOtherDayTemplate = request.AskNameOtherDayTemplate;
        s.ConfirmationPollTemplate = request.ConfirmationPollTemplate;
        s.ConfirmedMessageTemplate = request.ConfirmedMessageTemplate;
        s.Reminder1hTemplate = request.Reminder1hTemplate;
        s.Evaluation2hPollTitle = request.Evaluation2hPollTitle;
        s.EvaluationWindowOpenMessage = request.EvaluationWindowOpenMessage;
        s.UpdatedAt = DateTimeOffset.UtcNow;

        db.Stores.Update(s);
        audit.Stage("Loja", "Alterou horário de funcionamento e/ou mensagens das enquetes.");
        await db.SaveChangesAsync(ct);
        return MapStoreSettings(s);
    }

    public async Task<WhatsAppBridgeStatusResponse> GetWhatsAppBridgeStatusAsync(CancellationToken ct)
    {
        var s = await GetCurrentStoreAsync(true, ct);

        // Consulta o processo TypeScript Baileys Bridge em tempo real (se estiver ativo na porta 3100)
        var liveBridge = await bridgeClient.GetBridgeLiveStatusAsync(ct);
        bool isBridgeReachable = liveBridge is not null;

        if (liveBridge is not null && !string.IsNullOrWhiteSpace(liveBridge.Status) && s.Id == 1)
        {
            s.WhatsAppStatus = liveBridge.Status;
            if (!string.IsNullOrWhiteSpace(liveBridge.ConnectedPhone))
            {
                s.ConnectedPhone = liveBridge.ConnectedPhone;
            }
            else if (!string.IsNullOrWhiteSpace(liveBridge.PairingPhone))
            {
                s.ConnectedPhone = liveBridge.PairingPhone;
            }
            else if (liveBridge.Status == "DISCONNECTED")
            {
                s.ConnectedPhone = null;
                s.LastQrCodeData = null;
                s.LastPairingCode = null;
            }

            if (!string.IsNullOrWhiteSpace(liveBridge.QrCodeBase64))
            {
                s.LastQrCodeData = liveBridge.QrCodeBase64;
            }
            else if (!string.IsNullOrWhiteSpace(liveBridge.QrCodeRaw))
            {
                s.LastQrCodeData = liveBridge.QrCodeRaw;
            }

            if (!string.IsNullOrWhiteSpace(liveBridge.PairingCode))
            {
                s.LastPairingCode = liveBridge.PairingCode;
            }

            s.UpdatedAt = DateTimeOffset.UtcNow;
            db.Stores.Update(s);

            if (!string.IsNullOrWhiteSpace(s.ConnectedPhone))
            {
                var primaryBarber = await db.Barbers.AsTracking().FirstOrDefaultAsync(b => b.Id == 1, ct);
                if (primaryBarber is not null && primaryBarber.Phone.Contains("99811-2200", StringComparison.Ordinal))
                {
                    primaryBarber.Phone = s.ConnectedPhone;
                    db.Barbers.Update(primaryBarber);
                }
            }

            await db.SaveChangesAsync(ct);
        }

        var services = await db.ServiceItems.Where(x => x.StoreId == s.Id && x.IsActive).OrderBy(x => x.DisplayOrder).ToListAsync(ct);
        var barbers = await db.Barbers.Where(x => x.StoreId == s.Id && x.IsActive).ToListAsync(ct);

        var flowPreview = new List<string>
        {
            $"1️⃣ Enquete Inicial: \"{s.WelcomePollTitle}\" [Agendamento | Encerrar]",
            $"2️⃣ Enquete Serviço: \"{s.ServicePollTitle}\" [{string.Join(" | ", services.Select(x => $"{x.Name} - R$ {x.Price:F2}"))} | Voltar | Encerrar]",
            barbers.Count > 1
                ? $"2️⃣B Enquete Funcionário: \"{s.BarberPollTitle}\" [{string.Join(" | ", barbers.Select(b => $"{b.Name} ({b.WorkStartTime} às {b.WorkEndTime})"))} | Agendar com qualquer um | Voltar | Encerrar]"
                : $"2️⃣B Funcionário Único Ativo: {barbers.FirstOrDefault()?.Name ?? "Profissional"}",
            $"3️⃣ Enquete Dia: \"{s.DatePollTitle}\" [Hoje | Amanhã | Dias Numerados | Voltar | Encerrar]",
            $"4️⃣ Enquete Hora (30 em 30 min, ocultando horários com >= 3 agendamentos para o funcionário): \"{s.TimePollTitle}\"",
            $"5️⃣ Coleta Nome: \"{s.AskNameTodayTemplate}\"",
            $"6️⃣ Enquete Confirmação: \"{s.ConfirmationPollTemplate}\" [Confirmar | Voltar | Cancelar]",
            $"7️⃣ Lembrete (1h antes) + Enquete de Avaliação (2h depois -> Janela de 1h para arquivo .txt)"
        };

        var bridgeUrl = config["WhatsAppBridge:BaseUrl"] ?? "http://127.0.0.1:3100";
        return new WhatsAppBridgeStatusResponse(
            s.WhatsAppStatus,
            s.ConnectedPhone,
            s.LastQrCodeData,
            s.LastPairingCode,
            bridgeUrl,
            isBridgeReachable || s.WhatsAppStatus is "CONNECTED" or "QR_READY" or "PAIRING_CODE_READY",
            s.UpdatedAt,
            flowPreview,
            s.BotPaused);
    }

    public async Task<WhatsAppBridgeStatusResponse> SetWhatsAppBotPausedAsync(bool paused, CancellationToken ct)
    {
        var store = await GetCurrentStoreAsync(true, ct);
        store.BotPaused = paused;
        store.UpdatedAt = DateTimeOffset.UtcNow;
        db.Stores.Update(store);
        audit.Stage("WhatsApp", paused ? "Pausou o bot do WhatsApp." : "Retomou o bot do WhatsApp.");
        await db.SaveChangesAsync(ct);

        realtime.Publish("whatsapp", paused ? "Bot do WhatsApp pausado." : "Bot do WhatsApp retomado.");
        return await GetWhatsAppBridgeStatusAsync(ct);
    }

    public async Task<WhatsAppBridgeStatusResponse> ControlWhatsAppBridgeAsync(string action, CancellationToken ct)
    {
        var s = await GetCurrentStoreAsync(true, ct);
        switch (action.ToLowerInvariant())
        {
            case "start":
            case "qrcode":
                var started = await bridgeClient.StartBridgeSessionAsync(ct);
                if (started is null)
                {
                    await Task.Delay(1800, ct);
                    started = await bridgeClient.StartBridgeSessionAsync(ct);
                }

                if (started is null || (string.IsNullOrWhiteSpace(started.QrCodeBase64) && string.IsNullOrWhiteSpace(started.QrCodeRaw) && started.Status != "CONNECTED"))
                {
                    throw new InvalidOperationException("O serviço WhatsApp Bridge está iniciando ou sem conexão de rede. Aguarde 3 segundos e tente novamente.");
                }

                s.WhatsAppStatus = started.Status == "CONNECTED" ? "CONNECTED" : "QR_READY";
                s.LastPairingCode = null;
                s.LastQrCodeData = !string.IsNullOrWhiteSpace(started.QrCodeBase64)
                    ? started.QrCodeBase64
                    : started.QrCodeRaw;
                break;
            case "disconnect":
                await bridgeClient.LogoutBridgeSessionAsync(ct);
                s.WhatsAppStatus = "DISCONNECTED";
                s.ConnectedPhone = null;
                s.LastQrCodeData = null;
                s.LastPairingCode = null;
                audit.Stage("WhatsApp", "Desconectou o número do WhatsApp da loja.");
                break;
        }
        s.UpdatedAt = DateTimeOffset.UtcNow;
        db.Stores.Update(s);
        await db.SaveChangesAsync(ct);
        return await GetWhatsAppBridgeStatusAsync(ct);
    }

    public async Task<WhatsAppBridgeStatusResponse> RequestWhatsAppPairingCodeAsync(string phoneNumber, CancellationToken ct)
    {
        var digits = new string((phoneNumber ?? "").Where(char.IsDigit).ToArray());
        if (digits.Length < 10)
        {
            throw new InvalidOperationException("Digite um número de WhatsApp válido com DDD (ex: 62982854050 ou 5562982854050).");
        }

        if (digits.Length is 10 or 11 && !digits.StartsWith("55", StringComparison.Ordinal))
        {
            digits = $"55{digits}";
        }

        var formattedPhone = digits.Length == 13
            ? $"+55 ({digits[2..4]}) {digits[4..9]}-{digits[9..]}"
            : digits.Length == 12
                ? $"+55 ({digits[2..4]}) {digits[4..8]}-{digits[8..]}"
                : $"+{digits}";

        var bridgeResult = await bridgeClient.RequestPairingCodeAsync(digits, ct);
        if (bridgeResult is null)
        {
            await Task.Delay(1800, ct);
            bridgeResult = await bridgeClient.RequestPairingCodeAsync(digits, ct);
        }

        if (bridgeResult is null || string.IsNullOrWhiteSpace(bridgeResult.PairingCode))
        {
            throw new InvalidOperationException("Não foi possível gerar o código de 8 dígitos junto ao WhatsApp Web. Verifique o número com DDD (ex: 5562982854050) e tente novamente.");
        }

        var s = await GetCurrentStoreAsync(true, ct);
        s.WhatsAppStatus = "PAIRING_CODE_READY";
        s.ConnectedPhone = bridgeResult.PairingPhone ?? formattedPhone;
        s.LastQrCodeData = null;
        s.LastPairingCode = bridgeResult.PairingCode;

        s.UpdatedAt = DateTimeOffset.UtcNow;
        db.Stores.Update(s);
        await db.SaveChangesAsync(ct);
        return await GetWhatsAppBridgeStatusAsync(ct);
    }

    private static StoreSettingsResponse MapStoreSettings(Store s) => new(
        s.Id,
        s.Name,
        s.OpeningTime,
        s.ClosingTime,
        s.WorkingDaysCsv,
        s.EvaluationRetentionDays,
        s.WelcomePollTitle,
        s.ServicePollTitle,
        s.BarberPollTitle,
        s.DatePollTitle,
        s.TimePollTitle,
        s.AskNameTodayTemplate,
        s.AskNameOtherDayTemplate,
        s.ConfirmationPollTemplate,
        s.ConfirmedMessageTemplate,
        s.Reminder1hTemplate,
        s.Evaluation2hPollTitle,
        s.EvaluationWindowOpenMessage);

    private static string EscapeCsv(string? val)
    {
        if (string.IsNullOrEmpty(val)) return "";
        if (val.Contains(',') || val.Contains('"') || val.Contains('\n'))
        {
            return $"\"{val.Replace("\"", "\"\"")}\"";
        }
        return val;
    }

    private static string? ResolveBarberWhatsAppJid(string? barberPhone, string? storeConnectedPhone)
    {
        var barberDigits = new string((barberPhone ?? "").Where(char.IsDigit).ToArray());
        if ((string.IsNullOrWhiteSpace(barberDigits) || barberDigits.EndsWith("11998112200", StringComparison.Ordinal))
            && !string.IsNullOrWhiteSpace(storeConnectedPhone))
        {
            var connectedDigits = new string(storeConnectedPhone.Where(char.IsDigit).ToArray());
            if (connectedDigits.Length >= 10)
            {
                barberDigits = connectedDigits;
            }
        }

        if (barberDigits.Length < 10)
        {
            return null;
        }

        if (barberDigits.Length is 10 or 11 && !barberDigits.StartsWith("55", StringComparison.Ordinal))
        {
            barberDigits = $"55{barberDigits}";
        }

        return $"{barberDigits}@s.whatsapp.net";
    }
}

using System.Security.Claims;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Services.Features;
using ClinicHair.WebApi.Services.Platform;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Endpoints;

/// <summary>Endpoints dos itens 57 a 100 do plano de melhorias (assinaturas, SuperAdmin, produto e página pública).</summary>
public static class ExtensionEndpoints
{
    public static IServiceCollection AddExtensionServices(this IServiceCollection services)
    {
        services.AddScoped<ISubscriptionLifecycle, SubscriptionLifecycle>();
        services.AddScoped<ILoyaltyService, LoyaltyService>();
        services.AddScoped<ICompletionHooks, CompletionHooks>();
        services.AddScoped<IStockService, StockService>();
        services.AddScoped<ISearchService, SearchService>();
        services.AddScoped<IExtensionSettingsService, ExtensionSettingsService>();
        services.AddScoped<IPublicBookingService, PublicBookingService>();
        services.AddScoped<ISaasService, SaasService>();
        return services;
    }

    public static void MapExtensionEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/dashboard").WithTags("Recursos 57-100").RequireAuthorization();

        // ------------------------------------------------ ASSINATURAS (57–65)
        var subs = api.MapGroup("/plans/subscriptions");
        subs.MapPost("/{id:int}/change-plan", async (int id, ChangePlanRequest r, ISubscriptionLifecycle s, CancellationToken ct) => Results.Ok(await s.ChangePlanAsync(id, r.NewPlanId, ct)));
        subs.MapPost("/{id:int}/members", async (int id, AddSubscriptionMemberRequest r, ISubscriptionLifecycle s, CancellationToken ct) => Results.Ok(await s.AddMemberAsync(id, r.CustomerId, ct)));
        subs.MapDelete("/{id:int}/members/{customerId:int}", async (int id, int customerId, ISubscriptionLifecycle s, CancellationToken ct) =>
            await s.RemoveMemberAsync(id, customerId, ct) ? Results.NoContent() : Results.NotFound());
        subs.MapGet("/{id:int}/pix", async (int id, ISubscriptionLifecycle s, CancellationToken ct) => Results.Ok(await s.GetPixChargeAsync(id, ct)));
        api.MapGet("/plans/report", async (ISubscriptionLifecycle s, CancellationToken ct) => Results.Ok(await s.GetReportAsync(ct)));

        // ------------------------------------------------ CONFIGURAÇÕES (PIX, fidelidade, página pública, idioma, marca)
        api.MapGet("/extensions/settings", async (IExtensionSettingsService s, CancellationToken ct) => Results.Ok(await s.GetAsync(ct)));
        api.MapPut("/extensions/settings", async (ExtensionSettingsDto r, IExtensionSettingsService s, CancellationToken ct) => Results.Ok(await s.SaveAsync(r, ct)));
        api.MapGet("/extensions/calendars", async (IExtensionSettingsService s, CancellationToken ct) => Results.Ok(await s.GetCalendarsAsync(ct)));
        api.MapPut("/extensions/calendars/{barberId:int}", async (int barberId, SetBarberCalendarRequest r, IExtensionSettingsService s, CancellationToken ct) =>
            Results.Ok(await s.SetCalendarAsync(barberId, r.IcsUrl, ct)));

        // ------------------------------------------------ FIDELIDADE (97)
        api.MapGet("/crm/customers/{id:int}/loyalty", async (int id, ILoyaltyService s, CancellationToken ct) => Results.Ok(await s.GetAsync(id, ct)));
        api.MapPost("/crm/customers/{id:int}/loyalty/redeem", async (int id, ILoyaltyService s, CancellationToken ct) => Results.Ok(await s.RedeemAsync(id, ct)));
        api.MapPost("/crm/customers/{id:int}/loyalty/adjust", async (int id, LoyaltyAdjustRequest r, ILoyaltyService s, CancellationToken ct) => Results.Ok(await s.AdjustAsync(id, r, ct)));

        // ------------------------------------------------ ESTOQUE (99)
        var stock = api.MapGroup("/stock");
        stock.MapGet("", async (IStockService s, CancellationToken ct) => Results.Ok(await s.GetItemsAsync(ct)));
        stock.MapPost("", async (UpsertStockItemRequest r, IStockService s, CancellationToken ct) => Results.Ok(await s.UpsertAsync(null, r, ct)));
        stock.MapPut("/{id:int}", async (int id, UpsertStockItemRequest r, IStockService s, CancellationToken ct) => Results.Ok(await s.UpsertAsync(id, r, ct)));
        stock.MapDelete("/{id:int}", async (int id, IStockService s, CancellationToken ct) => await s.DeleteAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        stock.MapPost("/{id:int}/move", async (int id, StockMovementRequest r, IStockService s, CancellationToken ct) => Results.Ok(await s.MoveAsync(id, r, ct)));
        stock.MapGet("/movements", async (int? itemId, IStockService s, CancellationToken ct) => Results.Ok(await s.GetMovementsAsync(itemId, ct)));

        // ------------------------------------------------ BUSCA GLOBAL (82)
        api.MapGet("/search", async (string? q, ISearchService s, CancellationToken ct) => Results.Ok(await s.SearchAsync(q, ct)));

        // ------------------------------------------------ SEGUNDO FATOR TOTP (68)
        var totp = app.MapGroup("/api/auth/totp").WithTags("Autenticação").RequireAuthorization();
        totp.MapGet("/status", async (ClaimsPrincipal user, ClinicHairDbContext db, CancellationToken ct) =>
        {
            var u = await FindUserAsync(user, db, false, ct);
            return u is null ? Results.NotFound() : Results.Ok(new { enabled = u.TotpEnabled, role = u.Role });
        });
        totp.MapPost("/setup", async (ClaimsPrincipal user, ClinicHairDbContext db, CancellationToken ct) =>
        {
            var u = await FindUserAsync(user, db, true, ct);
            if (u is null) return Results.NotFound();
            if (u.TotpEnabled) return Results.Conflict(new { detail = "O segundo fator já está ativo. Desative antes de gerar outro." });
            u.TotpSecret = Totp.NewSecret();
            await db.SaveChangesAsync(ct);
            return Results.Ok(new TotpSetupDto(u.TotpSecret, Totp.OtpAuthUri(u.TotpSecret, u.Username)));
        });
        totp.MapPost("/enable", async (TotpCodeRequest r, ClaimsPrincipal user, ClinicHairDbContext db, CancellationToken ct) =>
        {
            var u = await FindUserAsync(user, db, true, ct);
            if (u is null) return Results.NotFound();
            if (!Totp.Verify(u.TotpSecret, r.Code)) return Results.BadRequest(new { detail = "Código inválido. Confira o horário do celular e tente de novo." });
            u.TotpEnabled = true;
            db.SystemLogEntries.Add(new SystemLogEntry { Level = "SECURITY", Category = "Auth", StoreId = u.StoreId, Message = $"Segundo fator (TOTP) ativado para '{u.Username}'.", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { enabled = true });
        });
        totp.MapPost("/disable", async (TotpCodeRequest r, ClaimsPrincipal user, ClinicHairDbContext db, CancellationToken ct) =>
        {
            var u = await FindUserAsync(user, db, true, ct);
            if (u is null) return Results.NotFound();
            if (!u.TotpEnabled || !Totp.Verify(u.TotpSecret, r.Code)) return Results.BadRequest(new { detail = "Código inválido." });
            u.TotpEnabled = false;
            u.TotpSecret = null;
            db.SystemLogEntries.Add(new SystemLogEntry { Level = "SECURITY", Category = "Auth", StoreId = u.StoreId, Message = $"Segundo fator (TOTP) desativado para '{u.Username}'.", CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { enabled = false });
        });

        // ------------------------------------------------ SUPERADMIN (69, 71–76)
        var sa = app.MapGroup("/api/superadmin").WithTags("SuperAdmin").RequireAuthorization("SuperAdminOnly");
        sa.MapPost("/support-session", async (SupportSessionRequest r, ClinicHairDbContext db, ICurrentStore current, CancellationToken ct) =>
        {
            var reason = (r.Reason ?? "").Trim();
            if (reason.Length < 5) return Results.BadRequest(new { detail = "Descreva o motivo do acesso (mínimo 5 caracteres)." });
            var store = await db.Stores.AsNoTracking().FirstOrDefaultAsync(s => s.Id == r.StoreId, ct);
            if (store is null) return Results.NotFound();
            db.SystemLogEntries.Add(new SystemLogEntry
            {
                Level = "SUPERADMIN", Category = "Modo suporte", StoreId = store.Id, StoreName = store.Name,
                Message = $"[{current.Username}] Entrou no painel da loja. Motivo: {reason[..Math.Min(300, reason.Length)]}",
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            return Results.Ok(new { storeId = store.Id, storeName = store.Name });
        });
        sa.MapGet("/signups", async (string? status, ISaasService s, CancellationToken ct) => Results.Ok(await s.GetSignupsAsync(status, ct)));
        sa.MapPost("/signups/{id:int}/approve", async (int id, ApproveSignupRequest r, ISaasService s, CancellationToken ct) => Results.Ok(await s.ApproveAsync(id, r, ct)));
        sa.MapPost("/signups/{id:int}/reject", async (int id, RejectSignupRequest r, ISaasService s, CancellationToken ct) =>
            await s.RejectAsync(id, r.Notes, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        sa.MapPost("/billing/run", async (ISaasService s, IConfiguration cfg, CancellationToken ct) =>
        {
            int charges = await s.GenerateDueChargesAsync(ct);
            int changed = await s.ApplyOverduePolicyAsync(cfg.GetValue("SaaS:AutoSuspendEnabled", false), cfg.GetValue("SaaS:SuspendAfterDays", 10), ct);
            return Results.Ok(new { chargesCreated = charges, storesUpdated = changed });
        });
        sa.MapGet("/plan-limits", () => Results.Ok(new[] { "Básico", "Pro", "Premium" }.Select(SaasPlanLimits.For)));

        // ------------------------------------------------ PÚBLICO (66, 71, 94, 96) — anônimo e com limite de requisições
        var pub = app.MapGroup("/api/public").WithTags("Público").AllowAnonymous().RequireRateLimiting("public-limit");
        pub.MapGet("/stores/{slug}", async (string slug, IPublicBookingService s, CancellationToken ct) =>
            await s.GetStoreAsync(slug, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        pub.MapGet("/stores/{slug}/slots", async (string slug, int? barberId, string? date, int? serviceId, IPublicBookingService s, CancellationToken ct) =>
            await s.GetSlotsAsync(slug, barberId ?? 0, date ?? "", serviceId ?? 0, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        pub.MapPost("/stores/{slug}/book", async (string slug, PublicBookingRequest r, IPublicBookingService s, CancellationToken ct) => Results.Ok(await s.BookAsync(slug, r, ct)));
        pub.MapPost("/signup", async (PublicSignupRequest r, IPublicBookingService s, CancellationToken ct) =>
        {
            await s.SignupAsync(r, ct);
            return Results.Accepted();
        });

        // Páginas HTML públicas: /p/{slug} (planos + agendamento) e /cadastro (onboarding)
        app.MapGet("/p/{slug}", (string slug, IWebHostEnvironment env) =>
            Results.File(Path.Combine(env.WebRootPath, "publico.html"), "text/html; charset=utf-8")).ExcludeFromDescription();
        app.MapGet("/cadastro", (IWebHostEnvironment env) =>
            Results.File(Path.Combine(env.WebRootPath, "publico.html"), "text/html; charset=utf-8")).ExcludeFromDescription();
    }

    private static async Task<AdminUser?> FindUserAsync(ClaimsPrincipal user, ClinicHairDbContext db, bool tracking, CancellationToken ct)
    {
        if (!int.TryParse(user.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id)) return null;
        var q = tracking ? db.AdminUsers.AsTracking() : db.AdminUsers.AsNoTracking();
        return await q.FirstOrDefaultAsync(u => u.Id == id, ct);
    }
}

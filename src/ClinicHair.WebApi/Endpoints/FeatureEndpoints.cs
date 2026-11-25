using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Services.Features;
using ClinicHair.WebApi.Services.Platform;

namespace ClinicHair.WebApi.Endpoints;

/// <summary>Endpoints dos recursos do plano de melhorias (itens 11 a 56). Todos exigem login e ficam presos à loja do token.</summary>
public static class FeatureEndpoints
{
    public static IServiceCollection AddFeatureServices(this IServiceCollection services)
    {
        services.AddScoped<IAvailabilityService, AvailabilityService>();
        services.AddScoped<IWaitlistNotifier, WaitlistNotifier>();
        services.AddScoped<IBotAdminService, BotAdminService>();
        services.AddScoped<IAgendaService, AgendaService>();
        services.AddScoped<ICrmService, CrmService>();
        services.AddScoped<IReportsService, ReportsService>();
        services.AddScoped<ISubscriptionPlanService, SubscriptionPlanService>();
        return services;
    }

    public static void MapFeatureEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/dashboard").WithTags("Recursos").RequireAuthorization();

        // ------------------------------------------------ BOT / WHATSAPP
        var bot = api.MapGroup("/bot");
        bot.MapGet("/settings", async (IBotAdminService s, CancellationToken ct) => Results.Ok(await s.GetSettingsAsync(ct)));
        bot.MapPut("/settings", async (BotAutomationSettingsDto r, IBotAdminService s, CancellationToken ct) => Results.Ok(await s.SaveSettingsAsync(r, ct)));
        bot.MapGet("/conversations", async (string? filter, IBotAdminService s, CancellationToken ct) => Results.Ok(await s.GetConversationsAsync(filter, ct)));
        bot.MapGet("/conversations/{phone}", async (string phone, IBotAdminService s, CancellationToken ct) =>
            await s.GetConversationAsync(phone, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        bot.MapPost("/conversations/{phone}/takeover", async (string phone, TakeoverRequest r, IBotAdminService s, CancellationToken ct) =>
            await s.SetTakeoverAsync(phone, r, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        bot.MapPost("/conversations/{phone}/resolve", async (string phone, IBotAdminService s, CancellationToken ct) =>
            await s.ResolveAsync(phone, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        bot.MapPost("/conversations/{phone}/reply", async (string phone, AgentReplyRequest r, IBotAdminService s, CancellationToken ct) =>
            await s.ReplyAsync(phone, r.Text, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        bot.MapGet("/quick-replies", async (IBotAdminService s, CancellationToken ct) => Results.Ok(await s.GetQuickRepliesAsync(ct)));
        bot.MapPost("/quick-replies", async (UpsertQuickReplyRequest r, IBotAdminService s, CancellationToken ct) => Results.Ok(await s.UpsertQuickReplyAsync(null, r, ct)));
        bot.MapPut("/quick-replies/{id:int}", async (int id, UpsertQuickReplyRequest r, IBotAdminService s, CancellationToken ct) => Results.Ok(await s.UpsertQuickReplyAsync(id, r, ct)));
        bot.MapDelete("/quick-replies/{id:int}", async (int id, IBotAdminService s, CancellationToken ct) =>
            await s.DeleteQuickReplyAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        bot.MapGet("/health", async (IBotAdminService s, CancellationToken ct) => Results.Ok(await s.GetHealthAsync(ct)));

        // ------------------------------------------------ AGENDA
        var agenda = api.MapGroup("/agenda");
        agenda.MapGet("/board", async (string? date, IAgendaService s, CancellationToken ct) => Results.Ok(await s.GetBoardAsync(date, ct)));
        agenda.MapPost("/appointments/{id:long}/move", async (long id, MoveAppointmentRequest r, IAgendaService s, CancellationToken ct) => Results.Ok(await s.MoveAsync(id, r, ct)));
        agenda.MapPost("/appointments/{id:long}/stage", async (long id, AppointmentStageRequest r, IAgendaService s, CancellationToken ct) => Results.Ok(await s.SetStageAsync(id, r, ct)));
        agenda.MapGet("/blocks", async (IAgendaService s, CancellationToken ct) => Results.Ok(await s.GetBlocksAsync(ct)));
        agenda.MapPost("/blocks", async (UpsertRecurringBlockRequest r, IAgendaService s, CancellationToken ct) => Results.Ok(await s.CreateBlockAsync(r, ct)));
        agenda.MapDelete("/blocks/{id:int}", async (int id, IAgendaService s, CancellationToken ct) =>
            await s.DeleteBlockAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        agenda.MapGet("/waitlist", async (string? date, IAgendaService s, CancellationToken ct) => Results.Ok(await s.GetWaitlistAsync(date, ct)));
        agenda.MapPost("/waitlist", async (CreateWaitlistEntryRequest r, IAgendaService s, CancellationToken ct) => Results.Ok(await s.AddWaitlistAsync(r, ct)));
        agenda.MapPatch("/waitlist/{id:int}", async (int id, UpdateWaitlistStatusRequest r, IAgendaService s, CancellationToken ct) =>
            await s.UpdateWaitlistStatusAsync(id, r.Status, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        agenda.MapDelete("/waitlist/{id:int}", async (int id, IAgendaService s, CancellationToken ct) =>
            await s.DeleteWaitlistAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        agenda.MapGet("/service-times", async (IAgendaService s, CancellationToken ct) => Results.Ok(await s.GetServiceTimesAsync(ct)));

        // ------------------------------------------------ CRM
        var crm = api.MapGroup("/crm");
        crm.MapGet("/customers/{id:int}/profile", async (int id, ICrmService s, CancellationToken ct) => Results.Ok(await s.GetProfileAsync(id, ct)));
        crm.MapPut("/customers/{id:int}", async (int id, UpdateCustomerRequest r, ICrmService s, CancellationToken ct) => Results.Ok(await s.UpdateCustomerAsync(id, r, ct)));
        crm.MapPost("/customers/{id:int}/anonymize", async (int id, AnonymizeCustomerRequest r, ICrmService s, IAccountService accounts, ICurrentStore current, CancellationToken ct) =>
        {
            // Ação irreversível: exige confirmação recente da senha
            if (!accounts.ValidateExportTicket(r.Ticket, current.UserId)) return Results.Forbid();
            await s.AnonymizeAsync(id, ct);
            return Results.NoContent();
        });
        crm.MapGet("/inactive", async (int? days, ICrmService s, CancellationToken ct) => Results.Ok(await s.GetInactiveAsync(days ?? 60, ct)));
        crm.MapPost("/segments/preview", async (SegmentFilter f, ICrmService s, CancellationToken ct) => Results.Ok(await s.PreviewSegmentAsync(f, ct)));
        crm.MapGet("/campaigns", async (ICrmService s, CancellationToken ct) => Results.Ok(await s.GetCampaignsAsync(ct)));
        crm.MapPost("/campaigns", async (CreateCampaignRequest r, ICrmService s, CancellationToken ct) => Results.Ok(await s.CreateCampaignAsync(r, ct)));
        crm.MapPost("/campaigns/{id:int}/cancel", async (int id, ICrmService s, CancellationToken ct) =>
            await s.CancelCampaignAsync(id, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        crm.MapGet("/duplicates", async (ICrmService s, CancellationToken ct) => Results.Ok(await s.GetDuplicatesAsync(ct)));
        crm.MapPost("/merge", async (MergeCustomersRequest r, ICrmService s, CancellationToken ct) => Results.Ok(await s.MergeAsync(r, ct)));
        crm.MapGet("/evaluations/trend", async (ICrmService s, CancellationToken ct) => Results.Ok(await s.GetEvaluationTrendAsync(ct)));

        // ------------------------------------------------ RELATÓRIOS
        var reports = api.MapGroup("/reports");
        reports.MapGet("/overview", async (string? from, string? to, IReportsService s, CancellationToken ct) => Results.Ok(await s.GetOverviewAsync(from, to, ct)));
        reports.MapGet("/cash", async (string? date, IReportsService s, CancellationToken ct) => Results.Ok(await s.GetCashAsync(date, ct)));
        reports.MapPost("/cash", async (SaveCashClosingRequest r, IReportsService s, CancellationToken ct) => Results.Ok(await s.SaveCashAsync(r, ct)));
        reports.MapGet("/export/period", async (string? from, string? to, string? ticket, IReportsService s, IAccountService accounts, ICurrentStore current, CancellationToken ct) =>
        {
            if (!accounts.ValidateExportTicket(ticket, current.UserId)) return Results.Forbid();
            var (bytes, name) = await s.ExportPeriodCsvAsync(from, to, ct);
            return Results.File(bytes, "text/csv; charset=utf-8", name);
        });

        // ------------------------------------------------ PLANOS DE ASSINATURA DO CLIENTE
        var plans = api.MapGroup("/plans");
        plans.MapGet("", async (ISubscriptionPlanService s, CancellationToken ct) => Results.Ok(await s.GetPlansAsync(ct)));
        plans.MapPost("", async (UpsertCustomerPlanRequest r, ISubscriptionPlanService s, CancellationToken ct) => Results.Ok(await s.UpsertPlanAsync(null, r, ct)));
        plans.MapPut("/{id:int}", async (int id, UpsertCustomerPlanRequest r, ISubscriptionPlanService s, CancellationToken ct) => Results.Ok(await s.UpsertPlanAsync(id, r, ct)));
        plans.MapDelete("/{id:int}", async (int id, ISubscriptionPlanService s, CancellationToken ct) =>
            await s.DeletePlanAsync(id, ct) ? Results.NoContent() : Results.NotFound());
        plans.MapGet("/summary", async (ISubscriptionPlanService s, CancellationToken ct) => Results.Ok(await s.GetSummaryAsync(ct)));
        plans.MapGet("/subscriptions", async (string? status, ISubscriptionPlanService s, CancellationToken ct) => Results.Ok(await s.GetSubscriptionsAsync(status, ct)));
        plans.MapPost("/subscriptions", async (CreateSubscriptionRequest r, ISubscriptionPlanService s, CancellationToken ct) => Results.Ok(await s.SubscribeAsync(r, ct)));
        plans.MapPost("/subscriptions/{id:int}/status", async (int id, UpdateSubscriptionStatusRequest r, ISubscriptionPlanService s, CancellationToken ct) =>
            await s.SetStatusAsync(id, r.Status, ct) is { } d ? Results.Ok(d) : Results.NotFound());
        plans.MapPost("/subscriptions/{id:int}/renew", async (int id, ISubscriptionPlanService s, CancellationToken ct) =>
            await s.RenewAsync(id, ct) is { } d ? Results.Ok(d) : Results.NotFound());
    }
}

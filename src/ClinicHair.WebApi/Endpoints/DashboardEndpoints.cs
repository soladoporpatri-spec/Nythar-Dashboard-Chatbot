using System.Text;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Services;
using ClinicHair.WebApi.Services.Platform;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ClinicHair.WebApi.Endpoints;

/// <summary>
/// Mapeamento dos endpoints Minimal API da Dashboard Clinic Hair seguindo as boas práticas do .NET 10 LTS.
/// </summary>
public static class DashboardEndpoints
{
    public static void MapDashboardEndpoints(this WebApplication app)
    {
        var authGroup = app.MapGroup("/api/auth").WithTags("Auth");

        authGroup.MapPost("/login", async Task<Results<Ok<AuthResponse>, UnauthorizedHttpResult>> (
            LoginRequest request,
            HttpContext httpContext,
            IAccountService accounts,
            IWebHostEnvironment env,
            CancellationToken ct) =>
        {
            var result = await accounts.LoginAsync(request, ct);
            if (result is null)
            {
                return TypedResults.Unauthorized();
            }

            AppendAuthCookie(httpContext, env, result);
            return TypedResults.Ok(result);
        })
        .WithName("LoginAdmin")
        .WithSummary("Autentica o administrador na Dashboard Clinic Hair")
        .WithDescription("Valida credenciais com bloqueio após tentativas incorretas e emite JWT + Cookie HttpOnly.")
        .RequireRateLimiting("login-limit");

        authGroup.MapPost("/logout", async (HttpContext httpContext, IAccountService accounts, CancellationToken ct) =>
        {
            if (httpContext.User.Identity?.IsAuthenticated == true)
            {
                await accounts.LogoutAsync(httpContext.User, ct);
            }
            httpContext.Response.Cookies.Delete("ClinicHairAuth");
            return TypedResults.NoContent();
        })
        .WithName("LogoutAdmin")
        .WithSummary("Encerra a sessão atual e invalida o token no servidor");

        authGroup.MapPost("/change-password", async Task<Ok<AuthResponse>> (
            ChangePasswordRequest request,
            HttpContext httpContext,
            IAccountService accounts,
            ICurrentStore current,
            IWebHostEnvironment env,
            CancellationToken ct) =>
        {
            var userId = current.UserId ?? throw new UnauthorizedAccessException("Sessão inválida.");
            var result = await accounts.ChangePasswordAsync(userId, request, ct);
            AppendAuthCookie(httpContext, env, result);
            return TypedResults.Ok(result);
        })
        .RequireAuthorization()
        .RequireRateLimiting("login-limit")
        .WithName("ChangePassword")
        .WithSummary("Troca a senha do usuário logado (encerra as outras sessões)");

        authGroup.MapPost("/reauth", async Task<Ok<ReauthResponse>> (
            ReauthRequest request,
            IAccountService accounts,
            ICurrentStore current,
            CancellationToken ct) =>
        {
            var userId = current.UserId ?? throw new UnauthorizedAccessException("Sessão inválida.");
            return TypedResults.Ok(await accounts.ReauthenticateAsync(userId, request.Password, ct));
        })
        .RequireAuthorization()
        .RequireRateLimiting("login-limit")
        .WithName("Reauthenticate")
        .WithSummary("Confirma a senha e emite um ticket de 5 minutos para ações sensíveis (exportações)");

        // Grupo autenticado da Dashboard
        var api = app.MapGroup("/api/dashboard")
            .WithTags("Dashboard")
            .RequireAuthorization();

        // 1. AGENDA & CALENDÁRIO
        api.MapGet("/appointments", async Task<Ok<IReadOnlyList<AppointmentResponse>>> (
            string? date,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var list = await service.GetAppointmentsAsync(date, ct);
            return TypedResults.Ok(list);
        })
        .WithName("GetAppointments")
        .WithSummary("Lista os agendamentos para a sub-aba Agenda");

        api.MapPost("/appointments", async Task<Created<AppointmentResponse>> (
            CreateManualAppointmentRequest request,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var created = await service.CreateManualAppointmentAsync(request, ct);
            return TypedResults.Created($"/api/dashboard/appointments/{created.Id}", created);
        })
        .WithName("CreateManualAppointment")
        .WithSummary("Cria um agendamento manual respeitando expediente e limite de 3 por hora");

        api.MapPatch("/appointments/{id:long}/status", async Task<Results<Ok<AppointmentResponse>, NotFound>> (
            long id,
            UpdateAppointmentStatusRequest request,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var updated = await service.UpdateAppointmentStatusAsync(id, request.Status, request.PaymentMethod, ct);
            return updated is null ? TypedResults.NotFound() : TypedResults.Ok(updated);
        })
        .WithName("UpdateAppointmentStatus")
        .WithSummary("Atualiza o status de um agendamento (Confirmed, Completed, Cancelled)");

        api.MapGet("/barbers/{barberId:int}/available-slots", async Task<Ok<IReadOnlyList<string>>> (
            int barberId,
            string date,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var slots = await service.GetAvailableSlotsForBarberAsync(barberId, date, ct);
            return TypedResults.Ok(slots);
        })
        .WithName("GetAvailableSlots")
        .WithSummary("Retorna horários de 30 em 30 min disponíveis (< 3 agendamentos na mesma hora)");

        api.MapGet("/calendar", async Task<Ok<IReadOnlyList<CalendarDaySummaryResponse>>> (
            int year,
            int month,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var summary = await service.GetCalendarMonthSummaryAsync(year, month, ct);
            return TypedResults.Ok(summary);
        })
        .WithName("GetCalendarSummary")
        .WithSummary("Retorna resumo mensal para a sub-aba Calendário (dias vermelhos e dias amarelos)");

        api.MapPost("/calendar/non-working-day", async Task<Ok<CalendarDaySummaryResponse>> (
            ToggleNonWorkingDayRequest request,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var updated = await service.ToggleNonWorkingDayAsync(request, ct);
            return TypedResults.Ok(updated);
        })
        .WithName("ToggleNonWorkingDay")
        .WithSummary("Marca ou desmarca um dia de não funcionamento (cor amarela no calendário)");

        // 2. CRM & AVALIAÇÕES (.txt)
        api.MapGet("/crm/customers", async Task<Ok<IReadOnlyList<CustomerCrmResponse>>> (
            IDashboardService service,
            CancellationToken ct) =>
        {
            var list = await service.GetCrmCustomersAsync(ct);
            return TypedResults.Ok(list);
        })
        .WithName("GetCrmCustomers")
        .WithSummary("Lista clientes no CRM com contagem de agendamentos e avaliações");

        api.MapGet("/crm/customers/{customerId:int}/evaluations", async Task<Ok<IReadOnlyList<CustomerEvaluationDetailResponse>>> (
            int customerId,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var list = await service.GetCustomerEvaluationsAsync(customerId, ct);
            return TypedResults.Ok(list);
        })
        .WithName("GetCustomerEvaluations")
        .WithSummary("Exibe as avaliações específicas de um cliente (botão azul escuro no card do CRM)");

        api.MapGet("/crm/evaluations-anonymous", async Task<Ok<IReadOnlyList<AnonymousEvaluationResponse>>> (
            IDashboardService service,
            CancellationToken ct) =>
        {
            var list = await service.GetAnonymousEvaluationsAsync(ct);
            return TypedResults.Ok(list);
        })
        .WithName("GetAnonymousEvaluations")
        .WithSummary("Lista todas as avaliações na sub-aba Avaliações sem identificar o cliente");

        api.MapPatch("/crm/evaluations/{evaluationId:long}/sentiment", async Task<Results<Ok<AnonymousEvaluationResponse>, NotFound>> (
            long evaluationId,
            UpdateEvaluationSentimentRequest request,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var updated = await service.UpdateEvaluationSentimentAsync(evaluationId, request.Sentiment, ct);
            return updated is null ? TypedResults.NotFound() : TypedResults.Ok(updated);
        })
        .WithName("UpdateEvaluationSentiment")
        .WithSummary("Classifica uma avaliação como Positivo ou Negativo");

        api.MapGet("/crm/evaluations/{evaluationId:long}/txt", async Task<Results<FileContentHttpResult, NotFound>> (
            long evaluationId,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var file = await service.GetEvaluationTxtDownloadAsync(evaluationId, ct);
            if (file is null) return TypedResults.NotFound();

            var bytes = Encoding.UTF8.GetBytes(file.Value.Content);
            return TypedResults.File(bytes, "text/plain; charset=utf-8", file.Value.FileName);
        })
        .WithName("DownloadEvaluationTxt")
        .WithSummary("Baixa o arquivo .txt original da avaliação");

        api.MapGet("/crm/evaluations/storage-stats", async Task<Ok<EvaluationStorageStatsResponse>> (
            IDashboardService service,
            CancellationToken ct) =>
        {
            var stats = await service.GetEvaluationStorageStatsAsync(ct);
            return TypedResults.Ok(stats);
        })
        .WithName("GetEvaluationStorageStats")
        .WithSummary("Retorna estatísticas de espaço em disco dos arquivos .txt de avaliação");

        api.MapPost("/crm/evaluations/cleanup", async Task<Ok<EvaluationStorageStatsResponse>> (
            IDashboardService service,
            CancellationToken ct) =>
        {
            var stats = await service.TriggerStorageCleanupAsync(ct);
            return TypedResults.Ok(stats);
        })
        .WithName("TriggerStorageCleanup")
        .WithSummary("Executa compactação GZip e limpeza de arquivos .txt para otimização de espaço");

        // 3. RELATÓRIOS & EXPORTAÇÃO (EXCEL .XLSX E GOOGLE SHEETS .CSV)
        api.MapGet("/reports", async Task<Ok<DashboardReportsResponse>> (
            IDashboardService service,
            CancellationToken ct) =>
        {
            var reports = await service.GetReportsAsync(ct);
            return TypedResults.Ok(reports);
        })
        .WithName("GetDashboardReports")
        .WithSummary("Retorna indicadores de Dia mais cheio, Horário de pico, Desempenho por barbeiro, Avaliações e Financeiro");

        api.MapGet("/reports/export/excel", async Task<Results<FileContentHttpResult, ForbidHttpResult>> (
            string? ticket,
            IDashboardService service,
            IAccountService accounts,
            ICurrentStore current,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (!accounts.ValidateExportTicket(ticket, current.UserId)) return TypedResults.Forbid();
            await audit.LogAsync("Exportação", "Exportou o relatório completo em Excel.", ct);
            var bytes = await service.ExportExcelReportAsync(ct);
            var fileName = $"ClinicHair_Relatorio_{DateTime.Now:yyyyMMdd_HHmm}.xlsx";
            return TypedResults.File(
                bytes,
                "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                fileName);
        })
        .WithName("ExportReportsExcel")
        .WithSummary("Exporta todos os dados da barbearia como planilha Excel (.xlsx)");

        api.MapGet("/reports/export/google-sheets", async Task<Results<FileContentHttpResult, ForbidHttpResult>> (
            string? ticket,
            IDashboardService service,
            IAccountService accounts,
            ICurrentStore current,
            IAuditLogger audit,
            CancellationToken ct) =>
        {
            if (!accounts.ValidateExportTicket(ticket, current.UserId)) return TypedResults.Forbid();
            await audit.LogAsync("Exportação", "Exportou o relatório completo em CSV (Google Sheets).", ct);
            var bytes = await service.ExportGoogleSheetsCsvAsync(ct);
            var fileName = $"ClinicHair_GoogleSheets_{DateTime.Now:yyyyMMdd_HHmm}.csv";
            return TypedResults.File(bytes, "text/csv; charset=utf-8", fileName);
        })
        .WithName("ExportReportsGoogleSheets")
        .WithSummary("Exporta todos os dados formatados para importação direta no Google Sheets (.csv UTF-8)");

        // 4. LOJA (FUNCIONÁRIOS, PRODUTOS/SERVIÇOS, CONFIGURAÇÕES DA LOJA)
        api.MapGet("/store/barbers", async Task<Ok<IReadOnlyList<BarberResponse>>> (
            IDashboardService service,
            CancellationToken ct) =>
        {
            var list = await service.GetBarbersAsync(ct);
            return TypedResults.Ok(list);
        })
        .WithName("GetBarbers")
        .WithSummary("Lista todos os funcionários registrados na loja e seus horários");

        api.MapPost("/store/barbers", async Task<Created<BarberResponse>> (
            UpsertBarberRequest request,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var created = await service.UpsertBarberAsync(null, request, ct);
            return TypedResults.Created($"/api/dashboard/store/barbers/{created.Id}", created);
        })
        .WithName("CreateBarber")
        .WithSummary("Registra um novo funcionário com horário de trabalho individual e limite por hora");

        api.MapPut("/store/barbers/{id:int}", async Task<Ok<BarberResponse>> (
            int id,
            UpsertBarberRequest request,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var updated = await service.UpsertBarberAsync(id, request, ct);
            return TypedResults.Ok(updated);
        })
        .WithName("UpdateBarber")
        .WithSummary("Atualiza o cadastro e horário de trabalho de um funcionário");

        api.MapDelete("/store/barbers/{id:int}", async Task<Results<NoContent, NotFound>> (
            int id,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var removed = await service.DeleteBarberAsync(id, ct);
            return removed ? TypedResults.NoContent() : TypedResults.NotFound();
        })
        .WithName("DeleteBarber")
        .WithSummary("Remove ou desativa um funcionário");

        api.MapGet("/store/services", async Task<Ok<IReadOnlyList<ServiceItemResponse>>> (
            IDashboardService service,
            CancellationToken ct) =>
        {
            var list = await service.GetServicesAsync(ct);
            return TypedResults.Ok(list);
        })
        .WithName("GetServices")
        .WithSummary("Lista os produtos e serviços que aparecem nas enquetes e na agenda");

        api.MapPost("/store/services", async Task<Created<ServiceItemResponse>> (
            UpsertServiceItemRequest request,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var created = await service.UpsertServiceAsync(null, request, ct);
            return TypedResults.Created($"/api/dashboard/store/services/{created.Id}", created);
        })
        .WithName("CreateService")
        .WithSummary("Cadastra um novo serviço/produto com preço em R$ e cor de gradiente");

        api.MapPut("/store/services/{id:int}", async Task<Ok<ServiceItemResponse>> (
            int id,
            UpsertServiceItemRequest request,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var updated = await service.UpsertServiceAsync(id, request, ct);
            return TypedResults.Ok(updated);
        })
        .WithName("UpdateService")
        .WithSummary("Atualiza preço, nome ou cor de gradiente de um serviço/produto");

        api.MapDelete("/store/services/{id:int}", async Task<Results<NoContent, NotFound>> (
            int id,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var removed = await service.DeleteServiceAsync(id, ct);
            return removed ? TypedResults.NoContent() : TypedResults.NotFound();
        })
        .WithName("DeleteService")
        .WithSummary("Remove ou desativa um serviço/produto");

        api.MapGet("/store/settings", async Task<Ok<StoreSettingsResponse>> (
            IDashboardService service,
            CancellationToken ct) =>
        {
            var settings = await service.GetStoreSettingsAsync(ct);
            return TypedResults.Ok(settings);
        })
        .WithName("GetStoreSettings")
        .WithSummary("Obtém horário de funcionamento geral e mensagens das enquetes");

        api.MapPut("/store/settings", async Task<Ok<StoreSettingsResponse>> (
            UpdateStoreSettingsRequest request,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var updated = await service.UpdateStoreSettingsAsync(request, ct);
            return TypedResults.Ok(updated);
        })
        .WithName("UpdateStoreSettings")
        .WithSummary("Salva horário de funcionamento geral e textos personalizados das enquetes");

        // 5. WHATSAPP BRIDGE STATUS & CONTROLE
        api.MapGet("/whatsapp/status", async Task<Ok<WhatsAppBridgeStatusResponse>> (
            IDashboardService service,
            CancellationToken ct) =>
        {
            var status = await service.GetWhatsAppBridgeStatusAsync(ct);
            return TypedResults.Ok(status);
        })
        .WithName("GetWhatsAppStatus")
        .WithSummary("Obtém status de conexão do WhatsApp Bridge e prévia do fluxo de enquetes");

        api.MapPost("/whatsapp/control/{action}", async Task<Ok<WhatsAppBridgeStatusResponse>> (
            string action,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var status = await service.ControlWhatsAppBridgeAsync(action, ct);
            return TypedResults.Ok(status);
        })
        .WithName("ControlWhatsAppBridge")
        .WithSummary("Liga o Bridge, gera QR Code real ou desconecta o número");

        api.MapPost("/whatsapp/bot/pause", async Task<Ok<WhatsAppBridgeStatusResponse>> (
            SetWhatsAppBotPausedRequest request,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var status = await service.SetWhatsAppBotPausedAsync(request.Paused, ct);
            return TypedResults.Ok(status);
        })
        .WithName("SetWhatsAppBotPaused")
        .WithSummary("Pausa ou retoma o bot de atendimento automático do WhatsApp");

        api.MapPost("/whatsapp/pairing-code", async Task<Ok<WhatsAppBridgeStatusResponse>> (
            RequestWhatsAppPairingCodeDto request,
            IDashboardService service,
            CancellationToken ct) =>
        {
            var status = await service.RequestWhatsAppPairingCodeAsync(request.PhoneNumber, ct);
            return TypedResults.Ok(status);
        })
        .WithName("RequestWhatsAppPairingCode")
        .WithSummary("Gera o Código de Pareamento oficial de 8 dígitos para vincular um número digitado");

        // 6. NOTIFICAÇÕES & STREAM DE EVENTOS EM TEMPO REAL (SSE) PARA DASHBOARD E APARELHO
        api.MapGet("/notifications", (
            long? afterId,
            IDashboardRealtimeNotifier notifier) =>
        {
            var list = notifier.GetRecentEvents(afterId ?? 0, 25);
            return TypedResults.Ok(list);
        })
        .WithName("GetRecentDashboardNotifications")
        .WithSummary("Retorna o histórico recente de notificações para a Dashboard e PWA mobile");

        api.MapPost("/notifications/test", (IDashboardRealtimeNotifier notifier) =>
        {
            var todayBr = DateTime.Now.ToString("dd/MM/yyyy");
            var evt = notifier.Publish(
                "agenda",
                $"Rafael Mendonça agendou Corte às 14:30 do dia {todayBr}.",
                "Alerta de Agendamento • Clinic Hair");
            return TypedResults.Ok(evt);
        })
        .WithName("TriggerTestDashboardNotification")
        .WithSummary("Dispara uma notificação de teste visual, sonora e no dispositivo");

        api.MapGet("/realtime/events", async (
            HttpContext httpContext,
            IDashboardRealtimeNotifier notifier,
            CancellationToken ct) =>
        {
            httpContext.Response.Headers.ContentType = "text/event-stream";
            httpContext.Response.Headers.CacheControl = "no-cache";
            httpContext.Response.Headers.Connection = "keep-alive";
            await httpContext.Response.Body.FlushAsync(ct);

            await foreach (var evt in notifier.SubscribeAsync(ct))
            {
                var json = System.Text.Json.JsonSerializer.Serialize(new
                {
                    id = evt.Id,
                    topic = evt.Topic,
                    title = evt.Title,
                    summary = evt.Summary,
                    timestamp = evt.Timestamp
                });
                await httpContext.Response.WriteAsync($"data: {json}\n\n", ct);
                await httpContext.Response.Body.FlushAsync(ct);
            }
        })
        .WithName("StreamDashboardRealtimeEvents")
        .WithSummary("Stream Server-Sent Events (SSE) para atualizar Agenda, Avaliações e WhatsApp em tempo real");

        // 7. SUPERADMINISTRADOR (PATRICIO) — GESTÃO DE LOJAS, COBRANÇAS, FINANCEIRO GLOBAL E LOGS
        // Política estrita: apenas tokens com Role = SuperAdmin acessam. Lojas comuns recebem 403.
        var superGroup = app.MapGroup("/api/superadmin")
            .WithTags("SuperAdmin")
            .RequireAuthorization("SuperAdminOnly");

        superGroup.MapGet("/overview", async Task<Ok<SuperAdminOverviewResponse>> (
            ISuperAdminService superAdmin,
            CancellationToken ct) =>
        {
            var overview = await superAdmin.GetOverviewAsync(ct);
            return TypedResults.Ok(overview);
        })
        .WithName("GetSuperAdminOverview")
        .WithSummary("Retorna visão geral de todas as lojas, resumo financeiro SaaS/GMV, cobranças e logs recentes");

        superGroup.MapPost("/stores", async Task<Created<SuperAdminStoreItemDto>> (
            CreateStoreWithPreconfigRequest request,
            ISuperAdminService superAdmin,
            CancellationToken ct) =>
        {
            var created = await superAdmin.CreateStoreWithPreconfigAsync(request, ct);
            return TypedResults.Created($"/api/superadmin/stores/{created.Id}", created);
        })
        .WithName("CreateStoreWithPreconfig")
        .WithSummary("Cria uma nova loja já com login Admin, barbeiro inicial, assinatura e pré-configuração entregue");

        superGroup.MapPost("/stores/{storeId:int}/preconfig", async Task<Results<Ok<SuperAdminStoreItemDto>, NotFound>> (
            int storeId,
            ApplyStorePreconfigRequest request,
            ISuperAdminService superAdmin,
            CancellationToken ct) =>
        {
            var updated = await superAdmin.ApplyPreconfigToStoreAsync(storeId, request, ct);
            return updated is null ? TypedResults.NotFound() : TypedResults.Ok(updated);
        })
        .WithName("ApplyStorePreconfig")
        .WithSummary("Entrega/Aplica kit de pré-configuração (serviços R$ 30/30/50/15 e enquetes) para uma loja existente");

        superGroup.MapPut("/stores/{storeId:int}", async Task<Results<Ok<SuperAdminStoreItemDto>, NotFound>> (
            int storeId,
            UpdateStoreSuperAdminRequest request,
            ISuperAdminService superAdmin,
            CancellationToken ct) =>
        {
            var updated = await superAdmin.UpdateStoreAsync(storeId, request, ct);
            return updated is null ? TypedResults.NotFound() : TypedResults.Ok(updated);
        })
        .WithName("UpdateStoreSuperAdmin")
        .WithSummary("Atualiza plano, mensalidade, vencimento, status ativo/suspenso ou senha Admin de uma loja");

        superGroup.MapDelete("/stores/{storeId:int}", async Task<Results<NoContent, NotFound>> (
            int storeId,
            ISuperAdminService superAdmin,
            CancellationToken ct) =>
        {
            var deleted = await superAdmin.DeleteStoreAsync(storeId, ct);
            return deleted ? TypedResults.NoContent() : TypedResults.NotFound();
        })
        .WithName("DeleteStoreSuperAdmin")
        .WithSummary("Remove uma loja secundária e seus dados vinculados");

        superGroup.MapGet("/billing", async Task<Ok<IReadOnlyList<StoreBillingRecordDto>>> (
            int? storeId,
            ISuperAdminService superAdmin,
            CancellationToken ct) =>
        {
            var records = await superAdmin.GetBillingRecordsAsync(storeId, ct);
            return TypedResults.Ok(records);
        })
        .WithName("GetSuperAdminBillingRecords")
        .WithSummary("Lista todas as cobranças de assinatura das lojas");

        superGroup.MapPost("/billing/charge", async Task<Created<StoreBillingRecordDto>> (
            CreateStoreBillingChargeRequest request,
            ISuperAdminService superAdmin,
            CancellationToken ct) =>
        {
            var created = await superAdmin.CreateBillingChargeAsync(request, ct);
            return TypedResults.Created($"/api/superadmin/billing/{created.Id}", created);
        })
        .WithName("CreateStoreBillingCharge")
        .WithSummary("Gera cobrança de assinatura para uma loja e envia aviso opcional via WhatsApp");

        superGroup.MapPost("/billing/{billingId:long}/pay", async Task<Results<Ok<StoreBillingRecordDto>, NotFound>> (
            long billingId,
            ISuperAdminService superAdmin,
            CancellationToken ct) =>
        {
            var paid = await superAdmin.MarkBillingPaidAsync(billingId, ct);
            return paid is null ? TypedResults.NotFound() : TypedResults.Ok(paid);
        })
        .WithName("MarkStoreBillingPaid")
        .WithSummary("Confirma pagamento de assinatura, renova vencimento (+30 dias) e reativa a loja");

        superGroup.MapGet("/logs", async Task<Ok<IReadOnlyList<SystemLogEntryDto>>> (
            string? level,
            int? storeId,
            string? search,
            int? limit,
            ISuperAdminService superAdmin,
            CancellationToken ct) =>
        {
            var logs = await superAdmin.GetSystemLogsAsync(level, storeId, search, limit ?? 150, ct);
            return TypedResults.Ok(logs);
        })
        .WithName("GetSuperAdminSystemLogs")
        .WithSummary("Retorna logs diretos do sistema filtrados por nível, loja ou busca textual");

        superGroup.MapPost("/logs/clear", async Task<Ok<object>> (
            ISuperAdminService superAdmin,
            CancellationToken ct) =>
        {
            await superAdmin.ClearLogsAsync(ct);
            return TypedResults.Ok((object)new { cleared = true });
        })
        .WithName("ClearSuperAdminSystemLogs")
        .WithSummary("Limpa o histórico de logs do sistema e registra auditoria");

        superGroup.MapGet("/logs/export", async Task<FileContentHttpResult> (
            ISuperAdminService superAdmin,
            CancellationToken ct) =>
        {
            var bytes = await superAdmin.ExportLogsTxtAsync(ct);
            var fileName = $"ClinicHair_SystemLogs_{DateTime.Now:yyyyMMdd_HHmm}.log";
            return TypedResults.File(bytes, "text/plain; charset=utf-8", fileName);
        })
        .WithName("ExportSuperAdminSystemLogs")
        .WithSummary("Exporta todos os logs do sistema em arquivo .log/.txt");
    }

    internal static void AppendAuthCookie(HttpContext httpContext, IWebHostEnvironment env, AuthResponse result)
    {
        httpContext.Response.Cookies.Append("ClinicHairAuth", result.Token, new CookieOptions
        {
            HttpOnly = true,
            SameSite = SameSiteMode.Strict,
            // Em produção o cookie só trafega por HTTPS; em desenvolvimento local (http://localhost) segue o protocolo da requisição
            Secure = httpContext.Request.IsHttps || !env.IsDevelopment(),
            Expires = result.ExpiresAt
        });
    }
}

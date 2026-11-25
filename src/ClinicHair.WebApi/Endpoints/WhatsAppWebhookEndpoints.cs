using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using ClinicHair.WebApi.Services;
using Microsoft.AspNetCore.Http.HttpResults;

namespace ClinicHair.WebApi.Endpoints;

/// <summary>
/// Endpoints de integração com o TypeScript Baileys Bridge (Webhook autenticado via HMAC-SHA256)
/// e endpoints do Simulador Interativo de Enquetes para a Dashboard.
/// </summary>
public static class WhatsAppWebhookEndpoints
{
    public static void MapWhatsAppWebhookEndpoints(this IEndpointRouteBuilder app)
    {
        // 1. WEBHOOK RECEBIDO DO TYPESCRIPT BAILEYS BRIDGE
        app.MapPost("/api/whatsapp/webhook", async Task<Results<Ok<object>, UnauthorizedHttpResult, BadRequest<string>>> (
            HttpContext httpContext,
            IConfiguration config,
            IWhatsAppOrchestratorService orchestrator,
            ClinicHairDbContext db,
            CancellationToken ct) =>
        {
            var sharedSecret = config["WhatsAppBridge:SharedSecret"]
                ?? throw new InvalidOperationException("WhatsAppBridge:SharedSecret não configurado.");

            httpContext.Request.EnableBuffering();
            using var reader = new StreamReader(httpContext.Request.Body, Encoding.UTF8, leaveOpen: true);
            var rawBody = await reader.ReadToEndAsync(ct);
            httpContext.Request.Body.Position = 0;

            var headerSecret = httpContext.Request.Headers["X-Bridge-Secret"].FirstOrDefault();
            var headerSignature = httpContext.Request.Headers["X-Webhook-Signature"].FirstOrDefault();

            bool isAuthorized = false;
            if (!string.IsNullOrWhiteSpace(headerSecret) &&
                CryptographicOperations.FixedTimeEquals(
                    Encoding.UTF8.GetBytes(headerSecret),
                    Encoding.UTF8.GetBytes(sharedSecret)))
            {
                isAuthorized = true;
            }
            else if (!string.IsNullOrWhiteSpace(headerSignature))
            {
                var expectedHmac = ComputeHmacSha256Hex(rawBody, sharedSecret);
                if (CryptographicOperations.FixedTimeEquals(
                        Encoding.UTF8.GetBytes(headerSignature.ToLowerInvariant()),
                        Encoding.UTF8.GetBytes(expectedHmac)))
                {
                    isAuthorized = true;
                }
            }

            if (!isAuthorized)
            {
                return TypedResults.Unauthorized();
            }

            WhatsAppWebhookEventPayload? payload;
            try
            {
                payload = JsonSerializer.Deserialize<WhatsAppWebhookEventPayload>(
                    rawBody,
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch (Exception ex)
            {
                return TypedResults.BadRequest($"Payload JSON inválido: {ex.Message}");
            }

            if (payload is null || string.IsNullOrWhiteSpace(payload.EventId))
            {
                return TypedResults.BadRequest("EventId obrigatório.");
            }

            try
            {
                await orchestrator.ProcessWebhookEventAsync(payload, ct);
            }
            catch (DbUpdateException)
            {
                // Corrida de idempotência: o mesmo evento chegou duas vezes ao mesmo tempo e o outro já gravou.
                // Se o EventId já está registrado, é duplicata inofensiva (200); caso contrário, falha real (500 -> bridge reenvia).
                db.ChangeTracker.Clear();
                var alreadyStored = await db.InboxWebhookEvents.AnyAsync(e => e.EventId == payload.EventId, ct);
                if (!alreadyStored)
                {
                    throw;
                }
                return TypedResults.Ok<object>(new { processed = false, duplicate = true, eventId = payload.EventId });
            }

            return TypedResults.Ok<object>(new { processed = true, eventId = payload.EventId });
        })
        .WithTags("WhatsApp Bridge Webhook")
        .WithName("ReceiveWhatsAppWebhook")
        .WithSummary("Recebe eventos de QR Code, Status, Votos de Enquete e Mensagens do Bridge TypeScript Baileys");

        // 2. ENDPOINTS DE SIMULAÇÃO E TESTE DO FLUXO DE ENQUETES NA DASHBOARD
        var simGroup = app.MapGroup("/api/dashboard/whatsapp/simulator")
            .RequireAuthorization()
            .WithTags("WhatsApp Bot Simulator");

        simGroup.MapGet("/{phone}", async Task<Ok<BotSimulationStateResponse>> (
            string phone,
            IWhatsAppOrchestratorService orchestrator,
            CancellationToken ct) =>
        {
            var state = await orchestrator.GetSimulationStateAsync(phone, ct);
            return TypedResults.Ok(state);
        })
        .WithName("GetWhatsAppBotSimulationState")
        .WithSummary("Obtém o estado atual da sessão de enquetes de um telefone");

        simGroup.MapPost("/", async Task<Ok<BotSimulationStateResponse>> (
            BotSimulationRequest request,
            IWhatsAppOrchestratorService orchestrator,
            CancellationToken ct) =>
        {
            var state = await orchestrator.ExecuteSimulationActionAsync(request, ct);
            return TypedResults.Ok(state);
        })
        .WithName("ExecuteWhatsAppBotSimulationAction")
        .WithSummary("Executa uma ação no fluxo de enquetes (enviar mensagem, votar na enquete, testar lembrete 1h ou avaliação 2h)");
    }

    private static string ComputeHmacSha256Hex(string data, string key)
    {
        var keyBytes = Encoding.UTF8.GetBytes(key);
        var dataBytes = Encoding.UTF8.GetBytes(data);
        var hash = HMACSHA256.HashData(keyBytes, dataBytes);
        return Convert.ToHexStringLower(hash);
    }
}

using System.Net.Http.Json;
using System.Text.Json;
using ClinicHair.WebApi.Contracts;

namespace ClinicHair.WebApi.Services;

/// <summary>
/// Cliente HTTP resiliente para comunicação entre o Backend C# (.NET 10) e o Microservice TypeScript (@whiskeysockets/baileys).
/// </summary>
public interface IWhatsAppBridgeClient
{
    Task<BridgeSendMessageResponse> SendPollAsync(
        string recipientJid,
        string question,
        IReadOnlyList<string> options,
        CancellationToken ct = default);

    Task<BridgeSendMessageResponse> SendTextAsync(
        string recipientJid,
        string text,
        CancellationToken ct = default);

    Task<BridgeLiveStatusDto?> GetBridgeLiveStatusAsync(CancellationToken ct = default);
    Task<BridgeLiveStatusDto?> StartBridgeSessionAsync(CancellationToken ct = default);
    Task<BridgeLiveStatusDto?> RequestPairingCodeAsync(string phoneNumber, CancellationToken ct = default);
    Task<bool> LogoutBridgeSessionAsync(CancellationToken ct = default);
}

/// <summary>
/// Status retornado pelo processo TypeScript Baileys Bridge.
/// </summary>
public sealed record BridgeLiveStatusDto(
    string Status,
    string? ConnectedPhone,
    string? QrCodeBase64,
    string? QrCodeRaw,
    string? PairingCode,
    string? PairingPhone);

/// <summary>
/// Implementação do cliente HTTP para o TypeScript Baileys Bridge com suporte a QR Code e Pairing Code de 8 dígitos.
/// </summary>
public sealed class WhatsAppBridgeClient(
    HttpClient httpClient,
    IConfiguration config,
    ILogger<WhatsAppBridgeClient> logger) : IWhatsAppBridgeClient
{
    private readonly string _sharedSecret = config["WhatsAppBridge:SharedSecret"]
        ?? throw new InvalidOperationException("WhatsAppBridge:SharedSecret não configurado.");

    public async Task<BridgeSendMessageResponse> SendPollAsync(
        string recipientJid,
        string question,
        IReadOnlyList<string> options,
        CancellationToken ct = default)
    {
        var fallbackId = $"poll_{Guid.NewGuid():N}"[..24];
        var body = await PostWithRetryAsync(
            "/bridge/messages/poll",
            new BridgeSendPollRequest(recipientJid, question, options, 1),
            idempotencyKey: Guid.NewGuid().ToString("N"),
            ct);

        return body ?? new BridgeSendMessageResponse(fallbackId, false);
    }

    public async Task<BridgeSendMessageResponse> SendTextAsync(
        string recipientJid,
        string text,
        CancellationToken ct = default)
    {
        var fallbackId = $"msg_{Guid.NewGuid():N}"[..24];
        var body = await PostWithRetryAsync(
            "/bridge/messages/text",
            new BridgeSendTextRequest(recipientJid, text),
            idempotencyKey: Guid.NewGuid().ToString("N"),
            ct);

        return body ?? new BridgeSendMessageResponse(fallbackId, false);
    }

    /// <summary>
    /// Envia ao bridge com até 3 tentativas (backoff 300ms/900ms) reutilizando a MESMA Idempotency-Key.
    /// O bridge devolve o resultado original se a primeira tentativa já tinha sido processada,
    /// então um timeout nunca causa mensagem duplicada no WhatsApp.
    /// Só repete em falha de rede/timeout/5xx; erros 4xx não são repetidos.
    /// </summary>
    private async Task<BridgeSendMessageResponse?> PostWithRetryAsync<TRequest>(
        string path,
        TRequest payload,
        string idempotencyKey,
        CancellationToken ct)
    {
        const int maxAttempts = 3;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, path);
                req.Headers.Add("X-Bridge-Secret", _sharedSecret);
                req.Headers.Add("Idempotency-Key", idempotencyKey);
                req.Content = JsonContent.Create(payload);

                using var resp = await httpClient.SendAsync(req, ct);
                if (resp.IsSuccessStatusCode)
                {
                    var body = await resp.Content.ReadFromJsonAsync<BridgeSendMessageResponse>(
                        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
                    if (body is not null && !string.IsNullOrWhiteSpace(body.MessageId))
                    {
                        return body;
                    }
                    return null;
                }

                if ((int)resp.StatusCode < 500)
                {
                    logger.LogWarning("Bridge recusou {Path} com status {Status}; sem novas tentativas.", path, (int)resp.StatusCode);
                    return null;
                }

                logger.LogWarning("Bridge respondeu {Status} em {Path} (tentativa {Attempt}/{Max}).", (int)resp.StatusCode, path, attempt, maxAttempts);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return null;
            }
            catch (HttpRequestException ex)
            {
                // Conexão recusada = bridge fora do ar: o supervisor religa; não adianta insistir (e evita travar o simulador).
                logger.LogDebug(ex, "Bridge offline em {Path}; envio não entregue.", path);
                return null;
            }
            catch (Exception ex)
            {
                // Timeout (TaskCanceledException sem cancelamento do chamador): repete com a mesma Idempotency-Key
                logger.LogDebug(ex, "Timeout do bridge em {Path} (tentativa {Attempt}/{Max}).", path, attempt, maxAttempts);
            }

            if (attempt < maxAttempts)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * attempt * attempt), ct);
                }
                catch (OperationCanceledException)
                {
                    return null;
                }
            }
        }

        return null;
    }
    public async Task<BridgeLiveStatusDto?> GetBridgeLiveStatusAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, "/bridge/session/status");
            req.Headers.Add("X-Bridge-Secret", _sharedSecret);
            using var resp = await httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadFromJsonAsync<BridgeLiveStatusDto>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            }
        }
        catch
        {
            // Bridge process offline
        }
        return null;
    }

    public async Task<BridgeLiveStatusDto?> StartBridgeSessionAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/bridge/session/start");
            req.Headers.Add("X-Bridge-Secret", _sharedSecret);
            req.Content = JsonContent.Create(new { forceFreshQr = true });
            using var resp = await httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadFromJsonAsync<BridgeLiveStatusDto>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Não foi possível contatar /bridge/session/start no processo TypeScript.");
        }
        return null;
    }

    public async Task<BridgeLiveStatusDto?> RequestPairingCodeAsync(string phoneNumber, CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/bridge/session/pairing-code");
            req.Headers.Add("X-Bridge-Secret", _sharedSecret);
            req.Content = JsonContent.Create(new { phoneNumber });
            using var resp = await httpClient.SendAsync(req, ct);
            if (resp.IsSuccessStatusCode)
            {
                return await resp.Content.ReadFromJsonAsync<BridgeLiveStatusDto>(
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true }, ct);
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Não foi possível solicitar Pairing Code no processo TypeScript.");
        }
        return null;
    }

    public async Task<bool> LogoutBridgeSessionAsync(CancellationToken ct = default)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/bridge/session/logout");
            req.Headers.Add("X-Bridge-Secret", _sharedSecret);
            using var resp = await httpClient.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}

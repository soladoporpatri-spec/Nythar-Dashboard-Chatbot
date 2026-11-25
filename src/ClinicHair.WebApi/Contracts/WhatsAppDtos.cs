using System.ComponentModel.DataAnnotations;

namespace ClinicHair.WebApi.Contracts;

/// <summary>
/// Payload de evento recebido do microservice TypeScript Baileys Bridge via Webhook assinado (HMAC-SHA256).
/// </summary>
public sealed record WhatsAppWebhookEventPayload
{
    [Required, MaxLength(128)]
    public required string EventId { get; init; }

    [Required, MaxLength(40)]
    public required string EventType { get; init; } // "session.qr" | "session.status" | "poll.vote" | "message.text"

    [MaxLength(100)]
    public string SenderJid { get; init; } = string.Empty;

    [MaxLength(3000)]
    public string? TextMessage { get; init; }

    [MaxLength(128)]
    public string? PollMessageId { get; init; }

    public IReadOnlyList<string>? SelectedOptions { get; init; }

    public string? QrCodeBase64 { get; init; }

    public string? QrCodeRaw { get; init; }

    [MaxLength(32)]
    public string? PairingCode { get; init; }

    [MaxLength(40)]
    public string? SessionStatus { get; init; }

    [MaxLength(60)]
    public string? ConnectedPhone { get; init; }

    public DateTimeOffset? Timestamp { get; init; }
}

/// <summary>
/// Requisição enviada da API C# para o Bridge TypeScript para disparar uma Enquete nativa no WhatsApp.
/// </summary>
public sealed record BridgeSendPollRequest(
    string RecipientJid,
    string Question,
    IReadOnlyList<string> Options,
    int SelectableCount = 1);

/// <summary>
/// Requisição enviada da API C# para o Bridge TypeScript para disparar uma mensagem de texto no WhatsApp.
/// </summary>
public sealed record BridgeSendTextRequest(
    string RecipientJid,
    string Text);

/// <summary>
/// Resposta do Bridge TypeScript após envio de enquete ou texto.
/// </summary>
public sealed record BridgeSendMessageResponse(
    string MessageId,
    bool DeliveredToBridge);

/// <summary>
/// Metadado de uma opção de enquete armazenado na sessão para resolução determinística do voto.
/// </summary>
public sealed record PollOptionMeta(
    string Label,
    string ActionCode,
    string? Value = null);

/// <summary>
/// Item do histórico de conversa com o Bot de Enquetes (exibido também no Simulador ao Vivo da aba WhatsApp).
/// </summary>
public sealed record BotChatHistoryItem(
    string Id,
    string Direction, // "inbound" | "outbound"
    string Kind,      // "text" | "poll"
    string Text,
    string? PollMessageId,
    IReadOnlyList<string>? PollOptions,
    string? SelectedOption,
    string Timestamp);

/// <summary>
/// Requisição para interagir ou testar o fluxo de enquetes do WhatsApp diretamente na Dashboard.
/// </summary>
public sealed record BotSimulationRequest
{
    [Required, MaxLength(40)]
    public required string Phone { get; init; }

    [Required, MaxLength(40)]
    public required string ActionType { get; init; } // "text" | "vote" | "trigger-1h-reminder" | "trigger-2h-evaluation" | "reset"

    [MaxLength(2500)]
    public string? Text { get; init; }

    [MaxLength(128)]
    public string? PollMessageId { get; init; }

    [MaxLength(200)]
    public string? SelectedOption { get; init; }
}

/// <summary>
/// Estado completo da sessão do cliente e histórico de enquetes/mensagens no WhatsApp.
/// </summary>
public sealed record BotSimulationStateResponse(
    string Phone,
    string CurrentStep,
    string? ActivePollMessageId,
    bool EvaluationWindowOpen,
    DateTimeOffset? EvaluationWindowExpiresAt,
    IReadOnlyList<BotChatHistoryItem> Messages);

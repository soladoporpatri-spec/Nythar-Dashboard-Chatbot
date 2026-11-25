using System.Collections.Concurrent;
using System.Threading.Channels;

namespace ClinicHair.WebApi.Services;

/// <summary>
/// Evento em tempo real enviado para as telas abertas da Dashboard e armazenado no histórico recente.
/// </summary>
public sealed record DashboardRealtimeEvent(
    long Id,
    string Topic,     // "agenda" | "evaluations" | "whatsapp"
    string Title,     // Título curto da notificação
    string Summary,   // Mensagem descritiva (ex: "Rafael Mendonça agendou Corte às 14:00 do dia 11/11/2026.")
    DateTimeOffset Timestamp);

/// <summary>
/// Gerencia assinaturas em tempo real (Server-Sent Events) e histórico de notificações
/// para atualizar a Agenda, CRM/Avaliações e WhatsApp instantaneamente na Dashboard e no celular.
/// </summary>
public interface IDashboardRealtimeNotifier
{
    DashboardRealtimeEvent Publish(string topic, string summary, string? title = null);
    IReadOnlyList<DashboardRealtimeEvent> GetRecentEvents(long afterId = 0, int limit = 25);
    IAsyncEnumerable<DashboardRealtimeEvent> SubscribeAsync(CancellationToken ct);
}

/// <summary>
/// Implementação de alta vazão baseada em System.Threading.Channels + buffer circular thread-safe.
/// </summary>
public sealed class DashboardRealtimeNotifier : IDashboardRealtimeNotifier
{
    private readonly ConcurrentDictionary<Guid, Channel<DashboardRealtimeEvent>> _subscribers = new();
    private readonly ConcurrentQueue<DashboardRealtimeEvent> _recentHistory = new();
    private long _nextId = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    public DashboardRealtimeEvent Publish(string topic, string summary, string? title = null)
    {
        var resolvedTitle = !string.IsNullOrWhiteSpace(title)
            ? title
            : topic switch
            {
                "agenda" => "Novo Agendamento",
                "evaluations" => "Nova Avaliação (.txt)",
                "whatsapp" => "Conexão WhatsApp",
                _ => "Clinic Hair"
            };

        var id = Interlocked.Increment(ref _nextId);
        var evt = new DashboardRealtimeEvent(id, topic, resolvedTitle, summary, DateTimeOffset.UtcNow);

        _recentHistory.Enqueue(evt);
        while (_recentHistory.Count > 35 && _recentHistory.TryDequeue(out _))
        {
        }

        foreach (var (_, channel) in _subscribers)
        {
            channel.Writer.TryWrite(evt);
        }

        return evt;
    }

    public IReadOnlyList<DashboardRealtimeEvent> GetRecentEvents(long afterId = 0, int limit = 25)
    {
        return _recentHistory
            .Where(e => e.Id > afterId)
            .OrderByDescending(e => e.Id)
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();
    }

    public async IAsyncEnumerable<DashboardRealtimeEvent> SubscribeAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<DashboardRealtimeEvent>(new BoundedChannelOptions(32)
        {
            FullMode = BoundedChannelFullMode.DropOldest
        });

        _subscribers[id] = channel;
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(ct))
            {
                yield return item;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }
}

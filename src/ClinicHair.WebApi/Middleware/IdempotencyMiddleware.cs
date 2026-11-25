using System.Collections.Concurrent;

namespace ClinicHair.WebApi.Middleware;

/// <summary>
/// Idempotência para mutações da Dashboard/SuperAdmin (POST/PUT/PATCH/DELETE com cabeçalho Idempotency-Key).
/// - Duplo clique, reenvio por rede instável ou retry do navegador devolvem a resposta original
///   em vez de repetir a ação (ex.: criar o mesmo agendamento duas vezes, emitir cobrança em duplicidade).
/// - Requisições concorrentes com a mesma chave aguardam a primeira e recebem o mesmo resultado.
/// - A chave é isolada por usuário + método + rota; só respostas de sucesso (&lt; 400) ficam em cache.
/// - Janela curta (6s): barra duplo clique/retry imediato sem impedir ações legítimas repetidas (ex.: alternar dia de folga).
/// </summary>
public sealed class IdempotencyMiddleware(RequestDelegate next, ILogger<IdempotencyMiddleware> logger)
{
    private static readonly TimeSpan Ttl = TimeSpan.FromSeconds(6);
    private const int MaxCachedBodyBytes = 512 * 1024;

    private sealed record CachedResponse(int StatusCode, string? ContentType, byte[] Body, DateTime ExpiresAtUtc);

    private static readonly ConcurrentDictionary<string, Lazy<Task<CachedResponse?>>> Store = new();

    public async Task InvokeAsync(HttpContext context)
    {
        var request = context.Request;
        var isMutation = HttpMethods.IsPost(request.Method)
            || HttpMethods.IsPut(request.Method)
            || HttpMethods.IsPatch(request.Method)
            || HttpMethods.IsDelete(request.Method);

        var path = request.Path.Value ?? string.Empty;
        var inScope = path.StartsWith("/api/dashboard", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/api/superadmin", StringComparison.OrdinalIgnoreCase);

        var key = request.Headers["Idempotency-Key"].FirstOrDefault();
        if (!isMutation || !inScope || string.IsNullOrWhiteSpace(key) || key.Length > 200
            || context.User.Identity?.IsAuthenticated != true)
        {
            await next(context);
            return;
        }

        PurgeExpired();

        var scopedKey = $"{context.User.Identity.Name}|{request.Method}|{path}|{key}";

        // Entrada expirada (TTL) não pode reproduzir resposta antiga
        if (Store.TryGetValue(scopedKey, out var existing)
            && existing.Value.IsCompletedSuccessfully
            && existing.Value.Result is { } old
            && old.ExpiresAtUtc < DateTime.UtcNow)
        {
            Store.TryRemove(scopedKey, out _);
        }

        var created = false;
        var lazy = Store.GetOrAdd(scopedKey, _ =>
        {
            created = true;
            return new Lazy<Task<CachedResponse?>>(() => ExecuteAndCaptureAsync(context, scopedKey));
        });

        if (created)
        {
            // Primeira requisição: executa de fato e escreve a resposta direto no cliente
            await lazy.Value;
            return;
        }

        // Duplicada: espera a original e reproduz a mesma resposta
        CachedResponse? cached;
        try
        {
            cached = await lazy.Value;
        }
        catch
        {
            cached = null;
        }
        if (cached is null)
        {
            // A original falhou: esta tentativa é uma nova execução legítima
            await next(context);
            return;
        }

        logger.LogInformation("Requisição idempotente repetida ({Method} {Path}); devolvendo resposta original.", request.Method, path);
        context.Response.StatusCode = cached.StatusCode;
        context.Response.Headers["Idempotent-Replayed"] = "true";
        if (cached.ContentType is not null)
        {
            context.Response.ContentType = cached.ContentType;
        }
        await context.Response.Body.WriteAsync(cached.Body);
    }

    private async Task<CachedResponse?> ExecuteAndCaptureAsync(HttpContext context, string scopedKey)
    {
        var originalBody = context.Response.Body;
        await using var buffer = new MemoryStream();
        context.Response.Body = buffer;

        try
        {
            await next(context);
        }
        catch
        {
            context.Response.Body = originalBody;
            Store.TryRemove(scopedKey, out _);
            throw;
        }

        buffer.Position = 0;
        context.Response.Body = originalBody;
        await buffer.CopyToAsync(originalBody);

        var status = context.Response.StatusCode;
        if (status >= 400 || buffer.Length > MaxCachedBodyBytes)
        {
            Store.TryRemove(scopedKey, out _);
            return null;
        }

        return new CachedResponse(status, context.Response.ContentType, buffer.ToArray(), DateTime.UtcNow.Add(Ttl));
    }

    private static void PurgeExpired()
    {
        if (Store.Count < 256) return;
        foreach (var (k, v) in Store)
        {
            if (v.Value.IsCompletedSuccessfully && v.Value.Result is { } r && r.ExpiresAtUtc < DateTime.UtcNow)
            {
                Store.TryRemove(k, out _);
            }
        }
    }
}

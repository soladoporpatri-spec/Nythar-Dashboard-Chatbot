using System.Security.Claims;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Services.Platform;

/// <summary>Identidade e loja ativa da requisição atual (lojas comuns ficam presas ao próprio StoreId).</summary>
public interface ICurrentStore
{
    int StoreId { get; }
    int? UserId { get; }
    string Username { get; }
    bool IsSuperAdmin { get; }
}

public sealed class CurrentStore(IHttpContextAccessor accessor) : ICurrentStore
{
    public int StoreId
    {
        get
        {
            var ctx = accessor.HttpContext;
            if (ctx is null) return 1;

            if (ctx.User.IsInRole("SuperAdmin"))
            {
                if (ctx.Request.Headers.TryGetValue("X-Target-Store-Id", out var h)
                    && int.TryParse(h.ToString(), out var hs) && hs > 0)
                {
                    return hs;
                }
                if (ctx.Request.Query.TryGetValue("targetStoreId", out var q)
                    && int.TryParse(q.ToString(), out var qs) && qs > 0)
                {
                    return qs;
                }
            }

            return int.TryParse(ctx.User.FindFirst("StoreId")?.Value, out var claim) && claim > 0 ? claim : 1;
        }
    }

    public int? UserId => int.TryParse(accessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var id) ? id : null;

    public string Username => accessor.HttpContext?.User.Identity?.Name ?? "sistema";

    public bool IsSuperAdmin => accessor.HttpContext?.User.IsInRole("SuperAdmin") ?? false;
}

/// <summary>Trilha de auditoria de ações sensíveis (gravada em SystemLogEntries com usuário e loja).</summary>
public interface IAuditLogger
{
    Task LogAsync(string category, string message, CancellationToken ct = default);
    void Stage(string category, string message);
}

public sealed class AuditLogger(ClinicHairDbContext db, ICurrentStore current) : IAuditLogger
{
    public async Task LogAsync(string category, string message, CancellationToken ct = default)
    {
        Stage(category, message);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Adiciona a entrada ao contexto sem salvar (salva junto com a operação principal).</summary>
    public void Stage(string category, string message)
    {
        var storeId = current.StoreId;
        db.SystemLogEntries.Add(new SystemLogEntry
        {
            Level = "AUDIT",
            Category = category,
            StoreId = storeId,
            StoreName = null,
            Message = $"[{current.Username}] {message}",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }
}

/// <summary>Helpers de modelo de dados compartilhados.</summary>
public static class StoreQueries
{
    public static async Task<Store> LoadAsync(ClinicHairDbContext db, int storeId, bool tracking, CancellationToken ct)
    {
        var q = tracking ? db.Stores.AsTracking() : db.Stores.AsNoTracking();
        return await q.FirstOrDefaultAsync(s => s.Id == storeId, ct)
            ?? throw new KeyNotFoundException("Loja não encontrada.");
    }
}

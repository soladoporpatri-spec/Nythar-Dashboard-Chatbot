using ClinicHair.WebApi.Services.Platform;
using Microsoft.AspNetCore.Mvc;

namespace ClinicHair.WebApi.Middleware;

/// <summary>Cabeçalhos de segurança HTTP (CSP, anti-clickjacking, anti-sniffing, referrer).</summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    private const string Csp =
        "default-src 'self'; " +
        "script-src 'self' https://unpkg.com; " +
        "style-src 'self' 'unsafe-inline' https://fonts.googleapis.com; " +
        "font-src 'self' data: https://fonts.gstatic.com; " +
        "img-src 'self' data: blob: https:; " +
        "connect-src 'self'; " +
        "manifest-src 'self'; " +
        "worker-src 'self'; " +
        "frame-ancestors 'none'; base-uri 'self'; form-action 'self'; object-src 'none'";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var h = context.Response.Headers;
            h["X-Content-Type-Options"] = "nosniff";
            h["X-Frame-Options"] = "DENY";
            h["Referrer-Policy"] = "no-referrer";
            h["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (!context.Request.Path.StartsWithSegments("/openapi"))
            {
                h["Content-Security-Policy"] = Csp;
            }
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                h["Cache-Control"] = "no-store";
            }
            return Task.CompletedTask;
        });
        return next(context);
    }
}

/// <summary>
/// Sessões revogadas (logout / troca de senha) e usuários obrigados a trocar a senha.
/// Enquanto a troca não acontecer, só /api/auth/* fica liberado.
/// </summary>
public sealed class PasswordChangeGateMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.User.Identity?.IsAuthenticated == true
            && context.User.HasClaim(AccountService.ClaimMustChangePassword, "1")
            && context.Request.Path.StartsWithSegments("/api")
            && !context.Request.Path.StartsWithSegments("/api/auth"))
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            await context.Response.WriteAsJsonAsync(new ProblemDetails
            {
                Status = 403,
                Title = "PASSWORD_CHANGE_REQUIRED",
                Detail = "Defina uma nova senha antes de continuar."
            });
            return;
        }

        await next(context);
    }
}

/// <summary>
/// Id de correlação por requisição (item 89): aceita X-Correlation-Id do cliente (ou gera um),
/// devolve no cabeçalho da resposta e inclui em todas as linhas de log da requisição.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        var incoming = context.Request.Headers["X-Correlation-Id"].ToString();
        var id = !string.IsNullOrWhiteSpace(incoming) && incoming.Length <= 64 && incoming.All(c => char.IsLetterOrDigit(c) || c is '-' or '_')
            ? incoming
            : Guid.NewGuid().ToString("N")[..16];
        context.TraceIdentifier = id;
        context.Response.Headers["X-Correlation-Id"] = id;
        using (logger.BeginScope(new Dictionary<string, object> { ["CorrelationId"] = id, ["Path"] = context.Request.Path.Value ?? "" }))
        {
            await next(context);
        }
    }
}

/// <summary>
/// Isolamento do SuperAdmin (itens 67, 69 e 76):
/// - lojas comuns recebem 404 em /api/superadmin (a área nem "existe" para elas);
/// - toda alteração feita pelo SuperAdmin dentro de uma loja (modo suporte) vai para o log SUPERADMIN.
/// </summary>
public sealed class SuperAdminIsolationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, ClinicHair.WebApi.Infrastructure.Data.ClinicHairDbContext db)
    {
        var path = context.Request.Path;
        bool isSuperArea = path.StartsWithSegments("/api/superadmin");
        bool isSuper = context.User.IsInRole("SuperAdmin");
        if (isSuperArea && !isSuper)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        await next(context);

        if (isSuper && !isSuperArea && path.StartsWithSegments("/api/dashboard")
            && !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method)
            && int.TryParse(context.Request.Headers["X-Target-Store-Id"].ToString(), out var target) && target > 0
            && context.Response.StatusCode < 400)
        {
            try
            {
                db.SystemLogEntries.Add(new ClinicHair.WebApi.Domain.Entities.SystemLogEntry
                {
                    Level = "SUPERADMIN",
                    Category = "Modo suporte",
                    StoreId = target,
                    Message = $"[{context.User.Identity?.Name}] {context.Request.Method} {path} (correlação {context.TraceIdentifier})",
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await db.SaveChangesAsync(context.RequestAborted);
            }
            catch
            {
                // auditoria nunca derruba a requisição já concluída
            }
        }
    }
}

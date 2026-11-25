using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace ClinicHair.WebApi.Middleware;

/// <summary>Conta bloqueada por excesso de tentativas de login.</summary>
public sealed class AccountLockedException(string message) : Exception(message);

/// <summary>Senha correta, mas a conta exige o código do segundo fator (TOTP).</summary>
public sealed class TotpRequiredException() : Exception("Informe o código de 6 dígitos do seu aplicativo autenticador.");

/// <summary>
/// Tratador global de exceções retornando ProblemDetails padronizado (RFC 7807).
/// </summary>
internal sealed class ApiExceptionHandler(ILogger<ApiExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        logger.LogError(exception, "Exceção na API [{Path}]: {Message}", httpContext.Request.Path, exception.Message);

        var (statusCode, title, detail) = exception switch
        {
            AccountLockedException ex => (StatusCodes.Status429TooManyRequests, "Conta temporariamente bloqueada", ex.Message),
            TotpRequiredException ex => (StatusCodes.Status401Unauthorized, "TOTP_REQUIRED", ex.Message),
            KeyNotFoundException ex => (StatusCodes.Status404NotFound, "Recurso não encontrado", ex.Message),
            ArgumentException ex => (StatusCodes.Status400BadRequest, "Dados inválidos", ex.Message),
            InvalidOperationException ex => (StatusCodes.Status409Conflict, "Regra de negócio violada", ex.Message),
            UnauthorizedAccessException ex => (StatusCodes.Status401Unauthorized, "Não autorizado", ex.Message),
            // Nunca expõe stack trace ou detalhes internos para o navegador (ficam apenas no log do servidor)
            _ => (StatusCodes.Status500InternalServerError, "Erro interno do servidor", "Ocorreu um erro inesperado. Tente novamente; se persistir, avise o suporte.")
        };

        if (statusCode == 0)
        {
            return false;
        }

        logger.LogWarning(exception, "Exceção tratada na API ({StatusCode}): {Detail}", statusCode, detail);

        httpContext.Response.StatusCode = statusCode;
        var problem = new ProblemDetails
        {
            Status = statusCode,
            Title = title,
            Detail = detail,
            Instance = httpContext.Request.Path
        };
        // Id de correlação: o mesmo valor aparece no log do servidor, facilitando o suporte (item 89)
        problem.Extensions["correlationId"] = httpContext.TraceIdentifier;
        await httpContext.Response.WriteAsJsonAsync(problem, cancellationToken);

        return true;
    }
}

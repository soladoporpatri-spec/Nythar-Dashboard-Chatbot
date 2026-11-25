using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Middleware;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.IdentityModel.Tokens;

namespace ClinicHair.WebApi.Services.Platform;

/// <summary>
/// Autenticação e segurança de contas: bloqueio por tentativas, política de senha, troca obrigatória,
/// revogação de sessão no logout e reautenticação para ações sensíveis (exportações).
/// </summary>
public interface IAccountService
{
    Task<AuthResponse?> LoginAsync(LoginRequest request, CancellationToken ct);
    Task<AuthResponse> ChangePasswordAsync(int userId, ChangePasswordRequest request, CancellationToken ct);
    Task LogoutAsync(ClaimsPrincipal user, CancellationToken ct);
    Task<bool> IsTokenStillValidAsync(ClaimsPrincipal principal, CancellationToken ct);
    Task<ReauthResponse> ReauthenticateAsync(int userId, string password, CancellationToken ct);
    bool ValidateExportTicket(string? ticket, int? userId);
    static string? ValidatePasswordPolicy(string password, string username) => PasswordPolicy.Validate(password, username);
}

public static class PasswordPolicy
{
    public const int MinLength = 8;

    public static string? Validate(string? password, string username)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < MinLength)
            return $"A senha precisa ter pelo menos {MinLength} caracteres.";
        if (!password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            return "A senha precisa ter letras e números.";
        if (password.Contains(username, StringComparison.OrdinalIgnoreCase))
            return "A senha não pode conter o nome de usuário.";
        if (password.Distinct().Count() < 4)
            return "A senha é muito simples (use caracteres variados).";
        return null;
    }
}

public sealed class AccountService(
    ClinicHairDbContext db,
    IConfiguration config,
    IMemoryCache cache,
    ILogger<AccountService> logger) : IAccountService
{
    public const string ClaimSessionVersion = "sv";
    public const string ClaimMustChangePassword = "pwd_change";
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);

    public async Task<AuthResponse?> LoginAsync(LoginRequest request, CancellationToken ct)
    {
        var username = request.Username.Trim();
        var user = await db.AdminUsers.AsTracking()
            .FirstOrDefaultAsync(u => u.Username.ToLower() == username.ToLower(), ct);
        if (user is null)
        {
            return null;
        }

        var now = DateTimeOffset.UtcNow;
        if (user.LockoutEndUtc.HasValue && user.LockoutEndUtc.Value > now)
        {
            var minutes = Math.Max(1, (int)Math.Ceiling((user.LockoutEndUtc.Value - now).TotalMinutes));
            throw new AccountLockedException($"Muitas tentativas incorretas. Tente novamente em {minutes} minuto(s).");
        }

        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == user.StoreId, ct)
            ?? await db.Stores.OrderBy(s => s.Id).FirstAsync(ct);

        var hasher = new PasswordHasher<AdminUser>();
        var verify = hasher.VerifyHashedPassword(user, user.PasswordHash, request.Password);
        if (verify == PasswordVerificationResult.Failed)
        {
            int maxFailed = config.GetValue("Auth:MaxFailedLogins", 5);
            int lockMinutes = config.GetValue("Auth:LockoutMinutes", 15);
            user.FailedLoginCount += 1;
            string msg = $"Senha incorreta para '{user.Username}' ({user.FailedLoginCount}/{maxFailed}).";
            if (user.FailedLoginCount >= maxFailed)
            {
                user.LockoutEndUtc = now.AddMinutes(lockMinutes);
                user.FailedLoginCount = 0;
                msg = $"Conta '{user.Username}' bloqueada por {lockMinutes} min após {maxFailed} tentativas incorretas.";
            }
            AddSecurityLog(store, msg);
            await db.SaveChangesAsync(ct);
            return null;
        }

        if (verify == PasswordVerificationResult.SuccessRehashNeeded)
        {
            user.PasswordHash = hasher.HashPassword(user, request.Password);
        }

        if (!string.Equals(user.Role, "SuperAdmin", StringComparison.OrdinalIgnoreCase) && !store.IsActive)
        {
            throw new InvalidOperationException(
                $"O acesso da loja '{store.Name}' encontra-se suspenso (Assinatura: {store.SubscriptionStatus}). Entre em contato com o suporte.");
        }

        // Segundo fator (item 68): com TOTP ativo, a senha sozinha não basta
        if (user.TotpEnabled)
        {
            if (string.IsNullOrWhiteSpace(request.TotpCode))
            {
                await db.SaveChangesAsync(ct);
                throw new ClinicHair.WebApi.Middleware.TotpRequiredException();
            }
            if (!ClinicHair.WebApi.Services.Features.Totp.Verify(user.TotpSecret, request.TotpCode))
            {
                user.FailedLoginCount += 1;
                AddSecurityLog(store, $"Código do segundo fator incorreto para '{user.Username}'.");
                await db.SaveChangesAsync(ct);
                return null;
            }
        }

        user.FailedLoginCount = 0;
        user.LockoutEndUtc = null;
        AddSecurityLog(store, $"Login autenticado por '{user.Username}' (Perfil: {user.Role}).");
        await db.SaveChangesAsync(ct);

        return IssueToken(user, store);
    }

    public async Task<AuthResponse> ChangePasswordAsync(int userId, ChangePasswordRequest request, CancellationToken ct)
    {
        var user = await db.AdminUsers.AsTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("Usuário não encontrado.");

        var hasher = new PasswordHasher<AdminUser>();
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.CurrentPassword) == PasswordVerificationResult.Failed)
        {
            throw new ArgumentException("A senha atual está incorreta.");
        }

        var policyError = PasswordPolicy.Validate(request.NewPassword, user.Username);
        if (policyError is not null)
        {
            throw new ArgumentException(policyError);
        }

        if (hasher.VerifyHashedPassword(user, user.PasswordHash, request.NewPassword) != PasswordVerificationResult.Failed)
        {
            throw new ArgumentException("A nova senha precisa ser diferente da atual.");
        }

        user.PasswordHash = hasher.HashPassword(user, request.NewPassword);
        user.MustChangePassword = false;
        user.SessionVersion += 1; // encerra as sessões abertas em outros aparelhos
        user.PasswordChangedAt = DateTimeOffset.UtcNow;

        var store = await db.Stores.FirstOrDefaultAsync(s => s.Id == user.StoreId, ct)
            ?? await db.Stores.OrderBy(s => s.Id).FirstAsync(ct);
        AddSecurityLog(store, $"Senha alterada por '{user.Username}'. Sessões anteriores encerradas.");
        await db.SaveChangesAsync(ct);

        return IssueToken(user, store);
    }

    public async Task LogoutAsync(ClaimsPrincipal user, CancellationToken ct)
    {
        var jti = user.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        if (string.IsNullOrWhiteSpace(jti)) return;

        var expClaim = user.FindFirst(JwtRegisteredClaimNames.Exp)?.Value;
        var expires = long.TryParse(expClaim, out var unix)
            ? DateTimeOffset.FromUnixTimeSeconds(unix)
            : DateTimeOffset.UtcNow.Add(TokenLifetime);

        if (!await db.RevokedTokens.AnyAsync(r => r.Jti == jti, ct))
        {
            db.RevokedTokens.Add(new RevokedToken { Jti = jti, ExpiresAtUtc = expires });
        }

        // Limpeza de tokens revogados já expirados
        var now = DateTimeOffset.UtcNow;
        var expired = (await db.RevokedTokens.AsTracking().ToListAsync(ct)).Where(r => r.ExpiresAtUtc < now).ToList();
        if (expired.Count > 0) db.RevokedTokens.RemoveRange(expired);

        await db.SaveChangesAsync(ct);
        cache.Set($"jti-valid:{jti}", false, TimeSpan.FromHours(13));
    }

    public async Task<bool> IsTokenStillValidAsync(ClaimsPrincipal principal, CancellationToken ct)
    {
        var jti = principal.FindFirst(JwtRegisteredClaimNames.Jti)?.Value;
        if (!int.TryParse(principal.FindFirst(ClaimTypes.NameIdentifier)?.Value, out var userId))
        {
            return false;
        }

        // Tokens antigos (emitidos antes desta versão) não têm jti/sv: forçam novo login
        if (string.IsNullOrWhiteSpace(jti)) return false;

        var cacheKey = $"jti-valid:{jti}";
        if (cache.TryGetValue(cacheKey, out bool cached)) return cached;

        bool revoked = await db.RevokedTokens.AnyAsync(r => r.Jti == jti, ct);
        var user = await db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct);
        int.TryParse(principal.FindFirst(ClaimSessionVersion)?.Value, out var sv);
        bool valid = !revoked && user is not null && user.SessionVersion == sv;

        cache.Set(cacheKey, valid, TimeSpan.FromSeconds(20));
        return valid;
    }

    public async Task<ReauthResponse> ReauthenticateAsync(int userId, string password, CancellationToken ct)
    {
        var user = await db.AdminUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new KeyNotFoundException("Usuário não encontrado.");
        var hasher = new PasswordHasher<AdminUser>();
        if (hasher.VerifyHashedPassword(user, user.PasswordHash, password ?? "") == PasswordVerificationResult.Failed)
        {
            throw new ArgumentException("Senha incorreta.");
        }

        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        var expires = DateTimeOffset.UtcNow.AddMinutes(5);
        cache.Set($"export-ticket:{ticket}", userId, expires);
        return new ReauthResponse(ticket, expires);
    }

    public bool ValidateExportTicket(string? ticket, int? userId)
    {
        if (string.IsNullOrWhiteSpace(ticket) || userId is null) return false;
        return cache.TryGetValue($"export-ticket:{ticket}", out int owner) && owner == userId.Value;
    }

    private AuthResponse IssueToken(AdminUser user, Store store)
    {
        var secret = config["Jwt:SecretKey"] ?? throw new InvalidOperationException("Jwt:SecretKey não configurado.");
        var expires = DateTimeOffset.UtcNow.Add(TokenLifetime);
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret)), SecurityAlgorithms.HmacSha256);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, user.Id.ToString()),
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role),
            new("StoreId", store.Id.ToString()),
            new(ClaimSessionVersion, user.SessionVersion.ToString()),
            new(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
        };
        if (user.MustChangePassword)
        {
            claims.Add(new Claim(ClaimMustChangePassword, "1"));
        }

        var jwt = new JwtSecurityToken(
            issuer: "ClinicHair.WebApi",
            audience: "ClinicHair.Dashboard",
            claims: claims,
            expires: expires.UtcDateTime,
            signingCredentials: creds);

        return new AuthResponse(
            new JwtSecurityTokenHandler().WriteToken(jwt),
            user.Username,
            store.Name,
            expires,
            user.Role,
            store.Id,
            user.MustChangePassword);
    }

    private void AddSecurityLog(Store store, string message)
    {
        logger.LogInformation("{Message}", message);
        db.SystemLogEntries.Add(new SystemLogEntry
        {
            StoreId = store.Id,
            StoreName = store.Name,
            Level = "SECURITY",
            Category = "Auth",
            Message = message,
            CreatedAt = DateTimeOffset.UtcNow
        });
    }
}

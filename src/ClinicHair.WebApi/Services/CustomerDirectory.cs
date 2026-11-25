using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace ClinicHair.WebApi.Services;

/// <summary>
/// Identidade do cliente: normalização de telefone (com/sem 55, com/sem 9º dígito) e validação de nome.
/// Garante que "62982854050", "5562982854050" e "556282854050" sejam reconhecidos como a MESMA pessoa.
/// </summary>
public static class PhoneIdentity
{
    private static readonly HashSet<string> NotNames = new(StringComparer.Ordinal)
    {
        "oi", "ola", "oie", "opa", "eai", "e ai", "bom dia", "boa tarde", "boa noite", "tudo bem",
        "agendar", "agendamento", "agenda", "marcar", "quero agendar", "quero marcar", "menu", "inicio",
        "cancelar", "remarcar", "sim", "nao", "ok", "okay", "encerrar", "sair", "voltar", "teste",
        "cliente", "cliente whatsapp", "obrigado", "obrigada", "valeu", "ajuda", "oi tudo bem"
    };

    private static readonly HashSet<string> NamePrepositions = new(StringComparer.Ordinal)
    {
        "da", "de", "do", "das", "dos", "e"
    };

    /// <summary>Forma canônica: 55 + DDD + (9) + número. Números que não parecem telefone BR (ex.: ID @lid) ficam como estão.</summary>
    public static string Canonical(string? raw)
    {
        var digits = DigitsOnly(raw);
        if (digits.Length == 0) return string.Empty;

        if (digits.Length is 10 or 11)
        {
            digits = "55" + digits;
        }

        // Celular antigo sem o 9º dígito (55 + DDD + 8 dígitos começando em 6–9): insere o 9
        if (digits.StartsWith("55", StringComparison.Ordinal) && digits.Length == 12 && "6789".Contains(digits[4]))
        {
            digits = digits[..4] + "9" + digits[4..];
        }

        return digits;
    }

    /// <summary>Todas as grafias equivalentes do mesmo número (para localizar cadastros antigos).</summary>
    public static IReadOnlyList<string> Variants(string? raw)
    {
        var rawDigits = DigitsOnly(raw);
        if (rawDigits.Length == 0) return [];

        var canonical = Canonical(raw);
        var set = new HashSet<string>(StringComparer.Ordinal) { rawDigits, canonical };

        if (canonical.StartsWith("55", StringComparison.Ordinal) && canonical.Length is 12 or 13)
        {
            set.Add(canonical[2..]); // sem DDI
            if (canonical.Length == 13 && canonical[4] == '9')
            {
                var withoutNine = canonical[..4] + canonical[5..];
                set.Add(withoutNine);
                set.Add(withoutNine[2..]);
            }
        }

        return set.ToList();
    }

    public static bool IsPlaceholderName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var n = name.Trim();
        return n.StartsWith("Cliente Teste", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Cliente WhatsApp", StringComparison.OrdinalIgnoreCase)
            || n.Equals("Cliente", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Valida e limpa o nome digitado pelo cliente. Rejeita saudações/comandos ("oi", "agendar"), números e frases longas.
    /// Capitaliza apenas quando veio todo em minúsculas/maiúsculas.
    /// </summary>
    public static bool TryCleanName(string? input, out string name)
    {
        name = string.Empty;
        if (string.IsNullOrWhiteSpace(input)) return false;

        var cleaned = new string(input.Where(c => !char.IsControl(c)).ToArray());
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        if (cleaned.Length > 80) cleaned = cleaned[..80].Trim();

        var letters = cleaned.Count(char.IsLetter);
        if (letters < 2) return false;
        if (cleaned.Count(char.IsDigit) > 0 && cleaned.Count(char.IsDigit) >= letters) return false;
        if (cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 6) return false;

        if (NotNames.Contains(StripForCompare(cleaned))) return false;

        var allLower = cleaned == cleaned.ToLower(CultureInfo.GetCultureInfo("pt-BR"));
        var allUpper = cleaned == cleaned.ToUpper(CultureInfo.GetCultureInfo("pt-BR"));
        if (allLower || allUpper)
        {
            var words = cleaned.ToLower(CultureInfo.GetCultureInfo("pt-BR")).Split(' ', StringSplitOptions.RemoveEmptyEntries);
            cleaned = string.Join(' ', words.Select((w, i) =>
                i > 0 && NamePrepositions.Contains(w)
                    ? w
                    : char.ToUpper(w[0], CultureInfo.GetCultureInfo("pt-BR")) + w[1..]));
        }

        name = cleaned;
        return true;
    }

    private static string DigitsOnly(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
        var beforeAt = raw.Split('@')[0].Split(':')[0];
        return new string(beforeAt.Where(char.IsDigit).ToArray());
    }

    private static string StripForCompare(string text)
    {
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(c) || c == ' ') sb.Append(char.ToLowerInvariant(c));
        }
        return Regex.Replace(sb.ToString(), @"\s+", " ").Trim();
    }
}

public interface ICustomerDirectory
{
    /// <summary>Localiza o cliente por qualquer grafia do telefone. Com tracking=true também "cura" o telefone para a forma canônica.</summary>
    Task<Customer?> FindAsync(int storeId, string? rawPhone, bool tracking, CancellationToken ct);

    /// <summary>Nome confiável já salvo para este telefone (null se desconhecido ou apenas placeholder).</summary>
    Task<string?> GetKnownNameAsync(int storeId, string? rawPhone, CancellationToken ct);

    /// <summary>Grava (cria/atualiza) nome + telefone IMEDIATAMENTE, sem esperar o agendamento ser concluído.</summary>
    Task<Customer> SaveIdentityAsync(int storeId, string rawPhone, string name, CancellationToken ct);
}

public sealed class CustomerDirectory(
    ClinicHairDbContext db,
    ILogger<CustomerDirectory> logger) : ICustomerDirectory
{
    public async Task<Customer?> FindAsync(int storeId, string? rawPhone, bool tracking, CancellationToken ct)
    {
        var variants = PhoneIdentity.Variants(rawPhone);
        if (variants.Count == 0) return null;

        var query = tracking ? db.Customers.AsTracking() : db.Customers.AsNoTracking();
        var candidates = await query
            .Where(c => c.StoreId == storeId && variants.Contains(c.Phone))
            .OrderByDescending(c => c.TotalAppointments)
            .ThenBy(c => c.Id)
            .ToListAsync(ct);

        var found = candidates.FirstOrDefault();
        if (found is null) return null;

        if (tracking && candidates.Count == 1)
        {
            var canonical = PhoneIdentity.Canonical(rawPhone);
            if (canonical.Length > 0 && found.Phone != canonical)
            {
                found.Phone = canonical; // auto-cura de cadastro antigo (sem 55 / sem 9)
            }
        }

        return found;
    }

    public async Task<string?> GetKnownNameAsync(int storeId, string? rawPhone, CancellationToken ct)
    {
        var customer = await FindAsync(storeId, rawPhone, tracking: false, ct);
        return customer is not null && !PhoneIdentity.IsPlaceholderName(customer.Name)
            ? customer.Name.Trim()
            : null;
    }

    public async Task<Customer> SaveIdentityAsync(int storeId, string rawPhone, string name, CancellationToken ct)
    {
        var canonical = PhoneIdentity.Canonical(rawPhone);
        if (canonical.Length == 0) canonical = rawPhone.Trim();

        for (var attempt = 0; attempt < 2; attempt++)
        {
            var customer = await FindAsync(storeId, rawPhone, tracking: true, ct);
            if (customer is null)
            {
                customer = new Customer
                {
                    StoreId = storeId,
                    Name = name,
                    Phone = canonical,
                    TotalAppointments = 0,
                    LastAppointmentAt = DateTimeOffset.UtcNow
                };
                db.Customers.Add(customer);
            }
            else if (!string.Equals(customer.Name, name, StringComparison.Ordinal))
            {
                customer.Name = name;
            }

            try
            {
                await db.SaveChangesAsync(ct);
                return customer;
            }
            catch (DbUpdateException ex) when (attempt == 0)
            {
                // Corrida: outro processo criou o mesmo telefone ao mesmo tempo. Recarrega e atualiza.
                logger.LogDebug(ex, "Cadastro concorrente do cliente {Phone}; tentando novamente.", canonical);
                db.ChangeTracker.Clear();
            }
        }

        throw new InvalidOperationException("Não foi possível salvar o cliente.");
    }
}

/// <summary>
/// Migração única e idempotente na inicialização: unifica clientes duplicados que são a mesma pessoa
/// (ex.: "62982854050" e "5562982854050"), preservando agendamentos, avaliações e o melhor nome conhecido.
/// Faz backup do SQLite antes de qualquer alteração.
/// </summary>
public static class CustomerIdentityMigration
{
    public static async Task RunAsync(IServiceProvider services, string backupDir, ILogger logger)
    {
        try
        {
            using var scope = services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ClinicHairDbContext>();

            var all = await db.Customers.AsTracking().ToListAsync();
            var groups = all
                .GroupBy(c => new { c.StoreId, Key = PhoneIdentity.Canonical(c.Phone) })
                .Where(g => g.Key.Key.Length > 0)
                .ToList();

            var needsWork = groups.Any(g => g.Count() > 1 || g.First().Phone != g.Key.Key);
            if (!needsWork) return;

            if (db.Database.IsSqlite())
            {
                Directory.CreateDirectory(backupDir);
                var target = Path.Combine(backupDir, $"clinichair_pre_customer_merge_{DateTime.Now:yyyyMMdd_HHmmss}.db");
#pragma warning disable EF1002 // caminho interno e escapado
                await db.Database.ExecuteSqlRawAsync($"VACUUM INTO '{target.Replace("'", "''")}'");
#pragma warning restore EF1002
                logger.LogInformation("Backup de segurança antes da unificação de clientes: {Path}", target);
            }

            await using var tx = await db.Database.BeginTransactionAsync();

            foreach (var g in groups)
            {
                var ordered = g.OrderByDescending(c => c.TotalAppointments).ThenBy(c => c.CreatedAt).ToList();
                var winner = ordered[0];
                var losers = ordered.Skip(1).ToList();

                if (losers.Count > 0)
                {
                    var bestName = g
                        .Where(c => !PhoneIdentity.IsPlaceholderName(c.Name))
                        .OrderByDescending(c => c.LastAppointmentAt)
                        .Select(c => c.Name)
                        .FirstOrDefault();

                    foreach (var loser in losers)
                    {
                        await db.Appointments.Where(a => a.CustomerId == loser.Id)
                            .ExecuteUpdateAsync(s => s.SetProperty(a => a.CustomerId, winner.Id));
                        await db.Evaluations.Where(e => e.CustomerId == loser.Id)
                            .ExecuteUpdateAsync(s => s.SetProperty(e => e.CustomerId, winner.Id));
                    }

                    winner.TotalAppointments = g.Sum(c => c.TotalAppointments);
                    winner.LastAppointmentAt = g.Max(c => c.LastAppointmentAt);
                    if (bestName is not null) winner.Name = bestName;

                    db.Customers.RemoveRange(losers);
                    await db.SaveChangesAsync(); // remove duplicados antes de ajustar o telefone (índice único)
                    logger.LogInformation("Clientes unificados: {Count} cadastro(s) → #{Id} ({Name}).", g.Count(), winner.Id, winner.Name);
                }

                if (winner.Phone != g.Key.Key)
                {
                    winner.Phone = g.Key.Key;
                    await db.SaveChangesAsync();
                }
            }

            await tx.CommitAsync();
        }
        catch (Exception ex)
        {
            // Nunca impede a API de subir
            logger.LogError(ex, "Falha na unificação de clientes por telefone (dados originais preservados).");
        }
    }
}

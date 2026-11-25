using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClinicHair.WebApi.Services.Features;

/// <summary>
/// Gera o "PIX copia e cola" (BR Code estático do Banco Central, padrão EMV) com valor e identificador.
/// Não movimenta dinheiro: só produz o texto que o cliente cola no app do banco.
/// </summary>
public static class PixBrCode
{
    public static string Build(string key, string merchantName, string merchantCity, decimal amount, string txid)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("Chave PIX não configurada.");
        string name = Clean(merchantName, 25, "LOJA");
        string city = Clean(merchantCity, 15, "BRASIL");
        string tx = new string((txid ?? "").Where(char.IsLetterOrDigit).Take(25).ToArray());
        if (tx.Length == 0) tx = "***";

        var sb = new StringBuilder();
        sb.Append(Field("00", "01"));
        sb.Append(Field("26", Field("00", "br.gov.bcb.pix") + Field("01", key.Trim())));
        sb.Append(Field("52", "0000"));
        sb.Append(Field("53", "986"));
        if (amount > 0) sb.Append(Field("54", amount.ToString("0.00", CultureInfo.InvariantCulture)));
        sb.Append(Field("58", "BR"));
        sb.Append(Field("59", name));
        sb.Append(Field("60", city));
        sb.Append(Field("62", Field("05", tx)));
        sb.Append("6304");
        sb.Append(Crc16(sb.ToString()).ToString("X4"));
        return sb.ToString();
    }

    private static string Field(string id, string value) => id + value.Length.ToString("D2") + value;

    private static string Clean(string? text, int max, string fallback)
    {
        if (string.IsNullOrWhiteSpace(text)) return fallback;
        var normalized = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder();
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c < 128 && (char.IsLetterOrDigit(c) || c == ' ')) sb.Append(char.ToUpperInvariant(c));
        }
        var result = sb.ToString().Trim();
        if (result.Length == 0) return fallback;
        return result.Length > max ? result[..max] : result;
    }

    /// <summary>CRC16-CCITT (polinômio 0x1021, valor inicial 0xFFFF), exigido pelo BR Code.</summary>
    public static ushort Crc16(string payload)
    {
        ushort crc = 0xFFFF;
        foreach (byte b in Encoding.UTF8.GetBytes(payload))
        {
            crc ^= (ushort)(b << 8);
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (ushort)((crc << 1) ^ 0x1021) : (ushort)(crc << 1);
            }
        }
        return crc;
    }
}

/// <summary>Senhas de uso único por tempo (TOTP, RFC 6238) compatíveis com Google Authenticator, Authy, 1Password etc.</summary>
public static class Totp
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string NewSecret()
    {
        var bytes = RandomNumberGenerator.GetBytes(20);
        return Base32Encode(bytes);
    }

    public static string OtpAuthUri(string secret, string account, string issuer = "ClinicHair")
        => $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={secret}&issuer={Uri.EscapeDataString(issuer)}&digits=6&period=30";

    public static bool Verify(string? base32Secret, string? code, DateTimeOffset? now = null)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code)) return false;
        code = new string(code.Where(char.IsDigit).ToArray());
        if (code.Length != 6) return false;
        byte[] key;
        try { key = Base32Decode(base32Secret); } catch { return false; }

        long counter = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds() / 30;
        for (long offset = -1; offset <= 1; offset++) // tolera 30s de diferença de relógio
        {
            var expected = Compute(key, counter + offset);
            if (CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(code))) return true;
        }
        return false;
    }

    public static string Compute(byte[] key, long counter)
    {
        var msg = BitConverter.GetBytes(counter);
        if (BitConverter.IsLittleEndian) Array.Reverse(msg);
        using var hmac = new HMACSHA1(key);
        var hash = hmac.ComputeHash(msg);
        int offset = hash[^1] & 0x0F;
        int binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6");
    }

    public static string Base32Encode(byte[] data)
    {
        var sb = new StringBuilder();
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                sb.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) sb.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return sb.ToString();
    }

    public static byte[] Base32Decode(string text)
    {
        var clean = text.Trim().TrimEnd('=').Replace(" ", "").ToUpperInvariant();
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in clean)
        {
            int v = Alphabet.IndexOf(c);
            if (v < 0) throw new FormatException("Base32 inválido.");
            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }
        return output.ToArray();
    }
}

/// <summary>Compromissos lidos das agendas externas (ICS) de cada profissional, mantidos em memória pelo CalendarSyncWorker.</summary>
public static class ExternalCalendarCache
{
    private static readonly ConcurrentDictionary<int, IReadOnlyList<(DateTime Start, DateTime End, string Title)>> Events = new();
    private static readonly ConcurrentDictionary<int, DateTimeOffset> LastSync = new();

    public static void Set(int barberId, IReadOnlyList<(DateTime Start, DateTime End, string Title)> events)
    {
        Events[barberId] = events;
        LastSync[barberId] = DateTimeOffset.UtcNow;
    }

    public static void Clear(int barberId)
    {
        Events.TryRemove(barberId, out _);
        LastSync.TryRemove(barberId, out _);
    }

    public static DateTimeOffset? LastSyncOf(int barberId) => LastSync.TryGetValue(barberId, out var v) ? v : null;
    public static int CountOf(int barberId) => Events.TryGetValue(barberId, out var v) ? v.Count : 0;

    /// <summary>Intervalos ocupados no dia, já arredondados para a grade de 30 minutos.</summary>
    public static IEnumerable<(string Start, string End, string Title)> GetBusy(int barberId, DateOnly date)
    {
        if (!Events.TryGetValue(barberId, out var list)) yield break;
        var dayStart = date.ToDateTime(TimeOnly.MinValue);
        var dayEnd = dayStart.AddDays(1);
        foreach (var (start, end, title) in list)
        {
            if (end <= dayStart || start >= dayEnd) continue;
            var s = start < dayStart ? dayStart : start;
            var e = end > dayEnd ? dayEnd.AddMinutes(-1) : end;
            int sMin = (s.Hour * 60 + s.Minute) / 30 * 30;
            int eMin = (int)Math.Ceiling((e.Hour * 60 + e.Minute) / 30.0) * 30;
            if (eMin >= 24 * 60) eMin = 24 * 60 - 1;
            if (eMin <= sMin) continue;
            yield return ($"{sMin / 60:D2}:{sMin % 60:D2}", $"{eMin / 60:D2}:{eMin % 60:D2}", "Agenda externa: " + title);
        }
    }
}

/// <summary>Leitor mínimo de arquivos iCalendar (.ics): eventos simples com DTSTART/DTEND (sem expandir recorrências).</summary>
public static class IcsParser
{
    public static List<(DateTime Start, DateTime End, string Title)> Parse(string ics, DateTime from, DateTime to)
    {
        var result = new List<(DateTime, DateTime, string)>();
        // "Desdobra" linhas continuadas (RFC 5545: linha que começa com espaço continua a anterior)
        var lines = new List<string>();
        foreach (var raw in ics.Replace("\r\n", "\n").Split('\n'))
        {
            if ((raw.StartsWith(' ') || raw.StartsWith('\t')) && lines.Count > 0) lines[^1] += raw[1..];
            else lines.Add(raw);
        }

        DateTime? start = null, end = null;
        string title = "Ocupado";
        bool inEvent = false, transparent = false, cancelled = false;
        foreach (var line in lines)
        {
            if (line == "BEGIN:VEVENT") { inEvent = true; start = end = null; title = "Ocupado"; transparent = cancelled = false; continue; }
            if (line == "END:VEVENT")
            {
                if (inEvent && start.HasValue && !transparent && !cancelled)
                {
                    var e = end ?? start.Value.AddHours(1);
                    if (e > from && start.Value < to) result.Add((start.Value, e, title));
                }
                inEvent = false;
                continue;
            }
            if (!inEvent) continue;

            int colon = line.IndexOf(':');
            if (colon <= 0) continue;
            string name = line[..colon], value = line[(colon + 1)..].Trim();
            string prop = name.Split(';')[0].ToUpperInvariant();
            switch (prop)
            {
                case "DTSTART": start = ParseDate(value, name); break;
                case "DTEND": end = ParseDate(value, name); break;
                case "SUMMARY": title = value.Length > 60 ? value[..60] : value; break;
                case "TRANSP": transparent = value.Equals("TRANSPARENT", StringComparison.OrdinalIgnoreCase); break;
                case "STATUS": cancelled = value.Equals("CANCELLED", StringComparison.OrdinalIgnoreCase); break;
            }
        }
        return result;
    }

    private static DateTime? ParseDate(string value, string nameWithParams)
    {
        if (nameWithParams.Contains("VALUE=DATE", StringComparison.OrdinalIgnoreCase) || value.Length == 8)
        {
            return DateTime.TryParseExact(value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : null;
        }
        if (value.EndsWith('Z'))
        {
            return DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var u) ? u.ToLocalTime() : null;
        }
        // Com TZID ou "flutuante": tratado como horário local da loja
        return DateTime.TryParseExact(value, "yyyyMMdd'T'HHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var l) ? l : null;
    }
}

/// <summary>Limites por plano SaaS da loja (item 74). Planos desconhecidos usam o limite "Pro".</summary>
public static class SaasPlanLimits
{
    public sealed record Limits(string Tier, int MaxBarbers, int MaxCampaignsPerMonth, bool PublicBooking, bool Stock);

    public static Limits For(string? planName)
    {
        var n = (planName ?? "").ToLowerInvariant();
        if (n.Contains("básico") || n.Contains("basico") || n.Contains("basic") || n.Contains("start"))
            return new Limits("Básico", 2, 2, false, false);
        if (n.Contains("premium") || n.Contains("ilimitado") || n.Contains("enterprise") || n.Contains("rede"))
            return new Limits("Premium", 50, 100, true, true);
        return new Limits("Pro", 10, 20, true, true);
    }
}

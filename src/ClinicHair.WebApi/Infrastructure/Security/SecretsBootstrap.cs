using System.Security.Cryptography;
using System.Text.Json;

namespace ClinicHair.WebApi.Infrastructure.Security;

/// <summary>
/// Carrega os segredos da aplicação fora do código e do appsettings.json.
/// Precedência: variáveis de ambiente (Jwt__SecretKey, WhatsAppBridge__SharedSecret) &gt; storage/secrets.json.
/// Na primeira execução o arquivo storage/secrets.json é criado com uma chave JWT aleatória.
/// O segredo do bridge é preservado a partir da configuração existente para não quebrar o processo Node já em uso.
/// </summary>
public static class SecretsBootstrap
{
    private const string JwtKey = "Jwt:SecretKey";
    private const string BridgeKey = "WhatsAppBridge:SharedSecret";

    // Valor histórico usado pelo bridge Node quando BRIDGE_SHARED_SECRET não é informado (mantém compatibilidade).
    private const string LegacyBridgeSecret = "ClinicHair@BridgeHmacSecret2026!";

    public static void Apply(ConfigurationManager configuration, string storageRoot)
    {
        var path = Path.Combine(storageRoot, "secrets.json");
        var file = Read(path);
        bool dirty = false;

        if (string.IsNullOrWhiteSpace(file.JwtSecretKey))
        {
            file.JwtSecretKey = Convert.ToBase64String(RandomNumberGenerator.GetBytes(48));
            dirty = true;
        }

        if (string.IsNullOrWhiteSpace(file.BridgeSharedSecret))
        {
            file.BridgeSharedSecret = configuration[BridgeKey] is { Length: > 0 } existing ? existing : LegacyBridgeSecret;
            dirty = true;
        }

        if (dirty)
        {
            Directory.CreateDirectory(storageRoot);
            File.WriteAllText(path, JsonSerializer.Serialize(file, new JsonSerializerOptions { WriteIndented = true }));
        }

        var overrides = new Dictionary<string, string?>();
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("Jwt__SecretKey")))
        {
            overrides[JwtKey] = file.JwtSecretKey;
        }
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("WhatsAppBridge__SharedSecret")))
        {
            overrides[BridgeKey] = file.BridgeSharedSecret;
        }

        if (overrides.Count > 0)
        {
            configuration.AddInMemoryCollection(overrides);
        }
    }

    private static SecretsFile Read(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<SecretsFile>(File.ReadAllText(path)) ?? new SecretsFile();
            }
        }
        catch
        {
            // Arquivo corrompido: gera novos valores (a chave JWT nova apenas exige novo login)
        }
        return new SecretsFile();
    }

    private sealed class SecretsFile
    {
        public string? JwtSecretKey { get; set; }
        public string? BridgeSharedSecret { get; set; }
    }
}

/// <summary>Resolve a pasta de dados (banco, backups, avaliações, logs). Permite isolar instâncias de teste.</summary>
public static class StoragePaths
{
    public static string Root(IHostEnvironment env, IConfiguration config)
    {
        var fromEnv = Environment.GetEnvironmentVariable("CLINICHAIR_STORAGE_ROOT");
        if (!string.IsNullOrWhiteSpace(fromEnv)) return fromEnv;
        var fromConfig = config["Storage:Root"];
        if (!string.IsNullOrWhiteSpace(fromConfig)) return fromConfig;
        return Path.Combine(env.ContentRootPath, "storage");
    }

    public static bool WorkersDisabled(IConfiguration config)
        => string.Equals(Environment.GetEnvironmentVariable("CLINICHAIR_DISABLE_WORKERS"), "1", StringComparison.Ordinal)
           || config.GetValue<bool>("Workers:Disabled");
}

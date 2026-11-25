using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ClinicHair.Tests;

/// <summary>
/// Sobe a API inteira em memória contra um banco SQLite descartável numa pasta temporária.
/// Nunca usa o storage real nem o MySQL, não liga os workers e troca o WhatsApp Bridge por um falso.
/// </summary>
public sealed class TestApp : WebApplicationFactory<Program>
{
    public string StorageRoot { get; }
    public FakeBridge Bridge { get; } = new();

    public TestApp()
    {
        StorageRoot = Path.Combine(Path.GetTempPath(), "clinichair-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(StorageRoot);
        Environment.SetEnvironmentVariable("CLINICHAIR_STORAGE_ROOT", StorageRoot);
        Environment.SetEnvironmentVariable("CLINICHAIR_DISABLE_WORKERS", "1");
        Environment.SetEnvironmentVariable("CLINICHAIR_FORCE_SQLITE", "1");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseSetting("CLINICHAIR_FORCE_SQLITE", "1");
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IWhatsAppBridgeClient>();
            services.AddSingleton<IWhatsAppBridgeClient>(Bridge);
        });
    }

    /// <summary>Cria uma loja com um administrador de senha aleatória (nada fixo no código) e devolve o login.</summary>
    public async Task<(int StoreId, string Username, string Password)> CreateStoreAdminAsync(
        string role = "Admin", bool mustChange = false, string? totpSecret = null)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicHairDbContext>();
        var store = new Store { Name = "Loja Teste " + Guid.NewGuid().ToString("N")[..6] };
        db.Stores.Add(store);
        await db.SaveChangesAsync();

        var username = "u" + Guid.NewGuid().ToString("N")[..10];
        var password = "T" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)) + "x9";
        var user = new AdminUser { StoreId = store.Id, Username = username, Role = role, MustChangePassword = mustChange, TotpSecret = totpSecret, TotpEnabled = totpSecret != null };
        user.PasswordHash = new PasswordHasher<AdminUser>().HashPassword(user, password);
        db.AdminUsers.Add(user);
        await db.SaveChangesAsync();
        return (store.Id, username, password);
    }

    public async Task<HttpClient> LoginAsync(string username, string password, string? totp = null)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var res = await client.PostAsJsonAsync("/api/auth/login", new { username, password, totpCode = totp });
        res.EnsureSuccessStatusCode();
        var auth = await res.Content.ReadFromJsonAsync<AuthResponse>();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", auth!.Token);
        return client;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        try { Directory.Delete(StorageRoot, recursive: true); } catch { /* arquivo ainda aberto: a pasta temporária é limpa pelo sistema */ }
    }
}

/// <summary>WhatsApp falso: registra tudo o que o bot "enviaria".</summary>
public sealed class FakeBridge : IWhatsAppBridgeClient
{
    public ConcurrentQueue<(string To, string Kind, string Body)> Sent { get; } = new();

    public Task<BridgeSendMessageResponse> SendPollAsync(string recipientJid, string question, IReadOnlyList<string> options, CancellationToken ct = default)
    {
        Sent.Enqueue((recipientJid, "poll", question + " | " + string.Join(" / ", options)));
        return Task.FromResult(new BridgeSendMessageResponse(Guid.NewGuid().ToString("N"), true));
    }

    public Task<BridgeSendMessageResponse> SendTextAsync(string recipientJid, string text, CancellationToken ct = default)
    {
        Sent.Enqueue((recipientJid, "text", text));
        return Task.FromResult(new BridgeSendMessageResponse(Guid.NewGuid().ToString("N"), true));
    }

    public Task<BridgeLiveStatusDto?> GetBridgeLiveStatusAsync(CancellationToken ct = default)
        => Task.FromResult<BridgeLiveStatusDto?>(new BridgeLiveStatusDto("CONNECTED", "5511900000000", null, null, null, null));

    public Task<BridgeLiveStatusDto?> StartBridgeSessionAsync(CancellationToken ct = default) => GetBridgeLiveStatusAsync(ct);
    public Task<BridgeLiveStatusDto?> RequestPairingCodeAsync(string phoneNumber, CancellationToken ct = default) => GetBridgeLiveStatusAsync(ct);
    public Task<bool> LogoutBridgeSessionAsync(CancellationToken ct = default) => Task.FromResult(true);
}

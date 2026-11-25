using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ClinicHair.WebApi.Contracts;
using ClinicHair.WebApi.Domain.Entities;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Services;
using ClinicHair.WebApi.Services.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ClinicHair.Tests;

/// <summary>Autorização e isolamento entre lojas (item 7 do plano), 2FA, página pública e fluxo do bot.</summary>
public sealed class SecurityAndFeatureTests(TestApp app) : IClassFixture<TestApp>
{
    [Fact]
    public void Storage_is_an_isolated_temp_folder()
    {
        Assert.StartsWith(Path.GetTempPath(), app.StorageRoot);
        Assert.Equal(app.StorageRoot, Environment.GetEnvironmentVariable("CLINICHAIR_STORAGE_ROOT"));
    }

    [Fact]
    public async Task Health_is_public()
    {
        var res = await app.CreateClient().GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
    }

    [Theory]
    [InlineData("/api/dashboard/plans")]
    [InlineData("/api/dashboard/stock")]
    [InlineData("/api/dashboard/search?q=ab")]
    [InlineData("/api/dashboard/bot/settings")]
    public async Task Dashboard_requires_login(string url)
    {
        var res = await app.CreateClient().GetAsync(url);
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [Fact]
    public async Task SuperAdmin_area_does_not_exist_for_store_users_or_anonymous()
    {
        Assert.Equal(HttpStatusCode.NotFound, (await app.CreateClient().GetAsync("/api/superadmin/overview")).StatusCode);
        var (_, user, pass) = await app.CreateStoreAdminAsync();
        var client = await app.LoginAsync(user, pass);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/superadmin/overview")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/superadmin/signups")).StatusCode);
    }

    [Fact]
    public async Task SuperAdmin_can_reach_superadmin_area()
    {
        var (_, user, pass) = await app.CreateStoreAdminAsync(role: "SuperAdmin");
        var client = await app.LoginAsync(user, pass);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/superadmin/signups")).StatusCode);
    }

    [Fact]
    public async Task Store_user_cannot_see_or_change_another_store_data()
    {
        var (storeA, userA, passA) = await app.CreateStoreAdminAsync();
        var (_, userB, passB) = await app.CreateStoreAdminAsync();
        var a = await app.LoginAsync(userA, passA);
        var b = await app.LoginAsync(userB, passB);

        var created = await a.PostAsJsonAsync("/api/dashboard/stock", new UpsertStockItemRequest("Pomada A", null, 1, 10, 25, true, 5));
        created.EnsureSuccessStatusCode();
        var item = await created.Content.ReadFromJsonAsync<StockItemDto>();

        var listB = await b.GetFromJsonAsync<List<StockItemDto>>("/api/dashboard/stock");
        Assert.DoesNotContain(listB!, i => i.Id == item!.Id);

        // Mesmo forjando o cabeçalho de loja-alvo, um usuário comum continua preso à própria loja
        var forged = new HttpRequestMessage(HttpMethod.Get, "/api/dashboard/stock");
        forged.Headers.Add("X-Target-Store-Id", storeA.ToString());
        var forgedRes = await b.SendAsync(forged);
        var forgedList = await forgedRes.Content.ReadFromJsonAsync<List<StockItemDto>>();
        Assert.DoesNotContain(forgedList!, i => i.Id == item!.Id);

        var edit = await b.PutAsJsonAsync($"/api/dashboard/stock/{item!.Id}", new UpsertStockItemRequest("Invadido", null, 1, 1, 1));
        Assert.Equal(HttpStatusCode.NotFound, edit.StatusCode);
        var move = await b.PostAsJsonAsync($"/api/dashboard/stock/{item.Id}/move", new StockMovementRequest(-5, "Venda"));
        Assert.Equal(HttpStatusCode.NotFound, move.StatusCode);
    }

    [Fact]
    public async Task Password_change_is_enforced_before_using_the_panel()
    {
        var (_, user, pass) = await app.CreateStoreAdminAsync(mustChange: true);
        var client = await app.LoginAsync(user, pass);
        var res = await client.GetAsync("/api/dashboard/plans");
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
        Assert.Contains("PASSWORD_CHANGE_REQUIRED", await res.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Totp_is_required_when_enabled()
    {
        var secret = Totp.NewSecret();
        var (_, user, pass) = await app.CreateStoreAdminAsync(role: "SuperAdmin", totpSecret: secret);
        var anon = app.CreateClient();

        var noCode = await anon.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass });
        Assert.Equal(HttpStatusCode.Unauthorized, noCode.StatusCode);
        Assert.Contains("TOTP_REQUIRED", await noCode.Content.ReadAsStringAsync());

        var wrong = await anon.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass, totpCode = "000000" });
        if (Totp.Verify(secret, "000000")) return; // coincidência rara: o código errado é o válido
        Assert.Equal(HttpStatusCode.Unauthorized, wrong.StatusCode);

        var code = Totp.Compute(Totp.Base32Decode(secret), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        var ok = await anon.PostAsJsonAsync("/api/auth/login", new { username = user, password = pass, totpCode = code });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [Fact]
    public async Task Public_page_hides_unknown_stores_and_blocks_bots()
    {
        var anon = app.CreateClient();
        Assert.Equal(HttpStatusCode.NotFound, (await anon.GetAsync("/api/public/stores/nao-existe-123")).StatusCode);

        var (storeId, user, pass) = await app.CreateStoreAdminAsync();
        var admin = await app.LoginAsync(user, pass);
        var slug = "teste-" + storeId;
        var settings = await admin.GetFromJsonAsync<ExtensionSettingsDto>("/api/dashboard/extensions/settings");
        var save = await admin.PutAsJsonAsync("/api/dashboard/extensions/settings", settings! with { PublicSlug = slug, PublicBookingEnabled = true });
        save.EnsureSuccessStatusCode();

        var page = await anon.GetAsync($"/api/public/stores/{slug}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);

        var bot = await anon.PostAsJsonAsync($"/api/public/stores/{slug}/book",
            new PublicBookingRequest("Robô", "11999998888", 1, 0, DateTime.Now.ToString("yyyy-MM-dd"), "10:00", "http://spam"));
        Assert.Equal(HttpStatusCode.BadRequest, bot.StatusCode);
    }

    [Fact]
    public async Task Bot_answers_a_new_customer_and_subscription_commands()
    {
        using var scope = app.Services.CreateScope();
        var orchestrator = scope.ServiceProvider.GetRequiredService<IWhatsAppOrchestratorService>();
        var db = scope.ServiceProvider.GetRequiredService<ClinicHairDbContext>();
        var store = await db.Stores.AsTracking().OrderBy(s => s.Id).FirstAsync();
        store.BotPaused = false;
        store.BotScheduleEnabled = false;
        store.BotPauseOutsideOpeningHours = false;
        await db.SaveChangesAsync();

        var phone = "55119" + Random.Shared.Next(10000000, 99999999);
        int before = app.Bridge.Sent.Count;
        await orchestrator.ProcessWebhookEventAsync(new WhatsAppWebhookEventPayload
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "message.text",
            SenderJid = phone + "@s.whatsapp.net",
            TextMessage = "ASSINATURA",
            Timestamp = DateTimeOffset.UtcNow
        });
        Assert.True(app.Bridge.Sent.Count > before, "o bot deveria responder ao comando ASSINATURA");
        Assert.Contains(app.Bridge.Sent.Skip(before), m => m.To.StartsWith(phone) && m.Body.Contains("assinatura", StringComparison.OrdinalIgnoreCase));

        var phone2 = "55119" + Random.Shared.Next(10000000, 99999999);
        before = app.Bridge.Sent.Count;
        await orchestrator.ProcessWebhookEventAsync(new WhatsAppWebhookEventPayload
        {
            EventId = Guid.NewGuid().ToString("N"),
            EventType = "message.text",
            SenderJid = phone2 + "@s.whatsapp.net",
            TextMessage = "oi",
            Timestamp = DateTimeOffset.UtcNow
        });
        Assert.Contains(app.Bridge.Sent.Skip(before), m => m.To.StartsWith(phone2) && m.Kind == "poll");
    }
}

/// <summary>Testes de unidade das peças sem banco.</summary>
public sealed class UnitTests
{
    [Fact]
    public void Pix_crc16_matches_reference_vector()
        => Assert.Equal(0x29B1, PixBrCode.Crc16("123456789"));

    [Fact]
    public void Pix_payload_is_well_formed()
    {
        var code = PixBrCode.Build("chave@exemplo.com", "Barbearia São João", "São Paulo", 49.9m, "ASS1-2");
        Assert.StartsWith("000201", code);
        Assert.Contains("br.gov.bcb.pix", code);
        Assert.Contains("540549.90", code);
        Assert.Contains("BARBEARIA SAO JOAO", code);
        Assert.Equal(PixBrCode.Crc16(code[..^4]).ToString("X4"), code[^4..]);
    }

    [Fact]
    public void Totp_matches_rfc6238_vector()
        => Assert.Equal("287082", Totp.Compute(Encoding.ASCII.GetBytes("12345678901234567890"), 59 / 30));

    [Fact]
    public void Totp_roundtrip()
    {
        var s = Totp.NewSecret();
        var code = Totp.Compute(Totp.Base32Decode(s), DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 30);
        Assert.True(Totp.Verify(s, code));
        Assert.False(Totp.Verify(s, "12345"));
    }

    [Fact]
    public void Ics_events_become_busy_blocks()
    {
        var ics = "BEGIN:VCALENDAR\r\nBEGIN:VEVENT\r\nDTSTART:20261125T140000\r\nDTEND:20261125T151500\r\nSUMMARY:Dentista\r\nEND:VEVENT\r\n" +
                  "BEGIN:VEVENT\r\nDTSTART:20261125T180000\r\nDTEND:20261125T190000\r\nTRANSP:TRANSPARENT\r\nSUMMARY:Livre\r\nEND:VEVENT\r\nEND:VCALENDAR";
        var events = IcsParser.Parse(ics, new DateTime(2026, 11, 1), new DateTime(2026, 12, 31));
        Assert.Single(events);
        ExternalCalendarCache.Set(9999, events);
        var busy = ExternalCalendarCache.GetBusy(9999, new DateOnly(2026, 11, 25)).ToList();
        Assert.Single(busy);
        Assert.Equal("14:00", busy[0].Start);
        Assert.Equal("15:30", busy[0].End);
        ExternalCalendarCache.Clear(9999);
    }

    [Theory]
    [InlineData("Plano Básico", 2)]
    [InlineData("Plano Pro WhatsApp", 10)]
    [InlineData("Premium Rede", 50)]
    public void Saas_limits_by_plan(string plan, int maxBarbers)
        => Assert.Equal(maxBarbers, SaasPlanLimits.For(plan).MaxBarbers);

    [Fact]
    public void Slugify_strips_accents_and_symbols()
        => Assert.Equal("clinic-hair-sao-paulo", ExtensionSettingsService.Slugify("  Clinic Hair — São Paulo!! "));
}

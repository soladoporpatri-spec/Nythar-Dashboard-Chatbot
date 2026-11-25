using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using ClinicHair.WebApi.Endpoints;
using ClinicHair.WebApi.Infrastructure.Data;
using ClinicHair.WebApi.Infrastructure.Security;
using ClinicHair.WebApi.Infrastructure.Storage;
using ClinicHair.WebApi.Middleware;
using ClinicHair.WebApi.Services;
using ClinicHair.WebApi.Services.Platform;
using ClinicHair.WebApi.Workers;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);

// Estabilidade: uma falha em worker de segundo plano NUNCA derruba a API inteira (padrão do .NET é StopHost).
// Cada worker já captura e registra seus próprios erros e continua no próximo ciclo.
builder.Services.Configure<HostOptions>(options =>
{
    options.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
    options.ShutdownTimeout = TimeSpan.FromSeconds(20);
});

// Configuração de JSON e Enums como string (dotnet-webapi skill)
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
    options.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

// Validação nativa do .NET 10 para Minimal APIs e OpenAPI nativo
builder.Services.AddValidation();
builder.Services.AddOpenApi();
builder.Services.AddExceptionHandler<ApiExceptionHandler>();
builder.Services.AddProblemDetails();

// Detecção inteligente do MySQL 8.4 LTS (porta 3306) com fallback local persistente (caminho absoluto) para SQLite
var mysqlConn = builder.Configuration.GetConnectionString("MySql")!;
var storageRoot = StoragePaths.Root(builder.Environment, builder.Configuration);
Directory.CreateDirectory(storageRoot);
// Segredos (chave JWT e segredo do bridge) ficam em variáveis de ambiente ou storage/secrets.json, nunca no código
SecretsBootstrap.Apply(builder.Configuration, storageRoot);
bool workersDisabled = StoragePaths.WorkersDisabled(builder.Configuration);
var sqliteAbsPath = Path.Combine(storageRoot, "clinichair_local.db");
// CLINICHAIR_FORCE_SQLITE=1 (testes e cópias isoladas) nunca toca no MySQL, mesmo se a porta 3306 estiver aberta
bool isMySqlReachable = builder.Configuration["CLINICHAIR_FORCE_SQLITE"] != "1" && CanConnectTcp("127.0.0.1", 3306, timeoutMs: 400);

builder.Services.AddDbContext<ClinicHairDbContext>(options =>
{
    if (isMySqlReachable)
    {
        options.UseMySQL(mysqlConn);
    }
    else
    {
        options.UseSqlite($"Data Source={sqliteAbsPath};Default Timeout=30"); // espera até 30s por lock em vez de falhar
    }
});

// Autenticação JWT (suporta tanto Header Authorization Bearer quanto Cookie HttpOnly "ClinicHairAuth")
var jwtSecret = builder.Configuration["Jwt:SecretKey"]
    ?? throw new InvalidOperationException("Jwt:SecretKey não configurado.");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = "ClinicHair.WebApi",
            ValidateAudience = true,
            ValidAudience = "ClinicHair.Dashboard",
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtSecret)),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2)
        };

        options.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                if (string.IsNullOrEmpty(context.Token))
                {
                    if (context.Request.Cookies.TryGetValue("ClinicHairAuth", out var cookieToken))
                    {
                        context.Token = cookieToken;
                    }
                    else if (context.Request.Query.TryGetValue("access_token", out var queryToken))
                    {
                        context.Token = queryToken;
                    }
                }
                return Task.CompletedTask;
            },
            // Sessões encerradas (logout) ou anteriores a uma troca de senha deixam de valer imediatamente
            OnTokenValidated = async context =>
            {
                var accounts = context.HttpContext.RequestServices.GetRequiredService<IAccountService>();
                if (context.Principal is null || !await accounts.IsTokenStillValidAsync(context.Principal, context.HttpContext.RequestAborted))
                {
                    context.Fail("Sessão encerrada.");
                }
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SuperAdminOnly", policy => policy.RequireRole("SuperAdmin"));
});

// Rate Limiting contra ataques de força bruta no Login da Dashboard
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login-limit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "local",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
    // Página pública e onboarding: anônimos, então com limite por IP
    options.AddPolicy("public-limit", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "local",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 40,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// Logs estruturados em JSON (item 89), opcional: CLINICHAIR_JSON_LOGS=1
if (builder.Configuration["CLINICHAIR_JSON_LOGS"] == "1")
{
    builder.Logging.ClearProviders();
    builder.Logging.AddJsonConsole(o =>
    {
        o.IncludeScopes = true;
        o.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffzzz ";
    });
}

builder.Services.AddHttpClient<IWhatsAppBridgeClient, WhatsAppBridgeClient>(client =>
{
    var bridgeBaseUrl = builder.Configuration["WhatsAppBridge:BaseUrl"] ?? "http://127.0.0.1:3100";
    client.BaseAddress = new Uri(bridgeBaseUrl);
    client.Timeout = TimeSpan.FromSeconds(15);
});

builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<ICurrentStore, CurrentStore>();
builder.Services.AddScoped<IAuditLogger, AuditLogger>();
builder.Services.AddScoped<IAccountService, AccountService>();
builder.Services.AddFeatureServices();
builder.Services.AddExtensionServices();
builder.Services.AddSingleton<IDashboardRealtimeNotifier, DashboardRealtimeNotifier>();
builder.Services.AddScoped<IEvaluationTxtStorageService, EvaluationTxtStorageService>();
builder.Services.AddScoped<ICustomerDirectory, CustomerDirectory>();
builder.Services.AddScoped<IWhatsAppOrchestratorService, WhatsAppOrchestratorService>();
builder.Services.AddScoped<IDashboardService, DashboardService>();
builder.Services.AddScoped<ISuperAdminService, SuperAdminService>();
if (!workersDisabled)
{
    builder.Services.AddHostedService<WhatsAppBridgeSupervisorWorker>();
    builder.Services.AddHostedService<EvaluationStorageCleanupWorker>();
    builder.Services.AddHostedService<Reminder1hWorker>();
    builder.Services.AddHostedService<Evaluation2hWorker>();
    builder.Services.AddHostedService<DatabaseBackupWorker>();
    builder.Services.AddHostedService<EngagementWorker>();
    builder.Services.AddHostedService<CalendarSyncWorker>();
    builder.Services.AddHostedService<MaintenanceWorker>();
}
builder.Services.AddHttpClient("calendar", c =>
{
    c.Timeout = TimeSpan.FromSeconds(10);
    c.MaxResponseContentBufferSize = 2 * 1024 * 1024;
});

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRateLimiter();

app.UseDefaultFiles();
var contentTypeProvider = new Microsoft.AspNetCore.StaticFiles.FileExtensionContentTypeProvider();
contentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypeProvider
});

app.UseAuthentication();
app.UseMiddleware<PasswordChangeGateMiddleware>();
app.UseMiddleware<SuperAdminIsolationMiddleware>();
app.UseAuthorization();
app.UseMiddleware<IdempotencyMiddleware>();

// Documentação OpenAPI (item 93): aberta em desenvolvimento; em produção só para o SuperAdmin
if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}
else
{
    app.MapOpenApi().RequireAuthorization("SuperAdminOnly");
}

// Health checks anônimos (usados pelo watchdog externo e monitoramento)
app.MapGet("/health/live", () => Results.Ok(new { status = "alive", timeUtc = DateTimeOffset.UtcNow }))
    .ExcludeFromDescription();

app.MapGet("/health/ready", async (ClinicHairDbContext db, IWhatsAppBridgeClient bridge, CancellationToken ct) =>
{
    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
    timeout.CancelAfter(TimeSpan.FromSeconds(3));

    bool dbOk;
    try { dbOk = await db.Database.CanConnectAsync(timeout.Token); }
    catch { dbOk = false; }

    var bridgeStatus = await bridge.GetBridgeLiveStatusAsync(timeout.Token);
    var body = new
    {
        status = dbOk ? "ready" : "degraded",
        database = dbOk ? "ok" : "down",
        whatsappBridge = bridgeStatus is null ? "offline" : "online",
        whatsapp = bridgeStatus?.Status ?? "UNKNOWN"
    };
    return dbOk ? Results.Ok(body) : Results.Json(body, statusCode: StatusCodes.Status503ServiceUnavailable);
}).ExcludeFromDescription();

app.MapDashboardEndpoints();
app.MapWhatsAppWebhookEndpoints();
app.MapFeatureEndpoints();
app.MapExtensionEndpoints();

// Inicializa e popula a loja Clinic Hair, Admin Itamar (311208), Serviços e Funcionários
await ClinicHairSeeder.SeedAsync(app.Services);

// Unifica clientes duplicados (mesmo telefone em grafias diferentes) e normaliza telefones para o formato canônico
await CustomerIdentityMigration.RunAsync(app.Services, Path.Combine(storageRoot, "backups"), app.Logger);

// SQLite em modo WAL: leituras não bloqueiam escritas e o banco resiste melhor a queda de energia/processo
if (!isMySqlReachable)
{
    try
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClinicHairDbContext>();
        await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;");
        await db.Database.ExecuteSqlRawAsync("PRAGMA synchronous=NORMAL;");
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Não foi possível ativar o modo WAL no SQLite.");
    }
}

// Rede de segurança global: registra falhas não observadas em vez de perdê-las silenciosamente
TaskScheduler.UnobservedTaskException += (_, e) =>
{
    app.Logger.LogError(e.Exception, "Exceção de Task não observada (processo mantido).");
    e.SetObserved();
};

app.Run();

static bool CanConnectTcp(string host, int port, int timeoutMs)
{
    try
    {
        using var client = new TcpClient();
        var task = client.ConnectAsync(host, port);
        return task.Wait(timeoutMs) && client.Connected;
    }
    catch
    {
        return false;
    }
}

/// <summary>Exposto para os testes de integração (WebApplicationFactory).</summary>
public partial class Program;

using System.Diagnostics;
using System.Net.Sockets;

namespace ClinicHair.WebApi.Workers;

/// <summary>
/// Supervisor do microservice TypeScript (@whiskeysockets/baileys) na porta 3100.
/// - Sobe o bridge automaticamente junto com a API.
/// - Verifica a saúde real (GET /health) e não apenas se a porta está aberta.
/// - Se o processo morrer OU travar (health falhando 3x seguidas), derruba e religa sozinho.
/// - Backoff exponencial em crash-loop (5s → 60s) para não consumir CPU em falhas persistentes.
/// - Registra a saída do Node em storage/logs/bridge.log para diagnóstico.
/// </summary>
public sealed class WhatsAppBridgeSupervisorWorker(
    IWebHostEnvironment env,
    IConfiguration config,
    ILogger<WhatsAppBridgeSupervisorWorker> logger) : BackgroundService
{
    private const int BridgePort = 3100;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(2) };

    private Process? _bridgeProcess;
    private StreamWriter? _logWriter;
    private readonly object _logLock = new();
    private int _consecutiveHealthFailures;
    private int _crashStreak;
    private DateTime _lastStartUtc = DateTime.MinValue;
    private DateTime _nextAllowedStartUtc = DateTime.MinValue;
    private bool _warnedMissingDist;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Pequena espera para a API terminar de subir antes de o bridge enviar webhooks
        try { await Task.Delay(TimeSpan.FromSeconds(1), stoppingToken); } catch (OperationCanceledException) { return; }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(8));
        do
        {
            try
            {
                await CheckAndHealAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Nunca deixa o supervisor morrer
                logger.LogError(ex, "Falha inesperada no supervisor do WhatsApp Bridge (continuando).");
            }
        }
        while (await SafeWaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }

    private async Task CheckAndHealAsync(CancellationToken ct)
    {
        var portOpen = IsPortListening("127.0.0.1", BridgePort);

        if (!portOpen)
        {
            _consecutiveHealthFailures = 0;
            TryStartBridge();
            return;
        }

        if (await IsBridgeHealthyAsync(ct))
        {
            _consecutiveHealthFailures = 0;
            // Estável por mais de 2 minutos: zera o contador de crash-loop
            if (_crashStreak > 0 && DateTime.UtcNow - _lastStartUtc > TimeSpan.FromMinutes(2))
            {
                _crashStreak = 0;
            }
            return;
        }

        _consecutiveHealthFailures++;
        logger.LogWarning("WhatsApp Bridge não respondeu ao /health ({Count}/3).", _consecutiveHealthFailures);

        if (_consecutiveHealthFailures >= 3)
        {
            logger.LogError("WhatsApp Bridge travado (porta aberta, sem resposta). Reiniciando o processo...");
            KillBridge();
            _consecutiveHealthFailures = 0;
            await Task.Delay(TimeSpan.FromSeconds(1), ct);
            TryStartBridge();
        }
    }

    private static async Task<bool> IsBridgeHealthyAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await Http.GetAsync($"http://127.0.0.1:{BridgePort}/health", ct);
            return resp.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private void TryStartBridge()
    {
        if (DateTime.UtcNow < _nextAllowedStartUtc)
        {
            return;
        }

        var bridgeDir = Path.GetFullPath(Path.Combine(env.ContentRootPath, "..", "ClinicHair.WhatsAppBridge"));
        var entryJs = Path.Combine(bridgeDir, "dist", "index.js");
        if (!File.Exists(entryJs))
        {
            if (!_warnedMissingDist)
            {
                _warnedMissingDist = true;
                logger.LogWarning("Bridge não compilado ({Path}). Execute 'npm run build' em src/ClinicHair.WhatsAppBridge.", entryJs);
            }
            return;
        }

        // Backoff: 5s, 10s, 20s, 40s, 60s (teto) após inícios consecutivos que morreram cedo
        var backoffSeconds = Math.Min(5 * (1 << Math.Min(_crashStreak, 4)), 60);
        _nextAllowedStartUtc = DateTime.UtcNow.AddSeconds(backoffSeconds);

        try
        {
            logger.LogInformation("Iniciando WhatsApp Bridge (Baileys) na porta {Port}...", BridgePort);
            var psi = new ProcessStartInfo
            {
                FileName = "node",
                Arguments = $"\"{entryJs}\"",
                WorkingDirectory = bridgeDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.Environment["BRIDGE_SHARED_SECRET"] = config["WhatsAppBridge:SharedSecret"] ?? throw new InvalidOperationException("WhatsAppBridge:SharedSecret não configurado.");

            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => WriteBridgeLog(e.Data);
            proc.ErrorDataReceived += (_, e) => WriteBridgeLog(e.Data);
            proc.Exited += (_, _) =>
            {
                var aliveFor = DateTime.UtcNow - _lastStartUtc;
                if (aliveFor < TimeSpan.FromSeconds(30))
                {
                    Interlocked.Increment(ref _crashStreak);
                }
                logger.LogWarning("Processo do WhatsApp Bridge encerrou (ativo por {Seconds:n0}s). O supervisor religará automaticamente.", aliveFor.TotalSeconds);
            };

            _lastStartUtc = DateTime.UtcNow;
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            _bridgeProcess = proc;
        }
        catch (Exception ex)
        {
            _crashStreak++;
            logger.LogWarning(ex, "Não foi possível iniciar o processo Node do WhatsApp Bridge.");
        }
    }

    private void KillBridge()
    {
        try
        {
            if (_bridgeProcess is { HasExited: false })
            {
                _bridgeProcess.Kill(entireProcessTree: true);
                return;
            }
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Falha ao encerrar o processo filho do bridge.");
        }

        // Bridge iniciado por outro meio (ex.: script iniciar-clinichair): derruba quem escuta na porta 3100
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var ps = Process.Start(new ProcessStartInfo
                {
                    FileName = "powershell",
                    Arguments = $"-NoProfile -Command \"Get-NetTCPConnection -LocalPort {BridgePort} -State Listen -ErrorAction SilentlyContinue | ForEach-Object {{ Stop-Process -Id $_.OwningProcess -Force -ErrorAction SilentlyContinue }}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true
                });
                ps?.WaitForExit(8000);
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "Falha ao liberar a porta {Port}.", BridgePort);
            }
        }
    }

    private void WriteBridgeLog(string? line)
    {
        if (string.IsNullOrWhiteSpace(line)) return;
        try
        {
            lock (_logLock)
            {
                if (_logWriter is null)
                {
                    var logDir = Path.Combine(ClinicHair.WebApi.Infrastructure.Security.StoragePaths.Root(env, config), "logs");
                    Directory.CreateDirectory(logDir);
                    var logPath = Path.Combine(logDir, "bridge.log");
                    // Rotação simples: acima de 5 MB, arquiva e recomeça
                    if (File.Exists(logPath) && new FileInfo(logPath).Length > 5 * 1024 * 1024)
                    {
                        File.Move(logPath, Path.Combine(logDir, "bridge.previous.log"), overwrite: true);
                    }
                    _logWriter = new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
                }
                _logWriter.WriteLine($"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}");
            }
        }
        catch
        {
            // log nunca pode derrubar o supervisor
        }
    }

    private static bool IsPortListening(string host, int port)
    {
        try
        {
            using var tcp = new TcpClient();
            var task = tcp.ConnectAsync(host, port);
            return task.Wait(350) && tcp.Connected;
        }
        catch
        {
            return false;
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_bridgeProcess is { HasExited: false })
            {
                _bridgeProcess.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Ignora ao encerrar
        }

        lock (_logLock)
        {
            _logWriter?.Dispose();
            _logWriter = null;
        }

        await base.StopAsync(cancellationToken);
    }
}

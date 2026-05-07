using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Options;

namespace MangosSuperUI.Services;

public class RaService : IDisposable
{
    private TcpClient? _client;
    private NetworkStream? _stream;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private readonly RemoteAccessSettings _settings;
    private readonly ILogger<RaService> _logger;
    private bool _alive;              // OUR liveness flag — only set false on actual I/O failure
    private Timer? _keepAliveTimer;
    private DateTime _lastCommandUtc = DateTime.MinValue;

    /// <summary>
    /// Application-level keepalive interval (seconds).
    /// This is the SOLE mechanism keeping the connection alive.
    /// We do NOT use OS-level TCP keepalive because on Linux, the kernel's
    /// keepalive probes cause TcpClient.Connected to return false, which
    /// was triggering spurious reconnects.
    /// </summary>
    private const int KeepAliveIntervalSec = 25;

    public bool IsConnected => _alive;

    public RaService(IOptions<RemoteAccessSettings> settings, ILogger<RaService> logger)
    {
        _settings = settings.Value;
        _logger = logger;
    }

    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        await _semaphore.WaitAsync(ct);
        try
        {
            await EnsureConnectedAsync(ct);

            // Drain any stale bytes left in the buffer from a previous
            // read that returned on idle-timeout without consuming the prompt
            DrainStaleData();

            var cmdBytes = Encoding.UTF8.GetBytes(command + "\n");
            await _stream!.WriteAsync(cmdBytes, ct);
            await _stream.FlushAsync(ct);

            var raw = await ReadResponseAsync(ct);
            _lastCommandUtc = DateTime.UtcNow;
            return CleanResponse(raw);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RA command failed: {Command}", command);
            Disconnect();
            throw;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private async Task EnsureConnectedAsync(CancellationToken ct)
    {
        if (_alive && _stream != null)
            return;

        Disconnect();

        _logger.LogInformation("Connecting to RA at {Host}:{Port}", _settings.Host, _settings.Port);

        _client = new TcpClient
        {
            NoDelay = true
        };
        await _client.ConnectAsync(_settings.Host, _settings.Port, ct);
        _stream = _client.GetStream();

        // Read the welcome banner
        await ReadResponseAsync(ct);

        // Send username
        await WriteLineAsync(_settings.Username, ct);

        // Read password prompt
        var authResponse = await ReadResponseAsync(ct);
        _logger.LogInformation("RA auth response: {Response}", authResponse);

        // Send password
        await WriteLineAsync(_settings.Password, ct);

        // Read post-auth response (+Logged in.\r\nmangos>)
        await ReadResponseAsync(ct);

        _alive = true;
        _lastCommandUtc = DateTime.UtcNow;
        _logger.LogInformation("RA connection established and authenticated");

        StartKeepAlive();
    }

    private async Task WriteLineAsync(string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text + "\n");
        await _stream!.WriteAsync(bytes, ct);
        await _stream.FlushAsync(ct);
    }

    private void StartKeepAlive()
    {
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = new Timer(KeepAliveTick, null,
            TimeSpan.FromSeconds(KeepAliveIntervalSec),
            TimeSpan.FromSeconds(KeepAliveIntervalSec));
    }

    private async void KeepAliveTick(object? state)
    {
        // Skip if a real command was sent recently
        var elapsed = DateTime.UtcNow - _lastCommandUtc;
        if (elapsed.TotalSeconds < KeepAliveIntervalSec - 5)
            return;

        // Non-blocking acquire — if a real command is in flight, skip
        if (!await _semaphore.WaitAsync(0))
            return;

        try
        {
            if (!_alive || _stream == null)
                return;

            // Drain stale bytes, send newline, read prompt
            DrainStaleData();

            await WriteLineAsync("", default);

            await ReadResponseAsync(default);
            _lastCommandUtc = DateTime.UtcNow;

            _logger.LogDebug("RA keepalive OK");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "RA keepalive failed — will reconnect on next command");
            Disconnect();
        }
        finally
        {
            _semaphore.Release();
        }
    }

    /// <summary>
    /// Synchronously drains any bytes sitting in the receive buffer.
    /// Prevents protocol desync from a previous ReadResponseAsync that
    /// returned on idle-timeout without consuming the full prompt.
    /// </summary>
    private void DrainStaleData()
    {
        if (_stream == null)
            return;

        try
        {
            while (_stream.DataAvailable)
            {
                var junk = new byte[4096];
                var n = _stream.Read(junk, 0, junk.Length);
                if (n == 0) break;
                _logger.LogDebug("RA drained {Bytes} stale bytes", n);
            }
        }
        catch
        {
            // If draining fails, the next real I/O will catch it
        }
    }

    /// <summary>
    /// Reads until the "mangos>" prompt is detected or silence is detected.
    /// Two-phase timeout: generous wait for the first byte (mangosd processes
    /// CLI commands on its world-tick), then tight timeout between chunks.
    /// </summary>
    private async Task<string> ReadResponseAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[4096];
        var overallTimeoutMs = _settings.CommandTimeoutMs;

        using var overallCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        overallCts.CancelAfter(overallTimeoutMs);

        var isFirstRead = true;

        while (true)
        {
            // First byte: 3s (world-tick delay). Subsequent: 500ms.
            var idleMs = isFirstRead ? 3000 : 500;

            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(overallCts.Token);
            idleCts.CancelAfter(idleMs);

            try
            {
                var bytesRead = await _stream!.ReadAsync(buffer.AsMemory(0, buffer.Length), idleCts.Token);
                if (bytesRead == 0)
                    throw new IOException("RA connection closed by remote host");

                isFirstRead = false;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, bytesRead));

                // Prompt-based completion
                if (sb.ToString().Contains("mangos>"))
                    break;
            }
            catch (OperationCanceledException) when (!overallCts.Token.IsCancellationRequested)
            {
                // Idle timeout — no more data
                break;
            }
            catch (OperationCanceledException)
            {
                if (sb.Length > 0)
                    break;

                throw new TimeoutException($"RA response timed out after {overallTimeoutMs}ms");
            }
        }

        return sb.ToString().Trim();
    }

    private static string CleanResponse(string raw)
    {
        if (string.IsNullOrEmpty(raw))
            return raw;

        var cleaned = raw;
        for (var pass = 0; pass < 2; pass++)
        {
            cleaned = cleaned.TrimEnd();
            if (cleaned.EndsWith("+mangos>"))
                cleaned = cleaned[..^"+mangos>".Length];
            else if (cleaned.EndsWith("-mangos>"))
                cleaned = cleaned[..^"-mangos>".Length];
            else if (cleaned.EndsWith("mangos>"))
                cleaned = cleaned[..^"mangos>".Length];
            else
                break;
        }

        return cleaned.TrimEnd();
    }

    private void Disconnect()
    {
        _alive = false;
        _keepAliveTimer?.Dispose();
        _keepAliveTimer = null;
        try { _stream?.Dispose(); } catch { }
        try { _client?.Dispose(); } catch { }
        _stream = null;
        _client = null;
    }

    public void Dispose()
    {
        Disconnect();
        _semaphore.Dispose();
    }
}
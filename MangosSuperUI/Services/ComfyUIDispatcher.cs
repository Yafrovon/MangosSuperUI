using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace MangosSuperUI.Services;

/// <summary>
/// Load-balancing dispatcher for a pool of ComfyUI instances.
///
/// Session 26 rewrite — Channel-based work-stealing pool.
///
/// Previous design used a background monitor polling queue depths every 2s
/// and a semaphore to wake waiting tasks. This created 2-4s idle gaps between
/// jobs because: (1) the monitor only signaled one waiter per cycle even if
/// multiple slots opened, (2) the submit gate serialized all submits, and
/// (3) the fixed poll interval meant GPUs sat idle until the next tick.
///
/// New design — "token pool":
/// - Each node gets N tokens (N = MaxQueueDepthPerNode) written to a Channel.
/// - To submit, a task reads one token from the channel (blocks if none available).
/// - The token tells it which node to submit to. Submit is lock-free — no gate.
/// - When a job COMPLETES (detected via /history poll), its token is returned
///   to the channel immediately — the next waiting task wakes up in microseconds.
/// - No background polling for queue depth. No semaphore. No submit gate.
/// - If a node is temporarily unreachable, its tokens are returned after a delay
///   so they aren't lost.
///
/// Result: the gap between job completion and next submission is bounded by
/// the /history poll interval (~2s), not by monitor polling + semaphore + gate.
/// In practice it's ~0-2s because the history poll is already running.
///
/// Config in appsettings.json or server-config.json:
///   "SpellCreator": {
///     "ComfyUI": {
///       "Nodes": [
///         { "Name": "gpu1", "BaseUrl": "http://192.168.0.244:8188" },
///         { "Name": "gpu2", "BaseUrl": "http://192.168.0.201:8188" }
///       ]
///     }
///   }
/// </summary>
public class ComfyUIDispatcher : IDisposable
{
    private readonly ILogger<ComfyUIDispatcher> _logger;
    private readonly HttpClient _http;
    private readonly List<ComfyNode> _nodes = new();

    /// <summary>
    /// The token pool. Each token is a node reference. Reading a token = "I have
    /// permission to submit one job to this node." Returning a token = "this node
    /// has a free slot again."
    /// </summary>
    private readonly Channel<ComfyNode> _tokenPool;

    /// <summary>
    /// Max jobs to queue per node in ComfyUI. With cap=2, each node always has
    /// 1 running + 1 ready → zero idle gap between jobs.
    /// </summary>
    private const int MaxQueueDepthPerNode = 2;

    /// <summary>How often to poll /history/{promptId} waiting for completion.</summary>
    private static readonly TimeSpan HistoryPollInterval = TimeSpan.FromSeconds(2);

    /// <summary>Max time to wait for a single generation to complete.</summary>
    private static readonly TimeSpan MaxGenerationTime = TimeSpan.FromMinutes(5);

    /// <summary>Max time to wait for a slot token.</summary>
    private static readonly TimeSpan MaxWaitForSlot = TimeSpan.FromMinutes(15);

    /// <summary>Tracks whether we've seeded the token pool from live queue state.</summary>
    private volatile bool _poolSeeded;
    private readonly SemaphoreSlim _seedLock = new(1, 1);

    public ComfyUIDispatcher(IConfiguration config, ILogger<ComfyUIDispatcher> logger)
    {
        _logger = logger;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        var nodeConfigs = config.GetSection("SpellCreator:ComfyUI:Nodes").GetChildren().ToList();

        if (nodeConfigs.Count > 0)
        {
            foreach (var nc in nodeConfigs)
            {
                var name = nc["Name"] ?? nc["BaseUrl"] ?? "unknown";
                var url = nc["BaseUrl"];
                if (!string.IsNullOrEmpty(url))
                {
                    _nodes.Add(new ComfyNode(name, url.TrimEnd('/')));
                    _logger.LogInformation("ComfyUI Dispatcher: Registered node '{Name}' at {Url}", name, url);
                }
            }
        }

        if (_nodes.Count == 0)
        {
            var legacyUrl = config["SpellCreator:ComfyUI:BaseUrl"]?.TrimEnd('/')
                            ?? "http://localhost:8188";
            _nodes.Add(new ComfyNode("default", legacyUrl));
            _logger.LogInformation("ComfyUI Dispatcher: No Nodes[] config, using single node at {Url}", legacyUrl);
        }

        // Unbounded channel — we know exactly how many tokens exist (nodes × cap)
        _tokenPool = Channel.CreateUnbounded<ComfyNode>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = false,
            AllowSynchronousContinuations = true  // wake waiters immediately
        });

        _logger.LogInformation("ComfyUI Dispatcher: {Count} node(s) in pool, max {Cap} queued per node",
            _nodes.Count, MaxQueueDepthPerNode);
    }

    /// <summary>
    /// Seed the token pool on first use. Checks each node's live queue depth and
    /// writes (MaxQueueDepthPerNode - currentDepth) tokens per node.
    /// This handles the case where ComfyUI already has queued jobs from a previous
    /// session or manual submission.
    /// </summary>
    private async Task EnsurePoolSeededAsync(CancellationToken ct)
    {
        if (_poolSeeded) return;

        await _seedLock.WaitAsync(ct);
        try
        {
            if (_poolSeeded) return;

            foreach (var node in _nodes)
            {
                int currentDepth = 0;
                try
                {
                    var depth = await GetQueueDepthAsync(node, ct);
                    currentDepth = depth ?? 0;
                }
                catch
                {
                    // Node unreachable — assume empty, tokens will fail-and-return on submit
                }

                int freeSlots = Math.Max(0, MaxQueueDepthPerNode - currentDepth);
                for (int i = 0; i < freeSlots; i++)
                    _tokenPool.Writer.TryWrite(node);

                _logger.LogInformation(
                    "ComfyUI Dispatcher: Seeded {Slots} token(s) for {Node} (live depth={Depth})",
                    freeSlots, node.Name, currentDepth);
            }

            _poolSeeded = true;
        }
        finally
        {
            _seedLock.Release();
        }
    }

    /// <summary>
    /// Submit workflow, poll for result, download image.
    /// Token-based flow: grab a token (blocks until a slot is free), submit,
    /// poll for completion, return the token so the next job can go.
    /// </summary>
    public async Task<string?> GenerateAsync(Dictionary<string, object> workflow,
        string label, string outputDir, CancellationToken ct = default)
    {
        await EnsurePoolSeededAsync(ct);

        // ── Acquire a slot token ──
        ComfyNode node;
        using (var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct))
        {
            timeoutCts.CancelAfter(MaxWaitForSlot);
            try
            {
                node = await _tokenPool.Reader.ReadAsync(timeoutCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                _logger.LogError("ComfyUI Dispatch: Timeout waiting for slot token for '{Label}'", label);
                return null;
            }
        }

        // ── Submit ──
        string? promptId = null;
        try
        {
            promptId = await SubmitToNodeAsync(node, workflow, label, ct);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ComfyUI Dispatch: Submit failed on {Node} for '{Label}', returning token", node.Name, label);
            // Return the token — this node slot is free again
            _tokenPool.Writer.TryWrite(node);
            return null;
        }

        if (promptId == null)
        {
            // POST was unsuccessful but didn't throw — return token
            _tokenPool.Writer.TryWrite(node);
            return null;
        }

        // ── Poll for result — token is returned when job completes ──
        try
        {
            return await PollForResultAsync(node, promptId, label, outputDir, ct);
        }
        finally
        {
            // ALWAYS return the token, whether the job succeeded, failed, or timed out.
            // This is the key guarantee: tokens are never lost.
            _tokenPool.Writer.TryWrite(node);
        }
    }

    /// <summary>Alias for GenerateAsync — compatibility with existing call sites.</summary>
    public Task<string?> GenerateParallelAsync(Dictionary<string, object> workflow,
        string label, string outputDir, CancellationToken ct = default)
    {
        return GenerateAsync(workflow, label, outputDir, ct);
    }

    /// <summary>
    /// Submit a workflow to a specific node. No locking — each caller has its own
    /// token so there's no contention.
    /// </summary>
    private async Task<string?> SubmitToNodeAsync(ComfyNode node,
        Dictionary<string, object> workflow, string label, CancellationToken ct)
    {
        var payload = JsonSerializer.Serialize(new { prompt = workflow });
        var content = new StringContent(payload, Encoding.UTF8, "application/json");

        var resp = await _http.PostAsync($"{node.BaseUrl}/api/prompt", content, ct);
        if (resp.IsSuccessStatusCode)
        {
            var result = JsonSerializer.Deserialize<JsonElement>(
                await resp.Content.ReadAsStringAsync(ct));
            string promptId = result.GetProperty("prompt_id").GetString()!;

            node.IncrementSubmitted();

            _logger.LogInformation(
                "ComfyUI Dispatch: Queued '{Label}' on {Node} → {PromptId}",
                label, node.Name, promptId);

            return promptId;
        }
        else
        {
            var body = await resp.Content.ReadAsStringAsync(ct);
            _logger.LogWarning(
                "ComfyUI Dispatch: POST failed on {Node}: {Status} — {Body}",
                node.Name, resp.StatusCode, body);
            return null;
        }
    }

    /// <summary>
    /// Poll /history/{promptId} until the job completes, then download the result image.
    /// </summary>
    private async Task<string?> PollForResultAsync(ComfyNode node, string promptId,
        string label, string outputDir, CancellationToken ct)
    {
        var startTime = DateTime.UtcNow;
        var deadline = startTime + MaxGenerationTime;

        while (DateTime.UtcNow < deadline && !ct.IsCancellationRequested)
        {
            await Task.Delay(HistoryPollInterval, ct);

            try
            {
                var histResp = await _http.GetAsync(
                    $"{node.BaseUrl}/history/{promptId}", ct);
                if (!histResp.IsSuccessStatusCode) continue;

                var hist = JsonSerializer.Deserialize<JsonElement>(
                    await histResp.Content.ReadAsStringAsync(ct));
                if (!hist.TryGetProperty(promptId, out var run)) continue;
                if (!run.TryGetProperty("outputs", out var outputs)) continue;

                foreach (var nodeOut in outputs.EnumerateObject())
                {
                    if (!nodeOut.Value.TryGetProperty("images", out var images)) continue;
                    foreach (var img in images.EnumerateArray())
                    {
                        string fname = img.GetProperty("filename").GetString()!;
                        string subfolder = img.TryGetProperty("subfolder", out var sf)
                            ? sf.GetString() ?? "" : "";

                        string viewUrl = $"{node.BaseUrl}/view" +
                            $"?filename={Uri.EscapeDataString(fname)}" +
                            $"&subfolder={Uri.EscapeDataString(subfolder)}&type=output";

                        var imgResp = await _http.GetAsync(viewUrl, ct);
                        if (!imgResp.IsSuccessStatusCode) continue;

                        Directory.CreateDirectory(outputDir);
                        string outPath = Path.Combine(outputDir, fname);
                        await File.WriteAllBytesAsync(outPath,
                            await imgResp.Content.ReadAsByteArrayAsync(ct), ct);

                        var elapsed = DateTime.UtcNow - startTime;
                        node.IncrementCompleted();
                        _logger.LogInformation(
                            "ComfyUI Dispatch: '{Label}' completed on {Node} in {Elapsed:F1}s → {File}",
                            label, node.Name, elapsed.TotalSeconds, fname);

                        // Token is returned in the finally block of GenerateAsync
                        return outPath;
                    }
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogDebug(ex, "ComfyUI Dispatch: Poll error for {PromptId}", promptId);
            }
        }

        _logger.LogWarning("ComfyUI Dispatch: Timeout waiting for '{Label}' on {Node}",
            label, node.Name);
        return null;
    }

    // ═══════════════════════════════════════════════════════════════════
    // HEALTH / DIAGNOSTICS
    // ═══════════════════════════════════════════════════════════════════

    /// <summary>Check if any node in the pool is reachable.</summary>
    public async Task<bool> IsAnyNodeOnlineAsync(CancellationToken ct = default)
    {
        foreach (var node in _nodes)
        {
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(3));
                var resp = await _http.GetAsync($"{node.BaseUrl}/system_stats", cts.Token);
                if (resp.IsSuccessStatusCode) return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>Get status of all nodes for diagnostics / UI.</summary>
    public async Task<List<ComfyNodeStatus>> GetPoolStatusAsync(CancellationToken ct = default)
    {
        var statuses = new List<ComfyNodeStatus>();
        foreach (var node in _nodes)
        {
            var status = new ComfyNodeStatus { Name = node.Name, BaseUrl = node.BaseUrl };
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                cts.CancelAfter(TimeSpan.FromSeconds(5));

                var resp = await _http.GetAsync($"{node.BaseUrl}/queue", cts.Token);
                if (resp.IsSuccessStatusCode)
                {
                    status.Online = true;
                    var json = JsonSerializer.Deserialize<JsonElement>(
                        await resp.Content.ReadAsStringAsync(cts.Token));

                    if (json.TryGetProperty("queue_running", out var qr))
                        status.Running = qr.GetArrayLength();
                    if (json.TryGetProperty("queue_pending", out var qp))
                        status.Pending = qp.GetArrayLength();

                    status.Busy = status.Running > 0 || status.Pending > 0;
                }

                status.TotalSubmitted = node.TotalSubmitted;
                status.TotalCompleted = node.TotalCompleted;
            }
            catch (Exception ex)
            {
                status.Error = ex.Message;
            }
            statuses.Add(status);
        }
        return statuses;
    }

    /// <summary>
    /// Get the total number of running + pending items in a node's ComfyUI queue.
    /// Returns null if the node is unreachable.
    /// </summary>
    private async Task<int?> GetQueueDepthAsync(ComfyNode node, CancellationToken ct)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(5));

            var resp = await _http.GetAsync($"{node.BaseUrl}/queue", cts.Token);
            if (!resp.IsSuccessStatusCode) return null;

            var json = JsonSerializer.Deserialize<JsonElement>(
                await resp.Content.ReadAsStringAsync(cts.Token));

            int running = 0, pending = 0;
            if (json.TryGetProperty("queue_running", out var qr))
                running = qr.GetArrayLength();
            if (json.TryGetProperty("queue_pending", out var qp))
                pending = qp.GetArrayLength();

            return running + pending;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogDebug("ComfyUI Dispatch: Node {Node} unreachable — {Err}", node.Name, ex.Message);
            return null;
        }
    }

    public void Dispose()
    {
        _tokenPool.Writer.Complete();
        _http.Dispose();
        _seedLock.Dispose();
    }
}

// ═══════════════════════════════════════════════════════════════════════
// SUPPORTING TYPES
// ═══════════════════════════════════════════════════════════════════════

/// <summary>
/// Represents one ComfyUI instance in the pool.
/// Tracks submission/completion counts for diagnostics.
/// </summary>
public class ComfyNode
{
    public string Name { get; }
    public string BaseUrl { get; }

    private int _totalSubmitted;
    private int _totalCompleted;

    public int TotalSubmitted => _totalSubmitted;
    public int TotalCompleted => _totalCompleted;

    /// <summary>Kept for API compatibility with GetPoolStatusAsync — always false now.</summary>
    public bool IsAcquired => false;

    public ComfyNode(string name, string baseUrl)
    {
        Name = name;
        BaseUrl = baseUrl;
    }

    public void IncrementSubmitted() => Interlocked.Increment(ref _totalSubmitted);
    public void IncrementCompleted() => Interlocked.Increment(ref _totalCompleted);

    public override string ToString() => $"{Name} ({BaseUrl})";
}

/// <summary>Handle for a dispatched ComfyUI job.</summary>
public class ComfyJob
{
    public string PromptId { get; set; } = "";
    public ComfyNode Node { get; set; } = null!;
    public string Label { get; set; } = "";
    public DateTime SubmittedAt { get; set; }
}

/// <summary>Diagnostic snapshot of a node's state.</summary>
public class ComfyNodeStatus
{
    public string Name { get; set; } = "";
    public string BaseUrl { get; set; } = "";
    public bool Online { get; set; }
    public bool Busy { get; set; }
    public int Running { get; set; }
    public int Pending { get; set; }
    public bool AcquiredByDispatcher { get; set; }
    public int TotalSubmitted { get; set; }
    public int TotalCompleted { get; set; }
    public string? Error { get; set; }
}
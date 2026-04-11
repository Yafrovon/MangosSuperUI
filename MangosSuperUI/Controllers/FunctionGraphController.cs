using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace MangosSuperUI.Controllers;

public class FunctionGraphController : Controller
{
    private readonly IWebHostEnvironment _env;

    private static object? _graphData;
    private static readonly object _lock = new();

    public FunctionGraphController(IWebHostEnvironment env)
    {
        _env = env;
    }

    private JsonElement? EnsureLoaded()
    {
        lock (_lock)
        {
            if (_graphData == null)
            {
                var path = Path.Combine(_env.WebRootPath, "data", "function-graph.json");
                if (!System.IO.File.Exists(path)) return null;
                var json = System.IO.File.ReadAllText(path);
                _graphData = JsonSerializer.Deserialize<JsonElement>(json);
            }
        }
        return (JsonElement)_graphData!;
    }

    public IActionResult Index() => View();

    [HttpGet]
    public IActionResult Node([FromQuery] string id)
    {
        var root = EnsureLoaded();
        if (root == null) return NotFound(new { error = "function-graph.json not found" });

        var nodes = root.Value.GetProperty("nodes");
        if (!nodes.TryGetProperty(id, out var centerNode))
            return NotFound(new { error = $"Node '{id}' not found" });

        // Track which IDs are outgoing vs incoming
        var outSet = new HashSet<string>();
        var inSet = new HashSet<string>();

        if (centerNode.TryGetProperty("calls", out var calls))
            foreach (var c in calls.EnumerateArray())
            {
                var cid = c.GetString()!;
                if (cid != id) outSet.Add(cid);
            }

        if (centerNode.TryGetProperty("called_by", out var callers))
            foreach (var c in callers.EnumerateArray())
            {
                var cid = c.GetString()!;
                if (cid != id) inSet.Add(cid);
            }

        // Union all neighbor IDs
        var allIds = new HashSet<string>(outSet);
        allIds.UnionWith(inSet);

        var neighbors = new List<object>();
        foreach (var nid in allIds)
        {
            if (!nodes.TryGetProperty(nid, out var neighbor)) continue;
            var dir = outSet.Contains(nid) && inSet.Contains(nid) ? "both"
                    : outSet.Contains(nid) ? "outgoing"
                    : "incoming";

            neighbors.Add(new
            {
                id = nid,
                qualified = neighbor.GetProperty("qualified").GetString(),
                name = neighbor.GetProperty("name").GetString(),
                className = neighbor.GetProperty("class").GetString(),
                file = neighbor.GetProperty("file").GetString(),
                line_start = neighbor.GetProperty("line_start").GetInt32(),
                line_count = neighbor.GetProperty("line_count").GetInt32(),
                call_count = neighbor.GetProperty("call_count").GetInt32(),
                caller_count = neighbor.GetProperty("caller_count").GetInt32(),
                max_depth = neighbor.GetProperty("max_depth").GetInt32(),
                total_connections = neighbor.GetProperty("total_connections").GetInt32(),
                direction = dir
            });
        }

        return Json(new
        {
            center = new
            {
                id = centerNode.GetProperty("id").GetString(),
                qualified = centerNode.GetProperty("qualified").GetString(),
                name = centerNode.GetProperty("name").GetString(),
                className = centerNode.GetProperty("class").GetString(),
                file = centerNode.GetProperty("file").GetString(),
                line_start = centerNode.GetProperty("line_start").GetInt32(),
                line_end = centerNode.GetProperty("line_end").GetInt32(),
                line_count = centerNode.GetProperty("line_count").GetInt32(),
                signature = centerNode.GetProperty("signature").GetString(),
                call_count = centerNode.GetProperty("call_count").GetInt32(),
                caller_count = centerNode.GetProperty("caller_count").GetInt32(),
                max_depth = centerNode.GetProperty("max_depth").GetInt32(),
                total_connections = centerNode.GetProperty("total_connections").GetInt32(),
            },
            neighbors
        });
    }

    [HttpGet]
    public IActionResult Search([FromQuery] string q)
    {
        if (string.IsNullOrWhiteSpace(q))
            return Json(new { results = Array.Empty<object>() });

        var root = EnsureLoaded();
        if (root == null) return NotFound(new { error = "function-graph.json not found" });

        var nodes = root.Value.GetProperty("nodes");
        var query = q.Trim().ToLowerInvariant();
        var results = new List<object>();

        foreach (var prop in nodes.EnumerateObject())
        {
            var node = prop.Value;
            var qualified = node.GetProperty("qualified").GetString() ?? "";
            var sig = node.GetProperty("signature").GetString() ?? "";

            if (qualified.ToLowerInvariant().Contains(query) ||
                sig.ToLowerInvariant().Contains(query))
            {
                results.Add(new
                {
                    id = prop.Name,
                    qualified,
                    className = node.GetProperty("class").GetString(),
                    file = node.GetProperty("file").GetString(),
                    call_count = node.GetProperty("call_count").GetInt32(),
                    caller_count = node.GetProperty("caller_count").GetInt32(),
                    max_depth = node.GetProperty("max_depth").GetInt32(),
                    total_connections = node.GetProperty("total_connections").GetInt32(),
                });
                if (results.Count >= 50) break;
            }
        }

        return Json(new { results });
    }

    [HttpGet]
    public IActionResult Stats()
    {
        var root = EnsureLoaded();
        if (root == null) return NotFound(new { error = "function-graph.json not found" });

        return Json(new
        {
            meta = root.Value.GetProperty("_meta"),
            stats = root.Value.GetProperty("stats"),
            classes = root.Value.GetProperty("classes"),
            files = root.Value.GetProperty("files"),
        });
    }

    /// <summary>
    /// GET /FunctionGraph/ExportTree?id=Player::ApplyEquipSpell
    /// Recursively walks outgoing calls. Each function is only fully expanded once;
    /// subsequent appearances get "ref": true (prevents exponential blowup).
    /// </summary>
    [HttpGet]
    public IActionResult ExportTree([FromQuery] string id)
    {
        var root = EnsureLoaded();
        if (root == null) return NotFound(new { error = "function-graph.json not found" });

        var nodes = root.Value.GetProperty("nodes");
        if (!nodes.TryGetProperty(id, out _))
            return NotFound(new { error = $"Node '{id}' not found" });

        var expanded = new HashSet<string>();
        var tree = BuildCallTree(nodes, id, new HashSet<string>(), expanded, 0);

        var jsonBytes = JsonSerializer.SerializeToUtf8Bytes(tree, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            MaxDepth = 1024
        });

        var safeName = id.Replace("::", "_").Replace("/", "_").Replace("\\", "_").Replace(" ", "_");
        return File(jsonBytes, "application/json", $"{safeName}_calltree.json");
    }

    private object BuildCallTree(JsonElement nodes, string nodeId, HashSet<string> ancestors, HashSet<string> expanded, int depth)
    {
        if (ancestors.Contains(nodeId))
            return new { name = nodeId, cycle = true, calls = Array.Empty<object>() };

        if (depth > 80)
            return new { name = nodeId, truncated = true, calls = Array.Empty<object>() };

        if (!nodes.TryGetProperty(nodeId, out var node))
            return new { name = nodeId, external = true, calls = Array.Empty<object>() };

        var qualified = node.GetProperty("qualified").GetString();
        var file = node.GetProperty("file").GetString();
        var line = node.GetProperty("line_start").GetInt32();

        // Already expanded in another branch — just reference
        if (expanded.Contains(nodeId))
            return new { name = qualified, file, line, @ref = true, calls = Array.Empty<object>() };

        expanded.Add(nodeId);
        var newAncestors = new HashSet<string>(ancestors) { nodeId };
        var children = new List<object>();

        if (node.TryGetProperty("calls", out var calls))
        {
            foreach (var call in calls.EnumerateArray())
            {
                var callId = call.GetString()!;
                if (callId != nodeId)
                    children.Add(BuildCallTree(nodes, callId, newAncestors, expanded, depth + 1));
            }
        }

        return new { name = qualified, file, line, calls = children };
    }
}
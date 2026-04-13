using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MangosSuperUI.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace MangosSuperUI.Services;

// ======================== Wire Protocol DTOs ========================

/// <summary>
/// Envelope for all messages on the wire (both directions).
/// Each line on the TCP socket is one JSON object with "type" + "payload".
/// </summary>
public class BridgeMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    [JsonPropertyName("payload")]
    public JsonElement Payload { get; set; }
}

// --- Inbound (C++ → C#) ---

public class BotHelloPayload
{
    [JsonPropertyName("guid")]
    public int Guid { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("race")]
    public int Race { get; set; }

    [JsonPropertyName("classId")]
    public int ClassId { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("mapId")]
    public int MapId { get; set; }

    [JsonPropertyName("zoneId")]
    public int ZoneId { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }
}

public class BotStatePayload
{
    [JsonPropertyName("guid")]
    public int Guid { get; set; }

    [JsonPropertyName("health")]
    public int Health { get; set; }

    [JsonPropertyName("maxHealth")]
    public int MaxHealth { get; set; }

    [JsonPropertyName("mana")]
    public int Mana { get; set; }

    [JsonPropertyName("maxMana")]
    public int MaxMana { get; set; }

    [JsonPropertyName("level")]
    public int Level { get; set; }

    [JsonPropertyName("mapId")]
    public int MapId { get; set; }

    [JsonPropertyName("zoneId")]
    public int ZoneId { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }

    [JsonPropertyName("inCombat")]
    public bool InCombat { get; set; }

    [JsonPropertyName("isDead")]
    public bool IsDead { get; set; }

    [JsonPropertyName("targetGuid")]
    public int TargetGuid { get; set; }

    [JsonPropertyName("taskState")]
    public string TaskState { get; set; } = "IDLE";
}

public class BotEventPayload
{
    [JsonPropertyName("guid")]
    public int Guid { get; set; }

    [JsonPropertyName("event")]
    public string Event { get; set; } = "";

    [JsonPropertyName("data")]
    public string Data { get; set; } = "";

    // --- Phase 2.5 extended fields (present depending on event type) ---

    [JsonPropertyName("creature_entry")]
    public int? CreatureEntry { get; set; }

    [JsonPropertyName("creature_guid")]
    public int? CreatureGuid { get; set; }

    [JsonPropertyName("quest_id")]
    public int? QuestId { get; set; }

    [JsonPropertyName("status")]
    public string? Status { get; set; }

    [JsonPropertyName("new_level")]
    public int? NewLevel { get; set; }

    [JsonPropertyName("sender")]
    public string? Sender { get; set; }

    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("chat_type")]
    public string? ChatType { get; set; }

    [JsonPropertyName("channel_name")]
    public string? ChannelName { get; set; }
}

public class BotChatPayload
{
    [JsonPropertyName("guid")]
    public int Guid { get; set; }

    [JsonPropertyName("senderName")]
    public string SenderName { get; set; } = "";

    [JsonPropertyName("message")]
    public string Message { get; set; } = "";

    [JsonPropertyName("chatType")]
    public int ChatType { get; set; } // 7 = WHISPER in vanilla
}

// --- Outbound (C# → C++) ---

public class MoveToPayload
{
    [JsonPropertyName("guid")]
    public int Guid { get; set; }

    [JsonPropertyName("mapId")]
    public int MapId { get; set; }

    [JsonPropertyName("x")]
    public float X { get; set; }

    [JsonPropertyName("y")]
    public float Y { get; set; }

    [JsonPropertyName("z")]
    public float Z { get; set; }
}

public class SayTextPayload
{
    [JsonPropertyName("guid")]
    public int Guid { get; set; }

    [JsonPropertyName("text")]
    public string Text { get; set; } = "";

    [JsonPropertyName("chatType")]
    public int ChatType { get; set; } // 0=SAY, 6=YELL, 7=WHISPER

    [JsonPropertyName("target")]
    public string? Target { get; set; } // Player name for whisper replies

    [JsonPropertyName("channel")]
    public string? Channel { get; set; } // Channel name for channel replies
}

// --- Phase 2.5 command payloads ---

public class QuestCommandPayload
{
    [JsonPropertyName("quest_id")]
    public int QuestId { get; set; }
}

public class LearnSpellPayload
{
    [JsonPropertyName("spell_id")]
    public int SpellId { get; set; }
}

public class TargetGuidPayload
{
    [JsonPropertyName("guid")]
    public int Guid { get; set; }
}

// ======================== Live Bot State ========================

public class BotState
{
    public int Guid { get; set; }
    public string Name { get; set; } = "";
    public int Race { get; set; }
    public int ClassId { get; set; }
    public int Level { get; set; }
    public int Health { get; set; }
    public int MaxHealth { get; set; }
    public int Mana { get; set; }
    public int MaxMana { get; set; }
    public int MapId { get; set; }
    public int ZoneId { get; set; }
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    public bool InCombat { get; set; }
    public bool IsDead { get; set; }
    public int TargetGuid { get; set; }
    public string TaskState { get; set; } = "IDLE";
    public DateTime ConnectedAt { get; set; }
    public DateTime LastUpdate { get; set; }
}

/// <summary>
/// Tracks a single TCP connection from an AiBotAI instance.
/// </summary>
public class BotConnection
{
    public int Guid { get; set; }
    public TcpClient Client { get; set; } = null!;
    public NetworkStream Stream { get; set; } = null!;
    public CancellationTokenSource Cts { get; set; } = new();
    public BotState State { get; set; } = new();
}

// ======================== BotBridgeService ========================

/// <summary>
/// TCP listener on port 3444. Each AiBotAI (C++ inside mangosd) connects as a client.
/// Protocol: newline-delimited JSON ("JSON lines"). Each line is a BridgeMessage.
///
/// Inbound message types:
///   HELLO       — bot announces itself on connect (guid, name, race, class, level, position)
///   STATE       — periodic state update (health, mana, position, combat, task)
///   EVENT       — discrete events (COMBAT_START, DEATH, RESPAWN, QUEST_COMPLETE, etc.)
///   CHAT_RECV   — a player whispered the bot
///
/// Outbound message types:
///   MOVE_TO     — walk to coordinates
///   SAY_TEXT    — say/yell/whisper text
///   SET_TASK    — assign a persistent task (GRIND, IDLE)
///   PING        — keepalive
/// </summary>
public class BotBridgeService : BackgroundService
{
    private readonly ILogger<BotBridgeService> _logger;
    private readonly IHubContext<BotBridgeHub> _hub;
    private TcpListener? _listener;

    // BotBrain integration — set after startup to avoid circular DI
    private BotBrainService? _brain;

    // All connected bots, keyed by character GUID
    public ConcurrentDictionary<int, BotConnection> Connections { get; } = new();

    // Snapshot of all bot states (survives brief disconnects for UI display)
    public ConcurrentDictionary<int, BotState> BotStates { get; } = new();

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public BotBridgeService(ILogger<BotBridgeService> logger, IHubContext<BotBridgeHub> hub)
    {
        _logger = logger;
        _hub = hub;
    }

    /// <summary>
    /// Called by BotBrainService after DI resolution to wire itself in.
    /// Can't inject directly due to circular dependency (Bridge ← Brain → Bridge).
    /// </summary>
    public void SetBrainService(BotBrainService brain)
    {
        _brain = brain;
        _logger.LogInformation("BotBridge: BotBrainService wired for event routing");
    }

    // ==================== Lifecycle ====================

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _listener = new TcpListener(IPAddress.Loopback, 3444);
        _listener.Start();
        _logger.LogInformation("BotBridge TCP listener started on 127.0.0.1:3444");

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(stoppingToken);
                _ = HandleClientAsync(client, stoppingToken);
            }
        }
        catch (OperationCanceledException) { /* shutdown */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "BotBridge listener error");
        }
        finally
        {
            _listener.Stop();
            _logger.LogInformation("BotBridge TCP listener stopped");
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("BotBridge stopping — closing listener and all connections");

        _listener?.Stop();

        foreach (var kvp in Connections)
        {
            try { kvp.Value.Cts.Cancel(); } catch { }
            try { kvp.Value.Client.Dispose(); } catch { }
        }
        Connections.Clear();

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken appToken)
    {
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _logger.LogInformation("BotBridge: new connection from {Endpoint}", endpoint);

        BotConnection? conn = null;

        try
        {
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true);
            conn = new BotConnection { Client = client, Stream = stream };

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(appToken, conn.Cts.Token);

            while (!linked.Token.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(linked.Token);
                if (line == null) break; // connection closed

                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var msg = JsonSerializer.Deserialize<BridgeMessage>(line, JsonOpts);
                    if (msg != null)
                        await ProcessInboundAsync(msg, conn);
                }
                catch (JsonException ex)
                {
                    _logger.LogWarning("BotBridge: malformed JSON from {Endpoint}: {Error}", endpoint, ex.Message);
                }
            }
        }
        catch (OperationCanceledException) { /* normal */ }
        catch (IOException) { /* disconnected */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBridge: client error from {Endpoint}", endpoint);
        }
        finally
        {
            if (conn != null && conn.Guid != 0)
            {
                Connections.TryRemove(conn.Guid, out _);
                // Mark state as disconnected but keep it for UI
                if (BotStates.TryGetValue(conn.Guid, out var state))
                    state.TaskState = "DISCONNECTED";

                _logger.LogInformation("BotBridge: bot {Guid} ({Name}) disconnected", conn.Guid, conn.State.Name);
                await _hub.Clients.All.SendAsync("BotDisconnected", conn.Guid);
            }

            try { client.Dispose(); } catch { }
        }
    }

    // ==================== Inbound Processing ====================

    private async Task ProcessInboundAsync(BridgeMessage msg, BotConnection conn)
    {
        switch (msg.Type.ToUpperInvariant())
        {
            case "HELLO":
                await HandleHelloAsync(msg.Payload, conn);
                break;

            case "STATE":
                await HandleStateAsync(msg.Payload, conn);
                break;

            case "EVENT":
                await HandleEventAsync(msg.Payload, conn);
                break;

            case "CHAT_RECV":
                await HandleChatAsync(msg.Payload, conn);
                break;

            default:
                _logger.LogWarning("BotBridge: unknown message type '{Type}' from bot {Guid}", msg.Type, conn.Guid);
                break;
        }
    }

    private async Task HandleHelloAsync(JsonElement payload, BotConnection conn)
    {
        var hello = payload.Deserialize<BotHelloPayload>(JsonOpts);
        if (hello == null) return;

        conn.Guid = hello.Guid;
        conn.State = new BotState
        {
            Guid = hello.Guid,
            Name = hello.Name,
            Race = hello.Race,
            ClassId = hello.ClassId,
            Level = hello.Level,
            MapId = hello.MapId,
            ZoneId = hello.ZoneId,
            X = hello.X,
            Y = hello.Y,
            Z = hello.Z,
            Health = 100,
            MaxHealth = 100,
            TaskState = "IDLE",
            ConnectedAt = DateTime.UtcNow,
            LastUpdate = DateTime.UtcNow
        };

        Connections[hello.Guid] = conn;
        BotStates[hello.Guid] = conn.State;

        _logger.LogInformation("BotBridge: HELLO from {Name} (guid={Guid}, class={Class}, level={Level})",
            hello.Name, hello.Guid, hello.ClassId, hello.Level);

        await _hub.Clients.All.SendAsync("BotConnected", conn.State);
    }

    private async Task HandleStateAsync(JsonElement payload, BotConnection conn)
    {
        var state = payload.Deserialize<BotStatePayload>(JsonOpts);
        if (state == null) return;

        var bs = conn.State;
        bs.Health = state.Health;
        bs.MaxHealth = state.MaxHealth;
        bs.Mana = state.Mana;
        bs.MaxMana = state.MaxMana;
        bs.Level = state.Level;
        bs.MapId = state.MapId;
        bs.ZoneId = state.ZoneId;
        bs.X = state.X;
        bs.Y = state.Y;
        bs.Z = state.Z;
        bs.InCombat = state.InCombat;
        bs.IsDead = state.IsDead;
        bs.TargetGuid = state.TargetGuid;
        bs.TaskState = state.TaskState;
        bs.LastUpdate = DateTime.UtcNow;

        BotStates[conn.Guid] = bs;

        await _hub.Clients.All.SendAsync("BotStateUpdate", bs);
    }

    private async Task HandleEventAsync(JsonElement payload, BotConnection conn)
    {
        var evt = payload.Deserialize<BotEventPayload>(JsonOpts);
        if (evt == null) return;

        var eventType = evt.Event?.ToUpperInvariant() ?? "";

        switch (eventType)
        {
            case "KILL":
                _logger.LogInformation("BotBridge: KILL by {Name} — creature entry={Entry} guid={CrGuid}",
                    conn.State.Name, evt.CreatureEntry, evt.CreatureGuid);
                await _hub.Clients.All.SendAsync("BotEvent", new
                {
                    guid = conn.Guid,
                    name = conn.State.Name,
                    eventType = "KILL",
                    creatureEntry = evt.CreatureEntry,
                    creatureGuid = evt.CreatureGuid,
                    timestamp = DateTime.UtcNow
                });
                break;

            case "QUEST_UPDATE":
                _logger.LogInformation("BotBridge: QUEST_UPDATE {Name} — quest={QuestId} status={Status}",
                    conn.State.Name, evt.QuestId, evt.Status);
                await _hub.Clients.All.SendAsync("BotEvent", new
                {
                    guid = conn.Guid,
                    name = conn.State.Name,
                    eventType = "QUEST_UPDATE",
                    questId = evt.QuestId,
                    status = evt.Status,
                    timestamp = DateTime.UtcNow
                });
                break;

            case "LEVEL_UP":
                _logger.LogInformation("BotBridge: LEVEL_UP {Name} → level {Level}",
                    conn.State.Name, evt.NewLevel);
                if (evt.NewLevel.HasValue)
                    conn.State.Level = evt.NewLevel.Value;
                await _hub.Clients.All.SendAsync("BotEvent", new
                {
                    guid = conn.Guid,
                    name = conn.State.Name,
                    eventType = "LEVEL_UP",
                    newLevel = evt.NewLevel,
                    timestamp = DateTime.UtcNow
                });
                break;

            case "CHAT_RECV":
                _logger.LogInformation("BotBridge: CHAT_RECV bot={Name} from={Sender} [{ChatType}]: {Message}",
                    conn.State.Name, evt.Sender, evt.ChatType ?? "say", evt.Message);
                await _hub.Clients.All.SendAsync("BotChatReceived", new
                {
                    guid = conn.Guid,
                    botName = conn.State.Name,
                    senderName = evt.Sender ?? "Unknown",
                    message = evt.Message ?? "",
                    chatType = evt.ChatType ?? "say",
                    channelName = evt.ChannelName ?? "",
                    timestamp = DateTime.UtcNow
                });
                // TODO Phase 4: Route to Ollama personality engine
                break;

            case "TASK_COMPLETE":
                _logger.LogInformation("BotBridge: TASK_COMPLETE {Name} — {Data}",
                    conn.State.Name, evt.Data);
                conn.State.TaskState = "IDLE";
                await _hub.Clients.All.SendAsync("BotEvent", new
                {
                    guid = conn.Guid,
                    name = conn.State.Name,
                    eventType = "TASK_COMPLETE",
                    data = evt.Data,
                    timestamp = DateTime.UtcNow
                });
                break;

            default:
                _logger.LogInformation("BotBridge: EVENT {Event} from {Name} (guid={Guid}): {Data}",
                    evt.Event, conn.State.Name, conn.Guid, evt.Data);
                await _hub.Clients.All.SendAsync("BotEvent", new
                {
                    guid = conn.Guid,
                    name = conn.State.Name,
                    eventType = evt.Event,
                    data = evt.Data,
                    timestamp = DateTime.UtcNow
                });
                break;
        }

        // Route to behavioral engine (if wired)
        if (_brain != null)
        {
            var botEvent = new MangosSuperUI.BotLogic.Core.BotEvent
            {
                EventType = eventType,
                CreatureEntry = evt.CreatureEntry ?? 0,
                CreatureGuid = evt.CreatureGuid ?? 0,
                QuestId = evt.QuestId ?? 0,
                QuestStatus = evt.Status ?? "",
                NewLevel = evt.NewLevel ?? 0,
                Sender = evt.Sender ?? "",
                Message = evt.Message ?? "",
                ChatType = evt.ChatType ?? "",
                ChannelName = evt.ChannelName ?? "",
                Data = evt.Data ?? ""
            };
            _ = Task.Run(() => _brain.HandleBridgeEventAsync(conn.Guid, botEvent));
        }
    }

    private async Task HandleChatAsync(JsonElement payload, BotConnection conn)
    {
        var chat = payload.Deserialize<BotChatPayload>(JsonOpts);
        if (chat == null) return;

        _logger.LogInformation("BotBridge: CHAT_RECV bot={Name} from={Sender}: {Message}",
            conn.State.Name, chat.SenderName, chat.Message);

        // Push to UI for visibility
        await _hub.Clients.All.SendAsync("BotChatReceived", new
        {
            guid = conn.Guid,
            botName = conn.State.Name,
            senderName = chat.SenderName,
            message = chat.Message,
            chatType = chat.ChatType,
            timestamp = DateTime.UtcNow
        });

        // Route to behavioral engine (if wired)
        if (_brain != null)
        {
            var botEvent = new MangosSuperUI.BotLogic.Core.BotEvent
            {
                EventType = "CHAT_RECV",
                Sender = chat.SenderName,
                Message = chat.Message,
                ChatType = chat.ChatType.ToString()
            };
            _ = Task.Run(() => _brain.HandleBridgeEventAsync(conn.Guid, botEvent));
        }
    }

    // ==================== Outbound Commands ====================

    public async Task SendToBotAsync(int guid, string type, object payload)
    {
        if (!Connections.TryGetValue(guid, out var conn))
        {
            _logger.LogWarning("BotBridge: cannot send {Type} — bot {Guid} not connected", type, guid);
            return;
        }

        var envelope = new { type, payload };
        var json = JsonSerializer.Serialize(envelope, JsonOpts) + "\n";
        var bytes = Encoding.UTF8.GetBytes(json);

        try
        {
            await conn.Stream.WriteAsync(bytes);
            await conn.Stream.FlushAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "BotBridge: send to bot {Guid} failed", guid);
        }
    }

    public Task SendMoveToAsync(int guid, int mapId, float x, float y, float z)
    {
        return SendToBotAsync(guid, "MOVE_TO", new MoveToPayload
        {
            Guid = guid,
            MapId = mapId,
            X = x,
            Y = y,
            Z = z
        });
    }

    public Task SendSayTextAsync(int guid, string text, int chatType = 0, string? target = null, string? channel = null)
    {
        return SendToBotAsync(guid, "SAY_TEXT", new SayTextPayload
        {
            Guid = guid,
            Text = text,
            ChatType = chatType,
            Target = target,
            Channel = channel
        });
    }

    public async Task SendToAllBotsAsync(string type, object payload)
    {
        foreach (var kvp in Connections)
        {
            await SendToBotAsync(kvp.Key, type, payload);
        }
    }

    // --- Phase 2.5 commands ---

    public Task SendAcceptQuestAsync(int guid, int questId)
    {
        return SendToBotAsync(guid, "ACCEPT_QUEST", new QuestCommandPayload { QuestId = questId });
    }

    public Task SendCompleteQuestAsync(int guid, int questId)
    {
        return SendToBotAsync(guid, "COMPLETE_QUEST", new QuestCommandPayload { QuestId = questId });
    }

    public Task SendAbandonQuestAsync(int guid, int questId)
    {
        return SendToBotAsync(guid, "ABANDON_QUEST", new QuestCommandPayload { QuestId = questId });
    }

    public Task SendLearnSpellAsync(int guid, int spellId)
    {
        return SendToBotAsync(guid, "LEARN_SPELL", new LearnSpellPayload { SpellId = spellId });
    }

    public Task SendAttackTargetAsync(int guid, int targetGuid)
    {
        return SendToBotAsync(guid, "ATTACK_TARGET", new TargetGuidPayload { Guid = targetGuid });
    }

    public Task SendInteractNpcAsync(int guid, int npcGuid)
    {
        return SendToBotAsync(guid, "INTERACT_NPC", new TargetGuidPayload { Guid = npcGuid });
    }

    public Task SendSetTaskGrindAsync(int guid, float x, float y, float z,
        float radius = 40f, int creatureEntry = 0, int killCount = 0)
    {
        return SendToBotAsync(guid, "SET_TASK", new
        {
            task = "GRIND",
            x,
            y,
            z,
            radius,
            creature_entry = creatureEntry,
            kill_count = killCount
        });
    }

    public Task SendSetTaskIdleAsync(int guid)
    {
        return SendToBotAsync(guid, "SET_TASK", new { task = "IDLE" });
    }

    // ==================== Query ====================

    public List<BotState> GetAllBotStates()
    {
        return BotStates.Values
            .OrderBy(b => b.Name)
            .ToList();
    }

    public BotState? GetBotState(int guid)
    {
        BotStates.TryGetValue(guid, out var state);
        return state;
    }

    public int ConnectedCount => Connections.Count;
    public int TotalTracked => BotStates.Count;
}
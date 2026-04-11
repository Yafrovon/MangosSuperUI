# BotBridge Wire Protocol — v1

**Transport:** TCP on `127.0.0.1:3444`  
**Encoding:** UTF-8, newline-delimited JSON (one JSON object per `\n`)  
**Direction:** C++ (AiBotAI) is the TCP CLIENT → C# (BotBridgeService) is the TCP SERVER  

---

## Connection Lifecycle

1. `mangosd` starts → AiBotAI spawns → `OnSessionLoaded()` fires
2. AiBotAI opens TCP connection to `127.0.0.1:3444`
3. AiBotAI sends a `HELLO` message with identity + initial position
4. AiBotAI sends `STATE` messages every N seconds (configurable, default 5s)
5. AiBotAI sends `EVENT` messages on discrete occurrences
6. C# sends commands (`MOVE_TO`, `SAY_TEXT`, etc.) asynchronously at any time
7. On disconnect, C# marks bot as `DISCONNECTED` but retains state for UI

---

## Message Envelope

Every line is a JSON object with exactly two fields:

```json
{"type":"MESSAGE_TYPE","payload":{...}}
```

---

## Inbound Messages (C++ → C#)

### HELLO
Sent once on connect. Registers the bot with the bridge.

```json
{
  "type": "HELLO",
  "payload": {
    "guid": 12345,
    "name": "Edageq",
    "race": 1,
    "classId": 1,
    "level": 5,
    "mapId": 0,
    "zoneId": 12,
    "x": -8949.95,
    "y": -132.493,
    "z": 83.5312
  }
}
```

### STATE
Periodic heartbeat with full state snapshot. Sent every 5 seconds by default.

```json
{
  "type": "STATE",
  "payload": {
    "guid": 12345,
    "health": 180,
    "maxHealth": 200,
    "mana": 0,
    "maxMana": 0,
    "level": 5,
    "mapId": 0,
    "zoneId": 12,
    "x": -8949.95,
    "y": -132.493,
    "z": 83.5312,
    "inCombat": false,
    "isDead": false,
    "targetGuid": 0,
    "taskState": "IDLE"
  }
}
```

**taskState values:** `IDLE`, `MOVING`, `COMBAT`, `DEAD`, `EXECUTING`, `WAITING`

### EVENT
Discrete events for logging and intelligence.

```json
{
  "type": "EVENT",
  "payload": {
    "guid": 12345,
    "event": "COMBAT_START",
    "data": "Engaging Kobold Vermin (guid=54321)"
  }
}
```

**event values:**
- `COMBAT_START` / `COMBAT_END`
- `DEATH` / `RESPAWN`
- `LEVEL_UP`
- `QUEST_ACCEPT` / `QUEST_COMPLETE` / `QUEST_ABANDON`
- `TASK_COMPLETE` / `TASK_FAILED`
- `ZONE_CHANGE`

### CHAT_RECV
A real player whispered (or said to) the bot.

```json
{
  "type": "CHAT_RECV",
  "payload": {
    "guid": 12345,
    "senderName": "Nico",
    "message": "Hey, want to group up?",
    "chatType": 7
  }
}
```

**chatType values:** `0` = SAY, `1` = PARTY, `6` = YELL, `7` = WHISPER

---

## Outbound Messages (C# → C++)

### MOVE_TO
Walk to coordinates.

```json
{
  "type": "MOVE_TO",
  "payload": {
    "guid": 12345,
    "mapId": 0,
    "x": -8950.0,
    "y": -130.0,
    "z": 83.0
  }
}
```

### SAY_TEXT
Make the bot speak.

```json
{
  "type": "SAY_TEXT",
  "payload": {
    "guid": 12345,
    "text": "Looking for group!",
    "chatType": 0
  }
}
```

### PING
Keepalive (future use).

```json
{
  "type": "PING",
  "payload": {}
}
```

### SET_TASK (Phase 3 — future)
Assign a complex task.

```json
{
  "type": "SET_TASK",
  "payload": {
    "guid": 12345,
    "task": "QUEST_KILL",
    "params": {
      "questId": 6,
      "creatureId": 257,
      "count": 8,
      "area": {"mapId": 0, "x": -8900, "y": -150, "z": 82, "radius": 200}
    }
  }
}
```

---

## C++ Implementation Notes

The AiBotAI TCP client should:
1. Use a **non-blocking socket** or a dedicated thread — do NOT block the main game loop
2. Buffer incoming data and split on `\n` to extract complete JSON lines
3. Use a lightweight JSON parser (RapidJSON is already in VMaNGOS deps, or simple sscanf for the minimal payloads)
4. Queue outbound messages and flush in `UpdateAI()` or on a timer
5. Reconnect on disconnect with exponential backoff (2s, 4s, 8s, max 30s)

The STATE message should be sent from `UpdateAI()` gated by a `ShortTimeTracker` (every 5000ms).

---

## Error Handling

- Malformed JSON lines are logged and skipped (no disconnect)
- Unknown message types are logged and skipped
- If C# can't parse a payload, the message is dropped with a warning
- TCP disconnect → C# marks bot DISCONNECTED, C++ attempts reconnect

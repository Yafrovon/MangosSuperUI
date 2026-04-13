# BotBridge Wire Protocol — v2

**Transport:** TCP on `127.0.0.1:3444`  
**Encoding:** UTF-8, newline-delimited JSON (one JSON object per `\n`)  
**Direction:** C++ (AiBotAI) is the TCP CLIENT → C# (BotBridgeService) is the TCP SERVER  
**Last audited:** AiBotAI.cpp, BotBridgeService.cs, BotBridgeHub.cs

---

## Connection Lifecycle

1. `mangosd` starts → AiBotAI spawns → `OnSessionLoaded()` fires
2. AiBotAI opens TCP connection to `127.0.0.1:3444`
3. After `m_initialized` is true, AiBotAI sends `HELLO` with identity + initial position
4. AiBotAI sends `STATE` every `BRIDGE_STATE_INTERVAL` ms (default 5000)
5. AiBotAI sends `EVENT` messages on discrete occurrences
6. C# sends commands asynchronously at any time
7. On disconnect, C# marks bot as `DISCONNECTED` but retains state for UI display
8. C++ reconnects with exponential backoff: `BRIDGE_RECONNECT_BASE` → max `BRIDGE_RECONNECT_MAX`

---

## Message Envelope

Every line is a JSON object with exactly two fields:

```json
{"type":"MESSAGE_TYPE","payload":{...}}
```

C++ uses `snprintf` for outbound, `JsonExtractString/Int/Float` helpers for inbound (no JSON library). All payload fields are flat within the `payload` object.

---

## Inbound Messages (C++ → C#)

### HELLO
Sent once after initialization. Registers the bot with the bridge.

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
Periodic heartbeat with full state snapshot.

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

**taskState values currently emitted by C++:**

| Value | Condition |
|-------|-----------|
| `IDLE` | Default — not dead, not in combat, no active task |
| `MOVING` | `m_currentTask.type == TASK_MOVE_TO` or `me->IsMoving()` |
| `COMBAT` | `me->IsInCombat()` |
| `DEAD` | `me->IsDead()` |

> **Note:** `EXECUTING` and `WAITING` are reserved for Phase 3 task states but not yet emitted by C++.

### EVENT
Discrete events. The `event` field determines which additional payload fields are present.

**Common envelope:**
```json
{
  "type": "EVENT",
  "payload": {
    "guid": 12345,
    "event": "EVENT_NAME",
    "data": "optional free-text"
  }
}
```

**Events currently sent by C++:**

| Event | When Fired | Extended Payload Fields | C++ Sender |
|-------|-----------|------------------------|------------|
| `KILL` | Target creature dies in combat | `creature_entry`, `creature_guid` | `SendKillEvent()` |
| `QUEST_UPDATE` | Quest accepted, rewarded, or abandoned | `quest_id`, `status` | `SendQuestUpdateEvent()` |
| `LEVEL_UP` | Bot level increased | `new_level` | `SendLevelUpEvent()` |
| `CHAT_RECV` | Incoming whisper/say intercepted via EVENT path | `sender`, `message`, `chat_type` | `SendChatRecvEvent()` |
| `TASK_COMPLETE` | `MovementInform(AIBOT_POINT_TASK_DEST)` fires | `data` = `"MOVE_TO arrived"` | `BridgeSendEvent()` |
| `DEATH` | `me->IsDead()` transition detected | (none) | `BridgeSendEvent()` |
| `RESPAWN` | Self-revive completes | (none) | `BridgeSendEvent()` |
| `NPC_INTERACT` | INTERACT_NPC reaches the NPC (≤10yd) | `data` = creature name | `BridgeSendEvent()` |
| `QUEST_FAILED` | Quest command validation fails | `data` = reason string | `BridgeSendEvent()` |

**KILL example:**
```json
{
  "type": "EVENT",
  "payload": {
    "guid": 12345,
    "event": "KILL",
    "creature_entry": 257,
    "creature_guid": 54321
  }
}
```

**QUEST_UPDATE example:**
```json
{
  "type": "EVENT",
  "payload": {
    "guid": 12345,
    "event": "QUEST_UPDATE",
    "quest_id": 6,
    "status": "accepted"
  }
}
```

**QUEST_UPDATE `status` values:** `accepted`, `rewarded`, `abandoned`

**QUEST_FAILED `data` values:** `"quest not found"`, `"requirements not met"`, `"quest log full"`, `"quest not in log"`

**LEVEL_UP example:**
```json
{
  "type": "EVENT",
  "payload": {
    "guid": 12345,
    "event": "LEVEL_UP",
    "new_level": 6
  }
}
```

### CHAT_RECV (standalone message type)
Also exists as a separate top-level message type in addition to the EVENT path above. C# `BotBridgeService` handles both — `HandleChatAsync()` for this type, and `HandleEventAsync()` case `"CHAT_RECV"` for the EVENT-wrapped version.

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

> **Implementation note:** C++ `OnPacketReceived()` intercepts `SMSG_MESSAGECHAT` and routes through `SendChatRecvEvent()` which uses the EVENT envelope. The standalone CHAT_RECV type handler exists in C# but the C++ currently sends via EVENT. Both paths produce identical UI output.

---

## Outbound Commands (C# → C++)

### Phase 1 — Implemented and Tested ✓

#### MOVE_TO
Walk to coordinates using pathfinding.

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

**C++ behavior:**
- Rejected if `me->IsInCombat()` (logged, deferred)
- Rejected if `mapId != me->GetMapId()` (cross-map not supported)
- Calls `StopMoving()` then `MovePoint(AIBOT_POINT_TASK_DEST, x, y, z, MOVE_PATHFINDING)`
- Sets `m_currentTask.type = TASK_MOVE_TO`
- Fires `TASK_COMPLETE` event on arrival via `MovementInform()`

#### SAY_TEXT
Make the bot speak or yell.

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

**chatType:** `0` = `me->Say()`, `6` = `me->Yell()`. Whisper (7) is NOT implemented outbound.

#### PING
Keepalive — no-op on C++ side.

```json
{"type":"PING","payload":{}}
```

### Phase 2.5 — Implemented, Testing In Progress

#### ACCEPT_QUEST

```json
{
  "type": "ACCEPT_QUEST",
  "payload": { "quest_id": 6 }
}
```

**C++ behavior:**
- Validates `CanTakeQuest()` + `CanAddQuest()`
- Calls `me->AddQuest(pQuest, nullptr)`
- Auto-calls `CompleteQuest()` if objectives already satisfied
- Fires `QUEST_UPDATE` with status `"accepted"` on success
- Fires `QUEST_FAILED` with reason on failure

#### COMPLETE_QUEST

```json
{
  "type": "COMPLETE_QUEST",
  "payload": { "quest_id": 6 }
}
```

**C++ behavior:**
- Validates quest is in log (`GetQuestStatus != QUEST_STATUS_NONE`)
- Calls `me->FullQuestComplete(questId)` — awards items, XP, reputation, money
- Fires `QUEST_UPDATE` with status `"rewarded"`

> **Note:** `FullQuestComplete` does NOT require proximity to the quest giver. This is by design for bot automation.

#### ABANDON_QUEST

```json
{
  "type": "ABANDON_QUEST",
  "payload": { "quest_id": 6 }
}
```

**C++ behavior:**
- Sets `QuestStatus` to `QUEST_STATUS_NONE`
- Fires `QUEST_UPDATE` with status `"abandoned"`

#### LEARN_SPELL

```json
{
  "type": "LEARN_SPELL",
  "payload": { "spell_id": 133 }
}
```

**C++ behavior:**
- Calls `me->LearnSpell(spellId, false)` directly
- Silently skips if bot already knows the spell
- Does NOT deduct gold — cost tracking is C#'s responsibility via `bot_training_log`

#### ATTACK_TARGET

```json
{
  "type": "ATTACK_TARGET",
  "payload": { "guid": 54321 }
}
```

**C++ behavior:**
- `guid` is the creature's `GetGUIDLow()` counter value
- Looks up creature on current map via `ObjectGuid(HIGHGUID_UNIT, uint32(guidLow))`
- Validates `IsValidHostileTarget()`
- Calls `AttackStart(pCreature)` — handles role-aware chase distance
- Once engaged, autonomous combat rotation (`UpdateInCombatAI_*`) takes over entirely

#### INTERACT_NPC

```json
{
  "type": "INTERACT_NPC",
  "payload": { "guid": 54321 }
}
```

**C++ behavior:**
- `guid` is the NPC's `GetGUIDLow()` counter value
- If distance > 10yd: moves to contact point via `MovePoint()`, defers interaction
- If distance ≤ 10yd: `SetFacingToObject(pCreature)`, fires `NPC_INTERACT` event
- Does NOT open vendor/trainer/quest UI — those require separate commands

### Phase 3 — Planned, Need C++ Handlers

#### High Priority (blocking domain functionality)

| Command | Payload | What It Would Do | Needed By |
|---------|---------|-------------------|-----------|
| `STOP` | (none) | `StopMoving()` — cancel all movement | All domains |
| `EAT_DRINK` | (none) | Call `DrinkAndEat()` on demand | CombatDomain post-fight |
| `USE_MOUNT` | (none) | Call `UseMount()` — race/class/level aware | Explore, Questing |
| `DISMOUNT` | (none) | `RemoveSpellsCausingAura(SPELL_AURA_MOUNTED)` | Combat entry, NPC interact |
| `SELL_ITEM` | `item_id`, `count`, `vendor_guid` | Sell from inventory to vendor | EconomyDomain |
| `BUY_ITEM` | `item_id`, `count`, `vendor_guid` | Buy from vendor | EconomyDomain |
| `LOOT_CORPSE` | `corpse_guid` | Loot a killed creature's corpse | CombatDomain → EconomyDomain |
| `WHISPER` | `target_name`, `text` | `me->Whisper()` to a player | SocialDomain, Ollama chat |

#### Medium Priority

| Command | Payload | What It Would Do | Needed By |
|---------|---------|-------------------|-----------|
| `CAST_SPELL` | `spell_id`, `target_guid` (0=self) | Generic `me->CastSpell()` | Utility buffs, professions |
| `USE_ITEM` | `item_id` | Use a consumable or quest item | Economy, Questing |
| `EQUIP_ITEM` | `item_id`, `slot` | Equip gear from inventory | EconomyDomain |
| `SET_TASK_STATE` | `state` string | Override `taskState` for C# coordination | All domains |
| `EMOTE` | `emote_id` | `HandleEmoteCommand()` | SocialDomain |

#### Low Priority

| Command | Payload | What It Would Do | Needed By |
|---------|---------|-------------------|-----------|
| `JOIN_GROUP` | `target_guid` | Accept/send group invite | SocialDomain |
| `LEAVE_GROUP` | (none) | Leave current party | SocialDomain |
| `FOLLOW_PLAYER` | `target_guid`, `distance` | `MoveFollow()` | SocialDomain |
| `TAXI` | `taxi_path_id` | `ActivateTaxiPathTo()` | QuestingDomain |
| `SET_SHEATH` | `sheath_state` | Visual weapon display (0/1/2) | SocialDomain (RP) |

---

## Autonomous C++ Behaviors (NOT bridge-controlled)

These run automatically in `UpdateAI()`. C# does not control them — the bridge commands operate *alongside* these behaviors:

| Behavior | Method | When | Notes |
|----------|--------|------|-------|
| Combat rotation | `UpdateInCombatAI_*()` | `me->IsInCombat()` | 9 class-specific rotations (verbatim from BattleBotAI) |
| Self-buff/prep | `UpdateOutOfCombatAI_*()` | Out of combat | Auras, weapon buffs, pet summon, stance |
| Target selection | `SelectAttackTarget()` | Out of combat, idle | Threat list → party assist → nearby hostile |
| Eat/drink | `DrinkAndEat()` | Out of combat, not full HP/mana | Uses AB_SPELL_FOOD (1131) / AB_SPELL_DRINK (1137) |
| Mounting | `UseMount()` | Out of combat, idle, level ≥ 40 | Race/class-aware mount spell. Rogues excluded |
| Random wander | `DoRandomWander()` | Idle, no task, no target | 15yd radius, 10-20s timer |
| Self-revive | Death handler | `GetDeathState() == DEAD` | `ResurrectPlayer(0.5f)` — revives at 50% HP |
| CC break | `BreakCrowdControlEffects()` | Has CC aura | Inherited from CombatBotBaseAI |
| Unreachable target | `CheckForUnreachableTarget()` | Chase unreachable | Includes `NearTeleportTo` cheat for stuck pathing |
| Level-up refresh | Level detection | `GetLevel() > m_lastKnownLevel` | Re-runs `PopulateSpellData()`, `UpdateSkillsToMaxSkillsForLevel()` |
| Ammo replenish | `AddHunterAmmo()` | Auto shot NEED_AMMO | Hunter only |
| Pet management | `SummonPetIfNeeded()` | Out of combat, no pet | Hunter/Warlock |
| Stealth detection | Victim visibility check | Each tick | Stops chasing stealthed targets |

> **Design principle:** C# controls *strategic* decisions (where to go, which quest, when to train). C++ handles *tactical* execution (combat rotation, target selection, eat/drink, movement mechanics). The bridge is the interface between strategy and tactics.

---

## STATE Packet — Future Expansion

Fields available in C++ but not yet in the STATE packet. Add to `BridgeSendState()` as domains need them:

| Field | C++ Source | Type | Needed By |
|-------|-----------|------|-----------|
| `isMounted` | `me->IsMounted()` | bool | All travel domains |
| `copper` | `me->GetMoney()` | uint32 | EconomyDomain |
| `powerType` | `me->GetPowerType()` | int | UI (rage/energy display) |
| `power` | `me->GetPower(powerType)` | int | Warrior rage, Rogue energy |
| `maxPower` | `me->GetMaxPower(powerType)` | int | Percentage calculation |
| `comboPoints` | `me->GetComboPoints()` | int | Rogue/Druid |
| `shapeshiftForm` | `me->GetShapeshiftForm()` | int | Druid form tracking |
| `isMoving` | `me->IsMoving()` | bool | Movement state |
| `orientation` | `me->GetOrientation()` | float | Facing direction |
| `standState` | `me->GetStandState()` | int | Sitting/standing |
| `petGuid` | pet->GetGUIDLow() | int | Pet tracking |
| `petHealthPct` | pet health % | float | Pet monitoring |
| `isStealthed` | `HasAuraType(SPELL_AURA_MOD_STEALTH)` | bool | Rogue/Druid |

---

## Future Events — Need C++ Senders

Events C++ should send but doesn't yet. Add as domains require them:

| Event | Trigger Point | Payload | Needed By |
|-------|--------------|---------|-----------|
| `COMBAT_START` | `AttackStart()` | `target_guid`, `target_entry`, `target_level` | CombatDomain |
| `COMBAT_END` | Last attacker dies / evade | `duration_ms`, `kills` | CombatDomain |
| `INVENTORY_UPDATE` | Item gained/lost/equipped | `item_id`, `count`, `action` | EconomyDomain |
| `MONEY_UPDATE` | Gold changed | `copper_total` | EconomyDomain |
| `QUEST_OBJECTIVE` | Kill/item progress | `quest_id`, `obj_index`, `current`, `required` | QuestingDomain |
| `ZONE_CHANGE` | Zone transition | `old_zone`, `new_zone` | All domains |
| `REACHED_NPC` | Contact point reached | `npc_guid`, `npc_entry` | QuestingDomain |
| `LOOT_RECEIVED` | Loot from corpse | `item_id`, `count`, `creature_entry` | EconomyDomain |
| `SPELL_LEARNED` | LearnSpell completed | `spell_id` | TrainingDomain |
| `MOUNT_STATE` | Mounted/dismounted | `mounted` bool | State tracking |

---

## C++ Implementation Notes

The AiBotAI TCP client:
1. Uses a **non-blocking socket** — `fcntl(O_NONBLOCK)` on Linux, `ioctlsocket(FIONBIO)` on Windows
2. Buffers incoming data in `m_bridgeRecvBuf` (fixed `BRIDGE_RECV_BUF_SIZE`), splits on `\n`
3. Uses hand-rolled `JsonExtractString/Int/Float` — no JSON library dependency
4. `BridgeSend()` writes JSON + newline synchronously (messages are small, TCP buffering sufficient)
5. Reconnects on disconnect with exponential backoff
6. `BridgeRecv()` called every `UpdateAI()` tick — non-blocking recv processes all available data

## Error Handling

- Malformed JSON lines: logged and skipped (no disconnect)
- Unknown message types: logged and skipped
- Payload parse failures: message dropped with warning
- TCP disconnect: C# marks bot `DISCONNECTED`, C++ attempts reconnect
- Buffer overflow: buffer cleared, logged
- MOVE_TO while in combat: deferred with log message
- Quest validation failures: `QUEST_FAILED` event returned to C# with reason string

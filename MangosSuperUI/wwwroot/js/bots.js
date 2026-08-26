// MangosSuperUI — Bot Monitor JS (BotBridge + BotBrain SignalR client)
// Session 25: Stale bot cleanup — AllBots purges old entries, BotDisconnected auto-removes after 30s
// Cockpit pass: fleet census strip (counts per zone / level / class / activity, click to filter),
//   sortable + groupable roster, and the old page-wide command bar folded into a per-bot control
//   suite inside the bot modal. No BotsController changes — every action rides an existing endpoint.

$(function () {

    // ===================== CONSTANTS =====================
    var CLASS_NAMES = {
        1: 'Warrior', 2: 'Paladin', 3: 'Hunter', 4: 'Rogue',
        5: 'Priest', 7: 'Shaman', 8: 'Mage', 9: 'Warlock', 11: 'Druid'
    };
    var CLASS_CSS = {
        1: 'class-warrior', 2: 'class-paladin', 3: 'class-hunter', 4: 'class-rogue',
        5: 'class-priest', 7: 'class-shaman', 8: 'class-mage', 9: 'class-warlock', 11: 'class-druid'
    };
    var RACE_NAMES = {
        1: 'Human', 2: 'Orc', 3: 'Dwarf', 4: 'Night Elf',
        5: 'Undead', 6: 'Tauren', 7: 'Gnome', 8: 'Troll'
    };
    var TRAIT_META = {
        patience: { icon: 'fa-hourglass-half', color: '#9ece6a' },
        greed: { icon: 'fa-coins', color: '#e0af68' },
        curiosity: { icon: 'fa-compass', color: '#7aa2f7' },
        sociability: { icon: 'fa-comments', color: '#bb9af7' },
        aggression: { icon: 'fa-crosshairs', color: '#f7768e' },
        efficiency: { icon: 'fa-bolt', color: '#ff9e64' },
        cautiousness: { icon: 'fa-shield-halved', color: '#73daca' },
        indecisiveness: { icon: 'fa-shuffle', color: '#c0caf5' },
        spontaneity: { icon: 'fa-dice', color: '#2ac3de' }
    };
    var QUALITY_COLORS = {
        0: '#9d9d9d', // Poor (grey)
        1: '#ffffff', // Common (white)
        2: '#1eff00', // Uncommon (green)
        3: '#0070dd', // Rare (blue)
        4: '#a335ee', // Epic (purple)
        5: '#ff8000', // Legendary (orange)
        6: '#e6cc80'  // Artifact (light gold)
    };
    var QUALITY_NAMES = {
        0: 'Poor', 1: 'Common', 2: 'Uncommon', 3: 'Rare',
        4: 'Epic', 5: 'Legendary', 6: 'Artifact'
    };

    // Legible-on-page variants of the WoW quality colours. QUALITY_COLORS above is tuned for a
    // black game background — Common (#ffffff) is invisible on this page's light card surface,
    // which is exactly why backpack item names looked blank while the hover tooltip showed them.
    // QUALITY_COLORS stays for the dark floating tooltip; QUALITY_TEXT is what the page uses.
    var QUALITY_TEXT = {
        0: '#8a8f98', // Poor
        1: 'var(--text-primary)', // Common — theme text, never white-on-white
        2: '#16a34a', // Uncommon
        3: '#0062c4', // Rare
        4: '#8b2fd6', // Epic
        5: '#d97706', // Legendary
        6: '#a67c2e'  // Artifact
    };

    // Zone ids as they come off AreaTable.dbc (the spine reports ctx.ZoneId; quest_template
    // reuses the same ids in ZoneOrSort, with negatives meaning "class sort" rather than a zone).
    // Anything not listed falls back to "Zone N" — add ids here as you meet them, nothing else
    // depends on this table being complete.
    var ZONE_NAMES = {
        1: 'Dun Morogh', 3: 'Badlands', 4: 'Blasted Lands', 8: 'Swamp of Sorrows',
        9: 'Northshire Valley', 10: 'Duskwood', 11: 'Wetlands', 12: 'Elwynn Forest',
        14: 'Durotar', 15: 'Dustwallow Marsh', 16: 'Azshara', 17: 'The Barrens',
        25: 'Blackrock Mountain', 28: 'Western Plaguelands', 33: 'Stranglethorn Vale',
        36: 'Alterac Mountains', 38: 'Loch Modan', 40: 'Westfall', 41: 'Deadwind Pass',
        44: 'Redridge Mountains', 45: 'Arathi Highlands', 46: 'Burning Steppes',
        47: 'The Hinterlands', 51: 'Searing Gorge', 85: 'Tirisfal Glades',
        130: 'Silverpine Forest', 139: 'Eastern Plaguelands', 141: 'Teldrassil',
        148: 'Darkshore', 215: 'Mulgore', 267: 'Hillsbrad Foothills', 331: 'Ashenvale',
        357: 'Feralas', 361: 'Felwood', 400: 'Thousand Needles', 405: 'Desolace',
        406: 'Stonetalon Mountains', 440: 'Tanaris', 490: "Un'Goro Crater",
        493: 'Moonglade', 618: 'Winterspring', 796: 'Scarlet Monastery',
        1176: "Zul'Farrak", 1337: 'Uldaman', 1377: 'Silithus',
        1417: 'The Temple of Atal\'Hakkar', 1497: 'Undercity', 1519: 'Stormwind City',
        1537: 'Ironforge', 1581: 'The Deadmines', 1583: 'Blackrock Spire',
        1584: 'Blackrock Depths', 1637: 'Orgrimmar', 1638: 'Thunder Bluff',
        1657: 'Darnassus', 1977: "Zul'Gurub", 2017: 'Stratholme', 2057: 'Scholomance',
        2100: 'Maraudon', 2159: "Onyxia's Lair", 2257: 'Deeprun Tram',
        2597: 'Alterac Valley', 2717: 'Molten Core', 3277: 'Warsong Gulch',
        3358: 'Arathi Basin', 3428: "Ahn'Qiraj", 3429: "Ruins of Ahn'Qiraj",
        3456: 'Naxxramas',
        // quest_template ZoneOrSort negatives (class-sorted quests, not zones)
        '-81': 'Warrior', '-141': 'Paladin', '-261': 'Mage'
    };

    // Map ids — used as the zone fallback when the brain has no live context for a bot
    // (bridge STATE always carries mapId; zoneId only arrives with the spine projection).
    var MAP_NAMES = {
        0: 'Eastern Kingdoms', 1: 'Kalimdor', 30: 'Alterac Valley', 33: 'Shadowfang Keep',
        34: 'The Stockade', 36: 'The Deadmines', 43: 'Wailing Caverns', 47: 'Razorfen Kraul',
        48: 'Blackfathom Deeps', 70: 'Uldaman', 90: 'Gnomeregan', 109: 'Sunken Temple',
        129: 'Razorfen Downs', 189: 'Scarlet Monastery', 209: "Zul'Farrak",
        229: 'Blackrock Spire', 230: 'Blackrock Depths', 249: "Onyxia's Lair",
        289: 'Scholomance', 309: "Zul'Gurub", 329: 'Stratholme', 349: 'Maraudon',
        389: 'Ragefire Chasm', 409: 'Molten Core', 429: 'Dire Maul', 469: 'Blackwing Lair',
        489: 'Warsong Gulch', 509: "Ruins of Ahn'Qiraj", 529: 'Arathi Basin',
        531: "Temple of Ahn'Qiraj", 533: 'Naxxramas'
    };
    var EQUIP_SLOT_NAMES = {
        0: 'Non-equip', 1: 'Head', 2: 'Neck', 3: 'Shoulder', 4: 'Shirt',
        5: 'Chest', 6: 'Waist', 7: 'Legs', 8: 'Feet', 9: 'Wrists',
        10: 'Hands', 11: 'Finger', 12: 'Trinket', 13: 'One-Hand',
        14: 'Shield', 15: 'Ranged', 16: 'Back', 17: 'Two-Hand',
        18: 'Bag', 20: 'Robe', 21: 'Main Hand', 22: 'Off Hand',
        23: 'Holdable', 24: 'Ammo', 25: 'Thrown', 26: 'Ranged'
    };
    var ITEM_CLASS_NAMES = {
        0: 'Consumable', 1: 'Container', 2: 'Weapon', 4: 'Armor',
        5: 'Reagent', 6: 'Projectile', 7: 'Trade Goods', 9: 'Recipe',
        11: 'Quiver', 12: 'Quest', 13: 'Key', 15: 'Miscellaneous'
    };
    var COMBAT_ROLE_NAMES = {
        1: 'Melee DPS', 2: 'Ranged DPS', 3: 'Tank', 4: 'Healer'
    };

    // ===================== STATE =====================
    var connection = null;
    var connected = false;
    var botStates = {};       // guid → BotState (from bridge)
    var botBrains = {};       // guid → brain data (personality, decisions)
    var selectedGuid = null;
    var decisionLog = {};     // guid → array of decision entries
    var decisionCount = 0;
    var dpmStartTime = Date.now();
    var engineEnabled = false;
    var maxTimelineEntries = 100;
    var inventoryCache = {};  // guid → inventory data from /Bots/Inventory
    // The combat-loadout endpoint keeps the existing flat talent projection and adds
    // authoritative rotation state plus the guarded build-change catalog. It remains
    // deliberately lazy: only the selected bot ever causes a request.
    var combatLoadoutCache = {};     // guid → { data, fetchedAt }
    var combatLoadoutLoading = {};   // guid → true while GET is in flight
    var combatLoadoutApplying = {};  // guid → true while POST is awaiting the core
    var combatLoadoutQueueBusy = {}; // guid → 'saving' | 'cancelling' while queue state mutates
    var combatLoadoutRefreshPending = {}; // guid → refetch after an in-flight GET settles
    var combatLoadoutNotices = {};   // guid → last structured apply error/status for the editor

    // Spellbook: a read-only character_spell projection (Spell/SkillLine DBC resolved,
    // grouped into the client's own spellbook tabs). Cached per bot and fetched only for
    // the selected one, exactly like the combat loadout. It is invalidated by the events
    // that can actually change a learned set — connect, level up, applied build — and
    // NOT by the combat/death transitions that merely unlock the build editor.
    var spellbookCache = {};     // guid → { data, fetchedAt }
    var spellbookLoading = {};   // guid → true while GET is in flight
    var spellbookView = {};      // guid → { q, topOnly, hidePassive, classOnly, collapsed:{} }

    // Cockpit (fleet census + roster ordering). liveFleet is the /Bots/LiveFleet projection
    // keyed by guid — it carries zoneId/goal/step, which bridge STATE does not, and is the only
    // reason the roster can group by zone. It is optional: with the brain off the cockpit still
    // works off bridge state alone and zones fall back to map names.
    var liveFleet = {};             // guid -> live spine projection
    var fleetPollTimer = null;
    var rosterDirty = false;        // in-place card updates set this; the re-sort runs on a timer
    var dirtyCards = {};            // guid -> true; STATE packets queue here, flushDirtyCards repaints
    // Roster virtualization: only the rows in view exist in the DOM. Without this a fleet-sized
    // roster puts hundreds of thousands of nodes on the page, and every re-sort rebuilds them all.
    var rosterRows = [];            // flat display list: {t:'c',guid} | {t:'h',label,count}
    var rosterOffsets = [];         // rosterOffsets[i] = pixel offset of row i (len = rows+1)
    var rosterCardH = 58;           // measured from the first rendered card
    var rosterHeadH = 30;           // measured from the first rendered group header
    var rosterMeasured = false;
    var rosterWinTick = false;      // rAF throttle for scroll
    var ROSTER_OVERSCAN = 6;        // rows rendered beyond the viewport on each side
    var rosterSort = localStorage.getItem('msui_bots_sort') || 'level_desc';
    var rosterGroupBy = localStorage.getItem('msui_bots_group') || 'none';
    var rosterSearch = '';
    var cockpitFilter = null;       // { kind:'zone'|'level'|'class'|'activity'|'group'|'flag', key }

    // Live tab (real-time BotContext feed)
    var detailTab = 'overview';   // 'overview' | 'talents' | 'spellbook' | 'live'
    var livePollTimer = null;     // 5s server poll
    var liveTickTimer = null;     // 1s client-side age ticker
    var liveData = null;          // last /Bots/LiveState payload
    var liveGuid = null;          // guid liveData belongs to
    var liveFetchedAt = 0;        // Date.now() of last fetch (for age offset)
    var liveScaffoldGuid = null;  // guid the live DOM scaffold was built for
    var liveLogTimer = null;      // 2s per-bot log poll
    var liveLogSeq = 0;           // last-seen log sequence cursor
    var liveQuestTimer = null;    // 4s per-bot server quest-status poll
    var liveQuestMap = {};        // questId → server queststatus row (authoritative kill credit)
    var liveQuestGuid = null;     // guid liveQuestMap belongs to
    var liveLastFleetAt = 0;      // wall-clock of last fleet heartbeat shown in this bot's log
    var FLEET_MIN_MS = 90000;     // show the fleet census at most once per ~90s per bot

    // ===================== SIGNALR =====================
    function initConnection() {
        connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/botbridge')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        // --- Bridge events ---

        // Session 25: AllBots now purges stale entries from previous sessions
        connection.on('AllBots', function (bots) {
            var newStates = {};
            for (var i = 0; i < bots.length; i++) newStates[bots[i].guid] = bots[i];

            // Remove brain/decision/inventory/DOM for bots no longer present
            var oldGuids = Object.keys(botStates);
            for (var i = 0; i < oldGuids.length; i++) {
                var g = oldGuids[i];
                if (!newStates[g]) {
                    delete botBrains[g];
                    delete decisionLog[g];
                    delete inventoryCache[g];
                    delete combatLoadoutCache[g];
                    delete combatLoadoutLoading[g];
                    delete combatLoadoutApplying[g];
                    delete combatLoadoutQueueBusy[g];
                    delete combatLoadoutRefreshPending[g];
                    delete combatLoadoutNotices[g];
                    $('#roster-' + g).remove();
                }
            }

            botStates = newStates;
            renderRoster();
            updateStats();
            updateBotDropdown();

            // If selected bot no longer exists, deselect
            if (selectedGuid && !botStates[selectedGuid]) {
                selectedGuid = null;
                $('#detailEmpty').show();
                $('#detailPanel').empty();
                stopBrainPoll();
                stopLivePoll();
            }
        });

        connection.on('BotConnected', function (state) {
            botStates[state.guid] = state;
            invalidateCombatLoadout(state.guid, selectedGuid === state.guid && detailTab === 'talents');
            invalidateSpellbook(state.guid);
            // NOT renderRoster() — during a mass spawn this fires once per bot, and a full
            // rebuild per arrival is quadratic. rosterResortIfNeeded picks the new bot up on
            // its next tick; updateStats has its own 5s timer.
            rosterDirty = true;
            tlAppend(state.guid, 'Connected: ' + state.name + ' L' + state.level + ' ' + (CLASS_NAMES[state.classId] || ''), 'bt-tl-event');
        });

        // Session 25: BotDisconnected auto-removes after 30s if bot doesn't reconnect
        connection.on('BotDisconnected', function (guid) {
            if (botStates[guid]) {
                botStates[guid].taskState = 'DISCONNECTED';
                dirtyCards[guid] = true;
                tlAppend(guid, 'Disconnected', 'bt-tl-error');

                // Auto-remove after 30s if still disconnected (nuke/shutdown)
                setTimeout(function () {
                    if (botStates[guid] && botStates[guid].taskState === 'DISCONNECTED') {
                        delete botStates[guid];
                        delete botBrains[guid];
                        delete decisionLog[guid];
                        delete inventoryCache[guid];
                        delete combatLoadoutCache[guid];
                        delete combatLoadoutLoading[guid];
                        delete combatLoadoutApplying[guid];
                        delete combatLoadoutQueueBusy[guid];
                        delete combatLoadoutRefreshPending[guid];
                        delete combatLoadoutNotices[guid];
                        delete spellbookCache[guid];
                        delete spellbookLoading[guid];
                        delete spellbookView[guid];
                        $('#roster-' + guid).remove();
                        rosterDirty = true;

                        if (selectedGuid === parseInt(guid)) {
                            selectedGuid = null;
                            $('#detailEmpty').show();
                            $('#detailPanel').empty();
                            stopBrainPoll();
                            stopLivePoll();
                        }
                    }
                }, 30000);
            }
            invalidateCombatLoadout(guid, selectedGuid === parseInt(guid) && detailTab === 'talents');
        });

        connection.on('BotStateUpdate', function (state) {
            var previous = botStates[state.guid];
            botStates[state.guid] = state;
            // Card repaint is coalesced (see flushDirtyCards): a bot reports every
            // BRIDGE_STATE_INTERVAL, so at fleet scale this fires hundreds of times a second and
            // must not touch the DOM inline. updateStats() is NOT called here on purpose — it
            // scans the whole fleet and already runs on its own 5s timer.
            dirtyCards[state.guid] = true;
            // The selected bot is a single row, so keep it live.
            if (selectedGuid === state.guid) {
                updateEconomyStrip(state);
                // Combat/death are build-mutation blockers. Refresh only on the
                // transition so the editor unlocks promptly without turning the
                // normal STATE heartbeat into a loadout query loop.
                if (detailTab === 'talents' && (!previous || previous.inCombat !== state.inCombat || previous.isDead !== state.isDead))
                    invalidateCombatLoadout(state.guid, true);
            }
        });

        connection.on('BotEvent', function (evt) {
            var cls = 'bt-tl-event';
            var text = evt.eventType;
            if (evt.eventType === 'KILL') text += ' creature=' + evt.creatureEntry;
            else if (evt.eventType === 'LEVEL_UP') {
                text += ' → L' + evt.newLevel; cls = 'bt-tl-switch';
                invalidateCombatLoadout(evt.guid, detailTab === 'talents' && selectedGuid === evt.guid);
                invalidateSpellbook(evt.guid);   // the core trains new ranks on level up
            }
            else if (evt.eventType === 'QUEST_UPDATE') text += ' quest=' + evt.questId + ' ' + evt.status;
            else if (evt.eventType === 'SELL_ACK') { text += ' ' + (evt.data || ''); cls = 'bt-tl-switch'; }
            else if (evt.eventType === 'EQUIP') { text += ' ' + (evt.data || ''); }
            else text += ' ' + (evt.data || '');

            // Invalidate inventory cache on loot/sell/equip events
            if (['LOOT', 'SELL_ACK', 'EQUIP', 'BAG_EQUIP'].indexOf(evt.eventType) >= 0) {
                delete inventoryCache[evt.guid];
            }
            tlAppend(evt.guid, text, cls);
        });

        connection.on('BotChatReceived', function (chat) {
            tlAppend(chat.guid, 'WHISPER from ' + chat.senderName + ': ' + chat.message, 'bt-tl-event');
        });

        // --- Brain events ---
        connection.on('BotBrainInit', function (data) {
            botBrains[data.guid] = data;
            renderRosterCard(data.guid);
            if (selectedGuid === data.guid) renderDetail();
            tlAppend(data.guid, 'Brain initialized — ' +
                data.personality.chatStyle + '/' + data.personality.temperament +
                (data.personality.quirks.length ? ' [' + data.personality.quirks.map(function (q) { return q.name; }).join(', ') + ']' : ''),
                'bt-tl-switch');
        });

        connection.on('BotDecision', function (data) {
            botBrains[data.guid] = botBrains[data.guid] || {};
            botBrains[data.guid].lastDecision = data;
            decisionCount++;

            if (!decisionLog[data.guid]) decisionLog[data.guid] = [];
            decisionLog[data.guid].push(data);
            if (decisionLog[data.guid].length > maxTimelineEntries)
                decisionLog[data.guid].shift();

            renderRosterCard(data.guid);
            if (selectedGuid === data.guid) {
                renderWeights(data.weights);
                renderTimeline(data.guid);
            }

            var cls = data.activityChanged ? 'bt-tl-switch' : 'bt-tl-stay';
            tlAppend(data.guid, data.decision, cls);
        });

        connection.on('CommandAck', function () { /* silent */ });

        // A successful build mutation can originate from this page now and from
        // MSUIClient later. Either way, discard the selected bot's read model and
        // ask the authoritative endpoint for the post-commit state.
        connection.on('BotCombatLoadoutChanged', function (evt) {
            var rawGuid = evt && evt.guid != null ? evt.guid : evt;
            var guid = parseInt(rawGuid);
            if (!guid) return;
            invalidateCombatLoadout(guid, selectedGuid === guid && detailTab === 'talents');
        });

        // Server-backed one-deep queue changes may originate here or from a
        // future MSUIClient. Refresh the authoritative projection either way.
        connection.on('BotCombatLoadoutQueueChanged', function (evt) {
            var rawGuid = evt && evt.guid != null ? evt.guid : evt;
            var guid = parseInt(rawGuid);
            if (!guid) return;
            invalidateCombatLoadout(guid, selectedGuid === guid && detailTab === 'talents');

            // The initiating AJAX path already owns its toast. Only announce
            // external queue changes so one action never produces two messages.
            if (!combatLoadoutQueueBusy[guid] && !combatLoadoutApplying[guid] && evt && evt.message) {
                var status = String(evt.status || (evt.queue && evt.queue.status) || '').toLowerCase();
                var failed = evt.success === false || status === 'error' || status === 'failed' || status === 'uncertain';
                showToast(evt.message, failed);
            }
        });

        // Add Bots batch progress (BotSpawnService) — the handler lives with the Add Bots modal below.
        connection.on('SpawnProgress', function (job) { abOnSpawnProgress(job); });

        // --- Lifecycle ---
        connection.onreconnecting(function () { setStatus('offline'); });
        connection.onreconnected(function () {
            setStatus('online');
            connection.invoke('GetAllBots').catch(function () { });
        });
        connection.onclose(function () { setStatus('offline'); });

        connection.start().then(function () {
            setStatus('online');
            connection.invoke('GetAllBots').catch(function () { });

            // Load brain state + grouping mode from server (survives page refresh)
            $.getJSON('/Bots/BrainStatus', function (data) {
                // Sync engine toggle to actual server state
                engineEnabled = data.enabled;
                $('#engineToggle').toggleClass('active', engineEnabled);
                $('#engineToggle').find('.bt-engine-label').text(engineEnabled ? 'Engine On' : 'Engine Off');

                // Sync grouping mode dropdown
                if (typeof data.groupingMode !== 'undefined') {
                    $('#groupingMode').val(data.groupingMode);
                    updateGroupingUI(data);
                }

                // One request for the whole fleet. This used to be a $.getJSON PER BOT, which at
                // fleet scale queues thousands of XHRs behind the browser's per-origin connection
                // limit and can wedge the tab before the page is even usable.
                if (data.bots && data.bots.length) {
                    $.getJSON('/Bots/BrainStates', function (all) {
                        var list = (all && all.bots) || [];
                        for (var i = 0; i < list.length; i++) {
                            var bs = list[i];
                            if (!bs || !bs.personality) continue;
                            botBrains[bs.guid] = bs;
                            dirtyCards[bs.guid] = true;
                            if (selectedGuid === bs.guid) renderDetail();
                        }
                    });
                }
            });
        }).catch(function (err) {
            setStatus('error');
        });
    }

    function setStatus(state) {
        connected = (state === 'online');
        $('#bridgeStatus').removeClass('online offline error').addClass(state);
        var labels = { online: 'Bridge: Connected', offline: 'Bridge: Disconnected', error: 'Bridge: Error' };
        $('#bridgeStatusText').text(labels[state] || state);
    }

    // ===================== COCKPIT + ROSTER =====================
    // The roster is a sortable / groupable / filterable fleet list. The cockpit strip above it
    // is the census (how many bots per zone, per level band, per class, per activity) and it
    // doubles as the filter control: clicking a tile or a chip filters the roster to that slice.

    // ---- derived facts about a bot (single source of truth for both the cards and the census)

    function levelBandKey(lvl) {
        lvl = parseInt(lvl, 10) || 0;
        if (lvl >= 60) return 60;
        if (lvl < 10) return 0;
        return Math.floor(lvl / 10) * 10;
    }

    function levelBandLabel(k) {
        k = parseInt(k, 10) || 0;
        if (k >= 60) return 'L60';
        if (k === 0) return 'L1-9';
        return 'L' + k + '-' + (k + 9);
    }

    function botZoneKey(guid) {
        var lf = liveFleet[guid];
        if (lf && lf.zoneId) return 'z' + lf.zoneId;
        var s = botStates[guid];
        return 'm' + ((s && s.mapId) || 0);
    }

    function zoneKeyLabel(key) {
        key = String(key);
        if (key.charAt(0) === 'z') {
            var z = parseInt(key.slice(1), 10);
            return ZONE_NAMES[z] || ('Zone ' + z);
        }
        var m = parseInt(key.slice(1), 10);
        return (MAP_NAMES[m] || ('Map ' + m));
    }

    // What the bot is doing, in priority order: brain decision > live spine goal > bridge state.
    function botActivity(guid) {
        var s = botStates[guid];
        if (!s) return { text: 'IDLE', cls: 'bt-act-idle' };
        if (s.taskState === 'DISCONNECTED') return { text: 'OFFLINE', cls: 'bt-act-idle' };

        var brain = botBrains[guid];
        var lf = liveFleet[guid];
        var text = null;
        if (brain && brain.lastDecision && brain.lastDecision.newActivity) text = brain.lastDecision.newActivity;
        else if (lf && lf.goal) text = lf.goal;
        else if (s.inCombat) text = 'COMBAT';
        else if (s.taskState && s.taskState !== 'IDLE') text = s.taskState;
        if (!text) text = 'IDLE';

        text = String(text);
        var cls = 'bt-act-' + text.toLowerCase();
        if (text.toUpperCase() === 'COMBAT') cls = 'bt-act-grinding';
        return { text: text.toUpperCase(), cls: cls };
    }

    function botGold(guid) { var s = botStates[guid]; return (s && s.copper) || 0; }
    function botHpPct(guid) {
        var s = botStates[guid];
        if (!s || !s.maxHealth) return 100;
        return Math.round((s.health || 0) / s.maxHealth * 100);
    }

    // ---- filtering

    function passesCockpitFilter(guid) {
        if (!cockpitFilter) return true;
        var s = botStates[guid];
        if (!s) return false;
        var f = cockpitFilter;
        if (f.kind === 'zone') return botZoneKey(guid) === String(f.key);
        if (f.kind === 'level') return levelBandKey(s.level) === (parseInt(f.key, 10) || 0);
        if (f.kind === 'class') return (s.classId || 0) === (parseInt(f.key, 10) || 0);
        if (f.kind === 'activity') return botActivity(guid).text === String(f.key);
        if (f.kind === 'group') return (groupOf[guid] || 0) === (parseInt(f.key, 10) || 0);
        if (f.kind === 'flag') {
            if (f.key === 'combat') return !!s.inCombat;
            if (f.key === 'dead') return !!s.isDead;
            if (f.key === 'idle') return botActivity(guid).text === 'IDLE';
            if (f.key === 'grouped') return !!groupOf[guid];
            if (f.key === 'bagsfull') return (s.freeSlots != null && s.freeSlots <= 0);
            if (f.key === 'offline') return s.taskState === 'DISCONNECTED';
        }
        return true;
    }

    function cockpitFilterLabel() {
        if (!cockpitFilter) return '';
        var f = cockpitFilter;
        if (f.kind === 'zone') return 'zone: ' + zoneKeyLabel(f.key);
        if (f.kind === 'level') return 'level: ' + levelBandLabel(f.key);
        if (f.kind === 'class') return 'class: ' + (CLASS_NAMES[f.key] || f.key);
        if (f.kind === 'activity') return 'doing: ' + f.key;
        if (f.kind === 'group') return 'group #' + f.key;
        return f.key;
    }

    function setCockpitFilter(kind, key) {
        if (cockpitFilter && cockpitFilter.kind === kind && String(cockpitFilter.key) === String(key)) cockpitFilter = null;
        else cockpitFilter = { kind: kind, key: key };
        renderCockpit();
        renderRoster();
    }

    // ---- sorting

    function rosterComparator(a, b) {
        var sa = botStates[a] || {}, sb = botStates[b] || {};
        var byName = function () { return (sa.name || '').localeCompare(sb.name || ''); };
        switch (rosterSort) {
            case 'level_asc': return ((sa.level || 0) - (sb.level || 0)) || byName();
            case 'name': return byName();
            case 'class':
                return String(CLASS_NAMES[sa.classId] || 'z').localeCompare(String(CLASS_NAMES[sb.classId] || 'z'))
                    || ((sb.level || 0) - (sa.level || 0)) || byName();
            case 'zone':
                return zoneKeyLabel(botZoneKey(a)).localeCompare(zoneKeyLabel(botZoneKey(b)))
                    || ((sb.level || 0) - (sa.level || 0)) || byName();
            case 'activity':
                return botActivity(a).text.localeCompare(botActivity(b).text) || byName();
            case 'gold': return (botGold(b) - botGold(a)) || byName();
            case 'hp': return (botHpPct(a) - botHpPct(b)) || byName();
            case 'group':
                return ((groupOf[a] || 9999) - (groupOf[b] || 9999))
                    || ((leaderOf[b] ? 1 : 0) - (leaderOf[a] ? 1 : 0)) || byName();
            case 'level_desc':
            default: return ((sb.level || 0) - (sa.level || 0)) || byName();
        }
    }

    // ---- bucketing (group-by)

    function rosterBucket(guid) {
        var s = botStates[guid] || {};
        switch (rosterGroupBy) {
            case 'zone': return { key: botZoneKey(guid), label: zoneKeyLabel(botZoneKey(guid)), sort: zoneKeyLabel(botZoneKey(guid)) };
            case 'level': var k = levelBandKey(s.level); return { key: 'L' + k, label: levelBandLabel(k), sort: 1000 - k };
            case 'class': return { key: 'C' + (s.classId || 0), label: CLASS_NAMES[s.classId] || 'Unknown', sort: CLASS_NAMES[s.classId] || 'zz' };
            case 'activity': var a = botActivity(guid); return { key: 'A' + a.text, label: a.text, sort: a.text };
            case 'group':
                var g = groupOf[guid] || 0;
                return { key: 'G' + g, label: g ? ('Group #' + g) : 'Ungrouped', sort: g ? g : 9999 };
            default: return null;
        }
    }

    // ---- roster render

    var rosterSig = '';   // ordered guid+bucket signature of the last DOM build

    function rosterSignature(guids) {
        var parts = [];
        for (var i = 0; i < guids.length; i++) {
            var g = guids[i];
            parts.push(rosterGroupBy === 'none' ? g : (g + ':' + rosterBucket(g).key));
        }
        return parts.join(',');
    }

    // Called on a timer when cards report themselves dirty: re-sorts the DOM only if the order
    // or the bucketing actually moved, so a stream of STATE packets doesn't rebuild the list
    // under the operator's cursor every few seconds.
    function rosterResortIfNeeded() {
        renderCockpit();
        if (!rosterDirty) return;
        var guids = rosterVisibleGuids();
        if (rosterSignature(guids) !== rosterSig) renderRoster();
        else rosterDirty = false;
    }

    function rosterVisibleGuids() {
        var q = (rosterSearch || '').toLowerCase();
        var out = [];
        var keys = Object.keys(botStates);
        for (var i = 0; i < keys.length; i++) {
            var g = parseInt(keys[i], 10);
            var s = botStates[g];
            if (!s) continue;
            if (q && String(s.name || '').toLowerCase().indexOf(q) < 0) continue;
            if (!passesCockpitFilter(g)) continue;
            out.push(g);
        }
        out.sort(rosterComparator);
        return out;
    }

    function renderRoster() {
        var $r = $('#botRoster');
        if ($r.length === 0) return;

        var total = Object.keys(botStates).length;
        if (total === 0) {
            $('#rosterEmpty').show();
            $r.find('.bt-roster-card, .bt-roster-ghead').remove();
            $('#rosterCount').text('0');
            renderCockpit();
            return;
        }
        $('#rosterEmpty').hide();

        var guids = rosterVisibleGuids();
        $('#rosterCount').text(guids.length === total ? String(total) : (guids.length + ' / ' + total));

        rosterRows = [];
        if (rosterGroupBy === 'none') {
            for (var i = 0; i < guids.length; i++) rosterRows.push({ t: 'c', guid: guids[i] });
        } else {
            var buckets = {}, order = [];
            for (var j = 0; j < guids.length; j++) {
                var b = rosterBucket(guids[j]);
                if (!buckets[b.key]) { buckets[b.key] = { meta: b, guids: [] }; order.push(b.key); }
                buckets[b.key].guids.push(guids[j]);
            }
            order.sort(function (x, y) {
                var mx = buckets[x].meta.sort, my = buckets[y].meta.sort;
                if (typeof mx === 'number' && typeof my === 'number') return mx - my;
                return String(mx).localeCompare(String(my));
            });
            for (var k = 0; k < order.length; k++) {
                var bk = buckets[order[k]];
                rosterRows.push({ t: 'h', label: bk.meta.label, count: bk.guids.length });
                for (var m = 0; m < bk.guids.length; m++) rosterRows.push({ t: 'c', guid: bk.guids[m] });
            }
        }
        rosterRebuildOffsets();

        renderRosterWindow();
        rosterSig = rosterSignature(guids);
        rosterDirty = false;
        updateBotDropdown();
        renderCockpit();
    }

    // Pixel offset of every row, so the window can be located without measuring the DOM.
    function rosterRebuildOffsets() {
        rosterOffsets = new Array(rosterRows.length + 1);
        var acc = 0;
        for (var i = 0; i < rosterRows.length; i++) {
            rosterOffsets[i] = acc;
            acc += rosterRows[i].t === 'c' ? rosterCardH : rosterHeadH;
        }
        rosterOffsets[rosterRows.length] = acc;
    }

    // First row whose bottom edge is past y.
    function rosterRowAt(y) {
        var lo = 0, hi = rosterRows.length - 1, best = 0;
        while (lo <= hi) {
            var mid = (lo + hi) >> 1;
            if (rosterOffsets[mid] <= y) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best;
    }

    function rosterEnsurePads($r) {
        if (!$r.find('#rosterPadTop').length) $r.append('<div id="rosterPadTop" style="height:0"></div>');
        if (!$r.find('#rosterPadBot').length) $r.append('<div id="rosterPadBot" style="height:0"></div>');
    }

    // Cards are uniform, so one real measurement calibrates the whole list.
    function rosterMeasure($r) {
        if (rosterMeasured) return false;
        var $c = $r.find('.bt-roster-card').first();
        if (!$c.length) return false;
        var h = $c.outerHeight(true);
        if (!h) return false;
        var $h = $r.find('.bt-roster-ghead').first();
        var hh = $h.length ? $h.outerHeight(true) : rosterHeadH;
        rosterMeasured = true;
        if (Math.abs(h - rosterCardH) > 1 || (hh && Math.abs(hh - rosterHeadH) > 1)) {
            rosterCardH = h; if (hh) rosterHeadH = hh;
            rosterRebuildOffsets();
            return true;   // caller re-renders once with correct geometry
        }
        return false;
    }

    // Renders only the rows intersecting the viewport; two spacer divs preserve scroll geometry.
    function renderRosterWindow() {
        var $r = $('#botRoster');
        if ($r.length === 0) return;
        var el = $r[0];
        rosterEnsurePads($r);

        var total = rosterOffsets[rosterRows.length] || 0;
        var view = el.clientHeight || 600;
        var over = ROSTER_OVERSCAN * rosterCardH;
        var start = rosterRows.length ? rosterRowAt(Math.max(0, el.scrollTop - over)) : 0;
        var end = rosterRows.length ? rosterRowAt(el.scrollTop + view + over) + 1 : 0;
        if (end > rosterRows.length) end = rosterRows.length;

        var html = '';
        for (var i = start; i < end; i++) {
            var r = rosterRows[i];
            html += r.t === 'c'
                ? rosterCardHtml(r.guid)
                : '<div class="bt-roster-ghead">' + esc(r.label) +
                  '<span class="bt-roster-gcount">' + r.count + '</span></div>';
        }

        $r.find('.bt-roster-card, .bt-roster-ghead').remove();
        $('#rosterPadTop').css('height', (rosterOffsets[start] || 0) + 'px').after(html);
        $('#rosterPadBot').css('height', Math.max(0, total - (rosterOffsets[end] || 0)) + 'px');

        if (rosterMeasure($r)) renderRosterWindow();   // once, with real heights
    }

    function rosterCardHtml(guid) {
        var s = botStates[guid] || {};
        var cls = 'bt-roster-card' +
            (s.taskState === 'DISCONNECTED' ? ' disconnected' : '') +
            (selectedGuid === guid ? ' selected' : '');
        return '<div class="' + cls + '" data-guid="' + guid + '" id="roster-' + guid + '">' +
            rosterCardInner(guid) + '</div>';
    }

    function rosterCardInner(guid) {
        var s = botStates[guid];
        if (!s) return '';
        var isDisc = s.taskState === 'DISCONNECTED';
        var isDead = s.isDead;
        var dotCls = isDisc ? 'offline' : (isDead ? 'dead' : 'alive');
        var act = botActivity(guid);
        var className = CLASS_NAMES[s.classId] || '?';
        var raceName = RACE_NAMES[s.race] || '?';
        var hp = botHpPct(guid);
        var hpColor = hp < 35 ? '#f7768e' : (hp < 70 ? '#e0af68' : '#9ece6a');
        var zone = zoneKeyLabel(botZoneKey(guid));
        var bags = (s.freeSlots != null && s.totalSlots) ? (s.totalSlots - s.freeSlots) + '/' + s.totalSlots : null;

        return '<span class="bt-roster-dot ' + dotCls + '"></span>' +
            '<div class="bt-roster-info">' +
            '<div class="bt-roster-name">' + esc(s.name) +
            '<span class="bt-roster-lvl">L' + (s.level || 0) + '</span>' +
            '<span class="bt-class-badge ' + (CLASS_CSS[s.classId] || '') + '">' + className + '</span>' +
            groupBadgeHtml(guid) + '</div>' +
            '<div class="bt-roster-meta">' +
            '<span class="bt-roster-zone" title="' + esc(zone) + '"><i class="fa-solid fa-location-dot"></i> ' + esc(zone) + '</span>' +
            '<span style="color:' + hpColor + ';">' + hp + '%</span>' +
            '<span style="color:#e0af68;">' + formatGold(botGold(guid)) + '</span>' +
            (bags ? '<span title="bag slots used">' + bags + '</span>' : '') +
            '<span class="bt-roster-race">' + raceName + '</span>' +
            '</div></div>' +
            '<div class="bt-roster-right">' +
            '<span class="bt-roster-activity ' + act.cls + '">' + esc(act.text) + '</span>' +
            '<button class="bt-roster-ctl btnBotControl" data-guid="' + guid + '" title="Open control suite for this bot">' +
            '<i class="fa-solid fa-sliders"></i></button>' +
            '</div>';
    }

    // In-place refresh of one card. Called from every state / brain / decision event, so it must
    // stay cheap and must NOT re-sort — reordering on every STATE packet makes the list unusable.
    // It flags the roster dirty instead; the 3s tick below re-sorts once.
    // Repaints only the cards that actually changed since the last tick, and only those currently
    // in the DOM. Bounded work per tick no matter how large the fleet is.
    function flushDirtyCards() {
        var guids = Object.keys(dirtyCards);
        if (guids.length === 0) return;
        dirtyCards = {};
        for (var i = 0; i < guids.length; i++) renderRosterCard(parseInt(guids[i], 10));
    }

    function renderRosterCard(guid) {
        var s = botStates[guid];
        if (!s) return;
        var $card = $('#roster-' + guid);
        if ($card.length === 0) { rosterDirty = true; return; }
        $card.html(rosterCardInner(guid));
        $card.toggleClass('disconnected', s.taskState === 'DISCONNECTED');
        $card.toggleClass('selected', selectedGuid === guid);
        rosterDirty = true;
    }

    // ---- cockpit census

    function cockpitTile(label, val, color, flag, title) {
        var on = cockpitFilter && cockpitFilter.kind === 'flag' && cockpitFilter.key === flag;
        return '<div class="bt-tile' + (flag ? ' clickable' : '') + (on ? ' on' : '') + '"' +
            (flag ? ' data-flag="' + flag + '"' : '') +
            (title ? ' title="' + esc(title) + '"' : '') + '>' +
            '<div class="bt-tile-val" style="color:' + color + ';">' + val + '</div>' +
            '<div class="bt-tile-lbl">' + esc(label) + '</div></div>';
    }

    function cockpitChip(kind, key, label, count) {
        var on = cockpitFilter && cockpitFilter.kind === kind && String(cockpitFilter.key) === String(key);
        return '<span class="bt-chip' + (on ? ' on' : '') + '" data-fk="' + kind + '" data-fv="' + esc(String(key)) + '">' +
            esc(label) + '<b>' + count + '</b></span>';
    }

    function chipRow(icon, title, kind, counts, labelFn) {
        var keys = Object.keys(counts);
        if (keys.length === 0) return '';
        keys.sort(function (a, b) { return counts[b] - counts[a] || String(a).localeCompare(String(b)); });
        var html = '<div class="bt-chiprow"><span class="bt-chiprow-h"><i class="fa-solid ' + icon + '"></i>' + esc(title) + '</span>';
        for (var i = 0; i < keys.length; i++) html += cockpitChip(kind, keys[i], labelFn(keys[i]), counts[keys[i]]);
        return html + '</div>';
    }

    function renderCockpit() {
        var $tiles = $('#fleetTiles');
        if ($tiles.length === 0) return;

        var guids = Object.keys(botStates).map(function (g) { return parseInt(g, 10); });
        var online = 0, dead = 0, combat = 0, idle = 0, grouped = 0, bagsFull = 0, offline = 0;
        var gold = 0, lvlSum = 0, lvlN = 0;
        var zones = {}, bands = {}, classes = {}, acts = {};

        for (var i = 0; i < guids.length; i++) {
            var g = guids[i], s = botStates[g];
            if (!s) continue;
            if (s.taskState === 'DISCONNECTED') { offline++; continue; }
            online++;
            if (s.isDead) dead++;
            if (s.inCombat) combat++;
            if (groupOf[g]) grouped++;
            if (s.freeSlots != null && s.freeSlots <= 0) bagsFull++;
            gold += (s.copper || 0);
            if (s.level) { lvlSum += s.level; lvlN++; }

            var a = botActivity(g);
            if (a.text === 'IDLE') idle++;
            acts[a.text] = (acts[a.text] || 0) + 1;

            var zk = botZoneKey(g); zones[zk] = (zones[zk] || 0) + 1;
            var bk = levelBandKey(s.level); bands[bk] = (bands[bk] || 0) + 1;
            var ck = s.classId || 0; classes[ck] = (classes[ck] || 0) + 1;
        }

        var avgLvl = lvlN ? (lvlSum / lvlN).toFixed(1) : '—';
        var tiles = '';
        tiles += cockpitTile('Online', online, 'var(--text-primary)', null, 'Bots connected on the bridge');
        tiles += cockpitTile('In combat', combat, '#f7768e', 'combat', 'Filter to bots in combat');
        tiles += cockpitTile('Dead', dead, '#f7768e', 'dead', 'Filter to corpses');
        tiles += cockpitTile('Idle', idle, '#8d96a0', 'idle', 'Filter to bots with no activity');
        tiles += cockpitTile('Grouped', grouped, '#7aa2f7', 'grouped', 'Filter to bots in a group');
        tiles += cockpitTile('Bags full', bagsFull, '#e0af68', 'bagsfull', 'Filter to bots with 0 free slots');
        tiles += cockpitTile('Avg level', avgLvl, '#9ece6a', null, 'Mean level of connected bots');
        tiles += cockpitTile('Fleet gold', formatGold(gold), '#e0af68', null, 'Total copper carried by the fleet');
        if (offline > 0) tiles += cockpitTile('Offline', offline, '#5f6b7a', 'offline', 'Bots that dropped the bridge');
        $tiles.html(tiles);

        var bd = '';
        bd += chipRow('fa-map-location-dot', 'Zones', 'zone', zones, function (k) { return zoneKeyLabel(k); });
        bd += chipRow('fa-arrow-up-9-1', 'Levels', 'level', bands, function (k) { return levelBandLabel(k); });
        bd += chipRow('fa-shield-halved', 'Classes', 'class', classes, function (k) { return CLASS_NAMES[k] || ('Class ' + k); });
        bd += chipRow('fa-person-running', 'Doing', 'activity', acts, function (k) { return k; });
        $('#fleetBreakdown').html(bd);

        var $fp = $('#cockpitFilterPill');
        if (cockpitFilter) $fp.html('<i class="fa-solid fa-filter"></i> ' + esc(cockpitFilterLabel()) + ' <i class="fa-solid fa-xmark"></i>').show();
        else $fp.hide();
    }

    // ---- cockpit / roster controls

    $(document).on('click', '#fleetTiles .bt-tile.clickable', function () {
        setCockpitFilter('flag', $(this).data('flag'));
    });
    $(document).on('click', '#fleetBreakdown .bt-chip', function () {
        setCockpitFilter($(this).data('fk'), $(this).data('fv'));
    });
    $(document).on('click', '#cockpitFilterPill', function () {
        cockpitFilter = null; renderCockpit(); renderRoster();
    });
    $(document).on('input', '#rosterSearch', function () {
        rosterSearch = $(this).val() || '';
        renderRoster();
    });
    $(document).on('change', '#rosterSort', function () {
        rosterSort = $(this).val();
        localStorage.setItem('msui_bots_sort', rosterSort);
        renderRoster();
    });
    $(document).on('change', '#rosterGroup', function () {
        rosterGroupBy = $(this).val();
        localStorage.setItem('msui_bots_group', rosterGroupBy);
        renderRoster();
    });
    $(document).on('click', '#cockpitToggle', function () {
        var off = $('.bt-page').toggleClass('cockpit-off').hasClass('cockpit-off');
        localStorage.setItem('msui_bots_cockpit', off ? '0' : '1');
        $(this).find('i').attr('class', off ? 'fa-solid fa-chevron-down' : 'fa-solid fa-chevron-up');
    });

    // ---- live fleet poll (zone + goal enrichment; harmless when the brain is off)

    function fetchFleet() {
        $.getJSON('/Bots/LiveFleet', function (d) {
            var arr = (d && d.bots) || [];
            var next = {};
            for (var i = 0; i < arr.length; i++) {
                var b = arr[i];
                if (b && b.guid != null) next[b.guid] = b;
            }
            liveFleet = next;
            renderRoster();
        }).fail(function () { /* brain off / endpoint busy — cockpit falls back to bridge state */ });
    }

    function startFleetPoll() {
        fetchFleet();
        if (fleetPollTimer) clearInterval(fleetPollTimer);
        fleetPollTimer = setInterval(fetchFleet, 8000);
    }

    // ===================== DETAIL PANEL =====================

    function renderDetail() {
        if (!selectedGuid) {
            $('#detailEmpty').show();
            stopLivePoll();
            return;
        }
        $('#detailEmpty').hide();

        var s = botStates[selectedGuid];
        if (!s) return;

        // Talents are loaded only for the selected bot; there is no fleet-sized DB fan-out.
        var scaffold =
            '<div class="bt-detail-tabs">' +
            '<div class="bt-detail-tab' + (detailTab === 'overview' ? ' active' : '') + '" data-dtab="overview">' +
            '<i class="fa-solid fa-id-card" style="margin-right:5px;"></i>Overview</div>' +
            '<div class="bt-detail-tab' + (detailTab === 'talents' ? ' active' : '') + '" data-dtab="talents">' +
            '<i class="fa-solid fa-diagram-project" style="margin-right:5px;"></i>Talents &amp; Rotation</div>' +
            '<div class="bt-detail-tab' + (detailTab === 'spellbook' ? ' active' : '') + '" data-dtab="spellbook">' +
            '<i class="fa-solid fa-book-sparkles" style="margin-right:5px;"></i>Spellbook</div>' +
            '<div class="bt-detail-tab' + (detailTab === 'live' ? ' active' : '') + '" data-dtab="live">' +
            '<i class="fa-solid fa-satellite-dish" style="margin-right:5px;"></i>Live' +
            '<span class="bt-live-dot"></span></div>' +
            '</div>' +
            '<div id="detailTabBody"></div>';
        $('#detailPanel').html(scaffold);

        renderActiveDetailTab();
        ensureLivePoll();
    }

    function renderActiveDetailTab() {
        if (!selectedGuid) return;
        var s = botStates[selectedGuid];
        if (!s) return;

        if (detailTab === 'live') {
            renderLiveTab(s);
            return;
        }
        if (detailTab === 'talents') {
            renderTalentsTab(s);
            return;
        }
        if (detailTab === 'spellbook') {
            renderSpellbookTab(s);
            return;
        }
        var brain = botBrains[selectedGuid];
        try { renderDetailInner(s, brain); }
        catch (ex) {
            console.error('renderDetail crashed for guid ' + selectedGuid + ':', ex);
            $('#detailTabBody').html('<div class="bt-talent-error"><i class="fa-solid fa-triangle-exclamation"></i><div><b>Detail render error</b><span>' + esc(ex.message) + '</span></div></div>');
        }
    }

    // ===================== COMBAT LOADOUT TAB =====================
    // Persisted profile + exact DBC ranks, effective runtime rotation, and the
    // guarded build editor. Fetches remain selected-bot-only.

    function renderTalentsTab(s) {
        var g = selectedGuid;
        var cached = combatLoadoutCache[g];
        if (!cached) {
            $('#detailTabBody').html('<div class="bt-talent-loading"><i class="fa-solid fa-spinner fa-spin"></i> Reading talents, build policy, and live rotation…</div>');
            fetchCombatLoadout(g, false);
            return;
        }

        renderCombatLoadoutData(cached.data, s);
        if (Date.now() - cached.fetchedAt > 15000) fetchCombatLoadout(g, true);
    }

    function invalidateCombatLoadout(guid, refetch) {
        guid = parseInt(guid);
        if (!guid) return;
        delete combatLoadoutCache[guid];
        if (combatLoadoutLoading[guid]) {
            if (refetch) combatLoadoutRefreshPending[guid] = true;
            return;
        }
        if (refetch && selectedGuid === guid && detailTab === 'talents')
            fetchCombatLoadout(guid, true);
    }

    function fetchCombatLoadout(guid, force) {
        guid = parseInt(guid);
        if (!guid || combatLoadoutLoading[guid]) return;
        var cached = combatLoadoutCache[guid];
        if (!force && cached && Date.now() - cached.fetchedAt <= 15000) return;

        combatLoadoutLoading[guid] = true;
        $.getJSON('/Bots/CombatLoadout/' + guid)
            .done(function (data) {
                data = normalizeCombatLoadout(data);
                combatLoadoutCache[guid] = { data: data, fetchedAt: Date.now() };
                if (selectedGuid === guid && detailTab === 'talents') renderCombatLoadoutData(data, botStates[guid]);
            })
            .fail(function (xhr) {
                var body = xhr.responseJSON || {};
                var problem = parseCombatLoadoutProblem(xhr);
                var data = {
                    guid: guid,
                    errorCode: body.errorCode || body.code || 'request_failed',
                    error: (typeof body.error === 'string' && body.error) ||
                        (typeof body.message === 'string' && body.message) || problem.message
                };
                combatLoadoutCache[guid] = { data: data, fetchedAt: Date.now() };
                if (selectedGuid === guid && detailTab === 'talents') renderCombatLoadoutData(data, botStates[guid]);
            })
            .always(function () {
                delete combatLoadoutLoading[guid];
                if (combatLoadoutRefreshPending[guid]) {
                    delete combatLoadoutRefreshPending[guid];
                    delete combatLoadoutCache[guid];
                    if (selectedGuid === guid && detailTab === 'talents') fetchCombatLoadout(guid, true);
                }
            });
    }

    // ===================== SPELLBOOK TAB =====================
    // Read-only /Bots/Spellbook/{guid}: every enabled character_spell row resolved
    // against build-5875 Spell.dbc, grouped by the client's own skill-line tabs, with
    // the highest known rank of each chain marked. The rank marking is the point --
    // an unusable rotation instruction is almost always a stale RANK, not a missing
    // spell, and a loaded/skipped count can never show that.

    function spellbookState(guid) {
        if (!spellbookView[guid])
            spellbookView[guid] = { q: '', topOnly: true, hidePassive: false, classOnly: false, collapsed: {} };
        return spellbookView[guid];
    }

    function renderSpellbookTab(s) {
        var g = selectedGuid;
        var cached = spellbookCache[g];
        if (!cached) {
            $('#detailTabBody').html('<div class="bt-talent-loading"><i class="fa-solid fa-spinner fa-spin"></i> Reading learned spells…</div>');
            fetchSpellbook(g, false);
            return;
        }
        renderSpellbookData(cached.data);
        if (Date.now() - cached.fetchedAt > 60000) fetchSpellbook(g, true);
    }

    function invalidateSpellbook(guid) {
        guid = parseInt(guid);
        if (!guid) return;
        delete spellbookCache[guid];
        if (selectedGuid === guid && detailTab === 'spellbook') fetchSpellbook(guid, true);
    }

    function fetchSpellbook(guid, force) {
        guid = parseInt(guid);
        if (!guid || spellbookLoading[guid]) return;
        var cached = spellbookCache[guid];
        if (!force && cached && Date.now() - cached.fetchedAt <= 60000) return;

        spellbookLoading[guid] = true;
        $.getJSON('/Bots/Spellbook/' + guid)
            .done(function (data) {
                spellbookCache[guid] = { data: data, fetchedAt: Date.now() };
                if (selectedGuid === guid && detailTab === 'spellbook') renderSpellbookData(data);
            })
            .fail(function (xhr) {
                var body = xhr.responseJSON || {};
                var data = {
                    guid: guid,
                    errorCode: body.errorCode || 'request_failed',
                    error: (typeof body.error === 'string' && body.error) || 'The spellbook could not be read.'
                };
                spellbookCache[guid] = { data: data, fetchedAt: Date.now() };
                if (selectedGuid === guid && detailTab === 'spellbook') renderSpellbookData(data);
            })
            .always(function () { delete spellbookLoading[guid]; });
    }

    function renderSpellbookData(d) {
        var guid = d.guid || selectedGuid;
        if (d.errorCode) {
            $('#detailTabBody').html('<div class="bt-talent-error"><i class="fa-solid fa-triangle-exclamation"></i><div><b>Spellbook unavailable</b>' +
                '<span>' + esc(d.error || d.errorCode) + '</span><small>Code: ' + esc(d.errorCode) + '</small></div></div>');
            return;
        }

        var view = spellbookState(guid);
        var sum = d.summary || {};
        var groups = d.groups || [];

        var html = '<div class="bt-talent-summary">' +
            talentSummaryCard('Known spells', safeInt(sum.known, 0),
                safeInt(sum.castable, 0) + ' castable · ' + safeInt(sum.passive, 0) + ' passive', 'fa-book') +
            talentSummaryCard('Top ranks', safeInt(sum.highestRank, 0),
                safeInt(sum.superseded, 0) + ' superseded rank(s) still known', 'fa-arrow-up-9-1') +
            talentSummaryCard('Class skills', safeInt(sum.classSkills, 0),
                safeInt(sum.fromTalents, 0) + ' granted by talents', 'fa-hat-wizard') +
            talentSummaryCard('Spellbook tabs', groups.length,
                (safeInt(sum.disabled, 0) + safeInt(sum.unresolved, 0)) > 0
                    ? safeInt(sum.disabled, 0) + ' disabled · ' + safeInt(sum.unresolved, 0) + ' unresolved'
                    : 'Every learned id resolved', 'fa-layer-group') +
            '</div>';

        html += renderSpellbookRotationBanner(d.rotation || {});

        html += '<div class="bt-spell-toolbar">' +
            '<div class="bt-spell-search"><i class="fa-solid fa-magnifying-glass"></i>' +
            '<input type="text" id="btSpellSearch" class="form-input" placeholder="Filter by name or spell id…" value="' + escAttr(view.q) + '" /></div>' +
            '<label><input type="checkbox" class="bt-spell-toggle" data-sbtoggle="topOnly"' + (view.topOnly ? ' checked' : '') + ' /> Top ranks only</label>' +
            '<label><input type="checkbox" class="bt-spell-toggle" data-sbtoggle="hidePassive"' + (view.hidePassive ? ' checked' : '') + ' /> Hide passives</label>' +
            '<label><input type="checkbox" class="bt-spell-toggle" data-sbtoggle="classOnly"' + (view.classOnly ? ' checked' : '') + ' /> Class skills only</label>' +
            '</div>';

        var needle = (view.q || '').trim().toLowerCase();
        var shown = 0, total = 0;
        var body = '';

        for (var gi = 0; gi < groups.length; gi++) {
            var grp = groups[gi];
            var spells = grp.spells || [];
            if (view.classOnly && !grp.isClassSkill) { total += spells.length; continue; }

            var visible = [];
            for (var si = 0; si < spells.length; si++) {
                var sp = spells[si];
                total++;
                if (view.topOnly && !sp.highestKnownRank) continue;
                if (view.hidePassive && sp.passive) continue;
                if (needle && (sp.name || '').toLowerCase().indexOf(needle) < 0 &&
                    String(sp.spellId).indexOf(needle) < 0) continue;
                visible.push(sp);
            }
            if (!visible.length) continue;
            shown += visible.length;

            var collapsed = !!view.collapsed[grp.skillLineId];
            body += '<section class="bt-spell-group' + (collapsed ? ' collapsed' : '') + '">' +
                '<header data-sbgroup="' + escAttr(String(grp.skillLineId)) + '">' +
                '<i class="fa-solid ' + (collapsed ? 'fa-chevron-right' : 'fa-chevron-down') + '"></i>' +
                '<b>' + esc(grp.name) + '</b><span>' + esc(grp.categoryName) + '</span>' +
                '<strong>' + visible.length + (visible.length !== spells.length ? ' / ' + spells.length : '') + '</strong>' +
                '</header>';
            if (!collapsed) {
                body += '<div class="bt-spell-list">';
                for (var vi = 0; vi < visible.length; vi++) body += renderSpellRow(visible[vi]);
                body += '</div>';
            }
            body += '</section>';
        }

        html += '<div class="bt-spell-count">Showing ' + shown + ' of ' + total + ' learned spell(s)</div>';
        html += body || '<div class="bt-talent-warning"><i class="fa-solid fa-circle-info"></i>No learned spell matches the current filter.</div>';
        html += '<div class="bt-talent-source"><i class="fa-solid fa-database"></i> Learned set: <code>character_spell</code> · names, ranks, icons and the PASSIVE flag: build-5875 <code>Spell.dbc</code> · grouping and rank chains: <code>SkillLineAbility</code>/<code>SkillLine</code> · talent provenance: <code>Talent.dbc</code></div>';

        $('#detailTabBody').html(html);
    }

    function renderSpellRow(sp) {
        var classes = ['bt-spell-row'];
        if (!sp.highestKnownRank) classes.push('old');
        if (sp.passive) classes.push('passive');
        if (sp.inRotation) classes.push('rot');

        var meta = [];
        if (sp.rank) meta.push(esc(sp.rank));
        if (safeInt(sp.level, 0) > 0) meta.push('Level ' + safeInt(sp.level, 0));
        if (safeInt(sp.chainLength, 0) > 1) meta.push('rank ' + safeInt(sp.rankIndex, 0) + ' of ' + safeInt(sp.chainLength, 0) + ' known');

        var tags = '';
        if (sp.inRotation) tags += '<span class="bt-spell-tag rot" title="Named by the assigned custom rotation">P' + safeInt(sp.rotationPriority, 0) + '</span>';
        if (sp.fromTalent) tags += '<span class="bt-spell-tag talent" title="Granted by talent ' + escAttr(String(sp.talentId)) + '">' + esc(sp.talentTree || 'Talent') + '</span>';
        if (sp.passive) tags += '<span class="bt-spell-tag passive">Passive</span>';
        if (!sp.highestKnownRank) tags += '<span class="bt-spell-tag old" title="A higher rank of this spell is also known">Superseded by ' + safeInt(sp.supersededBySpellId, 0) + '</span>';
        if (!sp.resolved) tags += '<span class="bt-spell-tag bad" title="No Spell.dbc row for this id">Unresolved</span>';
        else if (sp.hidden) tags += '<span class="bt-spell-tag muted" title="Spell.dbc HIDDEN_CLIENTSIDE">Hidden</span>';

        return '<div class="' + classes.join(' ') + '">' +
            '<div class="bt-spell-icon"><img src="' + escAttr(sp.iconUrl) + '" alt="" loading="lazy" /></div>' +
            '<div class="bt-spell-main"><b>' + esc(sp.name) + '</b><small>' + meta.join(' · ') + '</small></div>' +
            '<div class="bt-spell-tags">' + tags + '</div>' +
            '<button type="button" class="bt-spell-id" data-sbcopy="' + escAttr(String(sp.spellId)) + '" title="Copy spell id">' +
            sp.spellId + '</button>' +
            '</div>';
    }

    // The assigned custom rotation is a PROFILE fact, not a runtime one. The core can
    // only report how many instructions it skipped; only the profile knows which spell
    // that was, and only the spellbook knows the bot outgrew a rank it still can cast.
    function renderSpellbookRotationBanner(r) {
        if (!r.assigned)
            return '<div class="bt-talent-compat ok"><i class="fa-solid fa-circle-check"></i><div>' +
                '<b>No custom rotation assigned</b><span>In-combat casting follows the built-in spec policy, or the legacy class AI when the talent profile is not usable.</span></div></div>';

        if (!r.profileFound)
            return '<div class="bt-talent-error"><i class="fa-solid fa-triangle-exclamation"></i><div>' +
                '<b>Assigned rotation is missing</b><span>This bot is assigned <code>' + esc(r.profileName) + '</code>, but no such profile file exists. Nothing is pushed on its next login.</span></div></div>';

        var missing = r.missingSpells || [];
        var stale = r.staleRankSpells || [];
        var cls = missing.length ? 'bad' : (stale.length ? 'warn' : 'ok');
        var icon = missing.length ? 'fa-triangle-exclamation' : (stale.length ? 'fa-circle-exclamation' : 'fa-circle-check');
        var head = missing.length
            ? missing.length + ' rotation instruction(s) name a spell this bot does not know'
            : (stale.length ? stale.length + ' rotation instruction(s) name an outgrown rank' : 'Every rotation instruction is castable at its best known rank');

        var html = '<div class="bt-talent-compat ' + cls + '"><i class="fa-solid ' + icon + '"></i><div>' +
            '<b>' + esc(r.profileName) + '</b><span>' + esc(head) + '</span>' +
            '<small>' + safeInt(r.coveredCount, 0) + ' of ' + safeInt(r.instructionCount, 0) +
            ' instruction spell(s) known' + (r.description ? ' · ' + esc(r.description) : '') + '</small></div></div>';

        for (var i = 0; i < missing.length; i++)
            html += '<div class="bt-talent-warning"><i class="fa-solid fa-ban"></i>Priority ' + safeInt(missing[i].priority, 0) +
                ' — <b>' + esc(missing[i].name) + '</b> ' + esc(missing[i].rank) + ' (id ' + missing[i].spellId + ') is not learned; the core skips it.</div>';
        for (var j = 0; j < stale.length; j++)
            html += '<div class="bt-talent-warning"><i class="fa-solid fa-arrow-up"></i>Priority ' + safeInt(stale[j].priority, 0) +
                ' — <b>' + esc(stale[j].name) + '</b> ' + esc(stale[j].rank) + ' (id ' + stale[j].spellId + ') casts, but ' +
                esc(stale[j].betterRank || 'a higher rank') + ' (id ' + stale[j].betterSpellId + ') is also known.</div>';

        return html;
    }

    // The public contract is a flat extension of the talent read model. During
    // rollout, accept the service's earlier nested names too so a web/core deploy
    // cannot temporarily blank the tab while the two processes are restarted.
    function normalizeCombatLoadout(data) {
        if (!data || typeof data !== 'object' || data.error) return data;
        var talents = data.talents && typeof data.talents === 'object' ? data.talents : null;
        if (talents) {
            var talentKeys = ['name', 'classId', 'className', 'level', 'specTab', 'profile', 'points',
                'trees', 'nextPlannedPurchase', 'compatibility', 'asOfUtc'];
            for (var i = 0; i < talentKeys.length; i++) {
                var key = talentKeys[i];
                if (data[key] === undefined && talents[key] !== undefined) data[key] = talents[key];
            }
            // The wrapper's legacy ActiveRole is an integer; the flat talent model
            // carries the display object the UI needs.
            if (talents.activeRole && typeof talents.activeRole === 'object') data.activeRole = talents.activeRole;
        }
        if (!data.activeRole || typeof data.activeRole !== 'object') {
            var roleId = safeInt(data.activeRole, 0);
            data.activeRole = { id: roleId, name: COMBAT_ROLE_NAMES[roleId] || 'Unassigned' };
        }

        if (!Array.isArray(data.availableProfiles)) data.availableProfiles = data.profileOptions || [];
        if (!Array.isArray(data.availableRotations)) data.availableRotations = data.customRotations || [];
        // Older profile summaries used Name as both identity and label.
        for (var pi = 0; pi < data.availableRotations.length; pi++) {
            var option = data.availableRotations[pi];
            if (option && !option.id && option.name) option.id = option.name;
        }
        if (data.applyBlockedReason === undefined) data.applyBlockedReason = data.applyBlocker || null;
        if (data.canQueue === undefined) data.canQueue = data.online !== false;
        if (!data.queuedChange || typeof data.queuedChange !== 'object') data.queuedChange = null;

        data.rotation = data.rotation || {};
        var r = data.rotation;
        if (!r.source) r.source = r.effectiveSource || (data.online === false ? 'offline' : 'legacy');
        if (!r.profile && r.effectiveProfile) r.profile = r.effectiveProfile;
        if (!r.persistedProfile && r.desiredProfile) r.persistedProfile = r.desiredProfile;
        if (!r.talentProfileState) r.talentProfileState = data.talentProfileState || 'unknown';
        if (r.revision === undefined) r.revision = data.combatConfigRevision;
        return data;
    }

    function renderCombatLoadoutData(d, s) {
        d = normalizeCombatLoadout(d);
        if (!d) return;
        if (d.error) {
            $('#detailTabBody').html(
                '<div class="bt-talent-error"><i class="fa-solid fa-triangle-exclamation"></i><div><b>Combat loadout unavailable</b>' +
                '<span>' + esc(d.error) + '</span>' +
                (d.errorCode ? '<small>' + esc(d.errorCode) + '</small>' : '') + '</div></div>');
            return;
        }

        var p = d.profile;
        var pts = d.points || { earned: 0, spent: 0, available: 0, overspent: 0 };
        var compat = d.compatibility || { status: 'unknown', compatible: false, message: '' };
        var statusClass = compat.status === 'compatible' ? 'ok' :
            (compat.status === 'compatible_incomplete' || compat.status === 'unassigned' ? 'warn' : 'bad');
        var profileName = p ? p.name : 'Unassigned';
        var profileId = p ? p.id : 'spec_tab ' + d.specTab;
        var roleName = d.activeRole ? d.activeRole.name : 'Unassigned';
        var rotationView = combatRotationView(d);
        var html = '';

        html += '<div class="bt-talent-summary">' +
            talentSummaryCard('Specialization', profileName, profileId, 'fa-diagram-project') +
            talentSummaryCard('Active role', roleName, p ? humanPolicy(p.rolePolicy) : 'No profile policy', 'fa-people-group') +
            talentSummaryCard('Talent points', pts.spent + ' / ' + pts.earned,
                pts.available + ' available' + (pts.overspent ? ' · ' + pts.overspent + ' overspent' : ''), 'fa-star') +
            talentSummaryCard('Effective rotation', rotationView.name, rotationView.shortNote, 'fa-arrows-spin') +
            '</div>';

        html += renderEffectiveRotation(d, rotationView);
        html += renderBuildWorkshop(d, rotationView);

        html += '<div class="bt-talent-compat ' + statusClass + '">' +
            '<i class="fa-solid ' + (statusClass === 'ok' ? 'fa-circle-check' : statusClass === 'warn' ? 'fa-circle-exclamation' : 'fa-triangle-exclamation') + '"></i>' +
            '<div><b>' + esc(humanPolicy(compat.status)) + '</b><span>' + esc(compat.message || '') + '</span></div>' +
            '</div>';

        var warnings = compat.warnings || [];
        for (var wi = 0; wi < warnings.length; wi++)
            html += '<div class="bt-talent-warning"><i class="fa-solid fa-circle-info"></i>' + esc(warnings[wi]) + '</div>';

        if (d.nextPlannedPurchase) {
            var n = d.nextPlannedPurchase;
            html += '<div class="bt-talent-next' + (n.dueNow ? ' due' : '') + '">' +
                '<i class="fa-solid fa-forward-step"></i><div><span>Next planned purchase</span>' +
                '<b>' + esc(n.name) + ' · Rank ' + n.rank + '</b>' +
                '<small>' + (n.dueNow ? 'Due now' : 'Level ' + n.requiredLevel) + ' · TalentID ' + n.talentId +
                (n.spellId ? ' · Spell ' + n.spellId : '') + '</small></div></div>';
        } else if (p && pts.spent >= 51) {
            html += '<div class="bt-talent-next complete"><i class="fa-solid fa-trophy"></i><div><span>Talent plan</span><b>Complete</b><small>All 51 planned purchases are present.</small></div></div>';
        }

        html += '<div class="bt-talent-trees">';
        var trees = d.trees || [];
        for (var ti = 0; ti < trees.length; ti++) {
            var tree = trees[ti];
            html += '<section class="bt-talent-tree"><header><div><b>' + esc(tree.name) + '</b>' +
                '<span>Tree ' + (tree.order + 1) + '</span></div><strong>' + tree.points +
                '<small> / ' + tree.plannedPoints + '</small></strong></header>' +
                '<div class="bt-talent-grid">';

            var talents = tree.talents || [];
            for (var xi = 0; xi < talents.length; xi++) {
                var t = talents[xi];
                var classes = [];
                if (t.currentRank > 0) classes.push('learned');
                if (t.plannedRank > 0) classes.push('planned');
                if (t.currentRank < t.plannedRankAtLevel) classes.push('missing');
                if (t.isUnexpected) classes.push('conflict');
                if (d.nextPlannedPurchase && d.nextPlannedPurchase.talentId === t.talentId) classes.push('next');
                var tip = t.name + ' — current ' + t.currentRank + '/' + t.maxRank +
                    (t.plannedRank ? ', profile target ' + t.plannedRank : ', not in profile') +
                    ' — TalentID ' + t.talentId;
                html += '<div class="bt-talent-node ' + classes.join(' ') + '" style="grid-column:' + (t.column + 1) + ';grid-row:' + (t.row + 1) + '" title="' + escAttr(tip) + '">' +
                    '<div class="bt-talent-icon"><img src="' + escAttr(t.iconUrl) + '" alt="" loading="lazy" />' +
                    '<em>' + t.currentRank + '/' + t.maxRank + '</em></div>' +
                    '<span>' + esc(t.name) + '</span>' +
                    (t.plannedRank ? '<small>target ' + t.plannedRank + '</small>' : '') +
                    '</div>';
            }
            html += '</div><footer>Now ' + tree.points + ' · planned through level ' + d.level + ' ' + tree.plannedPointsAtLevel +
                ' · final ' + tree.plannedPoints + '</footer></section>';
        }
        html += '</div>';

        html += '<div class="bt-talent-source"><i class="fa-solid fa-database"></i> Profile and role: <code>playerbot</code> · ranks: exact <code>character_spell</code> ↔ build-5875 <code>Talent.dbc</code> mapping · rotation: live core state</div>';
        $('#detailTabBody').html(html);
        refreshBuildWorkshopState(d);
    }

    function combatRotationView(d) {
        var r = d.rotation || {};
        var raw = String(r.source || (!d.online ? 'offline' : 'legacy')).toLowerCase().replace(/[\s-]+/g, '_');
        var kind = raw.indexOf('custom') >= 0 ? 'custom' :
            (raw.indexOf('offline') >= 0 || raw.indexOf('unavailable') >= 0) ? 'offline' :
            raw.indexOf('legacy') >= 0 ? 'legacy' : 'builtin';
        var currentProfile = d.profile && d.profile.name ? d.profile.name : 'selected spec';
        var customName = r.profile || r.persistedProfile || 'Custom rotation';
        var name = kind === 'custom' ? customName :
            kind === 'offline' ? 'Offline' :
            kind === 'legacy' ? 'Legacy class AI' : currentProfile + ' built-in';
        var instructionCount = safeInt(r.instructionCount, 0);
        var castableCount = safeInt(r.castableCount, 0);
        var shortNote = kind === 'custom' ? castableCount + ' / ' + instructionCount + ' castable' :
            kind === 'offline' ? 'No live runtime state' :
            kind === 'legacy' ? 'Spec policy unavailable' : 'Hardcoded spec policy';
        return {
            kind: kind,
            name: String(name),
            shortNote: shortNote,
            instructionCount: instructionCount,
            castableCount: castableCount,
            profileState: r.talentProfileState || 'unknown',
            revision: r.revision,
            persistedProfile: r.persistedProfile || ''
        };
    }

    function renderEffectiveRotation(d, view) {
        var icon = view.kind === 'custom' ? 'fa-sliders' :
            view.kind === 'offline' ? 'fa-plug-circle-xmark' :
            view.kind === 'legacy' ? 'fa-shield-halved' : 'fa-code-branch';
        var detail = view.kind === 'custom'
            ? view.castableCount + ' of ' + view.instructionCount + ' instructions are castable by this bot.'
            : view.kind === 'builtin'
                ? 'The core is using the hardcoded rotation selected by this bot\'s usable specialization profile.'
                : view.kind === 'legacy'
                    ? 'The specialization policy is not usable, so the inherited class AI is the safe fallback.'
                    : 'The bot is offline, so no live rotation can be confirmed.';
        var meta = 'Talent profile state: ' + humanPolicy(view.profileState);
        if (view.revision != null) meta += ' · revision ' + view.revision;
        if (view.persistedProfile && view.kind !== 'custom') meta += ' · persisted custom profile ' + view.persistedProfile;
        return '<section class="bt-rotation-card source-' + view.kind + '">' +
            '<div class="bt-rotation-icon"><i class="fa-solid ' + icon + '"></i></div>' +
            '<div class="bt-rotation-copy"><span>Effective live rotation</span><b>' + esc(view.name) + '</b>' +
            '<p>' + esc(detail) + '</p><small>' + esc(meta) + '</small></div>' +
            '<span class="bt-rotation-badge">' + esc(humanPolicy(view.kind)) + '</span></section>';
    }

    function renderBuildWorkshop(d, rotationView) {
        var profiles = d.availableProfiles || [];
        var rotations = d.availableRotations || [];
        var queued = d.queuedChange;
        var currentRoleId = queued && queued.activeRole != null
            ? safeInt(queued.activeRole, 0)
            : d.activeRole && d.activeRole.id != null ? safeInt(d.activeRole.id, 0) : 0;
        var requestedSpec = queued && queued.specTab != null ? queued.specTab : d.specTab;
        var selectedProfile = findBuildProfile(d, queued && queued.profileId ? queued.profileId : requestedSpec);
        var selectedSpecTab = selectedProfile ? safeInt(selectedProfile.specTab, safeInt(requestedSpec, 255)) : safeInt(requestedSpec, 255);
        var applying = !!combatLoadoutApplying[d.guid];
        var queueBusy = combatLoadoutQueueBusy[d.guid] || '';
        var operationBusy = applying || !!queueBusy;
        var canApplyNow = d.online !== false && d.canApply !== false;
        var canQueue = d.online !== false && d.canQueue !== false;
        var blockedReason = d.online === false ? 'Bot is offline. Bring it online before applying or queueing a live build.' :
            (d.applyBlockedReason || (d.canApply === false ? 'The core reports that this build cannot be changed right now.' : ''));
        if (blockedReason && !canApplyNow && canQueue && (!queued || queued.canReplace === true))
            blockedReason += ' You can keep editing and queue one build for the next safe opportunity.';
        var disabled = operationBusy ? ' disabled' : '';
        var profileOptions = '';
        for (var pi = 0; pi < profiles.length; pi++) {
            var profile = profiles[pi] || {};
            var specTab = safeInt(profile.specTab, -1);
            if (specTab < 0 || specTab > 2) continue;
            var label = profile.name || profile.spec || profile.id || ('Profile ' + specTab);
            profileOptions += '<option value="' + specTab + '"' + (specTab === selectedSpecTab ? ' selected' : '') + '>' + esc(label) + '</option>';
        }
        if (!profileOptions)
            profileOptions = '<option value="">No profiles available</option>';

        var roleOptions = buildRoleOptions(selectedProfile, currentRoleId);
        var queuedMode = queued ? normalizeRotationMode(queued.rotationMode) : '';
        var activeCustomId = queued && queuedMode === 'custom'
            ? String(queued.rotationProfile || '')
            : rotationView.kind === 'custom'
                ? String((d.rotation && (d.rotation.persistedProfile || d.rotation.profile)) || '') : '';
        var rotationOptions = '<option value="spec_default"' + (!activeCustomId ? ' selected' : '') + '>Specialization default (built in)</option>';
        var matchedActiveCustom = false;
        for (var ri = 0; ri < rotations.length; ri++) {
            var rotation = rotations[ri] || {};
            var rotationId = String(rotation.id || rotation.name || '');
            if (!rotationId) continue;
            var selected = activeCustomId && rotationId.toLowerCase() === activeCustomId.toLowerCase();
            if (selected) matchedActiveCustom = true;
            rotationOptions += '<option value="custom:' + escAttr(rotationId) + '"' + (selected ? ' selected' : '') + '>' +
                esc(rotation.name || rotationId) + ' (' + safeInt(rotation.instructionCount, 0) + ' steps)</option>';
        }
        if (activeCustomId && !matchedActiveCustom)
            rotationOptions += '<option value="custom:' + escAttr(activeCustomId) + '" selected disabled>' + esc(activeCustomId) + ' (profile unavailable)</option>';

        var state = applying ? 'applying' : queueBusy ? 'saving' : queued ? queueStateToken(queued) : canApplyNow ? 'ready' : 'blocked';
        var statusClass = queued && !applying && !queueBusy ? queueStatusClass(queued) : state;
        var statusText = applying ? 'Applying…' : queueBusy === 'cancelling' ? 'Cancelling…' : queueBusy ? 'Saving…' :
            queued ? humanPolicy(queued.status || 'queued') : canApplyNow ? 'Ready' : canQueue ? 'Queue available' : 'Unavailable';
        var showRestore = rotationView.kind === 'custom' || !!rotationView.persistedProfile;
        return '<section class="bt-build-workshop' + (applying ? ' applying' : '') + '" data-guid="' + safeInt(d.guid, 0) + '" data-state="' + escAttr(state) + '" aria-busy="' + (operationBusy ? 'true' : 'false') + '">' +
            '<header><div><i class="fa-solid fa-screwdriver-wrench"></i><span><b>Build Workshop</b><small>Choose one coherent talent, role, and rotation package.</small></span></div>' +
            '<em class="bt-build-status ' + statusClass + '" aria-live="polite">' + esc(statusText) + '</em></header>' +
            (blockedReason ? '<div class="bt-build-blocker"><i class="fa-solid fa-clock"></i><span>' + esc(blockedReason) + '</span></div>' : '') +
            renderQueuedBuildSummary(queued, queueBusy) +
            '<div class="bt-build-grid">' +
            '<label><span>Talent profile</span><select id="btBuildSpec" class="form-input bt-build-select"' + disabled + '>' + profileOptions + '</select><small id="btBuildSpecHelp"></small></label>' +
            '<label><span>Active role</span><select id="btBuildRole" class="form-input bt-build-select"' + disabled + '>' + roleOptions + '</select><small>Roles are constrained by the selected profile.</small></label>' +
            '<label><span>Combat rotation</span><select id="btBuildRotation" class="form-input bt-build-select"' + disabled + '>' + rotationOptions + '</select><small id="btBuildRotationHelp"></small></label>' +
            '</div>' +
            '<div class="bt-build-preview"><div><span>Current</span><b id="btBuildBefore"></b></div><i class="fa-solid fa-arrow-right-long"></i><div><span id="btBuildAfterLabel">Requested build</span><b id="btBuildAfter"></b></div></div>' +
            '<div class="bt-build-danger"><i class="fa-solid fa-triangle-exclamation"></i><div><b>This resets learned talents.</b><span id="btBuildDangerText">The core will remove the current talent build, buy the selected profile through this level, then activate the chosen rotation.</span></div></div>' +
            '<label class="bt-build-confirm"><input type="checkbox" id="btBuildConfirm"' + disabled + ' /><span>I understand that this will erase and rebuild this bot\'s learned talents.</span></label>' +
            '<div class="bt-build-notice" id="btBuildNotice" aria-live="polite" hidden></div>' +
            '<footer><button type="button" class="btn-accent bt-build-apply" id="btBuildApply" disabled><i class="fa-solid fa-rotate"></i> Reset talents &amp; apply build</button>' +
            (showRestore ? '<button type="button" class="bt-build-secondary" id="btRestoreSpecRotation"' + disabled + '><i class="fa-solid fa-rotate-left"></i> Restore spec rotation</button>' : '') +
            '</footer></section>';
    }

    function renderQueuedBuildSummary(queue, queueBusy) {
        if (!queue) return '';
        var profile = queue.profileName || queue.profileId || ('Spec ' + safeInt(queue.specTab, 0));
        var role = queue.activeRoleName || COMBAT_ROLE_NAMES[safeInt(queue.activeRole, 0)] || ('Role ' + safeInt(queue.activeRole, 0));
        var rotation = queue.rotationName || queue.rotationProfile ||
            (normalizeRotationMode(queue.rotationMode) === 'custom' ? 'Custom rotation' : 'Specialization default');
        var statusToken = String(queue.status || 'waiting').trim().toLowerCase();
        var status = humanPolicy(statusToken);
        var meta = status + ' · ' + (queue.resetTalents ? 'talent rebuild' : 'rotation only');
        if (queue.expectedRevision != null) meta += ' · revision ' + safeInt(queue.expectedRevision, 0);
        if (queue.queuedAtUtc) meta += ' · queued ' + formatQueueTime(queue.queuedAtUtc);
        if (queue.updatedAtUtc && queue.updatedAtUtc !== queue.queuedAtUtc) meta += ' · updated ' + formatQueueTime(queue.updatedAtUtc);
        if (safeInt(queue.attemptCount, 0) > 0) meta += ' · ' + safeInt(queue.attemptCount, 0) + ' attempt' + (safeInt(queue.attemptCount, 0) === 1 ? '' : 's');
        if (queue.lastCode) meta += ' · ' + queue.lastCode;
        var heading = statusToken === 'failed' ? 'Queued build failed' :
            statusToken === 'uncertain' ? 'Build outcome uncertain' :
                statusToken === 'dispatching' ? 'Applying queued build' : 'Pending build';
        var replaceNote = statusToken === 'failed'
            ? 'Review the failure, then replace or cancel this entry.'
            : statusToken === 'uncertain'
                ? 'Refresh live state; automatic retry and replacement are disabled.'
                : statusToken === 'dispatching'
                    ? 'The core dispatch is already claimed and cannot be changed.'
                    : 'This one-deep entry can be replaced before it is claimed.';
        var cancelDisabled = queueBusy || !queue.canCancel ? ' disabled' : '';
        var cancelLabel = statusToken === 'uncertain' ? 'Dismiss uncertain result' : 'Cancel queued build';
        var cancelTitle = statusToken === 'uncertain'
            ? 'Dismiss this record only after reviewing current live talents and rotation; this does not undo core state'
            : queue.canCancel ? 'Remove this unsent queued build' : 'Only an unsent queued build can be cancelled';
        return '<div class="bt-build-queue" id="btBuildQueue" data-queue-id="' + escAttr(queue.queueId || '') + '">' +
            '<i class="fa-solid fa-clock-rotate-left"></i>' +
            '<div class="bt-build-queue-copy"><b>' + esc(heading) + ' · ' + esc(profile) + '</b>' +
            '<span>' + esc(role) + ' · ' + esc(rotation) + '</span>' +
            '<small>' + esc(meta + ' · ' + replaceNote) + (queue.lastMessage ? ' · ' + esc(queue.lastMessage) : '') + '</small></div>' +
            '<div class="bt-build-queue-actions"><button type="button" class="bt-build-secondary bt-build-cancel-queued" id="btBuildCancelQueued" title="' + escAttr(cancelTitle) + '"' + cancelDisabled + '>' +
            '<i class="fa-solid fa-xmark"></i> ' + esc(cancelLabel) + '</button></div></div>';
    }

    function normalizeRotationMode(value) {
        var mode = String(value || '').trim().toLowerCase();
        return mode === 'custom' ? 'custom' : 'spec_default';
    }

    function queueStateToken(queue) {
        var status = String(queue && queue.status || 'waiting').trim().toLowerCase().replace(/[^a-z0-9_-]+/g, '-');
        return 'queue-' + (status || 'waiting');
    }

    function queueStatusClass(queue) {
        var status = String(queue && queue.status || '').trim().toLowerCase();
        if (status === 'dispatching') return 'applying';
        if (status === 'uncertain' || status === 'failed') return 'blocked';
        return 'queued';
    }

    function formatQueueTime(value) {
        var date = new Date(value);
        return Number.isNaN(date.getTime()) ? String(value) : date.toLocaleString();
    }

    function findBuildProfile(d, specOrId) {
        var profiles = (d && d.availableProfiles) || [];
        for (var i = 0; i < profiles.length; i++) {
            var p = profiles[i] || {};
            if (String(p.id) === String(specOrId) || safeInt(p.specTab, -1) === safeInt(specOrId, -2)) return p;
        }
        return null;
    }

    function buildRoleOptions(profile, selectedRoleId) {
        var roles = profile && profile.allowedRoles ? profile.allowedRoles : [];
        var html = '';
        var selectedFound = false;
        for (var i = 0; i < roles.length; i++) {
            var role = roles[i];
            var id = safeInt(typeof role === 'object' && role !== null ? role.id : role, 0);
            if (!id) continue;
            var selected = id === safeInt(selectedRoleId, 0);
            if (selected) selectedFound = true;
            var name = typeof role === 'object' && role !== null ? role.name : COMBAT_ROLE_NAMES[id];
            html += '<option value="' + id + '"' + (selected ? ' selected' : '') + '>' + esc(name || ('Role ' + id)) + '</option>';
        }
        if (!html && selectedRoleId)
            html = '<option value="' + safeInt(selectedRoleId, 0) + '" selected>' + safeInt(selectedRoleId, 0) + '</option>';
        else if (html && !selectedFound)
            html = html.replace('<option ', '<option selected ');
        return html || '<option value="">No allowed roles</option>';
    }

    function refreshBuildWorkshopState(d) {
        if (!d || !$('#btBuildSpec').length) return;
        var profile = findBuildProfile(d, $('#btBuildSpec').val());
        var roleId = safeInt($('#btBuildRole').val(), 0);
        var roleName = $('#btBuildRole option:selected').text() || 'No role';
        var rotationValue = String($('#btBuildRotation').val() || '');
        var rotation = findAvailableRotation(d, rotationValue.indexOf('custom:') === 0 ? rotationValue.substring(7) : '');
        var profileName = profile ? String(profile.name || profile.spec || profile.id) : 'No profile';
        var rotationName = rotationValue === 'spec_default' ? profileName + ' built-in' :
            rotation ? String(rotation.name || rotation.id) : 'Unavailable custom rotation';
        var currentRotation = combatRotationView(d);
        var before = (d.profile ? d.profile.name : 'Unassigned') + ' · ' +
            (d.activeRole ? d.activeRole.name : 'Unassigned') + ' · ' + currentRotation.name;
        var after = profileName + ' · ' + roleName + ' · ' + rotationName;
        $('#btBuildBefore').text(before);
        $('#btBuildAfter').text(after);

        var treePoints = profile && profile.treePoints ? profile.treePoints.join(' / ') : '';
        var profileHelp = profile ? humanPolicy(profile.rolePolicy) + (profile.gearPolicy ? ' · ' + humanPolicy(profile.gearPolicy) : '') +
            (treePoints ? ' · ' + treePoints + ' points' : '') : 'Select a valid profile.';
        $('#btBuildSpecHelp').text(profileHelp);
        $('#btBuildRotationHelp').text(rotationValue === 'spec_default'
            ? 'Uses the core\'s hardcoded policy for the selected specialization.'
            : rotation ? (rotation.description || (safeInt(rotation.instructionCount, 0) + ' priority instructions.')) : 'This custom profile is not available.');

        var applying = !!combatLoadoutApplying[d.guid];
        var queueBusy = combatLoadoutQueueBusy[d.guid] || '';
        var operationBusy = applying || !!queueBusy;
        var canApplyNow = d.online !== false && d.canApply !== false;
        var canQueue = d.online !== false && d.canQueue !== false;
        var queued = d.queuedChange;
        var queuesInstead = !canApplyNow || !!queued;
        var valid = !!profile && !!roleId && (rotationValue === 'spec_default' || !!rotation);
        var confirmed = $('#btBuildConfirm').is(':checked');
        var queueReplaceAllowed = !queued || queued.canReplace === true;
        var state = applying ? 'applying' : queueBusy ? 'saving' : queued ? queueStateToken(queued) : canApplyNow ? 'ready' : 'blocked';
        var statusClass = queued && !applying && !queueBusy ? queueStatusClass(queued) : state;
        var statusText = applying ? 'Applying…' : queueBusy === 'cancelling' ? 'Cancelling…' : queueBusy ? 'Saving…' :
            queued ? humanPolicy(queued.status || 'queued') : canApplyNow ? 'Ready' : canQueue ? 'Queue available' : 'Unavailable';
        var $workshop = $('.bt-build-workshop');
        $workshop.toggleClass('applying', applying).attr('data-state', state).attr('aria-busy', operationBusy ? 'true' : 'false');
        $('.bt-build-status').removeClass('ready blocked applying saving queued').addClass(statusClass).text(statusText);

        var primaryHtml = applying ? '<i class="fa-solid fa-spinner fa-spin"></i> Applying build…' :
            queueBusy ? '<i class="fa-solid fa-spinner fa-spin"></i> ' + (queueBusy === 'cancelling' ? 'Cancelling queue…' : 'Saving queue…') :
                queued ? '<i class="fa-solid fa-pen-to-square"></i> Replace queued build' :
                    !canApplyNow ? '<i class="fa-solid fa-clock"></i> Queue build' :
                        '<i class="fa-solid fa-rotate"></i> Reset talents &amp; apply build';
        $('#btBuildApply').html(primaryHtml).attr('data-mode', queuesInstead ? (queued ? 'replace-queue' : 'queue') : 'apply');
        $('#btBuildApply').prop('disabled', operationBusy || !valid || !confirmed ||
            (queuesInstead && (!canQueue || !queueReplaceAllowed)));
        $('#btBuildConfirm, #btBuildSpec, #btBuildRole, #btBuildRotation').prop('disabled', operationBusy);
        $('#btBuildCancelQueued').prop('disabled', operationBusy || !queued || queued.canCancel !== true);
        $('#btRestoreSpecRotation').prop('disabled', operationBusy ||
            (queuesInstead && (!canQueue || (!!queued && queued.canReplace !== true))));
        $('#btBuildAfterLabel').text(queued ? 'Queued replacement' : canApplyNow ? 'After reset' : 'Queued build');

        var dangerText = profile && safeInt(profile.specTab, -1) === safeInt(d.specTab, -2)
            ? 'The current talents will still be erased and rebuilt from the beginning of this profile.'
            : 'The current talents will be erased and rebuilt as ' + profileName + ' through level ' + safeInt(d.level, 0) + '.';
        if (queuesInstead)
            dangerText = 'When this queued request becomes safe to run, ' + dangerText.charAt(0).toLowerCase() + dangerText.slice(1);
        $('#btBuildDangerText').text(dangerText);
        renderBuildWorkshopNotice(combatLoadoutNotices[d.guid]);
    }

    function findAvailableRotation(d, id) {
        if (!id) return null;
        var rotations = (d && d.availableRotations) || [];
        for (var i = 0; i < rotations.length; i++) {
            var rotationId = rotations[i] && (rotations[i].id || rotations[i].name);
            if (rotationId && String(rotationId).toLowerCase() === String(id).toLowerCase()) return rotations[i];
        }
        return null;
    }

    function repopulateBuildRoles() {
        var cached = selectedGuid && combatLoadoutCache[selectedGuid];
        if (!cached) return;
        var d = cached.data;
        var profile = findBuildProfile(d, $('#btBuildSpec').val());
        var previous = safeInt($('#btBuildRole').val(), 0);
        $('#btBuildRole').html(buildRoleOptions(profile, previous));
        refreshBuildWorkshopState(d);
    }

    $(document).on('change', '#btBuildSpec', repopulateBuildRoles);
    $(document).on('change', '#btBuildRole, #btBuildRotation, #btBuildConfirm', function () {
        var cached = selectedGuid && combatLoadoutCache[selectedGuid];
        if (cached) refreshBuildWorkshopState(cached.data);
    });

    $(document).on('click', '#btBuildApply', function () {
        var guid = selectedGuid;
        var cached = guid && combatLoadoutCache[guid];
        if (!cached || combatLoadoutApplying[guid] || combatLoadoutQueueBusy[guid]) return;
        if (!$('#btBuildConfirm').is(':checked')) {
            showToast('Confirm the destructive talent reset first.', true);
            return;
        }
        var d = cached.data;
        var queued = d.queuedChange;
        var canApplyNow = d.online !== false && d.canApply !== false;
        var canQueue = d.online !== false && d.canQueue !== false;
        var queuesInstead = !canApplyNow || !!queued;
        if (queued && queued.canReplace !== true) {
            showToast('This queued build has already been claimed and cannot be replaced.', true);
            return;
        }
        if (queuesInstead && !canQueue) {
            showToast('The bot must be online before a build can be queued.', true);
            return;
        }
        var rotationValue = String($('#btBuildRotation').val() || '');
        var custom = rotationValue.indexOf('custom:') === 0;
        var payload = {
            expectedQueueId: queued && queued.queueId ? queued.queueId : null,
            expectedRevision: safeInt(d.rotation && d.rotation.revision, 0),
            specTab: safeInt($('#btBuildSpec').val(), -1),
            activeRole: safeInt($('#btBuildRole').val(), 0),
            rotationMode: custom ? 'custom' : 'spec_default',
            rotationProfile: custom ? rotationValue.substring(7) : null,
            resetTalents: true,
            confirmReset: true
        };
        if (queuesInstead)
            postCombatLoadoutQueue(guid, payload, queued ? 'Queued build replaced' : 'Build queued');
        else
            postCombatLoadout(guid, payload, 'Build applied');
    });

    $(document).on('click', '#btRestoreSpecRotation', function () {
        var guid = selectedGuid;
        var cached = guid && combatLoadoutCache[guid];
        if (!cached || combatLoadoutApplying[guid] || combatLoadoutQueueBusy[guid]) return;
        var d = cached.data;
        var queued = d.queuedChange;
        var queuesInstead = d.online === false || d.canApply === false || !!queued;
        if (queued && queued.canReplace !== true) {
            showToast('This queued build has already been claimed and cannot be replaced.', true);
            return;
        }
        if (queuesInstead && (d.online === false || d.canQueue === false)) {
            showToast('The bot must be online before a rotation change can be queued.', true);
            return;
        }
        var payload = {
            expectedQueueId: queued && queued.queueId ? queued.queueId : null,
            expectedRevision: safeInt(d.rotation && d.rotation.revision, 0),
            specTab: safeInt(d.specTab, -1),
            activeRole: d.activeRole ? safeInt(d.activeRole.id, 0) : 0,
            rotationMode: 'spec_default',
            rotationProfile: null,
            resetTalents: false,
            confirmReset: false
        };
        if (queuesInstead)
            postCombatLoadoutQueue(guid, payload, queued ? 'Queued build replaced with the spec rotation' : 'Spec rotation change queued');
        else
            postCombatLoadout(guid, payload, 'Spec rotation restored');
    });

    $(document).on('click', '#btBuildCancelQueued', function () {
        var guid = selectedGuid;
        var cached = guid && combatLoadoutCache[guid];
        var queue = cached && cached.data && cached.data.queuedChange;
        if (!queue || queue.canCancel !== true || combatLoadoutApplying[guid] || combatLoadoutQueueBusy[guid]) return;
        if (String(queue.status || '').toLowerCase() === 'uncertain' &&
            !window.confirm('Dismiss this uncertain record? This does not undo a build that may have reached the core. Review the bot\'s live talents and rotation before making another change.')) return;
        deleteCombatLoadoutQueue(guid);
    });

    function postCombatLoadout(guid, payload, successText) {
        var shouldRefresh = false;
        combatLoadoutApplying[guid] = true;
        delete combatLoadoutNotices[guid];
        var cached = combatLoadoutCache[guid];
        if (cached) refreshBuildWorkshopState(cached.data);
        $.ajax({
            url: '/Bots/CombatLoadout/' + guid,
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload)
        }).done(function (response) {
            if (response && response.success === false) {
                combatLoadoutNotices[guid] = { kind: 'error', title: 'Build rejected', message: response.error || response.message || 'The build was not applied.' };
                showToast(combatLoadoutNotices[guid].message, true);
                return;
            }
            shouldRefresh = true;
            delete combatLoadoutNotices[guid];
            showToast((response && (response.message || response.status)) || successText);
        }).fail(function (xhr) {
            var problem = parseCombatLoadoutProblem(xhr);
            combatLoadoutNotices[guid] = { kind: problem.kind || 'error', title: problem.title, message: problem.message, code: problem.code };
            showToast(problem.message, true);
            shouldRefresh = problem.refresh;
        }).always(function () {
            delete combatLoadoutApplying[guid];
            if (shouldRefresh) invalidateSpellbook(guid);
            if (shouldRefresh) invalidateCombatLoadout(guid, selectedGuid === guid && detailTab === 'talents');
            else if (selectedGuid === guid && detailTab === 'talents' && combatLoadoutCache[guid])
                refreshBuildWorkshopState(combatLoadoutCache[guid].data);
        });
    }

    function postCombatLoadoutQueue(guid, payload, successText) {
        var shouldRefresh = false;
        combatLoadoutQueueBusy[guid] = 'saving';
        delete combatLoadoutNotices[guid];
        var cached = combatLoadoutCache[guid];
        if (cached) refreshBuildWorkshopState(cached.data);
        $.ajax({
            url: '/Bots/CombatLoadout/' + guid + '/Queue',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload)
        }).done(function (response) {
            if (response && response.success === false) {
                combatLoadoutNotices[guid] = { kind: 'error', title: 'Queue rejected', message: response.error || response.message || 'The build was not queued.' };
                showToast(combatLoadoutNotices[guid].message, true);
                return;
            }
            shouldRefresh = true;
            if (cached && response && response.queue) cached.data.queuedChange = response.queue;
            combatLoadoutNotices[guid] = {
                kind: 'success',
                title: 'Pending build saved',
                message: (response && (response.message || response.status)) || successText
            };
            showToast(combatLoadoutNotices[guid].message);
        }).fail(function (xhr) {
            var problem = parseCombatLoadoutProblem(xhr);
            combatLoadoutNotices[guid] = { kind: 'error', title: problem.title, message: problem.message, code: problem.code };
            showToast(problem.message, true);
            shouldRefresh = problem.refresh;
        }).always(function () {
            delete combatLoadoutQueueBusy[guid];
            if (shouldRefresh) invalidateCombatLoadout(guid, selectedGuid === guid && detailTab === 'talents');
            else if (selectedGuid === guid && detailTab === 'talents' && combatLoadoutCache[guid])
                refreshBuildWorkshopState(combatLoadoutCache[guid].data);
        });
    }

    function deleteCombatLoadoutQueue(guid) {
        var shouldRefresh = false;
        combatLoadoutQueueBusy[guid] = 'cancelling';
        delete combatLoadoutNotices[guid];
        var cached = combatLoadoutCache[guid];
        if (cached) refreshBuildWorkshopState(cached.data);
        $.ajax({
            url: '/Bots/CombatLoadout/' + guid + '/Queue?expectedQueueId=' + encodeURIComponent(
                cached && cached.data && cached.data.queuedChange ? cached.data.queuedChange.queueId || '' : '') +
                '&expectedStatus=' + encodeURIComponent(
                    cached && cached.data && cached.data.queuedChange ? cached.data.queuedChange.status || '' : ''),
            method: 'DELETE'
        }).done(function (response) {
            if (response && response.success === false) {
                combatLoadoutNotices[guid] = { kind: 'error', title: 'Cancellation rejected', message: response.error || response.message || 'The queued build was not cancelled.' };
                showToast(combatLoadoutNotices[guid].message, true);
                return;
            }
            shouldRefresh = true;
            if (cached) cached.data.queuedChange = null;
            combatLoadoutNotices[guid] = {
                kind: 'success',
                title: response && response.status === 'dismissed' ? 'Uncertain result dismissed' : 'Queued build cancelled',
                message: (response && (response.message || response.status)) || 'Queued build cancelled'
            };
            showToast(combatLoadoutNotices[guid].message);
        }).fail(function (xhr) {
            var problem = parseCombatLoadoutProblem(xhr);
            combatLoadoutNotices[guid] = { kind: 'error', title: problem.title, message: problem.message, code: problem.code };
            showToast(problem.message, true);
            shouldRefresh = problem.refresh;
        }).always(function () {
            delete combatLoadoutQueueBusy[guid];
            if (shouldRefresh) invalidateCombatLoadout(guid, selectedGuid === guid && detailTab === 'talents');
            else if (selectedGuid === guid && detailTab === 'talents' && combatLoadoutCache[guid])
                refreshBuildWorkshopState(combatLoadoutCache[guid].data);
        });
    }

    function parseCombatLoadoutProblem(xhr) {
        var status = xhr ? xhr.status : 0;
        var body = xhr && xhr.responseJSON ? xhr.responseJSON : {};
        var code = body.errorCode || body.code || '';
        var detail = typeof body.error === 'string' ? body.error :
            body.error && typeof body.error.message === 'string' ? body.error.message :
            typeof body.message === 'string' ? body.message :
            typeof body.detail === 'string' ? body.detail : '';
        if (!detail && body.errors && typeof body.errors === 'object') {
            var pieces = [];
            Object.keys(body.errors).forEach(function (key) {
                var values = body.errors[key];
                if (!Array.isArray(values)) values = [values];
                for (var i = 0; i < values.length; i++) if (typeof values[i] === 'string') pieces.push(values[i]);
            });
            detail = pieces.join(' ');
        }
        var base = status === 400 ? { title: 'Invalid build request', message: 'Check the selected profile, role, and rotation.', refresh: false } :
            status === 409 ? { title: 'Build state changed', message: 'The bot changed or is busy. Its current build is being refreshed.', refresh: true } :
            status === 422 ? { title: 'Build cannot be applied', message: 'The core rejected this profile or rotation combination.', refresh: false } :
            status === 504 ? { kind: 'uncertain', title: 'Core acknowledgement timed out', message: 'The result is uncertain. Current state is being refreshed before another attempt.', refresh: true } :
            { title: 'Build change failed', message: 'The server could not apply this build (' + status + ').', refresh: false };
        if (detail) base.message += ' ' + detail;
        base.code = code;
        return base;
    }

    function renderBuildWorkshopNotice(notice) {
        var $notice = $('#btBuildNotice');
        if (!$notice.length) return;
        if (!notice) {
            $notice.attr('hidden', true).removeAttr('data-kind').empty();
            return;
        }
        var kind = notice.kind || 'success';
        var icon = kind === 'error' ? 'fa-circle-xmark' :
            kind === 'warning' || kind === 'uncertain' ? 'fa-triangle-exclamation' : 'fa-circle-check';
        $notice.removeAttr('hidden').attr('data-kind', kind).empty();
        $('<i>').addClass('fa-solid ' + icon).appendTo($notice);
        var $copy = $('<div>').appendTo($notice);
        $('<b>').text(notice.title || 'Build update').appendTo($copy);
        $('<span>').text(notice.message || '').appendTo($copy);
        if (notice.code) $('<small>').text(notice.code).appendTo($copy);
    }

    function talentSummaryCard(label, value, note, icon) {
        return '<div class="bt-talent-summary-card"><i class="fa-solid ' + icon + '"></i><div><span>' + esc(label) + '</span><b>' + esc(value) + '</b><small>' + esc(note) + '</small></div></div>';
    }

    function humanPolicy(value) {
        if (!value) return '';
        return String(value).split('_').map(function (word) { return capitalize(word); }).join(' ');
    }

    function safeInt(value, fallback) {
        var n = parseInt(value);
        return Number.isFinite(n) ? n : fallback;
    }

    // ===================== LIVE TAB (real-time BotContext feed) =====================

    var GOAL_COLOR = {
        Idle: '#5f6b7a', Questing: '#7aa2f7', Grinding: '#f7768e', Vendoring: '#e0af68',
        Training: '#bb9af7', Maintenance: '#ff9e64', Following: '#73daca',
        Exploring: '#2ac3de', Socializing: '#9ece6a'
    };

    function startLivePoll() {
        // New bot? drop stale payload so we show "connecting" not the last bot's data.
        if (liveGuid !== selectedGuid) {
            liveData = null; liveGuid = selectedGuid;
            liveLogSeq = 0; liveScaffoldGuid = null;
            liveQuestMap = {}; liveQuestGuid = null;
            liveLastFleetAt = 0;
        }
        stopLivePoll();
        fetchLive();                                  // immediate
        fetchLiveLog();                               // immediate log pull
        fetchLiveQuestStatus();                       // immediate server quest credit pull
        livePollTimer = setInterval(fetchLive, 5000); // server refresh
        liveLogTimer = setInterval(fetchLiveLog, 2000); // log feed
        liveQuestTimer = setInterval(fetchLiveQuestStatus, 4000); // server kill-credit truth
        liveTickTimer = setInterval(function () {     // client-side age ticking between polls
            if (detailTab === 'live' && selectedGuid && liveData) renderLiveTab(botStates[selectedGuid]);
        }, 1000);
    }

    // (Re)start only when needed — avoids an extra fetch on every incidental re-render.
    function ensureLivePoll() {
        // Poll the realtime spine for ANY selected bot — the Overview "Current quest"
        // section reads the same liveData/liveQuestMap the Live tab does. The heavy
        // live-LOG poll still self-gates to the Live tab (see fetchLiveLog).
        if (!selectedGuid) { stopLivePoll(); return; }
        if (livePollTimer && liveGuid === selectedGuid) return;
        startLivePoll();
    }

    function stopLivePoll() {
        if (livePollTimer) { clearInterval(livePollTimer); livePollTimer = null; }
        if (liveTickTimer) { clearInterval(liveTickTimer); liveTickTimer = null; }
        if (liveLogTimer) { clearInterval(liveLogTimer); liveLogTimer = null; }
        if (liveQuestTimer) { clearInterval(liveQuestTimer); liveQuestTimer = null; }
    }

    function fetchLive() {
        if (!selectedGuid || !connected) return;
        var g = selectedGuid;
        $.getJSON('/Bots/LiveState/' + g, function (data) {
            if (selectedGuid !== g) return;           // selection moved on while in flight
            if (!data || data.error) {
                liveData = null;
                if (detailTab === 'live') renderLiveTab(botStates[g]);
                else updateDetailQuest();
                return;
            }
            liveData = data;
            liveGuid = g;
            liveFetchedAt = Date.now();
            if (detailTab === 'live') renderLiveTab(botStates[g]);
            else updateDetailQuest();
        });
    }

    function fetchLiveLog() {
        if (!selectedGuid || !connected || detailTab !== 'live') return;
        var g = selectedGuid;
        var st = botStates[g];
        if (!st || !st.name) return;
        $.getJSON('/Bots/LiveLog', { name: st.name, after: liveLogSeq }, function (data) {
            if (selectedGuid !== g || detailTab !== 'live') return;
            if (!data) return;
            if (typeof data.lastSeq === 'number') liveLogSeq = data.lastSeq;
            var $box = $('#liveLogBox');
            if ($box.length === 0) return;
            if (data.lines && data.lines.length) {
                var el = $box[0];
                var nearBottom = (el.scrollHeight - el.scrollTop - el.clientHeight) < 48;
                var add = '';
                for (var i = 0; i < data.lines.length; i++) {
                    var ln = data.lines[i];
                    // The FleetReport heartbeat names every bot, so the server's name filter
                    // hands it to this single-bot feed every tick. Keep it (it's a useful
                    // fleet pulse) but throttle to ~once/90s so it can't bury this bot's lines.
                    if (isFleetLine(ln.msg)) {
                        var now = Date.now();
                        if (now - liveLastFleetAt < FLEET_MIN_MS) continue;
                        liveLastFleetAt = now;
                        add += logLineHtml(ln, true);
                    } else {
                        add += logLineHtml(ln);
                    }
                }
                if (add) $box.append(add);
                var kids = $box.children();
                if (kids.length > 400) kids.slice(0, kids.length - 400).remove();
                if (nearBottom) el.scrollTop = el.scrollHeight;
                $('#liveLogMeta').text('(' + $box.children().length + ')');
            }
        });
    }

    // Pull the authoritative kill credit (character_queststatus.mob_count*) for the
    // watched bot. The live BotContext objective `have` has NO per-kill feed — C++ only
    // emits QUEST_UPDATE on accept/reward/abandon (AIBOTAI_REFERENCE §SendQuestUpdateEvent)
    // — so the spine's projected count sits at its seed (0) until turn-in. The DB count is
    // ground truth, and surfacing it makes "0/10 but it's killing" self-diagnosing: if the
    // server count is also 0 while kills land, that's the tag-credit bug, not a display gap.
    function fetchLiveQuestStatus() {
        if (!selectedGuid || !connected) return;
        var g = selectedGuid;
        $.getJSON('/Bots/QuestStatus', { guid: g }, function (data) {
            if (selectedGuid !== g) return;
            if (!data || data.error || !data.quests) return;
            var map = {};
            for (var i = 0; i < data.quests.length; i++) map[data.quests[i].questId] = data.quests[i];
            liveQuestMap = map;
            liveQuestGuid = g;
            if (detailTab === 'live') { if (liveData) renderLiveTab(botStates[g]); }
            else updateDetailQuest();
        });
    }

    // index of the n-th (0-based) non-zero entry in a required-count array, or -1
    function nthNonzero(arr, n) {
        if (!arr) return -1;
        var seen = 0;
        for (var i = 0; i < arr.length; i++) {
            if (arr[i] > 0) { if (seen === n) return i; seen++; }
        }
        return -1;
    }

    // Map one live objective to its authoritative server slot. killIdx/itemIdx are the
    // running per-kind ordinals so the k-th kill objective pairs with the k-th non-zero
    // mob slot — exact for single-objective quests, best-effort (positional) otherwise.
    function serverObjFor(questId, o, killIdx, itemIdx) {
        var row = liveQuestMap[questId];
        if (!row) return null;
        if (o.kind === 'kill') {
            var slot = nthNonzero(row.mobRequired, killIdx);
            if (slot < 0) return null;
            return { have: row.mobCounts[slot], need: row.mobRequired[slot], status: row.status };
        }
        if (o.kind === 'gather') {
            var s2 = nthNonzero(row.itemRequired, itemIdx);
            if (s2 < 0) return null;
            return { have: row.itemCounts[s2], need: row.itemRequired[s2], status: row.status };
        }
        return null;
    }

    function reEscape(s) { return String(s).replace(/[.*+?^${}()|[\]\\]/g, '\\$&'); }
    function wholeWord(hay, word) {
        if (!word) return false;
        return new RegExp('\\b' + reEscape(word) + '\\b').test(hay);
    }
    // A fleet heartbeat names many bots in one line. Detect it from the live roster:
    // ≥3 bot names is unambiguous; ≥2 names plus a census token catches small fleets
    // without flagging an ordinary grouping/assist line that merely mentions one ally.
    function isFleetLine(m) {
        if (!m) return false;
        var seen = 0;
        for (var g in botStates) {
            var nm = botStates[g] && botStates[g].name;
            if (nm && wholeWord(m, nm)) { seen++; if (seen >= 3) return true; }
        }
        return seen >= 2 && /pick=|av=|why=/.test(m);
    }

    function logLineHtml(l, isFleet) {
        var t = '';
        try { t = new Date(l.t).toLocaleTimeString(); } catch (e) { t = ''; }
        var m = l.msg || '';
        var c = 'var(--text-secondary)';
        if (/STALL|no_path|MOVE_FAILED|PATH_UNSAFE|negated|deferring|shelving|_FAIL|cross_map/i.test(m)) c = '#f7768e';
        else if (/LEVEL_UP|completed|GRIND finished|rewarded|grey-drop/i.test(m)) c = '#9ece6a';
        else if (/RESURRECT|RESPAWN|DEATH|relocate|heal|graveyard|REPAIR|SELL/i.test(m)) c = '#ff9e64';
        else if (/\[QUEST\]|batch|seeding|QUEST_/i.test(m)) c = '#7aa2f7';
        else if (/KILL/i.test(m)) c = 'var(--text-muted)';
        var tag = isFleet ? '<span class="bt-live-fleet-tag" title="fleet heartbeat (throttled to ~90s)">fleet</span> ' : '';
        return '<div class="bt-live-logline">' + tag + '<span class="bt-live-logt">' + t + '</span> ' +
            '<span style="color:' + c + ';">' + esc(m) + '</span></div>';
    }

    // seconds offset since last fetch — lets ages tick up / countdowns tick down live
    function liveOff() { return Math.max(0, Math.floor((Date.now() - liveFetchedAt) / 1000)); }
    function ageUp(n) { return (n == null) ? null : (n + liveOff()); }
    function ageDn(n) { return (n == null) ? null : (n - liveOff()); }
    function secs(n) { return (n == null) ? '—' : (n + 's'); }
    function clk(sec, goodUnder, warnUnder) {
        // color a "time since" by health: low good, high bad
        var c = sec == null ? 'var(--text-muted)' : (sec < goodUnder ? '#9ece6a' : (sec < warnUnder ? '#e0af68' : '#f7768e'));
        return '<span style="color:' + c + ';">' + secs(sec) + '</span>';
    }
    function chip(text, color, bg) {
        return '<span style="display:inline-block;padding:1px 7px;border-radius:10px;font-size:10px;font-weight:700;' +
            'color:' + color + ';background:' + (bg || 'rgba(122,162,247,0.12)') + ';margin-right:4px;">' + esc(text) + '</span>';
    }
    function statBox(label, val, color) {
        return '<div class="bt-live-stat"><div class="bt-live-stat-val" style="color:' + (color || 'var(--text-primary)') + ';">' +
            val + '</div><div class="bt-live-stat-lbl">' + esc(label) + '</div></div>';
    }

    // 2D distance from the bot to a world point, or null if a different map (cross-map dist is meaningless).
    function objDist(d, o) {
        if (!d || !o || o.map !== d.mapId) return null;
        var dx = d.pos.x - o.x, dy = d.pos.y - o.y;
        return Math.round(Math.sqrt(dx * dx + dy * dy));
    }
    function distTag(d, o) {
        var dist = objDist(d, o);
        if (dist != null) return '<span class="bt-live-obj-dist">' + dist + 'y</span>';
        if (o && o.map !== d.mapId) return '<span class="bt-live-obj-dist">map ' + o.map + '</span>';
        return '';
    }
    function questNpcRow(label, icon, npc, d, color) {
        return '<div class="bt-live-obj"><i class="fa-solid ' + icon + '" style="color:' + color + ';width:14px;"></i> ' +
            '<span style="color:var(--text-muted);">' + label + ':</span> ' +
            '<span class="bt-live-obj-name">' + esc(npc.name || '?') + '</span>' + distTag(d, npc) + '</div>';
    }

    // ===================== "RIGHT NOW" NARRATIVE =====================
    // The payload has goal/step/pending/target/scratch/timers but makes you fuse them in
    // your head. These helpers synthesize a plain-English story: what the bot is doing,
    // where it's going (the MOVE_TO target, named by matching the driven-to coords to the
    // scratch's known waypoints), why, and what to watch for next.

    // Objectives augmented with the server-authoritative kill credit (same override the
    // objective panel uses), so the story's progress matches what's shown below it.
    function objsWithServerHave(aq) {
        var out = [], killIdx = 0, itemIdx = 0;
        if (!aq || !aq.objectives) return out;
        for (var i = 0; i < aq.objectives.length; i++) {
            var o = aq.objectives[i];
            var srv = serverObjFor(aq.id, o, killIdx, itemIdx);
            if (o.kind === 'kill') killIdx++; else if (o.kind === 'gather') itemIdx++;
            out.push({
                name: o.name, kind: o.kind, from: o.from, x: o.x, y: o.y, map: o.map, active: o.active,
                have: (srv != null) ? srv.have : o.have,
                need: (srv != null && srv.need > 0) ? srv.need : o.need
            });
        }
        return out;
    }

    // Named, located waypoints the spine knows from the active scratch.
    function liveWaypoints(d) {
        var pts = [], sc = d.scratch;
        if (!sc) return pts;
        if (sc.kind === 'quest' && sc.active) {
            var aq = sc.active;
            if (aq.objectives) for (var i = 0; i < aq.objectives.length; i++) {
                var o = aq.objectives[i];
                // For a gather-from-creature objective the bot drives to the SOURCE creature,
                // so name that (with the item as context) rather than the item itself.
                var lbl = (o.kind === 'gather' && o.from) ? (o.from + ' (for ' + o.name + ')') : o.name;
                pts.push({ label: lbl, kind: 'objective', active: !!o.active, x: o.x, y: o.y, map: o.map });
            }
            if (aq.giver) pts.push({ label: aq.giver.name, kind: 'giver', x: aq.giver.x, y: aq.giver.y, map: aq.giver.map });
            if (aq.turnIn) pts.push({ label: aq.turnIn.name, kind: 'turnin', x: aq.turnIn.x, y: aq.turnIn.y, map: aq.turnIn.map });
        } else if (sc.kind === 'vendor' && sc.target) {
            pts.push({ label: sc.canRepair ? 'repair vendor' : 'vendor', kind: 'vendor', x: sc.target.x, y: sc.target.y, map: sc.target.map });
        } else if (sc.kind === 'grind' && sc.center) {
            pts.push({ label: 'grind area', kind: 'grind', x: sc.center.x, y: sc.center.y, map: sc.center.map });
        }
        return pts;
    }

    // Name the current MOVE_TO destination by matching driven-to coords to a waypoint;
    // nearest wins (target IS the dest coord, so the match is near-exact). Falls back to
    // objective-grind/step hints, else null (caller shows coords).
    function liveDest(d) {
        var t = d.target, pts = liveWaypoints(d);
        if (t) {
            var best = null, bestD = Infinity;
            for (var i = 0; i < pts.length; i++) {
                var p = pts[i];
                if (p.map !== t.map) continue;
                var dx = p.x - t.x, dy = p.y - t.y, dist = Math.sqrt(dx * dx + dy * dy);
                if (dist < bestD) { bestD = dist; best = p; }
            }
            if (best && bestD <= 25) return { label: best.label, kind: best.kind };
        }
        if (d.pending && d.pending.isObjectiveGrind) {
            for (var j = 0; j < pts.length; j++) if (pts[j].kind === 'objective' && pts[j].active) return { label: pts[j].label, kind: 'objective' };
            for (var k = 0; k < pts.length; k++) if (pts[k].kind === 'objective') return { label: pts[k].label, kind: 'objective' };
        }
        var st = (d.step || '').toLowerCase();
        if (/accept|giver/.test(st)) for (var a = 0; a < pts.length; a++) if (pts[a].kind === 'giver') return pts[a];
        if (/turnin/.test(st)) for (var b = 0; b < pts.length; b++) if (pts[b].kind === 'turnin') return pts[b];
        return null;
    }

    function humanWhy(w) {
        if (!w) return '';
        if (/^in-quest/.test(w)) return 'already mid-quest';
        if (/graph-loading/.test(w)) return 'quest graph still loading';
        if (/no-identity/.test(w)) return 'no identity assigned yet';
        var m = w.match(/q av=(\d+) pick=(\d+)/);
        if (m) return 'picked from ' + m[1] + ' available quest' + (m[1] === '1' ? '' : 's');
        return w;
    }

    function composeStory(d) {
        var sc = d.scratch || {};
        var dest = liveDest(d);

        // ---- DOING ----
        var doing;
        if (d.dead || sc.kind === 'maintenance') {
            var ph = sc.phase || '';
            doing = ph === 'resurrecting' ? 'Resurrecting'
                : ph === 'relocate' ? 'Walking back from the graveyard'
                    : ph === 'heal' ? 'Healing up after a death'
                        : ph === 'rez-wait' ? 'Dead — waiting to resurrect'
                            : 'Recovering from a death';
        } else if (sc.kind === 'vendor') {
            doing = sc.canRepair ? 'On a repair/vendor run' : 'On a vendor run';
        } else if (d.goal === 'Questing') {
            var st = (d.step || '').toLowerCase();
            doing = /objective/.test(st) ? 'Working a quest objective'
                : /accept|giver/.test(st) ? 'Going to pick up a quest'
                    : /turnin/.test(st) ? 'Going to turn in a quest'
                        : 'Questing';
        } else if (d.goal === 'Grinding') doing = 'Grinding mobs';
        else if (d.goal === 'Training') doing = 'Training new skills';
        else if (d.goal === 'Following') doing = 'Following the group';
        else if (d.goal === 'Idle') doing = (d.why && d.why !== 'idle') ? 'Idle — about to pick its next move' : 'Idle';
        else doing = d.goal;
        if (d.inCombat) doing += ' (fighting right now)';

        // ---- GOING TO ----
        var going;
        if (d.pending) {
            var p = d.pending;
            if (p.cmd === 'MOVE_TO') {
                var where = dest ? dest.label : 'a waypoint';
                var dist = (d.distToTarget != null) ? (', ' + d.distToTarget + 'y out') : '';
                going = 'heading to <b>' + esc(where) + '</b>' + dist + (p.isObjectiveGrind ? ' to grind it down' : '');
            } else if (p.cmd === 'QUEST_INTERACT') {
                going = 'talking to <b>' + esc(dest ? dest.label : 'the questgiver') + '</b>';
            } else if (p.cmd === 'SET_TASK') going = 'grinding in place';
            else if (p.cmd === 'TAKE_FLIGHT') going = 'taking a flight path';
            else going = esc(p.cmd) + ', waiting on ' + esc(p.expect);
        } else {
            going = 'between commands — deciding what to do next';
        }

        // ---- WHY ----
        var why = null;
        if (sc.kind === 'quest' && sc.active) {
            var aq = sc.active;
            var objs = objsWithServerHave(aq);
            var ao = null;
            for (var i = 0; i < objs.length; i++) if (objs[i].active) { ao = objs[i]; break; }
            if (!ao && objs.length) ao = objs[0];
            var prog = '';
            if (ao && ao.have != null && ao.need) prog = ' — ' + ao.have + '/' + ao.need + ' ' + esc(ao.name);
            else if (ao) prog = ' — needs ' + esc(ao.name);
            why = 'For <b>' + esc(aq.title || ('#' + aq.id)) + '</b> (#' + aq.id + ')' + prog;
            if (sc.count > 1) {
                var done = 0, deferred = 0, failed = 0;
                for (var b = 0; b < sc.batch.length; b++) {
                    if (sc.batch[b].turnedIn) done++; else if (sc.batch[b].deferred) deferred++; else if (sc.batch[b].failed) failed++;
                }
                var ex = [];
                if (done) ex.push(done + ' done');
                if (deferred) ex.push(deferred + ' deferred');
                if (failed) ex.push(failed + ' failed');
                why += '. Quest ' + (sc.activeSlot + 1) + ' of ' + sc.count + ' in the batch' + (ex.length ? ' (' + ex.join(', ') + ')' : '');
            } else {
                why += '. Only quest in the batch right now';
            }
        } else if (sc.kind === 'grind') {
            why = 'Grinding ' + (sc.creatureEntry ? 'creature #' + sc.creatureEntry : 'nearby mobs') +
                (sc.killGoal > 0 ? ' — ' + sc.killCount + '/' + sc.killGoal + ' killed' : ' to level up / earn gold');
        } else if (sc.kind === 'vendor') {
            why = sc.canRepair ? 'Gear durability or bags need attention' : 'Bags need clearing';
        } else if (sc.kind === 'maintenance') {
            why = 'It died' + (sc.deathLoop ? ' repeatedly (death loop)' : '') + ' and must recover before doing anything else';
        } else if (d.why) {
            why = 'Reason: ' + esc(humanWhy(d.why));
        }

        // ---- WATCH NEXT ----
        var next = [];
        if (sc.kind === 'maintenance') {
            if (sc.rezInSec != null && sc.rezInSec > 0) next.push('resurrects in ~' + ageDn(sc.rezInSec) + 's');
            if (sc.deathLoop) next.push('death loop — will escalate to a graveyard port');
            else if (sc.phase === 'relocate') next.push('then walks back to where it died');
        }
        if (sc.kind === 'quest' && sc.active) {
            var oo = objsWithServerHave(sc.active), allDone = true, anyKill = false;
            for (var i2 = 0; i2 < oo.length; i2++) {
                var o2 = oo[i2];
                if (o2.have != null && o2.need) {
                    anyKill = true;
                    if (o2.have < o2.need) { allDone = false; var rem = o2.need - o2.have; if (rem <= 3) next.push(rem + ' more ' + esc(o2.name) + ' to go'); }
                }
            }
            if (anyKill && allDone) next.push('objective done — heading to turn in' + (sc.active.turnIn ? ' to ' + esc(sc.active.turnIn.name) : ''));
        }
        var np = ageUp(d.noProgressSec);
        if (np != null && np >= 30 && !d.dead) next.push('no progress for ' + np + 's — reselects if it passes ~120s');
        if (d.failure && ageUp(d.failure.ageSec) < 60) next.push('last move failed (' + esc(d.failure.reason) + ') — recovering');
        if (d.freeSlots <= 2) next.push('bags almost full — vendor run likely soon');
        if (d.durability < 30) next.push('durability low — repair trip likely soon');
        var dl2 = d.pending ? ageDn(d.pending.secsToDeadline) : null;
        if (dl2 != null && dl2 <= 30 && dl2 > -3600) next.push('command deadline in ' + dl2 + 's');

        return { doing: doing, going: going, why: why, next: next };
    }

    function renderStoryCard(d) {
        var s = composeStory(d);
        var html = '<div class="bt-live-card bt-story">';
        html += '<div class="bt-live-card-h"><i class="fa-solid fa-book-open"></i> Right now</div>';
        html += '<div class="bt-story-lead">' + s.doing + ' · ' + s.going + '.</div>';
        if (s.why) html += '<div class="bt-story-why">' + s.why + '.</div>';
        if (s.next && s.next.length)
            html += '<div class="bt-story-next"><span class="bt-story-next-h">Next</span> ' + s.next.slice(0, 3).join(' &nbsp;·&nbsp; ') + '</div>';
        html += '</div>';
        return html;
    }

    function renderLiveTab(s) {
        var $body = $('#detailTabBody');
        if ($body.length === 0) return;

        var d = liveData;
        if (!d || liveGuid !== selectedGuid) {
            liveScaffoldGuid = null;
            $body.html('<div style="padding:24px;text-align:center;color:var(--text-muted);">' +
                '<i class="fa-solid fa-satellite-dish fa-fade" style="font-size:20px;margin-bottom:8px;display:block;"></i>' +
                'Connecting to live feed…<div style="font-size:11px;margin-top:4px;">(brain engine must be ON)</div></div>');
            return;
        }

        var goalColor = GOAL_COLOR[d.goal] || 'var(--accent)';
        var html = '';

        // --- Goal / Step banner ---
        html += '<div class="bt-live-banner" style="border-left:3px solid ' + goalColor + ';">';
        html += '<div class="bt-live-goal" style="color:' + goalColor + ';">' + esc(d.goal) +
            '<span class="bt-live-step"> / ' + esc(d.step) + '</span></div>';
        if (d.why) html += '<div class="bt-live-why">why = ' + esc(d.why) + '</div>';
        html += '<div class="bt-live-badges">';
        if (d.dead) html += chip('DEAD', '#fff', 'rgba(247,118,142,0.8)');
        if (d.inCombat) html += chip('IN COMBAT', '#f7768e', 'rgba(247,118,142,0.15)');
        if (d.combat) {
            if (d.combat.anchorGuid === d.guid)
                html += chip('⚔ ANCHOR', '#7dcfff', 'rgba(125,207,255,0.15)');
            else {
                var anc = botStates[d.combat.anchorGuid];
                html += chip('⚔ assist → ' + esc(anc ? anc.name : ('#' + d.combat.anchorGuid)), '#7dcfff', 'rgba(125,207,255,0.15)');
            }
        }
        if (d.goal === 'Idle' && d.why && d.why !== 'idle')
            html += chip('↻ reselecting', '#ff9e64', 'rgba(255,158,100,0.15)');
        html += chip('L' + d.level, 'var(--text-secondary)', 'rgba(255,255,255,0.06)');
        html += chip('zone ' + d.zoneId + ' · map ' + d.mapId, 'var(--text-muted)', 'rgba(255,255,255,0.04)');
        html += '</div></div>';

        // --- "Right now" narrative (synthesizes the state below into plain English) ---
        html += renderStoryCard(d);

        // --- Vitals strip ---
        var durColor = d.durability < 30 ? '#f7768e' : (d.durability < 60 ? '#e0af68' : '#9ece6a');
        var bagColor = d.freeSlots <= 0 ? '#f7768e' : (d.freeSlots <= 2 ? '#e0af68' : '#9ece6a');
        html += '<div class="bt-live-stats">';
        html += statBox('HP', d.hpPct + '%', d.hpPct < 35 ? '#f7768e' : '#9ece6a');
        if (d.manaPct > 0 && d.manaPct < 100) html += statBox('MP', d.manaPct + '%', '#7aa2f7');
        html += statBox('Durability', d.durability + '%', durColor);
        html += statBox('Free bags', d.freeSlots, bagColor);
        html += statBox('Gold', formatGold(d.copper), '#e0af68');
        html += '</div>';

        // --- THE WAIT (hero signal) ---
        html += '<div class="bt-live-card">';
        html += '<div class="bt-live-card-h"><i class="fa-solid fa-hourglass-half"></i> Outstanding command</div>';
        if (d.pending) {
            var age = ageUp(d.pending.ageSec);
            var dl = ageDn(d.pending.secsToDeadline);
            var dlColor = dl == null ? 'var(--text-muted)' : (dl > 60 ? '#9ece6a' : (dl > 15 ? '#e0af68' : '#f7768e'));
            html += '<div class="bt-live-wait">';
            html += '<span class="bt-live-wait-cmd">' + esc(d.pending.cmd) + '</span>';
            var pdest = liveDest(d);
            if (pdest) html += ' <span style="color:var(--text-muted);">→</span> <span class="bt-live-wait-dest">' + esc(pdest.label) + '</span>';
            html += ' <span style="color:var(--text-muted);">· waiting</span> ';
            html += '<span class="bt-live-wait-evt">' + esc(d.pending.expect) + '</span>';
            html += ' <span style="color:var(--text-muted);">(' + secs(age) + ')</span>';
            html += '<span style="float:right;color:' + dlColor + ';font-weight:700;">deadline ' + secs(dl) + '</span>';
            html += '</div><div class="bt-live-badges" style="margin-top:6px;">';
            if (d.pending.isObjectiveGrind) html += chip('objective grind', '#f7768e', 'rgba(247,118,142,0.15)');
            if (d.pending.interruptible) html += chip('interruptible trek', '#73daca', 'rgba(115,218,202,0.15)');
            if (d.distToTarget != null) html += chip('tgt ' + d.distToTarget + 'y', '#7aa2f7', 'rgba(122,162,247,0.12)');
            html += '</div>';
        } else {
            html += '<div style="color:var(--text-muted);font-size:12px;">— none (between commands)</div>';
        }
        html += '</div>';

        // --- Failure (only if present) ---
        if (d.failure) {
            html += '<div class="bt-live-card" style="border-color:rgba(247,118,142,0.4);background:rgba(247,118,142,0.06);">';
            html += '<div class="bt-live-card-h" style="color:#f7768e;"><i class="fa-solid fa-triangle-exclamation"></i> Last failure</div>';
            html += '<div style="font-size:12px;">' + esc(d.failure.cmd) + ' ← <span style="color:#f7768e;font-weight:700;">' +
                esc(d.failure.reason) + '</span> <span style="color:var(--text-muted);">(' + secs(ageUp(d.failure.ageSec)) + ')</span>';
            if (d.failure.danger > 0) html += ' ' + chip('danger ' + d.failure.danger, '#ff9e64', 'rgba(255,158,100,0.15)');
            if (d.failure.questId) html += ' ' + chip('quest #' + d.failure.questId, '#e0af68', 'rgba(224,175,104,0.12)');
            html += '</div></div>';
        }

        // --- Stall (only if present) ---
        if (d.stall) {
            html += '<div class="bt-live-card" style="border-color:rgba(247,118,142,0.5);background:rgba(247,118,142,0.1);">';
            html += '<div style="color:#f7768e;font-weight:700;font-size:12px;"><i class="fa-solid fa-ban" style="margin-right:5px;"></i>STALLED — ' +
                esc(d.stall.reason) + ' (' + secs(ageUp(d.stall.sinceSec)) + ')</div></div>';
        }

        // --- Progress clocks ---
        html += '<div class="bt-live-card">';
        html += '<div class="bt-live-card-h"><i class="fa-solid fa-gauge-high"></i> Progress</div>';
        html += '<div class="bt-live-rows">';
        html += liveRow('In goal', secs(ageUp(d.timeInGoalSec)));
        html += liveRow('In step', secs(ageUp(d.timeInStepSec)));
        html += liveRow('No progress', clk(ageUp(d.noProgressSec), 30, 120));
        html += liveRow('Last kill', clk(ageUp(d.lastKillSec), 30, 120));
        html += liveRow('Last quest advance', clk(ageUp(d.lastQuestSec), 120, 600));
        html += liveRow('Last level', secs(ageUp(d.lastLevelSec)));
        html += '</div></div>';

        // --- Active scratch ---
        html += renderLiveScratch(d.scratch, d);

        // --- Position footer ---
        html += '<div class="bt-live-foot">pos ' + Math.round(d.pos.x) + ', ' + Math.round(d.pos.y) + ', ' + Math.round(d.pos.z) +
            ' @ map ' + d.mapId + ' &nbsp;·&nbsp; refreshed ' + liveOff() + 's ago</div>';

        // Lay the scaffold once per bot so the log panel (appended incrementally by the
        // 2s poll) survives the 1s state re-render. Then update only the state region.
        // Rebuild also when the scaffold node is gone — switching to Overview overwrites
        // #detailTabBody, so returning to Live for the SAME bot finds no #liveState to fill.
        if (liveScaffoldGuid !== selectedGuid || $('#liveState').length === 0) {
            $body.html(
                '<div id="liveState"></div>' +
                '<div class="bt-live-card" id="liveLogCard">' +
                '<div class="bt-live-card-h"><i class="fa-solid fa-terminal"></i> Live log ' +
                '<span id="liveLogMeta" style="font-weight:400;color:var(--text-muted);"></span>' +
                '<button id="btnBotReport" class="bt-report-btn" title="Quantized report from this bot\'s buffered log">' +
                '<i class="fa-solid fa-bolt"></i> Report</button></div>' +
                '<div id="liveLogBox" class="bt-live-log"></div>' +
                '</div>');
            liveScaffoldGuid = selectedGuid;
        }
        $('#liveState').html(html);
    }

    function liveRow(label, valHtml) {
        return '<div class="bt-live-row"><span class="bt-live-row-lbl">' + esc(label) + '</span>' +
            '<span class="bt-live-row-val">' + valHtml + '</span></div>';
    }

    function renderLiveScratch(sc, d) {
        if (!sc || sc.kind === 'none') {
            return '<div class="bt-live-card"><div class="bt-live-card-h"><i class="fa-solid fa-list-check"></i> Task</div>' +
                '<div style="color:var(--text-muted);font-size:12px;">No active task scratch.</div></div>';
        }
        var html = '<div class="bt-live-card">';

        if (sc.kind === 'quest') {
            html += '<div class="bt-live-card-h"><i class="fa-solid fa-scroll"></i> Quest — ' + sc.count + ' in batch ' +
                chip(d.step, 'var(--text-secondary)', 'rgba(255,255,255,0.06)') + '</div>';

            // Active quest detail — title, objectives (active highlighted), where to accept / hand in.
            var aq = sc.active;
            if (aq) {
                html += '<div class="bt-live-active">';
                html += '<div class="bt-live-active-t">★ ' + esc(aq.title || ('#' + aq.id)) +
                    ' <span style="color:var(--text-muted);font-weight:400;">#' + aq.id + (aq.level ? ' · L' + aq.level : '') + '</span></div>';

                if (aq.objectives && aq.objectives.length) {
                    var killIdx = 0, itemIdx = 0;
                    for (var oi = 0; oi < aq.objectives.length; oi++) {
                        var o = aq.objectives[oi];
                        var icon = o.kind === 'kill' ? 'fa-khanda' : (o.kind === 'gather' ? 'fa-hand-holding' : 'fa-hand-pointer');

                        // Authoritative server kill credit overrides the spine's un-fed `have`
                        // (no per-kill QUEST_UPDATE → projected count sticks at its seed).
                        var srv = serverObjFor(aq.id, o, killIdx, itemIdx);
                        if (o.kind === 'kill') killIdx++; else if (o.kind === 'gather') itemIdx++;
                        var have = (srv != null) ? srv.have : o.have;
                        var need = (srv != null && srv.need > 0) ? srv.need : o.need;

                        var done = (have != null && need > 0 && have >= need);
                        var cnt = (have != null) ? (have + '/' + need) : ('×' + need);
                        var cntColor = done ? '#9ece6a' : (o.active ? '#e0af68' : 'var(--text-secondary)');
                        html += '<div class="bt-live-obj' + (o.active ? ' active' : '') + '">';
                        html += '<i class="fa-solid ' + icon + '" style="width:14px;"></i> ';
                        html += '<span class="bt-live-obj-cnt" style="color:' + cntColor + ';">' + cnt + '</span> ';
                        if (srv != null) html += '<span class="bt-live-obj-srv" title="live server kill credit (character_queststatus.mob_count)">srv</span> ';
                        html += '<span class="bt-live-obj-name">' + esc(o.name) + '</span>';
                        if (o.kind === 'gather' && o.from) html += ' <span style="color:var(--text-muted);">from ' + esc(o.from) + '</span>';
                        // No-credit flag: actively grinding this kill, killed in the last 90s,
                        // server credit still 0 → real tag-credit contention, not a display gap.
                        if (o.kind === 'kill' && o.active && srv != null && have === 0 && need > 0 &&
                            d.lastKillSec != null && ageUp(d.lastKillSec) < 90) {
                            html += ' ' + chip('no quest credit', '#ff9e64', 'rgba(255,158,100,0.15)');
                        }
                        html += distTag(d, o);
                        html += '</div>';
                    }
                }

                if (aq.giver) html += questNpcRow('Accept', 'fa-circle-question', aq.giver, d, '#7aa2f7');
                if (aq.turnIn) html += questNpcRow('Turn in', 'fa-flag-checkered', aq.turnIn, d, '#9ece6a');
                html += '</div>';
            }

            // Full batch — id + title, active row highlighted.
            html += '<div class="bt-live-qlist">';
            for (var i = 0; i < sc.batch.length; i++) {
                var q = sc.batch[i];
                var col = '#7aa2f7', tag = 'fa-circle-dot';
                if (q.turnedIn) { col = '#9ece6a'; tag = 'fa-circle-check'; }
                else if (q.failed) { col = '#f7768e'; tag = 'fa-circle-xmark'; }
                else if (q.deferred) { col = '#e0af68'; tag = 'fa-circle-pause'; }
                else if (q.force) { col = '#bb9af7'; tag = 'fa-bolt'; }
                else if (q.accepted) { col = '#73daca'; tag = 'fa-circle-dot'; }
                var isActive = (sc.activeId && q.id === sc.activeId);
                html += '<div class="bt-live-qrow' + (isActive ? ' active' : '') + '">' +
                    '<i class="fa-solid ' + tag + '" style="color:' + col + ';width:14px;"></i> ' +
                    '<span class="bt-live-qid">#' + q.id + '</span> ' +
                    '<span class="bt-live-qtitle">' + esc(q.title || '(unknown)') + '</span></div>';
            }
            html += '</div>';
        } else if (sc.kind === 'grind') {
            html += '<div class="bt-live-card-h"><i class="fa-solid fa-khanda"></i> Grind</div>';
            html += '<div class="bt-live-rows">';
            html += liveRow('Creature entry', sc.creatureEntry || 'any');
            html += liveRow('Kills', sc.killCount + (sc.killGoal > 0 ? ' / ' + sc.killGoal : ' (indefinite)'));
            html += liveRow('Radius', Math.round(sc.radius) + 'y');
            html += '</div>';
        } else if (sc.kind === 'maintenance') {
            var phaseColor = { 'rez-wait': '#ff9e64', 'resurrecting': '#ff9e64', 'relocate': '#e0af68', 'heal': '#9ece6a', 'done': '#73daca', 'post-rez': '#7aa2f7' };
            html += '<div class="bt-live-card-h" style="color:#ff9e64;"><i class="fa-solid fa-kit-medical"></i> Recovery</div>';
            html += '<div class="bt-live-badges" style="margin-bottom:6px;">';
            html += chip(sc.phase.toUpperCase(), '#1a1b26', (phaseColor[sc.phase] || '#ff9e64'));
            if (sc.deathLoop) html += chip('death loop', '#f7768e', 'rgba(247,118,142,0.15)');
            if (sc.escalated) html += chip('graveyard port', '#bb9af7', 'rgba(187,154,247,0.15)');
            html += '</div><div class="bt-live-rows">';
            if (sc.deadForSec != null) html += liveRow('Dead for', secs(ageUp(sc.deadForSec)));
            if (sc.rezInSec != null && sc.rezInSec > -3600) html += liveRow('Rez in', secs(ageDn(sc.rezInSec)));
            html += liveRow('Relocate', sc.relocateDone ? 'done' : (sc.relocateSent ? 'in progress' : 'pending'));
            html += liveRow('Heal', sc.healDone ? 'done' : 'pending');
            html += '</div>';
        } else if (sc.kind === 'vendor') {
            html += '<div class="bt-live-card-h" style="color:#e0af68;"><i class="fa-solid fa-store"></i> Vendor errand</div>';
            html += '<div class="bt-live-badges" style="margin-bottom:6px;">';
            html += chip(sc.phase.toUpperCase(), '#1a1b26', '#e0af68');
            if (sc.canRepair) html += chip('repairs', '#9ece6a', 'rgba(158,206,106,0.15)');
            html += '</div><div class="bt-live-rows">';
            html += liveRow('Vendor NPC', '#' + sc.npcEntry);
            if (sc.startedSec != null) html += liveRow('Trip time', secs(ageUp(sc.startedSec)));
            html += '</div>';
        }

        html += '</div>';
        return html;
    }

    function renderDetailInner(s, brain) {

        var html = '';

        // --- Bot Header ---
        var className = CLASS_NAMES[s.classId] || '?';
        var raceName = RACE_NAMES[s.race] || '?';
        var hpPct = (s.maxHealth || 0) > 0 ? Math.round((s.health || 0) / s.maxHealth * 100) : 0;
        var mpPct = (s.maxMana || 0) > 0 ? Math.round((s.mana || 0) / s.maxMana * 100) : 0;
        var posX = (s.x != null ? s.x : 0).toFixed(0);
        var posY = (s.y != null ? s.y : 0).toFixed(0);

        html += '<div class="bt-section"><div class="bt-section-body">' +
            '<div class="d-flex align-items-center justify-content-between mb-2">' +
            '<div><span style="font-size:16px;font-weight:700;">' + esc(s.name) + '</span> ' +
            '<span class="bt-class-badge ' + (CLASS_CSS[s.classId] || '') + '">' + className + '</span>' +
            ' <button class="btn-sm btnOpenModal" data-guid="' + s.guid + '" style="font-size:10px;padding:2px 10px;cursor:pointer;background:var(--bg-card-alt,#24283b);border:1px solid var(--border-light,#414868);border-radius:3px;color:var(--accent,#7aa2f7);margin-left:8px;">' +
            '<i class="fa-solid fa-up-right-from-square" style="margin-right:3px;"></i>Details</button>' +
            ' <button class="btn-sm btnBotControl" data-guid="' + s.guid + '" title="Move, say, quest, grind, trace — everything for this bot" ' +
            'style="font-size:10px;padding:2px 10px;cursor:pointer;background:var(--accent-subtle,#24283b);border:1px solid var(--accent,#7aa2f7);border-radius:3px;color:var(--accent,#7aa2f7);margin-left:6px;">' +
            '<i class="fa-solid fa-sliders" style="margin-right:3px;"></i>Control</button></div>' +
            '<div style="font-size:12px;color:var(--text-muted);">L' + (s.level || 0) + ' ' + raceName + ' — Map ' + (s.mapId || 0) + ' (' + posX + ', ' + posY + ')</div>' +
            '</div>' +
            '<div class="d-flex gap-3" style="font-size:12px;">' +
            '<div><span style="color:#9ece6a;">HP ' + hpPct + '%</span></div>' +
            '<div><span style="color:#7aa2f7;">MP ' + mpPct + '%</span></div>' +
            (s.inCombat ? '<div><span style="color:#f7768e;font-weight:600;">IN COMBAT</span></div>' : '') +
            (s.isDead ? '<div><span style="color:#f7768e;font-weight:600;">DEAD</span></div>' : '') +
            '</div>';

        // Sub-phase + quest info from brain
        if (brain && brain.subPhase) {
            html += '<div style="margin-top:6px;font-size:11px;color:var(--text-muted);">' +
                '<i class="fa-solid fa-route" style="margin-right:4px;"></i>' +
                '<span style="color:var(--text-secondary);">' + esc(brain.activity || '') + '</span>' +
                ' → <span style="color:#7aa2f7;">' + esc(brain.subPhase) + '</span>';
            // (Quest # moved to the realtime "Current quest" section below — the brain
            //  summary's activeQuestId is stale; the spine scratch is the live truth.)
            if (brain.contextTag) html += ' <span style="color:var(--text-muted);">' + esc(brain.contextTag) + '</span>';
            html += '</div>';
        }

        // Pending action indicator
        if (brain && brain.pendingAction) {
            html += '<div style="margin-top:4px;font-size:11px;"><span style="color:#ff9e64;">' +
                '<i class="fa-solid fa-rotate-left" style="margin-right:4px;"></i>Pending: return to ' +
                esc(brain.pendingAction.returnTo) + ' (' + esc(brain.pendingAction.subPhase || '') + ')' +
                (brain.pendingAction.questId ? ' quest #' + brain.pendingAction.questId : '') +
                '</span></div>';
        }

        html += '</div></div>';

        // --- Current quest (realtime spine + server kill-credit; filled by the live poll) ---
        html += '<div class="bt-section"><div class="bt-section-header"><span>' +
            '<i class="fa-solid fa-scroll" style="color:#e0af68;margin-right:6px;"></i>Current quest</span></div>' +
            '<div class="bt-section-body"><div id="detailQuest"></div></div></div>';

        // --- Personality Section ---
        if (brain && brain.personality) {
            var p = brain.personality;
            html += '<div class="bt-section"><div class="bt-section-header"><span><i class="fa-solid fa-fingerprint" style="color:var(--accent);margin-right:6px;"></i>Personality</span>' +
                '<span style="font-weight:400;text-transform:none;letter-spacing:0;font-size:11px;color:var(--text-muted);">' +
                p.chatStyle + ' / ' + p.temperament + ' — tick base ' + p.tickBase.toFixed(1) + 's</span></div>';
            html += '<div class="bt-section-body">';

            var traits = ['patience', 'greed', 'curiosity', 'sociability', 'aggression', 'efficiency', 'cautiousness', 'indecisiveness', 'spontaneity'];
            for (var i = 0; i < traits.length; i++) {
                var t = traits[i];
                var val = p[t];
                var meta = TRAIT_META[t] || { icon: 'fa-circle', color: '#888' };
                var pct = Math.round(val * 100);
                html += '<div class="bt-trait">' +
                    '<span class="bt-trait-icon"><i class="fa-solid ' + meta.icon + '" style="color:' + meta.color + ';"></i></span>' +
                    '<span class="bt-trait-label">' + capitalize(t) + '</span>' +
                    '<div class="bt-trait-bar-track"><div class="bt-trait-bar-fill" style="width:' + pct + '%;background:' + meta.color + ';"></div></div>' +
                    '<span class="bt-trait-val">' + pct + '</span>' +
                    '</div>';
            }

            if (p.quirks && p.quirks.length > 0) {
                html += '<div style="margin-top:10px;">';
                for (var qi = 0; qi < p.quirks.length; qi++) {
                    var q = p.quirks[qi];
                    html += '<span class="bt-quirk" title="' + esc(q.description || '') + '"><i class="fa-solid fa-star" style="font-size:9px;"></i> ' + esc(q.name) + '</span>';
                }
                html += '</div>';
            } else {
                html += '<div style="margin-top:8px;font-size:11px;color:var(--text-muted);">No quirks</div>';
            }

            html += '</div></div>';
        }

        // --- Last Decision / Weights ---
        html += '<div class="bt-section"><div class="bt-section-header"><span><i class="fa-solid fa-scale-balanced" style="color:var(--accent);margin-right:6px;"></i>Decision Weights</span></div>';
        html += '<div class="bt-section-body"><div class="bt-weights" id="weightsGrid">';
        if (brain && brain.lastDecision && brain.lastDecision.weights) {
            html += renderWeightsHtml(brain.lastDecision.weights);
        } else {
            html += '<div style="font-size:12px;color:var(--text-muted);grid-column:1/-1;">Waiting for first decision tick...</div>';
        }
        html += '</div></div></div>';

        // --- Economy (real data from enriched STATE) ---
        var copper = s.copper || 0;
        var freeSlots = s.freeSlots != null ? s.freeSlots : 16;
        var totalSlots = s.totalSlots != null ? s.totalSlots : 16;
        var usedSlots = totalSlots - freeSlots;
        var bagPct = totalSlots > 0 ? Math.round(usedSlots / totalSlots * 100) : 0;
        var bagColor = bagPct >= 90 ? '#f7768e' : (bagPct >= 70 ? '#e0af68' : '#9ece6a');

        html += '<div class="bt-section"><div class="bt-section-header">' +
            '<span><i class="fa-solid fa-coins" style="color:#e0af68;margin-right:6px;"></i>Economy</span>' +
            '<button class="btn-sm" id="btnLoadInventory" style="font-size:10px;padding:2px 8px;cursor:pointer;background:var(--bg-card-alt);border:1px solid var(--border-light);border-radius:3px;color:var(--text-secondary);">' +
            '<i class="fa-solid fa-box-open" style="margin-right:3px;"></i>Inventory</button>' +
            '</div>';
        html += '<div class="bt-section-body">';
        html += '<div class="bt-econ-grid" id="econStrip">';
        html += renderEconStripHtml(s, brain);
        html += '</div>';
        // Inventory container (populated on click)
        html += '<div id="inventoryPanel" style="display:none;margin-top:12px;"></div>';
        html += '</div></div>';

        // --- Activity Timeline ---
        html += '<div class="bt-section"><div class="bt-section-header"><span><i class="fa-solid fa-clock-rotate-left" style="color:var(--accent);margin-right:6px;"></i>Activity Timeline</span></div>';
        html += '<div class="bt-section-body"><div class="bt-timeline" id="timeline"></div></div></div>';

        $('#detailTabBody').html(html);
        renderTimeline(selectedGuid);
        updateDetailQuest();   // fill the realtime "Current quest" section
    }

    // ---- Overview "Current quest": realtime quest progress — the same spine scratch +
    // server kill-credit the Live tab renders — written into the stable #detailQuest
    // container so the 4-5s polls refresh it in place without rebuilding the Overview panel.
    function updateDetailQuest() {
        var $box = $('#detailQuest');
        if ($box.length === 0) return;            // not on Overview / panel not built yet
        $box.html(renderDetailQuestHtml());
    }

    function renderDetailQuestHtml() {
        var d = liveData;
        if (!d || liveGuid !== selectedGuid) {
            return '<div style="font-size:11px;color:var(--text-muted);">' +
                '<i class="fa-solid fa-satellite-dish fa-fade" style="margin-right:5px;"></i>' +
                'connecting to live feed… (brain engine must be ON)</div>';
        }

        var goalColor = GOAL_COLOR[d.goal] || 'var(--accent)';
        var head = '<div style="font-size:12px;margin-bottom:6px;">' +
            '<span style="color:' + goalColor + ';font-weight:700;">' + esc(d.goal) + '</span>' +
            '<span style="color:var(--text-muted);"> / ' + esc(d.step) + '</span>' +
            (d.why ? '<span style="color:var(--text-muted);margin-left:8px;">why = ' + esc(d.why) + '</span>' : '') +
            '</div>';

        var sc = d.scratch;
        if (!sc || sc.kind !== 'quest' || !sc.active) {
            return head + '<div style="font-size:11px;color:var(--text-muted);">No active quest objective right now.</div>';
        }

        var aq = sc.active;
        var html = head + '<div class="bt-live-active">';
        html += '<div class="bt-live-active-t">★ ' + esc(aq.title || ('#' + aq.id)) +
            ' <span style="color:var(--text-muted);font-weight:400;">#' + aq.id + (aq.level ? ' · L' + aq.level : '') + '</span></div>';

        if (aq.objectives && aq.objectives.length) {
            var killIdx = 0, itemIdx = 0;
            for (var oi = 0; oi < aq.objectives.length; oi++) {
                var o = aq.objectives[oi];
                var icon = o.kind === 'kill' ? 'fa-khanda' : (o.kind === 'gather' ? 'fa-hand-holding' : 'fa-hand-pointer');
                // Authoritative server kill credit overrides the spine's un-fed `have`.
                var srv = serverObjFor(aq.id, o, killIdx, itemIdx);
                if (o.kind === 'kill') killIdx++; else if (o.kind === 'gather') itemIdx++;
                var have = (srv != null) ? srv.have : o.have;
                var need = (srv != null && srv.need > 0) ? srv.need : o.need;
                var done = (have != null && need > 0 && have >= need);
                var cnt = (have != null) ? (have + '/' + need) : ('×' + need);
                var cntColor = done ? '#9ece6a' : (o.active ? '#e0af68' : 'var(--text-secondary)');
                html += '<div class="bt-live-obj' + (o.active ? ' active' : '') + '">';
                html += '<i class="fa-solid ' + icon + '" style="width:14px;"></i> ';
                html += '<span class="bt-live-obj-cnt" style="color:' + cntColor + ';">' + cnt + '</span> ';
                if (srv != null) html += '<span class="bt-live-obj-srv" title="live server kill credit (character_queststatus.mob_count)">srv</span> ';
                html += '<span class="bt-live-obj-name">' + esc(o.name) + '</span>';
                if (o.kind === 'gather' && o.from) html += ' <span style="color:var(--text-muted);">from ' + esc(o.from) + '</span>';
                html += distTag(d, o);
                html += '</div>';
            }
        }
        if (aq.giver) html += questNpcRow('Accept', 'fa-circle-question', aq.giver, d, '#7aa2f7');
        if (aq.turnIn) html += questNpcRow('Turn in', 'fa-flag-checkered', aq.turnIn, d, '#9ece6a');
        html += '</div>';

        if (sc.batch && sc.batch.length) {
            var doneN = 0;
            for (var bi = 0; bi < sc.batch.length; bi++) if (sc.batch[bi].turnedIn) doneN++;
            html += '<div style="font-size:11px;color:var(--text-muted);margin-top:6px;">batch: ' +
                sc.batch.length + ' quest' + (sc.batch.length === 1 ? '' : 's') +
                (doneN ? ' · ' + doneN + ' turned in' : '') + '</div>';
        }
        return html;
    }

    // --- Economy strip (updates live from STATE) ---
    function renderEconStripHtml(s, brain) {
        var copper = s.copper || 0;
        var freeSlots = s.freeSlots != null ? s.freeSlots : 16;
        var totalSlots = s.totalSlots != null ? s.totalSlots : 16;
        var usedSlots = totalSlots - freeSlots;
        var bagPct = totalSlots > 0 ? Math.round(usedSlots / totalSlots * 100) : 0;
        var bagColor = bagPct >= 90 ? '#f7768e' : (bagPct >= 70 ? '#e0af68' : '#9ece6a');

        var html = '';
        html += '<div class="bt-econ-item"><div class="bt-econ-val" style="color:#e0af68;">' + formatGold(copper) + '</div><div class="bt-econ-label">Gold</div></div>';
        html += '<div class="bt-econ-item"><div class="bt-econ-val" style="color:' + bagColor + ';">' + usedSlots + '/' + totalSlots + '</div><div class="bt-econ-label">Bag Slots</div></div>';
        html += '<div class="bt-econ-item"><div class="bt-econ-val">' + (brain && brain.hasUnlearnedSpells ? '<span style="color:#f7768e;">Yes</span>' : '<span style="color:#9ece6a;">No</span>') + '</div><div class="bt-econ-label">Needs Training</div></div>';
        return html;
    }

    function updateEconomyStrip(s) {
        var $strip = $('#econStrip');
        if ($strip.length === 0) return;
        var brain = botBrains[s.guid];
        $strip.html(renderEconStripHtml(s, brain));
    }

    // --- Inventory panel (lazy-loaded from /Bots/Inventory) ---
    $(document).on('click', '#btnLoadInventory', function () {
        var $panel = $('#inventoryPanel');
        if ($panel.is(':visible')) {
            $panel.hide();
            return;
        }
        if (!selectedGuid) return;

        // Check cache
        if (inventoryCache[selectedGuid]) {
            renderInventoryPanel(inventoryCache[selectedGuid]);
            return;
        }

        $panel.html('<div style="text-align:center;padding:12px;color:var(--text-muted);"><i class="fa-solid fa-spinner fa-spin"></i> Loading inventory...</div>').show();

        $.getJSON('/Bots/Inventory', { guid: selectedGuid }, function (data) {
            if (data.error) {
                $panel.html('<div style="color:#f7768e;font-size:12px;">Error: ' + esc(data.error) + '</div>');
                return;
            }
            inventoryCache[selectedGuid] = data;
            renderInventoryPanel(data);
        }).fail(function () {
            $panel.html('<div style="color:#f7768e;font-size:12px;">Failed to load inventory</div>');
        });
    });

    function renderInventoryPanel(data) {
        $('#inventoryPanel').html(inventoryHtml(data)).show();
    }

    // Inventory markup, split out of renderInventoryPanel so the page's Economy section and the
    // modal's Gear tab render from exactly one place.
    function inventoryHtml(data) {
        var html = '';
        var icons = data.icons || {};

        // Equipped gear
        if (data.equipped && data.equipped.length > 0) {
            html += '<div class="bt-inv-section-title"><i class="fa-solid fa-shield-halved"></i> Equipped</div>';
            html += '<div class="bt-inv-grid">';
            for (var i = 0; i < data.equipped.length; i++) {
                html += renderInvItem(data.equipped[i], true, icons);
            }
            html += '</div>';
        }

        // Backpack
        if (data.backpack && data.backpack.length > 0) {
            html += '<div class="bt-inv-section-title" style="margin-top:10px;"><i class="fa-solid fa-suitcase"></i> Backpack (' + data.backpack.length + '/16)</div>';
            html += '<div class="bt-inv-grid">';
            for (var i = 0; i < data.backpack.length; i++) {
                html += renderInvItem(data.backpack[i], false, icons);
            }
            html += '</div>';
        }

        // Extra bags
        if (data.bags && data.bags.length > 0) {
            for (var b = 0; b < data.bags.length; b++) {
                var bag = data.bags[b];
                var bagName = bag.bag ? bag.bag.name : 'Bag';
                html += '<div class="bt-inv-section-title" style="margin-top:10px;"><i class="fa-solid fa-box"></i> ' + esc(bagName) + ' (' + bag.used + '/' + bag.capacity + ')</div>';
                if (bag.contents.length > 0) {
                    html += '<div class="bt-inv-grid">';
                    for (var c = 0; c < bag.contents.length; c++) {
                        html += renderInvItem(bag.contents[c], false, icons);
                    }
                    html += '</div>';
                } else {
                    html += '<div style="font-size:11px;color:var(--text-muted);padding:4px 0;">Empty</div>';
                }
            }
        }

        // Summary
        if (data.totalSellValue > 0) {
            html += '<div style="margin-top:8px;font-size:11px;color:var(--text-muted);">' +
                'Total sell value: <span style="color:#e0af68;">' + formatGold(data.totalSellValue) + '</span>' +
                '</div>';
        }

        return html;
    }

    function renderInvItem(item, isEquipped, icons) {
        // Page-surface colour, NOT the game-background colour: QUALITY_COLORS[1] is #ffffff, which
        // is why Common items rendered as blank space on the card while the dark tooltip showed them.
        var qColor = QUALITY_TEXT[item.quality] || 'var(--text-primary)';
        var slotLabel = isEquipped ? (EQUIP_SLOT_NAMES[item.inventoryType] || 'Slot ' + item.slot) : '';
        var iconPath = (item.displayId && icons[item.displayId]) ? icons[item.displayId] : '/Icon/Get?name=inv_misc_questionmark';
        var count = item.stackCount || 1;

        return '<div class="bt-inv-item"' +
            ' data-tt-name="' + esc(item.name) + '"' +
            ' data-tt-quality="' + item.quality + '"' +
            ' data-tt-class="' + item.itemClass + '"' +
            ' data-tt-subclass="' + (item.subclass || 0) + '"' +
            ' data-tt-invtype="' + item.inventoryType + '"' +
            ' data-tt-ilvl="' + item.itemLevel + '"' +
            ' data-tt-armor="' + item.armor + '"' +
            ' data-tt-sell="' + item.sellPrice + '"' +
            ' data-tt-equipped="' + isEquipped + '"' +
            ' data-tt-count="' + count + '"' +
            '>' +
            '<div class="bt-inv-icon-wrap">' +
            '<img class="bt-inv-icon" src="' + esc(iconPath) + '" alt="" loading="lazy" />' +
            (count > 1 ? '<span class="bt-inv-count">' + count + '</span>' : '') +
            '</div>' +
            '<span class="bt-inv-name" style="color:' + qColor + ';" title="' + esc(item.name) + '">' + esc(item.name) + '</span>' +
            (isEquipped ? '<span class="bt-inv-slot">' + slotLabel + '</span>' : '') +
            (item.armor > 0 ? '<span class="bt-inv-stat">' + item.armor + ' armor</span>' : '') +
            '</div>';
    }

    // ===================== WEIGHTS =====================

    function renderWeightsHtml(weights) {
        var html = '';
        var maxW = 0;
        var keys = Object.keys(weights);
        for (var i = 0; i < keys.length; i++) if (weights[keys[i]] > maxW) maxW = weights[keys[i]];
        if (maxW === 0) maxW = 1;

        keys.sort(function (a, b) { return weights[b] - weights[a]; });
        for (var i = 0; i < keys.length; i++) {
            var k = keys[i];
            var v = weights[k];
            var pct = Math.round(v / maxW * 100);
            html += '<div class="bt-weight-row">' +
                '<span class="bt-weight-label">' + k + '</span>' +
                '<div class="bt-weight-bar-track"><div class="bt-weight-bar-fill" style="width:' + pct + '%;"></div></div>' +
                '<span class="bt-weight-val">' + v.toFixed(2) + '</span>' +
                '</div>';
        }
        return html;
    }

    function renderWeights(weights) {
        var $grid = $('#weightsGrid');
        if ($grid.length === 0) return;
        $grid.html(renderWeightsHtml(weights));
    }

    // ===================== TIMELINE =====================

    function renderTimeline(guid) {
        var $tl = $('#timeline');
        if ($tl.length === 0) return;

        var entries = decisionLog[guid];
        if (!entries || entries.length === 0) {
            $tl.html('<div style="color:#5f6b7a;">No decisions recorded yet.</div>');
            return;
        }

        var html = '';
        var start = Math.max(0, entries.length - 30);
        for (var i = start; i < entries.length; i++) {
            var e = entries[i];
            var cls = e.activityChanged ? 'bt-tl-switch' : 'bt-tl-stay';
            var ts = new Date(e.timestamp).toLocaleTimeString();
            html += '<div class="' + cls + '">[' + ts + '] ' + esc(e.decision) + '</div>';
        }
        $tl.html(html);
        $tl[0].scrollTop = $tl[0].scrollHeight;
    }

    function tlAppend(guid, text, cls) {
        if (selectedGuid !== guid) return;
        var $tl = $('#timeline');
        if ($tl.length === 0) return;
        var ts = new Date().toLocaleTimeString();
        $tl.append('<div class="' + (cls || 'bt-tl-event') + '">[' + ts + '] ' + esc(text) + '</div>');
        while ($tl.children().length > maxTimelineEntries) $tl.children(':first').remove();
        $tl[0].scrollTop = $tl[0].scrollHeight;
    }

    // ===================== STATS =====================

    function updateStats() {
        var guids = Object.keys(botStates);
        var tracked = 0;
        for (var i = 0; i < guids.length; i++) {
            if (botStates[guids[i]].taskState !== 'DISCONNECTED') tracked++;
        }
        $('#statTracked').text(tracked);
        $('#statBrains').text(Object.keys(botBrains).length);

        var elapsed = (Date.now() - dpmStartTime) / 60000;
        var dpm = elapsed > 0 ? Math.round(decisionCount / elapsed) : 0;
        $('#statDpm').text(dpm);
    }

    // Fills every bot picker on the page or in the modal. Any <select data-botlist> gets the
    // connected roster; data-exclude="<guid>" drops one bot (the group leader picks followers).
    function updateBotDropdown() {
        var guids = Object.keys(botStates).sort(function (a, b) {
            return (botStates[a].name || '').localeCompare(botStates[b].name || '');
        });
        $('[data-botlist]').each(function () {
            var $sel = $(this);
            var current = $sel.val();
            var exclude = parseInt($sel.attr('data-exclude'), 10) || 0;
            $sel.find('option[data-bot]').remove();
            for (var i = 0; i < guids.length; i++) {
                var s = botStates[guids[i]];
                if (!s || s.taskState === 'DISCONNECTED') continue;
                if (exclude && s.guid === exclude) continue;
                $sel.append('<option data-bot value="' + s.guid + '">' + esc(s.name) + ' (L' + s.level + ')</option>');
            }
            if (current) $sel.val(current);
        });
    }

    // ===================== PERIODIC BRAIN REFRESH =====================
    // Poll brain state every 5s for the selected bot so sub-phase/quest
    // info stays fresh between strategic evals (which can be 3-10 min apart)

    var brainPollTimer = null;
    var rosterPollTimer = null;

    function startBrainPoll() {
        stopBrainPoll();
        brainPollTimer = setInterval(function () {
            if (!selectedGuid || !connected) return;
            $.getJSON('/Bots/BrainState/' + selectedGuid, function (data) {
                if (data && data.guid) {
                    var existing = botBrains[data.guid] || {};
                    var prevDecision = existing.lastDecision;
                    botBrains[data.guid] = data;
                    if (prevDecision) botBrains[data.guid].lastDecision = prevDecision;
                    updateBrainHeader(data.guid);
                    renderRosterCard(data.guid);
                }
            });
        }, 5000);
    }

    function stopBrainPoll() {
        if (brainPollTimer) { clearInterval(brainPollTimer); brainPollTimer = null; }
    }

    // Refresh just the header/sub-phase section without re-rendering the whole detail panel
    function updateBrainHeader(guid) {
        if (selectedGuid !== guid) return;
        var s = botStates[guid];
        var brain = botBrains[guid];
        if (!s || !brain) return;

        // Update header info strip
        var $header = $('#botHeaderInfo');
        if ($header.length > 0) {
            var hpPct = (s.maxHealth || 0) > 0 ? Math.round((s.health || 0) / s.maxHealth * 100) : 0;
            var mpPct = (s.maxMana || 0) > 0 ? Math.round((s.mana || 0) / s.maxMana * 100) : 0;
            var headerHtml = '<span style="color:#9ece6a;">HP ' + hpPct + '%</span>';
            headerHtml += ' &nbsp; <span style="color:#7aa2f7;">MP ' + mpPct + '%</span>';
            if (s.inCombat) headerHtml += ' &nbsp; <span style="color:#f7768e;font-weight:600;">IN COMBAT</span>';
            if (s.isDead) headerHtml += ' &nbsp; <span style="color:#f7768e;font-weight:600;">DEAD</span>';
            $header.html(headerHtml);
        }

        // Update sub-phase strip
        var $subphase = $('#botSubPhase');
        if ($subphase.length > 0) {
            var spHtml = '<i class="fa-solid fa-route" style="margin-right:4px;"></i>';
            spHtml += '<span style="color:var(--text-secondary);">' + esc(brain.activity || '') + '</span>';
            spHtml += ' &rarr; <span style="color:#7aa2f7;">' + esc(brain.subPhase || '') + '</span>';
            if (brain.activeQuestId) spHtml += ' <span style="color:#e0af68;">(Quest #' + brain.activeQuestId + ')</span>';
            if (brain.contextTag) spHtml += ' <span style="color:var(--text-muted);">' + esc(brain.contextTag) + '</span>';
            $subphase.html(spHtml);
        }

        // Update pending action
        var $pending = $('#botPending');
        if ($pending.length > 0) {
            if (brain.pendingAction) {
                $pending.html('<span style="color:#ff9e64;">' +
                    '<i class="fa-solid fa-rotate-left" style="margin-right:4px;"></i>Pending: return to ' +
                    esc(brain.pendingAction.returnTo) + ' (' + esc(brain.pendingAction.subPhase || '') + ')' +
                    (brain.pendingAction.questId ? ' quest #' + brain.pendingAction.questId : '') +
                    '</span>').show();
            } else {
                $pending.hide();
            }
        }

        // Update position
        var $pos = $('#botPosition');
        if ($pos.length > 0) {
            $pos.text('L' + (s.level || 0) + ' ' + (RACE_NAMES[s.race] || '?') + ' \u2014 Map ' + (s.mapId || 0) + ' (' + (s.x != null ? s.x : 0).toFixed(0) + ', ' + (s.y != null ? s.y : 0).toFixed(0) + ')');
        }
    }

    // Roster-level brain refresh: poll all bots every 10s for roster card accuracy
    function startRosterPoll() {
        if (rosterPollTimer) clearInterval(rosterPollTimer);
        rosterPollTimer = setInterval(function () {
            if (!connected) return;
            // ONE request for the fleet. This was a $.getJSON per bot every 10s — at fleet scale
            // that is thousands of requests per poll, which exhausts the browser's socket pool
            // (net::ERR_INSUFFICIENT_RESOURCES) and keeps the tab permanently saturated.
            $.getJSON('/Bots/BrainStates', function (all) {
                var list = (all && all.bots) || [];
                for (var i = 0; i < list.length; i++) {
                    var data = list[i];
                    if (!data || !data.guid) continue;
                    var existing = botBrains[data.guid] || {};
                    var prevDecision = existing.lastDecision;
                    botBrains[data.guid] = data;
                    if (prevDecision) botBrains[data.guid].lastDecision = prevDecision;
                    dirtyCards[data.guid] = true;   // coalesced repaint
                }
            });
        }, 10000);
    }

    // ===================== ENGINE TOGGLE =====================

    $('#engineToggle').on('click', function () {
        engineEnabled = !engineEnabled;
        $(this).toggleClass('active', engineEnabled);
        $(this).find('.bt-engine-label').text(engineEnabled ? 'Engine On' : 'Engine Off');
        $.post('/Bots/ToggleBrain', { enabled: engineEnabled });
    });

    // ===================== GROUPING MODE (Session 31) =====================

    $('#groupingMode').on('change', function () {
        var mode = parseInt($(this).val());
        $.ajax({
            url: '/Bots/SetGroupingMode',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ mode: mode }),
            success: function (data) {
                if (data.success) {
                    showToast('Grouping: ' + data.modeName);
                    refreshGroupingStatus();
                } else {
                    showToast('Error: ' + (data.error || 'unknown'), true);
                }
            }
        });
    });

    $('#autoFormGroups').on('click', function () {
        $.ajax({
            url: '/Bots/AutoFormGroups',
            type: 'POST',
            contentType: 'application/json',
            data: '{}',
            success: function (data) {
                if (data.success) {
                    showToast('Formed ' + data.groupsFormed + ' group(s)');
                    refreshGroupingStatus();
                }
            }
        });
    });

    function refreshGroupingStatus() {
        $.getJSON('/Bots/BrainStatus', function (data) {
            updateGroupingUI(data);
        });
    }

    // Deterministic per-group colour + roster badge, shared scheme with Map / Fleet (groupId is a
    // stable int -> fixed palette index, so a group is the same colour on every page and poll).
    var GROUP_COLORS = ['#7aa2f7', '#bb9af7', '#9ece6a', '#e0af68', '#f7768e', '#2ac3de', '#ff9e64', '#7dcfff', '#73daca', '#c0caf5', '#d7a65f', '#9d7cd8'];
    var groupOf = {}, leaderOf = {};   // guid -> groupId / leader (rebuilt from BrainStatus below)
    function groupColor(gid) { return gid > 0 ? GROUP_COLORS[(gid - 1) % GROUP_COLORS.length] : null; }
    function groupBadgeHtml(guid) {
        var gid = groupOf[guid]; if (!gid) return '';
        var c = groupColor(gid);
        return ' <span title="group ' + gid + (leaderOf[guid] ? ' (leader)' : '') + '" class="bt-group-badge" style="display:inline-block;padding:0 5px;border-radius:3px;font-size:10px;font-weight:700;color:' + c + ';background:' + c + '22;border:1px solid ' + c + '66;">' + (leaderOf[guid] ? '\u2605' : '') + 'G' + gid + '</span>';
    }

    function updateGroupingUI(data) {
        var groups = data.groups || [];
        // Rebuild the guid -> group lookup the roster cards read for their colour badge.
        groupOf = {}; leaderOf = {};
        groups.forEach(function (g) {
            (g.memberGuids || []).forEach(function (mg) { groupOf[mg] = g.groupId; if (mg === g.leaderGuid) leaderOf[mg] = true; });
        });
        var $list = $('#groupList');
        $('#groupCount').text(groups.length);
        if (groups.length === 0) {
            $list.html('<div style="color:var(--text-muted);font-size:12px;">No active groups</div>');
            return;
        }
        var html = '';
        groups.forEach(function (g) {
            var memberNames = g.memberGuids.map(function (guid) {
                var s = botStates[guid];
                var isLeader = guid === g.leaderGuid;
                var name = s ? s.name : ('Bot #' + guid);
                return '<span style="color:' + (isLeader ? '#e0af68' : '#c0caf5') + ';">'
                    + (isLeader ? '<i class="fa-solid fa-crown" style="font-size:10px;margin-right:2px;"></i>' : '')
                    + name + '</span>';
            }).join(', ');

            html += '<div class="bt-group-row" style="display:flex;align-items:center;gap:8px;padding:4px 0;border-bottom:1px solid var(--border);">'
                + '<span style="color:var(--text-muted);font-size:11px;min-width:24px;">#' + g.groupId + '</span>'
                + '<span style="flex:1;font-size:12px;">' + memberNames + '</span>'
                + '<button class="btn btn-sm" style="padding:1px 6px;font-size:10px;background:var(--danger);color:#fff;border:none;border-radius:3px;cursor:pointer;" '
                + 'onclick="disbandGroup(' + g.groupId + ')">Disband</button>'
                + '</div>';
        });
        $list.html(html);
    }

    // Global function for inline onclick
    window.disbandGroup = function (groupId) {
        $.ajax({
            url: '/Bots/DisbandGroup',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ groupId: groupId }),
            success: function (data) {
                if (data.success) {
                    showToast('Group #' + groupId + ' disbanded');
                    refreshGroupingStatus();
                }
            }
        });
    };

    function showToast(msg, isError) {
        var $t = $('<div class="bt-toast"></div>').text(msg == null ? '' : String(msg)).css({
            position: 'fixed', bottom: '20px', right: '20px', padding: '8px 16px',
            background: 'var(--bg-card)', color: 'var(--text-primary)',
            border: '1px solid var(--border-light)',
            borderLeft: '4px solid ' + (isError ? 'var(--status-error)' : 'var(--status-online)'),
            borderRadius: '6px', fontWeight: 600, fontSize: '13px', zIndex: 9999,
            boxShadow: 'var(--shadow-md)', opacity: 0
        });
        $('body').append($t);
        $t.animate({ opacity: 1 }, 200).delay(2500).animate({ opacity: 0 }, 300, function () { $(this).remove(); });
    }

    // ===================== ROSTER SELECTION =====================

    $(document).on('click', '.bt-roster-card', function () {
        var guid = parseInt($(this).data('guid'));
        selectedGuid = guid;
        $('.bt-roster-card').removeClass('selected');
        $(this).addClass('selected');

        // Start polling for this bot's brain state
        startBrainPoll();

        if (!botBrains[guid]) {
            $.getJSON('/Bots/BrainState/' + guid, function (data) {
                if (data && data.guid) {
                    botBrains[guid] = data;
                }
                renderDetail();
            }).fail(function () {
                renderDetail();
            });
        } else {
            renderDetail();
        }
    });

    // ===================== SPELLBOOK CONTROLS =====================
    // Filtering is client-side over the already fetched projection: the read model is
    // one bot's learned set, so re-querying on every keystroke would buy nothing.

    function repaintSpellbook() {
        var cached = spellbookCache[selectedGuid];
        if (cached && detailTab === 'spellbook') renderSpellbookData(cached.data);
    }

    $(document).on('input', '#btSpellSearch', function () {
        if (!selectedGuid) return;
        var caret = this.selectionStart;
        spellbookState(selectedGuid).q = this.value;
        repaintSpellbook();
        // The tab body is rebuilt wholesale, so put the operator back where they were.
        var field = document.getElementById('btSpellSearch');
        if (field) { field.focus(); try { field.setSelectionRange(caret, caret); } catch (e) { } }
    });

    $(document).on('change', '.bt-spell-toggle', function () {
        if (!selectedGuid) return;
        var key = $(this).data('sbtoggle');
        if (!key) return;
        spellbookState(selectedGuid)[key] = this.checked;
        repaintSpellbook();
    });

    $(document).on('click', '.bt-spell-group > header', function () {
        if (!selectedGuid) return;
        var key = String($(this).data('sbgroup'));
        var collapsed = spellbookState(selectedGuid).collapsed;
        if (collapsed[key]) delete collapsed[key]; else collapsed[key] = true;
        repaintSpellbook();
    });

    // Authoring a rotation instruction starts with its spell id, so make the id the
    // one thing on the row that is trivially liftable.
    $(document).on('click', '.bt-spell-id', function (e) {
        e.stopPropagation();
        var id = String($(this).data('sbcopy') || '');
        if (!id) return;
        var done = function () { showToast('Copied spell id ' + id); };
        if (navigator.clipboard && navigator.clipboard.writeText) {
            navigator.clipboard.writeText(id).then(done, function () { showToast('Could not copy ' + id, true); });
            return;
        }
        var scratch = document.createElement('textarea');
        scratch.value = id;
        scratch.setAttribute('readonly', '');
        scratch.style.position = 'fixed';
        scratch.style.opacity = '0';
        document.body.appendChild(scratch);
        scratch.select();
        try { document.execCommand('copy'); done(); }
        catch (ex) { showToast('Could not copy ' + id, true); }
        document.body.removeChild(scratch);
    });

    // Detail-panel tab switching (Overview | Talents & Rotation | Spellbook | Live)
    $(document).on('click', '.bt-detail-tab', function () {
        var t = $(this).data('dtab');
        if (t === detailTab) return;
        detailTab = t;
        $('.bt-detail-tab').removeClass('active');
        $(this).addClass('active');
        renderActiveDetailTab();
        ensureLivePoll();
    });

    // ===================== COMMAND BAR =====================
    // Gone from the page. Everything it did (and Move To / Say / quests / spells / targeting)
    // now lives in the per-bot control suite: the modal's Control tab, opened by the sliders
    // button on any roster card, the Control button on the detail panel, or "Fleet control"
    // in the header for a broadcast. See the CONTROL SUITE section near the bottom of this file.

    // ===================== HELPERS =====================

    function formatGold(copper) {
        if (!copper || copper <= 0) return '0c';
        var g = Math.floor(copper / 10000);
        var s = Math.floor((copper % 10000) / 100);
        var c = copper % 100;
        var parts = [];
        if (g > 0) parts.push(g + 'g');
        if (s > 0) parts.push(s + 's');
        if (c > 0 || parts.length === 0) parts.push(c + 'c');
        return parts.join(' ');
    }

    function esc(s) {
        if (s === null || s === undefined) return '';
        var d = document.createElement('div');
        d.textContent = String(s);
        return d.innerHTML;
    }

    function escAttr(s) {
        return esc(s).replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function capitalize(s) {
        return s.charAt(0).toUpperCase() + s.slice(1);
    }

    // ===================== BOT DETAIL MODAL =====================

    var QUEST_STATUS_NAMES = { 0: 'None', 1: 'In Progress', 3: 'Complete', 6: 'Failed' };
    var QUEST_STATUS_ICONS = { 0: 'fa-circle-xmark', 1: 'fa-spinner', 3: 'fa-circle-check', 6: 'fa-skull' };
    var QUEST_STATUS_COLORS = { 0: '#5f6b7a', 1: '#7aa2f7', 3: '#e0af68', 6: '#f7768e' };
    // ZONE_NAMES / MAP_NAMES now live in the CONSTANTS block at the top (shared with the cockpit).
    var questStatusCache = {};

    // Inject modal styles
    $('<style>').text(
        '.bm-overlay { position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:5000;display:none;align-items:center;justify-content:center; }' +
        '.bm-overlay.active { display:flex; }' +
        '.bm-modal { background:var(--bg-card, #1a1b26);border:1px solid var(--border-light, #414868);border-radius:10px;width:90vw;max-width:1100px;height:88vh;display:flex;flex-direction:column;box-shadow:0 16px 48px rgba(0,0,0,0.5);overflow:hidden; }' +
        '.bm-header { display:flex;align-items:center;justify-content:space-between;padding:14px 20px;border-bottom:1px solid var(--border-light, #414868);flex-shrink:0; }' +
        '.bm-header-title { font-size:15px;font-weight:700;color:var(--text-primary, #c0caf5); }' +
        '.bm-close { background:none;border:none;color:var(--text-muted, #5f6b7a);font-size:18px;cursor:pointer;padding:4px 8px; }' +
        '.bm-close:hover { color:var(--text-primary, #c0caf5); }' +
        '.bm-tabs { display:flex;gap:0;border-bottom:1px solid var(--border-light, #414868);flex-shrink:0;padding:0 20px; }' +
        '.bm-tab { padding:8px 16px;font-size:12px;font-weight:600;color:var(--text-muted, #5f6b7a);cursor:pointer;border-bottom:2px solid transparent;text-transform:uppercase;letter-spacing:0.5px; }' +
        '.bm-tab:hover { color:var(--text-secondary, #a9b1d6); }' +
        '.bm-tab.active { color:var(--accent, #7aa2f7);border-bottom-color:var(--accent, #7aa2f7); }' +
        '.bm-tab-danger { color:#f7768e; }' +
        '.bm-tab-danger:hover { color:#ff8fa3; }' +
        '.bm-tab-danger.active { color:#f7768e;border-bottom-color:#f7768e; }' +
        '.bm-delete-btn { margin-left:auto;align-self:center;padding:5px 14px;font-size:12px;font-weight:600;color:#f7768e;background:rgba(247,118,142,0.1);border:1px solid rgba(247,118,142,0.4);border-radius:4px;cursor:pointer;text-transform:uppercase;letter-spacing:0.5px; }' +
        '.bm-delete-btn:hover { color:#fff;background:#f7768e;border-color:#f7768e; }' +
        '.bm-body { flex:1;overflow-y:auto;padding:16px 20px; }' +
        '.bdc-overlay { display:none;position:fixed;inset:0;background:rgba(0,0,0,0.55);z-index:10001;align-items:center;justify-content:center; }' +
        '.bdc-overlay.active { display:flex; }' +
        '.bdc-box { background:var(--bg-secondary, #1a1b26);border:1px solid var(--border-light, #414868);border-radius:8px;padding:20px;width:340px;box-shadow:0 8px 24px rgba(0,0,0,0.4); }' +
        '.bdc-msg { color:var(--text-primary, #c0caf5);font-size:14px;margin-bottom:18px;line-height:1.4; }' +
        '.bdc-actions { display:flex;justify-content:flex-end;gap:8px; }' +
        '.bdc-cancel { padding:6px 14px;font-size:12px;font-weight:600;color:var(--text-secondary, #a9b1d6);background:transparent;border:1px solid var(--border-light, #414868);border-radius:4px;cursor:pointer; }' +
        '.bdc-cancel:hover { color:var(--text-primary, #c0caf5);border-color:var(--text-secondary, #a9b1d6); }' +
        '.bdc-confirm { padding:6px 14px;font-size:12px;font-weight:600;color:#fff;background:#f7768e;border:1px solid #f7768e;border-radius:4px;cursor:pointer; }' +
        '.bdc-confirm:hover { background:#db5a75;border-color:#db5a75; }' +

        '.bq-zone-group { margin-bottom:16px; }' +
        '.bq-zone-header { font-size:13px;font-weight:700;color:var(--text-secondary, #a9b1d6);margin-bottom:8px;display:flex;align-items:center;gap:8px; }' +
        '.bq-zone-badge { font-size:10px;background:var(--bg-card-alt, #24283b);padding:2px 8px;border-radius:10px;color:var(--text-muted); }' +
        '.bq-quest-row { display:flex;align-items:center;gap:10px;padding:6px 10px;border-radius:5px;cursor:pointer;font-size:12px;transition:background 0.15s; }' +
        '.bq-quest-row:hover { background:var(--bg-card-alt, #24283b); }' +
        '.bq-quest-row.expanded { background:var(--bg-card-alt, #24283b);border-radius:5px 5px 0 0; }' +
        '.bq-status-icon { width:16px;text-align:center;flex-shrink:0; }' +
        '.bq-quest-title { flex:1;font-weight:500; }' +
        '.bq-quest-level { color:var(--text-muted);font-size:11px;width:30px;text-align:right;flex-shrink:0; }' +
        '.bq-quest-progress { font-size:11px;width:80px;text-align:right;flex-shrink:0; }' +
        '.bq-rewarded { color:#9ece6a; }' +
        '.bq-detail { background:var(--bg-card-alt, #24283b);padding:10px 14px 10px 36px;margin-bottom:4px;border-radius:0 0 5px 5px;font-size:11px;line-height:1.7;display:none;color:var(--text-muted); }' +
        '.bq-detail.visible { display:block; }' +
        '.bq-detail-label { color:var(--text-secondary, #a9b1d6);font-weight:600;margin-right:4px; }' +
        '.bq-obj-row { display:flex;align-items:center;gap:6px;margin-top:2px; }' +
        '.bq-obj-bar { height:4px;background:var(--border-light, #414868);border-radius:2px;flex:1;max-width:100px;overflow:hidden; }' +
        '.bq-obj-fill { height:100%;border-radius:2px; }' +
        '.bq-chain-tag { font-size:10px;padding:1px 6px;border-radius:3px;border:1px solid var(--border-light);color:var(--text-muted); }' +
        '.bq-excl-tag { font-size:10px;padding:1px 6px;border-radius:3px;background:#f7768e22;border:1px solid #f7768e44;color:#f7768e; }' +
        '.bq-loading { text-align:center;padding:40px;color:var(--text-muted); }'
    ).appendTo('head');

    // Inject modal DOM
    $('body').append(
        '<div class="bm-overlay" id="botModal">' +
        '<div class="bm-modal">' +
        '<div class="bm-header">' +
        '<span class="bm-header-title" id="bmTitle">Bot Details</span>' +
        '<button class="bm-close" id="bmClose"><i class="fa-solid fa-xmark"></i></button>' +
        '</div>' +
        '<div class="bm-tabs">' +
        '<div class="bm-tab active" data-tab="control"><i class="fa-solid fa-sliders" style="margin-right:5px;"></i>Control</div>' +
        '<div class="bm-tab bm-tab-danger" data-tab="danger" style="display:none;"><i class="fa-solid fa-triangle-exclamation" style="margin-right:5px;"></i>Danger Zone</div>' +
        '<div class="bm-tab" data-tab="quests"><i class="fa-solid fa-scroll" style="margin-right:5px;"></i>Quests</div>' +
        '<div class="bm-tab" data-tab="gear"><i class="fa-solid fa-shield-halved" style="margin-right:5px;"></i>Gear</div>' +
        '<div class="bm-tab" data-tab="brain"><i class="fa-solid fa-brain" style="margin-right:5px;"></i>Brain</div>' +
        '<button type="button" class="bm-delete-btn" id="bmDeleteBtn"><i class="fa-solid fa-trash" style="margin-right:5px;"></i>Delete</button>' +
        '</div>' +
        '<div class="bm-body" id="bmBody"></div>' +
        '</div>' +
        '</div>'
    );

    // Inject delete-confirmation dialog
    $('body').append(
        '<div class="bdc-overlay" id="bdcOverlay">' +
        '<div class="bdc-box">' +
        '<div class="bdc-msg" id="bdcMsg"></div>' +
        '<div class="bdc-actions">' +
        '<button type="button" class="bdc-cancel" id="bdcCancel">Cancel</button>' +
        '<button type="button" class="bdc-confirm" id="bdcConfirm">Delete</button>' +
        '</div>' +
        '</div>' +
        '</div>'
    );

    // Modal close
    $('#bmClose').on('click', function () { $('#botModal').removeClass('active'); });
    $('#botModal').on('click', function (e) {
        if (e.target === this) $(this).removeClass('active');
    });
    $(document).on('keydown', function (e) {
        if (e.key === 'Escape') $('#botModal').removeClass('active');
    });

    // Tab switching
    $(document).on('click', '.bm-tab', function () {
        var tab = $(this).data('tab');
        $('.bm-tab').removeClass('active');
        $(this).addClass('active');
        loadModalTab(tab);
    });

    // ===================== BOT DELETE (single + mass, confirmation) =====================

    // Drops a deleted bot from every client-side cache/DOM row, mirroring the
    // BotDisconnected 30s auto-remove cleanup above.
    function removeDeletedBot(guid) {
        delete botStates[guid];
        delete botBrains[guid];
        delete decisionLog[guid];
        delete inventoryCache[guid];
        $('#roster-' + guid).remove();
        rosterDirty = true;

        if (selectedGuid === guid) {
            selectedGuid = null;
            $('#detailEmpty').show();
            $('#detailPanel').empty();
            stopBrainPoll();
            stopLivePoll();
        }
    }

    $(document).on('click', '#bmDeleteBtn', function () {
        var guid = parseInt($('#botModal').data('guid'), 10) || 0;
        if (guid <= 0) return;
        var s = botStates[guid];
        var name = s ? s.name : ('bot ' + guid);
        $('#bdcMsg').text('Are you sure you want to delete ' + name + '?');
        $('#bdcOverlay').data({ mode: 'single', guid: guid }).addClass('active');
    });

    $(document).on('click', '#bmMassDeleteBtn', function () {
        var total = $('#bmBody').data('rosterTotal') || 0;
        if (total <= 0) return;
        $('#bdcMsg').text('Are you sure you want to delete ' + total + ' bot' + (total === 1 ? '' : 's') + '?');
        $('#bdcOverlay').data({ mode: 'mass' }).addClass('active');
    });

    $(document).on('click', '#bdcCancel', function () {
        $('#bdcOverlay').removeClass('active');
    });

    $('#bdcOverlay').on('click', function (e) {
        if (e.target === this) $(this).removeClass('active');
    });

    $(document).on('click', '#bdcConfirm', function () {
        var mode = $('#bdcOverlay').data('mode');

        if (mode === 'single') {
            var guid = parseInt($('#bdcOverlay').data('guid'), 10) || 0;
            if (guid <= 0) return;
            $.ajax({ url: '/Bots/DeleteBot', type: 'POST', contentType: 'application/json', data: JSON.stringify({ guid: guid }) })
                .done(function (r) {
                    if (r && r.success) {
                        $('#bdcOverlay').removeClass('active');
                        $('#botModal').removeClass('active');
                        removeDeletedBot(guid);
                        showToast('Bot deleted');
                    } else {
                        $('#bdcOverlay').removeClass('active');
                        showToast((r && r.error) || 'Delete failed', true);
                    }
                })
                .fail(function (x) {
                    $('#bdcOverlay').removeClass('active');
                    showToast('Delete failed (HTTP ' + x.status + ')', true);
                });
            return;
        }

        if (mode === 'mass') {
            $.ajax({ url: '/Bots/DeleteAllBots', type: 'POST' })
                .done(function (r) {
                    $('#bdcOverlay').removeClass('active');
                    if (r && r.success) {
                        $('#botModal').removeClass('active');
                        Object.keys(botStates).forEach(function (g) { removeDeletedBot(parseInt(g, 10)); });
                        showToast(r.deleted + ' bot' + (r.deleted === 1 ? '' : 's') + ' deleted');
                    } else {
                        showToast((r && r.error) || 'Mass delete failed', true);
                    }
                })
                .fail(function (x) {
                    $('#bdcOverlay').removeClass('active');
                    showToast('Mass delete failed (HTTP ' + x.status + ')', true);
                });
        }
    });

    // ===================== BOT REPORT (quantized, on-the-spot) =====================
    // Pulls /Bots/BotReport for the watched bot and renders a bounded, counts-only
    // snapshot of what that one bot's buffered log shows — the cocktail, on demand.

    $('body').append(
        '<div class="br-overlay" id="botReportModal">' +
        '<div class="br-modal">' +
        '<div class="br-header"><span id="brTitle"><i class="fa-solid fa-bolt"></i> Bot report</span>' +
        '<button class="br-close" id="brClose"><i class="fa-solid fa-xmark"></i></button></div>' +
        '<div class="br-body" id="brBody"></div>' +
        '</div></div>'
    );
    $('#brClose').on('click', function () { $('#botReportModal').removeClass('active'); });
    $('#botReportModal').on('click', function (e) { if (e.target === this) $(this).removeClass('active'); });
    $(document).on('keydown', function (e) { if (e.key === 'Escape') $('#botReportModal').removeClass('active'); });

    // ===================== ADD BOTS MODAL (race-aware .bot addai spawner) =====================
    // Pick counts per race x class — type a number straight into a cell, use the +/- steppers, or
    // set "N each" for one race or for everything. Posts { spawns:[{race,cls,count}] } to
    // /Bots/AddBots, which draws unique names from wwwroot/data and hands the batch to
    // BotSpawnService: the `.bot addai <class> <race> <name>` RA loop runs in the background there
    // and streams SpawnProgress over /hubs/botbridge (polled from /Bots/AddBotsStatus as a
    // fallback), so the request returns at once and a batch can be watched, closed over, or
    // cancelled. The C++ command spawns each bot at that race's real starting zone.
    //
    // Limits come from /Bots/AddBotsLimits so the modal and the server can't drift: the configured
    // per-batch ceiling (BotSpawn:MaxPerRequest), the unused-name pool (the real lifetime ceiling),
    // bots online, and mangosd's PlayerLimit (bot sessions count against it).
    var AB_RACES = [
        { key: 'human', name: 'Human', side: 'alliance' },
        { key: 'dwarf', name: 'Dwarf', side: 'alliance' },
        { key: 'nightelf', name: 'Night Elf', side: 'alliance' },
        { key: 'gnome', name: 'Gnome', side: 'alliance' },
        { key: 'orc', name: 'Orc', side: 'horde' },
        { key: 'undead', name: 'Undead', side: 'horde' },
        { key: 'tauren', name: 'Tauren', side: 'horde' },
        { key: 'troll', name: 'Troll', side: 'horde' }
    ];
    var AB_RACE_CLASSES = {
        human: ['warrior', 'paladin', 'rogue', 'priest', 'mage', 'warlock'],
        dwarf: ['warrior', 'paladin', 'hunter', 'rogue', 'priest'],
        nightelf: ['warrior', 'hunter', 'rogue', 'priest', 'druid'],
        gnome: ['warrior', 'rogue', 'mage', 'warlock'],
        orc: ['warrior', 'hunter', 'rogue', 'shaman', 'warlock'],
        undead: ['warrior', 'rogue', 'priest', 'mage', 'warlock'],
        tauren: ['warrior', 'hunter', 'shaman', 'druid'],
        troll: ['warrior', 'hunter', 'rogue', 'priest', 'mage', 'shaman']
    };
    // Class color + iconography (vanilla WoW class colors; shaman added for Horde).
    var ADD_BOT_META = {
        warrior: { color: '#C79C6E', icon: 'fa-shield-halved' },
        paladin: { color: '#F58CBA', icon: 'fa-hammer' },
        hunter: { color: '#ABD473', icon: 'fa-crosshairs' },
        rogue: { color: '#FFF569', icon: 'fa-user-ninja' },
        priest: { color: '#FFFFFF', icon: 'fa-hands-praying' },
        mage: { color: '#69CCF0', icon: 'fa-hat-wizard' },
        warlock: { color: '#9482C9', icon: 'fa-skull' },
        druid: { color: '#FF7D0A', icon: 'fa-paw' },
        shaman: { color: '#0070DE', icon: 'fa-bolt' }
    };
    // abCounts[race][cls] = n
    var abCounts = {};
    AB_RACES.forEach(function (r) { abCounts[r.key] = {}; AB_RACE_CLASSES[r.key].forEach(function (c) { abCounts[r.key][c] = 0; }); });
    var abLimits = { maxPerRequest: 0, namesAvailable: null, namesError: null, botsOnline: 0, playerLimit: null };
    var abJob = null;            // latest SpawnProgress snapshot (running or finished)
    var abToastedJobId = null;   // batch whose completion toast has already been shown
    var abPollTimer = null;      // 1s /Bots/AddBotsStatus poll while a batch runs (SignalR fallback)

    $('head').append(
        '<style>' +
        '.ab-overlay{position:fixed;inset:0;background:rgba(10,11,20,0.62);backdrop-filter:blur(2px);display:none;align-items:center;justify-content:center;z-index:1000;}' +
        '.ab-overlay.active{display:flex;animation:abFade .14s ease;}' +
        '@keyframes abFade{from{opacity:0}to{opacity:1}}' +
        '@keyframes abPop{from{transform:translateY(8px) scale(.985);opacity:.5}to{transform:none;opacity:1}}' +
        '.ab-modal{background:var(--bg-card,#1a1b26);border:1px solid var(--border-light,#414868);border-radius:14px;width:92vw;max-width:780px;max-height:88vh;display:flex;flex-direction:column;box-shadow:0 24px 64px rgba(0,0,0,0.6);overflow:hidden;animation:abPop .16s cubic-bezier(.2,.8,.2,1);}' +
        '.ab-header{display:flex;align-items:center;justify-content:space-between;padding:16px 18px;border-bottom:1px solid var(--border-light,#414868);background:linear-gradient(135deg,rgba(122,162,247,0.14),rgba(187,154,247,0.05));}' +
        '.ab-hleft{display:flex;align-items:center;}' +
        '.ab-hicon{width:34px;height:34px;border-radius:9px;display:flex;align-items:center;justify-content:center;background:rgba(122,162,247,0.16);color:var(--accent,#7aa2f7);font-size:15px;margin-right:11px;}' +
        '.ab-htxt{display:flex;flex-direction:column;gap:2px;}' +
        '.ab-htxt b{font-size:15px;font-weight:700;letter-spacing:.2px;}' +
        '.ab-htxt span{font-size:11px;color:var(--text-muted,#787c99);}' +
        '.ab-close{background:none;border:none;color:var(--text-muted,#787c99);cursor:pointer;font-size:17px;line-height:1;padding:4px 6px;border-radius:6px;transition:all .12s;}' +
        '.ab-close:hover{color:var(--text-secondary,#c0caf5);background:rgba(255,255,255,0.06);}' +
        '.ab-body{padding:14px 18px;overflow-y:auto;}' +
        '.ab-meta{display:flex;flex-wrap:wrap;gap:4px 16px;font-size:11px;color:var(--text-muted,#787c99);margin:0 0 12px 2px;}' +
        '.ab-meta b{color:var(--text-secondary,#c0caf5);font-weight:600;font-variant-numeric:tabular-nums;}' +
        '.ab-meta .ab-limit{opacity:.8;}' +
        '.ab-meta .ab-bad{color:#e0af68;}' +
        '.ab-warn{display:none;font-size:11px;line-height:1.4;color:#e0af68;background:rgba(224,175,104,0.08);border:1px solid rgba(224,175,104,0.35);border-radius:8px;padding:7px 10px;margin:0 0 12px;}' +
        '.ab-warn.show{display:block;}' +
        '.ab-race{margin-bottom:15px;}' +
        '.ab-race:last-child{margin-bottom:2px;}' +
        '.ab-rhead{display:flex;align-items:center;gap:8px;margin:0 0 8px 2px;font-size:11px;font-weight:700;letter-spacing:.4px;text-transform:uppercase;color:var(--text-secondary,#c0caf5);}' +
        '.ab-rdot{width:8px;height:8px;border-radius:50%;flex:none;}' +
        '.ab-rdot.alliance{background:#4a86ff;}' +
        '.ab-rdot.horde{background:#e0503a;}' +
        '.ab-rand{border:1px solid var(--border-light,#414868);border-radius:10px;background:var(--bg-card-alt,#24283b);padding:10px 12px;margin:0 0 13px;}' +
        '.ab-rand-row{display:flex;align-items:center;gap:11px;flex-wrap:wrap;}' +
        '.ab-rand-t{display:inline-flex;align-items:center;gap:7px;font-size:11px;font-weight:700;letter-spacing:.4px;text-transform:uppercase;color:var(--text-secondary,#c0caf5);}' +
        '.ab-rand-t i{color:var(--accent,#7aa2f7);font-size:12px;}' +
        '.ab-rand label{display:inline-flex;align-items:center;gap:5px;font-size:11px;color:var(--text-muted,#787c99);}' +
        '.ab-rand input{width:62px;height:26px;padding:0 6px;text-align:center;font-size:12px;font-weight:700;font-variant-numeric:tabular-nums;border:1px solid var(--border-light,#414868);border-radius:7px;background:var(--bg-card,#1a1b26);color:var(--text-secondary,#c0caf5);outline:none;-moz-appearance:textfield;appearance:textfield;transition:border-color .12s;}' +
        '.ab-rand input::-webkit-outer-spin-button,.ab-rand input::-webkit-inner-spin-button{-webkit-appearance:none;margin:0;}' +
        '.ab-rand input:focus{border-color:var(--accent,#7aa2f7);}' +
        '.ab-rand input.ab-fa:focus{border-color:#4a86ff;}' +
        '.ab-rand input.ab-fh:focus{border-color:#e0503a;}' +
        '.ab-rand-go{margin-left:auto;font-size:11.5px;font-weight:600;padding:6px 13px;cursor:pointer;border-radius:7px;border:1px solid var(--accent,#7aa2f7);background:rgba(122,162,247,0.14);color:var(--text-secondary,#c0caf5);display:inline-flex;align-items:center;gap:6px;transition:all .12s;white-space:nowrap;}' +
        '.ab-rand-go:hover{background:rgba(122,162,247,0.26);color:#fff;}' +
        '.ab-split{display:flex;height:6px;border-radius:4px;overflow:hidden;margin-top:10px;background:rgba(255,255,255,0.06);}' +
        '.ab-split i{display:block;height:100%;transition:width .18s;}' +
        '.ab-split .sa{background:#4a86ff;}' +
        '.ab-split .sh{background:#e0503a;}' +
        '.ab-split-txt{display:flex;justify-content:space-between;font-size:10.5px;color:var(--text-muted,#787c99);margin-top:5px;font-variant-numeric:tabular-nums;}' +
        '.ab-split-txt b{font-weight:700;}' +
        '.ab-split-txt .a b{color:#6f9dff;}' +
        '.ab-split-txt .h b{color:#ec6a52;}' +
        '.ab-quick{display:flex;align-items:center;gap:5px;flex-wrap:wrap;margin-top:9px;}' +
        '.ab-qlabel{font-size:10px;font-weight:700;letter-spacing:.4px;text-transform:uppercase;color:var(--text-muted,#787c99);}' +
        '.ab-q{font-size:11px;height:22px;padding:0 9px;cursor:pointer;border-radius:6px;border:1px solid var(--border-light,#414868);background:transparent;color:var(--text-muted,#787c99);font-variant-numeric:tabular-nums;transition:all .12s;}' +
        '.ab-q:hover{color:var(--text-secondary,#c0caf5);border-color:var(--accent,#7aa2f7);}' +
        '.ab-q.on{background:rgba(122,162,247,0.16);border-color:var(--accent,#7aa2f7);color:var(--text-secondary,#c0caf5);font-weight:600;}' +
        '.ab-q.sa:hover,.ab-q.sa.on{border-color:#4a86ff;color:#8fb4ff;}' +
        '.ab-q.sh:hover,.ab-q.sh.on{border-color:#e0503a;color:#f08a76;}' +
        '.ab-q:disabled{opacity:.4;cursor:default;border-color:var(--border-light,#414868);color:var(--text-muted,#787c99);}' +
        '.ab-qsep{width:1px;height:15px;background:var(--border-light,#414868);margin:0 4px;}' +
        '.ab-rtot{margin-left:auto;font-size:11px;font-weight:600;color:var(--text-muted,#787c99);font-variant-numeric:tabular-nums;}' +
        '.ab-grid{display:grid;grid-template-columns:1fr 1fr;gap:9px;}' +
        '.ab-card{position:relative;display:flex;align-items:center;justify-content:space-between;gap:8px;padding:9px 11px 9px 14px;border-radius:10px;border:1px solid var(--border-light,#414868);background:var(--bg-card-alt,#24283b);overflow:hidden;transition:opacity .14s,box-shadow .14s,border-color .14s;}' +
        '.ab-card::before{content:"";position:absolute;left:0;top:0;bottom:0;width:3px;background:var(--cc,#7aa2f7);opacity:.45;transition:opacity .14s;}' +
        '.ab-card.on{box-shadow:0 2px 14px rgba(0,0,0,0.28);}' +
        '.ab-card.on::before{opacity:1;}' +
        '.ab-card.off{opacity:.48;}' +
        '.ab-card.off:focus-within{opacity:1;}' +
        '.ab-cname{display:flex;align-items:center;gap:8px;min-width:0;}' +
        '.ab-cname i{font-size:13px;width:17px;text-align:center;}' +
        '.ab-cname b{font-size:12.5px;font-weight:600;text-transform:capitalize;letter-spacing:.2px;}' +
        '.ab-step{display:inline-flex;align-items:center;gap:5px;flex:none;}' +
        '.ab-step button{width:23px;height:23px;border-radius:6px;border:1px solid var(--border-light,#414868);background:var(--bg-card,#1a1b26);color:var(--text-secondary,#c0caf5);cursor:pointer;font-size:14px;line-height:1;display:flex;align-items:center;justify-content:center;transition:all .12s;}' +
        '.ab-step button:hover{border-color:var(--cc,#7aa2f7);color:#fff;}' +
        '.ab-step input.ab-cnt{width:50px;height:23px;padding:0 4px;text-align:center;font-weight:700;font-size:13px;font-variant-numeric:tabular-nums;border:1px solid var(--border-light,#414868);border-radius:6px;background:var(--bg-card,#1a1b26);color:var(--text-secondary,#c0caf5);outline:none;-moz-appearance:textfield;appearance:textfield;transition:border-color .12s;}' +
        '.ab-step input.ab-cnt::-webkit-outer-spin-button,.ab-step input.ab-cnt::-webkit-inner-spin-button{-webkit-appearance:none;margin:0;}' +
        '.ab-step input.ab-cnt:focus{border-color:var(--cc,#7aa2f7);}' +
        '.ab-prog{display:none;padding:12px 18px;border-top:1px solid var(--border-light,#414868);background:rgba(0,0,0,0.12);}' +
        '.ab-prog.show{display:block;}' +
        '.ab-prog-h{display:flex;align-items:center;justify-content:space-between;gap:10px;font-size:12px;margin-bottom:8px;}' +
        '.ab-prog-h b{font-weight:700;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;}' +
        '.ab-prog-h span{color:var(--text-muted,#787c99);font-variant-numeric:tabular-nums;white-space:nowrap;}' +
        '.ab-bar{height:8px;border-radius:5px;background:rgba(255,255,255,0.07);overflow:hidden;}' +
        '.ab-bar i{display:block;height:100%;width:0;border-radius:5px;background:linear-gradient(90deg,var(--accent,#7aa2f7),#bb9af7);transition:width .25s;}' +
        '.ab-bar.done i{background:#9ece6a;}' +
        '.ab-bar.cancelled i,.ab-bar.failed i{background:#e0af68;}' +
        '.ab-prog-f{display:flex;align-items:center;gap:10px;margin-top:9px;font-size:11px;color:var(--text-muted,#787c99);}' +
        '.ab-prog-f span{flex:1;min-width:0;line-height:1.4;}' +
        '.ab-foot{display:flex;align-items:center;justify-content:space-between;padding:14px 18px;border-top:1px solid var(--border-light,#414868);gap:10px;background:rgba(0,0,0,0.12);}' +
        '.ab-presets{display:flex;align-items:center;gap:6px;flex-wrap:wrap;}' +
        '.ab-preset{font-size:11px;padding:5px 10px;cursor:pointer;background:transparent;border:1px solid var(--border-light,#414868);border-radius:7px;color:var(--text-muted,#787c99);transition:all .12s;}' +
        '.ab-preset:hover{color:var(--text-secondary,#c0caf5);border-color:var(--accent,#7aa2f7);}' +
        '.ab-preset:disabled{opacity:.5;cursor:default;}' +
        '.ab-spawn{font-size:13px;font-weight:600;padding:9px 18px;border:none;border-radius:9px;cursor:pointer;color:#fff;display:inline-flex;align-items:center;gap:7px;background:linear-gradient(135deg,var(--accent,#7aa2f7),#bb9af7);box-shadow:0 4px 14px rgba(122,162,247,0.35);transition:transform .12s,box-shadow .12s,filter .12s;white-space:nowrap;}' +
        '.ab-spawn:hover{transform:translateY(-1px);box-shadow:0 6px 20px rgba(122,162,247,0.45);}' +
        '.ab-spawn:active{transform:translateY(0);}' +
        '.ab-spawn:disabled{filter:grayscale(.4) brightness(.78);cursor:default;transform:none;box-shadow:none;}' +
        '.ab-tot{font-weight:700;font-variant-numeric:tabular-nums;}' +
        '.ab-tot.over{color:#f7768e;}' +
        '.ab-pill{display:none;align-items:center;gap:6px;font-size:11px;height:26px;padding:0 10px;border-radius:999px;border:1px solid var(--border-light,#414868);color:var(--text-secondary,#c0caf5);cursor:pointer;white-space:nowrap;font-variant-numeric:tabular-nums;}' +
        '.ab-pill.show{display:inline-flex;}' +
        '.ab-pill i{color:var(--accent,#7aa2f7);}' +
        '</style>'
    );

    $('body').append(
        '<div class="ab-overlay" id="addBotsModal">' +
        '<div class="ab-modal">' +
        '<div class="ab-header">' +
        '<div class="ab-hleft"><div class="ab-hicon"><i class="fa-solid fa-user-plus"></i></div>' +
        '<div class="ab-htxt"><b>Add Bots</b><span>Randomize a population by faction, or type counts per class — each spawns at its own start with a real name</span></div></div>' +
        '<button class="ab-close" id="abClose"><i class="fa-solid fa-xmark"></i></button></div>' +
        '<div class="ab-body">' +
        '<div class="ab-meta" id="abMeta"></div>' +
        '<div class="ab-rand">' +
        '<div class="ab-rand-row">' +
        '<span class="ab-rand-t"><i class="fa-solid fa-dice"></i> Randomize</span>' +
        '<label>Total <input type="number" id="abRandTotal" min="0" inputmode="numeric" value="100" title="How many bots to spread across every race and class" /></label>' +
        '<label>Alliance <input type="number" class="ab-fa" id="abRandA" min="0" max="100" inputmode="numeric" value="50" />%</label>' +
        '<label>Horde <input type="number" class="ab-fh" id="abRandH" min="0" max="100" inputmode="numeric" value="50" />%</label>' +
        '<button class="ab-rand-go" id="abRandGo"><i class="fa-solid fa-shuffle"></i> Fill</button>' +
        '</div>' +
        '<div class="ab-quick">' +
        '<span class="ab-qlabel">Total</span>' +
        '<button class="ab-q" data-ab-qty="25">25</button>' +
        '<button class="ab-q" data-ab-qty="50">50</button>' +
        '<button class="ab-q" data-ab-qty="100">100</button>' +
        '<button class="ab-q" data-ab-qty="250">250</button>' +
        '<button class="ab-q" data-ab-qty="max" id="abQtyMax">Max</button>' +
        '<span class="ab-qsep"></span>' +
        '<span class="ab-qlabel">Split</span>' +
        '<button class="ab-q" data-ab-split="50">50 / 50</button>' +
        '<button class="ab-q sa" data-ab-split="100">All Alliance</button>' +
        '<button class="ab-q sh" data-ab-split="0">All Horde</button>' +
        '</div>' +
        '<div class="ab-split"><i class="sa" id="abSplitA"></i><i class="sh" id="abSplitH"></i></div>' +
        '<div class="ab-split-txt"><span class="a">Alliance <b id="abSplitAN">0</b></span><span class="h"><b id="abSplitHN">0</b> Horde</span></div>' +
        '</div>' +
        '<div class="ab-warn" id="abWarn"></div>' +
        '<div id="abRows"></div></div>' +
        '<div class="ab-prog" id="abProg">' +
        '<div class="ab-prog-h"><b id="abProgTitle"></b><span id="abProgCount"></span></div>' +
        '<div class="ab-bar" id="abBar"><i></i></div>' +
        '<div class="ab-prog-f"><span id="abProgInfo"></span>' +
        '<button class="ab-preset" id="abCancel"><i class="fa-solid fa-stop"></i> Cancel</button>' +
        '<button class="ab-preset" id="abDismiss">Dismiss</button></div></div>' +
        '<div class="ab-foot">' +
        '<div class="ab-presets">' +
        '<button class="ab-preset" id="abClear">Clear</button>' +
        '</div>' +
        '<button class="ab-spawn" id="abSpawn"><i class="fa-solid fa-bolt"></i> Spawn <span class="ab-tot" id="abTotal">0</span></button>' +
        '</div></div></div>'
    );

    // Toolbar pill: visible while a batch runs (even with the modal closed); click to reopen it.
    $('#btnAddBots').after('<span class="ab-pill" id="abPill" title="Spawn batch in progress — click to view"><i class="fa-solid fa-spinner fa-spin"></i> Spawning <span>0/0</span></span>');

    function abFmt(n) { return Number(n || 0).toLocaleString(); }
    function abParseKey(k) { var p = (k || '').split(':'); return { race: p[0], cls: p[1] }; }

    // Largest count a batch may carry: the configured ceiling (0 = unlimited) capped by unused names.
    function abEffectiveMax() {
        var m = abLimits.maxPerRequest > 0 ? abLimits.maxPerRequest : Infinity;
        if (abLimits.namesAvailable != null) m = Math.min(m, abLimits.namesAvailable);
        return m;
    }
    function abClamp(v) {
        v = parseInt(v, 10);
        if (isNaN(v) || v < 0) v = 0;
        var m = abEffectiveMax();
        if (isFinite(m) && v > m) v = m;
        return v;
    }
    function abTotal() {
        var t = 0;
        AB_RACES.forEach(function (r) { AB_RACE_CLASSES[r.key].forEach(function (c) { t += abCounts[r.key][c] || 0; }); });
        return t;
    }
    function abRunning() { return !!(abJob && abJob.phase === 'running'); }

    // Full grid build — on open and after a bulk set. Typing and +/- update in place (abUpdateTotals)
    // so the focused input survives.
    function renderAddBots() {
        var html = '';
        for (var ri = 0; ri < AB_RACES.length; ri++) {
            var r = AB_RACES[ri], cards = '';
            var clss = AB_RACE_CLASSES[r.key];
            for (var ci = 0; ci < clss.length; ci++) {
                var c = clss[ci], n = (abCounts[r.key][c] || 0), m = ADD_BOT_META[c], k = r.key + ':' + c;
                cards += '<div class="ab-card ' + (n > 0 ? 'on' : 'off') + '" style="--cc:' + m.color + ';" data-ab-card="' + k + '">' +
                    '<span class="ab-cname"><i class="fa-solid ' + m.icon + '" style="color:' + m.color + ';"></i><b>' + c + '</b></span>' +
                    '<span class="ab-step">' +
                    '<button data-ab-dec="' + k + '" tabindex="-1" title="-1">-</button>' +
                    '<input class="ab-cnt" type="number" min="0" inputmode="numeric" value="' + n + '" data-ab-key="' + k + '" aria-label="' + r.name + ' ' + c + ' count" />' +
                    '<button data-ab-inc="' + k + '" tabindex="-1" title="+1">+</button>' +
                    '</span></div>';
            }
            html += '<div class="ab-race">' +
                '<div class="ab-rhead"><span class="ab-rdot ' + r.side + '"></span>' + r.name +
                '<span class="ab-rtot" id="abRtot-' + r.key + '">0</span></div>' +
                '<div class="ab-grid">' + cards + '</div></div>';
        }
        $('#abRows').html(html);
        abUpdateTotals();
    }

    // In-place refresh of race totals, the grand total vs. limit, card on/off state, the PlayerLimit
    // heads-up, and whether Spawn is allowed.
    function abUpdateTotals() {
        var total = 0;
        AB_RACES.forEach(function (r) {
            var rt = 0;
            AB_RACE_CLASSES[r.key].forEach(function (c) {
                var n = abCounts[r.key][c] || 0;
                rt += n;
                $('[data-ab-card="' + r.key + ':' + c + '"]').toggleClass('on', n > 0).toggleClass('off', n === 0);
            });
            $('#abRtot-' + r.key).text(abFmt(rt));
            total += rt;
        });
        var max = abEffectiveMax(), over = isFinite(max) && total > max;
        $('#abTotal').toggleClass('over', over).text(over ? abFmt(total) + ' / ' + abFmt(max) : abFmt(total));
        $('#abSpawn').prop('disabled', total === 0 || over || abRunning())
            .attr('title', over ? 'Over the limit — max ' + abFmt(max) + ' per batch' : (abRunning() ? 'A batch is already running' : ''));
        var pl = abLimits.playerLimit;
        if (pl && total > 0 && (abLimits.botsOnline + total) > pl) {
            $('#abWarn').html('<b>Heads-up:</b> ' + abFmt(abLimits.botsOnline) + ' bots online + ' + abFmt(total) + ' new exceeds mangosd\'s PlayerLimit (' + abFmt(pl) +
                '). Bots count as player sessions, so non-GM players will be put in the login queue. Raise PlayerLimit in mangosd.conf if that matters.').addClass('show');
        } else {
            $('#abWarn').removeClass('show');
        }
    }

    function abSetCell(race, cls, v, $input) {
        v = abClamp(v);
        abCounts[race][cls] = v;
        if ($input && $input.length && String($input.val()) !== String(v)) $input.val(v);
        abUpdateTotals();
    }
    function abClearAll() {
        AB_RACES.forEach(function (r) { AB_RACE_CLASSES[r.key].forEach(function (c) { abCounts[r.key][c] = 0; }); });
        renderAddBots();
    }

    function abRenderMeta() {
        var parts = [];
        parts.push('Max per batch <b>' + (abLimits.maxPerRequest > 0 ? abFmt(abLimits.maxPerRequest) : 'unlimited') + '</b>');
        if (abLimits.namesError) parts.push('<span class="ab-bad">' + abLimits.namesError + '</span>');
        else if (abLimits.namesAvailable != null) parts.push('Unused names <b>' + abFmt(abLimits.namesAvailable) + '</b>');
        parts.push('Bots online <b>' + abFmt(abLimits.botsOnline) + '</b>' +
            (abLimits.playerLimit ? ' <span class="ab-limit">(PlayerLimit ' + abFmt(abLimits.playerLimit) + ')</span>' : ''));
        $('#abMeta').html(parts.map(function (p) { return '<span>' + p + '</span>'; }).join(''));
    }

    function abLoadLimits() {
        $('#abMeta').html('<span>Loading limits…</span>');
        $.getJSON('/Bots/AddBotsLimits')
            .done(function (d) {
                abLimits = {
                    maxPerRequest: d.maxPerRequest || 0,
                    namesAvailable: d.namesAvailable != null ? d.namesAvailable : null,
                    namesError: d.namesError || null,
                    botsOnline: d.botsOnline || 0,
                    playerLimit: d.playerLimit || null
                };
                abRenderMeta();
                // Re-clamp anything already typed against the real ceiling.
                var changed = false;
                AB_RACES.forEach(function (r) {
                    AB_RACE_CLASSES[r.key].forEach(function (c) {
                        var v = abClamp(abCounts[r.key][c]);
                        if (v !== abCounts[r.key][c]) { abCounts[r.key][c] = v; changed = true; }
                    });
                });
                if (changed) renderAddBots(); else abUpdateTotals();
                $('#abRandTotal').val(abClamp($('#abRandTotal').val()));
                abRandPreview();
                if (d.job) abAdoptJob(d.job);
            })
            .fail(function () {
                $('#abMeta').html('<span class="ab-bad">Limits unavailable — the server still enforces them</span>');
                abUpdateTotals();
            });
    }

    function abOpen() {
        renderAddBots();
        abRandPreview();
        $('#addBotsModal').addClass('active');
        abLoadLimits();
    }
    $('#btnAddBots').on('click', abOpen);
    $('#abPill').on('click', abOpen);

    // Load SuperUI Bots — fire `.bot add_all` over RA to re-add every persisted bot to the
    // world (the standard recovery step after a server reset). Same command as the server
    // admin console's "Add All", surfaced here so it's one click from the IBot Monitor.
    $('#btnLoadSuperuiBots').on('click', function () {
        if (!confirm('Re-add every persisted SuperUI bot to the world via .bot add_all? Run this after a server reset.')) return;
        var $btn = $(this).prop('disabled', true);
        $.ajax({ url: '/Bots/AddAll', method: 'POST' })
            .done(function (res) {
                if (res && res.success) {
                    var extra = res.response ? (' — ' + String(res.response).trim().split('\n')[0]) : '';
                    showToast('Loading SuperUI bots (.bot add_all sent)' + extra);
                } else {
                    showToast('Load SuperUI bots failed: ' + ((res && res.error) || 'unknown'), true);
                }
            })
            .fail(function (xhr) { showToast('Load SuperUI bots failed (' + xhr.status + ')', true); })
            .always(function () { $btn.prop('disabled', false); });
    });

    $('#abClose').on('click', function () { $('#addBotsModal').removeClass('active'); });
    $('#addBotsModal').on('click', function (e) { if (e.target === this) $(this).removeClass('active'); });
    $(document).on('keydown', function (e) { if (e.key === 'Escape') $('#addBotsModal').removeClass('active'); });

    // --- Per-cell editing: type, +/-, Enter to commit ---
    $(document).on('focus', '.ab-cnt, .ab-rand input', function () { var el = this; setTimeout(function () { el.select(); }, 0); });
    // A focused type="number" consumes the wheel: scrolling this (scrollable) modal with the cursor
    // over one silently changes the number. Dropping focus first lets the scroll through untouched.
    $(document).on('wheel', '.ab-cnt, .ab-rand input', function () { if (this === document.activeElement) this.blur(); });
    $(document).on('input', '.ab-cnt', function () {
        var kv = abParseKey($(this).attr('data-ab-key'));
        if ($(this).val() === '') { abCounts[kv.race][kv.cls] = 0; abUpdateTotals(); return; }   // mid-edit blank: don't fight the caret
        abSetCell(kv.race, kv.cls, $(this).val(), $(this));
    });
    $(document).on('change blur', '.ab-cnt', function () {
        var kv = abParseKey($(this).attr('data-ab-key'));
        abSetCell(kv.race, kv.cls, $(this).val(), $(this));
        $(this).val(abCounts[kv.race][kv.cls]);   // normalise "", "007", etc.
    });
    $(document).on('keydown', '.ab-cnt', function (e) { if (e.key === 'Enter') { e.preventDefault(); this.blur(); } });
    $(document).on('click', '[data-ab-inc]', function () {
        var k = $(this).attr('data-ab-inc'), kv = abParseKey(k);
        abSetCell(kv.race, kv.cls, (abCounts[kv.race][kv.cls] || 0) + 1, $('.ab-cnt[data-ab-key="' + k + '"]'));
    });
    $(document).on('click', '[data-ab-dec]', function () {
        var k = $(this).attr('data-ab-dec'), kv = abParseKey(k);
        abSetCell(kv.race, kv.cls, Math.max(0, (abCounts[kv.race][kv.cls] || 0) - 1), $('.ab-cnt[data-ab-key="' + k + '"]'));
    });

    $('#abClear').on('click', abClearAll);

    // --- Randomize: spread a target population over every race/class, split by faction ---
    // The two percent boxes are two views of one number, so they always total 100.
    function abRandAlliancePct() {
        var a = parseInt($('#abRandA').val(), 10);
        if (isNaN(a) || a < 0) a = 0;
        if (a > 100) a = 100;
        return a;
    }

    // Live preview of what Fill would produce — drives the split bar and the two counts.
    function abRandPreview() {
        var total = abClamp($('#abRandTotal').val()), pct = abRandAlliancePct();
        var alliance = Math.min(total, Math.round(total * pct / 100)), horde = total - alliance;
        var pa = total ? (alliance / total * 100) : pct;
        $('#abSplitA').css('width', pa + '%');
        $('#abSplitH').css('width', (100 - pa) + '%');
        $('#abSplitAN').text(abFmt(alliance));
        $('#abSplitHN').text(abFmt(horde));
        abSyncQuick();
        return { total: total, alliance: alliance, horde: horde };
    }

    // Give every race/class of one faction a random weight, hand out n bots in proportion, then
    // settle the leftover by largest remainder — so the faction total lands EXACTLY on target
    // instead of drifting by however many of the 20 cells happened to round down.
    function abSpread(side, n) {
        var cells = [];
        AB_RACES.forEach(function (r) {
            if (r.side !== side) return;
            AB_RACE_CLASSES[r.key].forEach(function (c) { cells.push({ race: r.key, cls: c, w: 0.35 + Math.random() }); });
        });
        if (!cells.length || n <= 0) return;
        var sum = 0, assigned = 0;
        cells.forEach(function (c) { sum += c.w; });
        cells.forEach(function (c) { var exact = n * c.w / sum; c.n = Math.floor(exact); c.rem = exact - c.n; assigned += c.n; });
        cells.sort(function (x, y) { return y.rem - x.rem; });
        for (var i = 0; assigned < n; i++, assigned++) cells[i % cells.length].n++;
        cells.forEach(function (c) { abCounts[c.race][c.cls] = c.n; });
    }

    function abRandomize() {
        var p = abRandPreview();
        AB_RACES.forEach(function (r) { AB_RACE_CLASSES[r.key].forEach(function (c) { abCounts[r.key][c] = 0; }); });
        abSpread('alliance', p.alliance);
        abSpread('horde', p.horde);
        renderAddBots();
        if (p.total > 0) showToast('Randomized ' + abFmt(p.total) + ' — ' + abFmt(p.alliance) + ' Alliance / ' + abFmt(p.horde) + ' Horde');
    }

    // Quick-set chips: the common answers, without typing. They set the boxes and refresh the
    // preview - Fill stays an explicit second step, same as it is for a hand-typed total.
    function abSyncQuick() {
        var cur = parseInt($('#abRandTotal').val(), 10);
        $('[data-ab-qty]').each(function () {
            var v = $(this).attr('data-ab-qty');
            $(this).toggleClass('on', v !== 'max' && parseInt(v, 10) === cur);
        });
        var max = abEffectiveMax(), known = isFinite(max);
        $('#abQtyMax').prop('disabled', !known)
            .toggleClass('on', known && cur === max)
            .text(known ? 'Max ' + abFmt(max) : 'Max')
            .attr('title', known
                ? 'The lower of the per-batch ceiling and the unused-name pool'
                : 'No readable ceiling yet - type a total');
        var pct = abRandAlliancePct();
        $('[data-ab-split]').each(function () {
            $(this).toggleClass('on', parseInt($(this).attr('data-ab-split'), 10) === pct);
        });
    }
    $(document).on('click', '[data-ab-qty]', function () {
        var v = $(this).attr('data-ab-qty');
        var n = v === 'max' ? abEffectiveMax() : parseInt(v, 10);
        if (!isFinite(n)) return;
        $('#abRandTotal').val(abClamp(n));
        abRandPreview();
    });
    $(document).on('click', '[data-ab-split]', function () {
        var a = parseInt($(this).attr('data-ab-split'), 10);
        $('#abRandA').val(a);
        $('#abRandH').val(100 - a);
        abRandPreview();
    });

    $('#abRandGo').on('click', abRandomize);
    $('#abRandTotal').on('input', abRandPreview);
    $('#abRandTotal').on('change blur', function () { $(this).val(abClamp($(this).val())); abRandPreview(); });
    $('#abRandA').on('input', function () { $('#abRandH').val(100 - abRandAlliancePct()); abRandPreview(); });
    $('#abRandH').on('input', function () {
        var h = parseInt($(this).val(), 10);
        if (isNaN(h) || h < 0) h = 0;
        if (h > 100) h = 100;
        $('#abRandA').val(100 - h);
        abRandPreview();
    });
    $('#abRandA, #abRandH').on('change blur', function () {
        var a = abRandAlliancePct();
        $('#abRandA').val(a); $('#abRandH').val(100 - a);
        abRandPreview();
    });
    $('#abRandTotal, #abRandA, #abRandH').on('keydown', function (e) { if (e.key === 'Enter') { e.preventDefault(); abRandomize(); } });

    // --- Spawn: the request returns as soon as the batch is queued; progress streams in after ---
    $('#abSpawn').on('click', function () {
        var spawns = [], total = 0;
        AB_RACES.forEach(function (r) {
            AB_RACE_CLASSES[r.key].forEach(function (c) {
                var n = abCounts[r.key][c] || 0;
                if (n > 0) { spawns.push({ race: r.key, cls: c, count: n }); total += n; }
            });
        });
        if (!total) { showToast('Pick at least one bot', true); return; }
        var $btn = $(this).prop('disabled', true);
        $.ajax({ url: '/Bots/AddBots', method: 'POST', contentType: 'application/json', data: JSON.stringify({ spawns: spawns }) })
            .done(function (res) {
                if (res && res.success && res.job) {
                    showToast('Spawning ' + abFmt(total) + ' bot(s) in the background');
                    // The batch belongs to the server now, so unload the picker. Left staged, the
                    // same counts sit there through the run and Spawn re-enables itself the moment
                    // the batch finishes - one stray click from silently doubling the fleet. The
                    // modal stays open because it is where the progress lives; it just isn't loaded.
                    abClearAll();
                    abOnSpawnProgress(res.job);
                } else {
                    showToast('Add bots failed: ' + ((res && res.error) || 'unknown'), true);
                    if (res && res.job) abAdoptJob(res.job);   // e.g. "already running" — show that batch
                    abUpdateTotals();
                }
            })
            .fail(function (xhr) { showToast('Add Bots failed (' + xhr.status + ')', true); abUpdateTotals(); });
    });

    // Adopt a snapshot that arrived out of band (page load, modal open, refused Spawn): only a
    // running batch, or the one we were already following — never resurrect a stale finished one.
    function abAdoptJob(job) {
        if (!job) return;
        if (job.phase === 'running' || (abJob && abJob.id === job.id)) abOnSpawnProgress(job);
    }

    // SpawnProgress handler — also wired to the hub event in initConnection().
    function abOnSpawnProgress(job) {
        if (!job) return;
        if (abJob && abJob.id === job.id && abJob.phase !== 'running' && job.phase !== 'running') return;   // duplicate final
        var prev = abJob;
        abJob = job;
        var running = job.phase === 'running';
        var done = (job.sent || 0) + (job.failed || 0);
        var pct = job.requested ? Math.min(100, Math.round(done / job.requested * 100)) : 0;
        var secs = Math.round((job.elapsedMs || 0) / 1000);
        var rate = job.elapsedMs > 0 ? done / (job.elapsedMs / 1000) : 0;
        var eta = running && rate > 0 ? Math.round((job.requested - done) / rate) : 0;
        var title = running ? 'Spawning' : job.phase === 'done' ? 'Batch complete' : job.phase === 'cancelled' ? 'Batch cancelled' : 'Batch failed';

        $('#abProg').addClass('show');
        $('#abProgTitle').text(title + (running && job.current ? ' — ' + job.current : ''));
        $('#abProgCount').text(abFmt(done) + ' / ' + abFmt(job.requested) + (job.failed ? ' · ' + abFmt(job.failed) + ' failed' : ''));
        $('#abBar').removeClass('done cancelled failed').addClass(running ? '' : job.phase).find('i').css('width', pct + '%');

        var info = secs + 's';
        if (rate) info += ' · ' + rate.toFixed(1) + ' bots/s';
        if (running && eta) info += ' · ~' + eta + 's left';
        if (job.error) info += ' · ' + job.error;
        if (!running && job.failedNames && job.failedNames.length) {
            info += ' · failed: ' + job.failedNames.slice(0, 8).join(', ') + (job.failedNames.length > 8 ? ' +' + (job.failedNames.length - 8) + ' more' : '');
        }
        $('#abProgInfo').text(info);
        $('#abCancel').toggle(running).prop('disabled', false);
        $('#abDismiss').toggle(!running);

        $('#abPill').toggleClass('show', running).find('span').text(abFmt(done) + '/' + abFmt(job.requested));
        if (running) abStartPoll(); else abStopPoll();

        // One completion toast per batch we watched run.
        if (!running && prev && prev.id === job.id && prev.phase === 'running' && abToastedJobId !== job.id) {
            abToastedJobId = job.id;
            var summary = abFmt(job.sent) + ' / ' + abFmt(job.requested) + ' spawned' + (job.failed ? ' (' + abFmt(job.failed) + ' failed)' : '');
            showToast(job.phase === 'done' ? summary : title + ' — ' + summary, job.phase !== 'done');
            if ($('#addBotsModal').hasClass('active')) abLoadLimits();   // names / bots online moved
        }
        abUpdateTotals();
    }
    function abStartPoll() {
        if (abPollTimer) return;
        abPollTimer = setInterval(function () {
            $.getJSON('/Bots/AddBotsStatus').done(function (d) { if (d && d.job) abOnSpawnProgress(d.job); });
        }, 1000);
    }
    function abStopPoll() { if (abPollTimer) { clearInterval(abPollTimer); abPollTimer = null; } }

    $('#abCancel').on('click', function () {
        $(this).prop('disabled', true);
        $.post('/Bots/AddBotsCancel').done(function (d) { if (d && d.job) abOnSpawnProgress(d.job); });
    });
    $('#abDismiss').on('click', function () { $('#abProg').removeClass('show'); });

    // A batch may already be running from before this page load — pick it up.
    $.getJSON('/Bots/AddBotsStatus').done(function (d) { if (d && d.job) abAdoptJob(d.job); });

    // ==================== Manage Bot Groups modal ====================
    // Header button → overlay listing every active group (BrainStatus), filterable by member
    // name / group id, with per-group Disband and a Dissolve All. Reuses the Add Bots modal
    // chrome (.ab-*) so the two dialogs look like one family.
    $('head').append(
        '<style>' +
        '.mg-modal{max-width:640px;}' +
        '.mg-tools{display:flex;align-items:center;gap:10px;margin-bottom:12px;}' +
        '.mg-search{flex:1;position:relative;}' +
        '.mg-search i{position:absolute;left:10px;top:50%;transform:translateY(-50%);color:var(--text-muted,#787c99);font-size:12px;pointer-events:none;}' +
        '.mg-search input{width:100%;box-sizing:border-box;padding:7px 10px 7px 30px;border-radius:8px;border:1px solid var(--border-light,#414868);background:var(--bg-card-alt,#24283b);color:var(--text-secondary,#c0caf5);font-size:12.5px;outline:none;transition:border-color .12s;}' +
        '.mg-search input:focus{border-color:var(--accent,#7aa2f7);}' +
        '.mg-sum{font-size:11px;color:var(--text-muted,#787c99);white-space:nowrap;font-variant-numeric:tabular-nums;}' +
        '.mg-row{display:flex;align-items:center;gap:10px;padding:9px 11px 9px 14px;border-radius:10px;border:1px solid var(--border-light,#414868);background:var(--bg-card-alt,#24283b);margin-bottom:8px;position:relative;overflow:hidden;}' +
        '.mg-row::before{content:"";position:absolute;left:0;top:0;bottom:0;width:3px;background:var(--gc,#7aa2f7);}' +
        '.mg-gid{font-size:11px;font-weight:700;padding:1px 7px;border-radius:4px;color:var(--gc,#7aa2f7);background:color-mix(in srgb,var(--gc,#7aa2f7) 14%,transparent);border:1px solid color-mix(in srgb,var(--gc,#7aa2f7) 40%,transparent);flex:none;}' +
        '.mg-members{flex:1;min-width:0;display:flex;flex-wrap:wrap;gap:4px 8px;font-size:12px;}' +
        '.mg-members .mg-lead{color:#e0af68;}' +
        '.mg-members mark{background:rgba(224,175,104,0.35);color:inherit;border-radius:2px;padding:0 1px;}' +
        '.mg-meta{font-size:10.5px;color:var(--text-muted,#787c99);white-space:nowrap;flex:none;}' +
        '.mg-dis{font-size:11px;padding:4px 10px;border-radius:7px;border:1px solid rgba(247,118,142,0.5);background:rgba(247,118,142,0.12);color:#f7768e;cursor:pointer;flex:none;transition:all .12s;}' +
        '.mg-dis:hover{background:#f7768e;color:#1a1b26;}' +
        '.mg-dis:disabled{opacity:.5;cursor:default;}' +
        '.mg-empty{color:var(--text-muted,#787c99);font-size:12px;padding:18px 4px;text-align:center;}' +
        '.mg-hint{font-size:11px;color:var(--text-muted,#787c99);}' +
        '.mg-hint b{color:var(--text-secondary,#c0caf5);font-weight:600;}' +
        '.mg-all{font-size:13px;font-weight:600;padding:9px 16px;border:none;border-radius:9px;cursor:pointer;color:#fff;display:inline-flex;align-items:center;gap:7px;background:linear-gradient(135deg,#f7768e,#ff9e64);box-shadow:0 4px 14px rgba(247,118,142,0.3);transition:transform .12s,box-shadow .12s,filter .12s;}' +
        '.mg-all:hover{transform:translateY(-1px);box-shadow:0 6px 20px rgba(247,118,142,0.42);}' +
        '.mg-all:disabled{filter:grayscale(.5) brightness(.7);cursor:default;transform:none;box-shadow:none;}' +
        '</style>'
    );

    $('body').append(
        '<div class="ab-overlay" id="manageGroupsModal">' +
        '<div class="ab-modal mg-modal">' +
        '<div class="ab-header">' +
        '<div class="ab-hleft"><div class="ab-hicon"><i class="fa-solid fa-people-group"></i></div>' +
        '<div class="ab-htxt"><b>Manage Bot Groups</b><span>Find a group by member name, disband one, or dissolve every group at once</span></div></div>' +
        '<button class="ab-close" id="mgClose"><i class="fa-solid fa-xmark"></i></button></div>' +
        '<div class="ab-body">' +
        '<div class="mg-tools">' +
        '<div class="mg-search"><i class="fa-solid fa-magnifying-glass"></i><input type="text" id="mgSearch" placeholder="Search by bot name or group #..." autocomplete="off"></div>' +
        '<span class="mg-sum" id="mgSummary"></span>' +
        '</div>' +
        '<div id="mgList"><div class="mg-empty">Loading...</div></div>' +
        '</div>' +
        '<div class="ab-foot">' +
        '<span class="mg-hint" id="mgHint"></span>' +
        '<button class="mg-all" id="mgDissolveAll"><i class="fa-solid fa-people-arrows"></i> Dissolve All <span class="ab-tot" id="mgAllCount">0</span></button>' +
        '</div></div></div>'
    );

    var mgGroups = [], mgNames = {}, mgModeName = '';

    function mgEsc(s) { return String(s == null ? '' : s).replace(/[&<>"']/g, function (c) { return { '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]; }); }
    function mgHighlight(name, q) {
        var safe = mgEsc(name);
        if (!q) return safe;
        var i = name.toLowerCase().indexOf(q);
        if (i < 0) return safe;
        return mgEsc(name.slice(0, i)) + '<mark>' + mgEsc(name.slice(i, i + q.length)) + '</mark>' + mgEsc(name.slice(i + q.length));
    }
    function mgNameOf(guid) {
        if (mgNames[guid]) return mgNames[guid];
        var s = botStates[guid];
        return s && s.name ? s.name : ('Bot #' + guid);
    }
    function mgAge(formedAt) {
        var t = Date.parse(formedAt); if (isNaN(t)) return '';
        var m = Math.max(0, Math.round((Date.now() - t) / 60000));
        if (m < 60) return m + 'm';
        var h = Math.floor(m / 60); if (h < 48) return h + 'h ' + (m % 60) + 'm';
        return Math.floor(h / 24) + 'd';
    }

    function renderManageGroups() {
        var q = ($('#mgSearch').val() || '').trim().toLowerCase();
        var shown = mgGroups.filter(function (g) {
            if (!q) return true;
            if (('#' + g.groupId).indexOf(q) === 0 || ('g' + g.groupId).indexOf(q) === 0 || String(g.groupId) === q) return true;
            return (g.memberGuids || []).some(function (guid) { return mgNameOf(guid).toLowerCase().indexOf(q) >= 0; });
        });
        var grouped = 0; mgGroups.forEach(function (g) { grouped += (g.memberGuids || []).length; });
        $('#mgSummary').text(mgGroups.length + ' group' + (mgGroups.length === 1 ? '' : 's') + ' \u00b7 ' + grouped + ' bots' + (q ? ' \u00b7 ' + shown.length + ' match' : ''));
        $('#mgAllCount').text(mgGroups.length);
        $('#mgDissolveAll').prop('disabled', mgGroups.length === 0);
        $('#mgHint').html(mgModeName ? ('Grouping mode: <b>' + mgEsc(mgModeName) + '</b>' + (mgModeName !== 'Off' ? ' \u2014 auto-forming may regroup freed bots' : '')) : '');

        if (!mgGroups.length) { $('#mgList').html('<div class="mg-empty">No active groups</div>'); return; }
        if (!shown.length) { $('#mgList').html('<div class="mg-empty">No group has a member matching "' + mgEsc(q) + '"</div>'); return; }

        var html = '';
        shown.forEach(function (g) {
            var c = groupColor(g.groupId) || '#7aa2f7';
            var members = (g.memberGuids || []).slice().sort(function (a, b) { return (a === g.leaderGuid ? 0 : 1) - (b === g.leaderGuid ? 0 : 1); })
                .map(function (guid) {
                    var lead = guid === g.leaderGuid;
                    return '<span class="' + (lead ? 'mg-lead' : '') + '" title="guid ' + guid + (lead ? ' (leader)' : '') + '">'
                        + (lead ? '<i class="fa-solid fa-crown" style="font-size:10px;margin-right:3px;"></i>' : '')
                        + mgHighlight(mgNameOf(guid), q) + '</span>';
                }).join('');
            var age = mgAge(g.formedAt);
            html += '<div class="mg-row" style="--gc:' + c + ';">'
                + '<span class="mg-gid">G' + g.groupId + '</span>'
                + '<span class="mg-members">' + members + '</span>'
                + '<span class="mg-meta">' + (g.memberGuids || []).length + ' bots' + (age ? ' \u00b7 ' + age : '') + '</span>'
                + '<button class="mg-dis" data-mg-disband="' + g.groupId + '"><i class="fa-solid fa-link-slash"></i> Disband</button>'
                + '</div>';
        });
        $('#mgList').html(html);
    }

    function loadManageGroups() {
        $.getJSON('/Bots/BrainStatus').done(function (data) {
            mgGroups = data.groups || [];
            mgModeName = data.groupingModeName || '';
            mgNames = {};
            (data.bots || []).forEach(function (b) { if (b.name) mgNames[b.guid] = b.name; });
            renderManageGroups();
            updateGroupingUI(data);
        }).fail(function (xhr) {
            $('#mgList').html('<div class="mg-empty">Failed to load groups (' + xhr.status + ')</div>');
        });
    }

    $('#btnManageGroups').on('click', function () {
        $('#mgList').html('<div class="mg-empty">Loading...</div>');
        $('#manageGroupsModal').addClass('active');
        loadManageGroups();
        setTimeout(function () { $('#mgSearch').trigger('focus'); }, 50);
    });
    $('#mgClose').on('click', function () { $('#manageGroupsModal').removeClass('active'); });
    $('#manageGroupsModal').on('click', function (e) { if (e.target === this) $(this).removeClass('active'); });
    $(document).on('keydown', function (e) { if (e.key === 'Escape') $('#manageGroupsModal').removeClass('active'); });
    $('#mgSearch').on('input', renderManageGroups);

    $(document).on('click', '[data-mg-disband]', function () {
        var gid = parseInt($(this).attr('data-mg-disband'), 10);
        if (!gid) return;
        var $btn = $(this).prop('disabled', true);
        $.ajax({ url: '/Bots/DisbandGroup', type: 'POST', contentType: 'application/json', data: JSON.stringify({ groupId: gid }) })
            .done(function (data) {
                if (data && data.success) {
                    showToast('Group #' + gid + ' disbanded');
                    mgGroups = mgGroups.filter(function (g) { return g.groupId !== gid; });
                    renderManageGroups();
                    refreshGroupingStatus();
                } else {
                    showToast('Disband failed: ' + ((data && data.error) || 'unknown'), true);
                    $btn.prop('disabled', false);
                }
            })
            .fail(function (xhr) { showToast('Disband failed (' + xhr.status + ')', true); $btn.prop('disabled', false); });
    });

    $('#mgDissolveAll').on('click', function () {
        var n = mgGroups.length;
        if (!n) return;
        if (!confirm('Dissolve all ' + n + ' bot group' + (n === 1 ? '' : 's') + '? Every member goes solo and DISBAND_GROUP is sent to each leader.')) return;
        var $btn = $(this).prop('disabled', true);
        $.ajax({ url: '/Bots/DisbandAllGroups', type: 'POST' })
            .done(function (data) {
                if (data && data.success) {
                    showToast('Dissolved ' + data.disbanded + ' group' + (data.disbanded === 1 ? '' : 's'));
                    loadManageGroups();
                } else {
                    showToast('Dissolve all failed: ' + ((data && data.error) || 'unknown'), true);
                }
            })
            .fail(function (xhr) { showToast('Dissolve all failed (' + xhr.status + ')', true); })
            .always(function () { $btn.prop('disabled', false); });
    });

    $(document).on('click', '#btnBotReport', function (e) {
        e.stopPropagation();
        openBotReport();
    });

    function openBotReport() {
        var g = selectedGuid;
        var st = g ? botStates[g] : null;
        if (!st || !st.name) return;
        $('#brTitle').html('<i class="fa-solid fa-bolt"></i> Report — ' + esc(st.name));
        $('#brBody').html('<div class="br-loading"><i class="fa-solid fa-spinner fa-spin"></i> reading buffered log…</div>');
        $('#botReportModal').addClass('active');
        $.getJSON('/Bots/BotReport', { name: st.name }, function (data) {
            if (!data || data.error) { $('#brBody').html('<div class="br-loading">' + esc((data && data.error) || 'no data') + '</div>'); return; }
            $('#brBody').html(renderBotReport(data));
        }).fail(function () { $('#brBody').html('<div class="br-loading">request failed</div>'); });
    }

    function renderBotReport(d) {
        if (d.empty || !d.total) return '<div class="br-loading">log buffer empty for this bot</div>';
        var c = d.census || {};
        var h = d.health || {};

        var html = '<div class="br-meta">' + (d.botLines != null ? d.botLines : d.total) + ' bot lines' +
            (d.fleetLines ? ' · ' + d.fleetLines + ' fleet heartbeats folded out' : '') +
            ' · ' + (d.spanSec || 0) + 's window</div>';

        // Health banner — the two diagnostics proxies up top.
        var spiral = h.deathSpiral;
        html += '<div class="br-health' + (spiral ? ' bad' : '') + '">' +
            '<span><b>' + (h.killsVsCompletions || '—') + '</b> kills/credit</span>' +
            '<span>rez/kill <b>' + (h.rezPerKill != null ? h.rezPerKill : '—') + '</b></span>' +
            (spiral ? '<span class="br-flag">death spiral</span>' : '') +
            ((c.kills > 0 && c.completions === 0) ? '<span class="br-flag">kills, 0 credit</span>' : '') +
            '</div>';

        // Census grid — bounded, counts only.
        var cells = [
            ['kills', c.kills, '#9ece6a'], ['completions', c.completions, '#9ece6a'],
            ['rewarded', c.rewarded, '#9ece6a'], ['grind done', c.grindFinished, '#9ece6a'],
            ['level-ups', c.levelUps, '#bb9af7'], ['quest evts', c.questEvents, '#7aa2f7'],
            ['resurrects', c.resurrects, '#ff9e64'], ['deaths', c.deaths, '#f7768e'],
            ['relocates', c.relocates, '#ff9e64'], ['repairs', c.repairs, '#ff9e64'],
            ['sells', c.sells, '#ff9e64'], ['overflow', c.overflow, '#e0af68'],
            ['shelvings', c.shelvings, '#e0af68'], ['stalls', c.stalls, '#f7768e'],
            ['no-path', c.noPath, '#f7768e'], ['path-unsafe', c.pathUnsafe, '#f7768e']
        ];
        html += '<div class="br-grid">';
        for (var i = 0; i < cells.length; i++) {
            var n = cells[i][1] || 0;
            html += '<div class="br-cell"><div class="br-cell-n" style="color:' + (n > 0 ? cells[i][2] : 'var(--text-muted)') + ';">' +
                n + '</div><div class="br-cell-l">' + cells[i][0] + '</div></div>';
        }
        html += '</div>';

        // Top repeated signatures (≤12).
        if (d.top && d.top.length) {
            html += '<div class="br-sec-h">Top repeated lines</div><div class="br-top">';
            for (var t = 0; t < d.top.length; t++) {
                html += '<div class="br-top-row"><span class="br-top-n">' + d.top[t].n + '</span>' +
                    '<span class="br-top-sig">' + esc(d.top[t].sig) + '</span></div>';
            }
            html += '</div>';
        }
        return html;
    }

    // Open modal on double-click of roster card (single click keeps existing detail panel)
    $(document).on('dblclick', '.bt-roster-card', function () {
        var guid = parseInt($(this).data('guid'));
        openBotModal(guid);
    });

    // Details button in the bot header panel
    $(document).on('click', '.btnOpenModal', function (e) {
        e.stopPropagation();
        var guid = parseInt($(this).data('guid'));
        openBotModal(guid);
    });

    // guid 0 opens the modal in FLEET mode: control only, every command broadcasts.
    function openBotModal(guid, tab) {
        guid = parseInt(guid, 10) || 0;
        var s = botStates[guid];
        if (guid > 0 && !s) return;

        if (guid === 0) {
            $('#bmTitle').html('<span style="color:var(--text-primary);">Fleet control</span>' +
                ' <span style="font-weight:400;font-size:12px;color:var(--text-muted);">every connected bot</span>');
            $('.bm-tab[data-tab!="control"]').hide();
            $('.bm-tab[data-tab="danger"]').show();
            $('#bmDeleteBtn').hide();
            tab = 'control';
        } else {
            var className = CLASS_NAMES[s.classId] || '?';
            $('#bmTitle').html(
                '<span style="color:var(--text-primary);">' + esc(s.name) + '</span>' +
                ' <span class="bt-class-badge ' + (CLASS_CSS[s.classId] || '') + '" style="font-size:11px;">' + className + '</span>' +
                ' <span style="font-weight:400;font-size:12px;color:var(--text-muted);">L' + (s.level || 0) +
                ' \u00b7 ' + esc(zoneKeyLabel(botZoneKey(guid))) + '</span>'
            );
            $('.bm-tab').show();
            $('.bm-tab[data-tab="danger"]').hide();
            $('#bmDeleteBtn').show();
        }

        $('#botModal').data('guid', guid).addClass('active');
        var $t = $('.bm-tab[data-tab="' + (tab || 'control') + '"]');
        ($t.length ? $t : $('.bm-tab:visible').first()).click();
    }

    function loadModalTab(tab) {
        var guid = $('#botModal').data('guid');
        if (guid == null) return;
        guid = parseInt(guid, 10) || 0;

        if (tab === 'control') { renderControlTab(guid); return; }
        if (tab === 'danger') { renderDangerZoneTab(); return; }
        if (guid === 0) { renderControlTab(0); return; }

        switch (tab) {
            case 'quests':
                loadQuestTab(guid);
                break;
            case 'gear':
                renderGearTab(guid);
                break;
            case 'brain':
                renderBrainTab(guid);
                break;
        }
    }

    // ===================== DANGER ZONE (mass delete) =====================
    // Targets the entire persisted bot roster (characters.playerbot) — independent of
    // the Control tab's "Apply to" scope, which only covers currently-connected bots.
    function renderDangerZoneTab() {
        var $body = $('#bmBody');
        $body.html('<div class="bq-loading"><i class="fa-solid fa-spinner fa-spin"></i> Loading roster...</div>');

        $.getJSON('/Bots/RosterSummary', function (data) {
            var total = data.total || 0;
            var online = data.online || 0;
            var html = '<div class="bc-card"><div class="bc-card-h"><i class="fa-solid fa-skull-crossbones"></i>Mass Delete' +
                '<span class="bc-note">permanently deletes bots from the database</span></div>' +
                '<div class="bc-card-b">' +
                '<div style="margin-bottom:12px;color:var(--text-secondary, #a9b1d6);font-size:13px;">' +
                'Targets the entire bot roster: <b>' + total + '</b> bot' + (total === 1 ? '' : 's') + '.' +
                (online > 0
                    ? ' <span style="color:#f7768e;">' + online + ' still online — disconnect them first, the whole batch is blocked otherwise.</span>'
                    : '') +
                '</div>' +
                '<button type="button" class="bm-delete-btn" id="bmMassDeleteBtn" style="margin-left:0;"' +
                (total === 0 ? ' disabled' : '') + '><i class="fa-solid fa-trash" style="margin-right:5px;"></i>Delete All Bots</button>' +
                '</div></div>';
            $body.html(html);
            $body.data('rosterTotal', total);
        }).fail(function () {
            $body.html('<div style="color:#f7768e;padding:16px;">Failed to load roster summary</div>');
        });
    }

    function loadQuestTab(guid) {
        var $body = $('#bmBody');
        $body.html('<div class="bq-loading"><i class="fa-solid fa-spinner fa-spin"></i> Loading quest status...</div>');

        // Always fetch fresh
        $.getJSON('/Bots/QuestStatus', { guid: guid }, function (data) {
            if (data.error) {
                $body.html('<div style="color:#f7768e;padding:16px;">' + esc(data.error) + '</div>');
                return;
            }
            questStatusCache[guid] = data;
            renderQuestTab(data);
        }).fail(function () {
            $body.html('<div style="color:#f7768e;padding:16px;">Failed to load quest status</div>');
        });
    }

    function renderQuestTab(data) {
        var $body = $('#bmBody');
        var quests = data.quests || [];

        if (quests.length === 0) {
            $body.html('<div style="color:var(--text-muted);padding:20px;text-align:center;">No quests in log</div>');
            return;
        }

        // Group by zone
        var zones = {};
        for (var i = 0; i < quests.length; i++) {
            var q = quests[i];
            var z = q.zone || 0;
            if (!zones[z]) zones[z] = [];
            zones[z].push(q);
        }

        // Sort zones: positive zones first (by ID), then negative (class quests)
        var zoneKeys = Object.keys(zones).sort(function (a, b) {
            var ai = parseInt(a), bi = parseInt(b);
            if (ai > 0 && bi > 0) return ai - bi;
            if (ai > 0) return -1;
            if (bi > 0) return 1;
            return ai - bi;
        });

        // Stats summary
        var rewarded = quests.filter(function (q) { return q.rewarded === 1; }).length;
        var active = quests.filter(function (q) { return q.status === 1 && q.rewarded === 0; }).length;
        var complete = quests.filter(function (q) { return q.status === 3 && q.rewarded === 0; }).length;

        var html = '<div style="display:flex;gap:16px;margin-bottom:14px;font-size:12px;">' +
            '<span style="color:#9ece6a;"><i class="fa-solid fa-circle-check" style="margin-right:4px;"></i>' + rewarded + ' rewarded</span>' +
            '<span style="color:#e0af68;"><i class="fa-solid fa-circle-check" style="margin-right:4px;"></i>' + complete + ' complete</span>' +
            '<span style="color:#7aa2f7;"><i class="fa-solid fa-spinner" style="margin-right:4px;"></i>' + active + ' active</span>' +
            '<span style="color:var(--text-muted);">' + quests.length + ' total</span>' +
            '</div>';

        for (var zi = 0; zi < zoneKeys.length; zi++) {
            var zoneId = zoneKeys[zi];
            var zoneQuests = zones[zoneId];
            var zoneName = ZONE_NAMES[zoneId] || ('Zone ' + zoneId);
            var zoneRewarded = zoneQuests.filter(function (q) { return q.rewarded === 1; }).length;

            html += '<div class="bq-zone-group">';
            html += '<div class="bq-zone-header"><i class="fa-solid fa-map-location-dot"></i> ' + esc(zoneName) +
                ' <span class="bq-zone-badge">' + zoneRewarded + '/' + zoneQuests.length + ' done</span></div>';

            for (var qi = 0; qi < zoneQuests.length; qi++) {
                var q = zoneQuests[qi];
                var statusColor = q.rewarded === 1 ? '#9ece6a' : (QUEST_STATUS_COLORS[q.status] || '#5f6b7a');
                var statusIcon = q.rewarded === 1 ? 'fa-circle-check' : (QUEST_STATUS_ICONS[q.status] || 'fa-circle-xmark');
                var statusText = q.rewarded === 1 ? 'Rewarded' : (QUEST_STATUS_NAMES[q.status] || 'Unknown');

                // Build progress string
                var progressHtml = '';
                if (q.rewarded === 1) {
                    progressHtml = '<span class="bq-rewarded"><i class="fa-solid fa-check"></i> Done</span>';
                } else if (q.status === 1 || q.status === 3) {
                    // Show mob/item progress
                    var parts = [];
                    for (var m = 0; m < 4; m++) {
                        if (q.mobRequired[m] > 0)
                            parts.push(q.mobCounts[m] + '/' + q.mobRequired[m] + ' kills');
                    }
                    for (var it = 0; it < 4; it++) {
                        if (q.itemRequired[it] > 0)
                            parts.push(q.itemCounts[it] + '/' + q.itemRequired[it] + ' items');
                    }
                    if (parts.length > 0) progressHtml = parts.join(', ');
                    else if (q.status === 3) progressHtml = '<span style="color:#e0af68;">Turn in</span>';
                    else progressHtml = '<span style="color:#7aa2f7;">Active</span>';
                }

                // Chain tags
                var tags = '';
                if (q.prevQuestId !== 0) tags += ' <span class="bq-chain-tag">req #' + Math.abs(q.prevQuestId) + '</span>';
                if (q.exclusiveGroup !== 0) tags += ' <span class="bq-excl-tag">excl ' + q.exclusiveGroup + '</span>';

                var rowId = 'bqr-' + data.guid + '-' + q.questId;
                var detId = 'bqd-' + data.guid + '-' + q.questId;

                html += '<div class="bq-quest-row" data-detail="' + detId + '" id="' + rowId + '">' +
                    '<div class="bq-status-icon"><i class="fa-solid ' + statusIcon + '" style="color:' + statusColor + ';"></i></div>' +
                    '<span class="bq-quest-title" style="color:' + (q.rewarded === 1 ? '#9ece6a' : 'var(--text-primary)') + ';">' +
                    '<span style="color:var(--text-muted);font-weight:400;font-size:11px;margin-right:4px;">[#' + q.questId + ']</span>' +
                    esc(q.title) + tags + '</span>' +
                    '<span class="bq-quest-level">L' + q.questLevel + '</span>' +
                    '<span class="bq-quest-progress">' + progressHtml + '</span>' +
                    '</div>';

                // Expandable detail
                html += '<div class="bq-detail" id="' + detId + '">';
                html += '<div><span class="bq-detail-label">Quest ID:</span> ' + q.questId + '</div>';
                if (q.giverName) html += '<div><span class="bq-detail-label">Given by:</span> ' + esc(q.giverName) + '</div>';
                if (q.turnInName) html += '<div><span class="bq-detail-label">Turn in:</span> ' + esc(q.turnInName) + '</div>';
                html += '<div><span class="bq-detail-label">Level:</span> ' + q.questLevel + ' (min ' + q.minLevel + ')</div>';
                html += '<div><span class="bq-detail-label">Status:</span> ' + statusText + ' (DB status=' + q.status + ', rewarded=' + q.rewarded + ')</div>';

                // Detailed objective progress bars
                for (var m = 0; m < 4; m++) {
                    if (q.mobRequired[m] > 0) {
                        var pct = Math.min(100, Math.round(q.mobCounts[m] / q.mobRequired[m] * 100));
                        var barColor = pct >= 100 ? '#9ece6a' : '#7aa2f7';
                        html += '<div class="bq-obj-row">' +
                            '<span>Kill slot ' + (m + 1) + ': ' + q.mobCounts[m] + '/' + q.mobRequired[m] + '</span>' +
                            '<div class="bq-obj-bar"><div class="bq-obj-fill" style="width:' + pct + '%;background:' + barColor + ';"></div></div>' +
                            '</div>';
                    }
                }
                for (var it = 0; it < 4; it++) {
                    if (q.itemRequired[it] > 0) {
                        var pct = Math.min(100, Math.round(q.itemCounts[it] / q.itemRequired[it] * 100));
                        var barColor = pct >= 100 ? '#9ece6a' : '#e0af68';
                        html += '<div class="bq-obj-row">' +
                            '<span>Item slot ' + (it + 1) + ': ' + q.itemCounts[it] + '/' + q.itemRequired[it] + '</span>' +
                            '<div class="bq-obj-bar"><div class="bq-obj-fill" style="width:' + pct + '%;background:' + barColor + ';"></div></div>' +
                            '</div>';
                    }
                }

                if (q.prevQuestId !== 0)
                    html += '<div><span class="bq-detail-label">Requires:</span> Quest #' + Math.abs(q.prevQuestId) + (q.prevQuestId > 0 ? ' (rewarded)' : ' (active)') + '</div>';
                if (q.exclusiveGroup !== 0)
                    html += '<div><span class="bq-detail-label">Exclusive Group:</span> ' + q.exclusiveGroup + ' — only one from this group can be active/completed</div>';

                html += '</div>';
            }
            html += '</div>';
        }

        $body.html(html);
    }

    // Toggle quest detail on click
    $(document).on('click', '.bq-quest-row', function () {
        var detId = $(this).data('detail');
        var $det = $('#' + detId);
        var wasVisible = $det.hasClass('visible');
        // Collapse all in this zone group, then toggle this one
        $(this).closest('.bq-zone-group').find('.bq-detail').removeClass('visible');
        $(this).closest('.bq-zone-group').find('.bq-quest-row').removeClass('expanded');
        if (!wasVisible) {
            $det.addClass('visible');
            $(this).addClass('expanded');
        }
    });

    // ===================== INIT =====================
    // Inject stack count badge styles
    $('<style>')
        .text(
            '.bt-inv-icon-wrap { position: relative; display: inline-block; }' +
            '.bt-inv-count { position: absolute; bottom: 0; right: 0; background: rgba(0,0,0,0.8);' +
            '  color: #fff; font-size: 10px; font-weight: 700; line-height: 1; padding: 1px 3px;' +
            '  border-radius: 3px; min-width: 14px; text-align: center; pointer-events: none; }'
        )
        .appendTo('head');

    // Live tab + detail tab styles
    $('<style>')
        .text(
            '.bt-detail-tabs { display:flex;gap:0;border-bottom:1px solid var(--border-light,#414868);position:sticky;top:0;background:var(--bg-card,#1a1b26);z-index:2; }' +
            '.bt-detail-tab { padding:9px 18px;font-size:12px;font-weight:600;color:var(--text-muted,#5f6b7a);cursor:pointer;border-bottom:2px solid transparent;text-transform:uppercase;letter-spacing:0.5px;user-select:none; }' +
            '.bt-detail-tab:hover { color:var(--text-secondary,#a9b1d6); }' +
            '.bt-detail-tab.active { color:var(--accent,#7aa2f7);border-bottom-color:var(--accent,#7aa2f7); }' +
            '.bt-live-dot { display:inline-block;width:6px;height:6px;border-radius:50%;background:#9ece6a;margin-left:6px;vertical-align:middle;animation:btLivePulse 1.6s ease-in-out infinite; }' +
            '@keyframes btLivePulse { 0%,100%{opacity:0.35;} 50%{opacity:1;} }' +
            '#detailTabBody { padding:14px 16px; }' +
            '.bt-live-banner { padding:10px 12px;background:var(--bg-card-alt,#24283b);border-radius:6px;margin-bottom:10px; }' +
            '.bt-live-goal { font-size:17px;font-weight:800;letter-spacing:0.3px; }' +
            '.bt-live-step { font-size:14px;font-weight:500;color:var(--text-secondary,#a9b1d6); }' +
            '.bt-live-why { font-family:ui-monospace,Menlo,Consolas,monospace;font-size:11px;color:var(--text-muted,#5f6b7a);margin-top:3px; }' +
            '.bt-live-badges { margin-top:7px; }' +
            '.bt-live-stats { display:flex;gap:8px;margin-bottom:10px;flex-wrap:wrap; }' +
            '.bt-live-stat { flex:1;min-width:64px;background:var(--bg-card-alt,#24283b);border-radius:6px;padding:7px 4px;text-align:center; }' +
            '.bt-live-stat-val { font-size:16px;font-weight:800;line-height:1.1; }' +
            '.bt-live-stat-lbl { font-size:9px;text-transform:uppercase;letter-spacing:0.5px;color:var(--text-muted,#5f6b7a);margin-top:2px; }' +
            '.bt-live-card { background:var(--bg-card-alt,#24283b);border:1px solid var(--border-light,#414868);border-radius:6px;padding:9px 11px;margin-bottom:9px; }' +
            '.bt-live-card-h { font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:0.5px;color:var(--text-secondary,#a9b1d6);margin-bottom:7px; }' +
            '.bt-live-card-h i { color:var(--accent,#7aa2f7);margin-right:5px; }' +
            '.bt-live-wait { font-family:ui-monospace,Menlo,Consolas,monospace;font-size:12px;line-height:1.5; }' +
            '.bt-live-wait-cmd { color:var(--text-primary,#c0caf5);font-weight:700; }' +
            '.bt-live-wait-evt { color:#7aa2f7;font-weight:700; }' +
            '.bt-live-rows { display:flex;flex-direction:column;gap:3px; }' +
            '.bt-live-row { display:flex;justify-content:space-between;font-size:12px; }' +
            '.bt-live-row-lbl { color:var(--text-muted,#5f6b7a); }' +
            '.bt-live-row-val { font-family:ui-monospace,Menlo,Consolas,monospace;color:var(--text-primary,#c0caf5); }' +
            '.bt-live-chips, .bt-live-badges { display:flex;flex-wrap:wrap;gap:4px; }' +
            '.bt-live-qchip { font-family:ui-monospace,Menlo,Consolas,monospace;font-size:11px;font-weight:700;padding:2px 8px;border-radius:10px; }' +
            '.bt-live-active { background:rgba(122,162,247,0.06);border:1px solid rgba(122,162,247,0.25);border-radius:5px;padding:8px 10px;margin-bottom:8px; }' +
            '.bt-live-active-t { font-size:13px;font-weight:700;color:var(--text-primary,#c0caf5);margin-bottom:6px; }' +
            '.bt-live-obj { font-size:12px;line-height:1.7;display:flex;align-items:center;gap:2px;flex-wrap:wrap; }' +
            '.bt-live-obj i { color:var(--text-muted,#5f6b7a); }' +
            '.bt-live-obj.active { background:rgba(224,175,104,0.10);border-radius:4px;padding:1px 5px;margin:1px -5px; }' +
            '.bt-live-obj.active i { color:#e0af68; }' +
            '.bt-live-obj-cnt { font-family:ui-monospace,Menlo,Consolas,monospace;font-weight:700; }' +
            '.bt-live-obj-name { color:var(--text-secondary,#a9b1d6); }' +
            '.bt-live-obj-dist { margin-left:auto;font-family:ui-monospace,Menlo,Consolas,monospace;font-size:11px;color:#7aa2f7;padding-left:8px; }' +
            '.bt-live-qlist { display:flex;flex-direction:column;gap:1px; }' +
            '.bt-live-qrow { display:flex;align-items:center;gap:6px;font-size:12px;padding:2px 4px;border-radius:3px; }' +
            '.bt-live-qrow.active { background:rgba(115,218,202,0.10); }' +
            '.bt-live-qid { font-family:ui-monospace,Menlo,Consolas,monospace;color:var(--text-muted,#5f6b7a);font-size:11px; }' +
            '.bt-live-qtitle { color:var(--text-secondary,#a9b1d6);white-space:nowrap;overflow:hidden;text-overflow:ellipsis; }' +
            '.bt-live-log { max-height:240px;overflow-y:auto;font-family:ui-monospace,Menlo,Consolas,monospace;font-size:11px;line-height:1.5;background:#000;border:1px solid var(--border-light,#414868);border-radius:4px;padding:6px 8px; }' +
            '.bt-live-logline { white-space:pre-wrap;word-break:break-word; }' +
            '.bt-live-logt { color:var(--text-muted,#5f6b7a);margin-right:6px; }' +
            '.bt-live-foot { font-size:10px;color:var(--text-muted,#5f6b7a);text-align:right;margin-top:2px; }' +
            '.bt-live-obj-srv { font-family:ui-monospace,Menlo,Consolas,monospace;font-size:9px;font-weight:700;color:#73daca;background:rgba(115,218,202,0.12);padding:0 4px;border-radius:3px;letter-spacing:0.5px; }' +
            '.bt-live-fleet-tag { font-size:9px;font-weight:700;color:#bb9af7;background:rgba(187,154,247,0.14);padding:0 4px;border-radius:3px;margin-right:4px;letter-spacing:0.5px; }' +
            '.bt-story { background:linear-gradient(180deg,rgba(122,162,247,0.08),rgba(122,162,247,0.02));border-color:rgba(122,162,247,0.30); }' +
            '.bt-story-lead { font-size:13px;line-height:1.55;color:var(--text-primary,#c0caf5); }' +
            '.bt-story-why { font-size:12px;line-height:1.55;color:var(--text-secondary,#a9b1d6);margin-top:5px; }' +
            '.bt-story-next { font-size:12px;line-height:1.5;color:var(--text-secondary,#a9b1d6);margin-top:7px; }' +
            '.bt-story-next-h { color:#bb9af7;font-weight:700;text-transform:uppercase;font-size:10px;letter-spacing:0.6px;margin-right:6px; }' +
            '.bt-story b { color:var(--text-primary,#c0caf5);font-weight:700; }' +
            '.bt-live-wait-dest { color:var(--text-primary,#c0caf5);font-weight:700; }' +
            '.bt-report-btn { float:right;font-size:10px;font-weight:700;cursor:pointer;background:rgba(187,154,247,0.12);border:1px solid rgba(187,154,247,0.35);border-radius:4px;color:#bb9af7;padding:1px 8px;text-transform:none;letter-spacing:0; }' +
            '.bt-report-btn:hover { background:rgba(187,154,247,0.22); }' +
            '.bt-report-btn i { color:#bb9af7;margin-right:3px; }' +
            '.br-overlay { position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.6);z-index:5500;display:none;align-items:center;justify-content:center; }' +
            '.br-overlay.active { display:flex; }' +
            '.br-modal { background:var(--bg-card,#1a1b26);border:1px solid var(--border-light,#414868);border-radius:10px;width:90vw;max-width:560px;max-height:86vh;display:flex;flex-direction:column;box-shadow:0 16px 48px rgba(0,0,0,0.55);overflow:hidden; }' +
            '.br-header { display:flex;align-items:center;justify-content:space-between;padding:12px 16px;border-bottom:1px solid var(--border-light,#414868);font-weight:700;font-size:14px;color:var(--text-primary,#c0caf5); }' +
            '.br-header i { color:#bb9af7;margin-right:6px; }' +
            '.br-close { background:none;border:none;color:var(--text-muted,#5f6b7a);font-size:16px;cursor:pointer; }' +
            '.br-close:hover { color:var(--text-primary,#c0caf5); }' +
            '.br-body { padding:14px 16px;overflow-y:auto; }' +
            '.br-loading { text-align:center;padding:34px;color:var(--text-muted,#5f6b7a);font-size:13px; }' +
            '.br-meta { font-size:11px;color:var(--text-muted,#5f6b7a);margin-bottom:10px;font-family:ui-monospace,Menlo,Consolas,monospace; }' +
            '.br-health { display:flex;gap:14px;align-items:center;flex-wrap:wrap;background:rgba(115,218,202,0.06);border:1px solid rgba(115,218,202,0.25);border-radius:6px;padding:9px 12px;margin-bottom:12px;font-size:12px;color:var(--text-secondary,#a9b1d6); }' +
            '.br-health.bad { background:rgba(247,118,142,0.07);border-color:rgba(247,118,142,0.35); }' +
            '.br-health b { color:var(--text-primary,#c0caf5); }' +
            '.br-flag { font-size:10px;font-weight:700;color:#ff9e64;background:rgba(255,158,100,0.15);padding:1px 8px;border-radius:10px; }' +
            '.br-grid { display:grid;grid-template-columns:repeat(4,1fr);gap:7px;margin-bottom:14px; }' +
            '.br-cell { background:var(--bg-card-alt,#24283b);border:1px solid var(--border-light,#414868);border-radius:6px;padding:8px 4px;text-align:center; }' +
            '.br-cell-n { font-size:18px;font-weight:800;line-height:1.1;font-family:ui-monospace,Menlo,Consolas,monospace; }' +
            '.br-cell-l { font-size:9px;text-transform:uppercase;letter-spacing:0.4px;color:var(--text-muted,#5f6b7a);margin-top:3px; }' +
            '.br-sec-h { font-size:11px;font-weight:700;text-transform:uppercase;letter-spacing:0.5px;color:var(--text-secondary,#a9b1d6);margin:4px 0 7px; }' +
            '.br-top { display:flex;flex-direction:column;gap:2px; }' +
            '.br-top-row { display:flex;gap:8px;align-items:baseline;font-family:ui-monospace,Menlo,Consolas,monospace;font-size:11px; }' +
            '.br-top-n { min-width:30px;text-align:right;color:#bb9af7;font-weight:700; }' +
            '.br-top-sig { color:var(--text-secondary,#a9b1d6);white-space:nowrap;overflow:hidden;text-overflow:ellipsis; }'
        )
        .appendTo('head');

    initConnection();
    setInterval(updateStats, 5000);
    startRosterPoll();

    // --- cockpit boot -------------------------------------------------------------------
    startFleetPoll();                                             // zone/goal enrichment
    $('#botRoster').on('scroll', function () {
        if (rosterWinTick) return;
        rosterWinTick = true;
        requestAnimationFrame(function () { rosterWinTick = false; renderRosterWindow(); });
    });
    setInterval(flushDirtyCards, 250);                            // coalesced card repaints
    setInterval(rosterResortIfNeeded, 3000);                      // debounced re-sort + census
    setInterval(refreshGroupingStatus, 15000);                    // keeps group badges + census live
    $('#rosterSort').val(rosterSort);
    $('#rosterGroup').val(rosterGroupBy);
    if (localStorage.getItem('msui_bots_cockpit') === '0') {
        $('.bt-page').addClass('cockpit-off');
        $('#cockpitToggle i').attr('class', 'fa-solid fa-chevron-down');
    }
    renderCockpit();

    // ===================== ITEM TOOLTIP =====================
    // Floating tooltip that appears on hover over inventory items

    var $tooltip = $('<div id="itemTooltip"></div>').css({
        position: 'fixed',
        display: 'none',
        zIndex: 9999,
        pointerEvents: 'none',
        background: 'linear-gradient(135deg, #1a1b26 0%, #24283b 100%)',
        border: '1px solid #414868',
        borderRadius: '6px',
        padding: '10px 14px',
        maxWidth: '280px',
        fontSize: '12px',
        lineHeight: '1.5',
        color: '#c0caf5',
        boxShadow: '0 8px 24px rgba(0,0,0,0.6)'
    }).appendTo('body');

    $(document).on('mouseenter', '.bt-inv-item', function (e) {
        var $el = $(this);
        var name = $el.data('tt-name') || '';
        var quality = parseInt($el.data('tt-quality')) || 0;
        var itemClass = parseInt($el.data('tt-class')) || 0;
        var subclass = parseInt($el.data('tt-subclass')) || 0;
        var invType = parseInt($el.data('tt-invtype')) || 0;
        var iLvl = parseInt($el.data('tt-ilvl')) || 0;
        var armor = parseInt($el.data('tt-armor')) || 0;
        var sellPrice = parseInt($el.data('tt-sell')) || 0;
        var isEquipped = $el.data('tt-equipped') === true || $el.data('tt-equipped') === 'true';
        var count = parseInt($el.data('tt-count')) || 1;

        var qColor = QUALITY_COLORS[quality] || '#fff';
        var qName = QUALITY_NAMES[quality] || '';

        var html = '<div style="font-size:14px;font-weight:700;color:' + qColor + ';margin-bottom:4px;">' + esc(name) + '</div>';

        // Stack count
        if (count > 1) html += '<div style="color:#c0caf5;">Count: ' + count + '</div>';

        // Item level
        if (iLvl > 0) html += '<div style="color:#e0af68;">Item Level ' + iLvl + '</div>';

        // Slot + type
        var slotName = EQUIP_SLOT_NAMES[invType] || '';
        var className = ITEM_CLASS_NAMES[itemClass] || '';
        if (slotName || className) {
            html += '<div style="display:flex;justify-content:space-between;color:var(--text-muted);">';
            if (slotName) html += '<span>' + slotName + '</span>';
            if (className) html += '<span>' + className + '</span>';
            html += '</div>';
        }

        // Armor
        if (armor > 0) html += '<div style="color:#c0caf5;">' + armor + ' Armor</div>';

        // Quality
        html += '<div style="color:' + qColor + ';font-size:11px;margin-top:4px;">' + qName + '</div>';

        // Sell price (total for stack)
        if (sellPrice > 0) {
            var totalSell = sellPrice * count;
            html += '<div style="color:var(--text-muted);font-size:11px;">Sell: ' + formatGold(totalSell) + (count > 1 ? ' (' + formatGold(sellPrice) + ' each)' : '') + '</div>';
        }

        if (isEquipped) html += '<div style="color:#9ece6a;font-size:11px;margin-top:2px;">Equipped</div>';

        $tooltip.html(html).show();
        positionTooltip(e);
    });

    $(document).on('mousemove', '.bt-inv-item', function (e) {
        positionTooltip(e);
    });

    $(document).on('mouseleave', '.bt-inv-item', function () {
        $tooltip.hide();
    });

    function positionTooltip(e) {
        var x = e.clientX + 16;
        var y = e.clientY + 12;
        var tw = $tooltip.outerWidth();
        var th = $tooltip.outerHeight();
        if (x + tw > window.innerWidth - 10) x = e.clientX - tw - 12;
        if (y + th > window.innerHeight - 10) y = e.clientY - th - 12;
        $tooltip.css({ left: x + 'px', top: y + 'px' });
    }
    // ===================== CONTROL SUITE (modal) =====================
    // Everything that used to live in the page-wide command bar now lives here, scoped to one
    // bot (or broadcast to the whole fleet). Every action posts to the existing BotsController
    // REST endpoints — no new server code, no hub method assumptions.

    var bcGuid = 0;            // 0 = fleet mode (no single bot selected)
    var bcBroadcast = false;   // true = apply to every connected bot

    function bcTargets() {
        if (bcGuid > 0 && !bcBroadcast) return [bcGuid];
        var out = [];
        Object.keys(botStates).forEach(function (g) {
            var s = botStates[g];
            if (s && s.taskState !== 'DISCONNECTED') out.push(parseInt(g, 10));
        });
        return out;
    }

    function bcTargetLabel() {
        var t = bcTargets();
        if (t.length === 1 && botStates[t[0]]) return botStates[t[0]].name;
        return t.length + ' bots';
    }

    // Fire one POST per target and report a single rolled-up toast.
    function bcSend(url, build, verb) {
        var targets = bcTargets();
        if (!targets.length) { showToast('No target bots', true); return; }
        var done = 0, failed = 0, firstErr = null;
        targets.forEach(function (g) {
            $.ajax({ url: url, type: 'POST', contentType: 'application/json', data: JSON.stringify(build(g)) })
                .done(function (r) {
                    if (r && r.success === false) { failed++; if (!firstErr) firstErr = r.error; }
                    else done++;
                })
                .fail(function (x) { failed++; if (!firstErr) firstErr = 'HTTP ' + x.status; })
                .always(function () {
                    if (done + failed !== targets.length) return;
                    if (failed) showToast(verb + ' — ' + done + ' ok, ' + failed + ' failed' + (firstErr ? ' (' + firstErr + ')' : ''), true);
                    else showToast(verb + ' \u2192 ' + (targets.length === 1 ? bcTargetLabel() : targets.length + ' bots'));
                });
        });
    }

    function bcCard(icon, title, body, note) {
        return '<div class="bc-card"><div class="bc-card-h"><i class="fa-solid ' + icon + '"></i>' + esc(title) +
            (note ? '<span class="bc-note">' + esc(note) + '</span>' : '') + '</div>' +
            '<div class="bc-card-b">' + body + '</div></div>';
    }

    function bcNum(id, ph, val, w) {
        return '<input type="number" class="form-input bc-in" id="' + id + '" placeholder="' + esc(ph) + '" value="' +
            (val == null ? '' : val) + '" style="width:' + (w || 82) + 'px;" />';
    }

    function bcBtn(id, icon, label, kind) {
        return '<button class="bc-btn' + (kind ? ' ' + kind : '') + '" id="' + id + '"><i class="fa-solid ' + icon + '"></i>' + esc(label) + '</button>';
    }

    function renderControlTab(guid) {
        bcGuid = parseInt(guid, 10) || 0;
        if (bcGuid === 0) bcBroadcast = true;
        var s = botStates[bcGuid];
        var html = '';

        // --- target strip -------------------------------------------------------------
        var tgt = '<div class="bc-target">';
        tgt += '<span class="bc-target-l">Apply to</span>';
        if (bcGuid > 0 && s) {
            tgt += '<label class="bc-radio"><input type="radio" name="bcScope" value="one"' + (bcBroadcast ? '' : ' checked') + '> ' +
                esc(s.name) + '</label>';
        }
        tgt += '<label class="bc-radio"><input type="radio" name="bcScope" value="all"' + (bcBroadcast ? ' checked' : '') + '> ' +
            'all connected bots</label>';
        tgt += '<span class="bc-target-n" id="bcTargetCount">' + bcTargets().length + ' target(s)</span>';
        tgt += '</div>';
        html += tgt;

        if (bcGuid > 0 && s) {
            var hp = botHpPct(bcGuid);
            var act = botActivity(bcGuid);
            html += '<div class="bc-vitals">' +
                '<span><b>L' + (s.level || 0) + '</b> ' + esc(CLASS_NAMES[s.classId] || '?') + '</span>' +
                '<span>' + esc(zoneKeyLabel(botZoneKey(bcGuid))) + '</span>' +
                '<span>map ' + (s.mapId || 0) + ' @ ' + Math.round(s.x || 0) + ', ' + Math.round(s.y || 0) + ', ' + Math.round(s.z || 0) + '</span>' +
                '<span style="color:' + (hp < 35 ? '#f7768e' : '#9ece6a') + ';">HP ' + hp + '%</span>' +
                '<span style="color:#e0af68;">' + formatGold(s.copper || 0) + '</span>' +
                '<span class="bt-roster-activity ' + act.cls + '">' + esc(act.text) + '</span>' +
                (s.isDead ? '<span style="color:#f7768e;font-weight:700;">DEAD</span>' : '') +
                (s.inCombat ? '<span style="color:#f7768e;font-weight:700;">IN COMBAT</span>' : '') +
                '</div>';
        }

        html += '<div class="bc-grid">';

        // --- movement -----------------------------------------------------------------
        var mv = '<div class="bc-row">' +
            bcNum('bcMap', 'map', s ? (s.mapId || 0) : 0, 62) +
            bcNum('bcX', 'X', s ? Math.round(s.x || 0) : '', 92) +
            bcNum('bcY', 'Y', s ? Math.round(s.y || 0) : '', 92) +
            bcNum('bcZ', 'Z', s ? Math.round(s.z || 0) : '', 92) +
            bcBtn('bcMoveTo', 'fa-location-arrow', 'Move to', 'primary') +
            '</div>' +
            '<div class="bc-row">' +
            '<select class="form-input bc-in" id="bcGotoBot" data-botlist style="width:200px;"><option value="0">-- copy coords from bot --</option></select>' +
            bcBtn('bcMoveToBot', 'fa-people-arrows', 'Send to that bot') +
            (bcGuid > 0 ? bcBtn('bcFillSelf', 'fa-crosshairs', 'Reset to current position') : '') +
            '</div>';
        html += bcCard('fa-person-walking', 'Movement', mv, 'MOVE_TO — the bot walks, it does not teleport');

        // --- grind task ---------------------------------------------------------------
        var gr = '<div class="bc-row">' +
            bcNum('bcGrindX', 'X', s ? Math.round(s.x || 0) : '', 92) +
            bcNum('bcGrindY', 'Y', s ? Math.round(s.y || 0) : '', 92) +
            bcNum('bcGrindZ', 'Z', s ? Math.round(s.z || 0) : '', 92) +
            bcNum('bcGrindR', 'radius', 60, 78) +
            '</div><div class="bc-row">' +
            bcNum('bcGrindEntry', 'creature entry', '', 130) +
            bcNum('bcGrindKills', 'kills', 10, 74) +
            bcBtn('bcSetGrind', 'fa-skull', 'Set grind task', 'primary') +
            '</div>';
        html += bcCard('fa-skull-crossbones', 'Grind task', gr, 'SET_TASK_GRIND — entry 0 = anything in radius');

        // --- chat ---------------------------------------------------------------------
        var ch = '<div class="bc-row">' +
            '<input type="text" class="form-input bc-in" id="bcText" placeholder="text to say..." style="flex:1;min-width:220px;" />' +
            '<select class="form-input bc-in" id="bcChatType" style="width:120px;">' +
            '<option value="0">Say (0)</option><option value="6">Yell (6)</option><option value="custom">Custom...</option>' +
            '</select>' +
            bcNum('bcChatCustom', 'type', 0, 70) +
            bcBtn('bcSay', 'fa-paper-plane', 'Send', 'primary') +
            '</div>';
        html += bcCard('fa-comment', 'Chat', ch, 'SAY_TEXT — chat type is the raw server enum, unchanged from the old command bar');

        // --- quests -------------------------------------------------------------------
        var qs = '<div class="bc-row">' +
            bcNum('bcQuestId', 'quest id', '', 110) +
            bcBtn('bcQAccept', 'fa-plus', 'Accept') +
            bcBtn('bcQComplete', 'fa-check', 'Complete') +
            bcBtn('bcQAbandon', 'fa-xmark', 'Abandon', 'danger') +
            '</div>';
        html += bcCard('fa-scroll', 'Quests', qs);

        // --- spells + targeting -------------------------------------------------------
        var sp = '<div class="bc-row">' +
            bcNum('bcSpellId', 'spell id', '', 110) + bcBtn('bcLearn', 'fa-book', 'Learn spell') +
            '</div><div class="bc-row">' +
            bcNum('bcTargetGuid', 'target guid', '', 120) +
            bcBtn('bcAttack', 'fa-crosshairs', 'Attack', 'danger') +
            bcBtn('bcInteract', 'fa-hand-point-up', 'Interact') +
            '</div>';
        html += bcCard('fa-wand-sparkles', 'Spells & targeting', sp);

        // --- flight -------------------------------------------------------------------
        var fl = '<div class="bc-row">' +
            bcNum('bcFlySrc', 'source node', '', 110) + bcNum('bcFlyDst', 'dest node', '', 110) +
            bcBtn('bcTakeFlight', 'fa-plane', 'Take flight') +
            '</div>';
        html += bcCard('fa-plane-departure', 'Flight path', fl, 'TAKE_FLIGHT — taxi node ids');

        // --- grouping -----------------------------------------------------------------
        var grp = '';
        if (bcGuid > 0) {
            grp += '<div class="bc-row"><span class="bc-lbl">Leader: <b>' + esc(s ? s.name : ('#' + bcGuid)) + '</b></span></div>';
            grp += '<div class="bc-row"><select multiple class="form-input" id="bcFollowers" data-botlist data-exclude="' + bcGuid +
                '" style="min-width:240px;height:88px;"></select>' +
                bcBtn('bcFormGroup', 'fa-users', 'Form group', 'primary') +
                (groupOf[bcGuid] ? bcBtn('bcDisband', 'fa-users-slash', 'Disband group #' + groupOf[bcGuid], 'danger') : '') +
                '</div>';
        } else {
            grp += '<div class="bc-row">' + bcBtn('bcAutoForm', 'fa-users', 'Auto-form groups') + '</div>';
        }
        html += bcCard('fa-users', 'Grouping', grp, 'grouping mode must not be Off');

        // --- diagnostics ---------------------------------------------------------------
        var dg = '<div class="bc-row">' +
            bcBtn('bcConnect', 'fa-plug', 'Connect bot', 'primary') +
            bcBtn('bcTraceOn', 'fa-record-vinyl', 'Trace on') + bcBtn('bcTraceOff', 'fa-stop', 'Trace off') +
            bcBtn('bcStoryOn', 'fa-book-open', 'Story on') + bcBtn('bcStoryOff', 'fa-stop', 'Story off') +
            (bcGuid > 0 ? bcBtn('bcReport', 'fa-bolt', 'Bot report') : '') +
            '</div><div class="bc-row"><span class="bc-lbl" id="bcTraceState">trace: —</span></div>';
        html += bcCard('fa-microscope', 'Diagnostics', dg, 'per-guid flight recorder + causal story log');

        html += '</div>';

        $('#bmBody').html(html);
        updateBotDropdown();
        $('#bcChatCustom').hide();
        refreshTraceState();
    }

    function refreshTraceState() {
        $.getJSON('/Bots/TraceStatus', function (d) {
            if (!d) return;
            var targets = d.targets || [];
            var mine = bcGuid > 0 ? (targets.indexOf(bcGuid) >= 0) : false;
            $('#bcTraceState').text('trace: ' + (d.enabled ? 'enabled' : 'disabled') +
                ' \u00b7 ' + targets.length + ' target(s)' + (bcGuid > 0 ? (mine ? ' \u00b7 this bot IS traced' : ' \u00b7 this bot is not traced') : ''));
        }).fail(function () { $('#bcTraceState').text('trace: status unavailable'); });
    }

    // ---- control handlers (delegated — the modal body is rebuilt on every open)

    $(document).on('change', 'input[name="bcScope"]', function () {
        bcBroadcast = $(this).val() === 'all';
        $('#bcTargetCount').text(bcTargets().length + ' target(s)');
    });

    $(document).on('change', '#bcChatType', function () {
        if ($(this).val() === 'custom') $('#bcChatCustom').show(); else $('#bcChatCustom').hide();
    });

    $(document).on('click', '#bcMoveTo', function () {
        var m = parseInt($('#bcMap').val(), 10) || 0;
        var x = parseFloat($('#bcX').val()) || 0, y = parseFloat($('#bcY').val()) || 0, z = parseFloat($('#bcZ').val()) || 0;
        bcSend('/Bots/MoveTo', function (g) { return { guid: g, mapId: m, x: x, y: y, z: z }; }, 'MOVE_TO');
    });

    $(document).on('click', '#bcMoveToBot', function () {
        var other = parseInt($('#bcGotoBot').val(), 10) || 0;
        var t = botStates[other];
        if (!t) { showToast('Pick a bot to travel to', true); return; }
        $('#bcMap').val(t.mapId || 0); $('#bcX').val(Math.round(t.x || 0));
        $('#bcY').val(Math.round(t.y || 0)); $('#bcZ').val(Math.round(t.z || 0));
        bcSend('/Bots/MoveTo', function (g) { return { guid: g, mapId: t.mapId || 0, x: t.x, y: t.y, z: t.z }; }, 'MOVE_TO ' + t.name);
    });

    $(document).on('click', '#bcFillSelf', function () {
        var s = botStates[bcGuid]; if (!s) return;
        $('#bcMap').val(s.mapId || 0); $('#bcX').val(Math.round(s.x || 0));
        $('#bcY').val(Math.round(s.y || 0)); $('#bcZ').val(Math.round(s.z || 0));
    });

    $(document).on('click', '#bcSetGrind', function () {
        var p = {
            x: parseFloat($('#bcGrindX').val()) || 0,
            y: parseFloat($('#bcGrindY').val()) || 0,
            z: parseFloat($('#bcGrindZ').val()) || 0,
            radius: parseFloat($('#bcGrindR').val()) || 60,
            creatureEntry: parseInt($('#bcGrindEntry').val(), 10) || 0,
            killCount: parseInt($('#bcGrindKills').val(), 10) || 0
        };
        bcSend('/Bots/SetTaskGrind', function (g) {
            return { guid: g, x: p.x, y: p.y, z: p.z, radius: p.radius, creatureEntry: p.creatureEntry, killCount: p.killCount };
        }, 'SET_TASK_GRIND');
    });

    $(document).on('click', '#bcSay', function () {
        var text = $.trim($('#bcText').val() || '');
        if (!text) { showToast('Nothing to say', true); return; }
        var ct = $('#bcChatType').val();
        var chatType = ct === 'custom' ? (parseInt($('#bcChatCustom').val(), 10) || 0) : (parseInt(ct, 10) || 0);
        bcSend('/Bots/SayText', function (g) { return { guid: g, text: text, chatType: chatType }; }, 'SAY_TEXT');
        $('#bcText').val('');
    });
    $(document).on('keydown', '#bcText', function (e) { if (e.key === 'Enter') { e.preventDefault(); $('#bcSay').click(); } });

    function bcQuest(url, verb) {
        var qid = parseInt($('#bcQuestId').val(), 10) || 0;
        if (!qid) { showToast('Quest id required', true); return; }
        bcSend(url, function (g) { return { guid: g, questId: qid }; }, verb);
    }
    $(document).on('click', '#bcQAccept', function () { bcQuest('/Bots/AcceptQuest', 'ACCEPT_QUEST'); });
    $(document).on('click', '#bcQComplete', function () { bcQuest('/Bots/CompleteQuest', 'COMPLETE_QUEST'); });
    $(document).on('click', '#bcQAbandon', function () { bcQuest('/Bots/AbandonQuest', 'ABANDON_QUEST'); });

    $(document).on('click', '#bcLearn', function () {
        var sid = parseInt($('#bcSpellId').val(), 10) || 0;
        if (!sid) { showToast('Spell id required', true); return; }
        bcSend('/Bots/LearnSpell', function (g) { return { guid: g, spellId: sid }; }, 'LEARN_SPELL');
    });
    $(document).on('click', '#bcAttack', function () {
        var t = parseInt($('#bcTargetGuid').val(), 10) || 0;
        if (!t) { showToast('Target guid required', true); return; }
        bcSend('/Bots/AttackTarget', function (g) { return { guid: g, targetGuid: t }; }, 'ATTACK_TARGET');
    });
    $(document).on('click', '#bcInteract', function () {
        var t = parseInt($('#bcTargetGuid').val(), 10) || 0;
        if (!t) { showToast('Target guid required', true); return; }
        bcSend('/Bots/InteractNpc', function (g) { return { guid: g, targetGuid: t }; }, 'INTERACT_NPC');
    });
    $(document).on('click', '#bcTakeFlight', function () {
        var src = parseInt($('#bcFlySrc').val(), 10) || 0, dst = parseInt($('#bcFlyDst').val(), 10) || 0;
        if (!src || !dst) { showToast('Source + dest node required', true); return; }
        bcSend('/Bots/TakeFlight', function (g) { return { guid: g, sourceNode: src, destNode: dst }; }, 'TAKE_FLIGHT');
    });

    $(document).on('click', '#bcFormGroup', function () {
        var followers = ($('#bcFollowers').val() || []).map(function (v) { return parseInt(v, 10); });
        if (!followers.length) { showToast('Pick at least one follower', true); return; }
        $.ajax({
            url: '/Bots/FormGroup', type: 'POST', contentType: 'application/json',
            data: JSON.stringify({ leaderGuid: bcGuid, followerGuids: followers })
        }).done(function (r) {
            if (r && r.success) { showToast('Group #' + r.groupId + ' formed'); refreshGroupingStatus(); renderControlTab(bcGuid); }
            else showToast((r && r.error) || 'Formation failed', true);
        }).fail(function (x) { showToast('FormGroup failed (' + x.status + ')', true); });
    });
    $(document).on('click', '#bcDisband', function () {
        var gid = groupOf[bcGuid];
        if (!gid) return;
        window.disbandGroup(gid);
        setTimeout(function () { renderControlTab(bcGuid); }, 400);
    });
    $(document).on('click', '#bcAutoForm', function () { $('#autoFormGroups').click(); });

    function bcDiag(url, enabled, verb) {
        var targets = bcTargets();
        $.ajax({ url: url, type: 'POST', contentType: 'application/json', data: JSON.stringify({ enabled: enabled, guids: targets }) })
            .done(function () { showToast(verb + ' \u2192 ' + targets.length + ' bot(s)'); refreshTraceState(); })
            .fail(function (x) { showToast(verb + ' failed (' + x.status + ')', true); });
    }
    $(document).on('click', '#bcTraceOn', function () { bcDiag('/Bots/SetTrace', true, 'trace on'); });
    $(document).on('click', '#bcTraceOff', function () { bcDiag('/Bots/SetTrace', false, 'trace off'); });
    $(document).on('click', '#bcStoryOn', function () { bcDiag('/Bots/SetStory', true, 'story on'); });
    $(document).on('click', '#bcStoryOff', function () { bcDiag('/Bots/SetStory', false, 'story off'); });
    $(document).on('click', '#bcConnect', function () {
        if (!bcGuid) {
            showToast('No bot selected', true);
            return;
        }

        var bot = botStates[bcGuid];

        if (!bot || !bot.name) {
            console.log('Missing bot state:', bcGuid, botStates);
            showToast('Cannot determine bot name', true);
            return;
        }

        if (!confirm('Connect this bot to the world server?')) {
            return;
        }

        var $btn = $(this).prop('disabled', true);

        $.ajax({
            url: '/Bots/Connect',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({
                name: bot.name
            })
        })
            .done(function (r) {
                if (r && r.success) {
                    showToast('Connect requested: ' + bot.name);
                } else {
                    showToast('Connect failed: ' + ((r && r.error) || 'unknown'), true);
                }
            })
            .fail(function (xhr) {
                showToast('Connect failed (' + xhr.status + ')', true);
            })
            .always(function () {
                $btn.prop('disabled', false);
            });
    });
    $(document).on('click', '#bcReport', function () {
        var st = bcGuid ? botStates[bcGuid] : null;
        if (!st || !st.name) return;
        $('#brTitle').html('<i class="fa-solid fa-bolt"></i> Report \u2014 ' + esc(st.name));
        $('#brBody').html('<div class="br-loading"><i class="fa-solid fa-spinner fa-spin"></i> reading buffered log\u2026</div>');
        $('#botReportModal').addClass('active');
        $.getJSON('/Bots/BotReport', { name: st.name }, function (data) {
            if (!data || data.error) { $('#brBody').html('<div class="br-loading">' + esc((data && data.error) || 'no data') + '</div>'); return; }
            $('#brBody').html(renderBotReport(data));
        }).fail(function () { $('#brBody').html('<div class="br-loading">request failed</div>'); });
    });

    // Open the control suite for a bot (0 = fleet-wide) — the roster button, the detail-panel
    // button and the header "Fleet control" button all land here.
    function openBotControl(guid) {
        openBotModal(guid, 'control');
    }
    $(document).on('click', '.btnBotControl', function (e) {
        e.stopPropagation();
        openBotControl(parseInt($(this).data('guid'), 10) || 0);
    });
    $(document).on('click', '#btnFleetControl', function () { openBotControl(0); });

    // ===================== MODAL: GEAR + BRAIN TABS =====================

    function renderGearTab(guid) {
        var $b = $('#bmBody');
        if (inventoryCache[guid]) { $b.html(inventoryHtml(inventoryCache[guid])); return; }
        $b.html('<div class="bq-loading"><i class="fa-solid fa-spinner fa-spin"></i> Loading inventory...</div>');
        $.getJSON('/Bots/Inventory', { guid: guid }, function (data) {
            if (!data || data.error) { $b.html('<div class="bq-loading">' + esc((data && data.error) || 'no data') + '</div>'); return; }
            inventoryCache[guid] = data;
            $b.html(inventoryHtml(data));
        }).fail(function () { $b.html('<div class="bq-loading">Failed to load inventory</div>'); });
    }

    function renderBrainTab(guid) {
        var $b = $('#bmBody');
        var brain = botBrains[guid];
        var html = '';

        $.getJSON('/Bots/LiveState/' + guid, function (d) {
            var live = '';
            if (d && !d.error) {
                live += '<div class="bc-card"><div class="bc-card-h"><i class="fa-solid fa-satellite-dish"></i>Live spine</div><div class="bc-card-b">' +
                    '<div class="bc-row"><b style="font-size:15px;color:' + (GOAL_COLOR[d.goal] || 'var(--accent)') + ';">' + esc(d.goal || '') + '</b>' +
                    '<span style="color:var(--text-secondary);">/ ' + esc(d.step || '') + '</span></div>' +
                    (d.why ? '<div class="bc-row" style="font-family:ui-monospace,Consolas,monospace;font-size:11px;color:var(--text-muted);">why = ' + esc(d.why) + '</div>' : '') +
                    '<div class="bc-row">' +
                    '<span class="bc-lbl">zone ' + (d.zoneId || 0) + ' \u00b7 map ' + (d.mapId || 0) + '</span>' +
                    '<span class="bc-lbl">HP ' + (d.hpPct || 0) + '%</span>' +
                    '<span class="bc-lbl">durability ' + (d.durability != null ? d.durability + '%' : '—') + '</span>' +
                    '<span class="bc-lbl">free bags ' + (d.freeSlots != null ? d.freeSlots : '—') + '</span>' +
                    '</div>' +
                    (d.pending ? '<div class="bc-row"><span class="bc-lbl">outstanding: <b>' + esc(d.pending.cmd) + '</b> waiting ' + esc(d.pending.expect) + ' (' + (d.pending.ageSec || 0) + 's)</span></div>' : '<div class="bc-row"><span class="bc-lbl">no outstanding command</span></div>') +
                    (d.failure ? '<div class="bc-row" style="color:#f7768e;">last failure: ' + esc(d.failure.cmd) + ' \u2190 ' + esc(d.failure.reason) + '</div>' : '') +
                    (d.stall ? '<div class="bc-row" style="color:#f7768e;font-weight:700;">STALLED — ' + esc(d.stall.reason) + '</div>' : '') +
                    '</div></div>';
            } else {
                live = '<div class="bc-card"><div class="bc-card-b" style="color:var(--text-muted);font-size:12px;">No live context — the brain engine is off for this bot.</div></div>';
            }
            $('#bmBrainLive').html(live);
        });

        html += '<div id="bmBrainLive"><div class="bq-loading"><i class="fa-solid fa-spinner fa-spin"></i> reading live context…</div></div>';

        if (brain && brain.personality) {
            var p = brain.personality;
            var traits = ['patience', 'greed', 'curiosity', 'sociability', 'aggression', 'efficiency', 'cautiousness', 'indecisiveness', 'spontaneity'];
            var t = '';
            for (var i = 0; i < traits.length; i++) {
                var k = traits[i], meta = TRAIT_META[k] || { icon: 'fa-circle', color: '#888' }, pct = Math.round((p[k] || 0) * 100);
                t += '<div class="bt-trait"><span class="bt-trait-icon"><i class="fa-solid ' + meta.icon + '" style="color:' + meta.color + ';"></i></span>' +
                    '<span class="bt-trait-label">' + capitalize(k) + '</span>' +
                    '<div class="bt-trait-bar-track"><div class="bt-trait-bar-fill" style="width:' + pct + '%;background:' + meta.color + ';"></div></div>' +
                    '<span class="bt-trait-val">' + pct + '</span></div>';
            }
            if (p.quirks && p.quirks.length) {
                t += '<div style="margin-top:10px;">';
                for (var qi = 0; qi < p.quirks.length; qi++)
                    t += '<span class="bt-quirk" title="' + esc(p.quirks[qi].description || '') + '"><i class="fa-solid fa-star" style="font-size:9px;"></i> ' + esc(p.quirks[qi].name) + '</span>';
                t += '</div>';
            }
            html += bcCard('fa-fingerprint', 'Personality', t, p.chatStyle + ' / ' + p.temperament);
        }

        if (brain && brain.lastDecision && brain.lastDecision.weights) {
            html += bcCard('fa-scale-balanced', 'Decision weights', '<div class="bt-weights">' + renderWeightsHtml(brain.lastDecision.weights) + '</div>');
        }

        var entries = decisionLog[guid] || [];
        if (entries.length) {
            var log = '<div class="bt-timeline" style="max-height:220px;">';
            for (var e = Math.max(0, entries.length - 40); e < entries.length; e++) {
                var en = entries[e];
                log += '<div class="' + (en.activityChanged ? 'bt-tl-switch' : 'bt-tl-stay') + '">[' +
                    new Date(en.timestamp).toLocaleTimeString() + '] ' + esc(en.decision) + '</div>';
            }
            log += '</div>';
            html += bcCard('fa-clock-rotate-left', 'Decision timeline', log);
        }

        $b.html(html);
    }
});

// MangosSuperUI — Game Objects Browser + Editor JS

$(function () {

    var currentPage = 1;
    var totalPages = 1;
    var currentPageSize = parseInt(localStorage.getItem('msui_go_pageSize')) || 50;
    var currentModels = {};
    var currentDetailEntry = null;
    var currentDetailObj = null;

    // Edit state
    var editMode = false;
    var editEntry = null;
    var editIsClone = false;
    var editIsBaseGame = false;
    var editSourceEntry = null;
    var editOriginalRow = null;

    // Spell picker state
    var spellPickerPage = 1;
    var spellPickerQuery = '';
    var spellPickerTargetField = null;

    var CUSTOM_RANGE_START = 900000;

    // ===================== BASELINE INTEGRATION =====================

    BaselineSystem.checkStatus(function (status) {
        var hasGoBaseline = false;
        if (status.tables) {
            for (var i = 0; i < status.tables.length; i++) {
                if (status.tables[i].tableName === 'og_gameobject_template') { hasGoBaseline = true; break; }
            }
        }
        if (!hasGoBaseline && status.initialized) {
            $('#baselineWarning').html(
                '<div class="baseline-warning">' +
                '<div class="baseline-warning-icon"><i class="fa-solid fa-triangle-exclamation"></i></div>' +
                '<div class="baseline-warning-body">' +
                '<div class="baseline-warning-title">Game Object Baseline Missing</div>' +
                '<div class="baseline-warning-text">The OG baseline exists for items/spells/loot but not for game objects. Re-run initialization to add the game object snapshot.</div>' +
                '<button class="baseline-init-btn" id="btnInitBaseline"><i class="fa-solid fa-database"></i> Initialize Missing Tables</button>' +
                '<div id="baselineProgress" style="display:none; margin-top:10px;"></div>' +
                '</div></div>'
            ).show();
        } else {
            BaselineSystem.renderWarningBanner('#baselineWarning');
        }
    });

    $(document).on('baseline:initialized', function () {
        if (currentDetailEntry) loadGoChangelog(currentDetailEntry);
    });

    // ===================== CONSTANTS =====================

    var TYPE_NAMES = {
        0: 'Door', 1: 'Button', 2: 'Quest Giver', 3: 'Chest', 5: 'Generic',
        6: 'Trap', 7: 'Chair', 8: 'Spell Focus', 9: 'Text', 10: 'Goober',
        11: 'Transport', 13: 'Camera', 14: 'Map Object', 15: 'MO Transport',
        17: 'Fishing Node', 18: 'Ritual', 19: 'Mailbox', 20: 'Auction House',
        22: 'Spell Caster', 23: 'Meeting Stone', 24: 'Flag Stand', 25: 'Fishing Hole',
        26: 'Flag Drop', 29: 'Capture Point', 30: 'Aura Generator', 31: 'Dungeon Difficulty'
    };

    var MAP_NAMES = {
        0: 'Eastern Kingdoms', 1: 'Kalimdor', 30: 'Alterac Valley',
        33: 'Shadowfang Keep', 34: 'Stormwind Stockade', 36: 'Deadmines',
        43: 'Wailing Caverns', 47: 'Razorfen Kraul', 48: 'Blackfathom Deeps',
        70: 'Uldaman', 90: 'Gnomeregan', 109: 'Sunken Temple',
        129: 'Razorfen Downs', 189: 'Scarlet Monastery', 209: "Zul'Farrak",
        229: 'Blackrock Spire', 230: 'Blackrock Depths', 249: "Onyxia's Lair",
        289: 'Scholomance', 309: "Zul'Gurub", 329: 'Stratholme',
        349: 'Maraudon', 369: 'Deeprun Tram', 389: 'Ragefire Chasm',
        409: 'Molten Core', 429: 'Dire Maul', 469: 'Blackwing Lair',
        489: 'Warsong Gulch', 509: "Ruins of Ahn'Qiraj", 529: 'Arathi Basin',
        531: "Ahn'Qiraj Temple", 533: 'Naxxramas'
    };

    var SCHOOL_NAMES = { 0: 'Physical', 1: 'Holy', 2: 'Fire', 3: 'Nature', 4: 'Frost', 5: 'Shadow', 6: 'Arcane' };

    // ── Polymorphic data field definitions ──
    // type: 'int' (number), 'bool' (checkbox), 'spell' (picker + resolve),
    //       'quest' (resolve), 'loot' (loot ref hint), 'lock' (lock hint)
    // desc: plain-English explanation shown below the field

    var DATA_FIELD_DEFS = {
        0: { // DOOR
            data0: { label: 'Start Open', type: 'bool', desc: 'Door starts in the open position when spawned' },
            data1: { label: 'Lock ID', type: 'lock', desc: '0 = unlocked. Other values reference Lock.dbc — requires a key, spell, or skill to open' },
            data2: { label: 'Auto Close Time', type: 'int', desc: 'Delay before the door closes automatically (65536 × seconds). 0 = stays open' },
            data3: { label: 'No Damage Immune', type: 'bool', desc: 'If enabled, the door can be damaged/destroyed' },
            data4: { label: 'Open Text ID', type: 'int', desc: 'Broadcast text shown when door opens' },
            data5: { label: 'Close Text ID', type: 'int', desc: 'Broadcast text shown when door closes' }
        },
        1: { // BUTTON
            data0: { label: 'Start Open', type: 'bool', desc: 'Button starts in the activated position' },
            data1: { label: 'Lock ID', type: 'lock', desc: '0 = anyone can press. Other values require a key or skill' },
            data2: { label: 'Auto Close Time', type: 'int', desc: 'Delay before the button resets (65536 × seconds)' },
            data3: { label: 'Linked Trap', type: 'int', desc: 'Entry of a type 6 (Trap) object to trigger when pressed' },
            data4: { label: 'No Damage Immune', type: 'bool', desc: 'If enabled, the button can be damaged' },
            data5: { label: 'Large', type: 'bool', desc: 'Increases interaction range' },
            data6: { label: 'Open Text ID', type: 'int', desc: 'Broadcast text shown on activation' },
            data7: { label: 'Close Text ID', type: 'int', desc: 'Broadcast text shown on deactivation' },
            data8: { label: 'LOS OK', type: 'bool', desc: 'Can be used without line of sight' }
        },
        2: { // QUESTGIVER
            data0: { label: 'Lock ID', type: 'lock', desc: '0 = anyone can interact' },
            data1: { label: 'Quest List', type: 'int', desc: 'Internal quest list reference' },
            data2: { label: 'Page Material', type: 'int', desc: 'Background texture for page text (PageTextMaterial.dbc)' },
            data3: { label: 'Gossip ID', type: 'int', desc: 'Gossip menu shown when interacting' },
            data4: { label: 'Custom Anim', type: 'int', desc: 'Animation played on interaction (1-4)' },
            data5: { label: 'No Damage Immune', type: 'bool', desc: 'If enabled, object can be damaged' },
            data6: { label: 'Open Text ID', type: 'int', desc: 'Broadcast text on interaction' },
            data7: { label: 'LOS OK', type: 'bool', desc: 'Can be used without line of sight' },
            data8: { label: 'Allow Mounted', type: 'bool', desc: 'Player can interact while mounted' },
            data9: { label: 'Large', type: 'bool', desc: 'Increases interaction range' }
        },
        3: { // CHEST
            data0: { label: 'Lock ID', type: 'lock', desc: '0 = anyone can loot. Other values require a key, lockpicking, or herbalism/mining skill' },
            data1: { label: 'Loot Template', type: 'loot', desc: 'The gameobject_loot_template.entry that defines what drops from this chest' },
            data2: { label: 'Restock Time', type: 'int', desc: 'Seconds before the chest respawns its loot. 0 = no restock' },
            data3: { label: 'Consumable', type: 'bool', desc: 'If enabled, the chest despawns after being looted' },
            data4: { label: 'Min Restock', type: 'int', desc: 'Minimum successful loot attempts (for mining/herbalism nodes)' },
            data5: { label: 'Max Restock', type: 'int', desc: 'Maximum successful loot attempts before depletion' },
            data6: { label: 'Looted Event', type: 'int', desc: 'DB script event triggered when looted' },
            data7: { label: 'Linked Trap', type: 'int', desc: 'Entry of a type 6 (Trap) object triggered when opened' },
            data8: { label: 'Quest ID', type: 'quest', desc: 'Quest that must be in the player\'s log to loot this chest' },
            data9: { label: 'Min Level', type: 'int', desc: 'Minimum player level required to open' },
            data10: { label: 'LOS OK', type: 'bool', desc: 'Can be looted without line of sight' },
            data11: { label: 'Leave Loot', type: 'bool', desc: 'Loot remains if not fully taken' },
            data12: { label: 'Not In Combat', type: 'bool', desc: 'Cannot be looted while in combat' },
            data13: { label: 'Log Loot', type: 'bool', desc: 'Log loot events to the server log' },
            data14: { label: 'Open Text ID', type: 'int', desc: 'Broadcast text when opened' },
            data15: { label: 'Group Loot Rules', type: 'bool', desc: 'Use group loot rules (round-robin, need/greed)' }
        },
        5: { // GENERIC
            data0: { label: 'Floating Tooltip', type: 'bool', desc: 'Show tooltip floating above the object' },
            data1: { label: 'Highlight', type: 'bool', desc: 'Object glows when moused over' },
            data2: { label: 'Server Only', type: 'bool', desc: 'Object exists only on the server, invisible to clients' },
            data3: { label: 'Large', type: 'bool', desc: 'Increases interaction range' },
            data4: { label: 'Float On Water', type: 'bool', desc: 'Object bobs on water surface' },
            data5: { label: 'Quest ID', type: 'quest', desc: 'Object only visible/usable when player has this quest active' }
        },
        6: { // TRAP
            data0: { label: 'Lock ID', type: 'lock', desc: '0 = triggers automatically. Other values require disarming' },
            data1: { label: 'Level', type: 'int', desc: 'NPC-equivalent level for the trap\'s spell damage calculations' },
            data2: { label: 'Diameter', type: 'int', desc: 'Trigger radius (diameter, not radius). Players within this distance activate the trap' },
            data3: { label: 'Spell ID', type: 'spell', desc: 'The spell cast when the trap triggers' },
            data4: { label: 'Charges', type: 'int', desc: '0 = single use then despawn. 1 = reusable' },
            data5: { label: 'Cooldown', type: 'int', desc: 'Seconds between activations if reusable' },
            data6: { label: 'Auto Close', type: 'int', desc: 'Time before trap resets' },
            data7: { label: 'Start Delay', type: 'int', desc: 'Seconds after spawn before the trap becomes active' },
            data8: { label: 'Server Only', type: 'bool', desc: 'Trap is invisible to players' },
            data9: { label: 'Stealthed', type: 'bool', desc: 'Trap is stealthed (detectable by stealth detection)' },
            data10: { label: 'Large', type: 'bool', desc: 'Increases trigger range' },
            data11: { label: 'Stealth Affected', type: 'bool', desc: 'Stealth detection affects whether this trap triggers' },
            data12: { label: 'Open Text ID', type: 'int', desc: 'Broadcast text when triggered' }
        },
        7: { // CHAIR
            data0: { label: 'Chair Slots', type: 'int', desc: 'Number of players that can sit simultaneously' },
            data1: { label: 'Chair Orientation', type: 'int', desc: 'Which side(s) players can sit on (0-4)' }
        },
        8: { // SPELL_FOCUS
            data0: { label: 'Spell Focus Type', type: 'int', desc: 'SpellFocusObject.dbc entry — required by spells that need a nearby focus object to cast' },
            data1: { label: 'Diameter', type: 'int', desc: 'Range within which the focus works (diameter)' },
            data2: { label: 'Linked Trap', type: 'int', desc: 'Entry of a Trap object triggered on use' },
            data3: { label: 'Server Only', type: 'bool', desc: 'Invisible to players (GM only)' },
            data4: { label: 'Quest ID', type: 'quest', desc: 'Only active when player has this quest' },
            data5: { label: 'Large', type: 'bool', desc: 'Increases interaction range' }
        },
        9: { // TEXT
            data0: { label: 'Page ID', type: 'int', desc: 'Entry in page_text table — the content the player reads' },
            data1: { label: 'Language', type: 'int', desc: 'Text language from Languages.dbc' },
            data2: { label: 'Page Material', type: 'int', desc: 'Background texture for the text (PageTextMaterial.dbc)' }
        },
        10: { // GOOBER
            data0: { label: 'Lock ID', type: 'lock', desc: '0 = anyone can interact' },
            data1: { label: 'Quest ID', type: 'quest', desc: 'Quest required in log to interact' },
            data2: { label: 'Event ID', type: 'int', desc: 'DB script event triggered on use' },
            data3: { label: 'Auto Close', type: 'int', desc: 'Delay before resetting (65536 × seconds)' },
            data4: { label: 'Custom Anim', type: 'int', desc: 'Animation played on interaction (1-4)' },
            data5: { label: 'Consumable', type: 'bool', desc: 'If enabled, despawns after use' },
            data6: { label: 'Cooldown', type: 'int', desc: 'Seconds before the object can be used again' },
            data7: { label: 'Page ID', type: 'int', desc: 'Entry in page_text — text shown on use' },
            data8: { label: 'Language', type: 'int', desc: 'Language of the page text' },
            data9: { label: 'Page Material', type: 'int', desc: 'Background texture for page text' },
            data10: { label: 'Spell ID', type: 'spell', desc: 'Spell cast on the player when they interact' },
            data11: { label: 'No Damage Immune', type: 'bool', desc: 'Object can be damaged' },
            data12: { label: 'Linked Trap', type: 'int', desc: 'Entry of a Trap object triggered on use' },
            data13: { label: 'Large', type: 'bool', desc: 'Increases interaction range' },
            data14: { label: 'Open Text ID', type: 'int', desc: 'Broadcast text on use' },
            data15: { label: 'Close Text ID', type: 'int', desc: 'Broadcast text on reset' },
            data16: { label: 'LOS OK', type: 'bool', desc: 'Can be used without line of sight' }
        },
        13: { // CAMERA
            data0: { label: 'Lock ID', type: 'lock', desc: '0 = anyone can trigger' },
            data1: { label: 'Cinematic ID', type: 'int', desc: 'CinematicCamera.dbc entry — the cutscene played' }
        },
        15: { // MO_TRANSPORT
            data0: { label: 'Taxi Path ID', type: 'int', desc: 'Path from TaxiPath.dbc that the transport follows' },
            data1: { label: 'Move Speed', type: 'int', desc: 'Transport movement speed' },
            data2: { label: 'Accel Rate', type: 'int', desc: 'Acceleration rate' }
        },
        18: { // RITUAL
            data0: { label: 'Required Casters', type: 'int', desc: 'Number of players needed to complete the ritual' },
            data1: { label: 'Spell ID', type: 'spell', desc: 'Spell cast by participants during the ritual' },
            data2: { label: 'Anim Spell', type: 'spell', desc: 'Visual animation spell shown during the ritual' },
            data3: { label: 'Ritual Persistent', type: 'bool', desc: 'Ritual remains active after completion' },
            data4: { label: 'Caster Target Spell', type: 'spell', desc: 'Spell cast on the ritual target when completed' },
            data5: { label: 'Caster Target Spell Targets', type: 'bool', desc: 'Whether the target spell hits all participants' },
            data6: { label: 'Casters Grouped', type: 'bool', desc: 'All casters must be in the same group' }
        },
        20: { // AUCTIONHOUSE
            data0: { label: 'Auction House ID', type: 'int', desc: 'AuctionHouse.dbc entry — determines which AH faction pool to use' }
        },
        22: { // SPELLCASTER
            data0: { label: 'Spell ID', type: 'spell', desc: 'The spell cast on a player when they click this object' },
            data1: { label: 'Charges', type: 'int', desc: '-1 = unlimited uses. 0 or 1 = single use, then the object despawns' },
            data2: { label: 'Party Only', type: 'bool', desc: 'Only the summoner\'s party members can use this object. Disable for public buff shrines' }
        },
        23: { // MEETINGSTONE
            data0: { label: 'Min Level', type: 'int', desc: 'Minimum level to use the meeting stone' },
            data1: { label: 'Max Level', type: 'int', desc: 'Maximum level to use the meeting stone' },
            data2: { label: 'Area ID', type: 'int', desc: 'AreaTable.dbc entry — the dungeon/zone this stone summons to' }
        },
        24: { // FLAGSTAND
            data0: { label: 'Lock ID', type: 'lock', desc: '0 = anyone can pick up the flag' },
            data1: { label: 'Pickup Spell', type: 'spell', desc: 'Spell cast when a player picks up the flag' },
            data2: { label: 'Radius', type: 'int', desc: 'Interaction radius' },
            data3: { label: 'Return Aura', type: 'spell', desc: 'Aura applied that returns the flag' },
            data4: { label: 'Return Spell', type: 'spell', desc: 'Spell cast when the flag is returned' },
            data5: { label: 'No Damage Immune', type: 'bool', desc: 'Flag stand can be damaged' },
            data6: { label: 'Open Text ID', type: 'int', desc: 'Broadcast text on pickup' },
            data7: { label: 'LOS OK', type: 'bool', desc: 'Can be used without line of sight' }
        },
        25: { // FISHINGHOLE
            data0: { label: 'Radius', type: 'int', desc: 'Fishing area radius' },
            data1: { label: 'Loot Template', type: 'loot', desc: 'The gameobject_loot_template.entry defining catchable fish' },
            data2: { label: 'Min Restock', type: 'int', desc: 'Minimum successful catches before depletion' },
            data3: { label: 'Max Restock', type: 'int', desc: 'Maximum successful catches before depletion' }
        },
        26: { // FLAGDROP
            data0: { label: 'Lock ID', type: 'lock', desc: '0 = anyone can pick up' },
            data1: { label: 'Event ID', type: 'int', desc: 'Event triggered on pickup' },
            data2: { label: 'Pickup Spell', type: 'spell', desc: 'Spell cast when picked up' },
            data3: { label: 'No Damage Immune', type: 'bool', desc: 'Can be damaged' }
        },
        29: { // CAPTURE_POINT
            data0: { label: 'Radius', type: 'int', desc: 'Capture area radius' },
            data1: { label: 'Spell', type: 'spell', desc: 'Spell associated with the capture point' },
            data2: { label: 'World State 1', type: 'int', desc: 'World state variable for Alliance progress' },
            data3: { label: 'World State 2', type: 'int', desc: 'World state variable for Horde progress' },
            data4: { label: 'Win Event 1', type: 'int', desc: 'Event when Alliance captures' },
            data5: { label: 'Win Event 2', type: 'int', desc: 'Event when Horde captures' },
            data6: { label: 'Contested Event 1', type: 'int', desc: 'Event when Alliance contests' },
            data7: { label: 'Contested Event 2', type: 'int', desc: 'Event when Horde contests' },
            data8: { label: 'Progress Event 1', type: 'int', desc: 'Event during Alliance progress' },
            data9: { label: 'Progress Event 2', type: 'int', desc: 'Event during Horde progress' },
            data10: { label: 'Neutral Event 1', type: 'int', desc: 'Event when point becomes neutral (Alliance side)' },
            data11: { label: 'Neutral Event 2', type: 'int', desc: 'Event when point becomes neutral (Horde side)' },
            data12: { label: 'Neutral Percent', type: 'int', desc: 'Percentage at which the point is considered neutral' },
            data13: { label: 'World State 3', type: 'int', desc: 'Additional world state variable' },
            data14: { label: 'Min Superiority', type: 'int', desc: 'Minimum player advantage to begin capture' },
            data15: { label: 'Max Superiority', type: 'int', desc: 'Maximum player advantage (caps capture speed)' },
            data16: { label: 'Min Time', type: 'int', desc: 'Minimum capture time in seconds' },
            data17: { label: 'Max Time', type: 'int', desc: 'Maximum capture time in seconds' },
            data18: { label: 'Large', type: 'bool', desc: 'Increases interaction range' }
        },
        30: { // AURA_GENERATOR
            data0: { label: 'Start Open', type: 'bool', desc: 'Generator starts active when spawned' },
            data1: { label: 'Radius', type: 'int', desc: 'Aura effect radius' },
            data2: { label: 'Aura ID 1', type: 'spell', desc: 'The aura spell applied to players within range' },
            data3: { label: 'Condition ID 1', type: 'int', desc: 'Condition that must be met for the aura to apply' }
        },
        31: { // DUNGEON_DIFFICULTY
            data0: { label: 'Map ID', type: 'int', desc: 'Map.dbc entry for the dungeon' },
            data1: { label: 'Difficulty', type: 'int', desc: '0 = Normal, 1 = Heroic' }
        }
    };

    // ===================== SEARCH =====================

    function doSearch(page) {
        currentPage = page || 1;
        var params = {
            q: $('#goSearch').val(),
            typeFilter: $('#filterGoType').val() || undefined,
            customOnly: $('#chkCustomOnly').is(':checked') || undefined,
            page: currentPage,
            pageSize: currentPageSize
        };
        Object.keys(params).forEach(function (k) { if (params[k] === undefined || params[k] === '' || params[k] === false) delete params[k]; });

        $('#goListContainer').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i> Searching...</div>');

        $.getJSON('/GameObjects/Search', params, function (data) {
            currentModels = data.models || {};
            totalPages = data.totalPages;
            $('#totalGoCount').text(data.totalCount.toLocaleString());
            $('#goResultInfo').text('Showing ' + data.objects.length + ' of ' + data.totalCount.toLocaleString());

            if (data.objects.length === 0) {
                $('#goListContainer').html('<div class="text-center p-4 text-muted">No game objects found</div>');
                $('#goPaginationBar').hide();
                return;
            }

            var html = '';
            data.objects.forEach(function (obj) {
                var typeName = TYPE_NAMES[obj.type] || 'Type ' + obj.type;
                var hasModel = !!currentModels[obj.displayId];
                var isCustom = obj.entry >= CUSTOM_RANGE_START;

                html += '<div class="go-row" data-entry="' + obj.entry + '">' +
                    '<div class="go-model-indicator ' + (hasModel ? '' : 'no-model') + '">' +
                    '<i class="fa-solid ' + (hasModel ? 'fa-cube' : 'fa-square') + '"></i></div>' +
                    '<div style="flex: 1; min-width: 0;">' +
                    '<div class="go-name">' + esc(obj.name) +
                    (isCustom ? ' <span style="font-size:9px;color:var(--status-online);">&#9733;</span>' : '') + '</div>' +
                    '<div class="go-meta">Display: ' + obj.displayId + ' &middot; Size: ' + obj.size + '</div>' +
                    '</div>' +
                    '<span class="go-type-badge">' + esc(typeName) + '</span>' +
                    '<div class="go-entry">#' + obj.entry + '</div></div>';
            });

            $('#goListContainer').html(html);

            if (data.totalPages > 1) {
                $('#goPaginationBar').show();
                $('#goPageJump').val(data.page).attr('max', data.totalPages);
                $('#goPageTotal').text(data.totalPages);
                $('#btnGoFirstPage').prop('disabled', data.page <= 1);
                $('#btnGoPrevPage').prop('disabled', data.page <= 1);
                $('#btnGoNextPage').prop('disabled', data.page >= data.totalPages);
                $('#btnGoLastPage').prop('disabled', data.page >= data.totalPages);
            } else if (data.objects.length > 0) {
                $('#goPaginationBar').show();
                $('#goPageJump').val(1).attr('max', 1);
                $('#goPageTotal').text('1');
                $('#btnGoFirstPage, #btnGoPrevPage, #btnGoNextPage, #btnGoLastPage').prop('disabled', true);
            } else {
                $('#goPaginationBar').hide();
            }
        }).fail(function () {
            $('#goListContainer').html('<div class="text-center p-4 text-muted">Search failed</div>');
        });
    }

    // ===================== DETAIL =====================

    function loadDetail(entry) {
        currentDetailEntry = entry;
        $('#goDetailContent').html('<div class="text-center p-3"><i class="fa-solid fa-spinner fa-spin"></i></div>');

        $.getJSON('/GameObjects/Detail', { entry: entry }, function (data) {
            if (!data.found) {
                $('#goDetailContent').html('<div class="text-center text-muted p-3">Game object not found</div>');
                $('#goDetailActions').hide();
                return;
            }

            currentDetailObj = data.obj;
            var obj = data.obj;
            var isCustom = entry >= CUSTOM_RANGE_START;
            var html = '';

            if (data.modelPath) {
                html += '<div class="model-preview-container"><model-viewer src="' + esc(data.modelPath) + '" auto-rotate camera-controls shadow-intensity="0.5" exposure="1.2" style="width:100%;height:100%;--poster-color:transparent;"></model-viewer></div>';
            } else {
                html += '<div class="no-model-placeholder"><i class="fa-solid fa-cube" style="margin-right:6px;"></i> No 3D model available</div>';
            }

            html += '<div style="font-size:15px;font-weight:600;margin-bottom:2px;">' + esc(obj.name) +
                (isCustom ? ' <span style="font-size:10px;color:var(--status-online);">&#9733; Custom</span>' : '') + '</div>';
            html += '<div style="font-size:12px;color:var(--text-muted);margin-bottom:8px;">' + esc(data.typeLabel) + ' &middot; Entry #' + obj.entry + '</div>';

            html += '<div class="detail-section"><div class="detail-section-title">Properties</div>';
            html += '<div class="detail-row"><span class="label">Type</span><span class="value">' + esc(data.typeLabel) + ' (' + obj.type + ')</span></div>';
            html += '<div class="detail-row"><span class="label">Display ID</span><span class="value">' + obj.displayId + '</span></div>';
            html += '<div class="detail-row"><span class="label">Size</span><span class="value">' + obj.size + '</span></div>';
            if (obj.faction > 0) html += '<div class="detail-row"><span class="label">Faction</span><span class="value">' + obj.faction + '</span></div>';
            if (obj.flags > 0) html += '<div class="detail-row"><span class="label">Flags</span><span class="value">0x' + obj.flags.toString(16).toUpperCase() + '</span></div>';
            html += '</div>';

            if (data.dataLabels && Object.keys(data.dataLabels).length > 0) {
                html += '<div class="detail-section"><div class="detail-section-title">Type-Specific Data</div>';
                for (var key in data.dataLabels) {
                    var val = obj[key];
                    if (val !== undefined && val !== 0)
                        html += '<div class="detail-row"><span class="label">' + esc(data.dataLabels[key]) + '</span><span class="value">' + val + '</span></div>';
                }
                for (var d = 0; d <= 23; d++) {
                    var dk = 'data' + d;
                    if (!data.dataLabels[dk] && obj[dk] !== undefined && obj[dk] !== 0)
                        html += '<div class="detail-row"><span class="label">' + dk + '</span><span class="value">' + obj[dk] + '</span></div>';
                }
                html += '</div>';
            }

            html += '<div class="detail-section"><div class="detail-section-title">World Spawns (' + data.spawnCount + ')</div>';
            if (data.spawns && data.spawns.length > 0) {
                data.spawns.forEach(function (sp) {
                    var mapName = MAP_NAMES[sp.map] || 'Map ' + sp.map;
                    html += '<div class="spawn-chip"><i class="fa-solid fa-location-dot" style="font-size:9px;"></i> ' +
                        esc(mapName) + ' (' + sp.x.toFixed(1) + ', ' + sp.y.toFixed(1) + ', ' + sp.z.toFixed(1) + ')</div> ';
                });
                if (data.spawnCount > 10) html += '<div style="font-size:11px;color:var(--text-muted);margin-top:4px;">... and ' + (data.spawnCount - 10) + ' more</div>';
            } else {
                html += '<div style="font-size:12px;color:var(--text-muted);">No world spawns</div>';
            }
            html += '</div>';

            $('#goDetailContent').html(html);
            $('#goDetailActions').show();
            $('#btnEditGoOriginal').html(isCustom ? '<i class="fa-solid fa-pen"></i> Edit' : '<i class="fa-solid fa-pen"></i> Edit Original');
            loadGoChangelog(entry);
        });
    }

    // ===================== GO CHANGELOG =====================

    function loadGoChangelog(entry) {
        if (!BaselineSystem.isInitialized()) { $('#goChangelogPanel, #goResetContainer').hide(); return; }

        BaselineSystem.loadGameObjectDiff(entry, '#goChangelogContent', function (data) {
            if (!data || !data.available || !data.hasOriginal) {
                if (entry >= CUSTOM_RANGE_START) { $('#goChangelogPanel').show(); $('#goChangeCount').text('—').addClass('clean'); }
                else { $('#goChangelogPanel').hide(); }
                $('#goResetContainer').hide();
                return;
            }
            $('#goChangelogPanel').show();
            if (data.isModified) {
                $('#goChangeCount').text(data.changes ? data.changes.length : 0).removeClass('clean');
                $('#goResetContainer').show();
            } else {
                $('#goChangeCount').text('0').addClass('clean');
                $('#goResetContainer').hide();
            }
        });
    }

    // ===================== EDIT FORM =====================

    function openEditPanel(sourceEntry, asClone) {
        $.getJSON('/GameObjects/FullRow', { entry: sourceEntry }, function (data) {
            if (!data.found) { showToast('Game object not found', 'error'); return; }
            editSourceEntry = sourceEntry;
            editIsClone = asClone;
            editIsBaseGame = !asClone && sourceEntry < CUSTOM_RANGE_START;
            editOriginalRow = data.obj;

            if (asClone) {
                $.getJSON('/GameObjects/NextCustomId', function (idData) {
                    editEntry = idData.nextId;
                    renderEditForm(data.obj, data.modelPath);
                    showEditPanel();
                });
            } else {
                editEntry = sourceEntry;
                renderEditForm(data.obj, data.modelPath);
                showEditPanel();
            }
        });
    }

    function showEditPanel() {
        editMode = true;
        $('#editGoHeaderName').text(($('#editFieldName').val() || 'New Object') + (editIsClone ? ' (Clone)' : ''));
        if (editIsBaseGame) {
            $('#editGoBadge').text('\u26A0 BASE GAME').addClass('base-game');
            $('#editGoPanel').addClass('base-game-mode');
            $('#editGoWarningBar').show();
        } else {
            $('#editGoBadge').text('CUSTOM').removeClass('base-game');
            $('#editGoPanel').removeClass('base-game-mode');
            $('#editGoWarningBar').hide();
        }
        $('#colGoDetail').hide();
        $('#colGoEdit').show();
    }

    function closeEditPanel() {
        editMode = false; editEntry = null; editIsClone = false; editIsBaseGame = false;
        editSourceEntry = null; editOriginalRow = null;
        $('#colGoEdit').hide();
        $('#colGoDetail').show();
    }

    function renderEditForm(obj, modelPath) {
        var h = '';

        h += sectionStart('identity', 'Identity', 'fa-tag', true);
        h += field('Name', '<input type="text" id="editFieldName" value="' + escAttr(obj.name) + '" />');
        h += field('Type', buildTypeDropdown(obj.type));
        h += field('Display ID', '<div class="d-flex gap-2 align-items-center">' +
            '<input type="number" id="editFieldDisplayId" value="' + (obj.displayId || 0) + '" min="0" style="flex:1;" />' +
            '<button type="button" class="btn-sm btn-outline-subtle" id="btnCheckModel" title="Check for 3D model"><i class="fa-solid fa-cube"></i></button></div>');

        if (modelPath) {
            h += '<div id="editModelPreview" class="model-preview-container" style="height:150px;margin-top:8px;">' +
                '<model-viewer src="' + esc(modelPath) + '" auto-rotate camera-controls shadow-intensity="0.5" exposure="1.2" style="width:100%;height:100%;--poster-color:transparent;"></model-viewer></div>';
        } else {
            h += '<div id="editModelPreview"></div>';
        }

        h += '<div class="edit-field-inline">';
        h += field('Faction', '<input type="number" id="editFieldFaction" value="' + (obj.faction || 0) + '" min="0" />');
        h += field('Flags', '<input type="number" id="editFieldFlags" value="' + (obj.flags || 0) + '" min="0" />');
        h += '</div>';
        h += field('Size', '<input type="number" id="editFieldSize" value="' + (obj.size || 1) + '" min="0.01" step="0.1" />');
        h += field('Icon Name', '<input type="text" id="editFieldIcon" value="' + escAttr(obj.icon || '') + '" placeholder="IconName string (usually empty for custom objects)" />');
        h += sectionEnd();

        h += sectionStart('typedata', 'Type-Specific Data', 'fa-sliders', true);
        h += '<div id="dataFieldsContainer">' + buildDataFields(obj.type, obj) + '</div>';
        h += sectionEnd();

        if (!editIsClone && editEntry >= CUSTOM_RANGE_START) {
            h += '<div style="margin-top:16px;padding-top:16px;border-top:1px solid var(--border-light);">' +
                '<button type="button" class="btn-sm" id="btnDeleteGo" style="color:var(--status-error);background:none;border:1px solid var(--status-error);border-radius:var(--radius-sm);padding:4px 12px;font-size:12px;cursor:pointer;">' +
                '<i class="fa-solid fa-trash"></i> Delete Object</button></div>';
        }

        $('#editGoFormContainer').html(h);
        resolveAllInlineHints();
    }

    // ===================== FORM BUILDERS =====================

    function sectionStart(id, title, icon, open) {
        return '<div class="edit-section" data-section="' + id + '">' +
            '<div class="edit-section-header' + (open ? '' : ' collapsed') + '" data-target="' + id + '">' +
            '<i class="fa-solid ' + icon + '" style="color:var(--accent);font-size:12px;"></i> ' + title +
            '<i class="fa-solid fa-chevron-down chevron"></i></div>' +
            '<div class="edit-section-body' + (open ? '' : ' collapsed') + '" data-body="' + id + '">';
    }
    function sectionEnd() { return '</div></div>'; }
    function field(label, inner) { return '<div class="edit-field"><label>' + label + '</label>' + inner + '</div>'; }

    function buildTypeDropdown(selected) {
        var h = '<select id="editFieldType">';
        Object.keys(TYPE_NAMES).sort(function (a, b) { return +a - +b; }).forEach(function (k) {
            h += '<option value="' + k + '"' + (+k === selected ? ' selected' : '') + '>' + k + ' \u2014 ' + TYPE_NAMES[k] + '</option>';
        });
        return h + '</select>';
    }

    function buildDataFields(type, obj) {
        var defs = DATA_FIELD_DEFS[type] || {};
        var definedKeys = Object.keys(defs);
        var h = '';

        if (definedKeys.length === 0) {
            h += '<div style="font-size:12px;color:var(--text-muted);padding:4px 0;">No type-specific fields defined for this type. Non-zero data fields shown below.</div>';
        }

        // Render typed fields
        definedKeys.forEach(function (key) {
            var def = defs[key];
            var val = obj ? (obj[key] !== undefined ? obj[key] : 0) : 0;
            h += buildTypedField(key, def, val);
        });

        // Collect leftover non-zero data fields not in the type definition
        var inherited = [];
        for (var d = 0; d <= 23; d++) {
            var dk = 'data' + d;
            if (!defs[dk]) {
                var dv = obj ? (obj[dk] !== undefined ? obj[dk] : 0) : 0;
                if (dv !== 0) inherited.push({ key: dk, val: dv });
            }
        }

        // Show inherited in a collapsible section if any exist
        if (inherited.length > 0) {
            h += '<div class="inherited-data-section">' +
                '<div class="inherited-data-toggle" id="inheritedDataToggle">' +
                '<i class="fa-solid fa-chevron-right inherited-chevron"></i> ' +
                'Inherited Data <span class="inherited-count">' + inherited.length + ' field' + (inherited.length > 1 ? 's' : '') + '</span>' +
                '</div>' +
                '<div class="inherited-data-body" id="inheritedDataBody" style="display:none;">' +
                '<div class="inherited-data-note">These values were carried over from the source object and may not be relevant to the current type. Set to 0 if not needed.</div>';
            inherited.forEach(function (f) {
                h += buildTypedField(f.key, { label: f.key, type: 'int', desc: 'Untyped field — not defined for this object type' }, f.val);
            });
            h += '</div></div>';
        }

        return h;
    }

    function buildTypedField(key, def, val) {
        var label = def.label;
        var fieldType = def.type;
        var desc = def.desc || '';

        var labelHtml = '<label>' + esc(label) +
            (label !== key ? ' <span style="font-weight:400;color:var(--text-muted);">(' + key + ')</span>' : '') + '</label>';
        var inputHtml = '';
        var hintHtml = '';
        var descHtml = desc ? '<div class="field-desc">' + esc(desc) + '</div>' : '';

        switch (fieldType) {
            case 'bool':
                inputHtml = '<label class="bool-toggle">' +
                    '<input type="checkbox" class="data-field-bool" data-key="' + key + '"' + (val ? ' checked' : '') + ' />' +
                    '<span class="bool-toggle-label">' + (val ? 'Yes' : 'No') + '</span></label>';
                break;

            case 'spell':
                inputHtml = '<div class="d-flex gap-2 align-items-center">' +
                    '<input type="number" class="data-field-input" data-key="' + key + '" data-field-type="spell" value="' + val + '" min="0" style="flex:1;" />' +
                    '<button type="button" class="btn-sm btn-outline-subtle btn-pick-spell" data-key="' + key + '" title="Browse Spells">' +
                    '<i class="fa-solid fa-magnifying-glass"></i></button></div>';
                hintHtml = '<div class="field-hint spell-hint" data-field="' + key + '"></div>';
                break;

            case 'quest':
                inputHtml = '<input type="number" class="data-field-input" data-key="' + key + '" data-field-type="quest" value="' + val + '" min="0" />';
                hintHtml = '<div class="field-hint quest-hint" data-field="' + key + '"></div>';
                break;

            case 'loot':
                inputHtml = '<input type="number" class="data-field-input" data-key="' + key + '" data-field-type="loot" value="' + val + '" min="0" />';
                hintHtml = '<div class="field-hint loot-hint" data-field="' + key + '"></div>';
                break;

            case 'lock':
                inputHtml = '<input type="number" class="data-field-input" data-key="' + key + '" data-field-type="lock" value="' + val + '" min="0" />';
                break;

            default:
                inputHtml = '<input type="number" class="data-field-input" data-key="' + key + '" data-field-type="int" value="' + val + '" />';
                break;
        }
        return '<div class="edit-field">' + labelHtml + descHtml + inputHtml + hintHtml + '</div>';
    }

    // ===================== INLINE HINT RESOLUTION =====================

    function resolveAllInlineHints() {
        $('.data-field-input[data-field-type="spell"]').each(function () {
            resolveSpellHint($(this).data('key'), parseInt($(this).val()) || 0);
        });
        $('.data-field-input[data-field-type="quest"]').each(function () {
            resolveQuestHint($(this).data('key'), parseInt($(this).val()) || 0);
        });
    }

    function resolveSpellHint(key, spellId) {
        var $h = $('.spell-hint[data-field="' + key + '"]');
        if (!$h.length) return;
        if (!spellId || spellId <= 0) { $h.html(''); return; }
        $h.html('<i class="fa-solid fa-spinner fa-spin" style="font-size:9px;"></i>');
        $.getJSON('/Spells/Detail', { entry: spellId }, function (data) {
            if (data.found) {
                var sp = data.spell;
                var school = SCHOOL_NAMES[sp.school] || '';
                $h.html('<i class="fa-solid fa-bolt" style="font-size:9px;color:var(--accent);"></i> ' +
                    '<strong>' + esc(sp.name) + '</strong>' +
                    (sp.nameSubtext ? ' <span style="color:var(--text-muted);">(' + esc(sp.nameSubtext) + ')</span>' : '') +
                    (school ? ' &middot; <span class="school-label">' + esc(school) + '</span>' : ''));
            } else {
                $h.html('<span style="color:var(--status-error);">Unknown spell #' + spellId + '</span>');
            }
        }).fail(function () { $h.html(''); });
    }

    function resolveQuestHint(key, questId) {
        var $h = $('.quest-hint[data-field="' + key + '"]');
        if (!$h.length) return;
        if (!questId || questId <= 0) { $h.html(''); return; }
        $h.html('<i class="fa-solid fa-spinner fa-spin" style="font-size:9px;"></i>');
        $.getJSON('/GameObjects/QuestName', { questId: questId }, function (data) {
            if (data.name) {
                $h.html('<i class="fa-solid fa-scroll" style="font-size:9px;color:var(--status-warning);"></i> ' + esc(data.name));
            } else {
                $h.html('<span style="color:var(--text-muted);">Quest #' + questId + '</span>');
            }
        }).fail(function () { $h.html(''); });
    }

    // ===================== SPELL PICKER MODAL =====================

    function openSpellPicker(targetKey) {
        spellPickerTargetField = targetKey;
        spellPickerPage = 1;
        spellPickerQuery = '';
        $('#spellPickerSearch').val('');
        loadSpellPickerPage();
        var modalEl = document.getElementById('spellPickerModal');
        if (!modalEl) { showToast('Spell picker not available — redeploy Index.cshtml', 'error'); return; }
        new bootstrap.Modal(modalEl).show();
        setTimeout(function () { $('#spellPickerSearch').focus(); }, 300);
    }

    function loadSpellPickerPage() {
        var params = { q: spellPickerQuery, page: spellPickerPage, pageSize: 50 };
        $('#spellPickerResults').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i></div>');

        $.getJSON('/Spells/Search', params, function (data) {
            $('#spellPickerInfo').text(data.totalCount.toLocaleString() + ' spells');
            $('#spellPickerPageInfo').text(data.page + ' / ' + data.totalPages);
            $('#btnSpellPickerPrev').prop('disabled', data.page <= 1);
            $('#btnSpellPickerNext').prop('disabled', data.page >= data.totalPages);

            if (!data.spells || data.spells.length === 0) {
                $('#spellPickerResults').html('<div class="text-center text-muted p-4">No spells found</div>');
                return;
            }

            var icons = data.icons || {};
            var h = '';
            data.spells.forEach(function (sp) {
                var iconPath = icons[sp.spellIconId] || '/icons/inv_misc_questionmark.png';
                var school = SCHOOL_NAMES[sp.school] || '';
                var rank = sp.nameSubtext || '';

                h += '<div class="sp-pick-row" data-entry="' + sp.entry + '">' +
                    '<img class="sp-pick-icon" src="' + esc(iconPath) + '" loading="lazy" />' +
                    '<div style="flex:1;min-width:0;">' +
                    '<div class="sp-pick-name">' + esc(sp.name) +
                    (rank ? ' <span class="sp-pick-rank">' + esc(rank) + '</span>' : '') + '</div>' +
                    '<div class="sp-pick-meta">' +
                    (school ? school + ' &middot; ' : '') +
                    'Level ' + (sp.spellLevel || 0) +
                    (sp.manaCost > 0 ? ' &middot; ' + sp.manaCost + ' mana' : '') + '</div>' +
                    '</div>' +
                    '<div class="sp-pick-id">#' + sp.entry + '</div></div>';
            });

            $('#spellPickerResults').html(h);
        }).fail(function () {
            $('#spellPickerResults').html('<div class="text-center text-muted p-4">Search failed</div>');
        });
    }

    function selectSpell(entry) {
        if (!spellPickerTargetField) return;
        $('.data-field-input[data-key="' + spellPickerTargetField + '"]').val(entry);
        resolveSpellHint(spellPickerTargetField, entry);
        var modalEl = document.getElementById('spellPickerModal');
        if (modalEl) {
            var inst = bootstrap.Modal.getInstance(modalEl);
            if (inst) inst.hide();
        }
        spellPickerTargetField = null;
    }

    // ===================== COLLECT FORM DATA =====================

    function collectFormData() {
        var data = {};
        if (editOriginalRow) {
            var keys = Object.keys(editOriginalRow);
            for (var k = 0; k < keys.length; k++) data[keys[k]] = editOriginalRow[keys[k]];
        }
        data.entry = editEntry;
        data.name = $('#editFieldName').val() || 'Custom Object';
        data.type = int('#editFieldType');
        data.displayId = int('#editFieldDisplayId');
        data.faction = int('#editFieldFaction');
        data.flags = int('#editFieldFlags');
        data.size = parseFloat($('#editFieldSize').val()) || 1.0;
        data.icon = $('#editFieldIcon').val() || '';

        for (var d = 0; d <= 23; d++) data['data' + d] = 0;
        $('.data-field-input').each(function () { data[$(this).data('key')] = parseInt($(this).val()) || 0; });
        $('.data-field-bool').each(function () { data[$(this).data('key')] = $(this).is(':checked') ? 1 : 0; });
        return data;
    }

    function int(sel) { return parseInt($(sel).val()) || 0; }

    // ===================== SAVE / DELETE =====================

    function saveGo() {
        var data = collectFormData();
        if (!data.name || data.name.trim() === '') { showToast('Object name is required', 'error'); return; }

        $('#btnSaveGo').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Saving...');
        $.ajax({
            url: '/GameObjects/Save', method: 'POST', contentType: 'application/json',
            data: JSON.stringify(data),
            success: function (result) {
                $('#btnSaveGo').prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save');
                if (result.success) {
                    showToast(result.isInsert ? 'Object #' + data.entry + ' created!' : 'Object #' + data.entry + ' saved!', 'success');
                    closeEditPanel(); doSearch(currentPage); loadDetail(data.entry);
                } else { showToast('Save failed: ' + (result.error || 'Unknown error'), 'error'); }
            },
            error: function () {
                $('#btnSaveGo').prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save');
                showToast('Save failed — server error', 'error');
            }
        });
    }

    function deleteGo() {
        if (!editEntry || editEntry < CUSTOM_RANGE_START) return;
        if (!confirm('Delete this custom game object permanently?\nAny world spawns will also be removed.')) return;
        $.post('/GameObjects/Delete', { entry: editEntry }, function (result) {
            if (result.success) {
                var msg = 'Object #' + editEntry + ' deleted';
                if (result.spawnsDeleted > 0) msg += ' (' + result.spawnsDeleted + ' spawns removed)';
                showToast(msg, 'success');
                closeEditPanel(); doSearch(currentPage);
                $('#goDetailContent').html('<div class="text-center text-muted p-3">Object deleted</div>');
                $('#goDetailActions').hide();
            } else { showToast('Delete failed: ' + (result.error || 'Unknown error'), 'error'); }
        });
    }

    // ===================== HELPERS =====================

    function esc(text) { if (!text && text !== 0) return ''; var d = document.createElement('div'); d.textContent = text; return d.innerHTML; }
    function escAttr(text) { if (text == null) return ''; return String(text).replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/'/g, '&#39;').replace(/</g, '&lt;').replace(/>/g, '&gt;'); }

    function showToast(msg, type) {
        var el = $('<div class="edit-toast ' + type + '">' + esc(msg) + '</div>');
        $('body').append(el);
        setTimeout(function () { el.fadeOut(300, function () { el.remove(); }); }, 3000);
    }

    // ===================== EVENTS =====================

    $('#btnSearchGo').on('click', function () { doSearch(1); });
    $('#goSearch').on('keydown', function (e) { if (e.key === 'Enter') doSearch(1); });
    $('#filterGoType').on('change', function () { doSearch(1); });
    $('#btnGoFirstPage').on('click', function () { doSearch(1); });
    $('#btnGoPrevPage').on('click', function () { if (currentPage > 1) doSearch(currentPage - 1); });
    $('#btnGoNextPage').on('click', function () { if (currentPage < totalPages) doSearch(currentPage + 1); });
    $('#btnGoLastPage').on('click', function () { doSearch(totalPages); });
    $('#goPageJump').on('keydown', function (e) {
        if (e.key === 'Enter') {
            var p = Math.max(1, Math.min(parseInt($(this).val()) || 1, totalPages));
            doSearch(p);
        }
    });
    $('#goPageJump').on('blur', function () {
        var p = Math.max(1, Math.min(parseInt($(this).val()) || 1, totalPages));
        if (p !== currentPage) doSearch(p);
    });
    $('#goPageSize').val(currentPageSize);
    $('#goPageSize').on('change', function () {
        currentPageSize = parseInt($(this).val()) || 50;
        try { localStorage.setItem('msui_go_pageSize', currentPageSize); } catch (e) { }
        doSearch(1);
    });

    $(document).on('click', '.go-row', function () {
        if (editMode) return;
        $('.go-row').removeClass('active'); $(this).addClass('active');
        loadDetail($(this).data('entry'));
    });

    $('#btnCloneGo').on('click', function () { if (currentDetailEntry) openEditPanel(currentDetailEntry, true); });
    $('#btnEditGoOriginal').on('click', function () {
        if (!currentDetailEntry) return;
        if (currentDetailEntry >= CUSTOM_RANGE_START) { openEditPanel(currentDetailEntry, false); }
        else {
            $('#confirmGoName').text(currentDetailObj ? currentDetailObj.name : 'this object');
            $('#confirmGoEntry').text('(Entry #' + currentDetailEntry + ')');
            new bootstrap.Modal($('#editGoOriginalModal')[0]).show();
        }
    });
    $('#btnConfirmGoCloneInstead').on('click', function () { bootstrap.Modal.getInstance($('#editGoOriginalModal')[0]).hide(); openEditPanel(currentDetailEntry, true); });
    $('#btnConfirmGoEditOriginal').on('click', function () { bootstrap.Modal.getInstance($('#editGoOriginalModal')[0]).hide(); openEditPanel(currentDetailEntry, false); });

    $('#btnSaveGo').on('click', saveGo);
    $('#btnCancelGoEdit').on('click', closeEditPanel);
    $(document).on('click', '#btnDeleteGo', deleteGo);

    $(document).on('click', '.edit-section-header', function () {
        var t = $(this).data('target'); $(this).toggleClass('collapsed'); $('[data-body="' + t + '"]').toggleClass('collapsed');
    });

    // Type change → rebuild
    $(document).on('change', '#editFieldType', function () {
        var newType = parseInt($(this).val()) || 0;
        var cur = {};
        $('.data-field-input').each(function () { cur[$(this).data('key')] = parseInt($(this).val()) || 0; });
        $('.data-field-bool').each(function () { cur[$(this).data('key')] = $(this).is(':checked') ? 1 : 0; });
        var tmp = {}; for (var d = 0; d <= 23; d++) tmp['data' + d] = cur['data' + d] || 0;
        $('#dataFieldsContainer').html(buildDataFields(newType, tmp));
        resolveAllInlineHints();
    });

    $(document).on('change', '.data-field-bool', function () { $(this).siblings('.bool-toggle-label').text($(this).is(':checked') ? 'Yes' : 'No'); });
    $(document).on('change', '.data-field-input[data-field-type="spell"]', function () { resolveSpellHint($(this).data('key'), parseInt($(this).val()) || 0); });
    $(document).on('change', '.data-field-input[data-field-type="quest"]', function () { resolveQuestHint($(this).data('key'), parseInt($(this).val()) || 0); });
    $(document).on('click', '.btn-pick-spell', function () { openSpellPicker($(this).data('key')); });

    // Inherited data toggle
    $(document).on('click', '#inheritedDataToggle', function () {
        var $body = $('#inheritedDataBody');
        var $chev = $(this).find('.inherited-chevron');
        $body.slideToggle(200);
        $chev.toggleClass('open');
    });

    var spellSearchTimer = null;
    $('#spellPickerSearch').on('input', function () {
        clearTimeout(spellSearchTimer);
        spellSearchTimer = setTimeout(function () { spellPickerQuery = $('#spellPickerSearch').val(); spellPickerPage = 1; loadSpellPickerPage(); }, 300);
    });
    $('#btnSpellPickerPrev').on('click', function () { if (spellPickerPage > 1) { spellPickerPage--; loadSpellPickerPage(); } });
    $('#btnSpellPickerNext').on('click', function () { spellPickerPage++; loadSpellPickerPage(); });
    $(document).on('click', '.sp-pick-row', function () { selectSpell($(this).data('entry')); });

    $(document).on('click', '#btnCheckModel', function () {
        var did = parseInt($('#editFieldDisplayId').val()) || 0;
        if (did <= 0) { showToast('Enter a Display ID first', 'error'); return; }
        $.getJSON('/GameObjects/ModelExists', { displayId: did }, function (data) {
            if (data.exists) {
                $('#editModelPreview').html('<div class="model-preview-container" style="height:150px;"><model-viewer src="' + esc(data.path) + '" auto-rotate camera-controls shadow-intensity="0.5" exposure="1.2" style="width:100%;height:100%;--poster-color:transparent;"></model-viewer></div>');
                showToast('3D model found', 'success');
            } else { $('#editModelPreview').html(''); showToast('No 3D model for Display ID ' + did, 'error'); }
        });
    });

    $(document).on('input', '#editFieldName', function () { $('#editGoHeaderName').text($(this).val() || 'New Object'); });
    $('#goChangelogToggle').on('click', function () { $(this).toggleClass('collapsed'); $('#goChangelogBody').toggleClass('collapsed'); });
    $('#btnResetGoOG').on('click', function () {
        if (!currentDetailEntry || currentDetailEntry >= CUSTOM_RANGE_START) return;
        BaselineSystem.resetGameObject(currentDetailEntry, currentDetailObj ? currentDetailObj.name : null, function (ok) {
            if (ok) { loadDetail(currentDetailEntry); doSearch(currentPage); }
        });
    });

    // Custom only filter
    $('#chkCustomOnly').on('change', function () { doSearch(1); });

    // ===================== CUSTOM SUMMARY MODAL =====================

    $('#btnCustomSummary').on('click', function () {
        loadCustomSummary();
        var modalEl = document.getElementById('customSummaryModal');
        if (modalEl) new bootstrap.Modal(modalEl).show();
    });

    function loadCustomSummary() {
        $('#customSummaryBody').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i> Loading custom objects...</div>');

        $.getJSON('/GameObjects/CustomSummary', function (data) {
            if (data.totalCount === 0) {
                $('#customSummaryBody').html(
                    '<div class="text-center p-4 text-muted">' +
                    '<i class="fa-solid fa-cubes" style="font-size:24px; margin-bottom:8px; display:block;"></i>' +
                    'No custom game objects yet. Clone an existing object to get started.</div>');
                return;
            }

            // Group by type
            var groups = {};
            data.objects.forEach(function (obj) {
                var t = obj.type;
                if (!groups[t]) groups[t] = [];
                groups[t].push(obj);
            });

            var h = '<div class="cs-total">Total: <strong>' + data.totalCount + '</strong> custom object' + (data.totalCount > 1 ? 's' : '') + '</div>';

            var typeKeys = Object.keys(groups).sort(function (a, b) { return +a - +b; });
            typeKeys.forEach(function (typeId) {
                var items = groups[typeId];
                var typeName = TYPE_NAMES[+typeId] || 'Type ' + typeId;
                var groupId = 'csGroup' + typeId;

                h += '<div class="cs-group">' +
                    '<div class="cs-group-header" data-cs-group="' + groupId + '">' +
                    '<i class="fa-solid fa-chevron-right cs-group-chevron"></i> ' +
                    '<span class="cs-group-name">' + esc(typeName) + '</span>' +
                    '<span class="cs-group-count">' + items.length + '</span>' +
                    '</div>' +
                    '<div class="cs-group-body" id="' + groupId + '">';

                items.forEach(function (obj) {
                    var detail = buildSummaryDetail(obj, +typeId, data.spellNames, data.spawnCounts);
                    var spawns = data.spawnCounts[obj.entry] || 0;

                    h += '<div class="cs-item" data-entry="' + obj.entry + '">' +
                        '<div class="cs-item-main">' +
                        '<div class="cs-item-name">' + esc(obj.name) + '</div>' +
                        '<div class="cs-item-meta">#' + obj.entry + (detail ? ' · ' + detail : '') + '</div>' +
                        '</div>' +
                        '<div class="cs-item-right">' +
                        (spawns > 0
                            ? '<span class="cs-spawn-badge" title="World spawns"><i class="fa-solid fa-location-dot"></i> ' + spawns + '</span>'
                            : '<span class="cs-spawn-badge none" title="No spawns">0</span>') +
                        '</div>' +
                        '</div>';
                });

                h += '</div></div>';
            });

            $('#customSummaryBody').html(h);
        }).fail(function () {
            $('#customSummaryBody').html('<div class="text-center text-muted p-4">Failed to load summary</div>');
        });
    }

    function buildSummaryDetail(obj, type, spellNames, spawnCounts) {
        var d0 = obj.data0 || 0;
        var d1 = obj.data1 || 0;
        var d3 = obj.data3 || 0;

        switch (type) {
            case 22: // Spell Caster
                var spName = spellNames[d0] || ('Spell #' + d0);
                var charges = d1 === -1 ? 'unlimited' : (d1 + ' charge' + (d1 !== 1 ? 's' : ''));
                return d0 > 0 ? esc(spName) + ' · ' + charges : '';
            case 6: // Trap
                var trapSpell = spellNames[d3] || ('Spell #' + d3);
                return d3 > 0 ? 'Trap → ' + esc(trapSpell) : '';
            case 3: // Chest
                return d1 > 0 ? 'Loot #' + d1 : 'No loot template';
            case 10: // Goober
                return d1 > 0 ? 'Quest #' + d1 : '';
            case 18: // Ritual
                var ritualSpell = spellNames[d1] || ('Spell #' + d1);
                return d1 > 0 ? esc(ritualSpell) : '';
            case 30: // Aura Generator
                var auraSpell = spellNames[obj.data2 || 0] || '';
                return auraSpell ? 'Aura: ' + esc(auraSpell) : '';
            default:
                return '';
        }
    }

    // Summary group expand/collapse
    $(document).on('click', '.cs-group-header', function () {
        var groupId = $(this).data('cs-group');
        $('#' + groupId).slideToggle(200);
        $(this).find('.cs-group-chevron').toggleClass('open');
    });

    // Summary item click → go to object in browser
    $(document).on('click', '.cs-item', function () {
        var entry = $(this).data('entry');
        var modalEl = document.getElementById('customSummaryModal');
        if (modalEl) { var inst = bootstrap.Modal.getInstance(modalEl); if (inst) inst.hide(); }
        // Set custom filter, search for this entry
        $('#chkCustomOnly').prop('checked', false);
        $('#goSearch').val(entry);
        doSearch(1);
        // Load detail after a brief delay for search results to render
        setTimeout(function () { loadDetail(entry); }, 300);
    });

    // ===================== INIT =====================
    doSearch(1);

});
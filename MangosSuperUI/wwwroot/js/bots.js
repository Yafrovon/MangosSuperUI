// MangosSuperUI — Bot Tuner JS (BotBridge + BotBrain SignalR client)

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

    // ===================== STATE =====================
    var connection = null;
    var connected = false;
    var botStates = {};       // guid → BotState (from bridge)
    var botBrains = {};       // guid → brain data (personality, decisions)
    var selectedGuid = null;
    var decisionLog = {};     // guid → array of decision entries
    var decisionCount = 0;    // total decisions since page load (for DPM)
    var dpmStartTime = Date.now();
    var engineEnabled = false;
    var maxTimelineEntries = 100;

    // ===================== SIGNALR =====================
    function initConnection() {
        connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/botbridge')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        // --- Bridge events ---
        connection.on('AllBots', function (bots) {
            botStates = {};
            for (var i = 0; i < bots.length; i++) botStates[bots[i].guid] = bots[i];
            renderRoster();
            updateStats();
        });

        connection.on('BotConnected', function (state) {
            botStates[state.guid] = state;
            renderRoster();
            updateStats();
            updateBotDropdown();
            tlAppend(state.guid, 'Connected: ' + state.name + ' L' + state.level + ' ' + (CLASS_NAMES[state.classId] || ''), 'bt-tl-event');
        });

        connection.on('BotDisconnected', function (guid) {
            if (botStates[guid]) {
                botStates[guid].taskState = 'DISCONNECTED';
                renderRosterCard(guid);
                updateStats();
                tlAppend(guid, 'Disconnected', 'bt-tl-error');
            }
        });

        connection.on('BotStateUpdate', function (state) {
            botStates[state.guid] = state;
            renderRosterCard(state.guid);
            updateStats();
        });

        connection.on('BotEvent', function (evt) {
            var cls = 'bt-tl-event';
            var text = evt.eventType;
            if (evt.eventType === 'KILL') text += ' creature=' + evt.creatureEntry;
            else if (evt.eventType === 'LEVEL_UP') { text += ' → L' + evt.newLevel; cls = 'bt-tl-switch'; }
            else if (evt.eventType === 'QUEST_UPDATE') text += ' quest=' + evt.questId + ' ' + evt.status;
            else text += ' ' + (evt.data || '');
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

    // ===================== ROSTER =====================

    function renderRoster() {
        var guids = Object.keys(botStates).sort(function (a, b) {
            return (botStates[a].name || '').localeCompare(botStates[b].name || '');
        });

        if (guids.length === 0) {
            $('#rosterEmpty').show();
            $('#botRoster .bt-roster-card').remove();
            return;
        }
        $('#rosterEmpty').hide();

        var existing = {};
        $('#botRoster .bt-roster-card').each(function () { existing[$(this).data('guid')] = true; });

        for (var i = 0; i < guids.length; i++) {
            var guid = parseInt(guids[i]);
            if (!existing[guid]) {
                $('#botRoster').append('<div class="bt-roster-card" data-guid="' + guid + '" id="roster-' + guid + '"></div>');
            }
            renderRosterCard(guid);
        }
        updateBotDropdown();
    }

    function renderRosterCard(guid) {
        var s = botStates[guid];
        if (!s) return;
        var $card = $('#roster-' + guid);
        if ($card.length === 0) return;

        var isDisc = s.taskState === 'DISCONNECTED';
        var isDead = s.isDead;
        var dotCls = isDisc ? 'offline' : (isDead ? 'dead' : 'alive');

        var brain = botBrains[guid];
        var actText = 'IDLE';
        var actCls = 'bt-act-idle';
        if (brain && brain.lastDecision) {
            actText = brain.lastDecision.newActivity;
            actCls = 'bt-act-' + actText.toLowerCase();
        } else if (s.inCombat) {
            actText = 'COMBAT';
            actCls = 'bt-act-grinding';
        } else if (s.taskState && s.taskState !== 'IDLE') {
            actText = s.taskState;
        }

        var className = CLASS_NAMES[s.classId] || '?';
        var raceName = RACE_NAMES[s.race] || '?';

        $card.html(
            '<span class="bt-roster-dot ' + dotCls + '"></span>' +
            '<div class="bt-roster-info">' +
            '<div class="bt-roster-name">' + esc(s.name) + '</div>' +
            '<div class="bt-roster-meta">L' + s.level + ' ' + raceName + ' <span class="bt-class-badge ' + (CLASS_CSS[s.classId] || '') + '">' + className + '</span></div>' +
            '</div>' +
            '<span class="bt-roster-activity ' + actCls + '">' + actText + '</span>'
        );

        $card.toggleClass('disconnected', isDisc);
        $card.toggleClass('selected', selectedGuid === guid);
    }

    // ===================== DETAIL PANEL =====================

    function renderDetail() {
        if (!selectedGuid) {
            $('#detailEmpty').show();
            return;
        }
        $('#detailEmpty').hide();

        var s = botStates[selectedGuid];
        var brain = botBrains[selectedGuid];
        if (!s) return;

        var html = '';

        // --- Bot Header ---
        var className = CLASS_NAMES[s.classId] || '?';
        var raceName = RACE_NAMES[s.race] || '?';
        var hpPct = s.maxHealth > 0 ? Math.round(s.health / s.maxHealth * 100) : 0;
        var mpPct = s.maxMana > 0 ? Math.round(s.mana / s.maxMana * 100) : 0;

        html += '<div class="bt-section"><div class="bt-section-body">' +
            '<div class="d-flex align-items-center justify-content-between mb-2">' +
            '<div><span style="font-size:16px;font-weight:700;">' + esc(s.name) + '</span> ' +
            '<span class="bt-class-badge ' + (CLASS_CSS[s.classId] || '') + '">' + className + '</span></div>' +
            '<div style="font-size:12px;color:var(--text-muted);">L' + s.level + ' ' + raceName + ' — Map ' + s.mapId + ' (' + s.x.toFixed(0) + ', ' + s.y.toFixed(0) + ')</div>' +
            '</div>' +
            '<div class="d-flex gap-3" style="font-size:12px;">' +
            '<div><span style="color:#9ece6a;">HP ' + hpPct + '%</span></div>' +
            '<div><span style="color:#7aa2f7;">MP ' + mpPct + '%</span></div>' +
            (s.inCombat ? '<div><span style="color:#f7768e;font-weight:600;">IN COMBAT</span></div>' : '') +
            (s.isDead ? '<div><span style="color:#f7768e;font-weight:600;">DEAD</span></div>' : '') +
            '</div>' +
            '</div></div>';

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

            // Quirks
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

        // --- Economy ---
        var copper = brain && brain.copper ? brain.copper : 0;
        var gold = Math.floor(copper / 10000);
        var silver = Math.floor((copper % 10000) / 100);
        var copperRem = copper % 100;
        var invCount = brain && brain.inventoryCount ? brain.inventoryCount : 0;

        html += '<div class="bt-section"><div class="bt-section-header"><span><i class="fa-solid fa-coins" style="color:#e0af68;margin-right:6px;"></i>Shadow Economy</span></div>';
        html += '<div class="bt-section-body"><div class="bt-econ-grid">';
        html += '<div class="bt-econ-item"><div class="bt-econ-val" style="color:#e0af68;">' + gold + 'g ' + silver + 's ' + copperRem + 'c</div><div class="bt-econ-label">Gold Balance</div></div>';
        html += '<div class="bt-econ-item"><div class="bt-econ-val">' + invCount + '</div><div class="bt-econ-label">Inventory Items</div></div>';
        html += '<div class="bt-econ-item"><div class="bt-econ-val">' + (brain && brain.hasUnlearnedSpells ? '<span style="color:#f7768e;">Yes</span>' : '<span style="color:#9ece6a;">No</span>') + '</div><div class="bt-econ-label">Needs Training</div></div>';
        html += '</div></div></div>';

        // --- Activity Timeline ---
        html += '<div class="bt-section"><div class="bt-section-header"><span><i class="fa-solid fa-clock-rotate-left" style="color:var(--accent);margin-right:6px;"></i>Activity Timeline</span></div>';
        html += '<div class="bt-section-body"><div class="bt-timeline" id="timeline"></div></div></div>';

        $('#detailPanel').html(html);
        renderTimeline(selectedGuid);
    }

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

    function renderTimeline(guid) {
        var $tl = $('#timeline');
        if ($tl.length === 0) return;

        var entries = decisionLog[guid];
        if (!entries || entries.length === 0) {
            $tl.html('<div style="color:#5f6b7a;">No decisions recorded yet.</div>');
            return;
        }

        var html = '';
        // Show last 30
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

    // Per-bot timeline append (for events not from BotDecision)
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

        var elapsed = (Date.now() - dpmStartTime) / 60000; // minutes
        var dpm = elapsed > 0 ? Math.round(decisionCount / elapsed) : 0;
        $('#statDpm').text(dpm);
    }

    function updateBotDropdown() {
        var $sel = $('#cmdBotSelect');
        var current = $sel.val();
        $sel.find('option:not(:first)').remove();
        var guids = Object.keys(botStates).sort(function (a, b) {
            return (botStates[a].name || '').localeCompare(botStates[b].name || '');
        });
        for (var i = 0; i < guids.length; i++) {
            var s = botStates[guids[i]];
            if (s.taskState === 'DISCONNECTED') continue;
            $sel.append('<option value="' + s.guid + '">' + s.name + ' (L' + s.level + ')</option>');
        }
        if (current) $sel.val(current);
    }

    // ===================== ENGINE TOGGLE =====================

    $('#engineToggle').on('click', function () {
        engineEnabled = !engineEnabled;
        $(this).toggleClass('active', engineEnabled);
        $(this).find('.bt-engine-label').text(engineEnabled ? 'Engine On' : 'Engine Off');

        // POST to controller to toggle
        $.post('/Bots/ToggleBrain', { enabled: engineEnabled });
    });

    // ===================== ROSTER SELECTION =====================

    $(document).on('click', '.bt-roster-card', function () {
        var guid = parseInt($(this).data('guid'));
        selectedGuid = guid;
        $('.bt-roster-card').removeClass('selected');
        $(this).addClass('selected');

        // Fetch brain data if we don't have it
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

    // ===================== COMMAND BAR =====================

    $('#cmdType').on('change', function () {
        var type = $(this).val();
        $('#cmdParamsMoveTo, #cmdParamsSay, #cmdParamsQuest, #cmdParamsSpell, #cmdParamsTarget').hide();
        switch (type) {
            case 'move_to': $('#cmdParamsMoveTo').show(); break;
            case 'say': case 'yell': $('#cmdParamsSay').show(); break;
            case 'accept_quest': case 'complete_quest': case 'abandon_quest': $('#cmdParamsQuest').show(); break;
            case 'learn_spell': $('#cmdParamsSpell').show(); break;
            case 'attack_target': case 'interact_npc': $('#cmdParamsTarget').show(); break;
        }
    });

    $('#btnSendCmd').on('click', function () {
        if (!connected) return;
        var guid = parseInt($('#cmdBotSelect').val());
        var cmdType = $('#cmdType').val();

        switch (cmdType) {
            case 'move_to':
                var m = parseInt($('#cmdMapId').val()) || 0, x = parseFloat($('#cmdX').val()) || 0;
                var y = parseFloat($('#cmdY').val()) || 0, z = parseFloat($('#cmdZ').val()) || 0;
                if (guid === 0) connection.invoke('SendMoveToAll', m, x, y, z).catch(logErr);
                else connection.invoke('SendMoveTo', guid, m, x, y, z).catch(logErr);
                break;
            case 'say': case 'yell':
                var text = $('#cmdText').val().trim();
                if (!text) return;
                var chatType = (cmdType === 'yell') ? 6 : 0;
                if (guid === 0) {
                    Object.keys(botStates).forEach(function (g) {
                        if (botStates[g].taskState !== 'DISCONNECTED')
                            connection.invoke('SendSayText', parseInt(g), text, chatType).catch(logErr);
                    });
                } else connection.invoke('SendSayText', guid, text, chatType).catch(logErr);
                $('#cmdText').val('');
                break;
            case 'accept_quest':
                var qid = parseInt($('#cmdQuestId').val()) || 0;
                if (qid && guid) connection.invoke('SendAcceptQuest', guid, qid).catch(logErr);
                break;
            case 'complete_quest':
                var qid = parseInt($('#cmdQuestId').val()) || 0;
                if (qid && guid) connection.invoke('SendCompleteQuest', guid, qid).catch(logErr);
                break;
            case 'abandon_quest':
                var qid = parseInt($('#cmdQuestId').val()) || 0;
                if (qid && guid) connection.invoke('SendAbandonQuest', guid, qid).catch(logErr);
                break;
            case 'learn_spell':
                var sid = parseInt($('#cmdSpellId').val()) || 0;
                if (sid && guid) connection.invoke('SendLearnSpell', guid, sid).catch(logErr);
                break;
            case 'attack_target':
                var tg = parseInt($('#cmdTargetGuid').val()) || 0;
                if (tg && guid) connection.invoke('SendAttackTarget', guid, tg).catch(logErr);
                break;
            case 'interact_npc':
                var ng = parseInt($('#cmdTargetGuid').val()) || 0;
                if (ng && guid) connection.invoke('SendInteractNpc', guid, ng).catch(logErr);
                break;
        }
        function logErr(err) { console.error('Cmd send failed:', err); }
    });

    $('#cmdText').on('keydown', function (e) {
        if (e.key === 'Enter') { e.preventDefault(); $('#btnSendCmd').click(); }
    });

    // ===================== HELPERS =====================

    function esc(s) {
        if (!s) return '';
        var d = document.createElement('div');
        d.textContent = s;
        return d.innerHTML;
    }

    function capitalize(s) {
        return s.charAt(0).toUpperCase() + s.slice(1);
    }

    // ===================== INIT =====================
    initConnection();

    // DPM counter refresh
    setInterval(updateStats, 5000);
});

// MangosSuperUI — Bots Page JS (BotBridge SignalR client)

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

    // ===================== STATE =====================
    var connection = null;
    var connected = false;
    var botStates = {}; // guid → state object
    var maxLogLines = 500;

    // ===================== SIGNALR =====================
    function initConnection() {
        connection = new signalR.HubConnectionBuilder()
            .withUrl('/hubs/botbridge')
            .withAutomaticReconnect([0, 2000, 5000, 10000, 30000])
            .build();

        // --- Server → Client ---

        connection.on('AllBots', function (bots) {
            botStates = {};
            for (var i = 0; i < bots.length; i++) {
                botStates[bots[i].guid] = bots[i];
            }
            renderGrid();
            updateStats();
            logAppend('Loaded ' + bots.length + ' bot(s) from server', 'log-sys');
        });

        connection.on('BotConnected', function (state) {
            botStates[state.guid] = state;
            renderGrid();
            updateStats();
            updateBotDropdown();
            logAppend('CONNECTED: ' + state.name + ' (L' + state.level + ' ' + (CLASS_NAMES[state.classId] || '?') + ')', 'log-sys');
        });

        connection.on('BotDisconnected', function (guid) {
            if (botStates[guid]) {
                var name = botStates[guid].name;
                botStates[guid].taskState = 'DISCONNECTED';
                renderBotCard(guid);
                updateStats();
                logAppend('DISCONNECTED: ' + name + ' (guid=' + guid + ')', 'log-err');
            }
        });

        connection.on('BotStateUpdate', function (state) {
            botStates[state.guid] = state;
            renderBotCard(state.guid);
            updateStats();
        });

        connection.on('BotEvent', function (evt) {
            var text = '[' + evt.name + '] ' + evt.eventType;
            switch (evt.eventType) {
                case 'KILL':
                    text += ': creature entry=' + evt.creatureEntry + ' guid=' + evt.creatureGuid;
                    logAppend(text, 'log-event');
                    break;
                case 'QUEST_UPDATE':
                    text += ': quest ' + evt.questId + ' → ' + evt.status;
                    logAppend(text, 'log-state');
                    break;
                case 'LEVEL_UP':
                    text += ': now level ' + evt.newLevel;
                    logAppend(text, 'log-sys');
                    // Update local state
                    if (botStates[evt.guid]) {
                        botStates[evt.guid].level = evt.newLevel;
                        renderBotCard(evt.guid);
                        updateBotDropdown();
                    }
                    break;
                default:
                    text += ': ' + (evt.data || '');
                    logAppend(text, 'log-event');
                    break;
            }
        });

        connection.on('BotChatReceived', function (chat) {
            logAppend('[' + chat.botName + '] WHISPER from ' + chat.senderName + ': ' + chat.message, 'log-chat');
        });

        connection.on('CommandAck', function (ack) {
            logAppend('CMD OK: ' + ack.command + (ack.guid ? ' → bot ' + ack.guid : ''), 'log-state');
        });

        // --- Connection lifecycle ---

        connection.onreconnecting(function () { setStatus('offline'); });
        connection.onreconnected(function () {
            setStatus('online');
            connection.invoke('GetAllBots').catch(function () { });
        });
        connection.onclose(function () { setStatus('offline'); });

        connection.start()
            .then(function () {
                setStatus('online');
                connection.invoke('GetAllBots').catch(function () { });
            })
            .catch(function (err) {
                setStatus('error');
                logAppend('SignalR connect failed: ' + err.toString(), 'log-err');
            });
    }

    function setStatus(state) {
        connected = (state === 'online');
        $('#bridgeStatus').removeClass('online offline error').addClass(state);
        var labels = { online: 'Bridge: Connected', offline: 'Bridge: Disconnected', error: 'Bridge: Error' };
        $('#bridgeStatusText').text(labels[state] || state);
    }

    // ===================== RENDERING =====================

    function renderGrid() {
        var guids = Object.keys(botStates);
        if (guids.length === 0) {
            $('#botGridEmpty').show();
            // Remove any existing cards
            $('#botGrid .bot-card').remove();
            return;
        }
        $('#botGridEmpty').hide();

        // Build set of existing card GUIDs
        var existing = {};
        $('#botGrid .bot-card').each(function () {
            existing[$(this).data('guid')] = true;
        });

        // Add missing cards
        for (var i = 0; i < guids.length; i++) {
            var guid = parseInt(guids[i]);
            if (!existing[guid]) {
                var html = buildCardHtml(guid);
                $('#botGrid').append(html);
            }
            renderBotCard(guid);
        }

        updateBotDropdown();
    }

    function buildCardHtml(guid) {
        return '<div class="bot-card" data-guid="' + guid + '" id="bot-' + guid + '">' +
            '<div class="bot-card-header">' +
            '<span class="bot-card-name"></span>' +
            '<span class="bot-card-class"></span>' +
            '</div>' +
            '<div class="bot-card-bar bar-health"><div class="bot-card-bar-fill"></div></div>' +
            '<div class="bot-card-bar bar-mana"><div class="bot-card-bar-fill"></div></div>' +
            '<div class="bot-card-info">' +
            '<span class="bot-card-pos"></span>' +
            '<span class="bot-card-level"></span>' +
            '</div>' +
            '<div class="bot-card-task"></div>' +
            '</div>';
    }

    function renderBotCard(guid) {
        var state = botStates[guid];
        if (!state) return;

        var $card = $('#bot-' + guid);
        if ($card.length === 0) return;

        // Name + status dot
        var dotClass = state.taskState === 'DISCONNECTED' ? 'dot-disconnected' : 'dot-connected';
        $card.find('.bot-card-name').html(
            '<span class="bot-card-status-dot ' + dotClass + '"></span>' + state.name
        );

        // Class badge
        var className = CLASS_NAMES[state.classId] || 'Unknown';
        var classCss = CLASS_CSS[state.classId] || '';
        $card.find('.bot-card-class').text(className).attr('class', 'bot-card-class ' + classCss);

        // Health bar
        var hpPct = state.maxHealth > 0 ? (state.health / state.maxHealth * 100) : 0;
        $card.find('.bar-health .bot-card-bar-fill').css('width', hpPct + '%');

        // Mana bar
        var manaPct = state.maxMana > 0 ? (state.mana / state.maxMana * 100) : 0;
        $card.find('.bar-mana .bot-card-bar-fill').css('width', manaPct + '%');

        // Position
        $card.find('.bot-card-pos').text(
            'Map ' + state.mapId + ' (' + state.x.toFixed(0) + ', ' + state.y.toFixed(0) + ')'
        );

        // Level
        var raceStr = RACE_NAMES[state.race] || '?';
        $card.find('.bot-card-level').text('L' + state.level + ' ' + raceStr);

        // Task state
        var taskText = state.taskState || 'IDLE';
        var taskClass = 'bot-card-task';
        if (state.inCombat || taskText === 'COMBAT') taskClass += ' task-combat';
        else if (taskText === 'MOVING' || taskText === 'MOVE_TO') taskClass += ' task-moving';
        else taskClass += ' task-idle';

        if (state.isDead) taskText = 'DEAD';
        if (state.inCombat) taskText = 'COMBAT';

        $card.find('.bot-card-task').text(taskText).attr('class', taskClass);

        // Card state classes
        $card.toggleClass('combat', state.inCombat);
        $card.toggleClass('dead', state.isDead);
    }

    function updateStats() {
        var guids = Object.keys(botStates);
        var connectedCount = 0, combatCount = 0, deadCount = 0;
        for (var i = 0; i < guids.length; i++) {
            var s = botStates[guids[i]];
            if (s.taskState !== 'DISCONNECTED') connectedCount++;
            if (s.inCombat) combatCount++;
            if (s.isDead) deadCount++;
        }
        $('#countConnected').text(connectedCount);
        $('#countCombat').text(combatCount);
        $('#countDead').text(deadCount);
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

    // ===================== COMMAND BAR =====================

    // Toggle param sections based on command type
    $('#cmdType').on('change', function () {
        var type = $(this).val();
        $('#cmdParamsMoveTo').hide();
        $('#cmdParamsSay').hide();
        $('#cmdParamsQuest').hide();
        $('#cmdParamsSpell').hide();
        $('#cmdParamsTarget').hide();

        switch (type) {
            case 'move_to': $('#cmdParamsMoveTo').show(); break;
            case 'say': case 'yell': $('#cmdParamsSay').show(); break;
            case 'accept_quest': case 'complete_quest': case 'abandon_quest': $('#cmdParamsQuest').show(); break;
            case 'learn_spell': $('#cmdParamsSpell').show(); break;
            case 'attack_target': case 'interact_npc': $('#cmdParamsTarget').show(); break;
        }
    });

    // Send command
    $('#btnSendCmd').on('click', function () {
        if (!connected) { logAppend('Not connected to BotBridge.', 'log-err'); return; }

        var guid = parseInt($('#cmdBotSelect').val());
        var cmdType = $('#cmdType').val();

        if (!guid && cmdType !== 'move_to' && cmdType !== 'say' && cmdType !== 'yell') {
            logAppend('Select a specific bot for this command.', 'log-err');
            return;
        }

        switch (cmdType) {
            case 'move_to':
                var mapId = parseInt($('#cmdMapId').val()) || 0;
                var x = parseFloat($('#cmdX').val()) || 0;
                var y = parseFloat($('#cmdY').val()) || 0;
                var z = parseFloat($('#cmdZ').val()) || 0;
                if (guid === 0) {
                    connection.invoke('SendMoveToAll', mapId, x, y, z).catch(logErr);
                } else {
                    connection.invoke('SendMoveTo', guid, mapId, x, y, z).catch(logErr);
                }
                break;

            case 'say':
            case 'yell':
                var text = $('#cmdText').val().trim();
                if (!text) { logAppend('Enter text to say.', 'log-err'); return; }
                var chatType = (cmdType === 'yell') ? 6 : 0;
                if (guid === 0) {
                    var allGuids = Object.keys(botStates);
                    for (var gi = 0; gi < allGuids.length; gi++) {
                        if (botStates[allGuids[gi]].taskState !== 'DISCONNECTED')
                            connection.invoke('SendSayText', parseInt(allGuids[gi]), text, chatType).catch(logErr);
                    }
                } else {
                    connection.invoke('SendSayText', guid, text, chatType).catch(logErr);
                }
                $('#cmdText').val('');
                break;

            case 'accept_quest':
                var qid = parseInt($('#cmdQuestId').val()) || 0;
                if (!qid) { logAppend('Enter a quest ID.', 'log-err'); return; }
                connection.invoke('SendAcceptQuest', guid, qid).catch(logErr);
                break;

            case 'complete_quest':
                var qid = parseInt($('#cmdQuestId').val()) || 0;
                if (!qid) { logAppend('Enter a quest ID.', 'log-err'); return; }
                connection.invoke('SendCompleteQuest', guid, qid).catch(logErr);
                break;

            case 'abandon_quest':
                var qid = parseInt($('#cmdQuestId').val()) || 0;
                if (!qid) { logAppend('Enter a quest ID.', 'log-err'); return; }
                connection.invoke('SendAbandonQuest', guid, qid).catch(logErr);
                break;

            case 'learn_spell':
                var sid = parseInt($('#cmdSpellId').val()) || 0;
                if (!sid) { logAppend('Enter a spell ID.', 'log-err'); return; }
                connection.invoke('SendLearnSpell', guid, sid).catch(logErr);
                break;

            case 'attack_target':
                var tguid = parseInt($('#cmdTargetGuid').val()) || 0;
                if (!tguid) { logAppend('Enter a target GUID.', 'log-err'); return; }
                connection.invoke('SendAttackTarget', guid, tguid).catch(logErr);
                break;

            case 'interact_npc':
                var nguid = parseInt($('#cmdTargetGuid').val()) || 0;
                if (!nguid) { logAppend('Enter an NPC GUID.', 'log-err'); return; }
                connection.invoke('SendInteractNpc', guid, nguid).catch(logErr);
                break;
        }

        function logErr(err) { logAppend('Send failed: ' + err.toString(), 'log-err'); }
    });

    // Enter key in text input triggers main Send button
    $('#cmdText').on('keydown', function (e) {
        if (e.key === 'Enter') { e.preventDefault(); $('#btnSendCmd').click(); }
    });

    // ===================== EVENT LOG =====================

    function logAppend(text, cls) {
        var el = document.getElementById('eventLog');
        var ts = new Date().toLocaleTimeString();
        var div = document.createElement('div');
        div.className = cls || 'log-sys';
        div.textContent = '[' + ts + '] ' + text;
        el.appendChild(div);
        while (el.children.length > maxLogLines) el.removeChild(el.children[0]);
        el.scrollTop = el.scrollHeight;
    }

    $('#btnClearLog').on('click', function () {
        $('#eventLog').empty();
        logAppend('Log cleared.', 'log-sys');
    });

    // ===================== INIT =====================
    initConnection();
    logAppend('BotBridge UI initialized. Connecting to SignalR hub...', 'log-sys');

});
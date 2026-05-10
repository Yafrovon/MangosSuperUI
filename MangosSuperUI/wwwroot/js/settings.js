// MangosSuperUI — Settings Page JS

$(function () {

    // ===================== LOAD CURRENT CONFIG =====================
    function loadConfig() {
        $.getJSON('/Settings/Current', function (data) {
            var s = data.settings;

            // DB
            $('#cfgMangos').val(s.connectionStrings.mangos);
            $('#cfgCharacters').val(s.connectionStrings.characters);
            $('#cfgRealmd').val(s.connectionStrings.realmd);
            $('#cfgLogs').val(s.connectionStrings.logs);
            $('#cfgAdmin').val(s.connectionStrings.admin);

            // RA
            $('#cfgRaHost').val(s.remoteAccess.host);
            $('#cfgRaPort').val(s.remoteAccess.port);
            $('#cfgRaUser').val(s.remoteAccess.username);
            $('#cfgRaPass').val(s.remoteAccess.password);
            $('#cfgRaTimeout').val(s.remoteAccess.commandTimeoutMs);

            // Paths & Processes
            $('#cfgBinDir').val(s.vmangos.binDirectory);
            $('#cfgLogDir').val(s.vmangos.logDirectory);
            $('#cfgConfDir').val(s.vmangos.configDirectory);
            $('#cfgMangosdProcess').val(s.vmangos.mangosdProcess);
            $('#cfgRealmdProcess').val(s.vmangos.realmdProcess);
            $('#cfgMangosdConfPath').val(s.vmangos.mangosdConfPath);
            $('#cfgLogsDir').val(s.vmangos.logsDir);

            // DBC
            $('#cfgDbcPath').val(s.vmangos.dbcPath);

            // Maps Data
            $('#cfgMapsDataPath').val(s.vmangos.mapsDataPath);

            // Spell Creator Paths (under spellCreator, not vmangos)
            if (s.spellCreator) {
                $('#cfgClientM2Path').val(s.spellCreator.clientM2Path || '');
                $('#cfgClientDataPath').val(s.spellCreator.clientDataPath || '');
                $('#cfgPatchOutputPath').val(s.spellCreator.patchOutputPath || '');
            }

            // Backup
            $('#cfgBackupDir').val(s.vmangos.backupDirectory);
            $('#cfgSourcePath').val(s.vmangos.vmangosSourcePath);
            $('#cfgSqlPath').val(s.vmangos.vmangosSqlPath);

            // Kestrel
            $('#cfgKestrelUrl').val(s.kestrel.url);

            // AI Services
            if (s.spellCreator) {
                // ComfyUI nodes
                renderComfyNodes(s.spellCreator.comfyUI ? s.spellCreator.comfyUI.nodes : []);
                $('#cfgClipModel2').val(s.spellCreator.comfyUI ? s.spellCreator.comfyUI.clipModel2 : '');

                // Ollama
                if (s.spellCreator.ollama) {
                    $('#cfgOllamaUrl').val(s.spellCreator.ollama.baseUrl);
                    $('#cfgOllamaModel').val(s.spellCreator.ollama.model);
                }

                // Vanilla BLP paths
                $('#cfgRawBlpPath').val(s.spellCreator.rawBlpPath || '');
                $('#cfgSpellDataPath').val(s.spellCreator.dataPath || '');
            }

            // Status
            if (data.overrideExists) {
                $('#configStatusTitle').text('Using server-config.json overrides');
                $('#configStatusDetail').text('Config file: ' + data.configFilePath);
                $('#configStatusCard').css('border-left', '3px solid var(--status-online)');
            } else {
                $('#configStatusTitle').text('Using appsettings.json defaults (no override file)');
                $('#configStatusDetail').text('Save settings to create a server-config.json override file.');
                $('#configStatusCard').css('border-left', '3px solid var(--accent)');
            }
        });

        // Also load DBC status + ComfyUI pool status
        loadDbcStatus();
        loadComfyStatus();
    }

    // ===================== COMFYUI NODE MANAGEMENT =====================

    function renderComfyNodes(nodes) {
        var $container = $('#comfyNodesContainer');
        $container.empty();

        if (!nodes || nodes.length === 0) {
            nodes = [{ name: '', baseUrl: '' }];
        }

        nodes.forEach(function (node, idx) {
            $container.append(buildNodeRow(node.name, node.baseUrl, idx));
        });
    }

    function buildNodeRow(name, url, idx) {
        return '<div class="comfy-node-row" data-node-idx="' + idx + '">' +
            '<input type="text" class="form-input node-name-input" placeholder="Name" value="' + escapeAttr(name) + '" />' +
            '<input type="text" class="form-input node-url-input" placeholder="http://192.168.0.244:8188" value="' + escapeAttr(url) + '" />' +
            '<span class="node-status-dot" title="Unknown" style="background: var(--text-muted);"></span>' +
            '<button class="btn-remove-node" title="Remove node"><i class="fa-solid fa-xmark"></i></button>' +
            '</div>';
    }

    // Add node button
    $('#btnAddComfyNode').on('click', function () {
        var idx = $('#comfyNodesContainer .comfy-node-row').length;
        $('#comfyNodesContainer').append(buildNodeRow('', '', idx));
    });

    // Remove node button (delegated)
    $('#comfyNodesContainer').on('click', '.btn-remove-node', function () {
        var $rows = $('#comfyNodesContainer .comfy-node-row');
        if ($rows.length <= 1) {
            showMessage('error', 'At least one ComfyUI node is required.');
            return;
        }
        $(this).closest('.comfy-node-row').remove();
    });

    // Collect node data from UI
    function getComfyNodesFromUI() {
        var nodes = [];
        $('#comfyNodesContainer .comfy-node-row').each(function () {
            var name = $(this).find('.node-name-input').val().trim();
            var url = $(this).find('.node-url-input').val().trim();
            if (url) {
                nodes.push({ name: name || ('node' + (nodes.length + 1)), baseUrl: url });
            }
        });
        return nodes;
    }

    // ===================== COMFYUI POOL STATUS =====================

    function loadComfyStatus() {
        $.getJSON('/Settings/ComfyPoolStatus', function (data) {
            var $panel = $('#comfyStatusPanel');
            var $row = $('#comfyStatusRow');

            if (data && data.length > 0) {
                var chips = '';
                data.forEach(function (node) {
                    var color = node.online
                        ? (node.busy ? 'var(--status-warning)' : 'var(--status-online)')
                        : 'var(--status-error)';
                    var label = node.online
                        ? (node.busy ? 'Busy (' + node.running + ' running, ' + node.pending + ' queued)' : 'Idle')
                        : (node.error ? 'Offline: ' + node.error : 'Offline');

                    chips += '<span class="dbc-count-chip">' +
                        '<span class="node-status-dot" style="background: ' + color + ';"></span> ' +
                        escapeHtml(node.name) + ': <span class="count-val">' + escapeHtml(label) + '</span></span> ';
                });

                $row.html(
                    '<i class="fa-solid fa-circle-check" style="font-size: 13px; color: var(--status-online);"></i>' +
                    '<span style="font-size: 12.5px; color: var(--text-secondary);">ComfyUI node pool</span>' +
                    '<div class="d-flex flex-wrap gap-2 mt-2">' + chips + '</div>'
                );

                var allOnline = data.every(function (n) { return n.online; });
                $panel.css('border-left', '3px solid ' + (allOnline ? 'var(--status-online)' : 'var(--status-warning)'));

                // Also update the dots next to each node row
                data.forEach(function (node) {
                    $('#comfyNodesContainer .comfy-node-row').each(function () {
                        var rowUrl = $(this).find('.node-url-input').val().trim().replace(/\/+$/, '');
                        var nodeUrl = (node.baseUrl || '').replace(/\/+$/, '');
                        if (rowUrl && nodeUrl && rowUrl === nodeUrl) {
                            var dotColor = node.online
                                ? (node.busy ? 'var(--status-warning)' : 'var(--status-online)')
                                : 'var(--status-error)';
                            var dotTitle = node.online
                                ? (node.busy ? 'Busy' : 'Idle')
                                : 'Offline';
                            $(this).find('.node-status-dot')
                                .css('background', dotColor)
                                .attr('title', dotTitle);
                        }
                    });
                });
            } else {
                $row.html(
                    '<i class="fa-solid fa-circle-xmark" style="font-size: 13px; color: var(--text-muted);"></i>' +
                    '<span style="font-size: 12.5px; color: var(--text-secondary);">No ComfyUI nodes configured</span>'
                );
                $panel.css('border-left', '3px solid var(--text-muted)');
            }
        }).fail(function () {
            $('#comfyStatusRow').html(
                '<i class="fa-solid fa-circle-xmark" style="font-size: 13px; color: var(--status-error);"></i>' +
                '<span style="font-size: 12.5px; color: var(--text-secondary);">Could not reach ComfyUI status endpoint</span>'
            );
            $('#comfyStatusPanel').css('border-left', '3px solid var(--status-error)');
        });
    }

    // ===================== DBC STATUS =====================

    function loadDbcStatus() {
        $.getJSON('/Dbc/Status', function (data) {
            var $panel = $('#dbcStatusPanel');
            var $row = $('#dbcStatusRow');

            if (data.isLoaded) {
                var chips = '';
                for (var dbcName in data.counts) {
                    chips += '<span class="dbc-count-chip">' + escapeHtml(dbcName) +
                        ': <span class="count-val">' + data.counts[dbcName] + '</span></span> ';
                }
                $row.html(
                    '<i class="fa-solid fa-circle-check" style="font-size: 13px; color: var(--status-online);"></i>' +
                    '<span style="font-size: 12.5px; color: var(--text-secondary);">DBC loaded from <code>' +
                    escapeHtml(data.dbcPath) + '</code></span>' +
                    '<div class="d-flex flex-wrap gap-2 mt-2">' + chips + '</div>'
                );
                $panel.css('border-left', '3px solid var(--status-online)');
            } else {
                var errMsg = data.error || 'DBC files not loaded';
                $row.html(
                    '<i class="fa-solid fa-triangle-exclamation" style="font-size: 13px; color: var(--status-warning);"></i>' +
                    '<span style="font-size: 12.5px; color: var(--text-secondary);">' + escapeHtml(errMsg) + '</span>' +
                    '<div style="font-size: 11.5px; color: var(--text-muted); margin-top: 4px;">' +
                    'Spell/Item browsers will not show icons until DBC files are available at the configured path.</div>'
                );
                $panel.css('border-left', '3px solid var(--status-warning)');
            }
        }).fail(function () {
            $('#dbcStatusRow').html(
                '<i class="fa-solid fa-circle-xmark" style="font-size: 13px; color: var(--status-error);"></i>' +
                '<span style="font-size: 12.5px; color: var(--text-secondary);">Could not reach DBC status endpoint</span>'
            );
            $('#dbcStatusPanel').css('border-left', '3px solid var(--status-error)');
        });
    }

    // ===================== RELOAD DBC =====================
    $('#btnReloadDbc').on('click', function () {
        var $btn = $(this);
        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Reloading...');

        $('#dbcStatusRow').html(
            '<i class="fa-solid fa-spinner fa-spin" style="font-size: 13px; color: var(--text-muted);"></i>' +
            '<span style="font-size: 12.5px; color: var(--text-secondary);">Reloading DBC files...</span>'
        );

        $.ajax({
            url: '/Dbc/Reload',
            type: 'POST',
            success: function (data) {
                if (data.success) {
                    showMessage('success', 'DBC files reloaded successfully');
                } else {
                    showMessage('error', 'DBC reload failed: ' + (data.error || 'Unknown error'));
                }
            },
            error: function (xhr) {
                showMessage('error', 'DBC reload request failed: ' + xhr.statusText);
            },
            complete: function () {
                $btn.prop('disabled', false).html('<i class="fa-solid fa-arrows-rotate"></i> Reload DBC');
                loadDbcStatus();
            }
        });
    });

    // ===================== SAVE =====================
    $('#btnSaveConfig').on('click', function () {
        var $btn = $(this);
        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Saving...');

        var config = {
            connectionStrings: {
                mangos: $('#cfgMangos').val(),
                characters: $('#cfgCharacters').val(),
                realmd: $('#cfgRealmd').val(),
                logs: $('#cfgLogs').val(),
                admin: $('#cfgAdmin').val()
            },
            remoteAccess: {
                host: $('#cfgRaHost').val(),
                port: parseInt($('#cfgRaPort').val()) || 3443,
                username: $('#cfgRaUser').val(),
                password: $('#cfgRaPass').val(),
                reconnectDelayMs: 3000,
                commandTimeoutMs: parseInt($('#cfgRaTimeout').val()) || 5000
            },
            vmangos: {
                binDirectory: $('#cfgBinDir').val(),
                logDirectory: $('#cfgLogDir').val(),
                configDirectory: $('#cfgConfDir').val(),
                mangosdProcess: $('#cfgMangosdProcess').val() || 'mangosd',
                realmdProcess: $('#cfgRealmdProcess').val() || 'realmd',
                mangosdConfPath: $('#cfgMangosdConfPath').val() || '',
                logsDir: $('#cfgLogsDir').val() || '',
                dbcPath: $('#cfgDbcPath').val() || '',
                mapsDataPath: $('#cfgMapsDataPath').val() || '',
                backupDirectory: $('#cfgBackupDir').val() || '',
                vmangosSourcePath: $('#cfgSourcePath').val() || '',
                vmangosSqlPath: $('#cfgSqlPath').val() || ''
            },
            spellCreator: {
                comfyUI: {
                    nodes: getComfyNodesFromUI(),
                    clipModel2: $('#cfgClipModel2').val() || ''
                },
                ollama: {
                    baseUrl: $('#cfgOllamaUrl').val() || '',
                    model: $('#cfgOllamaModel').val() || ''
                },
                rawBlpPath: $('#cfgRawBlpPath').val() || '',
                dataPath: $('#cfgSpellDataPath').val() || '',
                clientM2Path: $('#cfgClientM2Path').val() || '',
                clientDataPath: $('#cfgClientDataPath').val() || '',
                patchOutputPath: $('#cfgPatchOutputPath').val() || ''
            },
            kestrel: {
                url: $('#cfgKestrelUrl').val()
            }
        };

        $.ajax({
            url: '/Settings/Save',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(config),
            success: function (data) {
                if (data.success) {
                    showMessage('success', data.message);
                } else {
                    showMessage('error', 'Save failed: ' + data.error);
                }
            },
            error: function (xhr) {
                showMessage('error', 'Request failed: ' + xhr.statusText);
            },
            complete: function () {
                $btn.prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save Settings');
                loadConfig(); // Refresh status
            }
        });
    });

    // ===================== RESET =====================
    $('#btnResetConfig').on('click', function () {
        if (!confirm('This will delete server-config.json and revert to appsettings.json defaults on next restart. Continue?')) {
            return;
        }

        var $btn = $(this);
        $btn.prop('disabled', true);

        $.ajax({
            url: '/Settings/Reset',
            type: 'POST',
            success: function (data) {
                if (data.success) {
                    showMessage('success', data.message);
                } else {
                    showMessage('error', 'Reset failed: ' + data.error);
                }
            },
            error: function (xhr) {
                showMessage('error', 'Request failed: ' + xhr.statusText);
            },
            complete: function () {
                $btn.prop('disabled', false);
                loadConfig();
            }
        });
    });

    // ===================== FEEDBACK =====================
    function showMessage(type, text) {
        var icon = type === 'success'
            ? '<i class="fa-solid fa-circle-check" style="color: var(--status-online); font-size: 18px;"></i>'
            : '<i class="fa-solid fa-circle-exclamation" style="color: var(--status-error); font-size: 18px;"></i>';

        $('#saveMessageBody').html(icon + '<div style="font-size: 13.5px;">' + escapeHtml(text) + '</div>');
        $('#saveMessage').show();

        setTimeout(function () { $('#saveMessage').fadeOut(300); }, 6000);
    }

    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    function escapeAttr(text) {
        return (text || '').replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    // ===================== INIT =====================
    loadConfig();

});
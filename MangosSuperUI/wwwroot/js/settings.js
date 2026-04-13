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

            // Backup
            $('#cfgBackupDir').val(s.vmangos.backupDirectory);
            $('#cfgSourcePath').val(s.vmangos.vmangosSourcePath);
            $('#cfgSqlPath').val(s.vmangos.vmangosSqlPath);

            // Kestrel
            $('#cfgKestrelUrl').val(s.kestrel.url);

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

        // Also load DBC status
        loadDbcStatus();
    }

    // ===================== DBC STATUS =====================
    function loadDbcStatus() {
        $.getJSON('/Dbc/Status', function (data) {
            var $panel = $('#dbcStatusPanel');
            var $row = $('#dbcStatusRow');

            if (data.isLoaded) {
                var chips = '';
                if (data.counts) {
                    $.each(data.counts, function (name, count) {
                        chips += '<span class="dbc-count-chip">' + escapeHtml(name) +
                            ': <span class="count-val">' + count.toLocaleString() + '</span></span> ';
                    });
                }
                $row.html(
                    '<i class="fa-solid fa-circle-check" style="font-size: 13px; color: var(--status-online);"></i>' +
                    '<span style="font-size: 12.5px; color: var(--text-secondary);">DBC files loaded from <code>' +
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

    // ===================== INIT =====================
    loadConfig();

});
// MangosSuperUI — Home/Dashboard Page JS

$(function () {

    // ===================== QUICK COMMAND =====================
    function sendQuickCommand() {
        var cmd = $('#quickCommand').val().trim();
        if (!cmd) return;

        var $output = $('#quickOutput');
        var $btn = $('#btnSendQuick');

        $btn.prop('disabled', true);
        $output.append('<div style="color: #7aa2f7;">&gt; ' + escapeHtml(cmd) + '</div>');

        $.ajax({
            url: '/Home/SendCommand',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ command: cmd }),
            success: function (data) {
                if (data.success) {
                    $output.append('<div>' + escapeHtml(data.response || '(no response)') + '</div>');
                } else {
                    $output.append('<div style="color: #f7768e;">Error: ' + escapeHtml(data.error) + '</div>');
                }
            },
            error: function (xhr) {
                $output.append('<div style="color: #f7768e;">Request failed: ' + xhr.statusText + '</div>');
            },
            complete: function () {
                $btn.prop('disabled', false);
                $output.scrollTop($output[0].scrollHeight);
                $('#quickCommand').val('').focus();
            }
        });
    }

    $('#btnSendQuick').on('click', sendQuickCommand);

    $('#quickCommand').on('keydown', function (e) {
        if (e.key === 'Enter') {
            e.preventDefault();
            sendQuickCommand();
        }
    });

    // ===================== QUICK ACTIONS (systemd) =====================
    function processAction(service, action, $btn) {
        var originalHtml = $btn.html();
        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i>');

        $.ajax({
            url: '/Home/ProcessAction',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ service: service, action: action }),
            success: function (data) {
                if (data.success) {
                    $('#quickOutput').append('<div style="color: #9ece6a;">' + escapeHtml(data.message) + '</div>');
                } else {
                    $('#quickOutput').append('<div style="color: #f7768e;">Error: ' + escapeHtml(data.error) + '</div>');
                }
                $('#quickOutput').scrollTop($('#quickOutput')[0].scrollHeight);
            },
            error: function (xhr) {
                $('#quickOutput').append('<div style="color: #f7768e;">Request failed: ' + xhr.statusText + '</div>');
            },
            complete: function () {
                $btn.prop('disabled', false).html(originalHtml);
                setTimeout(pollStatus, 2000);
            }
        });
    }

    function sendRaQuick(cmd, $btn) {
        var originalHtml = $btn.html();
        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i>');

        $.ajax({
            url: '/Home/SendCommand',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ command: cmd }),
            success: function (data) {
                if (data.success) {
                    $('#quickOutput').append('<div style="color: #9ece6a;">' + escapeHtml(data.response || 'OK') + '</div>');
                } else {
                    $('#quickOutput').append('<div style="color: #f7768e;">Error: ' + escapeHtml(data.error) + '</div>');
                }
                $('#quickOutput').scrollTop($('#quickOutput')[0].scrollHeight);
            },
            complete: function () {
                $btn.prop('disabled', false).html(originalHtml);
            }
        });
    }

    $('#btnStartWorld').on('click', function () { processAction('mangosd', 'start', $(this)); });
    $('#btnStopWorld').on('click', function () { processAction('mangosd', 'stop', $(this)); });
    $('#btnStartAuth').on('click', function () { processAction('realmd', 'start', $(this)); });
    $('#btnStopAuth').on('click', function () { processAction('realmd', 'stop', $(this)); });
    $('#btnSaveAll').on('click', function () { sendRaQuick('.saveall', $(this)); });

    $('#btnRestartBoth').on('click', function () {
        var $btn = $(this);
        var originalHtml = $btn.html();
        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i>');

        $.ajax({
            url: '/Home/ProcessAction',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ service: 'realmd', action: 'restart' }),
            complete: function () {
                $.ajax({
                    url: '/Home/ProcessAction',
                    type: 'POST',
                    contentType: 'application/json',
                    data: JSON.stringify({ service: 'mangosd', action: 'restart' }),
                    complete: function () {
                        $btn.prop('disabled', false).html(originalHtml);
                        $('#quickOutput').append('<div style="color: #9ece6a;">Restart both requested</div>');
                        $('#quickOutput').scrollTop($('#quickOutput')[0].scrollHeight);
                        setTimeout(pollStatus, 3000);
                    }
                });
            }
        });
    });

    // ===================== STATUS POLLING =====================
    function pollStatus() {
        $.getJSON('/Home/Status', function (data) {
            // mangosd process
            var mRunning = data.mangosd && data.mangosd.isRunning;
            $('#mangosdStatus').removeClass('online offline').addClass(mRunning ? 'online' : 'offline');
            $('#mangosdText').text(mRunning ? 'Running (PID ' + data.mangosd.pid + ')' : 'Offline');

            // realmd process
            var rRunning = data.realmd && data.realmd.isRunning;
            $('#realmdStatus').removeClass('online offline').addClass(rRunning ? 'online' : 'offline');
            $('#realmdText').text(rRunning ? 'Running (PID ' + data.realmd.pid + ')' : 'Offline');

            // RA
            $('#raStatus').removeClass('online offline error').addClass(data.raConnected ? 'online' : 'offline');
            $('#raStatusText').text(data.raConnected ? 'Connected' : 'Not connected');

            // Server info (from RA .server info parse)
            $('#playersOnline').text(data.playersOnline != null ? data.playersOnline : '—');
            $('#maxOnline').text(data.maxOnline != null ? data.maxOnline : '—');
            $('#serverUptime').text(data.uptime || '—');
            $('#coreRevision').text(data.coreRevision || '—');

            // DB stats
            $('#totalAccounts').text(data.totalAccounts != null ? data.totalAccounts : '—');
            $('#totalCharacters').text(data.totalCharacters != null ? data.totalCharacters : '—');
            $('#gmAccounts').text(data.gmAccounts != null ? data.gmAccounts : '—');
            $('#bannedAccounts').text(data.bannedAccounts != null ? data.bannedAccounts : '—');
        });
    }

    pollStatus();
    setInterval(pollStatus, 20000);

    // ===================== DATABASE HEALTH (once on load) =====================
    function loadDbHealth() {
        var $panel = $('#dbHealthPanel');
        var $body = $('#dbHealthBody');

        $.getJSON('/Home/DbHealth', function (data) {
            var html = '';

            // Per-database connectivity chips
            var dbOrder = [
                { key: 'mangos', label: 'mangos' },
                { key: 'characters', label: 'characters' },
                { key: 'realmd', label: 'realmd' },
                { key: 'logs', label: 'logs' },
                { key: 'vmangos_admin', label: 'vmangos_admin' }
            ];

            html += '<div class="db-health-chips">';
            var allOk = true;
            for (var i = 0; i < dbOrder.length; i++) {
                var db = dbOrder[i];
                var info = data.databases[db.key];
                var ok = info && info.reachable;
                if (!ok) allOk = false;

                var dotClass = ok ? 'online' : 'offline';
                var tooltip = ok ? 'Connected' : (info && info.error ? info.error : 'Unreachable');

                html += '<span class="db-health-chip" title="' + escapeHtml(tooltip) + '">';
                html += '<span class="status-dot ' + dotClass + '" style="width: 8px; height: 8px;"></span>';
                html += '<span class="db-health-chip-label">' + escapeHtml(db.label) + '</span>';
                html += '</span>';
            }
            html += '</div>';

            // Admin DB init status
            if (data.adminInitialized) {
                var detail = '';
                if (data.tablesCreated > 0) {
                    detail = data.tablesCreated + ' table(s) created on this boot';
                } else {
                    detail = 'All tables already existed';
                }

                html += '<div class="db-health-init-status">';
                html += '<i class="fa-solid fa-circle-check" style="color: var(--status-online); font-size: 12px;"></i> ';
                html += '<span>' + escapeHtml(detail) + '</span>';
                html += '</div>';

                $panel.css('border-left-color', allOk ? 'var(--status-online)' : 'var(--status-warning)');
            } else {
                var errMsg = data.adminInitError || 'vmangos_admin bootstrap failed';
                html += '<div class="db-health-init-status db-health-init-error">';
                html += '<i class="fa-solid fa-triangle-exclamation" style="color: var(--status-error); font-size: 12px;"></i> ';
                html += '<span>' + escapeHtml(errMsg) + '</span>';
                html += '</div>';

                $panel.css('border-left-color', 'var(--status-error)');
            }

            $body.html(html);
        }).fail(function () {
            $body.html(
                '<span style="color: var(--text-muted); font-size: 12.5px;">' +
                '<i class="fa-solid fa-circle-xmark" style="color: var(--status-error);"></i> ' +
                'Could not reach health endpoint</span>'
            );
            $panel.css('border-left-color', 'var(--status-error)');
        });
    }

    loadDbHealth();

    // ===================== UTILITY =====================
    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

});
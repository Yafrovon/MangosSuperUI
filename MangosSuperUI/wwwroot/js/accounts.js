// MangosSuperUI — Accounts Page JS

$(function () {

    // ===================== STATE =====================
    var currentPage = 1;
    var currentSearch = '';
    var currentGmFilter = '';
    var currentStatusFilter = '';
    var selectedAccountId = null;
    var searchTimer = null;

    // ===================== INIT =====================
    loadSummary();
    loadList();

    // ===================== SUMMARY =====================
    function loadSummary() {
        $.getJSON('/Accounts/Summary', function (s) {
            $('#badgeTotal').text(s.total);
            $('#badgeOnline').text(s.online);
            $('#badgeGm').text(s.gm);
            $('#badgeBanned').text(s.banned);
            $('#badgeMuted').text(s.muted);
            $('#badgeLocked').text(s.locked);
        });
    }

    // Badge click → filter
    $(document).on('click', '.acct-badge', function () {
        var filter = $(this).data('filter');

        if ($(this).hasClass('active')) {
            // Toggle off
            $(this).removeClass('active');
            currentStatusFilter = '';
            currentGmFilter = '';
        } else {
            $('.acct-badge').removeClass('active');
            $(this).addClass('active');
            currentGmFilter = '';
            currentStatusFilter = filter; // backend now handles 'gm' as gmlevel > 0
        }

        currentPage = 1;
        loadList();
    });

    // ===================== SEARCH =====================
    $('#acctSearch').on('input', function () {
        clearTimeout(searchTimer);
        var val = $(this).val().trim();
        searchTimer = setTimeout(function () {
            currentSearch = val;
            currentPage = 1;
            loadList();
        }, 300);
    });

    $('#acctGmFilter').on('change', function () {
        currentGmFilter = $(this).val();
        currentStatusFilter = '';
        $('.acct-badge').removeClass('active');
        currentPage = 1;
        loadList();
    });

    $('#btnClearFilters').on('click', function () {
        currentSearch = '';
        currentGmFilter = '';
        currentStatusFilter = '';
        currentPage = 1;
        $('#acctSearch').val('');
        $('#acctGmFilter').val('');
        $('.acct-badge').removeClass('active');
        loadList();
    });

    // ===================== LIST =====================
    function loadList() {
        var params = { page: currentPage, pageSize: 50 };
        if (currentSearch) params.q = currentSearch;
        if (currentGmFilter !== '') params.gmLevel = currentGmFilter;
        if (currentStatusFilter) params.status = currentStatusFilter;

        $.getJSON('/Accounts/List', params, function (data) {
            $('#listCount').text('(' + data.total + ')');
            renderTable(data.accounts);
            renderPagination(data.page, data.totalPages, data.total);
        });
    }

    function renderTable(accounts) {
        var $body = $('#acctTableBody');
        $body.empty();

        if (!accounts || accounts.length === 0) {
            $body.append('<tr><td colspan="6" class="text-center text-muted" style="padding: 40px;">No accounts found</td></tr>');
            return;
        }

        for (var i = 0; i < accounts.length; i++) {
            var a = accounts[i];
            var statusHtml = '';
            if (a.online) statusHtml += '<span class="acct-tag acct-tag-online">Online</span> ';
            if (a.isBanned) statusHtml += '<span class="acct-tag acct-tag-banned">Banned</span> ';
            if (a.isMuted) statusHtml += '<span class="acct-tag acct-tag-muted">Muted</span> ';
            if (a.locked) statusHtml += '<span class="acct-tag acct-tag-locked">Locked</span> ';
            if (a.gmLevel > 0) statusHtml += '<span class="acct-tag acct-tag-gm">' + escapeHtml(a.gmLevelName) + '</span> ';
            if (!statusHtml) statusHtml = '<span class="text-muted" style="font-size: 12px;">—</span>';

            var selected = a.id === selectedAccountId ? ' selected' : '';

            var row = '<tr class="acct-row' + selected + '" data-id="' + a.id + '">';
            row += '<td style="color: var(--text-muted); font-size: 12px;">' + a.id + '</td>';
            row += '<td><span style="font-weight: 600;">' + escapeHtml(a.username) + '</span></td>';
            row += '<td style="font-size: 12px;">' + escapeHtml(a.gmLevelName) + '</td>';
            row += '<td style="font-size: 12px;">' + a.characterCount + '</td>';
            row += '<td>' + statusHtml + '</td>';
            row += '<td style="font-size: 12px; color: var(--text-secondary);">' + escapeHtml(a.lastLogin) + '</td>';
            row += '</tr>';

            $body.append(row);
        }
    }

    function renderPagination(page, totalPages, total) {
        var $pg = $('#pagination');
        $pg.empty();

        if (totalPages <= 1) return;

        $pg.append('<button class="acct-page-btn" data-page="' + (page - 1) + '"' + (page <= 1 ? ' disabled' : '') + '><i class="fa-solid fa-chevron-left"></i></button>');
        $pg.append('<span class="acct-page-info">' + page + ' / ' + totalPages + '</span>');
        $pg.append('<button class="acct-page-btn" data-page="' + (page + 1) + '"' + (page >= totalPages ? ' disabled' : '') + '><i class="fa-solid fa-chevron-right"></i></button>');
    }

    $(document).on('click', '.acct-page-btn:not(:disabled)', function () {
        currentPage = parseInt($(this).data('page'));
        loadList();
    });

    // ===================== ROW SELECT → DETAIL =====================
    $(document).on('click', '.acct-row', function () {
        var id = $(this).data('id');
        selectedAccountId = id;

        $('.acct-row').removeClass('selected');
        $(this).addClass('selected');

        loadDetail(id);
    });

    function loadDetail(id) {
        $.getJSON('/Accounts/Detail?id=' + id, function (data) {
            if (!data.found) {
                acctOutput('Account not found.', 'err');
                return;
            }

            var a = data.account;

            // Header
            $('#dUsername').text(a.username);
            $('#dId').text(a.id);
            $('#dJoinDate').text(a.joinDate);
            $('#dStatus').text(a.online ? 'Online' : 'Offline')
                .removeClass('online offline').addClass(a.online ? 'online' : 'offline');

            if (a.gmLevel > 0) {
                $('#dGmBadge').text(a.gmLevelName).show();
            } else {
                $('#dGmBadge').hide();
            }

            // Stats
            $('#dLastIp').text(a.lastIp || '—');
            $('#dLastLogin').text(a.lastLogin);
            $('#dEmail').text(a.email || '—');
            $('#dClient').text([a.os, a.platform].filter(Boolean).join(' / ') || '—');

            // GM level dropdown
            $('#dGmLevelSelect').val(a.gmLevel);

            // Characters
            var $chars = $('#dCharList');
            $chars.empty();
            if (data.characters && data.characters.length > 0) {
                $('#dCharCount').text('(' + data.characters.length + ')');
                for (var i = 0; i < data.characters.length; i++) {
                    var c = data.characters[i];
                    var onlineTag = c.online ? '<span class="acct-tag acct-tag-online" style="margin-left: 6px;">Online</span>' : '';
                    var html = '<div class="acct-char-row">';
                    html += '<div><span class="acct-char-name">' + escapeHtml(c.name) + '</span>' + onlineTag + '</div>';
                    html += '<div class="acct-char-detail">Lv' + c.level + ' ' + escapeHtml(c.race) + ' ' + escapeHtml(c.className) + ' · ' + c.playedTotal + '</div>';
                    html += '</div>';
                    $chars.append(html);
                }
            } else {
                $('#dCharCount').text('(0)');
                $chars.html('<div style="padding: 18px; color: var(--text-muted); text-align: center; font-size: 13px;">No characters</div>');
            }

            // Ban history
            if (data.bans && data.bans.length > 0) {
                $('#banHistoryCard').show();
                var $bans = $('#dBanHistory');
                $bans.empty();
                for (var j = 0; j < data.bans.length; j++) {
                    var b = data.bans[j];
                    var activeClass = b.active ? ' ban-active' : '';
                    var html2 = '<div class="ban-row' + activeClass + '">';
                    html2 += '<div class="d-flex align-items-center justify-content-between">';
                    html2 += '<div><strong>' + escapeHtml(b.banDate) + '</strong>';
                    html2 += ' → ' + escapeHtml(b.unbanDate);
                    if (b.active) html2 += ' <span class="acct-tag acct-tag-banned">Active</span>';
                    html2 += '</div>';
                    html2 += '<div class="text-muted" style="font-size: 12px;">by ' + escapeHtml(b.bannedBy) + '</div>';
                    html2 += '</div>';
                    if (b.banReason) html2 += '<div style="margin-top: 4px; color: var(--text-secondary); font-size: 12.5px;">' + escapeHtml(b.banReason) + '</div>';
                    html2 += '</div>';
                    $bans.append(html2);
                }
            } else {
                $('#banHistoryCard').hide();
            }

            // Show detail, hide empty
            $('#detailEmpty').hide();
            $('#detailContent').show();
        });
    }

    $('#btnRefreshDetail').on('click', function () {
        if (selectedAccountId) loadDetail(selectedAccountId);
    });

    // ===================== ACTIONS =====================
    function sendCommand(cmd) {
        if (!cmd) return;
        acctOutput('> ' + cmd, 'cmd');

        $.ajax({
            url: '/Accounts/RaCommand',
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ command: cmd }),
            success: function (data) {
                if (data.success) {
                    acctOutput(data.response || 'OK', 'ok');
                } else {
                    acctOutput('Error: ' + data.error, 'err');
                }
            },
            error: function (xhr) {
                acctOutput('Request failed: ' + xhr.statusText, 'err');
            },
            complete: function () {
                // Refresh detail + list + summary after actions
                setTimeout(function () {
                    if (selectedAccountId) loadDetail(selectedAccountId);
                    loadList();
                    loadSummary();
                }, 1500);
            }
        });
    }

    $(document).on('click', '.acct-action', function () {
        if (!selectedAccountId) { acctOutput('No account selected.', 'err'); return; }

        var action = $(this).data('action');
        var username = $('#dUsername').text();
        var cmd = null;

        switch (action) {
            case 'ban':
                cmd = '.ban account ' + username + ' -1 Banned by admin';
                break;
            case 'unban':
                cmd = '.unban account ' + username;
                break;
            case 'mute':
                cmd = '.mute ' + username + ' 30';
                break;
            case 'unmute':
                cmd = '.unmute ' + username;
                break;
            case 'lock':
                cmd = '.account lock ' + username;
                break;
            case 'unlock':
                cmd = '.account unlock ' + username;
                break;
            case 'setGmLevel':
                var gl = $('#dGmLevelSelect').val();
                cmd = '.account set gmlevel ' + username + ' ' + gl;
                break;
            case 'setPassword':
                var pw = $('#dNewPass').val().trim();
                if (!pw) { acctOutput('Password cannot be empty.', 'err'); return; }
                cmd = '.account set password ' + username + ' ' + pw + ' ' + pw;
                break;
        }

        if (cmd) {
            sendCommand(cmd);
        }
    });

    // ===================== OUTPUT =====================
    function acctOutput(text, type) {
        var colors = { cmd: '#7aa2f7', ok: '#c8d0da', err: '#f7768e', sys: '#9ece6a' };
        var color = colors[type] || colors.ok;
        var $el = $('#acctOutput');
        $el.append('<div style="color: ' + color + ';">' + escapeHtml(text) + '</div>');
        while ($el[0].children.length > 200) $el[0].removeChild($el[0].children[0]);
        $el.scrollTop($el[0].scrollHeight);
    }

    $('#btnClearOutput').on('click', function () {
        $('#acctOutput').empty();
    });

    // ===================== UTILITY =====================
    function escapeHtml(text) {
        var div = document.createElement('div');
        div.textContent = text;
        return div.innerHTML;
    }

    // ===================== FOCUS =====================
    $('#acctSearch').focus();

});
// MangosSuperUI — Backup Manager JS

$(function () {

    var pendingRestore = null; // { folder, group }
    var pendingDelete = null;  // folder name

    var GROUP_META = {
        world: { label: 'Game World', icon: 'fa-earth-americas', badgeClass: 'world' },
        players: { label: 'Characters', icon: 'fa-users', badgeClass: 'players' },
        core: { label: 'Core Source', icon: 'fa-code', badgeClass: 'core' }
    };

    // ===================== INIT =====================

    loadBackups();
    loadStats();

    // ===================== GROUP TOGGLES =====================

    $('.bk-group-toggle').on('click', function () {
        var cb = $(this).find('input[type="checkbox"]');
        cb.prop('checked', !cb.prop('checked'));
        $(this).toggleClass('active', cb.prop('checked'));
    });

    // ===================== CREATE BACKUP =====================

    $('#btnCreateBackup').on('click', function () {
        var groups = [];
        $('.bk-group-toggle').each(function () {
            if ($(this).find('input').prop('checked'))
                groups.push($(this).data('group'));
        });

        if (groups.length === 0) {
            showToast('Select at least one backup group', 'error');
            return;
        }

        var label = $('#backupLabel').val().trim();
        var btn = $(this);

        btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Backing up...');
        $('#backupProgress').show();
        $('#progressFill').addClass('indeterminate').css('width', '100%');
        $('#progressText').text('Creating backup... this may take a minute for large databases.');

        $.ajax({
            url: '/Backup/Create',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ groups: groups, label: label }),
            success: function (result) {
                btn.prop('disabled', false).html('<i class="fa-solid fa-hard-drive"></i> Create Backup');
                $('#backupProgress').hide();
                $('#progressFill').removeClass('indeterminate').css('width', '0%');

                if (result.success) {
                    showToast('Backup created successfully!', 'success');
                    $('#backupLabel').val('');
                    loadBackups();
                    loadStats();
                } else {
                    showToast('Backup failed: ' + (result.error || 'Unknown error'), 'error');
                }
            },
            error: function () {
                btn.prop('disabled', false).html('<i class="fa-solid fa-hard-drive"></i> Create Backup');
                $('#backupProgress').hide();
                showToast('Backup request failed', 'error');
            }
        });
    });

    // ===================== LOAD BACKUPS =====================

    function loadBackups() {
        $.getJSON('/Backup/List', function (data) {
            var backups = data.backups || [];
            $('#backupCount').text(backups.length + ' backup' + (backups.length !== 1 ? 's' : ''));

            if (backups.length === 0) {
                $('#backupList').html(
                    '<div class="bk-empty">' +
                    '<i class="fa-solid fa-hard-drive"></i>' +
                    '<p>No backups yet. Create your first backup above.</p>' +
                    '</div>'
                );
                return;
            }

            var html = '';
            backups.forEach(function (b) {
                var m = b.manifest;
                html += renderBackupCard(b.folder, m, b.totalSize);
            });
            $('#backupList').html(html);
        }).fail(function () {
            $('#backupList').html('<div class="text-center p-4 text-muted">Failed to load backups</div>');
        });
    }

    function renderBackupCard(folder, manifest, totalSize) {
        var ts = parseTimestamp(folder);
        var isAuto = folder.indexOf('_pre-restore') !== -1;
        var groups = manifest.groups || [];
        var stats = manifest.stats || {};
        var sizes = manifest.sizes || {};
        var label = typeof manifest.label === 'object' ? '' : (manifest.label || '');

        var h = '<div class="bk-card" data-folder="' + esc(folder) + '">';

        // Header row
        h += '<div class="bk-card-header">';
        h += '<div>';
        h += '<span class="bk-card-time">' + esc(ts.time) + '</span>';
        h += '<span class="bk-card-date">' + esc(ts.date) + '</span>';
        if (isAuto) h += '<span class="bk-auto-label">AUTO</span>';
        h += '</div>';
        h += '<span class="bk-card-size">' + formatBytes(totalSize) + '</span>';
        h += '</div>';

        // Label (clickable to edit)
        h += '<div class="bk-card-label" data-folder="' + esc(folder) + '" title="Click to edit">' + esc(label) + '</div>';

        // Group badges
        h += '<div class="bk-card-groups">';
        groups.forEach(function (g) {
            var meta = GROUP_META[g] || { label: g, icon: 'fa-circle', badgeClass: '' };
            h += '<span class="bk-group-badge ' + meta.badgeClass + '">';
            h += '<i class="fa-solid ' + meta.icon + '"></i> ' + esc(meta.label);
            if (sizes[g]) h += ' <span style="opacity:0.7;">(' + esc(typeof sizes[g] === 'string' ? sizes[g] : '') + ')</span>';
            h += '</span>';
        });
        h += '</div>';

        // Stats summary
        h += '<div class="bk-card-stats">';
        if (stats.customItems !== undefined)
            h += '<span class="bk-card-stat"><i class="fa-solid fa-star" style="color: var(--status-online);"></i> ' + stats.customItems + ' custom items</span>';
        if (stats.lootifierItems !== undefined)
            h += '<span class="bk-card-stat"><i class="fa-solid fa-dragon" style="color: #a855f7;"></i> ' + stats.lootifierItems + ' lootifier items</span>';
        if (stats.totalCharacters !== undefined)
            h += '<span class="bk-card-stat"><i class="fa-solid fa-user"></i> ' + stats.totalCharacters + ' characters</span>';
        if (stats.totalAccounts !== undefined)
            h += '<span class="bk-card-stat"><i class="fa-solid fa-id-badge"></i> ' + stats.totalAccounts + ' accounts</span>';
        if (stats.auditLogRows !== undefined)
            h += '<span class="bk-card-stat"><i class="fa-solid fa-clipboard-list"></i> ' + stats.auditLogRows + ' audit rows</span>';
        h += '</div>';

        // Action buttons
        h += '<div class="bk-card-actions">';
        groups.forEach(function (g) {
            var meta = GROUP_META[g] || { label: g };
            h += '<button class="btn-sm btn-outline-subtle bk-restore-btn" data-folder="' + esc(folder) + '" data-group="' + esc(g) + '">';
            h += '<i class="fa-solid fa-rotate-left"></i> Restore ' + esc(meta.label);
            h += '</button>';
        });
        h += '<button class="btn-sm bk-delete-btn" data-folder="' + esc(folder) + '" style="color: var(--status-error); background: none; border: 1px solid var(--status-error); border-radius: var(--radius-sm); padding: 3px 10px; font-size: 11.5px; cursor: pointer;">';
        h += '<i class="fa-solid fa-trash"></i>';
        h += '</button>';
        h += '</div>';

        h += '</div>';
        return h;
    }

    // ===================== STATS =====================

    function loadStats() {
        $.getJSON('/Backup/Stats', function (stats) {
            var h = '';
            h += statCard(stats.totalItems, 'Total Items');
            h += statCard(stats.customItems, 'Custom Items');
            h += statCard(stats.lootifierItems, 'Lootifier Items');
            h += statCard(stats.totalCharacters, 'Characters');
            h += statCard(stats.totalAccounts, 'Accounts');
            h += statCard(stats.auditLogRows, 'Audit Rows');
            $('#statsRow').html(h);
        });
    }

    function statCard(value, label) {
        return '<div class="bk-stat-card">' +
            '<div class="bk-stat-value">' + (value !== undefined ? Number(value).toLocaleString() : '—') + '</div>' +
            '<div class="bk-stat-label">' + esc(label) + '</div>' +
            '</div>';
    }

    // ===================== RESTORE =====================

    $(document).on('click', '.bk-restore-btn', function () {
        var folder = $(this).data('folder');
        var group = $(this).data('group');
        var meta = GROUP_META[group] || { label: group };

        pendingRestore = { folder: folder, group: group };
        $('#restoreGroupName').text(meta.label);
        $('#restoreBackupName').text(folder);

        // Only show server note for DB restores
        if (group === 'core') {
            $('#restoreServerNote').hide();
        } else {
            $('#restoreServerNote').show();
        }

        new bootstrap.Modal($('#restoreModal')[0]).show();
    });

    $('#btnConfirmRestore').on('click', function () {
        if (!pendingRestore) return;
        var btn = $(this);
        btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Restoring...');

        $.ajax({
            url: '/Backup/Restore',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(pendingRestore),
            success: function (result) {
                btn.prop('disabled', false).html('<i class="fa-solid fa-rotate-left"></i> Restore');
                bootstrap.Modal.getInstance($('#restoreModal')[0]).hide();

                if (result.success) {
                    showToast('Restore complete! Safety snapshot: ' + result.safetyBackup, 'success');
                    loadBackups();
                    loadStats();
                } else {
                    showToast('Restore failed: ' + (result.error || 'Unknown error'), 'error');
                }
                pendingRestore = null;
            },
            error: function () {
                btn.prop('disabled', false).html('<i class="fa-solid fa-rotate-left"></i> Restore');
                showToast('Restore request failed', 'error');
                pendingRestore = null;
            }
        });
    });

    // ===================== DELETE =====================

    $(document).on('click', '.bk-delete-btn', function () {
        pendingDelete = $(this).data('folder');
        $('#deleteBackupName').text(pendingDelete);
        new bootstrap.Modal($('#deleteModal')[0]).show();
    });

    $('#btnConfirmDelete').on('click', function () {
        if (!pendingDelete) return;
        var btn = $(this);
        btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i>');

        $.ajax({
            url: '/Backup/Delete',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ folder: pendingDelete }),
            success: function (result) {
                btn.prop('disabled', false).html('<i class="fa-solid fa-trash"></i> Delete');
                bootstrap.Modal.getInstance($('#deleteModal')[0]).hide();

                if (result.success) {
                    showToast('Backup deleted', 'success');
                    loadBackups();
                } else {
                    showToast('Delete failed: ' + (result.error || 'Unknown error'), 'error');
                }
                pendingDelete = null;
            },
            error: function () {
                btn.prop('disabled', false).html('<i class="fa-solid fa-trash"></i> Delete');
                showToast('Delete request failed', 'error');
                pendingDelete = null;
            }
        });
    });

    // ===================== EDIT LABEL =====================

    $(document).on('click', '.bk-card-label', function () {
        var el = $(this);
        var folder = el.data('folder');
        var current = el.text();
        var newLabel = prompt('Backup label:', current);
        if (newLabel === null) return;

        $.ajax({
            url: '/Backup/UpdateLabel',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ folder: folder, label: newLabel }),
            success: function (result) {
                if (result.success) {
                    el.text(newLabel);
                    if (!newLabel) el.hide(); else el.show();
                } else {
                    showToast('Failed to update label', 'error');
                }
            }
        });
    });

    // ===================== HELPERS =====================

    function parseTimestamp(folder) {
        // "2026-04-13_14-30-00" or "2026-04-13_14-30-00_pre-restore"
        var parts = folder.split('_');
        var datePart = parts[0] || '';
        var timePart = (parts[1] || '').replace(/-/g, ':');
        return {
            date: datePart,
            time: timePart || ''
        };
    }

    function formatBytes(bytes) {
        if (!bytes || bytes <= 0) return '0 B';
        if (bytes < 1024) return bytes + ' B';
        if (bytes < 1024 * 1024) return (bytes / 1024).toFixed(1) + ' KB';
        if (bytes < 1024 * 1024 * 1024) return (bytes / (1024 * 1024)).toFixed(1) + ' MB';
        return (bytes / (1024 * 1024 * 1024)).toFixed(1) + ' GB';
    }

    function esc(text) {
        if (text == null) return '';
        var div = document.createElement('div');
        div.textContent = String(text);
        return div.innerHTML;
    }

    function showToast(msg, type) {
        var el = $('<div class="bk-toast ' + type + '">' + esc(msg) + '</div>');
        $('body').append(el);
        setTimeout(function () { el.fadeOut(300, function () { el.remove(); }); }, 4000);
    }

});

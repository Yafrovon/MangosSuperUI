// net.js — network layer.
//
// Sections:
//   1. getJSON / postJSON — Promise wrappers over jQuery
//   2. streamSSE          — POST + read text/event-stream response
//   3. regenerateServerData / restoreVanillaDefaults — SSE-driven flows

// ─────────────────────────────────────────────────────────────────────────────
// 1. JSON wrappers (kept on jQuery — used everywhere in MangosSuperUI)
// ─────────────────────────────────────────────────────────────────────────────

export function getJSON(url) {
    return new Promise(function (resolve, reject) {
        // eslint-disable-next-line no-undef
        $.getJSON(url, function (data) { resolve(data); }).fail(function (xhr, status, err) {
            reject(new Error('getJSON ' + url + ' failed: ' + (err || status)));
        });
    });
}

export function postJSON(url, body) {
    return new Promise(function (resolve, reject) {
        // eslint-disable-next-line no-undef
        $.ajax({
            url: url,
            type: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(body),
            success: function (resp) { resolve(resp); },
            error: function (xhr, status, err) { reject(new Error('postJSON ' + url + ' failed: ' + (err || status))); }
        });
    });
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. streamSSE — POST JSON, read text/event-stream response
// ─────────────────────────────────────────────────────────────────────────────
//
// EventSource doesn't do POST, so we use fetch + a streaming reader. The
// caller's onMessage is called once per "data: ..." line. Resolves when the
// stream ends or rejects on network error.

export function streamSSE(url, body, onMessage) {
    return fetch(url, {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify(body || {})
    }).then(function (response) {
        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = '';

        function read() {
            return reader.read().then(function (result) {
                if (result.done) return;
                buffer += decoder.decode(result.value, { stream: true });
                const lines = buffer.split('\n');
                buffer = lines.pop();
                for (let i = 0; i < lines.length; i++) {
                    const line = lines[i].trim();
                    if (line.startsWith('data: ')) onMessage(line.substring(6));
                }
                return read();
            });
        }

        return read();
    });
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Regenerate Server Data / Restore Vanilla Defaults
// ─────────────────────────────────────────────────────────────────────────────
//
// Both flows append progress to the placement modal's #wmoRegenProgress div.
// Both rebuild the same UI feedback (spinner, color-coded lines, terminal
// state).

function addProgress(progressDiv, msg, color) {
    if (!progressDiv) return;
    const line = document.createElement('div');
    line.style.color     = color || '#ccc';
    line.style.marginTop = '2px';
    line.textContent     = msg;
    progressDiv.appendChild(line);
    progressDiv.scrollTop = progressDiv.scrollHeight;
}

export function regenerateServerData(editor, modal) {
    const store = editor.placementStore;
    const committed = store.placedWmos.filter((p) => p.committed && p.dbId);
    if (committed.length === 0) {
        alert('No committed placements to regenerate server data for');
        return;
    }
    const lastCommitted = committed[committed.length - 1];

    const regenBtn    = modal.querySelector('#wmoRegenServerData');
    const progressDiv = modal.querySelector('#wmoRegenProgress');

    if (regenBtn) {
        regenBtn.disabled = true;
        regenBtn.innerHTML = '<i class="fa-solid fa-spinner fa-spin"></i> Regenerating...';
    }
    if (progressDiv) {
        progressDiv.style.display = 'block';
        progressDiv.innerHTML = '<div style="color:#ffc107;">Starting server data regeneration...</div>';
    }

    streamSSE('/WorldEditor/RegenerateServerData', { placementDbId: lastCommitted.dbId }, (msg) => {
        if (msg.startsWith('DONE:')) {
            addProgress(progressDiv, msg, '#28a745');
            if (regenBtn) {
                regenBtn.disabled = false;
                regenBtn.innerHTML = '<i class="fa-solid fa-check"></i> Server Data Regenerated!';
                regenBtn.className = 'btn btn-sm btn-outline-success';
                setTimeout(() => {
                    regenBtn.innerHTML = '<i class="fa-solid fa-server"></i> Regenerate Server Data (Collision/LoS/Pathing)';
                    regenBtn.className = 'btn btn-sm btn-outline-warning';
                }, 5000);
            }
        } else if (msg.startsWith('ERROR:')) {
            addProgress(progressDiv, msg, '#dc3545');
            if (regenBtn) {
                regenBtn.disabled = false;
                regenBtn.innerHTML = '<i class="fa-solid fa-server"></i> Regenerate Server Data (Collision/LoS/Pathing)';
            }
        } else {
            addProgress(progressDiv, msg, msg.startsWith('  >') ? '#888' : '#ffc107');
        }
    }).then(() => {
        if (regenBtn && regenBtn.disabled) {
            regenBtn.disabled = false;
            regenBtn.innerHTML = '<i class="fa-solid fa-server"></i> Regenerate Server Data (Collision/LoS/Pathing)';
        }
    }).catch((err) => {
        addProgress(progressDiv, 'Request failed: ' + err.message, '#dc3545');
        if (regenBtn) {
            regenBtn.disabled = false;
            regenBtn.innerHTML = '<i class="fa-solid fa-server"></i> Regenerate Server Data (Collision/LoS/Pathing)';
        }
    });
}

export function restoreVanillaDefaults(editor, modal) {
    const restoreBtn  = modal.querySelector('#wmoRestoreDefaults');
    const progressDiv = modal.querySelector('#wmoRegenProgress');

    if (restoreBtn) {
        restoreBtn.disabled = true;
        restoreBtn.innerHTML = '<i class="fa-solid fa-spinner fa-spin"></i> Checking backups...';
    }

    getJSON('/WorldEditor/BackupStatus').then((status) => {
        if (restoreBtn) {
            restoreBtn.disabled = false;
            restoreBtn.innerHTML = '<i class="fa-solid fa-rotate-left"></i> Restore Vanilla Defaults';
        }
        let backupInfo = '';
        if (status.totalBackups === 0) {
            backupInfo = '\n\u26a0 No vanilla backups found!\ndir_bin will be rebuilt from baseline, but vmaps/mmaps\ncannot be restored without .vanilla backup files.\n';
        } else {
            backupInfo = '\nBackups available to restore:\n';
            if (status.dirBinBackup)      backupInfo += '  \u2713 dir_bin.vanilla (' + Math.round(status.dirBinBackupSize / 1024) + ' KB)\n';
            if (status.vmapFiles > 0)     backupInfo += '  \u2713 Server vmaps: ' + status.vmapFiles + ' file(s)\n';
            if (status.mmapFiles > 0)     backupInfo += '  \u2713 Server mmaps: ' + status.mmapFiles + ' file(s)\n';
            if (status.clientVmapFiles>0) backupInfo += '  \u2713 Client vmaps: ' + status.clientVmapFiles + ' file(s)\n';
            if (status.clientMmapFiles>0) backupInfo += '  \u2713 Client mmaps: ' + status.clientMmapFiles + ' file(s)\n';
        }
        if (!confirm('This will:\n' +
            '\u2022 Delete ALL custom WMO placements from the database\n' +
            '\u2022 Restore vanilla server data (vmaps, mmaps)\n' +
            '\u2022 Delete patch-Z.MPQ\n' +
            backupInfo + '\nAre you sure?')) return;
        doRestore(editor, modal);
    }).catch(() => {
        if (restoreBtn) {
            restoreBtn.disabled = false;
            restoreBtn.innerHTML = '<i class="fa-solid fa-rotate-left"></i> Restore Vanilla Defaults';
        }
        if (!confirm('This will:\n\u2022 Delete ALL custom WMO placements from the database\n\u2022 Restore vanilla server data (vmaps, mmaps)\n\u2022 Delete patch-Z.MPQ\n\n(Could not check backup status)\n\nAre you sure?')) return;
        doRestore(editor, modal);
    });
}

function doRestore(editor, modal) {
    const restoreBtn  = modal.querySelector('#wmoRestoreDefaults');
    const progressDiv = modal.querySelector('#wmoRegenProgress');

    if (restoreBtn) {
        restoreBtn.disabled = true;
        restoreBtn.innerHTML = '<i class="fa-solid fa-spinner fa-spin"></i> Restoring...';
    }
    if (progressDiv) {
        progressDiv.style.display = 'block';
        progressDiv.innerHTML = '<div style="color:#ffc107;">Starting vanilla restore...</div>';
    }

    streamSSE('/WorldEditor/RestoreVanillaDefaults', {}, (msg) => {
        if (msg.startsWith('DONE:')) {
            addProgress(progressDiv, msg, '#28a745');
            editor.placementStore.clearAll();
            editor.history.clear();
            if (editor.objectStream) editor.objectStream.clearAll();
            if (restoreBtn) {
                restoreBtn.disabled = false;
                restoreBtn.innerHTML = '<i class="fa-solid fa-check"></i> Defaults Restored!';
                restoreBtn.className = 'btn btn-sm btn-outline-success';
                restoreBtn.style.display = 'none';
                setTimeout(() => {
                    restoreBtn.innerHTML = '<i class="fa-solid fa-rotate-left"></i> Restore Vanilla Defaults';
                    restoreBtn.className = 'btn btn-sm btn-outline-danger';
                }, 5000);
            }
            const dl = modal.querySelector('#wmoDownloadMpq');     if (dl) dl.style.display = 'none';
            const rg = modal.querySelector('#wmoRegenServerData'); if (rg) rg.style.display = 'none';
        } else if (msg.startsWith('ERROR:')) {
            addProgress(progressDiv, msg, '#dc3545');
            if (restoreBtn) {
                restoreBtn.disabled = false;
                restoreBtn.innerHTML = '<i class="fa-solid fa-rotate-left"></i> Restore Vanilla Defaults';
            }
        } else {
            addProgress(progressDiv, msg, msg.startsWith('  >') ? '#888' : '#ffc107');
        }
    }).then(() => {
        if (restoreBtn && restoreBtn.disabled) {
            restoreBtn.disabled = false;
            restoreBtn.innerHTML = '<i class="fa-solid fa-rotate-left"></i> Restore Vanilla Defaults';
        }
    }).catch((err) => {
        addProgress(progressDiv, 'Request failed: ' + err.message, '#dc3545');
        if (restoreBtn) {
            restoreBtn.disabled = false;
            restoreBtn.innerHTML = '<i class="fa-solid fa-rotate-left"></i> Restore Vanilla Defaults';
        }
    });
}

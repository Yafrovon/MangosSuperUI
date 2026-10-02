export function initEquipmentNameRevision() {
    const el = id => document.getElementById(id);
    if (!el('ewRenamePreview')) return;
    const fields = ['ewRenameItem', 'ewRenameDisplay', 'ewRenameOld', 'ewRenameNew'];
    let reviewed = null, report = null, busy = false, serial = 0;
    const invalidate = () => {
        ++serial; reviewed = null; report = null;
        el('ewRenameApply').disabled = true; el('ewRenameReport').disabled = true; el('ewRenameResult').textContent = '';
    };
    for (const id of fields) el(id).addEventListener('input', () => {
        invalidate(); el('ewRenameStatus').textContent = 'Input changed. Preview the exact name change again.';
    });
    async function run(apply) {
        if (busy) return;
        busy = true;
        for (const id of [...fields, 'ewRenamePreview', 'ewRenameApply']) el(id).disabled = true;
        try {
            const prior = reviewed;
            if (apply && !prior) throw new Error('Preview the exact name change first.');
            invalidate(); const requestSerial = serial;
            const body = { itemEntry: Number(el('ewRenameItem').value), displayId: Number(el('ewRenameDisplay').value),
                expectedOldName: el('ewRenameOld').value, newName: el('ewRenameNew').value, apply };
            if (![body.itemEntry, body.displayId].every(x => Number.isSafeInteger(x) && x > 0) || !body.expectedOldName || !body.newName)
                throw new Error('Enter the exact existing IDs and both names.');
            if (apply) Object.assign(body, { expectedWorldRowSha256: prior.beforeWorldRowSha256, expectedRevisionToken: prior.revisionToken });
            el('ewRenameStatus').textContent = apply ? 'Applying reviewed name…' : 'Checking the full item row and recovery requirements…';
            const response = await fetch('/WeaponForge/ReviseName', { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) });
            const result = await response.json();
            if (!response.ok || !result.ok) throw new Error(result.error || `HTTP ${response.status}`);
            if (requestSerial !== serial) throw new Error('Inputs changed while checking. Preview again.');
            report = result; el('ewRenameReport').disabled = false;
            el('ewRenameResult').textContent = JSON.stringify({ itemEntry: result.itemEntry, displayId: result.displayId,
                oldName: result.oldName, newName: result.newName, beforeWorldRowSha256: result.beforeWorldRowSha256,
                afterWorldRowSha256: result.afterWorldRowSha256, revisionToken: result.revisionToken,
                unchangedOtherColumns: result.columnsPreserved?.length, adminEngine: result.adminEngine, worldEngine: result.worldEngine,
                sameServer: result.sameServer, atomic: result.atomic, recoverableJournal: result.recoverableJournal,
                transactionScope: result.transactionScope, sqlSha256: result.sqlSha256, runtimeReloadRequired: result.runtimeReloadRequired }, null, 2);
            if (apply) {
                el('ewRenameOld').value = result.newName;
                el('ewRenameStatus').textContent = `Renamed item ${result.itemEntry}. Normal item-template reload and a fresh client query remain required.`;
            } else {
                reviewed = result;
                el('ewRenameStatus').textContent = 'Name-only preview ready. Full world row, Forge metadata and export are bound to this token. No rename performed.' +
                    (result.recoverableJournal ? ' This server uses a recovery journal and guarded compensation; the two table writes are not atomic.' : '');
            }
        } catch (error) { el('ewRenameStatus').textContent = error.message || String(error); }
        finally {
            busy = false; for (const id of [...fields, 'ewRenamePreview']) el(id).disabled = false;
            el('ewRenameApply').disabled = !reviewed;
        }
    }
    el('ewRenamePreview').addEventListener('click', () => run(false));
    el('ewRenameApply').addEventListener('click', () => run(true));
    el('ewRenameReport').addEventListener('click', () => {
        if (!report) return;
        const url = URL.createObjectURL(new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' }));
        const a = document.createElement('a'); a.href = url; a.download = `weapon-name-${report.itemEntry}-${report.revisionToken}.json`;
        a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
    });
}

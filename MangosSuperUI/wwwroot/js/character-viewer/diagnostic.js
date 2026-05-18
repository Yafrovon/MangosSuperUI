// Character Viewer — Diagnostic panel.
//
// All interactive debug UI lives here. Boot code in index.js just calls
// `mountDiagnosticPanel({ canvasEl, character, viewer, ... })` and this
// module appends a floating overlay to the canvas's parent.
//
// === Why this is its own module ===
// The diagnostic surface area grew faster than the rest of the viewer
// during the rules-vs-truth diagnosis work. Keeping it separate means
// (a) index.js stays a legible bootstrap, (b) the diagnostic can be
// stripped out by replacing this file with a no-op `export function
// mountDiagnosticPanel() {}` when shipping a customer-facing build, and
// (c) re-organizing the UI is a one-file change.
//
// === Panel layout ===
// Collapsible sections, in order:
//   1. Snapshot           — capture PNG + state bundle; localStorage list
//   2. Category picker    — manual per-cat variant selection (geometry)
//   3. Item dressing test — equip via real rules, diff vs hand-tuned state
//   4. Mesh textures      — per-mesh texture source (atlas / own / debug / upload)
//   5. Atlas slot tester  — force-paint a BLP into a specific slot
//   6. Region editor      — live-tune the (x,y,w,h) of each atlas region
//   7. Face / head        — load a specific face BLP onto the head mesh
//   8. Legacy harness     — region paint, isolation toggles, dressing presets
//                            (rolled in from the old ?debug=regions panel)
//
// === Snapshot bundle ===
// What gets captured:
//   - PNG of the canvas at the current frame
//   - state.categories[cat] = { visible: number[], hidden: number[] }
//   - state.meshTextureSources[geosetId] = 'atlas' | 'own' | 'debug' | 'custom:<dataurl>'
//   - state.regionOverrides[regionKey] = { x, y, w, h } when changed
//   - state.equipContext = { displayId, itemId } | null
//   - state.modelInputs = { glbUrl, datasetGlbUrl }
//
// Snapshots persist in localStorage so they survive page reloads. The PNG
// is included in localStorage when quota allows; if it doesn't, the bundle
// is stored without the PNG (the PNG download still happens regardless).
//
// === Honest caveats ===
//   - All "current state" reads are point-in-time. If you mutate visibility
//     through console hacks while the panel is open, the picker dropdowns
//     will be out of sync until you click `refresh` or reload.
//   - Custom-uploaded textures live only in this session (the data URLs in
//     localStorage are large; we don't paint them onto new page loads).
//   - The region editor mutates the in-memory REGIONS object on the
//     compositor side; changes don't persist to region-rects.js. Use the
//     "dump regions JS" button to copy the edited values back into the file.

import * as THREE from 'three';
import { REGIONS, SLOT_TO_REGION, REGION_DEBUG_COLORS } from './region-rects.js';

const SNAPSHOT_STORAGE_KEY = 'cv-diagnostic-snapshots';
const SNAPSHOT_SCHEMA_VERSION = 2;
const ATLAS_SIZE = 256;

/**
 * Mount the diagnostic panel as an absolutely-positioned overlay on the
 * canvas's parent. Returns the panel element (caller doesn't usually need
 * it; it's there for tests).
 *
 * @param {object} opts
 * @param {HTMLCanvasElement} opts.canvasEl
 * @param {object} opts.character  loadCharacterGlb() result
 * @param {object} opts.viewer     createViewer() result
 * @param {object} opts.compositor exported compositor.js
 * @param {object} opts.dresser    exported dresser.js
 * @param {object} opts.equip      exported equip.js
 * @param {string|null} [opts.initialFocus]
 *        Section key to auto-expand on load. Currently only 'regions' is
 *        wired up (legacy ?debug=regions URL).
 */
export function mountDiagnosticPanel({
    canvasEl, character, viewer, compositor, dresser, equip,
    initialFocus = null,
}) {
    const parent = canvasEl.parentElement;
    if (parent && getComputedStyle(parent).position === 'static') {
        parent.style.position = 'relative';
    }

    const panel = document.createElement('div');
    panel.id = 'cv-diagnostic-panel';
    panel.style.cssText = `
        position: absolute;
        top: 8px;
        right: 8px;
        z-index: 10000;
        background: rgba(18, 18, 22, 0.94);
        border: 1px solid #444;
        border-radius: 6px;
        padding: 8px;
        font-family: ui-monospace, SFMono-Regular, Consolas, monospace;
        font-size: 12px;
        color: #ddd;
        display: flex;
        flex-direction: column;
        gap: 4px;
        min-width: 280px;
        max-width: 360px;
        max-height: calc(100% - 16px);
        overflow-y: auto;
        box-shadow: 0 2px 12px rgba(0, 0, 0, 0.5);
    `;

    // Top header — entire panel collapse toggle.
    let panelExpanded = true;
    const topHeader = document.createElement('div');
    topHeader.style.cssText = `
        display: flex; justify-content: space-between; align-items: center;
        cursor: pointer; user-select: none; font-weight: bold; color: #88c;
        padding: 2px 0;
    `;
    const topLabel = document.createElement('span');
    topLabel.textContent = '🔬 Diagnostics';
    const topToggle = document.createElement('span');
    topToggle.textContent = '▾';
    topToggle.style.cssText = 'font-size: 10px; color: #888;';
    topHeader.appendChild(topLabel);
    topHeader.appendChild(topToggle);
    panel.appendChild(topHeader);

    const body = document.createElement('div');
    body.style.cssText = 'display: flex; flex-direction: column; gap: 4px;';
    panel.appendChild(body);

    topHeader.addEventListener('click', () => {
        panelExpanded = !panelExpanded;
        body.style.display = panelExpanded ? 'flex' : 'none';
        topToggle.textContent = panelExpanded ? '▾' : '▸';
    });

    // ────────────────────────────────────────────────────────────────
    // State mirror — lives for the lifetime of this panel.
    // ────────────────────────────────────────────────────────────────
    //
    // We track the user's *intended* per-mesh texture source and region
    // overrides separately from the THREE.Material state because the
    // material is mutated by the compositor every time someone clicks
    // a region-paint button. Without a separate mirror we'd lose the
    // user's choices on the next paint.
    //
    // meshTextureSources is keyed by geosetId (number). Values:
    //   'atlas'  — the shared body atlas (compositor.paintBodyAtlas wins)
    //   'own'    — restore the load-time material.map (userData._originalMap)
    //   'debug'  — paint a solid debug color so this mesh is identifiable
    //   'custom' — user-uploaded PNG; image stored in meshCustomImages
    const panelState = {
        meshTextureSources: new Map(),  // geosetId → string mode tag
        meshCustomImages: new Map(),    // geosetId → { dataUrl, texture }
        regionOverrides: new Map(),     // regionKey → { x, y, w, h }
        atlasSlotOverrides: new Map(),  // slot → { url, image }
        lastEquipContext: null,
    };

    // Compositor needs to see region overrides. We replace fields on the
    // imported REGIONS object in place so subsequent compositor calls
    // pick up the new values. Risky if anyone else holds a reference,
    // but in practice region-rects.js is the only owner.
    function applyRegionOverridesToCompositor() {
        for (const [key, rect] of panelState.regionOverrides) {
            if (REGIONS[key]) {
                Object.assign(REGIONS[key], rect);
            }
        }
    }

    // ────────────────────────────────────────────────────────────────
    // Reusable section / widget helpers.
    // ────────────────────────────────────────────────────────────────
    function section(title, opts = {}) {
        const wrap = document.createElement('div');
        wrap.style.cssText = `
            display: flex; flex-direction: column; gap: 4px;
            border: 1px solid #2a2a2a; border-radius: 4px;
            padding: 4px 6px;
        `;
        const head = document.createElement('div');
        head.style.cssText = `
            display: flex; justify-content: space-between; align-items: center;
            cursor: pointer; user-select: none;
            font-size: 10px; color: #888;
            text-transform: uppercase; letter-spacing: 0.5px;
        `;
        const label = document.createElement('span');
        label.textContent = title;
        const toggle = document.createElement('span');
        toggle.textContent = opts.expanded ? '▾' : '▸';
        head.appendChild(label);
        head.appendChild(toggle);
        wrap.appendChild(head);

        const inner = document.createElement('div');
        inner.style.cssText = 'display: flex; flex-direction: column; gap: 4px;';
        inner.style.display = opts.expanded ? 'flex' : 'none';
        wrap.appendChild(inner);

        head.addEventListener('click', () => {
            const open = inner.style.display === 'none';
            inner.style.display = open ? 'flex' : 'none';
            toggle.textContent = open ? '▾' : '▸';
        });

        body.appendChild(wrap);
        return inner;
    }

    function btn(parent, label, onClick, opts = {}) {
        const b = document.createElement('button');
        b.textContent = label;
        b.style.cssText = `
            background: ${opts.bg || '#2a2a2a'};
            border: 1px solid #555;
            border-radius: 4px;
            color: #ddd;
            padding: 5px 8px;
            font-family: inherit;
            font-size: 12px;
            cursor: pointer;
            text-align: left;
        `;
        b.addEventListener('mouseover', () => { b.style.background = opts.bgHover || '#3a3a4a'; });
        b.addEventListener('mouseout', () => { b.style.background = opts.bg || '#2a2a2a'; });
        b.addEventListener('click', onClick);
        parent.appendChild(b);
        return b;
    }

    function info(parent, text, style = '') {
        const d = document.createElement('div');
        d.textContent = text;
        d.style.cssText = `font-size: 11px; color: #888; ${style}`;
        parent.appendChild(d);
        return d;
    }

    function textInput(placeholder) {
        const i = document.createElement('input');
        i.type = 'text';
        i.placeholder = placeholder;
        i.style.cssText = `
            background: #1a1a1a;
            border: 1px solid #444;
            border-radius: 3px;
            color: #ddd;
            padding: 4px 6px;
            font-family: inherit;
            font-size: 12px;
            min-width: 0;
        `;
        return i;
    }

    // (numberInput is module-scope below — it has no closure deps and
    // is also used by the module-level buildRegionEditorRow.)

    // ════════════════════════════════════════════════════════════════
    // SECTION 1 — Snapshot capture / restore
    // ════════════════════════════════════════════════════════════════
    const snapSection = section('1. snapshot (PNG + state)', { expanded: true });

    const lastSnapshotInfo = info(snapSection, 'no snapshot yet', 'font-style: italic;');

    btn(snapSection, '📸 Snapshot (PNG + JSON)', async () => {
        const label = prompt('Snapshot label:', `snapshot-${Date.now()}`);
        if (label === null) return;
        const safeLabel = label.replace(/[^a-zA-Z0-9_-]/g, '_') || `snapshot-${Date.now()}`;

        const png = capturePng(canvasEl, viewer);
        const state = captureState(character, panelState);
        const bundle = {
            schemaVersion: SNAPSHOT_SCHEMA_VERSION,
            label,
            capturedAt: new Date().toISOString(),
            url: window.location.href,
            userAgent: navigator.userAgent,
            state,
        };

        downloadDataUrl(png, `${safeLabel}.png`);
        downloadText(JSON.stringify(bundle, null, 2), `${safeLabel}.json`, 'application/json');
        saveSnapshotToStorage(bundle, png);

        lastSnapshotInfo.textContent = `saved: ${label}`;
        lastSnapshotInfo.style.fontStyle = 'normal';
        lastSnapshotInfo.style.color = '#9c9';
        refreshSnapshotList();
        console.log('[diagnostic] snapshot saved:', bundle);
    }, { bg: '#2a3a2a', bgHover: '#3a4a3a' });

    btn(snapSection, '🔄 Dump current state (console + clipboard)', async () => {
        const state = captureState(character, panelState);
        const json = JSON.stringify(state, null, 2);
        console.group('[diagnostic] current state');
        console.log(state);
        console.log('JSON:\n' + json);
        console.groupEnd();
        try {
            await navigator.clipboard.writeText(json);
            console.log('[diagnostic] copied to clipboard');
        } catch (err) {
            console.warn('[diagnostic] clipboard copy failed:', err);
        }
    });

    const snapshotListWrap = document.createElement('div');
    snapshotListWrap.style.cssText = 'display: flex; flex-direction: column; gap: 2px; margin-top: 4px;';
    snapSection.appendChild(snapshotListWrap);

    function refreshSnapshotList() {
        snapshotListWrap.innerHTML = '';
        const list = loadSnapshotListFromStorage();
        if (list.length === 0) {
            const d = document.createElement('div');
            d.textContent = '(no saved snapshots)';
            d.style.cssText = 'font-size: 11px; color: #666; font-style: italic;';
            snapshotListWrap.appendChild(d);
            return;
        }
        for (const entry of list) {
            const row = document.createElement('div');
            row.style.cssText = 'display: flex; gap: 2px; align-items: center;';

            const loadBtn = document.createElement('button');
            loadBtn.textContent = `↺ ${entry.label}`;
            loadBtn.title = `Saved ${entry.capturedAt}`;
            loadBtn.style.cssText = `
                background: #2a2a3a; border: 1px solid #555;
                border-radius: 3px; color: #ddd; padding: 3px 6px;
                font-family: inherit; font-size: 11px; cursor: pointer;
                flex: 1; text-align: left;
                overflow: hidden; text-overflow: ellipsis; white-space: nowrap;
            `;
            loadBtn.addEventListener('click', () => {
                applyState(character, entry.state, panelState, compositor);
                lastSnapshotInfo.textContent = `loaded: ${entry.label}`;
                lastSnapshotInfo.style.fontStyle = 'normal';
                lastSnapshotInfo.style.color = '#cc9';
                refreshCategoryPicker();
                refreshMeshTextureList();
                refreshRegionEditor();
                console.log('[diagnostic] loaded snapshot:', entry);
            });

            const delBtn = document.createElement('button');
            delBtn.textContent = '✕';
            delBtn.title = 'Delete snapshot';
            delBtn.style.cssText = `
                background: #3a1a1a; border: 1px solid #555;
                border-radius: 3px; color: #c88; padding: 3px 5px;
                font-family: inherit; font-size: 10px; cursor: pointer;
            `;
            delBtn.addEventListener('click', () => {
                if (!confirm(`Delete snapshot "${entry.label}"?`)) return;
                deleteSnapshotFromStorage(entry.id);
                refreshSnapshotList();
            });

            row.appendChild(loadBtn);
            row.appendChild(delBtn);
            snapshotListWrap.appendChild(row);
        }
    }

    btn(snapSection, '📦 Export all snapshots (clipboard)', async () => {
        const list = loadSnapshotListFromStorage();
        const json = JSON.stringify(list, null, 2);
        try {
            await navigator.clipboard.writeText(json);
            console.log(`[diagnostic] exported ${list.length} snapshots`);
        } catch {
            console.log(json);
        }
    });

    btn(snapSection, '📂 Import snapshots (paste JSON)', () => {
        const text = prompt('Paste exported JSON:');
        if (!text) return;
        try {
            const list = JSON.parse(text);
            if (!Array.isArray(list)) throw new Error('not an array');
            for (const entry of list) {
                if (entry && entry.state) addSnapshotToStorage(entry);
            }
            refreshSnapshotList();
            console.log(`[diagnostic] imported ${list.length} snapshots`);
        } catch (err) {
            alert(`Import failed: ${err.message}`);
        }
    });

    refreshSnapshotList();

    // ════════════════════════════════════════════════════════════════
    // SECTION 2 — Manual category picker (geometry)
    // ════════════════════════════════════════════════════════════════
    const catSection = section('2. category picker (geometry)', { expanded: true });
    info(catSection,
        'Per-cat dropdowns: default / hidden / each variant.',
        'margin-bottom: 4px;');

    const byCategory = indexByCategory(character);
    const categories = [...byCategory.keys()].sort((a, b) => a - b);

    // pickerState: cat → 'default' | 'hidden' | <geosetId number>
    const pickerState = new Map();
    for (const cat of categories) pickerState.set(cat, 'default');

    function applyPickerSelection(cat) {
        const sel = pickerState.get(cat);
        const meshes = byCategory.get(cat);
        if (!meshes) return;
        if (sel === 'default') {
            for (const m of meshes) m.visible = m.userData?._originalVisible ?? true;
        } else if (sel === 'hidden') {
            for (const m of meshes) m.visible = false;
        } else {
            for (const m of meshes) {
                m.visible = (m.userData?.geosetId === sel);
            }
        }
    }

    const pickerRows = new Map();
    for (const cat of categories) {
        const row = buildPickerRow(cat, byCategory, pickerState, applyPickerSelection, catSection);
        pickerRows.set(cat, row);
    }

    function refreshCategoryPicker() {
        for (const cat of categories) {
            const row = pickerRows.get(cat);
            if (!row) continue;
            const meshes = byCategory.get(cat);
            const visible = meshes.filter(m => m.visible);
            let sel;
            if (visible.length === 0) sel = 'hidden';
            else if (visible.length === meshes.length) sel = 'default';
            else if (visible.length === 1) sel = String(visible[0].userData?.geosetId);
            else sel = 'default';  // mixed — best-effort
            row._select.value = sel;
            pickerState.set(cat, sel === 'default' || sel === 'hidden' ? sel : Number(sel));
        }
    }

    const pickerActions = document.createElement('div');
    pickerActions.style.cssText = 'display: flex; gap: 4px; margin-top: 4px;';
    btn(pickerActions, 'all → default', () => {
        for (const cat of categories) {
            pickerState.set(cat, 'default');
            applyPickerSelection(cat);
        }
        refreshCategoryPicker();
    }, { bg: '#2a2a2a' });
    btn(pickerActions, 'all → hidden', () => {
        for (const cat of categories) {
            pickerState.set(cat, 'hidden');
            applyPickerSelection(cat);
        }
        refreshCategoryPicker();
    }, { bg: '#2a2a2a' });
    btn(pickerActions, 'refresh', () => refreshCategoryPicker(),
        { bg: '#2a2a2a' });
    catSection.appendChild(pickerActions);

    // ════════════════════════════════════════════════════════════════
    // SECTION 3 — Item dressing test
    // ════════════════════════════════════════════════════════════════
    const dressSection = section('3. item dressing test', { expanded: false });

    const dressingRow = document.createElement('div');
    dressingRow.style.cssText = 'display: flex; gap: 4px;';
    const displayIdInput = textInput('displayId');
    displayIdInput.style.flex = '1';
    const itemIdInput = textInput('itemId (opt)');
    itemIdInput.style.flex = '1';
    dressingRow.appendChild(displayIdInput);
    dressingRow.appendChild(itemIdInput);
    dressSection.appendChild(dressingRow);

    btn(dressSection, 'Equip via real rules', async () => {
        const dispId = Number(displayIdInput.value);
        const itemId = Number(itemIdInput.value) || 0;
        if (!dispId) { alert('Enter a displayId first.'); return; }
        console.log(`[diagnostic] equipDisplay(displayId=${dispId}, itemId=${itemId})`);
        const result = await equip.equipDisplay(character, dispId, itemId);
        console.log('[diagnostic] equip result:', result);
        panelState.lastEquipContext = { displayId: dispId, itemId };
        refreshCategoryPicker();
        refreshMeshTextureList();
        const state = captureState(character, panelState);
        console.log('[diagnostic] post-equip state:', state);
    }, { bg: '#2a3a3a', bgHover: '#3a4a4a' });

    btn(dressSection, 'Fetch /Items/ItemDressing payload', async () => {
        const dispId = Number(displayIdInput.value);
        const itemId = Number(itemIdInput.value) || 0;
        if (!dispId) { alert('Enter a displayId first.'); return; }
        const dressing = await equip.fetchDressing(dispId, itemId);
        console.log('[diagnostic] /Items/ItemDressing →', dressing);
        if (dressing) {
            try {
                await navigator.clipboard.writeText(JSON.stringify(dressing, null, 2));
                console.log('[diagnostic] copied to clipboard');
            } catch { }
        }
    });

    btn(dressSection, 'Unequip (back to default)', async () => {
        await equip.unequipAll(character);
        panelState.lastEquipContext = null;
        refreshCategoryPicker();
        refreshMeshTextureList();
    });

    // ════════════════════════════════════════════════════════════════
    // SECTION 4 — Per-mesh texture inspector
    // ════════════════════════════════════════════════════════════════
    const meshTexSection = section('4. mesh textures', { expanded: false });
    info(meshTexSection,
        'Pick a texture source per visible mesh. Use this to find which meshes paint into the body atlas vs need their own texture.',
        'margin-bottom: 4px;');

    const meshTexListWrap = document.createElement('div');
    meshTexListWrap.style.cssText = 'display: flex; flex-direction: column; gap: 2px;';
    meshTexSection.appendChild(meshTexListWrap);

    function refreshMeshTextureList() {
        meshTexListWrap.innerHTML = '';
        // Show every mesh, not just visible — you sometimes want to set a
        // texture on a hidden mesh and then toggle visibility on. Sort by
        // category then variant for predictable ordering.
        const sorted = [...character.geosetList].sort((a, b) => {
            const ca = a.userData?.geosetCategory ?? 0;
            const cb = b.userData?.geosetCategory ?? 0;
            if (ca !== cb) return ca - cb;
            return (a.userData?.geosetId ?? 0) - (b.userData?.geosetId ?? 0);
        });
        for (const mesh of sorted) {
            const row = buildMeshTextureRow(mesh, panelState, character);
            meshTexListWrap.appendChild(row);
        }
    }

    btn(meshTexSection, 'reset all to atlas', () => {
        for (const m of character.geosetList) {
            const id = m.userData?.geosetId;
            if (typeof id !== 'number') continue;
            panelState.meshTextureSources.set(id, 'atlas');
            applyMeshTextureSource(m, 'atlas', panelState, character);
        }
        refreshMeshTextureList();
    }, { bg: '#2a2a2a' });

    refreshMeshTextureList();

    // ════════════════════════════════════════════════════════════════
    // SECTION 5 — Atlas slot tester
    // ════════════════════════════════════════════════════════════════
    const slotSection = section('5. atlas slot tester', { expanded: false });
    info(slotSection,
        'Force-paint an image into a specific atlas slot. Useful for verifying a BLP lands where its slot says it should.',
        'margin-bottom: 4px;');

    const slotRow = document.createElement('div');
    slotRow.style.cssText = 'display: flex; gap: 4px; align-items: center;';
    const slotSelect = document.createElement('select');
    slotSelect.style.cssText = `
        background: #1a1a1a; border: 1px solid #444; border-radius: 3px;
        color: #ddd; padding: 2px 4px; font-family: inherit; font-size: 11px;
    `;
    for (const [slot, region] of Object.entries(SLOT_TO_REGION)) {
        const o = document.createElement('option');
        o.value = slot;
        o.textContent = `slot ${slot} → ${region}`;
        slotSelect.appendChild(o);
    }
    const slotUrlInput = textInput('PNG/BLP URL to paint');
    slotUrlInput.style.flex = '1';
    slotRow.appendChild(slotSelect);
    slotRow.appendChild(slotUrlInput);
    slotSection.appendChild(slotRow);

    const slotFileRow = document.createElement('div');
    slotFileRow.style.cssText = 'display: flex; gap: 4px; align-items: center;';
    const slotFileInput = document.createElement('input');
    slotFileInput.type = 'file';
    slotFileInput.accept = 'image/png,image/jpeg';
    slotFileInput.style.cssText = 'font-size: 11px; color: #ccc; flex: 1;';
    slotFileRow.appendChild(slotFileInput);
    slotSection.appendChild(slotFileRow);

    btn(slotSection, 'paint into slot', async () => {
        const slot = Number(slotSelect.value);
        let image = null;
        if (slotFileInput.files && slotFileInput.files[0]) {
            image = await createImageBitmap(slotFileInput.files[0]);
        } else if (slotUrlInput.value.trim()) {
            const url = slotUrlInput.value.trim();
            const res = await fetch(url);
            if (!res.ok) { alert(`fetch failed: HTTP ${res.status}`); return; }
            image = await createImageBitmap(await res.blob());
        } else {
            alert('Provide a URL or pick a file.');
            return;
        }
        panelState.atlasSlotOverrides.set(slot, { image });
        repaintAtlasFromOverrides(character, compositor, panelState);
    }, { bg: '#2a3a3a' });

    btn(slotSection, 'clear slot overrides', () => {
        panelState.atlasSlotOverrides.clear();
        repaintAtlasFromOverrides(character, compositor, panelState);
    });

    // ════════════════════════════════════════════════════════════════
    // SECTION 6 — Region rectangle editor
    // ════════════════════════════════════════════════════════════════
    const regionSection = section('6. region editor', { expanded: false });
    info(regionSection,
        'Live-tune REGIONS rectangles. Repaint with "show debug regions" to see the new layout.',
        'margin-bottom: 4px;');

    const regionRows = new Map();
    for (const key of Object.keys(REGIONS)) {
        const row = buildRegionEditorRow(key, panelState, () =>
            applyRegionOverridesToCompositor());
        regionSection.appendChild(row);
        regionRows.set(key, row);
    }

    function refreshRegionEditor() {
        for (const [key, row] of regionRows) {
            const r = panelState.regionOverrides.get(key) ?? REGIONS[key];
            if (!r) continue;
            row._inputs.x.value = r.x;
            row._inputs.y.value = r.y;
            row._inputs.w.value = r.w;
            row._inputs.h.value = r.h;
        }
    }

    btn(regionSection, 'show debug regions', () => {
        applyRegionOverridesToCompositor();
        compositor.paintDebugRegions(character);
    });

    btn(regionSection, 'dump regions JS (clipboard)', async () => {
        const lines = ['export const REGIONS = {'];
        for (const key of Object.keys(REGIONS)) {
            const r = panelState.regionOverrides.get(key) ?? REGIONS[key];
            lines.push(`    ${key}: { x: ${r.x}, y: ${r.y}, w: ${r.w}, h: ${r.h} },`);
        }
        lines.push('};');
        const text = lines.join('\n');
        try {
            await navigator.clipboard.writeText(text);
            console.log('[diagnostic] regions JS copied to clipboard');
            console.log(text);
        } catch {
            console.log(text);
        }
    });

    btn(regionSection, 'reset all regions', () => {
        // Note: this resets the panel state but does NOT undo the in-place
        // mutations we did to REGIONS. To do that properly we'd need to
        // cache the originals. For now, "reload page" is the clean-slate.
        panelState.regionOverrides.clear();
        refreshRegionEditor();
        info(regionSection,
            '⚠ reload page to fully restore default REGIONS values',
            'color: #c93; margin-top: 2px;');
    });

    // ════════════════════════════════════════════════════════════════
    // SECTION 7 — Face / head texture override
    // ════════════════════════════════════════════════════════════════
    const faceSection = section('7. face / head texture', { expanded: false });
    info(faceSection,
        'Load a face BLP (CharacterFaceXX_YY.png from the skin extractor) and apply to a chosen mesh. Useful for debugging the "face is purple body-skin" problem.',
        'margin-bottom: 4px;');

    const faceMeshSelect = document.createElement('select');
    faceMeshSelect.style.cssText = `
        background: #1a1a1a; border: 1px solid #444; border-radius: 3px;
        color: #ddd; padding: 2px 4px; font-family: inherit; font-size: 11px;
        width: 100%;
    `;
    // Pre-populate with all geosets in cats 0, 1, 2, 3, 17 — the plausible
    // face/head/sideburn candidates. Other categories are picked at runtime
    // via the per-mesh inspector above.
    const faceCandidateCats = new Set([0, 1, 2, 3, 17]);
    for (const m of character.geosetList) {
        const cat = m.userData?.geosetCategory;
        const id = m.userData?.geosetId;
        if (!faceCandidateCats.has(cat)) continue;
        const o = document.createElement('option');
        o.value = String(id);
        o.textContent = `${id} (cat ${cat}) — ${m.name}`;
        faceMeshSelect.appendChild(o);
    }
    faceSection.appendChild(faceMeshSelect);

    const faceUrlInput = textInput('face PNG URL (e.g. /character_textures/face/HumanMaleFace00_00.png)');
    faceSection.appendChild(faceUrlInput);

    const faceFileInput = document.createElement('input');
    faceFileInput.type = 'file';
    faceFileInput.accept = 'image/png,image/jpeg';
    faceFileInput.style.cssText = 'font-size: 11px; color: #ccc;';
    faceSection.appendChild(faceFileInput);

    btn(faceSection, 'apply face texture', async () => {
        const id = Number(faceMeshSelect.value);
        const mesh = character.geosets[id];
        if (!mesh) { alert('No mesh for that geosetId'); return; }
        let bitmap = null;
        if (faceFileInput.files && faceFileInput.files[0]) {
            bitmap = await createImageBitmap(faceFileInput.files[0]);
        } else if (faceUrlInput.value.trim()) {
            const res = await fetch(faceUrlInput.value.trim());
            if (!res.ok) { alert(`fetch failed: HTTP ${res.status}`); return; }
            bitmap = await createImageBitmap(await res.blob());
        } else {
            alert('Provide a URL or file.');
            return;
        }
        const tex = bitmapToTexture(bitmap);
        cloneMaterialAndSetMap(mesh, tex);
        // Mark in the inspector that this mesh is custom now so it doesn't
        // get clobbered by the next atlas repaint.
        panelState.meshTextureSources.set(id, 'custom');
        panelState.meshCustomImages.set(id, { texture: tex });
        refreshMeshTextureList();
        console.log(`[diagnostic] applied face texture to mesh ${id}`);
    }, { bg: '#2a3a3a' });

    // ════════════════════════════════════════════════════════════════
    // SECTION 8 — Legacy harness (region paint, isolation, presets)
    // ════════════════════════════════════════════════════════════════
    // Rolled in from the old `?debug=regions` panel. Lives in a collapsed
    // section because most of the time you don't need it; the new
    // category picker covers the same ground better. Kept around because
    // (a) region-paint debug colors are still useful, (b) the dressing
    // presets are a quick way to fake an equip-state without going
    // through DBC parsing.
    const legacySection = section('8. legacy harness', {
        expanded: initialFocus === 'regions',
    });

    info(legacySection, 'region paint:', 'color: #888;');
    const regionPaintRow = document.createElement('div');
    regionPaintRow.style.cssText = 'display: flex; flex-wrap: wrap; gap: 3px;';
    for (const key of Object.keys(REGIONS)) {
        btn(regionPaintRow, key, () => compositor.paintSingleRegion(character, key),
            { bg: '#2a2a2a' });
    }
    btn(regionPaintRow, 'all', () => compositor.paintDebugRegions(character),
        { bg: '#2a2a2a' });
    legacySection.appendChild(regionPaintRow);

    info(legacySection, 'visibility toggles:', 'color: #888; margin-top: 4px;');
    let bareBodyOn = false;
    btn(legacySection, '— bare body only —', () => {
        bareBodyOn = !bareBodyOn;
        for (const m of character.geosetList) {
            const cat = m.userData?.geosetCategory;
            if (typeof cat !== 'number') continue;
            if (bareBodyOn) {
                m.visible = (cat < 1);
            } else {
                m.visible = m.userData?._originalVisible ?? true;
            }
        }
        refreshCategoryPicker();
    }, { bg: '#2a3a2a' });

    btn(legacySection, 'restore visibility', () => {
        for (const m of character.geosetList) {
            const v = m.userData?._originalVisible;
            if (typeof v === 'boolean') m.visible = v;
        }
        bareBodyOn = false;
        refreshCategoryPicker();
    });

    info(legacySection, 'dressing presets (hand-coded, NOT verified):',
        'color: #c93; margin-top: 4px;');
    info(legacySection,
        'These were written before we knew the real cat numbering. Treat as guesses.',
        'color: #888; font-style: italic;');

    const presets = [
        ['naked (default)', []],
        ['chest only (cloth)', [{ inventoryType: 5, geosetGroup: [1, 0, 0, 0, 0] }]],
        ['robe (caster)', [{ inventoryType: 5, geosetGroup: [1, 0, 1, 0, 0] }]],
        ['pants only', [{ inventoryType: 7, geosetGroup: [1, 0, 0, 0, 0] }]],
        ['boots only', [{ inventoryType: 8, geosetGroup: [2, 0, 0, 0, 0] }]],
        ['gloves only', [{ inventoryType: 10, geosetGroup: [2, 0, 0, 0, 0] }]],
    ];
    for (const [label, items] of presets) {
        btn(legacySection, label, () => {
            dresser.applyItemFilters(character, items);
            refreshCategoryPicker();
        }, { bg: '#2a2a3a' });
    }

    btn(legacySection, 'reset texture (restore _originalMap)', () => {
        let n = 0;
        for (const m of character.geosetList) {
            const orig = m.userData?._originalMap;
            if (!orig) continue;
            const mats = Array.isArray(m.material) ? m.material : [m.material];
            for (const mat of mats) {
                if (mat && 'map' in mat) {
                    mat.map = orig;
                    mat.needsUpdate = true;
                    n++;
                }
            }
        }
        console.log(`[diagnostic] restored ${n} original textures`);
    });

    // ────────────────────────────────────────────────────────────────
    // Mount.
    // ────────────────────────────────────────────────────────────────
    parent.appendChild(panel);

    // For the test suite + console poking.
    return panel;
}

// ════════════════════════════════════════════════════════════════════
// Building blocks (per-row factories).
// ════════════════════════════════════════════════════════════════════

function buildPickerRow(cat, byCategory, pickerState, applyFn, parent) {
    const meshes = byCategory.get(cat);
    const row = document.createElement('div');
    row.style.cssText = 'display: flex; gap: 4px; align-items: center; font-size: 11px;';

    const label = document.createElement('span');
    label.textContent = `cat ${cat}:`;
    label.style.cssText = 'min-width: 48px; color: #aaa;';
    row.appendChild(label);

    const select = document.createElement('select');
    select.style.cssText = `
        background: #1a1a1a; border: 1px solid #444; border-radius: 3px;
        color: #ddd; padding: 2px 4px; font-family: inherit; font-size: 11px;
        flex: 1;
    `;
    const optDefault = document.createElement('option');
    optDefault.value = 'default';
    optDefault.textContent = 'default (load state)';
    select.appendChild(optDefault);
    const optHidden = document.createElement('option');
    optHidden.value = 'hidden';
    optHidden.textContent = 'hidden (all off)';
    select.appendChild(optHidden);
    const uniqueIds = [...new Set(meshes.map(m => m.userData?.geosetId))].sort((a, b) => a - b);
    for (const id of uniqueIds) {
        const sample = meshes.find(m => m.userData?.geosetId === id);
        const variant = sample?.userData?.geosetVariant ?? '?';
        const opt = document.createElement('option');
        opt.value = String(id);
        opt.textContent = `${id} (v${variant})`;
        select.appendChild(opt);
    }
    select.addEventListener('change', () => {
        const v = select.value;
        pickerState.set(cat, v === 'default' || v === 'hidden' ? v : Number(v));
        applyFn(cat);
    });
    row._select = select;
    row.appendChild(select);

    const nextBtn = document.createElement('button');
    nextBtn.textContent = '▶';
    nextBtn.title = 'Next option';
    nextBtn.style.cssText = `
        background: #2a2a2a; border: 1px solid #444; border-radius: 3px;
        color: #ddd; padding: 2px 6px; font-family: inherit; font-size: 10px;
        cursor: pointer;
    `;
    nextBtn.addEventListener('click', () => {
        select.selectedIndex = (select.selectedIndex + 1) % select.options.length;
        select.dispatchEvent(new Event('change'));
    });
    row.appendChild(nextBtn);

    parent.appendChild(row);
    return row;
}

function buildMeshTextureRow(mesh, panelState, character) {
    const id = mesh.userData?.geosetId;
    const cat = mesh.userData?.geosetCategory;
    const row = document.createElement('div');
    row.style.cssText = `
        display: flex; gap: 4px; align-items: center; font-size: 11px;
        ${mesh.visible ? '' : 'opacity: 0.45;'}
    `;

    const label = document.createElement('span');
    label.textContent = `${id} (c${cat})`;
    label.style.cssText = 'min-width: 64px; color: #aaa; font-size: 10px;';
    row.appendChild(label);

    const select = document.createElement('select');
    select.style.cssText = `
        background: #1a1a1a; border: 1px solid #444; border-radius: 3px;
        color: #ddd; padding: 2px 4px; font-family: inherit; font-size: 10px;
        flex: 1;
    `;
    for (const mode of ['atlas', 'own', 'debug', 'custom']) {
        const o = document.createElement('option');
        o.value = mode;
        o.textContent = mode;
        select.appendChild(o);
    }
    const currentMode = panelState.meshTextureSources.get(id) ?? 'atlas';
    select.value = currentMode;

    select.addEventListener('change', () => {
        const mode = select.value;
        if (mode === 'custom') {
            // Trigger file picker.
            const fi = document.createElement('input');
            fi.type = 'file';
            fi.accept = 'image/png,image/jpeg';
            fi.addEventListener('change', async () => {
                if (!fi.files || !fi.files[0]) {
                    select.value = currentMode;
                    return;
                }
                const bitmap = await createImageBitmap(fi.files[0]);
                const tex = bitmapToTexture(bitmap);
                panelState.meshCustomImages.set(id, { texture: tex });
                panelState.meshTextureSources.set(id, 'custom');
                applyMeshTextureSource(mesh, 'custom', panelState, character);
            });
            fi.click();
        } else {
            panelState.meshTextureSources.set(id, mode);
            applyMeshTextureSource(mesh, mode, panelState, character);
        }
    });
    row.appendChild(select);

    return row;
}

function buildRegionEditorRow(key, panelState, onChange) {
    const row = document.createElement('div');
    row.style.cssText = 'display: flex; gap: 3px; align-items: center; font-size: 11px;';

    const swatch = document.createElement('div');
    swatch.style.cssText = `
        width: 10px; height: 10px;
        background: ${REGION_DEBUG_COLORS[key] || '#888'};
        border: 1px solid #555;
    `;
    row.appendChild(swatch);

    const label = document.createElement('span');
    label.textContent = key;
    label.style.cssText = 'min-width: 64px; color: #aaa; font-size: 10px;';
    row.appendChild(label);

    const xIn = numberInput(REGIONS[key].x, { max: 256 });
    const yIn = numberInput(REGIONS[key].y, { max: 256 });
    const wIn = numberInput(REGIONS[key].w, { max: 256 });
    const hIn = numberInput(REGIONS[key].h, { max: 256 });
    row.appendChild(xIn); row.appendChild(yIn); row.appendChild(wIn); row.appendChild(hIn);

    function commit() {
        const rect = {
            x: Number(xIn.value), y: Number(yIn.value),
            w: Number(wIn.value), h: Number(hIn.value),
        };
        panelState.regionOverrides.set(key, rect);
        onChange();
    }
    for (const i of [xIn, yIn, wIn, hIn]) {
        i.addEventListener('change', commit);
    }
    row._inputs = { x: xIn, y: yIn, w: wIn, h: hIn };
    return row;
}

// ════════════════════════════════════════════════════════════════════
// State capture / restore.
// ════════════════════════════════════════════════════════════════════

function captureState(character, panelState) {
    const byCat = indexByCategory(character);
    const categories = {};
    for (const [cat, meshes] of byCat) {
        const visible = [];
        const hidden = [];
        for (const m of meshes) {
            const id = m.userData?.geosetId;
            if (typeof id !== 'number') continue;
            (m.visible ? visible : hidden).push(id);
        }
        categories[cat] = {
            visible: [...new Set(visible)].sort((a, b) => a - b),
            hidden: [...new Set(hidden)].sort((a, b) => a - b),
            allMeshes: meshes.length,
        };
    }

    const meshTextureSources = {};
    for (const [id, mode] of panelState.meshTextureSources) {
        meshTextureSources[id] = mode;
    }

    const regionOverrides = {};
    for (const [key, rect] of panelState.regionOverrides) {
        regionOverrides[key] = { ...rect };
    }

    return {
        categories,
        meshTextureSources,
        regionOverrides,
        equipContext: panelState.lastEquipContext ?? null,
        modelInputs: {
            glbUrl: character.gltf?.parser?.options?.path ?? null,
            datasetGlbUrl: document.getElementById('char-preview-canvas')?.dataset.glbUrl ?? null,
        },
    };
}

function applyState(character, state, panelState, compositor) {
    if (!state || !state.categories) {
        console.warn('[diagnostic] applyState: invalid state', state);
        return;
    }
    const byCat = indexByCategory(character);
    for (const [catStr, infoEntry] of Object.entries(state.categories)) {
        const cat = Number(catStr);
        const meshes = byCat.get(cat);
        if (!meshes) continue;
        const vis = new Set(infoEntry.visible || []);
        for (const m of meshes) {
            const id = m.userData?.geosetId;
            m.visible = vis.has(id);
        }
    }

    // Re-apply mesh texture sources where we can. 'custom' textures can't
    // be reconstructed from JSON alone (we don't store the data URL in
    // the state, only the mode tag), so 'custom' degrades to 'own'.
    if (state.meshTextureSources) {
        panelState.meshTextureSources.clear();
        for (const [idStr, mode] of Object.entries(state.meshTextureSources)) {
            const id = Number(idStr);
            const mesh = character.geosets[id];
            if (!mesh) continue;
            const effectiveMode = (mode === 'custom') ? 'own' : mode;
            panelState.meshTextureSources.set(id, effectiveMode);
            applyMeshTextureSource(mesh, effectiveMode, panelState, character);
        }
    }

    // Re-apply region overrides (mutates the REGIONS object in place).
    if (state.regionOverrides) {
        panelState.regionOverrides.clear();
        for (const [key, rect] of Object.entries(state.regionOverrides)) {
            panelState.regionOverrides.set(key, { ...rect });
            if (REGIONS[key]) Object.assign(REGIONS[key], rect);
        }
    }

    panelState.lastEquipContext = state.equipContext ?? null;
}

// ════════════════════════════════════════════════════════════════════
// Texture-source application.
// ════════════════════════════════════════════════════════════════════

/**
 * Set a mesh's texture according to the chosen mode tag.
 *
 *   'atlas'  — let compositor manage; we don't touch material.map here so
 *              the next paintBodyAtlas wins (the default behavior).
 *   'own'    — restore userData._originalMap, the texture loaded with the GLB.
 *   'debug'  — paint a unique debug color so the mesh is visually identifiable.
 *   'custom' — apply the texture stored in panelState.meshCustomImages.
 *
 * 'atlas' is a no-op here by design — the next compositor.paintBodyAtlas
 * or applyBodyTexture call will reach back and set this material's map.
 * If the panel is in a state where no further atlas paint is coming,
 * the mesh will keep whatever it has currently; that's intentional —
 * "atlas" is a declaration of intent, not a destructive override.
 */
function applyMeshTextureSource(mesh, mode, panelState, character) {
    if (!mesh) return;
    if (mode === 'atlas') {
        // Future atlas repaint will handle this. No-op now.
        return;
    }
    if (mode === 'own') {
        const orig = mesh.userData?._originalMap;
        if (!orig) return;
        cloneMaterialAndSetMap(mesh, orig);
        return;
    }
    if (mode === 'debug') {
        const id = mesh.userData?.geosetId ?? 0;
        const hue = (id * 47) % 360;
        const tex = makeSolidColorTexture(`hsl(${hue}, 80%, 55%)`);
        cloneMaterialAndSetMap(mesh, tex);
        return;
    }
    if (mode === 'custom') {
        const id = mesh.userData?.geosetId;
        const entry = panelState.meshCustomImages.get(id);
        if (!entry?.texture) return;
        cloneMaterialAndSetMap(mesh, entry.texture);
        return;
    }
}

/**
 * Set the map on a mesh's material — but clone the material first if
 * it's shared with any other mesh, so we don't accidentally change
 * texture on multiple meshes at once.
 *
 * SharpGLTF dedupes identical materials; THREE preserves that dedupe
 * during import. So a naive `mat.map = newMap` propagates to every
 * other mesh that shared the material. We detect that case and clone.
 */
function cloneMaterialAndSetMap(mesh, texture) {
    const mats = Array.isArray(mesh.material) ? mesh.material : [mesh.material];
    for (let i = 0; i < mats.length; i++) {
        let mat = mats[i];
        if (!mat || !('map' in mat)) continue;
        if (!mesh.userData._materialCloned) {
            mat = mat.clone();
            mats[i] = mat;
            // Replace the slot — if mesh.material was the bare array entry,
            // splice; otherwise reassign the singleton.
            if (Array.isArray(mesh.material)) mesh.material[i] = mat;
            else mesh.material = mat;
            mesh.userData._materialCloned = true;
        }
        mat.map = texture;
        mat.needsUpdate = true;
    }
}

function makeSolidColorTexture(cssColor) {
    const c = document.createElement('canvas');
    c.width = 4; c.height = 4;
    const ctx = c.getContext('2d');
    ctx.fillStyle = cssColor;
    ctx.fillRect(0, 0, 4, 4);
    const tex = new THREE.CanvasTexture(c);
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.flipY = false;
    return tex;
}

function bitmapToTexture(bitmap) {
    const c = document.createElement('canvas');
    c.width = bitmap.width;
    c.height = bitmap.height;
    const ctx = c.getContext('2d');
    ctx.drawImage(bitmap, 0, 0);
    const tex = new THREE.CanvasTexture(c);
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.flipY = false;
    return tex;
}

/**
 * Repaint the body atlas with any slot overrides the user set in the
 * "atlas slot tester" section. Walks the override map, builds a layers
 * array compatible with compositor.paintBodyAtlas, and calls it with the
 * cached base skin (if any).
 *
 * Falls back to a neutral skin-tone if no base skin texture is available
 * — same fallback compositor.paintBodyAtlas would use internally.
 */
function repaintAtlasFromOverrides(character, compositor, panelState) {
    const layers = [];
    for (const [slot, override] of panelState.atlasSlotOverrides) {
        if (override?.image) layers.push({ slot: Number(slot), image: override.image });
    }
    // Use any cached base skin we can find on a body mesh. The compositor
    // accepts null and falls back to a neutral fill.
    let baseSkin = null;
    for (const m of character.geosetList) {
        const orig = m.userData?._originalMap?.image;
        if (orig instanceof HTMLImageElement || orig instanceof ImageBitmap) {
            baseSkin = orig;
            break;
        }
    }
    compositor.paintBodyAtlas(character, baseSkin, layers);
}

// ════════════════════════════════════════════════════════════════════
// Canvas capture & download.
// ════════════════════════════════════════════════════════════════════

function capturePng(canvasEl, viewer) {
    // Force a fresh render so the drawing buffer is populated at this exact
    // tick. Without this, toDataURL on a WebGL canvas typically returns
    // blank because the buffer was cleared after the previous frame.
    viewer.renderer.render(viewer.scene, viewer.camera);
    try {
        return canvasEl.toDataURL('image/png');
    } catch (err) {
        console.warn('[diagnostic] canvas.toDataURL failed:', err);
        return null;
    }
}

function downloadDataUrl(dataUrl, filename) {
    if (!dataUrl) return;
    const a = document.createElement('a');
    a.href = dataUrl;
    a.download = filename;
    document.body.appendChild(a);
    a.click();
    a.remove();
}

function downloadText(text, filename, mime = 'text/plain') {
    const blob = new Blob([text], { type: mime });
    const url = URL.createObjectURL(blob);
    downloadDataUrl(url, filename);
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

// ════════════════════════════════════════════════════════════════════
// localStorage persistence.
// ════════════════════════════════════════════════════════════════════
//
// Schema:
//   [{ id, label, capturedAt, url, userAgent, state, png? }]
// `png` is optional — we record it inline only when localStorage quota
// permits. The PNG download happens separately so loss of the inline copy
// is recoverable from the user's filesystem.

function loadSnapshotListFromStorage() {
    try {
        const raw = localStorage.getItem(SNAPSHOT_STORAGE_KEY);
        if (!raw) return [];
        const parsed = JSON.parse(raw);
        return Array.isArray(parsed) ? parsed : [];
    } catch (err) {
        console.warn('[diagnostic] failed to load snapshot list:', err);
        return [];
    }
}

function saveSnapshotListToStorage(list, includePngs = true) {
    const sanitized = includePngs
        ? list
        : list.map(({ png, ...rest }) => rest);
    try {
        localStorage.setItem(SNAPSHOT_STORAGE_KEY, JSON.stringify(sanitized));
        return true;
    } catch (err) {
        if (includePngs) {
            console.warn('[diagnostic] localStorage quota exceeded with PNGs — retrying without');
            return saveSnapshotListToStorage(list, false);
        }
        console.warn('[diagnostic] failed to save snapshot list:', err);
        return false;
    }
}

function saveSnapshotToStorage(bundle, png) {
    const list = loadSnapshotListFromStorage();
    list.push({
        id: `snap_${Date.now()}_${Math.floor(Math.random() * 1000)}`,
        ...bundle,
        png,
    });
    saveSnapshotListToStorage(list);
}

function addSnapshotToStorage(entry) {
    const list = loadSnapshotListFromStorage();
    list.push({
        id: entry.id || `snap_${Date.now()}_${Math.floor(Math.random() * 1000)}`,
        ...entry,
    });
    saveSnapshotListToStorage(list);
}

function deleteSnapshotFromStorage(id) {
    const list = loadSnapshotListFromStorage().filter(e => e.id !== id);
    saveSnapshotListToStorage(list);
}

// ════════════════════════════════════════════════════════════════════
// Shared helpers.
// ════════════════════════════════════════════════════════════════════

function indexByCategory(character) {
    const byCat = new Map();
    for (const m of character.geosetList) {
        const cat = m.userData?.geosetCategory;
        if (typeof cat !== 'number') continue;
        if (!byCat.has(cat)) byCat.set(cat, []);
        byCat.get(cat).push(m);
    }
    return byCat;
}

/**
 * Module-scope number-input factory. Used by both the closure-scoped
 * UI code inside mountDiagnosticPanel AND by the module-level
 * buildRegionEditorRow. Lives at module scope (not inside the panel
 * closure) so cross-scope callers like buildRegionEditorRow can use it
 * without falling into a "numberInput is not defined" ReferenceError —
 * the bug that bit us in May 2026.
 */
function numberInput(value, opts = {}) {
    const i = document.createElement('input');
    i.type = 'number';
    i.value = String(value);
    i.min = opts.min ?? 0;
    i.max = opts.max ?? 1024;
    i.step = opts.step ?? 1;
    i.style.cssText = `
        background: #1a1a1a;
        border: 1px solid #444;
        border-radius: 3px;
        color: #ddd;
        padding: 2px 4px;
        font-family: inherit;
        font-size: 11px;
        width: 56px;
    `;
    return i;
}
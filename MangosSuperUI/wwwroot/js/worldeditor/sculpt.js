// sculpt.js — Phase 8: terrain sculpting.
//
// Sections:
//   1. SculptBrush       — brush parameter state (radius, strength, falloff, mode)
//   2. Falloff functions  — constant / linear / smooth / gaussian
//   3. BrushPreview       — terrain-projected circle mesh
//   4. SculptHeightStrokeCommand — undoable sparse vertex deltas
//   5. SculptTool         — Tool subclass: brush hit-test, displacement, preview
//   6. SculptPanel        — brush settings UI panel
//   7. saveSculptedTerrain — POST modified vertices to server

import * as THREE from 'three';
import { Tool, Command } from './core.js';
import { postJSON } from './net.js';

// ─────────────────────────────────────────────────────────────────────────────
// 1. SculptBrush — brush parameter state
// ─────────────────────────────────────────────────────────────────────────────

export class SculptBrush {
    constructor() {
        this.radius = 20;        // world units (mesh XZ space)
        this.strength = 1.0;      // displacement per application (mesh-Y units)
        this.falloff = 'smooth'; // 'constant' | 'linear' | 'smooth' | 'gaussian'
        this.mode = 'raise';  // 'raise' | 'lower' | 'smooth' | 'flatten'
        this.interval = 50;       // ms between applications during held drag
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Falloff functions
// ─────────────────────────────────────────────────────────────────────────────

function falloff(type, dist, radius) {
    const t = Math.min(dist / radius, 1);
    if (t >= 1) return 0;
    switch (type) {
        case 'constant': return 1;
        case 'linear': return 1 - t;
        case 'smooth': return 1 - t * t * (3 - 2 * t); // smoothstep
        case 'gaussian': return Math.exp(-5.5 * t * t);
        default: return 1 - t;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. BrushPreview — terrain-projected circle
// ─────────────────────────────────────────────────────────────────────────────

function createBrushPreview(radius) {
    const segments = 64;
    const group = new THREE.Group();
    group.name = 'brushPreview';
    group.renderOrder = 999;

    // Filled disc
    const discGeo = new THREE.CircleGeometry(radius, segments);
    const discMat = new THREE.MeshBasicMaterial({
        color: 0x44ff88, opacity: 0.12, transparent: true,
        depthWrite: false, depthTest: false, side: THREE.DoubleSide
    });
    const disc = new THREE.Mesh(discGeo, discMat);
    disc.rotation.x = -Math.PI / 2;
    disc.renderOrder = 999;
    group.add(disc);

    // Ring outline
    const ringGeo = new THREE.RingGeometry(radius * 0.92, radius, segments);
    const ringMat = new THREE.MeshBasicMaterial({
        color: 0x66ffaa, opacity: 0.7, transparent: true,
        depthWrite: false, depthTest: false, side: THREE.DoubleSide
    });
    const ring = new THREE.Mesh(ringGeo, ringMat);
    ring.rotation.x = -Math.PI / 2;
    ring.renderOrder = 999;
    group.add(ring);

    group.visible = false;
    return group;
}

function updateBrushPreviewRadius(preview, radius) {
    // Rebuild children with new radius
    while (preview.children.length > 0) {
        const c = preview.children[0];
        preview.remove(c);
        if (c.geometry) c.geometry.dispose();
        if (c.material) c.material.dispose();
    }
    const segments = 64;

    const discGeo = new THREE.CircleGeometry(radius, segments);
    const discMat = new THREE.MeshBasicMaterial({
        color: 0x44ff88, opacity: 0.08, transparent: true,
        depthWrite: false, depthTest: true, side: THREE.DoubleSide
    });
    const disc = new THREE.Mesh(discGeo, discMat);
    disc.rotation.x = -Math.PI / 2;
    preview.add(disc);

    const ringGeo = new THREE.RingGeometry(radius * 0.94, radius, segments);
    const ringMat = new THREE.MeshBasicMaterial({
        color: 0x44ff88, opacity: 0.5, transparent: true,
        depthWrite: false, depthTest: true, side: THREE.DoubleSide
    });
    const ring = new THREE.Mesh(ringGeo, ringMat);
    ring.rotation.x = -Math.PI / 2;
    preview.add(ring);
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. SculptHeightStrokeCommand — sparse vertex deltas
// ─────────────────────────────────────────────────────────────────────────────

export class SculptHeightStrokeCommand extends Command {
    /**
     * @param {Editor}    editor
     * @param {string}    tileKey       e.g. "48,33"
     * @param {THREE.Mesh} terrainMesh  live terrain mesh reference
     * @param {Object}    deltas        { vertexIndex: totalDeltaY, ... }
     */
    constructor(editor, tileKey, terrainMesh, deltas) {
        super(editor);
        this.tileKey = tileKey;
        this.terrainMesh = terrainMesh;
        this.deltas = deltas;      // { int: float }
        this.updatable = false;       // one command per stroke
        this._firstRun = true;
    }

    execute() {
        // First execute is a no-op — vertices were already modified during drag.
        if (this._firstRun) { this._firstRun = false; return; }
        this._apply(1);
    }

    undo() {
        this._apply(-1);
    }

    _apply(sign) {
        const pos = this.terrainMesh.geometry.attributes.position;
        const keys = Object.keys(this.deltas);
        for (let k = 0; k < keys.length; k++) {
            const idx = parseInt(keys[k]);
            pos.setY(idx, pos.getY(idx) + sign * this.deltas[keys[k]]);
        }
        pos.needsUpdate = true;
        this.terrainMesh.geometry.computeVertexNormals();
        this.terrainMesh.geometry.attributes.normal.needsUpdate = true;
        if (this.terrainMesh.geometry.boundsTree) {
            this.terrainMesh.geometry.boundsTree.refit();
        }
    }

    dispose() {
        this.deltas = null;
        this.terrainMesh = null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. SculptTool — the terrain sculpting tool
// ─────────────────────────────────────────────────────────────────────────────

export class SculptTool extends Tool {
    constructor(editor) {
        super(editor, 'sculpt');
        this.brush = new SculptBrush();
        this.preview = createBrushPreview(this.brush.radius);
        this.panel = null; // SculptPanel, set after construction

        this._raycaster = new THREE.Raycaster();
        this._mouse = new THREE.Vector2();

        // Stroke state
        this._stroking = false;
        this._activeCommand = null;
        this._activeMesh = null;
        this._activeTileKey = null;
        this._flattenTarget = null; // Y at brush center when stroke started (flatten mode)
        this._lastApply = 0;
        this._lastNormalRecompute = 0;

        // Neighbor table for smooth mode (built lazily per geometry)
        this._neighborCache = new WeakMap();

        // Track which tiles have been modified since last save
        this._dirtyTiles = new Set();

        // Cumulative deltas across all strokes since last DB save
        // { tileKey: { vertexIndex: totalDelta } }
        this._cumulativeDeltas = {};
    }

    activate() {
        this.editor.viewport.scene.add(this.preview);
        this.preview.visible = false;
        if (this.panel) this.panel.show();
    }

    deactivate() {
        this._endStroke();
        this.preview.visible = false;
        this.editor.viewport.scene.remove(this.preview);
        if (this.panel) this.panel.hide();
    }

    // ── Pointer events ──────────────────────────────────────────────

    onPointerDown(ev, ctx) {
        if (ev.button !== 0) return false; // left-click only

        const hit = this._raycastTerrain(ev, ctx);
        if (!hit) return false;

        this._stroking = true;
        this._activeMesh = hit.object;
        this._activeTileKey = hit.object.userData.tileKey || null;
        this._lastApply = 0;

        // Build the command's delta accumulator
        this._activeCommand = {
            tileKey: this._activeTileKey,
            mesh: this._activeMesh,
            deltas: {}
        };

        // Flatten mode: capture target height at brush center
        if (this.brush.mode === 'flatten') {
            const pos = this._activeMesh.geometry.attributes.position;
            // Find nearest vertex to hit point for the target height
            const nearest = this._findNearestVertex(pos, hit.point, this._activeMesh);
            this._flattenTarget = nearest !== null ? pos.getY(nearest) : hit.point.y;
        }

        // Apply first stroke
        this._applyAtPoint(hit);
        return true; // consume — suppress OrbitControls
    }

    onPointerMove(ev, ctx) {
        const hit = this._raycastTerrain(ev, ctx);

        // Always update preview position
        if (hit) {
            this.preview.position.copy(hit.point);
            this.preview.position.y += 0.5;
            this.preview.visible = true;
        } else {
            this.preview.visible = false;
        }

        if (!this._stroking) return;

        // Throttle application
        const now = performance.now();
        if (now - this._lastApply < this.brush.interval) return;
        this._lastApply = now;

        if (hit) this._applyAtPoint(hit);
    }

    onPointerUp(ev, ctx) {
        if (!this._stroking) return;
        this._endStroke();
    }

    onKeyDown(ev) {
        // Brush radius: [ and ]
        if (ev.code === 'BracketLeft') {
            this.brush.radius = Math.max(3, this.brush.radius - 3);
            updateBrushPreviewRadius(this.preview, this.brush.radius);
            if (this.panel) this.panel.syncFromBrush();
            return true;
        }
        if (ev.code === 'BracketRight') {
            this.brush.radius = Math.min(120, this.brush.radius + 3);
            updateBrushPreviewRadius(this.preview, this.brush.radius);
            if (this.panel) this.panel.syncFromBrush();
            return true;
        }
        // Brush strength: Shift+[ and Shift+]
        if (ev.shiftKey && ev.code === 'BracketLeft') {
            this.brush.strength = Math.max(0.05, this.brush.strength - 0.2);
            if (this.panel) this.panel.syncFromBrush();
            return true;
        }
        if (ev.shiftKey && ev.code === 'BracketRight') {
            this.brush.strength = Math.min(10.0, this.brush.strength + 0.2);
            if (this.panel) this.panel.syncFromBrush();
            return true;
        }
        // Mode hotkeys: 1-4
        if (ev.code === 'Digit1') { this.brush.mode = 'raise'; if (this.panel) this.panel.syncFromBrush(); return true; }
        if (ev.code === 'Digit2') { this.brush.mode = 'lower'; if (this.panel) this.panel.syncFromBrush(); return true; }
        if (ev.code === 'Digit3') { this.brush.mode = 'smooth'; if (this.panel) this.panel.syncFromBrush(); return true; }
        if (ev.code === 'Digit4') { this.brush.mode = 'flatten'; if (this.panel) this.panel.syncFromBrush(); return true; }

        // Escape → deactivate back to select
        if (ev.code === 'Escape') {
            this.editor.tools.setActive('select');
            return true;
        }
        return false;
    }

    onWheel(ev, ctx) {
        // Scroll adjusts brush radius while sculpt tool is active
        if (!ev.shiftKey) return;
        ev.preventDefault();
        const delta = ev.deltaY > 0 ? -3 : 3;
        this.brush.radius = Math.max(3, Math.min(120, this.brush.radius + delta));
        updateBrushPreviewRadius(this.preview, this.brush.radius);
        if (this.panel) this.panel.syncFromBrush();
    }

    // ── Internal ─────────────────────────────────────────────────────

    _raycastTerrain(ev, ctx) {
        if (!ctx) ctx = this.editor.viewport._ctx();
        const rect = this.editor.viewport.canvas.getBoundingClientRect();
        this._mouse.x = ((ev.clientX - rect.left) / rect.width) * 2 - 1;
        this._mouse.y = -((ev.clientY - rect.top) / rect.height) * 2 + 1;
        this._raycaster.setFromCamera(this._mouse, ctx.camera);

        const terrainMeshes = this.editor.tileGrid ? this.editor.tileGrid.terrainMeshes() : [];
        if (terrainMeshes.length === 0) return null;

        const hits = this._raycaster.intersectObjects(terrainMeshes, false);
        return hits.length > 0 ? hits[0] : null;
    }

    _findNearestVertex(pos, point, mesh) {
        // Convert hit point to mesh-local space
        const localPt = mesh.worldToLocal(point.clone());
        let bestIdx = null;
        let bestDist = Infinity;
        for (let i = 0; i < pos.count; i++) {
            const dx = pos.getX(i) - localPt.x;
            const dz = pos.getZ(i) - localPt.z;
            const d2 = dx * dx + dz * dz;
            if (d2 < bestDist) { bestDist = d2; bestIdx = i; }
        }
        return bestIdx;
    }

    _applyAtPoint(hit) {
        const mesh = this._activeMesh;
        if (!mesh) return;

        const pos = mesh.geometry.attributes.position;
        const brush = this.brush;
        const deltas = this._activeCommand.deltas;

        // Convert hit point to mesh-local space
        const localPt = mesh.worldToLocal(hit.point.clone());

        const radiusSq = brush.radius * brush.radius;
        const mode = brush.mode;

        // Collect vertices in radius (XZ-plane distance in local space)
        for (let i = 0; i < pos.count; i++) {
            const vx = pos.getX(i);
            const vz = pos.getZ(i);
            const dx = vx - localPt.x;
            const dz = vz - localPt.z;
            const distSq = dx * dx + dz * dz;
            if (distSq > radiusSq) continue;

            const dist = Math.sqrt(distSq);
            const weight = falloff(brush.falloff, dist, brush.radius);
            if (weight <= 0) continue;

            let delta = 0;
            const curY = pos.getY(i);

            if (mode === 'raise') {
                delta = brush.strength * weight;
            } else if (mode === 'lower') {
                delta = -brush.strength * weight;
            } else if (mode === 'flatten') {
                const diff = this._flattenTarget - curY;
                delta = diff * weight * Math.min(1, brush.strength);
            } else if (mode === 'smooth') {
                const avgY = this._neighborAvgY(mesh.geometry, i);
                if (avgY !== null) {
                    delta = (avgY - curY) * weight * Math.min(1, brush.strength);
                }
            }

            if (Math.abs(delta) < 0.0001) continue;

            pos.setY(i, curY + delta);
            deltas[i] = (deltas[i] || 0) + delta;
        }

        pos.needsUpdate = true;

        // Throttled normal recompute during drag
        const now = performance.now();
        if (now - this._lastNormalRecompute > 60) {
            this._lastNormalRecompute = now;
            mesh.geometry.computeVertexNormals();
            mesh.geometry.attributes.normal.needsUpdate = true;
        }
    }

    _neighborAvgY(geometry, vertIdx) {
        const neighbors = this._getNeighborTable(geometry);
        const nbrs = neighbors[vertIdx];
        if (!nbrs || nbrs.length === 0) return null;

        const pos = geometry.attributes.position;
        let sum = 0;
        for (let n = 0; n < nbrs.length; n++) {
            sum += pos.getY(nbrs[n]);
        }
        return sum / nbrs.length;
    }

    _getNeighborTable(geometry) {
        if (this._neighborCache.has(geometry)) return this._neighborCache.get(geometry);

        const index = geometry.index;
        const count = geometry.attributes.position.count;
        const table = new Array(count);
        for (let i = 0; i < count; i++) table[i] = [];

        if (index) {
            const arr = index.array;
            for (let t = 0; t < arr.length; t += 3) {
                const a = arr[t], b = arr[t + 1], c = arr[t + 2];
                if (table[a].indexOf(b) === -1) table[a].push(b);
                if (table[a].indexOf(c) === -1) table[a].push(c);
                if (table[b].indexOf(a) === -1) table[b].push(a);
                if (table[b].indexOf(c) === -1) table[b].push(c);
                if (table[c].indexOf(a) === -1) table[c].push(a);
                if (table[c].indexOf(b) === -1) table[c].push(b);
            }
        }

        this._neighborCache.set(geometry, table);
        return table;
    }

    _endStroke() {
        if (!this._stroking) return;

        const cmd = this._activeCommand;
        const mesh = this._activeMesh;

        if (mesh && cmd && Object.keys(cmd.deltas).length > 0) {
            // Final full-quality normal recompute
            mesh.geometry.computeVertexNormals();
            mesh.geometry.attributes.normal.needsUpdate = true;

            // BVH refit
            if (mesh.geometry.boundsTree) {
                mesh.geometry.boundsTree.refit();
            }

            // Push to history
            const historyCmd = new SculptHeightStrokeCommand(
                this.editor, cmd.tileKey, cmd.mesh, cmd.deltas
            );
            this.editor.history.execute(historyCmd);

            // Mark tile dirty + accumulate deltas for DB persistence
            if (cmd.tileKey) {
                this._dirtyTiles.add(cmd.tileKey);
                this._accumulateDeltas(cmd.tileKey, cmd.deltas);
            }
        }

        this._stroking = false;
        this._activeCommand = null;
        this._activeMesh = null;
        this._activeTileKey = null;
        this._flattenTarget = null;
    }

    /** Returns Set of tile keys that have been modified since last save. */
    get dirtyTiles() { return this._dirtyTiles; }

    /** Clear dirty state after a successful save. */
    clearDirty() { this._dirtyTiles.clear(); }

    /**
     * Returns cumulative deltas for a tile (all strokes since last save).
     * Used by the save endpoint to persist to DB.
     */
    getCumulativeDeltas(tileKey) {
        return this._cumulativeDeltas[tileKey] || {};
    }

    /** Merge stroke deltas into the cumulative tracker. */
    _accumulateDeltas(tileKey, strokeDeltas) {
        if (!this._cumulativeDeltas[tileKey]) this._cumulativeDeltas[tileKey] = {};
        const cum = this._cumulativeDeltas[tileKey];
        const keys = Object.keys(strokeDeltas);
        for (let k = 0; k < keys.length; k++) {
            const idx = keys[k];
            cum[idx] = (cum[idx] || 0) + strokeDeltas[idx];
        }
    }

    /** Clear cumulative deltas after a successful save to DB. */
    clearCumulativeDeltas() { this._cumulativeDeltas = {}; }

    /** Whether this tile has been committed (ADT patched + MPQ built). */
    committed = false;
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. SculptPanel — brush settings + save/commit/download/regen UI
//
//    Mirrors the WMO PlacementModal pattern:
//      Save      → POST deltas to DB (uncommitted). Terrain is visible in
//                   the editor immediately. No ADT/MPQ/.map changes.
//      Commit    → Patches ADT MCVT, builds patch-Z.MPQ, regenerates .map.
//                   After this, download the MPQ for the WoW client.
//      Download  → Serves patch-Z.MPQ (same endpoint as WMO placements).
//      Regen     → Rebuilds vmaps/mmaps for the tile so NPC pathfinding
//                   respects the sculpted terrain.
// ─────────────────────────────────────────────────────────────────────────────

export class SculptPanel {
    constructor(editor, sculptTool) {
        this.editor = editor;
        this.tool = sculptTool;
        this.brush = sculptTool.brush;
        this.el = null;
        this._build();
        sculptTool.panel = this;
    }

    _build() {
        const panel = document.createElement('div');
        panel.id = 'weSculptPanel';
        panel.style.cssText =
            'position:absolute;top:60px;left:12px;width:230px;padding:12px 14px;' +
            'background:rgba(20,20,28,0.92);border:1px solid rgba(255,255,255,0.12);' +
            'border-radius:8px;color:#ccc;font-size:12px;font-family:system-ui,sans-serif;' +
            'z-index:20;display:none;user-select:none;';

        const title = '<div style="font-size:13px;font-weight:600;color:#fff;margin-bottom:10px;">' +
            '\u{1F3D4} Terrain Sculpt</div>';

        // Mode buttons
        const modes = ['raise', 'lower', 'smooth', 'flatten'];
        const modeLabels = { raise: '\u25B2 Raise', lower: '\u25BC Lower', smooth: '\u223F Smooth', flatten: '\u2501 Flatten' };
        let modeHtml = '<div style="margin-bottom:10px;">';
        modes.forEach((m, i) => {
            modeHtml += '<button class="sculpt-mode-btn" data-mode="' + m + '" ' +
                'style="padding:3px 8px;margin:0 2px 4px 0;border:1px solid rgba(255,255,255,0.2);' +
                'border-radius:4px;background:' + (m === this.brush.mode ? '#3a6' : 'rgba(255,255,255,0.06)') +
                ';color:#fff;font-size:11px;cursor:pointer;">' +
                modeLabels[m] + ' <span style="color:#888;font-size:10px;">(' + (i + 1) + ')</span></button>';
        });
        modeHtml += '</div>';

        // Radius slider
        const radiusHtml =
            '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:6px;">' +
            '<span>Radius</span>' +
            '<span id="sculptRadiusVal" style="color:#8f8;font-family:monospace;">' + this.brush.radius + '</span>' +
            '</div>' +
            '<input type="range" id="sculptRadius" min="3" max="120" value="' + this.brush.radius +
            '" style="width:100%;margin-bottom:10px;accent-color:#3a6;">';

        // Strength slider
        const strengthHtml =
            '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:6px;">' +
            '<span>Strength</span>' +
            '<span id="sculptStrengthVal" style="color:#8f8;font-family:monospace;">' + this.brush.strength.toFixed(2) + '</span>' +
            '</div>' +
            '<input type="range" id="sculptStrength" min="5" max="1000" value="' + Math.round(this.brush.strength * 100) +
            '" style="width:100%;margin-bottom:10px;accent-color:#3a6;">';

        // Falloff select
        const falloffHtml =
            '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:10px;">' +
            '<span>Falloff</span>' +
            '<select id="sculptFalloff" style="background:#222;color:#ccc;border:1px solid #555;' +
            'border-radius:3px;padding:2px 6px;font-size:11px;">' +
            '<option value="smooth"' + (this.brush.falloff === 'smooth' ? ' selected' : '') + '>Smooth</option>' +
            '<option value="linear"' + (this.brush.falloff === 'linear' ? ' selected' : '') + '>Linear</option>' +
            '<option value="gaussian"' + (this.brush.falloff === 'gaussian' ? ' selected' : '') + '>Gaussian</option>' +
            '<option value="constant"' + (this.brush.falloff === 'constant' ? ' selected' : '') + '>Constant</option>' +
            '</select></div>';

        // Divider
        const divider = '<div style="border-top:1px solid rgba(255,255,255,0.1);margin:8px 0;"></div>';

        // Save button (DB only — no ADT/MPQ changes)
        const saveHtml =
            '<button id="sculptSave" style="width:100%;padding:6px;' +
            'background:#2a4a5a;color:#fff;border:1px solid #3a7a9a;border-radius:4px;' +
            'cursor:pointer;font-size:12px;font-weight:500;">' +
            '\u{1F4BE} Save Sculpt Data</button>';

        // Commit button (patches ADT + builds MPQ + writes .map)
        const commitHtml =
            '<button id="sculptCommit" style="width:100%;padding:6px;margin-top:6px;' +
            'background:#2a5a3a;color:#fff;border:1px solid #3a7;border-radius:4px;' +
            'cursor:pointer;font-size:12px;font-weight:500;display:none;">' +
            '\u{1F30D} Commit to Game World</button>';

        // Download MPQ button (same as WMO placement)
        const downloadHtml =
            '<button id="sculptDownloadMpq" style="width:100%;padding:5px;margin-top:4px;' +
            'background:transparent;color:#6af;border:1px solid #4a8abf;border-radius:4px;' +
            'cursor:pointer;font-size:11px;display:none;">' +
            '\u2B07 Download patch-Z.MPQ for Client</button>';

        // Regen server data button
        const regenHtml =
            '<button id="sculptRegen" style="width:100%;padding:5px;margin-top:4px;' +
            'background:transparent;color:#fa4;border:1px solid #a83;border-radius:4px;' +
            'cursor:pointer;font-size:11px;display:none;">' +
            '\u2699 Regenerate Server Data</button>' +
            '<div id="sculptRegenProgress" style="display:none;margin-top:4px;font-size:10px;' +
            'max-height:100px;overflow-y:auto;background:rgba(0,0,0,0.3);border-radius:4px;padding:6px;"></div>';

        // Restore Vanilla Defaults button
        const restoreHtml =
            '<button id="sculptRestore" style="width:100%;padding:5px;margin-top:8px;' +
            'background:transparent;color:#f66;border:1px solid #a44;border-radius:4px;' +
            'cursor:pointer;font-size:11px;display:none;">' +
            '\u21BA Restore Vanilla Terrain</button>';

        // Status line
        const statusHtml =
            '<div id="sculptStatus" style="font-size:10px;color:#888;margin-top:4px;text-align:center;min-height:14px;"></div>';

        // Hotkey hint
        const hintHtml =
            '<div style="margin-top:6px;font-size:10px;color:#555;line-height:1.5;">' +
            '[ ] Radius &nbsp; Shift+[ ] Strength<br>' +
            '1-4 Mode &nbsp; Esc Exit</div>';

        panel.innerHTML = title + modeHtml + radiusHtml + strengthHtml + falloffHtml +
            divider + saveHtml + commitHtml + downloadHtml + regenHtml + restoreHtml + statusHtml + hintHtml;

        this.editor.viewport.canvas.parentElement.appendChild(panel);
        this.el = panel;

        // ── Wiring ──

        // Mode buttons
        panel.querySelectorAll('.sculpt-mode-btn').forEach((btn) => {
            btn.addEventListener('click', () => {
                this.brush.mode = btn.dataset.mode;
                this.syncFromBrush();
                btn.blur();
            });
        });

        // Radius slider
        const radiusSlider = panel.querySelector('#sculptRadius');
        const radiusVal = panel.querySelector('#sculptRadiusVal');
        radiusSlider.addEventListener('input', () => {
            this.brush.radius = parseInt(radiusSlider.value);
            radiusVal.textContent = this.brush.radius;
            updateBrushPreviewRadius(this.tool.preview, this.brush.radius);
        });
        radiusSlider.addEventListener('keydown', (e) => { radiusSlider.blur(); });

        // Strength slider
        const strengthSlider = panel.querySelector('#sculptStrength');
        const strengthVal = panel.querySelector('#sculptStrengthVal');
        strengthSlider.addEventListener('input', () => {
            this.brush.strength = parseInt(strengthSlider.value) / 100;
            strengthVal.textContent = this.brush.strength.toFixed(2);
        });
        strengthSlider.addEventListener('keydown', (e) => { strengthSlider.blur(); });

        // Falloff select
        const falloffSelect = panel.querySelector('#sculptFalloff');
        falloffSelect.addEventListener('change', () => {
            this.brush.falloff = falloffSelect.value;
        });

        // Save (DB only)
        panel.querySelector('#sculptSave').addEventListener('click', () => this._save());

        // Commit (ADT + MPQ + .map)
        panel.querySelector('#sculptCommit').addEventListener('click', () => this._commit());

        // Download MPQ
        panel.querySelector('#sculptDownloadMpq').addEventListener('click', () => {
            window.location.href = '/WorldEditor/DownloadPatchMpq';
        });

        // Regen server data
        panel.querySelector('#sculptRegen').addEventListener('click', () => this._regen());

        // Restore vanilla terrain
        panel.querySelector('#sculptRestore').addEventListener('click', () => this._restore());
    }

    hide() { if (this.el) this.el.style.display = 'none'; }

    closeIfOpen() {
        if (this.el && this.el.style.display !== 'none') {
            this.editor.tools.setActive('select');
        }
    }

    syncFromBrush() {
        if (!this.el) return;
        const b = this.brush;
        this.el.querySelectorAll('.sculpt-mode-btn').forEach((btn) => {
            btn.style.background = btn.dataset.mode === b.mode
                ? '#3a6' : 'rgba(255,255,255,0.06)';
        });
        const rs = this.el.querySelector('#sculptRadius');
        if (rs) rs.value = b.radius;
        const rv = this.el.querySelector('#sculptRadiusVal');
        if (rv) rv.textContent = b.radius;
        const ss = this.el.querySelector('#sculptStrength');
        if (ss) ss.value = Math.round(b.strength * 100);
        const sv = this.el.querySelector('#sculptStrengthVal');
        if (sv) sv.textContent = b.strength.toFixed(2);
        const fo = this.el.querySelector('#sculptFalloff');
        if (fo) fo.value = b.falloff;
    }

    _setStatus(msg) {
        const el = this.el.querySelector('#sculptStatus');
        if (el) el.textContent = msg;
    }

    _syncButtons() {
        if (!this.el) return;
        const dirty = this.tool.dirtyTiles.size > 0;
        const committed = this.tool.committed;
        const hasSavedData = this.tool._hasSavedData || false;

        // Commit visible when there are unsaved changes OR saved-but-uncommitted data
        const commitBtn = this.el.querySelector('#sculptCommit');
        if (commitBtn) commitBtn.style.display = (dirty || hasSavedData || committed) ? 'block' : 'none';

        // Download + Regen visible after commit
        const dlBtn = this.el.querySelector('#sculptDownloadMpq');
        if (dlBtn) dlBtn.style.display = committed ? 'block' : 'none';
        const rgBtn = this.el.querySelector('#sculptRegen');
        if (rgBtn) rgBtn.style.display = committed ? 'block' : 'none';

        // Restore visible whenever there are any sculpt changes (dirty, saved, or committed)
        const restoreBtn = this.el.querySelector('#sculptRestore');
        if (restoreBtn) restoreBtn.style.display = (dirty || hasSavedData || committed) ? 'block' : 'none';
    }

    _save() {
        const tool = this.tool;
        const dirty = tool.dirtyTiles;

        if (dirty.size === 0) {
            this._setStatus('No changes to save.');
            return;
        }

        const tileGrid = this.editor.tileGrid;
        if (!tileGrid) return;

        this._setStatus('Saving...');

        // For each dirty tile, collect the cumulative deltas and POST to DB.
        const promises = [];
        dirty.forEach((tileKey) => {
            const entry = tileGrid.tiles[tileKey];
            if (!entry || !entry.mesh) return;

            const deltas = tool.getCumulativeDeltas(tileKey);
            if (!deltas || Object.keys(deltas).length === 0) return;

            const payload = {
                preset: this.editor.currentPreset,
                tileGridX: entry.gridX,
                tileGridY: entry.gridY,
                deltas: deltas  // { vertexIndex: cumulativeDelta }
            };

            promises.push(
                postJSON('/WorldEditor/SaveSculptData', payload)
                    .then((resp) => resp && resp.success)
                    .catch(() => false)
            );
        });

        Promise.all(promises).then((results) => {
            if (results.every(Boolean)) {
                // Don't clear dirtyTiles here — tiles are still "dirty" (uncommitted).
                // They need to stay dirty so Commit knows which tiles to process.
                // Only clear the cumulative deltas (they've been persisted to DB).
                tool.clearCumulativeDeltas();
                this._setStatus('Saved to DB \u2714 — click Commit to apply to game world');
                this._syncButtons();
                // Show commit button now
                const commitBtn = this.el.querySelector('#sculptCommit');
                if (commitBtn) commitBtn.style.display = 'block';
                setTimeout(() => this._setStatus(''), 5000);
            } else {
                this._setStatus('Some tiles failed to save.');
            }
        });
    }

    _commit() {
        this._setStatus('Committing...');

        const tileGrid = this.editor.tileGrid;
        if (!tileGrid) return;

        // Send the center tile's current mesh heights to the server.
        // The server will compare against vanilla and only patch if different.
        // This avoids all the dirty-tracking state bugs.
        const centerKey = tileGrid._key(tileGrid.centerGridX, tileGrid.centerGridY);
        const centerEntry = tileGrid.tiles[centerKey];

        if (!centerEntry || !centerEntry.mesh) {
            this._setStatus('No terrain loaded.');
            return;
        }

        const pos = centerEntry.mesh.geometry.attributes.position;

        // ── DEBUG: FULL DIAGNOSTIC ──
        const terrainMeshes = tileGrid.terrainMeshes ? tileGrid.terrainMeshes() : [];
        const sceneMeshes = [];
        this.editor.scene.traverse(obj => { if (obj.isMesh && obj.geometry?.attributes?.position?.count === 16641) sceneMeshes.push(obj); });
        console.log('=== SCULPT COMMIT DIAGNOSTIC ===');
        console.log('centerKey:', centerKey, 'gridX:', centerEntry.gridX, 'gridY:', centerEntry.gridY);
        console.log('centerEntry.mesh uuid:', centerEntry.mesh?.uuid, 'type:', centerEntry.mesh?.type, 'vertCount:', pos.count);
        console.log('centerEntry.mesh Y[0..4]:', pos.getY(0), pos.getY(1), pos.getY(2), pos.getY(3), pos.getY(4));
        console.log('terrainMeshes() count:', terrainMeshes.length);
        terrainMeshes.forEach((m, i) => {
            const mp = m.geometry?.attributes?.position;
            console.log('  terrainMesh[' + i + ']: uuid=' + m.uuid + ' verts=' + mp?.count + ' isEntry=' + (m === centerEntry.mesh) + ' sameGeo=' + (m.geometry === centerEntry.mesh?.geometry) + ' tileKey=' + m.userData?.tileKey + ' Y[0]=' + mp?.getY(0));
        });
        console.log('scene 16641-vert meshes:', sceneMeshes.length);
        sceneMeshes.forEach((m, i) => {
            const mp = m.geometry?.attributes?.position;
            console.log('  sceneMesh[' + i + ']: uuid=' + m.uuid + ' isEntry=' + (m === centerEntry.mesh) + ' sameGeo=' + (m.geometry === centerEntry.mesh?.geometry) + ' tileKey=' + m.userData?.tileKey + ' parent=' + m.parent?.type + ' Y[0]=' + mp?.getY(0));
        });
        console.log('tileGrid.tiles keys:', Object.keys(tileGrid.tiles).join(', '));
        for (const [k, v] of Object.entries(tileGrid.tiles)) {
            const tp = v.mesh?.geometry?.attributes?.position;
            const inScene = sceneMeshes.find(s => s === v.mesh);
            const inTerrain = terrainMeshes.find(s => s === v.mesh);
            console.log('  tile[' + k + ']: mesh.uuid=' + v.mesh?.uuid + ' verts=' + tp?.count + ' inScene=' + !!inScene + ' inTerrainMeshes=' + !!inTerrain + ' Y[0]=' + tp?.getY(0));
        }
        console.log('tool._activeMesh:', this.tool._activeMesh?.uuid || 'null');
        console.log('tool._activeTileKey:', this.tool._activeTileKey || 'null');
        console.log('tool._cumulativeDeltas keys:', Object.keys(this.tool._cumulativeDeltas || {}).join(', '));
        console.log('tool._dirtyTiles:', Array.from(this.tool._dirtyTiles || []).join(', '));
        console.log('=== END DIAGNOSTIC ===');
        // ── END DEBUG ──

        const heights = new Float32Array(pos.count);
        for (let i = 0; i < pos.count; i++) heights[i] = pos.getY(i);

        const payload = {
            preset: this.editor.currentPreset,
            tileGridX: centerEntry.gridX,
            tileGridY: centerEntry.gridY,
            globalMidHeight: tileGrid.globalMidHeight,
            globalHeightScale: tileGrid.globalHeightScale,
            heights: Array.from(heights)
        };

  
        this._setStatus('Committing terrain...');
        postJSON('/WorldEditor/CommitSculptedTerrain', payload)
            .then((resp) => {
                if (resp && resp.success) {
                    this.tool.committed = true;
                    this.tool.clearDirty();
                    this._setStatus('Committed \u2714 — download MPQ for client');
                    this._syncButtons();
                } else {
                    this._setStatus('Commit failed: ' + (resp?.error || 'unknown'));
                }
            })
            .catch((err) => {
                this._setStatus('Commit error: ' + err.message);
            });
    }

    _regen() {
        const progressEl = this.el.querySelector('#sculptRegenProgress');
        if (progressEl) { progressEl.style.display = 'block'; progressEl.textContent = 'Starting...\n'; }
        this._setStatus('Regenerating server data...');

        // Use the same SSE pattern as WMO placement regen.
        // The endpoint needs a preset to resolve the tile.
        const preset = this.editor.currentPreset;
        if (!preset) { this._setStatus('No preset loaded.'); return; }

        fetch('/WorldEditor/RegenerateSculptServerData?preset=' + encodeURIComponent(preset), {
            method: 'POST'
        }).then((response) => {
            const reader = response.body.getReader();
            const decoder = new TextDecoder();
            const read = () => {
                reader.read().then(({ done, value }) => {
                    if (done) { this._setStatus('Regen complete \u2714'); return; }
                    const text = decoder.decode(value, { stream: true });
                    const lines = text.split('\n');
                    for (const line of lines) {
                        if (line.startsWith('data: ')) {
                            const msg = line.substring(6);
                            if (progressEl) progressEl.textContent += msg + '\n';
                            if (msg.startsWith('DONE:')) this._setStatus('Regen complete \u2714');
                            else if (msg.startsWith('ERROR:')) this._setStatus(msg);
                        }
                    }
                    if (progressEl) progressEl.scrollTop = progressEl.scrollHeight;
                    read();
                });
            };
            read();
        }).catch((err) => {
            this._setStatus('Regen failed: ' + err.message);
        });
    }

    _restore() {
        if (!confirm('Restore vanilla terrain?\n\nThis will delete all sculpt data from the DB, ' +
            'restore the original .map file, and delete patch-Z.MPQ.\n\n' +
            'You will need to reload the preset to see the restored terrain in the editor.')) {
            return;
        }

        this._setStatus('Restoring vanilla terrain...');

        const preset = this.editor.currentPreset;
        if (!preset) { this._setStatus('No preset loaded.'); return; }

        postJSON('/WorldEditor/RestoreSculptedTerrain', { preset })
            .then((resp) => {
                if (resp && resp.success) {
                    this.tool.committed = false;
                    this.tool._hasSavedData = false;
                    this.tool.clearDirty();
                    this.tool.clearCumulativeDeltas();
                    this._setStatus('Vanilla restored \u2714 — reload preset to see changes');
                    this._syncButtons();
                } else {
                    this._setStatus('Restore failed: ' + (resp?.error || 'unknown'));
                }
            })
            .catch((err) => {
                this._setStatus('Restore error: ' + err.message);
            });
    }

    show() {
        if (this.el) {
            this.el.style.display = '';
            this._checkForSavedData();
            this._syncButtons();
        }
    }

    /** Check server for existing sculpt data on this preset to show correct buttons. */
    _checkForSavedData() {
        const preset = this.editor.currentPreset;
        if (!preset) return;

        fetch('/WorldEditor/HasSculptData?preset=' + encodeURIComponent(preset))
            .then(r => r.json())
            .then((resp) => {
                if (resp && resp.success) {
                    this.tool._hasSavedData = resp.hasSavedData;
                    this.tool.committed = resp.hasCommitted;
                    this._syncButtons();
                }
            })
            .catch(() => { /* silent */ });
    }
}
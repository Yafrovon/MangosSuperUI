// transform.js — Phase 5: TransformControls integration + transform commands.
//
// Sections:
//   1. Constants and helpers
//   2. SetPositionCommand   — coalescing translate command
//   3. SetRotationCommand   — coalescing rotate command (Y-axis only)
//   4. TransformGizmoManager — owns the TransformControls instance,
//                              attaches to selection, manages multi-select
//                              temp-Group, drives commands on drag-end.
//
// Scope:
//   - Translate (G hotkey) and rotate (R hotkey) only. Scale is uniform in
//     the placement data model and is rarely meaningful for WoW WMOs, so
//     it's deferred. (Re-add by registering a third mode if needed.)
//   - Rotation is restricted to the Y axis because the placement data model
//     stores `rotY` (yaw, degrees) and has no representation for pitch/roll.
//   - Only placed WMOs (regular THREE.Mesh in objectStream.pool.wmoGroup)
//     are transformable. Streamed (vanilla ADT) InstancedMesh entries are
//     `selectable: false` since Phase 4 — Phase 7 will revisit.
//
// Hotkey rationale:
//   The handoff suggested W/E/R but those collide with WASD camera movement
//   (W = forward strafe, E = up strafe). G (grab) and R (rotate) are
//   unclaimed and follow Blender's mnemonic — see README "Phase 5 hotkeys"
//   section for the full rationale. Esc detaches the gizmo without
//   clearing the selection.
//
// Coalescing:
//   The commands set `updatable = true` and implement `coalesceKey()`. We
//   push at drag-END only (one command per drag), but the coalesce window
//   merges back-to-back drags of the same object within 500ms into one
//   undo entry. Per-frame command pushing during the drag is unnecessary —
//   TransformControls handles the live visual.

import * as THREE from 'three';
import { TransformControls } from 'three/addons/controls/TransformControls.js';
import { Command } from './core.js';
import { safeDispose } from './render.js';
import {
    PlacementContext,
    cloneAsGhost,
    disposeGhostClone
} from './collision.js';

const ROT_SNAP_DEG = 15;       // Shift-modifier rotation snap
const TRANS_SNAP = 1.0;      // Shift-modifier translation snap (world units)

// ─────────────────────────────────────────────────────────────────────────────
// 1. Helpers
// ─────────────────────────────────────────────────────────────────────────────

// Normalize a degree value to [0, 360).
function normDeg(d) {
    d = d % 360;
    if (d < 0) d += 360;
    return d;
}

// Read a placed-WMO mesh's current rotY (degrees) from its Euler. Mesh
// rotation order is Three's default ('XYZ'); we only ever set .rotation.y.
function meshRotYDeg(mesh) {
    return normDeg(mesh.rotation.y * 180 / Math.PI);
}

// Resolve a mesh back to its placement entry. Placed WMOs have
// userData.placementId. Returns null for anything else.
function placementForMesh(editor, mesh) {
    const pid = mesh && mesh.userData ? mesh.userData.placementId : null;
    if (pid == null) return null;
    const store = editor.placementStore;
    return store.placedWmos.find((p) => p.id === pid) || null;
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. SetPositionCommand
// ─────────────────────────────────────────────────────────────────────────────
//
// Captures per-object prev/new world positions for one or more meshes. On
// execute(), writes the new positions into both the mesh transforms AND the
// placement data model, then schedules a saveToDb. On undo(), reverses.
//
// Coalescing: keyed on the sorted list of placement ids. Two drags on the
// same set of objects within 500ms merge — coalesce() copies the new
// positions from the second drag, but keeps the first drag's `prev` so
// undo restores all the way to the original.

export class SetPositionCommand extends Command {
    constructor(editor, entries) {
        super(editor);
        // entries: [{ mesh, placementId, prev: Vector3, next: Vector3 }]
        this.entries = entries.map((e) => ({
            mesh: e.mesh,
            placementId: e.placementId,
            prev: e.prev.clone(),
            next: e.next.clone()
        }));
        this.updatable = true;
        this.name = entries.length === 1 ? 'Move WMO' : ('Move ' + entries.length + ' WMOs');
        this._key = this.entries.map((e) => e.placementId).sort((a, b) => a - b).join(',');
    }

    coalesceKey() { return 'pos:' + this._key; }

    update(newCmd) {
        // Same set of placement ids (coalesce key already matched). Copy
        // their `next` values across by id; keep our existing `prev`.
        const lookup = {};
        for (let i = 0; i < newCmd.entries.length; i++) {
            const n = newCmd.entries[i];
            lookup[n.placementId] = n.next;
        }
        for (let i = 0; i < this.entries.length; i++) {
            const e = this.entries[i];
            const np = lookup[e.placementId];
            if (np) e.next.copy(np);
        }
        this.name = newCmd.name;
    }

    execute() {
        const store = this.editor.placementStore;
        for (let i = 0; i < this.entries.length; i++) {
            const e = this.entries[i];
            const oldX = e.mesh.position.x, oldZ = e.mesh.position.z;
            e.mesh.position.copy(e.next);
            const p = placementForMesh(this.editor, e.mesh);
            if (p) {
                p.x = e.next.x; p.y = e.next.y; p.z = e.next.z;
                store.updateSpatialKey(p, oldX, oldZ);
                store.saveToDb(p);
                this.editor.signals.placementUpdated.dispatch(p);
            }
        }
    }

    undo() {
        const store = this.editor.placementStore;
        for (let i = 0; i < this.entries.length; i++) {
            const e = this.entries[i];
            const oldX = e.mesh.position.x, oldZ = e.mesh.position.z;
            e.mesh.position.copy(e.prev);
            const p = placementForMesh(this.editor, e.mesh);
            if (p) {
                p.x = e.prev.x; p.y = e.prev.y; p.z = e.prev.z;
                store.updateSpatialKey(p, oldX, oldZ);
                store.saveToDb(p);
                this.editor.signals.placementUpdated.dispatch(p);
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. SetRotationCommand — Y-axis only (matches placement.rotY data model)
// ─────────────────────────────────────────────────────────────────────────────
//
// We only track rotY in the data model. Multi-select rotation around the
// centroid produces new positions AND new rotations for each child after
// detach() reparents them — we capture both so undo is complete. For
// translation-only operations callers should use SetPositionCommand
// instead; this one handles the position-as-side-effect-of-rotation case.

export class SetRotationCommand extends Command {
    constructor(editor, entries) {
        super(editor);
        // entries: [{ mesh, placementId,
        //             prevPos, nextPos, prevRotY, nextRotY }]
        this.entries = entries.map((e) => ({
            mesh: e.mesh,
            placementId: e.placementId,
            prevPos: e.prevPos.clone(),
            nextPos: e.nextPos.clone(),
            prevRotY: e.prevRotY,
            nextRotY: e.nextRotY
        }));
        this.updatable = true;
        this.name = entries.length === 1 ? 'Rotate WMO' : ('Rotate ' + entries.length + ' WMOs');
        this._key = this.entries.map((e) => e.placementId).sort((a, b) => a - b).join(',');
    }

    coalesceKey() { return 'rot:' + this._key; }

    update(newCmd) {
        const lookup = {};
        for (let i = 0; i < newCmd.entries.length; i++) {
            const n = newCmd.entries[i];
            lookup[n.placementId] = n;
        }
        for (let i = 0; i < this.entries.length; i++) {
            const e = this.entries[i];
            const n = lookup[e.placementId];
            if (n) {
                e.nextPos.copy(n.nextPos);
                e.nextRotY = n.nextRotY;
            }
        }
        this.name = newCmd.name;
    }

    execute() {
        const store = this.editor.placementStore;
        for (let i = 0; i < this.entries.length; i++) {
            const e = this.entries[i];
            const oldX = e.mesh.position.x, oldZ = e.mesh.position.z;
            e.mesh.position.copy(e.nextPos);
            e.mesh.rotation.y = e.nextRotY * Math.PI / 180;
            const p = placementForMesh(this.editor, e.mesh);
            if (p) {
                p.x = e.nextPos.x; p.y = e.nextPos.y; p.z = e.nextPos.z;
                p.rotY = e.nextRotY;
                store.updateSpatialKey(p, oldX, oldZ);
                store.saveToDb(p);
                this.editor.signals.placementUpdated.dispatch(p);
            }
        }
    }

    undo() {
        const store = this.editor.placementStore;
        for (let i = 0; i < this.entries.length; i++) {
            const e = this.entries[i];
            const oldX = e.mesh.position.x, oldZ = e.mesh.position.z;
            e.mesh.position.copy(e.prevPos);
            e.mesh.rotation.y = e.prevRotY * Math.PI / 180;
            const p = placementForMesh(this.editor, e.mesh);
            if (p) {
                p.x = e.prevPos.x; p.y = e.prevPos.y; p.z = e.prevPos.z;
                p.rotY = e.prevRotY;
                store.updateSpatialKey(p, oldX, oldZ);
                store.saveToDb(p);
                this.editor.signals.placementUpdated.dispatch(p);
            }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. TransformGizmoManager
// ─────────────────────────────────────────────────────────────────────────────
//
// Owns the single TransformControls instance. Subscribes to selectionChanged
// and reattaches the gizmo to the appropriate target:
//
//   - 0 selected items   → detach (gizmo hidden)
//   - 1 selected mesh    → attach to that mesh
//   - N selected meshes  → create a temp-Group at the centroid, Object3D.attach
//                          each mesh to it, attach the gizmo to the group.
//                          On drag-end, detach the children back to their
//                          original parent (preserving world transforms).
//
// Rotation is restricted to Y (showX = showZ = false) when in rotate mode.
//
// Walk mode + transform: the gizmo is hidden whenever walk mode is on.
// Multi-select rotation while walking is awkward and the design isn't
// proven; better to disable cleanly than half-support.

export class TransformGizmoManager {
    constructor(editor) {
        this.editor = editor;
        this.viewport = editor.viewport;
        this.tcontrols = new TransformControls(this.viewport.rig.camera, this.viewport.canvas);

        // The gizmo helper must be in the scene to render. In r162
        // TransformControls extends Object3D; in r169+ you'd add
        // .getHelper() instead. We're on r162.
        this.editor.scene.add(this.tcontrols);

        this.tcontrols.setMode('translate');
        this.tcontrols.setSpace('world');
        this.tcontrols.setSize(0.9);

        // Disable on construction — we attach on first selection.
        this.tcontrols.enabled = false;
        this.tcontrols.visible = false;

        // Temp-Group for multi-select. Created on demand and kept around.
        this.tempGroup = new THREE.Group();
        this.tempGroup.name = 'transform-temp-group';
        this.editor.scene.add(this.tempGroup);

        // Captured at drag-start so commands have a prev to undo to.
        this._dragStart = null;        // { entries: [...], mode: 'translate'|'rotate' }

        // Multi-select detach bookkeeping. While the temp-group owns a
        // mesh, we remember its original parent to reattach on drag end /
        // selection change.
        this._reparented = [];          // [{ mesh, originalParent }]

        // ── Event wiring ────────────────────────────────────────────────
        this.tcontrols.addEventListener('dragging-changed', (e) => this._onDraggingChanged(e));
        this.tcontrols.addEventListener('mouseDown', () => this._onDragStart());
        this.tcontrols.addEventListener('mouseUp', () => this._onDragEnd());

        editor.signals.selectionChanged.add((items) => this._onSelectionChanged(items));
        editor.signals.walkModeChanged.add((enabled) => this._onWalkModeChanged(enabled));
        editor.signals.presetClearing.add(() => this.detach());

        // If a placement is removed (Ctrl-Z of an AddPlacementCommand, or
        // trash icon in the placement modal), restore parents IMMEDIATELY.
        // PlacementStore.unregister disposes meshes by walking wmoGroup —
        // if a mesh is still parked under our tempGroup, it would be
        // orphaned. Restoring parents first lets the cleanup find it.
        editor.signals.placementRemoved.add(() => this._restoreParents());
        editor.signals.placementsCleared.add(() => this._restoreParents());

        // Modifier-driven snap toggle: Shift = snap on, release = snap off.
        // We listen at document level so it works regardless of focus.
        document.addEventListener('keydown', (ev) => this._updateSnap(ev.shiftKey));
        document.addEventListener('keyup', (ev) => this._updateSnap(ev.shiftKey));

        // ── Drag collision viz ──────────────────────────────────────────
        // On drag-start: clone the selected WMO as a ghost, hide the
        // original, attach gizmo to the ghost. During drag, the ghost
        // shows full collision viz (same as placement mode). On drag-end:
        // read the ghost's final position, destroy it, unhide the original,
        // apply the new position via command.
        this._dragCtx = new PlacementContext();
        this._dragGhost = null;           // ghost clone Group
        this._dragSource = null;          // the real WMO being moved (invisible during drag)
        this._dragHidden = [];            // [{ obj, wasVisible }] — originals hidden during drag
        this._dragTicker = null;          // per-frame sync function (ghost follows source)
    }

    // ── Public API ──────────────────────────────────────────────────────────

    setMode(mode) {
        if (mode !== 'translate' && mode !== 'rotate') return;
        // If we're mid-drag, do nothing (TransformControls would be confused).
        if (this.tcontrols.dragging) return;
        this.tcontrols.setMode(mode);
        if (mode === 'rotate') {
            // Restrict to Y axis to match the rotY data model.
            this.tcontrols.showX = false;
            this.tcontrols.showY = true;
            this.tcontrols.showZ = false;
        } else {
            this.tcontrols.showX = true;
            this.tcontrols.showY = true;
            this.tcontrols.showZ = true;
        }
    }

    getMode() { return this.tcontrols.getMode ? this.tcontrols.getMode() : this.tcontrols.mode; }

    detach() {
        // Reparent any temp-grouped meshes back to their original parents
        // before detaching, so we don't strand them.
        this._restoreParents();
        this.tcontrols.detach();
        this.tcontrols.enabled = false;
        this.tcontrols.visible = false;
    }

    dispose() {
        this.detach();
        if (this.tcontrols.dispose) this.tcontrols.dispose();
        this.editor.scene.remove(this.tcontrols);
        this.editor.scene.remove(this.tempGroup);
    }

    // ── Selection → gizmo target ────────────────────────────────────────────

    _onSelectionChanged(items) {
        // Don't re-attach mid-drag — TransformControls' state is mid-flight
        // and reparenting would scramble the world transforms.
        if (this.tcontrols.dragging) return;

        // First, return any previously temp-grouped meshes to their owners.
        this._restoreParents();

        // Filter to transformable meshes. Phase 5: placed WMOs only.
        // Streamed InstancedMesh entries have instanceId != null AND were
        // tagged selectable:false back in Phase 4, but the filter is the
        // belt-and-suspenders enforcement here.
        const meshes = [];
        for (let i = 0; i < items.length; i++) {
            const it = items[i];
            if (it.instanceId != null) continue;       // skip instance entries
            if (!it.object) continue;
            // Placed WMOs are now Groups containing child meshes. Accept
            // both Group and Mesh as transformable targets.
            if (!it.object.isMesh && !it.object.isGroup) continue;
            if (it.placementId == null) continue;       // only placed WMOs persist
            meshes.push(it.object);
        }

        if (meshes.length === 0) {
            this.tcontrols.detach();
            this.tcontrols.enabled = false;
            this.tcontrols.visible = false;
            return;
        }

        // Walk mode hides the gizmo entirely (see _onWalkModeChanged).
        const walk = this.viewport.rig.walk && this.viewport.rig.walk.mode;
        if (walk) {
            this.tcontrols.detach();
            this.tcontrols.enabled = false;
            this.tcontrols.visible = false;
            return;
        }

        if (meshes.length === 1) {
            this.tcontrols.attach(meshes[0]);
        } else {
            // Multi-select: reparent into the temp group at the centroid.
            const centroid = this.editor.selection.centroid.clone();
            this.tempGroup.position.copy(centroid);
            this.tempGroup.rotation.set(0, 0, 0);
            this.tempGroup.scale.set(1, 1, 1);
            this.tempGroup.updateMatrixWorld(true);

            for (let i = 0; i < meshes.length; i++) {
                this._reparented.push({ mesh: meshes[i], originalParent: meshes[i].parent });
                this.tempGroup.attach(meshes[i]); // preserves world transform
            }
            this.tcontrols.attach(this.tempGroup);
        }

        this.tcontrols.enabled = true;
        this.tcontrols.visible = true;
    }

    _onWalkModeChanged(enabled) {
        if (enabled) {
            this._restoreParents();
            this.tcontrols.detach();
            this.tcontrols.enabled = false;
            this.tcontrols.visible = false;
        } else {
            // Re-trigger selection-driven attach when leaving walk mode so
            // the gizmo reappears for the still-selected items.
            this._onSelectionChanged(this.editor.selection.items.slice());
        }
    }

    _restoreParents() {
        if (this._reparented.length === 0) return;
        // Snapshot then clear, because Object3D.attach mutates parents.
        const list = this._reparented;
        this._reparented = [];

        const store = this.editor.placementStore;
        for (let i = 0; i < list.length; i++) {
            const r = list[i];
            if (!r.mesh) continue;
            if (r.mesh.parent !== this.tempGroup) continue;

            // If the underlying placement was removed while the object was
            // parked here (e.g. Ctrl-Z of an AddPlacementCommand fired
            // unregister(), which can't find objects outside wmoGroup), the
            // object is now orphan data. Dispose it directly instead of
            // reattaching to a stale parent.
            const pid = r.mesh.userData ? r.mesh.userData.placementId : null;
            const placementStillExists = pid != null && store && store.placedWmos.some((p) => p.id === pid);

            if (!placementStillExists) {
                this.tempGroup.remove(r.mesh);
                // r.mesh may be a Group (placed WMO) — use safeDispose to
                // recursively clean up all child geometry/material/textures.
                safeDispose(r.mesh);
                continue;
            }

            if (r.originalParent) {
                r.originalParent.attach(r.mesh); // preserves world transform
            }
        }
    }

    // ── OrbitControls toggle during drag ────────────────────────────────────

    _onDraggingChanged(e) {
        const orbit = this.viewport.rig.controls;
        if (orbit) orbit.enabled = !e.value;
    }

    // ── Drag start / end → history command ──────────────────────────────────
    //
    // We snapshot prev state at drag-start, snapshot next state at drag-end,
    // and push exactly one command per drag. The command's coalesce window
    // then merges back-to-back drags on the same set of objects.

    _onDragStart() {
        this._dragStart = {
            mode: this.getMode(),
            entries: this._captureMeshEntries('prev')
        };

        // ── Ghost clone for collision viz (single-select only) ───────
        // Build a ghost clone of the WMO, hide the real one, and sync
        // the ghost's transform to the real one each frame. The gizmo
        // stays attached to the real (invisible) WMO — TransformControls
        // moves it normally. The ghost just mirrors the position and
        // shows collision viz.
        const items = this.editor.selection.items;
        if (items.length === 1 && this._reparented.length === 0) {
            const obj = items[0].object;
            if (obj && (obj.isMesh || obj.isGroup)) {
                const dp = this.viewport.depthPrepass;
                const ghost = cloneAsGhost(obj, dp);
                if (ghost) {
                    this._dragGhost = ghost;
                    this._dragSource = obj;
                    this.editor.viewport.scene.add(ghost);

                    // Hide the original so only the ghost renders.
                    this._dragHidden = [{ obj: obj, wasVisible: obj.visible }];
                    obj.visible = false;

                    // Register ghost as depth-prepass consumer.
                    if (dp) dp.registerConsumer(ghost);

                    // Engage PlacementContext overlay.
                    this._dragCtx.engage(this.editor, ghost);
                    this.viewport._placementCtx = this._dragCtx;

                    // Add a per-frame ticker that syncs the ghost transform
                    // to the real (invisible) WMO being moved by TransformControls.
                    this._dragTicker = () => {
                        if (!this._dragGhost || !this._dragSource) return;
                        this._dragSource.updateMatrixWorld(true);
                        this._dragGhost.position.setFromMatrixPosition(this._dragSource.matrixWorld);
                        // Copy rotation from the source's world quaternion.
                        const q = new THREE.Quaternion();
                        this._dragSource.matrixWorld.decompose(new THREE.Vector3(), q, new THREE.Vector3());
                        this._dragGhost.rotation.setFromQuaternion(q);
                        this._dragGhost.scale.copy(this._dragSource.scale);
                    };
                    this.viewport.addTicker(this._dragTicker);
                }
            }
        }
    }

    _onDragEnd() {
        if (!this._dragStart) return;
        const startEntries = this._dragStart.entries;
        const mode = this._dragStart.mode;
        this._dragStart = null;

        // ── Ghost clone teardown ─────────────────────────────────────
        // The real WMO was moved by TransformControls (invisibly). The
        // ghost was just a visual mirror. Destroy the ghost, unhide the
        // real WMO, and continue with the normal command path.
        this._endDragViz();

        // For multi-select, the meshes are still parented to tempGroup at
        // this moment. We need to read their FINAL world transforms while
        // still attached (so TransformControls' updates are reflected),
        // BUT then we restore parents to commit those world transforms into
        // local space relative to wmoGroup.
        //
        // Order matters here:
        //   1. Read each mesh's current world position and (for rotate)
        //      world rotation while still under tempGroup.
        //   2. Call _restoreParents() — this calls originalParent.attach()
        //      which bakes the world transform into local. After this,
        //      mesh.position and mesh.rotation are in their final
        //      placement-data-friendly form.
        //   3. Re-read mesh.position / mesh.rotation now that they're
        //      independent (these are what the command stores as "next").
        //
        // For single-select the mesh isn't reparented so step 2 is a no-op.

        if (this._reparented.length > 0) this._restoreParents();

        // Now read final transforms from the meshes themselves.
        const nextEntries = this._captureMeshEntries('next');

        // Stitch prev + next by placementId. Discard any mesh that lost its
        // placementId (shouldn't happen, but defensive).
        const prevById = {};
        for (let i = 0; i < startEntries.length; i++) prevById[startEntries[i].placementId] = startEntries[i];

        const merged = [];
        for (let i = 0; i < nextEntries.length; i++) {
            const n = nextEntries[i];
            const p = prevById[n.placementId];
            if (!p) continue;
            merged.push({ mesh: n.mesh, placementId: n.placementId, prev: p, next: n });
        }
        if (merged.length === 0) {
            // Re-attach gizmo since selection didn't change. The mesh-set
            // might have been re-grouped during _restoreParents call.
            this._onSelectionChanged(this.editor.selection.items.slice());
            return;
        }

        // If nothing actually moved, skip pushing a command.
        const moved = merged.some((m) => {
            if (mode === 'translate') return !m.prev.pos.equals(m.next.pos);
            return !m.prev.pos.equals(m.next.pos) || Math.abs(normDeg(m.prev.rotY) - normDeg(m.next.rotY)) > 1e-4;
        });
        if (!moved) {
            this._onSelectionChanged(this.editor.selection.items.slice());
            return;
        }

        if (mode === 'translate') {
            const entries = merged.map((m) => ({
                mesh: m.mesh, placementId: m.placementId,
                prev: m.prev.pos, next: m.next.pos
            }));
            this.editor.history.execute(new SetPositionCommand(this.editor, entries));
        } else { // rotate
            const entries = merged.map((m) => ({
                mesh: m.mesh, placementId: m.placementId,
                prevPos: m.prev.pos, nextPos: m.next.pos,
                prevRotY: m.prev.rotY, nextRotY: m.next.rotY
            }));
            this.editor.history.execute(new SetRotationCommand(this.editor, entries));
        }

        // After commit, reattach the gizmo to reflect the current selection.
        // (For single-select, the gizmo is already attached; for multi-select
        // the temp-group teardown means we need to rebuild it.)
        this._onSelectionChanged(this.editor.selection.items.slice());
    }

    // ── Drag viz teardown ─────────────────────────────────────────────────

    _endDragViz() {
        // Disengage the PlacementContext overlay.
        if (this._dragCtx.active) {
            this._dragCtx.disengage();
            this.viewport._placementCtx = null;
        }

        // Remove the per-frame sync ticker.
        if (this._dragTicker) {
            const idx = this.viewport._tickers.indexOf(this._dragTicker);
            if (idx >= 0) this.viewport._tickers.splice(idx, 1);
            this._dragTicker = null;
        }

        // Destroy the ghost clone and unregister from depth prepass.
        if (this._dragGhost) {
            const dp = this.viewport.depthPrepass;
            if (dp) dp.unregisterConsumer(this._dragGhost);
            disposeGhostClone(this._dragGhost);
            this._dragGhost = null;
        }
        this._dragSource = null;

        // Unhide the original WMO(s).
        for (let i = 0; i < this._dragHidden.length; i++) {
            this._dragHidden[i].obj.visible = this._dragHidden[i].wasVisible;
        }
        this._dragHidden = [];
    }

    // ── Command builder (shared by ghost path and normal path) ──────────

    _buildAndPushCommand(mode, startEntries, nextEntries) {
        const prevById = {};
        for (let i = 0; i < startEntries.length; i++) prevById[startEntries[i].placementId] = startEntries[i];

        const merged = [];
        for (let i = 0; i < nextEntries.length; i++) {
            const n = nextEntries[i];
            const p = prevById[n.placementId];
            if (!p) continue;
            merged.push({ mesh: n.mesh, placementId: n.placementId, prev: p, next: n });
        }
        if (merged.length === 0) return;

        const moved = merged.some((m) => {
            if (mode === 'translate') return !m.prev.pos.equals(m.next.pos);
            return !m.prev.pos.equals(m.next.pos) || Math.abs(normDeg(m.prev.rotY) - normDeg(m.next.rotY)) > 1e-4;
        });
        if (!moved) return;

        if (mode === 'translate') {
            const entries = merged.map((m) => ({
                mesh: m.mesh, placementId: m.placementId,
                prev: m.prev.pos, next: m.next.pos
            }));
            this.editor.history.execute(new SetPositionCommand(this.editor, entries));
        } else {
            const entries = merged.map((m) => ({
                mesh: m.mesh, placementId: m.placementId,
                prevPos: m.prev.pos, nextPos: m.next.pos,
                prevRotY: m.prev.rotY, nextRotY: m.next.rotY
            }));
            this.editor.history.execute(new SetRotationCommand(this.editor, entries));
        }
    }

    // Capture per-mesh world position and Y-rotation in DEGREES. World
    // values are used here regardless of whether the mesh is currently
    // parented under wmoGroup or tempGroup — that way the snapshot is
    // invariant to reparenting.
    _captureMeshEntries(_label) {
        const items = this.editor.selection.items;
        const out = [];
        const worldPos = new THREE.Vector3();
        const worldQuat = new THREE.Quaternion();
        const worldScale = new THREE.Vector3();
        const euler = new THREE.Euler(0, 0, 0, 'YXZ');

        for (let i = 0; i < items.length; i++) {
            const it = items[i];
            if (it.instanceId != null) continue;
            if (!it.object) continue;
            if (!it.object.isMesh && !it.object.isGroup) continue;
            if (it.placementId == null) continue;

            it.object.updateMatrixWorld(true);
            it.object.matrixWorld.decompose(worldPos, worldQuat, worldScale);
            euler.setFromQuaternion(worldQuat, 'YXZ');

            out.push({
                mesh: it.object,
                placementId: it.placementId,
                pos: worldPos.clone(),
                rotY: normDeg(euler.y * 180 / Math.PI)
            });
        }
        return out;
    }

    // ── Snap (held-shift) ───────────────────────────────────────────────────

    _updateSnap(shift) {
        if (shift) {
            this.tcontrols.setTranslationSnap(TRANS_SNAP);
            this.tcontrols.setRotationSnap(THREE.MathUtils.degToRad(ROT_SNAP_DEG));
        } else {
            this.tcontrols.setTranslationSnap(null);
            this.tcontrols.setRotationSnap(null);
        }
    }
}
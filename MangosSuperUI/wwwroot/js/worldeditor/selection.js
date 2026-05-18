// selection.js — Phase 4: selection state + outline integration + SelectTool.
//
// Sections:
//   1. SelectionSet         — tuple-based selection store, dispatches selectionChanged
//   2. OutlineProxyManager  — keeps outlinePass.selectedObjects in sync (proxies for instances)
//   3. SelectTool           — left-click pick, shift-toggle, Esc clear
//
// Selection items are tuples:
//   { object: Object3D, instanceId: number|null, placementId: number|null }
//
//   - object       — the picked THREE.Mesh or InstancedMesh
//   - instanceId   — index into an InstancedMesh, or null for regular Mesh
//   - placementId  — local PlacementStore.placedWmos[*].id when the selection
//                    is a placed WMO; null otherwise.
//
// Phase 4 only makes placed WMOs (regular THREE.Mesh) selectable. Streamed
// InstancedMesh entries are tagged `selectable: false` deliberately — the
// swap-with-last in InstancePool.removeInstance would silently re-target a
// proxy mesh during unload. That story belongs to Phase 7 when doodads get
// the same generalization. The proxy code path is stubbed here so Phase 5/7
// can flip a flag rather than re-architect.

import * as THREE from 'three';
import { Tool, entityOf, isSelectable } from './core.js';

// ─────────────────────────────────────────────────────────────────────────────
// 1. SelectionSet
// ─────────────────────────────────────────────────────────────────────────────
//
// Identity test: an item is "the same" as another iff their (object.uuid,
// instanceId) match. placementId is derivative and not part of identity.

function itemKey(item) {
    const uuid = item.object ? item.object.uuid : '';
    return uuid + '#' + (item.instanceId == null ? '-' : item.instanceId);
}

export class SelectionSet {
    constructor(editor) {
        this.editor = editor;
        this.items = [];                  // [{ object, instanceId, placementId }]
        this.centroid = new THREE.Vector3(); // world-space avg of selected positions

        // React to events that invalidate live selections.
        editor.signals.placementRemoved.add((id) => this._dropByPlacementId(id));
        editor.signals.placementsCleared.add(() => this.clear());
        editor.signals.presetClearing.add(() => this.clear());
    }

    size() { return this.items.length; }
    isEmpty() { return this.items.length === 0; }

    has(item) {
        const k = itemKey(item);
        for (let i = 0; i < this.items.length; i++) {
            if (itemKey(this.items[i]) === k) return true;
        }
        return false;
    }

    add(item) {
        if (this.has(item)) return;
        this.items.push(item);
        this._dispatch();
    }

    remove(item) {
        const k = itemKey(item);
        const before = this.items.length;
        this.items = this.items.filter((x) => itemKey(x) !== k);
        if (this.items.length !== before) this._dispatch();
    }

    toggle(item) {
        if (this.has(item)) this.remove(item);
        else this.add(item);
    }

    replace(items) {
        this.items = items ? items.slice() : [];
        this._dispatch();
    }

    clear() {
        if (this.items.length === 0) return;
        this.items.length = 0;
        this._dispatch();
    }

    // Internal — drop any selection items whose placementId matches.
    // Wired to signals.placementRemoved so Ctrl-Z'ing a placement (or the
    // trash icon in the placement modal) doesn't leave a phantom outline.
    _dropByPlacementId(placementId) {
        if (placementId == null) return;
        const before = this.items.length;
        this.items = this.items.filter((x) => x.placementId !== placementId);
        if (this.items.length !== before) this._dispatch();
    }

    _dispatch() {
        this._recomputeCentroid();
        this.editor.signals.selectionChanged.dispatch(this.items.slice());
    }

    // Centroid in world space. Phase 4 doesn't use it; Phase 5's
    // TransformControls placement does. Computing it here keeps the API
    // stable across phases.
    _recomputeCentroid() {
        this.centroid.set(0, 0, 0);
        if (this.items.length === 0) return;
        const tmp = new THREE.Vector3();
        for (let i = 0; i < this.items.length; i++) {
            const it = this.items[i];
            if (it.instanceId == null) {
                // Regular Mesh — use world position.
                it.object.getWorldPosition(tmp);
                this.centroid.add(tmp);
            } else {
                // InstancedMesh entry — pull translation from the instance matrix.
                // Currently unreachable in Phase 4 (instances are not selectable)
                // but the code is here so Phase 5/7 doesn't need to revisit.
                const m = new THREE.Matrix4();
                it.object.getMatrixAt(it.instanceId, m);
                tmp.setFromMatrixPosition(m);
                it.object.localToWorld(tmp);
                this.centroid.add(tmp);
            }
        }
        this.centroid.multiplyScalar(1 / this.items.length);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. OutlineProxyManager
// ─────────────────────────────────────────────────────────────────────────────
//
// Subscribes to selectionChanged and keeps outlinePass.selectedObjects in
// sync. For regular Meshes the outline pass takes the mesh directly. For
// InstancedMesh entries we create one invisible proxy Mesh per selected
// instance, sharing geometry, with its world matrix copied from
// instancedMesh.getMatrixAt(instanceId) each frame.
//
// Phase 4: instance proxies are unreachable (streamed objects are
// selectable:false). The code path exists so Phase 5/7 doesn't need to
// rewrite this manager — just flip the tag and the rest works.

const PROXY_KEY = '__we_outline_proxy';

export class OutlineProxyManager {
    constructor(editor) {
        this.editor = editor;
        this.outlinePass = editor.viewport.outlinePass;

        // proxies: itemKey(item) → THREE.Mesh (or null if direct pass-through)
        this.proxies = new Map();

        // Hidden scene attachment for instance proxies. Outline pass needs
        // proxies to be in the rendered scene tree to compute their bbox.
        this.proxyGroup = new THREE.Group();
        this.proxyGroup.name = 'outline-proxies';
        editor.scene.add(this.proxyGroup);

        editor.signals.selectionChanged.add((items) => this._rebuild(items));
        editor.viewport.addTicker(() => this._tickInstanceProxies());
    }

    _rebuild(items) {
        const liveKeys = new Set();
        const next = [];

        for (let i = 0; i < items.length; i++) {
            const item = items[i];
            const k = itemKey(item);
            liveKeys.add(k);

            if (item.instanceId == null) {
                // For placed WMO Groups, OutlinePass needs the child Meshes
                // (it doesn't traverse Groups). Push all child meshes.
                const obj = item.object;
                if (obj.isGroup) {
                    obj.traverse((c) => { if (c.isMesh) next.push(c); });
                } else {
                    next.push(obj);
                }
            } else {
                let proxy = this.proxies.get(k);
                if (proxy === undefined) {
                    proxy = this._makeProxyForInstance(item.object, item.instanceId);
                    this.proxies.set(k, proxy);
                }
                if (proxy) next.push(proxy);
            }
        }

        // Dispose instance proxies that fell out of selection.
        for (const [k, proxy] of this.proxies) {
            if (liveKeys.has(k)) continue;
            this.proxies.delete(k);
            if (proxy && proxy[PROXY_KEY]) {
                this.proxyGroup.remove(proxy);
                if (proxy.material) proxy.material.dispose();
            }
        }

        this.outlinePass.selectedObjects = next;
    }

    _makeProxyForMesh(object) {
        // Pass the mesh directly. OutlinePass walks it; no proxy needed.
        return object;
    }

    _makeProxyForInstance(instancedMesh, instanceId) {
        // Stubbed for Phase 4 — kept correct so Phase 5/7 can enable it.
        const placeholder = new THREE.MeshBasicMaterial({ visible: false });
        const proxy = new THREE.Mesh(instancedMesh.geometry, placeholder);
        proxy.matrixAutoUpdate = false;
        proxy[PROXY_KEY] = true;
        proxy.userData.__we_source = { instancedMesh, instanceId };
        instancedMesh.getMatrixAt(instanceId, proxy.matrix);
        this.proxyGroup.add(proxy);
        return proxy;
    }

    _tickInstanceProxies() {
        // Refresh matrices in case the underlying InstancedMesh entry moved
        // (Phase 5 transform gizmos will drive this). No-op in Phase 4.
        for (const proxy of this.proxies.values()) {
            if (!proxy || !proxy[PROXY_KEY]) continue;
            const src = proxy.userData.__we_source;
            if (!src) continue;
            src.instancedMesh.getMatrixAt(src.instanceId, proxy.matrix);
        }
    }

    dispose() {
        for (const [, proxy] of this.proxies) {
            if (proxy && proxy[PROXY_KEY]) {
                this.proxyGroup.remove(proxy);
                if (proxy.material) proxy.material.dispose();
            }
        }
        this.proxies.clear();
        this.editor.scene.remove(this.proxyGroup);
        this.outlinePass.selectedObjects = [];
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. SelectTool
// ─────────────────────────────────────────────────────────────────────────────

export class SelectTool extends Tool {
    constructor(editor) {
        super(editor, 'select');
        this._raycaster = new THREE.Raycaster();
        this._tmpMouse = new THREE.Vector2();
    }

    activate() { this.editor.viewport.canvas.style.cursor = ''; }
    deactivate() { /* nothing — selection state survives tool switches */ }

    onPointerDown(ev, ctx) {
        if (ev.button !== 0) return false; // ignore middle/right

        // If the cursor is hovering a TransformControls gizmo handle, let
        // the gizmo own the drag — don't reinterpret the click as a pick.
        // TC sets `.axis` to a string ('X', 'XYZ', 'XY', etc.) during
        // hover; it's null when nothing is hovered.
        const gizmo = this.editor.transformGizmo;
        if (gizmo && gizmo.tcontrols && gizmo.tcontrols.enabled && gizmo.tcontrols.axis) {
            return false;
        }

        const hit = this._pick(ev, ctx);
        const selection = this.editor.selection;

        if (!hit) {
            // Click on empty space: clear unless shift-modified (shift keeps
            // the current selection so the user can shift-click to add later).
            if (!ev.shiftKey && !selection.isEmpty()) selection.clear();
            // Always return false on left-click so OrbitControls still
            // receives the event and left-drag orbit still works. Selection
            // happens on the down edge; orbit is driven by the subsequent
            // drag. The two don't conflict.
            return false;
        }

        const item = this._itemFromHit(hit);
        if (!item) return false;

        if (ev.shiftKey) selection.toggle(item);
        else selection.replace([item]);

        // Same reasoning — let OrbitControls see the event. Click-to-select
        // does not preclude drag-to-orbit.
        return false;
    }

    onKeyDown(ev) {
        // Only consume Esc when there's a selection to clear. If empty, let
        // input.js's modal-close cascade run on the same Esc press.
        if (ev.code === 'Escape' && !this.editor.selection.isEmpty()) {
            this.editor.selection.clear();
            return true;
        }
        // Phase 5: gizmo mode hotkeys. We use G/R (not W/E) to avoid
        // colliding with WASD/QE camera movement. The gizmo manager is
        // optional — it's installed by index.js; ignore the key if it's
        // not attached.
        if (this.editor.transformGizmo && !this.editor.selection.isEmpty()) {
            if (ev.code === 'KeyG') {
                this.editor.transformGizmo.setMode('translate');
                return true;
            }
            if (ev.code === 'KeyR') {
                this.editor.transformGizmo.setMode('rotate');
                return true;
            }
        }
        return false;
    }

    // ── picking ────────────────────────────────────────────────────────────

    _pick(ev, ctx) {
        const canvas = this.editor.viewport.canvas;
        const rect = canvas.getBoundingClientRect();
        this._tmpMouse.x = ((ev.clientX - rect.left) / rect.width) * 2 - 1;
        this._tmpMouse.y = -((ev.clientY - rect.top) / rect.height) * 2 + 1;

        this._raycaster.setFromCamera(this._tmpMouse, ctx.camera);

        // Raycast against the WMO group and the transform tempGroup.
        const targets = [];
        const wmoGroup = this.editor.objectStream && this.editor.objectStream.pool
            ? this.editor.objectStream.pool.wmoGroup
            : null;
        if (wmoGroup) targets.push(wmoGroup);
        if (this.editor.transformGizmo && this.editor.transformGizmo.tempGroup) {
            targets.push(this.editor.transformGizmo.tempGroup);
        }
        if (targets.length === 0) return null;

        const hits = this._raycaster.intersectObjects(targets, true);

        for (let i = 0; i < hits.length; i++) {
            const h = hits[i];
            // The raycast hits a child mesh inside a placement Group.
            // Walk up the parent chain to find the nearest ancestor with
            // an editorEntity tag — that's the selectable placement group.
            let obj = h.object;
            while (obj) {
                if (isSelectable(obj)) {
                    h.object = obj;
                    return h;
                }
                obj = obj.parent;
            }
        }
        return null;
    }

    _itemFromHit(hit) {
        const obj = hit.object;
        const ent = entityOf(obj);
        if (!ent) return null;

        const isInstanced = obj.isInstancedMesh === true;

        return {
            object: obj,
            instanceId: isInstanced ? hit.instanceId : null,
            placementId: isInstanced ? null : (obj.userData ? (obj.userData.placementId || null) : null)
        };
    }
}
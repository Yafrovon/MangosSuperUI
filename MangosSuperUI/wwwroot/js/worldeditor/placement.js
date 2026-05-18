// placement.js — custom WMO placements (user-authored).
//
// Sections:
//   1. Ghost helpers — async ghost build with generation guard
//   2. PlacementStore — placedWmos list + DB persistence + solid spawn + dedup
//   3. AddPlacementCommand / RemovePlacementCommand — undoable mutations
//   4. PlaceWmoTool — owns the ghost lifecycle and the click flow
//   5. PlacementModal — catalog browser + 3D preview + placed-list sidebar

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { Tool, Command, tagEntity } from './core.js';
import {
    makeWmoMaterial,
    safeDispose,
    maxAnisotropy
} from './render.js';
import {
    makeGhostMaterial,
    tagGhostRoot,
    PlacementContext
} from './collision.js';
import {
    getJSON, postJSON,
    regenerateServerData,
    restoreVanillaDefaults
} from './net.js';

// ─────────────────────────────────────────────────────────────────────────────
// 1. Ghost helpers
// ─────────────────────────────────────────────────────────────────────────────
//
// buildGhostGroup is async (Image.onload). If placement is exited before the
// callback fires, an orphan ghost would land in the scene. The fix is a
// generation counter on the tool: exitPlacementMode bumps it; the build
// callback aborts if the counter changed.

export function computeGhostBBox(wmoData) {
    const pos = wmoData.positions;
    const min = new THREE.Vector3(Infinity, Infinity, Infinity);
    const max = new THREE.Vector3(-Infinity, -Infinity, -Infinity);
    for (let i = 0; i < pos.length; i += 3) {
        if (pos[i] < min.x) min.x = pos[i];
        if (pos[i + 1] < min.y) min.y = pos[i + 1];
        if (pos[i + 2] < min.z) min.z = pos[i + 2];
        if (pos[i] > max.x) max.x = pos[i];
        if (pos[i + 1] > max.y) max.y = pos[i + 1];
        if (pos[i + 2] > max.z) max.z = pos[i + 2];
    }
    return { min, max };
}

export function buildGhostGroup(wmoData, editor, callback) {
    const group = new THREE.Group();
    const positions = new Float32Array(wmoData.positions);
    const normals = wmoData.normals ? new Float32Array(wmoData.normals) : null;
    const uvs = wmoData.uvs ? new Float32Array(wmoData.uvs) : null;
    const indices = new Uint32Array(wmoData.indices);
    let pending = wmoData.submeshes.length;

    if (pending === 0) { callback(group); return; }

    const depthPrepass = editor && editor.viewport ? editor.viewport.depthPrepass : null;

    wmoData.submeshes.forEach((sub) => {
        const subIdx = indices.slice(sub.indexStart, sub.indexStart + sub.indexCount);
        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        if (normals) geo.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
        if (uvs) geo.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));
        geo.setIndex(new THREE.BufferAttribute(subIdx, 1));

        function addMesh(mat) {
            group.add(new THREE.Mesh(geo, mat));
            pending--;
            if (pending <= 0) callback(group);
        }

        // If the Viewport hasn't built its DepthPrepass yet (shouldn't
        // happen in normal boot order but defensive), fall back to a plain
        // translucent material — no collision viz but a working ghost.
        if (!depthPrepass) {
            const fallback = new THREE.MeshStandardMaterial({
                color: 0x88bbff,
                side: sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
                transparent: true, opacity: 0.7, depthWrite: false,
                roughness: 0.6, metalness: 0.0
            });
            if (sub.textureBase64) {
                const img = new Image();
                img.onload = function () {
                    const tex = new THREE.Texture(img);
                    tex.colorSpace = THREE.SRGBColorSpace;
                    tex.needsUpdate = true;
                    tex.wrapS = THREE.RepeatWrapping;
                    tex.wrapT = THREE.RepeatWrapping;
                    fallback.map = tex;
                    fallback.needsUpdate = true;
                };
                img.src = sub.textureBase64;
            }
            addMesh(fallback);
            return;
        }

        if (sub.textureBase64) {
            const img = new Image();
            img.onload = function () {
                const tex = new THREE.Texture(img);
                tex.colorSpace = THREE.SRGBColorSpace;
                tex.needsUpdate = true;
                tex.wrapS = THREE.RepeatWrapping;
                tex.wrapT = THREE.RepeatWrapping;
                addMesh(makeGhostMaterial({
                    depthPrepass: depthPrepass,
                    map: tex,
                    doubleSided: sub.doubleSided
                }));
            };
            img.src = sub.textureBase64;
        } else {
            addMesh(makeGhostMaterial({
                depthPrepass: depthPrepass,
                map: null,
                baseColor: 0x6699cc,
                doubleSided: sub.doubleSided
            }));
        }
    });
}

export function destroyGhost(ghostGroup, scene) {
    if (!ghostGroup) return;
    scene.remove(ghostGroup);
    safeDispose(ghostGroup);
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. PlacementStore — placedWmos list + DB ops + solid mesh spawn + dedup
// ─────────────────────────────────────────────────────────────────────────────
//
// Replaces the old window._wmoPlacement scope bridge with a proper service.
// Owns:
//   - placedWmos: { id, dbId, path, name, x, y, z, rotY, scale, committed }
//   - customPlacementKeys: spatialKey → localId  (dedup against streaming)
//   - placementIdCounter: monotonic local id
// Emits signals: placementAdded / placementRemoved / placementUpdated /
// placementsCleared.

export class PlacementStore {
    constructor(editor, wmoGroup) {
        this.editor = editor;
        this.wmoGroup = wmoGroup; // InstancePool.wmoGroup
        this.placedWmos = [];
        this.placementIdCounter = 0;
        this.customPlacementKeys = {};
    }

    spatialKey(p) { return Math.round(p.x) + '|' + Math.round(p.z); }

    // Called by transform commands after a placement's x/z changes (drag-move).
    // Removes the old spatial-dedup key, inserts the new one, and purges any
    // vanilla streamed WMO that happens to sit at the new position.
    updateSpatialKey(placement, oldX, oldZ) {
        const oldKey = Math.round(oldX) + '|' + Math.round(oldZ);
        const newKey = this.spatialKey(placement);
        if (oldKey !== newKey) {
            delete this.customPlacementKeys[oldKey];
            this.customPlacementKeys[newKey] = placement.id;
            if (this.editor.objectStream) {
                this.editor.objectStream.purgeStreamedNear(placement.x, placement.z, 2);
            }
        }
    }

    register(placement) {
        if (placement.id == null) placement.id = ++this.placementIdCounter;
        else if (placement.id > this.placementIdCounter) this.placementIdCounter = placement.id;

        this.placedWmos.push(placement);
        this.customPlacementKeys[this.spatialKey(placement)] = placement.id;

        if (this.editor.objectStream) {
            this.editor.objectStream.purgeStreamedNear(placement.x, placement.z, 2);
        }

        this.editor.signals.placementAdded.dispatch(placement);
        return placement;
    }

    unregister(id) {
        const idx = this.placedWmos.findIndex((p) => p.id === id);
        if (idx < 0) return null;
        const removed = this.placedWmos[idx];
        removed.cancelled = true; // any in-flight texture loads will bail

        delete this.customPlacementKeys[this.spatialKey(removed)];

        if (this.editor.objectStream) {
            this.editor.objectStream.purgeStreamedNear(removed.x, removed.z, 2);
        }

        this.placedWmos.splice(idx, 1);

        // Placed WMOs are now Groups (parent) with child meshes inside.
        // Find the group by placementId and dispose the whole subtree.
        const cleanup = () => {
            const toRemove = [];
            for (let i = 0; i < this.wmoGroup.children.length; i++) {
                const child = this.wmoGroup.children[i];
                if (child.userData && child.userData.placementId === id) toRemove.push(child);
            }
            toRemove.forEach((g) => {
                this.wmoGroup.remove(g);
                safeDispose(g);
            });
            return toRemove.length;
        };
        cleanup();
        setTimeout(cleanup, 500); // catch any group populated by an in-flight texture load

        this.editor.signals.placementRemoved.dispatch(id);
        return removed;
    }

    // ----- Database -----
    saveToDb(placement) {
        if (!this.editor.currentPreset) return Promise.resolve(null);
        const body = {
            preset: this.editor.currentPreset,
            mapId: 0,
            wmoPath: placement.path,
            wmoName: placement.name,
            meshX: placement.x,
            meshY: placement.y,
            meshZ: placement.z,
            rotY: placement.rotY,
            scale: placement.scale
        };
        // If this placement already has a DB row, send the id so the
        // server UPDATEs the existing record instead of INSERTing a new one.
        if (placement.dbId) body.id = placement.dbId;
        return postJSON('/WorldEditor/SavePlacement', body).then((resp) => {
            if (resp.success && resp.id) {
                placement.dbId = resp.id;
                this.editor.signals.placementUpdated.dispatch(placement);
            }
            return resp;
        });
    }

    deleteFromDb(placement) {
        if (!placement.dbId) return Promise.resolve(null);
        return postJSON('/WorldEditor/DeletePlacement', { id: placement.dbId });
    }

    commitToWorld(localId) {
        const p = this.placedWmos.find((x) => x.id === localId);
        if (!p || !p.dbId) return Promise.reject(new Error('not saved'));
        return postJSON('/WorldEditor/CommitToWorld', { placementDbId: p.dbId })
            .then((resp) => {
                if (resp.success) {
                    p.committed = true;
                    this.editor.signals.placementUpdated.dispatch(p);
                }
                return resp;
            });
    }

    loadSaved() {
        if (!this.editor.currentPreset) return Promise.resolve([]);
        return getJSON('/WorldEditor/LoadPlacements?preset=' + encodeURIComponent(this.editor.currentPreset))
            .then((resp) => {
                if (!resp.success || !resp.placements) return [];
                resp.placements.forEach((row) => {
                    const placement = {
                        id: ++this.placementIdCounter,
                        dbId: row.id,
                        path: row.wmoPath,
                        name: row.wmoName || row.wmoPath.split('\\').pop().replace('.wmo', ''),
                        x: row.meshX,
                        y: row.meshY,
                        z: row.meshZ,
                        rotY: row.rotY,
                        scale: row.scale,
                        committed: !!row.committed
                    };
                    this.placedWmos.push(placement);
                    this.customPlacementKeys[this.spatialKey(placement)] = placement.id;
                    this.spawnSolid(placement);
                    this.editor.signals.placementAdded.dispatch(placement);
                });
                return resp.placements;
            });
    }

    // ----- Spawn the solid (non-ghost) copy after placement is confirmed -----
    //
    // Creates a parent THREE.Group per placement. All submeshes live inside
    // the group. The GROUP is the selectable/transformable entity — tagged
    // with editorEntity + placementId. Individual child meshes are untagged
    // so the raycaster walks up to the group via isSelectable traversal.
    spawnSolid(placement) {
        const group = new THREE.Group();
        group.name = 'placed-wmo:' + placement.id;
        group.position.set(placement.x, placement.y, placement.z);
        group.rotation.y = placement.rotY * Math.PI / 180;
        group.scale.setScalar(placement.scale);
        group.userData.placementId = placement.id;
        tagEntity(group, {
            type: 'wmo',
            id: 'wmo:' + placement.id,
            source: 'placement'
        });
        this.wmoGroup.add(group);

        getJSON('/WorldEditor/WmoModel?path=' + encodeURIComponent(placement.path)).then((data) => {
            if (!data.success || !data.positions) return;
            if (placement.cancelled) return;

            const positions = new Float32Array(data.positions);
            const normals = data.normals ? new Float32Array(data.normals) : null;
            const uvs = data.uvs ? new Float32Array(data.uvs) : null;
            const indices = new Uint32Array(data.indices);

            data.submeshes.forEach((sub) => {
                if (placement.cancelled) return;
                const subIdx = indices.slice(sub.indexStart, sub.indexStart + sub.indexCount);
                const geo = new THREE.BufferGeometry();
                geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
                if (normals) geo.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
                if (uvs) geo.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));
                geo.setIndex(new THREE.BufferAttribute(subIdx, 1));

                const matOpts = {
                    side: sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
                    transparent: sub.transparent || false,
                    alphaTest: sub.transparent ? 0.5 : 0
                };

                const addChild = (mat) => {
                    if (placement.cancelled) {
                        geo.dispose();
                        if (mat.map) mat.map.dispose();
                        mat.dispose();
                        return;
                    }
                    // Child meshes are LOCAL to the group — no position/rotation
                    // of their own. The group carries the world transform.
                    group.add(new THREE.Mesh(geo, mat));
                };

                if (sub.textureBase64) {
                    const img = new Image();
                    img.onload = () => {
                        const tex = new THREE.Texture(img);
                        tex.colorSpace = THREE.SRGBColorSpace;
                        tex.needsUpdate = true;
                        tex.wrapS = THREE.RepeatWrapping;
                        tex.wrapT = THREE.RepeatWrapping;
                        tex.anisotropy = maxAnisotropy();
                        matOpts.map = tex;
                        addChild(makeWmoMaterial(matOpts));
                    };
                    img.src = sub.textureBase64;
                } else {
                    addChild(makeWmoMaterial(matOpts));
                }
            });
        });
    }

    clearAll() {
        this.placedWmos.forEach((p) => {
            const toRemove = [];
            for (let i = 0; i < this.wmoGroup.children.length; i++) {
                const child = this.wmoGroup.children[i];
                if (child.userData && child.userData.placementId === p.id) toRemove.push(child);
            }
            toRemove.forEach((g) => {
                this.wmoGroup.remove(g);
                safeDispose(g);
            });
        });
        this.placedWmos = [];
        this.placementIdCounter = 0;
        this.customPlacementKeys = {};
        this.editor.signals.placementsCleared.dispatch();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Commands — undoable add/remove
// ─────────────────────────────────────────────────────────────────────────────
//
// AddPlacementCommand: save to DB, register in store, spawn solid.
// On undo: delete from DB, unregister (which disposes meshes).
// On redo: re-save (gets new dbId), re-register, re-spawn.

export class AddPlacementCommand extends Command {
    constructor(editor, placement) {
        super(editor);
        this.name = 'Place ' + (placement.name || 'WMO');
        this.snapshot = {
            path: placement.path,
            name: placement.name,
            x: placement.x,
            y: placement.y,
            z: placement.z,
            rotY: placement.rotY,
            scale: placement.scale
        };
        this._placementRef = placement; // direct ref valid until first undo
    }

    execute() {
        const store = this.editor.placementStore;
        if (!this._placementRef || !store.placedWmos.includes(this._placementRef)) {
            // Redo path — placement was removed by an earlier undo.
            this._placementRef = Object.assign({}, this.snapshot);
            store.register(this._placementRef);
            store.spawnSolid(this._placementRef);
        }
        store.saveToDb(this._placementRef);
    }

    undo() {
        const store = this.editor.placementStore;
        if (!this._placementRef) return;
        store.deleteFromDb(this._placementRef);
        store.unregister(this._placementRef.id);
        this._placementRef = null;
    }
}

export class RemovePlacementCommand extends Command {
    constructor(editor, placement) {
        super(editor);
        this.name = 'Remove ' + (placement.name || 'WMO');
        this.snapshot = {
            id: placement.id,
            dbId: placement.dbId,
            path: placement.path,
            name: placement.name,
            x: placement.x,
            y: placement.y,
            z: placement.z,
            rotY: placement.rotY,
            scale: placement.scale,
            committed: !!placement.committed
        };
    }

    execute() {
        const store = this.editor.placementStore;
        const live = store.placedWmos.find((p) => p.id === this.snapshot.id);
        if (live) {
            store.deleteFromDb(live);
            store.unregister(live.id);
        }
    }

    undo() {
        const store = this.editor.placementStore;
        const recreated = Object.assign({}, this.snapshot);
        store.register(recreated);
        store.spawnSolid(recreated);
        store.saveToDb(recreated);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. PlaceWmoTool — ghost lifecycle + click flow
// ─────────────────────────────────────────────────────────────────────────────
//
// Lifecycle:
//   activate     — show cancel button, set crosshair, status text, build ghost
//   deactivate   — bump generation, dispose helpers + ghost, restore cursor
//   onPointerMove — raycast terrain, snap floor + apply height offset
//   onPointerDown — left = confirm (push AddPlacementCommand);
//                   right = cancel (setActive 'select')
//   onKeyDown    — Q/E rotate; Esc cancel
//   onWheel      — adjust height offset (does NOT scroll page)

export class PlaceWmoTool extends Tool {
    constructor(editor) {
        super(editor, 'place-wmo');

        this.selectedWmoPath = null;
        this.selectedWmoData = null;

        this.ghost = null;
        this.ghostBBox = null;
        this.ghostRotY = 0;
        this.ghostScale = 1.0;
        this.ghostHeightOffset = 0;
        this.ghostTerrainY = 0;
        this.ghostGeneration = 0;

        this._heightLabel = null;
        this._cancelBtn = null;

        this._raycaster = new THREE.Raycaster();
        this._tmpMouse = new THREE.Vector2();

        // Wireframe X-ray context: makes nearby terrain/trees/buildings
        // see-through so the ghost is the only solid-looking object.
        this._placementCtx = new PlacementContext();
    }

    setTarget(path, data) {
        this.selectedWmoPath = path;
        this.selectedWmoData = data;
    }

    activate() {
        if (!this.selectedWmoPath || !this.selectedWmoData) {
            console.warn('PlaceWmoTool: no target set; activation aborted');
            return;
        }
        this.ghostRotY = 0;
        this.ghostScale = 1.0;
        this.ghostHeightOffset = 0;

        this._setStatus('Click to place \u2022 Q/E rotate \u2022 Scroll offset height \u2022 Right-click or Esc cancel \u2022 Floor auto-snaps to terrain');
        this._setCursor('crosshair');
        this._showCancelBtn(true);

        this.ghostBBox = computeGhostBBox(this.selectedWmoData);
        this._createHelpers();
        this._spawnGhost();
    }

    deactivate() {
        this.ghostGeneration++;        // invalidate in-flight async builds
        this._disposeHelpers();
        this.ghostBBox = null;
        // Remove the collision overlay pass from the composer and stop
        // rendering ghost depth. Must happen BEFORE ghost disposal.
        this._placementCtx.disengage();
        if (this.editor.viewport) this.editor.viewport._placementCtx = null;
        if (this.ghost && this.editor.viewport.depthPrepass) {
            this.editor.viewport.depthPrepass.unregisterConsumer(this.ghost);
        }
        destroyGhost(this.ghost, this.editor.viewport.scene);
        this.ghost = null;
        this._setCursor('');
        this._setStatus('');
        this._showCancelBtn(false);
    }

    onPointerMove(ev, ctx) {
        if (!this.ghost) return false;
        const canvas = this.editor.viewport.canvas;
        const camera = this.editor.viewport.rig.camera;

        const rect = canvas.getBoundingClientRect();
        this._tmpMouse.set(
            ((ev.clientX - rect.left) / rect.width) * 2 - 1,
            -((ev.clientY - rect.top) / rect.height) * 2 + 1
        );
        this._raycaster.setFromCamera(this._tmpMouse, camera);

        const terrainMeshes = this.editor.tileGrid.terrainMeshes();
        const hits = this._raycaster.intersectObjects(terrainMeshes);
        if (hits.length > 0) {
            const terrainY = hits[0].point.y;
            this.ghostTerrainY = terrainY;
            const floorOffset = this.ghostBBox ? -this.ghostBBox.min.y : 0;
            this.ghost.position.set(
                hits[0].point.x,
                terrainY + floorOffset + this.ghostHeightOffset,
                hits[0].point.z
            );
            this.ghost.visible = true;
        } else {
            this.ghost.visible = false;
        }
        return true;
    }

    onPointerDown(ev, ctx) {
        if (ev.button === 2) {
            this.editor.tools.setActive('select');
            return true;
        }
        if (ev.button === 0 && this.ghost && this.ghost.visible) {
            this._confirm();
            return true;
        }
        return false;
    }

    onKeyDown(ev) {
        if (!this.ghost) {
            if (ev.code === 'Escape') { this.editor.tools.setActive('select'); return true; }
            return false;
        }
        const rotStep = ev.shiftKey ? 45 : 15;
        if (ev.code === 'KeyQ') {
            this.ghostRotY = (this.ghostRotY - rotStep + 360) % 360;
            this.ghost.rotation.y = this.ghostRotY * Math.PI / 180;
            ev.preventDefault();
            return true;
        }
        if (ev.code === 'KeyE') {
            this.ghostRotY = (this.ghostRotY + rotStep) % 360;
            this.ghost.rotation.y = this.ghostRotY * Math.PI / 180;
            ev.preventDefault();
            return true;
        }
        if (ev.code === 'Escape') { this.editor.tools.setActive('select'); return true; }
        return false;
    }

    onWheel(ev, ctx) {
        if (!this.ghost) return false;
        ev.preventDefault();
        ev.stopPropagation();
        const dy = ev.deltaY > 0 ? -1 : 1;
        this.ghostHeightOffset += dy;
        this.ghost.position.y += dy;
        return true;
    }

    onContextMenu(ev) { return true; }

    updateHelpers() {
        if (!this.ghost || !this.ghost.visible || !this.ghostBBox || !this._heightLabel) return;
        const camera = this.editor.viewport.rig.camera;
        const canvas = this.editor.viewport.canvas;

        const offsetText = this.ghostHeightOffset === 0
            ? '\u2022 Floor on terrain'
            : (this.ghostHeightOffset > 0 ? '\u25b2 +' : '\u25bc ') + this.ghostHeightOffset.toFixed(1) + ' yd';
        let label = offsetText;
        if (this.ghostRotY !== 0) label += '  \u21bb ' + this.ghostRotY + '\u00b0';
        this._heightLabel.textContent = label;
        this._heightLabel.style.display = 'block';

        const sp = this.ghost.position.clone().project(camera);
        const rect = canvas.getBoundingClientRect();
        const sx = (sp.x * 0.5 + 0.5) * rect.width;
        const sy = (-sp.y * 0.5 + 0.5) * rect.height;
        this._heightLabel.style.left = (sx + 20) + 'px';
        this._heightLabel.style.top = (sy - 10) + 'px';
    }

    _confirm() {
        if (!this.ghost || !this.selectedWmoPath) return;

        const store = this.editor.placementStore;
        const wmoGroup = this.editor.objectStream.pool.wmoGroup;

        const placement = {
            id: null,
            dbId: null,
            path: this.selectedWmoPath,
            name: this.selectedWmoPath.split('\\').pop().replace('.wmo', ''),
            x: this.ghost.position.x,
            y: this.ghost.position.y - wmoGroup.position.y,
            z: this.ghost.position.z,
            rotY: this.ghostRotY,
            scale: this.ghostScale
        };

        // Register first so the command's `_placementRef` points at the live
        // placement. Then push the command (which saves to DB).
        store.register(placement);
        store.spawnSolid(placement);
        this.editor.history.execute(new AddPlacementCommand(this.editor, placement));

        this._setStatus('Placed ' + placement.name + '. Move to place another, or right-click / Esc to stop.');

        // Tear down current ghost; build fresh for next placement.
        this._disposeHelpers();
        if (this.ghost && this.editor.viewport.depthPrepass) {
            this.editor.viewport.depthPrepass.unregisterConsumer(this.ghost);
        }
        destroyGhost(this.ghost, this.editor.viewport.scene);
        this.ghost = null;
        this.ghostBBox = computeGhostBBox(this.selectedWmoData);
        this._createHelpers();
        this._spawnGhost();
    }

    _spawnGhost() {
        const gen = this.ghostGeneration;
        buildGhostGroup(this.selectedWmoData, this.editor, (g) => {
            if (gen !== this.ghostGeneration) {
                safeDispose(g);
                return;
            }
            this.ghost = g;
            this.ghost.scale.setScalar(this.ghostScale);
            this.ghost.rotation.y = this.ghostRotY * Math.PI / 180;
            this.ghost.visible = false; // shown on first pointermove
            tagGhostRoot(this.ghost);
            this.editor.viewport.scene.add(this.ghost);
            // Register the ghost as a depth-prepass consumer so the prepass
            // starts running and the ghost shader can see scene depth.
            // Unregistration happens in deactivate / mid-flow teardown.
            if (this.editor.viewport.depthPrepass) {
                this.editor.viewport.depthPrepass.registerConsumer(this.ghost);
            }
            // Engage the collision overlay so scene-into-ghost intrusions
            // are highlighted. Pass the ghost reference for ghost-depth rendering.
            if (!this._placementCtx.active) {
                this._placementCtx.engage(this.editor, this.ghost);
                // Expose on the viewport so the animate loop can call runIfNeeded.
                this.editor.viewport._placementCtx = this._placementCtx;
            } else {
                // Mid-flow rebuild (after _confirm): context is already active,
                // just update the ghost ref so the ghost depth re-renders correctly.
                this._placementCtx.setGhost(this.ghost);
            }
        });
    }

    _createHelpers() {
        if (this._heightLabel) return;
        this._heightLabel = document.createElement('div');
        this._heightLabel.style.cssText = 'position:absolute;pointer-events:none;z-index:15;' +
            'font-size:13px;font-weight:bold;font-family:monospace;color:#ffaa66;' +
            'text-shadow:0 1px 4px rgba(0,0,0,0.9);' +
            'background:rgba(0,0,0,0.65);padding:2px 8px;border-radius:4px;' +
            'white-space:nowrap;display:none;border:1px solid rgba(255,255,255,0.15);';
        this.editor.viewport.canvas.parentElement.appendChild(this._heightLabel);
    }

    _disposeHelpers() {
        if (this._heightLabel) {
            this._heightLabel.parentElement && this._heightLabel.parentElement.removeChild(this._heightLabel);
            this._heightLabel = null;
        }
    }

    _ensureCancelBtn() {
        if (this._cancelBtn) return;
        const btn = document.createElement('button');
        btn.innerHTML = '<i class="fa-solid fa-xmark"></i> Cancel Placement';
        btn.style.cssText = 'display:none;position:absolute;top:12px;right:16px;z-index:20;' +
            'background:rgba(220,53,69,0.85);color:#fff;border:none;border-radius:6px;' +
            'padding:6px 14px;font-size:12px;cursor:pointer;font-family:system-ui,sans-serif;' +
            'backdrop-filter:blur(4px);box-shadow:0 2px 8px rgba(0,0,0,0.3);';
        btn.addEventListener('click', (e) => {
            e.stopPropagation();
            this.editor.tools.setActive('select');
        });
        this.editor.viewport.canvas.parentElement.appendChild(btn);
        this._cancelBtn = btn;
    }

    _showCancelBtn(show) {
        this._ensureCancelBtn();
        this._cancelBtn.style.display = show ? 'block' : 'none';
    }

    _setStatus(msg) {
        const el = document.getElementById('weStatus');
        if (el) el.textContent = msg;
    }

    _setCursor(c) {
        this.editor.viewport.canvas.style.cursor = c;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. PlacementModal — catalog browser + 3D preview + placed-list sidebar
// ─────────────────────────────────────────────────────────────────────────────
//
// Builds the toolbar button + the modal panel. Wires up:
//   - Catalog tree fetch + tree rendering + search
//   - 3D preview renderer (its own scene/camera/renderer)
//   - "Place on Terrain" → set target on PlaceWmoTool, switch active tool
//   - The list of placed WMOs (rendered into the modal sidebar)
//   - Buttons for: Download MPQ, Regen Server Data, Restore Defaults

export class PlacementModal {
    constructor(editor) {
        this.editor = editor;
        this.canvas = editor.viewport.canvas;

        this._catalogLoaded = false;
        this._catalogData = [];
        this._isOpen = false;

        this._selectedPath = null;
        this._selectedData = null;

        // Preview state (lazy init on first modal open)
        this._previewInited = false;
        this._previewRenderer = null;
        this._previewScene = null;
        this._previewCamera = null;
        this._previewControls = null;
        this._previewGroup = null;
        this._previewAnimId = null;

        this._build();

        editor.signals.placementAdded.add(() => this._updatePlacedList());
        editor.signals.placementRemoved.add(() => this._updatePlacedList());
        editor.signals.placementUpdated.add(() => this._updatePlacedList());
        editor.signals.placementsCleared.add(() => this._updatePlacedList());
    }

    isOpen() { return this._isOpen; }
    closeIfOpen() { if (this._isOpen) this._hide(); }

    _build() {
        const toolbar = document.getElementById('weLoadBtn');
        if (!toolbar || !toolbar.parentElement) return;
        const container = toolbar.parentElement;

        this._backdrop = document.createElement('div');
        this._backdrop.style.cssText = 'display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.5);z-index:999;';
        this.canvas.parentElement.appendChild(this._backdrop);

        this._modal = document.createElement('div');
        this._modal.style.cssText = 'display:none;position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);' +
            'width:900px;max-width:92vw;max-height:85vh;background:#1e2530;color:#ccc;border-radius:10px;' +
            'border:1px solid rgba(255,255,255,0.1);padding:20px;z-index:1000;overflow:hidden;' +
            'flex-direction:column;font-family:-apple-system,BlinkMacSystemFont,sans-serif;';
        this.canvas.parentElement.appendChild(this._modal);

        this._backdrop.addEventListener('click', () => this._hide());

        const placeBtn = document.createElement('button');
        placeBtn.innerHTML = '<i class="fa-solid fa-building"></i>';
        placeBtn.className = 'we-control we-btn';
        placeBtn.title = 'WMO Placement Tool';
        placeBtn.addEventListener('click', () => { placeBtn.blur(); this._show(); });
        container.appendChild(placeBtn);

        this._modal.innerHTML =
            '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;">' +
            '<span style="font-size:16px;font-weight:600;color:#fff;"><i class="fa-solid fa-building"></i> WMO Placement</span>' +
            '<span id="wmoPlaceClose" style="cursor:pointer;font-size:20px;color:#888;padding:4px 8px;">&times;</span>' +
            '</div>' +
            '<div style="display:flex;gap:12px;flex:1;overflow:hidden;min-height:0;">' +
            '<div style="flex:1;display:flex;flex-direction:column;min-width:0;">' +
            '<input type="text" id="wmoCatSearch" placeholder="Search WMOs..." ' +
            'style="width:100%;padding:6px 10px;margin-bottom:8px;background:#141a22;color:#ccc;' +
            'border:1px solid rgba(255,255,255,0.12);border-radius:6px;font-size:12px;outline:none;">' +
            '<div id="wmoCatTree" style="flex:1;overflow-y:auto;font-size:12px;line-height:1.8;"></div>' +
            '</div>' +
            '<div style="width:280px;flex-shrink:0;display:flex;flex-direction:column;gap:8px;">' +
            '<div style="position:relative;background:#0a0e14;border-radius:8px;overflow:hidden;height:200px;border:1px solid rgba(255,255,255,0.06);">' +
            '<canvas id="wmoPreviewCanvas" style="width:100%;height:100%;display:block;"></canvas>' +
            '<div id="wmoPreviewOverlay" style="position:absolute;top:0;left:0;right:0;bottom:0;display:flex;align-items:center;justify-content:center;pointer-events:none;">' +
            '<span style="color:#444;font-size:12px;">Select a WMO</span>' +
            '</div>' +
            '</div>' +
            '<div id="wmoPreviewInfo" style="font-size:11px;color:#888;padding:0 2px;"></div>' +
            '<div id="wmoPlacementControls" style="display:none;">' +
            '<button id="wmoPlaceBtn" class="btn btn-sm btn-primary" style="width:100%;margin-bottom:4px;">' +
            '<i class="fa-solid fa-crosshairs"></i> Place on Terrain' +
            '</button>' +
            '<div style="font-size:10px;color:#666;text-align:center;line-height:1.5;">' +
            'Click to place &bull; <b>Q/E</b> rotate &bull; <b>Scroll</b> height &bull; <b>Right-click</b> or <b>Esc</b> cancel' +
            '</div>' +
            '</div>' +
            '<div style="flex:1;overflow-y:auto;min-height:0;">' +
            '<div style="font-size:11px;color:#888;margin-bottom:4px;">Placed WMOs</div>' +
            '<div id="wmoPlacedList" style="font-size:11px;"></div>' +
            '</div>' +
            '<button id="wmoDownloadMpq" class="btn btn-sm btn-outline-info" style="width:100%;font-size:11px;display:none;">' +
            '<i class="fa-solid fa-download"></i> Download patch-Z.MPQ for Client' +
            '</button>' +
            '<button id="wmoRegenServerData" class="btn btn-sm btn-outline-warning" style="width:100%;font-size:11px;display:none;margin-top:4px;">' +
            '<i class="fa-solid fa-server"></i> Regenerate Server Data (Collision/LoS/Pathing)' +
            '</button>' +
            '<div id="wmoRegenProgress" style="display:none;margin-top:4px;font-size:10px;max-height:120px;overflow-y:auto;background:rgba(0,0,0,0.3);border-radius:4px;padding:6px;"></div>' +
            '<button id="wmoRestoreDefaults" class="btn btn-sm btn-outline-danger" style="width:100%;font-size:11px;display:none;margin-top:8px;">' +
            '<i class="fa-solid fa-rotate-left"></i> Restore Vanilla Defaults' +
            '</button>' +
            '</div>' +
            '</div>';

        this._modal.querySelector('#wmoPlaceClose').addEventListener('click', () => this._hide());
        this._modal.querySelector('#wmoDownloadMpq').addEventListener('click', () => {
            window.location.href = '/WorldEditor/DownloadPatchMpq';
        });
        this._modal.querySelector('#wmoRegenServerData').addEventListener('click', () => {
            regenerateServerData(this.editor, this._modal);
        });
        this._modal.querySelector('#wmoRestoreDefaults').addEventListener('click', () => {
            restoreVanillaDefaults(this.editor, this._modal);
        });
        this._modal.querySelector('#wmoPlaceBtn').addEventListener('click', () => {
            if (!this._selectedPath || !this._selectedData) return;
            const tool = this.editor.tools.tools['place-wmo'];
            tool.setTarget(this._selectedPath, this._selectedData);
            this._hide();
            this.editor.tools.setActive('place-wmo');
        });

        this._previewCanvas = this._modal.querySelector('#wmoPreviewCanvas');
        this._previewOverlay = this._modal.querySelector('#wmoPreviewOverlay');

        const searchEl = this._modal.querySelector('#wmoCatSearch');
        let searchTimeout = null;
        searchEl.addEventListener('input', () => {
            clearTimeout(searchTimeout);
            const q = searchEl.value.trim().toLowerCase();
            searchTimeout = setTimeout(() => {
                if (!q) { this._renderTree(this._catalogData); return; }
                const filtered = this._catalogData.filter((e) =>
                    e.name.toLowerCase().indexOf(q) >= 0 ||
                    e.path.toLowerCase().indexOf(q) >= 0 ||
                    e.category.toLowerCase().indexOf(q) >= 0 ||
                    (e.subcategory && e.subcategory.toLowerCase().indexOf(q) >= 0)
                );
                this._renderTree(filtered);
                this._modal.querySelectorAll('.wmo-cat-body').forEach((b) => b.style.display = 'block');
                this._modal.querySelectorAll('.wmo-sub-body').forEach((b) => b.style.display = 'block');
                this._modal.querySelectorAll('.wmo-cat-header i, .wmo-sub-header i').forEach((i) => i.style.transform = 'rotate(90deg)');
            }, 200);
        });
    }

    _show() {
        this._backdrop.style.display = 'block';
        this._modal.style.display = 'flex';
        this._isOpen = true;
        if (!this._catalogLoaded) this._loadCatalog();
        this._startPreviewLoop();
    }

    _hide() {
        this._backdrop.style.display = 'none';
        this._modal.style.display = 'none';
        this._isOpen = false;
        this._stopPreviewLoop();
    }

    _loadCatalog() {
        const treeEl = this._modal.querySelector('#wmoCatTree');
        treeEl.innerHTML = '<span style="color:#888;">Loading WMO catalog...</span>';

        getJSON('/WorldEditor/WmoCatalog').then((resp) => {
            if (!resp.success) {
                treeEl.innerHTML = '<span style="color:#f66;">Failed: ' + (resp.error || 'unknown') + '</span>';
                return;
            }
            this._catalogData = resp.entries;
            this._catalogLoaded = true;
            this._renderTree(this._catalogData);
        });
    }

    _renderTree(entries) {
        const treeEl = this._modal.querySelector('#wmoCatTree');
        const grouped = {};
        entries.forEach((e) => {
            const cat = e.category || 'Other';
            const sub = e.subcategory || '_root';
            (grouped[cat] = grouped[cat] || {})[sub] = grouped[cat][sub] || [];
            grouped[cat][sub].push(e);
        });

        let html = '';
        Object.keys(grouped).sort().forEach((cat) => {
            const subs = grouped[cat];
            let totalInCat = 0;
            Object.values(subs).forEach((arr) => totalInCat += arr.length);

            html += '<div class="wmo-cat" style="margin-bottom:2px;">';
            html += '<div class="wmo-cat-header" style="cursor:pointer;padding:3px 6px;background:rgba(255,255,255,0.04);border-radius:4px;font-weight:600;color:#ddd;">';
            html += '<i class="fa-solid fa-chevron-right" style="font-size:9px;margin-right:6px;transition:transform 0.15s;"></i>';
            html += cat + ' <span style="color:#666;font-weight:400;">(' + totalInCat + ')</span></div>';
            html += '<div class="wmo-cat-body" style="display:none;padding-left:16px;">';

            Object.keys(subs).sort().forEach((sub) => {
                const items = subs[sub];
                if (sub !== '_root' && sub !== '') {
                    html += '<div class="wmo-sub" style="margin-top:2px;">';
                    html += '<div class="wmo-sub-header" style="cursor:pointer;padding:2px 4px;color:#aaa;">';
                    html += '<i class="fa-solid fa-chevron-right" style="font-size:8px;margin-right:4px;transition:transform 0.15s;"></i>';
                    html += sub + ' <span style="color:#555;">(' + items.length + ')</span></div>';
                    html += '<div class="wmo-sub-body" style="display:none;padding-left:12px;">';
                }
                items.forEach((e) => {
                    html += '<div class="wmo-item" style="cursor:pointer;padding:2px 6px;border-radius:3px;white-space:nowrap;overflow:hidden;text-overflow:ellipsis;" data-path="' + e.path.replace(/"/g, '&quot;') + '" title="' + e.path.replace(/"/g, '&quot;') + '">';
                    html += '<i class="fa-solid fa-cube" style="color:#555;margin-right:4px;font-size:10px;"></i>' + e.name;
                    html += '</div>';
                });
                if (sub !== '_root' && sub !== '') html += '</div></div>';
            });
            html += '</div></div>';
        });

        treeEl.innerHTML = html;

        treeEl.querySelectorAll('.wmo-cat-header').forEach((el) => {
            el.addEventListener('click', () => {
                const body = el.nextElementSibling;
                const icon = el.querySelector('i');
                const show = body.style.display === 'none';
                body.style.display = show ? 'block' : 'none';
                icon.style.transform = show ? 'rotate(90deg)' : '';
            });
        });
        treeEl.querySelectorAll('.wmo-sub-header').forEach((el) => {
            el.addEventListener('click', (e) => {
                e.stopPropagation();
                const body = el.nextElementSibling;
                const icon = el.querySelector('i');
                const show = body.style.display === 'none';
                body.style.display = show ? 'block' : 'none';
                icon.style.transform = show ? 'rotate(90deg)' : '';
            });
        });
        treeEl.querySelectorAll('.wmo-item').forEach((el) => {
            el.addEventListener('click', () => {
                treeEl.querySelectorAll('.wmo-item').forEach((x) => {
                    x.style.background = '';
                    x.style.color = '';
                });
                el.style.background = 'rgba(74,144,217,0.2)';
                el.style.color = '#fff';
                this._selectWmo(el.getAttribute('data-path'));
            });
        });
    }

    _selectWmo(path) {
        this._selectedPath = path;
        const infoEl = this._modal.querySelector('#wmoPreviewInfo');
        const ctrls = this._modal.querySelector('#wmoPlacementControls');

        infoEl.innerHTML = '<span style="color:#888;">Loading...</span>';
        ctrls.style.display = 'none';

        this._loadPreview3D(path);

        getJSON('/WorldEditor/WmoPreview?path=' + encodeURIComponent(path)).then((resp) => {
            if (!resp.success) {
                infoEl.innerHTML = '<span style="color:#f66;">Failed</span>';
                return;
            }
            infoEl.innerHTML =
                '<span style="color:#fff;font-weight:600;">' + resp.name + '</span> &mdash; ' +
                resp.groups + ' groups, ' + resp.materials + ' mats &mdash; ' +
                '<span style="color:#aaa;">' + resp.sizeX + '×' + resp.sizeY + '×' + resp.sizeZ + ' yd</span>';
            ctrls.style.display = 'block';
        });

        this._selectedData = null;
        getJSON('/WorldEditor/WmoModel?path=' + encodeURIComponent(path)).then((data) => {
            if (data.success && data.positions) this._selectedData = data;
        });
    }

    _initPreview() {
        if (this._previewInited) return;
        this._previewInited = true;

        this._previewRenderer = new THREE.WebGLRenderer({ canvas: this._previewCanvas, antialias: true, alpha: true });
        this._previewRenderer.setPixelRatio(window.devicePixelRatio);
        this._previewRenderer.setClearColor(0x0a0e14);
        this._previewRenderer.toneMapping = THREE.LinearToneMapping;
        this._previewRenderer.toneMappingExposure = 1.1;
        this._previewRenderer.outputColorSpace = THREE.SRGBColorSpace;

        this._previewScene = new THREE.Scene();
        this._previewCamera = new THREE.PerspectiveCamera(50, 1, 0.1, 5000);
        this._previewControls = new OrbitControls(this._previewCamera, this._previewCanvas);
        this._previewControls.enableDamping = true;
        this._previewControls.dampingFactor = 0.12;
        this._previewControls.autoRotate = true;
        this._previewControls.autoRotateSpeed = 1.5;

        // r162 physical lights tuning for preview
        this._previewScene.add(new THREE.AmbientLight(0xffffff, 1.5));
        const pvSun = new THREE.DirectionalLight(0xffeedd, 2.8);
        pvSun.position.set(50, 80, 30);
        this._previewScene.add(pvSun);
        const pvFill = new THREE.DirectionalLight(0x8899bb, 0.9);
        pvFill.position.set(-30, 20, -50);
        this._previewScene.add(pvFill);

        this._previewGroup = new THREE.Group();
        this._previewScene.add(this._previewGroup);
    }

    _startPreviewLoop() {
        this._initPreview();
        if (this._previewAnimId) return;
        const tick = () => {
            this._previewAnimId = requestAnimationFrame(tick);
            this._previewControls.update();
            const rect = this._previewCanvas.getBoundingClientRect();
            const w = Math.round(rect.width * window.devicePixelRatio);
            const h = Math.round(rect.height * window.devicePixelRatio);
            if (this._previewCanvas.width !== w || this._previewCanvas.height !== h) {
                this._previewRenderer.setSize(rect.width, rect.height, false);
                this._previewCamera.aspect = rect.width / rect.height;
                this._previewCamera.updateProjectionMatrix();
            }
            this._previewRenderer.render(this._previewScene, this._previewCamera);
        };
        tick();
    }

    _stopPreviewLoop() {
        if (this._previewAnimId) {
            cancelAnimationFrame(this._previewAnimId);
            this._previewAnimId = null;
        }
    }

    _clearPreview() {
        if (!this._previewGroup) return;
        while (this._previewGroup.children.length > 0) {
            const c = this._previewGroup.children[0];
            this._previewGroup.remove(c);
            if (c.geometry) c.geometry.dispose();
            if (c.material) {
                if (c.material.map) c.material.map.dispose();
                c.material.dispose();
            }
        }
    }

    _loadPreview3D(path) {
        this._initPreview();
        this._clearPreview();
        this._previewOverlay.innerHTML = '<span style="color:#888;font-size:11px;">Loading 3D model...</span>';
        this._previewOverlay.style.display = 'flex';

        getJSON('/WorldEditor/WmoModel?path=' + encodeURIComponent(path)).then((data) => {
            if (!data.success || !data.positions) {
                this._previewOverlay.innerHTML = '<span style="color:#f66;font-size:11px;">Failed to load</span>';
                return;
            }
            const positions = new Float32Array(data.positions);
            const normals = data.normals ? new Float32Array(data.normals) : null;
            const uvs = data.uvs ? new Float32Array(data.uvs) : null;
            const indices = new Uint32Array(data.indices);
            let pending = data.submeshes.length;

            data.submeshes.forEach((sub) => {
                const subIdx = indices.slice(sub.indexStart, sub.indexStart + sub.indexCount);
                const geo = new THREE.BufferGeometry();
                geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
                if (normals) geo.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
                if (uvs) geo.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));
                geo.setIndex(new THREE.BufferAttribute(subIdx, 1));

                const matOpts = {
                    side: sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
                    transparent: sub.transparent || false,
                    alphaTest: sub.transparent ? 0.5 : 0
                };

                const add = (mat) => {
                    this._previewGroup.add(new THREE.Mesh(geo, mat));
                    pending--;
                    if (pending <= 0) this._fitPreviewCamera();
                };

                if (sub.textureBase64) {
                    const img = new Image();
                    img.onload = () => {
                        const tex = new THREE.Texture(img);
                        tex.colorSpace = THREE.SRGBColorSpace;
                        tex.needsUpdate = true;
                        tex.wrapS = THREE.RepeatWrapping;
                        tex.wrapT = THREE.RepeatWrapping;
                        matOpts.map = tex;
                        add(new THREE.MeshStandardMaterial({
                            map: matOpts.map, side: matOpts.side, transparent: matOpts.transparent,
                            alphaTest: matOpts.alphaTest, roughness: 0.6, metalness: 0.05
                        }));
                    };
                    img.src = sub.textureBase64;
                } else {
                    add(new THREE.MeshStandardMaterial({
                        color: 0xaaaaaa, side: matOpts.side, roughness: 0.6, metalness: 0.05
                    }));
                }
            });
            this._previewOverlay.style.display = 'none';
        }).catch(() => {
            this._previewOverlay.innerHTML = '<span style="color:#f66;font-size:11px;">Request failed</span>';
        });
    }

    _fitPreviewCamera() {
        const box = new THREE.Box3();
        this._previewGroup.traverse((c) => {
            if (c.isMesh) {
                c.geometry.computeBoundingBox();
                const b = c.geometry.boundingBox.clone();
                b.applyMatrix4(c.matrixWorld);
                box.union(b);
            }
        });
        if (box.isEmpty()) return;
        const center = new THREE.Vector3(); box.getCenter(center);
        const size = new THREE.Vector3(); box.getSize(size);
        const maxDim = Math.max(size.x, size.y, size.z);
        const dist = maxDim * 1.4;
        this._previewCamera.position.set(center.x + dist * 0.6, center.y + dist * 0.4, center.z + dist * 0.6);
        this._previewControls.target.copy(center);
        this._previewCamera.near = dist * 0.01;
        this._previewCamera.far = dist * 10;
        this._previewCamera.updateProjectionMatrix();
    }

    _updatePlacedList() {
        const listEl = this._modal.querySelector('#wmoPlacedList');
        if (!listEl) return;

        const store = this.editor.placementStore;
        if (store.placedWmos.length === 0) {
            listEl.innerHTML = '<span style="color:#555;">None yet</span>';
            this._toggleFooterButtons();
            return;
        }
        let html = '';
        store.placedWmos.forEach((p) => {
            html += '<div style="display:flex;justify-content:space-between;align-items:center;padding:3px 0;border-bottom:1px solid rgba(255,255,255,0.05);gap:4px;">';
            html += '<span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1;" title="' + p.path + '">';
            html += '<i class="fa-solid fa-cube" style="color:#4a90d9;margin-right:4px;font-size:9px;"></i>' + p.name +
                ' <span style="color:#555;">(' + p.rotY + '°)</span>';
            html += '</span>';
            if (p.dbId && !p.committed) {
                html += '<span class="wmo-commit" data-id="' + p.id + '" style="cursor:pointer;color:#4a4;padding:0 4px;" title="Commit to Game World">' +
                    '<i class="fa-solid fa-globe" style="font-size:10px;"></i></span>';
            } else if (p.committed) {
                html += '<span style="color:#4a4;padding:0 4px;font-size:9px;" title="Committed to game world">' +
                    '<i class="fa-solid fa-check"></i></span>';
            }
            html += '<span class="wmo-remove" data-id="' + p.id + '" style="cursor:pointer;color:#f66;padding:0 4px;" title="Remove">' +
                '<i class="fa-solid fa-trash" style="font-size:10px;"></i></span>';
            html += '</div>';
        });
        listEl.innerHTML = html;

        listEl.querySelectorAll('.wmo-remove').forEach((el) => {
            el.addEventListener('click', () => {
                const id = parseInt(el.getAttribute('data-id'));
                const p = store.placedWmos.find((x) => x.id === id);
                if (!p) return;
                this.editor.history.execute(new RemovePlacementCommand(this.editor, p));
            });
        });
        listEl.querySelectorAll('.wmo-commit').forEach((el) => {
            el.addEventListener('click', () => {
                store.commitToWorld(parseInt(el.getAttribute('data-id')))
                    .then((resp) => {
                        if (!resp.success) alert('Commit failed: ' + (resp.error || 'unknown error'));
                    })
                    .catch(() => alert('Commit request failed'));
            });
        });

        this._toggleFooterButtons();
    }

    _toggleFooterButtons() {
        const store = this.editor.placementStore;
        const hasCommitted = store.placedWmos.some((p) => p.committed);
        const dl = this._modal.querySelector('#wmoDownloadMpq'); if (dl) dl.style.display = hasCommitted ? 'block' : 'none';
        const rg = this._modal.querySelector('#wmoRegenServerData'); if (rg) rg.style.display = hasCommitted ? 'block' : 'none';
        const rs = this._modal.querySelector('#wmoRestoreDefaults'); if (rs) rs.style.display = store.placedWmos.length > 0 ? 'block' : 'none';
    }
}
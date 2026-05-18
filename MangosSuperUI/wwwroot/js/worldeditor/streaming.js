// streaming.js — terrain + object streaming + per-model InstancedMesh pools.
//
// Sections:
//   1. Texture / model builders   — turn JSON responses into GPU resources
//   2. InstancePool                — per-model InstancedMesh manager
//   3. ObjectStream                — fetch queue + nearby-object pump
//   4. TileGrid                    — progressive ADT terrain + water

import * as THREE from 'three';
import { getJSON } from './net.js';
import { tagEntity } from './core.js';
import {
    makeTerrainMaterial,
    makeDoodadMaterial,
    makeWmoMaterial,
    maxAnisotropy
} from './render.js';

// Phase 8: BVH for sub-millisecond terrain raycasting (sculpt brush)
import {
    computeBoundsTree,
    disposeBoundsTree,
    acceleratedRaycast
} from 'three-mesh-bvh';

// Phase 8: monkey-patch BVH onto Three.js prototypes
THREE.BufferGeometry.prototype.computeBoundsTree = computeBoundsTree;
THREE.BufferGeometry.prototype.disposeBoundsTree = disposeBoundsTree;
THREE.Mesh.prototype.raycast = acceleratedRaycast;

// ─────────────────────────────────────────────────────────────────────────────
// 1. Texture / model builders
// ─────────────────────────────────────────────────────────────────────────────

function makeTextureFromDataURI(dataURI) {
    const tex = new THREE.TextureLoader().load(dataURI);
    tex.flipY = true;
    tex.wrapS = THREE.RepeatWrapping;
    tex.wrapT = THREE.RepeatWrapping;
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.anisotropy = maxAnisotropy();
    return tex;
}

export function buildModelParts(data) {
    const posAttr = new THREE.Float32BufferAttribute(data.positions, 3);
    const normAttr = new THREE.Float32BufferAttribute(data.normals, 3);
    const uvAttr = new THREE.Float32BufferAttribute(data.uvs, 2);
    const allIndices = data.indices;
    const subs = data.submeshes || [{ indexStart: 0, indexCount: allIndices.length, textureBase64: null }];

    const parts = [];
    for (let si = 0; si < subs.length; si++) {
        const sub = subs[si];
        if (!sub.indexCount) continue;

        const subIndices = allIndices.slice(sub.indexStart, sub.indexStart + sub.indexCount);
        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', posAttr);
        geo.setAttribute('normal', normAttr);
        geo.setAttribute('uv', uvAttr);
        geo.setIndex(new THREE.BufferAttribute(new Uint32Array(subIndices), 1));

        let material;
        if (sub.textureBase64) {
            material = makeDoodadMaterial({
                map: makeTextureFromDataURI(sub.textureBase64),
                side: THREE.DoubleSide,
                alphaTest: 0.5,
                transparent: true
            });
        } else {
            material = makeDoodadMaterial({ color: 0x808080, side: THREE.DoubleSide });
        }
        parts.push({ geometry: geo, material: material });
    }
    return parts;
}

export function buildWmoParts(data) {
    const posAttr = new THREE.Float32BufferAttribute(data.positions, 3);
    const normAttr = new THREE.Float32BufferAttribute(data.normals, 3);
    const uvAttr = new THREE.Float32BufferAttribute(data.uvs, 2);
    const allIndices = data.indices;
    const subs = data.submeshes || [];

    const parts = [];
    for (let si = 0; si < subs.length; si++) {
        const sub = subs[si];
        if (!sub.indexCount) continue;

        const subIndices = allIndices.slice(sub.indexStart, sub.indexStart + sub.indexCount);
        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', posAttr);
        geo.setAttribute('normal', normAttr);
        geo.setAttribute('uv', uvAttr);
        geo.setIndex(new THREE.BufferAttribute(new Uint32Array(subIndices), 1));

        let material;
        if (sub.textureBase64) {
            material = makeWmoMaterial({
                map: makeTextureFromDataURI(sub.textureBase64),
                side: sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
                alphaTest: sub.transparent ? 0.5 : 0,
                transparent: !!sub.transparent
            });
        } else {
            material = makeWmoMaterial({
                color: 0xaaaaaa,
                side: sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide
            });
        }
        parts.push({ geometry: geo, material: material });
    }
    return parts;
}

export function buildTerrainGeometry(tile) {
    const geo = new THREE.BufferGeometry();
    geo.setAttribute('position', new THREE.Float32BufferAttribute(tile.positions, 3));
    geo.setIndex(new THREE.BufferAttribute(new Uint32Array(tile.indices), 1));

    const uvs = new Float32Array(tile.positions.length / 3 * 2);
    for (let i = 0; i < tile.positions.length / 3; i++) {
        uvs[i * 2] = (i % tile.vertsWidth) / (tile.vertsWidth - 1);
        uvs[i * 2 + 1] = 1.0 - Math.floor(i / tile.vertsWidth) / (tile.vertsHeight - 1);
    }
    geo.setAttribute('uv', new THREE.Float32BufferAttribute(uvs, 2));
    geo.computeVertexNormals();
    return geo;
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. InstancePool — per-model InstancedMesh manager
// ─────────────────────────────────────────────────────────────────────────────
//
// Each loaded model has one InstanceSet:
//   { meshes: InstancedMesh[],  // one per submesh part
//     idToIndex, indexToId,     // bidirectional placement-id ↔ instance-index map
//     count, capacity, isWmo, parentGroup }
//
// Add: insert at next free index, set matrix, bump count.
// Remove: swap-with-last for O(1), shrink to 0 → dispose entire set.
// Grow: when count would exceed capacity, double capacity (rebuild meshes,
//       copy old matrices, swap children).

const INITIAL_CAPACITY = 512;

export class InstancePool {
    constructor() {
        // modelPath → { parts: [{geometry, material}] }
        this.modelRegistry = {};
        // modelPath → InstanceSet
        this.sets = {};

        // Parent groups with independent visibility toggles.
        this.doodadGroup = new THREE.Group();
        this.doodadGroup.name = 'allDoodads';
        this.doodadGroup.position.y = -0.5;

        this.wmoGroup = new THREE.Group();
        this.wmoGroup.name = 'allWmos';
        this.wmoGroup.position.y = -0.5;
    }

    attachTo(scene) {
        scene.add(this.doodadGroup);
        scene.add(this.wmoGroup);
    }

    registerModel(modelPath, parts) { this.modelRegistry[modelPath] = { parts }; }
    isModelLoaded(modelPath) { return !!this.modelRegistry[modelPath]; }

    setDoodadsVisible(v) { this.doodadGroup.visible = !!v; }
    setWmosVisible(v) { this.wmoGroup.visible = !!v; }

    buildPlacementMatrix(placement) {
        const matrix = new THREE.Matrix4();
        const pos = new THREE.Vector3(placement.x, placement.y, placement.z);
        const scl = new THREE.Vector3(1, 1, 1);

        if (placement.kind === 'w') {
            // WMO: MODF has no scale field — always 1.
            scl.set(1, 1, 1);
            const rot = new THREE.Euler(0, 0, 0, 'YXZ');
            if (placement.rotY) rot.y = (placement.rotY || 0) * Math.PI / 180;
            matrix.compose(pos, new THREE.Quaternion().setFromEuler(rot), scl);
            return matrix;
        }

        if (placement.kind === 'wd') {
            // WMO-embedded doodad: full quaternion, already in Y-up world space.
            // Server pre-composed (WMO_world_rot) · (MODD_local_rot) and did
            // the Z-up→Y-up basis change on both. We just pose it.
            const s = placement.scale || 1.0;
            scl.set(s, s, s);
            const quat = new THREE.Quaternion(
                placement.qx || 0,
                placement.qy || 0,
                placement.qz || 0,
                placement.qw == null ? 1 : placement.qw
            );
            matrix.compose(pos, quat, scl);
            return matrix;
        }

        // Default: ADT MDDF M2 doodad — Euler rotY in degrees with the
        // historical -90° offset to align model-forward with WoW's convention.
        const s = placement.scale || 1.0;
        scl.set(s, s, s);
        const rotM = new THREE.Euler(0, 0, 0, 'YXZ');
        rotM.y = ((placement.rotY || 0) - 90) * Math.PI / 180;
        matrix.compose(pos, new THREE.Quaternion().setFromEuler(rotM), scl);
        return matrix;
    }

    _getOrCreate(modelPath, isWmo) {
        if (this.sets[modelPath]) return this.sets[modelPath];

        const reg = this.modelRegistry[modelPath];
        if (!reg) return null;

        const parent = isWmo ? this.wmoGroup : this.doodadGroup;
        const meshes = [];
        for (let pi = 0; pi < reg.parts.length; pi++) {
            const part = reg.parts[pi];
            const im = new THREE.InstancedMesh(part.geometry, part.material, INITIAL_CAPACITY);
            im.count = 0;
            im.frustumCulled = false;
            // Phase 4: streamed instances are deliberately NOT selectable.
            // The swap-with-last in removeInstance() during unload would
            // silently re-target any outline-proxy to a different placement.
            // Phase 5/7 will flip this to selectable:true once the
            // unload-during-edit story is designed (will need a
            // streamedUnloaded signal in ObjectStream.pump).
            tagEntity(im, {
                type: isWmo ? 'wmo' : 'm2',
                id: 'instanced:' + modelPath,
                selectable: false,
                transformable: false,
                persistable: false,
                source: 'vanilla'
            });
            parent.add(im);
            meshes.push(im);
        }
        const set = {
            meshes, idToIndex: {}, indexToId: {},
            count: 0, capacity: INITIAL_CAPACITY,
            isWmo, parentGroup: parent
        };
        this.sets[modelPath] = set;
        return set;
    }

    addInstance(modelPath, placementId, placement) {
        const isWmo = placement.kind === 'w';
        const set = this._getOrCreate(modelPath, isWmo);
        if (!set) return;

        if (set.count >= set.capacity) this._grow(modelPath, set);

        const idx = set.count;
        set.idToIndex[placementId] = idx;
        set.indexToId[idx] = placementId;
        set.count++;

        const matrix = this.buildPlacementMatrix(placement);
        for (let mi = 0; mi < set.meshes.length; mi++) {
            set.meshes[mi].setMatrixAt(idx, matrix);
            set.meshes[mi].count = set.count;
            set.meshes[mi].instanceMatrix.needsUpdate = true;
        }
    }

    removeInstance(modelPath, placementId) {
        const set = this.sets[modelPath];
        if (!set) return;
        const idx = set.idToIndex[placementId];
        if (idx === undefined) return;

        const lastIdx = set.count - 1;
        if (idx !== lastIdx) {
            const lastId = set.indexToId[lastIdx];
            const tmp = new THREE.Matrix4();
            for (let mi = 0; mi < set.meshes.length; mi++) {
                set.meshes[mi].getMatrixAt(lastIdx, tmp);
                set.meshes[mi].setMatrixAt(idx, tmp);
                set.meshes[mi].instanceMatrix.needsUpdate = true;
            }
            set.idToIndex[lastId] = idx;
            set.indexToId[idx] = lastId;
        }
        delete set.idToIndex[placementId];
        delete set.indexToId[lastIdx];
        set.count--;
        for (let mi = 0; mi < set.meshes.length; mi++) set.meshes[mi].count = set.count;
        if (set.count === 0) this.disposeSet(modelPath);
    }

    _grow(modelPath, set) {
        const newCap = set.capacity * 2;
        const reg = this.modelRegistry[modelPath];
        if (!reg) return;

        const newMeshes = [];
        const tmp = new THREE.Matrix4();
        for (let pi = 0; pi < reg.parts.length; pi++) {
            const part = reg.parts[pi];
            const newIm = new THREE.InstancedMesh(part.geometry, part.material, newCap);
            newIm.count = set.count;
            newIm.frustumCulled = false;

            const oldIm = set.meshes[pi];
            for (let i = 0; i < set.count; i++) {
                oldIm.getMatrixAt(i, tmp);
                newIm.setMatrixAt(i, tmp);
            }
            newIm.instanceMatrix.needsUpdate = true;

            set.parentGroup.remove(oldIm);
            oldIm.dispose();
            set.parentGroup.add(newIm);
            newMeshes.push(newIm);
        }
        set.meshes = newMeshes;
        set.capacity = newCap;
    }

    disposeSet(modelPath) {
        const set = this.sets[modelPath];
        if (!set) return;
        for (let mi = 0; mi < set.meshes.length; mi++) {
            set.parentGroup.remove(set.meshes[mi]);
            set.meshes[mi].dispose();
        }
        delete this.sets[modelPath];
    }

    disposeAll() {
        for (const mp of Object.keys(this.sets)) this.disposeSet(mp);
        for (const mp of Object.keys(this.modelRegistry)) {
            const reg = this.modelRegistry[mp];
            if (reg && reg.parts) {
                for (const p of reg.parts) {
                    if (p.geometry) p.geometry.dispose();
                    if (p.material) {
                        if (p.material.map) p.material.map.dispose();
                        p.material.dispose();
                    }
                }
            }
        }
        this.modelRegistry = {};
    }

    wmoMeshList() {
        const out = [];
        this.wmoGroup.traverse((c) => {
            if (c.isInstancedMesh || c.isMesh) out.push(c);
        });
        return out;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. ObjectStream — nearby-object streaming pump
// ─────────────────────────────────────────────────────────────────────────────
//
// Responsibilities:
//  - Fetch queue: limit concurrent /WorldEditor/{Wmo,Doodad}Model fetches
//  - On 600ms tick: ask server for nearby objects, unload distant ones
//  - Maintain InstancePool: dedup against placement-store's customPlacementKeys
//  - Provide WMO mesh list for walk-mode collision

const MAX_CONCURRENT_FETCHES = 4;

export class ObjectStream {
    constructor(editor) {
        this.editor = editor;
        this.pool = new InstancePool();

        // activePlacements: id → { model, x, y, z, rotY, scale, type, rotX, rotZ,
        //                          kind:'d'|'w', instanced:bool }
        this.activePlacements = {};

        this._fetchQueue = [];
        this._fetchInFlight = 0;
        this._fetching = {};
        this._streamingInFlight = false;

        this.LOAD_RADIUS = 250;
        this.UNLOAD_RADIUS = 350;

        // One-shot diag flag: when true, the next /WorldEditor/NearbyObjects
        // request is sent with wmoDoodadDiag=1. The server then bypasses its
        // placement cache for that request and logs every WMO it composes
        // (one banner + 3 doodad dumps each). Set by clearAll() so every
        // preset reload (re)arms it; cleared by pump() after one send.
        this._diagNextNearby = true;
    }

    attachTo(scene) { this.pool.attachTo(scene); }
    wmoMeshList() { return this.pool.wmoMeshList(); }
    setLoadRadii(load, unload) { this.LOAD_RADIUS = load; this.UNLOAD_RADIUS = unload; }
    setDoodadsVisible(v) { this.pool.setDoodadsVisible(v); }
    setWmosVisible(v) { this.pool.setWmosVisible(v); }

    _enqueueFetch(modelPath, priority) {
        if (this._fetching[modelPath]) return;
        this._fetching[modelPath] = true;
        this._fetchQueue.push({ path: modelPath, priority: priority || 0 });
        this._drain();
    }

    _drain() {
        while (this._fetchInFlight < MAX_CONCURRENT_FETCHES && this._fetchQueue.length > 0) {
            this._fetchQueue.sort((a, b) => a.priority - b.priority);
            const item = this._fetchQueue.shift();
            this._fetchInFlight++;

            const modelPath = item.path;
            const isWmo = modelPath.toLowerCase().indexOf('.wmo') !== -1;
            const url = isWmo ? '/WorldEditor/WmoModel' : '/WorldEditor/DoodadModel';

            getJSON(url + '?path=' + encodeURIComponent(modelPath))
                .then((mdata) => {
                    this._fetchInFlight--;
                    if (mdata.success && mdata.positions && mdata.positions.length > 0) {
                        const parts = isWmo ? buildWmoParts(mdata) : buildModelParts(mdata);
                        this.pool.registerModel(modelPath, parts);
                        this._instantiatePending(modelPath);
                    }
                    delete this._fetching[modelPath];
                    this._drain();
                })
                .catch(() => {
                    this._fetchInFlight--;
                    delete this._fetching[modelPath];
                    this._drain();
                });
        }
    }

    _instantiatePending(modelPath) {
        for (const id in this.activePlacements) {
            const p = this.activePlacements[id];
            if (p.model === modelPath && !p.instanced) {
                this.pool.addInstance(modelPath, id, p);
                p.instanced = true;
            }
        }
    }

    pump(camX, camZ, globalMidHeight, globalHeightScale) {
        if (!this.editor.currentPreset || this._streamingInFlight) return;

        // Unload distant
        const toRemove = [];
        for (const id in this.activePlacements) {
            const p = this.activePlacements[id];
            const dx = p.x - camX;
            const dz = p.z - camZ;
            if (dx * dx + dz * dz > this.UNLOAD_RADIUS * this.UNLOAD_RADIUS) toRemove.push(id);
        }
        for (const id of toRemove) {
            const p = this.activePlacements[id];
            if (p.instanced) this.pool.removeInstance(p.model, id);
            delete this.activePlacements[id];
        }

        // Server fetch
        this._streamingInFlight = true;
        let url = '/WorldEditor/NearbyObjects' +
            '?preset=' + encodeURIComponent(this.editor.currentPreset) +
            '&camX=' + camX.toFixed(1) +
            '&camZ=' + camZ.toFixed(1) +
            '&loadRadius=' + this.LOAD_RADIUS.toFixed(0) +
            '&globalMidHeight=' + globalMidHeight +
            '&globalHeightScale=' + globalHeightScale;

        // One-shot WMO doodad composition diagnostic. Server bypasses the
        // placement cache for this request and logs every WMO it composes.
        if (this._diagNextNearby) {
            url += '&wmoDoodadDiag=true';
            this._diagNextNearby = false;
            console.log('[ObjectStream] firing WMO doodad diagnostic on this NearbyObjects request');
        }

        getJSON(url)
            .then((resp) => {
                this._streamingInFlight = false;
                if (!resp.success) return;
                const adds = resp.add || {};
                this._addDoodads(adds.doodads || [], camX, camZ);
                this._addWmos((adds.wmos || []), camX, camZ);
            })
            .catch(() => { this._streamingInFlight = false; });
    }

    _addDoodads(arr, camX, camZ) {
        for (const d of arr) {
            if (this.activePlacements[d.id]) continue;

            // Two flavors of doodad share this array:
            //   kind 'd'  = ADT MDDF placement, oriented by Euler rotY
            //   kind 'wd' = WMO-embedded MODD, oriented by a full quaternion
            //               already in Y-up world space (server pre-composes
            //               WMO_world_rot · MODD_local_rot for us)
            const placementKind = d.kind || 'd';
            const rec = {
                model: d.model, x: d.x, y: d.y, z: d.z,
                scale: d.scale, type: d.type,
                kind: placementKind, instanced: false
            };
            if (placementKind === 'wd') {
                rec.qx = d.qx; rec.qy = d.qy; rec.qz = d.qz; rec.qw = d.qw;
            } else {
                rec.rotY = d.rotY;
            }
            this.activePlacements[d.id] = rec;

            if (this.pool.isModelLoaded(d.model)) {
                this.pool.addInstance(d.model, d.id, this.activePlacements[d.id]);
                this.activePlacements[d.id].instanced = true;
            } else {
                const dx = d.x - camX, dz = d.z - camZ;
                this._enqueueFetch(d.model, dx * dx + dz * dz);
            }
        }
    }

    _addWmos(arr, camX, camZ) {
        const customKeys = this.editor.placementStore
            ? this.editor.placementStore.customPlacementKeys
            : {};
        for (const w of arr) {
            if (this.activePlacements[w.id]) continue;
            // Skip if a custom placement is at this position (avoids
            // double-rendering placed WMOs that also exist in the ADT).
            const sp = Math.round(w.x) + '|' + Math.round(w.z);
            if (customKeys[sp]) continue;

            this.activePlacements[w.id] = {
                model: w.model, x: w.x, y: w.y, z: w.z,
                rotX: w.rotX, rotY: w.rotY, rotZ: w.rotZ,
                kind: 'w', instanced: false
            };
            if (this.pool.isModelLoaded(w.model)) {
                this.pool.addInstance(w.model, w.id, this.activePlacements[w.id]);
                this.activePlacements[w.id].instanced = true;
            } else {
                const dx = w.x - camX, dz = w.z - camZ;
                this._enqueueFetch(w.model, dx * dx + dz * dz);
            }
        }
    }

    purgeStreamedNear(x, z, r) {
        const r2 = r * r;
        const purge = [];
        for (const sid in this.activePlacements) {
            const sp = this.activePlacements[sid];
            if (sp.kind !== 'w') continue;
            const dx = sp.x - x, dz = sp.z - z;
            if (dx * dx + dz * dz < r2) purge.push(sid);
        }
        for (const pid of purge) {
            const pp = this.activePlacements[pid];
            if (pp.instanced) this.pool.removeInstance(pp.model, pid);
            delete this.activePlacements[pid];
        }
    }

    clearAll() {
        this.pool.disposeAll();
        this.activePlacements = {};
        this._fetchQueue = [];
        this._fetchInFlight = 0;
        this._fetching = {};
        // Re-arm one-shot diag for the next preset's first NearbyObjects call.
        this._diagNextNearby = true;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. TileGrid — progressive ADT terrain + water
// ─────────────────────────────────────────────────────────────────────────────
//
// Owns the `tiles[]` map, initial 3x3 heightmap load, single-tile loads on
// camera movement, water mesh per tile, and unloading distant tiles.
// Exposes `terrainMeshes()` for the placement raycaster + walk-mode snap.

export class TileGrid {
    constructor(editor) {
        this.editor = editor;
        // 'gx,gy' → { mesh, gridX, gridY, dx, dy, loading, waterMesh }
        this.tiles = {};
        this.loading = {};
        this.tileWidthMesh = 0;
        this.globalMidHeight = 0;
        this.globalHeightScale = 2.0;
        this.centerGridX = 0;
        this.centerGridY = 0;
        this.mapId = 0;
        this.TILE_RADIUS = 1;
        this.UNLOAD_RADIUS = 3;
        this.textureRes = 128;
        this.fogNear = 180;
        this.fogFar = 550;
    }

    terrainMeshes() {
        const out = [];
        for (const k in this.tiles) {
            const t = this.tiles[k];
            if (t.mesh) out.push(t.mesh);
        }
        return out;
    }

    setTileRadius(r) {
        this.TILE_RADIUS = Math.max(1, r | 0);
        this.UNLOAD_RADIUS = this.TILE_RADIUS + 2;
    }

    setTextureRes(r) { this.textureRes = r; }

    cameraToGrid(controlsTarget) {
        const dx = Math.round(controlsTarget.x / this.tileWidthMesh);
        const dy = Math.round(controlsTarget.z / this.tileWidthMesh);
        return { gridX: this.centerGridX + dy, gridY: this.centerGridY + dx };
    }

    updateFogForRadius(scene, camera, r) {
        const range = Math.max(0.3, r + 0.5) * (this.tileWidthMesh || 400);
        this.fogNear = range * 0.3;
        this.fogFar = range * 0.9;
        if (scene.fog) {
            scene.fog.near = this.fogNear;
            scene.fog.far = this.fogFar;
        }
        camera.far = range * 1.5;
        camera.updateProjectionMatrix();
    }

    objectRadiiForCurrent() {
        const base = this.tileWidthMesh || 400;
        const load = Math.max(150, (this.TILE_RADIUS + 0.5) * base * 0.6);
        const unload = load * 1.4;
        return { load, unload };
    }

    loadPreset(presetKey, statusCallback) {
        const editor = this.editor;
        editor.currentPreset = presetKey;

        // Clear existing tiles
        Object.keys(this.tiles).forEach((k) => this._unloadTile(k));
        this.tiles = {};
        this.loading = {};

        return getJSON('/WorldEditor/Heightmap?preset=' + encodeURIComponent(presetKey) + '&tileRadius=1')
            .then((hm) => {
                if (!hm.success) {
                    statusCallback && statusCallback('Heightmap failed: ' + hm.error);
                    return null;
                }
                this.tileWidthMesh = hm.tileWidthMesh;
                this.globalMidHeight = hm.midHeight;
                this.globalHeightScale = hm.heightScale;

                const center = hm.tiles.find((t) => t.dx === 0 && t.dy === 0);
                if (center) {
                    this.centerGridX = center.gridX;
                    this.centerGridY = center.gridY;
                }

                const radii = this.objectRadiiForCurrent();
                if (editor.objectStream) editor.objectStream.setLoadRadii(radii.load, radii.unload);

                const toLoad = [];
                hm.tiles.forEach((tile) => {
                    const key = this._key(tile.gridX, tile.gridY);
                    const entry = {
                        mesh: null, gridX: tile.gridX, gridY: tile.gridY,
                        dx: tile.dx, dy: tile.dy,
                        geo: buildTerrainGeometry(tile),
                        loading: false
                    };
                    this.tiles[key] = entry;
                    toLoad.push(entry);
                });

                let texLoaded = 0;
                return new Promise((resolve) => {
                    toLoad.forEach((entry) => {
                        this._loadTexture(entry, () => {
                            texLoaded++;
                            statusCallback && statusCallback('Textures: ' + texLoaded + '/' + toLoad.length);
                            if (texLoaded >= toLoad.length) {
                                this.updateFogForRadius(editor.viewport.scene, editor.viewport.rig.camera, this.TILE_RADIUS);
                                statusCallback && statusCallback(hm.label || presetKey);
                                toLoad.forEach((e) => this._loadWater(e));
                                editor.signals.presetLoaded.dispatch(presetKey);
                                resolve(hm);
                            }
                        });
                    });
                });
            });
    }

    checkProgressive(controlsTarget) {
        if (!this.editor.currentPreset || this.tileWidthMesh === 0) return;
        const cam = this.cameraToGrid(controlsTarget);
        for (let dy = -this.TILE_RADIUS; dy <= this.TILE_RADIUS; dy++) {
            for (let dx = -this.TILE_RADIUS; dx <= this.TILE_RADIUS; dx++) {
                const gx = cam.gridX + dy;
                const gy = cam.gridY + dx;
                const key = this._key(gx, gy);
                if (gx < 0 || gx > 63 || gy < 0 || gy > 63) continue;
                if (this.tiles[key] || this.loading[key]) continue;
                this.loading[key] = true;
                this._loadSingleTile(gx, gy);
            }
        }
        Object.keys(this.tiles).forEach((key) => {
            const t = this.tiles[key];
            const dgx = t.gridX - cam.gridX;
            const dgy = t.gridY - cam.gridY;
            if (Math.abs(dgx) > this.UNLOAD_RADIUS || Math.abs(dgy) > this.UNLOAD_RADIUS) {
                this._unloadTile(key);
            }
        });
    }

    _key(gx, gy) { return gx + ',' + gy; }

    _loadSingleTile(gx, gy) {
        const key = this._key(gx, gy);
        const url = '/WorldEditor/SingleTileHeightmap' +
            '?preset=' + encodeURIComponent(this.editor.currentPreset) +
            '&tileGridX=' + gx + '&tileGridY=' + gy +
            '&globalMidHeight=' + this.globalMidHeight +
            '&globalHeightScale=' + this.globalHeightScale;

        getJSON(url).then((hm) => {
            if (!hm.success) { delete this.loading[key]; return; }
            const geo = buildTerrainGeometry(hm);
            const dx = gy - this.centerGridY;
            const dy = gx - this.centerGridX;
            const entry = { mesh: null, gridX: gx, gridY: gy, dx, dy, geo, loading: true };
            this.tiles[key] = entry;
            this._loadTexture(entry, () => {
                if (entry.mesh) {
                    entry.mesh.position.x = dx * this.tileWidthMesh;
                    entry.mesh.position.z = dy * this.tileWidthMesh;
                    entry.mesh.position.y = -0.5;
                }
                delete this.loading[key];
                this._loadWater(entry);
            });
        }).catch(() => { delete this.loading[key]; });
    }

    _loadTexture(entry, callback) {
        const url = '/WorldEditor/Textures?preset=' + encodeURIComponent(this.editor.currentPreset) +
            '&tileGridX=' + entry.gridX + '&tileGridY=' + entry.gridY +
            '&pixelsPerChunk=' + this.textureRes;

        getJSON(url).then((tex) => {
            if (tex.success && tex.compositeBase64) {
                const img = new Image();
                img.onload = () => {
                    const t = new THREE.Texture(img);
                    t.colorSpace = THREE.SRGBColorSpace;
                    t.needsUpdate = true;
                    t.wrapS = THREE.ClampToEdgeWrapping;
                    t.wrapT = THREE.ClampToEdgeWrapping;
                    t.minFilter = THREE.LinearMipmapLinearFilter;
                    t.magFilter = THREE.LinearFilter;
                    t.anisotropy = maxAnisotropy();
                    t.generateMipmaps = true;
                    this._finishTile(entry, makeTerrainMaterial({ map: t }));
                    if (callback) callback();
                };
                img.src = 'data:image/png;base64,' + tex.compositeBase64;
                return;
            }
            this._finishTile(entry, makeTerrainMaterial({ color: 0x3a5a2a }));
            if (callback) callback();
        }).catch(() => {
            this._finishTile(entry, makeTerrainMaterial({ color: 0x3a5a2a }));
            if (callback) callback();
        });
    }

    _finishTile(entry, mat) {
        entry.mesh = new THREE.Mesh(entry.geo, mat);
        entry.mesh.position.y = -0.5;

        // Phase 8: tag terrain mesh with tile identity for sculpt tool
        entry.mesh.userData.tileKey = this._key(entry.gridX, entry.gridY);
        entry.mesh.userData.tileGridX = entry.gridX;
        entry.mesh.userData.tileGridY = entry.gridY;

        // Phase 8: BVH for fast brush raycasting (also benefits walk-mode
        // terrain snap and placement ghost terrain raycast)
        entry.mesh.geometry.computeBoundsTree();

        this.editor.viewport.scene.add(entry.mesh);
        entry.loading = false;
    }

    _unloadTile(key) {
        const t = this.tiles[key];
        if (!t) return;
        const scene = this.editor.viewport.scene;
        if (t.mesh) {
            scene.remove(t.mesh);
            // Phase 8: dispose BVH before geometry
            if (t.mesh.geometry && t.mesh.geometry.boundsTree) {
                t.mesh.geometry.disposeBoundsTree();
            }
            if (t.mesh.geometry) t.mesh.geometry.dispose();
            if (t.mesh.material) {
                if (t.mesh.material.map) t.mesh.material.map.dispose();
                t.mesh.material.dispose();
            }
        }
        if (t.waterMesh) {
            scene.remove(t.waterMesh);
            if (t.waterMesh.geometry) t.waterMesh.geometry.dispose();
            if (t.waterMesh.material) t.waterMesh.material.dispose();
        }
        delete this.tiles[key];
    }

    _loadWater(entry) {
        const url = '/WorldEditor/Water?preset=' + encodeURIComponent(this.editor.currentPreset) +
            '&tileGridX=' + entry.gridX + '&tileGridY=' + entry.gridY +
            '&globalMidHeight=' + this.globalMidHeight +
            '&globalHeightScale=' + this.globalHeightScale;

        getJSON(url).then((w) => {
            if (!w.success || !w.hasWater) return;
            const tileEntry = this.tiles[this._key(entry.gridX, entry.gridY)];
            if (!tileEntry) return;

            // Server returns flat positions[] + indices[] already in tile-local
            // mesh coordinates (tile centered at origin in its own frame). Per
            // VMaNGOS .map cell-vertex grid resolution — one quad per water cell
            // with 4 real heights from the vertex grid.
            if (!w.positions || !w.indices || w.positions.length === 0) return;

            const geo = new THREE.BufferGeometry();
            geo.setAttribute('position', new THREE.Float32BufferAttribute(new Float32Array(w.positions), 3));
            geo.setIndex(new THREE.BufferAttribute(new Uint32Array(w.indices), 1));
            geo.computeVertexNormals();

            const mat = new THREE.MeshBasicMaterial({
                color: 0x2266aa, transparent: true, opacity: 0.45,
                side: THREE.DoubleSide, depthWrite: false, fog: true
            });

            const waterMesh = new THREE.Mesh(geo, mat);
            const dx = entry.gridY - this.centerGridY;
            const dy = entry.gridX - this.centerGridX;
            waterMesh.position.x = dx * this.tileWidthMesh;
            waterMesh.position.z = dy * this.tileWidthMesh;
            waterMesh.position.y = 0;
            waterMesh.renderOrder = 1;
            this.editor.viewport.scene.add(waterMesh);
            tileEntry.waterMesh = waterMesh;
        });
    }

    applyWireframe(on) {
        for (const k in this.tiles) {
            const t = this.tiles[k];
            if (t.mesh && t.mesh.material) t.mesh.material.wireframe = on;
        }
    }

    unloadTile(key) { this._unloadTile(key); }

    clearAll() {
        Object.keys(this.tiles).forEach((k) => this._unloadTile(k));
        this.tiles = {};
        this.loading = {};
    }
}
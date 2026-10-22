// Quest Atlas — dressing the table: the buildings and props the ADTs place, and a figurine at
// every creature spawn. Static on purpose (QUEST_ATLAS.md); a later follow-along view can move them.
//
// Placement law is the 3D World Editor's (proven there): WMO yaw = MODF rotY, M2 yaw = MDDF rotY − 90°,
// model geometry already in the editor's Y-up space whose x/z run the same way as this table's.
// Models keep TRUE scale; only the ground under them is exaggerated, so a tower stands on the
// raised hill at its real height instead of being stretched with it.

import * as THREE from 'three';
import { S } from './stage.js';

const MODEL_FETCHES = 4;
const TREE_KINDS = new Set(['tree', 'vegetation', 'wood']);

// creature_template.type → figurine colour (no faction reaction data on the page yet)
const TYPE_COLOR = {
    1: 0xc98a4b,   // beast
    2: 0xb23a2a,   // dragonkin
    3: 0xa33d7a,   // demon
    4: 0x6fd0e0,   // elemental
    5: 0x8f8f80,   // giant
    6: 0x8b6bb8,   // undead
    7: 0xc24a3a,   // humanoid
    8: 0x9a9284,   // critter
    9: 0x7c8c99,   // mechanical
    10: 0xb0a080   // not specified
};
const QUEST_COLOR = 0xf2c14e;
const SERVICE_COLOR = 0x5fb3c9;
const TYPE_NAME = { 1: 'beast', 2: 'dragonkin', 3: 'demon', 4: 'elemental', 5: 'giant', 6: 'undead', 7: 'humanoid', 8: 'critter', 9: 'mechanical', 10: 'unspecified' };

export class WorldDressing {
    constructor(stage) {
        this.stage = stage;
        this.root = new THREE.Group();
        this.buildings = new THREE.Group();
        this.props = new THREE.Group();
        this.figures = new THREE.Group();
        this.root.add(this.buildings, this.props, this.figures);
        stage.scene.add(this.root);

        this.modelCache = new Map();     // path → Promise<parts[] | null>   (kept across zones)
        this.texCache = new Map();       // data URI → Texture
        this.sets = [];                  // { kind, list, meshes }
        this.token = 0;
        this.figureScale = 2.5;
        this.showCritters = false;
        this.pawn = pawnGeometry();
        this.figureList = [];            // per-instance pick data
    }

    // ── buildings + props ──────────────────────────────────────────────

    /** Fetch every tile's placements, then load each distinct model once and instance it. */
    async load(board, onProgress) {
        const token = ++this.token;
        this.#clearSets();
        const tiles = [...board.tiles.values()];
        const wmos = new Map(), doodads = new Map();
        const seen = new Set();

        await pool(tiles, 6, async t => {
            const r = await fetch(`/QuestAtlas/Objects?mapId=${board.mapId}&gx=${t.gx}&gy=${t.gy}`).then(x => x.json()).catch(() => null);
            if (!r?.exists || token !== this.token) return;
            for (const [arr, into, kind] of [[r.wmos, wmos, 'wmo'], [r.doodads, doodads, 'doodad']])
                for (const p of arr) {
                    if (!this.stage.onBoard(board.mapId, p.x, p.y)) continue;
                    const k = `${p.m}|${Math.round(p.x)}|${Math.round(p.y)}|${Math.round(p.z)}`;
                    if (seen.has(k)) continue;           // a WMO is listed by every tile it touches
                    seen.add(k);
                    p.kind = kind;
                    if (!into.has(p.m)) into.set(p.m, []);
                    into.get(p.m).push(p);
                }
        });
        if (token !== this.token) return;

        // buildings first, then props by how often they appear
        const jobs = [
            ...[...wmos].map(([path, list]) => ({ path, list, kind: 'wmo' })),
            ...[...doodads].sort((a, b) => b[1].length - a[1].length).map(([path, list]) => ({ path, list, kind: 'doodad' }))
        ];
        let done = 0;
        const total = jobs.length;
        const counts = { buildings: [...wmos.values()].reduce((n, l) => n + l.length, 0), props: [...doodads.values()].reduce((n, l) => n + l.length, 0) };
        onProgress?.({ done, total, ...counts });

        await pool(jobs, MODEL_FETCHES, async job => {
            const parts = await this.#model(job.path, job.kind === 'wmo');
            if (token !== this.token) return;
            if (parts) this.#instance(job, parts);
            onProgress?.({ done: ++done, total, ...counts });
        });
    }

    #model(path, isWmo) {
        if (!this.modelCache.has(path)) {
            const url = (isWmo ? '/WorldEditor/WmoModel?path=' : '/WorldEditor/DoodadModel?path=') + encodeURIComponent(path);
            this.modelCache.set(path, fetch(url).then(r => r.ok ? r.json() : null)
                .then(d => d?.success && d.positions?.length ? this.#parts(d, isWmo) : null)
                .catch(() => null));
        }
        return this.modelCache.get(path);
    }

    #parts(data, isWmo) {
        const pos = new THREE.Float32BufferAttribute(data.positions, 3);
        const nrm = new THREE.Float32BufferAttribute(data.normals, 3);
        const uv = new THREE.Float32BufferAttribute(data.uvs, 2);
        const subs = data.submeshes?.length ? data.submeshes : [{ indexStart: 0, indexCount: data.indices.length }];
        const parts = [];
        for (const sub of subs) {
            if (!sub.indexCount) continue;
            const geo = new THREE.BufferGeometry();
            geo.setAttribute('position', pos);
            geo.setAttribute('normal', nrm);
            geo.setAttribute('uv', uv);
            geo.setIndex(new THREE.BufferAttribute(new Uint32Array(data.indices.slice(sub.indexStart, sub.indexStart + sub.indexCount)), 1));

            const additive = !isWmo && (sub.blendMode === 3 || sub.blendMode === 4);
            const mat = new THREE.MeshLambertMaterial({
                color: sub.textureBase64 ? 0xffffff : 0x9a9080,
                // WMO UVs want flipY (MOTV); M2 doodads follow V=0-at-top (see worldeditor/streaming.js)
                map: sub.textureBase64 ? this.#texture(sub.textureBase64, isWmo) : null,
                side: isWmo ? (sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide) : THREE.DoubleSide,
                alphaTest: additive ? 0 : (isWmo ? (sub.transparent ? 0.5 : 0) : 0.5),
                transparent: additive || (isWmo && !!sub.transparent),
                blending: additive ? THREE.AdditiveBlending : THREE.NormalBlending,
                depthWrite: !additive
            });
            parts.push({ geo, mat });
        }
        return parts;
    }

    #texture(uri, flipY) {
        const k = (flipY ? 'w' : 'm') + uri;
        let t = this.texCache.get(k);
        if (!t) {
            t = new THREE.TextureLoader().load(uri);
            t.flipY = flipY;
            t.wrapS = t.wrapT = THREE.RepeatWrapping;
            t.colorSpace = THREE.SRGBColorSpace;
            t.anisotropy = 4;
            this.texCache.set(k, t);
        }
        return t;
    }

    #instance(job, parts) {
        const parent = job.kind === 'wmo' ? this.buildings : this.props;
        const tree = job.kind === 'doodad' && TREE_KINDS.has(job.list[0].k);
        const meshes = parts.map(({ geo, mat }) => {
            const im = new THREE.InstancedMesh(geo, mat, job.list.length);
            im.castShadow = true;
            im.receiveShadow = job.kind === 'wmo';
            im.frustumCulled = false;       // bounds would need recomputing per relayout; the board is one view anyway
            parent.add(im);
            return im;
        });
        const set = { kind: job.kind, tree, list: job.list, meshes };
        this.sets.push(set);
        this.#place(set);
    }

    #place(set) {
        const m = new THREE.Matrix4(), q = new THREE.Quaternion(), e = new THREE.Euler(0, 0, 0, 'YXZ');
        set.list.forEach((p, i) => {
            e.y = ((set.kind === 'wmo' ? p.r : p.r - 90) * Math.PI) / 180;
            q.setFromEuler(e);
            const s = S * (set.kind === 'wmo' ? 1 : (p.s || 1));
            m.compose(this.stage.toSceneModel(p.x, p.y, p.z), q, new THREE.Vector3(s, s, s));
            for (const im of set.meshes) im.setMatrixAt(i, m);
        });
        for (const im of set.meshes) im.instanceMatrix.needsUpdate = true;
    }

    // ── figurines ──────────────────────────────────────────────────────

    /** Every creature standing on the board, as pawns coloured by role/type. */
    async loadCreatures(board, atlasModel) {
        const token = this.token;
        const q = `mapId=${board.mapId}&top=${board.top}&bottom=${board.bottom}&left=${board.left}&right=${board.right}`;
        const r = await fetch(`/QuestAtlas/Creatures?${q}`).then(x => x.json()).catch(() => null);
        if (!r || token !== this.token) return null;
        this.creatures = r;
        this.atlasModel = atlasModel;
        this.#buildFigures();
        return { count: r.spawns.length, truncated: r.truncated };
    }

    #buildFigures() {
        for (const o of [...this.figures.children]) { this.figures.remove(o); o.material?.dispose(); }
        this.figureList = [];
        const r = this.creatures;
        if (!r) return;
        const m = this.atlasModel;
        const questNpc = e => m && (m.gives.has(`npc:${e}`) || m.ends.has(`npc:${e}`));

        const rows = [];
        for (const s of r.spawns) {
            const t = r.templates[s[0]];
            if (!t) continue;
            if (t.type === 8 && !this.showCritters) continue;
            const role = questNpc(s[0]) ? 'quest' : (t.npcFlags & ~1) ? 'service' : 'creature';
            rows.push({ s, t, role });
        }

        const mesh = new THREE.InstancedMesh(this.pawn, new THREE.MeshLambertMaterial({ color: 0xffffff }), Math.max(1, rows.length));
        mesh.count = rows.length;
        mesh.castShadow = true;
        const c = new THREE.Color();
        rows.forEach((row, i) => {
            c.set(row.role === 'quest' ? QUEST_COLOR : row.role === 'service' ? SERVICE_COLOR : (TYPE_COLOR[row.t.type] ?? 0xb0a080));
            mesh.setColorAt(i, c);
            this.figureList.push({
                type: 'figure', entry: row.s[0], role: row.role,
                name: row.t.name, subname: row.t.subname, levelMin: row.t.levelMin, levelMax: row.t.levelMax,
                rank: row.t.rank, creatureType: TYPE_NAME[row.t.type] ?? '', x: row.s[1], y: row.s[2], z: row.s[3], o: row.s[4]
            });
        });
        if (mesh.instanceColor) mesh.instanceColor.needsUpdate = true;
        this.figureMesh = mesh;
        this.figures.add(mesh);
        this.#placeFigures();
    }

    #placeFigures() {
        const mesh = this.figureMesh;
        if (!mesh) return;
        const m = new THREE.Matrix4(), q = new THREE.Quaternion(), up = new THREE.Vector3(0, 1, 0);
        this.figureList.forEach((f, i) => {
            const size = 7 * S * this.figureScale * rankScale(f.rank) * (f.creatureType === 'critter' ? 0.5 : 1);
            f.head = this.stage.toScene(f.x, f.y, null);
            q.setFromAxisAngle(up, f.o || 0);
            m.compose(f.head.clone(), q, new THREE.Vector3(size, size, size));
            f.head.y += size * 0.85;
            f.size = size;
            mesh.setMatrixAt(i, m);
        });
        mesh.instanceMatrix.needsUpdate = true;
    }

    /** Ray-near picking (cheaper than triangle tests over thousands of pawns). */
    pickFigure(raycaster) {
        if (!this.figures.visible || !this.figureList.length) return null;
        let best = null, bestT = Infinity;
        const ray = raycaster.ray, v = new THREE.Vector3();
        for (const f of this.figureList) {
            if (!f.head) continue;
            const d = ray.distanceSqToPoint(f.head);
            if (d > (f.size * 0.6) ** 2) continue;
            const t = v.copy(f.head).sub(ray.origin).dot(ray.direction);
            if (t > 0 && t < bestT) { bestT = t; best = f; }
        }
        return best;
    }

    // ── switches ───────────────────────────────────────────────────────

    setBuildings(on) { this.buildings.visible = on; }
    setProps(on) { this.props.visible = on; }
    setFigures(on) { this.figures.visible = on; }
    setCritters(on) { this.showCritters = on; this.#buildFigures(); }
    setFigureScale(v) { this.figureScale = v; this.#placeFigures(); }

    /** Ground height changed (relief slider): re-seat everything. */
    relayout() {
        for (const set of this.sets) this.#place(set);
        this.#placeFigures();
    }

    clear() {
        this.token++;
        this.#clearSets();
        for (const o of [...this.figures.children]) { this.figures.remove(o); o.material?.dispose(); }
        this.figureList = [];
        this.figureMesh = null;
        this.creatures = null;
    }

    #clearSets() {
        // geometry/materials stay in modelCache for the next zone; only the instance holders go
        for (const set of this.sets) for (const im of set.meshes) { im.parent?.remove(im); im.dispose(); }
        this.sets = [];
    }
}

function rankScale(rank) {
    return rank === 3 ? 1.8 : rank === 1 || rank === 2 ? 1.35 : rank === 4 ? 1.25 : 1;
}

/** A board-game pawn, 1 unit tall, base at the origin. */
function pawnGeometry() {
    const pts = [
        [0, 0], [0.40, 0], [0.42, 0.04], [0.38, 0.09], [0.26, 0.13], [0.20, 0.22],
        [0.15, 0.50], [0.24, 0.56], [0.24, 0.60], [0.13, 0.63],
        [0.19, 0.68], [0.22, 0.76], [0.21, 0.84], [0.16, 0.92], [0.09, 0.97], [0, 1]
    ].map(([r, h]) => new THREE.Vector2(r, h));
    const g = new THREE.LatheGeometry(pts, 12);
    g.computeVertexNormals();
    return g;
}

async function pool(items, n, fn) {
    let next = 0;
    await Promise.all(Array.from({ length: Math.min(n, items.length) }, async () => {
        while (next < items.length) await fn(items[next++]);
    }));
}

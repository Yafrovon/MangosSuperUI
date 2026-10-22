// Quest Atlas — the table: compiled relief slab, lights, miniature lens, camera flights, picking.
//
// World law (WoW): X north, Y west, Z up.  Scene law: x east, z south, y up, centred on the board.
// Relief tiles come from /QuestAtlas/Relief (33×33 heights per ADT tile), paint from the client's
// minimap tiles via /WorldMap/Tile — both read from the MPQs on demand by the server.

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { ShaderPass } from 'three/addons/postprocessing/ShaderPass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { HorizontalTiltShiftShader } from 'three/addons/shaders/HorizontalTiltShiftShader.js';
import { VerticalTiltShiftShader } from 'three/addons/shaders/VerticalTiltShiftShader.js';
import { CSS2DRenderer } from 'three/addons/renderers/CSS2DRenderer.js';

export const TILE = 533.33333;
export const S = 0.1;                 // scene units per yard
const SAMPLES = 33;
const STEP = TILE / (SAMPLES - 1);    // yards between relief samples
const SLAB_DEPTH = 45;                // yards of "earth" under the lowest point

export class Stage {
    constructor(host, handlers) {
        this.host = host;
        this.handlers = handlers;     // { onHover(data, px, py), onPick(data), onGround(world) }
        this.exaggeration = 2.2;
        this.board = null;
        this.pickables = [];
        this.flight = null;

        this.renderer = new THREE.WebGLRenderer({ antialias: true });
        this.renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));
        this.renderer.toneMapping = THREE.ACESFilmicToneMapping;
        this.renderer.toneMappingExposure = 1.05;
        this.renderer.outputColorSpace = THREE.SRGBColorSpace;
        this.renderer.shadowMap.enabled = true;
        this.renderer.shadowMap.type = THREE.PCFSoftShadowMap;
        host.appendChild(this.renderer.domElement);

        this.labels = new CSS2DRenderer();
        this.labels.domElement.style.position = 'absolute';
        this.labels.domElement.style.inset = '0';
        this.labels.domElement.style.pointerEvents = 'none';
        host.appendChild(this.labels.domElement);

        this.scene = new THREE.Scene();
        this.scene.background = new THREE.Color(0x1a140e);
        this.scene.fog = new THREE.Fog(0x1a140e, 400, 2400);

        this.camera = new THREE.PerspectiveCamera(30, 1, 0.5, 8000);
        this.camera.position.set(0, 300, 300);

        this.controls = new OrbitControls(this.camera, this.renderer.domElement);
        this.controls.enableDamping = true;
        this.controls.dampingFactor = 0.08;
        this.controls.screenSpacePanning = false;
        this.controls.maxPolarAngle = 1.38;
        this.controls.mouseButtons = { LEFT: THREE.MOUSE.PAN, MIDDLE: THREE.MOUSE.DOLLY, RIGHT: THREE.MOUSE.ROTATE };
        this.controls.touches = { ONE: THREE.TOUCH.PAN, TWO: THREE.TOUCH.DOLLY_ROTATE };
        this.controls.addEventListener('start', () => { this.flight = null; });

        this.#lights();
        this.#table();

        this.boardGroup = new THREE.Group();       // terrain + water + skirt, y-scaled by exaggeration
        this.piecesGroup = new THREE.Group();      // owned by Pieces
        this.scene.add(this.boardGroup, this.piecesGroup);

        this.composer = new EffectComposer(this.renderer);
        this.composer.addPass(new RenderPass(this.scene, this.camera));
        this.tiltH = new ShaderPass(HorizontalTiltShiftShader);
        this.tiltV = new ShaderPass(VerticalTiltShiftShader);
        this.tiltH.uniforms.r.value = this.tiltV.uniforms.r.value = 0.52;
        this.composer.addPass(this.tiltH);
        this.composer.addPass(this.tiltV);
        this.composer.addPass(new OutputPass());
        this.miniature = true;

        this.raycaster = new THREE.Raycaster();
        this.#wireInput();

        this.resizeObserver = new ResizeObserver(() => this.#resize());
        this.resizeObserver.observe(host);
        this.#resize();

        this.clock = new THREE.Clock();
        this.renderer.setAnimationLoop(() => this.#frame());
    }

    // ── Board (relief) ──────────────────────────────────────────────────

    /**
     * Build the slab for a world rectangle. rect = { top, bottom, left, right } in world yards
     * (top = max X, bottom = min X, left = max Y, right = min Y).
     */
    async loadBoard(mapId, rect, onProgress) {
        this.#clearBoard();
        const board = {
            mapId, ...rect,
            width: (rect.left - rect.right),      // east-west yards
            depth: (rect.top - rect.bottom),      // north-south yards
            tiles: new Map(),
            baseZ: 0
        };
        board.span = Math.max(board.width, board.depth);
        this.board = board;

        const gx0 = Math.floor(32 - rect.top / TILE), gx1 = Math.floor(32 - rect.bottom / TILE);
        const gy0 = Math.floor(32 - rect.left / TILE), gy1 = Math.floor(32 - rect.right / TILE);
        const jobs = [];
        for (let gx = gx0; gx <= gx1; gx++)
            for (let gy = gy0; gy <= gy1; gy++) jobs.push({ gx, gy });

        let done = 0;
        let mapName = null;
        await pool(jobs, 6, async ({ gx, gy }) => {
            try {
                const r = await fetch(`/QuestAtlas/Relief?mapId=${mapId}&gx=${gx}&gy=${gy}`).then(x => x.json());
                if (r.exists) {
                    mapName = r.mapName;
                    board.tiles.set(`${gx},${gy}`, {
                        gx, gy, northX: r.northX, westY: r.westY, minZ: r.minZ, maxZ: r.maxZ,
                        heights: decodeFloats(r.heights), water: r.water
                    });
                }
            } catch { /* a missing tile becomes sea */ }
            onProgress?.(++done / jobs.length);
        });
        if (this.board !== board) return;          // a newer zone replaced us mid-load

        let minZ = Infinity;
        for (const t of board.tiles.values()) minZ = Math.min(minZ, t.minZ);
        if (!isFinite(minZ)) minZ = 0;
        if (board.tiles.size < jobs.length) minZ = Math.min(minZ, 0);   // open sea on the board
        board.baseZ = minZ - SLAB_DEPTH;
        board.mapName = mapName;

        this.#buildTerrain(jobs, mapName);
        this.#buildSkirt();
        this.#fitShadowsAndFog();
        this.boardGroup.scale.y = this.exaggeration;
        return board;
    }

    /** Terrain height (world Z) at a world position, bilinear over the compiled relief. */
    heightAt(x, y) {
        const b = this.board;
        if (!b) return 0;
        const t = b.tiles.get(`${Math.floor(32 - x / TILE)},${Math.floor(32 - y / TILE)}`);
        return t ? heightIn(t, x, y) : 0;
    }

    /** World (x, y, z) → scene position. z omitted → on the relief. lift = extra yards up (not exaggerated). */
    toScene(x, y, z = null, lift = 0) {
        const b = this.board;
        const ground = this.heightAt(x, y);
        const h = z == null ? ground : Math.max(z, ground);
        return new THREE.Vector3(
            (b.left - y) * S - b.width * S / 2,
            (h - b.baseZ) * S * this.exaggeration + lift * S,
            (b.top - x) * S - b.depth * S / 2);
    }

    /**
     * World (x, y, z) → scene position for a MODEL (building, prop): it rides the exaggerated
     * ground but keeps its true-scale offset from it, so nothing floats or sinks when the relief grows.
     */
    toSceneModel(x, y, z) {
        const b = this.board;
        const ground = this.heightAt(x, y);
        return new THREE.Vector3(
            (b.left - y) * S - b.width * S / 2,
            (ground - b.baseZ) * S * this.exaggeration + (z - ground) * S,
            (b.top - x) * S - b.depth * S / 2);
    }

    toWorld(v) {
        const b = this.board;
        return {
            x: b.top - (v.z + b.depth * S / 2) / S,
            y: b.left - (v.x + b.width * S / 2) / S
        };
    }

    onBoard(map, x, y) {
        const b = this.board;
        return b && map === b.mapId && x <= b.top && x >= b.bottom && y <= b.left && y >= b.right;
    }

    setExaggeration(e) {
        this.exaggeration = e;
        this.boardGroup.scale.y = e;
    }

    setMiniature(on) { this.miniature = on; }

    setWater(on) { if (this.water) this.water.visible = on; }

    // ── Camera ──────────────────────────────────────────────────────────

    /** Opening shot: a low sweep across the table that settles into the overview. */
    intro() {
        const span = this.board.span * S;
        this.camera.position.copy(sphericalTo(new THREE.Vector3(), span * 2.4, 1.42, -1.2));
        this.controls.target.set(0, 0, 0);
        this.flyTo(new THREE.Vector3(0, 0, span * 0.05), span * 1.15, 0.82, 0.12, 3600);
    }

    overview(ms = 1400) {
        const span = this.board.span * S;
        this.flyTo(new THREE.Vector3(0, 0, span * 0.05), span * 1.15, 0.82, null, ms);
    }

    /** Frame a set of scene points. */
    frame(points, ms = 1300) {
        if (!points.length) return;
        const box = new THREE.Box3().setFromPoints(points);
        const c = box.getCenter(new THREE.Vector3());
        const size = box.getSize(new THREE.Vector3());
        const r = Math.max(size.x, size.z, 40 * S * 6) * 1.45 + 30;
        this.flyTo(c, r, 0.9, null, ms);
    }

    flyTo(target, radius, phi = null, theta = null, ms = 1300) {
        const from = new THREE.Spherical().setFromVector3(this.camera.position.clone().sub(this.controls.target));
        const to = new THREE.Spherical(radius, phi ?? from.phi, theta ?? from.theta);
        // shortest way round
        while (to.theta - from.theta > Math.PI) to.theta -= Math.PI * 2;
        while (from.theta - to.theta > Math.PI) to.theta += Math.PI * 2;
        this.flight = {
            t0: performance.now(), ms,
            fromTarget: this.controls.target.clone(), toTarget: target.clone(),
            from, to
        };
    }

    // ── Internals ───────────────────────────────────────────────────────

    #lights() {
        this.scene.add(new THREE.HemisphereLight(0xfff0d8, 0x2a2016, 1.15));
        const sun = new THREE.DirectionalLight(0xffe0b0, 2.1);
        sun.position.set(-300, 420, -220);
        sun.castShadow = true;
        sun.shadow.mapSize.set(2048, 2048);
        sun.shadow.bias = -0.0004;
        sun.shadow.normalBias = 0.6;
        this.sun = sun;
        this.scene.add(sun, sun.target);
    }

    #table() {
        const c = document.createElement('canvas');
        c.width = 512; c.height = 512;
        const g = c.getContext('2d');
        g.fillStyle = '#3a281a';
        g.fillRect(0, 0, 512, 512);
        for (let i = 0; i < 260; i++) {                 // grain
            const y = Math.random() * 512;
            g.strokeStyle = `rgba(${20 + Math.random() * 40},${12 + Math.random() * 20},6,${0.15 + Math.random() * 0.25})`;
            g.lineWidth = 0.5 + Math.random() * 2.5;
            g.beginPath();
            g.moveTo(0, y);
            for (let x = 0; x <= 512; x += 32) g.lineTo(x, y + Math.sin(x / 70 + i) * 3);
            g.stroke();
        }
        const tex = new THREE.CanvasTexture(c);
        tex.colorSpace = THREE.SRGBColorSpace;
        tex.wrapS = tex.wrapT = THREE.RepeatWrapping;
        tex.repeat.set(10, 10);
        const table = new THREE.Mesh(
            new THREE.PlaneGeometry(6000, 6000),
            new THREE.MeshStandardMaterial({ map: tex, roughness: 0.75, metalness: 0.05 }));
        table.rotation.x = -Math.PI / 2;
        table.position.y = -0.05;
        table.receiveShadow = true;
        this.scene.add(table);
    }

    #clearBoard() {
        for (const o of [...this.boardGroup.children]) {
            this.boardGroup.remove(o);
            o.geometry?.dispose();
            if (o.material) {
                o.material.map?.dispose();
                o.material.dispose();
            }
        }
        this.water = null;
        this.board = null;
    }

    #buildTerrain(jobs, mapName) {
        const b = this.board;
        const loader = new THREE.TextureLoader();
        const waterPos = [];
        const pushQuad = (x0, x1, y0, y1, zWorld) => {
            // world rect → scene quad (unscaled height; the group scales y)
            const ax = (b.left - y0) * S - b.width * S / 2, bx = (b.left - y1) * S - b.width * S / 2;
            const az = (b.top - x0) * S - b.depth * S / 2, bz = (b.top - x1) * S - b.depth * S / 2;
            const h = (zWorld - b.baseZ) * S;
            waterPos.push(ax, h, az, bx, h, az, ax, h, bz, bx, h, az, bx, h, bz, ax, h, bz);
        };

        for (const { gx, gy } of jobs) {
            const northX = (32 - gx) * TILE, westY = (32 - gy) * TILE;
            // tile ∩ board, in world yards
            const xHi = Math.min(b.top, northX), xLo = Math.max(b.bottom, northX - TILE);
            const yHi = Math.min(b.left, westY), yLo = Math.max(b.right, westY - TILE);
            if (xHi <= xLo || yHi <= yLo) continue;

            const t = b.tiles.get(`${gx},${gy}`);
            if (!t) { pushQuad(xHi, xLo, yHi, yLo, 0); continue; }

            const pos = new Float32Array(SAMPLES * SAMPLES * 3);
            const uv = new Float32Array(SAMPLES * SAMPLES * 2);
            for (let j = 0; j < SAMPLES; j++)
                for (let i = 0; i < SAMPLES; i++) {
                    const k = j * SAMPLES + i;
                    const wx = clamp(northX - j * STEP, xLo, xHi);
                    const wy = clamp(westY - i * STEP, yLo, yHi);
                    pos[k * 3] = (b.left - wy) * S - b.width * S / 2;
                    pos[k * 3 + 1] = (heightIn(t, wx, wy) - b.baseZ) * S;
                    pos[k * 3 + 2] = (b.top - wx) * S - b.depth * S / 2;
                    uv[k * 2] = (westY - wy) / TILE;
                    uv[k * 2 + 1] = 1 - (northX - wx) / TILE;
                }
            const idx = [];
            for (let j = 0; j < SAMPLES - 1; j++)
                for (let i = 0; i < SAMPLES - 1; i++) {
                    const a = j * SAMPLES + i, c = a + 1, d = a + SAMPLES, e = d + 1;
                    idx.push(a, d, c, c, d, e);
                }
            const geo = new THREE.BufferGeometry();
            geo.setAttribute('position', new THREE.BufferAttribute(pos, 3));
            geo.setAttribute('uv', new THREE.BufferAttribute(uv, 2));
            geo.setIndex(idx);
            geo.computeVertexNormals();

            const mat = new THREE.MeshStandardMaterial({ color: 0x8a7a5a, roughness: 0.97, metalness: 0 });
            if (mapName) {
                loader.load(`/WorldMap/Tile?map=${encodeURIComponent(mapName)}&row=${gy}&col=${gx}`, tex => {
                    tex.colorSpace = THREE.SRGBColorSpace;
                    tex.anisotropy = 8;
                    mat.map = tex;
                    mat.color.set(0xffffff);
                    mat.needsUpdate = true;
                });
            }
            const mesh = new THREE.Mesh(geo, mat);
            mesh.castShadow = mesh.receiveShadow = true;
            mesh.userData.ground = true;
            this.boardGroup.add(mesh);

            // water, per 33-yard chunk
            if (t.water) {
                const C = TILE / 16;
                for (let cr = 0; cr < 16; cr++)
                    for (let cc = 0; cc < 16; cc++) {
                        const w = t.water[cr * 16 + cc];
                        if (w == null) continue;
                        const x0 = Math.min(xHi, northX - cr * C), x1 = Math.max(xLo, northX - (cr + 1) * C);
                        const y0 = Math.min(yHi, westY - cc * C), y1 = Math.max(yLo, westY - (cc + 1) * C);
                        if (x0 > x1 && y0 > y1) pushQuad(x0, x1, y0, y1, w);
                    }
            }
        }

        if (waterPos.length) {
            const geo = new THREE.BufferGeometry();
            geo.setAttribute('position', new THREE.BufferAttribute(new Float32Array(waterPos), 3));
            geo.computeVertexNormals();
            this.water = new THREE.Mesh(geo, new THREE.MeshStandardMaterial({
                color: 0x2f6f8f, roughness: 0.15, metalness: 0.1, transparent: true, opacity: 0.72, side: THREE.DoubleSide
            }));
            this.water.receiveShadow = true;
            this.boardGroup.add(this.water);
        }
    }

    /** The cut earth around the board edge, so the zone reads as a model on the table. */
    #buildSkirt() {
        const b = this.board;
        const pts = [];
        const walk = (x0, y0, x1, y1) => {
            const n = Math.max(2, Math.ceil(Math.hypot(x1 - x0, y1 - y0) / STEP));
            for (let k = 0; k < n; k++) pts.push([x0 + (x1 - x0) * k / n, y0 + (y1 - y0) * k / n]);
        };
        walk(b.top, b.left, b.top, b.right);
        walk(b.top, b.right, b.bottom, b.right);
        walk(b.bottom, b.right, b.bottom, b.left);
        walk(b.bottom, b.left, b.top, b.left);
        pts.push(pts[0]);

        const pos = [], col = [];
        const top = new THREE.Color(0x6e5034), bottom = new THREE.Color(0x24180f);
        for (const [x, y] of pts) {
            const sx = (b.left - y) * S - b.width * S / 2, sz = (b.top - x) * S - b.depth * S / 2;
            pos.push(sx, (this.heightAt(x, y) - b.baseZ) * S, sz, sx, 0, sz);
            col.push(top.r, top.g, top.b, bottom.r, bottom.g, bottom.b);
        }
        const idx = [];
        for (let k = 0; k < pts.length - 1; k++) {
            const a = k * 2, c = a + 1, d = a + 2, e = a + 3;
            idx.push(a, c, d, d, c, e);
        }
        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
        geo.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
        geo.setIndex(idx);
        geo.computeVertexNormals();
        const skirt = new THREE.Mesh(geo, new THREE.MeshStandardMaterial({ vertexColors: true, roughness: 1, side: THREE.DoubleSide }));
        skirt.castShadow = true;
        this.boardGroup.add(skirt);
    }

    #fitShadowsAndFog() {
        const span = this.board.span * S;
        const cam = this.sun.shadow.camera;
        cam.left = cam.bottom = -span * 0.75;
        cam.right = cam.top = span * 0.75;
        cam.near = 1;
        cam.far = span * 4;
        cam.updateProjectionMatrix();
        this.sun.position.set(-span * 0.55, span * 0.9, -span * 0.4);
        this.scene.fog.near = span * 1.6;
        this.scene.fog.far = span * 4.5;
        this.controls.maxDistance = span * 3;
        this.controls.minDistance = 4;
    }

    #wireInput() {
        const el = this.renderer.domElement;
        const ndc = new THREE.Vector2();
        let down = null, hoverQueued = null;

        const cast = (ev) => {
            const r = el.getBoundingClientRect();
            ndc.set(((ev.clientX - r.left) / r.width) * 2 - 1, -((ev.clientY - r.top) / r.height) * 2 + 1);
            this.raycaster.setFromCamera(ndc, this.camera);
            return { px: ev.clientX - r.left, py: ev.clientY - r.top };
        };
        const pickPiece = () => {
            const hit = this.raycaster.intersectObjects(this.pickables, false)[0];
            if (hit) return hit.object.userData.pick;
            return this.extraPick?.(this.raycaster) ?? null;   // e.g. figurines (ray-near, not triangles)
        };
        const pickGround = () => {
            const ground = this.boardGroup.children.filter(o => o.userData.ground);
            const hit = this.raycaster.intersectObjects(ground, false)[0];
            return hit && this.board ? this.toWorld(hit.point) : null;
        };

        el.addEventListener('pointermove', ev => {
            if (hoverQueued) return;
            hoverQueued = requestAnimationFrame(() => {
                hoverQueued = null;
                if (!this.board) return;
                const { px, py } = cast(ev);
                this.handlers.onHover?.(pickPiece(), px, py);
                const w = pickGround();
                this.handlers.onGround?.(w ? { x: w.x, y: w.y, z: this.heightAt(w.x, w.y) } : null);
            });
        });
        el.addEventListener('pointerleave', () => this.handlers.onHover?.(null));
        el.addEventListener('pointerdown', ev => { down = { x: ev.clientX, y: ev.clientY }; });
        el.addEventListener('pointerup', ev => {
            if (!down || ev.button !== 0) return;
            const moved = Math.hypot(ev.clientX - down.x, ev.clientY - down.y);
            down = null;
            if (moved > 5 || !this.board) return;
            cast(ev);
            this.handlers.onPick?.(pickPiece());
        });
    }

    #resize() {
        const w = this.host.clientWidth || 1, h = this.host.clientHeight || 1;
        this.renderer.setSize(w, h, false);
        this.renderer.domElement.style.width = '100%';
        this.renderer.domElement.style.height = '100%';
        this.labels.setSize(w, h);
        this.composer.setSize(w, h);
        this.camera.aspect = w / h;
        this.camera.updateProjectionMatrix();
        const blur = 2.2;
        this.tiltH.uniforms.h.value = blur / w;
        this.tiltV.uniforms.v.value = blur / h;
    }

    #frame() {
        if (this.flight) {
            const f = this.flight;
            const t = Math.min(1, (performance.now() - f.t0) / f.ms);
            const e = t < 0.5 ? 4 * t * t * t : 1 - Math.pow(-2 * t + 2, 3) / 2;
            this.controls.target.lerpVectors(f.fromTarget, f.toTarget, e);
            const s = new THREE.Spherical(
                lerp(f.from.radius, f.to.radius, e), lerp(f.from.phi, f.to.phi, e), lerp(f.from.theta, f.to.theta, e));
            this.camera.position.copy(sphericalTo(this.controls.target, s.radius, s.phi, s.theta));
            if (t >= 1) this.flight = null;
        }
        this.controls.update();
        this.piecesTick?.(this.camera);
        if (this.miniature) this.composer.render();
        else this.renderer.render(this.scene, this.camera);
        this.labels.render(this.scene, this.camera);
    }
}

// ── helpers ─────────────────────────────────────────────────────────────

function sphericalTo(target, radius, phi, theta) {
    return new THREE.Vector3().setFromSpherical(new THREE.Spherical(radius, phi, theta)).add(target);
}
/** Bilinear height inside one compiled tile (positions outside it clamp to its edge). */
function heightIn(t, x, y) {
    const fi = clamp((t.westY - y) / STEP, 0, SAMPLES - 1);
    const fj = clamp((t.northX - x) / STEP, 0, SAMPLES - 1);
    const i0 = Math.min(Math.floor(fi), SAMPLES - 2), j0 = Math.min(Math.floor(fj), SAMPLES - 2);
    const di = fi - i0, dj = fj - j0;
    const h = t.heights;
    const a = h[j0 * SAMPLES + i0], c = h[j0 * SAMPLES + i0 + 1];
    const d = h[(j0 + 1) * SAMPLES + i0], e = h[(j0 + 1) * SAMPLES + i0 + 1];
    return (a * (1 - di) + c * di) * (1 - dj) + (d * (1 - di) + e * di) * dj;
}
const clamp = (v, a, b) => v < a ? a : v > b ? b : v;
const lerp = (a, b, t) => a + (b - a) * t;

function decodeFloats(b64) {
    const bin = atob(b64);
    const bytes = new Uint8Array(bin.length);
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i);
    return new Float32Array(bytes.buffer);
}

async function pool(items, n, fn) {
    let next = 0;
    const workers = Array.from({ length: Math.min(n, items.length) }, async () => {
        while (next < items.length) await fn(items[next++]);
    });
    await Promise.all(workers);
}

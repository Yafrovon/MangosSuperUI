// render.js — rendering layer.
//
// Sections:
//   1. Constants and palette
//   2. Material factories (lit/flat aware, r162 ColorManagement)
//   3. Lighting rig (ambient/hemi/sun/fill + sky dome + ground plane)
//   4. CameraRig (camera + OrbitControls + walk-mode look state)
//   5. WalkMode helpers (terrain snap + forward collision)
//   6. safeDispose (recursive geometry/material/texture cleanup)
//   7. Viewport (renderer, scene assembly, animate loop, resize, input dispatch)

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';
import { EffectComposer } from 'three/addons/postprocessing/EffectComposer.js';
import { RenderPass } from 'three/addons/postprocessing/RenderPass.js';
import { OutlinePass } from 'three/addons/postprocessing/OutlinePass.js';
import { OutputPass } from 'three/addons/postprocessing/OutputPass.js';
import { DepthPrepass } from './collision.js';

// ─────────────────────────────────────────────────────────────────────────────
// 1. Palette constants
// ─────────────────────────────────────────────────────────────────────────────

export const SKY_TOP = 0x2a4f8a;
export const SKY_HORIZON = 0xe8a840;
export const FOG_COLOR = 0xc49a50;

// ─────────────────────────────────────────────────────────────────────────────
// 2. Material factories
// ─────────────────────────────────────────────────────────────────────────────
//
// r162 note: MeshStandardMaterial responds to physical-light intensities.
// Earlier values tuned for r128 legacy lights look dim, so lighting in
// LightingRig is re-tuned. Materials here unchanged in structure.

let litMode = true;
let wireframeOn = false;
let maxAnisotropyVal = 1;

export function setLitMode(v) { litMode = !!v; }
export function isLitMode() { return litMode; }
export function setWireframe(v) { wireframeOn = !!v; }
export function isWireframe() { return wireframeOn; }
export function setMaxAnisotropy(v) { maxAnisotropyVal = v; }
export function maxAnisotropy() { return maxAnisotropyVal; }

export function makeTerrainMaterial(opts) {
    opts = opts || {};
    if (litMode) {
        return new THREE.MeshStandardMaterial({
            map: opts.map || null,
            color: opts.color || 0xffffff,
            side: THREE.FrontSide,
            roughness: 0.85,
            metalness: 0.0,
            fog: true,
            wireframe: wireframeOn
        });
    }
    return new THREE.MeshBasicMaterial({
        map: opts.map || null,
        color: opts.color || 0xffffff,
        side: THREE.FrontSide,
        fog: true,
        wireframe: wireframeOn
    });
}

export function makeDoodadMaterial(opts) {
    opts = opts || {};
    if (litMode) {
        return new THREE.MeshStandardMaterial({
            map: opts.map || null,
            color: opts.color || 0x808080,
            side: opts.side || THREE.DoubleSide,
            alphaTest: opts.alphaTest || 0,
            transparent: opts.transparent || false,
            depthWrite: true,
            roughness: 0.7,
            metalness: 0.0,
            fog: true
        });
    }
    return new THREE.MeshBasicMaterial({
        map: opts.map || null,
        color: opts.color || 0x808080,
        side: opts.side || THREE.DoubleSide,
        alphaTest: opts.alphaTest || 0,
        transparent: opts.transparent || false,
        depthWrite: true,
        fog: true
    });
}

export function makeWmoMaterial(opts) {
    opts = opts || {};
    if (litMode) {
        return new THREE.MeshStandardMaterial({
            map: opts.map || null,
            color: opts.color || 0xaaaaaa,
            side: opts.side || THREE.FrontSide,
            alphaTest: opts.alphaTest || 0,
            transparent: opts.transparent || false,
            depthWrite: true,
            roughness: 0.5,
            metalness: 0.05,
            fog: true,
            wireframe: wireframeOn
        });
    }
    return new THREE.MeshBasicMaterial({
        map: opts.map || null,
        color: opts.color || 0xaaaaaa,
        side: opts.side || THREE.FrontSide,
        alphaTest: opts.alphaTest || 0,
        transparent: opts.transparent || false,
        depthWrite: true,
        fog: true,
        wireframe: wireframeOn
    });
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Lighting rig + sky dome + ground plane
// ─────────────────────────────────────────────────────────────────────────────
//
// r162 physical lights are roughly 10× brighter for the same intensity number
// as r128 legacy lights — but only for MeshStandardMaterial. With our scene
// using MeshStandardMaterial (litMode) the original intensities looked dim.
// New values calibrated to preserve the warm-afternoon feel.

export class LightingRig {
    constructor(scene) {
        this.scene = scene;
        this.ambient = new THREE.AmbientLight(0xffe8c8, 0.9);
        scene.add(this.ambient);

        this.hemi = new THREE.HemisphereLight(0xeebb66, 0x4a5530, 0.8);
        scene.add(this.hemi);

        this.sun = new THREE.DirectionalLight(0xffbb55, 3.5);
        this.sun.position.set(-100, 28, 50);
        scene.add(this.sun);

        this.fill = new THREE.DirectionalLight(0x99bbdd, 0.4);
        this.fill.position.set(60, 60, -40);
        scene.add(this.fill);

        this._lit = true;
    }

    setLit(v) {
        this._lit = !!v;
        this.ambient.intensity = this._lit ? 0.9 : 1.4;
        this.hemi.visible = this._lit;
        this.sun.intensity = this._lit ? 3.5 : 1.8;
        this.fill.visible = this._lit;
    }

    isLit() { return this._lit; }
}

export function addSkyDome(scene) {
    const skyGeo = new THREE.SphereGeometry(1400, 32, 16, 0, Math.PI * 2, 0, Math.PI * 0.5);
    const skyMat = new THREE.ShaderMaterial({
        side: THREE.BackSide, depthWrite: false,
        uniforms: {
            topColor: { value: new THREE.Color(SKY_TOP) },
            horizonColor: { value: new THREE.Color(SKY_HORIZON) },
            offset: { value: 10 },
            exponent: { value: 0.4 }
        },
        vertexShader: `
            varying vec3 vWorldPosition;
            void main() {
                vec4 wp = modelMatrix * vec4(position, 1.0);
                vWorldPosition = wp.xyz;
                gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
            }`,
        fragmentShader: `
            uniform vec3 topColor;
            uniform vec3 horizonColor;
            uniform float offset;
            uniform float exponent;
            varying vec3 vWorldPosition;
            void main() {
                float h = normalize(vWorldPosition + offset).y;
                gl_FragColor = vec4(mix(horizonColor, topColor, max(pow(max(h, 0.0), exponent), 0.0)), 1.0);
            }`
    });
    const sky = new THREE.Mesh(skyGeo, skyMat);
    sky.renderOrder = -1;
    sky.name = 'sky';
    scene.add(sky);
    return sky;
}

export function addGroundPlane(scene) {
    const geo = new THREE.PlaneGeometry(8000, 8000);
    const mat = new THREE.MeshBasicMaterial({ color: FOG_COLOR, transparent: true, opacity: 0.5 });
    const ground = new THREE.Mesh(geo, mat);
    ground.rotation.x = -Math.PI / 2;
    ground.position.y = -5;
    ground.renderOrder = -0.5;
    ground.name = 'ground';
    scene.add(ground);
    return ground;
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. CameraRig — camera + OrbitControls + walk-mode look state
// ─────────────────────────────────────────────────────────────────────────────
//
// Walk mode bypasses OrbitControls' rotation; instead we manage yaw/pitch
// directly via mouse delta. The state lives here so input handlers can
// mutate it without exporting globals.

export class CameraRig {
    constructor(canvas) {
        this.camera = new THREE.PerspectiveCamera(60, 1, 0.1, 2000);
        this.camera.position.set(0, 30, 80);

        this.controls = new OrbitControls(this.camera, canvas);
        this.controls.enableDamping = true;
        this.controls.dampingFactor = 0.1;
        this.controls.maxPolarAngle = Math.PI - 0.1;
        this.controls.minPolarAngle = 0.1;
        this.controls.minDistance = 1;
        this.controls.maxDistance = 5000;
        this.controls.enableZoom = false;
        // Free up right-click for walk-mode look — move pan to middle mouse.
        this.controls.mouseButtons = {
            LEFT: THREE.MOUSE.ROTATE,
            MIDDLE: THREE.MOUSE.PAN,
            RIGHT: null
        };

        this.walk = {
            mode: false,
            eyeHeight: 2,
            yaw: 0,
            pitch: 0,
            inited: false,
            rightMouseDown: false,
            lastMouseX: 0,
            lastMouseY: 0
        };
    }

    enterWalkMode() {
        this.walk.mode = true;
        this.walk.eyeHeight = 2;
        this.controls.enableRotate = false;
        this.controls.enablePan = false;
        const dir = new THREE.Vector3();
        this.camera.getWorldDirection(dir);
        this.walk.yaw = Math.atan2(dir.x, dir.z);
        this.walk.pitch = 0;
        this.walk.inited = true;
        this.applyWalkLook();
    }

    leaveWalkMode() {
        this.walk.mode = false;
        this.controls.enableRotate = true;
        this.controls.enablePan = true;
        this.walk.inited = false;
    }

    applyWalkLook() {
        const cy = Math.cos(this.walk.pitch);
        const lookDir = new THREE.Vector3(
            Math.sin(this.walk.yaw) * cy,
            Math.sin(this.walk.pitch),
            Math.cos(this.walk.yaw) * cy
        );
        this.controls.target.copy(this.camera.position).addScaledVector(lookDir, 10);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. WalkMode helpers
// ─────────────────────────────────────────────────────────────────────────────
//
// updateSnap(): cast ray downward, lift camera to eyeHeight above terrain.
// updateCollision(): cast forward from camera, push back if a WMO is closer
// than COLLISION_DISTANCE.

const COLLISION_DISTANCE = 3;

export class WalkMode {
    constructor(editor) {
        this.editor = editor;
        this._down = new THREE.Vector3(0, -1, 0);
        this._rayDown = new THREE.Raycaster();
        this._rayFwd = new THREE.Raycaster();
    }

    updateSnap() {
        const rig = this.editor.viewport.rig;
        if (!rig.walk.mode) return;
        if (!this.editor.tileGrid) return;

        const terrainMeshes = this.editor.tileGrid.terrainMeshes();
        if (terrainMeshes.length === 0) return;

        const origin = new THREE.Vector3(rig.camera.position.x, 500, rig.camera.position.z);
        this._rayDown.set(origin, this._down);
        this._rayDown.far = 1000;

        const hits = this._rayDown.intersectObjects(terrainMeshes);
        if (hits.length > 0) {
            const targetY = hits[0].point.y + rig.walk.eyeHeight;
            const dy = (targetY - rig.camera.position.y) * 0.3;
            rig.camera.position.y += dy;
            rig.controls.target.y += dy;
        }
    }

    updateCollision() {
        const rig = this.editor.viewport.rig;
        if (!rig.walk.mode) return;
        const stream = this.editor.objectStream;
        if (!stream) return;

        const dir = new THREE.Vector3();
        rig.camera.getWorldDirection(dir);
        dir.y = 0;
        if (dir.lengthSq() === 0) return;
        dir.normalize();

        this._rayFwd.set(rig.camera.position.clone(), dir);
        this._rayFwd.far = COLLISION_DISTANCE + 2;

        const wmoMeshes = stream.wmoMeshList();
        if (wmoMeshes.length === 0) return;

        const hits = this._rayFwd.intersectObjects(wmoMeshes, false);
        if (hits.length > 0 && hits[0].distance < COLLISION_DISTANCE) {
            const pushBack = COLLISION_DISTANCE - hits[0].distance;
            rig.camera.position.addScaledVector(dir, -pushBack);
            rig.controls.target.addScaledVector(dir, -pushBack);
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. safeDispose — recursive geometry/material/texture cleanup
// ─────────────────────────────────────────────────────────────────────────────
//
// Three.js does not auto-dispose geometry, materials, or textures when an
// object is removed from the scene. Call this whenever you remove() something
// you built yourself (i.e. not shared from a registry).

function disposeMaterial(mat) {
    if (!mat) return;
    if (mat.map) mat.map.dispose();
    if (mat.normalMap) mat.normalMap.dispose();
    if (mat.roughnessMap) mat.roughnessMap.dispose();
    if (mat.metalnessMap) mat.metalnessMap.dispose();
    if (mat.alphaMap) mat.alphaMap.dispose();
    mat.dispose();
}

export function safeDispose(root) {
    if (!root) return;
    root.traverse(function (c) {
        if (c.isMesh || c.isLine || c.isLineSegments || c.isPoints) {
            if (c.geometry) c.geometry.dispose();
            const mat = c.material;
            if (mat) {
                if (Array.isArray(mat)) mat.forEach(disposeMaterial);
                else disposeMaterial(mat);
            }
        }
        if (c.isInstancedMesh) {
            if (c.geometry) c.geometry.dispose();
            if (c.material) {
                if (Array.isArray(c.material)) c.material.forEach(disposeMaterial);
                else disposeMaterial(c.material);
            }
            if (typeof c.dispose === 'function') c.dispose();
        }
    });
}

// ─────────────────────────────────────────────────────────────────────────────
// 7. Viewport — renderer, scene assembly, animate loop, resize, input dispatch
// ─────────────────────────────────────────────────────────────────────────────
//
// Scene composition: sky dome → lighting rig → ground plane → tile grid
// (added later as it loads) → InstancePool's wmoGroup + doodadGroup
// (already added by ObjectStream.attachTo).

export class Viewport {
    constructor(editor, canvas) {
        this.editor = editor;
        this.canvas = canvas;
        editor.viewport = this;

        // Renderer
        this.renderer = new THREE.WebGLRenderer({
            canvas: canvas, antialias: true, alpha: true, powerPreference: 'high-performance'
        });
        this.renderer.setPixelRatio(window.devicePixelRatio);
        this.renderer.outputColorSpace = THREE.SRGBColorSpace;
        this.renderer.toneMapping = THREE.NoToneMapping;
        if ('useLegacyLights' in this.renderer) this.renderer.useLegacyLights = false;

        setMaxAnisotropy(this.renderer.capabilities.getMaxAnisotropy());

        // Scene + fog
        this.scene = editor.scene;
        this.scene.background = new THREE.Color(FOG_COLOR);
        this.scene.fog = new THREE.Fog(FOG_COLOR, 180, 550);

        // Lighting + sky + ground
        this.lighting = new LightingRig(this.scene);
        addSkyDome(this.scene);
        this.ground = addGroundPlane(this.scene);

        // Camera + controls
        this.rig = new CameraRig(canvas);

        // ── Postprocessing (Phase 4) ────────────────────────────────────────
        // EffectComposer with a RenderPass → OutlinePass → OutputPass chain.
        // OutlinePass takes a Vector2 (size); renderer hasn't been sized yet
        // so we pass a placeholder and update via resize() below.
        this.composer = new EffectComposer(this.renderer);
        this.composer.addPass(new RenderPass(this.scene, this.rig.camera));

        const initSize = new THREE.Vector2(canvas.clientWidth || 1, canvas.clientHeight || 1);
        this.outlinePass = new OutlinePass(initSize, this.scene, this.rig.camera);
        this.outlinePass.edgeStrength = 4.0;
        this.outlinePass.edgeThickness = 1.5;
        this.outlinePass.edgeGlow = 0.4;
        this.outlinePass.visibleEdgeColor.set(0xffaa00);
        this.outlinePass.hiddenEdgeColor.set(0x553300);
        this.composer.addPass(this.outlinePass);

        this.composer.addPass(new OutputPass());

        // Resize
        this._preFsHeight = 0;
        window.addEventListener('resize', () => this.resize());
        this.resize();
        this._preFsHeight = this.canvas.clientHeight || (window.innerHeight - 130);

        // FPS counter
        this._fpsCounter = 0;
        this.currentFps = 0;
        setInterval(() => { this.currentFps = this._fpsCounter * 2; this._fpsCounter = 0; }, 500);

        // Input dispatch — every event passes through ToolManager.
        this._bindInputDispatch();

        // Periodic-task counters
        this._progressiveTimer = 0;
        this._streamTimer = 0;
        this._helperTimer = 0;

        // External per-frame callbacks (registered by index.js)
        this._tickers = [];

        // Phase 6: depth prepass for collision-aware ghost rendering. Must
        // be constructed AFTER this.ground exists (added to exclusions in
        // its ctor). Idle when no consumers registered, so zero cost
        // outside placement mode.
        this.depthPrepass = new DepthPrepass(this.editor);
        // Sync to current CSS-pixel size (matches OutlinePass convention).
        // Viewport.resize() already ran above, sizing the renderer; the
        // initial DepthPrepass placeholder size needs the same update.
        this.depthPrepass.setSize(this.canvas.clientWidth || 1, this.canvas.clientHeight || 1);

        // Kick off the loop.
        this._animate = this._animate.bind(this);
        this._animate();
    }

    addTicker(fn) { this._tickers.push(fn); }

    resize() {
        const parent = this.canvas.parentElement;
        let w, h;
        if (document.fullscreenElement) {
            w = window.innerWidth;
            h = window.innerHeight;
        } else {
            this.canvas.style.width = '';
            this.canvas.style.height = '';
            w = parent.clientWidth;
            h = this._preFsHeight > 0
                ? this._preFsHeight
                : Math.max(400, window.innerHeight - 130);
            if (h > window.innerHeight - 60) h = window.innerHeight - 60;
        }
        this.rig.camera.aspect = w / h;
        this.rig.camera.updateProjectionMatrix();
        this.renderer.setSize(w, h);
        if (this.composer) this.composer.setSize(w, h);
        if (this.outlinePass) this.outlinePass.setSize(w, h);
        if (this.depthPrepass) this.depthPrepass.setSize(w, h);
        if (this._placementCtx) this._placementCtx.setSize(w, h);
    }

    rememberPreFullscreen() {
        this._preFsHeight = this.canvas.clientHeight;
    }

    _bindInputDispatch() {
        const tools = this.editor.tools;

        this.canvas.addEventListener('pointerdown', (ev) => {
            if (tools.active && typeof tools.active.onPointerDown === 'function') {
                try {
                    const handled = tools.active.onPointerDown(ev, this._ctx());
                    if (handled) { ev.stopImmediatePropagation(); }
                } catch (err) { console.error('onPointerDown', err); }
            }
        }, true);

        this.canvas.addEventListener('pointermove', (ev) => {
            if (tools.active && typeof tools.active.onPointerMove === 'function') {
                try { tools.active.onPointerMove(ev, this._ctx()); }
                catch (err) { console.error('onPointerMove', err); }
            }
        });

        this.canvas.addEventListener('pointerup', (ev) => {
            if (tools.active && typeof tools.active.onPointerUp === 'function') {
                try { tools.active.onPointerUp(ev, this._ctx()); }
                catch (err) { console.error('onPointerUp', err); }
            }
        });

        this.canvas.addEventListener('contextmenu', (ev) => {
            const rig = this.rig;
            if (rig.walk.mode) { ev.preventDefault(); return; }
            if (tools.active && typeof tools.active.onContextMenu === 'function') {
                try { if (tools.active.onContextMenu(ev)) ev.preventDefault(); }
                catch (err) { console.error('onContextMenu', err); }
            }
        });

        this.canvas.addEventListener('wheel', (ev) => {
            if (tools.active && typeof tools.active.onWheel === 'function') {
                try { tools.active.onWheel(ev, this._ctx()); }
                catch (err) { console.error('onWheel', err); }
            }
        }, { capture: true, passive: false });
    }

    _ctx() {
        return {
            camera: this.rig.camera,
            controls: this.rig.controls,
            scene: this.scene
        };
    }

    _animate() {
        requestAnimationFrame(this._animate);
        this._fpsCounter++;

        // In walk mode, the controls.target is being driven manually via
        // yaw/pitch — bypass OrbitControls' damping/rotation entirely.
        if (this.rig.walk.mode) {
            this.rig.camera.lookAt(this.rig.controls.target);
        } else {
            this.rig.controls.update();
        }

        // Walk-mode hooks (terrain snap + WMO collision)
        if (this.editor.walkModeImpl) {
            this.editor.walkModeImpl.updateSnap();
            this.editor.walkModeImpl.updateCollision();
        }

        // External tickers (movement, compass, hud, etc.)
        for (let i = 0; i < this._tickers.length; i++) {
            try { this._tickers[i](this); } catch (err) { console.error('ticker', err); }
        }

        // Progressive terrain check (~500ms)
        this._progressiveTimer++;
        if (this._progressiveTimer >= 30 && this.editor.tileGrid && this.editor.currentPreset) {
            this._progressiveTimer = 0;
            this.editor.tileGrid.checkProgressive(this.rig.controls.target);
            // Slide ground plane to follow the camera so its corners stay
            // hidden behind the fog horizon.
            if (this.ground) {
                this.ground.position.x = this.rig.controls.target.x;
                this.ground.position.z = this.rig.controls.target.z;
            }
        }

        // Object streaming check (~600ms, offset from terrain to spread load)
        this._streamTimer++;
        if (this._streamTimer >= 36 && this.editor.objectStream && this.editor.currentPreset && this.editor.tileGrid) {
            this._streamTimer = 0;
            this.editor.objectStream.pump(
                this.rig.camera.position.x,
                this.rig.camera.position.z,
                this.editor.tileGrid.globalMidHeight,
                this.editor.tileGrid.globalHeightScale
            );
        }

        // Tool helper update (~10Hz, cheap DOM positioning)
        this._helperTimer++;
        if (this._helperTimer >= 6) {
            this._helperTimer = 0;
            const active = this.editor.tools.active;
            if (active && typeof active.updateHelpers === 'function') {
                try { active.updateHelpers(); } catch (err) { console.error('updateHelpers', err); }
            }
        }

        // Phase 6: depth prepass for ghost collision viz. No-op when no
        // consumers are registered (i.e. outside placement mode).
        if (this.depthPrepass) this.depthPrepass.runIfNeeded();

        // Placement context: ghost depth pass + collision overlay. No-op
        // when no ghost is active. Must run AFTER the scene depth prepass
        // (so tSceneDepth is fresh) and BEFORE composer.render (so the
        // overlay pass has valid ghost depth to sample).
        if (this._placementCtx) this._placementCtx.runIfNeeded();

        this.composer.render();
    }
}
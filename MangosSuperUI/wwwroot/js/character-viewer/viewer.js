// Character Viewer — Three.js scene setup.
//
// Pure factory: takes a canvas, returns { scene, camera, renderer, controls }.
// No GLB knowledge here — caller adds whatever it wants to viewer.scene.
//
// Lighting setup mirrors Session A's inline module so the visual result is
// unchanged from the verified screenshot:
//   - AmbientLight 0xffffff @ 0.6
//   - DirectionalLight key, position (2,4,3) @ 0.8
//
// Render loop is unconditional (continues even before the GLB lands) so
// OrbitControls feel snappy from the moment the page loads.
//
// === Session O — animation mixer ===
// The viewer holds an optional THREE.AnimationMixer slot. After loading a
// character GLB, boot code (index.js) calls `viewer.attachMixer(character.root)`
// to wire it up; the render loop then drives it via mixer.update(delta) on
// every frame.
//
// We keep the mixer on the viewer rather than on the loaded character because
// the mixer must update from the render loop. Putting the responsibility here
// keeps the loop in one place — if a future feature wants to swap mixers (e.g.
// to drive an attachment GLB with its own clips), it goes through this same
// `attachMixer` hook.

import * as THREE from 'three';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

/**
 * Create a configured Three.js viewer attached to a canvas element.
 * @param {HTMLCanvasElement} canvas
 * @returns {{
 *   scene: THREE.Scene,
 *   camera: THREE.PerspectiveCamera,
 *   renderer: THREE.WebGLRenderer,
 *   controls: OrbitControls,
 *   fit: function,
 *   attachMixer: (root: THREE.Object3D) => THREE.AnimationMixer,
 *   getMixer: () => THREE.AnimationMixer | null,
 * }}
 */
export function createViewer(canvas) {
    const parent = canvas.parentElement;
    const initialWidth = parent.clientWidth || 800;
    const initialHeight = parent.clientHeight || 600;

    const renderer = new THREE.WebGLRenderer({ canvas, antialias: true });
    renderer.setPixelRatio(window.devicePixelRatio || 1);
    renderer.setSize(initialWidth, initialHeight, false);
    renderer.setClearColor(0x222222, 1);

    const scene = new THREE.Scene();

    const camera = new THREE.PerspectiveCamera(
        45,                                   // fov
        initialWidth / initialHeight,         // aspect
        0.01, 100                             // near/far
    );
    camera.position.set(0, 1.2, 3);

    const controls = new OrbitControls(camera, canvas);
    controls.target.set(0, 1, 0);
    controls.enableDamping = true;
    controls.dampingFactor = 0.08;
    controls.update();

    scene.add(new THREE.AmbientLight(0xffffff, 0.6));
    const key = new THREE.DirectionalLight(0xffffff, 0.8);
    key.position.set(2, 4, 3);
    scene.add(key);

    // ── Animation mixer (Session O) ──
    // `mixer` is set by attachMixer() once the character GLB is loaded.
    // Until then the render loop skips the mixer.update() call. The Clock
    // tracks wall-time deltas independent of frame rate; using its own
    // delta (rather than counting frames) means animation speed is correct
    // whether the page is running at 30 or 144 FPS.
    let mixer = null;
    const clock = new THREE.Clock();

    // ── Material-animation handles (m2fx) ──
    // The mixer covers skeletal motion and nothing else. An M2's colour, opacity
    // and UV-scroll tracks have no glTF representation, so they arrive as a
    // manifest in the GLB's extras and are replayed by m2fx.js — which needs a
    // per-frame tick and cannot get one from the mixer, because attachments
    // (weapons, helms, spaulders) are separate GLBs with no mixer of their own.
    //
    // They are driven off an accumulated elapsed time rather than a delta:
    // every M2 material track is a loop of known period, so absolute time keeps
    // two objects sharing a global sequence in phase with each other, which a
    // per-handle delta accumulator would not.
    const fxHandles = new Set();
    let elapsedMs = 0;

    // ── Render loop ──
    function frame() {
        // Mixer update first so any bones/attachments the controls or
        // renderer query downstream see this frame's animated transforms.
        // Three.js propagates matrix updates from skinned meshes itself,
        // but downstream code reading bone world positions (e.g. the
        // diagnostic panel) reads them after this point in the frame.
        const dt = clock.getDelta();
        if (mixer) mixer.update(dt);

        if (fxHandles.size > 0) {
            const dtMs = dt * 1000;
            elapsedMs += dtMs;
            for (const fx of fxHandles) {
                try {
                    // Absolute time drives the material loops; the delta and the
                    // camera drive the particle systems, which integrate motion
                    // and need the camera basis to billboard against.
                    fx.update(elapsedMs, dtMs, camera);
                } catch (err) {
                    // One bad manifest must not take the render loop with it.
                    console.warn('[viewer] m2fx handle failed; dropping it', err);
                    fxHandles.delete(fx);
                }
            }
        }

        controls.update();
        renderer.render(scene, camera);
        requestAnimationFrame(frame);
    }
    frame();

    // ── Resize handling ──
    // The render loop calls renderer.setSize() with updateStyle=false so we
    // never fight the CSS layout. setPixelRatio is sticky.
    const resize = () => {
        const w = parent.clientWidth, h = parent.clientHeight;
        if (w === 0 || h === 0) return;       // hidden tab etc.
        renderer.setSize(w, h, false);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
    };
    window.addEventListener('resize', resize);
    // The container changes size WITHOUT a window resize — on the Armor Forge the result panel
    // below the viewer grows after an import and the viewer flexes shorter, which left the drawing
    // buffer at the old aspect and squashed the character. Observe the container itself.
    if (typeof ResizeObserver !== 'undefined') new ResizeObserver(resize).observe(parent);

    /**
     * Fit camera to the bounding box of the scene. Useful after loading a GLB
     * if the default camera position doesn't frame it well. Not auto-called —
     * Session A's hardcoded (0, 1.2, 3) frames a human-sized T-pose fine.
     */
    function fit() {
        const box = new THREE.Box3().setFromObject(scene);
        if (box.isEmpty()) return;
        const size = box.getSize(new THREE.Vector3());
        const center = box.getCenter(new THREE.Vector3());

        const maxDim = Math.max(size.x, size.y, size.z);
        const fov = camera.fov * (Math.PI / 180);
        const dist = (maxDim / 2) / Math.tan(fov / 2) * 1.5;

        camera.position.set(center.x, center.y, center.z + dist);
        controls.target.copy(center);
        controls.update();
    }

    /**
     * Attach a THREE.AnimationMixer rooted at the given Object3D. Called by
     * boot code (index.js) after the character GLB loads. If a previous
     * mixer was attached, it is stopped (all running actions halted) before
     * the new one replaces it — relevant if a future feature reloads the
     * character without reloading the page.
     *
     * Returns the new mixer so the caller can immediately
     * `mixer.clipAction(clip).play()`.
     *
     * @param {THREE.Object3D} root
     * @returns {THREE.AnimationMixer}
     */
    function attachMixer(root) {
        if (mixer) mixer.stopAllAction();
        mixer = new THREE.AnimationMixer(root);
        return mixer;
    }

    function getMixer() { return mixer; }

    /**
     * Register an fx handle (the object installM2Fx returns) to be ticked every
     * frame with (elapsedMs, deltaMs, camera). Returns a function that unregisters it, so callers
     * that own a transient object (an attachment being replaced) can drop it
     * without knowing about the set.
     *
     * @param {{update: (ms:number)=>void}} handle
     * @returns {() => void}
     */
    function addFx(handle) {
        if (!handle || typeof handle.update !== 'function') return () => {};
        fxHandles.add(handle);
        return () => removeFx(handle);
    }

    /** Unregister a handle and dispose it if it knows how. */
    function removeFx(handle) {
        if (!handle) return;
        fxHandles.delete(handle);
        try { handle.dispose?.(); } catch { /* already gone */ }
    }

    /** Drop every registered handle — used when a viewer is torn down or reused. */
    function clearFx() {
        for (const handle of Array.from(fxHandles)) removeFx(handle);
    }

    return { scene, camera, renderer, controls, fit, attachMixer, getMixer, addFx, removeFx, clearFx };
}
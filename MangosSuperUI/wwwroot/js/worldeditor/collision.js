// collision.js — Phase 6: depth-prepass + collision-aware ghost shader.
//
// Sections:
//   1. DepthPrepass — manages the depth render target, runs the prepass each
//                     frame BEFORE composer.render(), maintains an exclusion
//                     list so non-collidable geometry (sky, ground, the ghost
//                     itself) doesn't pollute scene depth.
//   2. makeGhostMaterial — custom ShaderMaterial factory. Samples the depth
//                          texture from DepthPrepass and lerps an "ok" tint
//                          to a "clipping" red where the ghost fragment is
//                          BEHIND scene depth.
//   3. PlacementContext — dual-depth collision overlay for placement mode.
//                         Renders ghost depth into a second RT, then a
//                         post-process ShaderPass tints pixels where scene
//                         objects intrude into the ghost volume. Terrain
//                         stays fully textured; collisions glow from any angle.
//
// Why a depth prepass and not stencil
// -----------------------------------
// Three.js has no public stencil API (issue #7785, open since 2015) and the
// renderer caches state in ways that silently override raw `gl` calls.
// renderer.autoClearStencil defaults to true, so each composer pass wipes
// it. The canonical Three.js recipe — and the one the official
// webgl_depth_texture example uses — is a depth-texture render-target
// prepass + a custom ShaderMaterial that compares fragment depths in eye
// space. That's what this file implements.
//
// Performance
// -----------
// The prepass is a full extra scene render. It runs ONLY when at least one
// consumer is registered (registerConsumer / unregisterConsumer). In
// practice that means it's active only while a placement ghost exists —
// outside of placement mode there's zero overhead.

import * as THREE from 'three';
import { tagEntity } from './core.js';

// ─────────────────────────────────────────────────────────────────────────────
// 1. DepthPrepass
// ─────────────────────────────────────────────────────────────────────────────

export class DepthPrepass {
    constructor(editor) {
        this.editor = editor;
        this.viewport = editor.viewport;

        const canvas = this.viewport.canvas;
        const w = Math.max(1, canvas.clientWidth);
        const h = Math.max(1, canvas.clientHeight);

        // Render target — color attachment exists but is unused; depth is
        // the payload. NearestFilter on the depth texture is mandatory
        // (LinearFilter silently breaks sampling on some drivers — see the
        // discourse.threejs.org "Reading from depth texture" thread).
        // UnsignedInt248Type is the standard 24-bit depth + 8-bit stencil
        // combined format; we don't read the stencil byte. Stencil buffer
        // is disabled at the RT level anyway.
        this.renderTarget = new THREE.WebGLRenderTarget(w, h, {
            format: THREE.RGBAFormat,
            type: THREE.UnsignedByteType,
            depthBuffer: true,
            stencilBuffer: false
        });
        this.renderTarget.texture.generateMipmaps = false;
        this.renderTarget.texture.minFilter = THREE.NearestFilter;
        this.renderTarget.texture.magFilter = THREE.NearestFilter;

        this.depthTexture = new THREE.DepthTexture(w, h);
        this.depthTexture.format = THREE.DepthFormat;
        this.depthTexture.type = THREE.UnsignedInt248Type;
        this.depthTexture.minFilter = THREE.NearestFilter;
        this.depthTexture.magFilter = THREE.NearestFilter;
        this.renderTarget.depthTexture = this.depthTexture;

        // Consumers — ghost materials that need depth. We don't actually
        // use the references for anything beyond a count; they're a token
        // pattern so the prepass can skip itself when no one needs it.
        this.consumers = new Set();

        // Exclusions — objects to hide during the prepass to keep their
        // depth out of the captured texture. The ghost itself (whatever
        // calls registerConsumer) is auto-excluded. Sky has depthWrite:false
        // already and doesn't need exclusion. Ground plane writes depth
        // and would tint the ghost red unless hidden.
        this.exclusions = new Set();
        if (this.viewport.ground) this.exclusions.add(this.viewport.ground);

        // Sized via Viewport.resize.
        this._w = w;
        this._h = h;

        // Shared cheap material used via scene.overrideMaterial during the
        // prepass. MeshDepthMaterial is the standard "render only depth"
        // material; it writes depth and outputs gl_FragCoord.z encoded as
        // RGBA into the color attachment (which we don't read).
        this._depthMat = new THREE.MeshDepthMaterial();
        this._depthMat.depthPacking = THREE.BasicDepthPacking;
    }

    // Public API ──────────────────────────────────────────────────────────────

    registerConsumer(obj) {
        if (!obj) return;
        this.consumers.add(obj);
        this.exclusions.add(obj);
    }

    unregisterConsumer(obj) {
        if (!obj) return;
        this.consumers.delete(obj);
        this.exclusions.delete(obj);
    }

    addExclusion(obj) { if (obj) this.exclusions.add(obj); }
    removeExclusion(obj) { if (obj) this.exclusions.delete(obj); }

    isActive() { return this.consumers.size > 0; }

    getDepthTexture() { return this.depthTexture; }

    setSize(w, h) {
        if (w === this._w && h === this._h) return;
        this._w = w;
        this._h = h;
        // setSize handles both the color attachment and the depth texture
        // in place; the DepthTexture reference stays valid so shader
        // uniforms don't need updating.
        this.renderTarget.setSize(w, h);
    }

    // Run the prepass. Called by Viewport._animate BEFORE composer.render().
    // No-op when no consumers are registered.
    runIfNeeded() {
        if (this.consumers.size === 0) return;

        const renderer = this.viewport.renderer;
        const scene = this.viewport.scene;
        const camera = this.viewport.rig.camera;

        // Save & hide exclusions. Object3D.visible is read by the renderer
        // during draw-list assembly, so flipping it is enough — no need to
        // remove from the scene graph.
        const saved = [];
        for (const obj of this.exclusions) {
            if (!obj) continue;
            saved.push({ obj, wasVisible: obj.visible });
            obj.visible = false;
        }

        // Render to depth target. autoClear is on so the depth buffer is
        // cleared at the start of this render — good, exactly what we need.
        //
        // Optimization: scene.overrideMaterial swaps every material to a
        // cheap MeshDepthMaterial for the prepass. Saves the full PBR
        // shader cost on terrain. The depth output is what we want; the
        // color attachment is written but unused. If a future material has
        // alpha-test cutouts that materially affect the depth silhouette
        // (e.g. leaves), the override would skip them — but we don't have
        // those today.
        const savedOverride = scene.overrideMaterial;
        scene.overrideMaterial = this._depthMat;

        const prevTarget = renderer.getRenderTarget();
        renderer.setRenderTarget(this.renderTarget);
        renderer.clear(true, true, false); // color + depth, no stencil
        renderer.render(scene, camera);
        renderer.setRenderTarget(prevTarget);

        scene.overrideMaterial = savedOverride;

        // Restore visibility.
        for (let i = 0; i < saved.length; i++) {
            saved[i].obj.visible = saved[i].wasVisible;
        }

        // Update per-consumer shader uniforms that depend on camera state.
        // We do this every frame because near/far CAN change (OrbitControls
        // dolly, future LOD), and the resolution uniform must track resize.
        for (const c of this.consumers) {
            this._updateConsumerUniforms(c, camera);
        }
    }

    _updateConsumerUniforms(consumer, camera) {
        // Consumer is either a Mesh (single material) or a Group (multiple
        // child Meshes). Walk and update any material whose uniforms
        // include the depth-prepass set.
        consumer.traverse((child) => {
            if (!child.isMesh) return;
            const mat = child.material;
            if (!mat || !mat.uniforms) return;
            if (mat.uniforms.cameraNear) mat.uniforms.cameraNear.value = camera.near;
            if (mat.uniforms.cameraFar) mat.uniforms.cameraFar.value = camera.far;
            if (mat.uniforms.resolution) {
                mat.uniforms.resolution.value.set(this._w, this._h);
            }
        });
    }

    dispose() {
        this.renderTarget.dispose();
        this.depthTexture.dispose();
        if (this._depthMat) this._depthMat.dispose();
        this.consumers.clear();
        this.exclusions.clear();
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. makeGhostMaterial
// ─────────────────────────────────────────────────────────────────────────────
//
// Custom ShaderMaterial. Renders the ghost with:
//   - texture sample (optional) modulated by a cheap fake-Lambertian term
//     from the surface normal (gives shape readability without a full PBR
//     pipeline).
//   - intrusion overlay: red where the ghost fragment is BEHIND scene depth.
//
// Uniforms set on construction:
//   tMap         — texture or null (null = use baseColor)
//   baseColor    — RGB tint when tMap is null
//   clipColor    — RGB when fragment is intruding (red default)
//   opacity      — alpha (0.6 default)
//   epsilon      — small bias to avoid z-fighting flicker at exact intersection
//   intrusionScale — smoothstep width; bigger = softer color falloff
//
// Uniforms set per-frame by DepthPrepass._updateConsumerUniforms:
//   cameraNear, cameraFar, resolution
//
// Uniform tSceneDepth is bound to the DepthPrepass's DepthTexture at
// construction; setSize on the RT keeps the texture handle valid so this
// uniform never needs reassignment.
//
// Coordinate-space note:
// `perspectiveDepthToViewZ` (in <packing>) assumes a non-log depth buffer.
// We do not enable renderer.logarithmicDepthBuffer anywhere; if a future
// phase does, this shader breaks. Open issues mrdoob/three.js #13138 and
// #23072 describe the log-depth unprojection workaround.

const GHOST_VERTEX_SHADER = `
varying vec3 vWorldNormal;
varying vec2 vUv;

void main() {
    vUv = uv;
    // World-space normal for cheap diffuse term. Avoids the full lighting
    // chunks; we just want "this face is angled away from light" shading.
    vWorldNormal = normalize(mat3(modelMatrix) * normal);
    gl_Position  = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}
`;

const GHOST_FRAGMENT_SHADER = `
#include <packing>

uniform sampler2D tSceneDepth;
uniform sampler2D tMap;
uniform bool      useMap;

uniform vec3  baseColor;
uniform vec3  clipColor;
uniform float opacity;
uniform float epsilon;
uniform float intrusionScale;

uniform float cameraNear;
uniform float cameraFar;
uniform vec2  resolution;

varying vec3 vWorldNormal;
varying vec2 vUv;

void main() {
    // ── Surface sample (texture or base color) ─────────────────────────
    vec3 surfRGB = useMap ? texture2D(tMap, vUv).rgb : baseColor;

    // Cheap fake diffuse: dot(N, fixed-sun-dir). Matches the sun in
    // LightingRig's general direction (-100, 28, 50). Tuned for shape
    // readability, not physical accuracy.
    vec3 sun = normalize(vec3(-1.0, 1.5, 0.5));
    float nDotL = max(dot(normalize(vWorldNormal), sun), 0.0);
    float shade = 0.55 + 0.45 * nDotL;
    vec3  surfaceCol = surfRGB * shade;

    // ── Intrusion test ──────────────────────────────────────────────────
    // Read scene depth (everything excluding the ghost itself, as recorded
    // in the DepthPrepass). Both depths are in clip-space [0,1]; we
    // unproject to view-space Z (negative, in meters) for the comparison.
    vec2  screenUv      = gl_FragCoord.xy / resolution;
    float sceneFragZ    = texture2D(tSceneDepth, screenUv).x;
    float sceneViewZ    = perspectiveDepthToViewZ(sceneFragZ, cameraNear, cameraFar);

    float ghostFragZ    = gl_FragCoord.z;
    float ghostViewZ    = perspectiveDepthToViewZ(ghostFragZ, cameraNear, cameraFar);

    // intrusion > 0 means the ghost fragment is BEHIND the scene (its
    // viewZ is more negative — farther from camera). epsilon avoids
    // z-fighting tint flicker at exact-touch surfaces.
    float intrusion = sceneViewZ - ghostViewZ - epsilon;
    float k         = smoothstep(0.0, intrusionScale, intrusion);

    vec3 col = mix(surfaceCol, clipColor, k);
    gl_FragColor = vec4(col, opacity);
}
`;

export function makeGhostMaterial(opts) {
    opts = opts || {};

    const depthPrepass = opts.depthPrepass;       // required
    if (!depthPrepass) throw new Error('makeGhostMaterial: depthPrepass required');

    const tex = opts.map || null;
    const doubleSided = !!opts.doubleSided;
    const baseColor = new THREE.Color(opts.baseColor != null ? opts.baseColor : 0x88bbff);
    const clipColor = new THREE.Color(opts.clipColor != null ? opts.clipColor : 0xff3322);
    const opacity = opts.opacity != null ? opts.opacity : 0.6;
    const epsilon = opts.epsilon != null ? opts.epsilon : 0.05;
    const intrusionScale = opts.intrusionScale != null ? opts.intrusionScale : 0.5;

    const mat = new THREE.ShaderMaterial({
        vertexShader: GHOST_VERTEX_SHADER,
        fragmentShader: GHOST_FRAGMENT_SHADER,
        uniforms: {
            tSceneDepth: { value: depthPrepass.getDepthTexture() },
            tMap: { value: tex },
            useMap: { value: !!tex },
            baseColor: { value: baseColor },
            clipColor: { value: clipColor },
            opacity: { value: opacity },
            epsilon: { value: epsilon },
            intrusionScale: { value: intrusionScale },
            cameraNear: { value: 0.1 },
            cameraFar: { value: 2000 },
            resolution: { value: new THREE.Vector2(1, 1) }
        },
        transparent: true,
        depthWrite: false,
        side: doubleSided ? THREE.DoubleSide : THREE.FrontSide,
        fog: false
    });

    // Mark the material so DepthPrepass can recognize it during traversal.
    // Not strictly needed (the traversal checks uniforms by name) but it
    // makes the relationship discoverable in the inspector.
    mat.userData.isCollisionGhost = true;

    return mat;
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. PlacementContext — dual-depth collision overlay for placement mode
// ─────────────────────────────────────────────────────────────────────────────
//
// Problem: during ghost placement, you need to see collision from BOTH
// directions. The ghost shader (§2) tints red where the ghost is behind
// scene depth. But you also need to see where scene objects (terrain, trees,
// buildings) are in FRONT of the ghost — i.e. poking through the ghost's
// walls or floor. Without this, you can't see a tree trunk inside the
// building or terrain clipping through the foundation from the front.
//
// Solution: a second depth texture that captures only the ghost's depth,
// plus a post-process ShaderPass that overlays a collision tint on pixels
// where the scene depth is closer than the ghost depth. The terrain stays
// fully textured; the ghost stays transparent. Intersections glow from
// any viewing angle.
//
// Render order per frame (when active):
//   1. DepthPrepass.runIfNeeded()  → scene depth (excludes ghost) [existing]
//   2. PlacementContext._renderGhostDepth() → ghost depth (ghost only)
//   3. composer.render()
//      └─ RenderPass (normal scene + transparent ghost with red intrusion)
//      └─ CollisionOverlayPass (tints pixels where scene intruded into ghost)
//      └─ OutlinePass
//      └─ OutputPass
//
// Cost: one extra render target + one tiny render (ghost only, few tris) +
// one fullscreen ShaderPass. Zero material swapping, zero wireframes.
//
// The ShaderPass uses the EffectComposer's tDiffuse (the already-rendered
// frame) and two depth textures as inputs. It mixes the collision tint into
// the existing frame pixels.

import { ShaderPass } from 'three/addons/postprocessing/ShaderPass.js';

// ── Collision Overlay Shader ────────────────────────────────────────────────
//
// Fullscreen post-process: for each pixel, compare scene depth vs ghost
// depth. Where scene depth < ghost depth (scene object is closer to camera
// than ghost surface), overlay a colored tint. This is the "inverse" of the
// ghost's own red intrusion — it highlights where SCENE objects intrude
// into the ghost volume.

const OVERLAY_VERTEX = `
varying vec2 vUv;
void main() {
    vUv = uv;
    gl_Position = projectionMatrix * modelViewMatrix * vec4(position, 1.0);
}
`;

const OVERLAY_FRAGMENT = `
#include <packing>

uniform sampler2D tDiffuse;       // composed frame from previous passes
uniform sampler2D tSceneDepth;    // scene depth (excludes ghost)
uniform sampler2D tGhostDepth;    // ghost depth (ghost only)

uniform float cameraNear;
uniform float cameraFar;
uniform vec3  overlayColor;       // collision tint color
uniform float overlayStrength;    // 0–1 blend factor
uniform float epsilon;            // z-fighting avoidance bias

varying vec2 vUv;

void main() {
    vec4 texel = texture2D(tDiffuse, vUv);

    float sceneRaw = texture2D(tSceneDepth, vUv).x;
    float ghostRaw = texture2D(tGhostDepth, vUv).x;

    // ghostRaw == 1.0 means no ghost fragment at this pixel (cleared to far).
    // sceneRaw == 1.0 means no scene geometry at this pixel.
    // Skip both cases — no collision possible.
    if (ghostRaw >= 0.999 || sceneRaw >= 0.999) {
        gl_FragColor = texel;
        return;
    }

    // Convert to view-space Z for the comparison.
    float sceneZ = perspectiveDepthToViewZ(sceneRaw, cameraNear, cameraFar);
    float ghostZ = perspectiveDepthToViewZ(ghostRaw, cameraNear, cameraFar);

    // Scene intrusion: scene fragment is CLOSER to camera than ghost surface.
    // In view space, "closer" means less negative (sceneZ > ghostZ).
    // intrusion > 0 means the scene object is in front of the ghost.
    float intrusion = sceneZ - ghostZ - epsilon;
    float k = smoothstep(0.0, 0.5, intrusion);

    vec3 tinted = mix(texel.rgb, overlayColor, k * overlayStrength);
    gl_FragColor = vec4(tinted, texel.a);
}
`;

const CollisionOverlayShader = {
    uniforms: {
        tDiffuse: { value: null },
        tSceneDepth: { value: null },
        tGhostDepth: { value: null },
        cameraNear: { value: 0.1 },
        cameraFar: { value: 2000 },
        overlayColor: { value: new THREE.Color(0xff4422) },
        overlayStrength: { value: 0.65 },
        epsilon: { value: 0.05 }
    },
    vertexShader: OVERLAY_VERTEX,
    fragmentShader: OVERLAY_FRAGMENT
};

export class PlacementContext {
    constructor() {
        this._active = false;
        this._editor = null;
        this._ghostRef = null;

        // Ghost-only depth render target — same structure as DepthPrepass's RT.
        this._ghostRT = null;
        this._ghostDepthTex = null;
        this._depthMat = null;

        // Post-process pass — inserted into the EffectComposer chain.
        this._overlayPass = null;
        this._passInserted = false;
    }

    get active() { return this._active; }

    engage(editor, ghost) {
        if (this._active) return;
        this._active = true;
        this._editor = editor;
        this._ghostRef = ghost || null;
        this._ensureResources();
        this._insertPass();
    }

    setGhost(ghost) {
        this._ghostRef = ghost || null;
    }

    disengage() {
        if (!this._active) return;
        this._removePass();
        this._ghostRef = null;
        this._active = false;
        this._editor = null;
    }

    // Called on Viewport.resize — keep the ghost depth RT in sync.
    setSize(w, h) {
        if (this._ghostRT) this._ghostRT.setSize(w, h);
    }

    // Called each frame by DepthPrepass.runIfNeeded's caller (Viewport._animate).
    // Renders the ghost depth into the ghost-only RT, then updates the overlay
    // pass uniforms.
    runIfNeeded() {
        if (!this._active || !this._ghostRef || !this._editor) return;

        const viewport = this._editor.viewport;
        if (!viewport) return;
        const renderer = viewport.renderer;
        const scene = viewport.scene;
        const camera = viewport.rig.camera;

        // ── Render ghost depth ───────────────────────────────────────────
        // Hide everything except the ghost. Then render with depth override.
        // We do visibility-toggle on the top-level groups rather than per-mesh
        // — cheaper when there are thousands of instances.

        const savedVis = [];
        scene.children.forEach((child) => {
            if (child === this._ghostRef) return; // keep ghost visible
            savedVis.push({ obj: child, was: child.visible });
            child.visible = false;
        });

        const savedOverride = scene.overrideMaterial;
        scene.overrideMaterial = this._depthMat;

        const prevTarget = renderer.getRenderTarget();
        renderer.setRenderTarget(this._ghostRT);
        renderer.clear(true, true, false);
        renderer.render(scene, camera);
        renderer.setRenderTarget(prevTarget);

        scene.overrideMaterial = savedOverride;

        // Restore visibility.
        for (let i = 0; i < savedVis.length; i++) {
            savedVis[i].obj.visible = savedVis[i].was;
        }

        // ── Update overlay pass uniforms ─────────────────────────────────
        if (this._overlayPass) {
            const u = this._overlayPass.uniforms;
            u.cameraNear.value = camera.near;
            u.cameraFar.value = camera.far;
            // tSceneDepth is bound once in _ensureResources; stays valid
            // because DepthPrepass.setSize updates the texture in-place.
            // tGhostDepth same — RT.setSize keeps the reference valid.
        }
    }

    // ── Resource management ──────────────────────────────────────────────

    _ensureResources() {
        if (this._ghostRT) return;

        const viewport = this._editor.viewport;
        const w = Math.max(1, viewport.canvas.clientWidth);
        const h = Math.max(1, viewport.canvas.clientHeight);

        // Ghost depth RT — mirrors DepthPrepass structure.
        this._ghostRT = new THREE.WebGLRenderTarget(w, h, {
            format: THREE.RGBAFormat,
            type: THREE.UnsignedByteType,
            depthBuffer: true,
            stencilBuffer: false
        });
        this._ghostRT.texture.generateMipmaps = false;
        this._ghostRT.texture.minFilter = THREE.NearestFilter;
        this._ghostRT.texture.magFilter = THREE.NearestFilter;

        this._ghostDepthTex = new THREE.DepthTexture(w, h);
        this._ghostDepthTex.format = THREE.DepthFormat;
        this._ghostDepthTex.type = THREE.UnsignedInt248Type;
        this._ghostDepthTex.minFilter = THREE.NearestFilter;
        this._ghostDepthTex.magFilter = THREE.NearestFilter;
        this._ghostRT.depthTexture = this._ghostDepthTex;

        // Shared depth material.
        this._depthMat = new THREE.MeshDepthMaterial();
        this._depthMat.depthPacking = THREE.BasicDepthPacking;

        // Build the ShaderPass.
        this._overlayPass = new ShaderPass(CollisionOverlayShader);
        this._overlayPass.uniforms.tSceneDepth.value = viewport.depthPrepass.getDepthTexture();
        this._overlayPass.uniforms.tGhostDepth.value = this._ghostDepthTex;
        // tDiffuse is auto-bound by EffectComposer to the previous pass output.
    }

    _insertPass() {
        if (this._passInserted || !this._overlayPass) return;
        const composer = this._editor.viewport.composer;
        if (!composer) return;

        // Insert AFTER RenderPass (index 0) and BEFORE OutlinePass.
        // The composer passes array is: [RenderPass, OutlinePass, OutputPass].
        // We want: [RenderPass, CollisionOverlayPass, OutlinePass, OutputPass].
        // Insert at index 1.
        const passes = composer.passes;
        if (passes.length >= 2) {
            passes.splice(1, 0, this._overlayPass);
        } else {
            passes.push(this._overlayPass);
        }
        this._passInserted = true;
    }

    _removePass() {
        if (!this._passInserted || !this._overlayPass) return;
        const composer = this._editor.viewport ? this._editor.viewport.composer : null;
        if (composer) {
            const idx = composer.passes.indexOf(this._overlayPass);
            if (idx >= 0) composer.passes.splice(idx, 1);
        }
        this._passInserted = false;
    }

    dispose() {
        this.disengage();
        if (this._ghostRT) { this._ghostRT.dispose(); this._ghostRT = null; }
        if (this._ghostDepthTex) { this._ghostDepthTex.dispose(); this._ghostDepthTex = null; }
        if (this._depthMat) { this._depthMat.dispose(); this._depthMat = null; }
        this._overlayPass = null;
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. Helper: tag a ghost root for selection-filter exclusion
// ─────────────────────────────────────────────────────────────────────────────
//
// The ghost is in the scene graph and would be hit by selection raycasts
// against wmoGroup if anyone added it there. PlaceWmoTool already adds the
// ghost to viewport.scene directly (not wmoGroup), so this isn't strictly
// needed in Phase 6 — but it's cheap insurance and makes the intent
// explicit when reading the scene tree.

export function tagGhostRoot(root) {
    if (!root) return root;
    tagEntity(root, {
        type: 'ghost',
        id: 'ghost:transient',
        selectable: false,
        transformable: false,
        persistable: false,
        source: 'preview'
    });
    return root;
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. cloneAsGhost — build a ghost clone from an existing placed WMO Group
// ─────────────────────────────────────────────────────────────────────────────
//
// Creates a new THREE.Group that mirrors the source group's geometry but
// with ghost ShaderMaterials (same as placement ghosts). The clone shares
// geometry references (no GPU re-upload) and uses the source's already-loaded
// textures. Fully synchronous — no async Image.onload needed.
//
// Used by TransformGizmoManager: on drag-start, hide the real WMO, show the
// ghost clone at the same position, attach the gizmo to it. During the drag
// the ghost shows collision viz. On drag-end, destroy the ghost, unhide the
// real WMO, apply the new position.
//
// The returned group has the same world transform as the source and is
// tagged as a ghost root (selectable:false). The caller must:
//   1. Add it to the scene
//   2. Register it as a depth-prepass consumer
//   3. Engage PlacementContext with it
//   4. On teardown: unregister consumer, disengage context, remove+dispose

export function cloneAsGhost(sourceGroup, depthPrepass) {
    if (!sourceGroup || !depthPrepass) return null;

    const ghost = new THREE.Group();
    ghost.name = 'drag-ghost:' + (sourceGroup.name || 'unknown');

    sourceGroup.traverse((c) => {
        if (!c.isMesh) return;

        const origMat = c.material;
        const tex = origMat && origMat.map ? origMat.map : null;
        const doubleSided = origMat ? (origMat.side === THREE.DoubleSide) : false;

        const ghostMat = makeGhostMaterial({
            depthPrepass: depthPrepass,
            map: tex,
            baseColor: tex ? 0xffffff : 0x6699cc,
            doubleSided: doubleSided
        });

        // Share geometry — no clone needed, read-only during the drag.
        const clone = new THREE.Mesh(c.geometry, ghostMat);
        // Copy the mesh's local transform within the group (submeshes
        // may have local offsets, though placed WMO children usually don't).
        clone.position.copy(c.position);
        clone.rotation.copy(c.rotation);
        clone.scale.copy(c.scale);
        ghost.add(clone);
    });

    // Copy the source group's world transform.
    ghost.position.copy(sourceGroup.position);
    ghost.rotation.copy(sourceGroup.rotation);
    ghost.scale.copy(sourceGroup.scale);

    tagGhostRoot(ghost);
    return ghost;
}

// Dispose a ghost clone. Disposes ghost materials but NOT the shared
// geometry or textures (those belong to the original placed WMO).
export function disposeGhostClone(ghost) {
    if (!ghost) return;
    if (ghost.parent) ghost.parent.remove(ghost);
    ghost.traverse((c) => {
        if (!c.isMesh) return;
        const mat = c.material;
        if (mat) {
            // Null out texture refs before dispose so we don't kill shared textures.
            if (mat.uniforms) {
                if (mat.uniforms.tMap) mat.uniforms.tMap.value = null;
                if (mat.uniforms.tSceneDepth) mat.uniforms.tSceneDepth.value = null;
            }
            mat.dispose();
        }
        // Do NOT dispose geometry — it's shared with the source group.
    });
}
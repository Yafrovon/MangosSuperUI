// Character Viewer — embeddable mount API.
//
// Refactor of index.js's `boot()` into a callable that the Items page
// (and any other host page) can use to embed the viewer without copying
// the import map / module wiring. The CharacterPreview page itself still
// uses index.js for backwards compatibility; this module is purely
// additive for new embedding callers.
//
// === API ===
//   mountCharacterViewer({ canvas, glbUrl, skinUrl?, statsEl?, onReady? })
//     → Promise<{ character, viewer, dispose, swap }>
//
//   swap({ glbUrl, skinUrl? })
//     → Promise<character>   (replaces the loaded GLB; old one is detached)
//
//   dispose()
//     → void                 (stops animations, removes panels, frees GL state)
//
// The returned object also exposes `cv` — the same shape as window.cv,
// so caller pages can rebind window.cv = handle.cv on swap if desired.
//
// === Why not just call boot() from items.js? ===
// boot() in index.js mounts a diagnostic panel and writes to a specific
// stats element — too noisy for the items page. embed.js skips both by
// default (callers can opt back in via options if they want).
//
// === Race switching ===
// The Items page lets the user toggle race/gender. Each toggle calls
// swap({ glbUrl, skinUrl }). swap() must:
//   1. Stop the running animation mixer (so it doesn't tick on a freed scene).
//   2. Remove the previous character root from viewer.scene.
//   3. Dispose all geometries/textures/materials on the old character to
//      avoid GPU memory leaks (Three.js doesn't GC GL objects).
//   4. Load the new GLB, attach mixer, mount animation control.
//   5. Re-cache original geoset state.
//
// The canvas element itself is reused — no need to recreate the WebGL
// context or OrbitControls.

import { createViewer } from './viewer.js';
import { loadCharacterGlb } from './loader.js';
import * as compositor from './compositor.js';
import * as dresser from './dresser.js';
import * as geosetRules from './geoset-rules.js';
import * as equip from './equip.js';
import { mountAnimationControl } from './animation-control.js';

/**
 * Mount a character viewer onto a canvas.
 *
 * @param {{
 *   canvas: HTMLCanvasElement,
 *   glbUrl: string,
 *   skinUrl?: string,
 *   statsEl?: HTMLElement | null,
 *   onReady?: (handle: object) => void,
 * }} opts
 * @returns {Promise<{
 *   cv: object,
 *   character: object,
 *   viewer: object,
 *   swap: (next: { glbUrl: string, skinUrl?: string }) => Promise<object>,
 *   dispose: () => void,
 * }>}
 */
export async function mountCharacterViewer(opts) {
    const { canvas, statsEl } = opts;
    if (!canvas) throw new Error('mountCharacterViewer: canvas is required');

    // Set data-* up front so equip.js's loadDefaultSkin can find the skin URL.
    canvas.dataset.glbUrl = opts.glbUrl;
    if (opts.skinUrl) canvas.dataset.skinUrl = opts.skinUrl;

    const viewer = createViewer(canvas);

    let character = null;
    let animationControl = null;

    /**
     * Load (or reload) the character GLB and rewire animation control.
     * Used both for initial mount and for race-swap.
     */
    async function loadInto(glbUrl, skinUrl) {
        // Update dataset BEFORE awaiting so loadDefaultSkin (which reads
        // canvas.dataset.skinUrl) picks up the new URL on the very next
        // call — important for swap() where a re-equip might fire mid-load.
        canvas.dataset.glbUrl = glbUrl;
        if (skinUrl) canvas.dataset.skinUrl = skinUrl;

        // Tear down previous character — see swap() docstring for why this
        // matters (GL memory + mixer-on-freed-scene).
        if (character) {
            // Invalidate the equip module's base-skin bitmap cache. The
            // cache is keyed by NOTHING (singleton) — it just remembers
            // the first base-skin bitmap it ever loaded. Without this
            // clear, every race swap after the first re-uses the original
            // race's skin pixels, producing the "Orc body, Human skin"
            // bug visible after the items-page integration shipped.
            equip.clearSkinCache();

            // Stop the prior animation mixer so it doesn't tick during the
            // brief window where the old root is detached but the new one
            // isn't loaded yet.
            const oldMixer = viewer.getMixer();
            if (oldMixer) oldMixer.stopAllAction();

            if (animationControl) {
                animationControl.destroy();
                animationControl = null;
            }

            viewer.scene.remove(character.root);
            disposeCharacter(character);
        }

        character = await loadCharacterGlb(glbUrl);
        viewer.scene.add(character.root);
        cacheOriginalState(character);

        if (character.animations.length > 0) {
            const mixer = viewer.attachMixer(character.root);
            animationControl = mountAnimationControl({
                canvasEl: canvas,
                mixer,
                animations: character.animations,
                defaultClipName: 'Stand',
            });
        }

        if (statsEl) writeStats(statsEl, character);

        return character;
    }

    await loadInto(opts.glbUrl, opts.skinUrl);

    // Build the cv-shaped object exposed on the handle. The Items page
    // re-binds window.cv to this so devtools poking still works the same
    // way as on the CharacterPreview page.
    function buildCv() {
        return {
            scene: character.root,
            bones: character.bones,
            attachments: character.attachments,
            geosets: character.geosets,
            geosetList: character.geosetList,
            gltf: character.gltf,
            viewer,
            compositor,
            dresser,
            geosetRules,
            equip,
            character,
            mixer: viewer.getMixer(),
            animationControl,
            animations: character.animations,
        };
    }

    const handle = {
        get cv() { return buildCv(); },
        get character() { return character; },
        viewer,

        async swap(next) {
            await loadInto(next.glbUrl, next.skinUrl);
            return character;
        },

        dispose() {
            if (animationControl) {
                animationControl.destroy();
                animationControl = null;
            }
            const mixer = viewer.getMixer();
            if (mixer) mixer.stopAllAction();
            if (character) {
                viewer.scene.remove(character.root);
                disposeCharacter(character);
                character = null;
            }
        },
    };

    if (opts.onReady) opts.onReady(handle);
    return handle;
}

// ── Helpers ────────────────────────────────────────────────────────────

/**
 * Same caching as index.js boot() — see that file's cacheOriginalState
 * docstring. Duplicated here rather than imported because index.js doesn't
 * export it (and we don't want to refactor that file just for this).
 */
function cacheOriginalState(character) {
    for (const m of character.geosetList) {
        const cat = m.userData?.geosetCategory;
        const variant = m.userData?.geosetVariant;
        const isBodySkinned =
            cat === 0 ? variant === 0 :
                cat === 7 ? false :
                    true;

        if (!isBodySkinned && m.material) {
            if (Array.isArray(m.material)) {
                m.material = m.material.map(mat => mat.clone());
            } else {
                m.material = m.material.clone();
            }
        }

        m.userData._originalVisible = m.visible;
        const mats = Array.isArray(m.material) ? m.material : [m.material];
        for (const mat of mats) {
            if (mat?.map) {
                m.userData._originalMap = mat.map;
                break;
            }
        }
    }
}

/**
 * Free GPU resources for a character. Three.js doesn't dispose these
 * automatically when a node is removed from its parent — explicit
 * .dispose() calls on geometries/materials/textures are required to
 * release GL buffers. Without this, each race swap leaks ~20-30 MB of
 * VRAM (bones × textures × dimensions adds up fast).
 */
function disposeCharacter(character) {
    character.root.traverse((n) => {
        if (n.geometry) n.geometry.dispose?.();
        if (n.material) {
            const mats = Array.isArray(n.material) ? n.material : [n.material];
            for (const m of mats) {
                if (m.map) m.map.dispose?.();
                m.dispose?.();
            }
        }
    });
}

/**
 * Optional stats panel writer — same content as index.js's stats block.
 * Only invoked if the caller passed a statsEl.
 */
function writeStats(statsEl, character) {
    const lines = [
        `bones: ${character.boneList.length}`,
        `attachments: ${character.attachmentList.length}`,
        `geosets: ${character.geosetList.length}`,
        `animations: ${character.animations.length}`,
    ];
    statsEl.textContent = lines.join('\n');
}
// Character Viewer — entry point.
//
// Reads a canvas element's `data-glb-url` attribute and boots the Three.js
// viewer + GLTF loader against it. Mounts the diagnostic panel.
//
// Exposes `window.cv = { scene, bones, attachments, geosets, gltf, ... }`
// once the model is loaded, so a developer can poke around in DevTools
// without knowing which module owns which array.
//
// All UI / debug interactions live in diagnostic.js. This file does just
// the bootstrap: parse the canvas, load the GLB, wire up window.cv, mount
// the diagnostic panel, and write basic stats into the sidebar.

import { createViewer } from './viewer.js';
import { loadCharacterGlb } from './loader.js';
import * as compositor from './compositor.js';
import * as dresser from './dresser.js';
import * as geosetRules from './geoset-rules.js';
import * as equip from './equip.js';
import { mountDiagnosticPanel } from './diagnostic.js';
import { mountAnimationControl } from './animation-control.js';

const canvas = document.getElementById('char-preview-canvas');
const stats = document.getElementById('char-preview-stats');

if (!canvas) {
    console.warn('[character-viewer] no #char-preview-canvas found, nothing to do');
} else {
    const glbUrl = canvas.dataset.glbUrl;
    if (!glbUrl) {
        if (stats) stats.textContent = 'No data-glb-url on canvas.';
        console.error('[character-viewer] canvas is missing data-glb-url');
    } else {
        boot(canvas, glbUrl, stats);
    }
}

async function boot(canvasEl, glbUrl, statsEl) {
    const viewer = createViewer(canvasEl);

    try {
        const character = await loadCharacterGlb(glbUrl);
        viewer.scene.add(character.root);

        // Cache load-time visibility and textures so "reset to default"
        // operations have a true baseline to return to. Has to happen here
        // (boot time) before anything has had a chance to mutate things.
        cacheOriginalState(character);

        // ── Animation mixer (Session O) ──────────────────────────────────
        // If the server baked any AnimationClips into the GLB, wire up a
        // THREE.AnimationMixer rooted at the character. The mixer is owned
        // by the viewer (so its render loop drives mixer.update); we just
        // attach it here. Boot code DOESN'T start a clip directly —
        // mountAnimationControl does that, so the picker stays in sync
        // with the playing state from frame 1.
        let mixer = null;
        let animationControl = null;
        if (character.animations.length > 0) {
            mixer = viewer.attachMixer(character.root);
            animationControl = mountAnimationControl({
                canvasEl,
                mixer,
                animations: character.animations,
                defaultClipName: 'Stand',
            });
        }

        // Stash references for console + diagnostic panel + Session C/D
        // consumers. The diagnostic module reads from window.cv.
        window.cv = {
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
            mixer,                  // null if no animations baked
            animationControl,       // null if no animations baked
            animations: character.animations,
        };

        // Always-on diagnostic panel — floats top-right over the canvas.
        // `?debug=regions` query param expands the region-paint subsection
        // by default; everything else is collapsed for first-load tidiness.
        const params = new URLSearchParams(window.location.search);
        const debugMode = params.get('debug');
        mountDiagnosticPanel({
            canvasEl,
            character,
            viewer,
            compositor,
            dresser,
            equip,
            initialFocus: debugMode === 'regions' ? 'regions' : null,
        });

        // Short stats line for the sidebar — same content as Session A's
        // verification block so the existing handoff doc references still hold.
        if (statsEl) {
            const lines = [
                `bones: ${character.boneList.length}`,
                `attachments: ${character.attachmentList.length}`,
                ...character.attachmentList.slice(0, 8).map(n => `  ${n.name}`),
            ];
            if (character.attachmentList.length > 8) {
                lines.push(`  …(+${character.attachmentList.length - 8} more)`);
            }
            lines.push(`geosets: ${character.geosetList.length}`);
            lines.push(...character.geosetList.slice(0, 6).map(m =>
                `  ${m.name} (id=${m.userData.geosetId})`));
            if (character.geosetList.length > 6) {
                lines.push(`  …(+${character.geosetList.length - 6} more)`);
            }
            // Animations (Session O)
            lines.push(`animations: ${character.animations.length}`);
            for (const clip of character.animations) {
                lines.push(`  ${clip.name} (${clip.duration.toFixed(2)}s)`);
            }
            if (debugMode) {
                lines.push('', `debug: ${debugMode}`);
            }
            statsEl.textContent = lines.join('\n');
        }

        // One-shot console dump of categories present in the model —
        // useful for at-a-glance "do we have a cat 14 in this race or not."
        dumpGeosetCategories(character);

        console.log('[character-viewer] loaded', character.gltf);
        console.log('[character-viewer]',
            'bones=', character.boneList.length,
            'attachments=', character.attachmentList.length,
            'geosets=', character.geosetList.length,
            'animations=', character.animations.length);
    } catch (err) {
        const msg = (err && err.message) ? err.message : String(err);
        if (statsEl) statsEl.textContent = 'load error:\n' + msg;
        console.error('[character-viewer] load error', err);
    }
}

/**
 * Cache load-time state on each geoset so the diagnostic panel's
 * "restore to default" actions have a real baseline to return to.
 *
 *   userData._originalVisible — boolean at load time
 *   userData._originalMap     — first non-null material.map at load time
 *
 * Also clones the material on non-body-skinned geosets (hair, ears) so
 * they have INDEPENDENT material instances from the body. Without this,
 * vanilla M2 → GLB output gives hair geometry and body geometry the
 * SAME material object — meaning any later `mat.map = X` swap on the
 * body bleeds the new texture onto hair too. Cloning at load time
 * decouples them; the compositor's category/variant filter then has the
 * effect it advertises.
 *
 * Filter mirrors compositor.isBodySkinnedGeoset; if you change one,
 * change the other. (TODO: extract to a shared helper.)
 */
function cacheOriginalState(character) {
    for (const m of character.geosetList) {
        const cat = m.userData?.geosetCategory;
        const variant = m.userData?.geosetVariant;
        const isBodySkinned =
            cat === 0 ? variant === 0 :
                cat === 7 ? false :
                    true;

        // Decouple hair/ear materials from the shared body material so the
        // body atlas swap only affects body geometry. Three.js Material.clone()
        // makes a new material with the same map texture (texture pixels are
        // shared, just the .map POINTER becomes independent — exactly what we
        // need, since the compositor only swaps the pointer).
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
 * One-shot console dump of categories + visible-at-load variants. Helps
 * answer "does this model even have a cat 14" without writing a console
 * one-liner each time. Same content the diagnostic panel reads internally
 * when building the category picker.
 */
function dumpGeosetCategories(character) {
    const byCat = new Map();
    for (const m of character.geosetList) {
        const cat = m.userData?.geosetCategory;
        if (typeof cat !== 'number') continue;
        if (!byCat.has(cat)) byCat.set(cat, []);
        byCat.get(cat).push({
            id: m.userData.geosetId,
            visible: m.visible,
        });
    }
    const sorted = [...byCat.entries()].sort((a, b) => a[0] - b[0]);
    console.group('[character-viewer] geoset categories in this model');
    for (const [cat, meshes] of sorted) {
        const vis = meshes.filter(x => x.visible).length;
        console.log(`  cat ${cat}: ${meshes.length} meshes (${vis} visible)`,
            meshes.map(x => `${x.id}${x.visible ? '*' : ''}`).join(' '));
    }
    console.groupEnd();
}
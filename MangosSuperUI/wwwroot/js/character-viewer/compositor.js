// Character Viewer — Compositor.
//
// Builds the character's body skin texture by compositing armor textures into
// a single canvas, then uploads that canvas as a THREE.CanvasTexture and swaps
// it onto the SkinnedMesh material(s).
//
// Why a single canvas: vanilla WoW characters share ONE body skin texture across
// all body-skinned geosets (head/torso/arms/legs excluding accessory geosets
// like hair). When you equip plate armor, the chest artwork has to be painted
// into the TorsoUpper and TorsoLower regions of that one shared texture. WMV /
// wow.export / the WoW client all do this with a canvas blit at equip time.
//
// Session C status:
//   - paintDebugRegions: WORKING (used to verify region rectangles)
//   - paintBodyAtlas:    WORKING shape but consumers TODO
//   - applyBodyTexture:  WORKING (texture swap onto materials)
//
// flipY is risk #8 — OPEN. THREE.CanvasTexture defaults to flipY=true. If
// the debug regions appear vertically inverted vs. where they should be on
// the character, set tex.flipY = false on the CanvasTexture and re-test.

import * as THREE from 'three';
import { eachRegion, REGIONS, SLOT_TO_REGION } from './region-rects.js';

const ATLAS_SIZE = 256;   // vanilla character body atlas is 256×256

// ────────────────────────────────────────────────────────────────────
// Debug harness — paint each region a distinct flat color.
// Used to empirically verify region rectangles.
// ────────────────────────────────────────────────────────────────────

/**
 * Build a debug atlas that fills each region with its REGION_DEBUG_COLORS
 * value (everything else stays transparent so the underlying skin shows
 * through). Returns a THREE.CanvasTexture and also applies it to all body
 * geosets of the character.
 *
 * Use this to verify region-rects.js — call it, look at the character,
 * see whether each colored patch lands on the body part its key promises.
 *
 * @param {object} character  Result of loadCharacterGlb()
 * @param {object} [opts]
 * @param {number} [opts.alpha=0.85]  How opaque the debug colors are. Lower
 *                                    values let the skin texture show through.
 * @param {boolean} [opts.includeBackground=false]
 *                                    If true, fills the entire atlas with the
 *                                    base skin first, then overlays colors.
 *                                    If false (default), only the regions are
 *                                    painted — useful when you want to ignore
 *                                    skin pigmentation while iterating.
 * @returns {THREE.CanvasTexture}
 */
export function paintDebugRegions(character, opts = {}) {
    const alpha = opts.alpha ?? 0.85;

    const canvas = document.createElement('canvas');
    canvas.width = ATLAS_SIZE;
    canvas.height = ATLAS_SIZE;
    const ctx = canvas.getContext('2d');

    // Translucent dark base so any unpainted area is obvious (not pure black
    // — that conflicts with hair/eyebrow textures rendered out of the same
    // atlas on some races).
    ctx.fillStyle = 'rgba(40, 40, 40, 0.5)';
    ctx.fillRect(0, 0, ATLAS_SIZE, ATLAS_SIZE);

    // Paint each region in its assigned color.
    ctx.globalAlpha = alpha;
    for (const { key, rect, color } of eachRegion()) {
        ctx.fillStyle = color;
        ctx.fillRect(rect.x, rect.y, rect.w, rect.h);

        // Label each region with its key so we can tell them apart even if
        // two adjacent colors are similar.
        ctx.globalAlpha = 1.0;
        ctx.fillStyle = '#000';
        ctx.font = 'bold 11px monospace';
        ctx.fillText(key, rect.x + 3, rect.y + 12);
        ctx.globalAlpha = alpha;
    }
    ctx.globalAlpha = 1.0;

    const tex = new THREE.CanvasTexture(canvas);
    tex.colorSpace = THREE.SRGBColorSpace;
    // Risk #8: M2 UV convention has row 0 at the bottom of the atlas;
    // CanvasTexture.flipY defaults to true. The first paintDebugRegions test
    // showed regions painted upside-down (atlas top = model legs/feet), so
    // we disable flipY to match the M2 convention.
    tex.flipY = false;

    applyBodyTexture(character, tex);
    return tex;
}

// ────────────────────────────────────────────────────────────────────
// Production compositor — Session C will wire this to real item textures.
// ────────────────────────────────────────────────────────────────────

/**
 * Paint a SINGLE region for unambiguous verification. Everything else on the
 * atlas stays a neutral dark gray, so the only colored pixels on the model
 * show exactly which body parts that one rectangle maps to.
 *
 * Better than paintDebugRegions for iterating one region at a time — no
 * overlap confusion from adjacent regions or accessory geosets.
 *
 * @param {object} character    Result of loadCharacterGlb()
 * @param {string} regionKey    Key into REGIONS (e.g. 'armUpper')
 * @returns {THREE.CanvasTexture | null}
 */
export function paintSingleRegion(character, regionKey) {
    const rect = REGIONS[regionKey];
    if (!rect) {
        console.warn('[compositor] unknown region key', regionKey,
            '— valid:', Object.keys(REGIONS));
        return null;
    }

    const canvas = document.createElement('canvas');
    canvas.width = ATLAS_SIZE;
    canvas.height = ATLAS_SIZE;
    const ctx = canvas.getContext('2d');

    // Neutral dark background — non-painted body still has shape, won't
    // be confused with a paint color.
    ctx.fillStyle = '#3a3a3a';
    ctx.fillRect(0, 0, ATLAS_SIZE, ATLAS_SIZE);

    // The one region under test — bright magenta.
    ctx.fillStyle = '#ff00ff';
    ctx.fillRect(rect.x, rect.y, rect.w, rect.h);

    // Label and coordinates inside the region for at-a-glance reading.
    ctx.fillStyle = '#ffffff';
    ctx.font = 'bold 14px monospace';
    ctx.fillText(regionKey, rect.x + 4, rect.y + 16);
    ctx.fillText(`${rect.x},${rect.y} ${rect.w}x${rect.h}`,
        rect.x + 4, rect.y + 32);

    const tex = new THREE.CanvasTexture(canvas);
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.flipY = false;   // M2 UV convention; see risk #8

    applyBodyTexture(character, tex);
    return tex;
}

/**
 * Composite armor textures into the character's body skin canvas and apply
 * it as the new base texture for all body-skinned geosets.
 *
 * The `layers` argument is the resolved m_texture[] array, where each entry
 * already pairs a slot index with its decoded BLP→PNG ImageBitmap. The
 * SLOT_TO_REGION map handles the dispatch to the right rectangle.
 *
 * @param {object} character          Result of loadCharacterGlb()
 * @param {HTMLImageElement|ImageBitmap} skinImg   Base skin (e.g. HumanMaleSkin00_00.png)
 * @param {Array<{ slot: number, image: HTMLImageElement|ImageBitmap }>} layers
 *        Ordered list of (slot, image) pairs from ItemDisplayInfo m_texture[].
 *        Each layer is painted on top of the base skin into its slot's region.
 * @returns {THREE.CanvasTexture}
 */
export function paintBodyAtlas(character, skinImg, layers) {
    const canvas = document.createElement('canvas');
    canvas.width = ATLAS_SIZE;
    canvas.height = ATLAS_SIZE;
    const ctx = canvas.getContext('2d');

    // Base layer — full character skin. If the skin failed to load (e.g.
    // dev environment without skin PNGs generated yet), fall back to a
    // neutral skin-tone fill so the armor textures still show *something*
    // to paint over. Better than crashing in ctx.drawImage(null, ...).
    if (skinImg) {
        ctx.drawImage(skinImg, 0, 0, ATLAS_SIZE, ATLAS_SIZE);
    } else {
        console.warn('[compositor] paintBodyAtlas: no base skin — filling neutral');
        ctx.fillStyle = '#c8a888';
        ctx.fillRect(0, 0, ATLAS_SIZE, ATLAS_SIZE);
    }

    // Overlay each equipped texture into its region. Unknown slots are
    // silently skipped — defensive against future m_texture array growth.
    for (const { slot, image } of layers) {
        if (!image) continue;
        const regionKey = SLOT_TO_REGION[slot];
        if (!regionKey) continue;
        const rect = REGIONS[regionKey];
        ctx.drawImage(image, rect.x, rect.y, rect.w, rect.h);
    }

    const tex = new THREE.CanvasTexture(canvas);
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.flipY = false;   // M2 UV convention; see paintDebugRegions comment

    applyBodyTexture(character, tex);
    return tex;
}

/**
 * Layered variant of paintBodyAtlas — takes an ORDERED list of per-item
 * layer groups. Each group is painted in sequence onto the same canvas,
 * so later groups can overlay earlier ones in the same region.
 *
 * Why this matters: vanilla 1.12 textures often paint OVERLAY strips that
 * are designed to land on top of a base item's texture in the same slot.
 * The canonical example is plate belts: the belt's slot-5 (`_LU`) texture
 * is a thin gold strip at the top of the leg region with the rest of the
 * 128×64 region transparent. It's meant to overlay on top of the
 * legplate's slot-5 full-leg gold pant texture — when both are equipped,
 * you should see full gold pants WITH a fancy belt-top accent.
 *
 * The single-dict merge in equipMultiple's earlier implementation
 * last-write-wins'd the belt over the legplate, replacing the full pant
 * paint with a thin strip plus transparency, leaving the rest of the leg
 * showing bare skin. Per-item layered painting fixes this — the legplate
 * paints its full gold first, then the belt's transparent-rest strip
 * draws over it leaving the gold intact except where the belt has
 * opaque pixels.
 *
 * @param {object} character
 * @param {HTMLImageElement|ImageBitmap} skinImg
 * @param {Array<Array<{ slot: number, image: HTMLImageElement|ImageBitmap }>>} itemLayers
 *        Outer array: one entry per equipped item, IN PAINT ORDER (low layer
 *        first, high layer last). Inner array: that item's slot textures.
 * @returns {THREE.CanvasTexture}
 */
export function paintBodyAtlasLayered(character, skinImg, itemLayers) {
    const canvas = document.createElement('canvas');
    canvas.width = ATLAS_SIZE;
    canvas.height = ATLAS_SIZE;
    const ctx = canvas.getContext('2d');

    if (skinImg) {
        ctx.drawImage(skinImg, 0, 0, ATLAS_SIZE, ATLAS_SIZE);
    } else {
        console.warn('[compositor] paintBodyAtlasLayered: no base skin — filling neutral');
        ctx.fillStyle = '#c8a888';
        ctx.fillRect(0, 0, ATLAS_SIZE, ATLAS_SIZE);
    }

    for (const layers of itemLayers) {
        for (const { slot, image } of layers) {
            if (!image) continue;
            const regionKey = SLOT_TO_REGION[slot];
            if (!regionKey) continue;
            const rect = REGIONS[regionKey];
            ctx.drawImage(image, rect.x, rect.y, rect.w, rect.h);
        }
    }

    const tex = new THREE.CanvasTexture(canvas);
    tex.colorSpace = THREE.SRGBColorSpace;
    tex.flipY = false;

    applyBodyTexture(character, tex);
    return tex;
}

/**
 * Swap a texture onto every body-skinned geoset's material.
 *
 * The "body-skinned geosets" filter is conservative for now (everything that
 * isn't obviously a hair/ear/eyebrow accessory) — Session C will refine if
 * we find materials that shouldn't be swapped.
 *
 * @param {object} character
 * @param {THREE.Texture} texture
 */
export function applyBodyTexture(character, texture) {
    for (const m of character.geosetList) {
        const cat = m.userData?.geosetCategory;
        const variant = m.userData?.geosetVariant;
        if (typeof cat !== 'number') continue;
        if (!isBodySkinnedGeoset(cat, variant)) continue;

        const mats = Array.isArray(m.material) ? m.material : [m.material];
        for (const mat of mats) {
            if (mat && 'map' in mat) {
                mat.map = texture;
                mat.needsUpdate = true;
            }
        }
    }
}

/**
 * Whether a specific (category, variant) geoset paints into the shared body
 * skin atlas.
 *
 * For vanilla 1.12 HumanMale.m2 — verified empirically May 15 2026 — the
 * geosetList contains cats [0, 1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 12, 13, 15].
 * Most of these UV-map into the shared 256×256 body-skin atlas, with two
 * structural exceptions called out below.
 *
 *   cat 0  — base body + HAIR. This category contains the face/torso/limbs
 *           base mesh AS variant 0, AND ~13 hair-style variants 1-13. The
 *           base body UV-maps into the body atlas; the hair variants do
 *           NOT — they have their own UVs reaching into atlas areas the
 *           slot pipeline doesn't populate (CharSections-managed hair
 *           texture region). Swapping the composited body atlas onto hair
 *           geometry causes plate/cloth armor textures to bleed onto the
 *           hair strands.
 *           → ONLY variant 0 of cat 0 gets the body atlas swap. Hair
 *             variants 1-13 keep whatever texture the GLB shipped with.
 *   cat 1-3 face features (chin/jaw/mouth) — UV-sample into the faceUpper/
 *           faceLower regions of the atlas. We don't paint those regions
 *           yet (CharSections-driven head pipeline is future work), so
 *           they fall through to base skin pigment. Still safe to swap.
 *   cat 4  gloves/hand armor — paints in hand region
 *   cat 5  boots/feet — paints in legLower + foot regions
 *   cat 7  ears / scalp accessory — UV-samples atlas regions the body slot
 *           pipeline doesn't populate. Keep original M2 texture.
 *   cat 8  sleeves — paints in armUpper region
 *   cat 9  long pants — paints in legUpper + legLower regions
 *   cat 10 mid-torso shirt-tail — paints in torsoLower region
 *   cat 11 short pants/waistband — paints in legUpper region
 *   cat 12 tabard bottom
 *   cat 13 robe — paints in legUpper + legLower regions
 *   cat 15 shoulders (+ optional cape)
 *
 * === History ===
 *
 * Pre-Session-J revisions of this function excluded cats 5, 7, and 17.
 * Session J flipped it to `return true` unconditionally and boots rendered
 * correctly but the head/scalp showed flesh-blob artifacts.
 *
 * Session K (first pass): re-excluded cat 7. Didn't fix the head artifact.
 * Diagnostic picker reveals that what we'd been calling "the head/scalp
 * blob" is actually the cat-0 HAIR variants getting painted with armor
 * textures because cat 0 was treated as one category. Refactored: cat 0
 * variant 0 stays in (base body needs the atlas), cat 0 variants 1+ stay
 * out (hair geometry keeps its original texture). Lips are cat 1/2/3
 * variants other than the default — see docs (those still need the atlas
 * for skin tone but render the lip-line geometry on top).
 *
 * @param {number} category  Geoset ID divided by 100.
 * @param {number} [variant] Geoset ID mod 100. Used to distinguish base
 *                           body from hair within cat 0.
 */
function isBodySkinnedGeoset(category, variant) {
    // Cat 0 contains both base body (variant 0) AND hair styles (variants
    // 1-13 on HumanMale). Only the base body needs the composited atlas;
    // hair has its own UVs that don't sample from body slot regions, so
    // painting the atlas onto hair would bleed armor textures onto hair
    // strands.
    if (category === 0) return variant === 0;

    // Cat 7 (ears / scalp accessory) UV-samples atlas regions the body
    // slot pipeline doesn't paint. Keep its original M2 texture.
    if (category === 7) return false;

    return true;
}
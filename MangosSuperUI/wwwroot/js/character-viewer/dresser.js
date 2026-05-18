// Character Viewer — Dresser.
//
// Owns geoset visibility (Session C) and weapon attachment (Session D).
//
// Session C — geoset filtering (THIS FILE):
//   When items are equipped, certain geosets must be hidden so armor sleeves
//   replace bare arms, helms replace hair, etc. WoW encodes this via:
//     category = geosetId / 100
//     variant  = geosetId % 100
//   The rule is "for each category controlled by an item, show only the
//   item's chosen variant (from ItemDisplayInfo.geosetGroup[N]); hide all
//   other variants in that category."
//
//   The (slot, category, group_index) mapping is in geoset-rules.js.
//
// Session D — weapons:
//   getAttachment(attachmentId) returns the THREE.Object3D parented under the
//   right bone. The caller .add()s a weapon GLB scene onto that node and it
//   inherits the bone transform automatically.
//
// All functions take a `character` object as returned by loader.js. No
// global state — multiple characters can coexist on a page (future feature).

import { resolveVisibleGeosets, resolveNakedGeosets } from './geoset-rules.js';

/**
 * Show only the given geosets, hide all others.
 *
 * @param {object} character     Result of loadCharacterGlb()
 * @param {Set<number>} visible  Set of geoset IDs to keep visible
 */
export function setVisibleGeosets(character, visible) {
    for (const m of character.geosetList) {
        const id = m.userData?.geosetId;
        if (typeof id !== 'number') { m.visible = false; continue; }
        m.visible = visible.has(id);
    }
}

/**
 * Show only the default-variant geoset for each category. This is the
 * "naked character" baseline — base body + default underwear/doublet/robe.
 *
 * @param {object} character
 */
export function showDefaultGeosets(character) {
    const visible = resolveNakedGeosets(character.geosetList);
    setVisibleGeosets(character, visible);
}

/**
 * Apply a list of equipped items to the geoset visibility state.
 *
 * Each item must have:
 *   - inventoryType: number (1=head, 5=chest, 7=pants, etc; see SLOT_RULES)
 *   - geosetGroup:   number[] of length up to 5, sourced from
 *                    ItemDisplayInfo.dbc m_geosetGroup[0..4]
 *
 * Items whose inventoryType has no SLOT_RULES entry, or whose rules have
 * empty `groups`, contribute nothing to geoset visibility — they only paint
 * into the texture atlas (handled by compositor.js).
 *
 * @param {object} character
 * @param {Array<{ inventoryType: number, geosetGroup: number[] }>} items
 */
export function applyItemFilters(character, items) {
    const visible = resolveVisibleGeosets(character.geosetList, items);
    setVisibleGeosets(character, visible);
}

/**
 * Get an attachment Object3D by semantic ID (HandRight=1, Helm=11, ...).
 * Session D will use this to mount weapon GLBs.
 *
 * @param {object} character
 * @param {number} attachmentId  See wowdev.wiki/M2#Attachments
 * @returns {THREE.Object3D | null}
 */
export function getAttachment(character, attachmentId) {
    return character.attachments[attachmentId] ?? null;
}

/**
 * Mount a child Object3D onto an attachment point.
 * Session D will use this for weapons. Helpful enough to be real now.
 *
 * @param {object} character
 * @param {number} attachmentId
 * @param {THREE.Object3D} child
 * @returns {boolean}  true on success, false if attachment ID missing
 */
export function mountAttachment(character, attachmentId, child) {
    const att = getAttachment(character, attachmentId);
    if (!att) return false;
    // Clear previous mount; one item per attachment slot.
    while (att.children.length) att.remove(att.children[0]);
    att.add(child);
    return true;
}
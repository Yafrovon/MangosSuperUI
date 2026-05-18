// Character Viewer — Equip glue.
//
// Wraps the two halves of dressing (geoset visibility + body atlas painting)
// into a single equip-an-item-by-displayId call. Fetches the dressing
// payload from /Items/ItemDressing, decodes the per-slot PNGs into
// ImageBitmaps, and hands them off to dresser + compositor.
//
// Usage:
//   import { equipDisplay, unequipAll } from './equip.js';
//   await equipDisplay(character, /*displayId*/ 12345, /*itemId*/ 2167);
//
// The character argument is the result of loader.loadCharacterGlb().
//
// === Multi-item dressing ===
// Real characters wear up to 11 items at once (head, chest, pants, etc).
// To dress a full set, build an `items` array and call equipMultiple()
// — it runs dresser.applyItemFilters with the FULL array (so robe-vs-pants
// arbitration works correctly) and composites every body-atlas slot
// from every item onto a single canvas.
//
// === Session K addendum (paint order) ===
// equipMultiple now sorts items into vanilla paint order and composites
// each item's slots as a SEPARATE layer onto the same canvas, so an item
// with a partially-transparent texture (like the belt's narrow gold
// strip at the top of slot 5) correctly overlays on top of a base item's
// texture in the same slot (the legplate's full-leg gold pant) instead
// of replacing it. The previous single-dict merge clobbered legplate
// paint when a belt or tabard was equipped over it. See compositor's
// paintBodyAtlasLayered for the why.
//
// === Session L addendum (helm + shoulder attachments) ===
// Helms and shoulders are NOT body-atlas items — they're standalone M2
// models that mount under bones via the attachment system (Session D
// plumbing). The server's ItemDressing response now includes an
// `attachments` map for inventoryType 1 (helm) and 3 (shoulders)
// pointing at GLB URLs. equipDisplay / equipMultiple fetch those GLBs
// and call dresser.mountAttachment with the correct attachment ID.
//
// Attachment-ID mapping comes from vanilla M2 docs (M2Reader.cs):
//   ID  5 → ShoulderRight   (file: RShoulder_*.mdx → ModelName2)
//   ID  6 → ShoulderLeft    (file: LShoulder_*.mdx → ModelName1)
//   ID 11 → Helm            (file: Helm_*.mdx       → ModelName1)
//
// If shoulders appear swapped at runtime (left armor on the right
// shoulder), the M2 doc and our DBC interpretation disagree on which
// shoulder ID is which — flip ATTACHMENT_SHOULDER_LEFT/RIGHT below.
//
// === Session M addendum (weapon attachments) ===
// Weapons (inventoryType 13/17/21/22/23/26/15/25/28) mount on the
// character's hand bones via attachment IDs 1 (HandRight) and 2
// (HandLeft). The server's ItemDressing response includes a "weapon"
// key in the attachments map when the item is a weapon; the client
// chooses the hand based on inventoryType (22 = off-hand → left,
// everything else → right).
//
// Crucially, the weapon's own M2 Attachment-0 ("ItemVisual0", the
// hilt/grip mount point) has been baked into the GLB's scene root
// translation by SaveGlb (GlbWriter.cs Session M), so we don't need
// any weapon-specific positioning on the client — we just
// mountAttachment(character, 1, weaponScene) and the hilt lands at
// the hand. Same code path as helms/shoulders.

import * as dresser from './dresser.js';
import * as compositor from './compositor.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { applyBlendSuffix } from './blend-suffix.js';

const ENDPOINT = '/Items/ItemDressing';

// ────────────────────────────────────────────────────────────────────
// Attachment IDs (M2 semantic, per wowdev.wiki/M2#Attachments).
// Confirmed present on HumanMale.m2 via cv.attachments dump.
//
// (combined.txt:12157, verified May 2026). The names below use M2/wiki
// terminology;(LEFT_WRIST/RIGHT_PALM/
// LEFT_PALM correspond to attachment IDs 0/1/2 here).
// ────────────────────────────────────────────────────────────────────
const ATTACHMENT_LEFT_WRIST = 0;         // shields mount here, NOT on the palm
const ATTACHMENT_HAND_RIGHT = 1;         // Session M
const ATTACHMENT_HAND_LEFT = 2;          // Session M
const ATTACHMENT_SHOULDER_RIGHT = 5;
const ATTACHMENT_SHOULDER_LEFT = 6;
const ATTACHMENT_HELM = 11;

// All attachment IDs the dressing pipeline touches — used by unequipAll
// to clear stale geometry when re-dressing.
const ALL_DRESSING_ATTACHMENT_IDS = [
    ATTACHMENT_HELM,
    ATTACHMENT_SHOULDER_LEFT,
    ATTACHMENT_SHOULDER_RIGHT,
    ATTACHMENT_HAND_RIGHT,                // Session M
    ATTACHMENT_HAND_LEFT,                 // Session M
    ATTACHMENT_LEFT_WRIST,                // shields (Session "shield grip")
];

// Vanilla weapon-like inventoryTypes (server emits attachments.weapon
// for these). Mirror of the server-side list in ItemsController.cs;
// keep in sync if a new ranged or held-item type is ever added.
//
//   13 = One-Hand            21 = Main Hand (e.g. Thunderfury — yes, really;
//   14 = Shield                  inventoryType is 21 despite "1H" tooltip)
//   17 = Two-Hand            22 = Off Hand
//   23 = Held in Off-Hand    26 = Ranged (bows/guns)
//   15 = Ranged (legacy)     25 = Thrown
//   28 = Relic
//
// Shields (14): rigid GLB just like weapons. Mount on the off-hand
// attachment point (see LEFT_HAND_INVENTORY_TYPES below). Same Session-M
// pipeline as weapons — server's EnsureGlb bakes Attachment-0 into the
// scene root, client mounts on hand bone, no shield-specific code needed
// at this layer.
const WEAPON_INVENTORY_TYPES = new Set([13, 14, 17, 21, 22, 23, 26, 15, 25, 28]);

// Which inventoryTypes go on the LEFT hand. Vanilla offhand slot fills
// with:
//   14 — Shield (held with shield-arm, geometry visible on outer forearm)
//   22 — Off Hand (one-hand weapons in the offhand: daggers, swords, etc.)
//   23 — Held in Off-Hand (paladin librams, shaman totems, druid idols)
// Everything else (1H/2H/Main Hand/Ranged) goes on the right hand.
//
// Originally this set was just {22}, missed in Session M alongside the
// server-side shield gap. Once the server started emitting attachments.weapon
// for shields, the client routed them to the right hand on top of any
// already-equipped weapon — visible as the shield occluding Quel'Serrar
// in the first post-fix screenshot. Adding 14 and 23 here completes the
// offhand routing.
const LEFT_HAND_INVENTORY_TYPES = new Set([14, 22, 23]);

// One shared GLTFLoader for the attachment GLB fetches. GLTFLoader is
// thread-safe for parallel loads via its internal Promise wrapping;
// reusing the instance avoids re-creating its DRACOLoader/KTX2 setup
// dance per call.
//
// Session M phase 2.5: every loaded attachment GLB gets applyBlendSuffix
// run on it so additive blade materials (Thunderfury), helm glow
// materials, etc. get the right three.js blend mode. Without this, the
// glTF transparent flag is set but the blend equation defaults to
// NormalBlending — which gives a translucent painted effect rather than
// the additive glow the M2 author intended.
const _gltfLoader = new GLTFLoader();
function loadGlb(url) {
    return new Promise((resolve, reject) => {
        _gltfLoader.load(url, gltf => {
            applyBlendSuffix(gltf.scene);
            resolve(gltf);
        }, undefined, err => reject(err));
    });
}

/**
 * Vanilla character body texture paint order (bottom layer first).
 *
 * Lower inventoryTypes paint first; later ones paint on top. The values
 * come from observing how vanilla 1.12 handles overlapping textures:
 *   - Pants (7) lay down the full leg gold.
 *   - Boots (8) paint over the lower leg + foot.
 *   - Chest (5) lays down full sleeves and torso.
 *   - Robe (20) replaces chest art (treated like chest, same priority).
 *   - Bracers (9) overlay on top of chest sleeve art.
 *   - Belt (6) overlays on top of pants/chest at the waist.
 *   - Gloves (10) overlay on top of bracers at the wrist.
 *   - Tabard (19) goes ON TOP of chest art.
 *   - Cape (16) goes on top of everything else.
 *
 * Items whose inventoryType isn't in this list paint LAST (after
 * everything listed) — defensive default for future item types.
 */
const PAINT_ORDER = {
    4: 1,   // shirt    (under chest)
    7: 2,   // pants/legs
    5: 3,   // chest
    20: 3,   // robe (same layer as chest)
    8: 4,   // boots
    9: 5,   // bracers
    6: 6,   // belt
    10: 7,   // gloves
    19: 8,   // tabard
    16: 9,   // cape
};

function paintOrder(invType) {
    return PAINT_ORDER[invType] ?? 100;
}

/**
 * Pick the attachment ID for a weapon/shield based on its inventoryType.
 *
 *   Shield (14)         → LEFT_WRIST  (attachment 0)
 *   Off-hand (22, 23)   → LEFT_PALM   (attachment 2)
 *   Everything else     → RIGHT_PALM  (attachment 1)
 *
 * Why is the shield case separate from off-hand? Because shields mount on
 * the FOREARM, not the palm. The model origin of a vanilla shield M2 is
 * positioned such that the grip-side of the shield sits at the wrist
 * bone — mounting on LEFT_PALM (where off-hand weapons go) puts the hand
 * INSIDE the shield body, which is exactly the visual bug that turned
 * up after we added shields to the routing.
 *
 *
 * LEFT_WRIST = 0 is the M2 attachment ID, same value our character M2
 * exposes via its attachment lookup.
 *
 * Why is this a function and not a lookup? In case a future patch wants
 * to support sheathed weapons (attachments 26/27), the dispatch lives
 * here and the rest of the code doesn't have to care.
 */
function weaponAttachmentId(inventoryType) {
    if (inventoryType === 14) return ATTACHMENT_LEFT_WRIST;
    return LEFT_HAND_INVENTORY_TYPES.has(inventoryType)
        ? ATTACHMENT_HAND_LEFT
        : ATTACHMENT_HAND_RIGHT;
}

/**
 * Mount the helm / shoulder / weapon GLB attachments declared in a
 * dressing payload. Replaces whatever was previously mounted on the
 * relevant attachment IDs (dresser.mountAttachment clears existing
 * children).
 *
 * Quietly no-ops for body-atlas items (the server returns an empty
 * `attachments` object for inventoryTypes that aren't helm / shoulder /
 * weapon).
 *
 * @param {object} character        loader.loadCharacterGlb() result
 * @param {Object<string,string>} attachments  e.g.
 *        { helm: "/...", shoulderLeft: "/...", shoulderRight: "/...", weapon: "/..." }
 * @param {number} [inventoryType]  Used ONLY to pick the hand for weapons.
 *        If omitted and `attachments.weapon` is present, defaults to right
 *        hand (mainhand). Helm and shoulders ignore this.
 * @returns {Promise<number>}       number of attachments successfully mounted
 */
async function mountAttachmentsFromPayload(character, attachments, inventoryType) {
    if (!attachments) return 0;

    // Resolve each key to its M2 attachment ID. Unknown keys are ignored
    // rather than thrown — keeps the client tolerant of future server
    // additions (e.g. "back" for cape attachment if we ever add one).
    const jobs = [];
    if (attachments.helm) {
        jobs.push({ url: attachments.helm, attId: ATTACHMENT_HELM, label: 'helm' });
    }
    if (attachments.shoulderLeft) {
        jobs.push({ url: attachments.shoulderLeft, attId: ATTACHMENT_SHOULDER_LEFT, label: 'shoulderLeft' });
    }
    if (attachments.shoulderRight) {
        jobs.push({ url: attachments.shoulderRight, attId: ATTACHMENT_SHOULDER_RIGHT, label: 'shoulderRight' });
    }
    if (attachments.weapon) {
        // Default to mainhand when inventoryType is missing (e.g. an
        // older payload). 21 = Main Hand which our dispatch sends right.
        const attId = weaponAttachmentId(inventoryType ?? 21);
        let label;
        if (attId === ATTACHMENT_LEFT_WRIST) label = 'shield';
        else if (attId === ATTACHMENT_HAND_LEFT) label = 'weapon-offhand';
        else label = 'weapon-mainhand';
        jobs.push({
            url: attachments.weapon,
            attId,
            label,
        });
    }

    if (jobs.length === 0) return 0;

    // Parallel fetch — the GLBs are independent. Settling instead of
    // all-or-nothing so a single bad URL (e.g. server-side decode failure
    // on one spaulder) doesn't drop the other attachments.
    const results = await Promise.allSettled(jobs.map(j => loadGlb(j.url)));

    let mounted = 0;
    for (let i = 0; i < jobs.length; i++) {
        const job = jobs[i];
        const r = results[i];
        if (r.status !== 'fulfilled') {
            console.warn(`[equip] attachment ${job.label} failed to load:`, r.reason);
            continue;
        }
        const ok = dresser.mountAttachment(character, job.attId, r.value.scene);
        if (!ok) {
            console.warn(`[equip] mountAttachment(${job.attId}) returned false — attachment node missing on character`);
            continue;
        }
        mounted++;
    }
    return mounted;
}

/**
 * Clear every dressing-managed attachment (helm + both shoulders +
 * both hand weapons). Called from unequipAll and on a complete re-dress
 * (equipMultiple) so stale geometry from a prior set doesn't linger when
 * the new set doesn't replace it.
 *
 * Worth noting: dresser.mountAttachment(... empty child) isn't a thing;
 * we use the same clear-by-overwrite pattern as the dresser by removing
 * all children directly on each attachment node. This matches the
 * already-baked "one item per attachment slot" semantics in dresser.js.
 */
function clearAllAttachments(character) {
    for (const attId of ALL_DRESSING_ATTACHMENT_IDS) {
        const att = dresser.getAttachment(character, attId);
        if (!att) continue;
        while (att.children.length) att.remove(att.children[0]);
    }
}

// ────────────────────────────────────────────────────────────────────
// Character identity (race + gender) — derived from the GLB URL the
// same way loadDefaultSkin does. Used to pass race+gender to
// ItemDressing so the server can pick the right race-suffixed helm M2
// (vanilla helms live at race+gender-specific paths like
// "Helm_..._HuM.m2" — the DBC stores only the base name).
//
// Recognized race tokens match CharacterModelService.ValidRaces:
//   Human Dwarf NightElf Gnome Orc Tauren Troll Scourge
// Genders: Male | Female
//
// Returns { race, gender } or null if we can't determine them.
// ────────────────────────────────────────────────────────────────────
const RACE_TOKENS = ['Human', 'Dwarf', 'NightElf', 'Gnome',
    'Orc', 'Tauren', 'Troll', 'Scourge'];

function deriveCharacterIdentity(character) {
    // First try character.gltf direct sources — same waterfall as
    // loadDefaultSkin so behavior is consistent.
    const datasetUrl = document.getElementById('char-preview-canvas')?.dataset.glbUrl;
    const parserUrl = character?.gltf?.parser?.options?.path;
    const assetUrl = character?.gltf?.asset?.url;
    const glbUrl = datasetUrl || assetUrl || parserUrl || '';
    // Tolerate optional ".v{N}" version stamp between key and extension
    // (CacheVersionRegistry — bumped when SkinnedGlbWriter changes).
    //   /character_models/HumanMale.glb       → key="HumanMale"
    //   /character_models/HumanMale.v2.glb    → key="HumanMale"
    const match = glbUrl.match(/\/character_models\/([^./]+)(?:\.v\d+)?\.glb/);
    if (!match) return null;

    const key = match[1];   // e.g. "HumanMale" or "NightElfFemale"
    for (const race of RACE_TOKENS) {
        if (key.startsWith(race)) {
            const tail = key.slice(race.length);
            if (tail === 'Male' || tail === 'Female') {
                return { race, gender: tail };
            }
        }
    }
    return null;
}

/**
 * Fetch the dressing payload for a single item display.
 * Returns null if the displayId isn't in the DBC.
 *
 * race / gender are appended as query params so the server can pick
 * the right race-suffixed helm M2. Body-atlas, shoulder, and weapon
 * items ignore these params on the server side.
 *
 * @param {number} displayId
 * @param {number} [itemId]  Optional item entry — supplies inventoryType.
 * @param {object} [identity]  { race, gender }. If omitted, server defaults to Human/Male.
 * @returns {Promise<object | null>}
 */
export async function fetchDressing(displayId, itemId = 0, identity = null) {
    const params = new URLSearchParams({ displayId: String(displayId) });
    if (itemId) params.set('itemId', String(itemId));
    if (identity?.race) params.set('race', identity.race);
    if (identity?.gender) params.set('gender', identity.gender);
    const url = `${ENDPOINT}?${params.toString()}`;
    const res = await fetch(url);
    if (res.status === 404) return null;
    if (!res.ok) throw new Error(`fetchDressing ${displayId}: HTTP ${res.status}`);
    return await res.json();
}

/**
 * Decode a slotUrls map (slot index → PNG URL) into a (slot → ImageBitmap)
 * array suitable for compositor.paintBodyAtlas.
 *
 * @param {Object<string, string>} slotUrls
 * @returns {Promise<Array<{ slot: number, image: ImageBitmap }>>}
 */
export async function loadSlotImages(slotUrls) {
    const entries = Object.entries(slotUrls);
    const loaded = await Promise.all(entries.map(async ([slot, url]) => {
        const res = await fetch(url);
        if (!res.ok) {
            console.warn(`[equip] failed to fetch slot ${slot}: ${url} (HTTP ${res.status})`);
            return null;
        }
        const blob = await res.blob();
        const image = await createImageBitmap(blob);
        return { slot: Number(slot), image };
    }));
    return loaded.filter(x => x !== null);
}

/**
 * Equip a single item — fetches its dressing, applies geoset filters,
 * paints body atlas, mounts any helm/shoulder/weapon attachments.
 *
 * Body-atlas items (chest, pants, etc) go through the texture
 * compositor. Helm / shoulder / weapon items skip the compositor and
 * mount as scene-graph attachments. The two pipelines coexist — an
 * equipMultiple call mixes them freely.
 *
 * @param {object} character     loader.loadCharacterGlb() result
 * @param {number} displayId
 * @param {number} [itemId]      Optional. Required if inventoryType isn't
 *                               in the payload (cheap to omit and have the
 *                               caller pass inventoryType explicitly via opts).
 * @param {object} [opts]
 * @param {number} [opts.inventoryTypeOverride]
 *        If set, used instead of the API's inventoryType. Useful when caller
 *        knows the slot but doesn't want to round-trip through item_template.
 * @param {HTMLImageElement|ImageBitmap} [opts.baseSkin]
 *        Base skin texture for the body atlas. If omitted, compositor uses
 *        the current /character_textures/skin/<key>Skin00_00.png cached
 *        next to the GLB.
 * @returns {Promise<{ applied: boolean, reason?: string }>}
 */
export async function equipDisplay(character, displayId, itemId = 0, opts = {}) {
    const identity = deriveCharacterIdentity(character);
    const dressing = await fetchDressing(displayId, itemId, identity);
    if (!dressing) {
        return { applied: false, reason: 'displayId not in DBC' };
    }

    const inventoryType = opts.inventoryTypeOverride ?? dressing.inventoryType;
    if (!inventoryType) {
        return {
            applied: false,
            reason: 'no inventoryType (pass itemId or opts.inventoryTypeOverride)'
        };
    }

    // 1. Geoset visibility. Weapons don't drive geoset visibility on the
    //    character body (they have their own M2), but applyItemFilters is
    //    a no-op for inventoryTypes without SLOT_RULES entries — safe to
    //    call unconditionally.
    //
    //    hidesHair (server-computed boolean from ItemDressing) is threaded
    //    through so the helm SLOT_RULES can decide per-item whether to
    //    suppress hair. Open helms (Helm of Might, circlets, masks) come
    //    back with hidesHair=false; closed helms (Wrath, Lawbringer) with
    //    =true. Body-atlas items have it as undefined → ignored by the
    //    rules. See geoset-rules.js SLOT_RULES[1] for the per-item logic.
    dresser.applyItemFilters(character, [{
        inventoryType,
        geosetGroup: dressing.geosetGroup,
        hidesHair: dressing.hidesHair,
    }]);

    // 2. Body atlas paint. Weapons have empty slotUrls so this branch
    //    short-circuits for them.
    const layers = await loadSlotImages(dressing.slotUrls);
    if (layers.length > 0) {
        const baseSkin = opts.baseSkin ?? await loadDefaultSkin(character);
        compositor.paintBodyAtlas(character, baseSkin, layers);
    }

    // 3. Helm / shoulder / weapon attachments. mountAttachmentsFromPayload
    //    uses inventoryType ONLY to pick the hand for weapons; helms and
    //    shoulders ignore it.
    await mountAttachmentsFromPayload(character, dressing.attachments, inventoryType);

    return { applied: true };
}

/**
 * Equip multiple items at once. The full item array is passed to
 * dresser.applyItemFilters in one call (so the robe/pants/chest
 * arbitration in geoset-rules.js sees every slot at once), and each
 * item's slot textures are composited as a SEPARATE layer onto a single
 * canvas in vanilla paint order — so partially-transparent overlay
 * textures (belt, tabard, gloves wrist trim) land correctly on top of
 * the base item textures they're meant to decorate.
 *
 * Caller does NOT need to pre-order the equipment array — equipMultiple
 * sorts by inventoryType priority before painting.
 *
 * @param {object} character
 * @param {Array<{ displayId: number, itemId?: number, inventoryType?: number }>} equipment
 *        At least one of itemId / inventoryType must be set per entry.
 * @param {object} [opts]   See equipDisplay.
 * @returns {Promise<{ applied: number, skipped: number }>}
 */
export async function equipMultiple(character, equipment, opts = {}) {
    const identity = deriveCharacterIdentity(character);

    // Fetch all dressing payloads in parallel.
    const dressings = await Promise.all(equipment.map(e =>
        fetchDressing(e.displayId, e.itemId ?? 0, identity).then(d => ({ entry: e, d }))));

    // Build a list of resolved items annotated with their effective
    // inventoryType (caller override > API). Filter out anything that
    // can't be resolved.
    const resolved = [];
    let skipped = 0;
    for (const { entry, d } of dressings) {
        if (!d) { skipped++; continue; }
        const invType = entry.inventoryType ?? d.inventoryType;
        if (!invType) { skipped++; continue; }
        resolved.push({ invType, d });
    }

    // 1. Geoset visibility — one call with ALL items so robe/pants
    //    arbitration sees every slot. Order doesn't matter for the
    //    geoset rules (they don't have layering semantics).
    //
    //    hidesHair is per-item: only helms (inventoryType 1) get it
    //    truthy. Threaded so the helm SLOT_RULES can pick between
    //    "hide hair under closed helm" vs "show hair under open helm"
    //    based on the server's helmetGeosetVis-derived computation.
    //    Body-atlas items have it as undefined → ignored by the rules.
    const items = resolved.map(r => ({
        inventoryType: r.invType,
        geosetGroup: r.d.geosetGroup,
        hidesHair: r.d.hidesHair,
    }));
    dresser.applyItemFilters(character, items);

    // 2. Body atlas — sort into paint order and composite each item as
    //    a separate layer onto one canvas. Stable sort: same-priority
    //    items keep their input array order (matters for e.g. multiple
    //    items with the same inventoryType, unlikely in practice but
    //    safe to preserve).
    //
    //    Weapons contribute no slotUrls so they don't show up in this
    //    loop — paintBodyAtlasLayered just sees the body-atlas items.
    const sorted = [...resolved].sort((a, b) =>
        paintOrder(a.invType) - paintOrder(b.invType));

    // Load each item's slot images as its own layer group, in paint order.
    const itemLayers = await Promise.all(
        sorted.map(r => loadSlotImages(r.d.slotUrls)));

    const totalLayers = itemLayers.reduce((sum, l) => sum + l.length, 0);
    if (totalLayers > 0) {
        const baseSkin = opts.baseSkin ?? await loadDefaultSkin(character);
        compositor.paintBodyAtlasLayered(character, baseSkin, itemLayers);
    }

    // 3. Helm / shoulder / weapon attachments.
    // Clear first so a re-dress without (e.g.) a helm doesn't leave the
    // previous helm geometry mounted. Then walk every resolved item and
    // mount any attachment URLs it brought along — at most one helm,
    // one pair of shoulders, and one weapon per hand across the whole
    // set (the player can only wear one of each), but the loop tolerates
    // duplicates because mountAttachment overwrites prior children.
    //
    // Pass each item's inventoryType to mountAttachmentsFromPayload so
    // the right hand vs left hand dispatch picks correctly. A 1H sword
    // (13) and an off-hand dagger (22) in the same equipment array will
    // mount on attachment 1 and attachment 2 respectively.
    clearAllAttachments(character);
    for (const r of resolved) {
        if (r.d.attachments) {
            await mountAttachmentsFromPayload(character, r.d.attachments, r.invType);
        }
    }

    return { applied: items.length, skipped };
}

/**
 * Reset the character to default (naked) appearance — base skin texture,
 * no item geosets, no helm / shoulder / weapon attachments.
 */
export async function unequipAll(character, opts = {}) {
    dresser.showDefaultGeosets(character);
    clearAllAttachments(character);
    const baseSkin = opts.baseSkin ?? await loadDefaultSkin(character);
    if (baseSkin) {
        // Paint a blank-layer atlas (just base skin, no overlays).
        compositor.paintBodyAtlas(character, baseSkin, []);
    }
}

// ────────────────────────────────────────────────────────────────────
// Default skin loader — finds the /character_textures/skin/*.png
// generated alongside the GLB and returns it as an ImageBitmap.
// ────────────────────────────────────────────────────────────────────

let _skinCache = null;

/**
 * Invalidate the base-skin bitmap cache. Must be called when the
 * character GLB is swapped (e.g. race/gender change on the Items page)
 * — otherwise the next equip will re-use the previous race's skin
 * texture as the body atlas base, painting Human pixels onto Orc
 * geometry. The bug this fixes was visible after the in-place race
 * swap added in the items-page integration: geometry swapped correctly
 * but the underlying skin remained whatever was loaded first.
 *
 * Idempotent. Safe to call before the cache has been populated.
 */
export function clearSkinCache() {
    _skinCache = null;
}

async function loadDefaultSkin(character) {
    if (_skinCache) return _skinCache;

    // Derive the skin URL from the character's GLB URL. The server writes
    // both files in a parallel naming scheme:
    //   /character_models/HumanMale.glb
    //   /character_textures/skin/HumanMaleSkin00_00.png
    //
    // Skin URL resolution, in priority order:
    //   1. canvas.dataset.skinUrl — explicitly set by Razor view
    //      (Views/Items/CharacterPreview.cshtml: data-skin-url="@ViewBag.SkinUrl").
    //      This is the AUTHORITATIVE source — the server knows the
    //      SkinPngVersion stamp; the client otherwise wouldn't.
    //   2. Regex-derive from the GLB URL (tolerating optional .v{N}
    //      stamp). Used when the view hasn't been updated to publish
    //      data-skin-url yet, or for backwards compatibility.
    const canvasEl = document.getElementById('char-preview-canvas');
    let skinUrl = canvasEl?.dataset.skinUrl;
    if (!skinUrl) {
        const datasetUrl = canvasEl?.dataset.glbUrl;
        const parserUrl = character.gltf?.parser?.options?.path;
        const assetUrl = character.gltf?.asset?.url;
        const glbUrl = datasetUrl || assetUrl || parserUrl || '';
        // Tolerate optional ".v{N}" version stamp between key and extension.
        const match = glbUrl.match(/\/character_models\/([^./]+)(?:\.v\d+)?\.glb/);
        if (!match) {
            console.warn('[equip] cannot derive skin URL — no data-skin-url and GLB URL didn\'t match', glbUrl);
            return null;
        }
        const key = match[1];   // e.g. "HumanMale"
        // Note: this fallback path doesn't know the SkinPngVersion. If
        // the view hasn't been updated to publish data-skin-url, this
        // will request the unversioned skin path which (after cache
        // sweeps) will 404. Update the view to fix.
        skinUrl = `/character_textures/skin/${key}Skin00_00.png`;
    }

    try {
        const res = await fetch(skinUrl);
        if (!res.ok) {
            console.warn(`[equip] base skin not found at ${skinUrl}`);
            return null;
        }
        const blob = await res.blob();
        _skinCache = await createImageBitmap(blob);
        return _skinCache;
    } catch (err) {
        console.warn('[equip] base skin load failed', err);
        return null;
    }
}
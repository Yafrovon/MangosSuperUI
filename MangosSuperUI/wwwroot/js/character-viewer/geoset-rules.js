// Character Viewer — Geoset rules.
//
// Maps an equipped item's inventory_type and ItemDisplayInfo.geosetGroup[]
// values to the set of geoset meshes that should be visible.
//
// ════════════════════════════════════════════════════════════════════
// CONFIDENCE LEGEND
// ════════════════════════════════════════════════════════════════════
//
// Throughout this file, each rule is tagged with a confidence level:
//
//   [VERIFIED]   — verified by hand-tuning a known-good visual state in
//                  the diagnostic picker AND, where applicable, comparing
//                  against the DBC geosetGroup values returned for that item.
//                  Currently only Apprentice's Robe (item 56, display 12647)
//                  has a full DBC-to-truth pair.
//
//   [HAND-TUNED] — visual state was produced by hand-tuning what an item
//                  "should look like" in the diagnostic picker, but no
//                  real item's DBC was checked against the result. So we
//                  know the mesh variants exist and look right, but the
//                  geosetGroup[N] → variant mapping is unverified.
//
//   [UNVERIFIED] — written from wowdev wiki references and pattern-matching
//                  against the one verified case. Untested. Will probably
//                  need correction on first real test.
//
// === The history of this file ===
//
// Prior revisions of this file made strong claims that did not survive
// empirical testing, including:
//   - "cat 10 = NakedTorso doublet, default-visible" — wrong; cat 10 is
//     hidden in the naked truth and only activates on specific items
//   - "cat 11 = NakedPelvis kilt, default-visible" — wrong; hidden in naked
//   - "cat 13 inversion: geoset 1301 long, 1302 short" — wrong; 1302 is
//     the robe geometry, 1301 is the bare-thighs geometry
//   - "cat 15 = tabard" — wrong; cat 15 is shoulders+cape (v1 = shoulders
//     only, v2-6 = capes that include shoulders)
//   - "DEFAULT_VISIBLE_CATEGORIES = [10, 11]" — wrong; this caused most
//     items to produce visually-broken naked-with-extras output
//
// The current file was rewritten May 15, 2026 from the diagnostic
// snapshots `naked-truth-human-male.json`, `apprentice-robe-truth-...`,
// `cloth-shirt-truth-...`, `pants-only-truth-...`, `boots-only-truth-...`.
// Only `apprentice-robe-truth` has a verified DBC payload (geosetGroup
// [1, 0, 1] for inventoryType 20 produces cat 8→802 + cat 13→1302).
// The other truths show what mesh combinations are "right-looking" but
// without a real DBC comparison, so SLOT_RULES entries for non-robe
// items are HAND-TUNED, not VERIFIED.
//
// === Session J addendum (May 15, 2026) ===
//
// After the StormLib swap unblocked patch.MPQ texture reads, we equipped
// the full Lawbringer set and observed that boots (variant 3 instead of 4)
// and gloves (variant 3 instead of 4) rendered as thinner-than-expected
// geometry. Cross-referenced against the decompiled vanilla WoW client
// algorithm (wowdev.wiki/DB/ItemDisplayInfo/GeosRenderPrep), which is
// explicit about the formula:
//
//     gloves geoset = 401 + pGloves->GeosetGroup[0]
//     boots  geoset = 501 + pBoots->GeosetGroup[0]
//     shirt  geoset = 801 + pShirt->GeosetGroup[0]
//     ...
//
// The "+ 1" offset is universal across armor categories — it's not "skip
// the bare-skin variant 1," it's literally how vanilla encodes "geosetGroup
// 0 means default." Cats 4 and 5 were the holdouts using offset 0; now
// corrected to +1. Cats 8/9/11/13 were already correct.

// ════════════════════════════════════════════════════════════════════
// Category meanings — HumanMale.m2, verified empirically May 15, 2026
// ════════════════════════════════════════════════════════════════════
//
// cat 0  — base body (variant 0) + HAIR STYLES (variants 1-13)
//          Variant 0 is the face/torso/limbs base mesh that every render
//          needs. Variants 1-13 are the ~13 hair geometries that appear
//          in HumanMale character creation. Vanilla renders variant 0 +
//          exactly ONE hair variant (driven by CharSections.dbc); we
//          haven't built the hair-selector UI yet, so HAIR_VARIANT below
//          picks the default.
// cat 1  — face: chin (v1=default, v2=alt)
// cat 2  — face: jaw (v1=default, v2=alt)
// cat 3  — face: mouth (v1=default, v2=alt)
// cat 4  — gloves / hand armor (v1=bare, v2-4=glove variants)
// cat 5  — boots / feet (v1=bare feet, v2-4=boot variants)
// cat 7  — ears (default hidden by hair; not seen in any truth snapshot)
// cat 8  — sleeves (v2/v3 — note: NO variant 1 in this model)
// cat 9  — long pants/leggings (v2/v3 — no variant 1)
// cat 10 — mid-torso garment ("shirt-tail" hanging below chest; v2 only)
// cat 11 — short pants/waistband (v2/v3 — no variant 1)
// cat 12 — tabard bottom (v2 only)
// cat 13 — ROBE vs THIGHS — mutually exclusive! v1 = bare thighs, v2 = robe
// cat 15 — shoulders + optional cape (v1 = shoulders only, v2-6 = capes)
//
// Cats 6, 14, 16+ — not present in HumanMale.m2.
// Cats present but unused in the items we've tested: 7 (ears),
//   10 (only cloth-shirt truth uses it), 12 (only tabard would use it).

// Default hair-style variant for cat 0. Vanilla HumanMale has ~13 hair
// geometries indexed 1-13. Variant 10 chosen by visual testing
// (Session K) as a reasonable default. Future work: drive this from a
// per-character hair-style selector / from CharSections.dbc.
const HAIR_VARIANT = 10;

// ════════════════════════════════════════════════════════════════════
// Naked-default state — VERIFIED against naked-truth-human-male.json
// ════════════════════════════════════════════════════════════════════
//
// For each category, what variant should be visible when no items are
// equipped. Race-specific in principle; only verified on HumanMale.
// Other races may need overrides — we'll discover those when we test.
//
// The naked state is NOT "smallest nonzero variant of every cat that
// exists." It's a specific per-cat choice that produces a recognizable
// naked humanoid character. Cats not in this map default to hidden.
const NAKED_DEFAULTS = {
    1: 1,   // face chin
    2: 1,   // face jaw
    3: 1,   // face mouth
    4: 1,   // bare hands (variant 1)
    5: 1,   // bare feet (variant 1)
    7: 1,   // ears (variant 1 = default/smaller; v2 = larger alternative)
    13: 1,   // bare thighs (variant 1; "no robe")
    15: 1,   // shoulders only (variant 1; "no cape")
};

// ════════════════════════════════════════════════════════════════════
// Categories items never modify.
// ════════════════════════════════════════════════════════════════════
//
// Items don't change a character's face. Cats 1, 2, 3 are driven by the
// player's character-creation choices, not by equipment. We list them
// here so the rules never accidentally clobber them when applying an
// item — and so future code that does want to vary them (e.g. helms
// hiding hair) has a clearly-named hook to override.
//
// Cat 7 (ears) is in here for the same reason: ears are character
// customization, not equipment. (Earrings could change cat 7 in
// principle, but earrings aren't body-atlas items in vanilla.)
const CHARACTER_APPEARANCE_CATEGORIES = new Set([1, 2, 3, 7]);

// ════════════════════════════════════════════════════════════════════
// Per-inventory-type rules.
// ════════════════════════════════════════════════════════════════════
//
// For each inventoryType, `groups` is an array of [category, groupIdx,
// offset] tuples. `groupIdx` indexes into item.geosetGroup[]; the
// resolved mesh variant is `geosetGroup[groupIdx] + offset`.
//
// `offset` is universally 1 for armor categories. The decompiled vanilla
// WoW client code (GeosRenderPrep) encodes the formula as
// `BASE + geosetGroup[N]` where BASE is the first mesh ID in the cat
// (e.g. 401 for gloves, 501 for boots, 801 for sleeves). Our representation
// `geosetGroup[N] + 1` evaluated against `variant N+1` is mathematically
// equivalent, since cat-N mesh IDs are 100*N + variant. The "+1" is not
// "skip the bare-skin variant" — it's how vanilla encodes the
// "0 = default, N = variant N" convention.
//
// `forceHide` is a list of categories the slot explicitly suppresses
// regardless of geosetGroup. Robes force-hide cat 11 (waistband),
// for example, because a robe should not show the waistband through
// the robe geometry.
export const SLOT_RULES = {

    // 1 = HEAD (helm). Vanilla has no helm geosets in the body M2 — the
    //   helm is its own M2 mounted on Attachment_11 (Session L). What we
    //   DO need from the body M2 is to suppress hair so the hair doesn't
    //   poke through plate FOR CLOSED HELMS — but open helms (circlets,
    //   masks, Helm of Might) leave the head exposed and need the hair /
    //   scalp dome to stay visible. (In HumanMale.m2 the scalp dome is
    //   baked into each hair-style geoset, so hiding all hair also
    //   removes the skull — visible as a hollow above the face for any
    //   helm M2 that doesn't cover the top of the head.)
    //
    //   `setVariants` is normally a static cat → variants object applied
    //   AFTER the SLOT_RULES groups + forceHide passes. For the helm
    //   slot we make it a FUNCTION that gets called with the equipped
    //   item — returning either an override object or null (no override).
    //
    //   Per-item decision uses the server-computed `hidesHair` boolean,
    //   which the ItemDressing endpoint derives from m_helmetGeosetVis[0..1]
    //   via the open-vs-closed heuristic (v1 != v2 → closed → hide).
    //   The endpoint is the single source of truth — if/when we replace
    //   the heuristic with a real HelmetGeosetVisData.dbc decode, only
    //   the server changes; this rule keeps working.
    //
    //   Verified May 16 2026:
    //     Helm of Wrath  (closed, 248/306) → hidesHair=true  → hair hidden
    //     Helm of Might  (open,   247/247) → hidesHair=false → hair shown
    1: {
        groups: [],
        setVariants: (item) => item?.hidesHair ? { 0: [0] } : null,
    },

    // 2 = NECK — atlas-only / no geoset change.  [VERIFIED-EMPTY]
    2: { groups: [] },

    // 3 = SHOULDER — paints into cat 15 (shoulders+cape). The shoulder
    //   slot adds optional shoulder pad geometry on top of the default
    //   shoulders. Vanilla cat 15 has variants 1-6; v1 is "no shoulder
    //   pads" and v2-6 are pad shapes. We don't yet know which
    //   geosetGroup index controls them.
    //   [UNVERIFIED] — need a real shoulder item DBC to confirm.
    3: { groups: [[15, 0, 0]] },

    // 4 = SHIRT (cosmetic undershirt) — adds cat 8 sleeves and cat 10
    //   shirt-tail.  [HAND-TUNED only]
    4: {
        groups: [
            [8, 0, 1],   // sleeves — same +1 offset as chest
            [10, 1, 0],   // shirt-tail — only 1 variant, offset unknown
        ]
    },

    // 5 = CHEST (cloth/leather/plate non-robe).  [HAND-TUNED]
    //   Hand-tuned cloth-shirt truth shows cat 8→803 and cat 10→1002,
    //   but we don't have a real cloth-chest DBC to compare. Pattern-
    //   matched from chest=robe: probably groupIdx 0 for sleeves and
    //   groupIdx 1 for the shirt-tail.
    5: {
        groups: [
            [8, 0, 1],   // sleeves +1 offset (UNVERIFIED but matches robe)
            [10, 1, 0],   // shirt-tail — only 1 variant exists; offset unknown
        ]
    },

    // 6 = WAIST (belt) — vanilla has no belt-specific geoset.
    //   [VERIFIED-EMPTY]
    6: { groups: [] },

    // 7 = PANTS — long pants (cat 9) + waistband (cat 11). Hand-tuned
    //   truth shows cat 9→903 and cat 11→1102 for "what pants should
    //   look like," but no real pants DBC.  [HAND-TUNED]
    7: {
        groups: [
            [9, 0, 1],   // long pants — same +1 pattern as cat 8/13
            [11, 1, 1],   // waistband — same +1 pattern (guessing)
        ]
    },

    // 8 = BOOTS — cat 5 (feet/boots).
    //   Cat 5 has 4 variants: 501 bare, 502-504 boot styles.
    //   [VERIFIED via decompiled GeosRenderPrep, Session J]:
    //     boots geoset = 501 + pBoots->GeosetGroup[0]
    //   So DBC value N → mesh variant N+1. Lawbringer boots DBC
    //   geosetGroup[0]=3 → mesh variant 4 (chunky plate boot).
    8: {
        groups: [
            [5, 0, 1],   // boots — +1 offset per vanilla client algorithm
        ]
    },

    // 9 = WRIST (bracers) — atlas-only.  [VERIFIED-EMPTY by convention]
    9: { groups: [] },

    // 10 = GLOVES — cat 4 (gloves/hand armor).
    //   Cat 4 has 4 variants: 401 bare, 402-404 glove styles.
    //   [VERIFIED via decompiled GeosRenderPrep, Session J]:
    //     gloves geoset = 401 + pGloves->GeosetGroup[0]
    //   So DBC value N → mesh variant N+1. Lawbringer gauntlets DBC
    //   geosetGroup[0]=3 → mesh variant 4 (chunky plate gauntlet).
    10: {
        groups: [
            [4, 0, 1],   // gloves — +1 offset per vanilla client algorithm
        ]
    },

    // 11/12 — reserved / not used by vanilla items.
    11: { groups: [] },
    12: { groups: [] },

    // 13/14/15/17/21/22/23/25/26 — weapons / shields / ranged. No body
    //   geosets affected; Session D attachment territory.
    13: { groups: [] }, 14: { groups: [] }, 15: { groups: [] },
    17: { groups: [] }, 21: { groups: [] }, 22: { groups: [] },
    23: { groups: [] }, 25: { groups: [] }, 26: { groups: [] },

    // 16 = BACK (cape) — cat 15 again, but selecting cape variants 2-6.
    //   [UNVERIFIED] — no cape item tested. Probably geosetGroup[0]
    //   with offset 1 (so DBC variant 1 selects cape variant 2 = first
    //   real cape style).
    16: {
        groups: [
            [15, 0, 1],
        ]
    },

    // 19 = TABARD — cat 12 (tabard-bottom).
    //   Cat 12 has only one variant (1202). With offset 0, DBC value 1
    //   selects mesh variant 1 (which doesn't exist) → no render. With
    //   offset 1, DBC value 1 selects variant 2 (geoset 1202) → renders.
    //   Guessing offset 1 to match the +1 pattern from cats 8/13.
    //   [UNVERIFIED] — no tabard item tested.
    19: {
        groups: [
            [12, 0, 1],
        ]
    },

    // 20 = ROBE (alt chest, used by caster gear).
    //   [VERIFIED] from apprentice-robe-truth + DBC geosetGroup [1, 0, 1]:
    //     groupIdx 0 (value 1) → cat 8 mesh variant 2 (geoset 802) ← +1 offset
    //     groupIdx 1 (value 0) → cat 10 stays at default (hidden)   ← 0 = leave alone
    //     groupIdx 2 (value 1) → cat 13 mesh variant 2 (geoset 1302) ← +1 offset
    //
    //   Robes also force cat 11 (waistband) hidden, since the robe
    //   covers it and we don't want belt geometry poking through.
    //   Force-hide on cat 9 too (long pants under a robe).
    20: {
        groups: [
            [8, 0, 1],   // sleeves
            [10, 1, 0],   // shirt-tail (rarely used with robes; DBC sets 0)
            [13, 2, 1],   // robe vs thighs
        ],
        forceHide: [9, 11],   // long pants + waistband off
    },
};

// ════════════════════════════════════════════════════════════════════
// Core resolution: given equipped items, what should be visible?
// ════════════════════════════════════════════════════════════════════

/**
 * Compute the set of geoset IDs that should be visible given a list
 * of equipped items.
 *
 * Algorithm (intentionally explicit per step so future debugging is
 * easier than the last revision's was):
 *
 *   1. Index every geoset in the model by category.
 *   2. Initialize selectedVariant per cat:
 *        - cat 0 → 'all' (every variant; base body)
 *        - cats in NAKED_DEFAULTS → that variant
 *        - everything else → 0 (hidden)
 *   3. For each equipped item, for each rule (cat, groupIdx, offset):
 *        - Read item.geosetGroup[groupIdx], call it `dbcValue`.
 *        - If dbcValue > 0: selectedVariant[cat] = dbcValue + offset
 *        - If dbcValue === 0: leave the existing default in place.
 *      Then apply the item's `forceHide` list — those cats go to 0.
 *      Items don't touch CHARACTER_APPEARANCE_CATEGORIES.
 *   4. Convert selectedVariant to the visible-geoset-id set.
 *
 * @param {THREE.SkinnedMesh[]} geosetList — every body geoset on the character
 * @param {Array<{ inventoryType: number, geosetGroup: number[] }>} items
 * @returns {Set<number>} geoset IDs to set visible
 */
export function resolveVisibleGeosets(geosetList, items) {

    // ── 1. Index by category ──
    const byCategory = new Map();
    for (const m of geosetList) {
        const cat = m.userData?.geosetCategory;
        if (typeof cat !== 'number') continue;
        if (!byCategory.has(cat)) byCategory.set(cat, []);
        byCategory.get(cat).push(m);
    }

    // ── 2. Initialize selected-variant map ──
    // Sentinel values:
    //   'all'    → show every variant in this cat
    //   Array<number> → show meshes whose variant is in this set
    //   number   → show meshes whose geosetVariant === that number
    //   0        → hide every variant
    const selectedVariant = new Map();
    for (const cat of byCategory.keys()) {
        selectedVariant.set(cat, 0);  // default everything hidden
    }
    // Cat 0 = base body (variant 0) + ONE hair style (variants 1-13 on
    // HumanMale). The previous 'all' setting made every hair variant
    // visible simultaneously, producing the giant-mass-of-hair look
    // seen pre-Session-K.
    if (byCategory.has(0)) selectedVariant.set(0, [0, HAIR_VARIANT]);
    for (const [cat, variant] of Object.entries(NAKED_DEFAULTS)) {
        selectedVariant.set(Number(cat), variant);
    }

    // ── 3. Apply each item's rules ──
    for (const item of items) {
        const rule = SLOT_RULES[item.inventoryType];
        if (!rule) continue;

        for (const [cat, groupIdx, offset] of rule.groups || []) {
            // Character-appearance cats are NEVER touched by items.
            if (CHARACTER_APPEARANCE_CATEGORIES.has(cat)) continue;

            const dbcValue = item.geosetGroup?.[groupIdx] ?? 0;
            if (dbcValue > 0) {
                selectedVariant.set(cat, dbcValue + (offset ?? 0));
            }
            // dbcValue === 0: leave the NAKED_DEFAULTS-derived value alone.
            //   This is the right behavior for robes: their geosetGroup[1]=0
            //   for cat 10 means "I don't put a shirt-tail under the robe,
            //   keep the default" — which is 'hidden' since cat 10 isn't
            //   in NAKED_DEFAULTS.
        }

        for (const cat of rule.forceHide || []) {
            if (CHARACTER_APPEARANCE_CATEGORIES.has(cat)) continue;
            selectedVariant.set(cat, 0);
        }

        // setVariants — per-category override that REPLACES whatever
        // resolution the cat had so far. Used by the helm slot to force
        // cat 0 to [0] (base body only, no hair). Honored even for
        // CHARACTER_APPEARANCE_CATEGORIES because the helm rule
        // legitimately needs to suppress hair — that's the whole point.
        //
        // May be either:
        //   - A static object { cat: variants, ... } — applied as-is.
        //   - A FUNCTION (item) → object|null — evaluated against the
        //     current item. Null result = no override. Used by the helm
        //     slot to gate hair suppression on per-item open-vs-closed
        //     (server publishes the `hidesHair` boolean for each helm).
        let effectiveSetVariants = rule.setVariants;
        if (typeof effectiveSetVariants === 'function') {
            effectiveSetVariants = effectiveSetVariants(item);
        }
        if (effectiveSetVariants) {
            for (const [catStr, sel] of Object.entries(effectiveSetVariants)) {
                selectedVariant.set(Number(catStr), sel);
            }
        }
    }

    // ── 4. Build the visible-geoset-id set ──
    const visible = new Set();
    for (const [cat, sel] of selectedVariant) {
        const meshes = byCategory.get(cat);
        if (!meshes) continue;
        if (sel === 0) continue;
        if (sel === 'all') {
            for (const m of meshes) {
                if (m.userData?.geosetId !== undefined) {
                    visible.add(m.userData.geosetId);
                }
            }
            continue;
        }
        if (Array.isArray(sel)) {
            // Multi-variant selection (e.g. cat 0 = base + hair).
            const wanted = new Set(sel);
            for (const m of meshes) {
                if (wanted.has(m.userData?.geosetVariant)) {
                    visible.add(m.userData.geosetId);
                }
            }
            continue;
        }
        // sel is a numeric variant.
        for (const m of meshes) {
            if (m.userData?.geosetVariant === sel) {
                visible.add(m.userData.geosetId);
            }
        }
    }

    return visible;
}

/**
 * Convenience: the naked-character visible set (no items equipped).
 * Should reproduce naked-truth-human-male.json's `state.categories`.
 */
export function resolveNakedGeosets(geosetList) {
    return resolveVisibleGeosets(geosetList, []);
}

// ════════════════════════════════════════════════════════════════════
// Self-check (call from console: cv.geosetRules.verifyAgainstNaked(cv))
// ════════════════════════════════════════════════════════════════════
//
// Compares resolveNakedGeosets against the recorded naked truth and
// logs any cats whose visibility doesn't match. Useful as a smoke
// test after rule edits.
export function verifyAgainstNaked(cv) {
    // Note: cat 0 expectation changed Session K. Previously this verifier
    // checked `0: 'all'` because the old algorithm made every cat 0 mesh
    // visible — but that included all ~13 hair variants simultaneously,
    // which is the "giant hair blob" rendering bug. Naked-truth snapshot
    // pre-dates the fix; if comparing this verifier output against the
    // raw JSON snapshot, cat 0 will look different (the snapshot has all
    // hair variants listed; the new render has variant 0 + HAIR_VARIANT).
    // That's not a regression — it's the snapshot being from a buggy era.
    const expected = {
        0: 'base+hair',  // variant 0 (base body) + HAIR_VARIANT
        1: [101], 2: [201], 3: [301],
        4: [401], 5: [501],
        13: [1301],
        15: [1501],
        // every other cat: hidden
    };

    const visible = resolveNakedGeosets(cv.geosetList);
    const byCat = new Map();
    for (const m of cv.geosetList) {
        const cat = m.userData?.geosetCategory;
        if (typeof cat !== 'number') continue;
        if (!byCat.has(cat)) byCat.set(cat, []);
        if (visible.has(m.userData.geosetId)) {
            byCat.get(cat).push(m.userData.geosetId);
        }
    }

    let fails = 0;
    for (const cat of [0, 1, 2, 3, 4, 5, 7, 8, 9, 10, 11, 12, 13, 15]) {
        const got = (byCat.get(cat) ?? []).sort((a, b) => a - b);
        const want = expected[cat];
        if (want === 'base+hair') {
            // Expect at least one cat 0 mesh with variant 0 (base body)
            // visible, and at least one variant > 0 (hair) visible.
            const cat0Meshes = cv.geosetList.filter(m =>
                m.userData?.geosetCategory === 0);
            const visBase = cat0Meshes.some(m =>
                m.userData?.geosetVariant === 0 && visible.has(m.userData.geosetId));
            const visHair = cat0Meshes.some(m =>
                m.userData?.geosetVariant > 0 && visible.has(m.userData.geosetId));
            if (!visBase || !visHair) {
                console.error(`cat 0: expected base+hair, got base=${visBase} hair=${visHair}`);
                fails++;
            }
        } else if (want) {
            const wantSorted = [...want].sort((a, b) => a - b);
            if (JSON.stringify(got) !== JSON.stringify(wantSorted)) {
                console.error(`cat ${cat}: expected ${wantSorted}, got ${got}`);
                fails++;
            }
        } else {
            if (got.length !== 0) {
                console.error(`cat ${cat}: expected hidden, got ${got}`);
                fails++;
            }
        }
    }
    if (fails === 0) {
        console.log('✓ resolveNakedGeosets passes (Session K cat-0 rules)');
    } else {
        console.error(`✗ ${fails} category mismatches`);
    }
    return fails === 0;
}
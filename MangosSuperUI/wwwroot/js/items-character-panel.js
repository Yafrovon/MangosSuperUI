// MangosSuperUI — Items page character panel.
//
// Wires the embedded character viewer to the Items page UI:
//   - Race + gender toolbar (16 buttons: 8 races × 2 genders).
//   - Default starter clothes (Recruit's Shirt + Recruit's Pants) loaded
//     on initial mount and after every race swap so the character is
//     never fully naked.
//   - Equip-on-click: items.js dispatches a 'cv:item-selected' CustomEvent
//     when the user clicks an item in the results list; this panel
//     equips it cumulatively on top of whatever's already worn.
//   - Tier set picker: class tabs + tier buttons; clicking a (class,tier)
//     combo equips all 8-9 pieces at once via equipMultiple.
//   - "Strip" button that clears everything and re-equips just the
//     defaults so the character is never lost behind opaque junk.
//   - Per-slot unequip chips that show currently-equipped items and let
//     the user remove a single piece.
//
// === Module shape ===
// Exports `mountItemsCharacterPanel(opts)` which the Items page calls
// once on DOM ready. The panel boots lazy — it waits until the user
// clicks "Try On" (or any item) before actually loading the viewer,
// keeping the Items page snappy when the user is only browsing.

import { mountCharacterViewer } from '/js/character-viewer/embed.js';
import { TIER_SETS, TIER_CLASSES, TIER_IDS } from '/js/character-viewer/tier-sets.js';

// ── Races + genders ────────────────────────────────────────────────────
// Matches CharacterModelService.EnsureCharacterGlbAsync valid inputs.
const RACES = [
    { id: 'Human', label: 'Human' },
    { id: 'Dwarf', label: 'Dwarf' },
    { id: 'NightElf', label: 'Night Elf' },
    { id: 'Gnome', label: 'Gnome' },
    { id: 'Orc', label: 'Orc' },
    { id: 'Tauren', label: 'Tauren' },
    { id: 'Troll', label: 'Troll' },
    { id: 'Scourge', label: 'Undead' },
];
const GENDERS = [
    { id: 'Male', label: 'M' },
    { id: 'Female', label: 'F' },
];

// ── Starter clothes ────────────────────────────────────────────────────
// Looked up against item_template earlier this session. Pre-resolved
// itemId + displayId so the panel doesn't have to make a DB roundtrip on
// every mount. If the DB is regenerated with different entries these
// might break — easy to update.
const DEFAULT_OUTFIT = [
    { name: "Recruit's Shirt", itemId: 38, displayId: 9891, slot: 4 },
    { name: "Recruit's Pants", itemId: 39, displayId: 9892, slot: 7 },
];

// ── Slot labels (item_template.inventory_type) ─────────────────────────
const SLOT_LABEL = {
    1: 'Head', 3: 'Shoulder', 4: 'Shirt', 5: 'Chest', 6: 'Waist',
    7: 'Legs', 8: 'Feet', 9: 'Wrist', 10: 'Hands', 11: 'Ring',
    12: 'Trinket', 13: '1H', 14: 'Shield', 15: 'Ranged', 16: 'Back',
    17: '2H', 20: 'Robe', 21: 'Main', 22: 'Off', 23: 'Held',
};

// Inventory types that mount on a hand/wrist attachment — weapons,
// shields, off-hand items, ranged, held-in-offhand (tomes/librams).
// Mirrors equip.js's WEAPON_INVENTORY_TYPES so the tier-set apply path
// can preserve these across tier swaps. Sessions T2 → T2.5 should not
// nuke your Thunderfury.
const WEAPON_SLOTS = new Set([13, 14, 15, 17, 21, 22, 23, 25, 26, 28]);

/**
 * Mount the items-page character panel.
 *
 * @param {{
 *   canvas: HTMLCanvasElement,
 *   raceToolbarEl: HTMLElement,
 *   tierSideEl: HTMLElement,       // empty container inside the canvas wrap
 *   tierToggleBtnEl: HTMLElement,  // small button on canvas to hide/show the panel
 *   stripBtnEl: HTMLElement,
 *   equippedListEl: HTMLElement,
 * }} opts
 * @returns {{ destroy: () => void }}
 */
export function mountItemsCharacterPanel(opts) {
    const {
        canvas, raceToolbarEl, tierSideEl, tierToggleBtnEl,
        stripBtnEl, equippedListEl,
    } = opts;

    // ── State ──────────────────────────────────────────────────────────
    let currentRace = 'Human';
    let currentGender = 'Male';
    let viewerHandle = null;     // result of mountCharacterViewer
    let equipped = [];           // [{name, itemId, displayId, slot}, ...]
    // Always includes DEFAULT_OUTFIT entries
    // at the bottom.

    // ── Race + gender toolbar ──────────────────────────────────────────
    buildRaceToolbar(raceToolbarEl, (race, gender) => {
        if (race === currentRace && gender === currentGender) return;
        currentRace = race;
        currentGender = gender;
        switchRace();
    });

    // ── Tier set side panel ────────────────────────────────────────────
    // Mounts ONCE — clicking a tier button calls applyTierSet directly,
    // no modal open/close cycle. The panel is a permanent sidebar in
    // the canvas wrap; the toggle button on the canvas hides/shows it.
    if (tierSideEl) {
        buildTierSidePanel(tierSideEl, async (cls, tier) => {
            const pieces = TIER_SETS[cls]?.[tier];
            if (!pieces) return;
            // Lazy-init if the user hasn't clicked an item yet.
            if (!viewerHandle) await initialMount();

            // Preserve any equipped weapons/shields/offhand/held items
            // across tier swaps. Tier sets only define armor pieces
            // (head/chest/shoulders/etc), never hand slots — cycling
            // T0 → T0.5 → T1 → ... should not force the user to re-pick
            // their weapon each time. Strip button still nukes them via
            // stripAndDefaultDress() — that's the only path that clears.
            const keptWeapons = equipped.filter(e => WEAPON_SLOTS.has(e.slot));

            equipped = [
                ...pieces.map(p => ({
                    name: p.name,
                    itemId: p.itemId,
                    displayId: p.displayId,
                    slot: p.slot,
                })),
                ...keptWeapons,
            ];
            await applyOutfit(equipped.slice());
        });
    }

    // ── Tier panel toggle (small button on canvas) ─────────────────────
    if (tierToggleBtnEl && tierSideEl) {
        tierToggleBtnEl.addEventListener('click', () => {
            tierSideEl.classList.toggle('collapsed');
            tierToggleBtnEl.classList.toggle('panel-collapsed');
        });
    }

    // ── Strip button ───────────────────────────────────────────────────
    if (stripBtnEl) {
        stripBtnEl.addEventListener('click', async () => {
            await stripAndDefaultDress();
        });
    }

    // ── Item-click events from items.js ────────────────────────────────
    // items.js dispatches { detail: { itemId, displayId, inventoryType } }
    // when the user clicks an item row in the results list.
    const itemSelectedHandler = async (e) => {
        const { itemId, displayId, inventoryType, name } = e.detail || {};
        if (!displayId) return;

        // Lazy-init the viewer on the first equip.
        if (!viewerHandle) await initialMount();

        await equipOne({
            name: name || `Item ${itemId}`,
            itemId, displayId,
            slot: inventoryType,
        });
    };
    document.addEventListener('cv:item-selected', itemSelectedHandler);

    // ── Functions (operate on the closed-over state above) ─────────────

    /**
     * First-time viewer mount. Loads the default race/gender + starter
     * clothes. Subsequent races use switchRace() which goes through swap().
     */
    async function initialMount() {
        const { glbUrl, skinUrl } = await fetchCharacterUrls(currentRace, currentGender);
        if (!glbUrl) {
            console.warn('[items-character] could not load initial character');
            return;
        }
        viewerHandle = await mountCharacterViewer({
            canvas,
            glbUrl,
            skinUrl,
        });
        // Re-bind window.cv so devtools poking still works the same as
        // on the CharacterPreview page.
        window.cv = viewerHandle.cv;

        // Apply default outfit so the character is never naked.
        await applyOutfit(DEFAULT_OUTFIT);
    }

    /**
     * Race or gender changed. Reload the GLB, then re-apply whatever
     * equipped state we had — including the default outfit, which the
     * new GLB starts without.
     */
    async function switchRace() {
        if (!viewerHandle) {
            // Initial mount uses the current race/gender as-is.
            await initialMount();
            return;
        }
        const { glbUrl, skinUrl } = await fetchCharacterUrls(currentRace, currentGender);
        if (!glbUrl) {
            console.warn(`[items-character] no GLB for ${currentRace} ${currentGender}`);
            return;
        }
        await viewerHandle.swap({ glbUrl, skinUrl });
        window.cv = viewerHandle.cv;

        // Re-apply equipped state. The swap stripped the old character;
        // the new one needs the full kit reapplied.
        await applyOutfit(equipped.slice());
    }

    /**
     * Equip one item on top of whatever is already worn. Replaces any
     * existing piece in the same slot (paper-doll semantics).
     */
    async function equipOne(item) {
        if (!viewerHandle) return;

        // Robe (slot 20) and chest (slot 5) compete for the chest mount;
        // remove either if the new item is either. Shirt (4) is treated
        // as a separate slot so it can stack under a chest piece.
        const competing = (a, b) => {
            const chestGroup = new Set([5, 20]);
            if (chestGroup.has(a) && chestGroup.has(b)) return true;
            return a === b;
        };
        equipped = equipped.filter(e => !competing(e.slot, item.slot));
        equipped.push(item);

        await applyOutfit(equipped.slice());
    }

    /**
     * Strip everything off, then put the defaults back on. Used by the
     * "Strip" button and after a tier swap if the user wants a clean
     * slate.
     */
    async function stripAndDefaultDress() {
        if (!viewerHandle) return;
        equipped = [];
        await applyOutfit(DEFAULT_OUTFIT);
    }

    /**
     * Apply an arbitrary outfit to the viewer.
     * equipMultiple is the workhorse — it handles geoset filters, body
     * atlas painting, AND helm/shoulder/weapon attachments in one call.
     *
     * We always include DEFAULT_OUTFIT pieces in the call, in addition
     * to whatever's in `next`, IF the corresponding slot isn't already
     * covered. That way "Strip" → defaults; a tier-set apply replaces
     * the defaults' chest/legs naturally (because the tier piece is in
     * the same competing-slot group); and a single-item click preserves
     * the rest of the kit.
     */
    async function applyOutfit(next) {
        if (!viewerHandle) return;
        const cv = viewerHandle.cv;
        const slotsCovered = new Set(next.map(e => e.slot));
        const chestCovered = slotsCovered.has(5) || slotsCovered.has(20);

        const finalOutfit = next.slice();
        for (const d of DEFAULT_OUTFIT) {
            // Skip the default if the next outfit already covers its slot.
            // Special case: skip the default pants (slot 7) if a robe (20)
            // is being worn, since robes cover the legs visually.
            const defaultSlot = d.slot;
            const inSet = slotsCovered.has(defaultSlot);
            const robeWearingPants = defaultSlot === 7 && slotsCovered.has(20);
            if (inSet || robeWearingPants) continue;
            finalOutfit.push(d);
        }

        // Convert to equipMultiple's expected payload shape:
        // { itemId, displayId, inventoryType }
        const payload = finalOutfit.map(e => ({
            itemId: e.itemId,
            displayId: e.displayId,
            inventoryType: e.slot,
        }));

        try {
            await cv.equip.equipMultiple(cv.character, payload);
        } catch (err) {
            console.error('[items-character] equipMultiple failed', err);
        }

        // Update the "currently equipped" UI; show non-default items only
        // so the user sees their selections without the starter clothes
        // cluttering the list.
        equipped = next.slice();
        renderEquippedList();
    }

    /**
     * Render the "currently equipped" chips. Each chip has an X button
     * that removes that single piece and re-dresses.
     */
    function renderEquippedList() {
        if (!equippedListEl) return;
        equippedListEl.innerHTML = '';
        if (equipped.length === 0) {
            equippedListEl.innerHTML = '<div class="text-muted" style="font-size:12px;padding:6px;">Nothing equipped yet — click an item or pick a tier set.</div>';
            return;
        }
        // Sort by slot for visual order (head → ring)
        const sorted = [...equipped].sort((a, b) => a.slot - b.slot);
        for (const e of sorted) {
            const chip = document.createElement('span');
            chip.className = 'equipped-chip';
            chip.innerHTML = `
                <span class="equipped-slot">${SLOT_LABEL[e.slot] || ('slot ' + e.slot)}</span>
                <span class="equipped-body">
                    <span class="equipped-name">${escapeHtml(e.name)}</span>
                    <span class="equipped-meta">#${e.itemId} · disp ${e.displayId}</span>
                </span>
                <button class="equipped-remove" title="Unequip" data-itemid="${e.itemId}">×</button>
            `;
            equippedListEl.appendChild(chip);
        }
        equippedListEl.querySelectorAll('.equipped-remove').forEach(btn => {
            btn.addEventListener('click', async () => {
                const itemId = parseInt(btn.dataset.itemid, 10);
                equipped = equipped.filter(e => e.itemId !== itemId);
                await applyOutfit(equipped.slice());
            });
        });
    }

    // ── Initial render ─────────────────────────────────────────────────
    renderEquippedList();

    return {
        destroy() {
            document.removeEventListener('cv:item-selected', itemSelectedHandler);
            if (viewerHandle) viewerHandle.dispose();
        },
    };
}

// ────────────────────────────────────────────────────────────────────────
// DOM builders (kept module-local; not exported)
// ────────────────────────────────────────────────────────────────────────

function buildRaceToolbar(el, onChange) {
    el.innerHTML = '';
    const wrap = document.createElement('div');
    wrap.className = 'cv-race-toolbar';

    // Build one button per (race, gender). Default selection is
    // Human Male — first race, first gender.
    let selectedRace = 'Human';
    let selectedGender = 'Male';

    for (const r of RACES) {
        const group = document.createElement('div');
        group.className = 'cv-race-group';
        const lbl = document.createElement('span');
        lbl.className = 'cv-race-label';
        lbl.textContent = r.label;
        group.appendChild(lbl);

        for (const g of GENDERS) {
            const btn = document.createElement('button');
            btn.className = 'cv-race-btn';
            btn.dataset.race = r.id;
            btn.dataset.gender = g.id;
            btn.textContent = g.label;
            if (r.id === selectedRace && g.id === selectedGender) {
                btn.classList.add('active');
            }
            btn.addEventListener('click', () => {
                wrap.querySelectorAll('.cv-race-btn.active').forEach(b => b.classList.remove('active'));
                btn.classList.add('active');
                selectedRace = r.id;
                selectedGender = g.id;
                onChange(selectedRace, selectedGender);
            });
            group.appendChild(btn);
        }
        wrap.appendChild(group);
    }
    el.appendChild(wrap);
}

/**
 * Build the tier-set side panel inside the canvas wrap. One row per
 * class, each row containing a class icon and five tier buttons
 * (T0, T0.5, T1, T2, T3). Disabled buttons render for sets the data
 * file doesn't have — keeps the grid even.
 *
 * Class column uses a 22px icon (one of the iconic vanilla item/spell
 * icons that visually represents each class) instead of a text label.
 * Full class name is in the title attribute for hover/screen-readers.
 *
 * Icon mapping is deliberately hard-coded here (not derived from any
 * DBC) because the choices are stylistic, not semantic — we want the
 * icon a player would recognize as "their class," which doesn't
 * correspond to any single field in ClassData.dbc. If a future skin
 * wants different icons, edit this map.
 */
function buildTierSidePanel(el, onSelect) {
    el.innerHTML = '';

    const CLASS_ICON = {
        Druid: 'inv_misc_monsterclaw_04',
        Hunter: 'inv_weapon_bow_07',
        Mage: 'inv_staff_13',
        Paladin: 'spell_holy_holybolt',
        Priest: 'inv_staff_30',
        Rogue: 'inv_throwingknife_04',
        Shaman: 'spell_nature_bloodlust',
        Warlock: 'spell_nature_faeriefire',
        Warrior: 'inv_sword_27',
    };

    const header = document.createElement('div');
    header.className = 'cv-tier-side-header';
    header.textContent = 'Tier Sets';
    el.appendChild(header);

    for (const cls of TIER_CLASSES) {
        const row = document.createElement('div');
        row.className = 'cv-tier-side-row';

        const iconName = CLASS_ICON[cls];
        if (iconName) {
            const icon = document.createElement('img');
            icon.className = 'cv-tier-side-class-icon';
            icon.src = `/icons/${iconName}.png`;
            icon.alt = cls;
            icon.title = cls;
            // Fall back to a text abbreviation if the icon fails to load
            // (e.g. someone deploys to a box where /icons/ wasn't synced).
            // No flash of broken-image — we replace the <img> with a
            // <span> at the first error event.
            icon.addEventListener('error', () => {
                const span = document.createElement('span');
                span.className = 'cv-tier-side-class';
                span.textContent = cls.slice(0, 2);
                span.title = cls;
                icon.replaceWith(span);
            }, { once: true });
            row.appendChild(icon);
        } else {
            const span = document.createElement('span');
            span.className = 'cv-tier-side-class';
            span.textContent = cls.slice(0, 2);
            span.title = cls;
            row.appendChild(span);
        }

        for (const tier of TIER_IDS) {
            const pieces = TIER_SETS[cls]?.[tier];
            const btn = document.createElement('button');
            btn.className = 'cv-tier-side-btn';
            // Show tier number without the "T" prefix for tight fit;
            // "0.5" still uses the dot.
            btn.textContent = tier.replace(/^T/, '');
            if (!pieces) {
                btn.disabled = true;
                btn.title = `${cls} ${tier} not available`;
            } else {
                btn.title = `${cls} ${tier}\n${pieces.map(p => '· ' + p.name).join('\n')}`;
                btn.addEventListener('click', () => onSelect(cls, tier));
            }
            row.appendChild(btn);
        }
        el.appendChild(row);
    }
}

// ────────────────────────────────────────────────────────────────────────
// Server hookups
// ────────────────────────────────────────────────────────────────────────

/**
 * Hit /Items/CharacterPreview to get the GLB + skin URLs for a given
 * (race, gender). The endpoint already exists; we just want the URLs
 * out of it.
 *
 * The CharacterPreview action returns an HTML view (not JSON). To avoid
 * adding a new JSON endpoint for one piece of metadata, we ask for the
 * page, then read the canvas's data attributes the server stamped.
 * Cheap and avoids touching the controller.
 *
 * If/when adding more state, switch to a dedicated JSON action — but
 * for now this is the smallest delta to the server.
 */
async function fetchCharacterUrls(race, gender) {
    const url = `/Items/CharacterPreview?race=${encodeURIComponent(race)}&gender=${encodeURIComponent(gender)}`;
    try {
        const res = await fetch(url, { credentials: 'same-origin' });
        if (!res.ok) throw new Error(`HTTP ${res.status}`);
        const html = await res.text();
        // Parse out the data-glb-url and data-skin-url attributes from
        // the rendered canvas tag. Cheap regex; the HTML is server-
        // controlled so we don't need a real parser.
        const glbMatch = /data-glb-url="([^"]+)"/.exec(html);
        const skinMatch = /data-skin-url="([^"]+)"/.exec(html);
        return {
            glbUrl: glbMatch ? glbMatch[1] : null,
            skinUrl: skinMatch ? skinMatch[1] : null,
        };
    } catch (err) {
        console.error('[items-character] fetchCharacterUrls failed', err);
        return { glbUrl: null, skinUrl: null };
    }
}

function escapeHtml(s) {
    return String(s).replace(/[&<>"']/g, c => ({
        '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;',
    }[c]));
}
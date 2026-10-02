# Gear Rendering Reference — how gear must render 1:1

**Source of truth:** the C# client **MSUIClient** (`../MSUIClient`) renders gear 1:1 with the
game. Its gear rules were themselves ported *from* this repo's SuperUI JS
(`region-rects.js`, `equip.js`, `geoset-rules.js`), and its geoset engine was ported
byte-faithfully from benilla's `geosets.rs`. This document captures that reference so we
stop re-deriving it. Where the JS and C# ever disagree, **MSUIClient wins.**

---

## TL;DR — the current bug is NOT in the JavaScript

A full trace (Aug 2026) verified every JS stage — `region-rects.js`, `geoset-rules.js`,
`compositor.js`, `equip.js`, `loader.js`, `viewer.js`, `dresser.js` — against MSUIClient.
**They match.** The JS is a faithful "paint whatever the server sends" layer and is
expansion-agnostic. The character body GLB even preserves real geoset IDs
(`SkinnedGlbWriter` names each mesh `Geoset_{id}_c{cat}_v{var}_s{idx}`).

The TBC/Wrath "not 1:1" originates **server-side**, in the legacy-DBC **column-shift
heuristic**. See [§7](#7-the-actual-tbcwrath-bug). Stop editing the JS renderer.

---

## 1. Gear is FOUR separate mechanisms (not one)

From `MSUIClient/World/Units/CharacterEquipment.cs` (the best single spec):

1. **Body atlas** — chest, legs, boots, gloves, bracers, belt, tabard have **no geometry**.
   They paint partial textures into one shared **256×256** skin texture at fixed rectangles.
2. **Geoset variants** — the same items switch which body sub-meshes draw (sleeves vs bare
   arms, boot vs bare foot). Driven by `ItemDisplayInfo.geosetGroup[]`.
3. **Attached models** — helms, shoulders, weapons, shields are **separate M2 files**
   mounted on the skeleton's attachment points. In this app they are served to the JS as
   **GLB URLs**.
4. **Cape** — geoset group 15 (`1501 + geosetGroup[0]`) + a BLP through M2 replaceable
   texture **type 2**.

A full tier set touches all four. "Fix the rendering" is really "make all four correct."

---

## 2. Body atlas (mechanism 1)

**Canvas:** 256×256 (`AtlasSize`). Larger skins are handled by uniform scaling
(`sx = width/256, sy = height/256`) — no per-expansion layout.

**Regions** (identical in `region-rects.js` `REGIONS` and C# `SlotRegions`):

| slot | region      | x   | y   | w   | h  | TextureComponents folder |
|------|-------------|-----|-----|-----|----|--------------------------|
| 0    | ArmUpper    | 0   | 0   | 128 | 64 | `ArmUpperTexture`   |
| 1    | ArmLower    | 0   | 64  | 128 | 64 | `ArmLowerTexture`   |
| 2    | Hand        | 0   | 128 | 128 | 32 | `HandTexture`       |
| 3    | TorsoUpper  | 128 | 0   | 128 | 64 | `TorsoUpperTexture` |
| 4    | TorsoLower  | 128 | 64  | 128 | 32 | `TorsoLowerTexture` |
| 5    | LegUpper    | 128 | 96  | 128 | 64 | `LegUpperTexture`   |
| 6    | LegLower    | 128 | 160 | 128 | 64 | `LegLowerTexture`   |
| 7    | Foot        | 128 | 224 | 128 | 32 | `FootTexture`       |

Face rects (`FaceUpper 0,160,128,32` / `FaceLower 0,192,128,64`) are **not** body slots —
they come from CharSections face BLPs, composited before gear.

**Which rect a component paints is its column index 0–7, not the item's inventory slot.**
`ItemDisplayInfo.m_texture[0..7]` map 1:1 to the table above.

**BLP path:** `Item\TextureComponents\{Folder}\{partialName}_{suffix}.blp`, trying suffixes
`_M`/`_F` (gender) → `_U` (unisex) → bare. The DBC partial already includes the region
suffix (e.g. `Robe_C_01Blue_Chest_TU`). (This app: `BodyAtlasTextureService.cs`.)

**Paint order** (lowest first; `equip.js` `PAINT_ORDER` == C# `PaintOrder`):
`Shirt(1), Legs(2), Chest/Robe(3), Feet(4), Wrists(5), Waist(6), Hands(7), Tabard(8), Cloak(9)`.
Broad garments first, then band-overlays (belt/bracers/gloves), then tabard/cape.

**Blit:** alpha src-over (paint sits *on* the skin, never cuts a hole). `flipY = false`.

---

## 3. Geoset selection (mechanism 2)

A sub-mesh's `skinSectionId` = **group×100 + variant**. Draw it iff selected.

**Groups:** 0 hair/scalp · 1/2/3 facial hair · 4 gloves · 5 boots · 6 base(always) ·
7 ears(default **702**) · 8 sleeves · 9 knees · 10 doublet · 11 legs · 12 tabard ·
13 robe skirt · 14 base(always) · 15 cape.

**Naked defaults** (`RegionBases`, group×100+1 except ears=702):
`1,101,201,301,401,501,601,702,801,901,1001,1101,1201,1301,1401,1501` — plus body mesh `0`.

**Authoritative algorithm** = `MSUIClient/Formats/CharacterGeosets.cs` `Visible()` (benilla
port, used for real players/NPCs). Item bodyslots in benilla order
`[0 shirt, 1 chest, 2 belt, 3 pants, 4 boots, 5 wrist, 6 gloves, 7 tabard]`:

```
robe = chest.gg[2]  ?? pants.gg[2]
gloves  present → clear 401-499, add 401+gg[0]      ; else chest sleeves add 801+gg[0]
shirt (no chest)                                     → add 801+gg[0]   (sleeves)
robe            → hide 501-599, 902-999, 1100-1199, 1300-1399 ; add 1301+robe (skirt)
  else boots    → add 501+gg[0]
  else kneepads → add 901+gg[1]
tabard (no robe)→ add 1201+gg[0]
shirt doublet   → add 1001+gg[1]
pant legs (no robe) → add 1102+gg[0]
cloak           → clear 1500-1599, add 1501+gg[0]
```

Belt (waist) and bracers (wrists) are **atlas-only** — they never change geosets.
`geosetGroup == 0` means "leave the default", **not** "hide".

**Hair / facial / helm-hide** come from `CharHairGeosets.dbc`,
`CharacterFacialHairStyles.dbc`, and `HelmetGeosetVisData.dbc` (a closed helm — unequal
`HelmetGeosetVis1/2` — forces hair/facial/ears back to base). This app derives the
`hidesHair` boolean server-side in the dressing endpoint.

> ⚠️ **Known JS drift (minor, all-expansions):** `geoset-rules.js` `SLOT_RULES` (the older
> SuperUI lineage) reads pants as `[[9,0,1],[11,1,1]]` (knees from `gg[0]`, legs from
> `gg[1]`), whereas benilla reads **legs from `gg[0]` (base 1102), knees from `gg[1]`
> (base 901)** — columns swapped + different base; the doublet offset also differs. These
> rules were tagged HAND-TUNED/UNVERIFIED and never reconciled to benilla. This is *not*
> the TBC/Wrath bug (it hits vanilla equally) but is worth aligning to `CharacterGeosets.cs`.

---

## 4. Attached models (mechanism 3)

Separate M2 → served as GLB. `ItemDisplayInfo` gives `ModelName1/2` and `ModelTexture1/2`.

**Folders:** Head→`Head`, Shoulders→`Shoulder`, Shield→`Shield`, else→`Weapon`
(under `Item\ObjectComponents\`). Texture: `Item\ObjectComponents\{Folder}\{ModelTexture}.blp`
(the DBC texture wins over the M2's embedded name).

**Attachment IDs** (edition-invariant): LeftWrist 0 · HandRight 1 · HandLeft 2 ·
ShoulderRight 5 · ShoulderLeft 6 · Helm 11 · BackR 26 · BackL 27 · ShieldBack 28 ·
BackLowerMain 30 · BackLowerOff 31 · HipMain 32 · HipOff 33.

**Slot → attachment:** Head→11, **Shield→0 (left wrist, not palm)**, OffHand→2, else→1.
**Shoulders = two mounts:** `ModelName1`→attach **6** (left), `ModelName2`→attach **5**
(right); no mirroring (vanilla ships distinct L/R files). Helm is the only per-race/gender
model (append race/gender code, e.g. `_HuM`).

**Sheathe (drawn/stowed):** drawn main→HandRight, shield→LeftWrist, off→HandLeft; stowed by
the item's `Sheath` byte → Back(26/27), BackLower(30/31), Hip(32/33), ShieldBack(28); bows
(ranged type 15) draw to HandLeft.

**Transform:** `T(attachment.Position) · charSkin[attachment.BoneIndex] · characterInstance`,
item drawn rigid in bind pose (its own bones only camera-face billboard sub-parts).
**Blend from M2 render flags:** 3/4 additive, 5 modulate, 6 mod2x, else alpha; `TwoSided`,
`NoZWrite`, `NoZTest` from the flag bits.

---

## 5. DBC layout — VANILLA 1.12 `ItemDisplayInfo.dbc` (23 fields / 92 bytes)

| field | meaning |
|-------|---------|
| 0 | ID |
| 1–2 | ModelName[2] |
| 3–4 | ModelTexture[2] |
| 5 | InventoryIcon (**one** icon in vanilla) |
| 6–8 | **GeosetGroup[3]** |
| 9 | Flags |
| 10 | SpellVisualID |
| 11 | GroupSoundIndex |
| 12–13 | HelmetGeosetVis[2] |
| 14–21 | **m_texture[8]** (the 8 body-atlas partials) |
| 22 | ItemVisual |

Base skin/face/hair come from `CharSections.dbc` (match: Skin by colour; Face/Hair by
variation+colour). `CharHairGeosets.dbc` maps a hairstyle number → the real group-0 geoset.

---

## 6. How this app wires it (server ↔ JS)

- **Vanilla dressing:** `GET /Items/ItemDressing` (`ItemsController.cs`) →
  `DbcService.LoadItemModelInfo` (hardcoded **vanilla 23-field** offsets) +
  `BodyAtlasTextureService` (BLP→PNG per slot). Returns `inventoryType`, `geosetGroup`,
  `bodyTextures` URLs, attachment **GLB URLs**, `hidesHair`, cape.
- **TBC/WotLK (forge lanes):** `GET /ArmorForge/{lane}Dressing` (`ArmorForgeController.LaneDressing`)
  → `LegacyArmorCatalog.GetDisplayRow`; import commits via `LegacyArmorImporter` +
  `CustomArmorBuildService.RegisterDisplayWithDbc`. Weapon glows via `LegacyItemVisualIndex`.
- **Character body GLB:** `CharacterModelService` → `SkinnedGlbWriter` (skinned; preserves
  geoset IDs in mesh names). **Item/attachment GLB:** `GlbWriter` (rigid; names `Geoset{idx}`
  by array index — fine, those aren't geoset-toggled).
- **Client:** `equip.js equipMultiple` → `compositor.paintBodyAtlasLayered` (atlas),
  `dresser.applyItemFilters`→`geoset-rules.resolveVisibleGeosets` (geosets),
  `dresser.mountAttachment` (attachments).

---

## 7. THE actual TBC/Wrath bug

The **only** place expansion branching happens is a fragile column-shift heuristic that
reads the later-client `ItemDisplayInfo.dbc`:

- `Services/ArmorForge/LegacyArmorCatalog.cs:173`
  `_componentBase = _displayDbc.FieldCount >= 25 ? 15 : 14;`
- `Services/WeaponForge/LegacyItemVisualIndex.cs:32`
  `VisualField(fieldCount) => fieldCount >= 25 ? 23 : 22;`

**Mechanism.** Post-vanilla clients insert a **second inventory icon at field 6**, which
shifts geosetGroup, helmetVis, groupSound, and all 8 `m_texture` stems up by one. The
correct base is 15 whenever that second icon is present. But the code infers the shift from
**total field count ≥ 25** — i.e. the presence of the *independent, trailing*
`particleColorID`. That is the wrong signal:

| client | fields | 2nd icon? | correct base | code gives |
|--------|--------|-----------|--------------|------------|
| 1.12 | 23 | no | 14 | 14 ✓ |
| 2.4.3 (stock) | **24** | **yes** | **15** | **14 ✗** |
| 3.3.5a (stock) | 25 | yes | 15 | 15 ✓ |
| 3.3.5a, particleColorID stripped | **24** | **yes** | **15** | **14 ✗** |

A 24-field TBC (or stripped-WotLK) record is read **one column short**: `GeosetGroup` reads
fields 6–8 (icon2 + geoset0 + geoset1) instead of 7–9; components read 14–21 instead of
15–22. Result: wrong glove/boot/robe geoset variants, wrong/empty component BLP stems, wrong
helm hair-hide → exactly "not 1:1". It hits **both** the forge pre-import preview **and** the
committed items-page render (both derive from `GetDisplayRow`). The plain vanilla items page
(23-field `DbcService`) is unaffected — which is why vanilla looks right and TBC/Wrath don't.

The confusion is documented in the code itself: `LegacyArmorCatalog.cs:123` assumes
"2.4.3 = 24 fields (vanilla + trailing particleColorID, shift 0)", and `ARMOR_FORGE.md`
notes the dev's own TBC client measured 25 fields (so it worked for them) while "one patch
layer read as 24/96 (no second icon, base 14)". That 24-field layer is almost certainly the
stock second-icon layout being mis-labeled.

**Confirm (one line):** log `_displayDbc.FieldCount` (and dump fields 6–9) for the mounted
TBC and WotLK `ItemDisplayInfo.dbc`. If TBC reports **24**, the base is wrong → confirmed.

**Fix:** detect the second inventory icon by **content/record-size**, not by total field
count — e.g. validate that fields 6–8 look like small geoset ints (0–5) vs 7–9, or map
base from the DBC record size / known client version. Everything downstream (importer,
`LaneDressing`, `RegisterDisplayWithDbc`, and the entire JS renderer) is already correct and
renders 1:1 once `GetDisplayRow` returns the right columns.

---

## 8. Latent traps (not today's break, but they will bite)

- **WotLK v264+ M2 in the live path.** The render `M2Reader.Parse` hard-rejects v264+
  (`Services/M2Handlers/M2Reader.cs:506`). Only `M2WotlkReader` handles WotLK (split
  external `.skin`), and it's wired **into the forge import lanes only**. A raw Wrath item
  M2 referenced outside the forge won't mount. Forged items are converted to v256 and are
  fine.
- **`DbcService.LoadItemModelInfo` does no width detection** — it only *warns* on non-92
  record size, then parses vanilla base-14. Harmless today (TBC/WotLK don't flow through it)
  but a trap if `DbcPath` is ever pointed at a later-client DBC.
- **`geoset-rules.js` pants/doublet drift** from benilla (see §3) — minor, all-expansions.

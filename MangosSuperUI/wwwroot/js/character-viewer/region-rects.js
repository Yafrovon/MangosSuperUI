// Character Viewer — Body atlas region rectangles.
//
// The vanilla WoW (build 5875) character body skin is a single 256×256 BLP
// atlas. Armor pieces paint into specific rectangular regions of that atlas
// at equip time. There's one atlas per (race, gender) and the SAME region
// layout for all 16 race/gender combos — wow.export and WMV both rely on
// this invariant. (If a race ever turns out to differ, we'd promote this
// table to per-race; vanilla doesn't.)
//
// === CONFIDENCE: CANONICAL (Session K, May 2026) ===
//
// === The full atlas layout ===
//
//   LEFT COLUMN (x=0, w=128)              RIGHT COLUMN (x=128, w=128)
//   ┌─────────────────────────┐           ┌─────────────────────────┐
//   │ armUpper      y=0  h=64 │           │ torsoUpper    y=0   h=64│
//   │ armLower      y=64 h=64 │           │ torsoLower    y=64  h=32│
//   │ hand          y=128 h=32│           │ (gap          y=96  h=0)│
//   │ faceUpper     y=160 h=32│           │ legUpper      y=96  h=64│
//   │ faceLower     y=192 h=64│           │ legLower      y=160 h=64│
//   │                         │           │ foot          y=224 h=32│
//   └─────────────────────────┘           └─────────────────────────┘
//
// Each column sums to 256 vertically:
//   left:  64 + 64 + 32 + 32 + 64 = 256
//   right: 64 + 32 + 64 + 64 + 32 = 256
//
// === Body slot rectangles ===
//
// Region semantics from the vanilla 1.12 m_texture[] slot table (verified
// by parsing all 23 fields of ItemDisplayInfo.dbc across robes, plate
// chests, cloth, boots, and gloves):
//
//   m_texture[0]  ArmUpper      — biceps/shoulders region of body atlas
//   m_texture[1]  ArmLower      — forearms region
//   m_texture[2]  Hand          — hand/wrist region
//   m_texture[3]  TorsoUpper    — chest region
//   m_texture[4]  TorsoLower    — belly/waist region
//   m_texture[5]  LegUpper      — thigh region (and robe upper for cat 13)
//   m_texture[6]  LegLower      — shin region (boots paint here)
//   m_texture[7]  Foot          — foot region
//
// === Face rectangles (faceUpper, faceLower) ===
//
// Included for completeness against the canonical layout. They are NOT
// reached by the m_texture[] slot pipeline — character faces are painted
// from CharSections.dbc (skin/face/hair textures) at a separate pipeline
// stage we haven't built yet. Listing the rects here documents the full
// layout and prevents anyone from accidentally re-using that atlas area
// for body slots.
//
// === Coordinate convention ===
//
// (x, y) is top-left of the rectangle in the 256×256 canvas; +y is DOWN
// (HTML canvas convention). When painting, the source armor PNG is drawn
// from (0,0) to fill (w,h) at (x,y) via ctx.drawImage(img, x, y, w, h),
// which produces a non-aspect-preserving stretch.
// flipY MUST be false on the resulting CanvasTexture — see risk #8 (closed).

/**
 * Canonical vanilla body-atlas region rectangles on the 256×256 logical canvas
 */
export const REGIONS = {
    // Left column.
    armUpper: { x: 0, y: 0, w: 128, h: 64 },   // shoulders / biceps
    armLower: { x: 0, y: 64, w: 128, h: 64 },   // forearms
    hand: { x: 0, y: 128, w: 128, h: 32 },   // hand / wrist
    faceUpper: { x: 0, y: 160, w: 128, h: 32 },   // (not used by body slots)
    faceLower: { x: 0, y: 192, w: 128, h: 64 },   // (not used by body slots)

    // Right column.
    torsoUpper: { x: 128, y: 0, w: 128, h: 64 },   // chest
    torsoLower: { x: 128, y: 64, w: 128, h: 32 },   // belly / waist
    legUpper: { x: 128, y: 96, w: 128, h: 64 },   // thigh / robe upper
    legLower: { x: 128, y: 160, w: 128, h: 64 },   // shin / robe lower
    foot: { x: 128, y: 224, w: 128, h: 32 },   // foot (boots paint here)
};

/**
 * Map M2 m_texture slot index → region key.
 *
 * Vanilla 1.12 ItemDisplayInfo.dbc has 8 m_texture[] slots (0-7) covering
 * the full body atlas. Suffix conventions verified against actual MPQ
 * contents (Session F/G).
 *
 *   slot 0 (_AU) → armUpper      shoulders/biceps
 *   slot 1 (_AL) → armLower      forearms
 *   slot 2 (_HA) → hand          hand/wrist
 *   slot 3 (_TU) → torsoUpper    chest
 *   slot 4 (_TL) → torsoLower    belly/waist
 *   slot 5 (_LU) → legUpper      thigh / robe upper
 *   slot 6 (_LL) → legLower      shin / robe lower (boots paint here)
 *   slot 7 (_FO) → foot          foot (boots paint here)
 *
 * Session K: slot 7 now routes to the canonical `foot` rect at
 * (128, 224, 128, 32). Previously routed to legLower as a placeholder.
 */
export const SLOT_TO_REGION = {
    0: 'armUpper',
    1: 'armLower',
    2: 'hand',
    3: 'torsoUpper',
    4: 'torsoLower',
    5: 'legUpper',
    6: 'legLower',
    7: 'foot',
};

/**
 * Distinct debug colors per region — chosen to be visually distinguishable
 * on a character model. Used by compositor.paintDebugRegions(). Each color
 * is a CSS color string suitable for canvas fillStyle.
 *
 * Face regions get muted colors since they're not part of the body slot
 * pipeline — including them so paintDebugRegions still labels every rect
 * but they don't draw attention away from body slots during verification.
 */
export const REGION_DEBUG_COLORS = {
    armUpper: '#ff0066',   // hot pink — biceps
    armLower: '#ff9900',   // orange — forearms
    hand: '#ffff00',   // yellow — hands
    torsoUpper: '#00ff00',   // bright green — chest
    torsoLower: '#00ffff',   // cyan — belly
    legUpper: '#9966ff',   // purple — thighs / robe upper
    legLower: '#ff66cc',   // pink — shins / robe lower
    foot: '#ff3300',   // red-orange — feet
    faceUpper: '#666666',   // muted gray — not a body slot
    faceLower: '#888888',   // muted gray — not a body slot
};

/**
 * Iterator helper: yields { key, rect, color } for each region.
 * Used by the debug compositor to paint each region a different color.
 */
export function eachRegion() {
    return Object.keys(REGIONS).map(key => ({
        key,
        rect: REGIONS[key],
        color: REGION_DEBUG_COLORS[key],
    }));
}
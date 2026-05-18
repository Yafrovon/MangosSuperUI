// Character Viewer — Vanilla AnimationData.dbc ID → friendly name map.
//
// Mirror of the server-side switch in SkinnedGlbWriter.AnimationName(int).
// Sourced from vanilla 1.12 (build 5875) AnimationData.dbc; the IDs are
// stable across the entire vanilla client.
//
// === When this is used ===
// SkinnedGlbWriter names baked clips using these labels. But if a future
// session adds an animationId we haven't transcribed here (server-side
// fallback: "AnimN"), the client picker can still produce a friendly
// label using this table — server-side fallback only happens when we add
// IDs to DefaultAnimationsToBake without updating the switch.
//
// The picker control resolves clip labels in this order:
//   1. Server-baked name (Three.js puts it in clip.name) — used as-is if
//      it doesn't look like the "AnimN" fallback pattern.
//   2. Strip the "Anim" prefix from "AnimN", parse N, look up here.
//   3. Fall back to the raw clip.name.
//
// === Why two copies (server + client) ===
// The server includes the name in the baked GLB, so usually the client
// table is unused. But:
//   - It lets us add new animations to the bake list without touching the
//     server switch (the client renders the friendly label from this table).
//   - It documents the full vanilla animation set in one place for human
//     readers.
//
// === IDs only — sound/probability data deliberately omitted ===
// AnimationData.dbc has more columns (fallback animation, behavior flags,
// random selection weight). We don't need those for the picker. If a
// future session implements wowhead-style auto-sequencing (Stand →
// StandVariation1 every N seconds), the table will need that data too.

export const ANIMATION_NAMES = {
    0: 'Stand',
    1: 'Death',
    2: 'Spell',
    3: 'Stop',
    4: 'Walk',
    5: 'Run',
    6: 'Dead',
    7: 'Rise',
    8: 'StandWound',
    9: 'CombatWound',
    10: 'CombatCritical',
    11: 'ShuffleLeft',
    12: 'ShuffleRight',
    13: 'Walkbackwards',
    14: 'Stun',
    15: 'HandsClosed',
    16: 'AttackUnarmed',
    17: 'Attack1H',
    18: 'Attack2H',
    19: 'Attack2HL',
    20: 'ParryUnarmed',
    21: 'Parry1H',
    22: 'Parry2H',
    23: 'Parry2HL',
    24: 'ShieldBlock',
    25: 'ReadyUnarmed',
    26: 'Ready1H',
    27: 'Ready2H',
    28: 'Ready2HL',
    29: 'ReadyBow',
    30: 'Dodge',
    31: 'SpellPrecast',
    32: 'SpellCast',
    33: 'SpellCastArea',
    34: 'NPCWelcome',
    35: 'NPCGoodbye',
    36: 'Block',
    37: 'JumpStart',
    38: 'Jump',
    39: 'JumpEnd',
    40: 'Fall',
    41: 'SwimIdle',
    42: 'Swim',
    43: 'SwimLeft',
    44: 'SwimRight',
    45: 'SwimBackwards',
    46: 'AttackBow',
    47: 'FireBow',
    48: 'ReadyRifle',
    49: 'AttackRifle',
    50: 'Loot',
    51: 'ReadySpellDirected',
    52: 'ReadySpellOmni',
    53: 'SpellCastDirected',
    54: 'SpellCastOmni',
    55: 'BattleRoar',
    56: 'ReadyAbility',
    57: 'Special1H',
    58: 'Special2H',
    59: 'ShieldBash',
    60: 'EmoteTalk',
    61: 'EmoteEat',
    62: 'EmoteWork',
    63: 'EmoteUseStanding',
    64: 'EmoteTalkExclamation',
    65: 'EmoteTalkQuestion',
    66: 'EmoteBow',
    67: 'EmoteWave',
    68: 'EmoteCheer',
    69: 'EmoteDance',
    70: 'EmoteLaugh',
    71: 'EmoteSleep',
    72: 'EmoteSitGround',
    73: 'EmoteRude',
    74: 'EmoteRoar',
    75: 'EmoteKneel',
    76: 'EmoteKiss',
    77: 'EmoteCry',
    78: 'EmoteChicken',
    79: 'EmoteBeg',
    80: 'EmoteApplaud',
    81: 'EmoteShout',
    82: 'EmoteFlex',
    83: 'EmoteShy',
    84: 'EmotePoint',
    85: 'Attack1HPierce',
    86: 'Attack2HLoosePierce',
    87: 'AttackOff',
    88: 'AttackOffPierce',
    89: 'Sheath',
    90: 'HipSheath',
    91: 'Mount',
    92: 'RunRight',
    93: 'RunLeft',
    94: 'MountSpecial',
    95: 'Kick',
    96: 'SitGroundDown',
    97: 'SitGround',
    98: 'SitGroundUp',
    99: 'SleepDown',
    100: 'Sleep',
    101: 'SleepUp',
    102: 'SitChairLow',
    103: 'SitChairMed',
    104: 'SitChairHigh',
    105: 'LoadBow',
    106: 'LoadRifle',
    107: 'AttackThrown',
    108: 'ReadyThrown',
    109: 'HoldBow',
    110: 'HoldRifle',
    111: 'HoldThrown',
    112: 'LoadThrown',
    113: 'EmoteSalute',
    114: 'KneelStart',
    115: 'KneelLoop',
    116: 'KneelEnd',
    117: 'AttackUnarmedOff',
    118: 'SpecialUnarmed',
    119: 'StealthWalk',
    120: 'StealthStand',
    121: 'Knockdown',
    122: 'EatingLoop',
    123: 'UseStandingLoop',
    124: 'ChannelCastDirected',
    125: 'ChannelCastOmni',
    126: 'Whirlwind',
    127: 'Birth',
    128: 'UseStandingStart',
    129: 'UseStandingEnd',
};

/**
 * Resolve a Three.js AnimationClip.name (as baked by SkinnedGlbWriter) into
 * a user-friendly display label.
 *
 * Server-baked names like "Stand", "Walk", "Run" pass through unchanged.
 * Names that look like the "AnimN" fallback are decoded via this table.
 * If neither rule applies (e.g. a custom clip injected by a future feature),
 * the raw name is returned.
 *
 * @param {string} clipName
 * @returns {string}
 */
export function friendlyClipName(clipName) {
    if (!clipName) return '(unnamed)';
    const m = /^Anim(\d+)$/.exec(clipName);
    if (m) {
        const id = parseInt(m[1], 10);
        return ANIMATION_NAMES[id] || clipName;
    }
    return clipName;
}

// MangosSuperUI — Vanilla tier-set data (T0, T0.5, T1, T2, T3)
//
// One row per piece: { name, itemId, displayId, slot }.
// `slot` is item_template.inventory_type:
//   1=head 3=shoulder 5=chest 6=waist 7=legs 8=feet 9=wrist
//   10=hands 11=finger 20=robe (back-compat chest variant)
//
// Source: dump_tiers.sql against item_template, with (entry,MAX(patch))
// dedup. Pieces ordered head→shoulder→chest→robe→waist→legs→feet→wrist
// →hands→ring so iterating the array reads top-down on a paper doll.

export const TIER_SETS = {
    Druid: {
        'T0': [
            { name: 'Wildheart Cowl', itemId: 16720, displayId: 31228, slot: 1 }, // head
            { name: 'Wildheart Spaulders', itemId: 16718, displayId: 30412, slot: 3 }, // shoulder
            { name: 'Wildheart Vest', itemId: 16706, displayId: 29974, slot: 5 }, // chest
            { name: 'Wildheart Belt', itemId: 16716, displayId: 29976, slot: 6 }, // waist
            { name: 'Wildheart Kilt', itemId: 16719, displayId: 29975, slot: 7 }, // legs
            { name: 'Wildheart Boots', itemId: 16715, displayId: 29981, slot: 8 }, // feet
            { name: 'Wildheart Bracers', itemId: 16714, displayId: 29977, slot: 9 }, // wrist
            { name: 'Wildheart Gloves', itemId: 16717, displayId: 29979, slot: 10 }, // hands
        ],
        'T0.5': [
            { name: 'Feralheart Cowl', itemId: 22109, displayId: 34639, slot: 1 }, // head
            { name: 'Feralheart Spaulders', itemId: 22112, displayId: 34643, slot: 3 }, // shoulder
            { name: 'Feralheart Vest', itemId: 22113, displayId: 34644, slot: 5 }, // chest
            { name: 'Feralheart Belt', itemId: 22106, displayId: 34637, slot: 6 }, // waist
            { name: 'Feralheart Kilt', itemId: 22111, displayId: 34642, slot: 7 }, // legs
            { name: 'Feralheart Boots', itemId: 22107, displayId: 34638, slot: 8 }, // feet
            { name: 'Feralheart Bracers', itemId: 22108, displayId: 34641, slot: 9 }, // wrist
            { name: 'Feralheart Gloves', itemId: 22110, displayId: 34640, slot: 10 }, // hands
        ],
        'T1': [
            { name: 'Cenarion Helm', itemId: 16834, displayId: 32790, slot: 1 }, // head
            { name: 'Cenarion Spaulders', itemId: 16836, displayId: 32016, slot: 3 }, // shoulder
            { name: 'Cenarion Vestments', itemId: 16833, displayId: 31797, slot: 20 }, // robe
            { name: 'Cenarion Belt', itemId: 16828, displayId: 31722, slot: 6 }, // waist
            { name: 'Cenarion Leggings', itemId: 16835, displayId: 31729, slot: 7 }, // legs
            { name: 'Cenarion Boots', itemId: 16829, displayId: 31724, slot: 8 }, // feet
            { name: 'Cenarion Bracers', itemId: 16830, displayId: 31725, slot: 9 }, // wrist
            { name: 'Cenarion Gloves', itemId: 16831, displayId: 31726, slot: 10 }, // hands
        ],
        'T2': [
            { name: 'Stormrage Cover', itemId: 16900, displayId: 33655, slot: 1 }, // head
            { name: 'Stormrage Pauldrons', itemId: 16902, displayId: 30546, slot: 3 }, // shoulder
            { name: 'Stormrage Chestguard', itemId: 16897, displayId: 30536, slot: 5 }, // chest
            { name: 'Stormrage Belt', itemId: 16903, displayId: 30541, slot: 6 }, // waist
            { name: 'Stormrage Legguards', itemId: 16901, displayId: 30540, slot: 7 }, // legs
            { name: 'Stormrage Boots', itemId: 16898, displayId: 30542, slot: 8 }, // feet
            { name: 'Stormrage Bracers', itemId: 16904, displayId: 30548, slot: 9 }, // wrist
            { name: 'Stormrage Handguards', itemId: 16899, displayId: 34016, slot: 10 }, // hands
        ],
        'T3': [
            { name: 'Dreamwalker Headpiece', itemId: 22490, displayId: 35162, slot: 1 }, // head
            { name: 'Dreamwalker Spaulders', itemId: 22491, displayId: 35160, slot: 3 }, // shoulder
            { name: 'Dreamwalker Tunic', itemId: 22488, displayId: 35159, slot: 5 }, // chest
            { name: 'Dreamwalker Girdle', itemId: 22494, displayId: 35164, slot: 6 }, // waist
            { name: 'Dreamwalker Legguards', itemId: 22489, displayId: 35161, slot: 7 }, // legs
            { name: 'Dreamwalker Boots', itemId: 22492, displayId: 35173, slot: 8 }, // feet
            { name: 'Dreamwalker Wristguards', itemId: 22495, displayId: 35158, slot: 9 }, // wrist
            { name: 'Dreamwalker Handguards', itemId: 22493, displayId: 35167, slot: 10 }, // hands
        ],
    },
    Hunter: {
        'T0': [
            { name: "Beaststalker's Cap", itemId: 16677, displayId: 31410, slot: 1 }, // head
            { name: "Beaststalker's Mantle", itemId: 16679, displayId: 31409, slot: 3 }, // shoulder
            { name: "Beaststalker's Tunic", itemId: 16674, displayId: 31402, slot: 5 }, // chest
            { name: "Beaststalker's Belt", itemId: 16680, displayId: 31404, slot: 6 }, // waist
            { name: "Beaststalker's Pants", itemId: 16678, displayId: 31403, slot: 7 }, // legs
            { name: "Beaststalker's Boots", itemId: 16675, displayId: 31408, slot: 8 }, // feet
            { name: "Beaststalker's Bindings", itemId: 16681, displayId: 31405, slot: 9 }, // wrist
            { name: "Beaststalker's Gloves", itemId: 16676, displayId: 31406, slot: 10 }, // hands
        ],
        'T0.5': [
            { name: "Beastmaster's Cap", itemId: 22013, displayId: 34649, slot: 1 }, // head
            { name: "Beastmaster's Mantle", itemId: 22016, displayId: 34651, slot: 3 }, // shoulder
            { name: "Beastmaster's Tunic", itemId: 22060, displayId: 34645, slot: 5 }, // chest
            { name: "Beastmaster's Belt", itemId: 22010, displayId: 34646, slot: 6 }, // waist
            { name: "Beastmaster's Pants", itemId: 22017, displayId: 34652, slot: 7 }, // legs
            { name: "Beastmaster's Boots", itemId: 22061, displayId: 34648, slot: 8 }, // feet
            { name: "Beastmaster's Bindings", itemId: 22011, displayId: 34647, slot: 9 }, // wrist
            { name: "Beastmaster's Gloves", itemId: 22015, displayId: 34650, slot: 10 }, // hands
        ],
        'T1': [
            { name: "Giantstalker's Helmet", itemId: 16846, displayId: 32028, slot: 1 }, // head
            { name: "Giantstalker's Epaulets", itemId: 16848, displayId: 32030, slot: 3 }, // shoulder
            { name: "Giantstalker's Breastplate", itemId: 16845, displayId: 32022, slot: 5 }, // chest
            { name: "Giantstalker's Belt", itemId: 16851, displayId: 32019, slot: 6 }, // waist
            { name: "Giantstalker's Leggings", itemId: 16847, displayId: 32029, slot: 7 }, // legs
            { name: "Giantstalker's Boots", itemId: 16849, displayId: 32040, slot: 8 }, // feet
            { name: "Giantstalker's Bracers", itemId: 16850, displayId: 32021, slot: 9 }, // wrist
            { name: "Giantstalker's Gloves", itemId: 16852, displayId: 32024, slot: 10 }, // hands
        ],
        'T2': [
            { name: "Dragonstalker's Helm", itemId: 16939, displayId: 34367, slot: 1 }, // head
            { name: "Dragonstalker's Spaulders", itemId: 16937, displayId: 34091, slot: 3 }, // shoulder
            { name: "Dragonstalker's Breastplate", itemId: 16942, displayId: 33667, slot: 5 }, // chest
            { name: "Dragonstalker's Belt", itemId: 16936, displayId: 33665, slot: 6 }, // waist
            { name: "Dragonstalker's Legguards", itemId: 16938, displayId: 33672, slot: 7 }, // legs
            { name: "Dragonstalker's Greaves", itemId: 16941, displayId: 34269, slot: 8 }, // feet
            { name: "Dragonstalker's Bracers", itemId: 16935, displayId: 33666, slot: 9 }, // wrist
            { name: "Dragonstalker's Gauntlets", itemId: 16940, displayId: 33668, slot: 10 }, // hands
        ],
        'T3': [
            { name: 'Cryptstalker Headpiece', itemId: 22438, displayId: 35601, slot: 1 }, // head
            { name: 'Cryptstalker Spaulders', itemId: 22439, displayId: 35611, slot: 3 }, // shoulder
            { name: 'Cryptstalker Tunic', itemId: 22436, displayId: 35415, slot: 5 }, // chest
            { name: 'Cryptstalker Girdle', itemId: 22442, displayId: 35410, slot: 6 }, // waist
            { name: 'Cryptstalker Legguards', itemId: 22437, displayId: 35413, slot: 7 }, // legs
            { name: 'Cryptstalker Boots', itemId: 22440, displayId: 35409, slot: 8 }, // feet
            { name: 'Cryptstalker Wristguards', itemId: 22443, displayId: 35416, slot: 9 }, // wrist
            { name: 'Cryptstalker Handguards', itemId: 22441, displayId: 35411, slot: 10 }, // hands
        ],
    },
    Mage: {
        'T0': [
            { name: "Magister's Crown", itemId: 16686, displayId: 31087, slot: 1 }, // head
            { name: "Magister's Mantle", itemId: 16689, displayId: 30211, slot: 3 }, // shoulder
            { name: "Magister's Robes", itemId: 16688, displayId: 29591, slot: 20 }, // robe
            { name: "Magister's Belt", itemId: 16685, displayId: 29596, slot: 6 }, // waist
            { name: "Magister's Leggings", itemId: 16687, displayId: 29273, slot: 7 }, // legs
            { name: "Magister's Boots", itemId: 16682, displayId: 29594, slot: 8 }, // feet
            { name: "Magister's Bindings", itemId: 16683, displayId: 29597, slot: 9 }, // wrist
            { name: "Magister's Gloves", itemId: 16684, displayId: 29593, slot: 10 }, // hands
        ],
        'T0.5': [
            { name: "Sorcerer's Crown", itemId: 22065, displayId: 34602, slot: 1 }, // head
            { name: "Sorcerer's Mantle", itemId: 22068, displayId: 34597, slot: 3 }, // shoulder
            { name: "Sorcerer's Robes", itemId: 22069, displayId: 34596, slot: 20 }, // robe
            { name: "Sorcerer's Belt", itemId: 22062, displayId: 34599, slot: 6 }, // waist
            { name: "Sorcerer's Leggings", itemId: 22067, displayId: 34598, slot: 7 }, // legs
            { name: "Sorcerer's Boots", itemId: 22064, displayId: 34782, slot: 8 }, // feet
            { name: "Sorcerer's Bindings", itemId: 22063, displayId: 34601, slot: 9 }, // wrist
            { name: "Sorcerer's Gloves", itemId: 22066, displayId: 34600, slot: 10 }, // hands
        ],
        'T1': [
            { name: 'Arcanist Crown', itemId: 16795, displayId: 31517, slot: 1 }, // head
            { name: 'Arcanist Mantle', itemId: 16797, displayId: 30586, slot: 3 }, // shoulder
            { name: 'Arcanist Robes', itemId: 16798, displayId: 30581, slot: 20 }, // robe
            { name: 'Arcanist Belt', itemId: 16802, displayId: 30583, slot: 6 }, // waist
            { name: 'Arcanist Leggings', itemId: 16796, displayId: 30582, slot: 7 }, // legs
            { name: 'Arcanist Boots', itemId: 16800, displayId: 30587, slot: 8 }, // feet
            { name: 'Arcanist Bindings', itemId: 16799, displayId: 30584, slot: 9 }, // wrist
            { name: 'Arcanist Gloves', itemId: 16801, displayId: 30585, slot: 10 }, // hands
        ],
        'T2': [
            { name: 'Netherwind Crown', itemId: 16914, displayId: 34218, slot: 1 }, // head
            { name: 'Netherwind Mantle', itemId: 16917, displayId: 34254, slot: 3 }, // shoulder
            { name: 'Netherwind Robes', itemId: 16916, displayId: 34038, slot: 20 }, // robe
            { name: 'Netherwind Belt', itemId: 16818, displayId: 34046, slot: 6 }, // waist
            { name: 'Netherwind Pants', itemId: 16915, displayId: 34039, slot: 7 }, // legs
            { name: 'Netherwind Boots', itemId: 16912, displayId: 34044, slot: 8 }, // feet
            { name: 'Netherwind Bindings', itemId: 16918, displayId: 34045, slot: 9 }, // wrist
            { name: 'Netherwind Gloves', itemId: 16913, displayId: 34041, slot: 10 }, // hands
        ],
        'T3': [
            { name: 'Frostfire Circlet', itemId: 22498, displayId: 36440, slot: 1 }, // head
            { name: 'Frostfire Shoulderpads', itemId: 22499, displayId: 35326, slot: 3 }, // shoulder
            { name: 'Frostfire Robe', itemId: 22496, displayId: 35523, slot: 20 }, // robe
            { name: 'Frostfire Belt', itemId: 22502, displayId: 35519, slot: 6 }, // waist
            { name: 'Frostfire Leggings', itemId: 22497, displayId: 35522, slot: 7 }, // legs
            { name: 'Frostfire Sandals', itemId: 22500, displayId: 35525, slot: 8 }, // feet
            { name: 'Frostfire Bindings', itemId: 22503, displayId: 35677, slot: 9 }, // wrist
            { name: 'Frostfire Gloves', itemId: 22501, displayId: 35521, slot: 10 }, // hands
            { name: 'Frostfire Ring', itemId: 23062, displayId: 35472, slot: 11 }, // finger
        ],
    },
    Paladin: {
        'T0': [
            { name: 'Lightforge Helm', itemId: 16727, displayId: 31207, slot: 1 }, // head
            { name: 'Lightforge Spaulders', itemId: 16729, displayId: 29971, slot: 3 }, // shoulder
            { name: 'Lightforge Breastplate', itemId: 16726, displayId: 29969, slot: 5 }, // chest
            { name: 'Lightforge Belt', itemId: 16723, displayId: 29966, slot: 6 }, // waist
            { name: 'Lightforge Legplates', itemId: 16728, displayId: 29972, slot: 7 }, // legs
            { name: 'Lightforge Boots', itemId: 16725, displayId: 29967, slot: 8 }, // feet
            { name: 'Lightforge Bracers', itemId: 16722, displayId: 29968, slot: 9 }, // wrist
            { name: 'Lightforge Gauntlets', itemId: 16724, displayId: 29970, slot: 10 }, // hands
        ],
        'T0.5': [
            { name: 'Soulforge Helm', itemId: 22091, displayId: 34524, slot: 1 }, // head
            { name: 'Soulforge Spaulders', itemId: 22093, displayId: 34526, slot: 3 }, // shoulder
            { name: 'Soulforge Breastplate', itemId: 22089, displayId: 34519, slot: 5 }, // chest
            { name: 'Soulforge Belt', itemId: 22086, displayId: 34520, slot: 6 }, // waist
            { name: 'Soulforge Legplates', itemId: 22092, displayId: 34525, slot: 7 }, // legs
            { name: 'Soulforge Boots', itemId: 22087, displayId: 34521, slot: 8 }, // feet
            { name: 'Soulforge Bracers', itemId: 22088, displayId: 34522, slot: 9 }, // wrist
            { name: 'Soulforge Gauntlets', itemId: 22090, displayId: 34523, slot: 10 }, // hands
        ],
        'T1': [
            { name: 'Lawbringer Helm', itemId: 16854, displayId: 31506, slot: 1 }, // head
            { name: 'Lawbringer Spaulders', itemId: 16856, displayId: 31510, slot: 3 }, // shoulder
            { name: 'Lawbringer Chestguard', itemId: 16853, displayId: 31505, slot: 5 }, // chest
            { name: 'Lawbringer Belt', itemId: 16858, displayId: 31353, slot: 6 }, // waist
            { name: 'Lawbringer Legplates', itemId: 16855, displayId: 31352, slot: 7 }, // legs
            { name: 'Lawbringer Boots', itemId: 16859, displayId: 31354, slot: 8 }, // feet
            { name: 'Lawbringer Bracers', itemId: 16857, displayId: 31509, slot: 9 }, // wrist
            { name: 'Lawbringer Gauntlets', itemId: 16860, displayId: 31507, slot: 10 }, // hands
        ],
        'T2': [
            { name: 'Judgement Crown', itemId: 16955, displayId: 34216, slot: 1 }, // head
            { name: 'Judgement Spaulders', itemId: 16953, displayId: 34258, slot: 3 }, // shoulder
            { name: 'Judgement Breastplate', itemId: 16958, displayId: 33635, slot: 5 }, // chest
            { name: 'Judgement Belt', itemId: 16952, displayId: 33633, slot: 6 }, // waist
            { name: 'Judgement Legplates', itemId: 16954, displayId: 33637, slot: 7 }, // legs
            { name: 'Judgement Sabatons', itemId: 16957, displayId: 33639, slot: 8 }, // feet
            { name: 'Judgement Bindings', itemId: 16951, displayId: 33634, slot: 9 }, // wrist
            { name: 'Judgement Gauntlets', itemId: 16956, displayId: 33636, slot: 10 }, // hands
        ],
        'T3': [
            { name: 'Redemption Headpiece', itemId: 22428, displayId: 36972, slot: 1 }, // head
            { name: 'Redemption Spaulders', itemId: 22429, displayId: 35617, slot: 3 }, // shoulder
            { name: 'Redemption Tunic', itemId: 22425, displayId: 35618, slot: 5 }, // chest
            { name: 'Redemption Girdle', itemId: 22431, displayId: 35614, slot: 6 }, // waist
            { name: 'Redemption Legguards', itemId: 22427, displayId: 35616, slot: 7 }, // legs
            { name: 'Redemption Boots', itemId: 22430, displayId: 35613, slot: 8 }, // feet
            { name: 'Redemption Wristguards', itemId: 22424, displayId: 35619, slot: 9 }, // wrist
            { name: 'Redemption Handguards', itemId: 22426, displayId: 35615, slot: 10 }, // hands
        ],
    },
    Priest: {
        'T0': [
            { name: 'Devout Crown', itemId: 16693, displayId: 31104, slot: 1 }, // head
            { name: 'Devout Mantle', itemId: 16695, displayId: 31103, slot: 3 }, // shoulder
            { name: 'Devout Robe', itemId: 16690, displayId: 30422, slot: 20 }, // robe
            { name: 'Devout Belt', itemId: 16696, displayId: 30425, slot: 6 }, // waist
            { name: 'Devout Skirt', itemId: 16694, displayId: 30424, slot: 7 }, // legs
            { name: 'Devout Sandals', itemId: 16691, displayId: 30430, slot: 8 }, // feet
            { name: 'Devout Bracers', itemId: 16697, displayId: 30426, slot: 9 }, // wrist
            { name: 'Devout Gloves', itemId: 16692, displayId: 30427, slot: 10 }, // hands
        ],
        'T0.5': [
            { name: 'Virtuous Crown', itemId: 22080, displayId: 34630, slot: 1 }, // head
            { name: 'Virtuous Mantle', itemId: 22082, displayId: 34632, slot: 3 }, // shoulder
            { name: 'Virtuous Robe', itemId: 22083, displayId: 34633, slot: 20 }, // robe
            { name: 'Virtuous Belt', itemId: 22078, displayId: 34628, slot: 6 }, // waist
            { name: 'Virtuous Skirt', itemId: 22085, displayId: 34635, slot: 7 }, // legs
            { name: 'Virtuous Sandals', itemId: 22084, displayId: 34634, slot: 8 }, // feet
            { name: 'Virtuous Bracers', itemId: 22079, displayId: 34629, slot: 9 }, // wrist
            { name: 'Virtuous Gloves', itemId: 22081, displayId: 34631, slot: 10 }, // hands
        ],
        'T1': [
            { name: 'Circlet of Prophecy', itemId: 16813, displayId: 31371, slot: 1 }, // head
            { name: 'Mantle of Prophecy', itemId: 16816, displayId: 30623, slot: 3 }, // shoulder
            { name: 'Robes of Prophecy', itemId: 16815, displayId: 31490, slot: 20 }, // robe
            { name: 'Girdle of Prophecy', itemId: 16817, displayId: 30621, slot: 6 }, // waist
            { name: 'Pants of Prophecy', itemId: 16814, displayId: 28198, slot: 7 }, // legs
            { name: 'Boots of Prophecy', itemId: 16811, displayId: 31718, slot: 8 }, // feet
            { name: 'Vambraces of Prophecy', itemId: 16819, displayId: 30617, slot: 9 }, // wrist
            { name: 'Gloves of Prophecy', itemId: 16812, displayId: 30620, slot: 10 }, // hands
        ],
        'T2': [
            { name: 'Halo of Transcendence', itemId: 16921, displayId: 34233, slot: 1 }, // head
            { name: 'Pauldrons of Transcendence', itemId: 16924, displayId: 34048, slot: 3 }, // shoulder
            { name: 'Robes of Transcendence', itemId: 16923, displayId: 34047, slot: 20 }, // robe
            { name: 'Belt of Transcendence', itemId: 16925, displayId: 34053, slot: 6 }, // waist
            { name: 'Leggings of Transcendence', itemId: 16922, displayId: 34049, slot: 7 }, // legs
            { name: 'Boots of Transcendence', itemId: 16919, displayId: 34055, slot: 8 }, // feet
            { name: 'Bindings of Transcendence', itemId: 16926, displayId: 34052, slot: 9 }, // wrist
            { name: 'Handguards of Transcendence', itemId: 16920, displayId: 34051, slot: 10 }, // hands
        ],
        'T3': [
            { name: 'Circlet of Faith', itemId: 22514, displayId: 35155, slot: 1 }, // head
            { name: 'Shoulderpads of Faith', itemId: 22515, displayId: 35149, slot: 3 }, // shoulder
            { name: 'Robe of Faith', itemId: 22512, displayId: 36354, slot: 20 }, // robe
            { name: 'Belt of Faith', itemId: 22518, displayId: 35143, slot: 6 }, // waist
            { name: 'Leggings of Faith', itemId: 22513, displayId: 35154, slot: 7 }, // legs
            { name: 'Sandals of Faith', itemId: 22516, displayId: 35148, slot: 8 }, // feet
            { name: 'Bindings of Faith', itemId: 22519, displayId: 35144, slot: 9 }, // wrist
            { name: 'Gloves of Faith', itemId: 22517, displayId: 35145, slot: 10 }, // hands
            { name: 'Ring of Faith', itemId: 23061, displayId: 35472, slot: 11 }, // finger
        ],
    },
    Rogue: {
        'T0': [
            { name: 'Shadowcraft Cap', itemId: 16707, displayId: 28180, slot: 1 }, // head
            { name: 'Shadowcraft Spaulders', itemId: 16708, displayId: 28179, slot: 3 }, // shoulder
            { name: 'Shadowcraft Tunic', itemId: 16721, displayId: 28160, slot: 5 }, // chest
            { name: 'Shadowcraft Belt', itemId: 16713, displayId: 28177, slot: 6 }, // waist
            { name: 'Shadowcraft Pants', itemId: 16709, displayId: 28161, slot: 7 }, // legs
            { name: 'Shadowcraft Boots', itemId: 16711, displayId: 28162, slot: 8 }, // feet
            { name: 'Shadowcraft Bracers', itemId: 16710, displayId: 24190, slot: 9 }, // wrist
            { name: 'Shadowcraft Gloves', itemId: 16712, displayId: 28166, slot: 10 }, // hands
        ],
        'T0.5': [
            { name: 'Darkmantle Cap', itemId: 22005, displayId: 34700, slot: 1 }, // head
            { name: 'Darkmantle Spaulders', itemId: 22008, displayId: 34688, slot: 3 }, // shoulder
            { name: 'Darkmantle Tunic', itemId: 22009, displayId: 34689, slot: 5 }, // chest
            { name: 'Darkmantle Belt', itemId: 22002, displayId: 34699, slot: 6 }, // waist
            { name: 'Darkmantle Pants', itemId: 22007, displayId: 34687, slot: 7 }, // legs
            { name: 'Darkmantle Boots', itemId: 22003, displayId: 34684, slot: 8 }, // feet
            { name: 'Darkmantle Bracers', itemId: 22004, displayId: 34685, slot: 9 }, // wrist
            { name: 'Darkmantle Gloves', itemId: 22006, displayId: 34686, slot: 10 }, // hands
        ],
        'T1': [
            { name: 'Nightslayer Cover', itemId: 16821, displayId: 31514, slot: 1 }, // head
            { name: 'Nightslayer Shoulder Pads', itemId: 16823, displayId: 31504, slot: 3 }, // shoulder
            { name: 'Nightslayer Chestpiece', itemId: 16820, displayId: 31105, slot: 5 }, // chest
            { name: 'Nightslayer Belt', itemId: 16827, displayId: 31339, slot: 6 }, // waist
            { name: 'Nightslayer Pants', itemId: 16822, displayId: 31340, slot: 7 }, // legs
            { name: 'Nightslayer Boots', itemId: 16824, displayId: 31109, slot: 8 }, // feet
            { name: 'Nightslayer Bracelets', itemId: 16825, displayId: 31106, slot: 9 }, // wrist
            { name: 'Nightslayer Gloves', itemId: 16826, displayId: 31503, slot: 10 }, // hands
        ],
        'T2': [
            { name: 'Bloodfang Hood', itemId: 16908, displayId: 33743, slot: 1 }, // head
            { name: 'Bloodfang Spaulders', itemId: 16832, displayId: 33653, slot: 3 }, // shoulder
            { name: 'Bloodfang Chestpiece', itemId: 16905, displayId: 33650, slot: 5 }, // chest
            { name: 'Bloodfang Belt', itemId: 16910, displayId: 31110, slot: 6 }, // waist
            { name: 'Bloodfang Pants', itemId: 16909, displayId: 31115, slot: 7 }, // legs
            { name: 'Bloodfang Boots', itemId: 16906, displayId: 31111, slot: 8 }, // feet
            { name: 'Bloodfang Bracers', itemId: 16911, displayId: 31127, slot: 9 }, // wrist
            { name: 'Bloodfang Gloves', itemId: 16907, displayId: 33651, slot: 10 }, // hands
        ],
        'T3': [
            { name: 'Bonescythe Helmet', itemId: 22478, displayId: 35132, slot: 1 }, // head
            { name: 'Bonescythe Pauldrons', itemId: 22479, displayId: 35064, slot: 3 }, // shoulder
            { name: 'Bonescythe Breastplate', itemId: 22476, displayId: 35054, slot: 5 }, // chest
            { name: 'Bonescythe Waistguard', itemId: 22482, displayId: 36349, slot: 6 }, // waist
            { name: 'Bonescythe Legplates', itemId: 22477, displayId: 35065, slot: 7 }, // legs
            { name: 'Bonescythe Sabatons', itemId: 22480, displayId: 36351, slot: 8 }, // feet
            { name: 'Bonescythe Bracers', itemId: 22483, displayId: 35053, slot: 9 }, // wrist
            { name: 'Bonescythe Gauntlets', itemId: 22481, displayId: 35055, slot: 10 }, // hands
            { name: 'Bonescythe Ring', itemId: 23060, displayId: 35472, slot: 11 }, // finger
        ],
    },
    Shaman: {
        'T0': [
            { name: 'Coif of Elements', itemId: 16667, displayId: 31117, slot: 1 }, // head
            { name: 'Pauldrons of Elements', itemId: 16669, displayId: 30925, slot: 3 }, // shoulder
            { name: 'Vest of Elements', itemId: 16666, displayId: 31416, slot: 5 }, // chest
            { name: 'Cord of Elements', itemId: 16673, displayId: 31413, slot: 6 }, // waist
            { name: 'Kilt of Elements', itemId: 16668, displayId: 31415, slot: 7 }, // legs
            { name: 'Boots of Elements', itemId: 16670, displayId: 31412, slot: 8 }, // feet
            { name: 'Bindings of Elements', itemId: 16671, displayId: 31411, slot: 9 }, // wrist
            { name: 'Gauntlets of Elements', itemId: 16672, displayId: 31414, slot: 10 }, // hands
        ],
        'T0.5': [
            { name: 'Coif of The Five Thunders', itemId: 22097, displayId: 34693, slot: 1 }, // head
            { name: 'Pauldrons of The Five Thunders', itemId: 22101, displayId: 34697, slot: 3 }, // shoulder
            { name: 'Vest of The Five Thunders', itemId: 22102, displayId: 34698, slot: 5 }, // chest
            { name: 'Cord of The Five Thunders', itemId: 22098, displayId: 34694, slot: 6 }, // waist
            { name: 'Kilt of The Five Thunders', itemId: 22100, displayId: 34696, slot: 7 }, // legs
            { name: 'Boots of The Five Thunders', itemId: 22096, displayId: 34692, slot: 8 }, // feet
            { name: 'Bindings of The Five Thunders', itemId: 22095, displayId: 34691, slot: 9 }, // wrist
            { name: 'Gauntlets of The Five Thunders', itemId: 22099, displayId: 34695, slot: 10 }, // hands
        ],
        'T1': [
            { name: 'Earthfury Helmet', itemId: 16842, displayId: 31835, slot: 1 }, // head
            { name: 'Earthfury Epaulets', itemId: 16844, displayId: 31833, slot: 3 }, // shoulder
            { name: 'Earthfury Vestments', itemId: 16841, displayId: 31832, slot: 20 }, // robe
            { name: 'Earthfury Belt', itemId: 16838, displayId: 31829, slot: 6 }, // waist
            { name: 'Earthfury Legguards', itemId: 16843, displayId: 31836, slot: 7 }, // legs
            { name: 'Earthfury Boots', itemId: 16837, displayId: 31830, slot: 8 }, // feet
            { name: 'Earthfury Bracers', itemId: 16840, displayId: 31831, slot: 9 }, // wrist
            { name: 'Earthfury Gauntlets', itemId: 16839, displayId: 31834, slot: 10 }, // hands
        ],
        'T2': [
            { name: 'Helmet of Ten Storms', itemId: 16947, displayId: 34217, slot: 1 }, // head
            { name: 'Epaulets of Ten Storms', itemId: 16945, displayId: 34255, slot: 3 }, // shoulder
            { name: 'Breastplate of Ten Storms', itemId: 16950, displayId: 34081, slot: 5 }, // chest
            { name: 'Belt of Ten Storms', itemId: 16944, displayId: 34078, slot: 6 }, // waist
            { name: 'Legplates of Ten Storms', itemId: 16946, displayId: 34084, slot: 7 }, // legs
            { name: 'Greaves of Ten Storms', itemId: 16949, displayId: 34083, slot: 8 }, // feet
            { name: 'Bracers of Ten Storms', itemId: 16943, displayId: 34079, slot: 9 }, // wrist
            { name: 'Gauntlets of Ten Storms', itemId: 16948, displayId: 34082, slot: 10 }, // hands
        ],
        'T3': [
            { name: 'Earthshatter Headpiece', itemId: 22466, displayId: 37056, slot: 1 }, // head
            { name: 'Earthshatter Spaulders', itemId: 22467, displayId: 35751, slot: 3 }, // shoulder
            { name: 'Earthshatter Tunic', itemId: 22464, displayId: 35752, slot: 5 }, // chest
            { name: 'Earthshatter Girdle', itemId: 22470, displayId: 35747, slot: 6 }, // waist
            { name: 'Earthshatter Legguards', itemId: 22465, displayId: 35754, slot: 7 }, // legs
            { name: 'Earthshatter Boots', itemId: 22468, displayId: 35746, slot: 8 }, // feet
            { name: 'Earthshatter Wristguards', itemId: 22471, displayId: 35753, slot: 9 }, // wrist
            { name: 'Earthshatter Handguards', itemId: 22469, displayId: 35748, slot: 10 }, // hands
        ],
    },
    Warlock: {
        'T0': [
            { name: 'Dreadmist Mask', itemId: 16698, displayId: 31263, slot: 1 }, // head
            { name: 'Dreadmist Mantle', itemId: 16701, displayId: 29798, slot: 3 }, // shoulder
            { name: 'Dreadmist Robe', itemId: 16700, displayId: 29792, slot: 20 }, // robe
            { name: 'Dreadmist Belt', itemId: 16702, displayId: 29793, slot: 6 }, // waist
            { name: 'Dreadmist Leggings', itemId: 16699, displayId: 29797, slot: 7 }, // legs
            { name: 'Dreadmist Sandals', itemId: 16704, displayId: 29799, slot: 8 }, // feet
            { name: 'Dreadmist Bracers', itemId: 16703, displayId: 29795, slot: 9 }, // wrist
            { name: 'Dreadmist Wraps', itemId: 16705, displayId: 29800, slot: 10 }, // hands
        ],
        'T0.5': [
            { name: 'Deathmist Mask', itemId: 22074, displayId: 34624, slot: 1 }, // head
            { name: 'Deathmist Mantle', itemId: 22073, displayId: 34623, slot: 3 }, // shoulder
            { name: 'Deathmist Robe', itemId: 22075, displayId: 34625, slot: 20 }, // robe
            { name: 'Deathmist Belt', itemId: 22070, displayId: 34620, slot: 6 }, // waist
            { name: 'Deathmist Leggings', itemId: 22072, displayId: 34622, slot: 7 }, // legs
            { name: 'Deathmist Sandals', itemId: 22076, displayId: 34626, slot: 8 }, // feet
            { name: 'Deathmist Bracers', itemId: 22071, displayId: 34621, slot: 9 }, // wrist
            { name: 'Deathmist Wraps', itemId: 22077, displayId: 34627, slot: 10 }, // hands
        ],
        'T1': [
            { name: 'Felheart Horns', itemId: 16808, displayId: 31987, slot: 1 }, // head
            { name: 'Felheart Shoulder Pads', itemId: 16807, displayId: 31974, slot: 3 }, // shoulder
            { name: 'Felheart Robes', itemId: 16809, displayId: 31973, slot: 20 }, // robe
            { name: 'Felheart Belt', itemId: 16806, displayId: 31969, slot: 6 }, // waist
            { name: 'Felheart Pants', itemId: 16810, displayId: 31972, slot: 7 }, // legs
            { name: 'Felheart Slippers', itemId: 16803, displayId: 31975, slot: 8 }, // feet
            { name: 'Felheart Bracers', itemId: 16804, displayId: 31970, slot: 9 }, // wrist
            { name: 'Felheart Gloves', itemId: 16805, displayId: 31971, slot: 10 }, // hands
        ],
        'T2': [
            { name: 'Nemesis Skullcap', itemId: 16929, displayId: 34369, slot: 1 }, // head
            { name: 'Nemesis Spaulders', itemId: 16932, displayId: 34022, slot: 3 }, // shoulder
            { name: 'Nemesis Robes', itemId: 16931, displayId: 34014, slot: 20 }, // robe
            { name: 'Nemesis Belt', itemId: 16933, displayId: 34011, slot: 6 }, // waist
            { name: 'Nemesis Leggings', itemId: 16930, displayId: 29857, slot: 7 }, // legs
            { name: 'Nemesis Boots', itemId: 16927, displayId: 34015, slot: 8 }, // feet
            { name: 'Nemesis Bracers', itemId: 16934, displayId: 34012, slot: 9 }, // wrist
            { name: 'Nemesis Gloves', itemId: 16928, displayId: 34013, slot: 10 }, // hands
        ],
        'T3': [
            { name: 'Plagueheart Circlet', itemId: 22506, displayId: 35182, slot: 1 }, // head
            { name: 'Plagueheart Shoulderpads', itemId: 22507, displayId: 35187, slot: 3 }, // shoulder
            { name: 'Plagueheart Robe', itemId: 22504, displayId: 35185, slot: 20 }, // robe
            { name: 'Plagueheart Belt', itemId: 22510, displayId: 35179, slot: 6 }, // waist
            { name: 'Plagueheart Leggings', itemId: 22505, displayId: 35184, slot: 7 }, // legs
            { name: 'Plagueheart Sandals', itemId: 22508, displayId: 35186, slot: 8 }, // feet
            { name: 'Plagueheart Bindings', itemId: 22511, displayId: 35180, slot: 9 }, // wrist
            { name: 'Plagueheart Gloves', itemId: 22509, displayId: 35183, slot: 10 }, // hands
            { name: 'Plagueheart Ring', itemId: 23063, displayId: 35472, slot: 11 }, // finger
        ],
    },
    Warrior: {
        'T0': [
            { name: 'Helm of Valor', itemId: 16731, displayId: 31284, slot: 1 }, // head
            { name: 'Spaulders of Valor', itemId: 16733, displayId: 29964, slot: 3 }, // shoulder
            { name: 'Breastplate of Valor', itemId: 16730, displayId: 29958, slot: 5 }, // chest
            { name: 'Belt of Valor', itemId: 16736, displayId: 29959, slot: 6 }, // waist
            { name: 'Legplates of Valor', itemId: 16732, displayId: 29963, slot: 7 }, // legs
            { name: 'Boots of Valor', itemId: 16734, displayId: 29960, slot: 8 }, // feet
            { name: 'Bracers of Valor', itemId: 16735, displayId: 29961, slot: 9 }, // wrist
            { name: 'Gauntlets of Valor', itemId: 16737, displayId: 29962, slot: 10 }, // hands
        ],
        'T0.5': [
            { name: 'Helm of Heroism', itemId: 21999, displayId: 34614, slot: 1 }, // head
            { name: 'Spaulders of Heroism', itemId: 22001, displayId: 34616, slot: 3 }, // shoulder
            { name: 'Breastplate of Heroism', itemId: 21997, displayId: 34617, slot: 5 }, // chest
            { name: 'Belt of Heroism', itemId: 21994, displayId: 34610, slot: 6 }, // waist
            { name: 'Legplates of Heroism', itemId: 22000, displayId: 34615, slot: 7 }, // legs
            { name: 'Boots of Heroism', itemId: 21995, displayId: 34611, slot: 8 }, // feet
            { name: 'Bracers of Heroism', itemId: 21996, displayId: 34612, slot: 9 }, // wrist
            { name: 'Gauntlets of Heroism', itemId: 21998, displayId: 34613, slot: 10 }, // hands
        ],
        'T1': [
            { name: 'Helm of Might', itemId: 16866, displayId: 31260, slot: 1 }, // head
            { name: 'Pauldrons of Might', itemId: 16868, displayId: 31024, slot: 3 }, // shoulder
            { name: 'Breastplate of Might', itemId: 16865, displayId: 31021, slot: 5 }, // chest
            { name: 'Belt of Might', itemId: 16864, displayId: 31019, slot: 6 }, // waist
            { name: 'Legplates of Might', itemId: 16867, displayId: 31023, slot: 7 }, // legs
            { name: 'Sabatons of Might', itemId: 16862, displayId: 31025, slot: 8 }, // feet
            { name: 'Bracers of Might', itemId: 16861, displayId: 31020, slot: 9 }, // wrist
            { name: 'Gauntlets of Might', itemId: 16863, displayId: 31022, slot: 10 }, // hands
        ],
        'T2': [
            { name: 'Helm of Wrath', itemId: 16963, displayId: 34215, slot: 1 }, // head
            { name: 'Pauldrons of Wrath', itemId: 16961, displayId: 34253, slot: 3 }, // shoulder
            { name: 'Breastplate of Wrath', itemId: 16966, displayId: 33983, slot: 5 }, // chest
            { name: 'Waistband of Wrath', itemId: 16960, displayId: 33990, slot: 6 }, // waist
            { name: 'Legplates of Wrath', itemId: 16962, displayId: 33986, slot: 7 }, // legs
            { name: 'Sabatons of Wrath', itemId: 16965, displayId: 33989, slot: 8 }, // feet
            { name: 'Bracelets of Wrath', itemId: 16959, displayId: 33982, slot: 9 }, // wrist
            { name: 'Gauntlets of Wrath', itemId: 16964, displayId: 33984, slot: 10 }, // hands
        ],
        'T3': [
            { name: 'Dreadnaught Helmet', itemId: 22418, displayId: 35447, slot: 1 }, // head
            { name: 'Dreadnaught Pauldrons', itemId: 22419, displayId: 35177, slot: 3 }, // shoulder
            { name: 'Dreadnaught Breastplate', itemId: 22416, displayId: 35049, slot: 5 }, // chest
            { name: 'Dreadnaught Waistguard', itemId: 22422, displayId: 35058, slot: 6 }, // waist
            { name: 'Dreadnaught Legplates', itemId: 22417, displayId: 35051, slot: 7 }, // legs
            { name: 'Dreadnaught Sabatons', itemId: 22420, displayId: 35067, slot: 8 }, // feet
            { name: 'Dreadnaught Bracers', itemId: 22423, displayId: 35044, slot: 9 }, // wrist
            { name: 'Dreadnaught Gauntlets', itemId: 22421, displayId: 35050, slot: 10 }, // hands
        ],
    },
};

/** All classes that have at least one tier set. */
export const TIER_CLASSES = ['Druid', 'Hunter', 'Mage', 'Paladin', 'Priest', 'Rogue', 'Shaman', 'Warlock', 'Warrior'];

/** All tier IDs we ship, in display order. */
export const TIER_IDS = ['T0', 'T0.5', 'T1', 'T2', 'T3'];
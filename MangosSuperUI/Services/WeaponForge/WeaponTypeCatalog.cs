namespace MangosSuperUI.Services.WeaponForge;

/// <summary>
/// One forgeable weapon family: the item_template gameplay contract (class 2 subclass /
/// inventory type / sheath / material / delay, plus ammo/range for ranged families), the
/// visual-donor search patterns, and the grip presentation hints the import page uses. Everything
/// type-specific the Forge needs hangs off this record so adding a family is one table row, not a
/// code hunt.
///
/// Grip placement itself (how far the weapon extends behind the palm, the target length, which
/// cross-axis is the wide one, and where the mass sits relative to the hand) is NOT stored here —
/// it is measured from the resolved stock donor's vertices by <see cref="WeaponDonorResolver"/>,
/// because the stock model already encodes exactly where Blizzard put the palm. Only
/// <see cref="SecondHandFraction"/> is a hint: the off-hand is placed by the character animation,
/// so its band in the preview is approximate by nature.
/// </summary>
public sealed record WeaponTypeProfile
{
    public required string Key { get; init; }
    public required string Label { get; init; }

    /// <summary>Noun for the default item name, e.g. "Staff" → "Forged Staff 60012".</summary>
    public required string DefaultNoun { get; init; }

    // item_template gameplay contract (class 2 = weapon; 4 = armor for shields).
    public int ItemClass { get; init; } = 2;
    public required int Subclass { get; init; }
    public required int InventoryType { get; init; }   // 13 one-hand, 17 two-hand, 15 ranged, 25 thrown, 26 ranged-right
    public required int Sheath { get; init; }          // 3 hip, 1 back, 2 staff-back, 0 none (ranged slots sheathe by slot)
    public required int Material { get; init; }        // 1 metal, 2 wood
    public required int DelayMs { get; init; }

    public bool TwoHanded { get; init; }

    /// <summary>True for the ranged-slot families (bow/gun/crossbow/thrown/wand). Ranged weapons
    /// are bound to ONE inventory type (15 bows, 26 guns/crossbows/wands, 25 thrown), carry
    /// <see cref="AmmoType"/>/<see cref="RangeMod"/>, and sheathe by slot (sheath 0).</summary>
    public bool IsRanged { get; init; }

    /// <summary>item_template.ammo_type: 2 arrows (bows/crossbows), 3 bullets (guns), 0 none.</summary>
    public int AmmoType { get; init; }

    /// <summary>item_template.range_mod: 100 for every vanilla ranged weapon, 0 for melee.</summary>
    public int RangeMod { get; init; }

    /// <summary>Default item_template.dmg_type1 school when the family is not physical by nature
    /// (wands shoot a spell school). Null leaves the donor's physical (0).</summary>
    public int? DefaultDamageType { get; init; }

    /// <summary>True for the shield family: class-4/subclass-6 armor in the Shield slot (14),
    /// models under Item\ObjectComponents\Shield, no damage/delay, armor + block instead. Shields
    /// are held by the forearm strap (model origin), not a palm.</summary>
    public bool IsShield { get; init; }

    /// <summary>Default item_template armor / block for the shield family (a low-level vanilla
    /// shield); the item modal can override both.</summary>
    public int Armor { get; init; }
    public int Block { get; init; }

    /// <summary>Where this family's models/textures live in the client (and where forged members
    /// are written): weapons and every ranged family in Item\ObjectComponents\Weapon, shields in
    /// Item\ObjectComponents\Shield. The DBC ModelName1 stays a bare file name — the client picks
    /// the directory by item type.</summary>
    public string ComponentDir { get; init; } = WeaponNaming.WeaponDir;

    /// <summary>Whether the arbitrary-GLB import route (PCA orientation, grip heuristics) is
    /// offered for this family. Shields have no long axis or grip to find, so only the TBC import
    /// (already in the client's own frame) is offered for them.</summary>
    public bool GlbImportSupported { get; init; } = true;

    /// <summary>Approximate off-hand grip station for the preview band, as a fraction of the
    /// weapon's X extent ahead of the palm. Null for one-handers. The real off-hand is placed
    /// by the character animation — this is a visual guide, not a serialized value.</summary>
    public float? SecondHandFraction { get; init; }

    /// <summary>Explicit ItemDisplayInfo donor row (the proven golden sword pins 679). The pinned
    /// row is tried first (strict, then relaxed texture contract); when it is missing or fails
    /// structural validation, <see cref="WeaponDonorResolver"/> falls back to scanning by
    /// <see cref="DonorModelPatterns"/>.</summary>
    public uint? PinnedDisplayRow { get; init; }

    /// <summary>Optional SEPARATE stock row whose model supplies the measurements (length, palm-back,
    /// orientation hints) and presentation (icon, sound group, SpellVisual) while the scaffold M2
    /// still comes from the resolved structural donor. Used when the family's representative stock
    /// model is not a valid scaffold (every 2H crossbow is multi-submesh/multi-bone, but the
    /// single-bone hand crossbow is) — the forged weapon lands at the representative size and
    /// placement anyway. Null = measure the scaffold itself.</summary>
    public uint? MeasureDisplayRow { get; init; }

    /// <summary>Case-insensitive ModelName1 prefixes used to find a stock visual donor
    /// (e.g. "stave_2h" matches Stave_2H_Long_A_01.mdx).</summary>
    public string[] DonorModelPatterns { get; init; } = [];

    /// <summary>The item_template inventory types this family may legitimately bind to: two-handers
    /// only 17; one-handers 13/21/22; ranged families exactly their own slot.</summary>
    public IReadOnlyList<int> AllowedInventoryTypes =>
        IsRanged || IsShield ? [InventoryType] : TwoHanded ? [17] : [13, 21, 22];

    /// <summary>Human label for <see cref="AllowedInventoryTypes"/>, for validation messages.</summary>
    public string AllowedInventoryTypesLabel =>
        IsRanged || IsShield ? $"{CustomWeaponBuildService.InventoryTypeLabel(InventoryType)} ({InventoryType})"
        : TwoHanded ? "Two-Hand (17)"
        : "One-Hand, Main Hand, or Off Hand (13, 21, or 22)";

    /// <summary>Column overrides applied to the donor-2131 gameplay clone so a forged axe is an
    /// axe (subclass/inventory/sheath/material/delay) instead of inheriting sword values, a
    /// forged bow is a bow (ranged slot, arrows, ranged range_mod), and a forged shield is armor
    /// (class 4, no damage/delay, armor + block).</summary>
    public Dictionary<string, string> ItemTemplateOverrides()
    {
        var o = new Dictionary<string, string>
        {
            ["class"] = ItemClass.ToString(),
            ["subclass"] = Subclass.ToString(),
            ["inventory_type"] = InventoryType.ToString(),
            ["sheath"] = Sheath.ToString(),
            ["material"] = Material.ToString(),
            ["delay"] = DelayMs.ToString(),
        };
        if (IsRanged)
        {
            o["ammo_type"] = AmmoType.ToString();
            o["range_mod"] = RangeMod.ToString();
        }
        if (IsShield)
        {
            o["dmg_min1"] = "0";
            o["dmg_max1"] = "0";
            o["armor"] = Armor.ToString();
            o["block"] = Block.ToString();
        }
        if (DefaultDamageType is { } school)
            o["dmg_type1"] = school.ToString();
        return o;
    }
}

/// <summary>The forgeable weapon families. Vanilla enums: item class 2 subclasses
/// (0 axe1H, 1 axe2H, 2 bow, 3 gun, 4 mace1H, 5 mace2H, 6 polearm, 7 sword1H, 8 sword2H,
/// 10 staff, 15 dagger, 16 thrown, 18 crossbow, 19 wand); inventory types 13 one-hand /
/// 17 two-hand / 15 ranged (bows) / 26 ranged-right (guns, crossbows, wands) / 25 thrown;
/// item_template sheath (1 back, 2 staff-back, 3 hip, 0 none — ranged slots sheathe by slot).
/// Ranged delays are the TBC-catalog medians per subclass (bows 2400, guns 2600, crossbows 2700,
/// thrown 1800, wands 1600); vanilla ranged weapons all carry range_mod 100.</summary>
public static class WeaponTypeCatalog
{
    public const string DefaultKey = "sword1h";

    public static readonly IReadOnlyList<WeaponTypeProfile> All =
    [
        new()
        {
            Key = "sword1h", Label = "1H Sword", DefaultNoun = "Sword",
            Subclass = 7, InventoryType = 13, Sheath = 3, Material = 1, DelayMs = 2600,
            PinnedDisplayRow = 679, // the proven golden donor (Sword_1H_Short_A_01)
        },
        new()
        {
            Key = "dagger", Label = "Dagger", DefaultNoun = "Dagger",
            Subclass = 15, InventoryType = 13, Sheath = 3, Material = 1, DelayMs = 1800,
            DonorModelPatterns = ["knife_1h", "dagger"],
        },
        new()
        {
            Key = "axe1h", Label = "1H Axe", DefaultNoun = "Axe",
            Subclass = 0, InventoryType = 13, Sheath = 3, Material = 1, DelayMs = 2700,
            DonorModelPatterns = ["axe_1h"],
        },
        new()
        {
            Key = "mace1h", Label = "1H Mace", DefaultNoun = "Mace",
            Subclass = 4, InventoryType = 13, Sheath = 3, Material = 1, DelayMs = 2700,
            DonorModelPatterns = ["mace_1h", "hammer_1h"],
        },
        new()
        {
            Key = "sword2h", Label = "2H Sword", DefaultNoun = "Greatsword",
            Subclass = 8, InventoryType = 17, Sheath = 1, Material = 1, DelayMs = 3300,
            TwoHanded = true, SecondHandFraction = 0.12f,
            DonorModelPatterns = ["sword_2h"],
        },
        new()
        {
            Key = "axe2h", Label = "2H Axe", DefaultNoun = "Battleaxe",
            Subclass = 1, InventoryType = 17, Sheath = 1, Material = 1, DelayMs = 3400,
            TwoHanded = true, SecondHandFraction = 0.18f,
            DonorModelPatterns = ["axe_2h"],
        },
        new()
        {
            Key = "mace2h", Label = "2H Mace", DefaultNoun = "Maul",
            Subclass = 5, InventoryType = 17, Sheath = 1, Material = 1, DelayMs = 3500,
            TwoHanded = true, SecondHandFraction = 0.18f,
            DonorModelPatterns = ["mace_2h", "hammer_2h"],
        },
        new()
        {
            Key = "staff", Label = "Staff", DefaultNoun = "Staff",
            Subclass = 10, InventoryType = 17, Sheath = 2, Material = 2, DelayMs = 3200,
            TwoHanded = true, SecondHandFraction = 0.30f,
            // Vanilla names staves Stave_2H_* (Stave_2H_Long_A_01 ...); "staff_" is kept for
            // clients whose patches add Staff_* rows.
            DonorModelPatterns = ["stave_2h", "staff_"],
        },
        new()
        {
            Key = "polearm", Label = "Polearm", DefaultNoun = "Polearm",
            Subclass = 6, InventoryType = 17, Sheath = 1, Material = 1, DelayMs = 3300,
            TwoHanded = true, SecondHandFraction = 0.18f,
            // Vanilla names every polearm Polearm_2H_* (Polearm_2H_Bladed_A_01 ...); the
            // spear/halberd/pike stems only appear in later clients.
            DonorModelPatterns = ["polearm_2h", "spear_2h", "halberd_2h", "pike_", "spear_"],
        },

        // ── Ranged families (2026-08-21). Stock facts measured from the 1.12 client:
        //   • every bow/gun/crossbow/thrown/wand M2 lives in ITEM\ObjectComponents\WEAPON like the
        //     melee families, with the long axis on +X and the origin in the hand;
        //   • the forged mesh is rigid on bone 0 — a static root in every bow/gun/crossbow/wand
        //     donor (limb/hammer bones animate separately and are preserved in the scaffold), and
        //     the THROW spin bone in every thrown donor (so a forged throwing axe still spins);
        //   • ItemDisplayInfo.SpellVisualID is non-zero on stock ranged rows (bows 5, firearms 224,
        //     thrown 98) and is carried from the donor row so projectiles keep their visual;
        //   • stock thrown rows set ModelName2 = ModelName1; the Forge mirrors that.
        new()
        {
            Key = "bow", Label = "Bow", DefaultNoun = "Bow",
            Subclass = 2, InventoryType = 15, Sheath = 0, Material = 2, DelayMs = 2400,
            IsRanged = true, AmmoType = 2, RangeMod = 100,
            // Bow_1H_Standard_A_01: single submesh/batch, DBC-driven texture, static root bone,
            // 3 attachments, SpellVisual 5. Grip at the centre of the limbs (palm-back 0.5).
            PinnedDisplayRow = 2786,
            DonorModelPatterns = ["bow_1h_standard", "bow_1h_short", "bow_1h_horde", "bow_1h"],
        },
        new()
        {
            Key = "gun", Label = "Gun", DefaultNoun = "Rifle",
            Subclass = 3, InventoryType = 26, Sheath = 0, Material = 1, DelayMs = 2600,
            IsRanged = true, AmmoType = 3, RangeMod = 100, SecondHandFraction = 0.45f,
            // Firearm_2H_Rifle_A_01: the only vanilla firearm whose first texture slot is Type 2
            // (DBC-named); every other Firearm_2H_* hardcodes its texture. SpellVisual 224.
            PinnedDisplayRow = 1136,
            DonorModelPatterns = ["firearm_2h"],
        },
        new()
        {
            Key = "crossbow", Label = "Crossbow", DefaultNoun = "Crossbow",
            Subclass = 18, InventoryType = 26, Sheath = 0, Material = 2, DelayMs = 2700,
            IsRanged = true, AmmoType = 2, RangeMod = 100, SecondHandFraction = 0.35f,
            // Scaffold: Bow_1H_Crossbow_A_01 (one static bone, one submesh/batch — the only clean
            // crossbow scaffold). Measure: Bow_2H_Crossbow_A_01 — the representative two-hand
            // crossbow (length 1.22, prod across WoW Y, stock below the rail), so forged crossbows
            // land at real crossbow size and placement instead of hand-crossbow size.
            PinnedDisplayRow = 5288, MeasureDisplayRow = 2462,
            DonorModelPatterns = ["bow_1h_crossbow", "bow_2h_crossbow"],
        },
        new()
        {
            Key = "thrown", Label = "Thrown", DefaultNoun = "Throwing Axe",
            Subclass = 16, InventoryType = 25, Sheath = 0, Material = 1, DelayMs = 1800,
            IsRanged = true, AmmoType = 0, RangeMod = 100,
            // Scaffold Thrown_1H_Axe_A_01: single submesh/batch, all vertices on the (spinning)
            // bone 0, SpellVisual 98, ModelName2 mirrored. Pattern order matters only as a fallback
            // — the dynamite/molotov rows are structurally cleaner but carry no throw spin or
            // visual. Measure Thrown_1H_Axe_B_01: the same family values, but a handle-dominant
            // silhouette (X 0.65 vs blade span 0.30) so imports orient along the handle; the A_01
            // rest pose is wider across the blade than it is long.
            PinnedDisplayRow = 3155, MeasureDisplayRow = 3285,
            DonorModelPatterns = ["thrown_1h_axe", "thrown_1h_dagger", "thrown_1h"],
        },
        new()
        {
            Key = "wand", Label = "Wand", DefaultNoun = "Wand",
            Subclass = 19, InventoryType = 26, Sheath = 0, Material = 2, DelayMs = 1600,
            IsRanged = true, AmmoType = 0, RangeMod = 100,
            DefaultDamageType = 6, // arcane — a wand's Shoot deals its dmg_type school, never physical
            // Lesser Magic Wand: same simple Wand_1H_Standard_A_02 scaffold as row 5720,
            // but its display carries the real arcane Shoot visual (2799). Row 5720 has none.
            PinnedDisplayRow = 21096,
            DonorModelPatterns = ["wand_1h_standard", "wand_1h"],
        },

        // ── Shields (2026-08-21). Class-4/subclass-6 armor in slot 14, sheath 4, models under
        // Item\ObjectComponents\Shield. Every vanilla Shield_*/Buckler_* M2 is a clean scaffold
        // (one static bone, one submesh/batch, DBC-driven texture, 20–150 vertices); the origin is
        // the forearm strap at the centre of the face (palm-back ≈ 0.5 on the face's X span).
        // TBC import only — shields have no long axis or grip for the GLB heuristics to find.
        new()
        {
            Key = "shield", Label = "Shield", DefaultNoun = "Shield",
            ItemClass = 4, Subclass = 6, InventoryType = 14, Sheath = 4, Material = 1, DelayMs = 0,
            IsShield = true, Armor = 75, Block = 2,
            ComponentDir = WeaponNaming.ShieldDir, GlbImportSupported = false,
            PinnedDisplayRow = 1684, // Shield_Round_A_01
            DonorModelPatterns = ["shield_round", "shield_", "buckler_"],
        },
    ];

    /// <summary>Lookup by key, or null when the key names no family (including null/empty).
    /// Callers that would otherwise forge the WRONG weapon use this: <see cref="Get"/>'s fallback
    /// silently turns an unmapped subclass — fist weapon, spear, fishing pole, all of which
    /// <see cref="LegacyItemCatalog.TypeKeyForSubclass"/> deliberately returns null for — into a
    /// 1H sword, and the operator only finds out when the item is in-game.</summary>
    public static WeaponTypeProfile? Find(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return null;
        foreach (var p in All)
            if (string.Equals(p.Key, key.Trim(), StringComparison.OrdinalIgnoreCase))
                return p;
        return null;
    }

    /// <summary>Lookup by key; unknown/empty falls back to the proven 1H sword. Fine where a
    /// default is genuinely wanted (preview scaffolding, the GLB route's "no preference" case) —
    /// use <see cref="Find"/> anywhere the family decides what actually gets written.</summary>
    public static WeaponTypeProfile Get(string? key) => Find(key) ?? All[0];
}

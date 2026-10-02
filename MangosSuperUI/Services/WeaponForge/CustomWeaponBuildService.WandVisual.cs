using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MangosSuperUI.Models;

namespace MangosSuperUI.Services.WeaponForge;

public sealed partial class CustomWeaponBuildService
{
    /// <summary>Repair a previously authored arcane wand's missing Shoot display metadata in place.
    /// The compiled assets, item_template, build identity and all other display fields stay intact.
    /// A dry run is read-only. Applying queues the normal unified patch; it never deploys it.</summary>
    public async Task<object> RepairWandVisualAsync(long displayId, long itemEntry, bool apply)
    {
        if (displayId < WeaponIdReservationService.ItemDisplayFloor || itemEntry <= 0)
            throw new InvalidOperationException("A registered custom display and item entry are required.");
        await using var conn = _db.Admin();
        await conn.OpenAsync();
        var stored = await conn.QuerySingleOrDefaultAsync<WandVisualRepairRow>(@"
            SELECT d.dbc_fields_json AS DbcFieldsJson, ma.gameplay_json AS GameplayJson,
                   ma.build_id AS BuildId, mo.source_kind AS SourceKind,
                   mo.m2_sha256 AS ModelSha256, d.blp_sha256 AS TextureSha256
            FROM custom_weapon_display d
            JOIN custom_weapon_model mo ON mo.model_id = d.model_id
            JOIN custom_weapon_item_manifest ma ON ma.display_id = d.display_id
            WHERE d.display_id = @displayId AND ma.item_entry = @itemEntry",
            new { displayId, itemEntry });
        if (stored is null || stored.SourceKind != "glb_import" ||
            ReadGameplayJsonField(stored.GameplayJson, "weaponType") != "wand")
            throw new InvalidOperationException("The IDs must identify the same registered GLB-authored wand.");

        var world = await ReadItemRowAsync(itemEntry, propagate: true)
            ?? throw new InvalidOperationException("The registered wand has no live item_template row.");
        int Value(string key) => world.TryGetValue(key, out var value) ? Convert.ToInt32(value) : -1;
        if (Value("display_id") != displayId || Value("class") != 2 || Value("subclass") != 19 ||
            Value("inventory_type") != 26 || Value("dmg_type1") != 6)
            throw new InvalidOperationException("The live item must still be the registered arcane wand (school 6); no gameplay changes were made.");

        var donor = _donors.Resolve(WeaponTypeCatalog.Get("wand"));
        var repair = PlanWandVisualRepair(stored.DbcFieldsJson, donor.SpellVisualId);
        bool changed = false;
        if (apply && repair.Changed)
        {
            int count = await conn.ExecuteAsync(@"
                UPDATE custom_weapon_display SET dbc_fields_json = @after
                WHERE display_id = @displayId AND dbc_fields_json <=> @before",
                new { displayId, before = stored.DbcFieldsJson, after = repair.Json });
            if (count != 1) throw new InvalidOperationException("The wand display changed during repair; inspect it again before retrying.");
            changed = true;
            await _audit.LogAsync(new AuditEntry
            {
                Category = "weaponforge", Action = "repair_wand_visual", TargetType = "item",
                TargetId = checked((int)itemEntry), TargetName = ReadGameplayJsonField(stored.GameplayJson, "name"),
                StateBefore = stored.DbcFieldsJson, StateAfter = repair.Json,
                Success = true, IsReversible = false, RevertKind = RevertKind.None,
                Notes = $"Display {displayId}: SpellVisual 0 -> {donor.SpellVisualId}, from stock arcane wand display {donor.DisplayRow}. " +
                        "Existing item/build/model/texture retained; gameplay untouched. Unified patch rebuild required.",
            });
        }
        // An apply retry also records pending work, so a transient queue failure is recoverable
        // without changing the already-repaired registry row a second time.
        var queued = apply
            ? QueueUnifiedRebuild($"wand visual repair: item {itemEntry}, display {displayId}")
            : (Ok: true, Queued: false, Pending: 0, Message: "Preview only; no changes made.");
        return new
        {
            ok = true, dryRun = !apply, changed, needsRepair = repair.Changed, displayId, itemEntry,
            buildId = stored.BuildId, damageSchool = 6, previousSpellVisualId = repair.Previous,
            spellVisualId = donor.SpellVisualId, donorDisplayId = donor.DisplayRow,
            modelSha256 = stored.ModelSha256, textureSha256 = stored.TextureSha256,
            patchQueued = queued.Queued, patchPending = queued.Pending, message = queued.Message,
            patchDeployed = false,
        };
    }

    internal static (string Json, uint Previous, bool Changed) PlanWandVisualRepair(string? json, uint visualId)
    {
        if (visualId == 0) throw new InvalidOperationException("A verified nonzero stock wand visual is required.");
        var fields = JsonNode.Parse(json ?? "{}") as JsonObject
            ?? throw new InvalidOperationException("Stored display metadata must be a JSON object.");
        uint previous = fields["spellVisualId"]?.GetValue<uint>() ?? 0;
        if (previous != 0 && previous != visualId)
            throw new InvalidOperationException($"Wand already has another visual ({previous}); this repair only fills missing visuals.");
        fields["spellVisualId"] = visualId;
        return (fields.ToJsonString(), previous, previous != visualId);
    }

    private sealed class WandVisualRepairRow
    {
        public string? DbcFieldsJson { get; set; }
        public string? GameplayJson { get; set; }
        public string? BuildId { get; set; }
        public string? SourceKind { get; set; }
        public string? ModelSha256 { get; set; }
        public string? TextureSha256 { get; set; }
    }
}

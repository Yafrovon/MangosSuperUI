using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Dapper;
using MangosSuperUI.Models;

namespace MangosSuperUI.Services.WeaponForge;

public sealed partial class CustomWeaponBuildService
{
    private const string RevisionTargetSql = @"
        SELECT ma.item_entry AS ItemEntry, d.display_id AS DisplayId, mo.model_id AS ModelId,
               ma.build_id AS BuildId, mo.source_kind AS SourceKind, mo.compiled_m2 AS M2,
               mo.m2_sha256 AS ModelSha256, mo.source_sha256 AS SourceSha256,
               mo.generator_params_json AS GeneratorParamsJson, mo.model_mpq_path AS ModelMpqPath,
               d.compiled_blp AS Blp, d.blp_sha256 AS TextureSha256, d.texture_mpq_path AS TextureMpqPath,
               d.dbc_fields_json AS DbcFieldsJson, d.icon_stem AS IconStem, d.item_visual AS ItemVisual,
               d.donor_display_id AS DonorDisplayId, ma.gameplay_json AS GameplayJson,
               (SELECT COUNT(*) FROM custom_weapon_item_manifest x WHERE x.display_id=d.display_id) AS ManifestCount,
               (SELECT COUNT(*) FROM custom_weapon_display x WHERE x.model_id=mo.model_id) AS DisplayCount,
               (SELECT COUNT(*) FROM custom_weapon_model_texture x WHERE x.model_id=mo.model_id) AS EffectTextureCount
        FROM custom_weapon_item_manifest ma
        JOIN custom_weapon_display d ON d.display_id=ma.display_id
        JOIN custom_weapon_model mo ON mo.model_id=d.model_id
        WHERE d.display_id=@displayId AND ma.item_entry=@itemEntry";

    private static async Task<WeaponRevisionRow> LoadRevisionTargetAsync(IDbConnection connection,
        long displayId, long itemEntry, IDbTransaction? transaction = null) =>
        await connection.QuerySingleOrDefaultAsync<WeaponRevisionRow>(
            RevisionTargetSql + (transaction is null ? "" : " FOR UPDATE"),
            new { displayId, itemEntry }, transaction)
        ?? throw new InvalidOperationException("No registered weapon matches both item and display IDs.");

    public async Task<object> ReadGeometryRevisionTargetAsync(long displayId, long itemEntry)
    {
        await using var connection = _db.Admin();
        var row = await LoadRevisionTargetAsync(connection, displayId, itemEntry);
        WeaponGeometryRevision.ValidateTarget(row, displayId, itemEntry, row.ModelSha256);
        await RequireRevisionItemBindingAsync(row);
        return new { ok = true, itemEntry, displayId, row.BuildId, row.ModelSha256, row.TextureSha256,
            name = ReadGameplayJsonField(row.GameplayJson, "name"),
            weaponType = ReadGameplayJsonField(row.GameplayJson, "weaponType"),
            row.ModelMpqPath, row.TextureMpqPath, runtimeVerified = false };
    }

    public async Task<byte[]> ReadCurrentGeometryRevisionModelAsync(long displayId, long itemEntry, string expectedModelSha256)
    {
        await using var connection = _db.Admin();
        var row = await LoadRevisionTargetAsync(connection, displayId, itemEntry);
        WeaponGeometryRevision.ValidateTarget(row, displayId, itemEntry, expectedModelSha256);
        return row.M2;
    }

    public byte[] ReadGeometryRevisionReceipt(string revisionToken)
    {
        WeaponGeometryRevision.RequireHash(revisionToken, nameof(revisionToken));
        return File.ReadAllBytes(Path.Combine(_env.ContentRootPath, "App_Data", "weapon-revisions", revisionToken.ToLowerInvariant() + ".json"));
    }

    public async Task<object> ReviseGeometryAsync(long displayId, long itemEntry, string expectedModelSha256,
        RigidWeaponMesh mesh, byte[] sourceGlb, bool apply = false,
        string? expectedRevisionModelSha256 = null, string? expectedRevisionToken = null)
    {
        await using var connection = _db.Admin();
        await connection.OpenAsync();
        var row = await LoadRevisionTargetAsync(connection, displayId, itemEntry);
        WeaponGeometryRevision.ValidateTarget(row, displayId, itemEntry, expectedModelSha256);
        await RequireRevisionItemBindingAsync(row);
        var plan = WeaponGeometryRevision.Compile(row, mesh, sourceGlb);
        string revisionToken = GeometryRevisionToken(row, plan);
        var preview = _preview.RenderFromBytes(plan.M2, row.Blp, preserveSourceGraph: true);
        if (!preview.Ok) throw new InvalidOperationException("Compiled revision preview failed: " + preview.Error);

        long auditId = 0;
        if (apply)
        {
            WeaponGeometryRevision.ValidateApplyHash(plan, expectedRevisionModelSha256);
            WeaponGeometryRevision.RequireHash(expectedRevisionToken, "expectedRevisionToken");
            if (!string.Equals(revisionToken, expectedRevisionToken, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("The source or preserved weapon state differs from the reviewed dry run. Preview again.");
            await using var transaction = await connection.BeginTransactionAsync();
            var current = await LoadRevisionTargetAsync(connection, displayId, itemEntry, transaction);
            WeaponGeometryRevision.RequireUnchanged(row, current);
            await RequireRevisionItemBindingAsync(current);
            int updated = await connection.ExecuteAsync(@"
                UPDATE custom_weapon_model
                SET compiled_m2=@m2, m2_sha256=@modelSha, source_blob=@sourceGlb,
                    source_sha256=@sourceSha, writer_version='geometry-revision-v1', validation_state='built'
                WHERE model_id=@modelId AND source_kind='glb_import' AND m2_sha256=@beforeSha",
                new { m2 = plan.M2, modelSha = plan.ModelSha256, sourceGlb,
                    sourceSha = plan.SourceSha256, modelId = row.ModelId, beforeSha = row.ModelSha256 }, transaction);
            if (updated != 1) throw new InvalidOperationException("The registry model changed during revision. No revision was committed.");
            await transaction.CommitAsync();
            _memberCache.TryRemove(row.ModelMpqPath, out _);
            try { auditId = await _audit.LogAsync(new AuditEntry {
                Category = "weaponforge", Action = "revise_weapon_geometry", TargetType = "item",
                TargetId = checked((int)itemEntry), TargetName = ReadGameplayJsonField(row.GameplayJson, "name"),
                StateBefore = JsonSerializer.Serialize(new { row.ItemEntry, row.DisplayId, row.ModelId, row.BuildId,
                    row.ModelSha256, row.SourceSha256, row.TextureSha256, row.DbcFieldsJson, row.GameplayJson }),
                StateAfter = JsonSerializer.Serialize(new { plan.ItemEntry, plan.DisplayId, plan.ModelId, plan.BuildId,
                    plan.ModelSha256, plan.SourceSha256, plan.TextureSha256, revisionToken, plan.QualityAudit }),
                Success = true, IsReversible = false, RevertKind = RevertKind.None,
                Notes = "ID-preserving geometry revision. Existing M2 scaffold and registered texture retained; no item_template or display metadata mutation. Unified patch rebuild required; runtime/visual acceptance pending."
            }); }
            catch (Exception ex) { _logger.LogError(ex, "Geometry revision committed but audit log failed for display {DisplayId}", displayId); }
        }
        var queued = apply ? QueueUnifiedRebuild($"weapon geometry revision: item {itemEntry}, display {displayId}")
            : (Ok: true, Queued: false, Pending: 0, Message: "Dry run only; no registry or gameplay changes made.");
        var response = JsonSerializer.SerializeToNode(new { ok = true, dryRun = !apply, changed = apply,
            plan.ItemEntry, plan.DisplayId, plan.ModelId, plan.BuildId,
            plan.PreviousModelSha256, plan.ModelSha256, plan.SourceSha256, plan.TextureSha256, revisionToken,
            name = ReadGameplayJsonField(row.GameplayJson, "name"),
            weaponType = ReadGameplayJsonField(row.GameplayJson, "weaponType"),
            plan.VertexCount, plan.TriangleCount, plan.QualityAudit, preview,
            preservedScaffoldVerified = true, texturePreserved = true, itemMetadataPreserved = true,
            patchQueued = queued.Queued, patchPending = queued.Pending, message = queued.Message,
            patchDeployed = false, runtimeVerified = false, auditId, auditRecorded = auditId > 0,
            auditWarning = apply && auditId == 0 ? "Registry revision committed, but audit logging failed. The response and revision receipt describe the applied state; do not blindly repeat apply." : null,
            currentModelUrl = apply ? $"/WeaponForge/RevisionModel?displayId={displayId}&itemEntry={itemEntry}&expectedModelSha256={plan.ModelSha256}" : null,
            originalBuildArtifactsAreHistorical = true,
            limitation = "Geometry-only revision; GLB textures are ignored. Existing attachment anchors are preserved. Four equivalent M2 views are generated. Review grip, sheath, body contact and appearance in the native client after rebuilding the patch." },
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!.AsObject();
        if (apply)
        {
            try
            {
                string directory = Path.Combine(_env.ContentRootPath, "App_Data", "weapon-revisions");
                Directory.CreateDirectory(directory);
                response["receiptSaved"] = true;
                response["receiptUrl"] = "/WeaponForge/RevisionReceipt?revisionToken=" + revisionToken;
                File.WriteAllText(Path.Combine(directory, revisionToken + ".json"), response.ToJsonString());
            }
            catch (Exception ex)
            {
                response["receiptSaved"] = false; response["receiptUrl"] = null;
                response["receiptWarning"] = "Registry revision committed, but its receipt could not be saved. Download this response. " + ex.Message;
                _logger.LogError(ex, "Geometry revision committed but receipt save failed for display {DisplayId}", displayId);
            }
        }
        return response;
    }

    private async Task RequireRevisionItemBindingAsync(WeaponRevisionRow row)
    {
        var item = await ReadItemRowAsync(row.ItemEntry, propagate: true)
            ?? throw new InvalidOperationException("The registered weapon has no live item_template row.");
        var family = WeaponTypeCatalog.Get(ReadGameplayJsonField(row.GameplayJson, "weaponType"));
        if (!item.TryGetValue("display_id", out var display) || Convert.ToInt64(display) != row.DisplayId ||
            !item.TryGetValue("class", out var itemClass) || Convert.ToInt32(itemClass) != 2 ||
            !item.TryGetValue("subclass", out var subclass) || Convert.ToInt32(subclass) != family.Subclass)
            throw new InvalidOperationException("The live item/display/family binding changed; gameplay was not modified.");
    }

    internal static string GeometryRevisionToken(WeaponRevisionRow row, WeaponRevisionPlan plan) =>
        WeaponGeometryRevision.Hash(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new {
            row.ItemEntry, row.DisplayId, row.ModelId, row.BuildId, row.ModelSha256, row.TextureSha256,
            row.SourceSha256, row.GeneratorParamsJson, row.GameplayJson, row.DbcFieldsJson,
            row.ModelMpqPath, row.TextureMpqPath, row.IconStem, row.ItemVisual, row.DonorDisplayId,
            revisedModelSha256 = plan.ModelSha256, sourceGlbSha256 = plan.SourceSha256
        })));
}

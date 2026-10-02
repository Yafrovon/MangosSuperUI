using System.Data;
using System.Data.Common;
using System.Text.Json;
using Dapper;
using MangosSuperUI.Models;

namespace MangosSuperUI.Services.WeaponForge;

public sealed partial class CustomWeaponBuildService
{
    private static async Task<List<WeaponArtAuxiliary>> LoadArtAuxiliaryAsync(IDbConnection connection, long modelId, IDbTransaction? tx = null) =>
        (await connection.QueryAsync<WeaponArtAuxiliary>("SELECT slot AS Slot, mpq_path AS Path, compiled_blp AS Bytes, blp_sha256 AS Sha256 FROM custom_weapon_model_texture WHERE model_id=@modelId ORDER BY slot" +
            (tx is null ? "" : " FOR UPDATE"), new { modelId }, tx)).ToList();

    public async Task<object> ReadArtRevisionTargetAsync(long displayId, long itemEntry)
    {
        await using var connection = _db.Admin();
        var row = await LoadRevisionTargetAsync(connection, displayId, itemEntry);
        var auxiliary = await LoadArtAuxiliaryAsync(connection, row.ModelId);
        WeaponArtRevision.ValidateTarget(row, auxiliary, displayId, itemEntry, row.ModelSha256, row.TextureSha256);
        await RequireRevisionItemBindingAsync(row);
        return new { ok = true, row.ItemEntry, row.DisplayId, row.ModelId, row.BuildId, row.ModelSha256, row.TextureSha256,
            row.SourceSha256, row.ModelMpqPath, row.TextureMpqPath, row.IconStem, row.DbcFieldsJson,
            auxiliary = auxiliary.Select(a => new { a.Slot, a.Path, a.Sha256 }),
            name = ReadGameplayJsonField(row.GameplayJson, "name"), weaponType = ReadGameplayJsonField(row.GameplayJson, "weaponType"),
            runtimeVerified = false };
    }

    internal static async Task RunArtTransactionAsync(DbConnection connection, Func<DbTransaction, Task> writes)
    {
        await using var transaction = await connection.BeginTransactionAsync();
        try
        {
            await writes(transaction);
            await transaction.CommitAsync();
        }
        catch
        {
            await transaction.RollbackAsync();
            throw;
        }
    }

    internal async Task<object> ReviseArtAsync(long displayId, long itemEntry, string expectedModelSha256,
        string expectedTextureSha256, RigidWeaponMesh mesh, byte[] sourceGlb, byte[] texturePng, byte[] iconPng,
        BlpWriterService writer, bool apply = false, string? expectedRevisionToken = null,
        string? expectedRevisionModelSha256 = null, string? expectedRevisionTextureSha256 = null, string? expectedRevisionIconSha256 = null)
    {
        await using var connection = _db.Admin();
        await connection.OpenAsync();
        var row = await LoadRevisionTargetAsync(connection, displayId, itemEntry);
        var auxiliary = await LoadArtAuxiliaryAsync(connection, row.ModelId);
        WeaponArtRevision.ValidateTarget(row, auxiliary, displayId, itemEntry, expectedModelSha256, expectedTextureSha256);
        await RequireRevisionItemBindingAsync(row);
        var plan = WeaponArtRevision.Compile(row, mesh, sourceGlb, texturePng, iconPng, writer);
        string token = WeaponArtRevision.Token(row, auxiliary, plan);
        var preview = _preview.RenderFromBytes(plan.Geometry.M2, plan.Blp, preserveSourceGraph: true);
        if (!preview.Ok) throw new InvalidOperationException("Compiled art preview failed: " + preview.Error);
        if (apply) WeaponArtRevision.ValidateApply(plan, token, expectedRevisionToken,
            expectedRevisionModelSha256, expectedRevisionTextureSha256, expectedRevisionIconSha256);

        // Keep reviewed bytes and the old art available before any transaction can commit.
        string directory = Path.Combine(_env.ContentRootPath, "App_Data", "weapon-art-revisions", token);
        Directory.CreateDirectory(directory);
        foreach (var file in new[] { ("model.m2", plan.Geometry.M2), ("diffuse.blp", plan.Blp), ("icon.blp", plan.IconBlp),
                     ("source.glb", sourceGlb), ("skin.png", texturePng), ("icon.png", iconPng), ("before-model.m2", row.M2), ("before-diffuse.blp", row.Blp) })
            File.WriteAllBytes(Path.Combine(directory, file.Item1), file.Item2);
        if (auxiliary.Count == 1) File.WriteAllBytes(Path.Combine(directory, "before-icon.blp"), auxiliary[0].Bytes);

        long auditId = 0;
        if (apply)
        {
            await RunArtTransactionAsync(connection, async tx => {
                var current = await LoadRevisionTargetAsync(connection, displayId, itemEntry, tx);
                var currentAuxiliary = await LoadArtAuxiliaryAsync(connection, row.ModelId, tx);
                WeaponArtRevision.ValidateTarget(current, currentAuxiliary, displayId, itemEntry, expectedModelSha256, expectedTextureSha256);
                WeaponGeometryRevision.RequireUnchanged(row, current, currentAuxiliary.Count);
                if (WeaponArtRevision.Token(current, currentAuxiliary, plan) != token)
                    throw new InvalidOperationException("The registered art changed during preview/apply. Nothing was committed.");
                await RequireRevisionItemBindingAsync(current);
                int otherOwner = await connection.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM custom_weapon_model_texture WHERE mpq_path=@path AND model_id<>@modelId",
                    new { path = plan.IconMpqPath, modelId = row.ModelId }, tx);
                if (otherOwner != 0) throw new InvalidOperationException("The new icon member is owned by another weapon.");
                int models = await connection.ExecuteAsync(@"UPDATE custom_weapon_model SET compiled_m2=@m2, m2_sha256=@sha,
                    source_blob=@sourceGlb, source_sha256=@sourceSha, writer_version='art-revision-v1', validation_state='built'
                    WHERE model_id=@modelId AND source_kind='glb_import' AND m2_sha256=@before",
                    new { m2=plan.Geometry.M2, sha=plan.Geometry.ModelSha256, sourceGlb, sourceSha=plan.Geometry.SourceSha256,
                        modelId=row.ModelId, before=row.ModelSha256 }, tx);
                if (models != 1) throw new InvalidOperationException("The registered model changed during revision.");
                int displays = await connection.ExecuteAsync(@"UPDATE custom_weapon_display SET compiled_blp=@blp, blp_sha256=@sha,
                    source_texture=@texturePng, icon_stem=@icon, validation_state='built'
                    WHERE display_id=@displayId AND model_id=@modelId AND blp_sha256=@before",
                    new { blp=plan.Blp, sha=plan.TextureSha256, texturePng, icon=plan.IconStem, displayId, modelId=row.ModelId, before=row.TextureSha256 }, tx);
                if (displays != 1) throw new InvalidOperationException("The registered display changed during revision.");
                // Validation above allows only this model's one canonical icon, never effect members.
                await connection.ExecuteAsync("DELETE FROM custom_weapon_model_texture WHERE model_id=@modelId", new { modelId=row.ModelId }, tx);
                int icons = await connection.ExecuteAsync(@"INSERT INTO custom_weapon_model_texture (model_id,slot,mpq_path,compiled_blp,blp_sha256)
                    VALUES (@modelId,1,@path,@blp,@sha)", new { modelId=row.ModelId, path=plan.IconMpqPath, blp=plan.IconBlp, sha=plan.IconSha256 }, tx);
                if (icons != 1) throw new InvalidOperationException("The new icon could not be stored.");
            });
            _memberCache.TryRemove(row.ModelMpqPath, out _);
            _memberCache.TryRemove(row.TextureMpqPath, out _);
            _memberCache.TryRemove(plan.IconMpqPath, out _);
            _dbc.RegisterCustomDisplayIcon((uint)displayId, plan.IconStem);
            try { auditId = await _audit.LogAsync(new AuditEntry {
                Category="weaponforge", Action="revise_weapon_art", TargetType="item", TargetId=checked((int)itemEntry),
                TargetName=ReadGameplayJsonField(row.GameplayJson,"name"),
                StateBefore=JsonSerializer.Serialize(new { row.ItemEntry,row.DisplayId,row.ModelId,row.BuildId,row.ModelSha256,row.TextureSha256,row.IconStem,row.DbcFieldsJson,row.GameplayJson }),
                StateAfter=JsonSerializer.Serialize(new { plan.Geometry.ModelSha256,plan.TextureSha256,plan.IconSha256,plan.IconStem,token }),
                Success=true,IsReversible=false,RevertKind=RevertKind.None,
                Notes="ID-preserving complete authored art revision; exact model, texture and icon replaced atomically. Gameplay/display scalars retained. Old/new asset bytes retained in the revision receipt directory. Patch rebuild and runtime review remain separate."
            }); } catch (Exception ex) { _logger.LogError(ex,"Art revision committed but audit failed for display {DisplayId}",displayId); }
        }
        var queued = apply ? QueueUnifiedRebuild($"weapon art revision: item {itemEntry}, display {displayId}")
            : (Ok:true,Queued:false,Pending:0,Message:"Dry run only; no registry or gameplay changes.");
        var result = new { ok=true,dryRun=!apply,changed=apply,itemEntry,displayId,row.ModelId,row.BuildId,
            name=ReadGameplayJsonField(row.GameplayJson,"name"),
            previousModelSha256=row.ModelSha256,previousTextureSha256=row.TextureSha256,
            modelSha256=plan.Geometry.ModelSha256,textureSha256=plan.TextureSha256,iconSha256=plan.IconSha256,
            sourceGlbSha256=plan.Geometry.SourceSha256,plan.TextureSourceSha256,plan.IconSourceSha256,plan.IconStem,plan.IconMpqPath,
            modelMpqPath=row.ModelMpqPath,textureMpqPath=row.TextureMpqPath,revisionToken=token,
            plan.Geometry.VertexCount,plan.Geometry.TriangleCount,plan.Geometry.QualityAudit,preview,
            itemMetadataPreserved=true,displayScalarsPreserved=true,textureReplaced=true,iconReplaced=true,
            patchQueued=queued.Queued,patchPending=queued.Pending,message=queued.Message,patchDeployed=false,runtimeVerified=false,
            auditId,auditRecorded=auditId>0,auditWarning=apply&&auditId==0?"Revision committed but audit logging failed. Retain this receipt; do not blindly repeat apply.":null,
            modelUrl=$"/WeaponForge/ArtRevisionMember?revisionToken={token}&member=model.m2",
            textureUrl=$"/WeaponForge/ArtRevisionMember?revisionToken={token}&member=diffuse.blp",
            iconUrl=$"/WeaponForge/ArtRevisionMember?revisionToken={token}&member=icon.blp" };
        try { File.WriteAllText(Path.Combine(directory, apply?"receipt.json":"preview.json"), JsonSerializer.Serialize(result,new JsonSerializerOptions(JsonSerializerDefaults.Web))); }
        catch(Exception ex) when(apply) { _logger.LogError(ex,"Art revision committed but receipt write failed for {DisplayId}",displayId); }
        return result;
    }

    public byte[] ReadArtRevisionMember(string revisionToken, string member)
    {
        WeaponGeometryRevision.RequireHash(revisionToken,nameof(revisionToken));
        if (!new[] { "model.m2","diffuse.blp","icon.blp","source.glb","skin.png","icon.png","before-model.m2","before-diffuse.blp","before-icon.blp","preview.json","receipt.json" }.Contains(member))
            throw new InvalidOperationException("Unknown art revision member.");
        return File.ReadAllBytes(Path.Combine(_env.ContentRootPath,"App_Data","weapon-art-revisions",revisionToken.ToLowerInvariant(),member));
    }
}

using System.Text.Json;
using Dapper;
using MangosSuperUI.Models;
using MangosSuperUI.Services.WeaponForge;

namespace MangosSuperUI.Services.ArmorForge;

public sealed partial class CustomArmorBuildService
{
    private readonly SemaphoreSlim _authoredBuildGate = new(1, 1);

    /// <summary>Read-only evidence for one saved original package, using the actual reserved display IDs.
    /// Stage reports use placeholder IDs and therefore cannot predict persisted M2 internal-name hashes.</summary>
    public async Task<AuthoredArmorBuiltEvidence?> LoadAuthoredBuiltEvidenceAsync(string packageSha256)
    {
        if (packageSha256.Length != 64 || packageSha256.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
            throw new ArgumentException("Invalid package ID.");
        await using var conn = _db.Admin(); await conn.OpenAsync();
        const string sourceWhere = "JSON_UNQUOTE(JSON_EXTRACT(gameplay_json, '$.authoredPackageSha256'))=@sha";
        var pieces = (await conn.QueryAsync<AuthoredArmorBuiltPiece>(
            "SELECT display_id AS DisplayId, item_entry AS ItemEntry, set_id AS SetId, armor_type_key AS `Key`, name AS Name, inventory_type AS InventoryType FROM custom_armor_display WHERE " + sourceWhere + " ORDER BY display_id LIMIT 9",
            new { sha = packageSha256 })).ToList();
        if (pieces.Count == 0) return null;
        if (pieces.Count != 8 || pieces.Select(p => p.Key).Distinct().Count() != 8 || pieces.Select(p => p.SetId).Distinct().Count() != 1)
            throw new InvalidDataException("Saved package does not identify exactly eight distinct pieces in one set.");
        string? json = await conn.QuerySingleOrDefaultAsync<string>(
            "SELECT JSON_EXTRACT(gameplay_json, '$.report') FROM custom_armor_display WHERE " + sourceWhere + " ORDER BY display_id LIMIT 1",
            new { sha = packageSha256 });
        if (json is null || json.Length > 2 * 1024 * 1024) throw new InvalidDataException("Saved compilation report is missing or oversized.");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("PackageSha256", out var sourceHash) || sourceHash.ValueKind != JsonValueKind.String || sourceHash.GetString() != packageSha256
            || !root.TryGetProperty("CompiledSha256", out var compiled) || compiled.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Saved compilation report does not match the package.");
        var expected = compiled.EnumerateObject().ToArray();
        if (expected.Length is < 1 or > 128) throw new InvalidDataException("Saved compilation member count exceeds the evidence limit.");
        var members = new List<AuthoredArmorBuiltMember>(); long totalBytes = 0;
        foreach (var member in expected)
        {
            if (member.Value.ValueKind != JsonValueKind.String || member.Value.GetString() is not { Length: 64 } hash
                || hash.Any(c => c is not (>= '0' and <= '9' or >= 'a' and <= 'f')))
                throw new InvalidDataException("Saved compilation report contains an invalid member hash.");
            byte[]? bytes = TryGetMember(member.Name);
            totalBytes += bytes?.Length ?? 0;
            if (totalBytes > 64L * 1024 * 1024) throw new InvalidDataException("Registry comparison bytes exceed the evidence limit.");
            string? current = bytes is null ? null : AuthoredArmorPackage.Hash(bytes);
            string? declared = member.Value.GetString();
            members.Add(new(member.Name, declared, current, bytes?.Length, current is not null && current == declared));
        }
        return new(packageSha256, pieces[0].SetId, pieces, root.Clone(), members,
            members.All(m => m.Matched), DateTimeOffset.UtcNow);
    }

    /// <summary>Compile all art first, persist all eight pieces in one admin transaction, then apply
    /// world templates. Queues the ordinary unified patch; never deploys or restarts either process.</summary>
    public async Task<ArmorSetImportResult> BuildAuthoredSetAsync(AuthoredArmorPackage package)
    {
        await _authoredBuildGate.WaitAsync();
        var reservations = new List<(string Kind, long Id)>(); bool persisted = false;
        string buildId = "armauth-" + Guid.NewGuid().ToString("N")[..12];
        try
        {
            var check = new AuthoredArmorAssetValidator().Validate(package);
            if (!check.Valid) throw new InvalidDataException(string.Join("; ", check.Issues.Where(i => i.Severity == "error").Select(i => i.Asset + ": " + i.Message)));
            if (!DonorItemTemplateFixture.Verify()) throw new InvalidOperationException("Donor item template fixture failed verification.");
            await using var conn = _db.Admin(); await conn.OpenAsync();
            var existing = (await conn.QueryAsync<AuthoredSavedPiece>(
                "SELECT display_id AS DisplayId, item_entry AS ItemEntry, set_id AS SetId, armor_type_key AS ArmorTypeKey, name AS Name, render_kind AS RenderKind, sql_text AS SqlText FROM custom_armor_display WHERE JSON_UNQUOTE(JSON_EXTRACT(gameplay_json, '$.authoredPackageSha256'))=@sha",
                new { sha = package.Sha256 })).ToList();
            if (existing.Count > 0) return await ResumeAuthoredSetAsync(package, existing);
            uint dbcMax = DbcWriterService.ReadDbc(ResolveBaseDbc(), ArmorNaming.ItemDisplayInfoMember).GetMaxId();
            long entryFloor = await _ids.ComputeItemEntryFloorAsync(), displayFloor = await _ids.ComputeDisplayIdFloorAsync(dbcMax);
            int setId = checked((int)await Reserve(KindSet, await ComputeSetIdFloorAsync(), "set"));
            var entries = new Dictionary<string, long>(); var displays = new Dictionary<string, int>();
            foreach (var p in package.Manifest.Pieces)
            {
                entries[p.Key] = await Reserve(WeaponIdReservationService.KindItemEntry, entryFloor, p.Key);
                displays[p.Key] = checked((int)await Reserve(WeaponIdReservationService.KindItemDisplay, displayFloor, p.Key));
            }
            var compiled = new AuthoredArmorCompiler(_mpq, _blp).Compile(package, displays);
            if (!compiled.Report.Compiled) throw new InvalidDataException(string.Join("; ", compiled.Report.Issues.Where(i => i.Severity == "error").Select(i => i.Message)));
            var sqls = new Dictionary<string, GeneratedSql>();
            foreach (var p in compiled.Pieces)
            {
                var profile = ArmorTypeCatalog.Get(p.Key);
                var overrides = profile.ItemTemplateOverrides(p.Source.Material, profile.DefaultArmor(p.Source.Material), setId);
                overrides["quality"] = "3"; overrides["item_level"] = "60"; overrides["required_level"] = "55";
                sqls[p.Key] = WeaponItemTemplateSql.Build(entries[p.Key], p.Source.Name, p.DisplayId, buildId, overrides);
            }
            await using (var tx = await conn.BeginTransactionAsync())
            {
                await conn.ExecuteAsync("INSERT INTO custom_armor_set (set_id,name,bonuses_json,req_skill,req_skill_rank,created_at) VALUES (@setId,@name,'[]',0,0,NOW())",
                    new { setId, name = package.Manifest.Name }, tx);
                foreach (var p in compiled.Pieces)
                {
                    var s = p.Source; var profile = ArmorTypeCatalog.Get(p.Key);
                    await PersistInTransactionAsync(conn, tx, new ArmorPersistRow
                    {
                        DisplayId = p.DisplayId, ItemEntry = entries[p.Key], BuildId = buildId, SetId = setId,
                        RenderKind = s.RenderKind.ToString(), ArmorTypeKey = p.Key, Material = (int)s.Material,
                        InventoryType = profile.InventoryType, Name = s.Name, IconStem = s.IconStem,
                        ModelName = s.ModelName, ModelName2 = s.ModelName2, TextureName = s.TextureName,
                        TextureMpqPath = s.TextureMpqPath, ModelTextureBlp = s.TextureBlp,
                        Geoset0 = s.GeosetGroup[0], Geoset1 = s.GeosetGroup[1], Geoset2 = s.GeosetGroup[2],
                        HelmetVis0 = s.HelmetVis0, HelmetVis1 = s.HelmetVis1, GroupSound = s.GroupSoundIndex,
                        SqlText = sqls[p.Key].Text,
                        GameplayJson = JsonSerializer.Serialize(new { sourceExpansion = "authored", authoredPackageSha256 = package.Sha256,
                            author = package.Manifest.Author, designNotes = package.Manifest.DesignNotes, runtimeStatus = "unverified", report = compiled.Report }),
                    }, s.Components, s.ModelMembers);
                }
                await tx.CommitAsync(); persisted = true;
            }
            // Queue as soon as durable art exists, even if a subsequent world-template apply fails.
            var queued = QueueUnifiedRebuild($"authored set {setId} '{package.Manifest.Name}'");
            foreach (var (kind, id) in reservations) await _ids.MarkStateAsync(kind, id, "committed");
            var result = new ArmorSetImportResult { SetId = setId, Name = package.Manifest.Name };
            foreach (var p in compiled.Pieces)
            {
                RegisterDisplayWithDbc(p.DisplayId, ArmorTypeCatalog.Get(p.Key), p.Source);
                var apply = await ApplyItemSqlAsync(sqls[p.Key], entries[p.Key]);
                result.Pieces.Add(new CustomArmorBuildResult
                {
                    Ok = apply.Ok, SourceExpansion = "authored", ArmorTypeKey = p.Key, RenderKind = p.Source.RenderKind,
                    ItemEntry = entries[p.Key], DisplayId = p.DisplayId, Name = p.Source.Name, Sql = sqls[p.Key].Text,
                    ModelMemberCount = p.Source.ModelMembers.Count, ComponentCount = p.Source.Components.Count,
                    Message = apply.Message, Apply = new ServerApplyStatus { SqlApplied = apply.Ok, SqlMessage = apply.Message,
                        PatchQueued = queued.Queued, PatchPending = queued.Pending, PatchDeployed = false, ServerItemSetState = "Pending", ServerItemSetMessage = "Deployment is pending; rebuild the unified patch and server ItemSet.dbc before runtime testing." },
                });
            }
            if (result.Pieces.Any(p => p.Ok))
            {
                var reload = await ReloadItemTemplateAsync();
                foreach (var p in result.Pieces) if (p.Apply is { } apply) { apply.Reloaded = reload.Ok; apply.ReloadMessage = reload.Message; }
            }
            result.PatchQueued = queued.Queued; result.PatchPending = queued.Pending;
            result.ServerItemSetMessage = "Not deployed. The normal Rebuild patch action must deploy ItemSet.dbc; a server restart is required before set membership works.";
            result.Message = $"Saved all 8 original-art pieces as set {setId}; {result.Pieces.Count(p => p.Ok)}/8 world rows applied. Patch queued, not deployed. Runtime fit remains unverified.";
            await _audit.LogAsync(new AuditEntry { Category = "armorforge", Action = "authored_set", TargetType = "itemset", TargetName = result.Name,
                TargetId = setId, IsReversible = true, RevertKind = RevertKind.Registry, Success = result.Pieces.All(p => p.Ok),
                StateAfter = JsonSerializer.Serialize(new { buildId, setId, package.Sha256, runtimeStatus = "unverified", pieces = result.Pieces.Select(p => new { p.ItemEntry, p.DisplayId, p.Ok }) }), Notes = result.Message });
            return result;
        }
        finally
        {
            try { if (!persisted) foreach (var (kind, id) in reservations) await _ids.ReleaseAsync(kind, id); }
            finally { _authoredBuildGate.Release(); }
        }

        async Task<long> Reserve(string kind, long floor, string purpose)
        { var reservation = await _ids.ReserveAsync(kind, floor, buildId, purpose); reservations.Add((kind, reservation.Id)); return reservation.Id; }
    }

    private sealed class AuthoredSavedPiece
    {
        public long DisplayId { get; set; }
        public long ItemEntry { get; set; }
        public int SetId { get; set; }
        public string ArmorTypeKey { get; set; } = "";
        public string Name { get; set; } = "";
        public string RenderKind { get; set; } = "";
        public string SqlText { get; set; } = "";
    }
    private sealed class AuthoredWorldIdentity
    {
        public long DisplayId { get; set; }
        public int SetId { get; set; }
    }

    /// <summary>Recover a partial MyISAM apply using the already-owned IDs. Matching rows may have
    /// operator gameplay edits and are preserved. Conflicts are never overwritten by a retry.</summary>
    private async Task<ArmorSetImportResult> ResumeAuthoredSetAsync(AuthoredArmorPackage package, List<AuthoredSavedPiece> saved)
    {
        if (saved.Count != 8 || saved.Select(p => p.SetId).Distinct().Count() != 1 || saved[0].SetId <= 0
            || saved.Select(p => p.ItemEntry).Distinct().Count() != 8 || saved.Select(p => p.DisplayId).Distinct().Count() != 8
            || !saved.Select(p => p.ArmorTypeKey).OrderBy(p => p).SequenceEqual(package.Manifest.Pieces.Select(p => p.Key).OrderBy(p => p)))
            throw new InvalidOperationException("This package has an incomplete or changed registry set. Review its existing registry rows before rebuilding; no new IDs were allocated.");
        int setId = saved[0].SetId;
        var queued = QueueUnifiedRebuild($"resuming authored set {setId} '{package.Manifest.Name}'");
        var result = new ArmorSetImportResult { SetId = setId, Name = package.Manifest.Name,
            PatchQueued = queued.Queued, PatchPending = queued.Pending, ServerItemSetMessage = "Pending ordinary patch/ItemSet deployment and server restart." };
        foreach (var p in saved)
        {
            bool ok = false; string message;
            try
            {
                await using var world = _db.Mangos(); await world.OpenAsync();
                var rows = (await world.QueryAsync<AuthoredWorldIdentity>("SELECT display_id AS DisplayId, set_id AS SetId FROM item_template WHERE entry=@entry", new { entry = p.ItemEntry })).ToList();
                var disposition = AuthoredArmorResumePolicy.Decide(p.DisplayId, setId, rows.Select(r => (r.DisplayId, r.SetId)).ToArray());
                if (disposition == AuthoredArmorResumeDisposition.AlreadyApplied)
                { ok = true; message = "Existing matching world row retained, including operator gameplay edits."; }
                else if (disposition == AuthoredArmorResumeDisposition.Missing)
                {
                    var sql = new GeneratedSql(p.SqlText, AuthoredArmorPackage.Hash(System.Text.Encoding.UTF8.GetBytes(p.SqlText)));
                    var applied = await ApplyItemSqlAsync(sql, p.ItemEntry); ok = applied.Ok; message = applied.Message;
                }
                else message = "World entry is occupied by a different display/set or multiple patch rows; retry refused to overwrite it.";
            }
            catch (Exception ex) { message = "World retry failed: " + ex.Message; }
            result.Pieces.Add(new() { Ok = ok, SourceExpansion = "authored", ArmorTypeKey = p.ArmorTypeKey,
                RenderKind = Enum.Parse<ArmorRenderKind>(p.RenderKind), ItemEntry = p.ItemEntry, DisplayId = p.DisplayId, Name = p.Name,
                Message = message, Apply = new() { SqlApplied = ok, SqlMessage = message, PatchQueued = queued.Queued,
                    PatchPending = queued.Pending, ServerItemSetState = "Pending", ServerItemSetMessage = result.ServerItemSetMessage } });
        }
        if (result.Pieces.Any(p => p.Ok))
        {
            var reload = await ReloadItemTemplateAsync();
            foreach (var p in result.Pieces) { p.Apply!.Reloaded = reload.Ok; p.Apply.ReloadMessage = reload.Message; }
        }
        result.Message = $"Resumed existing set {setId}: {result.Pieces.Count(p => p.Ok)}/8 world rows present. Original IDs retained; patch queued, not deployed. Runtime fit remains unverified.";
        await _audit.LogAsync(new AuditEntry { Category = "armorforge", Action = "authored_set_resume", TargetType = "itemset", TargetId = setId,
            TargetName = result.Name, IsReversible = true, RevertKind = RevertKind.Registry, Success = result.Pieces.All(p => p.Ok),
            Notes = result.Message, StateAfter = JsonSerializer.Serialize(new { package.Sha256, setId, pieces = result.Pieces.Select(p => new { p.ItemEntry, p.DisplayId, p.Ok }) }) });
        return result;
    }
}

public sealed class AuthoredArmorBuiltPiece
{
    public int DisplayId { get; set; }
    public long ItemEntry { get; set; }
    public int SetId { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int InventoryType { get; set; }
}
public sealed record AuthoredArmorBuiltMember(string MpqPath, string? ExpectedSha256,
    string? RegistrySha256, int? RegistryBytes, bool Matched);
public sealed record AuthoredArmorBuiltEvidence(string SourcePackageSha256, int SetId,
    IReadOnlyList<AuthoredArmorBuiltPiece> Pieces, JsonElement StoredCompilationReport,
    IReadOnlyList<AuthoredArmorBuiltMember> Members, bool AllRegistryMembersMatch, DateTimeOffset CheckedAt)
{
    public bool RuntimeVerified => false;
    public string EvidenceScope => "Saved compilation report compared with current registry member bytes. Does not verify the installed MPQ, DBC rows, capture execution, or wearer fit. Registry member cache may be up to 30 seconds old.";
}

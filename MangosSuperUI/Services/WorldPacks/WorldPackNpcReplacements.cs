using System.Text.Json.Nodes;
using Dapper;
using MySqlConnector;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>A pack may replace one existing spawn's template without changing the stock template.
/// The original entry and pending writes are durable before touching the MyISAM world table.</summary>
public static class WorldPackNpcReplacements
{
    public sealed record Replacement(uint SpawnGuid, uint OriginalEntry, uint ReplacementEntry, int PackId);
    public sealed record Change(uint SpawnGuid, uint OriginalEntry, uint ObservedEntry, uint TargetEntry,
        uint? InstalledEntry, uint? PendingEntry, int PackId, bool Restore);
    public sealed class Baseline
    {
        public uint SpawnGuid { get; set; }
        public uint OriginalEntry { get; set; }
        public uint? InstalledEntry { get; set; }
        public uint? PendingEntry { get; set; }
        public int PackId { get; set; }
    }

    public static Replacement Parse(JsonObject body, int packId = 0)
    {
        uint Number(string key) => body[key] is { } n && uint.TryParse(WorldPackContent.Scalar(n), out uint v) ? v : 0;
        uint spawn = Number("spawnGuid"), original = Number("originalEntry"), replacement = Number("replacementEntry");
        if (spawn == 0 || spawn >= WorldPackContent.SpawnGuidBase)
            throw new ArgumentException("NPC replacement needs an existing stock spawn GUID.");
        if (original == 0 || original >= WorldPackContent.TemplateBase || replacement < WorldPackContent.TemplateBase)
            throw new ArgumentException("NPC replacement needs the original stock entry and a reserved replacement template.");
        return new(spawn, original, replacement, packId);
    }

    public static List<Replacement> ReadDesired(IReadOnlyList<DocRow> docs)
    {
        var result = new List<Replacement>();
        foreach (var doc in docs.Where(d => d.Kind == "npc-replacement"))
        {
            var item = Parse(JsonNode.Parse(doc.Body)!.AsObject(), doc.PackId);
            if (doc.DocKey != item.SpawnGuid.ToString()) throw new ArgumentException("NPC replacement key must match its spawn GUID.");
            if (result.Any(r => r.SpawnGuid == item.SpawnGuid))
                throw new ArgumentException($"More than one enabled pack replaces NPC spawn {item.SpawnGuid}.");
            if (!docs.Any(d => d.PackId == item.PackId && d.Kind == "dbrow:creature_template" &&
                JsonNode.Parse(d.Body)?["entry"] is { } entry && WorldPackContent.Scalar(entry) == item.ReplacementEntry.ToString()))
                throw new ArgumentException($"Replacement NPC {item.ReplacementEntry} must be a template owned by the same pack.");
            result.Add(item);
        }
        return result;
    }

    public static Change Plan(Replacement? desired, Baseline? baseline, uint spawnGuid, uint? current)
    {
        if (current is null) throw new InvalidOperationException($"NPC spawn {spawnGuid} no longer exists.");
        // A completed restoration ends that replacement generation. A fresh draft must validate
        // against the live stock entry, which may legitimately have changed since the previous generation.
        if (desired is not null && baseline is { InstalledEntry: null, PendingEntry: null }) baseline = null;
        uint original = baseline?.OriginalEntry ?? desired?.OriginalEntry ?? throw new ArgumentException("Missing replacement state.");
        if (desired is not null && desired.OriginalEntry != original)
            throw new InvalidOperationException($"NPC spawn {spawnGuid}: the recorded original entry does not match this draft.");
        if (current != original && current != baseline?.InstalledEntry && current != baseline?.PendingEntry)
            throw new InvalidOperationException($"NPC spawn {spawnGuid} was changed outside this pack. Refresh it before publishing; no replacement was applied.");
        return new(spawnGuid, original, current.Value, desired?.ReplacementEntry ?? original,
            baseline?.InstalledEntry, baseline?.PendingEntry, desired?.PackId ?? baseline?.PackId ?? 0, desired is null);
    }

    public static async Task<List<Change>> PrepareAsync(MySqlConnection world, MySqlConnection admin, IReadOnlyList<DocRow> docs)
    {
        var desired = ReadDesired(docs).ToDictionary(d => d.SpawnGuid);
        var saved = (await admin.QueryAsync<Baseline>(@"SELECT spawn_guid AS SpawnGuid, original_entry AS OriginalEntry,
installed_entry AS InstalledEntry, pending_entry AS PendingEntry, pack_id AS PackId FROM wp_npc_baseline")).ToDictionary(b => b.SpawnGuid);
        var ids = desired.Keys.Concat(saved.Values.Where(b => b.InstalledEntry is not null || b.PendingEntry is not null).Select(b => b.SpawnGuid)).Distinct().ToList();
        var plan = new List<Change>();
        foreach (uint id in ids)
        {
            var current = await world.QuerySingleOrDefaultAsync<uint?>("SELECT id FROM creature WHERE guid = @id", new { id });
            plan.Add(Plan(desired.GetValueOrDefault(id), saved.GetValueOrDefault(id), id, current));
        }
        return plan;
    }

    public static async Task<int> ApplyAsync(MySqlConnection world, MySqlConnection admin, IEnumerable<Change> changes)
    {
        int count = 0;
        foreach (var c in changes)
        {
            // The observed entry may be the previous attempt's pending target. Preserve that
            // actual value before replacing pending, so a second crash still admits either side of CAS.
            var beforeWrite = BeforeWrite(c);
            await admin.ExecuteAsync(@"INSERT INTO wp_npc_baseline
(spawn_guid, original_entry, installed_entry, pending_entry, pack_id)
VALUES (@SpawnGuid, @OriginalEntry, @InstalledEntry, @PendingEntry, @PackId)
ON DUPLICATE KEY UPDATE original_entry = VALUES(original_entry), installed_entry = VALUES(installed_entry),
pending_entry = VALUES(pending_entry), pack_id = VALUES(pack_id)", beforeWrite);
            if (c.ObservedEntry != c.TargetEntry)
            {
                int changed = await world.ExecuteAsync("UPDATE creature SET id = @TargetEntry WHERE guid = @SpawnGuid AND id = @ObservedEntry", c);
                if (changed != 1) throw new InvalidOperationException($"NPC spawn {c.SpawnGuid} changed during publish; its baseline was retained.");
                count += changed;
            }
            await admin.ExecuteAsync(@"UPDATE wp_npc_baseline SET installed_entry = @entry, pending_entry = NULL
WHERE spawn_guid = @SpawnGuid", new { c.SpawnGuid, entry = c.Restore ? (uint?)null : c.TargetEntry });
        }
        return count;
    }

    /// <summary>The durable recovery state immediately before the compare-and-swap world write.</summary>
    public static Baseline BeforeWrite(Change change) => new()
    {
        SpawnGuid = change.SpawnGuid,
        OriginalEntry = change.OriginalEntry,
        InstalledEntry = change.ObservedEntry,
        PendingEntry = change.TargetEntry,
        PackId = change.PackId,
    };
}

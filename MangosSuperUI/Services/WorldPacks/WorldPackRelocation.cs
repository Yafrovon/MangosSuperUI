using System.Globalization;
using System.Text.Json.Nodes;

namespace MangosSuperUI.Services.WorldPacks;

/// <summary>
/// Move a pack's region: everything the pack placed on <see cref="FromMap"/> moves to <see cref="ToMap"/>
/// shifted by a whole number of ADT tiles (<see cref="DCol"/>, <see cref="DRow"/>), so stamped tiles land on
/// tile boundaries and every coordinate moves by the same world offset. Built for Gilneas 2026-09-27: a zone
/// had been a separate map behind a teleport at the Greymane Wall; zones in WoW are seamless continent land.
/// </summary>
public sealed record RelocateSpec(int FromMap, int ToMap, int DCol, int DRow, bool ToStockMap)
{
    /// <summary>World X is north (row grows southward), world Y is west (col grows eastward).</summary>
    public float Dx => -DRow * WorldCoords.Tile;
    public float Dy => -DCol * WorldCoords.Tile;
}

/// <summary>
/// The pure doc half of a region move (MangosSuperUI.Tests: WorldPackRelocationTests). One doc in → the
/// doc changes out: a modified body, a delete, or a delete + create when the key itself carries the
/// location (tile docs: "map:col:row"). Docs that do not reference <see cref="RelocateSpec.FromMap"/>
/// are untouched. Rotations never change (a translation, not a turn).
/// </summary>
public static class WorldPackRelocation
{
    public readonly record struct Change(string Kind, string Key, string? Body);

    public static List<Change> Transform(string kind, string key, JsonObject? body, RelocateSpec r)
    {
        var none = new List<Change>();
        if (body is null) return none;
        var b = (JsonObject)body.DeepClone();
        bool changed = false;

        switch (kind)
        {
            case "tile":
                if (I(b["map"]) != r.FromMap) return none;
                b["map"] = r.ToMap;
                b["col"] = MovedTile(I(b["col"]), r.DCol);
                b["row"] = MovedTile(I(b["row"]), r.DRow);
                ShiftPoints(b["healHoles"] as JsonArray, r);
                ShiftPoints(b["areaPaint"] as JsonArray, r);
                string newKey = $"{r.ToMap}:{I(b["col"])}:{I(b["row"])}";
                return newKey == key
                    ? new() { new(kind, key, b.ToJsonString()) }
                    : new() { new(kind, key, null), new(kind, newKey, b.ToJsonString()) };

            case "path":
                if (I(b["map"]) != r.FromMap) return none;
                b["map"] = r.ToMap;
                ShiftArrayPoints(b["points"] as JsonArray, r, 3, "path");
                var path = GradedPath.Parse(key, b.ToJsonString());
                if (path.Points.Count < 2 || !float.IsFinite(path.Width) || path.Width <= 0 ||
                    !float.IsFinite(path.Falloff) || path.Falloff < 0 || path.Tiles().Any(t => t.col is < 0 or > 63 || t.row is < 0 or > 63))
                    throw new ArgumentException($"path '{key}' would leave the map or has invalid dimensions");
                changed = true;
                break;

            case "worldmap":
                if (I(b["map"]) != r.FromMap) return none;
                b["map"] = r.ToMap;
                ShiftArrayPoints(b["polygon"] as JsonArray, r, 2, "worldmap polygon");
                if (b["terrain"] is JsonObject terrain)
                {
                    if (terrain["tiles"] is not JsonArray tiles) throw new ArgumentException("coast terrain needs explicit tiles");
                    foreach (var node in tiles)
                    {
                        if (node is not JsonArray { Count: 2 } tile) throw new ArgumentException("coast tiles need [column,row]");
                        tile[0] = MovedTile(I(tile[0]), r.DCol); tile[1] = MovedTile(I(tile[1]), r.DRow);
                    }
                    if (terrain["joinNorth"] is not null) terrain["joinNorth"] = (float)(D(terrain["joinNorth"]) + r.Dx);
                }
                var region = WorldPackWorldMap.Parse(b); // Reject instance destinations, invalid geometry and hover bounds.
                string regionKey = region.Key;
                if (b["terrain"] is not null) _ = WorldPackCoast.Read(new[] { new DocRow { Kind = "worldmap", DocKey = regionKey, Body = b.ToJsonString() } });
                return regionKey == key ? new() { new(kind, key, b.ToJsonString()) }
                    : new() { new(kind, key, null), new(kind, regionKey, b.ToJsonString()) };

            case "map":
                // A pack map that becomes continent land stops existing as a map.
                return I(b["mapId"]) == r.FromMap && r.ToStockMap ? new() { new(kind, key, null) } : none;

            case "dbc:Map":
                return key == r.FromMap.ToString(CultureInfo.InvariantCulture) && r.ToStockMap ? new() { new(kind, key, null) } : none;

            case "dbrow:map_template":
                if (I(b["entry"]) == r.FromMap && r.ToStockMap) return new() { new(kind, key, null) };
                if (I(b["ghost_entrance_map"]) == r.FromMap)
                {
                    b["ghost_entrance_map"] = r.ToMap;
                    Shift(b, "ghost_entrance_x", "ghost_entrance_y", r);
                    changed = true;
                }
                break;

            case "dbrow:creature":
            case "dbrow:gameobject":
                if (I(b["map"]) == r.FromMap) { b["map"] = r.ToMap; Shift(b, "position_x", "position_y", r); changed = true; }
                break;

            case "dbrow:areatrigger_template":
                if (I(b["map_id"]) == r.FromMap) { b["map_id"] = r.ToMap; Shift(b, "x", "y", r); changed = true; }
                break;

            case "dbrow:areatrigger_teleport":
                if (I(b["target_map"]) == r.FromMap) { b["target_map"] = r.ToMap; Shift(b, "target_position_x", "target_position_y", r); changed = true; }
                break;

            case "dbrow:area_template":
                if (I(b["map_id"]) == r.FromMap) { b["map_id"] = r.ToMap; changed = true; }
                break;

            case "dbc:AreaTrigger":
            case "dbc:WorldSafeLocs":
                if (b["fields"] is JsonObject f && I(f["1"]) == r.FromMap)
                {
                    f["1"] = r.ToMap;
                    f["2"] = new JsonObject { ["f"] = D(f["2"]) + r.Dx };
                    f["3"] = new JsonObject { ["f"] = D(f["3"]) + r.Dy };
                    changed = true;
                }
                break;

            case "dbc:AreaTable":
                if (b["fields"] is JsonObject at && I(at["1"]) == r.FromMap) { at["1"] = r.ToMap; changed = true; }
                break;

            case "dbc:Light":
                // A pack map clones the continent's global light; a continent already has its own.
                if (b["fields"] is JsonObject lf && I(lf["1"]) == r.FromMap)
                {
                    if (r.ToStockMap) return new() { new(kind, key, null) };
                    lf["1"] = r.ToMap; changed = true;
                }
                break;
        }
        return changed ? new() { new(kind, key, b.ToJsonString()) } : none;
    }

    /// <summary>Validate the whole move against pack ownership before any document or geometry is written.</summary>
    public static void ValidateDestination(int packId, IReadOnlyList<DocRow> ownDocs, IReadOnlyList<DocRow> otherDocs, RelocateSpec spec)
    {
        if (spec.FromMap < 0 || spec.ToMap < 0 || spec.DCol is < -63 or > 63 || spec.DRow is < -63 or > 63)
            throw new ArgumentException("region maps and tile offsets are outside the supported grid");
        if (spec.ToMap is not (0 or 1) && !ownDocs.Any(d => d.Kind == "map" && I(JsonNode.Parse(d.Body)?["mapId"]) == spec.ToMap))
            throw new ArgumentException("destination must be a continent or a custom map defined by this pack");
        var movedSpawns = ownDocs.Where(d => d.Kind == "dbrow:creature").Select(d => JsonNode.Parse(d.Body)!.AsObject())
            .Where(b => I(b["map"]) == spec.FromMap).Select(b => I(b["guid"])).ToHashSet();
        if (ownDocs.Any(d => d.Kind == "dbrow:creature_movement" && movedSpawns.Contains(I(JsonNode.Parse(d.Body)?["id"]))))
            throw new ArgumentException("this region contains NPC patrol routes; region relocation cannot yet move those routes safely");
        var changes = ownDocs.SelectMany(d => Transform(d.Kind, d.DocKey, JsonNode.Parse(d.Body)!.AsObject(), spec)
            .Select(c => (Source: d.DocKey, Change: c))).ToList();
        var vacated = changes.Where(c => c.Change.Body is null).Select(c => (c.Change.Kind, c.Change.Key)).ToHashSet();
        var keys = ownDocs.Select(d => (d.Kind, d.DocKey)).ToHashSet();
        foreach (var (source, change) in changes.Where(c => c.Change.Body is not null))
        {
            if (change.Key != source && keys.Contains((change.Kind, change.Key)) && !vacated.Contains((change.Kind, change.Key)))
                throw new InvalidOperationException($"{change.Kind} {change.Key} already belongs to this pack at the destination");
            if (change.Kind is "tile" or "worldmap" && otherDocs.Any(d => d.Kind == change.Kind && d.DocKey == change.Key))
                throw new InvalidOperationException($"{change.Kind} {change.Key} belongs to another pack at the destination");
        }
        // Check translated hover ownership against every saved region, including disabled drafts.
        var resulting = ownDocs.ToDictionary(d => (d.Kind, d.DocKey), d => d);
        foreach (var c in changes.Select(c => c.Change).OrderBy(c => c.Body is null ? 0 : 1))
            if (c.Body is null) resulting.Remove((c.Kind, c.Key));
            else resulting[(c.Kind, c.Key)] = new DocRow { PackId = packId, Kind = c.Kind, DocKey = c.Key, Body = c.Body };
        _ = WorldPackWorldMap.ReadRegions(resulting.Values.Concat(otherDocs));
    }

    private static int MovedTile(int value, int delta)
    {
        long moved = (long)value + delta;
        if (moved is < 0 or > 63) throw new ArgumentException("the moved region leaves the 64x64 tile grid");
        return (int)moved;
    }

    private static void ShiftArrayPoints(JsonArray? points, RelocateSpec spec, int dimensions, string name)
    {
        if (points is null) throw new ArgumentException($"{name} points are missing");
        foreach (var node in points)
        {
            if (node is not JsonArray p || p.Count != dimensions) throw new ArgumentException($"{name} point needs {dimensions} coordinates");
            double x = D(p[0]) + spec.Dx, y = D(p[1]) + spec.Dy;
            if (!double.IsFinite(x) || !double.IsFinite(y) || Math.Abs(x) >= WorldCoords.Corner || Math.Abs(y) >= WorldCoords.Corner ||
                (dimensions == 3 && !double.IsFinite(D(p[2])))) throw new ArgumentException($"moved {name} point is outside the map");
            p[0] = (float)x; p[1] = (float)y;
        }
    }

    private static void Shift(JsonObject b, string xKey, string yKey, RelocateSpec r)
    {
        b[xKey] = Math.Round(D(b[xKey]) + r.Dx, 3);
        b[yKey] = Math.Round(D(b[yKey]) + r.Dy, 3);
    }

    private static void ShiftPoints(JsonArray? points, RelocateSpec r)
    {
        foreach (var p in points?.OfType<JsonObject>() ?? Enumerable.Empty<JsonObject>())
            Shift(p, "x", "y", r);
    }

    /// <summary>Numbers arrive as JSON numbers, numeric strings or DBC floats <c>{ "f": x }</c>.</summary>
    public static double D(JsonNode? n) => n switch
    {
        JsonObject o when o["f"] is JsonNode f => D(f),
        JsonValue v when v.TryGetValue(out double d) => d,
        JsonValue v when double.TryParse(v.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out double s) => s,
        _ => 0d,
    };

    public static int I(JsonNode? n) => (int)Math.Round(D(n));
}

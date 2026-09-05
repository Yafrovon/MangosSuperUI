using System.Buffers.Binary;

namespace MangosSuperUI.Services.WeaponForge.RawM2;

/// <summary>
/// Switches the blend mode of render-flag entries used EXCLUSIVELY by compositing batches over the
/// admitted native-effect textures. Additive passes can only ever add light, so a dark glow pick
/// (black, deep red) has nothing to add and vanishes; re-flagging those passes as alpha-blend lets
/// the (now alpha-carrying, darkened) effect sheet draw a dark aura instead. Offset-stable: it
/// clones the M2 and rewrites only the two-byte blend field of each selected render-flag entry. An
/// entry also used by an opaque or out-of-boundary batch is left alone and reported, since the same
/// entry would otherwise drag the body of the weapon into alpha blending.
/// </summary>
public static class M2EffectBlendModeWriter
{
    private const int BatchSize = 24;

    public sealed record Result(
        byte[] M2,
        IReadOnlyList<int> MaterialsChanged,
        IReadOnlyList<int> MaterialsSkipped,
        bool IsComplete,
        IReadOnlyList<string> Notes);

    public static Result Apply(byte[] m2, IReadOnlyCollection<int> eligibleTextureIndices, ushort targetBlendMode = 2)
    {
        ArgumentNullException.ThrowIfNull(m2);
        ArgumentNullException.ThrowIfNull(eligibleTextureIndices);
        var notes = new List<string>();

        RawM2Document? document = RawM2Document.Parse(m2, out string? parseError);
        if (document is null)
            return Failure(m2, parseError ?? "target is not a canonical v256 M2");

        RawM2Array? renderFlags = document.FindArray("renderFlags");
        RawM2Array? textures = document.FindArray("textures");
        RawM2Array? textureLookup = document.FindArray("textureLookup");
        if (renderFlags is null || !renderFlags.InBounds)
            return Failure(m2, "model carries no valid render-flag table");
        if (textures is null || !textures.InBounds || textureLookup is null || !textureLookup.InBounds)
            return Failure(m2, "model carries a malformed texture table or texture lookup");

        var eligible = eligibleTextureIndices.ToHashSet();
        if (eligible.Count == 0)
            return new Result(m2, Array.Empty<int>(), Array.Empty<int>(), true, notes);
        if (eligible.Any(index => index < 0 || (uint)index >= textures.Count))
            return Failure(m2, "eligible native-effect texture set contains an invalid texture index");

        foreach (RawM2View view in document.Views)
            if (!view.HeaderInBounds || !view.Batches.InBounds)
                return Failure(m2, $"view {view.Index} has an invalid header or batch table");

        // Per render-flag entry: is it used by an admitted compositing batch, and by anything else?
        var effectUse = new HashSet<int>();
        var otherUse = new HashSet<int>();
        bool graphComplete = true;
        foreach (RawM2View view in document.Views)
        {
            for (uint batchIndex = 0; batchIndex < view.Batches.Count; batchIndex++)
            {
                int batch = checked((int)(view.Batches.Offset + (long)batchIndex * BatchSize));
                ushort materialIndex = U16(m2, batch + 10);
                if (materialIndex >= renderFlags.Count)
                {
                    notes.Add($"view {view.Index} batch {batchIndex} references invalid material {materialIndex}");
                    graphComplete = false;
                    continue;
                }

                ushort textureCount = U16(m2, batch + 14);
                ushort textureCombo = U16(m2, batch + 16);
                if ((long)textureCombo + textureCount > textureLookup.Count)
                {
                    notes.Add($"view {view.Index} batch {batchIndex} has an invalid texture span");
                    graphComplete = false;
                    otherUse.Add(materialIndex);
                    continue;
                }

                bool within = textureCount > 0;
                for (int unit = 0; unit < textureCount && within; unit++)
                {
                    int combo = checked((int)(textureLookup.Offset + ((long)textureCombo + unit) * sizeof(ushort)));
                    ushort textureIndex = U16(m2, combo);
                    if (textureIndex >= textures.Count)
                    {
                        notes.Add($"view {view.Index} batch {batchIndex} unit {unit} references invalid texture {textureIndex}");
                        graphComplete = false;
                        within = false;
                        break;
                    }
                    if (!eligible.Contains(textureIndex)) within = false;
                }

                int material = checked((int)(renderFlags.Offset + (long)materialIndex * 4));
                ushort blendMode = U16(m2, material + 2);
                if (within && blendMode >= 3) effectUse.Add(materialIndex);
                else otherUse.Add(materialIndex);
            }
        }

        var output = (byte[])m2.Clone();
        var changed = new List<int>();
        var skipped = new List<int>();
        foreach (int materialIndex in effectUse.OrderBy(i => i))
        {
            if (otherUse.Contains(materialIndex))
            {
                skipped.Add(materialIndex);
                notes.Add($"material {materialIndex} kept additive: an opaque or out-of-boundary batch shares it");
                continue;
            }
            int material = checked((int)(renderFlags.Offset + (long)materialIndex * 4));
            BinaryPrimitives.WriteUInt16LittleEndian(output.AsSpan(material + 2, 2), targetBlendMode);
            changed.Add(materialIndex);
        }

        return new Result(output, changed, skipped, graphComplete && skipped.Count == 0, notes);
    }

    private static Result Failure(byte[] m2, string note) =>
        new(m2, Array.Empty<int>(), Array.Empty<int>(), false, new[] { note });

    private static ushort U16(byte[] data, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
}

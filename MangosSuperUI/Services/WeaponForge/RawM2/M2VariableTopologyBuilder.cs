using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace MangosSuperUI.Services.WeaponForge.RawM2;

/// <summary>
/// Emits a valid MD20 v256 for ARBITRARY (variable) topology by APPENDING, not rebuilding
/// (WEAPON_GEN.md Phase 5). It keeps the entire donor file intact — so every nested bone/animation/
/// event/attachment pointer stays valid by construction — then appends fresh data at end-of-file
/// and repoints only top-level header fields. The donor's original arrays become harmless dead space.
///
/// Two emission modes:
///
///  • SINGLE-PASS (mesh.Passes == null): one submesh + one batch per view, reusing the donor's
///    preserved lookup/bone/texture tables BY INDEX (the copied batch resolves against the untouched
///    donor arrays). An explicit material replaces blend mode and the two-sided bit in the
///    donor render-flag record (fixed-width, offset-preserving); null keeps donor state.
///
///  • MULTI-PASS (mesh.Passes set — TBC imports): N submeshes + the complete source batch stack per
///    view. Textures, render flags, texture/coordinate/weight/transform combo tables, and constant
///    transparency tracks are appended and repointed. All 24 bytes of each batch are authored from
///    the render IR; no source pass inherits unrelated shader or combo indices from the donor.
///
/// Two deliberate policies still require reference-client proof (they are reported by the caller):
///   1. Four EQUIVALENT views (not real per-LOD structures).
///   2. Appended layout leaving the donor's original geometry as dead bytes.
///
/// New vertices are rigidly weighted (255,0,0,0) to bone 0, matching every measured stock sword.
/// </summary>
public static class M2VariableTopologyBuilder
{
    /// <summary>Build the M2. Inputs are WoW model space (Z-up); the caller applies the §2.3
    /// contract. Indices are a flat triangle list into the vertex arrays (UInt16-safe).
    /// <paramref name="material"/> adjusts the donor render flag for single-pass output;
    /// <paramref name="effectTexturePaths"/> supplies the packaged MPQ member paths for the mesh's
    /// effect texture slots (required when mesh.Passes references slots ≥ 1).</summary>
    public static byte[] Build(byte[] donor, IReadOnlyList<Vector3> posWoW, IReadOnlyList<Vector3> normalWoW,
        IReadOnlyList<Vector2> uv0, RigidWeaponMesh mesh, int viewCount = 4,
        WeaponMaterial? material = null, IReadOnlyList<string>? effectTexturePaths = null)
    {
        var doc = RawM2Document.Parse(donor, out var err)
            ?? throw new InvalidOperationException($"Donor parse failed: {err}");
        if (doc.Views.Count == 0) throw new InvalidOperationException("Donor has no views to template from.");

        var indices = mesh.Indices;
        int n = posWoW.Count;
        if (n == 0 || n > ushort.MaxValue) throw new ArgumentException($"Vertex count {n} invalid (1..65535).");
        if (normalWoW.Count != n || uv0.Count != n) throw new ArgumentException("Vertex attribute length mismatch.");
        int t3 = indices.Length;
        if (t3 == 0 || t3 % 3 != 0) throw new ArgumentException($"Index count {t3} is not a positive multiple of 3.");
        if (t3 > ushort.MaxValue) throw new ArgumentException($"Index count {t3} exceeds the v256 skin-section address space ({ushort.MaxValue}).");
        foreach (var ix in indices) if (ix >= n) throw new ArgumentException($"Index {ix} out of range (>= {n}).");

        // Pass plan: single pseudo-pass over everything, or the mesh's own multi-pass structure.
        bool multiPass = mesh.Passes is { Count: > 0 } && mesh.SubmeshRanges is { Count: > 0 };
        var ranges = multiPass
            ? mesh.SubmeshRanges!
            : new[] { new WeaponSubmeshRange { IndexStart = 0, IndexCount = t3, VertexStart = 0, VertexCount = n } };
        var passes = multiPass
            ? mesh.Passes!
            : new[] { new WeaponPass { SubmeshSlot = 0, RenderFlags = 0, BlendMode = 0, Layer = 0, TextureSlot = 0 } };
        int nSub = ranges.Count, nBatch = passes.Count;

        static IReadOnlyList<WeaponTextureBinding> BindingsOf(WeaponPass pass) =>
            pass.TextureBindings is { Count: > 0 } bindings
                ? bindings
                : new[] { new WeaponTextureBinding { TextureSlot = pass.TextureSlot } };
        var passBindings = passes.Select(BindingsOf).ToArray();
        var restColors = passes.Where(p => p.RestColor is not null)
            .Select(p => p.RestColor!).Distinct().ToList();
        // Texture-transform provenance is the source lookup index, not the evaluated rest value.
        // Two source records can share the same rest pose but carry different animation, while one
        // source record can be referenced by several units. Preserve first-use source identity and
        // reject contradictory copies instead of collapsing records through record equality.
        var transformPlans = new List<(ushort SourceIndex, WeaponRestTextureTransform Payload)>();
        var transformPlanIndex = new Dictionary<ushort, int>();
        foreach (var binding in passBindings.SelectMany(b => b))
        {
            if (binding.TextureTransform == ushort.MaxValue || binding.RestTransform is null) continue;
            if (transformPlanIndex.TryGetValue(binding.TextureTransform, out int existing))
            {
                if (!SameTransformPayload(transformPlans[existing].Payload, binding.RestTransform))
                    throw new ArgumentException(
                        $"Source UV transform {binding.TextureTransform} is referenced with conflicting payloads.");
                continue;
            }

            transformPlanIndex[binding.TextureTransform] = transformPlans.Count;
            transformPlans.Add((binding.TextureTransform, binding.RestTransform));
        }
        var restTransforms = transformPlans.Select(p => p.Payload).ToList();
        int effectSlots = passBindings.SelectMany(b => b).Max(b => b.TextureSlot);
        if (effectSlots < 0)
            throw new InvalidOperationException($"Mesh references invalid negative texture slot {effectSlots}.");

        int texCount = Math.Max(checked(1 + effectSlots), mesh.TextureSlots?.Count ?? 0);
        if (texCount > ushort.MaxValue)
            throw new InvalidOperationException($"Texture count {texCount} exceeds the v256 UInt16 lookup address space ({ushort.MaxValue}).");
        if (restColors.Count > short.MaxValue)
            throw new InvalidOperationException($"Color count {restColors.Count} exceeds the v256 signed batch index address space ({short.MaxValue}).");
        if (restTransforms.Count > ushort.MaxValue)
            throw new InvalidOperationException($"UV-transform count {restTransforms.Count} exceeds the v256 lookup address space ({ushort.MaxValue}).");
        int requiredEffectPaths = texCount - 1;
        if (requiredEffectPaths > 0 && (effectTexturePaths is null || effectTexturePaths.Count < requiredEffectPaths))
            throw new InvalidOperationException(
                $"Mesh requires {requiredEffectPaths} effect texture path(s) but only {effectTexturePaths?.Count ?? 0} were supplied.");

        if (nSub > ushort.MaxValue)
            throw new ArgumentException($"Submesh count {nSub} exceeds the v256 UInt16 batch address space ({ushort.MaxValue}).");

        foreach (var r in ranges)
        {
            if (r.VertexStart < 0 || r.VertexCount < 0 || r.IndexStart < 0 || r.IndexCount < 0 ||
                r.VertexStart > ushort.MaxValue || r.VertexCount > ushort.MaxValue ||
                r.IndexStart > ushort.MaxValue || r.IndexCount > ushort.MaxValue)
                throw new ArgumentException("A submesh range exceeds the UInt16 fields available in a v256 skin section.");
            if ((long)r.VertexStart + r.VertexCount > n)
                throw new ArgumentException(
                    $"Submesh vertex span [{r.VertexStart},{(long)r.VertexStart + r.VertexCount}) exceeds vertex count {n}.");
            if ((long)r.IndexStart + r.IndexCount > t3)
                throw new ArgumentException(
                    $"Submesh index span [{r.IndexStart},{(long)r.IndexStart + r.IndexCount}) exceeds index count {t3}.");
            if (r.IndexStart % 3 != 0 || r.IndexCount % 3 != 0)
                throw new ArgumentException(
                    $"Submesh index span start/count ({r.IndexStart}/{r.IndexCount}) is not triangle-aligned.");
        }

        for (int pi = 0; pi < nBatch; pi++)
        {
            if (passes[pi].ColorIndex < -1)
                throw new ArgumentException(
                    $"Pass {pi} has invalid color index {passes[pi].ColorIndex}; only -1 denotes no color track.");
            if ((passes[pi].ColorIndex >= 0) != (passes[pi].RestColor is not null))
                throw new ArgumentException(
                    $"Pass {pi} color provenance/sample mismatch; referenced colors must carry a validated rest sample.");
            int submeshSlot = passes[pi].SubmeshSlot;
            if (submeshSlot < 0 || submeshSlot >= nSub || submeshSlot > ushort.MaxValue)
                throw new ArgumentException($"Pass {pi} references invalid submesh slot {submeshSlot} (count {nSub}).");
            if (passBindings[pi].Count > ushort.MaxValue)
                throw new ArgumentException(
                    $"Pass {pi} texture-unit count {passBindings[pi].Count} exceeds UInt16 ({ushort.MaxValue}).");
            foreach (var binding in passBindings[pi])
            {
                if (binding.TextureSlot < 0 || binding.TextureSlot >= texCount || binding.TextureSlot > ushort.MaxValue)
                    throw new ArgumentException(
                        $"Pass {pi} references invalid texture slot {binding.TextureSlot} (count {texCount}).");
                if ((binding.TextureTransform != ushort.MaxValue) != (binding.RestTransform is not null))
                    throw new ArgumentException(
                        $"Pass {pi} texture transform provenance/sample mismatch; referenced transforms must carry a validated rest sample.");
            }
        }

        foreach (var color in restColors)
            if (!IsFinite(color.Rgb) || !float.IsFinite(color.Alpha))
                throw new ArgumentException("A rest color contains non-finite values.");
        foreach (var transform in restTransforms)
            if (!IsFinite(transform.Translation) || !IsFinite(transform.Rotation) ||
                !IsFinite(transform.Scale) || transform.Rotation.LengthSquared() < 1e-10f)
                throw new ArgumentException("A rest UV transform contains non-finite or invalid values.");

        // Preserve the donor's global-loop indices as an unchanged prefix. Imported identities are
        // source-index + duration pairs because distinct source globals may coincidentally share a
        // duration. They are appended in transform/component first-use order and remapped below.
        uint donorGlobalCountRaw = BinaryPrimitives.ReadUInt32LittleEndian(donor.AsSpan(0x14, 4));
        uint donorGlobalOffset = BinaryPrimitives.ReadUInt32LittleEndian(donor.AsSpan(0x18, 4));
        if (donorGlobalCountRaw > int.MaxValue ||
            donorGlobalCountRaw > 0 &&
            (donorGlobalOffset == 0 || donorGlobalOffset + (long)donorGlobalCountRaw * 4 > donor.Length))
            throw new InvalidOperationException("Donor global-loop array is out of bounds.");
        var donorGlobalDurations = new uint[checked((int)donorGlobalCountRaw)];
        for (int i = 0; i < donorGlobalDurations.Length; i++)
            donorGlobalDurations[i] = BinaryPrimitives.ReadUInt32LittleEndian(
                donor.AsSpan(checked((int)donorGlobalOffset + i * 4), 4));

        var importedGlobalIdentities = new List<(int SourceGlobalSequence, uint DurationMs)>();
        var importedGlobalIndex = new Dictionary<(int SourceGlobalSequence, uint DurationMs), int>();
        void RegisterGlobalIdentity(int sourceGlobalSequence, uint durationMs)
        {
            var identity = (sourceGlobalSequence, durationMs);
            if (importedGlobalIndex.ContainsKey(identity)) return;
            importedGlobalIndex[identity] = importedGlobalIdentities.Count;
            importedGlobalIdentities.Add(identity);
        }

        for (int i = 0; i < transformPlans.Count; i++)
        {
            var transform = transformPlans[i].Payload;
            ValidateGlobalVectorTrack(transform.TranslationAnimation,
                $"Source UV transform {transformPlans[i].SourceIndex} translation");
            ValidateGlobalQuaternionTrack(transform.RotationAnimation,
                $"Source UV transform {transformPlans[i].SourceIndex} rotation");
            ValidateGlobalVectorTrack(transform.ScaleAnimation,
                $"Source UV transform {transformPlans[i].SourceIndex} scale");

            if (transform.TranslationAnimation is { } translation)
                RegisterGlobalIdentity(translation.SourceGlobalSequence, translation.DurationMs);
            if (transform.RotationAnimation is { } rotation)
                RegisterGlobalIdentity(rotation.SourceGlobalSequence, rotation.DurationMs);
            if (transform.ScaleAnimation is { } scale)
                RegisterGlobalIdentity(scale.SourceGlobalSequence, scale.DurationMs);
        }
        if ((long)donorGlobalDurations.Length + importedGlobalIdentities.Count > short.MaxValue + 1L)
            throw new InvalidOperationException(
                "Combined donor/imported global-loop count exceeds the signed Int16 track index address space.");

        // Donor templates whose byte contents we reuse verbatim so their index references into the
        // donor's preserved tables stay valid. Measured donor evidence (InspectWeapon, 2026-08-18):
        // the four donor views are NOT interchangeable — each view's submesh carries a DIFFERENT
        // boneComboIndex (0/1/2/3 into the 4-entry bone lookup table) and each view header ends in a
        // DIFFERENT trailing dword (256/75/53/21). So each generated view copies its OWN donor
        // view's submesh template and trailing dword, mirroring the donor slot-for-slot.
        var v0 = doc.Views[0];
        byte[] batchTemplate = SliceOr(donor, (int)v0.Batches.Offset, 24, v0.Batches.Count > 0);
        var submeshTemplates = new byte[viewCount][];
        var viewTrailing = new uint[viewCount];
        for (int i = 0; i < viewCount; i++)
        {
            var dv = doc.Views[Math.Min(i, doc.Views.Count - 1)];
            submeshTemplates[i] = SliceOr(donor, (int)dv.Submeshes.Offset, 32, dv.Submeshes.Count > 0);
            viewTrailing[i] = dv.Lod;
        }

        // ── Layout: append after the donor, everything 4-aligned. ──────────────────────────────
        int cursor = Align4(donor.Length);
        int voff = cursor; cursor += n * 48;
        cursor = Align4(cursor);
        int hoff = cursor; cursor += viewCount * 44;

        var lookupOff = new int[viewCount];
        var triOff = new int[viewCount];
        var propOff = new int[viewCount];
        var subOff = new int[viewCount];
        var batchOff = new int[viewCount];
        for (int i = 0; i < viewCount; i++)
        {
            cursor = Align4(cursor); lookupOff[i] = cursor; cursor += n * 2;
            cursor = Align4(cursor); triOff[i] = cursor; cursor += t3 * 2;
            cursor = Align4(cursor); propOff[i] = cursor; cursor += n * 4;
            cursor = Align4(cursor); subOff[i] = cursor; cursor += nSub * 32;
            cursor = Align4(cursor); batchOff[i] = cursor; cursor += nBatch * 24;
        }

        // Multi-pass material tables. Combo arrays are flattened per pass so +16/+18/+20/+22 in
        // every batch are parallel starts and TextureCount units resolve contiguously.
        int texTableOff = 0, texLookupOff = 0, coordLookupOff = 0, weightLookupOff = 0,
            transformLookupOff = 0, rfTableOff = 0, alphaTrackOff = 0, alphaRangeOff = 0,
            alphaTimeOff = 0, alphaKeysOff = 0, colorTableOff = 0, colorRgbKeysOff = 0,
            colorAlphaKeysOff = 0, transformTableOff = 0, globalLoopsOff = 0;
        int alphaRangeCount = 0;
        var transformTranslationTimeOff = new int[restTransforms.Count];
        var transformTranslationKeysOff = new int[restTransforms.Count];
        var transformRotationTimeOff = new int[restTransforms.Count];
        var transformRotationKeysOff = new int[restTransforms.Count];
        var transformScaleTimeOff = new int[restTransforms.Count];
        var transformScaleKeysOff = new int[restTransforms.Count];
        var texNameOffs = new int[texCount];   // string offset per effect texture (0 = none)
        var texNameBytes = new byte[texCount][];
        var rfEntries = new List<(ushort Flags, ushort Blend)>();
        var comboStarts = new int[nBatch];
        var textureCombos = new List<ushort>();
        var coordCombos = new List<ushort>();
        var weightCombos = new List<ushort>();
        var transformCombos = new List<ushort>();
        var alphaKeys = new List<short>();
        if (multiPass)
        {
            for (int s = 1; s < texCount; s++)
            {
                texNameBytes[s] = Encoding.ASCII.GetBytes(effectTexturePaths![s - 1] + "\0");
                cursor = Align4(cursor); texNameOffs[s] = cursor; cursor += texNameBytes[s].Length;
            }
            cursor = Align4(cursor); texTableOff = cursor; cursor += texCount * 16;

            for (int pi = 0; pi < nBatch; pi++)
            {
                comboStarts[pi] = textureCombos.Count;
                foreach (var binding in passBindings[pi])
                {
                    textureCombos.Add((ushort)binding.TextureSlot);
                    coordCombos.Add(binding.TextureCoordinate);

                    short alpha = (short)Math.Clamp(
                        (int)MathF.Round(Math.Clamp(binding.StaticAlpha, 0f, 1f) * 32767f), 0, 32767);
                    int alphaIndex = alphaKeys.IndexOf(alpha);
                    if (alphaIndex < 0) { alphaIndex = alphaKeys.Count; alphaKeys.Add(alpha); }
                    weightCombos.Add((ushort)alphaIndex);

                    transformCombos.Add(binding.RestTransform is null
                        ? ushort.MaxValue
                        : checked((ushort)transformPlanIndex[binding.TextureTransform]));
                }
            }
            if (textureCombos.Count > ushort.MaxValue)
                throw new InvalidOperationException("Texture combo count exceeds the v256 UInt16 batch address space.");

            cursor = Align4(cursor); texLookupOff = cursor; cursor += textureCombos.Count * 2;
            cursor = Align4(cursor); coordLookupOff = cursor; cursor += coordCombos.Count * 2;
            cursor = Align4(cursor); weightLookupOff = cursor; cursor += weightCombos.Count * 2;
            cursor = Align4(cursor); transformLookupOff = cursor; cursor += transformCombos.Count * 2;

            // One constant vanilla AnimationBlockM2 per distinct static alpha. This freezes the
            // source's rest-pose weight without retaining pointers into the TBC file.
            alphaRangeCount = checked((int)Math.Max(1u, doc.FindArray("sequences")?.Count ?? 0));
            cursor = Align4(cursor); alphaTrackOff = cursor; cursor += alphaKeys.Count * 28;
            cursor = Align4(cursor); alphaRangeOff = cursor; cursor += alphaRangeCount * 8;
            cursor = Align4(cursor); alphaTimeOff = cursor; cursor += 4;  // timestamp 0, shared
            cursor = Align4(cursor); alphaKeysOff = cursor; cursor += alphaKeys.Count * 2;

            // Color records contain RGB + fixed16 alpha tracks. Static UV components retain the
            // same constant [0,0] sequence ranges/timestamp; optional imported global components
            // own their range-free timestamp and key arrays.
            cursor = Align4(cursor); colorTableOff = cursor; cursor += restColors.Count * 56;
            cursor = Align4(cursor); colorRgbKeysOff = cursor; cursor += restColors.Count * 12;
            cursor = Align4(cursor); colorAlphaKeysOff = cursor; cursor += restColors.Count * 2;
            cursor = Align4(cursor); transformTableOff = cursor; cursor += restTransforms.Count * 84;

            void AllocateTransformTrack(bool animated, int keyCount, int keyStride,
                out int timeOffset, out int keyOffset)
            {
                if (animated)
                {
                    cursor = Align4(cursor); timeOffset = cursor;
                    cursor = checked(cursor + checked(keyCount * 4));
                }
                else
                {
                    timeOffset = alphaTimeOff;
                    keyCount = 1;
                }
                cursor = Align4(cursor); keyOffset = cursor;
                cursor = checked(cursor + checked(keyCount * keyStride));
            }

            for (int i = 0; i < restTransforms.Count; i++)
            {
                var transform = restTransforms[i];
                AllocateTransformTrack(transform.TranslationAnimation is not null,
                    transform.TranslationAnimation?.Keys.Count ?? 1, 12,
                    out transformTranslationTimeOff[i], out transformTranslationKeysOff[i]);
                AllocateTransformTrack(transform.RotationAnimation is not null,
                    transform.RotationAnimation?.Keys.Count ?? 1, 16,
                    out transformRotationTimeOff[i], out transformRotationKeysOff[i]);
                AllocateTransformTrack(transform.ScaleAnimation is not null,
                    transform.ScaleAnimation?.Keys.Count ?? 1, 12,
                    out transformScaleTimeOff[i], out transformScaleKeysOff[i]);
            }

            if (importedGlobalIdentities.Count > 0)
            {
                cursor = Align4(cursor); globalLoopsOff = cursor;
                cursor = checked(cursor +
                    checked((donorGlobalDurations.Length + importedGlobalIdentities.Count) * 4));
            }

            foreach (var p in passes)
                if (!rfEntries.Contains((p.RenderFlags, p.BlendMode)))
                    rfEntries.Add((p.RenderFlags, p.BlendMode));
            cursor = Align4(cursor); rfTableOff = cursor; cursor += rfEntries.Count * 4;
        }

        int total = Align4(cursor);
        var outp = new byte[total];
        Array.Copy(donor, outp, donor.Length);

        // Vertices.
        var min = new Vector3(float.MaxValue); var max = new Vector3(float.MinValue);
        for (int i = 0; i < n; i++)
        {
            int o = voff + i * 48;
            WriteVec3(outp, o + 0, posWoW[i]);
            outp[o + 12] = 255; // weights (255,0,0,0)
            // bones (o+16..19) already zero
            WriteVec3(outp, o + 20, CoordinateContract.Normalize(normalWoW[i]));
            WriteF(outp, o + 32, uv0[i].X); WriteF(outp, o + 36, uv0[i].Y);
            if (mesh.Uv1 is { Length: > 0 } uv1 && i < uv1.Length)
            {
                WriteF(outp, o + 40, uv1[i].X);
                WriteF(outp, o + 44, uv1[i].Y);
            }
            min = Vector3.Min(min, posWoW[i]); max = Vector3.Max(max, posWoW[i]);
        }
        float radius = 0f;
        var center = (min + max) * 0.5f;
        for (int i = 0; i < n; i++) radius = MathF.Max(radius, Vector3.Distance(center, posWoW[i]));

        // Per-submesh centers (raw WoW space) for the submesh records' culling fields.
        var subCenters = new Vector3[nSub];
        for (int si = 0; si < nSub; si++)
        {
            var r = ranges[si];
            var acc = Vector3.Zero;
            int cnt = Math.Max(1, r.VertexCount);
            for (int k = 0; k < r.VertexCount && r.VertexStart + k < n; k++)
                acc += posWoW[r.VertexStart + k];
            subCenters[si] = acc / cnt;
        }

        // Four equivalent views.
        for (int vi = 0; vi < viewCount; vi++)
        {
            // vertexLookup = identity [0..n-1]; triangles reference it directly.
            for (int k = 0; k < n; k++) U16(outp, lookupOff[vi] + k * 2, (ushort)k);
            for (int k = 0; k < t3; k++) U16(outp, triOff[vi] + k * 2, (ushort)indices[k]);
            // properties: per-vertex 4-byte bone-index quads, all bone 0 (rigid). Left as zeros.

            // Submeshes: copy THIS view's donor template (preserves its per-view boneComboIndex),
            // override ranges + center per submesh.
            for (int si = 0; si < nSub; si++)
            {
                int s = subOff[vi] + si * 32;
                Array.Copy(submeshTemplates[vi], 0, outp, s, 32);
                var r = ranges[si];
                U16(outp, s + 4, (ushort)r.VertexStart);   // vertexStart
                U16(outp, s + 6, (ushort)r.VertexCount);   // vertexCount
                U16(outp, s + 8, (ushort)r.IndexStart);    // indexStart
                U16(outp, s + 10, (ushort)r.IndexCount);   // indexCount
                WriteVec3(outp, s + 20, subCenters[si]);
            }

            // Batches. Single-pass keeps the proven donor record. Multi-pass authors all 24 bytes
            // from the TBC render IR and points every dependent index at the appended tables.
            for (int bi = 0; bi < nBatch; bi++)
            {
                int t = batchOff[vi] + bi * 24;
                Array.Copy(batchTemplate, 0, outp, t, 24);
                var p = passes[bi];
                U16(outp, t + 4, (ushort)p.SubmeshSlot);   // submesh index
                U16(outp, t + 6, (ushort)p.SubmeshSlot);   // geoset index (mirrors submesh in stock weapons)
                if (multiPass)
                {
                    outp[t + 0] = p.BatchFlags;
                    outp[t + 1] = unchecked((byte)p.PriorityPlane);
                    U16(outp, t + 2, p.ShaderId);
                    int rfIdx = rfEntries.IndexOf((p.RenderFlags, p.BlendMode));
                    I16(outp, t + 8, p.RestColor is null
                        ? (short)-1
                        : checked((short)restColors.IndexOf(p.RestColor)));
                    U16(outp, t + 10, (ushort)rfIdx);              // render-flag table
                    U16(outp, t + 12, checked((ushort)p.Layer));   // material layer
                    U16(outp, t + 14, checked((ushort)passBindings[bi].Count));
                    U16(outp, t + 16, (ushort)comboStarts[bi]);    // texture combo start
                    U16(outp, t + 18, (ushort)comboStarts[bi]);    // texture-coordinate combo start
                    U16(outp, t + 20, (ushort)comboStarts[bi]);    // texture-weight combo start
                    U16(outp, t + 22, (ushort)comboStarts[bi]);    // texture-transform combo start
                }
            }

            // view header (44 bytes). Trailing dword mirrors the donor's own per-view value.
            int h = hoff + vi * 44;
            U32(outp, h + 0, (uint)n); U32(outp, h + 4, (uint)lookupOff[vi]);      // vertexLookup
            U32(outp, h + 8, (uint)t3); U32(outp, h + 12, (uint)triOff[vi]);        // triangles
            U32(outp, h + 16, (uint)n); U32(outp, h + 20, (uint)propOff[vi]);       // properties
            U32(outp, h + 24, (uint)nSub); U32(outp, h + 28, (uint)subOff[vi]);     // submeshes
            U32(outp, h + 32, (uint)nBatch); U32(outp, h + 36, (uint)batchOff[vi]); // batches
            U32(outp, h + 40, viewTrailing[vi]);
        }

        // Multi-pass appended tables + header repoints.
        if (multiPass)
        {
            // Textures: slot 0 = Type-2 (DBC-driven), slots 1.. = Type-0 hardcoded members. TBC
            // wrap/clamp flags are retained per slot instead of copied from one donor texture.
            uint donorTexFlags = 0;
            uint nDonorTex = BinaryPrimitives.ReadUInt32LittleEndian(donor.AsSpan(0x5C, 4));
            uint ofsDonorTex = BinaryPrimitives.ReadUInt32LittleEndian(donor.AsSpan(0x60, 4));
            if (nDonorTex > 0 && ofsDonorTex + 16 <= donor.Length)
                donorTexFlags = BinaryPrimitives.ReadUInt32LittleEndian(donor.AsSpan((int)ofsDonorTex + 4, 4));

            for (int s = 0; s < texCount; s++)
            {
                int o = texTableOff + s * 16;
                U32(outp, o + 0, s == 0 ? 2u : 0u);          // type
                uint textureFlags = mesh.TextureSlots is { } slots && s < slots.Count
                    ? slots[s].Flags
                    : donorTexFlags;
                U32(outp, o + 4, textureFlags);              // flags (wrap bits)
                if (s > 0)
                {
                    Array.Copy(texNameBytes[s], 0, outp, texNameOffs[s], texNameBytes[s].Length);
                    U32(outp, o + 8, (uint)texNameBytes[s].Length);   // filename length (incl. NUL)
                    U32(outp, o + 12, (uint)texNameOffs[s]);          // filename offset
                }
            }
            U32(outp, 0x5C, (uint)texCount); U32(outp, 0x60, (uint)texTableOff);

            for (int i = 0; i < textureCombos.Count; i++)
            {
                U16(outp, texLookupOff + i * 2, textureCombos[i]);
                U16(outp, coordLookupOff + i * 2, coordCombos[i]);
                U16(outp, weightLookupOff + i * 2, weightCombos[i]);
                U16(outp, transformLookupOff + i * 2, transformCombos[i]);
            }
            U32(outp, 0x94, (uint)textureCombos.Count); U32(outp, 0x98, (uint)texLookupOff);
            U32(outp, 0x9C, (uint)coordCombos.Count); U32(outp, 0xA0, (uint)coordLookupOff);
            U32(outp, 0xA4, (uint)weightCombos.Count); U32(outp, 0xA8, (uint)weightLookupOff);
            U32(outp, 0xAC, (uint)transformCombos.Count); U32(outp, 0xB0, (uint)transformLookupOff);

            // Constant rest-pose transparency tracks.
            for (int i = 0; i < alphaRangeCount; i++)
            {
                U32(outp, alphaRangeOff + i * 8 + 0, 0);
                U32(outp, alphaRangeOff + i * 8 + 4, 0);
            }
            U32(outp, alphaTimeOff, 0);
            for (int i = 0; i < alphaKeys.Count; i++)
            {
                int track = alphaTrackOff + i * 28;
                U16(outp, track + 0, 0);            // interpolation: none
                U16(outp, track + 2, ushort.MaxValue); // no global sequence
                U32(outp, track + 4, (uint)alphaRangeCount); U32(outp, track + 8, (uint)alphaRangeOff);
                U32(outp, track + 12, 1); U32(outp, track + 16, (uint)alphaTimeOff);
                U32(outp, track + 20, 1); U32(outp, track + 24, (uint)(alphaKeysOff + i * 2));
                I16(outp, alphaKeysOff + i * 2, alphaKeys[i]);
            }
            U32(outp, 0x64, (uint)alphaKeys.Count); U32(outp, 0x68, (uint)alphaTrackOff);

            // Constant rest-pose material colors. Alpha uses the native signed fixed16 encoding.
            for (int i = 0; i < restColors.Count; i++)
            {
                var color = restColors[i];
                int record = colorTableOff + i * 56;
                WriteConstantTrack(outp, record, alphaRangeCount, alphaRangeOff, alphaTimeOff,
                    colorRgbKeysOff + i * 12);
                WriteConstantTrack(outp, record + 28, alphaRangeCount, alphaRangeOff, alphaTimeOff,
                    colorAlphaKeysOff + i * 2);
                WriteVec3(outp, colorRgbKeysOff + i * 12, color.Rgb);
                I16(outp, colorAlphaKeysOff + i * 2, (short)Math.Clamp(
                    (int)MathF.Round(Math.Clamp(color.Alpha, 0f, 1f) * 32767f), 0, 32767));
            }
            U32(outp, 0x54, (uint)restColors.Count);
            U32(outp, 0x58, restColors.Count == 0 ? 0u : (uint)colorTableOff);

            // Texture transforms. Static components retain deterministic rest keys; supported
            // range-free global step/linear components retain all timestamps and keys. v256 uses
            // float4 quaternion keys, so decoded TBC compact quaternions are written as floats.
            int RemappedGlobalSequence(int sourceGlobalSequence, uint durationMs)
            {
                int imported = importedGlobalIndex[(sourceGlobalSequence, durationMs)];
                return checked(donorGlobalDurations.Length + imported);
            }

            for (int i = 0; i < restTransforms.Count; i++)
            {
                var transform = restTransforms[i];
                int record = transformTableOff + i * 84;
                if (transform.TranslationAnimation is { } translation)
                    WriteGlobalVectorTrack(outp, record, translation,
                        RemappedGlobalSequence(
                            translation.SourceGlobalSequence, translation.DurationMs),
                        transformTranslationTimeOff[i], transformTranslationKeysOff[i]);
                else
                {
                    WriteConstantTrack(outp, record, alphaRangeCount, alphaRangeOff,
                        transformTranslationTimeOff[i], transformTranslationKeysOff[i]);
                    WriteVec3(outp, transformTranslationKeysOff[i], transform.Translation);
                }

                if (transform.RotationAnimation is { } rotationAnimation)
                    WriteGlobalQuaternionTrack(outp, record + 28, rotationAnimation,
                        RemappedGlobalSequence(
                            rotationAnimation.SourceGlobalSequence, rotationAnimation.DurationMs),
                        transformRotationTimeOff[i], transformRotationKeysOff[i]);
                else
                {
                    WriteConstantTrack(outp, record + 28, alphaRangeCount, alphaRangeOff,
                        transformRotationTimeOff[i], transformRotationKeysOff[i]);
                    WriteQuaternion(outp, transformRotationKeysOff[i],
                        Quaternion.Normalize(transform.Rotation));
                }

                if (transform.ScaleAnimation is { } scale)
                    WriteGlobalVectorTrack(outp, record + 56, scale,
                        RemappedGlobalSequence(scale.SourceGlobalSequence, scale.DurationMs),
                        transformScaleTimeOff[i], transformScaleKeysOff[i]);
                else
                {
                    WriteConstantTrack(outp, record + 56, alphaRangeCount, alphaRangeOff,
                        transformScaleTimeOff[i], transformScaleKeysOff[i]);
                    WriteVec3(outp, transformScaleKeysOff[i], transform.Scale);
                }
            }
            U32(outp, 0x74, (uint)restTransforms.Count);
            U32(outp, 0x78, restTransforms.Count == 0 ? 0u : (uint)transformTableOff);

            // Existing donor globals remain byte-value-identical at the same indices; imported
            // identities follow them. Repoint only when at least one imported track needs a loop.
            if (importedGlobalIdentities.Count > 0)
            {
                for (int i = 0; i < donorGlobalDurations.Length; i++)
                    U32(outp, globalLoopsOff + i * 4, donorGlobalDurations[i]);
                for (int i = 0; i < importedGlobalIdentities.Count; i++)
                    U32(outp, globalLoopsOff + (donorGlobalDurations.Length + i) * 4,
                        importedGlobalIdentities[i].DurationMs);
                U32(outp, 0x14,
                    checked((uint)(donorGlobalDurations.Length + importedGlobalIdentities.Count)));
                U32(outp, 0x18, (uint)globalLoopsOff);
            }

            // Render flags: distinct (flags, blend) pairs carried verbatim from the source passes.
            for (int i = 0; i < rfEntries.Count; i++)
            {
                U16(outp, rfTableOff + i * 4 + 0, rfEntries[i].Flags);
                U16(outp, rfTableOff + i * 4 + 2, rfEntries[i].Blend);
            }
            U32(outp, 0x84, (uint)rfEntries.Count); U32(outp, 0x88, (uint)rfTableOff);
        }

        // Repoint header: vertices + views.
        U32(outp, 0x44, (uint)n); U32(outp, 0x48, (uint)voff);
        U32(outp, 0x4C, (uint)viewCount); U32(outp, 0x50, (uint)hoff);

        // Vertex bounding box + radius only (0xB4). The collision box at 0xD0 is deliberately LEFT
        // AS THE DONOR'S (all zeros for simple swords, which ship no collision geometry) — a nonzero
        // collision sphere with zero collision triangles is a state no stock weapon exhibits.
        WriteVec3(outp, 0x0B4, min); WriteVec3(outp, 0x0C0, max); WriteF(outp, 0x0CC, radius);

        // Single-pass material carry-over: the copied batch references the donor's render-flag
        // record (index at batch +10). That record is 4 fixed-width bytes inside the preserved donor
        // region, and only OUR views' batch points at it — patching in place is offset-preserving.
        // Default values are explicit too: an opaque, single-sided source must clear an
        // alpha/two-sided donor. A null material intentionally preserves geometry-only callers.
        if (!multiPass && material is not null)
        {
            ushort rfIndex = BinaryPrimitives.ReadUInt16LittleEndian(batchTemplate.AsSpan(10, 2));
            uint nRenderFlags = BinaryPrimitives.ReadUInt32LittleEndian(outp.AsSpan(0x84, 4));
            uint ofsRenderFlags = BinaryPrimitives.ReadUInt32LittleEndian(outp.AsSpan(0x88, 4));
            if (rfIndex < nRenderFlags && ofsRenderFlags + (rfIndex + 1L) * 4 <= outp.Length)
            {
                int rf = (int)(ofsRenderFlags + rfIndex * 4u);
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(outp.AsSpan(rf, 2));
                U16(outp, rf, (ushort)((flags & ~0x04) | (material.TwoSided ? 0x04 : 0)));
                U16(outp, rf + 2, (ushort)material.BlendMode);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Donor render-flag index {rfIndex} unresolvable (n={nRenderFlags}); cannot apply the explicit material.");
            }
        }

        return outp;
    }

    private static byte[] SliceOr(byte[] src, int off, int len, bool present)
    {
        var b = new byte[len];
        if (present && off > 0 && off + len <= src.Length) Array.Copy(src, off, b, 0, len);
        return b;
    }

    private static int Align4(int x) => (x + 3) & ~3;
    private static bool IsFinite(Vector3 v) =>
        float.IsFinite(v.X) && float.IsFinite(v.Y) && float.IsFinite(v.Z);
    private static bool IsFinite(Quaternion q) =>
        float.IsFinite(q.X) && float.IsFinite(q.Y) && float.IsFinite(q.Z) && float.IsFinite(q.W);

    private static bool SameTransformPayload(
        WeaponRestTextureTransform left,
        WeaponRestTextureTransform right) =>
        left.Translation == right.Translation &&
        left.Rotation == right.Rotation &&
        left.Scale == right.Scale &&
        left.AnimationFrozen == right.AnimationFrozen &&
        SameGlobalVectorTrack(left.TranslationAnimation, right.TranslationAnimation) &&
        SameGlobalQuaternionTrack(left.RotationAnimation, right.RotationAnimation) &&
        SameGlobalVectorTrack(left.ScaleAnimation, right.ScaleAnimation);

    private static bool SameGlobalVectorTrack(
        WeaponGlobalVectorTrack? left,
        WeaponGlobalVectorTrack? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return left.Interpolation == right.Interpolation &&
               left.SourceGlobalSequence == right.SourceGlobalSequence &&
               left.DurationMs == right.DurationMs &&
               left.Timestamps.SequenceEqual(right.Timestamps) &&
               left.Keys.SequenceEqual(right.Keys);
    }

    private static bool SameGlobalQuaternionTrack(
        WeaponGlobalQuaternionTrack? left,
        WeaponGlobalQuaternionTrack? right)
    {
        if (ReferenceEquals(left, right)) return true;
        if (left is null || right is null) return false;
        return left.Interpolation == right.Interpolation &&
               left.SourceGlobalSequence == right.SourceGlobalSequence &&
               left.DurationMs == right.DurationMs &&
               left.Timestamps.SequenceEqual(right.Timestamps) &&
               left.Keys.SequenceEqual(right.Keys);
    }

    private static void ValidateGlobalVectorTrack(WeaponGlobalVectorTrack? track, string label)
    {
        if (track is null) return;
        ValidateGlobalTrackHeader(track.Interpolation, track.SourceGlobalSequence,
            track.DurationMs, track.Timestamps, track.Keys.Count, label);
        for (int i = 0; i < track.Keys.Count; i++)
            if (!IsFinite(track.Keys[i]))
                throw new ArgumentException($"{label} key {i} contains a non-finite vector.");
    }

    private static void ValidateGlobalQuaternionTrack(
        WeaponGlobalQuaternionTrack? track,
        string label)
    {
        if (track is null) return;
        ValidateGlobalTrackHeader(track.Interpolation, track.SourceGlobalSequence,
            track.DurationMs, track.Timestamps, track.Keys.Count, label);
        for (int i = 0; i < track.Keys.Count; i++)
            if (!IsFinite(track.Keys[i]) || track.Keys[i].LengthSquared() < 1e-10f)
                throw new ArgumentException($"{label} key {i} contains a non-finite or zero-length quaternion.");
    }

    private static void ValidateGlobalTrackHeader(
        ushort interpolation,
        int sourceGlobalSequence,
        uint durationMs,
        IReadOnlyList<uint> timestamps,
        int keyCount,
        string label)
    {
        if (interpolation > 1)
            throw new ArgumentException(
                $"{label} interpolation {interpolation} is unsupported; only step/linear (0/1) can be written.");
        if (sourceGlobalSequence < 0)
            throw new ArgumentException($"{label} has invalid source global sequence {sourceGlobalSequence}.");
        if (durationMs == 0)
            throw new ArgumentException($"{label} has zero global-loop duration.");
        if (timestamps is null)
            throw new ArgumentException($"{label} has a null timestamp collection.");
        if (timestamps.Count == 0 || keyCount == 0 || timestamps.Count != keyCount)
            throw new ArgumentException(
                $"{label} timestamp/key counts must be equal and nonzero ({timestamps.Count}/{keyCount}).");
        uint previous = 0;
        for (int i = 0; i < timestamps.Count; i++)
        {
            uint timestamp = timestamps[i];
            if (i > 0 && timestamp <= previous)
                throw new ArgumentException(
                    $"{label} timestamps are not strictly increasing at key {i} ({previous}, {timestamp}).");
            if (timestamp > durationMs)
                throw new ArgumentException(
                    $"{label} timestamp {timestamp} at key {i} exceeds duration {durationMs}.");
            previous = timestamp;
        }
    }

    private static void WriteConstantTrack(byte[] b, int track, int rangeCount, int rangeOffset,
        int timeOffset, int keyOffset)
    {
        U16(b, track, 0);
        U16(b, track + 2, ushort.MaxValue);
        U32(b, track + 4, (uint)rangeCount); U32(b, track + 8, (uint)rangeOffset);
        U32(b, track + 12, 1); U32(b, track + 16, (uint)timeOffset);
        U32(b, track + 20, 1); U32(b, track + 24, (uint)keyOffset);
    }

    private static void WriteGlobalVectorTrack(
        byte[] b,
        int trackOffset,
        WeaponGlobalVectorTrack track,
        int outputGlobalSequence,
        int timeOffset,
        int keyOffset)
    {
        WriteGlobalTrackHeader(b, trackOffset, track.Interpolation, outputGlobalSequence,
            track.Timestamps.Count, timeOffset, keyOffset);
        for (int i = 0; i < track.Timestamps.Count; i++)
        {
            U32(b, timeOffset + i * 4, track.Timestamps[i]);
            WriteVec3(b, keyOffset + i * 12, track.Keys[i]);
        }
    }

    private static void WriteGlobalQuaternionTrack(
        byte[] b,
        int trackOffset,
        WeaponGlobalQuaternionTrack track,
        int outputGlobalSequence,
        int timeOffset,
        int keyOffset)
    {
        WriteGlobalTrackHeader(b, trackOffset, track.Interpolation, outputGlobalSequence,
            track.Timestamps.Count, timeOffset, keyOffset);
        for (int i = 0; i < track.Timestamps.Count; i++)
        {
            U32(b, timeOffset + i * 4, track.Timestamps[i]);
            WriteQuaternion(b, keyOffset + i * 16, track.Keys[i]);
        }
    }

    private static void WriteGlobalTrackHeader(
        byte[] b,
        int trackOffset,
        ushort interpolation,
        int outputGlobalSequence,
        int count,
        int timeOffset,
        int keyOffset)
    {
        U16(b, trackOffset, interpolation);
        I16(b, trackOffset + 2, checked((short)outputGlobalSequence));
        U32(b, trackOffset + 4, 0); U32(b, trackOffset + 8, 0); // global: no sequence ranges
        U32(b, trackOffset + 12, checked((uint)count)); U32(b, trackOffset + 16, (uint)timeOffset);
        U32(b, trackOffset + 20, checked((uint)count)); U32(b, trackOffset + 24, (uint)keyOffset);
    }

    private static void U16(byte[] b, int o, ushort v) => BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(o, 2), v);
    private static void I16(byte[] b, int o, short v) => BinaryPrimitives.WriteInt16LittleEndian(b.AsSpan(o, 2), v);
    private static void U32(byte[] b, int o, uint v) => BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o, 4), v);
    private static void WriteF(byte[] b, int o, float f) => BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o, 4), f);
    private static void WriteVec3(byte[] b, int o, Vector3 v) { WriteF(b, o, v.X); WriteF(b, o + 4, v.Y); WriteF(b, o + 8, v.Z); }
    private static void WriteQuaternion(byte[] b, int o, Quaternion q)
    {
        WriteF(b, o, q.X); WriteF(b, o + 4, q.Y); WriteF(b, o + 8, q.Z); WriteF(b, o + 12, q.W);
    }
}

using System.Numerics;

namespace MangosSuperUI.Services.M2Fx;

/// <summary>
/// Reads an M2's material animation tracks in full, for preview only.
///
/// === Why this is separate from M2Reader ===
///
/// <c>M2Reader</c> deliberately samples every colour / alpha / texture-weight / UV track down to one
/// rest value (<c>TryReadRestTrack</c>) and its stricter sibling <c>TryReadSupportedGlobalAnimation</c>
/// fails CLOSED on anything the vanilla M2 writer could not faithfully re-emit — a per-sequence
/// track, a hermite curve, a track with ranges. That policy is right for the forge: an import that
/// cannot round-trip a track must refuse it, and those failures are surfaced as import-fatal errors
/// through <c>RestColorErrors</c> / <c>TransparencyStaticAlphaErrors</c>.
///
/// A previewer wants the opposite policy. It is not re-emitting anything; it just has to move. A
/// hermite pulse it can only approximate linearly still reads as a pulse, and refusing it leaves the
/// user looking at a dead object. Sharing one extractor between the two would mean either weakening
/// the writer's guarantees or keeping the previewer blind, so this is a second, permissive reader
/// that touches nothing the forge depends on: it takes raw bytes and an already-parsed
/// <see cref="M2Model"/>, returns a manifest, and records no errors anywhere.
///
/// === Layouts (v256, all measured against the 1.12 client) ===
///
///   colors        header 0x054, 56-byte records: M2Track&lt;C3Vector&gt; rgb at +0 (stride 12),
///                 M2Track&lt;fixed16&gt; alpha at +28 (stride 2)
///   transparency  header 0x064, 28-byte records: one M2Track&lt;fixed16&gt; (stride 2)
///   uvAnimations  header 0x074, 84-byte records: M2Track&lt;C3Vector&gt; translation at +0 (12),
///                 M2Track&lt;Quat&gt; rotation at +28 (16), M2Track&lt;C3Vector&gt; scale at +56 (12)
///   M2Track       28 bytes: u16 interpolation | i16 globalSequence | M2Array ranges (+4)
///                 | M2Array timestamps (+12) | M2Array values (+20)
///
/// These are the same offsets <c>M2BinaryValidator.ValidateMaterialTracks</c> asserts, which is the
/// independent second opinion on them.
/// </summary>
/// <summary>One primitive the writer actually emitted, with the glTF names it landed under and the
/// already-decoded animation for the (batch, texture unit) it came from.
///
/// The writer is the only component that knows which batches survived its own filtering, which
/// texture unit it sampled, and what it called the result. Handing that to the manifest builder
/// beats re-deriving it there: the old "first batch per submesh" reconstruction could only ever
/// describe the base layer, so every compositing overlay shipped without its animation.</summary>
public readonly record struct M2FxPass(string MeshName, string MaterialName, M2FxMesh Fx);

public static class M2FxReader
{
    private const int HdrColors = 0x054;
    private const int HdrTransparency = 0x064;
    private const int HdrUvAnimations = 0x074;
    private const int HdrParticles = 0x13C;

    // v256 M2Particle, 504 bytes. Every offset below is verified against the mounted 1.12 client
    // (see the probe dumps quoted in ARMOR_FORGE.md 8c); the ten float tracks in the middle are the
    // same TrackStarts M2EmitterTransplanter uses.
    private const int EmitterStride = 504;
    private const int EmPosition = 0x008;       // C3Vector, WoW space
    private const int EmTexture = 0x016;        // uint16, index into the M2 texture table
    private const int EmBlend = 0x028;          // uint16 (blendingType)
    private const int EmEmitterType = 0x029;    // uint8
    private const int EmTileRows = 0x030;       // uint16
    private const int EmTileCols = 0x032;       // uint16
    private const int EmMidpoint = 0x14C;       // float 0..1, where colour/scale key 1 lands
    private const int EmColorKeys = 0x150;      // 3 x CImVector (BGRA)
    private const int EmScaleKeys = 0x15C;      // 3 x float
    private const int EmHeadCells = 0x168;      // uint16[3] lifespanUVAnim {start, end, repeat}
    private const int EmTailCells = 0x16E;      // uint16[3] decayUVAnim

    /// <summary>The ten float tracks, by record offset, in the order M2FxEmitter wants them.</summary>
    private static readonly int[] EmitterTrackStarts = { 52, 80, 108, 136, 164, 192, 220, 248, 276, 304 };

    /// <summary>Cap per model. Vanilla item models run 1-5 emitters and the forge caps its own grafts
    /// at 5; a browser re-simulating a dozen at once is a frame-rate problem, not an effect.</summary>
    private const int MaxEmitters = 8;

    private const int ColorRecordStride = 56;
    private const int TransparencyRecordStride = 28;
    private const int UvRecordStride = 84;
    private const int TrackStride = 28;

    /// <summary>A track with more keys than this is not a material pulse, it is a data error; the
    /// manifest is JSON inside a GLB and must not be allowed to grow without bound.</summary>
    private const int MaxKeysPerTrack = 256;

    /// <summary>
    /// Build the manifest for a model whose meshes the caller names.
    /// </summary>
    /// <param name="m2Data">The raw M2 bytes the model was parsed from.</param>
    /// <param name="model">The parsed model — for the batch chain and the lookup tables.</param>
    /// <param name="emitted">Every primitive the writer actually put in the scene, with the glTF
    /// names it landed under. See <see cref="M2FxPass"/> — the writer is the only thing that knows
    /// which batches survived, which texture unit it sampled and what it named the result, so it
    /// hands that in rather than having this method re-derive it. The previous version reconstructed
    /// "first batch per submesh", which silently gave every overlay layer's animation away.</param>
    /// <param name="resolveTexture">M2 texture slot to the glTF texture index the writer embedded the
    /// sheet under, or null when it has no image for that slot. An emitter whose sheet is unavailable
    /// is dropped rather than drawn untextured, because an untextured additive quad is a white blob.</param>
    public static M2FxManifest Build(byte[]? m2Data, M2Model? model,
        IReadOnlyList<M2FxPass> emitted, Func<int, int?>? resolveTexture = null)
    {
        var meshes = new Dictionary<string, M2FxMesh>(StringComparer.Ordinal);
        var emitters = new List<M2FxEmitter>();
        var loops = model?.GlobalSequenceDurations ?? new List<uint>();

        foreach (var pass in emitted)
        {
            // TWO keys per primitive. GLTFLoader runs MESH names through createUniqueName (a second
            // mesh called Geoset3 becomes Geoset3_1) but copies MATERIAL names verbatim, and
            // m2fx.js accepts either: manifest.meshes[node.name] || manifest.meshes[mat.name].
            // Emitting both means a pass keeps its animation even if the loader renames its mesh.
            // First-wins is safe because the writer mints a UNIQUE material name for every pass that
            // carries an animation, precisely so these cannot collide.
            if (!string.IsNullOrEmpty(pass.MeshName)) meshes.TryAdd(pass.MeshName, pass.Fx);
            if (!string.IsNullOrEmpty(pass.MaterialName)) meshes.TryAdd(pass.MaterialName, pass.Fx);
        }

        if (m2Data is not null && m2Data.Length >= 0x148 && resolveTexture is not null)
            emitters.AddRange(ReadEmitters(m2Data, resolveTexture));

        return new M2FxManifest(loops, meshes, emitters);
    }

    /// <summary>Decode one emitted primitive's material animation, or null when it holds still.
    ///
    /// Exception-safe by contract: the writer calls this DURING geometry emission, outside the
    /// try/catch that wraps manifest construction, and a manifest must never fail a GLB. Returns
    /// null on the WotLK lane, where <see cref="M2Model.SourceBytes"/> is null by design.</summary>
    public static M2FxMesh? ReadPassFx(byte[]? m2Data, M2Model? model, M2Batch batch, int unit)
    {
        if (m2Data is null || m2Data.Length < 0x148 || model is null) return null;
        try { return ReadBatchFx(m2Data, model, batch, unit); }
        catch { return null; }
    }

    /// <summary>
    /// Build emitter records from an ALREADY-PARSED model — the WotLK (v264) lane, where
    /// <see cref="M2Model.SourceBytes"/> is deliberately null (M2WotlkReader's raw emitter/track reader
    /// is ≤ v263-only, so <see cref="Build"/>'s binary path cannot run). The emitters M2WotlkReader DID
    /// decode into <see cref="M2Model.ParticleEmitters"/> already carry everything the browser needs —
    /// reader-space position, blend mode, tile grid, the <c>Motion</c> timing (rate × lifespan is what
    /// makes fire read as a body rather than a strobe) and the colour ramp. Routing them into the
    /// manifest is what puts the flame back on WotLK armour (Worldbreaker's shoulders) in the preview,
    /// matching what the game client draws from the same model. The vanilla/TBC lane is untouched: it
    /// keeps SourceBytes and goes through the higher-fidelity binary reader above.
    /// </summary>
    /// <param name="resolveTexture">Emitter sheet FILENAME → the glTF texture index the writer embedded
    /// it under, or null when no sheet is available (an untextured additive quad is a white blob, so the
    /// emitter is dropped rather than drawn — same rule the binary path applies).</param>
    public static List<M2FxEmitter> BuildEmittersFromModel(M2Model model, Func<string?, int?> resolveTexture)
    {
        var result = new List<M2FxEmitter>();
        foreach (var e in model.ParticleEmitters)
        {
            if (e.Motion is not { IsUsable: true }) continue;   // no usable rate/lifespan → draws nothing
            if (!(e.Scale > 0.0005f)) continue;                 // zero size is invisible
            if (resolveTexture(e.TextureName) is not { } tex) continue;

            int cells = Math.Max(1, e.TileRows * e.TileCols);
            float[][] colors = e.ColorRamp is { } ramp
                ? new[] { Rgb01(ramp.Start), Rgb01(ramp.Mid), Rgb01(ramp.End) }
                : new[] { Rgb01OrWhite(e.ColorRgb), Rgb01OrWhite(e.ColorRgb), Rgb01OrWhite(e.ColorRgb) };

            result.Add(new M2FxEmitter(
                Position: new[] { e.Position.X, e.Position.Y, e.Position.Z },
                Texture: tex,
                BlendMode: e.BlendMode,
                EmitterType: 1,                                 // plane/point spawn (WotLK type not summarised)
                EmissionRate: e.Motion.EmissionRate,
                Lifespan: e.Motion.Lifespan,
                Speed: e.Motion.EmissionSpeed,
                SpeedVariation: e.Motion.SpeedVariation,
                VerticalRange: e.Motion.VerticalRange,
                HorizontalRange: e.Motion.HorizontalRange,
                Gravity: e.Motion.Gravity,
                AreaLength: e.Motion.EmissionAreaLength,
                AreaWidth: e.Motion.EmissionAreaWidth,
                ZSource: e.Motion.ZSource,
                Midpoint: 0.5f,
                ScaleRamp: new[] { e.Scale, e.Scale, e.Scale },
                ColorRamp: colors,
                // WotLK stores particle opacity as a SEPARATE track the reader doesn't carry, so this
                // is a default envelope, not the source curve. Fade IN (0 → 1 → 0): with additive
                // blending the colour ramp's white-hot BIRTH sample would otherwise slam in at full
                // strength and overlapping cores blow out to white (the "too white vs in game" report).
                // Fading in hides that instant and shows the orange body of the flame — the colour-ramp
                // mid — which is what reads as fire.
                AlphaRamp: new[] { 0f, 1f, 0f },
                HeadCells: new[] { 0, cells - 1, 1 },
                TailCells: new[] { 0, cells - 1, 1 },
                TileRows: e.TileRows,
                TileCols: e.TileCols));
        }
        return result;

        static float[] Rgb01(System.Numerics.Vector3 c) => new[] { c.X / 255f, c.Y / 255f, c.Z / 255f };
        static float[] Rgb01OrWhite(System.Numerics.Vector3? c) =>
            c is { } v ? new[] { v.X / 255f, v.Y / 255f, v.Z / 255f } : new[] { 1f, 1f, 1f };
    }

    /// <summary>Which M2 texture slots the emitters sample, so a writer knows which sheets to embed.
    /// Empty for the overwhelming majority of models, which carry no emitters at all.</summary>
    public static IReadOnlyList<int> EmitterTextureSlots(byte[]? m2Data)
    {
        var slots = new List<int>();
        if (m2Data is null || m2Data.Length < 0x148) return slots;

        uint count = ReadU32(m2Data, HdrParticles), offset = ReadU32(m2Data, HdrParticles + 4);
        if (count == 0 || offset == 0) return slots;

        for (uint i = 0; i < count && i < MaxEmitters; i++)
        {
            long record = offset + (long)i * EmitterStride;
            if (!InBounds(m2Data, record, EmitterStride)) break;
            int slot = ReadU16(m2Data, (int)record + EmTexture);
            if (!slots.Contains(slot)) slots.Add(slot);
        }
        return slots;
    }

    /// <summary>
    /// Decode the particle emitters into something a browser can re-simulate.
    ///
    /// This reads the record directly rather than reusing <c>M2Model.ParticleEmitters</c>, which is a
    /// deliberately thin rest summary built for the ItemVisual suggester: one representative colour,
    /// the peak scale, no midpoint, no alpha curve, no flipbook cell ranges. Re-simulating needs the
    /// curves, not a sample of them, and putting preview-only fields on that record would push them
    /// through the forge import path for no reason.
    ///
    /// A particle effect is a RATE, not a picture. The forge learned that expensively on Worldbreaker
    /// (ARMOR_FORGE.md 8c) - position, colour and size were all right and it still read as a strobe
    /// because rate and lifespan had been dropped. So everything that decides motion travels.
    /// </summary>
    private static List<M2FxEmitter> ReadEmitters(byte[] data, Func<int, int?> resolveTexture,
        int onlyIndex = -1)
    {
        var result = new List<M2FxEmitter>();

        uint count = ReadU32(data, HdrParticles), offset = ReadU32(data, HdrParticles + 4);
        if (count == 0 || offset == 0) return result;

        for (uint i = 0; i < count && result.Count < MaxEmitters; i++)
        {
            if (onlyIndex >= 0 && i != onlyIndex) continue;
            long record = offset + (long)i * EmitterStride;
            if (!InBounds(data, record, EmitterStride)) break;
            int r = (int)record;

            int? texture = resolveTexture(ReadU16(data, r + EmTexture));
            if (texture is null) continue;   // no sheet embedded, nothing worth drawing

            float rate = TrackFirst(data, r, 6);
            float life = TrackFirst(data, r, 5);
            if (!(rate > 0.01f) || !(life > 0.01f)) continue;   // draws nothing in the client either

            var scale = new[] { F32(data, r + EmScaleKeys), F32(data, r + EmScaleKeys + 4), F32(data, r + EmScaleKeys + 8) };
            if (!scale.Any(v => float.IsFinite(v) && v > 0.0005f)) continue;   // zero size is invisible

            // Colour is BGRA per key. Alpha is the fade curve and travels separately because the
            // client multiplies it into the vertex colour rather than into the tint.
            var colors = new float[3][];
            var alpha = new float[3];
            for (int k = 0; k < 3; k++)
            {
                int c = r + EmColorKeys + k * 4;
                colors[k] = new[] { data[c + 2] / 255f, data[c + 1] / 255f, data[c] / 255f };
                alpha[k] = data[c + 3] / 255f;
            }

            float midpoint = F32(data, r + EmMidpoint);
            if (!float.IsFinite(midpoint) || midpoint <= 0f || midpoint >= 1f) midpoint = 0.5f;

            int rows = Math.Clamp((int)ReadU16(data, r + EmTileRows), 1, 16);
            int cols = Math.Clamp((int)ReadU16(data, r + EmTileCols), 1, 16);
            int cells = rows * cols;

            result.Add(new M2FxEmitter(
                // Reader space, the same conversion the vertices went through: WoW (x,y,z) to (x,z,-y).
                // The emitter own up axis (WoW +Z) therefore becomes +Y, which is what makes a flame
                // rise in the viewer rather than drift sideways.
                Position: new[] { F32(data, r + EmPosition), F32(data, r + EmPosition + 8), -F32(data, r + EmPosition + 4) },
                Texture: texture.Value,
                BlendMode: ReadU16(data, r + EmBlend) & 0xFF,
                EmitterType: data[r + EmEmitterType],
                EmissionRate: rate,
                Lifespan: life,
                Speed: TrackFirst(data, r, 0),
                SpeedVariation: TrackFirst(data, r, 1),
                VerticalRange: TrackFirst(data, r, 2),
                HorizontalRange: TrackFirst(data, r, 3),
                Gravity: TrackFirst(data, r, 4),
                AreaLength: TrackFirst(data, r, 7),
                AreaWidth: TrackFirst(data, r, 8),
                ZSource: TrackFirst(data, r, 9),
                Midpoint: midpoint,
                ScaleRamp: scale,
                ColorRamp: colors,
                AlphaRamp: alpha,
                HeadCells: ClampCells(data, r + EmHeadCells, cells),
                TailCells: ClampCells(data, r + EmTailCells, cells),
                TileRows: rows,
                TileCols: cols));
        }

        return result;
    }

    /// <summary>
    /// Emitters of a SEPARATE effect model, shifted onto the host item that mounts it.
    ///
    /// This is how an <c>ItemVisual</c> reaches the previewer: the glow is not in the item's bytes at
    /// all, it is in a model named by ItemVisualEffects.dbc that the client hangs on one of the item's
    /// attachment points. Folding its emitters into the host's manifest — offset to the mount — means
    /// the browser draws them with the same particle system as everything else, with no second scene
    /// graph and no extra request. See <see cref="ItemVisualEffects"/> for the mounting rule.
    /// </summary>
    public static List<M2FxEmitter> ReadMountedEmitters(byte[]? m2Data, Func<int, int?> resolveTexture,
        System.Numerics.Vector3 mountMesh)
    {
        if (m2Data is null || m2Data.Length < 0x148) return new List<M2FxEmitter>();

        var emitters = ReadEmitters(m2Data, resolveTexture);
        for (int i = 0; i < emitters.Count; i++)
        {
            var p = emitters[i].Position;
            emitters[i] = emitters[i] with
            {
                Position = new[] { p[0] + mountMesh.X, p[1] + mountMesh.Y, p[2] + mountMesh.Z },
            };
        }
        return emitters;
    }

    /// <summary>
    /// The emitter a planned graft WILL produce, without building the M2 first.
    ///
    /// The Weapon Forge's import preview renders an intermediate mesh, not a packaged model, so there
    /// are no forged bytes to read emitters out of — but the motion plan already holds everything
    /// that decides the result: which donor record is being lifted, and the source overrides for
    /// position, colour, size and timing. This applies those overrides to the donor's decoded record
    /// exactly the way <see cref="M2EmitterTransplanter.Apply"/> applies them to its bytes, so the
    /// preview shows what the forge will actually package rather than a second approximation of it.
    ///
    /// <paramref name="positionMesh"/> is in the preview's own Y-up mesh space, because the caller
    /// holds the placed position and the graft only carries the WoW-space one.
    /// </summary>
    public static M2FxEmitter? FromGraft(
        MangosSuperUI.Services.WeaponForge.RawM2.M2EmitterTransplanter.Graft graft,
        int textureIndex,
        System.Numerics.Vector3 positionMesh)
    {
        var donor = ReadEmitters(graft.DonorM2, _ => textureIndex, graft.DonorEmitterIndex);
        if (donor.Count == 0) return null;
        var e = donor[0];

        var colors = e.ColorRamp;
        var alpha = e.AlphaRamp;
        if (graft.ColorRamp is { } ramp)
        {
            colors = new[]
            {
                new[] { ramp.Start.X / 255f, ramp.Start.Y / 255f, ramp.Start.Z / 255f },
                new[] { ramp.Mid.X / 255f, ramp.Mid.Y / 255f, ramp.Mid.Z / 255f },
                new[] { ramp.End.X / 255f, ramp.End.Y / 255f, ramp.End.Z / 255f },
            };
        }

        // Same rebasing Apply does: keep the donor's grow/shrink SHAPE, scaled to the source's size.
        var scale = e.ScaleRamp;
        if (graft.Scale is { } s && float.IsFinite(s) && s > 0f)
        {
            float peak = MathF.Max(MathF.Max(scale[0], scale[1]), scale[2]);
            scale = peak > 1e-6f
                ? new[] { scale[0] / peak * s, scale[1] / peak * s, scale[2] / peak * s }
                : new[] { s, s, s };
        }

        var m = graft.Motion;
        return e with
        {
            Position = new[] { positionMesh.X, positionMesh.Y, positionMesh.Z },
            ColorRamp = colors,
            AlphaRamp = alpha,
            ScaleRamp = scale,
            EmissionRate = m is { IsUsable: true } ? m.EmissionRate : e.EmissionRate,
            Lifespan = m is { IsUsable: true } ? m.Lifespan : e.Lifespan,
            Speed = m is { IsUsable: true } ? m.EmissionSpeed : e.Speed,
            SpeedVariation = m is { IsUsable: true } ? m.SpeedVariation : e.SpeedVariation,
            VerticalRange = m is { IsUsable: true } ? m.VerticalRange : e.VerticalRange,
            HorizontalRange = m is { IsUsable: true } ? m.HorizontalRange : e.HorizontalRange,
            Gravity = m is { IsUsable: true } ? m.Gravity : e.Gravity,
            AreaLength = m is { IsUsable: true } ? m.EmissionAreaLength : e.AreaLength,
            AreaWidth = m is { IsUsable: true } ? m.EmissionAreaWidth : e.AreaWidth,
        };
    }

    /// <summary>The flipbook cell range a particle walks during one phase of its life, as
    /// {start, end, repeat}.
    ///
    /// The stored end is EXCLUSIVE and routinely equals the cell count: FLAMELICKSMALL is a 4x4
    /// sheet (cells 0-15) whose decay range reads {8, 16, 1}. Clamping into range rather than
    /// rejecting the record is the difference between a flipbook that plays its second half and one
    /// that freezes on cell 0 for the whole decay phase. A range that is still inverted after
    /// clamping is not a range, and collapses to a single cell.</summary>
    private static int[] ClampCells(byte[] data, int offset, int cells)
    {
        int start = Math.Clamp((int)ReadU16(data, offset), 0, cells - 1);
        int end = Math.Clamp((int)ReadU16(data, offset + 2), 0, cells - 1);
        int repeat = Math.Max(1, (int)ReadU16(data, offset + 4));
        if (end < start) return new[] { start, start, 1 };
        return new[] { start, end, repeat };
    }

    /// <summary>First keyframe of emitter track <paramref name="index"/> (0-based, in
    /// <see cref="EmitterTrackStarts"/> order).</summary>
    private static float TrackFirst(byte[] data, int record, int index)
    {
        int track = record + EmitterTrackStarts[index];
        uint count = ReadU32(data, track + 20), offset = ReadU32(data, track + 24);
        if (count == 0 || offset == 0 || !InBounds(data, offset, 4)) return 0f;
        float v = F32(data, (int)offset);
        return float.IsFinite(v) ? v : 0f;
    }

    /// <summary>Everything animated about one batch's material.</summary>
    private static M2FxMesh? ReadBatchFx(byte[] data, M2Model model, M2Batch batch, int unit)
    {
        M2FxTrack? rgb = null, alpha = null;
        float[]? baseRgb = null;
        float? baseAlpha = null;
        if (batch.ColorIndex >= 0 && batch.ColorIndex < model.ColorTrackCount)
        {
            long record = (long)ReadU32(data, HdrColors + 4) + batch.ColorIndex * ColorRecordStride;
            if (InBounds(data, record, ColorRecordStride))
            {
                rgb = ReadTrack(data, model, (int)record, components: 3, ReadVector3, 12);
                alpha = ReadTrack(data, model, (int)record + 28, components: 1, ReadFixed16, 2);
                if (FirstKey(data, (int)record, ReadVector3, 12) is { } c)
                    baseRgb = new[] { c.X, c.Y, c.Z };
                if (FirstKey(data, (int)record + 28, ReadFixed16, 2) is { } a)
                    baseAlpha = a;
            }
        }

        // Texture weight: the same chain M2Model.GetStaticAlphaForBatch walks for the rest value.
        M2FxTrack? weight = null;
        float? baseWeight = null;
        if (batch.TextureCount > 0 && batch.TextureWeightIndex != ushort.MaxValue &&
            batch.TextureWeightIndex < model.TransparencyLookup.Count)
        {
            ushort trackIndex = model.TransparencyLookup[batch.TextureWeightIndex];
            if (trackIndex != ushort.MaxValue)
            {
                long record = (long)ReadU32(data, HdrTransparency + 4) + trackIndex * TransparencyRecordStride;
                if (InBounds(data, record, TransparencyRecordStride))
                {
                    weight = ReadTrack(data, model, (int)record, components: 1, ReadFixed16, 2);
                    baseWeight = FirstKey(data, (int)record, ReadFixed16, 2);
                }
            }
        }

        var uv = ReadUvFx(data, model, batch, unit);

        var fx = new M2FxMesh(rgb, alpha, weight, uv, baseRgb, baseAlpha, baseWeight);
        return fx.Any ? fx : null;
    }

    /// <param name="unit">Texture UNIT within the batch. TextureCount spans the transform-combo
    /// table exactly as it spans the texture and coordinate tables, so unit 1's transform lives at
    /// TextureTransformIndex + 1. Reading unit 0 for every unit is what froze the Warglaive's wave:
    /// its scroll is authored on unit 1, and a MODULATE pass whose scrolling half does not move is
    /// a stationary dark band — visibly worse than the un-composited render it replaces.</param>
    private static M2FxUv? ReadUvFx(byte[] data, M2Model model, M2Batch batch, int unit)
    {
        if (batch.TextureTransformIndex == ushort.MaxValue) return null;
        if (unit < 0 || unit >= Math.Max(1, (int)batch.TextureCount)) return null;
        long combo = (long)batch.TextureTransformIndex + unit;
        if (combo < 0 || combo >= model.TextureTransformLookup.Count) return null;

        ushort transform = model.TextureTransformLookup[(int)combo];
        if (transform == ushort.MaxValue || transform >= model.TextureTransformCount) return null;

        long record = (long)ReadU32(data, HdrUvAnimations + 4) + transform * UvRecordStride;
        if (!InBounds(data, record, UvRecordStride)) return null;
        int r = (int)record;

        var translate = ReadTrack(data, model, r, components: 3, ReadVector3, 12);
        var rotate = ReadTrack(data, model, r + 28, components: 1, ReadQuaternionZAngle, 16);
        var scale = ReadTrack(data, model, r + 56, components: 3, ReadVector3, 12);

        // The rest pose of all three, so the client can compose a static transform the exporter did
        // not bake into the vertices (GlbWriter writes raw UVs) and animate on top of it.
        var restT = FirstKey(data, r, ReadVector3, 12) ?? Vector3.Zero;
        float restR = FirstKey(data, r + 28, ReadQuaternionZAngle, 16)?.X ?? 0f;
        var restS = FirstKey(data, r + 56, ReadVector3, 12) ?? Vector3.One;

        bool identity = restT == Vector3.Zero && restR == 0f && restS == Vector3.One;
        float[]? rest = identity ? null : new[] { restT.X, restT.Y, restR, restS.X, restS.Y };

        var uv = new M2FxUv(rest, translate, rotate, scale);
        return uv.Any || rest is not null ? uv : null;
    }

    // ── track decoding ──────────────────────────────────────────────────────

    /// <summary>
    /// Decode one 28-byte M2Track into a loopable channel, or null when it holds still.
    ///
    /// Two time bases exist and both are handled:
    ///   • <b>globalSequence ≥ 0</b> — the track loops on its own timer, independent of any
    ///     animation. This is the case the forge's own glow pulse writes and the one that matters
    ///     most: a weapon sitting in a preview plays no animation at all, so a per-sequence track has
    ///     no clock, but a global-sequence track keeps running.
    ///   • <b>globalSequence == −1</b> — the keys belong to the animation timeline, and the ranges
    ///     array (indexed BY SEQUENCE) says which slice belongs to which sequence. The preview has
    ///     no animation state, so it takes range 0 — sequence 0 is Stand on every item model — and
    ///     loops that slice on its own span. Taking the whole timestamp array instead would splice
    ///     keys from unrelated sequences into one curve.
    /// </summary>
    private static M2FxTrack? ReadTrack<T>(byte[] data, M2Model model, int track, int components,
        Func<byte[], int, T> readValue, int valueStride) where T : struct
    {
        if (!InBounds(data, track, TrackStride)) return null;

        ushort interpolation = ReadU16(data, track);
        short globalSequence = (short)ReadU16(data, track + 2);
        uint rangeCount = ReadU32(data, track + 4), rangeOffset = ReadU32(data, track + 8);
        uint timeCount = ReadU32(data, track + 12), timeOffset = ReadU32(data, track + 16);
        uint keyCount = ReadU32(data, track + 20), keyOffset = ReadU32(data, track + 24);

        if (keyCount <= 1 || timeCount != keyCount || keyCount > MaxKeysPerTrack) return null;
        if (timeOffset == 0 || keyOffset == 0) return null;

        // Hermite/Bezier store (value, inTangent, outTangent) per key. We keep the value and read
        // it linearly; see M2FxTrack.Step for why the interpolation kind still has to travel.
        int storedStride = interpolation is 2 or 3 ? valueStride * 3 : valueStride;
        if (!InBounds(data, timeOffset, timeCount * 4L)) return null;
        if (!InBounds(data, keyOffset, keyCount * (long)storedStride)) return null;

        int first = 0, last = (int)keyCount - 1;
        uint durationMs;

        if (globalSequence >= 0)
        {
            if (globalSequence >= model.GlobalSequenceDurations.Count) return null;
            durationMs = model.GlobalSequenceDurations[globalSequence];
            if (durationMs == 0) return null;
        }
        else
        {
            if (rangeCount > 0 && rangeOffset != 0 && InBounds(data, rangeOffset, 8))
            {
                uint start = ReadU32(data, (int)rangeOffset), end = ReadU32(data, (int)rangeOffset + 4);
                if (start > end || end >= keyCount) return null;
                first = (int)start;
                last = (int)end;
                if (last - first < 1) return null;   // one key in this sequence: holds still
            }
            uint firstMs = ReadU32(data, (int)(timeOffset + first * 4L));
            uint lastMs = ReadU32(data, (int)(timeOffset + last * 4L));
            if (lastMs <= firstMs) return null;
            durationMs = lastMs - firstMs;
        }

        int count = last - first + 1;
        var times = new uint[count];
        var keys = new float[count][];
        uint baseMs = globalSequence >= 0 ? 0u : ReadU32(data, (int)(timeOffset + first * 4L));

        for (int i = 0; i < count; i++)
        {
            int k = first + i;
            uint ms = ReadU32(data, (int)(timeOffset + k * 4L));
            times[i] = ms >= baseMs ? ms - baseMs : 0u;
            if (i > 0 && times[i] < times[i - 1]) return null;   // not monotonic: not a curve

            var value = readValue(data, (int)(keyOffset + k * (long)storedStride));
            var parsed = Components(value, components);
            if (parsed is null) return null;
            keys[i] = parsed;
        }

        // A "curve" whose keys are all the same value animates nothing; leave it to the baked
        // constant rather than paying for a per-frame update that changes nothing.
        if (IsConstant(keys)) return null;

        return new M2FxTrack(durationMs, components, interpolation == 0, times, keys);
    }

    private static T? FirstKey<T>(byte[] data, int track, Func<byte[], int, T> readValue, int valueStride)
        where T : struct
    {
        if (!InBounds(data, track, TrackStride)) return null;
        ushort interpolation = ReadU16(data, track);
        uint keyCount = ReadU32(data, track + 20), keyOffset = ReadU32(data, track + 24);
        if (keyCount == 0 || keyOffset == 0) return null;
        int storedStride = interpolation is 2 or 3 ? valueStride * 3 : valueStride;
        if (!InBounds(data, keyOffset, storedStride)) return null;
        return readValue(data, (int)keyOffset);
    }

    private static float[]? Components<T>(T value, int components) where T : struct
    {
        switch (value)
        {
            case Vector3 v:
                if (!float.IsFinite(v.X) || !float.IsFinite(v.Y) || !float.IsFinite(v.Z)) return null;
                return components == 1 ? new[] { v.X } : new[] { v.X, v.Y, v.Z };
            case float f:
                return float.IsFinite(f) ? new[] { f } : null;
            default:
                return null;
        }
    }

    private static bool IsConstant(float[][] keys)
    {
        for (int i = 1; i < keys.Length; i++)
            for (int c = 0; c < keys[i].Length; c++)
                if (MathF.Abs(keys[i][c] - keys[0][c]) > 1e-4f) return false;
        return true;
    }

    // ── value readers ───────────────────────────────────────────────────────

    private static Vector3 ReadVector3(byte[] d, int o) =>
        new(ReadF32(d, o), ReadF32(d, o + 4), ReadF32(d, o + 8));

    /// <summary>M2 fixed16 alpha: a signed 16-bit fraction of 32767, clamped to 0–1.</summary>
    private static float ReadFixed16(byte[] d, int o) =>
        Math.Clamp((short)ReadU16(d, o) / 32767f, 0f, 1f);

    /// <summary>
    /// A UV rotation quaternion, reduced to the Z angle the client actually applies.
    ///
    /// Texture-space rotation only has one degree of freedom, and the client composes it about
    /// (0.5, 0.5). Shipping the whole quaternion would make the browser reconstruct that; shipping
    /// the angle keeps the client side a single <c>setUvTransform</c> call. Packed into a Vector3 so
    /// it goes through the same component path as the others.
    /// </summary>
    private static Vector3 ReadQuaternionZAngle(byte[] d, int o)
    {
        float x = ReadF32(d, o), y = ReadF32(d, o + 4), z = ReadF32(d, o + 8), w = ReadF32(d, o + 12);
        if (!float.IsFinite(x) || !float.IsFinite(y) || !float.IsFinite(z) || !float.IsFinite(w))
            return Vector3.Zero;
        return new Vector3(2f * MathF.Atan2(z, w), 0f, 0f);
    }

    // ── binary helpers ──────────────────────────────────────────────────────

    private static bool InBounds(byte[] d, long offset, long length) =>
        offset > 0 && length >= 0 && offset + length <= d.Length;

    private static ushort ReadU16(byte[] d, int o) =>
        o < 0 || o + 2 > d.Length ? (ushort)0 : BitConverter.ToUInt16(d, o);

    private static uint ReadU32(byte[] d, int o) =>
        o < 0 || o + 4 > d.Length ? 0u : BitConverter.ToUInt32(d, o);

    private static float ReadF32(byte[] d, int o) =>
        o < 0 || o + 4 > d.Length ? 0f : BitConverter.ToSingle(d, o);

    private static float F32(byte[] d, int o) => ReadF32(d, o);
}

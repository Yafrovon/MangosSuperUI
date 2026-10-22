using System.Buffers.Binary;
using System.Numerics;
using System.Text;
using MangosSuperUI.Services;

namespace MangosSuperUI.Services.WeaponForge.RawM2;

/// <summary>
/// Gives a forged v256 model a <b>real, moving</b> particle effect by transplanting one of Blizzard's
/// own emitters out of a stock 1.12 item model.
///
/// === Why a transplant and not an author ===
///
/// A vanilla particle-emitter record is 504 bytes, and it is NOT flat. It carries eleven
/// <c>M2Track</c> members (28 B each: <c>uint16 interp; int16 globalSequence;</c> then three
/// <c>M2Array{uint32 count; uint32 offsetIntoFile}</c> at +4/+12/+20) plus two filename arrays at
/// +24/+32 — 33 file offsets per record. Authoring one from scratch means inventing emission rate,
/// lifespan, gravity, drag, spin, particle type and every keyframe, none of which is guessable and
/// all of which the client will happily render as garbage. Lifting a working one instead means every
/// field except the four we deliberately change is Blizzard-authored and client-proven.
///
/// === The property that makes it safe (measured, do NOT re-derive) ===
///
/// Across <b>391 emitters on 130 stock 1.12 item models</b> (weapon/shield/shoulder/head), the
/// emitter block is <b>self-contained and terminal</b> in 391/391 cases:
///   • every non-empty M2Array inside a record points at or after <c>ofsParticleEmitters</c>;
///   • no other header array points into that region;
///   • the referenced data runs contiguously to EOF.
/// So <c>donor[ofsParticleEmitters .. EOF]</c> is a closed blob. Append it somewhere else, add a
/// constant delta to those 33 offsets per record, and the emitter is intact — no element sizes, no
/// per-track knowledge, no offset rebuild. That is the same offset-preserving-surgery discipline the
/// rest of RawM2 uses.
///
/// === Relationship to Services/M2Handlers (read this before adding a third one) ===
///
/// The project already had an M2 particle subsystem — <c>M2ParticlePatcher</c>,
/// <c>M2EmitterParser</c>, <c>M2TextureParser</c> in <c>Services/M2Handlers</c>, used by the Spell,
/// Item and Patch features. It does NOT overlap in function: those PATCH emitters (and texture
/// entries) that a model already has, which is what spell visuals need. This class ADDS an emitter to
/// a model that has none, which is what the Forge needs, because the vanilla weapon/armor donor
/// scaffolds ship with zero emitters. Nothing there can add one.
///
/// It does overlap in KNOWLEDGE, and the two derivations independently agree, which is worth keeping:
/// <c>M2EmitterParser</c>'s verified property offsets (emissionSpeed 0x048, speedVariation 0x064,
/// verticalRange 0x080, horizontalRange 0x09C, gravity 0x0B8, lifespan 0x0D4, emissionRate 0x0F0,
/// emissionAreaLength 0x10C) are exactly the <b>values array of each track</b> at
/// <see cref="TrackStarts"/> + 20 for the first eight tracks — 52+20=0x48, 80+20=0x64, 108+20=0x80,
/// 136+20=0x9C, 164+20=0xB8, 192+20=0xD4, 220+20=0xF0, 248+20=0x10C. Both descriptions also agree
/// that a track is 28 bytes; that file reads the leading <c>interp|globalSequence</c> pair as a single
/// <c>0x0000FFFF</c> marker and the empty ranges array as padding, which is the same bytes seen from
/// the other side. A change to either understanding should be reflected in both.
///
/// === What gets retargeted ===
///
/// Fixed-width, offset-free fields: position (+8, three WoW floats), bone (+20), texture index
/// (+22), the three colour keyframes (+336, BGRA each) and the three scale keyframes (+348) — where
/// the effect sits, what colour it is and how big.
///
/// Plus, when the source hands one over, the ten float TRACKS' <b>values in place</b>
/// (<see cref="Graft.Motion"/>). A track's value array is a separate run of floats addressed from the
/// record; overwriting the floats there changes nothing structural — same count, same offset, same
/// interpolation — so the offset-preserving-surgery discipline still holds, and the client is still
/// running Blizzard's own emitter code over Blizzard's own field layout.
///
/// That last part was originally left to the donor on the theory that the donor "keeps deciding how
/// it MOVES". Measured, that is the bug rather than the design: a donor authored for a thrown molotov
/// (FLAMELICKSMALL — lifespan 0.75 s, 7 particles/s, 4×4 flipbook) standing in for a Worldbreaker
/// shoulder brazier (lifespan 2.30 s, 8 particles/s, 1×1) drops the steady-state particle count from
/// ~18 to ~5 and steps a 16-cell flipbook 21 times a second, which reads as a fast on/off strobe
/// instead of fire. Position/colour/size were right and it still looked wrong. See
/// <see cref="M2EmitterMotion"/> for the measurement. The donor still decides everything that is not
/// a number on these ten tracks: flags, emitter type, particle type, blend, spin/tumble/wind, and the
/// tile grid that matches its own texture.
/// </summary>
public static class M2EmitterTransplanter
{
    public const int EmitterStride = 504;

    private const int HdrGlobalSequences = 0x014;   // count, offset at +4
    private const int HdrTextures = 0x05C;      // count, offset at +4
    private const int HdrParticles = 0x13C;     // count, offset at +4
    private const int TextureEntrySize = 16;    // type, flags, nFilename, ofsFilename

    // Record offsets of the fixed-width fields we retarget.
    private const int FldPosition = 8;
    private const int FldBone = 20;
    private const int FldTexture = 22;
    private const int FldBlend = 40;
    private const int FldColorKeys = 336;       // 3 x BGRA
    private const int FldScaleKeys = 348;       // 3 x float

    /// <summary>Every (count, offset) M2Array position inside one 504-byte record: the two filename
    /// arrays, then the eleven tracks, each contributing ranges/timestamps/values.</summary>
    internal static readonly int[] TrackStarts = { 52, 80, 108, 136, 164, 192, 220, 248, 276, 304, 476 };
    private static readonly int[] ArrayPositions = BuildArrayPositions();

    private static int[] BuildArrayPositions()
    {
        var list = new List<int> { 24, 32 };
        foreach (int t in TrackStarts) { list.Add(t + 4); list.Add(t + 12); list.Add(t + 20); }
        return list.ToArray();
    }

    /// <summary>One emitter to graft on, already resolved to a donor.</summary>
    /// <param name="DonorM2">Raw bytes of the stock 1.12 model to lift the emitter from.</param>
    /// <param name="DonorEmitterIndex">Which emitter of that donor (0-based).</param>
    /// <param name="PositionWoW">Where it should sit on the forged model, WoW model space.</param>
    /// <param name="ColorRgb">Recolour (0–255 per channel); null keeps the donor's colours.</param>
    /// <param name="Scale">Particle size; null keeps the donor's.</param>
    /// <param name="TexturePath">MPQ path for the emitter's texture. Either a stock vanilla path or a
    /// member this build packages; null keeps whatever the donor's own texture slot resolves to.</param>
    /// <param name="Motion">The SOURCE emitter's own track values — rate, lifespan, speed, spread,
    /// gravity, emission area. Null keeps the donor's timing wholesale (the pre-measurement
    /// behaviour). See the class remarks for why passing this matters.</param>
    /// <param name="ColorRamp">The source's birth → mid → death colour curve. Takes precedence over
    /// <paramref name="ColorRgb"/>, which paints one colour on all three keyframes.</param>
    public sealed record Graft(
        byte[] DonorM2,
        int DonorEmitterIndex,
        Vector3 PositionWoW,
        Vector3? ColorRgb,
        float? Scale,
        string? TexturePath,
        string Describe,
        M2EmitterMotion? Motion = null,
        M2EmitterColorRamp? ColorRamp = null);

    /// <summary>Vanilla-sane bounds for the values we write into a donor's tracks. A later client can
    /// hold numbers 1.12 never shipped, and an emitter that asks for 4,000 particles a second is a
    /// frame-rate bug rather than an effect. Ranges are the min/max actually observed across the 391
    /// stock 1.12 item emitters, widened slightly so a legitimate outlier is not clamped flat.</summary>
    private static class MotionLimits
    {
        public const float MinLifespan = 0.05f, MaxLifespan = 10f;
        public const float MinRate = 0.5f, MaxRate = 400f;
        public const float MaxSpeed = 20f;
        public const float MaxArea = 5f;
        public const float MaxAbsGravity = 20f;
        /// <summary>Particles alive at once (rate × lifespan). Stock 1.12's densest item emitter is
        /// GLOWSTAR at 200/s × 0.2 s = 40; this leaves generous headroom while still refusing a model
        /// that would put thousands of additive sprites on one shoulder.</summary>
        public const float MaxSteadyState = 150f;
    }

    public sealed record Result(byte[] M2, int Grafted, IReadOnlyList<string> Notes);

    /// <summary>
    /// The MPQ path of the texture a donor's emitter actually samples, read out of the donor's own
    /// texture table.
    ///
    /// This exists because the hand-written paths in <see cref="VanillaEmitterDonors"/> were wrong for
    /// 20 of 27 entries: they were built by prefixing the texture stem with the DONOR MODEL's own
    /// directory, and vanilla emitter sheets overwhelmingly do not live there. FIRE1 is
    /// <c>SPELLS\FIRE1.BLP</c>, GLOWBALL is <c>CREATURE\FIREELEMENTAL\GLOWBALL.BLP</c>, GLOWSTAR is
    /// <c>INTERFACE\BUTTONS\GLOWSTAR.BLP</c>. A graft that names a member the client cannot open gets
    /// an emitter with no texture, which draws nothing — silently, because a missing effect looks
    /// exactly like an effect that was never grafted.
    ///
    /// Reading it from the donor cannot go stale, so this is the source of truth and the catalog's
    /// <c>TexturePath</c> is only a fallback for a donor whose table cannot be read.
    /// </summary>
    public static string? ResolveDonorTexture(byte[] donor, int emitterIndex)
    {
        if (donor.Length < 0x148) return null;

        uint emitterCount = U32(donor, HdrParticles), emitterOfs = U32(donor, HdrParticles + 4);
        if (emitterIndex < 0 || emitterIndex >= emitterCount || emitterOfs == 0) return null;

        long record = emitterOfs + (long)emitterIndex * EmitterStride;
        if (record + EmitterStride > donor.Length) return null;

        int textureIndex = BinaryPrimitives.ReadUInt16LittleEndian(donor.AsSpan((int)record + FldTexture));
        uint textureCount = U32(donor, HdrTextures), textureOfs = U32(donor, HdrTextures + 4);
        if (textureIndex >= textureCount || textureOfs == 0) return null;

        long entry = textureOfs + (long)textureIndex * TextureEntrySize;
        if (entry + TextureEntrySize > donor.Length) return null;

        uint nameLength = U32(donor, (int)entry + 8), nameOfs = U32(donor, (int)entry + 12);
        if (nameLength <= 1 || nameOfs == 0 || nameOfs + nameLength > donor.Length) return null;

        return Encoding.ASCII.GetString(donor, (int)nameOfs, (int)nameLength - 1).TrimEnd('\0');
    }

    /// <summary>Is this donor safe to lift from? (see the class remarks). Cheap, and every caller
    /// should gate on it rather than trusting a hardcoded list against an unknown client build.</summary>
    public static bool IsTransplantable(byte[] donor, int emitterIndex, out string reason)
    {
        reason = "";
        if (donor.Length < 0x148) { reason = "not an M2"; return false; }
        uint n = U32(donor, HdrParticles), ofs = U32(donor, HdrParticles + 4);
        if (n == 0 || ofs == 0) { reason = "donor has no particle emitters"; return false; }
        if (emitterIndex < 0 || emitterIndex >= n) { reason = $"donor has {n} emitter(s), index {emitterIndex} requested"; return false; }
        if (ofs + (long)n * EmitterStride > donor.Length) { reason = "emitter array runs past EOF"; return false; }

        // No other header array may point into the block we are about to lift.
        foreach (int h in new[] { 0x018, 0x020, 0x038, 0x040, 0x048, 0x050, 0x058, 0x060, 0x068,
                                  0x078, 0x088, 0x098, 0x0A0, 0x0A8, 0x0B0, 0x108, 0x110, 0x138 })
        {
            uint ho = U32(donor, h);
            if (ho >= ofs && ho < donor.Length) { reason = $"header array at 0x{h:X3} points into the emitter block"; return false; }
        }
        // Every array in every record must reference data at or after the block start.
        for (uint i = 0; i < n; i++)
        {
            int rec = (int)(ofs + i * EmitterStride);
            foreach (int p in ArrayPositions)
            {
                uint count = U32(donor, rec + p), aofs = U32(donor, rec + p + 4);
                if (count == 0 || aofs == 0) continue;
                if (aofs < ofs || aofs >= donor.Length) { reason = $"emitter {i} array at +{p} points outside the block"; return false; }
            }
        }
        return true;
    }

    /// <summary>Graft the emitters onto <paramref name="target"/>. Returns the target unchanged (and
    /// says why) when nothing could be grafted — motion is an enhancement, never a build blocker.</summary>
    public static Result Apply(byte[] target, IReadOnlyList<Graft> grafts)
    {
        var notes = new List<string>();
        if (grafts.Count == 0) return new Result(target, 0, notes);
        if (target.Length < 0x148) return new Result(target, 0, new[] { "target is not a v256 M2" });

        // Emitter positions go in verbatim, in model space. An M2 bone's rest transform is
        // T(pivot) · anim · T(-pivot), which is IDENTITY at rest — the pivot is a rotation centre,
        // not a rest translation — so a point on the root bone sits exactly where its coordinates
        // say. This is the same convention RewriteAttachmentPositions writes glow anchors under, and
        // that one is client-proven. (Subtracting the pivot here shifted every emitter by it:
        // measured -0.146 on Sword_2H_Claymore_B_02.)
        var usable = new List<(Graft G, byte[] Donor, uint Ofs, int Rec)>();
        foreach (var g in grafts)
        {
            if (!IsTransplantable(g.DonorM2, g.DonorEmitterIndex, out string why))
            {
                notes.Add($"{g.Describe}: donor rejected ({why}).");
                continue;
            }
            uint ofs = U32(g.DonorM2, HdrParticles + 4);
            usable.Add((g, g.DonorM2, ofs, (int)(ofs + g.DonorEmitterIndex * EmitterStride)));
        }
        if (usable.Count == 0) return new Result(target, 0, notes);

        var outp = new List<byte>(target);
        Align4(outp);

        // 1) Texture table: append entries for the emitters that name their own texture, so each
        //    graft can point at one. Nothing but the header addresses the table, so appending a new
        //    one and repointing 0x5C/0x60 is safe.
        var texIndexFor = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var wantTextures = usable.Select(u => u.G.TexturePath).Where(p => !string.IsNullOrWhiteSpace(p))
                                 .Select(p => p!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (wantTextures.Count > 0)
        {
            uint oldTexCount = U32(target, HdrTextures), oldTexOfs = U32(target, HdrTextures + 4);
            // Strings first, so their offsets are known when the table is written.
            var stringOffsets = new Dictionary<string, (int Ofs, int Len)>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in wantTextures)
            {
                Align4(outp);
                int so = outp.Count;
                var bytes = Encoding.ASCII.GetBytes(path);
                outp.AddRange(bytes);
                outp.Add(0);
                stringOffsets[path] = (so, bytes.Length + 1);
            }
            Align4(outp);
            int newTableOfs = outp.Count;
            for (uint i = 0; i < oldTexCount; i++)     // copy the existing entries verbatim
            {
                int src = (int)(oldTexOfs + i * TextureEntrySize);
                if (src + TextureEntrySize > target.Length) break;
                outp.AddRange(target.Skip(src).Take(TextureEntrySize));
            }
            int next = (int)oldTexCount;
            foreach (var path in wantTextures)
            {
                var (so, len) = stringOffsets[path];
                AddU32(outp, 0);                 // type 0 = hardcoded filename (not DBC-driven)
                AddU32(outp, 0);                 // wrap flags
                AddU32(outp, (uint)len);
                AddU32(outp, (uint)so);
                texIndexFor[path] = next++;
            }
            var withTable = outp.ToArray();
            U32W(withTable, HdrTextures, (uint)next);
            U32W(withTable, HdrTextures + 4, (uint)newTableOfs);
            outp = new List<byte>(withTable);
            notes.Add($"{wantTextures.Count} emitter texture slot(s) appended (indices {oldTexCount}..{next - 1}).");
        }

        // 2) Each donor's emitter block, verbatim. Offsets inside are fixed up by a constant delta.
        var deltaFor = new List<(Graft G, int RecInOut)>();
        // One colour-key buffer for the whole loop: a stackalloc inside it grows the stack per graft.
        Span<Vector3> keys = stackalloc Vector3[3];
        foreach (var (g, donor, ofs, rec) in usable)
        {
            Align4(outp);
            int blobBase = outp.Count;
            for (int i = (int)ofs; i < donor.Length; i++) outp.Add(donor[i]);
            int delta = blobBase - (int)ofs;
            deltaFor.Add((g, rec + delta));
        }

        // 3) The emitter array itself: one contiguous run of retargeted records.
        Align4(outp);
        int arrayBase = outp.Count;
        var buf = outp.ToArray();
        var records = new List<byte[]>();
        // Track-value rewrites are collected here and applied to the finished buffer: the floats they
        // target live in the appended donor blob, not in the record, so they can only be written once
        // every blob is in place. (count, offset) come from the record's own — already shifted —
        // value M2Array, so this stays offset-preserving.
        var valueWrites = new List<(int Offset, int Count, float Value)>();
        for (int k = 0; k < deltaFor.Count; k++)
        {
            var (g, recInOut) = deltaFor[k];
            var rec = new byte[EmitterStride];
            Array.Copy(buf, recInOut, rec, 0, EmitterStride);

            int delta = recInOut - usable[k].Rec;
            foreach (int p in ArrayPositions)
            {
                uint count = U32(rec, p), aofs = U32(rec, p + 4);
                if (count == 0 || aofs == 0) continue;
                U32W(rec, p + 4, (uint)(aofs + delta));
            }

            WriteVec3(rec, FldPosition, g.PositionWoW);
            BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(FldBone), 0);   // rigid on the root bone
            if (g.TexturePath is { Length: > 0 } tp && texIndexFor.TryGetValue(tp, out int ti))
                BinaryPrimitives.WriteUInt16LittleEndian(rec.AsSpan(FldTexture), (ushort)ti);
            // Colour: the source's whole ramp when it handed one over, else its single colour on all
            // three keys (which is a flat particle, but still the source's flat particle).
            var ramp = g.ColorRamp ?? (g.ColorRgb is { } flat ? new M2EmitterColorRamp(flat, flat, flat) : null);
            if (ramp is not null)
            {
                keys[0] = ramp.Start; keys[1] = ramp.Mid; keys[2] = ramp.End;
                for (int key = 0; key < 3; key++)
                {
                    int o = FldColorKeys + key * 4;
                    rec[o + 0] = ClampByte(keys[key].Z);   // B
                    rec[o + 1] = ClampByte(keys[key].Y);   // G
                    rec[o + 2] = ClampByte(keys[key].X);   // R
                    // keyframe alpha (rec[o+3]) is the donor's fade curve — left alone on purpose
                }
            }
            NeutraliseDanglingGlobalSequences(rec, target, g.Describe, notes);
            if (g.Scale is { } s && float.IsFinite(s) && s > 0f)
                for (int key = 0; key < 3; key++)
                {
                    float donorScale = BitConverter.ToSingle(rec, FldScaleKeys + key * 4);
                    // Preserve the donor's grow/shrink SHAPE, re-based on the source's size.
                    float peak = MathF.Max(MathF.Max(
                        BitConverter.ToSingle(rec, FldScaleKeys),
                        BitConverter.ToSingle(rec, FldScaleKeys + 4)),
                        BitConverter.ToSingle(rec, FldScaleKeys + 8));
                    float shaped = peak > 1e-6f && float.IsFinite(donorScale) ? donorScale / peak * s : s;
                    WriteFloat(rec, FldScaleKeys + key * 4, shaped);
                }
            if (g.Motion is { } m)
                CollectMotionWrites(rec, m, valueWrites, notes, g.Describe);
            records.Add(rec);
        }
        foreach (var r in records) outp.AddRange(r);

        var final = outp.ToArray();
        foreach (var (off, count, value) in valueWrites)
            for (int i = 0; i < count; i++) WriteFloat(final, off + i * 4, value);
        U32W(final, HdrParticles, (uint)records.Count);
        U32W(final, HdrParticles + 4, (uint)arrayBase);
        notes.Add($"{records.Count} particle emitter(s) grafted from stock 1.12 donors at 0x{arrayBase:X}.");
        return new Result(final, records.Count, notes);
    }

    /// <summary>Point any track that times itself against a GLOBAL SEQUENCE back at the animation
    /// clock, because the donor's index does not survive the move.
    ///
    /// A track's <c>int16 globalSequence</c> at track+2 indexes the model's own globalLoops array
    /// (header 0x014/0x018). Apply copies the record verbatim and shifts only the three M2Array
    /// offsets — the index rides along unchanged and now points into the TARGET's array, which for
    /// every measured forge scaffold has zero entries. The client then reads a loop duration that is
    /// not there and takes a modulo against it: a track timed on garbage cycles arbitrarily fast, and
    /// on the enabled track that is an emitter switching on and off at nonsense speed.
    ///
    /// Measured: all eleven tracks on all of <see cref="VanillaEmitterDonors"/>'s donors carry −1, so
    /// this fires on nothing today. It is here because the failure is silent, indistinguishable from
    /// the timing bug it sits next to, and one donor away — WotLK item models routinely drive
    /// emission rate off a global sequence, so the first person to add such a donor would hit it.
    ///
    /// Resetting to −1 makes the track constant at its first key rather than importing the donor's
    /// loop. That is the conservative half of the trade: a lost pulse is a duller effect, whereas a
    /// dangling index is undefined client behaviour.</summary>
    private static void NeutraliseDanglingGlobalSequences(byte[] rec, byte[] target, string describe, List<string> notes)
    {
        uint targetGlobalSequences = U32(target, HdrGlobalSequences);
        foreach (int track in TrackStarts)
        {
            short gs = BinaryPrimitives.ReadInt16LittleEndian(rec.AsSpan(track + 2));
            if (gs < 0 || gs < targetGlobalSequences) continue;
            BinaryPrimitives.WriteInt16LittleEndian(rec.AsSpan(track + 2), -1);
            notes.Add($"{describe}: donor track at +{track} was timed on global sequence {gs}, which this " +
                      $"model does not have ({targetGlobalSequences} loop(s)) — reset to the animation clock " +
                      "so it holds its first value instead of looping against an absent duration.");
        }
    }

    /// <summary>Queue the source's ten track values over the donor's, clamped to what 1.12 ships.
    ///
    /// A track whose value array the source cannot speak to is left alone rather than zeroed — an
    /// emitter with no lifespan draws nothing, so a half-read source must degrade to "the donor's
    /// timing", which is what the forge did before this existed and is merely wrong-looking, not
    /// invisible. Every key of a track gets the same value: all 391 stock item emitters store one
    /// constant key per track, so there is no donor curve to preserve, and a donor that does carry
    /// several keys would otherwise end up with the source's rate on key 0 and the donor's on key 1.</summary>
    private static void CollectMotionWrites(byte[] rec, M2EmitterMotion m,
        List<(int Offset, int Count, float Value)> writes, List<string> notes, string describe)
    {
        if (!m.IsUsable) return;

        float lifespan = Math.Clamp(m.Lifespan, MotionLimits.MinLifespan, MotionLimits.MaxLifespan);
        float rate = Math.Clamp(m.EmissionRate, MotionLimits.MinRate, MotionLimits.MaxRate);
        if (rate * lifespan > MotionLimits.MaxSteadyState)
        {
            float trimmed = MotionLimits.MaxSteadyState / lifespan;
            notes.Add($"{describe}: source asks for {rate * lifespan:F0} simultaneous particles; " +
                      $"emission rate trimmed {rate:F1}/s → {trimmed:F1}/s to stay inside what 1.12 draws comfortably.");
            rate = trimmed;
        }

        // Index into TrackStarts, and the clamped source value for each.
        var plan = new (int Track, float Value)[]
        {
            (0, Math.Clamp(m.EmissionSpeed, -MotionLimits.MaxSpeed, MotionLimits.MaxSpeed)),
            (1, Math.Clamp(m.SpeedVariation, 0f, 1f)),
            (2, Math.Clamp(m.VerticalRange, 0f, MathF.Tau)),
            (3, Math.Clamp(m.HorizontalRange, 0f, MathF.Tau)),
            (4, Math.Clamp(m.Gravity, -MotionLimits.MaxAbsGravity, MotionLimits.MaxAbsGravity)),
            (5, lifespan),
            (6, rate),
            (7, Math.Clamp(m.EmissionAreaLength, 0f, MotionLimits.MaxArea)),
            (8, Math.Clamp(m.EmissionAreaWidth, 0f, MotionLimits.MaxArea)),
            (9, Math.Clamp(m.ZSource, -MotionLimits.MaxArea, MotionLimits.MaxArea)),
        };

        int applied = 0;
        foreach (var (trackIdx, value) in plan)
        {
            if (!float.IsFinite(value)) continue;
            int track = TrackStarts[trackIdx];
            uint count = U32(rec, track + 20), ofs = U32(rec, track + 24);
            if (count == 0 || count > 1024 || ofs == 0) continue;   // nothing addressable to overwrite
            writes.Add(((int)ofs, (int)count, value));
            applied++;
        }
        if (applied > 0)
            notes.Add($"{describe}: source timing applied over the donor's — lifespan {lifespan:F2}s, " +
                      $"rate {rate:F1}/s (~{rate * lifespan:F0} particles alive), speed {m.EmissionSpeed:F2}, " +
                      $"spread {m.VerticalRange:F2}/{m.HorizontalRange:F2} rad, gravity {m.Gravity:F2}.");
    }

    private static byte ClampByte(float v) => (byte)Math.Clamp((int)MathF.Round(v), 0, 255);
    private static void Align4(List<byte> l) { while (l.Count % 4 != 0) l.Add(0); }
    private static void AddU32(List<byte> l, uint v) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(b, v); foreach (var x in b) l.Add(x); }
    private static uint U32(byte[] b, int o) => o + 4 > b.Length ? 0u : BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(o));
    private static void U32W(byte[] b, int o, uint v) { if (o + 4 <= b.Length) BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(o), v); }
    private static void WriteFloat(byte[] b, int o, float f) { if (o + 4 <= b.Length) BinaryPrimitives.WriteSingleLittleEndian(b.AsSpan(o), f); }
    private static void WriteVec3(byte[] b, int o, Vector3 v) { WriteFloat(b, o, v.X); WriteFloat(b, o + 4, v.Y); WriteFloat(b, o + 8, v.Z); }
}

using System.Linq;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;
using SkiaSharp;

namespace MangosSuperUI.Services;

using SKIN_VERTEX = VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>;

/// <summary>
/// Writes character M2 models as skinned glTF (GLB) — preserves the skeleton,
/// per-vertex bone weights, named attachment nodes that Three.js finds at
/// runtime to position weapons/helms/shoulders, AND (Session O) baked
/// animation clips for the configured animation set.
///
/// Companion to <see cref="GlbWriter"/> (which handles rigid item/weapon GLBs).
///
/// === Output structure (Session O) ===
///   Scene
///   ├── Armature
///   │   ├── Bone_0 (root)
///   │   │   ├── Bone_18 → ...
///   │   │   └── Attachment_1 (HandRight) → ...
///   │   └── ...
///   + glTF Animations:
///       "Stand" (animationId 0) ← idle pose; fixes hand/weapon angle
///       "Walk"  (animationId 4)
///       "Run"   (animationId 5)
///       ... whatever DefaultAnimationsToBake contains
///
/// Three.js's GLTFLoader exposes these as gltf.animations[] — an array of
/// THREE.AnimationClip — which our client passes to a THREE.AnimationMixer
/// for playback.
///
/// === Coordinate system ===
/// M2Reader has already converted all positions/rotations/scales to glTF
/// conventions (Y-up). Bone pivots are world-space; each NodeBuilder gets a
/// LOCAL translation = bone.Pivot - parent.Pivot. Animation tracks add their
/// per-sequence offset on top of this rest position.
///
/// === M2 → glTF TRS mapping ===
///   M = T(pivot) * T(translation) * R(rotation) * S(scale) * T(-pivot)
///
/// Conjugation by T(±pivot) means the M2's TRS is applied IN THE PIVOT FRAME.
/// In glTF terms, the bone node sits AT the pivot (relative to its parent's
/// pivot), so the node-local TRS frame IS the pivot frame. That makes the
/// mapping:
///
///   glTF node rest TRS    = T(pivot - parent.pivot)
///   glTF node animated T  = T(pivot - parent.pivot) + M2_translation[t]
///   glTF node animated R  = M2_rotation[t]            (no offset)
///   glTF node animated S  = M2_scale[t]               (no offset)
///
/// i.e. the M2 rotation and scale tracks pass through unchanged because
/// they're already expressed in the pivot frame; only translation gets
/// offset by the rest position.
/// </summary>
public static class SkinnedGlbWriter
{
    /// <summary>
    /// Default set of animations baked into character GLBs.
    /// AnimationData.dbc IDs:
    ///   0 = Stand (canonical idle — fixes weapon/hand-angle issue from
    ///              prior sessions; the idle animation includes a small
    ///              hand-bone rotation that gives weapons a natural angle).
    ///   4 = Walk
    ///   5 = Run
    ///
    /// Each animation adds ~50 bones × N keyframes worth of TRS data. At
    /// three short animations a typical character GLB grows from ~250KB
    /// to ~400KB — acceptable. Expand this list to ship more anims.
    ///
    /// Marginal cost per added animation is small because most bones
    /// don't animate during walk/run (only the hips, legs, arms, and
    /// spine have keyframes — fingers, jaw, eye bones are static).
    /// </summary>
    /// <remarks>
    /// Extended 2026-07-26 for the world editor's walkable character.
    ///
    /// AnimationData.dbc ids (vanilla 1.12, verified against MSUIClient's table):
    ///   0  Stand          4  Walk           5  Run
    ///   13 WalkBackwards  37 JumpStart      38 Jump
    ///   39 JumpEnd        40 Fall
    ///
    /// 13 lands first: vanilla has a DISTINCT backpedal speed (MOVE_RUN_BACK,
    /// 4.5 yd/s) and playing Walk while moving backwards reads as moonwalking.
    /// 37/38/39/40 are the jump chain, ready for when the controller grows
    /// gravity in M5.
    ///
    /// NOT ADDED: 11/12 ShuffleLeft/ShuffleRight. There are no strafe clips on
    /// land in vanilla — strafing reuses Walk/Run and rotates the torso. Baking
    /// them would invite the wrong implementation.
    ///
    /// TRAP for whoever wires 37/39 up: M2Sequence's 0x20 flag is NOT a loop
    /// flag. It reads CLEAR on Stand/Walk/Run, so trusting it makes every clip a
    /// one-shot that clamps and holds. Real looping lives in the repetition
    /// fields at +24/+28, which the reader skips. JumpStart(37) and JumpEnd(39)
    /// are the only one-shots; everything else loops.
    ///
    /// Cache: SkinnedGlbVersion is derived from this assembly's MVID, so editing
    /// this file regenerates every character GLB automatically. No manual bump.
    /// </remarks>
    public static readonly int[] DefaultAnimationsToBake = { 0, 4, 5, 13, 37, 38, 39, 40 };

    /// <summary>
    /// Backward-compatible entry point — bakes the default animation set.
    /// </summary>
    public static bool SaveSkinnedGlb(M2Model m2, Dictionary<int, byte[]> textures, string outputPath)
        => SaveSkinnedGlb(m2, textures, outputPath, DefaultAnimationsToBake);

    /// <summary>
    /// Save a skinned character GLB with explicit animation set. Pass an
    /// empty array to skip animation baking entirely (bind pose only).
    /// </summary>
    public static bool SaveSkinnedGlb(
            M2Model m2,
            Dictionary<int, byte[]> textures,
            string outputPath,
            IReadOnlyList<int> animationsToBake)
    {
        if (!m2.IsValid) return false;
        if (!m2.HasSkeleton) return false;   // no bones → use GlbWriter instead

        try
        {
            // ── Materials ───────────────────────────────────────────────────
            // Values may be raw BLP (straight from the MPQ) or an already-
            // decoded PNG. CharacterModelService hands us a composited PNG for
            // the body-skin slot so the CharSections face overlay is baked into
            // the GLB itself, rather than depending on a client-side texture
            // swap that may never fire.
            var materialsByTexIdx = new Dictionary<int, MaterialBuilder>();
            foreach (var (texIdx, imageData) in textures)
            {
                var pngBytes = IsPng(imageData) ? imageData : ConvertBlpToPngBytes(imageData);
                if (pngBytes == null)
                {
                    Console.WriteLine($"[SkinnedGlbWriter] texture slot {texIdx} failed to decode — slot left UNBOUND");
                    continue;
                }

                var img = new SharpGLTF.Memory.MemoryImage(pngBytes);
                var mat = new MaterialBuilder($"mat_{texIdx}")
                    .WithUnlitShader()
                    .WithBaseColor(img);
                materialsByTexIdx[texIdx] = mat;
            }

            // An unbound texture slot must NEVER silently inherit the body
            // atlas. The old fallback was materialsByTexIdx.Values.First(),
            // which is insertion-ordered and therefore always the body skin —
            // so every unresolved slot (cape = type 2, facial hair = type 7,
            // skin extra = type 8, and any type-0 whose BLP is missing from the
            // MPQ) rendered with body-skin pixels. That is the long-running
            // "hair and capes look like skin" bug, and because it rendered
            // plausibly rather than failing, it hid every upstream error.
            //
            //   * slots the client dresses at equip time render fully
            //     transparent, so the geoset is simply absent until something
            //     is equipped;
            //   * anything else renders MAGENTA so it cannot be overlooked.
            var transparentMat = new MaterialBuilder("unbound_transparent")
                .WithUnlitShader()
                .WithAlpha(SharpGLTF.Materials.AlphaMode.BLEND)
                .WithChannelParam(KnownChannel.BaseColor, KnownProperty.RGBA,
                    new Vector4(0f, 0f, 0f, 0f));

            var unboundMat = new MaterialBuilder("unbound_missing")
                .WithUnlitShader()
                .WithChannelParam(KnownChannel.BaseColor, KnownProperty.RGBA,
                    new Vector4(1f, 0f, 1f, 1f));

            var fallbackMat = new MaterialBuilder("default")
                .WithUnlitShader()
                .WithChannelParam(KnownChannel.BaseColor, KnownProperty.RGBA,
                    new Vector4(0.7f, 0.7f, 0.7f, 1f));

            // ── Bone armature ───────────────────────────────────────────────
            var boneNodes = BuildBoneArmature(m2);

            for (int i = 0; i < boneNodes.Length; i++)
            {
                if (boneNodes[i] == null)
                    throw new InvalidOperationException($"Bone {i} is null after BuildBoneArmature");
            }
            int m2Roots = 0;
            for (int i = 0; i < m2.Bones.Count; i++)
                if (m2.Bones[i].ParentBone == -1) m2Roots++;
            Console.WriteLine($"[SkinnedGlbWriter] {m2.Bones.Count} bones, {m2Roots} M2-root(s) under synthetic Armature, " +
                              $"{m2.Attachments.Count} attachments, {m2.Sequences.Count} sequences available");

            // ── Animations (Session O) ──────────────────────────────────────
            // Bake the requested subset onto the existing boneNodes. SharpGLTF
            // collects the animation tracks set on each NodeBuilder when the
            // SceneBuilder is finalized via ToGltf2(), so this needs to
            // happen before that call.
            int animsBaked = EmitAnimations(m2, boneNodes, animationsToBake);
            int globalsBaked = EmitGlobalSequences(m2, boneNodes);
            Console.WriteLine($"[SkinnedGlbWriter] baked {animsBaked}/{animationsToBake.Count} requested animations + {globalsBaked} global loops");

            // ── Mesh ────────────────────────────────────────────────────────
            var allGeoIds = m2.Submeshes.Select(s => s.Id).ToList();
            var catSummary = allGeoIds.GroupBy(id => id / 100)
                .OrderBy(g => g.Key)
                .Select(g => $"cat{g.Key}=[{string.Join(",", g.Select(id => id.ToString()))}]");
            Console.WriteLine($"[SkinnedGlbWriter] {m2.Submeshes.Count} submeshes, geoset IDs: {string.Join(" ", catSummary)}");

            // Texture-table dump. One line, every slot, so an unbound slot is
            // obvious from the log without attaching a debugger.
            for (int i = 0; i < m2.Textures.Count; i++)
            {
                Console.WriteLine(
                    $"[SkinnedGlbWriter] texslot {i}: Type={m2.Textures[i].Type} " +
                    $"Flags={m2.Textures[i].Flags} File='{m2.Textures[i].Filename}' " +
                    $"bound={materialsByTexIdx.ContainsKey(i)}");
            }

            var scene = new SceneBuilder("scene");
            var submeshTexture = BuildSubmeshTextureMap(m2);
            var seenMeshNames = new HashSet<string>();

            for (int subIdx = 0; subIdx < m2.Submeshes.Count; subIdx++)
            {
                var submesh = m2.Submeshes[subIdx];
                if (submesh.IndexCount == 0 || submesh.IndexCount % 3 != 0) continue;

                int texIdx = submeshTexture.ContainsKey(subIdx) ? submeshTexture[subIdx] : subIdx;

                MaterialBuilder mat;
                if (materialsByTexIdx.TryGetValue(texIdx, out var boundMat))
                {
                    mat = boundMat;
                }
                else if (materialsByTexIdx.Count == 0)
                {
                    mat = fallbackMat;
                }
                else
                {
                    uint slotType = (texIdx >= 0 && texIdx < m2.Textures.Count)
                        ? m2.Textures[texIdx].Type : 0u;
                    // 2 = OBJECT_SKIN (cape/item), 7 = CHAR_FACIAL_HAIR, 8 = SKIN_EXTRA
                    bool clientFilled = slotType == 2 || slotType == 7 || slotType == 8;
                    mat = clientFilled ? transparentMat : unboundMat;
                    Console.WriteLine(
                        $"[SkinnedGlbWriter] submesh {subIdx} (geoset {submesh.Id}) → texture slot {texIdx} " +
                        $"(M2 type {slotType}) UNBOUND → {(clientFilled ? "transparent" : "MAGENTA")}");
                }

                int geosetId = submesh.Id;
                int geosetCategory = geosetId / 100;
                int geosetVariant = geosetId % 100;
                string meshName = $"Geoset_{geosetId}_c{geosetCategory}_v{geosetVariant}_s{subIdx}";

                if (!seenMeshNames.Add(meshName))
                {
                    Console.WriteLine($"[SkinnedGlbWriter] mesh name collision '{meshName}' — should be impossible");
                }

                var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(meshName);
                var prim = meshBuilder.UsePrimitive(mat);

                for (int i = submesh.IndexStart;
                     i + 2 < submesh.IndexStart + submesh.IndexCount;
                     i += 3)
                {
                    if (i + 2 >= m2.Indices.Count) break;
                    int i0 = m2.Indices[i], i1 = m2.Indices[i + 1], i2 = m2.Indices[i + 2];
                    if (i0 >= m2.Vertices.Count || i1 >= m2.Vertices.Count || i2 >= m2.Vertices.Count)
                        continue;

                    prim.AddTriangle(
                        MakeSkinnedVertex(m2.Vertices[i0], m2.Bones.Count),
                        MakeSkinnedVertex(m2.Vertices[i1], m2.Bones.Count),
                        MakeSkinnedVertex(m2.Vertices[i2], m2.Bones.Count));
                }

                // Explicit inverse bind matrices: T(-pivot) per bone, exactly the reference animator's
                // "the inverse bind is free" rule. Letting SharpGLTF derive them from the joints'
                // rest world matrices would fold the static global-sequence scale/rotation baked into
                // the rest pose (BuildBoneArmature) back OUT of the skinned body — the female hand
                // bone's 0.85 would shrink the pauldron on the attachment but not the hand under it,
                // which is not what the client draws.
                var joints = new (NodeBuilder Joint, Matrix4x4 InverseBindMatrix)[boneNodes.Length];
                for (int j = 0; j < boneNodes.Length; j++)
                    joints[j] = (boneNodes[j], Matrix4x4.CreateTranslation(-m2.Bones[j].Pivot));
                scene.AddSkinnedMesh(meshBuilder, joints);
            }

            EmitAttachments(m2, boneNodes);

            var model = scene.ToGltf2();
            model.SaveGLB(outputPath);
            return true;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[SkinnedGlbWriter] save failed: {ex.Message}");
            Console.WriteLine(ex.StackTrace);
            return false;
        }
    }
    // ────────────────────────────────────────────────────────────────────────
    // Bone armature
    // ────────────────────────────────────────────────────────────────────────
    //
    // We build a synthetic "Armature" root NodeBuilder under which the M2's
    // own root bones hang. Standard glTF skinning convention — SharpGLTF's
    // AddSkinnedMesh expects the joints array to share a common parent.
    //
    // Each bone node's LOCAL translation = bone.Pivot - parent.Pivot. The
    // identity rotation and unit scale are implicit. Animation tracks
    // (Session O) replace these at runtime when a clip is playing.
    private static NodeBuilder[] BuildBoneArmature(M2Model m2)
    {
        string armatureName = m2.DefaultHairGeosetId >= 0
            ? $"Armature_HairGeoset_{m2.DefaultHairGeosetId}"
            : "Armature";
        var armatureRoot = new NodeBuilder(armatureName);
        var nodes = new NodeBuilder[m2.Bones.Count];

        for (int i = 0; i < m2.Bones.Count; i++)
        {
            var bone = m2.Bones[i];
            int parentIdx = bone.ParentBone;
            bool hasValidParent = parentIdx >= 0 && parentIdx < i && nodes[parentIdx] != null;

            // Carry the M2 key-bone id in the node NAME so the browser can find
            // the strafe twist bones at runtime (SpineLow = 4, Waist = 5) without
            // needing a glTF `extras` object — SharpGLTF's NodeBuilder.Extras is
            // version-dependent, the name is not. Only bones that ARE a key bone
            // get the suffix (KeyBoneId >= 0); the rest stay plain "Bone_{i}" so
            // the animation-track node names three.js derives never carry a
            // hyphen. character.js parses /_k(\d+)$/ off THREE.Bone.name.
            string boneName = bone.KeyBoneId >= 0 ? $"Bone_{i}_k{bone.KeyBoneId}" : $"Bone_{i}";

            NodeBuilder node;
            Vector3 restTranslation = hasValidParent ? bone.Pivot - m2.Bones[parentIdx].Pivot : bone.Pivot;

            // STATIC GLOBAL-SEQUENCE KEYS ARE THE REST POSE. A track parked on a global sequence with
            // a single key never changes — the client evaluates global sequences continuously, so
            // that key is simply always in effect. The clip exporter below only writes per-sequence
            // keys and multi-key global loops, so these constants used to vanish. That is where the
            // race/gender proportions of attached gear live: HumanFemale.m2 scales its shoulder
            // attachment bones to 0.62 and its right-hand bone to 0.85 exactly this way (HumanMale has
            // none), so without this every female wore male-sized pauldrons and a male-sized sword.
            // Rotation and translation get the same treatment for consistency; a clip that animates
            // the bone replaces the value, a clip that does not leaves it in force.
            if (bone.Translation.GlobalSequence >= 0 && bone.Translation.Keys.Count == 1)
                restTranslation += bone.Translation.Keys[0];

            node = hasValidParent ? nodes[parentIdx].CreateNode(boneName) : armatureRoot.CreateNode(boneName);
            node.WithLocalTranslation(restTranslation);
            if (bone.Rotation.GlobalSequence >= 0 && bone.Rotation.Keys.Count == 1)
            {
                var r = bone.Rotation.Keys[0];
                node.WithLocalRotation(NormalizeQuaternion(new Quaternion(r.X, r.Y, r.Z, r.W)));
            }
            if (bone.Scale.GlobalSequence >= 0 && bone.Scale.Keys.Count == 1)
                node.WithLocalScale(bone.Scale.Keys[0]);

            nodes[i] = node;
        }

        return nodes;
    }

    // ────────────────────────────────────────────────────────────────────────
    // Animation baking (Session O)
    // ────────────────────────────────────────────────────────────────────────
    //
    // For each animationId in `animationsToBake`:
    //   1. Resolve to a sequence index via TryFindSequenceIndexByAnimationId.
    //   2. Skip if not found or duration is 0.
    //   3. Pick a human-friendly clip name (e.g. "Stand").
    //   4. For each bone:
    //      a. If translation track has keys for this sequence, enumerate them
    //         and emit via node.UseTranslation(clipName).WithPoint(
    //            timeSec, restTranslation + M2_translation_value).
    //      b. Rotation: emit via node.UseRotation(clipName).WithPoint(
    //            timeSec, normalize(quat)).
    //      c. Scale: emit via node.UseScale(clipName).WithPoint(
    //            timeSec, M2_scale_value).
    //
    // Edge cases:
    //   - A bone with no keys for this sequence: emit nothing (the glTF node
    //     falls back to its rest TRS — what we want).
    //   - A bone whose translation has keys but rotation doesn't: handled
    //     per-track independently.
    //   - Single-keyframe tracks: emitted as a single point at t=0; glTF
    //     interpolation will hold the value (constant offset for the clip).
    //
    // SharpGLTF API notes:
    //   - node.UseTranslation(string name) returns CurveBuilder<Vector3>
    //   - node.UseRotation(string name) returns CurveBuilder<Quaternion>
    //   - node.UseScale(string name) returns CurveBuilder<Vector3>
    //   - .WithPoint(float seconds, T value) appends one keyframe
    //   - The string `name` becomes the glTF Animation name; multiple calls
    //     with the same name accumulate into the same animation, while
    //     different names produce different animations. This is exactly the
    //     multi-clip surface we need.
    //
    // Returns the count of animations actually baked (some requested IDs may
    // be missing — e.g. a Tauren M2 missing a particular sleep variant).
    private static int EmitAnimations(M2Model m2, NodeBuilder[] boneNodes, IReadOnlyList<int> animationsToBake)
    {
        if (animationsToBake.Count == 0) return 0;

        int baked = 0;
        foreach (int animId in animationsToBake)
        {
            int seqIdx = m2.TryFindSequenceIndexByAnimationId(animId);
            if (seqIdx < 0)
            {
                Console.WriteLine($"[SkinnedGlbWriter] animation {animId} not present in M2 — skipping");
                continue;
            }

            var sequence = m2.Sequences[seqIdx];
            if (sequence.DurationMs == 0)
            {
                Console.WriteLine($"[SkinnedGlbWriter] animation {animId} (seqIdx={seqIdx}) has zero duration — skipping");
                continue;
            }

            string clipName = AnimationName(animId);
            int bonesTouched = 0;

            // Session P: switched from index-based ranges to absolute timestamp
            // windows. Each sequence's keyframes are picked out of the shared
            // Timestamps array by `t ∈ [startMs, endMs]` rather than by
            // `Ranges[seqIdx]`. Empirically the M2s we parse leave Ranges as
            // (0, Timestamps.Count-1) for every sequence — so the old code
            // emitted the entire 600+ second shared timeline for every clip
            // and Three.js looped at the wrong interval.
            uint startMs = sequence.StartTimestamp;
            uint endMs = sequence.EndTimestamp;
            float durSec = sequence.DurationMs / 1000f;
            bool looping = sequence.IsLooping;

            for (int boneIdx = 0; boneIdx < m2.Bones.Count; boneIdx++)
            {
                var bone = m2.Bones[boneIdx];
                var node = boneNodes[boneIdx];

                // Diagnostic: log eye bone track status for bones 83/84
                bool isEyeBone = (boneIdx == 83 || boneIdx == 84);
                if (isEyeBone)
                {
                    Console.WriteLine($"[SkinnedGlbWriter] EYE BONE {boneIdx} in {clipName}: " +
                        $"trans(ts={bone.Translation.Timestamps.Count} keys={bone.Translation.Keys.Count} globalSeq={bone.Translation.GlobalSequence}) " +
                        $"rot(ts={bone.Rotation.Timestamps.Count} keys={bone.Rotation.Keys.Count} globalSeq={bone.Rotation.GlobalSequence}) " +
                        $"scale(ts={bone.Scale.Timestamps.Count} keys={bone.Scale.Keys.Count} globalSeq={bone.Scale.GlobalSequence}) " +
                        $"seqWindow=[{startMs}..{endMs}]");
                    if (bone.Rotation.Timestamps.Count > 0)
                    {
                        var allTs = bone.Rotation.Timestamps;
                        Console.WriteLine($"[SkinnedGlbWriter]   rot timestamps range: [{allTs.Min()}..{allTs.Max()}] count={allTs.Count}");
                    }
                }

                // Rest-pose translation for this bone (used as base for any
                // translation track — see class doc on TRS mapping).
                Vector3 restTranslation = (bone.ParentBone >= 0 && bone.ParentBone < m2.Bones.Count)
                    ? bone.Pivot - m2.Bones[bone.ParentBone].Pivot
                    : bone.Pivot;

                bool boneHadAnyTrack = false;

                // ── Translation track ───────────────────────────────────────
                //
                // Materialize the keyframes BEFORE calling UseTranslation.
                // UsesSequence returns true when the track's overall timestamp
                // range *overlaps* the sequence window, but that doesn't
                // guarantee any actual timestamp falls *inside* it. If we
                // called UseTranslation and then emitted zero keys, SharpGLTF
                // would throw "keyframes cannot be empty" at save time.
                if (bone.Translation.UsesSequence(startMs, endMs))
                {
                    var keys = bone.Translation.EnumerateSequenceKeys(startMs, endMs).ToList();
                    if (keys.Count > 0)
                    {
                        var curve = node.UseTranslation(clipName);
                        Vector3 firstValue = restTranslation + keys[0].value;
                        float lastT = 0f;
                        foreach (var (timeMs, value) in keys)
                        {
                            float t = timeMs / 1000f;
                            curve.WithPoint(t, restTranslation + value);
                            lastT = t;
                        }
                        // Pin a closing keyframe at exactly durSec so the
                        // clip's max-keyframe-time equals the authored
                        // duration (glTF derives AnimationClip.duration from
                        // that) and the loop wraps cleanly back to the
                        // starting pose. Skip when the last authored key is
                        // already at/past durSec — SharpGLTF's CurveBuilder
                        // rejects duplicate timestamps.
                        if (durSec > 0f && lastT < durSec - 1e-4f)
                            curve.WithPoint(durSec, firstValue);
                        boneHadAnyTrack = true;
                    }
                }

                // ── Rotation track ──────────────────────────────────────────
                if (bone.Rotation.UsesSequence(startMs, endMs))
                {
                    var keys = bone.Rotation.EnumerateSequenceKeys(startMs, endMs).ToList();
                    if (keys.Count > 0)
                    {
                        var curve = node.UseRotation(clipName);
                        // M2AnimTrack<Vector4> stores quat components as
                        // (x, y, z, w) post-fix_quaternion. Normalize before
                        // emitting — vanilla data is mostly unit but
                        // accumulated float error can creep in, and glTF
                        // requires unit quaternions for rotation channels.
                        Quaternion firstQuat = NormalizeQuaternion(new Quaternion(
                            keys[0].value.X, keys[0].value.Y, keys[0].value.Z, keys[0].value.W));
                        float lastT = 0f;
                        foreach (var (timeMs, value) in keys)
                        {
                            float t = timeMs / 1000f;
                            var q = NormalizeQuaternion(new Quaternion(value.X, value.Y, value.Z, value.W));
                            curve.WithPoint(t, q);
                            lastT = t;
                        }
                        if (durSec > 0f && lastT < durSec - 1e-4f)
                            curve.WithPoint(durSec, firstQuat);
                        boneHadAnyTrack = true;
                    }
                }

                // ── Scale track ─────────────────────────────────────────────
                if (bone.Scale.UsesSequence(startMs, endMs))
                {
                    var keys = bone.Scale.EnumerateSequenceKeys(startMs, endMs).ToList();
                    if (keys.Count > 0)
                    {
                        var curve = node.UseScale(clipName);
                        Vector3 firstScale = keys[0].value;
                        float lastT = 0f;
                        foreach (var (timeMs, value) in keys)
                        {
                            float t = timeMs / 1000f;
                            curve.WithPoint(t, value);
                            lastT = t;
                        }
                        if (durSec > 0f && lastT < durSec - 1e-4f)
                            curve.WithPoint(durSec, firstScale);
                        boneHadAnyTrack = true;
                    }
                }

                if (boneHadAnyTrack) bonesTouched++;
            }

            baked++;
            Console.WriteLine($"[SkinnedGlbWriter]   ✓ {clipName} (animId={animId}, seqIdx={seqIdx}, dur={sequence.DurationMs}ms, " +
                              $"looping={sequence.IsLooping}, animatedBones={bonesTouched}/{m2.Bones.Count})");
        }

        return baked;
    }

    /// <summary>
    /// Bake M2 global sequences as separate clips. The browser plays these
    /// alongside Stand/Walk/Run, preserving independent loops such as blinking.
    /// </summary>
    internal static int EmitGlobalSequences(M2Model m2, NodeBuilder[] boneNodes)
    {
        int baked = 0;
        for (int globalIdx = 0; globalIdx < m2.GlobalSequenceDurations.Count; globalIdx++)
        {
            uint durationMs = m2.GlobalSequenceDurations[globalIdx];
            if (durationMs == 0) continue;

            string clipName = $"GlobalSequence_{globalIdx}";
            float durationSec = durationMs / 1000f;
            int tracksEmitted = 0;

            for (int boneIdx = 0; boneIdx < m2.Bones.Count; boneIdx++)
            {
                var bone = m2.Bones[boneIdx];
                var node = boneNodes[boneIdx];
                Vector3 restTranslation = bone.ParentBone >= 0 && bone.ParentBone < m2.Bones.Count
                    ? bone.Pivot - m2.Bones[bone.ParentBone].Pivot
                    : bone.Pivot;

                var translationKeys = bone.Translation.EnumerateGlobalKeys(globalIdx)
                    .Where(k => k.timeMs <= durationMs).ToList();
                if (translationKeys.Count > 0)
                {
                    var curve = node.UseTranslation(clipName);
                    Vector3 first = restTranslation + translationKeys[0].value;
                    if (translationKeys[0].timeMs > 0) curve.WithPoint(0, first);
                    foreach (var (timeMs, value) in translationKeys)
                        curve.WithPoint(timeMs / 1000f, restTranslation + value);
                    if (translationKeys[^1].timeMs < durationMs)
                        curve.WithPoint(durationSec, first);
                    tracksEmitted++;
                }

                var rotationKeys = bone.Rotation.EnumerateGlobalKeys(globalIdx)
                    .Where(k => k.timeMs <= durationMs).ToList();
                if (rotationKeys.Count > 0)
                {
                    var curve = node.UseRotation(clipName);
                    Quaternion first = NormalizeQuaternion(new Quaternion(
                        rotationKeys[0].value.X, rotationKeys[0].value.Y,
                        rotationKeys[0].value.Z, rotationKeys[0].value.W));
                    if (rotationKeys[0].timeMs > 0) curve.WithPoint(0, first);
                    foreach (var (timeMs, value) in rotationKeys)
                        curve.WithPoint(timeMs / 1000f, NormalizeQuaternion(new Quaternion(
                            value.X, value.Y, value.Z, value.W)));
                    if (rotationKeys[^1].timeMs < durationMs)
                        curve.WithPoint(durationSec, first);
                    tracksEmitted++;
                }

                var scaleKeys = bone.Scale.EnumerateGlobalKeys(globalIdx)
                    .Where(k => k.timeMs <= durationMs).ToList();
                if (scaleKeys.Count > 0)
                {
                    var curve = node.UseScale(clipName);
                    Vector3 first = scaleKeys[0].value;
                    if (scaleKeys[0].timeMs > 0) curve.WithPoint(0, first);
                    foreach (var (timeMs, value) in scaleKeys)
                        curve.WithPoint(timeMs / 1000f, value);
                    if (scaleKeys[^1].timeMs < durationMs)
                        curve.WithPoint(durationSec, first);
                    tracksEmitted++;
                }
            }

            if (tracksEmitted > 0)
            {
                baked++;
                Console.WriteLine($"[SkinnedGlbWriter]   global {globalIdx}: {durationMs}ms, {tracksEmitted} track(s)");
            }
        }

        return baked;
    }

    private static Quaternion NormalizeQuaternion(Quaternion q)
    {
        float lenSq = q.X * q.X + q.Y * q.Y + q.Z * q.Z + q.W * q.W;
        if (lenSq < 1e-8f) return Quaternion.Identity;
        float invLen = 1f / MathF.Sqrt(lenSq);
        return new Quaternion(q.X * invLen, q.Y * invLen, q.Z * invLen, q.W * invLen);
    }

    /// <summary>
    /// Map a vanilla AnimationData.dbc ID to a human-readable clip name.
    /// Three.js's AnimationMixer uses these names to identify clips
    /// (mixer.clipAction(scene.animations.find(c => c.name === "Stand"))).
    ///
    /// Only the IDs we might bake are listed — falls back to "AnimN" for
    /// unknowns so future additions don't silently produce unnamed clips.
    ///
    /// Source: AnimationData.dbc (vanilla 1.12, build 5875). The mapping
    /// is also shipped client-side as `animation-names.js` so the UI
    /// dropdown can present names for any clips the server bakes without
    /// needing a roundtrip to discover what's available.
    /// </summary>
    private static string AnimationName(int animationId) => animationId switch
    {
        0 => "Stand",
        1 => "Death",
        2 => "Spell",
        3 => "Stop",
        4 => "Walk",
        5 => "Run",
        6 => "Dead",
        7 => "Rise",
        8 => "StandWound",
        9 => "CombatWound",
        10 => "CombatCritical",
        11 => "ShuffleLeft",
        12 => "ShuffleRight",
        13 => "Walkbackwards",
        14 => "Stun",
        15 => "HandsClosed",
        16 => "AttackUnarmed",
        17 => "Attack1H",
        18 => "Attack2H",
        19 => "Attack2HL",
        20 => "ParryUnarmed",
        21 => "Parry1H",
        22 => "Parry2H",
        23 => "Parry2HL",
        24 => "ShieldBlock",
        25 => "ReadyUnarmed",
        26 => "Ready1H",
        27 => "Ready2H",
        28 => "Ready2HL",
        29 => "ReadyBow",
        30 => "Dodge",
        37 => "JumpStart",
        38 => "Jump",
        39 => "JumpEnd",
        40 => "Fall",
        _ => $"Anim{animationId}",
    };

    // ────────────────────────────────────────────────────────────────────────
    // Attachment node emission (Session L)
    // ────────────────────────────────────────────────────────────────────────
    //
    // M2 attachment.Position is in MODEL SPACE. To get bone-local translation
    // (what NodeBuilder.WithLocalTranslation wants) we subtract the parent
    // bone's pivot. See Session L handoff for full rationale.
    //
    // Animation note (Session O): once the bone armature is animating, the
    // attachment nodes ride along automatically because they're parented
    // under the bone nodes. No per-attachment animation work required —
    // weapons/helms/shoulders inherit their bone's animated transform.
    private static void EmitAttachments(M2Model m2, NodeBuilder[] boneNodes)
    {
        foreach (var att in m2.Attachments)
        {
            if (att.BoneIndex >= boneNodes.Length) continue;
            if (att.BoneIndex >= m2.Bones.Count) continue;

            var parent = boneNodes[(int)att.BoneIndex];
            var bonePivot = m2.Bones[(int)att.BoneIndex].Pivot;

            var localPos = att.Position - bonePivot;

            var node = parent.CreateNode($"Attachment_{att.Id}");
            node.WithLocalTranslation(localPos);
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Item rig (Thunderfury)
    // ────────────────────────────────────────────────────────────────────────
    //
    // An ITEM M2 is normally written by GlbWriter as a rigid GLB: no skin, no joints, no bone
    // animation. That is correct for a plate helm and wrong for the small class of models whose
    // authored motion lives entirely in the SKELETON rather than in a material track, a UV
    // transform, a particle emitter or an ItemVisual.
    //
    // Thunderfury (Sword_2H_Ashbringer02.mdx, displayId 30606) is the canonical case:
    //
    //   - its lightning fins (submeshes 0-6) are weighted to bones 21-28, whose translation and
    //     rotation ride M2 GLOBAL SEQUENCES 10/11/13 - independent loops that run forever with no
    //     AnimationData sequence selected;
    //   - its glow shell and LIGHTNINGBALL orb (submeshes 7-8) are weighted to bones 0-4, which
    //     carry flags 0x208 - bit 0x08 is a camera-facing (billboard) mode.
    //
    // Exported rigid, the material manifest still pulsed the fins' alpha (that part always worked)
    // while the geometry stayed nailed to the rest pose, and the orb rendered as a frozen card
    // facing whatever direction the artist happened to author. Both halves of that are skeleton
    // behaviour the rigid writer had no way to carry.
    //
    // === Output shape ===
    //
    //   ItemArmature                      <- distinct from the character "Armature" ON PURPOSE:
    //   +-- Bone_0                           m2fx.js keys the item mixer off this name so it never
    //   |   +-- M2Billboard_0_f520           starts a second mixer on the character body, whose
    //   |        +-- Bone_9 ...              GlobalSequence_* clips animation-control.js drives
    //   +-- Bone_21 ...
    //   +-- ...
    //
    // Animation lands on Bone_i. The M2Billboard_i child stays at identity in the file and is the
    // node the browser rewrites each frame, so THREE.AnimationMixer and the camera-facing law never
    // fight over the same quaternion channel. The billboard child is also the SKIN JOINT for that
    // bone, and descendant bones hang beneath it - which is what makes a descendant inherit the
    // rewritten parent orientation, matching SpellMeshSkinningLaw's `parentChanged` propagation.

    /// <summary>Name of the item rig's root node. The browser gates its item mixer on this.</summary>
    internal const string ItemArmatureName = "ItemArmature";

    /// <summary>Bone flag: drop the parent's rotation from this bone's frame.</summary>
    internal const uint IgnoreParentRotation = 0x04;
    /// <summary>Bone flags 0x08/0x10/0x20/0x40: the four M2 billboard modes.</summary>
    internal const uint BillboardMask = 0x78;
    /// <summary>Any bit here means the client rewrites this bone's orientation against the camera.</summary>
    internal const uint CameraFacingMask = IgnoreParentRotation | BillboardMask;

    /// <summary>An item M2's skinning rig: authored bone nodes, skin joints, and their root.</summary>
    internal sealed class ItemRig
    {
        /// <summary>The "ItemArmature" node every bone hangs under.</summary>
        public NodeBuilder Root = null!;
        /// <summary>Per M2 bone: the node that carries its animation tracks.</summary>
        public NodeBuilder[] Bones = Array.Empty<NodeBuilder>();
        /// <summary>Per M2 bone: the node the skin binds to - the billboard child when it has one.</summary>
        public NodeBuilder[] Joints = Array.Empty<NodeBuilder>();
        /// <summary>How many bones got a camera-facing correction child.</summary>
        public int BillboardCount;
    }

    /// <summary>
    /// Does this model's VISIBLE geometry actually depend on bone behaviour a rigid GLB cannot
    /// carry? Only then is it worth paying for a skin.
    ///
    /// True when some emitted vertex has a non-zero influence on a bone that - itself or through
    /// an ancestor - either faces the camera or rides a live global sequence. Everything else
    /// (every ordinary weapon, helm, spaulder, prop and GameObject in the catalogue) stays on the
    /// rigid fast path, which is both the cheap path and the one already proven on those models.
    ///
    /// The ancestor walk matters: submeshes 9-10 of Thunderfury sit on static bone 5, but a fin
    /// weighted to a child of an animated bone still moves with it.
    /// </summary>
    internal static bool RequiresItemSkin(M2Model m2)
    {
        int n = m2.Bones.Count;
        if (n == 0 || m2.Vertices.Count == 0) return false;

        // Pass 1 - bones that are interesting in their own right.
        var direct = new bool[n];
        bool anyDirect = false;
        for (int i = 0; i < n; i++)
        {
            var bone = m2.Bones[i];
            if ((bone.Flags & CameraFacingMask) != 0 ||
                HasLiveGlobalTrack(m2, bone.Translation) ||
                HasLiveGlobalTrack(m2, bone.Rotation) ||
                HasLiveGlobalTrack(m2, bone.Scale))
            {
                direct[i] = true;
                anyDirect = true;
            }
        }
        if (!anyDirect) return false;

        // Pass 2 - close over ancestry. Depth-capped rather than cycle-checked: a malformed model
        // that parents a bone to its own descendant must not spin here.
        var inherited = new bool[n];
        for (int i = 0; i < n; i++)
        {
            int bone = i;
            for (int depth = 0; depth < n && bone >= 0 && bone < n; depth++)
            {
                if (direct[bone]) { inherited[i] = true; break; }
                bone = m2.Bones[bone].ParentBone;
            }
        }

        // Pass 3 - only vertices this writer actually emits count. A bone graph nothing draws from
        // is not a reason to change how the model is written.
        foreach (int vi in EmittedVertexIndices(m2))
        {
            var v = m2.Vertices[vi];
            if (v.BoneWeight0 != 0 && Influenced(inherited, v.BoneIndex0)) return true;
            if (v.BoneWeight1 != 0 && Influenced(inherited, v.BoneIndex1)) return true;
            if (v.BoneWeight2 != 0 && Influenced(inherited, v.BoneIndex2)) return true;
            if (v.BoneWeight3 != 0 && Influenced(inherited, v.BoneIndex3)) return true;

            // The shared skin contract (ResolveJoints) assigns a zero-total vertex fully to bone 0.
            if (v.BoneWeight0 == 0 && v.BoneWeight1 == 0 && v.BoneWeight2 == 0 && v.BoneWeight3 == 0
                && inherited[0]) return true;
        }

        return false;
    }

    private static bool Influenced(bool[] inherited, byte boneIndex)
        => boneIndex < inherited.Length && inherited[boneIndex];

    /// <summary>
    /// A track that <see cref="EmitGlobalSequences"/> would actually turn into a clip: bound to a
    /// declared global loop, that loop has a non-zero duration, and the key/timestamp arrays agree.
    /// </summary>
    private static bool HasLiveGlobalTrack<T>(M2Model m2, M2AnimTrack<T> track) where T : struct
        => track.GlobalSequence >= 0
        && track.GlobalSequence < m2.GlobalSequenceDurations.Count
        && m2.GlobalSequenceDurations[track.GlobalSequence] > 0
        && track.Timestamps.Count > 0
        && track.Timestamps.Count == track.Keys.Count;

    /// <summary>
    /// The global vertex ids GlbWriter's submesh loop will emit, in the same shape that loop uses.
    ///
    /// M2Submesh.VertexStart/VertexCount index the SKIN's local vertex-lookup table, not
    /// <see cref="M2Model.Vertices"/> - M2Reader has already resolved the index buffer through that
    /// lookup, so the index range is the only honest source of "which vertices get drawn".
    /// </summary>
    private static IEnumerable<int> EmittedVertexIndices(M2Model m2)
    {
        var ranges = new List<(int start, int count)>();
        if (m2.Submeshes.Count > 0)
        {
            foreach (var sub in m2.Submeshes)
            {
                if (m2.Submeshes.Count > 1 && (sub.IndexCount == 0 || sub.IndexCount % 3 != 0)) continue;
                ranges.Add((sub.IndexStart, sub.IndexCount));
            }
        }
        else
        {
            ranges.Add((0, m2.Indices.Count - (m2.Indices.Count % 3)));
        }

        var seen = new HashSet<int>();
        foreach (var (start, count) in ranges)
        {
            for (int i = start; i < start + count && i < m2.Indices.Count; i++)
            {
                int vi = m2.Indices[i];
                if (vi < m2.Vertices.Count && seen.Add(vi)) yield return vi;
            }
        }
    }

    /// <summary>
    /// Build the item armature described in the block comment above.
    ///
    /// Pivot and hierarchy rules are <see cref="BuildBoneArmature"/>'s, unchanged: a root bone's
    /// local translation is its pivot, a child's is its pivot minus its parent's. The two
    /// differences are the correction children and the parent-first walk - the character builder
    /// requires parentIndex &lt; boneIndex and silently re-roots a bone that forward-references its
    /// parent, which on an item would move geometry that used to render correctly as a rigid mesh.
    /// </summary>
    internal static ItemRig BuildItemRig(M2Model m2)
    {
        int n = m2.Bones.Count;
        var rig = new ItemRig
        {
            Root = new NodeBuilder(ItemArmatureName),
            Bones = new NodeBuilder[n],
            Joints = new NodeBuilder[n],
        };

        foreach (int i in ParentFirstOrder(m2))
        {
            var bone = m2.Bones[i];
            int parentIdx = bone.ParentBone;
            bool hasParent = parentIdx >= 0 && parentIdx < n && parentIdx != i && rig.Joints[parentIdx] != null;

            // Parent under the parent's CORRECTION node when it has one, so a descendant inherits
            // the rewritten orientation the way the client's palette does.
            var anchor = hasParent ? rig.Joints[parentIdx] : rig.Root;
            var node = anchor.CreateNode($"Bone_{i}");
            node.WithLocalTranslation(hasParent ? bone.Pivot - m2.Bones[parentIdx].Pivot : bone.Pivot);
            rig.Bones[i] = node;

            if ((bone.Flags & CameraFacingMask) != 0)
            {
                // Identity in the file; the browser writes its local matrix each frame. The flags
                // ride in the name because that survives every SharpGLTF/GLTFLoader round-trip we
                // already rely on, which NodeBuilder.Extras does not.
                rig.Joints[i] = node.CreateNode($"M2Billboard_{i}_f{bone.Flags}");
                rig.BillboardCount++;
            }
            else
            {
                rig.Joints[i] = node;
            }
        }

        // A cycle would leave holes. Root the survivors rather than handing GlbWriter a null joint.
        for (int i = 0; i < n; i++)
        {
            if (rig.Joints[i] != null) continue;
            var node = rig.Root.CreateNode($"Bone_{i}");
            node.WithLocalTranslation(m2.Bones[i].Pivot);
            rig.Bones[i] = node;
            rig.Joints[i] = node;
        }

        return rig;
    }

    /// <summary>
    /// Bone indices in an order where every bone follows its parent, tolerating forward references
    /// and stopping cleanly on a cycle. Mirrors SpellMeshSkinningLaw.ParentFirstOrder.
    /// </summary>
    private static IEnumerable<int> ParentFirstOrder(M2Model m2)
    {
        int n = m2.Bones.Count;
        var emitted = new bool[n];
        for (int pass = 0; pass < n; pass++)
        {
            bool progress = false;
            for (int i = 0; i < n; i++)
            {
                if (emitted[i]) continue;
                int parent = m2.Bones[i].ParentBone;
                if (parent >= 0 && parent < n && parent != i && !emitted[parent]) continue;
                emitted[i] = true;
                progress = true;
                yield return i;
            }
            if (!progress) break;
        }
    }

    // ────────────────────────────────────────────────────────────────────────
    // Vertex construction
    // ────────────────────────────────────────────────────────────────────────
    private static SKIN_VERTEX MakeSkinnedVertex(M2Vertex v, int boneCount)
    {
        var pos = new VertexPositionNormal(
            new Vector3(v.PosX, v.PosY, v.PosZ),
            new Vector3(v.NormX, v.NormY, v.NormZ));

        var uv = new VertexTexture1(new Vector2(v.TexU, v.TexV));

        return new SKIN_VERTEX(pos, uv, ResolveJoints(v, boneCount));
    }

    /// <summary>
    /// The one skin-weight policy, shared by the character path above and the item rig that
    /// <see cref="GlbWriter"/> builds for camera-facing / global-sequence item models.
    ///
    /// Normalize the four authored bytes by their ACTUAL total, and give a zero-total vertex full
    /// weight on bone 0. That is <c>SpellMeshSkinningLaw.Resolve</c> in MSUIClient, which is the
    /// proven client behaviour. Dividing each byte by 255 — what this used to do — agrees only
    /// when the total happens to be exactly 255; a vertex whose weights sum to zero came out with
    /// four zero weights, which three.js skins to the origin rather than to bone 0.
    /// </summary>
    internal static VertexJoints4 ResolveJoints(M2Vertex v, int boneCount)
    {
        int last = Math.Max(0, boneCount - 1);
        int b0 = Math.Clamp((int)v.BoneIndex0, 0, last);
        int b1 = Math.Clamp((int)v.BoneIndex1, 0, last);
        int b2 = Math.Clamp((int)v.BoneIndex2, 0, last);
        int b3 = Math.Clamp((int)v.BoneIndex3, 0, last);

        float total = v.BoneWeight0 + v.BoneWeight1 + v.BoneWeight2 + v.BoneWeight3;
        if (total <= 0f) return new VertexJoints4((0, 1f), (0, 0f), (0, 0f), (0, 0f));

        return new VertexJoints4(
            (b0, v.BoneWeight0 / total),
            (b1, v.BoneWeight1 / total),
            (b2, v.BoneWeight2 / total),
            (b3, v.BoneWeight3 / total));
    }

    // ────────────────────────────────────────────────────────────────────────
    // Submesh → texture mapping (same as GlbWriter)
    // ────────────────────────────────────────────────────────────────────────
    private static Dictionary<int, int> BuildSubmeshTextureMap(M2Model m2)
    {
        var map = new Dictionary<int, int>();
        foreach (var batch in m2.Batches)
        {
            int subIdx = batch.SubmeshIndex;
            if (map.ContainsKey(subIdx)) continue;
            int texIdx = 0;
            if (batch.TextureIndex < m2.TextureLookup.Count)
                texIdx = m2.TextureLookup[batch.TextureIndex];
            map[subIdx] = texIdx;
        }
        return map;
    }


    /// <summary>
    /// True if the buffer starts with the 8-byte PNG signature. Lets the
    /// texture dictionary carry either raw BLP or an already-composited PNG
    /// per slot — the body-skin slot arrives as PNG with the face overlay
    /// already painted on.
    /// </summary>
    private static bool IsPng(byte[] d) =>
        d != null && d.Length >= 8 &&
        d[0] == 0x89 && d[1] == 0x50 && d[2] == 0x4E && d[3] == 0x47 &&
        d[4] == 0x0D && d[5] == 0x0A && d[6] == 0x1A && d[7] == 0x0A;

    // ────────────────────────────────────────────────────────────────────────
    // BLP → PNG (same as GlbWriter)
    // ────────────────────────────────────────────────────────────────────────
    private static byte[]? ConvertBlpToPngBytes(byte[] blpData)
    {
        try
        {
            var pixels = BlpDecoder.GetPixels(blpData, 0, out int w, out int h);

            if (w == 0 || h == 0 || pixels.Length == 0) return null;

            using var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var bitmapPixels = bitmap.GetPixels();
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmapPixels, pixels.Length);
            bitmap.NotifyPixelsChanged();

            using var pngStream = new MemoryStream();
            bitmap.Encode(pngStream, SKEncodedImageFormat.Png, 100);
            return pngStream.ToArray();
        }
        catch
        {
            return null;
        }
    }
}

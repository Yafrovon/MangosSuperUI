using System.Linq;
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SharpGLTF.Transforms;
using SkiaSharp;
using War3Net.Drawing.Blp;

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
    public static readonly int[] DefaultAnimationsToBake = { 0, 4, 5 };

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
            var materialsByTexIdx = new Dictionary<int, MaterialBuilder>();
            foreach (var (texIdx, blpData) in textures)
            {
                var pngBytes = ConvertBlpToPngBytes(blpData);
                if (pngBytes == null) continue;

                var img = new SharpGLTF.Memory.MemoryImage(pngBytes);
                var mat = new MaterialBuilder($"mat_{texIdx}")
                    .WithUnlitShader()
                    .WithBaseColor(img);
                materialsByTexIdx[texIdx] = mat;
            }

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
            Console.WriteLine($"[SkinnedGlbWriter] baked {animsBaked}/{animationsToBake.Count} requested animations");

            // ── Mesh ────────────────────────────────────────────────────────
            var scene = new SceneBuilder("scene");
            var submeshTexture = BuildSubmeshTextureMap(m2);
            var seenMeshNames = new HashSet<string>();

            for (int subIdx = 0; subIdx < m2.Submeshes.Count; subIdx++)
            {
                var submesh = m2.Submeshes[subIdx];
                if (submesh.IndexCount == 0 || submesh.IndexCount % 3 != 0) continue;

                int texIdx = submeshTexture.ContainsKey(subIdx) ? submeshTexture[subIdx] : subIdx;
                var mat = materialsByTexIdx.ContainsKey(texIdx) ? materialsByTexIdx[texIdx] :
                          materialsByTexIdx.Count > 0 ? materialsByTexIdx.Values.First() : fallbackMat;

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

                scene.AddSkinnedMesh(meshBuilder, Matrix4x4.Identity, boneNodes);
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
        var armatureRoot = new NodeBuilder("Armature");
        var nodes = new NodeBuilder[m2.Bones.Count];

        for (int i = 0; i < m2.Bones.Count; i++)
        {
            var bone = m2.Bones[i];
            int parentIdx = bone.ParentBone;
            bool hasValidParent = parentIdx >= 0 && parentIdx < i && nodes[parentIdx] != null;

            NodeBuilder node;
            if (hasValidParent)
            {
                node = nodes[parentIdx].CreateNode($"Bone_{i}");
                var parentPivot = m2.Bones[parentIdx].Pivot;
                node.WithLocalTranslation(bone.Pivot - parentPivot);
            }
            else
            {
                node = armatureRoot.CreateNode($"Bone_{i}");
                node.WithLocalTranslation(bone.Pivot);
            }

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
    // Vertex construction
    // ────────────────────────────────────────────────────────────────────────
    private static SKIN_VERTEX MakeSkinnedVertex(M2Vertex v, int boneCount)
    {
        var pos = new VertexPositionNormal(
            new Vector3(v.PosX, v.PosY, v.PosZ),
            new Vector3(v.NormX, v.NormY, v.NormZ));

        var uv = new VertexTexture1(new Vector2(v.TexU, v.TexV));

        int b0 = Math.Clamp(v.BoneIndex0, (byte)0, (byte)(boneCount - 1));
        int b1 = Math.Clamp(v.BoneIndex1, (byte)0, (byte)(boneCount - 1));
        int b2 = Math.Clamp(v.BoneIndex2, (byte)0, (byte)(boneCount - 1));
        int b3 = Math.Clamp(v.BoneIndex3, (byte)0, (byte)(boneCount - 1));

        float w0 = v.BoneWeight0 / 255f;
        float w1 = v.BoneWeight1 / 255f;
        float w2 = v.BoneWeight2 / 255f;
        float w3 = v.BoneWeight3 / 255f;

        var joints = new VertexJoints4(
            (b0, w0),
            (b1, w1),
            (b2, w2),
            (b3, w3));

        return new SKIN_VERTEX(pos, uv, joints);
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

    // ────────────────────────────────────────────────────────────────────────
    // BLP → PNG (same as GlbWriter)
    // ────────────────────────────────────────────────────────────────────────
    private static byte[]? ConvertBlpToPngBytes(byte[] blpData)
    {
        try
        {
            using var blpStream = new MemoryStream(blpData);
            var blpFile = new BlpFile(blpStream);
            var pixels = blpFile.GetPixels(0, out int w, out int h);
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
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SkiaSharp;
using War3Net.Drawing.Blp;

namespace MangosSuperUI.Services;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>;

/// <summary>
/// Converts a parsed M2Model + BLP textures into a GLB (glTF Binary) file.
/// Uses SharpGLTF Toolkit (MeshBuilder + SceneBuilder) API.
///
/// Ported from MangosSuperUI_Extractor.GlbWriter — uses SkiaSharp instead
/// of System.Drawing for Linux (server-side) compatibility.
///
/// Each submesh becomes a separate mesh in the scene (like wow.export's "Geoset0", "Geoset1")
/// with its own material/texture. This prevents SharpGLTF from merging primitives that
/// share the same material.
///
/// Triangle winding: M2 indices are emitted in native order (i0, i1, i2) — no swap needed.
/// The Z-up → Y-up coordinate transform in M2Reader already flips handedness, so the
/// indices come out in glTF's expected counter-clockwise front-face convention as-is.
///
/// === Session M, then Session M-revert ===
/// Session M added "weapon mount-offset baking" — negating the weapon
/// M2's Attachment-0 position into the scene root translation, on the
/// theory that Attachment-0 in an item M2 is the hilt mount point.
///

/// The Attachment struct owns exactly one
/// (bone, position) pair (sourced from the CHARACTER M2's attachment
/// record) and weapons render with their vertices as-authored — the
/// M2 artist already placed the hilt at the model origin. The
/// weapon's own attachments are reserved for spell-visual EFFECT
/// mount-points (glow on enchanted weapons, Effect class with
/// itemVisualEffectId), not for geometry positioning.
///
/// Empirical confirmation of the offset error: for displayId 1542
/// (Sword_1H_Short_A_02.mdx), Session M placed the weapon mesh at
/// world (-0.285, 0.899, 0.476) while the hand bone was at world
/// (-0.059, 0.904, 0.476) — a (-0.226, -0.005, 0) push that turned
/// out to be exactly the M2's Attachment-0 position, the spot the
/// glow effect would mount, not the grip.
///
/// Revert behavior: scene root = Matrix4x4.Identity. The weapon's
/// vertex origin sits at whichever character bone the client mounts
/// it under (Attachment_1 = HandRight, Attachment_2 = HandLeft).
/// Visually awkward at character rest pose (no idle-pose rotation
/// on the hand → blade points along +X out of the hand instead of
/// down/back) but mechanically correct.
///
/// The "looks awkward" issue is a separate problem: vanilla WoW
/// applies an idle animation that rotates the hand bone so the
/// weapon sits naturally. Our character GLB ships in bind-pose with
/// identity rotations, so the hand is unrotated. Fixing it is an
/// idle-animation sampling problem, not a
/// GLB-writer problem.
///
/// === Cache impact ===
/// RigidGlbVersion bump invalidates the stale Session-M GLBs. The
/// CacheVersionRegistry sweep clears them; new requests regenerate
/// with identity transform. No code-side action beyond the version
/// bump.
/// </summary>
public static class GlbWriter
{
    /// <summary>
    /// Threshold below which a submesh's static alpha is considered "near
    /// transparent" — used by the diagnostic endpoint to flag candidate
    /// submeshes for inspection. Session N initially planned to skip
    /// submeshes below this threshold but reverted that decision:we
    /// render these submeshes with the computed alpha (no skip), and
    /// dropping the geometry would also drop legitimately-faded effects
    /// like a 19%-alpha lightning halo that's supposed to be present.
    ///
    /// The actual visibility decision now lives in baseColorFactor.A,
    /// baked per-material at GLB write time. This constant is retained
    /// only for the diagnostic's "near-zero" flag and external callers
    /// that may want to do their own filtering.
    /// </summary>
    public const float SUBMESH_VISIBILITY_THRESHOLD = 0.01f;

    /// <summary>
    /// Convert a parsed M2 + textures into a GLB on disk.
    ///
    /// === doubleSided ===
    /// When true, every material in the output is marked double-sided
    /// (glTF KHR_materials.doubleSided = true → three.js renders both
    /// faces regardless of triangle winding).
    ///
    /// This is needed for armor attachment models (helms, shoulders) where
    /// vanilla M2 geometry includes single-sided thin features (spaulder
    /// hanging flaps, helm horns/wings, cloak panels) whose authored
    /// winding renders the WRONG side toward the camera after our
    /// Z-up→Y-up flip — backface culling then hides them entirely.
    /// Session L empirical evidence (LShoulder_Plate_RaidPaladin_A_01):
    /// the upper "wing" portion is double-sided in the source and
    /// renders fine, the lower flap is single-sided and disappears
    /// until doubleSided=true.
    ///
    /// Default false because weapons (Session D) and rigid item models
    /// already render correctly with backface culling, and double-sided
    /// pixels cost real GPU fragment work — only opt in when you know
    /// the M2 has problematic single-sided geometry. Attachments YES;
    /// weapons NO.
    /// </summary>
    public static bool SaveGlb(M2Model m2, Dictionary<int, byte[]> textures, string outputPath,
        bool doubleSided = false)
    {
        if (!m2.IsValid) return false;

        try
        {
            // ── Decode all source textures to PNG bytes ONCE (Session M phase 2.5).
            // We previously built one MaterialBuilder per texIdx eagerly. With
            // per-batch blend modes we need (texIdx × blendMode) materials, so
            // defer material construction to the per-submesh loop and just cache
            // the decoded PNG up front.
            var pngByTexIdx = new Dictionary<int, byte[]>();
            foreach (var (texIdx, blpData) in textures)
            {
                var pngBytes = ConvertBlpToPngBytes(blpData);
                if (pngBytes != null) pngByTexIdx[texIdx] = pngBytes;
            }

            var fallbackMat = new MaterialBuilder("default")
                .WithUnlitShader()
                .WithChannelParam(KnownChannel.BaseColor, KnownProperty.RGBA, new Vector4(0.7f, 0.7f, 0.7f, 1f));
            if (doubleSided) fallbackMat.WithDoubleSide(true);

            // Material cache keyed by (texIdx, blendMode, alphaBucket). Two
            // batches with the same texture, blend mode, and (rounded) alpha
            // share the material; differing on any of the three gets a
            // distinct material with a distinct name so the client can decode
            // the suffix per-mesh.
            //
            // Why alphaBucket: M2 transparency tracks produce floats with
            // arbitrary precision (e.g. 0.190008..). We round to 1% steps
            // so the cache stays small and the material name suffix
            // (mat_5_blend2_a19) is human-readable. Resolution at 1%
            // is well below perceptual threshold for the cases we care
            // about (faded lightning quads at 19%, etc.).
            var matCache = new Dictionary<(int texIdx, int blendMode, int alphaBucket), MaterialBuilder>();

            MaterialBuilder GetMaterial(int texIdx, int blendMode, bool wantDoubleSide, float alpha = 1.0f)
            {
                // Clamp + bucket the alpha. 0 stays 0 (we'd skip the submesh
                // in some future world but right now we render it anyway —
                // see the per-submesh loop comments).
                if (alpha < 0f) alpha = 0f;
                if (alpha > 1f) alpha = 1f;
                int alphaBucket = (int)Math.Round(alpha * 100f);

                var key = (texIdx, blendMode, alphaBucket);
                if (matCache.TryGetValue(key, out var existing)) return existing;

                // Three-tier resolution (matches pre-Session-M behavior):
                //   1. Exact texture match for this submesh's texIdx
                //   2. First-available texture (the common case for weapons —
                //      one texture loaded, many submeshes referencing
                //      texIdx values that don't directly index into it).
                //      Prefer a type=2 (DBC-supplied "item object skin")
                //      slot over a type=0 (M2-embedded environment/reflect
                //      map) when both are present — picking the reflect
                //      map as the base color is the Might-helm/shoulder bug.
                //   3. Grey fallback (only if zero textures decoded at all)
                // Losing tier 2 is what made the v4-regenerated Thunderfury
                // come out fully grey: 11 submeshes all referenced texIdx
                // values that weren't present in pngByTexIdx (the only
                // entry was at slot 0, but the batches were resolving to
                // other slots).
                byte[]? pngBytes = null;
                int resolvedTexIdx = texIdx;
                if (pngByTexIdx.TryGetValue(texIdx, out var exact))
                {
                    pngBytes = exact;
                }
                else if (pngByTexIdx.Count > 0)
                {
                    // Prefer the lowest-index type=2 slot (the DBC-supplied
                    // diffuse) over any type=0 slot (embedded reflection
                    // maps like ShoulderReflect01.blp). If neither is
                    // present, fall back to dictionary-insertion order —
                    // single-texture weapons land here and there's nothing
                    // to disambiguate.
                    int? preferred = null;
                    foreach (var kvp in pngByTexIdx)
                    {
                        if (kvp.Key < m2.Textures.Count && m2.Textures[kvp.Key].Type == 2)
                        {
                            if (preferred == null || kvp.Key < preferred.Value)
                                preferred = kvp.Key;
                        }
                    }
                    if (preferred != null)
                    {
                        resolvedTexIdx = preferred.Value;
                        pngBytes = pngByTexIdx[preferred.Value];
                    }
                    else
                    {
                        var first = pngByTexIdx.First();
                        resolvedTexIdx = first.Key;
                        pngBytes = first.Value;
                    }
                }

                if (pngBytes == null) return fallbackMat;

                var img = new SharpGLTF.Memory.MemoryImage(pngBytes);
                // Name suffix _blendN tells the client to set three.js blending
                // accordingly. See character-viewer/blend-suffix.js applyBlendSuffix.
                // We append _a{NN} (1% steps) when alpha < 1 so the client could
                // also decode the alpha factor if needed; SharpGLTF writes the
                // factor into pbrMetallicRoughness.baseColorFactor[3] regardless
                // so three.js already sees the correct alpha at the standard
                // glTF level — the name suffix is purely diagnostic.
                var alphaSuffix = alphaBucket < 100 ? $"_a{alphaBucket:D2}" : "";
                var name = $"mat_{resolvedTexIdx}_blend{blendMode}{alphaSuffix}";

                // Session N: bake static alpha into baseColorFactor.
                //
                //   WithBaseColor(img) sets the texture and an implicit
                //   RGBA factor of (1,1,1,1). To override the factor we
                //   call WithChannelParam after — same pattern used by
                //   SharpGLTF's own SceneBuilderTests/Example1. glTF's
                //   baseColorFactor[3] is the canonical place to put an
                //   overall material alpha multiplier; three.js reads it
                //   into Material.color.a automatically.
                var mat = new MaterialBuilder(name)
                    .WithUnlitShader()
                    .WithBaseColor(img)
                    .WithChannelParam(
                        KnownChannel.BaseColor,
                        KnownProperty.RGBA,
                        new Vector4(1f, 1f, 1f, alpha));

                // M2 blend modes (vanilla):
                //   0 = opaque         (default, no alpha)
                //   1 = alpha-key       (cutout — alphaTest)
                //   2 = alpha-blend     (standard transparency)
                //   3 = additive        (glow, additive blending)
                //   4 = add-alpha       (additive with alpha modulation)
                //   5 = modulate        (multiply)
                //   6 = mod2x           (multiply by 2x, rare)
                // For glTF we can only signal "this material is transparent or
                // opaque" — three.js then reads the suffix and applies the
                // specific blend equation.
                //
                // Session N: alpha < 1 forces BLEND even when blendMode is 0,
                // because an opaque material with a baseColorFactor.A < 1
                // gets ignored under AlphaMode.OPAQUE — alpha only counts
                // under MASK or BLEND. This is what lets the Thunderfury
                // lightning quads (alpha 0.19, blendMode 5 modulate) fade
                // away instead of rendering as flat opaque billboards.
                if (blendMode >= 2 || alphaBucket < 100)
                {
                    mat.WithAlpha(SharpGLTF.Materials.AlphaMode.BLEND);
                }
                else if (blendMode == 1)
                {
                    mat.WithAlpha(SharpGLTF.Materials.AlphaMode.MASK, 0.5f);
                }
                if (wantDoubleSide) mat.WithDoubleSide(true);

                // Cache under the FULL key (caller's texIdx, blendMode,
                // alphaBucket). A future call with the same triple gets
                // the same MaterialBuilder, even though internally we
                // resolved to a different texture for tier-2 fallback.
                matCache[key] = mat;
                return mat;
            }

            // Session M-revert: scene root is identity (mount-offset baking was
            // wrong — see class docstring).
            var rootMatrix = Matrix4x4.Identity;

            var scene = new SceneBuilder("scene");
            var vertices = m2.Vertices;
            var indices = m2.Indices;

            // Build a per-submesh blend mode lookup. Resolved via the M2 batch
            // chain: batch.SubmeshIndex → batch.MaterialIndex → m2.RenderFlags[idx]
            // → blendingMode. Submeshes not referenced by any batch (rare)
            // default to opaque (0).
            var submeshBlend = BuildSubmeshBlendMap(m2);

            // ── Submesh visibility (Session N) ──
            // For each submesh, resolve the static alpha its first-listed
            // batch produces via the M2's transparency tracks:
            //   batch.TextureWeightIndex → TransparencyLookup[idx]
            //                            → TransparencyStaticAlphas[idx]
            //
            // Result gets baked into the GLB material's baseColorFactor.A
            // and the material is flagged AlphaMode.BLEND when below 1.0.
            // Three.js reads the factor automatically; no client-side
            // changes needed.
            //
            // This is what makes Thunderfury's lightning quad geosets
            // (alpha 0.19 in their authored idle pose) render as faint
            // tints instead of flat opaque blue billboards. Hilt + blade
            // come back at 1.0 and render normally.
            //
            // We intentionally do NOT drop low-alpha submeshes — we
            // render them with their computed alpha, and skipping them
            // would discard legitimately-faded effects that the M2 author
            // wanted visible-but-subtle in the default pose.
            var submeshVis = BuildSubmeshVisibilityMap(m2);

            if (m2.Submeshes.Count > 1)
            {
                // ── Multi-submesh: build a SEPARATE MeshBuilder per submesh ──
                var submeshTexture = BuildSubmeshTextureMap(m2);

                for (int subIdx = 0; subIdx < m2.Submeshes.Count; subIdx++)
                {
                    var submesh = m2.Submeshes[subIdx];
                    if (submesh.IndexCount == 0 || submesh.IndexCount % 3 != 0) continue;

                    // Session N: per-batch static alpha from the M2's
                    // transparency tracks. Submeshes 0-6 of Thunderfury
                    // (the lightning fins) come back with alpha ~0.19 here;
                    // hilt + blade come back 1.0. The alpha gets baked into
                    // the GLB material's baseColor.A and the material is
                    // flagged AlphaMode.BLEND when below 1, so the renderer
                    // applies it instead of treating the geometry as opaque.
                    //
                    // We do NOT skip the submesh even when alpha is low —
                    // we render it with the computed alpha (which is then multiplied by the blend
                    // mode behavior). Dropping the geometry would also drop
                    // the "barely-visible faded lightning halo" that's
                    // supposed to be there in the static pose.
                    float vis = submeshVis.ContainsKey(subIdx) ? submeshVis[subIdx] : 1.0f;

                    int texIdx = submeshTexture.ContainsKey(subIdx) ? submeshTexture[subIdx] : subIdx;
                    int blendMode = submeshBlend.ContainsKey(subIdx) ? submeshBlend[subIdx] : 0;
                    var mat = GetMaterial(texIdx, blendMode, doubleSided, vis);

                    var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>($"Geoset{subIdx}");
                    var prim = meshBuilder.UsePrimitive(mat);

                    for (int i = submesh.IndexStart; i + 2 < submesh.IndexStart + submesh.IndexCount; i += 3)
                    {
                        if (i + 2 >= indices.Count) break;
                        int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                        if (i0 >= vertices.Count || i1 >= vertices.Count || i2 >= vertices.Count) continue;
                        prim.AddTriangle(MakeVertex(vertices[i0]), MakeVertex(vertices[i1]), MakeVertex(vertices[i2]));
                    }

                    scene.AddRigidMesh(meshBuilder, rootMatrix);
                }
            }
            else
            {
                // ── Single submesh or no submesh info: one mesh ──
                // Blend lookup still applies: a single-submesh M2 may carry an
                // additive material (rare for weapons, common for spell M2s).
                int singleBlend = submeshBlend.ContainsKey(0) ? submeshBlend[0] : 0;
                float singleVis = submeshVis.ContainsKey(0) ? submeshVis[0] : 1.0f;

                // Texture selection MUST follow the same batch chain the
                // multi-submesh branch uses:
                //   batch[0].SubmeshIndex(=0)
                //     → batch[0].TextureIndex
                //     → TextureLookup[ ... ]
                //     → texIdx into m2.Textures
                //
                // Why this matters even though there's only one submesh:
                // when an M2 has multiple textures (e.g. type=2 DBC diffuse
                // at slot 0 + type=0 embedded reflection at slot 1) and only
                // one submesh, `pngByTexIdx.Keys.First()` returns whichever
                // slot got inserted into the dictionary first — which is a
                // function of the texture-collection loop order in
                // ItemTextureService, NOT what the M2's batch actually wants
                // rendered as the diffuse.
                //
                // Empirical: Helm of Might (displayId 31260) and Pauldrons of
                // Might (31024) both have textureCount=2, submeshCount=1, and
                // both came out grey (the type=0 ShoulderReflect01.blp was
                // baked as the material) until this branch was rewritten to
                // consult the batch chain. The fully-correct sister items
                // (Helm/Pauldrons of Wrath) have multiple submeshes and went
                // through the multi-submesh branch, hiding the bug.
                var submeshTextureSingle = BuildSubmeshTextureMap(m2);
                int singleTexIdx = submeshTextureSingle.ContainsKey(0)
                    ? submeshTextureSingle[0]
                    : (pngByTexIdx.Count > 0 ? pngByTexIdx.Keys.First() : 0);
                var mat = pngByTexIdx.Count > 0
                    ? GetMaterial(singleTexIdx, singleBlend, doubleSided, singleVis)
                    : fallbackMat;
                var meshBuilder = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>("mesh");
                var prim = meshBuilder.UsePrimitive(mat);

                if (m2.Submeshes.Count == 1)
                {
                    var sub = m2.Submeshes[0];
                    for (int i = sub.IndexStart; i + 2 < sub.IndexStart + sub.IndexCount; i += 3)
                    {
                        if (i + 2 >= indices.Count) break;
                        int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                        if (i0 >= vertices.Count || i1 >= vertices.Count || i2 >= vertices.Count) continue;
                        prim.AddTriangle(MakeVertex(vertices[i0]), MakeVertex(vertices[i1]), MakeVertex(vertices[i2]));
                    }
                }
                else
                {
                    for (int i = 0; i + 2 < indices.Count; i += 3)
                    {
                        int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                        if (i0 >= vertices.Count || i1 >= vertices.Count || i2 >= vertices.Count) continue;
                        prim.AddTriangle(MakeVertex(vertices[i0]), MakeVertex(vertices[i1]), MakeVertex(vertices[i2]));
                    }
                }

                scene.AddRigidMesh(meshBuilder, rootMatrix);
            }

            // ── Save ──
            var model = scene.ToGltf2();
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            model.SaveGLB(outputPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Build a mapping of submeshIndex → blendMode using the batch chain:
    ///   batch.SubmeshIndex → batch.MaterialIndex → m2.RenderFlags[idx] → blendingMode.
    /// First-wins on duplicates (one batch per submesh is the common case;
    /// when there are layered batches on the same submesh, the first-listed
    /// is the base material). Submeshes with no batch reference fall back to
    /// opaque (0) via the caller's ContainsKey check.
    /// </summary>
    private static Dictionary<int, int> BuildSubmeshBlendMap(M2Model m2)
    {
        var map = new Dictionary<int, int>();

        foreach (var batch in m2.Batches)
        {
            int subIdx = batch.SubmeshIndex;
            if (map.ContainsKey(subIdx)) continue;

            int blendMode = 0;
            if (batch.MaterialIndex < m2.RenderFlags.Count)
            {
                blendMode = m2.RenderFlags[batch.MaterialIndex].BlendingMode;
            }

            map[subIdx] = blendMode;
        }

        return map;
    }

    /// <summary>
    /// Build a mapping of submeshIndex → textureIndex using the batch chain:
    ///   batch.SubmeshIndex → batch.TextureIndex → TextureLookup[idx] → texture index
    /// </summary>
    private static Dictionary<int, int> BuildSubmeshTextureMap(M2Model m2)
    {
        var map = new Dictionary<int, int>();

        foreach (var batch in m2.Batches)
        {
            int subIdx = batch.SubmeshIndex;
            if (map.ContainsKey(subIdx)) continue;

            int texIdx = 0;
            if (batch.TextureIndex < m2.TextureLookup.Count)
            {
                texIdx = m2.TextureLookup[batch.TextureIndex];
            }

            map[subIdx] = texIdx;
        }

        return map;
    }

    /// <summary>
    /// Build a mapping of submeshIndex → static-alpha-in-idle-pose using
    /// the batch's transparency track chain:
    ///   batch.TextureWeightIndex (= vanilla transparencyIndex)
    ///     → TransparencyLookup[idx]
    ///     → TransparencyStaticAlphas[idx]
    ///
    /// First-batch-wins on duplicates, matching the BlendMap convention.
    /// If a submesh has no batch reference, no entry is added (caller
    /// treats absence as "visible = 1.0").
    /// </summary>
    private static Dictionary<int, float> BuildSubmeshVisibilityMap(M2Model m2)
    {
        var map = new Dictionary<int, float>();

        foreach (var batch in m2.Batches)
        {
            int subIdx = batch.SubmeshIndex;
            if (map.ContainsKey(subIdx)) continue;

            map[subIdx] = m2.GetStaticAlphaForBatch(batch);
        }

        return map;
    }

    /// <summary>Simplified overload for single-texture models.</summary>
    public static bool SaveGlb(M2Model m2, byte[]? singleTexture, string outputPath,
        bool doubleSided = false)
    {
        var textures = new Dictionary<int, byte[]>();
        if (singleTexture != null) textures[0] = singleTexture;
        return SaveGlb(m2, textures, outputPath, doubleSided);
    }

    private static VERTEX MakeVertex(M2Vertex v)
    {
        return new VERTEX(
            new VertexPositionNormal(new Vector3(v.PosX, v.PosY, v.PosZ), new Vector3(v.NormX, v.NormY, v.NormZ)),
            new VertexTexture1(new Vector2(v.TexU, v.TexV))
        );
    }

    /// <summary>
    /// Convert BLP data to PNG bytes using SkiaSharp (Linux-compatible).
    /// Original extractor used System.Drawing (Windows-only GDI+).
    /// </summary>
    private static byte[]? ConvertBlpToPngBytes(byte[] blpData)
    {
        try
        {
            using var blpStream = new MemoryStream(blpData);
            var blpFile = new BlpFile(blpStream);
            var pixels = blpFile.GetPixels(0, out int w, out int h);
            if (w == 0 || h == 0 || pixels.Length == 0) return null;

            // War3Net returns BGRA pixels → SKBitmap
            using var bitmap = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var bitmapPixels = bitmap.GetPixels();
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmapPixels, pixels.Length);
            bitmap.NotifyPixelsChanged();

            using var pngStream = new MemoryStream();
            bitmap.Encode(pngStream, SKEncodedImageFormat.Png, 100);
            return pngStream.ToArray();
        }
        catch { return null; }
    }
}
using System.Numerics;
using SharpGLTF.Geometry;
using SharpGLTF.Geometry.VertexTypes;
using SharpGLTF.Materials;
using SharpGLTF.Scenes;
using SkiaSharp;

namespace MangosSuperUI.Services;

using VERTEX = VertexBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>;
using VERTEX2 = VertexBuilder<VertexPositionNormal, VertexTexture2, VertexEmpty>;
// Skinned twins of the two above, used only for the item rig (see SkinnedGlbWriter's "Item rig"
// section). Both UV shapes need one: fixing only the single-UV form would silently drop JOINTS_0
// off every fused Warglaive-style _mod primitive.
using SKIN_VERTEX = VertexBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>;
using SKIN_VERTEX2 = VertexBuilder<VertexPositionNormal, VertexTexture2, VertexJoints4>;

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
    /// <param name="visualEffects">Separate effect models this item's <c>ItemVisual</c> mounts on it —
    /// enchant glows and some permanent weapon effects. None of those models is in the item's own
    /// bytes, so without this an item can be decoded perfectly and still render dead. Resolve them
    /// with <see cref="M2Fx.ItemVisualEffects.Resolve"/>; their emitters are folded into this GLB's
    /// manifest at their mount points and their sheets embedded alongside.</param>
    /// <param name="plannedEmitters">Pre-import preview parity: the donor grafts a motion plan says
    /// the import WILL bake, rendered via <see cref="M2Fx.M2FxReader.FromGraft"/> instead of the raw
    /// later-client emitter summary (which has no scale/alpha curves or flipbook ranges and draws
    /// giant flat white columns). When present, the degraded WotLK raw-summary fallback never runs.</param>
    /// <param name="strictTextureSlots">Require every material to use the exact M2 texture slot it
    /// samples. Source-preserved items use this because replaceable slots have distinct client
    /// semantics: substituting the Type-2 object skin for a missing Type-3 weapon-blade texture
    /// paints the blade sheen with the diffuse skin and no longer resembles the stock client.</param>
    public static bool SaveGlb(M2Model m2, Dictionary<int, byte[]> textures, string outputPath,
        bool doubleSided = false,
        IReadOnlyList<M2Fx.ItemVisualEffects.Effect>? visualEffects = null,
        IReadOnlyList<WeaponForge.WeaponPreviewService.PreviewEmitter>? plannedEmitters = null,
        bool strictTextureSlots = false)
    {
        if (!m2.IsValid) return false;

        try
        {
            // ── Decode all source textures to PNG bytes ONCE (Session M phase 2.5).
            // We previously built one MaterialBuilder per texIdx eagerly. With
            // per-batch blend modes we need (texIdx × blendMode) materials, so
            // defer material construction to the per-submesh loop and just cache
            // the decoded PNG up front.
            Dictionary<int, byte[]> pngByTexIdx = new();
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
            //
            // Why the tint is in the key: the M2Color record's RGB rest sample is baked into
            // baseColorFactor below, and two batches can agree on texture/blend/alpha while pointing
            // at different colour records (a weapon whose blade glows orange and whose rune glows
            // blue off one texture). Merging those would paint both the same.
            // env/mod are per-PASS properties that are not derivable from (texture, blend, alpha,
            // tint), so they belong in the key. Two passes on one energy sheet — an env-mapped shell
            // and a plain additive shell, same slot, same blend, same alpha, no colour record — are
            // exactly the Warglaive shape, and would otherwise collide with the second silently
            // inheriting the first's marker. wantDoubleSide is in the key because a fused _mod
            // primitive forces it on regardless of what the caller asked for.
            Dictionary<(int texIdx, int blendMode, int alphaBucket, int tintBucket,
                bool env, bool mod, bool dbl), MaterialBuilder> matCache = new();

            // exactOnly: an overlay or fused pass must resolve its OWN texture. The three-tier
            // fallback below is load-bearing for a BASE pass (losing tier 2 is what made Thunderfury
            // render all-grey) but for an overlay it silently retargets to the preferred Type-2
            // diffuse and emits a second full copy of the submesh painted with the base skin.
            // unique: bypass the cache and mint a distinctly-named material, used for every pass
            // that carries an animation so the manifest's material-name key is unambiguous.
            MaterialBuilder? GetMaterial(int texIdx, int blendMode, bool wantDoubleSide, float alpha = 1.0f,
                Vector3? tint = null, bool env = false, bool mod = false,
                bool exactOnly = false, string? unique = null)
            {
                // Clamp + bucket the alpha. 0 stays 0 (we'd skip the submesh
                // in some future world but right now we render it anyway —
                // see the per-submesh loop comments).
                if (alpha < 0f) alpha = 0f;
                if (alpha > 1f) alpha = 1f;
                int alphaBucket = (int)Math.Round(alpha * 100f);
                var rgb = tint ?? Vector3.One;
                if (!float.IsFinite(rgb.X) || !float.IsFinite(rgb.Y) || !float.IsFinite(rgb.Z)) rgb = Vector3.One;
                rgb = Vector3.Clamp(rgb, Vector3.Zero, Vector3.One);
                int tintBucket = ((int)MathF.Round(rgb.X * 255f) << 16)
                               | ((int)MathF.Round(rgb.Y * 255f) << 8)
                               | (int)MathF.Round(rgb.Z * 255f);

                var key = (texIdx, blendMode, alphaBucket, tintBucket, env, mod, wantDoubleSide);
                if (unique is null && matCache.TryGetValue(key, out var existing)) return existing;

                // Three-tier resolution (matches pre-Session-M behavior):
                //   1. Exact texture match for this submesh's texIdx
                //   2. First-available texture (the common case for weapons —
                //      one texture loaded, many submeshes referencing
                //      texIdx values that don't directly index into it).
                //      Prefer a type=2 (DBC-supplied "item object skin")
                //      slot over a type=0 (M2-embedded environment/reflect
                //      map) when both are present — picking the reflect
                //      map as the base color is the Might-helm/shoulder bug.
                //   3. Grey fallback (when no substitution is available, or strict slots forbid it)
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
                else if (MaySubstituteMissingTexture(exactOnly, strictTextureSlots, pngByTexIdx.Count))
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

                if (pngBytes == null) return exactOnly ? null : fallbackMat;

                // An `_env` marker is only honest when the pass sampled its OWN texture.
                // applyEnvMapping matcaps whatever mat.map holds, so a tier-2 substitution here
                // would matcap the armour DIFFUSE — a chrome blob smeared with its own skin.
                if (env && resolvedTexIdx != texIdx) env = false;

                var img = new SharpGLTF.Memory.MemoryImage(pngBytes);
                // Name suffix _blendN tells the client to set three.js blending
                // accordingly. See character-viewer/blend-suffix.js applyBlendSuffix.
                // We append _a{NN} (1% steps) when alpha < 1 so the client could
                // also decode the alpha factor if needed; SharpGLTF writes the
                // factor into pbrMetallicRoughness.baseColorFactor[3] regardless
                // so three.js already sees the correct alpha at the standard
                // glTF level — the name suffix is purely diagnostic.
                var alphaSuffix = alphaBucket < 100 ? $"_a{alphaBucket:D2}" : "";
                // `_env` and `_mod` sit BEFORE the blend suffix: blend-suffix.js's blend regex is
                // END-anchored while its _env/_mod regexes match anywhere — the same composition
                // WeaponPreviewService uses. The ad-hoc /_blend[34]/ glow-tint tests on the Forge
                // pages still match because the alpha suffix stays last.
                var marker = $"{(env ? "_env" : "")}{(mod ? "_mod" : "")}";
                var name = unique is null
                    ? $"mat_{resolvedTexIdx}{marker}_blend{blendMode}{alphaSuffix}"
                    : $"{unique}_{resolvedTexIdx}{marker}_blend{blendMode}{alphaSuffix}";

                // Session N: bake static alpha into baseColorFactor.
                //
                //   WithBaseColor(img) sets the texture and an implicit
                //   RGBA factor of (1,1,1,1). To override the factor we
                //   call WithChannelParam after — same pattern used by
                //   SharpGLTF's own SceneBuilderTests/Example1. glTF's
                //   baseColorFactor[3] is the canonical place to put an
                //   overall material alpha multiplier; three.js reads it
                //   into Material.color.a automatically.
                //
                // The RGB half is the M2Color record's rest tint, which used to be dropped on the
                // floor — GlbWriter never read ReachableRestColors at all. That is why an authored
                // coloured glow (a fel-green rune, an orange blade) arrived in the previewer white:
                // the geometry and the additive blend were right and the colour the artist put on
                // the pass was simply not exported. Models with no colour record pass Vector3.One
                // and are unchanged.
                var mat = new MaterialBuilder(name)
                    .WithUnlitShader()
                    .WithBaseColor(img)
                    .WithChannelParam(
                        KnownChannel.BaseColor,
                        KnownProperty.RGBA,
                        new Vector4(rgb, alpha));

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
                // A `unique` material is deliberately NOT cached — it exists so one animated pass
                // owns one material name that the manifest can key on.
                if (unique is null) matCache[key] = mat;
                return mat;
            }

            // Session M-revert: scene root is identity (mount-offset baking was
            // wrong — see class docstring).
            var rootMatrix = Matrix4x4.Identity;

            // Explicit types, not `var`: these are captured by the local functions below, which
            // see a `var` local's declared (nullable) type rather than its not-null flow state.
            SceneBuilder scene = new("scene");
            List<M2Vertex> vertices = m2.Vertices;
            List<ushort> indices = m2.Indices;

            // Skinned item path. Non-null ONLY for the handful of models whose visible geometry
            // depends on a camera-facing bone or a global-sequence bone track - Thunderfury, the
            // enchant-orb props, a few GameObjects. Everything else keeps the rigid fast path
            // below unchanged, which is the whole point of the selector: this file also writes
            // every helm, spaulder, sword and chest in the catalogue.
            //
            // At rest a skinned pass is byte-for-byte the same geometry as the rigid one - each
            // joint's inverse-bind matrix cancels its rest world transform - so the only thing
            // this changes for a selected model is that its authored motion now survives export.
            var itemRig = SkinnedGlbWriter.RequiresItemSkin(m2) ? SkinnedGlbWriter.BuildItemRig(m2) : null;
            int itemBoneCount = m2.Bones.Count;
            int itemSkinnedPasses = 0;

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
            var submeshTint = BuildSubmeshTintMap(m2);

            // Declared before the emit helpers below, which capture it.
            var submeshTexture = BuildSubmeshTextureMap(m2);

            // Batches for one submesh, in DRAW ORDER (material layer, then source order — see
            // BatchesInDrawOrder). Batch 0 is the base material; later batches are additional material
            // LAYERS the client composites over it. This writer historically kept only batch 0 (the
            // first-wins guards in the Build*Map helpers), which is why a Warglaive renders dead here
            // while the Weapon Forge shows it alive: its blade energy is entirely in the dropped layers.
            var batchesBySubmesh = new Dictionary<int, List<M2Batch>>();
            foreach (var b in BatchesInDrawOrder(m2))
            {
                if (!batchesBySubmesh.TryGetValue(b.SubmeshIndex, out var list))
                    batchesBySubmesh[b.SubmeshIndex] = list = new List<M2Batch>();
                list.Add(b);
            }

            // A pass may only add a SECOND primitive over geometry the base already covers when its
            // blend mode COMPOSITES. blend-suffix.js gives blend 3/4 additive, 5 multiply and 6 a
            // mod2x equation, all with depthWrite = false — such an overlay can neither z-fight with
            // the base nor replace it.
            //
            // Blend 0/1/2 layers stay dropped, deliberately. Two coincident depth-WRITING primitives
            // resolve by three.js's opaque sort (ascending material.id), so the overlay — created
            // second — would simply erase the base diffuse. That is the Might-helm grey bug with
            // extra steps. This one predicate is also what keeps this change off every plate helm,
            // spaulder and GameObject in the catalogue: the effects this exists for are blend 6
            // (env-mapped) and blend 4 (modulate).
            const int OVERLAY_MIN_BLEND = 3;

            // TEXCOORD_1 is only real if the model authored a second UV set.
            bool hasUv2 = false;
            for (int vi = 0; vi < vertices.Count && !hasUv2; vi++)
                if (vertices[vi].TexU2 != 0f || vertices[vi].TexV2 != 0f) hasUv2 = true;

            // Every primitive actually emitted, with the glTF names it landed under — this is what
            // the animation manifest keys on now, instead of re-deriving "first batch per submesh".
            List<M2Fx.M2FxPass> emittedPasses = new();

            // ── Fusion test ─────────────────────────────────────────────────────────────────────
            // A batch with TWO texture units that resolve to the SAME M2 texture slot is WoW's
            // multi-texture combine of one sheet against itself at two mappings: a static copy times
            // a scrolling copy, whose interference is the wave that travels along the blade. That is
            // the ONLY two-unit shape we act on, and requiring the two units to actually differ
            // (different coordinate set OR different UV transform) is what makes the multiply mean
            // something rather than square one sample.
            //
            // The blend gate is what makes this safe on DIFFERENT slots. A two-unit batch at blend
            // 0/1/2 is a base diffuse plus a hardcoded reflect/spec overlay, and which combiner
            // applies is named by M2Batch.ShaderId, which nothing here decodes — fusing one of those
            // is how a helm loses its DBC skin and renders its reflect map squared. Those never
            // reach this function. At blend >= 3 the batch is already a compositing overlay, so
            // fusing it cannot erase a base.
            //
            // Approximation, stated plainly: applyMultiTexture (blend-suffix.js) clones the material's
            // ONE map and samples it twice, so a two-DIFFERENT-texture combine cannot be expressed —
            // it renders as unit 1's sheet scrolled against itself. WeaponPreviewService makes the
            // identical trade (it picks b1's png and falls back to b0's), and matching it is the
            // point of this change: the Weapon Forge and the Items page must agree on the same model.
            bool TryFuse(M2Batch b, int blend, out int slot, out bool scrollUv1, out bool staticUv1)
            {
                slot = -1; scrollUv1 = false; staticUv1 = false;
                if (b.TextureCount < 2 || blend < OVERLAY_MIN_BLEND) return false;

                ushort c0 = ResolveCoordCombo(m2, b, 0), c1 = ResolveCoordCombo(m2, b, 1);
                ushort t0 = ResolveTransformCombo(m2, b, 0), t1 = ResolveTransformCombo(m2, b, 1);
                if (c0 == c1 && t0 == t1) return false;   // identical samples: multiply = square

                // An env-mapped unit is sampled by view normal, not by a UV set, so there is no
                // second UV to multiply against — that shape belongs on the _env path instead.
                if ((c0 & 0x8000) != 0 || (c1 & 0x8000) != 0) return false;

                // Unit 1 carries the scroll, so it is the sheet worth showing; unit 0 is the fallback.
                int s1 = ResolveTextureSlot(m2, b, 1), s0 = ResolveTextureSlot(m2, b, 0);
                slot = s1 >= 0 && pngByTexIdx.ContainsKey(s1) ? s1
                     : s0 >= 0 && pngByTexIdx.ContainsKey(s0) ? s0 : -1;
                if (slot < 0) return false;

                scrollUv1 = (c1 & 0x7fff) == 1 && hasUv2;
                staticUv1 = (c0 & 0x7fff) == 1 && hasUv2;
                // Both samples on the same UV set with no differing transform would square one
                // sample; the transform check above already caught the no-UV1 case for us, but be
                // explicit rather than emitting a _mod the client will multiply into mush.
                if (scrollUv1 == staticUv1 && t0 == t1) return false;
                return true;
            }

            // One index walk, four emitters. The bounds rules (stop at a short buffer, skip a
            // triangle that points past the vertex table) are load-bearing on malformed MPQ models
            // and must not drift between the rigid and skinned forms.
            IEnumerable<(int i0, int i1, int i2)> Triangles(int indexStart, int indexCount)
            {
                for (int i = indexStart; i + 2 < indexStart + indexCount; i += 3)
                {
                    if (i + 2 >= indices.Count) yield break;
                    int i0 = indices[i], i1 = indices[i + 1], i2 = indices[i + 2];
                    if (i0 >= vertices.Count || i1 >= vertices.Count || i2 >= vertices.Count) continue;
                    yield return (i0, i1, i2);
                }
            }

            void AddTris(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexEmpty> prim,
                         int indexStart, int indexCount)
            {
                foreach (var (i0, i1, i2) in Triangles(indexStart, indexCount))
                    prim.AddTriangle(MakeVertex(vertices[i0]), MakeVertex(vertices[i1]), MakeVertex(vertices[i2]));
            }

            void AddTris2(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture2, VertexEmpty> prim,
                          int indexStart, int indexCount, bool scrollUv1, bool staticUv1)
            {
                foreach (var (i0, i1, i2) in Triangles(indexStart, indexCount))
                    prim.AddTriangle(
                        MakeVertex2(vertices[i0], scrollUv1, staticUv1),
                        MakeVertex2(vertices[i1], scrollUv1, staticUv1),
                        MakeVertex2(vertices[i2], scrollUv1, staticUv1));
            }

            void AddSkinTris(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture1, VertexJoints4> prim,
                             int indexStart, int indexCount)
            {
                foreach (var (i0, i1, i2) in Triangles(indexStart, indexCount))
                    prim.AddTriangle(
                        MakeSkinVertex(vertices[i0], itemBoneCount),
                        MakeSkinVertex(vertices[i1], itemBoneCount),
                        MakeSkinVertex(vertices[i2], itemBoneCount));
            }

            void AddSkinTris2(PrimitiveBuilder<MaterialBuilder, VertexPositionNormal, VertexTexture2, VertexJoints4> prim,
                              int indexStart, int indexCount, bool scrollUv1, bool staticUv1)
            {
                foreach (var (i0, i1, i2) in Triangles(indexStart, indexCount))
                    prim.AddTriangle(
                        MakeSkinVertex2(vertices[i0], scrollUv1, staticUv1, itemBoneCount),
                        MakeSkinVertex2(vertices[i1], scrollUv1, staticUv1, itemBoneCount),
                        MakeSkinVertex2(vertices[i2], scrollUv1, staticUv1, itemBoneCount));
            }

            // One primitive per glTF MESH — never two primitives inside one mesh.
            //
            // A glTF mesh with two primitives arrives in three.js as a Group whose children were
            // renamed by the loader's uniquifier, and m2fx.js only traverses nodes where isMesh is
            // true — so the Group is skipped and the children no longer match the manifest. With one
            // primitive the loader returns the mesh directly, name intact, and every mesh-name key
            // still resolves. (The manifest also carries a material-name key as a second belt.)
            void EmitPass(string meshName, int indexStart, int indexCount, MaterialBuilder mat,
                          bool fused, bool scrollUv1, bool staticUv1, M2Fx.M2FxMesh? fx)
            {
                // Only the vertex skinning fragment and the scene insertion call differ between
                // the two paths. Material selection, pass naming, blend suffixes, the fused _mod
                // shape and the fx manifest entry below are shared, deliberately - forking them is
                // how the Warglaive's travelling wave would quietly stop being emitted.
                if (fused)
                {
                    if (itemRig is null)
                    {
                        var mb2 = new MeshBuilder<VertexPositionNormal, VertexTexture2, VertexEmpty>(meshName);
                        AddTris2(mb2.UsePrimitive(mat), indexStart, indexCount, scrollUv1, staticUv1);
                        scene.AddRigidMesh(mb2, rootMatrix);
                    }
                    else
                    {
                        var mb2 = new MeshBuilder<VertexPositionNormal, VertexTexture2, VertexJoints4>(meshName);
                        AddSkinTris2(mb2.UsePrimitive(mat), indexStart, indexCount, scrollUv1, staticUv1);
                        scene.AddSkinnedMesh(mb2, rootMatrix, itemRig.Joints);
                        itemSkinnedPasses++;
                    }
                }
                else
                {
                    if (itemRig is null)
                    {
                        var mb = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexEmpty>(meshName);
                        AddTris(mb.UsePrimitive(mat), indexStart, indexCount);
                        scene.AddRigidMesh(mb, rootMatrix);
                    }
                    else
                    {
                        var mb = new MeshBuilder<VertexPositionNormal, VertexTexture1, VertexJoints4>(meshName);
                        AddSkinTris(mb.UsePrimitive(mat), indexStart, indexCount);
                        scene.AddSkinnedMesh(mb, rootMatrix, itemRig.Joints);
                        itemSkinnedPasses++;
                    }
                }
                if (fx is not null)
                    emittedPasses.Add(new M2Fx.M2FxPass(meshName, mat.Name, fx));
            }

            // Layers 1..n — the passes this writer used to drop on the floor.
            void EmitOverlays(int subIdx, string baseMeshName, int indexStart, int indexCount,
                              List<M2Batch> batches)
            {
                for (int bi = 1; bi < batches.Count; bi++)
                {
                    var b = batches[bi];
                    int blendO = b.MaterialIndex < m2.RenderFlags.Count
                        ? m2.RenderFlags[b.MaterialIndex].BlendingMode : 0;
                    if (blendO < OVERLAY_MIN_BLEND) continue;

                    float visO = m2.GetStaticAlphaForBatch(b);
                    var tintO = RestTintForBatch(m2, b);
                    string meshNameO = $"{baseMeshName}_ov{bi}";

                    if (TryFuse(b, blendO, out int fSlot, out bool fScroll, out bool fStatic))
                    {
                        var fxF = M2Fx.M2FxReader.ReadPassFx(m2.SourceBytes, m2, b, 1);
                        var matF = GetMaterial(fSlot, blendO, wantDoubleSide: true, visO, tintO,
                            env: false, mod: true, exactOnly: true,
                            unique: fxF is null ? null : $"p{subIdx}_{bi}_1");
                        if (matF is null) continue;
                        EmitPass(meshNameO, indexStart, indexCount, matF, fused: true, fScroll, fStatic, fxF);
                        continue;
                    }

                    // Unit 0 only, and it must resolve its OWN texture (exactOnly): a substituted
                    // one would draw a second full copy of the submesh in the base skin.
                    int slotO = ResolveTextureSlot(m2, b, 0);
                    if (slotO < 0 || !pngByTexIdx.ContainsKey(slotO)) continue;
                    bool envO = b.TextureCount < 2 && (ResolveCoordCombo(m2, b, 0) & 0x8000) != 0;
                    var fx0 = M2Fx.M2FxReader.ReadPassFx(m2.SourceBytes, m2, b, 0);
                    var matO = GetMaterial(slotO, blendO, doubleSided, visO, tintO,
                        env: envO, mod: false, exactOnly: true,
                        unique: fx0 is null ? null : $"p{subIdx}_{bi}_0");
                    if (matO is null) continue;
                    EmitPass(meshNameO, indexStart, indexCount, matO, fused: false, false, false, fx0);
                }
            }

            // Emit a submesh: its base primitive, then any compositing overlay layers.
            void EmitSubmesh(int subIdx, string meshName, int indexStart, int indexCount)
            {
                var batches = batchesBySubmesh.TryGetValue(subIdx, out var bl) ? bl : new List<M2Batch>();
                var baseBatch = batches.Count > 0 ? batches[0] : null;

                float vis = submeshVis.TryGetValue(subIdx, out var v) ? v : 1.0f;
                int blend = submeshBlend.TryGetValue(subIdx, out var bm) ? bm : 0;
                int texIdx = submeshTexture.TryGetValue(subIdx, out var ti) ? ti
                           : (pngByTexIdx.Count > 0 ? pngByTexIdx.Keys.First() : subIdx);
                var tint = submeshTint.TryGetValue(subIdx, out var t) ? t : (Vector3?)null;

                // ── Base ────────────────────────────────────────────────────────────────────────
                if (baseBatch is not null && TryFuse(baseBatch, blend, out int fSlot, out bool fScroll, out bool fStatic))
                {
                    // Unit 1 owns the animated transform, so its fx and its mapping drive channel 0.
                    // Forced double-sided: an additive energy submesh is wound to be seen from both
                    // faces, and single-sided culling drops it entirely — which is why only the base
                    // blade would show.
                    var fxF = M2Fx.M2FxReader.ReadPassFx(m2.SourceBytes, m2, baseBatch, 1);
                    var matF = GetMaterial(fSlot, blend, wantDoubleSide: true, vis, tint,
                        // env + mod cannot coexist: applyEnvMapping replaces the material with a
                        // matcap, which has no .map, and applyMultiTexture's !mat.map guard then
                        // skips it. Decide here rather than leaving it to client call order.
                        env: false, mod: true, exactOnly: true,
                        unique: fxF is null ? null : $"p{subIdx}_0_1");
                    if (matF is not null)
                    {
                        EmitPass(meshName, indexStart, indexCount, matF, fused: true, fScroll, fStatic, fxF);
                        EmitOverlays(subIdx, meshName, indexStart, indexCount, batches);
                        return;
                    }
                    // else fall through to the ordinary single-UV base
                }

                bool env = baseBatch is not null
                        && blend >= OVERLAY_MIN_BLEND
                        && baseBatch.TextureCount < 2
                        && (ResolveCoordCombo(m2, baseBatch, 0) & 0x8000) != 0
                        && pngByTexIdx.ContainsKey(texIdx);

                var baseFx = baseBatch is null ? null
                           : M2Fx.M2FxReader.ReadPassFx(m2.SourceBytes, m2, baseBatch, 0);
                var baseMat = GetMaterial(texIdx, blend, doubleSided, vis, tint, env: env,
                                  unique: baseFx is null ? null : $"p{subIdx}_0_0")
                              ?? fallbackMat;
                EmitPass(meshName, indexStart, indexCount, baseMat, fused: false, false, false, baseFx);

                EmitOverlays(subIdx, meshName, indexStart, indexCount, batches);
            }

            if (m2.Submeshes.Count > 1)
            {
                for (int subIdx = 0; subIdx < m2.Submeshes.Count; subIdx++)
                {
                    var submesh = m2.Submeshes[subIdx];
                    if (submesh.IndexCount == 0 || submesh.IndexCount % 3 != 0) continue;
                    EmitSubmesh(subIdx, $"Geoset{subIdx}", submesh.IndexStart, submesh.IndexCount);
                }
            }
            else if (m2.Submeshes.Count == 1)
            {
                var sub = m2.Submeshes[0];
                EmitSubmesh(0, "mesh", sub.IndexStart, sub.IndexCount);
            }
            else
            {
                EmitSubmesh(0, "mesh", 0, indices.Count - (indices.Count % 3));
            }

            // ── Item global-sequence clips ──
            // One glTF animation per M2 global loop, named GlobalSequence_{n}, riding the AUTHORED
            // bone nodes. They are separate clips because the M2 runs them concurrently and at
            // independent periods; merging them into one clip, or letting the client pick a single
            // one, leaves part of the model frozen. Emitted before ToGltf2 - that call is what
            // harvests the curves off the NodeBuilders - and only when a pass actually bound to the
            // rig, so we never write animation channels aimed at nodes no instance references.
            if (itemRig is not null && itemSkinnedPasses > 0)
            {
                int globalClips = SkinnedGlbWriter.EmitGlobalSequences(m2, itemRig.Bones);
                Console.WriteLine($"[GlbWriter] item rig: {m2.Bones.Count} bone(s), " +
                                  $"{itemRig.BillboardCount} camera-facing, {globalClips} global loop(s), " +
                                  $"{itemSkinnedPasses} skinned pass(es)");
            }

            // ── Save ──
            var model = scene.ToGltf2();

            // ── Animation manifest (glTF extras) ──
            // glTF cannot animate a material, so the colour / alpha / UV-scroll tracks ride out as a
            // JSON blob on the root and character-viewer/m2fx.js replays them per frame. Best-effort
            // and additive: a model with nothing animated writes no extras and the GLB is what it
            // always was. See Services/M2Fx/M2FxManifest.cs.
            try
            {
                // Particle-emitter sheets have to reach the browser somehow, and they are usually NOT
                // bound to any batch, so the material loop above never emitted them. They go in as
                // ordinary glTF images that no material references — verified to survive SharpGLTF's
                // save and reload with their names intact — which puts them in the GLB's binary chunk
                // where the client's own loader can resolve them, instead of a base64 blob in the JSON
                // or a second request against an endpoint that does not exist.
                var emitterTextureIndex = EmbedEmitterTextures(model, m2, pngByTexIdx);

                var fx = M2Fx.M2FxReader.Build(m2.SourceBytes, m2, emittedPasses,
                    slot => emitterTextureIndex.TryGetValue(slot, out int gltfIndex) ? gltfIndex : null);

                var mounted = EmbedVisualEffects(model, visualEffects);
                if (mounted.Count > 0)
                    fx = fx with { Emitters = fx.Emitters.Concat(mounted).ToList() };

                // Planned-graft emitters — what the import will actually produce (donor curves +
                // source overrides), same as the weapon preview's AttachEmitterManifest path.
                if (plannedEmitters is { Count: > 0 })
                {
                    var planned = new List<M2Fx.M2FxEmitter>();
                    foreach (var pe in plannedEmitters)
                    {
                        if (pe.Png is not { Length: > 0 }) continue;
                        var existing = model.LogicalTextures.FirstOrDefault(t => SameImage(t.PrimaryImage, pe.Png));
                        int texIdx;
                        if (existing is not null) texIdx = existing.LogicalIndex;
                        else
                        {
                            var image = model.CreateImage();
                            image.Content = new SharpGLTF.Memory.MemoryImage(pe.Png);
                            image.Name = $"EmitterSheet_g{planned.Count}";
                            var texture = model.UseTexture(image);
                            texture.Name = image.Name;
                            texIdx = texture.LogicalIndex;
                        }
                        var em = M2Fx.M2FxReader.FromGraft(pe.Graft, texIdx, pe.PositionMesh);
                        if (em is not null) planned.Add(em);
                    }
                    if (planned.Count > 0) fx = fx with { Emitters = fx.Emitters.Concat(planned).ToList() };
                }

                // WotLK (v264) fallback. M2WotlkReader parses fine but leaves SourceBytes null on
                // purpose (its raw emitter/track reader is v256-only), so M2FxReader.Build above found
                // no emitters and the preview shipped with an empty suiFx — which is why WotLK armour
                // (Worldbreaker's shoulder fire) rendered dead while the game showed it. Route the
                // emitters the WotLK reader DID decode into the manifest, resolving each sheet by name
                // against the already-embedded textures. Gated on SourceBytes==null so vanilla/TBC,
                // which use the higher-fidelity binary path, are untouched.
                if (fx.Emitters.Count == 0 && m2.SourceBytes is null && m2.ParticleEmitters.Count > 0)
                {
                    var wotlk = M2Fx.M2FxReader.BuildEmittersFromModel(m2,
                        name => EmbedNamedEmitterTexture(model, m2, pngByTexIdx, name));
                    if (wotlk.Count > 0) fx = fx with { Emitters = fx.Emitters.Concat(wotlk).ToList() };
                }

                if (fx.Any) model.Extras = fx.ToExtras();
            }
            catch { /* a manifest is an enhancement; never fail a GLB over one */ }

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
    /// Embed each emitter's texture sheet as a glTF image, returning M2 texture slot → glTF texture
    /// index for the manifest to reference.
    ///
    /// Emitter sheets are already in <paramref name="pngByTexIdx"/> for free: every caller populates
    /// the texture dictionary by walking the M2's whole texture table and pulling each type-0
    /// filename out of the MPQ, and an emitter's sheet is exactly such an entry. It simply never got
    /// used, because nothing binds it to a batch and the material loop only builds materials for
    /// batches.
    ///
    /// A slot with no decoded PNG is left out of the map, which makes M2FxReader drop that emitter.
    /// That is deliberate: an untextured additive quad is a white blob, and a white blob on a weapon
    /// is worse than no effect at all.
    /// </summary>
    private static Dictionary<int, int> EmbedEmitterTextures(
        SharpGLTF.Schema2.ModelRoot model, M2Model m2, IReadOnlyDictionary<int, byte[]> pngByTexIdx)
    {
        var map = new Dictionary<int, int>();

        foreach (int slot in M2Fx.M2FxReader.EmitterTextureSlots(m2.SourceBytes))
        {
            if (map.ContainsKey(slot)) continue;
            if (!pngByTexIdx.TryGetValue(slot, out var png) || png is not { Length: > 0 }) continue;

            // An emitter's sheet is frequently the same image a batch already renders with — a
            // flaming sword's blade texture IS its ember sheet — and the material loop has already
            // embedded that one. Re-adding it would put a second copy of the same PNG in the binary
            // chunk, which on a 128x128 sheet is tens of kilobytes per model for nothing. Match on
            // content rather than on slot, because SceneBuilder does not expose which glTF texture
            // came from which M2 slot.
            var existing = model.LogicalTextures.FirstOrDefault(t => SameImage(t.PrimaryImage, png));
            if (existing is not null) { map[slot] = existing.LogicalIndex; continue; }

            var image = model.CreateImage();
            image.Content = new SharpGLTF.Memory.MemoryImage(png);
            image.Name = $"EmitterSheet_{slot}";

            var texture = model.UseTexture(image);
            texture.Name = image.Name;
            map[slot] = texture.LogicalIndex;
        }

        return map;
    }

    /// <summary>
    /// Embed a WotLK emitter's sheet by FILENAME and return its glTF texture index. The v264 lane
    /// (<see cref="M2Handlers.M2WotlkReader"/>) exposes each emitter's sheet as a path on
    /// <c>M2ParticleEmitterInfo.TextureName</c> rather than as a slot, so match the name against the M2
    /// texture table to find the PNG the caller already decoded, then embed it exactly as
    /// <see cref="EmbedEmitterTextures"/> does (deduped by content). Null when the sheet is not among the
    /// decoded textures, which makes <see cref="M2Fx.M2FxReader.BuildEmittersFromModel"/> drop that
    /// emitter — same rule as the slot path, since an untextured additive quad is a white blob.
    /// </summary>
    private static int? EmbedNamedEmitterTexture(SharpGLTF.Schema2.ModelRoot model, M2Model m2,
        IReadOnlyDictionary<int, byte[]> pngByTexIdx, string? textureName)
    {
        if (string.IsNullOrEmpty(textureName)) return null;
        string want = textureName.Replace('/', '\\');

        for (int i = 0; i < m2.Textures.Count; i++)
        {
            if (!string.Equals(m2.Textures[i].Filename?.Replace('/', '\\'), want, StringComparison.OrdinalIgnoreCase))
                continue;
            if (!pngByTexIdx.TryGetValue(i, out var png) || png is not { Length: > 0 }) return null;

            var existing = model.LogicalTextures.FirstOrDefault(t => SameImage(t.PrimaryImage, png));
            if (existing is not null) return existing.LogicalIndex;

            var image = model.CreateImage();
            image.Content = new SharpGLTF.Memory.MemoryImage(png);
            image.Name = $"WotlkEmitterSheet_{i}";
            var texture = model.UseTexture(image);
            texture.Name = image.Name;
            return texture.LogicalIndex;
        }
        return null;
    }

    /// <summary>
    /// Embed each mounted effect model's sheets and decode its emitters onto the host.
    ///
    /// The effect model's GEOMETRY is deliberately not merged. Vanilla enchant effects are emitters
    /// hung off an essentially empty model — RedFlame_Low is 2.7 KB — so the emitters are the whole
    /// visual, and pulling their geometry in would mean reconciling a second material/skin set for no
    /// gain. Ribbon emitters are the exception and are not handled anywhere yet.
    /// </summary>
    internal static List<M2Fx.M2FxEmitter> EmbedVisualEffects(
        SharpGLTF.Schema2.ModelRoot model, IReadOnlyList<M2Fx.ItemVisualEffects.Effect>? effects)
    {
        var result = new List<M2Fx.M2FxEmitter>();
        if (effects is null || effects.Count == 0) return result;

        foreach (var effect in effects)
        {
            // Decode the effect's own textures, then embed the ones its emitters actually sample.
            var pngBySlot = new Dictionary<int, byte[]>();
            foreach (var (slot, blp) in effect.Textures)
            {
                var png = ConvertBlpToPngBytes(blp);
                if (png is { Length: > 0 }) pngBySlot[slot] = png;
            }

            var indexBySlot = new Dictionary<int, int>();
            foreach (int slot in M2Fx.M2FxReader.EmitterTextureSlots(effect.M2))
            {
                if (indexBySlot.ContainsKey(slot)) continue;
                if (!pngBySlot.TryGetValue(slot, out var png)) continue;

                var existing = model.LogicalTextures.FirstOrDefault(t => SameImage(t.PrimaryImage, png));
                if (existing is not null) { indexBySlot[slot] = existing.LogicalIndex; continue; }

                var image = model.CreateImage();
                image.Content = new SharpGLTF.Memory.MemoryImage(png);
                image.Name = $"VisualSheet_{Path.GetFileNameWithoutExtension(effect.ModelPath)}_{slot}";
                var texture = model.UseTexture(image);
                texture.Name = image.Name;
                indexBySlot[slot] = texture.LogicalIndex;
            }

            result.AddRange(M2Fx.M2FxReader.ReadMountedEmitters(
                effect.M2,
                slot => indexBySlot.TryGetValue(slot, out int i) ? i : null,
                effect.MountMesh));
        }

        return result;
    }

    /// <summary>Byte-identical image content? Encoding is deterministic here (both sides come out of
    /// the same ConvertBlpToPngBytes call for the same slot), so equality is exact rather than
    /// perceptual.</summary>
    private static bool SameImage(SharpGLTF.Schema2.Image? image, byte[] png)
    {
        if (image is null) return false;
        var content = image.Content.Content;
        return content.Length == png.Length && content.Span.SequenceEqual(png);
    }

    /// <summary>
    /// Build a mapping of submeshIndex → the M2Color record's rest RGB, via
    ///   batch.SubmeshIndex → batch.ColorIndex → m2.ReachableRestColors[idx].Rgb.
    ///
    /// This is the tint the M2 author put on the material pass, and it was previously discarded:
    /// nothing in this file ever read ReachableRestColors, so a pass authored as a coloured glow
    /// exported white and the previewer showed white. Only the ~1% of batches that reference a
    /// colour record are affected; everything else stays at (1,1,1) and renders identically.
    ///
    /// Values outside 0–1 are dropped rather than clamped here — an out-of-range tint means the
    /// chain resolved to something that is not a colour, and a wrong tint is worse than none.
    /// </summary>
    private static Dictionary<int, Vector3> BuildSubmeshTintMap(M2Model m2)
    {
        var map = new Dictionary<int, Vector3>();

        foreach (var batch in BatchesInDrawOrder(m2))
        {
            int subIdx = batch.SubmeshIndex;
            if (map.ContainsKey(subIdx)) continue;
            if (batch.ColorIndex < 0) continue;
            if (!m2.ReachableRestColors.TryGetValue(batch.ColorIndex, out var rest)) continue;

            var rgb = rest.Rgb;
            if (!float.IsFinite(rgb.X) || !float.IsFinite(rgb.Y) || !float.IsFinite(rgb.Z)) continue;
            if (rgb.X < 0f || rgb.Y < 0f || rgb.Z < 0f) continue;
            if (rgb.X > 1f || rgb.Y > 1f || rgb.Z > 1f) continue;

            map[subIdx] = rgb;
        }

        return map;
    }

    /// <summary>
    /// The model's batches in the order the client composites them: material layer ascending,
    /// source order within a layer. Every "base batch of a submesh" decision in this writer — the
    /// blend/texture/alpha/tint maps and EmitSubmesh's batches[0] — must go through this, never
    /// through raw m2.Batches order.
    ///
    /// Why: file order is NOT draw order. TBC Tier 5/6 plate (Onslaught, Lightbringer, ...) helms
    /// and spaulders list their layer-1 pass FIRST — an alpha-blended (blend 2, no depth write)
    /// redraw of the skin whose alpha channel is a shininess mask over the env-mapped base — and the
    /// layer-0 opaque diffuse+reflect base SECOND. Taking the first batch as the base exported only
    /// the mask pass as a BLEND material, so the helm rendered see-through in the previewer (the
    /// Onslaught skin has no fully-opaque texel) while the real base was dropped as a "blend &lt; 3
    /// overlay". Vanilla models list layer 0 first, so they are unchanged by this.
    /// </summary>
    internal static IReadOnlyList<M2Batch> BatchesInDrawOrder(M2Model m2)
        => m2.Batches
            .Select((batch, sourceIndex) => (batch, sourceIndex))
            .OrderBy(x => x.batch.MaterialLayer)
            .ThenBy(x => x.sourceIndex)
            .Select(x => x.batch)
            .ToList();

    /// <summary>
    /// Build a mapping of submeshIndex → blendMode using the batch chain:
    ///   batch.SubmeshIndex → batch.MaterialIndex → m2.RenderFlags[idx] → blendingMode.
    /// First-wins on duplicates in DRAW order (one batch per submesh is the common case;
    /// when there are layered batches on the same submesh, the lowest material layer
    /// is the base material). Submeshes with no batch reference fall back to
    /// opaque (0) via the caller's ContainsKey check.
    /// </summary>
    private static Dictionary<int, int> BuildSubmeshBlendMap(M2Model m2)
    {
        var map = new Dictionary<int, int>();

        foreach (var batch in BatchesInDrawOrder(m2))
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

        foreach (var batch in BatchesInDrawOrder(m2))
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
    /// The texture-coordinate combo VALUE for one unit of a batch.
    ///
    /// 0xFFFF as a VALUE means environment-mapped: the client samples by view normal, not by UV.
    /// 0 / 1 select UV0 / UV1.
    ///
    /// The 0xFFFF START INDEX is an unrelated thing — the old-format sentinel for "this batch
    /// declares no coordinate combo at all". Masking that with 0x8000 without checking reports every
    /// such batch as environment-mapped, because 0xFFFF &amp; 0x8000 != 0. Guard the start index first,
    /// then read the table.
    /// </summary>
    private static ushort ResolveCoordCombo(M2Model m2, M2Batch batch, int unit)
    {
        if (batch.TextureCoordinateIndex == ushort.MaxValue) return 0;   // no combo → UV0, not env
        long combo = (long)batch.TextureCoordinateIndex + unit;
        if (combo < 0 || combo >= m2.TextureCoordinateLookup.Count) return 0;
        return m2.TextureCoordinateLookup[(int)combo];
    }

    /// <summary>Transform-combo value for one unit — used only to tell the two units of a fused
    /// pass apart. 0xFFFF means "no transform".</summary>
    private static ushort ResolveTransformCombo(M2Model m2, M2Batch batch, int unit)
    {
        if (batch.TextureTransformIndex == ushort.MaxValue) return ushort.MaxValue;
        long combo = (long)batch.TextureTransformIndex + unit;
        if (combo < 0 || combo >= m2.TextureTransformLookup.Count) return ushort.MaxValue;
        return m2.TextureTransformLookup[(int)combo];
    }

    /// <summary>M2 texture slot for one unit of a batch: batch.TextureIndex + unit → TextureLookup.
    /// −1 when the combo is out of range. Callers must SKIP that unit rather than substitute
    /// anything: an overlay drawn with a substituted texture is a second full copy of the submesh
    /// composited over itself in the base skin.</summary>
    private static int ResolveTextureSlot(M2Model m2, M2Batch batch, int unit)
    {
        long combo = (long)batch.TextureIndex + unit;
        if (combo < 0 || combo >= m2.TextureLookup.Count) return -1;
        return m2.TextureLookup[(int)combo];
    }

    /// <summary>Rest tint for ONE batch — the same validation BuildSubmeshTintMap applies, but per
    /// batch rather than first-batch-wins, because an overlay layer carries its own colour record.</summary>
    private static Vector3? RestTintForBatch(M2Model m2, M2Batch batch)
    {
        if (batch.ColorIndex < 0) return null;
        if (!m2.ReachableRestColors.TryGetValue(batch.ColorIndex, out var rest)) return null;
        var rgb = rest.Rgb;
        if (!float.IsFinite(rgb.X) || !float.IsFinite(rgb.Y) || !float.IsFinite(rgb.Z)) return null;
        if (rgb.X < 0f || rgb.Y < 0f || rgb.Z < 0f) return null;
        if (rgb.X > 1f || rgb.Y > 1f || rgb.Z > 1f) return null;
        return rgb;
    }

    /// <summary>
    /// The distinct texture indices the geometry actually samples, resolved via the
    /// same batch → TextureLookup → Textures chain the material builder uses. Anything
    /// baking a recolor into the GLB must target one of THESE indices, not a Type
    /// heuristic — otherwise the recolor lands on a slot no submesh references.
    /// </summary>
    public static IReadOnlyList<int> SampledTextureIndices(M2Model m2)
        => BuildSubmeshTextureMap(m2).Values.Distinct().ToList();

    /// <summary>Pure guard for the legacy first-available texture rescue. Preserved source graphs
    /// opt out because their replaceable texture types are not interchangeable.</summary>
    internal static bool MaySubstituteMissingTexture(
        bool exactOnly, bool strictTextureSlots, int decodedTextureCount)
        => !exactOnly && !strictTextureSlots && decodedTextureCount > 0;

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

        foreach (var batch in BatchesInDrawOrder(m2))
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

    /// <summary>Vertex for a fused multi-texture MODULATE primitive.
    ///
    /// TEXCOORD_0 carries the SCROLLING unit's mapping — m2fx.js drives map.matrix, which is
    /// channel 0. TEXCOORD_1 carries the STATIC unit's mapping; blend-suffix.js applyMultiTexture
    /// reads it through the aoMap slot (vAoMapUv) and multiplies it into diffuseColor. The product
    /// of the two samples is the travelling wave. Two separate additive passes could only ADD,
    /// which lights the whole area instead — the "full glow" bug.
    ///
    /// M2Vertex.TexU2/TexV2 are parsed on both the vanilla and WotLK lanes and were discarded here
    /// and nowhere else.</summary>
    private static VERTEX2 MakeVertex2(M2Vertex v, bool scrollUsesUv1, bool staticUsesUv1)
    {
        var uv0 = scrollUsesUv1 ? new Vector2(v.TexU2, v.TexV2) : new Vector2(v.TexU, v.TexV);
        var uv1 = staticUsesUv1 ? new Vector2(v.TexU2, v.TexV2) : new Vector2(v.TexU, v.TexV);
        return new VERTEX2(
            new VertexPositionNormal(new Vector3(v.PosX, v.PosY, v.PosZ), new Vector3(v.NormX, v.NormY, v.NormZ)),
            new VertexTexture2(uv0, uv1)
        );
    }

    /// <summary>Skinned twin of <see cref="MakeVertex"/>. Joints/weights come from the one shared
    /// policy in <see cref="SkinnedGlbWriter.ResolveJoints"/> so the item rig and the character rig
    /// cannot disagree about what a malformed weight means.</summary>
    private static SKIN_VERTEX MakeSkinVertex(M2Vertex v, int boneCount)
    {
        return new SKIN_VERTEX(
            new VertexPositionNormal(new Vector3(v.PosX, v.PosY, v.PosZ), new Vector3(v.NormX, v.NormY, v.NormZ)),
            new VertexTexture1(new Vector2(v.TexU, v.TexV)),
            SkinnedGlbWriter.ResolveJoints(v, boneCount));
    }

    /// <summary>Skinned twin of <see cref="MakeVertex2"/> - the fused two-UV MODULATE primitive.</summary>
    private static SKIN_VERTEX2 MakeSkinVertex2(M2Vertex v, bool scrollUsesUv1, bool staticUsesUv1, int boneCount)
    {
        var uv0 = scrollUsesUv1 ? new Vector2(v.TexU2, v.TexV2) : new Vector2(v.TexU, v.TexV);
        var uv1 = staticUsesUv1 ? new Vector2(v.TexU2, v.TexV2) : new Vector2(v.TexU, v.TexV);
        return new SKIN_VERTEX2(
            new VertexPositionNormal(new Vector3(v.PosX, v.PosY, v.PosZ), new Vector3(v.NormX, v.NormY, v.NormZ)),
            new VertexTexture2(uv0, uv1),
            SkinnedGlbWriter.ResolveJoints(v, boneCount));
    }

    /// <summary>
    /// Convert BLP data to PNG bytes using SkiaSharp (Linux-compatible).
    /// Original extractor used System.Drawing (Windows-only GDI+).
    /// </summary>
    internal static byte[]? ConvertBlpToPngBytes(byte[] blpData)
    {
        try
        {
            var pixels = BlpDecoder.GetPixels(blpData, 0, out int w, out int h);
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

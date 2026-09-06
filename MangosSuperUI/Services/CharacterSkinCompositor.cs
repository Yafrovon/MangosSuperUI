using SkiaSharp;

namespace MangosSuperUI.Services;

/// <summary>
/// Composites the default character skin PNG by layering CharSections.dbc
/// face textures onto the body skin BLP at the canonical body-atlas
/// face regions.
///
/// === Why this exists (Session R) ===
/// Before this service, /character_textures/skin/{Race}{Gender}Skin00_00.png
/// was a raw BLP→PNG conversion of the base body skin only. For Human
/// Female and Troll Female that produced visible open eyes because their
/// base BLPs have eye detail baked in; for every other race/gender combo
/// the eye area was just skin-tone, so characters rendered "closed-eyed".
///
/// The vanilla client builds character skins by compositing
/// CharSections face textures onto the body atlas at the FACE_LOWER and
/// FACE_UPPER regions. This service performs the same composite.
///
/// === Pipeline ===
///   1. Resolve race+gender to a CharSectionDbc Face row at
///      (BaseSection=Face, variationIndex=0, colorIndex=0) via
///      DbcService.GetDefaultFaceSection.
///   2. Extract textureName[0] BLP (face_lower) and textureName[1] BLP
///      (face_upper) from the MPQ via MpqReaderService.
///   3. Decode base skin + face BLPs to SKBitmaps.
///   4. Paint face_lower onto (0, 192, 128, 64).
///      Paint face_upper onto (0, 160, 128, 32).
///      Coordinates mirror wwwroot/js/character-viewer/region-rects.js.
///   5. Encode composite to PNG and return bytes.
///
/// === BLP path resolution ===
/// CharSections stringrefs typically contain race/gender-specific
/// partial paths like "Character\Human\Male\HumanMaleFaceUpper00_00".
/// We try three candidates and use the first hit:
///   a) {partial}.blp                                              — as stored
///   b) Character\{partial}.blp                                    — for short refs
///   c) Character\{race}\{gender}\{partial}.blp                    — for bare names
/// Empirically (a) covers vanilla; (b)(c) are belt-and-suspenders for
/// any edge-case row that ships a shortened path.
///
/// === Cache versioning ===
/// This service is the SkinPngVersion witness type in
/// CacheVersionRegistry. Any change to this file changes the assembly
/// MVID and auto-invalidates every cached skin PNG on next startup —
/// no manual version bumps. See CacheVersionRegistry class doc for
/// the full mechanic.
/// </summary>
public class CharacterSkinCompositor
{
    private readonly MpqReaderService _mpq;
    private readonly DbcService _dbc;
    private readonly ILogger<CharacterSkinCompositor> _logger;

    public CharacterSkinCompositor(
        MpqReaderService mpq,
        DbcService dbc,
        ILogger<CharacterSkinCompositor> logger)
    {
        _mpq = mpq;
        _dbc = dbc;
        _logger = logger;
    }

    // Canonical face regions on the 256×256 body atlas — mirrors
    // wwwroot/js/character-viewer/region-rects.js exactly.
    private const int FACE_LOWER_X = 0, FACE_LOWER_Y = 192,
                      FACE_LOWER_W = 128, FACE_LOWER_H = 64;
    private const int FACE_UPPER_X = 0, FACE_UPPER_Y = 160,
                      FACE_UPPER_W = 128, FACE_UPPER_H = 32;

    /// <summary>
    /// Build the composited skin PNG bytes for (race, gender). Returns
    /// null on hard failure so the caller can fall back to bare skin.
    /// Partial failures (e.g. face_upper resolves but face_lower doesn't)
    /// return a partially-composited canvas — better than nothing.
    /// </summary>
    public byte[]? ComposeDefaultSkin(string raceName, string genderName, byte[] baseSkinBlp)
    {
        try
        {
            // Decode base skin → working bitmap
            using var canvas = DecodeBlp(baseSkinBlp);
            if (canvas == null)
            {
                _logger.LogWarning(
                    "CharacterSkinCompositor: base skin decode failed for {Race}/{Gender}",
                    raceName, genderName);
                return null;
            }
            _logger.LogInformation(
                "CharacterSkinCompositor: base skin decoded {W}x{H} for {Race}/{Gender}",
                canvas.Width, canvas.Height, raceName, genderName);

            // Resolve race ID + gender ID for DBC lookup
            uint raceId = RaceNameToId(raceName);
            uint genderId = genderName.Equals("Female", StringComparison.OrdinalIgnoreCase) ? 1u : 0u;
            if (raceId == 0)
            {
                _logger.LogWarning(
                    "CharacterSkinCompositor: unknown race '{Race}' — returning bare skin",
                    raceName);
                return EncodePng(canvas);
            }

            // Find default Face row (BaseSection=Face, varIdx=0, colIdx=0)
            var faceRow = _dbc.GetDefaultFaceSection(raceId, genderId);
            if (faceRow == null)
            {
                _logger.LogWarning(
                    "CharacterSkinCompositor: no Face row in CharSections.dbc for race={Race} sex={Sex} — returning bare skin",
                    raceId, genderId);
                return EncodePng(canvas);
            }

            // Composite face_lower (textureName[0]) → FACE_LOWER region
            if (!string.IsNullOrEmpty(faceRow.TextureName1))
            {
                var lowerBlp = ResolveBlp(faceRow.TextureName1, raceName, genderName);
                if (lowerBlp != null)
                {
                    using var bmp = DecodeBlp(lowerBlp);
                    if (bmp != null)
                    {
                        _logger.LogInformation(
                            "CharacterSkinCompositor: face_lower BLP decoded {W}x{H} for {Race}/{Gender}, painting into region ({RX},{RY},{RW},{RH})",
                            bmp.Width, bmp.Height, raceName, genderName,
                            FACE_LOWER_X, FACE_LOWER_Y, FACE_LOWER_W, FACE_LOWER_H);
                        PaintRegion(canvas, bmp, FACE_LOWER_X, FACE_LOWER_Y, FACE_LOWER_W, FACE_LOWER_H);
                        _logger.LogDebug(
                            "CharacterSkinCompositor: painted face_lower for {Race}/{Gender} from '{Partial}'",
                            raceName, genderName, faceRow.TextureName1);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "CharacterSkinCompositor: face_lower BLP not in MPQ — partial='{Partial}' (race={Race} gender={Gender})",
                        faceRow.TextureName1, raceName, genderName);
                }
            }

            // Composite face_upper (textureName[1]) → FACE_UPPER region
            if (!string.IsNullOrEmpty(faceRow.TextureName2))
            {
                var upperBlp = ResolveBlp(faceRow.TextureName2, raceName, genderName);
                if (upperBlp != null)
                {
                    using var bmp = DecodeBlp(upperBlp);
                    if (bmp != null)
                    {
                        _logger.LogInformation(
                            "CharacterSkinCompositor: face_upper BLP decoded {W}x{H} for {Race}/{Gender}, painting into region ({RX},{RY},{RW},{RH})",
                            bmp.Width, bmp.Height, raceName, genderName,
                            FACE_UPPER_X, FACE_UPPER_Y, FACE_UPPER_W, FACE_UPPER_H);
                        PaintRegion(canvas, bmp, FACE_UPPER_X, FACE_UPPER_Y, FACE_UPPER_W, FACE_UPPER_H);
                        _logger.LogDebug(
                            "CharacterSkinCompositor: painted face_upper for {Race}/{Gender} from '{Partial}'",
                            raceName, genderName, faceRow.TextureName2);
                    }
                }
                else
                {
                    _logger.LogWarning(
                        "CharacterSkinCompositor: face_upper BLP not in MPQ — partial='{Partial}' (race={Race} gender={Gender})",
                        faceRow.TextureName2, raceName, genderName);
                }
            }

            return EncodePng(canvas);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "CharacterSkinCompositor: compose failed for {Race}/{Gender}",
                raceName, genderName);
            return null;
        }
    }

    /// <summary>
    /// Try multiple BLP path candidates for a CharSections texture
    /// partial-name. Returns the first hit, or null if nothing matches.
    /// </summary>
    /// <summary>A CharSections row for one appearance choice, with the vanilla fallbacks the
    /// client itself applies: colour 0 of the same variation, then variation 0 / colour 0.</summary>
    private CharSectionDbc? FindSection(uint race, uint sex, uint section, uint variation, uint color)
    {
        // Exact row only — the client has no fallback lookup (MSUIClient CharSectionsTable.Find is a
        // straight match), so guessing a neighbouring colour here paints a look the game never shows.
        return _dbc.CharacterSections.FirstOrDefault(r =>
            r.Race == race && r.Sex == sex && r.BaseSection == section && r.VariationIndex == variation && r.ColorIndex == color);
    }

    /// <summary>The hair row whose sheet is blank resolves to variation 1 at the same colour — benilla's
    /// hair_mesh_texture rule, which MSUIClient mirrors as HairSubstituteVariation. Human male style 0
    /// is the canonical case: its row names no sheet, and the substitute is Hair03_&lt;colour&gt;.</summary>
    private const uint HairSubstituteVariation = 1;

    /// <summary>The body atlas for a SPECIFIC character: their skin colour, face, hair colour
    /// (scalp overlays) and facial hair (beard overlays) from CharSections.dbc, composited the same
    /// way <see cref="ComposeDefaultSkin"/> builds the race/gender template. <paramref name="hairTexturePartial"/>
    /// comes back with the hair-colour texture the hair geosets should wear. Null on hard failure.</summary>
    public byte[]? ComposeCharacterSkin(string raceName, string genderName,
        uint skin, uint face, uint hairStyle, uint hairColor, uint facialHair, out string? hairTexturePartial)
    {
        hairTexturePartial = null;
        try
        {
            uint raceId = RaceNameToId(raceName);
            uint genderId = genderName.Equals("Female", StringComparison.OrdinalIgnoreCase) ? 1u : 0u;
            if (raceId == 0) return null;

            // Base skin: CharSections section 0, colour = skin variant.
            var skinRow = FindSection(raceId, genderId, 0, 0, skin);
            byte[]? baseBlp = skinRow is not null && !string.IsNullOrEmpty(skinRow.TextureName1)
                ? ResolveBlp(skinRow.TextureName1, raceName, genderName) : null;
            baseBlp ??= _mpq.ExtractFile($"Character\\{raceName}\\{genderName}\\{raceName}{genderName}Skin00_{skin:00}.blp")
                     ?? _mpq.ExtractFile($"Character\\{raceName}\\{genderName}\\{raceName}{genderName}Skin00_00.blp");
            if (baseBlp is null) return null;
            using var canvas = DecodeBlp(baseBlp);
            if (canvas is null) return null;

            void Paint(string? partial, int x, int y, int w, int h)
            {
                if (string.IsNullOrEmpty(partial)) return;
                var blp = ResolveBlp(partial, raceName, genderName);
                if (blp is null) return;
                using var bmp = DecodeBlp(blp);
                if (bmp is not null) PaintRegion(canvas, bmp, x, y, w, h);
            }

            // Face: section 1, variation = face, colour = skin (faces are painted per skin tone).
            var faceRow = FindSection(raceId, genderId, 1, face, skin);
            if (faceRow is not null)
            {
                Paint(faceRow.TextureName1, FACE_LOWER_X, FACE_LOWER_Y, FACE_LOWER_W, FACE_LOWER_H);
                Paint(faceRow.TextureName2, FACE_UPPER_X, FACE_UPPER_Y, FACE_UPPER_W, FACE_UPPER_H);
            }

            // Facial hair: section 2, variation = facial hair, colour = hair colour. Its lower/upper
            // sheets overlay the face regions (sideburns, moustache shading) under the scalp overlays.
            var facialRow = FindSection(raceId, genderId, 2, facialHair, hairColor);
            if (facialRow is not null)
            {
                Paint(facialRow.TextureName1, FACE_LOWER_X, FACE_LOWER_Y, FACE_LOWER_W, FACE_LOWER_H);
                Paint(facialRow.TextureName2, FACE_UPPER_X, FACE_UPPER_Y, FACE_UPPER_W, FACE_UPPER_H);
            }

            // Hair: section 3, variation = style, colour = hair colour. TextureName1 is the hair-colour
            // sheet the hair geosets wear; 2 and 3 are the scalp overlays painted onto the face regions.
            var hairRow = FindSection(raceId, genderId, 3, hairStyle, hairColor);
            if (hairRow is not null)
            {
                hairTexturePartial = string.IsNullOrEmpty(hairRow.TextureName1) ? null : hairRow.TextureName1;
                Paint(hairRow.TextureName2, FACE_LOWER_X, FACE_LOWER_Y, FACE_LOWER_W, FACE_LOWER_H);
                Paint(hairRow.TextureName3, FACE_UPPER_X, FACE_UPPER_Y, FACE_UPPER_W, FACE_UPPER_H);
            }
            if (hairTexturePartial is null)
            {
                var substitute = FindSection(raceId, genderId, 3, HairSubstituteVariation, hairColor);
                if (substitute is not null && !string.IsNullOrEmpty(substitute.TextureName1))
                    hairTexturePartial = substitute.TextureName1;
            }

            return EncodePng(canvas);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "CharacterSkinCompositor: character skin composite failed for {Race}/{Gender}", raceName, genderName);
            return null;
        }
    }

    /// <summary>Decode any character BLP (hair sheets, for one) to PNG bytes.</summary>
    public byte[]? BlpToPng(byte[] blp)
    {
        try { using var bmp = DecodeBlp(blp); return bmp is null ? null : EncodePng(bmp); }
        catch { return null; }
    }

    /// <summary>Resolve a CharSections partial to BLP bytes (public for the Armory's hair sheet).</summary>
    public byte[]? ResolveCharacterBlp(string partial, string raceName, string genderName) => ResolveBlp(partial, raceName, genderName);

    private byte[]? ResolveBlp(string partial, string raceName, string genderName)
        => ResolveCharacterTextureBlp(_mpq, partial, raceName, genderName);

    /// <summary>
    /// Resolve a CharSections.dbc texture stringref to BLP bytes by trying
    /// multiple candidate MPQ paths. Public + static so CharacterModelService
    /// (and any future caller) can reuse the same path-resolution rules
    /// without instantiating the compositor.
    ///
    /// Path-resolution rules:
    ///
    ///   * CharSections stringrefs include the ".blp" extension already
    ///     (e.g. "Character\Human\Male\HumanMaleFaceLower00_00.blp"). The
    ///     extension is stripped first so the candidate builder is the
    ///     single source of truth — this avoids the doubled-".blp" bug.
    ///   * Forward slashes are normalized to backslashes (vanilla MPQ
    ///     convention).
    ///   * Three candidates are tried in order: as-stored full path,
    ///     prepended "Character\", and full constructed path under
    ///     Character\&lt;race&gt;\&lt;gender&gt;\. Empirically the first
    ///     covers vanilla; the others are belt-and-suspenders.
    /// </summary>
    public static byte[]? ResolveCharacterTextureBlp(
        MpqReaderService mpq, string partial, string raceName, string genderName)
    {
        if (string.IsNullOrEmpty(partial)) return null;

        var part = partial.Replace('/', '\\').TrimStart('\\');
        if (part.EndsWith(".blp", StringComparison.OrdinalIgnoreCase))
            part = part.Substring(0, part.Length - 4);

        var candidates = new[]
        {
            $"{part}.blp",                                           // full path as-stored
            $"Character\\{part}.blp",                                // short ref
            $"Character\\{raceName}\\{genderName}\\{part}.blp",      // bare name
        };

        foreach (var c in candidates)
        {
            var data = mpq.ExtractFile(c);
            if (data != null) return data;
        }
        return null;
    }

    /// <summary>
    /// Paint a CharSections face texture onto the body atlas.
    ///
    /// THE CLOSED-EYES BUG
    ///   This used to unconditionally stretch the WHOLE source into the target
    ///   rectangle: src = (0,0,source.W,source.H) -> dst = (0,192,128,64).
    ///   That is right only if the BLP is already a face-sized strip.
    ///
    ///   CharSections face textures are, for most race/gender combinations,
    ///   FULL-CANVAS 256x256 overlays: a mostly-transparent sheet with the face
    ///   detail already positioned where it belongs on the atlas. Squashing one
    ///   of those into a 128x64 rectangle crushes the eyes into a couple of
    ///   texels of smeared skin tone — which is exactly "the eyes are closed".
    ///
    ///   WoWee composites these as full-canvas layers (compositePlayerSkin
    ///   stacks [bodySkin, faceLower, faceUpper, ...underwear] at 1:1).
    ///   MSUIClient flagged this as the prime suspect but never confirmed it:
    ///   "If SuperUI is squashing a full-canvas texture into a 128x64 rect,
    ///   that is the eyes bug."
    ///
    /// THE FIX IS SELF-SELECTING
    ///   The source tells us which kind it is. If it is atlas-sized, draw it
    ///   1:1 over the whole canvas and let its own alpha place it. Otherwise it
    ///   really is a strip, and the original rect-stretch is correct.
    ///
    ///   That means this cannot regress the combinations that already worked
    ///   (Human Female and Troll Female rendered open eyes), because for those
    ///   the branch taken is unchanged unless the source is genuinely
    ///   full-canvas — in which case they were being squashed too and simply
    ///   survived it better.
    /// </summary>
    private static void PaintRegion(SKBitmap canvas, SKBitmap source,
        int x, int y, int w, int h)
    {
        using var surface = new SKCanvas(canvas);
        using var paint = new SKPaint { FilterQuality = SKFilterQuality.High };

        bool fullCanvas = source.Width == canvas.Width && source.Height == canvas.Height;
        if (fullCanvas)
        {
            // 1:1 overlay. The texture's own alpha decides where it lands; the
            // region rectangle is not involved at all.
            surface.DrawBitmap(source, 0, 0, paint);
            return;
        }

        var dst = new SKRect(x, y, x + w, y + h);
        var src = new SKRect(0, 0, source.Width, source.Height);
        surface.DrawBitmap(source, src, dst, paint);
    }

    private static SKBitmap? DecodeBlp(byte[] blpData)
    {
        try
        {
            var pixels = BlpDecoder.GetPixels(blpData, 0, out int w, out int h);
            if (w == 0 || h == 0 || pixels.Length == 0) return null;

            var bmp = new SKBitmap(w, h, SKColorType.Bgra8888, SKAlphaType.Unpremul);
            var bitmapPixels = bmp.GetPixels();
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, bitmapPixels, pixels.Length);
            bmp.NotifyPixelsChanged();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? EncodePng(SKBitmap bmp)
    {
        try
        {
            using var ms = new MemoryStream();
            bmp.Encode(ms, SKEncodedImageFormat.Png, 100);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    // Vanilla CharRaces.dbc IDs. Race folder names match
    // CharacterModelService.NormalizeRace so MPQ paths agree.
    private static uint RaceNameToId(string raceName) => raceName.ToLowerInvariant() switch
    {
        "human" => 1,
        "orc" => 2,
        "dwarf" => 3,
        "nightelf" => 4,
        "scourge" => 5,
        "undead" => 5,
        "tauren" => 6,
        "gnome" => 7,
        "troll" => 8,
        _ => 0,
    };
}
using System.Text.Json;
using MangosSuperUI.Services;
using MangosSuperUI.Services.ArmorForge;
using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

/// <summary>Original-art Armor Forge lane. Stage/preview is DB-free; Build is an explicit registry/world write.
/// Neither action deploys patches, restarts processes, nor marks assets as game-verified.</summary>
public sealed class AuthoredArmorController(MpqReaderService mpq, BlpWriterService blp,
    CustomArmorBuildService armor, DbcService dbc, IWebHostEnvironment env, ILogger<AuthoredArmorController> logger) : Controller
{
    private static readonly SemaphoreSlim StageGate = new(1, 1);
    private string Root => Path.Combine(env.ContentRootPath, "App_Data", "authored-armor");
    private static bool ValidId(string? id) => id is { Length: 64 } && id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f');
    private string StageDir(string id) => ValidId(id) ? Path.Combine(Root, id) : throw new ArgumentException("Invalid package ID.");
    private AuthoredArmorPackage ReadPackage(string id)
    {
        var package = AuthoredArmorPackage.Read(System.IO.File.ReadAllBytes(Path.Combine(StageDir(id), "package.zip")));
        if (package.Sha256 != id) throw new InvalidDataException("Stored package hash mismatch.");
        return package;
    }

    [HttpPost, RequestSizeLimit(AuthoredArmorPackage.MaxZipBytes + 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit = AuthoredArmorPackage.MaxZipBytes + 1024 * 1024)]
    public async Task<IActionResult> Stage(IFormFile? package)
    {
        if (package is null || package.Length is < 1 or > AuthoredArmorPackage.MaxZipBytes) return BadRequest(new { message = "Choose a ZIP of at most 32 MiB." });
        await StageGate.WaitAsync(HttpContext.RequestAborted);
        try
        {
            using var stream = new MemoryStream(); await package.CopyToAsync(stream, HttpContext.RequestAborted);
            var bytes = stream.ToArray(); var input = AuthoredArmorPackage.Read(bytes);
            var compilation = new AuthoredArmorCompiler(mpq, blp).Compile(input);
            if (!compilation.Report.Compiled) return UnprocessableEntity(new { id = input.Sha256, report = compilation.Report });
            string directory = StageDir(input.Sha256);
            if (!Directory.Exists(directory) && Directory.Exists(Root) && Directory.GetDirectories(Root).Length >= 32)
                return Conflict(new { message = "32 authored packages are already staged. Archive completed staging packages before adding more." });
            Directory.CreateDirectory(directory);
            AuthoredArmorCompiler.WritePreviews(compilation, directory);
            await System.IO.File.WriteAllBytesAsync(Path.Combine(directory, "package.zip"), bytes, HttpContext.RequestAborted);
            await System.IO.File.WriteAllTextAsync(Path.Combine(directory, "report.json"), JsonSerializer.Serialize(compilation.Report, AuthoredArmorPackage.JsonOptions), HttpContext.RequestAborted);
            return Json(new { id = input.Sha256, report = compilation.Report,
                pieces = input.Manifest.Pieces.Select((p, i) => new { key = p.Key, p.Name, displayId = i + 1, inventoryType = ArmorTypeCatalog.Get(p.Key).InventoryType }) });
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException or IOException)
        { return BadRequest(new { message = ex.Message }); }
        finally { StageGate.Release(); }
    }

    [HttpPost]
    public async Task<IActionResult> Build(string id)
    {
        if (!ValidId(id)) return BadRequest(new { message = "Invalid package ID." });
        try
        {
            var result = await armor.BuildAuthoredSetAsync(ReadPackage(id));
            return Json(new { result, runtimeStatus = "unverified", sourcePackageSha256 = id });
        }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or InvalidOperationException or IOException)
        { return BadRequest(new { message = ex.Message, runtimeStatus = "unverified" }); }
    }

    [HttpGet]
    public IActionResult Report(string id)
    {
        if (!ValidId(id)) return BadRequest();
        string path = Path.Combine(StageDir(id), "report.json");
        return System.IO.File.Exists(path) ? PhysicalFile(path, "application/json") : NotFound();
    }

    [HttpGet]
    public async Task<IActionResult> BuiltReport(string id)
    {
        if (!ValidId(id)) return BadRequest(new { message = "Invalid package ID." });
        try
        {
            var report = await armor.LoadAuthoredBuiltEvidenceAsync(id);
            return report is null ? NotFound(new { message = "This source package has no saved armor set." }) : Json(report);
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
        { return UnprocessableEntity(new { message = ex.Message, runtimeVerified = false }); }
        catch (System.Data.Common.DbException ex)
        {
            logger.LogError(ex, "Could not read saved authored-armor evidence for source package {PackageId}", id);
            return StatusCode(503, new { message = "Saved armor evidence could not be read from the registry. See the application diagnostic log.", runtimeVerified = false });
        }
    }

    [HttpGet]
    public IActionResult Asset(string id, string file)
    {
        if (!ValidId(id) || string.IsNullOrEmpty(file) || file.Length > 80 || !file.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '.')
            || !(file.EndsWith(".png", StringComparison.Ordinal) || file.EndsWith(".glb", StringComparison.Ordinal))) return BadRequest();
        string path = Path.Combine(StageDir(id), file);
        return System.IO.File.Exists(path) ? PhysicalFile(path, file.EndsWith(".png") ? "image/png" : "model/gltf-binary") : NotFound();
    }

    [HttpGet]
    public IActionResult Dressing(string id, string key, string race = "Human", string gender = "Male")
    {
        if (!ValidId(id)) return BadRequest();
        try
        {
            var package = ReadPackage(id); var piece = package.Manifest.Pieces.FirstOrDefault(p => p.Key == key);
            if (piece is null) return NotFound();
            bool female = gender == "Female";
            var (raceCode, raceId) = race switch
            {
                "Human" => ("Hu", 1u), "Orc" => ("Or", 2u), "Dwarf" => ("Dw", 3u), "NightElf" => ("Ni", 4u),
                "Scourge" or "Undead" => ("Sc", 5u), "Tauren" => ("Ta", 6u), "Gnome" => ("Gn", 7u), "Troll" => ("Tr", 8u),
                _ => throw new ArgumentException("Unknown vanilla race."),
            };
            if (gender is not ("Male" or "Female")) return BadRequest(new { message = "Unknown gender." });
            var urls = new Dictionary<int, string>();
            foreach (int slot in piece.Components.Select(c => c.Slot).Distinct().Order())
            {
                var c = piece.Components.FirstOrDefault(c => c.Slot == slot && c.Gender == (female ? "_F" : "_M"))
                    ?? piece.Components.First(c => c.Slot == slot && c.Gender == "_U");
                urls[slot] = AssetUrl($"{key}_slot{slot}{c.Gender}.png");
            }
            var attachments = new Dictionary<string, string>();
            if (key == "helm") attachments["helm"] = AssetUrl($"helm_{raceCode}{(female ? "F" : "M")}.glb");
            var shoulderFits = new Dictionary<string, ShoulderFitSelection>();
            if (key == "shoulder")
            {
                foreach (string side in new[] { "L", "R" })
                {
                    string file = "shoulder_" + side + ".glb";
                    if (piece.ShoulderFits.Count > 0)
                    {
                        var selected = AuthoredArmorShoulderPreviews.Resolve(StageDir(id),
                            package.Manifest.Pieces.IndexOf(piece) + 1, side, raceCode + (female ? "F" : "M"));
                        file = selected.File;
                        shoulderFits.Add(side, selected.Source);
                    }
                    attachments[side == "L" ? "shoulderLeft" : "shoulderRight"] = AssetUrl(file);
                }
            }
            return Json(new { success = true, displayId = package.Manifest.Pieces.IndexOf(piece) + 1,
                itemId = package.Manifest.Pieces.IndexOf(piece) + 1, inventoryType = ArmorTypeCatalog.Get(key).InventoryType,
                geosetGroup = piece.GeosetGroup, slotUrls = urls, attachments, shoulderFits, bodyTextures = new string[8],
                helmetGeosetVis1 = piece.HelmetVis[0], helmetGeosetVis2 = piece.HelmetVis[1],
                hidesHair = key == "helm" && dbc.DoesHelmHideHair(piece.HelmetVis[female ? 1 : 0], raceId),
                name = piece.Name, runtimeStatus = "unverified", sourcePackageSha256 = id });
            string AssetUrl(string file) => "/AuthoredArmor/Asset?id=" + id + "&file=" + Uri.EscapeDataString(file);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or InvalidDataException)
        { return BadRequest(new { message = ex.Message }); }
    }

    [HttpGet]
    public IActionResult Schema() => Json(new
    {
        schemaVersion = 1,
        description = "Original art ZIP. All geometry uses glTF Y-up, one unit per WoW unit, with attachment origin preserved. No automatic fit, decimation, or substitution.",
        requiredPieces = new[] { "helm", "shoulder", "chest OR robe", "legs", "gloves", "boots", "bracers", "belt" },
        helmModels = ArmorNaming.HelmVariantSuffixes, shoulderModels = new[] { "L", "R" },
        shoulderFits = new { optional = true, bodyCodes = ShoulderFitResolver.BodyCodes, sides = new[] { "L", "R" },
            description = "Explicit authored GLB overrides: shoulderFits[body][side]. Both default models remain mandatory; omitted sides use their default. One shared skin. No runtime fitting or transform changes." },
        maxAttachmentTriangles = AuthoredArmorAssetValidator.MaxAttachmentTriangles,
        bodySlots = ArmorTypeCatalog.All.Where(p => p.PaintedSlots.Count > 0).Select(p => new { key = p.Key, slots = p.PaintedSlots.Select(s => new { slot = s, width = 128, height = LegacyArmorImporter.ComponentRegion(s).Height }) }),
        modelSkin = "Opaque PNG, each dimension 128 or 256 pixels; one shared skin per helm/shoulder piece.", icon = "64×64 PNG",
        exampleManifest = new AuthoredArmorManifest { SchemaVersion = 1, Name = "Original set name", Material = "plate", Author = "Artist", DesignNotes = "Shape/material reference study and authoring method.",
            Pieces = new() { new() { Key = "helm", Name = "Original helm", IconPng = "icons/helm.png", SkinPng = "textures/helm.png", Models = ArmorNaming.HelmVariantSuffixes.ToDictionary(s => s, s => $"models/helm_{s}.glb") },
                new() { Key = "chest", Name = "Original breastplate", IconPng = "icons/chest.png", Components = new[] { 0, 1, 3, 4 }.Select(s => new AuthoredArmorComponent { Slot = s, Gender = "_U", Png = $"textures/chest_{s}.png" }).ToList() } } },
        runtimeStatus = "unverified",
    });
}

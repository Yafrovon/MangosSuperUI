using MangosSuperUI.Services.ArmorForge;
using MangosSuperUI.Services.WeaponForge;
using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

[Route("EquipmentReference")]
public sealed class EquipmentReferenceController : Controller
{
    private readonly EquipmentReferenceService _references;

    public EquipmentReferenceController(LegacyImportSources sources, ArmorImportSources armor, WeaponPreviewService preview)
        => _references = new(sources, armor, preview);

    [HttpGet("Browse")]
    public IActionResult Browse(string source = "vanilla", string kind = "weapon", string? family = null,
        string? search = null, int skip = 0, int take = 40)
        => Read(() => Json(_references.Browse(source, kind, family, search, skip, take)));

    [HttpGet("Inspect")]
    public IActionResult Inspect(uint displayId, string source = "vanilla", string kind = "weapon",
        string? family = null, string raceGender = "HuM", bool preview = true)
        => Read(() => Json(_references.Inspect(source, kind, displayId, family, raceGender, preview, HttpContext.RequestAborted)));

    [HttpGet("Measure")]
    public IActionResult Measure(string source = "vanilla", string kind = "weapon", string? family = null,
        string? search = null, int skip = 0, int take = 40, string raceGender = "HuM")
        => Read(() => Json(_references.Measure(source, kind, family, search, skip, take, raceGender, HttpContext.RequestAborted)));

    [HttpGet("Texture")]
    public IActionResult Texture(uint displayId, int index, string source = "vanilla", string kind = "weapon",
        string? family = null, string raceGender = "HuM")
        => Read(() => File(_references.Texture(source, kind, displayId, family, raceGender, index), "image/png"));

    private IActionResult Read(Func<IActionResult> read)
    {
        try { return read(); }
        catch (ArgumentException ex) { return BadRequest(new { ok = false, error = ex.Message }); }
        catch (KeyNotFoundException ex) { return NotFound(new { ok = false, error = ex.Message }); }
        catch (InvalidOperationException ex) { return StatusCode(503, new { ok = false, error = ex.Message }); }
        catch (InvalidDataException ex) { return UnprocessableEntity(new { ok = false, error = ex.Message }); }
        catch (NotSupportedException ex) { return UnprocessableEntity(new { ok = false, error = ex.Message }); }
    }
}

using MangosSuperUI.Services.WeaponForge;
using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

public partial class WeaponForgeController
{
    [HttpGet]
    public async Task<IActionResult> RevisionModel(long displayId, long itemEntry, string expectedModelSha256)
    {
        try { return File(await _builder.ReadCurrentGeometryRevisionModelAsync(displayId, itemEntry, expectedModelSha256),
            "application/octet-stream", $"revision-{displayId}-{expectedModelSha256}.m2"); }
        catch (InvalidOperationException ex) { return Conflict(new { ok = false, error = ex.Message }); }
    }

    [HttpGet]
    public IActionResult RevisionReceipt(string revisionToken)
    {
        try { return File(_builder.ReadGeometryRevisionReceipt(revisionToken), "application/json", "weapon-revision-" + revisionToken + ".json"); }
        catch (InvalidOperationException ex) { return BadRequest(new { ok = false, error = ex.Message }); }
        catch (FileNotFoundException) { return NotFound(); }
    }

    [HttpGet]
    public async Task<IActionResult> RevisionTarget(long displayId, long itemEntry)
    {
        try { return Json(await _builder.ReadGeometryRevisionTargetAsync(displayId, itemEntry)); }
        catch (InvalidOperationException ex) { return BadRequest(new { ok = false, error = ex.Message }); }
    }

    [HttpPost]
    [RequestSizeLimit(WeaponGeometryRevision.MaxSourceBytes + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = WeaponGeometryRevision.MaxSourceBytes + 65536,
        ValueLengthLimit = 256, ValueCountLimit = 12)]
    public async Task<IActionResult> ReviseGlb(IFormFile? file, long displayId, long itemEntry,
        string expectedModelSha256, bool apply = false, string? expectedRevisionModelSha256 = null,
        string? expectedRevisionToken = null)
    {
        try
        {
            WeaponGeometryRevision.RequireHash(expectedModelSha256, nameof(expectedModelSha256));
            var (bytes, error) = await ReadBounded(file, WeaponGeometryRevision.MaxSourceBytes);
            if (error is not null) return BadRequest(new { ok = false, error });
            // Exact authored grip coordinates and topology: revision never auto-orients or decimates.
            var imported = _glbImporter.Import(bytes!, new GlbImportOptions { Reorient = false });
            if (!imported.Ok || imported.Mesh is null)
                return BadRequest(new { ok = false, error = "GLB import failed.",
                    diagnostics = imported.Diagnostics.Items.Select(x => x.ToString()) });
            return Json(await _builder.ReviseGeometryAsync(displayId, itemEntry, expectedModelSha256,
                imported.Mesh, bytes!, apply, expectedRevisionModelSha256, expectedRevisionToken));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or OverflowException)
        { return BadRequest(new { ok = false, error = ex.Message }); }
    }
}

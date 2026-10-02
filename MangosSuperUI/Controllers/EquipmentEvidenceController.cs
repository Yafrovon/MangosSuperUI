using System.Text.Json;
using MangosSuperUI.Services.ArmorForge;
using MangosSuperUI.Services.WeaponForge;
using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

[Route("EquipmentEvidence")]
public sealed class EquipmentEvidenceController(CustomWeaponBuildService weapons, CustomArmorBuildService armor,
    IWebHostEnvironment env) : Controller
{
    private static readonly SemaphoreSlim AttachGate = new(1, 1);
    private const int MaximumStoredReports = 128;
    private string Root => Path.Combine(env.ContentRootPath, "App_Data", "equipment-evidence");

    [HttpPost("Attach"), RequestSizeLimit(EquipmentEvidenceService.MaximumBytes + 65536)]
    [RequestFormLimits(MultipartBodyLengthLimit = EquipmentEvidenceService.MaximumBytes + 65536)]
    public async Task<IActionResult> Attach(IFormFile? evidence)
    {
        if (evidence is null || evidence.Length is < 1 or > EquipmentEvidenceService.MaximumBytes)
            return BadRequest(new { error = "Choose one evidence JSON file of at most 2 MiB." });
        await AttachGate.WaitAsync(HttpContext.RequestAborted);
        try
        {
            using var input = evidence.OpenReadStream();
            using var stream = new MemoryStream();
            byte[] buffer = new byte[16384];
            int read;
            while ((read = await input.ReadAsync(buffer, HttpContext.RequestAborted)) != 0)
            {
                if (stream.Length + read > EquipmentEvidenceService.MaximumBytes) return StatusCode(413, new { error = "Evidence exceeds 2 MiB." });
                await stream.WriteAsync(buffer.AsMemory(0, read), HttpContext.RequestAborted);
            }
            var bytes = stream.ToArray();
            var report = EquipmentEvidenceService.Analyze(bytes, weapons.TryGetMember, armor.TryGetMember, HttpContext.RequestAborted);
            byte[] reportBytes = JsonSerializer.SerializeToUtf8Bytes(report, EquipmentEvidenceService.JsonOptions);
            string id = EquipmentEvidenceService.Hash(reportBytes), directory = Path.Combine(Root, id);
            if (Directory.Exists(Root) && Directory.GetDirectories(Root).Length >= MaximumStoredReports)
                return Conflict(new { error = "The 128-report evidence limit has been reached. Archive reports before adding more." });
            Directory.CreateDirectory(directory);
            await System.IO.File.WriteAllBytesAsync(Path.Combine(directory, "input.json"), bytes, HttpContext.RequestAborted);
            await System.IO.File.WriteAllBytesAsync(Path.Combine(directory, "report.json"), reportBytes, HttpContext.RequestAborted);
            return Json(new { id, report, reportUrl = $"/EquipmentEvidence/Report?id={id}" });
        }
        catch (Exception ex) when (ex is InvalidDataException or JsonException or ArgumentException)
        { return BadRequest(new { error = ex.Message }); }
        finally { AttachGate.Release(); }
    }

    [HttpGet("Report")]
    public IActionResult Report(string? id)
    {
        if (!EquipmentEvidenceService.IsHash(id)) return BadRequest(new { error = "Invalid report hash." });
        string path = Path.Combine(Root, id!, "report.json");
        if (!System.IO.File.Exists(path)) return NotFound();
        byte[] bytes = System.IO.File.ReadAllBytes(path);
        if (EquipmentEvidenceService.Hash(bytes) != id) return StatusCode(409, new { error = "Stored report hash mismatch." });
        return File(bytes, "application/json", $"equipment-evidence-{id}.json");
    }
}

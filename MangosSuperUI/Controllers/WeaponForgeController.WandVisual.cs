using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

public partial class WeaponForgeController
{
    /// <summary>Preview or repair missing Shoot visual metadata on a registered custom arcane wand.
    /// apply=false is read-only; apply=true queues the same unified patch as the other Forge actions.</summary>
    [HttpPost]
    public async Task<IActionResult> RepairWandVisual(long displayId, long itemEntry, bool apply = false)
    {
        try { return Json(await _builder.RepairWandVisualAsync(displayId, itemEntry, apply)); }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "WeaponForge: wand visual repair for display {DisplayId} failed", displayId);
            return Json(new { ok = false, error = ex.Message });
        }
    }
}

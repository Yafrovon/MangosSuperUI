using MangosSuperUI.Services.ArmorForge;
using MangosSuperUI.Services.WeaponForge;
using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

public partial class WeaponForgeController
{
    [HttpGet]
    public async Task<IActionResult> ArtRevisionTarget(long displayId,long itemEntry)
    {
        try { return Json(await _builder.ReadArtRevisionTargetAsync(displayId,itemEntry)); }
        catch(InvalidOperationException ex) { return BadRequest(new { ok=false,error=ex.Message }); }
    }

    [HttpGet]
    public IActionResult ArtRevisionMember(string revisionToken,string member)
    {
        try { return File(_builder.ReadArtRevisionMember(revisionToken,member),"application/octet-stream",member); }
        catch(InvalidOperationException ex) { return BadRequest(new {ok=false,error=ex.Message}); }
        catch(FileNotFoundException) { return NotFound(); }
        catch(DirectoryNotFoundException) { return NotFound(); }
    }

    [HttpPost]
    [RequestSizeLimit(21 * 1024 * 1024)]
    [RequestFormLimits(MultipartBodyLengthLimit=21 * 1024 * 1024,ValueLengthLimit=256,ValueCountLimit=16)]
    public async Task<IActionResult> ReviseArt(IFormFile? file,IFormFile? texture,IFormFile? icon,
        long displayId,long itemEntry,string expectedModelSha256,string expectedTextureSha256,bool apply=false,
        string? expectedRevisionToken=null,string? expectedRevisionModelSha256=null,
        string? expectedRevisionTextureSha256=null,string? expectedRevisionIconSha256=null)
    {
        try
        {
            WeaponGeometryRevision.RequireHash(expectedModelSha256,nameof(expectedModelSha256));
            WeaponGeometryRevision.RequireHash(expectedTextureSha256,nameof(expectedTextureSha256));
            var (glb,ge)=await ReadBounded(file,WeaponGeometryRevision.MaxSourceBytes);
            var (png,pe)=await ReadBounded(texture,WeaponArtRevision.MaxPngBytes);
            var (ico,ie)=await ReadBounded(icon,WeaponArtRevision.MaxPngBytes);
            if(ge is not null || pe is not null || ie is not null) return BadRequest(new {ok=false,error=ge??pe??ie});
            var report=new AuthoredArmorReport();
            var mesh=AuthoredArmorAssetValidator.ReadMesh(glb!,"weapon.glb",report);
            if(!report.Valid) return BadRequest(new {ok=false,error="Strict rigid source validation failed.",issues=report.Issues});
            return Json(await _builder.ReviseArtAsync(displayId,itemEntry,expectedModelSha256,expectedTextureSha256,
                mesh,glb!,png!,ico!,_blp,apply,expectedRevisionToken,expectedRevisionModelSha256,expectedRevisionTextureSha256,expectedRevisionIconSha256));
        }
        catch(Exception ex) when(ex is InvalidOperationException or InvalidDataException or ArgumentException or OverflowException)
        { return BadRequest(new {ok=false,error=ex.Message}); }
    }
}

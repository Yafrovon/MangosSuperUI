using MangosSuperUI.Services.WeaponForge;
using Microsoft.AspNetCore.Mvc;

namespace MangosSuperUI.Controllers;

public partial class WeaponForgeController
{
    [HttpPost]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> RecoverName([FromBody] WeaponNameRecoveryRequest request)
    {
        try{return Json(await _builder.RecoverNameAsync(request.ItemEntry,request.DisplayId,request.ExpectedRevisionToken));}
        catch(Exception ex)when(ex is InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {return BadRequest(new{ok=false,blocked=true,error=ex.Message});}
    }

    [HttpPost]
    [RequestSizeLimit(4096)]
    public async Task<IActionResult> ReviseName([FromBody] WeaponNameRevisionRequest request)
    {
        try{return Json(await _builder.ReviseNameAsync(request.ItemEntry,request.DisplayId,request.ExpectedOldName,
            request.NewName,request.Apply,request.ExpectedWorldRowSha256,request.ExpectedRevisionToken));}
        catch(Exception ex)when(ex is InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {return BadRequest(new{ok=false,blocked=true,error=ex.Message});}
    }
}
public sealed record WeaponNameRecoveryRequest(long ItemEntry,long DisplayId,string ExpectedRevisionToken);
public sealed class WeaponNameRevisionRequest
{
    public long ItemEntry {get;set;}
    public long DisplayId {get;set;}
    public string ExpectedOldName {get;set;}="";
    public string NewName {get;set;}="";
    public bool Apply {get;set;}
    public string? ExpectedWorldRowSha256 {get;set;}
    public string? ExpectedRevisionToken {get;set;}
}

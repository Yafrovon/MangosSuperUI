namespace MangosSuperUI.Services.ArmorForge;

public enum AuthoredArmorResumeDisposition { Missing, AlreadyApplied, Conflict }

/// <summary>Partial world applies can resume, but cannot turn into an overwrite of another live row.</summary>
public static class AuthoredArmorResumePolicy
{
    public static AuthoredArmorResumeDisposition Decide(long displayId, int setId, IReadOnlyList<(long DisplayId, int SetId)> rows) => rows.Count switch
    {
        0 => AuthoredArmorResumeDisposition.Missing,
        1 when rows[0].DisplayId == displayId && rows[0].SetId == setId => AuthoredArmorResumeDisposition.AlreadyApplied,
        _ => AuthoredArmorResumeDisposition.Conflict,
    };
}

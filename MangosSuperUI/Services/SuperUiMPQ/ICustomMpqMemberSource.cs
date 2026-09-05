namespace MangosSuperUI.Services;

/// <summary>A forge registry that can hand back one of its own packaged members (an M2 or BLP it
/// minted, addressed by the MPQ path it will ship under) when no mounted archive carries it yet —
/// which, with patch rebuilds on request, is every freshly forged item until the next rebuild.</summary>
public interface ICustomMpqMemberSource
{
    /// <summary>The member bytes, or null when this registry did not mint that path.</summary>
    byte[]? TryGetMember(string mpqPath);
}

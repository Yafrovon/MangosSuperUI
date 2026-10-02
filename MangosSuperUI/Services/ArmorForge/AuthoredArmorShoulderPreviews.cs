using System.Text.Json;

namespace MangosSuperUI.Services.ArmorForge;

/// <summary>Staging proof binds previews to the same compiled members that registration will store.</summary>
public static class AuthoredArmorShoulderPreviews
{
    public sealed record Preview(string File, string Sha256);
    public sealed class Proof
    {
        public Dictionary<string, byte[]> Members { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Preview> Previews { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }
    public sealed record Selection(string File, ShoulderFitSelection Source);
    private const string ProofFile = "shoulder-preview-members.json";

    public static void Write(AuthoredArmorCompiledPiece piece, string directory)
    {
        var proof = new Proof();
        foreach (var member in piece.Source.ModelMembers)
        {
            proof.Members.Add(member.MpqPath, member.Data);
            if (!member.MpqPath.EndsWith(".m2", StringComparison.OrdinalIgnoreCase)) continue;
            string file = "shoulder_" + AuthoredArmorCompiler.PreviewVariant(piece, member.MpqPath) + ".glb";
            proof.Previews.Add(member.MpqPath, new(file, ShoulderFitResolver.Hash(File.ReadAllBytes(Path.Combine(directory, file)))));
        }
        proof.Members.Add(piece.Source.TextureMpqPath!, piece.Source.TextureBlp!);
        File.WriteAllBytes(Path.Combine(directory, ProofFile), JsonSerializer.SerializeToUtf8Bytes(proof, ShoulderFitResolver.JsonOptions));
    }

    public static Selection Resolve(string directory, int displayId, string side, string body)
    {
        string proofPath = Path.Combine(directory, ProofFile);
        if (!File.Exists(proofPath)) throw new InvalidDataException("Declared shoulder-fit staging proof is missing; restage this package.");
        Proof proof;
        try { proof = JsonSerializer.Deserialize<Proof>(File.ReadAllBytes(proofPath), ShoulderFitResolver.JsonOptions)
            ?? throw new JsonException("Empty proof."); }
        catch (JsonException ex) { throw new InvalidDataException("Invalid shoulder-fit staging proof.", ex); }
        string path = side == "L" ? ArmorNaming.ShoulderLeftMpqPath(displayId) : ArmorNaming.ShoulderRightMpqPath(displayId);
        byte[] bytes = Read(path) ?? throw new InvalidDataException("Staged shoulder default is missing.");
        var model = M2Reader.Parse(bytes) ?? throw new InvalidDataException("Staged shoulder default cannot be parsed.");
        var selected = ShoulderFitResolver.Resolve(path, bytes, model.Name, body,
            ArmorNaming.TextureMpqPath(displayId, ArmorNaming.ShoulderDir), side, Read);
        if (!proof.Previews.TryGetValue(selected.Path, out var preview)
            || preview.File != "shoulder_" + side + (selected.DeclaredFit ? "_" + body : "") + ".glb")
            throw new InvalidDataException("Selected shoulder preview is missing or incorrectly bound.");
        string glbPath = Path.Combine(directory, preview.File);
        if (!File.Exists(glbPath) || ShoulderFitResolver.Hash(File.ReadAllBytes(glbPath)) != preview.Sha256)
            throw new InvalidDataException("Selected shoulder preview is missing or has changed; restage this package.");
        return new(preview.File, selected);
        byte[]? Read(string member) => proof.Members.GetValueOrDefault(member);
    }
}

using System.Text.Json.Nodes;
using MangosSuperUI.Services;
using Xunit;

namespace MangosSuperUI.Tests;

/// <summary>SpellComposition.Parse: the MSUIClient session's kit-level block (shared_docs/
/// SPELL_CREATOR_PLAN.md P2.5, P3.9, P4.4). What the client writes must arrive as the cloner
/// reads it - a dropped field is a silently dropped design choice.</summary>
public sealed class SpellCompositionTests
{
    [Fact]
    public void ReadsEveryFieldTheWorkshopExports()
    {
        JsonNode node = JsonNode.Parse("""
        {
          "impact": { "animationId": 54, "cameraShake": 3, "sound": 6215, "worldEffect": "Spells\\ThunderClap_Cast_Base.m2",
                      "slots": [ { "slot": 2, "modelPath": "Spells\\Custom\\x\\sketch_impact.m2", "scale": 1.5 } ] },
          "precast": { "animationId": null, "cameraShake": 0, "slots": [] },
          "missile": { "modelPath": "Spells\\Custom\\x\\sketch_cast.m2", "scale": 1, "sound": 7210 },
          "area": { "modelPath": "", "scale": 1, "shardSelector": 2, "shardRate": 8, "sound": 4012 }
        }
        """)!;
        SpellComposition? c = SpellComposition.Parse(node);
        Assert.NotNull(c);
        StageComposition impact = c!.Stages["impact"];
        Assert.Equal(54, impact.AnimationId);
        Assert.Equal(3, impact.CameraShake);
        Assert.Equal(6215, impact.Sound);
        Assert.Equal(@"Spells\ThunderClap_Cast_Base.m2", impact.WorldEffect);
        Assert.Single(impact.Slots);
        Assert.Equal(1.5f, impact.Slots[0].Scale);

        StageComposition precast = c.Stages["precast"];
        Assert.Null(precast.AnimationId);
        Assert.Equal(0, precast.CameraShake);      // 0 = "no shake": written, not skipped
        Assert.Null(precast.Sound);                // absent = keep the kit's
        Assert.Null(precast.WorldEffect);

        Assert.Equal(7210, c.Missile!.Sound);
        Assert.Equal(2, c.Area!.ShardSelector);
        Assert.Equal(4012, c.Area.Sound);
    }

    [Fact]
    public void RoundTripFieldsParse()
    {
        // MSUIClient SPELL_CREATOR_PLAN.md P5.1: what the visual round trip needed to say losslessly.
        JsonNode node = JsonNode.Parse("""
        {
          "state": { "slots": [], "kitType": 2, "charProcs": [
              { "type": -1, "params": [0, 0, 0, 0] },
              { "type": 1, "params": [16711680, 0.5, 0, 0] } ] },
          "missile": { "modelPath": "", "scale": 1, "attachment": 1 },
          "visualFields": { "8": 2, "14": 4294967295, "15": 1, "3": 99 }
        }
        """)!;
        SpellComposition c = SpellComposition.Parse(node)!;
        StageComposition state = c.Stages["state"];
        Assert.Equal(2, state.KitType);
        Assert.Equal(2, state.CharProcs!.Count);
        Assert.Equal(-1, state.CharProcs[0].Type);              // an empty lane keeps its position
        Assert.Equal(1, state.CharProcs[1].Type);
        Assert.Equal(16711680f, state.CharProcs[1].Parameters[0]);
        Assert.Equal(1, c.Missile!.Attachment);
        Assert.Equal(2u, c.VisualFields[8]);
        Assert.Equal(0xFFFFFFFFu, c.VisualFields[14]);         // read as an integer, not through a float
        Assert.Equal(1u, c.VisualFields[15]);
        Assert.False(c.VisualFields.ContainsKey(3));            // only the raw fields nothing else models
    }

    [Fact]
    public void AnEmptyBlockIsNoComposition() =>
        Assert.Null(SpellComposition.Parse(JsonNode.Parse("{}")));
}

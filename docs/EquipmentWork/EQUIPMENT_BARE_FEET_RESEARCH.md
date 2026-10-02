# Bare-foot race compositing investigation — 2026-09-28

The native quick Tauren armor captures paint the complete hoof with the boot's
FootTexture region. The same behavior occurs with an original Valor baseline in
MSUIClient, so agreement with that baseline cannot validate the rendering rule.
This is a renderer investigation, independent of the custom armor mesh fitting.

## Primary implementation evidence

The [archived December 2009 WoW Model Viewer compositor](https://github.com/Chuanhsing/wowmodelviewer/blob/79a97c404db38d7b1eef8b1f769eb84c5a202f32/src/charcontrol.cpp#L306)
sets `showFeet` for race IDs 6 and 8 (Tauren/Troll), as well as Draenei, Naga, and
Broken. Its boot branch at lines 1344–1349 always adds the lower-leg component and
adds the foot component only when `showFeet` is false. The source contains separate
non-WotLK paths, but this snapshot is dated 2009, not an authenticated 1.12 renderer.
It directly establishes that implementation's behavior without proving every older
patch behaved identically.

The [archived vanilla WoW Model Viewer 0.48d source](https://github.com/danielsreichenbach/wowmodelview-vanilla/blob/1e9b23713b967b2feb4465d0bf68fb4299eb709a/charcontrol.cpp#L752)
does **not** corroborate that rule: its equipment compositor adds all eight regions
unconditionally, including FootTexture at line 760. The repository is a 2025 import
of historical source, not a contemporaneous commit history. Its omission must not
be concealed or treated as authoritative proof of original client behavior. Model
viewers approximate client rendering and can contain the same omission as MSUIClient.

[AzerothCore's ChrRaces DBC documentation](https://github.com/azerothcore/wiki/blob/master/docs/chrraces.md#flags)
identifies flag `0x2` as bare feet, for the documented 3.3.5a table.
[WoWDBDefs' original-version ChrRaces schema](https://github.com/wowdev/WoWDBDefs/blob/master/definitions/ChrRaces.dbd#L137)
places Flags immediately after ID for builds 1.0.0–1.12.2 and original TBC. The schema
establishes field position, not bit semantics. Inspect the mounted vanilla table's
ID/Flags values before using that bit as the data-driven original-era rule.

The original author's [WoWmodelview 0.4 page](https://wowmapview.sourceforge.net/wowmodelview/)
links a September 2005 GPL source archive. SourceForge returned HTML instead of the
ZIP during this investigation; no claim is based on unread source from that archive.

Saved study copies are under
`artifacts/equipment-workshop/reference-corpus/historical-code/` with the GPL license.
The 0.48d `charcontrol.cpp` SHA256 is
`0b9e3aa0d0c133531593bbd069a08aad3052b7f8985c886645b20b6e14f4aa15`.
These are research files, never inputs to the equipment package or client patch.

## Narrow implementation scope if the original rule is confirmed

Preserve lower-leg armor and normal boot geoset behavior. Suppress the FootTexture
composite region for a wearer whose race has the bare-foot rule. Do not delete or
repaint the authored boot textures, because other races need them.

`CharacterEquipment.Composite` currently lacks wearer race and has four production
call paths: `PlayerRenderer.BuildDressedAtlas`, `CharacterRenderer.PrepareAppearanceUpdate`,
`CharacterRenderer.ApplyEquipment`, and `CreatureRenderer.PrepareNpcBareComposite`.
Carry the rule through each appropriate path; changing only the offline capture
runner would leave live rendering wrong. Cache identities must include race or
the resulting appearance policy. `PlayerAppearance` carries skin/face/hair values,
not race. A compositor test should preserve base pixels in the foot rectangle for
bare-foot races while still changing lower-leg pixels, and verify that a normal
race paints both. Native before/after Tauren and Troll captures plus a normal-race
control should then corroborate the code change.

## Mounted verification and implemented correction

The production C# `MpqMount` now independently confirms the actual vanilla member:
`patch.MPQ`, 1,234 bytes, nine records, 29 fields, SHA256
`c338676c04e9c322839ab46ca45f5ba76f55e1eabe3e72847e467d675ea99d43`.
Tauren/Troll rows 6/8 both have flags 14; Human/Orc/Dwarf/Undead/Gnome have 12;
Night Elf has 4; Goblin has 1. Only Tauren/Troll of the eight playable races carry
bit 0x2. This is actual archive evidence, independent of the old viewer source.

`Formats/RaceAppearanceTable.cs` parses those flags. All four native compositor
calls now pass the wearer's bare-foot policy explicitly; the compositor skips
only region 7. The streamed and local cache identities already include race.
`tools/race-foot-composite-check` passed 17 assertions against the real mounted
table and actual CPU compositor. The report is saved as
`artifacts/equipment-workshop/fit-analysis/race-foot-compositor-check.json`.
It includes a changed-flag negative control proving that policy follows the DBC,
normal Human foot painting, Tauren/Troll base foot preservation, lower-leg painting,
no foot texture load when suppressed, and source-buffer immutability. Debug and
Release builds pass with 19 pre-existing warnings.

The browser character loader now reads `/CharacterAppearance/Races`, which obtains
the current mounted DBC through `MpqReaderService` and returns bounded parsed rows
plus the exact member hash. The policy travels with each loaded character. Both
single and layered compositors preserve the bare-foot region, including direct
retexture previews. Per-character source URL takes precedence over global canvas
identity. Two C# parser tests and JavaScript syntax checks pass; the actual browser
compositor functions also pass four mocked-canvas mode/policy combinations plus
four race/model identity cases. Run the latter with
`node --experimental-vm-modules tools/equipment-workshop/check_web_foot_compositor.mjs`.

These checks establish the implementation and mounted data. Actual world inspection
remains a separate required check.

## 2026-09-28 — native offline regression completed

Ran the existing Release binary, without rebuilding or changing installed patches,
using isolated `qa-settings.json`. Captured stock Valor and custom Greywatch on
Human, Tauren, and Troll, both sexes, front/back, standing animation 0 at 0.6 seconds,
768×768. All 24 requested frames completed with zero technical errors. Every one of
the 16 Tauren/Troll frames omits region 7 from resolved body textures; all 24 frames
retain lower-leg region 6; the eight Human frames retain region 7.

Reviewed both complete 12-frame contact sheets plus full-size Tauren/Troll frames
and the matched Tauren before/after difference. Hooves and toes show their original
skin while shin armor remains. Matched Greywatch Human male/female front/back
controls are pixel-identical to the four earlier captures. Greywatch Tauren male
has 8,292 changed front pixels, confined to x285–479/y560–636, and 6,758 changed
back pixels, confined to x299–484/y506–632. The latter also includes the fur above
the hooves, which is part of the foot texture region. Camera, pose, lighting, body
transform, model, and visible geosets match in all six comparisons. Every shared
captured asset member has the same SHA256 (22 per Human capture, 21 per Tauren
capture). These pixel comparisons cover Tauren male and Human controls only;
Tauren female and both Troll sexes have new-result review and region-resolution
evidence, without a matched pre-fix image in this batch.

Reproducible analyzer: `tools/equipment-workshop/analyze_foot_regression.py`.
Results, frame hashes, per-row differences, both set contact sheets, and full-frame
before/after sheet: `artifacts/equipment-workshop/foot-regression/`.
Original capture JSON/assets/PNGs: `scratch/equipment-batch/foot-regression/captures/`.
This is offline production-renderer evidence; no live-world verification, body
collision result, or general armor-art approval is inferred from it.

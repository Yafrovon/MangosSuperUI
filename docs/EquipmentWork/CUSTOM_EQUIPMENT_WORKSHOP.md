# Custom equipment workshop — 2026-09-28

The current agent-operated procedure is [EQUIPMENT_AGENT_WORKFLOW.md](EQUIPMENT_AGENT_WORKFLOW.md).
Its links distinguish source, native, installed, live and personal-review gates;
the historical studies below retain their original proven/pending scope.

## Requested outcome

Ten original weapons and four complete eight-piece sets (plate, mail, leather, cloth),
with original helm and shoulder geometry, built through MangosSuperUI and verified in
MSUIClient. A source mesh, successful packaging, or a screenshot alone is not completion.
All items remain candidates until the checks below have actual evidence.

## Art direction

Original 1.12 and early TBC construction is the reference. Use simple silhouettes,
deliberate thickness, broad painted light/dark regions and recognizable materials.
Measure budgets by family against original MPQ assets; do not mistake the importer
capacity ceiling for an appropriate art budget. Body armor uses character texture
regions and geosets. Helms and shoulders are attached models, with explicitly fitted
variants for the original eight races and two sexes.

Rejected first-pass collection: Greywatch plate (blue-gray iron, ochre fittings, chevrons), Redfen
mail (bronze scales, desaturated teal, river motifs), Briarpath leather (umber hide,
moss panels, leaf shapes), Archive Warden cloth (indigo, parchment gold, angular stars).
The replacement construction briefs are in `EQUIPMENT_ART_REDESIGN.md`; reusing these
names does not retain the rejected common geometry or panel layout. Weapon silhouettes cover arming
sword, knife, bearded axe, flanged mace, greatsword, greataxe, maul, crook, glaive and wand.

## Tools and evidence

- `EquipmentReference` reads original MPQs on demand. Provenance includes model and
  texture hashes. Its sample distributions are descriptive, not approval thresholds.
- `EquipmentMeshAudit` measures final mesh arrays, welded edge topology, surface and
  UV degeneracy, normals, UV density, texture luma. It explicitly does not yet solve
  self-intersection or UV overlap. Intentional open/layered surfaces need review.
- `AuthoredArmor` stages a complete ZIP with original painted components, 16 explicit
  helm GLBs, L/R shoulder GLBs, skins and icons. Staging compiles and reopens native
  M2/BLP. The ordinary forge registry and unified patch remain the shipping path.
- `tools/equipment-workshop/build_weapons.py` is editable Blender source for the initial
  10 meshes; GLBs and a catalog are in `wwwroot/equipment-workshop/weapons`.
- `render_weapons.py` creates controlled **source-model** captures. These are not
  native M2 or live-world evidence.
- MSUIClient equipment capture work extends the existing production CharacterRenderer
  batch runner. Native offline captures and live-world acceptance must stay distinct.

## Acceptance record required per item

1. Source brief, original reference cohort, editable source, exact export hash.
2. All dependencies resolve; triangles/UVs/normals/bounds/texture paths are valid.
3. Inspect front/back/sides/top/three-quarter in texture, unlit, neutral surface,
   wireframe, silhouette and normal views. Check declared straight edges directly.
4. Reopen final M2/BLP and compare to source. Inspect native texture resolution/mips.
5. Production client captures of all supported race/sex fits, held/sheathed weapons,
   idle/walk/run/attack/cast appropriate to the equipment. Record missing samples.
6. Real in-world equip/animation/lighting check using the identical installed patch.
7. Human-readable review of flaws and corrections. Artifact existence is never a pass.

## Current state

**Owner art review rejected all four first armor designs.** They share too much
helmet/shoulder/body-panel shape and repeat their patterns too uniformly. These 32
armor pieces are technical candidates, not finished sets. Complete original-ensemble
study and a fresh design pass now precede further armor acceptance testing. See
[the rejection analysis and replacement workflow](EQUIPMENT_ART_REDESIGN.md).

The first-pass ten weapons and four eight-piece armor packages were authored,
compiled through Forge, and packaged in the client patch. Weapons use entries
1102488–1102497 / displays76331–76340. Armor uses entries1102498–1102529,
displays76341–76372 and sets5192–5195. Editable Blender sources, painted textures,
GLBs and complete armor ZIPs are retained. Geometry revision now preserves an existing
weapon's IDs and registered texture while repeating compilation and fit review.

**Final acceptance is still pending.** The bounded original study covers593 display
references. Offline native review covers all16 vanilla bodies, including1,568 armor
frames, the original2,880 weapon frames, corrected wand poses, and additional staff
iterations. The armor attachment contains134 rendered custom members matching the
registry;32 inventory icons are outside that character-render evidence. The live
ledger contains historical technical coverage for 24/42 pieces across eight weapons
and two first-pass sets, with remote observer coverage still pending. The owner art
rejection overrides that armor coverage: **zero finished or accepted armor sets**.
Failed runs remain excluded.

Two concrete defects remain in progress: the staff's Troll ear/Gnome running head
contacts prompted a third source revision, now compiled under the same item/display
IDs but still awaiting a fresh installed-patch native review; VMaNGOS reused item GUIDs still referenced
by orphan inventory rows, rolling back QA equipment saves. A bounded allocator fix
is built with four passing tests and preserves all database rows. Automatic approval
review blocked its server stop, so installation is awaiting explicit owner approval.
Read-only mage relog confirmed all nine custom pieces were lost; this is not treated
as a successful persistent loadout. See `artifacts/equipment-workshop/live-evidence/`
and `scratch/equipment-batch/live-equipment/persistence-diagnostics/FINDING.md` for
the exact retained evidence. No completion count is inferred from generated files.

### 2026-09-28 — weapon construction and compiled checks

The ten meshes have 120–244 triangles, opaque 256² diffuse textures and explicit
grip origins. Import used identity placement with no decimation. Their geometric
audits found zero degenerate/duplicate surface triangles, invalid normals, opposing
winding, non-manifold/boundary edges, unused positions, or UV coordinates outside
the unit square. Multiple closed material parts are intentional. Blender BVH checks
found one connected contact assembly per weapon and zero within-part contacts
between non-adjacent triangles. These checks do not prove hand/body clearance.

Fixed-camera source renders caught and corrected a disconnected axe head, insufficient
mace depth, and unsupported staff/wand ornament. The final generated sources and
connection reports are bound to the exact GLB SHA-256.

Reopened compiled M2/BLP previews match each source's oriented triangle corners at
absolute tolerance 1e-6. Positions and UVs are unchanged; normals undergo tiny float
renormalization. The 256² DXT1 texture's mean absolute RGB codec error is 2.80/255,
with 99th-percentile channel error 13/255. This measures compression, not art quality.
Evidence: local `artifacts/equipment-workshop/weapon-imports`, `weapon-builds`,
`compiled-comparison`, and `source-review`. Those outputs are excluded from source
control alongside extracted reference studies. Reproduction scripts remain in the
owner-local `tools/equipment-workshop` directory.

Focused web validation: 32 tests pass (17 authored armor, 10 reference measurement,
5 mesh audit). Native client capture tooling builds Debug and Release; the stock
24-image weapon baseline is byte-identical between configurations. Broader original
baseline includes all 16 race/sex variants. Custom live-world checks remain pending.

## Texture source

`tools/equipment-workshop/textures/forged-materials-source.png` was generated with the
built-in imagegen tool, then downsampled by Blender to the delivered 256² diffuse atlas.
Prompt: flat orthographic four-quadrant material atlas; blue-gray forged steel with a
chevron; aged ochre bronze; reddish wrapped leather; desaturated oak. Broad hand-painted
2004 RPG diffuse values, no PBR/normal map, no labels, no weapon silhouettes, no background.
Its use is a painted source study, subject to normal visual acceptance in the exported model.

## Research

- Original character compositing: https://github.com/samwhosung/wow-1121-client-internals/blob/main/docs/character-model.md
- Original MD20 structure: https://github.com/samwhosung/wow-1121-client-internals/blob/main/docs/models.md
- Versioned display schema: https://github.com/wowdev/WoWDBDefs/blob/master/definitions/ItemDisplayInfo.dbd

## Measured original-equipment study — 2026-09-28

The read-only `EquipmentReference` endpoints were used to inspect **593 display
references**, yielding **240 distinct weapon geometries across ten families**, **121
distinct helm/shoulder geometries**, **80 painted armor references**, and **764 distinct
decoded texture inputs**. Raw responses, archive/DBC/model/texture hashes and sampling
metadata are retained in
[`reference-corpus/summary.json`](../artifacts/equipment-workshop/reference-corpus/summary.json).
The collection script is `tools/equipment-workshop/collect_reference_corpus.py`.
No original asset exports were installed as authored equipment.

This is a bounded, deterministic study sample, not an exhaustive inventory or a
probability sample. Model paths were ordered by SHA-256, then inspected until each
weapon family supplied 12 new geometries per source. Geometry identity is the exact
ordered position/index hash, globally deduplicated within weapon or armor. Mirroring,
vertex reordering and differing unused vertices can still distinguish visually similar
models. TBC selections are distinct from the *sampled* vanilla geometry, not proven
TBC-exclusive designs. Each source contributes 30 helm geometries (HuM variant only);
shoulders contribute 31 vanilla and 30 TBC geometries, counting left/right separately.
The body sample is 40 vanilla display templates and 40 TBC chests, ten each of plate,
mail, leather and cloth. Vanilla template material class is not established.

The mounted sources are 1.12 and 2.4.3, not a verified launch-only 2004 asset collection.
They include later content such as Ahn'Qiraj, Naxxramas and late TBC raids. The vanilla
mount uses the existing stock archive allowlist; the configured TBC mount's label is
not proof that every byte is unmodified. Exact asset hashes remain the provenance.

### Geometry observed, not acceptance thresholds

Each weapon cell is `median [25th–75th percentile]` triangles from 12 unique models.
The two source columns intentionally represent separate sampled geometries.

| Family | Vanilla sample | TBC sample | Current authored candidate |
| --- | ---: | ---: | ---: |
| 1H sword | 85 [64–162.5] | 134.5 [83.5–354.5] | Greywatch 176 |
| Dagger | 197 [142.5–235.5] | 98 [75.5–434.5] | Wayfarer 168 |
| 1H axe | 145.5 [72–260.5] | 268 [160–463.5] | Redfen 148 |
| 1H mace | 122 [97–192] | 322 [216–431] | Stonewake 244 |
| 2H sword | 107.5 [66.5–233] | 338 [219–571.5] | Watchfire 184 |
| 2H axe | 183 [129–305] | 421.5 [367.5–454] | Timberfall 176 |
| 2H mace | 173 [131–290.5] | 290 [181.25–388.5] | Oathstone 120 |
| Staff | 132 [83–281] | 593 [459–694.75] | Briarpath 196 |
| Polearm | 106 [86–260.5] | 435 [297.5–600] | Marshwarden 120 |
| Wand | 238 [103–331] | 253 [182.75–410.5] | Archive Warden 176 |

The vanilla weapon sample spans 10–708 triangles, TBC 48–1,336. Those extrema include
unusual models and cannot define a universal budget. Current candidates at 120–244
triangles are compatible with a restrained vanilla construction brief. Stonewake is
above its vanilla sample's upper quartile (192), and Greywatch slightly above its
family's (162.5), but both remain within observed ranges. Review whether their extra
facets improve the silhouette; do not decimate solely to satisfy these small samples.

Vanilla HuM helms have median 71 triangles [51–110], range 22–402; individual shoulder
models median 96 [84–123], range 42–397. The TBC samples have helm median 147
[80.75–332.75], range 24–612, and shoulder median 116 [77–249.5], range 42–430.
Painted body references do not contribute zero-triangle observations: their appearance
is composed on character geometry, rather than an independent chest mesh.

### Primary diffuse textures and values

[`primary-skin-summary.json`](../artifacts/equipment-workshop/reference-corpus/primary-skin-summary.json)
separates sampled M2 type-2 replacement skins and exact item-selected diffuse paths
from other embedded reflection/particle/effect maps. Painted references include their
resolved body components. Unclassified embedded diffuse maps can be omitted by this
conservative rule. Texture counts include all inspected display variants, not just one
texture per deduplicated geometry. Textures are deduplicated by original BLP SHA-256
within each source/category.

| Primary texture sample | Distinct inputs | Common dimensions | Median mean linear luminance [25th–75th] |
| --- | ---: | --- | ---: |
| Vanilla weapon | 122 | 69 at 128×64; 25 at 256×128; only 1 at 256×256 | .192 [.125–.258] |
| TBC weapon | 240 | 97 at 128×64; 84 at 256×128; 11 at 256×256; 9 at 512×256 | .169 [.104–.245] |
| Vanilla helm/shoulder | 53 | 32 at 64×64; 10 at 128×128; 9 at 128×64 | .166 [.108–.235] |
| TBC helm/shoulder | 71 | 30 at 128×128; 26 at 64×64; 11 at 128×64 | .137 [.075–.184] |
| Vanilla body components | 106 | 74 at 128×64; 32 at 128×32 | .073 [.042–.150] |
| TBC body components | 191 | 120 at 128×64; 71 at 128×32 | .075 [.048–.136] |

Means are alpha-weighted linear-sRGB luminance over the decoded image, not screen
brightness or perceived material gloss. Transparent pixels do not count as black.
These values are useful for matched diffuse-texture comparisons; do not compare them
directly with `EquipmentMeshAudit.meanLuma`, which uses encoded RGB values. They also
do not prescribe one brightness for every material or atlas region.

The current four-region 256² weapon atlas is generous relative to many vanilla skins,
though a quarter occupies only 128². Check a 128² whole-atlas/mip variant and actual UV
texel density at equipped distance before deciding whether that extra detail is useful.
Body components must retain the atlas region dimensions expected by the native compositor;
the component sizes above do not imply that the completed body atlas is 128×64.

### Controlled visual observations

Thirty original exemplars, three per family, were exported through the reference
preview route and rendered in four controlled views: flat diffuse broad side, edge,
three-quarter, and neutral clay broad side. Source hashes and camera matrices are in
[`visual-study/captures.json`](../artifacts/equipment-workshop/reference-corpus/visual-study/captures.json).
The 120 images and ten family sheets are reproducible with
`prepare_reference_exemplars.py`, `render_reference_weapons.py` and
`summarize_reference_corpus.py`. Cameras fit visible indexed equipment vertices, with
the longest bounding axis vertical; this is a documented study orientation, not the
in-game attachment transform. Blender's hidden imported armature helpers are excluded.
Duplicated M2 overlay-pass meshes are explicitly omitted because Workbench does not
implement the native blending pipeline. Animated emitters and reflection behavior are
not established by these renders.

All ten family sheets were visually inspected. Concrete observations:

- [1H swords](../artifacts/equipment-workshop/reference-corpus/visual-study/sword1h-study-sheet.png):
  Well-used Sword (display 1550, 64 triangles) uses a few blade planes and a narrow
  edge section; the pronounced bevel highlight and wrapped grip survive in the diffuse
  view but largely disappear in clay. Quel'Serrar (30994, 146) spends its silhouette
  on the bent point and hooked guard, while the blue blade inlay and gold scrollwork
  are painted detail.
- [Daggers](../artifacts/equipment-workshop/reference-corpus/visual-study/dagger-study-sheet.png):
  the AQ knife (34141, 150) obtains a thick organic outline from a few coarse facets;
  nodules, creases and surface separation are mostly painted. The Coilfang knife
  (39048, 396) has a more developed blade section and an actual open cutout. Midnight
  Haze (35244) remains effect-dependent; its flat diffuse study must not be treated as
  the complete in-game appearance.
- [1H axes](../artifacts/equipment-workshop/reference-corpus/visual-study/axe1h-study-sheet.png):
  Hatchet A01 (1386, 72) has a simple wedge and cylindrical haft; bright edge borders,
  rivets and grip wrapping are texture detail. Frostbite (31611, 228) uses actual
  concave cutouts and spikes for identity. The Draenei axe (41944, 420) develops
  separated rings and a thicker central gem, so its extra geometry changes the edge view.
- [1H maces](../artifacts/equipment-workshop/reference-corpus/visual-study/mace1h-study-sheet.png):
  the Thaurissan hammer (21751, 100) is predominantly a block and shaft, with its
  framed emblem and wrapped grip painted. Ironspine's Fist (15726, 164) puts geometry
  into the spikes and changes of head section. Light's Justice (40923, 372) devotes
  geometry to separated enclosing fins; the preserved effect surfaces are not a
  native additive-lighting reproduction in this renderer.
- [2H swords](../artifacts/equipment-workshop/reference-corpus/visual-study/sword2h-study-sheet.png):
  Korean C01 (729, 68) is a lightly curved narrow section. Ashbringer (23875, 226)
  is recognized by its large asymmetrical outline and disc; its bevel, guard trim and
  disc emblem use broad painted value bands. Raid D01 (45113, 568) adds shaped inset
  rails while keeping the main design legible from the broad side.
- [2H axes](../artifacts/equipment-workshop/reference-corpus/visual-study/axe2h-study-sheet.png):
  War B03 (3889, 130) has a real opening between blade and haft; its small fittings
  are largely painted. Huge Thorium Battleaxe (23434, 276) and the Alliance axe
  (40955, 444) have broad paired wings and negative spaces. The latter's lion and
  filigree vanish in clay: they are not modeled relief.
- [2H maces](../artifacts/equipment-workshop/reference-corpus/visual-study/mace2h-study-sheet.png):
  Spiked B02 (3935, 132) makes its ring opening and spikes from visibly few sides.
  Sulfuron Hammer (29699, 276) has real head/side spikes but much simpler clay
  paneling than its textured face suggests. Herald of Woe (31878, 388) uses a
  distinctive head projection and hooked butt, with broad red/gold painted regions.
- [Staves](../artifacts/equipment-workshop/reference-corpus/visual-study/staff-study-sheet.png):
  Long C01 (2509, 84) concentrates nearly all outline detail at the finial and butt.
  Zul'Gurub D02 (32517, 268) uses broad wing shapes while skull/metal detail is painted.
  Pillar of Ferocity (45224, 671) adds multiple deep projections and gems that remain
  visible in the edge view; its budget serves a more complex silhouette.
- [Polearms](../artifacts/equipment-workshop/reference-corpus/visual-study/polearm-study-sheet.png):
  Bladed B02 (5637, 88) is a narrow lance head and sparse shaft. The Naxxramas
  reference (35259, 230) is asymmetric but very thin edge-on. Black Temple D01
  (45317, 564) uses an open cage and projecting side blades, not uniform subdivision.
- [Wands](../artifacts/equipment-workshop/reference-corpus/visual-study/wand-study-sheet.png):
  Jeweled B02 (23455, 112) uses a visibly faceted sphere with a painted highlight.
  AQ D01 (33427, 324) has a strongly asymmetric organic crown. The Eredar wand
  (43915, 382) relies on its paired horns and central face; the blade-like projections
  and the large painted eye/face make it distinct from a shortened sword.

### Implications for the authored collection

Keep the largest identifying masses and negative spaces deliberate. Spend geometry
on a silhouette, a meaningful change of section or a visible opening; use broad
diffuse light/dark bands for the bevel and raised-trim impression. Narrow edge views
are common, but never justify an accidental zero-thickness surface or broken winding.
Use clay and edge views to distinguish actual form from texture shading, then inspect
the same feature under native lighting and the shipped mip chain.

The authored source sheets show the ten silhouettes, but also strong repeated motifs:
the Greywatch sword and Watchfire greatsword share nearly the same blade/guard design;
the Wayfarer knife shares their grip and blade chevron. This can be coherent as one
equipment family, but ten separately named weapons should not depend only on size for
identity. Give the greatsword a distinct guard or blade rhythm, and the knife a more
deliberate skinning-hook/edge transition if greater differentiation is desired.
The Marshwarden is currently closer to a spear head than the asymmetric Naxxramas
glaive example; decide that silhouette intentionally. All ten currently reuse the same
steel/bronze/leather/wood atlas and repeated chevron. Material reuse is economical,
but per-weapon UV placement and a few identity accents would avoid a stamped pattern
across unrelated themes. These are design review observations, not recorded fixes or
acceptance failures inferred from triangle counts.

No original-reference render, structural report, source render, or geometry round trip
establishes attachment fit, animation, all-race behavior, or live-world correctness.

## 2026-09-28 — leather/cloth ensemble construction and redesign prototypes

The owner rejected all four first armor designs. Their old structural and capture
evidence does not count as accepted art. The new [leather/cloth construction study](../artifacts/equipment-workshop/reference-ensembles/leather-cloth/STUDY.md)
examines six named original sets plus ordinary Emblazoned and Buccaneer clothing,
with72 native HuM/HuF panels,131 labeled source texture uses and15 original model
exports in60 texture/clay/edge views. Exact membership, original source hashes,
texture roles and evidence-class limitations are retained in the study manifest.
TBC whole-ensemble observations came from the root agent's Forge browser review;
they are explicitly separate from hash-bound native Classic captures.

Repeated motifs are not categorically wrong: original Felheart repeats them strongly.
The relevant differences are silhouette, material zones, scale of detail, exposed
joints and front/back arrangement. Magister's rear crown, Cenarion's antlers and
leaf cards, Shadowcraft's facewrap and ordinary clothing's small pads are distinct
constructions despite similarly small diffuse textures.

The selected replacement leather/cloth attachment directions now have isolated
[Human male prototypes and a painting handoff](../artifacts/equipment-workshop/armor-redesign/prototypes/LEATHER_CLOTH_HANDOFF.md).
These progressed from material blocks to four original diffuse paintings, with
recorded crop/resample into fixed128×128 UV regions and static Blender wearer studies. They have
not been accepted, fitted across all bodies, imported into the Forge registry or
verified in native motion/live play. Whole-character review precedes that work.

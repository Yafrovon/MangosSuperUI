# Armor design rejection and redesign — 2026-09-28

**Current selected raid directions:** the owner explicitly approved Oath of the Broken Gate (plate), Leviathan’s Ransom (mail), Gloamwing Stalker (leather), and Glass Comet Masquerade (cloth) as concept directions. The four original concept sheets are retained under `artifacts/equipment-workshop/armor-redesign/raid-direction-study/concepts-v1`. This selects the art direction; model construction, measured budgets, wearer fit and in-game appearance still require review. The older v2 directions below remain historical technical studies.

**Latest owner direction: the second-generation Greywatch, Redfen, Briarpath and Archive outfits are also superseded as art candidates.** They read as bland field gear to the owner, who requested raid-level creativity, stronger thematic identity, and broader inspiration from World of Warcraft and Final Fantasy XIV. Their native captures remain useful construction, paint-ownership and tool evidence. They are not accepted or finished sets and must not be presented as current candidate approval.

The owner rejected all four first armor designs after reviewing matched native front/back views. The designs are too similar in shape and too uniformly patterned to satisfy the requested Classic/TBC appearance. **None of these four designs counts as accepted or finished armor.** Their valid geometry, package hashes, native captures and working equipment behavior remain technical evidence only.

## What produced the failure

The problem is in the authoring decisions, not a rendering bug:

- `build_armor.py::helm` builds every material around the same Valor-sized dome, the same face-opening formula, and the same segment/ring layout. Small crest and side-panel substitutions do not create four distinct head silhouettes. A cloth hood and metal helmet were treated as variants of one cap.
- `shoulder` begins all four materials with similarly sized domes and appends narrow decorative falls. The differences are overwhelmed by the common cap outline, position and proportion.
- The body painter reuses the same outlined arm, wrist, thigh and shin panel logic across materials. The same narrow light border makes cloth, hide and metal read as interchangeable decorated panels.
- Every 64-pixel half is repeated, including the same front/back waist treatment. That simplifies seams but erases functional distinctions such as a front closure versus an undecorated back.
- Trim, chevrons, leaves or stars are distributed across nearly every available region. There is little separation between focal detail and quiet support areas. Changing a motif is insufficient when its placement, frequency and outlining remain the same.
- The research measured many individual assets, but those counts did not establish understanding of a complete outfit's hierarchy, silhouette or construction. The art review failed to reject the common template before costly fit and live tests.

The previous native overview is retained at `artifacts/equipment-workshop/armor-native-evidence/armor-front-back-overview.png` as **rejected-design evidence**, not a showcase of finished sets.

## Required study before replacement authoring

Study complete original ensembles on characters, with front/back/side views, and inspect their component textures and attachment geometry. Begin with two Classic ensembles and one TBC ensemble per material, plus ordinary lower-detail examples where available. Preserve authentic set membership: a five-piece TBC tier is not an eight-piece set. Record any companion pieces separately.

For each ensemble, identify:

1. The major head, shoulder, sleeve, glove, waist and leg silhouette decisions.
2. What is real attachment geometry, a character geoset, or painted body detail.
3. Where the material changes, where skin or underlayers remain visible, and how joints are treated.
4. The main visual focus, quieter regions, and how color/material ties pieces together without stamping an identical pattern onto each slot.
5. Front/back differences, closures, overlap direction, folds, wear and painted highlights.
6. Differences within a material: a rogue and druid need not use one universal leather shape; mage, warlock and priest cloth need not share one hood/robe formula.

References are construction lessons, not meshes or pixels to present as original work. Modern remasters, PBR fan art and later expansion armor are not substitutes for the mounted 1.12/2.4.3 assets.

## Replacement review order

First compare the proposed four designs as plain silhouettes and rough material blocks beside the original ensemble study. Give each its own helmet, shoulder construction and garment layout; remove the shared-cap and shared-panel assumptions. Then review one complete prototype per material on the same body in front, back and side views before fitting sixteen bodies or running the full acceptance matrix.

Geometric validity, attachment fit, native lighting, persistence and remote replication remain required later. They do not answer whether the design belongs in this game.

## Selected prototype directions

The original comparison includes eight complete Classic dungeon/raid sets, four ordinary gear families and four authentic five-piece TBC tiers. Native Classic images and source textures are hash-bound in `scratch/equipment-batch/reference-sets/expanded-study`; original TBC whole ensembles were also inspected in the existing Armor Forge browser preview. Browser observations are art research, not native-client fit evidence. Missing pieces in the five-piece tiers and ordinary seven-piece families remain missing rather than silently borrowing fillers.

The original ensembles frequently repeat ornament. Lawbringer, Green Iron and Felheart are especially clear counterexamples to a rule that armor must be sparse. The correction is to vary physical construction, painted form, material zones and scale of detail. Front/back differences are useful where the garment calls for them; they are not an arbitrary requirement to make every surface unrelated.

Four independent prototype directions are now selected:

- **Greywatch plate:** a broad low angular sallet, projecting brow and separate cheek guards; three overlapping shoulder lames; broad steel breastplate and greaves over dark flexible joints. Ochre focuses on selected fasteners and a chest mark. The back uses riveted plate construction rather than repeating the front closure.
- **Redfen mail:** a narrow swept/conical nasal helmet with open cheeks and leather straps; a low scale mantle; mail at joints and flanks, leather protection over the ribs, and a muted teal short tunic skirt. Its shallow shoulder V and taller narrow head contrast with Greywatch's horizontal plate mass.
- **Briarpath leather:** a low soft brimmed cap with a slightly crooked crown; compact quilted hide pads and an overlapping stitched flap; burgundy hide vest over a light underlayer, exposed elbow/finger breaks, quiet trousers and folded boot cuffs. No common plate-cap silhouette under a brown material.
- **Archive cloth:** uncovered crown with a tall folded rear collar; draped shoulder cloth with downward corner ends; pale sleeves, an indigo robe and broad warm-cream side panels. Fabric folds and a localized chest clasp establish construction; the lower robe remains a broad readable field.

These are working art decisions, not user acceptance. First build one Human male prototype per direction with plain material blocks, then evaluate its full assembled silhouette and body painting. Only a successful prototype advances to sixteen-body fitting and runtime tests.

Historical v2 prototype state: all four independent Human male studies have original painted attachments and body atlases, compiled by the strict Forge production path and reviewed from six native angles. Internal reviewers advanced them to sixteen-body fitting, but the owner's later art feedback supersedes that direction: these are technical studies, not current art candidates. No replacement armor set is finished. Weapon review continues separately; staff candidates with unnatural bends are also rejected even when collision checks pass.

### 2026-09-28 — complete prototypes and separate item layers

The fixed six-angle body/dressed comparisons live under `artifacts/equipment-workshop/armor-redesign/prototypes/{greywatch,redfen,briarpath,archive}/native-dressed-v*`. `four-native-prototype-front-back.jpg` is a labelled summary with crop/source hashes; it does not replace the side and three-quarter views. Native review caught and corrected a white cloth yoke, leather forearm/hand/toe gaps, double-sided export settings and nonmanifold crown/rim junctions. Shader/material validation was not relaxed to admit bad meshes.

Original full-outfit painting is now separated into six real painted pieces, with original underlayer art beneath removable cuffs, belts and boots. `split_body_equipment.py` records exact region ownership, source hashes and selected row masks; it does not reuse the rejected procedural panel painter. Cloth shoes paint only the feet, preserving the robe's lower-leg texture; the separate cloth pants have their own undertrouser paint. `native-layered-v1` contains 126 hash-bound Human male full/removal frames. Successful capture is still not automatic visual acceptance. Other-body fit, motion, hairstyles, full package deployment and live persistence/remote tests remain pending.

Thirty-two new64px inventory icons have original imagegen source sheets and import receipts in each prototype's `icons-v1` directory. The two legacy rejected sets with earlier valid live technical frames still count as zero accepted armor sets.

### 2026-09-28 — V2 art direction superseded by owner feedback

The owner requested raid gear and substantially stronger thematic imagination after viewing all four v2 designs. Broader WoW and Final Fantasy XIV references may inform concepts; the output still needs original authored assets that work with the target game's model/texture construction. Source fitting of the superseded concepts must not be used to imply artistic acceptance. The Forge native-review catalog labels each v2 set as a superseded study, preserves exact evidence revision labels, and retains full/removal comparison controls for technical inspection. Future designs require new concept review rather than inheriting approval from these captures.

### 2026-09-28 — selected raid concepts prepared for Forge

The new concept card precedes the historical native construction review. It shows the four selected original sheets, short construction briefs and explicit concept-only status. The public catalog binds the unchanged PNG bytes and source prompt receipt; all measured model counts remain null and native/live acceptance remains false. Images load lazily with their original resolution available by opening a sheet. The publisher refuses to replace an existing concepts-v1 image with different bytes. This is a local web preparation; publication and actual browser verification remain separate.

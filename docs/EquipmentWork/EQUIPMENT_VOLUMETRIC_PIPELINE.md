# Equipment volumetric pipeline: weapons, helms and shoulders, step by step

Written 2026-09-30 so that a smaller local model can make new Classic/TBC-style equipment for MSUIClient
without improvising. Follow the steps in order. Every number here was measured, either on the 124
original weapons and the original helms and shoulders (`artifacts/equipment-workshop/reference-study-v2`),
or in this pipeline's own native captures. When this document and a tool disagree, the tool's docstring
wins. Report the disagreement.

Companion documents:
- `docs/EQUIPMENT_LOCAL_MODEL_COOKBOOK.md`: the older route, with painted body pieces, the forge package
  and install.
- `docs/EQUIPMENT_HANDOFF_2026-09-30.md`: the current state, what was learned, the from-scratch track
  and the next steps (read it before continuing this work).
- `docs/EQUIPMENT_RAID_HANDOFF_2026-09-28.md`: the earlier installed-equipment record.
- MSUIClient `shared_docs/EQUIPMENT_CAPTURE.md`: the capture system.

Worked results:
- weapons: `artifacts/equipment-workshop/volumetric-v1/README.md`
- armor: `armor-volumetric-v1/README.md`
- internet sources: `external-workflows-v2/README.md`

---

## 0. Laws (read before touching anything)

1. **Never mutate production.** Make new candidates only.
   - Do not run `raid_weapon_adoption.py`, `apply_raid_weapon_names.py`, `forge_weapons.py`,
     `register_accepted_armor.py`, `forge_armor.py` or `stage_armor.py`.
   - Do not use `/UnifiedPatch/Rebuild`, `Install-ReviewedRaidWeaponPatch.ps1`,
     `Promote-TestedSkinExtraClient.ps1`, `four-set-installation-v1\promote-reviewed.ps1`, or
     `.reload item_template`.
   - Never write into `MSUIClient\GameData\Data\*.MPQ`. Those are the installed originals, hard-linked
     into review mounts.
   - No SQL writes. Never touch the unrelated CMaNGOS server. Never commit, push or create branches:
     the owner does git.
2. **Never overwrite evidence.** Every tool refuses an existing output directory. Make a new, versioned
   name (`-a`, `-b`, `final-c`...). Keep rejected builds and add `-rejected` to their name. Do not
   delete them.
3. **Fit the equipment to the original bodies.**
   - Never change a body, skeleton, geoset or attachment point.
   - Never invent attachment offsets to hide geometry.
   - Helms and shoulders are separate models. Everything else is painted texture plus geosets.
4. **Numbers do not prove the look.** A build passes its gates, then it is captured natively, then a
   human looks at the sheets. Say "passes gates" or "captured". Never say "done" or "looks right".
5. Work from `C:\Users\nico\source\repos\MangosSuperUI`. The tools are in
   `tools\equipment-workshop\volumetric\`.

Programs:

```powershell
$py  = 'C:\Users\nico\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'  # or Python 3.12 with numpy+Pillow
$bl  = 'C:\Users\nico\Desktop\CRPG-Ultum\_tools\blender\blender-4.5.12-windows-x64\blender.exe'   # only for external models
$vol = 'C:\Users\nico\source\repos\MangosSuperUI\tools\equipment-workshop\volumetric'
$W   = 'C:\Users\nico\source\repos\MangosSuperUI\artifacts\equipment-workshop'
```

Windows PowerShell 5.1 pitfalls that broke earlier runs:
- Do not pass Python code with double-quoted f-strings through `python -c`: PowerShell strips the inner
  quotes. Write a `.py` file instead.
- Bash quoted heredocs are unreliable here as well.
- Reading `.json` written by PowerShell needs `encoding='utf-8-sig'` (BOM).
- `Select-Object -First N` cuts a pipe and reports exit code 255. That is not a failure.

---

## 1. Know the target: what the originals measure

### 1.1 Weapon size per family (bounding box in the weapon frame; p10 / median / p90 of the originals)

| family | length | width | thickness |
|---|---|---|---|
| sword1h | 1.13 / 1.54 / 2.44 | 0.29 / 0.48 / 0.59 | 0.10 / 0.16 / 0.38 |
| dagger | 0.72 / 0.92 / 1.17 | 0.25 / 0.26 / 0.36 | 0.08 / 0.11 / 0.21 |
| axe1h | 0.93 / 1.26 / 1.47 | 0.45 / 0.67 / 0.83 | 0.10 / 0.15 / 0.41 |
| mace1h | 0.99 / 1.28 / 1.52 | 0.34 / 0.50 / 0.68 | 0.19 / 0.40 / 0.52 |
| sword2h | 1.70 / 1.99 / 2.34 | 0.28 / 0.50 / 0.73 | 0.08 / 0.12 / 0.18 |
| axe2h | 1.43 / 1.72 / 1.90 | 0.60 / 0.81 / 0.89 | 0.13 / 0.19 / 0.28 |
| mace2h | 1.25 / 1.62 / 2.03 | 0.37 / 0.65 / 0.74 | 0.33 / 0.44 / 0.67 |
| staff | 2.23 / 2.30 / 2.56 | 0.23 / 0.61 / 0.80 | 0.12 / 0.24 / 0.44 |
| polearm | 1.98 / 2.61 / 2.67 | 0.28 / 0.39 / 0.65 | 0.07 / 0.14 / 0.30 |
| wand | 0.73 / 0.91 / 1.06 | 0.11 / 0.21 / 0.48 | 0.08 / 0.13 / 0.23 |

**Rule:** aim between the 10th percentile and the median unless the brief says otherwise, and write the
reason down when you leave that range. WoW weapons are chunky. A realistic proportion reads as a stick.

The first volumetric pass missed this: blades were a third of the originals' thickness (0.016 against
about 0.05) and a dagger was 0.07 wide. Width includes the guard and the head.

### 1.2 Blades, handles, guards, heads

- **Mid-blade section:** width ~0.15-0.19, thickness ~0.045-0.052, thickness/width ~0.27-0.30.
- **Blade edges:** sharp. The thickness 2% in from the edge is only 4-12% of the maximum.
- **Blade profile:** blades taper, with a coefficient of variation of the thickness along the blade of
  about 0.35.
- **Blade sections:** 86% of original swords and daggers have a ridged (diamond/lens) or wedge section.
  None is a plain slab.
- **Grip radius:** about 0.026. The measured stock handle median is 0.026, P95 0.0325. The hand's socket
  is 0.020-0.033 (`metrics.SOCKET_RADIUS`).
- **Grip shape:** 82% of handles are a 4-sided diamond ring.
- **Fist window:** x from -0.10 to +0.06 around the origin (`metrics.FIST_WINDOW`). Nothing but handle
  may be there.
- **Guard and pommel:** on blades the guard base is ~+0.10 above the origin, and the handle continues
  ~0.32-0.41 below it. The pommel sits near -0.21.
- **Guard span:** ~1.8-2.4x the blade width.
- **Handle share of total length:** ~23% on blades, ~47% on axes, ~40% on maces, ~50% on staves, ~53% on
  polearms.

### 1.3 Budgets (body triangles; the attachment hard limit is 1500)

- **All weapon families:** medians ~207 (Vanilla), 336 (TBC), 714 (Wrath).
- **Plate helms:** ~382; cloth helms ~259.
- **Shoulders:** ~168 (V), 434 (T), 501 (W); plate ~444.
- **This pipeline's results:**
  - weapons: 364-872 triangles
  - shoulders: 472-644 per side
  - helms: 524-624 per variant

### 1.4 Textures and painted values (the VALUE LAW)

- **Sizes:** 128x64 (Vanilla), 256x128 (TBC), 256x256 (Wrath). Helm skins are 128x128 or 256x256;
  shoulder skins are 256x128 in the accepted sets. The armor validator accepts 128 or 256 per side.
- **Measured on the RAW textures of the 124 originals** (`reference-study-v2/weapons/texture_values.json`)
  over the covered texels:
  - the median luma is only ~78/255 (the models range 45-118);
  - the 90th percentile is ~163 (at most ~210);
  - near-white (>220) is ~1% (at most 7%);
  - ~18% of texels are darker than 40.
- **The engine brightens.** A texture painted "bright" becomes a flat white plane in MSUIClient. The
  first painter was calibrated on lit renders, and its pale blades showed exactly that failure.
- **Gate** (`paint.VALUE_GATE`): p50 <= 130, p90 <= 215, near-white <= 0.07. `build.py` prints
  `VALUE LAW:` when a build fails it.
- **Fix a bright build** by darkening palette **mids** (mid luma <= ~170, light <= ~235). The painter's
  soft knee catches only the highlights. For external colour maps, use `paint.mid_target` (a gamma
  solved to a median of 110).
- **Find the culprit** with `python part_values.py RECIPE.py`: it lists every part's texel share and
  p50/p90. One large bright part (a gem with density 1.8) can fail a whole atlas.

### 1.5 The cardboard test (is it an extruded silhouette?)

```powershell
cd $W\reference-study-v2\tools
& $py cardboard.py glb <source.glb>
```

- **Verdicts:** `volumetric` below 0.6; `borderline` from 0.6; `cardboard` when the score is >= 0.8 and
  the depth is constant (CV <= 0.25).
- **Originals:** a median of 0.24, and 0 of 124 are cardboard. The first custom weapons scored 8 of 10
  cardboard.
- **It is a blade test.** Box hammer heads fail it legitimately (Ironfoe scores 0.87). For a box head,
  show instead that the depth varies from the side, using crowned `section_coffin` sections.

### 1.6 Armor facts

- **Shoulder frame** (attachment 6 = left, 5 = right): glTF, Y up, X front, +Z = the character's right.
  - The left pad goes outward along -Z. The right pad is the exact mirror across Z.
  - The frame carries the body's scale: HuM 1, TaM 1.6, GnF 0.55. **One pair serves all 16 bodies**, and
    all 40 original pairs are mirrors.
- **Shoulder seat** (bounding box including spikes): above 0.19-0.23, below 0.22-0.33, front/behind
  0.19-0.24, outward 0.35-0.37, inward 0.09-0.14.
  - **The dome itself is LOW.** The stock Valor pauldron, sliced at x=0 in the same frame: crown y ~ +0.10
    (studs to 0.16); neck edge (z +0.10, y +0.08); outer side z ~ -0.30 at y ~ 0; flap to y ~ -0.22.
  - The skull top is only ~0.29 above the HuM shoulder origin. A crown near +0.20 reaches eye level.
- **Pad construction:** closed plates ~0.04 thick (cloth ~0.026, leather ~0.022) with a modelled,
  dark-painted underside, decorated face outward.
- **Helm frame** (attachment 11): origin at the skull top for most bodies, but NOT consistently.
  - HuM's eyes sit at x +0.09, y -0.14. HuF's eyes sit at x +0.01. TrF's eyes are above the origin.
  - So every body gets its own helm variant: **16 variants, same topology, one skin**. Blizzard's race
    variants are re-fitted vertices, not scales: TaM ~x1.47 and GnM ~x1.20 of HuM.
  - A helm hangs ~0.38 below the origin. Walls are 0.023-0.033.
  - Ears, horns and tusks pass through stock helms.
  - `helmetVis` must be an existing HelmetGeosetVisData row. Keep the accepted set's value.
- **Body data** (`fit.py`): `armor-redesign/fit-baselines-valor/race{1-8}-sex{0,1}-stand.json` holds the
  whole visible body in each attachment's frame (Stand 0 @ 0.6 s, appearance 0). Eye landmarks are in
  `raid-v3/head-anatomy/eye-landmarks.json`.
- **Race order:** 1 Hu, 2 Or, 3 Dw, 4 Ni, 5 Sc, 6 Ta, 7 Gn, 8 Tr. Body codes are race + M/F, e.g. `TaF`.

---

## 2. The weapon contract (coordinates, origin, grip)

- **Axes:** a source GLB is glTF, right-handed, Y-up. **+X runs from the grip toward the tip or head**,
  Y is the blade WIDTH (the edge direction), Z is the THICKNESS. One unit is one WoW model unit. The
  compiler maps (x, y, z) -> M2 (x, -z, y).
- **Origin:** the origin is where the hand holds. Nothing but the handle may enter the fist window.
- **UVs:** top-left origin (u right, v down), in [0,1].
- **Material:** one opaque material, embedded PNG, white base colour factor.
- **Scale:** weapons scale with the hand bone per race (HuF ~0.85, TaM ~1.40). One model serves everyone,
  so check it on all 16 bodies.
- **Hand closure:** MSUIClient closes the hand with the original HandsClosed animation while a weapon is
  held, and opens it in stowed or spell poses (AnimationData WeaponFlags). A handle outside the socket
  either buries the fingers (too thick) or floats (too thin).

---

## 3. Building a weapon from scratch (the volumetric route)

### 3.1 Write the brief first (5 lines)

Write 5 lines: family, silhouette idea, construction (what is turned, lofted or inflated), materials and
palette, and the target L x W x T from table 1.1. Example (Last Lock):

> sword1h, broad short sword; single-edged wedge blade with one key tooth; stone guard plate; hex grip;
> bolt-head pommel; blue-black iron, old-gold bearing. Target 1.15 x 0.33 x ~0.10.

### 3.2 Choose primitives (`shapes.py`); never extrude a flat outline

| need | primitive | notes |
|---|---|---|
| blades, axe bits, flanges, fins, planar plates | `inflate_field(outline, name, material, field=...)` | outline in the X-Y plane; thickness varies across it: `field_wedge(H, edge_line, spine_width, power, edge, spine_rim, chamfer)` (single edge), `field_lens(H, power, edge)` (double edge / fins), `field_plate(H, bevel, rim)` (guards) |
| grips, shafts, pommels, bells, collars, cups | `revolve(profile, ..., sides, hard=...)` | profile [(x, r), ...]; profiles may turn back (hollow bells); `hard` = crease indices |
| sections along a path | `loft(stations, ..., spine=...)` | sections: `section_diamond`, `section_hexblade`, `section_lens`, `section_rect`, **`section_coffin`** (crowned block: full depth only across a middle band - hammer cheeks, yoke bars) |
| bows, arcs, prongs, spikes, bars | `tube(points, radii, ..., sides)` | radius 0 at an end makes a point |
| crystals | `gem(radius, top, bottom, ...)` | faceted bipyramid |
| chamfered blocks | `box(size, ..., chamfer)` | prefer `loft` with `section_coffin` for anything seen from the side |
| placement | `xform.place(mesh, RX(a), T(x, y, z))`, `armor.align_x(mesh, point, normal)` | `align_x` puts a part's +X along a surface normal |
| proportion pass | `xform.grow(parts, names, pivot, factors)`, `xform.move(parts, names, offset)` | scales/moves named parts only; UV island sizes follow |

The rule that fixes "thick cardboard": thickness must vary across every flat part, with knife edges on
blades and crowned faces on blocks, and the silhouette must change from the side and from the end.

### 3.3 Write the recipe (a Python file with `build()` returning a dict)

**Start by copying `recipes/templates/starter_sword1h.py`.** It is a plain arming sword that passes
every gate as it stands (built 2026-09-30 into `artifacts/equipment-workshop/volumetric-templates/starter-sword1h-b`:
320 triangles, fill 0.73, cardboard 0.20 volumetric, L 1.30 x W 0.35 x T 0.11 inside the sword1h range,
painted p50/p90 57/157, welded census 0/0). Rename `key`/`title`, then change one thing at a time and
rebuild into a new folder each time.

The skeleton of every recipe, trimmed from the template. **This block alone is NOT a passing
weapon** (without the guard and pommel it is under the sword1h width and thickness range); copy the
file, not the block:

```python
import os, sys
sys.path.insert(0, os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))))
import numpy as np
from shapes import box, loft, revolve, section_diamond

STEEL = {'dark': '#1c2230', 'mid': '#5a6678', 'light': '#c2ccd8'}   # mid luma <= ~170 (value law)

def build():
    # blade: diamond sections (x, width, thickness) tapering in BOTH width and thickness
    st = [(0.115, 0.170, 0.050), (0.265, 0.160, 0.047), (0.535, 0.166, 0.043), (0.755, 0.150, 0.037), (0.96, 0.120, 0.028)]
    blade = loft([{'x': x, 'section': section_diamond(w, t)} for x, w, t in st] +
                 [{'x': 1.09, 'section': section_diamond(0.001, 0.001), 'tip': True}],
                 'blade', 'metal', mirror_halves=True, density=1.25)
    grip = revolve([(-0.135, 0.0), (-0.135, 0.024), (-0.120, 0.027), (0.066, 0.027), (0.082, 0.024), (0.082, 0.0)],
                   'grip', 'leather', sides=8, hard=(1, 4))           # radius 0.027: inside the 0.020-0.033 socket
    # ... guard (swept diamond), ecusson (box), pommel (revolve) - see the template
    return {'key': 'my-sword-a', 'title': 'My Sword (a)', 'weaponType': 'sword1h',
            'budget': [250, 900], 'slabRange': None, 'checkGrip': True,
            'parts': [blade, grip],
            'paint': {'blade': {'material': 'metal', 'style': 'polished', 'palette': STEEL, 'fuller': (0.04, 0.62, 0.07)},
                      'grip': {'material': 'leather', 'palette': {'dark': '#150b07', 'mid': '#4a2a17', 'light': '#a5774e'}, 'turns': 7}},
            'icon': {'background': ('#101216', '#2e3440'), 'halo': '#c8ccd4'}}
```

**Why not a flat outline pushed through `inflate_field`** (this document's first example did that and
it failed three gates: L 1.08, W 0.16 and T 0.09 all under the family's 10th percentile, cardboard
"borderline" 0.65 with a constant blade depth): `inflate_field(field=...)` samples its field (a
half-thickness) only at the outline and at its inset rings (`rings`, default 0.3 and 0.62 of the
inradius); inside the innermost ring the surface is one flat plateau and never reaches the field's
maximum. A plain `field_lens` blade is therefore a slab with bevels (EC ~0.9), and thinner than asked
(a lens field for a 0.048 blade built 0.025). Use `inflate_field` for irregular silhouettes (axe bits,
a key tooth, fins, wedge blades), wrap its field with a taper along X as `recipes/vol1/last_lock.py`
does, measure what it built, and loft straight blades from diamond sections.

- **Paint materials:**
  - `metal` takes `style` polished/dark/warm, `ridge`, `edges`, `rim_side`, `fuller`, `slot`,
    `scratches`.
  - The others are `leather` (`turns`), `wood`, `bone` (`cracks`), `stone`, `cloth` (`weave`) and `gem`.
- **Options for any part:**
  - `gradient` + `gradient_axis` ('s' along the island, 't' across it);
  - `bevel`, `groove`, `contrast`;
  - `runes=(s0, s1, t0, t1, count)` + `rune_glow`;
  - `bands=(count, width)`;
  - `decals=[{'rect': (s0, s1, t0, t1), 'shape': 'ellipse'|'rect', 'color', 'alpha', 'feather'}]`, for
    painted slits, eye holes and stars.
- Palettes are hex dark / mid / light.

### 3.4 Build and read the gates

```powershell
cd $vol
python build.py recipes\vol1\my_sword.py $W\volumetric-v1\my-sword-a
```

The output directory must be new. `report.json` holds:
- `triangles` / `budgetOk`;
- `validation`:
  - `degenerate`, `duplicateFaces`, `nonManifoldEdges`, `inconsistentWinding`, `collapsedUv`,
    `uvOutside01` must all be 0;
  - the check welds positions at 1e-6 like the production validator;
  - `boundaryEdges` is a warning only;
- `atlas.fill`: aim for 0.6 or more;
- `atlas.values.ok`: the value law;
- `grip.fits`: the handle p95 radius inside the socket;
- `slabIndex`.

Then:
- look at `preview-board.png` (flat face, edge-on, end-on, 3/4 views, and the atlas);
- run the cardboard test (section 1.5); `python $vol\gate_summary.py <build dir> ...` prints every gate plus the verdict in one line;
- compare the dimensions with table 1.1: `python $vol\diagnostics\size_check.py <build dir> ...` prints
  L x W x T against the family's p10/median/p90 and flags LOW/HIGH (`seal_weapon_candidates.py` records
  the same for a sealed set);
- if the cardboard score is high, `python $vol\diagnostics\wall_share.py <recipe>` names the parts whose
  side walls cause it.

If the proportions are off, append a **proportion pass**: keep the construction as `_build_v1()` and add a
`build()` that calls `xform.grow` on the named head or blade parts. Never scale the grip. Every
`recipes/vol1/*.py` except Unfallen shows this.

### 3.5 Stage, compile, capture on all 16 bodies, check seating

Weapons are reviewed inside a PRIVATE overlay that swaps one of the ten raid weapon slots' model and skin.
Nothing is registered. The slot's item and display rows stay what they are.

```powershell
& $py $vol\stage_candidates.py $W\volumetric-v1\stage-my-a "$W\volumetric-v1\my-sword-a=last-lock"
dotnet $W\..\..\tools\equipment-workshop\raid_weapon_compile\bin\Release\net8.0\raid_weapon_compile.dll `
    $W\volumetric-v1\stage-my-a\source\index.json C:\Users\nico\source\repos\MSUIClient\GameData\Data $W\volumetric-v1\stage-my-a\native
& $py tools\equipment-workshop\raid_weapon_prepare_native.py $W\volumetric-v1\stage-my-a\native
```

- **Manifest:** `python $vol\make_b16_manifests.py <stage>` writes one per weapon: races 1-8, sexes 0-1, views
  front-quarter (45, 8) and left (270, 8).
  - Poses are stand (0 @ 0.6) plus attack-1h (17 @ 0.35), attack-2h (18 @ 0.35) for two-handers, or
    hold-thrown (111 @ 0.3) with sheath 2 for wands.
  - Sheath state is 1 (drawn). Wands use 2.
- **Capture:**
  ```powershell
  powershell -File $vol\Invoke-NativeReview.ps1 -ReviewDirectory <stage>\native -Manifest <manifest> -Name b16-<slot> -MeshProbe
  ```
  It refuses to run beside another MSUIClient, binds every input hash before and after, and writes
  `execution-<name>.json`.
- **Seating** (in `<stage>\native`; summarize with `python $vol\seating_summary.py <stage>\native <slot> ...`):
  ```powershell
  & $py tools\equipment-workshop\grip_seating_check.py captures-b16-<slot> captures-b16-<slot> seating-b16-<slot>
  ```
  The stock sword control on the same bodies buries at most 5 hand vertices (mean 0.5). This run's
  candidates reach at most 8 (the Night Elf male), mean 0-1.3.
- **Sheets:**
  - `weapon_rows.py OUT.png <captures...> --pick race1-sex0-stand-sheath1-front-quarter ...` crops around
    the held weapon;
  - `before_after.py` puts the installed weapon (captured through `prepare_installed_baseline.py`) beside
    the candidate in the same frames.
- **Seal:** `seal_weapon_candidates.py BUILDS STAGE STAGE\receipt.json --baseline <baseline review dir>`.

---

## 4. The texture atlas (automatic, but know what it does)

- **Placement.** `uvpack.pack` places each part's island-local (s, t) UVs:
  - long islands (aspect >= 4, at least 45% of the longest) become full-height columns;
  - the rest are skyline-packed (bottom-left, 90-degree turns, several orders tried) at the largest
    uniform scale;
  - if one dominant island caps that scale and the fill stays under 62%, it is placed alone first
    (the `anchor`) and the others share the rest at a larger scale.
- **Density.** `density` on a part raises or lowers its texel share. A gem at 1.8 took 38% of a staff's
  atlas and failed the value gate.
- **Fill.** Fill counts rectangles. A crescent's empty corners are wasted (known limit).
- **Shared layouts.** `uvpack.apply_layout(mesh, ref, W, H)` gives a variant the reference's exact layout.
  The 16 helm variants share one skin this way.

---

## 5. Helms and shoulders

### 5.1 Shoulder pair (one mirrored pair for all 16 bodies)

Recipe, as in `recipes/armor/broken_gate_shoulder.py`. The recipe's `build(ctx)` makes the LEFT pad in the
shoulder_L frame:

1. **Outer surface.** Use `armor.pad_grid(PROFILE, front, behind, center, nu, nv, rolloff, crown)`.
   PROFILE is the centre slice (y, z) from the neck edge over the crown to the lower rim. **Start from the
   Valor calibration:**
   `[(0.045, 0.105), (0.095, 0.040), (0.112, -0.050), (0.088, -0.160), (0.022, -0.255), (-0.080, -0.300), (-0.195, -0.306)]`,
   with `center=(0.02, -0.12, -0.08)` and `rolloff=0.265`.
2. **Fitted plate.** Use `armor.fitted_shell(G, name, material, ctx['pts'], center, thickness, clearance=0.016-0.018, ceiling=0.22)`.
   - It builds the underside, pushes it radially to clear every body sample, and rebuilds the outer
     surface on top.
   - It returns `(mesh, outer_grid, inner_grid, push)`.
   - A sample may push by at most 0.06 beyond the design (`fit.push_out(max_push=0.06)`).
3. **Construction on the outer grid:**
   - lames: `armor.sub_grid` of the lower band, moved down and outward, then fitted again;
   - bars and rims: `armor.surface_curve(outer, u=... or v=..., lift=...)` fed to `tube`;
   - scalloped edges: `armor.scallop`;
   - radial ribs: `armor.ribs` (pass the columns as `hard_u`);
   - knobs and gems: `armor.align_x`.
4. **Paint.** Undersides are dark (their own part). Plate edges get automatic bright trim, because the
   island kind is `shell`.

Build:

```powershell
cd $vol
python build_armor.py recipes\armor\my_shoulder.py $W\armor-volumetric-v1\my-shoulder-a [--exclude ScM]
```

- **Outputs:**
  - `shoulder_L.glb` and `shoulder_R.glb` (R is the exact mirror, same UVs);
  - `skin.png` (256x128);
  - `report.json`: seat against the calibrated ranges, per-body penetration L/R, fit push,
    welded validation, values;
  - `preview-fit.png` (pad edges over 6 bodies in the frame) and `preview-paint.png`.
- **Accept only if:**
  - `seatOk`;
  - the push max is <= 0.06 with a small mean;
  - the penetration is a handful of samples at most;
  - there are no defects.
- **Look at `preview-fit.png`:** the pad must arch over the shoulder, not spike toward the head.
- **Outlier body:** if one body forces a huge push, `--exclude` it and build a per-body fit for it
  (`shoulderFits` in the package).

### 5.2 Helm set (16 variants, one skin)

Recipe, as in `recipes/armor/broken_gate_helm.py`. It defines `build_body(code, ctx)`:

1. `eye = ctx['anatomy'][code]['eye']` and `P = ctx['samples'](code)` give the head samples in the helm
   frame.
2. **Scale:** `s = (helm.scalp_top(P, eye) - eye[1]) / 0.131`. 0.131 is the HuM eye-to-scalp height.
3. **Rings:**
   ```python
   G, radii, gates = helm.rings(P, eye, [(dy * s, 'fit' | 'dome'), ...], nu=12, clearance, thickness, dome={row: factor})
   ```
   - Rows are heights relative to the EYES. Use the same rows on every body, so the painted eye slit lands
     on every body's eyes.
   - The fitter gates head samples with a robust, vertically coherent ellipse, so ears, horns, tusks,
     buns and a rotated frame's shoulder pass through.
   - Below the brow, a ring may grow at most 1.22x (sides and back) or 1.9x (front, for muzzles) over the
     ring above.
4. **Style operations:**
   - `helm.symmetrize(G)` always;
   - `flatten_front` for a faceplate;
   - `keel`;
   - `flare(G, row, factor)`.
5. **Shell:**
   - Closed helm: `shapes.closed_shell(G, name, material, thickness, cap_lift, cap_t=0.10, hard_v=...)`.
   - Open-faced hood: `closed_shell` on the crown rows, plus
     `shapes.shell(helm.orient_outward(helm.open_face(G, brow_row, face_deg), centre))`. **Tuck the hood's
     top row 6 mm under the crown rim**, or the shared ring welds into non-manifold edges.
   - Mask: `helm.face_rings(P, eye, rows, nu=24, face_deg=58-64)` gives a patch on the face. Then
     `helm.shape_mask(M, top=lambda u: ..., bottom=lambda u: ...)` gives it a real outline, then
     `helm.orient_outward`, then `shell`.
   - Fins and crests: `helm.fin_on(G, x_front, x_back, heights)` gives an outline standing on the helm's
     sagittal profile, which `inflate_field` turns into a fin.
6. **Paint:**
   - slits, eye holes and motifs are `decals` in the part's island (s, t);
   - for a closed helm, s runs round from the back (the face is at s = 0.5) and t runs down, with the
     crown cap in t 0-0.10;
   - the inside is dark.
7. Return `{'key', 'title', 'parts', 'paint', 'grid': G, 'thickness', 'scale': s, 'gates': gates}`.

Build:

```powershell
python build_helm.py recipes\armor\my_helm.py $W\armor-volumetric-v1\my-helm-a
```

- **Outputs:** `helm_<Body>.glb` x 16, `skin.png`, `report.json` (per variant: triangles, head samples
  inside the wall and the worst depth, bounds against the stock Valor helm's bounds on that body, the eye
  row's offset), `preview-fit-a/b.png` and `preview-paint.png`.
- **Accept only if** every variant has no defects, the eye row sits within ~0.003 of the eyes, and head
  samples inside the wall are 0-40 at <= ~0.04 (ear and hair roots).
- **Look at the fit sheets.** No bells over the shoulders, no ring blown up by horns.
- **Before packaging**, run the welded edge census:
  `python $vol\glb_manifold.py helm_HuM.glb`. Production rejects non-manifold edges.

### 5.3 Package a candidate (never the accepted ZIP itself)

```powershell
python $vol\armor_candidate_package.py <accepted armor.zip> $W\armor-volumetric-v1\pkg-<name> `
    --shoulders <build_armor out> --helms <build_helm out> --note "what changed and why"
```

- Every other member (the painted pieces, icons, names, helmetVis) is copied byte-identical, and
  `package-source-provenance.json` proves it member by member.
- The accepted ZIPs:
  - Broken Gate: `artifacts\shoulder-fits-pipeline-v1\fitted-pair-candidate-v2-proof\broken-gate\armor.zip` (44a95df6);
  - Leviathan: `...\leviathan\armor.zip` (b44313be);
  - Glass Comet: `armor-redesign\raid-v3\glass-comet\motion-local-relief-v1\candidate-T\forge-package\armor.zip` (fc46965c);
  - Gloamwing: `armor-redesign\raid-v3\gloamwing\motion-shoulder-candidate-v15\armor.zip` (631f3cc8).
- `ArmorPackageCheck` (Debug) is stale: it predates `shoulderFits`, so skip it. The production compiler
  runs in the next step.

### 5.4 Private native review on all 16 bodies

```powershell
powershell -File $vol\Invoke-ArmorCandidateReview.ps1 -Package <pkg>\armor.zip -SetKey broken-gate -Name <study-name>
```

- **What it runs:**
  - `ArmorPrototype --package`, the production compiler, into
    `armor-redesign\prototypes\volumetric-v1\<study-name>` (review-only display ids 960100+);
  - the mount;
  - the race rules;
  - a bound capture of 16 bodies x 6 views x 4 poses (stand, attack-1h, cast-omni 52, run 5), which is
    64 mesh probes and 384 frames.
- **Baseline:** for a before/after, copy the ACCEPTED zip byte-identically into a new folder with a
  provenance note and review it the same way. Never write beside the accepted evidence.
- **Sheets:**
  - `armor_sheet.py OUT.png <captures> --set <key> --focus helm|shoulders --bodies HuM,... --cols stand:front,... [--before <baseline captures>]`;
  - `armor_overview.py` puts accepted and candidate side by side for several sets.
- **Seal:** `seal_armor_candidates.py OUT.json --candidate key=STUDY,PKG,HELM_BUILD,SHOULDER_BUILD`.

---

## 6. Internet-sourced models and references

### 6.1 Licences

Only CC0, or CC-BY with the attribution line kept, may be used. For each source:
- save the download bytes;
- save the asset page and licence page, or the API record;
- record the author and URL;
- record SHA-256s;
- write the courtesy credit.

`external-workflows-v2/originals/provenance.json` is the model. Reject:
- unclear custom licences;
- AI-generated assets;
- GPL;
- anything that copies a Blizzard design;
- login-walled hosts.

CC-BY needs the exact line, for example `"Viking Helmet" by Michael Fuchs (URL), licensed under CC BY 3.0
(URL). Modified: <what changed>.`

### 6.2 Step 1 for every model: Blender to a clean intermediate

```powershell
& $bl --background --python-exit-code 1 --python $vol\external_blender_adapt.py -- JOB.json
```

JOB fields:
- `source`;
- `out` (a new .glb);
- `texture` (binds a PNG when the file references a missing .tga);
- `decimateTo`, `unwrap: "smart"`, `bakeSize`, `bakeExtrusion` (only for the documented failure test,
  see 6.4).

It imports, joins, applies transforms, triangulates, and exports Y-up.

Then measure; never guess:

```powershell
python $vol\profile_external.py <intermediate.glb> <profile.png>
```

The widths per station along the longest axis tell you:
- which end the tip is on;
- where the guard is (the widest station near one end);
- where the handle is (the narrow stretch past the guard);
- which axis is the width (the blade's wide one) and which is the thickness.

### 6.3 Route A: low-poly, hand-painted: import directly

```powershell
python $vol\adapt_external.py JOB.json <new out dir>
```

JOB fields:
- `long` / `width`: the intermediate axes that become +X and +Y;
- `gripAt`: a fraction from the butt;
- `stretch`: `[{from, to, factor}]` for a handle that is too short;
- `length`, or `scale`;
- `head`: `{from, scale: [w, t], blend}` to exaggerate a realistic head;
- `gripRadius`;
- `valueLaw`;
- `midTarget`.

It writes the contract GLB, the icon and the report with every step and gate. Then stage and capture as in
section 3.5. Worked example: `w2-sword00-contract`, 260 tris. The handle was stretched x2 because it was
shorter than the fist window.

### 6.4 Route B: high-poly: rebuild from measurements (do NOT decimate)

Collapse-decimating an ornate 14.7k-triangle mace to 600 triangles produced 684 open edges, spiky
fragments, hundreds of UV islands (18% fill) and a muddy bake. That result is kept as `h1-mace-contract`.
Instead:

```powershell
python $vol\measure_reference.py <highpoly intermediate.glb> <reference.json> --long +Y --head-from 0.70
```

This measures the profile per station, the flange count and angles, the flange outline, and palettes from
the ALBEDO. The albedo is the material's `baseColorTexture`; the embedded normal map sampled first gave
violet palettes.

Then write a recipe that builds the low-poly from those numbers, and list every deliberate departure in
its docstring (`recipes/external/ornate_mace_rebuild.py`): length to the family range, head x2.2, handle
to the socket, the ornate noise simplified.

### 6.5 Route C: an external helm fitted to all 16 bodies

```powershell
python $vol\fit_external_helm.py JOB.json <new out dir>
```

JOB fields: `front` / `up` / `right` (intermediate axes), and `slitY` (the measured eye-slit or face-window
height).

- The helm is refitted per axis about the slit onto each body's fitted head envelope.
- Topology, UVs and skin are the same for all 16.
- The ray test counts head samples beyond the helm's first surface.

Then package and review as in section 5.3-5.4. Worked example: `a1-bucket-fitted`, which reports 0 head
crossings on 15 of 16 bodies.

### 6.6 Photos and drawings

A photo gives colour and silhouette, never geometry. Trace the silhouette's key points into a recipe
outline, or into ring rows for a helm, and paint broad value groups. The cookbook's v1 wood exercise shows
the texture side.

---

## 7. What each check proves (and what it does not)

| check | proves | does not prove |
|---|---|---|
| build gates (budget, welded validation, values, grip, atlas) | the file is sound, sized, paintable, holdable | how it looks |
| cardboard test | the blade/bit is not a constant-depth slab | box heads (they fail legitimately) |
| dimensions vs family table | proportions are in the originals' range | style |
| production compile (`raid_weapon_compile`, `ArmorPrototype --package`) | the real game formats accept it | the look |
| native capture (Invoke-NativeReview / bound armor capture) | the real renderer draws it on 16 bodies without technical errors | that the look is right; that other poses and hairstyles are fine |
| seating / penetration counts | hands and bodies meet the gear within measured limits | style; motion outside the captured frames |
| before/after sheets | a human can compare against what is installed | anything without a human looking |
| live world | nothing here; that is a separate owner-approved promotion | |

---

## 8. Failure gallery: symptom, cause, fix (all happened in this pipeline)

Weapons:
1. **Weapon looks like thick cardboard.**
   - Cause: a 2D outline extruded to a constant depth (the first custom set: 8 of 10 scored cardboard).
   - Fix: `inflate_field` with a wedge or lens field, knife edges, taper; check with `cardboard.py`.
2. **Box hammer head still reads flat.**
   - Cause: every block had the same depth. Rectangular loft sections did not help (0.90).
   - Fix: `section_coffin` crowned sections, and proud collars instead of plates buried in the stone.
     A chipped (`fracture`) face.
3. **Head reads as a lantern on a stick.**
   - Cause: the head was undersized (0.55 x 0.19 against Blizzard's 0.70-0.80 x 0.37-0.43).
   - Fix: size it from table 1.1 and the reference cards.
4. **Weapons systematically undersized** (a 0.016-thick blade, a 0.07-wide dagger).
   - Cause: nobody compared against the family table.
   - Fix: a proportion pass (`xform.grow`), never scaling the grip.
5. **Pale blades render as flat white planes.**
   - Cause: painted "high-key", calibrated on lit renders.
   - Fix: the value law. Darken the mids; use the knee; `part_values.py` finds the culprit.
6. **Half-empty atlas** (fill 0.48-0.71).
   - Cause: a shelf packer, and a blade that stopped qualifying as a column after it was widened.
   - Fix: the skyline packer plus the anchor tier.
7. **Painted crease line across a smooth surface.**
   - Cause: every slope change was painted as a crease.
   - Fix: paint only intended (group or part) edges over 28 degrees, or edges over 62 degrees anywhere.
8. **Collapsed UVs on caps and rims.**
   - Cause: fan and rim UVs of zero area.
   - Fix: fan caps with the centre pulled in, rim walls with inset UVs.
9. **Folded faces in inflated outlines.**
   - Cause: an inset larger than the local width.
   - Fix: `_safe_inset` with a clamp at `_local_width`.
10. **Fingers buried or floating.**
    - Cause: a handle outside the 0.020-0.033 socket, or something besides the handle in the fist window.
    - Fix: a revolve grip at 0.026, and the guard at +0.10.

Armor:
11. **Pads rise to eye level like wings.**
    - Cause: a dome crown at +0.20. The study's "above 0.19-0.23" is a bounding box that includes spikes.
    - Fix: calibrate on a real pad sliced in the same frame (Valor crown ~+0.10).
12. **Pad edge spikes through the head.**
    - Cause: the radial push took the maximum body radius, including a second body part further along the
      ray.
    - Fix: `max_push` (0.06), and a penetration test that counts only samples inside or just above the
      plate.
13. **Helm flares into a bell over the shoulders.**
    - Cause: the lowest rings picked up shoulder samples.
    - Fix: growth limits between rows (1.22 at the sides, 1.9 at the front).
14. **One helm ring blows up** (Tauren +-0.35).
    - Cause: horns were most of that slab's samples.
    - Fix: vertically coherent ellipses (`helm.coherent`) with two-pass gating.
15. **Metric reports many crossings on ScM.**
    - Cause: in its rotated frame the shoulder sits beside the head.
    - Fix: the metric uses the fitter's head gates.
16. **Production rejects helms: "6 nonmanifold edges".**
    - Cause: the hood and crown shared ring positions; each part was clean alone.
    - Fix: tuck the hood row under the crown rim. The local validator now welds by position like
      production.
17. **Mask reads as a welding visor.**
    - Cause: a rectangular patch with pin-hole eyes.
    - Fix: `shape_mask` outlines, almond `ellipse` decals, a motif.
18. **Fin floats above a helm.**
    - Cause: a flat fin base on a domed cap.
    - Fix: `helm.fin_on` follows the cap's sagittal profile and sinks the base into it.
19. **Packager: stale checker crashes on `shoulderFits`.**
    - Cause: an old `ArmorPackageCheck` binary.
    - Fix: rely on `ArmorPrototype --package` (Release, current).

External models:
20. **Missing texture.**
    - Cause: the FBX references a `.tga` the archive does not ship.
    - Fix: bind the shipped PNG in the Blender job.
21. **Handle too short for the fist.**
    - Cause: realistic proportions.
    - Fix: `stretch` the handle segment and record the factor.
22. **Ornate high-poly shredded by decimation.**
    - Fix: measure and rebuild (route B).
23. **Violet palettes from a PBR model.**
    - Cause: the normal map was sampled as albedo.
    - Fix: use the `baseColorTexture` image.
24. **Photographic texture too bright or not painted.**
    - Fix: `midTarget` for the value range. A repaint is still needed for the style.

Process:
25. **PowerShell mangled a Python one-liner.** Use script files.
26. **An exit code of 255** after `Select-Object -First` is only the cut pipe.
27. **Tools refuse existing outputs.** That is deliberate: make a new version name. Never delete someone
    else's evidence.
28. **The copy-paste example itself was undersized and borderline cardboard** (found 2026-09-30 while
    writing the handoff: nobody had built it).
    - Cause: a flat outline through `inflate_field` with a lens field: the field is only sampled on
      the rings, so the core is a flat plateau below the requested thickness; no guard or pommel, so
      the width and thickness were under the family table.
    - Fix: `recipes/templates/starter_sword1h.py` (loft of diamond sections). Build every example you
      publish.
29. **A review tool refuses with "client hash differs".**
    - Cause: `Invoke-NativeReview.ps1` and `Invoke-ArmorCandidateReview.ps1` pin the client DLL
      (default `162336f6...`, the hand-grip v4 Release). Any newer installed client changes it.
    - Fix: pass `-ExpectedClientSha256 <new hash>` after checking the new client is the intended
      one, and recapture the baseline with the SAME client before comparing before/after.

---

## 9. Checklists (copy them into your working notes)

**New weapon:**
1. brief with target L x W x T;
2. recipe with volumetric primitives;
3. `build.py`: gates clean, values ok, grip fits;
4. preview board looked at;
5. cardboard test and dimensions against the family table;
6. proportion pass if needed;
7. stage, compile, prepare, capture 16 bodies with the mesh probe;
8. seating;
9. before/after sheet;
10. seal the receipt;
11. write down what is NOT proven.

**New shoulder pair:**
1. brief (set motif, material thickness);
2. profile from the Valor calibration;
3. `fitted_shell` plus construction;
4. `build_armor.py`: seat ok, push <= 0.06, penetration small, no defects;
5. fit and paint previews looked at;
6. package from the accepted zip;
7. private review, 4 poses;
8. before/after against a byte-identical baseline;
9. seal.

**New helm set:**
1. brief (closed, hood and mask, or cap);
2. rows relative to the eyes;
3. rings, symmetrize, style operations, shells, decals;
4. `build_helm.py`: every variant has no defects, eye row within 0.003, crossings shallow;
5. fit sheets looked at (no bells, no blown rings);
6. welded edge census;
7. package, review, before/after, seal.

**External model:**
1. licence and provenance recorded;
2. Blender intermediate;
3. profile measured;
4. route A, B or C chosen and every decision recorded in the job or recipe;
5. gates;
6. native capture;
7. README entry with source and credit.

---

## 10. Where things are (2026-09-30)

- Tools: `tools/equipment-workshop/volumetric/`.
  - `shapes.py`, `mesh.py`, `uvpack.py`, `paint.py`, `creases.py`, `raster.py`, `metrics.py`, `xform.py`
  - `build.py`, `build_armor.py`, `build_helm.py`
  - `armor.py`, `helm.py`, `fit.py`, `fit_view.py`
  - `stage_candidates.py`, `Invoke-NativeReview.ps1`, `Invoke-ArmorCandidateReview.ps1`
  - `armor_candidate_package.py`, `prepare_installed_baseline.py`
  - `weapon_rows.py`, `before_after.py`, `armor_sheet.py`, `armor_overview.py`
  - `seal_weapon_candidates.py`, `seal_armor_candidates.py`, `part_values.py`
  - `external_blender_adapt.py`, `profile_external.py`, `adapt_external.py`, `measure_reference.py`,
    `fit_external_helm.py`
  - `diagnostics/` (measurement aids, read-only: `size_check.py`, `wall_share.py`, `frame_axes.py`,
    `shoulder_survey.py`, `slice_reference.py`, `export_stock_m2.py`, `helm_summary.py`,
    `helm_rows_debug.py`, `helm_inside_view.py`) and `examples/` (the PowerShell sequences that produced
    the 2026-09-30 candidates; they refuse existing outputs)
- Recipes: `recipes/templates/` (start here: `starter_sword1h.py`), `recipes/vol1/` (ten weapons),
  `recipes/armor/` (four shoulder pairs, four helm sets), `recipes/external/`.
- Current state, lessons and next steps for whoever continues: `docs/EQUIPMENT_HANDOFF_2026-09-30.md`.
- Weapon candidates: `artifacts/equipment-workshop/volumetric-v1/final-e`, staged as
  `stage-vol1-final-e` (receipt). Not installed.
- Armor candidates: `artifacts/equipment-workshop/armor-volumetric-v1` (README, receipt). Studies are in
  `armor-redesign/prototypes/volumetric-v1`. Not installed.
- External: `artifacts/equipment-workshop/external-workflows-v2` (README).
- Promotion of any candidate is a separate step, approved by the owner, through the existing
  ArmorForge / raid weapon paths.

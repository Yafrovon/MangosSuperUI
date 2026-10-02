# Equipment handoff, 2026-09-30: volumetric pipeline, candidates and the from-scratch track

Written at the owner's request ("prepare a handoff with everything learned, so another agent can keep
going; part of this is also trying to make from scratch"). It is the entry point for whoever continues
the equipment art work. It does not repeat the how-to: that is `docs/EQUIPMENT_VOLUMETRIC_PIPELINE.md`
(the pipeline doc). This file says what exists, what was learned, what to do next and how to make new
equipment from scratch.

**Read in this order:**
1. Both repositories' `AGENTS.md` (MangosSuperUI and MSUIClient).
2. This file.
3. `docs/EQUIPMENT_VOLUMETRIC_PIPELINE.md`: sections 0 (laws), 1 (the numbers), 8 (failure gallery,
   29 items), then the section for your task.
4. The README of the evidence you touch: `artifacts/equipment-workshop/volumetric-v1/README.md`
   (weapons), `armor-volumetric-v1/README.md` (helms and shoulders), `external-workflows-v2/README.md`
   (internet sources).
5. For installed equipment, registration, forge packages and live proof:
   `docs/EQUIPMENT_RAID_HANDOFF_2026-09-28.md` (its dated top sections supersede its numbered body)
   and `docs/EQUIPMENT_LOCAL_MODEL_COOKBOOK.md`.

---

## 1. Status: what exists, what works, what is installed, what needs proof

| track | exists | works (measured) | installed / live | still needs proof |
|---|---|---|---|---|
| **Hand grip** (MSUIClient closes the hand on held weapons with the original HandsClosed animation) | yes, client source (uncommitted) | 55,059 numerical checks per build; 270 native frames per binary, personally reviewed; weapons unmoved, only palm vertices move | **installed 2026-09-30 04:37 UTC**: Release `162336f6...`, Debug `c8d513de...`; observed live (self + remote, warrior169 with Silken Mercy) | the owner's visual acceptance; the remote stowed control (it failed to toggle, so it is missing); MSUIClient does not stow weapons for WeaponFlags 0x4/0x10 animations yet (a separate session is doing that, see section 9) |
| **Ten weapon candidates** (`volumetric-v1/final-e`) | yes | all gates; 640 native frames on 16 bodies; seating max 8 / mean <= 1.3 buried hand vertices (stock sword control: 5 / 0.5); before/after sheets vs the installed weapons | **no**; the installed weapons are unchanged | the owner's visual verdict; the size findings in section 7 |
| **Four helm sets + four shoulder pairs** (`armor-volumetric-v1`) | yes | production compiler accepts all; 384/384 frames per set on 16 bodies x 6 views x 4 poses, 0 technical errors; before/after vs byte-identical accepted copies | **no**; the accepted sets are unchanged | the owner's verdict; plain procedural paint; hairstyles, mounts, sitting, emotes, motion, live world |
| **Internet-sourced routes** (`external-workflows-v2`) | 3 routes demonstrated, 4 more inputs sourced | direct import (CC0 sword), measure-and-rebuild (CC0 high-poly mace; naive decimation failed and is kept), external helm refitted to 16 bodies (CC0 bucket) | **no** (demonstrations; slots were private vehicles) | a repaint of the bucket helm; the 4 unused inputs |
| **Starter template** (`recipes/templates/starter_sword1h.py`) | yes (new at handoff) | passes every gate as it stands (section 5.2) | no | not captured natively (not needed until it is a design) |
| **Docs** | pipeline doc, this handoff, READMEs, MSUIClient `shared_docs/EQUIPMENT_CAPTURE.md` section | `interface-wire-check --shared-docs-only` was PASS after the MSUIClient edit | n/a | nothing |

**Reproducibility (checked at handoff).** Every reviewed candidate's recipe file still matches the
`recipeSha256` in its build report (10/10 weapons, 4/4 helms, 4/4 shoulders), and rebuilding with
today's library reproduces the reviewed files byte-for-byte (tested: Last Lock and Meridian GLB +
skin; the Glass Comet helm set's 16 GLBs + skin; the Broken Gate shoulder L/R + skin). Note that
`broken-gate-helm-g` is the same recipe as the packaged `broken-gate-helm-f`, rebuilt 8 minutes later;
`f` is the one that was packaged and reviewed.

---

## 2. Rules for this work (short form; AGENTS.md is binding)

- **The owner does git.** No commits, pushes, branches or worktrees.
- **No SQL writes, no database or worldstate backup/restore/swap, never the unrelated CMaNGOS server.**
- **Leave the Commander raid paused.** Never touch Testwar (787) or the raid bots (115-142, 150-160).
  Equipment QA actors are `Forgeplateqa` (GUID 169, Human male warrior 60) and `Forgecastqa` (GUID 170,
  Human female mage 60) on the private account `EQUIPQA0928`. Never expose credentials
  (`private-config.json`, settings).
- **Authorized without asking (owner, 2026-09-30):** boot, build, rebuild and restart MSUIClient, the
  SuperUI web app and VMaNGOS for this project. Check for a user-owned MSUIClient process first and
  never close one without asking.
- **Candidates only.** Promotion (registration, item rows, patch installs, `.reload item_template`)
  needs the owner. Do not run the production-mutating tools: `raid_weapon_adoption.py`,
  `apply_raid_weapon_names.py`, `forge_weapons.py`, `publish_raid_weapon_review.py`,
  `register_accepted_armor.py`, `forge_armor.py`, `stage_armor.py`, `install-server-dbc.py`,
  `Install-ReviewedRaidWeaponPatch.ps1`, `Promote-TestedSkinExtraClient.ps1`,
  `four-set-installation-v1\promote-reviewed.ps1`, the web app's `/UnifiedPatch/Rebuild`, and
  `.reload item_template`. Do not repeat completed weapon mutations, registrations or item grants.
  Never write into `MSUIClient\GameData\Data\*.MPQ` (installed, hard-linked into review mounts).
- **Never overwrite evidence.** Every tool refuses an existing output; use a new version suffix and
  keep rejected builds with `-rejected`.
- **Fit equipment to the original bodies.** No body, skeleton, geoset or attachment changes, no runtime
  offsets.
- **Numbers do not prove the look.** Say "passes gates" / "captured". The owner judges the sheets.
- Build the client in Debug AND Release when changing MSUIClient code.

---

## 3. Environment facts (this PC)

- **Everything in this track is local-only.** MangosSuperUI `.gitignore` ignores `/tools/` (line 67)
  and `/artifacts/equipment-workshop/` (line 96): the owner keeps these owner-local and transfers them
  privately (they contain derived original-client art). `docs/EQUIPMENT_*.md` are untracked. Do not
  un-ignore anything. A move to another machine needs an explicit private transfer.
- **Programs:**
  - Python 3.12 with numpy + Pillow (`python`), or
    `C:\Users\nico\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe`.
  - Blender 4.5: `C:\Users\nico\Desktop\CRPG-Ultum\_tools\blender\blender-4.5.12-windows-x64\blender.exe`
    (external models only; its glTF exporter parameter is `export_texcoords`, with an s).
  - .NET: `tools\equipment-workshop\raid_weapon_compile\bin\Release\net8.0\raid_weapon_compile.dll`,
    `tools\equipment-workshop\ArmorPrototype\bin\Release\net8.0\ArmorPrototype.dll`.
- **Installed now (verified at handoff):** MSUIClient Release DLL `162336f6e626d4ca...`, Debug
  `c8d513de08314d5a...`; `patch-4.MPQ` `e607f73988bf0454...`; `patch-7.MPQ` `6a43fc6c5a2d214c...`.
  No MSUIClient process was running.
- **The review tools pin the client.** `Invoke-NativeReview.ps1` and `Invoke-ArmorCandidateReview.ps1`
  refuse to run unless the Release DLL hashes to `162336f6...`. When the client changes (the sheath
  session in section 9 will change it), pass `-ExpectedClientSha256 <new>` after confirming the new
  client is intended, and recapture any baseline with the same client before comparing.
- **Windows PowerShell 5.1 pitfalls that broke runs:**
  - `python -c` with double-quoted f-strings loses its quotes: write a `.py` file.
  - JSON written by PowerShell has a BOM: read it with `encoding='utf-8-sig'`. One recipe
    (`funeral_bell.py`) starts with a BOM too; imports handle it, `ast.parse(open(...).read())` does not.
  - `Select-Object -First N` cuts the pipe and reports exit code 255; that is not a failure.
  - **Variable names are case-insensitive:** `$R` and `$r` are the same variable (a loop over `$r`
    silently overwrote a `$R` path at handoff).
  - The command sandbox has refused `Remove-Item` commands containing `k:` or `\d+'`; split them.
  - Bash quoted heredocs are unreliable here; write files with the Write tool.
- **The locator** (`http://127.0.0.1:5077`) indexes the web app, but NOT the git-ignored
  `tools/equipment-workshop` (ArmorPrototype, the Python tools). Search those with file-scoped Grep.

---

## 4. What was learned (the rules behind the candidates)

### 4.1 Measure the originals; never guess
- **Sizes.** Per-family L x W x T of the 124 original weapons is in pipeline doc table 1.1. The first
  volumetric pass had blades a third of the originals' thickness (0.016 vs ~0.05) and a 0.07-wide
  dagger; nobody had compared. Now: `diagnostics/size_check.py BUILD` flags LOW/HIGH per axis.
- **Values.** Raw original textures have a median luma of ~78 and a p90 of ~163 (near-white > 220 is
  ~1%). The engine brightens: a texture painted "high-key" (calibrated on lit renders) renders as a
  white plane. Gate: p50 <= 130, p90 <= 215, near-white <= 7%. Darken palette MIDS (mid luma <= ~170).
- **Shoulder seat.** The study's "above 0.19-0.23" is a bounding box including spikes. The real dome
  crown (stock Valor pauldron sliced at x = 0) is ~+0.10; a +0.20 crown puts pads at eye level.
  Calibrate on a real sliced reference (`diagnostics/export_stock_m2.py` + `slice_reference.py`).
- **Helm frames differ per body** (HuM eyes x +0.09 y -0.14; HuF x +0.01; TrF eyes above the origin).
  Build 16 variants from rings anchored at each body's eyes, one topology, one skin.

### 4.2 Build volume, not silhouettes
- **Straight blades: loft diamond sections** (`loft` + `section_diamond`), tapering in width AND
  thickness. The template's EC is 0.28, inside the 0.25-0.5 that diamond and lens blades give, and
  its score 0.20 is below the originals' median 0.24.
- **`inflate_field` only samples its field on the rings.** The field value is a half-thickness at the
  outline and at each inset ring (default 0.3 and 0.62 of the inradius); inside the innermost ring the
  surface is one flat plateau, so the field's maximum is never reached. A plain `field_lens` blade
  therefore builds as a bevelled slab (EC ~0.9), and thinner than asked: a lens field for a 0.048
  blade built 0.025. Use inflate for irregular silhouettes (axe bits, key teeth, fins, wedge blades),
  wrap the field with a taper along X (`recipes/vol1/last_lock.py`), and always measure the result.
- **Blocks: crowned `section_coffin` lofts**, proud collars, chipped faces; never same-depth boxes.
  Box hammer heads still fail the (blade) cardboard test legitimately (Ironfoe 0.87; our Unfallen 0.95).
- **Turned parts** (`revolve`) may turn back on themselves: hollow bells and muzzles with real cavities
  (Funeral Bell, Last Coordinate) read far better than solid lumps.
- **Holes cannot be cut** (opaque skins): paint slots, eye holes and windows as dark decals.
- **Grip contract:** origin in the palm, grip radius ~0.026 (socket 0.020-0.033), nothing else inside
  x -0.10..0.06, guard base ~+0.10, pommel ~-0.21. The hand now closes with the original animation,
  so a wrong radius shows immediately (buried or floating fingers).
- **Chunky proportions.** WoW heads are ~2x realistic (the Poly Haven mace head was scaled x2.2);
  handles are long; guards span 1.8-2.4x the blade width.

### 4.3 Fitting to the 16 bodies
- **Shoulders:** one mirrored pair serves every body because the attachment frame carries the body
  scale (HuM 1, TaM 1.6, GnF 0.55); all 40 original pairs are mirrors. L = attachment 6, R = 5; the L
  pad goes outward along -Z. The underside is pushed radially to clear every body's samples, at most
  0.06 beyond the design (larger pushes spike through the head).
- **Helms:** rows are heights relative to the EYES; ears, horns, tusks, buns and ScM's shoulder are
  gated out by robust, vertically coherent ellipses (they pass through, as on stock helms); rings may
  grow at most 1.22x (sides) / 1.9x (front) below the brow.
- **Weld by position like production.** Two parts that share ring positions become non-manifold edges
  and the compiler rejects the package (6-7 edges on the first hood/crown helms). Tuck one row under
  the other; `glb_manifold.py` runs the production weld locally.

### 4.4 Process lessons (what reviews missed, including this session's own)
- **Build every example you publish.** The pipeline doc's copy-paste recipe had never been built; at
  handoff it measured L 1.08, W 0.16, T 0.09 (all under the sword1h 10th percentile) and "borderline"
  cardboard. Replaced by the verified template (failure gallery item 28).
- **Tabulate all three dimensions.** The weapon review table listed L x W only; thickness puts
  Unfallen (T 0.28 vs p10 0.33) and Meridian (T 0.10 vs 0.12) under range (section 7).
- **Check every body against the stated limit.** The armor README said helm samples outside the wall
  were "<= 0.04"; two helms reach 0.056-0.058 on TrM (corrected in that README).
- **Same renderer for before/after.** Baselines are byte-identical copies of the installed/accepted
  assets captured with the same client and manifests.
- **Numerical checks do not show visual defects.** The hand-grip fix passed 55,040 checks, and the
  first native frames still found two defects (mount snapshot probes, a Tauren closed fist beside the
  weapon in spell poses).
- **Seal what you measured.** `seal_weapon_candidates.py` / `seal_armor_candidates.py` record hashes
  of the library, recipes, builds, overlays, captures and sheets.

---

## 5. The from-scratch track (the main forward work)

"From scratch" here means new designs authored as recipes with this library, not adapted from other
models. The ten weapons and four helm/shoulder sets were all made this way; the template below is the
clean starting point.

### 5.1 Pick the closest worked recipe

| family / piece | start from | its construction |
|---|---|---|
| sword1h | `recipes/templates/starter_sword1h.py` | loft of diamond sections; swept diamond crossguard; ecusson box; revolve grip and pommel |
| sword1h, single-edged | `recipes/vol1/last_lock.py` | inflate of a drawn silhouette with a wedge field x taper; plate guard; painted slot |
| dagger | `recipes/vol1/silken_mercy.py` | wedge section from a recurved silhouette, out-of-plane curve; lens "eye" through both faces |
| axe1h | `recipes/vol1/tideclaw.py` | crescent inflated thick at the shaft, knife-thin at the rim; turned collar and joint |
| mace1h | `recipes/vol1/funeral_bell.py`, `recipes/external/ornate_mace_rebuild.py` | hollow turned bell with swept ribs; six flanges at 60 degrees |
| sword2h | `recipes/vol1/orchard_winter.py` | wedge blade with a swept thorn spine; fork guard of swept prongs |
| axe2h | `recipes/vol1/ransom_deep.py` | asymmetric jaw silhouette inflated thick at the socket, knife at the rim; tooth stubs |
| mace2h | `recipes/vol1/unfallen_weight.py` | crowned (coffin) lofted cheeks, yoke with a real window, proud collars (thickness below range) |
| staff | `recipes/vol1/comet_meridian.py` | swept broken arc, faceted turned lens, fins, turned shaft (thickness below range) |
| polearm | `recipes/vol1/pale_proboscis.py` | wedge silhouette with a wing curve, swept chitin spine, plain shaft where hands go |
| wand | `recipes/vol1/last_coordinate.py` | hollow turned muzzle, swept prongs, tilted lens-edged disc |
| shoulder pair (plate) | `recipes/armor/broken_gate_shoulder.py` | Valor-calibrated dome, two lames, bars, rolled rim, studs |
| shoulder pair (cloth/leather/mail) | `glass_comet_shoulder.py` / `gloamwing_shoulder.py` / `leviathan_shoulder.py` | mantle; layered wing plates with lobed hems; ribbed clam shell with scallops |
| helm, closed | `recipes/armor/broken_gate_helm.py` | closed shell over fitted rings; faceplate; painted slit |
| helm, hood + mask | `glass_comet_helm.py`, `gloamwing_helm.py` | crown shell + open hood (tucked row) + shaped mask patch + fin/antennae |
| helm, cap + crest | `leviathan_helm.py` | ribbed cap, ring of teeth, fin standing on the cap (`helm.fin_on`) |

### 5.2 A new weapon, literally
1. Write the five-line brief (family, silhouette, construction, palette, target L x W x T between the
   family's p10 and median). Put it in the recipe's docstring.
2. Copy the closest recipe from 5.1 to `recipes/<your-folder>/<name>.py`; change `key` and `title`.
3. Build into a NEW folder:
   `cd tools\equipment-workshop\volumetric; python build.py recipes\<folder>\<name>.py ..\..\..\artifacts\equipment-workshop\<your-track>\<name>-a`
4. Gates, in this order, all must pass:
   - `python gate_summary.py <build>`: budgetOk, problems none, grip fits, fill >= 0.6, cardboard not
     "cardboard" (volumetric < 0.6);
   - `python diagnostics\size_check.py <build>`: no LOW/HIGH, or a written reason;
   - the build printed no `VALUE LAW:` line (else `python part_values.py <recipe>` names the part);
   - look at `preview-board.png` (flat face, edge-on, end-on, 3/4, atlas).
5. Fix one thing, rebuild into `-b`, repeat. Keep the rejected folders.
6. Native review: stage the build into a raid slot of the SAME hand type as a private vehicle (the slot
   key decides the review poses: 1H slots `last-lock` `silken-mercy` `tideclaw` `funeral-bell`; 2H
   `orchard-winter` `ransom-deep` `unfallen-weight` `comet-meridian` `pale-proboscis`; wand
   `last-coordinate`). Follow `examples/stage_capture_external.ps1` (edit the builds, slots and stage
   name): stage -> compile -> prepare -> manifests -> capture with `-MeshProbe` -> seating.
7. Sheets and seal: `weapon_rows.py` / `before_after.py`, then `seal_weapon_candidates.py`.
8. Report "passes gates, captured on 16 bodies", list what is not proven, and show the owner the sheets.

The template itself (`artifacts/equipment-workshop/volumetric-templates/starter-sword1h-b`): 320
triangles, fill 0.73, cardboard 0.20 volumetric (EC 0.28), L 1.30 x W 0.35 x T 0.11 (all inside the
sword1h range), painted p50/p90 57/157, welded census 0 boundary / 0 non-manifold, grip p95 0.027.

### 5.3 A new shoulder pair or helm set
- Shoulders: pipeline doc 5.1 (`build_armor.py`); helms: 5.2 (`build_helm.py`). Test the extreme bodies
  first (TaM, TrM, GnF, ScM) with `diagnostics/helm_rows_debug.py` and `helm_inside_view.py`, then read
  `diagnostics/helm_summary.py` for all 16.
- To review a new helm/shoulder design today, package it into one of the four accepted zips with
  `armor_candidate_package.py` (a private vehicle; every other member stays byte-identical) and run
  `Invoke-ArmorCandidateReview.ps1` with that set's key. Name the study after YOUR design, and say in
  the `--note` that the set is only a vehicle.

### 5.4 A whole new armor set from scratch (not possible end-to-end yet)
The full package route exists but has one blocker for review:
1. Painted body pieces (chest or robe, legs, gloves, boots, bracers, belt) come from the cookbook
   route: regional body PNGs on the fixed atlas regions (`docs/EQUIPMENT_LOCAL_MODEL_COOKBOOK.md`
   section 4) described in a `painted-pieces.json` (example:
   `armor-redesign/prototypes/briarpath/painted-pieces-v1/painted-pieces.json`).
2. Helm (16 variants + skin) from `build_helm.py`; shoulders (L, R + skin) from `build_armor.py`;
   eight 64 px icons named by piece key.
3. `tools/equipment-workshop/assemble_armor_v2_package.py CONFIG.json` builds the `armor.zip`. The
   config keys (from its source): `name`, `material` (plate/mail/leather/cloth), `designNotes`,
   `pieceNames`, `helmModels` (all 16 body codes), `shoulderModels` (`L`, `R`), optional
   `shoulderFits`, `helmSkin`, `shoulderSkin`, `helmetVis` (existing HelmetGeosetVisData ids only),
   `iconDirectory`, `paintedPieces`, `sourceFitReport`, `outputDirectory` (must not exist). Worked
   configs: `armor-redesign/raid-v3/broken-gate/package-draft-v1.config.json` and the Glass Comet
   `forge-package*.config.json` files.
4. **Blocker:** `ArmorPrototype --package` accepts only the keys `broken-gate`, `leviathan`,
   `gloamwing`, `glass-comet` (`tools/equipment-workshop/ArmorPrototype/FullArmorStudy.cs` line 31),
   and `Invoke-ArmorCandidateReview.ps1` line 3 has the same `ValidateSet`. The key is only a label
   (display ids are allocated from 960100 in order; the key names the compile report and the capture
   set). The fix is to accept a validated pattern such as `^[a-z0-9-]{3,40}$` in both places, then
   rebuild ArmorPrototype Release. This is a shared helper: rebuild only when no other agent is using
   it, and record the new DLL hash.
5. Registration and installation of a new set go through `register_accepted_armor.py` and the unified
   patch: owner-only.

### 5.5 Design guidance for new pieces
- Silhouette first at game scale: judge at the capture's sheet size, not in close-up. If the idea is
  not readable at 64 px tall, simplify.
- Every flat part varies in thickness across and along; every block is crowned; every round part is
  turned with a real profile. Check the edge-on and end-on views on the preview board.
- Budgets: weapons 320-870 triangles were enough here (the owner's range is 250-2000; the attachment
  hard limit is 1500); shoulders 470-650 per side; helms 520-630 per variant.
- Paint: dark mids, the set's accent colour on a few bearings/rims, motifs as decals. The procedural
  painter is plain: the biggest visible gap between these candidates and Blizzard pieces is painted
  detail (scales, trims, glyphs), not geometry.
- Reference from the originals, not from memory: the reference study
  (`artifacts/equipment-workshop/reference-study-v2`) has the measurements, and route "photos and
  drawings" (pipeline doc 6.6) applies to the sourced CMA sallet photos (`external-workflows-v2/originals/r1-photo-cma-visored-sallet`, CC0) as a reference for a new from-scratch helm.

---

## 6. Next steps, in priority order (each with its acceptance test)

1. **Owner visual verdicts.** Show `volumetric-v1/stage-vol1-final-e/review/before-after-{1h,2h,wand}.png`
   and `armor-volumetric-v1/overview-four-sets-{HuM,TaM}.png`, plus any per-set sheets asked for.
   Record each verdict (accept / revise / reject) per item in the READMEs. Nothing is promoted without it.
2. **Paint motifs on pads and helms.** Reuse each accepted set's painted motifs (scales, trims) as
   decals or texture regions on the new pad faces and helm shells. Accept when: value gate ok, fill
   unchanged or better, a new study captured, and the before/after shows detail comparable to the
   accepted pieces.
3. **Fix the measured weapon weaknesses** as new versions (`final-f`): Unfallen head deeper (T >= 0.33),
   Meridian T >= 0.12, and the edge cases (Last Lock T, Bell L, Last Coordinate L, Proboscis L) moved
   inside the range; Ransom's jaw paint (flat); Proboscis fill 0.63. Accept when `size_check.py` shows no
   LOW/HIGH (or a written exception), all gates pass and the 16-body capture + seating are recaptured.
4. **Helm details:** identify the 10 TrM samples at 0.056-0.058 on `glass-comet-helm-d` /
   `gloamwing-helm-d` (`helm_inside_view.py`, then close native views); give the Glass Comet fin real
   thickness (it reads as a card edge-on on GnF); look at `gloamwing-helm-d` fill 0.50. Accept when every
   body is <= ~0.04 or each exception is classified (ear/hair root) from real views.
5. **Coverage:** hairstyles other than appearance 0, mounted, sitting, emote poses and full animation
   clips (not one frame). The capture manifest already accepts an optional top-level `appearance`
   object (`skin`, `face`, `hairStyle`, `hairColor`, `facialHair`; one appearance per manifest) and a
   `poses` list of `animationId` + `timeSeconds` (sample several times per clip). See MSUIClient
   `shared_docs/EQUIPMENT_CAPTURE.md`, "2026-09-28 — explicit offline appearance inputs". Accept when
   helm and shoulder sheets exist for at least the long-hair/horn/tusk extremes of each body and for
   several frames of run, attack and cast, with no visible cut through skin or face.
6. **From-scratch sets:** unblock 5.4 (key pattern + ArmorPrototype rebuild), then build one complete
   new set end to end as the proof that the whole route works.
7. **Tooling debts:**
   - `adapt_external.py`'s grip-remap window caught the mace collar (route B note);
   - `ArmorPackageCheck` (Debug) is stale (no `shoulderFits`); the production compiler is the check;
   - the UV packer cannot reuse the corners of crescent islands (fill limit);
   - the cardboard test has no box-head mode (Unfallen, Ironfoe fail legitimately);
   - `stage_candidates.py` names captures after the vehicle slot, so record the build-to-slot mapping
     (the stage's `source/index.json`) in every README;
   - `fit.clearance_report` is a stub that raises `NotImplementedError`;
   - `helm_inside_view.py` counts differ from the build report's metric (it is a coarse viewer).
8. **External inputs not yet used:** w1 KayKit axe (CC0, swatch-palette UVs: needs an unwrap and a
   repaint), w3 Price mace (CC0), a2 Viking helmet (CC BY 3.0, attribution line in the README), r1 CMA
   sallet photos (CC0 reference). Also repaint the fitted bucket helm (photographic texture).
9. **Promotion** of any accepted candidate: only with the owner, through the existing raid weapon /
   ArmorForge paths, with new receipts and live proof.

---

## 7. Known weaknesses of the current candidates

Weapons (`final-e`; `size_check.py` at handoff; originals p10 / p90 in brackets):

| weapon | finding |
|---|---|
| Weight of the Unfallen (mace2h) | box head reads "cardboard" 0.95 by the blade test (documented exception); **T 0.28 [0.33]**: thinner than 90% of the originals |
| Meridian of the Glass Comet (staff) | **T 0.10 [0.12]** |
| Silken Mercy (dagger) | W 0.13 [0.25], a deliberate slender moth-wing brief |
| The Last Lock (sword1h) | T 0.099 [0.10], at the edge |
| Bell of the Unburied (mace1h) | L 0.993 [0.99], at the edge |
| The Last Coordinate (wand) | L 0.72 [0.73], at the edge |
| Proboscis of the Pale Hunt (polearm) | L 2.68 [p90 2.67], at the edge; atlas fill 0.63, the lowest |
| Ransom of the Deep (axe2h) | jaw paint reads flat (review note) |

Note: the installed Last Lock (not the candidate) buries 12-21 hand vertices in both old and new
clients; the candidate buries at most 8 (mean 1.1).

Armor (`armor-volumetric-v1`):
- procedural paint, plainer than the accepted sets' hand-painted motifs (all four);
- helm clearance: TrM 10 samples at depths up to 0.056 (Glass Comet) and 0.058 (Gloamwing), over the
  0.04 guidance; GnF 40-41 at up to 0.040-0.042; no cut is visible in the TrM stand captures at sheet
  scale (the pale spikes there are the Troll's own ears passing through, as on stock helms);
- the Glass Comet comet fin reads as a flat card edge-on;
- the masks cover the eyes (painted almond holes instead);
- shoulders: 0-17 penetration samples per body per side in stand, mostly the Tauren mane and neck;
- `gloamwing-helm-d` atlas fill 0.50;
- untested: hairstyles other than 0, mounted/sit/emote poses, motion clips, the live world.

---

## 8. Evidence index (all under `C:\Users\nico\source\repos\MangosSuperUI\artifacts\equipment-workshop\`)

| what | path | sha256 |
|---|---|---|
| weapon candidate receipt | `volumetric-v1/stage-vol1-final-e/receipt.json` | `124b771727bc2cc006728e30f7b36dde6ef321d51165b005b1dc41d771c4e70d` |
| weapon review overlay | `volumetric-v1/stage-vol1-final-e/native/prototype.MPQ` | `7785513270306ea35666d2097044b088b599011bd5e64ccc5f706d991034d46f` |
| weapon before/after sheets | `volumetric-v1/stage-vol1-final-e/review/before-after-{1h,2h,wand}.png`; baseline captures `volumetric-v1/baseline-installed/` | in the receipt |
| armor candidate receipt | `armor-volumetric-v1/receipt-candidates.json` | `2ef334c773824dd5d268ebebad99e4e7ef382e4946280e9e7a1a677f5181553f` |
| armor studies (384 frames each) | `armor-redesign/prototypes/volumetric-v1/{bg-helm-f-shoulder-e, glass-comet-helm-d-shoulder-b, gloamwing-helm-d-shoulder-b, leviathan-helm-c-shoulder-b, *-accepted-baseline, bg-external-bucket-helm}` | in the receipt |
| armor overviews | `armor-volumetric-v1/overview-four-sets-HuM.png`, `-TaM.png` | |
| external sources and licences | `external-workflows-v2/originals/` (`provenance.json`, licence texts, tool logs) | provenance.json prefix `7bc76cf1686f7566` |
| external results | `external-workflows-v2/adapted-v1/`, `stage-external-v1/`, `pkg-broken-gate-bucket-helm/` | |
| hand grip v4 seal | `hand-grip-v1/build-checkpoint-v4/checkpoint.json` | `b57a27c53faf158c73d867f6469eddee829bf8b981f47eb13418b8a7df45287a` |
| hand grip rollback | `C:\Users\nico\AppData\Local\Temp\msui-hand-grip-rollback-20260930T043736Z` | |
| starter template build | `volumetric-templates/starter-sword1h-b/` | `report.json` inside |
| reference measurements | `reference-study-v2/weapons/metrics.json`, `weapons/texture_values.json` | |
| body data | `armor-redesign/fit-baselines-valor/`, `armor-redesign/raid-v3/head-anatomy/eye-landmarks.json` | |

---

## 9. Parallel work and coordination

- **Sheath reconcile (separate session, started by the owner as task `task_d665278f`, "Implement
  original weapon sheath reconcile in MSUIClient").** It implements the original client's stowing of
  weapons during AnimationData WeaponFlags 0x4/0x10 animations, which the hand-grip v4 only works around
  (under those animations the palm keeps the animation's own fingers). Do not duplicate it. It will
  likely touch `AttachedItemRenderer.cs` / `CharacterEquipment.cs` / `HandGripPose.cs` and produce a new
  client hash: see the pin note in section 3.
- **One native capture slot at a time.** The review tools refuse to run beside another MSUIClient
  process; do not kill processes you did not start.

---

## 10. Open questions for the owner

1. Which candidates (if any) should replace the installed weapons or the accepted helms/shoulders?
2. Are the documented exceptions acceptable: Silken Mercy's slender blade, Unfallen's box head?
3. Should new from-scratch designs become NEW items (new registrations) or replace existing ones?
4. May the ArmorPrototype set-key list be widened for new sets (section 5.4)?
5. Paint direction for pads and helms: reuse each set's hand-painted motifs, or new painted motifs?

---

## 11. Files created or changed in this work (uncommitted; the owner does git)

MangosSuperUI:
- `docs/EQUIPMENT_VOLUMETRIC_PIPELINE.md` (new), `docs/EQUIPMENT_HANDOFF_2026-09-30.md` (this file);
  top section added to `docs/EQUIPMENT_RAID_HANDOFF_2026-09-28.md`; pointer added to
  `docs/EQUIPMENT_LOCAL_MODEL_COOKBOOK.md` (all four untracked).
- `tools/equipment-workshop/volumetric/**` (git-ignored, local only): the library, build/review tools,
  `recipes/`, `diagnostics/`, `examples/`, READMEs.
- `artifacts/equipment-workshop/{volumetric-v1, armor-volumetric-v1, external-workflows-v2,
  volumetric-templates}` and the studies under `armor-redesign/prototypes/volumetric-v1` (git-ignored).
- `artifacts/equipment-workshop/reference-study-v2/tools/texture_values.py` and
  `weapons/texture_values.json` (git-ignored).

MSUIClient:
- the hand-grip implementation (among the modified/untracked files: `World/Units/HandGripPose.cs`,
  `AttachedItemRenderer.cs`, `CharacterEquipment.cs`, `M2Animator.cs`, the equipment capture files)
  and the appended section in `shared_docs/EQUIPMENT_CAPTURE.md`. The working tree also holds other
  agents' unrelated changes (World Builder, NPC creator); never reset or "clean" it.

---

## Suggested opening prompt for the next agent

> Continue the equipment art work in `C:\Users\nico\source\repos\MangosSuperUI`. Read both repositories'
> AGENTS.md, then `docs/EQUIPMENT_HANDOFF_2026-09-30.md` and `docs/EQUIPMENT_VOLUMETRIC_PIPELINE.md`
> (sections 0, 1 and 8). Everything in `volumetric-v1`, `armor-volumetric-v1` and
> `external-workflows-v2` is a reviewed candidate, not installed; do not promote, register or install
> anything without me. Work through the handoff's next steps in order, making every change a new
> versioned candidate and keeping rejected builds. For new designs, start from
> `recipes/templates/starter_sword1h.py` or the closest recipe in the handoff's table, and pass every
> gate plus a 16-body native capture before showing me sheets. Report what passed, what is captured
> and what is not proven; do not call anything done.

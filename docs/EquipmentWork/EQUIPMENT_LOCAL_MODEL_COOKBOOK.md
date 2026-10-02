# Equipment cookbook for a smaller local model

> **2026-09-30: for new weapons, helms and shoulders, use `docs/EQUIPMENT_VOLUMETRIC_PIPELINE.md`
> first.** It is the volumetric route: measured original sizes, the texture value law, fitting to the
> 16 bodies, native review commands and a failure gallery. This cookbook stays the reference for the
> painted body pieces, the forge package, installation and the older external v1 exercise. Current
> state and next steps: `docs/EQUIPMENT_HANDOFF_2026-09-30.md`.

2026-09-28. Target: **MSUIClient, with Classic/TBC art and asset construction**.
This is an executable workshop recipe, not permission to promote every compiled asset.
Use the existing character, skeleton, attachment IDs, geosets and body-atlas layout.
Create new visible geometry for helms, shoulders and weapons. Paint the other armor
pieces onto the existing garment/body regions. A body-specific shoulder fit is a
different authored shoulder mesh, selected by the declared body code. It does not
move the character's bone, rescale the character, or add runtime placement offsets.

Read both repositories' `AGENTS.md`, `ARMOR_FORGE.md`, `WEAPON_FORGE.md`,
`docs/EQUIPMENT_RAID_HANDOFF_2026-09-28.md`, and the current root agent's runtime
receipt first. Their historical pending restart language is superseded by the
owner's later restart authorization. Runtime/native slots still require coordination
between concurrent agents. Leave existing work unstaged; never repeat a completed
Build, weapon mutation, item allocation, or installation just to obtain evidence.

## 1. Work in small, explicit stages

For each stage, finish its output and inspect its checkpoint before continuing.
Use a new output directory on every revision. Do not edit evidence after capture.

| Stage | Input → output | Required checkpoint |
|---|---|---|
| License | Official download → preserved original + provenance | Actual downloadable asset license permits adaptation; original SHA256 saved |
| Design | Brief + worn reference → silhouette/material/part plan | Can name the fastening, hero shape, painted regions and triangle budget |
| Author | OBJ/GLB or new primitives + diffuse → static low-poly GLB | One atlas; real normals/UVs; no body geometry exported |
| Fit | Original posed body + original attachment matrices → authored local vertices | All exposed contacts reviewed; support actually connects |
| Compile | GLB + PNG → M2 + BLP + private review DBC/MPQ | Strict validator and decoded native roundtrip pass |
| Capture | Frozen private mount + actual DLL → native PNGs/probes/assets | Exact source/member/supplier/geometry/coverage audit passes |
| Review | Actual wearer color/contour/crops → explicit visual verdict | Geometry and paint are readable and attached; no exposed cuts |
| Expand | Accepted focused case → all bodies/poses/appearances | Full matrix and per-item layering checks pass |
| Register/install/live | Accepted full package → registered set → installed bytes → real world | Separate receipts for every step; offline evidence is not live evidence |

If a stage fails, write the failure and make a new candidate. Do not lower the
validator, move a body bone, hide an unexpected intersection, or mark a PNG accepted
because the process exited zero.

## 2. Literal setup and commands

All commands below are PowerShell, from this repository. These are the tools used
for the retained exercises; resolve tools on another machine instead of assuming
these paths exist. The .NET helper reads real MPQs; it does not require the web app.

```powershell
Set-Location C:\Users\nico\source\repos\MangosSuperUI
$taskPython = 'C:\Users\nico\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
$taskBlender = 'C:\Users\nico\Desktop\CRPG-Ultum\_tools\blender\blender-4.5.12-windows-x64\blender.exe'
$taskHelper = 'tools/equipment-workshop/ArmorPrototype/bin/Release/net8.0/ArmorPrototype.dll'
& $taskPython -c 'import PIL,numpy; print(PIL.__version__,numpy.__version__)'
& $taskBlender --version
dotnet --version
```

Blender has `bpy`, `bmesh`, `mathutils` and NumPy; it does **not** have Pillow here.
Use `$taskPython` for image preparation/audit and Blender for geometry/contact work.
If the helper must be rebuilt, coordinate with agents using it, then run:

```powershell
dotnet build tools/equipment-workshop/ArmorPrototype/ArmorPrototype.csproj -c Release
```

Before code discovery, ask the locator, for example:

```powershell
$taskQuery = [uri]::EscapeDataString('AuthoredArmorAssetValidator attachment triangle limits')
Invoke-RestMethod ('http://127.0.0.1:5077/locate?task=' + $taskQuery)
```

Use its outline/read routes to read the returned method span. If the service is
down, report/start it according to AGENTS; do not silently search around it.

## 3. Triangle budget: 250–2000 is a design target, not the armor compiler limit

Count triangles **after modifiers and export**, using `indices.Length / 3`.
A quad becomes two triangles. Hard normals and UV seams may increase vertices
without increasing triangles. Report each left/right or body-specific variant
separately, plus the visible pair total.

| Scope | Current rule |
|---|---|
| Owner's visual target | Roughly 250–2000 triangles for a useful low-poly piece; spend triangles on silhouette and support |
| Strict authored armor GLB | **1–1500 triangles per attachment mesh**, at most 65535 vertices |
| Weapon geometry revision lane | `WeaponGeometryRevision.MaxTriangles = 4000`; this is that lane's ceiling, not a blanket promise for every weapon importer |
| Painted chest/legs/gloves/boots/bracers/belt | No new attachment geometry; use existing geosets plus regional diffuse textures |

For armor, choose a working budget of 250–1500. A 1600-triangle shoulder is above
today's contract even though it lies inside the owner's broad 250–2000 target.
Reduce it or discuss an intentional compiler change; never quietly raise the limit.
Do not add useless triangles to force a simple good mesh above 250.

Example budgets actually exercised here:

* Imported rock: original172 → reduced96 triangles. Three rocks + seat44 + two
  fastenings12 each = **356 per shoulder**, 712 for the pair. It compiled but failed art review.
* First wooden platform: four boards44 each + seat44 + two fastenings12 each +
  four bolts16 each = **308 per shoulder**. It also failed art review.
* Repaired curved wood cap: shell192 + two broad supports52 each + four bolts16
  each = **360 per shoulder**, 720 for the pair. Its source/profile is versioned separately.

A practical 600-triangle plan might reserve 300 for the hero silhouette, 120 for
the rim/back, 120 for a real fastening and 60 for accents. Paint scratches, grain,
small rivet shading and seams; do not model every texel. This allocation is advice,
not a validator rule. Preserve the recognizable silhouette when reducing geometry.

## 4. Know the two texture spaces

An attachment's diffuse atlas belongs to that attachment. Its UVs can be newly
authored. The character body atlas has a fixed layout and existing UVs. Do not
assume a rectangle's center is the center of the character's chest.

For a 256×256 body study canvas, pixel coordinates start at the top left:

| Slot | MPQ component region | X,Y,W,H |
|---|---|---|
| 0 | ArmUpperTexture | 0,0,128,64 |
| 1 | ArmLowerTexture | 0,64,128,64 |
| 2 | HandTexture | 0,128,128,32 |
| 3 | TorsoUpperTexture | 128,0,128,64 |
| 4 | TorsoLowerTexture | 128,64,128,32 |
| 5 | LegUpperTexture | 128,96,128,64 |
| 6 | LegLowerTexture | 128,160,128,64 |
| 7 | FootTexture | 128,224,128,32 |

The lower-left face region is not an equipment paint target. Preserve it.
Final packages supply **separate per-item region PNGs**, not one image assigned
to every slot. Exact permitted regions are defined by `ArmorTypeCatalog`:

| Item | Allowed component slots |
|---|---|
| Chest | 0,1,3,4 |
| Robe | 0,1,3,4,5,6 |
| Legs | 5,6 |
| Gloves | 1,2 |
| Boots | 6,7 |
| Bracers | 1 |
| Belt | 4,5 |
| Shirt / tabard, outside the eight-piece authored set | Shirt0,1,3,4; tabard3,4 |

An allowed region need not be filled. Alpha zero leaves the underlying layer
visible; use alpha deliberately. Required image dimensions match the region.
Use `_U`, or both `_M` and `_F` as required by the package contract. Test actual
layer order and removal: gloves, boots, belt, chest/robe can overlap in the same
atlas regions. Tauren/troll foot treatment follows original `ChrRaces` rules;
do not "fix" bare toes by drawing over a different region.

To paint reliably, use the original body's UV layout as a locked guide, mark a few
temporary UV-space landmarks, capture on the actual body, then paint in a new
revision. The V1 torso badge landed near the torso sides: this is retained evidence
that a region rectangle is not a front-view picture. Do not compensate by moving
the body UVs. Do not use random `geosetGroup` or helmet visibility numbers: read a
matching stock item's DBC values, identify the selected original geosets, and
capture the intended silhouette. Existing glove/boot/robe geometry supplies shape.

Attachment PNG limits: opaque128×128,128×256,256×128 or256×256. Icons are64×64
and may have alpha. Each shoulder pair (including declared fits) shares one skin.
If packing independent left/right textures, remap once: `uL=0.5*u`,
`uR=0.5+0.5*u`. Preserve existing packed UVs; never apply that mapping twice.

## 5. Axes, scale, attachment IDs and bones

The shared source contract is right-handed **Y-up**; M2 storage is right-handed
**Z-up**. One source unit is one WoW model unit. There is no implicit unit repair.

```text
source/client local → M2 file: (x,y,z) → (x,-z,y)
M2 file → source/client local: (x,y,z) → (x,z,-y)
```

This is a rotation (determinant+1), so it does not reverse winding. Mirroring an
authoring node is different: negative determinant needs the importer's winding
correction. Export explicit applied transforms and inspect the strict surface report.
UV origin is **top-left**; U points right, V points down. The compiler copies UVs
unchanged and does not flip the image. Blender internally displays bottom-left UVs;
our direct GLB writer uses top-left values and sets Blender preview UVs to `(u,1-v)`.
Blender's normal glTF exporter already performs its Z-up→Y-up conversion: do not
also run an axis swap on the same vertices. These scripts directly write a GLB
from already-computed attachment-local coordinates instead.

For weapons, +X points from the grip origin toward the blade tip. That semantic
does not mean every helm should face its local +X; inspect the real attachment.
The armor validator's ±8-unit coordinate guard is a safety envelope, **not** a
recommended physical size. Compare measured bounds with the original wearer.

| Attachment ID | Meaning | Relevant authored asset |
|---|---|---|
| 0 | Left wrist/shield | Shield lane |
| 1 | Right hand | Main-hand attachment |
| 2 | Left hand | Off-hand attachment |
| 5 | Right shoulder | ModelName2 / R mesh |
| 6 | Left shoulder | ModelName1 / L mesh |
| 11 | Helm | Body-specific helm mesh |

These are **attachment IDs, not bone indices**. The renderer finds the body's
attachment record by ID, then uses that record's `BoneIndex` and position. In the
retained Human male stand control, L uses bone84, R bone83 and helm bone87.
Those numbers are observations for that body; do not hardcode them for other races.

The original renderer uses System.Numerics row-vector order:

```text
itemRoot = Translation(attachment.Position) * skin[attachment.BoneIndex] * worldInstance
worldVertex = [localX,localY,localZ,1] * itemRoot
authoredLocalVertex = [desiredWorldX,desiredWorldY,desiredWorldZ,1] * inverse(itemRoot)
```

The last line is an authoring calculation. Save the actual captured matrix and its
probe hash; bake the resulting vertices into the GLB. Do not replace `itemRoot`
at runtime. The body remains read-only reference geometry and never enters the
exported attachment. Static authored armor GLBs have no skin, animations or weights.
The compiler's native scaffold does not authorize exporting a body skeleton or
borrowing a stock visible model. Native probes verify the actual submitted mesh.

Full armor has sixteen explicit helm variants: `HuM HuF OrM OrF DwM DwF NiM NiF
ScM ScF TaM TaF GnM GnF TrM TrF`. Races1–8 are Human, Orc, Dwarf, Night Elf,
Undead, Tauren, Gnome, Troll; sex0=M,1=F. Helms must preserve face/eye readability
and the chosen hair/ear/horn visibility rules. See `docs/SHOULDER_FITS_EXTENSION.md`
for optional shoulder fits. A shoulder piece may declare:

```json
"models": {"L":"models/shoulder_L.glb","R":"models/shoulder_R.glb"},
"shoulderFits": {"NiF":{"L":"models/shoulder_fit_NiF_L.glb"}}
```

This fragment belongs inside a complete shoulder piece, not at manifest root.
Defaults remain mandatory and every fit owns a distinct path. The compiler creates
hash-bound native companions; do not handwrite those companions or runtime offsets.
This extension targets MSUIClient; it is not a claim that original Blizzard1.12
implements every feature. Preserve the ordinary default assets too.

## 6. How to draw, model, reduce and export

Choose the input route explicitly:

**Existing 3D asset.** Preserve the original. Inspect its bounds, axes, triangle
count, materials and license. Remove scene/collision/helper objects from the export
selection. Decide which part supplies the new silhouette. Reduce a duplicate, then
add your own fastening and back surfaces. Apply dimensions explicitly. Never use
"scale until it looks about right" without recording the dimensions and wearer.
The rock exercise imports OBJ positions/faces, triangulates n-gons, measures its
bounds, applies Blender Decimate0.56, recalculates normals, then normalizes measured
XYZ ranges before authoring the final world dimensions. It does not infer meters
from an OBJ file or claim to preserve an unspecified original up-axis.

**Flat photo/texture.** A photo supplies color, not geometry or a skeleton. Remove
photographic lighting/noise that does not belong on a tiny painted asset, reduce to
the target atlas and broad value groups, then make a simple mesh independently.
The wood exercise uses only a diffuse JPEG. No normal, roughness, displacement or
metallic maps enter this Classic/TBC lane. Its explicit reproducible preparation
is1024px JPEG→128×96 Lanczos→Gaussian0.45→saturation0.5→contrast1.2→32 colors
without dither; the lower32rows are a painted support strip. This is a study
conversion, not proof that automated quantization creates finished hand-painted art.

**New drawing.** Sketch front/side silhouettes with the fastening visible. For a
helm/shoulder, build a low-sided shell from rings or a shallow extruded outline;
add thickness and an underside wherever the camera can see it. Paint broad light,
shadow, edge wear and motif shapes into the diffuse atlas. For chest/legs/etc.,
paint directly in the fixed region templates. A drawing does not automatically
become valid3D: model the side/back and physically join components explicitly.

In Blender, work on a duplicate source, apply scale, triangulate the evaluated
mesh, recalculate outward normals, and inspect both sides with backface culling.
Keep only the selected static attachment, one opaque single-sided material, UV0,
POSITION and NORMAL. Bake color tint into the PNG; baseColorFactor must be white.
Delete unsupported animation/skin/morph data from the exported copy. Export a
self-contained binary GLB with image bufferViews, not external image URIs/dataURIs.
Check a strict report before attempting native conversion:

```powershell
dotnet $taskHelper --validate-mesh <NEW-report.json> <shoulder_L.glb> <shoulder_R.glb>
```

Use real paths in place of angle brackets. The report path must be new. Required:
finite positions/normals/UVs; nonzero normals; UVs inside[0,1]; supported triangle
primitives; budget valid; no degenerate/duplicate faces, collapsed UV triangles,
opposed normals, inconsistent winding or non-manifold edges. Open boundaries are
a review warning, not automatic approval: a helm opening can be intentional;
an exposed missing back face cannot. Multiple connected components require physical
join inspection. The validator does not decimate, repair, mirror or normalize for you.

## 7. Two licensed outside inputs, actually converted

These assets were selected from the internet for differing workflows, not as claims
of high-quality raid gear. Original bytes and license evidence are preserved in
`artifacts/equipment-workshop/external-workflows-v1/originals/`.

| Input | Official source/license | Original SHA256 |
|---|---|---|
| Kenney Nature Kit ZIP, using `Models/OBJ format/rock_tallB.obj` | [Nature Kit](https://kenney.nl/assets/nature-kit), included `License.txt`, [CC0](https://creativecommons.org/publicdomain/zero/1.0/) | `fa7974a0d342bfe63c38664ba9f8ec1a4aab8ea25f099bdc56870e33588c4d9d` |
| Poly Haven Wooden Planks diffuse1k JPEG | [Asset](https://polyhaven.com/a/wooden_planks), [license](https://polyhaven.com/license) | `1273876d6f92fb1a1d29c1bb28f548a9256154187531e84f993b56e347f1d17b` |

Credits retained: Nature Kit by Kenney. Wooden Planks by Charlotte Baglioni
(photography) and Dario Barresi (processing), Poly Haven. CC0 permits adaptation;
credit is preserved even though optional. Download the actual asset, not a website
screenshot/preview image. The included Kenney license labels the archive2.1 while
the page's release label differs; do not invent a consistent version number.
`originals/provenance.json` records download URLs, license URLs, authors, times,
individual file hashes, included license bytes and raw Poly Haven API metadata.

### Reproduce the source exercises in a NEW directory

The following names are an example fresh run. The retained run is
`external-workflows-v1`; do not rerun commands into that directory.

```powershell
$taskRun = 'artifacts/equipment-workshop/external-workflows-replay-v1'
$taskControl = 'artifacts/equipment-workshop/armor-redesign/prototypes/raid-v3/gloam-motion-candidate-v15-current-v1/full-native-v1/base-1-captures/captures.json'
& $taskPython tools/equipment-workshop/download_external_workshop_inputs.py --output "$taskRun/originals"
& $taskPython tools/equipment-workshop/prepare_external_workshop_sources.py --originals "$taskRun/originals" --output "$taskRun/prepared-v1" --control $taskControl
& $taskBlender --background --python-exit-code 1 --python tools/equipment-workshop/author_external_workshop_experiments.py -- --sources "$taskRun/prepared-v1" --output "$taskRun/authored-v1"
dotnet $taskHelper "$taskRun/authored-v1/rock/compile-input.json"
dotnet $taskHelper "$taskRun/authored-v1/wood/compile-input.json"
```

Network/hardlink/native-process operations may require the tool's normal sandbox
approval. Do not bypass a rejection. Downloads are bounded to the named official
assets. Hashes must be compared with the original receipt; if the provider changed
bytes, make a new provenance decision instead of treating it as the same fixture.
Checkpoint: `authoring.json`, `source-experiments.blend`, bothGLBs, bothsource views,
`compiled-v1/provenance.json`, `prototype.MPQ`, `capture.json`, and region PNGs exist.
All four compiled meshes in the retained V1 had zero strict surface errors and
decoded-native roundtrip success. **That did not make their art acceptable.**

The minimal helper input used by the wood study has this exact shape (absolute
paths required; create the empty output directory before invoking the helper):

```json
{
  "sourceDataPath":"C:/Users/nico/source/repos/MSUIClient/GameData/Data",
  "outputDirectory":"C:/work/NEW-review/compiled",
  "atlasPng":"C:/work/source/body-atlas.png",
  "robe":false,"gloveGroup":0,"bootGroup":0,"sleeveGroup":0,
  "captureItemRemovals":false,
  "shoulders":{
    "leftGlb":"C:/work/source/shoulder_L.glb",
    "rightGlb":"C:/work/source/shoulder_R.glb",
    "skinPng":"C:/work/source/wood-atlas.png"
  }
}
```

This single-atlas study deliberately creates review-only display rows960000–960004
inside a private overlay. Those are not registered game items. It is unsuitable
as the final per-item paint package. The helper calls the production validator,
converts axes, replaces visible geometry in a native M2 scaffold, builds BLPs, and
reopens output to compare positions, normals, UVs and indices. It uses the real
MPQ priority mount; never substitute pre-extracted website files.

### Freeze and capture a focused study

Coordinate the native slot first. The owner has authorized tests; coordination
prevents agents from overwriting shared client state. Freeze the current DLL hash
from the current root receipt. Do not copy a historical hash after a promotion.

```powershell
$taskCompiled = "$taskRun/authored-v1/wood/compiled-v1"
& $taskPython tools/equipment-workshop/prepare_armor_prototype_mount.py $taskCompiled
$taskClient = 'C:/Users/nico/source/repos/MSUIClient/MSUIClient/bin/Release/net8.0/MSUIClient.dll'
$taskClientHash = (Get-FileHash -LiteralPath $taskClient -Algorithm SHA256).Hash.ToLowerInvariant()
# Compare taskClientHash with the coordinator's current baseline before freezing.
& $taskPython tools/equipment-workshop/prepare_external_workshop_capture.py $taskCompiled --client $taskClient --client-sha256 $taskClientHash --output "$taskRun/authored-v1/wood/native-v1"
& tools/equipment-workshop/Invoke-BoundEquipmentCapture.ps1 -RunDirectory "$taskRun/authored-v1/wood/native-v1"
& $taskPython tools/equipment-workshop/verify_external_workshop_native.py "$taskRun/authored-v1/wood/native-v1" --output "$taskRun/authored-v1/wood/native-v1/audit-v1"
& $taskBlender --background --python-exit-code 1 --python tools/equipment-workshop/probe_external_workshop_contacts.py -- "$taskRun/authored-v1/wood/native-v1" --output "$taskRun/authored-v1/wood/native-v1/contacts-v1.json"
```

The mount copies numbered mutable patches and creates read-only-use hardlinks to
static originals. Never modify the linked originals. `client-config.json` disables
server connections. The runner binds inputs before/after and refuses old capture
directories. One study is2sets×1HuMbody×1standpose×6views=12frames and2probes.
Repeat for `rock` using its separate directory/config; never merge different
private overlays under the same case identity.

The distinct verifier checks every expected frame/probe, effective manifest,
source/DLL/mount hashes, actual compiled member SHA **and supplier**, material/atlas
binding, actual submitted positions/indices, unchanged original attachment matrices,
and exactly unchanged posed body between matched dressed/paint-only views. It
produces lossless PNG review boards with real captured pixels and recorded crops.
The compiler's roundtrip separately covers normals/UVs. Inspect color and contour
images yourself; numerical checks do not decide style or raster visibility.

Retained V1:24frames,4probes,12compiled members per exercise; zero capture errors;
maximum submitted position error1.72e-7 units. Both failed art review: rock reads as
spikes on a platform; wood reads as boarding planks. Body contacts were counted
separately from component joins. Wood had22 raw body crossings per side in its
inner boards, requiring diagnosis; no generic "contact count zero" waiver applies.

### Repair loop: curved cap, then local crest relief

Preserve V1. V2 replaces the flat plank assembly with a faceted curved wooden shell,
two short broad supports, four bolts, and **the exact unchanged wood atlas**.
Its explicit Human male stand surface is:

```text
u ∈ [-1,1], t ∈ [0,1], seven samples in each direction
x = -0.025 + 0.145*u*(1-0.28*t)
abs(y) = 0.170 + 0.210*t       (mirror sign for R, then recalculate winding)
z = 1.750 - 0.145*t^1.5 - 0.076*u^2
shell thickness = 0.023; broad support depth = 0.067
```

These are measured authoring dimensions for this case, not universal race offsets.
V2 source contacts found4 exposed top crossings per side near the outer crest;
the remaining contacts were mostly underside seating. A local relief revision adds
`0.012*exp(-((t-0.70)/0.20)^2)*(1-u^2)` to Z. The existing body and attachment
matrices stay exact. Do not require a stylized assembled object to be mathematically
disjoint internally; do require any exposed body cut to be investigated and repaired.

```powershell
& $taskBlender --background --python-exit-code 1 --python tools/equipment-workshop/author_external_wood_cap_v2.py -- --sources "$taskRun/prepared-v1" --v1 "$taskRun/authored-v1" --output "$taskRun/authored-v2"
& $taskBlender --background --python-exit-code 1 --python tools/equipment-workshop/probe_external_workshop_source.py -- "$taskRun/authored-v2/authoring.json" --output "$taskRun/authored-v2/source-contacts-v1.json"
& $taskBlender --background --python-exit-code 1 --python tools/equipment-workshop/author_external_wood_cap_relief.py -- --sources "$taskRun/prepared-v1" --v1 "$taskRun/authored-v1" --output "$taskRun/authored-v2-relief-v1"
New-Item -ItemType Directory -Path "$taskRun/authored-v2-relief-v1/wood-cap/compiled-v1"
dotnet $taskHelper "$taskRun/authored-v2-relief-v1/wood-cap/compile-input.json"
```

Run source contacts, private mount, frozen12-frame capture, exact audit and visual
review again for that new candidate. The same capture/audit commands above accept
its `wood-cap` paths. The retained relief revision completed12 native frames with
exact source/body/attachment checks and passed the focused six-view visual review.
Both shells have0 exposed top crossings,44 underside and2 inner-edge pairs; broad
supports retain59left/61right seating pairs. These are scoped observations, not an
all-body clearance result. See
`artifacts/equipment-workshop/external-workflows-v1/RESULTS.md` and its bound review
receipt. Native completion must always come from the receipt, not from commands
printed in a recipe.

### Package an external adaptation without false authorship

The existing shared assembler hardcodes original-work attribution. It was left
unchanged because earlier receipts bind it. For the credited teaching fixture use:

```powershell
& $taskPython tools/equipment-workshop/package_external_shoulder_tutorial.py <NEW-credited-config.json>
dotnet $taskHelper --package <NEW-credited-full-study-input.json>
```

Copy the **structure**, then set new output paths, of
`artifacts/equipment-workshop/external-workflows-v1/credited-package-config.json`.
It requires explicit author/design notes, local-control attribution, external
credit/license/source URL, original provenance hash, base ZIP hash, and both new
GLB/skin hashes. The wrapper preserves37 GloamV15 control members byte-for-byte,
replaces only the shoulders/skin, removes incompatible old fits, and writes a
new manifest/provenance. The controls are neither newly authored external art nor
relicensed CC0. This is a mixed teaching fixture, not a new accepted raid set.

The tested full-study schema is:

```json
{
  "sourceDataPath":"C:/Users/nico/source/repos/MSUIClient/GameData/Data",
  "outputDirectory":"C:/Users/nico/source/repos/MangosSuperUI/artifacts/equipment-workshop/armor-redesign/prototypes/NEW-external-teaching-v1",
  "packages":[{
    "key":"gloamwing",
    "zip":"C:/work/NEW-credited-package/armor.zip",
    "sha256":"REPLACE_WITH_EXACT_NEW_PACKAGE_SHA256"
  }]
}
```

This helper accepts only the four established study keys. Here `gloamwing` is the
fixture adapter key for retained local controls; the manifest name is explicitly
`External Wood Cap Teaching Package`. Do not mistake it for registered Gloam or
rerun registration. The retained credited package compiled8 review displays and
41 native members, with no production IDs allocated. Its whole-set native/body
matrix remains untested. Use the real hash, not the placeholder string above.

## 8. Expand from one teaching example to a real equipment set

The focused examples are not a shortcut around the full pipeline. Build exactly
eight final pieces: helm, shoulder, chest **or** robe, legs, gloves, boots, bracers,
belt; sixteen explicit helm variants, two default shoulders and any declared fits;
eight64px icons; opaque attachment skins; correctly owned regional body PNGs.
Use meaningful author/design notes and preserve third-party credit/provenance.
No unreferenced ZIP payloads. `assemble_armor_v2_package.py` consumes a JSON config
and refuses an existing output directory; use it for original-work packages only,
or the explicit credited wrapper above for this external teaching fixture. Inspect
a proven complete config such as
the accepted GloamV15 package's `package-source-provenance.json` and its referenced
configuration before creating your own. Do not relabel external work as original.

```powershell
& $taskPython tools/equipment-workshop/assemble_armor_v2_package.py <NEW-assembly-config.json>
dotnet $taskHelper --package <NEW-full-study-input.json>
```

The full-study input is a different schema from the focused input; read
`tools/equipment-workshop/ArmorPrototype/FullArmorStudy.cs` and a retained complete
study input rather than pasting the single-body JSON into `--package`.
Check all16race/sex bodies, all relevant stand/walk/run/attack/cast samples and
alternate skin/hair contexts. Examine both shoulder sides, ears/horns/hair,
underarms/forearms and the entire submitted body. A direct-head weight subset is
not the entire body and is not all descendant head bones. Report contact location,
surface role and visibility; counts alone do not prove a visible defect or safety.
Verify helm eyes from actual wearer views. Check component joins and external
support-to-body seating separately. Keep full-frame and crop evidence tied to
source hashes. Test removing overlapping painted pieces as well as the full set.

Only accepted full packages proceed through the established guarded
`register_accepted_armor.py` Stage→BuiltReport preflight→one Build workflow.
Read `tools/equipment-workshop/REGISTER_ACCEPTED_ARMOR.md`; an existing BuiltReport
means preserve/reconcile, not Build again. Registration, unified patch rebuild,
installed member audit, server DBC/restart, equipped local/remote views, tooltip/
icon, combat/movement, lighting and persistence are separate stages. Coordinate
any work on the shared runtime. The four original sets have completed their
recorded gates; these external
exercises must not register, allocate IDs, rename weapons, install patches, or
change the database.

**Installation checkpoint, 2026-09-29.** Windows MSUIClient's Data directory and
SuperUI's configured Linux client-data directory are separate deliveries. Verify
the accepted patch SHA in both, then query the app's actual MPQ mount.
`verify_superui_mpq_mount_v2.py` records the completed e607 example: 268 exact
first-supplier hits, expected decompressed sizes and the accepted SHA of the
actual opened archive snapshot. Use a newly bound plan/version for new assets;
this fixed historical proof is not a generic approval. Server ItemSet status now
compares to the current unified MPQ member, not the old armor-only canonical DBC.
Require comparison-known/match and startup-known/no-restart; unknown is not
success. See the current handoff and `EQUIPMENT_AGENT_WORKFLOW.md` for receipts.

## 9. Stop conditions a smaller model must obey

Stop the dependent stage and report exact files/evidence when:

* Source license/author/download provenance is unclear, or original bytes changed.
* Budget, material, UV, topology or native roundtrip fails. Preserve the error;
  repair a duplicate source, not the validator.
* You cannot identify the original attachment matrix or body. Do not guess bones,
  use whole-body scaling, move the original skeleton or add runtime offsets.
* A region badge appears on the wrong surface. Inspect original UVs and repaint;
  do not change the atlas rules or fabricate a screenshot correction.
* A compiled member loads from a different MPQ or has a different hash; an earlier
  bad observation cannot be overwritten by a later good one in an aggregate map.
* A frame/probe is missing, source-to-submitted geometry differs, inputs changed
  during the run, or a capture process did not close cleanly.
* A support floats, the hero surface visibly cuts skin/ear/hair, a shell loses its
  back, or source topology folds. Make a local authored repair and recapture.
* Only one body/pose has been tested. State that scope; do not certify all-body fit.
* Registration already exists, another agent owns the native/runtime slot, or an
  automatic approval review rejected an action. Preserve evidence and coordinate.

End each stage with a short record: input hashes, tools/versions, exact commands,
output hashes, counts, checks passed, findings, accepted scope and remaining gates.
A useful result can say "conversion passed; art failed". A finished gear claim
needs the later visual, full-matrix, installed and live evidence too.

The fixture's refusal checks are runnable without launching a client or mutating
assets. Always use a new output directory:

```powershell
& $taskPython tools/equipment-workshop/test_external_workshop_refusals.py --fixture artifacts/equipment-workshop/external-workflows-v1 --output artifacts/equipment-workshop/NEW-external-refusal-checks
```

The retained run passed all six cases: absent author/license, changed base ZIP or
diffuse provenance, wrong client DLL, and wrong bound audit input.

## Source authority map

Verify rules against current source before changing their implementation:

* `MangosSuperUI/Services/ArmorForge/AuthoredArmorAssetValidator.cs`: armor budget,
  mesh/material/PNG/package restrictions; `AuthoredArmorCompiler.cs`: conversion
  and decoded-native equality.
* `MangosSuperUI/Services/WeaponForge/CoordinateContract.cs`: units, handedness,
  axis mapping, UV origin; `WeaponGeometryRevision.cs`: that revision lane's budget.
* `ArmorTypeCatalog.cs` and `LegacyArmorImporter.ComponentRegion`: allowed painted
  slots and fixed body regions. `ArmorPrototype/Program.cs` contains the tested
  full-atlas crop rectangles and focused helper schema.
* Client `MSUIClient/World/Units/AttachedItemRenderer.cs`: IDs0/1/2/5/6/11,
  `FindAttachment`, original bone/matrix multiplication and actual material draws.
* `docs/SHOULDER_FITS_EXTENSION.md` and the native selector: declared authored fits
  and exact hash/skin binding, not runtime geometry transforms.
* Client `shared_docs/EQUIPMENT_CAPTURE.md`: production renderer evidence and its
  offline/live distinction. Historical validators remain unchanged by this work.

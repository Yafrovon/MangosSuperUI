# Armor attachment fit measurements (2026-09-28)

`tools/equipment-workshop/analyze_armor_fit.py` measures attachment placement and
bounding-envelope proxies from native `captures.json`. It does **not** measure
wearer contact, penetration, clearance, or face occlusion. It never sets
`wearerFitAccepted` or `runtimeVerified` true.

## Current capture contract and measurements

The inspected native frames include attachment model paths, local bounds, attachment
IDs/bones, resolved world matrices, the body matrix, animation/time, camera, race/sex,
active geosets, image SHA256, and a whole-character silhouette summary. They do not
include a bone palette, posed body triangles, head bounds, per-piece object masks, or
per-surface depth. The whole-character silhouette cannot isolate a helm from a head.

The analyzer pairs attachment 11 (helm), 6 (left shoulder), and 5 (right shoulder)
with explicitly selected original equipment on the same race/sex. The supplied
`armor-fit-reference-map.json` records the actual authoring envelopes: Valor helms;
Valor, Beaststalker, Shadowcraft, and Magister shoulders for the four respective sets.
It does not fall back to a Human reference when a race is missing.

Measurements are finite affine-matrix validity, determinant, scale/orthogonality,
local extent ratios, local center offsets normalized by original per-axis extents
and diagonal, and world AABBs transformed from all eight corners. Stock envelopes
are also reposed with the candidate matrix for contextual comparison. Positive
uniform scaling is allowed; singular matrices, reflections, shear, and nonuniform
scaling are separately reported.

Exact attachment-anchor and matrix deltas require identical race, sex, body model
path, animation/time, sheath state, and body matrix. Matching a model path alone
does not authenticate body bytes. Envelope review defaults (0.5–2.0 extent ratio,
center offset over 0.5 original extent) are engineering triage thresholds, not
historical art budgets. Deliberate padding, crests, and asymmetry may exceed them.

```powershell
python tools/equipment-workshop/analyze_armor_fit.py `
  scratch/equipment-batch/custom-armor/quick-hum/captures.json `
  --reference scratch/equipment-batch/reference-sets/all-body-fit/captures.json `
  --reference-map tools/equipment-workshop/armor-fit-reference-map.json `
  --out artifacts/equipment-workshop/fit-analysis/custom-armor-quick-hum.json
```

Reports bind their input files and map by SHA256 and retain candidate/reference
image paths and hashes. Original raw bounds are separately recorded in
`scratch/equipment-batch/reference-sets/fit-bounds.json`.

## Checks executed

Nine focused unit tests pass: row-vector translation, rotated corner bounds,
identical envelopes, positive uniform scale, shear/reflection/singularity,
changed anchor, changed pose, missing race reference, and invalid numbers/bounds.
A stock Valor self-comparison covers 16 bodies and 48 attachment samples with no
findings. This verifies a baseline calculation, not the correctness of stock rendering.

The first custom quick capture set covers four sets, Human male/female, Orc male,
and Tauren male, each in front/back/three-quarter views: 48 frames and 144 attachment
samples. All 144 have valid rigid transforms and no size/center review flags. Every
sample explicitly reports that exact placement comparison is unavailable: custom
captures use sheath state 0, while the available original capture uses sheath state 1.
Do not drop this condition to manufacture a pass. Capture the originals under the
same configuration to complete the comparison. These four bodies and one pose do
not establish fit for the other bodies or animations.

The combined focused C# suite also passes 49/49: evidence 17, original references
10, authored armor packages 17, and mesh audit 5. Those tests establish their
respective code contracts; none is visual approval.

An independent six-frame visual spot check covered Greywatch plate on Human male
and Orc male in three-quarter view, Tauren male front/back, and Redfen mail on Human
female front and Tauren male three-quarter. The face openings remain readable and
no detached or inside-out helm/shoulder is apparent in those views. The large
Tauren/Orc shoulder masses crowd the helm silhouette and deserve movement review;
an AABB envelope cannot determine whether their underside contacts the body.
Both Tauren examples visibly paint the hoof with boot texture. That is recorded
in `EQUIPMENT_BARE_FEET_RESEARCH.md` as a baseline rendering issue requiring a
race-foot rule investigation, not accepted because an original set does it too.
This spot check is supplemental to the armor agent's broader review.

## Required next contract for actual contact or occlusion checks

Two useful extensions can be captured without guessing a head from equipment bounds:

* For image-space coverage, emit semantic object IDs and linear depth for body-only,
  equipment-only, and combined renders at the same camera and pose. Preserve active
  geosets, alpha test, and culling. Separate protected face regions from hair and
  permitted covered scalp; a closed helm needs a different declared visibility goal
  than an open visor. Measure a protected-region occluded-pixel fraction using depth,
  and compare controlled front/side/back frames with an original under the same pose.
* For surface contact, emit hashed posed vertices and active triangle indices for the
  relevant body and armor, or base meshes plus the exact skinning matrices and weights.
  Use BVH triangle intersections and nearest-surface clearance with explicit permitted
  contact bands. A signed inside/outside test is invalid when the body surface is open;
  AABB overlap and shared screen pixels alone do not establish penetration.

These measurements still need controlled visual contact review. Inspect suspected
intersections in the matching original and custom pose, then orbit and sample movement.
A stock baseline can contain a renderer bug—as the Tauren boot-foot texture investigation
demonstrates—so agreement with it is evidence of parity, not proof of intended behavior.
The full 16-body matrix and sampled movement improve coverage; they do not prove all
hair styles, all continuous animation times, or live-world correctness.

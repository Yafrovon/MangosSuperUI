# Equipment matrix visual review — 2026-09-28

All 1,568 supplied armor frames were inspected through 64 labeled contact sheets,
followed by eight full-resolution armor frames selected for contact, seam, and robe
questions. This is a bounded offline visual review, not an automatic art approval
or live-world result. The independent posed-triangle investigation is separate.

## Coverage and reproducibility

| Set | Static | Motion | Total |
|---|---:|---:|---:|
| Greywatch plate | 192 | 160 | 352 |
| Redfen mail | 192 | 160 | 352 |
| Briarpath leather | 192 | 224 | 416 |
| Archive cloth | 192 | 256 | 448 |
| Total | 768 | 800 | 1,568 |

Each run covers all eight vanilla races and both sexes. Static frames use six
cameras and two sheath states. Motion frames use front/back three-quarter cameras,
walk/run and the listed attack/cast/wand samples in each manifest. All eight native
summaries report completion and zero technical-error cases. The review inventory
independently checked the manifest Cartesian products: zero missing, unexpected,
or duplicate cases; every PNG SHA256 matches its native capture record.

Sources: `scratch/equipment-batch/custom-armor/{set}-{static,motion}`. Each frame's
exact key, absolute source path, SHA256, crop, and sheet position is recorded in
`artifacts/equipment-workshop/armor-visual-review/inventory.json`. Run
`tools/equipment-workshop/build_armor_review_sheets.py` to reproduce the sheets.
Thumbnails use recorded subject bounds plus ten pixels and preserve the whole
subject. Their independently fitted sizes must not be used for scale comparisons.

## Findings by set

**Greywatch.** The cool plate panels, dark underlayers, and narrow gold edges remain
consistent from chest to arms and legs. Front/back chevrons and waist bands meet
without a conspicuous unmatched texture rectangle in the reviewed views. The
shoulder tops remain distinct from the upper-arm paint during walking and running.
The open-face helmet preserves facial visibility in neutral poses, including the
wide Gnome head and long Troll face; long ears remain visible. The rigid shoulder
caps move close to the jaw in some raised-arm attacks. The exact contact flags
below remain open to the separate geometric comparison.

**Redfen.** Bronze scales repeat downward across chest, upper arms, and thigh
panels; smaller scales on the shoulder skirts remain legible beside larger torso
scales. Dark green underlayers separate the pieces at the waist and inner limbs.
The side view on Orc male confirms a dense but continuous shoulder skirt and
arm transition, without a blank patch. The scale pattern stays attached to the
limbs through attack samples. Raised shoulder/head screen overlaps recur in the
same kinds of poses as Greywatch; their appearance alone is not a collision test.

**Briarpath.** The brown stitched panels, muted leaf shapes, and green underlayers
give the chest, gloves, knees, and boots a common treatment. The low shoulder caps
remain visually smaller than the mail/plate caps. Walk/run frames preserve the
separation of knee panels and lower-leg paint; attack/cast frames do not show a
detached sleeve or an exposed rectangle from a missing body texture. The sheathed
staff clears the main helmet silhouette, but one Night Elf female side projection
crosses the long ear and is explicitly flagged below. Muted colors retain less
small-detail contrast than Greywatch/Redfen at these thumbnail sizes; the leather
panels still read as broader shapes.

**Archive.** Gold stars, piping, and dark blue folds continue from the shoulder
emblems into the chest, sleeves, and robe. Static rear and side views show a
continuous robe hem. Walk/run frames bend the robe sharply at the knees as the
existing leg-weighted body geoset moves; the cloth stretches between the stepping
legs rather than behaving like simulated fabric. Full-resolution Troll male and
Night Elf female run samples show continuous textured panels without exposed
skin cutting through their middle. The Undead male cast sample spreads the hem
with the wide stance while preserving its border and stars. These findings concern
the supplied native samples, not a claim that every possible animation is free of
robe self-intersection. Raised-arm casts/wand poses can hide the face behind a
shoulder in projection and retain the same contact-review limitation.

Across the four sets, Tauren hooves and Troll toes are visible below the footwear
or hem; shin textures remain. Undead bone openings remain part of the wearer
silhouette. No magenta fallback surface, detached armor piece, inverted full
attachment, or grossly stretched isolated triangle was observed in these sheets.
Fine subpixel seams, hidden intersections, other appearances/hairstyles, and
motion between captured samples are not ruled out by this review.

## Exact full-resolution checks and open flags

Frame keys identify the PNG in the corresponding `{set}-{lane}` source directory.

| Frame | Observation / disposition |
|---|---|
| `greywatch-race7-sex0-attack-mid-sheath1-front-three-quarter` | Shoulder cap covers the lower cheek/jaw in projection. Open contact flag; sent for paired posed-triangle comparison with stock Valor. |
| `greywatch-race7-sex1-attack-early-sheath1-front-three-quarter` | Same shoulder-to-jaw crowding during the raised arm. Open contact flag; stock comparison requested. |
| `greywatch-race2-sex0-attack-mid-sheath1-front-three-quarter` | Raised forearm and shoulder obscure the face; cap's underside remains visible. Open contact/occlusion distinction; stock comparison requested. |
| `redfen-race2-sex0-stand-mid-sheath0-right` | Scale skirt, sleeve, belt and lower-leg textures remain continuous in this edge view. No additional defect identified. |
| `briarpath-race4-sex1-stand-mid-sheath0-left` | Sheathed staff projects through the ear near image x350/y222. Open weapon/ear contact flag; same-slot stock staff comparison requested. |
| `archive-race8-sex0-run-mid-sheath2-back-three-quarter` | Trailing robe panel follows the bent leg; no visible skin through the panel. Sharp folds are present. |
| `archive-race4-sex1-run-mid-sheath2-front-three-quarter` | Stretched cloth across the stride remains continuous; shoe visible at the hem. |
| `archive-race5-sex0-cast-mid-sheath2-front-three-quarter` | Wide stance spreads the robe; border and star field remain coherent. |

The open flags are observations, not confirmed penetrations. They must not be
silently converted into a visual pass because source budgets, screenshot counts,
or capture error counters passed.

## Corrected wand animation comparison

Reviewed all 24 frames from
`scratch/equipment-batch/custom-weapons/wand-correct-animation-comparison`:
custom Archive wand versus stock Alchemist's Wand (display 6081), Human male and
Tauren male, hold animation 111 at 0.25 seconds and attack animation 107 at 0.2
seconds, front/back/front-three-quarter. Four paired sheets and twelve comparison
records are saved in `artifacts/equipment-workshop/wand-correct-animation-review`.
Run `tools/equipment-workshop/review_wand_comparison.py` to reproduce them.

All twelve pairs have identical recorded camera, wearer transform/model, pose,
lighting, attachment ID, attachment bone, and attachment world transform. Every
PNG hash was checked. The custom handle visibly occupies the same palm/finger grip
as the stock wand; no custom-only floating gap is visible in these corrected
samples. Human male hold three-quarter and Tauren male hold front were also opened
at full resolution for both models. The larger forked custom head extends farther
than the thin stock wand, as designed, while its grip remains at the shared hand
attachment. This resolves the earlier floating concern for these exact samples;
it does not establish correct grip in unrelated casting animations or certify
live server-controlled animation selection.

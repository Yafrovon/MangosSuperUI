# Live equipment visual review — 2026-09-28

This log records actual in-world screenshots separately from offline turntables and posed-mesh checks. A location label, a successful protocol or a screenshot count is not an art verdict. Review only the submitted frames and retain their exact identities.

## Scene selection: seven captured views

Source: `artifacts/equipment-workshop/live-scene-probe/`. All seven source PNGs decoded successfully with Pillow. The two outdoor PNGs needed JPEG display derivatives because the image-view tool rejected their PNG data; the originals remain untouched. Five interior PNGs and both outdoor derivatives were visually inspected.

Every frame depicts Forgeplateqa (Human male, level 60, GUID `0x00000000000000A9`) in the Greywatch Harness, with the Greywatch Arming Sword sheathed. The paired gameplay JSON records the eight armor display IDs 76341–76348 and weapon display ID 76331. The visible Recruit's Shirt is a separate stock slot. These are framing checks of this actor and equipment state, not all-body or combat coverage.

| Exact frame stem (all begin `gameplay-eq-`) | Framing observation | Use |
| --- | --- | --- |
| room-a-225-20260928-074505-631 | Large foreground column covers most of left scene; the body remains visible in a narrow gap. | Avoid for primary evidence. |
| room-a-270-20260928-074507-433 | Full body visible in side view between columns. Helm/shoulder outline and both boots remain in frame. | Supporting side view. |
| room-a-315-20260928-074509-349 | Full back/side body visible; pillars constrain the scene but do not mask the wearer. | Supporting rear view. |
| room-b-225-20260928-074514-349 | Full front/side body visible: helm, both shoulder caps, torso, hands and both boots. Candle stand is left of the body; banner is behind it. No foreground geometry covers the wearer. | Selected interior framing. |
| room-b-315-20260928-074516-328 | Foreground blue banner covers the right portion of the scene and masks the wearer's right shoulder/arm outline. | Reject as complete-body evidence. |
| outdoor-a-45-20260928-074521-101 | Complete rear/side body visible, including sword at the hip and both boots. No foreground fence/tree covers the body. | Selected exterior rear view. |
| outdoor-a-225-20260928-074523-006 | Complete front/side body visible with face, shoulder caps, chest and boots readable. Ground foliage adds background detail but does not hide the armor. | Selected exterior front view. |

The selected interior's paired JSON gives map 0, feet `[-8908, -155, 81.94199]`, framebuffer 2560×1369, field of view 60.45229°, MSAA 2 and painterly disabled. This is an observed clear floor position for this capture. The older scene-selection JSON has no production interior light weight: room appearance alone cannot prove which light the body used. The new `lighting.interior` fields must establish that in the subsequent matrix.

The source-indexed machine log is `artifacts/equipment-workshop/live-scene-probe/review-log.json`; it includes original PNG and paired gameplay JSON SHA-256 values, coordinates, actor and equipment identity. It does not mark an asset approved.

## Framework for each subsequent world matrix

1. Inventory the expected cells before reviewing: authored item/set, race/sex, actor GUID, sheath/drawn state, pose/action, camera view and light condition. Record missing/duplicate cells explicitly. Keep GM setup separate from ordinary observed rendering/combat.
2. Pair each screenshot with its gameplay JSON and `live-equipment-observation` JSON using the protocol label and actor identity. Hash all three. Record actual model/display/entry IDs and observed renderer source; unavailable kit data is not a pass.
3. Record camera `Distance`, `EffectiveDistance`, `EyeTarget`, `achievedEyeTargetDistance`, FOV/aspect and collision state. Reject a supposedly matching comparison if achieved framing differs materially or a wall/banner masks the area under review.
4. Record time source/hour and `lighting.interior`: enable state, production uniform/weight, sample feet, fresh floor color, WMO room narrative and baked light scale. The fresh floor resolve is separate from the last renderer uniform. Wait for rendered frames and settling after movement; retain any disagreement. A null floor result can mean unavailable geometry, not necessarily outdoors.
5. Review each image for equipment identity and texture fallback, silhouette/attachment position, visible UV continuity, bare-foot race policy, helm/face and shoulder/arm contact, sheath/hand/weapon placement, and any robe/leg deformation visible in the pose. Note exact body part and frame; do not replace observations with “looks good.”
6. For a suspected contact or placement defect, examine the native-resolution image and a same-body/pose/camera stock baseline, then consult posed-mesh measurements where available. Projected overlap alone does not prove penetration. A passing geometry check does not prove appearance.
7. Record one of `observed-no-flag`, `flag-needs-comparison`, `confirmed-defect`, `obscured`, or `not-reviewed` for each requested area. State what remains untested. No aggregate score or imported boolean grants global runtime/art approval.

Later matrix reviews are pending. The completed offline 1,568-frame armor matrix and 24-frame wand comparison remain documented in `docs/EQUIPMENT_MATRIX_VISUAL_REVIEW.md`; they are not relabeled live evidence.

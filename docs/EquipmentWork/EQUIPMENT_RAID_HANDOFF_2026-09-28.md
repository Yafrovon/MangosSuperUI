# Custom Classic equipment — handoff at the owner's stop request

**Checkpoint: 2026-09-28, after the 19:02 UTC installed-weapon captures.**

## Volumetric art candidates, reviewed natively and NOT installed (2026-09-30)

> **Continue from `docs/EQUIPMENT_HANDOFF_2026-09-30.md`** (written at the owner's stop request): current
> status, everything learned, the from-scratch track (starter template, recipe table, the new-set blocker),
> prioritized next steps with acceptance tests, evidence hashes and open questions. The section below is
> the summary written when the candidates were sealed.

This pass responded to the owner's art brief: weapons felt like thick 2D objects, shoulders and helms had
quirks, and the goal was a pipeline that a smaller model can follow. **Everything below is a separately
versioned candidate.** The installed weapons, armor, items, registrations and grants are unchanged. Each
candidate was compiled by the production compilers into private overlays and captured natively on all 16
bodies. Promotion is a separate, owner-approved step.

- **How-to for any model:** `docs/EQUIPMENT_VOLUMETRIC_PIPELINE.md`. It covers:
  - the numbers measured on the originals: family sizes, budgets, texture value law, armor seats;
  - the coordinate contracts;
  - exact commands;
  - what each check proves;
  - a failure gallery (27 items when sealed; 29 after the 2026-09-30 handoff check).
- **Tools:** `tools/equipment-workshop/volumetric/`, which has its own README.
- **Weapons:** `artifacts/equipment-workshop/volumetric-v1/`, whose README gives the whole history and the
  missed defects.
  - Ten candidates `final-e`, staged as `stage-vol1-final-e` (receipt: overlay sha 7785513270306ea3...).
  - 640 native frames; seating comparable to a stock sword.
  - Same-renderer before/after sheets against the installed weapons.
  - Nine blades pass the cardboard test. Unfallen is a box head, where the test legitimately does not
    apply.
  - Found and fixed along the way:
    - painting was calibrated on lit renders, so pale blades rendered as white planes; the value law fixes it;
    - weapons were systematically undersized (blades a third of the original thickness);
    - atlases were half empty.
- **Armor:** `artifacts/equipment-workshop/armor-volumetric-v1/`, with README and `receipt-candidates.json`.
  - New helm sets (16 variants, one skin each) and mirrored shoulder pairs for all four sets.
  - Each is packaged from the accepted ZIP, with every other member byte-identical.
  - Each is compiled by `ArmorPrototype --package` and captured at 384 frames.
  - Accepted versions were captured the same way, from byte-identical copies, for before/after.
  - `overview-four-sets-HuM.png` and `overview-four-sets-TaM.png` summarize it.
- **Internet sources:** `artifacts/equipment-workshop/external-workflows-v2/`, with README and provenance
  for 7 licensed inputs. Three routes were demonstrated through native capture:
  - a direct import of a CC0 hand-painted sword;
  - a high-poly CC0 mace: naive decimation FAILED, and a rebuild from measurements worked;
  - a CC0 helm refitted to all 16 bodies.
- **Still unproven:**
  - the look, which is the owner's review of the sheets;
  - hairstyles other than appearance 0;
  - mounted, sitting and emote poses;
  - motion beyond one frame of stand, attack, cast and run;
  - the live world.
- **Known weaker area:** the new pad and helm paint is procedural and plainer than the accepted sets'
  hand-painted motifs.

## Hand grip v4 — installed and observed live, 2026-09-30

The owner's Debug session had already exited, so nothing was interrupted; the owner then pre-authorized booting/building/restarting MSUIClient, the web app and VMaNGOS for this project. The first native runs of the "final" v2 candidate found what 55,040 numerical checks could not: (1) the equipment mesh probe rejected every frame because the renderer now draws cloned mount snapshots (this would have broken all contact audits) — fixed in v3 via `Mount.SnapshotOrigin`; (2) TaurenMale spell/kneel/swim animations move the hand attachment ~0.3 off the palm, so a closed fist appeared beside the weapon. The original client stows weapons for AnimationData WeaponFlags 0x4/0x10 animations (benilla-verified sheath reconcile), so v4 adds `HandGripLaw.ForPresentedAnimation`: under those animations the palm keeps the animation's own fingers. MSUIClient still does not implement the stow itself (separate follow-up task flagged).

Installed 04:37 UTC: Release `162336f6e626d4cac4f8089ea88189c3e3ac24f695eab639ccfc939b71bd2a74`, Debug `c8d513de08314d5ad2acf158cfaf361fe261d800b438d0010e94565fe52dcb84`; rollback `C:\Users\nico\AppData\Local\Temp\msui-hand-grip-rollback-20260930T043736Z`. Evidence: 270 native frames per binary (HuM/HuF/TaM; stand/run/attack/cast; stock and custom weapons, shield, staff), personally reviewed boards, probe-based seating reports (only palm vertices move; weapons unmoved), 55,059 numerical checks per build, byte-identical installed smoke, and live self + remote observation of warrior169 with Silken Mercy (the remote stowed control failed to toggle and is missing). Seal: `artifacts/equipment-workshop/hand-grip-v1/build-checkpoint-v4/checkpoint.json` (SHA `b57a27c53faf158c73d867f6469eddee829bf8b981f47eb13418b8a7df45287a`); details in `hand-grip-v1/README.md`. Normal runtime is now DLL 1623 (Release) / c8d5 (Debug) + patch4 e607 + patch7 6a43. Finding for the art pass: The Last Lock's handle is too thick for the vanilla fist (12–21 buried hand vertices in old and new clients; stock weapons 0–1).

## New hand-grip follow-up — 2026-09-29, implemented and numerically verified; installation pending

After the equipment delivery below, the owner observed open hands around held weapons and authorized a client fix. MSUIClient now has an isolated candidate that applies the original body's `HandsClosed` animation15 to the selected finger bones after the base/action pose. Actual visible palm attachments determine right/left closure; shield wrists and stowed weapons do not independently close a palm. Grip and rendered equipment share the same sheath/mount snapshot, including frozen poses. Bodies, skeletons, geosets, atlas rules, attachment transforms and equipment assets are preserved. No completed weapon mutation, registration or item grant was repeated.

Both final Debug and Release builds succeed with zero errors and19 existing warnings. Each passes55,040 independent regression assertions plus33 census checks across all16 bodies and20 animation IDs, including attacks/wand poses. All2,880 unmodified pose samples match the old Release client bit-for-bit. The same source-animation oracle fails standing hand closure on all16 bodies in the old binary. Numerical proof: `artifacts/hand-pose-check-v1/verification-final-v1.json`, SHA `441d1828678c48c059c04a19cc457f874e3b860aacedfd92cd9e7d7e4a7cc32e`. This establishes the animation defect and candidate's numerical behavior; it does not establish visual handle seating or live transitions.

**Not installed or visually accepted yet.** Final isolated Release SHA `8f0588ed98c9663ddceb58627a65e16d20977f662479ec0b84c069ece3401af8`; Debug `c5fc997d4dad0a2718a40e140bc07d4b32f75b3f3cccf5d2e4e0ca660f23109f`. The normal Debug `d9c367ae0fdff3676fadcc85fdd2d2d2db848958d9bce7ba98e49af863f8dc2e`, normal Release cea1 below, patch4 e607 and patch7 6a43 remain unchanged. A user-owned Debug session was open as PID61396; an explicit question about closing it is pending. Do not treat silence as approval. No user process was closed, and no new server restart/deployment occurred. The earlier scoped server restart authorization remains valid; this new fix needs no server change.

Sealed implementation checkpoint: `artifacts/equipment-workshop/hand-grip-v1/build-checkpoint-v1/checkpoint.json`, SHA `6d8df147250a7f7a98d5c81c33d6243e35d5712326e570985c6f5b940b91351a`. It binds final runtime source, binaries, build logs, independent checks and unchanged normal assets. `hand-grip-v1/README.md` documents the hash-guarded native runner and prepared16-frame stock/custom smoke plus254 extension frames. No native frame has been captured for this fix. Next: recheck the actual process state, resolve the pending close-session question, capture/review the baseline and final candidate, then install with rollback and verify the installed result. Keep the earlier accepted equipment scopes as historical evidence; they missed this hand-closure defect and are not proof of its correction. Commander raid remains paused.

## Current completion — 2026-09-29 02:35 UTC, deployed and verified

The agent-operated pipeline and recorded scopes for all ten weapons and four eight-piece armor sets are complete. The four-set evidence page is deployed at `/ArmorForge`; all 1,161 public overlay members were fetched and byte-verified, including 768 current and 384 retained previous images. Actual browser review confirmed the current/previous collection selector and corrected positive status badges. Final deployment record: `artifacts/equipment-workshop/evidence-ui-four-v1/deployment-final-v2/receipt.json`, SHA `8f7ba3cd338b6f9986cb47869af775f1fc56182aba0ac1da19db554a80c18409`. HTTP proof: `http-postdeploy-v2/receipt.json` under the same evidence root, SHA `aeb36e2debf84363a498ac8159d5c0527cb1c4144325dac5ca4db83cc8cda329`.

**The two client-data installations now agree.** Windows MSUIClient and SuperUI's configured Linux `/home/wowvmangos/wowclient/Data/patch-4.MPQ` both contain accepted patch `e607f73988bf0454c2f16d44d0fb326bece788be0a6ec5280211a20f0d41967a`. Linux synchronization completed without rebuilding at 02:33:03.287755 UTC. Its former `a74fdea81fb136afe262ea5f4ff87190d09a87cf4be90cdea8333b59bd9db8c4` archive is preserved at `/home/wowvmangos/deploy-backups/equipment-server-data-20260929-0220/patch-4.MPQ`. The 23 changed old members are the ten already accepted weapon M2/skin pairs, ItemDisplayInfo, ItemSet and listfile; 246 members were added, none removed. No weapon mutation was repeated.

SuperUI process **83845**, active since **02:33:35 UTC**, runs web DLL `a748f8d79c33792288da2ff28e498a41f7bd3dc678f646c017bc65c8d65edf10`; rollback is `/home/wowvmangos/deploy/backups/auto-20260928T223303`. This is separate from the unchanged Windows MSUIClient DLL `cea1cbe1cc2b96e0199f223d59a9087ce8f9f62785b801dc980ae4f32a38767f` and patch-7 `6a43fc6c5a2d214cd9d64110ae4ea5e319983cad6683acf80c46fecf4b9b1da4`. Core **80737** remains running from **01:00:41.5160575 UTC**; no extra Core restart accompanied this correction.

Server ItemSet now compares to the **current unified MPQ member**, not the obsolete September 5 armor-only canonical file. Actual server and expected member both hash `fbc3cd3d7925be4cee1450daf328aa91ed7da7d4b0d926505e8b7bfa6f362809`. `deployment-final-v2/positive-status-verification.json` requires explicit comparison-known, match and startup-known states; both comparisons match and no restart is required. False stale alone is not a known comparison.

Post-restart SuperUI mount proof covers **268 exact members including both DBCs**. Every HTTP hash-table probe resolved first from the actual opened `/tmp/superui-mpq-live/patch-4.MPQ.1.tmpmpq` with its expected decompressed size; opened snapshot and configured source hash e607 before/after. Exact member identities are bound through the accepted complete archive audit. Receipt: `artifacts/equipment-workshop/armor-redesign/raid-v3/four-set-installation-v1/superui-mount-verification-v2/run-v1/verification.json`, SHA `884ac66b4c8aee526695a46ff9f27239742fdc455c2f936c9d81b52646835bb1`.

Per set, acceptance remains 1,152 reviewed offline views, 96 current installed standing views and 72 scoped live frames. Glass/Gloam live keeps its historical63e5/235c runtime; currente607 standing compatibility does not relabel those runs. Plate/mail live uses currente607; ten weapons retain their completed180 live frames. Contact/compact-seat, occluded-paint and sampled-motion limits remain disclosed. Authored fits preserve body/skeleton/attachment transforms and Classic/TBC geoset/atlas rules. The two Dragons of Nightmare startup messages remain separate unresolved world-event diagnostics. **No pending approval, registration, grant, weapon mutation or native capture is required for these completed scopes.** Preserve all older checkpoints below and coordinate any new shared-runtime work.

## Latest execution state — 2026-09-29 02:06 UTC, all four live scopes accepted

All four finalized eight-piece armor sets now have accepted offline, installed-standing and documented live scopes. Each set has 1,152 reviewed offline views across 16 bodies, 96 current normal-mount standing views and 72 personally reviewed live frames. The live scopes remain character- and runtime-specific: Glass is HuF on its retained63e5 run, Gloam is HuM on235c, and both plate/mail are HuM on currente607. Current standing pixel compatibility preserves the historical Glass/Gloam live evidence; it does not relabel it as fresh e607 gameplay. The ten weapons' completed180-frame live scope is unchanged.

The final plate/mail ledgers are under `artifacts/equipment-workshop/live-evidence/current-armor-v1/`:

- `broken-gate-live-acceptance-v1/acceptance.json`, SHA `670013251c1b0e4ab7d7501f190ee51a16363c46d0b815fa28b2e9fdae99f6da` — 72 unique frames, 1,034 bound files and all40 physical wearer instances preserved.
- `leviathan-live-acceptance-v1/acceptance.json`, SHA `249b8e4f0c801b5d1d8c8b4f47bd238e9298f382782fdfc4df2723a2218afd88` — 72 unique frames, 1,035 bound files and all48 physical wearer instances preserved, including the prior plate/Gloam/ordinary items.

Both compose12 self/remote world views,8 real inventory hovers,2 received same-target melee samples,48 ordinary per-piece removal/restoration views and2 independent relog views. All19 saved slots match; exact temporary targets17379390967040632843 and17379390967040632909 were removed. Observer170's18 physical items and separate historical phantom2654 remain unchanged. The final wearer snapshot is `scratch/equipment-batch/live-equipment/current-armor-v1/warrior-after-leviathan-final-relog-v1/retrieval.json`, SHA `47dfb0d273b5954e3d3835cc47c3440c8d8d24eaaffb4db892aaf6a5085f70ca`. **No further registration, item grant or completed weapon mutation is needed.** Every native/live process has closed; root coordinates any new launch.

The reusable agent workflow is documented in `docs/EQUIPMENT_LOCAL_MODEL_COOKBOOK.md`, `docs/SHOULDER_FITS_EXTENSION.md` and `tools/equipment-workshop/PLATE_MAIL_LIVE_ACCEPTANCE.md`. The final sealer has70 independent boundary tests plus65 actual-schema/parity checks. A source-bound four-set delivery image is `artifacts/equipment-workshop/four-set-delivery-preview-v1/four-sets-installed-standing.jpg`; its ordinary crop/scale framing is explicitly not a world-size comparison.

Normal runtime remains cea1/e607/6a43, Core80737 from01:00:41.5160575UTC. The two disclosed Dragons of Nightmare startup messages remain unresolved and separate from equipment acceptance. Source/contact residuals, compact hollow seats, occluded paint and sampled-motion limitations remain explicit; no zero-contact or all-body-live claim is made. Final evidence-page publication is being prepared; it is **not yet a deployed-browser proof** at this checkpoint. Root will append the deployment result. All earlier failures, pending checkpoints and unrelated work remain preserved below.

## Latest execution state — 2026-09-29, Broken Gate live acceptance

Broken Gate set5198 now completes its documented live scope on the unchanged cea1/e607/6a43 runtime and Core80737 epoch. `artifacts/equipment-workshop/live-evidence/current-armor-v1/broken-gate-live-acceptance-v1/acceptance.json` (SHA `670013251c1b0e4ab7d7501f190ee51a16363c46d0b815fa28b2e9fdae99f6da`) joins72 unique personally reviewed frames across eight runs:12 self/remote world,8 UI hovers,2 received same-target melee samples,48 removal/restoration and2 independent final-login views. The exact target was removed, all19 saved slots match, and all40 physical wearer instances (32 earlier plus eight granted plate copies) retain GUID/item/owner/count. Observer170's18 physical instances and the separate historical phantom2654 reference remain preserved. The ledger checks1,034 bound files; independent sealer tests pass70 boundary cases and65 actual-schema/parity checks. Live body scope is HuM warrior169, not all16 bodies; the prior1,152-view offline and96-view installed acceptance, disclosed finite contacts, compact seats and visibility limits remain separate.

Leviathan5199 was granted **once**, adding entries1102554–1102561 with GUIDs2617–2624 while preserving those40 earlier instances. The current48-instance after-grant snapshot is `scratch/equipment-batch/live-equipment/current-armor-v1/warrior-after-leviathan-grant-v1/retrieval.json` (SHA `bb83b8e3151959a45c5c1723b8d2f8bf50774fabc96eafbdd78b27b966bd457b`). Its four self-day views already pass technical and personal review, including an interior and actual motion sample. Remaining UI/night/remote/combat/removal/relog acceptance is still pending; root owns the live slot. Do not repeat any armor grants. The startup's two disclosed Dragons of Nightmare messages and all earlier evidence remain unchanged.

## Latest execution state — 2026-09-29, four-set installation checkpoint

All four finalized eight-piece sets are registered and installed. Previous pending/restart statements below are preserved historical checkpoints; the owner subsequently authorized installation and VMaNGOS/SuperUI restarts when needed.

| Set | Set ID | Item entries | Display IDs | Current evidence scope |
| --- | --- | --- | --- | --- |
| Glass Comet T | 5196 | 1102530–1102537 | 76373–76380 | Historical HuF live72; current installed standing96 matches accepted pixels |
| Gloamwing V15 | 5197 | 1102538–1102545 | 76381–76388 | Historical HuM live72; current installed standing96 matches accepted pixels |
| Broken Gate fitted V2 | 5198 | 1102546–1102553 | 76389–76396 | Offline1,152 personally reviewed; installed standing96 accepted; live pending |
| Leviathan's Ransom fitted V2 | 5199 | 1102554–1102561 | 76397–76404 | Offline1,152 personally reviewed; installed standing96 accepted; live pending |

- **Current runtime:** patch-4 `e607f73988bf0454c2f16d44d0fb326bece788be0a6ec5280211a20f0d41967a`, installed **01:00:00.2038610 UTC**; normal Release DLL remains `cea1cbe1cc2b96e0199f223d59a9087ce8f9f62785b801dc980ae4f32a38767f`; patch-7 remains `6a43fc6c5a2d214cd9d64110ae4ea5e319983cad6683acf80c46fecf4b9b1da4`. The combined patch adds138 plate/mail members, preserves13,280 earlier members and all36,937 prior display/241 prior set rows, and changes only the listfile plus ItemDisplayInfo/ItemSet DBCs. All268 requested installed mount members resolve with exact hashes/suppliers. Evidence root: `artifacts/equipment-workshop/armor-redesign/raid-v3/four-set-installation-v1/` (`patch-verification.json`, `installation.json`, `installed-mount-verification.json`, and per-set `installed-members.json`).
- **Current Core:** server ItemSet SHA `fbc3cd3d7925be4cee1450daf328aa91ed7da7d4b0d926505e8b7bfa6f362809` installed01:00:32.779631UTC, adding only5198/5199 to the prior rows. Authorized VMaNGOS restart succeeded; PID**80737** started **01:00:41.5160575UTC**, RA connected and World initialized. Running `/proc/80737/exe` retains corrected allocator SHA `73a515be443340089bffc0c54530061cfad4e177c099f6c658ccb3f7c912a261`; realmd1802 is unchanged. Do not reuse targets from before this restart.
- **Startup is not error-free.** Distinct ERROR messages increased668→670: the two new messages are `GameEventMgr: [Dragons of Nightmare] instance 0 of map 0 not found!` and the same for map1. The retained `HardcodedEvents.cpp252–272` source logs and continues when FindMap returns null. No new SQL, duplicate-entry, assertion or ItemSet-failure message appears in this delta. No world-event repair was attempted. Exact logs/source/run hashes and the successful post-restart warrior169 baseline are bound by `four-set-installation-v1/server-health-verification.json` (SHA `e7a70b7080073674b52888bd22abd2a7ca3f8eb56609853e6c2080eefeaf54dd`).
- **Standing acceptance is current; live scopes remain separate.** Each set's96 normal-mount views covers all16 bodies; all96 color and96 contour PNGs per set match already accepted controls byte-for-byte. Plate/mail additionally bind all62/60 character runtime members and complete submitted geometry/body/geoset/attachment/material/fit state. Their receipts are `{broken-gate,leviathan}/installed-native-audit-v1/standing-acceptance.json`; Glass/Gloam use their per-set `standing-acceptance.json`. Glass/Gloam historical72-frame live receipts remain unchanged and are not relabeled as new e607 gameplay. Plate/mail live UI, self/remote/day/night/interior/combat, removal/restoration and save/relog are still pending here. Root owns the single native/live slot and current QA grants; inspect fresh state and never repeat completed grants blindly.

The exact accepted plate/mail source ZIPs and scoped contact limitations remain under `artifacts/shoulder-fits-pipeline-v1/fitted-pair-v2-offline-acceptance/`. Authored shoulder selection preserves normal body/skeleton/attachment transforms and the existing painted atlas/geoset rules. No completed weapon mutation was repeated; all older failures/evidence and unrelated work remain intact.

## Latest execution state — 2026-09-29, after 00:47 UTC (local September 28)

- **Gloam V15 now completes its documented live scope.** `artifacts/equipment-workshop/live-evidence/current-armor-v1/gloam-live-acceptance-v1/acceptance.json` (SHA `d414efd86506f6cb5b411a847cc581fc2d677f4b2fe670e05e0b11c630acc853`) joins72 unique personally reviewed PNG/observation/gameplay triples:12 self/remote world views,8 UI hovers,2 received melee samples,48 removal/restoration views and2 independent final-login views. Live wearer is Human male169, actual remote observer170. All32 real item instances remain with exact GUID/item/owner/count, all19 saved equipped slots match, and no orphan was introduced. Bracer paint is hidden by the gloves in the removal angles; no visible pixel difference is claimed for that piece. Offline1,152/installed96/live72 remain separate scopes with the earlier disclosed contacts retained.
- **No further Gloam grant is needed.** Entries1102538–1102545 retain GUIDs2566,2568,2569,2570,2571,2572,2573,2583; bag4500/GUID2565 stays equipped. Old Redfen helm2638 was moved by ordinary inventory gesture into that bag's slot5 to reserve backpack slot13 for removal. Current complete snapshot is `scratch/equipment-batch/live-equipment/current-armor-v1/warrior-after-gloam-final-relog-v1/retrieval.json` (SHA `f8d290528de02d603daee7ed821e96ead38dcace0daa8db09a6746234a67367c`). The combat target17379390967040632890 was removed; never reuse it.
- **Two audit improvements preserve all previous evidence.** `append_armor_combat_phase.ps1` supports both QA actors, and separate `armor_adaptive_execution_v2.py` recognizes the exact actual melee success message `attack start gate=PASS` while delegating the frozen verifier's other checks; six proof cases pass. New `prepare_armor_ui_review_v2.py` checks exact audited observation hashes and run identity;25 tampering checks pass. Both retained Glass/Gloam UI runs revalidate with all source PNG/derived JPEG identities unchanged. Supplemental receipts are `glass-ui-audit-v2-binding/corrective-binding.json` and `gloam-ui-audit-v2-binding/corrective-binding.json` under current-armor-v1. Original helpers and sealed acceptances are untouched.
- **Plate/mail full technical and personal review has completed; final acceptance/registration is being sealed.** Each set has960 base plus192 alternate native views personally inspected. The combined base matrix has1,920 frames/320 posed bodies/640 shoulder pose-side checks; the alternate matrix has384 frames/64 posed bodies/128 shoulder pose-side checks. All64 native processes closed successfully. The submitted source/body/attachment/material gates pass; no head/ear/helm contacts or eye-center obstruction in the sampled matrix. Residual backing/seat contacts and hollow-cap design remain explicit. No replacement plate/mail patch installation or live acceptance yet.
- **Runtime remains cea1/235c/6a43; VMaNGOS PID79660 remains the23:53 restart.** No weapon art/name mutation or further restart occurred in this checkpoint. Glass's72-frame acceptance and ten weapons'180-frame acceptance remain intact. The prepared current ArmorForge evidence card has not yet been deployed. The smaller-model cookbook and two licensed external adaptation trials remain as recorded below. Commander raid remains paused.

## Latest execution state — 2026-09-29, after 00:12 UTC (local September 28)

- **Glass Comet T is accepted for the documented offline, installed and live scope.** The final live receipt `artifacts/equipment-workshop/live-evidence/current-armor-v1/glass-live-acceptance-v1/acceptance.json` (SHA `b0ec59ba11056a9ed8e75f7a0acad58710b34e766c78a307fc52ad6500e924f9`) binds72 unique personally reviewed PNG/observation/gameplay triples and464 checked files:12 self/remote world views,8 real UI hovers,2 actual Fireballs,48 per-piece removal/restoration views and2 independent relog views. All19 saved references remain, including the untouched phantom2654. Legs/bracers occluded by the robe/sleeves are not claimed to produce visible pixel differences. Live body scope is HuF mage170 with actual observer169; retained offline contacts remain disclosed. Historical63e5 runtime proof is preserved separately.
- **Gloam V15 is installed and its installed standing gate passes.** Current normal DLL `cea1cbe1cc2b96e0199f223d59a9087ce8f9f62785b801dc980ae4f32a38767f`, patch4 `235c7c0e2eceb59b2925494e9538fdad7eb9d495c093927afaadf23fb287b9c0`, patch7 `6a43fc6c5a2d214cd9d64110ae4ea5e319983cad6683acf80c46fecf4b9b1da4`. Installed23:54:43UTC with outside-repository rollback. Exactly50 Gloam assets were added;13,230 prior assets and all36,929 prior display/240 set rows remain unchanged. Actual installed mount verifies130 requested members. Both Gloam96 and Glass96 standing color/contour sets are byte-identical to their accepted earlier counterparts. Gloam's strict audit covers42 runtime members,16 bodies, all32 seated shoulders, unchanged bodies/attachments, with767 classified support contacts retained. Evidence: `gloamwing/installation-V15-v1/installed-native-audit-v1/standing-acceptance.json` and Glass `registration-T-v1/patch235c-compatibility-v1/`.
- **VMaNGOS restarted successfully23:53:01UTC with set5197 added.** Current mangosd-main PID79660 runs the unchanged allocator binary73a515…; realmd1802 remains unchanged. ItemSet SHA `8e6370792ddb97f327e6f9fe324f1e55679eafab13bf35fe1586255a985dd3ec`;240 previous rows preserved. RA is healthy. Startup retains668 distinct pre-existing world-data errors; comparison finds zero new errors and no SQL/duplicate/assert/ItemSet failure. Do not claim a clean zero-error world database. Owner authorization remains in force; old temporary target GUIDs must not be reused.
- **Gloam live work has started, not completed.** Warrior169 received one capacity bag4500/GUID2565 through ordinary gameplay, then exactly one copy of each Gloam entry1102538–1102545 with GUIDs2566,2568,2569,2570,2571,2572,2573,2583 respectively. Complete inventory comparisons preserve23 original items, then24 originals including the bag;32 owned instances now exist without orphans. Do not repeat these grants. The self-day equip/capture protocol is running; further live/UI/combat/removal/relog gates remain. Evidence: `scratch/equipment-batch/live-equipment/current-armor-v1/gloam-plans-v1/` and its preservation receipts.
- **Plate/mail V2 focused seating is now accepted for the repaired cases.** Root reviewed DwF/OrM shoulder roots as visibly supported; intentional hollow interiors are not floating whole attachments. Source audit covers640 pose/side selections with all head/ear/helm contacts clear. The full1,920-view matrix is frozen and queued after the current live run; no replacement plate/mail registration has occurred.
- **The smaller-model cookbook and external trials are concrete.** [EQUIPMENT_LOCAL_MODEL_COOKBOOK.md](EQUIPMENT_LOCAL_MODEL_COOKBOOK.md) supplies commands, exact triangle budgets, atlas ownership, axis/UV/scale conventions and attachment-ID/bone distinction. Two licensed CC0 inputs retain original bytes and credits. The first rock and wood shoulder studies failed art review despite successful conversion; a locally repaired curved wood cap passes a focused HuM standing six-view check.36 native frames/6 probes and the mixed-provenance strict-valid teaching package remain private evidence, not accepted all-body gear. See `artifacts/equipment-workshop/external-workflows-v1/RESULTS.md`.
- **Ten weapons remain complete for their180-frame live scope.** No completed weapon mutation was repeated. Commander raid remains paused. Locator stopped unexpectedly and was restarted through its documented host; do not kill it. All evidence and failed candidates remain preserved.

## Latest execution state — 2026-09-28, after 23:40 UTC

- **Owner added a concrete teaching requirement:** the reusable workflow must be literal enough for a smaller local model. Document mesh triangle allocation/counting/reduction, drawing and conversion, texture regions, ordinary helm/shoulder attachment coordinates and scale, with commands and pass/fail gates. Two licensed external-asset exercises are underway: Kenney CC0 rock geometry and Poly Haven CC0 wood diffuse. Their private prototypes compiled and completed24 native frames; source/material and visual review remain separate gates. They are not registered gear.
- **Glass Comet T installed native proof is complete:**96 frames/16 bodies on the actual production IDs and normal cea1/63e5/6a43 runtime; all96 color and96 contour images are byte-identical to the accepted T standing matrix. Strict geometry/material/paint/body/attachment binding passes. Evidence: `registration-T-v1/installed-native-audit-v1/` beneath the Glass armor directory.
- **Glass live checks now pass22 frames:** four self daylight/interior/movement, two self night, eight actual icon/tooltip hovers, four actual remote daylight/interior, two remote night, and two actual Fireball views. All were personally reviewed. Eight names/icons and the complete8/8 set tooltip are proven. Live actor is HuF mage170, remote observer169; this is not all-body live coverage. Evidence: `artifacts/equipment-workshop/live-evidence/current-armor-v1/`.
- **Glass combat-v1 remains a retained failure.** Its second cast failed target-facing after intercast combatstop; exact-target cleanup succeeded. New combat-v2 faces the exact selected target before each camera/cast and stops combat after both captures. It passed131 steps, two received same-target Fireballs and exact target-removal/save checks. The original frozen preparer was not edited; the derived protocol binds its base plan/protocol and new wrapper. New target17379390967040632885 was removed. Receipt: `glass-combat-v2-composed-execution.json`.
- **Eight Glass copies were granted once and saved; do not grant them again.** Entries1102530–1102537 have GUIDs2539,2542,2558,2559,2560,2561,2562,2564 respectively. All19 saved references remain; the pre-existing phantom2654 stays untouched. All19 equipped slots are guarded. Piece-removal/restoration and final independent relog are the remaining Glass gates; their preparation is underway.
- **Gloam V15 is now offline accepted and registered, but not installed.** Source631f3cc8… has1,152 frames/192 probes with192 personally reviewed boards and an explicit retained-contact/eye-occlusion ledger. Set5197, entries1102538–1102545, displays76381–76388 were registered once; all50 registry members and eight world rows match. A build-only candidate235c7c0e… adds exactly those50 assets while preserving13,230 earlier asset members,36,929 display rows and240 set rows. Evidence: `gloamwing/registration-V15-v1/` and `installation-V15-v1/`. No local patch or server table changed during this candidate preparation.
- **Plate/mail repair continues.** The216-frame follow-up seating study closed all16 processes successfully; technical contact success is not art acceptance. Root independently rejected DwF left cast-release as still reading suspended in side views; a broader authored support/reseat is required. No plate/mail replacement registration has occurred.
- **Normal runtime and server remain the22:47 baseline below.** No weapon mutation was repeated; no additional restart occurred. The owner's restart/install permission remains in force. Commander raid remains paused.

## Latest execution state — 2026-09-28, after 22:47 UTC

- **Ten weapons:** complete scoped live evidence remains the180-frame ledger below; no completed art/name mutation was repeated. The updated Weapon Forge card is deployed and personally checked in the actual browser, including two real combat sheets and the38 disclosed historical hair/beard/horn cases.
- **Glass Comet T:** full offline acceptance is sealed at `artifacts/equipment-workshop/armor-redesign/prototypes/raid-v3/glass-relief-T/complete-review-v1/acceptance.json`:1,152 frames/192 posed bodies,768 personally reviewed full-body views,22 processes closed successfully. Small TaF mid-attack hand/cuff-to-mask and NiM early-run ear-tip/cloth contacts are explicitly retained; this is not a global zero-intersection claim.
- **Glass T is registered and installed, with live acceptance pending.** Exact source SHA `fc46965ca0dbb1b8672c23105e968b563ff1878015ca08e83c485e027c9dee54`; set5196, entries1102530–1102537, displays76373–76380. All48 saved asset members match the compiled report. The new patch adds those48 members and preserves13,182 existing asset members, all36,921 prior display rows and239 prior set rows. Evidence: `artifacts/equipment-workshop/armor-redesign/raid-v3/glass-comet/registration-T-v1/`.
- **Current normal runtime has changed:** DLL `cea1cbe1cc2b96e0199f223d59a9087ce8f9f62785b801dc980ae4f32a38767f`, patch4 `63e5c42aee1494992bdbb8e2b6cc639088a8671f1616412c127afd016a87cecd`, patch7 unchanged `6a43fc6c5a2d214cd9d64110ae4ea5e319983cad6683acf80c46fecf4b9b1da4`. Installation was22:41:29UTC with outside-repository rollback;80 requested members resolve correctly through the actual installed mount. Twelve ordinary sword/staff compatibility frames and contours are byte-identical to the old normal baseline. The installed Glass96-frame check is prepared, not yet run.
- **Server ItemSet deployed and VMaNGOS restarted successfully** at22:45:20UTC under the owner's standing permission. PID78301 runs the unchanged proven allocator binary73a515…; realmd1802 is unchanged; RA is healthy. Server ItemSet SHA `aeb1e104e8dd8f3691a0574ff9e4e5a508c8d1a50f4650bb4eb0cbfa96cf3cd1`;239 prior rows preserved, only set5196 added. Rollback: `/home/wowvmangos/deploy-backups/equipment-glass-T-20260928/ItemSet.dbc`. Startup diagnostics retain3,451 lines with no SQL/duplicate/assert/ItemSet failure found. Old combat target GUIDs must not be reused after this restart.
- **Shoulder-fit web support deployed** at22:23UTC, web DLL e4741cb9…; backup `/home/wowvmangos/deploy/backups/auto-20260928T182318`. All owner configuration, prior backups and pre-existing runtime payload are preserved. Actual staged and registered Glass shoulder previews pass32 body-side selection/hash checks. The page-wide stale-patch banner describes the Linux configured client folder, not the separately installed Windows MSUIClient.
- **Other armor remains unfinished.** Gloam V15 has accepted focused NiF/NiM/TrF repairs and is beginning the full1,152-frame matrix on the new baseline. Plate/mail NiF compact fits and TrM rear caps have successful focused native evidence; other body/side repairs continue. No leather/plate/mail replacement set is registered as accepted gear. All native runs are serialized; coordinate with the active owner before launching.

## Owner alignment update — 2026-09-28, subsequent conversation

This update records the owner's subsequent clarification. The original checkpoint and evidence below remain historical records; the restart authorization here supersedes their statements that permission is still missing.

- **Goal:** a reusable, agent-operated, documented equipment pipeline producing original gear that fits the Classic/TBC world. A new owner-operated guided Forge UI is not required.
- **Compatibility target:** MSUIClient with Classic/TBC art and asset limits. Exact feature parity with the original Blizzard 1.12 client is not required. The owner cited warglaives as the intended kind of extension: additional equipment capabilities in MSUIClient while retaining the era's construction and appearance.
- **Construction boundary:** preserve the existing geoset and texture-atlas rules. Helms and shoulders use custom models; the remaining armor is painted onto the existing supported body regions/geosets. This is not authorization to replace painted body armor with arbitrary attached 3D pieces or change character bodies/skeletons to accommodate the art.
- **Meaning of body-specific fitting:** if needed, fit the authored custom helm/shoulder mesh to the wearer. Preserve normal attachment transforms and rendering rules. An explicit selection of declared fitted meshes may be considered within MSUIClient; do not disguise art defects with per-theme/per-race transform exceptions. The optional shoulder-fit extension in section 7 remains unimplemented.
- **Explicit operational authorization:** the owner authorized installing the prepared item-ID allocator fix and restarting VMaNGOS or MangosSuperUI whenever needed for this task. Do not ask again for these covered actions. Recheck the actual service identity and current source/binary state before using the prepared installation, preserve existing evidence and persistent state, and verify health and save/relog behavior afterward. An actual new tool rejection must still be reported rather than bypassed.

At this alignment update, no allocator installation, service restart, armor repair, weapon mutation, or new verification run has occurred. Existing completed weapon mutations must not be repeated. The unrelated Commander raid mission remains paused.

### Resumed execution update — 2026-09-28, after 20:24 UTC

The owner subsequently said “Okay. Keep going.” Work has resumed within the clarified scope above. The frozen checkpoint below is not current authorization or completion state.

- **Item-ID fix installed and persistence verified.** The originally prepared binary failed actual startup: `m_ItemTextIds` also uses `HIGHGUID_ITEM`, so the old guard incorrectly queried `MAX(id)` from the item-GUID UNION. The old binary was restored and verified healthy before repairing the candidate. The corrected guard additionally requires `item_instance.guid`. Six source-derived C++/SQLite regression tests pass, including a demonstrated failure-before/pass-after item-text test. Corrected Core SHA **`73a515be443340089bffc0c54530061cfad4e177c099f6c658ccb3f7c912a261`** was installed and started at **20:12:12 UTC**; the running `/proc/74346/exe` hash matches. Rollback: `/home/wowvmangos/deploy-backups/equipment-item-guid-20260928-allocator-v2/mangosd`.
- **Four successful live protocols** verify actual equip/save and a separate read-only login for both disposable actors. Mage170 retains staff1102495/GUID2523 and wand1102497/GUID2526; warrior169 retains sword1102488/GUID2527. Saved instance rows, owner IDs, all19 visible slot guards, renderer display/name metadata and four PNG/JSON pairs are bound in `artifacts/equipment-workshop/live-evidence/item-guid-fix-v2/verification.json`. This is scoped persistence proof, not the complete current-gear live matrix or inventory-icon/tooltip acceptance.
- An initial mage guard correctly rejected a pre-existing phantom glove: inventory row2654 exists but its item_instance does not. It remains untouched. The subsequent test explicitly binds that absence and the observed empty slot; it does not delete/recreate the orphan or weaken the full19-slot guard. Unrelated orphan2546/actor163 also remains untouched. Failed first startup and failed baseline run are preserved. Deployment/regression/raw server evidence: `scratch/equipment-batch/live-equipment/persistence-diagnostics/authorized-install-v1/`.
- **Installed weapon audit completed.** The original240-frame run omitted weapon mesh probes. A separately guarded repeat with `MSUI_EQUIPMENT_MESH_PROBE_WEAPONS=1` produces240 byte-identical color and240 byte-identical contour PNGs, with38 actual submitted weapons and2 correctly hidden stowed-wand probes. Source/index/material/placement binding passes; maximum position error2.6783e-7. All240 cases were visually reviewed. `unified-patch-candidate-v1/installed-native-audit-v2/REVIEW.md` and `audit.json` record the narrow Human/Tauren male standing scope. `installation.json` now includes this proof; live/global cosmetic flags remain false. No weapon art/name/registry/patch mutation was repeated.
- **Armor work is ongoing.** The proposed plate/mail V8 rear-pocket experiment was created and rejected in source review; evidence remains immutable. Three body-specific source fitting fixtures exist, but they do not establish complete armor acceptance. A generic declared fitted-mesh selector is being implemented in isolated builds, preserving existing atlas/geoset/body/skeleton/attachment rules. Glass and Gloam have further versioned local repair candidates under review. No new raid armor registration or installation has occurred.

### Resumed execution update — 2026-09-28, after 21:16 UTC

- **Current weapons:** all70 self daytime/movement/sheath/panel/interior frames,20 night frames and10 actual live hover captures pass technical binding and have been personally inspected. All10 approved names and custom equipped icons are visible. Receipt: `artifacts/equipment-workshop/live-evidence/current-weapons-v1/self-and-ui-personal-review-v1.json`. Stowed wand frames do not prove its held/shoot appearance. No weapon art/name/registry/patch mutation was repeated; missing warrior QA copies were granted once through ordinary gameplay commands, then reused.
- The warrior remote daytime/interior run completed1,063 steps with0 failures;32 frames bind actual observer170/subject169 rendering, and all32 were personally inspected. Evidence: `current-weapons-v1/warrior-remote-day-audit-v1`. Remote night, mage remote, actual combat and complete per-copy persistence remain separate unfinished gates. Adaptive combat preparation is starting against a newly observed temporary creature; the pre-restart target GUID must never be reused.
- **Shoulder-fit extension:** implemented and tested in isolated builds.36 authored web tests and128 compiled/parser/MPQ/staged/registered checks pass. A separate actual GL fixture now passes8 simultaneous frames/48 body-side draws, retaining six model objects per set while alternating draw order: HuM defaults, TrF plate L/R fits, NiF mail R fit with L default. This is the real remote-style MountSet rendering path under one GL renderer, offline; it is not authenticated live-world proof. Evidence: `artifacts/shoulder-fits-pipeline-v1/REVIEW.md` and `simultaneous-v2/verification.json`; contract: `docs/SHOULDER_FITS_EXTENSION.md`. Normal installed DLL7657/patch4ef44/patch7 remained unchanged.
- **Armor candidates remain unaccepted.** Glass R has960 main native frames with exact binding, but the expanded motion matrix exposed real right-lens/sleeve crossings on DwF/GnM/GnF and a TrM helm-fin contact. Its192 alternate frames were prepared but cancelled; authored right fitted variants are under repair. Gloam V8 has exact384-frame proof but was rejected for floating shoulder seats; V9 adds physical fasteners and has completed384 focused native frames, pending full contact/art review. Plate/mail source repairs continue. These counts do not claim final armor quality or deployment.
- The reusable agent procedure is collected in [EQUIPMENT_AGENT_WORKFLOW.md](EQUIPMENT_AGENT_WORKFLOW.md), including source/native/live evidence distinctions, fitting boundaries and preserved-state rules.

### Resumed execution update — 2026-09-28, after 22:03 UTC

- **All ten current weapons now have complete scoped live verification.** `artifacts/equipment-workshop/live-evidence/current-weapons-v1/collection-live-ledger-v1.json` binds **180 unique reviewed frames**:90 self scenes,10 real icon/tooltip hovers,60 actual remote views and20 combat frames. Each weapon has7 self daytime/interior/movement/sheath/panel,2 self night,1 UI,4 remote daylight/interior,2 remote night and2 appropriate combat views. Staff Fireball133 and visibly held wand5019 have actual received spell events; melee has received hits on exact newly observed temporary targets. Both targets were verifiably removed.
- **All ten QA copies persist with their original item GUIDs and owners.** `ten-copy-persistence-v1/persistence.json` binds saved inventory, later real UI logins, current instances and inventory references. No duplicate weapon copies, art/name/registry/patch reapplication, unrelated orphan cleanup or additional Core restart was needed.
- Earlier failed combat harness runs remain preserved. The apparent warrior-combat-v3 pass is superseded: selecting the creature during `.combatstop` caused server `Player not found` errors. The corrected audit rejects that run. Accepted warrior-combat-v4 uses `warrior-combat-audit-v3` and `warrior-combat-events-v2`; the helper guards/selects the QA player for combatstop and verifies exact received despawn events. The log reader handles UTF16 and normalized GM errors;19 regression checks pass, with6 separate combat-target/cleanup checks.
- This completes the **live weapon scope**, not blanket cosmetic acceptance. The historical2,960-frame/880-probe all-body study retains38 disclosed hair/beard/horn cases and predates the type8 tail repair. The240 installed-native frames and180 live frames retain their own renderer/hash scope. Old Redfen armor and starter clothing are background only.
- **Glass T** passed focused108 frames/18 probes with exact source/member/body/transform binding. Four right fits repair the S cases; a ScF-only left local lift clears the outer flap. Root concurred that the short inner seam is seating rather than the previous exposed outer-lining cut. The disjoint remaining1,044-frame/174-probe matrix is running on isolated cea1 client; full armor acceptance remains pending.
- **Gloam V11** passed focused36 NiM frames with a compact bent support; V12 NiF/TrF straight stalks were rejected as visibly detached. Further compact local feather repairs remain source work. **Plate/mail** compact NiF front caps have24 painted full-kit native frames, pending complete art/contact review; other required fits remain unfinished. No new armor set is registered or installed.
- Normal client7657, patch4ef44 and patch76a43 remain unchanged. The shoulder-fit extension remains isolated; final app/client promotion and new armor registration/live verification are still required.

---

The owner said: **“Stop and give me a fantastic handoff for another agent.”** Work is stopped. This document transfers context; it is not permission to restart this task in the current conversation. All three subagents stopped. There are no owned Blender, MSUIClient capture, Python experiment, or pending tool sessions left running. Do not kill the unrelated locator/service process.

## 1. Read this first

The task is **not complete**. Ten original weapons have been authored, reviewed extensively in the production offline renderer, adopted into existing Forge records, renamed with explicit approval, and installed in the local client patch. Four complete eight-piece raid armor packages exist, but all four still have unresolved shoulder fitting defects. **No replacement raid armor set has been registered or installed as finished gear.** Native technical success is not artistic or live acceptance.

The important distinction at handoff:

| Area | Proven state | Remaining work |
| --- | --- | --- |
| Ten weapon model/skin/icon revisions | All 30 members adopted into existing production Forge records; exact bytes verified | Final installed-client capture audit, actual inventory icon/name draw, world and persistence verification |
| Ten weapon names | All ten approved name-only updates applied; other 129 world-template columns preserved | Runtime item-template reload/client cache and visible name verification |
| Local weapon patch | Installed `patch-4.MPQ`, all 32 requested members resolve through the actual client mount with expected hashes | Latest 240-frame installed-native run completed, but formal source/mesh/image audit and personal image review are pending |
| Normal client | Tested type-8 skin-extra DLL/PDB promoted; 12 normal-path frames pixel-identical to isolated build | Actual remote/live and broader appearance coverage |
| Plate/mail | Best native relief candidate V5 still cuts exposed head/ear surfaces; V7 is a source trial only | Correct rear-wall pocket, or adopt explicit fitted shoulder variants if shared sculpture cannot fit |
| Leather | Gloam V4 reduces many contacts but still cuts NiF/ScF rigid masks; additional early/run pose defects exist | Local repairs without losing mask eye openings, wing support, or identity |
| Cloth | Glass K has exact technical evidence and improves prior failures | Clear NiM lens/ear and ScF cloth/jaw intersections; finish visual review |
| Forge tools | Authored armor lane, research/evidence tools, deployed multi-angle/pose review cards | Publish later evidence honestly after new packages are actually verified |
| Live persistence | Identified and built a Core item-GUID allocator fix | **VMaNGOS stop/install/start requires an unanswered explicit approval** |

Do not rerun successful adoption/name batches. Do not describe the old rejected armor IDs as these new raid sets. Do not report the whole task complete because the weapons are installed.

## 2. What the owner actually wants

The original request was to study many original 1.12/TBC weapons and armor, learn how their simple meshes and hand-painted textures were constructed, then produce **10 different original weapons and four full original armor sets**, one each plate/mail/leather/cloth, with custom shoulders and helms. Use and extend MangosSuperUI Forge. Verify the real MSUIClient output across angles, movement, materials, appearance, and actual game use with reproducible tools rather than casual screenshots.

Feedback and decisions that must persist:

1. First armor generation was rejected: common shapes, uniformly repeated patterns, insufficient material/type distinction.
2. Second generation was superseded as bland fantasy. The owner asked for memorable raid gear, with broader WoW and FFXIV inspiration while keeping Classic construction/readability.
3. Owner approved carrying these four directions forward:
   - **Oath of the Broken Gate** — plate, gate/hinge/parapet identity.
   - **Leviathan’s Ransom** — mail, shell/sea-trophy identity.
   - **Gloamwing Stalker** — leather, ivory insect mask, three-wing fan and amber cocoon.
   - **Glass Comet Masquerade** — cloth, cream/magenta folded drape and asymmetric cyan lens.
4. Approval of the concept directions is **not acceptance of unresolved clipping**, nor permission to flatten every silhouette until numerical contacts disappear.
5. The owner expects autonomous problem-solving, not repeated requests to define the art or approve routine engineering choices.

The original concept PNGs/provenance are already visible in the Armor Forge concept card. Preserve their identity and the distinct material/garment language. Low triangle count alone is not enough. Avoid generic repeated trims across every piece, destroying silhouettes through global shrink, or manufacturing a renderer exception to hide a local art problem.

## 3. Permissions, environment, and repository rules

### Approvals already given

- Web deployment: **“Approve web app deployment.”** This authorized the tested MangosSuperUI web-only deployment/restart. It has already happened several times through the established workflow. Do not ask again merely for this same scope.
- Four armor directions: **“Carry these four directions forward.”**
- Ten weapon names: **“Approve the ten name changes.”** Those exact ten changes are now fully applied.

### Approval still missing

The separate unanswered question was:

> May I briefly stop and restart only VMaNGOS to install the tested item-GUID allocator fix? Automatic approval review requires your explicit approval for this interruption. The fix prevents custom equipment saves from colliding with orphaned item references; CMaNGOS and database rows will remain untouched.

Automatic approval review rejected that interruption pending explicit consent. **Neither web-deployment nor weapon-name approval grants it.** No Core restart/install occurred. Do not route around the block with another process-control API or database cleanup. Continue independent fitting/verification once the owner resumes; if restart remains needed, preserve the pending request and explain its exact source.

### Paths and services

| Purpose | Location |
| --- | --- |
| Web repository / working directory | `C:\Users\nico\source\repos\MangosSuperUI` |
| Client repository | `C:\Users\nico\source\repos\MSUIClient` |
| Installed client data | `C:\Users\nico\source\repos\MSUIClient\GameData\Data` |
| Normal client DLL | `C:\Users\nico\source\repos\MSUIClient\MSUIClient\bin\Release\net8.0\MSUIClient.dll` |
| Blender | `C:\Users\nico\Desktop\CRPG-Ultum\_tools\blender\blender-4.5.12-windows-x64\blender.exe` |
| Python | `C:\Users\nico\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe` |
| Node | `C:\Users\nico\.cache\codex-runtimes\codex-primary-runtime\dependencies\node\bin\node.exe` |
| Locator | `http://127.0.0.1:5077` |
| Running web app | `http://192.168.0.2:5000` |
| SSH | `wowvmangos@192.168.0.2` |
| Linux web runtime | `/opt/mangossuperui` |
| Linux Core | `/home/wowvmangos/vmangos` |

Always use Blender `--python-exit-code 1`; otherwise a Python failure may leave a misleading successful process exit.

Read both repositories' `AGENTS.md`. The root web instructions require locator-first code discovery: `locate` → `outline` → `neighbours` → one bounded `read` span (≤400 lines). Only fall back to tree searches after two failed phrasings, and say so. Locator was healthy at handoff. JSON/binary artifacts are not indexed; direct known-path reads are appropriate. If locator is down, start it from `C:\Users\nico\source\repos\SourceMapper\Locator` or tell the owner instead of silently bypassing it.

No commits, pushes, branches, worktrees, or in-tree `.bak`/`.pre*` files. Leave changes unstaged. Both repos contain unrelated changes, especially WorldBuilder/WorldPacks; do not revert or absorb them. Client `AGENTS.md` itself was already modified by other work. Preserve `server-config.json`, owner credentials, WorldPacks, and `patch-7.MPQ`. Runtime files live on Linux. Ship diagnostics through app endpoints rather than asking the owner to inspect server files. Source game assets come from mounted MPQs, not extracted `wwwroot` models/textures. Public review PNGs are evidence, not the asset loading source.

The client repo is outside the current writable roots; writes there require the actual tool escalation. Read-only inspection is available. Do not use another API to evade a denied write. Normal app data/private configs may contain credentials: inspect needed fields privately and never print secrets.

## 4. Exact installed state and rollback copies

### Normal client promotion — completed

Only the already-tested DLL/PDB were promoted from `MSUIClient\scratch\skin-extra-build\Release` to the normal Release directory. The apphost, owner config, deps and shaders were not replaced.

| File | Current SHA-256 |
| --- | --- |
| `MSUIClient.dll` | `7657c2da7aba749b60fcb3aeac5688a6100bb52682a3ee990e4f510c519df9ea` |
| `MSUIClient.pdb` | `2809f80b47aeca0e0d50618c1054582ea495e063e44aba044f55c16258edb615` |

Rollback directory: `C:\Users\nico\AppData\Local\Temp\msui-skin-extra-client-df2a9986d6154ef2894c73edfdcea1e5`.

Previous DLL: `40ffc7f86951d97946dd3871b1e4a0488c90230ca03717b0a575a5f676ef1a42`.

Script: `tools/equipment-workshop/Promote-TestedSkinExtraClient.ps1`.

Receipt: `artifacts/equipment-workshop/weapon-redesign/raid-v3/native-v5/client-promotion.json`.

The normal-path smoke has 12 frames / four probes / 21 members: Human/Tauren both sexes, three angles. Every PNG is pixel-identical to the corresponding isolated fixed DLL. Evidence is under `armor-redesign/prototypes/raid-v3/gloam-motion-candidate-v4/normal-path-smoke-*`. This was before the explicit weapon patch replacement below.

### Installed patch4 — completed

Current `MSUIClient\GameData\Data\patch-4.MPQ`:

- SHA-256: **`ef44bd3fb007d45760478109e7fb9d80f8a57d04a8e348e0eb25f0685b13827e`**
- Size: **81,717,656 bytes**.
- Previous SHA: `a74fdea81fb136afe262ea5f4ff87190d09a87cf4be90cdea8333b59bd9db8c4`.
- Rollback file: `C:\Users\nico\AppData\Local\Temp\msui-raid-weapon-patch-cb8d8cc846e349a1b94f721f05068e85\patch-4.MPQ`.
- Preserved patch7 SHA: **`6a43fc6c5a2d214cd9d64110ae4ea5e319983cad6683acf80c46fecf4b9b1da4`**.

Build/audit/install root: `artifacts/equipment-workshop/weapon-redesign/raid-v3/unified-patch-candidate-v1`.

Read `installation.json`, `verification.json`, `installed-mount-verification.json` there. The installation receipt is `state: installed`, `mountedVerified: true`, **`liveVerified: false`**. Script: `tools/equipment-workshop/Install-ReviewedRaidWeaponPatch.ps1`.

The initial `File.Replace(stage,target,$null)` failed before mutation because PowerShell passed an empty backup path. Root checked old, backup and staged hashes, corrected the script to same-directory `[IO.File]::Move(stage,target,$true)`, and installed the exact staged bytes. Receipt records this; there is no pending partial replacement.

The preceding explicit `/UnifiedPatch/Rebuild?deploy=false` was build-only. Snapshot preserved 7,249 retextures, 12 weapons, 56 armor records and 67 sets. Full member comparison found only ten changed models, ten changed skins, ten new icons, ten display field-5 icon references and listfile differences. **No removed members; 13,153 unrelated members byte-identical.** All 36,921 display rows retained; unrelated fields/rows and ItemSet are unchanged. Wand visual2799 is retained. Patch7 has no equipment DBC/weapon overlap.

The actual client `MpqMount.ReadFileWithSupplier` now resolves all **32** requested members (30 weapon art members + ItemDisplayInfo + ItemSet) to expected hashes, with real archive priority. Current mounted ItemDisplayInfo SHA: `30439bf5e44e6e935c151db10f8bbdb467b71ec731c313ea83b5c91eb26c796f`.

`tools/equipment-workshop/ArmorPrototype/Program.cs` gained `--mounted-members <input.json>` for this read-only audit. Input is `{dataPath,output,members:[{path,sha256}]}`; output includes real supplier. Helper build used `/p:BuildProjectReferences=false` and did not rebuild the normal client.

**Old study receipts froze patch4=a74f before this authorized installation.** They remain valid for their copied isolated mounts. Do not rerun an old “installed archive unchanged” gate against ef44 and mislabel the expected difference as a model regression. New studies must record ef44 and current DLL explicitly.

No server DBC deployment or Core restart is implied by the local patch installation.

## 5. Ten weapons: authoritative version and production records

Final current art is **painted-v6 / native-v5**, not earlier adoption-v1 or source-v5. These are ten distinct original meshes with simple hand-painted textures.

| Item entry | Display | Name | Family | Triangles |
| --- | --- | --- | --- | ---: |
| 1102488 | 76331 | The Last Lock | 1H sword | 172 |
| 1102489 | 76332 | Silken Mercy | Dagger | 220 |
| 1102490 | 76333 | Tideclaw Severer | Axe | 152 |
| 1102491 | 76334 | Bell of the Unburied | Mace | 316 |
| 1102492 | 76335 | The Orchard’s Last Winter | 2H sword | 264 |
| 1102493 | 76336 | Ransom of the Deep | 2H axe | 184 |
| 1102494 | 76337 | Weight of the Unfallen | 2H mace | 164 |
| 1102495 | 76338 | Meridian of the Glass Comet | Staff | 222 |
| 1102496 | 76339 | Proboscis of the Pale Hunt | Polearm | 140 |
| 1102497 | 76340 | The Last Coordinate | Wand | 108 |

Root prefix for the following paths: `artifacts/equipment-workshop/weapon-redesign/raid-v3/`.

| Evidence | Path / SHA-256 |
| --- | --- |
| Current compiled models/textures/icons | `native-v5/compile-index.json` |
| Current matrix ledger | `native-v5/matrix-v1/current-evidence.json` — `7eca035daac3113d081e9a0d71179a6650b9628eb92d64f1ba9e020b58793f4e` |
| Adoption manifest | `adoption-v2/adoption-manifest.json` — `d54d39e437706e99e27bb5d64d31e55fa2861622d0ebaf0f86fedd5699609feb` |
| Art apply receipts | `adoption-v2/production-apply-v1/index.json` — `38bcb2657313d12707307c5bd4ddc7ca3b0a0e3e72610ce53322c4e305480eff` |
| Name apply receipts | `adoption-v2/name-apply-production-v1/index.json` — `772033f20560f93091320c188cc23abd6b5e18f2c0fd1bdabbeeef48c1d298fe` |
| Reviewed limitations | `NATIVE_FIT_REVIEW.md`, `native-v5/matrix-v1/review/`, `native-v5/matrix-v1/stock-hair-comparison/` |

All ten art applies succeeded, exact 30 model/skin/icon hashes. All ten name applies succeeded following the latest explicit approval. The name runner made ten fresh previews before the first write and fresh after-inspections per item. Existing IDs and all other 129 columns are preserved. Runner: `tools/equipment-workshop/apply_raid_weapon_names.py`. **Do not apply again.**

Native full evidence is 2,960 unique frames / 880 posed meshes: 736 fresh and 2,224 explicitly retained only where exact source/model/texture metadata remained unchanged. V6 repaired Orchard Human-female hair crossings16→0 and Meridian Gnome-female hair crossings25→0, with palm/grip, UV, triangle streams and eight other weapons unchanged. There are zero sampled head-skin/ear crossings, but **38 recorded hair/beard/horn cases** remain (previously41), with no increases. These are disclosed, not all cosmetically dismissed. Dwarf-female hammer braids lack an exact stock control; Tauren-male staff/horn uses stock-width comparison; Night-Elf-male staff hair6 matches stock. The older full weapon matrix predates the separate type8 correction; do not pretend it used DLL7657.

All ten 64×64 original icons were personally reviewed. Native DXT3 preserves supported alpha with explicit15/255 quantization; do not call them opaque or infer actual live inventory icon loading from decoding/character captures.

### Immediate unfinished weapon checkpoint: installed native smoke

Directory: `unified-patch-candidate-v1/installed-native-smoke/`.

- `capture-quick.json` → `quick-captures/`: **216/216 frames**, 36 probes, zero technical errors.
- `capture-wand-quick.json` → `wand-quick-captures/`: **24/24 frames**, four probes, zero technical errors.
- Normal DLL7657, actual installed Data, no review overlay. Human male and Tauren male, standing0@0.6, six views, drawn/stowed. Wand uses sheath2/0; other weapons1/0.
- Both processes exited0. Summaries explicitly say offline production renderer, inWorldVerifiedfalse, visualReviewRequiredtrue. Logs retained.
- Assets sampled by root show correct patch4 model/skin hashes and current DBC; type8 original Tauren texture is present.
- **Not done:** full member/image/mesh/source/material/coverage audit for this run, source-derived crop boards, personal full review, installation receipt update for this new smoke.

Reuse logic from `tools/equipment-workshop/raid_weapon_verify_native.py` and `raid_weapon_verify_matrix.py`, but do not run them unchanged and then weaken their failures: they hard-code `captures-quick` naming, overlay supplier `patch-2000000000.mpq`, isolated `mount.json` and the old compile DBC. This installed run intentionally uses `quick-captures`, supplier `patch-4.MPQ`, and the real installed DBC30439. Adapt a distinct installed-run verifier with explicit expected current hashes/suppliers. Keep historical scripts/receipts intact. Native row data includes exact placements, camera, material batches, PNG hash and compressed posed-mesh references.

No runtime `.reload item_template` was issued after the approved renames. The app has audited RA routes (`Players/RaCommand` was documented; inspect its current guard before use) and `ConsoleHub.SendCommand`. `CustomWeaponBuildService.ReloadItemTemplateAsync` sends `.reload item_template`. Root had only started locating the appropriate route. Reload would not fix the pending item-GUID allocator problem and is not a Core restart. Verify actual response and subsequent template/cache behavior; never use generic Items/Save REPLACE to rename because it can reset fields.

## 6. Armor packages and candidate state

All four are complete eight-piece packages: helm, shoulders, chest, belt, legs, boots, wrists, gloves; six separately painted body pieces, original icons, sixteen race/sex helm variants, two shared shoulders. Helm textures128px, shoulders256×128. Body paint, gear removal, alpha/foot rules were researched and exercised. No raid sets are registered yet.

Frozen full-package baselines under `artifacts/equipment-workshop/armor-redesign/raid-v3/`:

| Set | Baseline package | SHA-256 |
| --- | --- | --- |
| Broken Gate | `broken-gate/package-draft-v1/armor.zip` | `f73b39cd4d721f7cc90e7799c83e25b6ec1fcfd5ab9216f016a2fc1362562fca` |
| Leviathan | `leviathan/package-draft-v1/armor.zip` | `7f5dbde67b0fb576b48c6ab72b45b721be4355b99b144f825826e2480c102f49` |
| Gloam | `gloamwing/forge-package-native-repair-v10/armor.zip` | `aaa8e56ea434b1b9a6ba1d9e192f0a60b3a7f6c78b9f46d3adb146ad38ecb8b1` |
| Glass | `glass-comet/forge-package-tailored-v9/armor.zip` | `458392a246e08d31c1df1bac44eef0b57fd1bc8d853cb4cf90e908241995ced9` |

Baseline helm/L/R triangles: plate224/176/212; mail312/330/246; Gloam432/272/56; Glass364/84/156. Later local relief may change shoulder topology as explicitly recorded.

The all-four compiled study is `armor-redesign/prototypes/raid-v3/full-four-drafts-v2`: 32 review-only display IDs960100..960131, 162 compiled members, 384 standing frames/64 posed meshes.130 character-loaded members are bound; 32 icons were not loaded by those character frames. Motion adds1,728 frames across16 bodies/nine poses, plus384 alternate-appearance frames. All passed technical binding but exposed intersections remain.

The old rejected production sets5192–5195, items1102498–1102529, displays76341–76372 remain retained. They are **not** the new raid designs. Do not overwrite them casually or claim new raid IDs already exist.

### 6A. Plate and mail — stopped agent `armor_pipeline_audit`

**Best native-tested relief is V5, not accepted.**

- Source/package root: `armor-redesign/raid-v3/plate-mail-motion-candidate-v5/`.
- Packages: `{broken-gate,leviathan}/package/armor.zip`.
- Native study: `armor-redesign/prototypes/raid-v3/plate-mail-motion-candidate-v5/`.
- 288 focused frames / 48 posed meshes, Human/Night Elf/Troll both sexes, four poses/six views/two sets. Exact source/material/member binding; paired actual body and unchanged helm delta0.
- V5 closed-backed pocket keeps a physically connected shoulder; plate292 and mail454 triangles. Painted hero face retained.
- **Failure:** broad retained rear wall crosses Night-Elf-female head; other right shoulder parapet/ear/jaw details still hit, and Troll ready left wall has skin contact. Lower totals do not excuse exposed geometry.
- `focused-review/stock-comparison/` contains eight hash-bound original-pad hybrid comparison sheets with same custom body/helm/pose. Four Night-Elf-female sheets were personally inspected. Stock pad crowding exists but custom V5 has additional rear-wall penetration.

Rejected history: V2 depth/seat candidate had480 frames/128 probes and real exposed crossings. V3 through-cut disconnected supports. V4 orphan-cap invalid. V6 rear/strap crescent lost hinge/shell identity. Do not promote any of them.

Latest source-only V7: `armor-redesign/raid-v3/plate-mail-motion-candidate-v7/`. It cants original trophies25° outward, slightly lifts to preserve hero height, and reties saddle to unchanged foot.48 source renders were finished and personally inspected. Identity is better than V6, but Night-Elf-female motion crossings and stretched static supports remain. **No native compilation or promotion.**

**Next bounded experiment proposed but NOT created:** the V5 recess cut from +X/front and retained the negative-X rear wall, where actual contacts concentrate. Reverse the local recess opening from negative-X/rear toward the head, retain the complete painted front face and perimeter, test physical connections/topology and focused NiF/Troll source views first. **There is no V8 asset, package, or run.** Do not search for or claim it.

Adjacent `.parts.json` binds source GLB SHA and complete per-triangle construction labels. Motion proof also binds the exact label receipt. Four malformed/hash/count/fallback tests passed. Visibility helpers cast actual captured camera rays; original opaque type1 body and authored attachments occlude, alpha hair/glow do not. Conservative visible flags still need raw image review.

### 6B. Gloamwing — root's current fitting work

Source roots: `armor-redesign/raid-v3/gloamwing/motion-shoulder-candidate-v{2,3,4}`.

Native studies: `armor-redesign/prototypes/raid-v3/gloam-motion-candidate-v{2,3,4}`.

**V4 is the latest native package**, SHA **`c7faa9fd9312a1e92f66b78f6650952ee46d2bbd3b3f9085bb176ce6ca85cbbb`**. It keeps all16 repaired v10 helms, six body paints, icons, UVs, topology streams and materials unchanged; only the two shoulder geometries/notes differ.

- V3/V4 left fan:40° outward,0.05 outward offset,0.24 rearward. Right:20°,0.05 outward,0.16 rearward,0.08 front relief. Read the authoring script's coordinate conventions; do not treat these as world axes.
- V4 reduces root-cup rim X/Z to0.65/0.7 around the seated peak, keeping height. Finite physical overlaps with all three wings are13/16/14. Earlier smaller/translated cups were rejected because wings detached; a0.5/0.6 rim also lost support.
- 192 native frames /64 poses across all16 bodies: Stand0@0.6, Attack17@0.35, Ready51@0.2, Cast53@0.35; left/front-three-quarter/back-three-quarter views.
- All64 paired actual bodies exact; binding/source/material/UV/index checks pass. All16 standing eye-center rays clear and zero standing helmet/shoulder contact.
- Head-weighted contacts fall L1190→24 and R318→20, no new/increased cases in this bounded64-pose subset.

**Not accepted. Remaining issues:**

- OrF Attack: right20 contacts, actual skin0 at cocoon lower outline point2, not root dome. Some front/left pixels exposed; back view hidden by hood.
- TaM Ready: left14 hair/mane-root contacts, tiny seam, no visibly severed mane in the reviewed crop. Disclose; broad shrink is not justified.
- TrM Cast: left10 ear-root702 contacts, small underarm seam with continuous ear silhouette. Disclose.
- **NiF77 and ScF113 rigid ivory-mask/fan triangle pairs remain.** Independent inspection proves these hit mask cheek/outer-eye-to-jaw, not only hidden hood antennae. ScF wing visibly crosses the mouth/cheek. These are real blockers despite head-skin count0.

Independent evidence: `gloam-motion-candidate-v4/independent-residual-review/REVIEW.md`, `projected-contacts.json`, five raw/projected crop sheets.

**Right-tip source repair proposed but NOT exported/native tested:** move only original outline point2 `(.120,-.131,.234)` and underside `(.102,-.131,.234)` duplicates by `(-.08,+.05,-.06)` after V3 deformation. Retains upper amber face and other vertices. Across192 source contexts: right rigid-helm88→0, head61→21, remaining NiF RunLate ear. `probe_gloam_cocoon_tip.py` and `gloam-motion-clearance-v1/cocoon-tip-trials.json` document it. No V5 package exists from this suggestion.

The larger192-context check with V4 left + proposed right tip still finds:

- NiF RunEarly: left50 ear702.
- NiF RunLate: right21 ear702.
- NiF AttackEarly: left10 actual skin0.
- TrF AttackEarly: left37 ear702 +22 actual skin0.
- TaM Ready14 hair2; TrM Cast10 ear702.

Those extra early/run poses have not been captured natively for the new candidate. Source report: `raid-v3/gloam-motion-clearance-v1/remaining-source-v4-plus-tip.json`.

Do not repeat failed global grids:50/60° fold, more rearward offsets, more outward shifts and whole fanY turns were measured and worsened contact/support or did not solve the issue. Do not push the whole mask into the face: X retreat trials created actual skin collisions and lost amber/antenna support. `gloam-mask-relief-feasibility-v1/` retains the table. `gloam-mask-contour-feasibility-v1/` proves protected eye-hole rim stations still account for NiF6/77 and ScF10/113 immutable contacts, so a hem-only edit cannot preserve every eye-aperture vertex and remove all current contacts.

Useful scripts under `tools/equipment-workshop/`: `author_gloam_motion_candidate.py` (`--version`, `--left`, `--right`, `--cup-rim`), `probe_gloam_root_cup.py`, `check_gloam_cup_support.py`, `probe_gloam_cup_rim.py`, `probe_gloam_cocoon_tip.py`, `classify_gloam_remaining_source.py`, `compare_gloam_motion_candidate.py --study`, `review_gloam_candidate_contacts.py --study`. The author script does **not** yet have a cocoon-tip option. Package-only-change proof `bind_shoulder_candidate_package.py` permits notes+two shoulders only; changing helms requires explicit new proof rather than pretending they stayed unchanged.

### 6C. Glass Comet — stopped agent `era_asset_research`

**K is best current candidate, still not accepted.**

- Source: `armor-redesign/raid-v3/glass-comet/motion-local-relief-v1/candidate-K/manifest.json`.
- Config: same root `forge-package.config.json`.
- ZIP: `candidate-K/forge-package/armor.zip`.
- ZIP SHA: **`6761ed73e7a9248d590a98edf947ab6e226a479db25580facc0b7cd46ae2b95e`**.
- Native study: `armor-redesign/prototypes/raid-v3/glass-relief-K/`.

K retains cream/magenta folds, gives the lower cloth room outside the upper-arm envelope, recesses inner seat, and tilts cyan lens45° outward. Left84/right156triangles. Only shoulders/notes differ from full v9; helms, body paint, icons, UV/diffuse remain unchanged.

Technical proof: strict Forge validation,8pieces/42members; **384 native frames/112 probes**. All16 standing/sixviews, all16 six priority poses/threeviews. Poses: Stand0@0.6; Attack17@0.15/0.35; Ready51@0.2/0.5; Run5@0.4; Cast53@0.35. All three `full-package-binding.json` reports have zero errors. Static and motion source/native geometry, indices, material match.288 original/candidate image pairs have identical cameras and body/helm geometry within enforced tolerance. Mount records current ef44patch and DLL7657.

Visual review done: static groups1/3 (Human, Orc, Undead, Tauren both sexes/fourviews); NiF/ScF six-pose left-view comparisons; all four remaining contact crops. K clears J's ScM upper-arm bone puncture through cream cloth. **Static groups2/4 and all Gnome/other-body motion sheets have not been fully visually reviewed.**

Four remaining cases:

1. **NiM Attack17@0.15:** exposed ear tip penetrates cyan lens.
2. **ScF Attack17@0.35:** exposed inner cloth edge intersects cheek/jaw.
3. NiF Cast53@0.35: small temple/ear contacts near hair/mask; pixel classification pending.
4. TaM Ready51@0.5: small horn/mane-area contact, mostly covered in inspected crop but not formally cleared.

Exact crops: `glass-relief-K/contact-crops/manifest.json`; full classification `contact-visibility-type1.json`. Conservative visibility is not an art pass.

**Next bounded new revision:** source-only testing suggests lowering K's high inner cloth seat0.055 removes ScF head contact without losing outer drape. It is **not exported or native-tested**. For NiM lens, inspect cyan triangles37/107 and relieve local rear thickness. That lens edit was only considered, **not implemented**. Whole-attachment translations worsened other cases. Do not create a new broad search grid.

History: C roll, E leading trim, H down-arm fold and J retracted seat all retain their failures. H looked directionally sound; J exposed ScM bone; K corrected it. C's REVIEW.md still has historical “next native comparison” wording. K immutable reports and this handoff supersede that status. No K public catalog, registry or installation update occurred.

## 7. Optional fitted shoulders: investigated, NOT implemented

This is a promising alternative when a shared sculpture cannot clear unusual bodies without destroying the approved art. The client already supports16 helm meshes; independent read-only audit found explicit per-body/per-side shoulder overrides feasible without new IDs, DBC fields, SQL tables, Core changes or transform exceptions.

Proposed manifest contract (not current supported input):

```json
{
  "models": { "L": "models/shoulder_L.glb", "R": "models/shoulder_R.glb" },
  "shoulderFits": {
    "NiF": { "L": "models/shoulder_L_NiF.glb" },
    "ScF": { "L": "models/shoulder_L_ScF.glb" }
  }
}
```

Defaults stay mandatory; one side may override independently. Reuse shared shoulder skin. Compiler would emit fitted M2s and a small versioned companion manifest next to each affected default M2, explicitly mapping body codes and hashes. The client should select **only declared** variants. Undeclared bodies use defaults. Missing/corrupt declared members must produce diagnostics and prevent false review success. Do not guess suffix files or hard-code theme/race exceptions.

Anchors (use locator to refresh spans):

- `MangosSuperUI/Services/ArmorForge/CustomArmorBuildService.cs` near1375 already stores arbitrary MPQ member blobs in `custom_armor_model`, including icons. Unified assembly near1586 forwards them. No schema change needed.
- `AuthoredArmorAssetValidator.cs` near42 currently requires exactly L/R. Add strict optional body/side declarations.
- `AuthoredArmorCompiler.cs` near50 currently treats every non-L shoulder key as R. Requires explicit side/body handling.
- Preview naming uses the final underscore suffix; fitted L/R would collide. Both staged and registered dressing selection need the same naming/selection rules.
- Client `MSUIClient/World/Units/AttachedItemRenderer.cs` near411 is shared by self/offline/remote. All receive `RaceGenderCode`; resolver and shoulder caches need body-aware keys.
- Capture evidence must report selected model and companion-manifest hash.

ZIP limits can remain unchanged:128entries (directories count),32MiBcompressed,64MiBexpanded,4MiBmember,64KiBmanifest. Maximum existing body-component coverage +16helms+2defaults+32fits+8icons+2skins+45components+manifest=106files. Omit directory entries.

This is a coordinated compiler/preview/client extension, not a renderer rewrite. Necessary tests include default compatibility, strict invalid declarations/hash failures, selective side override, and simultaneous different-body remote/default rendering to catch cache contamination. Native fit evidence remains required. **No implementation, source assets, or runtime state were changed for this proposal.** Root was still pursuing bounded local shared fixes when stopped; next agent should decide deliberately rather than assuming this design was already chosen.

## 8. Research and renderer findings to preserve

Do not restart the research from zero. Prior study covered593assets,240weapons,121helm/shoulders,80painted pieces,764textures;30weapon/120native renders and12ensemble/144native frames, plus four original TBC ensemble browser studies. Exact counts are research coverage, not completion claims for custom art.

Read:

- `docs/EQUIPMENT_ART_REDESIGN.md` — owner feedback, style/construction failures and required review order.
- `artifacts/equipment-workshop/reference-ensembles/plate-mail/CONSTRUCTION_STUDY.md`.
- `scratch/equipment-batch/reference-sets/expanded-study/README.md`.
- `artifacts/equipment-workshop/armor-redesign/RAID_PLATE_MAIL_DIRECTIONS.md`.
- `docs/EQUIPMENT_REFERENCE.md`, `docs/EQUIPMENT_BARE_FEET_RESEARCH.md`, `docs/EQUIPMENT_FIT_MEASUREMENTS.md`.

Original TBC browser evidence is not native1.12 evidence; the vanilla DBC parser cannot honestly claim the latter. Keep original missing slots for five-piece tiers/seven-piece families instead of inventing complete reference sets.

### Type8 fix is real and already installed

`armor-redesign/raid-v3/skin-extra-diagnostic/TYPE8_CORRECTION.md` records the diagnosis and23focused passing checks. Original CharSections skin `Texture2` supplies separate tail/mane skin-extra for texture type8. It must not receive the dressed type1 atlas. Tauren male geoset1501 has both type1 hump and type8 tail; do not suppress the whole geoset. Female tail shares geoset0. Missing extra remains absent rather than borrowing a previous batch texture.

Controlled sync/async, remote player/NPC and legacy routes were wired. Original before/after has96+96frames (four drafts +Valor/Magister, Human/Tauren both sexes/fourangles);48Human identical,48Tauren corrected, exact original asset hashes plus two newly bound extras. Original Scourge bone texture is beige/yellow; do not infer a rendering bug from golden bones. Actual remote/live appearance is still separate.

### Shoulder transform bug hypothesis was tested and unsupported

`armor-redesign/raid-v3/shoulder-attachment-diagnosis/DIAGNOSIS.md` binds432original/hybrid images,144meshes and1,296placements to an independent raw-v256 track/pivot evaluator. Max world-matrix error4.224e-7, zero bone mismatches. Original shoulder parent bones vary arm/clavicle correctly; observed flags0/0x200. Six original shoulder models have static roots and no missing authored counter-animation. Another320matrixcomparison max6.861e-7. Quaternion interpolation sensitivity is too small to explain severe covering. There is no evidence for a universal bone remap/flag fix.

The Blizzard executable itself was not replayed; do not claim complete original-client parity. Equally, do not modify current renderer transforms without new evidence. Original gear clipping is useful control, not permission to accept visibly worse custom head penetration.

## 9. Forge/tooling, web deployment, and durable mutation safety

Latest web deployment backup: **`auto-20260928T141038`**. Log: `scratch/equipment-batch/raid-name-recovery-web-deploy.log`. Health passed. No Core restart. It includes safe name journal/recovery and the weapon review card. The earlier armor-motion deployment receipt is `scratch/equipment-batch/raid-motion-art-web-deploy-receipt.json` (backup133246); current web includes that work too.

Armor review public catalog: `MangosSuperUI/wwwroot/equipment-workshop/armor/raid-fitted-v1/catalog.json`.2,112frames/4,224color+contour images, all16wearer/ninepose selectors, synchronized angle, paired zoom/pan/exposure/guides. Historical rejected/superseded sets remain clearly distinct. Current public motion catalog still describes failed draft motion; **it does not include latest local V5/V4/K fitting as accepted sets**. The Leviathan apostrophe correction was included in latest deployment.

Weapon review: `_RaidWeaponReview.cshtml`, `raid-weapon-review.js`, sharedCSS; publisher `publish_raid_weapon_review.py`. Public `/equipment-workshop/weapons/raid-native-v5/catalog.json` SHA `5d519e081eb43a19cd3b4f5540e167b8a8e74e12e9e69b403052cd1eeb8a422d`. Ten icons/two native overviews bind to current30appliedmembers,2960/880ledger,38remaininghaircases/livepending. Root personally checked actual IAB names/icons/counts/evidence links. Receipt `native-v5/forge-review-deployment.json` saysbrowserVerifiedtrue without invented screenshot. Both cards are deployed, not merely local HTML.

Authored armor files: `Controllers/AuthoredArmorController.cs`, `Services/ArmorForge/AuthoredArmor{Manifest,AssetValidator,Compiler,ResumePolicy}.cs`, `CustomArmorBuildService.Authored.cs`, `_AuthoredArmor.cshtml`, `authored-armor.js`. Optional painted-region rules permit foot-only cloth shoes, short sleeves/narrow belts; still require actual visible paint and valid universal or both gender components. All19authored-package tests passed at that stage.

New raid armor registration is **pending until fit/art is actually ready**. Use authored Stage/Build lane, with exact reviewed ZIP. It handles eight-piece admin transaction and guarded world MyISAM writes; resume same package/IDs instead of allocating duplicates. Queue/build/deploy scopes are separate. Inspect current APIs through locator before applying; do not substitute generic item SQL.

Weapon mutation files include `WeaponNameRevision.cs`, `WeaponNameJournal.cs`, `CustomWeaponBuildService.NameRevision.cs`, `.NameRecovery.cs`, controller`.NameRevision.cs` with RecoverName; related ArtRevision/GeometryRevision services and controllers. `docs/EQUIPMENT_ART_REVISION.md` has API/recovery details.

Actual production is **InnoDB Forge + MyISAM world**. Non-atomic name flow uses per-item GET_LOCK/release, fresh130column compare-and-swap, endpoint/server/schema binding, fsync/rename journal before world write, locked fresh registry recovery, exact before/after classification and conflict refusal. Prepared journals block fresh previews until recovery. Floats need correct single→double round-trip SQL predicate representation. Never call this atomic or silently overwrite a conflict. Combined70focused checks (name34) pass and actual MyISAM predicate previews passed. Actual live fault injection was not performed. The authorized ten-name apply finished cleanly.

Useful documentation: root `WEAPON_FORGE.md`, `ARMOR_FORGE.md` are chronological; later dated notes supersede earlier “pending” statements. This handoff corrects the latest installed state. `docs/CUSTOM_EQUIPMENT_WORKSHOP.md`, `EQUIPMENT_EVIDENCE.md`, `EQUIPMENT_ART_REVISION.md` explain tool contracts.

## 10. Live verification and the blocked Core fix

Earlier live studies were for old workshop art and exposed a persistence failure. They are not proof of current raid gear. A stale item GUID allocator reused GUIDs already referenced in orphaned inventory rows; save transaction rollback caused new equipment to disappear on relog. Do not hide this by deleting unrelated references or repeatedly recreating gear.

Prepared Core fix initializes HIGHGUID_ITEM from the maximum across seven relevant tables. Fourfocusedtests passed; Core CMakebuild100%. It is **built but not installed**.

- New binary SHA: `3a9378584956490bffc57f98490521344d8fbeb3a7d832cfb312bb6ce70029a3`.
- Old binary SHA: `80d5d2bc79aae44cc2056770e20212208cc6f7ed2c45ca64f67a183386aa77a9`.
- Rollback: `/home/wowvmangos/deploy-backups/equipment-item-guid-20260928-allocator/mangosd`.
- Work/evidence: `scratch/equipment-batch/live-equipment/persistence-diagnostics/`.
- Read `FINDING.md`, `core-install-prepare.json`, `item-guid-occupancy.diff`, `test_item_guid_occupancy.py`, `prepare-core-install.py`, `install-core-binary.py` before any approved install.
- Only after explicit approval: recheck current hashes/service identity, stop/install/start **VMaNGOS only**, verify health/save/relog. Preserve CMaNGOS and all DB rows.
- Unrelated actor163 orphan itemGUID2546/slot1/item13088 is untouched. Do not repair/delete it as a shortcut.

### Existing disposable QA actors and guards

- Private dedicated account `EQUIPQA0928`; do not expose credentials in `private-config.json` or settings.
- `Forgeplateqa`, GUID169, Human-male warrior60. Last recorded saved state: old Redfen eight pieces +axe1102490 +shirt38.
- `Forgecastqa`, GUID170, Human-female mage60. Last recorded custom items lost to rollback; starter cloth remained. Learned133Fireball,5009,5019wandshoot.
- Reinspect actual server/character state before relying on this snapshot.
- **Never operate on Testwar787, the protected raid, bots115–142/150–160, or unrelated characters.** Client Commander raid work is a separate paused scope.

Runner/docs: `scratch/equipment-batch/live-equipment/run-live.ps1`, `README.md`, `REMOTE_MATRIX.md`, `LIVE_EVIDENCE.md`, `TARGET_WORKFLOW.md`, `verify-live-evidence.py`, `prepare-sequential-run.py`, `prepare-remote-matrix.py`. They contain historic loadout names/IDs for old armor; regenerate for final raid packages, never just assume they point to the new sets. Use private isolated settings/config. Run `dotnet DLL` and wait; GUI apphost can return before capture completes.

Important established details:

- Full19slot expected-ID guards. Slots: helm0,shoulder2,chest4,belt5,legs6,boots7,wrists8,gloves9,main15,off16,ranged17.
- Normal `item-gesture use-entry` and assert route, not a fabricated render equip. GM send PASS alone is not server acceptance.
- Exact-GUID guard before every action; `.save SESSION` only. Fresh `/Bots/Inventory` is DB saved state, not current memory; save/logout first.
- Actual movement `press W` /`release W`, not `forward`.
- Wand5019 real autorepeat uses runtime ready111/shoot107; Escape to cancel. A second5019 can fail due to GCD and is not reliable cancel. Fireball133 is separate. Offline forced53 is fit stress, not proof of gameplay pose.
- `dump` writes real PNG+JSON to client `dumps`, not `--out`. Collect exact labels/timestamps and verify bothfiles.
- Day/night isolated local lighting times12/0; does not change server clock. Verify real WMO interior lighting weight.
- Outdoor map0 at(-8952.5,-132.493,83.36127),face0. Interior(-8908,-155,81.94199),camera225/15/6; remote(-8910.321,-153.606,81.944),225/15/7. Recheck framing rather than inheriting blindly.
- Same-account offline alt via `companion summon ExactName` gives real remote renderer. Exact-name companion possession has correct ACK; do not use nearest-player `sui-possess`, or `.sui possess` missing expected ACK. While possessed, GM additem targets selected Player, not automatically controlled actor.
- Disposable wolf target last recorded GUID17379390967040632837,low1501189,entry299,passiveHP1m. Verify exact identity before action or cleanup; never target an existing raid unit.

Required final live proof remains: approved current loadout and names/icons, actual controlled and remote rendering, outdoor day/night/interior, movement/attack/cast/sheath, expected server-visible display IDs, `.save` then relog persistence. Original study or offline native screenshot cannot stand in for those.

## 11. How to continue efficiently after the owner resumes

1. Read this file, bothAGENTS, then latest dated root Forge docs. Verify current installed DLL/patch hashes before creating any new run. Preserve unrelated dirty files. Do not rerun the already-completed ten art/name updates.
2. Finish the **already captured** installed weapon240frame/40probe audit and personal review. Adapt a separate verifier to realpatch4/currentDBC rather than modifying frozen overlay assumptions. Record exact limitations and update installation receipt only after proof. Check names via audited runtime reload and actual UI later.
3. Choose a bounded armor strategy. Plate/mail rear-opening pocket and Glass local seat/lens repair have specific measured next steps. Gloam global transforms and whole mask retreat have already failed; use local exact contacts, or implement explicit fitted shoulders if needed. Do not spend another long session cycling broad placement grids.
4. Preserve complete default identities, physical support, eye apertures, topology/material/UV provenance and piece-removal behavior. Source checks precede costly native captures. New revisions get new directories; never overwrite failed evidence.
5. Coordinate a **single native capture slot** across agents. It is free at handoff. Meaningful independent tasks can parallelize source audits/code work. Keep each process/output owned; no generic process killing. Capture planner has a64posed-probe per-run budget; split larger matrices deliberately.
6. Verify exact planned wearer/pose/view coverage, compiled/source bytes, actual submitted indices/positions/material/appearance, PNG/probe hashes and mount supplier. Classify contact by actual skin/ear/hair/eye-glow/other custom part, then inspect raw images from all relevant angles. Zero aggregate skin count does not prove custom mask/wing clearance; smaller totals do not prove art quality.
7. Complete missing visual review and expanded/alternate appearance samples on a candidate that survives focused tests. Reuse earlier evidence only for explicitly hash-identical assets and scope, not for changed geometry or renderer behavior.
8. Register four finalized eight-piece packages through Forge, preserving existing content, then unified build-only full diff, explicit local/runtime deployment steps and actual mount proof. Update Forge public review to exact current accepted/rejected state, not optimistic labels.
9. Resolve the separately pending VMaNGOS restart approval. After approved fix, verify persistence before investing in a long live matrix. Then run guarded current-item live/self/remote/icon/name/combat/relog proof. Do not use old outfit evidence as current proof.
10. Finish with a small reviewable gallery and exact item/set IDs, source packages, installed version, test coverage and remaining scoped limits. Do not claim completion before the original10weapons+4finishedsets requirement is actually met.

## 12. Artifact preservation and status traps

- Many `artifacts/`, `scratch/`, `tools/` and root design docs are ignored/untracked; `git diff` alone cannot transfer this task. The handoff assumes the same filesystem. A move to another machine needs an explicit artifact transfer including relevant immutable packages/evidence, without private credentials.
- No commits/pushes/branches were made. Dirty work also includes unrelated WorldPacks and client WorldBuilder changes. Never reset the tree to “clean up.”
- Root stopped immediately after the installed240frame capture, before audit. All agents are complete/stopped. Plate/mail rear-openingV8, Glass next seat/lens revision, Gloam right-tip export and fitted-shoulder architecture are **proposals, not completed work**.
- Public Forge cards have honest failed/pending labels. Approved concepts ≠ validated assets; strict import ≠ coherent design; production offline renderer ≠ authenticated world; registered bytes ≠ installed mount; screenshot ≠ icon draw; successful equip ≠ saved persistence.
- Contact visibility deliberately excludes alpha hair/glow as occluders. Pair counts depend on tessellation; compare exact context and surfaces, not raw totals alone. Hair cards, eye-glow alpha and hidden support seams require classification, while exposed face penetration remains a defect.
- All preceding “installed unchanged,” “name approval pending,” “normal client unchanged,” and “not deployed” notes are chronological checkpoints. Use the exact latest receipts and this handoff; do not overwrite historical facts to make the timeline look simpler.

### Suggested next-agent opening prompt

> Continue the custom Classic equipment task from `C:\Users\nico\source\repos\MangosSuperUI\docs\EQUIPMENT_RAID_HANDOFF_2026-09-28.md`. Read it and both repositories' AGENTS.md before changing anything. Ten weapons are already adopted, approved names applied, and local patch4 installed; do not repeat those mutations. Four approved raid armor directions remain unfinished because of documented fitting defects. Preserve all unrelated work and immutable evidence, finish the captured installed-weapon audit, then make bounded armor repairs or a carefully tested generic fitted-shoulder extension. Do not restart VMaNGOS unless I explicitly approve that separate pending interruption. Continue toward ten verified weapons and four finished, distinctive full sets with Forge and actual-client/live evidence; do not confuse technical passes with artistic acceptance.

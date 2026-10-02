# Declared authored shoulder fits — 2026-09-28

This is an optional MSUIClient selection extension for custom shoulder meshes. It selects an authored M2 by exact body and side. It does not alter the body, skeleton, attachment matrices, animations, geosets, atlas regions, or a loaded mesh. Chest, robe, legs, gloves, boots, bracers and belts still use the existing painted body pipeline. One skin remains shared by both shoulder defaults and every fit.

Current state (2026-09-29 00:12UTC): implemented and deployed. The web implementation was deployed September28 at22:23UTC; normal client cea1 was promoted22:41UTC. Glass Comet set5196 now has its complete scoped72-frame live acceptance at `artifacts/equipment-workshop/live-evidence/current-armor-v1/glass-live-acceptance-v1/acceptance.json`, including actual remote views, UI, combat, per-piece removal/restoration and relog. Its live body is HuF; this does not imply all-body live coverage. Gloamwing set5197 was added in patch235c at23:54UTC with its server ItemSet/restart verified. Both installed96-frame/16-body standing matrices pass exact source/body/attachment/member binding and192 byte-identical color/contour comparisons each. Gloam live verification is ongoing. Historical promotion and offline residual-contact scopes remain retained in their original receipts.

Current evidence: `artifacts/equipment-workshop/armor-redesign/raid-v3/glass-comet/registration-T-v1/installed-native-audit-v1/` binds the real installed captures, strict audit and pixel comparison. This promotion adds authored shoulder selection only; painted body armor, character bodies, skeletons and normal attachment transforms are preserved.

## Authoring contract

The original `models.L` and `models.R` GLBs remain mandatory. On the shoulder piece only, add an optional `shoulderFits` object:

```json
{
  "key": "shoulder",
  "name": "Example shoulders",
  "iconPng": "icons/shoulder.png",
  "skinPng": "textures/shoulder.png",
  "models": {
    "L": "models/shoulder_L.glb",
    "R": "models/shoulder_R.glb"
  },
  "shoulderFits": {
    "NiF": { "R": "models/shoulder_R_NiF.glb" },
    "TrF": {
      "L": "models/shoulder_L_TrF.glb",
      "R": "models/shoulder_R_TrF.glb"
    }
  }
}
```

The strict body keys are `HuM HuF OrM OrF DwM DwF NiM NiF ScM ScF TaM TaF GnM GnF TrM TrF`. Historical study names such as `NeF` must be explicitly converted to `NiF` when constructing a source manifest. They are not runtime aliases. Side keys are exactly `L` and `R`; a body can override either side independently. A missing body or side selects that side's ordinary default. Each declared default or fit names its own GLB member; duplicate ZIP/JSON declarations, unsafe paths, unknown fields, missing members, bad geometry, and ordinary material/triangle/texture violations are rejected.

The package still uses schema version 1 with one optional field. Existing packages without `shoulderFits` compile without marked models or additional manifests. Fits receive no display IDs or item IDs. The existing arbitrary `custom_armor_model` member storage and armor MPQ builder carry the additional models and JSON; there is no DB schema change.

## Compiled contract

A default side with at least one fit has the generic suffix `|MSUI_SHOULDER_FITS_V1` appended to its internal M2 name. This is only a version marker. The companion location is exactly the default's full MPQ path plus `.shoulderfits.json`:

```text
Item\ObjectComponents\Shoulder\SUI_A_960101_L.m2
Item\ObjectComponents\Shoulder\SUI_A_960101_L.m2.shoulderfits.json
```

Its JSON schema requires every field below:

```json
{
  "schemaVersion": 1,
  "side": "L",
  "default": {
    "path": "Item\\ObjectComponents\\Shoulder\\SUI_A_960101_L.m2",
    "sha256": "<64 hexadecimal characters>"
  },
  "skin": {
    "path": "Item\\ObjectComponents\\Shoulder\\SUI_A_960101_V01.blp",
    "sha256": "<64 hexadecimal characters>"
  },
  "variants": {
    "TrF": {
      "path": "Item\\ObjectComponents\\Shoulder\\SUI_A_960101_L_fit_TrF.m2",
      "sha256": "<64 hexadecimal characters>"
    }
  }
}
```

The compiler chooses each fit's filename and writes the exact path into the manifest. The resolver never constructs a fitted candidate filename or tries a guessed fallback. It requires a marked default's companion, matches its default, side and shared skin to the current display, and verifies the SHA-256 of the default, shared skin, and **every declared fit**, including unselected bodies. Missing or corrupt declared data stops resolution. Unknown marker or manifest versions stop resolution. An unmarked default never probes a companion and remains compatible with ordinary asset loading.

The companion hashes the final marked default. The M2 contains no manifest hash, so there is no circular dependency. The proof compares every original byte outside the internal-name header/name section: vertices, indices, UVs, material tables, skeleton and other original streams remain exact. Additional path/hash metadata is not a fitting transform.

The manifest is at most 64 KiB, has at most 16 body bindings for one side, and accepts only bounded safe model/skin paths under the shoulder object-component directory. Declared binary members retain the 4 MiB member bound. Changing any bound asset requires recompiling its manifest; do not recolor or replace one bound member independently and expect it to pass validation.

## Consumers and evidence

- `AuthoredArmorCompiler` compiles the same strict GLB path for defaults and fits. It emits explicit companions into `Source.ModelMembers`.
- Staging creates distinct `shoulder_L_NiF.glb` / `shoulder_R_NiF.glb` names and a private `shoulder-preview-members.json` proof. `AuthoredArmorController.Dressing` resolves the compiled data and checks the selected preview hash. A missing proof or changed selected GLB fails staging review.
- Registered dressing passes race/gender into `ItemTextureService.EnsureShoulderGlb`. The service uses the same manifest resolver, separate body/side preview files, and fingerprints the exact selected model and exact declared skin. A conflicting texture basename in another folder cannot shadow the declared shoulder skin. `ItemsController` reports a resolution failure instead of silently returning a successful default preview.
- Native `AttachedItemRenderer` uses the mirrored pure resolver in `Formats/ShoulderFitManifest.cs`. Its model cache key includes data mount, model, skin, body and side. Remote/player mount sets retain their own selected models and errors; no subsequent shared renderer body change alters an already built set. The texture cache also separates data mounts.
- Offline capture records the selected `path`, `sha256`, `bodyCode`, `declaredFit`, `defaultPath`, `manifestPath`, `manifestSha256`, `skinPath` and `skinSha256`. The asset ledger records actual suppliers/hashes for default, companion, selected model and skin. Mesh probes carry the same `shoulderFit` object. Resolution errors become capture technical errors and a nonzero exit, never a successful default capture.
- Controlled and remote live equipment observations report selected fit bindings and attachment-resolution errors. An unattended live inspection with a resolution error ends with `EQUIPMENT_RESOLUTION_FAILED` after writing the diagnostic evidence.

The pure resolver is a web/client contract twin. Changes must be applied to both copies and verified with both compiled assemblies.

## Reproducible checks and current fixtures

`tools/equipment-workshop/ShoulderFitCheck` references the web project and the isolated Debug client assembly. It tests ordinary compatibility without companion probing, all 16 bodies and both sides, independent shared cache keys for two mounts, malformed/unknown/duplicate declarations, missing/corrupt defaults/skin/selected and unselected fits, strict versions, and exact web/native results. The real fixture mode compiles two full packages through the production compiler, exports and validates staged previews, packs/reopens actual MPQs, and drives the production registered-preview service against private mounts. It checks a conflicting Weapon/Shoulder skin basename and verifies the marked preview's embedded PNG equals the staged compiled skin. A private missing-companion mount must fail with a diagnostic.

The initial three source fixtures are plate Troll female L/R and mail Night Elf female R from `armor-redesign/raid-v3/plate-mail-body-fit-trials-v1`. They are source feasibility trials, not accepted armor. The proof preserves the original packages, unchanged painted assets and defaults, source ZIP hashes, compiled member hashes, chosen preview paths, and marker byte comparisons.

Evidence lives under `artifacts/shoulder-fits-pipeline-v1/`. Earlier proof directories remain intact; use the latest successful `proof-v4/contract-tests.json`, `proof-v4/fixture-proof.json`, and per-set registered-preview reports. Required client build logs are `client-Debug-build.log` and `client-Release-build.log`. `build-hashes.json` identifies the isolated outputs; `normal-runtime-preserved.json` identifies the unchanged normal Release DLL and installed patches.

Example offline proof invocation, using a **new** output directory:

```powershell
dotnet run --no-restore --project tools/equipment-workshop/ShoulderFitCheck/ShoulderFitCheck.csproj -- artifacts/shoulder-fits-pipeline-v1/new-proof artifacts/shoulder-fits-pipeline-v1/fixture-input.json
dotnet test MangosSuperUI.Tests/MangosSuperUI.Tests.csproj --no-restore --filter FullyQualifiedName~AuthoredArmorPackageTests
```

Before native rendering, use an explicitly hashed isolated client build and mount. Keep normal binaries/MPQs unchanged while another capture depends on their current bytes. Native view/pose/contact evidence, simultaneous body/remote rendered checks, visual acceptance, registry/gameplay item evidence, and final delivery are not replaced by these format and preview tests.

## 2026-09-28 — simultaneous native cache proof

The capture manifest now optionally accepts `concurrentBodies: [{"race":8,"sex":1},{"race":4,"sex":1}]` with one primary body. This offline diagnostic loads and retains each body's `MountSet`, then draws them together through **one** production `AttachedItemRenderer.Render(MountSet)` and its shared GL/model cache. It alternates draw order between frames. Body meshes, evaluated skin palettes, attachment points and transforms follow the existing production render path. Duplicate controlled attachments are suppressed only during this opt-in fixture so each retained set draws once.

`simultaneous-v2/verification.json` records **8/8 frames, 48 actual body/side draw selections, zero technical errors**. Each frame contains HuM, TrF and NiF; plate selects both TrF fits, mail selects only NiF R while its L remains the ordinary default. Post-`DrawElements` observations match every compiled selected member SHA and bound skin. Six body/side model object identities per set remain distinct and stable across four alternating-order frames. Standing front images for both sets were personally opened and show all three bodies and their shoulders. This closes the offline shared-cache/retained-remote-MountSet proof; it does not claim authenticated remote world behavior or armor art acceptance.

Reproduction: prepare a new version with `prepare_shoulder_fit_simultaneous_proof.py`, validate the manifests, then run `Invoke-ShoulderFitSimultaneousProof.ps1 -ProofRoot <new-directory>` during an available native slot. The runner guards its exact isolated DLL and preserves normal runtime hashes. `verify_shoulder_fit_simultaneous_proof.py` binds actual draw observations to `proof-v4/fixture-proof.json`. `ConcurrentCaptureCheck` passes nine ordinary/valid/invalid manifest checks. Isolated `client-pairs-Debug` and `client-pairs-Release` both build with zero errors and the same 19 existing warnings; no normal client or installed MPQ was replaced.

Independent source review found that the first verification receipt checked bound skins only on marked sides. The verifier was strengthened and rerun against the same retained native frames: the current receipt is **`simultaneous-v2/verification-v2.json`**. It additionally checks the compiled skin bytes for ordinary unmarked sides and every field of the selected shoulder binding, including companion path/hash and skin path/hash. All eight frames and 48 selections still pass; the original receipt remains intact. The review was independent source review, not an independent native execution.

## 2026-09-28 — web implementation deployed

The reviewed Release web DLL `e4741cb9b003aca0d1cb92c4a0eb58ec90341e786cbcadec3cf57f90c536e88d` is serving. Portable-PDB comparison with the previous published build bounds source changes to the shoulder-fit implementation and weapon evidence card; existing WorldPacks sources are unchanged. The deployment preserved every backup, owner configuration, and pre-existing runtime payload. VMaNGOS PID74346 remained running. Rollback is `/home/wowvmangos/deploy/backups/auto-20260928T182318`; evidence is `artifacts/shoulder-fits-pipeline-v1/web-deployment-v1/verification.json`.

The Weapon Forge evidence card was personally checked in the actual browser: ten named icons, two native overviews, two gameplay sheets and the scoped180-frame live summary render. The page-wide stale-patch badge compares the Linux configured client directory; the completed weapon patch was intentionally built without deployment there, then installed and verified separately in Windows MSUIClient. No weapon rebuild or asset mutation was repeated. New armor runtime installation and authenticated live proof remain separate work.

## 2026-09-29 — four-set installation checkpoint

The selector now serves all four registered sets: Glass Comet5196, Gloamwing5197, Broken Gate5198 and Leviathan's Ransom5199. The current normal DLL remains `cea1cbe1cc2b96e0199f223d59a9087ce8f9f62785b801dc980ae4f32a38767f`; patch-4 is `e607f73988bf0454c2f16d44d0fb326bece788be0a6ec5280211a20f0d41967a` (installed01:00:00.2038610UTC), with patch-7 `6a43fc6c5a2d214cd9d64110ae4ea5e319983cad6683acf80c46fecf4b9b1da4` unchanged. The combined patch preserves prior members/rows;268 requested mounted members resolve exactly. Each set's current96 normal-mount standing views/16 bodies and192 color/contour PNGs match its accepted controls byte-for-byte. Plate/mail submitted geometry, body/geosets, original attachment transforms, actual material/atlas and all declared fit metadata also match after explicit production-ID resource/hash remapping. No runtime fitting transform or deformation was introduced.

Evidence is under `artifacts/equipment-workshop/armor-redesign/raid-v3/four-set-installation-v1/`; plate/mail standing receipts are `{broken-gate,leviathan}/installed-native-audit-v1/standing-acceptance.json`. Their source packages have complete1,152-view offline review each with disclosed seating/contact limits. Glass/Gloam retain their historical72-frame live scopes plus separate current standing compatibility; plate/mail live UI/gameplay/remote/removal/relog remain pending at this checkpoint. Current standing is not a claim of new live evidence or universal continuous-motion clearance.

Authorized server ItemSet installation (`fbc3cd3d…`) and VMaNGOS restart completed: PID80737/start01:00:41.5160575UTC, RA connected, World initialized, allocator73a515be… unchanged. Startup comparison retains668 earlier ERROR messages and **two new** Dragons of Nightmare missing instance0/map0 and map1 messages; the captured source branch logs/continues when FindMap is absent. No new SQL/duplicate/assertion/ItemSet failure appears in that delta. Exact evidence and limitations are in `four-set-installation-v1/server-health-verification.json`; do not summarize it as zero-new errors.


## 2026-09-29 02:06 UTC — four-set live acceptance complete

All four registered armor sets now complete their documented72-frame live scopes. Broken Gate5198 and Leviathan5199 use the unchanged normalcea1/e607/6a43 client/mount and Core80737 epoch; their final ledgers are `artifacts/equipment-workshop/live-evidence/current-armor-v1/{broken-gate,leviathan}-live-acceptance-v1/acceptance.json` (SHA670013251c1b0e4ab7d7501f190ee51a16363c46d0b815fa28b2e9fdae99f6da and249b8e4f0c801b5d1d8c8b4f47bd238e9298f382782fdfc4df2723a2218afd88). Each joins actual self/remote/day/night/interior/motion, eight real UI hovers, two received melee samples, every piece's ordinary removal/restoration and independent saved relog. All40 plate-era and48 final mail-era physical wearer instances survive; observer18 physical items and historical phantom2654 remain separately preserved. No further item grants are needed.

This closes the documented live pipeline gates, without changing the selector contract: only explicit authored custom mesh members are selected. No body/skeleton deformation, attachment-transform exception or replacement geoset/paint rules were introduced. The all16-body offline/installed matrices, original shared-cache rendering fixture and finite-contact/compact-seat limitations remain separate from HuM live proof. Glass/Gloam historical live scopes retain their actual older runtime while currente607 standing compatibility is exact. Final evidence UI deployment is separately coordinated by root. Server health continues to disclose the two Dragons of Nightmare missing-map-instance messages.


## 2026-09-29 02:35 UTC — final deployment and actual SuperUI mount proof

The four-set evidence UI and final scoped acceptance are deployed. The final receipt is `artifacts/equipment-workshop/evidence-ui-four-v1/deployment-final-v2/receipt.json`, SHA `8f7ba3cd338b6f9986cb47869af775f1fc56182aba0ac1da19db554a80c18409`, joining1,164 deployed members,1,161 HTTP byte checks, actual browser review and positive known status/startup checks. SuperUI PID83845/start02:33:35UTC runs web DLL `a748f8d79c33792288da2ff28e498a41f7bd3dc678f646c017bc65c8d65edf10`; rollback is `/home/wowvmangos/deploy/backups/auto-20260928T223303`.

Both Windows MSUIClient Data and SuperUI's configured Linux Data now contain the accepted e607f73988bf0454c2f16d44d0fb326bece788be0a6ec5280211a20f0d41967a patch. The Linux old a74f archive is retained at `/home/wowvmangos/deploy-backups/equipment-server-data-20260929-0220/patch-4.MPQ`; no reauthoring/rebuild was needed. The Windows client cea1 and patch-7 6a43 remain unchanged. The restarted web app's actual opened MPQ resolves all268 expected members including both DBCs. First-supplier/size checks and the accepted SHA of the exact opened snapshot are bound by `artifacts/equipment-workshop/armor-redesign/raid-v3/four-set-installation-v1/superui-mount-verification-v2/run-v1/verification.json`, SHA `884ac66b4c8aee526695a46ff9f27239742fdc455c2f936c9d81b52646835bb1`.

Server ItemSet status now compares the current unified MPQ member, not the obsolete armor-only canonical file. Expected/actual fbc3cd3d7925be4cee1450daf328aa91ed7da7d4b0d926505e8b7bfa6f362809 match; comparison and startup are explicitly known, with no restart needed. Core80737/start01:00:41.5160575UTC remains unchanged. The two disclosed Dragons of Nightmare startup messages remain unresolved.

All recorded pipeline gates are complete; no pending approval or native capture remains. This does not expand the authored-fit contract or acceptance scope: existing body/skeleton/attachment transforms and Classic/TBC geoset/atlas construction remain exact. Per-set1,152 offline/96 current standing/72 live scopes, historical Glass/Gloam live runtimes and all contact/compact-seat/visibility limitations remain preserved.

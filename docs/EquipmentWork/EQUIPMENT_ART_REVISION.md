# Full-art revisions of existing authored weapons

2026-09-28: implemented and locally checked. The parent ran ten successful deployed
full-art dry runs and verified all30 returned member bytes; registry adoption,
patch installation and native inventory/live verification remain separate steps.

The Weapon Forge existing-weapon panel offers geometry-only and complete-art scopes.
Complete art accepts an exact rigid, opaque, single-sided GLB, an explicit opaque
256×256 skin PNG, and a 64×64 inventory icon PNG. It does not resize, orient, decimate,
or flatten alpha. Skin is DXT1; icon is DXT3 with authored alpha retained subject to
the existing four-bit alpha codec quantization. GLB embedded images are not the
replacement skin authority. The explicit PNG is compiled and shown in the preview.

## API

- `GET /WeaponForge/ArtRevisionTarget?displayId=...&itemEntry=...` reads the exact
  unshared GLB registry target and validates its live item/display/family binding.
- `POST /WeaponForge/ReviseArt`, multipart: `file`, `texture`, `icon`, `displayId`,
  `itemEntry`, `expectedModelSha256`, `expectedTextureSha256`, `apply=false`.
- Dry run compiles and retains before/new art, returns a preview, source and compiled
  hashes, exact member download URLs, and `revisionToken`. It changes no registry row.
- Apply sends the identical inputs with `apply=true`, `expectedRevisionToken`,
  `expectedRevisionModelSha256`, `expectedRevisionTextureSha256`, and
  `expectedRevisionIconSha256`. Any changed source, compiled output or preserved
  registry metadata invalidates the token. UI file/scope/target changes clear review.
- `GET /WeaponForge/ArtRevisionMember?revisionToken=...&member=...` serves only
  allowlisted old/new source or compiled files and retained JSON receipts.

The transaction locks and rechecks the current model, display and auxiliary members,
then replaces model bytes/source GLB, skin bytes/source PNG, and the owned icon member.
Any write failure rolls back earlier writes. Effects and shared models are rejected.
Only a previously adopted canonical icon may be replaced on later art revisions.
The icon is `Interface\Icons\INV_SUI_W_DISPLAY_RAID.blp`; IDs and model/skin paths
stay fixed. Explicit source material replaces inherited blend/two-sided flags while
preserving unrelated material flags, bones, animation, attachments and other scaffold.

Gameplay, current item name, build identity and display scalars remain unchanged.
After commit, cache invalidation and audit occur and a unified rebuild is queued;
the endpoint does not deploy or claim runtime/art acceptance. Each item is atomic;
a ten-item caller is not a ten-item transaction. Retain any partial/uncertain receipts
before retrying. Old/new bytes are retained before commit in
`App_Data/weapon-art-revisions/TOKEN`. Audit failure is explicit in the response.

## Current ten-item handoff

`artifacts/equipment-workshop/weapon-redesign/raid-v3/adoption-v1/adoption-manifest.json`
binds painted-v5 GLBs, original generated skin PNGs, icons-v1 PNGs, current registry
old hashes, expected 30 compiled members, and the staff structural comparison.

```powershell
$python = 'C:/Users/nico/.cache/codex-runtimes/codex-primary-runtime/dependencies/python/python.exe'
& $python tools/equipment-workshop/raid_weapon_adoption.py dry-run --output artifacts/equipment-workshop/weapon-redesign/raid-v3/adoption-v1/production-dry-run
# Review every response/compiled preview and the downloaded 30 member hashes first.
# Then supply the printed SHA of the completed dry-run index explicitly:
& $python tools/equipment-workshop/raid_weapon_adoption.py apply --reviewed artifacts/equipment-workshop/weapon-redesign/raid-v3/adoption-v1/production-dry-run/index.json --reviewed-sha256 REVIEWED_INDEX_SHA --output artifacts/equipment-workshop/weapon-redesign/raid-v3/adoption-v1/production-apply
```

The runner preflights all ten old targets, checks every local source hash and every
server-compiled/downloaded member, retains per-item results, and refuses output reuse.
It never rebuilds/deploys the global patch or creates items. On partial failure the
remaining old targets and already-adopted items must be reconciled explicitly.

Nine M2 files and all ten skin BLPs match the 2,960-frame native matrix byte-for-byte.
Staff M2 differs because the current registry donor contains prior appended geometry:
name/vertices/views header pointers move, all other header bytes are identical, all
31 non-view payloads and four view payloads are exact, and the complete native parsed
graph is exact. Historical screenshots keep their old M2 SHA. This is structure-based
fit reuse, not a new screenshot claim. See `staff-structural-equivalence.json`.

Forty-one measured hair/beard/horn cases remain explicit. Original comparison is
context, not an automatic acceptance. Icon source/native codec sheets likewise do
not prove the actual inventory draw. Existing names require a separate name update;
the full-art metadata-preserved contract must not silently rename items.

## Local validation

46 focused xUnit checks pass across full-art, geometry-only and material regression
tests. They include controller input rejection before service access, stale old hashes,
new source/icon/skin/preserved-state token changes, explicit material replacement,
DXT3 icon alpha, and failures after earlier writes through the production transaction
boundary. The rollback fixture is in-memory transactional storage, not a MySQL
integration test. The deployed dry-run receipt is `adoption-v1/production-dry-run-v1/index.json`,
SHA256 `0a419ad0e7a0f10f2b0efdda24b210bc0332f9d815478d90f591b53f8c1f662b`.
No apply was performed. Any later source repair requires a new manifest/dry run.
Debug and Release builds pass. The focused Node revision UI test checks
both scopes, exact apply fields, missing paint and input/error invalidation.

## Separate name-only revision

`POST /WeaponForge/ReviseName` takes JSON:

```json
{"itemEntry":1102488,"displayId":76331,"expectedOldName":"Greywatch Arming Sword","newName":"The Last Lock","apply":false}
```

The existing-weapon panel has a separate name preview/apply form. Inputs invalidate
its token independently of the complete-art lane. Apply repeats the exact inputs
with `expectedWorldRowSha256` from `beforeWorldRowSha256` and `expectedRevisionToken`
from `revisionToken`. No art endpoint changes names implicitly.

Preview reads both connection/server identities and actual information_schema table
engines. Different configured endpoints or database servers, unsupported table engines,
unavailable engine metadata, multiple custom patch rows, missing/extra/nullable columns,
mismatched world/registry names or corrupt recorded SQL refuse the operation. Apply
uses one Admin connection and qualified world schema. For two transactional tables it locks and rechecks the exact
full130-column live row and Forge manifest, rechecks transactional engines under those
table metadata locks, updates only item_template.name, gameplay_json.name and stored
publishable sql_text/sql_sha256, then verifies the complete resulting row before commit.
The export is regenerated with ALL130 live column values, preserving existing gameplay
instead of filling omitted fields with donor defaults. No core reload is implicit.

Production previews on2026-09-28 proved the Forge manifest is InnoDB but world
item_template is MyISAM. The ten original refusal receipts remain in
`adoption-v1/name-preview-production-v1`. A separate supported path retains this engine:
an item advisory lock, durable before/after journal and all130-column world comparison
guard the name-only statement. The Forge manifest has its own transaction. This is
explicitly **not atomic**. After either success or a failed/uncertain response, exact
committed state determines whether both rows are complete or the world name needs
compensation. Compensation also compares the entire after row; a later unrelated edit
is never overwritten. Any unresolved state retains its Prepared journal and blocks a
new preview/apply. Previews do not secretly run recovery.

`POST /WeaponForge/RecoverName` takes `{itemEntry,displayId,expectedRevisionToken}`
from the retained journal/preview. It only finalizes an exact before/before or after/after
state, or restores the old world name when the registry is exactly before and the
world row exactly after. All other states refuse and retain the journal for inspection.
Journal files are flushed before the MyISAM statement and retained per token; this
addresses process interruption, without promising filesystem survival under power loss.
Recovery never changes table engines or item stats.

Historical before-row/manifest and proposed SQL are retained under
`App_Data/weapon-name-revisions/TOKEN`. After commit the caller must use the normal
item-template reload and fresh client query. Name revision does not change patch art.
The ten requested before/after names are in `adoption-v1/name-change-plan.json`.

Eighteen focused rename checks plus the prior46 checks pass (64 total), covering
full-column preservation, safe name encoding, world/registry mismatches, stale stats,
name/manifest/server tokens, nontransactional refusal, missing-column refusal and
rollback of an earlier world rename on later manifest failure. As with full-art tests,
rollback uses the production transaction boundary with a transactional fixture;
actual database engine/permission proof remains the deployed preview's job.
Ten additional recovery tests cover the production recovery resolver, interrupted
world-only writes, uncertain commits, concurrent edits, failed/stale compensation,
parameterized130-column comparisons and durable journal roundtrips (28 name checks).
These are controlled in-memory state/fault tests and filesystem checks, not an actual
MyISAM failure injection. Live previews above performed no item writes. Runtime names
and live reload remain unverified until a separately recorded apply.


## 2026-09-28 — compact current raid weapon review card

Weapon Forge now includes `_RaidWeaponReview` above the workshop tools. Its small
catalog lists the ten raid design names, weapon families, measured triangle counts
and original64px icons, plus two clickable full-size native Human male overviews.
It states offline review,38 recorded hair/beard/horn cases and pending live/icon
draw checks; registry adoption is not presented as cosmetic acceptance.

`tools/equipment-workshop/publish_raid_weapon_review.py` verifies the complete
production apply index SHA `38bcb2657313d12707307c5bd4ddc7ca3b0a0e3e72610ce53322c4e305480eff`,
all ten receipt hashes, all30 downloaded native member bytes and their exact
current compiler/evidence bindings before copying12 image files. The overviews
are bound to their raw frame/crop provenance. No2,960-frame copy or game data
extraction is involved. The total copied image size is657,867 bytes.

Public catalog: `/equipment-workshop/weapons/raid-native-v5/catalog.json`.
The two JPGs and ten PNGs are exact source copies. The browser requests the catalog
with `cache: no-store`, uses lazy image loading and SHA-bound URLs, and exposes the
catalog/limitations separately. It uses text nodes for names and refuses invalid
counts, false live/acceptance flags or foreign image paths.

Validation: publisher `--check` rechecks all hashes and the catalog; Node
`tools/equipment-workshop/check_raid_weapon_review.mjs` checks the actual public
catalog, ten rows, two links,12 image hashes, preserved residual/pending labels,
no-store fetching and rejection of false claims. Native source data is unchanged.
Deployment and real-browser confirmation are separate owner-coordinated steps.


## 2026-09-28 — deployed weapon card verified; name application awaiting approval

Web deployment backup `auto-20260928T141038` includes the compact raid weapon card.
Read-only HTTP checks matched live JavaScript, CSS, catalog and all12 image files
against local hashes. Root then personally verified the actual IAB page: all ten
names/icons/counts, expanded Evidence disclosure, both native overview links and
the explicit38-contact/live-pending labels. `native-v5/forge-review-deployment.json`
records that root-reported browser check without inventing a screenshot path.

The combined focused name/art suite has70 passing checks. Ten new production
name previews pass the actual130-column predicate check with MyISAM world and
InnoDB Forge tables. The ten-name APPLY was NOT executed: automatic approval
review rejected the command because explicit production-name authorization was
required for the non-atomic batch. The owner approval question remains pending.
Names are still the original production names; no indirect rename is permitted
while that request is unanswered. The separately authorized ten full-art applies
already completed and are unaffected. No installed client patch change or Core
restart is implied by this web deployment or either review result.

## 2026-09-28 — approved names and build-only patch preparation

The owner has now approved the ten names. All ten production name-only applies
and after-inspections succeeded with129 other world columns preserved; the
`adoption-v2/name-apply-production-v1/index.json` receipt supersedes the preceding
approval-pending checkpoint. Runtime item reload/cache state is separate.

The explicit build-only unified patch is retained under
`raid-v3/unified-patch-candidate-v1`. SHA
`ef44bd3fb007d45760478109e7fb9d80f8a57d04a8e348e0eb25f0685b13827e`, 81,717,656 bytes.
The exact 30 reviewed art members match production adoption. All ten target DBC
rows preserve every field except the intended icon stem; no unrelated member or
semantic DBC row changed. No files were removed. Patch7 and ItemSet.dbc remain
unchanged. See the hash-bound verification report and reproduction script
`tools/equipment-workshop/verify_raid_unified_patch.py`.

This candidate is not installed. It preserves existing registry armor rather
than silently importing isolated raid-armor studies. No Core restart or normal
client-binary promotion was performed.

# Original Classic/TBC equipment — agent workflow

Working contract, 2026-09-28. Target MSUIClient; preserve Classic/TBC construction
and appearance. This is an agent-operated workflow. See the dated state in
[the raid handoff](EQUIPMENT_RAID_HANDOFF_2026-09-28.md),
[Weapon Forge](../WEAPON_FORGE.md), and [Armor Forge](../ARMOR_FORGE.md).
This procedure is not a claim that every current item has passed every gate.

For literal authoring and conversion instructions, use the
[local-model cookbook](EQUIPMENT_LOCAL_MODEL_COOKBOOK.md). It gives measured
triangle budgets, fixed atlas regions, original attachment coordinates and
licensed external-asset examples with retained failures and repairs.

## 1. Resume without losing work

Read both repositories' AGENTS.md, the current handoff, and the relevant Forge
design document. Use the locator before code discovery. Inspect dirty state;
leave existing work unstaged, retain failed evidence, and use a new revision
directory for each candidate. Do not create branches or in-tree backup copies.

Keep a ledger of source/package hashes, registry IDs, installed patch members,
normal client hashes, captured frames, review findings, and remaining gates.
Do not repeat completed registration, adoption, rename, or installation batches
because a later review is unfinished. Current weapon mutations are complete.
Historical rejected armor IDs are not the new raid sets.

Only one native capture/live client group may run at a time. Agree on ownership
when agents share the machine; await process exit before handing it over.
Use private capture settings and isolated builds/mounts during experiments.

## 2. Research and author within the construction rules

Use original MPQ assets through EquipmentReference. Bind model and texture
hashes, identify mesh versus painted detail, and examine whole original
ensembles as well as individual items. The reference cohorts and interpretation
are in [Custom Equipment Workshop](CUSTOM_EQUIPMENT_WORKSHOP.md).
Reference distributions describe the originals; a triangle count alone is
not an artistic acceptance test.

Write a specific silhouette/material brief. Keep editable source and export
provenance. Use geometry for meaningful silhouette, openings, and section
changes, with diffuse paint supplying most material and bevel detail.
Preserve the selected concept's identity during fitting.

Weapons use their ordinary grip/attachment rules. Helms and shoulders may have
original meshes. Other armor stays painted onto existing supported body atlas
regions and geosets. Do not deform character bodies, replace their skeletons,
or add race/theme offsets to conceal a defect.

Helms have explicit race/sex variants. If an original shoulder cannot fit a
body's motion without spoiling its design, author an explicit fitted mesh under
the optional [shoulder-fit contract](SHOULDER_FITS_EXTENSION.md). A missing
override uses the ordinary default independently for each side. One shared skin,
unchanged attachment matrices, and strict source/member/hash validation remain
mandatory. This extension still requires deployment and real-item live proof
before treating fitted packages as shipped.

## 3. Compile and inspect a private candidate

Use the existing strict authored Forge compiler and prototype tooling; consult
[ArmorPrototype](../tools/equipment-workshop/ArmorPrototype/README.md).
Validate every piece, declared path, material, UV, normal, triangle and texture
bound. Reopen the actual compiled M2/BLP and compare it to editable source.
Verify mips and alpha at native resolution. Body-only, dressed, and item-removal
views expose paint ownership and layering defects.

Review texture, neutral surface, contour/wireframe, silhouette and multiple
angles. For attachments inspect both internal joins and actual fastening to
the wearer. A connected wing or lens can still float above the shoulder.
Classify finite contacts by the actual body region, hair, ear, eye glow, helmet,
other custom parts and sleeves; do not treat a head-only zero count as whole-body
clearance. Ordinary camera occlusion is different from mesh penetration.

Start with the bodies/poses implicated by a repair. Once those pass, capture the
declared full matrix: all16 supported bodies, standing and required motion
samples, six views, and alternate appearances. Do not call a narrowly passing
prototype an all-body fit. Stop expanding a candidate with a real visible
defect; preserve it and repair its source.

Bind actual submitted positions/indices/materials, PNGs, pose/appearance,
selected fit/default paths, MPQ suppliers, and exact source/compiled hashes.
Enable weapon probes explicitly where required. Inspect the retained images
personally and write a separate review receipt. A technical pass never sets
cosmetic acceptance automatically.

For renderer changes also test default compatibility and simultaneous different
bodies sharing the real renderer/cache, with independent left/right selection.
Parser tests or cache-key tests alone do not establish this rendering behavior.

## 4. Register and install the reviewed bytes

Use the ordinary Forge registry and unified patch workflow after candidate
review. Keep source, staging preview, registered preview, compiled assets and
installed MPQ members tied to the same revision. Preserve unrelated template
columns, registry rows and patches. Take required backups outside the tree and
retain request/response, before/after hashes and exact-member verification.

Recheck normal-path capture after installation. Test the exact installed client
and patch rather than substituting private candidate evidence. The owner's
authorization covers necessary VMaNGOS/SuperUI restarts for this task; inspect
service identity, preserve rollback, verify startup/health and persistence.
Do not resurrect the superseded restart-approval block in the historical handoff.

Verify **both** Windows MSUIClient's Data directory and SuperUI's configured
`Vmangos:ClientDataPath` / `SpellCreator:ClientDataPath`; these may be separate
machines and files. Promote the same reviewed bytes with guarded backups, then
verify the app's actual opened MPQ snapshot, not only a disk filename. The
2026-09-29 example, `verify_superui_mpq_mount_v2.py`, binds 268 exact first-supplier
hits and decompressed sizes to accepted archive e607. Its fixed plan is evidence
for that revision; prepare a new bound plan/checker version for different assets.

Server ItemSet readiness compares the server DBC to `DBFilesClient\ItemSet.dbc`
inside the **current unified patch**. Require explicit comparison-known/match
and startup-known with no restart required. The old armor-only
`armor_forge_builds/ItemSet.dbc` must not replace a newer server DBC to satisfy a
stale badge. Final deployment and actual mount receipts are in the current
handoff; all recorded gates are complete, with historical live runtimes and
contact limits kept separate.

## 5. Verify real use with disposable QA actors

The current scoped actors are Forgeplateqa169 and Forgecastqa170. The separate
Commander raid remains paused. Before every equipment change, bind exact actor
identity, observed all19 equipment slots, ownership and proficiency. Preserve
starter/non-target equipment and do not clean up unrelated DB inconsistencies.

Current reusable live helpers are in
`scratch/equipment-batch/live-equipment/current-weapons-v1/`:

- `prepare_matrix.py` prepares self day/night/UI and remote day/night plans.
  It validates current observation/inventory and preserves all19 slots. New QA
  copies are allowed only in the first self-day phase; later phases reuse them.
- `run_guarded.ps1` binds normal DLL and patches before/after, runs private
  settings, and retains plan, launcher and process evidence. Await actual exit.
- `prepare_combat.py` prepares an adaptive session: observe a newly summoned
  temporary target, bind its exact full GUID, validate passive/idle/no-attack
  server state, then issue guarded attacks. Never reuse a stale target GUID.
  Inbox phases must be separately retained before append and bound afterward.

Review self and actual remote-player rendering, day/night outdoor lighting and
real WMO interior weight, movement, appropriate attack/cast/shoot and sheath
states. Require received server events for the correct actor and target near
the captured frame; sent attack commands alone are insufficient. An equipped
stowed wand does not prove the held/attack appearance.

Use ordinary live character-panel hover and observational `ui-parity` capture
for icons and tooltip text. Do not stage synthetic item UI. Technical draw
evidence must show the expected custom icon supplier and hovered slot, and the
actual PNG must show the approved name. `.save`, quit, relog, then bind visible
slots to saved item instances and their owners. A successful DB query alone
does not prove a visible saved item.

## 6. Publish an honest evidence record

Current evidence tools include:

- `tools/equipment-workshop/raid_weapon_verify_installed.py`: installed native
  source/member/submitted-mesh proof.
- `tools/equipment-workshop/audit_current_weapon_live_run.py`: planned identities,
  all19 slots, PNG/observation pairs, live actions/lighting and launcher binding.
- `tools/equipment-workshop/verify_live_weapon_ui.py`: actual icon drawing and
  hover evidence; personal name review remains a separate receipt.
- `tools/equipment-workshop/verify_item_guid_persistence.py`: the scoped allocator
  save/relog regression evidence.
- `tools/equipment-workshop/verify_live_weapon_combat.py`: exact received target
  events and cleanup, bound to the successful guarded run and adaptive inbox phases.
- `tools/equipment-workshop/verify_current_weapon_persistence.py`: all ten saved
  instances and owners through later real logins.
- `tools/equipment-workshop/summarize_current_weapon_live.py`: the complete180-frame
  live weapon ledger with separate personal review and disclosed native limitations.
- `tools/equipment-workshop/prepare_armor_qa_grants.py` and
  `audit_armor_qa_setup.py`: one-time missing-copy/capacity-bag setup, exact executed
  commands and saved-instance preservation. Inspect every existing DB reference
  before any grant; never repeat a successful grant to get a better screenshot.
- `tools/equipment-workshop/prepare_armor_review_capacity.py`: move one preserved
  old item into an existing bag when the removal test needs a free backpack slot.
  It grants/deletes nothing and requires the complete post-save ownership audit.
- `tools/equipment-workshop/prepare_armor_live.py` and `audit_armor_live_run.py`:
  eight-piece self/remote world, lighting, motion, actual UI and combat frame binding.
  Their equipped replacements use ordinary inventory swaps and retain prior gear.
- `tools/equipment-workshop/prepare_armor_ui_review_v2.py`: exact audited/original
  observation hashes, run/actor/timestamp/launch identity, all19 slots and eight
  unique parity manifests bound to full-size actual tooltips and64 icon draws.
  Its `corrective-binding.json` is technical evidence; personal text/art review
  remains pending until those exact images have been inspected. `--previous-review`
  proves retained PNG/JPEG identities without rewriting historical acceptance.
  The frozen `prepare_armor_ui_review.py` is retained for historical reproduction
  only. Glass/Gloam's completed V2 correction receipts are linked in the adaptive
  armor workflow below.
- [Adaptive armor combat](../tools/equipment-workshop/ARMOR_ADAPTIVE_SESSION.md):
  same-session observed target, separately guarded configuration/combat/cleanup
  phases, received hits and exact despawn. The generic appender supports both QA
  actors; the historical Glass-only appender remains untouched.
- [Armor removal/restoration](../tools/equipment-workshop/ARMOR_REMOVAL_LIVE.md):
  all eight pieces removed/restored through normal pickup/place gestures,48 views,
  actual7/8→8/8 set tooltips, and a complete saved GUID/owner/count comparison.

Keep technical, personal art review, installation, live and persistence states
separate. Record failed/unsupported cases explicitly. Reuse evidence only for
hash-identical members and the exact scope it proves. Update the Forge review
catalog and dated design notes after actual state changes. Finish with a concise
collection ledger linking accepted revisions and listing any remaining work.

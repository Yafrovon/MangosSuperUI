# Equipment evidence attachment — 2026-09-28

`POST /EquipmentEvidence/Attach`, multipart field `evidence`, accepts one JSON file:

```json
{
  "schemaVersion": 1,
  "summary": { "contents": "the native capture summary.json object" },
  "assets": [ "the native capture assets.json array entries" ]
}
```

The example illustrates the envelope; the service requires the actual known native
fields, not the placeholder entries above. Use
`python tools/equipment-workshop/bundle_native_evidence.py <capture-directory> <output.json>`
to build it. The Weapon Forge **Custom create** card has an **Attach offline native
capture evidence** section for submission and stored-report download.

Only `offline-production-character-renderer` is accepted. `inWorldVerified` must be
false and `visualReviewRequired` true. Unknown and duplicate fields, impossible capture
counts, invalid hashes, duplicate MPQ paths, path traversal and unsupported member
extensions are rejected before registry queries. Limits: 2 MiB input, 1,024 assets,
100,000 claimed captures, 128 MiB resolved comparison bytes, and 128 stored reports.
This is a known-schema attachment, not an arbitrary screenshot upload.

The service compares each supplied M2/MDX/BLP hash and byte length with
`CustomWeaponBuildService.TryGetMember` or `CustomArmorBuildService.TryGetMember`.
It never reads pre-extracted `wwwroot` assets. DBC members are explicitly `not-checked`
because these compiled equipment resolvers do not expose them. Unresolved members
are listed as `missing` with the explanation that they may be stock dependencies or
an unavailable registry query; this does **not** assert that the client lacks them.
An asset reported missing by the client cannot become matched just because registry
bytes exist. Matching members, mismatches, unresolved/reported-missing members and
unchecked DBCs retain their exact paths and claimed/current hashes and lengths.

Capture counts, generated time, archive supplier paths and body-matrix completeness
remain untrusted supplied claims. Only byte comparisons are server checked. The
registry resolvers cache for up to 30 seconds; a stored result is a snapshot of those
compiled bytes, not verification of an installed client patch or proof that the
capture run actually used them. Images, execution, pose coverage, clipping, art and
live-world behavior are not independently authenticated by this route.

The response is `{id,report,reportUrl}`. `id` is SHA-256 of the serialized report bytes.
The exact upload and report are retained under
`App_Data/equipment-evidence/<id>/input.json` and `report.json`. The report also includes
the SHA-256 of the exact uploaded bytes. `GET /EquipmentEvidence/Report?id=<id>` checks
the stored report against its hash before returning a JSON download. No item, asset,
patch, registry row or acceptance flag is changed. Report properties always retain
`runtimeVerified=false`, `inWorldVerified=false`, and `visualReviewRequired=true`.

Focused tests cover known weapon and armor byte matches, changed bytes, inconsistent
byte lengths, stock/DBC ambiguity, malformed JSON/counts, unsupported evidence kinds,
rejected live-world claims, input size, asset count, path safety, duplicate paths, and
reported-missing assets. HTTP persistence and real native-report attachment are separate
runtime checks after deployment.

## Real armor attachment — 2026-09-28

The eight full armor capture runs (1,568 frames) were bundled with their exact source-summary and asset-manifest hashes. Their 134 rendered custom model/texture members match the current registry with zero mismatches or missing members. The 32 inventory icons in the 166-member compiled packages are outside these character renders and outside this attachment. The bundle intentionally excludes stock dependencies, accompanying weapons and DBCs; it makes no live or artistic acceptance claim.

Stored report: `/EquipmentEvidence/Report?id=aa99e9cbb8ebeae380a18803010446255068362f4d71540795f7feed3bfa0f83`. Local input, source-manifest hashes and returned report are under `artifacts/equipment-workshop/armor-native-evidence/`.

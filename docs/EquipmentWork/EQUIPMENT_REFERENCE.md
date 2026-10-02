# Original equipment reference study

2026-09-28 — read-only corpus endpoints added. These report source measurements; they do not
certify visual quality, native-client fit, or a historical art budget. No packaging, item creation,
database mutation, patch rebuild, deployment, or extraction-directory input is involved.

All source members are read on demand from `LegacyImportSources.Vanilla` or `.Tbc` MPQ mounts.
The vanilla mount already restricts input to its stock archive allowlist through patch-2. The TBC
mount honors configured archive precedence. A lane label cannot prove an installation is pristine;
the response identifies the actual DBC, M2, and BLP inputs with SHA-256 hashes. The existing preview
service may write a derived content-addressed GLB output cache; it never supplies source assets.

## Endpoints

All routes are GET under `/EquipmentReference`. Sources are strictly `vanilla` and `tbc`; kinds
are strictly `weapon` and `armor`. Unknown values are rejected rather than falling back to TBC.

- `Browse?source=vanilla&kind=weapon&family=sword1h&search=&skip=0&take=40`: catalog page,
  available family keys, source provenance and limitations. Ordered by display ID then family.
  `take` is clamped to 1–100, `skip` to zero or above, and search to 200 characters.
- `Inspect?source=vanilla&kind=weapon&displayId=...&family=sword1h&raceGender=HuM&preview=true`:
  original models, textures, hashes, counts, bounds, DBC geoset/helmet fields and optional GLB.
  Specify family when the same armor display is shared by more than one equipment slot.
- `Measure?source=tbc&kind=weapon&family=sword2h&skip=0&take=40&raceGender=HuM`: a bounded
  page of inspections without GLB generation, plus per-family quantiles. Page through it to export
  larger studies; every page is self-describing JSON. Cancellation is checked between assets.
- `Texture?source=...&kind=...&displayId=...&family=...&raceGender=HuM&index=0`: PNG decoded
  directly from a resolved original BLP. Use returned `textures[].pngUrl`; indices refer to the
  deterministic path-sorted list, not an arbitrary file path. BLP2 decoding is limited to 4096 on
  either axis and four megapixels. Missing or unsupported assets remain explicit errors.

Unavailable mounts return 503; invalid requests 400; missing reference rows or textures 404;
unsupported texture data 422. An individual missing model/texture appears as an error in inspection
JSON so a partial reference cannot silently look complete.

## Reading the results

`models[].geometry` reports the first parsed M2 skin view's stored vertex/index counts. Triangle
count is index count / 3, not a count multiplied by material passes. It reports batches, submeshes,
bones, particle/ribbon emitter counts and exact distinct positions separately. Bounds are computed
from vertices, not trusted header bounds: `readerYUpBounds` uses the existing M2 reader's coordinate
frame; `wowZUpBounds` reverses that coordinate conversion to the source WoW frame. Non-finite
positions are counted and excluded from bounds. This is measurement, not geometry validation.

`geometrySha256` hashes exact ordered positions and indices. Recolors do not create a second
geometry observation. Reordered equivalent meshes can still produce different fingerprints;
this intentionally avoids claiming canonical geometric equivalence. Quantiles use linear
interpolation at `(count-1)*p` after deduplication within each family and requested page. They are
not random sampling, cross-page deduplication, whole-corpus statistics, or recommended limits.
Painted armor contributes no fictitious zero-triangle model to distributions.

Textures report alpha-weighted linear-sRGB relative luminance with transparent pixels excluded,
visible coverage and a normalized 16-bin histogram. These describe image values, not the final
material appearance. Texture slots retain M2 replaceable types and sampled-by-batch status.

## Armor and preview limits

TBC armor uses `ArmorImportSources.Tbc.Catalog` for named items, slots, material and set membership.
Vanilla's existing `VanillaItemCatalog` contains only weapons/shields, so original vanilla armor
is browsed directly from mounted `ItemDisplayInfo.dbc`. `helm` and `shoulder` are model-name
classifications; `body` means a raw display row with body component references. Such a row can be
a shared set template: it is not assigned an invented item slot, name or material. For `body`, all
its referenced components are shown; a real equipped piece may use only some of them.

Inspect chooses the requested original helmet race/gender variant without fallback. Shoulders
are returned as distinct left/right models; a missing side is not replaced by a mirrored copy.
Body components are shown for every available M/F/U/bare original variant. The mounted TBC
catalog can also expose capes and all supported painted slot families.

The source graph is preserved through `WeaponPreviewService`. GLB previews do not include the
display's separately mounted ItemVisual effects and are not native-client verification. The
original weapon index excludes distinct paired weapon models. Measurements therefore describe
this supported reference corpus, not every equipment asset ever shipped.

Validation: focused tests cover original/reader coordinate bounds, geometry fingerprints across
recolors versus shape changes, non-finite positions, quantile interpolation, transparent and
partial-alpha luminance, malformed pixel dimensions, and rejection of unsupported source lanes.
Original MPQ and live-client observations must be recorded separately after the endpoints run on
the configured runtime host.

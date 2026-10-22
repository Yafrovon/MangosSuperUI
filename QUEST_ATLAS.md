# Quest Atlas — design guiderail

A web page for reading, understanding and (later) shaping every quest in the world, laid out on a
tabletop relief model of the zone it lives in. Not a storyboard, not a quest helper: an author's
and cartographer's table.

## Status

- 2026-10-14 — Guiderail agreed in conversation; first slice written (relief compiler, zone quest
  data, tabletop page with layer toggles and quest text card). **Unbuilt and unverified.**
- 2026-10-14 (later) — First slice seen live on Westfall. Relief fixed: the reader's MCNK `BaseX`/`BaseY`
  are swapped (0x68 is world X; checked on Azeroth_29_51), the compiler now detects the order per tile.
  Added the World layers: ADT buildings (MODF) and props (MDDF) placed from `/QuestAtlas/Objects`,
  geometry from the World Editor's `/WorldEditor/WmoModel|DoodadModel`, true model scale on the
  exaggerated ground; creatures as pawn figurines from `/QuestAtlas/Creatures` (colour = quest NPC /
  service NPC / creature type). Global sidebar hide toggle in the top bar. **Unbuilt and unverified.**

- 2026-10-14 (later) — **Web/client split decided.** The client gets a Creator Mode "Quest Tabletop" swap
  that renders this table at full GPU speed with the real world and real creatures; spec in
  `MSUIClient/shared_docs/QUEST_TABLETOP.md` (unbuilt). This page stays the reading/authoring table
  (text, chains, rewards, links); **no further 3D investment here** (no web creature models). The
  shared contract is `/QuestAtlas/Zone` — keep its shape stable, extend it additively.

## What we agreed (2026-10-14 conversation)

1. **Audience now: author + cartographer, static.** The page reads the world DB and explains it.
   A *follow-along* view (players/bots moving through quests live) comes later as its own view on
   the same data; nothing here may assume the data is static forever.
2. **The look: a raised-relief tabletop map.** A miniature of the zone sitting on a table — our own
   compiled heightfield (from the client ADTs) painted with the minimap, exaggerated height,
   tilt-shift, a camera that sweeps over it like the map shot in a film. Quests are pieces on it.
3. **Layers like Google Maps / an Excel chart.** Everything we can derive is available as a layer;
   nothing is forced on screen. The user switches on what they are interested in right now.
4. **Text is a first-class layer.** The quest's written text (details, objectives, progress,
   completion) and the NPC lines spoken around it (quest start/end scripts → broadcast_text,
   quest greetings) are shown with the quest, not hidden in a DB grid.
5. **Scope is everything.** All ~4,450 Blizzard quests and our own (7,000,000+), same page.
6. **The client stays the primary builder** (anything spatial: placing NPCs, triggers, terrain).
   The web page is where the overload goes: chains, character lines, text, loot decisions.

## Guiderails (rules for every slice)

- **One source of truth for edits.** When the page starts editing, it writes through the same
  World Pack `wp_op` documents the client Quest Creator uses (`POST /WorldPacks/Content`), so edits
  are audited, undoable and owned by a pack. Never raw `UPDATE quest_template` from this page.
  Editing a stock (Blizzard) quest needs a deliberate decision first (pack-owned override rows).
- **Locations are derived, never stored.** 1.12 has no quest POI data. Every point on the table
  comes from spawns (`creature`, `gameobject`), relation tables, loot joins and area triggers.
- **Patch rows.** `quest_template`, `creature_template`, `gameobject_template` key on
  `(entry, patch)`; the atlas shows the highest patch row of each entry.
- **Assets from the MPQs on demand.** Heights from ADTs, paint from minimap BLPs, both through the
  existing readers; only an in-memory cache of the compiled relief.
- **Data is a graph.** Quests, NPCs, objects, items, triggers are nodes; give/end/kill/collect/
  drop/chain/exclusive/breadcrumb/speaks are edges. Views (table, threads, follow-along) are
  projections of that graph.

## Slice 1 (2026-10-14)

- `Services/QuestAtlas/ReliefService.cs` — compiles a 33×33 height grid per ADT tile (every 4th
  outer MCVT vertex, ~16.7 yd) plus per-chunk water level; cached in memory.
- `Services/QuestAtlas/QuestAtlasService.cs` — zone bundle: quests (ZoneOrSort = zone, or a giver
  standing on the board), all givers/enders (creature + gameobject), kill/object targets, item drop
  sources, explore triggers, chain links (incl. off-board stubs), text, start/end-script lines,
  quest greetings, rewards.
- `Controllers/QuestAtlasController.cs` — `Index`, `Zones`, `Zone?areaId=`, `Relief?mapId&gx&gy`.
- `Views/QuestAtlas/Index.cshtml` + `wwwroot/js/questatlas/` — three.js tabletop: relief slab,
  pieces (giver !, ender ?, targets, sources, triggers), chain arcs, layer toggles, level/faction
  filters, quest list, quest card with all text.

## Pending / ideas parked for later slices

- **Real creature models** instead of pawns: display_id → CreatureDisplayInfo → CreatureModelData → M2 with
  creature skins; humanoid NPCs need CreatureDisplayInfoExtra + baked NPC textures. The web app has no
  creature-model pipeline yet (the client does).
- **Hostile/friendly colouring** needs FactionTemplate on the page.
- Gameobjects (chests, herbs, signs) as models via GameObjectDisplayInfo.

- **Character threads view** (NPC lines through quests, xkcd-style) — second view on the same graph.
- **Follow-along view** — live players/bots on the table (SUI quest facts / bot STATE).
- **Level as height** (quests lifted by level; chains as staircases) — a layer toggle candidate.
- **Loot layer** — reward quality, per-class slot coverage, Lootifier variants.
- **Editing** through `wp_op` (text first, then chain links, then rewards).
- **patch-7 terrain** — the relief reads the stock client MPQs; pack-sculpted terrain (Gilneas)
  needs the published patch in the reader's chain to show.
- Subzones by AreaTable (today membership is ZoneOrSort or giver-on-board).

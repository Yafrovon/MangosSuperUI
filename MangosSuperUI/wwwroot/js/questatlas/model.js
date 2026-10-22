// Quest Atlas — the zone bundle as a small graph: quests, people, places, chains.
// Pure data; knows the board only through stage.onBoard().

const ALLIANCE = 1 | 4 | 8 | 64;          // human, dwarf, night elf, gnome
const HORDE = 2 | 16 | 32 | 128;          // orc, undead, tauren, troll
const MAX_PIECES_PER_ENTITY = 4;          // a giver standing in 40 places is a pattern, not 40 pins
const MAX_SOURCES_DRAWN = 6;

export class AtlasModel {
    constructor(bundle, stage) {
        this.b = bundle;
        this.stage = stage;
        this.zone = bundle.zone;
        this.quests = new Map(bundle.quests.map(q => [q.id, q]));
        this.stubs = new Map(bundle.stubs.map(s => [s.id, s]));
        this._spawnCache = new Map();

        // people index
        this.gives = new Map();   // "npc:123" → [qid]
        this.ends = new Map();
        for (const q of bundle.quests) {
            for (const g of q.givers) push(this.gives, key(g), q.id);
            for (const e of q.enders) push(this.ends, key(e), q.id);
        }

        this.#chains();
        // QuestLevel <= 0 means "scales with the player" (holiday/class quests): no level of its own.
        const lvls = bundle.quests.map(q => q.level).filter(l => l > 0);
        this.levelRange = lvls.length ? [Math.min(...lvls), Math.max(...lvls)] : [1, 60];
    }

    // ── entities ────────────────────────────────────────────────────────

    entity(kind, entry) {
        return kind === 'npc' ? this.b.npcs[entry] : this.b.objects[entry];
    }

    entityName(kind, entry) {
        return this.entity(kind, entry)?.name ?? `${kind} #${entry}`;
    }

    /** On-board spawns of an entity as [x, y, z] (world yards). */
    spawnsOnBoard(kind, entry) {
        const k = `${kind}:${entry}`;
        if (this._spawnCache.has(k)) return this._spawnCache.get(k);
        const e = this.entity(kind, entry);
        const pts = (e?.spawns ?? [])
            .filter(s => this.stage.onBoard(s[0], s[1], s[2]))
            .map(s => [s[1], s[2], s[3]]);
        this._spawnCache.set(k, pts);
        return pts;
    }

    placeOf(ref) {
        return this.spawnsOnBoard(ref.kind, ref.entry)[0] ?? null;
    }

    giverPlace(q) {
        for (const g of q.givers) { const p = this.placeOf(g); if (p) return p; }
        return null;
    }

    enderPlace(q) {
        for (const e of q.enders) { const p = this.placeOf(e); if (p) return p; }
        return null;
    }

    /** Pieces to stand on the table for one entity (a few spawns at most). */
    piecePlaces(kind, entry) {
        return this.spawnsOnBoard(kind, entry).slice(0, MAX_PIECES_PER_ENTITY);
    }

    /** What the quest sends you to do, with where. */
    objectiveTargets(q) {
        const out = [];
        for (const o of q.objectives) {
            if (o.kind === 'kill') {
                out.push({ kind: 'kill', label: `${verb(o)} ${o.count}× ${this.entityName('npc', o.id)}`, pts: this.spawnsOnBoard('npc', o.id) });
            } else if (o.kind === 'object') {
                out.push({ kind: 'object', label: `${verb(o)} ${this.entityName('object', o.id)}`, pts: this.spawnsOnBoard('object', o.id) });
            } else if (o.kind === 'item') {
                const item = this.b.items[o.id];
                const pts = [];
                for (const s of (item?.sources ?? []).slice(0, MAX_SOURCES_DRAWN))
                    pts.push(...this.spawnsOnBoard(s.kind, s.entry));
                out.push({ kind: 'collect', label: `Collect ${o.count}× ${item?.name ?? 'item #' + o.id}`, pts });
            } else if (o.kind === 'explore') {
                const t = this.b.triggers[o.id];
                if (t && this.stage.onBoard(t.map, t.x, t.y))
                    out.push({ kind: 'explore', label: `Explore ${t.name || 'area trigger ' + t.id}`, pts: [[t.x, t.y, t.z]], trigger: t });
                else out.push({ kind: 'explore', label: `Explore ${t?.name || 'area trigger ' + o.id}`, pts: [] });
            }
        }
        return out;
    }

    // ── classification ──────────────────────────────────────────────────

    faction(q) {
        const r = q.races;
        if (!r) return 'both';
        const a = (r & ALLIANCE) !== 0, h = (r & HORDE) !== 0;
        return a && !h ? 'alliance' : h && !a ? 'horde' : 'both';
    }

    isClassOrRace(q) {
        if (q.classes) return true;
        const r = q.races;
        return r && r !== ALLIANCE && r !== HORDE && r !== (ALLIANCE | HORDE);
    }

    searchText(q) {
        if (!q._search) {
            const names = [...q.givers, ...q.enders].map(r => this.entityName(r.kind, r.entry));
            const objs = q.objectives.map(o => o.kind === 'item' ? this.b.items[o.id]?.name
                : o.kind === 'kill' ? this.entityName('npc', o.id)
                : o.kind === 'object' ? this.entityName('object', o.id) : '');
            q._search = [q.title, q.id, ...names, ...objs].join(' ').toLowerCase();
        }
        return q._search;
    }

    // ── chains ──────────────────────────────────────────────────────────

    /** Directed links between quests (both ends may be off the board — then the other end is a stub). */
    links(q) {
        const out = [];
        const add = (from, to, type) => { if (from && to && from !== to) out.push({ from, to, type }); };
        if (q.prev) add(Math.abs(q.prev), q.id, 'chain');
        if (q.nextInChain) add(q.id, q.nextInChain, 'chain');
        if (q.next > 0) add(q.id, q.next, 'chain');
        if (q.breadcrumbFor) add(q.id, q.breadcrumbFor, 'breadcrumb');
        return out;
    }

    #chains() {
        // undirected components over chain links among quests on this board
        const parent = new Map();
        const find = x => { while (parent.get(x) !== x) { parent.set(x, parent.get(parent.get(x))); x = parent.get(x); } return x; };
        for (const id of this.quests.keys()) parent.set(id, id);
        const edges = new Map();
        for (const q of this.quests.values())
            for (const l of this.links(q)) {
                edges.set(`${l.from}>${l.to}:${l.type}`, l);
                if (this.quests.has(l.from) && this.quests.has(l.to)) parent.set(find(l.from), find(l.to));
            }
        this.edges = [...edges.values()];

        const groups = new Map();
        for (const id of this.quests.keys()) push(groups, find(id), id);

        // order each group by depth along links, then level
        const preds = new Map();
        for (const e of this.edges) if (this.quests.has(e.to)) push(preds, e.to, e.from);
        const depth = new Map();
        const depthOf = (id, seen = new Set()) => {
            if (depth.has(id)) return depth.get(id);
            if (seen.has(id)) return 0;
            seen.add(id);
            const p = (preds.get(id) ?? []).filter(x => this.quests.has(x));
            const d = p.length ? 1 + Math.max(...p.map(x => depthOf(x, seen))) : 0;
            depth.set(id, d);
            return d;
        };

        this.chains = [];
        this.chainOf = new Map();
        let hueIdx = 0;
        for (const ids of groups.values()) {
            ids.sort((a, b) => depthOf(a) - depthOf(b) || this.quests.get(a).level - this.quests.get(b).level || a - b);
            const color = ids.length > 1 ? hsl((hueIdx++ * 137.508) % 360, 62, 60) : '#d9c7a0';
            const chain = { ids, color, multi: ids.length > 1 };
            this.chains.push(chain);
            for (const id of ids) this.chainOf.set(id, chain);
        }
        this.chains.sort((a, b) => this.quests.get(a.ids[0]).level - this.quests.get(b.ids[0]).level || a.ids[0] - b.ids[0]);
        for (const id of this.quests.keys()) this.quests.get(id).depth = depthOf(id);
    }
}

export const key = r => `${r.kind}:${r.entry}`;

function push(map, k, v) {
    const a = map.get(k);
    if (a) { if (!a.includes(v)) a.push(v); } else map.set(k, [v]);
}

function verb(o) {
    if (o.spell) return 'Use spell on';
    return o.kind === 'object' ? 'Use' : 'Slay';
}

function hsl(h, s, l) {
    s /= 100; l /= 100;
    const k = n => (n + h / 30) % 12;
    const a = s * Math.min(l, 1 - l);
    const f = n => l - a * Math.max(-1, Math.min(k(n) - 3, Math.min(9 - k(n), 1)));
    const to = x => Math.round(x * 255).toString(16).padStart(2, '0');
    return `#${to(f(0))}${to(f(8))}${to(f(4))}`;
}

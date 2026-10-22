// Quest Atlas — page glue: zone picker, filters, quest list, layers, quest/NPC card.
// Design guiderail: QUEST_ATLAS.md (repo root).

import { Stage, TILE } from './stage.js';
import { AtlasModel } from './model.js';
import { Pieces } from './pieces.js';
import { WorldDressing } from './world.js';

const $ = id => document.getElementById(id);
const esc = s => String(s ?? '').replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]));

const LAYERS = [
    { group: 'People' },
    { key: 'givers', label: 'Quest givers  !', swatch: '#f2c14e', on: true },
    { key: 'enders', label: 'Turn-ins  ?', swatch: '#f2c14e', on: true },
    { key: 'labels', label: 'Names', on: false },
    { group: 'Story' },
    { key: 'chains', label: 'Chain arcs', swatch: '#7fb8e8', on: true },
    { key: 'journey', label: 'Selected quest journey', swatch: '#fff4d6', on: true },
    { group: 'Objectives' },
    { key: 'kill', label: 'Kill targets', swatch: '#e0614f', on: false },
    { key: 'collect', label: 'Item sources', swatch: '#e8a33a', on: false },
    { key: 'objects', label: 'Objects to use', swatch: '#6fb3e0', on: false },
    { key: 'explore', label: 'Places to explore', swatch: '#b48be0', on: true },
    { group: 'Level' },
    { key: 'levelTint', label: 'Tint by level', on: false },
    { key: 'levelLift', label: 'Lift by level (height = level)', on: false },
    { group: 'World' },
    { key: 'buildings', label: 'Buildings', on: true },
    { key: 'props', label: 'Trees & props', on: true },
    { key: 'figures', label: 'Creatures (figurines)', swatch: '#c24a3a', on: true },
    { key: 'critters', label: 'Include critters', on: false },
    { slider: 'figureScale', label: 'Figures', min: 1, max: 6, step: 0.25, value: 2.5 },
    { group: 'Table' },
    { key: 'water', label: 'Water', on: true },
    { key: 'miniature', label: 'Miniature lens (tilt-shift)', on: true },
    { slider: 'exaggeration', label: 'Relief', min: 1, max: 5, step: 0.1, value: 2.2 }
];

const state = {
    zones: [],
    model: null,
    visible: new Set(),
    sel: { quest: null, entity: null },
    filters: { search: '', faction: 'all', lvMin: 1, lvMax: 70, homeOnly: false, classQuests: true },
    layers: Object.fromEntries(LAYERS.filter(l => l.key).map(l => [l.key, l.on])),
    exaggeration: 2.2,
    figureScale: 2.5,
    loadToken: 0
};
restorePrefs();

const stage = new Stage($('qaCanvas'), {
    onHover: hover,
    onPick: pick,
    onGround: w => { $('qaHud').textContent = w ? `x ${w.x.toFixed(1)}   y ${w.y.toFixed(1)}   z ${w.z.toFixed(1)}` : ''; }
});
const pieces = new Pieces(stage);
const world = new WorldDressing(stage);
stage.extraPick = ray => world.pickFigure(ray);
world.figureScale = state.figureScale;
world.showCritters = state.layers.critters;
world.setBuildings(state.layers.buildings);
world.setProps(state.layers.props);
world.setFigures(state.layers.figures);
stage.setExaggeration(state.exaggeration);
stage.setMiniature(state.layers.miniature);

buildLayersPanel();
wireFilters();
init();

// ── boot ────────────────────────────────────────────────────────────────

async function init() {
    state.zones = await fetch('/QuestAtlas/Zones').then(r => r.json());
    const sel = $('qaZone');
    const groups = { 0: 'Eastern Kingdoms', 1: 'Kalimdor' };
    const byMap = new Map();
    for (const z of state.zones) {
        const g = groups[z.mapId] ?? 'Elsewhere';
        if (!byMap.has(g)) byMap.set(g, []);
        byMap.get(g).push(z);
    }
    sel.innerHTML = '<option value="">Choose a zone…</option>' + [...byMap].map(([g, zs]) =>
        `<optgroup label="${esc(g)}">${zs.map(z =>
            `<option value="${z.areaId}">${esc(z.name)}  ·  ${z.minLevel}–${z.maxLevel}  ·  ${z.quests}q${z.areaId >= 7000 ? '  ✦' : ''}</option>`).join('')}</optgroup>`).join('');
    sel.addEventListener('change', () => sel.value && loadZone(+sel.value));

    const h = readHash();
    const start = h.zone || pref('zone');
    if (start && state.zones.some(z => z.areaId === +start)) {
        sel.value = start;
        loadZone(+start, h.quest ? +h.quest : null);
    }
}

async function loadZone(areaId, selectQuest = null) {
    const token = ++state.loadToken;
    const zoneInfo = state.zones.find(z => z.areaId === areaId);
    $('qaZone').value = String(areaId);
    veil(true, zoneInfo?.name ?? `Area ${areaId}`, 'Gathering quests…', 0);
    closeCard();
    world.clear();
    state.sel = { quest: null, entity: null };

    const bundle = await fetch(`/QuestAtlas/Zone?areaId=${areaId}`).then(r => r.json());
    if (token !== state.loadToken) return;
    if (bundle.error) { veil(true, 'Not found', bundle.error, 0); return; }

    const rect = bundle.zone.rect ?? deriveRect(bundle);
    if (!rect) { veil(true, bundle.zone.name, 'No quests with a place in the world here.', 0); return; }

    veil(true, bundle.zone.name, 'Raising the land…', 0);
    await stage.loadBoard(bundle.zone.mapId, rect, f => veil(true, bundle.zone.name, 'Raising the land…', f));
    if (token !== state.loadToken) return;

    stage.setWater(state.layers.water);
    state.model = new AtlasModel(bundle, stage);
    setPref('zone', areaId);
    const [lo, hi] = state.model.levelRange;
    $('qaZoneMeta').textContent =
        `${bundle.quests.length} quests · levels ${lo}–${hi} · ${Object.keys(bundle.npcs).length} NPCs` +
        (bundle.zone.rect ? '' : ' · board framed on spawns (no world-map rect)');

    refresh();
    veil(false);
    stage.intro();
    dressTable(token);
    if (selectQuest && state.model.quests.has(selectQuest)) setTimeout(() => selectQuestById(selectQuest), 1800);
}

/** Buildings, props and figurines arrive after the table is up — the board is usable meanwhile. */
async function dressTable(token) {
    const chip = $('qaLoad');
    const show = t => { chip.textContent = t; chip.hidden = !t; };
    const figures = world.loadCreatures(stage.board, state.model);
    await world.load(stage.board, p => {
        if (token !== state.loadToken) return;
        show(p.done < p.total ? `Placing the world… ${p.done}/${p.total} models · ${p.buildings} buildings · ${p.props} props` : '');
    });
    await figures;
    if (token === state.loadToken) show('');
}

/** Zones without a WorldMapArea row: frame the board on where its people stand. */
function deriveRect(bundle) {
    const pts = [];
    const map = bundle.zone.mapId;
    for (const q of bundle.quests)
        for (const r of [...q.givers, ...q.enders]) {
            const e = r.kind === 'npc' ? bundle.npcs[r.entry] : bundle.objects[r.entry];
            for (const s of e?.spawns ?? []) if (s[0] === map) pts.push(s);
        }
    if (!pts.length) return null;
    const xs = pts.map(p => p[1]), ys = pts.map(p => p[2]);
    const pad = 250;
    let top = Math.max(...xs) + pad, bottom = Math.min(...xs) - pad;
    let left = Math.max(...ys) + pad, right = Math.min(...ys) - pad;
    // keep it at least a tile across so the table reads as land
    const grow = (hi, lo) => { const d = TILE - (hi - lo); return d > 0 ? [hi + d / 2, lo - d / 2] : [hi, lo]; };
    [top, bottom] = grow(top, bottom);
    [left, right] = grow(left, right);
    return { top, bottom, left, right };
}

// ── filtering + list ────────────────────────────────────────────────────

function refresh() {
    const m = state.model;
    if (!m) return;
    const f = state.filters;
    const term = f.search.trim().toLowerCase();
    state.visible = new Set();
    for (const q of m.quests.values()) {
        if (q.level > 0 && (q.level < f.lvMin || q.level > f.lvMax)) continue;
        if (f.faction !== 'all') { const fa = m.faction(q); if (fa !== 'both' && fa !== f.faction) continue; }
        if (f.homeOnly && !q.home) continue;
        if (!f.classQuests && m.isClassOrRace(q)) continue;
        if (term && !m.searchText(q).includes(term)) continue;
        state.visible.add(q.id);
    }
    if (state.sel.quest != null) state.visible.add(state.sel.quest);   // never hide what you're reading
    renderList();
    redraw();
}

function redraw() {
    if (state.model) pieces.rebuild(state.model, state.visible, state.layers, state.sel);
}

function renderList() {
    const m = state.model;
    let html = '', shown = 0;
    for (const ch of m.chains) {
        const ids = ch.ids.filter(id => state.visible.has(id));
        if (!ids.length) continue;
        shown += ids.length;
        const rows = ids.map(id => questRow(m.quests.get(id))).join('');
        if (ch.multi) {
            html += `<div class="qa-chain"><div class="qa-chain-head"><span class="qa-chain-swatch" style="background:${ch.color}"></span>${esc(m.quests.get(ch.ids[0]).title)} · ${ch.ids.length} steps</div>${rows}</div>`;
        } else html += rows;
    }
    $('qaList').innerHTML = html || '<div class="qa-empty" style="padding:8px">Nothing matches these filters.</div>';
    $('qaCount').textContent = `${shown} of ${m.quests.size} quests`;
}

function questRow(q) {
    const tags = [];
    if (q.custom) tags.push('<span class="qa-q-tag">ours</span>');
    if (state.model.isClassOrRace(q)) tags.push('<span class="qa-q-tag" style="color:#8cc46a;border-color:rgba(140,196,106,.4)">class/race</span>');
    return `<div class="qa-q${q.home ? '' : ' visitor'}${state.sel.quest === q.id ? ' sel' : ''}" data-q="${q.id}">
        <span class="qa-q-lv">${lv(q.level)}</span><span class="qa-q-title">${esc(q.title)}</span>${tags.join('')}</div>`;
}

// ── selection ───────────────────────────────────────────────────────────

function selectQuestById(id, { fly = true } = {}) {
    const m = state.model;
    if (!m?.quests.has(id)) return;
    state.sel = { quest: id, entity: null };
    state.visible.add(id);
    writeHash();
    renderList();
    redraw();
    renderQuestCard(m.quests.get(id));
    $(`qaList`).querySelector(`[data-q="${id}"]`)?.scrollIntoView({ block: 'nearest' });
    if (fly) stage.frame(pieces.focusPoints(m, state.sel));
}

function selectEntity(kind, entry) {
    state.sel = { quest: null, entity: `${kind}:${entry}` };
    writeHash();
    renderList();
    redraw();
    renderEntityCard(kind, entry);
    stage.frame(pieces.focusPoints(state.model, state.sel));
}

function clearSelection() {
    state.sel = { quest: null, entity: null };
    writeHash();
    closeCard();
    refresh();
}

function pick(p) {
    if (!p) return;
    if (p.type === 'piece') {
        const qs = [...new Set([...p.gives, ...p.ends])];
        if (qs.length === 1) selectQuestById(qs[0]);
        else selectEntity(p.kind, p.entry);
    } else if (p.type === 'trigger') {
        const q = [...state.model.quests.values()].find(q => q.objectives.some(o => o.kind === 'explore' && o.id === p.id));
        if (q) selectQuestById(q.id);
    } else if (p.type === 'figure') {
        const k = `npc:${p.entry}`;
        if (state.model.gives.has(k) || state.model.ends.has(k)) selectEntity('npc', p.entry);
        else renderFigureCard(p);
    }
}

function hover(p, px, py) {
    const el = $('qaHover');
    if (!p || !state.model) { el.hidden = true; return; }
    const m = state.model;
    let html = '';
    if (p.type === 'piece') {
        const e = m.entity(p.kind, p.entry);
        html = `<b>${esc(e?.name ?? '?')}</b>${e?.subname ? ` &lt;${esc(e.subname)}&gt;` : ''}`;
        const parts = [];
        if (p.gives.length) parts.push(`gives ${p.gives.length}`);
        if (p.ends.length) parts.push(`takes ${p.ends.length}`);
        html += `<small>${parts.join(' · ')}${p.gives.length === 1 ? ' — ' + esc(m.quests.get(p.gives[0])?.title) : ''}</small>`;
    } else if (p.type === 'trigger') {
        const t = state.model.b.triggers[p.id];
        html = `<b>${esc(t?.name || 'Area trigger')}</b><small>explore · trigger ${p.id}</small>`;
    } else if (p.type === 'figure') {
        const wanted = questsWanting(p.entry).length;
        html = `<b>${esc(p.name)}</b>${p.subname ? ` &lt;${esc(p.subname)}&gt;` : ''}` +
            `<small>Lv ${levelText(p)} · ${esc(p.creatureType)}${rankText(p.rank)}${wanted ? ` · wanted by ${wanted} quest${wanted > 1 ? 's' : ''}` : ''}</small>`;
    }
    el.innerHTML = html;
    el.style.left = `${px}px`;
    el.style.top = `${py}px`;
    el.hidden = false;
}

// ── cards ───────────────────────────────────────────────────────────────

function openCard(html) {
    $('qaCard').innerHTML = html;
    $('qaRight').hidden = false;
    $('qaStage').classList.add('card-open');
}

function closeCard() {
    $('qaRight').hidden = true;
    $('qaStage').classList.remove('card-open');
}

function entLink(kind, entry) {
    const m = state.model;
    const on = m.spawnsOnBoard(kind, entry).length > 0;
    return `<span class="qa-ent${on ? '' : ' off'}" data-ent="${kind}:${entry}" title="${on ? 'Show on the table' : 'Not on this board'}">${esc(m.entityName(kind, entry))}</span>`;
}

function questLink(id) {
    const m = state.model;
    const q = m.quests.get(id);
    if (q) return `<span class="qa-ent" data-q="${id}">${esc(q.title)}</span>`;
    const s = m.stubs.get(id);
    if (s) return `<span class="qa-ent off" data-goto="${id}" data-zone="${s.topZone}" title="${esc(s.zoneName ?? '')}">${esc(s.title)} <small>(${esc(s.zoneName ?? 'elsewhere')})</small></span>`;
    return `<span class="qa-ent off">#${id}</span>`;
}

function renderQuestCard(q) {
    const m = state.model;
    const badges = [q.level > 0 ? `Lv ${q.level}` : 'scales to player', `req ${q.minLevel}`];
    const fa = m.faction(q);
    if (fa !== 'both') badges.push(fa === 'alliance' ? 'Alliance' : 'Horde');
    if (q.classes) badges.push('class quest');
    if (m.isClassOrRace(q) && !q.classes) badges.push('race quest');
    if (q.type === 1) badges.push('group');
    if (q.type === 81) badges.push('dungeon');
    if (q.type === 62) badges.push('raid');
    if (q.suggestedPlayers > 1) badges.push(`${q.suggestedPlayers} players`);
    if (q.specialFlags & 1) badges.push('repeatable');
    if (q.limitTime) badges.push(`timed ${Math.round(q.limitTime / 60)}m`);
    if (!q.home) badges.push('filed elsewhere');
    const badgeHtml = badges.map(b => `<span class="qa-badge">${esc(b)}</span>`).join('') +
        (q.custom ? '<span class="qa-badge custom">ours ✦</span>' : '') +
        (!q.givers.length ? '<span class="qa-badge warn">no giver</span>' : '') +
        (!q.enders.length ? '<span class="qa-badge warn">no turn-in</span>' : '');

    const people = `<dl class="qa-people">
        <dt>From</dt><dd>${q.givers.map(r => entLink(r.kind, r.entry)).join(', ') || '<span class="qa-empty">—</span>'}${q.srcItem ? ` · starts from item ${esc(m.b.items[q.srcItem]?.name ?? q.srcItem)}` : ''}</dd>
        <dt>To</dt><dd>${q.enders.map(r => entLink(r.kind, r.entry)).join(', ') || '<span class="qa-empty">—</span>'}</dd>
    </dl>`;

    // chain bar: what comes before, after, beside
    const before = m.edges.filter(e => e.to === q.id).map(e => questLink(e.from) + (e.type === 'breadcrumb' ? ' <small>(breadcrumb)</small>' : ''));
    const after = m.edges.filter(e => e.from === q.id).map(e => questLink(e.to) + (e.type === 'breadcrumb' ? ' <small>(breadcrumb)</small>' : ''));
    const mates = q.exclusiveGroup ? [...m.quests.values(), ...m.stubs.values()]
        .filter(o => o.id !== q.id && (o.exclusiveGroup === q.exclusiveGroup)).map(o => questLink(o.id)) : [];
    let chain = '';
    if (before.length || after.length || q.exclusiveGroup) {
        chain = `<div class="qa-chainbar">${before.length ? before.join(' · ') + ' →' : ''} <span class="now">this</span> ${after.length ? '→ ' + after.join(' · ') : ''}</div>`;
        if (q.exclusiveGroup) chain += `<div class="qa-chainbar">${q.exclusiveGroup > 0 ? 'Only one of' : 'All of'} group ${Math.abs(q.exclusiveGroup)}: ${mates.join(' · ') || '<span class="qa-empty">(none on this board)</span>'}</div>`;
    }

    const lines = voicesFor(q);
    const tabs = [
        { key: 'story', label: 'Story', html: prose(q.text.details) },
        { key: 'goal', label: 'Objectives', html: prose(q.text.objectives) + objectiveList(q) },
        { key: 'progress', label: 'Progress', html: prose(q.text.requestItems) },
        { key: 'complete', label: 'Completion', html: prose(q.text.offerReward) + (q.text.end ? `<div class="qa-section">Log text when done</div>${prose(q.text.end)}` : '') },
        { key: 'voices', label: 'Voices', n: lines.count, html: lines.html },
        { key: 'rewards', label: 'Rewards', html: rewardsHtml(q) }
    ];
    const active = pref('cardTab') || 'story';

    openCard(`
        <div class="qa-card-kicker">Quest ${q.id}${q.patch ? ` · patch row ${q.patch}` : ''} · ${esc(zoneLabel(q))}</div>
        <div class="qa-card-title">${esc(q.title)}</div>
        <div class="qa-badges">${badgeHtml}</div>
        ${people}${chain}
        <div class="qa-tabs">${tabs.map(t => `<button class="qa-tab${t.key === active ? ' on' : ''}" data-tab="${t.key}">${t.label}${t.n ? `<span class="n">${t.n}</span>` : ''}</button>`).join('')}</div>
        ${tabs.map(t => `<div class="qa-tabpane" data-pane="${t.key}"${t.key === active ? '' : ' hidden'}>${t.html}</div>`).join('')}`);
}

const lv = l => l > 0 ? l : '·';

function zoneLabel(q) {
    const z = state.model.zone;
    if (q.home) return z.name;
    if (q.zoneOrSort < 0) return `sort ${q.zoneOrSort}`;
    return `filed under area ${q.zoneOrSort}`;
}

function objectiveList(q) {
    const m = state.model;
    const icon = { kill: '⚔', object: '⚙', item: '✋', explore: '⌖', reputation: '★' };
    const items = q.objectives.map(o => {
        let main = '', sub = '';
        if (o.kind === 'kill') {
            main = `${o.spell ? 'Use spell on' : 'Slay'} ${o.count}× ${entLink('npc', o.id)}`;
            sub = `${m.spawnsOnBoard('npc', o.id).length} on this board`;
        } else if (o.kind === 'object') {
            main = `Use ${o.count > 1 ? o.count + '× ' : ''}${entLink('object', o.id)}`;
            sub = `${m.spawnsOnBoard('object', o.id).length} on this board`;
        } else if (o.kind === 'item') {
            const it = m.b.items[o.id];
            main = `Collect ${o.count}× <span class="q${it?.quality ?? 1}">${esc(it?.name ?? '#' + o.id)}</span>`;
            const src = (it?.sources ?? []).slice(0, 6).map(s => `${entLink(s.kind, s.entry)} ${s.chance}%`);
            sub = src.length ? 'from ' + src.join(', ') + ((it.sources.length > 6) ? ` +${it.sources.length - 6} more` : '') : 'no drop source found (vendor, crafted or scripted)';
        } else if (o.kind === 'explore') {
            const t = m.b.triggers[o.id];
            main = `Explore ${esc(t?.name || 'area trigger ' + o.id)}`;
            sub = t ? `trigger ${t.id} · radius ${Math.round(t.radius || Math.max(t.boxX, t.boxY) / 2)} yd` : '';
        } else if (o.kind === 'reputation') {
            main = `Reach ${o.count} reputation with faction ${o.id}`;
        }
        const text = o.text ? `<div class="qa-obj-sub">“${esc(o.text)}”</div>` : '';
        return `<li><span class="qa-obj-kind">${icon[o.kind] ?? '•'}</span><div>${main}${text}${sub ? `<div class="qa-obj-sub">${sub}</div>` : ''}</div></li>`;
    });
    if (q.srcSpell) items.push(`<li><span class="qa-obj-kind">✧</span><div>Casts spell ${q.srcSpell} on accept</div></li>`);
    return items.length ? `<ul class="qa-objs">${items.join('')}</ul>` : '<div class="qa-obj-sub" style="margin-top:10px">No tracked objectives — a talk-to or delivery quest.</div>';
}

function voicesFor(q) {
    const m = state.model;
    const out = [];
    const giverName = q.givers[0] ? m.entityName(q.givers[0].kind, q.givers[0].entry) : 'Giver';
    const enderName = q.enders[0] ? m.entityName(q.enders[0].kind, q.enders[0].entry) : 'Ender';

    for (const r of q.givers) {
        const g = m.b.greetings.find(x => x.kind === r.kind && x.entry === r.entry);
        if (g?.text) out.push(voice(`${m.entityName(r.kind, r.entry)} · greeting`, g.text, 'say'));
    }
    const lineSet = (phase, script, who) => m.b.lines.filter(l => l.phase === phase && l.script === script).forEach(l => {
        const speaker = l.targetParam > 0 && m.b.npcs[l.targetParam] ? m.b.npcs[l.targetParam].name : who;
        if (l.command === 0 && l.text) {
            const how = { 1: 'yells', 2: 'emote', 3: 'emote', 4: 'whispers', 6: 'whispers' }[l.chatType] ?? 'says';
            out.push(voice(`${speaker} ${how} · ${phase === 'start' ? 'on accept' : 'on turn-in'}${l.delay ? ` +${l.delay}s` : ''}`, l.text, how === 'emote' ? 'emote' : 'say'));
        } else if (l.comments) {
            out.push(`<div class="qa-voice"><div class="qa-voice-who">stage direction${l.delay ? ` +${l.delay}s` : ''}</div><div class="qa-obj-sub">${esc(l.comments)}</div></div>`);
        }
    });
    if (q.startScript) lineSet('start', q.startScript, giverName);
    if (q.completeScript) lineSet('end', q.completeScript, enderName);
    const count = out.length;
    return { count, html: count ? out.join('') : '<div class="qa-empty">No one says anything scripted around this quest. Its voice is the quest text itself.</div>' };
}

function voice(who, text, cls) {
    return `<div class="qa-voice ${cls}"><div class="qa-voice-who">${esc(who)}</div><div class="qa-prose">${fmt(text)}</div></div>`;
}

function rewardsHtml(q) {
    const m = state.model;
    const r = q.rewards;
    const item = it => { const i = m.b.items[it.id]; return `<li><span class="q${i?.quality ?? 1}">${esc(i?.name ?? '#' + it.id)}</span>${it.count > 1 ? ` ×${it.count}` : ''}</li>`; };
    let h = '';
    if (r.choice.length) h += `<div class="qa-section">Choose one</div><ul class="qa-rew">${r.choice.map(item).join('')}</ul>`;
    if (r.items.length) h += `<div class="qa-section">You also receive</div><ul class="qa-rew">${r.items.map(item).join('')}</ul>`;
    const misc = [];
    if (r.money > 0) misc.push(money(r.money));
    if (r.money < 0) misc.push(`costs ${money(-r.money)}`);
    if (r.xp) misc.push(`${r.xp} xp`);
    if (r.spell) misc.push(`teaches spell ${r.spell}`);
    if (r.spellCast) misc.push(`casts spell ${r.spellCast}`);
    if (misc.length) h += `<div class="qa-section">Also</div><div>${misc.join(' · ')}</div>`;
    if (r.rep.length) h += `<div class="qa-section">Reputation</div><div>${r.rep.map(x => `faction ${x.faction}: ${x.value > 0 ? '+' : ''}${x.value}`).join('<br>')}</div>`;
    return h || '<div class="qa-empty">No rewards.</div>';
}

function money(c) {
    const g = Math.floor(c / 10000), s = Math.floor(c / 100) % 100, k = c % 100;
    return [g && `${g}g`, s && `${s}s`, k && `${k}c`].filter(Boolean).join(' ');
}

function renderEntityCard(kind, entry) {
    const m = state.model;
    const e = m.entity(kind, entry);
    const k = `${kind}:${entry}`;
    const gives = m.gives.get(k) ?? [], ends = m.ends.get(k) ?? [];
    const greet = m.b.greetings.find(x => x.kind === kind && x.entry === entry);
    const list = ids => ids.map(id => m.quests.get(id)).sort((a, b) => a.level - b.level)
        .map(q => `<div class="qa-q" data-q="${q.id}"><span class="qa-q-lv">${lv(q.level)}</span><span class="qa-q-title">${esc(q.title)}</span></div>`).join('');
    openCard(`
        <div class="qa-card-kicker">${kind === 'npc' ? 'NPC' : 'Object'} ${entry}${e?.spawnTotal ? ` · ${e.spawnTotal} spawn${e.spawnTotal > 1 ? 's' : ''}` : ''}</div>
        <div class="qa-card-title">${esc(e?.name ?? '?')}</div>
        <div class="qa-badges">${e?.subname ? `<span class="qa-badge">&lt;${esc(e.subname)}&gt;</span>` : ''}${e?.levelMin ? `<span class="qa-badge">Lv ${e.levelMin}${e.levelMax > e.levelMin ? '–' + e.levelMax : ''}</span>` : ''}${entry >= 7000000 ? '<span class="qa-badge custom">ours ✦</span>' : ''}</div>
        ${greet?.text ? voice('greeting', greet.text, 'say') : ''}
        ${gives.length ? `<div class="qa-section">Gives</div>${list(gives)}` : ''}
        ${ends.length ? `<div class="qa-section">Takes in</div>${list(ends)}` : ''}`);
}

/** Quests on this board that send you after a creature: to kill it, or for something it drops. */
function questsWanting(entry) {
    const m = state.model;
    if (!m) return [];
    const out = [];
    for (const q of m.quests.values())
        for (const o of q.objectives) {
            if (o.kind === 'kill' && o.id === entry) { out.push({ q, why: `slay ${o.count}` }); break; }
            if (o.kind === 'item' && (m.b.items[o.id]?.sources ?? []).some(s => s.kind === 'npc' && s.entry === entry)) {
                out.push({ q, why: `drops ${m.b.items[o.id].name}` });
                break;
            }
        }
    return out.sort((a, b) => a.q.level - b.q.level);
}

const levelText = p => p.levelMax > p.levelMin ? `${p.levelMin}–${p.levelMax}` : `${p.levelMin}`;
const rankText = r => ({ 1: ' · elite', 2: ' · rare elite', 3: ' · boss', 4: ' · rare' }[r] ?? '');

function renderFigureCard(p) {
    const wanted = questsWanting(p.entry);
    state.sel = { quest: null, entity: null };
    writeHash();
    redraw();
    openCard(`
        <div class="qa-card-kicker">Creature ${p.entry} · ${esc(p.creatureType)}${rankText(p.rank)}</div>
        <div class="qa-card-title">${esc(p.name)}</div>
        <div class="qa-badges">${p.subname ? `<span class="qa-badge">&lt;${esc(p.subname)}&gt;</span>` : ''}<span class="qa-badge">Lv ${levelText(p)}</span>${p.entry >= 7000000 ? '<span class="qa-badge custom">ours ✦</span>' : ''}</div>
        ${wanted.length
            ? `<div class="qa-section">Wanted by</div>${wanted.map(w => `<div class="qa-q" data-q="${w.q.id}"><span class="qa-q-lv">${lv(w.q.level)}</span><span class="qa-q-title">${esc(w.q.title)} <small class="qa-obj-sub">— ${esc(w.why)}</small></span></div>`).join('')}`
            : '<div class="qa-empty">No quest on this board asks for this creature.</div>'}
        <div class="qa-section">Standing at</div><div class="qa-obj-sub">x ${p.x.toFixed(1)} · y ${p.y.toFixed(1)} · z ${p.z.toFixed(1)}</div>`);
}

// ── text ────────────────────────────────────────────────────────────────

/** Quest text: escape, then show the client's substitution tokens as tokens. */
function fmt(t) {
    return esc(t)
        .replace(/\$[Gg]\s*([^:;]*):([^;]*);/g, (_, a, b) => `<span class="qa-tok">${a.trim()}/${b.trim()}</span>`)
        .replace(/\$[Nn]/g, '<span class="qa-tok">‹name›</span>')
        .replace(/\$[Cc]/g, '<span class="qa-tok">‹class›</span>')
        .replace(/\$[Rr]/g, '<span class="qa-tok">‹race›</span>')
        .replace(/\$[Bb]/g, '<br>');
}

function prose(t) {
    if (!t || !t.trim()) return '<div class="qa-empty">(empty)</div>';
    return `<div class="qa-prose">${fmt(t).split(/(?:<br>\s*){2,}/).map(p => `<p>${p}</p>`).join('')}</div>`;
}

// ── panels + wiring ─────────────────────────────────────────────────────

function buildLayersPanel() {
    const body = $('qaLayersBody');
    body.innerHTML = LAYERS.map(l => {
        if (l.group) return `<div class="qa-layer-group">${l.group}</div>`;
        if (l.slider) return `<label class="qa-slider">${l.label}<input type="range" data-slider="${l.slider}" min="${l.min}" max="${l.max}" step="${l.step}" value="${state[l.slider]}"></label>`;
        return `<label class="qa-layer"><input type="checkbox" data-layer="${l.key}" ${state.layers[l.key] ? 'checked' : ''}>
            ${l.swatch ? `<span class="qa-layer-swatch" style="background:${l.swatch}"></span>` : ''}${esc(l.label)}</label>`;
    }).join('');
    $('qaLayersBtn').addEventListener('click', () => $('qaLayers').classList.toggle('open'));
    body.addEventListener('change', ev => {
        const k = ev.target.dataset.layer;
        if (!k) return;
        state.layers[k] = ev.target.checked;
        savePrefs();
        if (k === 'miniature') stage.setMiniature(state.layers.miniature);
        else if (k === 'water') stage.setWater(state.layers.water);
        else if (k === 'buildings') world.setBuildings(state.layers.buildings);
        else if (k === 'props') world.setProps(state.layers.props);
        else if (k === 'figures') world.setFigures(state.layers.figures);
        else if (k === 'critters') world.setCritters(state.layers.critters);
        else redraw();
    });
    body.addEventListener('input', ev => {
        const k = ev.target.dataset.slider;
        if (k === 'exaggeration') {
            state.exaggeration = +ev.target.value;
            stage.setExaggeration(state.exaggeration);
            savePrefs();
            redrawSoon(true);
        } else if (k === 'figureScale') {
            state.figureScale = +ev.target.value;
            world.setFigureScale(state.figureScale);
            savePrefs();
        }
    });
}

let redrawTimer = 0;
function redrawSoon(ground = false) {
    clearTimeout(redrawTimer);
    redrawTimer = setTimeout(() => { if (ground) world.relayout(); redraw(); }, 60);
}

function wireFilters() {
    const f = state.filters;
    $('qaSearch').addEventListener('input', ev => { f.search = ev.target.value; refreshSoon(); });
    $('qaFaction').addEventListener('click', ev => {
        const b = ev.target.closest('button');
        if (!b) return;
        f.faction = b.dataset.v;
        $('qaFaction').querySelectorAll('button').forEach(x => x.classList.toggle('on', x === b));
        refresh();
    });
    $('qaLvMin').addEventListener('change', ev => { f.lvMin = +ev.target.value || 1; refresh(); });
    $('qaLvMax').addEventListener('change', ev => { f.lvMax = +ev.target.value || 70; refresh(); });
    $('qaHomeOnly').addEventListener('change', ev => { f.homeOnly = ev.target.checked; refresh(); });
    $('qaClassQuests').addEventListener('change', ev => { f.classQuests = ev.target.checked; refresh(); });
    $('qaClear').addEventListener('click', () => { clearSelection(); state.model && stage.overview(); });
    $('qaRightClose').addEventListener('click', clearSelection);

    $('qaList').addEventListener('click', ev => {
        const row = ev.target.closest('[data-q]');
        if (row) selectQuestById(+row.dataset.q);
    });
    $('qaCard').addEventListener('click', ev => {
        const tab = ev.target.closest('[data-tab]');
        if (tab) {
            setPref('cardTab', tab.dataset.tab);
            $('qaCard').querySelectorAll('.qa-tab').forEach(t => t.classList.toggle('on', t === tab));
            $('qaCard').querySelectorAll('.qa-tabpane').forEach(p => p.hidden = p.dataset.pane !== tab.dataset.tab);
            return;
        }
        const goto = ev.target.closest('[data-goto]');
        if (goto && +goto.dataset.zone) { loadZone(+goto.dataset.zone, +goto.dataset.goto); return; }
        const q = ev.target.closest('[data-q]');
        if (q) { selectQuestById(+q.dataset.q); return; }
        const ent = ev.target.closest('[data-ent]');
        if (ent) { const [kind, entry] = ent.dataset.ent.split(':'); selectEntity(kind, +entry); }
    });
    document.addEventListener('keydown', ev => {
        if (ev.key === 'Escape' && !ev.target.closest('input, select')) { clearSelection(); state.model && stage.overview(); }
    });
}

let refreshTimer = 0;
function refreshSoon() { clearTimeout(refreshTimer); refreshTimer = setTimeout(refresh, 120); }

function veil(on, title, sub, frac) {
    const v = $('qaVeil');
    v.classList.toggle('gone', !on);
    if (!on) return;
    $('qaVeilTitle').textContent = title;
    $('qaVeilSub').textContent = sub;
    $('qaVeilBar').style.width = `${Math.round((frac ?? 0) * 100)}%`;
}

// ── url + prefs (per-viewer conveniences only) ──────────────────────────

function readHash() {
    const p = new URLSearchParams(location.hash.slice(1));
    return { zone: p.get('zone'), quest: p.get('quest') };
}

function writeHash() {
    const p = new URLSearchParams();
    if (state.model) p.set('zone', state.model.zone.areaId);
    if (state.sel.quest != null) p.set('quest', state.sel.quest);
    history.replaceState(null, '', `#${p}`);
}

function pref(k) { try { return localStorage.getItem(`qa.${k}`); } catch { return null; } }
function setPref(k, v) { try { localStorage.setItem(`qa.${k}`, v); } catch { /* private window */ } }
function savePrefs() {
    setPref('layers', JSON.stringify(state.layers));
    setPref('exaggeration', state.exaggeration);
    setPref('figureScale', state.figureScale);
}
function restorePrefs() {
    try {
        const l = JSON.parse(pref('layers') ?? 'null');
        if (l) for (const k of Object.keys(state.layers)) if (k in l) state.layers[k] = !!l[k];
        const e = +pref('exaggeration');
        if (e >= 1 && e <= 5) state.exaggeration = e;
        const f = +pref('figureScale');
        if (f >= 1 && f <= 6) state.figureScale = f;
    } catch { /* defaults */ }
}


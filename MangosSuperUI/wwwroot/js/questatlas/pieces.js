// Quest Atlas — the pieces on the table. Rebuilt wholesale on every filter/layer/selection change;
// the counts are small (hundreds of pins, a few thousand dots) so simplicity wins over diffing.

import * as THREE from 'three';
import { CSS2DObject } from 'three/addons/renderers/CSS2DRenderer.js';
import { S } from './stage.js';
import { key } from './model.js';

const GOLD = new THREE.Color(0xf2c14e);
const VISITOR = new THREE.Color(0xc9bfa8);
const CUSTOM = new THREE.Color(0xc59bf0);
const KIND_COLOR = {
    kill: new THREE.Color(0xe0614f),
    collect: new THREE.Color(0xe8a33a),
    object: new THREE.Color(0x6fb3e0),
    explore: new THREE.Color(0xb48be0)
};

export class Pieces {
    constructor(stage) {
        this.stage = stage;
        this.group = stage.piecesGroup;
        this.icons = {
            '!': glyphTexture('!'),
            '?': glyphTexture('?'),
            '!?': glyphTexture('!?')
        };
        this.dot = dotTexture();
    }

    /**
     * @param model   AtlasModel
     * @param visible Set of quest ids passing the filters
     * @param layers  { givers, enders, chains, journey, kill, collect, objects, explore, labels, levelLift, levelTint }
     * @param sel     { quest: id|null, entity: "npc:123"|null }
     */
    rebuild(model, visible, layers, sel) {
        this.#clear();
        if (!this.stage.board) return;
        const span = this.stage.board.span;
        this.postBase = span * 0.014;          // yards
        this.levelStep = span * 0.004;

        const selQ = sel.quest != null ? model.quests.get(sel.quest) : null;
        const focus = this.#focusSet(model, selQ, sel.entity);
        const dimOthers = !!(selQ || sel.entity);

        this.#people(model, visible, layers, selQ, focus, dimOthers);
        if (layers.chains) this.#arcs(model, visible, selQ);
        this.#objectives(model, visible, layers, selQ);
        if (selQ && layers.journey) this.#journey(model, selQ);
    }

    /** Scene points worth framing for the current selection. */
    focusPoints(model, sel) {
        const pts = [];
        const add = p => p && pts.push(this.stage.toScene(p[0], p[1], p[2]));
        if (sel.quest != null) {
            const q = model.quests.get(sel.quest);
            if (!q) return pts;
            for (const r of [...q.givers, ...q.enders]) model.piecePlaces(r.kind, r.entry).forEach(add);
            for (const t of model.objectiveTargets(q)) {
                const c = centroid(t.pts);
                if (c) add(c);
            }
        } else if (sel.entity) {
            const [kind, entry] = sel.entity.split(':');
            model.piecePlaces(kind, +entry).forEach(add);
        }
        return pts;
    }

    // ── people (givers / enders) ────────────────────────────────────────

    #people(model, visible, layers, selQ, focus, dimOthers) {
        const pieces = new Map();       // "npc:1:0" → { kind, entry, place, gives:Set, ends:Set }
        const take = (refs, qid, role) => {
            for (const r of refs)
                model.piecePlaces(r.kind, r.entry).forEach((p, i) => {
                    const k = `${key(r)}:${i}`;
                    let pc = pieces.get(k);
                    if (!pc) pieces.set(k, pc = { kind: r.kind, entry: r.entry, place: p, gives: new Set(), ends: new Set() });
                    pc[role].add(qid);
                });
        };
        for (const id of visible) {
            const q = model.quests.get(id);
            take(q.givers, id, 'gives');
            take(q.enders, id, 'ends');
        }

        const [lvLo, lvHi] = model.levelRange;
        const posts = [], postColors = [];
        for (const pc of pieces.values()) {
            const g = pc.gives.size > 0, e = pc.ends.size > 0;
            const showG = g && layers.givers, showE = e && layers.enders;
            if (!showG && !showE) continue;
            const glyph = showG && showE ? '!?' : showG ? '!' : '?';
            const qs = [...pc.gives, ...pc.ends].map(id => model.quests.get(id));
            const inFocus = focus.has(key(pc));
            const faded = dimOthers && !inFocus;

            // colour: level tint > custom > home > visitor
            let color;
            if (layers.levelTint) {
                const lv = Math.max(lvLo, Math.min(...[...(pc.gives.size ? pc.gives : pc.ends)].map(id => model.quests.get(id).level)));
                const t = lvHi > lvLo ? (lv - lvLo) / (lvHi - lvLo) : 0;
                color = new THREE.Color().setHSL((1 - t) * 0.33, 0.75, 0.55);
            } else if (qs.some(q => q.custom)) color = CUSTOM.clone();
            else if (qs.some(q => q.home)) color = GOLD.clone();
            else color = VISITOR.clone();

            let lift = this.postBase;
            if (layers.levelLift) {
                const lv = Math.max(...qs.map(q => q.level));
                lift += Math.max(0, lv - lvLo) * this.levelStep;
            }
            const foot = this.stage.toScene(pc.place[0], pc.place[1], pc.place[2]);
            const head = foot.clone(); head.y += lift * S;

            const mat = new THREE.SpriteMaterial({
                map: this.icons[glyph], color, sizeAttenuation: false, depthTest: true, transparent: true,
                opacity: faded ? 0.28 : 1
            });
            const sp = new THREE.Sprite(mat);
            const size = inFocus ? 0.05 : 0.034;
            sp.scale.set(size * (glyph === '!?' ? 1.4 : 1), size, 1);
            sp.center.set(0.5, 0.15);
            sp.position.copy(head);
            sp.renderOrder = inFocus ? 3 : 2;
            sp.userData.pick = { type: 'piece', kind: pc.kind, entry: pc.entry, gives: [...pc.gives], ends: [...pc.ends] };
            this.group.add(sp);
            this.stage.pickables.push(sp);

            posts.push(foot.x, foot.y, foot.z, head.x, head.y, head.z);
            const pcol = faded ? color.clone().multiplyScalar(0.35) : color;
            postColors.push(pcol.r, pcol.g, pcol.b, pcol.r, pcol.g, pcol.b);

            if (layers.labels || inFocus) this.#label(model.entityName(pc.kind, pc.entry), head, faded);
        }
        if (posts.length) {
            const geo = new THREE.BufferGeometry();
            geo.setAttribute('position', new THREE.Float32BufferAttribute(posts, 3));
            geo.setAttribute('color', new THREE.Float32BufferAttribute(postColors, 3));
            this.group.add(new THREE.LineSegments(geo, new THREE.LineBasicMaterial({ vertexColors: true, transparent: true, opacity: 0.85 })));
        }
    }

    // ── chain arcs: from where you hand the last quest in to where the next one starts ──

    #arcs(model, visible, selQ) {
        const selChain = selQ ? model.chainOf.get(selQ.id) : null;
        for (const e of model.edges) {
            const a = model.quests.get(e.from), b = model.quests.get(e.to);
            if (!a || !b || !visible.has(a.id) || !visible.has(b.id)) continue;
            const pa = model.enderPlace(a) ?? model.giverPlace(a);
            const pb = model.giverPlace(b);
            if (!pa || !pb) continue;
            const A = this.stage.toScene(pa[0], pa[1], pa[2], this.postBase);
            const B = this.stage.toScene(pb[0], pb[1], pb[2], this.postBase);
            const chain = model.chainOf.get(b.id);
            const strong = !selQ || chain === selChain;
            const color = new THREE.Color(chain?.color ?? '#d9c7a0');
            if (A.distanceTo(B) < 0.5) {
                // same NPC hands you the next step: a small loop above them
                const loop = new THREE.Mesh(
                    new THREE.TorusGeometry(this.postBase * S * 0.2, this.postBase * S * 0.025, 6, 24),
                    new THREE.MeshBasicMaterial({ color, transparent: true, opacity: strong ? 0.9 : 0.2 }));
                loop.position.copy(A); loop.position.y += this.postBase * S * 0.5;
                this.group.add(loop);
                continue;
            }
            this.#tube(A, B, color, strong ? 0.95 : 0.18, e.type === 'breadcrumb', strong && selQ ? 1.6 : 1);
        }
    }

    #tube(A, B, color, opacity, dashed, thick = 1) {
        const d = A.distanceTo(B);
        const mid = A.clone().add(B).multiplyScalar(0.5);
        mid.y = Math.max(A.y, B.y) + d * 0.28 + 2;
        const curve = new THREE.QuadraticBezierCurve3(A, mid, B);
        const r = Math.max(0.06, this.stage.board.span * S * 0.0005) * thick;
        if (dashed) {
            const n = Math.max(6, Math.round(d / 6));
            for (let k = 0; k < n; k += 2) {
                const seg = new THREE.CatmullRomCurve3([curve.getPoint(k / n), curve.getPoint((k + 0.5) / n), curve.getPoint((k + 1) / n)]);
                this.group.add(new THREE.Mesh(new THREE.TubeGeometry(seg, 4, r, 5),
                    new THREE.MeshBasicMaterial({ color, transparent: true, opacity })));
            }
            return;
        }
        this.group.add(new THREE.Mesh(new THREE.TubeGeometry(curve, 32, r, 6),
            new THREE.MeshBasicMaterial({ color, transparent: true, opacity })));
        // arrowhead at the receiving end
        const tip = curve.getPoint(1), back = curve.getPoint(0.94);
        const cone = new THREE.Mesh(new THREE.ConeGeometry(r * 2.4, r * 6, 8),
            new THREE.MeshBasicMaterial({ color, transparent: true, opacity }));
        cone.position.copy(back.clone().lerp(tip, 0.5));
        cone.quaternion.setFromUnitVectors(new THREE.Vector3(0, 1, 0), tip.clone().sub(back).normalize());
        this.group.add(cone);
    }

    // ── objectives: where the work is ───────────────────────────────────

    #objectives(model, visible, layers, selQ) {
        const want = { kill: layers.kill, collect: layers.collect, object: layers.objects, explore: layers.explore };
        const pos = [], col = [];
        const seen = new Set();
        for (const id of visible) {
            if (selQ && id === selQ.id) continue;
            for (const t of model.objectiveTargets(model.quests.get(id))) {
                if (!want[t.kind]) continue;
                if (t.trigger) { this.#trigger(t.trigger, false); continue; }
                for (const p of t.pts) {
                    const k = `${t.kind}:${p[0]}:${p[1]}`;
                    if (seen.has(k)) continue;
                    seen.add(k);
                    const v = this.stage.toScene(p[0], p[1], p[2], 2);
                    pos.push(v.x, v.y, v.z);
                    const c = KIND_COLOR[t.kind];
                    col.push(c.r, c.g, c.b);
                }
            }
        }
        if (pos.length) this.#points(pos, col, selQ ? 5 : 7, selQ ? 0.35 : 0.85);

        if (!selQ) return;
        const sp = [], sc = [];
        for (const t of model.objectiveTargets(selQ)) {
            if (t.trigger) { this.#trigger(t.trigger, true); continue; }
            const c = KIND_COLOR[t.kind];
            for (const p of t.pts) {
                const v = this.stage.toScene(p[0], p[1], p[2], 2);
                sp.push(v.x, v.y, v.z);
                sc.push(c.r, c.g, c.b);
            }
            const ctr = centroid(t.pts);
            if (ctr) this.#label(t.label, this.stage.toScene(ctr[0], ctr[1], ctr[2], this.postBase * 0.6), false, 'qa-label qa-label-obj');
        }
        if (sp.length) this.#points(sp, sc, 11, 1);
    }

    #points(pos, col, px, opacity) {
        const geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.Float32BufferAttribute(pos, 3));
        geo.setAttribute('color', new THREE.Float32BufferAttribute(col, 3));
        const pts = new THREE.Points(geo, new THREE.PointsMaterial({
            size: px, sizeAttenuation: false, vertexColors: true, map: this.dot,
            transparent: true, opacity, alphaTest: 0.2, depthWrite: false
        }));
        pts.renderOrder = 1;
        this.group.add(pts);
    }

    #trigger(t, strong) {
        const r = Math.max(t.radius || 0, t.boxX ? Math.max(t.boxX, t.boxY) / 2 : 0, 8);
        const ring = new THREE.Mesh(
            new THREE.RingGeometry(r * S * 0.82, r * S, 48),
            new THREE.MeshBasicMaterial({ color: KIND_COLOR.explore, transparent: true, opacity: strong ? 0.95 : 0.55, side: THREE.DoubleSide, depthWrite: false }));
        ring.rotation.x = -Math.PI / 2;
        ring.position.copy(this.stage.toScene(t.x, t.y, t.z, 3));
        ring.userData.pick = { type: 'trigger', id: t.id };
        this.group.add(ring);
        this.stage.pickables.push(ring);
    }

    // ── journey: giver → each objective → ender ─────────────────────────

    #journey(model, q) {
        const g = model.giverPlace(q), e = model.enderPlace(q);
        const white = new THREE.Color(0xfff4d6);
        const stops = model.objectiveTargets(q).map(t => centroid(t.pts)).filter(Boolean);
        const at = p => this.stage.toScene(p[0], p[1], p[2], this.postBase);
        let prev = g ? at(g) : null;
        for (const s of stops) {
            const S2 = at(s);
            if (prev && prev.distanceTo(S2) > 0.5) this.#tube(prev, S2, white, 0.9, true, 1.3);
            prev = S2;
        }
        if (prev && e) {
            const E = at(e);
            if (prev.distanceTo(E) > 0.5) this.#tube(prev, E, white, 0.9, true, 1.3);
        }
    }

    // ── helpers ─────────────────────────────────────────────────────────

    #focusSet(model, selQ, entity) {
        const f = new Set();
        if (selQ) for (const r of [...selQ.givers, ...selQ.enders]) f.add(key(r));
        if (entity) f.add(entity);
        return f;
    }

    #label(text, pos, faded, cls = 'qa-label') {
        const div = document.createElement('div');
        div.className = cls;
        div.textContent = text;
        if (faded) div.style.opacity = '0.4';
        const o = new CSS2DObject(div);
        o.position.copy(pos);
        this.group.add(o);
    }

    #clear() {
        this.stage.pickables.length = 0;
        for (const o of [...this.group.children]) {
            this.group.remove(o);
            if (o.isCSS2DObject) o.element.remove();
            o.geometry?.dispose();
            o.material?.dispose();      // shared glyph/dot textures stay alive (Material.dispose leaves maps)
        }
    }
}

function centroid(pts) {
    if (!pts.length) return null;
    // the densest third, so one stray spawn across the map doesn't drag the stop into nowhere
    const mx = pts.reduce((s, p) => s + p[0], 0) / pts.length;
    const my = pts.reduce((s, p) => s + p[1], 0) / pts.length;
    const near = [...pts].sort((a, b) => ((a[0] - mx) ** 2 + (a[1] - my) ** 2) - ((b[0] - mx) ** 2 + (b[1] - my) ** 2))
        .slice(0, Math.max(1, Math.ceil(pts.length / 3)));
    const n = near.length;
    return [near.reduce((s, p) => s + p[0], 0) / n, near.reduce((s, p) => s + p[1], 0) / n, near.reduce((s, p) => s + p[2], 0) / n];
}

function glyphTexture(glyph) {
    const c = document.createElement('canvas');
    c.width = glyph.length > 1 ? 180 : 128; c.height = 128;
    const g = c.getContext('2d');
    g.font = 'bold 104px Georgia, serif';
    g.textAlign = 'center';
    g.textBaseline = 'middle';
    g.lineJoin = 'round';
    g.lineWidth = 16;
    g.strokeStyle = 'rgba(20,12,4,0.95)';
    g.strokeText(glyph, c.width / 2, 68);
    g.fillStyle = '#ffffff';
    g.fillText(glyph, c.width / 2, 68);
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace;
    return t;
}

function dotTexture() {
    const c = document.createElement('canvas');
    c.width = c.height = 64;
    const g = c.getContext('2d');
    const grd = g.createRadialGradient(32, 32, 4, 32, 32, 30);
    grd.addColorStop(0, '#ffffff');
    grd.addColorStop(0.7, '#ffffff');
    grd.addColorStop(1, 'rgba(255,255,255,0)');
    g.fillStyle = grd;
    g.beginPath(); g.arc(32, 32, 30, 0, Math.PI * 2); g.fill();
    g.strokeStyle = 'rgba(0,0,0,0.6)'; g.lineWidth = 5;
    g.beginPath(); g.arc(32, 32, 24, 0, Math.PI * 2); g.stroke();
    const t = new THREE.CanvasTexture(c);
    t.colorSpace = THREE.SRGBColorSpace;
    return t;
}

(function (global) {
    'use strict';
    const collections = { current: '/equipment-workshop/armor/current-four-reviewed-v1/', previous: '/equipment-workshop/armor/current-reviewed-v1/', fitted: '/equipment-workshop/armor/raid-fitted-v1/', raid: '/equipment-workshop/armor/raid-native-v3/', historical: '/equipment-workshop/armor/prototypes-v2/' };
    const labels = { 'live-verified-scoped': 'Installed · live checks complete', 'installed-native-verified': 'Installed native verified · live checks pending', 'historical-failed': 'Earlier draft · failed motion study', 'historical-study': 'Earlier item-removal study', superseded: 'Superseded study' };
    const shaPattern = /^[a-f0-9]{64}$/;
    const historicalNotice = 'Historical snapshot — statements below describe that recorded revision, not the current installed pipeline. ';
    function collectionText(collection, text) { return (collection === 'previous' ? historicalNotice : '') + text; }
    function imageRecord(record, prefix) {
        if (!record || !record.url?.startsWith(prefix) || !/^[a-z0-9-]+\.png$/.test(record.url.slice(prefix.length)) || !shaPattern.test(record.sha256)) throw new Error('Invalid review image record.');
        return record;
    }
    function validateCatalog(catalog, collection) {
        const prefix = collections[collection], current = collection === 'current' || collection === 'previous';
        if (!prefix || catalog.schemaVersion !== (current ? 3 : 2) || !Array.isArray(catalog.sets) || catalog.sets.length < 2 || typeof catalog.status !== 'string') throw new Error('Incomplete native review catalog.');
        const keys = new Set();
        const sets = catalog.sets.map(source => {
            const set = { ...source };
            if (!/^[a-z0-9-]+$/.test(set.key) || keys.has(set.key) || typeof set.name !== 'string' || !set.revisions?.pieces || !Array.isArray(set.frames) || !set.frames.length) throw new Error('Invalid or duplicate review set.');
            keys.add(set.key);
            if (!current) {
                set.reviewStatus = collection === 'fitted' ? 'historical-failed' : collection === 'historical' ? 'superseded' : 'historical-study';
                set.reviewLabel = labels[set.reviewStatus];
                set.evidenceScope = 'Historical source revision. These images do not show the currently installed accepted geometry.';
                set.liveStatus = 'not-evaluated'; set.gates = [];
            } else {
                if (!['live-verified-scoped','installed-native-verified'].includes(set.reviewStatus) || set.reviewLabel !== labels[set.reviewStatus] || typeof set.evidenceScope !== 'string' || !shaPattern.test(set.sourcePackageSha256) || !['verified-scoped','pending'].includes(set.liveStatus) || (set.liveStatus === 'verified-scoped') !== (set.reviewStatus === 'live-verified-scoped')) throw new Error('Invalid scoped review status.');
                if (!Array.isArray(set.gates) || set.gates.map(g => g.stage).join(',') !== 'offline,installed,live') throw new Error('Separate evidence gates required.');
                for (const gate of set.gates) {
                    if (typeof gate.label !== 'string' || typeof gate.scope !== 'string' || !['accepted-scoped','pending'].includes(gate.status) || !Number.isInteger(gate.frames) || gate.frames < 0 || !Array.isArray(gate.receiptSha256) || gate.receiptSha256.some(h => !shaPattern.test(h))) throw new Error('Invalid evidence gate.');
                    if (gate.status === 'accepted-scoped' && !gate.receiptSha256.length) throw new Error('Accepted gate needs a bound receipt.');
                }
                const live = set.gates[2];
                if ((live.status === 'accepted-scoped') !== (set.liveStatus === 'verified-scoped') || (live.status === 'pending' && live.frames !== 0)) throw new Error('Live evidence scope conflicts with review status.');
                if (set.evidenceUrl !== prefix + set.key + '-evidence.json') throw new Error('Invalid evidence summary URL.');
            }
            const frames = new Set();
            for (const frame of set.frames) {
                if (![1,2,3,4,5,6,7,8].includes(frame.race ?? 1) || ![0,1].includes(frame.sex ?? 0) || !set.revisions[frame.stage] || typeof frame.mode !== 'string' || !['front','right','back','left','front-three-quarter','back-three-quarter'].includes(frame.view)) throw new Error('Invalid wearer or frame identity.');
                const key = [frame.stage,frame.mode,frame.race ?? 1,frame.sex ?? 0,frame.pose?.key ?? 'stand',frame.view].join(':');
                if (frames.has(key)) throw new Error('Duplicate native review frame.');
                frames.add(key); imageRecord(frame.images?.color, prefix); imageRecord(frame.images?.contour, prefix);
                if (current && (frame.stage !== 'pieces' || frame.mode !== 'dressed' || frame.pose?.key !== 'stand' || frame.pose.animationId !== 0 || frame.pose.timeSeconds !== .6)) throw new Error('Current collection contains an unreviewed pose.');
            }
            if (current && frames.size !== 96) throw new Error('Current installed collection needs all 96 standing views per set.');
            return set;
        });
        return { ...catalog, sets };
    }
    // Export the same validator used by the browser for offline catalog and refusal checks.
    if (typeof module !== 'undefined' && module.exports) module.exports = { validateCatalog, imageRecord, collections, collectionText };
    if (typeof document === 'undefined') return;
    const root = document.getElementById('nativeArmorReview');
    if (!root) return;
    const el = id => document.getElementById('nar' + id);
    const state = { sets: [], images: new Map(), active: [], revision: 0, collectionRevision: 0 };
    let prefix = collections[el('Collection').value];
    async function loadImage(record, requestPrefix) {
        imageRecord(record, requestPrefix);
        if (state.images.has(record.sha256)) return state.images.get(record.sha256);
        const response = await fetch(record.url);
        if (!response.ok) throw new Error('Native image unavailable: ' + response.status);
        const bytes = await response.arrayBuffer();
        if (globalThis.crypto?.subtle) {
            const actual = Array.from(new Uint8Array(await crypto.subtle.digest('SHA-256', bytes)), b => b.toString(16).padStart(2, '0')).join('');
            if (actual !== record.sha256) throw new Error('Native image hash differs from its recorded capture.');
        }
        const url = URL.createObjectURL(new Blob([bytes], { type: 'image/png' }));
        const image = new Image();
        try { image.src = url; await image.decode(); } finally { URL.revokeObjectURL(url); }
        state.images.set(record.sha256, image);
        return image;
    }
    function showEvidence(side, set) {
        const target = el('Evidence' + side); target.replaceChildren();
        if (el('Collection').value === 'previous') {
            const notice = document.createElement('p'); notice.textContent = historicalNotice; target.append(notice);
        }
        if (!set.gates.length) {
            const scope = document.createElement('p'); scope.textContent = set.evidenceScope; target.append(scope); return;
        }
        const list = document.createElement('ul');
        for (const gate of set.gates) {
            const item = document.createElement('li'); item.textContent = gate.label + ': ' + gate.scope; list.append(item);
        }
        const link = document.createElement('a'); link.textContent = 'Open evidence summary and receipt hashes'; link.href = set.evidenceUrl; link.target = '_blank'; link.rel = 'noopener';
        target.append(list, link);
    }
    function draw() {
        const exposure = Number(el('Exposure').value), zoom = Number(el('Zoom').value);
        el('ExposureValue').value = exposure.toFixed(1) + '×';
        el('ZoomValue').value = zoom.toFixed(1) + '×';
        for (const active of state.active) {
            const canvas = el('Canvas' + active.side), ctx = canvas.getContext('2d');
            ctx.save(); ctx.clearRect(0, 0, 1024, 1024);
            ctx.filter = `brightness(${exposure})`;
            const size = 1024 * zoom;
            const left = 512 - Number(el('FocusX').value) * size, top = 512 - Number(el('FocusY').value) * size;
            ctx.drawImage(active.image, left, top, size, size);
            ctx.filter = 'none';
            if (el('Guides').checked) {
                ctx.strokeStyle = 'rgba(255,218,120,.65)'; ctx.lineWidth = 1;
                for (const fraction of [.25, .5, .75]) {
                    ctx.beginPath(); ctx.moveTo(1024 * fraction, 0); ctx.lineTo(1024 * fraction, 1024); ctx.stroke();
                    ctx.beginPath(); ctx.moveTo(0, 1024 * fraction); ctx.lineTo(1024, 1024 * fraction); ctx.stroke();
                }
            }
            ctx.restore();
        }
    }
    function updateModes() {
        const selected = ['Left', 'Right'].map(side => state.sets.find(s => s.key === el(side).value)).filter(Boolean);
        for (const option of el('Stage').options) {
            const available = selected.some(set => set.revisions[option.value]);
            option.disabled = !available; option.hidden = !available;
        }
        if (!selected.some(set => set.revisions[el('Stage').value])) el('Stage').value = 'pieces';
        const stage = el('Stage').value;
        for (const option of el('Mode').options) {
            const available = selected.some(set => set.frames.some(frame => frame.stage === stage && frame.mode === option.value));
            option.disabled = !available;
            option.hidden = !available;
        }
        if (!selected.some(set => set.frames.some(frame => frame.stage === stage && frame.mode === el('Mode').value))) el('Mode').value = 'dressed';
        const frames = selected.flatMap(set => set.frames).filter(f => f.stage === stage && f.mode === el('Mode').value && `${f.race ?? 1}:${f.sex ?? 0}` === el('Body').value);
        const poses = new Map(frames.map(f => [f.pose?.key ?? 'stand', f.pose ?? {key:'stand',timeSeconds:0.6}]));
        const priorPose = el('Pose').value;
        el('Pose').replaceChildren();
        for (const [key,pose] of poses) {
            const option = document.createElement('option'); option.value = key;
            option.textContent = key.replaceAll('-', ' ') + ' · ' + Number(pose.timeSeconds).toFixed(2) + ' s';
            el('Pose').append(option);
        }
        if (poses.has(priorPose)) el('Pose').value = priorPose;
        el('Pose').disabled = poses.size < 2;
        const views = new Set(frames.filter(f => (f.pose?.key ?? 'stand') === el('Pose').value).map(f => f.view));
        for (const option of el('View').options) { option.disabled = !views.has(option.value); option.hidden = option.disabled; }
        if (!views.has(el('View').value)) el('View').value = [...views][0] ?? '';
    }
    async function refresh() {
        updateModes();
        const revision = ++state.revision;
        const requestPrefix = prefix;
        el('Status').textContent = 'Loading matching native captures…';
        state.active = [];
        for (const side of ['Left', 'Right']) {
            el('Canvas' + side).getContext('2d').clearRect(0, 0, 1024, 1024);
            el('Caption' + side).textContent = '';
            el('Evidence' + side).replaceChildren();
            el('Raw' + side).removeAttribute('href');
            el('Raw' + side).hidden = true;
        }
        try {
            const active = await Promise.all(['Left', 'Right'].map(async side => {
                const set = state.sets.find(s => s.key === el(side).value);
                const stage = el('Stage').value;
                const frame = set.frames.find(f => f.stage === stage && f.mode === el('Mode').value && f.view === el('View').value && `${f.race ?? 1}:${f.sex ?? 0}` === el('Body').value && (f.pose?.key ?? 'stand') === el('Pose').value);
                if (!frame) return { side, set, unavailable: true };
                const record = frame.images[el('Diagnostic').value];
                return { side, set, frame, image: await loadImage(record, requestPrefix) };
            }));
            if (revision !== state.revision) return;
            state.active = active.filter(item => !item.unavailable);
            for (const item of active) {
                showEvidence(item.side, item.set);
                if (item.unavailable) {
                    el('Caption' + item.side).textContent = item.set.name + ' · ' + item.set.reviewLabel + '. No matching capture exists for this selection; this pane is intentionally empty.';
                    continue;
                }
                el('Caption' + item.side).textContent = item.set.name + ' · ' + item.set.reviewLabel + '. ' + item.set.description + ' ' + item.set.revisions[item.frame.stage].label + '. ' + item.set.reviewNotes;
                el('Raw' + item.side).href = item.frame.images.color.url;
                el('Raw' + item.side).hidden = false;
            }
            el('Status').textContent = collectionText(el('Collection').value, 'Pose sample: ' + el('Pose').value.replaceAll('-', ' ') + ' • fixed native captures. ' + active.map(item => item.set.name + ': ' + item.set.reviewLabel + '.').join(' ') +
                (active.some(item => item.unavailable) ? ' An unavailable pane is intentionally empty.' : ''));
            draw();
        } catch (error) { if (revision === state.revision) el('Status').textContent = error.message; }
    }
    for (const id of ['Left','Right','Body','Pose','View','Stage','Mode','Diagnostic']) el(id).addEventListener('change', refresh);
    for (const id of ['Exposure','Zoom','FocusX','FocusY','Guides']) el(id).addEventListener('input', draw);
    el('Reset').addEventListener('click', () => { el('Exposure').value = '1'; el('Zoom').value = '1.3'; el('FocusX').value = '0.5'; el('FocusY').value = '0.5'; el('Guides').checked = false; el('Diagnostic').value = 'color'; refresh(); });
    async function loadCatalog() {
        const collectionRevision = ++state.collectionRevision;
        ++state.revision; state.active = []; state.sets = [];
        prefix = collections[el('Collection').value];
        if (!prefix) throw new Error('Unknown native review collection.');
        for (const side of ['Left','Right']) {
            el(side).replaceChildren();
            el('Canvas' + side).getContext('2d').clearRect(0,0,1024,1024);
            el('Caption' + side).textContent = '';
            el('Evidence' + side).replaceChildren();
            el('Raw' + side).removeAttribute('href'); el('Raw' + side).hidden = true;
        }
        el('Status').textContent = 'Loading native review collection…';
        const requestedCollection = el('Collection').value;
        const response = await fetch(prefix + 'catalog.json', { cache: 'no-store' });
        if (!response.ok) throw new Error('Native prototype catalog unavailable.');
        const catalog = validateCatalog(await response.json(), requestedCollection);
        if (collectionRevision !== state.collectionRevision) return;
        el('ArtStatus').textContent = collectionText(requestedCollection, catalog.status);
        state.sets = catalog.sets;
        const races = ['Human','Orc','Dwarf','Night Elf','Undead','Tauren','Gnome','Troll'];
        const bodies = [...new Set(state.sets.flatMap(set => set.frames.map(f => `${f.race ?? 1}:${f.sex ?? 0}`)))];
        el('Body').replaceChildren();
        for (const body of bodies) {
            const [race,sex] = body.split(':').map(Number);
            if (!races[race-1] || ![0,1].includes(sex)) throw new Error('Invalid wearer in native review collection.');
            const option = document.createElement('option'); option.value = body; option.textContent = races[race-1] + (sex ? ' female' : ' male'); el('Body').append(option);
        }
        el('Body').disabled = bodies.length === 1;
        el('Scope').textContent = collectionText(requestedCollection, catalog.scope || 'Human male standing and item-removal studies. Other bodies, hairstyles, movement and live verification are separate checks. Images are auto-framed; guides do not measure world-unit dimensions.');
        for (const side of ['Left','Right']) for (const set of state.sets) {
            const option = document.createElement('option'); option.value = set.key; option.textContent = set.name + ' — ' + set.reviewLabel; el(side).append(option);
        }
        el('Right').value = state.sets[1].key;
        await refresh();
    }
    const reload = () => { const promise = loadCatalog(), revision = state.collectionRevision; promise.catch(error => { if (revision === state.collectionRevision) el('Status').textContent = error.message; }); };
    el('Collection').addEventListener('change', reload);
    reload();
})(globalThis);

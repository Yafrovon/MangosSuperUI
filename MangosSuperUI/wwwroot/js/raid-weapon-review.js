const CATALOG = '/equipment-workshop/weapons/raid-native-v5/catalog.json';
const PREFIX = '/equipment-workshop/weapons/raid-native-v5/';

export function validateRaidWeaponCatalog(data) {
    if (data?.schemaVersion !== 1 || data.evidenceKind !== 'offline-production-character-renderer'
        || data.inWorldVerified !== true || data.cosmeticAccepted !== false || data.liveStatus !== 'verified-qa'
        || data.registryArtApplied !== true || data.weapons?.length !== 10 || data.overviews?.length !== 2
        || data.summary?.frames !== 2960 || data.summary?.posedMeshes !== 880 || data.summary?.recordedHairBeardHornCases !== 38
        || data.liveOverviews?.length !== 2 || data.liveProof?.frames !== 180 || data.liveProof?.weapons !== 10
        || data.liveProof?.persistentCopies !== 10 || !/^[a-f0-9]{64}$/.test(data.liveProof?.ledgerSha256))
        throw new Error('The recorded review contract differs; inspect its provenance before displaying it.');
    const keys = new Set();
    for (const weapon of data.weapons) {
        if (keys.has(weapon.key) || !Number.isInteger(weapon.triangles) || weapon.triangles < 1
            || typeof weapon.name !== 'string' || typeof weapon.type !== 'string') throw new Error('Invalid weapon review row.');
        keys.add(weapon.key);
    }
    for (const item of [...data.overviews, ...data.liveOverviews, ...data.weapons.map(w => w.icon)]) {
        if (!item?.url?.startsWith(PREFIX) || item.url.includes('..') || !/^[a-f0-9]{64}$/.test(item.sha256))
            throw new Error('Invalid local review image binding.');
    }
    return data;
}

export function renderRaidWeaponReview(data, doc = document) {
    validateRaidWeaponCatalog(data);
    const element = (tag, text) => { const node = doc.createElement(tag); if (text) node.textContent = text; return node; };
    const sheets = doc.getElementById('raidWeaponReviewSheets');
    const items = doc.getElementById('raidWeaponReviewItems');
    const evidence = doc.getElementById('raidWeaponReviewEvidence');
    const liveSheets = doc.getElementById('raidWeaponLiveSheets');
    sheets.replaceChildren(); items.replaceChildren(); evidence.replaceChildren(); liveSheets.replaceChildren();
    const image = (record, alt) => {
        const node = element('img'); node.src = record.url + '?v=' + record.sha256; node.alt = alt;
        node.width = record.width; node.height = record.height; node.loading = 'lazy'; node.decoding = 'async'; return node;
    };
    for (const overview of data.overviews) {
        const link = element('a'); link.href = overview.url + '?v=' + overview.sha256; link.target = '_blank'; link.rel = 'noopener';
        link.append(image(overview, `Native weapon overview ${overview.page}: five Human male drawn, edge and stowed examples.`),
            element('span', `Open full native overview ${overview.page}`));
        sheets.append(link);
    }
    for (const weapon of data.weapons) {
        const row = element('article'), text = element('div');
        text.append(element('strong', weapon.name), element('span', `${weapon.type} · ${weapon.triangles} triangles`));
        row.append(image(weapon.icon, `${weapon.name} original authored icon`), text); items.append(row);
    }
    for (const overview of data.liveOverviews) {
        const link = element('a'); link.href = overview.url + '?v=' + overview.sha256; link.target = '_blank'; link.rel = 'noopener';
        link.append(image(overview, overview.label), element('span', overview.label)); liveSheets.append(link);
    }
    evidence.append(element('p', `${data.revision}. 16 race/sex bodies; ${data.summary.frames.toLocaleString('en-US')} frames and ${data.summary.posedMeshes} posed meshes. ${data.summary.freshFrames} new frames plus ${data.summary.retainedFrames.toLocaleString('en-US')} explicitly retained frames for unchanged assets.`));
    evidence.append(element('p', `Live verification: ${data.liveProof.frames} reviewed frames, including ${data.liveProof.selfScenes} self scenes, ${data.liveProof.remoteViews} remote views, ${data.liveProof.realItemHovers} actual icon/name hovers and ${data.liveProof.combatFrames} combat views. All ${data.liveProof.persistentCopies} saved copies retain their item identity and owner through later logins.`));
    for (const limitation of data.limitations) evidence.append(element('p', limitation));
    doc.getElementById('raidWeaponReviewStatus').textContent = 'Installed · Live checks complete for all 10 weapons · 38 documented hair / beard / horn cases';
}

export async function initRaidWeaponReview(doc = document, fetcher = fetch) {
    const status = doc.getElementById('raidWeaponReviewStatus');
    if (!status) return;
    try {
        const response = await fetcher(CATALOG, { cache: 'no-store' });
        if (!response.ok) throw new Error(`Review catalog returned ${response.status}.`);
        renderRaidWeaponReview(await response.json(), doc);
    } catch (error) {
        status.classList.add('error'); status.textContent = 'Weapon review unavailable: ' + error.message;
    }
}

if (typeof document !== 'undefined') initRaidWeaponReview();

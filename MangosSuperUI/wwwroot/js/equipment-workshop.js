import * as THREE from 'three';
import { initEquipmentRevision } from './equipment-revision.js';
import { initEquipmentNameRevision } from './equipment-name-revision.js';
import { GLTFLoader } from 'three/addons/loaders/GLTFLoader.js';
import { OrbitControls } from 'three/addons/controls/OrbitControls.js';

const MODES = ['textured', 'unlit', 'clay', 'wireframe', 'silhouette', 'normals'];
const VIEWS = { front: [0, 0, 1], back: [0, 0, -1], edge: [1, 0, 0], top: [0, 1, 0], 'three-quarter': [1, .65, 1] };
const shortHash = value => String(value || '').slice(0, 16);
const readable = value => typeof value === 'number' ? Number(value.toFixed(4)).toLocaleString() : String(value ?? 'unavailable');
const $ = id => document.getElementById(id);

function download(blob, name) {
    const url = URL.createObjectURL(blob), link = document.createElement('a');
    link.href = url; link.download = name; link.click();
    setTimeout(() => URL.revokeObjectURL(url), 1000);
}

async function json(url, options) {
    const response = await fetch(url, options);
    if (!response.ok) {
        let detail; try { detail = (await response.json()).error; } catch { }
        throw new Error(detail || `Request failed (${response.status})`);
    }
    return response.json();
}

class StudyViewer {
    constructor(host) {
        this.host = host; this.serial = 0; this.originals = new Map(); this.replacements = [];
        this.mode = 'textured'; this.view = 'front'; this.observations = [];
        this.renderer = new THREE.WebGLRenderer({ antialias: true, preserveDrawingBuffer: true });
        this.renderer.setPixelRatio(1); // reproducible report pixels, independent of device pixel ratio
        this.renderer.outputColorSpace = THREE.SRGBColorSpace;
        this.renderer.toneMapping = THREE.NoToneMapping;
        host.appendChild(this.renderer.domElement);
        this.scene = new THREE.Scene(); this.scene.background = new THREE.Color('#202630');
        this.camera = new THREE.OrthographicCamera(-1, 1, 1, -1, .001, 100);
        this.controls = new OrbitControls(this.camera, this.renderer.domElement);
        this.controls.enableDamping = false;
        this.controls.addEventListener('change', () => this.render());
        this.controls.addEventListener('start', () => { this.view = 'manual-orbit'; });
        this.scene.add(new THREE.HemisphereLight(0xffffff, 0x666666, 1.2));
        const light = new THREE.DirectionalLight(0xffffff, 1.5); light.position.set(2, 3, 4); this.scene.add(light);
        this.resizeObserver = new ResizeObserver(() => this.resize()); this.resizeObserver.observe(host);
        this.resize();
    }
    resize() {
        const width = Math.max(1, this.host.clientWidth), height = Math.max(1, this.host.clientHeight);
        this.renderer.setSize(width, height, false);
        if (this.model) this.preset(VIEWS[this.view] ? this.view : 'front', false);
        else this.render();
    }
    clear() {
        this.serial++;
        this.restoreMaterials();
        if (this.model) {
            const textures = new Set(), materials = new Set(), geometries = new Set();
            this.model.traverse(node => {
                if (node.geometry) geometries.add(node.geometry);
                for (const material of [].concat(node.material || [])) {
                    materials.add(material);
                    for (const value of Object.values(material)) if (value?.isTexture) textures.add(value);
                }
            });
            textures.forEach(x => x.dispose()); materials.forEach(x => x.dispose()); geometries.forEach(x => x.dispose());
            this.scene.remove(this.model); this.model = null;
        }
        this.originals.clear();
        if (this.guides) { this.scene.remove(this.guides); this.guides.traverse(x => { x.geometry?.dispose(); x.material?.dispose(); }); this.guides = null; }
        this.observations = []; this.lastSheet = null; this.meta = null; this.render();
    }
    async load(url, meta) {
        this.clear(); const serial = this.serial;
        const gltf = await new GLTFLoader().loadAsync(url);
        if (serial !== this.serial) return false;
        this.model = gltf.scene; this.meta = meta; this.scene.add(this.model);
        this.model.updateMatrixWorld(true);
        this.model.traverse(node => { if (node.isMesh) this.originals.set(node, node.material); });
        this.bounds = new THREE.Box3();
        const point = new THREE.Vector3();
        this.model.traverse(node => {
            if (!node.isMesh || !node.geometry?.attributes.position) return;
            const positions = node.geometry.attributes.position;
            const indices = node.geometry.index ? new Set(node.geometry.index.array) : Array.from({ length: positions.count }, (_, i) => i);
            for (const index of indices) {
                node.getVertexPosition(index, point); point.applyMatrix4(node.matrixWorld); this.bounds.expandByPoint(point);
            }
        });
        if (this.bounds.isEmpty()) { this.clear(); throw new Error('The preview contains no measurable mesh.'); }
        this.guides = new THREE.Group();
        const extent = this.bounds.getSize(new THREE.Vector3()).length();
        this.guides.add(new THREE.AxesHelper(Math.max(.1, extent * .2)));
        this.guides.add(new THREE.Box3Helper(this.bounds, 0x7c8d9f));
        this.guides.visible = $('ewGuides').checked; this.scene.add(this.guides);
        this.setMode($('ewMode').value, false); this.preset($('ewCamera').value);
        return true;
    }
    restoreMaterials() {
        for (const [node, original] of this.originals) node.material = original;
        this.replacements.forEach(material => material.dispose()); this.replacements = [];
    }
    setMode(mode, record = true) {
        if (!MODES.includes(mode)) throw new Error('Unknown inspection mode.');
        this.restoreMaterials(); this.mode = mode;
        this.scene.background.set(mode === 'silhouette' ? '#eef0f2' : '#202630');
        if (mode !== 'textured') {
            for (const [node, original] of this.originals) {
                const converted = [].concat(original).map(material => {
                    const common = { side: material.side, depthTest: true, depthWrite: true };
                    let replacement;
                    if (mode === 'unlit') replacement = new THREE.MeshBasicMaterial({ ...common, map: material.map,
                        color: material.color || 0xffffff, alphaMap: material.alphaMap, alphaTest: material.alphaTest,
                        transparent: material.transparent, opacity: material.opacity });
                    else if (mode === 'clay') replacement = new THREE.MeshStandardMaterial({ ...common, color: 0xb5b6b7, roughness: .85, metalness: 0 });
                    else if (mode === 'wireframe') replacement = new THREE.MeshBasicMaterial({ ...common, color: 0x91d6e7, wireframe: true });
                    else if (mode === 'normals') replacement = new THREE.MeshNormalMaterial(common);
                    else replacement = new THREE.MeshBasicMaterial({ ...common, color: 0x141719 });
                    this.replacements.push(replacement); return replacement;
                });
                node.material = Array.isArray(original) ? converted : converted[0];
            }
        }
        this.render(); if (record) this.recordView();
    }
    preset(name, record = true) {
        if (!this.model || !VIEWS[name]) return;
        this.view = name;
        const center = this.bounds.getCenter(new THREE.Vector3());
        const radius = Math.max(.001, this.bounds.getSize(new THREE.Vector3()).length() * .57);
        const canvas = this.renderer.domElement, aspect = canvas.width / Math.max(1, canvas.height);
        this.camera.left = -radius * Math.max(1, aspect); this.camera.right = -this.camera.left;
        this.camera.top = radius * Math.max(1, 1 / aspect); this.camera.bottom = -this.camera.top;
        this.camera.near = radius / 1000; this.camera.far = radius * 20; this.camera.zoom = 1;
        this.camera.up.set(0, name === 'top' ? 0 : 1, name === 'top' ? -1 : 0);
        this.camera.position.copy(center).add(new THREE.Vector3(...VIEWS[name]).normalize().multiplyScalar(radius * 4));
        this.controls.target.copy(center); this.camera.lookAt(center); this.camera.updateProjectionMatrix();
        this.controls.update(); this.render(); if (record) this.recordView();
    }
    render() { this.renderer.render(this.scene, this.camera); }
    snapshot() {
        return { mode: this.mode, cameraPreset: this.view, projection: 'orthographic',
            position: this.camera.position.toArray(), target: this.controls.target.toArray(), up: this.camera.up.toArray(),
            frustum: [this.camera.left, this.camera.right, this.camera.top, this.camera.bottom], zoom: this.camera.zoom,
            pixels: [this.renderer.domElement.width, this.renderer.domElement.height], guides: this.guides?.visible || false,
            fitBasis: 'referenced rendered vertices; raw source-array bounds remain in the inspection report',
            animationTimeMs: 0, toneMapping: 'none', outputColorSpace: 'sRGB', lighting: 'hemisphere 1.2; directional 1.5 at (2,3,4)' };
    }
    recordView() { if (this.model) this.observations.push({ ...this.snapshot(), meaning: 'view displayed; not a review or approval' }); }
    sheet() {
        if (!this.model) return;
        const previous = { mode: this.mode, view: VIEWS[this.view] ? this.view : 'front' };
        const sourceSha256 = this.meta?.sha256;
        const width = 400, height = 300, label = 34, views = Object.keys(VIEWS);
        const canvas = document.createElement('canvas'); canvas.width = width * views.length; canvas.height = (height + label) * MODES.length + 44;
        const context = canvas.getContext('2d'); context.fillStyle = '#111722'; context.fillRect(0, 0, canvas.width, canvas.height);
        context.fillStyle = '#fff'; context.font = '15px sans-serif';
        context.fillText(`${this.meta?.name || 'Equipment'} | source ${this.meta?.sha256 || 'unavailable'} | native-client verification PENDING`, 12, 27);
        this.renderer.setSize(width, height, false);
        const captured = [];
        for (let row = 0; row < MODES.length; row++) for (let col = 0; col < views.length; col++) {
            this.setMode(MODES[row], false); this.preset(views[col], false);
            const x = col * width, y = 44 + row * (height + label);
            context.drawImage(this.renderer.domElement, x, y);
            context.fillStyle = '#fff'; context.font = '13px sans-serif'; context.fillText(`${MODES[row]} / ${views[col]} / time 0`, x + 10, y + height + 22);
            captured.push(this.snapshot());
        }
        this.lastSheet = { capturedAt: new Date().toISOString(), sourceSha256, views: captured, meaning: 'rendered evidence; not visually accepted' };
        this.setMode(previous.mode, false); this.resize(); this.preset(previous.view, false);
        canvas.toBlob(blob => { if (blob) download(blob, `equipment-${shortHash(sourceSha256)}-30-views.png`); });
    }
}

export function initEquipmentWorkshop(bridge) {
    if (!$('equipmentWorkshop')) return;
    const state = { candidates: [], page: null, selection: null, importResult: null, inspection: null, sample: null, serial: 0, browseSerial: 0, preparing: false, skip: 0, query: null };
    let viewer;
    const status = (message, error = false) => { $('ewStatus').textContent = message; $('ewStatus').classList.toggle('error', error); };
    const ensureViewer = () => viewer ||= new StudyViewer($('ewViewer'));
    const guard = action => async () => { try { await action(); } catch (error) { status(error.message || String(error), true); } };
    const pending = () => { $('ewConfigure').disabled = true; $('ewReport').disabled = true; $('ewSheet').disabled = true; };
    const metric = (label, value) => {
        const box = document.createElement('div'); box.className = 'ew-metric';
        const strong = document.createElement('strong'); strong.textContent = readable(value); box.append(strong, document.createTextNode(label)); return box;
    };
    const showMetrics = fields => { $('ewMetrics').replaceChildren(...fields.map(([label, value]) => metric(label, value))); };
    const enableReport = () => { $('ewReport').disabled = false; $('ewSheet').disabled = !viewer?.model; };
    initEquipmentNameRevision();
    initEquipmentRevision({
        getCandidate: () => state.candidates.find(x => x.key === $('ewCandidate').value),
        showPreview: async result => {
            beginSelection();
            state.selection = { kind: result.textureReplaced ? 'art-revision' : 'geometry-revision', name: result.name, sha256: result.modelSha256 };
            state.inspection = result;
            await ensureViewer().load(result.preview.glbWebPath, state.selection);
            showMetrics([['Revision triangles', result.triangleCount], ['Revision vertices', result.vertexCount], ['Compiled SHA-256', shortHash(result.modelSha256)]]);
            enableReport(); status(`Revision preview for ${result.name}. Existing IDs retained; ${result.textureReplaced ? 'new painted skin compiled' : 'registered texture retained'}; native review pending.`);
        }
    });
    const beginSelection = () => { const token = ++state.serial; pending(); viewer?.clear(); state.sample = null; state.inspection = null; state.importResult = null; state.selection = null; $('ewReviewNotes').value = ''; $('ewTextures').replaceChildren(); $('ewMetrics').replaceChildren(); return token; };
    const query = () => new URLSearchParams({ source: $('ewSource').value, kind: $('ewKind').value,
        family: $('ewFamily').value, search: $('ewSearch').value, skip: state.skip, take: 30 });

    async function inspectCandidate() {
        if (state.preparing) return;
        const candidate = state.candidates.find(x => x.key === $('ewCandidate').value);
        if (!candidate) throw new Error('Choose an authored candidate first.');
        const token = beginSelection(); status(`Inspecting ${candidate.name} through the Forge importer…`);
        $('ewCandidateNotes').textContent = candidate.designNotes || '';
        state.preparing = true; $('ewInspectCandidate').disabled = true;
        let result;
        try { result = await bridge.prepareCandidate(candidate); }
        finally { state.preparing = false; $('ewInspectCandidate').disabled = false; }
        if (token !== state.serial) return;
        if (!result?.ok) throw new Error(result?.error || 'The existing GLB importer could not preview this candidate.');
        if (candidate.sha256 && result.sourceSha256?.toLowerCase() !== candidate.sha256.toLowerCase())
            throw new Error('Candidate bytes do not match the catalog hash. Reload or regenerate the candidate catalog before forging.');
        state.selection = { kind: 'authored-candidate', name: candidate.name, sha256: result.sourceSha256, candidate };
        state.importResult = result;
        const audit = result.qualityAudit || {};
        showMetrics([['Triangles after import', result.triangleCount], ['Vertices', result.vertexCount],
            ['Degenerate triangles', audit.degenerateTriangles], ['Duplicate faces', audit.duplicateSurfaceTriangles],
            ['Normals oppose winding', audit.normalsOpposeWinding], ['Zero-area UV faces', audit.degenerateUvTriangles],
            ['Boundary edges', audit.boundaryEdges], ['Source SHA-256', shortHash(result.sourceSha256)]]);
        const url = result.preview?.glbWebPath;
        if (url) await ensureViewer().load(url, state.selection);
        if (token !== state.serial) return;
        $('ewConfigure').disabled = false; enableReport();
        status(`${candidate.name}: structural measurements ready. Native-client fit, animation and visual coherence are pending.`);
    }

    async function browse(reset = false) {
        const serial = ++state.browseSerial;
        if (reset) state.skip = 0;
        status('Reading the original MPQ reference catalog…');
        const params = query(), page = await json('/EquipmentReference/Browse?' + params);
        if (serial !== state.browseSerial) return;
        state.page = page; state.query = params.toString();
        const selectedFamily = $('ewFamily').value;
        $('ewFamily').replaceChildren(new Option('All', ''), ...page.families.map(x => new Option(x, x)));
        $('ewFamily').value = selectedFamily;
        $('ewReferences').replaceChildren(...page.items.map(item => {
            const button = document.createElement('button'); button.type = 'button';
            button.textContent = `${item.name} · ${item.family} · display ${item.displayId}`;
            button.addEventListener('click', guard(() => inspectReference(item, page.provenance.source, page.kind)));
            return button;
        }));
        $('ewPrevious').disabled = state.skip === 0; $('ewNext').disabled = !page.hasMore; $('ewMeasure').disabled = page.items.length === 0;
        $('ewPage').textContent = `${page.skip + (page.items.length ? 1 : 0)}–${page.skip + page.items.length} of ${page.total} display references`;
        $('ewReferenceNotes').textContent = page.notes.join(' ');
        status(`${page.provenance.label}: ${page.items.length} references loaded. Select one to inspect original geometry and textures.`);
    }

    async function inspectReference(item, source, kind) {
        const token = beginSelection(); status(`Inspecting original ${item.name}…`);
        const params = new URLSearchParams({ source, kind, displayId: item.displayId, family: item.family, raceGender: $('ewRace').value });
        const data = await json('/EquipmentReference/Inspect?' + params);
        if (token !== state.serial) return;
        state.inspection = data;
        const available = data.models.filter(x => x.preview?.ok && x.preview.glbWebPath);
        const firstModel = available[0] || data.models[0];
        state.selection = { kind: 'original-reference', name: item.name, source, displayId: item.displayId,
            sha256: firstModel?.sha256 || data.provenance.dbcSha256, modelPath: firstModel?.path };
        const geometry = firstModel?.geometry;
        showMetrics([['Triangles (first model)', geometry?.triangleCount], ['Vertices (first model)', geometry?.vertexCount],
            ['Models', data.models.length], ['Textures', data.textures.length], ['Source SHA-256', shortHash(state.selection.sha256)]]);
        $('ewTextures').replaceChildren(...data.textures.map(texture => {
            const figure = document.createElement('figure'), caption = document.createElement('figcaption');
            if (texture.pngUrl) { const link = document.createElement('a'); link.href = texture.pngUrl; link.target = '_blank'; link.rel = 'noopener';
                const image = new Image(); image.src = texture.pngUrl; image.alt = texture.path; image.loading = 'lazy'; link.append(image); figure.append(link); }
            caption.textContent = texture.error || `${texture.path.split('\\').pop()} · ${texture.statistics?.width}×${texture.statistics?.height} · luminance ${readable(texture.statistics?.meanLuminance)}`;
            figure.append(caption); return figure;
        }));
        for (const model of available) {
            const button = document.createElement('button'); button.type = 'button'; button.className = 'wf-btn secondary'; button.textContent = `View ${model.variant}`;
            button.addEventListener('click', guard(async () => {
                if (token !== state.serial) return;
                state.selection.sha256 = model.sha256; state.selection.modelPath = model.path;
                await ensureViewer().load(model.preview.glbWebPath, { ...state.selection });
                if (token === state.serial) enableReport();
            })); $('ewMetrics').append(button);
        }
        if (available.length) await ensureViewer().load(available[0].preview.glbWebPath, state.selection);
        if (token !== state.serial) return;
        enableReport();
        const errors = data.models.filter(x => x.error || x.preview?.error).map(x => x.error || x.preview.error);
        status(errors.length ? `Reference has unresolved parts: ${errors.join(' ')}` : `${item.name}: original source measured. ${data.limitations}`, errors.length > 0);
    }

    $('ewInspectCandidate').addEventListener('click', guard(inspectCandidate));
    $('ewCandidate').addEventListener('change', () => {
        const candidate = state.candidates.find(x => x.key === $('ewCandidate').value);
        $('ewCandidateNotes').textContent = candidate?.designNotes || '';
        if (state.selection?.kind === 'authored-candidate' || state.preparing) beginSelection();
        status(candidate ? `Selected ${candidate.name}. Inspect import to load and measure it. The shared Forge preview may still show the previous import.` : 'Choose an authored candidate or browse original references.');
    });
    $('ewConfigure').addEventListener('click', guard(async () => {
        if (state.selection?.kind !== 'authored-candidate' || !state.importResult?.ok) return;
        await bridge.configureCandidate(state.selection.candidate, state.importResult);
    }));
    $('ewBrowse').addEventListener('click', guard(() => browse(true)));
    $('ewSearch').addEventListener('keydown', event => { if (event.key === 'Enter') $('ewBrowse').click(); });
    for (const id of ['ewSource', 'ewKind']) $(id).addEventListener('change', () => {
        state.browseSerial++; state.page = null; state.query = null; state.skip = 0;
        $('ewFamily').replaceChildren(new Option('All', '')); $('ewReferences').replaceChildren(); $('ewPage').textContent = '';
        for (const button of ['ewPrevious', 'ewNext', 'ewMeasure']) $(button).disabled = true;
    });
    $('ewPrevious').addEventListener('click', guard(() => { state.skip = Math.max(0, state.skip - 30); return browse(); }));
    $('ewNext').addEventListener('click', guard(() => { state.skip += 30; return browse(); }));
    $('ewMeasure').addEventListener('click', guard(async () => {
        if (!state.query) return;
        status('Measuring this bounded reference page…');
        const params = new URLSearchParams(state.query); params.set('raceGender', $('ewRace').value);
        state.sample = await json('/EquipmentReference/Measure?' + params);
        download(new Blob([JSON.stringify(state.sample, null, 2)], { type: 'application/json' }), `equipment-reference-${state.page.provenance.source}-${state.skip}.json`);
        status('Sample report downloaded. Quantiles describe this page, with exact geometry duplicates removed; they are not historical limits or art approval.');
    }));
    $('ewMode').addEventListener('change', guard(() => ensureViewer().setMode($('ewMode').value)));
    $('ewCamera').addEventListener('change', guard(() => ensureViewer().preset($('ewCamera').value)));
    $('ewFit').addEventListener('click', guard(() => ensureViewer().preset($('ewCamera').value)));
    $('ewGuides').addEventListener('change', () => { if (viewer?.guides) { viewer.guides.visible = $('ewGuides').checked; viewer.render(); } });
    $('ewSheet').addEventListener('click', guard(() => viewer?.sheet()));
    $('ewReport').addEventListener('click', () => {
        if (!state.selection) return;
        const report = { schema: 'equipment-study-v1', createdAt: new Date().toISOString(), source: state.selection,
            runtimeVerified: false, visualAcceptance: 'pending', reviewNotes: $('ewReviewNotes').value,
            import: state.importResult, originalInspection: state.inspection, currentView: viewer?.snapshot(),
            displayedViews: viewer?.observations || [], contactSheet: viewer?.lastSheet || null,
            limitations: 'View coverage records rendered views, not visual approval. Structural metrics do not establish artistic coherence, animation, attachment fit, all-race compatibility or native-client correctness.' };
        download(new Blob([JSON.stringify(report, null, 2)], { type: 'application/json' }), `equipment-study-${shortHash(state.selection.sha256)}.json`);
    });
    $('ewEvidenceAttach').addEventListener('click', async () => {
        const button = $('ewEvidenceAttach'), message = $('ewEvidenceStatus'), output = $('ewEvidenceResult');
        const file = $('ewEvidenceFile').files[0];
        if (!file || file.size < 1 || file.size > 2 * 1024 * 1024) { message.textContent = 'Choose one evidence JSON file of at most 2 MiB.'; return; }
        button.disabled = true; output.replaceChildren(); message.textContent = 'Checking supplied hashes against compiled Forge registry bytes…';
        try {
            const data = new FormData(); data.append('evidence', file);
            const result = await json('/EquipmentEvidence/Attach', { method: 'POST', body: data });
            const report = result.report, claims = report.untrustedClaimedCounts;
            const claimed = document.createElement('p'); claimed.className = 'ew-note';
            claimed.textContent = `Untrusted supplied capture counts: ${claims.completed}/${claims.requested} completed; ${claims.technicalErrorCases} technical-error cases; complete claimed: ${claims.captureComplete}; full body matrix claimed: ${claims.fullVanillaBodyMatrix}. These counts were not independently verified.`;
            const checked = document.createElement('p'); checked.className = 'ew-note';
            checked.textContent = `Server byte comparisons: ${report.matched.length} matched, ${report.mismatch.length} mismatched, ${report.missing.length} unresolved or reported missing, ${report.notChecked.length} not checked. Offline evidence attached; visual and live-world verification remain pending.`;
            const scope = document.createElement('p'); scope.className = 'ew-note'; scope.textContent = report.scope;
            const link = document.createElement('a'); link.href = result.reportUrl; link.textContent = `Download stored report ${shortHash(result.id)}`;
            output.append(claimed, checked, scope, link);
            for (const [label, entries] of [['Matched', report.matched], ['Mismatched', report.mismatch], ['Unresolved / reported missing', report.missing], ['Not checked', report.notChecked]]) {
                if (!entries.length) continue;
                const details = document.createElement('details'), heading = document.createElement('summary');
                heading.textContent = `${label} (${entries.length})`; details.append(heading);
                for (const entry of entries) { const text = document.createElement('p'); text.className = 'ew-note';
                    text.textContent = `${entry.path}: ${entry.reason} Claimed ${entry.claimedSha256 || '(none)'}; registry ${entry.registrySha256 || '(unresolved)'}.`; details.append(text); }
                output.append(details);
            }
            message.textContent = `Stored a hash-bound offline report. Input SHA-256 ${report.inputSha256}. No asset, item, patch or acceptance status was changed.`;
        } catch (error) { message.textContent = error.message || String(error); }
        finally { button.disabled = false; }
    });
    json('/equipment-workshop/weapons/catalog.json').then(catalog => {
        state.candidates = Array.isArray(catalog.weapons) ? catalog.weapons : [];
        $('ewCandidate').replaceChildren(new Option('Choose a candidate…', ''), ...state.candidates.map(x => new Option(`${x.name} · ${x.triangleCount} triangles`, x.key)));
        if (!state.candidates.length) $('ewCandidateNotes').textContent = 'No authored candidates are available in this build. Original reference study remains available.';
    }).catch(() => { $('ewCandidate').replaceChildren(new Option('No candidate catalog in this build', '')); $('ewCandidateNotes').textContent = 'The generated candidate catalog is not installed. Browse original references while the authored assets are prepared.'; });
}

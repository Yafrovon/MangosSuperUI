(() => {
    'use strict';
    const form = document.getElementById('authoredArmorForm');
    if (!form) return;
    const state = document.getElementById('authoredArmorState');
    const metrics = document.getElementById('authoredArmorMetrics');
    const actions = document.getElementById('authoredArmorActions');
    const build = document.getElementById('authoredArmorBuild');
    let staged = null;
    let stagedCandidate = null;
    async function responseJson(response) {
        const data = await response.json();
        if (!response.ok && !data.report) throw new Error(data.message || `Request failed (${response.status}).`);
        return data;
    }
    const candidateSelect = document.getElementById('authoredArmorCandidate');
    const candidateLoad = document.getElementById('authoredArmorLoadCandidate');
    const candidateNote = document.getElementById('authoredArmorCandidateNote');
    const candidatePaint = document.getElementById('authoredArmorCandidatePaint');
    let candidates = [];
    let busy = false;
    const designRejected = candidate => candidate?.designStatus === 'rejected';
    const reviewLabel = candidate => designRejected(candidate) ? 'design rejected' : 'design review pending';
    function reviewNote(candidate) {
        const status = designRejected(candidate) ? 'Design rejected.' : 'Design review pending.';
        return `${status}${candidate?.designNotes ? ' ' + candidate.designNotes : ''}`;
    }
    function stagedStatus(message) {
        return `${message} ${reviewNote(stagedCandidate)} Compiling or previewing does not approve the design.`;
    }
    function setBusy(value) {
        busy = value;
        form.querySelector('button').disabled = value;
        candidateSelect.disabled = value;
        candidateLoad.disabled = value || !candidateSelect.value;
    }
    async function stage(body) {
        staged = null; stagedCandidate = null; actions.hidden = true; setBusy(true);
        state.textContent = 'Checking all body textures and 18 attachments, then compiling the native assets…';
        metrics.textContent = '';
        try {
            const data = await responseJson(await fetch('/AuthoredArmor/Stage', { method: 'POST', body }));
            const r = data.report;
            metrics.textContent = [
                `Source SHA-256: ${r.packageSha256}`,
                ...r.meshes.map(m => `${m.asset}: ${m.triangles} triangles; ${m.boundaryEdges} open edges; ${m.degenerateFaces} collapsed faces; ${m.degenerateUvFaces} collapsed UVs`),
                ...r.textures.map(t => `${t.asset}: ${t.width}×${t.height}; luminance ${t.meanLuminance.toFixed(3)} ± ${t.luminanceDeviation.toFixed(3)}`),
                ...r.issues.map(i => `${i.severity.toUpperCase()} ${i.asset}: ${i.message}`)
            ].join('\n');
            if (!r.valid || !r.compiled) { state.textContent = 'Package rejected. Resolve the reported errors and upload again.'; return; }
            staged = data;
            stagedCandidate = candidates.find(c => c.packageSha256 === data.id) || null;
            actions.hidden = false; build.disabled = designRejected(stagedCandidate);
            build.textContent = designRejected(stagedCandidate) ? 'Rejected design — review only' : 'Save eight pieces';
            document.getElementById('authoredArmorReport').href = `/AuthoredArmor/Report?id=${data.id}`;
            state.textContent = stagedStatus(`${r.name}: all eight pieces compiled.`);
        } catch (error) { state.textContent = error.message; }
        finally { setBusy(false); }
    }
    form.addEventListener('submit', event => {
        event.preventDefault();
        if (!busy) stage(new FormData(form));
    });
    candidateSelect.addEventListener('change', () => {
        staged = null; stagedCandidate = null; actions.hidden = true; metrics.textContent = '';
        const candidate = candidates.find(c => c.key === candidateSelect.value);
        candidateLoad.disabled = busy || !candidate;
        candidatePaint.hidden = !candidate;
        state.textContent = candidate ? `${reviewNote(candidate)} Load and validate this selection to inspect its compiled assets. The viewer may still show the previous set.` : 'Choose an original set or upload an armor package.';
        if (!candidate) { candidateNote.textContent = ''; return; }
        candidateNote.textContent = `${candidate.material} · Eight pieces, sixteen helms and both shoulders. ${candidate.description} ${reviewNote(candidate)}`;
        candidatePaint.src = `/equipment-workshop/armor/${candidate.key}-paint.png`;
    });
    candidateLoad.addEventListener('click', async () => {
        const candidate = candidates.find(c => c.key === candidateSelect.value);
        if (!candidate || busy) return;
        setBusy(true); state.textContent = `Loading ${candidate.name}…`;
        try {
            const response = await fetch(`/equipment-workshop/armor/${candidate.key}.zip`);
            if (!response.ok) throw new Error(`Candidate download failed (${response.status}).`);
            const blob = await response.blob();
            const body = new FormData(); body.append('package', blob, `${candidate.key}.zip`);
            await stage(body);
            if (staged && staged.id !== candidate.packageSha256) {
                staged = null; stagedCandidate = null; actions.hidden = true;
                state.textContent = 'Candidate catalog hash does not match its ZIP. Reload after publication completes.';
            }
        } catch (error) { state.textContent = error.message; }
        finally { setBusy(false); }
    });
    // Review decisions can change while package bytes stay identical. Never reuse a cached verdict.
    fetch('/equipment-workshop/armor/catalog.json', { cache: 'no-store' }).then(responseJson).then(catalog => {
        candidates = catalog.sets.filter(c => /^[a-z0-9-]+$/.test(c.key) && /^[a-f0-9]{64}$/.test(c.packageSha256));
        candidateSelect.replaceChildren(new Option('Choose an original set', ''));
        for (const candidate of candidates) candidateSelect.add(new Option(`${candidate.name} — ${candidate.material} (${reviewLabel(candidate)})`, candidate.key));
    }).catch(error => {
        candidateSelect.replaceChildren(new Option('Candidates unavailable', ''));
        candidateNote.textContent = error.message;
    });
    document.getElementById('authoredArmorPreview').addEventListener('click', async () => {
        if (!staged) return;
        try {
            if (!window.afViewer?.dressAuthored) throw new Error('The character viewer is not ready.');
            await window.afViewer.dressAuthored(staged.id, staged.pieces);
            state.textContent = stagedStatus(`${staged.report.name}: compiled assets on the selected character.`);
        } catch (error) { state.textContent = error.message; }
    });
    build.addEventListener('click', async () => {
        if (!staged || busy || designRejected(stagedCandidate)) return;
        setBusy(true); build.disabled = true; state.textContent = 'Saving the original set and queuing its patch…';
        try {
            const body = new URLSearchParams({ id: staged.id });
            const data = await responseJson(await fetch('/AuthoredArmor/Build', { method: 'POST', body }));
            state.textContent = data.result.message;
            metrics.textContent += '\n' + data.result.pieces.map(p => `${p.name}: item ${p.itemEntry}, display ${p.displayId}; ${p.ok ? 'world row applied' : p.message}`).join('\n');
            if (data.result.pieces.some(p => !p.ok)) { build.disabled = false; build.textContent = 'Retry world rows'; }
        } catch (error) { state.textContent = error.message; build.disabled = false; }
        finally { setBusy(false); }
    });
})();

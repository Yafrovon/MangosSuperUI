// Both revision scopes preserve identity and use the deferred patch queue.
export function initEquipmentRevision({ getCandidate, showPreview }) {
    const el = id => document.getElementById(id);
    if (!el('ewRevisionTarget')) return;
    const state = { target: null, file: null, texture: null, icon: null, expectedSourceHash: null, preview: null, report: null, serial: 0, busy: false };
    const art = () => el('ewRevisionMode').value === 'art';
    const inputs = ['ewRevisionMode','ewRevisionItem','ewRevisionDisplay','ewRevisionTarget','ewRevisionFile','ewRevisionTexture','ewRevisionIcon','ewRevisionCandidate','ewRevisionPreview'];
    let imageUrls = [];
    const status = text => { el('ewRevisionStatus').textContent = text; };
    const invalidate = (target = false) => {
        ++state.serial; state.preview = null; state.report = null;
        el('ewRevisionApply').disabled = true; el('ewRevisionReport').disabled = true;
        el('ewRevisionResult').textContent = '';
        for (const url of imageUrls) URL.revokeObjectURL(url);
        imageUrls = []; el('ewRevisionArtImages').replaceChildren();
        if (target) { state.target = null; el('ewRevisionHash').value = ''; }
    };
    const request = async (url, options) => {
        const response = await fetch(url, options);
        const data = await response.json();
        if (!response.ok || !data.ok) throw new Error(data.error || `HTTP ${response.status}`);
        return data;
    };
    const guard = action => async () => {
        if (state.busy) return;
        state.busy = true;
        for (const id of [...inputs,'ewRevisionApply']) el(id).disabled = true;
        try { await action(); }
        catch (error) { status(error.message || String(error)); }
        finally {
            state.busy = false;
            for (const id of inputs) el(id).disabled = false;
            el('ewRevisionApply').disabled = !state.preview;
        }
    };
    el('ewRevisionMode').addEventListener('change', () => {
        invalidate(true); el('ewRevisionPaint').hidden = !art(); el('ewRevisionPaint').style.display = art() ? '' : 'none';
        status('Scope changed. Load the current item/display pair and preview again.');
    });
    for (const [id,key] of [['ewRevisionTexture','texture'],['ewRevisionIcon','icon']]) el(id).addEventListener('change', () => {
        invalidate(); state[key] = el(id).files[0] || null;
        status('Paint input changed. Compile a fresh dry run before applying.');
    });
    for (const id of ['ewRevisionItem','ewRevisionDisplay']) el(id).addEventListener('input', () => { invalidate(true); status('Target changed. Load the current item/display pair again.'); });
    el('ewRevisionFile').addEventListener('change', () => {
        invalidate(); state.file = el('ewRevisionFile').files[0] || null; state.expectedSourceHash = null;
        status(state.file ? `Selected ${state.file.name}. Preview this replacement before applying.` : 'Choose a replacement GLB.');
    });
    el('ewRevisionTarget').addEventListener('click', guard(async () => {
        invalidate(true);
        const itemEntry = Number(el('ewRevisionItem').value), displayId = Number(el('ewRevisionDisplay').value);
        if (!Number.isSafeInteger(itemEntry) || itemEntry <= 0 || !Number.isSafeInteger(displayId) || displayId <= 0) throw new Error('Enter positive integer item and display IDs.');
        state.target = await request('/WeaponForge/' + (art() ? 'ArtRevisionTarget?' : 'RevisionTarget?') + new URLSearchParams({ itemEntry, displayId }));
        el('ewRevisionHash').value = state.target.modelSha256;
        status(`Loaded ${state.target.name}: item ${itemEntry}, display ${displayId}. Registered texture ${state.target.textureSha256}.`);
    }));
    el('ewRevisionCandidate').addEventListener('click', guard(async () => {
        invalidate(); state.file = null; state.expectedSourceHash = null; el('ewRevisionFile').value = '';
        const candidate = getCandidate();
        if (!candidate?.glbUrl) throw new Error('Choose an authored candidate above.');
        const response = await fetch(candidate.glbUrl);
        if (!response.ok) throw new Error('Candidate GLB is unavailable.');
        const bytes = await response.arrayBuffer();
        if (bytes.byteLength > 16 * 1024 * 1024) throw new Error('The replacement exceeds 16 MiB.');
        state.expectedSourceHash = candidate.sha256?.toLowerCase() || null;
        state.file = new File([bytes], candidate.key + '.glb', { type: 'model/gltf-binary' });
        status(`Staged ${candidate.name}. Preview will verify its server-computed source hash against the catalog.`);
    }));
    async function revise(apply) {
        if (!state.target || !state.file) throw new Error('Load a target and choose a replacement GLB first.');
        if (state.file.size > 16 * 1024 * 1024) throw new Error('The replacement exceeds 16 MiB.');
        if (art() && (!state.texture || !state.icon)) throw new Error('Choose the explicit painted skin and inventory icon PNGs.');
        if (art() && (state.texture.size > 2 * 1024 * 1024 || state.icon.size > 2 * 1024 * 1024)) throw new Error('Each PNG must be at most 2 MiB.');
        const reviewed = state.preview;
        if (apply && !reviewed) throw new Error('Preview the exact revision before applying.');
        invalidate();
        const serial = state.serial;
        const form = new FormData(); form.append('file',state.file);
        form.append('displayId',state.target.displayId); form.append('itemEntry',state.target.itemEntry);
        form.append('expectedModelSha256',state.target.modelSha256); form.append('apply',String(apply));
        if (apply) { form.append('expectedRevisionModelSha256',reviewed.modelSha256); form.append('expectedRevisionToken',reviewed.revisionToken); }
        if (art()) {
            form.append('texture',state.texture); form.append('icon',state.icon); form.append('expectedTextureSha256',state.target.textureSha256);
            if (apply) { form.append('expectedRevisionTextureSha256',reviewed.textureSha256); form.append('expectedRevisionIconSha256',reviewed.iconSha256); }
        }
        status(apply ? 'Applying the exact reviewed revision…' : 'Compiling and auditing the selected art…');
        const result = await request('/WeaponForge/' + (art() ? 'ReviseArt' : 'ReviseGlb'),{method:'POST',body:form});
        if (serial !== state.serial) throw new Error('Inputs changed while compiling. Preview again.');
        if (!apply && state.expectedSourceHash && (result.sourceGlbSha256 || result.sourceSha256)?.toLowerCase() !== state.expectedSourceHash)
            throw new Error('Candidate differs from the catalog hash. Reload the catalog before applying.');
        state.report = result; el('ewRevisionReport').disabled = false;
        el('ewRevisionResult').textContent = JSON.stringify({ itemEntry:result.itemEntry,displayId:result.displayId,
            previousModelSha256:result.previousModelSha256,modelSha256:result.modelSha256,textureSha256:result.textureSha256,
            sourceSha256:result.sourceGlbSha256 || result.sourceSha256,iconSha256:result.iconSha256,
            textureSourceSha256:result.textureSourceSha256,iconSourceSha256:result.iconSourceSha256,revisionToken:result.revisionToken,
            modelUrl:result.modelUrl,textureUrl:result.textureUrl,iconUrl:result.iconUrl,triangleCount:result.triangleCount,vertexCount:result.vertexCount,
            preservedScaffoldVerified:result.preservedScaffoldVerified,texturePreserved:result.texturePreserved,
            qualityAudit:result.qualityAudit,patchQueued:result.patchQueued,limitation:result.limitation },null,2);
        if (result.preview?.glbWebPath) await showPreview(result);
        if (serial !== state.serial) throw new Error('Inputs changed during preview. Compile again.');
        if (art()) for (const [label,file] of [['Explicit skin source',state.texture],['Explicit icon source — native inventory review pending',state.icon]]) {
            const box=document.createElement('figure'),image=document.createElement('img'),caption=document.createElement('figcaption');
            const url=URL.createObjectURL(file);imageUrls.push(url);image.src=url;image.alt=label;image.style.imageRendering='pixelated';
            image.style.width=file===state.icon?'64px':'256px';caption.textContent=label;box.append(image,caption);el('ewRevisionArtImages').append(box);
        }
        if (apply) {
            state.target.modelSha256 = result.modelSha256; el('ewRevisionHash').value = result.modelSha256;
            if (art()) state.target.textureSha256 = result.textureSha256;
            status(`Revised existing item ${result.itemEntry} / display ${result.displayId}. ${result.message} Native and live visual review pending.`);
        } else {
            state.preview = result;
            status(`Dry run ready for item ${result.itemEntry}. Inspect the compiled ${art()?'geometry and painted skin, plus the icon source':'model with the existing texture'}, then apply this exact revision. No registry changes made.`);
        }
    }
    el('ewRevisionPreview').addEventListener('click',guard(() => revise(false)));
    el('ewRevisionApply').addEventListener('click',guard(() => revise(true)));
    el('ewRevisionReport').addEventListener('click',() => {
        if (!state.report) return;
        const url=URL.createObjectURL(new Blob([JSON.stringify(state.report,null,2)],{type:'application/json'}));
        const a=document.createElement('a');a.href=url;a.download=`weapon-revision-${state.report.displayId}-${state.report.modelSha256}.json`;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);
    });
}

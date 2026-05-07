// MangosSuperUI — Experiment Lab JS (Session 30)
//
// Standalone self-contained file. Load in Patch/Index.cshtml AFTER patch-manager.js:
//   <script src="~/js/experiment-lab.js"></script>
//
// Required HTML in Patch/Index.cshtml:
//   <div class="sc-panel" id="experimentPanel">
//       <div class="sc-panel-title"><i class="fa-solid fa-flask"></i> Experiment Lab</div>
//       <div id="experimentContent"></div>
//   </div>
//
// Optional: expose refresh functions from patch-manager.js for list sync:
//   window.loadCustomSpells = loadCustomSpells;
//   window.loadPatches = loadPatches;
// (If not exposed, experiment lab works fine but custom spells list won't auto-refresh)

$(function () {

    // ── Self-contained helpers (patch-manager.js scopes these in its own closure) ──
    function esc(t) { if (!t && t !== 0) return ''; var d = document.createElement('div'); d.textContent = t; return d.innerHTML; }

    var SCHOOL_NAMES = { 0: 'Physical', 1: 'Holy', 2: 'Fire', 3: 'Nature', 4: 'Frost', 5: 'Shadow', 6: 'Arcane' };
    var SCHOOL_COLORS = { 0: '#aaa', 1: '#fff0aa', 2: '#ff6622', 3: '#33cc33', 4: '#5599ff', 5: '#bb55ff', 6: '#ff88ff' };
    var CLASS_NAMES = { 1: 'Warrior', 2: 'Paladin', 3: 'Hunter', 4: 'Rogue', 5: 'Priest', 7: 'Shaman', 8: 'Mage', 9: 'Warlock', 11: 'Druid' };

    // Load characters for teach dropdown
    var expCharacters = [];
    $.getJSON('/Patch/Characters', function (d) { if (d.characters) expCharacters = d.characters; });

    // Refresh shared UI — call patch-manager's functions if they exist on window, otherwise no-op
    function refreshLists() {
        if (typeof window.loadCustomSpells === 'function') window.loadCustomSpells();
        if (typeof window.loadPatches === 'function') window.loadPatches();
    }

    // ── Styles ──
    var EXP_STYLES = '<style>' +
        '#experimentPanel{margin-top:16px}' +
        '.exp-search-wrap{position:relative;margin-bottom:12px}' +
        '.exp-search-input{width:100%;padding:8px 12px;border:1px solid #333;background:var(--bg-input,#1a1a2e);color:#eee;border-radius:4px;font-size:13px;box-sizing:border-box}' +
        '.exp-search-input:focus{outline:none;border-color:#4488ff}' +
        '.exp-results-dropdown{position:absolute;top:100%;left:0;right:0;background:var(--surface,#1e1e32);border:1px solid #444;border-top:none;border-radius:0 0 6px 6px;max-height:350px;overflow-y:auto;z-index:1000;box-shadow:0 8px 24px rgba(0,0,0,.5)}' +
        '.exp-results-dropdown:empty{display:none}' +
        '.exp-result-item{padding:8px 14px;cursor:pointer;display:flex;align-items:center;justify-content:space-between;border-bottom:1px solid #2a2a3e;transition:background .1s}' +
        '.exp-result-item:hover{background:#2a2a44}' +
        '.exp-result-item:last-child{border-bottom:none}' +
        '.exp-result-name{font-weight:600;color:#eee;font-size:13px}' +
        '.exp-result-sub{color:#999;font-weight:400;font-size:12px}' +
        '.exp-result-right{display:flex;align-items:center;gap:8px}' +
        '.exp-result-id{font-size:11px;color:#555}' +
        '.exp-result-school{font-size:10px;padding:2px 8px;border-radius:10px;font-weight:600}' +
        '.exp-result-lvl{font-size:10px;color:#666}' +
        '.exp-search-empty{padding:12px 14px;color:#666;font-size:12px;text-align:center}' +
        '.exp-selected{background:var(--surface,#1e1e32);border:1px solid #4488ff;border-radius:6px;padding:10px 14px;margin-bottom:12px;display:flex;align-items:center;justify-content:space-between}' +
        '.exp-selected-name{font-weight:600;font-size:14px;color:#eee}' +
        '.exp-selected-meta{font-size:12px;color:#999;margin-top:4px}' +
        '.exp-selected-clear{cursor:pointer;color:#666;font-size:14px;padding:4px 8px}.exp-selected-clear:hover{color:#e74c3c}' +
        '.exp-search-btn,.exp-btn{padding:6px 14px;border:none;border-radius:4px;cursor:pointer;font-size:12px;font-weight:600}' +
        '.exp-phase-tag{display:inline-block;background:#4488ff22;color:#4488ff;padding:2px 8px;border-radius:10px;font-size:11px;margin:2px}' +
        '.exp-controls{display:flex;gap:8px;align-items:center;margin-bottom:12px;flex-wrap:wrap}' +
        '.exp-controls label{font-size:12px;color:#999}' +
        '.exp-controls input,.exp-controls select{padding:4px 8px;background:#1a1a2e;border:1px solid #333;color:#eee;border-radius:4px;font-size:12px;width:60px}' +
        '.exp-controls select{width:auto}' +
        '.exp-btn-plan{background:#f39c12;color:#000}.exp-btn-plan:hover{background:#e67e22}' +
        '.exp-btn-run{background:#2ecc71;color:#000}.exp-btn-run:hover{background:#27ae60}' +
        '.exp-btn-cleanup{background:#e74c3c;color:#fff}.exp-btn-cleanup:hover{background:#c0392b}' +
        '.exp-btn-save{background:#9b59b6;color:#fff}.exp-btn-save:hover{background:#8e44ad}' +
        '.exp-btn-export{background:#1abc9c;color:#000}.exp-btn-export:hover{background:#16a085}' +
        '.exp-btn:disabled{opacity:.5;cursor:not-allowed}' +
        '.exp-plan-wrap{max-height:400px;overflow-y:auto;border:1px solid #333;border-radius:4px}' +
        '.exp-plan-table{width:100%;border-collapse:collapse;font-size:11px}' +
        '.exp-plan-table th{background:#1a1a2e;color:#999;padding:6px 8px;text-align:left;border-bottom:1px solid #333;font-weight:500;position:sticky;top:0}' +
        '.exp-plan-table td{padding:5px 8px;border-bottom:1px solid #222;color:#ccc}' +
        '.exp-plan-table tr:hover td{background:#1a1a2e}' +
        '.exp-delta{font-family:monospace;font-size:11px}' +
        '.exp-delta-val{color:#2ecc71}.exp-delta-arrow{color:#666;margin:0 3px}' +
        '.exp-param-name{color:#4488ff;font-weight:500}' +
        '.exp-status{padding:8px 12px;border-radius:4px;margin-top:10px;font-size:12px}' +
        '.exp-status.info{background:#4488ff22;color:#4488ff}' +
        '.exp-status.success{background:#2ecc7122;color:#2ecc71}' +
        '.exp-status.error{background:#e74c3c22;color:#e74c3c}' +
        '.exp-obs-card{background:#111;border:1px solid #333;border-radius:6px;padding:10px 14px;margin-bottom:8px}' +
        '.exp-obs-header{display:flex;justify-content:space-between;align-items:center;margin-bottom:8px}' +
        '.exp-obs-title{font-weight:600;font-size:13px;color:#eee}' +
        '.exp-obs-entry{font-size:11px;color:#666}' +
        '.exp-obs-changes{font-family:monospace;font-size:11px;color:#999;margin-bottom:8px;line-height:1.6}' +
        '.exp-obs-change-line{display:block}' +
        '.exp-obs-phase-row{display:flex;align-items:center;gap:8px;margin-bottom:6px;flex-wrap:wrap}' +
        '.exp-obs-phase-label{font-size:11px;color:#4488ff;min-width:60px;font-weight:500}' +
        '.exp-impact-btn{padding:3px 10px;border:1px solid #333;background:#1a1a2e;color:#999;border-radius:3px;cursor:pointer;font-size:11px;transition:all .15s}' +
        '.exp-impact-btn.active-0{border-color:#666;color:#666;background:#66666622}' +
        '.exp-impact-btn.active-1{border-color:#f39c12;color:#f39c12;background:#f39c1222}' +
        '.exp-impact-btn.active-2{border-color:#e67e22;color:#e67e22;background:#e67e2222}' +
        '.exp-impact-btn.active-3{border-color:#e74c3c;color:#e74c3c;background:#e74c3c22}' +
        '.exp-obs-notes{width:100%;padding:4px 8px;background:#1a1a2e;border:1px solid #333;color:#eee;border-radius:3px;font-size:11px;margin-top:4px;resize:none}' +
        '.exp-obs-overall{width:100%;padding:6px 8px;background:#1a1a2e;border:1px solid #333;color:#eee;border-radius:3px;font-size:12px;margin-top:4px;resize:vertical;min-height:30px}' +
        '.exp-export-box{background:#0a0a1a;border:1px solid #333;border-radius:4px;padding:12px;margin-top:10px;font-family:monospace;font-size:11px;color:#ccc;white-space:pre-wrap;max-height:300px;overflow-y:auto;user-select:all}' +
        '.exp-mode-extreme{color:#e74c3c;font-weight:600}' +
        '#expMode{width:140px}' +
        '</style>';
    $('head').append(EXP_STYLES);

    var expSearchTimer = null;
    var expSource = null;
    var expPlan = null;
    var expLastRun = null;

    // ── Helpers ──
    function shortParam(p) {
        return {
            emissionRate: 'rate', lifespan: 'life', emissionSpeed: 'speed',
            gravity: 'grav', scaleStart: 'scale', emissionAreaLength: 'area',
            speedVariation: 'spdVar', verticalRange: 'vRange', horizontalRange: 'hRange'
        }[p] || p;
    }
    function fmtVal(v) { return Math.abs(v) >= 10 ? v.toFixed(1) : v.toFixed(2); }

    // ── Init ──
    function buildExperimentLab() {
        $('#experimentContent').html(
            '<div class="exp-search-wrap">' +
            '<input type="text" class="exp-search-input" id="expSearchInput" placeholder="Search any spell (e.g. Fireball, Lightning Bolt, 133)..." />' +
            '<div class="exp-results-dropdown" id="expSearchResults"></div></div>' +
            '<div id="expSelectedSource" style="display:none"></div>' +
            '<div id="expControls" style="display:none"></div>' +
            '<div id="expPlanPreview"></div>' +
            '<div id="expStatus"></div>' +
            '<div id="expObservations"></div>' +
            '<div id="expExport"></div>' +
            '<div id="expImport"></div>'
        );
    }
    buildExperimentLab();

    // ── Auto-search on typing (250ms debounce) ──
    $(document).on('input', '#expSearchInput', function () {
        var q = $(this).val().trim();
        clearTimeout(expSearchTimer);
        if (q.length < 2) { $('#expSearchResults').empty(); return; }
        expSearchTimer = setTimeout(function () {
            $.getJSON('/Patch/SearchSource', { q: q }, function (d) {
                if (!d.results || !d.results.length) {
                    $('#expSearchResults').html('<div class="exp-search-empty">No spells found</div>');
                    return;
                }
                var h = '';
                d.results.slice(0, 15).forEach(function (s) {
                    var sub = s.nameSubtext ? ' (' + esc(s.nameSubtext) + ')' : '';
                    var sc = SCHOOL_NAMES[s.school] || '?';
                    var scColor = SCHOOL_COLORS[s.school] || '#aaa';
                    h += '<div class="exp-result-item" data-entry="' + s.entry + '" data-name="' + esc(s.name) + '">' +
                        '<span><span class="exp-result-name">' + esc(s.name) + '</span><span class="exp-result-sub">' + sub + '</span></span>' +
                        '<span class="exp-result-right">' +
                        (s.spellLevel ? '<span class="exp-result-lvl">Lv' + s.spellLevel + '</span>' : '') +
                        '<span class="exp-result-school" style="background:' + scColor + '22;color:' + scColor + '">' + sc + '</span>' +
                        '<span class="exp-result-id">#' + s.entry + '</span></span></div>';
                });
                $('#expSearchResults').html(h);
            });
        }, 250);
    });

    // Close dropdown when clicking outside
    $(document).on('click', function (e) {
        if (!$(e.target).closest('.exp-search-wrap').length) $('#expSearchResults').empty();
    });

    // ── Select source spell from dropdown ──
    $(document).on('click', '.exp-result-item', function () {
        expSource = { entry: $(this).data('entry'), name: $(this).data('name') };
        expPlan = null; expLastRun = null;
        $('#expSearchResults').empty();
        $('#expSearchInput').hide();

        // Show selected state
        $('#expSelectedSource').html(
            '<div class="exp-selected">' +
            '<div><span class="exp-selected-name">' + esc(expSource.name) + '</span>' +
            '<span style="color:#555;margin-left:8px">#' + expSource.entry + '</span>' +
            '<div class="exp-selected-meta" id="expPhaseInfo">Scanning phases...</div></div>' +
            '<span class="exp-selected-clear" id="expClearSource"><i class="fa-solid fa-xmark"></i></span></div>'
        ).show();

        var ch = '<div class="exp-controls">' +
            '<label>Mode:</label><select id="expMode"><option value="standard">Standard Sweep</option><option value="extreme" selected>Extreme Contrast</option></select> ' +
            '<label>Batch size:</label><input type="number" id="expBatchSize" value="20" min="5" max="50" /> ' +
            '<label>Teach to:</label><select id="expTeachChar"><option value="0">None</option>';
        expCharacters.forEach(function (c) { ch += '<option value="' + c.guid + '">' + esc(c.name) + ' (Lv' + c.level + ' ' + (CLASS_NAMES[c.charClass] || '') + ')</option>'; });
        ch += '</select> <button class="exp-btn exp-btn-plan" id="btnExpPlan"><i class="fa-solid fa-list-check"></i> Preview Plan</button> ' +
            '<button class="exp-btn exp-btn-cleanup" id="btnExpCleanup"><i class="fa-solid fa-broom"></i> Cleanup Old</button></div>';
        $('#expControls').html(ch).show();
        $('#expPlanPreview,#expStatus,#expObservations,#expExport').empty();
        buildImportPanel();

        // Show/hide batch size based on mode (extreme is fixed at 12)
        function updateModeUI() {
            var isExtreme = $('#expMode').val() === 'extreme';
            $('#expBatchSize').closest('label').next('#expBatchSize').toggle(!isExtreme);
            if (isExtreme) { $('#expBatchSize').val(12); }
        }
        // Can't easily hide just the input+label pair with this structure, so we use a wrapper approach:
        // Instead, just disable batch size for extreme mode
        $(document).off('change.expMode').on('change.expMode', '#expMode', function () {
            var isExtreme = $(this).val() === 'extreme';
            $('#expBatchSize').prop('disabled', isExtreme);
            if (isExtreme) $('#expBatchSize').val(12);
        });
        // Init state
        $('#expBatchSize').prop('disabled', $('#expMode').val() === 'extreme');
        if ($('#expMode').val() === 'extreme') $('#expBatchSize').val(12);

        // Probe phases
        $.ajax({
            url: '/Patch/PlanExperiment', method: 'POST', contentType: 'application/json',
            data: JSON.stringify({ sourceSpellEntry: expSource.entry, sourceSpellName: expSource.name, batchSize: 1 }),
            success: function (r) {
                if (r.success && r.phasesFound) {
                    var tags = ''; r.phasesFound.forEach(function (p) { tags += '<span class="exp-phase-tag">' + p + ' (' + (r.phaseEmitterCounts[p] || '?') + ' em)</span> '; });
                    $('#expPhaseInfo').html(tags);
                } else { $('#expPhaseInfo').html('<span style="color:#e74c3c">' + esc(r.error || 'No phases') + '</span>'); }
            }
        });
    });

    // ── Clear source selection ──
    $(document).on('click', '#expClearSource', function () {
        expSource = null; expPlan = null; expLastRun = null;
        $('#expSelectedSource').hide().empty();
        $('#expSearchInput').val('').show().focus();
        $('#expControls').hide().empty();
        $('#expPlanPreview,#expStatus,#expObservations,#expExport').empty();
    });

    // ── Preview Plan ──
    $(document).on('click', '#btnExpPlan', function () {
        if (!expSource) return;
        var $b = $(this).prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i>');
        $.ajax({
            url: '/Patch/PlanExperiment', method: 'POST', contentType: 'application/json',
            data: JSON.stringify({ sourceSpellEntry: expSource.entry, sourceSpellName: expSource.name, batchSize: parseInt($('#expBatchSize').val()) || 20, mode: $('#expMode').val() || 'standard' }),
            success: function (r) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-list-check"></i> Preview Plan');
                if (!r.success) { $('#expPlanPreview').html('<div class="exp-status error">' + esc(r.error) + '</div>'); return; }
                expPlan = r;
                var phases = r.phasesFound || [];
                var isExtreme = (r.mode === 'extreme');
                var h = '<div style="margin:8px 0;font-size:12px;color:#999">' +
                    (isExtreme ? '<span style="color:#e74c3c;font-weight:600">EXTREME</span> ' : '') +
                    r.totalPlans + ' clones, ' + r.totalObservations + ' observations' +
                    (isExtreme ? ' — same param in ALL phases, suppress vs crank' : '');
                if (r.emitterReason) {
                    h += '<br><span style="color:#f39c12"><i class="fa-solid fa-crosshairs"></i> ' + esc(r.emitterReason) + '</span>';
                }
                h += '</div>';
                h += '<div class="exp-plan-wrap"><table class="exp-plan-table"><thead><tr><th>#</th><th>Name</th>';
                phases.forEach(function (p) { h += '<th>' + p + '</th>'; });
                h += '</tr></thead><tbody>';
                (r.plans || []).forEach(function (plan) {
                    // For extreme mode, alternate row colors for suppress/crank pairs
                    var rowStyle = '';
                    if (isExtreme) {
                        rowStyle = plan.cloneIndex % 2 === 1 ? 'background:#1a1a2e' : 'background:#1e2e1a';
                    }
                    h += '<tr style="' + rowStyle + '"><td>' + plan.cloneIndex + '</td><td style="color:#eee;white-space:nowrap">' + esc(plan.spellName) + '</td>';
                    phases.forEach(function (p) {
                        var c = (plan.changes || []).find(function (x) { return x.phase === p; });
                        h += c ? '<td><span class="exp-param-name">' + shortParam(c.parameter) + '</span><br><span class="exp-delta">' + fmtVal(c.baseline) + '<span class="exp-delta-arrow">\u2192</span><span class="exp-delta-val">' + fmtVal(c.newVal) + '</span></span></td>' : '<td style="color:#333">\u2014</td>';
                    });
                    h += '</tr>';
                });
                h += '</tbody></table></div><div style="margin-top:12px"><button class="exp-btn exp-btn-run" id="btnExpRun"><i class="fa-solid fa-play"></i> Run (' + r.totalPlans + ' clones)</button></div>';
                $('#expPlanPreview').html(h);
            },
            error: function () { $b.prop('disabled', false).html('<i class="fa-solid fa-list-check"></i> Preview Plan'); }
        });
    });

    // ── Run ──
    $(document).on('click', '#btnExpRun', function () {
        if (!expSource) return;
        var $b = $(this).prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Creating...');
        $('#expStatus').html('<div class="exp-status info"><i class="fa-solid fa-flask"></i> Creating clones, patching M2s, rebuilding patch...</div>');

        $.ajax({
            url: '/Patch/RunExperiment', method: 'POST', contentType: 'application/json',
            data: JSON.stringify({
                sourceSpellEntry: expSource.entry, sourceSpellName: expSource.name,
                batchSize: parseInt($('#expBatchSize').val()) || 20,
                mode: $('#expMode').val() || 'standard',
                teachToCharacterGuid: parseInt($('#expTeachChar').val()) || 0
            }),
            success: function (r) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-play"></i> Run');
                if (!r.success) { $('#expStatus').html('<div class="exp-status error">' + esc(r.error) + '</div>'); return; }
                expLastRun = r;
                var msg = '<i class="fa-solid fa-check"></i> <strong>' + r.clonesCreated + '</strong> clones. ' + r.totalObservations + ' observations. ';
                if (r.patchRebuilt) msg += 'Patch: <code>' + esc(r.patchFileName) + '</code>. ';
                msg += '<br><strong>' + esc(r.note) + '</strong>';
                $('#expStatus').html('<div class="exp-status success">' + msg + '</div>');
                buildObservationForm(r.results);
                refreshLists();
            },
            error: function (xhr) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-play"></i> Run');
                $('#expStatus').html('<div class="exp-status error">Failed: ' + (xhr.statusText || 'Error') + '</div>');
            }
        });
    });

    // ── Observation Form ──
    function buildObservationForm(results) {
        var successes = (results || []).filter(function (r) { return r.success; });
        if (!successes.length) { $('#expObservations').empty(); return; }

        var h = '<div style="margin-top:16px;margin-bottom:8px;font-size:13px;font-weight:600;color:#eee">' +
            '<i class="fa-solid fa-clipboard-check"></i> Log Observations</div>' +
            '<div style="font-size:11px;color:#666;margin-bottom:12px">Cast each spell in-game, then rate what you saw. Impact: 0=invisible, 1=subtle, 2=noticeable, 3=dramatic.</div>';

        successes.forEach(function (res) {
            h += '<div class="exp-obs-card" data-clone="' + res.cloneIndex + '" data-entry="' + res.spellEntry + '">';
            h += '<div class="exp-obs-header"><span class="exp-obs-title">' + esc(res.spellName) + '</span><span class="exp-obs-entry">#' + res.spellEntry + '</span></div>';

            var changeTxt = '';
            (res.changes || []).forEach(function (c) {
                changeTxt += '<span class="exp-obs-change-line"><span class="exp-param-name">' + c.phase + '</span>/em' + c.emitterIndex + ': ' +
                    shortParam(c.parameter) + ' ' + fmtVal(c.baseline) + '\u2192' + fmtVal(c.newVal) + ' (' + c.mode + ')</span>';
            });
            h += '<div class="exp-obs-changes">' + changeTxt + '</div>';

            (res.changes || []).forEach(function (c) {
                h += '<div class="exp-obs-phase-row"><span class="exp-obs-phase-label">' + c.phase + '</span>';
                ['None', 'Subtle', 'Notice', 'Dramatic'].forEach(function (label, level) {
                    h += '<button class="exp-impact-btn" data-clone="' + res.cloneIndex + '" data-phase="' + c.phase + '" data-level="' + level + '">' + label + '</button>';
                });
                h += '<input type="text" class="exp-obs-notes" data-clone="' + res.cloneIndex + '" data-phase="' + c.phase + '" placeholder="what did ' + shortParam(c.parameter) + ' change look like?" /></div>';
            });

            h += '<textarea class="exp-obs-overall" data-clone="' + res.cloneIndex + '" placeholder="Overall impression..." rows="1"></textarea>';
            h += '</div>';
        });

        h += '<div style="margin-top:12px;display:flex;gap:8px">' +
            '<button class="exp-btn exp-btn-save" id="btnExpSaveObs"><i class="fa-solid fa-save"></i> Save Observations</button> ' +
            '<button class="exp-btn exp-btn-export" id="btnExpExport"><i class="fa-solid fa-file-export"></i> Export for Claude</button> ' +
            '<button class="exp-btn" style="background:#3498db;color:#fff" id="btnExpShowSummary"><i class="fa-solid fa-brain"></i> Save Summary</button></div>';

        $('#expObservations').html(h);
    }

    // Impact buttons
    $(document).on('click', '.exp-impact-btn', function () {
        $(this).closest('.exp-obs-phase-row').find('.exp-impact-btn').removeClass('active-0 active-1 active-2 active-3');
        $(this).addClass('active-' + $(this).data('level'));
    });

    // ── Save Observations ──
    $(document).on('click', '#btnExpSaveObs', function () {
        if (!expSource || !expLastRun) return;
        var observations = [];

        $('.exp-obs-card').each(function () {
            var cloneIdx = $(this).data('clone');
            var cloneEntry = $(this).data('entry');
            var phases = {};
            var runRes = (expLastRun.results || []).find(function (r) { return r.cloneIndex === cloneIdx; });
            if (!runRes) return;

            $(this).find('.exp-obs-phase-row').each(function () {
                var $active = $(this).find('.exp-impact-btn[class*="active-"]');
                var level = $active.length ? parseInt($active.data('level')) : -1;
                var notes = $(this).find('.exp-obs-notes').val() || '';
                var phase = $(this).find('.exp-obs-notes').data('phase');
                var change = (runRes.changes || []).find(function (c) { return c.phase === phase; });
                if (!change) return;

                if (level >= 0 || notes) {
                    phases[phase] = {
                        parameter: change.parameter,
                        baselineValue: change.baseline,
                        newValue: change.newVal,
                        observation: notes || null,
                        impactRating: level >= 0 ? level : 0
                    };
                }
            });

            var overall = $(this).find('.exp-obs-overall').val() || '';
            if (Object.keys(phases).length > 0 || overall) {
                observations.push({ cloneIndex: cloneIdx, cloneSpellEntry: cloneEntry, phases: phases, overallNotes: overall || null });
            }
        });

        if (!observations.length) { alert('Rate at least one phase before saving.'); return; }

        var $b = $(this).prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Saving...');
        $.ajax({
            url: '/Patch/SaveObservations', method: 'POST', contentType: 'application/json',
            data: JSON.stringify({ sourceSpellEntry: expSource.entry, sourceSpellName: expSource.name, observations: observations }),
            success: function (r) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-save"></i> Save Observations');
                $('#expStatus').html(r.success
                    ? '<div class="exp-status success"><i class="fa-solid fa-check"></i> Saved ' + r.saved + ' observations.</div>'
                    : '<div class="exp-status error">' + esc(r.error) + '</div>');
            },
            error: function () { $b.prop('disabled', false).html('<i class="fa-solid fa-save"></i> Save Observations'); }
        });
    });

    // ── Export for Claude ──
    $(document).on('click', '#btnExpExport', function () {
        if (!expSource) return;
        $.getJSON('/Patch/ExportExperiment', { sourceName: expSource.name }, function (r) {
            if (r.success) {
                $('#expExport').html(
                    '<div style="margin-top:12px;font-size:12px;color:#999">Copy and paste into Claude chat with the Quantization Guide (' + r.count + ' observations):</div>' +
                    '<div class="exp-export-box">' + esc(r.text) + '</div>' +
                    '<button class="exp-btn" style="margin-top:8px;background:#333;color:#eee" onclick="navigator.clipboard.writeText(document.querySelector(\'.exp-export-box\').textContent).then(function(){alert(\'Copied!\')})"><i class="fa-solid fa-copy"></i> Copy to Clipboard</button>'
                );
            } else {
                $('#expExport').html('<div class="exp-status error">' + esc(r.error) + '</div>');
            }
        });
    });

    // ── Save Summary (findings for next batch auto-advance) ──
    $(document).on('click', '#btnExpShowSummary', function () {
        if (!expSource) return;
        // Load existing summary if any
        $.getJSON('/Patch/ExperimentSummary', { spellName: expSource.name }, function (r) {
            var existing = (r.success && r.exists) ? r.summary : null;
            var batchNum = existing ? (existing.batches || []).length + 1 : 1;
            var emIdx = (expPlan && expPlan.targetedEmitter !== undefined) ? expPlan.targetedEmitter : 0;

            var h = '<div style="margin-top:16px;margin-bottom:8px;font-size:13px;font-weight:600;color:#eee">' +
                '<i class="fa-solid fa-brain"></i> Save Experiment Summary — Batch ' + batchNum + '</div>' +
                '<div style="font-size:11px;color:#666;margin-bottom:8px">This records what you learned so the next batch auto-advances to the next emitter.</div>';

            h += '<div style="margin-bottom:8px"><label style="font-size:12px;color:#999">Parameter ranking (most → least visual impact):</label>' +
                '<input type="text" id="expSummaryRanking" class="exp-search-input" style="margin-top:4px" value="scaleStart, lifespan, emissionSpeed, gravity, emissionRate, emissionAreaLength" /></div>';

            var params = ['emissionRate', 'lifespan', 'emissionSpeed', 'gravity', 'scaleStart', 'emissionAreaLength', 'speedVariation', 'verticalRange', 'horizontalRange'];
            params.forEach(function (p) {
                h += '<div style="margin-bottom:6px"><label style="font-size:11px;color:#4488ff">' + p + ' notes:</label>' +
                    '<input type="text" class="exp-search-input exp-summary-param" data-param="' + p + '" style="margin-top:2px;font-size:11px" placeholder="What did suppress/crank do?" /></div>';
            });

            h += '<div style="margin-bottom:8px"><label style="font-size:12px;color:#999">Emitters remaining (comma-separated indices):</label>' +
                '<input type="text" id="expSummaryRemaining" class="exp-search-input" style="margin-top:4px" value="' +
                ((existing && existing.emittersRemaining) ? existing.emittersRemaining.join(', ') : '1, 2, 3, 4') + '" /></div>';

            h += '<button class="exp-btn" style="background:#3498db;color:#fff" id="btnExpDoSaveSummary"><i class="fa-solid fa-save"></i> Save Summary to Disk</button>';

            $('#expExport').html(h);

            // Pre-fill from existing summary findings if present
            if (existing && existing.batches) {
                var lastBatch = existing.batches[existing.batches.length - 1];
                if (lastBatch && lastBatch.findings) {
                    if (lastBatch.findings.parameterRanking)
                        $('#expSummaryRanking').val(lastBatch.findings.parameterRanking.join(', '));
                    var pp = lastBatch.findings.perParameter || {};
                    Object.keys(pp).forEach(function (k) {
                        if (pp[k].notes) {
                            $('.exp-summary-param[data-param="' + k + '"]').val(pp[k].notes);
                        }
                    });
                }
            }
        });
    });

    $(document).on('click', '#btnExpDoSaveSummary', function () {
        if (!expSource) return;
        var ranking = ($('#expSummaryRanking').val() || '').split(',').map(function (s) { return s.trim(); }).filter(Boolean);
        var remaining = ($('#expSummaryRemaining').val() || '').split(',').map(function (s) { return parseInt(s.trim()); }).filter(function (n) { return !isNaN(n); });
        var emIdx = (expPlan && expPlan.targetedEmitter !== undefined) ? expPlan.targetedEmitter : 0;

        var perParam = {};
        $('.exp-summary-param').each(function () {
            var p = $(this).data('param');
            var notes = $(this).val() || '';
            if (notes) perParam[p] = { suppressValue: 0, crankValue: 0, suppressImpact: '', crankImpact: '', notes: notes };
        });

        // Load existing, append new batch
        $.getJSON('/Patch/ExperimentSummary', { spellName: expSource.name }, function (r) {
            var summary = (r.success && r.exists) ? r.summary : {
                spellName: expSource.name,
                sourceEntry: expSource.entry,
                batches: [],
                emittersRemaining: [],
                nextBatchSuggestion: null
            };

            // Build testedCombos from the plan data
            var testedCombos = [];
            if (expPlan && expPlan.plans) {
                expPlan.plans.forEach(function (plan) {
                    (plan.changes || []).forEach(function (c) {
                        // Check if this combo is already in the list
                        var exists = testedCombos.some(function (tc) {
                            return tc.phase === c.phase && tc.emitterIndex === c.emitterIndex && tc.parameter === c.parameter;
                        });
                        if (!exists) {
                            testedCombos.push({
                                phase: c.phase,
                                emitterIndex: c.emitterIndex,
                                parameter: c.parameter,
                                baseline: c.baseline
                            });
                        }
                    });
                });
            }

            summary.batches.push({
                batchId: summary.batches.length + 1,
                mode: $('#expMode').val() || 'extreme',
                date: new Date().toISOString().split('T')[0],
                emitterTargeted: emIdx,
                emitterSelectionReason: (expPlan && expPlan.emitterReason) || 'manual',
                phasesFound: (expPlan && expPlan.phasesFound) || [],
                parametersCovered: ['emissionRate', 'lifespan', 'emissionSpeed', 'gravity', 'scaleStart', 'emissionAreaLength', 'speedVariation', 'verticalRange', 'horizontalRange'],
                testedCombos: testedCombos,
                findings: { parameterRanking: ranking, perParameter: perParam }
            });
            summary.emittersRemaining = remaining;
            summary.nextBatchSuggestion = remaining.length > 0
                ? 'emitter ' + remaining[0] + ' (next untested)'
                : 'All emitters tested — try a different spell';

            $.ajax({
                url: '/Patch/SaveExperimentSummary', method: 'POST', contentType: 'application/json',
                data: JSON.stringify(summary),
                success: function (sr) {
                    if (sr.success) {
                        $('#expExport').html('<div class="exp-status success"><i class="fa-solid fa-check"></i> Summary saved. Next batch will auto-target emitter ' +
                            (remaining.length > 0 ? remaining[0] : '?') + '.</div>');
                    } else {
                        $('#expExport').html('<div class="exp-status error">' + esc(sr.error) + '</div>');
                    }
                }
            });
        });
    });

    // ── Import Claude Batch ──
    function buildImportPanel() {
        var h = '<div style="margin-top:20px;border-top:1px solid #333;padding-top:16px">' +
            '<div style="font-size:13px;font-weight:600;color:#eee;margin-bottom:8px">' +
            '<i class="fa-solid fa-file-import"></i> Import Claude Quantization Batch</div>' +
            '<div style="font-size:11px;color:#666;margin-bottom:8px">' +
            'Paste the batch JSON from a Claude quantization session. This appends to the existing summary without replacing previous batches.</div>' +
            '<div style="margin-bottom:8px">' +
            '<input type="text" class="exp-search-input" id="expImportSpell" placeholder="Spell name (e.g. Fireball)" ' +
            (expSource ? 'value="' + esc(expSource.name) + '"' : '') + ' />' +
            '</div>' +
            '<div style="margin-bottom:8px">' +
            '<input type="number" class="exp-search-input" id="expImportEntry" placeholder="Source entry (e.g. 133)" style="width:160px" ' +
            (expSource ? 'value="' + expSource.entry + '"' : '') + ' />' +
            '</div>' +
            '<textarea id="expImportJson" rows="12" style="width:100%;background:#1a1a2e;color:#e0e0e0;border:1px solid #333;border-radius:6px;padding:10px;font-family:monospace;font-size:11px;resize:vertical" placeholder=\'Paste batch JSON here — the object inside "batches": [ ... ]\n\nExample:\n{\n  "mode": "extreme",\n  "emitterTargeted": 0,\n  "phasesFound": ["precast", "missile", "impact"],\n  "parametersCovered": ["emissionRate", "lifespan"],\n  "findings": {\n    "parameterRanking": ["scaleStart", "lifespan"],\n    "perParameter": {\n      "scaleStart": {\n        "suppressValue": 0.1,\n        "crankValue": 8.0,\n        "suppressImpact": "NOTICEABLE",\n        "crankImpact": "DRAMATIC",\n        "notes": "..."\n      }\n    }\n  }\n}\'></textarea>' +
            '<div style="margin-top:8px;display:flex;gap:8px;align-items:center">' +
            '<button class="exp-btn" style="background:#9b59b6;color:#fff" id="btnExpImportBatch"><i class="fa-solid fa-file-import"></i> Append Batch</button>' +
            '<span id="expImportStatus" style="font-size:11px"></span>' +
            '</div>' +
            '<div style="margin-top:8px;font-size:11px;color:#555">' +
            '<b>Optional overrides</b> — wrap the batch in an envelope to also update emittersRemaining/nextBatchSuggestion:<br>' +
            '<code style="color:#888">{ "spellName": "...", "sourceEntry": 133, "batch": { ... }, "emittersRemaining": [1,2,3], "nextBatchSuggestion": "..." }</code>' +
            '</div></div>';
        $('#expImport').html(h);
    }
    buildImportPanel();

    $(document).on('click', '#btnExpImportBatch', function () {
        var spellName = ($('#expImportSpell').val() || '').trim();
        var sourceEntry = parseInt($('#expImportEntry').val()) || 0;
        var raw = ($('#expImportJson').val() || '').trim();

        if (!spellName) { $('#expImportStatus').html('<span style="color:#e74c3c">Enter spell name</span>'); return; }
        if (!raw) { $('#expImportStatus').html('<span style="color:#e74c3c">Paste batch JSON</span>'); return; }

        var parsed;
        try { parsed = JSON.parse(raw); }
        catch (e) { $('#expImportStatus').html('<span style="color:#e74c3c">Invalid JSON: ' + esc(e.message) + '</span>'); return; }

        // Detect envelope vs raw batch
        var payload;
        if (parsed.batch && typeof parsed.batch === 'object') {
            // Envelope format: { spellName, sourceEntry, batch, emittersRemaining?, nextBatchSuggestion? }
            payload = {
                spellName: parsed.spellName || spellName,
                sourceEntry: parsed.sourceEntry || sourceEntry,
                batch: parsed.batch,
                emittersRemaining: parsed.emittersRemaining || null,
                nextBatchSuggestion: parsed.nextBatchSuggestion || null
            };
        } else {
            // Raw batch object — wrap it
            payload = {
                spellName: spellName,
                sourceEntry: sourceEntry,
                batch: parsed,
                emittersRemaining: null,
                nextBatchSuggestion: null
            };
        }

        var $btn = $(this).prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Appending...');
        $.ajax({
            url: '/Patch/AppendBatchSummary',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (r) {
                $btn.prop('disabled', false).html('<i class="fa-solid fa-file-import"></i> Append Batch');
                if (r.success) {
                    $('#expImportStatus').html('<span style="color:#27ae60"><i class="fa-solid fa-check"></i> Batch #' + r.batchId +
                        ' appended (' + r.totalBatches + ' total). Remaining: [' + (r.emittersRemaining || []).join(', ') + ']</span>');
                } else {
                    $('#expImportStatus').html('<span style="color:#e74c3c">' + esc(r.error) + '</span>');
                }
            },
            error: function () {
                $btn.prop('disabled', false).html('<i class="fa-solid fa-file-import"></i> Append Batch');
                $('#expImportStatus').html('<span style="color:#e74c3c">Request failed</span>');
            }
        });
    });

    // ── Cleanup ──
    $(document).on('click', '#btnExpCleanup', function () {
        if (!expSource) return;
        if (!confirm('Delete ALL experiment clones for "' + expSource.name + '"?')) return;
        var $b = $(this).prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i>');
        $.ajax({
            url: '/Patch/CleanupExperiment', method: 'POST', contentType: 'application/json',
            data: JSON.stringify({ sourceSpellName: expSource.name }),
            success: function (r) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-broom"></i> Cleanup Old');
                if (r.success) {
                    $('#expStatus').html('<div class="exp-status success"><i class="fa-solid fa-check"></i> Deleted ' + r.deleted + ' clones.</div>');
                    $('#expObservations,#expPlanPreview,#expExport').empty(); refreshLists();
                } else { $('#expStatus').html('<div class="exp-status error">' + esc(r.error) + '</div>'); }
            },
            error: function () { $b.prop('disabled', false).html('<i class="fa-solid fa-broom"></i> Cleanup Old'); }
        });
    });

});
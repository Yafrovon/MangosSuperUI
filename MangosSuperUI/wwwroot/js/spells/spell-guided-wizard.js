// MangosSuperUI — Guided Spell Creator (Session 33)
// A streamlined step-by-step wizard for creating custom spells.
// Sits alongside the existing Workshop (patch-manager.js) — loaded from the same page.
// Uses the same AJAX endpoints: /Patch/SearchSource, /Patch/SourceRanks, /Patch/Generate, etc.

$(function () {

    // ── Constants (shared with patch-manager.js) ──

    var SCHOOL_NAMES = { 0: 'Physical', 1: 'Holy', 2: 'Fire', 3: 'Nature', 4: 'Frost', 5: 'Shadow', 6: 'Arcane' };
    var SCHOOL_COLORS = { 0: '#9e9e9e', 1: '#fff0aa', 2: '#ff6622', 3: '#33cc33', 4: '#5599ff', 5: '#bb55ff', 6: '#ff88ff' };
    var SCHOOL_ICONS = { 0: 'fa-fist-raised', 1: 'fa-sun', 2: 'fa-fire', 3: 'fa-leaf', 4: 'fa-snowflake', 5: 'fa-skull', 6: 'fa-hat-wizard' };
    var CLASS_NAMES = { 1: 'Warrior', 2: 'Paladin', 3: 'Hunter', 4: 'Rogue', 5: 'Priest', 7: 'Shaman', 8: 'Mage', 9: 'Warlock', 11: 'Druid' };
    var CLASS_ICONS = { 1: 'fa-shield-halved', 2: 'fa-cross', 3: 'fa-bullseye', 4: 'fa-user-ninja', 5: 'fa-church', 7: 'fa-bolt-lightning', 8: 'fa-hat-wizard', 9: 'fa-ghost', 11: 'fa-paw' };
    var CLASS_SKILL_TABS = {
        1: [{ key: 'warrior_arms', label: 'Arms' }],
        2: [{ key: 'paladin_holy', label: 'Holy' }],
        3: [{ key: 'hunter_survival', label: 'Survival' }],
        4: [{ key: 'rogue_combat', label: 'Combat' }],
        5: [{ key: 'priest_holy', label: 'Holy' }, { key: 'priest_shadow', label: 'Shadow' }],
        7: [{ key: 'shaman_restoration', label: 'Restoration' }, { key: 'shaman_elemental', label: 'Elemental' }],
        8: [{ key: 'mage_frost', label: 'Frost' }, { key: 'mage_fire', label: 'Fire' }, { key: 'mage_arcane', label: 'Arcane' }],
        9: [{ key: 'warlock_affliction', label: 'Affliction' }, { key: 'warlock_destruction', label: 'Destruction' }],
        11: [{ key: 'druid_restoration', label: 'Restoration' }, { key: 'druid_balance', label: 'Balance' }]
    };
    var CAST_TIMES = { 1: 'Instant', 5: '1.5s', 16: '3.5s', 19: '2.0s', 14: '2.5s', 15: '3.0s', 22: '3.5s' };
    var RANGES = { 1: 'Self', 2: 'Melee (5yd)', 3: '20 yd', 4: '30 yd', 35: '35 yd', 6: '40 yd' };
    var COLOR_PRESETS = [
        { key: 'fire', label: 'Original', desc: 'Keep vanilla colors', swatch: 'linear-gradient(135deg,#ff6622,#ff4400)' },
        { key: 'shadow', label: 'Shadow', desc: 'Dark purple void', swatch: 'linear-gradient(135deg,#bb55ff,#3c0a96)' },
        { key: 'frost', label: 'Frost', desc: 'Icy blue chill', swatch: 'linear-gradient(135deg,#aaddff,#112266)' },
        { key: 'holy', label: 'Holy', desc: 'Golden radiance', swatch: 'linear-gradient(135deg,#fff5cc,#ccaa00)' },
        { key: 'nature', label: 'Nature', desc: 'Emerald growth', swatch: 'linear-gradient(135deg,#44ff44,#115511)' },
        { key: 'arcane', label: 'Arcane', desc: 'Pink-purple energy', swatch: 'linear-gradient(135deg,#ff88ff,#660088)' }
    ];
    var CLASS_ID_FROM_MASK = { 1: 1, 2: 2, 4: 3, 8: 4, 16: 5, 64: 7, 128: 8, 256: 9, 1024: 11 };

    // ── Wizard State ──

    var wiz = {
        step: 1,
        source: null,
        sourceRanks: [],
        name: '',
        description: '',
        school: 2,
        classId: 0,
        skillTabKey: '',
        dmgMin: 0, dmgMax: 0, manaCost: 0,
        spellLevel: 0, castTimeIndex: 0, rangeIndex: 0,
        colorPreset: 'fire',
        customColor: null,
        generateIcon: false,
        generateAllRanks: false,
        trainerMode: 'none',
        trainerCost: 0, trainerReqLevel: 1,
        teachCharGuid: 0,
        characters: [],
        existingIconPath: null,
        existingIconName: null,
        customIcons: []
    };

    var gSearchTimer = null;
    var wizardActive = false;

    // ── Inject Mode Selector ──

    // patch-manager.js creates #gwModeSelector (Guided active) and #gwContainer (visible).
    // Workshop content is already hidden. We just render the wizard.
    injectWizardStyles();
    wizardActive = true;
    $.getJSON('/Patch/Characters', function (d) { wiz.characters = d.characters || []; });
    $.getJSON('/Patch/CustomIcons', function (d) { wiz.customIcons = d.icons || []; });
    renderStep();

    // Mode selector and #gwContainer are created by patch-manager.js initTabSystem().

    $(document).on('click', '#btnGuidedMode', function () {
        wizardActive = true;
        $('#btnGuidedMode').addClass('active');
        $('#btnWorkshopMode').removeClass('active');
        resetWizard();
        showWizard();
    });

    $(document).on('click', '#btnWorkshopMode', function () {
        wizardActive = false;
        $('#btnWorkshopMode').addClass('active');
        $('#btnGuidedMode').removeClass('active');
        hideWizard();
    });

    function resetWizard() {
        wiz.step = 1;
        wiz.source = null;
        wiz.sourceRanks = [];
        wiz.name = '';
        wiz.description = '';
        wiz.school = 2;
        wiz.classId = 0;
        wiz.skillTabKey = '';
        wiz.dmgMin = 0; wiz.dmgMax = 0; wiz.manaCost = 0;
        wiz.spellLevel = 0; wiz.castTimeIndex = 0; wiz.rangeIndex = 0;
        wiz.colorPreset = 'fire';
        wiz.customColor = null;
        wiz.generateIcon = false;
        wiz.generateAllRanks = false;
        wiz.trainerMode = 'none';
        wiz.trainerCost = 0; wiz.trainerReqLevel = 1;
        wiz.teachCharGuid = 0;
        wiz.existingIconPath = null;
        wiz.existingIconName = null;
        // Load characters
        $.getJSON('/Patch/Characters', function (d) { wiz.characters = d.characters || []; });
    }

    function showWizard() {
        // Hide workshop panels
        $('.sc-tabs, #quickCreatePanel, #studioPanel').hide();

        // Show wizard
        $('#gwContainer').show();
        renderStep();
    }

    function hideWizard() {
        // Hide wizard
        $('#gwContainer').hide();

        // Show workshop
        $('.sc-tabs').show();
        // Show whichever tab is active
        if ($('.sc-tab[data-tab="studio"]').hasClass('active')) {
            $('#studioPanel').show();
        } else {
            $('#quickCreatePanel').show();
        }
    }

    // ── Step Rendering ──

    function renderStep() {
        var $c = $('#gwContainer');
        var h = buildProgressBar();
        switch (wiz.step) {
            case 1: h += buildStep1(); break;
            case 2: h += buildStep2(); break;
            case 3: h += buildStep3(); break;
            case 4: h += buildStep4(); break;
            case 5: h += buildStep5(); break;
            case 6: h += buildStep6(); break;
        }
        $c.html(h);
        // Post-render hooks
        if (wiz.step === 3 && wiz.source) prefillPowerFromSource();
        if (wiz.step === 5) loadRanksForWizard();
    }

    function buildProgressBar() {
        var steps = [
            { n: 1, label: 'Base Spell', icon: 'fa-search' },
            { n: 2, label: 'Identity', icon: 'fa-tag' },
            { n: 3, label: 'Power', icon: 'fa-bolt' },
            { n: 4, label: 'Appearance', icon: 'fa-palette' },
            { n: 5, label: 'Ranks', icon: 'fa-layer-group' },
            { n: 6, label: 'Create', icon: 'fa-wand-magic-sparkles' }
        ];
        var h = '<div class="gw-progress">';
        steps.forEach(function (s, i) {
            var cls = s.n < wiz.step ? 'done' : s.n === wiz.step ? 'active' : '';
            h += '<div class="gw-progress-step ' + cls + '">';
            h += '<div class="gw-progress-dot"><i class="fa-solid ' + (s.n < wiz.step ? 'fa-check' : s.icon) + '"></i></div>';
            h += '<span class="gw-progress-label">' + s.label + '</span>';
            h += '</div>';
            if (i < steps.length - 1) h += '<div class="gw-progress-line ' + (s.n < wiz.step ? 'done' : '') + '"></div>';
        });
        h += '</div>';
        return h;
    }

    function navButtons(canBack, canNext, nextLabel, nextId) {
        var h = '<div class="gw-nav">';
        if (canBack) h += '<button class="gw-btn gw-btn-back" id="gwBack"><i class="fa-solid fa-arrow-left"></i> Back</button>';
        else h += '<div></div>';
        if (canNext) h += '<button class="gw-btn gw-btn-next" id="' + (nextId || 'gwNext') + '">' + (nextLabel || 'Continue') + ' <i class="fa-solid fa-arrow-right"></i></button>';
        h += '</div>';
        return h;
    }

    // ── STEP 1: Base Spell ──

    function buildStep1() {
        var h = '<div class="gw-step">';
        h += '<div class="gw-step-header"><h2>Choose your base spell</h2>';
        h += '<p>Search for any vanilla spell to use as the foundation. Your custom spell will inherit its visual effects, projectile, and mechanics.</p></div>';
        h += '<div class="gw-search-wrap"><i class="fa-solid fa-search gw-search-icon"></i>';
        h += '<input type="text" id="gwSearch" class="gw-search" placeholder="Search by name or ID... (e.g. Fireball, Shadow Bolt, 133)" autofocus />';
        h += '</div>';
        h += '<div id="gwSearchResults" class="gw-search-results"></div>';
        if (wiz.source) {
            h += buildSourceCard(wiz.source);
            h += navButtons(false, true);
        }
        h += '</div>';
        return h;
    }

    function buildSourceCard(s) {
        var sc = SCHOOL_NAMES[s.school] || '?', c = SCHOOL_COLORS[s.school] || '#999', ic = SCHOOL_ICONS[s.school] || 'fa-circle';
        var bp = s.effectBasePoints1 || 0, ds = s.effectDieSides1 || 0;
        var dmgMin = bp + 1, dmgMax = bp + ds + 1;
        var cti = s.castingTimeIndex || 0;
        var castLabel = CAST_TIMES[cti] || (cti > 0 ? 'Index ' + cti : '?');
        var h = '<div class="gw-source-card">';
        h += '<div class="gw-source-icon" style="background:' + c + '22;color:' + c + '"><i class="fa-solid ' + ic + '"></i></div>';
        h += '<div class="gw-source-info">';
        h += '<div class="gw-source-name">' + esc(s.name) + (s.nameSubtext ? ' <span class="gw-source-rank">' + esc(s.nameSubtext) + '</span>' : '') + '</div>';
        h += '<div class="gw-source-meta">';
        h += '<span class="gw-tag" style="background:' + c + '18;color:' + c + ';border-color:' + c + '44"><i class="fa-solid ' + ic + '"></i> ' + sc + '</span>';
        if (dmgMax > 0) h += '<span class="gw-tag"><i class="fa-solid fa-crosshairs"></i> ' + dmgMin + '–' + dmgMax + ' dmg</span>';
        if (s.manaCost > 0) h += '<span class="gw-tag" style="color:#5599ff"><i class="fa-solid fa-droplet"></i> ' + s.manaCost + ' mana</span>';
        h += '<span class="gw-tag"><i class="fa-solid fa-clock"></i> ' + castLabel + '</span>';
        h += '<span class="gw-tag gw-tag-id">#' + s.entry + '</span>';
        h += '</div></div>';
        h += '<button class="gw-source-clear" id="gwClearSource"><i class="fa-solid fa-xmark"></i></button>';
        h += '</div>';
        return h;
    }

    $(document).on('input', '#gwSearch', function () {
        var q = $(this).val().trim();
        clearTimeout(gSearchTimer);
        if (q.length < 2) { $('#gwSearchResults').empty(); return; }
        gSearchTimer = setTimeout(function () {
            $.getJSON('/Patch/SearchSource', { q: q }, function (data) {
                if (!data.results || !data.results.length) {
                    $('#gwSearchResults').html('<div class="gw-search-empty">No spells found matching "' + esc(q) + '"</div>');
                    return;
                }
                var h = '';
                data.results.slice(0, 15).forEach(function (s) {
                    var sc = SCHOOL_NAMES[s.school] || '?', c = SCHOOL_COLORS[s.school] || '#999';
                    var ic = SCHOOL_ICONS[s.school] || 'fa-circle';
                    h += '<div class="gw-search-item" data-json=\'' + JSON.stringify(s).replace(/'/g, '&#39;') + '\'>';
                    h += '<div class="gw-search-item-icon" style="color:' + c + '"><i class="fa-solid ' + ic + '"></i></div>';
                    h += '<div class="gw-search-item-info"><span class="gw-search-item-name">' + esc(s.name);
                    if (s.nameSubtext) h += ' <span class="gw-search-item-rank">' + esc(s.nameSubtext) + '</span>';
                    h += '</span><span class="gw-search-item-school" style="color:' + c + '">' + sc + '</span></div>';
                    h += '<span class="gw-search-item-id">#' + s.entry + '</span></div>';
                });
                $('#gwSearchResults').html(h);
            });
        }, 200);
    });

    $(document).on('click', '.gw-search-item', function () {
        wiz.source = JSON.parse($(this).attr('data-json'));
        wiz.school = wiz.source.school;
        var bp = wiz.source.effectBasePoints1 || 0, ds = wiz.source.effectDieSides1 || 0;
        wiz.dmgMin = bp + 1; wiz.dmgMax = bp + ds + 1;
        wiz.manaCost = wiz.source.manaCost || 0;
        wiz.spellLevel = wiz.source.spellLevel || 1;
        wiz.castTimeIndex = wiz.source.castingTimeIndex || 0;
        wiz.rangeIndex = wiz.source.rangeIndex || 0;
        // Pre-fill description from source (has template vars like $s1, $o2, $d)
        if (wiz.source.description && !wiz.description) {
            wiz.description = wiz.source.description;
        }
        renderStep();
    });

    $(document).on('click', '#gwClearSource', function () {
        wiz.source = null;
        renderStep();
    });

    // ── STEP 2: Identity ──

    function buildStep2() {
        var h = '<div class="gw-step">';
        h += '<div class="gw-step-header"><h2>Name your spell</h2>';
        h += '<p>Give it a unique name, choose which class and spellbook tab it belongs to.</p></div>';
        h += '<div class="gw-form">';
        // Name
        h += '<div class="gw-field"><label>Spell Name</label>';
        h += '<input type="text" id="gwName" class="gw-input gw-input-lg" placeholder="e.g. Shadowflame, Frostfire Bolt" value="' + esc(wiz.name) + '" autofocus /></div>';
        // Tooltip Description
        h += '<div class="gw-field"><label>Tooltip Description</label>';
        // Token toolbar
        h += '<div class="gw-desc-toolbar desc-toolbar">';
        h += '<span class="desc-toolbar-label">Insert:</span>';
        h += '<button type="button" class="desc-token-btn gw-desc-token" data-token="$s1" title="Effect 1 value (direct damage)"><i class="fa-solid fa-crosshairs"></i> $s1 Damage</button>';
        h += '<button type="button" class="desc-token-btn gw-desc-token" data-token="$o1" title="Effect 1 periodic total"><i class="fa-solid fa-clock-rotate-left"></i> $o1 Periodic</button>';
        h += '<button type="button" class="desc-token-btn gw-desc-token" data-token="$s2" title="Effect 2 value"><i class="fa-solid fa-crosshairs"></i> $s2 Effect 2</button>';
        h += '<button type="button" class="desc-token-btn gw-desc-token" data-token="$d" title="Duration"><i class="fa-solid fa-hourglass"></i> $d Duration</button>';
        h += '</div>';
        h += '<textarea id="gwDesc" class="gw-input" rows="3" placeholder="e.g. Hurls a bolt of shadow flame at the enemy, causing $s1 Fire damage.">' + esc(wiz.description) + '</textarea>';
        h += '<div class="gw-desc-preview desc-preview" id="gwDescPreview" style="display:none">';
        h += '<span class="desc-preview-label"><i class="fa-solid fa-eye"></i> Preview:</span>';
        h += '<span class="desc-preview-text" id="gwDescPreviewText"></span>';
        h += '</div>';
        h += '<div class="gw-hint"><i class="fa-solid fa-info-circle" style="margin-right:4px"></i>This is the in-game tooltip. Use the Insert buttons to add dynamic values the WoW client fills in automatically. Leave blank to inherit from source.</div>';
        h += '</div>';
        // School
        var sc = SCHOOL_COLORS[wiz.school] || '#999';
        h += '<div class="gw-field"><label>Damage School</label>';
        h += '<div class="gw-school-grid">';
        [2, 4, 5, 1, 3, 6, 0].forEach(function (s) {
            var c = SCHOOL_COLORS[s], ic = SCHOOL_ICONS[s];
            h += '<button class="gw-school-btn' + (wiz.school === s ? ' active' : '') + '" data-school="' + s + '" style="--sc:' + c + '">';
            h += '<i class="fa-solid ' + ic + '"></i> ' + SCHOOL_NAMES[s] + '</button>';
        });
        h += '</div></div>';
        // Class + Tab
        h += '<div class="gw-field-row">';
        h += '<div class="gw-field"><label>Class</label><select id="gwClass" class="gw-select">';
        h += '<option value="0">Any class</option>';
        Object.keys(CLASS_NAMES).forEach(function (k) {
            h += '<option value="' + k + '"' + (wiz.classId == k ? ' selected' : '') + '>' + CLASS_NAMES[k] + '</option>';
        });
        h += '</select></div>';
        h += '<div class="gw-field"><label>Spellbook Tab</label><select id="gwTab" class="gw-select">';
        h += '<option value="">Auto</option>';
        if (wiz.classId && CLASS_SKILL_TABS[wiz.classId]) {
            CLASS_SKILL_TABS[wiz.classId].forEach(function (t) {
                h += '<option value="' + t.key + '"' + (wiz.skillTabKey === t.key ? ' selected' : '') + '>' + t.label + '</option>';
            });
        }
        h += '</select></div></div>';
        h += '</div>';
        h += navButtons(true, true);
        h += '</div>';
        return h;
    }

    $(document).on('click', '.gw-school-btn', function () {
        wiz.school = parseInt($(this).data('school'));
        $('.gw-school-btn').removeClass('active');
        $(this).addClass('active');
    });

    $(document).on('change', '#gwClass', function () {
        wiz.classId = parseInt($(this).val()) || 0;
        var $tab = $('#gwTab');
        $tab.html('<option value="">Auto</option>');
        if (wiz.classId && CLASS_SKILL_TABS[wiz.classId]) {
            var tabs = CLASS_SKILL_TABS[wiz.classId];
            tabs.forEach(function (t) {
                $tab.append('<option value="' + t.key + '">' + t.label + '</option>');
            });
            // Auto-select: if only one tab, use it. Otherwise try to match school name.
            if (tabs.length === 1) {
                wiz.skillTabKey = tabs[0].key;
                $tab.val(tabs[0].key);
            } else {
                // Try to match school to tab (e.g., school=Fire → mage_fire)
                var schoolName = (SCHOOL_NAMES[wiz.school] || '').toLowerCase();
                var matched = tabs.find(function (t) { return t.label.toLowerCase() === schoolName; });
                if (matched) {
                    wiz.skillTabKey = matched.key;
                    $tab.val(matched.key);
                } else {
                    wiz.skillTabKey = tabs[0].key; // Default to first tab
                    $tab.val(tabs[0].key);
                }
            }
        } else {
            wiz.skillTabKey = '';
        }
    });

    $(document).on('change', '#gwTab', function () {
        wiz.skillTabKey = $(this).val() || '';
    });

    // ── Description token insert + preview ──

    $(document).on('click', '.gw-desc-token', function () {
        var token = $(this).data('token');
        var $ta = $('#gwDesc');
        var ta = $ta[0];
        var start = ta.selectionStart, end = ta.selectionEnd;
        var val = $ta.val();
        $ta.val(val.substring(0, start) + token + val.substring(end));
        ta.selectionStart = ta.selectionEnd = start + token.length;
        $ta.focus().trigger('input');
    });

    $(document).on('input', '#gwDesc', function () {
        var text = $(this).val();
        if (!text || !text.match(/\$[sod]\d?/)) {
            $('#gwDescPreview').hide();
            return;
        }
        var s1 = wiz.dmgMin > 0 ? wiz.dmgMin + (wiz.dmgMax > wiz.dmgMin ? '-' + wiz.dmgMax : '') : '?';
        var resolved = text
            .replace(/\$s1/g, s1)
            .replace(/\$s2/g, '?')
            .replace(/\$o1/g, '?')
            .replace(/\$o2/g, '?')
            .replace(/\$d/g, '?');
        $('#gwDescPreviewText').text(resolved);
        $('#gwDescPreview').show();
    });

    // ── STEP 3: Power & Balance ──

    function buildStep3() {
        var h = '<div class="gw-step">';
        h += '<div class="gw-step-header"><h2>Set the power level</h2>';
        h += '<p>Adjust damage, mana cost, and timing. Values default to the source spell — tweak or leave as-is.</p></div>';
        h += '<div class="gw-form">';
        // Quick presets
        h += '<div class="gw-field"><label>Quick Adjust</label><div class="gw-preset-row">';
        h += '<button class="gw-preset-btn" data-mult="0.5"><i class="fa-solid fa-feather"></i> Weaker (50%)</button>';
        h += '<button class="gw-preset-btn active" data-mult="1.0"><i class="fa-solid fa-equals"></i> As-is</button>';
        h += '<button class="gw-preset-btn" data-mult="1.5"><i class="fa-solid fa-arrow-up"></i> Stronger (150%)</button>';
        h += '<button class="gw-preset-btn" data-mult="2.0"><i class="fa-solid fa-fire"></i> Double</button>';
        h += '</div></div>';
        // Damage
        h += '<div class="gw-field-row">';
        h += '<div class="gw-field"><label>Damage Min</label><input type="number" id="gwDmgMin" class="gw-input" value="' + wiz.dmgMin + '" min="0" /></div>';
        h += '<div class="gw-field"><label>Damage Max</label><input type="number" id="gwDmgMax" class="gw-input" value="' + wiz.dmgMax + '" min="0" /></div>';
        h += '<div class="gw-field"><label>Mana Cost</label><input type="number" id="gwMana" class="gw-input" value="' + wiz.manaCost + '" min="0" /></div>';
        h += '</div>';
        // Level, Cast, Range
        h += '<div class="gw-field-row">';
        h += '<div class="gw-field"><label>Spell Level</label><input type="number" id="gwLevel" class="gw-input" value="' + wiz.spellLevel + '" min="1" max="60" /></div>';
        h += '<div class="gw-field"><label>Cast Time</label><select id="gwCast" class="gw-select">';
        h += '<option value="0">Same as source</option>';
        Object.keys(CAST_TIMES).forEach(function (k) {
            h += '<option value="' + k + '"' + (wiz.castTimeIndex == k ? ' selected' : '') + '>' + CAST_TIMES[k] + '</option>';
        });
        h += '</select></div>';
        h += '<div class="gw-field"><label>Range</label><select id="gwRange" class="gw-select">';
        h += '<option value="0">Same as source</option>';
        Object.keys(RANGES).forEach(function (k) {
            h += '<option value="' + k + '"' + (wiz.rangeIndex == k ? ' selected' : '') + '>' + RANGES[k] + '</option>';
        });
        h += '</select></div>';
        h += '</div>';
        h += '</div>';
        h += navButtons(true, true);
        h += '</div>';
        return h;
    }

    function prefillPowerFromSource() {
        // Already set in state from step 1 selection
    }

    $(document).on('click', '.gw-preset-btn', function () {
        var mult = parseFloat($(this).data('mult'));
        if (!wiz.source) return;
        var bp = wiz.source.effectBasePoints1 || 0, ds = wiz.source.effectDieSides1 || 0;
        var baseDmgMin = bp + 1, baseDmgMax = bp + ds + 1, baseMana = wiz.source.manaCost || 0;
        $('#gwDmgMin').val(Math.round(baseDmgMin * mult));
        $('#gwDmgMax').val(Math.round(baseDmgMax * mult));
        $('#gwMana').val(Math.round(baseMana * mult));
        $('.gw-preset-btn').removeClass('active');
        $(this).addClass('active');
    });

    // ── STEP 4: Appearance ──

    // Per-phase knob state for the wizard (mirrors patch-manager.js phaseParams)
    var GW_KNOBS = [
        { key: 'emissionRate', label: 'Particle Density', icon: 'fa-cloud', min: 0.1, max: 10, step: 0.1, def: 1.0 },
        { key: 'scale', label: 'Particle Size', icon: 'fa-expand', min: 0.1, max: 10, step: 0.1, def: 1.0 },
        { key: 'speed', label: 'Particle Speed', icon: 'fa-gauge-high', min: 0.1, max: 5, step: 0.1, def: 1.0 },
        { key: 'lifespan', label: 'Trail Length', icon: 'fa-hourglass', min: 0.1, max: 5, step: 0.1, def: 1.0 },
        { key: 'area', label: 'Spread Area', icon: 'fa-arrows-up-down-left-right', min: 0.1, max: 10, step: 0.1, def: 1.0 }
    ];
    var GW_PHASES = [
        { key: 'precast', label: 'Precast', icon: 'fa-hand-sparkles', desc: 'Glow on your hands while charging' },
        { key: 'cast', label: 'Cast', icon: 'fa-burst', desc: 'Flash when you release' },
        { key: 'missile', label: 'Missile', icon: 'fa-meteor', desc: 'Projectile in flight' },
        { key: 'impact', label: 'Impact', icon: 'fa-explosion', desc: 'Hit explosion on target' }
    ];

    // Init per-phase state
    wiz.gwPhaseParams = {};
    GW_PHASES.forEach(function (p) {
        wiz.gwPhaseParams[p.key] = {};
        GW_KNOBS.forEach(function (k) { wiz.gwPhaseParams[p.key][k.key] = k.def; });
    });
    wiz.intensity = 1.0;
    wiz.advancedVisuals = false;

    var GW_VISUAL_RECIPES = {
        subtle: { emissionRate: 0.5, scale: 0.7, speed: 0.8, lifespan: 0.8, area: 0.5 },
        powerful: { emissionRate: 2.0, scale: 1.5, speed: 1.2, lifespan: 1.5, area: 1.5 },
        cosmic: { emissionRate: 3.0, scale: 1.2, speed: 2.0, lifespan: 2.5, area: 2.0 },
        nova: { emissionRate: 1.5, scale: 1.0, speed: 1.0, lifespan: 1.0, area: 1.0, impact: { emissionRate: 5, scale: 4, speed: 3, lifespan: 2.5, area: 5 } },
        reset: { emissionRate: 1.0, scale: 1.0, speed: 1.0, lifespan: 1.0, area: 1.0 }
    };

    function buildStep4() {
        var h = '<div class="gw-step">';
        h += '<div class="gw-step-header"><h2>Choose the look</h2>';
        h += '<p>Pick a particle color, adjust intensity, and optionally fine-tune individual spell phases.</p></div>';
        h += '<div class="gw-form">';

        // Color grid
        h += '<div class="gw-field"><label>Particle Color</label>';
        h += '<div class="gw-color-grid">';
        COLOR_PRESETS.forEach(function (p) {
            h += '<button class="gw-color-card' + (wiz.colorPreset === p.key ? ' active' : '') + '" data-preset="' + p.key + '">';
            h += '<div class="gw-color-swatch" style="background:' + p.swatch + '"></div>';
            h += '<div class="gw-color-label">' + p.label + '</div>';
            h += '<div class="gw-color-desc">' + p.desc + '</div>';
            h += '</button>';
        });
        h += '<button class="gw-color-card' + (wiz.colorPreset === 'custom' ? ' active' : '') + '" data-preset="custom">';
        h += '<div class="gw-color-swatch" style="background:' + (wiz.customColor || '#b432ff') + '"><i class="fa-solid fa-palette" style="font-size:18px;line-height:48px;color:rgba(255,255,255,.7)"></i></div>';
        h += '<div class="gw-color-label">Custom</div>';
        h += '<div class="gw-color-desc">Pick any color</div>';
        h += '</button>';
        h += '</div></div>';

        if (wiz.colorPreset === 'custom') {
            h += '<div class="gw-field" style="max-width:200px"><label>Custom Color</label>';
            h += '<input type="color" id="gwCustomColor" class="gw-color-picker" value="' + (wiz.customColor || '#b432ff') + '" /></div>';
        }

        // Intensity slider
        h += '<div class="gw-field"><label>Particle Intensity</label>';
        h += '<div class="gw-intensity-row">';
        h += '<input type="range" id="gwIntensity" class="gw-intensity-slider" min="0.5" max="5.0" step="0.1" value="' + wiz.intensity + '" />';
        h += '<span class="gw-intensity-val" id="gwIntensityVal">' + wiz.intensity.toFixed(1) + '×</span>';
        h += '</div>';
        h += '<div class="gw-hint">Controls overall particle density, size, and spread. 1.0× = vanilla.</div>';
        h += '</div>';

        // Visual recipe presets
        h += '<div class="gw-field"><label>Quick Recipes</label>';
        h += '<div class="gw-preset-row">';
        ['subtle', 'powerful', 'cosmic', 'nova', 'reset'].forEach(function (r) {
            h += '<button class="gw-recipe-btn" data-recipe="' + r + '"><i class="fa-solid ' +
                (r === 'subtle' ? 'fa-feather' : r === 'powerful' ? 'fa-fire' : r === 'cosmic' ? 'fa-star' : r === 'nova' ? 'fa-explosion' : 'fa-rotate') +
                '"></i> ' + r.charAt(0).toUpperCase() + r.slice(1) + '</button>';
        });
        h += '</div>';
        h += '<div class="gw-hint">Recipes adjust per-phase particle knobs. Open Fine Tune below to see/modify individual values.</div>';
        h += '</div>';

        // Expandable per-phase designer
        h += '<div class="gw-field">';
        h += '<button class="gw-designer-toggle" id="gwToggleDesigner">';
        h += '<i class="fa-solid fa-sliders"></i> Fine Tune Per-Phase';
        h += '<span class="gw-designer-toggle-hint">' + (wiz.advancedVisuals ? 'Click to collapse' : 'Particle density, size, speed per phase') + '</span>';
        h += '<i class="fa-solid ' + (wiz.advancedVisuals ? 'fa-chevron-up' : 'fa-chevron-down') + '" style="margin-left:auto;font-size:10px;color:#98a2b3"></i>';
        h += '</button>';
        h += '<div id="gwDesignerPanel" style="' + (wiz.advancedVisuals ? '' : 'display:none;') + 'margin-top:12px">';

        GW_PHASES.forEach(function (phase) {
            h += '<div class="gw-phase-card">';
            h += '<div class="gw-phase-header"><i class="fa-solid ' + phase.icon + '" style="color:#528bff;margin-right:6px"></i>';
            h += '<strong>' + phase.label + '</strong>';
            h += '<span style="color:#98a2b3;font-size:11px;margin-left:8px">' + phase.desc + '</span></div>';
            h += '<div class="gw-phase-knobs">';
            GW_KNOBS.forEach(function (k) {
                var val = wiz.gwPhaseParams[phase.key][k.key];
                var id = 'gwk-' + phase.key + '-' + k.key;
                h += '<div class="gw-knob-row">';
                h += '<label for="' + id + '"><i class="fa-solid ' + k.icon + '" style="color:#98a2b3;width:16px;text-align:center;margin-right:6px"></i>' + k.label + '</label>';
                h += '<input type="range" id="' + id + '" class="gw-knob-slider" min="' + k.min + '" max="' + k.max + '" step="' + k.step + '" value="' + val + '" data-phase="' + phase.key + '" data-knob="' + k.key + '" />';
                h += '<span class="gw-knob-val">' + val.toFixed(1) + '×</span>';
                h += '</div>';
            });
            h += '</div></div>';
        });
        h += '</div></div>';

        // Icon generation
        h += '<div class="gw-field" style="margin-top:8px">';
        h += '<label class="gw-checkbox-label"><input type="checkbox" id="gwGenIcon"' + (wiz.generateIcon ? ' checked' : '') + ' /> ';
        h += '<i class="fa-solid fa-wand-magic-sparkles"></i> Generate AI spell icon</label>';
        h += '<div class="gw-hint">Uses ComfyUI/FLUX to create a unique icon. Takes ~15 seconds.</div>';
        h += '</div>';

        // Existing icon picker
        if (wiz.customIcons.length > 0) {
            h += '<div class="gw-field"><label>Or reuse an existing icon</label>';
            h += '<div class="gw-icon-grid">';
            wiz.customIcons.forEach(function (ic) {
                h += '<div class="gw-icon-item' + (wiz.existingIconPath === ic.path ? ' active' : '') + '" data-path="' + esc(ic.path) + '" data-name="' + esc(ic.name) + '" title="' + esc(ic.name) + '">';
                h += '<img src="' + esc(ic.webPath) + '" /></div>';
            });
            h += '</div>';
            if (wiz.existingIconPath) {
                h += '<div class="gw-icon-selected"><i class="fa-solid fa-check"></i> Using: <strong>' + esc(wiz.existingIconName) + '</strong>';
                h += ' <span class="gw-icon-clear" id="gwClearIcon"><i class="fa-solid fa-xmark"></i> clear</span></div>';
            }
            h += '</div>';
        }

        h += '</div>';
        h += navButtons(true, true);
        h += '</div>';
        return h;
    }

    $(document).on('click', '.gw-color-card', function () {
        wiz.colorPreset = $(this).data('preset');
        $('.gw-color-card').removeClass('active');
        $(this).addClass('active');
        if (wiz.colorPreset === 'custom' && !$('#gwCustomColor').length) {
            renderStep();
        }
    });

    $(document).on('input', '#gwCustomColor', function () {
        wiz.customColor = $(this).val();
    });

    // Icon picker
    $(document).on('click', '.gw-icon-item', function () {
        wiz.existingIconPath = $(this).data('path');
        wiz.existingIconName = $(this).data('name');
        wiz.generateIcon = false;
        renderStep(); // Re-render to show selection + uncheck AI gen
    });
    $(document).on('click', '#gwClearIcon', function () {
        wiz.existingIconPath = null;
        wiz.existingIconName = null;
        renderStep();
    });
    $(document).on('change', '#gwGenIcon', function () {
        wiz.generateIcon = $(this).is(':checked');
        if (wiz.generateIcon) {
            wiz.existingIconPath = null;
            wiz.existingIconName = null;
            renderStep(); // Re-render to clear icon selection
        }
    });

    $(document).on('input', '#gwIntensity', function () {
        wiz.intensity = parseFloat($(this).val());
        $('#gwIntensityVal').text(wiz.intensity.toFixed(1) + '×');
    });

    $(document).on('click', '#gwToggleDesigner', function () {
        wiz.advancedVisuals = !wiz.advancedVisuals;
        $('#gwDesignerPanel').slideToggle(200);
        $(this).find('.gw-designer-toggle-hint').text(wiz.advancedVisuals ? 'Click to collapse' : 'Particle density, size, speed per phase');
        $(this).find('i:last').toggleClass('fa-chevron-down fa-chevron-up');
    });

    $(document).on('input', '.gw-knob-slider', function () {
        var p = $(this).data('phase'), k = $(this).data('knob'), v = parseFloat($(this).val());
        wiz.gwPhaseParams[p][k] = v;
        $(this).next('.gw-knob-val').text(v.toFixed(1) + '×');
    });

    $(document).on('click', '.gw-recipe-btn', function () {
        var r = $(this).data('recipe');
        var recipe = GW_VISUAL_RECIPES[r];
        if (!recipe) return;
        GW_PHASES.forEach(function (phase) {
            var phaseRecipe = recipe[phase.key] || recipe;
            GW_KNOBS.forEach(function (k) {
                var val = phaseRecipe[k.key] || k.def;
                wiz.gwPhaseParams[phase.key][k.key] = val;
                var $s = $('#gwk-' + phase.key + '-' + k.key);
                if ($s.length) {
                    var clamped = Math.min(Math.max(val, k.min), k.max);
                    $s.val(clamped);
                    $s.next('.gw-knob-val').text(clamped.toFixed(1) + '×');
                }
            });
        });
        $('.gw-recipe-btn').removeClass('active');
        $(this).addClass('active');
        setTimeout(function () { $('.gw-recipe-btn').removeClass('active'); }, 600);
        // Auto-expand designer if not already open
        if (!wiz.advancedVisuals) {
            wiz.advancedVisuals = true;
            $('#gwDesignerPanel').slideDown(200);
            $('#gwToggleDesigner .gw-designer-toggle-hint').text('Click to collapse');
            $('#gwToggleDesigner i:last').removeClass('fa-chevron-down').addClass('fa-chevron-up');
        }
    });

    // ── STEP 5: Ranks & Training ──

    function buildStep5() {
        var h = '<div class="gw-step">';
        h += '<div class="gw-step-header"><h2>Ranks &amp; training</h2>';
        h += '<p>Generate a full rank progression and choose how players learn the spell.</p></div>';
        h += '<div class="gw-form">';
        // Rank generation
        h += '<div class="gw-field">';
        h += '<label class="gw-checkbox-label"><input type="checkbox" id="gwAllRanks"' + (wiz.generateAllRanks ? ' checked' : '') + ' /> ';
        h += '<i class="fa-solid fa-layer-group"></i> Generate all ranks</label>';
        h += '<div class="gw-hint">Mirrors the source spell\'s rank progression with proportional damage/mana scaling.</div>';
        h += '</div>';
        h += '<div id="gwRankPreview" style="margin-top:12px"></div>';
        // Trainer
        h += '<div class="gw-field" style="margin-top:20px"><label>Trainer Registration</label>';
        h += '<div class="gw-radio-group">';
        h += '<label class="gw-radio"><input type="radio" name="gwTrainer" value="none"' + (wiz.trainerMode === 'none' ? ' checked' : '') + ' /> No trainer</label>';
        h += '<label class="gw-radio"><input type="radio" name="gwTrainer" value="classTemplate"' + (wiz.trainerMode === 'classTemplate' ? ' checked' : '') + ' /> Add to all class trainers</label>';
        h += '<label class="gw-radio"><input type="radio" name="gwTrainer" value="copySource"' + (wiz.trainerMode === 'copySource' ? ' checked' : '') + ' /> Copy from source spell</label>';
        h += '</div></div>';
        // Teach to character
        h += '<div class="gw-field"><label>Teach to Character</label>';
        h += '<select id="gwTeachChar" class="gw-select"><option value="0">None — teach later</option>';
        wiz.characters.forEach(function (c) {
            h += '<option value="' + c.guid + '">' + esc(c.name) + ' (Lv' + c.level + ' ' + (CLASS_NAMES[c.charClass] || '?') + ')</option>';
        });
        h += '</select></div>';
        h += '</div>';
        h += navButtons(true, true);
        h += '</div>';
        return h;
    }

    function loadRanksForWizard() {
        if (!wiz.source) return;
        if (wiz.sourceRanks.length > 0) { renderRankPreviewWizard(); return; }
        $.getJSON('/Patch/SourceRanks', { entry: wiz.source.entry }, function (d) {
            wiz.sourceRanks = d.ranks || [];
            renderRankPreviewWizard();
        });
    }

    function renderRankPreviewWizard() {
        var $p = $('#gwRankPreview');
        if (!$('#gwAllRanks').is(':checked')) { $p.empty(); return; }
        if (!wiz.sourceRanks.length) { $p.html('<div class="gw-hint">Source has no rank chain — only Rank 1 will be created.</div>'); return; }
        var src1 = wiz.sourceRanks[0];
        var src1Bp = src1.effectBasePoints1 || 0, src1Ds = src1.effectDieSides1 || 0, src1Mana = src1.manaCost || 0;
        var userBp = wiz.dmgMin - 1, userDs = wiz.dmgMax - wiz.dmgMin;
        var dmgRatio = (src1Bp + src1Ds) > 0 ? (userBp + userDs) / (src1Bp + src1Ds) : 1;
        var manaRatio = src1Mana > 0 ? wiz.manaCost / src1Mana : 1;

        var h = '<div class="gw-rank-table"><div class="gw-rank-header">';
        h += '<span>Rank</span><span>Level</span><span>Damage</span><span>Mana</span><span>Cast</span>';
        h += '</div>';
        wiz.sourceRanks.forEach(function (r, i) {
            var rank = r.rank || (i + 1);
            var bp = Math.round((r.effectBasePoints1 || 0) * dmgRatio);
            var ds = Math.round((r.effectDieSides1 || 0) * dmgRatio);
            var mn = Math.round((r.manaCost || 0) * manaRatio);
            var cti = r.castingTimeIndex || 0;
            var castLabel = CAST_TIMES[cti] || cti;
            h += '<div class="gw-rank-row' + (rank === 1 ? ' gw-rank-r1' : '') + '">';
            h += '<span class="gw-rank-num">R' + rank + '</span>';
            h += '<span>' + (r.spellLevel || '?') + '</span>';
            h += '<span class="gw-rank-dmg">' + (bp + 1) + '–' + (bp + ds + 1) + '</span>';
            h += '<span class="gw-rank-mana">' + mn + '</span>';
            h += '<span>' + castLabel + '</span>';
            h += '</div>';
        });
        h += '</div>';
        h += '<div class="gw-hint">' + wiz.sourceRanks.length + ' ranks, scaling ×' + dmgRatio.toFixed(2) + ' damage, ×' + manaRatio.toFixed(2) + ' mana</div>';
        $p.html(h);
    }

    $(document).on('change', '#gwAllRanks', function () {
        wiz.generateAllRanks = $(this).is(':checked');
        renderRankPreviewWizard();
    });

    $(document).on('change', 'input[name="gwTrainer"]', function () {
        wiz.trainerMode = $(this).val();
    });

    // ── STEP 6: Review & Create ──

    function buildStep6() {
        var sc = SCHOOL_NAMES[wiz.school] || '?', c = SCHOOL_COLORS[wiz.school] || '#999';
        var h = '<div class="gw-step">';
        h += '<div class="gw-step-header"><h2>Review &amp; create</h2>';
        h += '<p>Everything looks good? Hit create to generate the spell and build the patch.</p></div>';
        // Summary card
        h += '<div class="gw-summary">';
        h += '<div class="gw-summary-title" style="color:' + c + '"><i class="fa-solid ' + (SCHOOL_ICONS[wiz.school] || 'fa-circle') + '"></i> ' + esc(wiz.name || 'Unnamed Spell') + '</div>';
        h += '<div class="gw-summary-grid">';
        h += summaryRow('Base Spell', esc((wiz.source || {}).name || '?') + ' #' + ((wiz.source || {}).entry || '?'));
        h += summaryRow('School', '<span style="color:' + c + '">' + sc + '</span>');
        if (wiz.description) h += summaryRow('Tooltip', '<span style="font-size:11px;font-family:monospace;word-break:break-all">' + esc(wiz.description.substring(0, 80)) + (wiz.description.length > 80 ? '...' : '') + '</span>');
        if (wiz.classId) h += summaryRow('Class', CLASS_NAMES[wiz.classId] || '?');
        if (wiz.skillTabKey) h += summaryRow('Tab', wiz.skillTabKey.split('_').pop());
        h += summaryRow('Damage', wiz.dmgMin + '–' + wiz.dmgMax);
        h += summaryRow('Mana', wiz.manaCost);
        h += summaryRow('Level', wiz.spellLevel);
        var castLabel = CAST_TIMES[wiz.castTimeIndex] || 'Source default';
        h += summaryRow('Cast', castLabel);
        h += summaryRow('Color', wiz.colorPreset === 'custom' ? 'Custom (' + (wiz.customColor || '#b432ff') + ')' : (wiz.colorPreset || 'Original'));
        if (wiz.intensity !== 1.0) h += summaryRow('Intensity', wiz.intensity.toFixed(1) + '×');
        if (wiz.advancedVisuals) h += summaryRow('Visual Tuning', 'Per-phase custom');
        if (wiz.generateIcon) h += summaryRow('Icon', '<i class="fa-solid fa-wand-magic-sparkles"></i> AI Generated');
        else if (wiz.existingIconPath) h += summaryRow('Icon', '<i class="fa-solid fa-image"></i> ' + esc(wiz.existingIconName));
        if (wiz.generateAllRanks && wiz.sourceRanks.length > 1) h += summaryRow('Ranks', wiz.sourceRanks.length + ' ranks (proportional scaling)');
        if (wiz.trainerMode !== 'none') h += summaryRow('Trainer', wiz.trainerMode === 'classTemplate' ? 'All class trainers' : 'Copy from source');
        h += '</div></div>';
        h += '<div id="gwResult"></div>';
        h += '<div class="gw-nav">';
        h += '<button class="gw-btn gw-btn-back" id="gwBack"><i class="fa-solid fa-arrow-left"></i> Back</button>';
        h += '<button class="gw-btn gw-btn-create" id="gwCreate"><i class="fa-solid fa-wand-magic-sparkles"></i> Create Spell</button>';
        h += '</div>';
        h += '</div>';
        return h;
    }

    function summaryRow(label, value) {
        return '<div class="gw-summary-row"><span class="gw-summary-label">' + label + '</span><span class="gw-summary-value">' + value + '</span></div>';
    }

    // ── Navigation ──

    $(document).on('click', '#gwBack', function () {
        saveStepState();
        if (wiz.step > 1) { wiz.step--; renderStep(); }
    });

    $(document).on('click', '#gwNext', function () {
        if (!validateStep()) return;
        saveStepState();
        if (wiz.step < 6) { wiz.step++; renderStep(); }
    });

    function saveStepState() {
        switch (wiz.step) {
            case 2:
                wiz.name = ($('#gwName').val() || '').trim();
                wiz.description = ($('#gwDesc').val() || '').trim();
                wiz.classId = parseInt($('#gwClass').val()) || 0;
                wiz.skillTabKey = $('#gwTab').val() || '';
                break;
            case 3:
                wiz.dmgMin = parseInt($('#gwDmgMin').val()) || 0;
                wiz.dmgMax = parseInt($('#gwDmgMax').val()) || 0;
                wiz.manaCost = parseInt($('#gwMana').val()) || 0;
                wiz.spellLevel = parseInt($('#gwLevel').val()) || 1;
                wiz.castTimeIndex = parseInt($('#gwCast').val()) || 0;
                wiz.rangeIndex = parseInt($('#gwRange').val()) || 0;
                break;
            case 4:
                wiz.generateIcon = $('#gwGenIcon').is(':checked');
                wiz.intensity = parseFloat($('#gwIntensity').val()) || 1.0;
                // Per-phase knob state is already saved via live input handlers
                break;
            case 5:
                wiz.generateAllRanks = $('#gwAllRanks').is(':checked');
                wiz.teachCharGuid = parseInt($('#gwTeachChar').val()) || 0;
                break;
        }
    }

    function validateStep() {
        switch (wiz.step) {
            case 1:
                if (!wiz.source) { shake('#gwSearch'); return false; }
                return true;
            case 2:
                var name = ($('#gwName').val() || '').trim();
                if (!name) { shake('#gwName'); return false; }
                return true;
            default:
                return true;
        }
    }

    function shake(sel) {
        $(sel).addClass('gw-shake');
        setTimeout(function () { $(sel).removeClass('gw-shake'); }, 500);
    }

    // ── Generate ──

    $(document).on('click', '#gwCreate', function () {
        var $b = $(this);
        $b.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Creating...');

        var payload = {
            spellName: wiz.name,
            nameSubtext: 'Rank 1',
            description: wiz.description || null,
            sourceSpellEntry: wiz.source.entry,
            sourceSpellName: wiz.source.name,
            school: wiz.school,
            colorPreset: wiz.colorPreset === 'custom' ? 'custom' : (wiz.colorPreset === 'fire' ? null : wiz.colorPreset),
            customColor: wiz.colorPreset === 'custom' ? (wiz.customColor || '#b432ff') : null,
            intensity: wiz.intensity,
            generateIcon: wiz.generateIcon,
            existingIconPath: wiz.existingIconPath || null,
            teachToCharacterGuid: wiz.teachCharGuid,
            usePerPhaseParams: wiz.advancedVisuals,
            phaseParams: wiz.advancedVisuals ? {
                precast: wiz.gwPhaseParams.precast || null,
                cast: wiz.gwPhaseParams.cast || null,
                missile: wiz.gwPhaseParams.missile || null,
                impact: wiz.gwPhaseParams.impact || null
            } : null,
            skillTabKey: wiz.skillTabKey || null,
            damageMin: wiz.dmgMin > 0 ? wiz.dmgMin : null,
            damageMax: wiz.dmgMax > 0 ? wiz.dmgMax : null,
            manaCost: wiz.manaCost > 0 ? wiz.manaCost : null,
            spellLevel: wiz.spellLevel > 0 ? wiz.spellLevel : null,
            castingTimeIndex: wiz.castTimeIndex > 0 ? wiz.castTimeIndex : null,
            rangeIndex: wiz.rangeIndex > 0 ? wiz.rangeIndex : null,
            generateAllRanks: wiz.generateAllRanks,
            copySourceTrainers: wiz.trainerMode === 'copySource',
            rankOverrides: null
        };

        $.ajax({
            url: '/Patch/Generate', method: 'POST', contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (r) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-wand-magic-sparkles"></i> Create Spell');
                if (r.success) {
                    var h = '<div class="gw-result gw-result-success">';
                    h += '<div class="gw-result-icon"><i class="fa-solid fa-check-circle"></i></div>';
                    h += '<div class="gw-result-title">' + esc(wiz.name) + ' created!</div>';
                    h += '<div class="gw-result-entry">Spell #' + r.spellEntry + '</div>';
                    if (r.ranksGenerated && r.ranksGenerated.length > 1) h += '<div class="gw-result-detail"><i class="fa-solid fa-layer-group"></i> ' + r.ranksGenerated.length + ' ranks generated</div>';
                    if (r.taught) h += '<div class="gw-result-detail"><i class="fa-solid fa-graduation-cap"></i> Taught to character — relog required</div>';
                    if (r.iconResult) h += '<div class="gw-result-detail"><i class="fa-solid fa-image"></i> Icon: ' + esc(r.iconResult.IconName || r.iconResult.iconName || '') + '</div>';
                    h += '<div class="gw-result-detail" style="margin-top:12px;font-weight:600">Restart server &amp; copy patch to client Data/ folder</div>';
                    if (r.hasPatch) {
                        h += '<a href="/Patch/Download?file=' + encodeURIComponent(r.patchFileName) + '" class="gw-btn gw-btn-download" style="margin-top:12px;display:inline-flex">';
                        h += '<i class="fa-solid fa-download"></i> Download ' + esc(r.patchFileName) + '</a>';
                    }
                    h += '<button class="gw-btn gw-btn-next" id="gwCreateAnother" style="margin-top:12px"><i class="fa-solid fa-plus"></i> Create Another</button>';
                    h += '</div>';
                    $('#gwResult').html(h);

                    // Handle trainer registration
                    // "Copy from source" is now handled server-side via CopySourceTrainers flag
                    // on the generate request (per-rank copying in GenerateRankChainAsync).
                    // "Add to all class trainers" still uses the separate endpoint for R1 only.
                    if (wiz.trainerMode === 'classTemplate' && wiz.classId) {
                        $.ajax({
                            url: '/Patch/RegisterAtClassTrainers', method: 'POST', contentType: 'application/json',
                            data: JSON.stringify({ spellEntry: r.spellEntry, trainerClass: wiz.classId, cost: 0, reqLevel: wiz.spellLevel || 1, spellName: wiz.spellName, rankSubtext: 'Rank 1' })
                        });
                    }

                    // Refresh workshop lists
                    if (typeof loadPatches === 'function') loadPatches();
                    if (typeof loadCustomSpells === 'function') loadCustomSpells();
                } else {
                    $('#gwResult').html('<div class="gw-result gw-result-error"><i class="fa-solid fa-circle-xmark"></i> ' + esc(r.error || 'Unknown error') + '</div>');
                }
            },
            error: function (xhr) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-wand-magic-sparkles"></i> Create Spell');
                $('#gwResult').html('<div class="gw-result gw-result-error"><i class="fa-solid fa-circle-xmark"></i> ' + (xhr.statusText || 'Request failed') + '</div>');
            }
        });
    });

    $(document).on('click', '#gwCreateAnother', function () {
        resetWizard();
        renderStep();
    });

    // ── Helpers ──

    function esc(t) { if (!t && t !== 0) return ''; var d = document.createElement('div'); d.textContent = t; return d.innerHTML; }

    // ── Styles ──

    function injectWizardStyles() {
        if ($('#gwStyles').length) return;
        $('head').append('<style id="gwStyles">' +

            // Mode selector
            '.gw-mode-selector{display:flex;gap:12px;margin-bottom:20px}' +
            '.gw-mode-btn{flex:1;display:flex;flex-direction:column;align-items:center;gap:6px;padding:20px 16px;' +
            'border:2px solid #d0d5dd;border-radius:12px;background:#f8f9fa;cursor:pointer;transition:all .2s;color:#667}' +
            '.gw-mode-btn:hover{border-color:#98a2b3;color:#344054}' +
            '.gw-mode-btn.active{border-color:#528bff;background:#eff4ff;color:#528bff}' +
            '.gw-mode-btn i{font-size:24px}' +
            '.gw-mode-title{font-size:15px;font-weight:700}.gw-mode-desc{font-size:11px;opacity:.7}' +

            // Container
            '.gw-container{max-width:720px;margin:0 auto;padding:8px 0}' +

            // Full-width grid override
            '.spell-creator-grid.gw-fullwidth{grid-template-columns:1fr !important;max-width:100%}' +

            // Progress bar
            '.gw-progress{display:flex;align-items:center;margin-bottom:28px;gap:0}' +
            '.gw-progress-step{display:flex;flex-direction:column;align-items:center;gap:4px;min-width:50px}' +
            '.gw-progress-dot{width:32px;height:32px;border-radius:50%;border:2px solid #d0d5dd;' +
            'display:flex;align-items:center;justify-content:center;font-size:12px;color:#98a2b3;transition:all .3s;background:#f2f4f7}' +
            '.gw-progress-step.active .gw-progress-dot{border-color:#528bff;background:#528bff;color:#fff}' +
            '.gw-progress-step.done .gw-progress-dot{border-color:#528bff;background:#eff4ff;color:#528bff}' +
            '.gw-progress-label{font-size:10px;color:#98a2b3;white-space:nowrap}' +
            '.gw-progress-step.active .gw-progress-label{color:#528bff;font-weight:600}' +
            '.gw-progress-step.done .gw-progress-label{color:#528bff}' +
            '.gw-progress-line{flex:1;height:2px;background:#e4e7ec;margin:0 4px 18px}' +
            '.gw-progress-line.done{background:#528bff}' +

            // Step
            '.gw-step{animation:gwFadeIn .25s ease}' +
            '@keyframes gwFadeIn{from{opacity:0;transform:translateY(8px)}to{opacity:1;transform:none}}' +
            '.gw-step-header{margin-bottom:24px}.gw-step-header h2{font-size:22px;font-weight:700;color:#101828;margin:0 0 6px}' +
            '.gw-step-header p{font-size:13px;color:#667085;margin:0;line-height:1.5}' +

            // Search
            '.gw-search-wrap{position:relative;margin-bottom:12px}' +
            '.gw-search-icon{position:absolute;left:14px;top:50%;transform:translateY(-50%);color:#98a2b3;font-size:14px;z-index:1}' +
            '.gw-search{width:100%;padding:14px 14px 14px 40px;border:2px solid #d0d5dd;border-radius:10px;' +
            'background:#fff;color:#101828;font-size:15px;outline:none;transition:border-color .2s;box-sizing:border-box}' +
            '.gw-search:focus{border-color:#528bff;box-shadow:0 0 0 3px rgba(82,139,255,.12)}' +
            '.gw-search::placeholder{color:#98a2b3}' +
            '.gw-search-results{max-height:320px;overflow-y:auto;border:1px solid #e4e7ec;border-radius:8px;margin-top:4px}' +
            '.gw-search-empty{padding:20px;text-align:center;color:#98a2b3;font-size:13px}' +
            '.gw-search-item{display:flex;align-items:center;gap:12px;padding:10px 14px;cursor:pointer;transition:background .15s;border-bottom:1px solid #f2f4f7}' +
            '.gw-search-item:last-child{border-bottom:none}' +
            '.gw-search-item:hover{background:#f9fafb}' +
            '.gw-search-item-icon{width:32px;height:32px;border-radius:8px;display:flex;align-items:center;justify-content:center;font-size:14px;background:#f2f4f7}' +
            '.gw-search-item-info{flex:1;min-width:0}' +
            '.gw-search-item-name{font-size:13px;font-weight:600;color:#101828}' +
            '.gw-search-item-rank{font-weight:400;color:#98a2b3;font-size:11px}' +
            '.gw-search-item-school{font-size:11px;margin-left:8px}' +
            '.gw-search-item-id{font-size:11px;color:#98a2b3;font-family:monospace}' +

            // Source card
            '.gw-source-card{display:flex;align-items:center;gap:14px;padding:16px;border:2px solid #528bff;' +
            'border-radius:12px;background:#f8faff;margin-bottom:20px}' +
            '.gw-source-icon{width:48px;height:48px;border-radius:12px;display:flex;align-items:center;justify-content:center;font-size:20px;flex-shrink:0}' +
            '.gw-source-info{flex:1;min-width:0}' +
            '.gw-source-name{font-size:16px;font-weight:700;color:#101828}' +
            '.gw-source-rank{font-weight:400;color:#98a2b3;font-size:12px}' +
            '.gw-source-meta{display:flex;flex-wrap:wrap;gap:6px;margin-top:6px}' +
            '.gw-tag{font-size:11px;padding:3px 8px;border-radius:6px;border:1px solid #e4e7ec;background:#f9fafb;color:#344054;white-space:nowrap}' +
            '.gw-tag i{margin-right:3px;font-size:10px}.gw-tag-id{font-family:monospace;color:#98a2b3}' +
            '.gw-source-clear{background:none;border:none;color:#98a2b3;cursor:pointer;font-size:16px;padding:8px;border-radius:8px}' +
            '.gw-source-clear:hover{color:#e74c3c;background:#fef3f2}' +

            // Form
            '.gw-form{display:flex;flex-direction:column;gap:16px}' +
            '.gw-field{display:flex;flex-direction:column;gap:4px}' +
            '.gw-field label{font-size:12px;font-weight:600;color:#344054;text-transform:uppercase;letter-spacing:.5px}' +
            '.gw-field-row{display:flex;gap:12px}.gw-field-row>.gw-field{flex:1}' +
            '.gw-input,.gw-select{padding:10px 12px;border:1px solid #d0d5dd;border-radius:8px;background:#fff;' +
            'color:#101828;font-size:14px;outline:none;transition:border-color .2s,box-shadow .2s;width:100%;box-sizing:border-box}' +
            '.gw-input:focus,.gw-select:focus{border-color:#528bff;box-shadow:0 0 0 3px rgba(82,139,255,.12)}' +
            '.gw-input::placeholder{color:#98a2b3}' +
            '.gw-input-lg{font-size:18px;padding:14px 16px;font-weight:600}' +
            '.gw-hint{font-size:11px;color:#667085;margin-top:2px}' +

            // School buttons
            '.gw-school-grid{display:flex;flex-wrap:wrap;gap:8px}' +
            '.gw-school-btn{padding:8px 14px;border:2px solid #d0d5dd;border-radius:8px;background:#fff;' +
            'color:#667085;cursor:pointer;font-size:12px;font-weight:600;transition:all .15s}' +
            '.gw-school-btn:hover{border-color:var(--sc);color:var(--sc)}' +
            '.gw-school-btn.active{border-color:var(--sc);background:color-mix(in srgb,var(--sc) 8%,#fff);color:var(--sc)}' +
            '.gw-school-btn i{margin-right:4px}' +

            // Presets
            '.gw-preset-row{display:flex;gap:8px;flex-wrap:wrap}' +
            '.gw-preset-btn{padding:8px 14px;border:2px solid #d0d5dd;border-radius:8px;background:#fff;' +
            'color:#667085;cursor:pointer;font-size:12px;font-weight:600;transition:all .15s}' +
            '.gw-preset-btn:hover{border-color:#528bff;color:#528bff}' +
            '.gw-preset-btn.active{border-color:#528bff;background:#eff4ff;color:#528bff}' +
            '.gw-preset-btn i{margin-right:4px}' +

            // Color grid
            '.gw-color-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(100px,1fr));gap:10px}' +
            '.gw-color-card{display:flex;flex-direction:column;align-items:center;gap:6px;padding:14px 8px;' +
            'border:2px solid #d0d5dd;border-radius:10px;background:#fff;cursor:pointer;transition:all .15s}' +
            '.gw-color-card:hover{border-color:#98a2b3}' +
            '.gw-color-card.active{border-color:#528bff;background:#f8faff}' +
            '.gw-color-swatch{width:48px;height:48px;border-radius:10px;border:2px solid rgba(0,0,0,.08)}' +
            '.gw-color-label{font-size:12px;font-weight:700;color:#101828}' +
            '.gw-color-desc{font-size:10px;color:#667085;text-align:center}' +
            '.gw-color-picker{width:48px;height:48px;border:2px solid #d0d5dd;border-radius:8px;cursor:pointer;padding:0;background:none}' +

            // Checkbox / Radio
            '.gw-checkbox-label,.gw-radio{display:flex;align-items:center;gap:8px;font-size:14px;color:#344054;cursor:pointer}' +
            '.gw-checkbox-label input,.gw-radio input{width:16px;height:16px;accent-color:#528bff}' +
            '.gw-radio-group{display:flex;flex-direction:column;gap:8px;margin-top:4px}' +

            // Rank preview table
            '.gw-rank-table{border:1px solid #e4e7ec;border-radius:8px;overflow:hidden;font-size:12px;max-height:260px;overflow-y:auto}' +
            '.gw-rank-header{display:grid;grid-template-columns:50px 50px 1fr 70px 60px;gap:4px;padding:8px 12px;' +
            'background:#f9fafb;font-weight:600;color:#667085;font-size:11px;text-transform:uppercase;letter-spacing:.3px;position:sticky;top:0}' +
            '.gw-rank-row{display:grid;grid-template-columns:50px 50px 1fr 70px 60px;gap:4px;padding:6px 12px;border-top:1px solid #f2f4f7;align-items:center}' +
            '.gw-rank-r1{background:#f8faff}' +
            '.gw-rank-num{font-weight:700;color:#101828}' +
            '.gw-rank-dmg{font-family:monospace;color:#101828}' +
            '.gw-rank-mana{color:#528bff;font-family:monospace}' +

            // Navigation
            '.gw-nav{display:flex;justify-content:space-between;margin-top:28px;padding-top:20px;border-top:1px solid #e4e7ec}' +
            '.gw-btn{padding:12px 24px;border:none;border-radius:10px;font-size:14px;font-weight:600;cursor:pointer;display:flex;align-items:center;gap:8px;transition:all .15s}' +
            '.gw-btn-back{background:#f2f4f7;color:#667085;border:1px solid #d0d5dd}' +
            '.gw-btn-back:hover{color:#344054;border-color:#98a2b3;background:#e4e7ec}' +
            '.gw-btn-next{background:#528bff;color:#fff}.gw-btn-next:hover{background:#3b6fe0}' +
            '.gw-btn-create{background:linear-gradient(135deg,#528bff,#3b6fe0);color:#fff;font-size:16px;padding:14px 32px}' +
            '.gw-btn-create:hover{background:linear-gradient(135deg,#3b6fe0,#2d5bc7)}.gw-btn-create:disabled{opacity:.6;cursor:not-allowed}' +

            // Summary
            '.gw-summary{border:2px solid #e4e7ec;border-radius:12px;padding:20px;margin-bottom:20px;background:#fafbfc}' +
            '.gw-summary-title{font-size:20px;font-weight:700;margin-bottom:16px;display:flex;align-items:center;gap:10px}' +
            '.gw-summary-grid{display:flex;flex-direction:column;gap:0}' +
            '.gw-summary-row{display:flex;justify-content:space-between;padding:8px 0;border-bottom:1px solid #f2f4f7}' +
            '.gw-summary-row:last-child{border-bottom:none}' +
            '.gw-summary-label{font-size:12px;color:#667085;text-transform:uppercase;letter-spacing:.3px}' +
            '.gw-summary-value{font-size:13px;font-weight:600;color:#101828}' +

            // Results
            '.gw-result{padding:24px;border-radius:12px;text-align:center;margin-top:16px}' +
            '.gw-result-success{background:#ecfdf3;border:2px solid #a6f4c5}' +
            '.gw-result-error{background:#fef3f2;border:2px solid #fecdca;color:#b42318;font-size:14px}' +
            '.gw-result-icon{font-size:48px;color:#12b76a;margin-bottom:12px}' +
            '.gw-result-title{font-size:20px;font-weight:700;color:#101828}' +
            '.gw-result-entry{font-size:14px;color:#667085;font-family:monospace;margin-top:4px}' +
            '.gw-result-detail{font-size:12px;color:#344054;margin-top:4px}' +

            // Shake animation
            '@keyframes gwShake{0%,100%{transform:translateX(0)}20%,60%{transform:translateX(-6px)}40%,80%{transform:translateX(6px)}}' +
            '.gw-shake{animation:gwShake .4s ease}' +

            // Intensity slider
            '.gw-intensity-row{display:flex;align-items:center;gap:12px}' +
            '.gw-intensity-slider{flex:1;accent-color:#528bff}' +
            '.gw-intensity-val{font-size:14px;font-weight:700;color:#528bff;min-width:40px;text-align:right}' +

            // Recipe buttons
            '.gw-recipe-btn{padding:7px 12px;border:1px solid #d0d5dd;border-radius:6px;background:#fff;' +
            'color:#667085;cursor:pointer;font-size:11px;font-weight:600;transition:all .15s}' +
            '.gw-recipe-btn:hover{border-color:#528bff;color:#528bff}' +
            '.gw-recipe-btn.active{border-color:#528bff;background:#eff4ff;color:#528bff}' +
            '.gw-recipe-btn i{margin-right:3px}' +

            // Designer toggle
            '.gw-designer-toggle{display:flex;align-items:center;gap:8px;width:100%;padding:10px 14px;' +
            'border:1px solid #d0d5dd;border-radius:8px;background:#f9fafb;cursor:pointer;font-size:13px;font-weight:600;color:#344054;transition:all .15s}' +
            '.gw-designer-toggle:hover{border-color:#98a2b3;background:#f2f4f7}' +
            '.gw-designer-toggle i:first-child{color:#528bff}' +
            '.gw-designer-toggle-hint{font-size:11px;font-weight:400;color:#98a2b3;margin-left:8px}' +

            // Phase cards
            '.gw-phase-card{border:1px solid #e4e7ec;border-radius:8px;padding:12px;margin-bottom:10px;background:#fafbfc}' +
            '.gw-phase-header{font-size:13px;color:#344054;margin-bottom:8px;display:flex;align-items:center}' +
            '.gw-phase-knobs{display:flex;flex-direction:column;gap:6px}' +

            // Knob rows
            '.gw-knob-row{display:flex;align-items:center;gap:8px}' +
            '.gw-knob-row label{font-size:11px;color:#667085;min-width:130px;display:flex;align-items:center;white-space:nowrap}' +
            '.gw-knob-slider{flex:1;accent-color:#528bff}' +
            '.gw-knob-val{font-size:11px;font-weight:600;color:#344054;min-width:36px;text-align:right;font-family:monospace}' +

            // Icon picker grid
            '.gw-icon-grid{display:flex;flex-wrap:wrap;gap:6px;max-height:140px;overflow-y:auto;padding:4px 0}' +
            '.gw-icon-item{width:42px;height:42px;border:2px solid #d0d5dd;border-radius:6px;cursor:pointer;overflow:hidden;transition:border-color .15s}' +
            '.gw-icon-item:hover{border-color:#528bff}' +
            '.gw-icon-item.active{border-color:#528bff;box-shadow:0 0 0 2px rgba(82,139,255,.25)}' +
            '.gw-icon-item img{width:100%;height:100%;object-fit:cover}' +
            '.gw-icon-selected{font-size:12px;color:#344054;margin-top:6px;display:flex;align-items:center;gap:6px}' +
            '.gw-icon-clear{cursor:pointer;color:#98a2b3;font-size:11px;margin-left:8px}.gw-icon-clear:hover{color:#e74c3c}' +

            // Download button
            '.gw-btn-download{background:#12b76a;color:#fff;text-decoration:none;border-radius:10px;padding:10px 20px;font-size:14px;font-weight:600;gap:8px;transition:background .15s}' +
            '.gw-btn-download:hover{background:#0e9a59;color:#fff}' +

            // Description token toolbar + preview
            '.gw-desc-toolbar{display:flex;align-items:center;gap:4px;flex-wrap:wrap;margin-bottom:6px}' +
            '.gw-desc-toolbar .desc-toolbar-label{font-size:10px;font-weight:600;color:#98a2b3;text-transform:uppercase;letter-spacing:.3px;margin-right:2px}' +
            '.gw-desc-toolbar .desc-token-btn{padding:3px 8px;font-size:10px;font-weight:600;border:1px solid #d0d5dd;border-radius:4px;background:#f9fafb;color:#667085;cursor:pointer;transition:all .12s;white-space:nowrap}' +
            '.gw-desc-toolbar .desc-token-btn:hover{border-color:#528bff;color:#528bff;background:#eff4ff}' +
            '.gw-desc-toolbar .desc-token-btn i{margin-right:3px;font-size:9px}' +
            '.gw-desc-preview{margin-top:6px;padding:8px 10px;border:1px dashed #d0d5dd;border-radius:8px;background:#f8faff;font-size:12px;line-height:1.5}' +
            '.gw-desc-preview .desc-preview-label{font-size:10px;font-weight:600;color:#528bff;margin-right:6px}' +
            '.gw-desc-preview .desc-preview-text{color:#101828}' +

            '</style>');
    }
});
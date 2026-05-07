// MangosSuperUI — Spell Creator & Patch Manager JS (v5 — Session 15: Visual Studio Mode)

$(function () {

    var SCHOOL_NAMES = { 0: 'Physical', 1: 'Holy', 2: 'Fire', 3: 'Nature', 4: 'Frost', 5: 'Shadow', 6: 'Arcane' };
    var SCHOOL_COLORS = {
        0: '#aaa', 1: '#fff0aa', 2: '#ff6622', 3: '#33cc33',
        4: '#5599ff', 5: '#bb55ff', 6: '#ff88ff'
    };
    var CLASS_NAMES = {
        1: 'Warrior', 2: 'Paladin', 3: 'Hunter', 4: 'Rogue',
        5: 'Priest', 7: 'Shaman', 8: 'Mage', 9: 'Warlock', 11: 'Druid'
    };

    // Session 32: Class → available skill tabs mapping (hardcoded to avoid extra fetch)
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

    var CAST_TIME_OPTIONS = [
        { value: '', label: 'Same as source' },
        { value: '1', label: 'Instant' },
        { value: '5', label: '1.5 sec' },
        { value: '19', label: '2.0 sec' },
        { value: '14', label: '2.5 sec' },
        { value: '15', label: '3.0 sec' },
        { value: '22', label: '3.5 sec' }
    ];

    var RANGE_OPTIONS = [
        { value: '', label: 'Same as source' },
        { value: '1', label: 'Self only' },
        { value: '2', label: '5 yd (Melee)' },
        { value: '3', label: '20 yd' },
        { value: '4', label: '30 yd' },
        { value: '35', label: '35 yd' },
        { value: '6', label: '40 yd' }
    ];

    var PHASES = [
        { key: 'precast', label: 'Precast', icon: 'fa-hand-sparkles', desc: 'The glow on your hands as the spell charges' },
        { key: 'cast', label: 'Cast', icon: 'fa-burst', desc: 'The flare when you release the spell' },
        { key: 'missile', label: 'Missile', icon: 'fa-meteor', desc: 'The projectile flying through the air' },
        { key: 'impact', label: 'Impact', icon: 'fa-explosion', desc: 'The explosion when it hits the target' },
        { key: 'state', label: 'State', icon: 'fa-rotate', desc: 'Persistent aura / buff / debuff loop' },
        { key: 'stateDone', label: 'State End', icon: 'fa-circle-stop', desc: 'Effect when a persistent state expires' },
        { key: 'channel', label: 'Channel', icon: 'fa-bars-staggered', desc: 'Looping effect while channeling the spell' }
    ];

    var KNOBS = [
        { key: 'emissionRate', label: 'Particle Density', icon: 'fa-cloud', min: 0.1, max: 10, step: 0.1, def: 1.0, unit: '\u00d7' },
        { key: 'scale', label: 'Particle Size', icon: 'fa-expand', min: 0.1, max: 10, step: 0.1, def: 1.0, unit: '\u00d7' },
        { key: 'speed', label: 'Particle Speed', icon: 'fa-gauge-high', min: 0.1, max: 5, step: 0.1, def: 1.0, unit: '\u00d7' },
        { key: 'lifespan', label: 'Particle Duration', icon: 'fa-hourglass', min: 0.1, max: 5, step: 0.1, def: 1.0, unit: '\u00d7' },
        { key: 'area', label: 'Spread Area', icon: 'fa-arrows-up-down-left-right', min: 0.1, max: 10, step: 0.1, def: 1.0, unit: '\u00d7' }
    ];

    var BLEND_MODES = [
        { value: '', label: 'Default (unchanged)' },
        { value: '4', label: 'Additive Glow' },
        { value: '2', label: 'Alpha Blend' },
        { value: '1', label: 'Mod' },
        { value: '0', label: 'Opaque' }
    ];

    var EMITTER_TYPES = [
        { value: '', label: 'Default (unchanged)' },
        { value: '0', label: 'Point' },
        { value: '1', label: 'Sphere' },
        { value: '2', label: 'Plane' },
        { value: '3', label: 'Spline' }
    ];

    // ── State ──
    var selectedSource = null;
    var selectedPreset = 'shadow';
    var customColor = null;
    var searchTimer = null;
    var characters = [];
    var advancedMode = false;
    var activePhases = null;
    var customIcons = [];
    var selectedIconPath = null;
    var selectedIconName = null;
    var studioSelectedIconPath = null;
    var studioSelectedIconName = null;

    // ── Visual Studio state ──
    var currentTab = 'quick';
    var textureThemes = [];
    var selectedTheme = null;
    var globalBlendMode = null;
    var m2TextureData = {};
    var generatedTexturePreviews = {};
    var studioPreset = 'none';
    var studioCustomColor = null;

    // ── Per-phase knob state ──
    var phaseParams = {};
    PHASES.forEach(function (p) {
        phaseParams[p.key] = { color: null, blendMode: null, textureTheme: null, emitterType: null };
        KNOBS.forEach(function (k) { phaseParams[p.key][k.key] = k.def; });
    });

    // ===================== INIT =====================

    loadPatches();
    loadCustomSpells();
    loadCharacters();
    buildQuickCreateDesigner();
    buildSpellPropertiesPanel();
    buildDescToolbar();
    loadCustomIcons();
    injectCustomColorPicker();

    // Expose to global scope so spell-guided-wizard.js can call them after creation
    window.loadCustomSpells = loadCustomSpells;
    window.loadPatches = loadPatches;
    window.loadCustomIcons = loadCustomIcons;
    loadTextureThemes();
    initTabSystem();
    injectExtraStyles();

    // ===================== EXTRA STYLES =====================

    function injectExtraStyles() {
        if ($('#spellCreatorExtraStyles').length) return;
        $('head').append('<style id="spellCreatorExtraStyles">' +
            // Icon picker
            '.icon-picker-label{font-size:12px;color:var(--text-secondary);margin:8px 0 6px}' +
            '.icon-picker-grid{display:flex;flex-wrap:wrap;gap:6px;max-height:120px;overflow-y:auto;padding:4px 0}' +
            '.icon-picker-item{width:40px;height:40px;border:2px solid var(--border-light);border-radius:var(--radius-sm);cursor:pointer;overflow:hidden;transition:border-color .15s}' +
            '.icon-picker-item:hover{border-color:var(--accent)}' +
            '.icon-picker-item.selected{border-color:var(--accent);box-shadow:0 0 0 1px var(--accent)}' +
            '.icon-picker-item img{width:100%;height:100%;object-fit:cover}' +
            '.icon-picker-selected{font-size:12px;color:var(--text-secondary);margin-top:6px;display:flex;align-items:center;gap:6px}' +
            '.icon-picker-clear{cursor:pointer;color:var(--text-muted)}.icon-picker-clear:hover{color:#e74c3c}' +
            // Custom color wrap
            '.custom-color-wrap{display:inline-flex;align-items:center;gap:6px;margin-left:6px;vertical-align:middle}' +
            '.custom-color-input{width:32px;height:32px;border:2px solid var(--border-light);border-radius:var(--radius-sm);cursor:pointer;padding:0;background:none;-webkit-appearance:none}' +
            '.custom-color-input::-webkit-color-swatch-wrapper{padding:0}.custom-color-input::-webkit-color-swatch{border:none;border-radius:2px}' +
            '.custom-color-wrap.active .custom-color-input{border-color:var(--accent);box-shadow:0 0 0 1px var(--accent)}' +
            '.custom-color-hex{font-size:12px;font-family:monospace;color:var(--text-secondary);min-width:60px}' +
            // Phase color row
            '.phase-color-row{display:flex;align-items:center;gap:8px;padding:6px 0 8px;border-bottom:1px solid var(--border-light);margin-bottom:6px}' +
            '.phase-color-label{font-size:12px;color:var(--text-secondary);white-space:nowrap}' +
            '.phase-color-input{width:28px;height:28px;border:2px solid var(--border-light);border-radius:var(--radius-sm);cursor:pointer;padding:0;background:none;-webkit-appearance:none}' +
            '.phase-color-input::-webkit-color-swatch-wrapper{padding:0}.phase-color-input::-webkit-color-swatch{border:none;border-radius:2px}' +
            '.phase-color-input.active{border-color:var(--accent);box-shadow:0 0 0 1px var(--accent)}' +
            '.phase-color-hex{font-size:11px;font-family:monospace;color:var(--text-muted)}' +
            '.phase-color-clear{font-size:11px;cursor:pointer;color:var(--text-muted);text-decoration:underline}.phase-color-clear:hover{color:#e74c3c}' +
            '.phase-color-badge{font-size:10px;color:var(--text-muted);font-style:italic}' +
            // Tab system
            '.sc-tabs{display:flex;gap:0;margin-bottom:16px;border:1px solid var(--border-light);border-radius:var(--radius-md);overflow:hidden}' +
            '.sc-tab{flex:1;padding:10px 16px;text-align:center;font-size:13px;font-weight:600;cursor:pointer;border:none;background:var(--surface-alt);color:var(--text-muted);transition:all .15s}' +
            '.sc-tab:first-child{border-right:1px solid var(--border-light)}' +
            '.sc-tab:hover{color:var(--text-primary)}.sc-tab.active{background:var(--accent);color:#fff}' +
            '.sc-tab i{margin-right:6px}' +
            // Theme picker
            '.theme-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(120px,1fr));gap:8px;margin-top:8px}' +
            '.theme-card{padding:10px;border:2px solid var(--border-light);border-radius:var(--radius-md);cursor:pointer;text-align:center;transition:all .15s}' +
            '.theme-card:hover{border-color:var(--text-muted)}.theme-card.selected{border-color:var(--accent);background:rgba(85,153,255,.08)}' +
            '.theme-swatch{width:28px;height:28px;border-radius:50%;margin:0 auto 6px;border:2px solid rgba(255,255,255,.15)}' +
            '.theme-name{font-size:12px;font-weight:600;color:var(--text-primary)}.theme-desc{font-size:10px;color:var(--text-muted);margin-top:2px}' +
            // Blend select
            '.blend-select{padding:6px 10px;background:var(--bg-input);border:1px solid var(--border-light);border-radius:var(--radius-sm);color:var(--text-primary);font-size:12px}' +
            // Studio extras
            '.studio-phase-extras{padding:8px 0;border-top:1px solid var(--border-light);margin-top:6px}' +
            '.studio-extra-row{display:flex;align-items:center;gap:10px;padding:4px 0;font-size:12px}' +
            '.studio-extra-label{color:var(--text-muted);min-width:110px;white-space:nowrap;display:flex;align-items:center;gap:6px}' +
            '.studio-extra-label i{width:14px;text-align:center}' +
            // Texture slot cards
            '.texture-slots-wrap{margin-top:8px;border:1px solid var(--border-light);border-radius:var(--radius-sm);overflow:hidden}' +
            '.texture-slots-header{padding:8px 12px;background:var(--surface-alt);font-size:12px;font-weight:600;color:var(--text-secondary);display:flex;align-items:center;justify-content:space-between;cursor:pointer}' +
            '.texture-slots-header:hover{color:var(--text-primary)}' +
            '.texture-slots-body{display:none}.texture-slots-body.open{display:block}' +
            '.texture-slot-card{padding:10px 12px;border-bottom:1px solid var(--border-light)}.texture-slot-card:last-child{border-bottom:none}' +
            '.texture-slot-top{display:flex;align-items:center;gap:10px;margin-bottom:6px}' +
            '.texture-slot-index{font-family:monospace;font-size:11px;color:var(--text-muted);background:var(--surface-alt);padding:2px 6px;border-radius:3px}' +
            '.texture-slot-filename{font-size:11px;color:var(--text-secondary);font-family:monospace;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
            '.texture-slot-role{font-size:10px;padding:2px 8px;border-radius:10px;font-weight:600}' +
            '.role-glow{background:#332b00;color:#ffdd66;border:1px solid #ffdd6644}.role-shape{background:#331a00;color:#ff8844;border:1px solid #ff884444}' +
            '.role-ring{background:#001a33;color:#44aaff;border:1px solid #44aaff44}.role-ribbon{background:#003318;color:#44ff88;border:1px solid #44ff8844}' +
            '.role-atlas{background:#1a0033;color:#aa88ff;border:1px solid #aa88ff44}.role-bloom{background:#222;color:#fff;border:1px solid #ffffff44}' +
            '.role-body{background:#1a0000;color:#ff6666;border:1px solid #ff666644}' +
            '.texture-slot-prompt{width:100%;padding:6px 8px;background:var(--bg-input);border:1px solid var(--border-light);border-radius:var(--radius-sm);color:var(--text-primary);font-size:11px;resize:vertical;min-height:40px;margin-top:4px;font-family:inherit;box-sizing:border-box}' +
            '.texture-slot-prompt:focus{outline:none;border-color:var(--accent)}' +
            '.texture-slot-actions{display:flex;gap:6px;margin-top:6px;align-items:center}' +
            '.texture-gen-btn{padding:4px 10px;font-size:11px;border:1px solid var(--border-light);border-radius:var(--radius-sm);background:var(--surface-alt);color:var(--text-muted);cursor:pointer}' +
            '.texture-gen-btn:hover{border-color:var(--accent);color:var(--accent)}' +
            '.texture-preview-img{width:48px;height:48px;border-radius:4px;image-rendering:pixelated;border:1px solid var(--border-light)}' +
            // Studio sections
            '.studio-section{margin-top:16px}.studio-section-title{font-size:13px;font-weight:600;color:var(--text-primary);margin-bottom:8px;display:flex;align-items:center;gap:8px}' +
            '.studio-section-title i{color:var(--accent);font-size:12px}' +
            '.studio-hint{font-size:11px;color:var(--text-muted);margin-bottom:8px;font-style:italic}' +
            // Session 29: Retune modal
            '.retune-overlay{position:fixed;top:0;left:0;right:0;bottom:0;background:rgba(0,0,0,.75);z-index:99999;display:flex;align-items:center;justify-content:center}' +
            '.retune-modal{background:white;border:1px solid var(--border-light);border-radius:var(--radius-lg);width:800px;max-width:95vw;max-height:90vh;display:flex;flex-direction:column;box-shadow:0 24px 64px rgba(0,0,0,.4);position:relative;z-index:100000}' +
            'body.retune-open>*:not(.retune-overlay){position:relative;z-index:0 !important}' +
            'body.retune-open select,body.retune-open input,body.retune-open button:not(.retune-btn):not(.retune-close):not(.retune-tab){position:relative;z-index:0 !important}' +
            '.retune-header{padding:16px 20px;border-bottom:1px solid var(--border-light);display:flex;align-items:center;justify-content:space-between}' +
            '.retune-title{font-size:16px;font-weight:700;color:var(--text-primary);display:flex;align-items:center;gap:10px}.retune-title i{color:var(--accent)}' +
            '.retune-close{background:none;border:none;color:var(--text-muted);cursor:pointer;font-size:18px;padding:4px 8px}.retune-close:hover{color:var(--text-primary)}' +
            '.retune-tabs{display:flex;border-bottom:1px solid var(--border-light)}' +
            '.retune-tab{flex:1;padding:10px;text-align:center;font-size:12px;font-weight:600;cursor:pointer;color:var(--text-muted);border-bottom:2px solid transparent;transition:all .15s}' +
            '.retune-tab:hover{color:var(--text-primary)}.retune-tab.active{color:var(--accent);border-bottom-color:var(--accent)}' +
            '.retune-tab-body{display:none;flex:1;overflow-y:auto}.retune-tab-body.active{display:block}' +
            '.retune-controls{padding:14px 20px;border-bottom:1px solid var(--border-light);display:flex;flex-wrap:wrap;gap:16px;align-items:center;background:var(--surface-alt)}' +
            '.retune-control-group{display:flex;align-items:center;gap:8px}' +
            '.retune-control-label{font-size:12px;font-weight:600;color:var(--text-secondary);white-space:nowrap}' +
            '.retune-slider{width:120px;accent-color:var(--accent)}.retune-value{font-size:12px;font-family:monospace;color:var(--accent);min-width:40px}' +
            '.retune-slots{padding:12px 20px}' +
            '.retune-phase-label{font-size:12px;font-weight:700;color:var(--accent);text-transform:uppercase;letter-spacing:.5px;margin:12px 0 6px;padding-top:8px;border-top:1px solid var(--border-light)}.retune-phase-label:first-child{border-top:none;margin-top:0}' +
            '.retune-slot{display:flex;align-items:center;gap:10px;padding:8px 0;border-bottom:1px solid rgba(255,255,255,.04)}' +
            '.retune-slot-idx{font-family:monospace;font-size:11px;color:var(--text-muted);background:var(--surface-alt);padding:2px 6px;border-radius:3px}' +
            '.retune-slot-role{font-size:10px;padding:2px 8px;border-radius:10px;font-weight:600}' +
            '.retune-slot-file{font-size:11px;color:var(--text-secondary);font-family:monospace;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap}' +
            '.retune-slot-imgs{display:flex;gap:6px;align-items:center}' +
            '.retune-slot-img{width:48px;height:48px;border-radius:4px;image-rendering:pixelated;border:1px solid var(--border-light);background:#000}' +
            '.retune-slot-arrow{color:var(--text-muted);font-size:10px}.retune-no-img{width:48px;height:48px;border-radius:4px;border:1px dashed var(--border-light);display:flex;align-items:center;justify-content:center;font-size:9px;color:var(--text-muted)}' +
            '.retune-emitters{padding:12px 20px}' +
            '.retune-emitter-card{background:var(--surface-alt);border:1px solid var(--border-light);border-radius:var(--radius-sm);padding:12px;margin-bottom:10px}' +
            '.retune-emitter-header{display:flex;align-items:center;gap:10px;margin-bottom:8px;flex-wrap:wrap}' +
            '.retune-emitter-idx{font-family:monospace;font-size:13px;font-weight:700;color:var(--accent)}' +
            '.retune-emitter-badge{font-size:10px;padding:2px 8px;border-radius:10px;background:rgba(255,255,255,.06);color:var(--text-muted)}' +
            '.retune-prop-grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(180px,1fr));gap:6px}' +
            '.retune-prop{display:flex;align-items:center;justify-content:space-between;font-size:11px;padding:3px 8px;background:rgba(0,0,0,.2);border-radius:3px}' +
            '.retune-prop-name{color:var(--text-muted)}.retune-prop-val{font-family:monospace;color:var(--text-primary)}' +
            '.retune-json-wrap{padding:16px 20px;display:flex;flex-direction:column;gap:12px}' +
            '.retune-json-hint{font-size:12px;color:var(--text-muted);line-height:1.5}' +
            '.retune-json-textarea{min-height:300px;background:var(--bg-input);border:1px solid var(--border-light);border-radius:var(--radius-sm);color:var(--text-primary);font-family:monospace;font-size:11px;padding:12px;resize:vertical;tab-size:2}.retune-json-textarea:focus{outline:none;border-color:var(--accent)}' +
            '.retune-footer{padding:14px 20px;border-top:1px solid var(--border-light);display:flex;align-items:center;justify-content:space-between;gap:12px}' +
            '.retune-status{font-size:12px;color:var(--text-muted);flex:1}' +
            '.retune-btn{padding:8px 20px;border-radius:var(--radius-sm);font-size:13px;font-weight:600;cursor:pointer;border:none;transition:all .15s}' +
            '.retune-btn-primary{background:var(--accent);color:#fff}.retune-btn-primary:hover{filter:brightness(1.1)}.retune-btn-primary:disabled{opacity:.5;cursor:not-allowed}' +
            '.retune-btn-secondary{background:var(--surface-alt);color:var(--text-secondary);border:1px solid var(--border-light)}.retune-btn-secondary:hover{color:var(--text-primary)}' +
            '.retune-btn-warning{background:#e67e22;color:#fff}.retune-btn-warning:hover{filter:brightness(1.1)}.retune-btn-warning:disabled{opacity:.5;cursor:not-allowed}' +
            // Custom spells multi-rank grouping
            '.custom-spell-group{border:1px solid var(--border-light);border-radius:var(--radius-sm);margin-bottom:4px;overflow:hidden}' +
            '.custom-spell-multi{background:var(--surface-alt)}' +
            '.custom-spell-expand{background:none;border:none;color:var(--text-muted);cursor:pointer;padding:2px 6px;font-size:10px;transition:color .15s;flex-shrink:0}' +
            '.custom-spell-expand:hover{color:var(--accent)}' +
            '.custom-spell-rank-badge{font-size:10px;padding:2px 8px;border-radius:10px;background:var(--accent);color:#fff;font-weight:600;white-space:nowrap}' +
            '.custom-spell-ranks{border-top:1px solid var(--border-light);background:rgba(0,0,0,.03)}' +
            '.custom-spell-rank-row{display:flex;align-items:center;gap:10px;padding:6px 14px 6px 36px;border-bottom:1px solid var(--border-light);font-size:11px}' +
            '.custom-spell-rank-row:last-child{border-bottom:none}' +
            '.custom-spell-rank-num{font-weight:700;color:var(--accent);min-width:24px}' +
            '.custom-spell-rank-id{font-family:monospace;color:var(--text-muted);min-width:50px}' +
            '.custom-spell-rank-sub{color:var(--text-secondary);flex:1}' +
            '.custom-spell-rank-detail{color:var(--text-muted);font-family:monospace;font-size:10px}' +
            // Description token toolbar + preview
            '.desc-toolbar{display:flex;align-items:center;gap:4px;flex-wrap:wrap;margin-bottom:4px}' +
            '.desc-toolbar-label{font-size:10px;font-weight:600;color:var(--text-muted);text-transform:uppercase;letter-spacing:.3px;margin-right:2px}' +
            '.desc-token-btn{padding:3px 8px;font-size:10px;font-weight:600;border:1px solid var(--border-light);border-radius:4px;background:var(--surface-alt);color:var(--text-secondary);cursor:pointer;transition:all .12s;white-space:nowrap}' +
            '.desc-token-btn:hover{border-color:var(--accent);color:var(--accent);background:rgba(85,153,255,.06)}' +
            '.desc-token-btn i{margin-right:3px;font-size:9px}' +
            '.desc-preview{margin-top:6px;padding:8px 10px;border:1px dashed var(--border-light);border-radius:var(--radius-sm);background:rgba(85,153,255,.04);font-size:12px;line-height:1.5}' +
            '.desc-preview-label{font-size:10px;font-weight:600;color:var(--accent);margin-right:6px}' +
            '.desc-preview-text{color:var(--text-primary)}' +
            '</style>');
    }

    // ===================== TAB SYSTEM =====================

    function initTabSystem() {
        if ($('#quickCreatePanel').length) return;
        var $form = $('.sc-panel').first();
        var $title = $form.find('.sc-panel-title').first();

        // Mode selector — inject BEFORE the create panel so it's always visible
        var modeHtml = '<div id="gwModeSelector" class="gw-mode-selector">' +
            '<button class="gw-mode-btn gw-mode-guided active" id="btnGuidedMode">' +
            '<i class="fa-solid fa-wand-magic-sparkles"></i>' +
            '<span class="gw-mode-title">Guided Creation</span>' +
            '<span class="gw-mode-desc">Step-by-step spell builder</span>' +
            '</button>' +
            '<button class="gw-mode-btn gw-mode-workshop" id="btnWorkshopMode">' +
            '<i class="fa-solid fa-toolbox"></i>' +
            '<span class="gw-mode-title">Workshop</span>' +
            '<span class="gw-mode-desc">Full control &amp; visual tuning</span>' +
            '</button>' +
            '<button class="gw-mode-btn gw-mode-lab" id="btnLabMode">' +
            '<i class="fa-solid fa-flask"></i>' +
            '<span class="gw-mode-title">Experiment Lab</span>' +
            '<span class="gw-mode-desc">Parameter sweeps &amp; tuning</span>' +
            '</button>' +
            '</div>';
        $form.before(modeHtml);

        // Hide the panel title (mode selector replaces it)
        $title.hide();

        // Wizard container — visible by default since Guided is active, inside the create panel
        $title.after('<div id="gwContainer" class="gw-container"></div>');

        // Tabs — hidden by default since Guided is active
        var tabHtml = '<div class="sc-tabs" style="display:none">' +
            '<button class="sc-tab active" data-tab="quick"><i class="fa-solid fa-bolt"></i> Quick Create</button>' +
            '<button class="sc-tab" data-tab="studio"><i class="fa-solid fa-wand-magic-sparkles"></i> Visual Studio</button>' +
            '</div>';
        $('#gwContainer').after(tabHtml);

        // Wrap workshop content — hidden by default
        $form.find('.sc-tabs').nextAll().wrapAll('<div id="quickCreatePanel" style="display:none"></div>');
        $form.append('<div id="studioPanel" style="display:none;">' + buildStudioPanel() + '</div>');
    }

    // Experiment Lab mode toggle
    $(document).on('click', '#btnLabMode', function () {
        $('#btnLabMode').addClass('active');
        $('#btnGuidedMode, #btnWorkshopMode').removeClass('active');
        // Hide create panel + wizard, show experiment panel
        $('.sc-panel').first().hide();
        $('#gwContainer').hide();
        $('.sc-tabs, #quickCreatePanel, #studioPanel').hide();
        $('#experimentPanel').show();
    });

    // When switching back to Guided or Workshop, hide experiment lab and restore create panel
    $(document).on('click', '#btnGuidedMode', function () {
        $('#experimentPanel').hide();
        $('.sc-panel').first().show();
        $('#btnLabMode').removeClass('active');
    });

    $(document).on('click', '#btnWorkshopMode', function () {
        $('#experimentPanel').hide();
        $('.sc-panel').first().show();
        $('#btnLabMode').removeClass('active');
    });

    // Custom Spells modal
    $(document).on('click', '#btnOpenSpellsModal', function () {
        $('#spellsModalOverlay').show();
    });
    $(document).on('click', '#btnCloseSpellsModal', function () {
        $('#spellsModalOverlay').hide();
    });
    $(document).on('click', '#spellsModalOverlay', function (e) {
        if (e.target === this) $(this).hide();
    });

    // Install guide toggle
    $(document).on('click', '#btnTopbarInstall', function () {
        $('#installBody').slideToggle(200);
    });

    $(document).on('click', '.sc-tab', function () {
        var tab = $(this).data('tab');
        if (tab === currentTab) return;
        currentTab = tab;
        $('.sc-tab').removeClass('active');
        $(this).addClass('active');
        $('#quickCreatePanel').toggle(tab === 'quick');
        $('#studioPanel').toggle(tab === 'studio');
        if (tab === 'studio' && selectedSource) refreshStudioPhases();
    });

    // ===================== VISUAL STUDIO PANEL =====================

    function buildStudioPanel() {
        var h = '';

        // Source spell
        h += '<label class="sc-label" style="margin-top:0">Source Spell</label>';
        h += '<div class="source-search-wrap"><input type="text" class="form-input studio-source-search" placeholder="Search by name or entry ID..." autocomplete="off" />';
        h += '<div class="studio-source-results source-results"></div></div>';
        h += '<div class="studio-source-selected" style="display:none;margin-top:6px"></div>';

        // Name + School + Teach
        h += '<label class="sc-label">Spell Name</label>';
        h += '<input type="text" class="form-input" id="studioSpellName" placeholder="e.g. Thunderlance" />';
        h += '<div style="display:flex;gap:12px"><div style="flex:1"><label class="sc-label">School</label>';
        h += '<select id="studioSchool" class="form-input"><option value="0">Physical</option><option value="1">Holy</option><option value="2" selected>Fire</option><option value="3">Nature</option><option value="4">Frost</option><option value="5">Shadow</option><option value="6">Arcane</option></select></div>';
        h += '<div style="flex:1"><label class="sc-label">Teach To</label>';
        h += '<select id="studioTeachChar" class="form-input"><option value="0">None</option></select></div></div>';

        // TIER 1: Theme
        h += '<div class="studio-section"><div class="studio-section-title"><i class="fa-solid fa-palette"></i> Visual Theme</div>';
        h += '<div class="studio-hint">Pick a theme and the AI generates completely new textures for every phase — replacing the vanilla BLP sprites with theme-appropriate ones (lightning sparks, void tendrils, ice shards, etc.).</div>';
        h += '<div class="theme-grid" id="themeGrid"></div></div>';

        // Global Blend Mode
        h += '<div class="studio-section"><div class="studio-section-title"><i class="fa-solid fa-sun"></i> Global Blend Mode</div>';
        h += '<select id="globalBlendMode" class="blend-select" style="width:100%">';
        BLEND_MODES.forEach(function (b) { h += '<option value="' + b.value + '">' + b.label + '</option>'; });
        h += '</select></div>';

        // Color
        h += '<div class="studio-section"><div class="studio-section-title"><i class="fa-solid fa-droplet"></i> Particle Color</div>';
        h += '<div class="preset-grid" id="studioPresetGrid">';
        ['shadow', 'frost', 'holy', 'nature', 'arcane', 'fire', 'none'].forEach(function (p) {
            h += '<button class="preset-btn' + (p === 'none' ? ' active' : '') + '" data-preset="' + p + '">' + p.charAt(0).toUpperCase() + p.slice(1) + '</button>';
        });
        h += '</div>';
        h += '<span id="studioCustomColorWrap" class="custom-color-wrap" title="Custom color"><input type="color" id="studioCustomColorInput" class="custom-color-input" value="#5599ff" /><span class="custom-color-hex" id="studioCustomColorHex"></span></span></div>';

        // Icon
        h += '<div class="studio-section"><div class="studio-section-title"><i class="fa-solid fa-image"></i> Icon</div>';
        h += '<label style="font-size:12px;display:flex;align-items:center;gap:6px"><input type="checkbox" id="studioChkGenerateIcon" /> AI-generate icon (FLUX Q5)</label>';
        h += '<div id="studioIconPickerWrap"></div></div>';

        // TIER 2: Per-Phase
        h += '<div class="studio-section"><div class="studio-section-title"><i class="fa-solid fa-sliders"></i> Per-Phase Controls</div>';
        h += '<div class="studio-hint">Expand any phase to override the global theme, blend mode, colors, or particle parameters.</div>';
        h += '<div class="designer-presets" style="margin-bottom:12px"><span class="designer-presets-label">Quick Recipes:</span>';
        ['subtle', 'powerful', 'cosmic', 'nova', 'reset'].forEach(function (r) { h += '<button class="recipe-btn studio-recipe-btn" data-recipe="' + r + '">' + r.charAt(0).toUpperCase() + r.slice(1) + '</button>'; });
        h += '</div>';
        h += '<div id="studioPhaseCards"></div></div>';

        // Generate
        h += '<button id="btnStudioGenerate" class="btn-accent" style="margin-top:16px"><i class="fa-solid fa-wand-magic-sparkles"></i> Create Spell & Generate Patch</button>';
        h += '<div id="studioGenerateResult"></div>';

        return h;
    }

    // ── Theme Picker ──

    function loadTextureThemes() {
        $.getJSON('/Patch/TextureThemes', function (data) { textureThemes = data.themes || []; renderThemeGrid(); });
    }

    var THEME_SWATCHES = { lightning: '#4488ff', void: '#6622aa', crystal_ice: '#88ddff', holy_radiance: '#ffdd44', nature_vines: '#22cc44', arcane_rune: '#cc44ff', fel_chaos: '#44ff22' };

    function renderThemeGrid() {
        var $g = $('#themeGrid');
        if (!$g.length) return;
        var h = '<div class="theme-card' + (!selectedTheme ? ' selected' : '') + '" data-theme="">';
        h += '<div class="theme-swatch" style="background:var(--surface-alt);border-color:var(--border-light)"><i class="fa-solid fa-xmark" style="line-height:28px;color:var(--text-muted);font-size:14px;display:block;text-align:center"></i></div>';
        h += '<div class="theme-name">No Theme</div><div class="theme-desc">Keep vanilla textures</div></div>';
        textureThemes.forEach(function (t) {
            h += '<div class="theme-card' + (selectedTheme === t.key ? ' selected' : '') + '" data-theme="' + esc(t.key) + '">';
            h += '<div class="theme-swatch" style="background:' + (THEME_SWATCHES[t.key] || '#888') + '"></div>';
            h += '<div class="theme-name">' + esc(t.name) + '</div><div class="theme-desc">' + esc(t.color) + '</div></div>';
        });
        $g.html(h);
    }

    $(document).on('click', '.theme-card', function () { selectedTheme = $(this).data('theme') || null; renderThemeGrid(); });
    $(document).on('change', '#globalBlendMode', function () { globalBlendMode = $(this).val() || null; });

    // ── Studio Source Search ──

    $(document).on('input', '.studio-source-search', function () {
        var q = $(this).val().trim(), $r = $(this).siblings('.studio-source-results');
        clearTimeout(searchTimer);
        if (q.length < 2) { $r.removeClass('open').empty(); return; }
        searchTimer = setTimeout(function () {
            $.getJSON('/Patch/SearchSource', { q: q }, function (data) {
                if (!data.results || !data.results.length) { $r.html('<div class="search-empty">No spells found</div>').addClass('open'); return; }
                var html = '';
                data.results.forEach(function (s) {
                    html += '<div class="source-result studio-source-result" data-json=\'' + JSON.stringify(s).replace(/'/g, '&#39;') + '\'>';
                    html += '<div><span class="source-result-name">' + esc(s.name) + (s.nameSubtext ? ' (' + esc(s.nameSubtext) + ')' : '') + '</span> <span class="source-result-sub">' + (SCHOOL_NAMES[s.school] || '?') + '</span></div>';
                    html += '<span class="source-result-id">#' + s.entry + '</span></div>';
                });
                $r.html(html).addClass('open');
            });
        }, 250);
    });

    $(document).on('click', '.studio-source-result', function () {
        selectedSource = JSON.parse($(this).attr('data-json'));
        showStudioSource();
        showSelectedSource();
    });

    function showStudioSource() {
        if (!selectedSource) return;
        var c = SCHOOL_COLORS[selectedSource.school] || '#aaa';
        $('.studio-source-selected').html('<div class="source-selected"><span class="source-selected-name">' + esc(selectedSource.name) + '</span><span class="source-selected-id">#' + selectedSource.entry + ' <span style="color:' + c + '">' + (SCHOOL_NAMES[selectedSource.school] || '?') + '</span></span><span class="source-clear studio-clear-source"><i class="fa-solid fa-xmark"></i></span></div>').show();
        $('.studio-source-search').hide();
        $('.studio-source-results').removeClass('open').empty();
        $('#studioSchool').val(selectedSource.school);
        refreshStudioPhases();
    }

    $(document).on('click', '.studio-clear-source', function () {
        selectedSource = null; activePhases = null; m2TextureData = {};
        $('.studio-source-selected').hide().empty();
        $('.studio-source-search').val('').show().focus();
        $('#studioPhaseCards').empty();
        $('#sourceSelected').hide().empty();
        $('#sourceSearch').val('').show();
        updatePhaseVisibility();
    });

    function refreshStudioPhases() {
        if (!selectedSource) return;
        $.getJSON('/Patch/SourcePhases', { entry: selectedSource.entry }, function (data) {
            activePhases = data.phases || [];
            updatePhaseVisibility();
            buildStudioPhaseCards();
        });
        $.getJSON('/Patch/M2Textures', { entry: selectedSource.entry }, function (data) {
            m2TextureData = {};
            if (data.phases) data.phases.forEach(function (p) { m2TextureData[p.phase] = p; });
            updateTextureSlotCards();
        });
    }

    // ── Studio Phase Cards (Tier 2) ──

    function buildStudioPhaseCards() {
        var h = '';
        PHASES.forEach(function (phase) {
            if (!activePhases || activePhases.indexOf(phase.key) < 0) return;
            h += '<div class="phase-card" data-phase="' + phase.key + '"><div class="phase-header">';
            h += '<div class="phase-title"><i class="fa-solid ' + phase.icon + ' phase-icon"></i><span>' + phase.label + '</span></div>';
            h += '<span class="phase-desc">' + phase.desc + '</span>';
            h += '<button class="phase-collapse-btn"><i class="fa-solid fa-chevron-down"></i></button></div>';
            h += '<div class="phase-body" style="display:none"><div class="studio-phase-extras">';

            // Theme override
            h += '<div class="studio-extra-row"><span class="studio-extra-label"><i class="fa-solid fa-palette"></i> Theme</span>';
            h += '<select class="blend-select studio-phase-theme" data-phase="' + phase.key + '" style="flex:1"><option value="">Use global</option>';
            textureThemes.forEach(function (t) { h += '<option value="' + esc(t.key) + '">' + esc(t.name) + '</option>'; });
            h += '</select></div>';

            // Blend mode
            h += '<div class="studio-extra-row"><span class="studio-extra-label"><i class="fa-solid fa-sun"></i> Blend Mode</span>';
            h += '<select class="blend-select studio-phase-blend" data-phase="' + phase.key + '" style="flex:1"><option value="">Use global</option>';
            BLEND_MODES.forEach(function (b) { if (b.value) h += '<option value="' + b.value + '">' + b.label + '</option>'; });
            h += '</select></div>';

            // Emitter type
            h += '<div class="studio-extra-row"><span class="studio-extra-label"><i class="fa-solid fa-shapes"></i> Emitter Type</span>';
            h += '<select class="blend-select studio-phase-emitter" data-phase="' + phase.key + '" style="flex:1">';
            EMITTER_TYPES.forEach(function (e) { h += '<option value="' + e.value + '">' + e.label + '</option>'; });
            h += '</select></div>';

            // Color override
            var cid = 'studioPhaseColor-' + phase.key;
            h += '<div class="phase-color-row"><span class="phase-color-label"><i class="fa-solid fa-droplet knob-icon"></i> Color</span>';
            h += '<input type="color" id="' + cid + '" class="phase-color-input studio-phase-color" value="#5599ff" data-phase="' + phase.key + '" />';
            h += '<span class="phase-color-hex" id="' + cid + '-hex"></span>';
            h += '<span class="phase-color-clear" id="' + cid + '-clear" style="display:none">clear</span>';
            h += '<span class="phase-color-badge" id="' + cid + '-badge">using global</span></div></div>';

            // Knobs
            KNOBS.forEach(function (k) {
                var id = 'studio-knob-' + phase.key + '-' + k.key;
                h += '<div class="knob-row"><div class="knob-label-wrap"><i class="fa-solid ' + k.icon + ' knob-icon"></i><label class="knob-label" for="' + id + '">' + k.label + '</label></div>';
                h += '<div class="knob-control"><input type="range" id="' + id + '" class="knob-slider studio-knob-slider" min="' + k.min + '" max="' + k.max + '" step="' + k.step + '" value="' + k.def + '" data-phase="' + phase.key + '" data-knob="' + k.key + '">';
                h += '<span class="knob-value">' + k.def.toFixed(1) + k.unit + '</span></div></div>';
            });

            // Texture slots placeholder (Tier 3)
            h += '<div class="texture-slots-wrap" id="textureSlots-' + phase.key + '" style="display:none">';
            h += '<div class="texture-slots-header" data-phase="' + phase.key + '"><span><i class="fa-solid fa-image"></i> Customize Textures</span><i class="fa-solid fa-chevron-down"></i></div>';
            h += '<div class="texture-slots-body" id="textureSlotsBody-' + phase.key + '"></div></div>';

            h += '</div></div>';
        });
        $('#studioPhaseCards').html(h);
        updateTextureSlotCards();
    }

    $(document).on('input', '.studio-knob-slider', function () {
        var p = $(this).data('phase'), k = $(this).data('knob'), v = parseFloat($(this).val());
        phaseParams[p][k] = v;
        var u = '\u00d7'; KNOBS.forEach(function (kb) { if (kb.key === k) u = kb.unit; });
        $(this).siblings('.knob-value').text(v.toFixed(1) + u);
    });

    $(document).on('change', '.studio-phase-theme', function () { phaseParams[$(this).data('phase')].textureTheme = $(this).val() || null; });
    $(document).on('change', '.studio-phase-blend', function () { var v = $(this).val(); phaseParams[$(this).data('phase')].blendMode = v ? parseInt(v) : null; });
    $(document).on('change', '.studio-phase-emitter', function () { var v = $(this).val(); phaseParams[$(this).data('phase')].emitterType = v ? parseInt(v) : null; });

    $(document).on('input', '.studio-phase-color', function () {
        var p = $(this).data('phase'), hex = $(this).val();
        if (/^#[0-9a-fA-F]{6}$/.test(hex)) {
            phaseParams[p].color = hex; $(this).addClass('active');
            $('#studioPhaseColor-' + p + '-hex').text(hex);
            $('#studioPhaseColor-' + p + '-clear').show();
            $('#studioPhaseColor-' + p + '-badge').text('override').css('color', hex);
        }
    });
    $(document).on('click', '[id^="studioPhaseColor-"][id$="-clear"]', function () {
        var p = $(this).attr('id').replace('studioPhaseColor-', '').replace('-clear', '');
        phaseParams[p].color = null;
        $('#studioPhaseColor-' + p).removeClass('active');
        $('#studioPhaseColor-' + p + '-hex').text(''); $(this).hide();
        $('#studioPhaseColor-' + p + '-badge').text('using global').css('color', '');
    });

    $(document).on('click', '#studioPresetGrid .preset-btn', function () {
        $('#studioPresetGrid .preset-btn').removeClass('active'); $(this).addClass('active');
        studioPreset = $(this).data('preset'); studioCustomColor = null; $('#studioCustomColorWrap').removeClass('active');
    });
    $(document).on('input', '#studioCustomColorInput', function () {
        var hex = $(this).val();
        if (/^#[0-9a-fA-F]{6}$/.test(hex)) { studioCustomColor = hex; studioPreset = 'custom'; $('#studioPresetGrid .preset-btn').removeClass('active'); $('#studioCustomColorWrap').addClass('active'); $('#studioCustomColorHex').text(hex); }
    });

    // ── Texture Slot Cards (Tier 3) ──

    function updateTextureSlotCards() {
        PHASES.forEach(function (phase) {
            var pd = m2TextureData[phase.key], $w = $('#textureSlots-' + phase.key);
            if (!pd || !pd.m2Files || !pd.m2Files.length) { $w.hide(); return; }
            $w.show();
            var h = '';
            pd.m2Files.forEach(function (m2) {
                m2.textures.forEach(function (tex) {
                    var pk = phase.key + '-' + tex.index;
                    h += '<div class="texture-slot-card" data-phase="' + phase.key + '" data-tex-index="' + tex.index + '">';
                    h += '<div class="texture-slot-top"><span class="texture-slot-index">[' + tex.index + ']</span>';
                    h += '<span class="texture-slot-filename" title="' + esc(tex.filename) + '">' + esc(tex.filename) + '</span>';
                    h += '<span class="texture-slot-role role-' + tex.role + '">' + tex.role + '</span>';
                    if (tex.emitters && tex.emitters.length) h += '<span style="font-size:10px;color:var(--text-muted)">emit: ' + tex.emitters.join(',') + '</span>';
                    h += '</div>';
                    h += '<textarea class="texture-slot-prompt" data-phase="' + phase.key + '" data-tex-index="' + tex.index + '" placeholder="Auto-generated from theme. Type here to override the AI prompt..."></textarea>';
                    h += '<div class="texture-slot-actions"><button class="texture-gen-btn btn-gen-single-texture" data-phase="' + phase.key + '" data-tex-index="' + tex.index + '" data-filename="' + esc(tex.filename) + '" data-byte-length="' + tex.byteLength + '" data-role="' + tex.role + '"><i class="fa-solid fa-image"></i> Generate Preview</button>';
                    if (generatedTexturePreviews[pk]) h += '<img class="texture-preview-img" src="' + esc(generatedTexturePreviews[pk]) + '" />';
                    h += '</div></div>';
                });
            });
            $('#textureSlotsBody-' + phase.key).html(h);
        });
    }

    $(document).on('click', '.texture-slots-header', function () {
        $(this).siblings('.texture-slots-body').toggleClass('open');
        $(this).find('i:last').toggleClass('fa-chevron-down fa-chevron-up');
    });

    $(document).on('click', '.btn-gen-single-texture', function () {
        var $b = $(this), phase = $b.data('phase'), idx = $b.data('tex-index'), fn = $b.data('filename'), bl = $b.data('byte-length'), role = $b.data('role');
        var prompt = $b.closest('.texture-slot-card').find('.texture-slot-prompt').val().trim();
        var name = $('#studioSpellName').val().trim() || 'CustomSpell';
        var theme = phaseParams[phase].textureTheme || selectedTheme || null;
        $b.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Generating...');
        $.ajax({
            url: '/Patch/GenerateTextures', method: 'POST', contentType: 'application/json',
            data: JSON.stringify({
                spellName: name, themeKey: theme, sourceSpellEntry: selectedSource ? selectedSource.entry : 0,
                textureSlots: [{ index: idx, originalFilename: fn, originalFilenameLength: bl, originalWidth: 0, originalHeight: 0, roleOverride: role, customPrompt: prompt || null, useOllamaRefinement: false }]
            }),
            success: function (r) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-image"></i> Generate Preview');
                if (r.success && r.textures && r.textures.length) {
                    var wp = r.textures[0].pngPath || '';
                    if (wp.indexOf('/wwwroot/') > -1) wp = wp.substring(wp.indexOf('/wwwroot/') + '/wwwroot'.length);
                    else if (wp.indexOf('wwwroot') > -1) wp = '/' + wp.substring(wp.indexOf('wwwroot') + 'wwwroot/'.length);
                    generatedTexturePreviews[phase + '-' + idx] = wp;
                    updateTextureSlotCards();
                } else { alert('Generation failed: ' + ((r.errors || []).join(', ') || r.error || 'Unknown')); }
            },
            error: function () { $b.prop('disabled', false).html('<i class="fa-solid fa-image"></i> Generate Preview'); alert('Request failed.'); }
        });
    });

    // ── Studio Generate ──

    $(document).on('click', '#btnStudioGenerate', function () {
        if (!selectedSource) { showStudioResult('error', 'Select a source spell.'); return; }
        var name = $('#studioSpellName').val().trim();
        if (!name) { showStudioResult('error', 'Enter a spell name.'); return; }
        var $b = $(this);
        $b.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Generating...');

        var pBM = {}, pTO = {}, sPP = {};
        PHASES.forEach(function (p) {
            if (!activePhases || activePhases.indexOf(p.key) < 0) return;
            var bm = phaseParams[p.key].blendMode;
            if (bm === null && globalBlendMode) bm = parseInt(globalBlendMode);
            if (bm !== null) pBM[p.key] = bm;
            sPP[p.key] = { emissionRate: phaseParams[p.key].emissionRate, scale: phaseParams[p.key].scale, speed: phaseParams[p.key].speed, lifespan: phaseParams[p.key].lifespan, area: phaseParams[p.key].area, color: phaseParams[p.key].color, blendMode: bm, textureTheme: phaseParams[p.key].textureTheme || selectedTheme || null };
            var cp = [];
            $('#textureSlotsBody-' + p.key + ' .texture-slot-card').each(function () {
                var pr = $(this).find('.texture-slot-prompt').val().trim();
                if (pr) cp.push({ index: parseInt($(this).data('tex-index')), customPrompt: pr });
            });
            if (cp.length) pTO[p.key] = cp;
        });

        var payload = {
            spellName: name, nameSubtext: 'Rank 1', description: 'A custom spell crafted in Visual Studio.',
            sourceSpellEntry: selectedSource.entry, sourceSpellName: selectedSource.name,
            school: parseInt($('#studioSchool').val()),
            colorPreset: (studioPreset === 'none' ? null : studioPreset),
            customColor: (studioPreset === 'custom' ? studioCustomColor : null),
            intensity: 1.0, generateIcon: $('#studioChkGenerateIcon').is(':checked'),
            teachToCharacterGuid: parseInt($('#studioTeachChar').val()) || 0,
            usePerPhaseParams: true,
            phaseParams: { precast: sPP.precast || null, cast: sPP.cast || null, missile: sPP.missile || null, impact: sPP.impact || null, state: sPP.state || null, stateDone: sPP.stateDone || null, channel: sPP.channel || null },
            existingIconPath: studioSelectedIconPath || null,
            textureTheme: selectedTheme || null,
            phaseBlendModes: Object.keys(pBM).length ? pBM : null,
            phaseTextureOverrides: Object.keys(pTO).length ? pTO : null
        };

        $.ajax({
            url: '/Patch/Generate', method: 'POST', contentType: 'application/json', data: JSON.stringify(payload),
            success: function (r) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-wand-magic-sparkles"></i> Create Spell & Generate Patch');
                if (r.success) {
                    var m = '<div class="result-title"><i class="fa-solid fa-check"></i> <strong>' + esc(name) + '</strong> (#' + r.spellEntry + ') created</div>';
                    if (r.hasPatch) { m += '<div class="result-detail">Unified patch: <code>' + esc(r.patchFileName) + '</code>'; if (r.totalSpellsInPatch) m += ' (' + r.totalSpellsInPatch + ' spell' + (r.totalSpellsInPatch > 1 ? 's' : '') + ')'; m += '</div>'; m += '<div class="result-detail">' + r.m2Count + ' M2(s), ' + r.totalFiles + ' files</div>'; }
                    if (r.taught) m += '<div class="result-detail"><i class="fa-solid fa-graduation-cap"></i> Taught to character</div>';
                    if (selectedTheme) m += '<div class="result-detail"><i class="fa-solid fa-palette"></i> Theme: ' + esc(selectedTheme) + '</div>';
                    m += '<div class="result-detail"><em>Server restart required. Delete WDB cache.</em></div>';
                    showStudioResult('success', m); loadPatches(); loadCustomSpells();
                } else { showStudioResult('error', r.error || 'Unknown error.'); }
            },
            error: function (xhr) { $b.prop('disabled', false).html('<i class="fa-solid fa-wand-magic-sparkles"></i> Create Spell & Generate Patch'); showStudioResult('error', 'Failed: ' + (xhr.responseText || xhr.statusText)); }
        });
    });

    function showStudioResult(type, html) { $('#studioGenerateResult').html('<div class="patch-result ' + type + '">' + html + '</div>'); }

    function syncStudioCharacters() {
        var h = '<option value="0">None</option>';
        characters.forEach(function (c) { h += '<option value="' + c.guid + '">' + esc(c.name) + ' (Lv' + c.level + ' ' + (CLASS_NAMES[c.charClass] || '?') + ')</option>'; });
        $('#studioTeachChar').html(h);
    }

    function renderStudioIconPicker() {
        var $w = $('#studioIconPickerWrap');
        if (!$w.length || !customIcons.length) { if ($w.length) $w.hide(); return; }
        var h = '<div class="icon-picker-label" style="margin-top:6px">Or reuse an existing icon:</div><div class="icon-picker-grid">';
        customIcons.forEach(function (ic) { h += '<div class="icon-picker-item studio-icon-pick' + (studioSelectedIconPath === ic.path ? ' selected' : '') + '" data-path="' + esc(ic.path) + '" data-name="' + esc(ic.name) + '" title="' + esc(ic.name) + '"><img src="' + esc(ic.webPath) + '" /></div>'; });
        h += '</div>';
        if (studioSelectedIconPath) h += '<div class="icon-picker-selected"><i class="fa-solid fa-check"></i> Using: <strong>' + esc(studioSelectedIconName) + '</strong> <span class="icon-picker-clear" id="btnClearStudioIcon"><i class="fa-solid fa-xmark"></i></span></div>';
        $w.html(h).show();
    }

    // ===================== QUICK CREATE =====================
    // (Everything below is the original Quick Create, unchanged)

    $(document).on('click', '.studio-icon-pick', function () {
        studioSelectedIconPath = $(this).data('path');
        studioSelectedIconName = $(this).data('name');
        $('#studioChkGenerateIcon').prop('checked', false);
        renderStudioIconPicker();
    });
    $(document).on('click', '#btnClearStudioIcon', function () {
        studioSelectedIconPath = null; studioSelectedIconName = null; renderStudioIconPicker();
    });
    $(document).on('change', '#studioChkGenerateIcon', function () {
        if ($(this).is(':checked')) { studioSelectedIconPath = null; studioSelectedIconName = null; renderStudioIconPicker(); }
    });

    $('#sourceSearch').on('input', function () {
        var q = $(this).val().trim(); clearTimeout(searchTimer);
        if (q.length < 2) { $('#sourceResults').removeClass('open').empty(); return; }
        searchTimer = setTimeout(function () {
            $.getJSON('/Patch/SearchSource', { q: q }, function (data) {
                if (!data.results || !data.results.length) { $('#sourceResults').html('<div class="search-empty">No spells found</div>').addClass('open'); return; }
                var html = '';
                data.results.forEach(function (s) {
                    html += '<div class="source-result" data-json=\'' + JSON.stringify(s).replace(/'/g, '&#39;') + '\'><div><span class="source-result-name">' + esc(s.name) + (s.nameSubtext ? ' (' + esc(s.nameSubtext) + ')' : '') + '</span> <span class="source-result-sub">' + (SCHOOL_NAMES[s.school] || '?') + '</span></div><span class="source-result-id">#' + s.entry + '</span></div>';
                });
                $('#sourceResults').html(html).addClass('open');
            });
        }, 250);
    });

    $(document).on('click', '.source-result:not(.studio-source-result)', function () {
        selectedSource = JSON.parse($(this).attr('data-json')); showSelectedSource(); showStudioSource();
        populateSpellPropertiesFromSource();
    });

    function showSelectedSource() {
        if (!selectedSource) return;
        var c = SCHOOL_COLORS[selectedSource.school] || '#aaa';
        $('#sourceSelected').html('<div class="source-selected"><span class="source-selected-name">' + esc(selectedSource.name) + (selectedSource.nameSubtext ? ' (' + esc(selectedSource.nameSubtext) + ')' : '') + '</span><span class="source-selected-id">#' + selectedSource.entry + ' <span style="color:' + c + '">' + (SCHOOL_NAMES[selectedSource.school] || '?') + '</span></span><span class="source-clear" id="btnClearSource"><i class="fa-solid fa-xmark"></i></span></div>').show();
        $('#sourceSearch').hide(); $('#sourceResults').removeClass('open').empty();
        // Set school dropdown to match source
        $('#spellSchool').val(selectedSource.school);
        $.getJSON('/Patch/SourcePhases', { entry: selectedSource.entry }, function (data) { activePhases = data.phases || null; updatePhaseVisibility(); });
    }

    function populateSpellPropertiesFromSource() {
        if (!selectedSource) return;
        var s = selectedSource;
        // Damage: min = basePoints + 1, max = basePoints + dieSides
        var bp = s.effectBasePoints1 || 0;
        var ds = s.effectDieSides1 || 0;
        if (bp > 0 || ds > 0) {
            $('#spellDmgMin').val(bp + 1);
            $('#spellDmgMax').val(bp + ds);
        } else {
            $('#spellDmgMin').val('');
            $('#spellDmgMax').val('');
        }
        $('#spellManaCost').val(s.manaCost || '');
        $('#spellSpellLevel').val(s.spellLevel || '');
        $('#spellMaxLevel').val(s.maxLevel || '');

        // Pre-fill description from source (contains template variables like $s1, $o2, $d)
        if (s.description && !$('#spellDesc').val().trim()) {
            $('#spellDesc').val(s.description);
        }
        // Show hint about template variables
        if (!$('#descTemplateHint').length) {
            $('#spellDesc').after('<div id="descTemplateHint" style="font-size:10px;color:var(--text-muted);margin-top:2px"><i class="fa-solid fa-info-circle" style="margin-right:4px"></i>Leave blank to inherit source description. Use <code>$s1</code> (damage), <code>$o2</code> (DoT total), <code>$d</code> (duration) — client fills in real values.</div>');
        }
        // Cast time index
        var cti = s.castingTimeIndex || 0;
        if (cti && $('#spellCastTime option[value="' + cti + '"]').length) {
            $('#spellCastTime').val(String(cti));
        } else {
            $('#spellCastTime').val('');
        }
        // Range index
        var ri = s.rangeIndex || 0;
        if (ri && $('#spellRange option[value="' + ri + '"]').length) {
            $('#spellRange').val(String(ri));
        } else {
            $('#spellRange').val('');
        }
        // Speed
        $('#spellMissileSpeed').val(s.speed || '');
        // Coefficient
        var coeff = s.effectBonusCoefficient1 || 0;
        $('#spellCoeff').val(coeff > 0 ? coeff : '');
        // Per-level
        var ppl = s.effectRealPointsPerLevel1 || 0;
        $('#spellDmgPerLevel').val(ppl > 0 ? ppl : '');
        // Cooldown
        var cd = s.recoveryTime || 0;
        $('#spellCooldown').val(cd > 0 ? (cd / 1000) : '');
        // Auto-expand the panel so user sees the pre-filled values
        if (!$('#spellPropsBody').is(':visible')) {
            $('#spellPropsBody').slideDown(200);
            $('#spellPropsChevron').removeClass('fa-chevron-down').addClass('fa-chevron-up');
        }
        // Load rank data if checkbox is checked
        if ($('#chkGenerateAllRanks').is(':checked')) {
            loadRankPreview();
        } else {
            // Pre-load rank data in background for quick rendering
            $.getJSON('/Patch/SourceRanks', { entry: selectedSource.entry }, function (data) {
                sourceRankData = data.ranks || [];
            });
        }
    }

    $(document).on('click', '#btnClearSource', function () {
        selectedSource = null; activePhases = null; updatePhaseVisibility();
        $('#sourceSelected').hide().empty(); $('#sourceSearch').val('').show().focus();
        $('.studio-source-selected').hide().empty(); $('.studio-source-search').val('').show();
        $('#studioPhaseCards').empty(); m2TextureData = {};
        // Clear spell properties
        $('#spellDmgMin,#spellDmgMax,#spellManaCost,#spellSpellLevel,#spellMaxLevel,#spellMissileSpeed,#spellCoeff,#spellDmgPerLevel,#spellCooldown').val('');
        $('#spellCastTime,#spellRange,#spellClass,#spellSkillTab').val('');
    });

    $(document).on('click', function (e) { if (!$(e.target).closest('.source-search-wrap').length) { $('#sourceResults').removeClass('open'); $('.studio-source-results').removeClass('open'); } });

    function injectCustomColorPicker() {
        var $pw = $('.preset-grid').first();
        if ($pw.length && !$('#customColorWrap').length) {
            $pw.append('<span id="customColorWrap" class="custom-color-wrap" title="Custom color"><input type="color" id="customColorInput" class="custom-color-input" value="#b432ff" /><span class="custom-color-hex" id="customColorHex"></span></span>');
        }
    }

    $(document).on('input', '#customColorInput', function () { var h = $(this).val(); if (/^#[0-9a-fA-F]{6}$/.test(h)) { customColor = h; selectedPreset = 'custom'; $('.preset-grid').first().find('.preset-btn').removeClass('active'); $('#customColorWrap').addClass('active'); $('#customColorHex').text(h); } });
    $(document).on('click', '.preset-grid:not(#studioPresetGrid) .preset-btn', function () { $(this).closest('.preset-grid').find('.preset-btn').removeClass('active'); $(this).addClass('active'); selectedPreset = $(this).data('preset'); customColor = null; $('#customColorWrap').removeClass('active'); });

    $('#intensity').on('input', function () { var v = parseFloat($(this).val()); $('#intensityValue').text(v.toFixed(1) + '\u00d7'); if (!advancedMode) applyMasterIntensity(v); });
    function applyMasterIntensity(v) { PHASES.forEach(function (p) { KNOBS.forEach(function (k) { phaseParams[p.key][k.key] = v; var $s = $('#knob-' + p.key + '-' + k.key); if ($s.length) { var c = Math.min(Math.max(v, parseFloat($s.attr('min'))), parseFloat($s.attr('max'))); $s.val(c); $s.siblings('.knob-value').text(c.toFixed(1) + k.unit); } }); }); }

    // ===================== SPELL PROPERTIES PANEL (Session 32) =====================

    function buildSpellPropertiesPanel() {
        // Inject after the School dropdown row but before the designer toggle
        var $target = $('#designerContainer');
        if (!$target.length) return;

        var h = '<div id="spellPropertiesPanel" style="margin-top:12px;border:1px solid var(--border-light);border-radius:var(--radius-md);overflow:hidden">';
        h += '<div id="spellPropsToggle" style="padding:10px 14px;background:var(--surface-alt);cursor:pointer;display:flex;align-items:center;justify-content:space-between;font-size:13px;font-weight:600;color:var(--text-secondary)">';
        h += '<span><i class="fa-solid fa-wand-sparkles" style="margin-right:6px;color:var(--accent)"></i>Spell Properties</span>';
        h += '<i class="fa-solid fa-chevron-down" id="spellPropsChevron" style="font-size:10px;color:var(--text-muted)"></i></div>';
        h += '<div id="spellPropsBody" style="display:none;padding:14px">';

        // Class + Skill Tab row
        h += '<div style="display:flex;gap:12px;margin-bottom:10px">';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Class</label>';
        h += '<select id="spellClass" class="form-input"><option value="">Same as source</option>';
        Object.keys(CLASS_NAMES).forEach(function (k) { h += '<option value="' + k + '">' + CLASS_NAMES[k] + '</option>'; });
        h += '</select></div>';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Spellbook Tab</label>';
        h += '<select id="spellSkillTab" class="form-input"><option value="">Auto (from source)</option></select></div></div>';

        // Damage row
        h += '<div style="display:flex;gap:12px;margin-bottom:10px">';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Damage Min</label>';
        h += '<input type="number" id="spellDmgMin" class="form-input" placeholder="e.g. 14" min="0" /></div>';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Damage Max</label>';
        h += '<input type="number" id="spellDmgMax" class="form-input" placeholder="e.g. 22" min="0" /></div>';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Mana Cost</label>';
        h += '<input type="number" id="spellManaCost" class="form-input" placeholder="e.g. 30" min="0" /></div></div>';

        // Level + Cast Time + Range
        h += '<div style="display:flex;gap:12px;margin-bottom:10px">';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Spell Level</label>';
        h += '<input type="number" id="spellSpellLevel" class="form-input" placeholder="e.g. 1" min="1" max="60" /></div>';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Max Level</label>';
        h += '<input type="number" id="spellMaxLevel" class="form-input" placeholder="e.g. 5" min="0" max="64" /></div>';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Cast Time</label>';
        h += '<select id="spellCastTime" class="form-input">';
        CAST_TIME_OPTIONS.forEach(function (o) { h += '<option value="' + o.value + '">' + o.label + '</option>'; });
        h += '</select></div></div>';

        // Range + Cooldown + Speed
        h += '<div style="display:flex;gap:12px;margin-bottom:10px">';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Range</label>';
        h += '<select id="spellRange" class="form-input">';
        RANGE_OPTIONS.forEach(function (o) { h += '<option value="' + o.value + '">' + o.label + '</option>'; });
        h += '</select></div>';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Cooldown (sec)</label>';
        h += '<input type="number" id="spellCooldown" class="form-input" placeholder="0" min="0" step="0.5" /></div>';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Missile Speed</label>';
        h += '<input type="number" id="spellMissileSpeed" class="form-input" placeholder="e.g. 24" min="0" step="1" /></div></div>';

        // Advanced row (coefficient, per-level)
        h += '<div style="display:flex;gap:12px">';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">SP Coefficient</label>';
        h += '<input type="number" id="spellCoeff" class="form-input" placeholder="e.g. 1.0" min="0" max="10" step="0.01" /></div>';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Dmg / Level</label>';
        h += '<input type="number" id="spellDmgPerLevel" class="form-input" placeholder="e.g. 0.6" min="0" step="0.1" /></div>';
        h += '<div style="flex:1"></div></div>';

        // Trainer section
        h += '<div style="margin-top:14px;padding-top:12px;border-top:1px solid var(--border-light)">';
        h += '<div style="font-size:12px;font-weight:600;color:var(--text-secondary);margin-bottom:8px"><i class="fa-solid fa-chalkboard-user" style="margin-right:6px;color:var(--accent)"></i>Trainer Registration</div>';

        // Trainer mode radio buttons
        h += '<div style="display:flex;flex-direction:column;gap:6px;margin-bottom:10px">';
        h += '<label style="font-size:12px;display:flex;align-items:center;gap:6px;cursor:pointer"><input type="radio" name="trainerMode" value="none" checked /> None (skip trainer)</label>';
        h += '<label style="font-size:12px;display:flex;align-items:center;gap:6px;cursor:pointer"><input type="radio" name="trainerMode" value="copySource" /> Copy from source spell (same trainers that teach the original)</label>';
        h += '<label style="font-size:12px;display:flex;align-items:center;gap:6px;cursor:pointer"><input type="radio" name="trainerMode" value="classTemplate" /> Add to all class trainers (via primary template)</label>';
        h += '</div>';

        // Cost + Level (shared by both modes)
        h += '<div id="trainerDetailsRow" style="display:none">';
        h += '<div style="display:flex;gap:12px;margin-bottom:8px">';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Cost (copper)</label>';
        h += '<input type="number" id="trainerCost" class="form-input" placeholder="e.g. 100" min="0" /></div>';
        h += '<div style="flex:1"><label class="sc-label" style="margin-top:0">Req Level</label>';
        h += '<input type="number" id="trainerReqLevel" class="form-input" placeholder="e.g. 1" min="1" max="60" /></div></div></div>';
        h += '<div style="font-size:11px;color:var(--text-muted);font-style:italic">Cost is in copper (100 = 1 silver, 10000 = 1 gold). Copy from source replicates the exact trainer list of the source spell.</div>';
        h += '</div>';

        // Rank chain section
        h += '<div style="margin-top:14px;padding-top:12px;border-top:1px solid var(--border-light)">';
        h += '<label style="font-size:12px;display:flex;align-items:center;gap:6px;cursor:pointer;font-weight:600;color:var(--text-secondary)">';
        h += '<input type="checkbox" id="chkGenerateAllRanks" /> <i class="fa-solid fa-layer-group" style="color:var(--accent)"></i> Generate All Ranks (mirror source spell progression)</label>';
        h += '<div id="rankPreviewWrap" style="display:none;margin-top:10px">';
        h += '<div id="rankPreviewTable" style="font-size:11px;color:var(--text-muted)">Select a source spell to see rank preview...</div>';
        h += '</div></div>';

        h += '</div></div>';
        $target.before(h);
    }

    // ── Description Token Toolbar + Live Preview ──

    function buildDescToolbar() {
        var $desc = $('#spellDesc');
        if (!$desc.length) return;

        // Token toolbar above textarea
        var tb = '<div class="desc-toolbar" id="descToolbar">';
        tb += '<span class="desc-toolbar-label">Insert:</span>';
        tb += '<button type="button" class="desc-token-btn" data-token="$s1" title="Effect 1 value (direct damage)"><i class="fa-solid fa-crosshairs"></i> $s1 Damage</button>';
        tb += '<button type="button" class="desc-token-btn" data-token="$o1" title="Effect 1 periodic total (DoT/HoT)"><i class="fa-solid fa-clock-rotate-left"></i> $o1 Periodic</button>';
        tb += '<button type="button" class="desc-token-btn" data-token="$s2" title="Effect 2 value"><i class="fa-solid fa-crosshairs"></i> $s2 Effect 2</button>';
        tb += '<button type="button" class="desc-token-btn" data-token="$o2" title="Effect 2 periodic total"><i class="fa-solid fa-clock-rotate-left"></i> $o2 Periodic 2</button>';
        tb += '<button type="button" class="desc-token-btn" data-token="$d" title="Spell duration"><i class="fa-solid fa-hourglass"></i> $d Duration</button>';
        tb += '</div>';

        // Preview underneath
        tb += '<div class="desc-preview" id="descPreview" style="display:none">';
        tb += '<span class="desc-preview-label"><i class="fa-solid fa-eye"></i> Preview:</span>';
        tb += '<span class="desc-preview-text" id="descPreviewText"></span>';
        tb += '</div>';

        $desc.before(tb);
        // Add hint below textarea
        if (!$('#descTemplateHint').length) {
            $desc.after('<div id="descTemplateHint" style="font-size:10px;color:var(--text-muted);margin-top:2px"><i class="fa-solid fa-info-circle" style="margin-right:4px"></i>The WoW client fills in the token values automatically. Leave blank to inherit from the source spell.</div>');
        }
    }

    // Insert token at cursor position in #spellDesc
    $(document).on('click', '.desc-token-btn', function () {
        var token = $(this).data('token');
        var $ta = $(this).closest('.desc-toolbar, .gw-desc-toolbar').siblings('textarea').first();
        if (!$ta.length) $ta = $('#spellDesc');
        var ta = $ta[0];
        var start = ta.selectionStart, end = ta.selectionEnd;
        var val = $ta.val();
        $ta.val(val.substring(0, start) + token + val.substring(end));
        ta.selectionStart = ta.selectionEnd = start + token.length;
        $ta.focus().trigger('input');
    });

    // Live preview — resolve tokens with current damage values
    $(document).on('input', '#spellDesc', function () {
        updateDescPreview($(this).val(), '#descPreview', '#descPreviewText');
    });

    function updateDescPreview(text, previewSel, textSel) {
        if (!text || !text.match(/\$[sod]\d?/)) {
            $(previewSel).hide();
            return;
        }
        var dmgMin = parseInt($('#spellDmgMin').val()) || parseInt($('#gwDmgMin').val()) || 0;
        var dmgMax = parseInt($('#spellDmgMax').val()) || parseInt($('#gwDmgMax').val()) || 0;
        var s1 = dmgMin > 0 ? dmgMin + (dmgMax > dmgMin ? '-' + dmgMax : '') : '?';
        var resolved = text
            .replace(/\$s1/g, s1)
            .replace(/\$s2/g, '?')
            .replace(/\$o1/g, '?')
            .replace(/\$o2/g, '?')
            .replace(/\$d/g, '?');
        $(textSel).text(resolved);
        $(previewSel).show();
    }

    // Re-update preview when damage values change
    $(document).on('input', '#spellDmgMin, #spellDmgMax', function () {
        var text = $('#spellDesc').val();
        if (text) updateDescPreview(text, '#descPreview', '#descPreviewText');
    });

    // Generate all ranks checkbox
    $(document).on('change', '#chkGenerateAllRanks', function () {
        if ($(this).is(':checked')) {
            $('#rankPreviewWrap').slideDown(200);
            if (selectedSource) loadRankPreview();
        } else {
            $('#rankPreviewWrap').slideUp(200);
        }
    });

    // Rank preview state
    var sourceRankData = [];

    function loadRankPreview() {
        if (!selectedSource) return;
        $('#rankPreviewTable').html('<div style="padding:8px;color:var(--text-muted)"><i class="fa-solid fa-spinner fa-spin"></i> Loading ranks...</div>');
        $.getJSON('/Patch/SourceRanks', { entry: selectedSource.entry }, function (data) {
            sourceRankData = data.ranks || [];
            renderRankPreview();
        });
    }

    // Toggle spell properties panel
    $(document).on('click', '#spellPropsToggle', function () {
        var $body = $('#spellPropsBody');
        var open = $body.is(':visible');
        $body.slideToggle(200);
        $('#spellPropsChevron').toggleClass('fa-chevron-down fa-chevron-up');
    });

    // Class dropdown → populate skill tab dropdown
    $(document).on('change', '#spellClass', function () {
        var cls = parseInt($(this).val());
        var $tab = $('#spellSkillTab');
        $tab.html('<option value="">Auto (from source)</option>');
        if (cls && CLASS_SKILL_TABS[cls]) {
            CLASS_SKILL_TABS[cls].forEach(function (t) {
                $tab.append('<option value="' + t.key + '">' + t.label + '</option>');
            });
        }
    });

    // Trainer mode radio → show/hide cost/level row
    $(document).on('change', 'input[name="trainerMode"]', function () {
        var mode = $(this).val();
        if (mode === 'none') {
            $('#trainerDetailsRow').slideUp(150);
        } else {
            $('#trainerDetailsRow').slideDown(150);
        }
    });

    // Class → trainer_class mapping (WoW class IDs, not class_mask)
    var CLASS_ID_FROM_MASK = { 1: 1, 2: 2, 4: 3, 8: 4, 16: 5, 64: 7, 128: 8, 256: 9, 1024: 11 };

    function renderRankPreview() {
        if (!sourceRankData.length) {
            $('#rankPreviewTable').html('<div style="padding:8px;color:var(--text-muted)">Source spell has no rank chain — only Rank 1 will be created.</div>');
            return;
        }

        // Get user's rank 1 values for ratio calculation
        var userDmgMin = parseInt($('#spellDmgMin').val()) || 0;
        var userDmgMax = parseInt($('#spellDmgMax').val()) || 0;
        var userMana = parseInt($('#spellManaCost').val()) || 0;

        var src1 = sourceRankData[0];
        var src1Bp = src1.effectBasePoints1 || 0;
        var src1Ds = src1.effectDieSides1 || 0;
        var src1Mana = src1.manaCost || 0;

        // If user hasn't set values, use source values (ratio = 1.0)
        if (!userDmgMin && !userDmgMax) { userDmgMin = src1Bp + 1; userDmgMax = src1Bp + src1Ds; }
        if (!userMana) userMana = src1Mana;

        var dmgRatio = (src1Bp + src1Ds) > 0 ? (userDmgMin - 1 + (userDmgMax - userDmgMin)) / (src1Bp + src1Ds) : 1;
        var manaRatio = src1Mana > 0 ? userMana / src1Mana : 1;

        var h = '<div style="max-height:300px;overflow-y:auto;border:1px solid var(--border-light);border-radius:var(--radius-sm)">';
        h += '<table style="width:100%;border-collapse:collapse;font-size:11px">';
        h += '<thead><tr style="background:var(--surface-alt);position:sticky;top:0">';
        h += '<th style="padding:6px 8px;text-align:left;border-bottom:1px solid var(--border-light)">Rank</th>';
        h += '<th style="padding:6px 8px;text-align:right;border-bottom:1px solid var(--border-light)">Level</th>';
        h += '<th style="padding:6px 8px;text-align:right;border-bottom:1px solid var(--border-light)">Damage</th>';
        h += '<th style="padding:6px 8px;text-align:right;border-bottom:1px solid var(--border-light)">Mana</th>';
        h += '<th style="padding:6px 8px;text-align:right;border-bottom:1px solid var(--border-light)">Cast</th>';
        h += '<th style="padding:6px 8px;text-align:right;border-bottom:1px solid var(--border-light)">Coeff</th>';
        h += '<th style="padding:6px 8px;text-align:center;border-bottom:1px solid var(--border-light)"></th>';
        h += '</tr></thead><tbody>';

        sourceRankData.forEach(function (r, i) {
            var rank = r.rank || (i + 1);
            var bp = Math.round((r.effectBasePoints1 || 0) * dmgRatio);
            var ds = Math.round((r.effectDieSides1 || 0) * dmgRatio);
            var mn = Math.round((r.manaCost || 0) * manaRatio);
            var dmgMin = bp + 1, dmgMax = bp + ds;
            var cti = r.castingTimeIndex || 0;
            var castLabel = { 1: 'Inst', 5: '1.5s', 14: '2.5s', 15: '3.0s', 16: '3.5s', 19: '2.0s', 22: '3.5s' }[cti] || cti;
            var coeff = r.effectBonusCoefficient1 || 0;
            var isR1 = (rank === 1);

            h += '<tr style="border-bottom:1px solid var(--border-light)' + (isR1 ? ';background:rgba(85,153,255,.06)' : '') + '" data-rank="' + rank + '">';
            h += '<td style="padding:5px 8px;font-weight:' + (isR1 ? '700' : '400') + ';color:var(--text-primary)">R' + rank + (isR1 ? ' *' : '') + '</td>';
            h += '<td style="padding:5px 8px;text-align:right;color:var(--text-secondary)">' + (r.spellLevel || '?') + '</td>';
            h += '<td style="padding:5px 8px;text-align:right;color:var(--text-primary);font-family:monospace">' + dmgMin + '–' + dmgMax + '</td>';
            h += '<td style="padding:5px 8px;text-align:right;color:#5599ff;font-family:monospace">' + mn + '</td>';
            h += '<td style="padding:5px 8px;text-align:right;color:var(--text-muted)">' + castLabel + '</td>';
            h += '<td style="padding:5px 8px;text-align:right;color:var(--text-muted)">' + (coeff > 0 ? coeff.toFixed(2) : '-') + '</td>';
            if (!isR1) {
                h += '<td style="padding:5px 8px;text-align:center"><button class="rank-edit-btn" data-rank="' + rank + '" style="font-size:10px;padding:2px 6px;border:1px solid var(--border-light);border-radius:3px;background:var(--surface-alt);color:var(--text-muted);cursor:pointer" title="Override this rank"><i class="fa-solid fa-pen" style="font-size:9px"></i></button></td>';
            } else {
                h += '<td style="padding:5px 8px;text-align:center;font-size:10px;color:var(--text-muted)">from above</td>';
            }
            h += '</tr>';

            // Hidden edit row
            if (!isR1) {
                h += '<tr class="rank-edit-row" data-rank="' + rank + '" style="display:none;background:var(--surface-alt)">';
                h += '<td colspan="7" style="padding:8px">';
                h += '<div style="display:flex;gap:8px;flex-wrap:wrap">';
                h += '<div><label style="font-size:10px;color:var(--text-muted)">Dmg Min</label><input type="number" class="form-input rank-ovr" data-rank="' + rank + '" data-field="damageMin" value="' + dmgMin + '" style="width:70px;padding:4px 6px;font-size:11px" /></div>';
                h += '<div><label style="font-size:10px;color:var(--text-muted)">Dmg Max</label><input type="number" class="form-input rank-ovr" data-rank="' + rank + '" data-field="damageMax" value="' + dmgMax + '" style="width:70px;padding:4px 6px;font-size:11px" /></div>';
                h += '<div><label style="font-size:10px;color:var(--text-muted)">Mana</label><input type="number" class="form-input rank-ovr" data-rank="' + rank + '" data-field="manaCost" value="' + mn + '" style="width:70px;padding:4px 6px;font-size:11px" /></div>';
                h += '<div><label style="font-size:10px;color:var(--text-muted)">Level</label><input type="number" class="form-input rank-ovr" data-rank="' + rank + '" data-field="spellLevel" value="' + (r.spellLevel || '') + '" style="width:50px;padding:4px 6px;font-size:11px" /></div>';
                h += '<div><label style="font-size:10px;color:var(--text-muted)">Coeff</label><input type="number" class="form-input rank-ovr" data-rank="' + rank + '" data-field="spellCoefficient" value="' + (coeff || '') + '" step="0.01" style="width:60px;padding:4px 6px;font-size:11px" /></div>';
                h += '</div></td></tr>';
            }
        });

        h += '</tbody></table></div>';
        h += '<div style="font-size:10px;color:var(--text-muted);margin-top:6px">* Rank 1 uses your values above. Ranks 2+ scale proportionally. Click <i class="fa-solid fa-pen" style="font-size:9px"></i> to override individual ranks.</div>';
        h += '<div style="font-size:10px;color:var(--text-muted)">' + sourceRankData.length + ' ranks from source spell. Ratio: damage ×' + dmgRatio.toFixed(2) + ', mana ×' + manaRatio.toFixed(2) + '</div>';
        $('#rankPreviewTable').html(h);
    }

    // Toggle rank edit row
    $(document).on('click', '.rank-edit-btn', function () {
        var rank = $(this).data('rank');
        var $row = $('.rank-edit-row[data-rank="' + rank + '"]');
        $row.toggle();
    });

    // Re-render rank preview when user changes rank 1 values
    $(document).on('change', '#spellDmgMin, #spellDmgMax, #spellManaCost', function () {
        if ($('#chkGenerateAllRanks').is(':checked') && sourceRankData.length) {
            renderRankPreview();
        }
    });

    function buildQuickCreateDesigner() {
        var h = '<div class="designer-toggle-wrap"><button id="btnToggleDesigner" class="designer-toggle-btn"><i class="fa-solid fa-sliders"></i> Visual Designer<span class="designer-toggle-hint">Per-phase particle controls</span></button></div>';
        h += '<div id="designerPanel" class="designer-panel" style="display:none">';
        h += '<div class="designer-presets"><span class="designer-presets-label">Quick Recipes:</span>';
        ['subtle', 'powerful', 'cosmic', 'nova', 'reset'].forEach(function (r) { h += '<button class="recipe-btn" data-recipe="' + r + '">' + r.charAt(0).toUpperCase() + r.slice(1) + '</button>'; });
        h += '</div>';
        PHASES.forEach(function (phase) {
            h += '<div class="phase-card" data-phase="' + phase.key + '"><div class="phase-header"><div class="phase-title"><i class="fa-solid ' + phase.icon + ' phase-icon"></i><span>' + phase.label + '</span></div><span class="phase-desc">' + phase.desc + '</span><button class="phase-collapse-btn"><i class="fa-solid fa-chevron-down"></i></button></div><div class="phase-body">';
            var cid = 'phaseColor-' + phase.key;
            h += '<div class="phase-color-row"><span class="phase-color-label"><i class="fa-solid fa-palette knob-icon"></i> Phase Color</span><input type="color" id="' + cid + '" class="phase-color-input" value="#b432ff" data-phase="' + phase.key + '" /><span class="phase-color-hex" id="' + cid + '-hex"></span><span class="phase-color-clear" id="' + cid + '-clear" style="display:none">clear</span><span class="phase-color-badge" id="' + cid + '-badge">using global</span></div>';
            KNOBS.forEach(function (k) {
                var id = 'knob-' + phase.key + '-' + k.key;
                h += '<div class="knob-row"><div class="knob-label-wrap"><i class="fa-solid ' + k.icon + ' knob-icon"></i><label class="knob-label" for="' + id + '">' + k.label + '</label></div><div class="knob-control"><input type="range" id="' + id + '" class="knob-slider" min="' + k.min + '" max="' + k.max + '" step="' + k.step + '" value="' + k.def + '" data-phase="' + phase.key + '" data-knob="' + k.key + '"><span class="knob-value">' + k.def.toFixed(1) + k.unit + '</span></div></div>';
            });
            h += '</div></div>';
        });
        h += '</div>';
        $('#designerContainer').html(h);
    }

    $(document).on('click', '#btnToggleDesigner', function () { advancedMode = !advancedMode; $('#designerPanel').slideToggle(200); $(this).toggleClass('active'); if (advancedMode) applyMasterIntensity(parseFloat($('#intensity').val())); });
    $(document).on('click', '.phase-collapse-btn', function () { $(this).closest('.phase-card').find('.phase-body').slideToggle(150); $(this).find('i').toggleClass('fa-chevron-down fa-chevron-up'); });
    $(document).on('input', '.knob-slider:not(.studio-knob-slider)', function () { var p = $(this).data('phase'), k = $(this).data('knob'), v = parseFloat($(this).val()); phaseParams[p][k] = v; var u = '\u00d7'; KNOBS.forEach(function (kb) { if (kb.key === k) u = kb.unit; }); $(this).siblings('.knob-value').text(v.toFixed(1) + u); });

    $(document).on('input', '.phase-color-input:not(.studio-phase-color)', function () { var p = $(this).data('phase'), h = $(this).val(); if (/^#[0-9a-fA-F]{6}$/.test(h)) { phaseParams[p].color = h; $(this).addClass('active'); $('#phaseColor-' + p + '-hex').text(h); $('#phaseColor-' + p + '-clear').show(); $('#phaseColor-' + p + '-badge').text('override').css('color', h); } });
    $(document).on('click', '[id^="phaseColor-"][id$="-clear"]:not([id^="studioPhaseColor-"])', function () { var p = $(this).attr('id').replace('phaseColor-', '').replace('-clear', ''); phaseParams[p].color = null; $('#phaseColor-' + p).removeClass('active'); $('#phaseColor-' + p + '-hex').text(''); $(this).hide(); $('#phaseColor-' + p + '-badge').text('using global').css('color', ''); });

    $(document).on('click', '.recipe-btn', function () {
        var r = $(this).data('recipe');
        var recipes = { subtle: { precast: { emissionRate: .5, scale: .7, speed: .8, lifespan: .8, area: .5 }, cast: { emissionRate: .6, scale: .8, speed: .9, lifespan: .8, area: .6 }, missile: { emissionRate: .5, scale: .6, speed: .8, lifespan: .7, area: .5 }, impact: { emissionRate: .7, scale: .8, speed: .8, lifespan: .8, area: .6 } }, powerful: { precast: { emissionRate: 2, scale: 1.5, speed: 1.2, lifespan: 1.3, area: 1.5 }, cast: { emissionRate: 2.5, scale: 1.8, speed: 1.5, lifespan: 1.5, area: 2 }, missile: { emissionRate: 2, scale: 1.5, speed: 1, lifespan: 1.5, area: 1.5 }, impact: { emissionRate: 3, scale: 2.5, speed: 1.5, lifespan: 2, area: 2.5 } }, cosmic: { precast: { emissionRate: 4, scale: 1.2, speed: 3, lifespan: 2, area: 3 }, cast: { emissionRate: 2, scale: 1.5, speed: 2, lifespan: 1.5, area: 2 }, missile: { emissionRate: 3, scale: .8, speed: 2, lifespan: 2.5, area: 1.5 }, impact: { emissionRate: 3, scale: 2, speed: 2.5, lifespan: 2, area: 2.5 } }, nova: { precast: { emissionRate: 1.5, scale: 1, speed: 1, lifespan: 1, area: 1 }, cast: { emissionRate: 2, scale: 1.5, speed: 1.5, lifespan: 1.5, area: 1.5 }, missile: { emissionRate: 2, scale: 1, speed: 1, lifespan: 1.5, area: 1 }, impact: { emissionRate: 5, scale: 4, speed: 3, lifespan: 2.5, area: 5 } }, reset: { precast: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, cast: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, missile: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, impact: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, state: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, stateDone: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, channel: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 } } };
        if (!recipes[r]) return;
        PHASES.forEach(function (phase) { var vals = recipes[r][phase.key]; if (!vals) return; KNOBS.forEach(function (k) { var v = vals[k.key] || k.def; phaseParams[phase.key][k.key] = v; var $s = $('#knob-' + phase.key + '-' + k.key); if ($s.length) { var c = Math.min(Math.max(v, parseFloat($s.attr('min'))), parseFloat($s.attr('max'))); $s.val(c); $s.siblings('.knob-value').text(c.toFixed(1) + k.unit); } }); if (r === 'reset') { phaseParams[phase.key].color = null; $('#phaseColor-' + phase.key).removeClass('active'); $('#phaseColor-' + phase.key + '-hex').text(''); $('#phaseColor-' + phase.key + '-clear').hide(); $('#phaseColor-' + phase.key + '-badge').text('using global').css('color', ''); } });
        $('.recipe-btn').removeClass('active'); $(this).addClass('active'); setTimeout(function () { $('.recipe-btn').removeClass('active'); }, 600);
        if (!advancedMode) { advancedMode = true; $('#designerPanel').slideDown(200); $('#btnToggleDesigner').addClass('active'); }
    });

    $(document).on('click', '.studio-recipe-btn', function () {
        var r = $(this).data('recipe');
        var recipes = { subtle: { precast: { emissionRate: .5, scale: .7, speed: .8, lifespan: .8, area: .5 }, cast: { emissionRate: .6, scale: .8, speed: .9, lifespan: .8, area: .6 }, missile: { emissionRate: .5, scale: .6, speed: .8, lifespan: .7, area: .5 }, impact: { emissionRate: .7, scale: .8, speed: .8, lifespan: .8, area: .6 } }, powerful: { precast: { emissionRate: 2, scale: 1.5, speed: 1.2, lifespan: 1.3, area: 1.5 }, cast: { emissionRate: 2.5, scale: 1.8, speed: 1.5, lifespan: 1.5, area: 2 }, missile: { emissionRate: 2, scale: 1.5, speed: 1, lifespan: 1.5, area: 1.5 }, impact: { emissionRate: 3, scale: 2.5, speed: 1.5, lifespan: 2, area: 2.5 } }, cosmic: { precast: { emissionRate: 4, scale: 1.2, speed: 3, lifespan: 2, area: 3 }, cast: { emissionRate: 2, scale: 1.5, speed: 2, lifespan: 1.5, area: 2 }, missile: { emissionRate: 3, scale: .8, speed: 2, lifespan: 2.5, area: 1.5 }, impact: { emissionRate: 3, scale: 2, speed: 2.5, lifespan: 2, area: 2.5 } }, nova: { precast: { emissionRate: 1.5, scale: 1, speed: 1, lifespan: 1, area: 1 }, cast: { emissionRate: 2, scale: 1.5, speed: 1.5, lifespan: 1.5, area: 1.5 }, missile: { emissionRate: 2, scale: 1, speed: 1, lifespan: 1.5, area: 1 }, impact: { emissionRate: 5, scale: 4, speed: 3, lifespan: 2.5, area: 5 } }, reset: { precast: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, cast: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, missile: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, impact: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, state: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, stateDone: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 }, channel: { emissionRate: 1, scale: 1, speed: 1, lifespan: 1, area: 1 } } };
        if (!recipes[r]) return;
        PHASES.forEach(function (phase) { var vals = recipes[r][phase.key]; if (!vals) return; KNOBS.forEach(function (k) { var v = vals[k.key] || k.def; phaseParams[phase.key][k.key] = v; var $s = $('#studio-knob-' + phase.key + '-' + k.key); if ($s.length) { var c = Math.min(Math.max(v, parseFloat($s.attr('min'))), parseFloat($s.attr('max'))); $s.val(c); $s.siblings('.knob-value').text(c.toFixed(1) + k.unit); } }); if (r === 'reset') { phaseParams[phase.key].color = null; $('#studioPhaseColor-' + phase.key).removeClass('active'); $('#studioPhaseColor-' + phase.key + '-hex').text(''); $('#studioPhaseColor-' + phase.key + '-clear').hide(); $('#studioPhaseColor-' + phase.key + '-badge').text('using global').css('color', ''); } });
        $('.studio-recipe-btn').removeClass('active'); $(this).addClass('active'); setTimeout(function () { $('.studio-recipe-btn').removeClass('active'); }, 600);
    });

    function updatePhaseVisibility() { if (!activePhases) { $('#designerPanel .phase-card').show(); return; } $('#designerPanel .phase-card').each(function () { $(this).toggle(activePhases.indexOf($(this).data('phase')) >= 0); }); }

    function loadCustomIcons() { $.getJSON('/Patch/CustomIcons', function (data) { customIcons = data.icons || []; renderIconPicker(); renderStudioIconPicker(); }); }
    function renderIconPicker() {
        if (!$('#iconPickerWrap').length) $('<div id="iconPickerWrap"></div>').insertAfter('#iconResult');
        if (!customIcons.length) { $('#iconPickerWrap').hide(); return; }
        var h = '<div class="icon-picker-label">Or reuse an existing custom icon:</div><div class="icon-picker-grid">';
        customIcons.forEach(function (ic) { h += '<div class="icon-picker-item' + (selectedIconPath === ic.path ? ' selected' : '') + '" data-path="' + esc(ic.path) + '" data-name="' + esc(ic.name) + '" title="' + esc(ic.name) + '"><img src="' + esc(ic.webPath) + '" /></div>'; });
        h += '</div>';
        if (selectedIconPath) h += '<div class="icon-picker-selected"><i class="fa-solid fa-check"></i> Using: <strong>' + esc(selectedIconName) + '</strong> <span class="icon-picker-clear" id="btnClearIcon"><i class="fa-solid fa-xmark"></i></span></div>';
        $('#iconPickerWrap').html(h).show();
    }

    $(document).on('click', '.icon-picker-item:not(.studio-icon-pick)', function () { selectedIconPath = $(this).data('path'); selectedIconName = $(this).data('name'); $('#chkGenerateIcon').prop('checked', false); renderIconPicker(); });
    $(document).on('click', '#btnClearIcon', function () { selectedIconPath = null; selectedIconName = null; renderIconPicker(); });
    $(document).on('change', '#chkGenerateIcon', function () { if ($(this).is(':checked')) { selectedIconPath = null; selectedIconName = null; renderIconPicker(); } });

    // ── Quick Create Generate ──

    $('#btnGenerate').on('click', function () {
        if (!selectedSource) { showResult('error', 'Select a source spell to clone from.'); return; }
        var name = $('#spellName').val().trim();
        if (!name) { showResult('error', 'Enter a spell name.'); return; }
        var $b = $(this); $b.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Generating...');
        var payload = {
            spellName: name, nameSubtext: $('#nameSubtext').val().trim() || null, description: $('#spellDesc').val().trim() || null, sourceSpellEntry: selectedSource.entry, sourceSpellName: selectedSource.name, school: parseInt($('#spellSchool').val()), colorPreset: selectedPreset, customColor: selectedPreset === 'custom' ? customColor : null, intensity: parseFloat($('#intensity').val()), generateIcon: $('#chkGenerateIcon').is(':checked'), teachToCharacterGuid: parseInt($('#teachCharacter').val()) || 0, usePerPhaseParams: advancedMode, phaseParams: advancedMode ? phaseParams : null, existingIconPath: selectedIconPath || null,
            // Session 32: Spell Properties
            skillTabKey: $('#spellSkillTab').val() || null,
            damageMin: $('#spellDmgMin').val() ? parseInt($('#spellDmgMin').val()) : null,
            damageMax: $('#spellDmgMax').val() ? parseInt($('#spellDmgMax').val()) : null,
            manaCost: $('#spellManaCost').val() ? parseInt($('#spellManaCost').val()) : null,
            spellLevel: $('#spellSpellLevel').val() ? parseInt($('#spellSpellLevel').val()) : null,
            maxLevel: $('#spellMaxLevel').val() ? parseInt($('#spellMaxLevel').val()) : null,
            castingTimeIndex: $('#spellCastTime').val() ? parseInt($('#spellCastTime').val()) : null,
            rangeIndex: $('#spellRange').val() ? parseInt($('#spellRange').val()) : null,
            spellCoefficient: $('#spellCoeff').val() ? parseFloat($('#spellCoeff').val()) : null,
            damagePerLevel: $('#spellDmgPerLevel').val() ? parseFloat($('#spellDmgPerLevel').val()) : null,
            missileSpeed: $('#spellMissileSpeed').val() ? parseFloat($('#spellMissileSpeed').val()) : null,
            cooldown: $('#spellCooldown').val() ? Math.round(parseFloat($('#spellCooldown').val()) * 1000) : null,
            generateAllRanks: $('#chkGenerateAllRanks').is(':checked'),
            rankOverrides: collectRankOverrides()
        };

        function collectRankOverrides() {
            if (!$('#chkGenerateAllRanks').is(':checked')) return null;
            var ovr = {};
            $('.rank-ovr').each(function () {
                var rank = $(this).data('rank');
                var field = $(this).data('field');
                var val = $(this).val();
                if (!val) return;
                if (!ovr[rank]) ovr[rank] = {};
                if (field === 'spellCoefficient') ovr[rank][field] = parseFloat(val);
                else ovr[rank][field] = parseInt(val);
            });
            return Object.keys(ovr).length ? ovr : null;
        }
        $.ajax({
            url: '/Patch/Generate', method: 'POST', contentType: 'application/json', data: JSON.stringify(payload),
            success: function (r) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-bolt"></i> Create Spell & Generate Patch'); if (r.success) {
                    var m = '<div class="result-title"><i class="fa-solid fa-check"></i> <strong>' + esc(name) + '</strong> (#' + r.spellEntry + ') created</div>'; if (r.hasPatch) { m += '<div class="result-detail">Unified patch: <code>' + esc(r.patchFileName) + '</code>'; if (r.totalSpellsInPatch) m += ' (' + r.totalSpellsInPatch + ' spells)'; m += '</div><div class="result-detail">' + r.m2Count + ' M2(s), ' + r.totalFiles + ' files</div>'; } if (r.taught) m += '<div class="result-detail"><i class="fa-solid fa-graduation-cap"></i> Taught — relog required</div>'; if (r.iconResult) m += '<div class="result-detail"><i class="fa-solid fa-image"></i> Icon: ' + esc(r.iconResult.iconName) + ' (' + r.iconResult.source + ')</div>';
                    if (r.ranksGenerated && r.ranksGenerated.length > 1) m += '<div class="result-detail"><i class="fa-solid fa-layer-group"></i> ' + r.ranksGenerated.length + ' ranks generated (entries: ' + r.ranksGenerated.map(function (rk) { return '#' + rk.entry; }).join(', ') + ')</div>';
                    m += '<div class="result-detail"><em>Server restart required.</em></div>'; if (!r.hasPatch && r.warning) m += '<div class="result-detail" style="color:var(--text-warning)">' + esc(r.warning) + '</div>';
                    // Register at trainer based on selected mode
                    var trainerMode = $('input[name="trainerMode"]:checked').val();
                    if (trainerMode && trainerMode !== 'none' && r.spellEntry) {
                        var tCost = parseInt($('#trainerCost').val()) || 0;
                        var tLevel = parseInt($('#trainerReqLevel').val()) || 1;

                        if (trainerMode === 'copySource' && selectedSource) {
                            $.ajax({
                                url: '/Patch/CopySourceTrainers', method: 'POST', contentType: 'application/json',
                                data: JSON.stringify({ spellEntry: r.spellEntry, sourceSpellEntry: selectedSource.entry, cost: tCost, reqLevel: tLevel }),
                                success: function (tr) {
                                    if (tr.success) {
                                        var extra = '<div class="result-detail"><i class="fa-solid fa-chalkboard-user"></i> Copied ' + tr.copiedCount + ' trainer entries from ' + esc(selectedSource.name) + '</div>';
                                        $('#generateResult .patch-result').append(extra);
                                    }
                                }
                            });
                        } else if (trainerMode === 'classTemplate') {
                            var cls = parseInt($('#spellClass').val()) || 0;
                            var trainerClass = cls; // Class ID = Trainer Class directly (gotcha #97)
                            if (trainerClass > 0) {
                                var spName = $('#spellName').val() || 'Custom Spell';
                                $.ajax({
                                    url: '/Patch/RegisterAtClassTrainers', method: 'POST', contentType: 'application/json',
                                    data: JSON.stringify({ spellEntry: r.spellEntry, trainerClass: trainerClass, cost: tCost, reqLevel: tLevel, spellName: spName, rankSubtext: 'Rank 1' }),
                                    success: function (tr) {
                                        if (tr.success) {
                                            var extra = '<div class="result-detail"><i class="fa-solid fa-chalkboard-user"></i> Added to all ' + (CLASS_NAMES[cls] || 'class') + ' trainers (template #' + tr.templateId + ')</div>';
                                            $('#generateResult .patch-result').append(extra);
                                        }
                                    }
                                });
                            }
                        }
                    }
                    showResult('success', m); loadPatches(); loadCustomSpells(); loadCustomIcons();
                } else showResult('error', r.error || 'Unknown error.');
            },
            error: function (xhr) { $b.prop('disabled', false).html('<i class="fa-solid fa-bolt"></i> Create Spell & Generate Patch'); showResult('error', 'Failed: ' + (xhr.responseText || xhr.statusText)); }
        });
    });

    function showResult(type, html) { $('#generateResult').html('<div class="patch-result ' + type + '">' + html + '</div>'); }

    // ── Custom Spells List / Delete / Teach / Unlearn ──

    function loadCustomSpells() {
        $.getJSON('/Patch/CustomSpells', function (data) {
            if (!data.spells || !data.spells.length) { $('#customSpellsList').html('<div class="empty-state">No custom spells yet.</div>'); return; }

            // Group spells by firstSpell (rank chain root)
            var groups = {};
            var order = [];
            data.spells.forEach(function (s) {
                var key = s.firstSpell || s.entry;
                if (!groups[key]) { groups[key] = []; order.push(key); }
                groups[key].push(s);
            });

            // Sort each group by rank
            Object.keys(groups).forEach(function (k) {
                groups[k].sort(function (a, b) { return (a.rank || 1) - (b.rank || 1); });
            });

            var h = '';
            order.forEach(function (key) {
                var ranks = groups[key];
                var r1 = ranks[0];
                var sc = SCHOOL_NAMES[r1.school] || '?', c = SCHOOL_COLORS[r1.school] || '#aaa';
                var isMulti = ranks.length > 1;

                // Main row — always shows R1
                h += '<div class="custom-spell-group" data-first="' + key + '">';
                h += '<div class="custom-spell-row' + (isMulti ? ' custom-spell-multi' : '') + '">';
                h += '<div class="custom-spell-info">';
                if (isMulti) {
                    h += '<button class="custom-spell-expand" data-first="' + key + '"><i class="fa-solid fa-chevron-right"></i></button>';
                }
                h += '<span class="custom-spell-id">#' + r1.entry + '</span>';
                h += '<span class="custom-spell-name">' + esc(r1.name) + '</span>';
                h += '<span class="custom-spell-school" style="background:' + c + '22;color:' + c + '">' + sc + '</span>';
                if (isMulti) {
                    h += '<span class="custom-spell-rank-badge">' + ranks.length + ' ranks</span>';
                }
                h += '</div>';
                h += '<div class="custom-spell-actions">';
                if (r1.hasManifest) { h += '<button class="spell-action-btn btn-retune-spell" data-entry="' + r1.entry + '" data-source="' + (r1.sourceEntry || r1.entry) + '" data-name="' + esc(r1.name) + '" title="Retune Textures &amp; Emitters"><i class="fa-solid fa-sliders"></i></button>'; }
                h += '<button class="spell-action-btn btn-teach-spell" data-entry="' + r1.entry + '" data-name="' + esc(r1.name) + '"><i class="fa-solid fa-graduation-cap"></i></button>';
                h += '<button class="spell-action-btn btn-delete-spell" data-entry="' + r1.entry + '" data-name="' + esc(r1.name) + '"><i class="fa-solid fa-trash"></i></button>';
                h += '</div></div>';

                // Rank drill-down rows (hidden by default)
                if (isMulti) {
                    h += '<div class="custom-spell-ranks" data-first="' + key + '" style="display:none">';
                    ranks.forEach(function (rk) {
                        h += '<div class="custom-spell-rank-row">';
                        h += '<span class="custom-spell-rank-num">R' + (rk.rank || 1) + '</span>';
                        h += '<span class="custom-spell-rank-id">#' + rk.entry + '</span>';
                        h += '<span class="custom-spell-rank-sub">' + esc(rk.nameSubtext || '') + '</span>';
                        h += '<span class="custom-spell-rank-detail">Lv' + (rk.spellLevel || '?') + '</span>';
                        if (rk.manaCost > 0) h += '<span class="custom-spell-rank-detail" style="color:#5599ff">' + rk.manaCost + ' mana</span>';
                        h += '</div>';
                    });
                    h += '</div>';
                }

                h += '</div>';
            });
            $('#customSpellsList').html(h);
        }).fail(function () { $('#customSpellsList').html('<div class="empty-state error">Failed to load.</div>'); });
    }

    // Toggle rank drill-down
    $(document).on('click', '.custom-spell-expand', function (e) {
        e.stopPropagation();
        var first = $(this).data('first');
        var $ranks = $('.custom-spell-ranks[data-first="' + first + '"]');
        var $icon = $(this).find('i');
        $ranks.slideToggle(200);
        $icon.toggleClass('fa-chevron-right fa-chevron-down');
    });

    $(document).on('click', '.btn-delete-spell', function () { var e = $(this).data('entry'), n = $(this).data('name'); if (!confirm('Delete "' + n + '" (#' + e + ')?\n\n\u2022 Removes from characters\n\u2022 Rebuilds patch\n\nServer restart required.')) return; $.ajax({ url: '/Patch/DeleteSpell', method: 'POST', contentType: 'application/json', data: JSON.stringify({ entry: e }), success: function (r) { if (r.success) { showResult('success', '<i class="fa-solid fa-check"></i> Deleted <strong>' + esc(n) + '</strong>.'); loadCustomSpells(); loadPatches(); } else showResult('error', r.error || 'Failed'); } }); });
    $(document).on('click', '.btn-teach-spell', function () { var e = $(this).data('entry'), n = $(this).data('name'); var co = '<option value="">Select...</option>'; characters.forEach(function (c) { co += '<option value="' + c.guid + '">' + esc(c.name) + ' (Lv' + c.level + ' ' + (CLASS_NAMES[c.charClass] || '?') + ')</option>'; }); $('#generateResult').html('<div class="teach-modal"><div class="teach-title">Teach <strong>' + esc(n) + '</strong> (#' + e + ')</div><select id="teachModalChar" class="teach-select">' + co + '</select><div class="teach-buttons"><button class="teach-btn teach-confirm" id="btnTeachConfirm" data-entry="' + e + '"><i class="fa-solid fa-graduation-cap"></i> Teach</button><button class="teach-btn teach-cancel" id="btnTeachCancel">Cancel</button></div><div id="teachKnownBy" class="teach-known"></div></div>'); $.getJSON('/Patch/SpellCharacters', { entry: e }, function (d) { if (d.characters && d.characters.length) { var k = '<div class="teach-known-title">Already known by:</div>'; d.characters.forEach(function (c) { k += '<div class="teach-known-row"><span>' + esc(c.name) + '</span><button class="teach-btn teach-unlearn" data-entry="' + e + '" data-guid="' + c.guid + '" data-name="' + esc(c.name) + '"><i class="fa-solid fa-xmark"></i> Unlearn</button></div>'; }); $('#teachKnownBy').html(k); } }); });
    $(document).on('click', '#btnTeachConfirm', function () { var e = $(this).data('entry'), g = parseInt($('#teachModalChar').val()); if (!g) { alert('Select a character.'); return; } $.ajax({ url: '/Patch/TeachSpell', method: 'POST', contentType: 'application/json', data: JSON.stringify({ spellEntry: e, characterGuid: g }), success: function (r) { if (r.success) showResult('success', '<i class="fa-solid fa-graduation-cap"></i> Taught! Relog required.'); else showResult('error', 'Failed.'); } }); });
    $(document).on('click', '#btnTeachCancel', function () { $('#generateResult').empty(); });
    $(document).on('click', '.teach-unlearn', function () { var e = $(this).data('entry'), g = $(this).data('guid'), n = $(this).data('name'); if (!confirm('Remove from ' + n + '?')) return; var $r = $(this).closest('.teach-known-row'); $.ajax({ url: '/Patch/UnlearnSpell', method: 'POST', contentType: 'application/json', data: JSON.stringify({ spellEntry: e, characterGuid: g }), success: function (r) { if (r.success) $r.fadeOut(200, function () { $r.remove(); }); } }); });

    // ── Icon Generation ──
    $('#btnGenerateIcon').on('click', function () { var n = $('#spellName').val().trim(); if (!n) { alert('Enter a spell name first.'); return; } var $b = $(this); $b.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Generating...'); $.ajax({ url: '/Patch/GenerateIcon', method: 'POST', contentType: 'application/json', data: JSON.stringify({ spellName: n, school: parseInt($('#spellSchool').val()), description: $('#spellDesc').val().trim() || null }), success: function (r) { $b.prop('disabled', false).html('<i class="fa-solid fa-wand-magic-sparkles"></i> Preview'); if (r.success) { var h = '<div class="icon-result"><div class="icon-result-title"><i class="fa-solid fa-image"></i> ' + esc(r.iconName) + '</div><div class="icon-result-meta">Source: ' + esc(r.source) + '</div>'; if (r.prompt) h += '<div class="icon-result-prompt">' + esc(r.prompt) + '</div>'; if (r.iconPath) h += '<img class="icon-preview" src="/images/icons/custom/' + esc(r.iconName) + '.png" onerror="this.style.display=\'none\'" />'; h += '</div>'; $('#iconResult').html(h); } else $('#iconResult').html('<div class="icon-result error">' + esc(r.error || 'Failed') + '</div>'); }, error: function () { $b.prop('disabled', false).html('<i class="fa-solid fa-wand-magic-sparkles"></i> Preview'); $('#iconResult').html('<div class="icon-result error">Request failed.</div>'); } }); });

    // ── Patch List ──
    function loadPatches() { $.getJSON('/Patch/List', function (data) { if (!data.patches || !data.patches.length) { $('#topbarPatchName').text('No patch yet'); $('#topbarPatchMeta').text(''); $('#btnTopbarDownload').hide(); return; } var p = data.patches[0]; $('#topbarPatchName').text(p.fileName); $('#topbarPatchMeta').text(formatSize(p.sizeBytes) + ' \u00b7 ' + new Date(p.created).toLocaleString()); $('#btnTopbarDownload').data('file', p.fileName).show(); }).fail(function () { $('#topbarPatchName').text('Failed to load'); }); }
    $(document).on('click', '.btn-download-patch, #btnTopbarDownload', function () { window.location.href = '/Patch/Download?file=' + encodeURIComponent($(this).data('file')); });
    $(document).on('click', '.btn-delete-patch', function () { var f = $(this).data('file'); if (!confirm('Delete ' + f + '?')) return; $.ajax({ url: '/Patch/Delete', method: 'POST', contentType: 'application/json', data: JSON.stringify({ fileName: f }), success: function (r) { if (r.success) loadPatches(); else alert(r.error || 'Failed'); } }); });

    // ── Characters ──
    function loadCharacters() { $.getJSON('/Patch/Characters', function (data) { characters = data.characters || []; var h = '<option value="0">None (teach later)</option>'; characters.forEach(function (c) { h += '<option value="' + c.guid + '">' + esc(c.name) + ' (Lv' + c.level + ' ' + (CLASS_NAMES[c.charClass] || '?') + ')</option>'; }); $('#teachCharacter').html(h); syncStudioCharacters(); }); }

    $(document).on('click', '#installToggle', function () { $('#installBody').toggleClass('open'); $('#installChevron').toggleClass('fa-chevron-right fa-chevron-down'); });

    function formatSize(b) { if (b < 1024) return b + ' B'; if (b < 1048576) return (b / 1024).toFixed(1) + ' KB'; return (b / 1048576).toFixed(1) + ' MB'; }
    function esc(t) { if (!t && t !== 0) return ''; var d = document.createElement('div'); d.textContent = t; return d.innerHTML; }

    // ═══════════════════════════════════════════════════════════════
    // SESSION 29: RETUNE MODAL — Textures + Emitters + Tuning JSON
    // ═══════════════════════════════════════════════════════════════

    $(document).on('click', '.btn-retune-spell', function () {
        var entry = $(this).data('entry'), name = $(this).data('name'), source = $(this).data('source') || entry;
        var mReq = $.getJSON('/Patch/SpellManifest', { spellName: name });
        var eReq = $.getJSON('/Patch/M2Emitters', { entry: source });
        $.when(mReq, eReq).done(function (mR, eR) {
            var mD = mR[0], eD = eR[0];
            var entries = (mD.success && mD.entries) ? mD.entries : [];
            var emPhases = (eD.success && eD.phases) ? eD.phases : [];
            renderRetuneModal(entry, name, entries, emPhases);
        }).fail(function () { alert('Failed to load data for ' + name); });
    });

    function renderRetuneModal(entry, spellName, manifestEntries, emitterPhases) {
        var h = '<div class="retune-overlay" id="retuneOverlay"><div class="retune-modal">';
        h += '<div class="retune-header"><div class="retune-title"><i class="fa-solid fa-sliders"></i> Retune — ' + esc(spellName) + ' <span style="font-size:12px;color:var(--text-muted);font-weight:400">#' + entry + '</span></div>';
        h += '<button class="retune-close" id="btnRetuneClose"><i class="fa-solid fa-xmark"></i></button></div>';
        h += '<div class="retune-tabs"><div class="retune-tab active" data-tab="textures"><i class="fa-solid fa-image"></i> Textures</div>';
        h += '<div class="retune-tab" data-tab="emitters"><i class="fa-solid fa-atom"></i> Emitters</div>';
        h += '<div class="retune-tab" data-tab="tuning"><i class="fa-solid fa-code"></i> Tuning JSON</div></div>';

        // Tab 1: Textures
        h += '<div class="retune-tab-body active" data-tab="textures">';
        h += '<div class="retune-controls"><div class="retune-control-group"><span class="retune-control-label">Floor</span>';
        h += '<input type="range" class="retune-slider" id="retuneFloor" min="0" max="0.30" step="0.01" value="0" />';
        h += '<span class="retune-value" id="retuneFloorVal">default</span></div>';
        h += '<div class="retune-control-group"><span class="retune-control-label">Knee</span>';
        h += '<input type="range" class="retune-slider" id="retuneKnee" min="0" max="0.20" step="0.01" value="0" />';
        h += '<span class="retune-value" id="retuneKneeVal">default</span></div></div>';
        h += '<div class="retune-slots">';
        var phases = {}, phaseOrder = ['precast', 'cast', 'missile', 'impact', 'state', 'stateDone', 'channel'];
        manifestEntries.forEach(function (e) { if (!phases[e.phase]) phases[e.phase] = []; phases[e.phase].push(e); });
        phaseOrder.forEach(function (pk) {
            if (!phases[pk]) return;
            h += '<div class="retune-phase-label"><i class="fa-solid fa-layer-group"></i> ' + pk.charAt(0).toUpperCase() + pk.slice(1) + '</div>';
            phases[pk].forEach(function (slot) {
                h += '<div class="retune-slot"><span class="retune-slot-idx">' + slot.textureIndex + '</span>';
                h += '<span class="retune-slot-role role-' + slot.role + '">' + slot.role + '</span>';
                h += '<span class="retune-slot-file" title="' + esc(slot.originalFilename) + '">' + esc(slot.originalFilename) + '</span>';
                h += '<div class="retune-slot-imgs">';
                h += slot.hasPng ? '<img class="retune-slot-img" src="' + esc(slot.pngWebPath) + '?t=' + Date.now() + '" />' : '<div class="retune-no-img">no raw</div>';
                h += '<span class="retune-slot-arrow"><i class="fa-solid fa-arrow-right"></i></span>';
                h += slot.hasDebugPng ? '<img class="retune-slot-img" src="' + esc(slot.debugPngWebPath) + '?t=' + Date.now() + '" />' : '<div class="retune-no-img">not yet</div>';
                h += '</div></div>';
            });
        });
        h += '</div></div>';

        // Tab 2: Emitters
        h += '<div class="retune-tab-body" data-tab="emitters"><div class="retune-emitters">';
        var BL = { 0: 'Opaque', 1: 'Mod', 2: 'Alpha', 4: 'Additive' }, EL = { 0: 'Point', 1: 'Sphere', 2: 'Plane', 3: 'Spline' };
        if (!emitterPhases.length) { h += '<div style="padding:20px;text-align:center;color:var(--text-muted)">No emitter data available.</div>'; }
        emitterPhases.forEach(function (ep) {
            h += '<div class="retune-phase-label"><i class="fa-solid fa-layer-group"></i> ' + ep.phase + ' <span style="font-size:10px;font-weight:400;color:var(--text-muted);margin-left:6px">' + esc(ep.m2Path) + '</span></div>';
            ep.emitters.forEach(function (em) {
                h += '<div class="retune-emitter-card"><div class="retune-emitter-header">';
                h += '<span class="retune-emitter-idx">Emitter ' + em.index + '</span>';
                h += '<span class="retune-emitter-badge">blend: ' + (BL[em.blendMode] || em.blendMode) + '</span>';
                h += '<span class="retune-emitter-badge">type: ' + (EL[em.emitterType] || em.emitterType) + '</span>';
                h += '<span class="retune-emitter-badge">tex: ' + em.textureId + '</span>';
                h += '<span class="retune-emitter-badge">scale: ' + em.scaleStart.toFixed(2) + '\u2192' + em.scaleMid.toFixed(2) + '\u2192' + em.scaleEnd.toFixed(2) + '</span>';
                h += '</div><div class="retune-prop-grid">';
                if (em.tracks) {
                    ['emissionRate', 'lifespan', 'emissionSpeed', 'gravity', 'emissionAreaLength', 'emissionAreaWidth', 'speedVariation', 'verticalRange', 'horizontalRange'].forEach(function (tk) {
                        if (!em.tracks[tk]) return; var v = em.tracks[tk];
                        h += '<div class="retune-prop"><span class="retune-prop-name">' + tk + '</span>';
                        h += '<span class="retune-prop-val">' + v.value.toFixed(3) + (v.keyframes > 1 ? ' <span style="color:var(--text-muted);font-size:9px">(' + v.keyframes + 'kf)</span>' : '') + '</span></div>';
                    });
                }
                h += '</div></div>';
            });
        });
        h += '</div></div>';

        // Tab 3: Tuning JSON
        var template = buildTuningTemplate(entry, spellName, manifestEntries, emitterPhases);
        h += '<div class="retune-tab-body" data-tab="tuning"><div class="retune-json-wrap">';
        h += '<div class="retune-json-hint">Paste a Spell Tuning Preset JSON to completely reconfigure this spell. Define per-phase M2 sources, texture slot assignments, and emitter property overrides.</div>';
        h += '<textarea class="retune-json-textarea" id="retuneTuningJson" spellcheck="false">' + esc(JSON.stringify(template, null, 2)) + '</textarea></div></div>';

        // Footer
        h += '<div class="retune-footer"><span class="retune-status" id="retuneStatus">' + manifestEntries.length + ' textures, ' + emitterPhases.reduce(function (a, p) { return a + p.emitters.length; }, 0) + ' emitters</span>';
        h += '<button class="retune-btn retune-btn-secondary" id="btnRetuneClose2">Cancel</button>';
        h += '<button class="retune-btn retune-btn-primary" id="btnRetuneReprocess" data-entry="' + entry + '" data-name="' + esc(spellName) + '"><i class="fa-solid fa-rotate"></i> Reprocess Textures</button>';
        h += '<button class="retune-btn retune-btn-warning" id="btnRetuneTuningApply" data-entry="' + entry + '" data-name="' + esc(spellName) + '"><i class="fa-solid fa-bolt"></i> Apply Tuning</button>';
        h += '</div></div></div>';
        $('#retuneOverlay').remove(); $('body').addClass('retune-open').append(h);
    }

    function buildTuningTemplate(entry, spellName, manifestEntries, emitterPhases) {
        var t = { presetName: spellName + '_v1', sourceSpellEntry: entry, theme: 'lightning', phases: {} };
        var mP = {}, eP = {}, all = {};
        manifestEntries.forEach(function (e) { if (!mP[e.phase]) mP[e.phase] = []; mP[e.phase].push(e); });
        emitterPhases.forEach(function (ep) { eP[ep.phase] = ep; });
        Object.keys(mP).forEach(function (k) { all[k] = true; }); Object.keys(eP).forEach(function (k) { all[k] = true; });
        Object.keys(all).forEach(function (pk) {
            var phase = {};
            if (mP[pk]) { phase.textures = mP[pk].map(function (s) { var d = s.blpFilename.lastIndexOf('.'); return { slotIndex: s.textureIndex, sourcePng: (d >= 0 ? s.blpFilename.substring(0, d) : s.blpFilename) + '.png', role: s.role.charAt(0).toUpperCase() + s.role.slice(1), density: null, floorPercent: null, kneeWidth: null }; }); }
            if (eP[pk]) {
                phase.sourceM2 = eP[pk].m2Path; phase.emitters = eP[pk].emitters.map(function (em) {
                    var p = { emitterIndex: em.index }; if (em.tracks) { if (em.tracks.emissionRate) p.emissionRate = em.tracks.emissionRate.value; if (em.tracks.lifespan) p.lifespan = em.tracks.lifespan.value; if (em.tracks.emissionSpeed) p.emissionSpeed = em.tracks.emissionSpeed.value; if (em.tracks.gravity) p.gravity = em.tracks.gravity.value; if (em.tracks.emissionAreaLength) p.emissionAreaLength = em.tracks.emissionAreaLength.value; if (em.tracks.emissionAreaWidth) p.emissionAreaWidth = em.tracks.emissionAreaWidth.value; }
                    p.scaleStart = em.scaleStart; p.scaleMid = em.scaleMid; p.scaleEnd = em.scaleEnd; return p;
                });
            }
            t.phases[pk] = phase;
        });
        return t;
    }

    $(document).on('click', '.retune-tab', function () { var t = $(this).data('tab'); $('.retune-tab').removeClass('active'); $(this).addClass('active'); $('.retune-tab-body').removeClass('active'); $('.retune-tab-body[data-tab="' + t + '"]').addClass('active'); });
    $(document).on('input', '#retuneFloor', function () { var v = parseFloat($(this).val()); $('#retuneFloorVal').text(v === 0 ? 'default' : (v * 100).toFixed(0) + '%'); });
    $(document).on('input', '#retuneKnee', function () { var v = parseFloat($(this).val()); $('#retuneKneeVal').text(v === 0 ? 'default' : (v * 100).toFixed(0) + '%'); });
    $(document).on('click', '#btnRetuneClose, #btnRetuneClose2', function () { $('#retuneOverlay').remove(); $('body').removeClass('retune-open'); });
    $(document).on('click', '#retuneOverlay', function (e) { if (e.target === this) { $(this).remove(); $('body').removeClass('retune-open'); } });

    $(document).on('click', '#btnRetuneReprocess', function () {
        var $b = $(this), entry = $b.data('entry'), name = $b.data('name');
        var fl = parseFloat($('#retuneFloor').val()), kn = parseFloat($('#retuneKnee').val());
        $b.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Reprocessing...');
        $('#retuneStatus').text('Reprocessing textures...');
        $.ajax({
            url: '/Patch/ReprocessTextures', method: 'POST', contentType: 'application/json',
            data: JSON.stringify({ spellEntry: entry, spellName: name, floorOverride: fl > 0 ? fl : null, kneeOverride: kn > 0 ? kn : null }),
            success: function (r) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-rotate"></i> Reprocess Textures');
                if (r.success) { $('#retuneStatus').html('<i class="fa-solid fa-check" style="color:#2ecc71"></i> ' + r.reprocessedCount + ' textures done. <strong>Restart required.</strong>'); refreshRetune(entry, name); loadPatches(); }
                else { $('#retuneStatus').html('<i class="fa-solid fa-xmark" style="color:#e74c3c"></i> ' + esc(r.error)); }
            },
            error: function () { $b.prop('disabled', false).html('<i class="fa-solid fa-rotate"></i> Reprocess Textures'); $('#retuneStatus').html('<i class="fa-solid fa-xmark" style="color:#e74c3c"></i> Request failed.'); }
        });
    });

    $(document).on('click', '#btnRetuneTuningApply', function () {
        var $b = $(this), entry = $b.data('entry'), name = $b.data('name');
        var jsonStr = $('#retuneTuningJson').val().trim();
        if (!jsonStr) { alert('Paste a tuning JSON first.'); return; }
        var preset; try { preset = JSON.parse(jsonStr); } catch (e) { alert('Invalid JSON: ' + e.message); return; }
        if (!preset.presetName) { alert('Preset must have a presetName field.'); return; }
        $b.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Applying...');
        $('#retuneStatus').text('Applying tuning — reprocessing textures and patching emitters...');
        $.ajax({
            url: '/Patch/ApplySpellTuning', method: 'POST', contentType: 'application/json',
            data: JSON.stringify({ spellEntry: entry, spellName: name, preset: preset }),
            success: function (r) {
                $b.prop('disabled', false).html('<i class="fa-solid fa-bolt"></i> Apply Tuning');
                if (r.success) {
                    var msg = r.texturesProcessed + ' textures, ' + r.emittersPatched + ' emitter props. ';
                    if (r.patchRebuilt) msg += 'Patch rebuilt. ';
                    msg += '<strong>Restart required.</strong>';
                    $('#retuneStatus').html('<i class="fa-solid fa-check" style="color:#2ecc71"></i> ' + msg); refreshRetune(entry, name); loadPatches();
                }
                else { $('#retuneStatus').html('<i class="fa-solid fa-xmark" style="color:#e74c3c"></i> ' + esc(r.error)); }
            },
            error: function (xhr) { $b.prop('disabled', false).html('<i class="fa-solid fa-bolt"></i> Apply Tuning'); $('#retuneStatus').html('<i class="fa-solid fa-xmark" style="color:#e74c3c"></i> ' + (xhr.statusText || 'Failed')); }
        });
    });

    function refreshRetune(entry, name) {
        var sourceEntry = $('.btn-retune-spell[data-entry="' + entry + '"]').data('source') || entry;
        var mReq = $.getJSON('/Patch/SpellManifest', { spellName: name });
        var eReq = $.getJSON('/Patch/M2Emitters', { entry: sourceEntry });
        $.when(mReq, eReq).done(function (mR, eR) {
            renderRetuneModal(entry, name, (mR[0].success ? mR[0].entries : []), (eR[0].success ? eR[0].phases : []));
            $('#retuneStatus').html('<i class="fa-solid fa-check" style="color:#2ecc71"></i> Refreshed. <strong>Restart required.</strong>');
        });
    }
});
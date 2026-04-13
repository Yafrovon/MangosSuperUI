// MangosSuperUI — Spells Browser + Editor JS

$(function () {

    var currentPage = 1;
    var totalPages = 1;
    var currentPageSize = parseInt(localStorage.getItem('msui_spells_pageSize')) || 50;
    var currentIcons = {};
    var currentSpell = null;   // loaded spell data
    var editedFields = {};     // tracked changes { column: newValue }
    var dbcMeta = null;        // DBC dropdown data (cast times, durations, ranges)
    var viewMode = 'grouped';  // 'flat' or 'grouped'

    // ===================== ENUMS =====================

    var SCHOOL_NAMES = { 0: 'Physical', 1: 'Holy', 2: 'Fire', 3: 'Nature', 4: 'Frost', 5: 'Shadow', 6: 'Arcane' };
    var POWER_TYPES = { 0: 'Mana', 1: 'Rage', 2: 'Focus', 3: 'Energy', '-2': 'Health' };
    var DISPEL_TYPES = { 0: 'None', 1: 'Magic', 2: 'Curse', 3: 'Disease', 4: 'Poison', 5: 'Stealth', 6: 'Invisibility', 7: 'All', 9: 'Enrage' };
    var MECHANIC_NAMES = { 0: 'None', 1: 'Charm', 2: 'Disoriented', 3: 'Disarm', 4: 'Distract', 5: 'Fear', 6: 'Fumble', 7: 'Root', 8: 'Pacify', 9: 'Silence', 10: 'Sleep', 11: 'Snare', 12: 'Stun', 13: 'Freeze', 14: 'Knockout', 15: 'Bleed', 16: 'Bandage', 17: 'Polymorph', 18: 'Banish', 19: 'Shield', 20: 'Shackle', 21: 'Mount', 22: 'Persuade', 23: 'Turn', 24: 'Horror', 25: 'Invulnerability', 26: 'Interrupt', 27: 'Daze', 28: 'Discovery', 29: 'Immune Shield', 30: 'Sapped' };
    var DMG_CLASS = { 0: 'None', 1: 'Magic', 2: 'Melee', 3: 'Ranged' };
    var PREVENTION_TYPE = { 0: 'None', 1: 'Silence', 2: 'Pacify' };
    var SPELL_FAMILY = { 0: 'Generic', 1: 'Events', 3: 'Mage', 4: 'Warrior', 5: 'Warlock', 6: 'Priest', 7: 'Druid', 8: 'Rogue', 9: 'Hunter', 10: 'Paladin', 11: 'Shaman', 13: 'Potion', 15: 'Death Knight' };

    var EFFECT_NAMES = {
        0: 'None', 2: 'School Damage', 3: 'Dummy', 4: 'Portal Teleport', 5: 'Teleport Units', 6: 'Apply Aura', 7: 'Env Damage',
        8: 'Power Drain', 9: 'Health Leech', 10: 'Heal', 11: 'Bind', 16: 'Quest Complete', 17: 'Weapon Damage',
        18: 'Resurrect', 19: 'Add Extra Attacks', 21: 'Parry', 22: 'Defense', 23: 'Persist Area Aura',
        24: 'Create Item', 25: 'Weapon', 27: 'Normalize Weapon', 28: 'Summon', 30: 'Energize',
        33: 'Open Lock', 35: 'Apply Area Aura Party', 36: 'Learn Spell', 38: 'Dispel',
        44: 'Skill Step', 53: 'Enchant Item Perm', 54: 'Enchant Item Temp', 56: 'Summon Pet',
        58: 'Weapon Damage +', 62: 'Power Burn', 64: 'Trigger Spell', 67: 'Heal Max Health',
        68: 'Interrupt Cast', 74: 'Apply Glyph', 77: 'Script Effect', 78: 'Sanctuary',
        80: 'Add Combo Points', 87: 'Summon Totem', 94: 'Self Resurrect', 96: 'Charge',
        98: 'Knock Back', 101: 'Feed Pet', 104: 'Dismiss Pet', 113: 'Resurrect Flat',
        118: 'Apply Area Aura Raid', 119: 'Apply Area Aura Pet', 121: 'Normalized Weapon Dmg',
        135: 'Apply Area Aura Friend', 136: 'Apply Area Aura Enemy', 140: 'Force Cast'
    };

    var AURA_NAMES = {
        0: 'None', 3: 'Periodic Damage', 4: 'Dummy', 5: 'Confuse', 6: 'Charm', 7: 'Fear',
        8: 'Periodic Heal', 10: 'Mod Threat', 12: 'Stun', 13: 'Mod Damage Done',
        15: 'Mod Damage Taken', 16: 'Damage Shield', 17: 'Mod Stealth', 18: 'Mod Stealth Detect',
        19: 'Mod Invisibility', 22: 'Mod Resistance', 23: 'Periodic Trigger Spell',
        24: 'Periodic Energize', 26: 'Mod Pacify', 27: 'Mod Root', 28: 'Mod Silence',
        29: 'Reflect Spells', 31: 'Mod Speed', 33: 'Mod Speed Slow', 34: 'Mod Increase Health',
        35: 'Mod Increase Energy', 36: 'Mod Shapeshift', 37: 'Effect Immunity',
        42: 'Proc Trigger Spell', 43: 'Proc Trigger Damage', 44: 'Track Creatures',
        45: 'Track Resources', 48: 'Mod Parry %', 49: 'Periodic Triggered',
        53: 'Mod Increase Speed', 55: 'Mod Increase Swim Speed', 56: 'Mod Damage Done Creature',
        60: 'Mod Pacify+Silence', 61: 'Mod Scale', 65: 'Split Damage PCT',
        69: 'Mod Speed Not Stack', 79: 'Mod Damage Done %', 85: 'Mod Power Regen',
        87: 'Mod Damage % Taken', 99: 'Mod Attack Power', 101: 'Mod Melee Haste',
        103: 'Mod Total Threat', 107: 'Mod Total Stat %', 108: 'Mod Melee Haste',
        112: 'Mod Ranged Haste', 135: 'Mod Healing Done', 136: 'Mod Healing Done %',
        137: 'Mod Total Stat % SP', 142: 'Mod Base Resistance %', 166: 'Mod AP By Armor',
        189: 'Mod Rating', 219: 'Periodic Trigger Spell With Value'
    };

    var TARGET_NAMES = {
        0: 'None', 1: 'Self', 5: 'Pet', 6: 'Enemy', 15: 'Enemy AOE (src)', 16: 'Enemy AOE (dest)',
        20: 'Party in Range', 21: 'Friendly', 22: 'Caster Position', 23: 'Game Object',
        24: 'Cone (Front)', 25: 'Any Unit', 28: 'AOE (dynobj)', 30: 'Friendly AOE (src)',
        31: 'Friendly AOE (dest)', 33: 'Party AOE (src)', 35: 'Party Unit', 36: 'Enemy in Range',
        37: 'Party+Friendly', 38: 'Script NPC Near', 45: 'Chain Heal', 53: 'Target Position',
        56: 'Raid in Range', 57: 'Raid Unit', 61: 'Raid+Class', 77: 'Channel Target'
    };

    // ===================== FIELD GROUPS =====================
    // Defines the editor layout. Each group becomes a collapsible section.
    // type: int, float, text, enum, dbc, readonly, bitmask
    // dep: { field, value } — only show this field when dep.field equals dep.value (or is in dep.values)

    var FIELD_GROUPS = [
        {
            key: 'tuning', title: 'Gameplay Tuning', icon: 'fa-sliders', open: true, fields: [
                { col: 'manaCost', label: 'Mana/Resource Cost', type: 'int' },
                { col: 'powerType', label: 'Power Type', type: 'enum', options: POWER_TYPES },
                { col: 'manaCostPercentage', label: 'Cost as % of Pool', type: 'int' },
                { col: 'spellLevel', label: 'Spell Level', type: 'int' },
                { col: 'baseLevel', label: 'Base Level', type: 'int' },
                { col: 'maxLevel', label: 'Max Level', type: 'int' },
                { col: 'procChance', label: 'Proc Chance %', type: 'int' },
                { col: 'procCharges', label: 'Proc Charges', type: 'int' },
                { col: 'recoveryTime', label: 'Cooldown (ms)', type: 'int' },
                { col: 'categoryRecoveryTime', label: 'Category Cooldown (ms)', type: 'int' },
                { col: 'startRecoveryTime', label: 'GCD (ms)', type: 'int' },
                { col: 'speed', label: 'Projectile Speed', type: 'float' },
                { col: 'stackAmount', label: 'Stack Amount', type: 'int' },
                { col: 'maxAffectedTargets', label: 'Max Targets', type: 'int' }
            ]
        },
        {
            key: 'identity', title: 'Identity', icon: 'fa-tag', fields: [
                { col: 'entry', label: 'Spell ID', type: 'readonly' },
                { col: 'build', label: 'Build', type: 'readonly' },
                { col: 'name', label: 'Name', type: 'text' },
                { col: 'nameSubtext', label: 'Rank / Subtext', type: 'text' },
                { col: 'school', label: 'School', type: 'enum', options: SCHOOL_NAMES },
                { col: 'category', label: 'Category', type: 'int' },
                { col: 'dispel', label: 'Dispel Type', type: 'enum', options: DISPEL_TYPES },
                { col: 'mechanic', label: 'Mechanic', type: 'enum', options: MECHANIC_NAMES },
                { col: 'spellIconId', label: 'Icon ID', type: 'int' },
                { col: 'spellVisual', label: 'Visual ID', type: 'int' },
                { col: 'activeIconId', label: 'Active Icon ID', type: 'int' },
                { col: 'spellPriority', label: 'Priority', type: 'int' }
            ]
        },
        {
            key: 'timing', title: 'Timing & Range', icon: 'fa-clock', fields: [
                { col: 'castingTimeIndex', label: 'Cast Time', type: 'dbc', dbcKey: 'castTimes' },
                { col: 'durationIndex', label: 'Duration', type: 'dbc', dbcKey: 'durations' },
                { col: 'rangeIndex', label: 'Range', type: 'dbc', dbcKey: 'ranges' },
                { col: 'startRecoveryCategory', label: 'GCD Category', type: 'int' },
                { col: 'maxTargetLevel', label: 'Max Target Level', type: 'int' }
            ]
        },
        {
            key: 'cost', title: 'Additional Costs', icon: 'fa-coins', fields: [
                { col: 'manaCostPerlevel', label: 'Mana Cost Per Level', type: 'int' },
                { col: 'manaPerSecond', label: 'Mana Per Second', type: 'int' },
                { col: 'manaPerSecondPerLevel', label: 'Mana/Sec Per Level', type: 'int' },
                { col: 'reagent1', label: 'Reagent 1 (Item ID)', type: 'int' },
                { col: 'reagentCount1', label: 'Reagent 1 Count', type: 'int' },
                { col: 'reagent2', label: 'Reagent 2 (Item ID)', type: 'int' },
                { col: 'reagentCount2', label: 'Reagent 2 Count', type: 'int' },
                { col: 'totem1', label: 'Totem 1 (Item ID)', type: 'int' },
                { col: 'totem2', label: 'Totem 2 (Item ID)', type: 'int' },
                { col: 'totemCategory1', label: 'Totem Category 1', type: 'int' },
                { col: 'totemCategory2', label: 'Totem Category 2', type: 'int' }
            ]
        },
        {
            key: 'equip', title: 'Equipment Requirements', icon: 'fa-shield-halved', fields: [
                { col: 'equippedItemClass', label: 'Required Item Class', type: 'int', note: '-1 = none, 2 = weapon, 4 = armor' },
                { col: 'equippedItemSubClassMask', label: 'Item Subclass Mask', type: 'int' },
                { col: 'equippedItemInventoryTypeMask', label: 'Inventory Type Mask', type: 'int' }
            ]
        },
        {
            key: 'targeting', title: 'Targeting & Stances', icon: 'fa-crosshairs', fields: [
                { col: 'targets', label: 'Target Flags', type: 'int' },
                { col: 'targetCreatureType', label: 'Target Creature Type Mask', type: 'int' },
                { col: 'facingCasterFlags', label: 'Facing Flags', type: 'int' },
                { col: 'requiresSpellFocus', label: 'Required Spell Focus GO', type: 'int' },
                { col: 'stances', label: 'Required Stances', type: 'int' },
                { col: 'stancesNot', label: 'Excluded Stances', type: 'int' },
                { col: 'casterAuraState', label: 'Caster Aura State', type: 'int' },
                { col: 'targetAuraState', label: 'Target Aura State', type: 'int' },
                { col: 'casterAuraStateNot', label: 'Caster Aura State Not', type: 'int' },
                { col: 'targetAuraStateNot', label: 'Target Aura State Not', type: 'int' }
            ]
        },
        {
            key: 'proc', title: 'Proc System', icon: 'fa-bolt', fields: [
                { col: 'procFlags', label: 'Proc Flags', type: 'int', note: 'Bitmask: see SharedDefines.h' }
            ]
        },
        {
            key: 'classification', title: 'Classification', icon: 'fa-layer-group', fields: [
                { col: 'spellFamilyName', label: 'Spell Family', type: 'enum', options: SPELL_FAMILY },
                { col: 'spellFamilyFlags', label: 'Spell Family Flags', type: 'int' },
                { col: 'dmgClass', label: 'Damage Class', type: 'enum', options: DMG_CLASS },
                { col: 'preventionType', label: 'Prevention Type', type: 'enum', options: PREVENTION_TYPE },
                { col: 'areaId', label: 'Area ID', type: 'int' }
            ]
        },
        {
            key: 'interrupts', title: 'Interrupt Flags', icon: 'fa-hand', fields: [
                { col: 'interruptFlags', label: 'Interrupt Flags', type: 'int' },
                { col: 'auraInterruptFlags', label: 'Aura Interrupt Flags', type: 'int' },
                { col: 'channelInterruptFlags', label: 'Channel Interrupt Flags', type: 'int' }
            ]
        },
        {
            key: 'attributes', title: 'Attributes (Advanced)', icon: 'fa-cogs', fields: [
                { col: 'attributes', label: 'Attributes', type: 'int', note: 'Bitmask — SharedDefines.h' },
                { col: 'attributesEx', label: 'AttributesEx', type: 'int' },
                { col: 'attributesEx2', label: 'AttributesEx2', type: 'int' },
                { col: 'attributesEx3', label: 'AttributesEx3', type: 'int' },
                { col: 'attributesEx4', label: 'AttributesEx4', type: 'int' }
            ]
        }
    ];

    // Effect group — generated for each of the 3 effect slots
    function buildEffectGroup(n) {
        return {
            key: 'effect' + n, title: 'Effect ' + n, icon: 'fa-wand-sparkles', effectSlot: n,
            fields: [
                { col: 'effect' + n, label: 'Effect Type', type: 'enum', options: EFFECT_NAMES },
                { col: 'effectBasePoints' + n, label: 'Base Points', type: 'int' },
                { col: 'effectDieSides' + n, label: 'Die Sides', type: 'int' },
                { col: 'effectBaseDice' + n, label: 'Base Dice', type: 'int' },
                { col: 'effectDicePerLevel' + n, label: 'Dice Per Level', type: 'float' },
                { col: 'effectRealPointsPerLevel' + n, label: 'Points Per Level', type: 'float' },
                { col: 'effectPointsPerComboPoint' + n, label: 'Points Per Combo', type: 'float' },
                {
                    col: 'effectApplyAuraName' + n, label: 'Aura Type', type: 'enum', options: AURA_NAMES,
                    dep: { field: 'effect' + n, values: [6, 135] }
                },
                {
                    col: 'effectAmplitude' + n, label: 'Tick Interval (ms)', type: 'int',
                    dep: { field: 'effect' + n, values: [6, 135] }
                },
                {
                    col: 'effectTriggerSpell' + n, label: 'Trigger Spell ID', type: 'int',
                    dep: { field: 'effect' + n, values: [64, 36, 6, 135] }
                },
                { col: 'effectMiscValue' + n, label: 'Misc Value', type: 'int' },
                { col: 'effectMiscValueB' + n, label: 'Misc Value B', type: 'int' },
                { col: 'effectImplicitTargetA' + n, label: 'Target A', type: 'enum', options: TARGET_NAMES },
                { col: 'effectImplicitTargetB' + n, label: 'Target B', type: 'enum', options: TARGET_NAMES },
                { col: 'effectRadiusIndex' + n, label: 'Radius Index', type: 'int' },
                { col: 'effectChainTarget' + n, label: 'Chain Targets', type: 'int' },
                {
                    col: 'effectItemType' + n, label: 'Item Type (Create Item)', type: 'int',
                    dep: { field: 'effect' + n, values: [24] }
                },
                { col: 'effectMechanic' + n, label: 'Effect Mechanic', type: 'enum', options: MECHANIC_NAMES },
                { col: 'effectMultipleValue' + n, label: 'Multiple Value', type: 'float' },
                { col: 'dmgMultiplier' + n, label: 'Damage Multiplier', type: 'float' }
            ]
        };
    }

    // Insert effect groups after 'proc' section
    var procIdx = FIELD_GROUPS.findIndex(function (g) { return g.key === 'proc'; });
    FIELD_GROUPS.splice(procIdx + 1, 0, buildEffectGroup(1), buildEffectGroup(2), buildEffectGroup(3));

    // ===================== BASELINE =====================

    // ===================== BASELINE =====================

    BaselineSystem.checkStatus(function (status) {
        BaselineSystem.renderWarningBanner('#baselineWarning');

        // Check specifically for og_spell_template
        if (status.initialized) {
            var hasSpellBaseline = false;
            (status.tables || []).forEach(function (t) {
                if (t.tableName === 'og_spell_template') hasSpellBaseline = true;
            });

            if (!hasSpellBaseline) {
                $('#baselineWarning').html(
                    '<div class="baseline-warning">' +
                    '<div class="baseline-warning-icon"><i class="fa-solid fa-triangle-exclamation"></i></div>' +
                    '<div class="baseline-warning-body">' +
                    '<div class="baseline-warning-title">Spell Baseline Missing</div>' +
                    '<div class="baseline-warning-text">' +
                    'The spell table snapshot has not been created yet. Re-run initialization to add it — existing baselines will be skipped.' +
                    '</div>' +
                    '<button class="baseline-init-btn" id="btnInitBaseline">' +
                    '<i class="fa-solid fa-database"></i> Initialize Spell Baseline' +
                    '</button>' +
                    '<div id="baselineProgress" style="display:none; margin-top:10px;"></div>' +
                    '</div></div>'
                ).show();
            }
        }
    });

    // ===================== INIT =====================

    // Load DBC metadata for dropdowns
    $.getJSON('/Spells/DbcMeta', function (data) { dbcMeta = data; });

    doSearch(1);

    // ===================== SEARCH =====================

    function doSearch(page) {
        if (viewMode === 'grouped') {
            doSearchGrouped(page);
        } else {
            doSearchFlat(page);
        }
    }

    function doSearchFlat(page) {
        currentPage = page || 1;
        var params = {
            q: $('#spellSearch').val(),
            schoolFilter: $('#filterSchool').val() || undefined,
            mechanicFilter: $('#filterMechanic').val() || undefined,
            page: currentPage,
            pageSize: currentPageSize
        };

        Object.keys(params).forEach(function (k) { if (params[k] === undefined || params[k] === '') delete params[k]; });

        $('#spellListContainer').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i> Searching...</div>');

        $.getJSON('/Spells/Search', params, function (data) {
            currentIcons = data.icons || {};
            totalPages = data.totalPages;
            $('#totalSpellCount').text(data.totalCount.toLocaleString());
            $('#spellResultInfo').text('Showing ' + data.spells.length + ' of ' + data.totalCount.toLocaleString());

            if (data.spells.length === 0) {
                $('#spellListContainer').html('<div class="text-center p-4 text-muted">No spells found</div>');
                $('#spellPaginationBar').hide();
                return;
            }

            var html = '';
            data.spells.forEach(function (spell) {
                var iconPath = currentIcons[spell.spellIconId] || '/icons/inv_misc_questionmark.png';
                var schoolName = SCHOOL_NAMES[spell.school] || '';
                var rank = spell.nameSubtext || '';
                var meta = [schoolName, spell.spellLevel > 0 ? 'Level ' + spell.spellLevel : ''].filter(Boolean).join(' · ');

                html += '<div class="spell-row" data-entry="' + spell.entry + '">' +
                    '<img class="spell-icon" src="' + esc(iconPath) + '" alt="" loading="lazy" />' +
                    '<div style="flex: 1; min-width: 0;">' +
                    '<div class="spell-name">' + esc(spell.name) +
                    (rank ? ' <span class="spell-rank">' + esc(rank) + '</span>' : '') + '</div>' +
                    '<div class="spell-meta">' + esc(meta) + '</div>' +
                    '</div>' +
                    '<span class="school-badge school-' + (spell.school || 0) + '">' + esc(schoolName) + '</span>' +
                    '<div class="spell-entry">#' + spell.entry + '</div>' +
                    '</div>';
            });

            $('#spellListContainer').html(html);
            renderPagination(data.page, data.totalPages);
        }).fail(function () {
            $('#spellListContainer').html('<div class="text-center p-4 text-muted">Search failed</div>');
        });
    }

    function doSearchGrouped(page) {
        currentPage = page || 1;
        var params = {
            q: $('#spellSearch').val(),
            schoolFilter: $('#filterSchool').val() || undefined,
            mechanicFilter: $('#filterMechanic').val() || undefined,
            page: currentPage,
            pageSize: currentPageSize
        };

        Object.keys(params).forEach(function (k) { if (params[k] === undefined || params[k] === '') delete params[k]; });

        $('#spellListContainer').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i> Searching...</div>');

        $.getJSON('/Spells/SearchGrouped', params, function (data) {
            currentIcons = data.icons || {};
            totalPages = data.totalPages;
            $('#totalSpellCount').text(data.totalSpells.toLocaleString());
            $('#spellResultInfo').text(data.totalGroups.toLocaleString() + ' groups');

            if (data.groups.length === 0) {
                $('#spellListContainer').html('<div class="text-center p-4 text-muted">No spells found</div>');
                $('#spellPaginationBar').hide();
                return;
            }

            var html = '';
            data.groups.forEach(function (g) {
                var iconPath = currentIcons[g.spellIconId] || '/icons/inv_misc_questionmark.png';
                var schoolName = SCHOOL_NAMES[g.school] || '';
                var familyName = SPELL_FAMILY[g.family] || '';
                var isSingle = g.rankCount === 1;

                if (isSingle) {
                    // Single-rank spell — render as flat row, click loads editor directly
                    html += '<div class="spell-row" data-entry="' + g.firstEntry + '">' +
                        '<img class="spell-icon" src="' + esc(iconPath) + '" alt="" loading="lazy" />' +
                        '<div style="flex: 1; min-width: 0;">' +
                        '<div class="spell-name">' + esc(g.name) + '</div>' +
                        '<div class="spell-meta">' + esc(schoolName) + (g.levelRange !== '0' ? ' · Level ' + g.levelRange : '') + '</div>' +
                        '</div>' +
                        '<span class="school-badge school-' + g.school + '">' + esc(schoolName) + '</span>' +
                        '<div class="spell-entry">#' + g.firstEntry + '</div>' +
                        '</div>';
                } else {
                    // Multi-rank — collapsible group
                    var entriesAttr = escAttr(JSON.stringify(g.entries));
                    html += '<div class="spell-group" data-entries=\'' + entriesAttr + '\'>' +
                        '<div class="spell-group-header">' +
                        '<i class="fa-solid fa-chevron-right group-chevron"></i>' +
                        '<img class="spell-icon" src="' + esc(iconPath) + '" alt="" loading="lazy" />' +
                        '<div style="flex: 1; min-width: 0;">' +
                        '<div class="spell-name">' + esc(g.name) +
                        (familyName && familyName !== 'Generic' ? ' <span class="spell-rank">' + esc(familyName) + '</span>' : '') +
                        '</div>' +
                        '<div class="spell-meta">' + esc(schoolName) +
                        (g.levelRange !== '0' ? ' · Level ' + g.levelRange : '') + '</div>' +
                        '</div>' +
                        '<span class="rank-count">' + g.rankCount + ' ranks</span>' +
                        '<span class="school-badge school-' + g.school + '">' + esc(schoolName) + '</span>' +
                        '</div>' +
                        '<div class="spell-group-body" style="display:none;">' +
                        '<div class="spell-group-actions">' +
                        '<button class="btn-batch-edit btn-sm btn-outline-subtle" data-entries=\'' + entriesAttr + '\'>' +
                        '<i class="fa-solid fa-pen-ruler"></i> Batch Edit Shared Fields' +
                        '</button>' +
                        '</div>' +
                        '<div class="spell-group-ranks">' +
                        '<div class="text-center p-2 text-muted" style="font-size:11px;"><i class="fa-solid fa-spinner fa-spin"></i> Loading ranks...</div>' +
                        '</div>' +
                        '</div>' +
                        '</div>';
                }
            });

            $('#spellListContainer').html(html);
            renderPagination(data.page, data.totalPages);
        }).fail(function () {
            $('#spellListContainer').html('<div class="text-center p-4 text-muted">Search failed</div>');
        });
    }

    function renderPagination(page, pages) {
        if (pages > 1) {
            $('#spellPaginationBar').show();
            $('#spellPageJump').val(page).attr('max', pages);
            $('#spellPageTotal').text(pages);
            $('#btnSpellFirstPage').prop('disabled', page <= 1);
            $('#btnSpellPrevPage').prop('disabled', page <= 1);
            $('#btnSpellNextPage').prop('disabled', page >= pages);
            $('#btnSpellLastPage').prop('disabled', page >= pages);
        } else if (pages === 1) {
            // Single page — still show bar for page size selector access
            $('#spellPaginationBar').show();
            $('#spellPageJump').val(1).attr('max', 1);
            $('#spellPageTotal').text('1');
            $('#btnSpellFirstPage, #btnSpellPrevPage, #btnSpellNextPage, #btnSpellLastPage').prop('disabled', true);
        } else {
            $('#spellPaginationBar').hide();
        }
    }

    // ===================== GROUP EXPAND =====================

    $(document).on('click', '.spell-group-header', function () {
        var $group = $(this).closest('.spell-group');
        var $body = $group.find('.spell-group-body');
        var $chevron = $(this).find('.group-chevron');
        var isOpen = $body.is(':visible');

        if (isOpen) {
            $body.slideUp(200);
            $chevron.removeClass('open');
            return;
        }

        $body.slideDown(200);
        $chevron.addClass('open');

        // Load rank details if not yet loaded
        var $ranks = $group.find('.spell-group-ranks');
        if ($ranks.data('loaded')) return;

        var entries = $group.data('entries');
        if (!entries || !entries.length) return;

        // Fetch basic info for each rank via the existing Search results (already have the data)
        // Or just render minimal rows from the entry list and load detail on click
        var ranksHtml = '';
        entries.forEach(function (entry, idx) {
            ranksHtml += '<div class="spell-row spell-rank-row" data-entry="' + entry + '">' +
                '<div style="width:18px;text-align:center;color:var(--text-muted);font-size:10px;font-weight:700;">' + (idx + 1) + '</div>' +
                '<div style="flex:1;font-size:12px;">Rank ' + (idx + 1) + '</div>' +
                '<div class="spell-entry">#' + entry + '</div>' +
                '</div>';
        });

        $ranks.html(ranksHtml);
        $ranks.data('loaded', true);
    });

    // ===================== BATCH EDIT =====================

    $(document).on('click', '.btn-batch-edit', function (e) {
        e.stopPropagation();
        var entries = $(this).data('entries');
        if (!entries || !entries.length) return;

        loadBatchEditor(entries);
    });

    function loadBatchEditor(entries) {
        editedFields = {};
        updateSaveBar();
        $('#spellEditorContent').html('<div class="text-center p-3"><i class="fa-solid fa-spinner fa-spin"></i> Analyzing ' + entries.length + ' ranks...</div>');

        $.ajax({
            url: '/Spells/GroupDetail',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(entries),
            success: function (data) {
                if (!data.found) {
                    $('#spellEditorContent').html('<div class="text-center text-muted p-3">No spells found</div>');
                    return;
                }
                currentSpell = null; // clear single-spell state
                renderBatchEditor(data, entries);
            },
            error: function () {
                $('#spellEditorContent').html('<div class="text-center text-muted p-3">Failed to load group</div>');
            }
        });
    }

    function renderBatchEditor(data, entries) {
        var spells = data.spells;
        var sharedSet = {};
        data.sharedFields.forEach(function (f) { sharedSet[f] = true; });

        var firstName = spells[0].name || 'Unknown';

        // Header
        var html = '<div class="editor-header">' +
            '<div style="flex:1;">' +
            '<div style="font-size:15px;font-weight:600;">' +
            '<i class="fa-solid fa-layer-group" style="color:var(--accent);margin-right:6px;"></i>' +
            esc(firstName) +
            '</div>' +
            '<div style="font-size:12px;color:var(--text-muted);">' +
            'Batch editing ' + spells.length + ' ranks — changes apply to all ranks' +
            '</div>' +
            '</div>' +
            '</div>';

        html += '<div class="restart-notice">' +
            '<i class="fa-solid fa-rotate-right"></i> Changes require a server restart to take effect' +
            '</div>';

        // Shared fields section — these are editable, one value applies to all
        html += '<div class="editor-section" data-group="batch-shared">' +
            '<div class="editor-section-header open" onclick="$(this).toggleClass(\'open\');$(this).next().slideToggle(200);">' +
            '<span><i class="fa-solid fa-link" style="color:var(--accent);margin-right:6px;"></i> Shared Fields <span class="rank-count">' + data.sharedFields.length + ' identical across all ranks</span></span>' +
            '<i class="fa-solid fa-chevron-right section-chevron"></i>' +
            '</div>' +
            '<div class="editor-section-body">';

        // Shared fields — organized by the same FIELD_GROUPS sections as single editor
        FIELD_GROUPS.forEach(function (group) {
            // Collect shared fields in this group
            var groupSharedFields = [];
            group.fields.forEach(function (field) {
                if (sharedSet[field.col] && field.type !== 'readonly') {
                    groupSharedFields.push(field);
                }
            });

            if (groupSharedFields.length === 0) return; // skip empty groups

            html += '<div class="editor-section" data-group="batch-' + group.key + '">' +
                '<div class="editor-section-header" onclick="$(this).toggleClass(\'open\');$(this).next().slideToggle(200);">' +
                '<span><i class="fa-solid ' + group.icon + '"></i> ' + esc(group.title) +
                ' <span class="rank-count">' + groupSharedFields.length + ' shared</span></span>' +
                '<i class="fa-solid fa-chevron-right section-chevron"></i>' +
                '</div>' +
                '<div class="editor-section-body" style="display:none;">';

            groupSharedFields.forEach(function (field) {
                var val = data.sharedValues[field.col];
                if (val === null || val === undefined) val = '';

                html += '<div class="editor-field" data-col="' + field.col + '">';
                html += '<label class="editor-label">' + esc(field.label) + '</label>';

                if (field.type === 'enum') {
                    html += '<select class="form-input editor-input batch-input" data-col="' + field.col + '" data-orig="' + escAttr(String(val)) + '">';
                    var opts = field.options || {};
                    var found = false;
                    for (var k in opts) {
                        var sel = String(k) === String(val) ? ' selected' : '';
                        if (sel) found = true;
                        html += '<option value="' + esc(k) + '"' + sel + '>' + esc(k) + ' — ' + esc(opts[k]) + '</option>';
                    }
                    if (!found) html += '<option value="' + esc(String(val)) + '" selected>' + esc(String(val)) + '</option>';
                    html += '</select>';
                } else if (field.type === 'dbc') {
                    html += '<select class="form-input editor-input batch-input" data-col="' + field.col + '" data-orig="' + escAttr(String(val)) + '">';
                    var items = dbcMeta ? (dbcMeta[field.dbcKey] || []) : [];
                    var foundDbc = false;
                    items.forEach(function (item) {
                        var sel = String(item.id) === String(val) ? ' selected' : '';
                        if (sel) foundDbc = true;
                        html += '<option value="' + item.id + '"' + sel + '>' + item.id + ' — ' + esc(item.label) + '</option>';
                    });
                    if (!foundDbc) html += '<option value="' + esc(String(val)) + '" selected>' + esc(String(val)) + '</option>';
                    html += '</select>';
                } else if (field.type === 'text') {
                    html += '<input type="text" class="form-input editor-input batch-input" data-col="' + field.col + '" data-orig="' + escAttr(String(val)) + '" value="' + escAttr(String(val)) + '" />';
                } else {
                    html += '<input type="number" class="form-input editor-input batch-input" data-col="' + field.col + '" data-orig="' + escAttr(String(val)) + '" value="' + escAttr(String(val)) + '" ' +
                        (field.type === 'float' ? 'step="any"' : 'step="1"') + ' />';
                }
                html += '</div>';
            });

            html += '</div></div>';
        });

        // Per-rank comparison table — read-only reference showing what differs
        html += '<div class="editor-section" data-group="batch-perrank">' +
            '<div class="editor-section-header" onclick="$(this).toggleClass(\'open\');$(this).next().slideToggle(200);">' +
            '<span><i class="fa-solid fa-table-list" style="color:var(--accent);margin-right:6px;"></i> Per-Rank Values <span class="rank-count">' + data.perRankFields.length + ' fields differ</span></span>' +
            '<i class="fa-solid fa-chevron-right section-chevron"></i>' +
            '</div>' +
            '<div class="editor-section-body" style="display:none;overflow-x:auto;">';

        // Only show interesting per-rank fields (ones in our FIELD_GROUPS metadata)
        var interestingPerRank = [];
        FIELD_GROUPS.forEach(function (group) {
            group.fields.forEach(function (field) {
                if (data.perRankFields.indexOf(field.col) !== -1) {
                    interestingPerRank.push(field);
                }
            });
        });

        if (interestingPerRank.length > 0 && spells.length <= 20) {
            html += '<table class="perrank-table"><thead><tr><th>Field</th>';
            spells.forEach(function (s, idx) {
                html += '<th title="#' + s.entry + '">R' + (idx + 1) + '</th>';
            });
            html += '</tr></thead><tbody>';

            interestingPerRank.forEach(function (field) {
                html += '<tr><td class="perrank-label">' + esc(field.label) + '</td>';
                spells.forEach(function (s) {
                    var v = s[field.col];
                    html += '<td class="perrank-val">' + (v !== null && v !== undefined ? esc(String(v)) : '—') + '</td>';
                });
                html += '</tr>';
            });

            html += '</tbody></table>';
        } else if (spells.length > 20) {
            html += '<div class="text-muted" style="font-size:12px;padding:8px;">Too many ranks to display comparison table.</div>';
        }

        html += '</div></div>';

        // Store entries for batch save
        $('#spellEditorContent').html(html).data('batchEntries', entries);
    }

    // Batch save handler — reuse change tracking but send to SaveBatch
    $(document).on('input change', '.batch-input', function () {
        var col = $(this).data('col');
        var orig = $(this).data('orig');
        var val = $(this).val();

        if (String(val) !== String(orig)) {
            editedFields[col] = isNaN(Number(val)) || val === '' ? val : Number(val);
            $(this).addClass('field-changed');
        } else {
            delete editedFields[col];
            $(this).removeClass('field-changed');
        }
        updateSaveBar();
    });

    // ===================== DETAIL / EDITOR =====================

    function loadDetail(entry) {
        editedFields = {};
        updateSaveBar();
        $('#spellEditorContent').html('<div class="text-center p-3"><i class="fa-solid fa-spinner fa-spin"></i></div>');

        $.getJSON('/Spells/Detail', { entry: entry }, function (data) {
            if (!data.found) {
                $('#spellEditorContent').html('<div class="text-center text-muted p-3">Spell not found</div>');
                return;
            }

            currentSpell = data;
            renderEditor(data);
        });
    }

    function renderEditor(data) {
        var spell = data.spell;
        var schoolName = SCHOOL_NAMES[spell.school] || 'Unknown';

        // Header
        var html = '<div class="editor-header">' +
            '<img class="detail-icon" src="' + esc(data.iconPath) + '" />' +
            '<div style="flex:1;min-width:0;">' +
            '<div style="font-size: 15px; font-weight: 600;">' + esc(spell.name) + '</div>' +
            '<div style="font-size: 12px; color: var(--text-muted);">' +
            (spell.nameSubtext ? esc(spell.nameSubtext) + ' · ' : '') +
            '#' + spell.entry + ' · Build ' + spell.build +
            '</div>' +
            '</div>' +
            '<span class="school-badge school-' + (spell.school || 0) + '">' + esc(schoolName) + '</span>' +
            '</div>';

        // Restart notice
        html += '<div class="restart-notice">' +
            '<i class="fa-solid fa-rotate-right"></i> Changes require a server restart to take effect' +
            '</div>';

        // OG diff banner
        if (data.ogDiff) {
            var diffCount = Object.keys(data.ogDiff).length;
            html += '<div class="og-diff-banner" id="ogDiffBanner">' +
                '<div class="og-diff-header" onclick="$(\'#ogDiffBody\').slideToggle(200)">' +
                '<span><i class="fa-solid fa-clock-rotate-left"></i> ' + diffCount + ' field(s) changed from original</span>' +
                '<i class="fa-solid fa-chevron-down" style="font-size:10px;"></i>' +
                '</div>' +
                '<div id="ogDiffBody" style="display:none;">';
            for (var key in data.ogDiff) {
                var d = data.ogDiff[key];
                html += '<div class="og-diff-row">' +
                    '<span class="og-diff-col">' + esc(key) + '</span>' +
                    '<span class="og-diff-val og-val">' + esc(String(d.og)) + '</span>' +
                    '<span style="color:var(--text-muted);font-size:10px;">→</span>' +
                    '<span class="og-diff-val cur-val">' + esc(String(d.cur)) + '</span>' +
                    '</div>';
            }
            html += '</div>' +
                '<div style="padding:8px 16px;border-top:1px solid var(--border-light);">' +
                '<button class="btn-sm btn-outline-subtle" id="btnResetSpell" style="border-color:var(--status-warning);color:var(--status-warning);font-size:11px;">' +
                '<i class="fa-solid fa-rotate-left"></i> Reset to Original' +
                '</button>' +
                '</div>' +
                '</div>';
        }

        // DBC quick info
        html += '<div class="dbc-info-row">' +
            '<span title="Cast Time"><i class="fa-solid fa-hourglass-half"></i> ' + esc(data.castTimeLabel) + '</span>' +
            '<span title="Duration"><i class="fa-solid fa-clock"></i> ' + esc(data.durationLabel) + '</span>' +
            '<span title="Range"><i class="fa-solid fa-arrows-left-right"></i> ' + esc(data.rangeLabel) + '</span>' +
            '</div>';

        // Effect summary badges
        var effectSummary = '';
        for (var i = 1; i <= 3; i++) {
            var effVal = spell['effect' + i];
            if (effVal > 0) {
                var effName = EFFECT_NAMES[effVal] || 'Effect ' + effVal;
                var auraVal = spell['effectApplyAuraName' + i];
                var auraStr = (effVal === 6 || effVal === 135) && auraVal > 0 ? ' → ' + (AURA_NAMES[auraVal] || 'Aura ' + auraVal) : '';
                effectSummary += '<span class="effect-summary-badge">E' + i + ': ' + esc(effName) + esc(auraStr) + '</span>';
            }
        }
        if (effectSummary) {
            html += '<div class="effect-summary-row">' + effectSummary + '</div>';
        }

        // Field groups
        FIELD_GROUPS.forEach(function (group) {
            var isOpen = group.open || false;
            var isEffectSlot = group.effectSlot !== undefined;
            var effectVal = isEffectSlot ? (spell['effect' + group.effectSlot] || 0) : 0;
            var isDimmed = isEffectSlot && effectVal === 0;

            html += '<div class="editor-section' + (isDimmed ? ' dimmed' : '') + '" data-group="' + group.key + '">' +
                '<div class="editor-section-header' + (isOpen ? ' open' : '') + '" onclick="$(this).toggleClass(\'open\');$(this).next().slideToggle(200);">' +
                '<span><i class="fa-solid ' + group.icon + '"></i> ' + esc(group.title);

            // Show effect type label in section header
            if (isEffectSlot && effectVal > 0) {
                html += ' <span class="effect-type-tag">' + esc(EFFECT_NAMES[effectVal] || String(effectVal)) + '</span>';
            } else if (isEffectSlot) {
                html += ' <span class="effect-type-tag inactive">Unused</span>';
            }

            html += '</span><i class="fa-solid fa-chevron-right section-chevron"></i></div>';
            html += '<div class="editor-section-body"' + (isOpen ? '' : ' style="display:none;"') + '>';

            group.fields.forEach(function (field) {
                if (spell[field.col] === undefined && field.type !== 'readonly') return; // column doesn't exist in this DB

                var val = spell[field.col];
                if (val === null || val === undefined) val = '';

                // Dependency check — dim if parent not matched
                var depDimmed = false;
                if (field.dep) {
                    var parentVal = spell[field.dep.field] || 0;
                    if (field.dep.values && field.dep.values.indexOf(parentVal) === -1) depDimmed = true;
                }

                html += '<div class="editor-field' + (depDimmed ? ' dep-dimmed' : '') + '" data-col="' + field.col + '">';
                html += '<label class="editor-label">' + esc(field.label);
                if (field.note) html += ' <span class="field-note" title="' + escAttr(field.note) + '"><i class="fa-solid fa-circle-info"></i></span>';
                html += '</label>';

                if (field.type === 'readonly') {
                    html += '<span class="editor-readonly">' + esc(String(val)) + '</span>';
                } else if (field.type === 'enum') {
                    html += '<select class="form-input editor-input" data-col="' + field.col + '" data-orig="' + escAttr(String(val)) + '">';
                    var opts = field.options || {};
                    var found = false;
                    for (var k in opts) {
                        var sel = String(k) === String(val) ? ' selected' : '';
                        if (sel) found = true;
                        html += '<option value="' + esc(k) + '"' + sel + '>' + esc(k) + ' — ' + esc(opts[k]) + '</option>';
                    }
                    if (!found) html += '<option value="' + esc(String(val)) + '" selected>' + esc(String(val)) + ' — (unknown)</option>';
                    html += '</select>';
                } else if (field.type === 'dbc') {
                    html += '<select class="form-input editor-input" data-col="' + field.col + '" data-orig="' + escAttr(String(val)) + '">';
                    var items = dbcMeta ? (dbcMeta[field.dbcKey] || []) : [];
                    var foundDbc = false;
                    items.forEach(function (item) {
                        var sel = String(item.id) === String(val) ? ' selected' : '';
                        if (sel) foundDbc = true;
                        html += '<option value="' + item.id + '"' + sel + '>' + item.id + ' — ' + esc(item.label) + '</option>';
                    });
                    if (!foundDbc) html += '<option value="' + esc(String(val)) + '" selected>' + esc(String(val)) + ' — (unknown)</option>';
                    html += '</select>';
                } else if (field.type === 'text') {
                    html += '<input type="text" class="form-input editor-input" data-col="' + field.col + '" data-orig="' + escAttr(String(val)) + '" value="' + escAttr(String(val)) + '" />';
                } else {
                    // int or float
                    html += '<input type="number" class="form-input editor-input" data-col="' + field.col + '" ' +
                        'data-orig="' + escAttr(String(val)) + '" value="' + escAttr(String(val)) + '" ' +
                        (field.type === 'float' ? 'step="any"' : 'step="1"') + ' />';
                }

                html += '</div>';
            });

            html += '</div></div>';
        });

        $('#spellEditorContent').html(html);
    }

    // ===================== CHANGE TRACKING =====================

    $(document).on('input change', '.editor-input', function () {
        var col = $(this).data('col');
        var orig = $(this).data('orig');
        var val = $(this).val();

        if (String(val) !== String(orig)) {
            editedFields[col] = isNaN(Number(val)) || val === '' ? val : Number(val);
            $(this).addClass('field-changed');
        } else {
            delete editedFields[col];
            $(this).removeClass('field-changed');
        }

        updateSaveBar();

        // Handle effect type change → toggle section dimming
        if (col && col.match(/^effect[123]$/) && currentSpell) {
            var slotNum = col.replace('effect', '');
            var newEffVal = Number(val) || 0;
            var section = $('[data-group="effect' + slotNum + '"]');
            section.toggleClass('dimmed', newEffVal === 0);

            // Update dependency dimming within the section
            section.find('.editor-field').each(function () {
                var fieldCol = $(this).data('col');
                var fieldDef = findFieldDef(fieldCol);
                if (fieldDef && fieldDef.dep) {
                    var parentVal = fieldCol === col ? newEffVal : (Number(editedFields[fieldDef.dep.field]) || currentSpell.spell[fieldDef.dep.field] || 0);
                    $(this).toggleClass('dep-dimmed', fieldDef.dep.values && fieldDef.dep.values.indexOf(parentVal) === -1);
                }
            });
        }
    });

    function findFieldDef(col) {
        for (var g = 0; g < FIELD_GROUPS.length; g++) {
            for (var f = 0; f < FIELD_GROUPS[g].fields.length; f++) {
                if (FIELD_GROUPS[g].fields[f].col === col) return FIELD_GROUPS[g].fields[f];
            }
        }
        return null;
    }

    function updateSaveBar() {
        var count = Object.keys(editedFields).length;
        if (count > 0) {
            $('#saveBar').addClass('visible');
            $('#saveCount').text(count);
        } else {
            $('#saveBar').removeClass('visible');
        }
    }

    // ===================== SAVE =====================

    function saveChanges() {
        if (Object.keys(editedFields).length === 0) return;

        // Check if we're in batch mode
        var batchEntries = $('#spellEditorContent').data('batchEntries');

        if (batchEntries && batchEntries.length > 0) {
            saveBatch(batchEntries);
        } else if (currentSpell) {
            saveSingle();
        }
    }

    function saveSingle() {
        var payload = {
            entry: currentSpell.spell.entry,
            changes: editedFields
        };

        $('#btnSave').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Saving...');

        $.ajax({
            url: '/Spells/Save',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (result) {
                $('#btnSave').prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save');
                if (result.success) {
                    showToast(result.fieldsUpdated + ' field(s) saved. Restart required.', 'success');
                    loadDetail(currentSpell.spell.entry);
                } else {
                    showToast('Save failed: ' + (result.error || 'Unknown error'), 'error');
                }
            },
            error: function () {
                $('#btnSave').prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save');
                showToast('Save failed — server error', 'error');
            }
        });
    }

    function saveBatch(entries) {
        var payload = {
            entries: entries,
            changes: editedFields
        };

        $('#btnSave').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Saving ' + entries.length + ' spells...');

        $.ajax({
            url: '/Spells/SaveBatch',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (result) {
                $('#btnSave').prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save');
                if (result.success) {
                    showToast(result.totalUpdated + ' spell(s) updated. Restart required.', 'success');
                    editedFields = {};
                    updateSaveBar();
                    loadBatchEditor(entries);
                } else {
                    showToast('Batch save failed: ' + (result.error || 'Unknown error'), 'error');
                }
            },
            error: function () {
                $('#btnSave').prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save');
                showToast('Batch save failed — server error', 'error');
            }
        });
    }

    function discardChanges() {
        if (currentSpell) loadDetail(currentSpell.spell.entry);
    }

    // ===================== HELPERS =====================

    function esc(text) {
        if (text == null) return '';
        var div = document.createElement('div');
        div.textContent = String(text);
        return div.innerHTML;
    }

    function escAttr(text) {
        if (text == null) return '';
        return String(text).replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }

    function showToast(msg, type) {
        var el = $('<div class="spell-toast ' + type + '">' + esc(msg) + '</div>');
        $('body').append(el);
        setTimeout(function () { el.fadeOut(300, function () { el.remove(); }); }, 4000);
    }

    // ===================== EVENTS =====================

    $('#btnSearchSpells').on('click', function () { doSearch(1); });
    $('#spellSearch').on('keydown', function (e) { if (e.key === 'Enter') doSearch(1); });
    $('#filterSchool, #filterMechanic').on('change', function () { doSearch(1); });

    // View mode toggle
    $(document).on('click', '#btnViewMode', function () {
        viewMode = viewMode === 'grouped' ? 'flat' : 'grouped';
        $(this).html(viewMode === 'grouped'
            ? '<i class="fa-solid fa-layer-group"></i> Grouped'
            : '<i class="fa-solid fa-list"></i> Flat');
        doSearch(1);
    });

    $('#btnSpellFirstPage').on('click', function () { doSearch(1); });
    $('#btnSpellPrevPage').on('click', function () { if (currentPage > 1) doSearch(currentPage - 1); });
    $('#btnSpellNextPage').on('click', function () { if (currentPage < totalPages) doSearch(currentPage + 1); });
    $('#btnSpellLastPage').on('click', function () { doSearch(totalPages); });
    $('#spellPageJump').on('keydown', function (e) {
        if (e.key === 'Enter') {
            var p = Math.max(1, Math.min(parseInt($(this).val()) || 1, totalPages));
            doSearch(p);
        }
    });
    $('#spellPageJump').on('blur', function () {
        var p = Math.max(1, Math.min(parseInt($(this).val()) || 1, totalPages));
        if (p !== currentPage) doSearch(p);
    });
    $('#spellPageSize').val(currentPageSize);
    $('#spellPageSize').on('change', function () {
        currentPageSize = parseInt($(this).val()) || 50;
        try { localStorage.setItem('msui_spells_pageSize', currentPageSize); } catch (e) { }
        doSearch(1);
    });

    $(document).on('click', '.spell-row', function () {
        $('.spell-row').removeClass('active');
        $(this).addClass('active');
        loadDetail($(this).data('entry'));
    });

    // Reset spell to baseline
    $(document).on('click', '#btnResetSpell', function () {
        if (!currentSpell) return;
        var entry = currentSpell.spell.entry;
        var name = currentSpell.spell.name || 'Spell #' + entry;

        BaselineSystem.resetSpell(entry, name, function (success) {
            if (success) loadDetail(entry);
        });
    });

    $('#btnSave').on('click', saveChanges);
    $('#btnDiscard').on('click', discardChanges);

});
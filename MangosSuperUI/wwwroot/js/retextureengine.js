/* Retexture Engine — UI controller.
 *
 * Drives the /RetextureEngine backend (Preview / Sheet / queue). Retexture LOGIC
 * lives
 * in C# (PaletteSwapService); this file is UI only: browse retextureable items,
 * tune theory + tier + value knobs, and see the live flat recolor. Item search
 * reuses the proven /Items/Search endpoint.
 *
 * Viewer seam: on-model preview mounts via window.mountCharacterViewer once the
 * character-viewer module + a character-GLB source are wired (see Index.cshtml).
 * The mannequin is dressed at MOUNT time, not at first item selection — a GLB
 * straight out of the loader has every geoset variant switched on at once and
 * looks like melted wax until something resolves them (see showOnModel).
 */
(function () {
    'use strict';

    // The recolor theories (PaletteSwapService.RecolorTheories) and tiers. 'none' is the straight
    // recolor (picked hue on every material); the engine keeps 'fan' as its seeded default.
    var THEORIES = ['none', 'fan', 'identity', 'analogous', 'accent', 'luminance', 'bank'];
    var TIERS = ['improved', 'power', 'glory', 'gods'];

    // Inventory types that can actually be retextured (RetextureSupport.KindForInventoryType):
    // atlas + cape + model. Everything else has no visual to recolor.
    var RETEX_INVTYPES = new Set([
        4, 5, 6, 7, 8, 9, 10, 19, 20,   // atlas (painted armor)
        16,                              // cape
        1, 3, 13, 14, 15, 17, 21, 22, 23, 25, 26  // model (own M2)
    ]);

    var character = { race: 'Human', gender: 'Male' };
    // The character GLB is resolved SERVER-SIDE (CharacterModelService) via
    // /Items/CharacterPreview, which renders a canvas carrying data-glb-url /
    // data-skin-url. Same contract items-character-panel.js uses — it is not a
    // static file, which is why /character_models/HumanMale.glb 404s.
    async function fetchCharacterUrls(race, gender) {
        try {
            var res = await fetch('/Items/CharacterPreview?race=' + encodeURIComponent(race) +
                '&gender=' + encodeURIComponent(gender), { credentials: 'same-origin' });
            if (!res.ok) throw new Error('HTTP ' + res.status);
            var html = await res.text();
            var g = /data-glb-url="([^"]+)"/.exec(html);
            var s = /data-skin-url="([^"]+)"/.exec(html);
            return { glbUrl: g ? g[1] : null, skinUrl: s ? s[1] : null };
        } catch (err) {
            console.error('[retexture] fetchCharacterUrls failed', err);
            return { glbUrl: null, skinUrl: null };
        }
    }

    // Starter clothes so the mannequin is never naked (Recruit's Shirt + Pants).
    // Same items the items page uses; the selected item equips over them.
    var DEFAULT_OUTFIT = [
        { itemId: 38, displayId: 9891, inventoryType: 4 },
        { itemId: 39, displayId: 9892, inventoryType: 7 }
    ];

    var state = {
        displayId: 0,
        mode: 'existing',   // 'existing' = committed texture · 'new' = theory reroll preview
        itemId: 0,
        inventoryType: 0,
        outfitPieces: null,   // tier-set pieces to dress the mannequin (null = starter outfit)
        itemName: '',
        theory: 'fan',
        tier: 'improved',
        ladder: false,
        value: {
            mode: 'keep',      // keep | invert
            sigma: 2.5, detail: 1.0, blend: 1.0,
            knee: 0.40, floor: 0.25, alpha: 16, scale: true
        }
    };

    var page = 1, pageSize = 40, totalPages = 1, icons = {};
    var selection = {};   // entry -> { displayId, name, inventoryType }, persists across pages
    var generatedSet = null;   // lootifier generated entries — hidden from browse (navigate by base)
    var previewTimer = null;

    // ── init ────────────────────────────────────────────────────────────────
    $(function () {
        buildSeg('#reTheory', THEORIES, state.theory, function (v) { state.theory = v; schedulePreview(); updateBatchUsing(); renderTierVariants(); });
        buildSeg('#reTier', TIERS, state.tier, function (v) { state.tier = v; schedulePreview(); renderTierVariants(); });
        buildSeg('#reValueMode', ['keep', 'invert'], state.value.mode, function (v) {
            state.value.mode = v; schedulePreview(); updateBatchUsing(); renderTierVariants();
        });

        // search
        var searchTimer = null;
        $('#reSearch').on('input', function () {
            clearTimeout(searchTimer);
            searchTimer = setTimeout(function () { page = 1; doSearch(); }, 250);
        });
        $('#reRetexOnly').on('change', function () { page = 1; doSearch(); });
        $('#rePrev').on('click', function () { if (page > 1) { page--; doSearch(); } });
        $('#reNext').on('click', function () { if (page < totalPages) { page++; doSearch(); } });

        // Base-name navigation. Generated variants are now excluded server-side
        // (RetextureSupport.BrowseAsync), so this set is only kept for local
        // checks — and the first search no longer waits on it.
        $.getJSON('/RetextureEngine/GeneratedEntries', function (r) {
            if (r && r.success) generatedSet = new Set(r.entries || []);
        });
        doSearch();

        // filters
        buildFilters();
        $('#reFClass').on('change', function () { page = 1; doSearch(); });
        $('#reFSub, #reFSlot, #reFQuality').on('change', function () { page = 1; doSearch(); });
        $('#reFMinLvl, #reFMaxLvl').on('input', function () {
            clearTimeout(searchTimer);
            searchTimer = setTimeout(function () { page = 1; doSearch(); }, 350);
        });

        // direct display id
        $('#reLoadId').on('click', function () {
            var id = parseInt($('#reDisplayId').val(), 10) || 0;
            if (id > 0) selectItem(id, 'display ' + id, 0, 0);
        });
        $('#reDisplayId').on('keydown', function (e) { if (e.key === 'Enter') $('#reLoadId').click(); });

        // advanced value sliders
        bindSlider('#reSigma', '#reSigmaOut', 'sigma', 1);
        bindSlider('#reDetail', '#reDetailOut', 'detail', 1);
        bindSlider('#reBlend', '#reBlendOut', 'blend', 1);
        bindSlider('#reKnee', '#reKneeOut', 'knee', 2);
        bindSlider('#reFloor', '#reFloorOut', 'floor', 2);
        bindSlider('#reAlpha', '#reAlphaOut', 'alpha', 0);
        $('#reScale').on('change', function () { state.value.scale = this.checked; schedulePreview(); });

        $('#reSheetBtn').on('click', loadSheet);

        // batch (Lootifier retexture queue)
        loadBatchSources();
        $('#reBuildBtn').on('click', buildQueue);
        $('#reRunBtn').on('click', runQueue);
        $('#reResetBtn').on('click', resetFailed);
        $('#reClearBtn').on('click', clearQueue);
        $('#reRebuildBtn').on('click', rebuildPatch);
        $('#reDownloadBtn').on('click', downloadPatch);
        $('#reRedoBtn').on('click', redoAll);
        $('#reRevertBtn').on('click', revertQueue);
        $('#rePurgeBtn').on('click', purgeOrphans);
        // Every batch button acts on the TICKED sources, so re-read the scope
        // whenever they change.
        $('#reBatchSources').on('change', '.re-src', function () { updateScope(); refreshQueue(); });
        updateBatchUsing();

        // selection (ad-hoc multi-item retexture)
        $('#reSelClear').on('click', clearSelection);
        $('#reSelRun').on('click', runSelection);

        // view mode: existing (committed) vs new (theory reroll)
        $('#reMode').on('click', '.re-mode-btn', function () {
            state.mode = $(this).data('mode');
            $('#reMode .re-mode-btn').removeClass('active');
            $(this).addClass('active');
            if (state.displayId) { renderPreview(); renderTierVariants(); showOnModel(); }
        });

        // viewer race/gender
        $('#reRace').on('change', function () { character.race = this.value; swapCharacter(); });
        $('#reGender').on('change', function () { character.gender = this.value; swapCharacter(); });
    });

    // ── segmented button groups ───────────────────────────────────────────────
    function buildSeg(sel, values, current, onPick) {
        var $c = $(sel).empty();
        values.forEach(function (v) {
            var $b = $('<button type="button"></button>').text(v).toggleClass('active', v === current);
            $b.on('click', function () {
                $c.children().removeClass('active'); $b.addClass('active'); onPick(v);
            });
            $c.append($b);
        });
    }

    function bindSlider(inputSel, outSel, key, decimals) {
        var $in = $(inputSel), $out = $(outSel);
        $in.on('input', function () {
            var val = parseFloat(this.value);
            state.value[key] = val;
            $out.text(val.toFixed(decimals));
            schedulePreview();
        });
    }

    // ── search (reuses /Items/Search) ─────────────────────────────────────────
    // Filter option maps (mirror items.js) + subclass names for weapon/armor type.
    var CLASS_NAMES = { 0: 'Consumable', 1: 'Container', 2: 'Weapon', 4: 'Armor', 5: 'Reagent', 7: 'Trade Goods', 9: 'Recipe', 12: 'Quest', 15: 'Misc' };
    var SLOT_NAMES = {
        1: 'Head', 2: 'Neck', 3: 'Shoulder', 4: 'Shirt', 5: 'Chest', 6: 'Waist', 7: 'Legs',
        8: 'Feet', 9: 'Wrist', 10: 'Hands', 11: 'Finger', 12: 'Trinket', 13: 'One-Hand',
        14: 'Shield', 15: 'Ranged', 16: 'Back', 17: 'Two-Hand', 19: 'Tabard', 20: 'Robe',
        21: 'Main Hand', 22: 'Off Hand', 23: 'Held In Off-Hand', 25: 'Thrown', 26: 'Ranged'
    };
    var QUALITY_NAMES = ['Poor', 'Common', 'Uncommon', 'Rare', 'Epic', 'Legendary', 'Artifact'];
    // Combined weapon-type / armor-material dropdown; value "class:subclass" so it
    // needs no cascade (the old cascade left it disabled until a class was picked).
    var TYPE_OPTIONS = [
        ['2:0', 'Axe (1H)'], ['2:1', 'Axe (2H)'], ['2:2', 'Bow'], ['2:3', 'Gun'],
        ['2:4', 'Mace (1H)'], ['2:5', 'Mace (2H)'], ['2:6', 'Polearm'], ['2:7', 'Sword (1H)'],
        ['2:8', 'Sword (2H)'], ['2:10', 'Staff'], ['2:13', 'Fist'], ['2:15', 'Dagger'],
        ['2:16', 'Thrown'], ['2:18', 'Crossbow'], ['2:19', 'Wand'],
        ['4:1', 'Cloth'], ['4:2', 'Leather'], ['4:3', 'Mail'], ['4:4', 'Plate'], ['4:6', 'Shield']
    ];

    function fillSelect(sel, entries, anyLabel) {
        var h = '<option value="">' + anyLabel + '</option>';
        entries.forEach(function (e) { h += '<option value="' + e[0] + '">' + esc(e[1]) + '</option>'; });
        $(sel).html(h);
    }
    function buildFilters() {
        fillSelect('#reFClass', Object.keys(CLASS_NAMES).map(function (k) { return [k, CLASS_NAMES[k]]; }), 'Any class');
        fillSelect('#reFSub', TYPE_OPTIONS, 'Any type');
        fillSelect('#reFSlot', Object.keys(SLOT_NAMES).map(function (k) { return [k, SLOT_NAMES[k]]; }), 'Any slot');
        fillSelect('#reFQuality', QUALITY_NAMES.map(function (n, i) { return [i, n]; }), 'Any quality');
    }
    function activeFilters() {
        var f = {};
        var typeVal = $('#reFSub').val();   // "class:subclass" — overrides the class dropdown
        if (typeVal) { var p = typeVal.split(':'); f.classFilter = p[0]; f.subclassFilter = p[1]; }
        else { var c = $('#reFClass').val(); if (c !== '') f.classFilter = c; }
        var sl = $('#reFSlot').val(); if (sl !== '') f.inventoryTypeFilter = sl;
        var ql = $('#reFQuality').val(); if (ql !== '') f.qualityFilter = ql;
        var mn = $('#reFMinLvl').val(); if (mn !== '') f.minLevel = mn;
        var mx = $('#reFMaxLvl').val(); if (mx !== '') f.maxLevel = mx;
        return f;
    }

    function doSearch() {
        var q = ($('#reSearch').val() || '').trim();
        var f = activeFilters();
        var hasFilter = Object.keys(f).length > 0;
        if (q.length < 2 && !hasFilter) {
            $('#reList').empty();
            $('#reResultInfo').text('Type at least 2 characters, or pick a filter.');
            $('#rePager').hide();
            return;
        }
        $('#reResultInfo').text('Searching\u2026');

        // /RetextureEngine/Browse applies EVERY predicate in SQL — including
        // "has a displayId", "is a retextureable inventory type" and "is not a
        // lootifier generated variant". Those three used to be applied here, in
        // JS, AFTER the server had already paginated: a page whose 40 rows were
        // all filtered out rendered empty while the pager still counted them,
        // which is why Next past page 1 so often showed nothing. Now totalCount
        // and totalPages describe exactly the rows that get rendered.
        var params = $.extend({
            q: q,
            page: page,
            pageSize: pageSize,
            retexOnly: $('#reRetexOnly').is(':checked')
        }, f);

        $.getJSON('/RetextureEngine/Browse', params, function (data) {
            if (!data || !data.success) {
                $('#reResultInfo').text((data && data.error) ? 'Search failed: ' + data.error : 'Search failed.');
                $('#reList').empty();
                $('#rePager').hide();
                return;
            }

            icons = data.icons || {};
            totalPages = data.totalPages || 1;

            // The server clamps the page when a filter change shrinks the result
            // set under the page you were on; follow it rather than keeping a
            // stale number that would make Prev/Next behave oddly.
            if (data.page && data.page !== page) page = data.page;

            var items = data.items || [];
            $('#reResultInfo').text(
                'Showing ' + items.length + ' of ' +
                (data.totalCount || 0).toLocaleString() +
                ($('#reRetexOnly').is(':checked') ? ' retextureable' : '') + ' items');

            renderList(items);
            $('#rePager').toggle(totalPages > 1);
            $('#rePageLabel').text('Page ' + page + ' / ' + totalPages);
            $('#rePrev').prop('disabled', page <= 1);
            $('#reNext').prop('disabled', page >= totalPages);
        }).fail(function () {
            $('#reResultInfo').text('Search failed.');
            $('#reList').empty();
            $('#rePager').hide();
        });
    }

    function renderList(items) {
        var $list = $('#reList').empty();
        if (items.length === 0) {
            $list.html('<div class="re-result-info">No items match these filters.</div>');
            return;
        }
        items.forEach(function (it) {
            var icon = icons[it.displayId] || '/Icon/Get?name=inv_misc_questionmark';
            var checked = selection[it.entry] ? ' checked' : '';
            var $row = $(
                '<div class="re-row" data-did="' + it.displayId + '">' +
                '<input type="checkbox" class="re-selbox"' + checked + ' />' +
                '<img src="' + esc(icon) + '" alt="" loading="lazy" />' +
                '<div style="min-width:0;flex:1;">' +
                '<div class="re-row-name quality-' + (it.quality || 0) + '">' + esc(it.name) + '</div>' +
                '<div class="re-row-meta">#' + it.entry + ' \u00b7 display ' + it.displayId + '</div>' +
                '</div></div>');
            $row.find('.re-selbox')
                .on('click', function (e) { e.stopPropagation(); })
                .on('change', function () {
                    if (this.checked) selection[it.entry] = { displayId: it.displayId, name: it.name, inventoryType: it.inventoryType };
                    else delete selection[it.entry];
                    updateSelBar();
                });
            $row.on('click', function () {
                $('#reList .re-row').removeClass('active');
                $row.addClass('active');
                selectItem(it.displayId, it.name, it.entry, it.inventoryType);
            });
            $list.append($row);
        });
    }

    // ── selection + live preview ──────────────────────────────────────────────
    function selectItem(displayId, name, itemId, inventoryType) {
        state.displayId = displayId;
        state.itemId = itemId || 0;
        state.inventoryType = inventoryType || 0;
        state.itemName = name || '';
        $('#reDisplayId').val(displayId);
        $('#reSelected').html(esc(name || 'item') + '<span class="re-did">display ' + displayId + '</span>');
        $('#reSheetWrap').hide();
        renderPreview();
        renderTierVariants();
        renderItemSources(state.itemId);
        showOnModel();
    }

    function renderItemSources(entry) {
        var $s = $('#reSources');
        if (!entry) { $s.empty(); return; }
        $s.html('<span class="re-hint">Looking up sources\u2026</span>');
        $.getJSON('/RetextureEngine/ItemSources', { entry: entry }, function (r) {
            if (!r || !r.success) { $s.empty(); return; }
            var parts = [];
            if (r.creatures && r.creatures.length) parts.push('<b>Drops from:</b> ' + r.creatures.map(esc).join(', '));
            if (r.vendors && r.vendors.length) parts.push('<b>Sold by:</b> ' + r.vendors.map(esc).join(', '));
            if (r.quests && r.quests.length) parts.push('<b>Quest reward:</b> ' + r.quests.map(esc).join(', '));
            $s.html(parts.length ? parts.join('<br>') : '<span class="re-hint">No known drop / vendor / quest source.</span>');
        }).fail(function () { $s.empty(); });
    }

    function schedulePreview() {
        if (!state.displayId) return;
        clearTimeout(previewTimer);
        previewTimer = setTimeout(renderPreview, 220);
        scheduleModel();
    }

    var modelTimer = null;
    function scheduleModel() {
        if (!state.displayId || !viewerHandle) return;   // only once the viewer is up
        clearTimeout(modelTimer);
        modelTimer = setTimeout(function () { applyRetexture(); }, 500);
    }

    function valueParams() {
        var p = { value: state.value.mode };
        if (state.value.mode === 'invert') {
            var v = state.value;
            p.vSigma = v.sigma; p.vDetail = v.detail; p.vBlend = v.blend;
            p.vKnee = v.knee; p.vFloor = v.floor; p.vAlpha = v.alpha; p.vScale = v.scale;
        }
        return p;
    }

    function renderPreview() {
        if (!state.displayId) return;
        if (state.mode === 'existing') {
            $('#reFlatMsg').text('Loading\u2026').show();
            $.getJSON('/RetextureEngine/SourceTexture', { displayId: state.displayId }, function (data) {
                if (data && data.success && data.url) { $('#reFlat').attr('src', data.url); $('#reFlatMsg').hide(); $('#reStatus').text('existing (committed) texture'); }
                else { $('#reFlat').removeAttr('src'); $('#reFlatMsg').text((data && data.error) || 'no texture').show(); $('#reStatus').text(''); }
            }).fail(function () { $('#reFlatMsg').text('request failed').show(); });
            return;
        }
        var params = $.extend({ displayId: state.displayId, theory: state.theory, tier: state.tier, ladder: state.ladder }, valueParams());
        $('#reStatus').text('Rendering\u2026');
        $('#reFlatMsg').text('Rendering\u2026').show();
        $.getJSON('/RetextureEngine/Preview', params, function (data) {
            if (!data.success) { $('#reFlatMsg').text(data.error || 'no source texture').show(); $('#reFlat').removeAttr('src'); $('#reStatus').text(''); return; }
            $('#reFlat').attr('src', data.url); $('#reFlatMsg').hide();
            $('#reStatus').text(state.theory + ' \u00b7 ' + state.tier + (state.ladder ? ' \u00b7 ladder' : '') + ' \u00b7 value ' + (data.value || state.value.mode));
        }).fail(function () { $('#reFlatMsg').text('preview request failed').show(); $('#reStatus').text(''); });
    }

    // Tier-variant strip: the selected item rendered at each tier, labelled.
    var TIERS_ALL = ['improved', 'power', 'glory', 'gods'];
    function setTier(tier) {
        state.tier = tier;
        $('#reTier button').removeClass('active').filter(function () { return $(this).text() === tier; }).addClass('active');
        schedulePreview();
        renderTierVariants();
    }
    function renderTierVariants() {
        var $c = $('#reVariants');
        if (!state.displayId) { $c.empty(); return; }
        // Prefer the REAL generated tier variants for this item's base.
        if (state.itemId) {
            $.getJSON('/RetextureEngine/BaseVariants', { entry: state.itemId }, function (r) {
                if (r && r.success && r.hasVariants && r.tiers && r.tiers.length) renderRealVariants(r);
                else renderInterimVariants();
            }).fail(renderInterimVariants);
        } else {
            renderInterimVariants();
        }
    }
    function renderRealVariants(r) {
        var $c = $('#reVariants').empty();
        var baseInv = (r.tiers && r.tiers[0] && r.tiers[0].inventoryType) || state.inventoryType || 0;
        var list = [];
        // The base item first, as an always-there reference to flip the tiers against.
        if (r.baseDisplayId) list.push({ tier: '', label: 'Base', displayId: r.baseDisplayId, entry: r.baseEntry, name: r.baseName || 'base', inventoryType: baseInv, isBase: true });
        (r.tiers || []).forEach(function (t) {
            list.push({ tier: t.tier, label: t.tier || t.qualityName || '', displayId: t.displayId, entry: t.entry, name: t.name, inventoryType: t.inventoryType });
        });
        list.forEach(function (t) {
            var active = (t.displayId === state.displayId) ? ' active' : '';
            var $v = $('<div class="re-variant"><div class="re-variant-label">' + esc(t.label) + '</div>' +
                '<div class="re-variant-img' + active + '"><img alt="' + esc(t.label) + '" /></div></div>');
            $v.find('.re-variant-img').on('click', function () {
                if (t.tier) { state.tier = t.tier; $('#reTier button').removeClass('active').filter(function () { return $(this).text() === t.tier; }).addClass('active'); }
                selectItem(t.displayId, t.name, t.entry, t.inventoryType);
            });
            $c.append($v);
            // Base tile always shows its ORIGINAL texture (the reference); tiers follow the mode.
            if (t.isBase || state.mode === 'existing') {
                $.getJSON('/RetextureEngine/SourceTexture', { displayId: t.displayId }, function (data) { if (data && data.success && data.url) $v.find('img').attr('src', data.url); });
            } else {
                var params = $.extend({ displayId: t.displayId, theory: state.theory, tier: t.tier, ladder: state.ladder }, valueParams());
                $.getJSON('/RetextureEngine/Preview', params, function (data) { if (data && data.success) $v.find('img').attr('src', data.url); });
            }
        });
    }
    function renderInterimVariants() {
        var $c = $('#reVariants').empty();

        // Base reference (the item's own current texture) — always shown.
        var $b = $('<div class="re-variant"><div class="re-variant-label">Base</div>' +
            '<div class="re-variant-img"><img alt="Base" /></div></div>');
        $c.append($b);
        $.getJSON('/RetextureEngine/SourceTexture', { displayId: state.displayId }, function (data) { if (data && data.success && data.url) $b.find('img').attr('src', data.url); });

        // A non-lootifier item has no committed tier variants, so in "existing" mode the
        // base is all there is; in "new" mode preview what each tier would look like.
        if (state.mode === 'existing') return;

        TIERS_ALL.forEach(function (tier) {
            var $v = $('<div class="re-variant"><div class="re-variant-label">' + tier + '</div>' +
                '<div class="re-variant-img' + (tier === state.tier ? ' active' : '') + '"><img alt="' + tier + '" /></div></div>');
            $v.find('.re-variant-img').on('click', function () { setTier(tier); });
            $c.append($v);
            var params = $.extend({ displayId: state.displayId, theory: state.theory, tier: tier, ladder: state.ladder }, valueParams());
            $.getJSON('/RetextureEngine/Preview', params, function (data) { if (data && data.success) $v.find('img').attr('src', data.url); });
        });
    }

    // ladder toggle (declared here so it is bound after DOM ready)
    $(function () {
        $('#reLadder').on('change', function () { state.ladder = this.checked; schedulePreview(); });
    });

    // ── contact sheet ─────────────────────────────────────────────────────────
    function loadSheet() {
        if (!state.displayId) { $('#reStatus').text('Select an item first.'); return; }
        var params = $.extend({ displayId: state.displayId, ladder: state.ladder }, valueParams());
        var $btn = $('#reSheetBtn').prop('disabled', true);
        $('#reStatus').text('Building contact sheet\u2026');

        $.getJSON('/RetextureEngine/Sheet', params, function (data) {
            $btn.prop('disabled', false);
            if (!data.success) { $('#reStatus').text(data.error || 'sheet failed'); return; }
            $('#reSheet').attr('src', data.url);
            $('#reSheetWrap').show();
            $('#reStatus').text('Sheet: ' + data.chromaticFamilies + ' chromatic families \u00b7 value ' + data.value);
        }).fail(function () {
            $btn.prop('disabled', false);
            $('#reStatus').text('sheet request failed');
        });
    }

    // ── 3D viewer ─────────────────────────────────────────────────────────────
    // Base character mounts via mountCharacterViewer using the GLB resolved from
    // the selected item is dressed on with equip.equipDisplay (-> /Items/ItemDressing),
    // showing the BASE item on the model. On-model preview of the RETEXTURED result
    // needs recolored assets fed to equipBodyAtlasRetextureDirect(slotUrls) [armor] /
    // equipWeaponGlbDirect(glbUrl, inventoryType, attachments) [model items].
    var viewerHandle = null;
    var viewerPromise = null;    // in-flight mount, so two callers can't mount twice
    var equipToken = 0;
    var activeModelPreviewGlbs = [];

    function modelPreviewGlbUrls(data) {
        var urls = [];
        if (data && data.glbUrl) urls.push(data.glbUrl);
        var attachments = (data && data.attachments) || {};
        Object.keys(attachments).forEach(function (key) {
            if (attachments[key]) urls.push(attachments[key]);
        });
        return urls.filter(function (url, index) { return urls.indexOf(url) === index; });
    }

    function deleteModelPreviewGlbs(urls) {
        (urls || []).forEach(function (url) {
            $.ajax({
                url: '/Items/DeletePreviewGlb',
                method: 'POST',
                contentType: 'application/json',
                data: JSON.stringify({ glbUrl: url })
            });
        });
    }

    function replaceActiveModelPreviewGlbs(urls) {
        var next = urls || [];
        deleteModelPreviewGlbs(activeModelPreviewGlbs.filter(function (url) {
            return next.indexOf(url) < 0;
        }));
        activeModelPreviewGlbs = next;
    }

    window.addEventListener('re-viewer-ready', function () {
        buildOutfitPicker();
        // Dress the mannequin straight away. It used to wait for an item
        // selection, which left a raw all-geosets-visible GLB on screen for as
        // long as the page sat idle. See showOnModel().
        showOnModel();
    });

    // The mannequin's clothes: a class tier set (if picked) plus the starter
    // outfit filling any slots the set leaves bare; the selected item goes over.
    function outfitPayload() {
        var pieces = (state.outfitPieces && state.outfitPieces.length) ? state.outfitPieces : [];
        var covered = {};
        pieces.forEach(function (p) { covered[p.slot] = true; });
        var out = pieces.map(function (p) { return { itemId: p.itemId, displayId: p.displayId, inventoryType: p.slot }; });
        DEFAULT_OUTFIT.forEach(function (d) {
            if (covered[d.inventoryType]) return;
            if (d.inventoryType === 7 && covered[20]) return;   // robe covers legs
            out.push(d);
        });
        return out;
    }

    function buildOutfitPicker() {
        var ts = window.reTierSets;
        if (!ts || $('#reOutfitClass option').length) return;
        var ch = '<option value="">Starter clothes</option>';
        (ts.TIER_CLASSES || []).forEach(function (c) { ch += '<option value="' + esc(c) + '">' + esc(c) + '</option>'; });
        $('#reOutfitClass').html(ch);
        var th = '';
        (ts.TIER_IDS || []).forEach(function (t) { th += '<option value="' + esc(t) + '">' + esc(t) + '</option>'; });
        $('#reOutfitTier').html(th).prop('disabled', true);

        $('#reOutfitClass, #reOutfitTier').on('change', function () {
            var cls = $('#reOutfitClass').val();
            $('#reOutfitTier').prop('disabled', !cls);
            if (!cls) {
                state.outfitPieces = null;
            } else {
                var tier = $('#reOutfitTier').val() || (ts.TIER_IDS && ts.TIER_IDS[0]);
                var set = ts.TIER_SETS && ts.TIER_SETS[cls] && ts.TIER_SETS[cls][tier];
                state.outfitPieces = (set && set.length) ? set : null;
            }
            showOnModel();
        });
    }

    // Mount exactly once. Boot and a fast first click can both land in the same
    // tick now that boot no longer waits for a selection, so the in-flight
    // promise is memoised — otherwise two mountCharacterViewer() calls race and
    // the loser's equips write into a viewer nobody is looking at.
    function ensureViewer() {
        if (viewerHandle) return Promise.resolve(viewerHandle);
        if (viewerPromise) return viewerPromise;
        viewerPromise = mountViewer().then(function (h) {
            viewerHandle = h;
            viewerPromise = null;
            return h;
        }, function (err) {
            viewerPromise = null;
            $('#reViewerMsg').text('viewer error: ' + (err && err.message || err)).show();
            return null;
        });
        return viewerPromise;
    }

    async function mountViewer() {
        if (typeof window.mountCharacterViewer !== 'function') return null;
        var canvas = document.getElementById('char-preview-canvas');
        if (!canvas) return null;
        $('#reViewerMsg').text('Loading character\u2026').show();
        var urls = await fetchCharacterUrls(character.race, character.gender);
        if (!urls.glbUrl) { $('#reViewerMsg').text('No character model for ' + character.race + ' ' + character.gender + '.').show(); return null; }
        var h = await window.mountCharacterViewer({ canvas: canvas, glbUrl: urls.glbUrl, skinUrl: urls.skinUrl });
        // Resolve geoset categories the instant the geometry lands. unequipAll()
        // does this too, but it awaits a base-skin fetch first, and that gap is
        // long enough to show a frame of the all-variants mannequin. This call is
        // synchronous, so there is no such frame.
        try {
            if (window.reDresser && h && h.cv && h.cv.character) {
                window.reDresser.showDefaultGeosets(h.cv.character);
            }
        } catch (e) { /* older embed handle shape — unequipAll still covers it */ }
        $('#reViewerMsg').hide();
        return h;
    }

    // Put the character into a presentable state, then equip the selected item
    // over it if there is one.
    //
    // The baseline is the whole point of this function. A GLB out of loader.js
    // has EVERY geoset visible simultaneously — all sleeve variants, all leg
    // variants, every hair style stacked on the same skull. unequipAll() runs the
    // category/variant resolver (dresser.showDefaultGeosets) and repaints a clean
    // base-skin atlas, which is what turns that back into a person. So it has to
    // run on every mount and every race swap, NOT only when an item is picked —
    // that missing call is exactly what made a cold page load look wrong.
    async function showOnModel() {
        var h = await ensureViewer();
        if (!h || !window.reEquip) return;
        var token = ++equipToken;                 // guard against out-of-order equips
        try {
            // Strip to default geosets + base skin, then apply the starter outfit
            // (or the picked tier set) so the mannequin is never naked.
            await window.reEquip.unequipAll(h.cv.character);
            if (token !== equipToken) return;
            try { await window.reEquip.equipMultiple(h.cv.character, outfitPayload()); } catch (e) { }
            if (token !== equipToken) return;

            // Nothing selected — the dressed baseline IS the finished state.
            if (!state.displayId) {
                replaceActiveModelPreviewGlbs([]);
                $('#reViewerMsg').hide();
                return;
            }

            var res = await window.reEquip.equipDisplay(h.cv.character, state.displayId, state.itemId);
            if (token !== equipToken) return;
            if (res && res.applied === false) {
                $('#reViewerMsg').text('on-model: ' + (res.reason || 'not equippable')).show();
                return;
            }
            if (state.mode === 'existing') { $('#reViewerMsg').hide(); }   // committed texture already on the display
            else { await applyRetexture(h, token); }
        } catch (err) {
            $('#reViewerMsg').text('equip error: ' + (err && err.message || err)).show();
        }
    }

    // Overlay the RETEXTURED variant on the already-dressed character: recolors
    // atlas slots / bakes a GLB server-side at the current knobs, then paints or
    // remounts. Runs on select and (debounced) on every knob change.
    async function applyRetexture(h, token) {
        h = h || viewerHandle;
        if (!h || !window.reEquip || !state.displayId) return;
        if (token == null) token = ++equipToken;

        var params = $.extend({
            displayId: state.displayId,
            theory: state.theory,
            tier: state.tier,
            ladder: state.ladder
        }, valueParams());

        $('#reViewerMsg').text('Updating model\u2026').show();
        var responseGlbs = [];
        try {
            var data = await $.getJSON('/RetextureEngine/PreviewOnModel', params);
            responseGlbs = modelPreviewGlbUrls(data);
            if (token !== equipToken) {
                deleteModelPreviewGlbs(responseGlbs);
                return;
            }
            if (!data.success) { $('#reViewerMsg').text('on-model: ' + (data.error || 'recolor failed')).show(); return; }

            if (data.kind === 'atlas') {
                await window.reEquip.equipBodyAtlasRetextureDirect(h.cv.character, data.slotUrls);
            } else if (data.kind === 'weapon') {
                var mounted = await window.reEquip.equipWeaponGlbDirect(
                    h.cv.character, data.glbUrl, state.inventoryType || 0, data.attachments);
                if (!mounted) throw new Error('model preview could not be mounted');
            }
            if (token !== equipToken) {
                deleteModelPreviewGlbs(responseGlbs);
                return;
            }
            replaceActiveModelPreviewGlbs(data.kind === 'weapon' ? responseGlbs : []);
            $('#reViewerMsg').hide();
        } catch (err) {
            deleteModelPreviewGlbs(responseGlbs);
            $('#reViewerMsg').text('overlay error: ' + (err && err.message || err)).show();
        }
    }

    // Race / gender change. Last click wins — clicking through the race list
    // faster than the GLBs load used to leave whichever request happened to
    // return last on screen.
    var swapSeq = 0;
    async function swapCharacter() {
        if (!viewerHandle) { showOnModel(); return; }   // not mounted yet: mount at the new race
        var seq = ++swapSeq;
        var race = character.race, gender = character.gender;
        $('#reViewerMsg').text('Loading character\u2026').show();
        var urls = await fetchCharacterUrls(race, gender);
        if (seq !== swapSeq) return;
        if (!urls.glbUrl) { $('#reViewerMsg').text('No character model for ' + race + ' ' + gender + '.').show(); return; }
        try {
            await viewerHandle.swap({ glbUrl: urls.glbUrl, skinUrl: urls.skinUrl });
            if (seq !== swapSeq) return;
            try {
                if (window.reDresser && viewerHandle.cv && viewerHandle.cv.character) {
                    window.reDresser.showDefaultGeosets(viewerHandle.cv.character);
                }
            } catch (e) { }
            // The swapped-in GLB arrives all-variants-visible exactly like the
            // first one did, so re-dress it whether or not an item is selected.
            await showOnModel();
        } catch (err) {
            $('#reViewerMsg').text('swap error: ' + (err && err.message || err)).show();
        }
    }

    // ══════════════════════════════════════════════════════════════════════
    //  Batch: the Lootifier retexture queue — now served by /RetextureEngine/*
    //
    //  It used to call /Items/, whose queue endpoints have no notion of `source`:
    //  you could BUILD a queue for one lootifier, but Run / Requeue / Clear always
    //  hit the whole table, and none of them undid anything. Every button below
    //  acts on the TICKED sources only, one scoped call each.
    //
    //  The verbs, in the order you actually use them:
    //    Build queue   enqueue (base x tier) jobs for the ticked sources
    //    Run queue     drain them at the current theory + value
    //    Re-retexture  re-arm rows that are already done -> Run again picks them
    //                  up and RECYCLES the display each one minted, so the patch
    //                  does not grow
    //    Revert        put the variants back on their original display and delete
    //                  the minted ones. This is the undo. Clear is not.
    //    Purge orphans sweep minted displays nothing points at any more
    // ══════════════════════════════════════════════════════════════════════

    function selectedSources() { return $('.re-src:checked').map(function () { return this.value; }).get(); }

    function selectedLabels() {
        var l = $('.re-src:checked').map(function () {
            return $(this).closest('.re-src-row').find('strong').text();
        }).get();
        return l.length ? l.join(' + ') : 'nothing';
    }

    function updateScope() {
        var n = selectedSources().length;
        $('#reScopeText').text('Acting on: ' + selectedLabels())
            .toggleClass('re-scope-empty', n === 0);
    }

    function post(url, body) {
        return $.ajax({
            url: url, method: 'POST', contentType: 'application/json',
            data: JSON.stringify(body || {})
        });
    }

    // Run fn(source) once per ticked source, in series. Resolves with the results.
    function perSource(fn) {
        var srcs = selectedSources();
        if (srcs.length === 0) { $('#reQueueText').text('Tick at least one source first.'); return null; }
        return srcs.reduce(function (chain, src) {
            return chain.then(function (acc) {
                return $.when(fn(src)).then(function (r) { acc.push(r || {}); return acc; });
            });
        }, $.Deferred().resolve([]).promise());
    }

    function sum(results, key) {
        return results.reduce(function (n, r) { return n + (r[key] || 0); }, 0);
    }

    function loadBatchSources() {
        $.getJSON('/RetextureEngine/Sources', function (d) {
            if (!d || !d.success) { $('#reBatchSources').html('<span class="re-hint">Sources unavailable.</span>'); return; }
            var rows = (d.sources || []).map(function (s) {
                var none = s.bases === 0;
                var bits = [s.bases + ' base items \u00b7 ' + s.variants + ' variants'];
                if (s.queued) {
                    var q = s.done + '/' + s.queued + ' retextured';
                    if (s.pending) q += ' \u00b7 ' + s.pending + ' pending';
                    if (s.failed) q += ' \u00b7 ' + s.failed + ' failed';
                    if (s.reverted) q += ' \u00b7 ' + s.reverted + ' reverted';
                    bits.push(q);
                }
                return '<label class="re-src-row">' +
                    '<input type="checkbox" class="re-src" value="' + s.source + '"' + (none ? ' disabled' : ' checked') + ' />' +
                    '<span><strong>' + esc(s.label) + '</strong><br>' +
                    '<span class="re-src-meta">' + bits.join('<br>') + '</span></span>' +
                    '</label>';
            }).join('');
            $('#reBatchSources').html(rows || '<span class="re-hint">No lootifier variants found yet.</span>');
            updateScope();
            refreshQueue();
            refreshPatchInfo();
        }).fail(function () { $('#reBatchSources').html('<span class="re-hint">Sources request failed.</span>'); });
    }

    function updateBatchUsing() {
        $('#reBatchUsing').text('Batch runs with: ' + state.theory + ' \u00b7 value ' + state.value.mode +
            '  (per-tier shape/policy per job)');
    }

    // Counts are scoped when exactly one source is ticked; otherwise show the
    // whole table, since that is what the buttons will span anyway.
    function refreshQueue() {
        var srcs = selectedSources();
        var url = '/RetextureEngine/QueueStatus' +
            (srcs.length === 1 ? '?source=' + encodeURIComponent(srcs[0]) : '');
        $.getJSON(url, function (d) {
            if (!d || !d.success) return;
            var total = d.pending + d.done + d.failed + d.reverted;
            var txt = 'Queue' + (d.source ? ' [' + d.source + ']' : '') + ': ' +
                d.pending + ' pending \u00b7 ' + d.done + ' done';
            if (d.failed) txt += ' \u00b7 ' + d.failed + ' failed';
            if (d.reverted) txt += ' \u00b7 ' + d.reverted + ' reverted';
            $('#reQueueText').text(txt);
            if (total > 0) { $('#reBarWrap').show(); $('#reBar').css('width', Math.round(((d.done + d.failed) / total) * 100) + '%'); }
            else { $('#reBarWrap').hide(); }
            if (d.failures && d.failures.length) {
                $('#reFailures').html(d.failures.slice(0, 5).map(function (f) {
                    return esc(f.itemName) + ' [' + esc(f.tier) + ']: ' + esc(f.error || 'failed');
                }).join('<br>'));
            } else { $('#reFailures').empty(); }
        });
    }

    function buildQueue() {
        var sources = selectedSources();
        if (sources.length === 0) { $('#reQueueText').text('Tick at least one source first.'); return; }
        var $b = $('#reBuildBtn').prop('disabled', true);
        post('/RetextureEngine/BuildQueue', { sources: sources, requeue: $('#reRequeue').is(':checked') })
            .done(function (r) {
                if (!r || !r.success) { $('#reQueueText').text((r && r.error) || 'Build failed'); return; }
                $('#reQueueText').text('Queued ' + r.queued + ' jobs across ' + r.basesCovered + ' items' +
                    (r.skipped ? ' (' + r.skipped + ' already queued \u2014 tick Requeue to replace)' : ''));
                loadBatchSources();
            }).fail(function () { $('#reQueueText').text('Build request failed'); })
            .always(function () { $b.prop('disabled', false); });
    }

    // Drains one source at a time so a scoped run never touches another
    // lootifier's pending jobs.
    var running = false;
    function runQueue() {
        if (running) return;
        var srcs = selectedSources();
        if (srcs.length === 0) { $('#reQueueText').text('Tick at least one source first.'); return; }

        running = true;
        var $b = $('#reRunBtn').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Running\u2026');
        function stop() {
            running = false;
            $b.prop('disabled', false).html('<i class="fa-solid fa-play"></i> Run queue');
            loadBatchSources();
        }

        var i = 0;
        function nextSource() {
            if (i >= srcs.length) { $('#reQueueText').text('Queue complete.'); stop(); return; }
            var src = srcs[i++];
            (function step() {
                post('/RetextureEngine/ProcessQueue',
                    $.extend({ max: 3, source: src, theory: state.theory }, valueParams()))
                    .done(function (r) {
                        if (!r || !r.success) { $('#reQueueText').text((r && r.error) || 'Retexture failed'); return stop(); }
                        refreshQueue();
                        if (r.remaining > 0) step(); else nextSource();
                    })
                    .fail(function () { $('#reQueueText').text('Retexture request failed'); stop(); });
            })();
        }
        nextSource();
    }

    function resetFailed() {
        var p = perSource(function (src) {
            return post('/RetextureEngine/ResetQueue', { source: src, mode: 'failed' });
        });
        if (p) p.then(function (res) {
            $('#reQueueText').text('Requeued ' + sum(res, 'affected') + ' failed jobs');
            loadBatchSources();
        });
    }

    // RE-RETEXTURE: re-arm rows that already ran, keeping new_display_id so the
    // drain recycles the display each job minted instead of orphaning it.
    function redoAll() {
        if (!confirm('Re-arm every processed job for ' + selectedLabels() + '?\n\n' +
            'Nothing changes until you hit Run queue. Each job then re-recolors at the ' +
            'current theory + value and DELETES the display it minted last time, so the ' +
            'patch does not grow.')) return;
        var p = perSource(function (src) {
            return post('/RetextureEngine/ResetQueue', { source: src, mode: 'all' });
        });
        if (p) p.then(function (res) {
            $('#reQueueText').text('Re-armed ' + sum(res, 'affected') + ' jobs \u2014 hit Run queue.');
            loadBatchSources();
        });
    }

    function clearQueue() {
        if (!confirm('Delete the queue rows for ' + selectedLabels() + '?\n\n' +
            'Already-applied retextures STAY APPLIED and their tracking rows go with the ' +
            'queue \u2014 you lose the ability to revert them. Use Revert first if that is ' +
            'what you meant.')) return;
        var p = perSource(function (src) {
            return post('/RetextureEngine/ResetQueue', { source: src, mode: 'clear' });
        });
        if (p) p.then(function (res) {
            $('#reQueueText').text('Cleared ' + sum(res, 'affected') + ' rows');
            loadBatchSources();
        });
    }

    // THE UNDO. Restores base_display_id on every variant, deletes the displays
    // that were minted for them, rebuilds the patch. Rows stay pending so you can
    // run again from clean.
    function revertQueue() {
        if (!confirm('Revert all retextures for ' + selectedLabels() + '?\n\n' +
            'Variants go back to their ORIGINAL display, the custom displays are deleted ' +
            'and patch-4.MPQ is rebuilt without them. Jobs are left pending so you can ' +
            'Run queue again.')) return;
        var $b = $('#reRevertBtn').prop('disabled', true);
        var p = perSource(function (src) {
            return post('/RetextureEngine/RevertQueue', { source: src, requeue: true });
        });
        if (!p) { $b.prop('disabled', false); return; }
        p.then(function (res) {
            $('#reQueueText').text('Reverted ' + sum(res, 'reverted') + ' jobs \u00b7 ' +
                sum(res, 'itemsRestored') + ' items restored \u00b7 ' +
                sum(res, 'displaysPurged') + ' displays deleted \u00b7 patch rebuilt');
            loadBatchSources();
        }).always(function () { $b.prop('disabled', false); });
    }

    // Sweep minted displays nothing references any more: re-run debris from before
    // recycling existed, plus anything left behind by a lootifier rollback.
    function purgeOrphans() {
        var $b = $('#rePurgeBtn').prop('disabled', true);
        post('/RetextureEngine/PurgeOrphans', { apply: false })
            .done(function (r) {
                if (!r || !r.success) { $('#reQueueText').text((r && r.error) || 'Purge check failed'); return; }
                if (!r.orphans) { $('#reQueueText').text('No orphaned displays (' + r.minted + ' minted, all referenced).'); return; }
                if (!confirm(r.orphans + ' of ' + r.minted + ' minted displays are unreferenced.\n\n' +
                    'Delete their BLP rows and rebuild patch-4.MPQ?')) return;
                $b.prop('disabled', true);
                post('/RetextureEngine/PurgeOrphans', { apply: true })
                    .done(function (r2) {
                        $('#reQueueText').text(r2 && r2.success
                            ? 'Purged ' + r2.orphans + ' displays (' + r2.deleted + ' rows) \u00b7 patch now ' + r2.mpqFiles + ' files'
                            : ((r2 && r2.error) || 'Purge failed'));
                        loadBatchSources();
                    })
                    .fail(function () { $('#reQueueText').text('Purge request failed'); })
                    .always(function () { $b.prop('disabled', false); });
            })
            .fail(function () { $('#reQueueText').text('Purge check failed'); })
            .always(function () { $b.prop('disabled', false); });
    }

    // patch-4.MPQ itself. The rebuild auto-copies to the WSL client folder, but
    // the REAL client reads C:\WoW Vanilla\Data\ — that copy is manual, which is
    // what this button is for.
    function refreshPatchInfo() {
        $.getJSON('/RetextureEngine/PatchStatus', function (d) {
            if (!d || !d.success) { $('#rePatchInfo').empty(); return; }
            $('#reDownloadBtn').prop('disabled', !d.available);
            if (!d.available) { $('#rePatchInfo').text('No retextures committed yet.'); return; }
            var bits = [d.fileName];
            if (d.onDisk) {
                bits.push(d.sizeMb + ' MB');
                if (d.builtUtc) bits.push('built ' + new Date(d.builtUtc).toLocaleString());
            } else {
                bits.push('will be rebuilt on download');
            }
            $('#rePatchInfo').text(bits.join(' \u00b7 ') + '  \u2014 copy into the client Data folder yourself');
        }).fail(function () { $('#rePatchInfo').empty(); });
    }

    function downloadPatch() {
        window.location = '/RetextureEngine/DownloadPatch';
    }

    function rebuildPatch() {
        var $b = $('#reRebuildBtn').prop('disabled', true);
        post('/RetextureEngine/RebuildPatch', {})
            .done(function (r) {
                $('#reQueueText').text(r && r.success
                    ? 'Patch rebuilt \u00b7 ' + r.mpqFiles + ' files'
                    : ((r && r.error) || 'Rebuild done.'));
            })
            .fail(function () { $('#reQueueText').text('Rebuild request failed'); })
            .always(function () { $b.prop('disabled', false); });
    }

    function updateSelBar() {
        var n = Object.keys(selection).length;
        $('#reSelCount').text(n + ' selected');
        $('#reSelBar').toggleClass('active', n > 0);
    }

    function clearSelection() {
        selection = {};
        $('#reList .re-selbox').prop('checked', false);
        updateSelBar();
    }

    function runSelection() {
        var items = Object.keys(selection).map(Number);
        if (items.length === 0) return;
        var tierSel = $('#reSelTier').val();
        var body = $.extend({
            items: items,
            tiers: tierSel === 'all' ? [] : [tierSel],
            theory: state.theory,
            asSet: $('#reAsSet').is(':checked')
        }, valueParams());

        var $b = $('#reSelRun').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Retexturing\u2026');
        $('#reSelStatus').text('Retexturing ' + items.length + ' item(s)' + (tierSel === 'all' ? ', all tiers' : ', ' + tierSel) +
            ' as ' + state.theory + ($('#reAsSet').is(':checked') ? ' (one set)' : '') + '\u2026');

        $.ajax({ url: '/RetextureEngine/RetextureSelection', method: 'POST', contentType: 'application/json', data: JSON.stringify(body) })
            .done(function (r) {
                if (!r.success) { $('#reSelStatus').text(r.error || 'Retexture failed'); return; }
                $('#reSelStatus').text('Done: ' + r.succeeded + ' applied' + (r.failed ? ', ' + r.failed + ' failed' : '') +
                    (r.patchRebuilt ? ' \u00b7 patch rebuilt' : (r.patchError ? ' \u00b7 patch error' : '')));
                if (state.displayId) { renderPreview(); showOnModel(); }
            })
            .fail(function () { $('#reSelStatus').text('Retexture request failed'); })
            .always(function () { $b.prop('disabled', false).html('<i class="fa-solid fa-wand-magic-sparkles"></i> Retexture selection'); });
    }

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
            .replace(/"/g, '&quot;').replace(/'/g, '&#39;');
    }
})();

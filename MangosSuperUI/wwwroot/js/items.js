// MangosSuperUI — Items Browser + Editor JS

$(function () {

    var currentPage = 1;
    var totalPages = 1;
    var currentPageSize = parseInt(localStorage.getItem('msui_items_pageSize')) || 50;
    var currentIcons = {};
    var currentDetailEntry = null;
    var currentDetailItem = null;

    // Edit state
    var editMode = false;
    var editEntry = null;
    var editIsClone = false;
    var editIsBaseGame = false;
    var editSourceEntry = null;
    var editOriginalRow = null; // Full DB row — base for collectFormData()

    // Icon picker state
    var iconPickerPage = 1;
    var iconPickerQuery = '';
    var iconPickerCallback = null;

    var CUSTOM_RANGE_START = 900000;

    // ===================== BASELINE INTEGRATION =====================

    BaselineSystem.checkStatus(function (status) {
        BaselineSystem.renderWarningBanner('#baselineWarning');
    });

    $(document).on('baseline:initialized', function () {
        if (currentDetailEntry) {
            loadItemChangelog(currentDetailEntry);
        }
    });

    // ===================== CONSTANTS =====================

    var QUALITY_NAMES = ['Poor', 'Common', 'Uncommon', 'Rare', 'Epic', 'Legendary', 'Artifact'];
    var QUALITY_COLORS = ['#9d9d9d', 'inherit', '#1eff00', '#0070dd', '#a335ee', '#ff8000', '#e6cc80'];

    var CLASS_NAMES = {
        0: 'Consumable', 1: 'Container', 2: 'Weapon', 4: 'Armor',
        5: 'Reagent', 7: 'Trade Goods', 9: 'Recipe', 12: 'Quest', 15: 'Misc'
    };

    var SLOT_NAMES = {
        0: '', 1: 'Head', 2: 'Neck', 3: 'Shoulder', 4: 'Shirt', 5: 'Chest',
        6: 'Waist', 7: 'Legs', 8: 'Feet', 9: 'Wrist', 10: 'Hands', 11: 'Finger',
        12: 'Trinket', 13: 'One-Hand', 14: 'Shield', 15: 'Ranged', 16: 'Back',
        17: 'Two-Hand', 18: 'Bag', 19: 'Tabard', 20: 'Robe', 21: 'Main Hand',
        22: 'Off Hand', 23: 'Held In Off-Hand', 24: 'Ammo', 25: 'Thrown', 26: 'Ranged'
    };

    var BONDING_NAMES = { 0: 'No Binding', 1: 'Binds on Pickup', 2: 'Binds on Equip', 3: 'Binds on Use', 4: 'Quest Item' };

    var STAT_TYPES = {
        0: 'Mana', 1: 'Health', 3: 'Agility', 4: 'Strength', 5: 'Intellect',
        6: 'Spirit', 7: 'Stamina', 12: 'Defense', 13: 'Dodge', 14: 'Parry',
        15: 'Block', 31: 'Hit', 32: 'Crit', 35: 'Resilience', 36: 'Haste'
    };

    var TRIGGER_NAMES = {
        0: 'Use (right-click)',
        1: 'On Equip (passive)',
        2: 'Chance on Hit (proc)',
        5: 'Use (no delay)',
        6: 'Learn Spell (recipe)'
    };

    var DMG_TYPE_NAMES = {
        0: 'Physical', 1: 'Holy', 2: 'Fire', 3: 'Nature', 4: 'Frost', 5: 'Shadow', 6: 'Arcane'
    };

    var WOW_CLASSES = [
        { bit: 0, name: 'Warrior' }, { bit: 1, name: 'Paladin' }, { bit: 2, name: 'Hunter' },
        { bit: 3, name: 'Rogue' }, { bit: 4, name: 'Priest' }, { bit: 5, name: 'Shaman' },
        { bit: 6, name: 'Mage' }, { bit: 7, name: 'Warlock' }, { bit: 8, name: 'Druid' }
    ];

    var WOW_RACES = [
        { bit: 0, name: 'Human' }, { bit: 1, name: 'Orc' }, { bit: 2, name: 'Dwarf' },
        { bit: 3, name: 'Night Elf' }, { bit: 4, name: 'Undead' }, { bit: 5, name: 'Tauren' },
        { bit: 6, name: 'Gnome' }, { bit: 7, name: 'Troll' }
    ];

    // ===================== SEARCH =====================

    function doSearch(page) {
        currentPage = page || 1;
        var params = {
            q: $('#itemSearch').val(),
            classFilter: $('#filterClass').val() || undefined,
            qualityFilter: $('#filterQuality').val() || undefined,
            inventoryTypeFilter: $('#filterSlot').val() || undefined,
            page: currentPage,
            pageSize: currentPageSize
        };
        Object.keys(params).forEach(function (k) { if (params[k] === undefined || params[k] === '') delete params[k]; });

        $('#itemListContainer').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i> Searching...</div>');

        $.getJSON('/Items/Search', params, function (data) {
            currentIcons = data.icons || {};
            totalPages = data.totalPages;
            $('#totalItemCount').text(data.totalCount.toLocaleString());
            $('#resultInfo').text('Showing ' + data.items.length + ' of ' + data.totalCount.toLocaleString());

            if (data.items.length === 0) {
                $('#itemListContainer').html('<div class="text-center p-4 text-muted">No items found</div>');
                $('#paginationBar').hide();
                return;
            }

            var html = '';
            data.items.forEach(function (item) {
                var iconPath = currentIcons[item.displayId] || '/icons/inv_misc_questionmark.png';
                var qualityClass = 'quality-' + (item.quality || 0);
                var slot = SLOT_NAMES[item.inventoryType] || '';
                var cls = CLASS_NAMES[item.class] || '';
                var isCustom = item.entry >= CUSTOM_RANGE_START;
                var meta = [cls, slot, item.requiredLevel > 1 ? 'Req ' + item.requiredLevel : ''].filter(Boolean).join(' · ');

                html += '<div class="item-row" data-entry="' + item.entry + '">' +
                    '<img class="item-icon" src="' + esc(iconPath) + '" alt="" loading="lazy" />' +
                    '<div style="flex: 1; min-width: 0;">' +
                    '<div class="item-name ' + qualityClass + '">' + esc(item.name) +
                    (isCustom ? ' <span style="font-size:9px;color:var(--status-online);">★</span>' : '') +
                    '</div>' +
                    '<div class="item-meta">' + esc(meta) + '</div>' +
                    '</div>' +
                    '<div class="item-entry">#' + item.entry + '</div>' +
                    '</div>';
            });

            $('#itemListContainer').html(html);

            if (data.totalPages > 1) {
                $('#paginationBar').show();
                $('#pageJumpInput').val(data.page).attr('max', data.totalPages);
                $('#pageTotalLabel').text(data.totalPages);
                $('#btnFirstPage').prop('disabled', data.page <= 1);
                $('#btnPrevPage').prop('disabled', data.page <= 1);
                $('#btnNextPage').prop('disabled', data.page >= data.totalPages);
                $('#btnLastPage').prop('disabled', data.page >= data.totalPages);
            } else if (data.items.length > 0) {
                // Single page — still show bar for page size selector access
                $('#paginationBar').show();
                $('#pageJumpInput').val(1).attr('max', 1);
                $('#pageTotalLabel').text('1');
                $('#btnFirstPage, #btnPrevPage, #btnNextPage, #btnLastPage').prop('disabled', true);
            } else {
                $('#paginationBar').hide();
            }
        }).fail(function () {
            $('#itemListContainer').html('<div class="text-center p-4 text-muted">Search failed</div>');
        });
    }

    // ===================== DETAIL =====================

    function loadDetail(entry) {
        currentDetailEntry = entry;
        $('#detailContent').html('<div class="text-center p-3"><i class="fa-solid fa-spinner fa-spin"></i></div>');

        $.getJSON('/Items/Detail', { entry: entry }, function (data) {
            if (!data.found) {
                $('#detailContent').html('<div class="text-center text-muted p-3">Item not found</div>');
                $('#detailActions').hide();
                return;
            }

            currentDetailItem = data.item;
            var item = data.item;
            var q = item.quality || 0;
            var qualityClass = 'quality-' + q;
            var isCustom = entry >= CUSTOM_RANGE_START;

            var html = '<div class="item-detail-header">' +
                '<img class="detail-icon-lg" src="' + esc(data.iconPath) + '" data-entry="' + item.entry + '" title="Click to edit" />' +
                '<div style="flex:1;min-width:0;">' +
                '<div class="' + qualityClass + '" style="font-size: 18px; font-weight: 700; line-height: 1.2;">' + esc(item.name) + '</div>' +
                '<div style="font-size: 13px; color: var(--text-muted); margin-top: 2px;">' +
                esc(QUALITY_NAMES[q] || '') + ' · Entry #' + item.entry +
                (isCustom ? ' <span style="color: var(--status-online);">★ Custom</span>' : '') +
                '</div>' +
                '</div>' +
                '</div>';

            // 3D model preview (if available)
            if (data.modelPath) {
                html += '<div class="model-preview-container"><model-viewer src="' + esc(data.modelPath) + '" auto-rotate camera-controls shadow-intensity="0.5" exposure="1.2" style="width:100%;height:100%;--poster-color:transparent;"></model-viewer></div>';
            }

            if (item.bonding > 0)
                html += '<div style="font-size: 12px; color: var(--text-secondary);">' + esc(BONDING_NAMES[item.bonding] || '') + '</div>';

            var slotText = SLOT_NAMES[item.inventory_type] || '';
            var classText = CLASS_NAMES[item.class] || '';
            if (slotText || classText)
                html += '<div class="d-flex justify-content-between" style="font-size: 12.5px; color: var(--text-secondary);"><span>' + esc(slotText) + '</span><span>' + esc(classText) + '</span></div>';

            if (item.armor > 0)
                html += '<div style="font-size: 12.5px;">' + item.armor + ' Armor</div>';

            if (item.dmg_min1 > 0 || item.dmg_max1 > 0) {
                var speed = (item.delay || 2000) / 1000;
                var dps = ((item.dmg_min1 + item.dmg_max1) / 2) / speed;
                html += '<div class="d-flex justify-content-between" style="font-size: 12.5px;"><span>' + item.dmg_min1 + ' - ' + item.dmg_max1 + ' Damage</span><span>Speed ' + speed.toFixed(2) + '</span></div>' +
                    '<div style="font-size: 12.5px;">(' + dps.toFixed(1) + ' damage per second)</div>';
            }

            var stats = [];
            for (var i = 1; i <= 10; i++) {
                var st = item['stat_type' + i], sv = item['stat_value' + i];
                if (st > 0 && sv !== 0)
                    stats.push((sv > 0 ? '+' : '') + sv + ' ' + (STAT_TYPES[st] || 'Stat ' + st));
            }
            if (stats.length > 0) {
                html += '<div class="detail-section">';
                stats.forEach(function (s) { html += '<div class="stat-line">' + esc(s) + '</div>'; });
                html += '</div>';
            }

            var spells = [];
            for (var j = 1; j <= 5; j++) {
                var sid = item['spellid_' + j] || item['spell_id_' + j];
                var trigger = item['spelltrigger_' + j] || item['spell_trigger_' + j];
                if (sid > 0) spells.push({ id: sid, trigger: trigger });
            }
            if (spells.length > 0) {
                html += '<div class="detail-section"><div class="detail-section-title">Spells</div>';
                spells.forEach(function (sp) {
                    html += '<div class="spell-line"><i class="fa-solid fa-bolt" style="font-size: 10px;"></i> ' +
                        esc(TRIGGER_NAMES[sp.trigger] || 'Trigger ' + sp.trigger) + ': Spell #' + sp.id + '</div>';
                });
                html += '</div>';
            }

            html += '<div class="detail-section"><div class="detail-section-title">Info</div>';
            if (item.required_level > 1)
                html += '<div class="detail-row"><span class="label">Required Level</span><span class="value">' + item.required_level + '</span></div>';
            html += '<div class="detail-row"><span class="label">Item Level</span><span class="value">' + (item.item_level || 0) + '</span></div>';
            if (item.buy_price > 0)
                html += '<div class="detail-row"><span class="label">Buy Price</span><span class="value">' + formatCopper(item.buy_price) + '</span></div>';
            if (item.sell_price > 0)
                html += '<div class="detail-row"><span class="label">Sell Price</span><span class="value">' + formatCopper(item.sell_price) + '</span></div>';
            if (item.stackable > 1)
                html += '<div class="detail-row"><span class="label">Max Stack</span><span class="value">' + item.stackable + '</span></div>';
            html += '<div class="detail-row"><span class="label">Display ID</span><span class="value">' + (item.display_id || 0) + '</span></div>';
            html += '</div>';

            if (item.description)
                html += '<div style="font-size: 12px; color: #ffd100; font-style: italic; margin-top: 10px;">"' + esc(item.description) + '"</div>';

            $('#detailContent').html(html);

            // Show action buttons
            $('#detailActions').show();
            // If item is custom, change Edit button text
            if (isCustom) {
                $('#btnEditOriginal').html('<i class="fa-solid fa-pen"></i> Edit');
            } else {
                $('#btnEditOriginal').html('<i class="fa-solid fa-pen"></i> Edit Original');
            }

            // Load OG changelog
            loadItemChangelog(entry);

            // Load textures from MPQ
            var did = item.display_id || 0;
            loadItemTextures(did);

            // 3D character viewer integration — fire-and-forget event the
            // items-character-panel module listens for. Only equippable
            // items with a valid displayId are forwarded; consumables and
            // trade goods (inventory_type=0) are skipped so the viewer
            // doesn't try to dress a character in a potion.
            if (did > 0 && item.inventory_type > 0) {
                document.dispatchEvent(new CustomEvent('cv:item-selected', {
                    detail: {
                        itemId: item.entry,
                        displayId: did,
                        inventoryType: item.inventory_type,
                        name: item.name,
                    },
                }));
            }
        });
    }

    // ===================== ITEM TEXTURES =====================

    function loadItemTextures(displayId) {
        if (!displayId || displayId <= 0) {
            $('#itemTexturePanel').hide();
            return;
        }

        $('#itemTexturePanel').show();
        $('#itemTextureContent').html(
            '<div class="text-center p-2" style="font-size: 12px; color: var(--text-muted);">' +
            '<i class="fa-solid fa-spinner fa-spin"></i> Extracting textures from MPQ...</div>'
        );

        loadTextureGrid(displayId, '#itemTextureContent');
    }

    function loadEditTextures(displayId) {
        if (!displayId || displayId <= 0) {
            $('#editTextureContent').html(
                '<div class="text-center p-2" style="font-size:11px;color:var(--text-muted);">No display ID</div>');
            return;
        }

        $('#editTextureContent').html(
            '<div class="text-center p-2" style="font-size: 11px; color: var(--text-muted);">' +
            '<i class="fa-solid fa-spinner fa-spin"></i> Loading...</div>'
        );

        loadTextureGrid(displayId, '#editTextureContent');
    }

    function loadTextureGrid(displayId, targetSelector) {
        $.getJSON('/Items/TextureInfo', { displayId: displayId }, function (data) {
            if (!data.found || !data.textures || data.textures.length === 0) {
                $(targetSelector).html(
                    '<div class="text-center p-2" style="font-size: 11px; color: var(--text-muted);">' +
                    'No textures found for this model</div>'
                );
                return;
            }

            var html = '';

            // Model info header
            html += '<div style="font-size: 11px; color: var(--text-muted); margin-bottom: 8px; padding: 0 2px;">' +
                '<span style="color: var(--accent);">' + esc(data.modelName) + '</span>' +
                ' · ' + data.vertexCount.toLocaleString() + 'v/' + data.triangleCount.toLocaleString() + 't' +
                ' · ' + (data.m2Size / 1024).toFixed(0) + 'KB' +
                '</div>';

            // Texture grid
            html += '<div class="item-texture-grid">';

            data.textures.forEach(function (tex) {
                var sizeLabel = tex.width + '×' + tex.height;
                var formatLabel = tex.format + (tex.alphaDepth > 0 ? ' α' + tex.alphaDepth : '');
                var blpKB = (tex.blpFileSize / 1024).toFixed(0);

                html += '<div class="item-texture-card" data-mpq-path="' + esc(tex.mpqPath) + '" ' +
                    'data-tex-index="' + tex.index + '" data-width="' + tex.width + '" data-height="' + tex.height + '" ' +
                    'data-format="' + esc(tex.format) + '" title="' + esc(tex.mpqPath) + '">';

                if (tex.hasPreview) {
                    html += '<img class="item-texture-preview" src="' + esc(tex.previewUrl) + '" ' +
                        'alt="' + esc(tex.filename) + '" loading="lazy" />';
                } else {
                    html += '<div class="item-texture-preview" style="display:flex;align-items:center;justify-content:center;' +
                        'background:var(--bg-input);color:var(--text-muted);font-size:10px;">No preview</div>';
                }

                html += '<div class="item-texture-info">' +
                    '<div class="item-texture-name" title="' + esc(tex.filename) + '">' + esc(tex.filename) + '</div>' +
                    '<div class="item-texture-meta">' + sizeLabel + ' · ' + formatLabel + ' · ' + blpKB + 'KB</div>' +
                    '<button class="btn-retexture" title="AI Retexture"><i class="fa-solid fa-wand-magic-sparkles"></i></button>' +
                    '</div>';

                html += '</div>';
            });

            html += '</div>';

            $(targetSelector).html(html);
        }).fail(function () {
            $(targetSelector).html(
                '<div class="text-center p-2" style="font-size: 11px; color: var(--status-error);">' +
                'Failed to load textures</div>'
            );
        });
    }

    // Texture card click → show full-size preview in a modal overlay
    $(document).on('click', '.item-texture-card', function (e) {
        // Don't open overlay if they clicked the retexture button
        if ($(e.target).closest('.btn-retexture').length) return;

        var $img = $(this).find('.item-texture-preview');
        if (!$img.is('img')) return;

        var src = $img.attr('src');
        var filename = $(this).find('.item-texture-name').text();
        var meta = $(this).find('.item-texture-meta').text();
        var mpqPath = $(this).data('mpq-path');

        // Simple overlay
        var overlay = $('<div class="texture-overlay">' +
            '<div class="texture-overlay-content">' +
            '<img src="' + esc(src) + '" style="max-width:100%;max-height:70vh;image-rendering:pixelated;border-radius:4px;" />' +
            '<div style="margin-top:10px;text-align:center;">' +
            '<div style="font-size:14px;font-weight:600;color:var(--text-primary);">' + esc(filename) + '</div>' +
            '<div style="font-size:11px;color:var(--text-muted);margin-top:2px;">' + esc(meta) + '</div>' +
            '<div style="font-size:10px;color:var(--text-muted);margin-top:2px;word-break:break-all;">' + esc(mpqPath) + '</div>' +
            '</div>' +
            '</div>' +
            '</div>');

        overlay.on('click', function () { $(this).remove(); });
        $('body').append(overlay);
    });

    // ===================== RETEXTURE =====================

    // Right-click on texture card → show retexture dialog
    $(document).on('click', '.btn-retexture', function (e) {
        e.stopPropagation();
        var $card = $(this).closest('.item-texture-card');
        var mpqPath = $card.data('mpq-path');
        var filename = $card.find('.item-texture-name').text();
        var width = $card.data('width');
        var height = $card.data('height');
        var format = $card.data('format');

        var itemName = currentDetailItem ? (currentDetailItem.name || '') : '';

        // Show retexture modal
        var modal = $('<div class="texture-overlay" id="retextureOverlay">' +
            '<div class="retexture-dialog">' +
            '<div style="font-size:15px;font-weight:600;margin-bottom:12px;color:var(--text-primary);">' +
            '<i class="fa-solid fa-wand-magic-sparkles" style="color:var(--accent);"></i> Retexture: ' + esc(filename) + '</div>' +
            '<div style="font-size:11px;color:var(--text-muted);margin-bottom:12px;">' +
            width + '×' + height + ' · ' + esc(format) + ' · ' + esc(mpqPath) + '</div>' +

            // Mode toggle
            '<div style="display:flex;gap:8px;margin-bottom:12px;">' +
            '<label style="display:flex;align-items:center;gap:6px;font-size:12px;color:var(--text-primary);cursor:pointer;padding:6px 12px;border:1px solid var(--border);border-radius:var(--radius-sm);flex:1;transition:all 0.15s;" class="retex-mode-btn" data-mode="scratch">' +
            '<input type="radio" name="retexMode" value="scratch" style="accent-color:var(--accent);"> Generate from scratch</label>' +
            '<label style="display:flex;align-items:center;gap:6px;font-size:12px;color:var(--text-primary);cursor:pointer;padding:6px 12px;border:1px solid var(--border);border-radius:var(--radius-sm);flex:1;transition:all 0.15s;" class="retex-mode-btn active" data-mode="modify">' +
            '<input type="radio" name="retexMode" value="modify" checked style="accent-color:var(--accent);"> Modify existing</label>' +
            '</div>' +

            // Denoise slider (hidden by default, shown for modify mode)
            '<div id="retexDenoiseRow" style="margin-bottom:12px;">' +
            '<label style="display:block;font-size:11px;font-weight:600;color:var(--text-secondary);margin-bottom:3px;">' +
            'Modification Strength: <span id="retexDenoiseVal">0.50</span></label>' +
            '<div style="display:flex;align-items:center;gap:8px;">' +
            '<span style="font-size:10px;color:var(--text-muted);">Subtle</span>' +
            '<input type="range" id="retexDenoise" min="10" max="95" value="50" style="flex:1;accent-color:var(--accent);" />' +
            '<span style="font-size:10px;color:var(--text-muted);">Major</span>' +
            '</div>' +
            '</div>' +

            '<label style="display:block;font-size:11px;font-weight:600;color:var(--text-secondary);margin-bottom:3px;">Style Direction</label>' +
            '<input type="text" id="retextureStyle" class="form-input" placeholder="e.g. dark corrupted, frost-enchanted, golden holy" style="margin-bottom:10px;" />' +
            '<label style="display:block;font-size:11px;font-weight:600;color:var(--text-secondary);margin-bottom:3px;">Custom Prompt (optional — bypasses Ollama, sent directly to Flux)</label>' +
            '<textarea id="retexturePrompt" class="form-input" rows="3" placeholder="⚠ Must describe a FLAT TEXTURE, not an object. e.g. \'flat 2D texture, dark obsidian surface with gold inlay borders, hand-painted WoW style, no perspective\'" style="margin-bottom:14px;resize:vertical;"></textarea>' +
            '<div class="d-flex gap-2 justify-content-end">' +
            '<button class="btn-sm btn-outline-subtle" id="retextureCancel">Cancel</button>' +
            '<button class="btn-sm btn-accent" id="retextureGo" ' +
            'data-mpq="' + esc(mpqPath) + '" data-filename="' + esc(filename) + '">' +
            '<i class="fa-solid fa-wand-magic-sparkles"></i> Generate</button>' +
            '</div>' +
            '<div id="retextureStatus" style="margin-top:12px;display:none;"></div>' +
            '</div></div>');

        modal.on('click', function (ev) {
            if ($(ev.target).hasClass('texture-overlay')) $(this).remove();
        });
        $('body').append(modal);
    });

    $(document).on('click', '#retextureCancel', function () {
        $('#retextureOverlay').remove();
    });

    // Mode toggle: show/hide denoise slider
    $(document).on('change', 'input[name="retexMode"]', function () {
        var isModify = $(this).val() === 'modify';
        $('#retexDenoiseRow').toggle(isModify);
        $('.retex-mode-btn').removeClass('active');
        $(this).closest('.retex-mode-btn').addClass('active');
    });

    // Denoise slider label update
    $(document).on('input', '#retexDenoise', function () {
        $('#retexDenoiseVal').text(($(this).val() / 100).toFixed(2));
    });

    $(document).on('click', '#retextureGo', function () {
        var $btn = $(this);
        var mpqPath = $btn.data('mpq');
        var filename = $btn.data('filename');
        var style = $('#retextureStyle').val() || '';
        var customPrompt = $('#retexturePrompt').val() || '';
        var itemName = currentDetailItem ? (currentDetailItem.name || '') : '';
        var displayId = currentDetailItem ? (currentDetailItem.display_id || 0) : 0;
        var modifyExisting = $('input[name="retexMode"]:checked').val() === 'modify';
        var denoise = modifyExisting ? parseInt($('#retexDenoise').val()) / 100.0 : 1.0;

        if (!displayId) {
            showToast('No item selected', 'error');
            return;
        }

        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Generating...');
        $('#retextureStatus').show().html(
            '<div style="font-size:11px;color:var(--text-muted);">' +
            '<i class="fa-solid fa-spinner fa-spin"></i> Sending to Ollama + Flux pipeline... This may take 30-60s.' +
            '</div>');

        $.ajax({
            url: '/Items/Retexture',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({
                displayId: displayId,
                itemName: itemName,
                originalBlpFilename: filename,
                originalMpqPath: mpqPath,
                styleDirection: style,
                customPrompt: customPrompt || null,
                modifyExisting: modifyExisting,
                denoiseStrength: denoise
            }),
            success: function (data) {
                if (data.success) {
                    var html = '<div style="font-size:12px;color:var(--status-online);font-weight:600;margin-bottom:8px;">' +
                        '<i class="fa-solid fa-check"></i> Retexture complete!</div>';

                    if (data.previewUrl) {
                        html += '<img src="' + esc(data.previewUrl) + '?t=' + Date.now() + '" style="max-width:100%;border-radius:4px;image-rendering:pixelated;margin-bottom:8px;" />';
                    }

                    html += '<div style="font-size:10px;color:var(--text-muted);margin-bottom:4px;">' +
                        data.originalWidth + '×' + data.originalHeight + ' ' + esc(data.originalFormat || '') +
                        ' · BLP: ' + (data.blpSize / 1024).toFixed(1) + 'KB</div>';

                    if (data.newDisplayId > 0) {
                        html += '<div style="font-size:11px;color:var(--accent);margin-bottom:8px;font-weight:600;">' +
                            'New Display ID: ' + data.newDisplayId + '</div>';

                        // If in edit mode, offer to apply the new displayId
                        if (editMode && editEntry) {
                            html += '<button class="btn-sm btn-accent" id="btnApplyNewDisplayId" data-did="' + data.newDisplayId + '" style="margin-bottom:8px;">' +
                                '<i class="fa-solid fa-arrow-right"></i> Apply to this item</button> ';
                        }
                    }

                    if (data.prompt) {
                        html += '<div style="font-size:10px;color:var(--text-muted);margin-bottom:8px;font-style:italic;word-break:break-word;">' +
                            'Prompt: "' + esc(data.prompt.substring(0, 150)) + (data.prompt.length > 150 ? '...' : '') + '"</div>';
                    }

                    if (data.patchUrl) {
                        var patchFile = data.patchUrl.split('/').pop();
                        html += '<a href="/Items/DownloadPatch?file=' + encodeURIComponent(patchFile) + '" class="btn-sm btn-accent" download="' + esc(patchFile) + '" style="text-decoration:none;display:inline-block;">' +
                            '<i class="fa-solid fa-download"></i> Download ' + esc(patchFile) + '</a>';
                    }

                    $('#retextureStatus').html(html);
                    $btn.prop('disabled', false).html('<i class="fa-solid fa-wand-magic-sparkles"></i> Generate Another');
                } else {
                    $('#retextureStatus').html(
                        '<div style="font-size:11px;color:var(--status-error);">' +
                        '<i class="fa-solid fa-triangle-exclamation"></i> ' + esc(data.error || 'Unknown error') + '</div>');
                    $btn.prop('disabled', false).html('<i class="fa-solid fa-wand-magic-sparkles"></i> Retry');
                }
            },
            error: function () {
                $('#retextureStatus').html(
                    '<div style="font-size:11px;color:var(--status-error);">Request failed</div>');
                $btn.prop('disabled', false).html('<i class="fa-solid fa-wand-magic-sparkles"></i> Retry');
            }
        });
    });

    // ===================== ITEM CHANGELOG =====================

    function loadItemChangelog(entry) {
        if (!BaselineSystem.isInitialized()) {
            $('#itemChangelogPanel').hide();
            $('#itemResetContainer').hide();
            return;
        }

        BaselineSystem.loadItemDiff(entry, '#itemChangelogContent', function (data) {
            if (!data || !data.available || !data.hasOriginal) {
                if (entry >= CUSTOM_RANGE_START) {
                    $('#itemChangelogPanel').show();
                    $('#itemChangeCount').text('—').addClass('clean');
                } else {
                    $('#itemChangelogPanel').hide();
                }
                $('#itemResetContainer').hide();
                return;
            }

            $('#itemChangelogPanel').show();

            if (data.isModified) {
                var count = data.changes ? data.changes.length : 0;
                $('#itemChangeCount').text(count).removeClass('clean');
                $('#itemResetContainer').show();
            } else {
                $('#itemChangeCount').text('0').addClass('clean');
                $('#itemResetContainer').hide();
            }
        });
    }

    // ===================== EDIT FORM =====================

    function openEditPanel(sourceEntry, asClone) {
        // Fetch full row data
        $.getJSON('/Items/FullRow', { entry: sourceEntry }, function (data) {
            if (!data.found) {
                showToast('Item not found', 'error');
                return;
            }

            var item = data.item;
            editSourceEntry = sourceEntry;
            editIsClone = asClone;
            editIsBaseGame = !asClone && sourceEntry < CUSTOM_RANGE_START;
            editOriginalRow = item; // Stash full DB row as base for saves

            if (asClone) {
                // Get next custom ID
                $.getJSON('/Items/NextCustomId', function (idData) {
                    editEntry = idData.nextId;
                    renderEditForm(item, data.iconPath, data.modelPath);
                    showEditPanel();
                });
            } else {
                editEntry = sourceEntry;
                renderEditForm(item, data.iconPath, data.modelPath);
                showEditPanel();
            }
        });
    }

    function showEditPanel() {
        editMode = true;

        // Update header
        var name = $('#editFieldName').val() || 'New Item';
        $('#editHeaderName').text(name + (editIsClone ? ' (Clone)' : ''));

        var badge = $('#editBadge');
        if (editIsBaseGame) {
            badge.text('⚠ BASE GAME').addClass('base-game');
            $('#editPanel').addClass('base-game-mode');
            $('#editWarningBar').show();
        } else {
            badge.text('CUSTOM').removeClass('base-game');
            $('#editPanel').removeClass('base-game-mode');
            $('#editWarningBar').hide();
        }

        // Show edit panel, hide detail panel
        $('#colDetail').hide();
        $('#colEdit').show();
    }

    function closeEditPanel() {
        editMode = false;
        editEntry = null;
        editIsClone = false;
        editIsBaseGame = false;
        editSourceEntry = null;
        editOriginalRow = null;

        $('#colEdit').hide();
        $('#colDetail').show();
    }

    function renderEditForm(item, iconPath, modelPath) {
        var h = '';

        // ── Section 1: Identity ──
        h += sectionStart('identity', 'Identity', 'fa-tag', true);
        h += field('Name', '<input type="text" id="editFieldName" value="' + escAttr(item.name) + '" />');
        h += field('Quality', buildQualityDropdown(item.quality || 0));
        h += field('Icon / Appearance', buildIconPicker(iconPath, item.display_id || 0));

        // 3D model preview in edit form
        h += '<div class="edit-field"><label>3D Model <button type="button" class="btn-sm btn-outline-subtle" id="btnCheckItemModel" title="Check for 3D model" style="padding:1px 6px;font-size:10px;margin-left:6px;"><i class="fa-solid fa-cube"></i></button></label>';
        h += '<div id="editItemModelPreview">';
        if (modelPath) {
            h += '<div class="model-preview-container" style="height:180px;"><model-viewer src="' + esc(modelPath) + '" auto-rotate camera-controls shadow-intensity="0.5" exposure="1.2" style="width:100%;height:100%;--poster-color:transparent;"></model-viewer></div>';
        }
        h += '</div></div>';

        // Model Textures in edit form (retexture-capable)
        h += '<div class="edit-field"><label>Model Textures</label>';
        h += '<div id="editTextureContent"><div class="text-center p-2 text-muted" style="font-size:11px;">Loading textures...</div></div>';
        h += '</div>';

        h += field('Item Class', buildClassDropdown(item.class));
        h += field('Description', '<textarea id="editFieldDescription" placeholder="Orange flavor text shown in-game">' + esc(item.description || '') + '</textarea>');
        h += sectionEnd();

        // ── Section 2: Equipment & Stats ──
        h += sectionStart('equip', 'Equipment & Stats', 'fa-shield-halved', false);
        h += field('Inventory Slot', buildSlotDropdown(item.inventory_type));
        h += field('Item Level', '<input type="number" id="editFieldItemLevel" value="' + (item.item_level || 1) + '" min="1" max="100" />');
        h += '<div class="edit-field"><label>Stats</label><div id="statRowsContainer">';
        for (var i = 1; i <= 10; i++) {
            var st = item['stat_type' + i] || 0;
            var sv = item['stat_value' + i] || 0;
            if (st > 0 || sv !== 0)
                h += buildStatRow(i, st, sv);
        }
        h += '</div><button type="button" class="btn-add-row" id="btnAddStat"><i class="fa-solid fa-plus"></i> Add Stat</button></div>';
        h += field('Armor', '<input type="number" id="editFieldArmor" value="' + (item.armor || 0) + '" min="0" />');

        // Resistances (inline row)
        h += '<div class="edit-field"><label>Resistances</label><div class="edit-field-inline">';
        var resTypes = ['holy_res', 'fire_res', 'nature_res', 'frost_res', 'shadow_res', 'arcane_res'];
        var resLabels = ['Holy', 'Fire', 'Nature', 'Frost', 'Shadow', 'Arcane'];
        for (var r = 0; r < resTypes.length; r++) {
            h += '<div class="edit-field" style="flex: 0 0 auto;">' +
                '<label style="font-size:10px;">' + resLabels[r] + '</label>' +
                '<input type="number" class="editRes" data-col="' + resTypes[r] + '" value="' + (item[resTypes[r]] || 0) + '" min="0" style="width:54px;" />' +
                '</div>';
        }
        h += '</div></div>';
        h += sectionEnd();

        // ── Section 3: Weapon (only if class=2) ──
        var isWeapon = (item.class === 2);
        h += sectionStart('weapon', 'Weapon', 'fa-khanda', isWeapon);
        h += '<div class="edit-field-inline">';
        h += field('Damage Min', '<input type="number" id="editFieldDmgMin1" value="' + (item.dmg_min1 || 0) + '" min="0" />');
        h += field('Damage Max', '<input type="number" id="editFieldDmgMax1" value="' + (item.dmg_max1 || 0) + '" min="0" />');
        h += '</div>';
        h += '<div class="edit-field-inline">';
        h += field('Damage Type', buildDmgTypeDropdown(1, item.dmg_type1 || 0));
        h += field('Speed (sec)', '<input type="number" id="editFieldSpeed" value="' + ((item.delay || 2000) / 1000).toFixed(2) + '" min="0.1" step="0.1" />');
        h += '</div>';
        h += '<div class="edit-field"><label>DPS (calculated)</label><div id="dpsPreview" style="font-size: 13px; color: var(--text-secondary);">—</div></div>';

        // Second damage type (rare but supported)
        h += '<div class="edit-field-inline" style="margin-top:8px;">';
        h += field('Damage 2 Min', '<input type="number" id="editFieldDmgMin2" value="' + (item.dmg_min2 || 0) + '" min="0" />');
        h += field('Damage 2 Max', '<input type="number" id="editFieldDmgMax2" value="' + (item.dmg_max2 || 0) + '" min="0" />');
        h += '</div>';
        h += field('Damage 2 Type', buildDmgTypeDropdown(2, item.dmg_type2 || 0));
        h += sectionEnd();

        // ── Section 4: Spell Effects ──
        h += sectionStart('spells', 'Spell Effects', 'fa-bolt', false);
        h += '<div id="spellSlotsContainer">';
        for (var s = 1; s <= 5; s++) {
            var sid = item['spellid_' + s] || 0;
            var strig = item['spelltrigger_' + s] || 0;
            var scd = item['spellcooldown_' + s] || -1;
            var sch = item['spellcharges_' + s] || 0;
            if (sid > 0)
                h += buildSpellSlot(s, sid, strig, scd, sch);
        }
        h += '</div>';
        h += '<button type="button" class="btn-add-row" id="btnAddSpell"><i class="fa-solid fa-plus"></i> Add Spell Slot</button>';
        h += sectionEnd();

        // ── Section 5: Restrictions ──
        h += sectionStart('restrict', 'Restrictions', 'fa-lock', false);
        h += field('Required Level', '<input type="number" id="editFieldReqLevel" value="' + (item.required_level || 0) + '" min="0" max="60" />');
        h += field('Binding', buildBindingDropdown(item.bonding));
        h += '<div class="edit-field"><label>Allowed Classes</label>' + buildBitmaskGrid('class', WOW_CLASSES, item.allowable_class) + '</div>';
        h += '<div class="edit-field"><label>Allowed Races</label>' + buildBitmaskGrid('race', WOW_RACES, item.allowable_race) + '</div>';
        h += sectionEnd();

        // ── Section 6: Economics ──
        h += sectionStart('econ', 'Economics', 'fa-coins', false);
        h += field('Buy Price', buildPriceInputs('buy', item.buy_price || 0));
        h += field('Sell Price', buildPriceInputs('sell', item.sell_price || 0));
        h += '<div class="edit-field-inline">';
        h += field('Stack Size', '<input type="number" id="editFieldStackable" value="' + (item.stackable || 1) + '" min="1" />');
        h += field('Max Carry', '<input type="number" id="editFieldMaxCount" value="' + (item.max_count || 0) + '" min="0" />');
        h += '</div>';
        h += sectionEnd();

        // Delete button (only for custom items being edited, not clones)
        if (!editIsClone && editEntry >= CUSTOM_RANGE_START) {
            h += '<div style="margin-top: 16px; padding-top: 16px; border-top: 1px solid var(--border-light);">' +
                '<button type="button" class="btn-sm" id="btnDeleteItem" style="color: var(--status-error); background: none; border: 1px solid var(--status-error); border-radius: var(--radius-sm); padding: 4px 12px; font-size: 12px; cursor: pointer;">' +
                '<i class="fa-solid fa-trash"></i> Delete Item</button></div>';
        }

        $('#editFormContainer').html(h);

        // Update header icon
        $('#editHeaderIcon').attr('src', iconPath || '/icons/inv_misc_questionmark.png');

        // Load textures into edit form
        var editDisplayId = item.display_id || 0;
        if (editDisplayId > 0) {
            loadEditTextures(editDisplayId);
        }

        // Wire DPS preview
        updateDpsPreview();

        // Resolve spell names
        $('#spellSlotsContainer .spell-id-input').each(function () {
            var sid = parseInt($(this).val());
            if (sid > 0) resolveSpellName($(this).closest('.spell-slot-card'), sid);
        });
    }

    // ===================== FORM BUILDERS =====================

    function sectionStart(id, title, icon, open) {
        return '<div class="edit-section" data-section="' + id + '">' +
            '<div class="edit-section-header' + (open ? '' : ' collapsed') + '" data-target="' + id + '">' +
            '<i class="fa-solid ' + icon + '" style="color: var(--accent); font-size: 12px;"></i> ' + title +
            '<i class="fa-solid fa-chevron-down chevron"></i></div>' +
            '<div class="edit-section-body' + (open ? '' : ' collapsed') + '" data-body="' + id + '">';
    }

    function sectionEnd() {
        return '</div></div>';
    }

    function field(label, inner) {
        return '<div class="edit-field"><label>' + label + '</label>' + inner + '</div>';
    }

    function buildQualityDropdown(selected) {
        var h = '<select id="editFieldQuality">';
        for (var i = 0; i <= 6; i++) {
            h += '<option value="' + i + '" class="quality-option-' + i + '"' + (i === selected ? ' selected' : '') + '>' + QUALITY_NAMES[i] + '</option>';
        }
        return h + '</select>';
    }

    function buildClassDropdown(selected) {
        var h = '<select id="editFieldClass">';
        var keys = Object.keys(CLASS_NAMES).sort(function (a, b) { return +a - +b; });
        keys.forEach(function (k) {
            h += '<option value="' + k + '"' + (+k === selected ? ' selected' : '') + '>' + CLASS_NAMES[k] + '</option>';
        });
        return h + '</select>';
    }

    function buildSlotDropdown(selected) {
        var h = '<select id="editFieldSlot">';
        var keys = Object.keys(SLOT_NAMES).sort(function (a, b) { return +a - +b; });
        keys.forEach(function (k) {
            var label = SLOT_NAMES[k] || '(None)';
            h += '<option value="' + k + '"' + (+k === selected ? ' selected' : '') + '>' + label + '</option>';
        });
        return h + '</select>';
    }

    function buildBindingDropdown(selected) {
        var h = '<select id="editFieldBonding">';
        [0, 1, 2, 3, 4].forEach(function (v) {
            h += '<option value="' + v + '"' + (v === selected ? ' selected' : '') + '>' + BONDING_NAMES[v] + '</option>';
        });
        return h + '</select>';
    }

    function buildDmgTypeDropdown(index, selected) {
        var h = '<select id="editFieldDmgType' + index + '">';
        var keys = Object.keys(DMG_TYPE_NAMES).sort(function (a, b) { return +a - +b; });
        keys.forEach(function (k) {
            h += '<option value="' + k + '"' + (+k === selected ? ' selected' : '') + '>' + DMG_TYPE_NAMES[k] + '</option>';
        });
        return h + '</select>';
    }

    function buildIconPicker(iconPath, displayId) {
        return '<div class="icon-picker-trigger" id="iconPickerTrigger">' +
            '<img id="editIconPreview" src="' + esc(iconPath || '/icons/inv_misc_questionmark.png') + '" />' +
            '<div><div style="font-size: 13px; color: var(--text-primary);">Display ID: <span id="editDisplayIdLabel">' + (displayId || 0) + '</span></div>' +
            '<div class="change-text"><i class="fa-solid fa-images"></i> Change Icon</div></div>' +
            '<input type="hidden" id="editFieldDisplayId" value="' + (displayId || 0) + '" />' +
            '</div>';
    }

    function buildStatRow(index, statType, statValue) {
        return '<div class="stat-row" data-stat-index="' + index + '">' +
            '<select class="stat-type-select">' + buildStatTypeOptions(statType) + '</select>' +
            '<input type="number" class="stat-value-input" value="' + statValue + '" />' +
            '<button type="button" class="btn-remove-stat" title="Remove"><i class="fa-solid fa-xmark"></i></button>' +
            '</div>';
    }

    function buildStatTypeOptions(selected) {
        var h = '<option value="0">(None)</option>';
        var keys = Object.keys(STAT_TYPES).filter(function (k) { return +k > 0; }).sort(function (a, b) { return +a - +b; });
        keys.forEach(function (k) {
            h += '<option value="' + k + '"' + (+k === selected ? ' selected' : '') + '>' + STAT_TYPES[k] + '</option>';
        });
        return h;
    }

    function buildSpellSlot(index, spellId, trigger, cooldown, charges) {
        var cdSec = cooldown > 0 ? (cooldown / 1000).toFixed(0) : (cooldown === -1 ? '' : '0');
        return '<div class="spell-slot-card" data-spell-index="' + index + '">' +
            '<div class="spell-slot-header"><span>Spell Slot ' + index + '</span>' +
            '<button type="button" class="btn-remove-stat" title="Remove"><i class="fa-solid fa-xmark"></i></button></div>' +
            '<div class="edit-field-inline">' +
            '<div class="edit-field"><label>Spell ID</label><div class="d-flex gap-1">' +
            '<input type="number" class="spell-id-input" value="' + spellId + '" min="0" style="flex:1;" />' +
            '<a class="btn-sm btn-outline-subtle" title="Browse Spells" href="/Spells" target="_blank" style="flex-shrink:0; padding: 6px 8px;"><i class="fa-solid fa-magnifying-glass"></i></a>' +
            '</div><div class="spell-name-preview"></div></div>' +
            '<div class="edit-field"><label>Trigger</label><select class="spell-trigger-select">' + buildTriggerOptions(trigger) + '</select></div>' +
            '</div>' +
            '<div class="edit-field-inline" style="margin-top: 6px;">' +
            '<div class="edit-field"><label>Cooldown (sec)</label><input type="number" class="spell-cooldown-input" value="' + cdSec + '" min="0" placeholder="Use spell default" /></div>' +
            '<div class="edit-field"><label>Charges</label><input type="number" class="spell-charges-input" value="' + charges + '" /></div>' +
            '</div>' +
            '</div>';
    }

    function buildTriggerOptions(selected) {
        var h = '';
        var keys = Object.keys(TRIGGER_NAMES).sort(function (a, b) { return +a - +b; });
        keys.forEach(function (k) {
            h += '<option value="' + k + '"' + (+k === selected ? ' selected' : '') + '>' + TRIGGER_NAMES[k] + '</option>';
        });
        return h;
    }

    function buildBitmaskGrid(prefix, entries, value) {
        // value of -1 means "all allowed"
        var allSet = (value === -1 || value === undefined || value === null);
        var h = '<div style="margin-bottom: 4px;"><label style="display:flex; align-items:center; gap:5px; font-size:12px; font-weight:400; text-transform:none; letter-spacing:0; cursor:pointer;">' +
            '<input type="checkbox" class="bitmask-all" data-prefix="' + prefix + '"' + (allSet ? ' checked' : '') + ' /> <strong>All</strong></label></div>';
        h += '<div class="checkbox-grid">';
        entries.forEach(function (e) {
            var checked = allSet || ((value >> e.bit) & 1);
            h += '<label><input type="checkbox" class="bitmask-bit" data-prefix="' + prefix + '" data-bit="' + e.bit + '"' + (checked ? ' checked' : '') + ' /> ' + e.name + '</label>';
        });
        h += '</div>';
        return h;
    }

    function buildPriceInputs(prefix, copper) {
        var gold = Math.floor((copper || 0) / 10000);
        var silver = Math.floor(((copper || 0) % 10000) / 100);
        var cop = (copper || 0) % 100;
        return '<div class="price-inputs">' +
            '<div class="price-part"><input type="number" class="price-gold" data-prefix="' + prefix + '" value="' + gold + '" min="0" /><span class="coin-label coin-gold">g</span></div>' +
            '<div class="price-part"><input type="number" class="price-silver" data-prefix="' + prefix + '" value="' + silver + '" min="0" max="99" /><span class="coin-label coin-silver">s</span></div>' +
            '<div class="price-part"><input type="number" class="price-copper" data-prefix="' + prefix + '" value="' + cop + '" min="0" max="99" /><span class="coin-label coin-copper">c</span></div>' +
            '</div>';
    }

    // ===================== COLLECT FORM DATA =====================

    function collectFormData() {
        // Start with ALL original DB values as the base.
        // This ensures columns not represented in the form keep their original values
        // instead of being silently zeroed out.
        var data = {};

        if (editOriginalRow) {
            // Copy every column from the original row
            var keys = Object.keys(editOriginalRow);
            for (var k = 0; k < keys.length; k++) {
                data[keys[k]] = editOriginalRow[keys[k]];
            }
        }

        // Override entry (could be different if cloning)
        data.entry = editEntry;

        // ── Form overrides — only fields the UI actually controls ──

        // Identity
        data.name = $('#editFieldName').val() || 'Custom Item';
        data.quality = int('#editFieldQuality');
        data.display_id = int('#editFieldDisplayId');
        data['class'] = int('#editFieldClass');
        data.description = $('#editFieldDescription').val() || '';

        // Equipment & Stats
        data.inventory_type = int('#editFieldSlot');
        data.item_level = int('#editFieldItemLevel');
        data.armor = int('#editFieldArmor');

        // Resistances
        $('.editRes').each(function () {
            data[$(this).data('col')] = parseInt($(this).val()) || 0;
        });

        // Stats — collect in order from form rows
        var statIndex = 1;
        $('#statRowsContainer .stat-row').each(function () {
            var st = parseInt($(this).find('.stat-type-select').val()) || 0;
            var sv = parseInt($(this).find('.stat-value-input').val()) || 0;
            if (st > 0) {
                data['stat_type' + statIndex] = st;
                data['stat_value' + statIndex] = sv;
                statIndex++;
            }
        });
        // Zero out remaining stat slots (user removed them)
        for (var i = statIndex; i <= 10; i++) {
            data['stat_type' + i] = 0;
            data['stat_value' + i] = 0;
        }

        // Weapon
        data.dmg_min1 = intFloat('#editFieldDmgMin1');
        data.dmg_max1 = intFloat('#editFieldDmgMax1');
        data.dmg_type1 = int('#editFieldDmgType1');
        data.dmg_min2 = intFloat('#editFieldDmgMin2');
        data.dmg_max2 = intFloat('#editFieldDmgMax2');
        data.dmg_type2 = int('#editFieldDmgType2');
        var speed = parseFloat($('#editFieldSpeed').val()) || 2.0;
        data.delay = Math.round(speed * 1000);

        // Spells — only override slots that exist in the form
        for (var s = 1; s <= 5; s++) {
            var card = $('#spellSlotsContainer .spell-slot-card[data-spell-index="' + s + '"]');
            if (card.length) {
                data['spellid_' + s] = parseInt(card.find('.spell-id-input').val()) || 0;
                data['spelltrigger_' + s] = parseInt(card.find('.spell-trigger-select').val()) || 0;
                var cdVal = card.find('.spell-cooldown-input').val().trim();
                if (cdVal === '') {
                    // Empty = "use spell default" — preserve original DB value if we have it
                    var origCd = editOriginalRow ? editOriginalRow['spellcooldown_' + s] : null;
                    data['spellcooldown_' + s] = (origCd !== null && origCd !== undefined) ? origCd : -1;
                } else {
                    data['spellcooldown_' + s] = (parseInt(cdVal) || 0) * 1000;
                }
                data['spellcharges_' + s] = parseInt(card.find('.spell-charges-input').val()) || 0;
            } else {
                // Slot was removed or never added — zero it out only if the original had a spell here
                data['spellid_' + s] = 0;
                data['spelltrigger_' + s] = 0;
                // Preserve original cooldown if the original didn't have a spell either
                if (!editOriginalRow || !(editOriginalRow['spellid_' + s] > 0)) {
                    // No spell in original either — keep whatever original had
                    if (editOriginalRow && editOriginalRow['spellcooldown_' + s] !== undefined) {
                        data['spellcooldown_' + s] = editOriginalRow['spellcooldown_' + s];
                    } else {
                        data['spellcooldown_' + s] = 0;
                    }
                } else {
                    data['spellcooldown_' + s] = 0;
                }
                data['spellcharges_' + s] = 0;
            }
        }

        // Restrictions
        data.required_level = int('#editFieldReqLevel');
        data.bonding = int('#editFieldBonding');
        data.allowable_class = collectBitmask('class');
        data.allowable_race = collectBitmask('race');

        // Economics
        data.buy_price = collectPrice('buy');
        data.sell_price = collectPrice('sell');
        data.stackable = int('#editFieldStackable') || 1;
        data.max_count = int('#editFieldMaxCount');

        return data;
    }

    function int(sel) { return parseInt($(sel).val()) || 0; }
    function intFloat(sel) { return parseFloat($(sel).val()) || 0; }

    function collectBitmask(prefix) {
        if ($('.bitmask-all[data-prefix="' + prefix + '"]').is(':checked')) return -1;
        var val = 0;
        $('.bitmask-bit[data-prefix="' + prefix + '"]').each(function () {
            if ($(this).is(':checked')) val |= (1 << $(this).data('bit'));
        });
        return val || -1; // default to all if nothing checked
    }

    function collectPrice(prefix) {
        var g = parseInt($('.price-gold[data-prefix="' + prefix + '"]').val()) || 0;
        var s = parseInt($('.price-silver[data-prefix="' + prefix + '"]').val()) || 0;
        var c = parseInt($('.price-copper[data-prefix="' + prefix + '"]').val()) || 0;
        return g * 10000 + s * 100 + c;
    }

    // ===================== SAVE =====================

    function saveItem() {
        var data = collectFormData();

        if (!data.name || data.name.trim() === '') {
            showToast('Item name is required', 'error');
            return;
        }

        $('#btnSaveItem').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Saving...');

        $.ajax({
            url: '/Items/Save',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(data),
            success: function (result) {
                if (result.success) {
                    showToast(result.isInsert ? 'Item #' + data.entry + ' created!' : 'Item #' + data.entry + ' saved!', 'success');
                    var savedEntry = data.entry;
                    // Reset save button before closing
                    $('#btnSaveItem').prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save');
                    // Close editor and return to browse mode
                    closeEditPanel();
                    // Refresh the search results to reflect changes
                    doSearch(currentPage);
                    // Show the saved item in the detail panel
                    loadDetail(savedEntry);
                } else {
                    $('#btnSaveItem').prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save');
                    showToast('Save failed: ' + (result.error || 'Unknown error'), 'error');
                }
            },
            error: function () {
                $('#btnSaveItem').prop('disabled', false).html('<i class="fa-solid fa-floppy-disk"></i> Save');
                showToast('Save failed — server error', 'error');
            }
        });
    }

    function deleteItem() {
        if (!editEntry || editEntry < CUSTOM_RANGE_START) return;
        if (!confirm('Delete this custom item permanently? This cannot be undone.')) return;

        $.post('/Items/Delete', { entry: editEntry }, function (result) {
            if (result.success) {
                showToast('Item #' + editEntry + ' deleted', 'success');
                closeEditPanel();
                doSearch(currentPage);
                $('#detailContent').html('<div class="text-center text-muted p-3">Item deleted</div>');
                $('#detailActions').hide();
            } else {
                showToast('Delete failed: ' + (result.error || 'Unknown error'), 'error');
            }
        });
    }

    // ===================== ICON PICKER =====================

    function openIconPicker() {
        iconPickerPage = 1;
        iconPickerQuery = '';
        $('#iconPickerSearch').val('');
        loadIconPickerPage();
        new bootstrap.Modal($('#iconPickerModal')[0]).show();

        // Focus search after modal opens
        setTimeout(function () { $('#iconPickerSearch').focus(); }, 300);
    }

    function loadIconPickerPage() {
        var params = { q: iconPickerQuery, page: iconPickerPage, pageSize: 60 };
        $('#iconPickerGrid').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i></div>');

        $.getJSON('/Items/IconSearch', params, function (data) {
            $('#iconPickerInfo').text(data.totalCount + ' icons found');
            $('#iconPickerPageInfo').text(data.page + ' / ' + data.totalPages);
            $('#btnIconPrevPage').prop('disabled', data.page <= 1);
            $('#btnIconNextPage').prop('disabled', data.page >= data.totalPages);

            var currentDisplayId = parseInt($('#editFieldDisplayId').val()) || 0;
            var h = '';
            data.icons.forEach(function (icon) {
                var isSelected = icon.displayIds.indexOf(currentDisplayId) >= 0;
                h += '<div class="icon-picker-cell' + (isSelected ? ' selected' : '') + '" ' +
                    'data-icon-name="' + escAttr(icon.iconName) + '" ' +
                    'data-display-ids="' + escAttr(JSON.stringify(icon.displayIds)) + '" ' +
                    'title="' + escAttr(icon.iconName) + ' (IDs: ' + icon.displayIds.slice(0, 5).join(', ') + (icon.displayIds.length > 5 ? '...' : '') + ')">' +
                    '<img src="' + esc(icon.iconPath) + '" loading="lazy" />' +
                    '</div>';
            });

            if (data.icons.length === 0)
                h = '<div class="text-center text-muted p-4">No icons match your search</div>';

            $('#iconPickerGrid').html(h);
        });
    }

    function selectIcon(cell) {
        var displayIds = JSON.parse($(cell).attr('data-display-ids') || '[]');
        var iconName = $(cell).data('icon-name');
        if (displayIds.length === 0) return;

        // Use the first displayId
        var displayId = displayIds[0];
        var iconPath = '/icons/' + iconName + '.png';

        $('#editFieldDisplayId').val(displayId);
        $('#editDisplayIdLabel').text(displayId);
        $('#editIconPreview').attr('src', iconPath);
        $('#editHeaderIcon').attr('src', iconPath);

        // Refresh 3D model for new display ID
        checkItemModel(displayId);

        bootstrap.Modal.getInstance($('#iconPickerModal')[0]).hide();
    }

    function checkItemModel(displayId) {
        if (!displayId || displayId <= 0) {
            $('#editItemModelPreview').html('');
            return;
        }
        $.getJSON('/Items/ModelExists', { displayId: displayId }, function (data) {
            if (data.exists) {
                $('#editItemModelPreview').html(
                    '<div class="model-preview-container" style="height:180px;"><model-viewer src="' + esc(data.path) + '" auto-rotate camera-controls shadow-intensity="0.5" exposure="1.2" style="width:100%;height:100%;--poster-color:transparent;"></model-viewer></div>'
                );
            } else {
                $('#editItemModelPreview').html('');
            }
        });
    }

    // ===================== SPELL NAME RESOLUTION =====================

    function resolveSpellName(card, spellId) {
        if (!spellId || spellId <= 0) {
            card.find('.spell-name-preview').text('');
            return;
        }
        $.getJSON('/Spells/Detail', { entry: spellId }, function (data) {
            if (data.found) {
                var name = (data.item && data.item.name) || (data.spell && data.spell.name) || data.name || ('Spell #' + spellId);
                card.find('.spell-name-preview').text(name);
            } else {
                card.find('.spell-name-preview').text('Unknown spell');
            }
        }).fail(function () {
            card.find('.spell-name-preview').text('');
        });
    }

    // ===================== DPS PREVIEW =====================

    function updateDpsPreview() {
        var min = parseFloat($('#editFieldDmgMin1').val()) || 0;
        var max = parseFloat($('#editFieldDmgMax1').val()) || 0;
        var speed = parseFloat($('#editFieldSpeed').val()) || 2.0;
        if (speed > 0 && (min > 0 || max > 0)) {
            var dps = ((min + max) / 2) / speed;
            $('#dpsPreview').text(dps.toFixed(1) + ' DPS');
        } else {
            $('#dpsPreview').text('—');
        }
    }

    // ===================== HELPERS =====================

    function formatCopper(copper) {
        if (!copper || copper <= 0) return '0';
        var gold = Math.floor(copper / 10000);
        var silver = Math.floor((copper % 10000) / 100);
        var cop = copper % 100;
        var parts = [];
        if (gold > 0) parts.push(gold + 'g');
        if (silver > 0) parts.push(silver + 's');
        if (cop > 0) parts.push(cop + 'c');
        return parts.join(' ');
    }

    function esc(text) {
        if (text == null) return '';
        var div = document.createElement('div');
        div.textContent = String(text);
        return div.innerHTML;
    }

    function escAttr(text) {
        if (text == null) return '';
        return String(text).replace(/&/g, '&amp;').replace(/"/g, '&quot;').replace(/'/g, '&#39;').replace(/</g, '&lt;').replace(/>/g, '&gt;');
    }

    function showToast(msg, type) {
        var el = $('<div class="edit-toast ' + type + '">' + esc(msg) + '</div>');
        $('body').append(el);
        setTimeout(function () { el.fadeOut(300, function () { el.remove(); }); }, 3000);
    }

    // ===================== EVENTS =====================

    // Search
    $('#btnSearchItems').on('click', function () { doSearch(1); });
    $('#itemSearch').on('keydown', function (e) { if (e.key === 'Enter') doSearch(1); });
    $('#filterClass, #filterQuality, #filterSlot').on('change', function () { doSearch(1); });

    // Pagination
    $('#btnFirstPage').on('click', function () { doSearch(1); });
    $('#btnPrevPage').on('click', function () { if (currentPage > 1) doSearch(currentPage - 1); });
    $('#btnNextPage').on('click', function () { if (currentPage < totalPages) doSearch(currentPage + 1); });
    $('#btnLastPage').on('click', function () { doSearch(totalPages); });
    $('#pageJumpInput').on('keydown', function (e) {
        if (e.key === 'Enter') {
            var p = parseInt($(this).val()) || 1;
            p = Math.max(1, Math.min(p, totalPages));
            doSearch(p);
        }
    });
    $('#pageJumpInput').on('blur', function () {
        var p = parseInt($(this).val()) || 1;
        p = Math.max(1, Math.min(p, totalPages));
        if (p !== currentPage) doSearch(p);
    });
    $('#pageSizeSelect').val(currentPageSize);
    $('#pageSizeSelect').on('change', function () {
        currentPageSize = parseInt($(this).val()) || 50;
        try { localStorage.setItem('msui_items_pageSize', currentPageSize); } catch (e) { }
        doSearch(1);
    });

    // Item list click → detail
    $(document).on('click', '.item-row', function () {
        if (editMode) return; // Don't switch items while editing
        $('.item-row').removeClass('active');
        $(this).addClass('active');
        loadDetail($(this).data('entry'));
    });

    // ── Clone button ──
    $('#btnCloneItem').on('click', function () {
        if (!currentDetailEntry) return;
        openEditPanel(currentDetailEntry, true);
    });

    // ── Detail icon click → open edit ──
    $(document).on('click', '.detail-icon-lg', function () {
        if (!currentDetailEntry || editMode) return;
        var isCustom = currentDetailEntry >= CUSTOM_RANGE_START;
        openEditPanel(currentDetailEntry, !isCustom);
    });

    // ── Edit Original button ──
    $('#btnEditOriginal').on('click', function () {
        if (!currentDetailEntry) return;
        var isCustom = currentDetailEntry >= CUSTOM_RANGE_START;

        if (isCustom) {
            // Custom items can be edited directly — no confirmation needed
            openEditPanel(currentDetailEntry, false);
        } else {
            // Show confirmation modal for base game items
            $('#confirmItemName').text(currentDetailItem ? currentDetailItem.name : 'this item');
            $('#confirmItemEntry').text('(Entry #' + currentDetailEntry + ')');
            new bootstrap.Modal($('#editOriginalModal')[0]).show();
        }
    });

    // Confirmation modal — Clone Instead
    $('#btnConfirmCloneInstead').on('click', function () {
        bootstrap.Modal.getInstance($('#editOriginalModal')[0]).hide();
        openEditPanel(currentDetailEntry, true);
    });

    // Confirmation modal — Edit Original confirmed
    $('#btnConfirmEditOriginal').on('click', function () {
        bootstrap.Modal.getInstance($('#editOriginalModal')[0]).hide();
        openEditPanel(currentDetailEntry, false);
    });

    // ── Save / Cancel ──
    $('#btnSaveItem').on('click', saveItem);
    $('#btnCancelEdit').on('click', closeEditPanel);

    // ── Delete ──
    $(document).on('click', '#btnDeleteItem', deleteItem);

    // ── Section toggle ──
    $(document).on('click', '.edit-section-header', function () {
        var target = $(this).data('target');
        $(this).toggleClass('collapsed');
        $('[data-body="' + target + '"]').toggleClass('collapsed');
    });

    // ── Add stat row ──
    $(document).on('click', '#btnAddStat', function () {
        var count = $('#statRowsContainer .stat-row').length;
        if (count >= 10) { showToast('Maximum 10 stats', 'error'); return; }
        $('#statRowsContainer').append(buildStatRow(count + 1, 0, 0));
    });

    // ── Remove stat row ──
    $(document).on('click', '.stat-row .btn-remove-stat', function () {
        $(this).closest('.stat-row').remove();
    });

    // ── Add spell slot ──
    $(document).on('click', '#btnAddSpell', function () {
        var count = $('#spellSlotsContainer .spell-slot-card').length;
        if (count >= 5) { showToast('Maximum 5 spell slots', 'error'); return; }
        var nextIndex = count + 1;
        // Reindex: find next unused
        for (var i = 1; i <= 5; i++) {
            if ($('#spellSlotsContainer .spell-slot-card[data-spell-index="' + i + '"]').length === 0) {
                nextIndex = i;
                break;
            }
        }
        $('#spellSlotsContainer').append(buildSpellSlot(nextIndex, 0, 0, -1, 0));
    });

    // ── Remove spell slot ──
    $(document).on('click', '.spell-slot-card .btn-remove-stat', function () {
        $(this).closest('.spell-slot-card').remove();
    });

    // ── Spell ID change → resolve name ──
    $(document).on('change', '.spell-id-input', function () {
        var card = $(this).closest('.spell-slot-card');
        var sid = parseInt($(this).val()) || 0;
        resolveSpellName(card, sid);
    });

    // ── Bitmask "All" checkbox ──
    $(document).on('change', '.bitmask-all', function () {
        var prefix = $(this).data('prefix');
        var checked = $(this).is(':checked');
        $('.bitmask-bit[data-prefix="' + prefix + '"]').prop('checked', checked);
    });

    // ── Individual bitmask checkbox ──
    $(document).on('change', '.bitmask-bit', function () {
        var prefix = $(this).data('prefix');
        var total = $('.bitmask-bit[data-prefix="' + prefix + '"]').length;
        var checked = $('.bitmask-bit[data-prefix="' + prefix + '"]:checked').length;
        $('.bitmask-all[data-prefix="' + prefix + '"]').prop('checked', checked === total);
    });

    // ── Icon picker trigger ──
    $(document).on('click', '#iconPickerTrigger', openIconPicker);

    // ── Icon picker search ──
    var iconSearchTimer = null;
    $('#iconPickerSearch').on('input', function () {
        clearTimeout(iconSearchTimer);
        iconSearchTimer = setTimeout(function () {
            iconPickerQuery = $('#iconPickerSearch').val();
            iconPickerPage = 1;
            loadIconPickerPage();
        }, 300);
    });

    // ── Icon picker pagination ──
    $('#btnIconPrevPage').on('click', function () {
        if (iconPickerPage > 1) { iconPickerPage--; loadIconPickerPage(); }
    });
    $('#btnIconNextPage').on('click', function () {
        iconPickerPage++;
        loadIconPickerPage();
    });

    // ── Icon picker selection ──
    $(document).on('click', '.icon-picker-cell', function () {
        selectIcon(this);
    });

    // ── DPS live update ──
    $(document).on('input', '#editFieldDmgMin1, #editFieldDmgMax1, #editFieldSpeed', updateDpsPreview);

    // ── Check for 3D model button ──
    $(document).on('click', '#btnCheckItemModel', function () {
        var did = parseInt($('#editFieldDisplayId').val()) || 0;
        if (did <= 0) { showToast('No Display ID set', 'error'); return; }
        checkItemModel(did);
    });

    // ── Apply retextured displayId to item in edit mode ──
    $(document).on('click', '#btnApplyNewDisplayId', function () {
        var newDid = $(this).data('did');
        if (!newDid || !editMode) return;

        $('#editFieldDisplayId').val(newDid);
        // Update the icon picker display too
        var $trigger = $('.icon-picker-trigger');
        if ($trigger.length) {
            $trigger.find('.change-text').text('Display ID: ' + newDid);
        }

        showToast('Display ID updated to ' + newDid + ' — Save to apply', 'success');
        $('#retextureOverlay').remove();

        // Reload textures and model preview for the new displayId
        loadEditTextures(newDid);
    });

    // ── Changelog toggle ──
    $('#itemChangelogToggle').on('click', function () {
        $(this).toggleClass('collapsed');
        $('#itemChangelogBody').toggleClass('collapsed');
    });

    // ── Texture panel toggle ──
    $('#itemTextureToggle').on('click', function () {
        $(this).toggleClass('collapsed');
        $('#itemTextureBody').toggleClass('collapsed');
    });

    // ── Reset to OG ──
    $('#btnResetItemOG').on('click', function () {
        if (!currentDetailEntry || currentDetailEntry >= CUSTOM_RANGE_START) return;
        BaselineSystem.resetItem(currentDetailEntry, function (success) {
            if (success) {
                loadDetail(currentDetailEntry);
                doSearch(currentPage);
            }
        });
    });

    // ── Name change → update header ──
    $(document).on('input', '#editFieldName', function () {
        $('#editHeaderName').text($(this).val() || 'New Item');
    });

    // ── Quality change → update header color ──
    $(document).on('change', '#editFieldQuality', function () {
        var q = parseInt($(this).val()) || 0;
        $('#editHeaderName').css('color', QUALITY_COLORS[q] || 'inherit');
    });

    // ===================== INIT =====================
    doSearch(1);

});
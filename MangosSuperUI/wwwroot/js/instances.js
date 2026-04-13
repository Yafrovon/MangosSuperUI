// MangosSuperUI — Instance Loot Explorer JS

$(function () {

    var currentMapId = null;
    var currentCreatureEntry = null;
    var currentLootData = null;

    // Item picker state
    var itemPickerPage = 1;
    var itemPickerQuery = '';
    var itemPickerQuality = '';
    var pendingAddItem = null;   // { entry, name, quality, iconPath }
    var pendingRemoveData = null; // { entry, item, groupId, patchMin, patchMax, source, itemName }

    var QUALITY_NAMES = ['Poor', 'Common', 'Uncommon', 'Rare', 'Epic', 'Legendary', 'Artifact'];

    // ===================== BASELINE INTEGRATION =====================

    BaselineSystem.checkStatus(function (status) {
        BaselineSystem.renderWarningBanner('#baselineWarning');
    });

    $(document).on('baseline:initialized', function () {
        if (currentCreatureEntry) loadLootChangelog(currentCreatureEntry);
    });

    var RANK_ICONS = {
        0: '<i class="fa-solid fa-circle boss-rank-0" title="Normal"></i>',
        1: '<i class="fa-solid fa-star boss-rank-1" title="Elite"></i>',
        2: '<i class="fa-solid fa-star boss-rank-2" title="Rare Elite"></i>',
        3: '<i class="fa-solid fa-skull boss-rank-3" title="Boss"></i>',
        4: '<i class="fa-solid fa-diamond boss-rank-2" title="Rare"></i>'
    };

    // ===================== LOAD INSTANCES =====================

    function loadInstances() {
        $.getJSON('/Instances/List', function (data) {
            var dungeons = data.filter(function (i) { return i.category === 'dungeon'; });
            var raids = data.filter(function (i) { return i.category === 'raid'; });

            var h = '';

            h += '<div class="inst-category-header"><i class="fa-solid fa-dungeon"></i> Dungeons</div>';
            dungeons.forEach(function (inst) {
                h += buildInstRow(inst);
            });

            h += '<div class="inst-category-header"><i class="fa-solid fa-dragon"></i> Raids</div>';
            raids.forEach(function (inst) {
                h += buildInstRow(inst);
            });

            $('#instanceListContainer').html(h);
        });
    }

    function buildInstRow(inst) {
        return '<div class="inst-row" data-map="' + inst.mapId + '">' +
            '<div style="flex:1; min-width:0;">' +
            '<div class="inst-name">' + esc(inst.name) + '</div>' +
            '<div class="inst-meta">Level ' + inst.levelRange + '</div>' +
            '</div>' +
            '<span class="inst-badge">' + inst.bossCount + ' bosses</span>' +
            '</div>';
    }

    // ===================== LOAD BOSSES =====================

    function loadBosses(mapId) {
        currentMapId = mapId;
        currentCreatureEntry = null;

        var showTrash = $('#chkShowTrash').is(':checked');

        $('#bossListContainer').html('<div class="text-center p-3"><i class="fa-solid fa-spinner fa-spin"></i></div>');
        $('#lootContainer').html('<div class="text-center p-4 text-muted" style="font-size:13px;"><i class="fa-solid fa-scroll" style="font-size:24px;margin-bottom:8px;display:block;color:var(--border);"></i>Select a boss to view their loot table</div>');
        $('#lootActions').hide();

        $.getJSON('/Instances/Creatures', { mapId: mapId, showTrash: showTrash }, function (data) {
            $('#bossListTitle').html('<i class="fa-solid fa-skull" style="color:var(--accent);"></i> ' + esc(data.instanceName));

            var h = '';

            // Bosses (curated, ordered)
            if (data.bosses && data.bosses.length > 0) {
                data.bosses.forEach(function (c) {
                    h += buildBossRow(c, true);
                });
            } else {
                h += '<div class="text-center p-3 text-muted" style="font-size:12px;">No boss data for this instance</div>';
            }

            // Trash (from DB, only if toggled)
            if (data.trash && data.trash.length > 0) {
                h += '<div style="padding: 6px 14px; font-size: 10px; font-weight: 700; color: var(--text-muted); text-transform: uppercase; border-top: 1px solid var(--border); border-bottom: 1px solid var(--border-light); background: var(--bg-input);">' +
                    '<i class="fa-solid fa-users"></i> Trash Mobs (' + data.trash.length + ')</div>';
                data.trash.forEach(function (c) {
                    h += buildBossRow(c, false);
                });
            }

            $('#bossListContainer').html(h);
            $('#instanceMultFooter').show();

            // Show instance reset button if baseline is initialized
            if (BaselineSystem.isInitialized()) {
                $('#btnResetInstanceOG').show();
            }
        });
    }

    // ===================== LOOT CHANGELOG =====================

    function loadLootChangelog(creatureEntry) {
        if (!BaselineSystem.isInitialized()) {
            $('#lootChangelogPanel').hide();
            $('#lootResetContainer').hide();
            return;
        }

        BaselineSystem.loadCreatureLootDiff(creatureEntry, '#lootChangelogContent', function (data) {
            if (!data || !data.available || !data.hasLoot) {
                $('#lootChangelogPanel').hide();
                $('#lootResetContainer').hide();
                return;
            }

            $('#lootChangelogPanel').show();

            if (data.isModified) {
                var count = data.totalChanges || 0;
                $('#lootChangeCount').text(count).removeClass('clean');
                $('#lootResetContainer').show();
            } else {
                $('#lootChangeCount').text('0').addClass('clean');
                $('#lootResetContainer').hide();
            }
        });
    }

    function buildBossRow(c, isBoss) {
        var rankIcon = RANK_ICONS[c.rank] || RANK_ICONS[0];
        var levelText = c.level_min === c.level_max ? c.level_min : c.level_min + '-' + c.level_max;
        var optBadge = c.optional ? '<span style="font-size:9px;color:var(--text-muted);font-style:italic;margin-left:4px;">optional</span>' : '';
        var lootCount = c.lootRowCount || 0;
        var noLoot = c.loot_id === 0;

        return '<div class="boss-row' + (noLoot ? ' no-loot' : '') + '" data-entry="' + c.entry + '"' + (noLoot ? ' style="opacity:0.4;pointer-events:none;"' : '') + '>' +
            '<div class="boss-rank-icon">' + (isBoss ? '<i class="fa-solid fa-skull boss-rank-3"></i>' : rankIcon) + '</div>' +
            '<div class="boss-info">' +
            '<div class="boss-name">' + esc(c.name) + optBadge + '</div>' +
            '<div class="boss-level">Level ' + levelText + '</div>' +
            '</div>' +
            (lootCount > 0 ? '<span class="boss-loot-count">' + lootCount + '</span>' : '') +
            '</div>';
    }

    // ===================== LOAD LOOT TABLE =====================

    function loadLoot(creatureEntry) {
        currentCreatureEntry = creatureEntry;

        $('#lootContainer').html('<div class="text-center p-3"><i class="fa-solid fa-spinner fa-spin"></i></div>');

        $.getJSON('/Instances/Loot', { creatureEntry: creatureEntry }, function (data) {
            if (!data.found) {
                $('#lootContainer').html('<div class="text-center p-3 text-muted">Creature not found</div>');
                return;
            }

            currentLootData = data;
            var creature = data.creature;

            $('#lootTitle').html('<i class="fa-solid fa-scroll" style="color:var(--accent);"></i> ' + esc(creature.name));
            $('#lootActions').show();

            var h = '';

            // Direct items
            if (data.directItems.length > 0) {
                h += '<div class="loot-section-header">Direct Drops <span style="font-weight:400; color:var(--text-muted);">(' + data.directItems.length + ')</span></div>';

                // Calculate effective chances for grouped direct items
                var directGroups = {};
                data.directItems.forEach(function (item) {
                    var gid = item.groupId || 0;
                    if (!directGroups[gid]) directGroups[gid] = [];
                    directGroups[gid].push(item);
                });

                var directEffective = {};
                Object.keys(directGroups).forEach(function (gid) {
                    var items = directGroups[gid];
                    if (parseInt(gid) === 0) {
                        items.forEach(function (item) {
                            directEffective[item.itemEntry] = null; // show raw chance
                        });
                    } else {
                        var explicitTotal = 0;
                        var zeroCount = 0;
                        items.forEach(function (item) {
                            var abs = Math.abs(item.chance);
                            if (abs > 0) explicitTotal += abs;
                            else zeroCount++;
                        });
                        var remaining = Math.max(0, 100 - explicitTotal);
                        var equalShare = zeroCount > 0 ? remaining / zeroCount : 0;
                        items.forEach(function (item) {
                            var abs = Math.abs(item.chance);
                            directEffective[item.itemEntry] = abs > 0 ? abs : equalShare;
                        });
                    }
                });

                data.directItems.forEach(function (item) {
                    var eff = directEffective[item.itemEntry];
                    h += buildLootRow(item, data.lootId, eff);
                });
            }

            // Reference groups
            if (data.referenceGroups.length > 0) {
                data.referenceGroups.forEach(function (refGroup) {
                    h += buildRefGroupHeader(refGroup, data.lootId);

                    // Calculate effective chances for grouped items
                    var groupedItems = {};
                    refGroup.items.forEach(function (item) {
                        var gid = item.groupId || 0;
                        if (!groupedItems[gid]) groupedItems[gid] = [];
                        groupedItems[gid].push(item);
                    });

                    // For each group, calculate effective chances
                    var effectiveChances = {};
                    Object.keys(groupedItems).forEach(function (gid) {
                        var items = groupedItems[gid];
                        if (parseInt(gid) === 0) {
                            // groupid 0 = independent rolls, chance is as-is
                            items.forEach(function (item) {
                                effectiveChances[item.itemEntry] = Math.abs(item.chance);
                            });
                        } else {
                            // groupid > 0 = competing pool
                            // Items with explicit chance roll first
                            // Remaining % split equally among chance=0 items
                            var explicitTotal = 0;
                            var zeroCount = 0;
                            items.forEach(function (item) {
                                if (item.chance > 0) explicitTotal += item.chance;
                                else if (item.chance === 0) zeroCount++;
                            });
                            var remaining = Math.max(0, 100 - explicitTotal);
                            var equalShare = zeroCount > 0 ? remaining / zeroCount : 0;

                            items.forEach(function (item) {
                                if (item.chance > 0) {
                                    effectiveChances[item.itemEntry] = item.chance;
                                } else {
                                    effectiveChances[item.itemEntry] = equalShare;
                                }
                            });
                        }
                    });

                    refGroup.items.forEach(function (item) {
                        var eff = effectiveChances[item.itemEntry];
                        h += buildLootRow(item, refGroup.refEntry, eff);
                    });
                });
            }

            if (data.directItems.length === 0 && data.referenceGroups.length === 0) {
                h = '<div class="text-center p-4 text-muted">No loot data</div>';
            }

            $('#lootContainer').html(h);

            // Show add-item button
            $('#addItemFooter').show();

            // Load OG loot changelog
            loadLootChangelog(creatureEntry);
        });
    }

    function buildLootRow(item, lootEntry, effectiveChance) {
        var qualityClass = 'quality-' + item.quality;
        var absChance = Math.abs(item.chance);
        var isQuest = item.isQuest || item.chance < 0;
        var isEqualWeight = absChance === 0 && item.groupId > 0;

        // Input shows the raw DB value (what gets saved)
        // For equal-weight items, show 0 but with a tooltip showing effective %
        var chanceDisplay = formatChance(absChance);
        var chanceTitle = '';
        var effectiveSuffix = '';
        if (isEqualWeight && effectiveChance !== undefined && effectiveChance !== null) {
            chanceTitle = ' title="Currently equal weight in group — effective ~' + formatChance(effectiveChance) + '%. Enter a value to set an explicit rate."';
            chanceDisplay = '0';
            effectiveSuffix = '<span style="font-size:9px;color:var(--text-muted);white-space:nowrap;margin-left:1px;" title="Effective chance in group">≈' + formatChance(effectiveChance) + '</span>';
        }

        var entryAttr = ' data-entry="' + lootEntry + '"';
        var itemAttr = ' data-item="' + item.itemEntry + '"';
        var groupAttr = ' data-groupid="' + item.groupId + '"';
        var patchMinAttr = item.patchMin !== undefined ? ' data-patchmin="' + item.patchMin + '"' : ' data-patchmin="0"';
        var patchMaxAttr = item.patchMax !== undefined ? ' data-patchmax="' + item.patchMax + '"' : ' data-patchmax="10"';
        var sourceAttr = ' data-source="' + (item.source || 'direct') + '"';
        var nameAttr = ' data-itemname="' + escAttr(item.itemName) + '"';
        var origChance = ' data-origchance="' + item.chance + '"';
        var origMin = ' data-origmin="' + item.minCount + '"';
        var origMax = ' data-origmax="' + item.maxCount + '"';

        // Group badge
        var groupBadge = '';
        if (item.groupId > 0) {
            groupBadge = '<span class="loot-group-badge" title="Group ' + item.groupId + ' — one item selected from this group">G' + item.groupId + '</span>';
        }

        return '<div class="loot-item-row"' + entryAttr + itemAttr + groupAttr + patchMinAttr + patchMaxAttr + sourceAttr + nameAttr + origChance + origMin + origMax + '>' +
            '<img class="loot-item-icon" src="' + esc(item.iconPath) + '" loading="lazy" />' +
            '<div class="loot-item-info">' +
            '<span class="loot-item-name ' + qualityClass + '">' + esc(item.itemName) +
            (isQuest ? '<span class="loot-quest-badge">QUEST</span>' : '') +
            groupBadge +
            '</span>' +
            '</div>' +
            '<div class="loot-fields">' +
            '<div class="loot-chance-wrap">' +
            '<input type="text" class="loot-edit-field loot-chance-field" value="' + chanceDisplay + '"' + chanceTitle + ' />' +
            '</div>' +
            '<span class="loot-field-unit">%</span>' +
            effectiveSuffix +
            '<input type="number" class="loot-edit-field loot-count-field loot-max-field" value="' + item.maxCount + '" min="1" title="Drop count" />' +
            '<span class="loot-field-unit">×</span>' +
            '</div>' +
            '<button class="loot-save-btn" title="Save changes"><i class="fa-solid fa-check"></i></button>' +
            '<button class="loot-remove-btn" title="Remove from loot table"><i class="fa-solid fa-trash-can"></i></button>' +
            '</div>';
    }

    function buildRefGroupHeader(refGroup, creatureLootId) {
        var chanceVal = refGroup.refChance;
        var picksVal = refGroup.refMaxCount;

        return '<div class="loot-section-header ref-header-row"' +
            ' data-entry="' + creatureLootId + '"' +
            ' data-item="' + refGroup.pointerItem + '"' +
            ' data-groupid="' + refGroup.pointerGroupId + '"' +
            ' data-patchmin="' + refGroup.patchMin + '"' +
            ' data-patchmax="' + refGroup.patchMax + '"' +
            ' data-origchance="' + chanceVal + '"' +
            ' data-origpicks="' + picksVal + '"' +
            ' data-source="direct"' +
            ' data-itemname="Reference Pool #' + refGroup.refEntry + '"' +
            '>' +
            '<div style="flex:1; min-width:0;">' +
            '<span>Shared Pool #' + refGroup.refEntry + '</span>' +
            ' <span style="font-weight:400; color:var(--text-muted);">(' + refGroup.itemCount + ' items)</span>' +
            '</div>' +
            '<div class="d-flex align-items-center gap-3">' +
            '<div class="loot-field-group">' +
            '<span class="loot-field-label">Roll %</span>' +
            '<input type="text" class="loot-edit-field ref-chance-field" value="' + formatChance(chanceVal) + '" style="width:52px;" />' +
            '</div>' +
            '<div class="loot-field-group">' +
            '<span class="loot-field-label">Picks</span>' +
            '<input type="number" class="loot-edit-field ref-picks-field" value="' + picksVal + '" min="1" max="20" style="width:42px;text-align:center;" />' +
            '</div>' +
            '<button class="loot-save-btn ref-save-btn" title="Save pool settings" style="opacity:0;"><i class="fa-solid fa-check"></i></button>' +
            '</div>' +
            '</div>';
    }

    // ===================== SAVE SINGLE ROW =====================

    function saveRefHeader(headerEl) {
        var $h = $(headerEl);
        var origChance = parseFloat($h.data('origchance'));
        var origPicks = parseInt($h.data('origpicks'));

        var newChance = parseFloat($h.find('.ref-chance-field').val());
        var newPicks = parseInt($h.find('.ref-picks-field').val());

        var payload = {
            entry: parseInt($h.data('entry')),
            item: parseInt($h.data('item')),
            groupId: parseInt($h.data('groupid')),
            patchMin: parseInt($h.data('patchmin')),
            patchMax: parseInt($h.data('patchmax')),
            source: 'direct',
            itemName: $h.data('itemname')
        };

        var hasChange = false;
        if (!isNaN(newChance) && newChance !== origChance) {
            payload.newChance = newChance;
            hasChange = true;
        }
        if (!isNaN(newPicks) && newPicks !== origPicks) {
            payload.newMaxCount = newPicks;
            hasChange = true;
        }

        if (!hasChange) {
            showToast('No changes to save', 'error');
            return;
        }

        var $btn = $h.find('.ref-save-btn');
        $btn.html('<i class="fa-solid fa-spinner fa-spin"></i>');

        $.ajax({
            url: '/Instances/UpdateLoot',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (result) {
                if (result.success) {
                    showToast('Pool settings updated', 'success');
                    $h.data('origchance', newChance || origChance);
                    $h.data('origpicks', newPicks || origPicks);
                    $h.find('.ref-chance-field, .ref-picks-field').removeClass('changed');
                    $h.removeClass('dirty');
                    $btn.css('opacity', '0').html('<i class="fa-solid fa-check"></i>');
                } else {
                    showToast('Save failed: ' + (result.error || 'Unknown'), 'error');
                    $btn.html('<i class="fa-solid fa-check"></i>');
                }
            },
            error: function () {
                showToast('Save failed — server error', 'error');
                $btn.html('<i class="fa-solid fa-check"></i>');
            }
        });
    }

    function saveRow(row) {
        var $row = $(row);
        var origChance = parseFloat($row.data('origchance'));
        var origMax = parseInt($row.data('origmax'));

        var chanceVal = $row.find('.loot-chance-field').val();
        var newChance = $row.find('.loot-chance-field').prop('disabled') ? null : parseFloat(chanceVal);
        var newMax = parseInt($row.find('.loot-max-field').val());

        // Check if anything changed
        var payload = {
            entry: parseInt($row.data('entry')),
            item: parseInt($row.data('item')),
            groupId: parseInt($row.data('groupid')),
            patchMin: parseInt($row.data('patchmin')),
            patchMax: parseInt($row.data('patchmax')),
            source: $row.data('source'),
            itemName: $row.data('itemname')
        };

        var hasChange = false;
        if (newChance !== null && !isNaN(newChance) && newChance !== Math.abs(origChance)) {
            // Preserve quest sign
            payload.newChance = origChance < 0 ? -newChance : newChance;
            hasChange = true;
        }
        if (!isNaN(newMax) && newMax !== origMax) {
            payload.newMaxCount = newMax;
            hasChange = true;
        }

        if (!hasChange) {
            showToast('No changes to save', 'error');
            return;
        }

        var $btn = $row.find('.loot-save-btn');
        $btn.html('<i class="fa-solid fa-spinner fa-spin"></i>');

        $.ajax({
            url: '/Instances/UpdateLoot',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (result) {
                if (result.success) {
                    showToast(payload.itemName + ' updated', 'success');
                    // Update orig values so dirty detection resets
                    if (payload.newChance !== undefined) {
                        $row.data('origchance', payload.newChance);
                    }
                    if (payload.newMinCount !== undefined) $row.data('origmin', payload.newMinCount);
                    if (payload.newMaxCount !== undefined) $row.data('origmax', payload.newMaxCount);
                    $row.removeClass('dirty');
                    $row.find('.loot-edit-field').removeClass('changed');
                    $btn.html('<i class="fa-solid fa-check"></i>');
                } else {
                    showToast('Save failed: ' + (result.error || 'Unknown'), 'error');
                    $btn.html('<i class="fa-solid fa-check"></i>');
                }
            },
            error: function () {
                showToast('Save failed — server error', 'error');
                $btn.html('<i class="fa-solid fa-check"></i>');
            }
        });
    }

    // ===================== BOSS MULTIPLIER =====================

    function applyBossMultiplier(creatureEntry, multiplier) {
        if (!confirm('Apply ' + multiplier + '× to all non-guaranteed drops for this boss?')) return;

        $.ajax({
            url: '/Instances/MultiplyCreatureLoot',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({ creatureEntry: creatureEntry, multiplier: multiplier }),
            success: function (result) {
                if (result.success) {
                    showToast(result.creatureName + ': ' + result.totalUpdated + ' drops × ' + multiplier, 'success');
                    loadLoot(creatureEntry); // Refresh
                } else {
                    showToast('Failed: ' + (result.error || 'Unknown'), 'error');
                }
            },
            error: function () {
                showToast('Failed — server error', 'error');
            }
        });
    }

    function applyInstanceMultiplier(multiplier) {
        if (!currentMapId) return;

        // Get all boss entries currently shown
        var entries = [];
        $('#bossListContainer .boss-row').each(function () {
            entries.push(parseInt($(this).data('entry')));
        });

        if (entries.length === 0) return;
        if (!confirm('Apply ' + multiplier + '× to all non-guaranteed drops for ' + entries.length + ' creatures in this instance?')) return;

        var done = 0;
        var total = entries.length;
        var totalUpdated = 0;

        entries.forEach(function (entry) {
            $.ajax({
                url: '/Instances/MultiplyCreatureLoot',
                method: 'POST',
                contentType: 'application/json',
                data: JSON.stringify({ creatureEntry: entry, multiplier: multiplier }),
                success: function (result) {
                    if (result.success) totalUpdated += result.totalUpdated;
                    done++;
                    if (done === total) {
                        showToast(totalUpdated + ' drops updated across ' + total + ' creatures', 'success');
                        if (currentCreatureEntry) loadLoot(currentCreatureEntry);
                    }
                },
                error: function () {
                    done++;
                    if (done === total) {
                        showToast(totalUpdated + ' drops updated (some failed)', 'error');
                    }
                }
            });
        });
    }

    // ===================== HELPERS =====================

    function formatChance(val) {
        if (val === 0) return '0';
        if (val >= 10) return val.toFixed(1);
        if (val >= 1) return val.toFixed(2);
        if (val >= 0.1) return val.toFixed(3);
        return val.toFixed(4);
    }

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
        var el = $('<div class="inst-toast ' + type + '">' + esc(msg) + '</div>');
        $('body').append(el);
        setTimeout(function () { el.fadeOut(300, function () { el.remove(); }); }, 3500);
    }

    // ===================== ITEM PICKER (for Add Item) =====================

    var itemSearchTimer = null;

    function openItemPicker() {
        itemPickerPage = 1;
        itemPickerQuery = '';
        itemPickerQuality = '';
        pendingAddItem = null;
        $('#itemPickerSearch').val('');
        $('#itemPickerQuality').val('');
        $('#itemPickerResults').html('<div class="text-center p-4 text-muted">Search for items to add</div>');
        $('#itemPickerInfo').text('');
        $('#itemPickerPageInfo').text('');
        var modalEl = document.getElementById('itemPickerModal');
        new bootstrap.Modal(modalEl).show();
        setTimeout(function () { $('#itemPickerSearch').focus(); }, 300);
    }

    function loadItemPickerPage() {
        var params = { q: itemPickerQuery, page: itemPickerPage, pageSize: 30 };
        if (itemPickerQuality !== '') params.qualityFilter = parseInt(itemPickerQuality);

        $('#itemPickerResults').html('<div class="text-center p-4"><i class="fa-solid fa-spinner fa-spin"></i></div>');

        $.getJSON('/Instances/SearchItems', params, function (data) {
            $('#itemPickerInfo').text(data.totalCount.toLocaleString() + ' items');
            $('#itemPickerPageInfo').text(data.page + ' / ' + data.totalPages);
            $('#btnItemPickerPrev').prop('disabled', data.page <= 1);
            $('#btnItemPickerNext').prop('disabled', data.page >= data.totalPages);

            if (!data.items || data.items.length === 0) {
                $('#itemPickerResults').html('<div class="text-center text-muted p-4">No items found</div>');
                return;
            }

            var icons = data.icons || {};
            var h = '';
            data.items.forEach(function (item) {
                var iconPath = icons[item.displayId] || '/icons/inv_misc_questionmark.png';
                var qClass = 'quality-' + item.quality;
                var qName = QUALITY_NAMES[item.quality] || '';

                h += '<div class="ip-row" data-entry="' + item.entry + '" data-name="' + escAttr(item.name) + '" data-quality="' + item.quality + '" data-icon="' + escAttr(iconPath) + '">' +
                    '<img class="ip-icon" src="' + esc(iconPath) + '" loading="lazy" />' +
                    '<div style="flex:1;min-width:0;">' +
                    '<div class="ip-name ' + qClass + '">' + esc(item.name) + '</div>' +
                    '<div class="ip-meta">' + qName + ' &middot; iLvl ' + (item.itemLevel || 0) +
                    (item.requiredLevel > 0 ? ' &middot; Req ' + item.requiredLevel : '') + '</div>' +
                    '</div>' +
                    '<div class="ip-id">#' + item.entry + '</div></div>';
            });

            $('#itemPickerResults').html(h);
        }).fail(function () {
            $('#itemPickerResults').html('<div class="text-center text-muted p-4">Search failed</div>');
        });
    }

    function selectItemForAdd(entry, name, quality, iconPath) {
        pendingAddItem = { entry: entry, name: name, quality: quality, iconPath: iconPath };

        // Close the picker
        var pickerEl = document.getElementById('itemPickerModal');
        if (pickerEl) {
            var inst = bootstrap.Modal.getInstance(pickerEl);
            if (inst) inst.hide();
        }

        // Fill the config modal
        var qClass = 'quality-' + quality;
        var qName = QUALITY_NAMES[quality] || '';
        $('#addItemIcon').attr('src', iconPath);
        $('#addItemName').html('<span class="' + qClass + '">' + esc(name) + '</span>');
        $('#addItemMeta').text(qName + ' — #' + entry);
        $('#addItemChance').val(10);
        $('#addItemMaxCount').val(1);
        $('#addItemGroupId').val('0');
        $('#groupBalancePanel').hide();

        // Populate target dropdown: Direct + any reference pools from current boss
        var $target = $('#addItemTarget');
        $target.empty();
        $target.append('<option value="direct">Direct Drops (creature_loot_template)</option>');

        if (currentLootData && currentLootData.referenceGroups) {
            currentLootData.referenceGroups.forEach(function (rg) {
                $target.append(
                    '<option value="ref:' + rg.refEntry + '">Shared Pool #' + rg.refEntry +
                    ' (' + rg.itemCount + ' items, ' + formatChance(rg.refChance) + '% roll, ' + rg.refMaxCount + ' picks)</option>'
                );
            });
        }

        // Open config modal
        setTimeout(function () {
            new bootstrap.Modal(document.getElementById('addItemConfigModal')).show();
        }, 300);
    }

    // ===================== GROUP BALANCE PREVIEW =====================

    function loadGroupBalance() {
        var groupId = parseInt($('#addItemGroupId').val());
        if (groupId === 0 || !currentLootData) {
            $('#groupBalancePanel').hide();
            return;
        }

        // Determine the target table and entry
        var targetVal = $('#addItemTarget').val();
        var lootEntry, source;
        if (targetVal && targetVal.indexOf('ref:') === 0) {
            lootEntry = parseInt(targetVal.replace('ref:', ''));
            source = 'reference';
        } else {
            lootEntry = currentLootData.lootId;
            source = 'direct';
        }

        $('#groupBalancePanel').show();
        $('#groupBalanceContent').html('<div class="text-center p-2"><i class="fa-solid fa-spinner fa-spin"></i></div>');

        $.getJSON('/Instances/GroupInfo', { lootEntry: lootEntry, groupId: groupId, source: source }, function (data) {
            var newChance = parseFloat($('#addItemChance').val()) || 0;
            var h = '';

            if (data.items && data.items.length > 0) {
                data.items.forEach(function (item) {
                    h += '<div class="gb-row">' +
                        '<img class="gb-icon" src="' + esc(item.iconPath) + '" />' +
                        '<span class="gb-name quality-' + item.quality + '">' + esc(item.itemName) + '</span>' +
                        '<span class="gb-pct">' + formatChance(item.effectiveChance) + '%</span>' +
                        '<div class="gb-bar-wrap"><div class="gb-bar" style="width:' + item.effectiveChance + '%;"></div></div>' +
                        '</div>';
                });
            } else {
                h += '<div class="text-muted" style="font-size: 11px;">No items in this group yet — your item will be the first.</div>';
            }

            // Show the new item preview
            if (pendingAddItem) {
                h += '<div class="gb-row gb-new">' +
                    '<img class="gb-icon" src="' + esc(pendingAddItem.iconPath) + '" />' +
                    '<span class="gb-name">' + esc(pendingAddItem.name) + ' (new)</span>' +
                    '<span class="gb-pct">' + (newChance > 0 ? formatChance(newChance) + '%' : 'equal') + '</span>' +
                    '<div class="gb-bar-wrap"><div class="gb-bar" style="width:' + newChance + '%; background: var(--accent);"></div></div>' +
                    '</div>';
            }

            $('#groupBalanceContent').html(h);

            // Warning if total explicit exceeds 100
            var totalExplicit = (data.totalExplicit || 0) + newChance;
            if (totalExplicit > 100) {
                $('#groupBalanceWarning').show().find('span').text(
                    'Total explicit chances = ' + formatChance(totalExplicit) + '% (exceeds 100%). Consider reducing some values or using 0 for equal weighting.');
            } else {
                $('#groupBalanceWarning').hide();
            }
        }).fail(function () {
            $('#groupBalanceContent').html('<div class="text-muted">Could not load group data</div>');
        });
    }

    // ===================== ADD ITEM (submit) =====================

    function submitAddItem() {
        if (!pendingAddItem || !currentCreatureEntry) return;

        var targetVal = $('#addItemTarget').val();
        var payload = {
            creatureEntry: currentCreatureEntry,
            itemEntry: pendingAddItem.entry,
            chance: parseFloat($('#addItemChance').val()) || 0,
            groupId: parseInt($('#addItemGroupId').val()) || 0,
            minCount: 1,
            maxCount: parseInt($('#addItemMaxCount').val()) || 1,
            patchMin: 0,
            patchMax: 10
        };

        // If targeting a reference pool, set refEntry
        if (targetVal && targetVal.indexOf('ref:') === 0) {
            payload.refEntry = parseInt(targetVal.replace('ref:', ''));
        }

        var $btn = $('#btnConfirmAddItem');
        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Adding…');

        $.ajax({
            url: '/Instances/AddLootItem',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (result) {
                if (result.success) {
                    showToast('Added ' + esc(pendingAddItem.name) + ' to loot table', 'success');
                    var modalEl = document.getElementById('addItemConfigModal');
                    if (modalEl) {
                        var inst = bootstrap.Modal.getInstance(modalEl);
                        if (inst) inst.hide();
                    }
                    pendingAddItem = null;
                    loadLoot(currentCreatureEntry); // Refresh
                } else {
                    showToast('Failed: ' + (result.error || 'Unknown'), 'error');
                }
                $btn.prop('disabled', false).html('<i class="fa-solid fa-plus"></i> Add to Loot Table');
            },
            error: function () {
                showToast('Failed — server error', 'error');
                $btn.prop('disabled', false).html('<i class="fa-solid fa-plus"></i> Add to Loot Table');
            }
        });
    }

    // ===================== REMOVE ITEM =====================

    function openRemoveConfirm(row) {
        var $row = $(row);
        pendingRemoveData = {
            entry: parseInt($row.data('entry')),
            item: parseInt($row.data('item')),
            groupId: parseInt($row.data('groupid')),
            patchMin: parseInt($row.data('patchmin')),
            patchMax: parseInt($row.data('patchmax')),
            source: $row.data('source') || 'direct',
            itemName: $row.data('itemname') || 'Unknown'
        };

        var qualityClass = '';
        var nameEl = $row.find('.loot-item-name');
        if (nameEl.length) {
            var cl = nameEl.attr('class') || '';
            var match = cl.match(/quality-\d/);
            if (match) qualityClass = match[0];
        }

        $('#removeItemName').html(
            '<span class="' + qualityClass + '">' + esc(pendingRemoveData.itemName) + '</span>' +
            ' <span class="text-muted">(#' + pendingRemoveData.item + ')</span>'
        );

        new bootstrap.Modal(document.getElementById('removeLootModal')).show();
    }

    function submitRemoveItem() {
        if (!pendingRemoveData) return;

        var $btn = $('#btnConfirmRemoveLoot');
        $btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Removing…');

        $.ajax({
            url: '/Instances/RemoveLootItem',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(pendingRemoveData),
            success: function (result) {
                if (result.success) {
                    showToast('Removed ' + esc(pendingRemoveData.itemName), 'success');
                    var modalEl = document.getElementById('removeLootModal');
                    if (modalEl) {
                        var inst = bootstrap.Modal.getInstance(modalEl);
                        if (inst) inst.hide();
                    }
                    pendingRemoveData = null;
                    if (currentCreatureEntry) loadLoot(currentCreatureEntry); // Refresh
                } else {
                    showToast('Failed: ' + (result.error || 'Unknown'), 'error');
                }
                $btn.prop('disabled', false).html('<i class="fa-solid fa-trash"></i> Remove');
            },
            error: function () {
                showToast('Failed — server error', 'error');
                $btn.prop('disabled', false).html('<i class="fa-solid fa-trash"></i> Remove');
            }
        });
    }

    // ===================== EVENTS =====================

    // Instance click
    $(document).on('click', '.inst-row', function () {
        $('.inst-row').removeClass('active');
        $(this).addClass('active');
        loadBosses(parseInt($(this).data('map')));
    });

    // Boss click
    $(document).on('click', '.boss-row', function () {
        $('.boss-row').removeClass('active');
        $(this).addClass('active');
        loadLoot(parseInt($(this).data('entry')));
    });

    // Show/hide trash toggle
    $('#chkShowTrash').on('change', function () {
        if (currentMapId) loadBosses(currentMapId);
    });

    // Detect field changes → mark dirty (item rows)
    $(document).on('input', '.loot-item-row .loot-edit-field', function () {
        var $row = $(this).closest('.loot-item-row');
        var origChance = parseFloat($row.data('origchance'));
        var origMax = parseInt($row.data('origmax'));

        var curChance = $row.find('.loot-chance-field').val();
        var curMax = parseInt($row.find('.loot-max-field').val());

        var chanceChanged = !$row.find('.loot-chance-field').prop('disabled') &&
            parseFloat(curChance) !== Math.abs(origChance);
        var maxChanged = curMax !== origMax;

        $row.find('.loot-chance-field').toggleClass('changed', chanceChanged);
        $row.find('.loot-max-field').toggleClass('changed', maxChanged);
        $row.toggleClass('dirty', chanceChanged || maxChanged);
    });

    // Detect field changes → mark dirty (ref pool headers)
    $(document).on('input', '.ref-header-row .loot-edit-field', function () {
        var $h = $(this).closest('.ref-header-row');
        var origChance = parseFloat($h.data('origchance'));
        var origPicks = parseInt($h.data('origpicks'));

        var curChance = parseFloat($h.find('.ref-chance-field').val());
        var curPicks = parseInt($h.find('.ref-picks-field').val());

        var chanceChanged = !isNaN(curChance) && curChance !== origChance;
        var picksChanged = !isNaN(curPicks) && curPicks !== origPicks;

        $h.find('.ref-chance-field').toggleClass('changed', chanceChanged);
        $h.find('.ref-picks-field').toggleClass('changed', picksChanged);
        $h.toggleClass('dirty', chanceChanged || picksChanged);
        $h.find('.ref-save-btn').css('opacity', (chanceChanged || picksChanged) ? '1' : '0');
    });

    // Save button click (item rows)
    $(document).on('click', '.loot-item-row .loot-save-btn', function () {
        saveRow($(this).closest('.loot-item-row'));
    });

    // Save button click (ref pool headers)
    $(document).on('click', '.ref-save-btn', function () {
        saveRefHeader($(this).closest('.ref-header-row'));
    });

    // Enter key in field → save
    $(document).on('keydown', '.loot-edit-field', function (e) {
        if (e.key === 'Enter') {
            e.preventDefault();
            var $refHeader = $(this).closest('.ref-header-row');
            if ($refHeader.length) {
                saveRefHeader($refHeader);
            } else {
                saveRow($(this).closest('.loot-item-row'));
            }
        }
    });

    // Boss multiplier buttons
    $(document).on('click', '[data-bossmult]', function () {
        if (!currentCreatureEntry) return;
        applyBossMultiplier(currentCreatureEntry, parseFloat($(this).data('bossmult')));
    });

    // Instance multiplier buttons
    $(document).on('click', '.instance-mult-footer .mult-mini', function () {
        applyInstanceMultiplier(parseFloat($(this).data('mult')));
    });

    // ── Remove loot item button ──
    $(document).on('click', '.loot-remove-btn', function (e) {
        e.stopPropagation();
        openRemoveConfirm($(this).closest('.loot-item-row'));
    });

    $('#btnConfirmRemoveLoot').on('click', function () {
        submitRemoveItem();
    });

    // ── Add Item button → open picker ──
    $('#btnAddLootItem').on('click', function () {
        if (!currentCreatureEntry) return;
        openItemPicker();
    });

    // ── Item Picker search ──
    $('#itemPickerSearch').on('input', function () {
        clearTimeout(itemSearchTimer);
        itemSearchTimer = setTimeout(function () {
            itemPickerQuery = $('#itemPickerSearch').val();
            itemPickerPage = 1;
            loadItemPickerPage();
        }, 300);
    });

    $('#btnItemPickerSearch').on('click', function () {
        itemPickerQuery = $('#itemPickerSearch').val();
        itemPickerQuality = $('#itemPickerQuality').val();
        itemPickerPage = 1;
        loadItemPickerPage();
    });

    $('#itemPickerSearch').on('keydown', function (e) {
        if (e.key === 'Enter') {
            e.preventDefault();
            itemPickerQuery = $('#itemPickerSearch').val();
            itemPickerQuality = $('#itemPickerQuality').val();
            itemPickerPage = 1;
            loadItemPickerPage();
        }
    });

    $('#btnItemPickerPrev').on('click', function () {
        if (itemPickerPage > 1) { itemPickerPage--; loadItemPickerPage(); }
    });
    $('#btnItemPickerNext').on('click', function () {
        itemPickerPage++;
        loadItemPickerPage();
    });

    // ── Item Picker: select item → go to config ──
    $(document).on('click', '.ip-row', function () {
        var entry = parseInt($(this).data('entry'));
        var name = $(this).data('name');
        var quality = parseInt($(this).data('quality'));
        var iconPath = $(this).data('icon');
        selectItemForAdd(entry, name, quality, iconPath);
    });

    // ── Add Item Config: target/group change → load balance ──
    $('#addItemTarget').on('change', function () {
        loadGroupBalance();
    });

    $('#addItemGroupId').on('change', function () {
        loadGroupBalance();
    });

    $('#addItemChance').on('input', function () {
        if (parseInt($('#addItemGroupId').val()) > 0) {
            loadGroupBalance();
        }
    });

    // ── Add Item Config: confirm ──
    $('#btnConfirmAddItem').on('click', function () {
        submitAddItem();
    });

    // ===================== INIT =====================
    loadInstances();

    // ── Loot Changelog toggle ──
    $('#lootChangelogToggle').on('click', function () {
        $(this).toggleClass('collapsed');
        $('#lootChangelogBody').toggleClass('collapsed');
    });

    // ── Reset boss loot ──
    $('#btnResetBossLootOG').on('click', function () {
        if (!currentCreatureEntry) return;
        var name = $('#lootTitle').text() || 'this boss';
        BaselineSystem.resetCreatureLoot(currentCreatureEntry, name, function (success) {
            if (success) {
                loadLoot(currentCreatureEntry);
            }
        });
    });

    // ── Reset instance loot ──
    $('#btnResetInstanceOG').on('click', function () {
        if (!currentMapId) return;
        BaselineSystem.resetInstance(currentMapId, function (success) {
            if (success && currentCreatureEntry) {
                loadLoot(currentCreatureEntry);
            }
        });
    });

});
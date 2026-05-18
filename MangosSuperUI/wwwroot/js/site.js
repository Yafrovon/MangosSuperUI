/* ============================================================
   MangosSuperUI — site.js
   Global: Sidebar collapse, reorder, theme customization
   Runs on every page via _Layout.cshtml
   ============================================================ */

(function () {
    'use strict';

    // =========================================================
    //  1. THEME OVERRIDES — runs first, before DOM is interactive
    // =========================================================

    const THEME_STORAGE_KEY = 'msui_theme_overrides';

    // Default values (must match site.css :root)
    const THEME_DEFAULTS = {
        '--bg-body': '#f4f5f7',
        '--bg-sidebar': '#1e2530',
        '--bg-sidebar-hover': '#2a3341',
        '--bg-sidebar-active': '#33404f',
        '--bg-card': '#ffffff',
        '--bg-card-alt': '#f9fafb',
        '--bg-input': '#ffffff',
        '--text-primary': '#1a1d23',
        '--text-secondary': '#5f6b7a',
        '--text-muted': '#8d96a0',
        '--text-sidebar': '#9aa5b4',
        '--text-sidebar-active': '#ffffff',
        '--text-sidebar-heading': '#5c6a7a',
        '--text-on-accent': '#ffffff',
        '--accent': '#3b82c4',
        '--accent-hover': '#2d6da8',
        '--border-light': '#e2e5ea',
        '--border-medium': '#cbd0d8'
    };

    function getThemeOverrides() {
        try {
            return JSON.parse(localStorage.getItem(THEME_STORAGE_KEY)) || {};
        } catch { return {}; }
    }

    function applyThemeOverrides() {
        var overrides = getThemeOverrides();
        var styleEl = document.getElementById('theme-overrides');
        if (!styleEl) {
            styleEl = document.createElement('style');
            styleEl.id = 'theme-overrides';
            document.head.appendChild(styleEl);
        }
        var keys = Object.keys(overrides);
        if (keys.length === 0) {
            styleEl.textContent = '';
            return;
        }
        var css = ':root {\n';
        keys.forEach(function (k) {
            css += '  ' + k + ': ' + overrides[k] + ';\n';
        });
        css += '}';
        styleEl.textContent = css;
    }

    // Apply immediately (before DOMContentLoaded) so there's no flash
    applyThemeOverrides();


    // =========================================================
    //  2. SIDEBAR ORDER — read from localStorage, reorder DOM
    // =========================================================

    var ORDER_STORAGE_KEY = 'msui_sidebar_order';

    // Default group order + item order within each group
    var DEFAULT_ORDER = {
        groups: ['operations', 'server', 'spells', 'world', 'content', 'bots', 'data', 'downloads'],
        items: {
            operations: ['home', 'console', 'players', 'accounts', 'realm'],
            server: ['activity', 'serverlogs', 'livelogs', 'config', 'backup'],
            spells: ['spells', 'patch', 'visuallab'],
            world: ['worldmap', 'worldeditor'],
            content: ['items', 'gameobjects', 'loottuner', 'instances', 'lootifier'],
            bots: ['bots-dashboard'],
            data: ['database', 'sourcemap'],
            downloads: ['downloads-page']
        }
    };

    function getSidebarOrder() {
        var defaults = JSON.parse(JSON.stringify(DEFAULT_ORDER));
        var stored;
        try {
            stored = JSON.parse(localStorage.getItem(ORDER_STORAGE_KEY));
            if (!stored || !stored.groups || !stored.items) return defaults;
        } catch { return defaults; }

        // --- Merge: splice new groups/items into stored order ---

        // 1. Add any default groups missing from stored list (append at end)
        defaults.groups.forEach(function (g) {
            if (stored.groups.indexOf(g) === -1) stored.groups.push(g);
        });

        // 2. Remove stored groups that no longer exist in defaults
        stored.groups = stored.groups.filter(function (g) {
            return defaults.groups.indexOf(g) !== -1;
        });

        // 3. For each group, add missing items and prune removed ones
        defaults.groups.forEach(function (g) {
            var defItems = defaults.items[g] || [];
            var storedItems = stored.items[g] || [];

            // Append any new default items not yet in stored list
            defItems.forEach(function (item) {
                if (storedItems.indexOf(item) === -1) storedItems.push(item);
            });

            // Remove items that no longer exist in defaults
            stored.items[g] = storedItems.filter(function (item) {
                return defItems.indexOf(item) !== -1;
            });
        });

        // 4. Remove item entries for groups that no longer exist
        Object.keys(stored.items).forEach(function (g) {
            if (defaults.groups.indexOf(g) === -1) delete stored.items[g];
        });

        // Persist the merged result so this reconciliation only runs once
        saveSidebarOrder(stored);
        return stored;
    }

    function saveSidebarOrder(order) {
        localStorage.setItem(ORDER_STORAGE_KEY, JSON.stringify(order));
    }

    function applySidebarOrder() {
        var container = document.getElementById('sidebarGroups');
        if (!container) return;
        var order = getSidebarOrder();

        // Reorder groups
        order.groups.forEach(function (groupKey) {
            var section = container.querySelector('[data-group="' + groupKey + '"]');
            if (section) container.appendChild(section);
        });

        // Reorder items within each group
        Object.keys(order.items).forEach(function (groupKey) {
            var section = container.querySelector('[data-group="' + groupKey + '"]');
            if (!section) return;
            var ul = section.querySelector('.sidebar-nav-collapsible');
            if (!ul) return;
            order.items[groupKey].forEach(function (itemKey) {
                var li = ul.querySelector('[data-nav-key="' + itemKey + '"]');
                if (li) ul.appendChild(li);
            });
        });

        // Apply hidden groups
        applySidebarVisibility();
    }


    // =========================================================
    //  2B. SIDEBAR VISIBILITY — hide/show groups
    // =========================================================

    var VISIBILITY_STORAGE_KEY = 'msui_sidebar_hidden';

    function getHiddenGroups() {
        try {
            return JSON.parse(localStorage.getItem(VISIBILITY_STORAGE_KEY)) || [];
        } catch { return []; }
    }

    function saveHiddenGroups(hidden) {
        localStorage.setItem(VISIBILITY_STORAGE_KEY, JSON.stringify(hidden));
    }

    function applySidebarVisibility() {
        var container = document.getElementById('sidebarGroups');
        if (!container) return;
        var hidden = getHiddenGroups();
        var activeGroup = container.getAttribute('data-active-group') || '';

        container.querySelectorAll('.sidebar-section').forEach(function (sec) {
            var groupKey = sec.getAttribute('data-group');
            // Never hide the group containing the active page
            if (hidden.indexOf(groupKey) !== -1 && groupKey !== activeGroup) {
                sec.style.display = 'none';
            } else {
                sec.style.display = '';
            }
        });
    }

    // Apply visibility immediately (before DOMContentLoaded completes fully)
    applySidebarVisibility();


    // =========================================================
    //  3. SIDEBAR COLLAPSE/EXPAND
    // =========================================================

    var COLLAPSE_STORAGE_KEY = 'msui_sidebar_expanded';

    function getExpandedGroups() {
        try {
            return JSON.parse(localStorage.getItem(COLLAPSE_STORAGE_KEY)) || [];
        } catch { return []; }
    }

    function saveExpandedGroups(expanded) {
        localStorage.setItem(COLLAPSE_STORAGE_KEY, JSON.stringify(expanded));
    }

    function initSidebarCollapse() {
        var container = document.getElementById('sidebarGroups');
        if (!container) return;

        var activeGroup = container.getAttribute('data-active-group') || '';
        var expanded = getExpandedGroups();

        // On first visit (nothing stored), only expand the active group
        if (!localStorage.getItem(COLLAPSE_STORAGE_KEY)) {
            expanded = activeGroup ? [activeGroup] : [];
        }

        // Always ensure active group is expanded
        if (activeGroup && expanded.indexOf(activeGroup) === -1) {
            expanded.push(activeGroup);
        }

        var sections = container.querySelectorAll('.sidebar-section');
        sections.forEach(function (sec) {
            var groupKey = sec.getAttribute('data-group');
            if (expanded.indexOf(groupKey) !== -1) {
                sec.classList.add('expanded');
            } else {
                sec.classList.remove('expanded');
            }
        });

        saveExpandedGroups(expanded);
        updateLanderVisibility();

        // Click handlers for section titles
        container.querySelectorAll('.sidebar-section-title[data-toggle-group]').forEach(function (title) {
            title.addEventListener('click', function () {
                var groupKey = this.getAttribute('data-toggle-group');
                var sec = this.closest('.sidebar-section');
                var exp = getExpandedGroups();

                if (sec.classList.contains('expanded')) {
                    sec.classList.remove('expanded');
                    exp = exp.filter(function (g) { return g !== groupKey; });
                } else {
                    sec.classList.add('expanded');
                    if (exp.indexOf(groupKey) === -1) exp.push(groupKey);
                }

                saveExpandedGroups(exp);
                updateLanderVisibility();
            });
        });
    }

    function updateLanderVisibility() {
        var lander = document.getElementById('sidebarLander');
        if (!lander) return;
        var anyExpanded = document.querySelectorAll('.sidebar-section.expanded').length > 0;
        if (anyExpanded) {
            lander.classList.add('hidden');
        } else {
            lander.classList.remove('hidden');
        }
    }


    // =========================================================
    //  4. CUSTOMIZE MODAL — Order Tab
    // =========================================================

    function initCustomizeModal() {
        var overlay = document.getElementById('customizeOverlay');
        var btnOpen = document.getElementById('btnOpenCustomize');
        var btnClose = document.getElementById('btnCloseCustomize');
        if (!overlay || !btnOpen) return;

        btnOpen.addEventListener('click', function () {
            overlay.classList.add('open');
            populateReorderList();
            populateVisibilityList();
            populateThemePickers();
        });

        btnClose.addEventListener('click', function () {
            overlay.classList.remove('open');
        });

        overlay.addEventListener('click', function (e) {
            if (e.target === overlay) overlay.classList.remove('open');
        });

        // Tab switching
        overlay.querySelectorAll('.customize-tab').forEach(function (tab) {
            tab.addEventListener('click', function () {
                var target = this.getAttribute('data-tab');
                overlay.querySelectorAll('.customize-tab').forEach(function (t) { t.classList.remove('active'); });
                overlay.querySelectorAll('.customize-tab-content').forEach(function (c) { c.classList.remove('active'); });
                this.classList.add('active');
                overlay.querySelector('[data-tab-content="' + target + '"]').classList.add('active');
            });
        });

        // Theme reset button
        var btnResetTheme = document.getElementById('btnResetTheme');
        if (btnResetTheme) {
            btnResetTheme.addEventListener('click', function () {
                localStorage.removeItem(THEME_STORAGE_KEY);
                applyThemeOverrides();
                populateThemePickers();
            });
        }
    }


    // ---- Reorder List (groups + items within) ----

    function populateReorderList() {
        var list = document.getElementById('reorderGroupList');
        if (!list) return;
        list.innerHTML = '';

        var order = getSidebarOrder();

        // Group display names
        var groupNames = {
            operations: 'Operations',
            server: 'Server',
            spells: 'Spells',
            content: 'Content',
            bots: 'AI Bots',
            data: 'Data',
            downloads: 'Downloads & Uploads'
        };

        // Item display info (key -> {icon, label})
        var itemInfo = {
            home: { icon: 'fa-gauge', label: 'Dashboard' },
            console: { icon: 'fa-terminal', label: 'Console' },
            players: { icon: 'fa-users', label: 'Players' },
            accounts: { icon: 'fa-id-badge', label: 'Accounts' },
            realm: { icon: 'fa-globe', label: 'Realm' },
            activity: { icon: 'fa-clipboard-list', label: 'Activity Log' },
            serverlogs: { icon: 'fa-file-lines', label: 'Server Logs' },
            livelogs: { icon: 'fa-satellite-dish', label: 'Live Logs' },
            config: { icon: 'fa-sliders', label: 'Config Editor' },
            backup: { icon: 'fa-hard-drive', label: 'Backups' },
            worldmap: { icon: 'fa-map-location-dot', label: 'World Map' },
            items: { icon: 'fa-box-open', label: 'Items' },
            spells: { icon: 'fa-book-open', label: 'Spell Editor' },
            patch: { icon: 'fa-wand-sparkles', label: 'Spell Creator' },
            visuallab: { icon: 'fa-cube', label: 'Spell Visualizer' },
            gameobjects: { icon: 'fa-cubes', label: 'Game Objects' },
            loottuner: { icon: 'fa-dice-d20', label: 'Loot Tuner' },
            instances: { icon: 'fa-dungeon', label: 'Instance Loot' },
            lootifier: { icon: 'fa-dragon', label: 'ARPG Lootifier' },
            'downloads-page': { icon: 'fa-arrow-down-to-line', label: 'Downloads' },
            'bots-dashboard': { icon: 'fa-robot', label: 'AI Bots' },
            database: { icon: 'fa-database', label: 'Database Explorer' },
            sourcemap: { icon: 'fa-sitemap', label: 'Source Map' }
        };

        order.groups.forEach(function (groupKey) {
            var groupDiv = document.createElement('div');
            groupDiv.className = 'reorder-group';
            groupDiv.setAttribute('data-reorder-group', groupKey);
            groupDiv.draggable = true;

            var header = document.createElement('div');
            header.className = 'reorder-group-header';
            header.innerHTML = '<i class="fa-solid fa-grip-vertical drag-handle"></i> ' + (groupNames[groupKey] || groupKey);
            groupDiv.appendChild(header);

            var ul = document.createElement('ul');
            ul.className = 'reorder-item-list';

            (order.items[groupKey] || []).forEach(function (itemKey) {
                var info = itemInfo[itemKey] || { icon: 'fa-circle', label: itemKey };
                var li = document.createElement('li');
                li.className = 'reorder-item';
                li.setAttribute('data-reorder-item', itemKey);
                li.draggable = true;
                li.innerHTML =
                    '<i class="fa-solid fa-grip-vertical drag-handle"></i>' +
                    '<i class="fa-solid ' + info.icon + '"></i> ' +
                    info.label;
                ul.appendChild(li);
            });

            groupDiv.appendChild(ul);
            list.appendChild(groupDiv);
        });

        initGroupDragAndDrop(list);
        initItemDragAndDrop(list);
    }

    function initGroupDragAndDrop(container) {
        var draggedGroup = null;

        container.addEventListener('dragstart', function (e) {
            var group = e.target.closest('.reorder-group');
            if (!group) return;
            // Only allow group drag from the header
            if (!e.target.closest('.reorder-group-header') && e.target.classList.contains('reorder-item')) return;
            if (e.target.closest('.reorder-item')) { return; } // item drag is separate
            draggedGroup = group;
            group.classList.add('dragging');
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData('text/plain', 'group');
        });

        container.addEventListener('dragover', function (e) {
            if (!draggedGroup) return;
            e.preventDefault();
            e.dataTransfer.dropEffect = 'move';

            var target = e.target.closest('.reorder-group');
            if (!target || target === draggedGroup) return;

            // Clear all indicators
            container.querySelectorAll('.reorder-group').forEach(function (g) { g.classList.remove('drag-over'); });
            target.classList.add('drag-over');
        });

        container.addEventListener('drop', function (e) {
            if (!draggedGroup) return;
            e.preventDefault();
            var target = e.target.closest('.reorder-group');
            if (target && target !== draggedGroup) {
                container.insertBefore(draggedGroup, target);
            }
            commitGroupOrder(container);
        });

        container.addEventListener('dragend', function () {
            if (draggedGroup) draggedGroup.classList.remove('dragging');
            draggedGroup = null;
            container.querySelectorAll('.reorder-group').forEach(function (g) { g.classList.remove('drag-over'); });
        });
    }

    function initItemDragAndDrop(container) {
        var draggedItem = null;
        var sourceList = null;

        container.addEventListener('dragstart', function (e) {
            var item = e.target.closest('.reorder-item');
            if (!item) return;
            draggedItem = item;
            sourceList = item.closest('.reorder-item-list');
            item.classList.add('dragging');
            e.dataTransfer.effectAllowed = 'move';
            e.dataTransfer.setData('text/plain', 'item');
            e.stopPropagation();
        }, true);

        container.addEventListener('dragover', function (e) {
            if (!draggedItem) return;
            var targetItem = e.target.closest('.reorder-item');
            if (!targetItem || targetItem === draggedItem) return;
            // Only allow reorder within the same group
            if (targetItem.closest('.reorder-item-list') !== sourceList) return;
            e.preventDefault();
            e.stopPropagation();

            sourceList.querySelectorAll('.reorder-item').forEach(function (i) { i.classList.remove('drag-over'); });
            targetItem.classList.add('drag-over');
        }, true);

        container.addEventListener('drop', function (e) {
            if (!draggedItem) return;
            var targetItem = e.target.closest('.reorder-item');
            if (targetItem && targetItem !== draggedItem && targetItem.closest('.reorder-item-list') === sourceList) {
                e.preventDefault();
                e.stopPropagation();
                sourceList.insertBefore(draggedItem, targetItem);
                commitGroupOrder(container);
            }
        }, true);

        container.addEventListener('dragend', function () {
            if (draggedItem) draggedItem.classList.remove('dragging');
            draggedItem = null;
            sourceList = null;
            container.querySelectorAll('.reorder-item').forEach(function (i) { i.classList.remove('drag-over'); });
        }, true);
    }

    function commitGroupOrder(container) {
        var newOrder = { groups: [], items: {} };
        container.querySelectorAll('.reorder-group').forEach(function (groupEl) {
            var groupKey = groupEl.getAttribute('data-reorder-group');
            newOrder.groups.push(groupKey);
            newOrder.items[groupKey] = [];
            groupEl.querySelectorAll('.reorder-item').forEach(function (itemEl) {
                newOrder.items[groupKey].push(itemEl.getAttribute('data-reorder-item'));
            });
        });
        saveSidebarOrder(newOrder);
        applySidebarOrder(); // Live-update the actual sidebar
    }


    // =========================================================
    //  4B. CUSTOMIZE MODAL — Visibility Tab
    // =========================================================

    function populateVisibilityList() {
        var list = document.getElementById('visibilityList');
        if (!list) return;
        list.innerHTML = '';

        var container = document.getElementById('sidebarGroups');
        var activeGroup = container ? (container.getAttribute('data-active-group') || '') : '';
        var hidden = getHiddenGroups();
        var order = getSidebarOrder();

        var groupNames = {
            operations: 'Operations',
            server: 'Server',
            spells: 'Spells',
            content: 'Content',
            bots: 'AI Bots',
            data: 'Data',
            downloads: 'Downloads & Uploads'
        };

        var groupIcons = {
            operations: 'fa-gauge',
            server: 'fa-server',
            spells: 'fa-wand-sparkles',
            content: 'fa-box-open',
            bots: 'fa-robot',
            data: 'fa-database',
            downloads: 'fa-arrow-down-to-line'
        };

        order.groups.forEach(function (groupKey) {
            var isHidden = hidden.indexOf(groupKey) !== -1;
            var isActive = groupKey === activeGroup;
            var name = groupNames[groupKey] || groupKey;
            var icon = groupIcons[groupKey] || 'fa-folder';

            var row = document.createElement('div');
            row.className = 'visibility-row' + (isHidden ? ' hidden-group' : '');
            row.innerHTML =
                '<div class="visibility-info">' +
                '<i class="fa-solid ' + icon + '" style="color: var(--accent); font-size: 13px; width: 18px; text-align: center;"></i>' +
                '<span>' + name + '</span>' +
                (isActive ? '<span class="visibility-active-badge">current</span>' : '') +
                '</div>' +
                '<label class="visibility-toggle">' +
                '<input type="checkbox" ' + (!isHidden ? 'checked' : '') + ' ' + (isActive ? 'disabled' : '') +
                ' data-vis-group="' + groupKey + '" />' +
                '<span class="visibility-slider"></span>' +
                '</label>';

            row.querySelector('input').addEventListener('change', function () {
                var gk = this.getAttribute('data-vis-group');
                var h = getHiddenGroups();
                if (this.checked) {
                    h = h.filter(function (g) { return g !== gk; });
                } else {
                    if (h.indexOf(gk) === -1) h.push(gk);
                }
                saveHiddenGroups(h);
                applySidebarVisibility();
                // Update row styling
                row.classList.toggle('hidden-group', !this.checked);
            });

            list.appendChild(row);
        });
    }


    // =========================================================
    //  5. CUSTOMIZE MODAL — Theme Tab
    // =========================================================

    function populateThemePickers() {
        var overrides = getThemeOverrides();
        document.querySelectorAll('.theme-color-input').forEach(function (input) {
            var varName = input.getAttribute('data-var');
            // Show current override, or the default value
            input.value = overrides[varName] || THEME_DEFAULTS[varName] || '#000000';
        });

        // Bind change events
        document.querySelectorAll('.theme-color-input').forEach(function (input) {
            // Remove old listener by replacing node (simple approach)
            var newInput = input.cloneNode(true);
            input.parentNode.replaceChild(newInput, input);

            newInput.addEventListener('input', function () {
                var varName = this.getAttribute('data-var');
                var value = this.value;
                var overrides = getThemeOverrides();

                // If value matches default, remove the override
                if (value === THEME_DEFAULTS[varName]) {
                    delete overrides[varName];
                } else {
                    overrides[varName] = value;
                }

                localStorage.setItem(THEME_STORAGE_KEY, JSON.stringify(overrides));
                applyThemeOverrides();
            });
        });
    }


    // =========================================================
    //  6. INIT — on DOMContentLoaded
    // =========================================================

    document.addEventListener('DOMContentLoaded', function () {
        applySidebarOrder();
        initSidebarCollapse();
        initCustomizeModal();
    });

})();
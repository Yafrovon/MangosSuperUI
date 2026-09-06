/* ============================================================
   MangosSuperUI — site.js
   Global: Top-nav macro sections, sidebar collapse, reorder, theme customization
   Runs on every page via _Layout.cshtml
   ============================================================ */

(function () {
    'use strict';

    // =========================================================
    //  1. THEME MODE (light/dark) — runs first, before first paint
    // =========================================================

    var THEME_MODE_KEY = 'msui_theme_mode';

    function getStoredThemeMode() {
        var stored = localStorage.getItem(THEME_MODE_KEY);
        if (stored === 'light' || stored === 'dark') return stored;
        return 'light';
    }

    function applyThemeMode(mode) {
        document.documentElement.setAttribute('data-theme', mode);
    }

    function setThemeMode(mode) {
        localStorage.setItem(THEME_MODE_KEY, mode);
        applyThemeMode(mode);
    }

    // Apply immediately (before DOMContentLoaded) so there's no flash
    applyThemeMode(getStoredThemeMode());

    // =========================================================
    //  2. THEME OVERRIDES — runs first, before DOM is interactive
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
        var css = ':root[data-theme] {\n';
        keys.forEach(function (k) {
            css += '  ' + k + ': ' + overrides[k] + ';\n';
        });
        css += '}';
        styleEl.textContent = css;
    }

    // Apply immediately (before DOMContentLoaded) so there's no flash
    applyThemeOverrides();


    // =========================================================
    //  3. SIDEBAR ORDER — read from localStorage, reorder DOM
    // =========================================================

    var ORDER_STORAGE_KEY = 'msui_sidebar_order';

    // Macro sections shown in the top navbar. Group membership is fixed here
    // and mirrored by data-section="" on each .sidebar-section in _Sidebar.cshtml.
    var SECTIONS = [
        { key: 'admin', label: 'Server Administration', icon: 'fa-server', groups: ['operations', 'server', 'state'] },
        { key: 'tuning', label: 'Gameplay Tuning', icon: 'fa-sliders', groups: ['content', 'loot', 'spells', 'world', 'archive'] },
        { key: 'aibots', label: 'AI Bot Control', icon: 'fa-robot', groups: ['bots'] },
        { key: 'devdata', label: 'Data & Development', icon: 'fa-database', groups: ['data', 'devtools', 'gamedev'] },
        { key: 'wiki', label: 'Wiki', icon: 'fa-book', groups: ['wiki'] },
        { key: 'superui', label: 'SuperUI', icon: 'fa-toolbox', groups: ['gameworld', 'superui'] }
    ];

    // Display name + icon for every sidebar group (used by the Customize modal)
    var GROUP_META = {
        operations: { name: 'Operations', icon: 'fa-gauge' },
        server: { name: 'Server', icon: 'fa-server' },
        state: { name: 'Worlds & History', icon: 'fa-layer-group' },
        content: { name: 'Items', icon: 'fa-box-open' },
        loot: { name: 'Loot', icon: 'fa-dice-d20' },
        spells: { name: 'Spells', icon: 'fa-wand-sparkles' },
        world: { name: 'World & Maps', icon: 'fa-map-location-dot' },
        archive: { name: 'Archive', icon: 'fa-box-archive' },
        bots: { name: 'Bots', icon: 'fa-robot' },
        data: { name: 'Data', icon: 'fa-database' },
        devtools: { name: 'Bot Development', icon: 'fa-code' },
        gamedev: { name: 'Game Development', icon: 'fa-hammer' },
        wiki: { name: 'Documentation', icon: 'fa-book' },
        gameworld: { name: 'Gameworld', icon: 'fa-globe' },
        superui: { name: 'App', icon: 'fa-toolbox' }
    };

    function getSection(sectionKey) {
        for (var i = 0; i < SECTIONS.length; i++) {
            if (SECTIONS[i].key === sectionKey) return SECTIONS[i];
        }
        return null;
    }

    // Default group order + item order within each group
    var DEFAULT_ORDER = {
        groups: ['operations', 'server', 'state', 'content', 'loot', 'spells', 'world', 'archive', 'bots', 'data', 'devtools', 'gamedev', 'wiki', 'gameworld', 'superui'],
        items: {
            operations: ['home', 'console', 'players', 'accounts', 'realm'],
            server: ['serverlogs', 'livelogs', 'config'],
            state: ['worlds', 'changes', 'activity'],
            content: ['items', 'retextureengine'],
            loot: ['loottuner', 'instances', 'lootifier', 'questlootifier', 'craftinglootifier', 'professiontuning'],
            spells: ['spells', 'patch', 'spellcompleter'],
            world: ['worldmap', 'worldeditor'],
            archive: ['visuallab', 'gameobjects'],
            bots: ['bots-dashboard', 'bots-fleet', 'bots-map', 'bots-chatfeel', 'bots-chatcapacity'],
            data: ['database', 'sourcemap'],
            devtools: ['circuittrace'],
            gamedev: ['weaponforge', 'armorforge'],
            wiki: ['wiki-code', 'wiki-lua'],
            gameworld: ['armory'],
            superui: ['downloads-page', 'settings']
        }
    };

    // Insert entries present in defaultList but missing from storedList at the
    // position they hold in defaultList — right after their nearest preceding
    // sibling that already exists in storedList — instead of appending at the
    // end. Keeps a newly-added group/item next to its neighbours for users who
    // already have a saved order. Mutates storedList in place.
    function spliceMissingInOrder(storedList, defaultList) {
        var prev = -1; // stored index to insert the next missing entry after
        defaultList.forEach(function (key) {
            var idx = storedList.indexOf(key);
            if (idx === -1) {
                storedList.splice(prev + 1, 0, key);
                prev = prev + 1;
            } else {
                prev = idx;
            }
        });
    }

    function getSidebarOrder() {
        var defaults = JSON.parse(JSON.stringify(DEFAULT_ORDER));
        var stored;
        try {
            stored = JSON.parse(localStorage.getItem(ORDER_STORAGE_KEY));
            if (!stored || !stored.groups || !stored.items) return defaults;
        } catch { return defaults; }

        // --- Merge: splice new groups/items into stored order ---

        // 1. Splice any default groups missing from stored into their default
        //    position (next to their sibling), not at the end.
        spliceMissingInOrder(stored.groups, defaults.groups);

        // 2. Remove stored groups that no longer exist in defaults
        stored.groups = stored.groups.filter(function (g) {
            return defaults.groups.indexOf(g) !== -1;
        });

        // 3. For each group, add missing items and prune removed ones
        defaults.groups.forEach(function (g) {
            var defItems = defaults.items[g] || [];
            var storedItems = stored.items[g] || [];

            // Splice any new default items into their default position
            spliceMissingInOrder(storedItems, defItems);

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

        // Point each top-nav tab at the first page of its section
        applySectionTabs();
    }


    // =========================================================
    //  2C. TOP NAV — macro section tabs
    // =========================================================

    // Each tab links to the first *visible* page of its section, honouring the
    // user's group/item order and their hidden-group choices. Falls back to the
    // server-rendered href if the section has nothing visible.
    function applySectionTabs() {
        var container = document.getElementById('sidebarGroups');
        var nav = document.getElementById('topNavSections');
        if (!container || !nav) return;

        var hidden = getHiddenGroups();
        var order = getSidebarOrder();

        nav.querySelectorAll('.topnav-tab').forEach(function (tab) {
            var meta = getSection(tab.getAttribute('data-section'));
            if (!meta) return;

            var href = null;
            order.groups.forEach(function (groupKey) {
                if (href) return;
                if (meta.groups.indexOf(groupKey) === -1) return;
                if (hidden.indexOf(groupKey) !== -1) return;
                var sec = container.querySelector('.sidebar-section[data-group="' + groupKey + '"]');
                if (!sec) return;
                var link = sec.querySelector('.sidebar-nav-item a[href]');
                if (link) href = link.getAttribute('href');
            });

            if (href) tab.setAttribute('href', href);
        });
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
    //  4. SIDEBAR COLLAPSE/EXPAND
    // =========================================================

    var COLLAPSE_STORAGE_KEY = 'msui_sidebar_expanded';
    var COLLAPSED_STORAGE_KEY = 'msui_sidebar_collapsed';

    function getExpandedGroups() {
        try {
            return JSON.parse(localStorage.getItem(COLLAPSE_STORAGE_KEY)) || [];
        } catch { return []; }
    }

    function saveExpandedGroups(expanded) {
        localStorage.setItem(COLLAPSE_STORAGE_KEY, JSON.stringify(expanded));
    }

    // Groups the user explicitly collapsed. A group in the active section is
    // open by default; it only stays shut if the user shut it themselves.
    function getCollapsedGroups() {
        try {
            return JSON.parse(localStorage.getItem(COLLAPSED_STORAGE_KEY)) || [];
        } catch { return []; }
    }

    function saveCollapsedGroups(collapsed) {
        localStorage.setItem(COLLAPSED_STORAGE_KEY, JSON.stringify(collapsed));
    }

    function initSidebarCollapse() {
        var container = document.getElementById('sidebarGroups');
        if (!container) return;

        var activeGroup = container.getAttribute('data-active-group') || '';
        var activeSection = container.getAttribute('data-active-section') || '';
        var sectionMeta = getSection(activeSection);
        var sectionGroups = sectionMeta ? sectionMeta.groups : [];

        var expanded = getExpandedGroups();
        var collapsed = getCollapsedGroups();

        // Every group in the active section opens by default (the sidebar only
        // shows one section now, so there is room), unless it was shut by hand
        // or the group is flagged data-default-collapsed (e.g. Archive), which
        // stays shut until the user opens it themselves.
        sectionGroups.forEach(function (g) {
            var secEl = container.querySelector('.sidebar-section[data-group="' + g + '"]');
            if (secEl && secEl.getAttribute('data-default-collapsed') === 'true') return;
            if (collapsed.indexOf(g) === -1 && expanded.indexOf(g) === -1) expanded.push(g);
        });

        // The group holding the current page is always open
        if (activeGroup) {
            collapsed = collapsed.filter(function (g) { return g !== activeGroup; });
            if (expanded.indexOf(activeGroup) === -1) expanded.push(activeGroup);
        }

        var sections = container.querySelectorAll('.sidebar-section');
        sections.forEach(function (sec) {
            var groupKey = sec.getAttribute('data-group');
            if (expanded.indexOf(groupKey) !== -1 && collapsed.indexOf(groupKey) === -1) {
                sec.classList.add('expanded');
            } else {
                sec.classList.remove('expanded');
            }
        });

        saveExpandedGroups(expanded);
        saveCollapsedGroups(collapsed);
        updateLanderVisibility();

        // Click handlers for section titles
        container.querySelectorAll('.sidebar-section-title[data-toggle-group]').forEach(function (title) {
            title.addEventListener('click', function () {
                var groupKey = this.getAttribute('data-toggle-group');
                var sec = this.closest('.sidebar-section');
                var exp = getExpandedGroups();
                var col = getCollapsedGroups();

                if (sec.classList.contains('expanded')) {
                    sec.classList.remove('expanded');
                    exp = exp.filter(function (g) { return g !== groupKey; });
                    if (col.indexOf(groupKey) === -1) col.push(groupKey);
                } else {
                    sec.classList.add('expanded');
                    if (exp.indexOf(groupKey) === -1) exp.push(groupKey);
                    col = col.filter(function (g) { return g !== groupKey; });
                }

                saveExpandedGroups(exp);
                saveCollapsedGroups(col);
                updateLanderVisibility();
            });
        });
    }

    function updateLanderVisibility() {
        var lander = document.getElementById('sidebarLander');
        if (!lander) return;
        var container = document.getElementById('sidebarGroups');
        var anyExpanded = false;

        if (container) {
            var activeSection = container.getAttribute('data-active-section') || '';
            container.querySelectorAll('.sidebar-section.expanded').forEach(function (sec) {
                // Only groups actually on screen (right section, not hidden) count
                if (sec.getAttribute('data-section') !== activeSection) return;
                if (sec.style.display === 'none') return;
                anyExpanded = true;
            });
        }

        if (anyExpanded) {
            lander.classList.add('hidden');
        } else {
            lander.classList.remove('hidden');
        }
    }


    // =========================================================
    //  5. CUSTOMIZE MODAL — Order Tab
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

        // Theme mode select (light/dark)
        var themeModeSelect = document.getElementById('themeModeSelect');
        if (themeModeSelect) {
            themeModeSelect.addEventListener('change', function () {
                setThemeMode(this.value);
            });
        }

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
            worlds: { icon: 'fa-layer-group', label: 'World State' },
            changes: { icon: 'fa-diagram-project', label: 'Change Graph' },
            worldmap: { icon: 'fa-map-location-dot', label: 'World Map' },
            items: { icon: 'fa-box-open', label: 'Items' },
            spells: { icon: 'fa-book-open', label: 'Spell Editor' },
            patch: { icon: 'fa-wand-sparkles', label: 'Spell Creator' },
            visuallab: { icon: 'fa-cube', label: 'Spell Visualizer' },
            spellcompleter: { icon: 'fa-flag-checkered', label: 'Spell Completer' },
            gameobjects: { icon: 'fa-cubes', label: 'Game Objects' },
            loottuner: { icon: 'fa-dice-d20', label: 'Loot Tuner' },
            instances: { icon: 'fa-dungeon', label: 'Instance Loot' },
            lootifier: { icon: 'fa-dragon', label: 'ARPG Lootifier' },
            questlootifier: { icon: 'fa-scroll', label: 'Quest Lootifier' },
            craftinglootifier: { icon: 'fa-hammer', label: 'Crafting Lootifier' },
            professiontuning: { icon: 'fa-scale-balanced', label: 'Profession Tuning' },
            retextureengine: { icon: 'fa-palette', label: 'Retexture Engine' },
            weaponforge: { icon: 'fa-hammer', label: 'Weapon Forge' },
            armorforge: { icon: 'fa-shield-halved', label: 'Armor Forge' },
            worldeditor: { icon: 'fa-mountain-sun', label: '3D World Editor' },
            armory: { icon: 'fa-shield-halved', label: 'Armory' },
            'downloads-page': { icon: 'fa-download', label: 'Downloads' },
            settings: { icon: 'fa-gear', label: 'Settings' },
            'bots-fleet': { icon: 'fa-tower-broadcast', label: 'Fleet View' },
            'bots-map': { icon: 'fa-location-crosshairs', label: 'Bot Map Viewer' },
            'bots-dashboard': { icon: 'fa-robot', label: 'IBot Monitor' },
            'bots-chatfeel': { icon: 'fa-comments', label: 'Chat Feel' },
            'bots-chatcapacity': { icon: 'fa-server', label: 'Chat Capacity' },
            database: { icon: 'fa-database', label: 'Database Explorer' },
            sourcemap: { icon: 'fa-sitemap', label: 'Source Map' },
            circuittrace: { icon: 'fa-microchip', label: 'Circuit Board' },
            'wiki-code': { icon: 'fa-book', label: 'C++ SuperUI Docs' },
            'wiki-lua': { icon: 'fa-code', label: 'Lua & UI Docs' }
        };

        // Rendered section by section; groups can only be dragged within their section
        SECTIONS.forEach(function (section) {
            var sectionGroups = order.groups.filter(function (g) {
                return section.groups.indexOf(g) !== -1;
            });
            if (sectionGroups.length === 0) return;

            var label = document.createElement('div');
            label.className = 'customize-section-label';
            label.innerHTML = '<i class="fa-solid ' + section.icon + '"></i> ' + section.label;
            list.appendChild(label);

            var sectionWrap = document.createElement('div');
            sectionWrap.className = 'reorder-section';
            sectionWrap.setAttribute('data-reorder-section', section.key);

            sectionGroups.forEach(function (groupKey) {
                var meta = GROUP_META[groupKey] || { name: groupKey, icon: 'fa-folder' };

                var groupDiv = document.createElement('div');
                groupDiv.className = 'reorder-group';
                groupDiv.setAttribute('data-reorder-group', groupKey);
                groupDiv.draggable = true;

                var header = document.createElement('div');
                header.className = 'reorder-group-header';
                header.innerHTML = '<i class="fa-solid fa-grip-vertical drag-handle"></i> ' + meta.name;
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
                sectionWrap.appendChild(groupDiv);
            });

            list.appendChild(sectionWrap);
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
            // Groups belong to a fixed macro section - no cross-section moves
            if (target.closest('.reorder-section') !== draggedGroup.closest('.reorder-section')) return;

            // Clear all indicators
            container.querySelectorAll('.reorder-group').forEach(function (g) { g.classList.remove('drag-over'); });
            target.classList.add('drag-over');
        });

        container.addEventListener('drop', function (e) {
            if (!draggedGroup) return;
            e.preventDefault();
            var target = e.target.closest('.reorder-group');
            if (target && target !== draggedGroup &&
                target.closest('.reorder-section') === draggedGroup.closest('.reorder-section')) {
                target.parentNode.insertBefore(draggedGroup, target);
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

        SECTIONS.forEach(function (section) {
            var sectionGroups = order.groups.filter(function (g) {
                return section.groups.indexOf(g) !== -1;
            });
            if (sectionGroups.length === 0) return;

            var label = document.createElement('div');
            label.className = 'customize-section-label';
            label.innerHTML = '<i class="fa-solid ' + section.icon + '"></i> ' + section.label;
            list.appendChild(label);

            sectionGroups.forEach(function (groupKey) {
                var meta = GROUP_META[groupKey] || { name: groupKey, icon: 'fa-folder' };
                var isHidden = hidden.indexOf(groupKey) !== -1;
                var isActive = groupKey === activeGroup;

                var row = document.createElement('div');
                row.className = 'visibility-row' + (isHidden ? ' hidden-group' : '');
                row.innerHTML =
                    '<div class="visibility-info">' +
                    '<i class="fa-solid ' + meta.icon + '" style="color: var(--accent); font-size: 13px; width: 18px; text-align: center;"></i>' +
                    '<span>' + meta.name + '</span>' +
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
                    applySectionTabs();
                    // Update row styling
                    row.classList.toggle('hidden-group', !this.checked);
                });

                list.appendChild(row);
            });
        });
    }


    // =========================================================
    //  6. CUSTOMIZE MODAL — Theme Tab
    // =========================================================

    function populateThemePickers() {
        var themeModeSelect = document.getElementById('themeModeSelect');
        if (themeModeSelect) {
            themeModeSelect.value = getStoredThemeMode();
        }

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
    //  7. INIT — on DOMContentLoaded
    // =========================================================

    document.addEventListener('DOMContentLoaded', function () {
        applySidebarOrder();
        initSidebarCollapse();
        initCustomizeModal();
    });

})();
/* ============================================================
   sourcemap.js — VMaNGOS Source Map Explorer
   Evolved from functiongraph.js — adds types, enums, body
   preview, trace export, and reindex support.
   ============================================================ */

$(function () {
    'use strict';

    // ===================== HELPERS =====================

    function copyToClipboard(text) {
        if (navigator.clipboard && navigator.clipboard.writeText) {
            return navigator.clipboard.writeText(text);
        }
        // Fallback for non-secure contexts (HTTP)
        return new Promise(function (resolve, reject) {
            var ta = document.createElement('textarea');
            ta.value = text;
            ta.style.position = 'fixed';
            ta.style.left = '-9999px';
            document.body.appendChild(ta);
            ta.select();
            try {
                document.execCommand('copy');
                resolve();
            } catch (e) {
                reject(e);
            } finally {
                document.body.removeChild(ta);
            }
        });
    }

    // ===================== STATE =====================

    var statsData = null;   // { meta, stats, classes, files, source }
    var currentCenter = null;
    var currentNodeData = null;
    var breadcrumbs = [];
    var activeTab = 'classes';
    var activeView = 'graph';  // 'graph', 'body', 'trace'
    var sidebarSearch = '';

    // Zoom/pan state
    var smZoom = 1;
    var smPanX = 0, smPanY = 0;
    var smIsPanning = false;
    var smDragged = false;
    var smPanStartX = 0, smPanStartY = 0;
    var smPanStartPanX = 0, smPanStartPanY = 0;
    var smSvgW = 0, smSvgH = 0;

    // ===================== INIT =====================

    $.getJSON('/SourceMap/Stats', function (data) {
        statsData = data;
        var m = data.meta || {};
        $('#smTotalSymbols').text((m.total_functions || 0).toLocaleString());
        $('#smTotalTypes').text((m.total_types || 0).toLocaleString());
        $('#smTotalFiles').text((m.total_files || 0).toLocaleString());

        var src = data.source || 'none';
        if (src === 'live_index') {
            var ts = m.indexed_at ? new Date(m.indexed_at).toLocaleString() : '?';
            $('#smIndexStatus').text('Indexed: ' + ts);
        } else if (src === 'legacy') {
            $('#smIndexStatus').text('Legacy graph (run Reindex for full index)');
        } else {
            $('#smIndexStatus').text('No index — click Reindex');
        }

        renderSidebar();
    }).fail(function () {
        $('#smSidebarList').html('<div style="padding: 16px; color: var(--status-error);"><i class="fa-solid fa-exclamation-triangle"></i> Failed to load. Check that the source index or function-graph.json exists.</div>');
    });

    // ===================== SIDEBAR TABS =====================

    function renderSidebar() {
        // Universal search: if 2+ chars, hit search endpoint regardless of tab
        if (sidebarSearch.length >= 2) {
            renderUniversalSearch(sidebarSearch);
            return;
        }
        // Otherwise, render tab-specific browsing
        if (activeTab === 'classes') renderClassesList();
        else if (activeTab === 'files') renderFilesList();
        else if (activeTab === 'types') renderTypesList();
        else if (activeTab === 'enums') renderEnumsList();
        else if (activeTab === 'stats') renderStatsList();
    }

    var searchXhr = null;
    function renderUniversalSearch(query) {
        if (searchXhr) searchXhr.abort();
        $('#smSidebarList').html('<div style="padding: 16px; color: var(--text-muted);"><i class="fa-solid fa-spinner fa-spin"></i> Searching...</div>');

        searchXhr = $.getJSON('/SourceMap/Search', { q: query, kind: 'all' }, function (data) {
            searchXhr = null;
            var results = data.results || [];
            if (results.length === 0) {
                $('#smSidebarList').html('<div style="padding: 16px; color: var(--text-muted); font-size: 12.5px;">No matches for "' + esc(query) + '"</div>');
                return;
            }

            // Group by type
            var groups = { symbol: [], type: [], enum: [], file: [] };
            results.forEach(function (r) {
                var kind = r.type || 'symbol';
                if (groups[kind]) groups[kind].push(r);
                else groups.symbol.push(r);
            });

            var html = '';

            // Symbols
            if (groups.symbol.length) {
                html += '<div class="sm-group-header" data-grp="sr-sym"><span><i class="fa-solid fa-code" style="margin-right:4px;color:#3b82f6;"></i> Functions (' + groups.symbol.length + ')</span></div>';
                html += '<div class="sm-group-body" data-grp="sr-sym">';
                groups.symbol.forEach(function (r) {
                    var label = r.id || r.name;
                    var meta = '';
                    if (r.callerCount > 0) meta += r.callerCount + ' callers';
                    if (r.callCount > 0) meta += (meta ? ' · ' : '') + r.callCount + ' calls';
                    if (r.lineCount > 0) meta += (meta ? ' · ' : '') + r.lineCount + ' lines';
                    html += '<div class="sm-item sm-sym-item" data-fid="' + esc(r.id) + '">'
                        + '<span style="flex:1;overflow:hidden;text-overflow:ellipsis;" title="' + esc(r.id) + '">' + esc(label) + '</span>'
                        + (meta ? '<span class="sm-meta-right">' + meta + '</span>' : '')
                        + '</div>';
                });
                html += '</div>';
            }

            // Types
            if (groups.type.length) {
                html += '<div class="sm-group-header" data-grp="sr-type"><span><i class="fa-solid fa-shapes" style="margin-right:4px;color:#8b5cf6;"></i> Types (' + groups.type.length + ')</span></div>';
                html += '<div class="sm-group-body" data-grp="sr-type">';
                groups.type.forEach(function (r) {
                    html += '<div class="sm-item sm-type-item" data-type="' + esc(r.id) + '">'
                        + '<span class="sm-kind-badge sm-kind-type">' + (r.kind === 'struct' ? 'S' : 'C') + '</span>'
                        + '<span style="flex:1;overflow:hidden;text-overflow:ellipsis;" title="' + esc(r.id) + '">' + esc(r.name || r.id) + '</span>'
                        + '<span class="sm-meta-right">' + (r.methodCount || 0) + ' methods</span>'
                        + '</div>';
                });
                html += '</div>';
            }

            // Enums
            if (groups.enum.length) {
                html += '<div class="sm-group-header" data-grp="sr-enum"><span><i class="fa-solid fa-list-ol" style="margin-right:4px;color:#f59e0b;"></i> Enums (' + groups.enum.length + ')</span></div>';
                html += '<div class="sm-group-body" data-grp="sr-enum">';
                groups.enum.forEach(function (r) {
                    html += '<div class="sm-item sm-enum-item" data-enum="' + esc(r.id) + '">'
                        + '<span class="sm-kind-badge sm-kind-enum">E</span>'
                        + '<span style="flex:1;overflow:hidden;text-overflow:ellipsis;" title="' + esc(r.id) + '">' + esc(r.name || r.id) + '</span>'
                        + '<span class="sm-meta-right">' + (r.valueCount || 0) + ' values</span>'
                        + '</div>';
                });
                html += '</div>';
            }

            // Files
            if (groups.file.length) {
                html += '<div class="sm-group-header" data-grp="sr-file"><span><i class="fa-solid fa-file-code" style="margin-right:4px;color:#22c55e;"></i> Files (' + groups.file.length + ')</span></div>';
                html += '<div class="sm-group-body" data-grp="sr-file">';
                groups.file.forEach(function (r) {
                    var shortPath = (r.id || '').replace(/^src\//, '');
                    html += '<div class="sm-item sm-file-item" data-file="' + esc(r.id) + '">'
                        + '<span style="flex:1;overflow:hidden;text-overflow:ellipsis;" title="' + esc(r.id) + '">' + esc(shortPath) + '</span>'
                        + '<span class="sm-meta-right">' + (r.symbolCount || 0) + ' symbols</span>'
                        + '</div>';
                });
                html += '</div>';
            }

            $('#smSidebarList').html(html);
        }).fail(function (xhr) {
            if (xhr.statusText === 'abort') return;
            searchXhr = null;
            $('#smSidebarList').html('<div style="padding: 16px; color: var(--status-error);">Search failed</div>');
        });
    }

    function renderClassesList() {
        if (!statsData || !statsData.classes) return;
        var html = '', filter = sidebarSearch.toLowerCase();
        var classes = Object.values(statsData.classes).sort(function (a, b) { return b.function_count - a.function_count; });
        var shown = 0;
        classes.forEach(function (cls) {
            var nameMatch = !filter || cls.name.toLowerCase().indexOf(filter) !== -1;
            var funcs = cls.functions || [];
            var matchingFuncs = [];
            funcs.forEach(function (fid) {
                if (!filter || fid.toLowerCase().indexOf(filter) !== -1 || nameMatch) matchingFuncs.push(fid);
            });
            if (matchingFuncs.length === 0) return;
            shown++;
            var collapsed = !filter && shown > 10;
            html += '<div class="sm-group-header" data-grp="cls-' + shown + '"><span>' + esc(cls.name) + '</span><span class="count">' + cls.function_count + '</span></div>';
            html += '<div class="sm-group-body" data-grp="cls-' + shown + '"' + (collapsed ? ' style="display:none;"' : '') + '>';
            matchingFuncs.forEach(function (fid) {
                var shortName = fid.indexOf('::') !== -1 ? fid.split('::').pop() : fid;
                html += '<div class="sm-item sm-sym-item' + (fid === currentCenter ? ' active' : '') + '" data-fid="' + esc(fid) + '"><span style="overflow:hidden;text-overflow:ellipsis;" title="' + esc(fid) + '">' + esc(shortName) + '</span></div>';
            });
            html += '</div>';
        });
        if (!shown) html = '<div style="padding: 16px; color: var(--text-muted); font-size: 12.5px;">No matches</div>';
        $('#smSidebarList').html(html);
    }

    function renderFilesList() {
        if (!statsData || !statsData.files) return;
        var html = '', filter = sidebarSearch.toLowerCase();
        var files = Object.values(statsData.files).sort(function (a, b) { return b.function_count - a.function_count; });
        var shown = 0;
        files.forEach(function (file) {
            var shortPath = (file.path || '').replace(/^src\//, '');
            if (filter && shortPath.toLowerCase().indexOf(filter) === -1) return;
            shown++;
            var collapsed = !filter && shown > 10;
            html += '<div class="sm-group-header" data-grp="file-' + shown + '"><span title="' + esc(file.path) + '">' + esc(shortPath) + '</span><span class="count">' + file.function_count + '</span></div>';
            html += '<div class="sm-group-body" data-grp="file-' + shown + '"' + (collapsed ? ' style="display:none;"' : '') + '>';
            (file.functions || []).forEach(function (fid) {
                var shortName = fid.indexOf('::') !== -1 ? fid.split('::').pop() : fid;
                html += '<div class="sm-item sm-sym-item' + (fid === currentCenter ? ' active' : '') + '" data-fid="' + esc(fid) + '"><span style="overflow:hidden;text-overflow:ellipsis;" title="' + esc(fid) + '">' + esc(shortName) + '</span></div>';
            });
            html += '</div>';
        });
        if (!shown) html = '<div style="padding: 16px; color: var(--text-muted); font-size: 12.5px;">No matches</div>';
        $('#smSidebarList').html(html);
    }

    function renderTypesList() {
        // Use search endpoint to get types
        var filter = sidebarSearch.toLowerCase();
        if (!filter && statsData && statsData.source === 'live_index') {
            // Show a prompt to search
            $('#smSidebarList').html('<div style="padding: 16px; color: var(--text-muted); font-size: 12.5px;">Type to search types...</div>');
            if (statsData.classes) {
                // Show class names as types
                var html = '';
                var sorted = Object.values(statsData.classes).sort(function (a, b) { return b.function_count - a.function_count; });
                sorted.forEach(function (cls) {
                    html += '<div class="sm-item sm-type-item" data-type="' + esc(cls.name) + '"><span class="sm-kind-badge sm-kind-type">C</span><span style="overflow:hidden;text-overflow:ellipsis;" title="' + esc(cls.name) + '">' + esc(cls.name) + '</span><span class="sm-meta-right">' + cls.function_count + ' methods</span></div>';
                });
                $('#smSidebarList').html(html || '<div style="padding: 16px; color: var(--text-muted);">No types found</div>');
            }
            return;
        }

        if (filter.length >= 1) {
            $.getJSON('/SourceMap/Search', { q: filter, kind: 'type' }, function (data) {
                var html = '';
                (data.results || []).forEach(function (r) {
                    html += '<div class="sm-item sm-type-item" data-type="' + esc(r.id) + '"><span class="sm-kind-badge sm-kind-type">' + (r.kind === 'struct' ? 'S' : 'C') + '</span><span style="overflow:hidden;text-overflow:ellipsis;" title="' + esc(r.id) + '">' + esc(r.name || r.id) + '</span><span class="sm-meta-right">' + (r.methodCount || 0) + ' methods</span></div>';
                });
                if (!html) html = '<div style="padding: 16px; color: var(--text-muted); font-size: 12.5px;">No matches</div>';
                if (activeTab === 'types') $('#smSidebarList').html(html);
            });
        }
    }

    function renderEnumsList() {
        var filter = sidebarSearch.toLowerCase();
        if (!filter) {
            $('#smSidebarList').html('<div style="padding: 16px; color: var(--text-muted); font-size: 12.5px;">Type to search enums...</div>');
            return;
        }
        $.getJSON('/SourceMap/Search', { q: filter, kind: 'enum' }, function (data) {
            var html = '';
            (data.results || []).forEach(function (r) {
                html += '<div class="sm-item sm-enum-item" data-enum="' + esc(r.id) + '"><span class="sm-kind-badge sm-kind-enum">E</span><span style="overflow:hidden;text-overflow:ellipsis;" title="' + esc(r.id) + '">' + esc(r.name || r.id) + '</span><span class="sm-meta-right">' + (r.valueCount || 0) + ' values</span></div>';
            });
            if (!html) html = '<div style="padding: 16px; color: var(--text-muted); font-size: 12.5px;">No matches</div>';
            if (activeTab === 'enums') $('#smSidebarList').html(html);
        });
    }

    function renderStatsList() {
        if (!statsData || !statsData.stats || !statsData.meta) return;
        var m = statsData.meta, s = statsData.stats;
        var html = '<div style="padding: 14px;">'
            + '<div style="font-size: 11px; font-weight: 700; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 8px;">Overview</div>'
            + statRow('Symbols', m.total_functions) + statRow('Edges', m.total_edges) + statRow('Types', m.total_types) + statRow('Enums', m.total_enums) + statRow('Files', m.total_files) + statRow('Lines', m.total_lines)
            + '</div>';
        html += renderStatsGroup('Hub Functions', s.hub_functions, function (n) { return (n.total_connections || 0) + ' conn'; });
        html += renderStatsGroup('Most Called', s.top_callers, function (n) { return (n.caller_count || 0) + ' callers'; });
        html += renderStatsGroup('Most Complex', s.top_complex, function (n) { return (n.call_count || 0) + ' calls'; });
        html += renderStatsGroup('Deepest', s.top_deep, function (n) { return (n.complexity || n.max_depth || 0) + ' complexity'; });
        $('#smSidebarList').html(html);
    }

    function statRow(label, val) {
        return '<div style="display:flex;justify-content:space-between;padding:3px 0;font-size:12.5px;"><span style="color:var(--text-secondary);">' + label + '</span><span style="font-weight:600;color:var(--text-primary);">' + (val || 0).toLocaleString() + '</span></div>';
    }

    function renderStatsGroup(title, items, formatFn) {
        var html = '<div style="padding: 0 14px 14px;"><div style="font-size: 11px; font-weight: 700; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 6px;">' + title + '</div>';
        (items || []).slice(0, 15).forEach(function (n) {
            html += '<div class="sm-item sm-sym-item" data-fid="' + esc(n.id) + '" style="padding: 4px 0;"><span style="overflow:hidden;text-overflow:ellipsis;" title="' + esc(n.qualified || n.id) + '">' + esc(n.qualified || n.id) + '</span><span class="sm-meta-right">' + formatFn(n) + '</span></div>';
        });
        return html + '</div>';
    }

    // ── Sidebar events ──

    $(document).on('click', '.sm-sidebar-tabs button', function () {
        activeTab = $(this).data('tab');
        $('.sm-sidebar-tabs button').removeClass('active');
        $(this).addClass('active');
        renderSidebar();
    });

    $(document).on('click', '.sm-group-header', function () {
        var grp = $(this).data('grp');
        $('.sm-group-body[data-grp="' + grp + '"]').toggle();
    });

    $(document).on('click', '.sm-sym-item', function (e) {
        e.stopPropagation();
        var fid = $(this).attr('data-fid');
        if (fid) { breadcrumbs = []; loadNode(fid); }
    });

    $(document).on('click', '.sm-type-item', function (e) {
        e.stopPropagation();
        var name = $(this).attr('data-type');
        if (name) loadTypeDetail(name);
    });

    $(document).on('click', '.sm-enum-item', function (e) {
        e.stopPropagation();
        var name = $(this).attr('data-enum');
        if (name) loadEnumDetail(name);
    });

    $(document).on('click', '.sm-file-item', function (e) {
        e.stopPropagation();
        var path = $(this).attr('data-file');
        if (path) loadFileDetail(path);
    });

    var searchTimer = null;
    $('#smSearchInput').on('input', function () {
        var val = $(this).val();
        clearTimeout(searchTimer);
        searchTimer = setTimeout(function () { sidebarSearch = val; renderSidebar(); }, 200);
    });

    // ===================== LOAD NODE =====================

    function loadNode(nodeId) {
        $('#smWelcome').hide();
        $('#smToolbar').show();
        activeView = 'graph';
        showActiveView();
        $('#smStatsBar').html('<span style="color: var(--text-muted);"><i class="fa-solid fa-spinner fa-spin"></i> Loading...</span>').show();

        smDetachWheel();
        $('#smGraphSvg').remove();

        $.getJSON('/SourceMap/Node', { id: nodeId }, function (data) {
            currentCenter = nodeId;
            currentNodeData = data;

            // Breadcrumb cycle detection
            var cycleIdx = -1;
            for (var i = 0; i < breadcrumbs.length; i++) {
                if (breadcrumbs[i].id === nodeId) { cycleIdx = i; break; }
            }
            if (cycleIdx >= 0) {
                breadcrumbs = breadcrumbs.slice(0, cycleIdx + 1);
            } else {
                breadcrumbs.push({ id: nodeId, label: data.center.qualified, type: 'symbol' });
            }

            renderToolbar();
            renderStatsBar(data);
            buildGraph(data);
            renderDetailPanel(data);
            updateSidebarActive();
        }).fail(function () {
            $('#smStatsBar').html('<span style="color: var(--status-error);"><i class="fa-solid fa-exclamation-triangle"></i> Failed to load: ' + esc(nodeId) + '</span>');
        });
    }

    // ===================== TOOLBAR =====================

    function renderToolbar() {
        var html = '';
        if (breadcrumbs.length > 1) html += '<button id="smBtnBack" title="Go back"><i class="fa-solid fa-arrow-left"></i></button>';

        html += '<div class="sm-breadcrumbs">';
        breadcrumbs.forEach(function (crumb, i) {
            if (i > 0) html += '<span class="sm-crumb-sep"><i class="fa-solid fa-chevron-right"></i></span>';
            var isCurrent = i === breadcrumbs.length - 1;
            html += '<span class="sm-crumb' + (isCurrent ? ' current' : '') + '" data-idx="' + i + '">' + esc(crumb.label) + '</span>';
        });
        html += '</div>';

        html += '<button id="smBtnGraph" class="' + (activeView === 'graph' ? 'active' : '') + '" title="Graph view"><i class="fa-solid fa-project-diagram"></i></button>';
        html += '<button id="smBtnBody" class="' + (activeView === 'body' ? 'active' : '') + '" title="View source"><i class="fa-solid fa-code"></i> Source</button>';
        html += '<button id="smBtnTrace" class="' + (activeView === 'trace' ? 'active' : '') + '" title="Export trace"><i class="fa-solid fa-download"></i> Trace</button>';
        html += '<button id="smBtnDetail" title="Toggle detail panel"><i class="fa-solid fa-info-circle"></i></button>';

        $('#smToolbar').addClass('sm-toolbar').html(html);
    }

    $(document).on('click', '#smBtnBack', function () {
        if (breadcrumbs.length <= 1) return;
        breadcrumbs.pop();
        var prev = breadcrumbs.pop();
        if (prev.type === 'symbol') loadNode(prev.id);
        else if (prev.type === 'type') loadTypeDetail(prev.id);
    });

    $(document).on('click', '.sm-crumb:not(.current)', function () {
        var idx = parseInt($(this).data('idx'));
        var crumb = breadcrumbs[idx];
        breadcrumbs = breadcrumbs.slice(0, idx);
        if (crumb.type === 'symbol') loadNode(crumb.id);
        else if (crumb.type === 'type') loadTypeDetail(crumb.id);
    });

    $(document).on('click', '#smBtnDetail', function () { $('#smDetailPanel').toggleClass('visible').toggle(); });

    $(document).on('click', '#smBtnGraph', function () { activeView = 'graph'; showActiveView(); renderToolbar(); if (currentNodeData) buildGraph(currentNodeData); });
    $(document).on('click', '#smBtnBody', function () { activeView = 'body'; showActiveView(); renderToolbar(); loadBody(); });
    $(document).on('click', '#smBtnTrace', function () { activeView = 'trace'; showActiveView(); renderToolbar(); renderTracePanel(); });

    function showActiveView() {
        $('#smGraphView').toggle(activeView === 'graph');
        $('#smBodyView').toggle(activeView === 'body');
        $('#smTraceView').toggle(activeView === 'trace');
    }

    // ===================== STATS BAR =====================

    function renderStatsBar(data) {
        var c = data.center;
        var neighbors = data.neighbors || [];
        var outCount = 0, inCount = 0;
        neighbors.forEach(function (n) {
            if (n.direction === 'outgoing') outCount++;
            else if (n.direction === 'incoming') inCount++;
            else { outCount++; inCount++; }
        });
        var kindBadge = c.kind ? '<span class="sm-kind-badge sm-kind-symbol">' + esc(c.kind) + '</span>' : '';
        $('#smStatsBar').html(
            kindBadge + '<strong>' + esc(c.qualified) + '</strong>'
            + ' <span>·</span> <span>' + c.line_count + ' lines</span>'
            + ' <span>·</span> <span style="color: #3b82f6;">▸ ' + outCount + ' calls</span>'
            + ' <span>·</span> <span style="color: #22c55e;">◂ ' + inCount + ' callers</span>'
        ).show();
    }

    // ===================== BODY PREVIEW =====================

    function loadBody() {
        if (!currentCenter) return;
        $('#smBodyView').html('<div style="padding: 20px; color: var(--text-muted);"><i class="fa-solid fa-spinner fa-spin"></i> Loading source...</div>');

        $.getJSON('/SourceMap/Body', { id: currentCenter }, function (data) {
            var html = '<div class="sm-body-header"><div><span class="sm-body-title">' + esc(data.signature || currentCenter) + '</span></div>';
            if (data.file) html += '<span class="sm-body-file">' + esc(data.file) + ':' + (data.lineStart || '?') + '-' + (data.lineEnd || '?') + '</span>';
            html += '</div>';
            if (data.body) {
                html += '<pre>' + esc(data.body) + '</pre>';
            } else {
                html += '<div style="padding: 20px; color: var(--text-muted);">' + (data.note || 'No body available') + '</div>';
            }
            $('#smBodyView').html(html);
        }).fail(function () {
            $('#smBodyView').html('<div style="padding: 20px; color: var(--status-error);">Failed to load body</div>');
        });
    }

    // ===================== TRACE EXPORT =====================

    function renderTracePanel() {
        if (!currentCenter) return;
        var html = '<div class="sm-trace-controls">';
        html += '<label>Root:</label> <strong>' + esc(currentCenter) + '</strong>';
        html += '<label style="margin-left:12px;">Depth:</label> <input type="number" id="smTraceDepth" value="2" min="1" max="10" />';
        html += '<label class="sm-trace-check"><input type="checkbox" id="smTraceTypes" checked /> Types</label>';
        html += '<label class="sm-trace-check"><input type="checkbox" id="smTraceHeaders" checked /> Headers</label>';
        html += '<button id="smTraceGenerate" class="btn btn-sm" style="margin-left: 8px;"><i class="fa-solid fa-bolt"></i> Generate</button>';
        html += '<button id="smTraceCopy" class="btn btn-sm" style="display:none;"><i class="fa-solid fa-copy"></i> Copy</button>';
        html += '<button id="smTraceDownload" class="btn btn-sm" style="display:none;"><i class="fa-solid fa-download"></i> Download .txt</button>';
        html += '</div>';
        html += '<div id="smTraceStats" class="sm-trace-stats" style="display:none;"></div>';
        html += '<div id="smTraceOutput" class="sm-trace-output" style="display:none;"></div>';
        $('#smTraceView').html(html);
    }

    $(document).on('click', '#smTraceGenerate', function () {
        if (!currentCenter) return;
        var depth = parseInt($('#smTraceDepth').val()) || 2;
        var types = $('#smTraceTypes').is(':checked');
        var headers = $('#smTraceHeaders').is(':checked');

        $('#smTraceOutput').text('Generating trace...').show();
        $('#smTraceStats').hide();

        $.getJSON('/SourceMap/ExportTrace', { root: currentCenter, depth: depth, types: types, headers: headers }, function (data) {
            if (data.formattedText) {
                $('#smTraceOutput').text(data.formattedText).show();
                $('#smTraceStats').html(
                    '<strong>' + data.totalFunctions + '</strong> functions · <strong>' + data.totalLines + '</strong> lines · ~<strong>' + data.estimatedTokens.toLocaleString() + '</strong> tokens'
                ).show();
                $('#smTraceCopy, #smTraceDownload').show();
            } else {
                $('#smTraceOutput').text('No trace data returned').show();
            }
        }).fail(function () {
            $('#smTraceOutput').text('Failed to generate trace').show();
        });
    });

    $(document).on('click', '#smTraceCopy', function () {
        var text = $('#smTraceOutput').text();
        copyToClipboard(text).then(function () {
            var btn = $('#smTraceCopy');
            btn.html('<i class="fa-solid fa-check"></i> Copied');
            setTimeout(function () { btn.html('<i class="fa-solid fa-copy"></i> Copy'); }, 1500);
        });
    });

    $(document).on('click', '#smTraceDownload', function () {
        if (!currentCenter) return;
        var depth = parseInt($('#smTraceDepth').val()) || 2;
        var types = $('#smTraceTypes').is(':checked');
        var headers = $('#smTraceHeaders').is(':checked');
        var url = '/SourceMap/ExportTrace?root=' + encodeURIComponent(currentCenter) + '&depth=' + depth + '&types=' + types + '&headers=' + headers + '&format=text';
        var a = document.createElement('a');
        a.href = url;
        a.download = currentCenter.replace(/::/g, '_') + '_trace_d' + depth + '.txt';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    });

    // ===================== TYPE / ENUM DETAIL =====================

    function loadTypeDetail(typeName) {
        $.getJSON('/SourceMap/Type', { name: typeName }, function (data) {
            if (!data.type) return;
            var t = data.type;

            breadcrumbs.push({ id: typeName, label: typeName, type: 'type' });
            renderToolbar();

            var html = '<div class="sm-detail-header"><h3><span class="sm-kind-badge sm-kind-type">' + (t.kind === 'struct' ? 'S' : 'C') + '</span> ' + esc(t.name) + '</h3>';
            html += '<div class="sm-detail-file">' + esc(t.declaredIn) + (t.declaredAtLine ? ':' + t.declaredAtLine : '') + '</div></div>';

            if (t.inherits && t.inherits.length) {
                html += '<div class="sm-detail-section"><h4>Inherits</h4>';
                t.inherits.forEach(function (b) { html += '<span class="sm-type-pill" data-nav-type="' + esc(b) + '">' + esc(b) + '</span>'; });
                html += '</div>';
            }

            if (t.inheritedBy && t.inheritedBy.length) {
                html += '<div class="sm-detail-section"><h4>Inherited By</h4>';
                t.inheritedBy.forEach(function (c) { html += '<span class="sm-type-pill" data-nav-type="' + esc(c) + '">' + esc(c) + '</span>'; });
                html += '</div>';
            }

            if (t.members && t.members.length) {
                html += '<div class="sm-detail-section"><h4>Members (' + t.members.length + ')</h4><ul class="sm-detail-list">';
                t.members.forEach(function (m) { html += '<li style="cursor:default;">' + esc(m) + '</li>'; });
                html += '</ul></div>';
            }

            if (t.qualifiedMethods && t.qualifiedMethods.length) {
                html += '<div class="sm-detail-section"><h4>Methods (' + t.qualifiedMethods.length + ')</h4><ul class="sm-detail-list">';
                t.qualifiedMethods.forEach(function (qm) {
                    var shortName = qm.indexOf('::') !== -1 ? qm.split('::').pop() : qm;
                    html += '<li data-fid="' + esc(qm) + '">' + esc(shortName) + '</li>';
                });
                html += '</ul></div>';
            }

            $('#smDetailPanel').html(html).show().addClass('visible');
        });
    }

    function loadEnumDetail(enumName) {
        $.getJSON('/SourceMap/Enum', { name: enumName }, function (data) {
            if (!data.enum) return;
            var e = data.enum;

            var html = '<div class="sm-detail-header"><h3><span class="sm-kind-badge sm-kind-enum">E</span> ' + esc(e.name) + '</h3>';
            html += '<div class="sm-detail-file">' + esc(e.declaredIn) + ':' + e.lineStart + '-' + e.lineEnd + '</div></div>';

            if (e.values && e.values.length) {
                html += '<div class="sm-detail-section"><h4>Values (' + e.values.length + ')</h4><ul class="sm-detail-list">';
                e.values.forEach(function (v) {
                    html += '<li style="cursor:default;"><span style="flex:1;">' + esc(v.name) + '</span><span class="sm-meta-right">' + esc(v.value || '?') + '</span></li>';
                });
                html += '</ul></div>';
            }

            if (e.usedByFunctions && e.usedByFunctions.length) {
                html += '<div class="sm-detail-section"><h4>Used By (' + e.usedByFunctions.length + ')</h4><ul class="sm-detail-list">';
                e.usedByFunctions.forEach(function (fid) {
                    html += '<li data-fid="' + esc(fid) + '">' + esc(fid) + '</li>';
                });
                html += '</ul></div>';
            }

            $('#smDetailPanel').html(html).show().addClass('visible');
        });
    }

    function loadFileDetail(filePath) {
        $.getJSON('/SourceMap/File', { path: filePath }, function (data) {
            if (!data.file) return;
            var f = data.file;

            var html = '<div class="sm-detail-header"><h3><span class="sm-kind-badge sm-kind-file">F</span> ' + esc(f.fileName) + '</h3>';
            html += '<div class="sm-detail-file">' + esc(f.path) + ' · ' + f.lineCount + ' lines</div></div>';

            if (f.includes && f.includes.length) {
                html += '<div class="sm-detail-section"><h4>Includes (' + f.includes.length + ')</h4><ul class="sm-detail-list">';
                f.includes.forEach(function (inc) {
                    html += '<li style="cursor:default;">' + esc(inc) + '</li>';
                });
                html += '</ul></div>';
            }

            if (f.definedSymbols && f.definedSymbols.length) {
                html += '<div class="sm-detail-section"><h4>Defined Functions (' + f.definedSymbols.length + ')</h4><ul class="sm-detail-list">';
                f.definedSymbols.forEach(function (sym) {
                    var shortName = sym.indexOf('::') !== -1 ? sym.split('::').pop() : sym;
                    html += '<li data-fid="' + esc(sym) + '"><span style="flex:1;overflow:hidden;text-overflow:ellipsis;" title="' + esc(sym) + '">' + esc(shortName) + '</span></li>';
                });
                html += '</ul></div>';
            }

            if (f.declaredSymbols && f.declaredSymbols.length) {
                html += '<div class="sm-detail-section"><h4>Declared Functions (' + f.declaredSymbols.length + ')</h4><ul class="sm-detail-list">';
                f.declaredSymbols.forEach(function (sym) {
                    var shortName = sym.indexOf('::') !== -1 ? sym.split('::').pop() : sym;
                    html += '<li data-fid="' + esc(sym) + '"><span style="flex:1;overflow:hidden;text-overflow:ellipsis;" title="' + esc(sym) + '">' + esc(shortName) + '</span></li>';
                });
                html += '</ul></div>';
            }

            if (f.declaredTypes && f.declaredTypes.length) {
                html += '<div class="sm-detail-section"><h4>Types</h4>';
                f.declaredTypes.forEach(function (t) { html += '<span class="sm-type-pill" data-nav-type="' + esc(t) + '">' + esc(t) + '</span>'; });
                html += '</div>';
            }

            if (f.declaredEnums && f.declaredEnums.length) {
                html += '<div class="sm-detail-section"><h4>Enums</h4>';
                f.declaredEnums.forEach(function (e) {
                    html += '<div class="sm-item sm-enum-item" data-enum="' + esc(e) + '"><span class="sm-kind-badge sm-kind-enum">E</span>' + esc(e) + '</div>';
                });
                html += '</div>';
            }

            $('#smDetailPanel').html(html).show().addClass('visible');
        });
    }
    $(document).on('click', '[data-nav-type]', function () {
        var name = $(this).attr('data-nav-type');
        if (name) loadTypeDetail(name);
    });

    // Navigate on detail list item click (symbol)
    $(document).on('click', '.sm-detail-list li[data-fid]', function () {
        var fid = $(this).attr('data-fid');
        if (fid) loadNode(fid);
    });

    // ===================== GRAPH BUILDER =====================

    var SM_NODE_W = 200, SM_NODE_H = 68, SM_CENTER_W = 260, SM_CENTER_H = 80;
    var SM_BADGE_R = 14, SM_H_GAP = 24, SM_V_GAP = 60;

    function buildGraph(data) {
        var center = data.center;
        var neighbors = data.neighbors || [];

        var outgoing = [], incoming = [], both = [];
        neighbors.forEach(function (n) {
            if (n.direction === 'outgoing') outgoing.push(n);
            else if (n.direction === 'incoming') incoming.push(n);
            else both.push(n);
        });

        outgoing.sort(function (a, b) { return (b.max_depth || 0) - (a.max_depth || 0); });
        incoming.sort(function (a, b) { return (b.max_depth || 0) - (a.max_depth || 0); });
        both.sort(function (a, b) { return (b.max_depth || 0) - (a.max_depth || 0); });

        var topRow = incoming;
        var bottomRow = [].concat(outgoing, both);

        if (topRow.length === 0 && bottomRow.length === 0) {
            var svg = '<svg id="smGraphSvg" width="300" height="120" xmlns="http://www.w3.org/2000/svg">';
            svg += renderCenterNode(center, 30, 20);
            svg += '</svg>';
            $('#smGraphSvg').remove();
            $('#smGraphView').prepend(svg);
            smSvgW = 300; smSvgH = 120;
            smZoom = 1; smPanX = 0; smPanY = 0;
            smAttachWheel();
            setTimeout(function () { smFitToView(); }, 100);
            return;
        }

        var topRowW = topRow.length > 0 ? topRow.length * SM_NODE_W + (topRow.length - 1) * SM_H_GAP : 0;
        var bottomRowW = bottomRow.length > 0 ? bottomRow.length * SM_NODE_W + (bottomRow.length - 1) * SM_H_GAP : 0;
        var totalW = Math.max(topRowW, SM_CENTER_W, bottomRowW);
        var pad = 40;

        var topY = pad;
        var centerY = topRow.length > 0 ? topY + SM_NODE_H + SM_V_GAP : pad;
        var bottomY = centerY + SM_CENTER_H + SM_V_GAP;

        var centerX = pad + (totalW - SM_CENTER_W) / 2;
        var positions = {};
        positions['__center__'] = { x: centerX, y: centerY, w: SM_CENTER_W, h: SM_CENTER_H };

        var topStartX = pad + (totalW - topRowW) / 2;
        topRow.forEach(function (n, i) { positions[n.id] = { x: topStartX + i * (SM_NODE_W + SM_H_GAP), y: topY, w: SM_NODE_W, h: SM_NODE_H }; });

        var bottomStartX = pad + (totalW - bottomRowW) / 2;
        bottomRow.forEach(function (n, i) { positions[n.id] = { x: bottomStartX + i * (SM_NODE_W + SM_H_GAP), y: bottomY, w: SM_NODE_W, h: SM_NODE_H }; });

        var lastBottom = bottomRow.length > 0 ? bottomY + SM_NODE_H : centerY + SM_CENTER_H;
        smSvgW = totalW + pad * 2;
        smSvgH = lastBottom + pad;

        var svg = '<svg id="smGraphSvg" width="' + smSvgW + '" height="' + smSvgH + '" xmlns="http://www.w3.org/2000/svg">';
        svg += '<defs>';
        ['outgoing', 'incoming', 'both'].forEach(function (dir) {
            svg += '<marker id="sm-arrow-' + dir + '" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" class="sm-edge-arrow ' + dir + '" /></marker>';
        });
        svg += '</defs>';

        var cp = positions['__center__'];

        // Edges: callers → center
        topRow.forEach(function (n) {
            var pos = positions[n.id]; if (!pos) return;
            var x1 = pos.x + pos.w / 2, y1 = pos.y + pos.h, x2 = cp.x + cp.w / 2, y2 = cp.y;
            var cpy = y1 + (y2 - y1) * 0.5;
            svg += '<path class="sm-edge incoming" d="M' + x1.toFixed(1) + ',' + y1.toFixed(1) + ' C' + x1.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + y2.toFixed(1) + '" marker-end="url(#sm-arrow-incoming)" />';
        });

        // Edges: center → calls
        bottomRow.forEach(function (n) {
            var pos = positions[n.id]; if (!pos) return;
            var dir = n.direction === 'both' ? 'both' : 'outgoing';
            var x1 = cp.x + cp.w / 2, y1 = cp.y + cp.h, x2 = pos.x + pos.w / 2, y2 = pos.y;
            var cpy = y1 + (y2 - y1) * 0.5;
            svg += '<path class="sm-edge ' + dir + '" d="M' + x1.toFixed(1) + ',' + y1.toFixed(1) + ' C' + x1.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + y2.toFixed(1) + '" marker-end="url(#sm-arrow-' + dir + ')" />';
        });

        // Both: dashed return
        both.forEach(function (n) {
            var pos = positions[n.id]; if (!pos) return;
            var x1 = pos.x + pos.w / 2 + 12, y1 = pos.y, x2 = cp.x + cp.w / 2 + 12, y2 = cp.y + cp.h;
            var cpy = y2 + (y1 - y2) * 0.5;
            svg += '<path class="sm-edge both" style="stroke-dasharray:6 3;" d="M' + x1.toFixed(1) + ',' + y1.toFixed(1) + ' C' + x1.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + y2.toFixed(1) + '" marker-end="url(#sm-arrow-both)" />';
        });

        // Nodes
        svg += renderCenterNode(center, cp.x, cp.y);
        topRow.forEach(function (n) { var p = positions[n.id]; if (p) svg += renderConnectedNode(n, p.x, p.y); });
        bottomRow.forEach(function (n) { var p = positions[n.id]; if (p) svg += renderConnectedNode(n, p.x, p.y); });

        svg += '</svg>';
        $('#smGraphSvg').remove();
        $('#smGraphView').prepend(svg);
        smZoom = 1; smPanX = 0; smPanY = 0;
        smAttachWheel();
        setTimeout(function () { smFitToView(); }, 150);
    }

    function renderCenterNode(c, x, y) {
        var w = SM_CENTER_W, h = SM_CENTER_H;
        var safeId = 'sm-c-' + Math.random().toString(36).substr(2, 6);
        var g = '<g class="sm-node center" data-nid="' + escSvg(c.id) + '">';
        g += '<rect class="sm-node-bg" x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" />';
        g += '<clipPath id="' + safeId + '"><rect x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" rx="8" ry="8" /></clipPath>';
        g += '<rect class="sm-node-header" x="' + x + '" y="' + y + '" width="' + w + '" height="34" fill="var(--accent)" clip-path="url(#' + safeId + ')" />';
        var displayName = c.qualified.length > 30 ? c.name : c.qualified;
        g += '<text class="sm-node-title" x="' + (x + 10) + '" y="' + (y + 15) + '">' + escSvg(displayName) + '</text>';
        g += '<text class="sm-node-meta" x="' + (x + 10) + '" y="' + (y + 28) + '">' + c.line_count + ' lines · ' + c.call_count + ' calls · ' + c.caller_count + ' callers</text>';
        var shortFile = (c.file || '').replace(/^src\//, '');
        g += '<text class="sm-node-file" x="' + (x + 10) + '" y="' + (y + 48) + '">' + escSvg(shortFile) + ':' + c.line_start + '</text>';
        var shortSig = (c.signature || '').substring(0, 42);
        if ((c.signature || '').length > 42) shortSig += '...';
        g += '<text class="sm-node-class" x="' + (x + 10) + '" y="' + (y + 62) + '">' + escSvg(shortSig) + '</text>';
        g += '</g>';
        return g;
    }

    function renderConnectedNode(n, x, y) {
        var w = SM_NODE_W, h = SM_NODE_H;
        var safeId = 'sm-n-' + Math.random().toString(36).substr(2, 6);
        var g = '<g class="sm-node" data-nid="' + escSvg(n.id) + '">';
        g += '<rect class="sm-node-bg" x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" />';
        var headerColor = n.direction === 'outgoing' ? '#3b82f6' : n.direction === 'incoming' ? '#22c55e' : '#f59e0b';
        g += '<clipPath id="' + safeId + '"><rect x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" rx="8" ry="8" /></clipPath>';
        g += '<rect class="sm-node-header" x="' + x + '" y="' + y + '" width="' + w + '" height="30" fill="' + headerColor + '" clip-path="url(#' + safeId + ')" />';
        var displayName = n.qualified ? (n.qualified.length > 26 ? n.name : n.qualified) : n.id;
        g += '<text class="sm-node-title" x="' + (x + 10) + '" y="' + (y + 14) + '" font-size="11">' + escSvg(displayName) + '</text>';
        g += '<text class="sm-node-meta" x="' + (x + 10) + '" y="' + (y + 25) + '" font-size="9">' + n.call_count + ' calls · ' + n.caller_count + ' callers</text>';
        var shortFile = (n.file || '').replace(/^src\//, '');
        if (shortFile.length > 28) shortFile = '...' + shortFile.substring(shortFile.length - 25);
        g += '<text class="sm-node-file" x="' + (x + 10) + '" y="' + (y + 44) + '">' + escSvg(shortFile) + '</text>';
        if (n.className) g += '<text class="sm-node-class" x="' + (x + 10) + '" y="' + (y + 58) + '">' + escSvg(n.className) + '</text>';
        g += '</g>';
        return g;
    }

    // ===================== DETAIL PANEL =====================

    function renderDetailPanel(data) {
        var c = data.center;
        var neighbors = data.neighbors || [];
        var html = '<div class="sm-detail-header"><h3>' + esc(c.qualified) + '</h3>'
            + '<div class="sm-detail-file">' + esc(c.file) + ':' + c.line_start + '–' + c.line_end + '</div>'
            + '<div class="sm-detail-sig">' + esc(c.signature) + '</div></div>';

        html += '<div class="sm-detail-stats">'
            + statCell(c.call_count, 'Calls') + statCell(c.caller_count, 'Callers')
            + statCell(c.line_count, 'Lines') + statCell(c.max_depth || 0, 'Complexity')
            + '</div>';

        // Calls
        var outgoing = neighbors.filter(function (n) { return n.direction === 'outgoing' || n.direction === 'both'; });
        if (outgoing.length) {
            html += '<div class="sm-detail-section"><h4><span style="color:#3b82f6;">▸</span> Calls (' + outgoing.length + ')</h4><ul class="sm-detail-list">';
            outgoing.sort(function (a, b) { return (b.total_connections || 0) - (a.total_connections || 0); });
            outgoing.forEach(function (n) {
                html += '<li data-fid="' + esc(n.id) + '"><span class="sm-dir-icon ' + n.direction + '"><i class="fa-solid fa-arrow-right"></i></span><span style="flex:1;overflow:hidden;text-overflow:ellipsis;" title="' + esc(n.qualified || n.id) + '">' + esc(n.qualified || n.id) + '</span></li>';
            });
            html += '</ul></div>';
        }

        // Called By
        var incoming = neighbors.filter(function (n) { return n.direction === 'incoming' || n.direction === 'both'; });
        if (incoming.length) {
            html += '<div class="sm-detail-section"><h4><span style="color:#22c55e;">◂</span> Called By (' + incoming.length + ')</h4><ul class="sm-detail-list">';
            incoming.sort(function (a, b) { return (b.total_connections || 0) - (a.total_connections || 0); });
            incoming.forEach(function (n) {
                html += '<li data-fid="' + esc(n.id) + '"><span class="sm-dir-icon ' + n.direction + '"><i class="fa-solid fa-arrow-left"></i></span><span style="flex:1;overflow:hidden;text-overflow:ellipsis;" title="' + esc(n.qualified || n.id) + '">' + esc(n.qualified || n.id) + '</span></li>';
            });
            html += '</ul></div>';
        }

        // Uses Types
        if (c.usesTypes && c.usesTypes.length) {
            html += '<div class="sm-detail-section"><h4>Uses Types</h4>';
            c.usesTypes.forEach(function (t) { html += '<span class="sm-type-pill" data-nav-type="' + esc(t) + '">' + esc(t) + '</span>'; });
            html += '</div>';
        }

        // Member Of
        if (c.memberOf) {
            html += '<div class="sm-detail-section"><h4>Member Of</h4><span class="sm-type-pill" data-nav-type="' + esc(c.memberOf) + '">' + esc(c.memberOf) + '</span></div>';
        }

        $('#smDetailPanel').html(html).show().addClass('visible');
    }

    function statCell(val, label) {
        return '<div class="sm-stat-cell"><div class="sm-stat-val">' + (val || 0).toLocaleString() + '</div><div class="sm-stat-label">' + label + '</div></div>';
    }

    // ===================== SVG NODE CLICK =====================

    $(document).on('click', '.sm-node', function () {
        if (smDragged) return;
        var el = $(this).closest('[data-nid]');
        var nid = el.data('nid');
        if (!nid || nid === currentCenter) return;
        loadNode(nid);
    });

    // ===================== ZOOM / PAN =====================

    function smApplyTransform() { $('#smGraphSvg').css('transform', 'translate(' + smPanX + 'px,' + smPanY + 'px) scale(' + smZoom + ')'); }

    function smFitToView() {
        var container = $('#smGraphView');
        var cw = container.width(), ch = container.height();
        if (!cw || !ch || !smSvgW || !smSvgH) return;
        var scaleX = (cw - 40) / smSvgW, scaleY = (ch - 40) / smSvgH;
        smZoom = Math.min(scaleX, scaleY, 1.5);
        smZoom = Math.max(smZoom, 0.05);
        smPanX = (cw - smSvgW * smZoom) / 2;
        smPanY = (ch - smSvgH * smZoom) / 2;
        smApplyTransform();
    }

    $(document).on('click', '#smZoomIn', function () { smZoom = Math.min(smZoom * 1.25, 4); smApplyTransform(); });
    $(document).on('click', '#smZoomOut', function () { smZoom = Math.max(smZoom * 0.8, 0.05); smApplyTransform(); });
    $(document).on('click', '#smZoomFit', function () { smFitToView(); });

    function smHandleWheel(e) {
        e.preventDefault();
        var delta = e.deltaY < 0 ? 1.1 : 0.9;
        var newZoom = Math.max(0.05, Math.min(smZoom * delta, 5));
        var el = document.getElementById('smGraphView');
        if (!el) return;
        var rect = el.getBoundingClientRect();
        var mx = e.clientX - rect.left, my = e.clientY - rect.top;
        smPanX = mx - (mx - smPanX) * (newZoom / smZoom);
        smPanY = my - (my - smPanY) * (newZoom / smZoom);
        smZoom = newZoom;
        smApplyTransform();
    }

    function smAttachWheel() { smDetachWheel(); var el = document.getElementById('smGraphView'); if (el) el.addEventListener('wheel', smHandleWheel, { passive: false }); }
    function smDetachWheel() { var el = document.getElementById('smGraphView'); if (el) el.removeEventListener('wheel', smHandleWheel); }

    $(document).on('mousedown', '#smGraphView', function (e) {
        if ($(e.target).closest('button, .sm-controls, .sm-legend').length) return;
        smIsPanning = true; smDragged = false;
        smPanStartX = e.clientX; smPanStartY = e.clientY;
        smPanStartPanX = smPanX; smPanStartPanY = smPanY;
        $(this).addClass('grabbing'); e.preventDefault();
    });

    $(document).on('mousemove', function (e) {
        if (!smIsPanning) return;
        var dx = e.clientX - smPanStartX, dy = e.clientY - smPanStartY;
        if (Math.abs(dx) > 3 || Math.abs(dy) > 3) smDragged = true;
        smPanX = smPanStartPanX + dx; smPanY = smPanStartPanY + dy;
        smApplyTransform();
    });

    $(document).on('mouseup', function () {
        if (smIsPanning) { smIsPanning = false; $('#smGraphView').removeClass('grabbing'); setTimeout(function () { smDragged = false; }, 50); }
    });

    // ===================== MODE TOGGLE =====================

    $(document).on('click', '.sm-mode-btn', function () {
        var mode = $(this).data('mode');
        $('.sm-mode-btn').removeClass('active');
        $(this).addClass('active');

        $('#smMapMode').hide();
        $('#smTopicMode').hide();
        $('#smSmartMode').hide();

        if (mode === 'topic') {
            $('#smTopicMode').show();
            $('#smTopicInput').focus();
        } else if (mode === 'smart') {
            $('#smSmartMode').show();
            $('#smSmartInput').focus();
        } else {
            $('#smMapMode').show();
        }
    });

    // ===================== SMART SEARCH =====================

    $(document).on('click', '#smSmartGo', function () { runSmartSearch(); });
    $('#smSmartInput').on('keypress', function (e) { if (e.which === 13) runSmartSearch(); });

    function runSmartSearch() {
        var query = $('#smSmartInput').val().trim();
        if (!query) return;

        $('#smSmartResults').html('<div style="text-align:center;padding:40px;color:var(--text-muted);"><i class="fa-solid fa-spinner fa-spin" style="font-size:24px;"></i><div style="margin-top:8px;">Resolving expression...</div></div>');

        $.getJSON('/SourceMap/SmartSearch', { q: query }, function (data) {
            renderSmartSearchPanel(data);
        }).fail(function () {
            $('#smSmartResults').html('<div style="text-align:center;padding:40px;color:var(--status-error);"><i class="fa-solid fa-exclamation-triangle"></i> Search failed</div>');
        });
    }

    function renderSmartSearchPanel(data) {
        var matches = data.matches || [];
        var html = '';

        // ── Parsed expression banner ──
        html += '<div class="card" style="padding: 14px 18px; margin-bottom: 16px;">';
        html += '<div style="display:flex;align-items:center;gap:8px;flex-wrap:wrap;">';
        html += '<i class="fa-solid fa-wand-magic-sparkles" style="color: #a855f7;"></i> ';
        html += '<span style="font-size: 13px; font-weight: 600; color: var(--text-primary);">Parsed:</span> ';

        if (data.expressionType === 'pointer_member') {
            html += '<code style="font-size:14px;padding:2px 6px;border-radius:4px;background:var(--bg-tertiary);">'
                + '<span style="color:var(--text-secondary);">' + esc(data.variable) + '</span>'
                + '<span style="color:var(--text-muted);">-></span>'
                + '<span style="color:#3b82f6;font-weight:700;">' + esc(data.methodName) + '</span>'
                + '<span style="color:var(--text-muted);">()</span></code>';
        } else if (data.expressionType === 'dot_member') {
            html += '<code style="font-size:14px;padding:2px 6px;border-radius:4px;background:var(--bg-tertiary);">'
                + '<span style="color:var(--text-secondary);">' + esc(data.variable) + '</span>'
                + '<span style="color:var(--text-muted);">.</span>'
                + '<span style="color:#3b82f6;font-weight:700;">' + esc(data.methodName) + '</span>'
                + '<span style="color:var(--text-muted);">()</span></code>';
        } else if (data.expressionType === 'scope_resolution') {
            html += '<code style="font-size:14px;padding:2px 6px;border-radius:4px;background:var(--bg-tertiary);">'
                + '<span style="color:var(--text-secondary);">' + esc(data.explicitClass) + '</span>'
                + '<span style="color:var(--text-muted);">::</span>'
                + '<span style="color:#3b82f6;font-weight:700;">' + esc(data.methodName) + '</span>'
                + '<span style="color:var(--text-muted);">()</span></code>';
        } else {
            html += '<code style="font-size:14px;padding:2px 6px;border-radius:4px;background:var(--bg-tertiary);">'
                + '<span style="color:#3b82f6;font-weight:700;">' + esc(data.methodName) + '</span>'
                + '<span style="color:var(--text-muted);">()</span></code>';
        }

        if (data.resolvedTypes && data.resolvedTypes.length) {
            html += '<span style="margin-left:8px;font-size:12px;color:var(--text-muted);">';
            html += '<i class="fa-solid fa-link" style="font-size:10px;margin-right:3px;"></i>';
            html += esc(data.variable || data.explicitClass || '?') + ' resolves to: ';
            html += data.resolvedTypes.map(function (t) {
                return '<span class="sm-type-pill" data-nav-type="' + esc(t) + '" style="cursor:pointer;">' + esc(t) + '</span>';
            }).join(' ');
            html += '</span>';
        }

        html += '</div></div>';

        if (matches.length === 0) {
            html += '<div class="card" style="text-align:center;padding:40px;">';
            html += '<i class="fa-solid fa-ghost" style="font-size:36px;color:var(--text-muted);opacity:0.4;margin-bottom:12px;display:block;"></i>';
            html += '<div style="font-size:14px;color:var(--text-secondary);">No implementations found for <strong>' + esc(data.methodName || '') + '</strong></div>';
            html += '<div style="font-size:12px;color:var(--text-muted);margin-top:4px;">The method may not be indexed yet. Try running Reindex.</div>';
            html += '</div>';
            $('#smSmartResults').html(html);
            return;
        }

        // ── Group by confidence ──
        var highConf = [], possible = [];
        matches.forEach(function (m) {
            if (m.confidence === 'high') highConf.push(m);
            else possible.push(m);
        });

        // ── Likely Matches ──
        if (highConf.length) {
            html += '<div class="card" style="margin-bottom:12px;overflow:hidden;">';
            html += '<div style="padding:10px 16px;background:rgba(34,197,94,0.06);border-bottom:1px solid var(--border-color);display:flex;align-items:center;gap:6px;">';
            html += '<i class="fa-solid fa-bullseye" style="color:#22c55e;"></i>';
            html += '<span style="font-weight:700;font-size:13px;color:var(--text-primary);">Likely Match</span>';
            html += '<span style="font-size:11px;color:var(--text-muted);">(' + highConf.length + ')</span>';
            html += '</div>';
            highConf.forEach(function (m) { html += renderSmartMatchRow(m); });
            html += '</div>';
        }

        // ── Other Implementations ──
        if (possible.length) {
            html += '<div class="card" style="overflow:hidden;">';
            html += '<div style="padding:10px 16px;border-bottom:1px solid var(--border-color);display:flex;align-items:center;gap:6px;">';
            html += '<i class="fa-solid fa-code" style="color:#64748b;"></i>';
            html += '<span style="font-weight:700;font-size:13px;color:var(--text-primary);">Other Implementations</span>';
            html += '<span style="font-size:11px;color:var(--text-muted);">(' + possible.length + ')</span>';
            html += '</div>';
            possible.forEach(function (m) { html += renderSmartMatchRow(m); });
            html += '</div>';
        }

        // ── Summary ──
        html += '<div style="text-align:center;padding:12px;font-size:11px;color:var(--text-muted);">';
        html += data.totalMatches + ' implementation' + (data.totalMatches !== 1 ? 's' : '') + ' of <strong>' + esc(data.methodName) + '</strong> found across the codebase';
        html += '</div>';

        $('#smSmartResults').html(html);
    }

    function renderSmartMatchRow(m) {
        var shortFile = (m.file || '').replace(/^src\//, '');

        var meta = [];
        if (m.callerCount > 0) meta.push(m.callerCount + ' callers');
        if (m.callCount > 0) meta.push(m.callCount + ' calls');
        if (m.lineCount > 0) meta.push(m.lineCount + ' lines');
        var metaStr = meta.join(' · ');

        var confDot = m.confidence === 'high'
            ? '<span style="display:inline-block;width:8px;height:8px;border-radius:4px;background:#22c55e;margin-right:8px;flex-shrink:0;" title="High confidence"></span>'
            : '<span style="display:inline-block;width:8px;height:8px;border-radius:4px;background:#94a3b8;margin-right:8px;flex-shrink:0;opacity:0.4;" title="Possible match"></span>';

        var virtualBadge = m.isVirtual ? '<span style="font-size:10px;color:#a855f7;background:rgba(168,85,247,0.1);padding:1px 5px;border-radius:3px;margin-left:6px;">virtual</span>' : '';
        var kindBadge = m.kind ? '<span style="font-size:10px;color:var(--text-muted);background:var(--bg-tertiary);padding:1px 5px;border-radius:3px;margin-left:4px;">' + esc(m.kind) + '</span>' : '';

        var inhLine = '';
        if (m.inheritancePath) {
            inhLine = '<div style="font-size:11px;color:var(--text-muted);margin-top:2px;padding-left:16px;">'
                + '<i class="fa-solid fa-sitemap" style="font-size:9px;margin-right:4px;color:#a855f7;"></i>'
                + esc(m.inheritancePath) + '</div>';
        }

        return '<div class="sm-smart-row" data-fid="' + esc(m.id) + '" style="padding:10px 16px;border-bottom:1px solid var(--border-color);cursor:pointer;transition:background 0.1s;" onmouseover="this.style.background=\'var(--bg-secondary)\'" onmouseout="this.style.background=\'transparent\'">'
            + '<div style="display:flex;align-items:center;">'
            + confDot
            + '<div style="flex:1;min-width:0;">'
            + '<div style="display:flex;align-items:center;gap:4px;">'
            + '<span style="font-size:13px;font-weight:600;color:var(--text-primary);" title="' + esc(m.id) + '">'
            + '<span style="color:var(--text-muted);font-weight:400;">' + esc(m.memberOf ? m.memberOf + '::' : '') + '</span>'
            + esc(m.name)
            + '</span>'
            + virtualBadge + kindBadge
            + '</div>'
            + '<div style="font-size:11px;color:var(--text-muted);margin-top:2px;">'
            + '<span>' + esc(shortFile) + (m.lineStart ? ':' + m.lineStart : '') + '</span>'
            + (metaStr ? '<span style="margin-left:12px;">' + metaStr + '</span>' : '')
            + '</div>'
            + inhLine
            + '</div>'
            + '</div></div>';
    }

    // Click a smart search result → switch to Map mode and navigate
    $(document).on('click', '.sm-smart-row', function () {
        var fid = $(this).attr('data-fid');
        if (!fid) return;
        // Switch to Map mode
        $('.sm-mode-btn').removeClass('active');
        $('.sm-mode-btn[data-mode="map"]').addClass('active');
        $('#smSmartMode').hide();
        $('#smTopicMode').hide();
        $('#smMapMode').show();
        // Navigate
        breadcrumbs = [];
        loadNode(fid);
    });

    // ===================== TOPIC EXPLORER =====================

    $(document).on('click', '#smTopicGo', function () { runTopicExplore(); });
    $('#smTopicInput').on('keypress', function (e) { if (e.which === 13) runTopicExplore(); });

    var topicReport = null;

    function runTopicExplore() {
        var query = $('#smTopicInput').val().trim();
        if (!query) return;

        $('#smTopicResults').html('<div style="text-align:center; padding: 30px; color: var(--text-muted);"><i class="fa-solid fa-spinner fa-spin" style="font-size: 20px;"></i><p style="margin-top: 8px;">Analyzing "' + esc(query) + '"...</p></div>');

        $.getJSON('/SourceMap/TopicExplore', { q: query }, function (data) {
            topicReport = data;
            if (!data || data.totalSymbols === 0) {
                $('#smTopicResults').html('<div class="card" style="padding: 24px; text-align: center; color: var(--text-muted);"><i class="fa-solid fa-ghost" style="font-size: 28px; opacity: 0.3; margin-bottom: 8px;"></i><p>No matches found for "' + esc(query) + '"</p><p style="font-size: 12px;">Try broader terms, or single keywords like "taxi" instead of "taxi system"</p></div>');
                return;
            }
            renderTopicReport(data);
        }).fail(function () {
            $('#smTopicResults').html('<div style="padding: 20px; color: var(--status-error);">Topic explore failed</div>');
        });
    }

    function renderTopicReport(r) {
        var html = '<div class="card" style="padding: 16px;">';

        // Header
        html += '<div style="display: flex; justify-content: space-between; align-items: center; margin-bottom: 12px;">';
        html += '<div><h3 style="margin: 0; font-size: 16px; color: var(--text-primary);">Topic: "' + esc(r.query) + '"</h3>';
        html += '<span style="font-size: 12px; color: var(--text-muted);">' + r.totalSymbols + ' functions · ' + r.totalTypes + ' types · ' + r.totalEnums + ' enums · ' + r.totalFiles + ' files</span></div>';
        html += '<div style="display:flex; gap: 6px;">';
        html += '<button id="smTopicDeepDive" class="btn btn-sm" style="background: var(--accent); color: #fff; border-color: var(--accent);"><i class="fa-solid fa-microscope"></i> Deep Dive</button>';
        html += '<button id="smTopicCopyReport" class="btn btn-sm"><i class="fa-solid fa-copy"></i> Copy Report</button>';
        html += '<button id="smTopicDownload" class="btn btn-sm"><i class="fa-solid fa-download"></i> Download .txt</button>';
        html += '</div></div>';

        // Entry Points
        if (r.entryPoints && r.entryPoints.length) {
            html += '<div class="sm-topic-section">';
            html += '<div class="sm-topic-section-header"><i class="fa-solid fa-door-open" style="color: #ef4444;"></i> Entry Points (' + r.entryPoints.length + ')<span style="font-weight:400;text-transform:none;letter-spacing:0;margin-left:6px;color:var(--text-muted);">— external triggers, opcode handlers</span></div>';
            r.entryPoints.forEach(function (s) { html += renderTopicSym(s); });
            html += '</div>';
        }

        // Core Logic
        if (r.coreLogic && r.coreLogic.length) {
            html += '<div class="sm-topic-section">';
            html += '<div class="sm-topic-section-header"><i class="fa-solid fa-gears" style="color: #3b82f6;"></i> Core Logic (' + r.coreLogic.length + ')<span style="font-weight:400;text-transform:none;letter-spacing:0;margin-left:6px;color:var(--text-muted);">— called by + calls other topic functions</span></div>';
            r.coreLogic.forEach(function (s) { html += renderTopicSym(s); });
            html += '</div>';
        }

        // Helpers
        if (r.helpers && r.helpers.length) {
            html += '<div class="sm-topic-section">';
            html += '<div class="sm-topic-section-header"><i class="fa-solid fa-wrench" style="color: #22c55e;"></i> Helpers (' + r.helpers.length + ')<span style="font-weight:400;text-transform:none;letter-spacing:0;margin-left:6px;color:var(--text-muted);">— leaf functions, utilities</span></div>';
            r.helpers.forEach(function (s) { html += renderTopicSym(s); });
            html += '</div>';
        }

        // Other
        if (r.other && r.other.length) {
            html += '<div class="sm-topic-section">';
            html += '<div class="sm-topic-section-header"><i class="fa-solid fa-ellipsis" style="color: #64748b;"></i> Other (' + r.other.length + ')</div>';
            r.other.forEach(function (s) { html += renderTopicSym(s); });
            html += '</div>';
        }

        // Types
        if (r.types && r.types.length) {
            html += '<div class="sm-topic-section">';
            html += '<div class="sm-topic-section-header"><i class="fa-solid fa-shapes" style="color: #8b5cf6;"></i> Types (' + r.types.length + ')</div>';
            r.types.forEach(function (t) {
                var inh = t.inherits && t.inherits.length ? ' : ' + t.inherits.join(', ') : '';
                html += '<div class="sm-topic-sym" data-nav-type="' + esc(t.name) + '">';
                html += '<span class="sm-kind-badge sm-kind-type">' + (t.kind === 'struct' ? 'S' : 'C') + '</span>';
                html += '<span class="sm-topic-id">' + esc(t.name) + esc(inh) + '</span>';
                html += '<span class="sm-topic-stats">' + t.methodCount + ' methods, ' + t.memberCount + ' members</span>';
                html += '</div>';
            });
            html += '</div>';
        }

        // Related Types
        if (r.relatedTypes && r.relatedTypes.length) {
            html += '<div class="sm-topic-section">';
            html += '<div class="sm-topic-section-header"><i class="fa-solid fa-link" style="color: #8b5cf6; opacity: 0.5;"></i> Related Types<span style="font-weight:400;text-transform:none;letter-spacing:0;margin-left:6px;color:var(--text-muted);">— used by matched functions</span></div>';
            r.relatedTypes.forEach(function (t) {
                html += '<div class="sm-topic-sym" data-nav-type="' + esc(t.name) + '">';
                html += '<span class="sm-kind-badge sm-kind-type">C</span>';
                html += '<span class="sm-topic-id">' + esc(t.name) + '</span>';
                html += '<span class="sm-topic-stats">used by ' + t.usedByCount + ' matched</span>';
                html += '</div>';
            });
            html += '</div>';
        }

        // Enums
        if (r.enums && r.enums.length) {
            html += '<div class="sm-topic-section">';
            html += '<div class="sm-topic-section-header"><i class="fa-solid fa-list-ol" style="color: #f59e0b;"></i> Enums (' + r.enums.length + ')</div>';
            r.enums.forEach(function (e) {
                html += '<div class="sm-topic-sym" data-nav-enum="' + esc(e.name) + '">';
                html += '<span class="sm-kind-badge sm-kind-enum">E</span>';
                html += '<span class="sm-topic-id">' + esc(e.name) + '</span>';
                html += '<span class="sm-topic-stats">' + e.valueCount + ' values</span>';
                html += '</div>';
                if (e.sampleValues && e.sampleValues.length) {
                    html += '<div style="padding: 2px 10px 6px 36px; font-size: 10.5px; color: var(--text-muted); font-family: Consolas, monospace;">' + e.sampleValues.map(function (v) { return esc(v); }).join(', ') + (e.valueCount > 8 ? ', ...' : '') + '</div>';
                }
            });
            html += '</div>';
        }

        // Files
        if (r.files && r.files.length) {
            html += '<div class="sm-topic-section">';
            html += '<div class="sm-topic-section-header"><i class="fa-solid fa-file-code" style="color: #22c55e;"></i> Files (' + r.files.length + ')</div>';
            r.files.forEach(function (f) {
                var shortPath = f.path.replace(/^src\//, '');
                html += '<div class="sm-topic-sym" data-nav-file="' + esc(f.path) + '">';
                html += '<span class="sm-topic-id">' + esc(shortPath) + '</span>';
                html += '<span class="sm-topic-stats">' + f.matchedFunctions.length + ' matched / ' + f.totalFunctions + ' total</span>';
                html += '</div>';
            });
            html += '</div>';
        }

        // Call flows
        if (r.callFlows && r.callFlows.length) {
            html += '<div class="sm-topic-section">';
            html += '<div class="sm-topic-section-header"><i class="fa-solid fa-arrows-left-right" style="color: #06b6d4;"></i> Internal Call Flow</div>';
            r.callFlows.forEach(function (flow) {
                var shortFrom = flow.from.length > 40 ? '...' + flow.from.slice(-37) : flow.from;
                html += '<div class="sm-topic-flow">' + esc(shortFrom) + ' → ' + flow.to.map(function (t) {
                    var short = t.indexOf('::') !== -1 ? t.split('::').pop() : t;
                    return '<span class="sm-type-pill" data-fid="' + esc(t) + '" style="cursor:pointer;">' + esc(short) + '</span>';
                }).join('  ') + '</div>';
            });
            html += '</div>';
        }

        html += '</div>';
        $('#smTopicResults').html(html);
    }

    function renderTopicSym(s) {
        var shortFile = (s.file || '').replace(/^src\//, '').replace(/^game\//, '');
        if (shortFile.length > 30) shortFile = '...' + shortFile.slice(-27);
        var stats = s.totalCallers + ' callers · ' + s.totalCalls + ' calls · ' + s.lineCount + ' lines';
        var html = '<div class="sm-topic-sym" data-fid="' + esc(s.id) + '">';
        html += '<span class="sm-topic-id" title="' + esc(s.signature) + '">' + esc(s.id) + '</span>';
        html += '<span class="sm-topic-stats">' + stats + '</span>';
        html += '<span class="sm-topic-file">' + esc(shortFile) + '</span>';
        html += '</div>';
        return html;
    }

    // Topic report clicks
    $(document).on('click', '.sm-topic-sym[data-fid]', function () {
        var fid = $(this).attr('data-fid');
        if (fid) {
            // Switch to map mode and navigate
            $('.sm-mode-btn').removeClass('active');
            $('.sm-mode-btn[data-mode="map"]').addClass('active');
            $('#smTopicMode').hide();
            $('#smSmartMode').hide();
            $('#smMapMode').show();
            breadcrumbs = [];
            loadNode(fid);
        }
    });

    $(document).on('click', '.sm-topic-sym[data-nav-type]', function () {
        var name = $(this).attr('data-nav-type');
        if (name) {
            $('.sm-mode-btn').removeClass('active');
            $('.sm-mode-btn[data-mode="map"]').addClass('active');
            $('#smTopicMode').hide();
            $('#smSmartMode').hide();
            $('#smMapMode').show();
            loadTypeDetail(name);
        }
    });

    $(document).on('click', '.sm-topic-sym[data-nav-enum]', function () {
        var name = $(this).attr('data-nav-enum');
        if (name) loadEnumDetail(name);
    });

    $(document).on('click', '#smTopicCopyReport', function () {
        if (!topicReport || !topicReport.formattedText) return;
        copyToClipboard(topicReport.formattedText).then(function () {
            var btn = $('#smTopicCopyReport');
            btn.html('<i class="fa-solid fa-check"></i> Copied');
            setTimeout(function () { btn.html('<i class="fa-solid fa-copy"></i> Copy Report'); }, 1500);
        });
    });

    $(document).on('click', '#smTopicDownload', function () {
        if (!topicReport) return;
        var query = topicReport.query || 'topic';
        var url = '/SourceMap/TopicExplore?q=' + encodeURIComponent(query) + '&format=text';
        var a = document.createElement('a');
        a.href = url;
        a.download = 'topic_' + query.replace(/\s+/g, '_') + '.txt';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
    });

    // Also handle clicks on flow pills
    $(document).on('click', '.sm-topic-flow .sm-type-pill[data-fid]', function (e) {
        e.stopPropagation();
        var fid = $(this).attr('data-fid');
        if (fid) {
            $('.sm-mode-btn').removeClass('active');
            $('.sm-mode-btn[data-mode="map"]').addClass('active');
            $('#smTopicMode').hide();
            $('#smSmartMode').hide();
            $('#smMapMode').show();
            breadcrumbs = [];
            loadNode(fid);
        }
    });

    // ===================== DEEP DIVE MODAL =====================

    var ddActiveTab = 'files';
    var ddSelections = { files: {}, functions: {}, types: {}, enums: {} };
    var ddBundleResult = null;

    $(document).on('click', '#smTopicDeepDive', function () {
        if (!topicReport) return;
        ddSelections = { files: {}, functions: {}, types: {}, enums: {} };
        ddBundleResult = null;
        ddActiveTab = 'files';
        openDeepDiveModal();
    });

    function openDeepDiveModal() {
        // Build data structures from topic report
        var r = topicReport;
        var modal = '<div class="sm-dd-overlay" id="smDdOverlay">';
        modal += '<div class="sm-dd-modal">';

        // Header
        modal += '<div class="sm-dd-header">';
        modal += '<h3><i class="fa-solid fa-microscope"></i> Deep Dive: "' + esc(r.query) + '"</h3>';
        modal += '<button class="sm-dd-close" id="smDdClose"><i class="fa-solid fa-xmark"></i></button>';
        modal += '</div>';

        // Tabs
        var fileCount = ddGetUniqueFiles().length;
        var funcCount = ddGetAllFunctions().length;
        var typeCount = ddGetAllTypes().length;
        var enumCount = (r.enums || []).length;

        modal += '<div class="sm-dd-tabs">';
        modal += '<button class="active" data-dd-tab="files"><i class="fa-solid fa-file-code"></i> Files <span class="sm-dd-tab-count">' + fileCount + '</span></button>';
        modal += '<button data-dd-tab="functions"><i class="fa-solid fa-code"></i> Functions <span class="sm-dd-tab-count">' + funcCount + '</span></button>';
        modal += '<button data-dd-tab="types"><i class="fa-solid fa-shapes"></i> Types <span class="sm-dd-tab-count">' + typeCount + '</span></button>';
        modal += '<button data-dd-tab="enums"><i class="fa-solid fa-list-ol"></i> Enums <span class="sm-dd-tab-count">' + enumCount + '</span></button>';
        modal += '</div>';

        // Body
        modal += '<div class="sm-dd-body" id="smDdBody"></div>';

        // Footer
        modal += '<div class="sm-dd-footer">';
        modal += '<div class="sm-dd-selection-info" id="smDdSelInfo">Select items to include in your context package</div>';
        modal += '<div class="sm-dd-actions">';
        modal += '<button class="btn btn-sm" id="smDdGenerate" disabled><i class="fa-solid fa-bolt"></i> Generate Package</button>';
        modal += '<button class="btn btn-sm" id="smDdCopy" style="display:none;"><i class="fa-solid fa-copy"></i> Copy</button>';
        modal += '<button class="btn btn-sm" id="smDdDownload" style="display:none;"><i class="fa-solid fa-download"></i> Download .txt</button>';
        modal += '</div>';
        modal += '</div>';

        modal += '</div></div>';
        $('body').append(modal);
        ddRenderTab();
    }

    // Close modal
    $(document).on('click', '#smDdClose', function () { $('#smDdOverlay').remove(); });
    $(document).on('click', '#smDdOverlay', function (e) { if (e.target === this) $(this).remove(); });
    $(document).on('keydown', function (e) { if (e.key === 'Escape') $('#smDdOverlay').remove(); });

    // Tab switching
    $(document).on('click', '.sm-dd-tabs button', function () {
        ddActiveTab = $(this).data('dd-tab');
        $('.sm-dd-tabs button').removeClass('active');
        $(this).addClass('active');
        ddRenderTab();
    });

    // ── Data extraction helpers ──

    function ddGetUniqueFiles() {
        if (!topicReport) return [];
        return (topicReport.files || []).map(function (f) {
            return { path: f.path, matched: f.matchedFunctions ? f.matchedFunctions.length : 0, total: f.totalFunctions };
        });
    }

    function ddGetAllFunctions() {
        if (!topicReport) return [];
        var all = [];
        ['entryPoints', 'coreLogic', 'helpers', 'other'].forEach(function (cat) {
            (topicReport[cat] || []).forEach(function (s) {
                all.push({ id: s.id, category: cat, callers: s.totalCallers, calls: s.totalCalls, lines: s.lineCount, file: s.file, memberOf: s.memberOf || '' });
            });
        });
        return all;
    }

    function ddGetAllTypes() {
        if (!topicReport) return [];
        var all = [];
        (topicReport.types || []).forEach(function (t) {
            all.push({ name: t.name, kind: t.kind, methods: t.methodCount, members: t.memberCount, declaredIn: t.declaredIn, source: 'matched' });
        });
        (topicReport.relatedTypes || []).forEach(function (t) {
            all.push({ name: t.name, kind: 'class', methods: 0, members: 0, declaredIn: t.declaredIn || '', source: 'related', usedBy: t.usedByCount });
        });
        return all;
    }

    // ── Tab rendering ──

    function ddRenderTab() {
        var html = '';
        if (ddActiveTab === 'files') html = ddRenderFilesTab();
        else if (ddActiveTab === 'functions') html = ddRenderFunctionsTab();
        else if (ddActiveTab === 'types') html = ddRenderTypesTab();
        else if (ddActiveTab === 'enums') html = ddRenderEnumsTab();
        $('#smDdBody').html(html);
        ddUpdateSelectionInfo();
    }

    function ddRenderFilesTab() {
        var files = ddGetUniqueFiles();
        var html = '<div style="margin-bottom:8px; font-size:11.5px; color:var(--text-muted);">Select source files to include as full file content. Each selected file becomes a separate section in the export.</div>';
        html += '<div class="sm-dd-group">';
        html += '<div class="sm-dd-group-header"><span><i class="fa-solid fa-file-code" style="margin-right:4px; color:#22c55e;"></i> Source Files</span><span class="sm-dd-select-all" data-dd-selectall="files">Select all</span></div>';
        files.forEach(function (f) {
            var shortPath = f.path.replace(/^src\//, '');
            var checked = ddSelections.files[f.path] ? ' checked' : '';
            var sel = ddSelections.files[f.path] ? ' selected' : '';
            var ratio = f.matched + '/' + f.total + ' matched';
            html += '<div class="sm-dd-item' + sel + '" data-dd-key="' + esc(f.path) + '" data-dd-cat="files">';
            html += '<input type="checkbox"' + checked + ' />';
            html += '<span class="sm-dd-item-name">' + esc(shortPath) + '</span>';
            html += '<span class="sm-dd-item-meta">' + ratio + '</span>';
            html += '</div>';
        });
        html += '</div>';
        return html;
    }

    function ddRenderFunctionsTab() {
        var funcs = ddGetAllFunctions();
        // Group by memberOf (class name)
        var groups = {};
        funcs.forEach(function (f) {
            var grp = f.memberOf || '(free functions)';
            if (!groups[grp]) groups[grp] = [];
            groups[grp].push(f);
        });

        var html = '<div style="margin-bottom:8px; font-size:11.5px; color:var(--text-muted);">Select functions to extract bodies. Grouped by class — all selected functions export into a single context file.</div>';
        var groupNames = Object.keys(groups).sort();
        groupNames.forEach(function (grpName) {
            var items = groups[grpName];
            html += '<div class="sm-dd-group">';
            html += '<div class="sm-dd-group-header"><span><i class="fa-solid fa-code" style="margin-right:4px; color:#3b82f6;"></i> ' + esc(grpName) + ' (' + items.length + ')</span><span class="sm-dd-select-all" data-dd-selectall-group="functions" data-dd-grp="' + esc(grpName) + '">Select all</span></div>';
            items.sort(function (a, b) { return a.id.localeCompare(b.id); });
            items.forEach(function (f) {
                var checked = ddSelections.functions[f.id] ? ' checked' : '';
                var sel = ddSelections.functions[f.id] ? ' selected' : '';
                var shortName = f.id.indexOf('::') !== -1 ? f.id.split('::').pop() : f.id;
                var meta = f.callers + ' callers · ' + f.calls + ' calls';
                if (f.lines > 0) meta += ' · ' + f.lines + ' lines';
                html += '<div class="sm-dd-item' + sel + '" data-dd-key="' + esc(f.id) + '" data-dd-cat="functions" data-dd-grp="' + esc(f.memberOf || '(free functions)') + '">';
                html += '<input type="checkbox"' + checked + ' />';
                html += '<span class="sm-dd-item-name" title="' + esc(f.id) + '">' + esc(shortName) + '</span>';
                html += '<span class="sm-dd-item-meta">' + meta + '</span>';
                html += '</div>';
            });
            html += '</div>';
        });
        return html;
    }

    function ddRenderTypesTab() {
        var types = ddGetAllTypes();
        var matched = types.filter(function (t) { return t.source === 'matched'; });
        var related = types.filter(function (t) { return t.source === 'related'; });

        var html = '<div style="margin-bottom:8px; font-size:11.5px; color:var(--text-muted);">Select types to include full definitions (members, methods, inheritance).</div>';

        if (matched.length) {
            html += '<div class="sm-dd-group">';
            html += '<div class="sm-dd-group-header"><span><i class="fa-solid fa-shapes" style="margin-right:4px; color:#8b5cf6;"></i> Matched Types (' + matched.length + ')</span><span class="sm-dd-select-all" data-dd-selectall-group="types" data-dd-grp="matched">Select all</span></div>';
            matched.forEach(function (t) {
                var checked = ddSelections.types[t.name] ? ' checked' : '';
                var sel = ddSelections.types[t.name] ? ' selected' : '';
                var badge = t.kind === 'struct' ? 'S' : 'C';
                html += '<div class="sm-dd-item' + sel + '" data-dd-key="' + esc(t.name) + '" data-dd-cat="types" data-dd-grp="matched">';
                html += '<input type="checkbox"' + checked + ' />';
                html += '<span class="sm-kind-badge sm-kind-type">' + badge + '</span>';
                html += '<span class="sm-dd-item-name">' + esc(t.name) + '</span>';
                html += '<span class="sm-dd-item-meta">' + t.methods + ' methods, ' + t.members + ' members</span>';
                html += '</div>';
            });
            html += '</div>';
        }

        if (related.length) {
            html += '<div class="sm-dd-group">';
            html += '<div class="sm-dd-group-header"><span><i class="fa-solid fa-link" style="margin-right:4px; color:#8b5cf6; opacity:0.5;"></i> Related Types (' + related.length + ')</span><span class="sm-dd-select-all" data-dd-selectall-group="types" data-dd-grp="related">Select all</span></div>';
            related.forEach(function (t) {
                var checked = ddSelections.types[t.name] ? ' checked' : '';
                var sel = ddSelections.types[t.name] ? ' selected' : '';
                html += '<div class="sm-dd-item' + sel + '" data-dd-key="' + esc(t.name) + '" data-dd-cat="types" data-dd-grp="related">';
                html += '<input type="checkbox"' + checked + ' />';
                html += '<span class="sm-kind-badge sm-kind-type">C</span>';
                html += '<span class="sm-dd-item-name">' + esc(t.name) + '</span>';
                html += '<span class="sm-dd-item-meta">used by ' + (t.usedBy || 0) + ' matched</span>';
                html += '</div>';
            });
            html += '</div>';
        }
        return html;
    }

    function ddRenderEnumsTab() {
        var enums = topicReport ? (topicReport.enums || []) : [];
        var html = '<div style="margin-bottom:8px; font-size:11.5px; color:var(--text-muted);">Select enums to include all values in the export.</div>';
        html += '<div class="sm-dd-group">';
        html += '<div class="sm-dd-group-header"><span><i class="fa-solid fa-list-ol" style="margin-right:4px; color:#f59e0b;"></i> Enums (' + enums.length + ')</span><span class="sm-dd-select-all" data-dd-selectall="enums">Select all</span></div>';
        enums.forEach(function (e) {
            var checked = ddSelections.enums[e.name] ? ' checked' : '';
            var sel = ddSelections.enums[e.name] ? ' selected' : '';
            html += '<div class="sm-dd-item' + sel + '" data-dd-key="' + esc(e.name) + '" data-dd-cat="enums">';
            html += '<input type="checkbox"' + checked + ' />';
            html += '<span class="sm-kind-badge sm-kind-enum">E</span>';
            html += '<span class="sm-dd-item-name">' + esc(e.name) + '</span>';
            html += '<span class="sm-dd-item-meta">' + e.valueCount + ' values</span>';
            html += '</div>';
            if (e.sampleValues && e.sampleValues.length) {
                html += '<div style="padding: 1px 10px 4px 52px; font-size: 10px; color: var(--text-muted); font-family: Consolas, monospace;">' + e.sampleValues.slice(0, 5).map(function (v) { return esc(v); }).join(', ') + (e.valueCount > 5 ? ', ...' : '') + '</div>';
            }
        });
        html += '</div>';
        return html;
    }

    // ── Selection handling ──

    $(document).on('click', '.sm-dd-item', function (e) {
        if ($(e.target).is('input[type="checkbox"]')) return; // handled by change
        var cb = $(this).find('input[type="checkbox"]');
        cb.prop('checked', !cb.prop('checked')).trigger('change');
    });

    $(document).on('change', '.sm-dd-item input[type="checkbox"]', function () {
        var item = $(this).closest('.sm-dd-item');
        var key = item.data('dd-key');
        var cat = item.data('dd-cat');
        if (this.checked) {
            ddSelections[cat][key] = true;
            item.addClass('selected');
        } else {
            delete ddSelections[cat][key];
            item.removeClass('selected');
        }
        ddUpdateSelectionInfo();
    });

    // Select all (whole category)
    $(document).on('click', '[data-dd-selectall]', function (e) {
        e.stopPropagation();
        var cat = $(this).data('dd-selectall');
        var items = $('#smDdBody .sm-dd-item[data-dd-cat="' + cat + '"]');
        var allChecked = items.find('input:checked').length === items.length;
        items.each(function () {
            var key = $(this).data('dd-key');
            var cb = $(this).find('input[type="checkbox"]');
            if (allChecked) {
                cb.prop('checked', false);
                delete ddSelections[cat][key];
                $(this).removeClass('selected');
            } else {
                cb.prop('checked', true);
                ddSelections[cat][key] = true;
                $(this).addClass('selected');
            }
        });
        $(this).text(allChecked ? 'Select all' : 'Deselect all');
        ddUpdateSelectionInfo();
    });

    // Select all (within group)
    $(document).on('click', '[data-dd-selectall-group]', function (e) {
        e.stopPropagation();
        var cat = $(this).data('dd-selectall-group');
        var grp = $(this).data('dd-grp');
        var items = $('#smDdBody .sm-dd-item[data-dd-cat="' + cat + '"][data-dd-grp="' + grp + '"]');
        var allChecked = items.find('input:checked').length === items.length;
        items.each(function () {
            var key = $(this).data('dd-key');
            var cb = $(this).find('input[type="checkbox"]');
            if (allChecked) {
                cb.prop('checked', false);
                delete ddSelections[cat][key];
                $(this).removeClass('selected');
            } else {
                cb.prop('checked', true);
                ddSelections[cat][key] = true;
                $(this).addClass('selected');
            }
        });
        $(this).text(allChecked ? 'Select all' : 'Deselect all');
        ddUpdateSelectionInfo();
    });

    function ddGetTotalSelected() {
        var total = 0;
        for (var cat in ddSelections) total += Object.keys(ddSelections[cat]).length;
        return total;
    }

    function ddUpdateSelectionInfo() {
        var fc = Object.keys(ddSelections.files).length;
        var fnc = Object.keys(ddSelections.functions).length;
        var tc = Object.keys(ddSelections.types).length;
        var ec = Object.keys(ddSelections.enums).length;
        var total = fc + fnc + tc + ec;
        var parts = [];
        if (fc) parts.push('<strong>' + fc + '</strong> file' + (fc > 1 ? 's' : ''));
        if (fnc) parts.push('<strong>' + fnc + '</strong> function' + (fnc > 1 ? 's' : ''));
        if (tc) parts.push('<strong>' + tc + '</strong> type' + (tc > 1 ? 's' : ''));
        if (ec) parts.push('<strong>' + ec + '</strong> enum' + (ec > 1 ? 's' : ''));
        if (total === 0) {
            $('#smDdSelInfo').html('Select items to include in your context package');
        } else {
            $('#smDdSelInfo').html(parts.join(' · ') + ' selected');
        }
        $('#smDdGenerate').prop('disabled', total === 0);
    }

    // ── Generate package ──

    $(document).on('click', '#smDdGenerate', function () {
        var total = ddGetTotalSelected();
        if (total === 0) return;

        var request = {
            files: Object.keys(ddSelections.files),
            functions: Object.keys(ddSelections.functions),
            types: Object.keys(ddSelections.types),
            enums: Object.keys(ddSelections.enums)
        };

        // Show generating state
        $('#smDdBody').html('<div class="sm-dd-generating"><i class="fa-solid fa-spinner fa-spin"></i><p>Gathering context...</p></div>');
        $('#smDdGenerate').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Generating...');

        $.ajax({
            url: '/SourceMap/ResearchBundle',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(request),
            success: function (data) {
                ddBundleResult = data;
                var formatted = ddFormatBundle(data);
                $('#smDdBody').html('<div class="sm-dd-result-preview">' + esc(formatted) + '</div>');
                $('#smDdGenerate').html('<i class="fa-solid fa-bolt"></i> Regenerate').prop('disabled', false);
                $('#smDdCopy, #smDdDownload').show();

                // Stats
                var lines = formatted.split('\n').length;
                var tokens = Math.round(formatted.length / 4);
                $('#smDdSelInfo').html('<strong>' + lines.toLocaleString() + '</strong> lines · ~<strong>' + tokens.toLocaleString() + '</strong> tokens');
            },
            error: function () {
                $('#smDdBody').html('<div style="padding: 20px; color: var(--status-error);">Failed to generate bundle. Check that the index is loaded.</div>');
                $('#smDdGenerate').html('<i class="fa-solid fa-bolt"></i> Generate Package').prop('disabled', false);
            }
        });
    });

    function ddFormatBundle(data) {
        var lines = [];
        var query = topicReport ? topicReport.query : 'unknown';
        lines.push('=== RESEARCH CONTEXT: "' + query + '" ===');
        var counts = [];
        if (data.files && data.files.length) counts.push(data.files.length + ' file' + (data.files.length > 1 ? 's' : ''));
        if (data.functions && data.functions.length) counts.push(data.functions.length + ' function' + (data.functions.length > 1 ? 's' : ''));
        if (data.types && data.types.length) counts.push(data.types.length + ' type' + (data.types.length > 1 ? 's' : ''));
        if (data.enums && data.enums.length) counts.push(data.enums.length + ' enum' + (data.enums.length > 1 ? 's' : ''));
        lines.push('=== ' + counts.join(', ') + ' ===');
        lines.push('');

        // Files
        if (data.files && data.files.length) {
            data.files.forEach(function (f) {
                lines.push('--- FILE: ' + f.path + ' (' + f.lineCount + ' lines) ---');
                lines.push(f.content);
                lines.push('');
            });
        }

        // Functions
        if (data.functions && data.functions.length) {
            data.functions.forEach(function (f) {
                var loc = f.file ? f.file + ':' + f.lineStart + '-' + f.lineEnd : '';
                lines.push('--- FUNCTION: ' + f.id + ' ---');
                if (f.signature) lines.push('// ' + f.signature);
                if (loc) lines.push('// ' + loc);
                if (f.note) {
                    lines.push('// ' + f.note);
                } else {
                    lines.push(f.body);
                }
                lines.push('');
            });
        }

        // Types
        if (data.types && data.types.length) {
            data.types.forEach(function (t) {
                var inh = t.inherits && t.inherits.length ? ' : ' + t.inherits.join(', ') : '';
                lines.push('--- TYPE: ' + t.name + inh + ' ---');
                lines.push('[' + t.kind + '] ' + t.name + ' (' + (t.qualifiedMethods || t.methods || []).length + ' methods, ' + (t.members || []).length + ' members) [' + t.declaredIn + ']');
                if (t.members && t.members.length) lines.push('Members: ' + t.members.join(', '));
                if ((t.qualifiedMethods || t.methods || []).length) lines.push('Methods: ' + (t.qualifiedMethods || t.methods).join(', '));
                if (t.inheritedBy && t.inheritedBy.length) lines.push('Inherited by: ' + t.inheritedBy.join(', '));
                lines.push('');
            });
        }

        // Enums
        if (data.enums && data.enums.length) {
            data.enums.forEach(function (e) {
                lines.push('--- ENUM: ' + e.name + ' (' + (e.values || []).length + ' values) ---');
                lines.push('// ' + e.declaredIn + ':' + e.lineStart + '-' + e.lineEnd);
                (e.values || []).forEach(function (v) {
                    lines.push('  ' + v.name + (v.value ? ' = ' + v.value : ''));
                });
                lines.push('');
            });
        }

        return lines.join('\n');
    }

    // Copy & Download
    $(document).on('click', '#smDdCopy', function () {
        if (!ddBundleResult) return;
        var text = ddFormatBundle(ddBundleResult);
        copyToClipboard(text).then(function () {
            var btn = $('#smDdCopy');
            btn.html('<i class="fa-solid fa-check"></i> Copied');
            setTimeout(function () { btn.html('<i class="fa-solid fa-copy"></i> Copy'); }, 1500);
        });
    });

    $(document).on('click', '#smDdDownload', function () {
        if (!ddBundleResult) return;
        var text = ddFormatBundle(ddBundleResult);
        var query = topicReport ? topicReport.query.replace(/\s+/g, '_') : 'research';
        var blob = new Blob([text], { type: 'text/plain' });
        var a = document.createElement('a');
        a.href = URL.createObjectURL(blob);
        a.download = 'deepdive_' + query + '.txt';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);
        URL.revokeObjectURL(a.href);
    });

    // ===================== REINDEX =====================

    var reindexPollTimer = null;

    $(document).on('click', '#smBtnReindex', function () {
        if (reindexPollTimer) return; // already running

        $('#smReindexBanner').show();
        $('#smReindexPhase').text('Starting...');
        $('#smReindexBar').css('width', '0%');
        $('#smReindexPct').text('0%');
        $('#smBtnReindex').prop('disabled', true);

        $.post('/SourceMap/Reindex', function (result) {
            stopReindexPoll();
            $('#smReindexBanner').hide();
            $('#smBtnReindex').prop('disabled', false);

            if (result.success) {
                // Refresh stats
                $.getJSON('/SourceMap/Stats', function (data) {
                    statsData = data;
                    var m = data.meta || {};
                    $('#smTotalSymbols').text((m.total_functions || 0).toLocaleString());
                    $('#smTotalTypes').text((m.total_types || 0).toLocaleString());
                    $('#smTotalFiles').text((m.total_files || 0).toLocaleString());
                    $('#smIndexStatus').text('Indexed: ' + new Date(m.indexed_at).toLocaleString() + ' (' + result.elapsedMs + 'ms)');
                    renderSidebar();
                });
            } else {
                alert('Reindex failed: ' + (result.error || 'Unknown error'));
            }
        }).fail(function () {
            stopReindexPoll();
            $('#smReindexBanner').hide();
            $('#smBtnReindex').prop('disabled', false);
            alert('Reindex request failed');
        });

        // Start polling progress
        reindexPollTimer = setInterval(function () {
            $.getJSON('/SourceMap/ReindexProgress', function (prog) {
                $('#smReindexPhase').text(prog.phase || '...');
                var pct = prog.percentComplete || 0;
                $('#smReindexBar').css('width', pct + '%');
                $('#smReindexPct').text(Math.round(pct) + '%');
                if (prog.phase === 'complete' || prog.phase === 'error') stopReindexPoll();
            });
        }, 500);
    });

    function stopReindexPoll() {
        if (reindexPollTimer) { clearInterval(reindexPollTimer); reindexPollTimer = null; }
    }

    // ===================== HELPERS =====================

    function updateSidebarActive() {
        $('.sm-item').removeClass('active');
        if (currentCenter) $('.sm-sym-item[data-fid="' + currentCenter + '"]').addClass('active');
    }

    function esc(str) {
        if (str === null || str === undefined) return '';
        var div = document.createElement('div');
        div.textContent = String(str);
        return div.innerHTML;
    }

    function escSvg(str) {
        if (str === null || str === undefined) return '';
        return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }
});
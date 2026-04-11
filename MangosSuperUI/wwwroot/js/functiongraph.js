/* ============================================================
   functiongraph.js — VMaNGOS Engine Function Graph Explorer
   Vertical layout: Called By (top) → Center → Calls (bottom)
   ============================================================ */

$(function () {
    'use strict';

    // ===================== STATE =====================

    var graphMeta = null;
    var graphClasses = null;
    var graphFiles = null;
    var graphStats = null;

    var currentCenter = null;
    var currentData = null;
    var breadcrumbs = [];

    var activeTab = 'classes';
    var sidebarSearch = '';

    var fgZoom = 1;
    var fgPanX = 0, fgPanY = 0;
    var fgIsPanning = false;
    var fgDragged = false;
    var fgPanStartX = 0, fgPanStartY = 0;
    var fgPanStartPanX = 0, fgPanStartPanY = 0;
    var fgSvgW = 0, fgSvgH = 0;

    // ===================== INIT =====================

    $.getJSON('/FunctionGraph/Stats', function (data) {
        graphMeta = data.meta;
        graphClasses = data.classes;
        graphFiles = data.files;
        graphStats = data.stats;
        $('#fgTotalFuncs').text((graphMeta.total_functions || 0).toLocaleString());
        $('#fgTotalEdges').text((graphMeta.total_edges || 0).toLocaleString());
        renderSidebar();
    }).fail(function () {
        $('#fgSidebarList').html('<div style="padding: 16px; color: var(--status-error);"><i class="fa-solid fa-exclamation-triangle"></i> Failed to load. Ensure function-graph.json is in wwwroot/data/</div>');
    });

    // ===================== SIDEBAR =====================

    function renderSidebar() {
        if (activeTab === 'classes') renderClassesList();
        else if (activeTab === 'files') renderFilesList();
        else if (activeTab === 'stats') renderStatsList();
    }

    function renderClassesList() {
        if (!graphClasses) return;
        var html = '', filter = sidebarSearch.toLowerCase();
        var sorted = Object.values(graphClasses).sort(function (a, b) { return b.function_count - a.function_count; });
        var shown = 0;
        sorted.forEach(function (cls) {
            var classMatches = !filter || cls.name.toLowerCase().indexOf(filter) !== -1;
            var matchingFuncs = [];
            cls.functions.forEach(function (fid) {
                if (!filter || fid.toLowerCase().indexOf(filter) !== -1 || classMatches) matchingFuncs.push(fid);
            });
            if (matchingFuncs.length === 0) return;
            shown++;
            var collapsed = !filter && shown > 10;
            html += '<div class="fg-group-header" data-grp="cls-' + shown + '"><span>' + esc(cls.name) + '</span><span class="count">' + cls.function_count + '</span></div>';
            html += '<div class="fg-group-body" data-grp="cls-' + shown + '"' + (collapsed ? ' style="display:none;"' : '') + '>';
            matchingFuncs.forEach(function (fid) {
                var shortName = fid.indexOf('::') !== -1 ? fid.split('::').pop() : fid;
                html += '<div class="fg-func-item' + (fid === currentCenter ? ' active' : '') + '" data-fid="' + esc(fid) + '"><span style="overflow:hidden;text-overflow:ellipsis;" title="' + esc(fid) + '">' + esc(shortName) + '</span></div>';
            });
            html += '</div>';
        });
        if (!shown) html = '<div style="padding: 16px; color: var(--text-muted); font-size: 12.5px;">No matches</div>';
        $('#fgSidebarList').html(html);
    }

    function renderFilesList() {
        if (!graphFiles) return;
        var html = '', filter = sidebarSearch.toLowerCase();
        var sorted = Object.values(graphFiles).sort(function (a, b) { return b.function_count - a.function_count; });
        var shown = 0;
        sorted.forEach(function (file) {
            var shortPath = file.path.replace(/^src\//, '');
            if (filter && shortPath.toLowerCase().indexOf(filter) === -1) return;
            shown++;
            var collapsed = !filter && shown > 10;
            html += '<div class="fg-group-header" data-grp="file-' + shown + '"><span title="' + esc(file.path) + '">' + esc(shortPath) + '</span><span class="count">' + file.function_count + '</span></div>';
            html += '<div class="fg-group-body" data-grp="file-' + shown + '"' + (collapsed ? ' style="display:none;"' : '') + '>';
            file.functions.forEach(function (fid) {
                var shortName = fid.indexOf('::') !== -1 ? fid.split('::').pop() : fid;
                html += '<div class="fg-func-item' + (fid === currentCenter ? ' active' : '') + '" data-fid="' + esc(fid) + '"><span style="overflow:hidden;text-overflow:ellipsis;" title="' + esc(fid) + '">' + esc(shortName) + '</span></div>';
            });
            html += '</div>';
        });
        if (!shown) html = '<div style="padding: 16px; color: var(--text-muted); font-size: 12.5px;">No matches</div>';
        $('#fgSidebarList').html(html);
    }

    function renderStatsList() {
        if (!graphStats || !graphMeta) return;
        var html = '<div style="padding: 14px;"><div style="font-size: 11px; font-weight: 700; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 8px;">Overview</div>'
            + statRow('Functions', graphMeta.total_functions) + statRow('Edges', graphMeta.total_edges) + statRow('Classes', graphMeta.total_classes) + statRow('Files', graphMeta.total_files) + '</div>';
        html += renderStatsGroup('Hub Functions', graphStats.hub_functions, function (n) { return n.total_connections + ' conn'; });
        html += renderStatsGroup('Most Complex', graphStats.top_complex, function (n) { return n.call_count + ' calls'; });
        html += renderStatsGroup('Most Used', graphStats.top_callers, function (n) { return n.caller_count + ' callers'; });
        html += renderStatsGroup('Deepest Chains', graphStats.top_deep, function (n) { return 'depth ' + n.max_depth; });
        $('#fgSidebarList').html(html);
    }

    function statRow(label, val) {
        return '<div style="display:flex;justify-content:space-between;padding:3px 0;font-size:12.5px;"><span style="color:var(--text-secondary);">' + label + '</span><span style="font-weight:600;color:var(--text-primary);">' + (val || 0).toLocaleString() + '</span></div>';
    }

    function renderStatsGroup(title, items, formatFn) {
        var html = '<div style="padding: 0 14px 14px;"><div style="font-size: 11px; font-weight: 700; color: var(--text-muted); text-transform: uppercase; letter-spacing: 0.05em; margin-bottom: 6px;">' + title + '</div>';
        (items || []).slice(0, 15).forEach(function (n) {
            html += '<div class="fg-func-item" data-fid="' + esc(n.id) + '" style="padding: 4px 0;"><span style="overflow:hidden;text-overflow:ellipsis;" title="' + esc(n.qualified) + '">' + esc(n.qualified) + '</span><span class="fg-conn-count">' + formatFn(n) + '</span></div>';
        });
        return html + '</div>';
    }

    // --- Sidebar events ---
    $(document).on('click', '.fg-sidebar-tabs button', function () {
        activeTab = $(this).data('tab');
        $('.fg-sidebar-tabs button').removeClass('active');
        $(this).addClass('active');
        renderSidebar();
    });

    $(document).on('click', '.fg-group-header', function () {
        var grp = $(this).data('grp');
        $('.fg-group-body[data-grp="' + grp + '"]').toggle();
    });

    $(document).on('click', '.fg-func-item', function (e) {
        e.stopPropagation();
        var fid = $(this).attr('data-fid');
        if (fid) { breadcrumbs = []; loadNode(fid); }
    });

    var searchTimer = null;
    $('#fgSearchInput').on('input', function () {
        var val = $(this).val();
        clearTimeout(searchTimer);
        searchTimer = setTimeout(function () { sidebarSearch = val; renderSidebar(); }, 200);
    });

    // ===================== LOAD NODE =====================

    function loadNode(nodeId) {
        $('#fgWelcome').hide();
        $('#fgToolbar').show();
        $('#fgGraphView').show();
        $('#fgStatsBar').html('<span style="color: var(--text-muted);"><i class="fa-solid fa-spinner fa-spin"></i> Loading...</span>').show();
        fgDetachWheel();
        $('#fgGraphSvg').remove();

        $.getJSON('/FunctionGraph/Node', { id: nodeId }, function (data) {
            currentCenter = nodeId;
            currentData = data;

            // Cycle detection in breadcrumbs
            var cycleIdx = -1;
            for (var i = 0; i < breadcrumbs.length; i++) {
                if (breadcrumbs[i].id === nodeId) { cycleIdx = i; break; }
            }
            if (cycleIdx >= 0) {
                breadcrumbs = breadcrumbs.slice(0, cycleIdx + 1);
            } else {
                breadcrumbs.push({ id: nodeId, label: data.center.qualified });
            }

            renderToolbar();
            renderStatsBar(data);
            buildGraph(data);
            renderDetailPanel(data);
            updateSidebarActive();
        }).fail(function () {
            $('#fgStatsBar').html('<span style="color: var(--status-error);"><i class="fa-solid fa-exclamation-triangle"></i> Failed to load: ' + esc(nodeId) + '</span>');
        });
    }

    // ===================== TOOLBAR =====================

    function renderToolbar() {
        var html = '';
        if (breadcrumbs.length > 1) html += '<button id="fgBtnBack" title="Go back"><i class="fa-solid fa-arrow-left"></i></button>';

        html += '<div class="fg-breadcrumbs">';
        breadcrumbs.forEach(function (crumb, i) {
            if (i > 0) html += '<span class="fg-crumb-sep"><i class="fa-solid fa-chevron-right"></i></span>';
            var isCurrent = i === breadcrumbs.length - 1;
            html += '<span class="fg-crumb' + (isCurrent ? ' current' : '') + '" data-idx="' + i + '">' + esc(crumb.label) + '</span>';
        });
        html += '</div>';

        html += '<button id="fgBtnExport" title="Export call tree as JSON"><i class="fa-solid fa-download"></i> Export Tree</button>';
        html += '<button id="fgBtnDetail" title="Toggle detail panel"><i class="fa-solid fa-info-circle"></i> Detail</button>';
        $('#fgToolbar').addClass('fg-toolbar').html(html);
    }

    $(document).on('click', '#fgBtnBack', function () {
        if (breadcrumbs.length <= 1) return;
        breadcrumbs.pop();
        var prev = breadcrumbs.pop();
        loadNode(prev.id);
    });

    $(document).on('click', '.fg-crumb:not(.current)', function () {
        var idx = parseInt($(this).data('idx'));
        var crumb = breadcrumbs[idx];
        breadcrumbs = breadcrumbs.slice(0, idx);
        loadNode(crumb.id);
    });

    $(document).on('click', '#fgBtnDetail', function () {
        $('#fgDetailPanel').toggleClass('visible').toggle();
    });

    $(document).on('click', '#fgBtnExport', function () {
        if (!currentCenter) return;
        var safeName = currentCenter.replace(/::/g, '_').replace(/\//g, '_').replace(/ /g, '_');
        fetch('/FunctionGraph/ExportTree?id=' + encodeURIComponent(currentCenter))
            .then(function (r) { return r.blob(); })
            .then(function (blob) {
                var url = URL.createObjectURL(blob);
                var a = document.createElement('a');
                a.href = url;
                a.download = safeName + '_calltree.json';
                document.body.appendChild(a);
                a.click();
                document.body.removeChild(a);
                setTimeout(function () { URL.revokeObjectURL(url); }, 1000);
            })
            .catch(function (e) { alert('Export failed: ' + e); });
    });

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
        $('#fgStatsBar').html(
            '<strong>' + esc(c.qualified) + '</strong>'
            + ' <span>·</span> <span>' + c.line_count + ' lines</span>'
            + ' <span>·</span> <span style="color: #3b82f6;">▸ ' + outCount + ' calls</span>'
            + ' <span>·</span> <span style="color: #22c55e;">◂ ' + inCount + ' callers</span>'
            + ' <span>·</span> <span>depth ' + c.max_depth + '</span>'
        ).show();
    }

    // ===================== GRAPH BUILDER — VERTICAL LAYOUT =====================
    //
    //   [ Caller A ] [ Caller B ] [ Caller C ]     ← top row (Called By)
    //         \           |           /
    //          ↓          ↓          ↓
    //       [======= CENTER NODE =======]          ← middle
    //          ↓          ↓          ↓
    //         /           |           \
    //   [ Call X ]  [ Call Y ]  [ Call Z ]         ← bottom row (Calls)
    //

    var FG_NODE_W = 200;
    var FG_NODE_H = 68;
    var FG_CENTER_W = 260;
    var FG_CENTER_H = 80;
    var FG_BADGE_R = 14;
    var FG_H_GAP = 24;
    var FG_V_GAP = 60;

    function buildGraph(data) {
        var center = data.center;
        var neighbors = data.neighbors || [];

        var outgoing = [], incoming = [], both = [];
        neighbors.forEach(function (n) {
            if (n.direction === 'outgoing') outgoing.push(n);
            else if (n.direction === 'incoming') incoming.push(n);
            else both.push(n);
        });

        outgoing.sort(function (a, b) { return b.max_depth - a.max_depth; });
        incoming.sort(function (a, b) { return b.max_depth - a.max_depth; });
        both.sort(function (a, b) { return b.max_depth - a.max_depth; });

        // Top row = callers, Bottom row = calls + bidirectional
        var topRow = incoming;
        var bottomRow = [].concat(outgoing, both);

        if (topRow.length === 0 && bottomRow.length === 0) {
            var svg = '<svg id="fgGraphSvg" width="300" height="120" xmlns="http://www.w3.org/2000/svg">';
            svg += renderCenterNode(center, 30, 20);
            svg += '</svg>';
            $('#fgGraphSvg').remove();
            $('#fgGraphView').prepend(svg);
            fgSvgW = 300; fgSvgH = 120;
            fgZoom = 1; fgPanX = 0; fgPanY = 0;
            fgAttachWheel();
            setTimeout(function () { fgFitToView(); }, 100);
            return;
        }

        // Row widths
        var topRowW = topRow.length > 0 ? topRow.length * FG_NODE_W + (topRow.length - 1) * FG_H_GAP : 0;
        var bottomRowW = bottomRow.length > 0 ? bottomRow.length * FG_NODE_W + (bottomRow.length - 1) * FG_H_GAP : 0;
        var totalW = Math.max(topRowW, FG_CENTER_W, bottomRowW);
        var pad = 40;

        // Y positions
        var topY = pad;
        var centerY = topRow.length > 0 ? topY + FG_NODE_H + FG_V_GAP : pad;
        var bottomY = centerY + FG_CENTER_H + FG_V_GAP;

        var centerX = pad + (totalW - FG_CENTER_W) / 2;

        var nodePositions = {};
        nodePositions['__center__'] = { x: centerX, y: centerY, w: FG_CENTER_W, h: FG_CENTER_H };

        // Place top row centered
        var topStartX = pad + (totalW - topRowW) / 2;
        topRow.forEach(function (n, i) {
            nodePositions[n.id] = { x: topStartX + i * (FG_NODE_W + FG_H_GAP), y: topY, w: FG_NODE_W, h: FG_NODE_H };
        });

        // Place bottom row centered
        var bottomStartX = pad + (totalW - bottomRowW) / 2;
        bottomRow.forEach(function (n, i) {
            nodePositions[n.id] = { x: bottomStartX + i * (FG_NODE_W + FG_H_GAP), y: bottomY, w: FG_NODE_W, h: FG_NODE_H };
        });

        // SVG size
        var lastBottom = bottomRow.length > 0 ? bottomY + FG_NODE_H : centerY + FG_CENTER_H;
        fgSvgW = totalW + pad * 2;
        fgSvgH = lastBottom + pad;

        // Build SVG
        var svg = '<svg id="fgGraphSvg" width="' + fgSvgW + '" height="' + fgSvgH + '" xmlns="http://www.w3.org/2000/svg">';

        // Arrow markers
        svg += '<defs>';
        ['outgoing', 'incoming', 'both'].forEach(function (dir) {
            svg += '<marker id="fg-arrow-' + dir + '" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto"><polygon points="0 0, 8 3, 0 6" class="fg-edge-arrow ' + dir + '" /></marker>';
        });
        svg += '</defs>';

        var cp = nodePositions['__center__'];

        // --- Edges: callers (top) → center ---
        topRow.forEach(function (n) {
            var pos = nodePositions[n.id];
            if (!pos) return;
            var x1 = pos.x + pos.w / 2, y1 = pos.y + pos.h;
            var x2 = cp.x + cp.w / 2, y2 = cp.y;
            var cpy = y1 + (y2 - y1) * 0.5;
            svg += '<path class="fg-edge incoming" d="M' + x1.toFixed(1) + ',' + y1.toFixed(1) + ' C' + x1.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + y2.toFixed(1) + '" marker-end="url(#fg-arrow-incoming)" />';
        });

        // --- Edges: center → calls (bottom) ---
        bottomRow.forEach(function (n) {
            var pos = nodePositions[n.id];
            if (!pos) return;
            var dir = n.direction === 'both' ? 'both' : 'outgoing';
            var x1 = cp.x + cp.w / 2, y1 = cp.y + cp.h;
            var x2 = pos.x + pos.w / 2, y2 = pos.y;
            var cpy = y1 + (y2 - y1) * 0.5;
            svg += '<path class="fg-edge ' + dir + '" d="M' + x1.toFixed(1) + ',' + y1.toFixed(1) + ' C' + x1.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + y2.toFixed(1) + '" marker-end="url(#fg-arrow-' + dir + ')" />';
        });

        // --- "Both" nodes: also draw a dashed return edge back up ---
        both.forEach(function (n) {
            var pos = nodePositions[n.id];
            if (!pos) return;
            var x1 = pos.x + pos.w / 2 + 12, y1 = pos.y;
            var x2 = cp.x + cp.w / 2 + 12, y2 = cp.y + cp.h;
            var cpy = y2 + (y1 - y2) * 0.5;
            svg += '<path class="fg-edge both" style="stroke-dasharray:6 3;" d="M' + x1.toFixed(1) + ',' + y1.toFixed(1) + ' C' + x1.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + cpy.toFixed(1) + ' ' + x2.toFixed(1) + ',' + y2.toFixed(1) + '" marker-end="url(#fg-arrow-both)" />';
        });

        // --- Nodes ---
        svg += renderCenterNode(center, cp.x, cp.y);
        topRow.forEach(function (n) { var p = nodePositions[n.id]; if (p) svg += renderConnectedNode(n, p.x, p.y); });
        bottomRow.forEach(function (n) { var p = nodePositions[n.id]; if (p) svg += renderConnectedNode(n, p.x, p.y); });

        svg += '</svg>';

        $('#fgGraphSvg').remove();
        $('#fgGraphView').prepend(svg);
        fgZoom = 1; fgPanX = 0; fgPanY = 0;
        fgAttachWheel();
        setTimeout(function () { fgFitToView(); }, 150);
    }

    // ===================== NODE RENDERERS =====================

    function renderCenterNode(c, x, y) {
        var w = FG_CENTER_W, h = FG_CENTER_H;
        var safeId = 'fg-c-' + Math.random().toString(36).substr(2, 6);
        var g = '<g class="fg-node center" data-nid="' + escSvg(c.id) + '">';
        g += '<rect class="fg-node-bg" x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" />';
        g += '<clipPath id="' + safeId + '"><rect x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" rx="8" ry="8" /></clipPath>';
        g += '<rect class="fg-node-header" x="' + x + '" y="' + y + '" width="' + w + '" height="34" fill="var(--accent)" clip-path="url(#' + safeId + ')" />';
        var displayName = c.qualified.length > 30 ? c.name : c.qualified;
        g += '<text class="fg-node-title" x="' + (x + 10) + '" y="' + (y + 15) + '">' + escSvg(displayName) + '</text>';
        g += '<text class="fg-node-meta" x="' + (x + 10) + '" y="' + (y + 28) + '">' + c.line_count + ' lines · depth ' + c.max_depth + '</text>';
        var shortFile = (c.file || '').replace(/^src\//, '');
        g += '<text class="fg-node-file" x="' + (x + 10) + '" y="' + (y + 48) + '">' + escSvg(shortFile) + ':' + c.line_start + '</text>';
        var shortSig = (c.signature || '').substring(0, 42);
        if ((c.signature || '').length > 42) shortSig += '...';
        g += '<text class="fg-node-class" x="' + (x + 10) + '" y="' + (y + 62) + '">' + escSvg(shortSig) + '</text>';
        g += '</g>';
        return g;
    }

    function renderConnectedNode(n, x, y) {
        var w = FG_NODE_W, h = FG_NODE_H;
        var safeId = 'fg-n-' + Math.random().toString(36).substr(2, 6);
        var g = '<g class="fg-node" data-nid="' + escSvg(n.id) + '">';
        g += '<rect class="fg-node-bg" x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" />';
        var headerColor = n.direction === 'outgoing' ? '#3b82f6' : n.direction === 'incoming' ? '#22c55e' : '#f59e0b';
        g += '<clipPath id="' + safeId + '"><rect x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" rx="8" ry="8" /></clipPath>';
        g += '<rect class="fg-node-header" x="' + x + '" y="' + y + '" width="' + w + '" height="30" fill="' + headerColor + '" clip-path="url(#' + safeId + ')" />';
        var displayName = n.qualified ? (n.qualified.length > 26 ? n.name : n.qualified) : n.id;
        g += '<text class="fg-node-title" x="' + (x + 10) + '" y="' + (y + 14) + '" font-size="11">' + escSvg(displayName) + '</text>';
        g += '<text class="fg-node-meta" x="' + (x + 10) + '" y="' + (y + 25) + '" font-size="9">' + n.call_count + ' calls · ' + n.caller_count + ' callers</text>';
        var shortFile = (n.file || '').replace(/^src\//, '');
        if (shortFile.length > 28) shortFile = '...' + shortFile.substring(shortFile.length - 25);
        g += '<text class="fg-node-file" x="' + (x + 10) + '" y="' + (y + 44) + '">' + escSvg(shortFile) + '</text>';
        if (n.className) g += '<text class="fg-node-class" x="' + (x + 10) + '" y="' + (y + 58) + '">' + escSvg(n.className) + '</text>';
        // Depth badge top-right
        var bx = x + w - 2, by = y - 2;
        g += '<circle class="fg-badge-circle" cx="' + bx + '" cy="' + by + '" r="' + FG_BADGE_R + '" fill="' + getDepthColor(n.max_depth) + '" />';
        g += '<text class="fg-badge-text" x="' + bx + '" y="' + by + '">' + n.max_depth + '</text>';
        g += '</g>';
        return g;
    }

    function getDepthColor(d) {
        if (d === 0) return '#64748b';
        if (d <= 5) return '#22c55e';
        if (d <= 15) return '#06b6d4';
        if (d <= 30) return '#3b82f6';
        if (d <= 60) return '#8b5cf6';
        if (d <= 100) return '#ec4899';
        if (d <= 200) return '#f59e0b';
        return '#ef4444';
    }

    // ===================== DETAIL PANEL =====================

    function renderDetailPanel(data) {
        var c = data.center;
        var neighbors = data.neighbors || [];
        var html = '<div class="fg-detail-header"><h3>' + esc(c.qualified) + '</h3>'
            + '<div class="fg-detail-file">' + esc(c.file) + ':' + c.line_start + '–' + c.line_end + '</div>'
            + '<div class="fg-detail-sig">' + esc(c.signature) + '</div></div>';
        html += '<div class="fg-detail-stats">' + statCell(c.call_count, 'Calls') + statCell(c.caller_count, 'Callers') + statCell(c.max_depth, 'Depth') + statCell(c.line_count, 'Lines') + '</div>';

        var outgoing = neighbors.filter(function (n) { return n.direction === 'outgoing' || n.direction === 'both'; });
        if (outgoing.length) {
            html += '<div class="fg-detail-section"><h4><span style="color:#3b82f6;">▸</span> Calls (' + outgoing.length + ')</h4><ul class="fg-detail-func-list">';
            outgoing.sort(function (a, b) { return b.max_depth - a.max_depth; });
            outgoing.forEach(function (n) {
                html += '<li data-fid="' + esc(n.id) + '"><span class="fg-dir-icon ' + n.direction + '"><i class="fa-solid fa-arrow-right"></i></span><span style="flex:1;overflow:hidden;text-overflow:ellipsis;" title="' + esc(n.qualified || n.id) + '">' + esc(n.qualified || n.id) + '</span><span class="fg-depth-badge depth-' + depthClass(n.max_depth) + '">' + n.max_depth + '</span></li>';
            });
            html += '</ul></div>';
        }

        var incoming = neighbors.filter(function (n) { return n.direction === 'incoming' || n.direction === 'both'; });
        if (incoming.length) {
            html += '<div class="fg-detail-section"><h4><span style="color:#22c55e;">◂</span> Called By (' + incoming.length + ')</h4><ul class="fg-detail-func-list">';
            incoming.sort(function (a, b) { return b.max_depth - a.max_depth; });
            incoming.forEach(function (n) {
                html += '<li data-fid="' + esc(n.id) + '"><span class="fg-dir-icon ' + n.direction + '"><i class="fa-solid fa-arrow-left"></i></span><span style="flex:1;overflow:hidden;text-overflow:ellipsis;" title="' + esc(n.qualified || n.id) + '">' + esc(n.qualified || n.id) + '</span><span class="fg-depth-badge depth-' + depthClass(n.max_depth) + '">' + n.max_depth + '</span></li>';
            });
            html += '</ul></div>';
        }

        $('#fgDetailPanel').html(html).show().addClass('visible');
    }

    function statCell(val, label) {
        return '<div class="fg-stat-cell"><div class="fg-stat-val">' + (val || 0).toLocaleString() + '</div><div class="fg-stat-label">' + label + '</div></div>';
    }

    function depthClass(d) {
        if (d === 0) return '0'; if (d <= 5) return '1'; if (d <= 15) return '2'; if (d <= 30) return '3';
        if (d <= 60) return '4'; if (d <= 100) return '5'; if (d <= 200) return '6'; return '7';
    }

    $(document).on('click', '.fg-detail-func-list li', function () {
        var fid = $(this).attr('data-fid');
        if (fid) loadNode(fid);
    });

    // ===================== CLICK NODE IN SVG =====================

    $(document).on('click', '.fg-node', function () {
        if (fgDragged) return;
        var el = $(this).closest('[data-nid]');
        var nid = el.data('nid');
        if (!nid || nid === currentCenter) return;
        loadNode(nid);
    });

    // ===================== ZOOM / PAN =====================

    function fgApplyTransform() {
        $('#fgGraphSvg').css('transform', 'translate(' + fgPanX + 'px,' + fgPanY + 'px) scale(' + fgZoom + ')');
    }

    function fgFitToView() {
        var container = $('#fgGraphView');
        var cw = container.width(), ch = container.height();
        if (!cw || !ch || !fgSvgW || !fgSvgH) return;
        var scaleX = (cw - 40) / fgSvgW, scaleY = (ch - 40) / fgSvgH;
        fgZoom = Math.min(scaleX, scaleY, 1.5);
        fgZoom = Math.max(fgZoom, 0.05);
        fgPanX = (cw - fgSvgW * fgZoom) / 2;
        fgPanY = (ch - fgSvgH * fgZoom) / 2;
        fgApplyTransform();
    }

    $(document).on('click', '#fgZoomIn', function () { fgZoom = Math.min(fgZoom * 1.25, 4); fgApplyTransform(); });
    $(document).on('click', '#fgZoomOut', function () { fgZoom = Math.max(fgZoom * 0.8, 0.05); fgApplyTransform(); });
    $(document).on('click', '#fgZoomFit', function () { fgFitToView(); });

    function fgHandleWheel(e) {
        e.preventDefault();
        var delta = e.deltaY < 0 ? 1.1 : 0.9;
        var newZoom = Math.max(0.05, Math.min(fgZoom * delta, 5));
        var el = document.getElementById('fgGraphView');
        if (!el) return;
        var rect = el.getBoundingClientRect();
        var mx = e.clientX - rect.left, my = e.clientY - rect.top;
        fgPanX = mx - (mx - fgPanX) * (newZoom / fgZoom);
        fgPanY = my - (my - fgPanY) * (newZoom / fgZoom);
        fgZoom = newZoom;
        fgApplyTransform();
    }

    function fgAttachWheel() { fgDetachWheel(); var el = document.getElementById('fgGraphView'); if (el) el.addEventListener('wheel', fgHandleWheel, { passive: false }); }
    function fgDetachWheel() { var el = document.getElementById('fgGraphView'); if (el) el.removeEventListener('wheel', fgHandleWheel); }

    $(document).on('mousedown', '#fgGraphView', function (e) {
        if ($(e.target).closest('button, .fg-controls, .fg-legend').length) return;
        fgIsPanning = true; fgDragged = false;
        fgPanStartX = e.clientX; fgPanStartY = e.clientY;
        fgPanStartPanX = fgPanX; fgPanStartPanY = fgPanY;
        $(this).addClass('grabbing'); e.preventDefault();
    });

    $(document).on('mousemove', function (e) {
        if (!fgIsPanning) return;
        var dx = e.clientX - fgPanStartX, dy = e.clientY - fgPanStartY;
        if (Math.abs(dx) > 3 || Math.abs(dy) > 3) fgDragged = true;
        fgPanX = fgPanStartPanX + dx; fgPanY = fgPanStartPanY + dy;
        fgApplyTransform();
    });

    $(document).on('mouseup', function () {
        if (fgIsPanning) { fgIsPanning = false; $('#fgGraphView').removeClass('grabbing'); setTimeout(function () { fgDragged = false; }, 50); }
    });

    // ===================== HELPERS =====================

    function updateSidebarActive() {
        $('.fg-func-item').removeClass('active');
        if (currentCenter) $('.fg-func-item[data-fid="' + currentCenter + '"]').addClass('active');
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
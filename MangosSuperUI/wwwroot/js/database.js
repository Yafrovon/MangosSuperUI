// MangosSuperUI — Database Explorer JS

$(function () {

    // ===================== STATE =====================

    var currentDb = null;
    var currentTable = null;
    var currentPage = 1;
    var pageSize = parseInt(localStorage.getItem('msui_db_pageSize')) || 50;
    var lastTotalPages = 1;
    var currentSort = null;
    var currentSortDir = 'asc';
    var currentFilterCol = null;
    var currentFilterVal = null;
    var selectedRowPks = null;   // { columns: [...], values: [...] } of clicked row
    var selectedRowData = null;  // full row object
    var breadcrumbs = [];        // [{ db, table, filterCol, filterVal, label }]
    var showHumanNames = false;
    var humanNames = {};         // loaded from localStorage, key = "db.table.column" → "Human Name"
    var schema = null;           // current table schema response
    var tableListData = null;    // cached table list
    var showInsertForm = false;

    // Load human names from localStorage
    try {
        var stored = localStorage.getItem('msui_db_humanNames');
        if (stored) humanNames = JSON.parse(stored);
    } catch (e) { }

    // ===================== TABLE SIDEBAR =====================

    function loadTableList() {
        $.getJSON('/Database/Tables', function (data) {
            tableListData = data;
            $('#totalTableCount').text(data.totalTables);
            $('#dbCount').text(Object.keys(data.databases).length);
            renderTableList('');
        }).fail(function () {
            $('#tableList').html('<div class="db-empty-state"><i class="fa-solid fa-exclamation-triangle"></i><p>Failed to load tables</p></div>');
        });
    }

    function renderTableList(filter) {
        if (!tableListData) return;
        var html = '';
        var dbOrder = ['mangos', 'characters', 'realmd', 'logs'];
        var filterLower = (filter || '').toLowerCase();

        dbOrder.forEach(function (db) {
            var tables = tableListData.databases[db];
            if (!tables) return;

            var filtered = tables;
            if (filterLower) {
                filtered = tables.filter(function (t) {
                    return t.table.toLowerCase().indexOf(filterLower) !== -1;
                });
            }
            if (filtered.length === 0) return;

            html += '<div class="db-group-header" data-db="' + db + '">'
                + '<span>' + esc(db) + '</span>'
                + '<span class="count">' + filtered.length + '</span>'
                + '</div>';

            filtered.forEach(function (t) {
                var isActive = currentDb === db && currentTable === t.table;
                html += '<div class="db-table-item' + (isActive ? ' active' : '') + '" data-db="' + db + '" data-table="' + esc(t.table) + '">'
                    + '<span>' + esc(t.table) + '</span>';
                if (t.totalEdges > 0)
                    html += '<span class="edges">' + t.totalEdges + '</span>';
                html += '</div>';
            });
        });

        if (!html) {
            html = '<div class="db-empty-state"><p>No tables match filter</p></div>';
        }

        $('#tableList').html(html);
    }

    // Filter sidebar on search
    var tableFilterTimer;
    $('#tableSearchInput').on('input', function () {
        var val = $(this).val();
        clearTimeout(tableFilterTimer);
        tableFilterTimer = setTimeout(function () { renderTableList(val); }, 150);
    });

    // Click table in sidebar
    $(document).on('click', '.db-table-item', function () {
        var db = $(this).data('db');
        var table = $(this).data('table');
        // Reset navigation
        breadcrumbs = [];
        currentFilterCol = null;
        currentFilterVal = null;
        loadTable(db, table);
    });

    // ===================== LOAD TABLE =====================

    function loadTable(db, table, filterCol, filterVal) {
        currentDb = db;
        currentTable = table;
        currentPage = 1;
        currentSort = null;
        currentSortDir = 'asc';
        currentFilterCol = filterCol || null;
        currentFilterVal = filterVal || null;
        selectedRowPks = null;
        selectedRowData = null;
        showInsertForm = false;

        // Update sidebar highlight
        $('.db-table-item').removeClass('active');
        $('.db-table-item[data-db="' + db + '"][data-table="' + table + '"]').addClass('active');

        // Hide relationship panel and map view
        $('#relPanel').hide();
        if (mapViewActive) closeMapView();
        if (erDiagramActive) closeERDiagram();

        // Show loading in data grid
        $('#dataGrid').html('<div class="db-empty-state"><i class="fa-solid fa-spinner fa-spin"></i><p>Loading schema...</p></div>');
        $('#toolbar').show();
        $('#pagination').show();

        // Load schema, then data
        $.getJSON('/Database/Schema/' + encodeURIComponent(db) + '/' + encodeURIComponent(table), function (s) {
            schema = s;
            // Push breadcrumb
            var label = table;
            if (filterCol && filterVal) label += ' (' + filterCol + '=' + filterVal + ')';
            // Only push if not already at this exact breadcrumb
            var lastCrumb = breadcrumbs.length > 0 ? breadcrumbs[breadcrumbs.length - 1] : null;
            if (!lastCrumb || lastCrumb.db !== db || lastCrumb.table !== table || lastCrumb.filterVal !== filterVal) {
                breadcrumbs.push({ db: db, table: table, filterCol: filterCol, filterVal: filterVal, label: label });
            }
            renderToolbar();
            loadData();
        }).fail(function () {
            $('#dataGrid').html('<div class="db-empty-state"><i class="fa-solid fa-exclamation-triangle"></i><p>Failed to load schema</p></div>');
        });
    }

    // ===================== LOAD DATA =====================

    function loadData() {
        if (!schema) return;

        var params = {
            page: currentPage,
            pageSize: pageSize
        };
        if (currentSort) { params.sort = currentSort; params.dir = currentSortDir; }
        if (currentFilterVal) {
            params.filterVal = currentFilterVal;
            if (currentFilterCol) params.filterCol = currentFilterCol;
        }

        $.getJSON('/Database/Data/' + encodeURIComponent(currentDb) + '/' + encodeURIComponent(currentTable), params, function (data) {
            renderGrid(data);
            renderPagination(data);
        }).fail(function () {
            $('#dataGrid').html('<div class="db-empty-state"><i class="fa-solid fa-exclamation-triangle"></i><p>Failed to load data</p></div>');
        });
    }

    // ===================== RENDER GRID =====================

    function renderGrid(data) {
        if (!schema || !data.rows || data.rows.length === 0) {
            var msg = data.totalRows === 0 ? 'No rows found' : 'No results on this page';
            $('#dataGrid').html('<div class="db-empty-state"><i class="fa-solid fa-table"></i><p>' + msg + '</p></div>');
            return;
        }

        var columns = schema.columns;
        var pks = schema.primaryKeys || [];
        var humanClass = showHumanNames ? ' show-human' : '';

        var html = '';

        // Insert form (if visible)
        if (showInsertForm && !schema.readOnly) {
            html += renderInsertForm(columns);
        }

        html += '<table class="db-grid' + humanClass + '">';
        html += '<thead><tr>';

        // Row number column
        html += '<th style="width: 40px; text-align: center;">#</th>';

        for (var i = 0; i < columns.length; i++) {
            var col = columns[i];
            var colName = col.name;
            var isSorted = currentSort === colName;
            var sortedClass = isSorted ? ' sorted' : '';
            var sortIcon = '';
            if (isSorted) {
                sortIcon = currentSortDir === 'asc'
                    ? ' <i class="fa-solid fa-caret-up sort-icon"></i>'
                    : ' <i class="fa-solid fa-caret-down sort-icon"></i>';
            }
            var isPk = pks.indexOf(colName) !== -1;
            var pkIcon = isPk ? '<i class="fa-solid fa-key" style="color: var(--status-warning); font-size: 9px; margin-right: 3px;"></i>' : '';
            var hName = getHumanName(currentDb, currentTable, colName);

            html += '<th class="' + sortedClass + '" data-col="' + esc(colName) + '">'
                + pkIcon
                + '<span class="raw-name">' + esc(colName) + '</span>'
                + '<span class="human-name">' + esc(hName || colName) + '</span>'
                + sortIcon
                + '<span class="col-resize-handle"></span>'
                + '</th>';
        }
        html += '</tr></thead><tbody>';

        var startRow = (data.page - 1) * data.pageSize;
        for (var r = 0; r < data.rows.length; r++) {
            var row = data.rows[r];
            var rowPkVals = pks.map(function (pk) { return row[pk]; });
            var isSelected = selectedRowPks && JSON.stringify(selectedRowPks.values) === JSON.stringify(rowPkVals);

            html += '<tr class="' + (isSelected ? 'selected' : '') + '" data-pk-json="' + esc(JSON.stringify(rowPkVals)) + '">';
            html += '<td style="text-align: center; color: var(--text-muted); font-size: 11px;">' + (startRow + r + 1) + '</td>';

            for (var c = 0; c < columns.length; c++) {
                var cn = columns[c].name;
                var val = row[cn];
                if (val === null || val === undefined) {
                    html += '<td class="null-val" data-col="' + esc(cn) + '">NULL</td>';
                } else {
                    html += '<td data-col="' + esc(cn) + '">' + esc(String(val)) + '</td>';
                }
            }
            html += '</tr>';
        }

        html += '</tbody></table>';
        $('#dataGrid').html(html);
        applyColumnWidths();
    }

    // ===================== RENDER INSERT FORM =====================

    function renderInsertForm(columns) {
        var html = '<div class="db-insert-form"><div style="font-size: 13px; font-weight: 600; margin-bottom: 8px;">'
            + '<i class="fa-solid fa-plus" style="color: var(--accent);"></i> Insert New Row</div>'
            + '<div class="db-insert-grid">';

        for (var i = 0; i < columns.length; i++) {
            var col = columns[i];
            var placeholder = col.type;
            if (col.default && col.default !== 'NULL') placeholder += ' (default: ' + col.default + ')';
            html += '<div class="db-insert-field">'
                + '<label>' + esc(col.name) + '</label>'
                + '<input type="text" class="insert-field" data-col="' + esc(col.name) + '" placeholder="' + esc(placeholder) + '" />'
                + '</div>';
        }

        html += '</div><div class="db-insert-actions">'
            + '<button class="btn-accent" id="btnInsertSave" style="padding: 6px 14px; font-size: 12px;"><i class="fa-solid fa-plus"></i> Insert</button>'
            + '<button class="btn-outline-subtle" id="btnInsertCancel" style="padding: 6px 14px; font-size: 12px;">Cancel</button>'
            + '</div></div>';
        return html;
    }

    // ===================== RENDER TOOLBAR =====================

    function renderToolbar() {
        var html = '<div class="db-breadcrumbs">';

        // Back button when there's history
        if (breadcrumbs.length > 1) {
            html += '<button class="db-back-btn" id="btnGoBack" title="Go back"><i class="fa-solid fa-arrow-left"></i></button>';
        }

        for (var i = 0; i < breadcrumbs.length; i++) {
            if (i > 0) html += '<span class="db-breadcrumb-sep"><i class="fa-solid fa-chevron-right"></i></span>';
            var isLast = i === breadcrumbs.length - 1;
            html += '<span class="db-breadcrumb' + (isLast ? ' current' : '') + '" data-idx="' + i + '">'
                + esc(breadcrumbs[i].label)
                + '</span>';
        }
        html += '</div>';

        // Search bar
        html += '<div class="db-search-bar">';
        html += '<select id="searchCol" class="db-search-col">';
        html += '<option value="">All columns</option>';
        if (schema && schema.columns) {
            for (var c = 0; c < schema.columns.length; c++) {
                var col = schema.columns[c];
                var selected = currentFilterCol === col.name ? ' selected' : '';
                html += '<option value="' + esc(col.name) + '"' + selected + '>' + esc(col.name) + '</option>';
            }
        }
        html += '</select>';
        html += '<input type="text" id="searchVal" class="db-search-input" placeholder="Search..." value="' + esc(currentFilterVal || '') + '" />';
        html += '<button class="db-search-btn" id="btnSearch" title="Search"><i class="fa-solid fa-search"></i></button>';
        if (currentFilterCol || currentFilterVal) {
            html += '<button class="db-search-btn clear" id="btnClearSearch" title="Clear search"><i class="fa-solid fa-xmark"></i></button>';
        }
        html += '</div>';

        // Buttons
        html += '<div style="display: flex; align-items: center; gap: 6px;">';

        // Read-only badge
        if (schema && schema.readOnly) {
            html += '<span class="db-readonly-badge"><i class="fa-solid fa-lock"></i> ' + esc(schema.readOnlyReason || 'Read-only') + '</span>';
        }

        // Row ops
        html += '<div class="db-row-ops">';

        if (schema && !schema.readOnly) {
            html += '<button id="btnAddRow" title="Insert new row"><i class="fa-solid fa-plus"></i> Add</button>';
            html += '<button id="btnCloneRow" title="Clone selected row" disabled><i class="fa-solid fa-copy"></i> Clone</button>';
            html += '<button id="btnDeleteRow" class="danger" title="Delete selected row" disabled><i class="fa-solid fa-trash"></i> Delete</button>';
        }
        html += '<button id="btnShowRels" title="Show relationships sidebar" disabled><i class="fa-solid fa-project-diagram"></i> Relationships</button>';
        html += '<button id="btnRelMap" title="Relationship map view" disabled><i class="fa-solid fa-sitemap"></i> Map</button>';
        html += '<button id="btnERDiagram" title="ER Diagram view"><i class="fa-solid fa-diagram-project"></i> ER Diagram</button>';
        html += '<button id="btnExportSql" title="Export selected row + related data as .sql" disabled><i class="fa-solid fa-file-export"></i> Export SQL</button>';
        html += '</div>';

        // Column name toggle
        html += '<button class="btn-outline-subtle" id="btnToggleNames" style="padding: 5px 10px; font-size: 11.5px;" title="Toggle human-readable column names">'
            + '<i class="fa-solid fa-font"></i>'
            + '</button>';

        // CSV Export
        html += '<button class="btn-outline-subtle" id="btnExportCsv" style="padding: 5px 10px; font-size: 11.5px;" title="Export table to CSV">'
            + '<i class="fa-solid fa-file-csv"></i>'
            + '</button>';

        html += '</div>';

        $('#toolbar').html(html);
    }

    // ===================== RENDER PAGINATION =====================

    function renderPagination(data) {
        lastTotalPages = data ? data.totalPages : 1;

        if (!data || data.totalRows === 0) {
            $('#pagination').html('<span>0 rows</span><div></div>');
            return;
        }

        var html = '';
        if (data.totalPages > 1) {
            html += '<span>Showing ' + ((data.page - 1) * data.pageSize + 1) + '–'
                + Math.min(data.page * data.pageSize, data.totalRows) + ' of '
                + data.totalRows.toLocaleString() + ' rows</span>';
        } else {
            html += '<span>' + data.totalRows.toLocaleString() + ' rows</span>';
        }

        html += '<div class="db-page-btns">';
        if (data.totalPages > 1) {
            html += '<button id="btnPageFirst" ' + (data.page <= 1 ? 'disabled' : '') + '><i class="fa-solid fa-angles-left"></i></button>';
            html += '<button id="btnPagePrev" ' + (data.page <= 1 ? 'disabled' : '') + '><i class="fa-solid fa-chevron-left"></i></button>';
            html += '<input type="number" id="dbPageJump" class="db-page-jump" value="' + data.page + '" min="1" max="' + data.totalPages + '" title="Jump to page" />';
            html += '<span class="db-page-sep">of ' + data.totalPages + '</span>';
            html += '<button id="btnPageNext" ' + (data.page >= data.totalPages ? 'disabled' : '') + '><i class="fa-solid fa-chevron-right"></i></button>';
            html += '<button id="btnPageLast" ' + (data.page >= data.totalPages ? 'disabled' : '') + '><i class="fa-solid fa-angles-right"></i></button>';
        }
        html += '<span class="db-page-size-sep">|</span>';
        html += '<select id="dbPageSize" class="db-page-size-select" title="Rows per page">';
        [50, 100, 200, 500].forEach(function (n) {
            html += '<option value="' + n + '"' + (pageSize === n ? ' selected' : '') + '>' + n + '</option>';
        });
        html += '</select>';
        html += '</div>';

        $('#pagination').html(html);
    }

    // ===================== ROW SELECTION =====================

    $(document).on('click', '.db-grid tbody tr', function (e) {
        // Don't select row if clicking inside an editing cell
        if ($(e.target).closest('.editing').length) return;
        if (!schema) return;
        var pks = schema.primaryKeys || [];
        if (pks.length === 0) return;

        // Toggle selection
        var wasSelected = $(this).hasClass('selected');
        $('.db-grid tbody tr').removeClass('selected');

        if (wasSelected) {
            selectedRowPks = null;
            selectedRowData = null;
        } else {
            $(this).addClass('selected');
            try {
                var pkVals = JSON.parse($(this).attr('data-pk-json'));
                selectedRowPks = { columns: pks, values: pkVals };
            } catch (ex) {
                console.error('Failed to parse PK values:', $(this).attr('data-pk-json'), ex);
                selectedRowPks = null;
            }

            // Reconstruct row data from cells
            selectedRowData = {};
            $(this).find('td[data-col]').each(function () {
                var col = $(this).data('col');
                var val = $(this).hasClass('null-val') ? null : $(this).text();
                selectedRowData[col] = val;
            });
        }

        // Enable/disable buttons
        var hasSelection = selectedRowPks !== null;
        $('#btnShowRels').prop('disabled', !hasSelection);
        $('#btnRelMap').prop('disabled', !hasSelection);
        $('#btnCloneRow').prop('disabled', !hasSelection);
        $('#btnDeleteRow').prop('disabled', !hasSelection);
        $('#btnExportSql').prop('disabled', !hasSelection);
    });

    // ===================== SORTING =====================

    var _justResized = false;
    $(document).on('click', '.db-grid th[data-col]', function (e) {
        // Don't sort if the click originated from the resize handle
        if (_justResized || $(e.target).hasClass('col-resize-handle')) return;
        var col = $(this).data('col');
        if (currentSort === col) {
            currentSortDir = currentSortDir === 'asc' ? 'desc' : 'asc';
        } else {
            currentSort = col;
            currentSortDir = 'asc';
        }
        currentPage = 1;
        loadData();
    });

    // ===================== INLINE EDITING =====================

    $(document).on('dblclick', '.db-grid td[data-col]', function (e) {
        if (schema && schema.readOnly) return;
        if ($(this).hasClass('editing')) return;

        var td = $(this);
        var col = td.data('col');
        var currentVal = td.hasClass('null-val') ? '' : td.text();
        var tr = td.closest('tr');
        var pkVals = JSON.parse(tr.attr('data-pk-json'));
        var pks = schema.primaryKeys || [];

        td.addClass('editing');
        var input = $('<input type="text" />')
            .val(currentVal)
            .attr('data-original', currentVal);
        td.html('').append(input);
        input.focus().select();

        function commitEdit() {
            var newVal = input.val();
            var original = input.attr('data-original');
            td.removeClass('editing');

            // If empty and original was NULL, treat as NULL
            if (newVal === '' && original === '') {
                td.addClass('null-val').text('NULL');
                return;
            }

            // No change
            if (newVal === original) {
                if (original === '') {
                    td.addClass('null-val').text('NULL');
                } else {
                    td.removeClass('null-val').text(original);
                }
                return;
            }

            // Save
            var sendVal = newVal === '' ? null : newVal;
            td.removeClass('null-val').text(newVal || 'NULL');
            if (!newVal) td.addClass('null-val');

            $.ajax({
                url: '/Database/Update',
                method: 'POST',
                contentType: 'application/json',
                data: JSON.stringify({
                    db: currentDb,
                    table: currentTable,
                    pkColumns: pks,
                    pkValues: pkVals.map(String),
                    column: col,
                    value: sendVal
                }),
                success: function (result) {
                    if (result.success) {
                        showToast('Updated ' + col, 'success');
                    } else {
                        showToast('Update failed: ' + (result.error || 'Unknown'), 'error');
                        // Revert
                        td.removeClass('null-val');
                        if (original === '') td.addClass('null-val').text('NULL');
                        else td.text(original);
                    }
                },
                error: function () {
                    showToast('Update failed — server error', 'error');
                    td.removeClass('null-val');
                    if (original === '') td.addClass('null-val').text('NULL');
                    else td.text(original);
                }
            });
        }

        input.on('keydown', function (e) {
            if (e.key === 'Enter') { commitEdit(); }
            if (e.key === 'Escape') {
                td.removeClass('editing');
                if (input.attr('data-original') === '') td.addClass('null-val').text('NULL');
                else td.removeClass('null-val').text(input.attr('data-original'));
            }
            if (e.key === 'Tab') {
                e.preventDefault();
                commitEdit();
                // Move to next cell
                var nextTd = td.next('td[data-col]');
                if (nextTd.length) nextTd.trigger('dblclick');
            }
        });

        input.on('blur', function () {
            // Small delay to allow click events to fire first
            setTimeout(function () {
                if (td.hasClass('editing')) commitEdit();
            }, 100);
        });
    });

    // ===================== RELATIONSHIPS =====================

    // ===================== RELATIONSHIPS =====================

    var lastRelData = null;

    $(document).on('click', '#btnShowRels', function () {
        if (!selectedRowPks || !schema) return;

        var pkCol = selectedRowPks.columns[0];
        var pkVal = selectedRowPks.values[0];

        $('#relPanel').show().html('<div class="rel-loading"><i class="fa-solid fa-spinner fa-spin"></i> Loading relationships...</div>');

        $.getJSON('/Database/Relationships/' + encodeURIComponent(currentDb) + '/'
            + encodeURIComponent(currentTable) + '/'
            + encodeURIComponent(pkCol) + '/'
            + encodeURIComponent(pkVal), function (data) {
                lastRelData = data;
                renderRelationships(data);
            }).fail(function () {
                $('#relPanel').html('<div class="rel-loading">Failed to load relationships</div>');
            });
    });

    function renderRelationships(data) {
        var html = '<div class="rel-header">'
            + '<span><i class="fa-solid fa-project-diagram" style="color: var(--accent);"></i> Relationships</span>'
            + '<button class="close-btn" id="btnCloseRels"><i class="fa-solid fa-xmark"></i></button>'
            + '</div>';

        // Source row identity
        html += '<div class="rel-source">'
            + '<span class="col-name">' + esc(data.sourceTable) + '</span>.<span class="col-name">' + esc(data.sourceColumn) + '</span>'
            + ' = <span class="col-val">' + esc(String(data.sourceValue)) + '</span>'
            + '</div>';

        if (!data.edges || data.edges.length === 0) {
            html += '<div class="rel-loading" style="color: var(--text-muted);">No relationships found</div>';
            $('#relPanel').html(html);
            return;
        }

        // Connected edges — rendered as expandable card groups
        var connected = data.edges.filter(function (e) { return e.count > 0; });
        var empty = data.edges.filter(function (e) { return e.count === 0; });

        if (connected.length === 0) {
            html += '<div class="rel-loading" style="color: var(--text-muted);">No connected data for this value</div>';
        } else {
            var connOut = connected.filter(function (e) { return e.direction === 'outbound'; });
            var connIn = connected.filter(function (e) { return e.direction === 'inbound'; });

            if (connOut.length > 0) {
                html += '<div class="rel-section-title">References (' + connOut.length + ')</div>';
                connOut.forEach(function (e, i) { html += renderRelCard(e, data.sourceValue, 'out-' + i); });
            }

            if (connIn.length > 0) {
                html += '<div class="rel-section-title">Referenced By (' + connIn.length + ')</div>';
                connIn.forEach(function (e, i) { html += renderRelCard(e, data.sourceValue, 'in-' + i); });
            }
        }

        // Empty edges collapsed
        if (empty.length > 0) {
            html += '<div class="rel-empty-toggle" id="btnShowEmptyRels">'
                + '<i class="fa-solid fa-chevron-right"></i> '
                + empty.length + ' potential ' + (empty.length === 1 ? 'relationship' : 'relationships') + ' (0 rows)'
                + '</div>';
            html += '<div class="rel-empty-edges" style="display: none;">';
            empty.forEach(function (e, i) { html += renderRelCard(e, data.sourceValue, 'empty-' + i); });
            html += '</div>';
        }

        $('#relPanel').html(html);
    }

    // Render a single relationship as an expandable card group
    function renderRelCard(edge, sourceValue, uid) {
        var hasRows = edge.count > 0;
        var countLabel = edge.count.toLocaleString() + (edge.count === 1 ? ' row' : ' rows');
        var confidence = edge.confidence === 'proven'
            ? '<span style="color: #22c55e; font-size: 9px;" title="Proven (score ' + edge.score + ')">&#9679;</span>'
            : '<span style="color: var(--status-warning); font-size: 9px;" title="Likely (score ' + edge.score + ')">&#9679;</span>';
        var dirIcon = edge.direction === 'outbound'
            ? '<i class="fa-solid fa-arrow-right" style="font-size: 8px; opacity: 0.5;"></i>'
            : '<i class="fa-solid fa-arrow-left" style="font-size: 8px; opacity: 0.5;"></i>';

        var html = '<div class="rel-card-group" data-uid="' + uid + '">'
            + '<div class="rel-card-group-header' + (hasRows ? '' : ' empty') + '" data-uid="' + uid + '"'
            + ' data-db="' + esc(edge.targetDb) + '" data-table="' + esc(edge.targetTable) + '"'
            + ' data-col="' + esc(edge.targetCol) + '" data-val="' + esc(String(sourceValue)) + '">'
            + '<div class="rel-card-group-left">'
            + (hasRows ? '<i class="fa-solid fa-chevron-right rel-card-chevron"></i> ' : '')
            + confidence + ' ' + dirIcon + ' '
            + '<span class="rel-card-group-name">' + esc(edge.targetTable) + '</span>'
            + '</div>'
            + '<div class="rel-card-group-right">'
            + '<span class="rel-edge-count ' + (hasRows ? 'has-rows' : 'no-rows') + '">' + countLabel + '</span>'
            + '<button class="rel-card-nav-btn" title="Open in table view" data-db="' + esc(edge.targetDb) + '" data-table="' + esc(edge.targetTable) + '"'
            + ' data-col="' + esc(edge.targetCol) + '" data-val="' + esc(String(sourceValue)) + '">'
            + '<i class="fa-solid fa-external-link"></i></button>'
            + '</div>'
            + '</div>';

        // Expandable body — loaded on first click
        if (hasRows) {
            html += '<div class="rel-card-group-body" data-uid="' + uid + '" style="display: none;">'
                + '<div class="rel-loading"><i class="fa-solid fa-spinner fa-spin"></i></div>'
                + '</div>';
        }

        html += '</div>';
        return html;
    }

    // Expand/collapse a card group — loads data on first expand
    $(document).on('click', '.rel-card-group-header:not(.empty)', function (e) {
        if ($(e.target).closest('.rel-card-nav-btn').length) return;

        var uid = $(this).data('uid');
        var body = $('.rel-card-group-body[data-uid="' + uid + '"]');
        var chevron = $(this).find('.rel-card-chevron');

        if (body.is(':visible')) {
            body.slideUp(150);
            chevron.removeClass('expanded');
        } else {
            body.slideDown(150);
            chevron.addClass('expanded');

            // Load data on first expand
            if (body.find('.rel-loading').length) {
                var db = $(this).data('db');
                var table = $(this).data('table');
                var col = $(this).data('col');
                var val = $(this).data('val');
                loadCardRows(db, table, col, String(val), body);
            }
        }
    });

    function loadCardRows(db, table, col, val, container) {
        $.getJSON('/Database/RelatedRows/' + encodeURIComponent(db) + '/'
            + encodeURIComponent(table) + '/'
            + encodeURIComponent(col) + '/'
            + encodeURIComponent(val), function (data) {
                renderCardRows(data, container);
            }).fail(function () {
                container.html('<div style="padding: 10px 16px; color: var(--status-error); font-size: 12px;">Failed to load rows</div>');
            });
    }

    function renderCardRows(data, container) {
        if (!data.rows || data.rows.length === 0) {
            container.html('<div style="padding: 10px 16px; color: var(--text-muted); font-size: 12px;">No rows</div>');
            return;
        }

        var html = '';
        var cols = data.columns || Object.keys(data.rows[0]);

        data.rows.forEach(function (row, idx) {
            if (idx > 0) {
                html += '<div class="rel-card-connector"><div class="rel-card-connector-line"></div></div>';
            }

            html += '<div class="rel-card-row-card">';
            var keyColCount = Math.min(6, cols.length);
            for (var c = 0; c < cols.length; c++) {
                var colName = cols[c];
                var val = row[colName];
                var isNull = val === null || val === undefined;
                var isKey = c < keyColCount;

                html += '<div class="rel-card-field' + (isKey ? '' : ' secondary') + '">'
                    + '<span class="rel-card-key">' + esc(colName) + '</span>'
                    + '<span class="rel-card-val' + (isNull ? ' null-val' : '') + '">' + (isNull ? 'NULL' : esc(String(val))) + '</span>'
                    + '</div>';
            }
            html += '</div>';
        });

        if (data.totalRows >= 50) {
            html += '<div style="padding: 8px 12px; font-size: 10.5px; color: var(--text-muted); text-align: center;">Showing first 50</div>';
        }

        container.html(html);
    }

    // Navigate button on card group header
    $(document).on('click', '.rel-card-nav-btn', function (e) {
        e.stopPropagation();
        var db = $(this).data('db');
        var table = $(this).data('table');
        var col = $(this).data('col');
        var val = $(this).data('val');
        loadTable(db, table, col, String(val));
    });

    // Toggle empty relationships
    $(document).on('click', '#btnShowEmptyRels', function () {
        var container = $('.rel-empty-edges');
        var icon = $(this).find('i');
        container.slideToggle(150);
        icon.toggleClass('fa-chevron-right fa-chevron-down');
    });

    // Close relationship panel
    $(document).on('click', '#btnCloseRels', function () {
        $('#relPanel').hide();
    });

    // ===================== RELATIONSHIP MAP (FULL-WIDTH VIEW) =====================

    var mapViewActive = false;

    $(document).on('click', '#btnRelMap', function () {
        if (!selectedRowPks || !schema) return;

        var pkCol = selectedRowPks.columns[0];
        var pkVal = selectedRowPks.values[0];

        // Toggle off if already active
        if (mapViewActive) {
            closeMapView();
            return;
        }

        mapViewActive = true;
        $('#dataGrid').hide();
        $('#pagination').hide();
        $('#relMapView').remove();

        // Insert map view container after toolbar
        var mapHtml = '<div id="relMapView" class="card" style="flex: 1; overflow-y: auto; max-height: calc(100vh - 240px);">'
            + '<div class="rel-map-loading"><i class="fa-solid fa-spinner fa-spin"></i> Building relationship map...</div>'
            + '</div>';
        $('#dataGrid').after(mapHtml);

        // Style the Map button as active
        $('#btnRelMap').addClass('active-toggle');

        // Fetch relationships then build the map
        $.getJSON('/Database/Relationships/' + encodeURIComponent(currentDb) + '/'
            + encodeURIComponent(currentTable) + '/'
            + encodeURIComponent(pkCol) + '/'
            + encodeURIComponent(pkVal), function (data) {
                renderMapView(data, selectedRowData);
            }).fail(function () {
                $('#relMapView').html('<div class="rel-map-loading">Failed to load relationships</div>');
            });
    });

    function closeMapView() {
        mapViewActive = false;
        $('#relMapView').remove();
        $('#dataGrid').show();
        $('#pagination').show();
        $('#btnRelMap').removeClass('active-toggle');
    }

    function renderMapView(relData, sourceRow) {
        var connected = relData.edges.filter(function (e) { return e.count > 0; });
        var outbound = connected.filter(function (e) { return e.direction === 'outbound'; });
        var inbound = connected.filter(function (e) { return e.direction === 'inbound'; });

        var html = '';

        // Close bar
        html += '<div class="rel-map-topbar">'
            + '<span style="font-size: 13px; font-weight: 600; color: var(--text-primary);">'
            + '<i class="fa-solid fa-sitemap" style="color: var(--accent);"></i> Relationship Map</span>'
            + '<button class="btn-outline-subtle" id="btnCloseMap" style="padding: 4px 10px; font-size: 12px;">'
            + '<i class="fa-solid fa-table"></i> Back to table</button>'
            + '</div>';

        // --- REFERENCES (outbound) flowing UP ---
        if (outbound.length > 0) {
            html += '<div class="rel-map-section">';
            html += '<div class="rel-map-section-label"><i class="fa-solid fa-arrow-up" style="font-size: 10px;"></i> References (' + outbound.length + ')</div>';
            html += '<div class="rel-map-card-grid">';
            outbound.forEach(function (e, i) {
                html += renderMapCard(e, relData.sourceValue, 'map-out-' + i);
            });
            html += '</div></div>';

            // Connector lines down to source
            html += '<div class="rel-map-connector-vertical"><div class="rel-map-vline"></div><i class="fa-solid fa-chevron-down" style="font-size: 10px; color: var(--border-medium);"></i></div>';
        }

        // --- SOURCE ROW (center) ---
        html += '<div class="rel-map-source-card">';
        html += '<div class="rel-map-source-header">'
            + '<i class="fa-solid fa-bullseye" style="color: var(--accent);"></i> '
            + '<span>' + esc(currentDb) + '.' + esc(currentTable) + '</span>'
            + '<span style="color: var(--text-muted); font-weight: 400;"> — ' + esc(relData.sourceColumn) + ' = ' + esc(String(relData.sourceValue)) + '</span>'
            + '</div>';
        html += '<div class="rel-map-source-fields">';
        if (sourceRow) {
            var cols = schema.columns || [];
            for (var c = 0; c < cols.length; c++) {
                var colName = cols[c].name;
                var val = sourceRow[colName];
                var isNull = val === null || val === undefined;
                html += '<div class="rel-card-field">'
                    + '<span class="rel-card-key">' + esc(colName) + '</span>'
                    + '<span class="rel-card-val' + (isNull ? ' null-val' : '') + '">' + (isNull ? 'NULL' : esc(String(val))) + '</span>'
                    + '</div>';
            }
        }
        html += '</div></div>';

        // --- REFERENCED BY (inbound) flowing DOWN ---
        if (inbound.length > 0) {
            html += '<div class="rel-map-connector-vertical"><i class="fa-solid fa-chevron-down" style="font-size: 10px; color: var(--border-medium);"></i><div class="rel-map-vline"></div></div>';

            html += '<div class="rel-map-section">';
            html += '<div class="rel-map-section-label"><i class="fa-solid fa-arrow-down" style="font-size: 10px;"></i> Referenced By (' + inbound.length + ')</div>';
            html += '<div class="rel-map-card-grid">';
            inbound.forEach(function (e, i) {
                html += renderMapCard(e, relData.sourceValue, 'map-in-' + i);
            });
            html += '</div></div>';
        }

        if (connected.length === 0) {
            html += '<div class="rel-map-loading" style="color: var(--text-muted);">No connected data for this value</div>';
        }

        $('#relMapView').html(html);
    }

    function renderMapCard(edge, sourceValue, uid) {
        var countLabel = edge.count.toLocaleString() + (edge.count === 1 ? ' row' : ' rows');
        var confidence = edge.confidence === 'proven'
            ? '<span style="color: #22c55e;" title="Proven">&#9679;</span>'
            : '<span style="color: var(--status-warning);" title="Likely">&#9679;</span>';

        return '<div class="rel-map-card" data-uid="' + uid + '"'
            + ' data-db="' + esc(edge.targetDb) + '" data-table="' + esc(edge.targetTable) + '"'
            + ' data-col="' + esc(edge.targetCol) + '" data-val="' + esc(String(sourceValue)) + '">'
            + '<div class="rel-map-card-title">' + confidence + ' ' + esc(edge.targetTable) + '</div>'
            + '<div class="rel-map-card-col">' + esc(edge.targetCol) + '</div>'
            + '<div class="rel-map-card-count">' + countLabel + '</div>'
            + '<div class="rel-map-card-body" data-uid="' + uid + '"></div>'
            + '</div>';
    }

    // Click a map card → load rows into it
    $(document).on('click', '.rel-map-card', function (e) {
        if ($(e.target).closest('.rel-map-card-body').length && $(e.target).closest('.rel-map-card-body').children().length > 0) return;

        var body = $(this).find('.rel-map-card-body');
        if (body.children().length > 0) {
            // Toggle collapse
            body.slideToggle(150);
            $(this).toggleClass('expanded');
            return;
        }

        var db = $(this).data('db');
        var table = $(this).data('table');
        var col = $(this).data('col');
        var val = $(this).data('val');

        $(this).addClass('expanded');
        body.html('<div class="rel-loading" style="padding: 8px;"><i class="fa-solid fa-spinner fa-spin"></i></div>').show();

        $.getJSON('/Database/RelatedRows/' + encodeURIComponent(db) + '/'
            + encodeURIComponent(table) + '/'
            + encodeURIComponent(col) + '/'
            + encodeURIComponent(val) + '?limit=10', function (data) {
                var rowHtml = '';
                if (!data.rows || data.rows.length === 0) {
                    rowHtml = '<div style="padding: 6px; color: var(--text-muted); font-size: 11px;">No rows</div>';
                } else {
                    var cols = data.columns || Object.keys(data.rows[0]);
                    data.rows.forEach(function (row, idx) {
                        if (idx > 0) rowHtml += '<div class="rel-card-connector"><div class="rel-card-connector-line"></div></div>';
                        rowHtml += '<div class="rel-map-inline-row">';
                        for (var c = 0; c < Math.min(8, cols.length); c++) {
                            var v = row[cols[c]];
                            var isNull = v === null || v === undefined;
                            rowHtml += '<div class="rel-card-field">'
                                + '<span class="rel-card-key">' + esc(cols[c]) + '</span>'
                                + '<span class="rel-card-val' + (isNull ? ' null-val' : '') + '">' + (isNull ? 'NULL' : esc(String(v))) + '</span>'
                                + '</div>';
                        }
                        if (cols.length > 8) {
                            rowHtml += '<div class="rel-card-field secondary"><span class="rel-card-key">...</span><span class="rel-card-val">' + (cols.length - 8) + ' more</span></div>';
                        }
                        rowHtml += '</div>';
                    });
                    if (data.totalRows > 10) {
                        rowHtml += '<div style="padding: 4px 8px; font-size: 10px; color: var(--text-muted); text-align: center;">+' + (data.totalRows - 10) + ' more rows</div>';
                    }
                }
                body.html(rowHtml);
            }).fail(function () {
                body.html('<div style="padding: 6px; color: var(--status-error); font-size: 11px;">Failed to load</div>');
            });
    });

    // Close map view
    $(document).on('click', '#btnCloseMap', function () {
        closeMapView();
    });

    // Also close map view when loading a new table
    var origLoadTable = loadTable;
    // (handled already — loadTable hides relPanel and shows dataGrid)

    // ===================== ER DIAGRAM VIEW =====================

    var erDiagramActive = false;
    var erZoom = 1;
    var erPanX = 0, erPanY = 0;
    var erIsPanning = false;
    var erPanStartX = 0, erPanStartY = 0;
    var erPanStartPanX = 0, erPanStartPanY = 0;

    $(document).on('click', '#btnERDiagram', function () {
        if (!currentDb || !currentTable) return;

        if (erDiagramActive) {
            closeERDiagram();
            return;
        }

        erDiagramActive = true;
        erZoom = 1;
        erPanX = 0;
        erPanY = 0;

        $('#dataGrid').hide();
        $('#pagination').hide();
        $('#relPanel').hide();
        if (mapViewActive) closeMapView();
        $('#erDiagramView').remove();

        var containerHtml = '<div id="erDiagramView" class="card" style="flex: 1; min-height: 0;">'
            + '<div class="rel-map-loading"><i class="fa-solid fa-spinner fa-spin"></i> Building ER diagram...</div>'
            + '<div class="er-controls">'
            + '<button id="erZoomIn" title="Zoom in"><i class="fa-solid fa-plus"></i></button>'
            + '<button id="erZoomOut" title="Zoom out"><i class="fa-solid fa-minus"></i></button>'
            + '<button id="erZoomFit" title="Fit to view"><i class="fa-solid fa-expand"></i></button>'
            + '<button id="erCloseBtn" title="Back to table"><i class="fa-solid fa-table"></i></button>'
            + '</div>'
            + '<div class="er-legend">'
            + '<div class="er-legend-item"><div class="er-legend-line"></div> Proven</div>'
            + '<div class="er-legend-item"><div class="er-legend-line dashed"></div> Likely</div>'
            + '<div class="er-legend-item"><i class="fa-solid fa-key" style="color: var(--status-warning); font-size: 9px;"></i> Primary Key</div>'
            + '</div>'
            + '</div>';
        $('#dataGrid').after(containerHtml);
        $('#btnERDiagram').addClass('active-toggle');

        // Fetch table edges
        $.getJSON('/Database/TableEdges/' + encodeURIComponent(currentDb) + '/' + encodeURIComponent(currentTable),
            function (data) {
                buildERDiagram(data);
            }).fail(function () {
                $('#erDiagramView .rel-map-loading').html('<i class="fa-solid fa-exclamation-triangle"></i> Failed to load diagram data');
            });
    });

    function closeERDiagram() {
        erDiagramActive = false;
        erDetachWheel();
        $('#erDiagramView').remove();
        $('#dataGrid').show();
        $('#pagination').show();
        $('#btnERDiagram').removeClass('active-toggle');
    }

    $(document).on('click', '#erCloseBtn', function () { closeERDiagram(); });

    // --- ER Layout Engine ---

    var ER_NODE_W = 220;
    var ER_COL_H = 18;
    var ER_HEADER_H = 40;
    var ER_PAD = 10;
    var ER_MAX_COLS = 15; // Max columns to show per node (truncate wide tables)

    function buildERDiagram(data) {
        var centerKey = data.centerTable;
        var edges = data.edges || [];
        var schemas = data.schemas || {};

        if (!schemas[centerKey]) {
            $('#erDiagramView .rel-map-loading').html('No schema data for ' + centerKey);
            return;
        }

        // Build node list: center + connected tables
        var nodeKeys = [centerKey];
        var connectedKeys = [];
        edges.forEach(function (e) {
            var other = e.fromTable === centerKey ? e.toTable : e.fromTable;
            if (connectedKeys.indexOf(other) === -1 && other !== centerKey) {
                connectedKeys.push(other);
            }
        });

        // Sort connected by edge count descending
        connectedKeys.sort(function (a, b) {
            var aEdges = (schemas[a] || {}).totalEdges || 0;
            var bEdges = (schemas[b] || {}).totalEdges || 0;
            return bEdges - aEdges;
        });

        nodeKeys = nodeKeys.concat(connectedKeys);

        // Calculate node dimensions
        var nodes = {};
        nodeKeys.forEach(function (key) {
            var s = schemas[key];
            if (!s) return;
            var colCount = Math.min(s.columns.length, ER_MAX_COLS);
            var h = ER_HEADER_H + (colCount * ER_COL_H) + ER_PAD;
            if (s.columns.length > ER_MAX_COLS) h += 16; // "+N more" row
            nodes[key] = {
                key: key,
                schema: s,
                w: ER_NODE_W,
                h: h,
                x: 0,
                y: 0,
                isCenter: key === centerKey
            };
        });

        // --- Radial Layout ---
        // Center node in the middle, connected nodes in a circle around it
        var centerNode = nodes[centerKey];
        var numConnected = connectedKeys.length;

        // Calculate radius based on number of nodes
        var avgNodeH = 0;
        connectedKeys.forEach(function (k) { if (nodes[k]) avgNodeH += nodes[k].h; });
        avgNodeH = numConnected > 0 ? avgNodeH / numConnected : 150;

        var circumference = numConnected * (ER_NODE_W + 60);
        var radius = Math.max(350, circumference / (2 * Math.PI));

        // Place center
        var cx = radius + ER_NODE_W + 100;
        var cy = radius + avgNodeH + 100;
        centerNode.x = cx - centerNode.w / 2;
        centerNode.y = cy - centerNode.h / 2;

        // Place connected nodes radially
        var angleStep = (2 * Math.PI) / Math.max(numConnected, 1);
        var startAngle = -Math.PI / 2; // Start at top

        connectedKeys.forEach(function (key, i) {
            if (!nodes[key]) return;
            var angle = startAngle + (i * angleStep);
            var nx = cx + radius * Math.cos(angle) - nodes[key].w / 2;
            var ny = cy + radius * Math.sin(angle) - nodes[key].h / 2;
            nodes[key].x = nx;
            nodes[key].y = ny;
        });

        // Calculate SVG bounds
        var minX = Infinity, minY = Infinity, maxX = -Infinity, maxY = -Infinity;
        Object.values(nodes).forEach(function (n) {
            minX = Math.min(minX, n.x - 20);
            minY = Math.min(minY, n.y - 20);
            maxX = Math.max(maxX, n.x + n.w + 20);
            maxY = Math.max(maxY, n.y + n.h + 20);
        });

        var svgW = maxX - minX;
        var svgH = maxY - minY;

        // Shift all nodes so minX/minY become 20
        var shiftX = 20 - minX;
        var shiftY = 20 - minY;
        Object.values(nodes).forEach(function (n) {
            n.x += shiftX;
            n.y += shiftY;
        });

        // Build SVG
        var svg = '<svg id="erDiagramSvg" width="' + svgW + '" height="' + svgH + '" xmlns="http://www.w3.org/2000/svg">';

        // Defs for arrow markers
        svg += '<defs>'
            + '<marker id="er-arrow-proven" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">'
            + '<polygon points="0 0, 8 3, 0 6" class="er-edge-arrow proven" />'
            + '</marker>'
            + '<marker id="er-arrow-likely" markerWidth="8" markerHeight="6" refX="8" refY="3" orient="auto">'
            + '<polygon points="0 0, 8 3, 0 6" class="er-edge-arrow" />'
            + '</marker>'
            + '</defs>';

        // Draw edges first (behind nodes)
        edges.forEach(function (e) {
            var fromNode = nodes[e.fromTable];
            var toNode = nodes[e.toTable];
            if (!fromNode || !toNode) return;

            // Find the column Y positions for the connecting columns
            var fromY = getColumnY(fromNode, e.fromCol);
            var toY = getColumnY(toNode, e.toCol);

            // Edge exits from right side of source, enters left side of target
            var x1, y1, x2, y2;

            // Determine which side to connect from/to based on relative position
            if (fromNode.x + fromNode.w < toNode.x) {
                // Source is to the left
                x1 = fromNode.x + fromNode.w;
                x2 = toNode.x;
            } else if (toNode.x + toNode.w < fromNode.x) {
                // Source is to the right
                x1 = fromNode.x;
                x2 = toNode.x + toNode.w;
            } else {
                // Overlapping horizontally — use closest sides
                var fromCx = fromNode.x + fromNode.w / 2;
                var toCx = toNode.x + toNode.w / 2;
                if (fromCx < toCx) {
                    x1 = fromNode.x + fromNode.w;
                    x2 = toNode.x;
                } else {
                    x1 = fromNode.x;
                    x2 = toNode.x + toNode.w;
                }
            }
            y1 = fromY;
            y2 = toY;

            // Cubic bezier for smooth curves
            var dx = Math.abs(x2 - x1) * 0.5;
            var cpx1 = x1 + (x1 < x2 ? dx : -dx);
            var cpx2 = x2 + (x1 < x2 ? -dx : dx);

            var edgeClass = 'er-edge ' + e.confidence;
            var marker = e.confidence === 'proven' ? 'url(#er-arrow-proven)' : 'url(#er-arrow-likely)';

            svg += '<path class="' + edgeClass + '" d="M' + x1 + ',' + y1 + ' C' + cpx1 + ',' + y1 + ' ' + cpx2 + ',' + y2 + ' ' + x2 + ',' + y2 + '" marker-end="' + marker + '" />';

            // Edge label at midpoint
            var midX = (x1 + x2) / 2;
            var midY = (y1 + y2) / 2 - 6;
            svg += '<text class="er-edge-label" x="' + midX + '" y="' + midY + '" text-anchor="middle">'
                + escSvg(e.fromCol) + ' → ' + escSvg(e.toCol)
                + '</text>';
        });

        // Draw nodes
        Object.values(nodes).forEach(function (n) {
            svg += renderERNode(n, edges, centerKey);
        });

        svg += '</svg>';

        // Replace loading with SVG
        $('#erDiagramView .rel-map-loading').remove();
        $('#erDiagramView').prepend(svg);

        // Attach native wheel listener
        erAttachWheel();

        // Fit to view on first render (slight delay for container to get final dimensions)
        setTimeout(function () { erFitToView(svgW, svgH); }, 50);
    }

    function getColumnY(node, colName) {
        var cols = node.schema.columns;
        for (var i = 0; i < Math.min(cols.length, ER_MAX_COLS); i++) {
            if (cols[i].name === colName) {
                return node.y + ER_HEADER_H + (i * ER_COL_H) + ER_COL_H / 2;
            }
        }
        // Column not visible (truncated) — connect to bottom
        return node.y + node.h - 8;
    }

    function renderERNode(node, edges, centerKey) {
        var s = node.schema;
        var x = node.x, y = node.y, w = node.w, h = node.h;
        var isCenter = node.isCenter;

        // Find which columns are FK-highlighted (participate in edges)
        var fkCols = {};
        edges.forEach(function (e) {
            if (e.fromTable === node.key) fkCols[e.fromCol] = true;
            if (e.toTable === node.key) fkCols[e.toCol] = true;
        });

        var headerColor = isCenter ? 'var(--accent)' : '#475569';
        var g = '<g class="er-node' + (isCenter ? ' center' : '') + '" data-table="' + escSvg(node.key) + '">';

        // Background rect
        g += '<rect class="er-node-bg" x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" />';

        // Header background (clip to top rounded corners)
        g += '<clipPath id="clip-' + escSvg(node.key).replace(/\./g, '-') + '">'
            + '<rect x="' + x + '" y="' + y + '" width="' + w + '" height="' + h + '" rx="6" ry="6" />'
            + '</clipPath>';
        g += '<rect class="er-node-header" x="' + x + '" y="' + y + '" width="' + w + '" height="' + ER_HEADER_H + '"'
            + ' fill="' + headerColor + '"'
            + ' clip-path="url(#clip-' + escSvg(node.key).replace(/\./g, '-') + ')" />';

        // Table name
        g += '<text class="er-node-title" x="' + (x + 10) + '" y="' + (y + 17) + '">' + escSvg(s.table) + '</text>';

        // Meta line (rows, columns)
        var rowsLabel = s.estRows ? s.estRows.toLocaleString() + ' rows' : '? rows';
        g += '<text class="er-node-meta" x="' + (x + 10) + '" y="' + (y + 32) + '">'
            + escSvg(s.database) + ' · ' + s.columns.length + ' cols · ' + rowsLabel
            + '</text>';

        // Column rows
        var colCount = Math.min(s.columns.length, ER_MAX_COLS);
        for (var i = 0; i < colCount; i++) {
            var col = s.columns[i];
            var cy = y + ER_HEADER_H + (i * ER_COL_H);
            var isPk = col.key === 'PRI';
            var isFk = fkCols[col.name] || false;

            // Row background (alternate + FK highlight)
            var rowClass = 'er-col-row';
            if (isFk) rowClass += ' fk-highlight-bg';
            else if (i % 2 === 1) rowClass += ' alt';
            g += '<rect class="' + rowClass + '" x="' + x + '" y="' + cy + '" width="' + w + '" height="' + ER_COL_H + '" />';

            // PK icon
            if (isPk) {
                g += '<text x="' + (x + 8) + '" y="' + (cy + 13) + '" font-size="8" fill="' + (isFk ? 'var(--accent)' : 'var(--status-warning)') + '">&#xf084;</text>';
            }

            // Column name
            var nameClass = 'er-col-name' + (isPk ? ' pk' : '') + (isFk ? ' fk-highlight' : '');
            g += '<text class="' + nameClass + '" x="' + (x + (isPk ? 20 : 10)) + '" y="' + (cy + 13) + '">' + escSvg(col.name) + '</text>';

            // Column type (right-aligned)
            var shortType = col.type.replace(/\(\d+\)/g, '').replace(' unsigned', '');
            g += '<text class="er-col-type" x="' + (x + w - 8) + '" y="' + (cy + 13) + '" text-anchor="end">' + escSvg(shortType) + '</text>';
        }

        // Truncation indicator
        if (s.columns.length > ER_MAX_COLS) {
            var truncY = y + ER_HEADER_H + (colCount * ER_COL_H);
            g += '<text class="er-col-type" x="' + (x + w / 2) + '" y="' + (truncY + 12) + '" text-anchor="middle">+'
                + (s.columns.length - ER_MAX_COLS) + ' more columns</text>';
        }

        g += '</g>';
        return g;
    }

    function escSvg(str) {
        if (str === null || str === undefined) return '';
        return String(str).replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    // --- Zoom/Pan ---

    function erApplyTransform() {
        $('#erDiagramSvg').css('transform', 'translate(' + erPanX + 'px,' + erPanY + 'px) scale(' + erZoom + ')');
    }

    function erFitToView(svgW, svgH) {
        var container = $('#erDiagramView');
        var cw = container.width();
        var ch = container.height();
        if (!cw || !ch || !svgW || !svgH) return;

        var scaleX = (cw - 40) / svgW;
        var scaleY = (ch - 40) / svgH;
        erZoom = Math.min(scaleX, scaleY, 1.5);
        erZoom = Math.max(erZoom, 0.1);

        // Center the diagram
        erPanX = (cw - svgW * erZoom) / 2;
        erPanY = (ch - svgH * erZoom) / 2;
        erApplyTransform();
    }

    $(document).on('click', '#erZoomIn', function () {
        erZoom = Math.min(erZoom * 1.25, 3);
        erApplyTransform();
    });

    $(document).on('click', '#erZoomOut', function () {
        erZoom = Math.max(erZoom * 0.8, 0.1);
        erApplyTransform();
    });

    $(document).on('click', '#erZoomFit', function () {
        var svg = $('#erDiagramSvg');
        if (svg.length) erFitToView(parseFloat(svg.attr('width')), parseFloat(svg.attr('height')));
    });

    // Mouse wheel zoom — use native listener with passive:false to allow preventDefault
    function erHandleWheel(e) {
        e.preventDefault();
        var delta = e.deltaY < 0 ? 1.1 : 0.9;
        var newZoom = Math.max(0.05, Math.min(erZoom * delta, 4));

        // Zoom toward cursor position
        var rect = document.getElementById('erDiagramView').getBoundingClientRect();
        var mx = e.clientX - rect.left;
        var my = e.clientY - rect.top;

        erPanX = mx - (mx - erPanX) * (newZoom / erZoom);
        erPanY = my - (my - erPanY) * (newZoom / erZoom);
        erZoom = newZoom;
        erApplyTransform();
    }

    // Attach/detach wheel listener when ER diagram is shown/hidden
    function erAttachWheel() {
        var el = document.getElementById('erDiagramView');
        if (el) el.addEventListener('wheel', erHandleWheel, { passive: false });
    }
    function erDetachWheel() {
        var el = document.getElementById('erDiagramView');
        if (el) el.removeEventListener('wheel', erHandleWheel);
    }

    // Pan via mouse drag
    $(document).on('mousedown', '#erDiagramView', function (e) {
        if ($(e.target).closest('button, .er-controls, .er-legend').length) return;
        erIsPanning = true;
        erPanStartX = e.clientX;
        erPanStartY = e.clientY;
        erPanStartPanX = erPanX;
        erPanStartPanY = erPanY;
        $(this).addClass('grabbing');
        e.preventDefault();
    });

    $(document).on('mousemove', function (e) {
        if (!erIsPanning) return;
        erPanX = erPanStartPanX + (e.clientX - erPanStartX);
        erPanY = erPanStartPanY + (e.clientY - erPanStartY);
        erApplyTransform();
    });

    $(document).on('mouseup mouseleave', function () {
        if (erIsPanning) {
            erIsPanning = false;
            $('#erDiagramView').removeClass('grabbing');
        }
    });

    // Click a node to re-center diagram on that table
    $(document).on('click', '.er-node', function (e) {
        if (erIsPanning) return;
        // Check if we actually moved (drag vs click)
        var tableKey = $(this).data('table') || $(this).closest('[data-table]').data('table');
        if (!tableKey) return;
        var parts = tableKey.split('.');
        if (parts.length !== 2) return;

        // Don't re-center on the current center — instead navigate
        if (tableKey === currentDb + '.' + currentTable) return;

        var db = parts[0];
        var table = parts[1];

        // Re-load diagram centered on clicked table
        currentDb = db;
        currentTable = table;

        // Update sidebar highlight
        $('.db-table-item').removeClass('active');
        $('.db-table-item[data-db="' + db + '"][data-table="' + table + '"]').addClass('active');

        $('#erDiagramView .rel-map-loading').remove();
        $('#erDiagramView').prepend('<div class="rel-map-loading"><i class="fa-solid fa-spinner fa-spin"></i> Recentering...</div>');
        $('#erDiagramSvg').remove();

        $.getJSON('/Database/TableEdges/' + encodeURIComponent(db) + '/' + encodeURIComponent(table),
            function (data) {
                buildERDiagram(data);
            }).fail(function () {
                $('#erDiagramView .rel-map-loading').html('Failed to load diagram');
            });
    });

    // Double-click a node to navigate into table view
    $(document).on('dblclick', '.er-node', function (e) {
        var tableKey = $(this).data('table') || $(this).closest('[data-table]').data('table');
        if (!tableKey) return;
        var parts = tableKey.split('.');
        if (parts.length !== 2) return;
        closeERDiagram();
        loadTable(parts[0], parts[1]);
    });

    // ===================== DEEP EXPORT SQL =====================

    var exportPreviewData = null; // cached preview response

    $(document).on('click', '#btnExportSql', function () {
        if (!selectedRowPks || !schema) return;
        showExportSqlModal();
    });

    function showExportSqlModal() {
        $('#exportSqlModal').remove();
        exportPreviewData = null;

        var pkDesc = selectedRowPks.columns.map(function (c, i) {
            return c + ' = ' + selectedRowPks.values[i];
        }).join(', ');

        var html = '<div id="exportSqlModal" class="db-modal-overlay">'
            + '<div class="db-modal">'
            + '<div class="db-modal-header">'
            + '<span><i class="fa-solid fa-file-export" style="color: var(--accent);"></i> Export SQL</span>'
            + '<button class="close-btn" id="btnCloseExportModal"><i class="fa-solid fa-xmark"></i></button>'
            + '</div>'
            + '<div class="db-modal-body">'
            + '<div class="export-source-info">'
            + '<span class="col-name">' + esc(currentDb) + '.' + esc(currentTable) + '</span>'
            + ' &mdash; ' + esc(pkDesc)
            + '</div>'
            + '<div class="export-description">Exports this row and all rows from related tables that belong to it '
            + '(translations, scripts, quest givers, loot entries, etc). '
            + 'Referenced entities like items or creatures are assumed to already exist on the target server.</div>'
            + '<div id="exportPreview" class="export-preview">'
            + '<div class="rel-loading"><i class="fa-solid fa-spinner fa-spin"></i> Scanning related tables...</div>'
            + '</div>'
            + '<div class="export-option-row">'
            + '<label>Insert mode</label>'
            + '<select id="exportInsertMode">'
            + '<option value="INSERT IGNORE" selected>INSERT IGNORE (skip existing)</option>'
            + '<option value="REPLACE">REPLACE (overwrite existing)</option>'
            + '</select>'
            + '</div>'
            + '<div class="export-actions">'
            + '<button class="btn-accent" id="btnDoExportSql" disabled>'
            + '<i class="fa-solid fa-download"></i> Download .sql'
            + '</button>'
            + '</div>'
            + '</div>'
            + '</div>'
            + '</div>';

        $('body').append(html);
        loadExportPreview();
    }

    function loadExportPreview() {
        $('#exportPreview').html('<div class="rel-loading"><i class="fa-solid fa-spinner fa-spin"></i> Scanning related tables...</div>');
        $('#btnDoExportSql').prop('disabled', true);

        $.ajax({
            url: '/Database/ExportSqlPreview',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({
                db: currentDb,
                table: currentTable,
                pkColumns: selectedRowPks.columns,
                pkValues: selectedRowPks.values.map(String)
            }),
            success: function (data) {
                exportPreviewData = data;
                renderExportPreview(data);
            },
            error: function (xhr) {
                var msg = 'Preview failed';
                try { msg = JSON.parse(xhr.responseText).error || msg; } catch (e) { }
                $('#exportPreview').html('<div class="rel-loading" style="color: var(--status-error);">'
                    + '<i class="fa-solid fa-exclamation-triangle"></i> ' + esc(msg) + '</div>');
            }
        });
    }

    function renderExportPreview(data) {
        var html = '<div class="export-preview-summary">'
            + '<span class="export-stat"><i class="fa-solid fa-table"></i> <span id="exportTableCount">'
            + data.totalTables + '</span> tables</span>'
            + '<span class="export-stat"><i class="fa-solid fa-bars"></i> <span id="exportRowCount">'
            + data.totalRows.toLocaleString() + '</span> rows</span>'
            + '</div>';

        html += '<div class="export-table-list">';
        data.tables.forEach(function (t) {
            var isSource = t.db === currentDb && t.table === currentTable;
            var tableKey = t.db + '.' + t.table;
            html += '<div class="export-table-item' + (isSource ? ' source' : '') + '">'
                + '<label class="export-table-check">'
                + '<input type="checkbox" class="export-table-cb" data-key="' + esc(tableKey) + '" '
                + 'data-rows="' + t.rowCount + '" checked'
                + (isSource ? ' disabled' : '') + ' />'
                + '<span class="export-table-name">'
                + (isSource ? '<i class="fa-solid fa-bullseye" style="color: var(--accent); font-size: 9px; margin-right: 4px;"></i>' : '')
                + esc(t.db) + '.<strong>' + esc(t.table) + '</strong></span>'
                + '</label>'
                + '<span class="export-table-count">' + t.rowCount.toLocaleString() + '</span>'
                + '</div>';
        });
        html += '</div>';

        $('#exportPreview').html(html);
        $('#btnDoExportSql').prop('disabled', false);
    }

    // Update counts when checkboxes change
    $(document).on('change', '.export-table-cb', function () {
        if (!exportPreviewData) return;
        var totalTables = 0;
        var totalRows = 0;
        $('.export-table-cb').each(function () {
            if ($(this).is(':checked')) {
                totalTables++;
                totalRows += parseInt($(this).data('rows')) || 0;
            }
        });
        $('#exportTableCount').text(totalTables);
        $('#exportRowCount').text(totalRows.toLocaleString());
    });

    // Download SQL
    $(document).on('click', '#btnDoExportSql', function () {
        if (!selectedRowPks) return;

        var btn = $(this);
        btn.prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Generating...');

        var insertMode = $('#exportInsertMode').val() || 'INSERT IGNORE';

        // Collect unchecked tables as exclusions
        var excludeTables = [];
        $('.export-table-cb:not(:checked)').each(function () {
            excludeTables.push($(this).data('key'));
        });

        fetch('/Database/ExportSql', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                db: currentDb,
                table: currentTable,
                pkColumns: selectedRowPks.columns,
                pkValues: selectedRowPks.values.map(String),
                insertMode: insertMode,
                excludeTables: excludeTables
            })
        }).then(function (resp) {
            if (!resp.ok) throw new Error('Export failed');
            var cd = resp.headers.get('Content-Disposition') || '';
            var match = cd.match(/filename[^;=\n]*=((['"]).*?\2|[^;\n]*)/);
            var fileName = match ? match[1].replace(/['"]/g, '') : 'export.sql';
            return resp.blob().then(function (blob) { return { blob: blob, fileName: fileName }; });
        }).then(function (result) {
            var url = URL.createObjectURL(result.blob);
            var a = document.createElement('a');
            a.href = url;
            a.download = result.fileName;
            document.body.appendChild(a);
            a.click();
            document.body.removeChild(a);
            URL.revokeObjectURL(url);

            btn.prop('disabled', false).html('<i class="fa-solid fa-download"></i> Download .sql');
            showToast('SQL export downloaded', 'success');
        }).catch(function (err) {
            btn.prop('disabled', false).html('<i class="fa-solid fa-download"></i> Download .sql');
            showToast('Export failed: ' + err.message, 'error');
        });
    });

    // Close modal
    $(document).on('click', '#btnCloseExportModal', function () {
        $('#exportSqlModal').remove();
    });
    $(document).on('click', '.db-modal-overlay', function (e) {
        if ($(e.target).hasClass('db-modal-overlay')) {
            $('#exportSqlModal').remove();
        }
    });

    // ===================== BREADCRUMB NAVIGATION =====================

    $(document).on('click', '#btnGoBack', function () {
        if (breadcrumbs.length <= 1) return;
        var prevCrumb = breadcrumbs[breadcrumbs.length - 2];
        breadcrumbs = breadcrumbs.slice(0, breadcrumbs.length - 2);
        loadTable(prevCrumb.db, prevCrumb.table, prevCrumb.filterCol, prevCrumb.filterVal);
    });

    $(document).on('click', '.db-breadcrumb:not(.current)', function () {
        var idx = parseInt($(this).data('idx'));
        var crumb = breadcrumbs[idx];
        // Trim breadcrumbs to this point
        breadcrumbs = breadcrumbs.slice(0, idx);
        loadTable(crumb.db, crumb.table, crumb.filterCol, crumb.filterVal);
    });

    // ===================== PAGINATION =====================

    $(document).on('click', '#btnPageFirst', function () { currentPage = 1; loadData(); });
    $(document).on('click', '#btnPagePrev', function () { if (currentPage > 1) { currentPage--; loadData(); } });
    $(document).on('click', '#btnPageNext', function () { currentPage++; loadData(); });
    $(document).on('click', '#btnPageLast', function () {
        currentPage = lastTotalPages;
        loadData();
    });
    $(document).on('keydown', '#dbPageJump', function (e) {
        if (e.key === 'Enter') {
            var p = Math.max(1, Math.min(parseInt($(this).val()) || 1, lastTotalPages));
            currentPage = p;
            loadData();
        }
    });
    $(document).on('blur', '#dbPageJump', function () {
        var p = Math.max(1, Math.min(parseInt($(this).val()) || 1, lastTotalPages));
        if (p !== currentPage) { currentPage = p; loadData(); }
    });
    $(document).on('change', '#dbPageSize', function () {
        pageSize = parseInt($(this).val()) || 50;
        try { localStorage.setItem('msui_db_pageSize', pageSize); } catch (e) { }
        currentPage = 1;
        loadData();
    });

    // ===================== INSERT ROW =====================

    $(document).on('click', '#btnAddRow', function () {
        showInsertForm = !showInsertForm;
        // Re-render the grid with/without insert form
        loadData();
    });

    $(document).on('click', '#btnInsertCancel', function () {
        showInsertForm = false;
        loadData();
    });

    $(document).on('click', '#btnInsertSave', function () {
        if (!schema) return;
        var values = {};
        var hasAny = false;
        $('.insert-field').each(function () {
            var col = $(this).data('col');
            var val = $(this).val().trim();
            if (val !== '') {
                values[col] = val;
                hasAny = true;
            }
        });

        if (!hasAny) {
            showToast('Enter at least one value', 'error');
            return;
        }

        $('#btnInsertSave').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Inserting...');

        $.ajax({
            url: '/Database/Insert',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({
                db: currentDb,
                table: currentTable,
                values: values
            }),
            success: function (result) {
                if (result.success) {
                    showToast('Row inserted', 'success');
                    showInsertForm = false;
                    loadData();
                } else {
                    showToast('Insert failed: ' + (result.error || 'Unknown'), 'error');
                    $('#btnInsertSave').prop('disabled', false).html('<i class="fa-solid fa-plus"></i> Insert');
                }
            },
            error: function (xhr) {
                var msg = 'Insert failed — server error';
                try { msg = JSON.parse(xhr.responseText).error || msg; } catch (e) { }
                showToast(msg, 'error');
                $('#btnInsertSave').prop('disabled', false).html('<i class="fa-solid fa-plus"></i> Insert');
            }
        });
    });

    // ===================== CLONE ROW =====================

    $(document).on('click', '#btnCloneRow', function () {
        if (!selectedRowData || !schema) return;
        showInsertForm = true;
        // Re-render grid, then pre-fill insert form with selected row's data
        loadData();
        // After render, fill fields
        setTimeout(function () {
            $('.insert-field').each(function () {
                var col = $(this).data('col');
                if (selectedRowData[col] !== null && selectedRowData[col] !== undefined) {
                    $(this).val(selectedRowData[col]);
                }
            });
        }, 50);
    });

    // ===================== DELETE ROW =====================

    $(document).on('click', '#btnDeleteRow', function () {
        if (!selectedRowPks || !schema) return;

        var desc = selectedRowPks.columns.map(function (c, i) {
            return c + '=' + selectedRowPks.values[i];
        }).join(', ');

        if (!confirm('Delete row from ' + currentDb + '.' + currentTable + ' where ' + desc + '?\n\nThis cannot be undone.')) return;

        $.ajax({
            url: '/Database/Delete',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify({
                db: currentDb,
                table: currentTable,
                pkColumns: selectedRowPks.columns,
                pkValues: selectedRowPks.values.map(String)
            }),
            success: function (result) {
                if (result.success) {
                    showToast('Row deleted', 'success');
                    selectedRowPks = null;
                    selectedRowData = null;
                    loadData();
                } else {
                    showToast('Delete failed: ' + (result.error || 'Unknown'), 'error');
                }
            },
            error: function (xhr) {
                var msg = 'Delete failed — server error';
                try { msg = JSON.parse(xhr.responseText).error || msg; } catch (e) { }
                showToast(msg, 'error');
            }
        });
    });

    // ===================== COLUMN NAME TOGGLE =====================

    $(document).on('click', '#btnToggleNames', function () {
        showHumanNames = !showHumanNames;
        var grid = $('.db-grid');
        if (showHumanNames) grid.addClass('show-human');
        else grid.removeClass('show-human');
    });

    // Double-click a human name to edit it
    $(document).on('dblclick', '.db-grid th .human-name', function (e) {
        e.stopPropagation(); // Don't trigger sort
        var th = $(this).closest('th');
        var col = th.data('col');
        var current = getHumanName(currentDb, currentTable, col) || col;
        var newName = prompt('Human-readable name for "' + col + '":', current);
        if (newName !== null && newName.trim() !== '') {
            setHumanName(currentDb, currentTable, col, newName.trim());
            $(this).text(newName.trim());
        }
    });

    function getHumanName(db, table, col) {
        return humanNames[db + '.' + table + '.' + col] || null;
    }

    function setHumanName(db, table, col, name) {
        humanNames[db + '.' + table + '.' + col] = name;
        try { localStorage.setItem('msui_db_humanNames', JSON.stringify(humanNames)); } catch (e) { }
    }

    // ===================== SEARCH =====================

    $(document).on('click', '#btnSearch', function () { doSearch(); });
    $(document).on('keydown', '#searchVal', function (e) { if (e.key === 'Enter') doSearch(); });

    $(document).on('click', '#btnClearSearch', function () {
        currentFilterCol = null;
        currentFilterVal = null;
        currentPage = 1;
        // Update breadcrumb label
        if (breadcrumbs.length > 0) {
            breadcrumbs[breadcrumbs.length - 1].filterCol = null;
            breadcrumbs[breadcrumbs.length - 1].filterVal = null;
            breadcrumbs[breadcrumbs.length - 1].label = currentTable;
        }
        renderToolbar();
        loadData();
    });

    function doSearch() {
        var col = $('#searchCol').val();
        var val = $('#searchVal').val().trim();
        if (!val) return;

        currentFilterCol = col || null;
        currentFilterVal = val;
        currentPage = 1;

        // Update breadcrumb label
        if (breadcrumbs.length > 0) {
            var label = currentTable;
            if (col) label += ' (' + col + '=' + val + ')';
            else label += ' (search: ' + val + ')';
            breadcrumbs[breadcrumbs.length - 1].filterCol = currentFilterCol;
            breadcrumbs[breadcrumbs.length - 1].filterVal = currentFilterVal;
            breadcrumbs[breadcrumbs.length - 1].label = label;
        }
        renderToolbar();
        loadData();
    }

    // ===================== CSV EXPORT =====================

    $(document).on('click', '#btnExportCsv', function () {
        if (!currentDb || !currentTable) return;

        // Build export URL with current filter params
        var url = '/Database/Export/' + encodeURIComponent(currentDb) + '/' + encodeURIComponent(currentTable);
        var params = [];
        if (currentFilterCol) params.push('filterCol=' + encodeURIComponent(currentFilterCol));
        if (currentFilterVal) params.push('filterVal=' + encodeURIComponent(currentFilterVal));
        if (params.length > 0) url += '?' + params.join('&');

        // Trigger download via hidden link
        var a = document.createElement('a');
        a.href = url;
        a.download = '';
        document.body.appendChild(a);
        a.click();
        document.body.removeChild(a);

        showToast('Exporting ' + currentDb + '.' + currentTable + '...', 'info');
    });

    // ===================== UTILITIES =====================

    function esc(str) {
        if (str === null || str === undefined) return '';
        var div = document.createElement('div');
        div.appendChild(document.createTextNode(String(str)));
        return div.innerHTML;
    }

    function showToast(msg, type) {
        var el = $('<div class="db-toast ' + (type || 'info') + '">' + esc(msg) + '</div>');
        $('body').append(el);
        setTimeout(function () { el.fadeOut(300, function () { el.remove(); }); }, 3000);
    }

    // ===================== COLUMN RESIZE =====================

    var colWidths = {}; // { "db.table": { "col1": 180, "col2": 300 } }
    try { colWidths = JSON.parse(localStorage.getItem('msui_db_colWidths') || '{}'); } catch (e) { }

    function colWidthKey() {
        return currentDb + '.' + currentTable;
    }

    function applyColumnWidths() {
        var table = $('.db-grid');
        if (!table.length) return;

        var saved = colWidths[colWidthKey()] || {};
        var hasSaved = Object.keys(saved).length > 0;

        // Switch to fixed layout only when we have saved widths
        if (hasSaved) {
            table.css('table-layout', 'fixed');
            table.find('thead th[data-col]').each(function () {
                var col = $(this).data('col');
                if (saved[col]) {
                    $(this).css('width', saved[col] + 'px');
                }
            });
        } else {
            table.css('table-layout', 'auto');
        }
    }

    function saveColumnWidths() {
        var key = colWidthKey();
        var widths = {};
        $('.db-grid thead th[data-col]').each(function () {
            widths[$(this).data('col')] = $(this).outerWidth();
        });
        colWidths[key] = widths;
        try { localStorage.setItem('msui_db_colWidths', JSON.stringify(colWidths)); } catch (e) { }
    }

    // Drag-to-resize via mousedown on the resize handle
    (function () {
        var resizing = false;
        var resizeTh = null;
        var startX = 0;
        var startW = 0;

        $(document).on('mousedown', '.col-resize-handle', function (e) {
            e.preventDefault();
            e.stopPropagation(); // Don't trigger sort
            resizing = true;
            resizeTh = $(this).closest('th');
            startX = e.pageX;
            startW = resizeTh.outerWidth();

            // Snapshot all current column widths and switch to fixed layout
            var table = $('.db-grid');
            table.css('table-layout', 'fixed');
            table.find('thead th[data-col]').each(function () {
                $(this).css('width', $(this).outerWidth() + 'px');
            });
            // Also set the row-number column
            table.find('thead th:first-child').css('width', table.find('thead th:first-child').outerWidth() + 'px');

            $('body').addClass('col-resizing');
        });

        $(document).on('mousemove', function (e) {
            if (!resizing || !resizeTh) return;
            var diff = e.pageX - startX;
            var newW = Math.max(40, startW + diff);
            resizeTh.css('width', newW + 'px');
        });

        $(document).on('mouseup', function () {
            if (!resizing) return;
            resizing = false;
            resizeTh = null;
            $('body').removeClass('col-resizing');
            saveColumnWidths();
            _justResized = true;
            setTimeout(function () { _justResized = false; }, 50);
        });
    })();

    // ===================== INIT =====================

    loadTableList();

});
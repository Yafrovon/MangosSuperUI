// MangosSuperUI — World Map (worldmap.js)
// Leaflet.js map using extracted minimap tiles with WoW coordinate system.
// Each tile is placed as an L.imageOverlay at its exact grid position.

(function () {
    'use strict';

    // ── WoW tile/coordinate constants ──
    var TILE_PX = 256;
    var TILE_YARDS = 533.33333;

    // Map key → metadata
    var MAP_DEFS = {
        'Azeroth': { folder: 'Azeroth', mapId: 0, label: 'Eastern Kingdoms' },
        'Kalimdor': { folder: 'Kalimdor', mapId: 1, label: 'Kalimdor' }
    };

    // ── State ──
    var map = null;
    var currentMapKey = 'Azeroth';
    var tileOverlays = [];
    var tileIndex = [];
    var spawnLayer = null;
    var clickMarker = null;
    var clickWorldCoords = null;
    var selectedEntry = null;
    var catalog = [];
    var heightAvailable = false;
    var selectedOrientation = 0; // radians
    var selectedOrientDir = 'N';

    // WoW orientation: 0 = north (+X), counter-clockwise
    // Compass directions → radians
    var DIR_RADIANS = {
        'N': 0,
        'NW': Math.PI / 4,
        'W': Math.PI / 2,
        'SW': 3 * Math.PI / 4,
        'S': Math.PI,
        'SE': 5 * Math.PI / 4,
        'E': 3 * Math.PI / 2,
        'NE': 7 * Math.PI / 4
    };

    // CSS needle rotation: 0deg = pointing up (N). Clockwise rotation for visual.
    var DIR_NEEDLE_DEG = {
        'N': 0,
        'NE': 45,
        'E': 90,
        'SE': 135,
        'S': 180,
        'SW': 225,
        'W': 270,
        'NW': 315
    };

    // ── Init ──
    $(document).ready(function () {
        initMap();
        loadAvailableMaps();
        loadCatalog();
        bindEvents();
    });

    // ══════════════════════════════════════════════════════════
    //  MAP SETUP
    // ══════════════════════════════════════════════════════════

    function initMap() {
        map = L.map('worldmap', {
            crs: L.CRS.Simple,
            minZoom: -4,
            maxZoom: 3,
            zoomSnap: 0.5,
            zoomDelta: 0.5,
            attributionControl: false
        });

        document.getElementById('worldmap').style.background = '#0a0e14';

        spawnLayer = L.layerGroup().addTo(map);

        map.on('mousemove', function (e) {
            var w = latLngToWorld(e.latlng);
            $('#coordX').text(w.x.toFixed(1));
            $('#coordY').text(w.y.toFixed(1));
            var t = worldToTile(w.x, w.y);
            $('#coordTile').text('map' + zeroPad(t.row) + '_' + zeroPad(t.col));
        });

        map.on('click', function (e) {
            var w = latLngToWorld(e.latlng);
            setClickLocation(w.x, w.y, e.latlng);
        });

        switchMap('Azeroth');
    }

    function switchMap(mapKey) {
        currentMapKey = mapKey;
        var def = MAP_DEFS[mapKey];
        if (!def) return;

        $('.wm-map-btn').removeClass('active');
        $('.wm-map-btn').filter(function () {
            return $(this).data('map') === mapKey;
        }).addClass('active');

        $.getJSON('/WorldMap/TileIndex?map=' + encodeURIComponent(def.folder), function (data) {
            tileIndex = (data.tiles || []).map(function (t) { return { row: t[0], col: t[1] }; });
            buildTileOverlays(def.folder);
            fitToTiles();
            refreshSpawns();
        });
    }

    function buildTileOverlays(folder) {
        tileOverlays.forEach(function (ov) { map.removeLayer(ov); });
        tileOverlays = [];

        tileIndex.forEach(function (t) {
            var bounds = L.latLngBounds(
                L.latLng(-(t.col + 1) * TILE_PX, t.row * TILE_PX),
                L.latLng(-t.col * TILE_PX, (t.row + 1) * TILE_PX)
            );

            var url = '/minimap/' + folder + '/map' + zeroPad(t.row) + '_' + zeroPad(t.col) + '.png';

            var overlay = L.imageOverlay(url, bounds, {
                opacity: 1,
                interactive: false
            }).addTo(map);

            tileOverlays.push(overlay);
        });
    }

    function fitToTiles() {
        if (tileIndex.length === 0) return;

        var minRow = 999, maxRow = -1, minCol = 999, maxCol = -1;
        tileIndex.forEach(function (t) {
            if (t.row < minRow) minRow = t.row;
            if (t.row > maxRow) maxRow = t.row;
            if (t.col < minCol) minCol = t.col;
            if (t.col > maxCol) maxCol = t.col;
        });

        var sw = L.latLng(-(maxCol + 1) * TILE_PX, minRow * TILE_PX);
        var ne = L.latLng(-minCol * TILE_PX, (maxRow + 1) * TILE_PX);
        map.fitBounds(L.latLngBounds(sw, ne), { padding: [20, 20] });
    }

    // ══════════════════════════════════════════════════════════
    //  COORDINATE CONVERSION
    // ══════════════════════════════════════════════════════════

    function worldToLatLng(worldX, worldY) {
        var colF = 32 - (worldX / TILE_YARDS);
        var rowF = 32 - (worldY / TILE_YARDS);
        return L.latLng(-colF * TILE_PX, rowF * TILE_PX);
    }

    function latLngToWorld(latlng) {
        var rowF = latlng.lng / TILE_PX;
        var colF = -latlng.lat / TILE_PX;
        return {
            x: (32 - colF) * TILE_YARDS,
            y: (32 - rowF) * TILE_YARDS
        };
    }

    function worldToTile(worldX, worldY) {
        return {
            row: 32 - Math.ceil(worldY / TILE_YARDS),
            col: 32 - Math.ceil(worldX / TILE_YARDS)
        };
    }

    function zeroPad(n) {
        return n < 10 ? '0' + n : '' + n;
    }

    // ══════════════════════════════════════════════════════════
    //  HEIGHT LOOKUP
    // ══════════════════════════════════════════════════════════

    function resolveHeight(mapId, worldX, worldY, callback) {
        $.getJSON('/WorldMap/GetHeight', { map: mapId, x: worldX, y: worldY }, function (data) {
            if (data.z !== null && data.z !== undefined) {
                heightAvailable = true;
                callback(data.z);
            } else {
                callback(null);
            }
        }).fail(function () {
            callback(null);
        });
    }

    // ══════════════════════════════════════════════════════════
    //  CLICK TO PLACE
    // ══════════════════════════════════════════════════════════

    function setClickLocation(worldX, worldY, latlng) {
        var mapId = MAP_DEFS[currentMapKey].mapId;

        clickWorldCoords = {
            x: worldX,
            y: worldY,
            z: 0,
            map: mapId
        };

        // Show immediately with Z pending
        updateClickDisplay(worldX, worldY, null);

        if (clickMarker) {
            clickMarker.setLatLng(latlng);
        } else {
            clickMarker = L.circleMarker(latlng, {
                radius: 8,
                color: '#3b82c4',
                fillColor: '#3b82c4',
                fillOpacity: 0.7,
                weight: 2
            }).addTo(map);
        }

        updatePlaceButton();

        // Resolve Z from heightmap
        if (mapId >= 0) {
            resolveHeight(mapId, worldX, worldY, function (z) {
                if (clickWorldCoords &&
                    Math.abs(clickWorldCoords.x - worldX) < 0.01 &&
                    Math.abs(clickWorldCoords.y - worldY) < 0.01) {

                    clickWorldCoords.z = (z !== null) ? z : 0;
                    updateClickDisplay(worldX, worldY, z);
                }
            });
        }
    }

    function updateClickDisplay(worldX, worldY, z) {
        var zText;
        if (z === null || z === undefined) {
            zText = '<span class="wm-z-pending">resolving…</span>';
        } else {
            zText = '<strong>' + z.toFixed(1) + '</strong>';
        }

        $('#clickCoords').html(
            'X: <strong>' + worldX.toFixed(1) + '</strong> &nbsp; ' +
            'Y: <strong>' + worldY.toFixed(1) + '</strong> &nbsp; ' +
            'Z: ' + zText + '<br>' +
            'Map: <strong>' + escHtml(MAP_DEFS[currentMapKey].label || currentMapKey) + '</strong> (' + MAP_DEFS[currentMapKey].mapId + ')'
        );
        $('#clickInfo .wm-click-label').text('Placement coordinates');
    }

    function updatePlaceButton() {
        var canPlace = clickWorldCoords !== null && selectedEntry !== null;
        $('#btnPlace').prop('disabled', !canPlace);

        if (canPlace) {
            var obj = catalog.find(function (c) { return c.entry === selectedEntry; });
            $('#btnPlace').html(
                '<i class="fa-solid fa-map-pin"></i> Place "' +
                escHtml(obj ? obj.name : '#' + selectedEntry) + '"'
            );
        } else {
            $('#btnPlace').html('<i class="fa-solid fa-map-pin"></i> Place Object');
        }
    }

    function doPlace() {
        if (!clickWorldCoords || !selectedEntry) return;

        var payload = {
            entry: selectedEntry,
            map: clickWorldCoords.map,
            x: clickWorldCoords.x,
            y: clickWorldCoords.y,
            z: clickWorldCoords.z,
            orientation: selectedOrientation
        };

        $('#btnPlace').prop('disabled', true).text('Placing...');

        $.ajax({
            url: '/WorldMap/PlaceObject',
            method: 'POST',
            contentType: 'application/json',
            data: JSON.stringify(payload),
            success: function (res) {
                if (res.success) {
                    // Use the server-resolved Z (may differ if client sent 0 and server resolved it)
                    var finalZ = (res.z !== undefined && res.z !== null) ? res.z : payload.z;

                    addSpawnMarker({
                        guid: res.guid,
                        entry: payload.entry,
                        name: catalog.find(function (c) { return c.entry === payload.entry; })?.name || '?',
                        type: 0,
                        x: payload.x,
                        y: payload.y,
                        z: finalZ
                    }, true);

                    // Update the click display with the final Z
                    updateClickDisplay(payload.x, payload.y, finalZ);

                    updatePlaceButton();
                } else {
                    alert('Place failed: ' + (res.error || 'unknown'));
                }
            },
            error: function (xhr) {
                alert('Error: ' + (xhr.responseJSON?.error || xhr.statusText));
                updatePlaceButton();
            }
        });
    }

    // ══════════════════════════════════════════════════════════
    //  SPAWN OVERLAY
    // ══════════════════════════════════════════════════════════

    function refreshSpawns() {
        spawnLayer.clearLayers();

        var showCustom = $('#chkShowCustom').is(':checked');
        var showBase = $('#chkShowBase').is(':checked');
        if (!showCustom && !showBase) return;

        var def = MAP_DEFS[currentMapKey];
        if (!def || def.mapId < 0) return;

        var url = '/WorldMap/Spawns?map=' + def.mapId;
        if (showCustom && !showBase) url += '&customOnly=true';

        $.getJSON(url, function (data) {
            (data.spawns || []).forEach(function (s) {
                var isCustom = s.entry >= 900000;
                if (!showCustom && isCustom) return;
                if (!showBase && !isCustom) return;
                addSpawnMarker(s, isCustom);
            });
        });
    }

    function addSpawnMarker(spawn, isCustom) {
        var latlng = worldToLatLng(spawn.x, spawn.y);
        var color = isCustom ? '#3b82c4' : '#6b7280';

        var marker = L.circleMarker(latlng, {
            radius: isCustom ? 5 : 3,
            color: color,
            fillColor: color,
            fillOpacity: 0.8,
            weight: 1
        });

        var popupHtml =
            '<div class="wm-popup-title">' + escHtml(spawn.name || 'Unknown') + '</div>' +
            '<div class="wm-popup-detail">' +
            'Entry: ' + spawn.entry + ' &nbsp;|&nbsp; GUID: ' + spawn.guid + '<br>' +
            'Pos: ' + Number(spawn.x).toFixed(1) + ', ' + Number(spawn.y).toFixed(1) + ', ' + Number(spawn.z).toFixed(1) +
            '</div>';

        if (isCustom) {
            var teleCmd = '.tele ' + Number(spawn.x).toFixed(1) + ' ' + Number(spawn.y).toFixed(1) + ' ' + Number(spawn.z).toFixed(1) + ' ' + MAP_DEFS[currentMapKey].mapId;
            popupHtml +=
                '<div class="wm-popup-actions">' +
                '<button onclick="WM.copyText(\'' + teleCmd.replace(/'/g, "\\'") + '\')">Copy .tele</button>' +
                '<button class="danger" onclick="WM.deleteSpawn(' + spawn.guid + ')">Delete</button>' +
                '</div>';
        }

        marker.bindPopup(popupHtml, { maxWidth: 280 });

        if (isCustom) {
            marker.bindTooltip(escHtml(spawn.name || '?'), {
                className: 'wm-spawn-label custom',
                direction: 'top',
                offset: [0, -6]
            });
        }

        marker.addTo(spawnLayer);
    }

    // ══════════════════════════════════════════════════════════
    //  CATALOG
    // ══════════════════════════════════════════════════════════

    function loadCatalog() {
        $.getJSON('/WorldMap/Catalog', function (data) {
            catalog = data.objects || [];
            renderCatalog(catalog);
        });
    }

    function renderCatalog(items) {
        var $list = $('#catalogList');
        $list.empty();

        if (items.length === 0) {
            $list.append('<li style="padding:10px;color:var(--text-muted);font-size:12.5px;">No custom objects found</li>');
            return;
        }

        items.forEach(function (obj) {
            var isSelected = obj.entry === selectedEntry;
            var $li = $('<li class="wm-catalog-item' + (isSelected ? ' selected' : '') + '">')
                .attr('data-entry', obj.entry)
                .append('<span class="wm-cat-entry">#' + obj.entry + '</span>')
                .append('<span class="wm-cat-name">' + escHtml(obj.name) + '</span>')
                .append('<span class="wm-cat-type">T' + obj.type + '</span>');

            $li.on('click', function () {
                selectedEntry = obj.entry;
                $('.wm-catalog-item').removeClass('selected');
                $li.addClass('selected');
                updatePlaceButton();
            });

            $list.append($li);
        });
    }

    // ══════════════════════════════════════════════════════════
    //  MAP SELECTOR
    // ══════════════════════════════════════════════════════════

    function loadAvailableMaps() {
        $.getJSON('/WorldMap/AvailableMaps', function (data) {
            var $sel = $('#mapSelector');
            $sel.empty();

            var maps = data.maps || [];
            var priority = ['Azeroth', 'Kalimdor'];

            maps.sort(function (a, b) {
                var ai = priority.indexOf(a.name);
                var bi = priority.indexOf(b.name);
                if (ai >= 0 && bi >= 0) return ai - bi;
                if (ai >= 0) return -1;
                if (bi >= 0) return 1;
                return a.name.localeCompare(b.name);
            });

            maps.forEach(function (m) {
                if (!MAP_DEFS[m.name]) {
                    MAP_DEFS[m.name] = { folder: m.name, mapId: -1, label: m.name };
                }

                var active = m.name === currentMapKey ? ' active' : '';
                var $btn = $('<button class="wm-map-btn' + active + '">')
                    .data('map', m.name)
                    .html(escHtml(MAP_DEFS[m.name].label || m.name) +
                        ' <span style="opacity:0.5;font-size:11px;">(' + m.tileCount + ')</span>');

                $btn.on('click', function () { switchMap(m.name); });
                $sel.append($btn);
            });
        });
    }

    // ══════════════════════════════════════════════════════════
    //  EVENTS
    // ══════════════════════════════════════════════════════════

    function bindEvents() {
        $('#btnPlace').on('click', doPlace);
        $('#chkShowCustom, #chkShowBase').on('change', refreshSpawns);

        $('#catalogSearch').on('input', function () {
            var q = $(this).val().toLowerCase();
            if (!q) { renderCatalog(catalog); return; }
            var filtered = catalog.filter(function (obj) {
                return obj.name.toLowerCase().indexOf(q) >= 0 ||
                    String(obj.entry).indexOf(q) >= 0;
            });
            renderCatalog(filtered);
        });

        // Compass orientation buttons
        $('.wm-compass-btn').on('click', function () {
            var dir = $(this).attr('data-dir');
            if (!dir || DIR_RADIANS[dir] === undefined) return;

            selectedOrientDir = dir;
            selectedOrientation = DIR_RADIANS[dir];

            // Update button selection
            $('.wm-compass-btn').removeClass('selected');
            $(this).addClass('selected');

            // Update needle
            var $needle = $('#compassNeedle');
            $needle.addClass('visible');
            $needle.css('transform', 'translate(-50%, -100%) rotate(' + DIR_NEEDLE_DEG[dir] + 'deg)');

            // Update label
            $('#orientValue').text(dir + ' — ' + selectedOrientation.toFixed(2) + ' rad');
        });

        // Init needle to N
        $('#compassNeedle').addClass('visible').css('transform', 'translate(-50%, -100%) rotate(0deg)');

        // ── 3D Explore: drag horse onto map to enter World Viewer ──
        initExploreButton();
    }

    // ══════════════════════════════════════════════════════════
    //  3D EXPLORE — drag horse onto map to enter World Viewer
    // ══════════════════════════════════════════════════════════

    function initExploreButton() {
        var mapContainer = document.getElementById('worldmap');
        if (!mapContainer) return;

        // Create the horse button
        var btn = document.createElement('div');
        btn.id = 'exploreBtn';
        btn.innerHTML = '<i class="fa-solid fa-horse"></i>';
        btn.title = 'Drag onto map to explore in 3D';
        btn.style.cssText =
            'position:absolute;bottom:12px;right:12px;z-index:1000;' +
            'width:40px;height:40px;border-radius:8px;' +
            'background:rgba(20,24,32,0.92);border:1px solid rgba(255,255,255,0.15);' +
            'color:#e8a840;font-size:18px;' +
            'display:flex;align-items:center;justify-content:center;' +
            'cursor:grab;user-select:none;transition:transform 0.15s, box-shadow 0.15s;';
        mapContainer.appendChild(btn);

        // Tooltip label (shown during drag)
        var label = document.createElement('div');
        label.id = 'exploreLabel';
        label.textContent = 'Drop to explore in 3D';
        label.style.cssText =
            'position:fixed;z-index:10001;pointer-events:none;display:none;' +
            'background:rgba(20,24,32,0.95);color:#e8a840;' +
            'padding:4px 10px;border-radius:6px;font-size:12px;font-weight:600;' +
            'white-space:nowrap;border:1px solid rgba(232,168,64,0.3);';
        document.body.appendChild(label);

        // Ghost icon that follows the cursor during drag
        var ghost = document.createElement('div');
        ghost.innerHTML = '<i class="fa-solid fa-horse"></i>';
        ghost.style.cssText =
            'position:fixed;z-index:10000;pointer-events:none;display:none;' +
            'width:36px;height:36px;border-radius:50%;' +
            'background:rgba(232,168,64,0.9);color:#1a1e28;font-size:18px;' +
            'display:none;align-items:center;justify-content:center;' +
            'box-shadow:0 2px 12px rgba(232,168,64,0.5);';
        document.body.appendChild(ghost);

        var dragging = false;
        var startX, startY;
        var DRAG_THRESHOLD = 6;
        var dragStarted = false;

        btn.addEventListener('pointerdown', function (e) {
            e.preventDefault();
            e.stopPropagation();
            dragging = true;
            dragStarted = false;
            startX = e.clientX;
            startY = e.clientY;
            btn.style.cursor = 'grabbing';
            btn.setPointerCapture(e.pointerId);
        });

        btn.addEventListener('pointermove', function (e) {
            if (!dragging) return;
            e.preventDefault();
            e.stopPropagation();

            var dx = e.clientX - startX;
            var dy = e.clientY - startY;

            if (!dragStarted && Math.sqrt(dx * dx + dy * dy) < DRAG_THRESHOLD) return;

            if (!dragStarted) {
                dragStarted = true;
                ghost.style.display = 'flex';
                label.style.display = 'block';
                btn.style.opacity = '0.3';
            }

            ghost.style.left = (e.clientX - 18) + 'px';
            ghost.style.top = (e.clientY - 18) + 'px';
            label.style.left = (e.clientX + 24) + 'px';
            label.style.top = (e.clientY - 12) + 'px';

            // Check if cursor is over the map
            var mapRect = mapContainer.getBoundingClientRect();
            var overMap = e.clientX >= mapRect.left && e.clientX <= mapRect.right &&
                e.clientY >= mapRect.top && e.clientY <= mapRect.bottom;

            if (overMap) {
                ghost.style.background = 'rgba(232,168,64,0.9)';
                ghost.style.boxShadow = '0 2px 16px rgba(232,168,64,0.6)';
                label.textContent = 'Drop to explore in 3D';
            } else {
                ghost.style.background = 'rgba(120,120,120,0.7)';
                ghost.style.boxShadow = '0 2px 8px rgba(0,0,0,0.3)';
                label.textContent = 'Drag onto map';
            }
        });

        btn.addEventListener('pointerup', function (e) {
            if (!dragging) return;
            dragging = false;
            btn.style.cursor = 'grab';
            btn.style.opacity = '1';
            ghost.style.display = 'none';
            label.style.display = 'none';
            btn.releasePointerCapture(e.pointerId);

            if (!dragStarted) return;

            // Check if dropped on the map
            var mapRect = mapContainer.getBoundingClientRect();
            var overMap = e.clientX >= mapRect.left && e.clientX <= mapRect.right &&
                e.clientY >= mapRect.top && e.clientY <= mapRect.bottom;

            if (!overMap) return;

            // Convert pixel position to Leaflet latlng to WoW world coords
            var containerPoint = L.point(
                e.clientX - mapRect.left,
                e.clientY - mapRect.top
            );
            var latlng = map.containerPointToLatLng(containerPoint);
            var w = latLngToWorld(latlng);
            var def = MAP_DEFS[currentMapKey];
            var mapId = def ? def.mapId : 0;

            if (mapId < 0) {
                alert('Cannot explore this map — no mapId configured for ' + currentMapKey);
                return;
            }

            // Compute grid coordinates (same formula as HeightMapService)
            // gridX = gy = floor(32 - worldX / 533.33)  (first field in filename)
            // gridY = gx = floor(32 - worldY / 533.33)  (second field in filename)
            var gridX = Math.floor(32 - w.x / TILE_YARDS);
            var gridY = Math.floor(32 - w.y / TILE_YARDS);

            if (gridX < 0 || gridX > 63 || gridY < 0 || gridY > 63) {
                alert('Location is outside the valid map grid');
                return;
            }

            // Navigate to the World Viewer with coordinates
            var url = '/WorldEditor?mapId=' + mapId +
                '&gridX=' + gridX + '&gridY=' + gridY +
                '&worldX=' + w.x.toFixed(1) + '&worldY=' + w.y.toFixed(1);
            window.location.href = url;
        });

        // Cancel on pointer leave without release
        btn.addEventListener('pointercancel', function () {
            dragging = false;
            dragStarted = false;
            btn.style.cursor = 'grab';
            btn.style.opacity = '1';
            ghost.style.display = 'none';
            label.style.display = 'none';
        });
    }

    // ══════════════════════════════════════════════════════════
    //  GLOBALS
    // ══════════════════════════════════════════════════════════

    window.WM = {
        copyText: function (text) {
            navigator.clipboard.writeText(text).then(function () {
                alert('Copied: ' + text);
            });
        },
        deleteSpawn: function (guid) {
            if (!confirm('Delete spawn GUID ' + guid + '?')) return;
            $.ajax({
                url: '/WorldMap/DeleteSpawn?guid=' + guid,
                method: 'DELETE',
                success: function (res) {
                    if (res.success) refreshSpawns();
                    else alert('Delete failed');
                }
            });
        }
    };

    function escHtml(s) {
        if (!s) return '';
        return s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

})();
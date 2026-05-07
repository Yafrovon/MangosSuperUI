// MangosSuperUI — Visual Lab JS (Three.js Spell Effect Viewer)
// Session 37: Phase 1 — Read-only viewer with spatial scene
//
// Spatial layout:
//   Caster marker at (0, 0, 0) — precast, cast, channel origin
//   Target marker at (0, 0, -distance) — impact, state, stateDone, channel target
//   Missile travels from caster → target over flight time
//   Distance adjustable via slider (default 15 units ≈ 15 yards)
//
// Sequence mode: plays precast → cast → missile → impact in order

$(function () {

    // ═══════════════════════════════════════════════════════════════
    // STATE
    // ═══════════════════════════════════════════════════════════════

    var spellData = null;
    var activePhase = 'cast';

    // Three.js core
    var scene, camera, renderer, controls;
    var clock = new THREE.Clock();
    var particleSystems = [];
    var textureCache = {};
    var particleCount = 0;
    var frameCount = 0;
    var lastFpsTime = 0;

    // Scene markers
    var casterMarker, targetMarker, casterLabel, targetLabel;
    var targetDistance = 15;

    // Background mode: 0=black, 1=dark grey, 2=procedural ground, 3+=terrain presets
    var bgMode = 0;
    var bgGroundPlane = null;
    var terrainMesh = null;
    var terrainPresets = [];    // [{key, label}, ...] loaded from server
    var terrainPresetIdx = 0;
    var terrainLoading = false;
    var bgModeCount = 3;       // updated when presets load (3 + presets.length)
    var terrainDoodadGroup = null; // THREE.Group for doodad billboards
    var terrainWmoGroup = null;    // THREE.Group for WMO bounding boxes

    // Playback
    var playbackSpeed = 1.0;
    var paused = false;

    // Missile travel
    var missileGroup = null;     // THREE.Group holding missile particle systems
    var missileT = 0;           // 0 = at caster, 1 = at target
    var missileFlightTime = 1;  // seconds (computed from speed + distance)
    var missileActive = false;

    // Sequence playback
    var sequenceActive = false;
    var sequencePhaseIdx = 0;
    var sequenceTimer = 0;
    var sequencePhases = [];    // [{phase, duration}, ...]

    // Phase → position mapping
    var PHASE_ANCHOR = {
        precast: 'caster',
        cast: 'caster',
        missile: 'travel',   // special: translates caster→target
        impact: 'target',
        channel: 'target',
        state: 'target',
        stateDone: 'target'
    };

    // ═══════════════════════════════════════════════════════════════
    // THREE.JS INIT
    // ═══════════════════════════════════════════════════════════════

    function initThree() {
        var container = document.getElementById('viewerContainer');
        var canvas = document.getElementById('viewerCanvas');

        scene = new THREE.Scene();
        scene.background = new THREE.Color(0x000000);

        camera = new THREE.PerspectiveCamera(60, container.clientWidth / container.clientHeight, 0.1, 500);
        camera.position.set(8, 5, 12);

        renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true });
        renderer.setSize(container.clientWidth, container.clientHeight);
        renderer.setPixelRatio(Math.min(window.devicePixelRatio, 2));

        controls = new THREE.OrbitControls(camera, renderer.domElement);
        controls.enableDamping = true;
        controls.dampingFactor = 0.08;
        controls.target.set(0, 0, -targetDistance / 2);

        // Grid
        var grid = new THREE.GridHelper(40, 40, 0x1a1a1a, 0x0d0d0d);
        grid.position.y = -0.01;
        scene.add(grid);

        // Connection line (caster → target)
        var lineGeo = new THREE.BufferGeometry();
        lineGeo.setAttribute('position', new THREE.Float32BufferAttribute([
            0, 0.02, 0, 0, 0.02, -targetDistance
        ], 3));
        var lineMat = new THREE.LineDashedMaterial({ color: 0x333333, dashSize: 0.5, gapSize: 0.3 });
        var connectLine = new THREE.Line(lineGeo, lineMat);
        connectLine.computeLineDistances();
        connectLine.name = 'connectLine';
        scene.add(connectLine);

        // Caster marker
        casterMarker = createAnchorMarker(0x4488ff, 'Caster');
        casterMarker.position.set(0, 0, 0);
        scene.add(casterMarker);

        // Target marker
        targetMarker = createAnchorMarker(0xff4444, 'Target');
        targetMarker.position.set(0, 0, -targetDistance);
        scene.add(targetMarker);

        // Resize
        new ResizeObserver(function () {
            var w = container.clientWidth;
            var h = container.clientHeight;
            camera.aspect = w / h;
            camera.updateProjectionMatrix();
            renderer.setSize(w, h);
        }).observe(container);

        animate();
    }

    function createAnchorMarker(color, label) {
        var group = new THREE.Group();

        // Ground ring
        var ringGeo = new THREE.RingGeometry(0.6, 0.7, 32);
        ringGeo.rotateX(-Math.PI / 2);
        var ringMat = new THREE.MeshBasicMaterial({ color: color, side: THREE.DoubleSide, transparent: true, opacity: 0.5 });
        group.add(new THREE.Mesh(ringGeo, ringMat));

        // Inner dot
        var dotGeo = new THREE.CircleGeometry(0.08, 16);
        dotGeo.rotateX(-Math.PI / 2);
        var dotMat = new THREE.MeshBasicMaterial({ color: color, side: THREE.DoubleSide });
        var dot = new THREE.Mesh(dotGeo, dotMat);
        dot.position.y = 0.01;
        group.add(dot);

        // Vertical pole
        var poleGeo = new THREE.CylinderGeometry(0.015, 0.015, 2.5, 8);
        var poleMat = new THREE.MeshBasicMaterial({ color: color, transparent: true, opacity: 0.3 });
        var pole = new THREE.Mesh(poleGeo, poleMat);
        pole.position.y = 1.25;
        group.add(pole);

        // Label sprite
        var canvas = document.createElement('canvas');
        canvas.width = 128;
        canvas.height = 32;
        var ctx = canvas.getContext('2d');
        ctx.fillStyle = '#' + color.toString(16).padStart(6, '0');
        ctx.font = '500 18px Consolas, monospace';
        ctx.textAlign = 'center';
        ctx.fillText(label, 64, 22);
        var tex = new THREE.CanvasTexture(canvas);
        var spriteMat = new THREE.SpriteMaterial({ map: tex, transparent: true });
        var sprite = new THREE.Sprite(spriteMat);
        sprite.position.y = 2.8;
        sprite.scale.set(2, 0.5, 1);
        group.add(sprite);

        return group;
    }

    function updateTargetDistance(dist) {
        targetDistance = dist;
        targetMarker.position.z = -dist;

        // Update connection line
        var line = scene.getObjectByName('connectLine');
        if (line) {
            var pos = line.geometry.attributes.position.array;
            pos[5] = -dist;
            line.geometry.attributes.position.needsUpdate = true;
            line.computeLineDistances();
        }

        // Update camera target to midpoint
        controls.target.set(0, 0, -dist / 2);
    }

    // ═══════════════════════════════════════════════════════════════
    // BACKGROUND MODE
    // ═══════════════════════════════════════════════════════════════

    var BG_LABELS = ['Black', 'Grey', 'Ground'];

    function cycleBgMode() {
        bgMode = (bgMode + 1) % bgModeCount;
        applyBgMode();

        if (bgMode < 3) {
            $('#bgLabel').text(BG_LABELS[bgMode]);
        } else {
            var pi = bgMode - 3;
            $('#bgLabel').text(terrainPresets[pi].label);
        }
    }

    function clearBgExtras() {
        if (bgGroundPlane) {
            scene.remove(bgGroundPlane);
            bgGroundPlane.geometry.dispose();
            bgGroundPlane.material.dispose();
            bgGroundPlane = null;
        }
        if (terrainMesh) {
            scene.remove(terrainMesh);
            terrainMesh.geometry.dispose();
            if (terrainMesh.material.uniforms) {
                // ShaderMaterial — dispose uniforms textures
                var u = terrainMesh.material.uniforms;
                for (var ti = 0; ti < 4; ti++) {
                    if (u['uTex' + ti] && u['uTex' + ti].value) u['uTex' + ti].value.dispose();
                }
                if (u.uSplatMap && u.uSplatMap.value) u.uSplatMap.value.dispose();
            }
            terrainMesh.material.dispose();
            terrainMesh = null;
        }
        if (terrainDoodadGroup) {
            terrainDoodadGroup.traverse(function (obj) {
                if (obj.geometry) obj.geometry.dispose();
                if (obj.material) obj.material.dispose();
            });
            scene.remove(terrainDoodadGroup);
            terrainDoodadGroup = null;
        }
        if (terrainWmoGroup) {
            terrainWmoGroup.traverse(function (obj) {
                if (obj.geometry) obj.geometry.dispose();
                if (obj.material) obj.material.dispose();
            });
            scene.remove(terrainWmoGroup);
            terrainWmoGroup = null;
        }
    }

    function applyBgMode() {
        clearBgExtras();

        // Find grid helper
        var grid = null;
        scene.traverse(function (c) { if (c instanceof THREE.GridHelper) grid = c; });

        if (bgMode === 0) {
            // Black
            scene.background = new THREE.Color(0x000000);
            if (grid) { grid.visible = true; }
        } else if (bgMode === 1) {
            // Dark grey-blue
            scene.background = new THREE.Color(0x1a1a2e);
            if (grid) { grid.visible = true; }
        } else if (bgMode === 2) {
            // Procedural ground plane
            scene.background = new THREE.Color(0x0a0e14);
            if (grid) { grid.visible = false; }
            buildProceduralGround();
        } else {
            // Terrain preset
            scene.background = new THREE.Color(0x0a0e14);
            if (grid) { grid.visible = false; }
            var pi = bgMode - 3;
            if (pi < terrainPresets.length) {
                loadTerrainPreset(terrainPresets[pi].key);
            }
        }
    }

    function buildProceduralGround() {
        var planeGeo = new THREE.PlaneGeometry(80, 80, 16, 16);
        planeGeo.rotateX(-Math.PI / 2);
        var colors = new Float32Array(planeGeo.attributes.position.count * 3);
        var posAttr = planeGeo.attributes.position;

        for (var i = 0; i < posAttr.count; i++) {
            var x = posAttr.getX(i);
            var z = posAttr.getZ(i);
            var t = (Math.sin(x * 0.3) * Math.cos(z * 0.2) + 1) * 0.5;
            var noise = (Math.sin(x * 1.7 + z * 2.3) * 0.5 + 0.5) * 0.15;
            colors[i * 3] = 0.15 + t * 0.12 + noise;
            colors[i * 3 + 1] = 0.18 + t * 0.08 - noise * 0.5;
            colors[i * 3 + 2] = 0.08 + t * 0.04;
        }
        planeGeo.setAttribute('color', new THREE.Float32BufferAttribute(colors, 3));
        var planeMat = new THREE.MeshBasicMaterial({ vertexColors: true });
        bgGroundPlane = new THREE.Mesh(planeGeo, planeMat);
        bgGroundPlane.position.y = -0.02;
        scene.add(bgGroundPlane);
    }

    function loadTerrainPreset(presetKey) {
        if (terrainLoading) return;
        terrainLoading = true;
        $('#bgLabel').text('Loading...');

        // Step 1: Load heightmap geometry (existing endpoint)
        $.getJSON('/Patch/VisualTerrain', { preset: presetKey }, function (geoData) {
            if (!geoData.success) {
                terrainLoading = false;
                $('#bgLabel').text('Error');
                console.warn('Terrain geo failed:', geoData.error);
                return;
            }

            // Step 2: Load ADT texture data (new endpoint)
            $.getJSON('/Patch/VisualTerrainTextures', { preset: presetKey, cx: 8, cy: 8, radius: 3 }, function (texData) {

                // Doodad loading deferred — will fire after mesh is built to avoid clearBgExtras race
                var pendingDoodadLoad = function () {
                    $.getJSON('/Patch/VisualTerrainDoodads', { preset: presetKey, cx: 8, cy: 8, radius: 3 }, function (doodadData) {
                        if (doodadData.success) {
                            buildDoodadBillboards(doodadData.doodads || []);
                            if (doodadData.wmos && doodadData.wmos.length > 0) {
                                buildWmoBoxes(doodadData.wmos);
                            }
                            console.log('Terrain doodads:', doodadData.totalDoodads,
                                'WMOs:', doodadData.totalWmos, 'types:', doodadData.typeCounts);
                        }
                    });
                };

                if (!texData.success) {
                    // ADT textures not available — fall back to vertex-color terrain
                    console.warn('Terrain textures not available:', texData.error, '— using vertex colors');
                    buildTerrainMesh(geoData);
                    pendingDoodadLoad();
                    terrainLoading = false;
                    var preset = terrainPresets.find(function (p) { return p.key === presetKey; });
                    $('#bgLabel').text(preset ? preset.label : presetKey);
                    return;
                }

                // Load ground textures from server, then build textured mesh
                // New composite mode: server sends a pre-baked RGB texture
                if (texData.mode === 'composite' && texData.compositeBase64) {
                    var compImg = new Image();
                    compImg.onload = function () {
                        var compTexture = new THREE.Texture(compImg);
                        compTexture.needsUpdate = true;
                        compTexture.minFilter = THREE.LinearFilter;
                        compTexture.magFilter = THREE.LinearFilter;
                        compTexture.wrapS = THREE.ClampToEdgeWrapping;
                        compTexture.wrapT = THREE.ClampToEdgeWrapping;
                        buildTerrainMeshComposite(geoData, compTexture);
                        pendingDoodadLoad();
                        terrainLoading = false;
                        var preset = terrainPresets.find(function (p) { return p.key === presetKey; });
                        $('#bgLabel').text(preset ? preset.label : presetKey);
                    };
                    compImg.src = texData.compositeBase64;
                } else {
                    // Legacy splat map path (fallback)
                    var texturesToLoad = (texData.textures || []).filter(function (t) { return t.url; });
                    var loadedCount = 0;
                    var loadedTextures = {};

                    var splatTexture = null;
                    if (texData.splatMapBase64) {
                        var img = new Image();
                        img.onload = function () {
                            splatTexture = new THREE.Texture(img);
                            splatTexture.needsUpdate = true;
                            splatTexture.minFilter = THREE.LinearFilter;
                            splatTexture.magFilter = THREE.LinearFilter;
                            splatTexture.wrapS = THREE.ClampToEdgeWrapping;
                            splatTexture.wrapT = THREE.ClampToEdgeWrapping;
                            checkAllLoaded();
                        };
                        img.src = texData.splatMapBase64;
                    }

                    function checkAllLoaded() {
                        if (loadedCount < texturesToLoad.length) return;
                        if (texData.splatMapBase64 && !splatTexture) return;

                        var orderedTextures = [];
                        for (var oi = 0; oi < texData.textures.length; oi++) {
                            orderedTextures.push(loadedTextures[texData.textures[oi].index] || null);
                        }
                        buildTerrainMeshTextured(geoData, orderedTextures, splatTexture, texData);
                        pendingDoodadLoad();
                        terrainLoading = false;
                        var preset = terrainPresets.find(function (p) { return p.key === presetKey; });
                        $('#bgLabel').text(preset ? preset.label : presetKey);
                    }

                    if (texturesToLoad.length === 0) {
                        checkAllLoaded();
                    } else {
                        var loader = new THREE.TextureLoader();
                        for (var li = 0; li < texturesToLoad.length; li++) {
                            (function (tex) {
                                loader.load(tex.url, function (threeTex) {
                                    threeTex.wrapS = THREE.RepeatWrapping;
                                    threeTex.wrapT = THREE.RepeatWrapping;
                                    threeTex.minFilter = THREE.LinearMipMapLinearFilter;
                                    threeTex.magFilter = THREE.LinearFilter;
                                    loadedTextures[tex.index] = threeTex;
                                    loadedCount++;
                                    checkAllLoaded();
                                }, undefined, function () {
                                    loadedCount++;
                                    checkAllLoaded();
                                });
                            })(texturesToLoad[li]);
                        }
                    }
                }

            }).fail(function () {
                // Texture endpoint not available — vertex color fallback
                buildTerrainMesh(geoData);
                terrainLoading = false;
                var preset = terrainPresets.find(function (p) { return p.key === presetKey; });
                $('#bgLabel').text(preset ? preset.label : presetKey);
            });

        }).fail(function () {
            terrainLoading = false;
            $('#bgLabel').text('Failed');
        });
    }

    function buildTerrainMesh(data) {
        // Vertex-color fallback when ADT textures are not available
        clearBgExtras();

        var positions = new Float32Array(data.positions);
        var indices = new Uint32Array(data.indices);

        var geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        geometry.setIndex(new THREE.BufferAttribute(indices, 1));
        geometry.computeVertexNormals();

        // Height-based vertex colors (green valleys → brown hills → grey rock)
        var posArr = geometry.attributes.position.array;
        var vertCount = posArr.length / 3;
        var colors = new Float32Array(vertCount * 3);

        var minY = Infinity, maxY = -Infinity;
        for (var i = 0; i < vertCount; i++) {
            var y = posArr[i * 3 + 1];
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
        }
        var range = maxY - minY || 1;

        for (var i = 0; i < vertCount; i++) {
            var y = posArr[i * 3 + 1];
            var t = (y - minY) / range;
            var x = posArr[i * 3];
            var z = posArr[i * 3 + 2];
            var noise = (Math.sin(x * 0.7 + z * 1.1) * 0.5 + 0.5) * 0.08;

            if (t < 0.3) {
                colors[i * 3] = 0.12 + noise;
                colors[i * 3 + 1] = 0.22 + t * 0.3 + noise;
                colors[i * 3 + 2] = 0.06;
            } else if (t < 0.7) {
                var f = (t - 0.3) / 0.4;
                colors[i * 3] = 0.18 + f * 0.15 + noise;
                colors[i * 3 + 1] = 0.20 + (1 - f) * 0.08;
                colors[i * 3 + 2] = 0.08 + f * 0.04;
            } else {
                var f = (t - 0.7) / 0.3;
                colors[i * 3] = 0.25 + f * 0.15 + noise;
                colors[i * 3 + 1] = 0.22 + f * 0.12;
                colors[i * 3 + 2] = 0.18 + f * 0.1;
            }
        }
        geometry.setAttribute('color', new THREE.Float32BufferAttribute(colors, 3));

        var material = new THREE.MeshPhongMaterial({
            vertexColors: true,
            flatShading: true,
            shininess: 5,
            specular: new THREE.Color(0x111111)
        });

        terrainMesh = new THREE.Mesh(geometry, material);
        terrainMesh.position.y = -0.5;
        scene.add(terrainMesh);
        ensureTerrainLights();
    }

    // ── Textured terrain mesh with splat-mapped ground textures ──
    // DIAGNOSTIC MODE: cycles through UV transform variants to find correct tiling
    var _tileVariant = 0;
    var _tileVariantCount = 8;
    var _tileVariantLabels = [
        '0: baseline (u,v)',
        '1: flip V (u, 1-v)',
        '2: flip U (1-u, v)',
        '3: flip both (1-u, 1-v)',
        '4: swap (v, u)',
        '5: swap+flipV (v, 1-u)',
        '6: swap+flipU (1-v, u)',
        '7: swap+flip both (1-v, 1-u)',
    ];

    function buildTerrainMeshTextured(geoData, groundTextures, splatTexture, texData) {
        // Don't clear doodads — only clear terrain mesh and ground plane
        if (terrainMesh) {
            scene.remove(terrainMesh);
            terrainMesh.geometry.dispose();
            if (terrainMesh.material.uniforms) {
                var u = terrainMesh.material.uniforms;
                for (var ti = 0; ti < 4; ti++) {
                    if (u['uTex' + ti] && u['uTex' + ti].value) u['uTex' + ti].value.dispose();
                }
                if (u.uSplatMap && u.uSplatMap.value) u.uSplatMap.value.dispose();
            }
            terrainMesh.material.dispose();
            terrainMesh = null;
        }
        if (bgGroundPlane) {
            scene.remove(bgGroundPlane);
            bgGroundPlane.geometry.dispose();
            bgGroundPlane.material.dispose();
            bgGroundPlane = null;
        }

        var positions = new Float32Array(geoData.positions);
        var indices = new Uint32Array(geoData.indices);

        var geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        geometry.setIndex(new THREE.BufferAttribute(indices, 1));
        geometry.computeVertexNormals();

        // Compute UV attributes
        var posArr = geometry.attributes.position.array;
        var vertCount = posArr.length / 3;
        var splatUvs = new Float32Array(vertCount * 2);
        var worldUvs = new Float32Array(vertCount * 2);

        // Find mesh XZ bounds for splat UV normalization
        var minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
        for (var i = 0; i < vertCount; i++) {
            var x = posArr[i * 3], z = posArr[i * 3 + 2];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
        }
        var rangeX = maxX - minX || 1, rangeZ = maxZ - minZ || 1;

        var tileScale = 1.0 / (8 * 4.167);

        for (var i = 0; i < vertCount; i++) {
            var x = posArr[i * 3], z = posArr[i * 3 + 2];
            splatUvs[i * 2] = (x - minX) / rangeX;
            splatUvs[i * 2 + 1] = (z - minZ) / rangeZ;
            worldUvs[i * 2] = x * tileScale;
            worldUvs[i * 2 + 1] = z * tileScale;
        }

        geometry.setAttribute('vSplatUv', new THREE.BufferAttribute(splatUvs, 2));
        geometry.setAttribute('vWorldUv', new THREE.BufferAttribute(worldUvs, 2));

        // Fill missing texture slots with solid-color fallbacks
        var tex0 = groundTextures[0] || createFallbackTexture(0x3a6b35);
        var tex1 = groundTextures[1] || createFallbackTexture(0x6b5b3a);
        var tex2 = groundTextures[2] || createFallbackTexture(0x555555);
        var tex3 = groundTextures[3] || createFallbackTexture(0x444444);
        var hasTextures = splatTexture && (groundTextures[0] || groundTextures[1]);

        // Shader with uVariant uniform to cycle UV transforms
        var vertexShader = [
            'varying vec2 vSplatUvOut;',
            'varying vec2 vWorldUvOut;',
            'varying vec3 vNormal;',
            'varying vec3 vWorldPos;',
            'attribute vec2 vSplatUv;',
            'attribute vec2 vWorldUv;',
            'void main() {',
            '    vSplatUvOut = vSplatUv;',
            '    vWorldUvOut = vWorldUv;',
            '    vNormal = normalize(normalMatrix * normal);',
            '    vec4 worldPos = modelMatrix * vec4(position, 1.0);',
            '    vWorldPos = worldPos.xyz;',
            '    gl_Position = projectionMatrix * viewMatrix * worldPos;',
            '}'
        ].join('\n');

        var fragmentShader = [
            'uniform sampler2D uTex0, uTex1, uTex2, uTex3;',
            'uniform sampler2D uSplatMap;',
            'uniform float uHasTextures;',
            'uniform float uVariant;',
            'varying vec2 vSplatUvOut;',
            'varying vec2 vWorldUvOut;',
            'varying vec3 vNormal;',
            'varying vec3 vWorldPos;',
            'void main() {',
            '    vec3 lightDir = normalize(vec3(0.4, 0.8, 0.3));',
            '    float diffuse = max(dot(vNormal, lightDir), 0.0);',
            '    float lighting = 0.35 + diffuse * 0.65;',
            '    vec3 color;',
            '    if (uHasTextures > 0.5) {',
            '        float su = vSplatUvOut.x;',
            '        float sv = vSplatUvOut.y;',
            '        float v = uVariant;',
            '        // 0=baseline, 1=flipV, 2=flipU, 3=flipBoth,',
            '        // 4=swap, 5=swap+flipV, 6=swap+flipU, 7=swap+flipBoth',
            '        float ru, rv;',
            '        if (v < 0.5) { ru = su; rv = sv; }',
            '        else if (v < 1.5) { ru = su; rv = 1.0 - sv; }',
            '        else if (v < 2.5) { ru = 1.0 - su; rv = sv; }',
            '        else if (v < 3.5) { ru = 1.0 - su; rv = 1.0 - sv; }',
            '        else if (v < 4.5) { ru = sv; rv = su; }',
            '        else if (v < 5.5) { ru = sv; rv = 1.0 - su; }',
            '        else if (v < 6.5) { ru = 1.0 - sv; rv = su; }',
            '        else { ru = 1.0 - sv; rv = 1.0 - su; }',
            '        vec4 splat = texture2D(uSplatMap, vec2(ru, rv));',
            '        color = texture2D(uTex0, vWorldUvOut).rgb;',
            '        color = mix(color, texture2D(uTex1, vWorldUvOut).rgb, splat.g);',
            '        color = mix(color, texture2D(uTex2, vWorldUvOut).rgb, splat.b);',
            '    } else {',
            '        float h = clamp((vWorldPos.y + 2.0) / 4.0, 0.0, 1.0);',
            '        vec3 grass = vec3(0.15, 0.28, 0.08);',
            '        vec3 dirt  = vec3(0.30, 0.24, 0.14);',
            '        vec3 rock  = vec3(0.35, 0.33, 0.28);',
            '        color = h < 0.3 ? mix(grass, dirt, h / 0.3) :',
            '                h < 0.7 ? mix(dirt, rock, (h - 0.3) / 0.4) : rock;',
            '    }',
            '    color *= lighting;',
            '    float fogDist = length(vWorldPos - cameraPosition);',
            '    float fog = clamp(1.0 - exp(-fogDist * fogDist * 0.00004), 0.0, 0.6);',
            '    color = mix(color, vec3(0.04, 0.06, 0.08), fog);',
            '    gl_FragColor = vec4(color, 1.0);',
            '}'
        ].join('\n');

        var material = new THREE.ShaderMaterial({
            uniforms: {
                uTex0: { value: tex0 },
                uTex1: { value: tex1 },
                uTex2: { value: tex2 },
                uTex3: { value: tex3 },
                uSplatMap: { value: splatTexture },
                uHasTextures: { value: hasTextures ? 1.0 : 0.0 },
                uVariant: { value: 0.0 }
            },
            vertexShader: vertexShader,
            fragmentShader: fragmentShader,
            side: THREE.FrontSide
        });

        terrainMesh = new THREE.Mesh(geometry, material);
        terrainMesh.position.y = -0.5;
        scene.add(terrainMesh);
        ensureTerrainLights();

        // Start cycling variants every 3 seconds
        if (window._variantTimer) clearInterval(window._variantTimer);
        _tileVariant = 0;
        console.log('TILE VARIANT: ' + _tileVariantLabels[0]);
        window._variantTimer = setInterval(function () {
            _tileVariant = (_tileVariant + 1) % _tileVariantCount;
            if (terrainMesh && terrainMesh.material.uniforms && terrainMesh.material.uniforms.uVariant) {
                terrainMesh.material.uniforms.uVariant.value = _tileVariant;
                console.log('TILE VARIANT: ' + _tileVariantLabels[_tileVariant]);
            }
        }, 3000);

        console.log('Terrain: textured mesh with variant cycling. Watch for correct tiling.');
        console.log('Variants cycle every 3s. Check console for current variant number.');
    }

    function ensureTerrainLights() {
        if (!scene.getObjectByName('terrainLight')) {
            var light = new THREE.DirectionalLight(0xffeedd, 0.6);
            light.position.set(10, 20, 5);
            light.name = 'terrainLight';
            scene.add(light);
            var ambient = new THREE.AmbientLight(0x333344, 0.4);
            ambient.name = 'terrainAmbient';
            scene.add(ambient);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // COMPOSITE TERRAIN MESH — server-baked RGB texture
    // ═══════════════════════════════════════════════════════════════

    function buildTerrainMeshComposite(geoData, compositeTexture) {
        // Clean up previous terrain
        if (terrainMesh) {
            scene.remove(terrainMesh);
            terrainMesh.geometry.dispose();
            if (terrainMesh.material.uniforms) {
                var u = terrainMesh.material.uniforms;
                if (u.uComposite && u.uComposite.value) u.uComposite.value.dispose();
                // Also clean up old splat-style uniforms if present
                for (var ti = 0; ti < 4; ti++) {
                    if (u['uTex' + ti] && u['uTex' + ti].value) u['uTex' + ti].value.dispose();
                }
                if (u.uSplatMap && u.uSplatMap.value) u.uSplatMap.value.dispose();
            }
            terrainMesh.material.dispose();
            terrainMesh = null;
        }
        if (bgGroundPlane) {
            scene.remove(bgGroundPlane);
            bgGroundPlane.geometry.dispose();
            bgGroundPlane.material.dispose();
            bgGroundPlane = null;
        }

        var positions = new Float32Array(geoData.positions);
        var indices = new Uint32Array(geoData.indices);

        var geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        geometry.setIndex(new THREE.BufferAttribute(indices, 1));
        geometry.computeVertexNormals();

        // Compute UV: normalize mesh XZ to 0..1 for composite texture sampling
        var posArr = geometry.attributes.position.array;
        var vertCount = posArr.length / 3;
        var uvs = new Float32Array(vertCount * 2);

        var minX = Infinity, maxX = -Infinity, minZ = Infinity, maxZ = -Infinity;
        for (var i = 0; i < vertCount; i++) {
            var x = posArr[i * 3], z = posArr[i * 3 + 2];
            if (x < minX) minX = x; if (x > maxX) maxX = x;
            if (z < minZ) minZ = z; if (z > maxZ) maxZ = z;
        }
        var rangeX = maxX - minX || 1, rangeZ = maxZ - minZ || 1;

        for (var i = 0; i < vertCount; i++) {
            var x = posArr[i * 3], z = posArr[i * 3 + 2];
            uvs[i * 2] = (x - minX) / rangeX;
            uvs[i * 2 + 1] = (z - minZ) / rangeZ;
        }
        geometry.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));

        // Simple shader: sample composite texture + lighting + fog
        var vertexShader = [
            'varying vec2 vUv;',
            'varying vec3 vNormal;',
            'varying vec3 vWorldPos;',
            'void main() {',
            '    vUv = uv;',
            '    vNormal = normalize(normalMatrix * normal);',
            '    vec4 worldPos = modelMatrix * vec4(position, 1.0);',
            '    vWorldPos = worldPos.xyz;',
            '    gl_Position = projectionMatrix * viewMatrix * worldPos;',
            '}'
        ].join('\n');

        var fragmentShader = [
            'uniform sampler2D uComposite;',
            'varying vec2 vUv;',
            'varying vec3 vNormal;',
            'varying vec3 vWorldPos;',
            'void main() {',
            '    vec3 lightDir = normalize(vec3(0.4, 0.8, 0.3));',
            '    float diffuse = max(dot(vNormal, lightDir), 0.0);',
            '    float lighting = 0.35 + diffuse * 0.65;',
            '    vec3 color = texture2D(uComposite, vUv).rgb;',
            '    color *= lighting;',
            '    float fogDist = length(vWorldPos - cameraPosition);',
            '    float fog = clamp(1.0 - exp(-fogDist * fogDist * 0.00004), 0.0, 0.6);',
            '    color = mix(color, vec3(0.04, 0.06, 0.08), fog);',
            '    gl_FragColor = vec4(color, 1.0);',
            '}'
        ].join('\n');

        var material = new THREE.ShaderMaterial({
            uniforms: {
                uComposite: { value: compositeTexture }
            },
            vertexShader: vertexShader,
            fragmentShader: fragmentShader,
            side: THREE.FrontSide
        });

        terrainMesh = new THREE.Mesh(geometry, material);
        terrainMesh.name = 'terrainMesh';
        scene.add(terrainMesh);

        ensureTerrainLights();
    }

    function createFallbackTexture(color) {
        var canvas = document.createElement('canvas');
        canvas.width = 4; canvas.height = 4;
        var ctx = canvas.getContext('2d');
        var r = (color >> 16) & 0xFF, g = (color >> 8) & 0xFF, b = color & 0xFF;
        ctx.fillStyle = 'rgb(' + r + ',' + g + ',' + b + ')';
        ctx.fillRect(0, 0, 4, 4);
        var tex = new THREE.CanvasTexture(canvas);
        tex.wrapS = THREE.RepeatWrapping; tex.wrapT = THREE.RepeatWrapping;
        return tex;
    }

    // ═══════════════════════════════════════════════════════════════
    // DOODAD BILLBOARDS — trees, rocks, bushes as simple geometry
    // ═══════════════════════════════════════════════════════════════

    function buildDoodadBillboards(doodads) {
        if (!doodads || doodads.length === 0) return;

        terrainDoodadGroup = new THREE.Group();
        terrainDoodadGroup.name = 'terrainDoodads';

        // Cap at 800 — prioritize trees/rocks over detail clutter
        var maxDoodads = 800;
        var sorted = doodads.slice().sort(function (a, b) {
            var pa = (a.type === 'detail' || a.type === 'vegetation') ? 1 : 0;
            var pb = (b.type === 'detail' || b.type === 'vegetation') ? 1 : 0;
            return pa - pb;
        });
        if (sorted.length > maxDoodads) sorted = sorted.slice(0, maxDoodads);

        // Group doodads by unique model path
        var modelGroups = {};
        for (var i = 0; i < sorted.length; i++) {
            var key = sorted[i].model || '';
            if (!modelGroups[key]) modelGroups[key] = [];
            modelGroups[key].push(sorted[i]);
        }

        var uniqueModels = Object.keys(modelGroups);
        var loadedCount = 0;
        var failedModels = {};
        console.log('Doodad 3D: loading ' + uniqueModels.length + ' unique models for ' + sorted.length + ' placements');

        // Load each unique model once, then instance it
        uniqueModels.forEach(function (modelPath) {
            var placements = modelGroups[modelPath];

            $.getJSON('/Patch/VisualTerrainDoodadModel', { path: modelPath }, function (data) {
                if (data.success && data.positions && data.indices) {
                    // Shared vertex attributes
                    var posAttr = new THREE.Float32BufferAttribute(data.positions, 3);
                    var normAttr = new THREE.Float32BufferAttribute(data.normals, 3);
                    var uvAttr = new THREE.Float32BufferAttribute(data.uvs, 2);
                    var allIndices = data.indices;

                    // Build one geometry+material per submesh
                    var submeshParts = [];
                    var subs = data.submeshes || [{ indexStart: 0, indexCount: allIndices.length, textureBase64: null }];

                    for (var si = 0; si < subs.length; si++) {
                        var sub = subs[si];
                        if (!sub.indexCount || sub.indexCount === 0) continue;

                        // Extract indices for this submesh
                        var subIndices = allIndices.slice(sub.indexStart, sub.indexStart + sub.indexCount);

                        var geometry = new THREE.BufferGeometry();
                        geometry.setAttribute('position', posAttr);
                        geometry.setAttribute('normal', normAttr);
                        geometry.setAttribute('uv', uvAttr);
                        geometry.setIndex(new THREE.BufferAttribute(new Uint32Array(subIndices), 1));

                        var material;
                        if (sub.textureBase64) {
                            var tex = new THREE.TextureLoader().load(sub.textureBase64);
                            tex.flipY = true;
                            tex.wrapS = THREE.RepeatWrapping;
                            tex.wrapT = THREE.RepeatWrapping;
                            // Unlit material — WoW doodads don't use real-time lighting.
                            // MeshBasicMaterial avoids dark faces from backface normals.
                            material = new THREE.MeshBasicMaterial({
                                map: tex,
                                side: THREE.DoubleSide,
                                alphaTest: 0.5,
                                transparent: true,
                                depthWrite: true
                            });
                        } else {
                            material = new THREE.MeshPhongMaterial({
                                color: 0x808080,
                                flatShading: true,
                                side: THREE.DoubleSide
                            });
                        }

                        submeshParts.push({ geometry: geometry, material: material });
                    }

                    // Instance at each placement
                    for (var pi = 0; pi < placements.length; pi++) {
                        var d = placements[pi];
                        var group = new THREE.Group();

                        for (var sp = 0; sp < submeshParts.length; sp++) {
                            var mesh = new THREE.Mesh(submeshParts[sp].geometry, submeshParts[sp].material);
                            group.add(mesh);
                        }

                        var s = (d.scale || 1.0) * 0.5;
                        group.scale.set(s, s, s);
                        group.position.set(d.x, d.y, d.z);
                        group.rotation.y = (d.rotY || 0) * Math.PI / 180;
                        terrainDoodadGroup.add(group);
                    }
                    loadedCount++;
                    if (data.textureDebug) {
                        var unresolved = data.textureDebug.filter(function (t) { return !t.resolved; });
                        if (unresolved.length > 0 || loadedCount <= 3) {
                            console.log('Doodad [' + data.name + '] textures:', data.texturesResolved + '/' + data.textureCount,
                                'submeshes:', data.submeshes ? data.submeshes.map(function (s) { return 'tex' + s.texIdx + (s.batchMapped ? '(batch)' : '(fallback)') + '→resolved' + s.resolvedTexIdx; }) : 'none',
                                'unresolved:', unresolved.map(function (t) { return 'type' + t.type + ':' + (t.filename || 'no-filename'); }));
                            if (loadedCount <= 1 && data.batchDebug) {
                                console.log('  Batch chain:', data.batchDebug);
                                console.log('  TextureLookup:', data.textureLookup);
                                console.log('  Textures:', data.textureDebug);
                            }
                        }
                    }
                } else {
                    failedModels[modelPath] = true;
                    buildDoodadBillboardsFallback(placements);
                }
            }).fail(function () {
                failedModels[modelPath] = true;
                buildDoodadBillboardsFallback(placements);
            });
        });

        terrainDoodadGroup.position.y = -0.5; // match terrain offset
        scene.add(terrainDoodadGroup);
    }

    /// Billboard fallback for doodads whose M2 failed to load
    function buildDoodadBillboardsFallback(doodads) {
        var typeStyles = {
            tree: { color: 0x2d5a1e, height: 4.0, width: 2.5, shape: 'tree' },
            vegetation: { color: 0x3a7a2a, height: 1.0, width: 1.2, shape: 'bush' },
            rock: { color: 0x6a6a6a, height: 1.5, width: 2.0, shape: 'rock' },
            fence: { color: 0x5a4030, height: 1.5, width: 0.3, shape: 'pole' },
            wood: { color: 0x6a5030, height: 0.8, width: 1.5, shape: 'rock' },
            detail: { color: 0x4a6a3a, height: 0.4, width: 0.6, shape: 'bush' },
            generic: { color: 0x505050, height: 1.0, width: 1.0, shape: 'rock' }
        };

        for (var di = 0; di < doodads.length; di++) {
            var d = doodads[di];
            var style = typeStyles[d.type] || typeStyles.generic;
            var s = d.scale || 1.0;
            var h = style.height * s;
            var w = style.width * s;

            if (style.shape === 'tree') {
                var trunkGeo = new THREE.CylinderGeometry(0.08 * s, 0.12 * s, h * 0.4, 5);
                var trunkMat = new THREE.MeshPhongMaterial({ color: 0x4a3520, flatShading: true });
                var trunk = new THREE.Mesh(trunkGeo, trunkMat);
                trunk.position.set(d.x, d.y + h * 0.2, d.z);
                terrainDoodadGroup.add(trunk);
                var canopyGeo = new THREE.ConeGeometry(w * 0.5, h * 0.65, 6);
                var canopyMat = new THREE.MeshPhongMaterial({
                    color: style.color + Math.floor(Math.random() * 0x101010), flatShading: true
                });
                var canopy = new THREE.Mesh(canopyGeo, canopyMat);
                canopy.position.set(d.x, d.y + h * 0.4 + h * 0.65 * 0.5, d.z);
                canopy.rotation.y = Math.random() * Math.PI * 2;
                terrainDoodadGroup.add(canopy);
            } else if (style.shape === 'bush') {
                var bushGeo = new THREE.SphereGeometry(0.5, 5, 4);
                var bushMat = new THREE.MeshPhongMaterial({ color: style.color, flatShading: true });
                var bush = new THREE.Mesh(bushGeo, bushMat);
                bush.position.set(d.x, d.y + h * 0.3, d.z);
                bush.scale.set(w, h, w);
                terrainDoodadGroup.add(bush);
            } else if (style.shape === 'rock') {
                var rockGeo = new THREE.DodecahedronGeometry(0.5, 0);
                var rockMat = new THREE.MeshPhongMaterial({ color: style.color, flatShading: true });
                var rock = new THREE.Mesh(rockGeo, rockMat);
                rock.position.set(d.x, d.y + h * 0.3, d.z);
                rock.scale.set(w, h, w * 0.8);
                terrainDoodadGroup.add(rock);
            } else {
                var poleGeo = new THREE.CylinderGeometry(0.05, 0.05, h, 4);
                var poleMat = new THREE.MeshPhongMaterial({ color: style.color, flatShading: true });
                var pole = new THREE.Mesh(poleGeo, poleMat);
                pole.position.set(d.x, d.y + h * 0.5, d.z);
                terrainDoodadGroup.add(pole);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // WMO BOUNDING BOXES — building silhouettes
    // ═══════════════════════════════════════════════════════════════

    function buildWmoBoxes(wmos) {
        if (!wmos || wmos.length === 0) return;

        terrainWmoGroup = new THREE.Group();
        terrainWmoGroup.name = 'terrainWmos';

        for (var i = 0; i < wmos.length; i++) {
            var w = wmos[i];
            var sx = Math.max(w.sizeX || 1, 0.5);
            var sy = Math.max(w.sizeY || 1, 0.5);
            var sz = Math.max(w.sizeZ || 1, 0.5);

            var geo = new THREE.BoxGeometry(sx, sy, sz);
            var mat = new THREE.MeshBasicMaterial({
                color: 0xc4a882, transparent: true, opacity: 0.7,
                wireframe: false
            });
            var box = new THREE.Mesh(geo, mat);
            box.position.set(w.x, w.y + sy * 0.5, w.z);
            terrainWmoGroup.add(box);

            // Wireframe overlay for visibility
            var wireGeo = new THREE.EdgesGeometry(geo);
            var wireMat = new THREE.LineBasicMaterial({ color: 0xffd700 });
            var wireframe = new THREE.LineSegments(wireGeo, wireMat);
            wireframe.position.copy(box.position);
            terrainWmoGroup.add(wireframe);

            console.log('WMO ' + i + ': ' + w.model + ' at (' + w.x.toFixed(1) + ',' + w.y.toFixed(1) + ',' + w.z.toFixed(1) + ') size(' + sx.toFixed(1) + ',' + sy.toFixed(1) + ',' + sz.toFixed(1) + ')');
        }

        terrainWmoGroup.position.y = -0.5;
        scene.add(terrainWmoGroup);
    }

    function loadTerrainPresetList() {
        $.getJSON('/Patch/VisualTerrainPresets', function (data) {
            if (data.success && data.presets && data.presets.length > 0) {
                terrainPresets = data.presets;
                bgModeCount = 3 + terrainPresets.length;
            }
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // ANIMATION LOOP
    // ═══════════════════════════════════════════════════════════════

    function animate() {
        requestAnimationFrame(animate);
        var rawDt = clock.getDelta();
        var dt = paused ? 0 : rawDt * playbackSpeed;

        controls.update();

        // Missile travel
        if (missileActive && missileGroup && dt > 0) {
            missileT += dt / missileFlightTime;
            if (missileT >= 1) {
                missileT = 1;
                missileActive = false;
            }
            var z = lerp(0, -targetDistance, missileT);
            missileGroup.position.z = z;
        }

        // Sequence playback
        if (sequenceActive && dt > 0) {
            updateSequence(dt);
        }

        // Update particle systems
        particleCount = 0;
        for (var i = 0; i < particleSystems.length; i++) {
            particleSystems[i].update(dt);
            particleCount += particleSystems[i].liveCount;
        }

        renderer.render(scene, camera);

        // Overlay stats
        frameCount++;
        var now = performance.now();
        if (now - lastFpsTime > 500) {
            var fps = Math.round(frameCount / ((now - lastFpsTime) / 1000));
            $('#overlayFps').text('FPS: ' + fps);
            $('#overlayParticles').text('Particles: ' + particleCount);
            frameCount = 0;
            lastFpsTime = now;
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // PARTICLE SYSTEM (same as before — emission shapes, atlas, etc.)
    // ═══════════════════════════════════════════════════════════════

    function createParticleSystem(emitter, texture, parentGroup) {
        var rate = emitter.emissionRate || 20;
        var life = emitter.lifespan || 0.5;
        var maxParticles = Math.min(Math.ceil(rate * life * 2.5), 4000);

        var blendMode = emitter.blendMode === 4 ? THREE.AdditiveBlending :
            emitter.blendMode === 2 ? THREE.NormalBlending : THREE.NormalBlending;

        var cStart = parseArgb(emitter.colorStart);
        var cMid = parseArgb(emitter.colorMid);
        var cEnd = parseArgb(emitter.colorEnd);

        // Texture atlas
        var atlasRows = emitter.texRows || 1;
        var atlasCols = emitter.texCols || 1;
        if (atlasRows === 1 && atlasCols === 1 && emitter._texFilename) {
            var match = emitter._texFilename.match(/(\d+)[xX](\d+)/);
            if (match) { atlasCols = parseInt(match[1]); atlasRows = parseInt(match[2]); }
        }
        var cellCount = atlasRows * atlasCols;
        var cellW = 1.0 / atlasCols;
        var cellH = 1.0 / atlasRows;

        var positions = new Float32Array(maxParticles * 4 * 3);
        var uvs = new Float32Array(maxParticles * 4 * 2);
        var colors = new Float32Array(maxParticles * 4 * 4);
        var indices = new Uint32Array(maxParticles * 6);

        for (var i = 0; i < maxParticles; i++) {
            var vi = i * 4, ii = i * 6;
            indices[ii] = vi; indices[ii + 1] = vi + 1; indices[ii + 2] = vi + 2;
            indices[ii + 3] = vi; indices[ii + 4] = vi + 2; indices[ii + 5] = vi + 3;
        }

        var geometry = new THREE.BufferGeometry();
        geometry.setAttribute('position', new THREE.BufferAttribute(positions, 3));
        geometry.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));
        geometry.setAttribute('color', new THREE.BufferAttribute(colors, 4));
        geometry.setIndex(new THREE.BufferAttribute(indices, 1));

        var material = new THREE.MeshBasicMaterial({
            map: texture || null, vertexColors: true, transparent: true,
            blending: blendMode, depthWrite: false, side: THREE.DoubleSide
        });

        var mesh = new THREE.Mesh(geometry, material);
        mesh.frustumCulled = false;

        // Add to parent group (for missile travel) or scene directly
        var container = parentGroup || scene;
        container.add(mesh);

        var particles = new Array(maxParticles);
        for (var p = 0; p < maxParticles; p++) {
            particles[p] = { alive: false, age: 0, lifetime: 0, x: 0, y: 0, z: 0, vx: 0, vy: 0, vz: 0, atlasCell: 0 };
        }

        var emitAccum = 0;
        var eType = emitter.emitterType || 0;
        var areaL = emitter.emissionAreaLength || 0;
        var areaW = emitter.emissionAreaWidth || 0;

        var camRight = new THREE.Vector3();
        var camUp = new THREE.Vector3();
        var _tmp = new THREE.Vector3();

        var sys = {
            mesh: mesh, liveCount: 0, emitter: emitter,

            update: function (dt) {
                if (dt <= 0) return;
                var speed = emitter.emissionSpeed || 1.0;
                var speedVar = emitter.speedVariation || 0;
                var vRange = emitter.verticalRange || Math.PI;
                var hRange = emitter.horizontalRange || (Math.PI * 2);
                var grav = emitter.gravity || 0;
                var midpoint = emitter.midpoint || 0.5;

                emitAccum += rate * dt;
                while (emitAccum >= 1) {
                    emitAccum -= 1;
                    for (var j = 0; j < maxParticles; j++) {
                        if (!particles[j].alive) {
                            var pp = particles[j];
                            pp.alive = true; pp.age = 0; pp.lifetime = life;

                            if (eType === 1) {
                                var radius = Math.max(areaL, areaW, 0.1);
                                var theta = Math.random() * Math.PI * 2;
                                var phi = Math.acos(2 * Math.random() - 1);
                                pp.x = radius * Math.sin(phi) * Math.cos(theta);
                                pp.y = radius * Math.sin(phi) * Math.sin(theta);
                                pp.z = radius * Math.cos(phi);
                            } else if (eType === 2) {
                                pp.x = (Math.random() - 0.5) * (areaL || 0.1);
                                pp.y = 0;
                                pp.z = (Math.random() - 0.5) * (areaW || 0.1);
                            } else {
                                pp.x = 0; pp.y = 0; pp.z = 0;
                            }

                            var s = speed + (Math.random() - 0.5) * 2 * speedVar * speed;
                            var vTheta = (Math.random() - 0.5) * hRange;
                            var vPhi = (Math.random() - 0.5) * vRange;
                            pp.vx = s * Math.sin(vTheta) * Math.cos(vPhi);
                            pp.vy = s * Math.sin(vPhi);
                            pp.vz = s * Math.cos(vTheta) * Math.cos(vPhi);
                            pp.atlasCell = cellCount > 1 ? Math.floor(Math.random() * cellCount) : 0;
                            break;
                        }
                    }
                }

                var liveCount = 0;
                var posArr = geometry.attributes.position.array;
                var uvArr = geometry.attributes.uv.array;
                var colArr = geometry.attributes.color.array;
                camera.matrixWorld.extractBasis(camRight, camUp, _tmp);

                for (var k = 0; k < maxParticles; k++) {
                    var pp = particles[k];
                    var vi3 = k * 12, vi2 = k * 8, ci = k * 16;

                    if (!pp.alive) {
                        for (var z = 0; z < 12; z++) posArr[vi3 + z] = 0;
                        for (var z = 0; z < 16; z++) colArr[ci + z] = 0;
                        continue;
                    }
                    pp.age += dt;
                    if (pp.age >= pp.lifetime) {
                        pp.alive = false;
                        for (var z = 0; z < 12; z++) posArr[vi3 + z] = 0;
                        for (var z = 0; z < 16; z++) colArr[ci + z] = 0;
                        continue;
                    }
                    liveCount++;

                    pp.vy -= grav * dt;
                    pp.x += pp.vx * dt; pp.y += pp.vy * dt; pp.z += pp.vz * dt;

                    var t = pp.age / pp.lifetime;
                    var c, sc;
                    if (t < midpoint) {
                        var f = midpoint > 0 ? t / midpoint : 0;
                        c = lerpColor(cStart, cMid, f);
                        sc = lerp(emitter.scaleStart, emitter.scaleMid, f);
                    } else {
                        var f = midpoint < 1 ? (t - midpoint) / (1 - midpoint) : 1;
                        c = lerpColor(cMid, cEnd, f);
                        sc = lerp(emitter.scaleMid, emitter.scaleEnd, f);
                    }

                    var half = sc * 0.5;
                    var rx = camRight.x * half, ry = camRight.y * half, rz = camRight.z * half;
                    var ux = camUp.x * half, uy = camUp.y * half, uz = camUp.z * half;

                    posArr[vi3 + 0] = pp.x - rx - ux; posArr[vi3 + 1] = pp.y - ry - uy; posArr[vi3 + 2] = pp.z - rz - uz;
                    posArr[vi3 + 3] = pp.x + rx - ux; posArr[vi3 + 4] = pp.y + ry - uy; posArr[vi3 + 5] = pp.z + rz - uz;
                    posArr[vi3 + 6] = pp.x + rx + ux; posArr[vi3 + 7] = pp.y + ry + uy; posArr[vi3 + 8] = pp.z + rz + uz;
                    posArr[vi3 + 9] = pp.x - rx + ux; posArr[vi3 + 10] = pp.y - ry + uy; posArr[vi3 + 11] = pp.z - rz + uz;

                    if (cellCount > 1) {
                        var col = pp.atlasCell % atlasCols, row = Math.floor(pp.atlasCell / atlasCols);
                        var u0 = col * cellW, v0 = row * cellH, u1 = u0 + cellW, v1 = v0 + cellH;
                        uvArr[vi2 + 0] = u0; uvArr[vi2 + 1] = v1; uvArr[vi2 + 2] = u1; uvArr[vi2 + 3] = v1;
                        uvArr[vi2 + 4] = u1; uvArr[vi2 + 5] = v0; uvArr[vi2 + 6] = u0; uvArr[vi2 + 7] = v0;
                    } else {
                        uvArr[vi2 + 0] = 0; uvArr[vi2 + 1] = 1; uvArr[vi2 + 2] = 1; uvArr[vi2 + 3] = 1;
                        uvArr[vi2 + 4] = 1; uvArr[vi2 + 5] = 0; uvArr[vi2 + 6] = 0; uvArr[vi2 + 7] = 0;
                    }

                    for (var v = 0; v < 4; v++) {
                        colArr[ci + v * 4] = c.r; colArr[ci + v * 4 + 1] = c.g; colArr[ci + v * 4 + 2] = c.b; colArr[ci + v * 4 + 3] = c.a;
                    }
                }

                geometry.attributes.position.needsUpdate = true;
                geometry.attributes.uv.needsUpdate = true;
                geometry.attributes.color.needsUpdate = true;
                sys.liveCount = liveCount;
            },

            dispose: function () {
                container.remove(mesh);
                geometry.dispose();
                material.dispose();
            }
        };
        return sys;
    }

    // ═══════════════════════════════════════════════════════════════
    // PHASE LOADING — spatial positioning
    // ═══════════════════════════════════════════════════════════════

    function loadSpell(entry) {
        $('#viewerStatusText').text('Loading...');
        $('#viewerEmpty').hide();
        clearScene();
        sequenceActive = false;

        $.getJSON('/Patch/VisualPreview', { entry: entry }, function (data) {
            if (!data.success) {
                $('#viewerStatusText').text('Error: ' + (data.error || 'Unknown'));
                $('#viewerEmpty').text(data.error || 'Failed to load').show();
                return;
            }

            spellData = data;
            $('#spellNameLabel').text(data.spellName || '');
            $('#viewerStatusText').text(data.spellName || 'Loaded');

            // Compute missile flight time from spell data
            if (data.timing && data.timing.missileSpeed > 0) {
                missileFlightTime = targetDistance / data.timing.missileSpeed;
            } else {
                missileFlightTime = targetDistance / 24; // default ~24 yd/s like Fireball
            }

            updatePhaseTabs();

            var phaseOrder = ['cast', 'missile', 'impact', 'channel', 'precast', 'state', 'stateDone'];
            for (var i = 0; i < phaseOrder.length; i++) {
                if (spellData.phases[phaseOrder[i]]) {
                    switchPhase(phaseOrder[i]);
                    return;
                }
            }
            $('#viewerEmpty').text('No visual phases found').show();
        }).fail(function () { $('#viewerStatusText').text('Request failed'); });
    }

    function switchPhase(phase) {
        activePhase = phase;
        sequenceActive = false;
        $('.vlab-tab').removeClass('active');
        $('.vlab-tab[data-phase="' + phase + '"]').addClass('active');
        $('#overlayPhase').text('Phase: ' + phase);
        clearScene();
        loadPhase(phase);
    }

    function clearScene() {
        for (var i = 0; i < particleSystems.length; i++) particleSystems[i].dispose();
        particleSystems = [];
        if (missileGroup) { scene.remove(missileGroup); missileGroup = null; }
        missileActive = false;
        missileT = 0;
        particleCount = 0;
        $('#overlayEmitters').text('Emitters: 0');
    }

    function loadPhase(phase) {
        if (!spellData || !spellData.phases[phase]) {
            $('#viewerEmpty').text('No data for phase: ' + phase).show();
            $('#inspectorPanel').hide();
            return;
        }

        $('#viewerEmpty').hide();
        $('#inspectorPanel').show();

        var m2List = spellData.phases[phase];
        if (!Array.isArray(m2List) || m2List.length === 0) return;

        var anchor = PHASE_ANCHOR[phase] || 'caster';
        var anchorPos;
        var parentGroup = null;

        if (anchor === 'target') {
            anchorPos = new THREE.Vector3(0, 0, -targetDistance);
        } else if (anchor === 'travel') {
            // Missile: create a group that will be translated
            missileGroup = new THREE.Group();
            missileGroup.position.set(0, 0, 0);
            scene.add(missileGroup);
            parentGroup = missileGroup;
            missileT = 0;
            missileActive = true;
            anchorPos = new THREE.Vector3(0, 0, 0); // local to group
        } else {
            anchorPos = new THREE.Vector3(0, 0, 0);
        }

        // Collect textures
        var uniqueTexFiles = [];
        var texMapPerM2 = {};
        var totalEmitters = 0;

        for (var mi = 0; mi < m2List.length; mi++) {
            var m2 = m2List[mi];
            totalEmitters += (m2.emitters || []).length;
            texMapPerM2[mi] = {};
            var textures = m2.textures || [];
            for (var ti = 0; ti < textures.length; ti++) {
                texMapPerM2[mi][textures[ti].index] = textures[ti].normalized;
                if (uniqueTexFiles.indexOf(textures[ti].normalized) === -1)
                    uniqueTexFiles.push(textures[ti].normalized);
            }
        }

        $('#overlayEmitters').text('Emitters: ' + totalEmitters + ' (M2s: ' + m2List.length + ')');

        var loaded = 0;
        var totalToLoad = uniqueTexFiles.length;

        function onAllTexturesLoaded() {
            for (var mi = 0; mi < m2List.length; mi++) {
                var m2 = m2List[mi];
                var emitters = m2.emitters || [];
                var texMap = texMapPerM2[mi] || {};

                for (var ei = 0; ei < emitters.length; ei++) {
                    var e = emitters[ei];
                    var texName = texMap[e.textureId];
                    var tex = texName ? (textureCache[texName] || null) : null;
                    e._texFilename = texName || '';
                    e._m2Idx = mi;
                    e._m2Path = m2.m2Path;

                    var sys = createParticleSystem(e, tex, parentGroup);

                    // Offset to anchor position (for non-missile phases)
                    if (!parentGroup && (anchorPos.x !== 0 || anchorPos.z !== 0)) {
                        sys.mesh.position.copy(anchorPos);
                    }

                    particleSystems.push(sys);
                }
            }

            populateEmitterSelector(m2List);
            showEmitterDetails(0);
            showTextureGallery(m2List);
        }

        if (totalToLoad === 0) {
            onAllTexturesLoaded();
        } else {
            for (var i = 0; i < uniqueTexFiles.length; i++) {
                loadTexture(uniqueTexFiles[i], function () {
                    loaded++;
                    if (loaded >= totalToLoad) onAllTexturesLoaded();
                });
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // SEQUENCE PLAYBACK
    // ═══════════════════════════════════════════════════════════════

    function startSequence() {
        if (!spellData) return;

        sequenceActive = true;
        sequencePhaseIdx = 0;
        sequenceTimer = 0;
        paused = false;
        $('#btnPause').html('<i class="fa-solid fa-pause"></i>');

        // Build phase schedule from spell timing
        var castTime = (spellData.timing && spellData.timing.castTimeMs > 0)
            ? spellData.timing.castTimeMs / 1000
            : 2.5;
        var flightTime = missileFlightTime;

        sequencePhases = [];
        if (spellData.phases.precast) sequencePhases.push({ phase: 'precast', duration: 1.0 });
        if (spellData.phases.cast) sequencePhases.push({ phase: 'cast', duration: castTime });
        if (spellData.phases.missile) sequencePhases.push({ phase: 'missile', duration: flightTime + 0.3 });
        if (spellData.phases.impact) sequencePhases.push({ phase: 'impact', duration: 1.5 });
        if (spellData.phases.channel) sequencePhases.push({ phase: 'channel', duration: castTime });
        if (spellData.phases.state) sequencePhases.push({ phase: 'state', duration: 2.0 });
        if (spellData.phases.stateDone) sequencePhases.push({ phase: 'stateDone', duration: 1.5 });

        if (sequencePhases.length === 0) {
            sequenceActive = false;
            return;
        }

        // Load first phase
        activePhase = sequencePhases[0].phase;
        clearScene();
        loadPhase(activePhase);
        updatePhaseTabs();
        $('.vlab-tab').removeClass('active');
        $('.vlab-tab[data-phase="' + activePhase + '"]').addClass('active');
        $('#overlayPhase').text('Sequence: ' + activePhase);
        $('#btnSequence').addClass('active-seq');
    }

    function updateSequence(dt) {
        if (!sequenceActive || sequencePhaseIdx >= sequencePhases.length) {
            sequenceActive = false;
            $('#btnSequence').removeClass('active-seq');
            return;
        }

        sequenceTimer += dt;
        var current = sequencePhases[sequencePhaseIdx];

        if (sequenceTimer >= current.duration) {
            sequenceTimer = 0;
            sequencePhaseIdx++;

            if (sequencePhaseIdx >= sequencePhases.length) {
                sequenceActive = false;
                $('#btnSequence').removeClass('active-seq');
                $('#overlayPhase').text('Sequence: done');
                return;
            }

            var next = sequencePhases[sequencePhaseIdx];
            activePhase = next.phase;
            clearScene();
            loadPhase(activePhase);
            $('.vlab-tab').removeClass('active');
            $('.vlab-tab[data-phase="' + activePhase + '"]').addClass('active');
            $('#overlayPhase').text('Sequence: ' + activePhase);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // HELPERS
    // ═══════════════════════════════════════════════════════════════

    function parseArgb(hex) {
        if (!hex || hex.length < 8) return { r: 1, g: 1, b: 1, a: 1 };
        return {
            a: parseInt(hex.substring(0, 2), 16) / 255, r: parseInt(hex.substring(2, 4), 16) / 255,
            g: parseInt(hex.substring(4, 6), 16) / 255, b: parseInt(hex.substring(6, 8), 16) / 255
        };
    }
    function lerpColor(a, b, t) { return { r: a.r + (b.r - a.r) * t, g: a.g + (b.g - a.g) * t, b: a.b + (b.b - a.b) * t, a: a.a + (b.a - a.a) * t }; }
    function lerp(a, b, t) { return a + (b - a) * t; }
    function argbToHexCss(hex) { return (!hex || hex.length < 8) ? '#ffffff' : '#' + hex.substring(2); }

    function loadTexture(normalizedFilename, callback) {
        if (textureCache[normalizedFilename]) { callback(textureCache[normalizedFilename]); return; }
        new THREE.TextureLoader().load(
            '/Patch/VisualTexture?file=' + encodeURIComponent(normalizedFilename),
            function (tex) { tex.minFilter = THREE.LinearFilter; tex.magFilter = THREE.LinearFilter; textureCache[normalizedFilename] = tex; callback(tex); },
            undefined,
            function () { callback(null); }
        );
    }

    function updatePhaseTabs() {
        $('.vlab-tab').each(function () {
            var phase = $(this).data('phase');
            $(this).toggleClass('has-data', !!(spellData && spellData.phases[phase]));
            $(this).toggleClass('no-data', !(spellData && spellData.phases[phase]));
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // EMITTER SELECTOR + INSPECTOR (unchanged from previous)
    // ═══════════════════════════════════════════════════════════════

    function populateEmitterSelector(m2List) {
        var sel = $('#emitterSelect').empty();
        var globalIdx = 0;
        var blendNames = { 0: 'Opaque', 1: 'Mod', 2: 'Alpha', 4: 'Additive' };
        var typeNames = { 0: 'Point', 1: 'Sphere', 2: 'Plane', 3: 'Spline' };
        for (var mi = 0; mi < m2List.length; mi++) {
            var m2 = m2List[mi], emitters = m2.emitters || [];
            var m2Label = m2.m2Path ? m2.m2Path.split(/[\\\/]/).pop() : 'M2 #' + mi;
            if (m2List.length > 1 && emitters.length > 0)
                sel.append('<option disabled>── ' + m2Label + ' ──</option>');
            for (var ei = 0; ei < emitters.length; ei++) {
                var e = emitters[ei];
                sel.append('<option value="' + globalIdx + '">Emitter ' + globalIdx + ' — ' + (blendNames[e.blendMode] || e.blendMode) + ' ' + (typeNames[e.emitterType] || e.emitterType) + '</option>');
                globalIdx++;
            }
        }
    }

    function getEmitterByGlobalIndex(idx) {
        if (!spellData || !spellData.phases[activePhase]) return null;
        var m2List = spellData.phases[activePhase];
        var gi = 0;
        for (var mi = 0; mi < m2List.length; mi++) {
            var emitters = m2List[mi].emitters || [];
            for (var ei = 0; ei < emitters.length; ei++) {
                if (gi === idx) return { emitter: emitters[ei], m2: m2List[mi] };
                gi++;
            }
        }
        return null;
    }

    function showEmitterDetails(idx) {
        var info = getEmitterByGlobalIndex(idx);
        if (!info) return;
        var e = info.emitter;
        if (info.m2.m2Path) $('#m2Info').text(info.m2.m2Path);

        var html = '';
        html += propGroup('Emission', [
            propRow('Speed', fmt(e.emissionSpeed), isAnim(e, 'emissionSpeed')),
            propRow('Variation', fmt(e.speedVariation), isAnim(e, 'speedVariation')),
            propRow('Rate', fmt(e.emissionRate) + '/s', isAnim(e, 'emissionRate')),
            propRow('Lifespan', fmt(e.lifespan) + 's', isAnim(e, 'lifespan')),
            propRow('Gravity', fmt(e.gravity), isAnim(e, 'gravity'))
        ]);
        html += propGroup('Spread &amp; area', [
            propRow('V Range', fmtRad(e.verticalRange)),
            propRow('H Range', fmtRad(e.horizontalRange)),
            propRow('Area L', fmt(e.emissionAreaLength)),
            propRow('Area W', fmt(e.emissionAreaWidth))
        ]);
        var blendN = { 0: 'Opaque', 1: 'Mod', 2: 'Alpha blend', 4: 'Additive' };
        var typeN = { 0: 'Point', 1: 'Sphere', 2: 'Plane', 3: 'Spline' };
        html += propGroup('Type', [
            propRow('Blend', blendN[e.blendMode] || e.blendMode),
            propRow('Emitter', typeN[e.emitterType] || e.emitterType),
            propRow('Texture', '#' + e.textureId)
        ]);
        html += '<div class="prop-group"><div class="prop-group-title">Color</div>';
        html += '<div class="d-flex gap-2 align-items-center" style="margin:4px 0;">';
        html += sw(e.colorStart) + '<span class="prop-name">→</span>' + sw(e.colorMid) + '<span class="prop-name">→</span>' + sw(e.colorEnd);
        html += '</div>';
        html += propRow('Start A', alphaPct(e.colorStart)) + propRow('Mid A', alphaPct(e.colorMid)) + propRow('End A', alphaPct(e.colorEnd));
        html += '</div>';
        html += propGroup('Scale', [
            propRow('Start', fmt(e.scaleStart)), propRow('Mid', fmt(e.scaleMid)), propRow('End', fmt(e.scaleEnd))
        ]);
        $('#emitterProps').html(html);
    }

    function propGroup(title, rows) { return '<div class="prop-group"><div class="prop-group-title">' + title + '</div>' + rows.join('') + '</div>'; }
    function propRow(n, v, a) { return '<div class="prop-row"><span class="prop-name">' + n + '</span><span class="prop-value' + (a ? ' animated' : '') + '">' + v + (a ? ' ⚡' : '') + '</span></div>'; }
    function fmt(v) { return v != null ? Number(v).toFixed(3) : '—'; }
    function fmtRad(v) { return v != null ? Number(v).toFixed(3) + ' (' + (Number(v) * 180 / Math.PI).toFixed(0) + '°)' : '—'; }
    function isAnim(e, p) { return e.keyframeCounts && e.keyframeCounts[p] > 1; }
    function sw(hex) { return '<span class="color-swatch" style="background:' + argbToHexCss(hex) + '" title="#' + hex + '"></span>'; }
    function alphaPct(hex) { return hex && hex.length >= 2 ? (parseInt(hex.substring(0, 2), 16) / 255 * 100).toFixed(0) + '%' : '—'; }

    function showTextureGallery(m2List) {
        var gallery = $('#textureGallery').empty();
        var seen = {};
        for (var mi = 0; mi < m2List.length; mi++) {
            var textures = m2List[mi].textures || [];
            for (var ti = 0; ti < textures.length; ti++) {
                var t = textures[ti];
                if (seen[t.normalized]) continue; seen[t.normalized] = true;
                var url = '/Patch/VisualTexture?file=' + encodeURIComponent(t.normalized);
                var info = t.blpInfo ? (t.blpInfo.width + '×' + t.blpInfo.height + ' ' + t.blpInfo.format) : 'unknown';
                var div = $('<div>');
                div.append('<img class="tex-thumb" src="' + url + '" title="' + t.filename + '\n' + info + '" />');
                div.append('<div class="tex-info">[' + t.index + '] ' + info + '</div>');
                gallery.append(div);
            }
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // EVENT HANDLERS
    // ═══════════════════════════════════════════════════════════════

    $('#btnLoadSpell').on('click', function () {
        var entry = parseInt($('#spellEntry').val());
        if (entry > 0) loadSpell(entry);
    });
    $('#spellEntry').on('keydown', function (e) {
        if (e.key === 'Enter') { e.preventDefault(); var entry = parseInt($(this).val()); if (entry > 0) loadSpell(entry); }
    });
    $('#phaseTabs').on('click', '.vlab-tab', function () {
        var phase = $(this).data('phase');
        if (spellData && spellData.phases[phase]) switchPhase(phase);
    });
    $('#emitterSelect').on('change', function () { showEmitterDetails(parseInt($(this).val())); });

    // Playback
    $('#speedSlider').on('input', function () {
        playbackSpeed = parseFloat($(this).val());
        $('#speedLabel').text(playbackSpeed.toFixed(1) + 'x');
    });
    $('#btnPause').on('click', function () {
        paused = !paused;
        $(this).html(paused ? '<i class="fa-solid fa-play"></i>' : '<i class="fa-solid fa-pause"></i>');
    });
    $('#btnResetCam').on('click', function () {
        camera.position.set(8, 5, 12);
        controls.target.set(0, 0, -targetDistance / 2);
        controls.update();
    });
    $('#btnSequence').on('click', function () {
        if (sequenceActive) { sequenceActive = false; $(this).removeClass('active-seq'); }
        else startSequence();
    });
    $('#distSlider').on('input', function () {
        var d = parseFloat($(this).val());
        updateTargetDistance(d);
        $('#distLabel').text(d.toFixed(0) + ' yd');
        // Recompute missile flight time
        if (spellData && spellData.timing && spellData.timing.missileSpeed > 0) {
            missileFlightTime = d / spellData.timing.missileSpeed;
        } else {
            missileFlightTime = d / 24;
        }
    });
    $('#btnBg').on('click', function () { cycleBgMode(); });

    // ═══════════════════════════════════════════════════════════════
    // INIT
    // ═══════════════════════════════════════════════════════════════

    initThree();
    loadTerrainPresetList();

});
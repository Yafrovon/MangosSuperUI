/* ============================================================
   MangosSuperUI — worldviewer.js
   3D World Viewer: progressive tile loading, WASD movement,
   InstancedMesh-based spatial object streaming
   Session 45: spherical load/unload, InstancedMesh, fetch queue
   ============================================================ */

(function () {
    'use strict';

    var canvas = document.getElementById('wvCanvas');
    if (!canvas) return;

    var statusEl = document.getElementById('wvStatus');
    var presetSelect = document.getElementById('wvPresetSelect');

    // ── Sky/fog — deep golden hour palette (matching WoW late afternoon) ──
    var SKY_TOP = 0x2a4f8a;
    var SKY_HORIZON = 0xe8a840;
    var FOG_COLOR = 0xc49a50;

    // ── Three.js setup ──
    var renderer = new THREE.WebGLRenderer({ canvas: canvas, antialias: true });
    renderer.setPixelRatio(window.devicePixelRatio);
    renderer.setClearColor(FOG_COLOR);
    renderer.toneMapping = THREE.LinearToneMapping;
    renderer.toneMappingExposure = 1.1;
    renderer.outputEncoding = THREE.sRGBEncoding;

    var scene = new THREE.Scene();
    var fogNear = 180, fogFar = 550;
    scene.fog = new THREE.Fog(FOG_COLOR, fogNear, fogFar);

    var camera = new THREE.PerspectiveCamera(60, 1, 0.1, 2000);
    camera.position.set(0, 30, 80);

    var controls = new THREE.OrbitControls(camera, canvas);
    controls.enableDamping = true;
    controls.dampingFactor = 0.1;
    controls.maxPolarAngle = Math.PI - 0.1;
    controls.minPolarAngle = 0.1;
    controls.minDistance = 1;
    controls.maxDistance = 5000;
    controls.enableZoom = false;
    // Free up right-click for walk mode look — move pan to middle mouse
    controls.mouseButtons = {
        LEFT: THREE.MOUSE.ROTATE,
        MIDDLE: THREE.MOUSE.PAN,
        RIGHT: null
    };

    // ── Scrollwheel → camera height (additive, constant speed) ──
    canvas.addEventListener('wheel', function (e) {
        e.preventDefault();
        var heightStep = 8;
        // Scroll up = zoom in (lower), scroll down = zoom out (higher)
        var dir = e.deltaY > 0 ? 1 : -1;
        var minY = 0.5; // allow near-ground camera
        var newY = camera.position.y + dir * heightStep;
        if (newY < minY) newY = minY;
        var dy = newY - camera.position.y;
        camera.position.y = newY;
        controls.target.y += dy;
    }, { passive: false });

    // ── WASD movement ──
    var moveKeys = {};
    var moveSpeed = 0.3;
    var sprintSpeed = 1.0;
    var sprinting = false;

    // ── Right-click look (walk mode) — direct camera rotation ──
    var rightMouseDown = false;
    var lastMouseX = 0, lastMouseY = 0;
    var MOUSE_LOOK_SENSITIVITY = 0.004;
    var walkYaw = 0;    // radians, managed directly
    var walkPitch = 0;  // radians, managed directly
    var walkLookInited = false;

    function enterWalkMode() {
        walkMode = true;
        WALK_EYE_HEIGHT = 2;
        controls.enableRotate = false;
        controls.enablePan = false;
        // Init look direction from current camera, force pitch horizontal
        var dir = new THREE.Vector3();
        camera.getWorldDirection(dir);
        walkYaw = Math.atan2(dir.x, dir.z);
        walkPitch = 0;
        walkLookInited = true;
        applyWalkLook();
    }

    function leaveWalkMode() {
        walkMode = false;
        controls.enableRotate = true;
        controls.enablePan = true;
        walkLookInited = false;
    }

    canvas.addEventListener('pointerdown', function (e) {
        if (e.button === 2 && walkMode) {
            rightMouseDown = true;
            lastMouseX = e.clientX;
            lastMouseY = e.clientY;

            // Initialize yaw/pitch from current camera direction on first use
            if (!walkLookInited) {
                var dir = new THREE.Vector3();
                camera.getWorldDirection(dir);
                walkYaw = Math.atan2(dir.x, dir.z);
                walkPitch = Math.asin(Math.max(-0.95, Math.min(0.95, dir.y)));
                walkLookInited = true;
            }
            e.preventDefault();
            e.stopImmediatePropagation();
        }
    }, true);
    canvas.addEventListener('contextmenu', function (e) {
        if (walkMode) e.preventDefault();
    });
    document.addEventListener('pointerup', function (e) {
        if (e.button === 2) rightMouseDown = false;
    });
    document.addEventListener('pointermove', function (e) {
        if (!rightMouseDown || !walkMode) return;

        var dx = e.clientX - lastMouseX;
        var dy = e.clientY - lastMouseY;
        lastMouseX = e.clientX;
        lastMouseY = e.clientY;

        walkYaw -= dx * MOUSE_LOOK_SENSITIVITY;
        walkPitch -= dy * MOUSE_LOOK_SENSITIVITY;
        walkPitch = Math.max(-1.4, Math.min(1.4, walkPitch));

        applyWalkLook();
    });

    function applyWalkLook() {
        // Set controls.target based on yaw/pitch, 10 units ahead of camera
        var lookDir = new THREE.Vector3(
            Math.sin(walkYaw) * Math.cos(walkPitch),
            Math.sin(walkPitch),
            Math.cos(walkYaw) * Math.cos(walkPitch)
        );
        controls.target.copy(camera.position).addScaledVector(lookDir, 10);
    }

    document.addEventListener('keydown', function (e) {
        // Blur any focused control so Space/Enter/arrows don't re-trigger buttons
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'BUTTON' || e.target.tagName === 'SELECT') {
            e.target.blur();
        }
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;
        moveKeys[e.code] = true;
        if (e.code === 'ShiftLeft' || e.code === 'ShiftRight') sprinting = true;
    });
    document.addEventListener('keyup', function (e) {
        moveKeys[e.code] = false;
        if (e.code === 'ShiftLeft' || e.code === 'ShiftRight') sprinting = false;
    });

    function updateMovement() {
        if (placementMode) return; // suppress camera movement during WMO placement

        var forward = new THREE.Vector3();
        camera.getWorldDirection(forward);
        forward.y = 0;
        forward.normalize();

        var right = new THREE.Vector3();
        right.crossVectors(forward, new THREE.Vector3(0, 1, 0)).normalize();

        var delta = new THREE.Vector3();
        if (moveKeys['KeyW']) delta.add(forward);
        if (moveKeys['KeyS']) delta.sub(forward);
        if (moveKeys['KeyD']) delta.add(right);
        if (moveKeys['KeyA']) delta.sub(right);
        if (moveKeys['KeyE'] || moveKeys['Space']) delta.y += 1;
        if (moveKeys['KeyQ']) delta.y -= 1;

        if (delta.lengthSq() > 0) {
            var speed = sprinting ? sprintSpeed : moveSpeed;
            delta.normalize().multiplyScalar(speed);
            camera.position.add(delta);
            controls.target.add(delta);
        }

        // Arrow keys: rotate the view around the camera
        var turnSpeed = 0.03;
        var tiltSpeed = 0.015;
        if (moveKeys['ArrowLeft'] || moveKeys['ArrowRight'] ||
            moveKeys['ArrowUp'] || moveKeys['ArrowDown']) {

            if (walkMode) {
                // In walk mode, use the direct yaw/pitch system
                if (!walkLookInited) {
                    var dir2 = new THREE.Vector3();
                    camera.getWorldDirection(dir2);
                    walkYaw = Math.atan2(dir2.x, dir2.z);
                    walkPitch = Math.asin(Math.max(-0.95, Math.min(0.95, dir2.y)));
                    walkLookInited = true;
                }
                if (moveKeys['ArrowLeft']) walkYaw += turnSpeed;
                if (moveKeys['ArrowRight']) walkYaw -= turnSpeed;
                if (moveKeys['ArrowUp']) walkPitch += tiltSpeed;
                if (moveKeys['ArrowDown']) walkPitch -= tiltSpeed;
                walkPitch = Math.max(-1.4, Math.min(1.4, walkPitch));
                applyWalkLook();
            } else {
                // In fly mode, use spherical coordinates around controls.target
                var offset = new THREE.Vector3().subVectors(controls.target, camera.position);
                var spherical = new THREE.Spherical().setFromVector3(offset);

                if (moveKeys['ArrowLeft'] || moveKeys['ArrowRight']) {
                    spherical.theta += moveKeys['ArrowLeft'] ? turnSpeed : -turnSpeed;
                }
                if (moveKeys['ArrowUp'] || moveKeys['ArrowDown']) {
                    spherical.phi += moveKeys['ArrowDown'] ? tiltSpeed : -tiltSpeed;
                    spherical.phi = Math.max(0.1, Math.min(Math.PI - 0.1, spherical.phi));
                }

                offset.setFromSpherical(spherical);
                controls.target.copy(camera.position).add(offset);
            }
        }
    }

    // ── Lighting — deep golden hour, WoW late afternoon ──
    var ambientLight = new THREE.AmbientLight(0xffe8c8, 0.35);
    scene.add(ambientLight);

    // Hemisphere: warm golden sky + muted warm ground bounce
    var hemiLight = new THREE.HemisphereLight(0xeebb66, 0x4a5530, 0.3);
    scene.add(hemiLight);

    // Low afternoon sun — deep gold, nearly horizontal
    var sun = new THREE.DirectionalLight(0xffbb55, 1.15);
    sun.position.set(-100, 28, 50);
    scene.add(sun);

    // Subtle cool fill from opposite side (sky bounce)
    var fillLight = new THREE.DirectionalLight(0x99bbdd, 0.12);
    fillLight.position.set(60, 60, -40);
    scene.add(fillLight);

    // Lighting mode
    var litMode = true;

    // ── Sky dome ──
    (function () {
        var skyGeo = new THREE.SphereGeometry(1400, 32, 16, 0, Math.PI * 2, 0, Math.PI * 0.5);
        var skyMat = new THREE.ShaderMaterial({
            side: THREE.BackSide, depthWrite: false,
            uniforms: {
                topColor: { value: new THREE.Color(SKY_TOP) },
                horizonColor: { value: new THREE.Color(SKY_HORIZON) },
                offset: { value: 10 }, exponent: { value: 0.4 }
            },
            vertexShader: 'varying vec3 vWorldPosition; void main() { vec4 wp = modelMatrix * vec4(position,1.0); vWorldPosition = wp.xyz; gl_Position = projectionMatrix * modelViewMatrix * vec4(position,1.0); }',
            fragmentShader: 'uniform vec3 topColor; uniform vec3 horizonColor; uniform float offset; uniform float exponent; varying vec3 vWorldPosition; void main() { float h = normalize(vWorldPosition + offset).y; gl_FragColor = vec4(mix(horizonColor, topColor, max(pow(max(h,0.0), exponent), 0.0)), 1.0); }'
        });
        var sky = new THREE.Mesh(skyGeo, skyMat);
        sky.renderOrder = -1;
        scene.add(sky);
    })();

    // ── Ground plane ──
    var groundPlane;
    (function () {
        var geo = new THREE.PlaneGeometry(8000, 8000);
        var mat = new THREE.MeshBasicMaterial({ color: FOG_COLOR, transparent: true, opacity: 0.5 });
        groundPlane = new THREE.Mesh(geo, mat);
        groundPlane.rotation.x = -Math.PI / 2;
        groundPlane.position.y = -5;
        groundPlane.renderOrder = -0.5;
        scene.add(groundPlane);
    })();

    // ═══════════════════════════════════════════════════════════════
    // TILE GRID STATE (terrain only)
    // ═══════════════════════════════════════════════════════════════

    var currentPreset = null;
    var tiles = {};           // key: "gx,gy" → { mesh, gridX, gridY, dx, dy, loading }
    var tileWidthMesh = 0;
    var globalMidHeight = 0;
    var globalHeightScale = 2.0;
    var centerGridX = 0;
    var centerGridY = 0;
    var mapId = 0;
    var TILE_RADIUS = 1;
    var UNLOAD_RADIUS = 3;
    var loadingTiles = {};

    // ═══════════════════════════════════════════════════════════════
    // OBJECT MANAGER — InstancedMesh spatial streaming
    // ═══════════════════════════════════════════════════════════════

    var LOAD_RADIUS = 250;       // load objects within this distance of camera (XZ plane)
    var UNLOAD_OBJ_RADIUS = 350; // unload objects beyond this distance (hysteresis buffer)
    var MAX_INSTANCES = 512;     // initial InstancedMesh capacity per model (grows as needed)

    // Active placements: id → { model, x, y, z, rotY, scale, type, rotX, rotZ, kind:'d'|'w', instanced:bool }
    var activePlacements = {};
    // Model registry: modelPath → { parts: [{geometry, material}] }
    var modelRegistry = {};
    // Instance sets: modelPath → { meshes[], idToIndex{}, indexToId{}, count, capacity, isWmo, parentGroup }
    var instanceSets = {};

    var showDoodads = true;
    var showWmos = true;
    var doodadGroup = new THREE.Group();
    doodadGroup.name = 'allDoodads';
    doodadGroup.position.y = -0.5;
    scene.add(doodadGroup);
    var wmoGroup = new THREE.Group();
    wmoGroup.name = 'allWmos';
    wmoGroup.position.y = -0.5;
    scene.add(wmoGroup);

    // ── Fetch Queue: limits concurrent model fetches ──
    var fetchQueue = [];
    var fetchInFlight = 0;
    var MAX_CONCURRENT_FETCHES = 4;
    var fetchingModels = {}; // modelPath → true

    function enqueueFetch(modelPath, priority) {
        if (fetchingModels[modelPath]) return;
        fetchingModels[modelPath] = true;
        fetchQueue.push({ path: modelPath, priority: priority || 0 });
        drainFetchQueue();
    }

    function drainFetchQueue() {
        while (fetchInFlight < MAX_CONCURRENT_FETCHES && fetchQueue.length > 0) {
            fetchQueue.sort(function (a, b) { return a.priority - b.priority; });
            var item = fetchQueue.shift();
            fetchInFlight++;

            (function (modelPath) {
                var isWmo = modelPath.toLowerCase().indexOf('.wmo') !== -1;
                var url = isWmo ? '/WorldViewer/WmoModel' : '/WorldViewer/DoodadModel';

                $.getJSON(url, { path: modelPath }, function (mdata) {
                    fetchInFlight--;
                    if (mdata.success && mdata.positions && mdata.positions.length > 0) {
                        var parts = isWmo ? buildWmoParts(mdata) : buildModelParts(mdata);
                        modelRegistry[modelPath] = { parts: parts };
                        instantiatePendingPlacements(modelPath);
                    }
                    delete fetchingModels[modelPath];
                    drainFetchQueue();
                }).fail(function () {
                    fetchInFlight--;
                    delete fetchingModels[modelPath];
                    drainFetchQueue();
                });
            })(item.path);
        }
    }

    // Create or return existing InstancedMesh set for a model
    function getOrCreateInstanceSet(modelPath, isWmo) {
        if (instanceSets[modelPath]) return instanceSets[modelPath];

        var reg = modelRegistry[modelPath];
        if (!reg) return null;

        var parentGroup = isWmo ? wmoGroup : doodadGroup;
        var meshes = [];

        for (var pi = 0; pi < reg.parts.length; pi++) {
            var part = reg.parts[pi];
            var im = new THREE.InstancedMesh(part.geometry, part.material, MAX_INSTANCES);
            im.count = 0;
            im.frustumCulled = false;
            parentGroup.add(im);
            meshes.push(im);
        }

        var set = {
            meshes: meshes,
            idToIndex: {},
            indexToId: {},
            count: 0,
            capacity: MAX_INSTANCES,
            isWmo: isWmo,
            parentGroup: parentGroup
        };
        instanceSets[modelPath] = set;
        return set;
    }

    // Build transform matrix for a placement
    function buildPlacementMatrix(placement) {
        var matrix = new THREE.Matrix4();
        var pos = new THREE.Vector3(placement.x, placement.y, placement.z);
        var rot = new THREE.Euler(0, 0, 0, 'YXZ');
        var scl = new THREE.Vector3(1, 1, 1);

        if (placement.kind === 'w') {
            // WMO: scale up to match terrain coordinate system proportions
            var wmoScale = 1.0;
            scl.set(wmoScale, wmoScale, wmoScale);
            if (placement.rotY) rot.y = (placement.rotY || 0) * Math.PI / 180;
        } else {
            // Doodad: use MDDF scale directly
            var s = (placement.scale || 1.0);
            scl.set(s, s, s);
            // M2 coordinate transform is (x, z, -y); terrain uses 90° CCW rotation
            // MDDF rotY needs 90° offset to align with the terrain coordinate system
            rot.y = ((placement.rotY || 0) - 90) * Math.PI / 180;
        }

        var quat = new THREE.Quaternion().setFromEuler(rot);
        matrix.compose(pos, quat, scl);
        return matrix;
    }

    // Add a placement to its model's InstancedMesh set
    function addInstance(modelPath, id, placement) {
        var isWmo = placement.kind === 'w';
        var set = getOrCreateInstanceSet(modelPath, isWmo);
        if (!set) return;

        // Grow if needed
        if (set.count >= set.capacity) {
            growInstanceSet(modelPath, set);
        }

        var idx = set.count;
        set.idToIndex[id] = idx;
        set.indexToId[idx] = id;
        set.count++;

        var matrix = buildPlacementMatrix(placement);

        for (var mi = 0; mi < set.meshes.length; mi++) {
            set.meshes[mi].setMatrixAt(idx, matrix);
            set.meshes[mi].count = set.count;
            set.meshes[mi].instanceMatrix.needsUpdate = true;
        }
    }

    // Remove a placement — swap with last instance for O(1)
    function removeInstance(modelPath, id) {
        var set = instanceSets[modelPath];
        if (!set) return;

        var idx = set.idToIndex[id];
        if (idx === undefined) return;

        var lastIdx = set.count - 1;

        if (idx !== lastIdx) {
            var lastId = set.indexToId[lastIdx];
            var tempMatrix = new THREE.Matrix4();

            for (var mi = 0; mi < set.meshes.length; mi++) {
                set.meshes[mi].getMatrixAt(lastIdx, tempMatrix);
                set.meshes[mi].setMatrixAt(idx, tempMatrix);
                set.meshes[mi].instanceMatrix.needsUpdate = true;
            }

            set.idToIndex[lastId] = idx;
            set.indexToId[idx] = lastId;
        }

        delete set.idToIndex[id];
        delete set.indexToId[lastIdx];
        set.count--;

        for (var mi = 0; mi < set.meshes.length; mi++) {
            set.meshes[mi].count = set.count;
        }

        if (set.count === 0) {
            disposeInstanceSet(modelPath);
        }
    }

    function growInstanceSet(modelPath, set) {
        var newCap = set.capacity * 2;
        var reg = modelRegistry[modelPath];
        if (!reg) return;

        var newMeshes = [];
        for (var pi = 0; pi < reg.parts.length; pi++) {
            var part = reg.parts[pi];
            var newIm = new THREE.InstancedMesh(part.geometry, part.material, newCap);
            newIm.count = set.count;
            newIm.frustumCulled = false;

            var oldIm = set.meshes[pi];
            var tempMat = new THREE.Matrix4();
            for (var i = 0; i < set.count; i++) {
                oldIm.getMatrixAt(i, tempMat);
                newIm.setMatrixAt(i, tempMat);
            }
            newIm.instanceMatrix.needsUpdate = true;

            set.parentGroup.remove(oldIm);
            oldIm.dispose();
            set.parentGroup.add(newIm);
            newMeshes.push(newIm);
        }
        set.meshes = newMeshes;
        set.capacity = newCap;
    }

    function disposeInstanceSet(modelPath) {
        var set = instanceSets[modelPath];
        if (!set) return;
        for (var mi = 0; mi < set.meshes.length; mi++) {
            set.parentGroup.remove(set.meshes[mi]);
            set.meshes[mi].dispose();
        }
        delete instanceSets[modelPath];
    }

    // Instance all placements waiting for a model that just loaded
    function instantiatePendingPlacements(modelPath) {
        for (var id in activePlacements) {
            var p = activePlacements[id];
            if (p.model === modelPath && !p.instanced) {
                addInstance(modelPath, id, p);
                p.instanced = true;
            }
        }
    }

    // ── Streaming tick: called every ~600ms ──
    var streamingInFlight = false;

    function streamNearbyObjects() {
        if (!currentPreset || streamingInFlight) return;

        var camX = controls.target.x;
        var camZ = controls.target.z;

        // Step 1: Client-side unload — remove objects beyond UNLOAD_OBJ_RADIUS
        var removeList = [];
        for (var id in activePlacements) {
            var p = activePlacements[id];
            var dx = p.x - camX;
            var dz = p.z - camZ;
            var dist2 = dx * dx + dz * dz;
            if (dist2 > UNLOAD_OBJ_RADIUS * UNLOAD_OBJ_RADIUS) {
                removeList.push(id);
            }
        }
        for (var ri = 0; ri < removeList.length; ri++) {
            var rmId = removeList[ri];
            var rmP = activePlacements[rmId];
            if (rmP.instanced) {
                removeInstance(rmP.model, rmId);
            }
            delete activePlacements[rmId];
        }

        // Step 2: Request objects in range from server (client handles dedup)
        streamingInFlight = true;
        var url = '/WorldViewer/NearbyObjects?preset=' + encodeURIComponent(currentPreset) +
            '&camX=' + camX.toFixed(1) + '&camZ=' + camZ.toFixed(1) +
            '&loadRadius=' + LOAD_RADIUS.toFixed(0) +
            '&globalMidHeight=' + globalMidHeight +
            '&globalHeightScale=' + globalHeightScale;

        $.getJSON(url, function (resp) {
            streamingInFlight = false;
            if (!resp.success) return;

            var newDoodads = resp.add.doodads || [];
            var newWmos = resp.add.wmos || [];

            // Add new doodads
            for (var di = 0; di < newDoodads.length; di++) {
                var d = newDoodads[di];
                if (activePlacements[d.id]) continue;
                activePlacements[d.id] = {
                    model: d.model, x: d.x, y: d.y, z: d.z,
                    rotY: d.rotY, scale: d.scale, type: d.type,
                    kind: 'd', instanced: false
                };

                if (modelRegistry[d.model]) {
                    addInstance(d.model, d.id, activePlacements[d.id]);
                    activePlacements[d.id].instanced = true;
                } else {
                    var ddx = d.x - camX, ddz = d.z - camZ;
                    enqueueFetch(d.model, ddx * ddx + ddz * ddz);
                }
            }

            // Add new WMOs
            for (var wi = 0; wi < newWmos.length; wi++) {
                var w = newWmos[wi];
                if (activePlacements[w.id]) continue;
                activePlacements[w.id] = {
                    model: w.model, x: w.x, y: w.y, z: w.z,
                    rotX: w.rotX, rotY: w.rotY, rotZ: w.rotZ,
                    kind: 'w', instanced: false
                };

                if (modelRegistry[w.model]) {
                    addInstance(w.model, w.id, activePlacements[w.id]);
                    activePlacements[w.id].instanced = true;
                } else {
                    var wdx = w.x - camX, wdz = w.z - camZ;
                    enqueueFetch(w.model, wdx * wdx + wdz * wdz);
                }
            }
        }).fail(function () {
            streamingInFlight = false;
        });
    }

    // Dispose ALL objects (preset change)
    function clearAllObjects() {
        // Clear custom WMO placements
        if (window._wmoPlacement) window._wmoPlacement.clearAll();

        // Nuclear clear of wmoGroup — removes ALL children (placed + streamed)
        while (wmoGroup.children.length > 0) {
            var child = wmoGroup.children[0];
            wmoGroup.remove(child);
            if (child.isMesh || child.isInstancedMesh) {
                if (child.geometry) child.geometry.dispose();
                if (child.material) {
                    if (child.material.map) child.material.map.dispose();
                    child.material.dispose();
                }
            }
        }


        // Remove all instances
        for (var mp in instanceSets) {
            disposeInstanceSet(mp);
        }
        activePlacements = {};
        instanceSets = {};

        // Dispose model geometry/materials
        for (var mp in modelRegistry) {
            var reg = modelRegistry[mp];
            if (reg && reg.parts) {
                for (var pi = 0; pi < reg.parts.length; pi++) {
                    if (reg.parts[pi].geometry) reg.parts[pi].geometry.dispose();
                    if (reg.parts[pi].material) {
                        if (reg.parts[pi].material.map) reg.parts[pi].material.map.dispose();
                        reg.parts[pi].material.dispose();
                    }
                }
            }
        }
        modelRegistry = {};
        fetchQueue = [];
        fetchInFlight = 0;
        fetchingModels = {};
    }

    // Toggle visibility — actually hides the parent group
    function setDoodadsVisible(vis) {
        showDoodads = vis;
        doodadGroup.visible = vis;
    }
    function setWmosVisible(vis) {
        showWmos = vis;
        wmoGroup.visible = vis;
    }

    // ═══════════════════════════════════════════════════════════════
    // DRAW DISTANCE & FOG
    // ═══════════════════════════════════════════════════════════════

    function updateFogForRadius(r) {
        var range = Math.max(0.3, r + 0.5) * (tileWidthMesh || 400);
        fogNear = range * 0.3;
        fogFar = range * 0.9;
        scene.fog.near = fogNear;
        scene.fog.far = fogFar;
        camera.far = range * 1.5;
        camera.updateProjectionMatrix();
    }

    function updateObjectRadii() {
        var baseTile = tileWidthMesh || 400;
        LOAD_RADIUS = Math.max(150, (TILE_RADIUS + 0.5) * baseTile * 0.6);
        UNLOAD_OBJ_RADIUS = LOAD_RADIUS * 1.4;
    }

    // ═══════════════════════════════════════════════════════════════
    // OPTIONS MODAL — all settings in one panel
    // ═══════════════════════════════════════════════════════════════

    (function buildOptionsModal() {
        var toolbar = document.getElementById('wvLoadBtn');
        if (!toolbar || !toolbar.parentElement) return;
        var container = toolbar.parentElement;

        // ── Options button in toolbar ──
        var optBtn = document.createElement('button');
        optBtn.textContent = '\u2699 Options';
        optBtn.className = 'btn btn-sm btn-dark';
        optBtn.style.cssText = 'margin-left:8px;font-size:12px;padding:2px 10px;';
        container.appendChild(optBtn);

        // ── Modal backdrop ──
        var backdrop = document.createElement('div');
        backdrop.style.cssText = 'display:none;position:fixed;top:0;left:0;width:100%;height:100%;' +
            'background:rgba(0,0,0,0.5);z-index:9998;';
        document.body.appendChild(backdrop);

        // ── Modal panel ──
        var modal = document.createElement('div');
        modal.style.cssText = 'display:none;position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);' +
            'background:#1e1e2a;color:#ddd;border:1px solid #444;border-radius:10px;padding:24px 28px;' +
            'z-index:9999;min-width:340px;max-width:420px;font-family:system-ui,sans-serif;' +
            'box-shadow:0 8px 32px rgba(0,0,0,0.6);';
        document.body.appendChild(modal);

        function showModal() { modal.style.display = 'block'; backdrop.style.display = 'block'; }
        function hideModal() { modal.style.display = 'none'; backdrop.style.display = 'none'; }
        optBtn.addEventListener('click', showModal);
        backdrop.addEventListener('click', hideModal);

        // ── Build modal content ──
        var html = '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:16px;">' +
            '<span style="font-size:16px;font-weight:600;color:#fff;">\u2699 Viewer Options</span>' +
            '<button id="wvOptClose" style="background:none;border:none;color:#999;font-size:20px;cursor:pointer;padding:0 4px;">\u2715</button>' +
            '</div>';

        // Row helper
        function row(label, id, type, opts) {
            var r = '<div style="display:flex;align-items:center;justify-content:space-between;margin-bottom:12px;">' +
                '<label style="font-size:13px;color:#bbb;min-width:100px;">' + label + '</label>';
            if (type === 'slider') {
                r += '<div style="display:flex;align-items:center;gap:8px;flex:1;justify-content:flex-end;">' +
                    '<input type="range" id="' + id + '" min="' + opts.min + '" max="' + opts.max + '" value="' + opts.val + '" ' +
                    'style="width:120px;cursor:pointer;">' +
                    '<span id="' + id + 'Val" style="font-size:12px;font-family:monospace;min-width:36px;text-align:right;">' + opts.display + '</span></div>';
            } else if (type === 'toggle') {
                r += '<button id="' + id + '" class="btn btn-sm ' + (opts.active ? 'btn-outline-warning active' : 'btn-outline-secondary') + '" ' +
                    'style="font-size:11px;padding:2px 12px;min-width:60px;">' + opts.textOn + '</button>';
            } else if (type === 'select') {
                r += '<select id="' + id + '" style="background:#2a2a3a;color:#ddd;border:1px solid #555;border-radius:4px;padding:2px 8px;font-size:12px;">';
                opts.options.forEach(function (o) {
                    r += '<option value="' + o.value + '"' + (o.value === opts.val ? ' selected' : '') + '>' + o.label + '</option>';
                });
                r += '</select>';
            }
            r += '</div>';
            return r;
        }

        // Divider
        function divider(title) {
            return '<div style="font-size:11px;color:#777;text-transform:uppercase;letter-spacing:1px;margin:16px 0 8px;border-bottom:1px solid #333;padding-bottom:4px;">' + title + '</div>';
        }

        html += divider('Camera & Movement');
        html += row('Speed', 'optSpeed', 'slider', { min: 0, max: 30, val: 1, display: '0.1x' });
        html += row('Walk Mode', 'optWalk', 'toggle', { active: false, textOn: 'Off' });

        html += divider('Rendering');
        html += row('Draw Distance', 'optDraw', 'slider', { min: 1, max: 30, val: 10, display: '1.0x' });
        html += row('Lighting', 'optLit', 'toggle', { active: true, textOn: 'Lit' });
        html += row('Terrain Detail', 'optDetail', 'select', {
            val: '128',
            options: [
                { value: '64', label: 'Low (1024²)' },
                { value: '128', label: 'Medium (2048²)' },
                { value: '256', label: 'High (4096²)' }
            ]
        });

        html += divider('Visibility');
        html += row('Doodads', 'optDoodads', 'toggle', { active: true, textOn: 'On' });
        html += row('Buildings', 'optWmos', 'toggle', { active: true, textOn: 'On' });
        html += row('Wireframe', 'optWire', 'toggle', { active: false, textOn: 'Off' });

        modal.innerHTML = html;

        // ── Wire up controls ──
        modal.querySelector('#wvOptClose').addEventListener('click', hideModal);

        // Speed slider
        var speedSlider = modal.querySelector('#optSpeed');
        var speedVal = modal.querySelector('#optSpeedVal');
        speedSlider.addEventListener('input', function () {
            var mult = parseInt(this.value) / 10;
            moveSpeed = 3.0 * mult;
            sprintSpeed = 10.0 * mult;
            speedVal.textContent = mult.toFixed(1) + 'x';
        });

        // Draw distance slider
        var drawSlider = modal.querySelector('#optDraw');
        var drawVal = modal.querySelector('#optDrawVal');
        drawSlider.addEventListener('input', function () {
            var drawMult = parseInt(this.value) / 10;
            TILE_RADIUS = Math.max(1, Math.round(drawMult));
            UNLOAD_RADIUS = TILE_RADIUS + 2;
            drawVal.textContent = drawMult.toFixed(1) + 'x';
            updateFogForRadius(drawMult);
            updateObjectRadii();

            if (currentPreset && tileWidthMesh > 0) {
                var cam = cameraToGrid();
                Object.keys(tiles).forEach(function (key) {
                    var t = tiles[key];
                    var dgx = t.gridX - cam.gridX;
                    var dgy = t.gridY - cam.gridY;
                    if (Math.abs(dgx) > UNLOAD_RADIUS || Math.abs(dgy) > UNLOAD_RADIUS) {
                        unloadTile(key);
                    }
                });
            }
        });

        // Walk mode toggle
        var walkBtn = modal.querySelector('#optWalk');
        walkBtn.addEventListener('click', function () {
            if (!walkMode) { enterWalkMode(); } else { leaveWalkMode(); }
            this.classList.toggle('active', walkMode);
            this.classList.toggle('btn-outline-warning', walkMode);
            this.classList.toggle('btn-outline-secondary', !walkMode);
            this.textContent = walkMode ? 'On' : 'Off';
        });

        // Lighting toggle
        var litBtn = modal.querySelector('#optLit');
        litBtn.addEventListener('click', function () {
            litMode = !litMode;
            this.classList.toggle('active', litMode);
            this.classList.toggle('btn-outline-warning', litMode);
            this.classList.toggle('btn-outline-secondary', !litMode);
            this.textContent = litMode ? 'Lit' : 'Flat';
            ambientLight.intensity = litMode ? 0.35 : 0.6;
            hemiLight.visible = litMode;
            sun.intensity = litMode ? 1.15 : 0.8;
            fillLight.visible = litMode;
        });

        // Terrain detail (resolution)
        var detailSelect = modal.querySelector('#optDetail');
        detailSelect.addEventListener('change', function () {
            textureRes = parseInt(this.value);
            if (currentPreset) loadPresetByKey(currentPreset, statusEl ? statusEl.textContent : currentPreset);
        });

        // Doodads toggle
        var doodBtn = modal.querySelector('#optDoodads');
        doodBtn.addEventListener('click', function () {
            showDoodads = !showDoodads;
            setDoodadsVisible(showDoodads);
            this.classList.toggle('active', showDoodads);
            this.classList.toggle('btn-outline-warning', showDoodads);
            this.classList.toggle('btn-outline-secondary', !showDoodads);
            this.textContent = showDoodads ? 'On' : 'Off';
        });

        // WMOs toggle
        var wmoBtn = modal.querySelector('#optWmos');
        wmoBtn.addEventListener('click', function () {
            showWmos = !showWmos;
            setWmosVisible(showWmos);
            this.classList.toggle('active', showWmos);
            this.classList.toggle('btn-outline-warning', showWmos);
            this.classList.toggle('btn-outline-secondary', !showWmos);
            this.textContent = showWmos ? 'On' : 'Off';
        });

        // Wireframe toggle
        var wireBtn = modal.querySelector('#optWire');
        wireBtn.addEventListener('click', function () {
            wireframeOn = !wireframeOn;
            // Terrain tiles
            Object.values(tiles).forEach(function (t) {
                if (t.mesh && t.mesh.material) t.mesh.material.wireframe = wireframeOn;
            });
            // WMO buildings — InstancedMesh children each have a material
            wmoGroup.traverse(function (child) {
                if (child.isMesh && child.material) child.material.wireframe = wireframeOn;
            });
            this.classList.toggle('active', wireframeOn);
            this.classList.toggle('btn-outline-warning', wireframeOn);
            this.classList.toggle('btn-outline-secondary', !wireframeOn);
            this.textContent = wireframeOn ? 'On' : 'Off';
        });

        // ESC to close
        document.addEventListener('keydown', function (e) {
            if (e.code === 'Escape' && modal.style.display !== 'none') {
                hideModal();
                e.stopPropagation();
            }
        });
    })();

    // ═══════════════════════════════════════════════════════════════
    // WMO PLACEMENT MODAL — browse & place WMO buildings
    // ═══════════════════════════════════════════════════════════════

    var placedWmos = [];       // { id, path, name, x, y, z, rotY, scale, group }
    var placementIdCounter = 0;
    var placementMode = false; // true = ghost follows mouse
    var pendingPlacement = null; // { path, name } waiting for terrain click
    var ghostGroup = null;     // THREE.Group for the translucent ghost
    var ghostRotY = 0;         // degrees, adjusted with Q/E
    var ghostScale = 1.5;

    (function buildPlacementModal() {
        var toolbar = document.getElementById('wvLoadBtn');
        if (!toolbar || !toolbar.parentElement) return;
        var container = toolbar.parentElement;

        // Backdrop
        var backdrop = document.createElement('div');
        backdrop.style.cssText = 'display:none;position:fixed;top:0;left:0;width:100%;height:100%;background:rgba(0,0,0,0.5);z-index:999;';
        document.body.appendChild(backdrop);

        // Modal
        var modal = document.createElement('div');
        modal.style.cssText = 'display:none;position:fixed;top:50%;left:50%;transform:translate(-50%,-50%);' +
            'width:900px;max-width:92vw;max-height:85vh;background:#1e2530;color:#ccc;border-radius:10px;' +
            'border:1px solid rgba(255,255,255,0.1);padding:20px;z-index:1000;overflow:hidden;' +
            'flex-direction:column;font-family:-apple-system,BlinkMacSystemFont,sans-serif;';
        document.body.appendChild(modal);

        var isOpen = false;
        function showPlacementModal() {
            backdrop.style.display = 'block';
            modal.style.display = 'flex';
            isOpen = true;
            if (!catalogLoaded) loadCatalog();
        }
        function hidePlacementModal() {
            backdrop.style.display = 'none';
            modal.style.display = 'none';
            isOpen = false;
        }
        backdrop.addEventListener('click', hidePlacementModal);

        // Toolbar button
        var placeBtn = document.createElement('button');
        placeBtn.innerHTML = '<i class="fa-solid fa-building"></i>';
        placeBtn.className = 'wv-control wv-btn';
        placeBtn.title = 'WMO Placement Tool';
        placeBtn.addEventListener('click', function () { this.blur(); showPlacementModal(); });
        container.appendChild(placeBtn);

        // Modal content
        modal.innerHTML =
            '<div style="display:flex;justify-content:space-between;align-items:center;margin-bottom:12px;">' +
            '<span style="font-size:16px;font-weight:600;color:#fff;"><i class="fa-solid fa-building"></i> WMO Placement</span>' +
            '<span id="wmoPlaceClose" style="cursor:pointer;font-size:20px;color:#888;padding:4px 8px;">&times;</span>' +
            '</div>' +
            '<div style="display:flex;gap:12px;flex:1;overflow:hidden;min-height:0;">' +
            // Left panel: tree browser
            '<div style="flex:1;display:flex;flex-direction:column;min-width:0;">' +
            '<input type="text" id="wmoCatSearch" placeholder="Search WMOs..." ' +
            'style="width:100%;padding:6px 10px;margin-bottom:8px;background:#141a22;color:#ccc;' +
            'border:1px solid rgba(255,255,255,0.12);border-radius:6px;font-size:12px;outline:none;">' +
            '<div id="wmoCatTree" style="flex:1;overflow-y:auto;font-size:12px;line-height:1.8;"></div>' +
            '</div>' +
            // Right panel: 3D preview + controls + placed list
            '<div style="width:280px;flex-shrink:0;display:flex;flex-direction:column;gap:8px;">' +
            // 3D preview canvas
            '<div style="position:relative;background:#0a0e14;border-radius:8px;overflow:hidden;height:200px;border:1px solid rgba(255,255,255,0.06);">' +
            '<canvas id="wmoPreviewCanvas" style="width:100%;height:100%;display:block;"></canvas>' +
            '<div id="wmoPreviewOverlay" style="position:absolute;top:0;left:0;right:0;bottom:0;display:flex;' +
            'align-items:center;justify-content:center;pointer-events:none;">' +
            '<span style="color:#444;font-size:12px;">Select a WMO</span>' +
            '</div>' +
            '</div>' +
            // Info row
            '<div id="wmoPreviewInfo" style="font-size:11px;color:#888;padding:0 2px;"></div>' +
            // Placement controls
            '<div id="wmoPlacementControls" style="display:none;">' +
            '<button id="wmoPlaceBtn" class="btn btn-sm btn-primary" style="width:100%;margin-bottom:4px;">' +
            '<i class="fa-solid fa-crosshairs"></i> Place on Terrain' +
            '</button>' +
            '<div style="font-size:10px;color:#666;text-align:center;line-height:1.5;">' +
            'Click to place &bull; <b>Q/E</b> rotate &bull; <b>Scroll</b> height &bull; <b>Click</b> confirm &bull; <b>Esc</b> cancel' +
            '</div>' +
            '</div>' +
            // Placed WMOs list
            '<div style="flex:1;overflow-y:auto;min-height:0;">' +
            '<div style="font-size:11px;color:#888;margin-bottom:4px;">Placed WMOs</div>' +
            '<div id="wmoPlacedList" style="font-size:11px;"></div>' +
            '</div>' +
            // Download patch MPQ
            '<button id="wmoDownloadMpq" class="btn btn-sm btn-outline-info" style="width:100%;font-size:11px;display:none;">' +
            '<i class="fa-solid fa-download"></i> Download patch-Z.MPQ for Client' +
            '</button>' +
            '</div>' +
            '</div>';

        modal.querySelector('#wmoPlaceClose').addEventListener('click', hidePlacementModal);

        // Download patch MPQ button
        modal.querySelector('#wmoDownloadMpq').addEventListener('click', function () {
            window.location.href = '/WorldViewer/DownloadPatchMpq';
        });

        // ── 3D Preview renderer (created lazily on first modal open) ──
        var previewCanvas = modal.querySelector('#wmoPreviewCanvas');
        var previewOverlay = modal.querySelector('#wmoPreviewOverlay');
        var previewRenderer = null;
        var previewScene = null;
        var previewCamera = null;
        var previewControls = null;
        var previewGroup = null;
        var previewAnimId = null;
        var previewInited = false;

        function initPreview() {
            if (previewInited) return;
            previewInited = true;

            previewRenderer = new THREE.WebGLRenderer({ canvas: previewCanvas, antialias: true, alpha: true });
            previewRenderer.setPixelRatio(window.devicePixelRatio);
            previewRenderer.setClearColor(0x0a0e14);
            previewRenderer.toneMapping = THREE.LinearToneMapping;
            previewRenderer.toneMappingExposure = 1.1;
            previewRenderer.outputEncoding = THREE.sRGBEncoding;

            previewScene = new THREE.Scene();
            previewCamera = new THREE.PerspectiveCamera(50, 1, 0.1, 5000);
            previewControls = new THREE.OrbitControls(previewCamera, previewCanvas);
            previewControls.enableDamping = true;
            previewControls.dampingFactor = 0.12;
            previewControls.autoRotate = true;
            previewControls.autoRotateSpeed = 1.5;

            // Preview lights
            var pvAmb = new THREE.AmbientLight(0xffffff, 0.5);
            previewScene.add(pvAmb);
            var pvSun = new THREE.DirectionalLight(0xffeedd, 0.9);
            pvSun.position.set(50, 80, 30);
            previewScene.add(pvSun);
            var pvFill = new THREE.DirectionalLight(0x8899bb, 0.3);
            pvFill.position.set(-30, 20, -50);
            previewScene.add(pvFill);

            previewGroup = new THREE.Group();
            previewScene.add(previewGroup);
        }

        function startPreviewLoop() {
            initPreview();
            if (previewAnimId) return;
            function tick() {
                previewAnimId = requestAnimationFrame(tick);
                previewControls.update();
                // Size the renderer to actual pixel dimensions
                var rect = previewCanvas.getBoundingClientRect();
                var w = Math.round(rect.width * window.devicePixelRatio);
                var h = Math.round(rect.height * window.devicePixelRatio);
                if (previewCanvas.width !== w || previewCanvas.height !== h) {
                    previewRenderer.setSize(rect.width, rect.height, false);
                    previewCamera.aspect = rect.width / rect.height;
                    previewCamera.updateProjectionMatrix();
                }
                previewRenderer.render(previewScene, previewCamera);
            }
            tick();
        }
        function stopPreviewLoop() {
            if (previewAnimId) { cancelAnimationFrame(previewAnimId); previewAnimId = null; }
        }

        // Start/stop preview when modal opens/closes
        var origShow = showPlacementModal;
        showPlacementModal = function () { origShow(); startPreviewLoop(); };
        var origHide = hidePlacementModal;
        hidePlacementModal = function () { origHide(); stopPreviewLoop(); };

        function clearPreview() {
            if (!previewGroup) return;
            while (previewGroup.children.length > 0) {
                var c = previewGroup.children[0];
                previewGroup.remove(c);
                if (c.geometry) c.geometry.dispose();
                if (c.material) {
                    if (c.material.map) c.material.map.dispose();
                    c.material.dispose();
                }
            }
        }

        function loadPreview3D(path) {
            initPreview();
            clearPreview();
            previewOverlay.innerHTML = '<span style="color:#888;font-size:11px;">Loading 3D model...</span>';
            previewOverlay.style.display = 'flex';

            $.getJSON('/WorldViewer/WmoModel?path=' + encodeURIComponent(path), function (data) {
                if (!data.success || !data.positions) {
                    previewOverlay.innerHTML = '<span style="color:#f66;font-size:11px;">Failed to load</span>';
                    return;
                }

                var positions = new Float32Array(data.positions);
                var normals = data.normals ? new Float32Array(data.normals) : null;
                var uvs = data.uvs ? new Float32Array(data.uvs) : null;
                var indices = new Uint32Array(data.indices);

                var pendingSubs = data.submeshes.length;

                data.submeshes.forEach(function (sub) {
                    var subIndices = indices.slice(sub.indexStart, sub.indexStart + sub.indexCount);
                    var geo = new THREE.BufferGeometry();
                    geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
                    if (normals) geo.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
                    if (uvs) geo.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));
                    geo.setIndex(new THREE.BufferAttribute(subIndices, 1));

                    function addToPreview(mat) {
                        var mesh = new THREE.Mesh(geo, mat);
                        previewGroup.add(mesh);
                        pendingSubs--;
                        if (pendingSubs <= 0) fitPreviewCamera();
                    }

                    var matOpts = {
                        side: sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
                        transparent: sub.transparent || false,
                        alphaTest: sub.transparent ? 0.5 : 0
                    };

                    if (sub.textureBase64) {
                        var img = new Image();
                        img.onload = function () {
                            var tex = new THREE.Texture(img);
                            tex.needsUpdate = true;
                            tex.wrapS = THREE.RepeatWrapping;
                            tex.wrapT = THREE.RepeatWrapping;
                            matOpts.map = tex;
                            addToPreview(new THREE.MeshStandardMaterial({
                                map: matOpts.map, side: matOpts.side, transparent: matOpts.transparent,
                                alphaTest: matOpts.alphaTest, roughness: 0.6, metalness: 0.05
                            }));
                        };
                        img.src = sub.textureBase64;
                    } else {
                        addToPreview(new THREE.MeshStandardMaterial({
                            color: 0xaaaaaa, side: matOpts.side, roughness: 0.6, metalness: 0.05
                        }));
                    }
                });

                previewOverlay.style.display = 'none';
            }).fail(function () {
                previewOverlay.innerHTML = '<span style="color:#f66;font-size:11px;">Request failed</span>';
            });
        }

        function fitPreviewCamera() {
            // Compute bounding box of all preview meshes
            var box = new THREE.Box3();
            previewGroup.traverse(function (c) {
                if (c.isMesh) {
                    c.geometry.computeBoundingBox();
                    var b = c.geometry.boundingBox.clone();
                    b.applyMatrix4(c.matrixWorld);
                    box.union(b);
                }
            });
            if (box.isEmpty()) return;

            var center = new THREE.Vector3();
            box.getCenter(center);
            var size = new THREE.Vector3();
            box.getSize(size);
            var maxDim = Math.max(size.x, size.y, size.z);
            var dist = maxDim * 1.4;

            previewCamera.position.set(center.x + dist * 0.6, center.y + dist * 0.4, center.z + dist * 0.6);
            previewControls.target.copy(center);
            previewCamera.near = dist * 0.01;
            previewCamera.far = dist * 10;
            previewCamera.updateProjectionMatrix();
        }

        // ── Catalog loading ──
        var catalogLoaded = false;
        var catalogData = [];
        var categoryTree = {};

        function loadCatalog() {
            var treeEl = modal.querySelector('#wmoCatTree');
            treeEl.innerHTML = '<span style="color:#888;">Loading WMO catalog...</span>';

            $.getJSON('/WorldViewer/WmoCatalog', function (resp) {
                if (!resp.success) {
                    treeEl.innerHTML = '<span style="color:#f66;">Failed: ' + (resp.error || 'unknown') + '</span>';
                    return;
                }
                catalogData = resp.entries;
                catalogLoaded = true;

                categoryTree = {};
                catalogData.forEach(function (e) {
                    var cat = e.category || 'Other';
                    var sub = e.subcategory || '';
                    if (!categoryTree[cat]) categoryTree[cat] = {};
                    if (!categoryTree[cat][sub]) categoryTree[cat][sub] = [];
                    categoryTree[cat][sub].push(e);
                });

                renderTree(catalogData);
            });
        }

        function renderTree(entries) {
            var treeEl = modal.querySelector('#wmoCatTree');
            var grouped = {};
            entries.forEach(function (e) {
                var cat = e.category || 'Other';
                if (!grouped[cat]) grouped[cat] = {};
                var sub = e.subcategory || '_root';
                if (!grouped[cat][sub]) grouped[cat][sub] = [];
                grouped[cat][sub].push(e);
            });

            var html = '';
            Object.keys(grouped).sort().forEach(function (cat) {
                var subs = grouped[cat];
                var totalInCat = 0;
                Object.values(subs).forEach(function (arr) { totalInCat += arr.length; });

                html += '<div class="wmo-cat" style="margin-bottom:2px;">';
                html += '<div class="wmo-cat-header" style="cursor:pointer;padding:3px 6px;background:rgba(255,255,255,0.04);' +
                    'border-radius:4px;font-weight:600;color:#ddd;" data-cat="' + cat + '">';
                html += '<i class="fa-solid fa-chevron-right" style="font-size:9px;margin-right:6px;transition:transform 0.15s;"></i>';
                html += cat + ' <span style="color:#666;font-weight:400;">(' + totalInCat + ')</span></div>';
                html += '<div class="wmo-cat-body" style="display:none;padding-left:16px;">';

                Object.keys(subs).sort().forEach(function (sub) {
                    var items = subs[sub];
                    if (sub !== '_root' && sub !== '') {
                        html += '<div class="wmo-sub" style="margin-top:2px;">';
                        html += '<div class="wmo-sub-header" style="cursor:pointer;padding:2px 4px;color:#aaa;" data-sub="' + sub + '">';
                        html += '<i class="fa-solid fa-chevron-right" style="font-size:8px;margin-right:4px;transition:transform 0.15s;"></i>';
                        html += sub + ' <span style="color:#555;">(' + items.length + ')</span></div>';
                        html += '<div class="wmo-sub-body" style="display:none;padding-left:12px;">';
                    }

                    items.forEach(function (e) {
                        html += '<div class="wmo-item" style="cursor:pointer;padding:2px 6px;border-radius:3px;white-space:nowrap;' +
                            'overflow:hidden;text-overflow:ellipsis;" data-path="' + e.path.replace(/"/g, '&quot;') + '" ' +
                            'title="' + e.path.replace(/"/g, '&quot;') + '">';
                        html += '<i class="fa-solid fa-cube" style="color:#555;margin-right:4px;font-size:10px;"></i>' + e.name;
                        html += '</div>';
                    });

                    if (sub !== '_root' && sub !== '') {
                        html += '</div></div>';
                    }
                });

                html += '</div></div>';
            });

            treeEl.innerHTML = html;

            // Category expand/collapse
            treeEl.querySelectorAll('.wmo-cat-header').forEach(function (el) {
                el.addEventListener('click', function () {
                    var body = this.nextElementSibling;
                    var icon = this.querySelector('i');
                    var show = body.style.display === 'none';
                    body.style.display = show ? 'block' : 'none';
                    icon.style.transform = show ? 'rotate(90deg)' : '';
                });
            });
            treeEl.querySelectorAll('.wmo-sub-header').forEach(function (el) {
                el.addEventListener('click', function (e) {
                    e.stopPropagation();
                    var body = this.nextElementSibling;
                    var icon = this.querySelector('i');
                    var show = body.style.display === 'none';
                    body.style.display = show ? 'block' : 'none';
                    icon.style.transform = show ? 'rotate(90deg)' : '';
                });
            });

            // Item click → 3D preview
            treeEl.querySelectorAll('.wmo-item').forEach(function (el) {
                el.addEventListener('click', function () {
                    treeEl.querySelectorAll('.wmo-item').forEach(function (x) {
                        x.style.background = '';
                        x.style.color = '';
                    });
                    this.style.background = 'rgba(74,144,217,0.2)';
                    this.style.color = '#fff';
                    selectWmo(this.getAttribute('data-path'));
                });
            });
        }

        // Search
        var searchEl = modal.querySelector('#wmoCatSearch');
        var searchTimeout = null;
        searchEl.addEventListener('input', function () {
            clearTimeout(searchTimeout);
            var q = this.value.trim().toLowerCase();
            searchTimeout = setTimeout(function () {
                if (!q) { renderTree(catalogData); return; }
                var filtered = catalogData.filter(function (e) {
                    return e.name.toLowerCase().indexOf(q) >= 0 ||
                        e.path.toLowerCase().indexOf(q) >= 0 ||
                        e.category.toLowerCase().indexOf(q) >= 0 ||
                        (e.subcategory && e.subcategory.toLowerCase().indexOf(q) >= 0);
                });
                renderTree(filtered);
                modal.querySelectorAll('.wmo-cat-body').forEach(function (b) { b.style.display = 'block'; });
                modal.querySelectorAll('.wmo-sub-body').forEach(function (b) { b.style.display = 'block'; });
                modal.querySelectorAll('.wmo-cat-header i, .wmo-sub-header i').forEach(function (i) {
                    i.style.transform = 'rotate(90deg)';
                });
            }, 200);
        });

        // ── Selection & Info ──
        var selectedWmoPath = null;
        var selectedWmoData = null; // cached WmoModel response for ghost building

        function selectWmo(path) {
            selectedWmoPath = path;
            var infoEl = modal.querySelector('#wmoPreviewInfo');
            var controls = modal.querySelector('#wmoPlacementControls');

            infoEl.innerHTML = '<span style="color:#888;">Loading...</span>';
            controls.style.display = 'none';

            // Load 3D preview
            loadPreview3D(path);

            // Load metadata
            $.getJSON('/WorldViewer/WmoPreview?path=' + encodeURIComponent(path), function (resp) {
                if (!resp.success) {
                    infoEl.innerHTML = '<span style="color:#f66;">Failed</span>';
                    return;
                }
                infoEl.innerHTML =
                    '<span style="color:#fff;font-weight:600;">' + resp.name + '</span> &mdash; ' +
                    resp.groups + ' groups, ' + resp.materials + ' mats &mdash; ' +
                    '<span style="color:#aaa;">' + resp.sizeX + '×' + resp.sizeY + '×' + resp.sizeZ + ' yd</span>';
                controls.style.display = 'block';
            });

            // Pre-cache WmoModel data for ghost
            selectedWmoData = null;
            $.getJSON('/WorldViewer/WmoModel?path=' + encodeURIComponent(path), function (data) {
                if (data.success && data.positions) selectedWmoData = data;
            });
        }

        // ── Ghost placement system ──
        var ghostHeightOffset = 0; // manual height adjustment via scroll

        function buildGhostGroup(wmoData, callback) {
            var group = new THREE.Group();
            var positions = new Float32Array(wmoData.positions);
            var normals = wmoData.normals ? new Float32Array(wmoData.normals) : null;
            var uvs = wmoData.uvs ? new Float32Array(wmoData.uvs) : null;
            var indices = new Uint32Array(wmoData.indices);
            var pending = wmoData.submeshes.length;

            wmoData.submeshes.forEach(function (sub) {
                var subIdx = indices.slice(sub.indexStart, sub.indexStart + sub.indexCount);
                var geo = new THREE.BufferGeometry();
                geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
                if (normals) geo.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
                if (uvs) geo.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));
                geo.setIndex(new THREE.BufferAttribute(subIdx, 1));

                function addMesh(mat) {
                    mat.transparent = true;
                    mat.opacity = 0.45;
                    mat.depthWrite = false;
                    var mesh = new THREE.Mesh(geo, mat);
                    group.add(mesh);
                    pending--;
                    if (pending <= 0 && callback) callback(group);
                }

                if (sub.textureBase64) {
                    var img = new Image();
                    img.onload = function () {
                        var tex = new THREE.Texture(img);
                        tex.needsUpdate = true;
                        tex.wrapS = THREE.RepeatWrapping;
                        tex.wrapT = THREE.RepeatWrapping;
                        addMesh(new THREE.MeshStandardMaterial({
                            map: tex,
                            side: sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
                            roughness: 0.6, metalness: 0.0, color: 0x88bbff
                        }));
                    };
                    img.src = sub.textureBase64;
                } else {
                    addMesh(new THREE.MeshStandardMaterial({
                        color: 0x6699cc,
                        side: sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
                        roughness: 0.6, metalness: 0.0
                    }));
                }
            });
        }

        function destroyGhost() {
            if (!ghostGroup) return;
            scene.remove(ghostGroup);
            ghostGroup.traverse(function (c) {
                if (c.isMesh) {
                    if (c.geometry) c.geometry.dispose();
                    if (c.material) {
                        if (c.material.map) c.material.map.dispose();
                        c.material.dispose();
                    }
                }
            });
            ghostGroup = null;
        }

        function enterPlacementMode() {
            if (!selectedWmoPath || !selectedWmoData) return;
            pendingPlacement = { path: selectedWmoPath, name: selectedWmoPath.split('\\').pop().replace('.wmo', '') };
            placementMode = true;
            ghostRotY = 0;
            ghostScale = 1.5;
            ghostHeightOffset = 0;
            hidePlacementModal();
            canvas.style.cursor = 'crosshair';
            if (statusEl) statusEl.textContent = 'Move mouse to position \u2022 Q/E rotate \u2022 Scroll adjust height \u2022 Click to place \u2022 Esc cancel';

            // Build ghost
            buildGhostGroup(selectedWmoData, function (g) {
                ghostGroup = g;
                ghostGroup.scale.setScalar(ghostScale);
                ghostGroup.visible = false; // shown on first mousemove
                scene.add(ghostGroup);
            });
        }

        function exitPlacementMode() {
            placementMode = false;
            pendingPlacement = null;
            destroyGhost();
            canvas.style.cursor = '';
            if (statusEl) statusEl.textContent = '';
        }

        function confirmPlacement() {
            if (!ghostGroup || !pendingPlacement) return;

            var id = ++placementIdCounter;
            var placement = {
                id: id,
                dbId: null, // set after save
                path: pendingPlacement.path,
                name: pendingPlacement.name,
                x: ghostGroup.position.x,
                y: ghostGroup.position.y,
                z: ghostGroup.position.z,
                rotY: ghostRotY,
                scale: ghostScale
            };

            placedWmos.push(placement);

            // Save to database
            savePlacementToDb(placement);

            // Spawn solid copy
            spawnPlacedWmo(placement);
            updatePlacedList();

            // Destroy ghost and stay in placement mode for multi-place
            destroyGhost();
            if (statusEl) statusEl.textContent = 'Placed ' + placement.name + '. Move to place another or Esc to stop.';

            // Rebuild ghost for next placement
            buildGhostGroup(selectedWmoData, function (g) {
                ghostGroup = g;
                ghostGroup.scale.setScalar(ghostScale);
                ghostGroup.rotation.y = ghostRotY * Math.PI / 180;
                ghostGroup.visible = false;
                scene.add(ghostGroup);
            });
        }

        function savePlacementToDb(placement) {
            if (!currentPreset) return;
            $.ajax({
                url: '/WorldViewer/SavePlacement',
                type: 'POST',
                contentType: 'application/json',
                data: JSON.stringify({
                    preset: currentPreset,
                    mapId: 0,
                    wmoPath: placement.path,
                    wmoName: placement.name,
                    meshX: placement.x,
                    meshY: placement.y,
                    meshZ: placement.z,
                    rotY: placement.rotY,
                    scale: placement.scale
                }),
                success: function (resp) {
                    if (resp.success && resp.id) {
                        placement.dbId = resp.id;
                        updatePlacedList(); // re-render so commit button appears
                    }
                }
            });
        }

        // ── Place button ──
        modal.querySelector('#wmoPlaceBtn').addEventListener('click', function () {
            enterPlacementMode();
        });

        // ── Mouse move — update ghost position ──
        var placementRaycaster = new THREE.Raycaster();
        canvas.addEventListener('mousemove', function (e) {
            if (!placementMode || !ghostGroup) return;

            var rect = canvas.getBoundingClientRect();
            var mouse = new THREE.Vector2(
                ((e.clientX - rect.left) / rect.width) * 2 - 1,
                -((e.clientY - rect.top) / rect.height) * 2 + 1
            );

            placementRaycaster.setFromCamera(mouse, camera);
            var terrainMeshes = [];
            Object.values(tiles).forEach(function (t) { if (t.mesh) terrainMeshes.push(t.mesh); });
            var hits = placementRaycaster.intersectObjects(terrainMeshes);

            if (hits.length > 0) {
                ghostGroup.position.set(hits[0].point.x, hits[0].point.y + ghostHeightOffset, hits[0].point.z);
                ghostGroup.visible = true;
            } else {
                ghostGroup.visible = false;
            }
        });

        // ── Click — confirm placement ──
        canvas.addEventListener('click', function (e) {
            if (!placementMode || !ghostGroup || !ghostGroup.visible) return;
            // Don't place if this was a drag (orbit controls)
            confirmPlacement();
        });

        // ── Keyboard — Q/E rotate, Esc cancel ──
        document.addEventListener('keydown', function (e) {
            if (placementMode && ghostGroup) {
                var rotStep = e.shiftKey ? 45 : 15;
                if (e.code === 'KeyQ') {
                    ghostRotY = (ghostRotY - rotStep + 360) % 360;
                    ghostGroup.rotation.y = ghostRotY * Math.PI / 180;
                    e.preventDefault();
                } else if (e.code === 'KeyE') {
                    ghostRotY = (ghostRotY + rotStep) % 360;
                    ghostGroup.rotation.y = ghostRotY * Math.PI / 180;
                    e.preventDefault();
                }
            }

            if (e.code === 'Escape' && placementMode) {
                exitPlacementMode();
                e.stopPropagation();
            }
            if (e.code === 'Escape' && isOpen) {
                hidePlacementModal();
                e.stopPropagation();
            }
        });

        // ── Scroll — adjust ghost height offset (only in placement mode) ──
        canvas.addEventListener('wheel', function (e) {
            if (!placementMode || !ghostGroup) return;
            e.preventDefault();
            e.stopPropagation();
            ghostHeightOffset += e.deltaY > 0 ? -1 : 1;
            ghostGroup.position.y += e.deltaY > 0 ? -1 : 1;
        }, { capture: true, passive: false });

        // ── Spawn final placed WMO (solid) ──
        function spawnPlacedWmo(placement) {
            $.getJSON('/WorldViewer/WmoModel?path=' + encodeURIComponent(placement.path), function (data) {
                if (!data.success || !data.positions) return;

                var positions = new Float32Array(data.positions);
                var normals = data.normals ? new Float32Array(data.normals) : null;
                var uvs = data.uvs ? new Float32Array(data.uvs) : null;
                var indices = new Uint32Array(data.indices);

                data.submeshes.forEach(function (sub) {
                    var subIdx = indices.slice(sub.indexStart, sub.indexStart + sub.indexCount);
                    var geo = new THREE.BufferGeometry();
                    geo.setAttribute('position', new THREE.BufferAttribute(positions, 3));
                    if (normals) geo.setAttribute('normal', new THREE.BufferAttribute(normals, 3));
                    if (uvs) geo.setAttribute('uv', new THREE.BufferAttribute(uvs, 2));
                    geo.setIndex(new THREE.BufferAttribute(subIdx, 1));

                    var matOpts = {
                        side: sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
                        transparent: sub.transparent || false,
                        alphaTest: sub.transparent ? 0.5 : 0
                    };

                    if (sub.textureBase64) {
                        var img = new Image();
                        img.onload = function () {
                            var tex = new THREE.Texture(img);
                            tex.needsUpdate = true;
                            tex.wrapS = THREE.RepeatWrapping;
                            tex.wrapT = THREE.RepeatWrapping;
                            tex.anisotropy = maxAnisotropy;
                            matOpts.map = tex;
                            var mat = makeWmoMaterial(matOpts);
                            addPlacedMesh(geo, mat, placement);
                        };
                        img.src = sub.textureBase64;
                    } else {
                        var mat = makeWmoMaterial(matOpts);
                        addPlacedMesh(geo, mat, placement);
                    }
                });
            });
        }

        function addPlacedMesh(geo, mat, placement) {
            // Don't add if placement was deleted while async load was in-flight
            if (placement.cancelled) {
                geo.dispose();
                if (mat.map) mat.map.dispose();
                mat.dispose();
                return;
            }
            var mesh = new THREE.Mesh(geo, mat);
            mesh.position.set(placement.x, placement.y, placement.z);
            mesh.rotation.y = placement.rotY * Math.PI / 180;
            mesh.scale.setScalar(placement.scale);
            mesh.userData.placementId = placement.id;
            wmoGroup.add(mesh);
        }
        // ── Placed list UI ──
        function updatePlacedList() {
            var listEl = modal.querySelector('#wmoPlacedList');
            if (placedWmos.length === 0) {
                listEl.innerHTML = '<span style="color:#555;">None yet</span>';
                return;
            }
            var html = '';
            placedWmos.forEach(function (p) {
                html += '<div style="display:flex;justify-content:space-between;align-items:center;padding:3px 0;' +
                    'border-bottom:1px solid rgba(255,255,255,0.05);gap:4px;">';
                html += '<span style="overflow:hidden;text-overflow:ellipsis;white-space:nowrap;flex:1;" title="' + p.path + '">' +
                    '<i class="fa-solid fa-cube" style="color:#4a90d9;margin-right:4px;font-size:9px;"></i>' + p.name +
                    ' <span style="color:#555;">(' + p.rotY + '°)</span></span>';
                if (p.dbId && !p.committed) {
                    html += '<span class="wmo-commit" data-id="' + p.id + '" style="cursor:pointer;color:#4a4;padding:0 4px;" title="Commit to Game World">' +
                        '<i class="fa-solid fa-globe" style="font-size:10px;"></i></span>';
                } else if (p.committed) {
                    html += '<span style="color:#4a4;padding:0 4px;font-size:9px;" title="Committed to game world">' +
                        '<i class="fa-solid fa-check"></i></span>';
                }
                html += '<span class="wmo-remove" data-id="' + p.id + '" style="cursor:pointer;color:#f66;padding:0 4px;" title="Remove">' +
                    '<i class="fa-solid fa-trash" style="font-size:10px;"></i></span>';
                html += '</div>';
            });
            listEl.innerHTML = html;

            listEl.querySelectorAll('.wmo-remove').forEach(function (el) {
                el.addEventListener('click', function () {
                    removePlacedWmo(parseInt(this.getAttribute('data-id')));
                });
            });

            listEl.querySelectorAll('.wmo-commit').forEach(function (el) {
                el.addEventListener('click', function () {
                    var localId = parseInt(this.getAttribute('data-id'));
                    commitPlacementToWorld(localId);
                });
            });

            // Show download button if ANY placement has been committed
            var hasCommitted = placedWmos.some(function (p) { return p.committed; });
            var dlBtn = modal.querySelector('#wmoDownloadMpq');
            if (dlBtn) dlBtn.style.display = hasCommitted ? 'block' : 'none';
        }

        function commitPlacementToWorld(localId) {
            var p = placedWmos.find(function (x) { return x.id === localId; });
            if (!p || !p.dbId) return;

            $.ajax({
                url: '/WorldViewer/CommitToWorld',
                type: 'POST',
                contentType: 'application/json',
                data: JSON.stringify({ placementDbId: p.dbId }),
                success: function (resp) {
                    if (resp.success) {
                        p.committed = true;
                        updatePlacedList(); // this now handles download button visibility
                        var msg = 'Committed ' + p.name + ' to game world via ' + (resp.method || 'unknown') + '!';
                        if (resp.adtPath) msg += ' ADT: ' + resp.adtPath;
                        if (resp.patchMpqBuilt) msg += ' — patch-Z.MPQ ready for download';
                        if (statusEl) statusEl.textContent = msg;
                    } else {
                        alert('Commit failed: ' + (resp.error || 'unknown error'));
                    }
                },
                error: function () {
                    alert('Commit request failed');
                }
            });
        }

        function removePlacedWmo(id) {
            var removed = placedWmos.find(function (p) { return p.id === id; });
            if (removed) {
                // Flag as cancelled so in-flight async loads won't add meshes
                removed.cancelled = true;

                if (removed.dbId) {
                    $.ajax({
                        url: '/WorldViewer/DeletePlacement',
                        type: 'POST',
                        contentType: 'application/json',
                        data: JSON.stringify({ id: removed.dbId })
                    });
                }
            }

            placedWmos = placedWmos.filter(function (p) { return p.id !== id; });

            function cleanupMeshes() {
                var toRemove = [];
                wmoGroup.traverse(function (child) {
                    if (child.isMesh && child.userData.placementId === id) toRemove.push(child);
                });
                toRemove.forEach(function (m) {
                    wmoGroup.remove(m);
                    if (m.geometry) m.geometry.dispose();
                    if (m.material) {
                        if (m.material.map) m.material.map.dispose();
                        m.material.dispose();
                    }
                });
                return toRemove.length;
            }

            cleanupMeshes();
            // Second pass after 500ms to catch meshes from in-flight texture loads
            setTimeout(cleanupMeshes, 500);
            updatePlacedList();
        }

        updatePlacedList();

        // ── Load saved placements from DB on preset load ──
        // This is called from loadPresetByKey after terrain loads
        window._wmoPlacement = {
            loadSaved: function () {
                if (!currentPreset) return;
                $.getJSON('/WorldViewer/LoadPlacements?preset=' + encodeURIComponent(currentPreset), function (resp) {
                    if (!resp.success || !resp.placements) return;
                    resp.placements.forEach(function (row) {
                        var id = ++placementIdCounter;
                        var placement = {
                            id: id,
                            dbId: row.id,
                            path: row.wmoPath,
                            name: row.wmoName || row.wmoPath.split('\\').pop().replace('.wmo', ''),
                            x: row.meshX,
                            y: row.meshY,
                            z: row.meshZ,
                            rotY: row.rotY,
                            scale: row.scale,
                            committed: !!row.committed
                        };
                        placedWmos.push(placement);
                        spawnPlacedWmo(placement);
                    });
                    if (resp.count > 0) updatePlacedList();
                });
            },
            clearAll: function () {
                // Clear placed WMOs when switching presets
                placedWmos.forEach(function (p) {
                    var toRemove = [];
                    wmoGroup.traverse(function (child) {
                        if (child.isMesh && child.userData.placementId === p.id) toRemove.push(child);
                    });
                    toRemove.forEach(function (m) {
                        wmoGroup.remove(m);
                        if (m.geometry) m.geometry.dispose();
                        if (m.material) {
                            if (m.material.map) m.material.map.dispose();
                            m.material.dispose();
                        }
                    });
                });
                placedWmos = [];
                placementIdCounter = 0;
                updatePlacedList();
            }
        };
    })();

    // ═══════════════════════════════════════════════════════════════
    // WALK MODE — terrain collision via raycasting
    // ═══════════════════════════════════════════════════════════════

    var walkMode = false;
    var WALK_EYE_HEIGHT = 2; // close to ground, WoW-like perspective
    var terrainRaycaster = new THREE.Raycaster();
    var rayDown = new THREE.Vector3(0, -1, 0);

    function updateWalkMode() {
        if (!walkMode) return;

        // Collect all terrain meshes
        var terrainMeshes = [];
        Object.values(tiles).forEach(function (t) {
            if (t.mesh) terrainMeshes.push(t.mesh);
        });
        if (terrainMeshes.length === 0) return;

        // Cast ray downward from high above the camera's XZ position
        var rayOrigin = new THREE.Vector3(camera.position.x, 500, camera.position.z);
        terrainRaycaster.set(rayOrigin, rayDown);
        terrainRaycaster.far = 1000;

        var hits = terrainRaycaster.intersectObjects(terrainMeshes);
        if (hits.length > 0) {
            var groundY = hits[0].point.y;
            var targetY = groundY + WALK_EYE_HEIGHT;
            // Smoothly approach ground level — move camera AND target by same delta
            // This preserves the look direction (pitch) set by mouse look
            var dy = (targetY - camera.position.y) * 0.3;
            camera.position.y += dy;
            controls.target.y += dy;
        }
    }

    // Inject walk mode toggle into toolbar (kept as toolbar shortcut too)
    (function () {
        var toolbar = document.getElementById('wvLoadBtn');
        if (!toolbar || !toolbar.parentElement) return;
        var container = toolbar.parentElement;

        var walkBtn = document.createElement('button');
        walkBtn.textContent = 'Walk';
        walkBtn.className = 'btn btn-sm btn-dark';
        walkBtn.style.cssText = 'margin-left:8px;font-size:12px;padding:2px 8px;';
        walkBtn.title = 'Toggle walk mode (snap to terrain)';
        walkBtn.addEventListener('click', function () {
            if (!walkMode) { enterWalkMode(); } else { leaveWalkMode(); }
            this.className = walkMode ? 'btn btn-sm btn-primary' : 'btn btn-sm btn-dark';
            this.blur(); // prevent Space bar from re-toggling
            // Sync modal button if open
            var modalWalk = document.getElementById('optWalk');
            if (modalWalk) {
                modalWalk.classList.toggle('active', walkMode);
                modalWalk.classList.toggle('btn-outline-warning', walkMode);
                modalWalk.classList.toggle('btn-outline-secondary', !walkMode);
                modalWalk.textContent = walkMode ? 'On' : 'Off';
            }
        });
        container.appendChild(walkBtn);

        // "Map" button — link back to World Map
        var mapBtn = document.createElement('button');
        mapBtn.innerHTML = '<i class="fa-solid fa-map"></i>';
        mapBtn.className = 'btn btn-sm btn-dark';
        mapBtn.style.cssText = 'margin-left:8px;font-size:12px;padding:2px 8px;';
        mapBtn.title = 'Open World Map';
        mapBtn.addEventListener('click', function () {
            this.blur();
            window.location.href = '/WorldMap';
        });
        container.appendChild(mapBtn);
    })();

    // ═══════════════════════════════════════════════════════════════
    // TILE GRID MANAGEMENT (terrain only — no doodads)
    // ═══════════════════════════════════════════════════════════════

    var clock = new THREE.Clock();
    var fpsCounter = 0, currentFps = 0;

    function tileKey(gx, gy) { return gx + ',' + gy; }

    // ── Resize ──
    function resize() {
        var parent = canvas.parentElement;
        var w = parent.clientWidth;
        var h = parent.clientHeight || (window.innerHeight - 130);
        camera.aspect = w / h;
        camera.updateProjectionMatrix();
        renderer.setSize(w, h);
    }
    window.addEventListener('resize', resize);
    resize();

    // ── Presets ──
    $.getJSON('/WorldViewer/Presets', function (data) {
        if (data.success && data.presets) {
            presetSelect.innerHTML = '';
            data.presets.forEach(function (p) {
                var opt = document.createElement('option');
                opt.value = p.key;
                opt.textContent = p.name;
                presetSelect.appendChild(opt);
            });
        }
        // After presets load, check for URL-based coordinate teleport
        checkUrlTeleport();
    });

    // ── Event bindings ──
    document.getElementById('wvLoadBtn').addEventListener('click', loadPreset);

    // ═══════════════════════════════════════════════════════════════
    // URL TELEPORT — load from World Map coordinates
    // ═══════════════════════════════════════════════════════════════

    var pendingTeleport = null; // { meshX, meshZ } — set before load, applied after terrain ready

    function checkUrlTeleport() {
        var params = new URLSearchParams(window.location.search);
        var pMapId = params.get('mapId');
        var pGridX = params.get('gridX');
        var pGridY = params.get('gridY');

        if (pMapId === null || pGridX === null || pGridY === null) return;

        var mi = parseInt(pMapId), gx = parseInt(pGridX), gy = parseInt(pGridY);
        if (isNaN(mi) || isNaN(gx) || isNaN(gy)) return;

        // Build synthetic preset
        var syntheticPreset = '@' + mi + '_' + gx + '_' + gy;

        // Compute camera offset from world coords if provided
        var pWorldX = params.get('worldX');
        var pWorldY = params.get('worldY');
        if (pWorldX !== null && pWorldY !== null) {
            var worldX = parseFloat(pWorldX);
            var worldY = parseFloat(pWorldY);
            if (!isNaN(worldX) && !isNaN(worldY)) {
                // Tile center in WoW world coords
                // gridX = gy_vmangos = floor(32 - worldX / 533.33)  → worldX_center = (32 - gridX - 0.5) * 533.33
                // gridY = gx_vmangos = floor(32 - worldY / 533.33)  → worldY_center = (32 - gridY - 0.5) * 533.33
                var TILE_YARDS = 533.33333;
                var tileCenterWX = (32 - gx - 0.5) * TILE_YARDS;
                var tileCenterWY = (32 - gy - 0.5) * TILE_YARDS;
                // Mesh X is inverted relative to worldX, Z inverted relative to worldY
                pendingTeleport = {
                    meshX: tileCenterWX - worldX,
                    meshZ: tileCenterWY - worldY
                };
            }
        }

        // Auto-load using the synthetic preset
        currentPreset = syntheticPreset;
        setStatus('Teleporting...');
        loadPresetByKey(syntheticPreset, syntheticPreset);

        // Clean the URL so refresh doesn't re-teleport
        if (window.history.replaceState) {
            window.history.replaceState({}, '', window.location.pathname);
        }
    }

    // ═══════════════════════════════════════════════════════════════
    // INITIAL LOAD
    // ═══════════════════════════════════════════════════════════════

    function loadPreset() {
        var preset = presetSelect.value;
        if (!preset) return;
        var label = presetSelect.options[presetSelect.selectedIndex].textContent;
        pendingTeleport = null; // normal preset = center camera
        loadPresetByKey(preset, label);
    }

    function loadPresetByKey(preset, label) {
        currentPreset = preset;

        setStatus('Loading terrain...');

        // Clear terrain tiles
        Object.keys(tiles).forEach(function (key) { unloadTile(key); });
        tiles = {};
        loadingTiles = {};

        // Clear all objects
        clearAllObjects();

        // Step 1: initial 3×3 via multi-tile heightmap
        $.getJSON('/WorldViewer/Heightmap?preset=' + encodeURIComponent(preset) + '&tileRadius=1', function (hm) {
            if (!hm.success) { setStatus('Heightmap failed: ' + hm.error); return; }

            tileWidthMesh = hm.tileWidthMesh;
            globalMidHeight = hm.midHeight;
            globalHeightScale = hm.heightScale;
            updateObjectRadii();

            var centerTile = hm.tiles.find(function (t) { return t.dx === 0 && t.dy === 0; });
            if (centerTile) {
                centerGridX = centerTile.gridX;
                centerGridY = centerTile.gridY;
            }

            var texturesToLoad = [];
            hm.tiles.forEach(function (tile) {
                var key = tileKey(tile.gridX, tile.gridY);
                var geo = buildTerrainGeometry(tile);
                var entry = {
                    mesh: null, gridX: tile.gridX, gridY: tile.gridY,
                    dx: tile.dx, dy: tile.dy, geo: geo, loading: false
                };
                tiles[key] = entry;
                texturesToLoad.push(entry);
            });

            var texLoaded = 0;
            texturesToLoad.forEach(function (entry) {
                loadTileTexture(entry, function () {
                    texLoaded++;
                    setStatus('Textures: ' + texLoaded + '/' + texturesToLoad.length);
                    if (texLoaded >= texturesToLoad.length) {
                        updateFogForRadius(TILE_RADIUS);
                        setStatus(label || hm.label || preset);
                        // Load water for all initial tiles
                        texturesToLoad.forEach(function (e) { loadTileWater(e); });

                        // Load saved WMO placements for this preset
                        loadSavedPlacements();

                        // Apply teleport camera offset if pending
                        if (pendingTeleport) {
                            camera.position.set(pendingTeleport.meshX, 30, pendingTeleport.meshZ);
                            controls.target.set(pendingTeleport.meshX, 25, pendingTeleport.meshZ - 10);

                            // Auto-engage walk mode for ground-level exploration
                            if (!walkMode) {
                                enterWalkMode();
                                var walkBtn = document.querySelector('.btn-sm[title*="walk"]');
                                if (walkBtn) walkBtn.className = 'btn btn-sm btn-primary';
                            }

                            pendingTeleport = null;
                        }
                    }
                });
            });
        });
    }

    // Material factory — returns lit or unlit material based on mode
    function makeTerrainMaterial(opts) {
        if (litMode) {
            return new THREE.MeshStandardMaterial({
                map: opts.map || null,
                color: opts.color || 0xffffff,
                side: THREE.FrontSide,
                roughness: 0.85,   // slightly glossy for warm light response
                metalness: 0.0,
                fog: true,
                wireframe: wireframeOn
            });
        }
        return new THREE.MeshBasicMaterial({
            map: opts.map || null,
            color: opts.color || 0xffffff,
            side: THREE.FrontSide,
            fog: true,
            wireframe: wireframeOn
        });
    }

    function makeDoodadMaterial(opts) {
        if (litMode) {
            return new THREE.MeshStandardMaterial({
                map: opts.map || null,
                color: opts.color || 0x808080,
                side: opts.side || THREE.DoubleSide,
                alphaTest: opts.alphaTest || 0,
                transparent: opts.transparent || false,
                depthWrite: true,
                roughness: 0.7,
                metalness: 0.0,
                fog: true
            });
        }
        return new THREE.MeshBasicMaterial({
            map: opts.map || null,
            color: opts.color || 0x808080,
            side: opts.side || THREE.DoubleSide,
            alphaTest: opts.alphaTest || 0,
            transparent: opts.transparent || false,
            depthWrite: true,
            fog: true
        });
    }

    function makeWmoMaterial(opts) {
        if (litMode) {
            return new THREE.MeshStandardMaterial({
                map: opts.map || null,
                color: opts.color || 0xaaaaaa,
                side: opts.side || THREE.FrontSide,
                alphaTest: opts.alphaTest || 0,
                transparent: opts.transparent || false,
                depthWrite: true,
                roughness: 0.5,       // buildings get a bit of sheen
                metalness: 0.05,
                fog: true,
                wireframe: wireframeOn
            });
        }
        return new THREE.MeshBasicMaterial({
            map: opts.map || null,
            color: opts.color || 0xaaaaaa,
            side: opts.side || THREE.FrontSide,
            alphaTest: opts.alphaTest || 0,
            transparent: opts.transparent || false,
            depthWrite: true,
            fog: true,
            wireframe: wireframeOn
        });
    }

    // Max anisotropy for sharp ground textures at oblique angles
    var maxAnisotropy = renderer.capabilities.getMaxAnisotropy();
    var textureRes = 128; // pixels per chunk: 128=2048², 256=4096², 512=8192²
    var wireframeOn = false;

    function loadTileTexture(entry, callback) {
        var url = '/WorldViewer/Textures?preset=' + encodeURIComponent(currentPreset) +
            '&tileGridX=' + entry.gridX + '&tileGridY=' + entry.gridY +
            '&pixelsPerChunk=' + textureRes;

        $.getJSON(url, function (tex) {
            var mat;
            if (tex.success && tex.compositeBase64) {
                var img = new Image();
                img.onload = function () {
                    var t = new THREE.Texture(img);
                    t.needsUpdate = true;
                    t.wrapS = THREE.ClampToEdgeWrapping;
                    t.wrapT = THREE.ClampToEdgeWrapping;
                    t.minFilter = THREE.LinearMipmapLinearFilter;
                    t.magFilter = THREE.LinearFilter;
                    t.anisotropy = maxAnisotropy;
                    t.generateMipmaps = true;
                    mat = makeTerrainMaterial({ map: t });
                    finishTile(entry, mat);
                    if (callback) callback();
                };
                img.src = 'data:image/png;base64,' + tex.compositeBase64;
                return;
            }
            mat = makeTerrainMaterial({ color: 0x3a5a2a });
            finishTile(entry, mat);
            if (callback) callback();
        }).fail(function () {
            var mat = makeTerrainMaterial({ color: 0x3a5a2a });
            finishTile(entry, mat);
            if (callback) callback();
        });
    }

    function finishTile(entry, mat) {
        entry.mesh = new THREE.Mesh(entry.geo, mat);
        entry.mesh.position.y = -0.5;
        scene.add(entry.mesh);
        entry.loading = false;
    }

    // ═══════════════════════════════════════════════════════════════
    // PROGRESSIVE TERRAIN LOADING (heightmap + texture only)
    // ═══════════════════════════════════════════════════════════════

    function cameraToGrid() {
        var tx = controls.target.x;
        var tz = controls.target.z;
        var dx = Math.round(tx / tileWidthMesh);
        var dy = Math.round(tz / tileWidthMesh);
        return {
            gridX: centerGridX + dy,
            gridY: centerGridY + dx
        };
    }

    function checkProgressiveLoading() {
        if (!currentPreset || tileWidthMesh === 0) return;

        var cam = cameraToGrid();

        for (var dy = -TILE_RADIUS; dy <= TILE_RADIUS; dy++) {
            for (var dx = -TILE_RADIUS; dx <= TILE_RADIUS; dx++) {
                var gx = cam.gridX + dy;
                var gy = cam.gridY + dx;
                var key = tileKey(gx, gy);

                if (gx < 0 || gx > 63 || gy < 0 || gy > 63) continue;
                if (tiles[key] || loadingTiles[key]) continue;

                loadingTiles[key] = true;
                loadSingleTile(gx, gy);
            }
        }

        // Unload terrain beyond UNLOAD_RADIUS
        Object.keys(tiles).forEach(function (key) {
            var t = tiles[key];
            var dgx = t.gridX - cam.gridX;
            var dgy = t.gridY - cam.gridY;
            if (Math.abs(dgx) > UNLOAD_RADIUS || Math.abs(dgy) > UNLOAD_RADIUS) {
                unloadTile(key);
            }
        });
    }

    function loadSingleTile(gx, gy) {
        var key = tileKey(gx, gy);
        var hmUrl = '/WorldViewer/SingleTileHeightmap?preset=' + encodeURIComponent(currentPreset) +
            '&tileGridX=' + gx + '&tileGridY=' + gy +
            '&globalMidHeight=' + globalMidHeight + '&globalHeightScale=' + globalHeightScale;

        $.getJSON(hmUrl, function (hm) {
            if (!hm.success) { delete loadingTiles[key]; return; }

            var geo = buildTerrainGeometry(hm);
            var dx = gy - centerGridY;
            var dy = gx - centerGridX;

            var entry = {
                mesh: null, gridX: gx, gridY: gy,
                dx: dx, dy: dy, geo: geo, loading: true
            };
            tiles[key] = entry;

            loadTileTexture(entry, function () {
                if (entry.mesh) {
                    entry.mesh.position.x = dx * tileWidthMesh;
                    entry.mesh.position.z = dy * tileWidthMesh;
                    entry.mesh.position.y = -0.5;
                }
                delete loadingTiles[key];
                loadTileWater(entry);
            });
        }).fail(function () { delete loadingTiles[key]; });
    }

    function unloadTile(key) {
        var t = tiles[key];
        if (!t) return;
        if (t.mesh) {
            scene.remove(t.mesh);
            if (t.mesh.geometry) t.mesh.geometry.dispose();
            if (t.mesh.material) {
                if (t.mesh.material.map) t.mesh.material.map.dispose();
                t.mesh.material.dispose();
            }
        }
        if (t.waterMesh) {
            scene.remove(t.waterMesh);
            if (t.waterMesh.geometry) t.waterMesh.geometry.dispose();
            if (t.waterMesh.material) t.waterMesh.material.dispose();
        }
        delete tiles[key];
    }

    // ═══════════════════════════════════════════════════════════════
    // TERRAIN GEOMETRY
    // ═══════════════════════════════════════════════════════════════

    function buildTerrainGeometry(tile) {
        var geo = new THREE.BufferGeometry();
        geo.setAttribute('position', new THREE.Float32BufferAttribute(tile.positions, 3));
        geo.setIndex(new THREE.BufferAttribute(new Uint32Array(tile.indices), 1));

        var uvs = new Float32Array(tile.positions.length / 3 * 2);
        for (var i = 0; i < tile.positions.length / 3; i++) {
            uvs[i * 2] = (i % tile.vertsWidth) / (tile.vertsWidth - 1);
            uvs[i * 2 + 1] = 1.0 - Math.floor(i / tile.vertsWidth) / (tile.vertsHeight - 1);
        }
        geo.setAttribute('uv', new THREE.Float32BufferAttribute(uvs, 2));
        geo.computeVertexNormals();
        return geo;
    }

    // ═══════════════════════════════════════════════════════════════
    // WATER
    // ═══════════════════════════════════════════════════════════════

    function loadTileWater(entry) {
        var url = '/WorldViewer/Water?preset=' + encodeURIComponent(currentPreset) +
            '&tileGridX=' + entry.gridX + '&tileGridY=' + entry.gridY +
            '&globalMidHeight=' + globalMidHeight + '&globalHeightScale=' + globalHeightScale;

        $.getJSON(url, function (w) {
            if (!w.success || !w.hasWater) return;
            var tileEntry = tiles[tileKey(entry.gridX, entry.gridY)];
            if (!tileEntry) return;

            var waterWidth = w.x2 - w.x1;
            var waterDepth = w.z2 - w.z1;
            var cellW = waterWidth / w.width;
            var cellD = waterDepth / w.height;
            var vw = w.width + 1; // vertex grid width

            // Build geometry only for cells that actually have liquid
            // cellFlags[y * width + x]: if flag == 0x0F → no liquid
            var positions = [];
            var indices = [];
            var vertIdx = 0;

            for (var cy = 0; cy < w.height; cy++) {
                for (var cx = 0; cx < w.width; cx++) {
                    // Check cell flag — VMaNGOS convention: 0 = no liquid, non-zero = has liquid
                    var flag = (w.cellFlags && w.cellFlags.length > cy * w.width + cx)
                        ? w.cellFlags[cy * w.width + cx] : 0;
                    if (flag === 0) continue; // skip dry cells

                    // 4 corners of this cell
                    var x0 = w.x1 + cx * cellW;
                    var x1 = x0 + cellW;
                    var z0 = w.z1 + cy * cellD;
                    var z1 = z0 + cellD;

                    // Height per vertex (if variable) or flat
                    var h00, h10, h01, h11;
                    if (w.heights && w.heights.length >= vw * (w.height + 1)) {
                        h00 = w.heights[cy * vw + cx];
                        h10 = w.heights[cy * vw + cx + 1];
                        h01 = w.heights[(cy + 1) * vw + cx];
                        h11 = w.heights[(cy + 1) * vw + cx + 1];
                    } else {
                        h00 = h10 = h01 = h11 = w.waterY;
                    }

                    // 4 vertices
                    positions.push(x0, h00, z0);  // TL
                    positions.push(x1, h10, z0);  // TR
                    positions.push(x0, h01, z1);  // BL
                    positions.push(x1, h11, z1);  // BR

                    // 2 triangles
                    indices.push(vertIdx, vertIdx + 2, vertIdx + 1);
                    indices.push(vertIdx + 1, vertIdx + 2, vertIdx + 3);
                    vertIdx += 4;
                }
            }

            if (positions.length === 0) return; // no wet cells

            var geo = new THREE.BufferGeometry();
            geo.setAttribute('position', new THREE.Float32BufferAttribute(new Float32Array(positions), 3));
            geo.setIndex(new THREE.BufferAttribute(new Uint32Array(indices), 1));
            geo.computeVertexNormals();

            var mat = new THREE.MeshBasicMaterial({
                color: 0x2266aa,
                transparent: true,
                opacity: 0.45,
                side: THREE.DoubleSide,
                depthWrite: false,
                fog: true
            });

            var waterMesh = new THREE.Mesh(geo, mat);

            // Apply tile offset
            var dx = entry.gridY - centerGridY;
            var dy = entry.gridX - centerGridX;
            waterMesh.position.x = dx * tileWidthMesh;
            waterMesh.position.z = dy * tileWidthMesh;
            waterMesh.position.y = -0.5;

            waterMesh.renderOrder = 1;
            scene.add(waterMesh);
            tileEntry.waterMesh = waterMesh;
        });
    }

    // ═══════════════════════════════════════════════════════════════
    // MODEL PART BUILDERS (shared geometry, reused by InstancedMesh)
    // ═══════════════════════════════════════════════════════════════

    function buildModelParts(data) {
        var posAttr = new THREE.Float32BufferAttribute(data.positions, 3);
        var normAttr = new THREE.Float32BufferAttribute(data.normals, 3);
        var uvAttr = new THREE.Float32BufferAttribute(data.uvs, 2);
        var allIndices = data.indices;
        var subs = data.submeshes || [{ indexStart: 0, indexCount: allIndices.length, textureBase64: null }];

        var parts = [];
        for (var si = 0; si < subs.length; si++) {
            var sub = subs[si];
            if (!sub.indexCount) continue;

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
                tex.anisotropy = maxAnisotropy;
                material = makeDoodadMaterial({
                    map: tex, side: THREE.DoubleSide,
                    alphaTest: 0.5, transparent: true
                });
            } else {
                material = makeDoodadMaterial({ color: 0x808080, side: THREE.DoubleSide });
            }
            parts.push({ geometry: geometry, material: material });
        }
        return parts;
    }

    function buildWmoParts(data) {
        var posAttr = new THREE.Float32BufferAttribute(data.positions, 3);
        var normAttr = new THREE.Float32BufferAttribute(data.normals, 3);
        var uvAttr = new THREE.Float32BufferAttribute(data.uvs, 2);
        var allIndices = data.indices;
        var subs = data.submeshes || [];

        var parts = [];
        for (var si = 0; si < subs.length; si++) {
            var sub = subs[si];
            if (!sub.indexCount) continue;

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
                tex.anisotropy = maxAnisotropy;
                material = makeWmoMaterial({
                    map: tex,
                    side: sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide,
                    alphaTest: sub.transparent ? 0.5 : 0,
                    transparent: !!sub.transparent
                });
            } else {
                material = makeWmoMaterial({
                    color: 0xaaaaaa,
                    side: sub.doubleSided ? THREE.DoubleSide : THREE.FrontSide
                });
            }
            parts.push({ geometry: geometry, material: material });
        }
        return parts;
    }

    // ── Status helper ──
    function setStatus(msg) {
        if (statusEl) statusEl.textContent = msg;
    }

    function loadSavedPlacements() {
        if (window._wmoPlacement) window._wmoPlacement.loadSaved();
    }

    // ═══════════════════════════════════════════════════════════════
    // ANIMATION LOOP
    // ═══════════════════════════════════════════════════════════════

    var progressiveCheckTimer = 0;
    var objectStreamTimer = 0;

    setInterval(function () {
        currentFps = fpsCounter * 2;
        fpsCounter = 0;
    }, 500);

    function animate() {
        requestAnimationFrame(animate);
        fpsCounter++;
        updateMovement();
        updateWalkMode();
        updateCollision();
        if (walkMode) {
            // In walk mode, bypass OrbitControls entirely — manage camera directly
            camera.lookAt(controls.target);
        } else {
            controls.update();
        }

        var dt = clock.getDelta();

        // Progressive terrain loading check every 500ms
        progressiveCheckTimer += dt;
        if (progressiveCheckTimer >= 0.5) {
            progressiveCheckTimer = 0;
            checkProgressiveLoading();

            groundPlane.position.x = controls.target.x;
            groundPlane.position.z = controls.target.z;
        }

        // Object streaming check every 600ms (offset from terrain to spread load)
        objectStreamTimer += dt;
        if (objectStreamTimer >= 0.6) {
            objectStreamTimer = 0;
            streamNearbyObjects();
        }

        // HUD
        var fps = document.getElementById('wvFps');
        var dc = document.getElementById('wvDoodadCount');
        var wc = document.getElementById('wvWmoCount');
        var mc = document.getElementById('wvModelCount');
        var tc = document.getElementById('wvTileCount');
        if (fps) fps.textContent = currentFps;

        var dCount = 0, wCount = 0;
        for (var id in activePlacements) {
            if (activePlacements[id].kind === 'd') dCount++;
            else wCount++;
        }
        if (dc) dc.textContent = dCount;
        if (wc) wc.textContent = wCount;
        if (mc) mc.textContent = Object.keys(modelRegistry).length;
        if (tc) tc.textContent = Object.keys(tiles).length;

        // Compass + coordinates
        updateCompass();
        updateCoords();

        renderer.render(scene, camera);
    }

    // ═══════════════════════════════════════════════════════════════
    // COMPASS + COORDINATES + FULLSCREEN
    // ═══════════════════════════════════════════════════════════════

    var compassEl, compassArrow, coordsEl;

    // Create compass overlay
    (function () {
        // Compass container — bottom-right corner
        compassEl = document.createElement('div');
        compassEl.style.cssText = 'position:absolute;bottom:60px;right:16px;width:80px;height:80px;' +
            'border-radius:50%;background:rgba(0,0,0,0.6);border:2px solid rgba(255,255,255,0.3);' +
            'pointer-events:none;z-index:10;';

        // Cardinal labels
        var labels = [
            { txt: 'N', x: '50%', y: '6px', tx: '-50%', ty: '0' },
            { txt: 'S', x: '50%', y: 'auto', b: '4px', tx: '-50%', ty: '0' },
            { txt: 'E', x: 'auto', r: '6px', y: '50%', tx: '0', ty: '-50%' },
            { txt: 'W', x: '6px', y: '50%', tx: '0', ty: '-50%' }
        ];
        labels.forEach(function (l) {
            var lbl = document.createElement('span');
            lbl.textContent = l.txt;
            lbl.style.cssText = 'position:absolute;color:' + (l.txt === 'N' ? '#ff4444' : 'rgba(255,255,255,0.7)') +
                ';font-size:11px;font-weight:bold;';
            lbl.style.left = l.x || 'auto';
            lbl.style.top = l.y || 'auto';
            if (l.r) lbl.style.right = l.r;
            if (l.b) lbl.style.bottom = l.b;
            lbl.style.transform = 'translate(' + l.tx + ',' + l.ty + ')';
            compassEl.appendChild(lbl);
        });

        // Arrow (rotates with camera)
        compassArrow = document.createElement('div');
        compassArrow.style.cssText = 'position:absolute;left:50%;top:50%;width:4px;height:28px;' +
            'margin-left:-2px;margin-top:-24px;transform-origin:2px 24px;' +
            'background:linear-gradient(to bottom, #ff4444 50%, #ffffff 50%);border-radius:2px;';
        compassEl.appendChild(compassArrow);

        // Center dot
        var dot = document.createElement('div');
        dot.style.cssText = 'position:absolute;left:50%;top:50%;width:6px;height:6px;margin:-3px;' +
            'border-radius:50%;background:#fff;';
        compassEl.appendChild(dot);

        canvas.parentElement.style.position = 'relative';
        canvas.parentElement.appendChild(compassEl);

        // Coordinates display
        coordsEl = document.createElement('div');
        coordsEl.style.cssText = 'position:absolute;bottom:16px;right:16px;padding:4px 10px;' +
            'background:rgba(0,0,0,0.6);color:#ccc;font-size:12px;font-family:monospace;' +
            'border-radius:4px;pointer-events:none;z-index:10;';
        coordsEl.textContent = 'X: 0  Z: 0';
        canvas.parentElement.appendChild(coordsEl);

        // Fullscreen button — bottom-right, above compass
        var fsBtn = document.createElement('button');
        fsBtn.innerHTML = '&#x26F6;'; // fullscreen icon
        fsBtn.title = 'Toggle fullscreen';
        fsBtn.style.cssText = 'position:absolute;bottom:148px;right:16px;width:36px;height:36px;' +
            'background:rgba(0,0,0,0.6);color:#ccc;border:1px solid rgba(255,255,255,0.3);' +
            'border-radius:6px;font-size:18px;cursor:pointer;z-index:10;display:flex;' +
            'align-items:center;justify-content:center;';
        fsBtn.addEventListener('click', function () {
            var el = canvas.parentElement;
            if (!document.fullscreenElement) {
                (el.requestFullscreen || el.webkitRequestFullscreen || el.msRequestFullscreen).call(el);
            } else {
                (document.exitFullscreen || document.webkitExitFullscreen || document.msExitFullscreen).call(document);
            }
        });
        canvas.parentElement.appendChild(fsBtn);

        // Resize on fullscreen change
        document.addEventListener('fullscreenchange', function () { setTimeout(resize, 100); });
    })();

    function updateCompass() {
        if (!compassArrow) return;
        var dir = new THREE.Vector3();
        camera.getWorldDirection(dir);
        // Angle from positive Z axis (North) in XZ plane
        var angle = Math.atan2(dir.x, dir.z); // radians, 0 = looking along +Z
        compassArrow.style.transform = 'rotate(' + (-angle * 180 / Math.PI) + 'deg)';
    }

    function updateCoords() {
        if (!coordsEl) return;
        var cx = camera.position.x.toFixed(0);
        var cy = camera.position.y.toFixed(0);
        var cz = camera.position.z.toFixed(0);
        coordsEl.textContent = 'X:' + cx + '  Y:' + cy + '  Z:' + cz;
    }

    // ═══════════════════════════════════════════════════════════════
    // WMO COLLISION — raycast against WMO InstancedMeshes
    // ═══════════════════════════════════════════════════════════════

    var collisionRaycaster = new THREE.Raycaster();
    var COLLISION_DISTANCE = 3; // how close before we stop

    function updateCollision() {
        if (!walkMode) return; // only collide in walk mode

        // Get movement direction
        var dir = new THREE.Vector3();
        camera.getWorldDirection(dir);
        dir.y = 0;
        dir.normalize();

        // Cast forward from camera at chest height
        var origin = camera.position.clone();
        collisionRaycaster.set(origin, dir);
        collisionRaycaster.far = COLLISION_DISTANCE + 2;

        // Collect WMO meshes (InstancedMeshes in wmoGroup)
        var wmoMeshes = [];
        wmoGroup.traverse(function (child) {
            if (child.isInstancedMesh || child.isMesh) wmoMeshes.push(child);
        });

        if (wmoMeshes.length === 0) return;

        var hits = collisionRaycaster.intersectObjects(wmoMeshes, false);
        if (hits.length > 0 && hits[0].distance < COLLISION_DISTANCE) {
            // Push camera back along the hit normal or just stop forward movement
            var pushBack = COLLISION_DISTANCE - hits[0].distance;
            camera.position.addScaledVector(dir, -pushBack);
            controls.target.addScaledVector(dir, -pushBack);
        }
    }

    animate();

})();
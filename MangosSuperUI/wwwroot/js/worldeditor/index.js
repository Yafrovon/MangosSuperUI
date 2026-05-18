// index.js — boot orchestrator. Wires every subsystem to the Editor.
//
// Razor view loads only this file (plus the import map for `three`).
// Air-gapped: point the import map at locally-vendored Three.js, e.g.:
//
//   <script type="importmap">
//   { "imports": {
//       "three": "/lib/three/build/three.module.js",
//       "three/addons/": "/lib/three/examples/jsm/",
//       "three-mesh-bvh": "/lib/three-mesh-bvh/index.js"
//   } }
//   </script>
//   <script type="module" src="~/js/worldeditor/index.js"></script>

import { Editor } from './core.js';
import { Viewport, WalkMode } from './render.js';
import { TileGrid, ObjectStream } from './streaming.js';
import {
    PlacementStore,
    PlaceWmoTool,
    PlacementModal
} from './placement.js';
import {
    SelectionSet,
    OutlineProxyManager,
    SelectTool
} from './selection.js';
import { TransformGizmoManager } from './transform.js';
import {
    createMovementTicker,
    attachWalkLook,
    attachKeyboard
} from './input.js';
import {
    Status, HUD, Compass,
    OptionsModal,
    addToolbarShortcuts
} from './ui.js';
import { getJSON } from './net.js';

// Phase 8: terrain sculpting
import { SculptTool, SculptPanel } from './sculpt.js';

// ─────────────────────────────────────────────────────────────────────────────
// Inlined URL teleport — small enough to live here. World Map links into
// WorldEditor via ?mapId=&gridX=&gridY=&worldX=&worldY=. After consuming, we
// clean the URL so a refresh doesn't re-teleport.
// ─────────────────────────────────────────────────────────────────────────────

const TILE_YARDS = 533.33333;

function readUrlTeleport() {
    const params = new URLSearchParams(window.location.search);
    const pMapId = params.get('mapId');
    const pGridX = params.get('gridX');
    const pGridY = params.get('gridY');

    if (pMapId === null || pGridX === null || pGridY === null) return null;

    const mi = parseInt(pMapId);
    const gx = parseInt(pGridX);
    const gy = parseInt(pGridY);
    if (isNaN(mi) || isNaN(gx) || isNaN(gy)) return null;

    const syntheticPreset = '@' + mi + '_' + gx + '_' + gy;
    let cameraOffset = null;

    const pWorldX = params.get('worldX');
    const pWorldY = params.get('worldY');
    if (pWorldX !== null && pWorldY !== null) {
        const worldX = parseFloat(pWorldX);
        const worldY = parseFloat(pWorldY);
        if (!isNaN(worldX) && !isNaN(worldY)) {
            const tileCenterWX = (32 - gx - 0.5) * TILE_YARDS;
            const tileCenterWY = (32 - gy - 0.5) * TILE_YARDS;
            cameraOffset = {
                meshX: tileCenterWX - worldX,
                meshZ: tileCenterWY - worldY
            };
        }
    }
    return { preset: syntheticPreset, cameraOffset: cameraOffset };
}

function clearUrlTeleport() {
    if (window.history.replaceState) {
        window.history.replaceState({}, '', window.location.pathname);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// DOM hooks (created by Razor)
// ─────────────────────────────────────────────────────────────────────────────

const canvas = document.getElementById('weCanvas');
const presetSelect = document.getElementById('wePresetSelect');
const loadBtn = document.getElementById('weLoadBtn');

if (!canvas) throw new Error('worldeditor: #weCanvas not found');
if (!presetSelect) throw new Error('worldeditor: #wePresetSelect not found');
if (!loadBtn) throw new Error('worldeditor: #weLoadBtn not found');

// ─────────────────────────────────────────────────────────────────────────────
// Editor + Viewport
// ─────────────────────────────────────────────────────────────────────────────

const editor = new Editor();
const viewport = new Viewport(editor, canvas);

// ─────────────────────────────────────────────────────────────────────────────
// Subsystems
// ─────────────────────────────────────────────────────────────────────────────

const tileGrid = new TileGrid(editor);
const objectStream = new ObjectStream(editor);
objectStream.attachTo(editor.viewport.scene);

const placementStore = new PlacementStore(editor, objectStream.pool.wmoGroup);

editor.tileGrid = tileGrid;
editor.objectStream = objectStream;
editor.placementStore = placementStore;
editor.walkModeImpl = new WalkMode(editor);

// ─────────────────────────────────────────────────────────────────────────────
// Tools — 'select' (real picker, Phase 4) + 'place-wmo' + 'sculpt' (Phase 8).
// ─────────────────────────────────────────────────────────────────────────────

// Selection state replaces the placeholder Set on Editor. Construction order
// matters: SelectionSet subscribes to lifecycle signals; OutlineProxyManager
// subscribes to selectionChanged and needs viewport.outlinePass to exist
// (already set by Viewport ctor above).
editor.selection = new SelectionSet(editor);
const outlineProxies = new OutlineProxyManager(editor);

// Phase 5: TransformControls integration. Must be constructed after
// SelectionSet (subscribes to selectionChanged) and after the Viewport
// (uses rig.camera and canvas). Exposed on editor so SelectTool's G/R
// hotkeys can reach it without an import cycle.
const transformGizmo = new TransformGizmoManager(editor);
editor.transformGizmo = transformGizmo;

editor.tools.register(new SelectTool(editor));
editor.tools.register(new PlaceWmoTool(editor));

// Phase 8: terrain sculpting
const sculptTool = new SculptTool(editor);
editor.tools.register(sculptTool);
const sculptPanel = new SculptPanel(editor, sculptTool);

editor.tools.setActive('select');

// ─────────────────────────────────────────────────────────────────────────────
// Input
// ─────────────────────────────────────────────────────────────────────────────

const movementTicker = createMovementTicker(editor);
viewport.addTicker(movementTicker);
attachWalkLook(editor);

// ─────────────────────────────────────────────────────────────────────────────
// Loader (preset switching)
// ─────────────────────────────────────────────────────────────────────────────

let pendingTeleport = null;

function loadPresetByKey(presetKey, label) {
    // Tear down — fire BEFORE we touch anything else so listeners can capture
    // the outgoing preset.
    editor.signals.presetClearing.dispatch(editor.currentPreset);

    // Reset history (commands referring to deleted placements would dangle).
    editor.history.clear();

    // Clear placement store + object stream (tile grid clears its own tiles
    // internally during loadPreset).
    placementStore.clearAll();
    objectStream.clearAll();

    Status.set('Loading terrain...');
    tileGrid.loadPreset(presetKey, Status.set).then((hm) => {
        if (!hm) return; // failure path already wrote the status
        Status.set(label || hm.label || presetKey);

        // Load saved WMO placements for this preset.
        placementStore.loadSaved();

        // Apply pending teleport if any.
        if (pendingTeleport) {
            const cam = editor.viewport.rig.camera;
            const ctl = editor.viewport.rig.controls;
            cam.position.set(pendingTeleport.meshX, 30, pendingTeleport.meshZ);
            ctl.target.set(pendingTeleport.meshX, 25, pendingTeleport.meshZ - 10);
            if (!editor.viewport.rig.walk.mode) {
                editor.viewport.rig.enterWalkMode();
                editor.signals.walkModeChanged.dispatch(true);
            }
            pendingTeleport = null;
        }
    });
}

// ─────────────────────────────────────────────────────────────────────────────
// UI
// ─────────────────────────────────────────────────────────────────────────────

const placementModal = new PlacementModal(editor);
const optionsModal = new OptionsModal(editor, movementTicker, loadPresetByKey);
addToolbarShortcuts(editor);

// Phase 8: Sculpt toolbar button
(function addSculptButton() {
    const toolbar = document.getElementById('weLoadBtn');
    if (!toolbar || !toolbar.parentElement) return;
    const container = toolbar.parentElement;

    const sculptBtn = document.createElement('button');
    sculptBtn.textContent = 'Sculpt';
    sculptBtn.id = 'weSculptBtn';
    sculptBtn.className = 'btn btn-sm btn-dark';
    sculptBtn.style.cssText = 'margin-left:8px;font-size:12px;padding:2px 8px;';
    sculptBtn.title = 'Terrain sculpt tool (B)';
    sculptBtn.addEventListener('click', () => {
        const active = editor.tools.activeId;
        if (active === 'sculpt') {
            editor.tools.setActive('select');
        } else {
            editor.tools.setActive('sculpt');
        }
        sculptBtn.blur();
    });
    container.appendChild(sculptBtn);

    // Sync button highlight with tool changes
    editor.signals.toolChanged.add((toolId) => {
        sculptBtn.className = toolId === 'sculpt'
            ? 'btn btn-sm btn-success'
            : 'btn btn-sm btn-dark';
    });
})();

const compass = new Compass(editor);
const hud = new HUD(editor);

// Cheap UI tickers (compass + coords + FPS) on the viewport frame.
viewport.addTicker(() => { compass.tick(); hud.tick(); });

// Keyboard: global Esc + Ctrl-Z/Ctrl-Y + tool key dispatch.
attachKeyboard(editor, [placementModal, optionsModal, sculptPanel]);

// ─────────────────────────────────────────────────────────────────────────────
// Preset list + URL teleport
// ─────────────────────────────────────────────────────────────────────────────

loadBtn.addEventListener('click', () => {
    const preset = presetSelect.value;
    if (!preset) return;
    const label = presetSelect.options[presetSelect.selectedIndex].textContent;
    pendingTeleport = null;
    loadPresetByKey(preset, label);
});

getJSON('/WorldEditor/Presets').then((data) => {
    if (data.success && data.presets) {
        presetSelect.innerHTML = '';
        data.presets.forEach((p) => {
            const opt = document.createElement('option');
            opt.value = p.key;
            opt.textContent = p.name;
            presetSelect.appendChild(opt);
        });
    }
    // URL teleport (from World Map link)
    const tp = readUrlTeleport();
    if (tp) {
        pendingTeleport = tp.cameraOffset; // null if not provided
        editor.currentPreset = tp.preset;
        Status.set('Teleporting...');
        loadPresetByKey(tp.preset, tp.preset);
        clearUrlTeleport();
    }
});

// ─────────────────────────────────────────────────────────────────────────────
// Debug handle (dev only) — `window.we.editor`, `window.we.tools`, etc.
// ─────────────────────────────────────────────────────────────────────────────

window.we = {
    editor: editor,
    viewport: viewport,
    tools: editor.tools,
    history: editor.history,
    placement: placementStore,
    objectStream: objectStream,
    tileGrid: tileGrid,
    selection: editor.selection,
    outlineProxies: outlineProxies,
    transformGizmo: transformGizmo,
    sculptTool: sculptTool
};

editor.signals.sceneReady.dispatch();
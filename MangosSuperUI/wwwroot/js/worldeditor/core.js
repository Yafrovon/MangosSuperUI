// core.js — Editor core: signals, history, command, tool manager, entity tags.
//
// This file is the new architectural foundation. None of the other modules
// import from each other for "core" concerns — they all go through here.
//
// Sections:
//   1. Signal       — minimal pub/sub
//   2. makeSignals  — the named signal bus the Editor exposes
//   3. Command      — base class for undoable mutations
//   4. History      — undo/redo stack with coalescing
//   5. Tool/ToolManager — single-active-tool dispatch
//   6. tagEntity    — userData.editorEntity metadata helper
//   7. Editor       — root object every subsystem hangs off of

import * as THREE from 'three';

// ─────────────────────────────────────────────────────────────────────────────
// 1. Signal — minimal js-signals-style pub/sub
// ─────────────────────────────────────────────────────────────────────────────

export class Signal {
    constructor() { this._listeners = []; }
    add(fn) { if (typeof fn === 'function') this._listeners.push(fn); return fn; }
    remove(fn) {
        const i = this._listeners.indexOf(fn);
        if (i >= 0) this._listeners.splice(i, 1);
    }
    removeAll() { this._listeners.length = 0; }
    dispatch() {
        const args = arguments;
        // copy because a listener may add/remove others
        const snapshot = this._listeners.slice();
        for (let i = 0; i < snapshot.length; i++) {
            try { snapshot[i].apply(null, args); }
            catch (err) { console.error('Signal listener error:', err); }
        }
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. makeSignals — every cross-module event flows through one of these
// ─────────────────────────────────────────────────────────────────────────────

export function makeSignals() {
    return {
        // Lifecycle
        sceneReady: new Signal(), // ()
        presetLoaded: new Signal(), // (presetKey)
        presetClearing: new Signal(), // (presetKey)

        // Placement
        placementAdded: new Signal(), // (placement)
        placementRemoved: new Signal(), // (id)
        placementUpdated: new Signal(), // (placement)
        placementsCleared: new Signal(), // ()

        // Tools
        toolChanged: new Signal(), // (toolId)

        // History
        historyChanged: new Signal(), // (command)
        historyCleared: new Signal(), // ()

        // Walk mode
        walkModeChanged: new Signal(), // (enabled)

        // Selection (Phase 4)
        selectionChanged: new Signal(), // (items)

        // Terrain sculpt (Phase 8)
        terrainModified: new Signal()  // (tileKey)
    };
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Command — base class for undoable mutations
// ─────────────────────────────────────────────────────────────────────────────

export class Command {
    constructor(editor) {
        this.editor = editor;
        this.id = 0;
        this.time = 0;
        this.updatable = false;
        this.type = this.constructor.name;
        this.name = this.constructor.name;
    }
    execute() { /* override */ }
    undo() { /* override */ }
    update(newCmd) { /* override if updatable */ }
}

// ─────────────────────────────────────────────────────────────────────────────
// 4. History — undo/redo stack with coalescing
// ─────────────────────────────────────────────────────────────────────────────
//
// execute(cmd) runs the command and pushes it onto the undo stack, OR merges
// it into the previous command if both are updatable, same type, same target,
// and within the coalesce window. Subclasses define coalesceKey() to identify
// "same target" — e.g. SetPositionCommand keys on the object UUID. Drag
// operations (Phase 5) will coalesce hundreds of per-frame commands into one.

const COALESCE_MS = 500;
const MAX_DEPTH = 100;

export class History {
    constructor(editor) {
        this.editor = editor;
        this.undos = [];
        this.redos = [];
        this.idCounter = 0;
    }

    execute(cmd) {
        const last = this.undos[this.undos.length - 1];
        const dt = last ? (Date.now() - (last.time || 0)) : Infinity;

        const sameTarget = (
            last &&
            cmd.updatable && last.updatable &&
            last.type === cmd.type &&
            typeof last.coalesceKey === 'function' &&
            typeof cmd.coalesceKey === 'function' &&
            last.coalesceKey() === cmd.coalesceKey() &&
            dt < COALESCE_MS
        );

        if (sameTarget) {
            last.update(cmd);
            cmd.execute();
            last.time = Date.now();
        } else {
            cmd.execute();
            cmd.id = ++this.idCounter;
            cmd.time = Date.now();
            this.undos.push(cmd);
            while (this.undos.length > MAX_DEPTH) {
                const dropped = this.undos.shift();
                if (typeof dropped.dispose === 'function') dropped.dispose();
            }
        }
        this.redos.length = 0;
        this.editor.signals.historyChanged.dispatch(cmd);
    }

    undo() {
        if (this.undos.length === 0) return null;
        const cmd = this.undos.pop();
        try { cmd.undo(); }
        catch (err) { console.error('Undo failed:', err); }
        this.redos.push(cmd);
        this.editor.signals.historyChanged.dispatch(cmd);
        return cmd;
    }

    redo() {
        if (this.redos.length === 0) return null;
        const cmd = this.redos.pop();
        try { cmd.execute(); }
        catch (err) { console.error('Redo failed:', err); }
        this.undos.push(cmd);
        this.editor.signals.historyChanged.dispatch(cmd);
        return cmd;
    }

    clear() {
        for (const c of this.undos) if (typeof c.dispose === 'function') c.dispose();
        for (const c of this.redos) if (typeof c.dispose === 'function') c.dispose();
        this.undos.length = 0;
        this.redos.length = 0;
        this.editor.signals.historyCleared.dispatch();
    }

    canUndo() { return this.undos.length > 0; }
    canRedo() { return this.redos.length > 0; }
}

// ─────────────────────────────────────────────────────────────────────────────
// 5. Tool / ToolManager — single active tool, dispatches viewport events
// ─────────────────────────────────────────────────────────────────────────────
//
// Replaces the placementMode/walkMode global-boolean spaghetti. A Tool owns
// the cursor, the helpers, and the input handlers for one mode of interaction.
// Two singletons exist in this drop:
//   'select'    — default no-op picker (real in Phase 4)
//   'place-wmo' — owns the ghost lifecycle and the placement modal flow

export class Tool {
    constructor(editor, id) {
        this.editor = editor;
        this.id = id;
    }
    activate() { /* override */ }
    deactivate() { /* override */ }
    onPointerDown(ev, ctx) { /* override; ctx = { camera, controls, scene } */ }
    onPointerMove(ev, ctx) { /* override */ }
    onPointerUp(ev, ctx) { /* override */ }
    onKeyDown(ev) { /* override */ }
    onKeyUp(ev) { /* override */ }
    onWheel(ev, ctx) { /* override */ }
    onContextMenu(ev) { return false; }
}

export class ToolManager {
    constructor(editor) {
        this.editor = editor;
        this.tools = {};
        this.active = null;
        this.activeId = null;
    }

    register(tool) { this.tools[tool.id] = tool; return tool; }

    setActive(id) {
        if (this.activeId === id) return;
        const next = this.tools[id];
        if (!next) { console.warn('Unknown tool:', id); return; }
        if (this.active) {
            try { this.active.deactivate(); }
            catch (err) { console.error('deactivate', this.activeId, err); }
        }
        this.active = next;
        this.activeId = id;
        try { next.activate(); }
        catch (err) { console.error('activate', id, err); }
        this.editor.signals.toolChanged.dispatch(id);
    }
}

// ─────────────────────────────────────────────────────────────────────────────
// 6. tagEntity / entityOf — userData.editorEntity metadata
// ─────────────────────────────────────────────────────────────────────────────
//
// Every Object3D the editor manages (selectable, transformable, persistable)
// carries `userData.editorEntity`. Selection pickers (Phase 4), gizmos
// (Phase 5), and outline pass all filter by this metadata. Render-only
// objects (sky, ground plane, gizmo geometry) MUST NOT carry it.

export function tagEntity(obj, meta) {
    if (!obj) return obj;
    obj.userData = obj.userData || {};
    obj.userData.editorEntity = Object.assign({
        type: 'unknown',
        id: null,
        selectable: true,
        transformable: true,
        persistable: true,
        source: 'placement'
    }, meta || {});
    return obj;
}

export function entityOf(obj) {
    return obj && obj.userData ? obj.userData.editorEntity : null;
}

export function isSelectable(obj) {
    const e = entityOf(obj);
    return !!(e && e.selectable);
}

// ─────────────────────────────────────────────────────────────────────────────
// 7. Editor — the root object every subsystem hangs off of
// ─────────────────────────────────────────────────────────────────────────────

export class Editor {
    constructor() {
        this.signals = makeSignals();
        this.history = new History(this);
        this.tools = new ToolManager(this);

        // The persistable scene (everything you'd serialize) — same THREE.Scene
        // used by Viewport. Render-only helpers (sky, ground plane) live in
        // here too for now; Phase 4 may split into scene + sceneHelpers.
        this.scene = new THREE.Scene();

        // Selection. Initial value is a plain Set as a no-op stand-in.
        // index.js replaces this with a SelectionSet (selection.js) after
        // the Editor is constructed. SelectionSet items are tuples:
        //   { object: Object3D, instanceId: number|null, placementId: number|null }
        this.selection = new Set();

        // Active preset key (e.g. 'kalimdor' or '@0_30_36').
        this.currentPreset = null;

        // Subsystem references — set by their respective owners.
        this.viewport = null;
        this.placementStore = null;
        this.tileGrid = null;
        this.objectStream = null;
        this.walkModeImpl = null;
        this.transformGizmo = null;
    }
}
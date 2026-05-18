// input.js — keyboard + pointer input outside the canvas tool dispatch.
//
// Sections:
//   1. createMovementTicker — WASD + arrows + sprint (suppressed during placement)
//   2. attachWalkLook       — right-click drag → yaw/pitch in walk mode
//   3. attachKeyboard       — global Esc + Ctrl-Z/Ctrl-Y + key forward to active tool

import * as THREE from 'three';

const MOUSE_LOOK_SENSITIVITY = 0.0045;

// ─────────────────────────────────────────────────────────────────────────────
// 1. Movement ticker
// ─────────────────────────────────────────────────────────────────────────────
//
// Returns a function compatible with Viewport.addTicker. Suppressed when the
// active tool is 'place-wmo' so the camera doesn't fly off during placement.
// Exposes .setMoveSpeed and .setSprintSpeed for the options modal.

export function createMovementTicker(editor) {
    const moveKeys = {};
    let sprinting = false;
    let moveSpeed = 3.0;
    let sprintSpeed = 10.0;

    document.addEventListener('keydown', (e) => {
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'BUTTON' || e.target.tagName === 'SELECT') {
            e.target.blur();
        }
        if (e.target.tagName === 'INPUT' || e.target.tagName === 'SELECT') return;
        moveKeys[e.code] = true;
        if (e.code === 'ShiftLeft' || e.code === 'ShiftRight') sprinting = true;
    });
    document.addEventListener('keyup', (e) => {
        moveKeys[e.code] = false;
        if (e.code === 'ShiftLeft' || e.code === 'ShiftRight') sprinting = false;
    });

    const ticker = (vp) => {
        if (editor.tools.activeId === 'place-wmo') return; // suppress during placement

        const camera = vp.rig.camera;
        const controls = vp.rig.controls;
        const walk = vp.rig.walk;

        const forward = new THREE.Vector3();
        camera.getWorldDirection(forward);
        forward.y = 0;
        if (forward.lengthSq() > 0) forward.normalize();

        const right = new THREE.Vector3();
        right.crossVectors(forward, new THREE.Vector3(0, 1, 0)).normalize();

        const delta = new THREE.Vector3();
        if (moveKeys['KeyW']) delta.add(forward);
        if (moveKeys['KeyS']) delta.sub(forward);
        if (moveKeys['KeyD']) delta.add(right);
        if (moveKeys['KeyA']) delta.sub(right);
        if (moveKeys['KeyE'] || moveKeys['Space']) delta.y += 1;
        if (moveKeys['KeyQ']) delta.y -= 1;

        if (delta.lengthSq() > 0) {
            const speed = sprinting ? sprintSpeed : moveSpeed;
            delta.normalize().multiplyScalar(speed);
            camera.position.add(delta);
            controls.target.add(delta);
        }

        const turnSpeed = 0.03;
        const tiltSpeed = 0.015;
        const arrowPressed = moveKeys['ArrowLeft'] || moveKeys['ArrowRight'] ||
            moveKeys['ArrowUp'] || moveKeys['ArrowDown'];
        if (!arrowPressed) return;

        if (walk.mode) {
            if (!walk.inited) {
                const dir2 = new THREE.Vector3();
                camera.getWorldDirection(dir2);
                walk.yaw = Math.atan2(dir2.x, dir2.z);
                walk.pitch = Math.asin(Math.max(-0.95, Math.min(0.95, dir2.y)));
                walk.inited = true;
            }
            if (moveKeys['ArrowLeft']) walk.yaw += turnSpeed;
            if (moveKeys['ArrowRight']) walk.yaw -= turnSpeed;
            if (moveKeys['ArrowUp']) walk.pitch += tiltSpeed;
            if (moveKeys['ArrowDown']) walk.pitch -= tiltSpeed;
            walk.pitch = Math.max(-1.4, Math.min(1.4, walk.pitch));
            vp.rig.applyWalkLook();
        } else {
            const offset = new THREE.Vector3().subVectors(controls.target, camera.position);
            const spherical = new THREE.Spherical().setFromVector3(offset);
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
    };

    ticker.setMoveSpeed = (v) => { moveSpeed = v; };
    ticker.setSprintSpeed = (v) => { sprintSpeed = v; };

    return ticker;
}

// ─────────────────────────────────────────────────────────────────────────────
// 2. Walk-look — right-click drag → yaw/pitch when walk mode is on
// ─────────────────────────────────────────────────────────────────────────────
//
// Only active while rig.walk.mode is true. The capture-phase pointerdown
// runs before OrbitControls so we own right-click while walking. (RIGHT is
// also explicitly disabled in OrbitControls.mouseButtons, so this is belt
// and suspenders.)

export function attachWalkLook(editor) {
    const canvas = editor.viewport.canvas;
    const rig = editor.viewport.rig;

    canvas.addEventListener('pointerdown', (e) => {
        if (e.button !== 2 || !rig.walk.mode) return;
        rig.walk.rightMouseDown = true;
        rig.walk.lastMouseX = e.clientX;
        rig.walk.lastMouseY = e.clientY;

        if (!rig.walk.inited) {
            const dir = new THREE.Vector3();
            rig.camera.getWorldDirection(dir);
            rig.walk.yaw = Math.atan2(dir.x, dir.z);
            rig.walk.pitch = Math.asin(Math.max(-0.95, Math.min(0.95, dir.y)));
            rig.walk.inited = true;
        }
        e.preventDefault();
        e.stopImmediatePropagation();
    }, true);

    document.addEventListener('pointerup', (e) => {
        if (e.button === 2) rig.walk.rightMouseDown = false;
    });

    document.addEventListener('pointermove', (e) => {
        if (!rig.walk.rightMouseDown || !rig.walk.mode) return;
        const dx = e.clientX - rig.walk.lastMouseX;
        const dy = e.clientY - rig.walk.lastMouseY;
        rig.walk.lastMouseX = e.clientX;
        rig.walk.lastMouseY = e.clientY;
        rig.walk.yaw -= dx * MOUSE_LOOK_SENSITIVITY;
        rig.walk.pitch -= dy * MOUSE_LOOK_SENSITIVITY;
        rig.walk.pitch = Math.max(-1.4, Math.min(1.4, rig.walk.pitch));
        rig.applyWalkLook();
    });
}

// ─────────────────────────────────────────────────────────────────────────────
// 3. Keyboard — global Esc + Ctrl-Z/Ctrl-Y + tool forward
// ─────────────────────────────────────────────────────────────────────────────
//
// Movement keys (WASD, arrows, Shift) are owned by createMovementTicker —
// it attaches its own keydown/keyup listeners.

export function attachKeyboard(editor, modalRegistry) {
    document.addEventListener('keydown', (ev) => {
        const tag = ev.target.tagName;
        const inField = (tag === 'INPUT' || tag === 'SELECT' || tag === 'TEXTAREA');

        // Ctrl-Z / Ctrl-Y → history
        if (!inField && (ev.ctrlKey || ev.metaKey)) {
            if (ev.code === 'KeyZ' && !ev.shiftKey) {
                editor.history.undo();
                ev.preventDefault();
                return;
            }
            if ((ev.code === 'KeyZ' && ev.shiftKey) || ev.code === 'KeyY') {
                editor.history.redo();
                ev.preventDefault();
                return;
            }
        }

        // Phase 8: B → sculpt tool toggle
        if (!inField && ev.code === 'KeyB' && !ev.ctrlKey && !ev.metaKey) {
            const current = editor.tools.activeId;
            editor.tools.setActive(current === 'sculpt' ? 'select' : 'sculpt');
            ev.preventDefault();
            return;
        }

        // Forward to active tool
        const active = editor.tools.active;
        if (active && typeof active.onKeyDown === 'function') {
            try {
                const handled = active.onKeyDown(ev);
                if (handled) return;
            } catch (err) { console.error('onKeyDown', err); }
        }

        // Escape — close modals (registered first-wins)
        if (!inField && ev.code === 'Escape' && modalRegistry) {
            for (const m of modalRegistry) {
                if (m && typeof m.closeIfOpen === 'function') m.closeIfOpen();
            }
        }
    });

    document.addEventListener('keyup', (ev) => {
        const active = editor.tools.active;
        if (active && typeof active.onKeyUp === 'function') {
            try { active.onKeyUp(ev); }
            catch (err) { console.error('onKeyUp', err); }
        }
    });
}
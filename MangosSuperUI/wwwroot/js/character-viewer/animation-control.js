// Character Viewer — Animation picker control (Session O).
//
// A small floating panel anchored to the bottom-left of the canvas, with:
//   - dropdown listing baked animations (resolved via animation-names.js)
//   - play/pause toggle
//   - playback-speed slider (0.25× to 2.0×)
//
// Deliberately separate from the diagnostic panel. Animation picking is a
// user feature (visitors will use it on their characters), not a debug
// feature — keeping it in its own module means the diagnostic panel can
// stay collapsed-by-default without hiding animation controls.
//
// === API ===
//   mountAnimationControl({ canvasEl, mixer, animations })
//     Mounts the panel as a positioned <div> over the canvas's parent.
//     If `animations` is empty, mounts nothing and returns null.
//   Returns:
//     { destroy: function, play: function, stop: function }
//
// The mount function captures `mixer` and `animations` and uses them to
// drive THREE.AnimationActions. Nothing here knows about M2 internals —
// it operates entirely on standard Three.js animation primitives.
//
// === Single-action discipline ===
// We keep at most ONE THREE.AnimationAction running at a time. Switching
// animations stops the old action before starting the new one (no
// crossfade blending in this version). Three.js's default behavior of
// leaving stopped actions in the mixer's update list is fine for a small
// set of clips, but we explicitly call action.stop() rather than .reset()
// so the mixer doesn't keep ticking dead clips forward.

import { friendlyClipName } from './animation-names.js';

const PANEL_ID = 'cv-animation-control';

/**
 * Mount the animation picker. Idempotent — if a panel with the same ID
 * already exists in the document, it's removed first.
 *
 * @param {{
 *   canvasEl: HTMLCanvasElement,
 *   mixer: THREE.AnimationMixer,
 *   animations: THREE.AnimationClip[],
 *   defaultClipName?: string
 * }} opts
 * @returns {{ destroy: function, play: (name: string) => void, stop: function } | null}
 */
export function mountAnimationControl(opts) {
    const { canvasEl, mixer, animations } = opts;
    if (!animations || animations.length === 0) {
        // Nothing to drive — don't render an empty picker.
        return null;
    }

    // Remove any prior mount (idempotency).
    const existing = document.getElementById(PANEL_ID);
    if (existing) existing.remove();

    // ── Build DOM ────────────────────────────────────────────────────────
    const panel = document.createElement('div');
    panel.id = PANEL_ID;
    Object.assign(panel.style, {
        position: 'absolute',
        bottom: '12px',
        left: '12px',
        zIndex: '20',
        background: 'rgba(20, 20, 24, 0.85)',
        color: '#eee',
        font: '12px/1.3 system-ui, -apple-system, sans-serif',
        padding: '8px 10px',
        borderRadius: '6px',
        backdropFilter: 'blur(4px)',
        boxShadow: '0 2px 8px rgba(0,0,0,0.4)',
        pointerEvents: 'auto',
        display: 'flex',
        gap: '8px',
        alignItems: 'center',
        userSelect: 'none',
    });

    // Dropdown
    const select = document.createElement('select');
    Object.assign(select.style, {
        background: '#1d1d22',
        color: '#eee',
        border: '1px solid #3a3a42',
        borderRadius: '3px',
        padding: '3px 6px',
        font: 'inherit',
        minWidth: '120px',
    });
    for (let i = 0; i < animations.length; i++) {
        const clip = animations[i];
        const opt = document.createElement('option');
        opt.value = String(i);                    // index, not name (clip names may collide)
        opt.textContent = friendlyClipName(clip.name);
        select.appendChild(opt);
    }

    // Play/pause toggle. We start in "playing" state since the boot code
    // auto-starts the default clip; the button reflects current state.
    const playBtn = document.createElement('button');
    Object.assign(playBtn.style, {
        background: '#1d1d22',
        color: '#eee',
        border: '1px solid #3a3a42',
        borderRadius: '3px',
        padding: '3px 8px',
        font: 'inherit',
        cursor: 'pointer',
        minWidth: '52px',
    });
    playBtn.textContent = 'Pause';

    // Speed slider
    const speedWrap = document.createElement('label');
    Object.assign(speedWrap.style, {
        display: 'flex',
        alignItems: 'center',
        gap: '4px',
        color: '#aaa',
    });
    const speedLabel = document.createElement('span');
    speedLabel.textContent = '1.0×';
    speedLabel.style.minWidth = '32px';
    speedLabel.style.textAlign = 'right';
    const speedSlider = document.createElement('input');
    speedSlider.type = 'range';
    speedSlider.min = '0.25';
    speedSlider.max = '2';
    speedSlider.step = '0.05';
    speedSlider.value = '1';
    speedSlider.style.width = '80px';
    speedSlider.style.accentColor = '#888';
    speedWrap.appendChild(speedSlider);
    speedWrap.appendChild(speedLabel);

    panel.appendChild(select);
    panel.appendChild(playBtn);
    panel.appendChild(speedWrap);

    // The canvas's offsetParent is what we want to mount over — usually a
    // wrapping div. If it isn't positioned, force it: absolute positioning
    // needs a positioned ancestor.
    const mountTarget = canvasEl.parentElement;
    if (mountTarget) {
        const style = window.getComputedStyle(mountTarget);
        if (style.position === 'static') mountTarget.style.position = 'relative';
        mountTarget.appendChild(panel);
    } else {
        document.body.appendChild(panel);
    }

    // ── State ───────────────────────────────────────────────────────────
    let currentAction = null;
    let isPlaying = true;
    let timeScale = 1.0;

    function playByIndex(idx) {
        const clip = animations[idx];
        if (!clip) return;

        // Stop the previous action — see "Single-action discipline" above.
        if (currentAction) {
            currentAction.stop();
            currentAction = null;
        }

        const action = mixer.clipAction(clip);
        action.reset();
        // Defensive: force looping even if a clip's authored duration is
        // wrong. THREE's default is LoopRepeat but explicitly setting it
        // here guards against future SkinnedGlbWriter regressions where
        // a clip's max-keyframe-time doesn't match the intended duration.
        //
        // Hard-coded constant rather than `THREE.LoopRepeat` because this
        // file is an ES module and the rest of the viewer treats THREE as
        // a global script tag — there's no `import THREE` in scope here.
        // The numeric value of LoopRepeat has been stable since r58 (2013).
        const LOOP_REPEAT = 2201;
        action.setLoop(LOOP_REPEAT, Infinity);
        action.clampWhenFinished = false;
        action.timeScale = timeScale;
        action.play();
        currentAction = action;
        isPlaying = true;
        playBtn.textContent = 'Pause';
    }

    function togglePlayPause() {
        if (!currentAction) return;
        if (isPlaying) {
            currentAction.paused = true;
            isPlaying = false;
            playBtn.textContent = 'Play';
        } else {
            currentAction.paused = false;
            isPlaying = true;
            playBtn.textContent = 'Pause';
        }
    }

    function setSpeed(v) {
        timeScale = v;
        if (currentAction) currentAction.timeScale = v;
        speedLabel.textContent = v.toFixed(2).replace(/\.?0+$/, '') + '×';
    }

    // ── Wire events ─────────────────────────────────────────────────────
    select.addEventListener('change', () => {
        playByIndex(parseInt(select.value, 10));
    });
    playBtn.addEventListener('click', togglePlayPause);
    speedSlider.addEventListener('input', () => {
        setSpeed(parseFloat(speedSlider.value));
    });

    // ── Initial state ───────────────────────────────────────────────────
    // Pick the default clip — caller-specified name first, then "Stand",
    // then the first available clip.
    let initialIdx = 0;
    if (opts.defaultClipName) {
        const idx = animations.findIndex(c => c.name === opts.defaultClipName);
        if (idx >= 0) initialIdx = idx;
    } else {
        const idx = animations.findIndex(c => c.name === 'Stand');
        if (idx >= 0) initialIdx = idx;
    }
    select.value = String(initialIdx);
    playByIndex(initialIdx);

    // ── Return handle ───────────────────────────────────────────────────
    return {
        destroy() {
            if (currentAction) currentAction.stop();
            panel.remove();
        },
        play(name) {
            const idx = animations.findIndex(c => c.name === name);
            if (idx >= 0) {
                select.value = String(idx);
                playByIndex(idx);
            }
        },
        stop() {
            if (currentAction) {
                currentAction.stop();
                currentAction = null;
            }
            isPlaying = false;
            playBtn.textContent = 'Play';
        },
    };
}
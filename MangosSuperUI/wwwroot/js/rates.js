// Gameplay Tuning > Rates — shared page logic for Experience / Reputation / Honor.
// Loads /Rates/State (catalog + mangosd.conf values + what the running core reports
// via `.sui rates`), renders the fields grouped, tracks edits against the conf, and
// saves through /Rates/Save (conf write + `.reload config` + read-back).
// Page scripts (rates-xp.js, rates-rep.js) hook in through window.Rates.
(function () {
    'use strict';

    var root = document.getElementById('ratesRoot');
    if (!root) return;

    var page = root.getAttribute('data-page');
    var state = null;     // last /Rates/State payload
    var edited = {};      // key -> string value currently in the editor
    var listeners = [];

    function esc(s) {
        return String(s == null ? '' : s)
            .replace(/&/g, '&amp;').replace(/</g, '&lt;')
            .replace(/>/g, '&gt;').replace(/"/g, '&quot;');
    }

    function fmt(n) {
        return String(Math.round(n * 10000) / 10000);
    }

    function toast(msg, isErr) {
        var el = $('<div class="rt-toast' + (isErr ? ' err' : '') + '"></div>').text(msg);
        $('body').append(el);
        setTimeout(function () { el.fadeOut(300, function () { el.remove(); }); }, isErr ? 9000 : 6000);
    }

    // ===================== level curve (mirrors LevelCurve / SuiRates grammar) =====================

    function parseCurve(spec) {
        var bands = [], errors = [];
        String(spec || '').replace(/["',;]/g, ' ').split(/\s+/).forEach(function (tok) {
            if (!tok) return;
            var m = /^(\d+)(?:-(\d+))?:([0-9]*\.?[0-9]+)$/.exec(tok);
            if (!m) { errors.push(tok); return; }
            var from = +m[1], to = m[2] ? +m[2] : from, rate = +m[3];
            if (from < 1 || to < from || to > 255 || rate > 1000) { errors.push(tok); return; }
            bands.push({ from: from, to: to, rate: rate });
        });
        return { bands: bands, errors: errors };
    }

    function serializeCurve(bands) {
        return bands.map(function (b) {
            return (b.from === b.to ? b.from : b.from + '-' + b.to) + ':' + fmt(b.rate);
        }).join(' ');
    }

    function curveRate(spec, level) {
        var rate = 1;
        parseCurve(spec).bands.forEach(function (b) {
            if (level >= b.from && level <= b.to) rate = b.rate;   // later bands win, like the core
        });
        return rate;
    }

    var CURVE_PRESETS = [
        { label: 'No curve', spec: '' },
        { label: 'Fast early game', spec: '1-19:3 20-39:2 40-59:1.5' },
        { label: 'Catch-up to 40', spec: '1-39:2' },
        { label: 'Slow endgame', spec: '50-59:0.75' }
    ];

    // ===================== values =====================

    function field(key) {
        return state ? state.fields.find(function (f) { return f.key === key; }) : null;
    }

    function baseValue(f) {
        if (f.conf != null && f.conf !== '') return f.kind === 'curve' ? serializeCurve(parseCurve(f.conf).bands) : f.conf;
        return f.kind === 'curve' ? '' : fmt(f.default);
    }

    function sameValue(f, a, b) {
        if (f.kind === 'curve') return String(a || '') === String(b || '');
        var x = parseFloat(a), y = parseFloat(b);
        return !isNaN(x) && !isNaN(y) && Math.abs(x - y) < 1e-9;
    }

    function isChanged(f) {
        return !sameValue(f, edited[f.key], baseValue(f));
    }

    function fieldError(f) {
        var v = edited[f.key];
        if (f.kind === 'curve') {
            var p = parseCurve(v);
            return p.errors.length ? 'Fix the highlighted band' : null;
        }
        var n = parseFloat(v);
        if (v === '' || isNaN(n)) return 'Enter a number';
        if (n < f.min || n > f.max) return 'Must be ' + f.min + '–' + f.max;
        return null;
    }

    // Effective value the page currently shows (edited, else conf, else default), as numbers.
    function values() {
        var out = {};
        if (!state) return out;
        state.fields.forEach(function (f) {
            out[f.key] = f.kind === 'curve' ? (edited[f.key] || '') : parseFloat(edited[f.key]);
        });
        return out;
    }

    function notify() {
        var v = values();
        listeners.forEach(function (fn) { try { fn(v); } catch (e) { console.error(e); } });
    }

    // ===================== status (toolbar dots + problem banner) =====================

    function renderStatus() {
        var live = state.live || {};
        var dots = [], banner = [];

        if (state.confError) {
            dots.push('<span class="rt-dot err" title="mangosd.conf unreadable: ' + esc(state.confError) + '">conf</span>');
            banner.push('mangosd.conf could not be read: ' + esc(state.confError));
        } else {
            dots.push('<span class="rt-dot ok" title="Saved to ' + esc(state.confPath) + '">conf</span>');
        }

        if (!live.reachable) {
            dots.push('<span class="rt-dot err" title="' + esc(live.error || 'RA not reachable') + '">core</span>');
            banner.push('Server not reachable over RA. Saving still writes the conf; it applies on the next start.');
        } else if (live.hooks) {
            dots.push('<span class="rt-dot ok" title="Core rate hooks active; the status column shows live values">core</span>');
        } else {
            dots.push('<span class="rt-dot warn" title="This core build has no SuperUI rate hooks">core</span>');
            banner.push('This core build has no SuperUI rate hooks: rates marked <span class="rt-tag hook">H</span> are saved but have no effect until the core is rebuilt.');
        }

        if (live.rts) {
            dots.push('<span class="rt-dot warn" title="RTS world">RTS</span>');
            banner.push('RTS world: its rate.xp_* boot overrides win over these values (edit them in World State).');
        }

        $('#rtStatus').html(dots.join(''));
        $('#rtBanner').html(banner.map(function (b) {
            return '<div class="rt-banner"><i class="fa-solid fa-triangle-exclamation"></i> ' + b + '</div>';
        }).join(''));
    }

    // ===================== fields (one dense table) =====================

    // One line: "2×", "1× → 2×", "default 1×", plus a live tick/mismatch or an input error.
    function nowHtml(f) {
        var live = state.live || {};
        var base = baseValue(f);
        var shown = f.kind === 'curve' ? (base || 'none') : base + f.unit;
        var html;

        if (isChanged(f)) {
            var next = f.kind === 'curve' ? (edited[f.key] || 'none') : edited[f.key] + f.unit;
            html = '<span class="delta">' + esc(shown) + ' → ' + esc(next) + '</span>';
        } else if (f.conf == null) {
            html = '<span title="Not in mangosd.conf; the core default applies">default ' + esc(shown) + '</span>';
        } else {
            html = '<span title="Value in mangosd.conf">' + esc(shown) + '</span>';
        }

        if (live.hooks && f.live != null) {
            var match = f.kind === 'curve'
                ? String(f.live || '') === String(base || '')
                : sameValue(f, f.live, base);
            var liveText = f.kind === 'curve' ? (f.live || 'none') : f.live + f.unit;
            html += match
                ? ' <i class="fa-solid fa-check ok" title="Live in the running core"></i>'
                : ' <span class="mismatch" title="The running core uses ' + esc(liveText) +
                  ': not reloaded yet, or overridden by an RTS world"><i class="fa-solid fa-triangle-exclamation"></i> live ' + esc(liveText) + '</span>';
        }

        var err = fieldError(f);
        if (err) html += ' <span class="mismatch">' + esc(err) + '</span>';
        return html;
    }

    function tagsHtml(f) {
        var live = state.live || {};
        if (!f.hook) return '';
        if (live.reachable && !live.hooks)
            return '<span class="rt-tag inactive" title="Needs a core built with the SuperUI rate hooks; not active in this build">H</span>';
        return '<span class="rt-tag hook" title="Added by the SuperUI rate hooks in the core">H</span>';
    }

    function rowHtml(f) {
        return '<tr class="rt-row" data-row="' + esc(f.key) + '">' +
            '<td class="rt-label">' + esc(f.label) + ' ' + tagsHtml(f) +
            ' <i class="fa-regular fa-circle-question rt-info" title="' + esc(f.help + '\n' + f.key) + '"></i></td>' +
            '<td class="rt-input"><input type="number" class="form-input" data-key="' + esc(f.key) + '"' +
            ' min="' + f.min + '" max="' + f.max + '" step="' + f.step + '" value="' + esc(edited[f.key]) + '" />' +
            '<span class="x">' + esc(f.unit) + '</span></td>' +
            '<td class="rt-now" data-now="' + esc(f.key) + '">' + nowHtml(f) + '</td></tr>';
    }

    function renderFields() {
        var groups = [], byName = {};
        state.fields.forEach(function (f) {
            if (f.kind === 'curve') return;   // the curve lives in the side panel (#rtCurve)
            if (!byName[f.group]) { byName[f.group] = []; groups.push(f.group); }
            byName[f.group].push(f);
        });

        $('#rtFields').html('<table class="rt-grid"><tbody>' + groups.map(function (g) {
            return '<tr class="rt-group-row"><th colspan="3">' + esc(g) + '</th></tr>' + byName[g].map(rowHtml).join('');
        }).join('') + '</tbody></table>');

        state.fields.forEach(function (f) {
            if (f.kind !== 'curve') return;
            var $host = $('#rtCurve');
            if (!$host.length) return;
            $host.attr('data-key', f.key).addClass('rt-curve');
            $('#rtCurveTags').html(tagsHtml(f));
            $('#rtCurveNow').attr('data-now', f.key);
            renderCurve($host);
        });
    }

    // ===================== curve editor =====================

    function renderCurve($host) {
        var key = $host.attr('data-key');
        var spec = edited[key] || '';
        var parsed = parseCurve(spec);
        var bands = parsed.bands;

        var html = '<div class="rt-curve-presets">Presets ' + CURVE_PRESETS.map(function (p, i) {
            return '<button type="button" class="rt-chip" data-preset="' + i + '">' + esc(p.label) + '</button>';
        }).join('') + '</div>';

        html += bands.map(function (b, i) {
            return '<div class="rt-curve-band" data-band="' + i + '">' +
                'Levels <input type="number" class="form-input" data-part="from" min="1" max="255" value="' + b.from + '" />' +
                '–<input type="number" class="form-input" data-part="to" min="1" max="255" value="' + b.to + '" />' +
                ' at <input type="number" class="form-input rate" data-part="rate" min="0" max="1000" step="0.05" value="' + fmt(b.rate) + '" />×' +
                ' <button type="button" class="rt-link" data-remove="' + i + '" title="Remove band"><i class="fa-solid fa-xmark"></i></button></div>';
        }).join('');

        if (parsed.errors.length)
            html += '<div class="rt-now"><span class="mismatch">Could not read: ' + esc(parsed.errors.join(' ')) + '</span></div>';

        html += '<button type="button" class="rt-link" data-add="1"><i class="fa-solid fa-plus"></i> Add band</button>';

        // Strip: one cell per level 1-60, shaded by its multiplier.
        var cells = '';
        for (var lvl = 1; lvl <= 60; lvl++) {
            var r = curveRate(spec, lvl);
            var alpha = r === 1 ? 0 : Math.min(1, 0.2 + Math.abs(Math.log(r || 0.01)) / Math.log(5));
            var color = r >= 1 ? 'var(--accent)' : 'var(--status-warning)';
            cells += '<span title="Level ' + lvl + ': ' + fmt(r) + '×" style="' +
                (r === 1 ? '' : 'background:' + color + ';opacity:' + alpha.toFixed(2)) + '"></span>';
        }
        html += '<div class="rt-curve-strip">' + cells + '</div>' +
            '<div class="rt-curve-axis"><span>Level 1</span><span>20</span><span>40</span><span>60</span></div>';

        $host.html(html);
    }

    function readCurveEditor($host) {
        var bands = [];
        $host.find('.rt-curve-band').each(function () {
            var $b = $(this);
            var from = parseInt($b.find('[data-part=from]').val(), 10);
            var to = parseInt($b.find('[data-part=to]').val(), 10);
            var rate = parseFloat($b.find('[data-part=rate]').val());
            if (isNaN(from) || isNaN(to) || isNaN(rate)) return;
            bands.push({ from: from, to: to, rate: rate });
        });
        return bands;
    }

    function setCurve(key, spec, rerender) {
        edited[key] = spec;
        if (rerender) renderCurve($('.rt-curve[data-key="' + key + '"]'));
        refresh();
    }

    // ===================== pending / refresh =====================

    function refresh() {
        if (!state) return;
        var changed = 0, invalid = 0;
        state.fields.forEach(function (f) {
            var c = isChanged(f);
            if (c) changed++;
            if (fieldError(f)) invalid++;
            $('[data-row="' + f.key + '"]').toggleClass('changed', c);
            $('[data-now="' + f.key + '"]').html(nowHtml(f));
        });

        $('#rtPending').toggleClass('has', changed > 0).text(changed === 0 ? 'No unsaved changes'
            : changed + ' unsaved change' + (changed === 1 ? '' : 's') + (invalid ? ', ' + invalid + ' to fix' : ''));
        $('#rtSave').prop('disabled', changed === 0 || invalid > 0);
        $('#rtRevert').prop('disabled', changed === 0);

        // Highlight a chip when every master field sits at that value.
        var masters = state.fields.filter(function (f) { return f.master; });
        $('#rtMaster .rt-chip').each(function () {
            var m = $(this).attr('data-mult');
            $(this).toggleClass('active', masters.length > 0 && masters.every(function (f) {
                return sameValue(f, edited[f.key], m);
            }));
        });
        notify();
    }

    function applyMaster(mult) {
        var n = parseFloat(mult);
        if (isNaN(n) || n < 0 || n > 1000) { toast('Enter a multiplier between 0 and 1000.', true); return; }
        state.fields.forEach(function (f) {
            if (!f.master) return;
            edited[f.key] = fmt(Math.min(f.max, Math.max(f.min, n)));
            $('#rtFields input[data-key="' + f.key + '"]').val(edited[f.key]);
        });
        refresh();
    }

    // ===================== load / save =====================

    function adopt(s) {
        state = s;
        edited = {};
        state.fields.forEach(function (f) { edited[f.key] = baseValue(f); });
        renderStatus();
        renderFields();
        refresh();
    }

    function load() {
        $.getJSON('/Rates/State', { page: page })
            .done(adopt)
            .fail(function (xhr) {
                $('#rtFields').html('<div class="rt-empty text-danger">Could not load rates: ' +
                    esc(xhr.responseJSON && xhr.responseJSON.error || xhr.statusText) + '</div>');
            });
    }

    function save() {
        var payload = {};
        state.fields.forEach(function (f) { if (isChanged(f)) payload[f.key] = edited[f.key]; });
        $('#rtSave').prop('disabled', true).html('<i class="fa-solid fa-spinner fa-spin"></i> Saving&hellip;');

        $.ajax({
            url: '/Rates/Save', method: 'POST', contentType: 'application/json',
            data: JSON.stringify({ page: page, values: payload, reload: true })
        }).done(function (r) {
            var lines = (r.changes || []).map(function (c) {
                return c.key + ': ' + (c.from == null ? 'default' : (c.from || 'none')) + ' → ' + (c.to || 'none');
            });
            var applied = r.reloaded ? 'Applied live (.reload config).'
                : 'Saved to mangosd.conf. Not applied live: the server did not answer, so it takes effect on the next start.';
            toast((lines.length ? lines.join('\n') + '\n' : 'Nothing changed.\n') + applied, !r.reloaded && lines.length > 0);
            adopt(r.state);
        }).fail(function (xhr) {
            toast('Save failed: ' + (xhr.responseJSON && xhr.responseJSON.error || xhr.statusText), true);
            refresh();
        }).always(function () {
            $('#rtSave').html('<i class="fa-solid fa-bolt"></i> Save &amp; apply');
        });
    }

    // ===================== events =====================

    $(document).on('input', '#rtFields .rt-input input', function () {
        edited[$(this).attr('data-key')] = $(this).val();
        refresh();
    });

    $(document).on('input', '.rt-curve-band input', function () {
        var $host = $(this).closest('.rt-curve');
        setCurve($host.attr('data-key'), serializeCurve(readCurveEditor($host)), false);
    });
    $(document).on('change', '.rt-curve-band input', function () {
        var $host = $(this).closest('.rt-curve');
        setCurve($host.attr('data-key'), serializeCurve(readCurveEditor($host)), true);
    });
    $(document).on('click', '.rt-curve [data-add]', function () {
        var $host = $(this).closest('.rt-curve');
        var bands = readCurveEditor($host);
        var start = bands.length ? Math.min(255, bands[bands.length - 1].to + 1) : 1;
        bands.push({ from: start, to: Math.min(255, start + 9), rate: 2 });
        setCurve($host.attr('data-key'), serializeCurve(bands), true);
    });
    $(document).on('click', '.rt-curve [data-remove]', function () {
        var $host = $(this).closest('.rt-curve');
        var bands = readCurveEditor($host);
        bands.splice(parseInt($(this).attr('data-remove'), 10), 1);
        setCurve($host.attr('data-key'), serializeCurve(bands), true);
    });
    $(document).on('click', '.rt-curve [data-preset]', function () {
        var $host = $(this).closest('.rt-curve');
        setCurve($host.attr('data-key'), CURVE_PRESETS[parseInt($(this).attr('data-preset'), 10)].spec, true);
    });

    $('#rtMaster').on('click', '.rt-chip', function () { applyMaster($(this).attr('data-mult')); });
    $('#rtMasterApply').on('click', function () { applyMaster($('#rtMasterCustom').val()); });
    $('#rtMasterCustom').on('keydown', function (e) { if (e.key === 'Enter') applyMaster($(this).val()); });

    $('#rtRevert').on('click', function () { if (state) adopt(state); });
    $('#rtSave').on('click', save);

    window.addEventListener('beforeunload', function (e) {
        if (state && state.fields.some(isChanged)) { e.preventDefault(); e.returnValue = ''; }
    });

    window.Rates = {
        page: page,
        values: values,
        curveRate: curveRate,
        onChange: function (fn) { listeners.push(fn); if (state) fn(values()); },
        esc: esc,
        fmt: fmt,
        toast: toast
    };

    load();
})();

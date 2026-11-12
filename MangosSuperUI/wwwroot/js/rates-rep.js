// Rates > Reputation — per-faction overrides (world table reputation_reward_rate).
// Factions come from Faction.dbc (reputation-bearing rows), grouped under their
// parent faction. A row left at 1 / 1 / 1 is deleted on save (= stock behaviour).
// The "Overall" column multiplies in the page's global and per-source rates live.
(function () {
    'use strict';
    if (!window.Rates) return;
    var esc = Rates.esc, fmt = Rates.fmt;

    var factions = [];     // from /Rates/Factions
    var edits = {};        // id -> {quest, creature, spell} (strings from inputs)
    var pageValues = {};

    function loaded(f) {
        return { quest: fmt(f.quest), creature: fmt(f.creature), spell: fmt(f.spell) };
    }

    function current(f) {
        return edits[f.id] || loaded(f);
    }

    function changed(f) {
        var c = current(f), l = loaded(f);
        return ['quest', 'creature', 'spell'].some(function (k) { return parseFloat(c[k]) !== parseFloat(l[k]); });
    }

    function invalid(f) {
        var c = current(f);
        return ['quest', 'creature', 'spell'].some(function (k) {
            var v = parseFloat(c[k]);
            return isNaN(v) || v < 0 || v > 1000;
        });
    }

    function custom(f) {
        var c = current(f);
        return ['quest', 'creature', 'spell'].some(function (k) { return parseFloat(c[k]) !== 1; });
    }

    function overall(f) {
        var g = num(pageValues['Rate.Reputation.Gain']);
        var c = current(f);
        function one(src, factionRate) { return fmt(g * num(pageValues[src]) * num(factionRate)) + '×'; }
        return one('Rate.Reputation.Kill', c.creature) + ' / ' +
            one('Rate.Reputation.Quest', c.quest) + ' / ' +
            one('Rate.Reputation.Spell', c.spell);
    }

    function num(v) {
        var x = parseFloat(v);
        return isNaN(x) ? 1 : x;
    }

    function groups() {
        var parents = {};
        factions.forEach(function (f) { if (f.parentId) parents[f.parentId] = true; });
        var byKey = {}, order = [];
        factions.forEach(function (f) {
            var key = f.parentId || (parents[f.id] ? f.id : 0);
            if (!byKey[key]) {
                byKey[key] = { key: key, label: '', rows: [] };
                order.push(byKey[key]);
            }
            byKey[key].rows.push(f);
        });
        order.forEach(function (g) {
            var head = factions.find(function (f) { return f.id === g.key; });
            g.label = g.key === 0 ? 'Independent factions' : (head ? head.name : 'Faction ' + g.key);
            g.rows.sort(function (a, b) {
                if (a.id === g.key) return -1;
                if (b.id === g.key) return 1;
                return a.name.localeCompare(b.name);
            });
        });
        order.sort(function (a, b) {
            if (a.key === 0) return 1;
            if (b.key === 0) return -1;
            return a.label.localeCompare(b.label);
        });
        return order;
    }

    function render() {
        var filter = ($('#rfFilter').val() || '').toLowerCase();
        var onlyCustom = $('#rfOnlyCustom').is(':checked');

        var body = groups().map(function (g) {
            var rows = g.rows.filter(function (f) {
                if (filter && f.name.toLowerCase().indexOf(filter) < 0 && g.label.toLowerCase().indexOf(filter) < 0) return false;
                if (onlyCustom && !custom(f) && !changed(f)) return false;
                return true;
            });
            if (!rows.length) return '';
            return '<tr class="rt-parent"><td colspan="5">' + esc(g.label) + '</td></tr>' + rows.map(function (f) {
                var c = current(f);
                function input(k) {
                    return '<input type="number" class="form-input" min="0" max="1000" step="0.05" data-id="' + f.id +
                        '" data-k="' + k + '" value="' + esc(c[k]) + '" />';
                }
                return '<tr class="' + (custom(f) ? 'custom' : '') + '" data-fid="' + f.id + '">' +
                    '<td>' + esc(f.name) + ' <span class="rt-key">' + f.id + '</span></td>' +
                    '<td class="num">' + input('creature') + '</td>' +
                    '<td class="num">' + input('quest') + '</td>' +
                    '<td class="num">' + input('spell') + '</td>' +
                    '<td class="num" data-overall="' + f.id + '">' + overall(f) + '</td></tr>';
            }).join('');
        }).join('');

        $('#rfTable').html(
            '<table class="rt-table"><thead><tr><th>Faction</th><th class="num">Kills ×</th>' +
            '<th class="num">Quests ×</th><th class="num">Spells ×</th>' +
            '<th class="num" title="Global rate × source rate × faction override (kills / quests / spells)">Overall k / q / s</th>' +
            '</tr></thead><tbody>' + (body || '<tr><td colspan="5" class="text-muted">No factions match.</td></tr>') +
            '</tbody></table>');
        refreshPending();
        fitHeight();
    }

    function refreshPending() {
        var n = factions.filter(changed).length;
        var bad = factions.filter(invalid).length;
        $('#rfPending').toggleClass('has', n > 0).text(n === 0 ? ''
            : n + ' faction' + (n === 1 ? '' : 's') + ' changed' + (bad ? ', ' + bad + ' with invalid values' : ''));
        $('#rfSave').prop('disabled', n === 0 || bad > 0);
    }

    function load() {
        $.getJSON('/Rates/Factions').done(function (r) {
            if (!r.success) {
                $('#rfTable').html('<div class="rt-empty"></div>').find('.rt-empty').text('Could not read reputation_reward_rate: ' + (r.error || 'unknown error'));
                return;
            }
            factions = r.factions || [];
            edits = {};
            if (!factions.length) {
                $('#rfTable').html('<div class="rt-empty"></div>').find('.rt-empty').text('No reputation factions found. Faction.dbc did not load.');
                return;
            }
            render();
        }).fail(function (xhr) {
            $('#rfTable').html('<div class="rt-empty"></div>').find('.rt-empty').text('Could not load factions: ' + xhr.statusText);
        });
    }

    $(document).on('input', '#rfTable input', function () {
        var id = parseInt($(this).attr('data-id'), 10);
        var f = factions.find(function (x) { return x.id === id; });
        if (!f) return;
        edits[id] = edits[id] || loaded(f);
        edits[id][$(this).attr('data-k')] = $(this).val();
        $('[data-overall="' + id + '"]').text(overall(f));
        $('tr[data-fid="' + id + '"]').toggleClass('custom', custom(f));
        refreshPending();
    });

    // The table scrolls inside its panel; size it to end at the bottom of the window so the
    // page itself never scrolls (the area above it varies, e.g. when a status banner shows).
    function fitHeight() {
        var el = document.getElementById('rfTable');
        if (!el || window.innerWidth <= 1200) { if (el) el.style.maxHeight = ''; return; }
        el.style.maxHeight = Math.max(240, window.innerHeight - el.getBoundingClientRect().top - 32) + 'px';
    }
    $(window).on('resize', fitHeight);

    $('#rfFilter').on('input', render);
    $('#rfOnlyCustom').on('change', render);

    $('#rfSave').on('click', function () {
        var rows = factions.filter(changed).map(function (f) {
            var c = current(f);
            return { faction: f.id, quest: parseFloat(c.quest), creature: parseFloat(c.creature), spell: parseFloat(c.spell) };
        });
        $('#rfSave').prop('disabled', true);
        $.ajax({
            url: '/Rates/SaveFactions', method: 'POST', contentType: 'application/json',
            data: JSON.stringify({ rows: rows, reload: true })
        }).done(function (r) {
            Rates.toast(r.changed + ' faction override' + (r.changed === 1 ? '' : 's') + ' saved. ' +
                (r.reloaded ? 'Applied live.' : 'Not applied live: the server did not answer, so it takes effect on the next start.'),
                !r.reloaded);
            load();
        }).fail(function (xhr) {
            Rates.toast('Save failed: ' + (xhr.responseJSON && xhr.responseJSON.error || xhr.statusText), true);
            refreshPending();
        });
    });

    Rates.onChange(function (v) {
        pageValues = v;
        factions.forEach(function (f) { $('[data-overall="' + f.id + '"]').text(overall(f)); });
        fitHeight();   // the status banner above may have just appeared or gone
    });

    load();
})();

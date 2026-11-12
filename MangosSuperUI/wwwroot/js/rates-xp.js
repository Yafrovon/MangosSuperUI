// Rates > Experience — leveling preview. Kills-to-level per 10-level band at stock
// 1× against the settings currently in the editor, using the world DB's
// player_xp_for_level and the core's same-level kill formula
// (MaNGOS::XP::BaseGain: level*5 + 45, then Rate.XP.Kill, then the level curve).
(function () {
    'use strict';
    if (!window.Rates) return;

    var xpTable = null;   // [{level, xp}]
    var latest = null;

    function mobXp(level) {
        return level * 5 + 45;
    }

    function killsFor(level, xpNeeded, killRate, curveSpec) {
        var perKill = Math.round(mobXp(level) * killRate);                  // Formulas.h nearbyint
        var curve = Rates.curveRate(curveSpec, level);
        if (curve !== 1) perKill = Math.floor(perKill * curve + 0.5);      // Player::GiveXP
        return perKill < 1 ? Infinity : xpNeeded / perKill;               // fractional, so ratios stay exact
    }

    function n(v) {
        return v === Infinity ? '∞' : Math.round(v).toLocaleString();
    }

    function render() {
        if (!xpTable || !latest) return;
        var killRate = isNaN(latest['Rate.XP.Kill']) ? 1 : latest['Rate.XP.Kill'];
        var curve = latest['Rate.XP.LevelCurve'] || '';

        var bands = [], total = { xp: 0, base: 0, now: 0 };
        xpTable.forEach(function (row) {
            if (row.level >= 60) return;   // vanilla cap; xp_for_next_level at 60 is never used
            var idx = Math.floor((row.level - 1) / 10);
            if (!bands[idx]) bands[idx] = { from: row.level, to: row.level, xp: 0, base: 0, now: 0 };
            var b = bands[idx];
            b.to = row.level;
            b.xp += row.xp;
            b.base += killsFor(row.level, row.xp, 1, '');
            b.now += killsFor(row.level, row.xp, killRate, curve);
        });
        bands = bands.filter(Boolean);
        bands.forEach(function (b) { total.xp += b.xp; total.base += b.base; total.now += b.now; });

        function speed(base, now) {
            if (now === Infinity) return '<span class="text-muted">no kill XP</span>';
            var x = base / now;
            return Math.abs(x - 1) < 0.005 ? '<span class="text-muted">same</span>' : Rates.fmt(Math.round(x * 100) / 100) + '× ' + (x > 1 ? 'faster' : 'slower');
        }

        var rows = bands.map(function (b) {
            return '<tr><td>' + b.from + ' → ' + (b.to + 1) + '</td>' +
                '<td class="num">' + n(b.xp) + '</td>' +
                '<td class="num">' + n(b.base) + '</td>' +
                '<td class="num">' + n(b.now) + '</td>' +
                '<td class="num">' + speed(b.base, b.now) + '</td></tr>';
        }).join('');

        $('#rtPreview').removeClass('rt-empty').html(
            '<table class="rt-table"><thead><tr>' +
            '<th>Levels</th><th class="num">XP needed</th><th class="num">Kills at 1×</th>' +
            '<th class="num">Kills now</th><th class="num">Change</th></tr></thead><tbody>' + rows +
            '<tr class="total"><td>Total</td><td class="num">' + n(total.xp) + '</td>' +
            '<td class="num">' + n(total.base) + '</td><td class="num">' + n(total.now) + '</td>' +
            '<td class="num">' + speed(total.base, total.now) + '</td></tr></tbody></table>');
    }

    $.getJSON('/Rates/XpTable').done(function (r) {
        if (!r.success || !r.levels || !r.levels.length) {
            $('#rtPreview').text('No XP table available' + (r.error ? ': ' + r.error : '.'));
            return;
        }
        xpTable = r.levels;
        render();
    }).fail(function (xhr) {
        $('#rtPreview').text('Could not load the XP table: ' + xhr.statusText);
    });

    Rates.onChange(function (values) { latest = values; render(); });
})();

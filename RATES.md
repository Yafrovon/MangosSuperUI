# Rates — XP / Reputation / Honor multipliers

Gameplay Tuning > **Rates** (new sidebar group next to Spells and World & Maps): three pages,
`/Rates/Experience`, `/Rates/Reputation`, `/Rates/Honor`, for changing progression speed at a
macro scale (2×, 3×, 2.13×, and so on).

## Status (2026-11-10)

| Piece | State |
|---|---|
| Web pages, `RatesController`, `RatesService`, `RatesCatalog` | Built. Verified locally against a stand-in conf: render, master multiplier, curve editor, diff tracking, conf write and append, server-side validation |
| Core hooks (box, `~/vmangos/src`) | Source **uncommitted**. **Deployed** 2026-11-10 ~22:39 in mangosd build `634b8d92` (raid-autopilot session, binary only). Over RA, `.sui rates` returns `SUI rates: hooks=1 rts=0` with every key at the conf/stock values |
| Web pages on the box | **Published and verified live** 2026-11-11 (compact layout). All three pages read the box conf and show live values (`hooks=1`, no mismatches). The XP preview uses the real `player_xp_for_level` (60 rows). The faction list shows 44 factions. Save round trip: `Rate.Rest.Offline.InWilderness` 1 → 1.5 → 1. The conf line and live value followed each step, and the conf ended byte-identical (sha `c96e2ab1…`). Faction round trip: Ravenholdt quest 2× added, `Loaded 1 reputation_reward_rate` in Server.log, then removed (table empty again). Runs were coordinated with the raid-autopilot session during its setup window |

## How values are stored and applied

- Every rate is a `mangosd.conf` key, edited through `MangosdConfigDocument` (keys are updated in
  place; missing keys are appended). World State snapshots capture `mangosd.conf`, so **each
  parked world keeps its own rates**.
- Save → audit row (`config/rates_update`) → `.reload config` over RA → `.sui rates` read-back.
  The core reads all of these keys at award time, so a reload is enough; no restart needed.
- Per-faction reputation uses the stock world table `reputation_reward_rate`
  (`faction, quest_rate, creature_rate, spell_rate`) + `.reload reputation_reward_rate`. A
  faction set back to 1/1/1 has its row deleted. Faction names come from server `Faction.dbc`
  (`DbcService.ReputationFactions`: fields 0/1/18/19, profession-spec pseudo-factions skipped).
- Writes take a `WorldMaintenanceGate` lease and return 409 while a world restore runs.

## What stock VMaNGOS already had (no core change)

`Rate.XP.Kill`, `Rate.XP.Kill.Elite` (a factor on top of Kill, elites only), `Rate.XP.Quest`,
`Rate.XP.Explore`, `Rate.Rest.*`, `Rate.XP.Personal.Min/Max` (bounds for the player `.xprate`
command), `Rate.Reputation.Gain` (all sources, gains and losses),
`Rate.Reputation.LowLevel.Kill/Quest` (gray-content penalty), `RpDecay`, and the
`reputation_reward_rate` table. Max-level quest gold is **not** scaled by `Rate.XP.Quest`.

## Core hooks added (SuperUI)

| Key | Seam | Notes |
|---|---|---|
| `Rate.XP.LevelCurve` | `Player::GiveXP`, after the personal rate | `"1-19:3 20-39:2"`; covers every XP source; players only, not pets; parsed by `SuiRates::LoadLevelCurve` on boot and on reload |
| `Rate.Reputation.Kill/Quest/Spell` | `Player::CalculateReputationGain` | multiplies with Gain, low-level, and per-faction rates |
| `Rate.Honor.Kill/Bonus/Quest/Dishonor` | `HonorMgr::ApplyRate` inside `HonorMgr::Add` | per `HonorType`; `OTHER` (GM `.honor add`) never scaled; BG scoreboard shows the scaled bonus |
| `Rate.Honor.RankPoints` | `HonorMaintenancer::DistributeRankPoints` | weekly RP earning, still capped by `MaximumRpAtLevel` |
| `.sui rates` | `SuiRates.cpp`, console-enabled | prints `SUI rates: hooks=1 rts=N` plus `key=value` lines; the page uses it for the Live column and to detect a hook-less build |
| RTS re-apply | `World::LoadConfigSettings(reload)` → `SuiRts::ReapplyRateOverrides()` | `.reload config` used to drop an RTS world's `rate.xp_*` boot overrides |

Files: `World.h/.cpp`, `Objects/Player.cpp`, `HonorMgr.h/.cpp`, `Battlegrounds/BattleGround.cpp`,
`Chat/Chat.h/.cpp`, `SuperUiContent/SuiWorld/RTS/SuiRts.h/.cpp`, new
`SuperUiContent/SuiWorld/Shared/SuiRates.h/.cpp`, `CMakeLists.txt`, `mangosd/mangosd.conf.dist.in`.

## Design notes worth keeping

- **A plain honor multiplier does not speed up ranking.** Vanilla RP earning is interpolated from
  your weekly *standing* (`CalculateRpEarning` over the week's FX/FY brackets), so scaling
  everyone's CP leaves standings, and therefore RP, unchanged. `Rate.Honor.RankPoints` and
  `RpDecay` are what change rank speed. The Honor page explains this up front.
- The master multiplier on the Reputation page sets only `Rate.Reputation.Gain`; setting the
  by-source rates too would compound (2× would become 4×).
- An older core ignores unknown conf keys, so hook keys can be saved before the core is rebuilt;
  the page marks them "hook · not active" until `.sui rates` reports `hooks=1`.
- In an RTS world, `rate.xp_kill`, `rate.xp_kill_elite` and `rate.xp_quest` from
  `superui_worldstate` override the conf at boot (and now after reload). The page warns when
  `rts=1`, and the Live column shows the value that wins.

## To finish (owner)

1. Deploy (2026-11-10): the raid-autopilot session owns builds and restarts on the box. mangosd
   runs under gdb in screen `mangosd`, deployed with `scratch/autopilot/deploy.sh` (MSUIClient
   repo), which copies the binary only. **Never use `systemctl restart mangosd`**: it starts a
   second server. The rate hooks go out with that session's next build after its current
   Ragnaros attempt. No `make install` is needed because missing keys default to 1.
2. Run `.sui rates` from the console and expect `hooks=1`. Open the Rates pages and expect the
   "Core rate hooks active" status and a Live value on every row.
3. In-game check: set Kill XP to 2 and kill a normal mob (XP doubles), give reputation by quest
   with `Rate.Reputation.Quest` = 3, and get a BG bonus honor tick with `Rate.Honor.Bonus` = 2.

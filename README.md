# Archipelago for Escape from Tarkov (SPT)

An [Archipelago](https://archipelago.gg) multiworld randomizer for **Single Player Tarkov 4.1.6**
(Escape from Tarkov build 0.16.9.5.40743).

Still on SPT 4.0.13? Use release 1.1.1 (branch `spt-4.0`).

**→ New here? Start with the [tutorial](TUTORIAL.md).** Downloads are on the
[Releases page](https://github.com/Pegzzzz/Archipelago-Tarkov/releases).

SPT only. It has nothing to do with the official PvE or PvP modes and must never be used with them.

## Features

- **Locked progression:** the ten maps (Ground Zero to The Lab), the seven main traders (Prapor, Therapist, Skier,
  Peacekeeper, Mechanic, Ragman, Jaeger) and the Flea Market are items. Locked maps disappear from the map screen and
  transits to them are switched off.
- **Archipelago contracts (default):** Tarkov's quests are replaced by a line of contracts from the Archipelago
  trader, written for your seed and ordered so your items make each one possible. Kill, boss, survive and hand-in
  jobs that pay XP, roubles and trader standing. The line ends with the Kappa contract, which rewards the Kappa
  container and is the goal. 125 contracts by default (vanilla Kappa takes 254 quests), 40 to 200 if you like.
- **One trader:** with contracts, every other trader is locked. The Archipelago trader sells the stock of each main
  trader you've received, at the loyalty your level and standing give, and always has medications and ammo for your
  guns.
- **215 checks** with the default options: 125 contracts and the Kappa contract (126), every hideout area level (58),
  one PMC extraction per map (10), Scav and PMC kill totals (8) and the first kill of each boss (13).
- **Vanilla quests instead (`quest_mode: vanilla`):** every main-trader quest that can be completed reliably on a
  fresh profile is a check (364; 453 checks in all with the other defaults).
- **Lootsanity (optional):** pick item categories, and every item type in them is a check (up to ~3,200). The first
  spawn of an unchecked type in each raid is an *Archipelago Item*; extract with it to send the check. You never get
  the original item. The client plugin draws these with the Archipelago symbol.
- **Craftsanity (optional):** the first craft of each hideout recipe (~180) is a check and gives no item.
- **Randomized shops (optional):** the main traders sell random items of about the same value, from the same category
  or anything, with a share of offers in stock, random purchase limits by value tier and loyalty level, fixed for the
  seed or rerolled at every restock. Not checks.
- **Tracker (client plugin):** F8 or the Tasks screen shows the checks left on each map, and where this raid's
  Archipelago Items are.
- **Goals:** the Kappa contract; or, with vanilla quests, a number of quests, Fence's *Collector* (Kappa),
  extracting from every map, or killing every boss.
- **Items:** maps, traders, Flea Market access, experience, trader reputation, money, and crates of weapons, armor,
  medicine, ammo, keys, stims, barter goods and provisions. Traps: Fence Grudge, Exhaustion, Scav Cooldown.
- **Random starting kit (optional):** a weapon with matching ammo and magazines, armor, rig, backpack, meds, food and
  money rolled from the seed, on top of or instead of your starting stash, in three quality levels.
- **DeathLink.**
- **Logic that knows Tarkov:** quest prerequisites, the maps each quest's objectives need, trader loyalty and player
  level (estimated from how many maps you own), weapon parts for the Gunsmith quests, and a keycard for The Lab.
- **Plays from the in-game messenger:** link your profile with `connect server:port slot` to the *Archipelago*
  contact. Money, crates and the starting kit arrive in the System messages, ready to receive.
- **Works with other SPT mods:** when another mod replaces part of SPT, the mod hooks the replacement. The 1.x
  releases were tested on SPT 4.0.13 with Fika, SVM, WTT-CommonLib and a custom trader.
- **Safe to stop and restart:** the SPT server and the Archipelago server can both restart at any time without
  losing or duplicating items, and progress made while disconnected is sent on reconnect.

## Status

Early release (v2.0.0). Everything is tested against a real SPT 4.1.6 server and a real Archipelago server, with a
script that makes the same requests as the game client, but it hasn't had a full in-game playthrough yet. Please
[open an issue](https://github.com/Pegzzzz/Archipelago-Tarkov/issues) with your newest `SPT_Runtime\user\logs\spt` file if
something goes wrong.

Known limits: newly received maps appear after your next raid or a game restart, and the *Collector* goal has one
missable step (see the tutorial).

## For developers

| Path | What |
|---|---|
| `apworld/tarkov/` | The APWorld: items, locations, logic, options, docs, unit tests |
| `apworld/tarkov/data/tarkov_data.json` | Quest/hideout/extract/item table, generated from SPT 4.1.6's database by `tools/extract_data.py`. Embedded in the server mod too, and checked by hash when connecting |
| `server-mod/ArchipelagoTarkov/` | SPT 4.1.6 server mod (C#, .NET 10): Archipelago client, locks, check detection, item delivery, chat commands, Harmony patches |
| `client-plugin/ArchipelagoTarkov.Client/` | Optional BepInEx plugin: in-raid notifications and DeathLink kills (reaches the game by reflection) |
| `tests/` | End-to-end suite (real SPT server + real MultiServer + scripted game client), generation fuzzer, plugin tests under Mono |
| `tools/package.sh` | Builds the release files |
| `.github/workflows/build.yml` | Builds everything, runs the APWorld tests, and attaches the files to a release when one is published (or when run by hand with a release tag) |

**Building**

```
dotnet build server-mod/ArchipelagoTarkov -c Release -p:SptPath="C:\Games\SPT"
dotnet build client-plugin/ArchipelagoTarkov.Client -c Release -p:SptPath="C:\Games\SPT"
```

`SptPath` is your SPT 4.1.6 folder (the one with `EscapeFromTarkov.exe`). The server mod needs the .NET 10 SDK; the
plugin targets .NET Framework 4.7.2. For the APWorld, copy `apworld/tarkov` into an Archipelago checkout's `worlds/`
folder and run `python Launcher.py "Build APWorlds" -- "Escape from Tarkov"`.

**Testing**

| Test | Command |
|---|---|
| APWorld unit tests | `python -m pytest worlds/tarkov/test test/general` in an Archipelago checkout |
| Generation fuzz | `python tests/fuzz_apworld.py 60 out/` from an Archipelago checkout |
| End to end | `SPT_DIR=… SPT_DOTNET=… AP_DIR=… AP_PYTHON=… python tests/integration/run_integration.py` (see the top of the script; it wipes the profiles of the SPT install it's given) |
| Client plugin | `CLIENT_REFS=… tests/plugin/run.sh` (needs Mono) |

The end-to-end suite (42 checks) covers linking, every lock, every check and item type, raids, transits, Scav
raids, DeathLink, restarts of both servers, profile protection, version refusal and the goal. On SPT 4.1.6 it passes
with no other mods. Release 1.1.1 also passed it on SPT 4.0.13 with Fika 2.3.2, SVM 2.1.3, WTT-CommonLib 2.0.24 and a
custom trader installed.

## Credits

- Archipelago integration: **Pegz**
- SPT by the SPT and SP Tushonka teams
- [Archipelago](https://archipelago.gg) and its community

MIT license. Escape from Tarkov is made by Battlestate Games; this project isn't affiliated with Battlestate Games,
SPT or Archipelago.

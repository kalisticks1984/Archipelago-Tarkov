# My setup

The SPT install, mods and settings this branch was played and tested with. The config files themselves are in
[my-setup/](my-setup/).

## Game

- SPT 4.1.6 (server on .NET 10), Escape from Tarkov client with BepInEx
- Archipelago 0.6.7, with `tarkov.apworld` from this branch in `custom_worlds`

## Server mods (`SPT_Runtime/user/mods`)

| Mod | Version |
|---|---|
| ArchipelagoTarkov (this repo) | 2.0.0 |
| [SVM] Server Value Modifier | 2.2.3 |
| SAIN server mod | 4.5.1 |
| Lots of Loot Redux (archangelwtf) | 4.2.2 |
| UI Fixes server (Tyfon) | 6.0.4 |
| Show Me The Money server (SwiftXP) | 3.0.2 |
| WTT Server Common Lib | 3.0.6 |
| Load Ammo Anim server | 2.0.0 |
| Classic Movement server | 0.0.1 |
| Backpack Gun Slot | 1.0.1 |
| ModSource server | 1.0.0 |

## Client plugins (`BepInEx/plugins`)

| Plugin | Version |
|---|---|
| ArchipelagoTarkov.Client (this repo) | 1.0.0 |
| SAIN | 4.5.1 |
| BigBrain (DrakiaXYZ) | 1.5.0 |
| Waypoints (DrakiaXYZ) | 1.9.0 |
| UI Fixes (Tyfon) | 6.0.4 |
| Show Me The Money (SwiftXP) | 3.0.2 |
| WTT Client Common Lib | 3.0.6 |
| Load Ammo Anim | 2.0.0 |
| Continuous Load Ammo (ozen) | 1.2.0 |
| Bot Healing Fixes (ozen) | 1.0.0 |
| Classic Movement | 1.0.0 |
| Backpack Gun Slot | 1.0.1 |
| Cabinet Looter | 1.0.2 |
| Task Item Indicator | 1.0.0 |
| ModSource | 1.0.0 |

## Settings

- **Archipelago YAML:** [my-setup/kaliEFTtest.yaml](my-setup/kaliEFTtest.yaml). Archipelago contracts (125), start
  on Ground Zero with Prapor and Therapist, hideout checks, 1 extract per map, short kill milestones, boss checks,
  flea market locked, 10% traps, no lootsanity, craftsanity or shop randomizer. (`goal: quest_count` is ignored
  with contracts: the goal is the Kappa contract.)
- **SVM:** preset `Noname` ([my-setup/SVM-preset-Noname.json](my-setup/SVM-preset-Noname.json)), with the Items,
  Hideout, Traders, Loot (and airdrops), Player (fatigue, leg stamina), Raids (car co-op), Flea Market, Services
  (insurance, repair), Quests and Cases sections enabled. The Archipelago mod overrides SVM's trader unlocks on
  linked profiles.
- **SAIN:** server config [my-setup/SAIN-ServerConfig.json](my-setup/SAIN-ServerConfig.json) (the Death Wish
  preset is disabled); the preset in use is "I Like Pain [Modified]".
- **Lots of Loot Redux:** the `default` preset.
- **Archipelago client plugin:** [my-setup/com.pegzzzz.archipelago-tarkov.client.cfg](my-setup/com.pegzzzz.archipelago-tarkov.client.cfg)
  (tracker on F8 and on the Tasks screen, notifications and DeathLink deaths allowed, Archipelago item icons on).

SAIN is the heaviest mod here; if raids stutter, try without it first.

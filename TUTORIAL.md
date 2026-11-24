# Playing Escape from Tarkov (SPT) in Archipelago — Tutorial

This guide takes you from a working SPT install to playing Tarkov in an Archipelago multiworld.
Setting up the mod takes about 10 minutes.

> **Status: early release (v2.1.0, for SPT 4.1.6).** Everything is tested against a real SPT 4.1.6 server and a real Archipelago
> server, with a script standing in for the game client. It hasn't had a full in-game playthrough yet, so expect rough
> edges. Please report problems (see [Reporting problems](#reporting-problems)).

> **Single Player Tarkov only.** This works with SPT, the offline single-player version of Tarkov. It has nothing to
> do with the official PvE or PvP modes and must never be used with them.

---

## 1. What you need

| Thing | Where to get it |
|---|---|
| A legal copy of Escape from Tarkov | [escapefromtarkov.com](https://www.escapefromtarkov.com) (SPT needs it installed to make its copy) |
| **SPT 4.1.6** (game build `0.16.9.5.40743`) | See [section 2](#2-get-spt-416) |
| The .NET 10 runtimes SPT 4.1 needs | The *.NET Runtime* and *ASP.NET Core Runtime* 10.0 (Windows x64) from [dotnet.microsoft.com](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) |
| Archipelago **0.6.4 or newer** | [Archipelago releases](https://github.com/ArchipelagoMW/Archipelago/releases) — install it like any program |
| This mod's release files | The [**Releases** page](https://github.com/Pegzzzz/Archipelago-Tarkov/releases) of this repository |

From the latest release, download:

- `ArchipelagoTarkov-<version>-SPT-4.1.6.zip` — the SPT server mod and the (optional) client plugin
- `tarkov.apworld` — teaches Archipelago about Tarkov
- `Escape.from.Tarkov.yaml` — your settings file (GitHub swaps the spaces for dots; the name doesn't matter)

---

## 2. Get SPT 4.1.6

This release is made for **SPT 4.1.6**, the version now maintained by the SP Tushonka team. It won't load on SPT 4.0.x
or 3.x.

- **You already play SPT 4.1.6:** nothing to do. Use a separate install if you don't want to touch your usual one.
- **You have an older SPT 4.1.x:** update it with the 7z from the
  [SPT 4.1.6 release](https://github.com/SP-Tushonka/build/releases/tag/4.1.6), following the update steps on the
  [SPT wiki](https://wiki.sp-tushonka.com).
- **You don't have SPT yet, or you're on 4.0:** install 4.1.6 with SPT's installer or the manual install
  instructions, both on the [SPT wiki](https://wiki.sp-tushonka.com). Your game client must be on build
  `0.16.9.5.40743`.
- **You want to stay on SPT 4.0.13:** use release 1.1.1 of this mod (the `spt-4.0` branch). It isn't updated anymore.

Before going on, start SPT once without any mods (`SPT_Runtime\SPT.Server.exe`, then `SPT_Runtime\SPT.Launcher.exe`)
and check that you get to the main menu. That way, if something breaks later, you know it's the mod.

Your SPT folder should look like this (the names that matter):

```
<your SPT folder>\
├── EscapeFromTarkov.exe
├── BepInEx\
└── SPT_Runtime\
    ├── SPT.Server.exe
    ├── SPT.Launcher.exe
    └── user\
```

---

## 3. Install the mod

1. Close the game, the SPT launcher and the SPT server.
2. Open `ArchipelagoTarkov-<version>-SPT-4.1.6.zip` and extract **both folders inside it** (`SPT_Runtime` and `BepInEx`) into
   your SPT folder (the one with `EscapeFromTarkov.exe`). Say **yes** if Windows asks to merge folders.
3. Check that these two files exist:
   - `SPT_Runtime\user\mods\ArchipelagoTarkov\ArchipelagoTarkov.dll` — the server mod (**required**)
   - `BepInEx\plugins\ArchipelagoTarkov\ArchipelagoTarkov.Client.dll` — the client plugin (optional, recommended)
4. Start `SPT_Runtime\SPT.Server.exe`. In its console you should see:

   ```
   Archipelago for Tarkov loaded (364 quest checks, 0 linked profiles). Message the Archipelago contact in game to connect.
   ```

What the client plugin adds: on-screen notifications during raids (items received, items sent, chat), and dying
in raid when someone else's DeathLink hits you. Without it everything else works the same; messages just wait in
your messenger.

> **Other mods:** the mod is built to work next to other SPT 4.1 mods: when another mod replaces part of SPT, it hooks
> the replacement. The 1.x releases were tested with Fika, SVM (Server Value Modifier), WTT-CommonLib and a custom
> trader on SPT 4.0.13; on 4.1.6 it has been tested on its own so far. Only the seven main traders and the ten
> standard maps are locked; traders and maps added by other mods are left alone.
>
> **Fika:** install the server mod on the PC that runs the SPT server. Every player who wants in-raid notifications
> or DeathLink installs the client plugin on their own PC. Each player links their own profile to their own slot.

---

## 4. Install the APWorld

Only the person who **generates** the multiworld needs this (it doesn't hurt to have it anyway).

1. Double-click `tarkov.apworld`.
2. Archipelago opens and says the world was installed. That's it.

(If double-clicking doesn't work, copy the file into the `custom_worlds` folder of your Archipelago install,
usually `C:\ProgramData\Archipelago\custom_worlds`.)

---

## 5. Set up your YAML

Open `Escape.from.Tarkov.yaml` in a text editor (Notepad works).

1. Change `name: Player{number}` to your slot name, for example `name: TarkovRunner`. Keep it short, without spaces
   if you can (you'll type it in game).
2. Pick your options. Each option has a list of choices with weights; put `50` next to the one you want and `0` next
   to the others. The defaults are a good first game.

| Option | Default | What it does |
|---|---|---|
| `quest_mode` | `archipelago_contracts` | `archipelago_contracts`: Tarkov's quests are replaced by a line of contracts from the Archipelago trader, ending with the Kappa contract (your goal). `vanilla`: Tarkov's own quests are checks. See [Archipelago contracts](#archipelago-contracts) |
| `contract_count` | `125` | How many contracts come before the Kappa contract (40–200). The vanilla road to Kappa is 254 quests |
| `goal` | `quest_count` | With `quest_mode: vanilla`, what finishes your game: `quest_count` (complete a number of check quests), `collector` (Fence's *Collector*, the Kappa quest), `all_extracts` (extract once from every map), or `bosses` (kill every boss once). Ignored with contracts: the goal is the Kappa contract |
| `goal_quest_count` | `60` | How many check quests the `quest_count` goal needs (5–372) |
| `starting_map` | `ground_zero` | The map you start with. Every other map is an item. The Lab can't be the starting map |
| `starting_traders` | `prapor_and_therapist` | The traders you start with: Prapor + Therapist, two random ones, or one random one |
| `quest_traders` | all seven | Vanilla quests only: traders whose quests are checks. Quests from the others still work, they just don't send anything |
| `max_quest_level` | `79` | Vanilla quests only: quests that need a higher player level than this aren't checks. Lower = shorter game |
| `hideout_checks` | `true` | Each hideout area level you build is a check |
| `hideout_levels` | `items` | With hideout checks: `items` (building a level sends its check; the level starts working once its station's *Progressive* item arrives, see [The hideout](#the-hideout)) or `built` (building gives the level, as normal) |
| `hideout_supplies` | `10` | How many *Hideout Supplies* items are in the pool (0–40): each sends the found-in-raid materials for one upgrade you can build next |
| `extracts_per_map` | `1` | How many successful PMC extractions on each map are checks (0–5) |
| `kill_milestones` | `short` | Checks for total kills, in PMC and Scav raids on unlocked maps: `off`, `short` (5–50 Scavs, 1–25 PMCs) or `long` (5–300 Scavs, 1–100 PMCs) |
| `boss_checks` | `true` | The first kill of each boss is a check (13: the Goons count as three) |
| `lootsanity` | none | Item categories whose item types become checks: any of `barter`, `keys`, `medication`, `provisions`, `ammo`, `weapons`, `weapon_mods`, `gear`, `info`, `maps`, `special_equipment`, `money`. See [Lootsanity](#lootsanity) |
| `lootsanity_progression` | `false` | Let loot checks hold progression items. Off by default: some items are very rare spawns |
| `craftsanity` | `false` | The first craft of each hideout recipe (about 180) is a check. That first craft gives no item; later crafts are normal |
| `shop_randomizer` | `vanilla` | Randomize the seven main traders' stock: `vanilla`, `same_category` (ammo stays ammo, meds stay meds) or `anything`. Not checks. See [Randomized shops](#randomized-shops) |
| `shop_reshuffle` | `per_seed` | When randomized shops change: `per_seed` (fixed for the whole game) or `every_restock` |
| `shop_stock` | `40` | With randomized shops: the percentage of each trader's offers in stock (10-100) |
| `lock_flea_market` | `true` | The Flea Market is closed until you find *Flea Market Access* |
| `level_pacing` | `normal` | How many maps the logic expects you to own before it asks for higher-level quests: `relaxed`, `normal` or `fast` |
| `trap_percentage` | `10` | Percentage of filler items replaced by traps |
| `starting_kit` | `off` | A random starting kit rolled from the seed (see [The random starting kit](#the-random-starting-kit)): `off`, `extra` (on top of your normal starting stash) or `replace` (instead of it) |
| `starting_kit_quality` | `standard` | How good the kit is: `scrappy`, `standard` or `geared` |
| `kit_grace_raids` | `5` | With `starting_kit: replace`: in this many of your first PMC raids, dying keeps your gear (0–20, 0 = off) |
| `insurance` | `true` | Whether you can insure gear. The Archipelago trader is always an insurer, so the insurance screen works even when every other trader is locked. Off: insuring is refused |
| `insurance_price` | `100` | Insurance price in percent of normal (0–500) |
| `post_raid_healing` | `true` | The Archipelago trader heals you for roubles (the treatment screen after a raid, and its Services tab). Off: trader healing is refused |
| `trader_barters` | `false` | Off: trades paid with items are removed (Archipelago trader, and every trader with vanilla quests); everything is bought with money. On: barters stay |
| `death_link` | `false` | When your PMC dies in raid, everyone with DeathLink dies, and the other way round |

Short games: `contract_count: 40` is a game of a few evenings. With vanilla quests, `goal_quest_count: 20`,
`max_quest_level: 15` and `extracts_per_map: 1` do the same.

---

## 6. Generate and host the game

Whoever runs the multiworld does this once for the whole group.

1. Put everyone's YAML files into Archipelago's **Players** folder (`C:\ProgramData\Archipelago\Players`).
2. Open the **Archipelago Launcher** and click **Generate**.
3. A `.zip` file appears in the **output** folder.
4. Host it:
   - **Online (easiest):** go to [archipelago.gg/uploads](https://archipelago.gg/uploads), upload the `.zip`,
     then click **Create New Room**. Share the room link. The address to connect to is shown on the room page,
     for example `archipelago.gg:38281`.
   - **On your own PC:** in the Launcher, click **Host** and pick the `.zip`. Others connect to your IP and port
     `38281` (you may have to forward that port).

> Tarkov isn't one of Archipelago's built-in games, so seeds must be **generated on a PC with the APWorld
> installed** (step 2). Hosting the result on archipelago.gg works fine.

---

## 7. Connect and play

1. Start `SPT_Runtime\SPT.Server.exe`, then `SPT_Runtime\SPT.Launcher.exe`.
2. In the launcher, make a **new profile** for this multiworld. Pick the **Standard** edition: the others start with
   hideout levels already built (their checks are sent the moment you connect). Create your character and get to
   the main menu.
3. Open the **messenger** (the chat icon at the bottom of the screen) and pick the **Archipelago** contact.
4. Send:

   ```
   connect archipelago.gg:38281 TarkovRunner
   ```

   with your room's address and your slot name. Add the room password at the end if there is one. Slot names with
   spaces go in quotes: `connect archipelago.gg:38281 "Tarkov Runner"`.
5. The contact answers *"Connected to Archipelago as TarkovRunner"*. **Restart the game once** (close it and start it
   again from the launcher) so the map list and the trader screens show what is locked.

That's it. The connection is remembered for this profile: every time the SPT server starts, it reconnects by itself.

> The server mod connects out to the Archipelago server; nothing is opened on your PC. If you play on a different
> PC than the one running the SPT server (Fika), only the server PC needs to reach the Archipelago server.

---

## 8. What happens in-game

**Locked things**
- **Maps:** you only see the maps you own on the map selection screen. Transits to maps you don't own are switched
  off. A newly received map appears after your next raid or a game restart.
- **Traders:** Prapor, Therapist, Skier, Peacekeeper, Mechanic, Ragman and Jaeger are items. With Archipelago
  contracts (the default) every trader is locked and a trader item adds that trader's stock to the Archipelago trader
  (see [Archipelago contracts](#archipelago-contracts)). With vanilla quests, a trader you don't own is
  locked, even if a quest reward would normally unlock them (Jaeger). Fence, Lightkeeper, the BTR driver and Ref work as
  usual (their quests aren't checks).
- **Flea Market:** banned until you receive *Flea Market Access* (and then it opens at the usual level).

**Checks**
- **Contracts** (the default): turning in an Archipelago contract sends its check. See
  [Archipelago contracts](#archipelago-contracts).
- **Quests** (`quest_mode: vanilla`): turning in a quest from one of the main traders sends its check. Event, faction-only, edition-only and
  branching quests (where finishing one fails another) aren't checks, so picking a branch never costs you a check.
- **Hideout:** each area level you build (if `hideout_checks` is on). With `hideout_levels: items` (the default)
  the level itself comes from the multiworld; see [The hideout](#the-hideout).
- **Extractions:** surviving a raid as a PMC on a map you own. Scav raids and maps you don't own don't count. A
  transit to another map isn't an extraction.

**Receiving items**
- The **Archipelago** contact tells you everything you receive. Money and crates (weapons, armor, medicine, ammo,
  keys, stims, barter goods, provisions) arrive in your **System** messages (the "System" chat in the messenger,
  each starting with "Archipelago:"): open the message and press **Receive**. Messages with items are kept for a
  year.
- A new trader comes with a system message; receiving it opens the trader right away (otherwise after a restart).
- **Experience, reputation and traps** are applied by the server straight away and show in game after your next
  inventory action, trade or raid. If you're in a raid, they wait until the raid ends.
- **Traps:** *Fence Grudge* (−0.3 Fence reputation), *Exhaustion* (energy and hydration drop), *Scav Cooldown*
  (+20 minutes on your Scav cooldown, shown after your next Scav raid or a game restart).

**Sending items** — when you complete a check, the Archipelago contact tells you which item you sent and to whom.
With the client plugin, these messages also pop up during raids.

**DeathLink** (if on) — dying in raid as a PMC sends a death to the multiworld. A death from someone else kills you
only while you're in raid, and only with the client plugin; out of raid it's just announced.

**The Collector goal** (vanilla quests) — accept Therapist's *Colleagues - Part 3* **before** turning in Jaeger's *The Huntsman Path -
Sadist*. *Colleagues - Part 3* is only offered while *Sadist* is in progress, and *Collector* needs it. The contact
reminds you when it matters.

### Archipelago contracts

With `quest_mode: archipelago_contracts` (the default), Tarkov's own quests are switched off and the **Archipelago**
trader offers a line of contracts written for your seed. After linking, restart the game once: the trader and the
contracts' objective text appear then.

- **The Archipelago trader is the only trader.** Every other trader (the seven main ones, Fence, Lightkeeper, Ref,
  the BTR driver) stays locked. Instead, the Archipelago trader sells:
  - the stock of every main trader whose item you've received (*Prapor*, *Skier*...), at the loyalty level you'd have
    with that trader. Loyalty comes from your player level and that trader's standing (from contracts); sales don't
    count, since you can't sell to them. The hideout uses the same loyalty levels. With `shop_randomizer`, that stock
    is randomized as usual;
  - its own catalog, drawn from every weapon, armor, helmet, med, round, key and magazine in the game and rolled
    again at every **restock (every 15 minutes)**. Items come in four tiers (weapons, meds and keys by price, armor
    by the class of its plates, ammo by penetration). Tier 1 is open from the start, tier 2 once a quarter of the
    contracts is done, tier 3 at half and tier 4 at three quarters; the newest open tier comes up most often.
    Every restock has at least: 3 weapons (6 in all, fully built), 3 armors and a helmet (with their plates), a med
    for heavy bleeds, one for light bleeds, one for fractures and a painkiller (plus 4 random meds), 5 random keys,
    and a magazine and ammo for every gun on sale or that you own. Catalog items cost their handbook price plus
    25%, with small purchase limits that reset at the restock.

  It buys anything the main traders would buy. There's no insurance, repair or post-raid healing (no Therapist or
  Prapor to do it).
- **What they ask:** kill Scavs, PMCs or both (on a map or anywhere), kill a boss, survive and extract from a map, or
  hand over found-in-raid items. Early contracts use one objective and your starting map; later ones use two or
  three, bigger counts, bosses and pricier items.
- **Order:** three contracts are open at the start. Each later contract follows one (sometimes two) of the few
  before it, so several are usually open at once. The seed orders them so the maps and traders you can receive make
  every one possible when it opens.
- **Rewards:** experience, roubles and standing with a main trader (the trader named in the description). Over the
  whole line every trader gets enough standing for loyalty level 4. Trader offers that Tarkov's quests would unlock
  open up as you get through the line instead: an offer opens at the same share of contracts done as its quest's
  place on the road to Kappa.
- **The Kappa contract:** offered once every other contract is done. Hand over four valuables, found in raid, and
  the Kappa container is yours. Finishing it is the goal.
- **Length:** 125 contracts by default, about half the 254 quests vanilla Kappa takes. `contract_count` sets 40–200.
- Each contract is a check (`Contract 1`, `Contract 2`, ..., `Contract - Kappa`). Hideout recipes that a vanilla
  quest unlocks can't be crafted, so they aren't craftsanity checks.
- The tracker lists the contracts you've taken under the maps their objectives are on.

### Lootsanity

With `lootsanity` set, every item type in the chosen categories is a check. In each raid, the first item of a type
you haven't brought out yet spawns as an **Archipelago Item** instead: on the ground, in containers, or in a bot's rig,
pockets or backpack. Its name says what it stands for, for example *Archipelago Item (Bottle of water (0.6L))*. Only
one spawn per type per raid is swapped; the rest of that type is normal loot.

- **Extract with it** to send the check. The Archipelago Item is then removed: you get whatever the check holds in the
  multiworld, never the original item.
- **If you die**, Archipelago Items in your secure container still count (like anything else there). The rest are lost,
  and their checks stay available.
- Once a type's check is sent, that type spawns normally from then on.
- Bots never lose the ammo, magazines, meds or grenades they use themselves; those types only appear as Archipelago
  Items in loose loot and containers.
- With the optional client plugin, Archipelago Items show the Archipelago symbol. Without it they look like a pack of
  bolts with a violet background.

### The tracker

With the client plugin, press **F8** (or open the **Tasks** screen) for the Archipelago tracker. For each map you have
unlocked it lists what is left there: quests you have taken with objectives on that map, extractions, bosses that
spawn there, and how many loot checks can spawn there. During a raid the current map comes first and lists where this
raid's Archipelago Items are (in which container, on the ground, or carried by which bot). Below that: the next kill
milestones and checks that work on any map. The key and the Tasks-screen behaviour can be changed in the BepInEx
config (F12 with the configuration manager).

### Craftsanity

With `craftsanity: true`, the first time you collect each hideout recipe sends a check. That first craft gives no item
(the ingredients are spent and the multiworld gets the check); every later craft of the same recipe works as normal.
Tools used by the craft are given back as usual. Recipes unlocked by a quest are in logic once that quest is.

### Randomized shops

`shop_randomizer` changes what Prapor, Therapist, Skier, Peacekeeper, Mechanic, Ragman and Jaeger sell. Nothing you
buy is a check.

- `same_category`: each offer becomes another item from the same category (ammo stays ammo, meds stay meds).
  `anything`: any item a trader sells, or a loose item such as a key, a barter item or meds.
- Each offer becomes an item worth about the same as the one it replaces (half to double), so loyalty level 1 stays
  modest and good gear stays at higher levels. Only `shop_stock` percent of the offers are in stock.
- Each offer keeps its loyalty level and any quest that unlocks it. Prices follow the item's value, in the trader's
  currency.
- Purchase limits are random: cheap items allow 20-100 per restock, the most valuable 1-2. Higher loyalty levels raise
  them by 25% per level; loose ammo rounds get ten times more.
- `shop_reshuffle: per_seed` keeps the same stock all game; `every_restock` rolls new stock at each trader restock.
- The Lab keycard and Peacekeeper's and Skier's currency exchange never change. With randomized shops the logic
  expects the Flea Market for the later Gunsmith quests' weapon parts.

### The hideout

With `hideout_levels: items` (the default), each station has a *Progressive* item, one per level: *Progressive
Workbench*, *Progressive Generator* and so on.
- **Building** a level (paying for it and waiting for it to finish) sends that level's check right away.
- The level **starts working** (its crafts, bonuses and stash size) once you have as many of the station's
  Progressive items as the level needs. Until then the station stays at the level below. The Archipelago contact says
  which level is waiting and for how many items.
- When the item arrives, the level you already built is applied for you; the hideout shows it after a game
  restart. You never pay for a level twice: building it again is refused.
- A Progressive item that arrives before you build the level just lets the level work as soon as you build it.

*Hideout Supplies* (`hideout_supplies`) send the materials for one upgrade you can build next, found in raid, in your
System messages: the cheapest one whose other stations you already have.

With `hideout_levels: built`, building a level gives you the level and sends its check, as in the normal game.

### Insurance and healing

The Archipelago trader insures gear (on Therapist's terms: returns in 10–20 hours, 95% of the time) and heals you
after a raid and from its Services tab, in both quest modes. With vanilla quests it sells nothing; Prapor and
Therapist still insure and heal once you have them. `insurance` and `post_raid_healing` switch these off, and
`insurance_price` scales every insurance price.

### The random starting kit

With `starting_kit` on, you get a random kit when you first link your profile. It's rolled from the seed, so
everyone's is different:

| | scrappy | standard | geared |
|---|---|---|---|
| Weapon | a cheap gun | a mid-range gun | a good gun |
| Spare magazines (if the gun takes them) | 1 | 2 | 3 |
| Matching ammo | 90 rounds, weaker | 150 rounds, mid | 240 rounds, better |
| Body armor | class 2 | class 3 | class 4 |
| Helmet | — | half the time | yes |
| Rig and backpack | cheap | mid-range | better |
| Meds / food | 2 / 1 | 3 / 2 | 5 / 2 |
| Roubles | 30k–80k | 100k–250k | 300k–600k |

- **`extra`:** the kit arrives in your System messages. Your normal starting stash stays.
- **`replace`:** when you restart the game after linking, your starting stash and gear are removed (the knife,
  pockets and secure container stay) and the kit arrives in your System messages. Open it and press **Receive**, then
  gear up.
  This only happens on a brand-new profile: if the profile was already played, or you start a raid before
  restarting, your stash is kept and the kit comes on top instead.
- **Grace raids (`kit_grace_raids`, 5 by default):** with `replace`, dying in one of your first PMC raids keeps your
  gear (everything you carry, without found-in-raid status). The Archipelago contact counts them down.

---

## 9. Commands

Send these to the **Archipelago** contact in the messenger:

| Command | What it does |
|---|---|
| `connect <server:port> <slot> [password]` | Link this profile to a slot |
| `status` | Connection, unlocked maps / traders / Flea Market, checks done, goal progress |
| `items` | Everything you've received |
| `hint <item>` | Asks where an item is, e.g. `hint Customs Access` |
| `!<command>` | Any Archipelago server command, e.g. `!hint Customs Access`, `!remaining` |
| `disconnect` / `reconnect` | Stop or restart connecting (progress is kept) |
| `reset` | Forget this profile's Archipelago game and unlock everything again (asks you to send `reset confirm`) |
| `help` | The list of commands |

Anything else you type is sent to the multiworld chat.

---

## 10. Updating and uninstalling

**Updating:** close everything, extract the new release zip over the old one, and install the new `tarkov.apworld`.
Going from 1.x (SPT 4.0.13) to 2.0 (SPT 4.1.6) needs a new multiworld, because the quest list changed with the game
update.
The mod and the APWorld that generated your game must come from the **same release**; the mod refuses to connect
to a game made with a different version (it says so in the messenger). Your progress is kept.

**Uninstalling:** first send `reset` and then `reset confirm` to the Archipelago contact on every linked profile.
That unlocks the traders and lifts the Flea Market ban. Then delete `SPT_Runtime\user\mods\ArchipelagoTarkov` and
`BepInEx\plugins\ArchipelagoTarkov`. (If you delete the mod without resetting, those profiles keep their locked
traders and Flea Market ban.)

Your Archipelago progress lives in `SPT_Runtime\user\mods\ArchipelagoTarkov\profiles\`. Back it up together with
`SPT_Runtime\user\profiles\` if you back up your saves.

---

## 11. Troubleshooting

**The SPT console doesn't show "Archipelago for Tarkov loaded"**
Check that `ArchipelagoTarkov.dll` is at `SPT_Runtime\user\mods\ArchipelagoTarkov\ArchipelagoTarkov.dll`. The console
lists every mod it loads (`Mod: Archipelago for Tarkov version: 2.1.0 ... loaded`); on SPT older than 4.1.6, or 4.2
and newer, it shows an error about the mod's SPT version instead (for SPT 4.0.13, use release 1.1.1). Look for red
lines mentioning Archipelago.

**There's no Archipelago contact in the messenger**
Same as above: the server mod didn't load. Restart the SPT server and the game.

**"refused the connection: InvalidSlot"**
The slot name is case sensitive. Use the exact name from your YAML (also shown on the room page).

**"refused the connection: InvalidPassword"**
Add the room password at the end of the `connect` command.

**"Could not reach Archipelago at …"**
Check the address and port (the port changes when an archipelago.gg room restarts after being idle; look at the room
page). The mod keeps retrying; send `disconnect` to stop it.

**"generated with a different version of the Escape from Tarkov APWorld"**
The mod and the `.apworld` used to generate the game come from different releases. Use the same release for both.

**"This profile is linked to the slot …" / "already belongs to a different Archipelago game"**
A profile stays linked to its first slot and game. Make a new SPT profile, or send `reset`, `reset confirm` to wipe
this profile's Archipelago progress first.

**A map I received doesn't show up**
The game keeps the map list it loaded. Do one raid on a map you have, or restart the game.

**Items in the Archipelago chat can't be received (version 1.1.0 and older)**
The game only lets you collect items from System messages. Update the mod to 1.1.1 or newer (no new multiworld
needed): the first time the SPT server starts with it, every item still attached to an Archipelago chat message is
sent again to your System messages, and the contact tells you how many.

**A trader I received is still locked**
Open the system message that came with the trader and receive it, or restart the game.

**My experience or reputation looks wrong**
Server-side changes show after your next inventory action (moving an item is enough), a trade, or a raid. If they're
still wrong after a game restart, please report it.

**An extraction didn't count**
Extractions count only as a PMC, on maps you own, when the raid ends as *Survived* or *Run through*. Transits don't
count.

---

## Reporting problems

Open an [Issue](https://github.com/Pegzzzz/Archipelago-Tarkov/issues) on this repository and include:

- the newest log file from `SPT_Runtime\user\logs\spt\` (the lines with `Archipelago` matter most)
- what you were doing (which quest, map or item), and what you expected
- your mod version and the other mods you have installed

---

## Credits

- Archipelago integration: **Pegz**
- SPT by the SPT and SP Tushonka teams
- [Archipelago](https://archipelago.gg) and its community
- Escape from Tarkov is made by Battlestate Games. This project isn't affiliated with Battlestate Games, SPT or
  Archipelago.

#!/usr/bin/env python3
"""
Generate the shared Archipelago data table for Escape from Tarkov (SPT 4.1.6).

Reads the SPT server database (the SPT_Data folder shipped with an SPT release) and
writes one JSON file that both the APWorld and the SPT server mod load, so item and
location IDs can never drift apart between the two halves.

Usage:
    python tools/extract_data.py <path to SPT/SPT_Data> <output json>

Nothing in here is hand-maintained game data except the small tables at the top
(map keys, the seven gated traders, keyword hints used to place objectives on maps).
"""
from __future__ import annotations

import json
import os
import re
import sys
from collections import OrderedDict

# --------------------------------------------------------------------------------------
# Static tables
# --------------------------------------------------------------------------------------

ITEM_BASE = 76_310_000
LOCATION_BASE = 76_400_000
QUEST_OFFSET = 0
HIDEOUT_OFFSET = 10_000
EXTRACT_OFFSET = 20_000
KILL_OFFSET = 30_000
BOSS_OFFSET = 40_000

# Cumulative kill totals that are checks. "level" is the player level the logic expects before the total is
# reasonable (the APWorld turns it into a number of unlocked maps, like quest levels). Never reorder or remove
# entries: the position in each list is part of the location id. Add new tiers at the end.
KILL_MILESTONES = OrderedDict([
    ("scav", ("Scavs", [5, 10, 25, 50, 75, 100, 150, 200, 250, 300])),
    ("pmc", ("PMCs", [1, 5, 10, 25, 50, 75, 100])),
])
KILL_MILESTONE_LEVELS = {"scav": 5, "pmc": 2}  # level = total // divisor, capped at 60
KILL_SLOTS_PER_TARGET = 100  # id = KILL_OFFSET + target index * 100 + tier index

# Boss role (the bot type in SPT's database) -> (key, display name). The maps each boss can spawn on are read
# from the locations' BossLocationSpawn tables. Never reorder: the position is part of the location id.
BOSSES = OrderedDict([
    ("bossBully", ("reshala", "Reshala")),
    ("bossKilla", ("killa", "Killa")),
    ("bossKojaniy", ("shturman", "Shturman")),
    ("bossGluhar", ("glukhar", "Glukhar")),
    ("bossSanitar", ("sanitar", "Sanitar")),
    ("bossTagilla", ("tagilla", "Tagilla")),
    ("bossBoar", ("kaban", "Kaban")),
    ("bossKolontay", ("kollontay", "Kollontay")),
    ("bossZryachiy", ("zryachiy", "Zryachiy")),
    ("bossPartisan", ("partisan", "Partisan")),
    ("bossKnight", ("knight", "Knight")),
    ("followerBigPipe", ("bigpipe", "Big Pipe")),
    ("followerBirdEye", ("birdeye", "Birdeye")),
])
BOSS_LEVEL = 10

# Lootsanity: each lootable item type is a check. Ids come from LOOT_OFFSET upwards; an existing table's ids are
# kept on regeneration (new types are appended), so ids never move when SPT adds or removes items.
LOOT_OFFSET = 100_000
LOOT_MAX = 100_000
# Handbook top-level category id -> lootsanity category key (the APWorld option uses these keys).
LOOT_CATEGORIES = OrderedDict([
    ("5b47574386f77428ca22b33e", "barter"),
    ("5b47574386f77428ca22b342", "keys"),
    ("5b47574386f77428ca22b344", "medication"),
    ("5b47574386f77428ca22b340", "provisions"),
    ("5b47574386f77428ca22b346", "ammo"),
    ("5b5f78dc86f77409407a7f8e", "weapons"),
    ("5b5f71a686f77447ed5636ab", "weapon_mods"),
    ("5b47574386f77428ca22b33f", "gear"),
    ("5b47574386f77428ca22b341", "info"),
    ("5b47574386f77428ca22b343", "maps"),
    ("5b47574386f77428ca22b345", "special_equipment"),
    ("5b5f78b786f77447ed5636af", "money"),
])
# Bots carrying these spawn with them on every map but Labs.
GENERIC_BOT_TYPES = {"assault", "bear", "usec", "pmcbear", "pmcusec"}
BOT_LOOT_SLOTS = ("TacticalVest", "Pockets", "Backpack")
# Items bots use themselves (reloading, healing, throwing): never swapped on a bot, only in loose loot and containers.
BOT_USED_BASES = {
    "5485a8684bdc2da71d8b4567",  # Ammo
    "543be5cb4bdc2deb348b4568",  # AmmoBox
    "5448bc234bdc2d3c308b4569",  # Magazine
    "543be5664bdc2dd4348b4569",  # Meds (includes stimulants)
    "543be6564bdc2df4348b4568",  # ThrowWeap
}
# Stand-in template ids start with this, so the client plugin can recognise them without a list.
STAND_IN_PREFIX = "a9a9a9"

# Craftsanity: each hideout recipe is a check. Ids from CRAFT_OFFSET, kept on regeneration like lootsanity's.
CRAFT_OFFSET = 200_000
# Archipelago contracts (quest_mode: archipelago_contracts): the seed writes the contracts, the names are fixed.
CONTRACT_OFFSET = 300_000
CONTRACT_MAX = 200
CRAFT_MAX = 10_000

# Map key -> (display name, location ids used in quest data / raid server ids, text hints)
MAPS = OrderedDict([
    ("factory", ("Factory", ["factory4_day", "factory4_night"],
                 [r"\bFactory\b"])),
    ("customs", ("Customs", ["bigmap"],
                 [r"\bCustoms\b", r"\bdorms?\b", r"\bDorms\b"])),
    ("woods", ("Woods", ["Woods"],
               [r"\bWoods\b", r"\bsawmill\b"])),
    ("interchange", ("Interchange", ["Interchange"],
                     [r"\bInterchange\b", r"\bULTRA\b", r"\bKiba\b", r"\bGoshan\b", r"\bOLI\b", r"\bIDEA\b"])),
    ("shoreline", ("Shoreline", ["Shoreline"],
                   [r"\bShoreline\b", r"\bHealth Resort\b", r"\bresort\b"])),
    ("reserve", ("Reserve", ["RezervBase"],
                 [r"\bReserve\b"])),
    ("lighthouse", ("Lighthouse", ["Lighthouse"],
                    [r"\bLighthouse\b", r"\bWater Treatment Plant\b"])),
    ("streets", ("Streets of Tarkov", ["TarkovStreets"],
                 [r"\bStreets of Tarkov\b", r"\bConcordia\b", r"\bPrimorsky\b", r"\bLexOs\b", r"\bPinewood\b",
                  r"\bChekannaya\b", r"\bKlimov\b", r"\bCardinal\b", r"\bZmeevsky\b", r"\bSparja\b"])),
    ("groundzero", ("Ground Zero", ["Sandbox", "Sandbox_high"],
                    [r"\bGround Zero\b"])),
    ("labs", ("The Lab", ["laboratory"],
              [r"\bThe Lab\b", r"\bthe Lab\b", r"\bLabs\b"])),
])

# location base _Id values used by the quest "location" field
LOCATION_BASE_IDS = {
    "55f2d3fd4bdc2d5f408b4567": "factory",
    "59fc81d786f774390775787e": "factory",
    "56f40101d2720b2a4d8b45d6": "customs",
    "5704e3c2d2720bac5b8b4567": "woods",
    "5714dbc024597771384a510d": "interchange",
    "5704e554d2720bac5b8b456e": "shoreline",
    "5704e5fad2720bc05b8b4567": "reserve",
    "5704e4dad2720bb55b8b4567": "lighthouse",
    "5714dc692459777137212e12": "streets",
    "653e6760052c01c1c805532f": "groundzero",
    "65b8d6f5cdde2479cb2a3125": "groundzero",
    "5b0fc42d86f7744a585f9105": "labs",
}
UNPLAYABLE_LOCATION_BASE_IDS = {"6733700029c367a3d40b02af", "56db0b3bd2720bb0678b4567", "marathon"}

# The traders that become Archipelago items. Fence, Lightkeeper, BTR Driver and Ref
# keep their vanilla behaviour and their quests are not checks.
GATED_TRADERS = OrderedDict([
    ("prapor", "54cb50c76803fa8b248b4571"),
    ("therapist", "54cb57776803fa99248b456e"),
    ("skier", "58330581ace78e27b8b10cee"),
    ("peacekeeper", "5935c25fb3acc3127c3d8cd9"),
    ("mechanic", "5a7c2eca46aef81a7ca2145d"),
    ("ragman", "5ac3b934156ae10c4430e83c"),
    ("jaeger", "5c0647fdd443bc2504c2d371"),
])
TRADER_DISPLAY = {
    "prapor": "Prapor", "therapist": "Therapist", "skier": "Skier", "peacekeeper": "Peacekeeper",
    "mechanic": "Mechanic", "ragman": "Ragman", "jaeger": "Jaeger",
}
FENCE_ID = "579dc571d53a0658a154fbec"
COLLECTOR_QUEST_ID = "5c51aac186f77432ea65c552"

# Hideout area types that are not useful checks: 25 = equipment preset stand (auto upgrades
# with the weapon rack), 22 = defective wall (auto-upgrading story wall).
EXCLUDED_HIDEOUT_AREAS = {22, 25}

# Objective condition types that only make sense at a specific spot on a specific map.
ZONAL_FINISH_TYPES = {"LeaveItemAtLocation", "PlaceBeacon", "VisitPlace"}
ZONAL_COUNTER_TYPES = {"VisitPlace", "InZone", "ExitName", "LaunchFlare"}


def load(path):
    with open(path, encoding="utf-8") as f:
        return json.load(f)


def as_list(value):
    if value is None:
        return []
    if isinstance(value, list):
        return value
    return [value]


def extract_loot(db, items, locale, location_name_to_key, out_path):
    """Lootsanity table: every item type that can be looted in a raid, its category, and where it spawns."""
    import hashlib

    handbook = load(os.path.join(db, "templates", "handbook.json"))
    hb_cats = {c["Id"]: c for c in handbook["Categories"]}

    def top_category(cat_id):
        while hb_cats.get(cat_id, {}).get("ParentId"):
            cat_id = hb_cats[cat_id]["ParentId"]
        return cat_id

    category_of = {}
    price_of = {entry["Id"]: int(entry.get("Price") or 0) for entry in handbook["Items"]}
    for entry in handbook["Items"]:
        key = LOOT_CATEGORIES.get(top_category(entry["ParentId"]))
        if key:
            category_of[entry["Id"]] = key

    def is_a(tpl, bases):
        for _ in range(40):
            if tpl in bases:
                return True
            tpl = items.get(tpl, {}).get("_parent")
            if not tpl:
                return False
        return False

    # Where each type can be found: loose loot and static containers per map; generic bots anywhere.
    spawn_maps = {}
    for folder in sorted(os.listdir(os.path.join(db, "locations"))):
        map_key = location_name_to_key.get(folder.lower())
        folder_path = os.path.join(db, "locations", folder)
        if map_key is None or not os.path.isdir(folder_path):
            continue
        loose_path = os.path.join(folder_path, "looseLoot.json")
        if os.path.exists(loose_path):
            for sp in load(loose_path).get("spawnpoints", []):
                for it in (sp.get("template") or {}).get("Items", []):
                    spawn_maps.setdefault(it["_tpl"], set()).add(map_key)
        static_path = os.path.join(folder_path, "staticLoot.json")
        if os.path.exists(static_path):
            for container in load(static_path).values():
                for dist in container.get("itemDistribution", []):
                    if (dist.get("relativeProbability") or 0) > 0:
                        spawn_maps.setdefault(dist["tpl"], set()).add(map_key)
    on_bots = set()
    for bot_type in GENERIC_BOT_TYPES:
        path = os.path.join(db, "bots", "types", f"{bot_type}.json")
        if os.path.exists(path):
            slots = (load(path).get("inventory") or {}).get("items") or {}
            for slot in BOT_LOOT_SLOTS:
                on_bots.update((slots.get(slot) or {}).keys())

    # Keep the ids (and names) of an existing table so they never move.
    previous = {}
    if os.path.exists(out_path):
        try:
            previous = {r["tpl"]: r for r in load(out_path).get("loot", [])}
        except (OSError, ValueError):
            previous = {}
    used_ids = {r["location_id"] for r in previous.values()}
    used_names = {r["name"] for r in previous.values()}
    next_id = max(used_ids, default=LOCATION_BASE + LOOT_OFFSET - 1) + 1

    records = []
    for tpl in sorted(set(spawn_maps) | on_bots):
        item = items.get(tpl)
        category = category_of.get(tpl)
        if item is None or category is None or item.get("_type") != "Item":
            continue
        if (item.get("_props") or {}).get("QuestItem"):
            continue
        bot_safe = not is_a(tpl, BOT_USED_BASES)
        # Prefer real spawn places: bot loot tables can roll almost anything at tiny odds. Only types found on
        # nothing but bots count as "any map".
        maps = [m for m in MAPS if m in spawn_maps.get(tpl, ())]
        if not maps and not (tpl in on_bots and bot_safe):
            continue  # only on bots, and bots never give it up (they use it)
        old = previous.get(tpl)
        if old:
            name, location_id = old["name"], old["location_id"]
        else:
            display = locale.get(f"{tpl} Name") or item.get("_name") or tpl
            name = f"Loot - {display}"
            if name in used_names:
                name = f"Loot - {display} ({locale.get(f'{tpl} ShortName') or tpl[-6:]})"
            n = 2
            base_name = name
            while name in used_names:
                name = f"{base_name} {n}"
                n += 1
            location_id = next_id
            next_id += 1
        used_names.add(name)
        if location_id >= LOCATION_BASE + LOOT_OFFSET + LOOT_MAX:
            raise SystemExit("lootsanity location id range is full")
        records.append({
            "tpl": tpl,
            "name": name,
            "category": category,
            "maps": maps,  # empty: found on bots everywhere
            "bot_safe": bot_safe,
            "price": price_of.get(tpl, 0),
            "stand_in": STAND_IN_PREFIX + hashlib.sha1(f"archipelago-loot:{tpl}".encode()).hexdigest()[:18],
            "location_id": location_id,
        })
    records.sort(key=lambda r: r["location_id"])
    return records


def main(spt_data_dir: str, out_path: str) -> None:
    db = os.path.join(spt_data_dir, "database")
    quests = load(os.path.join(db, "templates", "quests.json"))
    items = load(os.path.join(db, "templates", "items.json"))
    locale = load(os.path.join(db, "locales", "global", "en.json"))
    areas = load(os.path.join(db, "hideout", "areas.json"))
    quest_cfg = load(os.path.join(spt_data_dir, "configs", "quest.json"))
    profiles = load(os.path.join(db, "templates", "profiles.json"))

    trader_bases = {}
    for tid in os.listdir(os.path.join(db, "traders")):
        base_path = os.path.join(db, "traders", tid, "base.json")
        if os.path.exists(base_path):
            trader_bases[tid] = load(base_path)
    gated_by_id = {tid: key for key, tid in GATED_TRADERS.items()}

    location_name_to_key = {}
    for key, (_, loc_ids, _) in MAPS.items():
        for loc_id in loc_ids:
            location_name_to_key[loc_id.lower()] = key
    map_text_patterns = {key: [re.compile(p) for p in hints] for key, (_, _, hints) in MAPS.items()}

    def text_maps(text: str) -> list[str]:
        found = []
        if not text:
            return found
        for key, patterns in map_text_patterns.items():
            if any(p.search(text) for p in patterns):
                found.append(key)
        return found

    def quest_name(qid: str) -> str:
        return locale.get(f"{qid} name") or quests[qid].get("QuestName") or qid

    def ll_min_level(trader_id: str, ll: int) -> int:
        base = trader_bases.get(trader_id)
        if not base:
            return 0
        levels = base.get("loyaltyLevels") or []
        if 1 <= ll <= len(levels):
            return int(levels[ll - 1].get("minLevel") or 0)
        return 0

    # ----------------------------------------------------------------------------------
    # Quest classification
    # ----------------------------------------------------------------------------------
    event_quests = set(quest_cfg.get("eventQuests", {}).keys())
    faction_quests = set(quest_cfg.get("usecOnlyQuests", [])) | set(quest_cfg.get("bearOnlyQuests", []))
    edition_quests = set(quest_cfg.get("profileWhitelist", {}).keys())
    for ids in quest_cfg.get("profileBlacklist", {}).values():
        edition_quests |= set(ids)

    # Three properties per quest, all "guaranteed regardless of player choices":
    #   start_ok    - the quest can always be accepted
    #   finish_ok   - the quest can always be brought to an end state (success OR fail)
    #   success_ok  - the quest can always be completed successfully
    # A quest is only a check when success_ok holds. Branching quests (completing a
    # sibling fails them) and quests that can fail for good are startable/finishable but
    # never guaranteed to succeed, so prerequisites that demand their success are not met.
    hard_block: dict[str, str] = {}   # quest cannot even be started
    no_success: dict[str, str] = {}   # startable, but success is not guaranteed
    for qid, q in quests.items():
        reason = None
        if qid in event_quests:
            reason = "seasonal/event quest"
        elif qid in faction_quests:
            reason = "faction-specific quest"
        elif qid in edition_quests:
            reason = "game-edition-specific quest"
        elif q.get("progressSource") == "arena" or q.get("gameModes"):
            reason = "Arena quest"
        elif q["traderId"] not in trader_bases:
            reason = "unknown trader"
        elif (q["traderId"] not in gated_by_id and q["traderId"] != FENCE_ID
              and not trader_bases[q["traderId"]].get("unlockedByDefault")):
            reason = f"trader {trader_bases[q['traderId']].get('nickname')} is locked on a new profile"
        elif any(c.get("conditionType") == "TraderStanding" and c.get("compareMethod") in ("<", "<=")
                 for c in q["conditions"].get("AvailableForStart", [])):
            reason = "only offered after lowering a trader's standing"
        elif any(c.get("conditionType") == "GlobalVariableValue"
                 for c in q["conditions"].get("AvailableForFinish", [])):
            reason = "depends on a game-wide variable SPT does not drive"
        if reason:
            hard_block[qid] = reason
            continue
        # Objectives that can only be done on maps SPT does not have
        unplayable = q.get("location") in UNPLAYABLE_LOCATION_BASE_IDS
        for c in q["conditions"].get("AvailableForFinish", []):
            if c.get("conditionType") == "CounterCreator":
                for cc in c["counter"]["conditions"]:
                    if cc.get("conditionType") == "Location":
                        targets = as_list(cc.get("target"))
                        if not any(t.lower() in location_name_to_key for t in targets):
                            unplayable = True
                    if cc.get("conditionType", "").startswith("Arena"):
                        unplayable = True
        if unplayable:
            hard_block[qid] = "objective on a map or mode SPT cannot play"
        elif any(c.get("conditionType") == "Quest" for c in q["conditions"].get("Fail", [])):
            no_success[qid] = "branching quest (completing another quest fails it)"
        elif q["conditions"].get("Fail") and not q.get("restartable"):
            no_success[qid] = "quest can fail permanently"

    start_ok = {qid: qid not in hard_block for qid in quests}
    finish_ok = dict(start_ok)
    success_ok = {qid: start_ok[qid] and qid not in no_success for qid in quests}
    reasons = {qid: hard_block.get(qid) or no_success.get(qid) for qid in quests}

    def finish_targets(qid: str) -> set:
        """Quests whose success the completion of qid depends on."""
        out = set()
        for c in quests[qid]["conditions"].get("AvailableForFinish", []):
            if c.get("conditionType") == "Quest":
                out.update(as_list(c.get("target")))
        return out

    def prereq_satisfied(statuses: set, target: str, dependent: str) -> tuple[bool, str]:
        if target not in quests:
            return False, "missing"
        if statuses and statuses <= {5}:
            return False, "fail"            # needs another quest to fail: never guaranteed
        if statuses and statuses <= {2}:
            # "while the other quest is in progress": the window closes when that quest is finished,
            # unless that quest cannot be finished before this one is (they depend on each other).
            if dependent not in finish_targets(target):
                return False, "window"
            return start_ok[target], "start"
        if 2 in statuses:
            return start_ok[target], "start"
        if 5 in statuses:
            return finish_ok[target], "finish"
        return success_ok[target], "success"

    def explain(why: str, target: str) -> str:
        if why == "fail":
            return f"only unlocked by failing '{quest_name(target)}'"
        if why == "missing":
            return "requires a quest missing from the database"
        if why == "window":
            return f"only offered while '{quest_name(target)}' is in progress"
        return f"requires '{quest_name(target)}' ({reasons.get(target)})"

    changed = True
    while changed:
        changed = False
        for qid, q in quests.items():
            if start_ok[qid]:
                for c in q["conditions"].get("AvailableForStart", []):
                    if c.get("conditionType") != "Quest":
                        continue
                    statuses = set(as_list(c.get("status")))
                    for target in as_list(c.get("target")):
                        ok, why = prereq_satisfied(statuses, target, qid)
                        if not ok:
                            start_ok[qid] = finish_ok[qid] = success_ok[qid] = False
                            reasons[qid] = explain(why, target)
                            changed = True
                            break
                    if not start_ok[qid]:
                        break
            if success_ok[qid]:
                # completion conditions that need another quest done
                for c in q["conditions"].get("AvailableForFinish", []):
                    if c.get("conditionType") != "Quest":
                        continue
                    statuses = set(as_list(c.get("status"))) or {4}
                    for target in as_list(c.get("target")):
                        if target == qid:
                            continue
                        ok, why = prereq_satisfied(statuses, target, qid)
                        if not ok:
                            finish_ok[qid] = success_ok[qid] = False
                            reasons[qid] = "to complete: " + explain(why, target)
                            changed = True
                            break
                    if not success_ok[qid]:
                        break

    impossible = {qid: reasons[qid] for qid in quests if not success_ok[qid]}

    # ----------------------------------------------------------------------------------
    # Quest records
    # ----------------------------------------------------------------------------------
    quest_records = []
    unknown_objectives = []
    for qid in sorted(quests.keys()):
        q = quests[qid]
        trader_key = gated_by_id.get(q["traderId"])
        min_level = 0
        trader_ll = []
        prereqs = []
        for c in q["conditions"].get("AvailableForStart", []):
            ctype = c.get("conditionType")
            if ctype == "Level" and c.get("compareMethod", ">=") in (">=", ">"):
                min_level = max(min_level, int(c.get("value") or 0))
            elif ctype == "TraderLoyalty":
                tid = c.get("target")
                tid = tid[0] if isinstance(tid, list) else tid
                ll = int(c.get("value") or 1)
                trader_ll.append({"trader": gated_by_id.get(tid), "trader_id": tid, "ll": ll,
                                  "min_level": ll_min_level(tid, ll)})
                min_level = max(min_level, ll_min_level(tid, ll))
            elif ctype == "TraderStanding":
                tid = c.get("target")
                tid = tid[0] if isinstance(tid, list) else tid
                trader_ll.append({"trader": gated_by_id.get(tid), "trader_id": tid, "ll": 1, "min_level": 0})
            elif ctype == "Quest":
                statuses = set(as_list(c.get("status")))
                mode = "start" if 2 in statuses else ("finish" if 5 in statuses else "success")
                for target in as_list(c.get("target")):
                    prereqs.append({"id": target, "mode": mode})

        # Completion conditions that need another quest, or loyalty with a trader
        finish_prereqs = []
        finish_trader_ll = []
        for c in q["conditions"].get("AvailableForFinish", []):
            ctype = c.get("conditionType")
            if ctype == "Quest":
                statuses = set(as_list(c.get("status"))) or {4}
                mode = "start" if 2 in statuses else ("finish" if 5 in statuses else "success")
                for target in as_list(c.get("target")):
                    if target != qid:
                        finish_prereqs.append({"id": target, "mode": mode})
            elif ctype in ("TraderLoyalty", "TraderStanding"):
                tid = c.get("target")
                tid = tid[0] if isinstance(tid, list) else tid
                ll = int(c.get("value") or 1) if ctype == "TraderLoyalty" else 1
                finish_trader_ll.append({"trader": gated_by_id.get(tid), "trader_id": tid, "ll": ll,
                                         "min_level": ll_min_level(tid, ll) if ctype == "TraderLoyalty" else 0})

        # Map requirement per objective. Each entry is a list of acceptable maps (any of);
        # all entries must be satisfied. "*" means the map could not be determined, which
        # the APWorld treats conservatively (every map required).
        objective_maps = []
        quest_loc_key = LOCATION_BASE_IDS.get(q.get("location", "any"))
        for c in q["conditions"].get("AvailableForFinish", []):
            ctype = c.get("conditionType")
            maps_for_obj: list[str] = []
            zonal = ctype in ZONAL_FINISH_TYPES
            if ctype == "CounterCreator":
                for cc in c["counter"]["conditions"]:
                    if cc.get("conditionType") == "Location":
                        for t in as_list(cc.get("target")):
                            key = location_name_to_key.get(t.lower())
                            if key and key not in maps_for_obj:
                                maps_for_obj.append(key)
                    if cc.get("conditionType") in ZONAL_COUNTER_TYPES:
                        zonal = True
            elif ctype == "FindItem":
                for tpl in as_list(c.get("target")):
                    if items.get(tpl, {}).get("_props", {}).get("QuestItem"):
                        zonal = True
            if not maps_for_obj:
                if quest_loc_key:
                    maps_for_obj = [quest_loc_key]
                else:
                    maps_for_obj = text_maps(locale.get(c.get("id", ""), ""))
            if not maps_for_obj and zonal:
                # fall back to the quest description, then to "unknown"
                desc_maps = text_maps(locale.get(f"{qid} description", ""))
                if len(desc_maps) == 1:
                    maps_for_obj = desc_maps
                else:
                    maps_for_obj = ["*"]
                    if qid not in impossible:
                        unknown_objectives.append((quest_name(qid), locale.get(c.get("id", ""), "")))
            if maps_for_obj:
                if maps_for_obj not in objective_maps:
                    objective_maps.append(maps_for_obj)

        # An objective with no map of its own (e.g. "stash the knife at the ritual spot")
        # almost always happens on the same map as its siblings. When every other
        # objective of the quest points at one single map, use that map.
        if ["*"] in objective_maps:
            known = {m for obj in objective_maps if obj != ["*"] for m in obj}
            if len(known) == 1:
                objective_maps = [obj for obj in objective_maps if obj != ["*"]]
                if [next(iter(known))] not in objective_maps:
                    objective_maps.append([next(iter(known))])
                unknown_objectives = [u for u in unknown_objectives if u[0] != quest_name(qid)]

        weapon_assembly = any(c.get("conditionType") == "WeaponAssembly"
                              for c in q["conditions"].get("AvailableForFinish", []))
        record = {
            "id": qid,
            "name": quest_name(qid),
            "weapon_assembly": weapon_assembly,
            "trader": trader_key,
            "trader_id": q["traderId"],
            "min_level": min_level,
            "trader_ll": trader_ll,
            "prereqs": prereqs,
            "finish_prereqs": finish_prereqs,
            "finish_trader_ll": finish_trader_ll,
            "objective_maps": objective_maps,
            "possible": success_ok[qid],
            "start_ok": start_ok[qid],
            "finish_ok": finish_ok[qid],
            "reason": reasons.get(qid),
            "check": False,
            "location_id": None,
            "location_name": None,
        }
        quest_records.append(record)

    # Assign location ids/names to quest checks (gated traders only)
    used_names = set()
    index = 0
    for record in quest_records:
        if not record["possible"] or record["trader"] is None:
            continue
        name = f"{TRADER_DISPLAY[record['trader']]} - {record['name']}"
        if name in used_names:
            n = 2
            while f"{name} ({n})" in used_names:
                n += 1
            name = f"{name} ({n})"
        used_names.add(name)
        record["check"] = True
        record["location_name"] = name
        record["location_id"] = LOCATION_BASE + QUEST_OFFSET + index
        index += 1

    # ----------------------------------------------------------------------------------
    # Hideout
    # ----------------------------------------------------------------------------------
    standard_levels = {}
    standard = profiles["Standard"]["usec"]["character"]
    for area in standard["Hideout"]["Areas"]:
        standard_levels[area["type"]] = area["level"]

    hideout_records = []
    h_index = 0
    for area in sorted(areas, key=lambda a: a["type"]):
        atype = area["type"]
        if atype in EXCLUDED_HIDEOUT_AREAS or not area.get("enabled", True):
            continue
        area_name = locale.get(f"hideout_area_{atype}_name") or f"Area {atype}"
        for level_key in sorted((k for k in area["stages"] if k != "0"), key=int):
            level = int(level_key)
            if level <= standard_levels.get(atype, 0):
                continue  # already built on a Standard-edition profile
            stage = area["stages"][level_key]
            prereq_areas = []
            trader_ll = []
            skills = []
            item_count = 0
            for r in stage.get("requirements", []):
                if r["type"] == "Area":
                    if r["areaType"] == atype and r["requiredLevel"] < level:
                        continue
                    if r["requiredLevel"] > 0:
                        prereq_areas.append([r["areaType"], r["requiredLevel"]])
                elif r["type"] == "TraderLoyalty":
                    tid = r["traderId"]
                    trader_ll.append({"trader": gated_by_id.get(tid), "trader_id": tid, "ll": r["loyaltyLevel"],
                                      "min_level": ll_min_level(tid, r["loyaltyLevel"])})
                elif r["type"] == "Skill":
                    skills.append([r["skillName"], r["skillLevel"]])
                elif r["type"] == "Item":
                    item_count += 1
            if level > 1:
                prereq_areas.append([atype, level - 1])
            hideout_records.append({
                "area": atype,
                "level": level,
                "name": f"Hideout - {area_name} Level {level}",
                "prereq_areas": prereq_areas,
                "trader_ll": trader_ll,
                "skills": skills,
                "item_requirements": item_count,
                "location_id": LOCATION_BASE + HIDEOUT_OFFSET + h_index,
            })
            h_index += 1
    # Drop hideout levels whose prerequisite areas are not themselves present or prebuilt
    present = {(h["area"], h["level"]) for h in hideout_records}

    def area_ok(t, lv):
        return lv <= standard_levels.get(t, 0) or (t, lv) in present

    while True:
        kept = [h for h in hideout_records if all(area_ok(t, lv) for t, lv in h["prereq_areas"])]
        if len(kept) == len(hideout_records):
            break
        hideout_records = kept
        present = {(h["area"], h["level"]) for h in hideout_records}

    # ----------------------------------------------------------------------------------
    # Extractions (up to 5 per map; the APWorld option picks how many are used)
    # ----------------------------------------------------------------------------------
    extract_records = []
    e_index = 0
    for key, (display, loc_ids, _) in MAPS.items():
        for n in range(1, 6):
            extract_records.append({
                "map": key,
                "count": n,
                "name": f"{display} - Extraction {n}",
                "location_id": LOCATION_BASE + EXTRACT_OFFSET + e_index,
            })
            e_index += 1

    # ----------------------------------------------------------------------------------
    # Kill milestones (cumulative totals across PMC and Scav raids)
    # ----------------------------------------------------------------------------------
    kill_records = []
    for t_index, (target, (display, totals)) in enumerate(KILL_MILESTONES.items()):
        for tier, total in enumerate(totals):
            kill_records.append({
                "target": target,
                "count": total,
                "name": f"Kills - {total} {display}",
                "level": min(60, total // KILL_MILESTONE_LEVELS[target]),
                "location_id": LOCATION_BASE + KILL_OFFSET + t_index * KILL_SLOTS_PER_TARGET + tier,
            })

    # ----------------------------------------------------------------------------------
    # Bosses (first kill of each). Spawn maps come from each location's BossLocationSpawn.
    # ----------------------------------------------------------------------------------
    boss_maps = {role: set() for role in BOSSES}
    for folder in os.listdir(os.path.join(db, "locations")):
        base_path = os.path.join(db, "locations", folder, "base.json")
        map_key = location_name_to_key.get(folder.lower())
        if map_key is None or not os.path.exists(base_path):
            continue
        for spawn in load(base_path).get("BossLocationSpawn", []):
            if (spawn.get("BossChance") or 0) <= 0:
                continue
            roles = [spawn.get("BossName")] + [s.get("BossEscortType") for s in spawn.get("Supports") or []]
            for role in roles:
                if role in boss_maps:
                    boss_maps[role].add(map_key)

    boss_records = []
    for b_index, (role, (key, display)) in enumerate(BOSSES.items()):
        maps = [m for m in MAPS if m in boss_maps[role]]
        if not maps:
            print(f"   ! boss {display} ({role}) spawns on no playable map; left out")
            continue
        boss_records.append({
            "key": key,
            "role": role,
            "name": f"Boss - {display}",
            "maps": maps,
            "level": BOSS_LEVEL,
            "location_id": LOCATION_BASE + BOSS_OFFSET + b_index,
        })

    loot_records = extract_loot(db, items, locale, location_name_to_key, out_path)

    # ----------------------------------------------------------------------------------
    # Crafts (first completion of each hideout recipe)
    # ----------------------------------------------------------------------------------
    present_levels = {(h["area"], h["level"]) for h in hideout_records}
    quest_ok = {q["id"] for q in quest_records if q["possible"]}
    previous_crafts = {}
    if os.path.exists(out_path):
        try:
            previous_crafts = {r["recipe"]: r for r in load(out_path).get("crafts", [])}
        except (OSError, ValueError):
            previous_crafts = {}
    used_craft_names = {r["name"] for r in previous_crafts.values()}
    next_craft_id = max((r["location_id"] for r in previous_crafts.values()),
                        default=LOCATION_BASE + CRAFT_OFFSET - 1) + 1
    # Contract n is "Contract n"; the last contract of every seed (the Kappa one) has its own name.
    contract_records = [{"index": 0, "name": "Contract - Kappa", "location_id": LOCATION_BASE + CONTRACT_OFFSET}]
    contract_records += [{"index": n, "name": f"Contract {n}", "location_id": LOCATION_BASE + CONTRACT_OFFSET + n}
                         for n in range(1, CONTRACT_MAX + 1)]
    craft_records = []
    craft_skipped = {}
    for recipe in sorted(load(os.path.join(db, "hideout", "production.json"))["recipes"], key=lambda r: r["_id"]):
        reqs = recipe.get("requirements", [])
        area_req = next((r for r in reqs if r["type"] == "Area"), None)
        quest_reqs = [r for r in reqs if r["type"] == "QuestComplete"]
        quest_req = next((r["questId"] for r in quest_reqs if r.get("questId")), None)
        reason = None
        if any(not r.get("questId") for r in quest_reqs):
            reason = "unnamed quest"  # unlocked by a quest the data doesn't name: logic can't place it
        elif recipe.get("isCodeProduction"):
            reason = "code"
        elif any(r["type"] == "GameVersion" for r in reqs):
            reason = "edition"
        elif area_req is None:
            reason = "no station"
        elif recipe["areaType"] in EXCLUDED_HIDEOUT_AREAS:
            reason = "excluded station"
        elif ((area_req["areaType"], area_req["requiredLevel"]) not in present_levels
              and area_req["requiredLevel"] > standard_levels.get(area_req["areaType"], 0)):
            reason = "station level unavailable"
        elif quest_req and quest_req not in quest_ok:
            reason = "quest not completable"
        elif recipe.get("locked") and not quest_req:
            reason = "locked"
        elif recipe["endProduct"] not in items:
            reason = "unknown product"
        if reason:
            craft_skipped[reason] = craft_skipped.get(reason, 0) + 1
            continue
        old = previous_crafts.get(recipe["_id"])
        if old:
            name, location_id = old["name"], old["location_id"]
        else:
            product = locale.get(f"{recipe['endProduct']} Name") or items[recipe["endProduct"]].get("_name")
            station = locale.get(f"hideout_area_{recipe['areaType']}_name") or f"Area {recipe['areaType']}"
            name = f"Craft - {product} ({station})"
            n, base_name = 2, name
            while name in used_craft_names:
                name = f"{base_name} #{n}"
                n += 1
            location_id = next_craft_id
            next_craft_id += 1
        used_craft_names.add(name)
        if location_id >= LOCATION_BASE + CRAFT_OFFSET + CRAFT_MAX:
            raise SystemExit("craftsanity location id range is full")
        craft_records.append({
            "recipe": recipe["_id"],
            "name": name,
            "area": area_req["areaType"],
            "level": area_req["requiredLevel"],
            "quest": quest_req,
            "product": recipe["endProduct"],
            "continuous": bool(recipe.get("continuous")),
            "location_id": location_id,
        })
    craft_records.sort(key=lambda r: r["location_id"])

    # ----------------------------------------------------------------------------------
    # Items
    # ----------------------------------------------------------------------------------
    item_records = []

    def add_item(name, kind, classification, **params):
        item_records.append({
            "name": name,
            "id": ITEM_BASE + len(item_records),
            "kind": kind,
            "classification": classification,
            "params": params,
        })

    for key, (display, loc_ids, _) in MAPS.items():
        add_item(f"{display} Access", "map", "progression", map=key, locations=loc_ids)
    for key, tid in GATED_TRADERS.items():
        add_item(TRADER_DISPLAY[key], "trader", "progression", trader=key, trader_id=tid)
    add_item("Flea Market Access", "flea", "progression")
    # Useful
    add_item("Experience Bundle", "xp", "useful", amount=15000)
    add_item("Large Experience Bundle", "xp", "useful", amount=50000)
    for key, tid in GATED_TRADERS.items():
        add_item(f"{TRADER_DISPLAY[key]} Reputation", "rep", "useful", trader=key, trader_id=tid, amount=0.05)
    add_item("Roubles (100,000)", "money", "useful", currency="5449016a4bdc2d6f028b456f", amount=100000)
    add_item("Roubles (500,000)", "money", "useful", currency="5449016a4bdc2d6f028b456f", amount=500000)
    add_item("Dollars (1,000)", "money", "useful", currency="5696686a4bdc2da3298b456a", amount=1000)
    add_item("Euros (1,000)", "money", "useful", currency="569668774bdc2da2298b4568", amount=1000)
    add_item("Weapon Crate", "loot", "useful", pool="weapon", count=1)
    add_item("Armor Crate", "loot", "useful", pool="armor", count=1)
    add_item("Medical Crate", "loot", "useful", pool="medical", count=4)
    add_item("Ammo Crate", "loot", "useful", pool="ammo", count=2)
    add_item("Barter Crate", "loot", "filler", pool="barter", count=3)
    add_item("Provisions Crate", "loot", "filler", pool="provisions", count=3)
    add_item("Key Crate", "loot", "useful", pool="key", count=2)
    add_item("Stimulant Crate", "loot", "useful", pool="stim", count=2)
    add_item("Roubles (25,000)", "money", "filler", currency="5449016a4bdc2d6f028b456f", amount=25000)
    # Traps
    add_item("Fence Grudge", "trap", "trap", trap="fence_rep", amount=-0.3)
    add_item("Exhaustion", "trap", "trap", trap="exhaustion")
    add_item("Scav Cooldown", "trap", "trap", trap="scav_cooldown", seconds=1200)
    # Hideout (option hideout_levels: items): one progressive item per station, one per level with a check.
    # Building a level sends its check; the level itself is active once enough of these have arrived.
    station_levels = OrderedDict()
    for h in hideout_records:
        name = h["name"][len("Hideout - "):h["name"].rindex(" Level ")]
        station_levels.setdefault(h["area"], [name, 0])[1] += 1
    for atype, (name, count) in station_levels.items():
        add_item(f"Progressive {name}", "station", "progression", area=atype, count=count,
                 base_level=min(h["level"] for h in hideout_records if h["area"] == atype) - 1)
    # The materials for the next station upgrade, sent as found-in-raid items.
    add_item("Hideout Supplies", "supplies", "useful")

    # A fingerprint of every item and location id/name. The APWorld sends it in slot data and the
    # mod refuses to play a seed made from a different table, instead of silently mismatching ids.
    import hashlib
    lines = [f"item:{i['id']}:{i['name']}" for i in item_records]
    lines += [f"loc:{q['location_id']}:{q['location_name']}" for q in quest_records if q["check"]]
    lines += [f"loc:{h['location_id']}:{h['name']}" for h in hideout_records]
    lines += [f"loc:{e['location_id']}:{e['name']}" for e in extract_records]
    lines += [f"loc:{k['location_id']}:{k['name']}" for k in kill_records]
    lines += [f"loc:{b['location_id']}:{b['name']}" for b in boss_records]
    lines += [f"loc:{r['location_id']}:{r['name']}:{r['tpl']}:{r['stand_in']}" for r in loot_records]
    lines += [f"loc:{r['location_id']}:{r['name']}:{r['recipe']}" for r in craft_records]
    lines += [f"loc:{c['location_id']}:{c['name']}" for c in contract_records]
    data_hash = hashlib.sha1("\n".join(sorted(lines)).encode("utf-8")).hexdigest()

    data = OrderedDict()
    data["meta"] = {
        "spt_version": "4.1.6",
        "eft_build": "0.16.9.5.40743",
        "item_base": ITEM_BASE,
        "location_base": LOCATION_BASE,
        "data_hash": data_hash,
    }
    data["maps"] = [
        {"key": k, "name": v[0], "location_ids": v[1], "item": f"{v[0]} Access"} for k, v in MAPS.items()
    ]
    data["traders"] = [
        {"key": k, "name": TRADER_DISPLAY[k], "id": tid,
         "ll_min_levels": [int(l.get("minLevel") or 0) for l in trader_bases[tid].get("loyaltyLevels", [])]}
        for k, tid in GATED_TRADERS.items()
    ]
    data["collector_quest_id"] = COLLECTOR_QUEST_ID
    data["quests"] = quest_records
    data["hideout"] = hideout_records
    data["extracts"] = extract_records
    data["kills"] = kill_records
    data["bosses"] = boss_records
    data["loot"] = loot_records
    data["crafts"] = craft_records
    data["contracts"] = contract_records
    data["items"] = item_records

    with open(out_path, "w", encoding="utf-8") as f:
        json.dump(data, f, indent=1, ensure_ascii=False)
        f.write("\n")

    checks = [r for r in quest_records if r["check"]]
    print(f"quests: {len(quest_records)} total, {len(checks)} checks, "
          f"{sum(1 for r in quest_records if not r['possible'])} impossible")
    print(f"hideout checks: {len(hideout_records)}; extract slots: {len(extract_records)}; "
          f"kill milestones: {len(kill_records)}; bosses: {len(boss_records)}; items: {len(item_records)}")
    for b in boss_records:
        print(f"   boss {b['name']}: {', '.join(b['maps'])}")
    from collections import Counter
    by_cat = Counter(r["category"] for r in loot_records)
    print(f"craftsanity: {len(craft_records)} recipes; left out: "
          + ", ".join(f"{k} {v}" for k, v in sorted(craft_skipped.items())))
    print(f"lootsanity: {len(loot_records)} item types ({sum(1 for r in loot_records if not r['maps'])} on bots "
          f"everywhere): " + ", ".join(f"{k} {v}" for k, v in sorted(by_cat.items())))
    print(f"objectives with unknown map (treated as needing every map): {len(unknown_objectives)}")
    for name, text in unknown_objectives:
        print(f"   ? {name}: {text}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1], sys.argv[2])

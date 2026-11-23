"""Archipelago world for Escape from Tarkov played through SPT (Single Player Tarkov) 4.0.13."""
from __future__ import annotations

import logging
from typing import Any, Callable, Dict, List

from BaseClasses import CollectionState, Item, ItemClassification, Location, LocationProgressType, Region, Tutorial
from Options import OptionError
from worlds.AutoWorld import WebWorld, World

from .contracts import Contract, write_contracts
from .data import load_data
from .items import (FILLER_WEIGHTS, FLEA_ITEM, GAME_NAME, MAP_ITEMS, TRADER_ITEMS, TRAP_ITEMS, TarkovItem,
                    item_name_groups, item_name_to_id, item_table)
from .logic import (ALL_MAPS, FLEA_MIN_LEVEL, LAB_KEYCARD_SOURCES, WEAPON_PARTS_TRADERS, Requirement, RequirementBuilder,
                    maps_needed_for_level)
from .options import (LOOT_CATEGORY_KEYS, SHORT_KILL_LIMITS, Goal, KillMilestones, QuestMode, ShopRandomizer,
                      StartingMap, StartingTraders, TarkovOptions)

logger = logging.getLogger("Escape from Tarkov")

DATA_VERSION = 1

STARTING_MAP_KEYS = {
    StartingMap.option_ground_zero: "groundzero",
    StartingMap.option_factory: "factory",
    StartingMap.option_customs: "customs",
    StartingMap.option_woods: "woods",
    StartingMap.option_interchange: "interchange",
    StartingMap.option_shoreline: "shoreline",
    StartingMap.option_reserve: "reserve",
    StartingMap.option_lighthouse: "lighthouse",
    StartingMap.option_streets_of_tarkov: "streets",
}
TRADER_KEYS = ["prapor", "therapist", "skier", "peacekeeper", "mechanic", "ragman", "jaeger"]
TRADER_DISPLAY = {t["key"]: t["name"] for t in load_data()["traders"]}


def _build_location_table() -> Dict[str, int]:
    data = load_data()
    table: Dict[str, int] = {}
    for q in data["quests"]:
        if q["check"]:
            table[q["location_name"]] = q["location_id"]
    for h in data["hideout"]:
        table[h["name"]] = h["location_id"]
    for e in data["extracts"]:
        table[e["name"]] = e["location_id"]
    for k in data["kills"]:
        table[k["name"]] = k["location_id"]
    for b in data["bosses"]:
        table[b["name"]] = b["location_id"]
    for r in data["loot"]:
        table[r["name"]] = r["location_id"]
    for c in data["crafts"]:
        table[c["name"]] = c["location_id"]
    for c in data["contracts"]:
        table[c["name"]] = c["location_id"]
    return table


def loot_requirement(loot: dict) -> Requirement:
    # No maps listed: generic bots carry it, so any map will do.
    if not loot["maps"]:
        return Requirement()
    return Requirement(clauses=frozenset({frozenset(loot["maps"])}))


def kill_requirement(kill: dict) -> Requirement:
    return Requirement(level=kill["level"])


def boss_requirement(boss: dict) -> Requirement:
    return Requirement(clauses=frozenset({frozenset(boss["maps"])}), level=boss["level"])


location_table = _build_location_table()


class TarkovLocation(Location):
    game = GAME_NAME


class TarkovWeb(WebWorld):
    theme = "stone"
    tutorials = [Tutorial(
        "Multiworld Setup Guide",
        "A guide to setting up Escape from Tarkov (SPT) for Archipelago.",
        "English",
        "setup_en.md",
        "setup/en",
        ["Pegzzzz"],
    )]


class TarkovWorld(World):
    """Escape from Tarkov is a hardcore extraction shooter. Played offline through SPT, every map and
    every main trader is locked behind an Archipelago item, and finishing quests, building your hideout
    and surviving raids send checks out to the multiworld."""

    game = GAME_NAME
    web = TarkovWeb()
    options_dataclass = TarkovOptions
    options: TarkovOptions
    topology_present = False

    item_name_to_id = item_name_to_id
    location_name_to_id = location_table
    item_name_groups = item_name_groups
    location_name_groups = {
        "Quests": {q["location_name"] for q in load_data()["quests"] if q["check"]},
        "Hideout": {h["name"] for h in load_data()["hideout"]},
        "Extractions": {e["name"] for e in load_data()["extracts"]},
        "Kills": {k["name"] for k in load_data()["kills"]},
        "Bosses": {b["name"] for b in load_data()["bosses"]},
        "Loot": {r["name"] for r in load_data()["loot"]},
        "Crafts": {c["name"] for c in load_data()["crafts"]},
        "Contracts": {c["name"] for c in load_data()["contracts"]},
        **{f"Loot - {key.replace('_', ' ').title()}": {r["name"] for r in load_data()["loot"] if r["category"] == key}
           for key in LOOT_CATEGORY_KEYS},
    }

    def __init__(self, multiworld, player):
        super().__init__(multiworld, player)
        self.requirements = RequirementBuilder()
        self.start_map: str = "groundzero"
        self.start_traders: List[str] = []
        self.quest_checks: List[dict] = []
        self.hideout_checks: List[dict] = []
        self.extract_checks: List[dict] = []
        self.kill_checks: List[dict] = []
        self.boss_checks: List[dict] = []
        self.loot_checks: List[dict] = []
        self.craft_checks: List[dict] = []
        self.contracts: List[Contract] = []
        self.shop_seed = 0
        self.goal_quest_count = 0

    # ------------------------------------------------------------------ generation steps
    def generate_early(self) -> None:
        self.start_map = STARTING_MAP_KEYS[self.options.starting_map.value]

        mode = self.options.starting_traders.value
        if mode == StartingTraders.option_prapor_and_therapist:
            self.start_traders = ["prapor", "therapist"]
        else:
            count = 2 if mode == StartingTraders.option_random_two else 1
            self.start_traders = self.random.sample(TRADER_KEYS, count)

        data = load_data()
        allowed_traders = {name.lower() for name in self.options.quest_traders.value}
        max_level = self.options.max_quest_level.value
        for q in data["quests"]:
            if self.contracts_mode:
                break  # Tarkov's own quests are switched off
            if not q["check"] or q["trader"] not in allowed_traders:
                continue
            if self.requirements.quest(q["id"]).level > max_level:
                continue
            self.quest_checks.append(q)

        if self.options.hideout_checks:
            self.hideout_checks = list(data["hideout"])

        per_map = self.options.extracts_per_map.value
        self.extract_checks = [e for e in data["extracts"] if e["count"] <= per_map]

        kills = self.options.kill_milestones.value
        if kills == KillMilestones.option_long:
            self.kill_checks = list(data["kills"])
        elif kills == KillMilestones.option_short:
            self.kill_checks = [k for k in data["kills"] if k["count"] <= SHORT_KILL_LIMITS[k["target"]]]
        if self.options.boss_checks:
            self.boss_checks = list(data["bosses"])
        categories = set(self.options.lootsanity.value)
        self.loot_checks = [r for r in data["loot"] if r["category"] in categories]
        if self.options.craftsanity:
            # Recipes unlocked by a vanilla quest can't be crafted when those quests are switched off.
            self.craft_checks = [c for c in data["crafts"] if not (self.contracts_mode and c["quest"])]
        self.shop_seed = self.random.getrandbits(31)
        if self.contracts_mode:
            self.contracts = write_contracts(self.random, self.options.contract_count.value, self.start_map)

        if self.options.goal == Goal.option_quest_count and not self.contracts_mode:
            if not self.quest_checks:
                raise OptionError(f"Escape from Tarkov ({self.player_name}): the Quest Count goal needs at least one "
                                  f"quest check, but Quest Traders / Max Quest Level leave none.")
            self.goal_quest_count = min(self.options.goal_quest_count.value, len(self.quest_checks))
            if self.goal_quest_count < self.options.goal_quest_count.value:
                logger.warning(f"Escape from Tarkov ({self.player_name}): only {len(self.quest_checks)} quest checks "
                               f"exist with these options; goal lowered to {self.goal_quest_count} quests.")

        # Make sure the game can start: there must be checks reachable with the starting items.
        start_state = (frozenset(self.start_traders), frozenset({self.start_map}), 1)
        if self._count_sphere_zero(start_state) < 3:
            if not self.extract_checks:
                self.extract_checks = [e for e in data["extracts"] if e["count"] <= 1]
                logger.warning(f"Escape from Tarkov ({self.player_name}): too few checks are reachable at the start; "
                               f"enabling one extraction check per map.")
            if not self.hideout_checks:
                self.hideout_checks = list(data["hideout"])
                logger.warning(f"Escape from Tarkov ({self.player_name}): too few checks are reachable at the start; "
                               f"enabling hideout checks.")

    @property
    def contracts_mode(self) -> bool:
        return self.options.quest_mode == QuestMode.option_archipelago_contracts

    @property
    def goal_key(self) -> str:
        return "kappa_contract" if self.contracts_mode else self.options.goal.current_key

    @property
    def shops_randomized(self) -> bool:
        return self.options.shop_randomizer != ShopRandomizer.option_vanilla

    def craft_requirement(self, craft: dict) -> Requirement:
        """The station level (with everything it needs) and, for quest-unlocked recipes, that quest."""
        req = self.requirements.hideout_level(craft["area"], craft["level"])
        if craft["quest"]:
            req = req.merge(self.requirements.quest(craft["quest"]))
        return req

    def _count_sphere_zero(self, start_state) -> int:
        traders, maps, _ = start_state
        pacing = self.options.level_pacing.value

        def ok(req: Requirement) -> bool:
            parts_ok = (not req.weapon_parts or not self.options.lock_flea_market.value
                        or (not self.shops_randomized and WEAPON_PARTS_TRADERS <= traders))
            keycard_ok = frozenset({"labs"}) not in req.clauses  # never true at the start: The Lab is not a start map
            return (req.traders <= traders
                    and all(clause & maps for clause in req.clauses)
                    and maps_needed_for_level(req.level, pacing) <= len(maps)
                    and parts_ok and keycard_ok)

        count = sum(1 for q in self.quest_checks if ok(self.requirements.quest(q["id"])))
        count += sum(1 for h in self.hideout_checks if ok(self.requirements.hideout_level(h["area"], h["level"])))
        count += sum(1 for e in self.extract_checks if e["map"] in maps)
        count += sum(1 for k in self.kill_checks if ok(kill_requirement(k)))
        count += sum(1 for b in self.boss_checks if ok(boss_requirement(b)))
        count += sum(1 for r in self.loot_checks if ok(loot_requirement(r)))
        count += sum(1 for c in self.craft_checks if ok(self.craft_requirement(c)))
        count += sum(1 for c in self.contracts if ok(c.requirement))
        return count

    def create_regions(self) -> None:
        menu = Region("Menu", self.player, self.multiworld)
        tarkov = Region("Tarkov", self.player, self.multiworld)
        self.multiworld.regions += [menu, tarkov]
        menu.connect(tarkov)

        for q in self.quest_checks:
            tarkov.locations.append(TarkovLocation(self.player, q["location_name"], q["location_id"], tarkov))
        for h in self.hideout_checks:
            tarkov.locations.append(TarkovLocation(self.player, h["name"], h["location_id"], tarkov))
        for e in self.extract_checks:
            tarkov.locations.append(TarkovLocation(self.player, e["name"], e["location_id"], tarkov))
        for k in self.kill_checks:
            tarkov.locations.append(TarkovLocation(self.player, k["name"], k["location_id"], tarkov))
        for b in self.boss_checks:
            tarkov.locations.append(TarkovLocation(self.player, b["name"], b["location_id"], tarkov))
        for c in self.craft_checks:
            tarkov.locations.append(TarkovLocation(self.player, c["name"], c["location_id"], tarkov))
        for c in self.contracts:
            tarkov.locations.append(TarkovLocation(self.player, c.location_name, c.location_id, tarkov))
        loot_excluded = not self.options.lootsanity_progression
        for r in self.loot_checks:
            location = TarkovLocation(self.player, r["name"], r["location_id"], tarkov)
            if loot_excluded:
                location.progress_type = LocationProgressType.EXCLUDED
            tarkov.locations.append(location)

        # Goal events
        if self.goal_key == "quest_count":
            for q in self.quest_checks:
                event = TarkovLocation(self.player, f"{q['location_name']} (Completed)", None, tarkov)
                event.place_locked_item(self._event("Quest Completed"))
                tarkov.locations.append(event)
        victory = TarkovLocation(self.player, "Escape from Tarkov", None, tarkov)
        victory.place_locked_item(self._event("Victory"))
        tarkov.locations.append(victory)

    def _event(self, name: str) -> Item:
        return TarkovItem(name, ItemClassification.progression, None, self.player)

    def create_item(self, name: str) -> Item:
        info = item_table[name]
        return TarkovItem(name, info.classification, info.code, self.player)

    def get_filler_item_name(self) -> str:
        names = list(FILLER_WEIGHTS.keys())
        return self.random.choices(names, weights=list(FILLER_WEIGHTS.values()))[0]

    def create_items(self) -> None:
        start_map_item = MAP_ITEMS[self.start_map]
        self.multiworld.push_precollected(self.create_item(start_map_item))
        for trader in self.start_traders:
            self.multiworld.push_precollected(self.create_item(TRADER_ITEMS[trader]))

        pool: List[Item] = []
        for key, name in MAP_ITEMS.items():
            if key != self.start_map:
                pool.append(self.create_item(name))
        for key, name in TRADER_ITEMS.items():
            if key not in self.start_traders:
                pool.append(self.create_item(name))
        if self.options.lock_flea_market:
            pool.append(self.create_item(FLEA_ITEM))

        total_locations = (len(self.quest_checks) + len(self.hideout_checks) + len(self.extract_checks)
                           + len(self.kill_checks) + len(self.boss_checks) + len(self.loot_checks)
                           + len(self.craft_checks) + len(self.contracts))
        # Loot checks that may only hold filler can't take the progression items.
        loot_excluded = len(self.loot_checks) if not self.options.lootsanity_progression else 0
        if len(pool) > total_locations - loot_excluded:
            raise OptionError(f"Escape from Tarkov ({self.player_name}): {total_locations - loot_excluded} checks are "
                              f"not enough for {len(pool)} required items. Enable more checks.")
        remaining = total_locations - len(pool)
        trap_count = 0
        if TRAP_ITEMS and self.options.trap_percentage.value:
            trap_count = remaining * self.options.trap_percentage.value // 100
        for _ in range(trap_count):
            pool.append(self.create_item(self.random.choice(TRAP_ITEMS)))
        # Excluded locations take only filler-class items (or traps): make sure there are enough of those.
        filler_only = max(0, min(remaining - trap_count, loot_excluded - trap_count))
        filler_names = [n for n in FILLER_WEIGHTS if item_table[n].classification == ItemClassification.filler]
        filler_weights = [FILLER_WEIGHTS[n] for n in filler_names]
        for _ in range(filler_only):
            pool.append(self.create_item(self.random.choices(filler_names, weights=filler_weights)[0]))
        for _ in range(remaining - trap_count - filler_only):
            pool.append(self.create_item(self.get_filler_item_name()))
        self.multiworld.itempool += pool

    # ------------------------------------------------------------------ rules
    def _rule(self, req: Requirement) -> Callable[[CollectionState], bool]:
        player = self.player
        trader_items = [TRADER_ITEMS[t] for t in sorted(req.traders)]
        clause_items = [[MAP_ITEMS[m] for m in sorted(clause)] for clause in req.clauses]
        maps_needed = maps_needed_for_level(req.level, self.options.level_pacing.value)
        all_map_items = list(MAP_ITEMS.values())
        flea_locked = bool(self.options.lock_flea_market.value)
        needs_parts = req.weapon_parts and flea_locked
        # Randomized shops may not sell the parts: then only the Flea Market can be counted on.
        shops_random = self.shops_randomized
        parts_traders = [TRADER_ITEMS[t] for t in sorted(WEAPON_PARTS_TRADERS)]
        # Anything that can only be done in The Lab needs a way to get its keycard.
        needs_keycard = frozenset({"labs"}) in req.clauses
        pacing = self.options.level_pacing.value
        keycard_traders = [(TRADER_ITEMS[t], maps_needed_for_level(level, pacing)) for t, level in LAB_KEYCARD_SOURCES]
        flea_maps = maps_needed_for_level(FLEA_MIN_LEVEL, pacing)

        def has_keycard_source(state: CollectionState) -> bool:
            for trader_item, maps in keycard_traders:
                if state.has(trader_item, player) and state.has_from_list(all_map_items, player, maps):
                    return True
            return (not flea_locked or state.has(FLEA_ITEM, player)) and state.has_from_list(all_map_items, player, flea_maps)

        def rule(state: CollectionState) -> bool:
            if trader_items and not state.has_all(trader_items, player):
                return False
            if needs_parts and not (state.has(FLEA_ITEM, player)
                                    or (not shops_random and state.has_all(parts_traders, player))):
                return False
            if needs_keycard and not has_keycard_source(state):
                return False
            for items in clause_items:
                if not state.has_any(items, player):
                    return False
            if maps_needed > 1 and not state.has_from_list(all_map_items, player, maps_needed):
                return False
            return True

        return rule

    def set_rules(self) -> None:
        player = self.player
        for q in self.quest_checks:
            rule = self._rule(self.requirements.quest(q["id"]))
            self.get_location(q["location_name"]).access_rule = rule
            if self.goal_key == "quest_count":
                self.get_location(f"{q['location_name']} (Completed)").access_rule = rule
        for h in self.hideout_checks:
            self.get_location(h["name"]).access_rule = self._rule(self.requirements.hideout_level(h["area"], h["level"]))
        for e in self.extract_checks:
            self.get_location(e["name"]).access_rule = self._rule(
                Requirement(clauses=frozenset({frozenset({e["map"]})})))
        for k in self.kill_checks:
            self.get_location(k["name"]).access_rule = self._rule(kill_requirement(k))
        for c in self.craft_checks:
            self.get_location(c["name"]).access_rule = self._rule(self.craft_requirement(c))
        loot_rules: Dict[Requirement, Callable[[CollectionState], bool]] = {}
        for r in self.loot_checks:
            req = loot_requirement(r)
            if req not in loot_rules:
                loot_rules[req] = self._rule(req)
            self.get_location(r["name"]).access_rule = loot_rules[req]
        for b in self.boss_checks:
            self.get_location(b["name"]).access_rule = self._rule(boss_requirement(b))

        for c in self.contracts:
            self.get_location(c.location_name).access_rule = self._rule(c.requirement)

        victory = self.get_location("Escape from Tarkov")
        goal = self.options.goal.value
        if self.contracts_mode:
            victory.access_rule = self._rule(self.contracts[-1].requirement)
        elif goal == Goal.option_quest_count:
            needed = self.goal_quest_count
            victory.access_rule = lambda state: state.has("Quest Completed", player, needed)
        elif goal == Goal.option_collector:
            victory.access_rule = self._rule(self.requirements.quest(load_data()["collector_quest_id"]))
        elif goal == Goal.option_bosses:
            boss_rules = [self._rule(boss_requirement(b)) for b in load_data()["bosses"]]
            victory.access_rule = lambda state: all(rule(state) for rule in boss_rules)
        else:
            every_map = [MAP_ITEMS[m] for m in ALL_MAPS]
            lab_rule = self._rule(Requirement(clauses=frozenset({frozenset({"labs"})})))
            victory.access_rule = lambda state: state.has_all(every_map, player) and lab_rule(state)
        self.multiworld.completion_condition[player] = lambda state: state.has("Victory", player)

    # ------------------------------------------------------------------ output
    def fill_slot_data(self) -> Dict[str, Any]:
        return {
            "data_version": DATA_VERSION,
            "data_hash": load_data()["meta"]["data_hash"],
            "spt_version": load_data()["meta"]["spt_version"],
            "goal": self.goal_key,
            "quest_mode": self.options.quest_mode.current_key,
            "contracts": [c.slot_data() for c in self.contracts],
            "goal_quest_count": self.goal_quest_count,
            "collector_quest_id": load_data()["collector_quest_id"],
            "extracts_per_map": self.options.extracts_per_map.value,
            "kill_milestones": self.options.kill_milestones.current_key,
            "boss_checks": bool(self.options.boss_checks.value),
            "lootsanity": sorted(self.options.lootsanity.value),
            "craftsanity": bool(self.options.craftsanity.value),
            "shop_randomizer": self.options.shop_randomizer.current_key,
            "shop_reshuffle": self.options.shop_reshuffle.current_key,
            "shop_stock": self.options.shop_stock.value,
            "shop_seed": self.shop_seed,
            "lock_flea_market": bool(self.options.lock_flea_market.value),
            "death_link": bool(self.options.death_link.value),
            "starting_map": self.start_map,
            "starting_traders": self.start_traders,
            "starting_kit": self.options.starting_kit.current_key,
            "starting_kit_quality": self.options.starting_kit_quality.current_key,
        }

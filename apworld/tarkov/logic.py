"""Requirement model for Escape from Tarkov.

Every check is reduced to three things the player must have:
  * a set of traders (each main trader is an item),
  * a list of map clauses; each clause is satisfied by owning any one of its maps,
  * a player level, approximated by how many maps are unlocked (see LevelPacing).

Quest requirements include everything their prerequisite quests need, so a quest can
never be in logic before the chain that leads to it.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, FrozenSet, Iterable, List, Optional, Tuple

from .data import load_data

ALL_MAPS: Tuple[str, ...] = tuple(m["key"] for m in load_data()["maps"])

# Weapon assembly quests from this player level on expect parts that only the Flea Market or a broad set
# of traders sell (the early Gunsmith steps can be done with Mechanic and Prapor alone).
WEAPON_PARTS_LEVEL = 10
WEAPON_PARTS_TRADERS = frozenset({"prapor", "skier", "peacekeeper", "mechanic", "jaeger"})

# Entering The Lab uses up a keycard. Traders sell it at Mechanic loyalty 3 or Therapist loyalty 4;
# otherwise it comes from the Flea Market (open from level 15).
_TRADER_LL_LEVELS = {t["key"]: t["ll_min_levels"] for t in load_data()["traders"]}
LAB_KEYCARD_SOURCES = (
    ("mechanic", _TRADER_LL_LEVELS["mechanic"][2]),
    ("therapist", _TRADER_LL_LEVELS["therapist"][3]),
)
FLEA_MIN_LEVEL = 15


@dataclass(frozen=True)
class Requirement:
    traders: FrozenSet[str] = frozenset()
    clauses: FrozenSet[FrozenSet[str]] = frozenset()
    level: int = 0
    # Weapon builds past the first Gunsmith steps need parts from many traders or the Flea Market.
    weapon_parts: bool = False
    # hideout_levels: items. (station area type, how many of its progressive items) pairs.
    stations: FrozenSet[Tuple[int, int]] = frozenset()

    def merge(self, other: "Requirement") -> "Requirement":
        return Requirement(
            self.traders | other.traders,
            _simplify(self.clauses | other.clauses),
            max(self.level, other.level),
            self.weapon_parts or other.weapon_parts,
            _merge_stations(self.stations, other.stations),
        )


def _merge_stations(a: FrozenSet[Tuple[int, int]], b: FrozenSet[Tuple[int, int]]) -> FrozenSet[Tuple[int, int]]:
    if not a or not b:
        return a or b
    best: Dict[int, int] = {}
    for area, count in a | b:
        best[area] = max(best.get(area, 0), count)
    return frozenset(best.items())


def _simplify(clauses: FrozenSet[FrozenSet[str]]) -> FrozenSet[FrozenSet[str]]:
    """Drop clauses that are implied by a stricter clause (a subset of their maps)."""
    result = set(clauses)
    for clause in clauses:
        for other in clauses:
            if other is not clause and other < clause:
                result.discard(clause)
                break
    return frozenset(result)


def _objective_clauses(objective_maps: Iterable[List[str]]) -> FrozenSet[FrozenSet[str]]:
    clauses = set()
    for maps in objective_maps:
        if "*" in maps:
            # Map could not be determined from game data: require every map.
            for key in ALL_MAPS:
                clauses.add(frozenset({key}))
        else:
            clauses.add(frozenset(maps))
    return _simplify(frozenset(clauses))


class RequirementBuilder:
    """Computes (and caches) the full requirement of quests and hideout levels."""

    def __init__(self, station_items: bool = False) -> None:
        """station_items: hideout levels come as progressive items (option hideout_levels: items)."""
        data = load_data()
        self.station_items = station_items
        self.base_levels: Dict[int, int] = {}
        for h in data["hideout"]:
            self.base_levels[h["area"]] = min(self.base_levels.get(h["area"], h["level"]), h["level"])
        self.base_levels = {area: low - 1 for area, low in self.base_levels.items()}
        self.quests: Dict[str, dict] = {q["id"]: q for q in data["quests"]}
        self.hideout: Dict[Tuple[int, int], dict] = {(h["area"], h["level"]): h for h in data["hideout"]}
        self._quest_cache: Dict[Tuple[str, str], Requirement] = {}
        self._hideout_cache: Dict[Tuple[int, int], Requirement] = {}

    @staticmethod
    def _trader_requirement(entries: Iterable[dict]) -> Requirement:
        traders = frozenset(e["trader"] for e in entries if e.get("trader"))
        level = max((int(e.get("min_level") or 0) for e in entries), default=0)
        return Requirement(traders, frozenset(), level)

    def quest(self, quest_id: str, mode: str = "success", _stack: Optional[set] = None) -> Requirement:
        key = (quest_id, "start" if mode == "start" else "done")
        cached = self._quest_cache.get(key)
        if cached is not None:
            return cached
        stack = _stack if _stack is not None else set()
        if key in stack:
            raise ValueError(f"circular quest prerequisites at {quest_id}")
        stack.add(key)

        q = self.quests[quest_id]
        req = Requirement(
            frozenset({q["trader"]}) if q.get("trader") else frozenset(),
            frozenset(),
            int(q.get("min_level") or 0),
        )
        req = req.merge(self._trader_requirement(q.get("trader_ll", [])))
        if key[1] == "done":
            req = req.merge(Requirement(clauses=_objective_clauses(q.get("objective_maps", []))))
            req = req.merge(self._trader_requirement(q.get("finish_trader_ll", [])))
            for prereq in q.get("finish_prereqs", []):
                if prereq["id"] in self.quests:
                    req = req.merge(self.quest(prereq["id"], prereq["mode"], stack))
            if q.get("weapon_assembly") and int(q.get("min_level") or 0) >= WEAPON_PARTS_LEVEL:
                req = req.merge(Requirement(weapon_parts=True))
        for prereq in q.get("prereqs", []):
            if prereq["id"] in self.quests:
                req = req.merge(self.quest(prereq["id"], prereq["mode"], stack))

        stack.discard(key)
        self._quest_cache[key] = req
        return req

    def hideout_level(self, area: int, level: int) -> Requirement:
        key = (area, level)
        cached = self._hideout_cache.get(key)
        if cached is not None:
            return cached
        h = self.hideout.get(key)
        if h is None:
            # Already built on a fresh Standard profile.
            return Requirement()
        # Higher levels ask for a lot of loot; expect some progression before them.
        req = Requirement(level=(level - 1) * 8)
        req = req.merge(self._trader_requirement(h.get("trader_ll", [])))
        for pa, pl in h.get("prereq_areas", []):
            req = req.merge(self.station_active(pa, pl))
        self._hideout_cache[key] = req
        return req

    def station_active(self, area: int, level: int) -> Requirement:
        """A station working at this level (for crafts and other stations): built, and with hideout_levels: items,
        enough of its progressive items received."""
        req = self.hideout_level(area, level)
        if self.station_items and (area, level) in self.hideout:
            req = req.merge(Requirement(stations=frozenset({(area, level - self.base_levels[area])})))
        return req


def maps_needed_for_level(level: int, pacing: int) -> int:
    return min(len(ALL_MAPS), 1 + level // max(1, pacing))

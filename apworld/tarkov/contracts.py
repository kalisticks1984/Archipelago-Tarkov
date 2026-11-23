"""Archipelago contracts: the quest line that replaces Tarkov's own quests (Quest Mode archipelago_contracts).

The seed writes the contracts. Each contract has one to three objectives (kills, a boss, surviving a map, handing in
found-in-raid items), one or two earlier contracts it follows, and rewards. Contract n is the location "Contract n".
The last contract ("Contract - Kappa") needs every other one and rewards the Kappa container. The server mod turns
this list (sent in slot data) into real quests of the Archipelago trader.

A contract's requirement includes everything its earlier contracts need, so the line is always played in an order
the received items allow, and early contracts only use what you start with.
"""
from __future__ import annotations

from dataclasses import dataclass, field
from typing import Dict, List, Optional

from .data import load_data
from .logic import ALL_MAPS, Requirement

KAPPA_TPL = "5c093ca986f7740a1867ab12"
TRADER_KEYS = ["prapor", "therapist", "skier", "peacekeeper", "mechanic", "ragman", "jaeger"]
HANDOVER_CATEGORIES = {"barter", "provisions", "medication"}
# How many contracts can be open at once, roughly: each one follows a contract among the last few.
WINDOW = 5
FIRST_OPEN = 3

MAP_NAMES = {m["key"]: m["name"] for m in load_data()["maps"]}
CONTRACT_LOCATIONS = {c["index"]: c for c in load_data()["contracts"]}
MAX_CONTRACTS = max(CONTRACT_LOCATIONS)


@dataclass
class Contract:
    index: int  # 0 is the Kappa contract
    title: str
    objectives: List[dict]
    prereqs: List[int]
    client: Optional[str]
    xp: int
    roubles: int
    standing: float
    own: Requirement  # what its objectives need
    requirement: Requirement = field(default_factory=Requirement)  # including every earlier contract

    @property
    def location_name(self) -> str:
        return CONTRACT_LOCATIONS[self.index]["name"]

    @property
    def location_id(self) -> int:
        return CONTRACT_LOCATIONS[self.index]["location_id"]

    def slot_data(self) -> dict:
        return {"index": self.index, "title": self.title, "objectives": self.objectives, "prereqs": self.prereqs,
                "client": self.client, "xp": self.xp, "roubles": self.roubles, "standing": self.standing}


def _map_clause(maps) -> Requirement:
    return Requirement(clauses=frozenset({frozenset(maps)}))


class ContractWriter:
    def __init__(self, random, count: int, start_map: str) -> None:
        data = load_data()
        self.random = random
        self.count = count
        self.start_map = start_map
        self.bosses = list(data["bosses"])
        self.items = sorted((r for r in data["loot"]
                             if r["category"] in HANDOVER_CATEGORIES and r["maps"] and r.get("price", 0) > 0),
                            key=lambda r: r["price"])

    # ------------------------------------------------------------------ objectives
    def _maps_for(self, progress: float) -> List[str]:
        if progress < 0.08:
            return [self.start_map]
        return [m for m in ALL_MAPS if m != "labs" or progress >= 0.4]

    def _kill(self, progress: float, maps: List[str]) -> (dict, Requirement):
        target = self.random.choices(["scav", "pmc", "any"], weights=[5, 3 if progress > 0.15 else 1, 2])[0]
        scale = {"scav": (4, 20), "pmc": (1, 7), "any": (5, 18)}[target]
        count = round(scale[0] + (scale[1] - scale[0]) * progress * self.random.uniform(0.6, 1.0))
        on_map = self.random.random() < 0.7 or progress < 0.08
        map_key = self.random.choice(maps) if on_map else None
        objective = {"type": "kill", "target": target, "count": max(1, count), "map": map_key}
        return objective, (_map_clause([map_key]) if map_key else Requirement())

    def _boss(self, progress: float) -> (dict, Requirement):
        boss = self.random.choice(self.bosses)
        objective = {"type": "boss", "role": boss["role"], "name": boss["name"].removeprefix("Boss - "),
                     "maps": boss["maps"], "count": 1}
        return objective, Requirement(clauses=frozenset({frozenset(boss["maps"])}), level=boss["level"])

    def _extract(self, progress: float, maps: List[str]) -> (dict, Requirement):
        map_key = self.random.choice(maps)
        count = 1 if progress < 0.5 else self.random.choice([1, 2])
        return {"type": "extract", "map": map_key, "count": count}, _map_clause([map_key])

    def _handover(self, progress: float, maps: List[str], budget: Optional[int] = None,
                  floor: int = 0) -> (dict, Requirement):
        budget = budget or int(12_000 + progress * 110_000)
        allowed = set(maps)
        pool = [r for r in self.items if floor <= r["price"] <= budget and allowed.intersection(r["maps"])]
        if not pool:
            pool = [r for r in self.items if allowed.intersection(r["maps"])] or self.items
            pool = pool[:20]
        item = self.random.choice(pool)
        count = max(1, min(5, budget // max(1, item["price"])))
        count = self.random.randint(max(1, count // 2), count)
        objective = {"type": "handover", "tpl": item["tpl"], "count": count}
        return objective, _map_clause(item["maps"])

    def _objectives(self, progress: float) -> (List[dict], Requirement):
        maps = self._maps_for(progress)
        wanted = 1 if progress < 0.25 else (2 if progress < 0.7 else self.random.choice([2, 2, 3]))
        kinds = ["kill", "extract", "handover"] + (["boss"] if progress >= 0.35 else [])
        weights = {"kill": 4, "extract": 2, "handover": 3, "boss": 1}
        objectives, req, used = [], Requirement(), set()
        while len(objectives) < wanted:
            kind = self.random.choices(kinds, weights=[weights[k] for k in kinds])[0]
            if kind in used:
                continue
            used.add(kind)
            if kind == "kill":
                objective, r = self._kill(progress, maps)
            elif kind == "boss":
                objective, r = self._boss(progress)
            elif kind == "extract":
                objective, r = self._extract(progress, maps)
            else:
                objective, r = self._handover(progress, maps)
            objectives.append(objective)
            req = req.merge(r)
        return objectives, req

    @staticmethod
    def _title(objectives: List[dict]) -> str:
        first = objectives[0]
        where = MAP_NAMES.get(first.get("map") or "", "")
        if first["type"] == "kill":
            base = {"scav": "Pest Control", "pmc": "Hunting Party", "any": "Thinning the Herd"}[first["target"]]
            return f"{base} on {where}" if where else base
        if first["type"] == "boss":
            return f"The Price on {first['name']}"
        if first["type"] == "extract":
            return f"Way Out of {where}"
        return "Supply Run"

    # ------------------------------------------------------------------ the line
    def write(self) -> List[Contract]:
        clients = (TRADER_KEYS * (self.count // len(TRADER_KEYS) + 1))[:self.count]
        self.random.shuffle(clients)
        # Enough standing over the whole line to reach loyalty level 4 with every trader, as the quests would.
        standing = round(min(0.15, max(0.04, len(TRADER_KEYS) * 0.9 / self.count)), 2)
        contracts: Dict[int, Contract] = {}
        for n in range(1, self.count + 1):
            progress = (n - 1) / max(1, self.count - 1)
            objectives, own = self._objectives(progress)
            own = own.merge(Requirement(level=round(3 + progress * 50)))
            if n <= FIRST_OPEN:
                prereqs = []
            else:
                low = max(1, n - WINDOW)
                prereqs = [self.random.randint(low, n - 1)]
                if n > 10 and self.random.random() < 0.25:
                    other = self.random.randint(max(1, n - 3 * WINDOW), n - 1)
                    if other not in prereqs:
                        prereqs.append(other)
            contract = Contract(
                index=n, title=f"Contract {n}: {self._title(objectives)}", objectives=objectives,
                prereqs=sorted(prereqs), client=clients[n - 1],
                xp=int(round(800 + progress * 14_000, -2)), roubles=int(round(10_000 + progress * 90_000, -3)),
                standing=standing, own=own)
            req = own
            for p in prereqs:
                req = req.merge(contracts[p].requirement)
            contract.requirement = req
            contracts[n] = contract

        # Kappa: every contract, then a hand-in of valuables, like the Collector.
        kappa_objectives, kappa_own = [], Requirement()
        tpls = set()
        for _ in range(4):
            objective, r = self._handover(1.0, list(ALL_MAPS), budget=400_000, floor=40_000)
            if objective["tpl"] in tpls:
                continue
            tpls.add(objective["tpl"])
            objective["count"] = 1
            kappa_objectives.append(objective)
            kappa_own = kappa_own.merge(r)
        kappa = Contract(index=0, title="Kappa", objectives=kappa_objectives, prereqs=list(range(1, self.count + 1)),
                         client=None, xp=50_000, roubles=0, standing=0.0, own=kappa_own)
        req = kappa_own
        for c in contracts.values():
            req = req.merge(c.requirement)
        kappa.requirement = req
        return list(contracts.values()) + [kappa]


def write_contracts(random, count: int, start_map: str) -> List[Contract]:
    return ContractWriter(random, min(count, MAX_CONTRACTS), start_map).write()

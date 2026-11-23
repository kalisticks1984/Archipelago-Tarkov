import random
import unittest

from ..contracts import KAPPA_TPL, write_contracts
from ..data import load_data
from ..logic import ALL_MAPS
from .bases import TarkovTestBase

LOCATION_BASE = 76_400_000
LOOT = {r["tpl"]: r for r in load_data()["loot"]}
BOSS_ROLES = {b["role"] for b in load_data()["bosses"]}


def _names(world_test) -> set:
    return {loc.name for loc in world_test.multiworld.get_locations(world_test.player) if loc.address is not None}


class TestContractData(unittest.TestCase):
    def test_contract_ids_in_their_range(self) -> None:
        contracts = load_data()["contracts"]
        self.assertEqual(len(contracts), 201)
        for c in contracts:
            self.assertEqual(c["location_id"], LOCATION_BASE + 300_000 + c["index"])
        self.assertEqual(contracts[0]["name"], "Contract - Kappa")

    def test_written_contracts_are_well_formed(self) -> None:
        for seed in range(20):
            count = random.Random(seed).choice([40, 125, 200])
            contracts = write_contracts(random.Random(seed), count, "groundzero")
            self.assertEqual(len(contracts), count + 1)
            kappa = contracts[-1]
            self.assertEqual(kappa.index, 0)
            self.assertEqual(kappa.prereqs, list(range(1, count + 1)))
            self.assertTrue(kappa.objectives and all(o["type"] == "handover" for o in kappa.objectives))
            by_index = {c.index: c for c in contracts}
            for c in contracts[:-1]:
                self.assertTrue(1 <= len(c.objectives) <= 3, c.title)
                self.assertTrue(all(p < c.index for p in c.prereqs), c.title)
                for p in c.prereqs:
                    # A contract needs everything the contracts before it need.
                    earlier = by_index[p].requirement
                    self.assertTrue(earlier.clauses <= c.requirement.clauses or earlier.level <= c.requirement.level)
                    self.assertGreaterEqual(c.requirement.level, earlier.level)
                for o in c.objectives:
                    self.assertGreaterEqual(o["count"], 1)
                    if o["type"] == "handover":
                        self.assertIn(o["tpl"], LOOT)
                    elif o["type"] == "boss":
                        self.assertIn(o["role"], BOSS_ROLES)
                    elif o["type"] in ("kill", "extract"):
                        self.assertTrue(o.get("map") is None or o["map"] in ALL_MAPS)
            # The first contracts are open from the start and only use the starting map.
            for c in contracts[:3]:
                self.assertEqual(c.prereqs, [])
                for o in c.objectives:
                    self.assertIn(o.get("map"), (None, "groundzero"))
                    if o["type"] == "handover":
                        self.assertIn("groundzero", LOOT[o["tpl"]]["maps"])

    def test_standing_reaches_loyalty_four(self) -> None:
        for count in (40, 125, 200):
            contracts = write_contracts(random.Random(1), count, "customs")[:-1]
            totals = {}
            for c in contracts:
                totals[c.client] = totals.get(c.client, 0) + c.standing
            self.assertEqual(len(totals), 7)
            self.assertTrue(all(t >= 0.75 for t in totals.values()), (count, totals))


class TestContractsDefault(TarkovTestBase):
    def test_contracts_replace_quests(self) -> None:
        names = _names(self)
        self.assertIn("Contract - Kappa", names)
        self.assertIn("Contract 125", names)
        self.assertNotIn("Contract 126", names)
        quests = {q["location_name"] for q in load_data()["quests"] if q["check"]}
        self.assertFalse(names & quests)

    def test_slot_data(self) -> None:
        slot = self.world.fill_slot_data()
        self.assertEqual(slot["quest_mode"], "archipelago_contracts")
        self.assertEqual(slot["goal"], "kappa_contract")
        self.assertEqual(len(slot["contracts"]), 126)
        self.assertEqual(slot["contracts"][-1]["index"], 0)

    def test_start_has_contracts(self) -> None:
        state = self.multiworld.state
        for n in (1, 2, 3):
            self.assertTrue(self.multiworld.get_location(f"Contract {n}", self.player).can_reach(state))
        self.assertFalse(self.multiworld.get_location("Contract - Kappa", self.player).can_reach(state))

    def test_kappa_needs_what_the_line_needs(self) -> None:
        kappa = self.world.contracts[-1].requirement
        single = sorted(next(iter(c)) for c in kappa.clauses if len(c) == 1 and next(iter(c)) != self.world.start_map)
        self.assertTrue(single, "the whole line should need some map beyond the start")
        map_item = {m["key"]: m["item"] for m in load_data()["maps"]}[single[0]]
        self.collect_all_but([map_item])
        self.assertFalse(self.can_reach_location("Contract - Kappa"))
        self.collect_by_name(map_item)
        self.assertTrue(self.can_reach_location("Contract - Kappa"))
        self.assertBeatable(True)


class TestContractsShort(TarkovTestBase):
    options = {"contract_count": 40, "starting_map": "customs", "craftsanity": True}

    def test_count(self) -> None:
        names = _names(self)
        self.assertIn("Contract 40", names)
        self.assertNotIn("Contract 41", names)

    def test_no_quest_recipes(self) -> None:
        crafts = {c["name"]: c for c in load_data()["crafts"]}
        for name in _names(self) & set(crafts):
            self.assertFalse(crafts[name]["quest"], name)


class TestContractsLong(TarkovTestBase):
    options = {"contract_count": 200, "starting_map": "factory", "lootsanity": ["provisions"]}

    def test_kappa_rewarded_by_last(self) -> None:
        self.assertEqual(self.world.contracts[-1].title, "Kappa")
        self.assertEqual(KAPPA_TPL, "5c093ca986f7740a1867ab12")

import unittest

from ..data import load_data
from ..logic import WEAPON_PARTS_LEVEL
from .bases import TarkovTestBase

LOCATION_BASE = 76_400_000


def _craft_names(world_test) -> set:
    return {loc.name for loc in world_test.multiworld.get_locations(world_test.player)
            if loc.address is not None and loc.name.startswith("Craft - ")}


def _late_gunsmith(world_test):
    """A weapon-assembly quest check late enough to need parts the early traders don't sell."""
    checks = {q["location_name"] for q in world_test.world.quest_checks}
    for q in load_data()["quests"]:
        if (q.get("weapon_assembly") and int(q.get("min_level") or 0) >= WEAPON_PARTS_LEVEL
                and q["location_name"] in checks):
            return q["location_name"]
    return None


def _everything_but_flea() -> list:
    data = load_data()
    return [m["item"] for m in data["maps"]] + [t["name"] for t in data["traders"]]


class TestCraftData(unittest.TestCase):
    def test_ids(self) -> None:
        crafts = load_data()["crafts"]
        ids = [c["location_id"] for c in crafts]
        self.assertEqual(len(ids), len(set(ids)))
        for c in crafts:
            self.assertTrue(LOCATION_BASE + 200_000 <= c["location_id"] < LOCATION_BASE + 210_000, c["name"])

    def test_unlocking_quests_are_completable(self) -> None:
        quests = {q["id"]: q for q in load_data()["quests"]}
        for c in load_data()["crafts"]:
            if c["quest"]:
                self.assertIn(c["quest"], quests, c["name"])
                self.assertTrue(quests[c["quest"]]["possible"], c["name"])


class TestCraftsanityOff(TarkovTestBase):
    def test_no_crafts_by_default(self) -> None:
        self.assertFalse(_craft_names(self))
        data = self.world.fill_slot_data()
        self.assertFalse(data["craftsanity"])
        self.assertEqual(data["shop_randomizer"], "vanilla")


class TestCraftsanity(TarkovTestBase):
    options = {"quest_mode": "vanilla", "craftsanity": True}

    def test_every_recipe(self) -> None:
        self.assertEqual(_craft_names(self), {c["name"] for c in load_data()["crafts"]})
        self.assertTrue(self.world.fill_slot_data()["craftsanity"])

    def test_quest_unlocked_recipe_needs_its_quest(self) -> None:
        craft = next(c for c in load_data()["crafts"] if c["quest"])
        quest = next(q for q in load_data()["quests"] if q["id"] == craft["quest"])
        quest_req = self.world.requirements.quest(quest["id"])
        craft_req = self.world.craft_requirement(craft)
        self.assertTrue(quest_req.traders <= craft_req.traders, craft["name"])
        self.assertGreaterEqual(craft_req.level, quest_req.level, craft["name"])


class TestShopRandomizer(TarkovTestBase):
    options = {"shop_randomizer": "same_category", "shop_reshuffle": "every_restock", "lock_flea_market": True}

    def test_slot_data(self) -> None:
        data = self.world.fill_slot_data()
        self.assertEqual(data["shop_randomizer"], "same_category")
        self.assertEqual(data["shop_reshuffle"], "every_restock")
        self.assertEqual(data["shop_stock"], 40)
        self.assertIsInstance(data["shop_seed"], int)

    def test_late_gunsmith_needs_the_flea_market(self) -> None:
        name = _late_gunsmith(self)
        if name is None:
            self.skipTest("no late weapon-assembly quest check")
        self.collect_by_name(_everything_but_flea())
        self.assertFalse(self.can_reach_location(name), "randomized shops can't be counted on for weapon parts")
        self.collect_by_name(["Flea Market Access"])
        self.assertTrue(self.can_reach_location(name))


class TestVanillaShopsTradersSellParts(TarkovTestBase):
    options = {"lock_flea_market": True}

    def test_late_gunsmith_with_traders(self) -> None:
        name = _late_gunsmith(self)
        if name is None:
            self.skipTest("no late weapon-assembly quest check")
        self.collect_by_name(_everything_but_flea())
        self.assertTrue(self.can_reach_location(name))


class TestShopsAnythingWithCrafts(TarkovTestBase):
    options = {"shop_randomizer": "anything", "craftsanity": True, "lootsanity": ["provisions"]}

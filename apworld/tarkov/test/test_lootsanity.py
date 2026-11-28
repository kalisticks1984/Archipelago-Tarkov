import unittest

from ..data import load_data
from ..options import LOOT_CATEGORY_KEYS
from .bases import TarkovTestBase

LOCATION_BASE = 76_400_000


def _loot_names(world_test) -> set:
    return {loc.name for loc in world_test.multiworld.get_locations(world_test.player)
            if loc.address is not None and loc.name.startswith("Loot - ")}


class TestLootData(unittest.TestCase):
    def test_ids_in_their_range_and_unique(self) -> None:
        loot = load_data()["loot"]
        ids = [r["location_id"] for r in loot]
        self.assertEqual(len(ids), len(set(ids)))
        for r in loot:
            self.assertTrue(LOCATION_BASE + 100_000 <= r["location_id"] < LOCATION_BASE + 200_000, r["name"])

    def test_stand_ins_are_unique_mongo_ids_with_the_prefix(self) -> None:
        loot = load_data()["loot"]
        stand_ins = [r["stand_in"] for r in loot]
        self.assertEqual(len(stand_ins), len(set(stand_ins)))
        for s in stand_ins:
            self.assertEqual(len(s), 24)
            self.assertTrue(s.startswith("a9a9a9"))
            int(s, 16)

    def test_categories_are_option_keys(self) -> None:
        self.assertTrue({r["category"] for r in load_data()["loot"]} <= set(LOOT_CATEGORY_KEYS))

    def test_items_bots_use_are_never_found_only_on_bots(self) -> None:
        for r in load_data()["loot"]:
            if not r["bot_safe"]:
                self.assertTrue(r["maps"], r["name"])

    def test_spawn_maps_are_known(self) -> None:
        maps = {m["key"] for m in load_data()["maps"]}
        for r in load_data()["loot"]:
            self.assertTrue(set(r["maps"]) <= maps, r["name"])


class TestLootsanityOff(TarkovTestBase):
    def test_no_loot_checks_by_default(self) -> None:
        self.assertFalse(_loot_names(self))
        self.assertEqual(self.world.fill_slot_data()["lootsanity"], [])


class TestLootsanityKeysAndMeds(TarkovTestBase):
    options = {"lootsanity": ["keys", "medication"]}

    def test_only_chosen_categories(self) -> None:
        expected = {r["name"] for r in load_data()["loot"] if r["category"] in ("keys", "medication")}
        self.assertEqual(_loot_names(self), expected)
        self.assertEqual(self.world.fill_slot_data()["lootsanity"], ["keys", "medication"])

    def test_map_only_loot_needs_its_map(self) -> None:
        # An item that only spawns on maps you don't start with (default start: Ground Zero), and not only in The Lab.
        loot = next(r for r in load_data()["loot"]
                    if r["category"] in ("keys", "medication") and r["maps"]
                    and "groundzero" not in r["maps"] and "labs" not in r["maps"])
        map_item = next(m["item"] for m in load_data()["maps"] if m["key"] == loot["maps"][0])
        self.assertFalse(self.can_reach_location(loot["name"]))
        self.collect_by_name([map_item])
        self.assertTrue(self.can_reach_location(loot["name"]))

    def test_loot_checks_hold_no_progression_by_default(self) -> None:
        for loc in self.multiworld.get_locations(self.player):
            if loc.name.startswith("Loot - ") and loc.item is not None:
                self.assertFalse(loc.item.advancement, f"{loc.name} holds {loc.item.name}")


class TestLootsanityProgressionAllowed(TarkovTestBase):
    options = {"lootsanity": ["provisions"], "lootsanity_progression": True}

    def test_not_excluded(self) -> None:
        from BaseClasses import LocationProgressType
        for loc in self.multiworld.get_locations(self.player):
            if loc.name.startswith("Loot - "):
                self.assertNotEqual(loc.progress_type, LocationProgressType.EXCLUDED, loc.name)


class TestLootsanityLabOnlyNeedsKeycard(TarkovTestBase):
    options = {"lootsanity": ["keys"], "lock_flea_market": True}

    def test_lab_key(self) -> None:
        lab_only = [r for r in load_data()["loot"] if r["category"] == "keys" and r["maps"] == ["labs"]]
        if not lab_only:
            self.skipTest("no Lab-only key in this SPT version")
        name = lab_only[0]["name"]
        self.collect_by_name(["The Lab Access"])
        self.assertFalse(self.can_reach_location(name), "The Lab needs a keycard source first")


class TestLootsanityBigCategory(TarkovTestBase):
    # The largest category, to make sure thousands of loot checks generate and fill.
    options = {"lootsanity": ["weapon_mods"]}
    run_default_tests = False

    def test_count(self) -> None:
        self.assertEqual(len(_loot_names(self)),
                         sum(1 for r in load_data()["loot"] if r["category"] == "weapon_mods"))

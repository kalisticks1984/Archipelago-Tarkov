from ..data import load_data
from ..items import STATION_BASE_LEVELS, STATION_ITEMS, SUPPLIES_ITEM, item_table
from .bases import TarkovTestBase


def _pool_names(world_test) -> list:
    return [item.name for item in world_test.multiworld.itempool if item.player == world_test.player]


class TestStationData(TarkovTestBase):
    def test_one_item_per_level(self) -> None:
        hideout = load_data()["hideout"]
        for area, name in STATION_ITEMS.items():
            levels = [h["level"] for h in hideout if h["area"] == area]
            self.assertEqual(item_table[name].params["count"], len(levels), name)
            self.assertEqual(STATION_BASE_LEVELS[area], min(levels) - 1, name)
        self.assertEqual(set(STATION_ITEMS), {h["area"] for h in hideout})


class TestStationItems(TarkovTestBase):
    """Default: hideout checks on, hideout_levels items."""

    def test_pool_has_one_item_per_hideout_check(self) -> None:
        pool = _pool_names(self)
        for area, name in STATION_ITEMS.items():
            self.assertEqual(pool.count(name), sum(1 for h in self.world.hideout_checks if h["area"] == area), name)
        self.assertEqual(pool.count(SUPPLIES_ITEM), 10)
        data = self.world.fill_slot_data()
        self.assertEqual(data["hideout_levels"], "items")
        self.assertEqual(data["hideout_supplies"], 10)

    def test_level_rules(self) -> None:
        req = self.world.requirements.hideout_level(10, 2)
        self.assertIn((10, 1), req.stations)
        self.assertEqual(self.world.requirements.hideout_level(10, 1).stations, frozenset())
        # A prerequisite station must be working: Generator level 1 needs one Progressive Generator.
        h = next(h for h in load_data()["hideout"] if [4, 1] in h["prereq_areas"])
        self.assertIn((4, 1), self.world.requirements.hideout_level(h["area"], h["level"]).stations)


class TestStationRulesWithState(TarkovTestBase):
    options = {"quest_mode": "vanilla"}

    def test_workbench_two_needs_one_item(self) -> None:
        workbench = STATION_ITEMS[10]
        location = self.multiworld.get_location("Hideout - Workbench Level 2", self.player)
        everything = self.multiworld.get_all_state(False)
        self.assertTrue(location.can_reach(everything))
        while everything.has(workbench, self.player):
            item = next(i for i in self.multiworld.itempool if i.player == self.player and i.name == workbench
                        and everything.prog_items[self.player][workbench] > 0)
            everything.remove(item)
        self.assertFalse(location.can_reach(everything))


class TestBuiltLevels(TarkovTestBase):
    options = {"hideout_levels": "built", "hideout_supplies": 0}

    def test_no_station_items(self) -> None:
        pool = _pool_names(self)
        self.assertFalse(set(STATION_ITEMS.values()) & set(pool))
        self.assertNotIn(SUPPLIES_ITEM, pool)
        self.assertEqual(self.world.fill_slot_data()["hideout_levels"], "built")
        self.assertEqual(self.world.requirements.hideout_level(10, 2).stations, frozenset())


class TestNoHideoutChecks(TarkovTestBase):
    options = {"quest_mode": "vanilla", "hideout_checks": False}

    def test_items_mode_needs_hideout_checks(self) -> None:
        self.assertFalse(set(STATION_ITEMS.values()) & set(_pool_names(self)))
        self.assertEqual(self.world.fill_slot_data()["hideout_levels"], "built")


class TestServiceOptions(TarkovTestBase):
    options = {"insurance": False, "insurance_price": 250, "post_raid_healing": False, "trader_barters": True,
               "starting_kit": "replace", "kit_grace_raids": 3}

    def test_slot_data(self) -> None:
        data = self.world.fill_slot_data()
        self.assertFalse(data["insurance"])
        self.assertEqual(data["insurance_price"], 250)
        self.assertFalse(data["post_raid_healing"])
        self.assertTrue(data["trader_barters"])
        self.assertEqual(data["kit_grace_raids"], 3)


class TestServiceDefaults(TarkovTestBase):
    def test_defaults(self) -> None:
        data = self.world.fill_slot_data()
        self.assertTrue(data["insurance"])
        self.assertEqual(data["insurance_price"], 100)
        self.assertTrue(data["post_raid_healing"])
        self.assertFalse(data["trader_barters"])
        self.assertEqual(data["kit_grace_raids"], 5)

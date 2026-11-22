import unittest

from ..data import load_data
from .bases import TarkovTestBase

LOCATION_BASE = 76_400_000


def _names(world_test) -> set:
    return {loc.name for loc in world_test.multiworld.get_locations(world_test.player) if loc.address is not None}


class TestKillBossData(unittest.TestCase):
    def test_kill_ids_in_their_range(self) -> None:
        for k in load_data()["kills"]:
            self.assertTrue(LOCATION_BASE + 30_000 <= k["location_id"] < LOCATION_BASE + 40_000, k["name"])

    def test_boss_ids_in_their_range(self) -> None:
        for b in load_data()["bosses"]:
            self.assertTrue(LOCATION_BASE + 40_000 <= b["location_id"] < LOCATION_BASE + 50_000, b["name"])

    def test_every_boss_spawns_on_a_known_map(self) -> None:
        maps = {m["key"] for m in load_data()["maps"]}
        bosses = load_data()["bosses"]
        self.assertEqual(len(bosses), 13)
        for b in bosses:
            self.assertTrue(b["maps"], b["name"])
            self.assertTrue(set(b["maps"]) <= maps, b["name"])

    def test_kill_tiers_ascend(self) -> None:
        for target in ("scav", "pmc"):
            tiers = [k for k in load_data()["kills"] if k["target"] == target]
            self.assertEqual([k["count"] for k in tiers], sorted(k["count"] for k in tiers))
            self.assertEqual([k["level"] for k in tiers], sorted(k["level"] for k in tiers))


class TestDefaultKillsAndBosses(TarkovTestBase):
    def test_short_milestones_and_all_bosses(self) -> None:
        names = _names(self)
        kills = {n for n in names if n.startswith("Kills - ")}
        self.assertEqual(len(kills), 8)
        self.assertIn("Kills - 50 Scavs", kills)
        self.assertNotIn("Kills - 75 Scavs", kills)
        self.assertIn("Kills - 25 PMCs", kills)
        self.assertNotIn("Kills - 50 PMCs", kills)
        self.assertEqual(len({n for n in names if n.startswith("Boss - ")}), 13)

    def test_slot_data(self) -> None:
        data = self.world.fill_slot_data()
        self.assertEqual(data["kill_milestones"], "short")
        self.assertTrue(data["boss_checks"])

    def test_boss_needs_a_spawn_map(self) -> None:
        # Default start is Ground Zero: Reshala needs Customs, Tagilla needs Factory.
        self.assertFalse(self.can_reach_location("Boss - Reshala"))
        self.collect_by_name(["Customs Access", "Factory Access"])
        self.assertTrue(self.can_reach_location("Boss - Reshala"))

    def test_goons_on_any_of_their_maps(self) -> None:
        self.assertFalse(self.can_reach_location("Boss - Knight"))
        self.collect_by_name(["Woods Access"])
        self.assertTrue(self.can_reach_location("Boss - Knight"))

    def test_low_kill_tier_reachable_at_start(self) -> None:
        self.assertTrue(self.can_reach_location("Kills - 5 Scavs"))
        self.assertTrue(self.can_reach_location("Kills - 1 PMCs"))


class TestLongKillMilestones(TarkovTestBase):
    options = {"kill_milestones": "long"}

    def test_all_tiers(self) -> None:
        kills = {n for n in _names(self) if n.startswith("Kills - ")}
        self.assertEqual(len(kills), len(load_data()["kills"]))

    def test_high_tier_needs_progression(self) -> None:
        self.assertFalse(self.can_reach_location("Kills - 300 Scavs"))
        self.assertFalse(self.can_reach_location("Kills - 100 PMCs"))


class TestKillsAndBossesOff(TarkovTestBase):
    options = {"kill_milestones": "off", "boss_checks": False}

    def test_none_present(self) -> None:
        names = _names(self)
        self.assertFalse({n for n in names if n.startswith(("Kills - ", "Boss - "))})


class TestBossesGoal(TarkovTestBase):
    options = {"goal": "bosses", "starting_map": "customs"}

    def test_victory_needs_every_boss_map(self) -> None:
        self.assertBeatable(False)
        self.collect_by_name(["Interchange Access", "Woods Access", "Reserve Access", "Shoreline Access",
                              "Factory Access", "Streets of Tarkov Access"])
        self.assertBeatable(False)  # Zryachiy is only on Lighthouse
        self.collect_by_name(["Lighthouse Access"])
        self.assertBeatable(True)

    def test_slot_data(self) -> None:
        self.assertEqual(self.world.fill_slot_data()["goal"], "bosses")


class TestBossesGoalWithoutBossChecks(TarkovTestBase):
    # The goal tracks boss kills on its own; it must not depend on the checks being in the seed.
    options = {"goal": "bosses", "boss_checks": False, "kill_milestones": "off"}

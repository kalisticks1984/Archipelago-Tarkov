from dataclasses import dataclass

from Options import Choice, DeathLink, DefaultOnToggle, OptionSet, PerGameCommonOptions, Range, Toggle


class Goal(Choice):
    """What you need to do to finish your game. With Quest Mode archipelago_contracts the goal is always the
    Kappa contract and this option is ignored.

    quest_count: complete a number of quests that are checks (see Goal Quest Count).
    collector: complete Fence's "Collector" quest (the Kappa container).
    all_extracts: survive and extract at least once from every map.
    bosses: kill every boss at least once (Reshala, Killa, Shturman, Glukhar, Sanitar, Tagilla, Kaban, Kollontay,
    Zryachiy, Partisan and the three Goons)."""
    display_name = "Goal"
    option_quest_count = 0
    option_collector = 1
    option_all_extracts = 2
    option_bosses = 3
    default = 0


class QuestMode(Choice):
    """Which quests you play.

    archipelago_contracts: Tarkov's own quests are switched off. The Archipelago trader offers a line of contracts
    written for your seed (kill, survive, boss and hand-in jobs), ordered so the items you can receive make each one
    possible. Every contract is a check and gives XP, roubles and standing with a main trader. Every other trader is
    locked: the Archipelago trader sells the stock of each main trader you have received (at the loyalty your level
    and standing give), plus a catalog of weapons, armor, meds, ammo and keys rolled every 15 minutes, in tiers
    that open as you finish contracts. The last contract,
    "Kappa", needs every other contract done and rewards the Kappa container: finishing it is your goal, whatever
    Goal is set to.
    vanilla: Tarkov's own quests are checks (see Quest Traders and Max Quest Level) and Goal applies."""
    display_name = "Quest Mode"
    option_archipelago_contracts = 0
    option_vanilla = 1
    default = 0


class ContractCount(Range):
    """How many contracts come before the Kappa contract (Archipelago contracts only).
    The vanilla road to Kappa is 254 quests; the default is about half of that."""
    display_name = "Contract Count"
    range_start = 40
    range_end = 200
    default = 125


class GoalQuestCount(Range):
    """How many check quests must be completed for the Quest Count goal.
    If fewer quest checks exist with your other settings, the goal uses all of them."""
    display_name = "Goal Quest Count"
    range_start = 5
    range_end = 372
    default = 60


class StartingMap(Choice):
    """The map you can raid on from the start. Every other map is an item.
    Ground Zero matches the vanilla new-player experience; most early Prapor quests start there.
    The Lab is not offered because it needs a keycard to enter."""
    display_name = "Starting Map"
    option_ground_zero = 0
    option_factory = 1
    option_customs = 2
    option_woods = 3
    option_interchange = 4
    option_shoreline = 5
    option_reserve = 6
    option_lighthouse = 7
    option_streets_of_tarkov = 8
    default = 0


class StartingTraders(Choice):
    """Which traders are available from the start. Every other main trader is an item.

    prapor_and_therapist: the two traders new players lean on the most.
    random_two: two random traders out of Prapor, Therapist, Skier, Peacekeeper, Mechanic, Ragman and Jaeger.
    random_one: one random trader."""
    display_name = "Starting Traders"
    option_prapor_and_therapist = 0
    option_random_two = 1
    option_random_one = 2
    default = 0


class QuestTraders(OptionSet):
    """Traders whose quests are checks. Quests from traders left out still work as normal in game,
    they just don't send anything."""
    display_name = "Quest Traders"
    valid_keys = frozenset({"Prapor", "Therapist", "Skier", "Peacekeeper", "Mechanic", "Ragman", "Jaeger"})
    default = frozenset({"Prapor", "Therapist", "Skier", "Peacekeeper", "Mechanic", "Ragman", "Jaeger"})


class MaxQuestLevel(Range):
    """Quests that need a higher player level than this to unlock are not checks.
    Lower values make shorter games."""
    display_name = "Max Quest Level"
    range_start = 1
    range_end = 79
    default = 79


class HideoutChecks(DefaultOnToggle):
    """Building each hideout area level is a check."""
    display_name = "Hideout Checks"


class ExtractsPerMap(Range):
    """How many successful PMC extractions on each map are checks.
    For example 2 means the first and second time you survive on Customs both send a check."""
    display_name = "Extracts Per Map"
    range_start = 0
    range_end = 5
    default = 1


class KillMilestones(Choice):
    """Checks for reaching cumulative kill totals. Kills in PMC and Scav raids both count, on unlocked maps.
    Scavs are any Scav-side AI (including bosses, Raiders and Rogues, like the game's own quest counters);
    PMCs are USEC and BEAR.

    off: no kill checks.
    short: 5 to 50 Scav kills and 1 to 25 PMC kills (8 checks).
    long: 5 to 300 Scav kills and 1 to 100 PMC kills (17 checks)."""
    display_name = "Kill Milestones"
    option_off = 0
    option_short = 1
    option_long = 2
    default = 1


SHORT_KILL_LIMITS = {"scav": 50, "pmc": 25}


class BossChecks(DefaultOnToggle):
    """Killing each boss for the first time is a check (13 checks: the Goons count as three).
    A boss is in logic once you can raid any map it spawns on; spawns are random, so some take patience."""
    display_name = "Boss Checks"


class LockFleaMarket(DefaultOnToggle):
    """The Flea Market stays banned until you find "Flea Market Access"."""
    display_name = "Lock Flea Market"


LOOT_CATEGORY_KEYS = ("barter", "keys", "medication", "provisions", "ammo", "weapons", "weapon_mods", "gear", "info",
                      "maps", "special_equipment", "money")


class Lootsanity(OptionSet):
    """Item categories whose item types are checks. The first time in a raid you find an item of a type that is
    still a check, it is an "Archipelago Item" instead (one per type per raid; the rest of that type is normal loot).
    Extract with it to send the check; you never get the original item. Dying keeps what is in your secure container,
    as usual. Leave empty to turn lootsanity off.

    Approximate number of item types (checks) per category: barter 229, keys 202, medication 42, provisions 41,
    ammo 387, weapons 162, weapon_mods 1738, gear 351, info 25, maps 6, special_equipment 11, money 3.
    Every check adds an item to the multiworld, mostly filler. The optional client plugin shows these items with the
    Archipelago symbol."""
    display_name = "Lootsanity"
    valid_keys = frozenset(LOOT_CATEGORY_KEYS)
    default = frozenset()


class LootsanityProgression(Toggle):
    """Allow loot checks to hold progression items (yours or other players').
    Off: loot checks only hold filler. Some item types are very rare spawns and the logic can't know the odds,
    so with this on a whole multiworld could wait on one unlucky spawn."""
    display_name = "Lootsanity Progression"


class Craftsanity(Toggle):
    """The first time you finish each hideout recipe is a check (about 180 recipes). That first craft gives no item,
    only the check; every later craft of the recipe is normal. Recipes are in logic once their station level can be
    built and any quest that unlocks them can be done."""
    display_name = "Craftsanity"


class ShopRandomizer(Choice):
    """Randomize what the seven main traders sell. Shop items are never checks.

    vanilla: normal trader stock.
    same_category: each offer becomes a random item from the same handbook category (ammo stays ammo).
    anything: each offer becomes any item a trader sells, or a loose item such as a key, barter item or meds.
    Each offer becomes an item worth about the same as the one it replaces, so early loyalty levels stay modest.
    Prices follow the item's value; purchase limits are random, smaller for valuable items and larger at higher
    loyalty levels. The Lab keycard and Peacekeeper's/Skier's currency exchange are never changed."""
    display_name = "Shop Randomizer"
    option_vanilla = 0
    option_same_category = 1
    option_anything = 2
    default = 0


class ShopStock(Range):
    """With randomized shops: the percentage of each trader's offers that are in stock (the rest are removed).
    The Lab keycard and currency exchange are always kept."""
    display_name = "Shop Stock"
    range_start = 10
    range_end = 100
    default = 40


class ShopReshuffle(Choice):
    """When randomized shops change.

    per_seed: each trader's random stock is decided once and stays the same for the whole game.
    every_restock: new random stock every time the trader restocks."""
    display_name = "Shop Reshuffle"
    option_per_seed = 0
    option_every_restock = 1
    default = 0


class LevelPacing(Choice):
    """How the logic estimates the player level you can reach.
    Logic assumes you need more maps unlocked before high level quests and loyalty levels are expected of you.

    relaxed: one extra map is expected for every 8 player levels.
    normal: one extra map for every 10 levels.
    fast: one extra map for every 15 levels."""
    display_name = "Level Pacing"
    option_relaxed = 8
    option_normal = 10
    option_fast = 15
    default = 10


class TrapPercentage(Range):
    """Percentage of filler items replaced by traps (Fence Grudge, Exhaustion, Scav Cooldown)."""
    display_name = "Trap Percentage"
    range_start = 0
    range_end = 50
    default = 10


class StartingKit(Choice):
    """Give your PMC a random starting kit, rolled from the seed: a weapon with magazines and matching ammo,
    body armor, a rig, a backpack, meds, food and money (see Starting Kit Quality).

    off: the normal starting stash of your SPT profile's edition.
    extra: the kit arrives as a message on top of the normal starting stash.
    replace: your starting stash and gear are removed and the kit is all you start with (the knife, pockets and
    secure container stay). Only applies to a brand-new profile; an older profile gets the kit on top instead."""
    display_name = "Starting Kit"
    option_off = 0
    option_extra = 1
    option_replace = 2
    default = 0


class StartingKitQuality(Choice):
    """How good the random starting kit is.

    scrappy: a cheap gun, light armor, little money.
    standard: a mid-range gun, class 3 armor and a helmet now and then.
    geared: a good gun with better ammo, class 4 armor, a helmet and more money."""
    display_name = "Starting Kit Quality"
    option_scrappy = 0
    option_standard = 1
    option_geared = 2
    default = 1


class TarkovDeathLink(DeathLink):
    """When your PMC dies in raid, everyone with DeathLink dies too, and the reverse.
    Receiving a death while in raid needs the optional client plugin; it is ignored outside raids."""


@dataclass
class TarkovOptions(PerGameCommonOptions):
    goal: Goal
    goal_quest_count: GoalQuestCount
    quest_mode: QuestMode
    contract_count: ContractCount
    starting_map: StartingMap
    starting_traders: StartingTraders
    quest_traders: QuestTraders
    max_quest_level: MaxQuestLevel
    hideout_checks: HideoutChecks
    extracts_per_map: ExtractsPerMap
    kill_milestones: KillMilestones
    boss_checks: BossChecks
    lootsanity: Lootsanity
    lootsanity_progression: LootsanityProgression
    craftsanity: Craftsanity
    shop_randomizer: ShopRandomizer
    shop_reshuffle: ShopReshuffle
    shop_stock: ShopStock
    lock_flea_market: LockFleaMarket
    level_pacing: LevelPacing
    trap_percentage: TrapPercentage
    starting_kit: StartingKit
    starting_kit_quality: StartingKitQuality
    death_link: TarkovDeathLink

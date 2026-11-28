using ArchipelagoTarkov.Data;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Eft.Match;

namespace ArchipelagoTarkov.Session;

/// <summary>
/// Insurance, healing, barters, kit grace raids and the hideout's progressive station items and supplies.
/// Seeds made before these options existed keep the old behaviour (every fallback below is the old one).
/// </summary>
public sealed partial class ApSession
{
    /// <summary>Hideout area type -> how many of its progressive station items have arrived.</summary>
    private readonly Dictionary<int, int> _stationItems = new();

    // ------------------------------------------------------------------ options
    public bool InsuranceOn => !Active || State.SlotFlag("insurance", true);
    public double InsurancePriceFactor => Active ? Math.Max(0, State.SlotInt("insurance_price", 100)) / 100.0 : 1.0;
    public bool HealingOn => !Active || State.SlotFlag("post_raid_healing", true);
    public bool BartersRemoved => Active && !State.SlotFlag("trader_barters", true);
    public bool StationItemsMode => Active && State.SlotString("hideout_levels") == "items";

    private int KitGraceRaids =>
        Active && State.SlotString("starting_kit") == "replace" ? Math.Max(0, State.SlotInt("kit_grace_raids")) : 0;

    // ------------------------------------------------------------------ kit grace raids
    /// <summary>Set at a grace-raid death; the next equipment removal SPT does for this profile is skipped.</summary>
    public bool KeepGearOnce { get; set; }

    /// <summary>
    /// Raid end, before SPT handles it: with Starting Kit replace, a death in one of the first Kit Grace Raids PMC raids
    /// keeps the gear. Returns true when this raid is one of them.
    /// </summary>
    public bool UseKitGrace(bool isPmc, EndRaidResult? results)
    {
        KeepGearOnce = false;
        if (!isPmc || results?.Result is null || !results.IsPlayerDead())
        {
            return false;
        }

        var grace = KitGraceRaids;
        if (State.PmcRaids >= grace)
        {
            return false;
        }

        KeepGearOnce = true;
        var left = grace - State.PmcRaids - 1;
        Announce(left > 0
            ? $"Grace raid {State.PmcRaids + 1} of {grace}: you keep your gear. {left} more grace raid{(left == 1 ? "" : "s")} left."
            : $"Grace raid {grace} of {grace}: you keep your gear. That was the last one: from now on a death costs your gear.");
        return true;
    }

    // ------------------------------------------------------------------ hideout: progressive stations
    private ItemDef? StationDef(int area) =>
        _data.ItemsById.Values.FirstOrDefault(d => d.Kind == "station" && (int)d.Num("area") == area);

    private static string StationName(ItemDef def) => def.Name.StartsWith("Progressive ") ? def.Name["Progressive ".Length..] : def.Name;

    /// <summary>The highest level of the station its received items allow.</summary>
    public int StationAllowed(int area)
    {
        var def = StationDef(area);
        return def is null ? int.MaxValue : (int)def.Num("base_level") + _stationItems.GetValueOrDefault(area);
    }

    /// <summary>
    /// A hideout upgrade finished. Records it as built (its check goes out with the others). Returns false when the
    /// station's items don't allow the level yet: the mod applies it once they arrive.
    /// </summary>
    public bool AllowUpgradeComplete(int area, int level)
    {
        if (!StationItemsMode || !_data.HideoutLocations.ContainsKey((area, level)))
        {
            return true;
        }

        if (State.HideoutBuilt.GetValueOrDefault(area) < level)
        {
            State.HideoutBuilt[area] = level;
            _dirty = true;
            _profileChanged = true;
        }

        if (level <= StationAllowed(area))
        {
            return true;
        }

        var def = StationDef(area);
        var name = def is null ? $"Area {area}" : StationName(def);
        var needed = level - (int)(def?.Num("base_level") ?? 0);
        Announce($"Built {name} level {level}: check sent. It starts working when you have {needed} Progressive {name} " +
                 $"(you have {_stationItems.GetValueOrDefault(area)}). Until then the station stays at level {level - 1}.");
        return false;
    }

    /// <summary>Refuses paying again for a level that is built and waiting for its items. Null when allowed.</summary>
    public string? BlockUpgradeStart(int area, int level)
    {
        if (!StationItemsMode || State.HideoutBuilt.GetValueOrDefault(area) < level)
        {
            return null;
        }

        var def = StationDef(area);
        var name = def is null ? $"Area {area}" : StationName(def);
        return $"You already built {name} level {level}. It starts working when the next Progressive {name} arrives.";
    }

    private void ReceiveStation(ItemDef def, string from, bool startingItem)
    {
        var area = (int)def.Num("area");
        var name = StationName(def);
        var have = _stationItems.GetValueOrDefault(area);
        var level = (int)def.Num("base_level") + have;
        if (startingItem)
        {
            return;
        }

        var built = State.HideoutBuilt.GetValueOrDefault(area);
        Announce(built >= level
            ? $"Received {def.Name} {from}. {name} level {level} starts working (the hideout shows it after a game restart)."
            : $"Received {def.Name} {from}. Build {name} level {level} and it works right away.");
    }

    /// <summary>Apply every built level whose items have arrived (outside raids).</summary>
    private void ApplyStationLevels()
    {
        if (!StationItemsMode || State.HideoutBuilt.Count == 0)
        {
            return;
        }

        foreach (var (area, built) in State.HideoutBuilt.ToList())
        {
            var target = Math.Min(built, StationAllowed(area));
            var applied = _game.ApplyHideoutLevels(ProfileId, area, target);
            if (applied > 0)
            {
                _profileChanged = true;
            }
        }
    }

    // ------------------------------------------------------------------ hideout supplies
    private void SendSupplies(ItemDef def, string from)
    {
        var pmc = _game.Pmc(ProfileId);
        if (pmc is null)
        {
            throw new InvalidOperationException("the profile is not loaded");
        }

        var items = _game.CreateHideoutSupplies(pmc, State, StationItemsMode, out var upgrade, out var description);
        if (upgrade is null)
        {
            // Everything is built: money instead.
            items = _game.CreateMoney("5449016a4bdc2d6f028b456f", 100_000);
            description = "every hideout upgrade is built, so 100,000 roubles instead";
        }
        else
        {
            State.SuppliedUpgrades.Add(upgrade);
            _dirty = true;
        }

        _game.SendItems(ProfileId, $"{def.Name} {from}: {description}", items);
        _chatLines.Add($"Received {def.Name} {from}: {description}. Collect it in your System messages.");
        EnqueueClientMessage($"Received {def.Name} (System messages)");
    }
}

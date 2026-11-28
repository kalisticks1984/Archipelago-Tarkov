using ArchipelagoTarkov.Data;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Spt.Mod;
using SPTarkov.Server.Core.Services.Modding.Custom;

namespace ArchipelagoTarkov.Game;

/// <summary>
/// Lootsanity. Every lootable item type can be a check: the first item of a still-unchecked type that spawns in a
/// raid is replaced by an "Archipelago Item" stand-in (its own template per type, so the mod knows which check it
/// is). Bringing a stand-in out of the raid sends the check; the stand-in is then removed, so the player never gets
/// the original item or keeps the stand-in.
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.Preload + 1)]
public class LootStandIns(ISptLogger<LootStandIns> logger, CustomItemService customItems) : IOnLoad
{
    /// <summary>
    /// True once every stand-in template is in SPT's item database. Nothing is swapped before that: a client handed an
    /// item template it doesn't know can fail to load the raid.
    /// </summary>
    public static bool Ready { get; private set; }

    /// <summary>SPT only accepts new item templates before profiles load, so this runs early (Preload).</summary>
    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            Register(TarkovData.LoadEmbedded());
        }
        catch (Exception ex)
        {
            logger.Error($"[Archipelago] lootsanity is unavailable: {ex.Message}");
        }

        return Task.CompletedTask;
    }

    /// <summary>A plain 1x1 barter item (Bolts) the stand-ins are cloned from: its model is what lies on the ground.</summary>
    private const string BaseTemplate = "57347c5b245977448d35f6e1";
    private const string BaseParent = "57864ada245977548638de91";
    private const string HandbookParent = "5b47574386f77428ca22b2ee";

    private static readonly HashSet<string> BotLootSlots = ["TacticalVest", "Pockets", "Backpack"];

    /// <summary>Adds one stand-in template per lootable type to SPT's item database (they are tiny and never sold).</summary>
    public void Register(TarkovData data)
    {
        var created = 0;
        foreach (var loot in data.Loot)
        {
            try
            {
                var result = customItems.CreateItemFromClone(new NewItemFromCloneDetails
                {
                    ItemTplToClone = new MongoId(BaseTemplate),
                    ParentId = new MongoId(BaseParent),
                    NewId = new MongoId(loot.StandIn),
                    NewItemName = "archipelago_item",
                    OverrideProperties = new TemplateItemProperties
                    {
                        ExaminedByDefault = true,
                        CanSellOnRagfair = false,
                        CanRequireOnRagfair = false,
                        InsuranceDisabled = true,
                        IsUnsaleable = true,
                        IsUnbuyable = true,
                        QuestItem = false,
                        BackgroundColor = "violet",
                        Weight = 0.05,
                        StackMaxSize = 1,
                    },
                    AddToHandbook = true,
                    HandbookParentId = HandbookParent,
                    HandbookPriceRoubles = 1,
                    FleaPriceRoubles = 1,
                    AddToFleaPriceDb = false,
                    Locales = new Dictionary<string, LocaleDetails>
                    {
                        ["en"] = new()
                        {
                            Name = $"Archipelago Item ({loot.ItemName})",
                            ShortName = "AP",
                            Description = $"An Archipelago check. Extract with it to send the check for {loot.ItemName}. " +
                                          "You get the Archipelago item it holds, not the original. It is lost if you die " +
                                          "with it outside your secure container.",
                        },
                    },
                }, typeof(LootStandIns).Assembly);
                if (result.Success)
                {
                    created++;
                }
                else
                {
                    logger.Warning($"[Archipelago] stand-in for {loot.ItemName}: {string.Join("; ", result.Errors ?? [])}");
                }
            }
            catch (Exception ex)
            {
                logger.Warning($"[Archipelago] stand-in for {loot.ItemName} failed: {ex.Message}");
            }
        }

        Ready = created == data.Loot.Count && created > 0;
        if (Ready)
        {
            logger.Info($"[Archipelago] lootsanity: {created} Archipelago Item templates ready.");
        }
        else
        {
            logger.Error($"[Archipelago] lootsanity: only {created}/{data.Loot.Count} Archipelago Item templates could be " +
                         "added; lootsanity is switched off (no item will be replaced).");
        }
    }

    /// <summary>
    /// Returns a copy of the map's loot where the first spawn of each targeted type (in random order) is a stand-in.
    /// The original lists are never edited: co-op mods can share them between players.
    /// </summary>
    public static List<SpawnpointTemplate> SwapLoot(IEnumerable<SpawnpointTemplate> loot, Func<string, LootDef?> target,
        ISet<string> placed, Random rng, Action<LootDef, string>? onPlaced = null, Func<string, string>? nameOf = null)
    {
        var spawnpoints = loot.ToList();
        var order = Enumerable.Range(0, spawnpoints.Count).OrderBy(_ => rng.Next()).ToList();
        foreach (var index in order)
        {
            var sp = spawnpoints[index];
            var items = sp.Items?.ToList();
            if (items is null || items.Count == 0)
            {
                continue;
            }

            var root = sp.Root ?? items.FirstOrDefault(i => string.IsNullOrEmpty(i.ParentId))?.Id.ToString();
            // A container's loot is its direct contents; a loose spawn's loot is its root item.
            var candidates = sp.IsContainer == true
                ? items.Where(i => i.ParentId == root).ToList()
                : items.Where(i => i.Id.ToString() == root).ToList();
            var changed = false;
            foreach (var item in candidates.OrderBy(_ => rng.Next()))
            {
                var def = target(item.Template.ToString());
                if (def is null || !placed.Add(def.Tpl))
                {
                    continue;
                }

                ReplaceWithStandIn(items, item, def);
                changed = true;
                if (onPlaced != null)
                {
                    var container = sp.IsContainer == true ? items.FirstOrDefault(i => i.Id.ToString() == root) : null;
                    onPlaced(def, container is null
                        ? "on the ground"
                        : $"in a {nameOf?.Invoke(container.Template.ToString()) ?? "container"}");
                }
            }

            if (changed)
            {
                spawnpoints[index] = sp with { Items = items };
            }
        }

        return spawnpoints;
    }

    /// <summary>Swaps the first carried item of each targeted type in a bot's rig, pockets and backpack.</summary>
    public static void SwapBotLoot(BotBase bot, Func<string, LootDef?> target, ISet<string> placed,
        Action<LootDef, string>? onPlaced = null, string carrier = "a bot")
    {
        var inventory = bot.Inventory;
        if (inventory?.Items is not { Count: > 0 } items || inventory.Equipment is not { } equipment)
        {
            return;
        }

        var equipmentId = equipment.ToString();
        var containers = items.Where(i => i.ParentId == equipmentId && i.SlotId != null && BotLootSlots.Contains(i.SlotId))
            .Select(i => i.Id.ToString()).ToHashSet();
        foreach (var item in items.Where(i => i.ParentId != null && containers.Contains(i.ParentId)).ToList())
        {
            var def = target(item.Template.ToString());
            if (def is null || !def.BotSafe || !placed.Add(def.Tpl))
            {
                continue;
            }

            ReplaceWithStandIn(items, item, def);
            onPlaced?.Invoke(def, $"carried by {carrier}");
        }
    }

    /// <summary>
    /// Takes every stand-in out of a profile at raid end and returns the checks they count for: all of them when the
    /// player survived, only those in the secure container otherwise (the rest is lost, as in Tarkov).
    /// </summary>
    public static List<LootDef> TakeStandIns(BotBaseInventory? inventory, TarkovData data, bool survived)
    {
        var counted = new List<LootDef>();
        if (inventory?.Items is not { Count: > 0 } items)
        {
            return counted;
        }

        var standIns = items.Where(i => data.LootByStandIn.ContainsKey(i.Template.ToString())).ToList();
        if (standIns.Count == 0)
        {
            return counted;
        }

        var parentOf = items.GroupBy(i => i.Id.ToString()).ToDictionary(g => g.Key, g => g.First().ParentId);
        var equipmentId = inventory.Equipment?.ToString();
        var secure = items.Where(i => i.ParentId == equipmentId && i.SlotId == "SecuredContainer")
            .Select(i => i.Id.ToString()).ToHashSet();

        foreach (var standIn in standIns)
        {
            if (survived || IsInside(standIn.Id.ToString(), secure, parentOf))
            {
                counted.Add(data.LootByStandIn[standIn.Template.ToString()]);
            }

            RemoveWithChildren(items, standIn);
        }

        return counted;
    }

    private static bool IsInside(string id, HashSet<string> containers, Dictionary<string, string?> parentOf)
    {
        for (var hops = 0; hops < 50 && parentOf.TryGetValue(id, out var parent) && parent != null; hops++)
        {
            if (containers.Contains(parent))
            {
                return true;
            }

            id = parent;
        }

        return false;
    }

    private static void ReplaceWithStandIn<T>(List<T> items, T item, LootDef def) where T : Item
    {
        RemoveChildren(items, item.Id.ToString());
        var index = items.IndexOf(item);
        // A 1x1 single item: keep the place (and grid position) of the original, drop its stack and contents.
        // Records clone their runtime type, so a loose-loot item stays an SptLootItem.
        items[index] = (T)(item with
        {
            Template = new MongoId(def.StandIn),
            Upd = new Upd { StackObjectsCount = 1, SpawnedInSession = true },
        });
    }

    private static void RemoveWithChildren(List<Item> items, Item item)
    {
        RemoveChildren(items, item.Id.ToString());
        items.Remove(item);
    }

    private static void RemoveChildren<T>(List<T> items, string parentId) where T : Item
    {
        var doomed = new HashSet<string> { parentId };
        bool grew;
        do
        {
            grew = false;
            foreach (var i in items)
            {
                if (i.ParentId != null && doomed.Contains(i.ParentId) && doomed.Add(i.Id.ToString()))
                {
                    grew = true;
                }
            }
        } while (grew);

        doomed.Remove(parentId);
        items.RemoveAll(i => doomed.Contains(i.Id.ToString()));
    }
}

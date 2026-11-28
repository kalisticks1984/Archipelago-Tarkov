using System.Security.Cryptography;
using System.Text;
using ArchipelagoTarkov.Data;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Extensions;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace ArchipelagoTarkov.Game;

/// <summary>
/// Randomizes what the seven main traders sell, per profile. Shop items are never checks.
///
/// Every offer keeps its id, loyalty level and quest unlock; only its item, price and purchase limit change. Items
/// come from a pool of real trader offers (so weapons keep a working preset and armor its plates), plus loose items
/// in "anything" mode. The result depends only on the seed, the trader, the offer and (when reshuffling) the trader's
/// restock time, so the shop the client sees and the shop a purchase is checked against are always the same.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class ShopRandomizer(ISptLogger<ShopRandomizer> logger, TradersTable traders, TemplateTable templates,
    HandbookHelper handbook)
{
    private const string LabKeycard = "5c94bbff86f7747ee735c08f";
    private const string Hideout = "hideout";

    private static readonly Dictionary<string, string> TopCategories = new()
    {
        ["5b47574386f77428ca22b33e"] = "barter", ["5b47574386f77428ca22b342"] = "keys",
        ["5b47574386f77428ca22b344"] = "medication", ["5b47574386f77428ca22b340"] = "provisions",
        ["5b47574386f77428ca22b346"] = "ammo", ["5b5f78dc86f77409407a7f8e"] = "weapons",
        ["5b5f71a686f77447ed5636ab"] = "weapon_mods", ["5b47574386f77428ca22b33f"] = "gear",
        ["5b47574386f77428ca22b341"] = "info", ["5b47574386f77428ca22b343"] = "maps",
        ["5b47574386f77428ca22b345"] = "special_equipment", ["5b5f78b786f77447ed5636af"] = "money",
    };

    /// <summary>Categories whose single loose items join the pool in "anything" mode (no parts or contents needed).</summary>
    private static readonly HashSet<string> LooseCategories =
        ["barter", "keys", "medication", "provisions", "info", "maps", "special_equipment"];

    private static readonly HashSet<string> Currencies =
        ["5449016a4bdc2d6f028b456f", "5696686a4bdc2da3298b456a", "569668774bdc2da2298b4568"];

    private sealed record PoolEntry(string Category, List<Item> Tree, double PriceRoubles, bool LooseAmmo);

    private readonly object _lock = new();
    private List<PoolEntry>? _traderPool;
    private List<PoolEntry>? _anythingPool;
    private Dictionary<string, string>? _categoryOf;

    /// <summary>Returns a randomized copy of a trader's assort; the original is never changed.</summary>
    public TraderAssort Randomize(TraderAssort assort, string traderId, string mode, bool everyRestock, int seed,
        int stockPercent, TarkovData data)
    {
        EnsurePool(data);
        var currency = traders.GetTrader(new MongoId(traderId))?.Base?.Currency ?? CurrencyType.RUB;
        var currencyTpl = currency.GetCurrencyTpl();
        var restock = everyRestock ? (long)(assort.NextResupply ?? 0) : 0;
        var pool = mode == "anything" ? _anythingPool! : _traderPool!;
        stockPercent = Math.Clamp(stockPercent, 1, 100);

        var childrenOf = assort.Items.Where(i => i.ParentId != null).GroupBy(i => i.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToList());
        var items = new List<Item>();
        var schemes = new Dictionary<MongoId, List<List<BarterScheme>>>();
        var loyalty = new Dictionary<MongoId, int>(assort.LoyalLevelItems ?? new());

        foreach (var root in assort.Items.Where(i => i.ParentId == Hideout))
        {
            var tpl = root.Template.ToString();
            var level = loyalty.GetValueOrDefault(root.Id, 1);
            PoolEntry? pick = null;
            if (!Currencies.Contains(tpl) && tpl != LabKeycard)
            {
                var rng = new Random(StableSeed(seed, traderId, root.Id.ToString(), restock));
                if (rng.NextDouble() * 100 >= stockPercent)
                {
                    loyalty.Remove(root.Id); // out of stock in this shop
                    continue;
                }

                var category = _categoryOf!.GetValueOrDefault(tpl);
                var candidates = mode == "same_category" && category != null
                    ? _byCategory!.GetValueOrDefault(category)
                    : pool;
                if (candidates is { Count: > 0 })
                {
                    // About as valuable as the offer it replaces, so loyalty level 1 stays modest.
                    var value = handbook.GetTemplatePriceForItems(WithDescendants(root, childrenOf).ToList());
                    pick = PickByValue(candidates, value, rng);
                    var price = Math.Max(1, Math.Ceiling(handbook.FromRoubles(pick.PriceRoubles, currencyTpl)));
                    var limit = PurchaseLimit(pick, level, rng);
                    items.AddRange(Instantiate(pick, root, limit, seed, restock));
                    schemes[root.Id] = [[new BarterScheme { Template = currencyTpl, Count = price }]];
                    continue;
                }
            }

            // Kept as it is: currency exchange, the Lab keycard, or nothing to pick from.
            items.AddRange(WithDescendants(root, childrenOf));
            if (assort.BarterScheme?.TryGetValue(root.Id, out var scheme) == true)
            {
                schemes[root.Id] = scheme;
            }
        }

        return new TraderAssort
        {
            NextResupply = assort.NextResupply,
            Items = items,
            BarterScheme = schemes,
            LoyalLevelItems = loyalty,
        };
    }

    /// <summary>
    /// A random entry worth about <paramref name="value"/>: half to double it, widening to a third..3x and a fifth..5x
    /// when nothing fits; otherwise one of the closest in value. <paramref name="sorted"/> is ordered by price.
    /// </summary>
    private static PoolEntry PickByValue(List<PoolEntry> sorted, double value, Random rng)
    {
        if (value <= 0)
        {
            return sorted[rng.Next(sorted.Count)];
        }

        foreach (var factor in new[] { 2.0, 3.0, 5.0 })
        {
            var low = LowerBound(sorted, value / factor);
            var high = LowerBound(sorted, value * factor + 0.0001);
            if (high > low)
            {
                return sorted[low + rng.Next(high - low)];
            }
        }

        var closest = sorted.OrderBy(e => Math.Abs(Math.Log(Math.Max(1, e.PriceRoubles) / value))).Take(5).ToList();
        return closest[rng.Next(closest.Count)];
    }

    private static int LowerBound(List<PoolEntry> sorted, double price)
    {
        int lo = 0, hi = sorted.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (sorted[mid].PriceRoubles < price) lo = mid + 1; else hi = mid;
        }

        return lo;
    }

    /// <summary>
    /// Random limit from the item's value tier, raised 25% per loyalty level above 1. Loose rounds of ammo are
    /// counted per round, so they get ten times more.
    /// </summary>
    private static int PurchaseLimit(PoolEntry entry, int loyaltyLevel, Random rng)
    {
        var (low, high) = entry.PriceRoubles switch
        {
            < 15_000 => (20, 100),
            < 60_000 => (6, 30),
            < 200_000 => (3, 12),
            < 600_000 => (1, 5),
            _ => (1, 2),
        };
        var limit = rng.Next(low, high + 1) * (1 + 0.25 * (Math.Clamp(loyaltyLevel, 1, 4) - 1));
        if (entry.LooseAmmo)
        {
            limit *= 10;
        }

        return Math.Max(1, (int)Math.Round(limit));
    }

    private static IEnumerable<Item> Instantiate(PoolEntry entry, Item offer, int limit, int seed, long restock)
    {
        // The offer keeps its id; the children get new ids derived from it, so they are the same on every request.
        var ids = new Dictionary<string, string> { [entry.Tree[0].Id.ToString()] = offer.Id.ToString() };
        for (var n = 1; n < entry.Tree.Count; n++)
        {
            ids[entry.Tree[n].Id.ToString()] = StableId($"{seed}:{offer.Id}:{restock}:{n}");
        }

        for (var n = 0; n < entry.Tree.Count; n++)
        {
            var source = entry.Tree[n];
            if (n == 0)
            {
                yield return source with
                {
                    Id = new MongoId(ids[source.Id.ToString()]),
                    ParentId = Hideout,
                    SlotId = Hideout,
                    Location = null,
                    Upd = new Upd
                    {
                        UnlimitedCount = true,
                        StackObjectsCount = 9_999_999,
                        BuyRestrictionMax = limit,
                        BuyRestrictionCurrent = offer.Upd?.BuyRestrictionCurrent ?? 0,
                    },
                };
            }
            else
            {
                yield return source with
                {
                    Id = new MongoId(ids[source.Id.ToString()]),
                    ParentId = source.ParentId != null && ids.TryGetValue(source.ParentId, out var parent) ? parent : source.ParentId,
                };
            }
        }
    }

    private Dictionary<string, List<PoolEntry>>? _byCategory;

    private void EnsurePool(TarkovData data)
    {
        lock (_lock)
        {
            if (_traderPool != null)
            {
                return;
            }

            var categories = templates.Handbook.Categories.ToDictionary(c => c.Id.ToString(), c => c.ParentId?.ToString());
            string? TopOf(string? categoryId)
            {
                for (var hops = 0; categoryId != null && hops < 20; hops++)
                {
                    if (TopCategories.TryGetValue(categoryId, out var key))
                    {
                        return key;
                    }

                    categoryId = categories.GetValueOrDefault(categoryId);
                }

                return null;
            }

            _categoryOf = new Dictionary<string, string>();
            foreach (var entry in templates.Handbook.Items)
            {
                if (TopOf(entry.ParentId.ToString()) is { } key)
                {
                    _categoryOf[entry.Id.ToString()] = key;
                }
            }

            var seen = new HashSet<string>();
            var traderPool = new List<PoolEntry>();
            foreach (var trader in data.Traders)
            {
                var assort = traders.GetTrader(new MongoId(trader.Id))?.Assort;
                if (assort?.Items is null)
                {
                    continue;
                }

                var childrenOf = assort.Items.Where(i => i.ParentId != null).GroupBy(i => i.ParentId!)
                    .ToDictionary(g => g.Key, g => g.ToList());
                foreach (var root in assort.Items.Where(i => i.ParentId == Hideout))
                {
                    var tpl = root.Template.ToString();
                    if (Currencies.Contains(tpl) || !_categoryOf.TryGetValue(tpl, out var category) || category == "money")
                    {
                        continue;
                    }

                    var tree = WithDescendants(root, childrenOf).ToList();
                    var signature = string.Join(",", tree.Select(i => i.Template.ToString()));
                    if (!seen.Add(signature))
                    {
                        continue;
                    }

                    traderPool.Add(new PoolEntry(category, tree, handbook.GetTemplatePriceForItems(tree),
                        category == "ammo" && tree.Count == 1 && IsAmmoRound(tpl)));
                }
            }

            var anything = new List<PoolEntry>(traderPool);
            var poolRoots = traderPool.Select(e => e.Tree[0].Template.ToString()).ToHashSet();
            foreach (var entry in templates.Handbook.Items)
            {
                var tpl = entry.Id.ToString();
                if (poolRoots.Contains(tpl) || !_categoryOf.TryGetValue(tpl, out var category) || !LooseCategories.Contains(category)
                    || (entry.Price ?? 0) <= 0 || !templates.Items.TryGetValue(entry.Id, out var template)
                    || template.Properties?.QuestItem == true || template.Type != "Item")
                {
                    continue;
                }

                var single = new Item { Id = new MongoId(StableId($"loose:{tpl}")), Template = entry.Id };
                anything.Add(new PoolEntry(category, [single], entry.Price ?? 0, false));
            }

            // Sorted by price for value matching.
            _traderPool = traderPool.OrderBy(e => e.PriceRoubles).ToList();
            _anythingPool = anything.OrderBy(e => e.PriceRoubles).ToList();
            _byCategory = _traderPool.GroupBy(e => e.Category).ToDictionary(g => g.Key, g => g.ToList());
            logger.Info($"[Archipelago] shop randomizer: {traderPool.Count} trader offers, {anything.Count} items in all.");
        }
    }

    private bool IsAmmoRound(string tpl) =>
        templates.Items.TryGetValue(new MongoId(tpl), out var t) && t.Parent.ToString() == "5485a8684bdc2da71d8b4567";

    private static IEnumerable<Item> WithDescendants(Item root, Dictionary<string, List<Item>> childrenOf)
    {
        var stack = new Stack<Item>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var item = stack.Pop();
            yield return item;
            if (childrenOf.TryGetValue(item.Id.ToString(), out var children))
            {
                for (var n = children.Count - 1; n >= 0; n--)
                {
                    stack.Push(children[n]);
                }
            }
        }
    }

    private static int StableSeed(int seed, string traderId, string offerId, long restock)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{seed}:{traderId}:{offerId}:{restock}"));
        return BitConverter.ToInt32(hash, 0);
    }

    private static string StableId(string text) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..24].ToLowerInvariant();
}

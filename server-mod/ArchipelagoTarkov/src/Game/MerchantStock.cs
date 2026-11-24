using System.Security.Cryptography;
using System.Text;
using ArchipelagoTarkov.Data;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Utils.Cloners;

namespace ArchipelagoTarkov.Game;

/// <summary>Who is asking for the Archipelago trader's stock, and what they have.</summary>
public sealed record MerchantRequest(
    PmcData Pmc,
    ISet<string> UnlockedTraders,
    ContractSet Contracts,
    string ShopMode,
    bool ShopEveryRestock,
    int ShopSeed,
    int ShopStock,
    bool Barters = true);

/// <summary>
/// Archipelago contracts lock every trader but the Archipelago one. It sells:
///  * the stock of every main trader whose item you have received, at the loyalty level you'd have with that trader
///    (player level and standing; sales don't count, since you can't sell to them), randomized if shops are, with
///    offers Tarkov's quests would unlock once that share of the contract line is done;
///  * its own catalog, rolled again at every restock (every 15 minutes) from every weapon, armor, med, ammo and key
///    in the game, in four tiers that open as the contract line progresses (1 at the start, 2 at a quarter, 3 at
///    half, 4 at three quarters). Every restock has at least 3 weapons, armor and a helmet, meds for heavy bleeds,
///    light bleeds and fractures and a painkiller, random keys, and a magazine and ammo for every gun it sells or you
///    own.
/// Offer ids are derived from their source (and, for the catalog, the restock), so an offer keeps its id until the
/// next restock: buying needs that.
/// </summary>
[Injectable(InjectionType.Singleton)]
public class MerchantStock(TradersTable traders, TemplateTable templates, ItemHelper itemHelper, HandbookHelper handbook,
    PresetHelper presets, ICloner cloner)
{
    /// <summary>Restock every 15 minutes (the end-to-end tests shorten it with ARCHIPELAGO_TRADER_RESTOCK_SECONDS).</summary>
    public static int RestockSeconds { get; } =
        int.TryParse(Environment.GetEnvironmentVariable("ARCHIPELAGO_TRADER_RESTOCK_SECONDS"), out var s) && s > 0 ? s : 15 * 60;
    private const string Hideout = "hideout";
    private const double PriceMarkup = 1.25;
    private static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");
    private static readonly MongoId AmmoBase = new("5485a8684bdc2da71d8b4567");
    private static readonly MongoId AmmoBoxBase = new("543be5cb4bdc2deb348b4568");
    private static readonly MongoId WeaponBase = new("5422acb9af1c889c16000029");
    private static readonly MongoId SpecialWeaponBase = new("5447e0e74bdc2d3c308b4567");
    private static readonly MongoId GrenadeLauncherBase = new("5447bedf4bdc2d87278b4568");
    private static readonly MongoId MedsBase = new("543be5664bdc2dd4348b4569");
    private static readonly MongoId ArmorBase = new("5448e54d4bdc2dcc718b4568");
    private static readonly MongoId VestBase = new("5448e5284bdc2dcb718b4567");
    private static readonly MongoId HeadwearBase = new("5a341c4086f77401f2541505");
    private static readonly MongoId KeyBase = new("543be5e94bdc2df1348b4568");
    private static readonly MongoId MagazineBase = new("5448bc234bdc2d3c308b4569");

    // Catalog sizes per restock.
    private const int WeaponCount = 6;
    private const int ArmorCount = 3;
    private const int HelmetCount = 1;
    private const int RandomMedCount = 4;
    private const int KeyCount = 5;
    private const int RoundTypesPerGun = 2;

    private readonly Dictionary<MongoId, HashSet<MongoId>> _roundsByGun = new();
    private readonly object _lock = new();
    private Catalog? _catalog;

    /// <summary>The loyalty level player level and standing alone give with a trader (at least 1).</summary>
    public static int EffectiveLevel(TraderBase trader, PmcData pmc)
    {
        var level = pmc.Info?.Level ?? 1;
        var standing = pmc.TradersInfo is not null && pmc.TradersInfo.TryGetValue(trader.Id, out var info) ? info.Standing ?? 0 : 0;
        var reached = 0;
        foreach (var ll in trader.LoyaltyLevels ?? [])
        {
            if ((ll.MinLevel ?? 0) <= level && (ll.MinStanding ?? 0) <= standing + 1e-9 && reached < 4)
            {
                reached++;
            }
            else
            {
                break;
            }
        }

        return Math.Max(1, reached);
    }

    /// <summary>The catalog tier open at this share of the contract line (1-4).</summary>
    public static int TierFor(double share) => Math.Clamp(1 + (int)Math.Floor(share * 4 + 1e-9), 1, 4);

    public TraderAssort Build(MerchantRequest request, TraderAssort merchantBase, TarkovData data, ShopRandomizer shops)
    {
        var result = new TraderAssort
        {
            Items = [],
            BarterScheme = new(),
            LoyalLevelItems = new(),
            NextResupply = merchantBase.NextResupply,
        };
        var pmc = request.Pmc;
        var done = pmc.Quests?.Count(q => q.Status == QuestStatusEnum.Success
                                          && request.Contracts.Indexes.TryGetValue(q.QId, out var i) && i != 0) ?? 0;
        var share = request.Contracts.Count == 0 ? 0 : done / (double)request.Contracts.Count;
        var sold = new HashSet<MongoId>(); // root templates on sale
        var soldRounds = new HashSet<MongoId>(); // rounds on sale, loose or boxed

        AddTraderStock(request, data, shops, share, result, sold, soldRounds);

        // The catalog: rolled per profile and restock.
        var archipelago = traders.GetTrader(new MongoId(Contracts.TraderId));
        var restock = (long)(archipelago?.Base.NextResupply ?? 0);
        var tier = TierFor(share);
        var catalog = GetCatalog();
        var rng = new Random(Seed($"{pmc.Id}:{request.ShopSeed}:{restock}"));
        var tag = $"catalog:{restock}";

        foreach (var weapon in Pick(catalog.Weapons, tier, WeaponCount, rng, sold))
        {
            AddPreset(result, weapon.Tpl, tag, Limit(weapon.Tier, 2), sold);
        }

        foreach (var armor in Pick(catalog.Armor, tier, ArmorCount, rng, sold))
        {
            AddPreset(result, armor.Tpl, tag, 2, sold);
        }

        foreach (var helmet in Pick(catalog.Helmets, tier, HelmetCount, rng, sold))
        {
            AddPreset(result, helmet.Tpl, tag, 2, sold);
        }

        // Meds: one for each problem, always, then a few at random.
        foreach (var effect in new[] { DamageEffectType.HeavyBleeding, DamageEffectType.LightBleeding,
                     DamageEffectType.Fracture, DamageEffectType.Pain })
        {
            var treats = catalog.Meds.Where(m => m.Treats.Contains(effect)).ToList();
            if (treats.Any(m => sold.Contains(m.Tpl) && m.Tier <= tier))
            {
                continue;
            }

            foreach (var med in Pick(treats, tier, 1, rng, sold, fallBackToLowest: true))
            {
                AddSingle(result, med.Tpl, tag, 10, sold);
            }
        }

        foreach (var med in Pick(catalog.Meds, tier, RandomMedCount, rng, sold))
        {
            AddSingle(result, med.Tpl, tag, 5, sold);
        }

        foreach (var key in Pick(catalog.Keys, tier, KeyCount, rng, sold))
        {
            AddSingle(result, key.Tpl, tag, 1, sold);
        }

        // A magazine and ammo for every gun on sale or owned. A gun's caliber name doesn't always match its ammo's
        // (the PP-9 says 9x18PMM, every 9x18 round 9x18PM), so this goes by what its chamber and magazine slot take.
        var guns = (pmc.Inventory?.Items ?? []).Concat(result.Items.Where(i => i.ParentId == Hideout))
            .Select(i => i.Template).Where(t => itemHelper.IsOfBaseclass(t, WeaponBase)).ToHashSet();
        foreach (var gun in guns)
        {
            var mags = Magazines(gun);
            if (mags.Count > 0 && !mags.Any(sold.Contains))
            {
                var options = catalog.Magazines.Where(m => mags.Contains(m.Tpl)).ToList();
                foreach (var mag in Pick(options, tier, 1, rng, sold, fallBackToLowest: true))
                {
                    AddSingle(result, mag.Tpl, tag, 4, sold);
                }
            }

            var rounds = Rounds(gun);
            if (rounds.Count > 0 && !rounds.Any(soldRounds.Contains))
            {
                var options = catalog.Ammo.Where(a => rounds.Contains(a.Tpl)).ToList();
                foreach (var round in Pick(options, tier, RoundTypesPerGun, rng, sold, fallBackToLowest: true))
                {
                    AddSingle(result, round.Tpl, tag, 300, sold);
                    soldRounds.Add(round.Tpl);
                }
            }
        }

        foreach (var root in result.Items.Where(i => i.ParentId == Hideout))
        {
            result.LoyalLevelItems[root.Id] = 1;
        }

        // Buying re-reads the trader's stored stock (stock and purchase limits): keep the last stock built there.
        if (archipelago is not null)
        {
            archipelago.Assort = result;
        }

        return result;
    }

    // ------------------------------------------------------------------ received traders' stock
    private void AddTraderStock(MerchantRequest request, TarkovData data, ShopRandomizer shops, double share,
        TraderAssort result, HashSet<MongoId> sold, HashSet<MongoId> soldRounds)
    {
        foreach (var def in data.Traders)
        {
            var trader = traders.GetTrader(new MongoId(def.Id));
            if (trader?.Assort?.Items is null || !request.UnlockedTraders.Contains(def.Key))
            {
                continue;
            }

            var level = EffectiveLevel(trader.Base, request.Pmc);
            var questLocks = new Dictionary<MongoId, MongoId>();
            foreach (var (_, offers) in trader.QuestAssort ?? new())
            {
                foreach (var (offer, quest) in offers)
                {
                    questLocks[offer] = quest;
                }
            }

            bool QuestOk(MongoId offerId) => !questLocks.TryGetValue(offerId, out var quest)
                                             || (data.QuestProgress.TryGetValue(quest.ToString(), out var needed) && share >= needed);

            var stock = request.ShopMode == "vanilla"
                ? cloner.Clone(trader.Assort)!
                : shops.Randomize(cloner.Clone(trader.Assort)!, def.Id, request.ShopMode, request.ShopEveryRestock,
                    request.ShopSeed, request.ShopStock, data);
            foreach (var offer in Offers(stock))
            {
                var ll = stock.LoyalLevelItems?.GetValueOrDefault(offer.Root.Id, 1) ?? 1;
                if (ll <= level && QuestOk(offer.Root.Id) && offer.Scheme is not null
                    && (request.Barters || IsMoneyOffer(offer.Scheme)))
                {
                    Add(result, offer, def.Id, "stock");
                    sold.Add(offer.Root.Template);
                    if (Round(offer) is { } round)
                    {
                        soldRounds.Add(round);
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ the catalog
    private sealed record Entry(MongoId Tpl, int Tier, double Price, HashSet<DamageEffectType> Treats);

    private sealed class Catalog
    {
        public List<Entry> Weapons = [];
        public List<Entry> Armor = [];
        public List<Entry> Helmets = [];
        public List<Entry> Meds = [];
        public List<Entry> Ammo = [];
        public List<Entry> Keys = [];
        public List<Entry> Magazines = [];
    }

    /// <summary>Every buyable weapon, armor, helmet, med, round, key and magazine in the game, with its tier.</summary>
    private Catalog GetCatalog()
    {
        lock (_lock)
        {
            if (_catalog != null)
            {
                return _catalog;
            }

            var catalog = new Catalog();
            var weapons = new List<(MongoId, double)>();
            var meds = new List<(MongoId, double, HashSet<DamageEffectType>)>();
            var keys = new List<(MongoId, double)>();
            var mags = new List<(MongoId, double)>();
            foreach (var (tpl, template) in templates.Items)
            {
                var props = template.Properties;
                if (template.Type != "Item" || props is null || props.QuestItem == true)
                {
                    continue;
                }

                var price = handbook.GetTemplatePrice(tpl);
                if (price <= 0)
                {
                    continue; // not in the handbook: dev, event or quest-only things
                }

                if (itemHelper.IsOfBaseclass(tpl, WeaponBase))
                {
                    if (!itemHelper.IsOfBaseclass(tpl, SpecialWeaponBase) && !itemHelper.IsOfBaseclass(tpl, GrenadeLauncherBase)
                        && presets.GetDefaultPreset(tpl) is { } preset)
                    {
                        weapons.Add((tpl, PresetPrice(preset)));
                    }
                }
                else if (itemHelper.IsOfBaseclass(tpl, ArmorBase) || itemHelper.IsOfBaseclass(tpl, VestBase)
                         || itemHelper.IsOfBaseclass(tpl, HeadwearBase))
                {
                    var preset = presets.GetDefaultPreset(tpl);
                    var items = preset?.Items ?? [new Item { Id = new MongoId(), Template = tpl }];
                    var armorClass = items.Max(i => itemHelper.GetItem(i.Template).Value?.Properties?.ArmorClass ?? 0);
                    if (armorClass <= 0)
                    {
                        continue; // a plain rig or a hat
                    }

                    var tier = armorClass switch { <= 3 => 1, 4 => 2, 5 => 3, _ => 4 };
                    var list = itemHelper.IsOfBaseclass(tpl, HeadwearBase) ? catalog.Helmets : catalog.Armor;
                    list.Add(new Entry(tpl, tier, price, []));
                }
                else if (itemHelper.IsOfBaseclass(tpl, MedsBase))
                {
                    meds.Add((tpl, price, props.EffectsDamage?.Keys.ToHashSet() ?? []));
                }
                else if (itemHelper.IsOfBaseclass(tpl, AmmoBase))
                {
                    var pen = props.PenetrationPower ?? 0;
                    var tier = pen switch { < 20 => 1, < 30 => 2, < 40 => 3, _ => 4 };
                    catalog.Ammo.Add(new Entry(tpl, tier, price, []));
                }
                else if (itemHelper.IsOfBaseclass(tpl, KeyBase))
                {
                    keys.Add((tpl, price));
                }
                else if (itemHelper.IsOfBaseclass(tpl, MagazineBase))
                {
                    mags.Add((tpl, price));
                }
            }

            catalog.Weapons = ByPrice(weapons.Select(w => (w.Item1, w.Item2, new HashSet<DamageEffectType>())));
            catalog.Meds = ByPrice(meds);
            catalog.Keys = ByPrice(keys.Select(k => (k.Item1, k.Item2, new HashSet<DamageEffectType>())));
            catalog.Magazines = ByPrice(mags.Select(m => (m.Item1, m.Item2, new HashSet<DamageEffectType>())));
            _catalog = catalog;
            return catalog;
        }
    }

    /// <summary>Tiers by price: the cheapest quarter is tier 1, the dearest tier 4.</summary>
    private static List<Entry> ByPrice(IEnumerable<(MongoId Tpl, double Price, HashSet<DamageEffectType> Treats)> items)
    {
        var sorted = items.OrderBy(i => i.Price).ToList();
        return sorted.Select((x, n) => new Entry(x.Tpl, 1 + n * 4 / Math.Max(1, sorted.Count), x.Price, x.Treats)).ToList();
    }

    private double PresetPrice(Preset preset) => preset.Items.Sum(i => Math.Max(0, handbook.GetTemplatePrice(i.Template)));

    /// <summary>
    /// <paramref name="count"/> distinct entries of an open tier, weighted toward the highest open tier. With
    /// <paramref name="fallBackToLowest"/>, the lowest tier there is when no tier is open yet.
    /// </summary>
    private static List<Entry> Pick(List<Entry> pool, int tier, int count, Random rng, HashSet<MongoId> sold,
        bool fallBackToLowest = false)
    {
        var open = pool.Where(e => e.Tier <= tier && !sold.Contains(e.Tpl)).ToList();
        if (open.Count == 0 && fallBackToLowest && pool.Count > 0)
        {
            var lowest = pool.Min(e => e.Tier);
            open = pool.Where(e => e.Tier == lowest && !sold.Contains(e.Tpl)).ToList();
        }

        var picked = new List<Entry>();
        while (picked.Count < count && open.Count > 0)
        {
            var weights = open.Select(e => e.Tier == tier ? 3.0 : 1.0).ToList();
            var roll = rng.NextDouble() * weights.Sum();
            var index = 0;
            while (index < open.Count - 1 && (roll -= weights[index]) > 0)
            {
                index++;
            }

            picked.Add(open[index]);
            open.RemoveAt(index);
        }

        return picked;
    }

    private static int Limit(int tier, int baseLimit) => tier <= 2 ? baseLimit + 1 : baseLimit;

    // ------------------------------------------------------------------ offers
    private sealed record Offer(Item Root, List<Item> Children, List<List<BarterScheme>>? Scheme);

    private static readonly HashSet<MongoId> Currencies =
    [
        new("5449016a4bdc2d6f028b456f"), // roubles
        new("5696686a4bdc2da3298b456a"), // dollars
        new("569668774bdc2da2298b4568"), // euros
    ];

    /// <summary>True when every way to pay for the offer is money (not a barter).</summary>
    public static bool IsMoneyOffer(List<List<BarterScheme>>? scheme) =>
        scheme is { Count: > 0 } && scheme.All(option => option.Count > 0 && option.All(p => Currencies.Contains(p.Template)));

    /// <summary>Option trader_barters off: take every barter offer (and its parts) out of an assort.</summary>
    public static TraderAssort RemoveBarters(TraderAssort assort)
    {
        var barters = assort.Items.Where(i => i.ParentId == Hideout
                                              && !IsMoneyOffer(assort.BarterScheme?.GetValueOrDefault(i.Id))).Select(i => i.Id).ToHashSet();
        if (barters.Count == 0)
        {
            return assort;
        }

        // Offer roots and every part under them (parent ids are strings; the roots' parent is "hideout").
        var removed = barters.Select(b => b.ToString()).ToHashSet();
        bool grew;
        do
        {
            grew = false;
            foreach (var item in assort.Items)
            {
                if (item.ParentId is { } parent && removed.Contains(parent) && removed.Add(item.Id.ToString()))
                {
                    grew = true;
                }
            }
        } while (grew);

        assort.Items = assort.Items.Where(i => !removed.Contains(i.Id.ToString())).ToList();
        foreach (var id in barters)
        {
            assort.BarterScheme?.Remove(id);
            assort.LoyalLevelItems?.Remove(id);
        }

        return assort;
    }

    private static IEnumerable<Offer> Offers(TraderAssort assort)
    {
        var children = assort.Items.Where(i => i.ParentId != null && i.ParentId != Hideout)
            .GroupBy(i => i.ParentId!).ToDictionary(g => g.Key, g => g.ToList());
        foreach (var root in assort.Items.Where(i => i.ParentId == Hideout))
        {
            var tree = new List<Item>();
            var stack = new Stack<string>([root.Id.ToString()]);
            while (stack.Count > 0)
            {
                foreach (var child in children.GetValueOrDefault(stack.Pop()) ?? [])
                {
                    tree.Add(child);
                    stack.Push(child.Id.ToString());
                }
            }

            yield return new Offer(root, tree, assort.BarterScheme?.GetValueOrDefault(root.Id));
        }
    }

    /// <summary>Copy an offer in, with ids derived from its source.</summary>
    private void Add(TraderAssort result, Offer offer, string source, string kind)
    {
        var ids = new Dictionary<string, MongoId>();
        MongoId NewId(MongoId old)
        {
            if (!ids.TryGetValue(old.ToString(), out var id))
            {
                ids[old.ToString()] = id = DerivedId($"{source}:{kind}:{old}");
            }

            return id;
        }

        var root = cloner.Clone(offer.Root)!;
        var rootId = NewId(root.Id);
        root.Id = rootId;
        result.Items.Add(root);
        foreach (var child in offer.Children)
        {
            var copy = cloner.Clone(child)!;
            copy.Id = NewId(child.Id);
            copy.ParentId = NewId(new MongoId(child.ParentId!)).ToString();
            result.Items.Add(copy);
        }

        result.BarterScheme[rootId] = cloner.Clone(offer.Scheme)!;
    }

    /// <summary>A catalog offer for an assembled item (its default preset: a built gun, armor with its plates).</summary>
    private void AddPreset(TraderAssort result, MongoId tpl, string tag, int limit, HashSet<MongoId> sold)
    {
        var preset = presets.GetDefaultPreset(tpl);
        if (preset?.Items is not { Count: > 0 } items || !sold.Add(tpl))
        {
            if (preset is null)
            {
                AddSingle(result, tpl, tag, limit, sold);
            }

            return;
        }

        var root = items.FirstOrDefault(i => i.Template == tpl) ?? items[0];
        var rest = items.Where(i => !ReferenceEquals(i, root)).ToList();
        var price = Math.Max(1, Math.Ceiling(PresetPrice(preset) * PriceMarkup));
        var offerRoot = cloner.Clone(root)!;
        offerRoot.ParentId = Hideout;
        offerRoot.SlotId = Hideout;
        offerRoot.Location = null;
        offerRoot.Upd = new Upd { StackObjectsCount = limit, BuyRestrictionMax = limit, BuyRestrictionCurrent = 0 };
        Add(result, new Offer(offerRoot, rest, [[new BarterScheme { Template = Roubles, Count = price }]]), tag, "preset");
    }

    /// <summary>A catalog offer for a single item at handbook price.</summary>
    private void AddSingle(TraderAssort result, MongoId tpl, string tag, int limit, HashSet<MongoId> sold)
    {
        if (!sold.Add(tpl))
        {
            return;
        }

        var id = DerivedId($"{tag}:single:{tpl}");
        result.Items.Add(new Item
        {
            Id = id,
            Template = tpl,
            ParentId = Hideout,
            SlotId = Hideout,
            Upd = new Upd { StackObjectsCount = limit, BuyRestrictionMax = limit, BuyRestrictionCurrent = 0 },
        });
        var price = Math.Max(1, Math.Ceiling(handbook.GetTemplatePrice(tpl) * PriceMarkup));
        result.BarterScheme[id] = [[new BarterScheme { Template = Roubles, Count = price }]];
    }

    // ------------------------------------------------------------------ what guns take
    /// <summary>The rounds a gun takes: what its chambers accept (or, without chambers, ammo of its caliber).</summary>
    private HashSet<MongoId> Rounds(MongoId gun)
    {
        lock (_lock)
        {
            if (_roundsByGun.TryGetValue(gun, out var cached))
            {
                return cached;
            }

            var template = itemHelper.GetItem(gun).Value;
            var rounds = new HashSet<MongoId>();
            foreach (var chamber in template?.Properties?.Chambers ?? [])
            {
                foreach (var filter in chamber.Properties?.Filters ?? [])
                {
                    rounds.UnionWith((filter.Filter ?? []).Where(t => itemHelper.IsOfBaseclass(t, AmmoBase)));
                }
            }

            if (rounds.Count == 0 && template?.Properties?.AmmoCaliber is { Length: > 0 } caliber)
            {
                rounds.UnionWith(templates.Items.Values
                    .Where(t => t.Properties?.Caliber == caliber && itemHelper.IsOfBaseclass(t.Id, AmmoBase))
                    .Select(t => t.Id));
            }

            _roundsByGun[gun] = rounds;
            return rounds;
        }
    }

    /// <summary>The magazines a gun with a detachable magazine takes (empty for internal magazines).</summary>
    private HashSet<MongoId> Magazines(MongoId gun)
    {
        var template = itemHelper.GetItem(gun).Value;
        if (template?.Properties?.ReloadMode != ReloadMode.ExternalMagazine)
        {
            return [];
        }

        var slot = template.Properties.Slots?.FirstOrDefault(s => s.Name == "mod_magazine");
        return (slot?.Properties?.Filters ?? []).SelectMany(f => f.Filter ?? [])
            .Where(t => itemHelper.IsOfBaseclass(t, MagazineBase)).ToHashSet();
    }

    /// <summary>The round an ammo offer sells (loose, or the rounds in a box), or null.</summary>
    private MongoId? Round(Offer offer)
    {
        var tpl = offer.Root.Template;
        if (itemHelper.IsOfBaseclass(tpl, AmmoBase))
        {
            return tpl;
        }

        if (itemHelper.IsOfBaseclass(tpl, AmmoBoxBase))
        {
            return offer.Children.FirstOrDefault(c => itemHelper.IsOfBaseclass(c.Template, AmmoBase))?.Template;
        }

        return null;
    }

    private static int Seed(string key) => BitConverter.ToInt32(SHA256.HashData(Encoding.UTF8.GetBytes(key)), 0);

    private static MongoId DerivedId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"archipelago-merchant:{key}"));
        return new MongoId(Convert.ToHexString(hash, 0, 12).ToLowerInvariant());
    }
}

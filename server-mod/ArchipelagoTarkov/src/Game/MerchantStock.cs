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
    int ShopStock);

/// <summary>
/// Archipelago contracts lock every trader but the Archipelago one, which sells instead:
///  * the stock of every main trader whose item you have received, at the loyalty level you'd have with that trader
///    (player level and standing; sales don't count, since you can't sell to them), randomized if shops are;
///  * offers Tarkov's quests would unlock, once that share of the contract line is done;
///  * always: medications (every trader's loyalty level 1 meds) and ammo for every caliber of the guns you own or
///    that it sells, from any trader (the best you'd be allowed, or at least the most basic one, or a plain offer at
///    handbook price if no trader sells that caliber at all).
/// Offer ids are derived from the source offer, so the same offer keeps its id between requests (buying needs that).
/// </summary>
[Injectable(InjectionType.Singleton)]
public class MerchantStock(TradersTable traders, TemplateTable templates, ItemHelper itemHelper, HandbookHelper handbook,
    ICloner cloner)
{
    private const string Hideout = "hideout";
    private static readonly MongoId Roubles = new("5449016a4bdc2d6f028b456f");
    private static readonly MongoId[] MedicationBases =
    [
        new("5448f39d4bdc2d0a728b4568"), // medkit
        new("5448f3ac4bdc2dce718b4569"), // medical (bandages, splints, surgery)
        new("5448f3a14bdc2d27728b4569"), // drugs (painkillers)
    ];
    private static readonly MongoId AmmoBase = new("5485a8684bdc2da71d8b4567");
    private static readonly MongoId AmmoBoxBase = new("543be5cb4bdc2deb348b4568");
    private static readonly MongoId WeaponBase = new("5422acb9af1c889c16000029");

    private readonly Dictionary<MongoId, HashSet<MongoId>> _roundsByGun = new();
    private readonly object _lock = new();

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
        var guaranteedMeds = new List<(Offer Offer, string Trader)>();
        var ammoOffers = new List<(Offer Offer, MongoId Round, string Trader, int Level, bool Allowed)>();

        foreach (var def in data.Traders)
        {
            var trader = traders.GetTrader(new MongoId(def.Id));
            if (trader?.Assort?.Items is null)
            {
                continue;
            }

            var level = EffectiveLevel(trader.Base, pmc);
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

            var vanilla = cloner.Clone(trader.Assort)!;
            if (request.UnlockedTraders.Contains(def.Key))
            {
                var stock = request.ShopMode == "vanilla"
                    ? vanilla
                    : shops.Randomize(cloner.Clone(trader.Assort)!, def.Id, request.ShopMode, request.ShopEveryRestock,
                        request.ShopSeed, request.ShopStock, data);
                foreach (var offer in Offers(stock))
                {
                    var ll = stock.LoyalLevelItems?.GetValueOrDefault(offer.Root.Id, 1) ?? 1;
                    if (ll <= level && QuestOk(offer.Root.Id))
                    {
                        Add(result, offer, def.Id, "stock");
                        sold.Add(offer.Root.Template);
                    }
                }
            }

            foreach (var offer in Offers(vanilla))
            {
                var ll = vanilla.LoyalLevelItems?.GetValueOrDefault(offer.Root.Id, 1) ?? 1;
                var tpl = offer.Root.Template;
                if (ll == 1 && MedicationBases.Any(b => itemHelper.IsOfBaseclass(tpl, b)))
                {
                    guaranteedMeds.Add((offer, def.Id));
                }
                else if (Round(offer) is { } round)
                {
                    ammoOffers.Add((offer, round, def.Id, ll, ll <= level && QuestOk(offer.Root.Id)));
                }
            }
        }

        foreach (var (offer, trader) in guaranteedMeds)
        {
            if (sold.Add(offer.Root.Template))
            {
                Add(result, offer, trader, "meds");
            }
        }

        // Ammo for every gun owned or on sale: rounds its chamber takes (a gun's caliber name doesn't always match its
        // ammo's, e.g. the PP-9 says 9x18PMM and every 9x18 round says 9x18PM).
        var guns = (pmc.Inventory?.Items ?? []).Concat(result.Items.Where(i => i.ParentId == Hideout))
            .Select(i => i.Template).Where(t => itemHelper.IsOfBaseclass(t, WeaponBase)).ToHashSet();
        var served = new List<HashSet<MongoId>>();
        foreach (var gun in guns)
        {
            var rounds = Rounds(gun);
            if (rounds.Count == 0 || served.Any(r => r.SetEquals(rounds)))
            {
                continue;
            }

            served.Add(rounds);
            var candidates = ammoOffers.Where(a => rounds.Contains(a.Round)).ToList();
            var allowed = candidates.Where(a => a.Allowed).ToList();
            if (allowed.Count == 0 && candidates.Count > 0)
            {
                var lowest = candidates.Min(a => a.Level);
                allowed = candidates.Where(a => a.Level == lowest).ToList();
            }

            var any = false;
            foreach (var (offer, _, trader, _, _) in allowed)
            {
                if (sold.Add(offer.Root.Template))
                {
                    Add(result, offer, trader, "ammo");
                }

                any = true;
            }

            if (!any && !rounds.Any(sold.Contains))
            {
                AddPlainAmmo(result, rounds, sold);
            }
        }

        foreach (var root in result.Items.Where(i => i.ParentId == Hideout))
        {
            result.LoyalLevelItems[root.Id] = 1;
        }

        // Buying re-reads the trader's stored stock (stock and purchase limits): keep the last stock built there.
        if (traders.GetTrader(new MongoId(Contracts.TraderId)) is { } archipelago)
        {
            archipelago.Assort = result;
        }

        return result;
    }

    // ------------------------------------------------------------------ offers
    private sealed record Offer(Item Root, List<Item> Children, List<List<BarterScheme>>? Scheme);

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

    private void Add(TraderAssort result, Offer offer, string traderId, string kind)
    {
        if (offer.Scheme is null)
        {
            return;
        }

        var ids = new Dictionary<string, MongoId>();
        MongoId NewId(MongoId old)
        {
            if (!ids.TryGetValue(old.ToString(), out var id))
            {
                ids[old.ToString()] = id = DerivedId($"{traderId}:{kind}:{old}");
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

    /// <summary>A plain roubles offer for the cheapest round a gun takes, when no trader sells any of them.</summary>
    private void AddPlainAmmo(TraderAssort result, HashSet<MongoId> rounds, HashSet<MongoId> sold)
    {
        var ammo = rounds.Where(r => handbook.GetTemplatePrice(r) > 0).OrderBy(r => handbook.GetTemplatePrice(r))
            .Cast<MongoId?>().FirstOrDefault();
        if (ammo is not { } tpl || !sold.Add(tpl))
        {
            return;
        }

        var id = DerivedId($"plain-ammo:{tpl}");
        result.Items.Add(new Item
        {
            Id = id,
            Template = tpl,
            ParentId = Hideout,
            SlotId = Hideout,
            Upd = new Upd { UnlimitedCount = true, StackObjectsCount = 999999, BuyRestrictionMax = 120, BuyRestrictionCurrent = 0 },
        });
        var price = Math.Max(1, Math.Ceiling(handbook.GetTemplatePrice(tpl) * 1.25));
        result.BarterScheme[id] = [[new BarterScheme { Template = Roubles, Count = price }]];
    }

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

    private static MongoId DerivedId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"archipelago-merchant:{key}"));
        return new MongoId(Convert.ToHexString(hash, 0, 12).ToLowerInvariant());
    }
}

using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArchipelagoTarkov.Data;
using ArchipelagoTarkov.State;
using SPTarkov.DI.Annotations;
using SPTarkov.Server.Core.DI;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Models.Spt.Config;
using SPTarkov.Server.Core.Models.Spt.Tables;
using SPTarkov.Server.Core.Routers;
using SPTarkov.Server.Core.Utils;

namespace ArchipelagoTarkov.Game;

/// <summary>One profile's contract line, as quests in SPT's database.</summary>
public sealed class ContractSet
{
    public required string ProfileId { get; init; }
    /// <summary>Quest id -> contract index (0 is the Kappa contract).</summary>
    public Dictionary<MongoId, int> Indexes { get; } = new();
    public Dictionary<int, MongoId> QuestIds { get; } = new();
    /// <summary>Contracts before the Kappa one.</summary>
    public int Count { get; set; }
    public MongoId KappaId => QuestIds[0];
}

/// <summary>
/// Archipelago contracts (Quest Mode archipelago_contracts). The Archipelago trader is registered once; each linked
/// profile's contracts (written by the seed, sent in slot data) become real quests of that trader, with their own
/// ids, so profiles linked to different games never share one. <see cref="Patches.ClientQuestsPatch"/> shows a
/// profile only its own contracts and hides Tarkov's quests from it.
///
/// The game client reads quest text from its locale, which it loads when it starts. Contracts are built when the
/// server starts (from the saved slot data), so their text is there from the first launch after linking; quest
/// names and descriptions are also their own locale keys, so even before a restart the Tasks screen reads well.
/// </summary>
[Injectable(InjectionType.Singleton, TypePriority = OnLoadOrder.PostLoad)]
public class Contracts(
    ISptLogger<Contracts> logger,
    TemplateTable templates,
    LocaleTable locales,
    TradersTable traders,
    TraderConfig traderConfig,
    InsuranceConfig insuranceConfig,
    ImageRouter imageRouter,
    JsonUtil json,
    ItemHelper itemHelper
) : IOnLoad
{
    /// <summary>"archipelago0" in hex.</summary>
    public const string TraderId = "617263686970656c61676f30";
    private const string TherapistId = "54cb57776803fa99248b456e";

    /// <summary>Insured items come back from the Archipelago trader with this chance (Therapist's in SPT).</summary>
    public const double InsuranceReturnChance = 95;
    public const string KappaTpl = "5c093ca986f7740a1867ab12";
    private const string Roubles = "5449016a4bdc2d6f028b456f";

    public static Contracts? Instance { get; private set; }
    public static bool TraderReady { get; private set; }

    private readonly ConcurrentDictionary<string, ContractSet> _sets = new();
    /// <summary>Contract quest id -> the profile it belongs to.</summary>
    private readonly ConcurrentDictionary<MongoId, string> _owners = new();
    /// <summary>Every locale string contracts and the trader add; one transformer per language reads it.</summary>
    private readonly ConcurrentDictionary<string, string> _text = new();
    private TarkovData? _data;

    public Task OnLoadAsync(CancellationToken cancellationToken)
    {
        Instance = this;
        try
        {
            _data = TarkovData.LoadEmbedded();
            foreach (var (_, lazy) in locales.Global)
            {
                lazy.AddTransformer(dict =>
                {
                    if (dict is not null)
                    {
                        foreach (var (key, value) in _text)
                        {
                            dict[key] = value;
                        }
                    }

                    return dict;
                });
            }

            RegisterTrader();
            TraderReady = true;
        }
        catch (Exception ex)
        {
            logger.Error($"[Archipelago] the Archipelago trader could not be added, so contracts are unavailable: {ex}");
        }

        return Task.CompletedTask;
    }

    public ContractSet? For(string profileId) => _sets.GetValueOrDefault(profileId);

    /// <summary>True if this quest id is any profile's contract.</summary>
    public bool IsContract(MongoId questId) => _owners.ContainsKey(questId);

    public bool OwnedBy(MongoId questId, string profileId) => _owners.TryGetValue(questId, out var owner) && owner == profileId;

    // ------------------------------------------------------------------ trader
    private void RegisterTrader()
    {
        var modDir = System.IO.Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) ?? Directory.GetCurrentDirectory();
        var avatarPath = System.IO.Path.Combine(modDir, "res", "archipelago_trader.png");
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(avatarPath)!);
        using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("ArchipelagoTarkov.archipelago.png"))
        {
            if (stream != null)
            {
                using var file = File.Create(avatarPath);
                stream.CopyTo(file);
            }
        }

        imageRouter.AddRoute($"/files/trader/avatar/{TraderId}", avatarPath);

        var template = traders.GetTrader(new MongoId("54cb50c76803fa8b248b4571"))
                       ?? throw new InvalidOperationException("Prapor is missing from the trader table");
        // Insurance and healing on Therapist's terms (as SPT ships them: other mods may change Therapist later).
        var therapist = traders.GetTrader(new MongoId(TherapistId));
        var level = (template.Base.LoyaltyLevels?.FirstOrDefault() ?? new TraderLoyaltyLevel()) with
        {
            InsurancePriceCoefficient = 20,
            HealPriceCoefficient = 100,
        };
        var traderBase = template.Base with
        {
            Id = new MongoId(TraderId),
            Name = "Archipelago",
            Surname = "",
            Nickname = "Archipelago",
            Location = "Somewhere in the multiworld",
            Avatar = $"/files/trader/avatar/{TraderId}.png",
            Currency = CurrencyType.RUB,
            UnlockedByDefault = true,
            NextResupply = (int)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() + MerchantStock.RestockSeconds),
            AvailableInRaid = false,
            // The Archipelago trader insures and heals in every quest mode, so the insurance and treatment screens
            // always have a trader to use (the game's insurance screen breaks with none, which happens when every
            // insurer is locked or another mod switches insurance off).
            Medic = true,
            CustomizationSeller = false,
            IsAvailableInPVE = true,
            Insurance = new TraderInsurance
            {
                Availability = true,
                ExcludedCategory = (therapist?.Base.Insurance ?? template.Base.Insurance)?.ExcludedCategory ?? [],
                MinPayment = 0,
                MinReturnHour = 10,
                MaxReturnHour = 20,
                MaxStorageTime = 144,
            },
            Repair = template.Base.Repair is { } rep ? rep with { Availability = false } : null,
            // Buys whatever any main trader buys: the others are locked to a contracts profile.
            ItemsBuy = MainTradersBuy(),
            ItemsBuyProhibited = template.Base.ItemsBuyProhibited ?? new ItemBuyData { Category = [], IdList = [] },
            LoyaltyLevels = [level with { MinLevel = 1, MinSalesSum = 0, MinStanding = 0 }],
        };

        traders[traderBase.Id] = new Trader
        {
            Base = traderBase,
            Assort = new TraderAssort
            {
                Items = [],
                BarterScheme = new(),
                LoyalLevelItems = new(),
                NextResupply = template.Assort?.NextResupply,
            },
            // Insurance mail ("insuranceStart", "insuranceFound"...) is written with these message templates.
            Dialogue = (therapist ?? template).Dialogue?.ToDictionary(e => e.Key, e => e.Value?.ToList()) ?? new(),
            QuestAssort = new()
            {
                ["started"] = new(),
                ["success"] = new(),
                ["fail"] = new(),
            },
            Suits = [],
            Services = [],
        };

        insuranceConfig.ReturnChancePercent[traderBase.Id] = InsuranceReturnChance;

        if (traderConfig.UpdateTime.All(u => u.TraderId != traderBase.Id))
        {
            traderConfig.UpdateTime.Add(new UpdateTime
            {
                Name = "archipelago",
                TraderId = traderBase.Id,
                Seconds = new MinMax<int> { Min = MerchantStock.RestockSeconds, Max = MerchantStock.RestockSeconds },
            });
        }

        _text[$"{TraderId} FullName"] = "Archipelago";
        _text[$"{TraderId} FirstName"] = "Archipelago";
        _text[$"{TraderId} Nickname"] = "Archipelago";
        _text[$"{TraderId} Location"] = "Somewhere in the multiworld";
        _text[$"{TraderId} Description"] =
            "Nobody has met the client. The contracts arrive anyway, each one paid for by somebody in another world. " +
            "Finish them all and the Kappa container is yours.";
        RefreshLocales();
    }

    /// <summary>
    /// Keep the trader's insurance and healing on: a mod that loads after this one may switch insurance off for every
    /// trader (SVM does when its return chances are 0).
    /// </summary>
    public void EnsureServices()
    {
        if (!TraderReady || traders.GetTrader(new MongoId(TraderId))?.Base is not { } b)
        {
            return;
        }

        if (b.Insurance is { Availability: not true } ins)
        {
            ins.Availability = true;
        }

        b.Medic = true;
        insuranceConfig.ReturnChancePercent[b.Id] = InsuranceReturnChance;
    }

    private ItemBuyData MainTradersBuy()
    {
        var buy = new ItemBuyData { Category = [], IdList = [] };
        foreach (var def in _data!.Traders)
        {
            if (traders.GetTrader(new MongoId(def.Id))?.Base.ItemsBuy is { } other)
            {
                buy.Category.UnionWith(other.Category);
                buy.IdList.UnionWith(other.IdList);
            }
        }

        return buy;
    }

    // ------------------------------------------------------------------ contracts
    /// <summary>
    /// Make the profile's contracts match its slot data: build them for a contracts game, or remove them otherwise.
    /// Safe to call again (the same seed gives the same quest ids).
    /// </summary>
    public void Build(ProfileState state)
    {
        if (_data is null || !TraderReady)
        {
            return;
        }

        if (!state.HasSlotData || state.SlotString("quest_mode") != "archipelago_contracts"
            || !state.SlotData!.Value.TryGetProperty("contracts", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            Remove(state.ProfileId);
            return;
        }

        Remove(state.ProfileId);
        var set = new ContractSet { ProfileId = state.ProfileId };
        var seed = $"{state.ProfileId}:{state.SeedName}:{state.SlotId}";
        foreach (var contract in list.EnumerateArray())
        {
            var index = contract.GetProperty("index").GetInt32();
            set.QuestIds[index] = Id(seed, $"quest{index}");
        }

        set.Count = set.QuestIds.Count(kv => kv.Key != 0);
        foreach (var contract in list.EnumerateArray())
        {
            try
            {
                var index = contract.GetProperty("index").GetInt32();
                var quest = BuildQuest(seed, set, contract);
                templates.Quests[quest.Id] = quest;
                set.Indexes[quest.Id] = index;
                _owners[quest.Id] = state.ProfileId;
            }
            catch (Exception ex)
            {
                logger.Error($"[Archipelago] could not build a contract: {ex.Message}");
            }
        }

        _sets[state.ProfileId] = set;
        RefreshLocales();
        logger.Info($"[Archipelago] {set.Count} contracts and the Kappa contract are ready for profile {state.ProfileId}.");
    }

    public void Remove(string profileId)
    {
        if (!_sets.TryRemove(profileId, out var set))
        {
            return;
        }

        foreach (var id in set.Indexes.Keys)
        {
            templates.Quests.Remove(id);
            _owners.TryRemove(id, out _);
        }
    }

    private void RefreshLocales()
    {
        foreach (var (_, lazy) in locales.Global)
        {
            lazy.Clear();
        }
    }

    private Quest BuildQuest(string seed, ContractSet set, JsonElement contract)
    {
        var data = _data!;
        var index = contract.GetProperty("index").GetInt32();
        var questId = set.QuestIds[index];
        var qid = questId.ToString();
        var title = contract.GetProperty("title").GetString() ?? $"Contract {index}";
        var client = contract.TryGetProperty("client", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
        var clientName = client != null && data.TradersByKey.TryGetValue(client, out var clientDef) ? clientDef.Name : null;

        var finish = new JsonArray();
        var lines = new List<string>();
        var kinds = new HashSet<string>();
        var n = 0;
        foreach (var objective in contract.GetProperty("objectives").EnumerateArray())
        {
            var condId = Id(seed, $"quest{index}:cond{n}").ToString();
            var (node, text, kind) = Objective(seed, index, n, condId, objective);
            finish.Add(node);
            _text[condId] = text;
            lines.Add("- " + text);
            kinds.Add(kind);
            n++;
        }

        var start = new JsonArray
        {
            new JsonObject
            {
                ["id"] = Id(seed, $"quest{index}:level").ToString(), ["conditionType"] = "Level", ["compareMethod"] = ">=",
                ["value"] = 1, ["index"] = 0, ["parentId"] = "", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "",
                ["visibilityConditions"] = new JsonArray(),
            },
        };
        var after = new List<string>();
        foreach (var prereq in contract.GetProperty("prereqs").EnumerateArray())
        {
            var p = prereq.GetInt32();
            if (!set.QuestIds.TryGetValue(p, out var prereqId))
            {
                continue;
            }

            start.Add(new JsonObject
            {
                ["id"] = Id(seed, $"quest{index}:after{p}").ToString(), ["conditionType"] = "Quest", ["target"] = prereqId.ToString(),
                ["status"] = new JsonArray(4), ["availableAfter"] = 0, ["dispersion"] = 0, ["index"] = start.Count,
                ["parentId"] = "", ["dynamicLocale"] = false, ["globalQuestCounterId"] = "", ["visibilityConditions"] = new JsonArray(),
            });
            after.Add($"Contract {p}");
        }

        var rewards = new JsonArray();
        var xp = contract.TryGetProperty("xp", out var x) ? x.GetInt32() : 0;
        if (xp > 0)
        {
            rewards.Add(Reward(seed, index, "xp", "Experience", xp));
        }

        var standing = contract.TryGetProperty("standing", out var s) ? s.GetDouble() : 0;
        if (standing > 0 && client != null && data.TradersByKey.TryGetValue(client, out var standingTrader))
        {
            var r = Reward(seed, index, "standing", "TraderStanding", standing);
            r["target"] = standingTrader.Id;
            rewards.Add(r);
        }

        var roubles = contract.TryGetProperty("roubles", out var rb) ? rb.GetInt32() : 0;
        if (roubles > 0)
        {
            rewards.Add(ItemReward(seed, index, "roubles", Roubles, roubles, foundInRaid: false));
        }

        if (index == 0)
        {
            rewards.Add(ItemReward(seed, index, "kappa", KappaTpl, 1, foundInRaid: true));
        }

        var description = new StringBuilder();
        if (index == 0)
        {
            description.AppendLine($"The last contract. Every other contract ({set.Count}) must be done first.");
            description.AppendLine("Hand over these valuables, found in raid, and the Kappa container is yours.");
        }
        else
        {
            description.AppendLine(clientName != null
                ? $"Somebody in the multiworld is paying for this, and {clientName} vouches for you when it's done."
                : "Somebody in the multiworld is paying for this.");
            if (after.Count > 0)
            {
                description.AppendLine($"Offered after {string.Join(" and ", after)}.");
            }
        }

        description.AppendLine();
        description.Append(string.Join("\n", lines));
        var name = index == 0 ? "Contract - Kappa" : title;
        var descriptionText = description.ToString();
        // Name and description are their own locale keys: a client that loaded its locale before these contracts
        // existed still shows the text.
        _text[name] = name;
        _text[descriptionText] = descriptionText;
        foreach (var key in new[] { "successMessageText", "failMessageText", "startedMessageText", "acceptPlayerMessage",
                     "declinePlayerMessage", "completePlayerMessage", "changeQuestMessageText", "note" })
        {
            _text[$"{qid} {key}"] = key switch
            {
                "successMessageText" => index == 0 ? "That's all of them. The Kappa container is yours." : "Contract done. Payment sent.",
                "startedMessageText" => "Contract accepted.",
                _ => "",
            };
        }

        var type = kinds.Count > 1 ? "Multi" : kinds.FirstOrDefault() ?? "Completion";
        var questJson = new JsonObject
        {
            ["_id"] = qid,
            ["QuestName"] = name,
            ["name"] = name,
            ["description"] = descriptionText,
            ["note"] = $"{qid} note",
            ["successMessageText"] = $"{qid} successMessageText",
            ["failMessageText"] = $"{qid} failMessageText",
            ["startedMessageText"] = $"{qid} startedMessageText",
            ["acceptPlayerMessage"] = $"{qid} acceptPlayerMessage",
            ["declinePlayerMessage"] = $"{qid} declinePlayerMessage",
            ["completePlayerMessage"] = $"{qid} completePlayerMessage",
            ["changeQuestMessageText"] = $"{qid} changeQuestMessageText",
            ["acceptanceAndFinishingSource"] = "eft",
            ["progressSource"] = "eft",
            ["arenaLocations"] = new JsonArray(),
            ["gameModes"] = new JsonArray(),
            ["rankingModes"] = new JsonArray(),
            ["canShowNotificationsInGame"] = true,
            ["image"] = "/files/quest/icon/596b465486f77457ca186188.jpg",
            ["instantComplete"] = false,
            ["isKey"] = index == 0,
            ["location"] = "any",
            ["restartable"] = false,
            ["secretQuest"] = false,
            ["side"] = "Pmc",
            ["status"] = 0,
            ["traderId"] = TraderId,
            ["type"] = type,
            ["conditions"] = new JsonObject
            {
                ["AvailableForStart"] = start,
                ["AvailableForFinish"] = finish,
                ["Fail"] = new JsonArray(),
            },
            ["rewards"] = new JsonObject
            {
                ["Started"] = new JsonArray(),
                ["Success"] = rewards,
                ["Fail"] = new JsonArray(),
            },
        };

        return json.Deserialize<Quest>(questJson.ToJsonString())
               ?? throw new InvalidOperationException($"contract {index} could not be read back");
    }

    private (JsonObject Node, string Text, string Kind) Objective(string seed, int index, int n, string condId, JsonElement o)
    {
        var data = _data!;
        var type = o.GetProperty("type").GetString();
        var count = o.TryGetProperty("count", out var cnt) ? cnt.GetInt32() : 1;
        string MapName(string key) => data.MapsByKey.TryGetValue(key, out var m) ? m.Name : key;

        switch (type)
        {
            case "kill":
            case "boss":
            {
                string? map = o.TryGetProperty("map", out var mp) && mp.ValueKind == JsonValueKind.String ? mp.GetString() : null;
                var target = "Savage";
                var roles = new JsonArray();
                string text;
                if (type == "boss")
                {
                    var role = o.GetProperty("role").GetString() ?? "";
                    roles.Add(role);
                    var bossName = o.TryGetProperty("name", out var bn) ? bn.GetString() : role;
                    var maps = data.BossesByRole.TryGetValue(role, out var boss) ? boss.Maps.Select(MapName) : [];
                    text = $"Eliminate {bossName} ({string.Join(", ", maps)})";
                }
                else
                {
                    var who = o.GetProperty("target").GetString();
                    target = who switch { "pmc" => "AnyPmc", "any" => "Any", _ => "Savage" };
                    var noun = who switch
                    {
                        "pmc" => count == 1 ? "PMC operative" : "PMC operatives",
                        "any" => count == 1 ? "enemy (Scav or PMC)" : "enemies (Scavs or PMCs)",
                        _ => count == 1 ? "Scav" : "Scavs",
                    };
                    text = $"Eliminate {count} {noun} " + (map != null ? $"on {MapName(map)}" : "anywhere");
                }

                var counterConditions = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = Id(seed, $"quest{index}:cond{n}:kills").ToString(), ["conditionType"] = "Kills", ["target"] = target,
                        ["value"] = 1, ["compareMethod"] = ">=", ["savageRole"] = roles, ["bodyPart"] = new JsonArray(),
                        ["daytime"] = new JsonObject { ["from"] = 0, ["to"] = 0 },
                        ["distance"] = new JsonObject { ["compareMethod"] = ">=", ["value"] = 0 },
                        ["dynamicLocale"] = false, ["enemyEquipmentExclusive"] = new JsonArray(),
                        ["enemyEquipmentInclusive"] = new JsonArray(), ["enemyHealthEffects"] = new JsonArray(),
                        ["resetOnSessionEnd"] = false, ["weapon"] = new JsonArray(), ["weaponCaliber"] = new JsonArray(),
                        ["weaponModsExclusive"] = new JsonArray(), ["weaponModsInclusive"] = new JsonArray(),
                    },
                };
                if (map != null)
                {
                    counterConditions.Add(LocationCondition(seed, index, n, map));
                }

                return (Counter(seed, index, n, condId, "Elimination", count, counterConditions), text, "Elimination");
            }
            case "extract":
            {
                var map = o.GetProperty("map").GetString() ?? "";
                var conditions = new JsonArray
                {
                    LocationCondition(seed, index, n, map),
                    new JsonObject
                    {
                        ["id"] = Id(seed, $"quest{index}:cond{n}:exit").ToString(), ["conditionType"] = "ExitStatus",
                        ["status"] = new JsonArray("Survived", "Runner"), ["dynamicLocale"] = false,
                    },
                };
                var text = $"Survive and extract from {MapName(map)}" + (count > 1 ? $" ({count} times)" : "");
                return (Counter(seed, index, n, condId, "Exploration", count, conditions), text, "Exploration");
            }
            case "handover":
            {
                var tpl = o.GetProperty("tpl").GetString() ?? "";
                var name = ItemName(tpl);
                var node = new JsonObject
                {
                    ["id"] = condId, ["conditionType"] = "HandoverItem", ["target"] = new JsonArray(tpl), ["value"] = count,
                    ["onlyFoundInRaid"] = true, ["dogtagLevel"] = 0, ["isEncoded"] = false, ["maxDurability"] = 100,
                    ["minDurability"] = 0, ["index"] = n, ["parentId"] = "", ["dynamicLocale"] = false,
                    ["globalQuestCounterId"] = "", ["visibilityConditions"] = new JsonArray(),
                };
                return (node, $"Hand over {count} found in raid {name}", "PickUp");
            }
            default:
                throw new InvalidOperationException($"unknown contract objective '{type}'");
        }
    }

    private JsonObject LocationCondition(string seed, int index, int n, string mapKey)
    {
        var ids = _data!.MapsByKey.TryGetValue(mapKey, out var map) ? map.LocationIds : [mapKey];
        return new JsonObject
        {
            ["id"] = Id(seed, $"quest{index}:cond{n}:location").ToString(), ["conditionType"] = "Location",
            ["target"] = new JsonArray(ids.Select(i => (JsonNode)i).ToArray()), ["dynamicLocale"] = false,
        };
    }

    private static JsonObject Counter(string seed, int index, int n, string condId, string type, int value, JsonArray conditions) => new()
    {
        ["id"] = condId, ["conditionType"] = "CounterCreator", ["type"] = type, ["value"] = value, ["index"] = n,
        ["completeInSeconds"] = 0, ["doNotResetIfCounterCompleted"] = false, ["dynamicLocale"] = false,
        ["globalQuestCounterId"] = "", ["isNecessary"] = false, ["isResetOnConditionFailed"] = false,
        ["oneSessionOnly"] = false, ["parentId"] = "", ["visibilityConditions"] = new JsonArray(),
        ["counter"] = new JsonObject { ["id"] = Id(seed, $"quest{index}:cond{n}:counter").ToString(), ["conditions"] = conditions },
    };

    private static JsonObject Reward(string seed, int index, string key, string type, double value) => new()
    {
        ["id"] = Id(seed, $"quest{index}:reward:{key}").ToString(), ["type"] = type, ["value"] = value,
        ["availableInGameEditions"] = new JsonArray(), ["gameMode"] = new JsonArray("regular", "pve"),
        ["isHidden"] = false, ["unknown"] = false,
    };

    private static JsonObject ItemReward(string seed, int index, string key, string tpl, int count, bool foundInRaid)
    {
        var itemId = Id(seed, $"quest{index}:reward:{key}:item").ToString();
        var reward = Reward(seed, index, key, "Item", count);
        var upd = new JsonObject { ["StackObjectsCount"] = count };
        if (foundInRaid)
        {
            upd["SpawnedInSession"] = true;
        }

        reward["target"] = itemId;
        reward["findInRaid"] = foundInRaid;
        reward["isEncoded"] = false;
        reward["items"] = new JsonArray(new JsonObject { ["_id"] = itemId, ["_tpl"] = tpl, ["upd"] = upd });
        return reward;
    }

    private string ItemName(string tpl)
    {
        try
        {
            return itemHelper.GetItemName(new MongoId(tpl));
        }
        catch
        {
            return tpl;
        }
    }

    /// <summary>A stable 24-hex-digit id for one part of one profile's contracts.</summary>
    private static MongoId Id(string seed, string part)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"archipelago-contract:{seed}:{part}"));
        return new MongoId(Convert.ToHexString(hash, 0, 12).ToLowerInvariant());
    }
}

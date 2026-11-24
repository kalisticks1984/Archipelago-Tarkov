using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArchipelagoTarkov.Ap;
using ArchipelagoTarkov.Data;
using ArchipelagoTarkov.Game;
using ArchipelagoTarkov.State;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Enums;

namespace ArchipelagoTarkov.Session;

/// <summary>
/// One SPT profile linked to one Archipelago slot. Every public method is called with the session lock
/// held, either from the SPT update loop or from an HTTP request (chat commands, patches, plugin route).
/// </summary>
public sealed partial class ApSession
{
    public const int DataVersion = 1;
    public const int MaxClientMessages = 30;
    private const int MaxDeliveryAttempts = 3;
    private const int MaxDeliveriesPerUpdate = 25;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(30);

    private readonly TarkovData _data;
    private readonly GameBridge _game;
    private readonly StateStore _store;
    private readonly NameBook _names;
    private readonly Action<string> _log;
    private readonly ApClient _client = new();

    private Task<bool>? _openTask;
    private DateTime _nextConnectAttempt = DateTime.MinValue;
    private DateTime _openedAt;
    private int _failedAttempts;
    private bool _dirty;
    private bool _profileDirty;
    private bool _profileSaveFailed;
    private bool _profileChanged;
    private bool _reachedConnected;
    private bool _firstConnectOfGame;
    private bool _goalSentThisConnection;
    private int _deliveryFailures;
    private readonly Dictionary<long, DateTime> _inflightChecks = new();
    private readonly List<string> _chatLines = new();
    private readonly Queue<string> _clientMessages = new();
    private readonly HashSet<string> _unlockedTraders = new();
    private readonly HashSet<string> _unlockedMaps = new();
    private bool _fleaUnlocked;
    private bool _deathCausedByLink;

    public ApSession(ProfileState state, TarkovData data, GameBridge game, StateStore store, NameBook names, Action<string> log)
    {
        State = state;
        _data = data;
        _game = game;
        _store = store;
        _names = names;
        _log = log;
        RecomputeUnlocks();
    }

    public ProfileState State { get; }
    public string ProfileId => State.ProfileId;
    public ApConnectionState ConnectionState => _client.State;
    public bool InRaid { get; private set; }
    public string? RaidMap { get; private set; }
    public DateTime RaidStarted { get; private set; }
    public bool PendingDeathLinkKill { get; private set; }
    public string? PendingDeathLinkCause { get; private set; }

    /// <summary>True once the slot is known: only then does the mod lock anything on this profile.</summary>
    public bool Active => State.HasSlotData;

    public bool IsMapUnlocked(string mapKey) => !Active || _unlockedMaps.Contains(mapKey);
    public bool IsTraderUnlocked(string traderKey) => !Active || _unlockedTraders.Contains(traderKey);

    /// <summary>Main traders whose item has been received (their keys).</summary>
    public IReadOnlySet<string> UnlockedTraders => _unlockedTraders;
    public bool FleaLocked => Active && State.SlotFlag("lock_flea_market", true) && !_fleaUnlocked;
    public bool DeathLinkEnabled => Active && State.SlotFlag("death_link");

    // ------------------------------------------------------------------ lootsanity
    /// <summary>Item types that already got their one Archipelago Item this raid.</summary>
    public HashSet<string> LootPlacedThisRaid { get; } = new();
    /// <summary>Where this raid's Archipelago Items are, for the tracker ("Salewa first aid kit: in a Jacket").</summary>
    public List<string> LootPlacesThisRaid { get; } = new();

    public void RecordLootPlacement(LootDef def, string place) => LootPlacesThisRaid.Add($"{def.ItemName}: {place}");
    public Random LootRandom { get; } = new();

    /// <summary>True when stand-ins should spawn on this map: the slot has loot checks and the map is unlocked.</summary>
    public bool LootsanityOn(string? mapKey) =>
        LootStandIns.Ready && Active && mapKey != null && IsMapUnlocked(mapKey) && State.SlotLocations.Any(id => _data.LootLocationIds.Contains(id));

    /// <summary>The loot check for an item type, while it is still to be found; null otherwise.</summary>
    public LootDef? LootTarget(string tpl) =>
        _data.LootByTpl.TryGetValue(tpl, out var def) && State.SlotLocations.Contains(def.LocationId)
        && !State.Checked.Contains(def.LocationId) && !State.LootFound.Contains(tpl)
            ? def
            : null;

    // ------------------------------------------------------------------ tracker
    /// <summary>
    /// The checks still to do, by map (only unlocked maps; the current raid's map first), for the client plugin's
    /// tracker window. During a raid it lists where this raid's Archipelago Items are.
    /// </summary>
    /// <summary>Contracts taken and not turned in yet, with the maps their objectives are on (none: any map).</summary>
    private IEnumerable<(string Name, List<string> Maps)> ContractsInProgress(Func<long, bool> pending)
    {
        var set = Contracts.Instance?.For(ProfileId);
        if (set is null || !State.HasSlotData || !State.SlotData!.Value.TryGetProperty("contracts", out var list))
        {
            yield break;
        }

        foreach (var contract in list.EnumerateArray())
        {
            var index = contract.GetProperty("index").GetInt32();
            if (!set.QuestIds.TryGetValue(index, out var questId) || !_data.ContractLocations.TryGetValue(index, out var location)
                || !pending(location)
                || _game.QuestState(ProfileId, questId.ToString()) is not (QuestStatusEnum.Started or QuestStatusEnum.AvailableForFinish))
            {
                continue;
            }

            var maps = new List<string>();
            foreach (var o in contract.GetProperty("objectives").EnumerateArray())
            {
                if (o.TryGetProperty("map", out var m) && m.ValueKind == JsonValueKind.String)
                {
                    maps.Add(m.GetString()!);
                }
                else if (o.TryGetProperty("maps", out var bm) && bm.ValueKind == JsonValueKind.Array)
                {
                    maps.AddRange(bm.EnumerateArray().Select(x => x.GetString()!));
                }
            }

            yield return (index == 0 ? "Contract - Kappa" : contract.GetProperty("title").GetString() ?? $"Contract {index}", maps);
        }
    }

    public TrackerResponse BuildTracker()
    {
        var response = new TrackerResponse { Linked = Active, RaidMap = InRaid ? RaidMap : null };
        if (!Active)
        {
            return response;
        }

        var detected = _game.DetectChecks(ProfileId, _data, State).ToHashSet();
        bool Pending(long id) => State.SlotLocations.Contains(id) && !State.Checked.Contains(id) && !detected.Contains(id);

        var questsByMap = new Dictionary<string, List<string>>();
        var questsAnywhere = new List<string>();
        void AddQuest(string name, IEnumerable<string> questMaps)
        {
            var list = questMaps.Distinct().ToList();
            if (list.Count == 0 || list.Contains("*"))
            {
                questsAnywhere.Add(name);
                return;
            }

            foreach (var map in list)
            {
                if (!questsByMap.TryGetValue(map, out var names))
                {
                    questsByMap[map] = names = new List<string>();
                }

                names.Add(name);
            }
        }

        foreach (var (name, contractMaps) in ContractsInProgress(Pending))
        {
            AddQuest(name, contractMaps);
        }

        foreach (var q in _data.Quests)
        {
            if (q.LocationId is not { } id || !Pending(id))
            {
                continue;
            }

            var status = _game.QuestState(ProfileId, q.Id);
            if (status is not (QuestStatusEnum.Started or QuestStatusEnum.AvailableForFinish))
            {
                continue; // only quests you have taken: the rest can't be done in this raid anyway
            }

            AddQuest(q.LocationName ?? q.Name, q.ObjectiveMaps.SelectMany(m => m));
        }

        var maps = _data.Maps.Where(m => IsMapUnlocked(m.Key))
            .OrderByDescending(m => InRaid && m.Key == RaidMap).ThenBy(m => m.Name);
        foreach (var map in maps)
        {
            var current = InRaid && map.Key == RaidMap;
            var groups = new List<TrackerGroup>();
            var quests = questsByMap.GetValueOrDefault(map.Key) ?? [];
            groups.Add(new TrackerGroup("Quests in progress", quests));
            groups.Add(new TrackerGroup("Extractions", _data.Extracts
                .Where(e => e.Map == map.Key && Pending(e.LocationId)).Select(e => e.Name).ToList()));
            groups.Add(new TrackerGroup("Bosses", _data.Bosses
                .Where(b => b.Maps.Contains(map.Key) && Pending(b.LocationId)).Select(b => b.Name.Replace("Boss - ", "")).ToList()));
            if (current && LootPlacesThisRaid.Count > 0)
            {
                groups.Add(new TrackerGroup("Archipelago Items this raid", LootPlacesThisRaid.ToList()));
            }
            else
            {
                var lootHere = _data.Loot.Count(l => l.Maps.Contains(map.Key) && Pending(l.LocationId));
                groups.Add(new TrackerGroup("Loot checks that can spawn here", [], lootHere));
            }

            groups.RemoveAll(g => g.Count == 0);
            response.Maps.Add(new TrackerMap
            {
                Key = map.Key, Name = map.Name, Current = current, Total = groups.Sum(g => g.Count), Groups = groups,
            });
        }

        var anywhere = new List<TrackerGroup>();
        var kills = new List<string>();
        foreach (var target in _data.Kills.Select(k => k.Target).Distinct())
        {
            var next = _data.Kills.Where(k => k.Target == target && Pending(k.LocationId)).OrderBy(k => k.Count).FirstOrDefault();
            if (next != null)
            {
                kills.Add($"{next.Name.Replace("Kills - ", "")} ({State.KillCounts.GetValueOrDefault(target)}/{next.Count})");
            }
        }

        anywhere.Add(new TrackerGroup("Next kill milestones", kills));
        anywhere.Add(new TrackerGroup("Quests in progress (any map)", questsAnywhere));
        anywhere.Add(new TrackerGroup("Loot checks carried by bots (any map)", [],
            _data.Loot.Count(l => l.Maps.Count == 0 && Pending(l.LocationId))));
        anywhere.Add(new TrackerGroup("Hideout levels to build", [], _data.Hideout.Count(h => Pending(h.LocationId))));
        anywhere.Add(new TrackerGroup("Recipes to craft", [], _data.Crafts.Count(c => Pending(c.LocationId))));
        anywhere.RemoveAll(g => g.Count == 0);
        response.Anywhere = anywhere;
        return response;
    }

    // ------------------------------------------------------------------ craftsanity
    /// <summary>The craft check for a recipe, while its first craft is still to come; null otherwise.</summary>
    public CraftDef? CraftTarget(string recipeId) =>
        Active && _data.CraftsByRecipe.TryGetValue(recipeId, out var def) && State.SlotLocations.Contains(def.LocationId)
        && !State.Checked.Contains(def.LocationId) && !State.CraftsDone.Contains(recipeId)
            ? def
            : null;

    /// <summary>The first craft of a recipe was collected (its product already taken away): record the check.</summary>
    public void RecordCraft(CraftDef craft)
    {
        if (State.CraftsDone.Add(craft.Recipe))
        {
            _dirty = true;
            Say($"Craft check: {craft.Name.Replace("Craft - ", "")}. The first craft of each recipe goes to the multiworld.");
        }
    }

    // ------------------------------------------------------------------ shop randomizer
    /// <summary>"vanilla", "same_category" or "anything".</summary>
    public string ShopMode => Active ? State.SlotString("shop_randomizer") ?? "vanilla" : "vanilla";
    public bool ShopEveryRestock => State.SlotString("shop_reshuffle") == "every_restock";
    public int ShopSeed => State.SlotInt("shop_seed");
    public int ShopStock => State.SlotInt("shop_stock", 100);
    public bool IsGatedTrader(string traderId) => _data.TradersById.ContainsKey(traderId);

    /// <summary>
    /// Raid end: takes every Archipelago Item out of the client's profile and records the ones brought out (all when
    /// the player survived, only the secure container's when not). Nothing happens on a transit: they travel along.
    /// </summary>
    public void CollectLoot(string? mapKey, PmcData? profile, ExitStatus? result)
    {
        if (result == ExitStatus.TRANSIT || profile is null)
        {
            return;
        }

        var survived = result is ExitStatus.SURVIVED or ExitStatus.RUNNER;
        var counted = LootStandIns.TakeStandIns(profile.Inventory, _data, survived);
        if (counted.Count == 0 || !Active || mapKey is null || !IsMapUnlocked(mapKey))
        {
            return;
        }

        var added = counted.Where(def => State.LootFound.Add(def.Tpl)).Select(def => def.ItemName).ToList();
        if (added.Count > 0)
        {
            _dirty = true;
            Say(added.Count == 1
                ? $"Archipelago Item brought out: {added[0]}."
                : $"{added.Count} Archipelago Items brought out: {string.Join(", ", added.Take(8))}{(added.Count > 8 ? "..." : "")}.");
        }
    }

    // ================================================================== commands
    /// <summary>Save connection settings and connect. Returns an error message instead when not allowed.</summary>
    public string? Configure(string server, string slot, string? password)
    {
        if (State.BoundSlotName != null && !string.Equals(State.BoundSlotName, slot, StringComparison.Ordinal))
        {
            return $"This profile is linked to the slot '{State.BoundSlotName}'. Use a new SPT profile for another slot, " +
                   "or send 'reset confirm' first to wipe this profile's Archipelago progress.";
        }

        State.Server = server;
        State.Slot = slot;
        State.Password = password;
        State.AutoConnect = true;
        _failedAttempts = 0;
        _nextConnectAttempt = DateTime.MinValue;
        _client.Close();
        _openTask = null;
        _reachedConnected = false;
        Save();
        return null;
    }

    public void Disconnect()
    {
        State.AutoConnect = false;
        _client.Close();
        _openTask = null;
        _reachedConnected = false;
        Save();
    }

    public void ResetGame()
    {
        _client.Close();
        _openTask = null;
        _game.RemoveLocks(ProfileId, _data);
        State.ResetGame(); // also stops auto-connecting, so the same game is not re-linked by accident
        Contracts.Instance?.Remove(ProfileId);
        RecomputeUnlocks();
        _inflightChecks.Clear();
        _reachedConnected = false;
        _deliveryFailures = 0;
        _profileChanged = true;
        SaveAll();
    }

    public void SendChat(string text)
    {
        if (_client.State == ApConnectionState.Connected)
        {
            _client.Send(ApClient.SayPacket(text));
        }
        else
        {
            Say("Not connected to Archipelago.");
        }
    }

    public string StatusText()
    {
        var sb = new StringBuilder();
        if (!State.IsConfigured)
        {
            sb.AppendLine("Not set up. Send: connect <server:port> <slot name> [password]");
            return sb.ToString().TrimEnd();
        }

        sb.AppendLine($"Slot {State.Slot} @ {State.Server}: {DescribeConnection()}");
        if (Active)
        {
            var mapNames = _data.Maps.Where(m => _unlockedMaps.Contains(m.Key)).Select(m => m.Name);
            var traderNames = _data.Traders.Where(t => _unlockedTraders.Contains(t.Key)).Select(t => t.Name);
            sb.AppendLine($"Maps: {string.Join(", ", mapNames)}");
            sb.AppendLine($"Traders: {string.Join(", ", traderNames)}");
            sb.AppendLine($"Flea Market: {(FleaLocked ? "locked" : "open")}");
            var total = State.SlotLocations.Count;
            var done = State.Checked.Count(c => State.SlotLocations.Contains(c));
            sb.AppendLine($"Checks: {done}/{total}");
            sb.AppendLine($"Goal: {GoalDescription()}");
        }

        return sb.ToString().TrimEnd();
    }

    private string DescribeConnection() => _client.State switch
    {
        ApConnectionState.Connected => "connected",
        ApConnectionState.Connecting or ApConnectionState.WaitingForRoomInfo or ApConnectionState.Authenticating => "connecting...",
        _ => State.AutoConnect
            ? $"disconnected, retrying{(_client.LastError != null ? $" ({_client.LastError})" : "")}"
            : "disconnected",
    };

    public string ItemsText()
    {
        if (State.Received.Count == 0)
        {
            return "No items received yet.";
        }

        var groups = State.Received.GroupBy(r => r.Item)
            .Select(g => (Name: _data.ItemsById.TryGetValue(g.Key, out var d) ? d.Name : $"Item {g.Key}", Count: g.Count()))
            .OrderBy(x => x.Name);
        return "Received so far:\n" + string.Join("\n", groups.Select(g => g.Count > 1 ? $"{g.Name} x{g.Count}" : g.Name));
    }

    private string GoalDescription()
    {
        return State.SlotString("goal") switch
        {
            "collector" => _game.QuestCompleted(ProfileId, _data.CollectorQuestId) ? "Collector done" : "complete Fence's Collector quest",
            "kappa_contract" => KappaContractDone() ? "Kappa contract done"
                : $"finish the Kappa contract ({ContractProgress().Done}/{ContractProgress().Total} contracts done)",
            "all_extracts" => $"extract from every map ({_data.Maps.Count(m => State.ExtractCounts.GetValueOrDefault(m.Key) > 0)}/{_data.Maps.Count})",
            "bosses" => $"kill every boss ({_data.Bosses.Count(b => State.BossesKilled.Contains(b.Key))}/{_data.Bosses.Count})",
            _ => $"complete {State.SlotInt("goal_quest_count")} quests ({CompletedQuestChecks()} done)",
        };
    }

    // ================================================================== raid hooks
    public void OnRaidStart(string? mapKey)
    {
        InRaid = true;
        RaidMap = mapKey;
        RaidStarted = DateTime.UtcNow;
        LootPlacedThisRaid.Clear();
        LootPlacesThisRaid.Clear();
        if (State.StartingKitPending == "replace")
        {
            // Never wipe a stash that has seen a raid: hand the kit over on top instead.
            State.StartingKitPending = "extra";
            _dirty = true;
            Say("You started a raid before restarting the game, so your stash is kept. Your random starting kit comes as a message instead.");
        }
        _deathCausedByLink = false;
        PendingDeathLinkKill = false;
        if (Active && mapKey != null && !IsMapUnlocked(mapKey))
        {
            Say($"Warning: {_data.MapsByKey[mapKey].Name} is locked in your Archipelago game. Extractions here won't count. Restart the game if the map list looks out of date.");
        }
    }

    /// <summary>Called before SPT copies the client's profile at raid end.</summary>
    public void BeforeRaidEnd(PmcData? postRaidProfile, bool isPmc)
    {
        // SPT 4.1 ends a raid asynchronously: stay "in raid" (profile changes wait) until OnRaidEnd, so nothing
        // lands between this point and SPT copying the client's profile in. Also covers a server restarted mid-raid.
        if (!InRaid)
        {
            InRaid = true;
            RaidStarted = DateTime.UtcNow;
        }

        // Not tied to Active: these are changes already made to the SPT profile (they survive a reset).
        _game.AddPendingToRaidResult(postRaidProfile, State, isPmc);
    }

    /// <summary>
    /// Counts the raid's victims towards the kill milestones and boss checks. Called with the victim list the client
    /// sent at raid end (PMC or Scav raid, any exit status, including each leg of a transit).
    /// </summary>
    public void RecordVictims(string? mapKey, IEnumerable<Victim>? victims)
    {
        if (!Active || victims is null || mapKey is null || !IsMapUnlocked(mapKey))
        {
            return;
        }

        foreach (var victim in victims)
        {
            if (victim is null)
            {
                continue;
            }

            // Bot profile ids are unique per spawn. Fall back to what identifies the kill in the client's list.
            var id = victim.ProfileId?.ToString() ?? $"{victim.Name}|{victim.Time}|{victim.Role}";
            if (!State.RaidVictimsCounted.Add(id))
            {
                continue;
            }

            if (KillTargets.ForSide(victim.Side) is { } target)
            {
                State.KillCounts[target] = State.KillCounts.GetValueOrDefault(target) + 1;
            }

            if (victim.Role is { } role && _data.BossesByRole.TryGetValue(role, out var boss) && State.BossesKilled.Add(boss.Key))
            {
                Say($"{boss.Name.Replace("Boss - ", "")} killed.");
            }

            _dirty = true;
        }
    }

    private DateTime _profileChangesPausedUntil = DateTime.MinValue;

    /// <summary>Hold back experience/reputation/trap changes for a while (the client is reading the profile).</summary>
    public void PauseProfileChanges(TimeSpan duration) => _profileChangesPausedUntil = DateTime.UtcNow + duration;

    private bool ProfileChangesPaused => InRaid || DateTime.UtcNow < _profileChangesPausedUntil;

    public void OnRaidEnd(string? mapKey, bool isPmc, ExitStatus? result, string? killerName)
    {
        PendingDeathLinkKill = false;
        if (result == ExitStatus.TRANSIT)
        {
            // Moving to another map: still in raid. Profile effects keep waiting.
            RaidStarted = DateTime.UtcNow;
            return;
        }

        InRaid = false;
        if (State.RaidVictimsCounted.Count > 0)
        {
            State.RaidVictimsCounted.Clear();
            _dirty = true;
        }

        if (!Active)
        {
            return;
        }

        KeepGearOnce = false;
        if (isPmc)
        {
            State.PmcRaids++;
            _dirty = true;
        }

        var survived = result is ExitStatus.SURVIVED or ExitStatus.RUNNER;
        var died = result is ExitStatus.KILLED or ExitStatus.MISSINGINACTION;
        if (isPmc && survived && mapKey != null && IsMapUnlocked(mapKey))
        {
            State.ExtractCounts[mapKey] = State.ExtractCounts.GetValueOrDefault(mapKey) + 1;
            _dirty = true;
        }

        if (isPmc && died && DeathLinkEnabled)
        {
            if (_deathCausedByLink)
            {
                _deathCausedByLink = false;
            }
            else if (_client.State == ApConnectionState.Connected)
            {
                var place = mapKey != null && _data.MapsByKey.TryGetValue(mapKey, out var m) ? m.Name : "Tarkov";
                var by = string.IsNullOrWhiteSpace(killerName) ? "" : $" by {killerName}";
                _client.Send(ApClient.DeathLinkPacket(State.Slot ?? "Tarkov", $"{State.Slot} was killed{by} on {place}."));
            }
        }

        ApplyDeferred();
        _game.RecalculateLevels(ProfileId);
        // The extraction count is the only record of an extraction: write it now.
        _profileChanged = true;
        SaveAll();
    }

    /// <summary>The game client just started (logged in): it can't be in a raid.</summary>
    public void OnGameStarted()
    {
        InRaid = false;
        PendingDeathLinkKill = false;
    }

    /// <summary>
    /// The game client is about to load the whole profile: the one safe moment to change the stash, because the
    /// client takes the server's copy as it is. Used for the "replace" starting kit.
    /// </summary>
    public void BeforeClientLoadsProfiles()
    {
        if (State.StartingKitPending == "replace")
        {
            GiveStartingKit(clearInventory: true);
            SaveAll();
        }
    }

    /// <summary>One-time repair for profiles that received items with version 1.1.0 or older.</summary>
    private void MoveStuckAttachments()
    {
        try
        {
            var moved = _game.ResendStuckAttachments(ProfileId);
            if (moved > 0)
            {
                _profileChanged = true;
                _log($"[Archipelago] re-sent {moved} stuck deliveries as system messages for {ProfileId}");
                Say($"{moved} earlier deliver{(moved == 1 ? "y" : "ies")} could not be collected from this chat. " +
                    "They have been sent again as System messages: open the System chat and press Receive.");
            }
        }
        catch (Exception ex)
        {
            _log($"[Archipelago] could not re-send stuck deliveries for {ProfileId}: {ex}");
        }

        State.MailVersion = 2;
        _dirty = true;
    }

    private void PlanStartingKit()
    {
        var mode = State.SlotString("starting_kit");
        if (State.StartingKitGiven || mode is not ("extra" or "replace"))
        {
            return;
        }

        if (mode == "replace" && !_game.IsFreshProfile(ProfileId))
        {
            mode = "extra";
            Say("This profile has already been played, so its stash is kept. Your random starting kit comes as a message instead.");
        }
        else if (mode == "replace" && !Patches.PatchInstaller.IsReady(typeof(Patches.ProfileListPatch)))
        {
            mode = "extra";
        }
        else if (mode == "replace")
        {
            Say("Random starting kit: when you restart the game, your starting stash and gear are replaced by your kit " +
                "(it arrives as a message from this contact). Restart before your first raid.");
        }

        State.StartingKitPending = mode;
        _dirty = true;
    }

    private void GiveStartingKit(bool clearInventory)
    {
        var quality = State.SlotString("starting_kit_quality") ?? "standard";
        try
        {
            // Build the kit before touching the stash, so a failure never leaves the player with nothing.
            var rng = new Random(StableSeed(State.SeedName ?? "", -1, State.SlotId));
            var items = _game.CreateStartingKit(quality, rng, out var contents);
            var removed = clearInventory ? _game.ClearStartingInventory(ProfileId) : 0;
            var intro = clearInventory
                ? $"Your random starting kit ({quality}). Your old starting stash ({removed} items) was removed."
                : $"Your random starting kit ({quality}).";
            _game.SendItems(ProfileId, intro + "\n- " + string.Join("\n- ", contents), items);
            _chatLines.Add("Your random starting kit is in your System messages: open it and press Receive.");
            EnqueueClientMessage("Your random starting kit is in your System messages");
            _log($"[Archipelago] starting kit for {ProfileId} ({quality}{(clearInventory ? ", replace" : "")}): {string.Join("; ", contents)}");
        }
        catch (Exception ex)
        {
            _log($"[Archipelago] could not build the starting kit for {ProfileId}: {ex}");
            _chatLines.Add($"Could not build your random starting kit ({ex.Message}). Check the SPT server console.");
        }

        State.StartingKitPending = null;
        State.StartingKitGiven = true;
        _profileChanged = true;
        _dirty = true;
    }

    /// <summary>The game client just loaded the whole profile (PMC and Scav), so it has seen every server-side change.</summary>
    public void OnClientLoadedProfiles()
    {
        if (State.HasPendingSync)
        {
            State.ClearPendingSync();
            Save();
        }
    }

    /// <summary>
    /// An inventory/trade/quest action just returned the PMC's current experience, health and trader
    /// standings to the client, which applies them.
    /// </summary>
    public void OnClientSyncedPmc()
    {
        if (State.HasPendingPmcSync)
        {
            State.ClearPendingPmcSync();
            Save();
        }
    }

    /// <summary>A new Scav was generated for the client (after a Scav raid): it copies the PMC's Fence standing.</summary>
    public void OnScavRegenerated(PmcData? scav)
    {
        if (State.PendingScavLockSeconds == 0 && State.PendingScavFence == 0)
        {
            return;
        }

        GameBridge.ExtendScavLock(scav, State.PendingScavLockSeconds);
        State.PendingScavLockSeconds = 0;
        State.PendingScavFence = 0;
        Save();
    }

    /// <summary>Called by the client plugin once it has killed the player for a DeathLink.</summary>
    public void AcknowledgeDeathLinkKill()
    {
        PendingDeathLinkKill = false;
        _deathCausedByLink = true;
    }

    public List<string> TakeClientMessages()
    {
        var list = new List<string>();
        while (_clientMessages.Count > 0)
        {
            list.Add(_clientMessages.Dequeue());
        }

        return list;
    }

    // ================================================================== main loop
    public void Update()
    {
        // Safety: a raid that never ended (game crash) should not keep the profile "in raid" forever.
        if (InRaid && DateTime.UtcNow - RaidStarted > TimeSpan.FromHours(4))
        {
            InRaid = false;
        }

        if (!_game.ProfileExists(ProfileId))
        {
            return;
        }

        if (State.MailVersion < 2)
        {
            MoveStuckAttachments();
        }

        MaintainConnection();
        while (_client.Incoming.TryDequeue(out var packet))
        {
            try
            {
                HandlePacket(packet);
            }
            catch (Exception ex)
            {
                _log($"[Archipelago] error handling {packet["cmd"]}: {ex}");
            }
        }

        if (Active)
        {
            var deliveredBefore = State.Delivered;
            DeliverItems();
            if (State.Delivered != deliveredBefore)
            {
                // Mail and profile changes live in the SPT profile, which SPT only autosaves every 60s.
                // SaveAll writes it before recording the items as delivered, so a closed server never loses an item.
                _profileChanged = true;
            }

            if (!ProfileChangesPaused)
            {
                ApplyDeferred();
                ApplyStationLevels();
            }

            if (State.StartingKitPending == "extra")
            {
                GiveStartingKit(clearInventory: false);
            }

            if (_game.EnforceLocks(ProfileId, _data, _unlockedTraders, FleaLocked, ContractsMode))
            {
                _profileChanged = true;
            }

            WarnAboutMissableKappaStep();

            if (_client.State == ApConnectionState.Connected)
            {
                SendChecks();
                CheckGoal();
            }
        }

        FlushChat();
        if (_profileDirty)
        {
            // Messages live in the SPT profile; save it so they survive the server being closed.
            _profileDirty = false;
            _game.TrimBotDialog(ProfileId);
            _profileChanged = true;
        }

        if (_profileChanged || _dirty)
        {
            SaveAll();
        }
    }

    private void MaintainConnection()
    {
        if (!State.IsConfigured || !State.AutoConnect)
        {
            return;
        }

        if (_openTask != null)
        {
            if (!_openTask.IsCompleted)
            {
                return;
            }

            var opened = _openTask.IsCompletedSuccessfully && _openTask.Result;
            _openTask = null;
            if (opened)
            {
                _openedAt = DateTime.UtcNow;
            }
            else
            {
                ScheduleRetry();
            }

            return;
        }

        var state = _client.State;
        if (state is ApConnectionState.WaitingForRoomInfo or ApConnectionState.Authenticating)
        {
            if (DateTime.UtcNow - _openedAt > HandshakeTimeout)
            {
                _client.Close();
                _ = _client; // keep analyzers quiet
                ScheduleRetry("the server did not answer the Archipelago handshake");
            }

            return;
        }

        if (state != ApConnectionState.Disconnected)
        {
            return;
        }

        if (_reachedConnected)
        {
            // We were playing and the connection dropped: reconnect quickly.
            _reachedConnected = false;
            _failedAttempts = 0;
            Say($"Lost connection to Archipelago{(_client.LastError != null ? $" ({_client.LastError})" : "")}. Reconnecting...");
            _nextConnectAttempt = DateTime.UtcNow.AddSeconds(5);
        }

        if (DateTime.UtcNow < _nextConnectAttempt)
        {
            return;
        }

        _openTask = _client.OpenAsync(State.Server!, CancellationToken.None);
    }

    private void ScheduleRetry(string? reason = null)
    {
        _failedAttempts++;
        var delay = Math.Min(60, 5 * Math.Pow(2, Math.Min(_failedAttempts - 1, 4)));
        _nextConnectAttempt = DateTime.UtcNow.AddSeconds(delay);
        if (_failedAttempts == 1 || _failedAttempts % 10 == 0)
        {
            Say($"Could not reach Archipelago at {State.Server} ({reason ?? _client.LastError ?? "no answer"}). " +
                $"Retrying every {delay:0} seconds; send 'disconnect' to stop.");
        }
    }

    private void HandlePacket(JsonObject packet)
    {
        switch (packet["cmd"]?.GetValue<string>())
        {
            case "RoomInfo":
                OnRoomInfo(packet);
                break;
            case "ConnectionRefused":
                OnConnectionRefused(packet);
                break;
            case "Connected":
                OnConnected(packet);
                break;
            case "ReceivedItems":
                OnReceivedItems(packet);
                break;
            case "RoomUpdate":
                OnRoomUpdate(packet);
                break;
            case "PrintJSON":
                OnPrintJson(packet);
                break;
            case "DataPackage":
                _names.AddDataPackage(packet["data"]?["games"] as JsonObject);
                break;
            case "Bounced":
                OnBounced(packet);
                break;
            case "InvalidPacket":
                _log($"[Archipelago] server rejected a packet: {packet["text"]}");
                break;
        }
    }

    private void Refuse(string message)
    {
        State.AutoConnect = false;
        _client.Close();
        _openTask = null;
        Save();
        Say(message);
    }

    private void OnRoomInfo(JsonObject packet)
    {
        var seed = packet["seed_name"]?.GetValue<string>();
        if (State.SeedName != null && seed != null && State.SeedName != seed)
        {
            Refuse($"This profile already belongs to a different Archipelago game (seed {State.SeedName}). " +
                   "Make a new SPT profile for the new game, or send 'reset confirm' to wipe this profile's Archipelago progress first.");
            return;
        }

        _firstConnectOfGame = State.SeedName == null;
        var missing = _names.MissingGames(packet["datapackage_checksums"] as JsonObject);
        if (missing.Count > 0)
        {
            _client.Send(ApClient.GetDataPackagePacket(missing));
        }

        _client.MarkAuthenticating();
        var tags = DeathLinkEnabled ? new[] { "DeathLink" } : Array.Empty<string>();
        _pendingSeed = seed;
        _client.Send(ApClient.ConnectPacket(State.Slot!, State.Password, $"spt-{ProfileId}", tags));
    }

    private string? _pendingSeed;

    private void OnConnectionRefused(JsonObject packet)
    {
        var errors = (packet["errors"] as JsonArray)?.Select(e => e?.GetValue<string>()).Where(e => e != null).ToList() ?? [];
        var reason = errors.Count > 0 ? string.Join(", ", errors) : "unknown reason";
        var hint = errors.Contains("InvalidSlot") ? " Check the slot name (it is case sensitive)."
            : errors.Contains("InvalidPassword") ? " Check the password."
            : errors.Contains("InvalidGame") ? " That slot is not an Escape from Tarkov slot."
            : errors.Contains("IncompatibleVersion") ? " The Archipelago server version is not compatible with this mod."
            : "";
        // Retrying with the same settings would fail the same way.
        Refuse($"Archipelago refused the connection: {reason}.{hint}");
    }

    private void OnConnected(JsonObject packet)
    {
        var team = packet["team"]?.GetValue<int>() ?? 0;
        var slot = packet["slot"]?.GetValue<int>() ?? 0;
        var slotData = packet["slot_data"] as JsonObject;

        var dataHash = slotData?["data_hash"]?.GetValue<string>();
        if (dataHash != null && !string.IsNullOrEmpty(_data.Meta.DataHash) && dataHash != _data.Meta.DataHash)
        {
            Refuse("This Archipelago game was generated with a different version of the Escape from Tarkov APWorld than " +
                   "this mod was built for, so checks and items would not match. Update the mod and the APWorld to the same release.");
            return;
        }

        if (State.BoundSlot is { } bound && (bound != slot || State.Team != team))
        {
            Refuse($"This profile is linked to the slot '{State.BoundSlotName}'. Use a new SPT profile for another slot.");
            return;
        }

        State.SeedName = _pendingSeed ?? State.SeedName;
        State.Team = team;
        State.SlotId = slot;
        State.BoundSlot = slot;
        State.BoundSlotName = State.Slot;
        _names.SetPlayers(packet["players"] as JsonArray, packet["slot_info"] as JsonObject);

        if (slotData != null)
        {
            var dataVersion = slotData["data_version"]?.GetValue<int>() ?? 0;
            if (dataVersion != DataVersion)
            {
                Say($"Warning: this game was generated with a different version of the Escape from Tarkov APWorld (data v{dataVersion}, mod v{DataVersion}).");
            }

            var hadDeathLinkTag = DeathLinkEnabled;
            State.SlotData = JsonSerializer.Deserialize<JsonElement>(slotData.ToJsonString());
            Contracts.Instance?.Build(State);
            if (DeathLinkEnabled && !hadDeathLinkTag)
            {
                // First connection: slot data was not known when Connect was sent.
                _client.Send(ApClient.ConnectUpdatePacket(["DeathLink"]));
            }
        }

        var missing = ReadLongs(packet["missing_locations"]);
        var checkedLocations = ReadLongs(packet["checked_locations"]);
        State.SlotLocations = [.. missing, .. checkedLocations];
        State.Checked = [.. checkedLocations];
        _inflightChecks.Clear();
        _client.MarkConnected();
        _failedAttempts = 0;
        _reachedConnected = true;
        _goalSentThisConnection = false;
        RecomputeUnlocks();
        Save();

        Say($"Connected to Archipelago as {State.Slot}. {State.Checked.Count}/{State.SlotLocations.Count} checks done.");
        if (_firstConnectOfGame)
        {
            PlanStartingKit();
            Say("Your profile is now linked to this Archipelago game. Maps and traders you don't have yet are locked " +
                "from now on. Restart the game once so the map list and trader screens update.");
            if (ContractsMode)
            {
                Say($"Your quests are Archipelago contracts this game: {Contracts.Instance?.For(ProfileId)?.Count ?? 0} of them, " +
                    "offered by the Archipelago trader, then the Kappa contract (your goal). Tarkov's own quests are hidden. " +
                    "Contract objectives read properly after that restart.");
            }

            if (State.SlotString("goal") == "collector")
            {
                Say("Kappa tip: accept Therapist's 'Colleagues - Part 3' while Jaeger's 'The Huntsman Path - Sadist' is " +
                    "still in progress. Once Sadist is turned in, Colleagues - Part 3 is never offered and Collector can't be finished.");
            }

            _firstConnectOfGame = false;
        }
    }

    private static List<long> ReadLongs(JsonNode? node) =>
        (node as JsonArray)?.Where(n => n != null).Select(n => n!.GetValue<long>()).ToList() ?? [];

    private void OnReceivedItems(JsonObject packet)
    {
        var index = packet["index"]?.GetValue<int>() ?? 0;
        var items = (packet["items"] as JsonArray)?.OfType<JsonObject>().Select(i => new ReceivedItem
        {
            Item = i["item"]?.GetValue<long>() ?? 0,
            Location = i["location"]?.GetValue<long>() ?? 0,
            Player = i["player"]?.GetValue<int>() ?? 0,
            Flags = i["flags"]?.GetValue<int>() ?? 0,
        }).ToList() ?? [];

        if (index == 0)
        {
            // Full inventory from the server: it is the truth.
            State.Received = items;
        }
        else if (index == State.Received.Count)
        {
            State.Received.AddRange(items);
        }
        else if (index < State.Received.Count)
        {
            // Overlap (resent packet): keep what we have, add only the new tail.
            var overlap = State.Received.Count - index;
            State.Received.AddRange(items.Skip(overlap));
        }
        else
        {
            // We missed some items: ask for everything again.
            _client.Send(ApClient.SyncPacket());
            return;
        }

        RecomputeUnlocks();
        _dirty = true;
    }

    private void OnRoomUpdate(JsonObject packet)
    {
        if (packet["checked_locations"] is JsonArray checkedLocations)
        {
            foreach (var id in ReadLongs(checkedLocations))
            {
                State.Checked.Add(id);
                _inflightChecks.Remove(id);
            }

            _dirty = true;
        }

        if (packet["players"] is JsonArray players)
        {
            _names.SetPlayers(players, null);
        }
    }

    private void OnBounced(JsonObject packet)
    {
        var tags = (packet["tags"] as JsonArray)?.Select(t => t?.GetValue<string>()).ToList() ?? [];
        if (!tags.Contains("DeathLink") || !DeathLinkEnabled)
        {
            return;
        }

        var data = packet["data"] as JsonObject;
        var source = data?["source"]?.GetValue<string>() ?? "someone";
        if (source == State.Slot)
        {
            return;
        }

        var cause = data?["cause"]?.GetValue<string>();
        var text = string.IsNullOrWhiteSpace(cause) ? $"{source} died." : cause!;
        if (InRaid)
        {
            PendingDeathLinkKill = true;
            PendingDeathLinkCause = text;
            Say($"DeathLink: {text}");
        }
        else
        {
            Say($"DeathLink: {text} You were safe out of raid.");
        }
    }

    private void OnPrintJson(JsonObject packet)
    {
        var type = packet["type"]?.GetValue<string>();
        var parts = packet["data"] as JsonArray;
        if (parts == null)
        {
            return;
        }

        bool relevant;
        switch (type)
        {
            case "ItemSend":
            {
                var receiving = packet["receiving"]?.GetValue<int>() ?? -1;
                var sender = packet["item"]?["player"]?.GetValue<int>() ?? -1;
                // Items we receive are announced when they are delivered, so only report what we send to others.
                relevant = sender == State.SlotId && receiving != State.SlotId;
                break;
            }
            case "Hint":
            {
                var receiving = packet["receiving"]?.GetValue<int>() ?? -1;
                var sender = packet["item"]?["player"]?.GetValue<int>() ?? -1;
                relevant = receiving == State.SlotId || sender == State.SlotId;
                break;
            }
            case "ItemCheat":
            case "Join":
            case "Part":
            case "TagsChanged":
                relevant = false;
                break;
            default:
                // Chat, ServerChat, CommandResult, Goal, Release, Collect, Countdown, Tutorial...
                relevant = true;
                break;
        }

        if (!relevant)
        {
            return;
        }

        var text = Render(parts);
        if (!string.IsNullOrWhiteSpace(text))
        {
            _chatLines.Add(text);
            EnqueueClientMessage(text);
        }
    }

    private string Render(JsonArray parts)
    {
        var sb = new StringBuilder();
        foreach (var part in parts.OfType<JsonObject>())
        {
            var text = part["text"]?.GetValue<string>() ?? "";
            var player = part["player"]?.GetValue<int>() ?? State.SlotId;
            switch (part["type"]?.GetValue<string>())
            {
                case "player_id":
                    sb.Append(int.TryParse(text, out var slot) ? _names.Player(slot) : text);
                    break;
                case "item_id":
                    sb.Append(long.TryParse(text, out var itemId) ? _names.Item(itemId, player) : text);
                    break;
                case "location_id":
                    sb.Append(long.TryParse(text, out var locationId) ? _names.Location(locationId, player) : text);
                    break;
                default:
                    sb.Append(text);
                    break;
            }
        }

        return sb.ToString();
    }

    // ================================================================== items
    private void RecomputeUnlocks()
    {
        _unlockedMaps.Clear();
        _unlockedTraders.Clear();
        _fleaUnlocked = false;
        _stationItems.Clear();
        foreach (var received in State.Received)
        {
            if (!_data.ItemsById.TryGetValue(received.Item, out var def))
            {
                continue;
            }

            switch (def.Kind)
            {
                case "map":
                    _unlockedMaps.Add(def.Str("map")!);
                    break;
                case "trader":
                    _unlockedTraders.Add(def.Str("trader")!);
                    break;
                case "flea":
                    _fleaUnlocked = true;
                    break;
                case "station":
                    var stationArea = (int)def.Num("area");
                    _stationItems[stationArea] = _stationItems.GetValueOrDefault(stationArea) + 1;
                    break;
            }
        }
    }

    private void DeliverItems()
    {
        if (State.Delivered > State.Received.Count)
        {
            // The server's item list got shorter (a room restarted without its save); never redeliver.
            State.Delivered = State.Received.Count;
            _dirty = true;
        }

        // A bounded batch per update keeps the session lock (which inventory actions wait on) short.
        var budget = MaxDeliveriesPerUpdate;
        while (State.Delivered < State.Received.Count && budget-- > 0)
        {
            var index = State.Delivered;
            var received = State.Received[index];
            try
            {
                Deliver(index, received);
                _deliveryFailures = 0;
            }
            catch (Exception ex)
            {
                _deliveryFailures++;
                _log($"[Archipelago] failed to deliver item {received.Item} (attempt {_deliveryFailures}): {ex}");
                if (_deliveryFailures < MaxDeliveryAttempts)
                {
                    return; // try again on the next update
                }

                _deliveryFailures = 0;
                var name = _data.ItemsById.TryGetValue(received.Item, out var d) ? d.Name : received.Item.ToString();
                _chatLines.Add($"Could not deliver {name} ({ex.Message}). Check the SPT server console.");
            }

            State.Delivered++;
            _dirty = true;
        }
    }

    private string FromText(ReceivedItem received)
    {
        if (received.Player == State.SlotId && received.Location == -2)
        {
            return "(starting item)";
        }

        var who = received.Player == State.SlotId ? "you" : _names.Player(received.Player);
        var where = _names.Location(received.Location, received.Player);
        if (received.Player == State.SlotId && _data.LocationNames.TryGetValue(received.Location, out var ours))
        {
            where = ours;
        }

        return $"from {who} ({where})";
    }

    private void Deliver(int index, ReceivedItem received)
    {
        if (!_data.ItemsById.TryGetValue(received.Item, out var def))
        {
            _chatLines.Add($"Received unknown item {received.Item}. Is the mod up to date?");
            return;
        }

        var from = FromText(received);
        var startingItem = received.Location == -2;
        switch (def.Kind)
        {
            case "map":
                if (!startingItem)
                {
                    Announce($"Received {def.Name} {from}. {_data.MapsByKey[def.Str("map")!].Name} appears on the map screen after your next raid or a game restart.");
                }

                break;
            case "trader":
            {
                var trader = _data.TradersByKey[def.Str("trader")!];
                var pmc = _game.Pmc(ProfileId);
                var alreadyUnlocked = pmc?.TradersInfo != null
                                      && pmc.TradersInfo.TryGetValue(new SPTarkov.Server.Core.Models.Common.MongoId(trader.Id), out var info)
                                      && (info.Unlocked ?? false);
                if (ContractsMode)
                {
                    // Every trader but the Archipelago one stays locked: it sells this trader's stock instead.
                    if (!startingItem)
                    {
                        Announce($"Received {trader.Name} {from}. The Archipelago trader now sells {trader.Name}'s stock.");
                    }

                    break;
                }

                if (!startingItem)
                {
                    Announce($"Received {trader.Name} {from}.");
                }

                if (!alreadyUnlocked && !startingItem)
                {
                    _game.SendTraderUnlockMail(ProfileId, trader.Id, trader.Name);
                }

                break;
            }
            case "flea":
                Announce($"Received Flea Market Access {from}. The Flea Market opens at the usual level.");
                break;
            case "station":
                ReceiveStation(def, from, startingItem);
                break;
            case "supplies":
                SendSupplies(def, from);
                break;
            case "xp":
            case "rep":
            case "trap":
                if (InRaid)
                {
                    State.Deferred.Add(index);
                    Announce($"Received {def.Name} {from}. It takes effect when your raid ends.");
                }
                else if (ProfileChangesPaused)
                {
                    // The game is loading the profile right now: apply in a few seconds.
                    State.Deferred.Add(index);
                    Announce($"Received {def.Name} {from}.{EffectSuffix(def)}");
                }
                else
                {
                    ApplyProfileEffect(def);
                    Announce($"Received {def.Name} {from}.{EffectSuffix(def)}");
                }

                break;
            case "money":
            {
                var items = _game.CreateMoney(def.Str("currency")!, (int)def.Num("amount"));
                _game.SendItems(ProfileId, $"{def.Name} {from}", items);
                _chatLines.Add($"Received {def.Name} {from}. Collect it in your System messages.");
                EnqueueClientMessage($"Received {def.Name} (System messages)");
                break;
            }
            case "loot":
            {
                var rng = new Random(StableSeed(State.SeedName ?? "", index, received.Item));
                var items = _game.CreateCrate(def.Str("pool")!, (int)def.Num("count", 1), rng, out var description);
                _game.SendItems(ProfileId, $"{def.Name} {from}: {description}", items);
                _chatLines.Add($"Received {def.Name} {from}: {description}. Collect it in your System messages.");
                EnqueueClientMessage($"Received {def.Name} (System messages)");
                break;
            }
            default:
                _chatLines.Add($"Received {def.Name} {from}.");
                break;
        }
    }

    /// <summary>FNV-1a: the same crate roll on every run (string.GetHashCode is randomised per process).</summary>
    private static int StableSeed(string seed, int index, long item)
    {
        unchecked
        {
            var hash = 2166136261u;
            foreach (var b in Encoding.UTF8.GetBytes($"{seed}|{index}|{item}"))
            {
                hash = (hash ^ b) * 16777619u;
            }

            return (int)hash;
        }
    }

    private static string EffectSuffix(ItemDef def) => def.Kind switch
    {
        "trap" => def.Str("trap") switch
        {
            "fence_rep" => " Fence likes you a little less.",
            "exhaustion" => " You feel exhausted and thirsty.",
            "scav_cooldown" => $" Your Scav cooldown grows by {def.Num("seconds") / 60:0} minutes (shows after your next Scav raid or a game restart).",
            _ => "",
        },
        "xp" => $" +{def.Num("amount"):0} experience (shows after your next inventory action, trade or raid).",
        "rep" => $" +{def.Num("amount"):0.00} reputation.",
        _ => "",
    };

    private void ApplyProfileEffect(ItemDef def)
    {
        switch (def.Kind)
        {
            case "xp":
            {
                var amount = (int)def.Num("amount");
                _game.AddExperience(ProfileId, amount);
                State.PendingXp += amount;
                break;
            }
            case "rep":
            {
                var traderId = def.Str("trader_id")!;
                var applied = _game.AddReputation(ProfileId, traderId, def.Num("amount"));
                AddPendingRep(traderId, applied);
                break;
            }
            case "trap":
            {
                var trap = def.Str("trap")!;
                switch (trap)
                {
                    case "fence_rep":
                    {
                        var fence = Traders.FENCE.ToString();
                        var applied = _game.AddReputation(ProfileId, fence, def.Num("amount"));
                        AddPendingRep(fence, applied);
                        State.PendingScavFence += applied;
                        break;
                    }
                    case "exhaustion":
                        _game.ApplyTrap(ProfileId, trap, 0, 0);
                        State.PendingExhaustion = true;
                        break;
                    case "scav_cooldown":
                        _game.ApplyTrap(ProfileId, trap, 0, (int)def.Num("seconds"));
                        // The client only sees the new cooldown once it reloads the profile or gets a new Scav.
                        State.PendingScavLockSeconds += (int)def.Num("seconds");
                        break;
                }

                break;
            }
        }

        _dirty = true;
        _profileChanged = true;
    }

    private void AddPendingRep(string traderId, double applied)
    {
        if (applied != 0)
        {
            State.PendingRep[traderId] = State.PendingRep.GetValueOrDefault(traderId) + applied;
        }
    }

    private void ApplyDeferred()
    {
        if (State.Deferred.Count == 0)
        {
            return;
        }

        foreach (var index in State.Deferred.ToList())
        {
            if (index < State.Received.Count && _data.ItemsById.TryGetValue(State.Received[index].Item, out var def))
            {
                try
                {
                    ApplyProfileEffect(def);
                }
                catch (Exception ex)
                {
                    _log($"[Archipelago] failed to apply {def.Name}: {ex}");
                }
            }
        }

        State.Deferred.Clear();
        _dirty = true; // saved by the caller, after the SPT profile
    }

    // ================================================================== checks & goal
    private void SendChecks()
    {
        var now = DateTime.UtcNow;
        var toSend = new List<long>();
        List<long> detected;
        try
        {
            // All of it first: nothing is marked as sent if reading the profile fails half way.
            detected = _game.DetectChecks(ProfileId, _data, State).Distinct().ToList();
        }
        catch (InvalidOperationException)
        {
            return; // the game was changing the profile; the next update tries again
        }

        foreach (var id in detected)
        {
            if (!State.SlotLocations.Contains(id) || State.Checked.Contains(id))
            {
                continue;
            }

            if (_inflightChecks.TryGetValue(id, out var sentAt) && now - sentAt < TimeSpan.FromSeconds(60))
            {
                continue;
            }

            _inflightChecks[id] = now;
            toSend.Add(id);
        }

        if (toSend.Count > 0)
        {
            _client.Send(ApClient.LocationChecksPacket(toSend));
        }
    }

    private int CompletedQuestChecks()
    {
        var done = new HashSet<long>(State.Checked);
        done.UnionWith(_game.DetectChecks(ProfileId, _data, State));
        return done.Count(id => _data.QuestLocationIds.Contains(id) && State.SlotLocations.Contains(id));
    }

    public bool ContractsMode => State.SlotString("quest_mode") == "archipelago_contracts";

    /// <summary>Contracts done (the Kappa one not counted), and how many there are.</summary>
    public (int Done, int Total) ContractProgress()
    {
        var set = Contracts.Instance?.For(ProfileId);
        if (set is null)
        {
            return (0, 0);
        }

        var done = set.QuestIds.Count(kv => kv.Key != 0 && _game.QuestCompleted(ProfileId, kv.Value.ToString()));
        return (done, set.Count);
    }

    private bool KappaContractDone() =>
        Contracts.Instance?.For(ProfileId) is { } set && _game.QuestCompleted(ProfileId, set.KappaId.ToString());

    private bool GoalMet() => State.SlotString("goal") switch
    {
        "kappa_contract" => KappaContractDone(),
        "collector" => _game.QuestCompleted(ProfileId, _data.CollectorQuestId),
        "all_extracts" => _data.Maps.All(m => State.ExtractCounts.GetValueOrDefault(m.Key) > 0),
        "bosses" => _data.Bosses.All(b => State.BossesKilled.Contains(b.Key)),
        _ => CompletedQuestChecks() >= Math.Max(1, State.SlotInt("goal_quest_count", int.MaxValue)),
    };

    private void CheckGoal()
    {
        // Re-sent once per connection: the server accepts duplicates, and this way a goal reached while
        // the connection was dropping is never lost.
        if (_goalSentThisConnection || !GoalMet())
        {
            return;
        }

        _client.Send(ApClient.StatusPacket(30));
        _goalSentThisConnection = true;
        if (!State.GoalSent)
        {
            State.GoalSent = true;
            _dirty = true;
            Announce("Goal complete! You escaped from Tarkov.");
        }
    }

    private void WarnAboutMissableKappaStep()
    {
        if (State.CollectorWarned || State.SlotString("goal") != "collector"
            || _data.SadistId is null || _data.ColleaguesPart3Id is null)
        {
            return;
        }

        var sadist = _game.QuestState(ProfileId, _data.SadistId);
        var colleagues = _game.QuestState(ProfileId, _data.ColleaguesPart3Id);
        var sadistActive = sadist is QuestStatusEnum.Started or QuestStatusEnum.AvailableForFinish;
        var colleaguesTaken = colleagues is QuestStatusEnum.Started or QuestStatusEnum.AvailableForFinish
            or QuestStatusEnum.Success or QuestStatusEnum.Fail or QuestStatusEnum.FailRestartable or QuestStatusEnum.MarkedAsFailed;
        if (sadistActive && !colleaguesTaken)
        {
            State.CollectorWarned = true;
            _dirty = true;
            Announce("Kappa warning: accept Therapist's 'Colleagues - Part 3' BEFORE turning in 'The Huntsman Path - Sadist'. " +
                     "It is only offered while Sadist is in progress, and Collector needs it.");
        }
    }

    // ================================================================== messaging
    private void Announce(string text)
    {
        _chatLines.Add(text);
        EnqueueClientMessage(text);
    }

    private void EnqueueClientMessage(string text)
    {
        _clientMessages.Enqueue(text.Length > 160 ? text[..157] + "..." : text);
        while (_clientMessages.Count > MaxClientMessages)
        {
            _clientMessages.Dequeue();
        }
    }

    public void Say(string text)
    {
        _chatLines.Add(text);
        FlushChat();
    }

    private void FlushChat()
    {
        if (_chatLines.Count == 0)
        {
            return;
        }

        // One message per batch keeps the messenger readable; split very long batches.
        var batch = new StringBuilder();
        foreach (var line in _chatLines)
        {
            if (batch.Length > 0 && batch.Length + line.Length > 1500)
            {
                _game.Say(ProfileId, batch.ToString().TrimEnd());
                batch.Clear();
            }

            batch.AppendLine(line);
        }

        if (batch.Length > 0)
        {
            _game.Say(ProfileId, batch.ToString().TrimEnd());
        }

        _chatLines.Clear();
        _profileDirty = true;
    }

    /// <summary>Write the SPT profile (if it changed) and then this state, in that order.</summary>
    private void SaveAll()
    {
        if (_profileChanged)
        {
            _profileChanged = false;
            if (!_game.Save(ProfileId))
            {
                _profileSaveFailed = true;
            }
        }

        Save();
    }

    public void Save()
    {
        _dirty = false;
        if (_profileSaveFailed)
        {
            // Never let our record of delivered items get ahead of the SPT profile on disk: after a crash
            // the items would be missing from the profile but marked as delivered.
            if (!_game.Save(ProfileId))
            {
                _dirty = true;
                return;
            }

            _profileSaveFailed = false;
        }

        try
        {
            _store.Save(State);
        }
        catch (Exception ex)
        {
            _dirty = true; // try again on the next update
            _log($"[Archipelago] could not save state for {ProfileId}: {ex.Message}");
        }
    }

    public void Shutdown()
    {
        if (_profileDirty)
        {
            _profileDirty = false;
            _profileChanged = true;
        }

        SaveAll();
        _client.Close();
    }
}

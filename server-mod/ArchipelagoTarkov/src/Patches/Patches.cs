using System.Reflection;
using ArchipelagoTarkov.Data;
using ArchipelagoTarkov.Game;
using ArchipelagoTarkov.Session;
using HarmonyLib;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Callbacks;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Models.Eft.Match;
using SPTarkov.Server.Core.Helpers.Quest;
using SPTarkov.Server.Core.Models.Enums;
using SPTarkov.Server.Core.Routers;
using LocationPath = SPTarkov.Server.Core.Models.Eft.Common.Tables.Path;

namespace ArchipelagoTarkov.Patches;

/// <summary>
/// All patches are small postfixes/prefixes that only act on profiles linked to an Archipelago game,
/// so other mods patching the same methods keep working. Each patch is installed on its own: if one
/// target changed in a future SPT build, the others still load.
///
/// Other mods can replace an SPT class with a subclass (DI type overrides, e.g. SVM's ProfileController).
/// When they do, the method actually called is the override, so that is the one patched. Patch methods
/// take arguments by position (__0, __1...) because an override may rename its parameters.
/// </summary>
public static class PatchInstaller
{
    private static IReadOnlyDictionary<Type, Type> _runtimeTypes = new Dictionary<Type, Type>();

    /// <summary>SPT classes this mod patches; their DI-resolved types are looked up before patching.</summary>
    public static readonly Type[] PatchedTypes =
    [
        typeof(LocationController), typeof(TraderHelper), typeof(LocationLifecycleService),
        typeof(ProfileCallbacks), typeof(EventOutputHolder), typeof(ProfileController), typeof(GameCallbacks),
        typeof(BotController), typeof(TraderAssortHelper), typeof(HideoutController), typeof(QuestHelper),
        typeof(AssortHelper),
    ];

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> Ready = new();

    /// <summary>True once every part (prefix, postfix, finalizer) of that patch is attached.</summary>
    internal static bool IsReady(Type patchType) => Ready.ContainsKey(patchType);

    /// <summary>The method that runs when SPT calls <paramref name="baseType"/>.<paramref name="name"/>.</summary>
    internal static MethodBase? Target(Type baseType, string name)
    {
        var baseMethod = AccessTools.Method(baseType, name);
        if (baseMethod is null || !_runtimeTypes.TryGetValue(baseType, out var actual))
        {
            return baseMethod;
        }

        var parameters = baseMethod.GetParameters().Select(p => p.ParameterType).ToArray();
        // Most derived declaration with the same signature (an override, or the base method if not overridden).
        for (var type = actual; type != null && type != baseType; type = type.BaseType)
        {
            var declared = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly,
                null, parameters, null);
            if (declared != null && declared.GetBaseDefinition().DeclaringType == baseMethod.GetBaseDefinition().DeclaringType)
            {
                return declared;
            }
        }

        return baseMethod;
    }

    public static void InstallAll(Action<string> log, IReadOnlyDictionary<Type, Type>? runtimeTypes = null)
    {
        _runtimeTypes = runtimeTypes ?? new Dictionary<Type, Type>();
        AbstractPatch[] patches =
        [
            new LocationListPatch(),
            new TraderUnlockPatch(),
            new TraderListPatch(),
            new TraderGetPatch(),
            new RaidStartPatch(),
            new RaidEndPatch(),
            new ProfileListPatch(),
            new ItemEventSyncPatch(),
            new ScavRegeneratePatch(),
            new GameStartPatch(),
            new BotGeneratePatch(),
            new TraderAssortPatch(),
            new TakeProductionPatch(),
            new ClientQuestsPatch(),
            new QuestAssortPatch(),
        ];

        foreach (var patch in patches)
        {
            try
            {
                patch.Enable();
                Ready[patch.GetType()] = true;
                if (patch.TargetMethod?.DeclaringType is { } declaring && !declaring.Assembly.Equals(typeof(LocationController).Assembly))
                {
                    log($"[Archipelago] {patch.GetType().Name} attached to {declaring.FullName} ({declaring.Assembly.GetName().Name}).");
                }
            }
            catch (Exception ex)
            {
                log($"[Archipelago] could not install {patch.GetType().Name}: {(ex.InnerException ?? ex)}");
                try
                {
                    // Enable attaches prefix, postfix and finalizer one by one; never leave half a patch behind.
                    Harmony.UnpatchID(patch.GetType().Name);
                }
                catch
                {
                    // nothing more to do
                }
            }
        }
    }
}

/// <summary>/client/locations: hide maps the player has not unlocked, and transit paths that lead to them.</summary>
public class LocationListPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(LocationController), nameof(LocationController.GenerateAll));

    [PatchPostfix]
    public static void Postfix(MongoId __0, ref LocationsGenerateAllResponse __result)
    {
        var manager = SessionManager.Instance;
        var locked = manager?.LockedMaps(__0.ToString());
        if (manager is null || locked is null || locked.Count == 0 || __result?.Locations is null)
        {
            return;
        }

        // The response holds the live database objects: copy instead of editing them.
        var lockedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var locations = new Dictionary<MongoId, LocationBase>();
        foreach (var (id, location) in __result.Locations)
        {
            var key = manager.MapKeyFor(location.Id);
            if (key != null && locked.Contains(key))
            {
                locations[id] = location with { Locked = true, Enabled = false, Transits = DisableTransits(location.Transits, manager, locked) };
                lockedIds.Add(id.ToString());
            }
            else
            {
                locations[id] = location with { Transits = DisableTransits(location.Transits, manager, locked) };
            }
        }

        var paths = __result.Paths?
            .Where(p => !lockedIds.Contains(p.Source ?? "") && !lockedIds.Contains(p.Destination ?? ""))
            .ToList();

        __result = new LocationsGenerateAllResponse { Locations = locations, Paths = paths ?? new List<LocationPath>() };
    }

    internal static IEnumerable<Transit>? DisableTransits(IEnumerable<Transit>? transits, SessionManager manager, HashSet<string> locked)
    {
        if (transits is null)
        {
            return null;
        }

        return transits.Select(t =>
        {
            var target = manager.MapKeyFor(t.Location);
            return target != null && locked.Contains(target) ? t with { IsActive = false } : t;
        }).ToList();
    }
}

/// <summary>Quest rewards that unlock a trader (Jaeger) must not bypass the trader item.</summary>
public class TraderUnlockPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(TraderHelper), nameof(TraderHelper.SetTraderUnlockedState));

    [PatchPrefix]
    public static bool Prefix(MongoId __0, bool __1, MongoId __2)
    {
        // (traderId, status, sessionId)
        if (!__1)
        {
            return true;
        }

        var manager = SessionManager.Instance;
        return manager is null || !manager.BlocksTraderUnlock(__2.ToString(), __0.ToString());
    }
}

/// <summary>
/// Trader settings for a linked profile: the client treats a trader as unlocked when its profile flag OR the trader's
/// "unlocked by default" setting is true. Vanilla sets that for six of the seven main traders, and mods such as SVM can
/// set it for Jaeger, so for a linked profile it is reported as false and the profile flag (which the mod keeps in
/// line with the received items) decides. The shared trader data is never changed.
/// </summary>
public class TraderListPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(TraderHelper), nameof(TraderHelper.GetAllTraders));

    [PatchPostfix]
    public static void Postfix(MongoId __0, ref List<TraderBase> __result)
    {
        if (__result is null)
        {
            return;
        }

        // The Archipelago trader only exists for profiles playing contracts.
        if (Contracts.Instance?.For(__0.ToString()) is null)
        {
            var archipelago = new MongoId(Contracts.TraderId);
            __result = __result.Where(t => t.Id != archipelago).ToList();
        }

        if (!TraderLocks.Applies(__0.ToString()))
        {
            return;
        }

        __result = __result.Select(TraderLocks.Locked).ToList();
    }
}

/// <summary>Same as <see cref="TraderListPatch"/> for a single trader.</summary>
public class TraderGetPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(TraderHelper), nameof(TraderHelper.GetTrader));

    [PatchPostfix]
    public static void Postfix(MongoId? __1, ref TraderBase __result)
    {
        if (__result is null || __1 is not { } session || !TraderLocks.Applies(session.ToString()))
        {
            return;
        }

        __result = TraderLocks.Locked(__result);
    }
}

internal static class TraderLocks
{
    public static bool Applies(string sessionId)
    {
        var manager = SessionManager.Instance;
        var session = manager?.Get(sessionId);
        if (session is null)
        {
            return false;
        }

        lock (session)
        {
            return session.Active;
        }
    }

    public static TraderBase Locked(TraderBase trader)
    {
        var data = SessionManager.Instance?.Data;
        return trader.UnlockedByDefault == true && data != null && data.TradersById.ContainsKey(trader.Id.ToString())
            ? trader with { UnlockedByDefault = false }
            : trader;
    }
}

/// <summary>client/match/local/start: remember the raid and switch off transits to locked maps.</summary>
public class RaidStartPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(LocationLifecycleService), nameof(LocationLifecycleService.StartLocalRaidAsync));

    // StartLocalRaidAsync is async: a postfix runs when it returns its task, not when the raid data is ready.
    // So the task is replaced by one that waits for SPT's and then does our part.
    [PatchPostfix]
    public static void Postfix(MongoId __0, StartLocalRaidRequestData __1, ref Task<StartLocalRaidResponseData> __result)
    {
        if (SessionManager.Instance?.Get(__0.ToString()) is null || __result is null)
        {
            return;
        }

        __result = After(__result, __0, __1);
    }

    private static async Task<StartLocalRaidResponseData> After(Task<StartLocalRaidResponseData> original, MongoId sessionId,
        StartLocalRaidRequestData request)
    {
        var result = await original.ConfigureAwait(false);
        try
        {
            var manager = SessionManager.Instance;
            var session = manager?.Get(sessionId.ToString());
            if (manager is null || session is null)
            {
                return result;
            }

            var mapKey = manager.MapKeyFor(request?.Location);
            List<SpawnpointTemplate>? swappedLoot = null;
            lock (session)
            {
                session.OnRaidStart(mapKey);
                if (result?.LocationLoot?.Loot is { } loot && session.LootsanityOn(mapKey))
                {
                    swappedLoot = LootStandIns.SwapLoot(loot, session.LootTarget, session.LootPlacedThisRaid, session.LootRandom,
                        session.RecordLootPlacement, manager.ItemName);
                }
            }

            var locked = manager.LockedMaps(sessionId.ToString());
            if (locked is { Count: > 0 } && result?.LocationLoot is not null)
            {
                // Copy before editing: other mods (Fika co-op) can share one location object between players.
                result.LocationLoot = result.LocationLoot with
                {
                    Transits = LocationListPatch.DisableTransits(result.LocationLoot.Transits, manager, locked),
                };
            }

            if (swappedLoot is not null && result?.LocationLoot is not null)
            {
                result.LocationLoot = result.LocationLoot with { Loot = swappedLoot };
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"raid start: {ex}");
        }

        return result;
    }
}

/// <summary>client/game/bot/generate: lootsanity swaps the first carried item of each still-unchecked type.</summary>
public class BotGeneratePatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(BotController), nameof(BotController.Generate));

    [PatchPostfix]
    public static void Postfix(MongoId __0, ref Task<IEnumerable<BotBase>> __result)
    {
        if (SessionManager.Instance?.Get(__0.ToString()) is null || __result is null)
        {
            return;
        }

        __result = After(__result, __0);
    }

    /// <summary>"a Scav", "a PMC", a boss's name, ...: who carries an Archipelago Item, for the tracker.</summary>
    private static string Carrier(BotBase bot)
    {
        var role = bot.Info?.Settings?.Role ?? "";
        if (SessionManager.Instance?.Data.BossesByRole.TryGetValue(role, out var boss) == true)
        {
            return boss.Name.Replace("Boss - ", "");
        }

        return role.ToLowerInvariant() switch
        {
            "assault" or "marksman" or "cursedassault" => "a Scav",
            var r when r.StartsWith("pmc") || r is "bear" or "usec" => "a PMC",
            "pmcbot" => "a Raider",
            "exusec" => "a Rogue",
            var r when r.StartsWith("follower") => "a boss guard",
            var r when r.StartsWith("sectant") => "a Cultist",
            _ => "a bot",
        };
    }

    private static async Task<IEnumerable<BotBase>> After(Task<IEnumerable<BotBase>> original, MongoId sessionId)
    {
        var bots = await original.ConfigureAwait(false);
        try
        {
            var session = SessionManager.Instance?.Get(sessionId.ToString());
            if (session is null || bots is null)
            {
                return bots!;
            }

            var list = bots as IList<BotBase> ?? bots.ToList();
            lock (session)
            {
                if (session.InRaid && session.LootsanityOn(session.RaidMap))
                {
                    foreach (var bot in list)
                    {
                        LootStandIns.SwapBotLoot(bot, session.LootTarget, session.LootPlacedThisRaid,
                            session.RecordLootPlacement, Carrier(bot));
                    }
                }
            }

            return list;
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"bot generation: {ex}");
            return bots!;
        }
    }
}

/// <summary>Trader assort requests (and purchases, which read the same assort): randomize the shop when the slot asks.</summary>
public class TraderAssortPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(TraderAssortHelper), nameof(TraderAssortHelper.GetAssort));

    [PatchPostfix]
    public static void Postfix(MongoId __0, MongoId __1, ref TraderAssort __result)
    {
        try
        {
            var manager = SessionManager.Instance;
            var session = manager?.Get(__0.ToString());
            if (manager is null || session is null || __result is null)
            {
                return;
            }

            string mode;
            bool everyRestock;
            int seed, stock;
            lock (session)
            {
                if (!session.IsGatedTrader(__1.ToString()) || (mode = session.ShopMode) == "vanilla")
                {
                    return;
                }

                everyRestock = session.ShopEveryRestock;
                seed = session.ShopSeed;
                stock = session.ShopStock;
            }

            __result = manager.Shops.Randomize(__result, __1.ToString(), mode, everyRestock, seed, stock, manager.Data);
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"trader assort: {ex}");
        }
    }
}

/// <summary>Hideout "take production": craftsanity takes the first craft of each recipe's product away and sends the check.</summary>
public class TakeProductionPatch : AbstractPatch
{
    public sealed record CraftState(ApSession Session, CraftDef Craft, HashSet<string> ItemsBefore);

    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(HideoutController), nameof(HideoutController.TakeProduction));

    [PatchPrefix]
    public static void Prefix(PmcData __0, HideoutTakeProductionRequestData __1, MongoId __2, ref CraftState? __state)
    {
        try
        {
            var session = SessionManager.Instance?.Get(__2.ToString());
            if (session is null || __0?.Inventory?.Items is null || __1 is null)
            {
                return;
            }

            lock (session)
            {
                if (session.CraftTarget(__1.RecipeId.ToString()) is { } craft)
                {
                    __state = new CraftState(session, craft, __0.Inventory.Items.Select(i => i.Id.ToString()).ToHashSet());
                }
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"take production (before): {ex}");
        }
    }

    [PatchPostfix]
    public static void Postfix(PmcData __0, MongoId __2, ItemEventRouterResponse __result, CraftState? __state)
    {
        if (__state is null || __result is null || __0?.Inventory?.Items is not { } items)
        {
            return;
        }

        try
        {
            if (__result.Warnings is { Count: > 0 })
            {
                return; // the craft wasn't collected
            }

            // The product SPT just added (stacks or a preset with its parts); tools given back are left alone.
            var removed = items.Where(i => !__state.ItemsBefore.Contains(i.Id.ToString())
                                           && i.Template.ToString() == __state.Craft.Product)
                .Select(i => i.Id.ToString()).ToHashSet();
            if (removed.Count == 0)
            {
                return;
            }

            bool grew;
            do
            {
                grew = false;
                foreach (var item in items)
                {
                    if (item.ParentId != null && removed.Contains(item.ParentId) && removed.Add(item.Id.ToString()))
                    {
                        grew = true;
                    }
                }
            } while (grew);

            items.RemoveAll(i => removed.Contains(i.Id.ToString()));
            if (__result.ProfileChanges?.TryGetValue(__2, out var change) == true && change?.Items?.NewItems is { } newItems)
            {
                newItems.RemoveAll(i => removed.Contains(i.Id.ToString()));
            }

            lock (__state.Session)
            {
                __state.Session.RecordCraft(__state.Craft);
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"take production: {ex}");
        }
    }
}

/// <summary>client/match/local/end: keep server-side changes the client has not seen, count extractions, send DeathLink.</summary>
public class RaidEndPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(LocationLifecycleService), nameof(LocationLifecycleService.EndLocalRaidAsync));

    internal static (string? MapKey, bool IsPmc) ParseServerId(SessionManager manager, string? serverId)
    {
        // "<location>.<pmc|scav>..." e.g. "bigmap.PMC.2025..." (the same parsing SPT does)
        var details = (serverId ?? "").Split('.');
        var mapKey = manager.MapKeyFor(details.Length > 0 ? details[0] : null);
        var isPmc = details.Length > 1 && details[1].Contains("pmc", StringComparison.OrdinalIgnoreCase);
        return (mapKey, isPmc);
    }

    /// <summary>
    /// Runs before SPT copies the client's profile in. The session stays "in raid" until the raid end has finished,
    /// so no Archipelago item changes the profile in between (they wait, as they do during a raid).
    /// </summary>
    [PatchPrefix]
    public static void Prefix(MongoId __0, EndLocalRaidRequestData __1)
    {
        try
        {
            var manager = SessionManager.Instance;
            var session = manager?.Get(__0.ToString());
            if (manager is null || session is null || __1?.Results?.Profile is null)
            {
                return;
            }

            var (mapKey, isPmc) = ParseServerId(manager, __1.ServerId);
            lock (session)
            {
                session.BeforeRaidEnd(__1.Results.Profile, isPmc);
                // Read the victims as the client sent them, before SPT merges the profile.
                session.RecordVictims(mapKey, __1.Results.Profile.Stats?.Eft?.Victims);
                // Take the Archipelago Items out before SPT copies the profile into the stash.
                session.CollectLoot(mapKey, __1.Results.Profile, __1.Results.Result);
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"raid end (before): {ex}");
        }
    }

    [PatchPostfix]
    public static void Postfix(MongoId __0, EndLocalRaidRequestData __1, ref Task __result)
    {
        if (SessionManager.Instance?.Get(__0.ToString()) is null || __result is null)
        {
            return;
        }

        __result = After(__result, __0, __1);
    }

    private static async Task After(Task original, MongoId sessionId, EndLocalRaidRequestData request)
    {
        await original.ConfigureAwait(false);
        try
        {
            var manager = SessionManager.Instance;
            var session = manager?.Get(sessionId.ToString());
            if (manager is null || session is null || request?.Results is null)
            {
                return;
            }

            var (mapKey, isPmc) = ParseServerId(manager, request.ServerId);
            var killer = request.Results.Profile?.Stats?.Eft?.Aggressor?.Name;
            lock (session)
            {
                session.OnRaidEnd(mapKey, isPmc, request.Results.Result, killer);
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"raid end: {ex}");
        }
    }
}

/// <summary>
/// client/game/profile/list: the client loads the whole profile. SPT 4.1 streams the response after this method
/// returns, so instead of holding a lock: forget the pending changes now (the client is about to get every
/// server-side value) and hold back new profile changes for a few seconds while the response is written.
/// </summary>
public class ProfileListPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(ProfileCallbacks), nameof(ProfileCallbacks.GetProfileData));

    [PatchPrefix]
    public static void Prefix(MongoId __2)
    {
        // (url, request, sessionID)
        try
        {
            var session = SessionManager.Instance?.Get(__2.ToString());
            if (session is null)
            {
                return;
            }

            lock (session)
            {
                session.BeforeClientLoadsProfiles();
                session.OnClientLoadedProfiles();
                session.PauseProfileChanges(TimeSpan.FromSeconds(3));
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"profile list: {ex}");
        }
    }
}

/// <summary>
/// Every inventory/trade/quest action returns the PMC's experience, health and trader standings to the
/// client, so pending changes are known to the client from then on.
/// </summary>
public class ItemEventSyncPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(EventOutputHolder), nameof(EventOutputHolder.UpdateOutputProperties));

    [PatchPrefix]
    public static void Prefix(MongoId __0, ref ApSession? __state)
    {
        __state = SessionLock.Enter(__0, typeof(ItemEventSyncPatch));
    }

    [PatchFinalizer]
    public static void Finalizer(Exception? __exception, ApSession? __state)
    {
        SessionLock.Exit(__state, __exception is null ? s => s.OnClientSyncedPmc() : null);
    }
}

/// <summary>client/game/profile/savage/regenerate: carry a Scav cooldown trap over to the new Scav.</summary>
public class ScavRegeneratePatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(ProfileController), nameof(ProfileController.GeneratePlayerScav));

    [PatchPrefix]
    public static void Prefix(MongoId __0, ref ApSession? __state)
    {
        // Held until the finalizer: a Fence Grudge landing mid-generation would otherwise miss the new Scav.
        __state = SessionLock.Enter(__0, typeof(ScavRegeneratePatch));
    }

    [PatchPostfix]
    public static void Postfix(MongoId __0, PmcData __result)
    {
        try
        {
            var session = SessionManager.Instance?.Get(__0.ToString());
            if (session is null)
            {
                return;
            }

            lock (session)
            {
                session.OnScavRegenerated(__result);
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"scav regenerate: {ex}");
        }
    }

    [PatchFinalizer]
    public static void Finalizer(ApSession? __state)
    {
        SessionLock.Exit(__state, null);
    }
}

/// <summary>client/game/start: the game was (re)started, so the player is not in a raid any more.</summary>
public class GameStartPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(GameCallbacks), nameof(GameCallbacks.GameStart));

    [PatchPostfix]
    public static void Postfix(MongoId __2)
    {
        // (url, request, sessionID)
        try
        {
            var session = SessionManager.Instance?.Get(__2.ToString());
            if (session is null)
            {
                return;
            }

            lock (session)
            {
                session.OnGameStarted();
            }
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"game start: {ex}");
        }
    }
}

/// <summary>Holds a session's lock across a patched method (prefix to finalizer, same thread).</summary>
internal static class SessionLock
{
    public static ApSession? Enter(MongoId sessionId, Type patchType)
    {
        if (!PatchInstaller.IsReady(patchType))
        {
            // Without its finalizer attached the lock would never be released.
            return null;
        }

        try
        {
            // Taken whether or not anything is pending: an item may arrive while the response is built.
            var session = SessionManager.Instance?.Get(sessionId.ToString());
            if (session is null)
            {
                return null;
            }

            Monitor.Enter(session);
            return session;
        }
        catch
        {
            return null;
        }
    }

    public static void Exit(ApSession? session, Action<ApSession>? action)
    {
        if (session is null)
        {
            return;
        }

        try
        {
            action?.Invoke(session);
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"profile sync: {ex}");
        }
        finally
        {
            Monitor.Exit(session);
        }
    }
}

/// <summary>
/// Archipelago contracts: a profile playing contracts sees only its own contracts, never Tarkov's quests; every
/// other profile never sees anybody's contracts.
/// </summary>
public class ClientQuestsPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(QuestHelper), nameof(QuestHelper.GetClientQuests));

    [PatchPostfix]
    public static void Postfix(MongoId __0, ref List<Quest> __result)
    {
        var contracts = Contracts.Instance;
        if (contracts is null || __result is null)
        {
            return;
        }

        var mine = contracts.For(__0.ToString());
        __result = mine != null
            ? __result.Where(q => mine.Indexes.ContainsKey(q.Id)).ToList()
            : __result.Where(q => !contracts.IsContract(q.Id)).ToList();
    }
}

/// <summary>
/// Archipelago contracts: Tarkov's quests never get done, so offers they unlock would stay locked forever. Instead an
/// offer unlocks once the share of contracts done reaches how far into the road to Kappa its quest is (loyalty levels
/// still apply as usual).
/// </summary>
public class QuestAssortPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(AssortHelper), nameof(AssortHelper.StripLockedQuestAssort));

    [PatchPrefix]
    public static void Prefix(PmcData __0, ref Dictionary<string, Dictionary<MongoId, MongoId>> __3)
    {
        try
        {
            var contracts = Contracts.Instance;
            var data = SessionManager.Instance?.Data;
            if (contracts is null || data is null || __0?.Id is not { } pmcId || __3 is null
                || contracts.For(pmcId.ToString()) is not { Count: > 0 } set)
            {
                return;
            }

            var done = __0.Quests?.Count(q => q.Status == QuestStatusEnum.Success && set.Indexes.TryGetValue(q.QId, out var i) && i != 0) ?? 0;
            var share = done / (double)set.Count;
            var filtered = new Dictionary<string, Dictionary<MongoId, MongoId>>();
            foreach (var (status, offers) in __3)
            {
                filtered[status] = offers
                    .Where(kv => !(data.QuestProgress.TryGetValue(kv.Value.ToString(), out var needed) && share >= needed))
                    .ToDictionary(kv => kv.Key, kv => kv.Value);
            }

            __3 = filtered;
        }
        catch
        {
            // Keep the vanilla filter.
        }
    }
}

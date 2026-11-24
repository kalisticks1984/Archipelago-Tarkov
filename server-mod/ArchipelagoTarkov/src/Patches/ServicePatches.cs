using System.Reflection;
using ArchipelagoTarkov.Game;
using ArchipelagoTarkov.Session;
using SPTarkov.Reflection.Patching;
using SPTarkov.Server.Core.Controllers;
using SPTarkov.Server.Core.Helpers.InRaid;
using SPTarkov.Server.Core.Models.Common;
using SPTarkov.Server.Core.Models.Eft.Common;
using SPTarkov.Server.Core.Models.Eft.Common.Tables;
using SPTarkov.Server.Core.Models.Eft.Health;
using SPTarkov.Server.Core.Models.Eft.Hideout;
using SPTarkov.Server.Core.Models.Eft.Insurance;
using SPTarkov.Server.Core.Models.Eft.ItemEvent;
using SPTarkov.Server.Core.Services.Commerce;

namespace ArchipelagoTarkov.Patches;

/// <summary>Option insurance off: insuring is refused (the screen still works, with the Archipelago trader).</summary>
public class InsurePatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(InsuranceController), nameof(InsuranceController.Insure));

    [PatchPrefix]
    public static bool Prefix(MongoId __2, ref ItemEventRouterResponse __result)
    {
        var manager = SessionManager.Instance;
        var session = manager?.Get(__2.ToString());
        if (manager is null || session is null)
        {
            return true;
        }

        lock (session)
        {
            if (session.InsuranceOn)
            {
                return true;
            }
        }

        __result = manager.Refuse(__2, "Insurance is switched off in this Archipelago game (YAML option insurance).");
        return false;
    }
}

/// <summary>Option insurance_price: scales every insurance price for a linked profile.</summary>
public class InsurancePricePatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(InsuranceService), nameof(InsuranceService.GetRoublePriceToInsureItemWithTrader));

    [PatchPostfix]
    public static void Postfix(PmcData? __0, ref double __result)
    {
        if (__0?.Id is not { } id || SessionManager.Instance?.Get(id.ToString()) is not { } session)
        {
            return;
        }

        double factor;
        lock (session)
        {
            factor = session.InsurancePriceFactor;
        }

        if (factor != 1.0)
        {
            __result = Math.Ceiling(__result * factor);
        }
    }
}

/// <summary>Option post_raid_healing off: trader healing (treatment screen, Services) is refused.</summary>
public class HealPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(HealthController), nameof(HealthController.HealthTreatment));

    [PatchPrefix]
    public static bool Prefix(MongoId __2, ref ItemEventRouterResponse __result)
    {
        var manager = SessionManager.Instance;
        var session = manager?.Get(__2.ToString());
        if (manager is null || session is null)
        {
            return true;
        }

        lock (session)
        {
            if (session.HealingOn)
            {
                return true;
            }
        }

        __result = manager.Refuse(__2, "Trader healing is switched off in this Archipelago game (YAML option post_raid_healing).");
        return false;
    }
}

/// <summary>
/// Kit grace raids: SPT removes the equipment of a PMC that died; skipped once when the raid end said so (see
/// <see cref="ApSession.UseKitGrace"/>).
/// </summary>
public class KeepGearPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(InRaidHelper), nameof(InRaidHelper.DeleteInventory));

    [PatchPrefix]
    public static bool Prefix(MongoId __1)
    {
        if (SessionManager.Instance?.Get(__1.ToString()) is not { } session)
        {
            return true;
        }

        lock (session)
        {
            if (!session.KeepGearOnce)
            {
                return true;
            }

            session.KeepGearOnce = false;
            return false;
        }
    }
}

/// <summary>
/// Option hideout_levels: items. Finishing a hideout upgrade sends its check; the level itself waits for the station's
/// progressive items (<see cref="ApSession.AllowUpgradeComplete"/>), and is applied later by the mod.
/// </summary>
public class UpgradeCompletePatch : AbstractPatch
{
    /// <summary>Set while the mod itself applies a level that has its items now.</summary>
    [ThreadStatic] public static bool Applying;

    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(HideoutController), nameof(HideoutController.UpgradeComplete));

    [PatchPrefix]
    public static bool Prefix(PmcData __0, HideoutUpgradeCompleteRequestData __1, MongoId __2)
    {
        if (Applying || __1?.AreaType is not { } areaType || SessionManager.Instance?.Get(__2.ToString()) is not { } session)
        {
            return true;
        }

        try
        {
            var area = __0.Hideout?.Areas?.FirstOrDefault(a => a.Type == areaType);
            if (area is null)
            {
                return true;
            }

            lock (session)
            {
                if (session.AllowUpgradeComplete((int)areaType, (area.Level ?? 0) + 1))
                {
                    return true;
                }
            }

            // Built, but not active yet: the construction is over and the station stays at its level.
            area.Constructing = false;
            area.CompleteTime = 0;
            return false;
        }
        catch (Exception ex)
        {
            SessionManager.Instance?.LogError($"hideout upgrade complete: {ex}");
            return true;
        }
    }
}

/// <summary>Option hideout_levels: items. A level already built (waiting for its items) can't be paid for again.</summary>
public class UpgradeStartPatch : AbstractPatch
{
    protected override MethodBase? GetTargetMethod() =>
        PatchInstaller.Target(typeof(HideoutController), nameof(HideoutController.StartUpgrade));

    [PatchPrefix]
    public static bool Prefix(PmcData __0, HideoutUpgradeRequestData __1, MongoId __2, ItemEventRouterResponse __3)
    {
        var manager = SessionManager.Instance;
        if (__1?.AreaType is not { } areaType || manager?.Get(__2.ToString()) is not { } session)
        {
            return true;
        }

        var area = __0.Hideout?.Areas?.FirstOrDefault(a => a.Type == areaType);
        string? message;
        lock (session)
        {
            message = session.BlockUpgradeStart((int)areaType, (area?.Level ?? 0) + 1);
        }

        if (message is null)
        {
            return true;
        }

        manager.AppendError(__3, message);
        return false;
    }
}

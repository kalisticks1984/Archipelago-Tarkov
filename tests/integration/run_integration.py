#!/usr/bin/env python3
"""
End-to-end test: real SPT 4.1.6 server + ArchipelagoTarkov mod + real Archipelago MultiServer.

The script plays the part of the game client (HTTP calls the EFT client makes) and of the
multiworld (a watcher client and server console commands), and checks every behaviour the mod
promises. Run it from anywhere:

    SPT_DIR=<SPT server folder> SPT_DOTNET=<.NET 10 dotnet> AP_DIR=<Archipelago checkout with the world installed> \
    AP_PYTHON=<python with Archipelago's requirements and websockets> python run_integration.py

It deletes the SPT profiles and the mod's state in SPT_DIR, so use a throwaway SPT install.

Optional: AP_PORT (default 38281) when another Archipelago server already uses that port on this machine.
Works on Linux and on Windows; on Windows the .sh helpers run in Git Bash (INTEGRATION_BASH to point elsewhere).
"""
from __future__ import annotations

import json
import os
import shutil
import subprocess
import sys
import tempfile
import time
import traceback

if os.name == "nt" and not sys.flags.utf8_mode:
    # SPT's files are UTF-8; Windows' default code page can't read them. Re-run in UTF-8 mode.
    sys.exit(subprocess.call([sys.executable, "-X", "utf8", *sys.argv]))

sys.path.insert(0, os.path.dirname(__file__))
from aptools import AP_PORT, MultiServer, Watcher, generate, patch_multidata  # noqa: E402
from sptclient import BOT_ID, SYSTEM_DIALOG, SptClient  # noqa: E402

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SPT = os.environ.get("SPT_DIR") or sys.exit("set SPT_DIR to a throwaway SPT 4.1.6 server folder (the SPT_Runtime folder)")
BIN = os.path.dirname(os.path.abspath(__file__)).replace("\\", "/")  # spt-start.sh / spt-stop.sh
# On Windows the .sh helpers run in Git Bash (never WSL's bash.exe, which is often first on PATH).
BASH = os.environ.get("INTEGRATION_BASH", r"C:\Program Files\Git\bin\bash.exe") if os.name == "nt" else None
WORK = os.environ.get("WORK_DIR", os.path.join(tempfile.gettempdir(), "archipelago-tarkov-integration"))
MOD_DIR = os.path.join(SPT, "user", "mods", "ArchipelagoTarkov")
DATA = json.load(open(os.path.join(ROOT, "apworld", "tarkov", "data", "tarkov_data.json")))
QUESTS = {q["id"]: q for q in DATA["quests"]}
QUEST_BY_NAME = {}
for _q in DATA["quests"]:
    QUEST_BY_NAME.setdefault(_q["name"], _q)
ITEMS = {i["name"]: i for i in DATA["items"]}
LOC_IDS = {}
for _q in DATA["quests"]:
    if _q["check"]:
        LOC_IDS[_q["location_name"]] = _q["location_id"]
for _h in DATA["hideout"]:
    LOC_IDS[_h["name"]] = _h["location_id"]
for _e in DATA["extracts"]:
    LOC_IDS[_e["name"]] = _e["location_id"]
for _k in DATA["kills"] + DATA["bosses"]:
    LOC_IDS[_k["name"]] = _k["location_id"]
LOOT_BY_STAND_IN = {_r["stand_in"]: _r for _r in DATA["loot"]}
CRAFTS = {_c["recipe"]: _c for _c in DATA["crafts"]}
USD = "5696686a4bdc2da3298b456a"


def handbook_category():
    """Item template -> top-level handbook category id (from SPT's database), like the shop randomizer uses."""
    hb = json.load(open(os.path.join(SPT, "SPT_Data", "database", "templates", "handbook.json"), encoding="utf-8"))
    parent = {c["Id"]: c.get("ParentId") for c in hb["Categories"]}

    def top(cat):
        while parent.get(cat):
            cat = parent[cat]
        return cat
    return {i["Id"]: top(i["ParentId"]) for i in hb["Items"]}


def vanilla_assort(trader_id):
    return json.load(open(os.path.join(SPT, "SPT_Data", "database", "traders", trader_id, "assort.json"), encoding="utf-8"))

_victim_seq = [0]


def victim(side="Savage", role="assault"):
    """One entry of the client's raid kill list, with a unique bot profile id. AccountId and Location matter: SPT sometimes has a
    killed PMC message you and parses it."""
    _victim_seq[0] += 1
    return {"ProfileId": f"{0xA9000000 + _victim_seq[0]:024x}", "AccountId": str(1000000 + _victim_seq[0]),
            "Name": f"Bot{_victim_seq[0]}", "Side": side, "Role": role, "Time": "00:05:00", "Level": 10,
            "PrestigeLevel": 0, "BodyPart": "Head", "Weapon": "", "Distance": 20.0, "Location": "bigmap"}

TRADERS = {t["key"]: t["id"] for t in DATA["traders"]}
_TEMPLATES = None


def templates():
    """SPT's item database, to check what the mod hands out."""
    global _TEMPLATES
    if _TEMPLATES is None:
        _TEMPLATES = json.load(open(os.path.join(SPT, "SPT_Data", "database", "templates", "items.json")))
    return _TEMPLATES


def is_a(tpl, base):
    items = templates()
    for _ in range(30):
        if tpl == base:
            return True
        tpl = items.get(tpl, {}).get("_parent")
        if not tpl:
            return False
    return False


def armor_class(tpl):
    try:
        return int(templates().get(tpl, {}).get("_props", {}).get("armorClass") or 0)
    except (TypeError, ValueError):
        return 0


WEAPON, AMMO, MAGAZINE, ARMOR, VEST, HEADWEAR, BACKPACK = (
    "5422acb9af1c889c16000029", "5485a8684bdc2da71d8b4567", "5448bc234bdc2d3c308b4569", "5448e54d4bdc2dcc718b4568",
    "5448e5284bdc2dcb718b4567", "5a341c4086f77401f2541505", "5448e53e4bdc2d60728b4567")
ROUBLES = "5449016a4bdc2d6f028b456f"


def deliveries(client):
    """Archipelago item messages in the System chat (trader unlock notes left out)."""
    return [m for m in client.dialog(SYSTEM_DIALOG, 11)
            if m.get("hasRewards") and (m.get("text") or "").startswith("Archipelago")
            and "is now available" not in (m.get("text") or "")]


def roots_of(items):
    ids = {i["_id"] for i in items}
    return [i for i in items if i.get("parentId") not in ids]


def protection(item, items):
    """Armor class of an armor item: its own or the best of its plates/inserts."""
    best = armor_class(item["_tpl"])
    stack = [item["_id"]]
    while stack:
        parent = stack.pop()
        for child in items:
            if child.get("parentId") == parent:
                best = max(best, armor_class(child["_tpl"]))
                stack.append(child["_id"])
    return best
RESULTS = []


def step(name):
    def wrap(fn):
        def run(*a, **kw):
            t0 = time.time()
            try:
                fn(*a, **kw)
                RESULTS.append((name, True, f"{time.time() - t0:.1f}s"))
                print(f"PASS  {name}", flush=True)
            except Exception as ex:  # noqa: BLE001
                RESULTS.append((name, False, f"{ex}"))
                print(f"FAIL  {name}: {ex}", flush=True)
                traceback.print_exc()
        return run
    return wrap


def sh(cmd):
    if BASH:
        return subprocess.run([BASH, "-c", cmd], capture_output=True, text=True)
    return subprocess.run(cmd, shell=True, capture_output=True, text=True)


def spt_restart(log):
    sh(f"{BIN}/spt-stop.sh")
    out = sh(f"{BIN}/spt-start.sh {log.replace(os.sep, '/')}").stdout.strip()
    if "started" not in out:
        raise RuntimeError(f"SPT did not start ({out}):\n" + open(log).read()[-3000:])


def wait(pred, timeout=30, interval=1.0, what="condition"):
    deadline = time.time() + timeout
    last = None
    while time.time() < deadline:
        try:
            last = pred()
            if last:
                return last
        except Exception as ex:  # noqa: BLE001
            last = ex
        time.sleep(interval)
    raise AssertionError(f"timed out waiting for {what} (last: {last})")


def trader_unlocked(c, key):
    return c.pmc()["TradersInfo"][TRADERS[key]].get("unlocked")


def enabled_maps(c):
    locs = c.locations()["locations"]
    return {l["Id"].lower() for l in locs.values() if l.get("Enabled") and not l.get("Locked")}


def flea_banned(c):
    bans = c.pmc()["Info"].get("Bans") or []
    return any(b.get("banType") in ("RagFair", 1) and b.get("dateTime") == 4102444800 for b in bans)


def main():
    os.makedirs(WORK, exist_ok=True)
    # ---------------------------------------------------------------- fresh environment
    sh(f"{BIN}/spt-stop.sh")
    shutil.rmtree(os.path.join(SPT, "user", "profiles"), ignore_errors=True)
    for sub in ("profiles", "cache"):
        shutil.rmtree(os.path.join(MOD_DIR, sub), ignore_errors=True)
    shutil.copy(os.path.join(ROOT, "server-mod", "ArchipelagoTarkov", "bin", "Release", "ArchipelagoTarkov.dll"), MOD_DIR)

    print("generating multiworld...", flush=True)
    multidata = generate({
        "Runner": {"quest_mode": "vanilla", "death_link": "true", "goal_quest_count": 5, "extracts_per_map": 2, "trap_percentage": 0,
                   "lootsanity": ["barter", "provisions"], "craftsanity": True,
                   "shop_randomizer": "same_category", "shop_reshuffle": "per_seed"},
        "Buddy": {},
        "Kitted": {"quest_mode": "vanilla", "starting_kit": "replace", "starting_kit_quality": "geared",
                   "goal_quest_count": 5},
        "Kitted2": {"quest_mode": "vanilla", "starting_kit": "replace", "starting_kit_quality": "scrappy",
                    "goal_quest_count": 5},
        "Contractor": {"quest_mode": "archipelago_contracts", "contract_count": 40, "starting_map": "customs"},
    }, os.path.join(WORK, "gen"), seed=12345)
    server = MultiServer(multidata, logfile=os.path.join(WORK, "multiserver.log"))
    watcher = buddy = None
    try:
        server.start()
        spt_restart(os.path.join(WORK, "spt1.log"))
        watcher = Watcher("Runner").start()
        buddy = Watcher("Buddy", tags=["Tracker", "DeathLink"]).start()
        c = SptClient()
        scenario(c, server, watcher, buddy)
    finally:
        for w in (watcher, buddy):
            if w:
                w.close()
        server.stop()

    print("\n==== RESULTS ====")
    for name, ok, info in RESULTS:
        print(f"{'PASS' if ok else 'FAIL'}  {name}  ({info})")
    failed = [r for r in RESULTS if not r[1]]
    print(f"\n{len(RESULTS) - len(failed)}/{len(RESULTS)} passed")
    sys.exit(1 if failed else 0)


def scenario(c: SptClient, server: MultiServer, watcher: Watcher, buddy: Watcher):
    @step("create account and character; Archipelago contact is in the friend list")
    def _():
        c.register_and_login("runner")
        c.create_character("Runner")
        c.start_game()
        names = [f["Info"]["Nickname"] for f in c.friends()["Friends"]]
        assert "Archipelago" in names, names
    _()

    @step("before linking, nothing is locked")
    def _():
        assert trader_unlocked(c, "skier") and trader_unlocked(c, "mechanic")
        assert {"bigmap", "woods", "sandbox"} <= enabled_maps(c)
        assert not flea_banned(c)
    _()

    @step("bad connect command gets a usage message")
    def _():
        n = c.bot_ids()
        c.say("connect")
        c.wait_for_bot("Usage: connect", start_count=n, timeout=10)
    _()

    @step("wrong slot name is refused with a clear message")
    def _():
        n = c.bot_ids()
        c.say(f"connect 127.0.0.1:{AP_PORT} Nobody")
        c.wait_for_bot("refused the connection", start_count=n, timeout=40)
        c.wait_for_bot("slot name", start_count=n, timeout=5)
    _()

    @step("connect links the profile and reports progress")
    def _():
        n = c.bot_ids()
        c.say(f"connect 127.0.0.1:{AP_PORT} Runner")
        c.wait_for_bot("Connected to Archipelago as Runner", start_count=n, timeout=40)
        c.wait_for_bot("Restart the game once", start_count=n, timeout=10)
    _()

    @step("after linking: only Prapor and Therapist, only Ground Zero, flea banned")
    def _():
        wait(lambda: not trader_unlocked(c, "skier"), what="skier locked")
        for key in ("prapor", "therapist"):
            assert trader_unlocked(c, key), key
        for key in ("skier", "peacekeeper", "mechanic", "ragman", "jaeger"):
            assert not trader_unlocked(c, key), key
        maps = enabled_maps(c)
        assert maps == {"sandbox", "sandbox_high"}, maps
        assert flea_banned(c)
    _()

    @step("the trader list a linked client gets never marks the main traders unlocked by default")
    def _():
        # The client treats a trader as unlocked if its profile flag OR this setting is true (vanilla: six of seven).
        bases = c.call("/client/trading/api/traderSettings")
        main = [b for b in bases if b["_id"] in TRADERS.values()]
        assert len(main) == len(TRADERS), [b["_id"] for b in bases]
        assert not [b["nickname"] for b in main if b.get("unlockedByDefault")], [b["nickname"] for b in main]
    _()

    @step("transits to locked maps are switched off in the map list and in raid")
    def _():
        locs = c.locations()["locations"]
        gz = next(l for l in locs.values() if l["Id"].lower() == "sandbox")
        active_targets = [t["location"] for t in gz.get("transits") or [] if t.get("active")]
        assert not active_targets, active_targets
        paths = c.locations().get("paths") or []
        assert all(p["Source"] in (gz["_Id"], "65b8d6f5cdde2479cb2a3125") and p["Destination"] in (gz["_Id"], "65b8d6f5cdde2479cb2a3125") for p in paths), paths
        start = c.raid_start("Sandbox")
        transits = start["locationLoot"].get("transits") or []
        assert transits, "expected Ground Zero to have transits"
        assert not [t for t in transits if t.get("active")], transits
        c.raid_end(start["serverId"], "Left")
    _()

    shooting_cans = QUEST_BY_NAME["Shooting Cans"]
    debut = QUEST_BY_NAME["Debut"]

    @step("completing a quest sends its check")
    def _():
        c.accept_quest(shooting_cans["id"])
        c.complete_quest(shooting_cans["id"])
        watcher.wait_checked(shooting_cans["location_id"], timeout=30)
    _()

    @step("sending an item to another player is announced")
    def _():
        n = c.bot_ids()
        c.accept_quest(debut["id"])
        c.complete_quest(debut["id"])
        watcher.wait_checked(debut["location_id"], timeout=30)
        # the item at Debut might be ours; look for either the 'sent' or 'received' message
        wait(lambda: any((" sent " in (m.get("text") or "") or "Received" in (m.get("text") or ""))
                         for m in c.bot_messages() if m["_id"] not in n),
             timeout=20, what="announcement")
    _()

    @step("receiving a map unlocks it in the map list")
    def _():
        n = c.bot_ids()
        server.command("/send Runner Customs Access")
        c.wait_for_bot("Received Customs Access", start_count=n, timeout=30)
        assert "bigmap" in enabled_maps(c)
    _()

    @step("receiving a trader unlocks it and sends a live unlock message")
    def _():
        n = c.bot_ids()
        server.command("/send Runner Skier")
        c.wait_for_bot("Received Skier", start_count=n, timeout=30)
        wait(lambda: trader_unlocked(c, "skier"), what="skier unlocked")
        sysmsgs = c.dialog(SYSTEM_DIALOG, 11)
        events = [e for m in sysmsgs for e in (m.get("profileChangeEvents") or []) if e.get("Type") == "UnlockTrader"]
        assert any(e.get("entity") == TRADERS["skier"] for e in events), events
    _()

    @step("receiving Flea Market Access lifts the ban")
    def _():
        n = c.bot_ids()
        server.command("/send Runner Flea Market Access")
        c.wait_for_bot("Received Flea Market Access", start_count=n, timeout=30)
        wait(lambda: not flea_banned(c), what="flea ban removed")
    _()

    @step("money and crates arrive as messages with attachments")
    def _():
        before = len(deliveries(c))
        server.command("/send Runner Roubles (100,000)")
        server.command("/send Runner Weapon Crate")
        server.command("/send Runner Armor Crate")
        server.command("/send Runner Medical Crate")
        server.command("/send Runner Ammo Crate")
        server.command("/send Runner Key Crate")

        def got():
            msgs = deliveries(c)
            return len(msgs) >= 6 and msgs
        msgs = wait(got, timeout=40, what="6 item messages")
        money = [m for m in msgs if "Roubles (100,000)" in m.get("text", "")]
        assert money, [m.get("text") for m in msgs]
        tpls = [i["_tpl"] for i in money[0]["items"]["data"]]
        assert tpls and all(t == "5449016a4bdc2d6f028b456f" for t in tpls), tpls
        total = sum(i.get("upd", {}).get("StackObjectsCount", 1) for i in money[0]["items"]["data"])
        assert total == 100000, total
        armor = [m for m in msgs if "Armor Crate" in m.get("text", "")][0]
        piece = roots_of(armor["items"]["data"])[0]
        assert any(is_a(piece["_tpl"], b) for b in (ARMOR, VEST, HEADWEAR)), piece
        assert protection(piece, armor["items"]["data"]) > 0, "the armor crate must hold real armor"
        weapon = [m for m in msgs if "Weapon Crate" in m.get("text", "")][0]
        assert len(weapon["items"]["data"]) > 3, "weapon should be a full preset"
        for m in msgs:
            roots = [i for i in m["items"]["data"] if i.get("slotId") == "main"]
            assert roots, m.get("text")
            assert m.get("type") in (7, "SystemMessage") and m.get("uid") == SYSTEM_DIALOG, (m.get("type"), m.get("uid"))
        assert not [m for m in c.dialog() if m.get("hasRewards")], "no attachments on Archipelago chat messages"
        c.wait_for_bot("Collect it in your System messages", timeout=10)
    _()

    @step("experience outside a raid is applied right away")
    def _():
        xp0 = c.pmc()["Info"]["Experience"]
        n = c.bot_ids()
        server.command("/send Runner Experience Bundle")
        c.wait_for_bot("Received Experience Bundle", start_count=n, timeout=30)
        # what the game sees after its next inventory action (the profile list is only loaded at startup)
        def seen_xp():
            changes = c.item_event([{"Action": "ReadEncyclopedia", "ids": []}])["profileChanges"][c.session]
            return changes["experience"] if changes["experience"] > xp0 else None
        xp1 = wait(seen_xp, timeout=40, interval=2, what="xp")
        assert xp1 - xp0 == 15000, (xp0, xp1)
        assert c.pmc()["Info"]["Experience"] == xp1
    _()

    @step("items that change the profile wait for the raid to end; extraction counts")
    def _():
        start = c.raid_start("bigmap")
        xp0 = c.pmc()["Info"]["Experience"]
        n = c.bot_ids()
        server.command("/send Runner Experience Bundle")
        c.wait_for_bot("takes effect when your raid ends", start_count=n, timeout=30)
        time.sleep(6)
        assert c.pmc()["Info"]["Experience"] == xp0, "xp must not be applied in raid"
        c.raid_end(start["serverId"], "Survived", exit_name="Smuggler's Boat")
        watcher.wait_checked(LOC_IDS["Customs - Extraction 1"], timeout=30)
        wait(lambda: c.pmc()["Info"]["Experience"] >= xp0 + 15000, what="deferred xp applied")
    _()

    state_path = os.path.join(MOD_DIR, "profiles", f"{c.session}.json")

    def state():
        return json.load(open(state_path))

    @step("experience received in the menu survives a raid the client started without reloading")
    def _():
        stale = c.pmc()  # the client's copy; loading it also clears anything pending
        xp_before = stale["Info"]["Experience"]
        wait(lambda: state().get("pendingXp", 0) == 0, what="nothing pending")
        n = c.bot_ids()
        server.command("/send Runner Experience Bundle")
        c.wait_for_bot("Received Experience Bundle", start_count=n, timeout=30)
        wait(lambda: state().get("pendingXp") == 15000, what="pending xp recorded")
        start = c.raid_start("bigmap")
        stale["Info"]["Experience"] = xp_before + 777  # what the client earned in the raid
        c.raid_end(start["serverId"], "Survived", exit_name="Smuggler's Boat", profile=stale)
        xp = c.pmc()["Info"]["Experience"]
        assert xp == xp_before + 777 + 15000, (xp_before, xp)
        wait(lambda: state().get("pendingXp") == 0, what="pending cleared once the client reloaded")
    _()

    @step("an inventory action hands pending changes to the client")
    def _():
        c.pmc()
        n = c.bot_ids()
        server.command("/send Runner Prapor Reputation")
        c.wait_for_bot("Received Prapor Reputation", start_count=n, timeout=30)
        wait(lambda: state().get("pendingRep", {}).get(TRADERS["prapor"]), what="pending rep")
        resp = c.item_event([{"Action": "ReadEncyclopedia", "ids": []}])
        sent = resp["profileChanges"][c.session]["traderRelations"][TRADERS["prapor"]]["standing"]
        wait(lambda: not state().get("pendingRep"), what="pending rep cleared")
        assert abs(sent - c.pmc()["TradersInfo"][TRADERS["prapor"]]["standing"]) < 1e-9
    _()

    @step("a map transit is not an extraction; items keep waiting until the raid really ends")
    def _():
        st0 = state()
        customs0 = st0["extractCounts"].get("customs", 0)
        gz0 = st0["extractCounts"].get("groundzero", 0)
        start = c.raid_start("bigmap")
        n = c.bot_ids()
        server.command("/send Runner Experience Bundle")
        c.wait_for_bot("takes effect when your raid ends", start_count=n, timeout=30)
        xp0 = c.pmc()["Info"]["Experience"]
        c.raid_end(start["serverId"], "Transit", exit_name="Customs_Transit")
        time.sleep(7)
        st = state()
        assert st["extractCounts"].get("customs", 0) == customs0, st["extractCounts"]
        assert st["deferred"], "the XP must keep waiting during a transit"
        assert c.pmc()["Info"]["Experience"] == xp0
        start2 = c.raid_start("Sandbox")
        c.raid_end(start2["serverId"], "Survived", exit_name="Sandbox_Exit")
        wait(lambda: state()["extractCounts"].get("groundzero", 0) == gz0 + 1, what="GZ extraction counted")
        wait(lambda: c.pmc()["Info"]["Experience"] >= xp0 + 15000, what="xp after the raid")
        assert not state()["deferred"]
    _()

    @step("restarting the game ends a raid that never finished")
    def _():
        c.raid_start("bigmap")
        c.start_game()  # the game crashed in raid and was started again
        n = c.bot_ids()
        server.command("/send Runner Euros (1,000)")
        server.command("/send Runner Experience Bundle")
        t = c.wait_for_bot("Received Experience Bundle", start_count=n, timeout=30)
        assert "when your raid ends" not in t, t
    _()

    @step("Fence Grudge still counts after a Scav raid made with an old Scav copy")
    def _():
        fence = "579dc571d53a0658a154fbec"
        old_scav = c.scav()  # loads the profile: nothing pending
        standing0 = old_scav["TradersInfo"][fence]["standing"]
        n = c.bot_ids()
        server.command("/send Runner Fence Grudge")
        c.wait_for_bot("Received Fence Grudge", start_count=n, timeout=30)
        wait(lambda: state().get("pendingScavFence", 0) < 0, what="pending fence change")
        start = c.raid_start("bigmap", side="Savage")
        side = start["serverId"].split(".")[1].lower()
        assert "savage" in side and "pmc" not in side, start["serverId"]
        c.raid_end(start["serverId"], "Killed", profile=old_scav)
        standing = c.pmc()["TradersInfo"][fence]["standing"]
        assert abs(standing - max(-7, standing0 - 0.3)) < 1e-6, (standing0, standing)
    _()

    @step("Scav Cooldown carries over to the next Scav the client gets")
    def _():
        n = c.bot_ids()
        server.command("/send Runner Scav Cooldown")
        c.wait_for_bot("Scav cooldown grows", start_count=n, timeout=30)
        wait(lambda: state().get("pendingScavLockSeconds") == 1200, what="pending scav cooldown")
        with_trap = c.regenerate_scav()["Info"]["SavageLockTime"]
        without = c.regenerate_scav()["Info"]["SavageLockTime"]
        assert 1195 <= with_trap - without <= 1205, (with_trap, without)
        assert state().get("pendingScavLockSeconds") == 0
    _()

    @step("extraction on a locked map does not count")
    def _():
        start = c.raid_start("Woods")
        c.raid_end(start["serverId"], "Survived", exit_name="UN Roadblock")
        time.sleep(8)
        assert LOC_IDS["Woods - Extraction 1"] not in watcher.checked
        c.wait_for_bot("is locked in your Archipelago game", timeout=5)
    _()

    @step("kills and boss kills send checks; a transit leg's kills are not counted twice")
    def _():
        before = dict(state().get("killCounts") or {})
        leg1 = [victim() for _ in range(5)] + [victim(role="bossBully"), victim(side="Usec", role="pmcUSEC")]
        start = c.raid_start("bigmap")
        c.raid_end(start["serverId"], "Transit", exit_name="Customs_Transit", victims=leg1)
        # The next leg reports the first leg's kills again (worst case) plus one more.
        start = c.raid_start("bigmap")
        c.raid_end(start["serverId"], "Survived", exit_name="Smuggler's Boat", victims=leg1 + [victim()])
        for name in ("Kills - 5 Scavs", "Kills - 1 PMCs", "Boss - Reshala"):
            watcher.wait_checked(LOC_IDS[name], timeout=30)
        # SPT 4.1 ends a raid asynchronously: the state is written once the mod has handled the final raid end.
        st = wait(lambda: (lambda s: s if not s.get("raidVictimsCounted")
                           and s.get("killCounts", {}).get("scav", 0) > before.get("scav", 0) else None)(state()),
                  what="raid end saved")
        assert st["killCounts"].get("scav", 0) - before.get("scav", 0) == 7, st["killCounts"]  # 6 Scavs + Reshala
        assert st["killCounts"].get("pmc", 0) - before.get("pmc", 0) == 1, st["killCounts"]
        assert "reshala" in st["bossesKilled"], st["bossesKilled"]
        assert LOC_IDS["Kills - 10 Scavs"] not in watcher.checked
    _()

    @step("kills on a locked map do not count")
    def _():
        before = dict(state()["killCounts"])
        start = c.raid_start("Woods")
        c.raid_end(start["serverId"], "Survived", exit_name="UN Roadblock",
                   victims=[victim() for _ in range(5)] + [victim(role="bossKojaniy")])
        time.sleep(8)
        st = state()
        assert st["killCounts"] == before, (before, st["killCounts"])
        assert "shturman" not in st["bossesKilled"]
        assert LOC_IDS["Boss - Shturman"] not in watcher.checked
    _()

    # ---------------------------------------------------------------- lootsanity (Runner: barter + provisions)
    def stand_ins_in(items):
        return [i for i in items if i["_tpl"] in LOOT_BY_STAND_IN]

    def map_stand_ins(start):
        return stand_ins_in([it for sp in start["locationLoot"]["Loot"] for it in sp.get("Items") or []])

    def put_in_profile(profile, tpls, where):
        """Add stand-ins to the client's raid-end profile: where is 'pockets' or 'secure'."""
        inv = profile["Inventory"]
        slot = {"pockets": "Pockets", "secure": "SecuredContainer"}[where]
        parent = next(i for i in inv["items"] if i.get("parentId") == inv["equipment"] and i.get("slotId") == slot)
        children = [i for i in inv["items"] if i.get("parentId") == parent["_id"]]
        used_slots = {i.get("slotId") for i in children}
        used_cells = {(i["location"].get("x"), i["location"].get("y")) for i in children
                      if isinstance(i.get("location"), dict)}
        for n, tpl in enumerate(tpls):
            if where == "pockets":
                place = next(s for s in ("pocket1", "pocket2", "pocket3", "pocket4") if s not in used_slots)
                used_slots.add(place)
                item = {"slotId": place}
            else:
                x, y = next((x, y) for y in range(4) for x in range(4) if (x, y) not in used_cells)
                used_cells.add((x, y))
                item = {"slotId": "main", "location": {"x": x, "y": y, "r": 0}}
            _victim_seq[0] += 1  # shared counter: every test item gets its own id
            item.update({"_id": f"{0xAB000000 + _victim_seq[0]:024x}", "_tpl": tpl,
                         "parentId": parent["_id"], "upd": {"StackObjectsCount": 1, "SpawnedInSession": True}})
            inv["items"].append(item)
        return profile

    @step("lootsanity: Archipelago Item templates exist, identified and named in every language")
    def _():
        templates_ = c.item_templates()
        sample = DATA["loot"][0]["stand_in"]
        missing = [r["stand_in"] for r in DATA["loot"] if r["stand_in"] not in templates_]
        assert not missing, f"{len(missing)} stand-in templates missing, e.g. {missing[:3]}"
        props = templates_[sample]["_props"]
        assert props.get("ExaminedByDefault") is True and props.get("Width") == 1 and props.get("Height") == 1, props
        assert props.get("CanSellOnRagfair") is False and props.get("InsuranceDisabled") is True, props
        for language in ("en", "de"):
            name = c.locale(language).get(f"{sample} Name", "")
            assert name.startswith("Archipelago Item"), (language, name)
    _()

    @step("lootsanity: a raid swaps one spawn per unchecked type, on the map and on bots")
    def _():
        start = c.raid_start("bigmap", loot=True)
        on_map = map_stand_ins(start)
        assert on_map, "expected Archipelago Items in Customs loot"
        types = [LOOT_BY_STAND_IN[i["_tpl"]] for i in on_map]
        assert len({t["tpl"] for t in types}) == len(types), "one Archipelago Item per type per raid"
        assert {t["category"] for t in types} <= {"barter", "provisions"}, {t["category"] for t in types}
        on_bots = []
        for _ in range(3):
            for bot in c.generate_bots("assault", 10):
                inv = bot["Inventory"]
                containers = {i["_id"] for i in inv["items"] if i.get("parentId") == inv["equipment"]
                              and i.get("slotId") in ("TacticalVest", "Pockets", "Backpack")}
                for item in stand_ins_in(inv["items"]):
                    assert item["parentId"] in containers, f"stand-in outside rig/pockets/backpack: {item}"
                    assert LOOT_BY_STAND_IN[item["_tpl"]]["bot_safe"], item
                    on_bots.append(item)
            if on_bots:
                break
        assert on_bots, "expected a Scav to carry an Archipelago Item"
        bot_types = {LOOT_BY_STAND_IN[i["_tpl"]]["tpl"] for i in on_bots}
        assert not bot_types & {t["tpl"] for t in types}, "a type got a second Archipelago Item in the same raid"
        assert len(bot_types) == len(on_bots), "one Archipelago Item per type per raid (bots)"

        tracker = c.call("/archipelago/client/tracker", method="GET")
        assert tracker["linked"] and tracker["raidMap"] == "customs", tracker.get("raidMap")
        first = tracker["maps"][0]
        assert first["key"] == "customs" and first["current"], first
        here = next(g for g in first["groups"] if g["title"] == "Archipelago Items this raid")
        assert here["count"] == len(on_map) + len(on_bots), (here["count"], len(on_map), len(on_bots))
        assert any(": in a " in e or ": on the ground" in e for e in here["entries"]), here["entries"][:5]
        assert any(": carried by " in e for e in here["entries"]), here["entries"][:5]
        assert any(g["title"] == "Next kill milestones" for g in tracker["anywhere"]), tracker["anywhere"]

        picked = [types[0], types[1]] if len(types) > 1 else [types[0]]
        profile = put_in_profile(c.pmc(), [t["stand_in"] for t in picked], "pockets")
        c.raid_end(start["serverId"], "Survived", exit_name="Smuggler's Boat", profile=profile)
        for t in picked:
            watcher.wait_checked(t["location_id"], timeout=30)
        st = wait(lambda: (lambda s: s if all(t["tpl"] in s.get("lootFound", []) for t in picked) else None)(state()),
                  what="loot found saved")
        assert not stand_ins_in(c.pmc()["Inventory"]["items"]), "Archipelago Items must not reach the stash"
        for t in picked:
            assert not any(i["_tpl"] == t["tpl"] for i in c.pmc()["Inventory"]["items"]
                           if i.get("upd", {}).get("SpawnedInSession")), "the original item must not be given"
    _()

    @step("lootsanity: a found type spawns normally again; on death only the secure container counts")
    def _():
        found = set(state()["lootFound"])
        start = c.raid_start("bigmap", loot=True)
        types = [LOOT_BY_STAND_IN[i["_tpl"]] for i in map_stand_ins(start)]
        assert not {t["tpl"] for t in types} & found, "a found type must not get another Archipelago Item"
        assert len(types) >= 2, "need two Archipelago Items for this step"
        kept, lost = types[0], types[1]
        profile = put_in_profile(c.pmc(), [kept["stand_in"]], "secure")
        profile = put_in_profile(profile, [lost["stand_in"]], "pockets")
        c.raid_end(start["serverId"], "Killed", profile=profile)
        watcher.wait_checked(kept["location_id"], timeout=30)
        st = wait(lambda: (lambda s: s if kept["tpl"] in s.get("lootFound", []) else None)(state()), what="saved")
        time.sleep(3)
        assert lost["tpl"] not in st["lootFound"] and lost["location_id"] not in watcher.checked
        assert not stand_ins_in(c.pmc()["Inventory"]["items"]), "Archipelago Items must not reach the stash"
    _()

    # ---------------------------------------------------------------- shop randomizer (Runner: same_category, per seed)
    def assort(trader_key):
        return c.call(f"/client/trading/api/getTraderAssort/{TRADERS[trader_key]}")

    @step("shops: randomized for the seed, same category, every offer limited, currency exchange kept")
    def _():
        shop = assort("prapor")
        vanilla = {i["_id"]: i for i in vanilla_assort(TRADERS["prapor"])["items"] if i.get("parentId") == "hideout"}
        roots = [i for i in shop["items"] if i.get("parentId") == "hideout"]
        assert roots and all(r["_id"] in vanilla for r in roots), "offers keep their ids"
        keep = {ROUBLES, USD, "569668774bdc2da2298b4568", "5c94bbff86f7747ee735c08f"}
        # The client only gets offers at its loyalty level (1) and not locked behind a quest.
        raw = vanilla_assort(TRADERS["prapor"])
        quest_locked = {oid for group in json.load(open(os.path.join(SPT, "SPT_Data", "database", "traders",
                        TRADERS["prapor"], "questassort.json"), encoding="utf-8")).values() for oid in group}
        visible = {k for k, v in vanilla.items() if raw["loyal_level_items"].get(k, 1) <= 1 and k not in quest_locked}
        randomizable = [v for k, v in vanilla.items() if k in visible and v["_tpl"] not in keep]
        stocked = [r for r in roots if vanilla[r["_id"]]["_tpl"] not in keep]
        assert 0.2 * len(randomizable) <= len(stocked) <= 0.65 * len(randomizable),             f"shop_stock 40%: {len(stocked)} of {len(randomizable)} offers in stock"
        changed = [r for r in roots if r["_tpl"] != vanilla[r["_id"]]["_tpl"]]
        assert len(changed) > len(stocked) // 2, f"only {len(changed)}/{len(stocked)} offers changed"
        prices = {i["Id"]: i.get("Price") or 0 for i in json.load(open(os.path.join(
            SPT, "SPT_Data", "database", "templates", "handbook.json"), encoding="utf-8"))["Items"]}
        ratios = sorted(prices.get(r["_tpl"], 0) / prices[vanilla[r["_id"]]["_tpl"]] for r in changed
                        if prices.get(vanilla[r["_id"]]["_tpl"]))
        median = ratios[len(ratios) // 2]
        assert 0.4 <= median <= 2.5, f"replacements should be worth about the same (median ratio {median:.2f})"
        category = handbook_category()
        wrong = [r["_tpl"] for r in changed if category.get(r["_tpl"]) != category.get(vanilla[r["_id"]]["_tpl"])]
        assert not wrong, f"{len(wrong)} offers changed category"
        assert all((r.get("upd") or {}).get("BuyRestrictionMax", 0) >= 1 for r in changed), "every offer has a limit"
        assert all(r["_id"] in shop["barter_scheme"] for r in roots)
        assert assort("prapor") == shop, "the same shop on every request (per seed)"
        pk = [i for i in vanilla_assort(TRADERS["peacekeeper"])["items"] if i.get("parentId") == "hideout" and i["_tpl"] == USD]
        if pk:
            pk_shop = {i["_id"]: i for i in assort("peacekeeper")["items"]}
            assert all(pk_shop.get(i["_id"], {}).get("_tpl") == USD for i in pk), "the dollar exchange must stay"
    _()

    @step("shops: buying an offer gives the randomized item")
    def _():
        shop = assort("prapor")
        vanilla = {i["_id"]: i for i in vanilla_assort(TRADERS["prapor"])["items"] if i.get("parentId") == "hideout"}
        money = sorted((i for i in c.pmc()["Inventory"]["items"] if i["_tpl"] == ROUBLES),
                       key=lambda i: -(i.get("upd") or {}).get("StackObjectsCount", 1))[0]
        cash = (money.get("upd") or {}).get("StackObjectsCount", 1)
        offer = next(r for r in shop["items"] if r.get("parentId") == "hideout"
                     and r["_tpl"] != vanilla[r["_id"]]["_tpl"]
                     and shop["barter_scheme"][r["_id"]][0][0]["_tpl"] == ROUBLES
                     and shop["barter_scheme"][r["_id"]][0][0]["count"] <= cash
                     and shop["loyal_level_items"].get(r["_id"], 1) == 1)
        price = shop["barter_scheme"][offer["_id"]][0][0]["count"]
        result = c.item_event([{"Action": "TradingConfirm", "type": "buy_from_trader", "tid": TRADERS["prapor"],
                                "item_id": offer["_id"], "count": 1, "scheme_id": 0,
                                "scheme_items": [{"id": money["_id"], "count": price}]}])
        new = result["profileChanges"][c.session]["items"]["new"]
        assert any(i["_tpl"] == offer["_tpl"] for i in new), (offer["_tpl"], [i["_tpl"] for i in new])
        assert not any(i["_tpl"] == vanilla[offer["_id"]]["_tpl"] for i in new
                       if vanilla[offer["_id"]]["_tpl"] != offer["_tpl"]), "never the original item"
    _()

    @step("a death in raid sends DeathLink to the multiworld")
    def _():
        mark = buddy.mark()
        start = c.raid_start("bigmap")
        c.raid_end(start["serverId"], "Killed", killer="Reshala")
        hit = buddy.wait_for(lambda p: p.get("cmd") == "Bounced" and "DeathLink" in p.get("tags", []), since=mark, timeout=30)
        assert "Runner" in hit["data"]["source"], hit
        assert "Reshala" in hit["data"]["cause"], hit
    _()

    @step("DeathLink received in raid is offered to the client plugin; no echo after the kill")
    def _():
        start = c.raid_start("bigmap")
        buddy.send([{"cmd": "Bounce", "tags": ["DeathLink"],
                     "data": {"time": time.time(), "source": "Buddy", "cause": "Buddy fell off a cliff."}}])
        poll = wait(lambda: (lambda p: p if p.get("kill") else None)(c.poll_plugin()), timeout=30, what="kill flag")
        assert "cliff" in poll["cause"]
        c.call("/archipelago/client/killed", method="GET")
        mark = buddy.mark()
        c.raid_end(start["serverId"], "Killed")
        time.sleep(8)
        echoes = buddy.find(lambda p: p.get("cmd") == "Bounced" and p.get("data", {}).get("source") == "Runner", since=mark)
        assert not echoes, echoes
    _()

    @step("DeathLink received outside a raid does nothing")
    def _():
        n = c.bot_ids()
        buddy.send([{"cmd": "Bounce", "tags": ["DeathLink"],
                     "data": {"time": time.time(), "source": "Buddy", "cause": "Buddy again."}}])
        c.wait_for_bot("safe out of raid", start_count=n, timeout=30)
        assert not c.poll_plugin()["kill"]
    _()

    @step("plugin poll returns recent messages")
    def _():
        server.command("/send Runner Euros (1,000)")
        msgs = wait(lambda: c.poll_plugin()["messages"], timeout=30, what="plugin messages")
        assert any("Euros" in m for m in msgs), msgs
    _()

    @step("a quest reward that unlocks Jaeger is blocked until the Jaeger item arrives")
    def _():
        intro = QUEST_BY_NAME["Introduction"]
        server.command("/send Runner Mechanic")
        wait(lambda: trader_unlocked(c, "mechanic"), what="mechanic")
        gunsmith = QUEST_BY_NAME["Gunsmith - Part 1"]
        c.accept_quest(gunsmith["id"])
        c.accept_quest(intro["id"])
        c.complete_quest(intro["id"])
        time.sleep(7)
        assert not trader_unlocked(c, "jaeger"), "Jaeger must stay locked"
        server.command("/send Runner Jaeger")
        wait(lambda: trader_unlocked(c, "jaeger"), what="jaeger unlocked")
    _()

    @step("status and items commands")
    def _():
        n = c.bot_ids()
        c.say("status")
        t = c.wait_for_bot("Checks:", start_count=n, timeout=15)
        assert "Customs" in t and "Skier" in t and "open" in t, t
        c.say("items")
        t = c.wait_for_bot("Received so far", start_count=n, timeout=15)
        assert "Roubles (100,000)" in t, t
    _()

    @step("chat is relayed to the multiworld and hints work")
    def _():
        mark = watcher.mark()
        c.say("hello from tarkov")
        watcher.wait_for(lambda p: p.get("cmd") == "PrintJSON" and p.get("type") == "Chat" and "hello from tarkov" in json.dumps(p),
                         since=mark, timeout=30)
        n = c.bot_ids()
        c.say("hint Woods Access")
        c.wait_for_bot("Woods Access", start_count=n, timeout=30)
    _()

    @step("restarting the SPT server reconnects and does not deliver items twice")
    def _():
        state_path = os.path.join(MOD_DIR, "profiles", f"{c.session}.json")
        wait(lambda: (lambda st: st["delivered"] == len(st["received"]))(json.load(open(state_path))), what="deliveries settled")
        time.sleep(6)
        with_items = len(deliveries(c))
        received_lines = sum(t.count("Received") for t in c.bot_texts())
        before_ids = c.bot_ids()
        spt_restart(os.path.join(WORK, "spt2.log"))
        c.wait_for_bot("Connected to Archipelago as Runner", start_count=before_ids, timeout=60)
        time.sleep(8)
        assert len(deliveries(c)) == with_items
        assert sum(t.count("Received") for t in c.bot_texts()) == received_lines
        assert trader_unlocked(c, "skier") and not trader_unlocked(c, "peacekeeper")
        state = json.load(open(os.path.join(MOD_DIR, "profiles", f"{c.session}.json")))
        assert state["delivered"] == len(state["received"]), (state["delivered"], len(state["received"]))
        loot = [r for r in state["received"] if next(i for i in DATA["items"] if i["id"] == r["item"])["kind"] in ("money", "loot")]
        assert len(loot) == with_items, (len(loot), with_items)
    _()

    @step("Archipelago server restart: the mod reconnects by itself")
    def _():
        n = c.bot_ids()
        server.stop()
        try:
            c.wait_for_bot("Lost connection", start_count=n, timeout=40)
        finally:
            server.start()
            watcher.reconnect()
            buddy.reconnect()
        c.wait_for_bot("Connected to Archipelago as Runner", start_count=n, timeout=90)
    _()

    @step("checks done while offline are sent after reconnecting")
    def _():
        c.say("disconnect")
        time.sleep(6)
        q = next(x for x in DATA["quests"] if x["check"] and x["trader"] == "therapist" and x["location_id"] not in watcher.checked)
        c.accept_quest(q["id"])
        c.complete_quest(q["id"])
        time.sleep(6)
        assert q["location_id"] not in watcher.checked
        c.say("reconnect")
        watcher.wait_checked(q["location_id"], timeout=60)
    _()

    @step("hideout levels built while the server was off are sent")
    def _():
        sh(f"{BIN}/spt-stop.sh")
        pid = c.session
        path = os.path.join(SPT, "user", "profiles", f"{pid}.json")
        prof = json.load(open(path))
        for area in prof["characters"]["pmc"]["Hideout"]["Areas"]:
            if area["type"] == 4:  # generator
                area["level"] = 1
        json.dump(prof, open(path, "w"))
        spt_restart(os.path.join(WORK, "spt3.log"))
        watcher.wait_checked(LOC_IDS["Hideout - Generator Level 1"], timeout=60)
    _()

    @step("craftsanity: the first craft of a recipe sends its check and gives no item")
    def _():
        recipes = {r["_id"]: r for r in json.load(open(os.path.join(SPT, "SPT_Data", "database", "hideout",
                                                                     "production.json"), encoding="utf-8"))["recipes"]}
        craft = next(cr for cr in DATA["crafts"] if cr["area"] == 10 and cr["level"] == 1 and not cr["quest"])
        recipe = recipes[craft["recipe"]]
        sh(f"{BIN}/spt-stop.sh")
        path = os.path.join(SPT, "user", "profiles", f"{c.session}.json")
        prof = json.load(open(path, encoding="utf-8"))
        pmc = prof["characters"]["pmc"]
        for area in pmc["Hideout"]["Areas"]:
            if area["type"] == craft["area"]:
                area["level"] = max(area.get("level", 0), craft["level"])
                area["constructing"] = False
        t = recipe["productionTime"]
        pmc["Hideout"].setdefault("Production", {})[craft["recipe"]] = {
            "Products": [], "Progress": t, "inProgress": True, "StartTimestamp": int(time.time()) - t - 60,
            "SkipTime": 0, "ProductionTime": t, "GivenItemsInStart": [], "Interrupted": False,
            "needFuelForAllProductionTime": False, "NeedFuelForAllProductionTime": False, "RecipeId": craft["recipe"],
        }
        json.dump(prof, open(path, "w", encoding="utf-8"))
        spt_restart(os.path.join(WORK, "spt-craft.log"))
        before = {i["_id"] for i in c.pmc()["Inventory"]["items"]}
        result = c.item_event([{"Action": "HideoutTakeProduction", "recipeId": craft["recipe"],
                                "timestamp": int(time.time())}])
        watcher.wait_checked(craft["location_id"], timeout=30)
        new = result["profileChanges"][c.session]["items"]["new"]
        assert not any(i["_tpl"] == craft["product"] for i in new), "the first craft must not give its product"
        assert not any(i["_tpl"] == craft["product"] and i["_id"] not in before for i in c.pmc()["Inventory"]["items"])
        assert craft["recipe"] in wait(lambda: state().get("craftsDone"), what="craft saved")
    _()

    @step("items stuck in Archipelago chat messages (1.1.0 and older) are sent again as System messages")
    def _():
        n = c.bot_ids()
        sh(f"{BIN}/spt-stop.sh")
        path = os.path.join(SPT, "user", "profiles", f"{c.session}.json")
        prof = json.load(open(path))
        dialog = prof["dialogues"][BOT_ID]
        stash = "6a0000000000000000000001"
        dialog["messages"].append({
            "_id": "6a0000000000000000000002", "uid": BOT_ID, "type": 1, "dt": int(time.time()) - 3600,
            "text": "Roubles (25,000) from Buddy (an old delivery)", "hasRewards": True, "rewardCollected": False,
            "maxStorageTime": 31536000,
            "items": {"stash": stash, "data": [{"_id": "6a0000000000000000000003", "_tpl": ROUBLES, "parentId": stash,
                                                 "slotId": "main", "upd": {"StackObjectsCount": 24680}}]},
        })
        dialog["attachmentsNew"] = 1
        json.dump(prof, open(path, "w"))
        state_file = os.path.join(MOD_DIR, "profiles", f"{c.session}.json")
        st = json.load(open(state_file))
        st["mailVersion"] = 0  # as saved by 1.1.0
        json.dump(st, open(state_file, "w"))
        spt_restart(os.path.join(WORK, "spt5.log"))
        c.wait_for_bot("sent again as System messages", start_count=n, timeout=60)
        resent = wait(lambda: next((m for m in deliveries(c) if "an old delivery" in (m.get("text") or "")), None),
                      timeout=20, what="re-sent message")
        total = sum(i.get("upd", {}).get("StackObjectsCount", 1) for i in resent["items"]["data"] if i["_tpl"] == ROUBLES)
        assert total == 24680, total
        old = next(m for m in c.dialog() if m["_id"] == "6a0000000000000000000002")
        assert not old.get("hasRewards") and not (old.get("items") or {}).get("data"), old
        # only once
        spt_restart(os.path.join(WORK, "spt6.log"))
        time.sleep(8)
        assert len([m for m in deliveries(c) if "an old delivery" in (m.get("text") or "")]) == 1
    _()

    @step("a profile cannot be reused for a different multiworld; reset works")
    def _():
        c2 = SptClient()
        c2.register_and_login("second")
        c2.create_character("Second")
        c2.start_game()
        n = c2.bot_ids()
        c2.say(f"connect 127.0.0.1:{AP_PORT} Buddy")
        c2.wait_for_bot("Connected to Archipelago as Buddy", start_count=n, timeout=40)
        # pretend this profile belonged to another seed
        state_path = os.path.join(MOD_DIR, "profiles", f"{c2.session}.json")
        wait(lambda: os.path.exists(state_path), what="state file")
        c2.say("disconnect")
        time.sleep(6)
        st = json.load(open(state_path))
        st["seedName"] = "SOMEOTHERSEED"
        sh(f"{BIN}/spt-stop.sh")
        json.dump(st, open(state_path, "w"))
        spt_restart(os.path.join(WORK, "spt4.log"))
        n = c2.bot_ids()
        c2.say("reconnect")
        c2.wait_for_bot("different Archipelago game", start_count=n, timeout=40)
        c2.say("reset confirm")
        c2.wait_for_bot("was reset", start_count=n, timeout=20)
        assert trader_unlocked(c2, "skier") and trader_unlocked(c2, "mechanic")
        assert {"bigmap", "woods"} <= enabled_maps(c2)
        assert not flea_banned(c2)
        # a reset must not quietly link the profile to the same game again
        n = c2.bot_ids()
        time.sleep(12)
        assert not [m for m in c2.bot_messages() if m["_id"] not in n and "Connected to Archipelago" in m.get("text", "")]
        assert trader_unlocked(c2, "skier") and not flea_banned(c2)
        c2.say("status")
        t = c2.wait_for_bot("Slot Buddy", start_count=n, timeout=15)
        assert "disconnected" in t and "retrying" not in t, t
        # reset while connected: the link is dropped and not made again
        n = c2.bot_ids()
        c2.say("reconnect")
        c2.wait_for_bot("Connected to Archipelago as Buddy", start_count=n, timeout=40)
        wait(lambda: not trader_unlocked(c2, "skier"), what="locks back on")
        n = c2.bot_ids()
        c2.say("reset confirm")
        c2.wait_for_bot("was reset", start_count=n, timeout=20)
        time.sleep(12)
        assert not [m for m in c2.bot_messages() if m["_id"] not in n and "Connected to Archipelago" in m.get("text", "")]
        assert trader_unlocked(c2, "skier") and not flea_banned(c2)
    _()

    @step("a profile linked to one slot refuses another slot name")
    def _():
        n = c.bot_ids()
        c.say(f"connect 127.0.0.1:{AP_PORT} Buddy")
        t = c.wait_for_bot("is linked to the slot", start_count=n, timeout=15)
        assert "Runner" in t, t
        c.say("status")
        c.wait_for_bot("Slot Runner", start_count=n, timeout=15)
    _()

    @step("an invalid server address is rejected before connecting")
    def _():
        n = c.bot_ids()
        c.say("connect localhost:99999 Runner")
        c.wait_for_bot("port", start_count=n, timeout=15)
        c.say("status")
        t = c.wait_for_bot(f"Slot Runner @ 127.0.0.1:{AP_PORT}", start_count=n, timeout=15)
        assert "connected" in t, t
    _()

    @step("goal is reported once enough quests are done")
    def _():
        mark = watcher.mark()
        quest_ids = {q["location_id"] for q in DATA["quests"] if q["check"]}
        for name in ("First in Line", "Background Check", "Shortage", "Sanitary Standards - Part 1", "Painkiller"):
            done = len(quest_ids & watcher.checked)
            if done >= 5:
                break
            q = QUEST_BY_NAME[name]
            c.accept_quest(q["id"])
            c.complete_quest(q["id"])
            watcher.wait_checked(q["location_id"], timeout=30)
        watcher.wait_for(lambda p: p.get("cmd") == "PrintJSON" and p.get("type") == "Goal", since=mark, timeout=40)
    _()

    @step("random starting kit (replace): stash and gear replaced when the game loads the profile")
    def _():
        k = SptClient()
        k.register_and_login("kitted")
        k.create_character("Kitted")
        k.start_game()
        stash_before = len(k.pmc()["Inventory"]["items"])
        n = k.bot_ids()
        k.say(f"connect 127.0.0.1:{AP_PORT} Kitted")
        k.wait_for_bot("Connected to Archipelago as Kitted", start_count=n, timeout=40)
        k.wait_for_bot("starting stash and gear are replaced", start_count=n, timeout=10)
        # the game restarts and loads the profile: that is when the stash is replaced
        k.start_game()
        pmc = k.pmc()
        inv = pmc["Inventory"]
        stash_items = [i for i in inv["items"] if i.get("parentId") == inv["stash"]]
        assert not stash_items, f"{len(stash_items)} items left in the stash"
        slots = {i["slotId"] for i in inv["items"] if i.get("parentId") == inv["equipment"]}
        assert slots <= {"Scabbard", "SecuredContainer", "Pockets", "ArmBand", "Compass", "Dogtag"}, slots
        assert len(inv["items"]) < stash_before
        kit = wait(lambda: next((m for m in deliveries(k) if "random starting kit (geared)" in (m.get("text") or "").lower()),
                                None), timeout=20, what="kit message")
        items = kit["items"]["data"]
        roots = roots_of(items)
        guns = [r for r in roots if is_a(r["_tpl"], WEAPON)]
        assert len(guns) == 1, [r["_tpl"] for r in roots]
        gun = templates()[guns[0]["_tpl"]]
        caliber = gun["_props"].get("ammoCaliber")
        ammo = [r for r in roots if is_a(r["_tpl"], AMMO)]
        assert ammo and all(templates()[a["_tpl"]]["_props"]["Caliber"] == caliber for a in ammo), (caliber, ammo)
        assert sum(a.get("upd", {}).get("StackObjectsCount", 1) for a in ammo) == 240
        mags = [r for r in roots if is_a(r["_tpl"], MAGAZINE)]
        if gun["_props"].get("ReloadMode") == "ExternalMagazine":
            allowed = next(s for s in gun["_props"]["Slots"] if s["_name"] == "mod_magazine")["_props"]["filters"][0]["Filter"]
            assert len(mags) == 3 and all(m["_tpl"] in allowed for m in mags), mags
        body = [r for r in roots if is_a(r["_tpl"], ARMOR)]
        assert len(body) == 1 and protection(body[0], items) == 4, [protection(b, items) for b in body]
        helmets = [r for r in roots if is_a(r["_tpl"], HEADWEAR)]
        assert len(helmets) == 1 and protection(helmets[0], items) > 0
        assert [r for r in roots if is_a(r["_tpl"], VEST)] and [r for r in roots if is_a(r["_tpl"], BACKPACK)]
        money = sum(r.get("upd", {}).get("StackObjectsCount", 1) for r in roots if r["_tpl"] == ROUBLES)
        assert 300_000 <= money <= 600_000, money
        # once only: a later profile load changes nothing
        k.start_game()
        assert len([m for m in deliveries(k) if "your random starting kit (" in (m.get("text") or "").lower()]) == 1
        assert len(k.pmc()["Inventory"]["items"]) == len(inv["items"])
    _()

    @step("random starting kit: a raid before restarting keeps the stash and sends the kit as a message")
    def _():
        k = SptClient()
        k.register_and_login("kitted2")
        k.create_character("Kittedtwo")
        k.start_game()
        def stash_count():
            inv = k.pmc()["Inventory"]
            return len([i for i in inv["items"] if i.get("parentId") == inv["stash"]])
        before = stash_count()
        n = k.bot_ids()
        k.say(f"connect 127.0.0.1:{AP_PORT} Kitted2")
        k.wait_for_bot("starting stash and gear are replaced", start_count=n, timeout=40)
        start = k.raid_start("Sandbox")
        k.wait_for_bot("so your stash is kept", start_count=n, timeout=10)
        kit = wait(lambda: next((m for m in deliveries(k) if "random starting kit (scrappy)" in (m.get("text") or "").lower()),
                                None), timeout=30, what="kit message")
        k.raid_end(start["serverId"], "Left")
        k.start_game()
        assert stash_count() == before, "the stash must be kept"
        money = sum(r.get("upd", {}).get("StackObjectsCount", 1) for r in roots_of(kit["items"]["data"]) if r["_tpl"] == ROUBLES)
        assert 30_000 <= money <= 80_000, money
    _()

    # ---------------------------------------------------------------- Archipelago contracts (Contractor: 40 contracts)
    ARCH = "617263686970656c61676f30"
    CONTRACT_BASE = 76_400_000 + 300_000
    KAPPA = "5c093ca986f7740a1867ab12"
    contractor = Watcher("Contractor").start()
    k = SptClient()
    done = set()

    def contract_quests():
        return {q["_id"]: q for q in k.call("/client/quest/list")}

    def slot_contracts():
        st = json.load(open(os.path.join(MOD_DIR, "profiles", f"{k.session}.json"), encoding="utf-8"))
        return {ct["index"]: ct for ct in st["slotData"]["contracts"]}

    def quest_id_for(index, quests=None):
        quests = quests or contract_quests()
        prefix = "Contract - Kappa" if index == 0 else f"Contract {index}:"
        return next((qid for qid, q in quests.items() if q["name"].startswith(prefix)), None)

    def finish(index):
        qid = quest_id_for(index)
        assert qid, f"contract {index} is not offered"
        k.accept_quest(qid)
        k.complete_quest(qid)
        done.add(index)
        return qid

    @step("contracts: a linked profile sees only the Archipelago trader's contracts; others never see them")
    def _():
        k.register_and_login("contractor")
        k.create_character("Contractor")
        k.start_game()
        n = k.bot_ids()
        k.say(f"connect 127.0.0.1:{AP_PORT} Contractor")
        k.wait_for_bot("Connected to Archipelago as Contractor", start_count=n, timeout=40)
        k.wait_for_bot("Archipelago contracts this game: 40 of them", start_count=n, timeout=10)
        quests = contract_quests()
        assert quests and all(q["traderId"] == ARCH for q in quests.values()), [q["QuestName"] for q in quests.values()][:5]
        assert len(quests) == 3, sorted(q["name"] for q in quests.values())
        assert all(q["name"].startswith(("Contract 1:", "Contract 2:", "Contract 3:")) for q in quests.values())
        assert any(t["_id"] == ARCH for t in k.call("/client/trading/api/traderSettings"))
        # A vanilla-quest profile: no contracts, no Archipelago trader.
        assert not any(q["traderId"] == ARCH for q in c.call("/client/quest/list"))
        assert not any(t["_id"] == ARCH for t in c.call("/client/trading/api/traderSettings"))
    _()

    # ---------------------------------------------------------------- the Archipelago trader's stock
    MED_BASES = {"5448f39d4bdc2d0a728b4568", "5448f3ac4bdc2dce718b4569", "5448f3a14bdc2d27728b4569"}
    WEAPON_BASE = "5422acb9af1c889c16000029"
    AMMO_BASE = "5485a8684bdc2da71d8b4567"

    AMMO_BOX_BASE = "543be5cb4bdc2deb348b4568"

    def merchant():
        return k.call(f"/client/trading/api/getTraderAssort/{ARCH}")

    def merchant_tpls(shop=None):
        shop = shop or merchant()
        return {i["_tpl"] for i in shop["items"] if i.get("parentId") == "hideout"}

    def vanilla_ll1_tpls(trader_key):
        a = vanilla_assort(TRADERS[trader_key])
        quest_locked = set()
        for group in json.load(open(os.path.join(SPT, "SPT_Data", "database", "traders", TRADERS[trader_key],
                                                 "questassort.json"), encoding="utf-8")).values():
            quest_locked.update(group)
        return {i["_tpl"] for i in a["items"] if i.get("parentId") == "hideout"
                and a["loyal_level_items"].get(i["_id"], 1) == 1 and i["_id"] not in quest_locked}

    @step("contracts: every trader but the Archipelago one is locked")
    def _():
        bases = k.call("/client/trading/api/traderSettings")
        assert any(t["_id"] == ARCH for t in bases)
        assert all(not t.get("unlockedByDefault") for t in bases if t["_id"] != ARCH), \
            [t["nickname"] for t in bases if t.get("unlockedByDefault") and t["_id"] != ARCH]
        info = wait(lambda: (lambda p: p if p["TradersInfo"].get(TRADERS["prapor"], {}).get("unlocked") is False
                             else None)(k.pmc()), what="trader locks")["TradersInfo"]
        opened = [tid for tid, t in info.items() if t.get("unlocked") and tid != ARCH]
        assert not opened, f"traders still open: {opened}"
    _()

    def received_traders():
        st = json.load(open(os.path.join(MOD_DIR, "profiles", f"{k.session}.json"), encoding="utf-8"))
        by_id = {i["id"]: i for i in DATA["items"]}
        return {by_id[r["item"]]["params"]["trader"] for r in st.get("received", [])
                if by_id.get(r["item"], {}).get("kind") == "trader"}

    def guaranteed(tpl):
        return any(is_a(tpl, b) for b in MED_BASES) or is_a(tpl, AMMO_BASE) or is_a(tpl, AMMO_BOX_BASE)

    @step("contracts: the Archipelago trader sells received traders' stock, medications and ammo for owned guns")
    def _():
        shop = merchant()
        tpls = merchant_tpls(shop)
        have = received_traders()
        assert {"prapor", "therapist"} <= have, have  # Contractor's starting traders
        offered = set()
        for key in have:
            missing = vanilla_ll1_tpls(key) - tpls
            assert not missing, f"{key}'s loyalty 1 stock missing: {sorted(missing)[:5]}"
            offered |= vanilla_ll1_tpls(key)
        for key in set(TRADERS) - have:
            early = {t for t in vanilla_ll1_tpls(key) - offered if not guaranteed(t)} & tpls
            assert not early, f"{key}'s stock without the {key} item: {[templates()[t]['_name'] for t in sorted(early)][:8]}"
        meds = set()
        for key in TRADERS:
            a = vanilla_assort(TRADERS[key])
            meds |= {i["_tpl"] for i in a["items"] if i.get("parentId") == "hideout"
                     and a["loyal_level_items"].get(i["_id"], 1) == 1 and any(is_a(i["_tpl"], b) for b in MED_BASES)}
        assert meds and meds <= tpls, f"medications missing: {sorted(meds - tpls)[:5]}"
        guns = [i for i in k.pmc()["Inventory"]["items"] if is_a(i["_tpl"], WEAPON_BASE)]
        guns += [i for i in shop["items"] if i.get("parentId") == "hideout" and is_a(i["_tpl"], WEAPON_BASE)]
        sold = set()
        for i in shop["items"]:
            if i.get("parentId") == "hideout" and is_a(i["_tpl"], AMMO_BASE):
                sold.add(i["_tpl"])
            elif i.get("parentId") == "hideout" and is_a(i["_tpl"], AMMO_BOX_BASE):
                sold |= {c["_tpl"] for c in shop["items"] if c.get("parentId") == i["_id"] and is_a(c["_tpl"], AMMO_BASE)}
        unserved = []
        for gun in {g["_tpl"] for g in guns}:
            takes = {t for ch in templates()[gun]["_props"].get("Chambers", [])
                     for f in ch["_props"]["filters"] for t in f.get("Filter", []) if is_a(t, AMMO_BASE)}
            if takes and not (takes & sold):
                unserved.append(templates()[gun]["_name"])
        assert guns and not unserved, f"no ammo for {unserved}"
        assert all(shop["loyal_level_items"].get(r, 1) == 1 for r in shop["barter_scheme"])
    _()

    @step("contracts: buying from the Archipelago trader works; selling to it pays")
    def _():
        shop = merchant()
        money = sorted((i for i in k.pmc()["Inventory"]["items"] if i["_tpl"] == ROUBLES),
                       key=lambda i: -(i.get("upd") or {}).get("StackObjectsCount", 1))[0]
        cash = (money.get("upd") or {}).get("StackObjectsCount", 1)
        offer = next(r for r in shop["items"] if r.get("parentId") == "hideout"
                     and shop["barter_scheme"][r["_id"]][0][0]["_tpl"] == ROUBLES
                     and shop["barter_scheme"][r["_id"]][0][0]["count"] <= cash
                     and any(is_a(r["_tpl"], b) for b in MED_BASES))
        price = shop["barter_scheme"][offer["_id"]][0][0]["count"]
        result = k.item_event([{"Action": "TradingConfirm", "type": "buy_from_trader", "tid": ARCH,
                                "item_id": offer["_id"], "count": 1, "scheme_id": 0,
                                "scheme_items": [{"id": money["_id"], "count": price}]}])
        new = result["profileChanges"][k.session]["items"]["new"]
        assert any(i["_tpl"] == offer["_tpl"] for i in new), \
            (offer["_id"], offer["_tpl"], price, cash, result.get("warnings"), [i["_tpl"] for i in new])
        bought = next(i for i in new if i["_tpl"] == offer["_tpl"])
        before = sum((i.get("upd") or {}).get("StackObjectsCount", 1) for i in k.pmc()["Inventory"]["items"]
                     if i["_tpl"] == ROUBLES)
        k.item_event([{"Action": "TradingConfirm", "type": "sell_to_trader", "tid": ARCH,
                       "items": [{"id": bought["_id"], "count": 1, "scheme_id": 0}], "price": 1}])
        after = sum((i.get("upd") or {}).get("StackObjectsCount", 1) for i in k.pmc()["Inventory"]["items"]
                    if i["_tpl"] == ROUBLES)
        assert after > before, f"selling paid nothing ({before} -> {after})"
    _()

    @step("contracts: receiving a trader adds its stock to the Archipelago trader; the trader stays locked")
    def _():
        missing_traders = sorted(set(TRADERS) - received_traders())
        key = missing_traders[0] if missing_traders else "skier"
        name = next(t["name"] for t in DATA["traders"] if t["key"] == key)
        item = next(i["name"] for i in DATA["items"] if i.get("kind") == "trader" and i["params"]["trader"] == key)
        n = k.bot_ids()
        server.command(f"/send Contractor {item}")
        k.wait_for_bot(f"The Archipelago trader now sells {name}'s stock", start_count=n, timeout=30)
        missing = vanilla_ll1_tpls(key) - merchant_tpls()
        assert not missing, f"{name}'s stock missing: {sorted(missing)[:5]}"
        time.sleep(3)
        assert k.pmc()["TradersInfo"][TRADERS[key]].get("unlocked") is False
    _()

    @step("contracts: objective text is in the client locale")
    def _():
        quests = contract_quests()
        loc = k.locale("en")
        for q in quests.values():
            for cond in q["conditions"]["AvailableForFinish"]:
                text = loc.get(cond["id"], "")
                assert text.startswith(("Eliminate", "Survive", "Hand over")), (cond["id"], text)
        assert loc.get(f"{ARCH} Nickname") == "Archipelago"
    _()

    @step("contracts: finishing one sends its check, pays XP and standing, and offers what follows it")
    def _():
        contracts = slot_contracts()
        before = k.pmc()
        xp_before = before["Info"]["Experience"]
        client = TRADERS[contracts[1]["client"]]
        standing_before = before["TradersInfo"].get(client, {}).get("standing", 0)
        finish(1)
        contractor.wait_checked(CONTRACT_BASE + 1, timeout=30)
        after = k.pmc()
        assert after["Info"]["Experience"] > xp_before
        assert after["TradersInfo"][client]["standing"] > standing_before
        offered = contract_quests()
        for i, ct in contracts.items():
            if ct["prereqs"] == [1]:
                assert quest_id_for(i, offered), f"contract {i} should be offered after contract 1"
    _()

    def prapor_offers():
        return {i["_id"] for i in k.call(f"/client/trading/api/getTraderAssort/{TRADERS['prapor']}")["items"]
                if i.get("parentId") == "hideout"}

    def tracker_lists(index, where):
        tracker = k.call("/archipelago/client/tracker", method="GET")
        entry = next((m for m in tracker["maps"] if m["key"] == where), {"groups": []})
        group = next((g for g in entry["groups"] if g["title"] == "Quests in progress"), {"entries": []})
        return any(e.startswith(f"Contract {index}:") for e in group["entries"]) or None

    tracker_checked = []

    def check_tracker(index, contract):
        """Accept a contract with a map objective and see it under that map in the tracker. Only a contract whose
        location isn't checked yet shows: when another slot reaches its goal, Archipelago collects that slot's items
        from this world and those locations count as done."""
        where = next((o["map"] for o in contract["objectives"] if o.get("map")), None)
        st = json.load(open(os.path.join(MOD_DIR, "profiles", f"{k.session}.json"), encoding="utf-8"))
        if tracker_checked or where is None or CONTRACT_BASE + index in set(st.get("checked", [])):
            return
        k.accept_quest(quest_id_for(index))
        try:
            wait(lambda: tracker_lists(index, where), timeout=10, what="contract in the tracker")
        except AssertionError:
            raise AssertionError(f"contract {index} not under {where} in the tracker: "
                                 f"{k.call('/archipelago/client/tracker', method='GET')}")
        tracker_checked.append(index)

    @step("contracts: offers Tarkov's quests would unlock open up; the tracker lists taken contracts by map")
    def _():
        quest_locked = set()
        for group in json.load(open(os.path.join(SPT, "SPT_Data", "database", "traders", TRADERS["prapor"],
                                                 "questassort.json"), encoding="utf-8")).values():
            quest_locked.update(group)
        early = prapor_offers() & quest_locked
        contracts = slot_contracts()
        for i in sorted(contracts):
            if i != 0 and i not in done and len(done) < 20:
                check_tracker(i, contracts[i])
                finish(i)
        later = prapor_offers() & quest_locked
        assert len(later) > len(early), f"quest-locked Prapor offers: {len(early)} -> {len(later)}"
        assert tracker_checked, "no unchecked contract with a map objective among the first 20"
    _()

    @step("contracts: the Kappa contract needs every contract, rewards the Kappa container and is the goal")
    def _():
        mark = contractor.mark()
        for i in sorted(slot_contracts()):
            if i != 0 and i not in done:
                assert not quest_id_for(0), "Kappa must not be offered before every contract is done"
                finish(i)
        for i in range(1, 41):
            contractor.wait_checked(CONTRACT_BASE + i, timeout=30)
        finish(0)
        contractor.wait_checked(CONTRACT_BASE, timeout=30)
        contractor.wait_for(lambda p: p.get("cmd") == "PrintJSON" and p.get("type") == "Goal", since=mark, timeout=40)
        got = any(i["_tpl"] == KAPPA for i in k.pmc()["Inventory"]["items"])
        if not got:
            for d in k.call("/client/mail/dialog/list") or []:
                for m in k.dialog(d["_id"], d.get("type", 2)):
                    if any(i.get("_tpl") == KAPPA for i in ((m.get("items") or {}).get("data") or [])):
                        got = True
        assert got, "the Kappa container was not given"
    _()

    @step("contracts: main traders' loyalty comes from player level and standing (nothing can be sold to them)")
    def _():
        k.item_event([{"Action": "ReadEncyclopedia", "ids": []}])
        pmc = k.pmc()
        level = pmc["Info"]["Level"]
        raised = 0
        for key, tid in TRADERS.items():
            base = json.load(open(os.path.join(SPT, "SPT_Data", "database", "traders", tid, "base.json"), encoding="utf-8"))
            standing = pmc["TradersInfo"][tid].get("standing", 0)
            expected = 0
            for ll in base["loyaltyLevels"]:
                if ll["minLevel"] <= level and ll["minStanding"] <= standing + 1e-9 and expected < 4:
                    expected += 1
                else:
                    break
            expected = max(1, expected)
            info = pmc["TradersInfo"][tid]
            got = 0  # what the game client works out (SPT doesn't send the level)
            for ll in base["loyaltyLevels"]:
                if (ll["minLevel"] <= level and ll["minStanding"] <= info.get("standing", 0) + 1e-9
                        and ll["minSalesSum"] <= info.get("salesSum", 0) and got < 4):
                    got += 1
                else:
                    break
            got = max(1, got)
            assert got == expected, \
                f"{key}: loyalty {got}, expected {expected} (level {level}, standing {standing}): {pmc['TradersInfo'][tid]}"
            raised += expected > 1
        assert raised, "the contract line should raise some loyalty levels"
    _()
    contractor.close()

    @step("a game made with a different APWorld data version is refused")
    def _():
        bad = patch_multidata(server.multidata, os.path.join(WORK, "gen", "out", "otherdata.archipelago"),
                              {"data_hash": "0000000000000000000000000000000000000000"})
        other = MultiServer(bad, port=38282, logfile=os.path.join(WORK, "multiserver2.log"))
        other.start()
        try:
            c3 = SptClient()
            c3.register_and_login("third")
            c3.create_character("Third")
            c3.start_game()
            n = c3.bot_ids()
            c3.say("connect 127.0.0.1:38282 Runner")
            c3.wait_for_bot("different version of the Escape from Tarkov APWorld", start_count=n, timeout=40)
            time.sleep(6)
            assert trader_unlocked(c3, "skier") and not flea_banned(c3), "nothing may be locked"
            st = json.load(open(os.path.join(MOD_DIR, "profiles", f"{c3.session}.json")))
            assert st.get("seedName") is None and not st.get("autoConnect"), st
        finally:
            other.stop()
    _()

    @step("server log has no errors from the mod")
    def _():
        bad = []
        for log in ("spt1.log", "spt2.log", "spt3.log", "spt4.log", "spt5.log", "spt6.log"):
            p = os.path.join(WORK, log)
            if os.path.exists(p):
                for line in open(p, errors="replace"):
                    if "Archipelago" in line and ("error" in line.lower() or "exception" in line.lower() or "could not install" in line):
                        bad.append(line.strip())
        assert not bad, bad[:10]
    _()


if __name__ == "__main__":
    main()

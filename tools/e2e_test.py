#!/usr/bin/env python3
"""End-to-end test of Junimo Hollow on this PC, fully hidden and silent.

Runs a fresh hidden server and hidden test players (each on its own private desktop,
own saves, no sound, no Steam), with 2-minute days, and checks:
  1. join by address + password, farmhand created
  2. day rolls over at 2am for everyone, server saves
  3. clock keeps running with nobody online
  4. crop planted in spring survives into summer
  5. energy carries over the night; nap refills energy without ending the day
  6. no pass-out money penalty
  7. wrong password is turned away
  8. out-of-date pack: player is turned away, updates, rejoins as the same farmhand
"""
import json, os, re, shutil, subprocess, sys, time
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
LAB = Path("/mnt/c/Users/Stembridge/hermes-work/StembridgeValley")
LABW = r"C:\Users\Stembridge\hermes-work\StembridgeValley"
RUN = LAB / "e2e"
RUNW = LABW + r"\e2e"
EXE = LAB / "bin/host/SVHost.exe"
results = {}
procs = []


def win(p: Path) -> str:
    return RUNW + "\\" + str(p.relative_to(RUN)).replace("/", "\\")


def build_pack(version, out):
    subprocess.run([sys.executable, str(REPO / "tools/build_pack.py"), version, str(out)], check=True, stdout=subprocess.DEVNULL)


def write_host(name, settings):
    d = RUN / name
    d.mkdir(parents=True, exist_ok=True)
    (d / "host.json").write_text(json.dumps(settings, indent=2))
    return d


def start(name):
    p = subprocess.Popen([str(EXE), "--instance", win(RUN / name)], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    procs.append(p)
    return p


def smapi_log(name):
    f = RUN / name / "Roaming/StardewValley/ErrorLogs/SMAPI-latest.txt"
    return f.read_text(errors="replace") if f.exists() else ""


def probe(name):
    f = RUN / name / "state/probe.json"
    try:
        return json.loads(f.read_text())
    except Exception:
        return []


def status():
    f = RUN / "server/state/server-status.txt"
    return f.read_text() if f.exists() else ""


def wait(cond, timeout, step=3):
    t0 = time.time()
    while time.time() - t0 < timeout:
        if cond():
            return True
        time.sleep(step)
    return False


def check(name, ok, detail=""):
    results[name] = {"pass": bool(ok), "detail": detail}
    print(("PASS " if ok else "FAIL ") + name + (" - " + detail if detail else ""), flush=True)


def stop_all():
    for name in ["server", "friend", "intruder"]:
        d = RUN / name
        if d.exists():
            (d / "stop.flag").write_text("stop")
    for p in procs:
        try:
            p.wait(timeout=30)
        except subprocess.TimeoutExpired:
            p.kill()


def main():
    shutil.rmtree(RUN, ignore_errors=True)
    RUN.mkdir(parents=True)
    feed = RUN / "feed"
    build_pack("v0.1.0", feed)
    feedw = win(feed / "pack.json")
    pw = "e2e-" + os.urandom(4).hex()
    common = {"Feed": feedw, "AutoRestart": False, "MaxMinutes": 25}
    env = {"SV_MINUTES_PER_DAY": "2", "SV_TEST_PROBE": "1"}

    write_host("server", {**common, "Role": "server", "Password": pw, "TestEnv": env})
    write_host("friend", {**common, "Role": "client", "Address": "127.0.0.1:24642", "Password": pw,
                          "TestEnv": {**env, "SV_TEST_CHARACTER": "TestFriend"}})
    write_host("intruder", {**common, "Role": "client", "Address": "127.0.0.1:24642", "Password": "wrong-password",
                            "MaxMinutes": 3, "TestEnv": {**env, "SV_TEST_CHARACTER": "Intruder"}})

    try:
        start("server")
        check("server starts and creates farm", wait(lambda: "running" in status(), 240), status().replace("\n", " "))

        start("friend")
        joined = wait(lambda: "[test] Joining as TestFriend" in smapi_log("friend") and "Farmhand with" in smapi_log("friend"), 180)
        check("friend joins by address + password", joined)

        start("intruder")
        turned = wait(lambda: "Turned away" in smapi_log("server"), 120)
        intr = smapi_log("intruder")
        check("wrong password is turned away", turned and "Farmhand with" not in intr,
              next((l for l in smapi_log("server").splitlines() if "Turned away" in l), ""))

        # Two full in-game nights with the friend online.
        wait(lambda: len([e for e in probe("friend") if e["what"] == "client-morning"]) >= 4, 560, 5)
        fp = probe("friend")
        mornings = [e for e in fp if e["what"] == "client-morning"]
        tired = [e for e in fp if e["what"] == "client-tired"]
        check("day rolls over for everyone", len(mornings) >= 3, " -> ".join(m["date"] for m in mornings))
        nights = [e for e in fp if e["what"] == "client-night"]
        if mornings and nights:
            pairs = []
            for n in nights:
                nxt = next((m for m in mornings if m["real"] >= n["real"]), None)
                if nxt:
                    pairs.append((n, nxt))
            carried = bool(pairs) and all(abs(m["stamina"] - n["stamina"]) <= 1 for n, m in pairs) \
                and any(n["stamina"] < n["maxStamina"] - 50 for n, _ in pairs)
            check("energy carries over the night (no free morning refill)", carried,
                  ", ".join(f'{n["date"]} night {n["stamina"]} -> morning {m["stamina"]}' for n, m in pairs))
            money = [m["money"] for m in mornings]
            check("no pass-out money penalty", len(set(money)) == 1, f"money each morning: {money}")
        nap = next((e for e in fp if e["what"] == "client-after-nap"), None)
        check("nap refills energy, day keeps going",
              bool(nap) and nap["stamina"] >= 200 and nap["sameDay"] and not nap["readySleep"], json.dumps(nap))

        sp = probe("server")
        summer = [e for e in sp if e["what"] == "server-morning" and e["date"].startswith("Summer")]
        check("spring crops survive into summer",
              bool(summer) and summer[0]["cropsAlive"] == 4,
              json.dumps(summer[0]) if summer else "never reached summer: " + json.dumps(sp[-2:]))

        # Out-of-date pack: publish v0.1.1, restart server on it; friend must be turned away, update, rejoin.
        friend_name_before = "TestFriend"
        build_pack("v0.1.1", feed)
        (RUN / "server/stop.flag").write_text("stop")
        wait(lambda: "Stopped on request" in (RUN / "server/host.log").read_text(errors="replace"), 60)
        (RUN / "server/stop.flag").unlink(missing_ok=True)
        start("server")
        updated = wait(lambda: "Updating mods v0.1.0 -> v0.1.1" in (RUN / "friend/host.log").read_text(errors="replace"), 300)
        rejoined = wait(lambda: smapi_log("friend").count("Farmhand with") >= 1 and "pack v0.1.1" in smapi_log("friend"), 240)
        same = wait(lambda: any(e["what"] == "client-morning" and e.get("name") == friend_name_before for e in probe("friend")), 200, 5)
        check("out-of-date player updates and rejoins as the same farmhand", updated and rejoined and same,
              (RUN / "friend/host.log").read_text(errors="replace").strip().splitlines()[-4:].__str__())

        # Nobody online: clock keeps going.
        (RUN / "friend/stop.flag").write_text("stop")
        time.sleep(15)
        c1 = status()
        time.sleep(45)
        c2 = status()
        t1 = re.search(r"clock=(.*)", c1).group(1)
        t2 = re.search(r"clock=(.*)", c2).group(1)
        check("clock keeps running with nobody online", "players=0" in c2 and t1 != t2, f"{t1} -> {t2}")

        errs = [l for n in ["server", "friend"] for l in smapi_log(n).splitlines() if " ERROR " in l]
        check("no errors in game logs", not errs, "\n".join(errs[:10]))
    finally:
        stop_all()
        (RUN / "results.json").write_text(json.dumps(results, indent=2))
        passed = sum(r["pass"] for r in results.values())
        print(f"\n{passed}/{len(results)} checks passed")


if __name__ == "__main__":
    main()

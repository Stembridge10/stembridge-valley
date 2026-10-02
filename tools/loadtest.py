#!/usr/bin/env python3
"""Load test: start N bot players (Linux copies of Stardew in WSL, no window, no sound) against a server.

Usage: loadtest.py start <first> <count> <address> <password>
       loadtest.py status
       loadtest.py stop
Each bot gets its own instance folder under loadtest/bots/botNN with its own saves and player key.
"""
import json, os, subprocess, sys, time, uuid
from pathlib import Path

LT = Path.home() / "work-artifacts/stardew-mmo/loadtest"
BOTS = LT / "bots"
DOTNET = LT / "dotnet6/dotnet"


def start(first, count, address, password):
    BOTS.mkdir(exist_ok=True)
    for i in range(first, first + count):
        d = BOTS / f"bot{i:02d}"
        (d / "state").mkdir(parents=True, exist_ok=True)
        hj = d / "host.json"
        key = json.loads(hj.read_text())["PlayerKey"] if hj.exists() else uuid.uuid4().hex
        hj.write_text(json.dumps({
            "Role": "client", "Address": address, "Password": password, "PlayerKey": key,
            "GamePath": str(LT / "game"), "Feed": str(LT / "feed/pack.json"), "AutoRestart": True,
            "TestEnv": {"SV_BOT": "1", "SV_TEST_CHARACTER": f"Bot{i:02d}"},
        }, indent=2))
        env = dict(os.environ, DOTNET_ROOT=str(LT / "dotnet6"), SDL_AUDIODRIVER="dummy", ALSOFT_DRIVERS="null",
                   LIBGL_ALWAYS_SOFTWARE="1", LD_LIBRARY_PATH=str(LT / "game"), HOME=str(d),
                   DOTNET_GCConserveMemory="9", DOTNET_GCgen0size="0x1000000", DOTNET_TieredCompilation="1",
                   LP_NUM_THREADS="1", GALLIUM_DRIVER="llvmpipe")
        log = open(d / "run.log", "ab")
        p = subprocess.Popen(["xvfb-run", "-a", "-s", "-screen 0 800x600x24", str(DOTNET), str(LT / "host/SVHost.dll"),
                              "--instance", str(d)], cwd=str(LT), env=env, stdout=log, stderr=subprocess.STDOUT,
                             stdin=subprocess.DEVNULL, start_new_session=True)
        (d / "pid").write_text(str(p.pid))
        print(f"bot{i:02d} started pid {p.pid}")
        time.sleep(4)


def status():
    for d in sorted(BOTS.glob("bot*")):
        csv = d / "state/bot.csv"
        last = csv.read_text().strip().splitlines()[-1] if csv.exists() else "-"
        print(d.name, last)


def stop():
    for d in sorted(BOTS.glob("bot*")):
        (d / "stop.flag").write_text("stop")
    time.sleep(5)
    for d in sorted(BOTS.glob("bot*")):
        pid = d / "pid"
        if pid.exists():
            try:
                os.killpg(int(pid.read_text()), 15)
            except (ProcessLookupError, PermissionError):
                pass
            pid.unlink()
    print("stopped")


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "start":
        start(int(sys.argv[2]), int(sys.argv[3]), sys.argv[4], sys.argv[5])
    elif cmd == "status":
        status()
    elif cmd == "stop":
        stop()

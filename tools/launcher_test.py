#!/usr/bin/env python3
"""Launcher test with a stand-in game (nothing opens on screen).

Checks: invite code is read, the pack is downloaded into the launcher's own Mods folder,
SMAPI is started with that folder + the right connection details, an out-of-date pack
is updated and relaunched, a tampered download is refused, and the real game folder is untouched.
"""
import hashlib, json, os, shutil, subprocess, sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
LAB = Path("/mnt/c/Users/Stembridge/hermes-work/StembridgeValley/launcher-test")
LABW = r"C:\Users\Stembridge\hermes-work\StembridgeValley\launcher-test"
LAUNCHER = REPO / "out/launcher/Play Junimo Hollow.exe"
FAKE = REPO / "tests/FakeSmapi/bin/Release/net8.0/win-x64"
REAL_MODS = Path("/mnt/c/Program Files (x86)/Steam/steamapps/common/Stardew Valley/Mods")
results = {}


def w(p: Path) -> str:
    return LABW + "\\" + str(p.relative_to(LAB)).replace("/", "\\")


def check(name, ok, detail=""):
    results[name] = bool(ok)
    print(("PASS " if ok else "FAIL ") + name + (" - " + detail if detail else ""), flush=True)


def snapshot(d: Path):
    h = hashlib.sha256()
    # Top level + one level down (every mod's folder and manifest) is enough to notice any install/change.
    # Skips config.json: mods rewrite their own settings while the owner is playing, which isn't the launcher.
    for f in sorted(list(d.iterdir()) + [g for x in d.iterdir() if x.is_dir() for g in x.iterdir()]):
        if f.name != "config.json":
            st = f.stat()
            h.update(f"{f.relative_to(d)}|{st.st_size}|{int(st.st_mtime)}".encode())
    return h.hexdigest()


def run_launcher(root: Path, *args):
    env = dict(os.environ, SV_LAUNCHER_ROOT=w(root), WSLENV="SV_LAUNCHER_ROOT")
    return subprocess.run([str(LAUNCHER), "--headless", *args], env=env, input="",
                          capture_output=True, text=True, timeout=180)


def main():
    shutil.rmtree(LAB, ignore_errors=True)
    game = LAB / "game"
    game.mkdir(parents=True)
    (game / "Stardew Valley.dll").write_text("stand-in")
    for f in FAKE.iterdir():
        shutil.copy2(f, game / f.name)

    feed = LAB / "feed"
    for v in ["v0.1.0", "v0.1.1"]:
        subprocess.run([sys.executable, str(REPO / "tools/build_pack.py"), v, str(LAB / v)], check=True, stdout=subprocess.DEVNULL)
    feed.mkdir()
    shutil.copy2(LAB / "v0.1.0/pack.json", feed / "pack.json")
    (game / "bump-on-first-run.txt").write_text(w(LAB / "v0.1.1/pack.json") + "\n" + w(feed / "pack.json") + "\n")

    root = LAB / "player"
    root.mkdir()
    (root / "launcher.json").write_text(json.dumps({"GamePath": w(game), "Feed": w(feed / "pack.json")}))
    real_before = snapshot(REAL_MODS)

    r = run_launcher(root, "sv:127.0.0.1:24642/acorn-berry-fig-42")
    out = r.stdout
    check("launcher exits cleanly", r.returncode == 0, f"code {r.returncode}")
    runs = sorted(game.glob("run-*.txt"))
    check("game started twice (once, then again after the update)", len(runs) == 2, str([p.name for p in runs]))
    if runs:
        first = dict(l.split("=", 1) for l in runs[0].read_text().splitlines())
        last = dict(l.split("=", 1) for l in runs[-1].read_text().splitlines())
        check("uses its own Mods folder", w(root / "Mods") in first["args"] and "--mods-path" in first["args"], first["args"])
        check("passes the invite address + password", first["SV_ADDRESS"] == "127.0.0.1:24642" and first["SV_PASSWORD"] == "acorn-berry-fig-42")
        check("player key created", len(first["SV_PLAYER_KEY"]) == 32)
        check("updated pack used on relaunch", first["SV_PACK_VERSION"] == "v0.1.0" and last["SV_PACK_VERSION"] == "v0.1.1",
              f'{first["SV_PACK_VERSION"]} -> {last["SV_PACK_VERSION"]}')
        check("same player key after update", first["SV_PLAYER_KEY"] == last["SV_PLAYER_KEY"])
    mods = root / "Mods"
    check("mods installed", (mods / "StembridgeValley/StembridgeValley.dll").exists()
          and json.loads((mods / "StembridgeValley/manifest.json").read_text())["Version"] == "0.1.1")
    check("updates explained in plain words", "newer mods" in out, out.strip().splitlines()[-3:].__str__())

    # Tampered download: change the zip but not pack.json.
    shutil.copy2(LAB / "v0.1.1/pack.json", feed / "pack.json")
    with open(LAB / "v0.1.1/mods.zip", "ab") as f:
        f.write(b"tampered")
    (game / "bump-on-first-run.txt").unlink()
    (root / "pack-installed.json").unlink()
    for p in game.glob("run-*.txt"):
        p.unlink()
    r2 = run_launcher(root)
    check("tampered download is refused, game not started", r2.returncode != 0 and not list(game.glob("run-*.txt")),
          (r2.stdout.strip().splitlines() or [""])[-3:].__str__())

    check("real Stardew Mods folder untouched", snapshot(REAL_MODS) == real_before)
    print(f"\n{sum(results.values())}/{len(results)} checks passed")


if __name__ == "__main__":
    main()

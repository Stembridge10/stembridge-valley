#!/usr/bin/env python3
"""SMAPI auto-install test: a stand-in game folder with no SMAPI, the real official SMAPI download
(checked against its SHA-256), the real SMAPI installer run unattended. Never touches the real game.

Checks: SMAPI missing is noticed, the official installer runs, SMAPI files land in the test game folder,
a second run sees SMAPI and skips the install, a download with the wrong checksum is refused (nothing installed),
and the real Stardew folder is untouched.
"""
import hashlib, json, os, shutil, subprocess, sys
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
LAB = Path("/mnt/c/Users/Stembridge/hermes-work/StembridgeValley/smapi-test3")
LABW = r"C:\Users\Stembridge\hermes-work\StembridgeValley\smapi-test3"
LAUNCHER = REPO / "out/launcher/Play Junimo Hollow.exe"
REAL_GAME = Path("/mnt/c/Program Files (x86)/Steam/steamapps/common/Stardew Valley")
results = {}


def w(p: Path) -> str:
    return LABW + "\\" + str(p.relative_to(LAB)).replace("/", "\\")


def check(name, ok, detail=""):
    results[name] = bool(ok)
    print(("PASS " if ok else "FAIL ") + name + (" - " + detail if detail else ""), flush=True)


def smapi_files(d: Path):
    return sorted(p.name for p in d.iterdir() if p.name.startswith("StardewModdingAPI") or p.name == "smapi-internal")


def fingerprint(d: Path):
    h = hashlib.sha256()
    for p in sorted(d.iterdir()):
        if p.name in ("Mods", "ErrorLogs"):
            continue
        st = p.stat()
        h.update(f"{p.name}|{st.st_size}|{int(st.st_mtime)}".encode())
    return h.hexdigest()


def run(root: Path, env_extra=None):
    extra = dict(env_extra or {}, SV_NO_ELEVATE="1")  # tests never ask Windows for admin
    env = dict(os.environ, SV_LAUNCHER_ROOT=w(root), **extra)
    env["WSLENV"] = ":".join(["SV_LAUNCHER_ROOT", *extra.keys()])
    return subprocess.run([str(LAUNCHER), "--headless", "--setup-only", "sv:127.0.0.1:7777/test-only"],
                          env=env, input="", capture_output=True, text=True, timeout=240)


def stand_in_game(name: str) -> Path:
    game = LAB / name
    game.mkdir(parents=True)
    # Just enough for SMAPI's installer to accept the folder as a game install.
    for f in ["Stardew Valley.dll", "Stardew Valley.exe", "Stardew Valley.deps.json", "Stardew Valley.runtimeconfig.json",
              "MonoGame.Framework.dll"]:
        if (REAL_GAME / f).exists():
            shutil.copy2(REAL_GAME / f, game / f)
    return game


def main():
    shutil.rmtree(LAB, ignore_errors=True)
    LAB.mkdir(parents=True)
    real_before = fingerprint(REAL_GAME)

    # 1. Fresh install
    game = stand_in_game("game")
    root = LAB / "player"
    root.mkdir()
    (root / "launcher.json").write_text(json.dumps({"GamePath": w(game)}))
    check("stand-in game has no SMAPI", smapi_files(game) == [], str(smapi_files(game)))
    r = run(root)
    out = (r.stdout + r.stderr).strip().splitlines()
    print("\n".join("    " + l for l in out[-12:]))
    check("launcher exits cleanly", r.returncode == 0, f"code {r.returncode}")
    check("noticed SMAPI was missing", any("SMAPI not installed" in l for l in out))
    check("SMAPI installed into the game folder", {"StardewModdingAPI.exe", "StardewModdingAPI.dll", "smapi-internal"} <= set(smapi_files(game)),
          str(smapi_files(game)))
    check("reports the installed version", any("SMAPI 4.5.2 is installed" in l for l in out))
    check("installer files cleaned up", not (root / "smapi").exists())

    # 2. Second run: already installed, no download
    r = run(root)
    out = (r.stdout + r.stderr).strip().splitlines()
    check("second run skips the install", r.returncode == 0 and any("SMAPI 4.5.2 found" in l for l in out) and not any("Downloading" in l for l in out),
          "; ".join(out[-3:]))

    # 3. Wrong checksum: refused, nothing installed
    game2 = stand_in_game("game2")
    root2 = LAB / "player2"
    root2.mkdir()
    (root2 / "launcher.json").write_text(json.dumps({"GamePath": w(game2)}))
    r = run(root2, {"SV_SMAPI_URL": "https://github.com/Pathoschild/SMAPI/releases/download/4.5.2/SMAPI-4.5.2-installer.zip",
                    "SV_SMAPI_SHA256": "0" * 64})
    out = (r.stdout + r.stderr).strip().splitlines()
    check("tampered SMAPI download refused", r.returncode != 0 and any("safety check" in l for l in out) and smapi_files(game2) == [],
          "; ".join(out[-2:]))

    check("real Stardew folder untouched", fingerprint(REAL_GAME) == real_before)
    print(f"\n{sum(results.values())}/{len(results)} checks passed")
    sys.exit(0 if all(results.values()) else 1)


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""
Build the STS2_MCP mod and collect the installable files under build/.

Produces:
    build/STS2_MCP.dll    compiled mod (against the game's own DLLs)
    build/STS2_MCP.json   mod manifest (copy of mod_manifest.json)
    build/STS2_MCP.conf   server config with the chosen port

Install by copying all three into <game>/mods/.

Usage:
    python build.py                       # default port 15152 (1+STS2), auto-detect Steam
    python build.py --port 15526
    python build.py --game-dir "D:/SteamLibrary/steamapps/common/Slay the Spire 2"

Game dir resolution: --game-dir, then STS2_GAME_DIR env var, then common
Steam locations. No package dependencies; nothing is installed globally —
outputs land only in build/ (and the usual bin/ + obj/).
"""

import argparse
import json
import os
import platform
import shutil
import subprocess
import sys

REPO = os.path.dirname(os.path.abspath(__file__))
BUILD_DIR = os.path.join(REPO, "build")
GAME_SUBPATH = os.path.join("steamapps", "common", "Slay the Spire 2")


def candidate_game_dirs():
    home = os.path.expanduser("~")
    system = platform.system()
    if system == "Windows":
        for steam in (r"C:\Program Files\Steam", r"C:\Program Files (x86)\Steam",
                      r"D:\SteamLibrary"):
            yield os.path.join(steam, GAME_SUBPATH)
    elif system == "Darwin":
        yield os.path.join(home, "Library", "Application Support", "Steam", GAME_SUBPATH)
    else:  # Linux (incl. WSL: also probe the Windows mount)
        for steam in (os.path.join(home, ".steam", "steam"),
                      os.path.join(home, ".local", "share", "Steam")):
            yield os.path.join(steam, GAME_SUBPATH)
        for steam in ("/mnt/c/Program Files/Steam", "/mnt/c/Program Files (x86)/Steam"):
            yield os.path.join(steam, GAME_SUBPATH)


def find_game_dir(explicit):
    explicit = explicit or os.environ.get("STS2_GAME_DIR")
    if explicit:
        # Accept Windows-style paths under WSL: C:\... → /mnt/c/...
        if platform.system() != "Windows" and len(explicit) >= 2 and explicit[1] == ":":
            explicit = "/mnt/" + explicit[0].lower() + explicit[2:].replace("\\", "/")
        if os.path.isdir(explicit):
            return explicit
        sys.exit(f"game dir not found: {explicit}")
    for d in candidate_game_dirs():
        if os.path.isdir(d):
            return d
    sys.exit("game dir not found — pass --game-dir or set STS2_GAME_DIR")


def main():
    parser = argparse.ArgumentParser(description="Build STS2_MCP into build/")
    parser.add_argument("--port", type=int, default=15152,
                        help="localhost port for the mod's HTTP server (default: 15152; mod falls back to 15526 without a conf)")
    parser.add_argument("--game-dir", help="Slay the Spire 2 install directory")
    parser.add_argument("--configuration", default="Release", choices=["Debug", "Release"])
    args = parser.parse_args()

    if not (0 < args.port <= 65535):
        sys.exit(f"invalid port: {args.port}")

    game_dir = find_game_dir(args.game_dir)
    print(f"Game directory: {game_dir}")

    # Locate the data dir by inspection rather than the csproj's OS guess —
    # a WSL build against a Windows install needs data_sts2_windows_x86_64
    # even though the build OS is Linux (the game DLLs are platform-agnostic IL).
    data_parent = game_dir
    data_dirs = [d for d in os.listdir(data_parent) if d.startswith("data_sts2_")]
    if not data_dirs:  # macOS nests it inside the app bundle
        data_parent = os.path.join(game_dir, "SlayTheSpire2.app", "Contents", "Resources")
        if os.path.isdir(data_parent):
            data_dirs = [d for d in os.listdir(data_parent) if d.startswith("data_sts2_")]
    if len(data_dirs) != 1:
        sys.exit(f"expected one data_sts2_* dir under {game_dir}, found: {data_dirs}")
    data_dir = os.path.join(data_parent, data_dirs[0])

    print(f"Building ({args.configuration})...")
    r = subprocess.run([
        "dotnet", "build", os.path.join(REPO, "STS2_MCP.csproj"),
        "-c", args.configuration,
        f"-p:STS2GameDir={game_dir}",
        f"-p:STS2GameDataDir={data_dir}",
        "--nologo", "-v", "q",
    ])
    if r.returncode != 0:
        sys.exit("build failed")

    dll = os.path.join(REPO, "bin", args.configuration, "net9.0", "STS2_MCP.dll")
    if not os.path.isfile(dll):
        sys.exit(f"built dll not found: {dll}")

    os.makedirs(BUILD_DIR, exist_ok=True)
    shutil.copy2(dll, os.path.join(BUILD_DIR, "STS2_MCP.dll"))
    shutil.copy2(os.path.join(REPO, "mod_manifest.json"),
                 os.path.join(BUILD_DIR, "STS2_MCP.json"))
    with open(os.path.join(BUILD_DIR, "STS2_MCP.conf"), "w",
              encoding="utf-8", newline="\n") as f:
        json.dump({"port": args.port}, f, indent=2)
        f.write("\n")

    print(f"\nbuild/ ready (port {args.port}):")
    for name in sorted(os.listdir(BUILD_DIR)):
        print(f"  {name}")
    print(f"\nInstall: copy the contents of build/ into\n  {os.path.join(game_dir, 'mods')}")


if __name__ == "__main__":
    main()

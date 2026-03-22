"""Build, package, and optionally release Timberbot.

Usage:
    python release.py            build + package ZIP
    python release.py --release  build + package + tag + GitHub release
"""
import json
import os
import shutil
import subprocess
import sys
import zipfile

ROOT = os.path.dirname(os.path.abspath(__file__))
MOD_DIR = os.path.join(ROOT, "timberbot")
CLI_DIR = os.path.join(ROOT, "timberbot_cli")
DIST_DIR = os.path.join(ROOT, "dist")
MANIFEST = os.path.join(MOD_DIR, "manifest.json")
DLL_PATH = os.path.join(MOD_DIR, "bin", "Release", "netstandard2.1", "Timberbot.dll")


def run(cmd, **kwargs):
    print(f"  > {cmd}")
    subprocess.check_call(cmd, shell=True, **kwargs)


def main():
    release = "--release" in sys.argv

    with open(MANIFEST) as f:
        version = json.load(f)["Version"]

    print(f"building timberbot v{version}")

    # build
    run("dotnet build -c Release", cwd=MOD_DIR)

    # package
    if os.path.exists(DIST_DIR):
        shutil.rmtree(DIST_DIR)
    os.makedirs(DIST_DIR)

    zip_name = f"Timberbot-v{version}.zip"
    zip_path = os.path.join(DIST_DIR, zip_name)

    # mod zip (DLL + manifest + thumbnail)
    with zipfile.ZipFile(zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        zf.write(DLL_PATH, "Timberbot.dll")
        zf.write(MANIFEST, "manifest.json")
        thumb = os.path.join(MOD_DIR, "thumbnail.png")
        if os.path.exists(thumb):
            zf.write(thumb, "thumbnail.png")

    # cli zip (Python client)
    cli_zip_name = f"timberbot-cli-v{version}.zip"
    cli_zip_path = os.path.join(DIST_DIR, cli_zip_name)
    with zipfile.ZipFile(cli_zip_path, "w", zipfile.ZIP_DEFLATED) as zf:
        for root, dirs, files in os.walk(CLI_DIR):
            dirs[:] = [d for d in dirs if d not in ("__pycache__", ".egg-info")]
            for f in files:
                full = os.path.join(root, f)
                arc = os.path.join("timberbot_cli", os.path.relpath(full, CLI_DIR))
                zf.write(full, arc)

    print(f"packaged: dist/{zip_name}")
    print(f"packaged: dist/{cli_zip_name}")

    # release
    if release:
        tag = f"v{version}"
        run(f"git tag {tag}")
        run(f"git push origin {tag}")
        run(
            f'gh release create {tag} "{zip_path}" "{cli_zip_path}"'
            f" --repo abix-/TimberbornMods"
            f' --title "Timberbot {tag}"'
            f' --notes "HTTP API for AI agents to read and control Timberborn."'
        )
        print(f"released: {tag}")


if __name__ == "__main__":
    main()

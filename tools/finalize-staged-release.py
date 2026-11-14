#!/usr/bin/env python3
"""Reassemble verified release chunks on the GitHub runner, then publish atomically."""
import argparse
import hashlib
import importlib.util
import json
import os
import pathlib
import re
import subprocess
import sys

import requests


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("manifest_asset", type=int)
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    repo = os.environ["GITHUB_REPOSITORY"]
    session = requests.Session()
    session.headers.update({"Authorization": "Bearer " + os.environ["GITHUB_TOKEN"], "User-Agent": "orcksd-release"})
    api = "https://api.github.com/repos/" + repo

    def download(asset_id):
        result = session.get(api + "/releases/assets/" + str(int(asset_id)), headers={"Accept": "application/octet-stream"}, timeout=180, stream=True)
        result.raise_for_status()
        return result

    with download(args.manifest_asset) as response:
        manifest = response.json()
    version = manifest["version"]
    if manifest["repository"].lower() != repo.lower() or manifest["commit"] != os.environ["GITHUB_SHA"]:
        raise RuntimeError("Staging repository/commit does not match the dispatched source")
    if not manifest["notes"].startswith("docs/release/") or ".." in pathlib.PurePosixPath(manifest["notes"]).parts or not manifest["notes"].endswith(".md"):
        raise RuntimeError("Unsafe release notes path")
    if not re.fullmatch(r"\d+\.\d+\.\d+(?:-[A-Za-z0-9.]+)?", version):
        raise RuntimeError("Invalid release version")
    for asset in manifest["assets"]:
        relative = pathlib.PurePosixPath(asset["path"])
        if relative.is_absolute() or ".." in relative.parts or "\\" in asset["path"] or not asset["path"].startswith(("dist/releases/" + version + "/", "dist/updaters/", "docs/release/")):
            raise RuntimeError("Unsafe staging output path")
        target = root.joinpath(*relative.parts)
        target.parent.mkdir(parents=True, exist_ok=True)
        whole = hashlib.sha256()
        with target.open("wb") as stream:
            for chunk in asset["chunks"]:
                digest = hashlib.sha256(); size = 0
                with download(chunk["id"]) as response:
                    for data in response.iter_content(1024 * 1024):
                        stream.write(data); digest.update(data); whole.update(data); size += len(data)
                if digest.hexdigest() != chunk["sha256"] or size != chunk["size"]:
                    raise RuntimeError("Staging chunk verification failed: " + asset["path"])
        if whole.hexdigest() != asset["sha256"] or target.stat().st_size != asset["size"]:
            raise RuntimeError("Reassembled asset verification failed: " + asset["path"])
        print("REASSEMBLED_VERIFIED", target.name, asset["size"], flush=True)
    (root / "dist/releases" / version / "verification").mkdir(parents=True, exist_ok=True)
    if manifest.get("mode") == "seeded":
        paths = {asset["path"] for asset in manifest["assets"]}
        if manifest["seedPatch"] not in paths or manifest["apkDelta"] not in paths:
            raise RuntimeError("Seed files were not in the verified transport")
        plan_file = root / "dist/releases" / version / "verification/cloud-plan.json"
        plan_file.write_text(json.dumps(manifest), encoding="utf-8")
        subprocess.run([sys.executable, str(root / "tools/materialize-seeded-release.py"), str(plan_file)], check=True)
    # A draft's notes may be edited during verification; binaries remain immutable.
    subprocess.run([sys.executable, str(root / "tools/upload-release.py"), "--tag", "v" + version, "--force", manifest["notes"]], check=True)
    subprocess.run([sys.executable, str(root / "tools/publish-release.py"), version, "--notes", manifest["notes"]], check=True)
    # The staging release is task-owned and never public; remove its temporary chunks.
    result = session.delete(api + "/releases/" + str(int(manifest["staging_release"])), timeout=60)
    result.raise_for_status()
    print("STAGING_REMOVED_AND_RELEASE_PUBLISHED", version, flush=True)


if __name__ == "__main__":
    main()

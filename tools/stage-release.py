#!/usr/bin/env python3
"""Stage bounded chunks in a private draft and dispatch GitHub to publish exact verified bytes."""
import argparse
import concurrent.futures
import hashlib
import importlib.util
import json
import os
import pathlib
import subprocess
import sys
import threading
import time
import urllib.parse
import zipfile

import requests


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("version")
    parser.add_argument("--notes", required=True)
    parser.add_argument("--chunk-mb", type=int, default=8)
    parser.add_argument("--workers", type=int, default=6)
    parser.add_argument("--seed-from")
    parser.add_argument("--baselines")
    parser.add_argument("--apk-delta")
    parser.add_argument("--patch-delta", nargs=2, action="append", default=[])
    args = parser.parse_args()
    if not 1 <= args.chunk_mb <= 16 or not 1 <= args.workers <= 8:
        raise RuntimeError("Use 1-16 MiB chunks and 1-8 workers")
    root = pathlib.Path(__file__).resolve().parents[1]
    spec = importlib.util.spec_from_file_location("u", root / "tools/upload-release.py")
    upload = importlib.util.module_from_spec(spec); spec.loader.exec_module(upload)
    token = upload.token()
    session = requests.Session(); session.headers.update({"Authorization": "Bearer " + token, "User-Agent": "orcksd-release"})
    repo = upload.REPO_DEFAULT; api = "https://api.github.com/repos/" + repo
    tag = "staging-v" + args.version
    release = upload.find_release(session, repo, tag)
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, encoding="utf-8").strip()
    if release is None:
        result = session.post(api + "/releases", json={"tag_name": tag, "target_commitish": commit, "name": "Private transport staging " + args.version, "draft": True, "prerelease": True}, timeout=60)
        result.raise_for_status(); release = result.json()
    if not release["draft"]:
        raise RuntimeError("Transport staging must stay draft-only")
    existing = {}
    for page in range(1, 20):
        result = session.get(api + f"/releases/{release['id']}/assets", params={"per_page": 100, "page": page, "_": time.time_ns()}, timeout=60)
        result.raise_for_status(); values = result.json(); existing.update({a["name"]: a for a in values})
        if len(values) < 100: break
    delivery = root / "dist/releases" / args.version
    files = [delivery / "full" / f"OrC-KSD-{args.version}-win-x64.zip", delivery / "android" / f"OrC-KSD-{args.version}-android.apk"]
    files += sorted((delivery / "patches").glob("OrC-*.orcupdate"))
    files += [root / "dist/updaters/1.2.0/OrC-KSD-offline-updater-1.2.0-win-x64.zip", pathlib.Path(args.notes).resolve()]
    manifest = {"repository": repo, "version": args.version, "commit": commit, "staging_release": release["id"], "notes": pathlib.Path(args.notes).resolve().relative_to(root).as_posix(), "assets": []}
    if args.seed_from:
        if not args.apk_delta: raise RuntimeError("Seeded publication requires a verified APK transport delta")
        def public_asset(path, version):
            with path.open("rb") as stream: digest = hashlib.file_digest(stream, "sha256").hexdigest()
            return {"version": version, "url": f"https://github.com/{repo}/releases/download/v{version}/{path.name}", "size": path.stat().st_size, "sha256": digest}
        base_zip = root / f"dist/releases/{args.seed_from}/full/OrC-KSD-{args.seed_from}-win-x64.zip"
        with zipfile.ZipFile(base_zip) as archive: base_build = json.loads(archive.read(".orcksd-build.json"))
        seed = next((delivery / "patches").glob("*-from-" + base_build["BuildId"][:12] + "-*.orcupdate"))
        with zipfile.ZipFile(seed) as archive: plan = json.loads(archive.read("patch.json"))
        apk_delta = pathlib.Path(os.path.abspath(args.apk_delta))
        previous = upload.find_release(session, repo, "v" + args.seed_from)
        updater = next(a for a in previous["assets"] if a["name"] == "OrC-KSD-offline-updater-1.2.0-win-x64.zip")
        if not updater.get("digest", "").startswith("sha256:"): raise RuntimeError("Updater has no authoritative digest")
        manifest.update({"mode": "seeded", "seedVersion": args.seed_from, "targetBuild": plan["Target"]["BuildId"], "seedPatch": seed.relative_to(root).as_posix(),
            "apkDelta": apk_delta.relative_to(root).as_posix(),
            "apkBase": public_asset(root / f"dist/releases/{args.seed_from}/android/OrC-KSD-{args.seed_from}-android.apk", args.seed_from),
            "apkTarget": public_asset(delivery / f"android/OrC-KSD-{args.version}-android.apk", args.version),
            "baselines": [public_asset(root / f"dist/releases/{v}/full/OrC-KSD-{v}-win-x64.zip", v) for v in (args.baselines or args.seed_from).split(',')],
            "updater": {"url": updater["browser_download_url"], "size": updater["size"], "sha256": updater["digest"][7:], "version": "1.2.0"}})
        files = [seed, apk_delta]
        if args.patch_delta:
            manifest["patchTransports"] = []
            for delta_path, output_path in args.patch_delta:
                delta = pathlib.Path(os.path.abspath(delta_path)); output = pathlib.Path(os.path.abspath(output_path))
                with output.open("rb") as stream: digest = hashlib.file_digest(stream, "sha256").hexdigest()
                with zipfile.ZipFile(output) as archive: patch_plan = json.loads(archive.read("patch.json"))
                manifest["patchTransports"].append({"delta": delta.relative_to(root).as_posix(), "output": output.relative_to(root).as_posix(), "sha256": digest, "sourceBuild": patch_plan["Source"]["BuildId"]})
                files.append(delta)
    tasks = []; block = args.chunk_mb * 1024 * 1024
    for index, path in enumerate(files):
        with path.open("rb") as stream: digest = hashlib.file_digest(stream, "sha256").hexdigest()
        asset = {"path": path.relative_to(root).as_posix(), "size": path.stat().st_size, "sha256": digest, "chunks": []}
        manifest["assets"].append(asset)
        for offset in range(0, asset["size"], block):
            tasks.append((asset, path, offset, f"part-{index:02}-{digest[:12]}-{offset // block:04}.bin"))
    lock = threading.Lock(); completed = 0

    def stage(task):
        nonlocal completed
        asset, path, offset, name = task
        with path.open("rb") as stream: stream.seek(offset); data = stream.read(block)
        digest = hashlib.sha256(data).hexdigest()
        remote = existing.get(name)
        if remote is None or remote.get("digest") != "sha256:" + digest or remote["size"] != len(data):
            remote = None
            for attempt in range(4):
                try:
                    result = requests.post(f"https://uploads.github.com/repos/{repo}/releases/{release['id']}/assets?name=" + urllib.parse.quote(name), data=data,
                        headers={"Authorization": "Bearer " + token, "User-Agent": "orcksd-release", "Content-Type": "application/octet-stream"}, timeout=(30, 90))
                    result.raise_for_status(); remote = result.json(); break
                except requests.RequestException:
                    if attempt == 3: raise
                    time.sleep(3 * (attempt + 1))
            if remote.get("digest") != "sha256:" + digest or remote["size"] != len(data):
                raise RuntimeError("Remote chunk differs: " + name)
        value = {"id": remote["id"], "offset": offset, "size": len(data), "sha256": digest}
        with lock:
            asset["chunks"].append(value); completed += 1
            print(f"CHUNK_VERIFIED {completed}/{len(tasks)} {name}", flush=True)

    with concurrent.futures.ThreadPoolExecutor(max_workers=args.workers) as pool:
        list(pool.map(stage, tasks))
    for asset in manifest["assets"]: asset["chunks"].sort(key=lambda item: item["offset"])
    encoded = json.dumps(manifest, ensure_ascii=False, indent=2).encode("utf-8")
    result = session.post(f"https://uploads.github.com/repos/{repo}/releases/{release['id']}/assets?name=transport-manifest-{commit[:12]}.json", data=encoded, headers={"Content-Type": "application/json"}, timeout=60)
    result.raise_for_status(); manifest_asset = result.json()["id"]
    output = delivery / "verification/staging-transport.json"; output.parent.mkdir(parents=True, exist_ok=True)
    output.write_bytes(encoded)
    result = session.post(api + "/actions/workflows/finalize-staged-release.yml/dispatches", json={"ref": "main", "inputs": {"manifest_asset": str(manifest_asset)}}, timeout=60)
    result.raise_for_status()
    print("CLOUD_FINALIZATION_DISPATCHED", manifest_asset, commit, flush=True)


if __name__ == "__main__":
    main()

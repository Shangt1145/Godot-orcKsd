#!/usr/bin/env python3
"""Upload and verify every release asset before publishing and advancing the update channel."""
import argparse
import hashlib
import importlib.util
import json
import pathlib
import subprocess
import sys

import requests


def published_manifest(release, names):
    if release["draft"]:
        raise RuntimeError("A draft release cannot become the public update channel")
    by_name = {asset["name"]: asset for asset in release["assets"]}
    assets = []
    for name in names:
        asset = by_name[name]
        if "/releases/download/" + release["tag_name"] + "/" not in asset["browser_download_url"]:
            raise RuntimeError("Release still exposes a temporary draft URL: " + name)
        assets.append({"name": name, "size": asset["size"], "url": asset["browser_download_url"]})
    return {"tag": release["tag_name"], "notes": release["body"], "assets": assets}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("version")
    parser.add_argument("--notes", required=True)
    parser.add_argument("--release-id", type=int)
    args = parser.parse_args()
    root = pathlib.Path(__file__).resolve().parents[1]
    delivery = root / "dist/releases" / args.version
    notes = pathlib.Path(args.notes).resolve()
    files = [delivery / "full" / f"OrC-KSD-{args.version}-win-x64.zip",
             delivery / "android" / f"OrC-KSD-{args.version}-android.apk"]
    files += sorted((delivery / "patches").glob("*.orcupdate"))
    files += [root / "dist/updaters/1.2.0/OrC-KSD-offline-updater-1.2.0-win-x64.zip", notes]
    if not any(p.suffix == ".orcupdate" for p in files):
        raise RuntimeError("No update patches; refusing an incomplete publication")
    hashes = {}
    for path in files:
        with path.open("rb") as stream:
            hashes[path.name] = hashlib.file_digest(stream, "sha256").hexdigest()
    checksum = delivery / "SHA256.txt"
    checksum.write_text("".join(hashes[p.name].upper() + "  " + p.name + "\n" for p in files), encoding="utf-8")
    files.append(checksum)
    spec = importlib.util.spec_from_file_location("upload", root / "tools/upload-release.py")
    upload = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(upload)
    session = requests.Session()
    session.headers.update({"Authorization": "Bearer " + upload.token(), "User-Agent": "orcksd-release", "Accept": "application/vnd.github+json"})
    api = "https://api.github.com/repos/" + upload.REPO_DEFAULT
    tag = "v" + args.version

    def request(method, path, **kwargs):
        result = session.request(method, api + path, timeout=60, **kwargs)
        result.raise_for_status()
        return result.json()

    release = upload.resolve_release(session, upload.REPO_DEFAULT, tag, args.release_id)
    if release is None:
        commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, encoding="utf-8").strip()
        release = request("POST", "/releases", json={"tag_name": tag, "target_commitish": commit, "name": "OrC-KSD " + args.version,
            "body": notes.read_text(encoding="utf-8"), "draft": True, "prerelease": "-" in args.version})
    subprocess.run([sys.executable, str(root / "tools/upload-release.py"), "--tag", tag, "--release-id", str(release["id"]), *map(str, files)], cwd=root, check=True)
    release = request("GET", "/releases/" + str(release["id"]))
    assets = []
    for path in files:
        asset = next(a for a in release["assets"] if a["name"] == path.name)
        if path.name not in hashes:
            with path.open("rb") as stream:
                hashes[path.name] = hashlib.file_digest(stream, "sha256").hexdigest()
        if asset["state"] != "uploaded" or asset["size"] != path.stat().st_size or asset.get("digest", "").lower() != "sha256:" + hashes[path.name]:
            raise RuntimeError("Remote asset verification failed: " + path.name)
        assets.append({"name": path.name, "size": asset["size"], "url": asset["browser_download_url"]})
        print("REMOTE_VERIFIED", path.name, flush=True)
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, encoding="utf-8").strip()
    release = request("PATCH", "/releases/" + str(release["id"]), json={"draft": False, "body": notes.read_text(encoding="utf-8"), "prerelease": "-" in args.version, "target_commitish": commit})
    manifest = published_manifest(release, [path.name for path in files])
    channel = root / "dist/releases/latest.json"
    channel.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    subprocess.run([sys.executable, str(root / "tools/upload-release.py"), "--tag", "channel", "--force", str(channel)], cwd=root, check=True)
    (delivery / "verification/publish-report.json").write_text(json.dumps({"url": release["html_url"], "assets": manifest["assets"], "sha256": hashes}, ensure_ascii=False, indent=2), encoding="utf-8")
    print("PUBLISHED_AND_CHANNEL_UPDATED", release["html_url"], flush=True)


if __name__ == "__main__":
    main()

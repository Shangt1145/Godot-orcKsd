#!/usr/bin/env python3
"""热修复发布：只换补丁，不动版本号，最后推进更新清单。

和 `publish-release.py` 的区别（这是 VERSIONING §5 那条"同版本只维护一条补丁"的落地）：

* **不传完整包、不传 APK**：热修复沿用已发布的完整包，只把补丁（和发行说明）换掉；
* **允许覆盖同版本资产**：`--force` 先删后传，所以同一个 release 的补丁会被新构建替换；
* **SHA256.txt 合并而不是重写**：旧的完整包 / APK 还在这个 release 上给玩家下，
  它们的校验和必须留着，否则玩家下完整包会校验失败；
* 最后把 `channel` 发行里的 `latest.json` 重写成"这个 release 现在的资产清单"——
  客户端读的是它，不推进它等于没发布。

用法：

    python tools/publish-hotfix.py 2.0.0-rc1-hotfix --notes docs/release/RELEASE_...md
"""
import argparse
import hashlib
import importlib.util
import json
import pathlib
import re
import subprocess
import sys

import requests

TIMEOUT = (180, 3600)
CHECKSUM = "SHA256.txt"


def load_upload():
    root = pathlib.Path(__file__).resolve().parents[1]
    spec = importlib.util.spec_from_file_location("upload", root / "tools/upload-release.py")
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return root, module


def sha256_of(path: pathlib.Path) -> str:
    with path.open("rb") as stream:
        return hashlib.file_digest(stream, "sha256").hexdigest().upper()


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    parser.add_argument("version")
    parser.add_argument("--notes", required=True)
    parser.add_argument("--dry-run", action="store_true")
    args = parser.parse_args()

    root, upload = load_upload()
    delivery = root / "dist" / "releases" / args.version
    patches = sorted((delivery / "patches").glob("*.orcupdate"))
    if not patches:
        raise SystemExit(f"没有补丁可发：{delivery / 'patches'}")
    notes = pathlib.Path(args.notes).resolve()
    if not notes.exists():
        raise SystemExit(f"找不到发行说明：{notes}")

    tag = "v" + args.version
    session = requests.Session()
    session.headers.update({
        "Authorization": "Bearer " + upload.token(),
        "Accept": "application/vnd.github+json",
        "User-Agent": "orcksd-release",
    })
    release = upload.find_release(session, upload.REPO_DEFAULT, tag)
    if release is None:
        raise SystemExit(f"找不到 release {tag}；热修复不新建发行，请先有完整包那一版")
    print(f"RELEASE {tag} id={release['id']} 现有资产 {len(release['assets'])} 个")

    # 1) 合并校验和：保留没被替换的资产条目（完整包 / APK / 更新器），换掉本次上传的。
    existing: dict[str, str] = {}
    checksum_asset = next((a for a in release["assets"] if a["name"] == CHECKSUM), None)
    if checksum_asset:
        text = session.get(checksum_asset["browser_download_url"], timeout=TIMEOUT).text
        for line in text.splitlines():
            match = re.match(r"^([A-Fa-f0-9]{64})\s+\*?(.+)$", line.strip())
            if match:
                existing[match.group(2)] = match.group(1).upper()
        print(f"沿用旧清单条目 {len(existing)} 条")

    uploading = patches + [notes]
    for path in uploading:
        existing[path.name] = sha256_of(path)
    checksum_path = delivery / CHECKSUM
    checksum_path.parent.mkdir(parents=True, exist_ok=True)
    checksum_path.write_text(
        "".join(hash + "  " + name + "\n" for name, hash in sorted(existing.items())),
        encoding="utf-8",
    )
    print(f"写 {CHECKSUM}：{len(existing)} 条（本次替换 {len(uploading)} 条）")

    files = uploading + [checksum_path]
    if args.dry_run:
        for path in files:
            print(f"DRY_RUN 会上传 {path.name}  {path.stat().st_size/1048576:.2f} MB")
        return 0

    # 2) 上传（--force：同名先删后传，这是"重新生成并覆盖同一条补丁"的唯一实现方式）。
    subprocess.run(
        [sys.executable, str(root / "tools/upload-release.py"), "--tag", tag,
         "--release-id", str(release["id"]), "--force", *map(str, files)],
        cwd=root, check=True,
    )

    # 3) 远端核对：大小与 digest 都对上才算传成功。
    release = session.get("https://api.github.com/repos/" + upload.REPO_DEFAULT + "/releases/" + str(release["id"]), timeout=TIMEOUT).json()
    for path in files:
        asset = next(a for a in release["assets"] if a["name"] == path.name)
        expected = existing[path.name] if path.name in existing else sha256_of(path)
        if asset["state"] != "uploaded" or asset["size"] != path.stat().st_size or asset.get("digest", "").lower() != "sha256:" + expected.lower():
            raise SystemExit("远端资产校验失败：" + path.name)
        print("REMOTE_VERIFIED", path.name)

    # 4) 推进更新清单：客户端只读 channel/latest.json，不推它等于没发布。
    assets = [{"name": a["name"], "size": a["size"], "url": a["browser_download_url"]} for a in release["assets"]]
    manifest = {"tag": release["tag_name"], "notes": notes.read_text(encoding="utf-8"), "assets": assets}
    channel_dir = root / "dist" / "releases"
    channel_dir.mkdir(parents=True, exist_ok=True)
    channel = channel_dir / "latest.json"
    channel.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    subprocess.run(
        [sys.executable, str(root / "tools/upload-release.py"), "--tag", "channel", "--force", str(channel)],
        cwd=root, check=True,
    )
    (delivery / "verification").mkdir(parents=True, exist_ok=True)
    (delivery / "verification" / "publish-report.json").write_text(
        json.dumps({"url": release["html_url"], "assets": assets, "patched": [p.name for p in patches]},
                   ensure_ascii=False, indent=2),
        encoding="utf-8",
    )
    print("HOTFIX_PUBLISHED_AND_CHANNEL_UPDATED", release["html_url"])
    return 0


if __name__ == "__main__":
    sys.exit(main())

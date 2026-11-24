#!/usr/bin/env python3
"""发一个「只换二进制、不重打 APK」的版本：补丁 + 完整包 + 更新器 + 说明 + 校验和。

为什么需要它：`publish-release.py` 硬性要求 `full/*.zip` 与 `android/*.apk` 同时存在，
而热修复（VERSIONING §5）本来就不出全量包；只修 Windows 的版本也没有新 APK。
这个脚本把同一套「上传 → 远端核对 → 推进 channel 清单」的流程，按"实际有哪些产物"来做。

关键约束（都是踩过的）：

* **校验和只能从 API 的 digest 拿**：`browser_download_url` 走 CDN，代理偶发 502 时
  返回的是一页错误 HTML，按行解析会得到 0 条 —— 那样写出�� SHA256.txt 会漏掉完整包与 APK，
  玩家下这两样必校验失败（2026-11-24 真踩过）。
* **补丁必须覆盖同名资产**：同版本只维护一条补丁，`--force` 先删后传。
* **最后必须推进 `channel/latest.json`**：客户端读的是它，不推等于没发布。

用法：

    python tools/publish-patch.py 2.0.0-rc.2 --notes docs/release/RELEASE_2.0.0_RC2_20261124.md --create
    python tools/publish-patch.py 2.0.0-rc1-hotfix --notes docs/release/RELEASE_2.0.0_RC1_HOTFIX_20261124.md
"""
import argparse
import hashlib
import importlib.util
import json
import pathlib
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
    parser.add_argument("--create", action="store_true", help="发行不存在时新建草稿（新版本用）")
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

    # 有就传、没有就不传：完整包（新玩家）/ 更新器（版本进位时才变）/ APK（本次没有）。
    optional = [
        delivery / "full" / f"OrC-KSD-{args.version}-win-x64.zip",
        root / "dist" / "updaters" / "1.2.0" / "OrC-KSD-offline-updater-1.2.0-win-x64.zip",
        delivery / "android" / f"OrC-KSD-{args.version}-android.apk",
    ]
    extras = [path for path in optional if path.exists()]
    for path in optional:
        if not path.exists():
            print(f"跳过（没有产物）：{path.name}")

    tag = "v" + args.version
    session = requests.Session()
    session.headers.update({
        "Authorization": "Bearer " + upload.token(),
        "Accept": "application/vnd.github+json",
        "User-Agent": "orcksd-release",
    })
    release = upload.find_release(session, upload.REPO_DEFAULT, tag)
    if release is None:
        if not args.create:
            raise SystemExit(f"找不到 release {tag}；补丁重发不新建发行，新版本请加 --create")
        if args.dry_run:
            print(f"DRY_RUN 会新建草稿发行 {tag}")
            return 0
        commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, encoding="utf-8").strip()
        body = {"tag_name": tag, "target_commitish": commit, "name": "OrC-KSD " + args.version,
                "body": notes.read_text(encoding="utf-8"), "draft": True, "prerelease": "-" in args.version}
        release = session.post("https://api.github.com/repos/" + upload.REPO_DEFAULT + "/releases",
                               json=body, timeout=TIMEOUT).json()
        print(f"已建草稿发行 {tag} id={release['id']}")
    print(f"RELEASE {tag} id={release['id']} 现有资产 {len(release['assets'])} 个")

    # 1) 校验和：先收 API digest（未替换的资产），再用本地文件覆盖本次要传的。
    existing: dict[str, str] = {}
    for asset in release["assets"]:
        digest = (asset.get("digest") or "").lower()
        if asset["name"] == CHECKSUM or not digest.startswith("sha256:"):
            continue
        existing[asset["name"]] = digest[len("sha256:"):].upper()
    untouched = len(existing)
    unexplainable = len(release["assets"]) - untouched - (1 if any(a["name"] == CHECKSUM for a in release["assets"]) else 0)
    print(f"沿用旧清单条目 {untouched} 条（来自 API digest；无 digest 的资产 {unexplainable} 个）")

    uploading = patches + [notes] + extras
    for path in uploading:
        existing[path.name] = sha256_of(path)
    checksum_path = delivery / CHECKSUM
    checksum_path.parent.mkdir(parents=True, exist_ok=True)
    checksum_path.write_text(
        "".join(hash + "  " + name + "\n" for name, hash in sorted(existing.items())),
        encoding="utf-8",
    )
    print(f"写 {CHECKSUM}：{len(existing)} 条（本次上传 {len(uploading)} 条）")

    files = uploading + [checksum_path]
    totals = sum(p.stat().st_size for p in files)
    print(f"合计上传 {totals/1048576:.1f} MB / {len(files)} 个文件")
    if args.dry_run:
        for path in files:
            print(f"DRY_RUN {path.name}  {path.stat().st_size/1048576:.2f} MB")
        return 0

    # 2) 上传（--force：同名先删后传）。
    subprocess.run(
        [sys.executable, str(root / "tools/upload-release.py"), "--tag", tag,
         "--release-id", str(release["id"]), "--force", *map(str, files)],
        cwd=root, check=True,
    )

    # 3) 远端核对：大小与 digest 都对上才算传成功。
    release = session.get("https://api.github.com/repos/" + upload.REPO_DEFAULT + "/releases/" + str(release["id"]), timeout=TIMEOUT).json()
    for path in files:
        asset = next(a for a in release["assets"] if a["name"] == path.name)
        expected = existing.get(path.name) or sha256_of(path)
        if asset["state"] != "uploaded" or asset["size"] != path.stat().st_size or asset.get("digest", "").lower() != "sha256:" + expected.lower():
            raise SystemExit("远端资产校验失败：" + path.name)
        print("REMOTE_VERIFIED", path.name)

    # 4) 公开（新版本才会是草稿；已公开的这里是无操作）。
    commit = subprocess.check_output(["git", "rev-parse", "HEAD"], cwd=root, encoding="utf-8").strip()
    release = session.patch(
        "https://api.github.com/repos/" + upload.REPO_DEFAULT + "/releases/" + str(release["id"]),
        json={"draft": False, "body": notes.read_text(encoding="utf-8"),
              "prerelease": "-" in args.version, "target_commitish": commit},
        timeout=TIMEOUT,
    ).json()

    # 5) 推进更新清单：客户端只读 channel/latest.json。
    assets = [{"name": a["name"], "size": a["size"], "url": a["browser_download_url"]} for a in release["assets"]]
    manifest = {"tag": release["tag_name"], "notes": notes.read_text(encoding="utf-8"), "assets": assets}
    channel = root / "dist" / "releases" / "latest.json"
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
    print("PUBLISHED_AND_CHANNEL_UPDATED", release["html_url"])
    return 0


if __name__ == "__main__":
    sys.exit(main())

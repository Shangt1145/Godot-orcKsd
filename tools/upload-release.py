#!/usr/bin/env python3
"""把发行文件上传到 GitHub Release 资产。

用法：
    python tools/upload-release.py --tag v2.0.0-alpha.1 FILE [FILE ...]
    python tools/upload-release.py --tag v2.0.0-alpha.1 --force BIG.zip

特性：
  * 用 `git credential fill` 取 token，不需要 gh CLI
  * requests 流式上传，1 GB 文件不吃内存；自动走 HTTPS_PROXY
  * 同名资产已存在则跳过；--force 先删后传
  * 失败自动重试 3 次（代理偶发 502）
"""
import argparse
import json
import os
import subprocess
import sys
import time
import urllib.parse

import requests

REPO_DEFAULT = "Shangt1145/Godot-orcKsd"
TIMEOUT = (30, 3600)


def token() -> str:
    out = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True, text=True, check=True,
    ).stdout
    for line in out.splitlines():
        if line.startswith("password="):
            return line[len("password="):]
    raise SystemExit("取不到 GitHub 凭据（git credential fill 为空）；先确认能 git push。")


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--tag", required=True)
    ap.add_argument("--repo", default=REPO_DEFAULT)
    ap.add_argument("--force", action="store_true")
    ap.add_argument("files", nargs="+")
    args = ap.parse_args()

    session = requests.Session()
    session.headers.update({
        "Authorization": f"Bearer {token()}",
        "Accept": "application/vnd.github+json",
        "User-Agent": "orcksd-release",
    })

    release = session.get(
        f"https://api.github.com/repos/{args.repo}/releases/tags/{args.tag}", timeout=TIMEOUT
    )
    if release.status_code >= 400:
        raise SystemExit(f"找不到 release {args.tag}：{release.status_code} {release.text[:300]}")
    release = release.json()
    print(f"Release {args.tag}  id={release['id']}  已有资产 {len(release['assets'])} 个")

    for path in args.files:
        if not os.path.exists(path):
            raise SystemExit(f"找不到文件：{path}")
        name = os.path.basename(path)
        size = os.path.getsize(path)

        existing = next((a for a in release["assets"] if a["name"] == name), None)
        if existing:
            if not args.force:
                print(f"跳过（已存在）：{name}")
                continue
            session.delete(f"https://api.github.com/repos/{args.repo}/releases/assets/{existing['id']}",
                           timeout=TIMEOUT)
            print(f"已删除旧资产：{name}")

        url = (f"https://uploads.github.com/repos/{args.repo}/releases/{release['id']}/assets?name="
               + urllib.parse.quote(name))
        print(f"上传 {name}（{size/1048576:.1f} MB）…", flush=True)

        for attempt in range(1, 4):
            try:
                with open(path, "rb") as fh:
                    resp = session.post(
                        url, data=fh,
                        headers={"Content-Type": "application/octet-stream",
                                 "Content-Length": str(size)},
                        timeout=TIMEOUT,
                    )
                if resp.status_code < 400:
                    info = resp.json()
                    print(f"ASSET_OK {info['name']}  {info['size']/1048576:.1f} MB")
                    break
                print(f"  第 {attempt} 次失败：{resp.status_code} {resp.text[:200]}")
            except requests.RequestException as error:
                print(f"  第 {attempt} 次异常：{error}")
            if attempt == 3:
                raise SystemExit(f"上传失败：{name}")
            time.sleep(5 * attempt)
    return 0


if __name__ == "__main__":
    sys.exit(main())

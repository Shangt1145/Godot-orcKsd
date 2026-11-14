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
import ipaddress
import os
import shutil
import subprocess
import sys
import tempfile
import time
import urllib.parse

import requests

REPO_DEFAULT = "Shangt1145/Godot-orcKsd"
# requests also uses the connection timeout while writing a streamed request body.
# Large release assets can encounter long proxy backpressure pauses.
TIMEOUT = (180, 3600)


def token() -> str:
    if value := os.environ.get("GH_TOKEN") or os.environ.get("GITHUB_TOKEN"):
        return value
    out = subprocess.run(
        ["git", "credential", "fill"],
        input="protocol=https\nhost=github.com\n\n",
        capture_output=True, text=True, check=True,
    ).stdout
    for line in out.splitlines():
        if line.startswith("password="):
            return line[len("password="):]
    raise SystemExit("取不到 GitHub 凭据（git credential fill 为空）；先确认能 git push。")


def find_release(session, repo: str, tag: str):
    """Draft tags need not exist in Git yet; resolve drafts through the authenticated list."""
    response = session.get(f"https://api.github.com/repos/{repo}/releases/tags/{tag}", timeout=TIMEOUT)
    if response.status_code == 404:
        listing = session.get(f"https://api.github.com/repos/{repo}/releases", params={"per_page": 100, "_": time.time_ns()}, timeout=TIMEOUT)
        listing.raise_for_status()
        return next((release for release in listing.json() if release["tag_name"] == tag), None)
    response.raise_for_status()
    return response.json()


def upload_asset(session, url: str, path: str, size: int):
    curl = shutil.which("curl.exe") or shutil.which("curl")
    if curl:
        descriptor, response_path = tempfile.mkstemp(prefix="orcksd-upload-", suffix=".json")
        os.close(descriptor)
        try:
            # Pass credentials on stdin, never in process arguments or progress output.
            config = "header = " + json.dumps("Authorization: " + session.headers["Authorization"]) + "\n"
            command = [curl, "--config", "-", "--http1.1", "--connect-timeout", "30", "--max-time", "3600",
                "--speed-time", "180", "--speed-limit", "1024", "--fail-with-body", "--request", "POST",
                "--header", "Content-Type: application/octet-stream", "--data-binary", "@" + os.path.abspath(path), "--output", response_path, url]
            if address := os.environ.get("ORC_GITHUB_UPLOAD_IP"):
                ipaddress.ip_address(address)
                command[1:1] = ["--resolve", "uploads.github.com:443:" + address]
            if os.name == "nt": command.insert(1, "--ssl-revoke-best-effort")
            result = subprocess.run(command, input=config, text=True)
            with open(response_path, encoding="utf-8") as stream: body = stream.read()
            if result.returncode:
                raise requests.RequestException(f"curl exited {result.returncode}: {body[:200]}")
            return json.loads(body)
        finally:
            os.unlink(response_path)
    with open(path, "rb") as stream:
        response = session.post(url, data=stream, headers={"Content-Type": "application/octet-stream", "Content-Length": str(size)}, timeout=TIMEOUT)
    response.raise_for_status()
    return response.json()


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

    release = find_release(session, args.repo, args.tag)
    if release is None:
        raise SystemExit(f"找不到 release {args.tag}（包括当前凭据可访问的草稿）")
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
                info = upload_asset(session, url, path, size)
                print(f"ASSET_OK {info['name']}  {info['size']/1048576:.1f} MB")
                break
            except requests.RequestException as error:
                print(f"  第 {attempt} 次异常：{error}")
            if attempt == 3:
                raise SystemExit(f"上传失败：{name}")
            time.sleep(5 * attempt)
    return 0


if __name__ == "__main__":
    sys.exit(main())

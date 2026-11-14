#!/usr/bin/env python3
"""Recover exact tested program bytes from a public baseline and verified deltas."""
import hashlib
import json
import os
import pathlib
import shutil
import subprocess
import sys
import tempfile
import zipfile

import requests


def digest_file(path):
    with path.open("rb") as stream: return hashlib.file_digest(stream, "sha256").hexdigest()


def main():
    plan = json.loads(pathlib.Path(sys.argv[1]).read_text(encoding="utf-8"))
    root = pathlib.Path(__file__).resolve().parents[1]
    version = plan["version"]; delivery = root / "dist/releases" / version
    work = pathlib.Path(tempfile.mkdtemp(prefix="orcksd-materialize-"))
    cli_project = root / "tools/OfflineUpdate/OrC.Update.Cli/OrC.Update.Cli.csproj"
    subprocess.run(["dotnet", "build", str(cli_project), "--nologo", "-v:q"], check=True)
    cli = cli_project.parent / "bin/Debug/net9.0/OrC.Update.Cli.dll"
    def command(*args): subprocess.run(["dotnet", str(cli), *map(str, args)], check=True)
    def fetch(meta, name):
        path = work / name; digest = hashlib.sha256()
        if not meta["url"].startswith("https://github.com/" + plan["repository"] + "/releases/download/"): raise RuntimeError("Untrusted baseline URL")
        with requests.get(meta["url"], stream=True, timeout=300) as response:
            response.raise_for_status()
            with path.open("wb") as stream:
                for data in response.iter_content(1024 * 1024): stream.write(data); digest.update(data)
        if path.stat().st_size != meta["size"] or digest.hexdigest() != meta["sha256"]: raise RuntimeError("Public baseline differs: " + name)
        return path
    def remove(directory):
        resolved = directory.resolve()
        if not resolved.is_relative_to(work.resolve()) or resolved == work.resolve(): raise RuntimeError("Unsafe materialization cleanup")
        shutil.rmtree(resolved)
    game = work / "game"; game.mkdir()
    base = next(b for b in plan["baselines"] if b["version"] == plan["seedVersion"])
    source_zip = fetch(base, "seed.zip")
    with zipfile.ZipFile(source_zip) as archive: archive.extractall(game)
    source_zip.unlink()
    command("apply", game, root / plan["seedPatch"])
    command("stamp", game, version)
    if json.loads((game / ".orcksd-build.json").read_text())["BuildId"] != plan["targetBuild"]: raise RuntimeError("Recovered Windows game differs from tested build")
    remove(game / ".orcksd-update")
    # Preserve the canonical already-published updater version, including its signature/runtime.
    updater = fetch(plan["updater"], "updater.zip")
    updater_out = root / "dist/updaters/1.2.0/OrC-KSD-offline-updater-1.2.0-win-x64.zip"; updater_out.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(updater, updater_out)
    with zipfile.ZipFile(updater) as archive: archive.extractall(game)
    updater.unlink()
    for transport in plan.get("patchTransports", []):
        output = root / transport["output"]; output.parent.mkdir(parents=True, exist_ok=True)
        subprocess.run([sys.executable, str(root / "tools/zip-transport.py"), "apply", str(root / plan["seedPatch"]), str(root / transport["delta"]), str(output)], check=True)
        if digest_file(output) != transport["sha256"]: raise RuntimeError("Recovered patch differs from locally tested bytes")
        inspected = subprocess.check_output(["dotnet", str(cli), "inspect", str(output)], text=True)
        metadata = json.loads(inspected)
        if metadata["Source"]["BuildId"] != transport["sourceBuild"] or metadata["Target"]["BuildId"] != plan["targetBuild"]: raise RuntimeError("Recovered patch has wrong source or target")
        print("CLOUD_TESTED_PATCH_EXACT_BYTES_OK", output.name, flush=True)
    for baseline in ([] if plan.get("patchTransports") else plan["baselines"]):
        if baseline["version"] == plan["seedVersion"]: continue
        source_zip = fetch(baseline, "baseline.zip")
        source = work / "baseline"; source.mkdir()
        with zipfile.ZipFile(source_zip) as archive: archive.extractall(source)
        source_zip.unlink()
        source_build = json.loads((source / ".orcksd-build.json").read_text())["BuildId"]
        output = delivery / "patches" / f"OrC-KSD-{version}-from-{source_build[:12]}-to-{plan['targetBuild'][:12]}.orcupdate"
        command("build", source, game, output, baseline["version"], version)
        command("apply", source, output); command("verify", source, game)
        remove(source)
        print("CLOUD_BASELINE_EXACT_FILES_OK", baseline["version"], plan["targetBuild"], flush=True)
    apk = fetch(plan["apkBase"], "previous.apk")
    fake = work / "apk"; fake.mkdir(); (fake / "KardsUi.exe").write_bytes(b"APK transport only; never executed"); os.link(apk, fake / "KardsUi.pck")
    command("apply", fake, root / plan["apkDelta"])
    with (fake / "KardsUi.pck").open("rb") as stream: digest = hashlib.file_digest(stream, "sha256").hexdigest()
    if digest != plan["apkTarget"]["sha256"]: raise RuntimeError("Recovered APK differs from tested signed APK")
    apk_out = delivery / "android" / f"OrC-KSD-{version}-android.apk"; apk_out.parent.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(fake / "KardsUi.pck", apk_out); remove(fake); apk.unlink()
    output = delivery / "full" / f"OrC-KSD-{version}-win-x64.zip"; output.parent.mkdir(parents=True, exist_ok=True)
    with zipfile.ZipFile(output, "w", compression=zipfile.ZIP_DEFLATED, compresslevel=6) as archive:
        for path in sorted(game.rglob("*")):
            if path.is_file(): archive.write(path, path.relative_to(game).as_posix())
    print("CLOUD_MATERIALIZATION_OK", plan["targetBuild"], "apk=" + digest, flush=True)


if __name__ == "__main__": main()

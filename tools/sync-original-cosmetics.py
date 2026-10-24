"""Fetch original KARDS cosmetic images, retain source URLs/hashes, and build a local catalog.

No generated artwork. Resized files, if required by the transfer budget, retain the originals.
"""
import concurrent.futures
import hashlib
import io
import json
from pathlib import Path
import time
import urllib.parse
import urllib.request

from PIL import Image

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / "proto/art/original-cosmetics"
BASE = "https://raw.githubusercontent.com/Gary-nope/KARDS-Assets/main/"
INDEX = ROOT / "artifacts/research-cosmetics-20261024/community-assets-index.json"


def get(url):
    with urllib.request.urlopen(urllib.request.Request(url, headers={"User-Agent": "OrC-KSD-source-research"}), timeout=35) as response:
        return response.read()


def fetch(entry):
    path = entry["path"]
    kind = "back" if path.startswith("CardBacks_Resized/") else "hq" if path.startswith("HQ2_unknown-id_Resized/") else "battlefield"
    source = BASE + urllib.parse.quote(path, safe="/")
    target = OUT / "source" / path
    target.parent.mkdir(parents=True, exist_ok=True)
    for attempt in range(3):
        try:
            data = target.read_bytes() if target.exists() and target.stat().st_size == entry["size"] else get(source)
            if len(data) != entry["size"]:
                raise ValueError("Source size mismatch: " + path)
            image = Image.open(io.BytesIO(data))
            image.load()
            target.write_bytes(data)
            original_hash = hashlib.sha256(data).hexdigest().upper()
            resource = target
            # Network copies must fit the agreed image budget; the source always stays untouched.
            if len(data) > 2 * 1024 * 1024 or max(image.size) > 4096 or image.format != "PNG":
                image = image.convert("RGBA")
                image.thumbnail((2048, 2048), Image.Resampling.LANCZOS)
                while True:
                    buffer = io.BytesIO()
                    image.save(buffer, "PNG", optimize=True)
                    data = buffer.getvalue()
                    if len(data) <= 2 * 1024 * 1024:
                        break
                    image.thumbnail((max(1, int(image.width * .85)), max(1, int(image.height * .85))), Image.Resampling.LANCZOS)
                resource = OUT / "network" / (Path(path).stem + ".png")
                resource.parent.mkdir(parents=True, exist_ok=True)
                resource.write_bytes(data)
            name = Path(path).stem
            return {"Kind": kind, "Name": name, "Image": hashlib.sha256(data).hexdigest().upper(),
                    "Resource": "res://" + resource.relative_to(ROOT).as_posix(), "Source": source,
                    "SourceSha256": original_hash, "Width": image.width, "Height": image.height,
                    "OriginalFile": path, "Derived": resource != target}
        except Exception:
            if attempt == 2:
                raise
            time.sleep(attempt + 1)


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    if not INDEX.exists():
        INDEX.parent.mkdir(parents=True,exist_ok=True)
        INDEX.write_bytes(get(BASE+"assets_index.json"))
    entries = [e for e in json.loads(INDEX.read_text(encoding="utf-8-sig"))["tree"]
               if e["path"].lower().endswith((".png", ".jpg")) and
               (e["path"].startswith(("CardBacks_Resized/", "HQ2_unknown-id_Resized/")) or
                "/tablecloths/images/" in e["path"])]
    complete, errors = [], []
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as executor:
        pending = {executor.submit(fetch, e): e["path"] for e in entries}
        for future in concurrent.futures.as_completed(pending):
            try:
                complete.append(future.result())
            except Exception as error:
                errors.append({"Path": pending[future], "Error": str(error)})
            if (len(complete) + len(errors)) % 50 == 0:
                print(f"Original assets fetched: {len(complete)}/{len(entries)}; errors={len(errors)}", flush=True)
    complete.sort(key=lambda e: (e["Kind"], e["Name"].casefold()))
    (OUT / "catalog.json").write_text(json.dumps(complete, ensure_ascii=False, indent=2), encoding="utf-8")
    (OUT / "download-errors.json").write_text(json.dumps(errors, ensure_ascii=False, indent=2), encoding="utf-8")
    print(f"CATALOG_READY assets={len(complete)} errors={len(errors)}", flush=True)
    if errors:
        raise SystemExit(1)


if __name__ == "__main__":
    main()

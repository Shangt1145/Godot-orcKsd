#!/usr/bin/env python3
"""Transport a ZIP exactly by reusing identical compressed entry bodies from a known ZIP."""
import argparse
import hashlib
import json
import pathlib
import struct
import uuid
import zipfile


def digest(path):
    with pathlib.Path(path).open("rb") as stream: return hashlib.file_digest(stream, "sha256").hexdigest()


def bodies(path):
    with zipfile.ZipFile(path) as archive, open(path, "rb") as stream:
        for entry in sorted(archive.infolist(), key=lambda item: item.header_offset):
            stream.seek(entry.header_offset + 26); name, extra = struct.unpack("<HH", stream.read(4))
            offset = entry.header_offset + 30 + name + extra
            stream.seek(offset); data = stream.read(entry.compress_size)
            yield offset, data


def build(source, target, packet):
    if len({pathlib.Path(p).resolve() for p in [source, target, packet]}) != 3: raise RuntimeError("Source, target and transport paths must differ")
    index = {hashlib.sha256(data).hexdigest(): (offset, len(data)) for offset, data in bodies(source) if data}
    plan = {"format": "zip-entry-reuse-v1", "sourceSha256": digest(source), "targetSha256": digest(target), "targetSize": pathlib.Path(target).stat().st_size, "parts": []}
    serial = 0
    with zipfile.ZipFile(packet, "w", zipfile.ZIP_DEFLATED, compresslevel=6) as output, open(target, "rb") as stream:
        def literal(data):
            nonlocal serial
            if not data: return
            name = f"new/{serial:06}.bin"; serial += 1; output.writestr(name, data); plan["parts"].append({"blob": name, "length": len(data)})
        previous = 0
        for offset, data in bodies(target):
            stream.seek(previous); literal(stream.read(offset - previous))
            found = index.get(hashlib.sha256(data).hexdigest()) if data else None
            if found and found[1] == len(data): plan["parts"].append({"copy": found[0], "length": found[1]})
            else: literal(data)
            previous = offset + len(data)
        stream.seek(previous); literal(stream.read())
        output.writestr("transport.json", json.dumps(plan, separators=(",", ":")))
    print("ZIP_TRANSPORT_BUILT", pathlib.Path(packet).stat().st_size, plan["targetSha256"], flush=True)


def apply(source, packet, target):
    if len({pathlib.Path(p).resolve() for p in [source, packet, target]}) != 3: raise RuntimeError("Transport paths must differ")
    temporary = pathlib.Path(target).with_name(pathlib.Path(target).name + ".transport-" + uuid.uuid4().hex + ".part")
    with zipfile.ZipFile(packet) as archive, open(source, "rb") as before, temporary.open("xb") as output:
        plan = json.loads(archive.read("transport.json")); checksum = hashlib.sha256()
        if plan["format"] != "zip-entry-reuse-v1" or digest(source) != plan["sourceSha256"]: raise RuntimeError("Wrong ZIP transport source")
        for part in plan["parts"]:
            if part["length"] < 0: raise RuntimeError("Invalid segment length")
            if "copy" in part:
                if part["copy"] < 0: raise RuntimeError("Invalid source offset")
                before.seek(part["copy"]); data = before.read(part["length"])
            else: data = archive.read(part["blob"])
            if len(data) != part["length"]: raise RuntimeError("ZIP transport segment truncated")
            output.write(data); checksum.update(data)
    if checksum.hexdigest() != plan["targetSha256"] or temporary.stat().st_size != plan["targetSize"]:
        temporary.unlink(); raise RuntimeError("Recovered ZIP differs")
    temporary.replace(target)
    print("ZIP_TRANSPORT_EXACT_BYTES_OK", plan["targetSha256"], flush=True)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__); parser.add_argument("mode", choices=["build", "apply"]); parser.add_argument("source"); parser.add_argument("other"); parser.add_argument("output")
    args = parser.parse_args()
    (build if args.mode == "build" else apply)(args.source, args.other, args.output)

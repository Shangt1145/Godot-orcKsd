#!/usr/bin/env python3
"""刷新 verified-cards.json（审核准入清单）。

哈希必须与 VerifiedCardPool 的口径逐字节一致：
    SHA256(UTF8(JsonElement.GetRawText()))  —— 卡值节点的原始文本（首 token 到末 token）。
用 json.JSONDecoder.raw_decode 的 span 精确提取，别用 json.dumps 重序列化（空白不同必错）。

用法：
    refresh_verified_cards.py add <cardId> [<cardId> ...]   # 追加并重写
    refresh_verified_cards.py list [前缀]                    # 列出现有条目
"""
import hashlib
import json
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SOURCE = ROOT / "proto" / "data" / "nations"
MANIFEST = ROOT / "proto" / "bridge" / "Kards.Ui.OrcBridge" / "Resources" / "verified-cards.json"
DECODER = json.JSONDecoder()


def skip_ws(text, i):
    while i < len(text) and text[i] in " \t\r\n":
        i += 1
    return i


def raw_hashes(path):
    """{卡 id: 大写 SHA256}，按 GetRawText 语义提取。"""
    text = path.read_text(encoding="utf-8")
    marker = '"cards"'
    if marker not in text:
        return {}
    m = text.index(marker)
    i = skip_ws(text, m + len(marker))
    assert text[i] == ":", f"{path.name}: cards 后应为冒号"
    i = skip_ws(text, i + 1)
    assert text[i] == "{", f"{path.name}: cards 后应为对象"
    out = {}
    while True:
        i = skip_ws(text, i + 1)
        if text[i] == "}":
            break
        key, j = DECODER.raw_decode(text, i)
        j = skip_ws(text, j)
        assert text[j] == ":", f"{path.name}: {key} 后应为冒号"
        v = skip_ws(text, j + 1)
        value, end = DECODER.raw_decode(text, v)
        raw = text[v:end]
        out[key] = hashlib.sha256(raw.encode("utf-8")).hexdigest().upper()
        i = end
        while text[i] in " \t\r\n":
            i += 1
        if text[i] == ",":
            continue
    return out


def load_all():
    hashes = {}
    for path in sorted(SOURCE.glob("*.json")):
        hashes.update(raw_hashes(path))
    return hashes


def main():
    cmd = sys.argv[1] if len(sys.argv) > 1 else "list"
    current = json.loads(MANIFEST.read_text(encoding="utf-8"))
    entries = {e["CardId"]: e["Sha256"] for e in current}
    if cmd == "add":
        hashes = load_all()
        missing = [cid for cid in sys.argv[2:] if cid not in hashes]
        if missing:
            sys.exit(f"源声明中不存在: {missing}")
        for cid in sys.argv[2:]:
            entries[cid] = hashes[cid]
            print(f"added {cid} {hashes[cid]}")
        merged = [{"CardId": k, "Sha256": entries[k]} for k in sorted(entries)]
        MANIFEST.write_text(json.dumps(merged, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"total {len(merged)}")
    elif cmd == "remove":
        for cid in sys.argv[2:]:
            if entries.pop(cid, None) is None:
                print(f"not present: {cid}")
            else:
                print(f"removed {cid}")
        merged = [{"CardId": k, "Sha256": entries[k]} for k in sorted(entries)]
        MANIFEST.write_text(json.dumps(merged, indent=2, ensure_ascii=False) + "\n", encoding="utf-8")
        print(f"total {len(merged)}")
    elif cmd == "list":
        prefix = sys.argv[2] if len(sys.argv) > 2 else ""
        for e in current:
            if e["CardId"].startswith(prefix):
                print(e["CardId"], e["Sha256"][:12])
        print(f"total {len(current)}")
    else:
        sys.exit(__doc__)


if __name__ == "__main__":
    main()

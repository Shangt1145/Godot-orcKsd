#!/usr/bin/env python3
"""把卡包 csx 里的 DesktopTargetContext 调用迁到引擎侧 targeter。

背景
----
卡数据里的效果脚本（card.json → components[effects].inline[].root...csx）原本调的是

    await Kards.Ui.OrcBridge.DesktopTargetContext.SelectAsync(orcSelf, "{json}", "{hash}", ct)

`Kards.Ui.OrcBridge` 是**前端程序集**——桌面版时期 UI 与引擎同进程，这么写没问题；
搬到 Orc.Server（服务端权威）后，服务端进程里没有这个类型，效果静默不执行，
也就永远不产生 `notify/target.request`。

正确入口是引擎自己的 targeter：

    await orcRuntime.BeginTargetSelectionAsync(orcSelf,
        new Orc.Game.Effects.EffectSelector("all", side, zone, Count:null), ct)

一次调用＝一次 targeter 请求（经 TargeterManager → 前端桥接），联机才有 pending。

映射
----
声明 JSON 的 side/zone → EffectSelector：
    side: any→both, enemy→enemy, friendly→friendly, 缺省→both
    zone: field→null（不限线）, frontline→"frontline", support→"support", 缺省→null

用法
----
    python tools/migrate-targeter-csx.py --dry-run      # 只看会改什么
    python tools/migrate-targeter-csx.py                # 备份后原地改
    python tools/migrate-targeter-csx.py --backup DIR   # 指定备份目录

只改匹配到的那一段文本，不重新序列化整个 JSON——避免缩进/转义全文件漂移。
"""

from __future__ import annotations

import argparse
import json
import re
import shutil
from pathlib import Path

DEFAULT_CARDS = Path(__file__).resolve().parents[1] / "proto/data/kards/packs/builtin/cards"

# 目标调用的整段（含 hash 与 ct），捕获声明字面量。
# 注意：这里读的是**原始 JSON 文本**，字符串里的引号是 `\"`（JSON 转义），
# 所以正则要按字面匹配反斜杠+引号，而不是裸引号。
CALL = re.compile(
    r"Kards\.Ui\.OrcBridge\.DesktopTargetContext\.SelectAsync\(\s*orcSelf\s*,\s*"
    r'\\"(?P<decl>(?:[^"\\]|\\.)*)\\"\s*,\s*\\"[0-9A-Fa-f]*\\"\s*,\s*ct\s*\)'
)

SIDE_MAP = {"any": "both", "enemy": "enemy", "friendly": "friendly", "": "both"}
ZONE_MAP = {"field": "null", "frontline": "FRONTLINE", "support": "SUPPORT", "": "null"}

# JSON 文件里 C# 字面量用的引号是 `\u0022`，而反斜杠本身在 JSON 里还要再转义一层，
# 所以落盘文本是 `\\u0022`。替换文本必须保持同一形式，否则字符串边界会错乱。
Q = r"\\u0022"


def decode_declaration(raw: str) -> dict:
    """把 C# 字面量（`\\u0022` 形式）还原成 JSON 对象。"""
    return json.loads(raw.replace(Q, '"'))


def build_replacement(side: str, zone: str) -> str:
    zone_literal = "null" if zone == "null" else f"{Q}{zone.lower()}{Q}"
    return (
        "orcRuntime.BeginTargetSelectionAsync(orcSelf,"
        f"new Orc.Game.Effects.EffectSelector({Q}all{Q},{Q}{side}{Q},{zone_literal},Count:null),ct)"
    )


def migrate_file(path: Path, backup_dir: Path | None, dry_run: bool) -> tuple[bool, str]:
    text = path.read_text(encoding="utf-8")
    if "DesktopTargetContext" not in text:
        return False, "skip（无 UI 桥调用）"

    match = CALL.search(text)
    if not match:
        return False, "NO-MATCH（有引用但正则没匹配，人工看一下）"

    try:
        declaration = decode_declaration(match.group("decl"))
    except json.JSONDecodeError as error:
        return False, f"声明解析失败：{error}"

    raw_side = str(declaration.get("side") or "")
    raw_zone = str(declaration.get("zone") or "")
    side = SIDE_MAP.get(raw_side, "both")
    zone = ZONE_MAP.get(raw_zone, "null")

    replaced = CALL.sub(build_replacement(side, zone), text, count=1)
    try:
        json.loads(replaced)
    except json.JSONDecodeError as error:
        return False, f"替换后 JSON 非法（已放弃）：{error}"

    summary = f"side={raw_side or '-'}/{side} zone={raw_zone or '-'}/{zone}"
    if dry_run:
        return True, f"would change: {summary}"

    if backup_dir is not None:
        backup_dir.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, backup_dir / path.name)

    path.write_text(replaced, encoding="utf-8", newline="")
    return True, f"changed: {summary}"


ENGINE_CALL = "await orcRuntime.BeginTargetSelectionAsync("


def wrap_array(text: str) -> tuple[str, int]:
    """把 `await BeginTargetSelectionAsync(...)` 包成 `(...).ToArray()`。

    必须保持返回值是 `Card[]`：原脚本里有 `new Card[][]{orcV0, ...}` 这种嵌套，
    换成 IReadOnlyList 会编译失败 → 整段 csx 不执行 → 效果静默（且无任何日志）。
    """
    pieces: list[str] = []
    index = 0
    wrapped = 0
    while True:
        start = text.find(ENGINE_CALL, index)
        if start < 0:
            pieces.append(text[index:])
            break

        cursor = start + len(ENGINE_CALL)
        depth = 1
        while cursor < len(text) and depth > 0:
            if text[cursor] == "(":
                depth += 1
            elif text[cursor] == ")":
                depth -= 1
            cursor += 1

        if text.startswith(".ToArray()", cursor):
            pieces.append(text[index:cursor + len(".ToArray()")])
            index = cursor + len(".ToArray()")
            continue

        pieces.append(text[index:start])
        pieces.append("(" + text[start:cursor] + ").ToArray()")
        wrapped += 1
        index = cursor

    return "".join(pieces), wrapped


def wrap_file(path: Path, backup_dir: Path | None, dry_run: bool) -> tuple[bool, str]:
    text = path.read_text(encoding="utf-8")
    if ENGINE_CALL not in text:
        return False, "skip（没有引擎调用）"

    replaced, count = wrap_array(text)
    if count == 0:
        return False, "skip（已经包过）"

    try:
        json.loads(replaced)
    except json.JSONDecodeError as error:
        return False, f"包数组后 JSON 非法（已放弃）：{error}"

    if dry_run:
        return True, f"would wrap: {count} 处"

    if backup_dir is not None:
        backup_dir.mkdir(parents=True, exist_ok=True)
        shutil.copy2(path, backup_dir / path.name)

    path.write_text(replaced, encoding="utf-8", newline="")
    return True, f"wrapped: {count} 处"


def main() -> int:
    parser = argparse.ArgumentParser(description="csx 选靶调用迁移到引擎 targeter")
    parser.add_argument("--cards", type=Path, default=DEFAULT_CARDS, help="卡包 cards 目录")
    parser.add_argument("--backup", type=Path, default=None, help="备份目录（默认不备份）")
    parser.add_argument("--dry-run", action="store_true", help="只报告，不写盘")
    parser.add_argument(
        "--wrap-array",
        action="store_true",
        help="第二段：把 BeginTargetSelectionAsync 的返回值包成 Card[]（类型不变，避免 csx 编译失败）",
    )
    args = parser.parse_args()

    if not args.cards.is_dir():
        print(f"目录不存在：{args.cards}")
        return 1

    changed = failed = 0
    for path in sorted(args.cards.glob("*.card.json")):
        if args.wrap_array:
            ok, message = wrap_file(path, args.backup, args.dry_run)
        else:
            ok, message = migrate_file(path, args.backup, args.dry_run)
        if ok:
            changed += 1
            print(f"{path.name:36s} {message}")
        elif message.startswith("NO-MATCH") or "失败" in message or "非法" in message:
            failed += 1
            print(f"{path.name:36s} {message}")

    print(f"\n{'预计' if args.dry_run else '已'}修改 {changed} 张，问题 {failed} 张")
    return 0 if failed == 0 else 1


if __name__ == "__main__":
    raise SystemExit(main())

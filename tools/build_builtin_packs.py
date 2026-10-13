# -*- coding: utf-8 -*-
"""内置卡包生成器：KARDS 官方 API dump → **引擎新资产模型**卡包目录。

输入：artifacts/kards-api-all-cards-20261012.json（官方 GraphQL 全量 dump）
输出：proto/data/kards/<packId>/（manifest.json + cards/*.card.json + texts.json）

迁移说明（前端资产迁移 S9）：清单由前端自造的 `pack.json` 改为引擎 `manifest.json`
（`PackageManifest`：kind=cardpack / id / guid / version / dependsOn[{id,version}] / assets[]）。
`guid` 由包 id 经 uuid5 确定性派生（跨次稳定——资产名与脚本缓存键依赖它）；`assets[]` 留空
（卡/效果/词条全走目录约定）；版本为语义化 `1.0.0`。

准入规则（v1）：
- 排除 OnlySpawnable（衍生牌）与 Special（promo 重印，语义不明）集合；
- 词条 attributes 必须全部命中引擎映射（CardAttributeMap 已实现面）；
- 卡面文本必须被效果转换器完整解析（文本→效果草稿由桥接层在装载时做，
  这里只做"可解析性"预检——与桥接 PackCardTextParser 同一套模板表）；
- 白板卡（无文本）直接准入。

产出报告：proto/data/kards/_report.json（每集合准入/剔除计数与原因）。
"""
from __future__ import annotations

import json
import re
import shutil
import sys
import uuid
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
DUMP = ROOT / "artifacts" / "kards-api-all-cards-20261012.json"
OUT = ROOT / "proto" / "data" / "kards"

# 包身份派生命名空间（固定字面——保证同名包跨次生成得到同一 guid）。
PACK_NAMESPACE = uuid.UUID("6f3a1c2e-9b47-4d5a-8c31-0d2e5f7a9b10")

# 官方集合键 → (包目录名, 中文包名, 是否扩展包)
SETS = {
    "Base": ("builtin", "内置卡包", False),
    "Allegiance": ("exp-loyalty", "忠诚", True),
    "TheatersOfWar": ("exp-warzone", "战区", True),
    "Breakthrough": ("exp-breakthrough", "突破", True),
    "WorldAtWar": ("exp-worldatwar", "世界大战", True),
    "Legions": ("exp-legions", "军团", True),
    "BrothersInArms": ("exp-comrades", "战友", True),
    "WinterWar": ("exp-winterwar", "冬季战争", True),
    "CovertOps": ("exp-covertops", "秘密行动", True),
    "BloodAndIron": ("exp-bloodandiron", "血与铁", True),
    "NavalWarfare": ("exp-navalwarfare", "海战", True),
    "Homefront": ("exp-homefront", "国土阵线", True),
    "OceaniaStorm": ("exp-oceaniastorm", "澳新风暴", True),
}

FACTIONS = {"Germany", "Soviet", "USA", "Britain", "Japan", "France", "Italy", "Poland", "Finland", "Anzac"}
UNIT_TYPES = {"infantry", "tank", "artillery", "fighter", "bomber"}
MAPPED_KW = {"blitz", "fury", "smokescreen", "ambush", "pincer", "mobilize", "covert", "guard", "shock"}
VALUED_KW = ("heavyArmor", "intel")

# ---- 与桥接层 PackCardTextParser 保持同一模板面（可解析性预检） ----
_T = r"(?:1\s*个敌方(?:单位|目标)|1\s*个友方单位|1\s*个(?:单位|目标)|所有敌方单位|所有友方单位|所有(?:单位|目标))"
OPS_PATTERNS = [
    re.compile(r"^抽\s*(\d+)\s*张牌$"),
    re.compile(r"^(?:额外)?获得\s*(\d+)\s*个指挥点槽$"),
    re.compile(r"^获得\s*(\d+)\s*个指挥点$"),
    re.compile(rf"^对\s*{_T}\s*造成\s*(\d+)\s*点伤害$"),
    re.compile(rf"^消灭\s*{_T}$"),
    re.compile(rf"^压制\s*{_T}(?:\s*1\s*回合)?$"),
    re.compile(rf"^抑制\s*{_T}$"),
    re.compile(rf"^使\s*{_T}获得\s*[+-]?\d+\s*/\s*[+-]?\d+$"),
    re.compile(rf"^使\s*{_T}获得\s*[+-]?\d+\s*行动花费$"),
]


def attr_ok(attributes: list[str]) -> bool:
    for a in attributes:
        if a in MAPPED_KW:
            continue
        if any(a.startswith(p) and a[len(p):].isdigit() for p in VALUED_KW):
            continue
        return False
    return True


def text_parsable(text: str) -> bool:
    """与桥接层模板同面：整卡文本按分隔符切句后每句必须命中模板。"""
    body = text.strip().rstrip("。")
    body = re.sub(r"^部署：", "", body)
    body = re.sub(r"^打出：", "", body)
    parts = [p.strip() for p in re.split(r"[；;]", body) if p.strip()]
    if not parts:
        return False
    for part in parts:
        seg = part.strip().rstrip("。").strip()
        if not any(p.match(seg) for p in OPS_PATTERNS):
            return False
    return True


def engine_card(d: dict) -> dict | None:
    """API 卡 → 引擎单卡数据体；不可准入返回 None（原因由调用方归类）。"""
    faction = d["faction"]
    if faction not in FACTIONS:
        return "faction"
    if not attr_ok(d.get("attributes") or []):
        return "keyword"
    text = (d.get("text") or {}).get("zh-Hans", "")
    if text and not text_parsable(text):
        return "effect"
    components: list[dict] = []
    dtype = d["type"]
    if dtype in UNIT_TYPES:
        components.append({"component": "typeCategory", "type": [dtype]})
    elif dtype == "order":
        components.append({"component": "typeCategory", "type": ["order"]})
    elif dtype == "countermeasure":
        components.append({"component": "typeCategory", "type": ["countermeasure"]})
    else:
        return "type"
    components.append({"component": "factionCost", "faction": faction, "kredits": int(d.get("kredits") or 0)})
    components.append({"component": "battleStats",
                       "operationCost": int(d.get("operationCost") or 0),
                       "attack": int(d.get("attack") or 0),
                       "defense": max(int(d.get("defense") or 0), 1)})
    components.append({"component": "tagData", "rarity": d["rarity"]})
    attrs = d.get("attributes") or []
    if attrs:
        components.append({"component": "keywords", "attributes": attrs})
    card = {
        "schemaVersion": 1,
        "id": d["id"],
        "name": (d.get("title") or {}).get("zh-Hans") or d["id"],
        "components": components,
    }
    return card, text


def main() -> int:
    cards = json.loads(DUMP.read_text(encoding="utf-8"))
    OUT.mkdir(parents=True, exist_ok=True)
    report: dict[str, dict] = {}
    for set_key, (pack_id, pack_name, is_expansion) in SETS.items():
        # 引擎 `PackageDiscovery` 约定：卡包置于 <基目录>/packs/<包>/manifest.json。
        pack_dir = OUT / "packs" / pack_id
        (pack_dir / "cards").mkdir(parents=True, exist_ok=True)
        for old in (pack_dir / "cards").glob("*.card.json"):
            old.unlink()
        counts = {"admitted": 0, "keyword": 0, "effect": 0, "type": 0, "faction": 0}
        texts: dict[str, str] = {}
        # 引擎 `manifest.json`（PackageManifest）：扩展包依赖基包（版本约束锁定主版）。
        # `name` 为前端显示名——引擎 Parse 读宽忽略它，前端收藏页读它命名瓷砖。
        manifest = {
            "kind": "cardpack",
            "id": pack_id,
            "name": pack_name,
            "guid": str(uuid.uuid5(PACK_NAMESPACE, pack_id)),
            "version": "1.0.0",
            "dependsOn": ([{"id": "builtin", "version": "1.*.*"}] if is_expansion else []),
            "assets": [],
        }
        for node in cards:
            d = node["json"]
            if d["set"] != set_key:
                continue
            result = engine_card(d)
            if isinstance(result, str):
                counts[result] += 1
                continue
            card, text = result
            if text:
                texts[card["id"]] = text
            (pack_dir / "cards" / f"{d['id']}.card.json").write_text(
                json.dumps(card, ensure_ascii=False, indent=1), encoding="utf-8")
            counts["admitted"] += 1
        (pack_dir / "texts.json").write_text(
            json.dumps(texts, ensure_ascii=False, indent=1), encoding="utf-8")
        if counts["admitted"] == 0:
            # 该集合没有一张卡达到 v1 准入面：不发布空包（报告里如实计 0）。
            shutil.rmtree(pack_dir, ignore_errors=True)
            report[f"{pack_name}（未发布：v1 准入 0 张）"] = {"packId": pack_id, **counts}
            continue
        (pack_dir / "manifest.json").write_text(
            json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
        report[pack_name] = {"packId": pack_id, **counts}
    (OUT / "_report.json").write_text(
        json.dumps(report, ensure_ascii=False, indent=1), encoding="utf-8")
    for name, counts in report.items():
        print(f"{name}: +{counts['admitted']} (kw剔除{counts['keyword']} 效果剔除{counts['effect']} 类型{counts['type']} 阵营{counts['faction']})")
    return 0


if __name__ == "__main__":
    sys.exit(main())

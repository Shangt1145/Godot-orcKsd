# -*- coding: utf-8 -*-
"""抓取 KARDS 官方全量卡数据（GraphQL）→ artifacts/kards-api-all-cards-YYYYMMDD.json。

端点说明：api.kards.com 不在 Watt Toolkit 加速范围（直连被断），
herokuapi.kards.com 是同一 GraphQL 服务的可达别名。
"""
from __future__ import annotations

import json
import ssl
import time
import urllib.request
from datetime import date
from pathlib import Path

ROOT = Path(__file__).resolve().parent.parent
URL = "https://herokuapi.kards.com/graphql"
QUERY = """query getCards($language: String, $offset: Int, $showSpawnables: Boolean, $showExiles: Boolean, $showReserved: Boolean) {
  cards(language: $language, first: 100, offset: $offset, showSpawnables: $showSpawnables, showExiles: $showExiles, showReserved: $showReserved) {
    pageInfo { count hasNextPage }
    edges { node { cardId json reserved } }
  }
}"""


def main() -> int:
    ctx = ssl.create_default_context()
    ctx.check_hostname = False
    ctx.verify_mode = ssl.CERT_NONE
    cards, offset = [], 0
    while True:
        body = {"operationName": "getCards",
                "variables": {"language": "zh", "offset": offset,
                              "showSpawnables": True, "showExiles": True, "showReserved": True},
                "query": QUERY}
        req = urllib.request.Request(URL, data=json.dumps(body).encode(),
                                     headers={"referer": "https://www.kards.com/",
                                              "accept": "*/*", "content-type": "application/json"})
        for attempt in range(3):
            try:
                with urllib.request.urlopen(req, timeout=30, context=ctx) as r:
                    data = json.load(r)
                break
            except Exception:
                if attempt == 2:
                    raise
                time.sleep(3)
        page = data["data"]["cards"]
        cards.extend(edge["node"] for edge in page["edges"])
        if not page["pageInfo"]["hasNextPage"]:
            break
        offset += 100
        time.sleep(0.8)
    out = ROOT / "artifacts" / f"kards-api-all-cards-{date.today():%Y%m%d}.json"
    out.write_text(json.dumps(cards, ensure_ascii=False), encoding="utf-8")
    print(f"fetched {len(cards)} cards -> {out}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

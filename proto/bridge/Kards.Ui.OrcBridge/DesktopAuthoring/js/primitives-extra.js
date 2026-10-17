/* 自动生成，勿手改：node game/tools/build-primitives.js */
(function (g) {
g.KG_PRIMITIVES_EXTRA = {
 "rules": [
  {
   "id": "discard-n-cards",
   "re": "弃\\s*([一二两三四五六七八九十\\d]+)\\s*张牌",
   "op": "discard",
   "args": {
    "count": "$1",
    "side": "self",
    "mode": "random"
   },
   "note": "「弃一张牌 / 弃两张牌」—— 随机弃自己的牌"
  },
  {
   "id": "hq-defense-plus",
   "re": "使(友方|我方|你的|敌方|对方)?总部防御力\\s*[+＋加]\\s*([一二两三四五六七八九十\\d]+)",
   "op": "hqMaxUp",
   "args": {
    "amount": "$2",
    "side": {
     "$1": "map",
     "敌方": "enemy",
     "对方": "enemy",
     "$else": "self"
    }
   },
   "note": "「使友方总部防御力+5」—— 总部上限与当前值一起加"
  },
  {
   "id": "transform-hand-to-set",
   "re": "将其随机转换为一个([A-Za-z\\u4e00-\\u9fa5]{2,8})单位",
   "op": "transformHandCard",
   "args": {
    "side": "self",
    "mode": "random",
    "filter": {
     "set": {
      "$1": "str"
     }
    }
   },
   "note": "「将其随机转换为一个USG单位」—— 随机把手牌一张换成该国家的单位（文档没列'转换'，我补的）。注意 $1 要用 {\"$1\":\"str\"} 取原样，否则会被当数字转成 null"
  },
  {
   "id": "join-frontline",
   "re": "^(?:并使?其?|将其|将之)?\\s*(?:加入|移至|移动到)\\s*前线",
   "op": "move",
   "args": {
    "to": "frontline",
    "target": "self"
   },
   "note": "「若可能，加入前线」的后半句 —— 文档 §1 的操作原语「加入战场/移动」，之前完全没实现，导致分号后整段被丢"
  },
  {
   "id": "join-support",
   "re": "^(?:并使?其?|将其|将之)?\\s*(?:加入|移至|移动到)\\s*(?:支援阵线|支援线)",
   "op": "move",
   "args": {
    "to": "support"
   },
   "note": "「加入支援阵线」"
  },
  {
   "id": "until-line-full",
   "re": "^直到\\s*(?:阵线|支援线)\\s*已满$",
   "op": "repeat",
   "args": {
    "times": {
     "supportFree": "self"
    }
   },
   "note": "「直到阵线已满」—— 数量限定原语：重复到支援线没空位为止"
  },
  {
   "id": "until-frontline-full",
   "re": "^直到\\s*前线\\s*已满$",
   "op": "repeat",
   "args": {
    "times": {
     "frontlineFree": "self"
    }
   },
   "note": "「直到前线已满」"
  },
  {
   "id": "upgrade-to-veteran",
   "re": "^(?:升为|晋升为|成为)\\s*老兵(?:形态)?$",
   "op": "upgradeSelf",
   "args": {
    "target": "self"
   },
   "note": "「升为老兵」—— op 早就有了(effects.js 的 upgradeSelf)，缺的只是解析规则（空投师）"
  },
  {
   "id": "damage-hq",
   "re": "对\\s*(敌方|对方|对手|敌军|友方|我方|你的|自己)?\\s*总部\\s*(?:施加|造成|打出)\\s*([一二两三四五六七八九十\\d]+)\\s*点?(?:伤害|伤害值)",
   "op": "damageHQ",
   "args": {
    "amount": "$2",
    "side": {
     "$1": "map",
     "敌方": "enemy",
     "对方": "enemy",
     "对手": "enemy",
     "敌军": "enemy",
     "友方": "self",
     "我方": "self",
     "你的": "self",
     "自己": "self",
     "$else": "enemy"
    }
   },
   "note": "「对敌方总部施加4点伤害」「对总部造成N点伤害」——直接打总部。以前**完全没这条规则**，整句落到'未实现'（老鼠那张充能卡就是被它坑的：充能正常触发，但效果是空的）"
  },
  {
   "id": "self-move-frontline-inline",
   "re": "^(?:并将其|将其|将其|使其)?\\s*移(?:至|到|上)\\s*(?:下一阵线|前线)",
   "op": "move",
   "args": {
    "to": "frontline",
    "target": "self"
   },
   "note": "「该单位部署时将其移至下一阵线」—— 单位自身效果里的「其」= 自己，不是随机友方单位"
  }
 ]
};
})(typeof window !== "undefined" ? window : globalThis);

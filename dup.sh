#!/bin/zsh
# 找【概念级重复】—— 同一件事被重新实现了几次。
#
# jscpd 那类工具找的是文本相似,而我们的病是"写法不同、干的是同一件事":
# 24处鼠标换算每处才2行、"实心且非平台"是个单行表达式,它一个都报不出来。
# 直接数关键 API 的出现次数更管用:有现成封装还在用原始 API,就是漏网。
cd "$(dirname "$0")"
printf "%-34s %-10s %s\n" "原始API(应该走封装)" "处数" "封装"
printf "%.0s─" {1..78}; echo
# -F 定长匹配:模式里带 [ 的话正则会把它当字符类,数出来是 0
# --include 递归全树:分了子目录之后 *.cs 只展开当前层,数出来全是 0
chk() { printf "%-34s %-10s %s\n" "$1" "$(grep -rhF --include='*.cs' --exclude-dir=bin --exclude-dir=obj --exclude-dir=.claude -- "$2" . 2>/dev/null | grep -vc '^\s*//')" "$3"; }
chk "Main.mouseX ="        "Main.mouseX ="            "Cursor.AimTile/AimPx/AimOffset"
chk "Main.tileSolid["      "Main.tileSolid["          "Predicates.IsSolid/IsWall"
chk "Main.tileSolidTop["   "Main.tileSolidTop["       "Predicates.IsPlatform"
chk "velocity.Y == 0f"     ".velocity.Y == 0f"        "落地判据(尚无封装)"
chk "controlUseItem=true"  "controlUseItem = true"    "ItemUseCoordinator"
chk "IsInTileInteraction"  "IsInTileInteractionRange" "够不够得着(判据表已登记)"
echo
echo "── jscpd 文本级重复(5行/50token 以上) ──"
npx --yes jscpd@4 . --pattern "**/*.cs" --min-lines 5 --min-tokens 50 \
  --reporters console --format csharp 2>/dev/null | grep -E "^ - |^   [A-Z]|Found [0-9]+ clones"

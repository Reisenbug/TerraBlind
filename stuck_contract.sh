#!/bin/sh
# 原语报 stuck 必须走 Stuck(Blocker) —— 不准直接 Finish("stuck")/Done("stuck")。
#
# 为什么要脚本管:靠"记得加"是必漏的。同一种阻挡在一个原语里会挖开、在另一个里直接失败,
# 就是这么来的。新写原语时这里会当场报错,逼着把失败现场交出来。
cd "$(dirname "$0")" || exit 1
# Stuck() 函数体内那句 Finish("stuck") 是"救不了才真失败"的兜底,合法。
# 用 awk 把每个 Stuck(Blocker) 函数体整段摘掉再查,剩下的才是绕过契约的。
bad=$(for f in *.cs; do
        [ "$f" = "Unstick.cs" ] && continue
        awk -v F="$f" '
          /static void Stuck\(/ { inb=1 }
          inb { if (/^\t\t}/) inb=0; next }
          /Finish\("stuck"\)|Done\("stuck"/ {
            if ($0 !~ /no_player/ && $0 !~ /Done\("stuck", reason\);/) print F":"NR":"$0
          }' "$f"
      done)
if [ -n "$bad" ]; then
  echo "✗ 这些地方绕过了 Stuck(Blocker),失败现场丢了:"
  echo "$bad"
  echo ""
  echo "  改成 Stuck(new Blocker(BlockKind.X, wx, wy, \"...\")) —— 四类:"
  echo "  Terrain=挖掉  SelfInWay=让开  OutOfReach=造落脚点  Hopeless=真没救"
  exit 1
fi
echo "✓ stuck 契约:所有原语都交了失败现场"

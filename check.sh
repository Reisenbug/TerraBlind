#!/bin/sh
# Type-check the mod WITHOUT packing it, so it works while tModLoader is running.
#
# A full `dotnet build` runs the C# compile fine but then fails at the pack step with TML003
# ("close tModLoader to build mods directly"). -t:Compile stops before packing: every compile
# error still surfaces, in under a second, with no game restart and nobody typing /build.
#
# This does NOT replace the in-game build — it only proves the code compiles. Loading the new
# code into the running game is still `/build TerraBlind` in chat.
cd "$(dirname "$0")" || exit 1
dotnet build -t:Compile -v q --nologo -nowarn:ChangeMagicNumberToID 2>&1 | grep -Ev "^$" | tail -20

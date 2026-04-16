#!/bin/bash
set -e
cd osu.Game.Rulesets.EmptyFreeform
dotnet build
cp bin/Debug/net8.0/osu.Game.Rulesets.EmptyFreeform.dll ~/.local/share/osu/rulesets
rm ~/.local/share/osu/logs/*
osu-lazer
cd ..

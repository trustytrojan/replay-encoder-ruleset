#!/bin/bash
cd osu.Game.Rulesets.ReplayEncoder
dotnet build || exit $?
rm ~/.local/share/osu/logs/*
rm ~/.local/share/osu/rulesets/*
cp bin/Debug/net8.0/osu.Game.Rulesets.ReplayEncoder.dll ~/.local/share/osu/rulesets
osu-lazer
cd ..

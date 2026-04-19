#!/bin/bash
set -e
OSU_PATH=~/.local/share/osu

# Build the ruleset
dotnet build -c Release

# Clean up data directory, copy ruleset
rm $OSU_PATH/{logs/*,rulesets/*} || true
cp osu.Game.Rulesets.ReplayEncoder/bin/Release/net8.0/osu.Game.Rulesets.ReplayEncoder.dll $OSU_PATH/rulesets

# Run game, follow runtime log
{
	until ls $OSU_PATH/logs/*.runtime.log &>/dev/null; do sleep 1; done
	tail -f $OSU_PATH/logs/*.runtime.log & echo $! >/tmp/tail_pid
} &
trap 'kill "$(</tmp/tail_pid)"' SIGINT
clear
osu-lazer
wait

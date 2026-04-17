#!/bin/bash
set -e
OSU_PATH=~/.local/share/osu

# Build the ruleset
dotnet build

# Clean up data directory, copy ruleset
rm $OSU_PATH/{logs/*,rulesets/*} || true
cp osu.Game.Rulesets.ReplayEncoder/bin/Debug/net8.0/osu.Game.Rulesets.ReplayEncoder.dll $OSU_PATH/rulesets

# Run game, follow runtime log
{
	until ls ~/.local/share/osu/logs/*.runtime.log &>/dev/null; do sleep 1; done
	tail -f ~/.local/share/osu/logs/*.runtime.log & echo $! >/tmp/tail_pid
} &
osu-lazer
kill "$(</tmp/tail_pid)"
wait

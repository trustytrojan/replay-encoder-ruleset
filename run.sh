#!/bin/bash
set -e

OSU_PATH=~/.local/share/osu

echo "Running on debug build of osu!"

# Build the ruleset
# cd osu.Game.Rulesets.ReplayEncoder
dotnet build -c Debug

# Clean up osu! directory, copy ruleset
rm $OSU_PATH/{logs/*,rulesets/*} || true
cp osu.Game.Rulesets.ReplayEncoder/bin/Debug/net8.0/osu.Game.Rulesets.ReplayEncoder.dll $OSU_PATH/rulesets

# Run osu!
# cd ..
{
	until ls ~/.local/share/osu/logs/*.runtime.log; do sleep 1; done
	tail -f ~/.local/share/osu/logs/*.runtime.log &
	echo $! >/tmp/tail_pid
} &
osu-lazer
kill $(</tmp/tail_pid)
wait

# cd ../../osu # local osu repository, clone it if you don't have it
# git checkout 2026.408.0
# dotnet run --project osu.Desktop -c Debug

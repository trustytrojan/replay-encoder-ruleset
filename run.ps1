$ErrorActionPreference = "Stop"
$OSU_PATH = "$env:AppData/osu"
$OSU_EXE = "$env:LocalAppData\osulazer\current\osu!.exe"

# Build and copy ruleset
dotnet build -c Release
Remove-Item -Path "$OSU_PATH/logs/*", "$OSU_PATH/rulesets/*" -Force -ErrorAction SilentlyContinue
Copy-Item "osu.Game.Rulesets.ReplayEncoder/bin/Release/net8.0/osu.Game.Rulesets.ReplayEncoder.dll" -Destination "$OSU_PATH/rulesets"

# Start game
Start-Process -FilePath $OSU_EXE -NoNewWindow

# Wait for log to exist
while (-not (Test-Path "$OSU_PATH/logs/*.runtime.log")) { Start-Sleep -Milliseconds 500 }

# Follow logs
Get-Content -Path "$OSU_PATH/logs/*.runtime.log" -Wait

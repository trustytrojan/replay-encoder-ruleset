using System;
using HarmonyLib;
using osu.Game.Screens.Play;

namespace osu.Game.Rulesets.ReplayEncoder;

public static class ExtensionMethods
{
	public static void Start(this Player player)
	{
		var gameplayClockContainerProperty = AccessTools.DeclaredProperty(typeof(Player), "GameplayClockContainer")
			?? throw new InvalidOperationException($"Property Player.GameplayClockContainer does not exist");
		(gameplayClockContainerProperty.GetValue(player) as GameplayClockContainer
			?? throw new InvalidOperationException("Player.GameplayClockContainer is null")).Start();
	}

	public static void Stop(this Player player)
	{
		var gameplayClockContainerProperty = AccessTools.DeclaredProperty(typeof(Player), "GameplayClockContainer")
			?? throw new InvalidOperationException($"Property Player.GameplayClockContainer does not exist");
		(gameplayClockContainerProperty.GetValue(player) as GameplayClockContainer
			?? throw new InvalidOperationException("Player.GameplayClockContainer is null")).Stop();
	}
}
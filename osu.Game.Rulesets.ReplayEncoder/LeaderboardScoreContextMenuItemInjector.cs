using System;
using System.Collections.Generic;
using HarmonyLib;
using osu.Framework.Graphics.UserInterface;
using osu.Game.Graphics.UserInterface;
using osu.Game.Online.Leaderboards;
using osu.Game.Scoring;

namespace osu.Game.Rulesets.ReplayEncoder;

[HarmonyPatch(typeof(LeaderboardScore), "ContextMenuItems", MethodType.Getter)]
static class ContextMenuItemsPatch
{
	[HarmonyPostfix]
	static void Postfix(ref MenuItem[] __result, LeaderboardScore __instance)
	{
		// Only add if score has files (same condition as original)  
		if (__instance.Score.Files.Count <= 0)
			return;

		var items = new List<MenuItem>(__result);

		// Find where to insert - after Export but before Delete, or at the end  
		int insertIndex = items.FindIndex(item =>
			item.Text.ToString().Contains("Export"));

		if (insertIndex >= 0)
			insertIndex++;
		else
			insertIndex = items.Count;

		items.Insert(insertIndex, new OsuMenuItem("Render to video",
			MenuItemType.Standard,
			() => Console.WriteLine("Render to video stub")));

		__result = [.. items];
	}
}
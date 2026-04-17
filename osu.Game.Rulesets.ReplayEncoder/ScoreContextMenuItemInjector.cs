using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.UserInterface;
using osu.Game.Graphics.UserInterface;
using osu.Game.Localisation;
using osu.Game.Screens.Select;

namespace osu.Game.Rulesets.ReplayEncoder;

[HarmonyPatch(typeof(BeatmapLeaderboardScore), "osu.Framework.Graphics.Cursor.IHasContextMenu.get_ContextMenuItems")]
[HarmonyPatchCategory("ContextMenuItems")]
static class ContextMenuItemsPatch
{
	public static void Postfix(ref MenuItem[] __result, BeatmapLeaderboardScore __instance)
	{
		// Only add if score has files (same condition as original)  
		if (__instance.Score.Files.Count <= 0)
			return;

		if (__result.Any(item => item.Text.Value == "Render to video"))
			return;

		var items = new List<MenuItem>(__result);

		// Find where to insert - after Export but before Delete, or at the end  
		int insertIndex = items.FindIndex(item =>
			item.Text.Value == SongSelectStrings.WatchReplay);

		if (insertIndex >= 0)
			insertIndex++;
		else
			insertIndex = items.Count;

		items.Insert(insertIndex, new OsuMenuItem("Render to video",
			MenuItemType.Standard,
			() => ReplayEncoderRuleset.replayEncoderDrawable.StartRecording(ReplayEncoderRuleset.Game.ScreenStack)));

		__result = [.. items];
	}
}
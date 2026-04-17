using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using osu.Framework.Graphics.UserInterface;
using osu.Game.Graphics.UserInterface;
using osu.Game.Localisation;
using osu.Game.Scoring;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Screens.Select;

namespace osu.Game.Rulesets.ReplayEncoder;

[HarmonyPatch(typeof(BeatmapLeaderboardScore), "osu.Framework.Graphics.Cursor.IHasContextMenu.get_ContextMenuItems")]
[HarmonyPatchCategory("StartupPatches")]
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
		int insertIndex = items.FindIndex(item => item.Text.Value == SongSelectStrings.WatchReplay);

		if (insertIndex >= 0)
			insertIndex++;
		else
			insertIndex = items.Count;

		items.Insert(insertIndex, new OsuMenuItem("Render to video", MenuItemType.Standard, () => HandleClick(__instance.Score)));

		__result = [.. items];
	}

	static void HandleClick(ScoreInfo score)
	{
		if (!ReplayEncoderRuleset.ReplayEncoder.CheckUserSettings())
			return;

		ReplayEncoderRuleset.Harmony.PatchCategory("RecordingTrigger");
		ReplayEncoderRuleset.Game.PresentScore(score, ScorePresentType.Gameplay);
	}
}

// PlayerLoader.OnEntering() finishes all of the necessary transitions, which
// ReplayPlayerLoader calls. We just need to wait for it to finish.
[HarmonyPatch(typeof(ReplayPlayerLoader), nameof(ReplayPlayerLoader.OnEntering))]
[HarmonyPatchCategory("RecordingTrigger")]
static class ReplayPlayerLoader_OnEntering_Patch
{
	static void Postfix(ReplayPlayerLoader __instance)
	{
		ReplayEncoderRuleset.Harmony.UnpatchCategory("RecordingTrigger");
		ReplayEncoderRuleset.ReplayEncoder.ReceiveReplayPlayerLoader(__instance);
	}
}

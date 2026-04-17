using System;
using System.Collections.Generic;
using System.Linq;
using HarmonyLib;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Screens;
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

		var game = ReplayEncoderRuleset.Game;

		// Single-fire event handler to catch
		void screenPushed(IScreen oldScreen, IScreen newScreen)
		{
			if (newScreen is not ReplayPlayerLoader rpl)
				return;
			Console.WriteLine("screenPushed event handler caught rpl!");
			Console.WriteLine("Now waiting for it to fire OnLoadComplete...");
			rpl.OnLoadComplete += _ =>
			{
				// rpl.IsCurrentScreen();
				Console.WriteLine("rpl.OnLoadComplete fired! Sending it to ReplayEncoderDrawable!");
				ReplayEncoderRuleset.ReplayEncoder.ReceiveReplayPlayerLoader(rpl);
			};
			game.ScreenStack.ScreenPushed -= screenPushed;
		}
		game.ScreenStack.ScreenPushed += screenPushed;

		game.PresentScore(score, ScorePresentType.Gameplay);
	}
}

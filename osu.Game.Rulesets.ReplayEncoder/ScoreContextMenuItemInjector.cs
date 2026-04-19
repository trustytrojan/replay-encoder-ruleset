using System;
using System.Linq;
using HarmonyLib;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Logging;
using osu.Framework.Screens;
using osu.Game.Graphics.UserInterface;
using osu.Game.Localisation;
using osu.Game.Overlays.Toolbar;
using osu.Game.Scoring;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Screens.Ranking;
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

		var items = __result.ToList();

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

[HarmonyPatch(typeof(ReplayPlayerLoader), nameof(ReplayPlayerLoader.OnEntering))]
[HarmonyPatchCategory("RecordingTrigger")]
static class ReplayPlayerLoader_OnEntering_Patch
{
	static void Postfix(ReplayPlayerLoader __instance)
	{
		Console.WriteLine($"ReplayPlayerLoader_OnEntering_Patch: caught ReplayPlayerLoader#{__instance.GetHashCode()}");
		ReplayEncoderRuleset.Harmony.UnpatchCategory("RecordingTrigger");
		ReplayEncoderRuleset.ReplayEncoder.ReceiveReplayPlayerLoader(__instance);
	}
}

[HarmonyPatch(typeof(PlayerLoader), nameof(PlayerLoader.OnSuspending))]
[HarmonyPatchCategory("WhileRecording")]
static class PlayerLoader_OnSuspending_Patch
{
	static void Postfix(PlayerLoader __instance, ScreenTransitionEvent e)
	{
		if (__instance is not ReplayPlayerLoader)
			return;
		if (e.Next is not ReplayPlayer)
			ReplayEncoderRuleset.ReplayEncoder.StopRecording();
	}
}

[HarmonyPatch(typeof(PlayerLoader), nameof(PlayerLoader.OnExiting))]
[HarmonyPatchCategory("WhileRecording")]
static class PlayerLoader_OnExiting_Patch
{
	static void Prefix(PlayerLoader __instance, ScreenExitEvent e)
	{
		if (__instance is not ReplayPlayerLoader)
			return;
		if (e.Destination is not ReplayPlayer)
			ReplayEncoderRuleset.ReplayEncoder.StopRecording();
	}
}

[HarmonyPatch(typeof(ReplayPlayer), nameof(ReplayPlayer.OnSuspending))]
[HarmonyPatchCategory("WhileRecording")]
static class ReplayPlayer_OnSuspending_Patch
{
	static void Prefix(ReplayPlayer __instance, ScreenTransitionEvent e)
	{
		if (!__instance.HasCompleted() || e.Next is not ResultsScreen)
			ReplayEncoderRuleset.ReplayEncoder.StopRecording();
	}
}

[HarmonyPatch(typeof(ReplayPlayer), nameof(ReplayPlayer.OnExiting))]
[HarmonyPatchCategory("WhileRecording")]
static class ReplayPlayer_OnExiting_Patch
{
	static void Prefix(ReplayPlayer __instance, ScreenExitEvent e)
	{
		if (!__instance.HasCompleted() || e.Destination is not ResultsScreen)
			ReplayEncoderRuleset.ReplayEncoder.StopRecording();
	}
}

// Stop recording 4 simulated seconds after showing the advanced statistics of the score in the results screen.
// Override OnEntering within SoloResultsScreen.
[HarmonyPatch(typeof(ResultsScreen), nameof(ResultsScreen.OnEntering))]
[HarmonyPatchCategory("WhileRecording")]
static class ResultsScreen_OnEntering_Patch
{
	// Use a postfix, because all that needs to happen before we run is `base.OnEntering()`.
	static void Postfix(ResultsScreen __instance)
	{
		if (__instance is not SoloResultsScreen srs)
			return;

		if (!ReplayEncoderRuleset.ReplayEncoder.Recording)
			return;

		if (srs.Score == null)
		{
			Logger.Log("wtf?", level: LogLevel.Error);
			return;
		}

		var scorePanelList = AccessTools.Property(typeof(ResultsScreen), "ScorePanelList").GetValue(srs) as ScorePanelList;
		var panel = scorePanelList.GetPanelForScore(srs.Score);

		// one-time event handler
		void callback(PanelState panelState)
		{
			if (panelState == PanelState.Expanded)
				panel.TriggerClick();
			ReplayEncoderRuleset.ReplayEncoder.CaptureInvokeActionIn(ReplayEncoderRuleset.ReplayEncoder.StopRecording, 4_000);
			panel.StateChanged -= callback;
		}
		panel.StateChanged += callback;
	}
}

[HarmonyPatch(typeof(VisibilityContainer), nameof(VisibilityContainer.ToggleVisibility))]
[HarmonyPatchCategory("WhileRecording")]
static class VisibilityContainer_ToggleVisibility_Patch
{
	static bool Prefix(VisibilityContainer __instance)
	{
		// true lets the original method run, false does not.
		// let the original method run if __instance is not a Toolbar.
		return __instance is not Toolbar;
	}
}

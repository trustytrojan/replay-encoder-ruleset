using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Cursor;
using osu.Framework.Graphics.UserInterface;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Graphics.UserInterface;
using osu.Game.Localisation;
using osu.Game.Screens;
using osu.Game.Screens.Play;
using osu.Game.Screens.Select;

namespace osu.Game.Rulesets.ReplayEncoder;

[HarmonyPatch(typeof(ScreenExtensions), nameof(ScreenExtensions.Push))]
[HarmonyPatchCategory("ScreenExtensions_Push")]
static class ScreenExtensionsPushPatch
{
	static void Postfix(IScreen newScreen)
	{
		if (ReplayEncoderRuleset.replayEncoderDrawable.Recording)
		{
			// Unpatch ourself because we started recording
			new Harmony("ScreenExtensionsPushPatch")
				.Unpatch(
					AccessTools.Method(typeof(ScreenExtensions), "Push"),
					HarmonyPatchType.Postfix
				);
			Console.WriteLine("Unpatched ScreenExtensions.Push");
		}
		// Console.WriteLine($"ScreenExtensionsPushPatch postfix called with screen {newScreen}");
		// Console.WriteLine($"Please unpatch this when the RPL was received");
		if (newScreen is not ReplayPlayerLoader rpl)
			return;
		Console.WriteLine("ReplayPlayerLoader caught in ScreenExtensions.Push");
		ReplayEncoderRuleset.replayEncoderDrawable.ReceiveReplayPlayerLoader(rpl);
	}
}

// Just to avoid the log spam.
[HarmonyPatch(typeof(GameplayClockContainer), "StartGameplayClock")]
[HarmonyPatchCategory("WhileRecording")]
static class GameplayClockContainer_StartGameplayClock
{
	static bool Prefix(GameplayClockContainer __instance)
	{
		__instance.GetGameplayClock().Start();
		return false;
	}
}

[HarmonyPatch(typeof(GameplayClockContainer), "StopGameplayClock")]
[HarmonyPatchCategory("WhileRecording")]
static class GameplayClockContainer_StopGameplayClock
{
	static bool Prefix(GameplayClockContainer __instance)
	{
		__instance.GetGameplayClock().Stop();
		return false;
	}
}

[HarmonyPatch(typeof(GameplayClockContainer), "Seek")]
[HarmonyPatchCategory("WhileRecording")]
static class GameplayClockContainer_Seek
{
	static bool Prefix(GameplayClockContainer __instance, double time)
	{
		__instance.GetGameplayClock().Seek(time);
		(AccessTools.Event(typeof(GameplayClockContainer), "OnSeek")
			?? throw new InvalidOperationException("Event GameplayClockContainer.OnSeek not found"))
			.GetRaiseMethod()?.Invoke(__instance, []);
		return false;
	}
}


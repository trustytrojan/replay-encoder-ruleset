using System;
using HarmonyLib;
using osu.Game.Screens.Play;

namespace osu.Game.Rulesets.ReplayEncoder;

// Just to avoid the log spam.
[HarmonyPatch(typeof(GameplayClockContainer), "StartGameplayClock")]
[HarmonyPatchCategory("WhileRecording")]
static class GameplayClockContainer_StartGameplayClock_Patch
{
	static bool Prefix(GameplayClockContainer __instance)
	{
		__instance.GetGameplayClock().Start();
		return false;
	}
}

[HarmonyPatch(typeof(GameplayClockContainer), "StopGameplayClock")]
[HarmonyPatchCategory("WhileRecording")]
static class GameplayClockContainer_StopGameplayClock_Patch
{
	static bool Prefix(GameplayClockContainer __instance)
	{
		__instance.GetGameplayClock().Stop();
		return false;
	}
}

[HarmonyPatch(typeof(GameplayClockContainer), "Seek")]
[HarmonyPatchCategory("WhileRecording")]
static class GameplayClockContainer_Seek_Patch
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

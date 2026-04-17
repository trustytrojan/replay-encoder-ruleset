using HarmonyLib;

namespace osu.Game.Rulesets.ReplayEncoder;

public static class ReplayEncoderMain
{
	public static OsuGame Game;

	public static void RunHarmonyPatches()
	{
		new Harmony("replay-encoder-ruleset").PatchAll();
	}
}

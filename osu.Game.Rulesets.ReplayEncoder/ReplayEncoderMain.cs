using System;
using System.Diagnostics;
using System.Linq;
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Game.Graphics.Containers;

namespace osu.Game.Rulesets.ReplayEncoder;

// Because I'm not writing an actual ruleset, all of ReplayEncoder-specific code will be
// outside of the ruleset template classes. Consider this to be the "main" or "entrypoint" class.
public static class ReplayEncoderMain
{
	public static OsuGame Game { get; private set; }
	static bool injected = false;

	public static void RunHarmonyPatches()
	{
		new Harmony("replay-encoder-ruleset").PatchAll();
	}

	public static void InjectCapturableOsuScreenStack()
	{
		if (injected)
			return;

		// At this point, we have intercepted OsuGame.LoadComplete RIGHT AFTER
		// it assigns ScreenStack and ScreenContainer, according to 2026.408.0 source.

		// Get the ScalingContainer that contains the original OsuScreenStack.
		var gameScreenContainerProperty = AccessTools.DeclaredProperty(typeof(OsuGame), "ScreenContainer")
			?? throw new InvalidOperationException("The declared property OsuGame.ScreenContainer doesn't exist");
		if (gameScreenContainerProperty.GetValue(Game) is not ScalingContainer gameScreenContainer)
			throw new InvalidOperationException("ScreenContainer is null");

		// Set the OsuGame.ScreenStack property to a new CapturableOsuScreenStack.
		// It's `private set`, so we need to set it like this.
		var gameScreenStackProperty = AccessTools.DeclaredProperty(typeof(OsuGame), "ScreenStack")
			?? throw new InvalidOperationException("The declared property OsuGame.ScreenStack doesn't exist");
		var newScreenStack = new CapturableOsuScreenStack { RelativeSizeAxes = Axes.Both };
		gameScreenStackProperty.SetValue(Game, newScreenStack);
		Debug.Assert(Game.ScreenStack == newScreenStack);
		// Now we can simply use `game.ScreenStack` to access our CapturableOsuScreenStack.

		Console.WriteLine("Replaced OsuGame.ScreenStack with a CapturableOsuScreenStack. Now injecting it into OsuGame.ScreenContainer...");

		// Make a copy of the children that we can modify.
		var children = gameScreenContainer.Children.ToList();

		Console.WriteLine("children:");
		foreach (var child in children)
		{
			Console.WriteLine($"  {child}#{child.GetHashCode()}");
		}

		// According to 2026.408.0 source,
		// The 2nd child is the screen stack we need to replace.
		children[1] = Game.ScreenStack;

		Console.WriteLine("children after modification:");
		foreach (var child in children)
		{
			Console.WriteLine($"  {child}#{child.GetHashCode()}");
		}

		// We replace the entire Children list with our new copy.
		// We MUST Clear(false) first because it does NOT dispose the removed children!
		gameScreenContainer.Clear(false);
		gameScreenContainer.Children = children;

		Console.WriteLine("Successfully injected CaptureableOsuScreenStack!");

		injected = true;
	}

	// In order to inject a CapturableOsuScreenStack, we need the OsuGame instance.
	// This is how we grab it.
	[HarmonyPatch(typeof(OsuGame), "load")]
	public class OsuGameLoadCompletePatch
	{
		static void Prefix(OsuGame __instance)
		{
			Game ??= __instance;
		}
	}

	// This line is the perfect place to replace OsuGame.ScreenStack with a CapturableOsuScreenStack:
	// https://github.com/ppy/osu/blob/8c6818e275d0bd369506d86c68c57df7af7163bd/osu.Game/OsuGame.cs#L1146
	// It's right after ScreenContainer is setup, and right before stateful operations are done on ScreenStack.
	// Using Harmony, we detect exactly when this method runs in OsuGame.LoadComplete and perform our injection.
	[HarmonyPatch(typeof(DependencyContainer), nameof(DependencyContainer.Cache), typeof(object))]
	public class OsuGameDependenciesCacheDetector
	{
		static void Prefix()
		{
			var method = new StackFrame(2, false).GetMethod();
			// Console.WriteLine($"Called by {method.DeclaringType.Name}.{method.Name} with __instance={__instance}#{__instance.GetHashCode()} and instance={instance}#{instance.GetHashCode()}");

			// Luckily, DependencyContainer.Cache is only called once.
			// If it was called before this point, we would also need to verify the argument...
			if (method.DeclaringType.Name != "OsuGame")
				return;
			if (method.Name != "LoadComplete")
				return;

			// Console.WriteLine("Detected the right caller!");
			if (Game == null)
				throw new InvalidOperationException("game is null");

			InjectCapturableOsuScreenStack();
		}
	}
}

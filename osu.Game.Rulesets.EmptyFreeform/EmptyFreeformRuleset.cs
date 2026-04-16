// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using AutoMapper.Internal;
using HarmonyLib;
using osu.Framework.Allocation;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Shapes;
using osu.Framework.Graphics.Sprites;
using osu.Framework.Input.Bindings;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Graphics;
using osu.Game.Graphics.Containers;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.EmptyFreeform.Beatmaps;
using osu.Game.Rulesets.EmptyFreeform.Mods;
using osu.Game.Rulesets.EmptyFreeform.UI;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.UI;
using osu.Game.Screens;
using osu.Game.Screens.Footer;
using osuTK;
using osuTK.Graphics;

namespace osu.Game.Rulesets.EmptyFreeform
{
    public partial class EmptyFreeformRuleset : Ruleset
    {
        public override string Description => "a very emptyfreeformruleset ruleset";

        public override DrawableRuleset CreateDrawableRulesetWith(IBeatmap beatmap, IReadOnlyList<Mod> mods = null) =>
            new DrawableEmptyFreeformRuleset(this, beatmap, mods);

        public override IBeatmapConverter CreateBeatmapConverter(IBeatmap beatmap) =>
            new EmptyFreeformBeatmapConverter(beatmap, this);

        public override DifficultyCalculator CreateDifficultyCalculator(IWorkingBeatmap beatmap) =>
            new EmptyFreeformDifficultyCalculator(RulesetInfo, beatmap);

        public override IEnumerable<Mod> GetModsFor(ModType type)
        {
            switch (type)
            {
                case ModType.Automation:
                    return [new EmptyFreeformModAutoplay()];

                default:
                    return [];
            }
        }

        public override string ShortName => "emptyfreeformruleset";

        public override IEnumerable<KeyBinding> GetDefaultKeyBindings(int variant = 0) =>
        [
            new KeyBinding(InputKey.Z, EmptyFreeformAction.Button1),
            new KeyBinding(InputKey.X, EmptyFreeformAction.Button2),
        ];

        public override Drawable CreateIcon() => new Icon(ShortName[0]);

        public partial class Icon : CompositeDrawable
        {
            public Icon(char c)
            {
                InternalChildren =
                [
                    new Circle
                    {
                        Size = new Vector2(20),
                        Colour = Color4.White,
                    },
                    new SpriteText
                    {
                        Anchor = Anchor.Centre,
                        Origin = Anchor.Centre,
                        Text = c.ToString(),
                        Font = OsuFont.Default.With(size: 18)
                    }
                ];
            }
        }

        // Leave this line intact. It will bake the correct version into the ruleset on each build/release.
        public override string RulesetAPIVersionSupported => CURRENT_RULESET_API_VERSION;

        public static Harmony harmony = new Harmony("replay-encoder-ruleset");
        public static OsuGame game;

        public EmptyFreeformRuleset()
        {
            try { harmony.PatchAll(); }
            catch (Exception ex) { Console.Error.WriteLine(ex); throw; }
        }

        [HarmonyPatch(typeof(OsuGame), "load")]
        public class OsuGameLoadCompletePatch
        {
            static void Prefix(OsuGame __instance)
            {
                game ??= __instance;
            }
        }

        [HarmonyPatch(typeof(DependencyContainer), nameof(DependencyContainer.Cache), typeof(object))]
        public class OsuGameDependenciesCacheDetector
        {
            static void Prefix(DependencyContainer __instance, object instance)
            {
                var method = new StackFrame(2, false).GetMethod();
                // Console.WriteLine($"Called by {method.DeclaringType.Name}.{method.Name} with __instance={__instance}#{__instance.GetHashCode()} and instance={instance}#{instance.GetHashCode()}");

                if (method.DeclaringType.Name != "OsuGame")
                    return;
                if (method.Name != "LoadComplete")
                    return;

                // Console.WriteLine("Detected the right caller!");
                if (game == null)
                    throw new InvalidOperationException("game is null");

                InjectCapturableOsuScreenStack();
            }
        }

        static bool injected = false;

        public static void InjectCapturableOsuScreenStack()
        {
            if (injected)
                return;

            // At this point, we have intercepted OsuGame.LoadComplete RIGHT AFTER
            // it assigns ScreenStack and ScreenContainer, according to 2026.408.0 source.

            // Get the ScalingContainer that contains the original OsuScreenStack.
            var gameScreenContainerProperty = AccessTools.DeclaredProperty(typeof(OsuGame), "ScreenContainer")
                ?? throw new InvalidOperationException("The declared property OsuGame.ScreenContainer doesn't exist");
            if (gameScreenContainerProperty.GetValue(game) is not ScalingContainer gameScreenContainer)
                throw new InvalidOperationException("ScreenContainer is null");

            // Set the OsuGame.ScreenStack property to a new CapturableOsuScreenStack.
            // It's `private set`, so we need to set it like this.
            var gameScreenStackProperty = AccessTools.DeclaredProperty(typeof(OsuGame), "ScreenStack")
                ?? throw new InvalidOperationException("The declared property OsuGame.ScreenStack doesn't exist");
            var newScreenStack = new CapturableOsuScreenStack { RelativeSizeAxes = Axes.Both };
            gameScreenStackProperty.SetValue(game, newScreenStack);
            Debug.Assert(game.ScreenStack == newScreenStack);
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
            children[1] = game.ScreenStack;

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
    }
}

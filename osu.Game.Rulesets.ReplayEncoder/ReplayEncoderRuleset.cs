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
using osu.Game.Rulesets.ReplayEncoder.Beatmaps;
using osu.Game.Rulesets.ReplayEncoder.Mods;
using osu.Game.Rulesets.ReplayEncoder.UI;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.UI;
using osu.Game.Screens;
using osu.Game.Screens.Footer;
using osuTK;
using osuTK.Graphics;
using osu.Game.Screens.Select;
using System.Reflection;

namespace osu.Game.Rulesets.ReplayEncoder
{
    public partial class ReplayEncoderRuleset : Ruleset
    {
        public override string Description => "Replay Encoder";

        public override DrawableRuleset CreateDrawableRulesetWith(IBeatmap beatmap, IReadOnlyList<Mod> mods = null) =>
            new DrawableReplayEncoderRuleset(this, beatmap, mods);

        public override IBeatmapConverter CreateBeatmapConverter(IBeatmap beatmap) =>
            new ReplayEncoderBeatmapConverter(beatmap, this);

        public override DifficultyCalculator CreateDifficultyCalculator(IWorkingBeatmap beatmap) =>
            new ReplayEncoderDifficultyCalculator(RulesetInfo, beatmap);

        public override IEnumerable<Mod> GetModsFor(ModType type)
        {
            switch (type)
            {
                case ModType.Automation:
                    return [new ReplayEncoderModAutoplay()];

                default:
                    return [];
            }
        }

        public override string ShortName => "Replay Encoder";

        public override IEnumerable<KeyBinding> GetDefaultKeyBindings(int variant = 0) =>
        [
            new KeyBinding(InputKey.Z, ReplayEncoderAction.Button1),
            new KeyBinding(InputKey.X, ReplayEncoderAction.Button2),
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

            [BackgroundDependencyLoader]
            private void load(OsuGame game)
            {
                Game = game;
                Schedule(() => game.Add(ReplayEncoder = new()));
            }
        }

        // Leave this line intact. It will bake the correct version into the ruleset on each build/release.
        public override string RulesetAPIVersionSupported => CURRENT_RULESET_API_VERSION;

        public static OsuGame Game;
        public static ReplayEncoder ReplayEncoder = new();
        public static Harmony Harmony;

        public ReplayEncoderRuleset()
        {
            Harmony = new Harmony($"{nameof(ReplayEncoderRuleset)}#{GetHashCode()}");
            Harmony.PatchCategory("StartupPatches");
            GLRenderer_ExtractFrameBufferData_Patch.Patch(Harmony);
        }
    }
}

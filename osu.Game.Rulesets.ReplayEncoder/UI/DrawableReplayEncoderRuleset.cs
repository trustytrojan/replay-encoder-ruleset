// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using osu.Framework.Allocation;
using osu.Framework.Input;
using osu.Game.Beatmaps;
using osu.Game.Input.Handlers;
using osu.Game.Replays;
using osu.Game.Rulesets.ReplayEncoder.Objects;
using osu.Game.Rulesets.ReplayEncoder.Objects.Drawables;
using osu.Game.Rulesets.ReplayEncoder.Replays;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Rulesets.UI;

namespace osu.Game.Rulesets.ReplayEncoder.UI
{
    [Cached]
    public partial class DrawableReplayEncoderRuleset : DrawableRuleset<ReplayEncoderHitObject>
    {
        public DrawableReplayEncoderRuleset(ReplayEncoderRuleset ruleset, IBeatmap beatmap, IReadOnlyList<Mod> mods = null)
            : base(ruleset, beatmap, mods)
        {
        }

        protected override Playfield CreatePlayfield() => new ReplayEncoderPlayfield();

        protected override ReplayInputHandler CreateReplayInputHandler(Replay replay) => new ReplayEncoderFramedReplayInputHandler(replay);

        public override DrawableHitObject<ReplayEncoderHitObject> CreateDrawableRepresentation(ReplayEncoderHitObject h) => new DrawableReplayEncoderHitObject(h);

        protected override PassThroughInputManager CreateInputManager() => new ReplayEncoderInputManager(Ruleset?.RulesetInfo);
    }
}

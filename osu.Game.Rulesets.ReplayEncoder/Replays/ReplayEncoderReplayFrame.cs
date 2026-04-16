// Copyright (c) ppy Pty Ltd <contact@ppy.sh>. Licensed under the MIT Licence.
// See the LICENCE file in the repository root for full licence text.

using System.Collections.Generic;
using System.Linq;
using osu.Game.Rulesets.Replays;
using osuTK;

namespace osu.Game.Rulesets.ReplayEncoder.Replays
{
    public class ReplayEncoderReplayFrame : ReplayFrame
    {
        public List<ReplayEncoderAction> Actions = new List<ReplayEncoderAction>();
        public Vector2 Position;

        public ReplayEncoderReplayFrame(ReplayEncoderAction? button = null)
        {
            if (button.HasValue)
                Actions.Add(button.Value);
        }

        public override bool IsEquivalentTo(ReplayFrame other)
            => other is ReplayEncoderReplayFrame freeformFrame && Time == freeformFrame.Time && Position == freeformFrame.Position && Actions.SequenceEqual(freeformFrame.Actions);
    }
}

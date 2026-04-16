using osu.Game.Beatmaps;
using osu.Game.Rulesets.ReplayEncoder.Objects;
using osu.Game.Rulesets.Replays;

namespace osu.Game.Rulesets.ReplayEncoder.Replays
{
    public class ReplayEncoderAutoGenerator : AutoGenerator<ReplayEncoderReplayFrame>
    {
        public new Beatmap<ReplayEncoderHitObject> Beatmap => (Beatmap<ReplayEncoderHitObject>)base.Beatmap;

        public ReplayEncoderAutoGenerator(IBeatmap beatmap)
            : base(beatmap)
        {
        }

        protected override void GenerateFrames()
        {
            Frames.Add(new ReplayEncoderReplayFrame());

            foreach (ReplayEncoderHitObject hitObject in Beatmap.HitObjects)
            {
                Frames.Add(new ReplayEncoderReplayFrame
                {
                    Time = hitObject.StartTime,
                    Position = hitObject.Position,
                    // todo: add required inputs and extra frames.
                });
            }
        }
    }
}

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using ManagedBass.Mix;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Screens;
using osu.Framework.Timing;
using osu.Game.Screens.Play;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Game.Rulesets.ReplayEncoder;

public partial class ReplayEncoderDrawable : CompositeDrawable
{
	public class MyClock(IClock source) : FramedClock(source, false), IAdjustableClock
	{
		double IAdjustableClock.Rate { get => Rate; set => throw new NotImplementedException(); }
		public void Reset() { }
		public void ResetSpeedAdjustments() { }
		public bool Seek(double position) => true;
		public void Start() { }
		public void Stop() { }
	}

	private double replayTime = 0;
	private bool replayTimeStarted = false;
	private ReplayPlayer player = null;

	protected readonly ManualClock ScreenStackTimeSource = new()
	{
		CurrentTime = 0,
		IsRunning = true,
		Rate = 1,
	};
	// public IFrameBasedClock ScreenStackClock = null;
	private FFmpegCliProcess ffmpeg;
	public bool Recording = false;
	private const int fps = 60;
	private const double frame_time_ms = 1000.0 / fps;
	// protected CapturableOsuScreenStack CaptureScreenStack;
	private ScreenStackScreenshotter screenshotter;
	private IFrameBasedClock originalStackClock;
	private double simulatedTimeToInvokeAction;
	private Action actionWhenSimulatedTimeReached;

	// Do something after `timeFromNowMs` simulated time ms.
	// This is kept track by ScreenStackClock.CurrentTime in Update().
	public void CaptureInvokeActionIn(Action action, double timeFromNowMs)
	{
		simulatedTimeToInvokeAction = ScreenStackTimeSource.CurrentTime + timeFromNowMs;
		actionWhenSimulatedTimeReached = action;
	}

	// Audio recording stuff
	private const int samplerate = 44100;
	private const int channels = 2;
	private Resolution resolution;
	private int myMixerHandle;
	private byte[] audioBuf = null;

	public class StatisticTimer
	{
		private readonly Stopwatch stopwatch = new();
		private uint count = 0;
		private double sum = 0;
		public double Average => sum / count;
		public void Begin() => stopwatch.Restart();
		public void End()
		{
			sum += stopwatch.Elapsed.TotalMilliseconds;
			++count;
		}
	}

	// Lock that waits for the Image before advancing replay time
	private bool currentlyCapturing = false;
	private readonly StatisticTimer extractTime = new(), captureTime = new(), audioTime = new(), updateChildrenTime = new();

	// We just count with a double, because Player.GameplayClockContainer actually controls
	// the beatmap's Track... so all we need to do is Seek() to our simulated time!
	protected void StartReplayTime(ReplayPlayer player)
	{
		replayTimeStarted = true;
		this.player = player;
	}

	public void StartRecording(ScreenStack target)
	{
		// This allows for a 10-fold speed increase over image.CreateReadOnlyPixelSpan()!
		// See FFmpegCliProcess.WriteFrame().
		SixLabors.ImageSharp.Configuration.Default.PreferContiguousImageBuffers = true;

		replayTimeStarted = false;
		replayTime = 0;
		ScreenStackTimeSource.CurrentTime = 0;
		Recording = true;

		ffmpeg?.Dispose();
		ffmpeg = null;
		currentlyCapturing = false;

		// this.CaptureScreenStack = stack;
		originalStackClock = target.Clock;
		target.Clock = new MyClock(ScreenStackTimeSource);

		// Create our own mixer to combine TrackMixer and SampleMixer
		myMixerHandle = BassMix.CreateMixerStream(samplerate, channels, BassFlags.MixerNonStop | BassFlags.Decode);
		if (myMixerHandle == 0)
			throw new InvalidOperationException($"CreateMixerStream: ${Bass.LastError}");

		// Just get the resolution since we're letting BASS choose it
		if (Bass.ChannelGetInfo(myMixerHandle, out ChannelInfo info))
			resolution = info.Resolution;
		else
			throw new InvalidOperationException($"BASS error: {Bass.LastError}");

		// var audioManager = ReplayEncoderRuleset.Game.Audio;
		// int trackMixerHandle = audioManager.TrackMixer.GetBassHandle();
		// int sampleMixerHandle = audioManager.SampleMixer.GetBassHandle();

		// // Remove mixers from global mixer
		// if (!BassMix.MixerRemoveChannel(trackMixerHandle))
		// 	throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");
		// if (!BassMix.MixerRemoveChannel(sampleMixerHandle))
		// 	throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");

		// // Add mixers to my mixer
		// if (!BassMix.MixerAddChannel(myMixerHandle, trackMixerHandle, 0))
		// 	throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");
		// if (!BassMix.MixerAddChannel(myMixerHandle, sampleMixerHandle, 0))
		// 	throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");

		Task.Run(() =>
		{
			while (Recording)
			{
				Thread.Sleep(1_000);
				Logger.Log($"Average times: Extract = {extractTime.Average}ms; Capture = {captureTime.Average}ms; Audio = {audioTime.Average}ms; Update children = {updateChildrenTime.Average}ms");
			}
		});

		screenshotter = new()
		{
			Target = target,
			OnImageReceived = OnImageReceived,
			OnExtractBegin = extractTime.Begin,
			OnExtractEnd = extractTime.End
		};
		AddInternal(screenshotter);

		Logger.Log("Started rendering replay.", level: LogLevel.Important);
	}

	public void StopRecording()
	{
		if (!Recording)
			return;
		Recording = false;
		player = null;
		replayTimeStarted = false;
		screenshotter.Target.Clock = originalStackClock;
		// ScreenStackClock = null;
		RemoveInternal(screenshotter, true);
		ffmpeg?.Dispose();
		ffmpeg = null;

		// var audioManager = ReplayEncoderRuleset.Game.Audio;
		// int trackMixerHandle = audioManager.TrackMixer.GetBassHandle();
		// int sampleMixerHandle = audioManager.SampleMixer.GetBassHandle();
		// int globalMixerHandle = audioManager.GetGlobalMixerHandle();

		// // Remove mixers from my mixer
		// if (!BassMix.MixerRemoveChannel(trackMixerHandle))
		// 	throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");
		// if (!BassMix.MixerRemoveChannel(sampleMixerHandle))
		// 	throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");

		// // Add mixers back to global mixer
		// if (!BassMix.MixerAddChannel(globalMixerHandle, trackMixerHandle, 0))
		// 	throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");
		// if (!BassMix.MixerAddChannel(globalMixerHandle, sampleMixerHandle, 0))
		// 	throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");

		Logger.Log("Stopped rendering replay.", level: LogLevel.Important);
	}

	protected override void Update()
	{
		base.Update();

		if (!Recording || currentlyCapturing)
		{
			// Console.WriteLine("not recording");
			return;
		}
		// Console.WriteLine("recording");

		// TODO: ffmpeg can be inited here using ScheduleAfterChildren()

		if (screenshotter?.Target.Clock != null)
		{
			if (ScreenStackTimeSource.CurrentTime >= simulatedTimeToInvokeAction)
				actionWhenSimulatedTimeReached?.Invoke();
			ScreenStackTimeSource.CurrentTime += frame_time_ms;
		}

		// The player was started at the end of OnImageReceived(),
		// giving BASS a lot of time to render audio, so we can record
		// once children have updated with the new clock state.
		// // ScheduleAfterChildren(() =>
		// // {
		// // updateChildrenTime.End();
		// recordAudio();

		// // Don't stop the music if the replay finished.
		// if (replayTimeStarted && !player.HasCompleted())
		// {
		// 	// Stop BEFORE seeking so it STAYS stopped as the image is taken.
		// 	player.Stop();
		player?.Seek(replayTime);
		// 	// We keep the clock stopped as to not take unpredictable images of the screen.

		// 	// Increment now before we forget.
		// 	replayTime += frame_time_ms;
		// }
		// // });

		screenshotter.RequestCapture();
		currentlyCapturing = true;
		captureTime.Begin();
		// updateChildrenTime.Begin();
	}

	private void recordAudio()
	{
		if (ffmpeg == null)
			return;

		if (audioBuf == null)
		{
			int afpvf = samplerate / fps;
			int samples = afpvf * channels;
			audioBuf = new byte[samples * resolution.ByteSize()];
		}

		audioTime.Begin();
		int bytesRead = Bass.ChannelGetData(myMixerHandle, audioBuf, audioBuf.Length);
		if (bytesRead == -1)
			throw new InvalidOperationException($"BASS error: {Bass.LastError}");

		if (!ffmpeg.WriteAudio(audioBuf.AsSpan().Slice(0, bytesRead)))
			Logger.Log("Dropped audio packet, is ffmpeg too slow?", level: LogLevel.Error);
		audioTime.End();
	}

	protected void OnImageReceived(Image<Rgba32> image)
	{
		if (!currentlyCapturing)
			return;
		currentlyCapturing = false;
		captureTime.End();

		if (!Recording || image == null)
			return;

		// We wait for the first image because by this point all
		// transforms should have been finished by StartRecording().
		if (ffmpeg == null)
		{
			ffmpeg = new FFmpegCliProcess
			{
				OutputFilePath = "out.mp4",
				VideoSize = new() { X = image.Width, Y = image.Height },
				Framerate = fps,
				Samplerate = samplerate,
				SampleFormat = resolution.ToFfmpegSmpFmt(),
				Channels = channels
			};
			ffmpeg.Start();
		}

		// Don't use `using` as the FFmpegCliProcess class now queues images for a separate thread to handle.
		// This was needed to deal with Windows' very small anonymous pipe buffers causing deadlocks.
		if (!ffmpeg.WriteFrame(image))
			Logger.Log("Dropped video frame, is ffmpeg too slow?", level: LogLevel.Error);

		if (replayTimeStarted)
		{
			// Start playing so that on the next update we will have audio to record.
			player.Start();
		}
	}
}

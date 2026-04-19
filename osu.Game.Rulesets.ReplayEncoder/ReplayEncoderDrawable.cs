using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using ManagedBass;
using ManagedBass.Mix;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Timing;
using osu.Game.Overlays.Notifications;
using osu.Game.Screens.Play;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Game.Rulesets.ReplayEncoder;

public partial class ReplayEncoder : CompositeDrawable
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
	private GameplayClockContainer playerClock;

	protected readonly ManualClock ScreenStackTimeSource = new()
	{
		CurrentTime = 0,
		IsRunning = true,
		Rate = 1,
	};
	// public IFrameBasedClock ScreenStackClock = null;
	private FFmpegCliProcess ffmpeg;
	public bool Recording { get; private set; } = false;
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

	[Resolved]
	private OsuGame Game { get; set; }

	[Resolved]
	private FrameworkConfigManager FrameworkConfig { get; set; }

	public bool CheckUserSettings()
	{
		// Programatically changing the UI is just impossible...
		if (Game.Toolbar.State.Value == Visibility.Visible)
		{
			Game.PostNotification(new SimpleErrorNotification()
			{
				Text = "Toolbar must be hidden first!"
			});
			return false;
		}

		// I refuse to deal with this...
		if (FrameworkConfig.Get<ExecutionMode>(FrameworkSetting.ExecutionMode) == ExecutionMode.MultiThreaded)
		{
			Game.PostNotification(new SimpleErrorNotification()
			{
				Text = "Set threading mode to single-threaded first!"
			});
			return false;
		}

		return true;
	}

	public void ReceiveReplayPlayerLoader(ReplayPlayerLoader rpl)
	{
		if (Recording)
			return;
		StartRecording(Game.ScreenStack);

		// Only set the replay clock when the ReplayPlayer has loaded
		Action waitForNonNullPlayerThenStart = null;
		Schedule(waitForNonNullPlayerThenStart = () =>
		{
			var player = rpl.CurrentPlayer;
			if (player == null || !player.IsCurrentScreen())
				Schedule(waitForNonNullPlayerThenStart);
			else
				StartReplayTime((ReplayPlayer)player);
		});
	}

	// We just count with a double, because Player.GameplayClockContainer actually controls
	// the beatmap's Track... so all we need to do is Seek() to our simulated time!
	protected void StartReplayTime(ReplayPlayer player)
	{
		replayTimeStarted = true;
		this.player = player;
		playerClock = player.GetGameplayClockContainer();

		// This saves 1-2ms draw time because ReplayPlayerSettings forces itself to be redrawn all the time 🤦‍♂️
		playerClock.Remove(player.ReplayOverlay, true);

		// Disable the log spam of GameplayClockContainer operations
		ReplayEncoderRuleset.Harmony.PatchCategory("WhileRecording");
	}

	public void StartRecording(ScreenStack target)
	{
		// This allows for a 10-fold speed increase over image.CreateReadOnlyPixelSpan()!
		// See FFmpegCliProcess.WriteFrame().
		SixLabors.ImageSharp.Configuration.Default.PreferContiguousImageBuffers = true;

		replayTimeStarted = false;
		replayTime = 0;
		Recording = true;

		ffmpeg?.Dispose();
		ffmpeg = null;
		currentlyCapturing = false;

		// this.CaptureScreenStack = stack;
		originalStackClock = target.Clock;
		ScreenStackTimeSource.CurrentTime = target.Clock.CurrentTime; // this is the bug killer...
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

		var audioManager = ReplayEncoderRuleset.Game.Audio;
		int trackMixerHandle = audioManager.TrackMixer.GetBassHandle();
		int sampleMixerHandle = audioManager.SampleMixer.GetBassHandle();

		// Remove mixers from global mixer
		if (!BassMix.MixerRemoveChannel(trackMixerHandle))
			throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");
		if (!BassMix.MixerRemoveChannel(sampleMixerHandle))
			throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");

		// Add mixers to my mixer
		if (!BassMix.MixerAddChannel(myMixerHandle, trackMixerHandle, 0))
			throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");
		if (!BassMix.MixerAddChannel(myMixerHandle, sampleMixerHandle, 0))
			throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");

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
		// Game.Add(screenshotter);
		AddInternal(screenshotter);

		// Game.OnUpdate += Update;

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
		// Game.Remove(screenshotter, true);
		ffmpeg?.Dispose();
		ffmpeg = null;

		// Game.OnUpdate -= Update;

		ReplayEncoderRuleset.Harmony.UnpatchCategory("WhileRecording");

		var audioManager = ReplayEncoderRuleset.Game.Audio;
		int trackMixerHandle = audioManager.TrackMixer.GetBassHandle();
		int sampleMixerHandle = audioManager.SampleMixer.GetBassHandle();
		int globalMixerHandle = audioManager.GetGlobalMixerHandle().Value
			?? throw new InvalidOperationException("Global mixer handle is null");

		// Remove mixers from my mixer
		if (!BassMix.MixerRemoveChannel(trackMixerHandle))
			throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");
		if (!BassMix.MixerRemoveChannel(sampleMixerHandle))
			throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");

		// Add mixers back to global mixer
		if (!BassMix.MixerAddChannel(globalMixerHandle, trackMixerHandle, 0))
			throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");
		if (!BassMix.MixerAddChannel(globalMixerHandle, sampleMixerHandle, 0))
			throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");

		Logger.Log("Stopped rendering replay.", level: LogLevel.Important);
	}

	protected override void Update()
	{
		// No need to call base.Update, because there's nothing to update up there.

		if (!Recording || currentlyCapturing)
			return;

		// TODO: ffmpeg can be inited here using ScheduleAfterChildren()

		// It literally doesn't make sense for the screenshotter or its target's clock to be null at this point.
		Debug.Assert(screenshotter?.Target.Clock != null);

		if (actionWhenSimulatedTimeReached != null && ScreenStackTimeSource.CurrentTime >= simulatedTimeToInvokeAction)
			actionWhenSimulatedTimeReached();
		ScreenStackTimeSource.CurrentTime += frame_time_ms;

		// The player was started at the end of OnImageReceived(),
		// giving BASS a lot of time to render audio, so we can record
		// once children have updated with the new clock state.
		ScheduleAfterChildren(() =>
		{
			updateChildrenTime.End();
			recordAudio();

			// Don't stop the music if the replay finished.
			// This keeps it playing when moving to the results screen.
			if (replayTimeStarted && !player.HasCompleted())
			{
				// Stop BEFORE seeking so it STAYS stopped as the image is taken.
				playerClock?.Stop();
				playerClock?.Seek(replayTime);
				// We keep the clock stopped as to not take unpredictable images of the screen.

				// Increment now before we forget.
				replayTime += frame_time_ms;
			}
		});

		screenshotter.RequestCapture();
		currentlyCapturing = true;
		captureTime.Begin();
		updateChildrenTime.Begin();
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

		if (!ffmpeg.WriteAudio(audioBuf.AsSpan()[..bytesRead]))
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
			playerClock.Start();
		}
	}
}

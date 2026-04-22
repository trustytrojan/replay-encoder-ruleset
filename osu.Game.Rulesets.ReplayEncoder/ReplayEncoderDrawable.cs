using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using ManagedBass;
using ManagedBass.Mix;
using osu.Framework.Allocation;
using osu.Framework.Audio;
using osu.Framework.Configuration;
using osu.Framework.Graphics.Containers;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Screens;
using osu.Framework.Timing;
using osu.Game.Extensions;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Game.Rulesets.ReplayEncoder;

// It's not at all required to be in the game's scene graph, but the dependency caching system is convenient.
// And we might as well be a CompositeDrawable to own the screenshotter.
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
	private FFmpegCliProcess ffmpeg;
	public bool Recording { get; private set; } = false;
	private const int fps = 60;
	private ScreenStackScreenshotter screenshotter;
	private ThrottledFrameClock originalStackClock;
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
	public OsuGame Game { get; private set; }

	[Resolved]
	public FrameworkConfigManager FrameworkConfig { get; private set; }

	// TODO: use this to play music after returning to SoloSongSelect after SoloResultsScreen
	[Resolved]
	public MusicController Music { get; private set; }

	private ExecutionMode originalExecutionMode;

	public void PrepareRecord()
	{
		originalExecutionMode = FrameworkConfig.Get<ExecutionMode>(FrameworkSetting.ExecutionMode);
		FrameworkConfig.SetValue(FrameworkSetting.ExecutionMode, ExecutionMode.SingleThread);
	}

	public bool CanRecord()
	{
		void postErrorNotification(string text)
		{
			Game.PostNotification(new SimpleErrorNotification() { Text = $"Replay Encoder: {text}" });
		}

		// Programatically changing the UI is just impossible...
		if (Game.Toolbar.State.Value == Visibility.Visible)
		{
			postErrorNotification("Toolbar must be hidden.");
			return false;
		}

		// I refuse to deal with this...
		if (FrameworkConfig.Get<ExecutionMode>(FrameworkSetting.ExecutionMode) == ExecutionMode.MultiThreaded)
		{
			postErrorNotification("Threading mode must be single-threaded.");
			return false;
		}

		try
		{
			if (!FFmpegCliProcess.TestFfmpegArguments("-version"))
			{
				postErrorNotification("Running `ffmpeg -version` failed.");
				return false;
			}
		}
		catch (Win32Exception ex)
		{
			if (ex.NativeErrorCode != 2) // "File not found" on both Windows & Unix
				throw;
			postErrorNotification("System couldn't find ffmpeg. Please install it.");
			return false;
		}

		return true;
	}

	private ScoreInfo score;

	public void ReceiveReplayPlayerLoader(ReplayPlayerLoader rpl)
	{
		if (Recording)
			return;
		StartRecording(Game.ScreenStack);
		score = rpl.Score;

		// Only set the replay clock when the ReplayPlayer has loaded
		Action waitForNonNullPlayerThenStart = null;
		Schedule(waitForNonNullPlayerThenStart = () =>
		{
			var player = rpl.CurrentPlayer;

			if (player == null)
			{
				Schedule(waitForNonNullPlayerThenStart);
				return;
			}

			// THIS prevents the "Clock failure" exception because we let Player.StartGameplay
			// be the one that starts the clock. Then we can take over.
			player.OnGameplayStarted += () => StartReplayTime((ReplayPlayer)player);
		});
	}

	private IEnumerable<IApplicableToRate> rateMods;

	// We just count with a double, because Player.GameplayClockContainer actually controls
	// the beatmap's Track... so all we need to do is Seek() to our simulated time!
	protected void StartReplayTime(ReplayPlayer player)
	{
		replayTimeStarted = true;
		this.player = player;
		playerClock = player.GetGameplayClockContainer();

		rateMods = player.GameplayState.Mods.OfType<IApplicableToRate>();

		// This saves 1-2ms draw time because ReplayPlayerSettings forces itself to be redrawn all the time 🤦‍♂️
		playerClock.Remove(player.ReplayOverlay, true);
	}

	private double CalcFrameTime()
	{
		var currentTime = playerClock.CurrentTime;
		var frameTime = 1000.0 / fps;
		foreach (var mod in rateMods)
			frameTime *= mod.ApplyToRate(currentTime);
		return frameTime;
	}

	public void StartRecording(ScreenStack target)
	{
		// Disable the log spam of GameplayClockContainer operations
		ReplayEncoderRuleset.Harmony.PatchCategory("WhileRecording");

		// This allows for a 10-fold speed increase over image.CreateReadOnlyPixelSpan()!
		// See FFmpegCliProcess.WriteFrame().
		SixLabors.ImageSharp.Configuration.Default.PreferContiguousImageBuffers = true;

		replayTimeStarted = false;
		replayTime = 0;
		Recording = true;

		ffmpeg?.Dispose();
		ffmpeg = null;
		currentlyCapturing = false;

		originalStackClock = (ThrottledFrameClock)target.Clock;
		// This kills the bug where we were waiting on a 0.01 alpha SongSelectMenu to fully suspend.
		ScreenStackTimeSource.CurrentTime = target.Clock.CurrentTime;
		target.Clock = new MyClock(ScreenStackTimeSource);

		// Create our own mixer to combine TrackMixer and SampleMixer
		myMixerHandle = BassMix.CreateMixerStream(samplerate, channels, BassFlags.MixerNonStop | BassFlags.Decode);
		if (myMixerHandle == 0)
			throw new InvalidOperationException($"CreateMixerStream: ${Bass.LastError}");

		// Just get the resolution since we're letting BASS choose it
		if (Bass.ChannelGetInfo(myMixerHandle, out ChannelInfo info))
			resolution = info.Resolution;
		else
			throw new InvalidOperationException($"ChannelGetInfo: {Bass.LastError}");

		// Handle the mixers based on whether the WASAPI setting is enabled.
		if (Game.Audio.UseExperimentalWasapi.Value)
		{
			int trackMixerHandle = Game.Audio.TrackMixer.GetBassHandle();
			int sampleMixerHandle = Game.Audio.SampleMixer.GetBassHandle();

			// The track & sample mixers are already decode mixers attached to the global mixer.
			// Remove them from the global mixer then add them to our mixer.

			if (!BassMix.MixerRemoveChannel(trackMixerHandle))
				throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");
			if (!BassMix.MixerRemoveChannel(sampleMixerHandle))
				throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");

			if (!BassMix.MixerAddChannel(myMixerHandle, trackMixerHandle, 0))
				throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");
			if (!BassMix.MixerAddChannel(myMixerHandle, sampleMixerHandle, 0))
				throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");
		}
		else
		{
			// We need to recreate the track & sample mixers as decode mixers,
			// but this only happens when the global mixer handle is non-null.

			// Pretend that the global mixer exists by setting it to our mixer!
			Game.Audio.GetGlobalMixerHandle().Value = myMixerHandle;

			// This will recreate the track & sample mixers as decode mixers, and then add them to our mixer.
			ReplayEncoderRuleset.Harmony.PatchCategory("FakeGlobalMixerHandle");
			AccessTools.Method(typeof(AudioManager), "initCurrentDevice").Invoke(Game.Audio, []);
			ReplayEncoderRuleset.Harmony.UnpatchCategory("FakeGlobalMixerHandle");
		}

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

		// This fixes the "slow motion" you see when going back to song select after recording ends.
		// The ThrottledFrameClock sitting in memory was still counting... which caused it's
		// accumulated sleep time/error to rise way too high... and we used to just shove it back into the ScreenStack,
		// causing the entire scene graph to have throttled updates...
		// The solution? Throw it out and make a new one.
		var tfc = AccessTools.CreateInstance<ThrottledFrameClock>();

		// This should make the right-click context menu of the score you rendered hide.
		// Before, the inner StopwatchClock was starting at 0, so certain parts of the ScreenStack were "stuck in time".
		// Another user reported the osu logo being stuck in the center of the screen.
		(tfc.Source as StopwatchClock).Seek(ScreenStackTimeSource.CurrentTime);

		tfc.MaximumUpdateHz = originalStackClock.MaximumUpdateHz;
		tfc.Throttling = originalStackClock.Throttling;
		originalStackClock = null;
		screenshotter.Target.Clock = tfc;

		RemoveInternal(screenshotter, true);
		ffmpeg?.Dispose();
		ffmpeg = null;
		score = null;

		FrameworkConfig.SetValue(FrameworkSetting.ExecutionMode, originalExecutionMode);

		// Reverse the operations we did in StartRecording.
		if (Game.Audio.UseExperimentalWasapi.Value)
		{
			int trackMixerHandle = Game.Audio.TrackMixer.GetBassHandle();
			int sampleMixerHandle = Game.Audio.SampleMixer.GetBassHandle();
			int globalMixerHandle = Game.Audio.GetGlobalMixerHandle().Value
				?? throw new InvalidOperationException("WASAPI is enabled, but global mixer handle is null");

			if (!BassMix.MixerRemoveChannel(trackMixerHandle))
				throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");
			if (!BassMix.MixerRemoveChannel(sampleMixerHandle))
				throw new InvalidOperationException($"MixerRemoveChannel: ${Bass.LastError}");

			if (!BassMix.MixerAddChannel(globalMixerHandle, trackMixerHandle, 0))
				throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");
			if (!BassMix.MixerAddChannel(globalMixerHandle, sampleMixerHandle, 0))
				throw new InvalidOperationException($"MixerAddChannel: ${Bass.LastError}");
		}
		else
		{
			Game.Audio.GetGlobalMixerHandle().Value = null;
			AccessTools.Method(typeof(AudioManager), "initCurrentDevice").Invoke(Game.Audio, []);
		}

		if (myMixerHandle != 0)
		{
			Bass.StreamFree(myMixerHandle);
			myMixerHandle = 0;
		}

		ReplayEncoderRuleset.Harmony.UnpatchCategory("WhileRecording");
		Logger.Log("Stopped rendering replay.", level: LogLevel.Important);
	}

	protected override void Update()
	{
		// No need to call base.Update, because there's nothing to update up there.

		if (!Recording || currentlyCapturing)
			return;

		if (actionWhenSimulatedTimeReached != null && ScreenStackTimeSource.CurrentTime >= simulatedTimeToInvokeAction)
			actionWhenSimulatedTimeReached();

		// The UI should always be advanced at the video framerate.
		ScreenStackTimeSource.CurrentTime += 1000.0 / fps;

		// The player was started at the end of OnImageReceived(),
		// giving BASS a lot of time to render audio, so we can record
		// once children have updated with the new clock state.
		ScheduleAfterChildren(() =>
		{
			updateChildrenTime.End();
			RecordAudio();

			// Don't stop the music if the replay finished.
			// This keeps it playing when moving to the results screen.
			if (replayTimeStarted && !player.HasCompleted())
			{
				// Stop BEFORE seeking so it STAYS stopped as the image is taken.
				playerClock?.Stop();
				playerClock?.Seek(replayTime);
				// We keep the clock stopped as to not take unpredictable images of the screen.

				// Increment now before we forget.
				replayTime += CalcFrameTime();
			}
		});

		screenshotter.RequestCapture();
		currentlyCapturing = true;
		captureTime.Begin();
		updateChildrenTime.Begin();
	}

	private void RecordAudio()
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
			var exportPath = Game.GetStorage().GetExportStorage().GetFullPath(".");

			// Taken from LegacyScoreExporter.GetFilename
			var filename = $"{score.GetDisplayString()} ({score.Date.LocalDateTime:yyyy-MM-dd_HH-mm}).mp4".GetValidFilename();

			ffmpeg = new FFmpegCliProcess
			{
				OutputFilePath = $"{exportPath}/{filename}",
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

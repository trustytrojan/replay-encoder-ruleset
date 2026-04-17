using System;
using HarmonyLib;
using ManagedBass;
using osu.Framework.Audio;
using osu.Framework.Audio.Mixing;
using osu.Framework.Bindables;
using osu.Game.Beatmaps;
using osu.Game.Overlays;
using osu.Game.Overlays.Notifications;
using osu.Game.Rulesets.Scoring;
using osu.Game.Screens.Play;

namespace osu.Game.Rulesets.ReplayEncoder;

public static class ExtensionMethods
{
	public static FramedBeatmapClock GetGameplayClock(this GameplayClockContainer gcc) =>
		AccessTools.FieldRefAccess<GameplayClockContainer, FramedBeatmapClock>(gcc, "GameplayClock");

	public static GameplayClockContainer GetGameplayClockContainer(this Player player) =>
		AccessTools.Property(typeof(Player), "GameplayClockContainer").GetValue(player) as GameplayClockContainer;
	// typeof(Player).GetProperty("GameplayClockContainer").GetValue(player) as GameplayClockContainer;

	public static void Start(this Player player) =>
		player.GetGameplayClockContainer().Start();

	public static void Stop(this Player player) =>
		player.GetGameplayClockContainer().Stop();

	public static ScoreProcessor GetScoreProcessor(this Player player) =>
		AccessTools.Property(typeof(Player), "ScoreProcessor").GetValue(player) as ScoreProcessor;
	// typeof(Player).GetProperty("ScoreProcessor").GetValue(player) as ScoreProcessor;

	public static bool HasCompleted(this Player player) =>
		player.GetScoreProcessor().HasCompleted.Value;

	public static int GetGlobalMixerHandle(this AudioManager audio) =>
		(int)(typeof(AudioManager).GetField("GlobalMixerHandle").GetValue(audio) as IBindable<int?>).Value;

	public static int GetBassHandle(this AudioMixer mixer) =>
		(int)typeof(AudioMixer).Assembly
			.GetType("osu.Framework.Audio.Mixing.Bass.BassAudioMixer")
			.GetProperty("Handle")
			.GetValue(mixer);

	public static string ToFfmpegSmpFmt(this Resolution r) =>
		r switch
		{
			Resolution.Float => "f32le",
			Resolution.Short => "s16le",
			Resolution.Byte => "u8",
			_ => throw new ArgumentException($"Resolution {r} invalid"),
		};

	public static int ByteSize(this Resolution r) =>
		r switch
		{
			Resolution.Float => 4,
			Resolution.Short => 2,
			Resolution.Byte => 1,
			_ => throw new ArgumentException($"Resolution {r} invalid"),
		};

	public static NotificationOverlay GetNotificationOverlay(this OsuGame game) =>
		AccessTools.FieldRefAccess<OsuGame, NotificationOverlay>(game, "Notifications");

	public static void PostNotification(this OsuGame game, Notification notification) =>
		game.GetNotificationOverlay().Post(notification);
}
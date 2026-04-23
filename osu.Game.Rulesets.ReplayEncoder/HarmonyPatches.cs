using System;
using HarmonyLib;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Logging;
using osu.Framework.Threading;
using osu.Game.Beatmaps;
using osu.Game.Screens.Play;
using osuTK.Graphics.ES30;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;

namespace osu.Game.Rulesets.ReplayEncoder;

// Just to avoid the log spam.
[HarmonyPatch(typeof(GameplayClockContainer), "StartGameplayClock")]
[HarmonyPatchCategory("WhileRecording")]
static class GameplayClockContainer_StartGameplayClock_Patch
{
	// Cache fields for speed, since there's only ever one ReplayPlayer recording at a time
	static GameplayClockContainer lastInstance;
	static FramedBeatmapClock lastUnderlyingClock;

	static bool Prefix(GameplayClockContainer __instance)
	{
		if (__instance != lastInstance)
		{
			Logger.Log("GameplayClockContainer_StartGameplayClock_Patch: new instance");
			lastUnderlyingClock = __instance.GetGameplayClock();
			lastInstance = __instance;
		}

		lastUnderlyingClock.Start();
		return false;
	}
}

[HarmonyPatch(typeof(GameplayClockContainer), "StopGameplayClock")]
[HarmonyPatchCategory("WhileRecording")]
static class GameplayClockContainer_StopGameplayClock_Patch
{
	// Cache fields for speed, since there's only ever one ReplayPlayer recording at a time
	static GameplayClockContainer lastInstance;
	static FramedBeatmapClock lastUnderlyingClock;

	static bool Prefix(GameplayClockContainer __instance)
	{
		if (__instance != lastInstance)
		{
			Logger.Log("GameplayClockContainer_StopGameplayClock_Patch: new instance");
			lastUnderlyingClock = __instance.GetGameplayClock();
			lastInstance = __instance;
		}

		lastUnderlyingClock.Stop();
		return false;
	}
}

[HarmonyPatch(typeof(GameplayClockContainer), "Seek")]
[HarmonyPatchCategory("WhileRecording")]
static class GameplayClockContainer_Seek_Patch
{
	// Cache fields for speed, since there's only ever one ReplayPlayer recording at a time
	static GameplayClockContainer lastInstance;
	static FramedBeatmapClock lastUnderlyingClock;

	// In 2026.408.0 source, GameplayClockContainer.OnSeek never had methods added to it,
	// so don't bother calling it here.
	static bool Prefix(GameplayClockContainer __instance, double time)
	{
		if (__instance != lastInstance)
		{
			Logger.Log("GameplayClockContainer_Seek_Patch: new instance");
			lastUnderlyingClock = __instance.GetGameplayClock();
			lastInstance = __instance;
		}

		lastUnderlyingClock.Seek(time);
		return false;
	}
}

// This needs to be manually patched because GLRenderer is an internal type!
[HarmonyPatch("osu.Framework.Graphics.OpenGL.GLRenderer", "ExtractFrameBufferData")]
[HarmonyPatchCategory("StartupPatches")]
public static class GLRenderer_ExtractFrameBufferData_Patch
{
	static int[] _pbos;
	static int _pboIndex = 0;
	static readonly int _numPbos = 2; // Double buffering
	static bool _isPboInitialized = false;
	static int _expectedByteSize = 0;

	[System.Diagnostics.StackTraceHidden]
	private static void CheckGLError(string location)
	{
		ErrorCode error = GL.GetError();
		if (error != ErrorCode.NoError)
		{
			throw new Exception($"OpenGL Error at {location}: {error}");
		}
	}

	static bool Prefix(ref Image<Rgba32> __result, IFrameBuffer frameBuffer)
	{
		int width = frameBuffer.Texture.Width;
		int height = frameBuffer.Texture.Height;
		int byteSize = width * height * 4;

		// 1. Initialize PBOs if not already done or if resolution changed
		if (!_isPboInitialized || _expectedByteSize != byteSize)
		{
			Logger.Log($"{nameof(GLRenderer_ExtractFrameBufferData_Patch)}: initializing PBOs");
			InitializePbos(byteSize);
		}

		// Indices for "Ping-Pong" buffering
		// We write the current frame to one, and read the previous frame from the other
		int writeIdx = _pboIndex % _numPbos;
		int readIdx = (_pboIndex + 1) % _numPbos;

		frameBuffer.Bind();

		// 2. Start the ASYNCHRONOUS transfer from GPU to PBO (writeIdx)
		// Passing IntPtr.Zero tells OpenGL to use the bound PBO as the destination
		GL.BindBuffer(BufferTarget.PixelPackBuffer, _pbos[writeIdx]);
		GL.ReadPixels(0, 0, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);

		// 3. Map the PREVIOUS frame's PBO (readIdx) to CPU memory
		GL.BindBuffer(BufferTarget.PixelPackBuffer, _pbos[readIdx]);

		// The choice between these MapBuffer & MapBufferRange is important... only on Windows. Go figure.
		// The specifics on my laptop are that MapBuffer fails on Intel and MapBufferRange fails on NVIDIA.
		// Since I know MapBuffer works more often (across OSes AND GPUs) than MapBufferRange, they will be error-chained like this.
		IntPtr ptr;
		if ((ptr = GL.Oes.MapBuffer(BufferTargetArb.PixelPackBuffer, BufferAccessArb.ReadOnly)) == 0)
			ptr = GL.MapBufferRange(BufferTarget.PixelPackBuffer, IntPtr.Zero, (IntPtr)byteSize, BufferAccessMask.MapReadBit | BufferAccessMask.MapPersistentBit);

		Image<Rgba32> image = null!;
		if (ptr != IntPtr.Zero)
		{
			unsafe
			{
				// Wrap the pointer in a Span to avoid extra copies
				var span = new ReadOnlySpan<Rgba32>((void*)ptr, width * height);
				image = Image.LoadPixelData(span, width, height);
			}
			GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
		}
		else
			CheckGLError("MapBuffer/MapBufferRange");

		GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
		frameBuffer.Unbind();

		_pboIndex++;

		// NOTE: This will return NULL on the very first frame because PBO[readIdx] 
		// hasn't been filled yet. Handle this in your calling code!
		// return image;
		__result = image;
		return false; // skip original
	}

	static void InitializePbos(int byteSize)
	{
		if (_pbos != null) GL.DeleteBuffers(_pbos.Length, _pbos);

		_pbos = new int[_numPbos];
		GL.GenBuffers(_numPbos, _pbos);

		foreach (int pbo in _pbos)
		{
			GL.BindBuffer(BufferTarget.PixelPackBuffer, pbo);
			GL.BufferData(BufferTarget.PixelPackBuffer, byteSize, IntPtr.Zero, BufferUsageHint.StreamRead);
		}
		GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);

		_expectedByteSize = byteSize;
		_isPboInitialized = true;
		_pboIndex = 0;
	}
}

[HarmonyPatch(typeof(AudioThread), "freeWasapi")]
[HarmonyPatchCategory("FakeGlobalMixerHandle")]
static class AudioThread_freeWasapi_Patch
{
	// When this patch is active, we are calling AudioManager.initCurrentDevice
	// which eventually calls AudioThread.freeWasapi unnecessarily.
	// Completely disable it so the game doesn't crash itself.
	static bool Prefix(AudioThread __instance) => false;
}

[HarmonyPatch(typeof(Player), "CreateGameplayClockContainer")]
[HarmonyPatchCategory("StartupPatches")]
static class Player_CreateGameplayClockContainer_Patch
{
	static bool Prefix(ref GameplayClockContainer __result, WorkingBeatmap beatmap, double gameplayStart)
	{
		__result = new MasterGameplayClockContainer(beatmap, gameplayStart);
		AccessTools.Property(typeof(MasterGameplayClockContainer), "ShouldValidatePlaybackRate").SetValue(__result, true);
		return false;
	}
}

using System;
using HarmonyLib;
using ManagedBass;
using ManagedBass.Mix;
using ManagedBass.Wasapi;
using osu.Framework.Bindables;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Threading;
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
	static bool Prefix(GameplayClockContainer __instance)
	{
		__instance.GetGameplayClock().Start();
		return false;
	}
}

[HarmonyPatch(typeof(GameplayClockContainer), "StopGameplayClock")]
[HarmonyPatchCategory("WhileRecording")]
static class GameplayClockContainer_StopGameplayClock_Patch
{
	static bool Prefix(GameplayClockContainer __instance)
	{
		__instance.GetGameplayClock().Stop();
		return false;
	}
}

[HarmonyPatch(typeof(GameplayClockContainer), "Seek")]
[HarmonyPatchCategory("WhileRecording")]
static class GameplayClockContainer_Seek_Patch
{
	static bool Prefix(GameplayClockContainer __instance, double time)
	{
		__instance.GetGameplayClock().Seek(time);
		(AccessTools.Event(typeof(GameplayClockContainer), "OnSeek")
			?? throw new InvalidOperationException("Event GameplayClockContainer.OnSeek not found"))
			.GetRaiseMethod()?.Invoke(__instance, []);
		return false;
	}
}

// This needs to be manually patched because GLRenderer is an internal type!
public static class GLRenderer_ExtractFrameBufferData_Patch
{
	public static void Patch(Harmony harmony) =>
		harmony.Patch(
			original: AccessTools.Method("osu.Framework.Graphics.OpenGL.GLRenderer:ExtractFrameBufferData"),
			prefix: new HarmonyMethod(Prefix)
		);

	static int[] _pbos;
	static int _pboIndex = 0;
	static readonly int _numPbos = 2; // Double buffering
	static bool _isPboInitialized = false;
	static int _expectedByteSize = 0;

	static bool Prefix(ref Image<Rgba32> __result, IFrameBuffer frameBuffer)
	{
		int width = frameBuffer.Texture.Width;
		int height = frameBuffer.Texture.Height;
		int byteSize = width * height * 4;

		// 1. Initialize PBOs if not already done or if resolution changed
		if (!_isPboInitialized || _expectedByteSize != byteSize)
		{
			Console.WriteLine("ExtractFrameBufferData: initializing PBOs");
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

		IntPtr ptr = GL.Oes.MapBuffer(BufferTargetArb.PixelPackBuffer, BufferAccessArb.ReadOnly);

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

[HarmonyPatch(typeof(AudioThread), "InitDevice")]
[HarmonyPatchCategory("StartupPatches")]
static class AudioThread_InitDevice_Patch
{
	static void Postfix(AudioThread __instance, bool useExperimentalWasapi)
	{
		if (useExperimentalWasapi)
			// Global mixer is already setup
			return;

		var globalMixerHandle = AccessTools.FieldRefAccess<AudioThread, Bindable<int?>>(__instance, "globalMixerHandle");

		if (globalMixerHandle.Value != null)
			return;

		globalMixerHandle.Value = BassMix.CreateMixerStream(44100, 2, BassFlags.MixerNonStop);

		if (globalMixerHandle.Value == 0)
			throw new InvalidOperationException($"CreateMixerStream: {Bass.LastError}");

		if (!Bass.ChannelPlay((int)globalMixerHandle.Value))
			throw new InvalidOperationException($"ChannelPlay: {Bass.LastError}");
	}
}

[HarmonyPatch(typeof(AudioThread), "freeWasapi")]
[HarmonyPatchCategory("StartupPatches")]
static class AudioThread_freeWasapi_Patch
{
	static bool Prefix(AudioThread __instance)
	{
		// We can check whether this is non-null to see if WASAPI was initialized.
		var wasapiProcedure = AccessTools.FieldRefAccess<AudioThread, WasapiProcedure>(__instance, "wasapiProcedure");

		if (wasapiProcedure != null)
			// WASAPI was initialized. Let the original method run.
			return true;

		// WASAPI was not initialized. We need to free our stream.
		var globalMixerHandle = AccessTools.FieldRefAccess<AudioThread, Bindable<int?>>(__instance, "globalMixerHandle");

		if (globalMixerHandle.Value == null)
			return false;

		Bass.StreamFree((int)globalMixerHandle.Value);

		return false; // skip running the original method
	}
}

// SPDX-License-Identifier: AGPL-3.0-only

using System.Diagnostics;
using System.IO;
using CouchSubs.Core;
using CouchSubs.RemotePlay;

namespace CouchSubs.App.Services;

internal sealed class VideoDeviceStream(string deviceName) : IStreamSession
{
    private const int Width = 1920;
    private const int Height = 1080;
    private const int Stride = Width * 4;
    private CancellationTokenSource? capture;

    public event Action<Frame>? FrameReceived;
    public event Action<SessionState>? StateChanged;
    public event Action<RegisteredConsole>? ConsoleMoved { add { } remove { } }

    public void Start()
    {
        if (capture is not null) return;
        var source = new CancellationTokenSource();
        var token = source.Token;
        capture = source;
        StateChanged?.Invoke(SessionState.Connecting);
        _ = Task.Run(() => ReadCameraAsync(token));
    }

    public void Stop()
    {
        var source = capture;
        capture = null;
        source?.Cancel();
        source?.Dispose();
    }

    public void ReconnectNow()
    {
        Stop();
        Start();
    }

    public void Dispose() => Stop();

    private async Task ReadCameraAsync(CancellationToken token)
    {
        try
        {
            token.ThrowIfCancellationRequested();
            var start = new ProcessStartInfo("ffmpeg")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            string[] arguments =
            [
                "-hide_banner", "-loglevel", "error", "-nostdin",
                "-rtbufsize", "32M", "-f", "dshow",
                "-i", $"video={deviceName}",
                "-an", "-vf", $"fps=5,scale={Width}:{Height}:force_original_aspect_ratio=decrease,pad={Width}:{Height}:(ow-iw)/2:(oh-ih)/2",
                "-pix_fmt", "bgra",
                "-threads", "1", "-f", "rawvideo", "pipe:1",
            ];
            foreach (var argument in arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start) ?? throw new IOException("FFmpeg başlatılamadı.");
            using var terminate = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); }
                catch (InvalidOperationException) { }
                catch (System.ComponentModel.Win32Exception) { }
            });
            var errors = process.StandardError.ReadToEndAsync(token);
            var stream = process.StandardOutput.BaseStream;
            var first = true;
            while (!token.IsCancellationRequested)
            {
                var pixels = new byte[Stride * Height];
                try { await stream.ReadExactlyAsync(pixels, token); }
                catch (EndOfStreamException) { break; }
                token.ThrowIfCancellationRequested();
                if (first)
                {
                    first = false;
                    SubtitleLog.Write($"Video yakalama aygıtından ilk kare: {Width}x{Height}");
                    StateChanged?.Invoke(SessionState.Streaming);
                }
                FrameReceived?.Invoke(new Frame(pixels, Width, Height, Stride));
            }
            await process.WaitForExitAsync(token);
            var error = (await errors).Trim();
            throw new IOException(error.Length == 0 ? "Video yakalama aygıtı akışı kapandı." : error);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            if (token.IsCancellationRequested) return;
            RemotePlayLog.Note($"Video yakalama aygıtı: {error.Message}");
            StateChanged?.Invoke(SessionState.Disconnected(DisconnectKind.Other, error.Message));
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
    }
}

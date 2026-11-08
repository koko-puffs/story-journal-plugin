using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Textures.TextureWraps;
using FFXIVClientStructs.FFXIV.Client.UI;
using StoryJournal.Core;

namespace StoryJournal.Session;

/// <summary>What a photo is for: its session and character, its caption, when it was asked for.</summary>
public sealed record PhotoAsk(Guid Session, string Character, string Caption, DateTime At);

/// <summary>
/// Photos without the HUD, as Glamour Studio's shots are taken: the HUD is hidden (as Scroll Lock does)
/// for a few frames, the game's next frame is grabbed before ImGui draws (so no plugin window is in
/// it either), the HUD shown again, then the picture is made at most 2560 on its long side (1920 if
/// that's still too big) as a JPEG under the site's 6 MB, off the framework thread, and kept in the
/// queue (<see cref="EventQueue.AddPhoto"/>) until the site has it. Framework thread, but for the making.
/// </summary>
public sealed class Photos : IDisposable
{
    /// <summary>WIC's JPEG container (GUID_ContainerFormatJpeg), if the encoders' list doesn't name it by its MIME type.</summary>
    private static readonly Guid JpegContainer = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057");

    /// <summary>DXGI_FORMAT_B8G8R8A8_UNORM.</summary>
    private const int Bgra = 87;

    private static readonly int[] LongEdges = { 2560, 1920 };

    private static readonly float[] Qualities = { 0.9f, 0.84f, 0.78f, 0.72f, 0.65f, 0.58f, 0.5f };

    /// <summary>What a photo may weigh: well under the bucket's 6 MB.</summary>
    private const int MaxBytes = 5_000_000;

    private static readonly TimeSpan MakeTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MakeGiveUp = TimeSpan.FromSeconds(25);

    private readonly EventQueue queue;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationToken stopping;
    private readonly object textures = new();
    private readonly List<IDalamudTextureWrap> owned = new();
    private bool disposed;

    private PhotoAsk? hudWait;
    private DateTime hudWaitAt;
    private int hudWaitFrames;
    private bool hidHud;
    private Task<IDalamudTextureWrap>? grab;
    private PhotoAsk? grabFor;
    private DateTime grabAt;
    private Task? making;
    private DateTime makingAt;
    private Making? makingNow;
    private int generation;

    /// <summary>
    /// A picture being made: given up on (after <see cref="MakeGiveUp"/>) or kept, never both, so a
    /// picture you were told to take again isn't kept as well.
    /// </summary>
    private sealed class Making
    {
        public const int Working = 0, Keeping = 1, GivenUp = 2;

        public int State;

        public readonly CancellationTokenSource Cancel;

        public Making(CancellationToken stopping) => Cancel = CancellationTokenSource.CreateLinkedTokenSource(stopping);
    }

    public Photos(EventQueue queue)
    {
        this.queue = queue;
        stopping = stop.Token;
    }

    /// <summary>A photo is being taken (the HUD hidden, the frame grabbed, the picture made).</summary>
    public bool Capturing { get; private set; }

    /// <summary>A photo was kept (framework thread): its size.</summary>
    public event Action<QueuedPhoto>? Taken;

    /// <summary>A photo couldn't be taken (framework thread): why.</summary>
    public event Action<string>? Failed;

    /// <summary>Takes a photo (framework thread); false, with why, when it can't now.</summary>
    public bool Take(PhotoAsk ask, out string why)
    {
        why = string.Empty;
        if (Capturing)
        {
            why = "A photo is being taken.";
            return false;
        }
        Capturing = true;
        if (!Services.GameGui.GameUiHidden && SetHud(false))
        {
            hidHud = true;
            hudWait = ask;
            hudWaitAt = DateTime.UtcNow;
            hudWaitFrames = 0;
            return true;
        }
        return Grab(ask, out why);
    }

    private bool Grab(PhotoAsk ask, out string why)
    {
        why = string.Empty;
        try
        {
            grab = Services.Textures.CreateFromImGuiViewportAsync(
                new ImGuiViewportTextureArgs { ViewportId = ImGui.GetMainViewport().ID, TakeBeforeImGuiRender = true },
                "Story Journal photo", stopping);
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Story Journal couldn't grab the game's picture");
            why = "Couldn't take the picture (see /xllog).";
            Fail(why);
            return false;
        }
        grabFor = ask;
        grabAt = DateTime.UtcNow;
        generation++;
        return true;
    }

    /// <summary>Framework thread, every frame: the HUD's wait, then the grabbed frame made into a picture.</summary>
    public void Tick()
    {
        if (hudWait is { } w)
        {
            hudWaitFrames++;
            var waited = DateTime.UtcNow - hudWaitAt;
            if ((hudWaitFrames < 3 || waited < TimeSpan.FromMilliseconds(150) || !Services.GameGui.GameUiHidden) && waited < TimeSpan.FromSeconds(1)) return;
            hudWait = null;
            if (!Grab(w, out _)) ShowHudAgain();
            return;
        }
        if (grab is { IsCompleted: false } late && DateTime.UtcNow - grabAt > TimeSpan.FromSeconds(5))
        {
            late.ContinueWith(x =>
            {
                if (x.IsCompletedSuccessfully) x.Result.Dispose();
            }, TaskScheduler.Default);
            grab = null;
            grabFor = null;
            generation++;
            ShowHudAgain();
            Fail("The game's picture never came. Try again.");
            return;
        }
        if (making is { IsCompleted: false } && Capturing && DateTime.UtcNow - makingAt > MakeGiveUp && makingNow is { } slow)
        {
            // Given up only if it isn't being kept this very moment (then it finishes, and says so itself).
            if (Interlocked.CompareExchange(ref slow.State, Making.GivenUp, Making.Working) == Making.Working)
            {
                slow.Cancel.Cancel();
                making = null;
                makingNow = null;
                generation++;
                Fail("Making the picture took too long. Try again.");
            }
            return;
        }
        if (grab is not { IsCompleted: true } t || grabFor is not { } ask) return;
        grab = null;
        grabFor = null;
        ShowHudAgain();
        if (!t.IsCompletedSuccessfully)
        {
            if (t.Exception != null) Services.Log.Error(t.Exception, "Story Journal couldn't grab the game's picture");
            Fail("Couldn't take the picture (see /xllog).");
            return;
        }
        var wrap = Own(t.Result);
        var gen = generation;
        var job = makingNow = new Making(stopping);
        makingAt = DateTime.UtcNow;
        making = Task.Run(async () =>
        {
            var (jpeg, width, height, why) = await Make(wrap, job.Cancel.Token).ConfigureAwait(false);
            // Given up meanwhile (you were told to take it again): not kept.
            if (Interlocked.CompareExchange(ref job.State, Making.Keeping, Making.Working) != Making.Working) return;
            job.Cancel.Dispose();
            QueuedPhoto? kept = null;
            if (jpeg != null && why == null && !stopping.IsCancellationRequested)
            {
                try
                {
                    kept = new QueuedPhoto(Guid.NewGuid(), ask.Session, ask.Character, ask.At, ask.Caption, width, height);
                    queue.AddPhoto(kept, jpeg);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    Services.Log.Error(ex, "Story Journal couldn't keep the photo");
                    kept = null;
                    why = "Couldn't keep the photo on this PC (see /xllog).";
                }
            }
            _ = Services.Framework.RunOnFrameworkThread(() =>
            {
                if (gen != generation || disposed) return;
                if (makingNow == job) makingNow = null;
                Capturing = false;
                if (kept != null) Taken?.Invoke(kept);
                else Fail(why ?? "Couldn't make the picture.");
            });
        });
    }

    private void Fail(string why)
    {
        Capturing = false;
        Services.Log.Warning($"Story Journal: no photo: {why}");
        Failed?.Invoke(why);
    }

    private static Guid? JpegCodec()
    {
        foreach (var codec in Services.TextureReadback.GetSupportedImageEncoderInfos())
            if (codec.ContainerGuid == JpegContainer || codec.MimeTypes.Any(m => m.Trim().Equals("image/jpeg", StringComparison.OrdinalIgnoreCase)))
                return codec.ContainerGuid;
        return null;
    }

    /// <summary>
    /// On a worker: the grabbed frame made 2560 at most on its long side, written as a JPEG at the first
    /// quality that's small enough (1920 if none at 2560 is). The frame and the sized picture are let go
    /// of here whatever happens.
    /// </summary>
    private async Task<(byte[]? Jpeg, int Width, int Height, string? Why)> Make(IDalamudTextureWrap grabbed, CancellationToken stopping)
    {
        IDalamudTextureWrap? sized = null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stopping);
        timeout.CancelAfter(MakeTimeout);
        var cancel = timeout.Token;
        try
        {
            if (JpegCodec() is not { } codec) return (null, 0, 0, "This PC can't write JPEG pictures.");
            var format = Services.Textures.IsDxgiFormatSupportedForCreateFromExistingTextureAsync(Bgra) ? Bgra : 0;
            (int Width, int Height)? tried = null;
            foreach (var edge in LongEdges)
            {
                if (PhotoUpload.Fit(grabbed.Width, grabbed.Height, edge) is not { } fit) return (null, 0, 0, "The game's picture is too small.");
                if (tried == fit) break;
                tried = fit;
                Release(sized);
                sized = null;
                sized = Own(await Services.Textures.CreateFromExistingTextureAsync(grabbed,
                    new TextureModificationArgs { NewWidth = fit.Width, NewHeight = fit.Height, MakeOpaque = true, DxgiFormat = format },
                    leaveWrapOpen: true, "Story Journal photo (sized)", cancel).ConfigureAwait(false));
                foreach (var q in Qualities)
                {
                    using var stream = new MemoryStream();
                    await Services.TextureReadback.SaveToStreamAsync(sized, codec, stream, new Dictionary<string, object> { ["ImageQuality"] = q },
                        leaveWrapOpen: true, leaveStreamOpen: true, cancel).ConfigureAwait(false);
                    if (stream.Length > MaxBytes) continue;
                    return (stream.ToArray(), sized.Width, sized.Height, null);
                }
            }
            return (null, 0, 0, "The picture is too big to send, even smaller.");
        }
        catch (OperationCanceledException) when (!stopping.IsCancellationRequested)
        {
            return (null, 0, 0, "Making the picture took too long. Try again.");
        }
        catch (OperationCanceledException)
        {
            return (null, 0, 0, "Stopped.");
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, "Story Journal couldn't make the picture");
            return (null, 0, 0, "Couldn't make the picture (see /xllog).");
        }
        finally
        {
            Release(grabbed);
            Release(sized);
        }
    }

    private void ShowHudAgain()
    {
        hudWait = null;
        if (!hidHud) return;
        hidHud = false;
        if (Services.GameGui.GameUiHidden) SetHud(true);
    }

    private static unsafe bool SetHud(bool visible)
    {
        try
        {
            var ui = UIModule.Instance();
            if (ui == null) return false;
            var atk = ui->GetRaptureAtkModule();
            if (atk == null) return false;
            atk->SetUiVisibility(visible);
            return true;
        }
        catch (Exception ex)
        {
            Services.Log.Error(ex, $"Story Journal couldn't {(visible ? "show" : "hide")} the HUD");
            return false;
        }
    }

    private T Own<T>(T wrap) where T : IDalamudTextureWrap
    {
        lock (textures)
        {
            if (disposed)
            {
                wrap.Dispose();
                return wrap;
            }
            owned.Add(wrap);
            return wrap;
        }
    }

    private void Release(IDalamudTextureWrap? wrap)
    {
        if (wrap == null) return;
        lock (textures) owned.Remove(wrap);
        wrap.Dispose();
    }

    public void Dispose()
    {
        stop.Cancel();
        ShowHudAgain();
        if (grab is { } g)
        {
            g.ContinueWith(x =>
            {
                if (x.IsCompletedSuccessfully) x.Result.Dispose();
            }, TaskScheduler.Default);
            grab = null;
            grabFor = null;
        }
        lock (textures)
        {
            disposed = true;
            foreach (var w in owned) w.Dispose();
            owned.Clear();
        }
    }
}

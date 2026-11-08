using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using StoryJournal.Core;

namespace StoryJournal.Session;

/// <summary>
/// The Codex (<c>codex.json</c>, version 2, made from the game's files by kokos-tools:
/// https://assets.kokos-tools.com/story/en/codex.json), kept in the plugin's config folder and fetched
/// again once a day. Until it's fetched (or when it can't be reached) the copy kept is used, or none: the
/// Codex is then empty, and only the people and places the plugin saw itself show. A copy kept from
/// before (version 1, the site's hand-written Codex) is read until the new one comes, and the new one is
/// fetched at once. Everything here runs on a worker.
/// </summary>
public sealed class CodexStore : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromDays(1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The most a Codex may weigh, unpacked (thousands of entries: some megabytes).</summary>
    private const long MaxBytes = 64 * 1024 * 1024;

    private readonly string file;
    private readonly HttpClient http;
    private readonly CancellationTokenSource stop = new();
    private readonly CancellationToken stopping;
    private Task? fetching;
    private DateTime nextFetch = DateTime.MinValue;
    private Codex codex = Codex.Empty;

    public CodexStore(string dir, HttpClient http)
    {
        file = Path.Combine(dir, "codex.json");
        this.http = http;
        stopping = stop.Token;
        fetching = Task.Run(Load);
    }

    /// <summary>The Codex now (empty until one's read).</summary>
    public Codex Codex => Volatile.Read(ref codex);

    /// <summary>Goes up when the Codex changes.</summary>
    public int Version { get; private set; }

    /// <summary>Why the last fetch didn't come (null: it came, or none tried yet).</summary>
    public string? Problem { get; private set; }

    public DateTime? FetchedAt { get; private set; }

    private void Load()
    {
        try
        {
            if (!File.Exists(file)) return;
            var read = Codex.Read(File.ReadAllBytes(file));
            Swap(read);
            FetchedAt = File.GetLastWriteTimeUtc(file);
            // The old Codex (version 1) is kept only until the new one comes: fetched at once.
            nextFetch = read.FileVersion >= 2 ? FetchedAt.Value + Every : DateTime.MinValue;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Services.Log.Warning(ex, "Story Journal couldn't read its copy of the Codex");
        }
    }

    private void Swap(Codex next)
    {
        Volatile.Write(ref codex, next);
        Version++;
    }

    /// <summary>Framework thread, now and then: fetches again when a day's gone by.</summary>
    public void Tick(DateTime now)
    {
        if (fetching is { IsCompleted: false } || now < nextFetch || stopping.IsCancellationRequested) return;
        nextFetch = now + TimeSpan.FromHours(1);
        fetching = Task.Run(() => Fetch(now));
    }

    /// <summary>Fetches now (Settings' Refresh).</summary>
    public void FetchNow() => nextFetch = DateTime.MinValue;

    private async Task Fetch(DateTime now)
    {
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(stopping);
            wait.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, SiteLink.CodexUrl);
            // Packed on the way (a few MB of text): the shared connection doesn't unpack by itself, so it's done here.
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("br"));
            request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("gzip"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, wait.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Problem = $"The site didn't have the Codex ({(int)response.StatusCode}).";
                return;
            }
            if (response.Content.Headers.ContentLength is > MaxBytes)
            {
                Problem = "The Codex is bigger than expected.";
                return;
            }
            // Read as a stream (the shared connection's buffer is for the site's small answers), at most MaxBytes unpacked.
            var bytes = await ReadAll(response.Content, wait.Token).ConfigureAwait(false);
            if (bytes == null)
            {
                Problem = "The Codex is bigger than expected.";
                return;
            }
            wait.Token.ThrowIfCancellationRequested();
            var read = Codex.Read(bytes);
            if (read.Entries.Count == 0)
            {
                Problem = "The site's Codex is empty or couldn't be read.";
                return;
            }
            var tmp = file + ".tmp";
            await File.WriteAllBytesAsync(tmp, bytes, wait.Token).ConfigureAwait(false);
            if (stopping.IsCancellationRequested) return;
            File.Move(tmp, file, true);
            Swap(read);
            FetchedAt = now;
            Problem = null;
            nextFetch = now + Every;
        }
        catch (OperationCanceledException)
        {
            Problem = stopping.IsCancellationRequested ? null : "The site didn't answer in time.";
        }
        catch (Exception) when (stopping.IsCancellationRequested)
        {
            // Unloading: the shared connection is let go (just after this was stopped) while the fetch
            // was under way. Nothing to say, nothing written.
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidDataException or UnauthorizedAccessException)
        {
            Problem = "Couldn't reach the site for the Codex.";
            Services.Log.Debug(ex, "Story Journal couldn't fetch the Codex");
        }
    }

    /// <summary>The answer's body, unpacked (gzip or Brotli); null when it's over <see cref="MaxBytes"/>.</summary>
    private static async Task<byte[]?> ReadAll(HttpContent content, CancellationToken cancel)
    {
        await using var raw = await content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        var packing = content.Headers.ContentEncoding.LastOrDefault()?.Trim().ToLowerInvariant();
        await using Stream body = packing switch
        {
            "gzip" => new GZipStream(raw, CompressionMode.Decompress, true),
            "br" => new BrotliStream(raw, CompressionMode.Decompress, true),
            _ => raw,
        };
        using var into = new MemoryStream();
        var buffer = new byte[81920];
        int n;
        while ((n = await body.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            if (into.Length + n > MaxBytes) return null;
            into.Write(buffer, 0, n);
        }
        return into.ToArray();
    }

    /// <summary>
    /// Stops a fetch under way (before the shared connection is let go, which the journal does after): it
    /// ends quietly, whatever the connection throws then, and nothing is written after.
    /// </summary>
    public void Dispose()
    {
        stop.Cancel();
        // (The source isn't disposed: a fetch still finishing may look at its token.)
    }
}

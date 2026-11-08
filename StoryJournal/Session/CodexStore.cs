using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using StoryJournal.Core;

namespace StoryJournal.Session;

/// <summary>
/// The Codex's words (<c>codex.json</c>, the site's: https://www.kokos-tools.com/story-journal/codex.json),
/// kept in the plugin's config folder and fetched again once a day. Until it's fetched (or when the site
/// can't be reached) the copy kept is used, or none: the Codex is then empty, and only the people and
/// places the plugin saw itself show. Everything here runs on a worker.
/// </summary>
public sealed class CodexStore : IDisposable
{
    private static readonly TimeSpan Every = TimeSpan.FromDays(1);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(20);

    /// <summary>The most a Codex may weigh (it's a few hundred KB at most).</summary>
    private const long MaxBytes = 8 * 1024 * 1024;

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
            Swap(Codex.Read(File.ReadAllText(file)));
            FetchedAt = File.GetLastWriteTimeUtc(file);
            nextFetch = FetchedAt.Value + Every;
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
            using var response = await http.GetAsync(SiteLink.CodexUrl, HttpCompletionOption.ResponseHeadersRead, wait.Token).ConfigureAwait(false);
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
            var text = await response.Content.ReadAsStringAsync(wait.Token).ConfigureAwait(false);
            var read = Codex.Read(text);
            if (read.Entries.Count == 0)
            {
                Problem = "The site's Codex is empty or couldn't be read.";
                return;
            }
            var tmp = file + ".tmp";
            await File.WriteAllTextAsync(tmp, text, wait.Token).ConfigureAwait(false);
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
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            Problem = "Couldn't reach the site for the Codex.";
            Services.Log.Debug(ex, "Story Journal couldn't fetch the Codex");
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        // (The source isn't disposed: a fetch still finishing may look at its token.)
    }
}

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace StoryJournal.Core;

/// <summary>What became of a photo sent: kept, try again later, or refused for good.</summary>
public enum PhotoOutcome
{
    /// <summary>The site has it (or had it already).</summary>
    Kept,

    /// <summary>Not now (no connection, the site busy, its session not there yet, switched off): it stays queued.</summary>
    Later,

    /// <summary>The site said too often: it stays queued, and photos wait a while.</summary>
    Rate,

    /// <summary>The token's no good: link again.</summary>
    Token,

    /// <summary>The site won't ever take this one (not a JPEG, too big): set aside.</summary>
    Refused,
}

/// <summary>How a photo's sending went, with the site's words.</summary>
public sealed record PhotoResult(PhotoOutcome Outcome, string? Why, string? Hint = null);

/// <summary>
/// Sends a photo to the site's edge function <c>sj-plugin-photo</c>: a multipart form with the token,
/// the character's key, the session and the photo's own id (a resend adds nothing), when it was taken,
/// its caption and the JPEG; the publishable key as <c>apikey</c>. Never throws; the token isn't logged.
/// </summary>
public static class PhotoUpload
{
    /// <summary>The bucket's limit: 6 MB.</summary>
    public const int MaxBytes = 6 * 1024 * 1024;

    /// <summary>The longest side the site takes (8192); pictures are made smaller than this anyway.</summary>
    public const int MaxSide = 8192;

    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(90);

    /// <summary>The form, as the function reads it.</summary>
    public static MultipartFormDataContent Form(string token, string character, Guid session, Guid id, DateTime at, string caption, byte[] jpeg)
    {
        var form = new MultipartFormDataContent();
        form.Add(Field("token", token));
        form.Add(Field("character", character));
        form.Add(Field("session", session.ToString("D")));
        form.Add(Field("id", id.ToString("D")));
        form.Add(Field("at", Json.Time(at)));
        form.Add(Field("caption", Json.Tidy(caption, 200)));
        var image = new ByteArrayContent(jpeg);
        image.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        image.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = "\"image\"", FileName = "\"photo.jpg\"" };
        form.Add(image);
        return form;
    }

    private static StringContent Field(string name, string value)
    {
        var c = new StringContent(value, Encoding.UTF8);
        c.Headers.ContentDisposition = new ContentDispositionHeaderValue("form-data") { Name = $"\"{name}\"" };
        return c;
    }

    /// <summary>Reads the function's answer: <c>{ id, data }</c>, or <c>{ message, hint }</c>.</summary>
    public static PhotoResult Read(HttpStatusCode status, string? body)
    {
        var code = (int)status;
        var o = Json.Parse(body) as JsonObject;
        if (code is >= 200 and < 300)
            return o != null && Json.Uuid(o["id"]) != null ? new PhotoResult(PhotoOutcome.Kept, null) : new PhotoResult(PhotoOutcome.Later, "The site answered, but not with the photo.");
        var message = Json.Str(o?["message"]);
        var hint = Json.Str(o?["hint"]);
        var words = message is { Length: > 0 and <= 300 } ? message : null;
        return hint switch
        {
            "token" => new PhotoResult(PhotoOutcome.Token, (words ?? "The site doesn't know this token.") + " Link again in Settings with a new one.", hint),
            "rate" => new PhotoResult(PhotoOutcome.Rate, words ?? "That's a lot of photos this hour. The rest wait.", hint),
            "off" or "gone" or "upload" or "server" => new PhotoResult(PhotoOutcome.Later, words ?? "The site can't take photos right now.", hint),
            "image" or "data" or "form" => new PhotoResult(PhotoOutcome.Refused, words ?? "The site won't take that photo.", hint),
            _ when code == 429 => new PhotoResult(PhotoOutcome.Rate, "The site is busy. Photos wait a while.", hint),
            // Turned away without the site saying it's the token (a gateway, a key the site changed): kept
            // and tried again later. Only the function's own "token" unlinks.
            _ when code is 401 or 403 => new PhotoResult(PhotoOutcome.Later, "The site turned the photo away for now. It's kept and tried again.", hint),
            _ when code is 413 => new PhotoResult(PhotoOutcome.Refused, "That photo is too big for the site.", hint),
            _ => new PhotoResult(PhotoOutcome.Later, code >= 500 ? $"The site had a problem ({code})." : $"The site said no ({code}).", hint),
        };
    }

    /// <summary>Sends one photo; never throws.</summary>
    public static async Task<PhotoResult> SendAsync(HttpClient http, string token, string character, QueuedPhoto photo, byte[] jpeg, CancellationToken cancel)
    {
        if (!SiteLink.LooksRight(token)) return new PhotoResult(PhotoOutcome.Token, "Link your kokos-tools.com account in Settings first.", "token");
        if (jpeg.Length == 0 || jpeg.Length > MaxBytes) return new PhotoResult(PhotoOutcome.Refused, "That photo is too big to send.", "image");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(Timeout);
        try
        {
            using var form = Form(token, character, photo.Session, photo.Id, photo.At, photo.Caption, jpeg);
            using var request = new HttpRequestMessage(HttpMethod.Post, SiteLink.PhotoUrl) { Content = form };
            request.Headers.Add("apikey", SiteLink.PublishableKey);
            request.Headers.Accept.ParseAdd("application/json");
            using var response = await http.SendAsync(request, timeout.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false);
            return Read(response.StatusCode, text);
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return new PhotoResult(PhotoOutcome.Later, "The site didn't answer in time.");
        }
        catch (OperationCanceledException)
        {
            return new PhotoResult(PhotoOutcome.Later, "Stopped.");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or ObjectDisposedException)
        {
            return new PhotoResult(PhotoOutcome.Later, "Couldn't reach the site.");
        }
    }

    /// <summary>A size with its longest side at most <paramref name="longEdge"/>, its shape kept; null when it's too small (under 64).</summary>
    public static (int Width, int Height)? Fit(int width, int height, int longEdge)
    {
        if (width < 64 || height < 64 || longEdge < 64) return null;
        var scale = Math.Min(1.0, (double)longEdge / Math.Max(width, height));
        var (w, h) = ((int)Math.Round(width * scale), (int)Math.Round(height * scale));
        return w < 64 || h < 64 ? null : (w, h);
    }
}

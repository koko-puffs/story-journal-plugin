using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Dalamud.Game;
using Dalamud.Interface.ManagedFontAtlas;

namespace StoryJournal.UI;

/// <summary>The faces, by job: Geist for controls (as kokos-tools), Fraunces to name things, Literata to read.</summary>
public enum Face
{
    /// <summary>Geist 400: what you read.</summary>
    Sans,

    /// <summary>Geist 500: buttons, names in lists.</summary>
    SansMedium,

    /// <summary>Geist 600: headings (tight, as the site's).</summary>
    SansSemibold,

    /// <summary>Geist Mono 400: every number (counts, levels, times), so they line up.</summary>
    Mono,

    /// <summary>Fraunces 600 (opsz 24, soft 60): what a thing is called (titles, the greeting, big numbers).</summary>
    Display,

    /// <summary>Fraunces 600 italic: the second half of a greeting, a chapter's name in its seal's card.</summary>
    DisplayItalic,

    /// <summary>Literata 400: anything read for more than a line (why you're here, previously, the Codex).</summary>
    Read,

    /// <summary>Literata 400 italic: your own words (notes, theories, an evening's title), in candle gold.</summary>
    ReadItalic,
}

/// <summary>
/// The faces at the sizes the windows use, loaded through Dalamud's font atlas (so they follow
/// Dalamud's global scale and rebuild with it): Geist 14–10 (400), 14/13/12 (500), 30–10 (600); Geist Mono
/// 22/15/13/12/11 (every number); Fraunces SemiBold 48 (the drop cap's), 28–13 and its italic (static instances at opsz 72, soft
/// 60); Literata Regular 16–12 and its italic (opsz 14). All under the SIL Open Font License 1.1, unmodified,
/// with their licences (Resources/Fonts).
///
/// Sizes are CSS sizes (the em). ImGui sizes a font by its line (ascent to descent), so each face is
/// loaded at its em times its line ratio, and 14px Geist here is as tall as 14px in the browser.
///
/// On a Japanese client the sans sizes are Dalamud's own font (it has the kana and kanji that duty
/// and ability names need); the mono keeps the design's face.
/// </summary>
public sealed class Fonts : IDisposable
{
    /// <summary>(hhea ascent − descent) / em, read from the font files.</summary>
    private const float GeistLine = 1.3f;

    private const float FrauncesLine = 1.233f;
    private const float LiterataLine = 1.485f;

    private static readonly (Face Face, int Px)[] Wanted =
    {
        (Face.Sans, 14), (Face.Sans, 13), (Face.Sans, 12), (Face.Sans, 11), (Face.Sans, 10),
        (Face.SansMedium, 14), (Face.SansMedium, 13), (Face.SansMedium, 12),
        (Face.SansSemibold, 30), (Face.SansSemibold, 22), (Face.SansSemibold, 18), (Face.SansSemibold, 16), (Face.SansSemibold, 15), (Face.SansSemibold, 14), (Face.SansSemibold, 13), (Face.SansSemibold, 12), (Face.SansSemibold, 11), (Face.SansSemibold, 10),
        (Face.Mono, 22), (Face.Mono, 15), (Face.Mono, 13), (Face.Mono, 12), (Face.Mono, 11),
        (Face.Display, Ink.DropCapPx), (Face.Display, 28), (Face.Display, 22), (Face.Display, 19), (Face.Display, 17), (Face.Display, 15), (Face.Display, 13),
        (Face.DisplayItalic, 22), (Face.DisplayItalic, 17), (Face.DisplayItalic, 15),
        (Face.Read, 16), (Face.Read, 15), (Face.Read, 14), (Face.Read, 13), (Face.Read, 12),
        (Face.ReadItalic, 15), (Face.ReadItalic, 14), (Face.ReadItalic, 13), (Face.ReadItalic, 12),
    };

    private readonly Dictionary<(Face, int), IFontHandle> handles = new();

    /// <summary>Sizes asked for that aren't loaded, and the handle used instead (found once, not each frame).</summary>
    private readonly Dictionary<(Face, int), IFontHandle> nearest = new();

    private int generation;
    private bool ready;

    public Fonts()
    {
        var atlas = Services.PluginInterface.UiBuilder.FontAtlas;
        var japanese = Services.ClientState.ClientLanguage == ClientLanguage.Japanese;
        foreach (var (face, px) in Wanted)
        {
            var file = face switch
            {
                Face.Sans => "Geist-Regular.ttf",
                Face.SansMedium => "Geist-Medium.ttf",
                Face.SansSemibold => "Geist-SemiBold.ttf",
                Face.Display => "Fraunces-SemiBold.ttf",
                Face.DisplayItalic => "Fraunces-SemiBoldItalic.ttf",
                Face.Read => "Literata-Regular.ttf",
                Face.ReadItalic => "Literata-Italic.ttf",
                _ => "GeistMono-Regular.ttf",
            };
            var line = face switch
            {
                Face.Display or Face.DisplayItalic => FrauncesLine,
                Face.Read or Face.ReadItalic => LiterataLine,
                _ => GeistLine,
            };
            var dalamudFont = japanese && face is not Face.Mono;
            // The mono only shows times and counts, and the drop cap one letter: they need no other language's letters.
            var handle = Make(atlas, file, px * line, dalamudFont && px < Ink.DropCapPx, face is not Face.Mono && px < Ink.DropCapPx);
            // Rebuilt (Dalamud's font settings, the scale): what was measured with the old one is stale.
            // This can come from the font build's own thread, hence only a counter.
            handle.ImFontChanged += OnFontChanged;
            handles[(face, px)] = handle;
        }
    }

    private void OnFontChanged(IFontHandle handle, ILockedImFont font) => Interlocked.Increment(ref generation);

    /// <summary>Goes up whenever a face is built again: text measured before is to be measured again.</summary>
    public int Generation => Volatile.Read(ref generation);

    /// <summary>
    /// Every face is built. Until then a push gives Dalamud's default font, so what's measured then
    /// (wrapped lines, cut text) mustn't be kept.
    /// </summary>
    public bool Ready
    {
        get
        {
            if (ready) return true;
            foreach (var h in handles.Values)
                if (!h.Available) return false;
            return ready = handles.Count > 0;
        }
    }

    /// <summary>Pushes a face at a CSS size (the nearest loaded size when that one isn't).</summary>
    public IDisposable Push(Face face, int px) => Handle(face, px).Push();

    public IFontHandle Handle(Face face, int px)
    {
        if (handles.TryGetValue((face, px), out var h)) return h;
        if (nearest.TryGetValue((face, px), out h)) return h;
        var best = handles.Keys.Where(k => k.Item1 == face).OrderBy(k => Math.Abs(k.Item2 - px)).FirstOrDefault();
        return nearest[(face, px)] = handles.TryGetValue(best, out h) ? h : handles[(Face.Sans, 14)];
    }

    private static IFontHandle Make(IFontAtlas atlas, string file, float sizePx, bool dalamudFont, bool languages) =>
        atlas.NewDelegateFontHandle(e => e.OnPreBuild(tk =>
        {
            var font = dalamudFont
                ? tk.AddDalamudDefaultFont(sizePx, null)
                : tk.AddFontFromMemory(Read(file), new SafeFontConfig { SizePx = sizePx }, file);
            // The game's own symbols and whatever Dalamud's language needs (ability names in any client's language).
            tk.AddGameSymbol(new SafeFontConfig { SizePx = sizePx, MergeFont = font });
            if (languages) tk.AttachExtraGlyphsForDalamudLanguage(new SafeFontConfig { SizePx = sizePx, MergeFont = font });
        }));

    private static readonly Dictionary<string, byte[]> Files = new();

    private static byte[] Read(string file)
    {
        lock (Files)
        {
            if (Files.TryGetValue(file, out var cached)) return cached;
            var asm = typeof(Fonts).Assembly;
            var name = asm.GetManifestResourceNames().First(n => n.EndsWith(file, StringComparison.Ordinal));
            using var s = asm.GetManifestResourceStream(name)!;
            using var ms = new MemoryStream();
            s.CopyTo(ms);
            return Files[file] = ms.ToArray();
        }
    }

    public void Dispose()
    {
        foreach (var h in handles.Values)
        {
            h.ImFontChanged -= OnFontChanged;
            h.Dispose();
        }
        handles.Clear();
        nearest.Clear();
    }
}

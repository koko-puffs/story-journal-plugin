using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using static StoryJournal.UI.Theme;

namespace StoryJournal.UI;

/// <summary>
/// The rows every window lays out the same way: the mark, a small heading in spaced capitals, a
/// paragraph, a setting's row with its control on the right, a line of chips, a row of buttons.
/// </summary>
internal static class Parts
{
    /// <summary>The mark: the tile red rounded square (8/30 of its size) with the open book in white.</summary>
    public static void Mark(Vector2 pos, float size)
    {
        var dl = Kit.Dl;
        dl.AddRectFilled(pos, pos + new Vector2(size), Palette.U32(Palette.AccentDeep), size * 8 / 30);
        var icon = MathF.Round(size * 0.62f);
        Icons.Draw(dl, Icon.BookOpen, pos + new Vector2((size - icon) / 2, (size - icon) / 2 + size * 0.04f), icon, Palette.White, 2.2f);
    }

    /// <summary>A card's head: the mark, its title, and × on the right (true when pressed).</summary>
    public static bool Head(string id, string title, bool close = true, string? note = null)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var h = S(24);
        Mark(at + new Vector2(0, S(2)), S(20));
        var tx = at.X + S(28);
        var right = close ? at.X + w - h - S(4) : at.X + w;
        var used = Txt.Line(Kit.Dl, new Vector2(tx, at.Y), Txt.Fit(title, Face.SansSemibold, 14, right - tx), Face.SansSemibold, 14, Palette.Ink, 24);
        if (note != null && tx + used + S(8) < right)
            Txt.Line(Kit.Dl, new Vector2(tx + used + S(8), at.Y), Txt.Fit(note, Face.Sans, 12, right - tx - used - S(8)), Face.Sans, 12, Palette.Muted, 24);
        var closed = close && Kit.IconButton(id, new Vector2(at.X + w - h, at.Y), Icon.X, "Close", 24, Btn.Quiet);
        Kit.Reserve(at, w, h);
        return closed;
    }

    /// <summary>A small heading in spaced capitals ("PREVIOUSLY"), its gap above.</summary>
    public static void Heading(string caps, float gapAbove = 14, Vector4? color = null)
    {
        Kit.Gap(gapAbove);
        Kit.LabelRow(caps, color);
        Kit.Gap(6);
    }

    /// <summary>A hairline across the box, with room around it.</summary>
    public static void Rule(float room = 12)
    {
        Kit.Gap(room);
        var at = Kit.At;
        Kit.Divider(at.X, at.X + Kit.Width, at.Y);
        Kit.Reserve(at, Kit.Width, 1);
        Kit.Gap(room);
    }

    /// <summary>Story words (the journal's, a whisper): 13.5px soft, a little more leading.</summary>
    public static void Story(string text, Vector4? color = null) => Lines.Para(text, color ?? Palette.Soft, 14, 20.5f);

    /// <summary>A quiet line (12px muted).</summary>
    public static void Quiet(string text, Vector4? color = null) => Lines.Para(text, color ?? Palette.Muted, 12, 18);

    /// <summary>A bullet list of story lines.</summary>
    public static void Bullets(IReadOnlyList<string> lines, Vector4? color = null)
    {
        foreach (var line in lines)
        {
            var at = Kit.At;
            var w = Kit.Width;
            Kit.Dot(new Vector2(at.X + S(4), at.Y + S(10)), Palette.Accent, 5);
            var h = Lines.Draw(Kit.Dl, new Vector2(at.X + S(14), at.Y), w - S(14), line, Face.Sans, 14, color ?? Palette.Soft, 20.5f);
            Kit.Reserve(at, w, h);
            Kit.Gap(4);
        }
    }

    /// <summary>A row of buttons left to right (wrapping); returns the index pressed, or -1. A label starting "→" goes to the right.</summary>
    public static int Buttons(string id, IReadOnlyList<(string Label, Btn Style, Icon? Icon)> buttons, BtnSize size = BtnSize.Sm)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var h = Kit.ButtonHeight(size);
        var x = at.X;
        var y = at.Y;
        var pressed = -1;
        var rightX = at.X + w;
        for (var i = buttons.Count - 1; i >= 0; i--)
        {
            var (label, style, icon) = buttons[i];
            if (!label.StartsWith('→')) continue;
            var text = label[1..];
            var bw = Kit.ButtonWidth(text, size, icon, style);
            rightX -= bw;
            if (Kit.Button(Ids.Of(id, i), new Vector2(rightX, y), text, style, size, icon)) pressed = i;
            rightX -= S(6);
        }
        for (var i = 0; i < buttons.Count; i++)
        {
            var (label, style, icon) = buttons[i];
            if (label.StartsWith('→')) continue;
            var bw = Kit.ButtonWidth(label, size, icon, style);
            if (x > at.X && x + bw > rightX)
            {
                x = at.X;
                y += h + S(6);
                rightX = at.X + w;
            }
            if (Kit.Button(Ids.Of(id, i), new Vector2(x, y), label, style, size, icon)) pressed = i;
            x += bw + S(6);
        }
        Kit.Reserve(at, w, y + h - at.Y);
        return pressed;
    }

    /// <summary>Chips that can be picked (moods): returns the index flipped, or -1.</summary>
    public static int PickChips(string id, IReadOnlyList<string> items, Func<int, bool> on)
    {
        var wrap = new Wrap(6);
        var flipped = -1;
        for (var i = 0; i < items.Count; i++)
        {
            var picked = on(i);
            var cw = Kit.ChipW(items[i], picked);
            var p = wrap.Next(cw, S(24));
            var (clicked, hovered, _) = Kit.Hit(Ids.Of(id, i), p, new Vector2(cw, S(24)));
            Kit.Chip(p, items[i], picked ? ChipKind.Accent : hovered ? ChipKind.Quiet : ChipKind.Plain, picked);
            if (clicked) flipped = i;
        }
        wrap.End();
        return flipped;
    }

    /// <summary>Chips to read (people), each pressable: returns the index pressed, or -1.</summary>
    public static int Chips(string id, IReadOnlyList<string> items, ChipKind kind = ChipKind.Plain)
    {
        var wrap = new Wrap(6);
        var pressed = -1;
        for (var i = 0; i < items.Count; i++)
        {
            var cw = Kit.ChipW(items[i]);
            var p = wrap.Next(MathF.Min(cw, Kit.Width), S(24));
            var (clicked, hovered, _) = Kit.Hit(Ids.Of(id, i), p, new Vector2(MathF.Min(cw, Kit.Width), S(24)));
            Kit.Chip(p, items[i], hovered ? ChipKind.Quiet : kind, maxWidth: Kit.Width);
            if (clicked) pressed = i;
        }
        wrap.End();
        return pressed;
    }

    /// <summary>A field across the box; true when it changed.</summary>
    public static bool Field(string id, ref string text, string hint, int max = 400, ImGuiInputTextFlags flags = ImGuiInputTextFlags.None, Icon? icon = null)
    {
        var at = Kit.At;
        var changed = Kit.Field(id, at, Kit.Width, ref text, hint, icon, max, flags);
        Kit.Reserve(at, Kit.Width, S(36));
        return changed;
    }

    /// <summary>
    /// A setting's row (13px 16px inside, a hairline between): its name and a line under it, its control on
    /// the right. Returns where the control goes (the row's room is already taken).
    /// </summary>
    public static Vector2 SetRow(int index, string name, string? note, float controlW, float controlH, bool disabled = false)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var dl = Kit.Dl;
        if (index > 0) dl.AddLine(new Vector2(at.X + 1, at.Y - 0.5f), new Vector2(at.X + w - 1, at.Y - 0.5f), Palette.U32(Palette.Line), 1f);
        var x = at.X + S(16);
        var wordsW = controlW <= 0 ? w - S(32) : w - S(32) - controlW - S(14);
        var nameH = Txt.Natural(Face.SansMedium, 14);
        var noteH = note != null ? Lines.Height(note, wordsW, Face.Sans, 12, 18) : 0;
        var wordsH = nameH + (note != null ? S(2) + noteH : 0);
        var h = S(13) * 2 + MathF.Max(wordsH, controlH);
        var top = at.Y + (h - wordsH) / 2;
        var old = Palette.Opacity;
        if (disabled) Palette.Opacity = old * 0.5f;
        Txt.Line(dl, new Vector2(x, top), Txt.Fit(name, Face.SansMedium, 14, wordsW), Face.SansMedium, 14, Palette.Ink);
        if (note != null) Lines.Draw(dl, new Vector2(x, top + nameH + S(2)), wordsW, note, Face.Sans, 12, Palette.Muted, 18);
        Palette.Opacity = old;
        Kit.Reserve(at, w, h);
        return new Vector2(at.X + w - S(16) - controlW, at.Y + (h - controlH) / 2);
    }

    public static bool SwitchRow(int index, string id, string name, string? note, bool on, bool disabled = false)
    {
        var p = SetRow(index, name, note, S(44), S(24), disabled);
        return Kit.Switch(id, p, on, disabled);
    }

    public static bool ButtonRow(int index, string id, string name, string? note, string label, Btn style = Btn.Glass, Icon? icon = null, bool disabled = false)
    {
        var p = SetRow(index, name, note, Kit.ButtonWidth(label, BtnSize.Tiny, icon, style), Kit.ButtonHeight(BtnSize.Tiny), disabled);
        return Kit.Button(id, p, label, style, BtnSize.Tiny, icon, 0, disabled);
    }

    public static int StepperRow(int index, string id, string name, string? note, string value, bool canDown, bool canUp)
    {
        var p = SetRow(index, name, note, Kit.StepperWidth(value), S(36));
        return Kit.Stepper(id, p, value, canDown, canUp);
    }

    public static int SegmentedRow(int index, string id, string name, string? note, IReadOnlyList<string> items, int selected)
    {
        var p = SetRow(index, name, note, Kit.SegmentedWidth(items), S(36));
        return Kit.Segmented(id, p, items, selected);
    }

    /// <summary>A progress bar across the box with words under it ("112 / 186 quests · 60%").</summary>
    public static void Bar(float fraction, string? words, Vector4? fill = null)
    {
        var at = Kit.At;
        Kit.Progress(at, Kit.Width, fraction, 6, fill);
        Kit.Reserve(at, Kit.Width, S(6));
        if (words == null) return;
        Kit.Gap(5);
        var a2 = Kit.At;
        Txt.Line(Kit.Dl, a2, Txt.Fit(words, Face.Mono, 11, Kit.Width), Face.Mono, 11, Palette.Muted, 16);
        Kit.Reserve(a2, Kit.Width, S(16));
    }

    /// <summary>A quote: a bar on the left in the quill's colour and the words beside it.</summary>
    public static void Quote(string text, string? by = null)
    {
        var at = Kit.At;
        var w = Kit.Width;
        var h = Lines.Draw(Kit.Dl, new Vector2(at.X + S(12), at.Y), w - S(12), text, Face.Sans, 14, Palette.Quill, 20.5f);
        if (by != null) h += Lines.Draw(Kit.Dl, new Vector2(at.X + S(12), at.Y + h + S(2)), w - S(12), by, Face.Sans, 12, Palette.Muted, 18) + S(2);
        Kit.Dl.AddRectFilled(at, at + new Vector2(S(2), h), Palette.U32(Palette.Quill), S(1));
        Kit.Reserve(at, w, h);
    }
}

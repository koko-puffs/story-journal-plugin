using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace StoryJournal.Game;

/// <summary>A game window's text as words (as Glamour Studio's AtkText reads it).</summary>
public static unsafe class AtkText
{
    public static bool IsText(AtkValueType type) => ((int)type & 0xF) is 8 or 0xA;

    public static string Text(byte* ptr)
    {
        if (ptr == null) return string.Empty;
        try
        {
            // Lumina's span reader: no copy of the bytes and no payload objects.
            return new Lumina.Text.ReadOnly.ReadOnlySeStringSpan(ptr).ExtractText().Trim();
        }
        catch
        {
            return Marshal.PtrToStringUTF8((nint)ptr) ?? string.Empty;
        }
    }
}

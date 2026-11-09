using System.Globalization;
using System.Text;

namespace StoryJournal.Core;

/// <summary>Roman numerals for chapters and volumes, lower case as the site sets them in small capitals ("iv"). Pure.</summary>
public static class Roman
{
    private static readonly (int V, string S)[] Parts =
    {
        (1000, "m"), (900, "cm"), (500, "d"), (400, "cd"), (100, "c"), (90, "xc"), (50, "l"), (40, "xl"), (10, "x"), (9, "ix"), (5, "v"), (4, "iv"), (1, "i"),
    };

    /// <summary>4 → "iv"; outside 1..3999, its digits.</summary>
    public static string Of(int n)
    {
        if (n < 1 || n > 3999) return n.ToString(CultureInfo.InvariantCulture);
        var s = new StringBuilder();
        foreach (var (v, r) in Parts)
            while (n >= v)
            {
                s.Append(r);
                n -= v;
            }
        return s.ToString();
    }
}

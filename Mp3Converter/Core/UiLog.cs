using System;

namespace Mp3Converter.Core
{
    /// <summary>Keeps the on-screen log from growing without bound during long FFmpeg jobs.</summary>
    public static class UiLog
    {
        public const int MaxChars = 12_000;

        public static string Append(string existing, string line)
        {
            string next = existing + line + Environment.NewLine;
            if (next.Length <= MaxChars) return next;

            int cut = next.Length - MaxChars / 2;
            int nl = next.IndexOf('\n', Math.Max(0, cut));
            if (nl < 0 || nl + 1 >= next.Length) nl = cut;
            return "… (eski log kırpıldı)" + Environment.NewLine + next[(nl + 1)..];
        }
    }
}

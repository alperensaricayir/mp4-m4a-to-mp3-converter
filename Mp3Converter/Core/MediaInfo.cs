using System;
using System.Collections.Generic;
using System.Linq;

namespace Mp3Converter.Core
{
    public sealed class MediaStreamInfo
    {
        public int Index { get; init; }
        public string CodecType { get; init; } = "";
        public string CodecName { get; init; } = "";
        public TimeSpan? Duration { get; init; }
        public TimeSpan? StartTime { get; init; }
        public int? SampleRate { get; init; }
        public int? Channels { get; init; }
        public int? Width { get; init; }
        public int? Height { get; init; }

        public bool IsVideo => CodecType.Equals("video", StringComparison.OrdinalIgnoreCase);
        public bool IsAudio => CodecType.Equals("audio", StringComparison.OrdinalIgnoreCase);

        public override string ToString()
        {
            var parts = new List<string> { $"#{Index} {CodecType} {CodecName}" };
            if (Width.HasValue && Height.HasValue) parts.Add($"{Width}x{Height}");
            if (SampleRate.HasValue) parts.Add($"{SampleRate} Hz");
            if (Channels.HasValue) parts.Add($"{Channels} ch");
            if (StartTime.HasValue) parts.Add($"start={StartTime.Value.TotalSeconds:0.###}s");
            if (Duration.HasValue) parts.Add($"dur={Duration.Value:hh\\:mm\\:ss\\.fff}");
            return string.Join(", ", parts);
        }
    }

    public sealed class MediaInfo
    {
        public string FilePath { get; init; } = "";
        public string FormatName { get; init; } = "";
        public TimeSpan? Duration { get; init; }
        public TimeSpan? StartTime { get; init; }
        public IReadOnlyList<MediaStreamInfo> Streams { get; init; } = Array.Empty<MediaStreamInfo>();

        public MediaStreamInfo? FirstVideo => Streams.FirstOrDefault(s => s.IsVideo && !IsCoverArt(s));
        public MediaStreamInfo? FirstAudio => Streams.FirstOrDefault(s => s.IsAudio);

        public bool HasVideo => FirstVideo != null;
        public bool HasAudio => FirstAudio != null;

        /// <summary>Effective duration: format duration, else longest stream duration.</summary>
        public TimeSpan EffectiveDuration =>
            Duration ?? Streams.Where(s => s.Duration.HasValue).Select(s => s.Duration!.Value).DefaultIfEmpty(TimeSpan.Zero).Max();

        /// <summary>Embedded cover art in audio files is reported as a video stream (mjpeg/png); ignore it.</summary>
        private static bool IsCoverArt(MediaStreamInfo s) =>
            s.CodecName is "mjpeg" or "png" or "bmp" or "gif" && s.Duration is null;
    }
}

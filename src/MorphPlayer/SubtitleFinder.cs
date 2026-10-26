using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace MorphPlayer;

/// <summary>A subtitle file found for a video, with a readable name for the menu.</summary>
public sealed record SubtitleMatch(string Path, string Label, int Score, bool Forced, string? Language);

/// <summary>
/// Finds the subtitle files that belong to a video, beyond LibVLC 3's own matching, which only finds names that
/// contain the video's whole file name. Handles other releases of the same film, "1x02"-style episode names, and
/// Subs folders, and names each match by the language and flags at the end of its file name.
/// </summary>
public static class SubtitleFinder
{
    public static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".sub", ".vtt", ".idx", ".smi",
    };

    static readonly string[] SubsFolderNames = { "subs", "sub", "subtitles", "subtitle" };

    // Words that end a title in a release name: quality, source, codec and edition tags.
    static readonly HashSet<string> ReleaseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "480p", "576p", "720p", "1080p", "1080i", "2160p", "4k", "uhd", "bluray", "blu", "bdrip", "brrip", "bdremux",
        "remux", "web", "webrip", "webdl", "dl", "hdtv", "hdrip", "dvdrip", "dvd", "dvdscr", "ts", "cam", "x264",
        "x265", "h264", "h265", "hevc", "avc", "xvid", "divx", "10bit", "hdr", "hdr10", "atmos", "aac", "ac3", "dts",
        "ddp5", "proper", "repack", "extended", "unrated", "remastered", "internal", "limited", "amzn", "nf", "dsnp",
    };

    static readonly Regex EpisodeCode = new(@"^(?:s(?<s>\d{1,2})e(?<e>\d{1,3})|(?<s>\d{1,2})x(?<e>\d{2,3}))$", RegexOptions.IgnoreCase);
    static readonly Regex Separators = new(@"[\s._\-\[\](){}+,]+");

    static readonly Dictionary<string, string> Languages = BuildLanguageTable();

    public static IReadOnlyList<SubtitleMatch> Find(string videoPath, Func<string, bool> isVideoFile)
    {
        var folder = Path.GetDirectoryName(videoPath);
        if (folder == null || !Directory.Exists(folder)) return Array.Empty<SubtitleMatch>();
        var videoName = Path.GetFileNameWithoutExtension(videoPath);
        var video = NameInfo.Parse(videoName);
        var onlyVideo = Directory.EnumerateFiles(folder).Count(isVideoFile) == 1;

        var matches = new List<SubtitleMatch>();
        foreach (var file in SubtitleFiles(folder))
            if (Score(Path.GetFileNameWithoutExtension(file), videoName, video) is { } match)
                matches.Add(Describe(file, match.Score, match.Words));

        foreach (var subs in Directory.EnumerateDirectories(folder).Where(d => SubsFolderNames.Contains(Path.GetFileName(d), StringComparer.OrdinalIgnoreCase)))
        {
            // Subs\<video name>\2_English.srt: for this video only, whatever else is in the folder.
            var own = Path.Combine(subs, videoName);
            if (Directory.Exists(own))
                matches.AddRange(SubtitleFiles(own).Select(f => Describe(f, 80, Tokens(Path.GetFileNameWithoutExtension(f)))));
            // Subs\2_English.srt: named for no video in particular, so only when this is the folder's only video.
            foreach (var file in SubtitleFiles(subs))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (Score(name, videoName, video) is { } match)
                    matches.Add(Describe(file, match.Score, match.Words));
                else if (onlyVideo && NameInfo.Parse(name).Episode == null)
                    matches.Add(Describe(file, 50, Tokens(name)));
            }
        }

        // Two files can describe the same language ("Movie.en.srt", "Subs\2_English.srt"): tell them apart by file name.
        return matches
            .GroupBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .SelectMany(g => g.Count() == 1 ? g : g.Select(m => m with { Label = $"{m.Label} · {System.IO.Path.GetFileName(m.Path)}" }))
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.Label, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Names a subtitle file the user picked themselves.</summary>
    public static SubtitleMatch Describe(string subtitlePath, string? videoPath)
    {
        var name = Path.GetFileNameWithoutExtension(subtitlePath);
        var videoName = videoPath == null ? null : Path.GetFileNameWithoutExtension(videoPath);
        var rest = videoName != null && name.StartsWith(videoName, StringComparison.OrdinalIgnoreCase)
            ? Tokens(name[videoName.Length..])
            : Tokens(name);
        return Describe(subtitlePath, 100, rest);
    }

    static IEnumerable<string> SubtitleFiles(string folder) =>
        Directory.EnumerateFiles(folder)
            .Where(f => SubtitleExtensions.Contains(Path.GetExtension(f)))
            // A VobSub pair is opened through its .idx; the .sub beside it holds the pictures.
            .Where(f => !Path.GetExtension(f).Equals(".sub", StringComparison.OrdinalIgnoreCase)
                        || !File.Exists(Path.ChangeExtension(f, ".idx")));

    /// <summary>How surely a subtitle file name belongs to the video, and the words after the shared part.</summary>
    static (int Score, string[] Words)? Score(string subtitleName, string videoName, NameInfo video)
    {
        if (subtitleName.Equals(videoName, StringComparison.OrdinalIgnoreCase))
            return (100, Array.Empty<string>());
        if (subtitleName.StartsWith(videoName, StringComparison.OrdinalIgnoreCase) && Separators.IsMatch(subtitleName[videoName.Length].ToString()))
            return (90, Tokens(subtitleName[videoName.Length..]));

        var subtitle = NameInfo.Parse(subtitleName);
        if (video.Title.Length == 0 || !subtitle.Title.SequenceEqual(video.Title, StringComparer.OrdinalIgnoreCase)) return null;

        if (video.Episode != null || subtitle.Episode != null)
            return video.Episode == subtitle.Episode ? (70, subtitle.Rest) : null;
        if (video.Year != null && subtitle.Year != null && video.Year != subtitle.Year) return null; // a remake
        return (60, subtitle.Rest);
    }

    static SubtitleMatch Describe(string path, int score, string[] rest)
    {
        // The language and flags sit at the end of a subtitle name: "…eng.forced", "… - German SDH", "2_English".
        // Reading back from the end, and stopping at the first other word, keeps episode titles ("It Begins") and
        // release groups from being mistaken for languages.
        string? language = null;
        bool sdh = false, forced = false, maybeHindi = false;
        for (var i = rest.Length - 1; i >= 0; i--)
        {
            var word = rest[i].ToLowerInvariant();
            if (word is "sdh" or "cc") sdh = true;
            else if (word is "forced" or "foreign") forced = true;
            else if (word == "hi") maybeHindi = true; // "hearing impaired" beside a language, Hindi on its own
            else if (word == "br" && i > 0 && rest[i - 1].Equals("pt", StringComparison.OrdinalIgnoreCase))
            {
                language ??= "Portuguese (Brazil)";
                i--;
            }
            else if (Languages.TryGetValue(word, out var name)) language ??= name;
            else break;
        }
        if (maybeHindi)
        {
            if (language != null) sdh = true;
            else language = "Hindi";
        }

        var flags = new[] { sdh ? "SDH" : null, forced ? "Forced" : null }.Where(f => f != null).ToArray();
        var label = language ?? System.IO.Path.GetFileName(path);
        if (flags.Length > 0) label += $" ({string.Join(", ", flags)})";
        return new SubtitleMatch(path, label, score, forced, language);
    }

    static string[] Tokens(string name) => Separators.Split(name).Where(t => t.Length > 0).ToArray();

    /// <summary>A release-style name split into its title, year, episode and the words after them.</summary>
    sealed record NameInfo(string[] Title, string? Year, (int Season, int Episode)? Episode, string[] Rest)
    {
        public static NameInfo Parse(string name)
        {
            var tokens = Tokens(name);
            string? year = null;
            (int, int)? episode = null;
            var titleEnd = tokens.Length;
            for (var i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                if (EpisodeCode.Match(token) is { Success: true } code)
                {
                    episode ??= (int.Parse(code.Groups["s"].Value), int.Parse(code.Groups["e"].Value));
                    titleEnd = Math.Min(titleEnd, i);
                }
                // A year that isn't the first word ("2012" can be a title) marks the end of the title.
                else if (i > 0 && token.Length == 4 && int.TryParse(token, out var y) && y is >= 1900 and <= 2099)
                {
                    year ??= token;
                    titleEnd = Math.Min(titleEnd, i);
                }
                else if (ReleaseWords.Contains(token))
                {
                    titleEnd = Math.Min(titleEnd, i);
                }
            }
            return new NameInfo(tokens[..titleEnd], year, episode, tokens[titleEnd..]);
        }
    }

    static Dictionary<string, string> BuildLanguageTable()
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var culture in CultureInfo.GetCultures(CultureTypes.NeutralCultures))
        {
            if (culture.TwoLetterISOLanguageName.Length != 2 || culture.Equals(CultureInfo.InvariantCulture)) continue;
            var name = culture.EnglishName.Split(" (")[0];
            table.TryAdd(culture.TwoLetterISOLanguageName, name);
            if (culture.ThreeLetterISOLanguageName.Length == 3) table.TryAdd(culture.ThreeLetterISOLanguageName, name);
            if (!name.Contains(' ')) table.TryAdd(name, name);
        }
        // The older "bibliographic" codes, still common in subtitle names, and a few usual spellings.
        foreach (var (code, name) in new[]
        {
            ("fre", "French"), ("ger", "German"), ("dut", "Dutch"), ("chi", "Chinese"), ("cze", "Czech"), ("gre", "Greek"),
            ("rum", "Romanian"), ("per", "Persian"), ("may", "Malay"), ("alb", "Albanian"), ("arm", "Armenian"),
            ("baq", "Basque"), ("bur", "Burmese"), ("geo", "Georgian"), ("ice", "Icelandic"), ("mac", "Macedonian"),
            ("slo", "Slovak"), ("wel", "Welsh"), ("sinhalese", "Sinhala"), ("farsi", "Persian"), ("brazilian", "Portuguese (Brazil)"),
            ("ptbr", "Portuguese (Brazil)"), ("pob", "Portuguese (Brazil)"), ("chs", "Chinese (Simplified)"),
            ("cht", "Chinese (Traditional)"), ("latino", "Spanish (Latin America)"),
        })
            table[code] = name;
        return table;
    }
}

using System.Text;
using System.Text.RegularExpressions;

namespace Glint.Tuning;

/// <summary>What "Tune with AI" optimises for. Kind: quiet | balanced | performance | custom (Text = the user's words, sanitised).
/// Target: which hardware the run tunes (gpu | cpu | all).</summary>
public sealed record TuningGoal(string Kind, string? Text, string Target)
{
    public static readonly string[] Kinds = { "quiet", "balanced", "performance", "custom" };
    public const int MaxText = 300, MaxContext = 1000;

    /// <summary>The Glint profile the AI works in for this goal. Re-tuning the same goal reuses it.</summary>
    public string ProfileName => "AI · " + char.ToUpperInvariant(Kind[0]) + Kind[1..];
    public bool Gpu => Target is "gpu" or "all";
    public bool Cpu => Target is "cpu" or "all";

    /// <summary>What the goal means, in the words the AI is given (also shown by tuning.fans.status).</summary>
    public string Objective => Kind switch
    {
        "quiet" => "QUIET: the lowest fan duty that stays stable and under the temperature target, with an efficient overclock (performance at or above stock; a lower power limit or smaller offsets are fine if they cut heat and noise).",
        "performance" => "PERFORMANCE: fans ramp harder to keep temperatures down, giving the highest sustained boost: power unlocked and the highest overclock that is fully stable.",
        "custom" => "CUSTOM: " + (Text ?? "(no text)") + " (trade performance, stability, temperature and fan noise accordingly; the safety rules still apply).",
        _ => "BALANCED: the best performance per unit of fan noise: close to the performance result but with clearly lower fan duty.",
    };

    /// <summary>Parse the goal arguments of tuning.ai.start. "gpu" | "cpu" | "all" alone is the old goal-less run (null goal).</summary>
    public static (TuningGoal? Goal, string Target) Parse(string? goal, string? text, string? target)
    {
        var g = (goal ?? "").Trim().ToLowerInvariant();
        var t = (target ?? "").Trim().ToLowerInvariant();
        if (g is "gpu" or "cpu" or "all") { if (t == "") t = g; g = ""; }
        if (t == "") t = "gpu";
        if (t is not ("gpu" or "cpu" or "all")) throw new ArgumentException("target must be gpu, cpu or all");
        if (g == "") return (null, t);
        if (!Kinds.Contains(g)) throw new ArgumentException("goal must be quiet, balanced, performance or custom");
        string? ct = null;
        if (g == "custom")
        {
            ct = Clean(text, MaxText);
            if (ct.Length == 0) throw new ArgumentException("a custom goal needs a description of the result you want");
        }
        return (new TuningGoal(g, ct, t), t);
    }

    /// <summary>Strip control characters, collapse whitespace, neutralise runs that could fake a prompt delimiter, cap the length.</summary>
    public static string Clean(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new StringBuilder(Math.Min(s.Length, max * 2));
        foreach (var ch in s.Length > max * 4 ? s[..(max * 4)] : s)
            sb.Append(char.IsControl(ch) || char.IsWhiteSpace(ch) || char.GetUnicodeCategory(ch) == System.Globalization.UnicodeCategory.Format ? ' ' : ch);
        var r = Regex.Replace(sb.ToString(), @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(1)).Trim();
        r = Regex.Replace(r, "-{3,}", "-", RegexOptions.None, TimeSpan.FromSeconds(1));
        return r.Length > max ? r[..max].TrimEnd() : r;
    }
}

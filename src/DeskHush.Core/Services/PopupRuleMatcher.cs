using System.Text.RegularExpressions;
using DeskHush.Core.Models;

namespace DeskHush.Core.Services;

public static partial class PopupRuleMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static bool IsMatch(PopupRule rule, WindowInfo window)
    {
        if (!rule.IsEnabled)
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.ProcessName) &&
            !string.Equals(NormalizeProcessName(rule.ProcessName), NormalizeProcessName(window.ProcessName), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.ProcessPath) &&
            !string.Equals(NormalizePath(rule.ProcessPath), NormalizePath(window.ProcessPath), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.WindowClass) &&
            !WildcardMatch(window.ClassName, rule.WindowClass))
        {
            return false;
        }

        return string.IsNullOrWhiteSpace(rule.TitlePattern) ||
               MatchText(window.Title, rule.TitlePattern, rule.TitleMatchMode);
    }

    public static bool MatchText(string input, string pattern, TextMatchMode mode)
    {
        input ??= string.Empty;
        pattern ??= string.Empty;

        return mode switch
        {
            TextMatchMode.Exact => string.Equals(input, pattern, StringComparison.OrdinalIgnoreCase),
            TextMatchMode.Contains => input.Contains(pattern, StringComparison.OrdinalIgnoreCase),
            TextMatchMode.Wildcard => WildcardMatch(input, pattern),
            TextMatchMode.Regex => Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout),
            _ => false
        };
    }

    public static bool WildcardMatch(string input, string pattern)
    {
        var escaped = Regex.Escape(pattern)
            .Replace("\\*", ".*", StringComparison.Ordinal)
            .Replace("\\?", ".", StringComparison.Ordinal);

        return Regex.IsMatch(input ?? string.Empty, $"^{escaped}$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);
    }

    private static string NormalizeProcessName(string value) =>
        Path.GetFileNameWithoutExtension(value.Trim());

    private static string NormalizePath(string value) =>
        value.Trim().Trim('"').Replace('/', '\\');
}

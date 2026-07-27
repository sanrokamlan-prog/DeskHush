using DeskHush.Core.Models;

namespace DeskHush.Core.Services;

public static class PopupRuleValidator
{
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "csrss",
        "deskhush",
        "dwm",
        "explorer",
        "fontdrvhost",
        "lsass",
        "services",
        "sihost",
        "smss",
        "wininit",
        "winlogon"
    };

    public static OperationResult Validate(PopupRule rule)
    {
        if (string.IsNullOrWhiteSpace(rule.Name))
        {
            return OperationResult.Failure("规则名称不能为空。");
        }

        if (string.IsNullOrWhiteSpace(rule.ProcessName) && string.IsNullOrWhiteSpace(rule.ProcessPath))
        {
            return OperationResult.Failure("规则必须限定到具体程序。");
        }

        if (string.IsNullOrWhiteSpace(rule.TitlePattern) && string.IsNullOrWhiteSpace(rule.WindowClass))
        {
            return OperationResult.Failure("规则必须指定窗口标题或窗口类，防止误关主窗口。");
        }

        if (IsProtectedProcess(rule.ProcessName, rule.ProcessPath))
        {
            return OperationResult.Failure("系统关键进程不能加入弹窗拦截规则。");
        }

        if (rule.TitleMatchMode == TextMatchMode.Regex)
        {
            try
            {
                _ = PopupRuleMatcher.MatchText(string.Empty, rule.TitlePattern, TextMatchMode.Regex);
            }
            catch (ArgumentException)
            {
                return OperationResult.Failure("正则表达式无效。");
            }
        }

        return OperationResult.Success();
    }

    public static bool IsProtectedProcess(string? processName, string? processPath = null)
    {
        return IsProtectedProcessValue(processName) || IsProtectedProcessValue(processPath);
    }

    private static bool IsProtectedProcessValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(value.Trim().Trim('"'));
        return ProtectedProcesses.Contains(name);
    }
}

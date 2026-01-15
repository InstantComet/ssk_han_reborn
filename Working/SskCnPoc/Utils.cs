using System.Text.RegularExpressions;

namespace SskCnPoc;

/// <summary>
/// 通用工具方法
/// </summary>
internal static class Utils
{
    private static readonly Regex MonthRegex = new(
        @"\b(Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// 检查字符串是否包含中文字符
    /// </summary>
    public static bool ContainsChinese(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        }
        return false;
    }

    /// <summary>
    /// 检查文本是否包含英文月份名
    /// </summary>
    public static bool HasEnglishMonth(string text) => 
        !string.IsNullOrEmpty(text) && MonthRegex.IsMatch(text);
}

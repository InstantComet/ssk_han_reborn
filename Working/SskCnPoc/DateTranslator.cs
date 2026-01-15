using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace SskCnPoc;

/// <summary>
/// 日期翻译器：将英文日期格式翻译为中文日期格式
/// 支持格式：
/// - "March 14th, 1905" -> "1905年3月14日"
/// - "14th Mar 1905" -> "1905年3月14日"
/// - "March 14, 1905" -> "1905年3月14日"
/// - "14 March 1905" -> "1905年3月14日"
/// </summary>
internal static class DateTranslator
{
    // 英文月份到中文月份的映射
    private static readonly Dictionary<string, string> MonthMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "January", "1月" },   { "Jan", "1月" },
        { "February", "2月" },  { "Feb", "2月" },
        { "March", "3月" },     { "Mar", "3月" },
        { "April", "4月" },     { "Apr", "4月" },
        { "May", "5月" },
        { "June", "6月" },      { "Jun", "6月" },
        { "July", "7月" },      { "Jul", "7月" },
        { "August", "8月" },    { "Aug", "8月" },
        { "September", "9月" }, { "Sep", "9月" }, { "Sept", "9月" },
        { "October", "10月" },  { "Oct", "10月" },
        { "November", "11月" }, { "Nov", "11月" },
        { "December", "12月" }, { "Dec", "12月" }
    };

    // 匹配序数后缀 (1st, 2nd, 3rd, 4th, 11th, 12th, 13th, 21st, 22nd, 23rd, etc.)
    private const string OrdinalPattern = @"(\d{1,2})(?:st|nd|rd|th)";
    
    // 月份名称模式（全称或缩写）
    private const string MonthPattern = @"(January|February|March|April|May|June|July|August|September|October|November|December|Jan|Feb|Mar|Apr|Jun|Jul|Aug|Sep|Sept|Oct|Nov|Dec)";
    
    // 年份模式（4位数字）
    private const string YearPattern = @"(\d{4})";

    // 日期模式1: "Month Day(th), Year" - 如 "March 14th, 1905" 或 "March 17th, 1905"
    private static readonly Regex Pattern1 = new(
        $@"^\s*{MonthPattern}\s+{OrdinalPattern},\s+{YearPattern}\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // 日期模式1b: "Month Day, Year" - 如 "March 14, 1905"（无序数后缀）
    private static readonly Regex Pattern1b = new(
        $@"^\s*{MonthPattern}\s+(\d{{1,2}}),\s+{YearPattern}\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 日期模式2: "Day(th) Month Year" - 如 "14th Mar 1905" 或 "14 March 1905"
    private static readonly Regex Pattern2 = new(
        $@"^\s*{OrdinalPattern}\s+{MonthPattern}\s+{YearPattern}\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // 日期模式2b: "Day Month Year" - 如 "14 March 1905"（无序数后缀）
    private static readonly Regex Pattern2b = new(
        $@"^\s*(\d{{1,2}})\s+{MonthPattern}\s+{YearPattern}\s*$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // 内联日期模式 - 用于在任意文本中查找并替换日期
    // "Month Day(th), Year" 格式
    private static readonly Regex InlinePattern1 = new(
        $@"{MonthPattern}\s+{OrdinalPattern},\s+{YearPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);
    
    // "Month Day, Year" 格式（无序数后缀）
    private static readonly Regex InlinePattern1b = new(
        $@"{MonthPattern}\s+(\d{{1,2}}),\s+{YearPattern}",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 缓存已翻译的日期
    private static readonly Dictionary<string, string> Cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> NoMatch = new(StringComparer.Ordinal);
    private static readonly object CacheLock = new();

    /// <summary>
    /// 尝试翻译日期文本
    /// </summary>
    /// <returns>翻译后的日期，如果不是日期格式则返回 null</returns>
    public static string? TryTranslate(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        
        // 快速排除：日期一般20-30字符以内
        if (text.Length > 50) return null;
        
        // 快速排除：日期必须包含数字
        bool hasDigit = false;
        foreach (char c in text)
        {
            if (char.IsDigit(c))
            {
                hasDigit = true;
                break;
            }
        }
        if (!hasDigit) return null;

        lock (CacheLock)
        {
            if (Cache.TryGetValue(text, out var cached))
            {
                return cached;
            }
            if (NoMatch.Contains(text))
            {
                return null;
            }
        }

        string? result = TryParseAndTranslate(text);
        
        lock (CacheLock)
        {
            if (result != null)
            {
                Cache[text] = result;
            }
            else
            {
                NoMatch.Add(text);
            }
        }
        
        return result;
    }

    private static string? TryParseAndTranslate(string text)
    {
        // 尝试模式1: "March 14th, 1905"
        var match = Pattern1.Match(text);
        if (match.Success)
        {
            string month = match.Groups[1].Value;
            string day = match.Groups[2].Value;
            string year = match.Groups[3].Value;
            return FormatChineseDate(year, month, day);
        }
        
        // 尝试模式1b: "March 14, 1905"
        match = Pattern1b.Match(text);
        if (match.Success)
        {
            string month = match.Groups[1].Value;
            string day = match.Groups[2].Value;
            string year = match.Groups[3].Value;
            return FormatChineseDate(year, month, day);
        }

        // 尝试模式2: "14th Mar 1905"
        match = Pattern2.Match(text);
        if (match.Success)
        {
            string day = match.Groups[1].Value;
            string month = match.Groups[2].Value;
            string year = match.Groups[3].Value;
            return FormatChineseDate(year, month, day);
        }
        
        // 尝试模式2b: "14 March 1905"
        match = Pattern2b.Match(text);
        if (match.Success)
        {
            string day = match.Groups[1].Value;
            string month = match.Groups[2].Value;
            string year = match.Groups[3].Value;
            return FormatChineseDate(year, month, day);
        }

        return null;
    }

    /// <summary>
    /// 格式化为中文日期: "1905年3月14日"
    /// </summary>
    private static string? FormatChineseDate(string year, string month, string day)
    {
        if (!MonthMap.TryGetValue(month, out var zhMonth))
        {
            return null;
        }
        
        // 去除日期前导零
        if (day.StartsWith('0') && day.Length > 1)
        {
            day = day.TrimStart('0');
        }
        
        return $"{year}年{zhMonth}{day}日";
    }

    /// <summary>
    /// 处理带富文本标签的日期，如 "<size=75%>March 14th, 1905</size>"
    /// </summary>
    public static string? TryTranslateWithTags(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        
        // DEBUG: 记录包含月份名的文本
        if (text.Contains("March") || text.Contains("January") || text.Contains("February") || 
            text.Contains("April") || text.Contains("May") || text.Contains("June") ||
            text.Contains("July") || text.Contains("August") || text.Contains("September") ||
            text.Contains("October") || text.Contains("November") || text.Contains("December"))
        {
            Plugin.LogSrc.LogInfo($"[DATE-DEBUG] Input text with month: '{text}'");
        }
        
        // 检查缓存
        lock (CacheLock)
        {
            if (Cache.TryGetValue(text, out var cached))
            {
                return cached;
            }
            if (NoMatch.Contains(text))
            {
                return null;
            }
        }

        // 尝试直接翻译（无标签）
        var directResult = TryParseAndTranslate(text);
        if (directResult != null)
        {
            lock (CacheLock)
            {
                Cache[text] = directResult;
            }
            return directResult;
        }

        // 尝试处理带标签的情况
        // 匹配 <tag>date</tag> 或 <tag=value>date</tag> 格式
        var tagMatch = Regex.Match(text, @"^(<[^>]+>)(.+?)(</[^>]+>)$");
        if (tagMatch.Success)
        {
            string openTag = tagMatch.Groups[1].Value;
            string content = tagMatch.Groups[2].Value;
            string closeTag = tagMatch.Groups[3].Value;
            
            var contentTranslated = TryParseAndTranslate(content);
            if (contentTranslated != null)
            {
                string result = $"{openTag}{contentTranslated}{closeTag}";
                lock (CacheLock)
                {
                    Cache[text] = result;
                }
                return result;
            }
        }
        
        // 尝试内联日期替换
        var inlineResult = TryTranslateInline(text);
        if (inlineResult != null)
        {
            lock (CacheLock)
            {
                Cache[text] = inlineResult;
            }
            return inlineResult;
        }

        lock (CacheLock)
        {
            NoMatch.Add(text);
        }
        return null;
    }
    
    /// <summary>
    /// 在文本中查找并替换所有日期
    /// </summary>
    private static string? TryTranslateInline(string text)
    {
        string result = text;
        bool hasMatch = false;
        
        // 替换 "Month Day(th), Year" 格式
        result = InlinePattern1.Replace(result, m =>
        {
            hasMatch = true;
            string month = m.Groups[1].Value;
            string day = m.Groups[2].Value;
            string year = m.Groups[3].Value;
            return FormatChineseDate(year, month, day) ?? m.Value;
        });
        
        // 替换 "Month Day, Year" 格式（无序数后缀）
        result = InlinePattern1b.Replace(result, m =>
        {
            hasMatch = true;
            string month = m.Groups[1].Value;
            string day = m.Groups[2].Value;
            string year = m.Groups[3].Value;
            return FormatChineseDate(year, month, day) ?? m.Value;
        });
        
        return hasMatch ? result : null;
    }
}

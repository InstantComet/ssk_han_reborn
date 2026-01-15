using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using BepInEx;

namespace SskCnPoc;

/// <summary>
/// 缺失翻译收集器：收集未翻译的文本并保存到文件
/// </summary>
internal static class MissingCollector
{
    private static readonly HashSet<string> _missing = new(StringComparer.Ordinal);
    private static readonly object _lock = new();
    private static DateTime _lastSaveTime = DateTime.MinValue;
    private static readonly TimeSpan _saveInterval = TimeSpan.FromSeconds(10);
    
    // 常见的动态参数模式（用于规范化收集）
    private static readonly (string prefix, string suffix)[] DynamicPatterns = new[]
    {
        ("(", "%):"),
        ("(", "%)"),
        (" v", ""),
    };

    /// <summary>
    /// 收集未翻译的文本
    /// </summary>
    public static void Collect(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        if (!ShouldCollect(text)) return;
        
        // 跳过已经是中文的文本
        if (text.Any(c => c >= 0x4E00 && c <= 0x9FFF)) return;
        
        // 规范化动态文本
        string normalizedText = NormalizeDynamicText(text);
        
        lock (_lock)
        {
            if (_missing.Add(normalizedText))
            {
                Plugin.LogSrc.LogDebug($"[MISSING] '{normalizedText}'");
                
                if (DateTime.Now - _lastSaveTime > _saveInterval)
                {
                    SaveToFile();
                    _lastSaveTime = DateTime.Now;
                }
            }
        }
    }

    private static bool ShouldCollect(string s)
    {
        if (s.All(char.IsWhiteSpace)) return false;
        if (s.Length <= 1) return false;
        if (IsResolutionFormat(s)) return false;

        bool allDigitOrPunct = true;
        foreach (char c in s)
        {
            if (char.IsLetter(c) || c > 0x7F) { allDigitOrPunct = false; break; }
            if (char.IsDigit(c)) continue;
            if (char.IsPunctuation(c) || char.IsSymbol(c) || char.IsWhiteSpace(c)) continue;
            allDigitOrPunct = false; break;
        }
        return !allDigitOrPunct;
    }

    private static bool IsResolutionFormat(string s)
    {
        int xIndex = s.IndexOf('x');
        if (xIndex < 0) xIndex = s.IndexOf('×');
        if (xIndex <= 0 || xIndex >= s.Length - 1) return false;

        for (int i = 0; i < xIndex; i++)
        {
            if (!char.IsDigit(s[i])) return false;
        }

        for (int i = xIndex + 1; i < s.Length; i++)
        {
            if (!char.IsDigit(s[i])) return false;
        }

        return true;
    }

    /// <summary>
    /// 规范化动态文本：将动态参数替换为 {0} 占位符
    /// </summary>
    private static string NormalizeDynamicText(string text)
    {
        foreach (var (prefix, suffix) in DynamicPatterns)
        {
            int prefixIdx = text.IndexOf(prefix, StringComparison.Ordinal);
            if (prefixIdx < 0) continue;
            
            int suffixIdx = suffix.Length > 0 
                ? text.IndexOf(suffix, prefixIdx + prefix.Length, StringComparison.Ordinal)
                : -1;
            
            int paramStart = prefixIdx + prefix.Length;
            int paramEnd = suffixIdx >= 0 ? suffixIdx : text.Length;
            
            if (paramEnd > paramStart && paramEnd - paramStart <= 20)
            {
                bool isValidParam = true;
                for (int i = paramStart; i < paramEnd; i++)
                {
                    char c = text[i];
                    if (!char.IsDigit(c) && c != '.' && !char.IsLetter(c))
                    {
                        isValidParam = false;
                        break;
                    }
                }
                
                if (isValidParam)
                {
                    string before = text.Substring(0, paramStart);
                    string after = suffixIdx >= 0 ? text.Substring(suffixIdx) : "";
                    return before + "{0}" + after;
                }
            }
        }
        
        return text;
    }

    private static void SaveToFile()
    {
        try
        {
            var missingPath = Path.Combine(Paths.PluginPath, "ssk_cn_missing.txt");
            var lines = _missing.OrderBy(s => s).Select(s => $"{s}=");
            File.WriteAllLines(missingPath, lines, Encoding.UTF8);
            Plugin.LogSrc.LogInfo($"[MISSING] Saved {_missing.Count} untranslated texts to ssk_cn_missing.txt");
        }
        catch (Exception ex)
        {
            Plugin.LogSrc.LogWarning($"Failed to save missing translations: {ex.Message}");
        }
    }
}

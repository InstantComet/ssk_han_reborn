using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;

namespace SskCnPoc;

/// <summary>
/// 翻译管理器：负责加载翻译、匹配翻译
/// </summary>
internal static class TranslationManager
{
    // 精确匹配字典
    public static Dictionary<string, string> Map { get; } = new(StringComparer.Ordinal);
    
    // 模板匹配：按前缀首字母分组，提高查找效率
    public static Dictionary<char, List<TemplateEntry>> TemplatesByFirstChar { get; } = new();
    
    // 前缀为空的模板（{0} 在句首），按后缀匹配
    public static List<TemplateEntry> TemplatesWithEmptyPrefix { get; } = new();
    
    // 已匹配的模板缓存：避免重复匹配相同的动态文本
    private static readonly Dictionary<string, string> _templateMatchCache = new(StringComparer.Ordinal);
    
    // 已确认不匹配任何模板的文本
    private static readonly HashSet<string> _noTemplateMatch = new(StringComparer.Ordinal);
    
    private static readonly object _cacheLock = new();

    /// <summary>
    /// 从配置文件加载翻译
    /// </summary>
    public static void LoadTranslations()
    {
        string path = Path.Combine(Paths.PluginPath, "ssk_cn.txt");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, @"# 精确匹配
New Game=新游戏
# 模板匹配（使用 {0} 作为参数占位符）
# Music Volume ({0}):=音乐音量 ({0}):
", Encoding.UTF8);
        }

        int exactCount = 0;
        int templateCount = 0;

        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.TrimStart().StartsWith('#')) continue;
            
            int idx = line.IndexOf('=');
            if (idx <= 0) continue;

            string en = line.Substring(0, idx).Trim();
            string zh = line[(idx + 1)..].Trim();

            if (en.Length == 0 || zh.Length == 0) continue;
            
            if (en.Contains("{0}") && zh.Contains("{0}"))
            {
                // 解析模板
                int placeholderIdx = en.IndexOf("{0}");
                string prefix = en.Substring(0, placeholderIdx);
                string suffix = en.Substring(placeholderIdx + 3);
                
                if (prefix.Length > 0)
                {
                    // 有前缀，按首字符索引
                    char firstChar = prefix[0];
                    if (!TemplatesByFirstChar.TryGetValue(firstChar, out var list))
                    {
                        list = new List<TemplateEntry>();
                        TemplatesByFirstChar[firstChar] = list;
                    }
                    list.Add(new TemplateEntry(prefix, suffix, zh));
                    templateCount++;
                }
                else if (suffix.Length > 0)
                {
                    // 前缀为空（{0}在句首），存入特殊列表
                    TemplatesWithEmptyPrefix.Add(new TemplateEntry(prefix, suffix, zh));
                    templateCount++;
                    Plugin.LogSrc.LogInfo($"Loaded empty-prefix template: suffix='{suffix}'");
                }
            }
            else
            {
                Map[en] = zh;
                exactCount++;
            }
        }

        Plugin.LogSrc.LogInfo($"Loaded translations: {exactCount} exact, {templateCount} templates");
    }

    /// <summary>
    /// 尝试翻译文本
    /// </summary>
    /// <returns>翻译后的文本，如果没有匹配则返回 null</returns>
    public static string? TryTranslate(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        
        // 1. 优先精确匹配（O(1) 哈希查找）
        if (Map.TryGetValue(text, out var zh))
        {
            return zh;
        }
        
        // 2. 检查模板匹配缓存
        lock (_cacheLock)
        {
            if (_templateMatchCache.TryGetValue(text, out var cachedZh))
            {
                return cachedZh;
            }
            
            // 3. 检查是否已确认无模板匹配
            if (_noTemplateMatch.Contains(text))
            {
                return null;
            }
        }
        
        // 4. 尝试模板匹配
        return TryMatchTemplate(text);
    }

    /// <summary>
    /// 高效的模板匹配：使用首字符索引快速定位候选模板
    /// </summary>
    private static string? TryMatchTemplate(string text)
    {
        // 1. 先尝试按首字符索引的模板
        if (TemplatesByFirstChar.Count > 0)
        {
            char firstChar = text[0];
            
            if (TemplatesByFirstChar.TryGetValue(firstChar, out var templates))
            {
                foreach (var template in templates)
                {
                    if (template.TryTranslate(text, out var translated))
                    {
                        lock (_cacheLock)
                        {
                            _templateMatchCache[text] = translated;
                        }
                        return translated;
                    }
                }
            }
        }
        
        // 2. 再尝试空前缀模板（{0}在句首，用后缀匹配）
        foreach (var template in TemplatesWithEmptyPrefix)
        {
            if (template.TryTranslate(text, out var translated))
            {
                lock (_cacheLock)
                {
                    _templateMatchCache[text] = translated;
                }
                return translated;
            }
        }
        
        lock (_cacheLock)
        {
            _noTemplateMatch.Add(text);
        }
        return null;
    }
}

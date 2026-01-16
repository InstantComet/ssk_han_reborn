using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
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
    /// 加载翻译：优先加载 ParaTranz JSON，回退到旧格式 txt
    /// </summary>
    public static void LoadTranslations()
    {
        var sw = Stopwatch.StartNew();
        
        // 优先加载 ParaTranz JSON 文件
        string paraDir = Path.Combine(Paths.PluginPath, "para");
        if (Directory.Exists(paraDir))
        {
            LoadParaTranzJson(paraDir);
        }
        
        // 加载旧格式 txt（可用于覆盖或补充）
        LoadLegacyTxt();
        
        sw.Stop();
        Plugin.LogSrc.LogInfo($"Translation loading completed in {sw.ElapsedMilliseconds}ms");
    }

    /// <summary>
    /// 加载 ParaTranz 格式的 JSON 翻译文件
    /// </summary>
    private static void LoadParaTranzJson(string paraDir)
    {
        var sw = Stopwatch.StartNew();
        int totalCount = 0;
        int skippedCount = 0;
        
        var jsonFiles = Directory.GetFiles(paraDir, "*.json");
        
        foreach (var jsonFile in jsonFiles)
        {
            try
            {
                var (loaded, skipped) = LoadSingleJsonFile(jsonFile);
                totalCount += loaded;
                skippedCount += skipped;
            }
            catch (Exception ex)
            {
                Plugin.LogSrc.LogWarning($"Failed to load {Path.GetFileName(jsonFile)}: {ex.Message}");
            }
        }
        
        sw.Stop();
        Plugin.LogSrc.LogInfo($"Loaded {totalCount} translations from ParaTranz JSON in {sw.ElapsedMilliseconds}ms (skipped {skippedCount} empty/untranslated)");
    }

    /// <summary>
    /// 加载单个 JSON 文件
    /// </summary>
    private static (int loaded, int skipped) LoadSingleJsonFile(string jsonFile)
    {
        int loaded = 0;
        int skipped = 0;
        
        // 使用流式读取，避免一次性加载整个文件到字符串
        using var stream = File.OpenRead(jsonFile);
        using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        foreach (var element in doc.RootElement.EnumerateArray())
        {
            // 获取 original 和 translation
            if (!element.TryGetProperty("original", out var originalProp) ||
                !element.TryGetProperty("translation", out var translationProp))
            {
                skipped++;
                continue;
            }

            var original = originalProp.GetString();
            var translation = translationProp.GetString();

            // 跳过空翻译或未翻译的条目
            if (string.IsNullOrEmpty(original) || 
                string.IsNullOrEmpty(translation) ||
                original == translation)  // 未翻译
            {
                skipped++;
                continue;
            }

            // 去除首尾空格
            original = original.Trim();
            translation = translation.Trim();

            if (original.Length == 0 || translation.Length == 0)
            {
                skipped++;
                continue;
            }

            // 处理模板翻译（包含 {0} 占位符）
            if (original.Contains("{0}") && translation.Contains("{0}"))
            {
                AddTemplateEntry(original, translation);
            }
            else
            {
                // 精确匹配
                Map[original] = translation;
            }
            
            loaded++;
        }

        return (loaded, skipped);
    }

    /// <summary>
    /// 添加模板翻译条目
    /// </summary>
    private static void AddTemplateEntry(string original, string translation)
    {
        int placeholderIdx = original.IndexOf("{0}");
        string prefix = original.Substring(0, placeholderIdx);
        string suffix = original.Substring(placeholderIdx + 3);
        
        if (prefix.Length > 0)
        {
            // 有前缀，按首字符索引
            char firstChar = prefix[0];
            if (!TemplatesByFirstChar.TryGetValue(firstChar, out var list))
            {
                list = new List<TemplateEntry>();
                TemplatesByFirstChar[firstChar] = list;
            }
            list.Add(new TemplateEntry(prefix, suffix, translation));
        }
        else if (suffix.Length > 0)
        {
            // 前缀为空（{0}在句首），存入特殊列表
            TemplatesWithEmptyPrefix.Add(new TemplateEntry(prefix, suffix, translation));
        }
    }

    /// <summary>
    /// 加载旧格式 txt 文件（用于覆盖或补充 JSON 翻译）
    /// </summary>
    private static void LoadLegacyTxt()
    {
        string path = Path.Combine(Paths.PluginPath, "ssk_cn.txt");
        if (!File.Exists(path))
        {
            // 创建示例文件
            File.WriteAllText(path, @"# 精确匹配（可用于覆盖 JSON 中的翻译）
# New Game=新游戏
# 模板匹配（使用 {0} 作为参数占位符）
# Music Volume ({0}):=音乐音量 ({0}):
", Encoding.UTF8);
            return;
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
                AddTemplateEntry(en, zh);
                templateCount++;
            }
            else
            {
                Map[en] = zh;  // 会覆盖 JSON 中的翻译
                exactCount++;
            }
        }

        if (exactCount > 0 || templateCount > 0)
        {
            Plugin.LogSrc.LogInfo($"Loaded from txt override: {exactCount} exact, {templateCount} templates");
        }
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
        
        // 4. 尝试日期翻译（动态处理各种日期格式）
        var dateResult = DateTranslator.TryTranslateWithTags(text);
        if (dateResult != null)
        {
            return dateResult;
        }
        
        // 5. 尝试模板匹配
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

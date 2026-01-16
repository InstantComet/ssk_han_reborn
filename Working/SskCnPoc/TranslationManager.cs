using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
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

            // 检查是否为嵌套 JSON 格式（VariableDescriptionText 类型）
            if (original.StartsWith("{") && original.EndsWith("}") &&
                translation.StartsWith("{") && translation.EndsWith("}"))
            {
                int nestedLoaded = LoadNestedJsonTranslations(original, translation);
                loaded += nestedLoaded;
                continue;
            }

            // 规范化文本格式（将 ParaTranz 格式转换为游戏运行时格式）
            original = NormalizeForGameRuntime(original);
            translation = NormalizeForGameRuntime(translation);

            // 检查并处理按键绑定格式（如 [<#FFD27C>R</color>] Dock）
            var (templateOrig, templateTrans) = TryConvertKeyBindingToTemplate(original, translation);
            if (templateOrig != null && templateTrans != null)
            {
                AddTemplateEntry(templateOrig, templateTrans);
                loaded++;
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

    // 匹配按键绑定格式的正则表达式: [<#HexColor>Key</color>]
    private static readonly Regex KeyBindingPattern = new(
        @"^\[<#[0-9A-Fa-f]{6}>(.+?)</color>\]\s*(.+)$",
        RegexOptions.Compiled);

    /// <summary>
    /// 尝试将按键绑定格式转换为模板
    /// 输入: "[&lt;#FFD27C&gt;R&lt;/color&gt;] Dock" -> 模板: "[&lt;#FFD27C&gt;{0}&lt;/color&gt;] Dock"
    /// </summary>
    private static (string? templateOrig, string? templateTrans) TryConvertKeyBindingToTemplate(string original, string translation)
    {
        var origMatch = KeyBindingPattern.Match(original);
        if (!origMatch.Success) return (null, null);
        
        var transMatch = KeyBindingPattern.Match(translation);
        if (!transMatch.Success)
        {
            // 翻译可能没有空格，尝试更宽松的匹配
            var loosePattern = new Regex(@"^\[<#[0-9A-Fa-f]{6}>(.+?)</color>\](.*)$");
            transMatch = loosePattern.Match(translation);
            if (!transMatch.Success) return (null, null);
        }
        
        // 提取按键和动作名
        string origKey = origMatch.Groups[1].Value;
        string origAction = origMatch.Groups[2].Value;
        string transAction = transMatch.Groups[2].Value.TrimStart();
        
        // 生成模板：将按键替换为 {0}
        // 原文模板: [<#FFD27C>{0}</color>] Dock
        string templateOrig = $"[<#FFD27C>{{0}}</color>] {origAction}";
        // 译文模板: [<#FFD27C>{0}</color>]进站
        string templateTrans = $"[<#FFD27C>{{0}}</color>]{transAction}";
        
        return (templateOrig, templateTrans);
    }

    /// <summary>
    /// 加载嵌套 JSON 格式的翻译（VariableDescriptionText 类型）
    /// 格式如：{"Relay":{"1":"text1","0":"text2"}}
    /// </summary>
    private static int LoadNestedJsonTranslations(string originalJson, string translationJson)
    {
        int loaded = 0;
        
        try
        {
            using var origDoc = JsonDocument.Parse(originalJson);
            using var transDoc = JsonDocument.Parse(translationJson);
            
            // 递归提取所有叶子节点的字符串值
            var origTexts = new Dictionary<string, string>();
            var transTexts = new Dictionary<string, string>();
            
            ExtractLeafStrings(origDoc.RootElement, "", origTexts);
            ExtractLeafStrings(transDoc.RootElement, "", transTexts);
            
            // 按路径匹配原文和译文
            foreach (var (path, origText) in origTexts)
            {
                if (transTexts.TryGetValue(path, out var transText) &&
                    !string.IsNullOrEmpty(origText) &&
                    !string.IsNullOrEmpty(transText) &&
                    origText != transText)
                {
                    // 规范化并添加到字典
                    var normalizedOrig = NormalizeForGameRuntime(origText.Trim());
                    var normalizedTrans = NormalizeForGameRuntime(transText.Trim());
                    
                    if (!string.IsNullOrEmpty(normalizedOrig) && !string.IsNullOrEmpty(normalizedTrans))
                    {
                        Map[normalizedOrig] = normalizedTrans;
                        loaded++;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // JSON 解析失败，忽略此条目
        }
        
        return loaded;
    }

    /// <summary>
    /// 递归提取 JSON 中所有叶子节点的字符串值
    /// </summary>
    private static void ExtractLeafStrings(JsonElement element, string path, Dictionary<string, string> result)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                result[path] = element.GetString() ?? "";
                break;
                
            case JsonValueKind.Object:
                foreach (var prop in element.EnumerateObject())
                {
                    string newPath = string.IsNullOrEmpty(path) ? prop.Name : $"{path}.{prop.Name}";
                    ExtractLeafStrings(prop.Value, newPath, result);
                }
                break;
                
            case JsonValueKind.Array:
                int index = 0;
                foreach (var item in element.EnumerateArray())
                {
                    ExtractLeafStrings(item, $"{path}[{index}]", result);
                    index++;
                }
                break;
        }
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
    /// 将 ParaTranz 格式的文本规范化为游戏运行时格式
    /// 例如：将 "[Gain 15 Sovereigns]" 转换为 "&lt;style="descriptive"&gt;Gain 15 Sovereigns&lt;/style&gt;"
    /// </summary>
    private static string NormalizeForGameRuntime(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        
        // 首先处理字面的 \n 转换为实际换行符（翻译者可能直接输入了 \n 文本）
        if (text.Contains("\\n"))
        {
            text = text.Replace("\\n", "\n");
        }
        
        // 使用 StringBuilder 进行高效的字符串处理
        var sb = new StringBuilder(text.Length + 64);
        int i = 0;
        bool modified = false;
        
        while (i < text.Length)
        {
            // 检查是否是 [dir:...] 格式（需要移除）
            if (i < text.Length - 5 && text[i] == '[' && 
                text.Substring(i, 5).Equals("[dir:", StringComparison.OrdinalIgnoreCase))
            {
                // 跳过整个 [dir:...] 标签
                int endBracket = text.IndexOf(']', i);
                if (endBracket > i)
                {
                    i = endBracket + 1;
                    modified = true;
                    // 跳过后面可能的标点和空格
                    while (i < text.Length && (text[i] == '.' || text[i] == ' ' || text[i] == '\r' || text[i] == '\n'))
                    {
                        i++;
                    }
                    continue;
                }
            }
            
            // 检查是否是普通的 [...] 格式（需要转换为 <style="descriptive">...</style>）
            if (text[i] == '[')
            {
                int endBracket = FindMatchingBracket(text, i);
                if (endBracket > i + 1)
                {
                    string content = text.Substring(i + 1, endBracket - i - 1);
                    // 排除一些不应该转换的特殊格式
                    if (!content.StartsWith("dir:", StringComparison.OrdinalIgnoreCase) &&
                        !content.StartsWith("qvd:", StringComparison.OrdinalIgnoreCase) &&
                        !content.Contains("{") && !content.Contains("}"))
                    {
                        sb.Append("<style=\"descriptive\">");
                        sb.Append(content);
                        sb.Append("</style>");
                        i = endBracket + 1;
                        modified = true;
                        continue;
                    }
                }
            }
            
            sb.Append(text[i]);
            i++;
        }
        
        if (!modified) return text;
        
        return sb.ToString().Trim();
    }

    /// <summary>
    /// 查找匹配的右方括号位置
    /// </summary>
    private static int FindMatchingBracket(string text, int openPos)
    {
        int depth = 1;
        for (int i = openPos + 1; i < text.Length; i++)
        {
            if (text[i] == '[') depth++;
            else if (text[i] == ']')
            {
                depth--;
                if (depth == 0) return i;
            }
        }
        return -1;
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

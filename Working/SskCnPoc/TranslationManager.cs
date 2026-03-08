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
    
    // 多占位符模板：按前缀首字母分组
    public static Dictionary<char, List<MultiTemplateEntry>> MultiTemplatesByFirstChar { get; } = new();
    
    // 前缀为空的多占位符模板
    public static List<MultiTemplateEntry> MultiTemplatesWithEmptyPrefix { get; } = new();
    
    // 已匹配的模板缓存：避免重复匹配相同的动态文本
    private static readonly Dictionary<string, string> _templateMatchCache = new(StringComparer.Ordinal);
    
    // 已确认不匹配任何模板的文本
    private static readonly HashSet<string> _noTemplateMatch = new(StringComparer.Ordinal);
    
    // 前缀索引：用于匹配游戏截断长文本只显示第一句/段的情况
    // Key: 原文前 PrefixLen 个字符, Value: (完整原文, 完整译文) 列表
    private static readonly Dictionary<string, List<(string fullOrig, string fullTrans)>> _prefixIndex = new(StringComparer.Ordinal);
    private const int PrefixLen = 16;
    
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

            // 检查是否为嵌套 JSON 格式（VariableDescriptionText / ChangeDescriptionText 类型）
            if (original.StartsWith("{") && original.EndsWith("}") &&
                translation.StartsWith("{") && translation.EndsWith("}"))
            {
                // 首先加载拆分后的单个值（用于游戏显示单个值的情况）
                int nestedLoaded = LoadNestedJsonTranslations(original, translation);
                loaded += nestedLoaded;
                
                // 同时也将整个 JSON 字符串本身加入字典（用于游戏直接显示整个 JSON 的情况）
                if (original != translation)
                {
                    Map[original] = translation;
                    loaded++;
                }
                continue;
            }

            // 规范化文本格式（将 ParaTranz 格式转换为游戏运行时格式）
            original = NormalizeForGameRuntime(original);
            translation = NormalizeForGameRuntime(translation);

            // 将游戏模板占位符 {{A}}/{{B}}/{{C}} 转换为 {0}/{1}/{2}
            original = ConvertGamePlaceholders(original);
            translation = ConvertGamePlaceholders(translation);

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
                // 检查是否有多个占位符
                if (original.Contains("{1}") && translation.Contains("{1}"))
                {
                    AddMultiTemplateEntry(original, translation);
                }
                else
                {
                    AddTemplateEntry(original, translation);
                }
            }
            else
            {
                // 精确匹配
                Map[original] = translation;
                AddToPrefixIndex(original, translation);
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
                        AddToPrefixIndex(normalizedOrig, normalizedTrans);
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
    /// 添加多占位符模板翻译条目（{0}, {1}, ...）
    /// </summary>
    private static void AddMultiTemplateEntry(string original, string translation)
    {
        // 按 {0}, {1}, {2}, ... 分割原文，提取固定文本段
        var parts = new List<string>();
        int placeholderCount = 0;
        int pos = 0;
        
        while (true)
        {
            string placeholder = $"{{{placeholderCount}}}";
            int idx = original.IndexOf(placeholder, pos, StringComparison.Ordinal);
            if (idx < 0)
            {
                // 没有更多占位符，添加剩余部分作为最后一个 part
                parts.Add(original.Substring(pos));
                break;
            }
            parts.Add(original.Substring(pos, idx - pos));
            pos = idx + placeholder.Length;
            placeholderCount++;
        }

        if (placeholderCount < 2) return; // 不应该发生，保护性检查

        var entry = new MultiTemplateEntry(parts.ToArray(), translation, placeholderCount);
        
        if (parts[0].Length > 0)
        {
            char firstChar = parts[0][0];
            if (!MultiTemplatesByFirstChar.TryGetValue(firstChar, out var list))
            {
                list = new List<MultiTemplateEntry>();
                MultiTemplatesByFirstChar[firstChar] = list;
            }
            list.Add(entry);
        }
        else
        {
            MultiTemplatesWithEmptyPrefix.Add(entry);
        }
    }

    /// <summary>
    /// 将游戏模板占位符 {{A}}/{{B}}/{{C}} 转换为标准占位符 {0}/{1}/{2}
    /// </summary>
    private static string ConvertGamePlaceholders(string text)
    {
        if (!text.Contains("{{")) return text;
        
        return text
            .Replace("{{A}}", "{0}")
            .Replace("{{B}}", "{1}")
            .Replace("{{C}}", "{2}")
            .Replace("{{D}}", "{3}");
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
                        !content.StartsWith("<#", StringComparison.Ordinal) &&  // 排除按键绑定格式 [<#FFD27C>R</color>]
                        !IsKeyBindingFormat(content) &&  // 排除按键绑定格式 [H], [Tab], [F8] 等
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
    /// 判断内容是否是按键绑定格式，如 H, J, Tab, ESC, F8, Arrow Up 等
    /// </summary>
    private static bool IsKeyBindingFormat(string content)
    {
        if (string.IsNullOrEmpty(content))
            return false;

        // 单个字母或数字
        if (content.Length == 1 && char.IsLetterOrDigit(content[0]))
            return true;

        // 常见按键名称（不区分大小写）
        string[] knownKeys = {
            "Tab", "ESC", "Escape", "Space", "Enter", "Return",
            "Shift", "Ctrl", "Alt", "Backspace", "Delete", "Insert",
            "Home", "End", "PageUp", "PageDown", "PgUp", "PgDn",
            "Up", "Down", "Left", "Right",
            "Arrow Up", "Arrow Down", "Arrow Left", "Arrow Right",
            "F1", "F2", "F3", "F4", "F5", "F6", "F7", "F8", "F9", "F10", "F11", "F12",
            "LMB", "RMB", "MMB", "Mouse1", "Mouse2", "Mouse3"
        };

        foreach (var key in knownKeys)
        {
            if (content.Equals(key, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
    /// 验证关键翻译条目是否已加载
    /// </summary>
    public static void VerifyKeyEntries()
    {
        string[] testKeys = new[]
        {
            "You can investigate the black box at New Winchester.",
            "You could take the box to London, as requested...",
            "...or you could sell it, and be done.",
            "You have been bequeathed a large black box which once belonged to Captain Whitlock.",
            "New Winchester",
            "The Blue Kingdom Transit Relay",
            "\"Excuse me!\"",
            "\"I specialise in test-driving, but I'm looking for something quieter.\""
        };
        foreach (var key in testKeys)
        {
            if (Map.TryGetValue(key, out var val))
                Plugin.LogSrc.LogInfo($"[VERIFY] ✓ '{key.Substring(0, Math.Min(40, key.Length))}...' -> '{val.Substring(0, Math.Min(30, val.Length))}..'");
            else
                Plugin.LogSrc.LogWarning($"[VERIFY] ✗ NOT FOUND: '{key}'");
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
        
        // 1.5 尝试去除首尾空白后匹配
        string trimmed = text.Trim();
        if (trimmed != text && Map.TryGetValue(trimmed, out zh))
        {
            return zh;
        }
        
        // 1.6 尝试补充冠词 "The " 后匹配（游戏地名有时省略 The）
        if (trimmed.Length > 0 && char.IsUpper(trimmed[0]))
        {
            if (Map.TryGetValue("The " + trimmed, out zh))
            {
                return zh;
            }
        }
        
        // 1.7 尝试剥离 TMP 富文本标签后匹配
        // 处理如 <b><color=#FDBD58FF><smallcaps>New Winchester</smallcaps></color></b> 的情况
        if (text.IndexOf('<') >= 0)
        {
            var tagResult = TryTranslateWithTagStripping(text);
            if (tagResult != null)
            {
                lock (_cacheLock) { _templateMatchCache[text] = tagResult; }
                return tagResult;
            }
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
        
        // 3.5 尝试前缀匹配（处理游戏截断长描述只显示第一句/段的情况）
        if (trimmed.Length >= PrefixLen + 3)
        {
            var prefixResult = TryPrefixMatch(trimmed);
            if (prefixResult != null)
            {
                lock (_cacheLock) { _templateMatchCache[text] = prefixResult; }
                return prefixResult;
            }
        }
        else if (trimmed.Length >= 8)
        {
            // 短文本：无法使用前缀索引，改用暴力扫描 Map 查找以此文本开头的长条目
            var shortResult = TryShortPrefixScan(trimmed);
            if (shortResult != null)
            {
                lock (_cacheLock) { _templateMatchCache[text] = shortResult; }
                return shortResult;
            }
        }
        
        // 4. 尝试日期翻译（动态处理各种日期格式）
        var dateResult = DateTranslator.TryTranslateWithTags(text);
        if (dateResult != null)
        {
            // 日期已翻译，进一步尝试翻译日期后的正文（如航海日志条目）
            return TryTranslateBodyAfterDate(dateResult);
        }
        
        // 5. 尝试模板匹配（单占位符 + 多占位符）
        return TryMatchTemplate(text);
    }

    // === 前缀索引匹配（处理游戏截断长文本） ===
    
    /// <summary>
    /// 将长文本条目加入前缀索引，同时提取首句/首段加入精确匹配字典
    /// </summary>
    private static void AddToPrefixIndex(string original, string translation)
    {
        if (original.Length < PrefixLen + 20) return;
        
        // 加入前缀索引
        string prefix = original.Substring(0, PrefixLen);
        if (!_prefixIndex.TryGetValue(prefix, out var list))
        {
            list = new List<(string, string)>();
            _prefixIndex[prefix] = list;
        }
        list.Add((original, translation));
        
        // 提取首句加入精确匹配字典（处理游戏只显示第一句话的情况）
        AddFirstSentenceToMap(original, translation);
    }
    
    /// <summary>
    /// 短文本暴力前缀扫描：遍历 Map 查找以输入文本开头的长条目。
    /// 仅用于 8-18 字符的短截断文本（长文本通过前缀索引走快速路径）。
    /// 结果会缓存，不会反复扫描。
    /// 也处理游戏重新添加闭合引号的情况（如 "Excuse me!" → "Excuse me! Captain!" ...）
    /// </summary>
    private static string TryShortPrefixScan(string text)
    {
        // 收集要尝试的前缀变体
        var prefixes = new List<string> { text };
        
        // 如果以引号结尾，也尝试去掉尾部引号（游戏可能重新闭合了截断的引号）
        char last = text[text.Length - 1];
        if (last == '"' || last == '\u201d' || last == '\u300d') // " " 」
        {
            string stripped = text.Substring(0, text.Length - 1);
            if (stripped.Length >= 6) prefixes.Add(stripped);
        }
        
        foreach (var (key, value) in Map)
        {
            foreach (var pfx in prefixes)
            {
                if (key.Length > pfx.Length + 10 && key.StartsWith(pfx, StringComparison.Ordinal))
                {
                    return ExtractTranslationForPrefix(text, key, value);
                }
            }
        }
        return null;
    }
    
    /// <summary>
    /// 从长文本中提取第一句/引语/段落，并将其与翻译的对应部分加入 Map。
    /// 处理游戏截断长描述只显示首句的情况。
    /// </summary>
    private static void AddFirstSentenceToMap(string original, string translation)
    {
        // 尝试不同的截断点来提取首句
        var origSentence = ExtractFirstSentence(original);
        if (origSentence == null || origSentence.Length < 8 || origSentence == original) return;
        
        // 如果首句已经在 Map 中，跳过
        if (Map.ContainsKey(origSentence)) return;
        
        // 从翻译中提取对应的首句
        var transSentence = ExtractFirstSentence(translation);
        if (transSentence == null || transSentence.Length < 2 || transSentence == translation) return;
        
        Map[origSentence] = transSentence;
    }
    
    /// <summary>
    /// 提取文本的第一个逻辑句子/引语/段落
    /// </summary>
    private static string ExtractFirstSentence(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        
        // 策略1: 在 \r\n\r\n 或 \n\n 段落分隔处截断
        int paraBreak = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        if (paraBreak < 0) paraBreak = text.IndexOf("\n\n", StringComparison.Ordinal);
        
        // 策略2: 找第一个完整引语 "..." 或 "..."
        // 策略3: 找第一个句末标点
        
        int bestEnd = -1;
        
        // 如果文本以引号开头，找匹配的闭合引号
        if (text.Length > 1)
        {
            char first = text[0];
            char closeQuote = '\0';
            if (first == '"') closeQuote = '"';
            else if (first == '\u201c') closeQuote = '\u201d'; // ""
            else if (first == '\u300c') closeQuote = '\u300d'; // 「」
            
            if (closeQuote != '\0')
            {
                // 找闭合引号（跳过第一个字符）
                int closeIdx = text.IndexOf(closeQuote, 1);
                if (closeIdx > 0 && closeIdx < text.Length - 5)
                {
                    bestEnd = closeIdx + 1;
                }
            }
        }
        
        // 如果没有找到引语边界，寻找段落分隔
        if (bestEnd < 0 && paraBreak > 0 && paraBreak < text.Length - 10)
        {
            bestEnd = paraBreak;
        }
        
        // 如果还没有，找句末标点（.!? 后跟空格或行尾）
        if (bestEnd < 0)
        {
            for (int i = 8; i < text.Length - 5; i++)
            {
                char c = text[i];
                bool isSentenceEnd = false;
                
                if ((c == '.' || c == '!' || c == '?') && i + 1 < text.Length && (text[i + 1] == ' ' || text[i + 1] == '\r' || text[i + 1] == '\n'))
                {
                    // 检查下一个字符不是小写（避免在缩写如 "e.g." 处截断）
                    if (i + 2 < text.Length && char.IsUpper(text[i + 2]))
                        isSentenceEnd = true;
                    else if (i + 2 < text.Length && text[i + 2] == '"')
                        isSentenceEnd = true;
                    else if (text[i + 1] == '\r' || text[i + 1] == '\n')
                        isSentenceEnd = true;
                }
                // 中文句末标点
                else if (c == '\u3002' || c == '\uff01' || c == '\uff1f')
                {
                    isSentenceEnd = true;
                    // 检查后面是否跟着闭合引号
                    if (i + 1 < text.Length && (text[i + 1] == '\u201d' || text[i + 1] == '\u300d'))
                    {
                        i++; // 包含闭合引号
                    }
                }
                
                if (isSentenceEnd)
                {
                    bestEnd = i + 1;
                    break;
                }
            }
        }
        
        if (bestEnd > 0 && bestEnd < text.Length)
        {
            return text.Substring(0, bestEnd).TrimEnd();
        }
        
        return null;
    }
    
    /// <summary>
    /// 前缀匹配：当游戏只显示长描述的第一句/段时，通过前缀索引查找完整翻译并截取对应部分
    /// 也处理游戏重新添加闭合引号的情况
    /// </summary>
    private static string TryPrefixMatch(string text)
    {
        if (text.Length < PrefixLen + 3) return null;
        
        // 收集要尝试的前缀变体
        var textVariants = new List<string> { text };
        char last = text[text.Length - 1];
        if (last == '"' || last == '\u201d' || last == '\u300d')
        {
            string stripped = text.Substring(0, text.Length - 1);
            if (stripped.Length >= PrefixLen + 3) textVariants.Add(stripped);
        }
        
        foreach (var variant in textVariants)
        {
            string prefix = variant.Substring(0, PrefixLen);
            if (!_prefixIndex.TryGetValue(prefix, out var candidates)) continue;
            
            foreach (var (fullOrig, fullTrans) in candidates)
            {
                if (fullOrig.Length > variant.Length + 10 &&
                    fullOrig.StartsWith(variant, StringComparison.Ordinal))
                {
                    return ExtractTranslationForPrefix(text, fullOrig, fullTrans);
                }
            }
        }
        return null;
    }
    
    /// <summary>
    /// 根据原文截断比例，在翻译文本中找到最近的句子/段落边界并截取
    /// </summary>
    private static string ExtractTranslationForPrefix(string inputText, string fullOriginal, string fullTranslation)
    {
        // 用长度比例估算翻译文本的截断位置
        double ratio = (double)inputText.Length / fullOriginal.Length;
        int approxEnd = (int)(fullTranslation.Length * ratio);
        
        // 在估算位置附近寻找最近的句子/段落边界（前后各搜 30 字符）
        int windowStart = Math.Max(0, approxEnd - 30);
        int windowEnd = Math.Min(fullTranslation.Length, approxEnd + 30);
        
        int bestPos = -1;
        int bestDist = int.MaxValue;
        
        for (int i = windowStart; i < windowEnd; i++)
        {
            char c = fullTranslation[i];
            int endPos = -1;
            
            // 中文句子结束符：。！？
            if (c == '\u3002' || c == '\uff01' || c == '\uff1f')
            {
                endPos = i + 1;
                // 检查后面是否跟着引号（处理对话格式 "...。"）
                if (endPos < fullTranslation.Length)
                {
                    char next = fullTranslation[endPos];
                    if (next == '\u201d' || next == '\u300d' || next == '\u2019') // "」'
                        endPos++;
                }
            }
            // 段落边界
            else if (c == '\n' && i + 1 < fullTranslation.Length && fullTranslation[i + 1] == '\n')
            {
                endPos = i;
            }
            
            if (endPos >= 0)
            {
                int dist = Math.Abs(endPos - approxEnd);
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestPos = endPos;
                }
            }
        }
        
        if (bestPos > 0 && bestPos <= fullTranslation.Length)
        {
            return BalanceQuotes(fullTranslation.Substring(0, bestPos).TrimEnd());
        }
        
        // 回退：按比例截断
        int safeEnd = Math.Min(Math.Max(approxEnd, 1), fullTranslation.Length);
        return BalanceQuotes(fullTranslation.Substring(0, safeEnd).TrimEnd());
    }
    
    /// <summary>
    /// 平衡引号：如果截取的文本有未闭合的左引号，补上对应的右引号
    /// </summary>
    private static string BalanceQuotes(string text)
    {
        // 中文双引号 "" 
        int openDouble = 0;
        // 中文单引号 ''
        int openSingle = 0;
        // 直角引号 「」
        int openCorner = 0;
        
        foreach (char c in text)
        {
            switch (c)
            {
                case '\u201c': openDouble++; break;  // "
                case '\u201d': openDouble--; break;  // "
                case '\u2018': openSingle++; break;  // '
                case '\u2019': openSingle--; break;  // '
                case '\u300c': openCorner++; break;  // 「
                case '\u300d': openCorner--; break;  // 」
            }
        }
        
        var sb = new System.Text.StringBuilder(text);
        // 补右引号（从内到外：先补最内层）
        for (int i = 0; i < openCorner; i++) sb.Append('\u300d');  // 」
        for (int i = 0; i < openSingle; i++) sb.Append('\u2019');  // '
        for (int i = 0; i < openDouble; i++) sb.Append('\u201d');  // "
        
        return sb.ToString();
    }
    
    // === 富文本标签匹配 ===
    // 匹配外层包裹标签: <tag>...<tag>CONTENT</tag>...</tag>
    private static readonly Regex _outerTagsRegex = new(
        @"^((?:<[^/>]+>)+)(.+?)((?:</[^>]+>)+)$",
        RegexOptions.Singleline | RegexOptions.Compiled);
    // 匹配所有 TMP 富文本标签
    private static readonly Regex _richTagRegex = new(
        @"<[^>]+>", RegexOptions.Compiled);

    /// <summary>
    /// 尝试剥离 TMP 富文本标签后翻译文本。
    /// Phase 1: 提取外层包裹标签（如 &lt;b&gt;&lt;color&gt;...&lt;/color&gt;&lt;/b&gt;），翻译内部纯文本，保留格式。
    /// Phase 2: 完全剥离所有标签，尝试翻译纯文本内容（丢失格式）。
    /// </summary>
    private static string TryTranslateWithTagStripping(string text)
    {
        Plugin.LogSrc.LogDebug($"[TAG-STRIP] Input: '{text.Substring(0, Math.Min(80, text.Length))}'");
        
        // Phase 1: 提取外层包裹标签，翻译内部内容（保留格式）
        var match = _outerTagsRegex.Match(text);
        if (match.Success)
        {
            string prefix = match.Groups[1].Value;
            string inner = match.Groups[2].Value;
            string suffix = match.Groups[3].Value;
            
            Plugin.LogSrc.LogDebug($"[TAG-STRIP] Phase1: prefix='{prefix}' inner='{inner.Substring(0, Math.Min(60, inner.Length))}' suffix='{suffix}'");
            
            // 内部不含标签时，递归调用 TryTranslate 翻译（不会无限递归，因为 inner 无 '<'）
            if (inner.IndexOf('<') < 0)
            {
                string translated = TryTranslate(inner);
                Plugin.LogSrc.LogDebug($"[TAG-STRIP] Phase1 translate: {(translated != null ? "HIT" : "MISS")} for '{inner.Substring(0, Math.Min(60, inner.Length))}'");
                if (translated != null)
                {
                    // 中文无大小写之分，移除 smallcaps 标签避免 TMP 异常缩放
                    prefix = prefix.Replace("<smallcaps>", "");
                    suffix = suffix.Replace("</smallcaps>", "");
                    return prefix + translated + suffix;
                }
            }
        }
        
        // Phase 2: 完全剥离所有标签，尝试翻译纯文本（丢失原始格式）
        string stripped = _richTagRegex.Replace(text, "").Trim();
        if (stripped.Length > 0 && stripped != text.Trim())
        {
            // stripped 不含 '<'，调用 TryTranslate 不会再进入标签剥离逻辑
            return TryTranslate(stripped);
        }
        
        return null;
    }

    /// <summary>
    /// 在日期翻译后，尝试翻译换行符之后的正文部分。
    /// 处理航海日志格式："{translated_date}\n{english_body}"
    /// </summary>
    private static string TryTranslateBodyAfterDate(string dateTranslatedText)
    {
        // 查找换行符分隔的正文部分
        int newlineIdx = dateTranslatedText.IndexOf('\n');
        if (newlineIdx < 0)
        {
            return dateTranslatedText; // 纯日期，无正文
        }

        string datePart = dateTranslatedText.Substring(0, newlineIdx);
        string bodyPart = dateTranslatedText.Substring(newlineIdx + 1);

        if (string.IsNullOrWhiteSpace(bodyPart))
        {
            return dateTranslatedText;
        }

        string bodyTrimmed = bodyPart.Trim();

        // 尝试精确匹配正文部分
        if (Map.TryGetValue(bodyTrimmed, out var zhBody))
        {
            return datePart + "\n" + zhBody;
        }

        // 尝试模板匹配正文部分
        var templateResult = TryMatchTemplate(bodyTrimmed);
        if (templateResult != null)
        {
            return datePart + "\n" + templateResult;
        }

        return dateTranslatedText;
    }

    /// <summary>
    /// 高效的模板匹配：使用首字符索引快速定位候选模板
    /// </summary>
    private static string? TryMatchTemplate(string text)
    {
        // 1. 先尝试按首字符索引的单占位符模板
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
        
        // 2. 再尝试空前缀单占位符模板（{0}在句首，用后缀匹配）
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

        // 3. 尝试按首字符索引的多占位符模板
        if (MultiTemplatesByFirstChar.Count > 0)
        {
            char firstChar = text[0];
            
            if (MultiTemplatesByFirstChar.TryGetValue(firstChar, out var multiTemplates))
            {
                foreach (var template in multiTemplates)
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

        // 4. 尝试空前缀多占位符模板
        foreach (var template in MultiTemplatesWithEmptyPrefix)
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

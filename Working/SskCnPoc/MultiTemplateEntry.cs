using System;

namespace SskCnPoc;

/// <summary>
/// 多占位符模板翻译条目，用于处理带多个参数的动态文本
/// 例如 "You've gained {0} x Supplies (new total {1})." 
///   可以匹配 "You've gained 4 x Supplies (new total 5)."
/// 
/// Parts 数组存储按占位符分割后的固定文本段：
///   原文 "AAA{0}BBB{1}CCC" → Parts = ["AAA", "BBB", "CCC"]
/// </summary>
internal sealed class MultiTemplateEntry
{
    /// <summary>
    /// 按占位符分割后的固定文本段。
    /// 对于 N 个占位符，有 N+1 个 Parts。
    /// </summary>
    public string[] Parts { get; }

    /// <summary>
    /// 中文翻译模板，包含 {0}, {1}, ... 占位符
    /// </summary>
    public string ZhTemplate { get; }

    /// <summary>
    /// 占位符数量
    /// </summary>
    public int PlaceholderCount { get; }

    public MultiTemplateEntry(string[] parts, string zhTemplate, int placeholderCount)
    {
        Parts = parts;
        ZhTemplate = zhTemplate;
        PlaceholderCount = placeholderCount;
    }

    /// <summary>
    /// 尝试匹配并翻译文本
    /// </summary>
    public bool TryTranslate(string input, out string translated)
    {
        translated = null!;

        // 快速检查：输入长度至少应等于所有固定部分的总长度
        int minLen = 0;
        foreach (var part in Parts) minLen += part.Length;
        if (input.Length < minLen) return false;

        // 检查前缀 (Parts[0])
        if (Parts[0].Length > 0 && !input.AsSpan().StartsWith(Parts[0].AsSpan(), StringComparison.Ordinal))
            return false;

        int pos = Parts[0].Length;
        var capturedValues = new string[PlaceholderCount];

        // 逐个匹配占位符
        for (int i = 0; i < PlaceholderCount; i++)
        {
            string nextPart = Parts[i + 1]; // 当前占位符之后的固定文本段

            if (nextPart.Length == 0 && i == PlaceholderCount - 1)
            {
                // 最后一个占位符且后缀为空：捕获剩余所有内容
                capturedValues[i] = input.Substring(pos);
                pos = input.Length;
            }
            else
            {
                // 在剩余文本中查找下一个固定文本段
                int nextPartIdx = input.IndexOf(nextPart, pos, StringComparison.Ordinal);
                if (nextPartIdx < 0) return false;

                capturedValues[i] = input.Substring(pos, nextPartIdx - pos);
                pos = nextPartIdx + nextPart.Length;
            }
        }

        // 确保已消费完整个输入
        if (pos != input.Length) return false;

        // 构建翻译结果
        translated = ZhTemplate;
        for (int i = 0; i < PlaceholderCount; i++)
        {
            string param = capturedValues[i];
            // 尝试对参数进行二次翻译
            if (TranslationManager.Map.TryGetValue(param, out var translatedParam))
            {
                param = translatedParam;
            }
            translated = translated.Replace($"{{{i}}}", param);
        }

        return true;
    }
}

using System;

namespace SskCnPoc;

/// <summary>
/// 模板翻译条目，用于处理带参数的动态文本
/// 例如 "Music Volume ({0}):" 可以匹配 "Music Volume (50%):" 
/// </summary>
internal sealed class TemplateEntry
{
    public string Prefix { get; }      // 前缀部分，如 "Music Volume ("
    public string Suffix { get; }      // 后缀部分，如 "):"
    public string ZhTemplate { get; }  // 中文模板，如 "音乐音量 ({0}):"
    
    public TemplateEntry(string prefix, string suffix, string zhTemplate)
    {
        Prefix = prefix;
        Suffix = suffix;
        ZhTemplate = zhTemplate;
    }
    
    /// <summary>
    /// 尝试匹配并翻译文本
    /// </summary>
    public bool TryTranslate(string input, out string translated)
    {
        translated = null!;
        
        // 快速检查长度
        int minLen = Prefix.Length + Suffix.Length;
        if (input.Length < minLen) return false;
        
        // 检查前缀（使用 Span 避免分配）
        if (!input.AsSpan().StartsWith(Prefix.AsSpan(), StringComparison.Ordinal))
            return false;
            
        // 检查后缀
        if (!input.AsSpan().EndsWith(Suffix.AsSpan(), StringComparison.Ordinal))
            return false;
        
        // 提取参数部分
        int paramStart = Prefix.Length;
        int paramLen = input.Length - Prefix.Length - Suffix.Length;
        if (paramLen < 0) return false;
        
        string param = input.Substring(paramStart, paramLen);
        
        // 构建翻译结果
        translated = ZhTemplate.Replace("{0}", param);
        return true;
    }
}

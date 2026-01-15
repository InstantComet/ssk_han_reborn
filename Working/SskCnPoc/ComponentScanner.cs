using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;

namespace SskCnPoc;

/// <summary>
/// 组件扫描器：扫描并翻译场景中的文本组件
/// </summary>
internal static class ComponentScanner
{
    private static readonly HashSet<int> _processedIds = new();
    private static readonly object _lock = new();

    /// <summary>
    /// 清除已处理缓存（场景切换时调用）
    /// </summary>
    public static void ClearCache()
    {
        lock (_lock) { _processedIds.Clear(); }
    }

    /// <summary>
    /// 扫描场景中所有 TMP 和 UGUI 文本组件
    /// </summary>
    public static void ScanAll()
    {
        int translated = 0, missing = 0;

        // 扫描 TMP
        foreach (var tmp in Resources.FindObjectsOfTypeAll<TMP_Text>())
        {
            if (TryScanTmp(tmp)) translated++;
            else missing++;
        }

        // 扫描 UGUI
        foreach (var text in Resources.FindObjectsOfTypeAll<UnityEngine.UI.Text>())
        {
            if (TryScanUgui(text)) translated++;
            else missing++;
        }

        if (translated > 0 || missing > 0)
            Plugin.LogSrc.LogInfo($"Scanned: {translated} translated, {missing} missing");
    }

    /// <summary>
    /// 扫描指定 GameObject 下的子组件
    /// </summary>
    public static void ScanChildren(GameObject root)
    {
        int translated = 0;

        foreach (var tmp in root.GetComponentsInChildren<TMP_Text>(true))
        {
            if (TryScanTmp(tmp)) translated++;
        }

        foreach (var text in root.GetComponentsInChildren<UnityEngine.UI.Text>(true))
        {
            if (TryScanUgui(text)) translated++;
        }

        if (translated > 0)
            Plugin.LogSrc.LogDebug($"ScanChildren '{root.name}': {translated} translated");
    }

    /// <summary>
    /// 使用缓存的组件数组快速扫描
    /// </summary>
    public static void ScanCached(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TMP_Text> tmpTexts,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<UnityEngine.UI.Text> uguiTexts)
    {
        foreach (var tmp in tmpTexts) TryScanTmp(tmp);
        foreach (var text in uguiTexts) TryScanUgui(text);
    }

    private static bool TryScanTmp(TMP_Text tmp)
    {
        try
        {
            if (tmp == null) return false;

            int id = tmp.GetInstanceID();
            lock (_lock)
            {
                if (_processedIds.Contains(id)) return false;
            }

            var text = tmp.text;
            if (string.IsNullOrEmpty(text)) return false;

            if (Utils.ContainsChinese(text))
            {
                lock (_lock) { _processedIds.Add(id); }
                return false;
            }

            var translated = TranslationManager.TryTranslate(text);
            if (translated != null)
            {
                tmp.text = translated;
                FontManager.EnsureChineseFontSupport(tmp);
                lock (_lock) { _processedIds.Add(id); }
                return true;
            }

            MissingCollector.Collect(text);
            lock (_lock) { _processedIds.Add(id); }
            return false;
        }
        catch { return false; }
    }

    private static bool TryScanUgui(UnityEngine.UI.Text text)
    {
        try
        {
            if (text == null) return false;

            int id = text.GetInstanceID();
            lock (_lock)
            {
                if (_processedIds.Contains(id)) return false;
            }

            var content = text.text;
            if (string.IsNullOrEmpty(content)) return false;

            if (Utils.ContainsChinese(content))
            {
                lock (_lock) { _processedIds.Add(id); }
                return false;
            }

            var translated = TranslationManager.TryTranslate(content);
            if (translated != null)
            {
                text.text = translated;
                lock (_lock) { _processedIds.Add(id); }
                return true;
            }

            MissingCollector.Collect(content);
            lock (_lock) { _processedIds.Add(id); }
            return false;
        }
        catch { return false; }
    }
}

using HarmonyLib;
using System;
using System.Linq;
using TMPro;
using UnityEngine;

namespace SskCnPoc;

/// <summary>
/// Harmony Patch 注册和回调
/// </summary>
internal static class HarmonyPatches
{
    [ThreadStatic] private static bool _isSettingTmpText;
    [ThreadStatic] private static bool _isSettingUguiText;

    private static bool _tmpPatched;
    private static bool _uguiPatched;
    
    /// <summary>
    /// 注册所有 Harmony Patches
    /// </summary>
    public static bool ApplyAll(Harmony harmony)
    {
        if (!_tmpPatched)
            _tmpPatched = PatchTmp(harmony);
        if (!_uguiPatched)
            _uguiPatched = PatchUgui(harmony);
        bool canvasPatched = PatchCanvas(harmony);
        
        Plugin.LogSrc.LogInfo($"Patched: TMP={_tmpPatched}, UGUI={_uguiPatched}, Canvas={canvasPatched}");
        
        // TMP 是主要的文本组件，必须成功 patch
        return _tmpPatched;
    }

    #region TMP Patches

    private static bool PatchTmp(Harmony harmony)
    {
        try
        {
            // 直接使用编译时类型，不需要动态查找
            var tmpType = typeof(TMP_Text);
            Plugin.LogSrc.LogInfo($"TMP_Text type: {tmpType?.FullName ?? "null"}");
            
            if (tmpType == null) 
            {
                Plugin.LogSrc.LogWarning("TMP_Text type is null");
                return false;
            }

            var prefix = AccessTools.Method(typeof(HarmonyPatches), nameof(TmpPrefix));
            var postfix = AccessTools.Method(typeof(HarmonyPatches), nameof(TmpPostfix));
            
            if (prefix == null || postfix == null)
            {
                Plugin.LogSrc.LogWarning($"Methods not found: prefix={prefix != null}, postfix={postfix != null}");
                return false;
            }
            
            int count = 0;

            // Patch SetText(string)
            var setTextMethods = tmpType.GetMethods().Where(m => m.Name == "SetText" && m.GetParameters().Length == 1).ToList();
            Plugin.LogSrc.LogInfo($"Found {setTextMethods.Count} SetText(string) methods");
            foreach (var m in setTextMethods)
            {
                try
                {
                    harmony.Patch(m, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                    count++;
                }
                catch (Exception ex)
                {
                    Plugin.LogSrc.LogWarning($"Failed to patch SetText: {ex.Message}");
                }
            }

            // Patch text property setter
            var textProp = tmpType.GetProperty("text");
            Plugin.LogSrc.LogInfo($"text property: {textProp?.Name ?? "null"}");
            var textSetter = textProp?.GetSetMethod();
            if (textSetter != null)
            {
                try
                {
                    harmony.Patch(textSetter, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                    count++;
                    Plugin.LogSrc.LogInfo("✓ Patched text setter");
                }
                catch (Exception ex)
                {
                    Plugin.LogSrc.LogWarning($"Failed to patch text setter: {ex.Message}");
                }
            }
            else
            {
                Plugin.LogSrc.LogWarning("text setter is null");
            }

            // Patch SetCharArray and multi-param SetText (postfix only)
            foreach (var m in tmpType.GetMethods().Where(m => m.Name == "SetCharArray" || (m.Name == "SetText" && m.GetParameters().Length > 1)))
            {
                try
                {
                    harmony.Patch(m, postfix: new HarmonyMethod(postfix));
                    count++;
                }
                catch { }
            }

            Plugin.LogSrc.LogInfo($"✓ Patched {count} TMP methods total");
            return count > 0;
        }
        catch (Exception ex)
        {
            Plugin.LogSrc.LogWarning($"PatchTmp error: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    private static Type? FindTmpType()
    {
        // 不再需要，直接使用 typeof(TMP_Text)
        return typeof(TMP_Text);
    }

    private static bool TmpPrefix(TMP_Text __instance, string __0)
    {
        if (_isSettingTmpText || __instance == null || string.IsNullOrEmpty(__0)) return true;
        if (Utils.ContainsChinese(__0)) return true;

        var translated = TranslationManager.TryTranslate(__0);
        if (translated != null)
        {
            _isSettingTmpText = true;
            try { __instance.text = translated; }
            finally { _isSettingTmpText = false; }
            FontManager.EnsureChineseFontSupport(__instance);
            return false;
        }
        
        MissingCollector.Collect(__0);
        return true;
    }

    private static void TmpPostfix(TMP_Text __instance)
    {
        if (_isSettingTmpText || __instance == null) return;

        var text = __instance.text;
        if (string.IsNullOrEmpty(text)) return;

        // 检查混合文本中的英文日期
        if (Utils.HasEnglishMonth(text))
        {
            var result = DateTranslator.TryTranslateWithTags(text);
            if (result != null && result != text)
            {
                SetTmpText(__instance, result);
                return;
            }
        }

        if (Utils.ContainsChinese(text))
        {
            FontManager.EnsureChineseFontSupport(__instance);
            return;
        }

        // 尝试日期翻译
        var dateResult = DateTranslator.TryTranslateWithTags(text);
        if (dateResult != null)
        {
            SetTmpText(__instance, dateResult);
            return;
        }

        // 尝试普通翻译
        var translated = TranslationManager.TryTranslate(text);
        if (translated != null)
        {
            SetTmpText(__instance, translated);
            return;
        }

        FontManager.EnsureChineseFontSupport(__instance);
    }

    private static void SetTmpText(TMP_Text instance, string text)
    {
        _isSettingTmpText = true;
        try { instance.text = text; }
        finally { _isSettingTmpText = false; }
        FontManager.EnsureChineseFontSupport(instance);
    }

    #endregion

    #region UGUI Patches

    private static bool PatchUgui(Harmony harmony)
    {
        try
        {
            var setter = typeof(UnityEngine.UI.Text).GetProperty("text")?.GetSetMethod();
            if (setter == null) return false;

            harmony.Patch(setter, prefix: new HarmonyMethod(AccessTools.Method(typeof(HarmonyPatches), nameof(UguiPrefix))));
            Plugin.LogSrc.LogInfo("✓ Patched UGUI Text");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogSrc.LogWarning($"PatchUgui error: {ex.Message}");
            return false;
        }
    }

    private static bool UguiPrefix(UnityEngine.UI.Text __instance, string value)
    {
        if (_isSettingUguiText || __instance == null || string.IsNullOrEmpty(value)) return true;
        if (Utils.ContainsChinese(value)) return true;

        var translated = TranslationManager.TryTranslate(value);
        if (translated != null)
        {
            _isSettingUguiText = true;
            try { __instance.text = translated; }
            finally { _isSettingUguiText = false; }
            return false;
        }
        
        MissingCollector.Collect(value);
        return true;
    }

    #endregion

    #region Canvas Patches

    private static bool PatchCanvas(Harmony harmony)
    {
        try
        {
            var canvasType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("UnityEngine.Canvas", false))
                .FirstOrDefault(t => t != null);
            
            if (canvasType == null) return false;

            var onEnable = canvasType.GetMethod("OnEnable", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (onEnable == null) return false;

            harmony.Patch(onEnable, postfix: new HarmonyMethod(AccessTools.Method(typeof(HarmonyPatches), nameof(CanvasPostfix))));
            Plugin.LogSrc.LogInfo("✓ Patched Canvas.OnEnable");
            return true;
        }
        catch (Exception ex)
        {
            Plugin.LogSrc.LogWarning($"PatchCanvas error: {ex.Message}");
            return false;
        }
    }

    private static void CanvasPostfix(object __instance)
    {
        try
        {
            if (__instance == null) return;
            
            var type = __instance.GetType();
            var renderMode = type.GetProperty("renderMode")?.GetValue(__instance);
            if (renderMode != null && Convert.ToInt32(renderMode) == 2) return; // WorldSpace
            
            var go = type.GetProperty("gameObject")?.GetValue(__instance) as GameObject;
            if (go != null) ComponentScanner.ScanChildren(go);
        }
        catch { /* ignore */ }
    }

    #endregion
}

using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System;
using System.Linq;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace SskCnPoc;

[BepInPlugin("ssk.cn.poc", "SSK CN PoC", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource LogSrc = null!;
    private Harmony _harmony = null!;
    private static bool _patched = false;

    public override void Load()
    {
        LogSrc = Log;
        LogSrc.LogInfo("SSK CN PoC Load()");

        TranslationManager.LoadTranslations();
        FontManager.Initialize();

        _harmony = new Harmony("ssk.cn.poc.harmony");
        
        // 尝试立即 Patch（如果程序集已加载）
        if (!TryPatch())
        {
            // 如果失败，注册场景加载回调，在场景加载时重试
            LogSrc.LogInfo("TMP not ready, will retry on scene load...");
            SceneManager.sceneLoaded += (UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
        }

        LogSrc.LogInfo("SSK CN PoC Load() completed");
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (_patched) return;
        
        LogSrc.LogInfo($"Scene loaded: {scene.name}, attempting patch...");
        
        // 获取 Plugin 实例并尝试 patch
        // 由于是静态回调，需要通过静态方法访问
        TryPatchStatic();
    }

    private static Harmony? _harmonyStatic;
    
    private bool TryPatch()
    {
        _harmonyStatic = _harmony;
        return TryPatchStatic();
    }

    private static bool TryPatchStatic()
    {
        if (_patched) return true;
        if (_harmonyStatic == null) return false;
        
        try
        {
            if (PatchTmpSetTextInternal(_harmonyStatic))
            {
                _patched = true;
                LogSrc.LogInfo("SSK CN PoC patched successfully");
                return true;
            }
        }
        catch (Exception ex)
        {
            LogSrc.LogWarning($"Patch attempt failed: {ex.Message}");
        }
        return false;
    }

    private static bool PatchTmpSetTextInternal(Harmony harmony)
    {
        try
        {
            Type? tmpType = null;
            
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var assemblyName = assembly.GetName().Name;
                    if (assemblyName != null && (
                        assemblyName.StartsWith("System") || 
                        assemblyName.StartsWith("Microsoft") ||
                        assemblyName.StartsWith("mscorlib") ||
                        assemblyName.StartsWith("netstandard")))
                    {
                        continue;
                    }
                    
                    tmpType = assembly.GetType("TMPro.TMP_Text", false);
                    if (tmpType != null) break;
                }
                catch
                {
                    continue;
                }
            }

            if (tmpType == null)
            {
                LogSrc.LogWarning("Cannot find TMPro.TMP_Text - TMP assembly may not be loaded yet");
                return false;
            }

            LogSrc.LogInfo($"Found TMP_Text: {tmpType.FullName}");

            var allSetTextMethods = tmpType.GetMethods()
                .Where(m => m.Name == "SetText" && m.GetParameters().Length == 1)
                .ToArray();

            LogSrc.LogInfo($"Found {allSetTextMethods.Length} SetText methods");

            var postfixMethod = AccessTools.Method(typeof(Plugin), nameof(SetTextStringPostfix));
            
            int patchedCount = 0;
            foreach (var setTextMethod in allSetTextMethods)
            {
                try
                {
                    harmony.Patch(setTextMethod, postfix: new HarmonyMethod(postfixMethod));
                    LogSrc.LogInfo($"✓ Patched SetText({setTextMethod.GetParameters()[0].ParameterType.Name})");
                    patchedCount++;
                }
                catch (Exception ex)
                {
                    LogSrc.LogWarning($"Failed to patch SetText: {ex.Message}");
                }
            }

            // Patch text 属性的 setter
            var textProperty = tmpType.GetProperty("text", 
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            
            if (textProperty?.CanWrite == true)
            {
                var textSetter = textProperty.GetSetMethod();
                if (textSetter != null)
                {
                    try
                    {
                        harmony.Patch(textSetter, postfix: new HarmonyMethod(postfixMethod));
                        LogSrc.LogInfo($"✓ Patched text property setter");
                        patchedCount++;
                    }
                    catch (Exception ex)
                    {
                        LogSrc.LogWarning($"Failed to patch text property setter: {ex.Message}");
                    }
                }
            }
            
            return patchedCount > 0;
        }
        catch (Exception ex)
        {
            LogSrc.LogError($"PatchTmpSetText error: {ex}");
            return false;
        }
    }

    /// <summary>
    /// Harmony Postfix - 在 SetText 执行后被调用
    /// </summary>
    private static void SetTextStringPostfix(object __instance)
    {
        try
        {
            if (__instance == null) return;

            var textProperty = __instance.GetType().GetProperty("text");
            if (textProperty == null || !textProperty.CanRead) return;

            var currentText = textProperty.GetValue(__instance) as string;
            if (string.IsNullOrEmpty(currentText)) return;

            var translated = TranslationManager.TryTranslate(currentText);
            
            if (translated != null)
            {
                LogSrc.LogInfo($"[TRANSLATION] '{currentText}' → '{translated}'");
                
                if (textProperty.CanWrite)
                {
                    textProperty.SetValue(__instance, translated);
                }
            }
            else
            {
                MissingCollector.Collect(currentText);
            }

            FontManager.EnsureChineseFontSupport(__instance);
        }
        catch (Exception ex)
        {
            LogSrc.LogError($"SetTextStringPostfix error: {ex}");
        }
    }
}

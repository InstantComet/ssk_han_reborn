using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;
using Il2CppInterop.Runtime;

namespace SskCnPoc;

[BepInPlugin("ssk.cn.poc", "SSK CN PoC", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource LogSrc = null!;
    private Harmony _harmony = null!;
    private static bool _patched = false;
    private static GameObject? _scannerObject;

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
        else
        {
            // 已经 patch 成功，注册场景加载回调用于扫描静态文本
            SceneManager.sceneLoaded += (UnityAction<Scene, LoadSceneMode>)OnSceneLoadedScanText;
        }

        LogSrc.LogInfo("SSK CN PoC Load() completed");
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        if (_patched) return;
        
        LogSrc.LogInfo($"Scene loaded: {scene.name}, attempting patch...");
        
        // 获取 Plugin 实例并尝试 patch
        // 由于是静态回调，需要通过静态方法访问
        if (TryPatchStatic())
        {
            // Patch 成功后，替换回调为扫描文本的回调
            SceneManager.sceneLoaded -= (UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
            SceneManager.sceneLoaded += (UnityAction<Scene, LoadSceneMode>)OnSceneLoadedScanText;
            // 立即扫描当前场景
            ScanAndTranslateAllTmpText();
        }
    }

    private static TextScanner? _scanner;

    private static void OnSceneLoadedScanText(Scene scene, LoadSceneMode mode)
    {
        LogSrc.LogInfo($"Scene loaded: {scene.name}, scanning TMP texts...");
        
        // 清除已翻译的缓存，因为场景变化后对象可能被销毁重建
        lock (_translatedLock)
        {
            _translatedInstanceIds.Clear();
        }
        
        // 立即扫描
        ScanAndTranslateAllTmpText();
        
        // 启动定期扫描协程（用于捕获动态加载的UI）
        StartPeriodicScanner();
        
        // 重置扫描器到快速模式
        if (_scanner != null)
        {
            _scanner.ResetToFastMode();
        }
    }

    private static void StartPeriodicScanner()
    {
        if (_scannerObject != null) return;
        
        _scannerObject = new GameObject("SskCnPoc_Scanner");
        UnityEngine.Object.DontDestroyOnLoad(_scannerObject);
        _scanner = _scannerObject.AddComponent<TextScanner>();
        _scanner.StartScanning();
        LogSrc.LogInfo("Started periodic TMP text scanner");
    }

    // 缓存已经翻译过的对象实例ID，避免重复翻译
    private static readonly HashSet<int> _translatedInstanceIds = new();
    private static readonly object _translatedLock = new();

    /// <summary>
    /// 检查字符串是否包含中文字符
    /// </summary>
    private static bool ContainsChinese(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        foreach (char c in text)
        {
            if (c >= 0x4E00 && c <= 0x9FFF) return true;
        }
        return false;
    }

    // 用于快速检测英文月份的正则
    private static readonly System.Text.RegularExpressions.Regex MonthRegex = new(
        @"\b(Jan(?:uary)?|Feb(?:ruary)?|Mar(?:ch)?|Apr(?:il)?|May|June?|July?|Aug(?:ust)?|Sep(?:t(?:ember)?)?|Oct(?:ober)?|Nov(?:ember)?|Dec(?:ember)?)\b",
        System.Text.RegularExpressions.RegexOptions.Compiled | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    /// <summary>
    /// 检查文本是否包含英文月份名
    /// </summary>
    private static bool HasEnglishMonth(string text) => !string.IsNullOrEmpty(text) && MonthRegex.IsMatch(text);

    private static int _lastObjectCount = 0;

    /// <summary>
    /// 扫描场景中所有 TMP 文本组件并翻译静态文本
    /// </summary>
    internal static void ScanAndTranslateAllTmpText()
    {
        try
        {
            // 直接使用 TMPro.TMP_Text 类型查找所有组件
            var allTmpTexts = Resources.FindObjectsOfTypeAll<TMPro.TMP_Text>();
            
            // 调试：列出 TMP 组件数量变化
            if (allTmpTexts.Length > 0 && _lastObjectCount != allTmpTexts.Length)
            {
                _lastObjectCount = allTmpTexts.Length;
                LogSrc.LogInfo($"Found {allTmpTexts.Length} TMP components");
            }
            
            int scanned = 0;
            int translated = 0;
            int missing = 0;

            foreach (var tmp in allTmpTexts)
            {
                try
                {
                    if (tmp == null) continue;
                    
                    int instanceId = tmp.GetInstanceID();
                    lock (_translatedLock)
                    {
                        if (_translatedInstanceIds.Contains(instanceId)) continue;
                    }

                    var currentText = tmp.text;
                    if (string.IsNullOrEmpty(currentText)) continue;
                    
                    // 跳过已经是中文的文本（已被翻译过）
                    if (ContainsChinese(currentText))
                    {
                        lock (_translatedLock)
                        {
                            _translatedInstanceIds.Add(instanceId);
                        }
                        continue;
                    }

                    scanned++;

                    var translatedText = TranslationManager.TryTranslate(currentText);
                    if (translatedText != null)
                    {
                        tmp.text = translatedText;
                        translated++;
                    }
                    else
                    {
                        MissingCollector.Collect(currentText);
                        missing++;
                    }
                    
                    // 无论翻译成功还是 missing，都记录已处理
                    lock (_translatedLock)
                    {
                        _translatedInstanceIds.Add(instanceId);
                    }

                    FontManager.EnsureChineseFontSupport(tmp);
                }
                catch (Exception ex)
                {
                    LogSrc.LogDebug($"Error processing TMP component: {ex.Message}");
                }
            }

            LogSrc.LogInfo($"Scanned {scanned} TMP texts: {translated} translated, {missing} missing");
            
            // 同时扫描 UGUI Text 组件
            ScanUguiText();
        }
        catch (Exception ex)
        {
            LogSrc.LogError($"ScanAndTranslateAllTmpText error: {ex}");
        }
    }

    /// <summary>
    /// 扫描 UGUI Text 组件（UnityEngine.UI.Text）
    /// </summary>
    private static void ScanUguiText()
    {
        try
        {
            // 直接遍历所有 GameObject 查找 UI.Text 组件
            var allGameObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            
            int scanned = 0;
            int translated = 0;
            int missing = 0;
            int foundUguiText = 0;
            
            foreach (var go in allGameObjects)
            {
                if (go == null) continue;
                if (go.scene.name == null) continue; // 跳过预制体
                
                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    
                    try
                    {
                        var il2cppType = comp.GetIl2CppType();
                        if (il2cppType?.FullName != "UnityEngine.UI.Text") continue;
                        
                        foundUguiText++;
                        int instanceId = comp.GetInstanceID();
                        
                        lock (_translatedLock)
                        {
                            if (_translatedInstanceIds.Contains(instanceId))
                                continue;
                        }

                        // 使用 Il2Cpp 类型转换
                        var uiText = comp.TryCast<UnityEngine.UI.Text>();
                        if (uiText == null)
                        {
                            LogSrc.LogDebug($"[UGUI] Failed to cast on {go.name}");
                            continue;
                        }
                        
                        var currentText = uiText.text;
                        if (string.IsNullOrEmpty(currentText)) continue;
                        
                        // 如果文本已经包含中文，跳过
                        if (ContainsChinese(currentText)) continue;
                        
                        scanned++;
                        
                        var translatedText = TranslationManager.TryTranslate(currentText);
                        if (translatedText != null)
                        {
                            uiText.text = translatedText;
                            translated++;
                            LogSrc.LogInfo($"[UGUI] '{currentText}' → '{translatedText}'");
                        }
                        else
                        {
                            MissingCollector.Collect(currentText);
                            missing++;
                        }
                        
                        // 无论成功还是 missing，都标记为已处理
                        lock (_translatedLock)
                        {
                            _translatedInstanceIds.Add(instanceId);
                        }
                    }
                    catch (Exception ex)
                    {
                        LogSrc.LogDebug($"Error processing UGUI Text: {ex.Message}");
                    }
                }
            }

            if (scanned > 0 || foundUguiText > 0)
            {
                LogSrc.LogInfo($"Scanned {scanned} UGUI texts (found {foundUguiText}): {translated} translated, {missing} missing");
            }
        }
        catch (Exception ex)
        {
            LogSrc.LogDebug($"ScanUguiText error: {ex.Message}");
        }
    }

    /// <summary>
    /// 使用缓存的组件数组进行快速扫描（避免每次调用 FindObjectsOfType）
    /// </summary>
    internal static void ScanCachedComponents(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TMPro.TMP_Text> tmpTexts,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<UnityEngine.UI.Text> uguiTexts)
    {
        int translated = 0;
        
        // 扫描 TMP 组件
        foreach (var tmp in tmpTexts)
        {
            try
            {
                if (tmp == null) continue;
                
                int instanceId = tmp.GetInstanceID();
                lock (_translatedLock)
                {
                    if (_translatedInstanceIds.Contains(instanceId))
                        continue;
                }

                var currentText = tmp.text;
                if (string.IsNullOrEmpty(currentText)) continue;
                
                if (ContainsChinese(currentText))
                {
                    lock (_translatedLock) { _translatedInstanceIds.Add(instanceId); }
                    continue;
                }

                var translatedText = TranslationManager.TryTranslate(currentText);
                if (translatedText != null)
                {
                    tmp.text = translatedText;
                    translated++;
                }
                else
                {
                    MissingCollector.Collect(currentText);
                }
                
                lock (_translatedLock) { _translatedInstanceIds.Add(instanceId); }
                FontManager.EnsureChineseFontSupport(tmp);
            }
            catch { /* ignore individual errors */ }
        }
        
        // 扫描 UGUI 组件
        foreach (var uiText in uguiTexts)
        {
            try
            {
                if (uiText == null) continue;
                
                int instanceId = uiText.GetInstanceID();
                lock (_translatedLock)
                {
                    if (_translatedInstanceIds.Contains(instanceId))
                        continue;
                }

                var currentText = uiText.text;
                if (string.IsNullOrEmpty(currentText)) continue;
                
                if (ContainsChinese(currentText))
                {
                    lock (_translatedLock) { _translatedInstanceIds.Add(instanceId); }
                    continue;
                }

                var translatedText = TranslationManager.TryTranslate(currentText);
                if (translatedText != null)
                {
                    uiText.text = translatedText;
                    translated++;
                }
                else
                {
                    MissingCollector.Collect(currentText);
                }
                
                lock (_translatedLock) { _translatedInstanceIds.Add(instanceId); }
            }
            catch { /* ignore individual errors */ }
        }
        
        if (translated > 0)
        {
            LogSrc.LogDebug($"ScanCachedComponents: translated {translated} texts");
        }
    }

    private static bool _debugDumpDone = false;
    private static int _lastComponentCount = 0;
    
    /// <summary>
    /// 调试：扫描场景中所有可能包含文本的组件
    /// </summary>
    internal static void DebugDumpAllTextComponents()
    {
        try
        {
            var allGameObjects = Resources.FindObjectsOfTypeAll<GameObject>();
            var componentTypeCounts = new Dictionary<string, int>();
            
            int totalComponents = 0;
            foreach (var go in allGameObjects)
            {
                if (go == null) continue;
                if (go.scene.name == null) continue; // 跳过预制体
                
                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    totalComponents++;
                    
                    // 使用 Il2Cpp 类型名称
                    string typeName;
                    try
                    {
                        var il2cppType = comp.GetIl2CppType();
                        typeName = il2cppType?.FullName ?? comp.GetType().FullName ?? "Unknown";
                    }
                    catch
                    {
                        typeName = comp.GetType().FullName ?? "Unknown";
                    }
                    
                    // 统计类型
                    if (!componentTypeCounts.ContainsKey(typeName))
                        componentTypeCounts[typeName] = 0;
                    componentTypeCounts[typeName]++;
                }
            }
            
            // 如果组件总数变化了，重新输出
            if (Math.Abs(totalComponents - _lastComponentCount) < 100 && _debugDumpDone) return;
            _lastComponentCount = totalComponents;
            _debugDumpDone = true;
            
            LogSrc.LogInfo($"=== DEBUG: Total {totalComponents} components, {componentTypeCounts.Count} types ===");
            
            // 输出所有类型（按数量排序，只输出前 30 个）
            int outputCount = 0;
            foreach (var kvp in componentTypeCounts.OrderByDescending(x => x.Value))
            {
                if (outputCount++ >= 30) break;
                LogSrc.LogInfo($"[TYPE] {kvp.Key}: {kvp.Value}");
            }
            
            // 输出所有 TMP 文本内容
            LogSrc.LogInfo("=== TMP TEXT CONTENTS ===");
            foreach (var go in allGameObjects)
            {
                if (go == null) continue;
                var components = go.GetComponents<Component>();
                foreach (var comp in components)
                {
                    if (comp == null) continue;
                    var typeName = comp.GetIl2CppType().FullName;
                    if (typeName.Contains("TextMeshPro"))
                    {
                        var textProp = comp.GetType().GetProperty("text");
                        if (textProp?.CanRead == true)
                        {
                            var text = textProp.GetValue(comp) as string;
                            if (!string.IsNullOrWhiteSpace(text) && text.Length < 100)
                            {
                                LogSrc.LogInfo($"[TMP-CONTENT] '{text}'");
                            }
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogSrc.LogError($"DebugDumpAllTextComponents error: {ex}");
        }
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
            bool tmpPatched = PatchTmpSetTextInternal(_harmonyStatic);
            bool uguiPatched = PatchUguiTextInternal(_harmonyStatic);
            bool canvasPatched = PatchCanvasOnEnable(_harmonyStatic);
            
            if (tmpPatched || uguiPatched)
            {
                _patched = true;
                LogSrc.LogInfo($"SSK CN PoC patched successfully (TMP:{tmpPatched}, UGUI:{uguiPatched}, Canvas:{canvasPatched})");
                return true;
            }
        }
        catch (Exception ex)
        {
            LogSrc.LogWarning($"Patch attempt failed: {ex.Message}");
        }
        return false;
    }
    
    /// <summary>
    /// Patch Canvas.OnEnable 来检测 UI 面板激活
    /// </summary>
    private static bool PatchCanvasOnEnable(Harmony harmony)
    {
        try
        {
            // 使用反射获取 Canvas 类型（避免编译时依赖问题）
            Type? canvasType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    canvasType = assembly.GetType("UnityEngine.Canvas", false);
                    if (canvasType != null) break;
                }
                catch { continue; }
            }
            
            if (canvasType == null)
            {
                LogSrc.LogWarning("Canvas type not found");
                return false;
            }
            
            var onEnableMethod = canvasType.GetMethod("OnEnable", 
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            
            if (onEnableMethod != null)
            {
                var postfixMethod = AccessTools.Method(typeof(Plugin), nameof(CanvasOnEnablePostfix));
                harmony.Patch(onEnableMethod, postfix: new HarmonyMethod(postfixMethod));
                LogSrc.LogInfo("✓ Patched Canvas.OnEnable (postfix) for UI panel detection");
                return true;
            }
            
            LogSrc.LogWarning("Could not find Canvas.OnEnable method");
            return false;
        }
        catch (Exception ex)
        {
            LogSrc.LogWarning($"PatchCanvasOnEnable error: {ex.Message}");
            return false;
        }
    }
    
    /// <summary>
    /// Canvas.OnEnable postfix - 当 Canvas 激活时扫描其子对象
    /// 使用 object 类型来避免编译时对 Canvas 的依赖
    /// </summary>
    private static void CanvasOnEnablePostfix(object __instance)
    {
        try
        {
            if (__instance == null) return;
            
            // 使用反射获取属性
            var instanceType = __instance.GetType();
            var renderModeProp = instanceType.GetProperty("renderMode");
            var gameObjectProp = instanceType.GetProperty("gameObject");
            
            if (renderModeProp != null)
            {
                var renderMode = renderModeProp.GetValue(__instance);
                // RenderMode.WorldSpace = 2
                if (renderMode != null && Convert.ToInt32(renderMode) == 2) return;
            }
            
            if (gameObjectProp != null)
            {
                var gameObject = gameObjectProp.GetValue(__instance) as GameObject;
                if (gameObject != null)
                {
                    ScanCanvasChildren(gameObject);
                }
            }
        }
        catch (Exception ex)
        {
            LogSrc.LogDebug($"CanvasOnEnablePostfix error: {ex.Message}");
        }
    }
    
    /// <summary>
    /// 只扫描指定 GameObject 下的子文本组件（高效，不遍历整个场景）
    /// </summary>
    private static void ScanCanvasChildren(GameObject root)
    {
        int translated = 0;
        
        // 扫描 TMP 组件
        var tmpTexts = root.GetComponentsInChildren<TMP_Text>(true);
        foreach (var tmp in tmpTexts)
        {
            try
            {
                if (tmp == null) continue;
                
                int instanceId = tmp.GetInstanceID();
                lock (_translatedLock)
                {
                    if (_translatedInstanceIds.Contains(instanceId))
                        continue;
                }

                var currentText = tmp.text;
                if (string.IsNullOrEmpty(currentText)) continue;
                
                if (ContainsChinese(currentText))
                {
                    lock (_translatedLock) { _translatedInstanceIds.Add(instanceId); }
                    continue;
                }

                var translatedText = TranslationManager.TryTranslate(currentText);
                if (translatedText != null)
                {
                    tmp.text = translatedText;
                    translated++;
                }
                else
                {
                    MissingCollector.Collect(currentText);
                }
                
                lock (_translatedLock) { _translatedInstanceIds.Add(instanceId); }
                FontManager.EnsureChineseFontSupport(tmp);
            }
            catch { /* ignore */ }
        }
        
        // 扫描 UGUI Text 组件
        var uguiTexts = root.GetComponentsInChildren<UnityEngine.UI.Text>(true);
        foreach (var uiText in uguiTexts)
        {
            try
            {
                if (uiText == null) continue;
                
                int instanceId = uiText.GetInstanceID();
                lock (_translatedLock)
                {
                    if (_translatedInstanceIds.Contains(instanceId))
                        continue;
                }

                var currentText = uiText.text;
                if (string.IsNullOrEmpty(currentText)) continue;
                
                if (ContainsChinese(currentText))
                {
                    lock (_translatedLock) { _translatedInstanceIds.Add(instanceId); }
                    continue;
                }

                var translatedText = TranslationManager.TryTranslate(currentText);
                if (translatedText != null)
                {
                    uiText.text = translatedText;
                    translated++;
                }
                else
                {
                    MissingCollector.Collect(currentText);
                }
                
                lock (_translatedLock) { _translatedInstanceIds.Add(instanceId); }
            }
            catch { /* ignore */ }
        }
        
        if (translated > 0)
        {
            LogSrc.LogInfo($"ScanCanvasChildren '{root.name}': translated {translated} texts");
        }
    }

    /// <summary>
    /// Patch UnityEngine.UI.Text 的 text 属性 setter
    /// </summary>
    private static bool PatchUguiTextInternal(Harmony harmony)
    {
        try
        {
            var uguiTextType = typeof(UnityEngine.UI.Text);
            LogSrc.LogInfo($"Found UnityEngine.UI.Text: {uguiTextType.FullName}");

            var textProperty = uguiTextType.GetProperty("text", 
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            
            if (textProperty?.CanWrite == true)
            {
                var textSetter = textProperty.GetSetMethod();
                if (textSetter != null)
                {
                    var prefixMethod = AccessTools.Method(typeof(Plugin), nameof(UguiTextSetterPrefix));
                    harmony.Patch(textSetter, prefix: new HarmonyMethod(prefixMethod));
                    LogSrc.LogInfo($"✓ Patched UnityEngine.UI.Text.text property setter (prefix)");
                    return true;
                }
            }
            
            LogSrc.LogWarning("Could not find UnityEngine.UI.Text.text setter");
            return false;
        }
        catch (Exception ex)
        {
            LogSrc.LogWarning($"PatchUguiTextInternal error: {ex.Message}");
            return false;
        }
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

            // 列出所有可能的SetText方法（包括多参数版本）
            var allSetTextMethods = tmpType.GetMethods()
                .Where(m => m.Name == "SetText")
                .ToArray();
            
            LogSrc.LogInfo($"[DEBUG] Total SetText overloads: {allSetTextMethods.Length}");
            foreach (var m in allSetTextMethods)
            {
                var pTypes = string.Join(", ", m.GetParameters().Select(p => p.ParameterType.Name));
                LogSrc.LogInfo($"  SetText({pTypes})");
            }
            
            // 只取单参数版本进行 Prefix 拦截
            var singleParamSetTextMethods = allSetTextMethods
                .Where(m => m.GetParameters().Length == 1)
                .ToArray();

            LogSrc.LogInfo($"Found {singleParamSetTextMethods.Length} single-param SetText methods");

            var prefixMethod = AccessTools.Method(typeof(Plugin), nameof(TmpTextSetterPrefix));
            var postfixMethod = AccessTools.Method(typeof(Plugin), nameof(TmpTextSetterPostfix));
            
            int patchedCount = 0;
            foreach (var setTextMethod in singleParamSetTextMethods)
            {
                try
                {
                    // 使用 prefix 拦截并翻译，postfix 确保字体
                    harmony.Patch(setTextMethod, 
                        prefix: new HarmonyMethod(prefixMethod),
                        postfix: new HarmonyMethod(postfixMethod));
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
                        harmony.Patch(textSetter, 
                            prefix: new HarmonyMethod(prefixMethod),
                            postfix: new HarmonyMethod(postfixMethod));
                        LogSrc.LogInfo($"✓ Patched text property setter");
                        patchedCount++;
                    }
                    catch (Exception ex)
                    {
                        LogSrc.LogWarning($"Failed to patch text property setter: {ex.Message}");
                    }
                }
            }
            
            // Also patch SetCharArray methods
            var setCharArrayMethods = tmpType.GetMethods()
                .Where(m => m.Name == "SetCharArray")
                .ToArray();
            
            LogSrc.LogInfo($"Found {setCharArrayMethods.Length} SetCharArray methods");
            
            foreach (var method in setCharArrayMethods)
            {
                try
                {
                    // SetCharArray 需要不同的 prefix - 我们在 postfix 中重新翻译
                    harmony.Patch(method, postfix: new HarmonyMethod(postfixMethod));
                    LogSrc.LogInfo($"✓ Patched SetCharArray (postfix only)");
                    patchedCount++;
                }
                catch (Exception ex)
                {
                    LogSrc.LogWarning($"Failed to patch SetCharArray: {ex.Message}");
                }
            }
            
            // Patch 多参数的 SetText 方法（可能用于日期等动态文本）
            var multiParamSetTextMethods = allSetTextMethods
                .Where(m => m.GetParameters().Length > 1)
                .ToArray();
            
            LogSrc.LogInfo($"Found {multiParamSetTextMethods.Length} multi-param SetText methods");
            
            foreach (var method in multiParamSetTextMethods)
            {
                try
                {
                    // 多参数版本只能用 postfix
                    harmony.Patch(method, postfix: new HarmonyMethod(postfixMethod));
                    var pTypes = string.Join(", ", method.GetParameters().Select(p => p.ParameterType.Name));
                    LogSrc.LogInfo($"✓ Patched SetText({pTypes}) (postfix only)");
                    patchedCount++;
                }
                catch (Exception ex)
                {
                    LogSrc.LogWarning($"Failed to patch multi-param SetText: {ex.Message}");
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

    // 防止 TMP 递归调用的标志
    [ThreadStatic]
    private static bool _isSettingTmpText;

    /// <summary>
    /// Harmony Prefix for TMP - 拦截设置，直接设置翻译后的文本
    /// 使用 __0 引用第一个参数（要设置的文本）
    /// </summary>
    private static bool TmpTextSetterPrefix(TMP_Text __instance, string __0)
    {
        if (_isSettingTmpText) return true;  // 防止递归，继续执行原方法
        
        try
        {
            if (__instance == null) return true;
            if (string.IsNullOrEmpty(__0)) return true;
            
            // 如果已经是中文，继续执行原方法并确保字体
            if (ContainsChinese(__0))
            {
                // 让原方法执行，然后在 postfix 中设置字体
                return true;
            }
            
            var translated = TranslationManager.TryTranslate(__0);
            
            if (translated != null)
            {
                LogSrc.LogInfo($"[TMP] '{__0}' → '{translated}'");
                
                // 直接设置翻译后的文本，阻止原方法执行
                _isSettingTmpText = true;
                try
                {
                    __instance.text = translated;
                }
                finally
                {
                    _isSettingTmpText = false;
                }
                
                FontManager.EnsureChineseFontSupport(__instance);
                
                // 检测到翻译，触发快速扫描来处理同一面板的其他静态文本
                TriggerFastScanOnNewText();
                
                return false;  // 阻止原方法执行（我们已经设置了翻译后的文本）
            }
            else
            {
                MissingCollector.Collect(__0);
                
                // 检测到未翻译的英文，也触发扫描（可能是新 UI 打开了）
                TriggerFastScanOnNewText();
            }
        }
        catch (Exception ex)
        {
            LogSrc.LogError($"TmpTextSetterPrefix error: {ex}");
        }
        
        return true;  // 没有翻译，继续执行原方法
    }
    
    // 防止频繁触发扫描的节流
    private static float _lastTriggerTime = 0f;
    private const float TriggerCooldown = 0.5f;  // 0.5秒冷却
    
    private static void TriggerFastScanOnNewText()
    {
        if (_scanner == null) return;
        
        float currentTime = UnityEngine.Time.time;
        if (currentTime - _lastTriggerTime < TriggerCooldown) return;
        
        _lastTriggerTime = currentTime;
        _scanner.ResetToFastMode();
        LogSrc.LogInfo("Triggered fast scan due to new text detected");
    }

    /// <summary>
    /// Harmony Postfix for TMP - 确保字体支持，并处理可能遗漏的翻译
    /// </summary>
    private static void TmpTextSetterPostfix(TMP_Text __instance)
    {
        if (_isSettingTmpText) return;  // 防止递归
        
        try
        {
            if (__instance == null) return;
            
            // 获取当前文本
            var currentText = __instance.text;
            if (string.IsNullOrEmpty(currentText)) return;
            
            // 检查是否包含英文月份名（即使文本已有中文，也可能有未翻译的日期）
            if (HasEnglishMonth(currentText))
            {
                var inlineTranslated = DateTranslator.TryTranslateWithTags(currentText);
                if (inlineTranslated != null && inlineTranslated != currentText)
                {
                    _isSettingTmpText = true;
                    try { __instance.text = inlineTranslated; }
                    finally { _isSettingTmpText = false; }
                    FontManager.EnsureChineseFontSupport(__instance);
                    return;
                }
            }
            
            // 如果文本已经包含中文（且没有英文日期），只确保字体
            if (ContainsChinese(currentText))
            {
                FontManager.EnsureChineseFontSupport(__instance);
                return;
            }
            
            // 检查是否有日期需要翻译（这对 SetCharArray 等情况很重要）
            var dateTranslated = DateTranslator.TryTranslateWithTags(currentText);
            if (dateTranslated != null)
            {
                _isSettingTmpText = true;
                try { __instance.text = dateTranslated; }
                finally { _isSettingTmpText = false; }
                FontManager.EnsureChineseFontSupport(__instance);
                return;
            }
            
            // 尝试普通翻译
            var translated = TranslationManager.TryTranslate(currentText);
            if (translated != null)
            {
                LogSrc.LogInfo($"[TMP-POSTFIX] '{currentText}' → '{translated}'");
                _isSettingTmpText = true;
                try
                {
                    __instance.text = translated;
                }
                finally
                {
                    _isSettingTmpText = false;
                }
                FontManager.EnsureChineseFontSupport(__instance);
                return;
            }
            
            FontManager.EnsureChineseFontSupport(__instance);
        }
        catch (Exception ex)
        {
            LogSrc.LogError($"TmpTextSetterPostfix error: {ex}");
        }
    }

    // 防止递归调用的标志
    [ThreadStatic]
    private static bool _isSettingUguiText;

    /// <summary>
    /// Harmony Prefix for UnityEngine.UI.Text.text setter - 拦截设置，直接设置翻译后的文本
    /// </summary>
    private static bool UguiTextSetterPrefix(UnityEngine.UI.Text __instance, string value)
    {
        // 防止递归
        if (_isSettingUguiText) return true;
        
        try
        {
            if (__instance == null) return true;
            if (string.IsNullOrEmpty(value)) return true;
            
            // 如果已经是中文，继续执行原方法
            if (ContainsChinese(value)) return true;
            
            var translated = TranslationManager.TryTranslate(value);
            
            if (translated != null)
            {
                LogSrc.LogInfo($"[UGUI] '{value}' → '{translated}'");
                
                // 直接设置翻译后的文本，阻止原方法执行
                _isSettingUguiText = true;
                try
                {
                    __instance.text = translated;
                }
                finally
                {
                    _isSettingUguiText = false;
                }
                
                return false;  // 阻止原方法执行
            }
            else
            {
                MissingCollector.Collect(value);
            }
        }
        catch (Exception ex)
        {
            LogSrc.LogError($"UguiTextSetterPrefix error: {ex}");
        }
        
        return true;  // 没有翻译，继续执行原方法
    }
}

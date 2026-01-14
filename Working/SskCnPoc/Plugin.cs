using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEngine;
using Il2CppInterop.Runtime;

namespace SskCnPoc;

[BepInPlugin("ssk.cn.poc", "SSK CN PoC", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource LogSrc = null!;
    internal static Dictionary<string, string> Map = new(StringComparer.Ordinal);
    internal static HashSet<string> Missing = new(StringComparer.Ordinal);
    internal static string ChineseFontBundlePath = null!;

    private Harmony _harmony = null!;

    public override void Load()
    {
        LogSrc = Log;
        LogSrc.LogInfo("SSK CN PoC Load()");

        LoadTranslations();
        LoadChineseFont();

        _harmony = new Harmony("ssk.cn.poc.harmony");
        
        // 延迟 Patch，等待游戏程序集加载完成
        // 使用后台任务延迟执行，确保所有程序集都已加载
        System.Threading.Tasks.Task.Run(async () =>
        {
            try
            {
                // 等待 3 秒，让游戏完成初始化
                await System.Threading.Tasks.Task.Delay(3000);
                
                PatchTmpSetText();
                LogSrc.LogInfo("SSK CN PoC patched (delayed)");
            }
            catch (Exception ex)
            {
                LogSrc.LogError($"Delayed patch failed: {ex}");
            }
        });

        LogSrc.LogInfo("SSK CN PoC Load() completed, patch scheduled");
    }

    private static void RegisterSceneLoadedCallback()
    {
        // 空方法，保留作为占位符
    }

    private static void OnSceneLoaded(object scene, object mode)
    {
        // 空方法，保留作为占位符
    }

    private void PatchSkipLogos()
    {
        // 简化版本，跳过复杂的反射逻辑
        LogSrc.LogInfo("Logo skipping module loaded");
    }

    private static void LoadTranslations()
    {
        string path = Path.Combine(Paths.ConfigPath, "ssk_cn.txt");
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "New Game=新游戏\n", Encoding.UTF8);
        }

        foreach (var line in File.ReadAllLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            int idx = line.IndexOf('=');
            if (idx <= 0) continue;

            string en = line.Substring(0, idx).Trim();
            string zh = line[(idx + 1)..].Trim();

            if (en.Length == 0 || zh.Length == 0) continue;
            Map[en] = zh;
        }

        LogSrc.LogInfo($"Loaded {Map.Count} translations from ssk_cn.txt");
    }

    private static void LoadChineseFont()
    {
        try
        {
            LogSrc.LogInfo("Looking for Chinese TMP font AssetBundle...");
            
            // 查找 AssetBundle 文件（优先级：E:\ssk_han_reborn\font_output > BepInEx\plugins）
            string[] searchPaths = new[]
            {
                // 外部字体目录（新位置）
                @"E:\ssk_han_reborn\font_output\sourcehan",
                // 外部字体目录（旧位置，保持兼容）
                @"E:\ssk_han_reborn\font\sourcehan",
                // BepInEx 插件目录
                Path.Combine(Paths.PluginPath, "Fonts", "sourcehan"),
                Path.Combine(Paths.PluginPath, "sourcehan"),
                // 游戏数据目录
                Path.Combine(Paths.GameRootPath, "Sunless Skies_Data", "StreamingAssets", "Fonts", "sourcehan"),
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    ChineseFontBundlePath = path;
                    var fileSize = new FileInfo(path).Length / 1024.0 / 1024.0;
                    LogSrc.LogInfo($"✓ Found TMP font AssetBundle: {path} ({fileSize:F1} MB)");
                    break;
                }
            }

            if (string.IsNullOrEmpty(ChineseFontBundlePath))
            {
                LogSrc.LogWarning("Chinese TMP font AssetBundle not found!");
                LogSrc.LogWarning("Please place 'sourcehan' AssetBundle file in: E:\\ssk_han_reborn\\font\\ or BepInEx\\plugins\\Fonts\\");
            }
        }
        catch (Exception ex)
        {
            LogSrc.LogWarning($"LoadChineseFont error: {ex.Message}");
        }
    }

    private void PatchTmpSetText()
    {
        try
        {
            // 安全地获取已加载的 TMP_Text 类型
            Type tmpType = null;
            
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    // 跳过可能导致问题的程序集
                    var assemblyName = assembly.GetName().Name;
                    if (assemblyName.StartsWith("System") || 
                        assemblyName.StartsWith("Microsoft") ||
                        assemblyName.StartsWith("mscorlib") ||
                        assemblyName.StartsWith("netstandard"))
                    {
                        continue;
                    }
                    
                    tmpType = assembly.GetType("TMPro.TMP_Text", false);
                    if (tmpType != null) break;
                }
                catch
                {
                    // 忽略无法访问的程序集
                    continue;
                }
            }

            if (tmpType == null)
            {
                LogSrc.LogWarning("Cannot find TMPro.TMP_Text - TMP assembly may not be loaded yet");
                return;
            }

            LogSrc.LogInfo($"Found TMP_Text: {tmpType.FullName}");

            // 获取所有 SetText 方法（所有重载）
            var allSetTextMethods = tmpType.GetMethods()
                .Where(m => m.Name == "SetText" && m.GetParameters().Length == 1)
                .ToArray();

            LogSrc.LogInfo($"Found {allSetTextMethods.Length} SetText methods:");
            foreach (var m in allSetTextMethods)
            {
                LogSrc.LogInfo($"  - {m.Name}({m.GetParameters()[0].ParameterType.Name})");
            }

            var postfixMethod = AccessTools.Method(typeof(Plugin), nameof(SetTextStringPostfix));
            
            // Patch 所有 SetText 方法
            foreach (var setTextMethod in allSetTextMethods)
            {
                try
                {
                    var patchResult = _harmony.Patch(setTextMethod, postfix: new HarmonyMethod(postfixMethod));
                    LogSrc.LogInfo($"✓ Patched SetText({setTextMethod.GetParameters()[0].ParameterType.Name})");
                }
                catch (Exception ex)
                {
                    LogSrc.LogWarning($"Failed to patch SetText({setTextMethod.GetParameters()[0].ParameterType.Name}): {ex.Message}");
                }
            }

            // 也尝试 patch text 属性的 setter
            var textProperty = tmpType.GetProperty("text", 
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
            
            if (textProperty != null && textProperty.CanWrite)
            {
                var textSetter = textProperty.GetSetMethod();
                if (textSetter != null)
                {
                    try
                    {
                        _harmony.Patch(textSetter, postfix: new HarmonyMethod(postfixMethod));
                        LogSrc.LogInfo($"✓ Patched text property setter");
                    }
                    catch (Exception ex)
                    {
                        LogSrc.LogWarning($"Failed to patch text property setter: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            LogSrc.LogError($"PatchTmpSetText error: {ex}");
        }
    }

    // Harmony Postfix - 在 SetText 执行后被调用（通用版本）
    private static void SetTextStringPostfix(object __instance)
    {
        try
        {
            if (__instance == null) return;

            // 获取 text 属性值
            var textProperty = __instance.GetType().GetProperty("text");
            if (textProperty == null || !textProperty.CanRead) return;

            var currentText = textProperty.GetValue(__instance) as string;
            if (string.IsNullOrEmpty(currentText)) return;

            // 检查是否需要翻译
            if (Map.TryGetValue(currentText, out var zh))
            {
                LogSrc.LogInfo($"[TRANSLATION] '{currentText}' → '{zh}'");
                
                // 设置翻译后的文本
                if (textProperty.CanWrite)
                {
                    textProperty.SetValue(__instance, zh);
                }
            }

            // 确保字体支持中文 - 尝试加载字体
            EnsureChineseFontSupport(__instance);
        }
        catch (Exception ex)
        {
            LogSrc.LogError($"SetTextStringPostfix error: {ex}");
        }
    }

    private static object _cachedChineseFont = null;
    private static object _loadedAssetBundle = null;
    private static bool _fontLoadingAttempted = false;

    private static void EnsureChineseFontSupport(object tmpTextInstance)
    {
        try
        {
            if (tmpTextInstance == null) return;
            
            // 首次尝试加载 TextMeshPro 字体资源
            if (!_fontLoadingAttempted)
            {
                _fontLoadingAttempted = true;
                LogSrc.LogInfo(">>> Font loading attempt started");
                
                // 从 AssetBundle 加载 TMP 字体
                TryLoadFont_FromAssetBundle();

                if (_cachedChineseFont != null)
                {
                    LogSrc.LogInfo(">>> TMP Font resource ready for use");
                }
                else
                {
                    LogSrc.LogWarning(">>> Failed to load TMP font - text will show as boxes");
                }
            }

            // 应用已加载的字体到文本实例
            if (_cachedChineseFont != null)
            {
                ApplyFontToInstance(tmpTextInstance);
            }
        }
        catch (Exception ex)
        {
            LogSrc.LogError($"EnsureChineseFontSupport error: {ex}");
        }
    }

    private static void TryLoadFont_FromAssetBundle()
    {
        try
        {
            if (string.IsNullOrEmpty(ChineseFontBundlePath))
            {
                LogSrc.LogWarning("  No AssetBundle path configured");
                return;
            }

            if (!File.Exists(ChineseFontBundlePath))
            {
                LogSrc.LogWarning($"  AssetBundle file not found: {ChineseFontBundlePath}");
                return;
            }

            LogSrc.LogInfo($"  Loading AssetBundle from: {ChineseFontBundlePath}");

            // 直接加载 AssetBundle
            AssetBundle bundle = null;
            
            LogSrc.LogInfo($"  Calling AssetBundle.LoadFromFile...");
            bundle = AssetBundle.LoadFromFile(ChineseFontBundlePath);
            
            if (bundle == null)
            {
                LogSrc.LogWarning("  AssetBundle.LoadFromFile returned null");
                
                // 尝试使用 LoadFromMemory 作为备选
                LogSrc.LogInfo("  Trying LoadFromMemory as fallback...");
                var bundleBytes = File.ReadAllBytes(ChineseFontBundlePath);
                LogSrc.LogInfo($"  Read {bundleBytes.Length / 1024.0 / 1024.0:F2} MB from file");
                bundle = AssetBundle.LoadFromMemory(bundleBytes);
                
                if (bundle != null)
                {
                    LogSrc.LogInfo("  ✓ LoadFromMemory succeeded");
                }
                else
                {
                    LogSrc.LogWarning("  LoadFromMemory also returned null!");
                    return;
                }
            }

            // 如果没有找到已加载的 bundle，尝试加载
            if (bundle == null)
            {
                LogSrc.LogInfo($"  Calling AssetBundle.LoadFromFile...");
                bundle = AssetBundle.LoadFromFile(ChineseFontBundlePath);
                
                if (bundle == null)
                {
                    LogSrc.LogWarning("  AssetBundle.LoadFromFile returned null");
                    
                    // 尝试使用 LoadFromMemory 作为备选
                    LogSrc.LogInfo("  Trying LoadFromMemory as fallback...");
                    var bundleBytes = File.ReadAllBytes(ChineseFontBundlePath);
                    LogSrc.LogInfo($"  Read {bundleBytes.Length / 1024.0 / 1024.0:F2} MB from file");
                    bundle = AssetBundle.LoadFromMemory(bundleBytes);
                    
                    if (bundle != null)
                    {
                        LogSrc.LogInfo("  ✓ LoadFromMemory succeeded");
                    }
                    else
                    {
                        LogSrc.LogWarning("  LoadFromMemory also returned null!");
                        return;
                    }
                }
            }

            _loadedAssetBundle = bundle;
            LogSrc.LogInfo("  ✓ AssetBundle loaded/found successfully");

            // 列出 AssetBundle 中的所有资源名称
            var assetNames = bundle.GetAllAssetNames();
            LogSrc.LogInfo($"  AssetBundle contains {assetNames.Length} assets:");
            foreach (var name in assetNames)
            {
                LogSrc.LogInfo($"    - {name}");
            }

            if (assetNames.Length == 0)
            {
                LogSrc.LogWarning("  AssetBundle contains 0 assets! Bundle may be corrupted or built with wrong Unity version.");
                return;
            }

            // 获取 TMP_FontAsset 类型（用于泛型加载）
            Type tmproFontAssetType = null;
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    tmproFontAssetType = assembly.GetType("TMPro.TMP_FontAsset", false);
                    if (tmproFontAssetType != null) break;
                }
                catch { continue; }
            }

            if (tmproFontAssetType == null)
            {
                LogSrc.LogWarning("  TMP_FontAsset type not found");
                return;
            }

            LogSrc.LogInfo($"  Found TMP_FontAsset type: {tmproFontAssetType.FullName}");

            // 转换为 Il2Cpp 类型用于 LoadAsset
            Il2CppSystem.Type il2cppFontType = null;
            try
            {
                il2cppFontType = Il2CppInterop.Runtime.Il2CppType.From(tmproFontAssetType, false);
                LogSrc.LogInfo($"  Converted to Il2Cpp type: {il2cppFontType?.FullName ?? "null"}");
            }
            catch (Exception ex)
            {
                LogSrc.LogWarning($"  Failed to convert type: {ex.Message}");
            }

            // 尝试不同的资源名称（按照常见命名优先级排列）
            string[] tryNames = { 
                "SourceHanSansSC-Normal SDF",
                "assets/fonts/sourcehansanssc-normal sdf.asset",  // Unity 会把路径转小写
                "Assets/Fonts/SourceHanSansSC-Normal SDF.asset",
                "sourcehan"
            };

            // 把 bundle 中实际的资源名也加入尝试列表
            var allTryNames = tryNames.Concat(assetNames).Distinct().ToArray();

            foreach (var assetName in allTryNames)
            {
                try
                {
                    LogSrc.LogInfo($"    Trying to load: {assetName}");
                    
                    // 使用 LoadAsset(string, Type) 方法 - 使用 Il2Cpp Type
                    UnityEngine.Object asset = null;
                    if (il2cppFontType != null)
                    {
                        asset = bundle.LoadAsset(assetName, il2cppFontType);
                    }
                    else
                    {
                        // 回退：加载为通用 Object
                        asset = bundle.LoadAsset_Internal(assetName, Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.Object>());
                    }
                    
                    if (asset != null)
                    {
                        LogSrc.LogInfo($"  ✓ Loaded asset: {assetName}");
                        LogSrc.LogInfo($"    Raw asset type: {asset.GetType().FullName}");
                        
                        // 尝试转换为 TMP_FontAsset
                        object convertedFont = TryConvertToTMPFont(asset, tmproFontAssetType);
                        if (convertedFont != null)
                        {
                            _cachedChineseFont = convertedFont;
                            LogSrc.LogInfo($"  ✓ Converted to TMP_FontAsset successfully");
                            return;
                        }
                        else
                        {
                            // 保存原始对象，在应用时尝试转换
                            _cachedChineseFont = asset;
                            LogSrc.LogInfo($"  Stored as UnityEngine.Object, will convert on apply");
                            return;
                        }
                    }
                }
                catch (Exception ex)
                {
                    LogSrc.LogDebug($"    Failed: {ex.Message}");
                }
            }

            // 备选：加载所有资源
            LogSrc.LogInfo("  Trying LoadAllAssets...");
            try
            {
                var allAssets = bundle.LoadAllAssets();
                LogSrc.LogInfo($"  LoadAllAssets returned {allAssets.Length} assets");
                
                foreach (var asset in allAssets)
                {
                    if (asset != null)
                    {
                        LogSrc.LogInfo($"    Asset: {asset.name} (Type: {asset.GetType().Name})");
                        
                        // 检查是否是 TMP_FontAsset
                        if (tmproFontAssetType.IsAssignableFrom(asset.GetType()))
                        {
                            _cachedChineseFont = asset;
                            LogSrc.LogInfo($"  ✓ Found TMP_FontAsset: {asset.name}");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                LogSrc.LogWarning($"  LoadAllAssets failed: {ex.Message}");
            }

            LogSrc.LogWarning("  Failed to load any TMP_FontAsset from AssetBundle");
        }
        catch (Exception ex)
        {
            LogSrc.LogError($"  TryLoadFont_FromAssetBundle error: {ex}");
        }
    }

    private static object TryConvertToTMPFont(UnityEngine.Object asset, Type targetType)
    {
        try
        {
            if (asset == null || targetType == null) return null;
            
            // 方法1：如果已经是正确的类型
            var assetType = asset.GetType();
            if (targetType.IsAssignableFrom(assetType))
            {
                LogSrc.LogInfo($"    Asset is already {targetType.Name}");
                return asset;
            }
            
            // 方法2：检查 Il2Cpp 实际类型名
            LogSrc.LogInfo($"    Asset Il2Cpp type: {assetType.FullName}");
            
            // 方法3：使用 Il2CppSystem.Type 的 IsAssignableFrom
            var il2cppAssetType = asset.GetIl2CppType();
            LogSrc.LogInfo($"    Asset GetIl2CppType: {il2cppAssetType?.FullName ?? "null"}");
            
            // 如果 Il2Cpp 类型名包含 TMP_FontAsset，执行类型转换
            if (il2cppAssetType != null && il2cppAssetType.FullName.Contains("TMP_FontAsset"))
            {
                LogSrc.LogInfo($"    Asset is TMP_FontAsset in Il2Cpp! Attempting cast...");
                
                // 使用 Il2CppObjectBase 的 Cast 方法
                // asset.Cast<TMPro.TMP_FontAsset>() - 但是我们需要用反射因为类型是运行时的
                var castMethod = asset.GetType().GetMethod("Cast");
                if (castMethod != null && castMethod.IsGenericMethod)
                {
                    var genericCast = castMethod.MakeGenericMethod(targetType);
                    var result = genericCast.Invoke(asset, null);
                    if (result != null)
                    {
                        LogSrc.LogInfo($"    ✓ Cast succeeded! Result type: {result.GetType().FullName}");
                        return result;
                    }
                }
                
                // 备选：尝试 TryCast
                var tryCastMethod = asset.GetType().GetMethod("TryCast");
                if (tryCastMethod != null && tryCastMethod.IsGenericMethod)
                {
                    var genericTryCast = tryCastMethod.MakeGenericMethod(targetType);
                    var result = genericTryCast.Invoke(asset, null);
                    if (result != null)
                    {
                        LogSrc.LogInfo($"    ✓ TryCast succeeded! Result type: {result.GetType().FullName}");
                        return result;
                    }
                }
                
                LogSrc.LogWarning($"    Cast methods not found or failed");
            }
            
            LogSrc.LogWarning($"    Could not convert asset to {targetType.Name}");
            return asset;  // 返回原始对象
        }
        catch (Exception ex)
        {
            LogSrc.LogWarning($"    TryConvertToTMPFont error: {ex.Message}");
            return asset;  // 出错时返回原始对象
        }
    }
    private static void ApplyFontToInstance(object tmpTextInstance)
    {
        try
        {
            if (_cachedChineseFont == null) return;
            
            var instanceType = tmpTextInstance.GetType();

            // 设置 font 属性 (TMP_Text.font)
            var fontProperty = instanceType.GetProperty("font",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.Instance);

            if (fontProperty != null && fontProperty.CanWrite)
            {
                var currentFont = fontProperty.GetValue(tmpTextInstance);
                if (currentFont != _cachedChineseFont)
                {
                    try
                    {
                        // 直接尝试设置，Il2Cpp 应该能自动处理类型转换
                        fontProperty.SetValue(tmpTextInstance, _cachedChineseFont);
                        // LogSrc.LogInfo($"  Applied TMP font to text instance");
                    }
                    catch (Exception setEx)
                    {
                        LogSrc.LogWarning($"  Failed to set font: {setEx.Message}");
                    }
                }
            }
            else
            {
                LogSrc.LogWarning($"  font property not found or not writable on {instanceType.Name}");
            }
        }
        catch (Exception ex)
        {
            LogSrc.LogDebug($"  Font application error: {ex.Message}");
        }
    }

    private static bool ShouldCollectMissing(string s)
    {
        // 简单过滤：纯数字、很短、全空白就不收集
        if (s.All(char.IsWhiteSpace)) return false;
        if (s.Length <= 1) return false;

        bool allDigitOrPunct = true;
        foreach (char c in s)
        {
            if (char.IsLetter(c) || c > 0x7F) { allDigitOrPunct = false; break; }
            if (char.IsDigit(c)) continue;
            if (char.IsPunctuation(c) || char.IsSymbol(c) || char.IsWhiteSpace(c)) continue;
            allDigitOrPunct = false; break;
        }
        return !allDigitOrPunct;
    }
}

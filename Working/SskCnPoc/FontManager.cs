using System;
using System.IO;
using System.Linq;
using BepInEx;
using UnityEngine;

namespace SskCnPoc;

/// <summary>
/// 字体管理器：负责加载和应用中文字体
/// </summary>
internal static class FontManager
{
    public static string? ChineseFontBundlePath { get; private set; }
    
    private static object? _cachedChineseFont;
    private static AssetBundle? _loadedAssetBundle;
    private static bool _fontLoadingAttempted;

    /// <summary>
    /// 查找中文字体 AssetBundle
    /// </summary>
    public static void Initialize()
    {
        try
        {
            Plugin.LogSrc.LogInfo("Looking for Chinese TMP font AssetBundle...");
            
            string[] searchPaths = new[]
            {
                @"E:\ssk_han_reborn\font_output\sourcehan",
                @"E:\ssk_han_reborn\font\sourcehan",
                Path.Combine(Paths.PluginPath, "Fonts", "sourcehan"),
                Path.Combine(Paths.PluginPath, "sourcehan"),
                Path.Combine(Paths.GameRootPath, "Sunless Skies_Data", "StreamingAssets", "Fonts", "sourcehan"),
            };

            foreach (var path in searchPaths)
            {
                if (File.Exists(path))
                {
                    ChineseFontBundlePath = path;
                    var fileSize = new FileInfo(path).Length / 1024.0 / 1024.0;
                    Plugin.LogSrc.LogInfo($"✓ Found TMP font AssetBundle: {path} ({fileSize:F1} MB)");
                    break;
                }
            }

            if (string.IsNullOrEmpty(ChineseFontBundlePath))
            {
                Plugin.LogSrc.LogWarning("Chinese TMP font AssetBundle not found!");
                Plugin.LogSrc.LogWarning("Please place 'sourcehan' AssetBundle file in: E:\\ssk_han_reborn\\font\\ or BepInEx\\plugins\\Fonts\\");
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSrc.LogWarning($"FontManager.Initialize error: {ex.Message}");
        }
    }

    /// <summary>
    /// 确保文本实例使用中文字体
    /// </summary>
    public static void EnsureChineseFontSupport(object tmpTextInstance)
    {
        try
        {
            if (tmpTextInstance == null) return;
            
            if (!_fontLoadingAttempted)
            {
                _fontLoadingAttempted = true;
                Plugin.LogSrc.LogInfo(">>> Font loading attempt started");
                
                LoadFontFromAssetBundle();

                if (_cachedChineseFont != null)
                {
                    Plugin.LogSrc.LogInfo(">>> TMP Font resource ready for use");
                }
                else
                {
                    Plugin.LogSrc.LogWarning(">>> Failed to load TMP font - text will show as boxes");
                }
            }

            if (_cachedChineseFont != null)
            {
                ApplyFontToInstance(tmpTextInstance);
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSrc.LogError($"EnsureChineseFontSupport error: {ex}");
        }
    }

    private static void LoadFontFromAssetBundle()
    {
        try
        {
            if (string.IsNullOrEmpty(ChineseFontBundlePath))
            {
                Plugin.LogSrc.LogWarning("  No AssetBundle path configured");
                return;
            }

            if (!File.Exists(ChineseFontBundlePath))
            {
                Plugin.LogSrc.LogWarning($"  AssetBundle file not found: {ChineseFontBundlePath}");
                return;
            }

            Plugin.LogSrc.LogInfo($"  Loading AssetBundle from: {ChineseFontBundlePath}");

            var bundle = AssetBundle.LoadFromFile(ChineseFontBundlePath);
            
            if (bundle == null)
            {
                Plugin.LogSrc.LogWarning("  AssetBundle.LoadFromFile returned null");
                Plugin.LogSrc.LogInfo("  Trying LoadFromMemory as fallback...");
                
                var bundleBytes = File.ReadAllBytes(ChineseFontBundlePath);
                Plugin.LogSrc.LogInfo($"  Read {bundleBytes.Length / 1024.0 / 1024.0:F2} MB from file");
                bundle = AssetBundle.LoadFromMemory(bundleBytes);
                
                if (bundle != null)
                {
                    Plugin.LogSrc.LogInfo("  ✓ LoadFromMemory succeeded");
                }
                else
                {
                    Plugin.LogSrc.LogWarning("  LoadFromMemory also returned null!");
                    return;
                }
            }

            _loadedAssetBundle = bundle;
            Plugin.LogSrc.LogInfo("  ✓ AssetBundle loaded successfully");

            var assetNames = bundle.GetAllAssetNames();
            Plugin.LogSrc.LogInfo($"  AssetBundle contains {assetNames.Length} assets:");
            foreach (var name in assetNames)
            {
                Plugin.LogSrc.LogInfo($"    - {name}");
            }

            if (assetNames.Length == 0)
            {
                Plugin.LogSrc.LogWarning("  AssetBundle contains 0 assets!");
                return;
            }

            // 获取 TMP_FontAsset 类型
            Type? tmproFontAssetType = null;
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
                Plugin.LogSrc.LogWarning("  TMP_FontAsset type not found");
                return;
            }

            Plugin.LogSrc.LogInfo($"  Found TMP_FontAsset type: {tmproFontAssetType.FullName}");

            // 转换为 Il2Cpp 类型
            Il2CppSystem.Type? il2cppFontType = null;
            try
            {
                il2cppFontType = Il2CppInterop.Runtime.Il2CppType.From(tmproFontAssetType, false);
                Plugin.LogSrc.LogInfo($"  Converted to Il2Cpp type: {il2cppFontType?.FullName ?? "null"}");
            }
            catch (Exception ex)
            {
                Plugin.LogSrc.LogWarning($"  Failed to convert type: {ex.Message}");
            }

            // 尝试加载资源
            string[] tryNames = { 
                "SourceHanSansSC-Normal SDF",
                "assets/fonts/sourcehansanssc-normal sdf.asset",
                "Assets/Fonts/SourceHanSansSC-Normal SDF.asset",
                "sourcehan"
            };

            var allTryNames = tryNames.Concat(assetNames).Distinct().ToArray();

            foreach (var assetName in allTryNames)
            {
                try
                {
                    Plugin.LogSrc.LogInfo($"    Trying to load: {assetName}");
                    
                    UnityEngine.Object? asset = null;
                    if (il2cppFontType != null)
                    {
                        asset = bundle.LoadAsset(assetName, il2cppFontType);
                    }
                    else
                    {
                        asset = bundle.LoadAsset_Internal(assetName, Il2CppInterop.Runtime.Il2CppType.Of<UnityEngine.Object>());
                    }
                    
                    if (asset != null)
                    {
                        Plugin.LogSrc.LogInfo($"  ✓ Loaded asset: {assetName}");
                        Plugin.LogSrc.LogInfo($"    Raw asset type: {asset.GetType().FullName}");
                        
                        var convertedFont = TryConvertToTMPFont(asset, tmproFontAssetType);
                        _cachedChineseFont = convertedFont ?? asset;
                        
                        if (convertedFont != null)
                        {
                            Plugin.LogSrc.LogInfo($"  ✓ Converted to TMP_FontAsset successfully");
                        }
                        else
                        {
                            Plugin.LogSrc.LogInfo($"  Stored as UnityEngine.Object, will convert on apply");
                        }
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Plugin.LogSrc.LogDebug($"    Failed: {ex.Message}");
                }
            }

            // 备选：加载所有资源
            Plugin.LogSrc.LogInfo("  Trying LoadAllAssets...");
            try
            {
                var allAssets = bundle.LoadAllAssets();
                Plugin.LogSrc.LogInfo($"  LoadAllAssets returned {allAssets.Length} assets");
                
                foreach (var asset in allAssets)
                {
                    if (asset != null)
                    {
                        Plugin.LogSrc.LogInfo($"    Asset: {asset.name} (Type: {asset.GetType().Name})");
                        
                        if (tmproFontAssetType.IsAssignableFrom(asset.GetType()))
                        {
                            _cachedChineseFont = asset;
                            Plugin.LogSrc.LogInfo($"  ✓ Found TMP_FontAsset: {asset.name}");
                            return;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSrc.LogWarning($"  LoadAllAssets failed: {ex.Message}");
            }

            Plugin.LogSrc.LogWarning("  Failed to load any TMP_FontAsset from AssetBundle");
        }
        catch (Exception ex)
        {
            Plugin.LogSrc.LogError($"  LoadFontFromAssetBundle error: {ex}");
        }
    }

    private static object? TryConvertToTMPFont(UnityEngine.Object asset, Type targetType)
    {
        try
        {
            if (asset == null || targetType == null) return null;
            
            var assetType = asset.GetType();
            if (targetType.IsAssignableFrom(assetType))
            {
                Plugin.LogSrc.LogInfo($"    Asset is already {targetType.Name}");
                return asset;
            }
            
            Plugin.LogSrc.LogInfo($"    Asset Il2Cpp type: {assetType.FullName}");
            
            var il2cppAssetType = asset.GetIl2CppType();
            Plugin.LogSrc.LogInfo($"    Asset GetIl2CppType: {il2cppAssetType?.FullName ?? "null"}");
            
            if (il2cppAssetType != null && il2cppAssetType.FullName.Contains("TMP_FontAsset"))
            {
                Plugin.LogSrc.LogInfo($"    Asset is TMP_FontAsset in Il2Cpp! Attempting cast...");
                
                var castMethod = asset.GetType().GetMethod("Cast");
                if (castMethod != null && castMethod.IsGenericMethod)
                {
                    var genericCast = castMethod.MakeGenericMethod(targetType);
                    var result = genericCast.Invoke(asset, null);
                    if (result != null)
                    {
                        Plugin.LogSrc.LogInfo($"    ✓ Cast succeeded! Result type: {result.GetType().FullName}");
                        return result;
                    }
                }
                
                var tryCastMethod = asset.GetType().GetMethod("TryCast");
                if (tryCastMethod != null && tryCastMethod.IsGenericMethod)
                {
                    var genericTryCast = tryCastMethod.MakeGenericMethod(targetType);
                    var result = genericTryCast.Invoke(asset, null);
                    if (result != null)
                    {
                        Plugin.LogSrc.LogInfo($"    ✓ TryCast succeeded! Result type: {result.GetType().FullName}");
                        return result;
                    }
                }
                
                Plugin.LogSrc.LogWarning($"    Cast methods not found or failed");
            }
            
            Plugin.LogSrc.LogWarning($"    Could not convert asset to {targetType.Name}");
            return null;
        }
        catch (Exception ex)
        {
            Plugin.LogSrc.LogWarning($"    TryConvertToTMPFont error: {ex.Message}");
            return null;
        }
    }

    private static void ApplyFontToInstance(object tmpTextInstance)
    {
        try
        {
            if (_cachedChineseFont == null) return;
            
            var instanceType = tmpTextInstance.GetType();
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
                        fontProperty.SetValue(tmpTextInstance, _cachedChineseFont);
                    }
                    catch (Exception setEx)
                    {
                        Plugin.LogSrc.LogWarning($"  Failed to set font: {setEx.Message}");
                    }
                }
            }
            else
            {
                Plugin.LogSrc.LogWarning($"  font property not found or not writable on {instanceType.Name}");
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSrc.LogDebug($"  Font application error: {ex.Message}");
        }
    }
}

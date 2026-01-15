using System;
using System.Collections;
using BepInEx.Unity.IL2CPP.Utils.Collections;
using Il2CppInterop.Runtime.Injection;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using Object = UnityEngine.Object;
using UnityEngine;
using TMPro;
using UnityEngine.UI;

namespace SskCnPoc;

/// <summary>
/// 定期扫描TMP文本组件，处理动态加载的UI
/// 主要用于初始加载时的文本翻译，Harmony Patch 负责实时翻译
/// </summary>
public class TextScanner : MonoBehaviour
{
    private float _fastInterval = 0.05f;  // 快速扫描间隔（50ms，几乎即时）
    private float _slowInterval = 30.0f;  // 慢速扫描间隔（30秒，几乎不扫描，避免卡顿）
    private int _fastScanCount = 30;      // 快速扫描次数（增加到30次，覆盖1.5秒）
    private float _lastScanTime = 0f;
    private bool _isScanning = false;
    private int _scanCount = 0;
    
    // 缓存的组件引用，避免每次都调用 FindObjectsOfType
    private Il2CppArrayBase<TMP_Text>? _cachedTmpTexts;
    private Il2CppArrayBase<Text>? _cachedUguiTexts;
    private bool _useCachedComponents = false;

    static TextScanner()
    {
        // 注册到 Il2Cpp 类型系统
        ClassInjector.RegisterTypeInIl2Cpp<TextScanner>();
    }

    public TextScanner(IntPtr ptr) : base(ptr) { }

    public void StartScanning()
    {
        _isScanning = true;
        _scanCount = 0;
        _lastScanTime = 0f;
        RefreshComponentCache();
        Plugin.LogSrc.LogInfo("TextScanner: Scanning enabled (fast mode)");
    }

    public void StopScanning()
    {
        _isScanning = false;
        ClearCache();
    }
    
    /// <summary>
    /// 重置为快速扫描模式（在场景变化或菜单打开时调用）
    /// </summary>
    public void ResetToFastMode()
    {
        _scanCount = 0;
        _lastScanTime = 0f;
        RefreshComponentCache();
        // 立即执行一次扫描
        DoScanNow();
    }
    
    /// <summary>
    /// 刷新组件缓存（只在场景变化时调用一次）
    /// </summary>
    private void RefreshComponentCache()
    {
        try
        {
            _cachedTmpTexts = Resources.FindObjectsOfTypeAll<TMP_Text>();
            _cachedUguiTexts = Resources.FindObjectsOfTypeAll<Text>();
            _useCachedComponents = true;
            Plugin.LogSrc.LogInfo($"TextScanner: Cached {_cachedTmpTexts?.Length ?? 0} TMP + {_cachedUguiTexts?.Length ?? 0} UGUI components");
        }
        catch (Exception ex)
        {
            Plugin.LogSrc.LogDebug($"TextScanner cache refresh error: {ex.Message}");
            _useCachedComponents = false;
        }
    }
    
    private void ClearCache()
    {
        _cachedTmpTexts = null;
        _cachedUguiTexts = null;
        _useCachedComponents = false;
    }
    
    private void DoScanNow()
    {
        try
        {
            if (_useCachedComponents && _cachedTmpTexts != null && _cachedUguiTexts != null)
            {
                // 使用缓存的组件进行扫描
                Plugin.ScanCachedComponents(_cachedTmpTexts, _cachedUguiTexts);
            }
            else
            {
                // 回退到完整扫描
                Plugin.ScanAndTranslateAllTmpText();
            }
        }
        catch (Exception ex)
        {
            Plugin.LogSrc.LogDebug($"TextScanner immediate scan error: {ex.Message}");
        }
    }

    private void Update()
    {
        if (!_isScanning) return;
        
        // 只在快速模式下扫描，之后完全依靠 Harmony patch
        if (_scanCount >= _fastScanCount)
        {
            // 快速扫描结束，停止扫描并清理缓存
            if (_useCachedComponents)
            {
                ClearCache();
                Plugin.LogSrc.LogInfo("TextScanner: Fast scan complete, switching to Harmony-only mode");
            }
            return;
        }
        
        if (Time.time - _lastScanTime >= _fastInterval)
        {
            _lastScanTime = Time.time;
            _scanCount++;
            
            try
            {
                if (_useCachedComponents && _cachedTmpTexts != null && _cachedUguiTexts != null)
                {
                    Plugin.ScanCachedComponents(_cachedTmpTexts, _cachedUguiTexts);
                }
                
                // 只在前几次做调试输出
                if (_scanCount <= 3)
                {
                    Plugin.DebugDumpAllTextComponents();
                }
            }
            catch (Exception ex)
            {
                Plugin.LogSrc.LogDebug($"TextScanner error: {ex.Message}");
            }
        }
    }
}

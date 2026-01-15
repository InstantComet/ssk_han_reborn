using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;
using UnityEngine.SceneManagement;

namespace SskCnPoc;

[BepInPlugin("ssk.cn.poc", "SSK CN PoC", "0.1.0")]
public class Plugin : BasePlugin
{
    internal static ManualLogSource LogSrc = null!;
    private Harmony _harmony = null!;
    private static bool _patched;
    private static GameObject? _scannerObject;
    private static TextScanner? _scanner;

    public override void Load()
    {
        LogSrc = Log;
        LogSrc.LogInfo("SSK CN PoC loading...");

        TranslationManager.LoadTranslations();
        FontManager.Initialize();

        _harmony = new Harmony("ssk.cn.poc.harmony");

        if (TryPatch())
        {
            SceneManager.sceneLoaded += (UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
        }
        else
        {
            LogSrc.LogInfo("TMP not ready, will retry on scene load...");
            SceneManager.sceneLoaded += (UnityAction<Scene, LoadSceneMode>)OnSceneLoadedRetryPatch;
        }

        LogSrc.LogInfo("SSK CN PoC loaded");
    }

    private bool TryPatch()
    {
        if (_patched) return true;
        _patched = HarmonyPatches.ApplyAll(_harmony);
        return _patched;
    }

    private void OnSceneLoadedRetryPatch(Scene scene, LoadSceneMode mode)
    {
        if (_patched) return;
        
        LogSrc.LogInfo($"Scene '{scene.name}' loaded, retrying patch...");
        if (TryPatch())
        {
            SceneManager.sceneLoaded -= (UnityAction<Scene, LoadSceneMode>)OnSceneLoadedRetryPatch;
            SceneManager.sceneLoaded += (UnityAction<Scene, LoadSceneMode>)OnSceneLoaded;
            OnSceneLoaded(scene, mode);
        }
    }

    private static void OnSceneLoaded(Scene scene, LoadSceneMode mode)
    {
        LogSrc.LogInfo($"Scene '{scene.name}' loaded, scanning...");
        
        ComponentScanner.ClearCache();
        ComponentScanner.ScanAll();
        
        EnsureScanner();
        _scanner?.ResetToFastMode();
    }

    private static void EnsureScanner()
    {
        if (_scannerObject != null) return;
        
        _scannerObject = new GameObject("SskCnPoc_Scanner");
        Object.DontDestroyOnLoad(_scannerObject);
        _scanner = _scannerObject.AddComponent<TextScanner>();
        _scanner.StartScanning();
        LogSrc.LogInfo("Started periodic scanner");
    }

    // 供 TextScanner 调用
    internal static void ScanAll() => ComponentScanner.ScanAll();
    
    internal static void ScanCached(
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<TMPro.TMP_Text> tmp,
        Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppArrayBase<UnityEngine.UI.Text> ugui) 
        => ComponentScanner.ScanCached(tmp, ugui);
}

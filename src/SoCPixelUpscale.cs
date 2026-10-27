// SoC Pixel Upscale v1.0
// Smooths Songs of Conquest pixel-art sprites with Scale2x (2x) or Scale2x twice (4x).
// Two ways to fill the cache:
//   * Realtime  - every atlas is converted in the background the first time the game shows it.
//   * Prebake   - in the main menu the plugin walks the game's asset bundles and converts everything
//                 in one go (progress overlay), so the game starts fully smoothed.
// Original game files are never modified. The cache lives in BepInEx/cache/SoCPixelUpscale/<game version>/x<N>.
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace SoCPixelUpscale
{
    public enum ScaleMode { X2 = 2, X4 = 4 }
    public enum CacheMode { Realtime, Prebake, CacheOnly }
    public enum MenuLanguage { Auto, English, Ukrainian, Russian }

    // Read by BepInEx.ConfigurationManager (F1) via reflection, if the player has it installed.
    internal sealed class ConfigurationManagerAttributes
    {
        public int? Order;
        public bool? Browsable;
        public bool? IsAdvanced;
    }
    enum Kind { Unit, MapObject }

    [BepInPlugin("ivan.soc.pixelupscale", "SoC Pixel Upscale", Plugin.Version)]
    public class Plugin : BaseUnityPlugin
    {
        public const string Version = "1.1.0"; // single source of truth; scripts/package.sh reads it
        internal static ManualLogSource Log;
        internal static Plugin Instance;
        void Awake()
        {
            Log = Logger; Instance = this;
            var go = new GameObject("SoCPixelUpscaleRunner");
            go.hideFlags = HideFlags.HideAndDontSave;
            DontDestroyOnLoad(go);
            go.AddComponent<Runner>();
            TryPatchUi();
        }

        static bool _uiPatched;
        internal static void TryPatchUi()
        {
            if (_uiPatched) return;
            try
            {
                Type ui = null, imgLayer = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    if (asm.GetName().Name.StartsWith("Lavapotion.SongsOfConquest.UtilitiesLayer"))
                    {
                        ui = asm.GetType("SongsOfConquest.Client.UI.UISprite");
                        imgLayer = asm.GetType("SongsOfConquest.Client.View.Sprites.SpriteImageLayer");
                    }
                var setter = ui != null ? AccessTools.PropertySetter(ui, "Sprite") : null;
                var layerSet = imgLayer != null ? AccessTools.Method(imgLayer, "SetSprite") : null;
                if (setter == null || layerSet == null) return;
                var h = new Harmony("ivan.soc.pixelupscale");
                var pre = new HarmonyMethod(typeof(UiPatch), "Prefix");
                h.Patch(setter, prefix: pre);
                h.Patch(layerSet, prefix: pre);
                _uiPatched = true;
                Log.LogInfo("UI patched: interface keeps original sprites");
            }
            catch (Exception ex) { Log.LogError("UI patch failed: " + ex); }
        }
    }

    static class UiPatch
    {
        // UI sizes sprites by their pixel rect, so the interface always gets the original sprite.
        static void Prefix(ref Sprite __0)
        {
            var r = Runner.Instance;
            if (r != null && __0 != null) __0 = r.ToOriginal(__0);
        }
    }

    // ------------------------------------------------------------------------------------------
    // Scale2x on packed RGBA (uint per pixel). Orientation-agnostic.
    static class Scale2x
    {
        public static uint[] Run(uint[] src, int w, int h)
        {
            var dst = new uint[w * h * 4];
            int dw = w * 2;
            for (int y = 0; y < h; y++)
            {
                int ym = y > 0 ? y - 1 : 0, yp = y < h - 1 ? y + 1 : h - 1;
                for (int x = 0; x < w; x++)
                {
                    int xm = x > 0 ? x - 1 : 0, xp = x < w - 1 ? x + 1 : w - 1;
                    uint E = src[y * w + x];
                    uint B = src[ym * w + x], D = src[y * w + xm], F = src[y * w + xp], H = src[yp * w + x];
                    uint e0 = E, e1 = E, e2 = E, e3 = E;
                    if (B != H && D != F)
                    {
                        if (D == B) e0 = D;
                        if (B == F) e1 = F;
                        if (D == H) e2 = D;
                        if (H == F) e3 = F;
                    }
                    int o = (y * 2) * dw + x * 2;
                    dst[o] = e0; dst[o + 1] = e1; dst[o + dw] = e2; dst[o + dw + 1] = e3;
                }
            }
            return dst;
        }

        public static uint[] Upscale(uint[] src, int w, int h, int factor, out int ow, out int oh)
        {
            ow = w; oh = h;
            for (int f = 1; f < factor; f *= 2) { src = Run(src, ow, oh); ow *= 2; oh *= 2; }
            return src;
        }

        public static uint[] FromBytes(byte[] b) { var u = new uint[b.Length / 4]; Buffer.BlockCopy(b, 0, u, 0, u.Length * 4); return u; }
        public static byte[] ToBytes(uint[] u) { var b = new byte[u.Length * 4]; Buffer.BlockCopy(u, 0, b, 0, b.Length); return b; }
    }

    // ------------------------------------------------------------------------------------------
    public class Runner : MonoBehaviour
    {
        static ManualLogSource Log { get { return Plugin.Log; } }
        internal static Runner Instance;

        // config
        ConfigEntry<bool> _enabled, _smoothUnits, _smoothMapObjects, _smoothDecorations, _smoothEffects, _gamepadToggle, _clearCache, _prebakeNow;
        ConfigEntry<ScaleMode> _scale;
        ConfigEntry<CacheMode> _mode;
        ConfigEntry<KeyCode> _toggleKey, _menuKey;
        ConfigEntry<float> _scanInterval, _menuScale;
        ConfigEntry<MenuLanguage> _lang;
        string _cacheRoot;

        int Factor { get { return (int)_scale.Value; } }
        string _cacheDir, _legacyDir;

        // state
        readonly Dictionary<string, Texture2D> _bigTex = new Dictionary<string, Texture2D>(StringComparer.Ordinal);
        readonly HashSet<string> _failedTex = new HashSet<string>(StringComparer.Ordinal);
        readonly HashSet<string> _pending = new HashSet<string>(StringComparer.Ordinal);
        readonly Dictionary<int, Sprite> _origToBig = new Dictionary<int, Sprite>();
        readonly Dictionary<int, Sprite> _bigToOrig = new Dictionary<int, Sprite>();
        readonly HashSet<int> _skip = new HashSet<int>();
        readonly Queue<Action> _mainThread = new Queue<Action>();
        bool _showUpscaled = true, _baking;
        string _bakeStatus;
        float _nextScan, _nextBeat;
        int _swapsTotal, _converted, _generation;
        bool _pngFlip; // set by self-test if EncodeArrayToPNG/LoadImage round-trip flips rows

        void Awake()
        {
            Instance = this;
            var cfg = Plugin.Instance.Config;
            int order = 100;
            Func<string, ConfigDescription> D = text => new ConfigDescription(text, null, new ConfigurationManagerAttributes { Order = order-- });
            Func<string, ConfigDescription> Hidden = text => new ConfigDescription(text, null, new ConfigurationManagerAttributes { Browsable = false });
            _enabled = cfg.Bind("1. General", "Enabled", true, D("Master switch. Off = the game looks exactly like vanilla."));
            _scale = cfg.Bind("1. General", "Scale", ScaleMode.X4, D("X2 = one Scale2x pass (lighter). X4 = two passes (smoother, recommended). Each scale has its own cache."));
            _mode = cfg.Bind("1. General", "CacheMode", CacheMode.Realtime, D(
                "Realtime = convert each atlas in the background the first time it appears.\n" +
                "Prebake = in the main menu convert everything at once with a progress bar (once per game version).\n" +
                "CacheOnly = never convert, only use what is already cached."));
            _prebakeNow = cfg.Bind("1. General", "PrebakeOnNextStart", false, Hidden("Run the prebake once more on next start (resets itself). The in-game window has a button for this."));
            _clearCache = cfg.Bind("1. General", "ClearCacheOnNextStart", false, Hidden("Delete the whole cache on next start (resets itself). The in-game window has a button for this."));
            _smoothUnits = cfg.Bind("2. What to smooth", "UnitsAndHeroes", true, D("Troops, heroes and their effect layers."));
            _smoothMapObjects = cfg.Bind("2. What to smooth", "MapObjects", true, D("Buildings, settlements, points of interest, mines, pickups, battle obstacles, pets."));
            _smoothDecorations = cfg.Bind("2. What to smooth", "Decorations", false, D("Ambient props: torches, ruins, lava pools, fish, festive decor."));
            _smoothEffects = cfg.Bind("2. What to smooth", "Effects", false, D("Spell and particle sprites."));
            _menuKey = cfg.Bind("3. Controls", "MenuKey", KeyCode.F9, D("Opens the in-game settings window. Gamepad: hold Select/View and click both sticks."));
            _toggleKey = cfg.Bind("3. Controls", "ToggleKey", KeyCode.Home, D("Toggle original / smoothed for comparison. End also works."));
            _gamepadToggle = cfg.Bind("3. Controls", "GamepadToggleL3R3", true, D("Toggle original / smoothed with both stick clicks."));
            _menuScale = cfg.Bind("3. Controls", "MenuScale", 1f, new ConfigDescription("Size of the in-game window.", new AcceptableValueRange<float>(0.6f, 2f), new ConfigurationManagerAttributes { Order = order-- }));
            _lang = cfg.Bind("3. Controls", "MenuLanguage", MenuLanguage.Auto, D("Language of the in-game window. Auto follows the system language."));
            _scanInterval = cfg.Bind("4. Advanced", "ScanIntervalSeconds", 1.5f, new ConfigDescription("How often to look for newly loaded sprites.", null, new ConfigurationManagerAttributes { IsAdvanced = true }));

            _cacheRoot = Path.Combine(Paths.CachePath, "SoCPixelUpscale");
            _legacyDir = Path.Combine(Path.GetDirectoryName(Plugin.Instance.Info.Location), "cache"); // pre-1.0 PNG cache (4x)
            if (_clearCache.Value) { DeleteCacheFiles(); _clearCache.Value = false; }
            SetCacheDir();
            Log.LogInfo("Cache: " + _cacheDir + " (" + Directory.GetFiles(_cacheDir, "*.png").Length + " atlases), mode " + _mode.Value + ", scale " + _scale.Value);

            _scale.SettingChanged += (o, e) => ResetAll("scale changed");
            _smoothUnits.SettingChanged += (o, e) => ResetAll("categories changed");
            _smoothMapObjects.SettingChanged += (o, e) => ResetAll("categories changed");
            _smoothDecorations.SettingChanged += (o, e) => ResetAll("categories changed");
            _smoothEffects.SettingChanged += (o, e) => ResetAll("categories changed");
            _enabled.SettingChanged += (o, e) => { if (_enabled.Value) _nextScan = 0f; else RestoreOriginals(); };

            PngSelfTest();
            ResolveGameTypes();
            SceneManager.sceneLoaded += OnSceneLoaded;
        }

        void SetCacheDir()
        {
            _cacheDir = Path.Combine(Path.Combine(_cacheRoot, Sanitize(Application.version)), "x" + Factor);
            Directory.CreateDirectory(_cacheDir);
        }

        void DeleteCacheFiles()
        {
            try { if (Directory.Exists(_cacheRoot)) Directory.Delete(_cacheRoot, true); } catch (Exception ex) { Log.LogWarning("Clear cache: " + ex.Message); }
            try { if (Directory.Exists(_legacyDir)) Directory.Delete(_legacyDir, true); } catch (Exception ex) { Log.LogWarning("Clear legacy cache: " + ex.Message); }
            Log.LogInfo("Cache cleared");
        }

        void RestoreOriginals()
        {
            bool prev = _showUpscaled; _showUpscaled = false;
            try { Scan(); } finally { _showUpscaled = prev; }
        }

        // Puts every original sprite back, drops all smoothed sprites/textures and starts over (after scale/category changes).
        void ResetAll(string why)
        {
            RestoreOriginals();
            foreach (var sp in _origToBig.Values) if (sp != null) Destroy(sp);
            foreach (var t in _bigTex.Values) if (t != null) Destroy(t);
            _origToBig.Clear(); _bigToOrig.Clear(); _skip.Clear(); _bigTex.Clear(); _failedTex.Clear();
            _generation++;
            SetCacheDir();
            _nextScan = 0f; _cacheStatsAt = 0f;
            Log.LogInfo("Reset (" + why + "), cache " + _cacheDir);
        }

        static string Sanitize(string s) { if (string.IsNullOrEmpty(s)) return "unknown"; foreach (var c in Path.GetInvalidFileNameChars()) s = s.Replace(c, '_'); return s; }

        void OnSceneLoaded(Scene sc, LoadSceneMode m)
        {
            _nextScan = 0f; Plugin.TryPatchUi();
            Log.LogInfo("Scene loaded: " + sc.name);
            if (_enabled.Value && !_baking && sc.name.Contains("MainMenu") && (_mode.Value == CacheMode.Prebake || _prebakeNow.Value))
            {
                var marker = Path.Combine(_cacheDir, "prebake.done");
                if (_prebakeNow.Value || !File.Exists(marker)) StartCoroutine(Prebake(marker));
            }
        }

        void PngSelfTest()
        {
            try
            {
                var px = new byte[] { 255, 0, 0, 255, 0, 255, 0, 255, 0, 0, 255, 255, 255, 255, 255, 255 }; // row0: red, green; row1: blue, white
                var png = ImageConversion.EncodeArrayToPNG(px, GraphicsFormat.R8G8B8A8_UNorm, 2, 2);
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                t.LoadImage(png);
                var back = t.GetPixels32();
                _pngFlip = !(back[0].r == 255 && back[0].g == 0 && back[2].b == 255);
                Destroy(t);
                Log.LogInfo("PNG round-trip self-test: " + (_pngFlip ? "flipped (compensating)" : "ok"));
            }
            catch (Exception ex) { Log.LogWarning("PNG self-test failed: " + ex.Message); }
        }

        // ---------------- types ----------------
        Type _animType; FieldInfo _fLayers, _fCollider, _fSprites;
        static readonly string[] EntityAnimatorNames = { "SimpleSpriteAnimator", "SimpleSyncedSpriteAnimator", "SpriteAnimator", "RandomSpriteSetter" };
        static readonly string[] SpriteFieldNames = { "_sprites", "_collisionSprite", "_sprite" };
        readonly List<KeyValuePair<Type, FieldInfo[]>> _entityTypes = new List<KeyValuePair<Type, FieldInfo[]>>();
        readonly Dictionary<Type, FieldInfo[]> _nestedSpriteFields = new Dictionary<Type, FieldInfo[]>();

        void ResolveGameTypes()
        {
            const BindingFlags BF = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var an = asm.GetName().Name;
                if (!an.StartsWith("Lavapotion")) continue;
                if (an.StartsWith("Lavapotion.SongsOfConquest.UtilitiesLayer") && _animType == null)
                    _animType = asm.GetType("SongsOfConquest.Client.View.Animation.SpriteAnimation");
                Type[] types;
                try { types = asm.GetTypes(); } catch (ReflectionTypeLoadException e) { types = e.Types; }
                foreach (var t in types)
                {
                    if (t == null || t.IsAbstract || t.IsGenericTypeDefinition || !typeof(MonoBehaviour).IsAssignableFrom(t)) continue;
                    if (Array.IndexOf(EntityAnimatorNames, t.Name) < 0 || t.Name.Contains("UI")) continue;
                    var fields = new List<FieldInfo>();
                    foreach (var n in SpriteFieldNames) { var f = FindField(t, n); if (f != null) fields.Add(f); }
                    if (fields.Count > 0) _entityTypes.Add(new KeyValuePair<Type, FieldInfo[]>(t, fields.ToArray()));
                }
            }
            if (_animType != null) { _fLayers = _animType.GetField("_layerDefinitions", BF); _fCollider = _animType.GetField("_colliderSprite", BF); }
            Log.LogInfo("Game types: unit animations " + (_fLayers != null ? "ok" : "MISSING") + ", map object animators " + _entityTypes.Count);
        }

        static FieldInfo FindField(Type t, string name)
        {
            for (var c = t; c != null && c != typeof(MonoBehaviour); c = c.BaseType)
            {
                var f = c.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly);
                if (f != null) return f;
            }
            return null;
        }

        // ---------------- classification ----------------
        static readonly Regex NeverRx = new Regex("Portrait|Sketch|Icons|UI|Button|Frame|Mask|Arrow|Alert|Foldout|CheckMark|PinDot|debug|editor|Placeholder|Deprecated|Highlight|Lighting|Cursor|Minimap", RegexOptions.IgnoreCase);
        static readonly Regex DecorRx = new Regex("Decoration|Standalone|Ruins|Blockers|Fish|Midsummer|Winter|Stamps|Wildlife|Canopy|Water|Shadow", RegexOptions.IgnoreCase);
        static readonly Regex EffectRx = new Regex("PixelEffects|HDEffect|Particle|AdventureEffects|Decal|Fx|Ramp|Noise|Trail", RegexOptions.IgnoreCase);

        bool Allowed(string texName, Kind kind)
        {
            if (string.IsNullOrEmpty(texName) || NeverRx.IsMatch(texName)) return false;
            if (EffectRx.IsMatch(texName) && !texName.Contains("VFX") && !texName.Contains("CrowEffect")) return _smoothEffects.Value;
            if (DecorRx.IsMatch(texName)) return _smoothDecorations.Value;
            if (kind == Kind.Unit) return _smoothUnits.Value;
            return _smoothMapObjects.Value;
        }

        static bool Convertible(Texture2D t)
        {
            if (t == null || t.filterMode != FilterMode.Point) return false;
            var f = t.format;
            return f == TextureFormat.RGBA32 || f == TextureFormat.ARGB32 || f == TextureFormat.RGB24 || f == TextureFormat.BGRA32;
        }

        // ---------------- update ----------------
        void Update()
        {
            while (true)
            {
                Action a = null;
                lock (_mainThread) { if (_mainThread.Count > 0) a = _mainThread.Dequeue(); }
                if (a == null) break;
                try { a(); } catch (Exception ex) { Log.LogError("main-thread job: " + ex); }
            }
            if (MenuPressed()) { _menuOpen = !_menuOpen; _menuSel = 0; _cacheStatsAt = 0f; }
            else if (_menuOpen) MenuGamepad();
            if (!_enabled.Value) return;
            if (Time.unscaledTime >= _nextBeat)
            {
                _nextBeat = Time.unscaledTime + 30f;
                Log.LogInfo("Heartbeat: scene=" + SceneManager.GetActiveScene().name + " atlases=" + _bigTex.Count + " sprites=" + _origToBig.Count + " converted=" + _converted + " pending=" + _pending.Count + " swaps=" + _swapsTotal + " mode=" + (_showUpscaled ? "SMOOTH" : "ORIGINAL"));
            }
            if (KeyPressed())
            {
                _showUpscaled = !_showUpscaled; _nextScan = 0f;
                Log.LogInfo("Toggle -> " + (_showUpscaled ? "SMOOTH" : "ORIGINAL"));
            }
            if (Time.unscaledTime >= _nextScan) { _nextScan = Time.unscaledTime + _scanInterval.Value; Scan(); }
        }

        void RunOnMain(Action a) { lock (_mainThread) _mainThread.Enqueue(a); }

        bool KeyPressed()
        {
            if (_gamepadToggle.Value)
            {
                try
                {
                    var gp = UnityEngine.InputSystem.Gamepad.current;
                    if (gp != null && !gp.selectButton.isPressed && BothSticks(gp)) return true;
                }
                catch { }
            }
            return KeyDown(_toggleKey.Value) || (_toggleKey.Value != KeyCode.End && KeyDown(KeyCode.End));
        }

        static bool BothSticks(UnityEngine.InputSystem.Gamepad gp)
        {
            return (gp.leftStickButton.isPressed && gp.rightStickButton.wasPressedThisFrame) || (gp.rightStickButton.isPressed && gp.leftStickButton.wasPressedThisFrame);
        }

        bool MenuPressed()
        {
            try { var gp = UnityEngine.InputSystem.Gamepad.current; if (gp != null && gp.selectButton.isPressed && BothSticks(gp)) return true; } catch { }
            return KeyDown(_menuKey.Value);
        }

        static bool KeyDown(KeyCode key)
        {
            try { if (Input.GetKeyDown(key)) return true; } catch { }
            try
            {
                var kb = UnityEngine.InputSystem.Keyboard.current;
                UnityEngine.InputSystem.Key k;
                if (kb != null && Enum.TryParse(key.ToString(), true, out k)) { var c = kb[k]; if (c != null && c.wasPressedThisFrame) return true; }
            }
            catch { }
            return false;
        }

        // ---------------- in-game window ----------------
        bool _menuOpen;
        int _menuSel;
        float _cacheStatsAt;
        int _cacheFiles; long _cacheBytes;
        GUIStyle _sTitle, _sLabel, _sValue, _sSmall, _sRowSel, _sRow, _sBtn;
        Texture2D _bg, _bgSel;

        class Row
        {
            public string Label; public Func<string> Value; public Action Activate, Left, Right;
            public Row(string label, Func<string> value, Action activate, Action left = null, Action right = null)
            { Label = label; Value = value; Activate = activate; Left = left ?? activate; Right = right ?? activate; }
        }

        string Lang()
        {
            var l = _lang.Value;
            if (l == MenuLanguage.Auto)
            {
                var sys = Application.systemLanguage;
                l = sys == SystemLanguage.Ukrainian ? MenuLanguage.Ukrainian : sys == SystemLanguage.Russian ? MenuLanguage.Russian : MenuLanguage.English;
            }
            return l == MenuLanguage.Ukrainian ? "uk" : l == MenuLanguage.Russian ? "ru" : "en";
        }

        static readonly Dictionary<string, string[]> Texts = new Dictionary<string, string[]>
        {   //            en                                  uk                                       ru
            { "title",   new[] { "Pixel Upscale settings",          "Налаштування Pixel Upscale",          "Настройки Pixel Upscale" } },
            { "enabled", new[] { "Mod enabled",                     "Мод увімкнено",                       "Мод включён" } },
            { "show",    new[] { "Show smoothed (compare)",         "Показувати згладжене (порівняння)",   "Показывать сглаженное (сравнение)" } },
            { "scale",   new[] { "Smoothing",                       "Згладжування",                        "Сглаживание" } },
            { "mode",    new[] { "Conversion",                      "Конвертація",                         "Конвертация" } },
            { "units",   new[] { "Units and heroes",                "Юніти та герої",                      "Юниты и герои" } },
            { "objects", new[] { "Buildings and points of interest","Будівлі та точки інтересу",           "Постройки и точки интереса" } },
            { "decor",   new[] { "Decorations",                     "Декорації",                           "Декорации" } },
            { "fx",      new[] { "Effects",                         "Ефекти",                              "Эффекты" } },
            { "bake",    new[] { "Convert everything now",          "Сконвертувати все зараз",             "Сконвертировать всё сейчас" } },
            { "clear",   new[] { "Clear cache",                     "Очистити кеш",                        "Очистить кэш" } },
            { "folder",  new[] { "Open cache folder",               "Відкрити теку кешу",                  "Открыть папку кэша" } },
            { "close",   new[] { "Close",                           "Закрити",                             "Закрыть" } },
            { "on",      new[] { "ON",                              "ТАК",                                 "ДА" } },
            { "off",     new[] { "off",                             "ні",                                  "нет" } },
            { "run",     new[] { "run",                             "запустити",                           "запустить" } },
            { "busy",    new[] { "running...",                      "триває...",                           "идёт..." } },
            { "Realtime",new[] { "on the fly",                      "на льоту",                            "на лету" } },
            { "Prebake", new[] { "all at once in main menu",        "усе одразу в меню",                   "всё сразу в меню" } },
            { "CacheOnly",new[]{ "cache only",                      "лише кеш",                            "только кэш" } },
            { "stats",   new[] { "In memory: {0} sheets, {1} sprites  |  converted now: {2}, in progress: {3}",
                                 "У пам'яті: {0} атласів, {1} спрайтів  |  сконвертовано зараз: {2}, у роботі: {3}",
                                 "В памяти: {0} атласов, {1} спрайтов  |  сконвертировано сейчас: {2}, в работе: {3}" } },
            { "cache",   new[] { "Cache on disk: {0} sheets, {1:0} MB",  "Кеш на диску: {0} атласів, {1:0} МБ", "Кэш на диске: {0} атласов, {1:0} МБ" } },
            { "hint",    new[] { "Mouse/touch, or D-pad + A, B to close. Changes apply immediately.",
                                 "Миша/дотик або хрестовина + A, B — закрити. Зміни застосовуються одразу.",
                                 "Мышь/касание или крестовина + A, B — закрыть. Изменения применяются сразу." } },
        };

        string T(string key)
        {
            string[] v; if (!Texts.TryGetValue(key, out v)) return key;
            var l = Lang(); return l == "uk" ? v[1] : l == "ru" ? v[2] : v[0];
        }

        string OnOff(bool b) { return b ? T("on") : T("off"); }

        List<Row> BuildRows()
        {
            return new List<Row>
            {
                new Row(T("enabled"), () => OnOff(_enabled.Value), () => _enabled.Value = !_enabled.Value),
                new Row(T("show"), () => OnOff(_showUpscaled), () => { _showUpscaled = !_showUpscaled; _nextScan = 0f; }),
                new Row(T("scale"), () => _scale.Value == ScaleMode.X4 ? "4x" : "2x", () => _scale.Value = _scale.Value == ScaleMode.X4 ? ScaleMode.X2 : ScaleMode.X4),
                new Row(T("mode"), () => T(_mode.Value.ToString()), () => _mode.Value = (CacheMode)(((int)_mode.Value + 1) % 3), () => _mode.Value = (CacheMode)(((int)_mode.Value + 2) % 3), () => _mode.Value = (CacheMode)(((int)_mode.Value + 1) % 3)),
                new Row(T("units"), () => OnOff(_smoothUnits.Value), () => _smoothUnits.Value = !_smoothUnits.Value),
                new Row(T("objects"), () => OnOff(_smoothMapObjects.Value), () => _smoothMapObjects.Value = !_smoothMapObjects.Value),
                new Row(T("decor"), () => OnOff(_smoothDecorations.Value), () => _smoothDecorations.Value = !_smoothDecorations.Value),
                new Row(T("fx"), () => OnOff(_smoothEffects.Value), () => _smoothEffects.Value = !_smoothEffects.Value),
                new Row(T("bake"), () => _baking ? T("busy") : T("run"), () => { if (!_baking) StartCoroutine(Prebake(Path.Combine(_cacheDir, "prebake.done"))); }),
                new Row(T("clear"), () => "", () => { ResetAll("cache cleared from menu"); DeleteCacheFiles(); SetCacheDir(); _cacheStatsAt = 0f; }),
                new Row(T("folder"), () => "", () => Application.OpenURL("file:///" + _cacheDir.Replace('\\', '/'))),
                new Row(T("close"), () => "", () => _menuOpen = false),
            };
        }

        void MenuGamepad()
        {
            try
            {
                var gp = UnityEngine.InputSystem.Gamepad.current;
                if (gp == null) return;
                var rows = BuildRows();
                if (gp.dpad.down.wasPressedThisFrame) _menuSel = (_menuSel + 1) % rows.Count;
                if (gp.dpad.up.wasPressedThisFrame) _menuSel = (_menuSel + rows.Count - 1) % rows.Count;
                if (gp.dpad.left.wasPressedThisFrame) rows[_menuSel].Left();
                if (gp.dpad.right.wasPressedThisFrame) rows[_menuSel].Right();
                if (gp.buttonSouth.wasPressedThisFrame) rows[_menuSel].Activate();
                if (gp.buttonEast.wasPressedThisFrame) _menuOpen = false;
            }
            catch { }
        }

        void RefreshCacheStats()
        {
            if (Time.unscaledTime < _cacheStatsAt) return;
            _cacheStatsAt = Time.unscaledTime + 2f;
            _cacheFiles = 0; _cacheBytes = 0;
            try
            {
                foreach (var dir in new[] { _cacheDir, Factor == 4 ? _legacyDir : null })
                    if (dir != null && Directory.Exists(dir))
                        foreach (var f in Directory.GetFiles(dir, "*.png")) { _cacheFiles++; _cacheBytes += new FileInfo(f).Length; }
            }
            catch { }
        }

        static Texture2D Solid(Color c) { var t = new Texture2D(1, 1); t.SetPixel(0, 0, c); t.Apply(); t.hideFlags = HideFlags.HideAndDontSave; return t; }

        void EnsureStyles()
        {
            if (_sTitle != null) return;
            _bg = Solid(new Color(0.08f, 0.07f, 0.06f, 0.96f));
            _bgSel = Solid(new Color(0.55f, 0.40f, 0.15f, 0.55f));
            _sTitle = new GUIStyle(GUI.skin.label) { fontSize = 26, fontStyle = FontStyle.Bold }; _sTitle.normal.textColor = new Color(0.95f, 0.85f, 0.6f);
            _sLabel = new GUIStyle(GUI.skin.label) { fontSize = 20, alignment = TextAnchor.MiddleLeft };
            _sValue = new GUIStyle(GUI.skin.button) { fontSize = 20, fixedWidth = 260 };
            _sBtn = new GUIStyle(GUI.skin.button) { fontSize = 20 };
            _sSmall = new GUIStyle(GUI.skin.label) { fontSize = 16, wordWrap = true }; _sSmall.normal.textColor = new Color(0.8f, 0.8f, 0.8f);
            _sRow = new GUIStyle(); _sRow.padding = new RectOffset(10, 10, 4, 4);
            _sRowSel = new GUIStyle(_sRow); _sRowSel.normal.background = _bgSel;
        }

        void OnGUI()
        {
            if (!_menuOpen && !(_baking && !string.IsNullOrEmpty(_bakeStatus))) return;
            EnsureStyles();
            float s = Screen.height / 1080f * _menuScale.Value;
            GUI.matrix = Matrix4x4.TRS(Vector3.zero, Quaternion.identity, new Vector3(s, s, 1f));
            float vw = Screen.width / s;

            if (_baking && !string.IsNullOrEmpty(_bakeStatus) && !_menuOpen)
            {
                var r = new Rect(vw / 2 - 420, 40, 840, 90);
                GUI.DrawTexture(r, _bg);
                GUI.Label(new Rect(r.x + 20, r.y + 10, r.width - 40, 30), "SoC Pixel Upscale", _sTitle);
                GUI.Label(new Rect(r.x + 20, r.y + 50, r.width - 40, 30), _bakeStatus, _sSmall);
                return;
            }

            RefreshCacheStats();
            var rows = BuildRows();
            if (_menuSel >= rows.Count) _menuSel = 0;
            var win = new Rect(vw / 2 - 380, 80, 760, 120 + rows.Count * 46 + 150);
            GUI.DrawTexture(win, _bg);
            GUILayout.BeginArea(new Rect(win.x + 24, win.y + 16, win.width - 48, win.height - 32));
            GUILayout.Label(T("title") + "  v" + Plugin.Version, _sTitle);
            GUILayout.Space(8);
            for (int i = 0; i < rows.Count; i++)
            {
                var row = rows[i];
                GUILayout.BeginHorizontal(i == _menuSel ? _sRowSel : _sRow, GUILayout.Height(42));
                var val = row.Value();
                if (string.IsNullOrEmpty(val))
                {
                    if (GUILayout.Button(row.Label, _sBtn, GUILayout.Height(34))) { _menuSel = i; row.Activate(); }
                }
                else
                {
                    GUILayout.Label(row.Label, _sLabel, GUILayout.Height(34));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button(val, _sValue, GUILayout.Height(34))) { _menuSel = i; row.Activate(); }
                }
                GUILayout.EndHorizontal();
            }
            GUILayout.Space(10);
            GUILayout.Label(string.Format(T("stats"), _bigTex.Count, _origToBig.Count, _converted, _pending.Count), _sSmall);
            GUILayout.Label(string.Format(T("cache"), _cacheFiles, _cacheBytes / 1048576.0) + "   (" + Application.version + ")", _sSmall);
            if (_baking && !string.IsNullOrEmpty(_bakeStatus)) GUILayout.Label(_bakeStatus, _sSmall);
            GUILayout.Label(T("hint"), _sSmall);
            GUILayout.EndArea();
        }


        // ---------------- sprite mapping ----------------
        internal Sprite ToOriginal(Sprite s) { Sprite o; return _bigToOrig.TryGetValue(s.GetInstanceID(), out o) ? o : s; }

        Sprite Map(Sprite s, Kind kind)
        {
            if (s == null) return null;
            if (!_enabled.Value || !_showUpscaled) return ToOriginal(s);
            int id = s.GetInstanceID();
            Sprite big;
            if (_origToBig.TryGetValue(id, out big)) return big;
            if (_bigToOrig.ContainsKey(id) || _skip.Contains(id)) return s;
            var tex = s.texture;
            if (tex == null || !Allowed(tex.name, kind) || !Convertible(tex)) { _skip.Add(id); return s; }
            var bigTex = GetBigTexture(tex);
            if (bigTex == null) { if (_failedTex.Contains(tex.name)) _skip.Add(id); return s; } // pending -> retry next scan
            return MakeBig(s, bigTex) ?? s;
        }

        Sprite MakeBig(Sprite s, Texture2D big)
        {
            try
            {
                int f = Factor;
                var r = s.rect;
                var rect = new Rect(r.x * f, r.y * f, r.width * f, r.height * f);
                var pivot = new Vector2(s.pivot.x / r.width, s.pivot.y / r.height);
                var ns = Sprite.Create(big, rect, pivot, s.pixelsPerUnit * f, 0, SpriteMeshType.FullRect, s.border * f, false);
                ns.name = s.name;
                try
                {   // keep the original tight mesh so outlines/shadows/selection cover the same area
                    var v = s.vertices; var nv = new Vector2[v.Length];
                    for (int i = 0; i < v.Length; i++)
                    {
                        var px = (v[i] * s.pixelsPerUnit + s.pivot) * f;
                        nv[i] = new Vector2(Mathf.Clamp(px.x, 0f, rect.width), Mathf.Clamp(px.y, 0f, rect.height));
                    }
                    ns.OverrideGeometry(nv, s.triangles);
                }
                catch { }
                _origToBig[s.GetInstanceID()] = ns;
                _bigToOrig[ns.GetInstanceID()] = s;
                return ns;
            }
            catch (Exception ex) { Log.LogWarning("Sprite.Create failed for " + s.name + ": " + ex.Message); _skip.Add(s.GetInstanceID()); return null; }
        }

        string CachePath(Texture2D t) { return Path.Combine(_cacheDir, t.name + "_" + t.width + "x" + t.height + ".png"); }

        Texture2D GetBigTexture(Texture2D orig)
        {
            Texture2D t;
            if (_bigTex.TryGetValue(orig.name, out t)) return t;
            if (_failedTex.Contains(orig.name) || _pending.Contains(orig.name)) return null;
            var p = CachePath(orig);
            if (!File.Exists(p) && Factor == 4) { var lp = Path.Combine(_legacyDir, orig.name + ".png"); if (File.Exists(lp)) p = lp; }
            if (File.Exists(p))
            {
                t = LoadPng(p, orig);
                if (t != null) { _bigTex[orig.name] = t; return t; }
            }
            if (_mode.Value == CacheMode.CacheOnly) { _failedTex.Add(orig.name); return null; }
            StartConversion(orig, CachePath(orig), null);
            return null;
        }

        Texture2D LoadPng(string path, Texture2D orig)
        {
            try
            {
                var t = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!ImageConversion.LoadImage(t, File.ReadAllBytes(path), true)) throw new Exception("LoadImage=false");
                if (t.width != orig.width * Factor || t.height != orig.height * Factor) { Destroy(t); throw new Exception("size mismatch, will reconvert"); }
                t.filterMode = FilterMode.Point; t.wrapMode = TextureWrapMode.Clamp; t.name = orig.name + "_x" + Factor;
                DontDestroyOnLoad(t);
                return t;
            }
            catch (Exception ex) { Log.LogWarning("Cache load failed for " + orig.name + ": " + ex.Message); try { if (path.StartsWith(_cacheDir)) File.Delete(path); } catch { } return null; }
        }

        // ---------------- conversion: GPU readback -> worker thread Scale2x -> PNG on disk -> texture ----------------
        void StartConversion(Texture2D orig, string cachePath, Action<bool> done)
        {
            var name = orig.name; int w = orig.width, h = orig.height, factor = Factor, gen = _generation; bool flip = _pngFlip;
            _pending.Add(name);
            try
            {
                AsyncGPUReadback.Request(orig, 0, TextureFormat.RGBA32, req =>
                {
                    if (req.hasError) { Fail(name, "GPU readback error", done); return; }
                    byte[] src;
                    try { src = req.GetData<byte>().ToArray(); } catch (Exception ex) { Fail(name, ex.Message, done); return; }
                    ThreadPool.QueueUserWorkItem(_ =>
                    {
                        try
                        {
                            int ow, oh;
                            var up = Scale2x.ToBytes(Scale2x.Upscale(Scale2x.FromBytes(src), w, h, factor, out ow, out oh));
                            byte[] png = null;
                            try { png = ImageConversion.EncodeArrayToPNG(flip ? FlipRows(up, ow, oh) : up, GraphicsFormat.R8G8B8A8_UNorm, (uint)ow, (uint)oh); }
                            catch (Exception ex) { RunOnMain(() => Log.LogWarning("PNG encode failed for " + name + " (texture still used this session): " + ex.Message)); }
                            if (png != null)
                            {
                                var tmp = cachePath + ".tmp";
                                File.WriteAllBytes(tmp, png);
                                if (File.Exists(cachePath)) File.Delete(cachePath);
                                File.Move(tmp, cachePath);
                            }
                            RunOnMain(() =>
                            {
                                _pending.Remove(name);
                                if (done == null && gen == _generation) // realtime: build the texture right away from raw data
                                {
                                    var t = new Texture2D(ow, oh, TextureFormat.RGBA32, false);
                                    t.LoadRawTextureData(up); t.Apply(false, true);
                                    t.filterMode = FilterMode.Point; t.wrapMode = TextureWrapMode.Clamp; t.name = name + "_x" + factor;
                                    DontDestroyOnLoad(t);
                                    _bigTex[name] = t; _nextScan = 0f;
                                    Log.LogInfo("Converted " + name + " " + w + "x" + h + " -> " + ow + "x" + oh);
                                }
                                _converted++;
                                if (done != null) done(true);
                            });
                        }
                        catch (Exception ex) { RunOnMain(() => Fail(name, ex.Message, done)); }
                    });
                });
            }
            catch (Exception ex) { Fail(name, ex.Message, done); }
        }

        void Fail(string name, string why, Action<bool> done)
        {
            _pending.Remove(name); _failedTex.Add(name);
            Log.LogWarning("Conversion failed for " + name + ": " + why);
            if (done != null) done(false);
        }

        static byte[] FlipRows(byte[] a, int w, int h)
        {
            var o = new byte[a.Length]; int row = w * 4;
            for (int y = 0; y < h; y++) Buffer.BlockCopy(a, y * row, o, (h - 1 - y) * row, row);
            return o;
        }

        // ---------------- prebake: walk the game's asset bundles ----------------
        static readonly Regex BakeBundleRx = new Regex(@"(troops|wielders|mapentities|shared_assets_all|shared\.battle|specialeffects\.battle|projectcontext)", RegexOptions.IgnoreCase);
        static readonly Regex DecorBundleRx = new Regex(@"cartography\.", RegexOptions.IgnoreCase);
        static readonly Regex UnitBundleRx = new Regex(@"(troops|wielders|specialeffects|shared\.battle)", RegexOptions.IgnoreCase);

        IEnumerator Prebake(string marker)
        {
            _baking = true; _prebakeNow.Value = false;
            yield return new WaitForSecondsRealtime(1f);
            var aa = Path.Combine(Application.streamingAssetsPath, "aa");
            var files = new List<string>();
            if (Directory.Exists(aa))
                foreach (var f in Directory.GetFiles(aa, "*.bundle", SearchOption.AllDirectories))
                {
                    var n = Path.GetFileName(f);
                    if (BakeBundleRx.IsMatch(n) || (_smoothDecorations.Value && DecorBundleRx.IsMatch(n))) files.Add(f);
                }
            Log.LogInfo("Prebake: " + files.Count + " bundles");
            int done = 0, made = 0, skipped = 0;
            foreach (var file in files)
            {
                done++;
                var bname = Path.GetFileName(file);
                _bakeStatus = "Bundle " + done + "/" + files.Count + ": " + bname + "   (converted " + made + ", already cached " + skipped + ")";
                AssetBundle bundle = null; bool ours = false;
                foreach (var b in AssetBundle.GetAllLoadedAssetBundles()) if (b != null && string.Equals(b.name, bname, StringComparison.OrdinalIgnoreCase)) bundle = b;
                if (bundle == null)
                {
                    var lr = AssetBundle.LoadFromFileAsync(file);
                    yield return lr;
                    bundle = lr.assetBundle; ours = bundle != null;
                }
                if (bundle == null) { Log.LogWarning("Prebake: could not open " + bname); continue; }
                var ar = bundle.LoadAllAssetsAsync<Texture2D>();
                yield return ar;
                var kind = UnitBundleRx.IsMatch(bname) ? Kind.Unit : Kind.MapObject;
                var todo = new List<Texture2D>();
                foreach (var o in ar.allAssets)
                {
                    var t = o as Texture2D;
                    if (!Convertible(t) || !Allowed(t.name, kind)) continue;
                    if (File.Exists(CachePath(t)) || (Factor == 4 && File.Exists(Path.Combine(_legacyDir, t.name + ".png")))) { skipped++; continue; }
                    todo.Add(t);
                }
                int left = todo.Count;
                foreach (var t in todo) StartConversion(t, CachePath(t), ok => { left--; if (ok) made++; });
                while (left > 0)
                {
                    _bakeStatus = "Bundle " + done + "/" + files.Count + ": " + bname + "   (converted " + made + ", already cached " + skipped + ")";
                    yield return null;
                }
                if (ours) bundle.Unload(true);
            }
            File.WriteAllText(marker, DateTime.Now.ToString("o"));
            Log.LogInfo("Prebake finished: converted " + made + ", already cached " + skipped);
            _bakeStatus = "Done: converted " + made + ", already cached " + skipped + ". Have fun!";
            yield return new WaitForSecondsRealtime(4f);
            _baking = false;
        }

        // ---------------- scanning & swapping ----------------
        void Scan()
        {
            int swaps = 0;
            try { swaps += SwapAnimations(); } catch (Exception ex) { Log.LogError("SwapAnimations: " + ex); }
            try { swaps += SwapEntityAnimators(); } catch (Exception ex) { Log.LogError("SwapEntityAnimators: " + ex); }
            try { swaps += SwapRenderers(); } catch (Exception ex) { Log.LogError("SwapRenderers: " + ex); }
            if (swaps > 0) _swapsTotal += swaps;
        }

        int SwapAnimations()
        {
            if (_animType == null || _fLayers == null) return 0;
            int n = 0;
            foreach (var anim in Resources.FindObjectsOfTypeAll(_animType))
            {
                if (_fCollider != null)
                {
                    var c = _fCollider.GetValue(anim) as Sprite; var m = Map(c, Kind.Unit);
                    if (m != c) { _fCollider.SetValue(anim, m); n++; }
                }
                var layers = _fLayers.GetValue(anim) as IEnumerable;
                if (layers == null) continue;
                foreach (var layer in layers)
                {
                    if (layer == null) continue;
                    if (_fSprites == null) _fSprites = layer.GetType().GetField("_sprites", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    if (_fSprites == null) return n;
                    var arr = _fSprites.GetValue(layer) as Sprite[];
                    if (arr == null) continue;
                    for (int i = 0; i < arr.Length; i++) { var m = Map(arr[i], Kind.Unit); if (m != arr[i]) { arr[i] = m; n++; } }
                }
            }
            return n;
        }

        FieldInfo[] NestedSpriteFields(Type t)
        {
            FieldInfo[] r;
            if (_nestedSpriteFields.TryGetValue(t, out r)) return r;
            var list = new List<FieldInfo>();
            foreach (var f in t.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
                if (f.FieldType == typeof(Sprite) || f.FieldType == typeof(Sprite[]) || typeof(IList<Sprite>).IsAssignableFrom(f.FieldType)) list.Add(f);
            r = list.ToArray(); _nestedSpriteFields[t] = r; return r;
        }

        int SwapValue(object owner, FieldInfo f, int depth)
        {
            int n = 0;
            var v = f.GetValue(owner);
            if (v == null) return 0;
            if (f.FieldType == typeof(Sprite)) { var sp = (Sprite)v; var m = Map(sp, Kind.MapObject); if (m != sp) { f.SetValue(owner, m); n++; } return n; }
            var arr = v as Sprite[];
            if (arr != null) { for (int i = 0; i < arr.Length; i++) { var m = Map(arr[i], Kind.MapObject); if (m != arr[i]) { arr[i] = m; n++; } } return n; }
            var list = v as IList<Sprite>;
            if (list != null) { for (int i = 0; i < list.Count; i++) { var m = Map(list[i], Kind.MapObject); if (m != list[i]) { list[i] = m; n++; } } return n; }
            if (depth > 0 || v is string) return 0;
            var en = v as IEnumerable;
            if (en == null) return 0;
            foreach (var item in en)
            {
                if (item == null || item.GetType().IsValueType || item is UnityEngine.Object) continue;
                foreach (var nf in NestedSpriteFields(item.GetType())) n += SwapValue(item, nf, depth + 1);
            }
            return n;
        }

        static bool IsUi(Component c) { return c.transform is RectTransform || c.GetComponentInParent<Canvas>() != null; }

        int SwapEntityAnimators()
        {
            int n = 0;
            foreach (var kv in _entityTypes)
                foreach (var obj in Resources.FindObjectsOfTypeAll(kv.Key))
                {
                    var c = obj as Component;
                    if (c == null || IsUi(c)) continue;
                    foreach (var f in kv.Value) n += SwapValue(c, f, 0);
                }
            return n;
        }

        int SwapRenderers()
        {
            int n = 0;
            foreach (var r in Resources.FindObjectsOfTypeAll<SpriteRenderer>())
            {
                if (r == null) continue;
                var s = r.sprite; if (s == null) continue;
                if (IsUi(r)) { var o = ToOriginal(s); if (o != s) { r.sprite = o; n++; } continue; }
                var m = Map(s, Kind.MapObject);
                if (m != s) { r.sprite = m; n++; }
            }
            return n;
        }
    }
}

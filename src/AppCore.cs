using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Soundeck.Models;
using Soundeck.Services;
using Soundeck.ViewModels;
using Soundeck.Views;

// ═══════════════════════════════════════════════════════════════
//  AppCore.cs  —  App  +  MainViewModel
// ═══════════════════════════════════════════════════════════════

namespace Soundeck
{
public partial class App : Application
{
    public static MainViewModel ViewModel  { get; private set; } = new();
    public static MainWindow?   MainWindow { get; private set; }
    private static MainWindow?  _mainWindowRef;

    /// <summary>True when launched with "--minimized" (set by the Windows-startup
    /// registry entry — see SettingsDialog — only when StartMinimizedInTray is on).</summary>
    public static bool StartMinimized { get; private set; }

    [STAThread]
    static void Main(string[] args)
    {
        var oldAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundboard");
        var newAppData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Soundeck");
        if (Directory.Exists(oldAppData) && !Directory.Exists(newAppData))
        {
            try { Directory.Move(oldAppData, newAppData); } catch { }
        }

        StartMinimized = args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));

        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Soundeck", "crash.log");

        try { Directory.CreateDirectory(Path.GetDirectoryName(logPath)!); } catch { }

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            try { File.AppendAllText(logPath, $"[UnhandledException]\n{e.ExceptionObject}\n"); } catch { }
            MsgBox($"Erreur fatale:\n{e.ExceptionObject}");
        };

        try
        {
            File.WriteAllText(logPath, $"[{DateTime.Now}] Main() started\n");
            WinRT.ComWrappersSupport.InitializeComWrappers();
            File.AppendAllText(logPath, "ComWrappers OK\n");

            Application.Start(p =>
            {
                try
                {
                    File.AppendAllText(logPath, "In Start callback\n");
                    var ctx = new DispatcherQueueSynchronizationContext(
                        DispatcherQueue.GetForCurrentThread());
                    System.Threading.SynchronizationContext.SetSynchronizationContext(ctx);
                    File.AppendAllText(logPath, "Creating App\n");
                    _ = new App();
                }
                catch (Exception ex)
                {
                    try { File.AppendAllText(logPath, $"[Start callback]\n{ex}\n"); } catch { }
                    MsgBox($"Erreur Start:\n{ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(logPath, $"[Startup]\n{ex}\n"); } catch { }
            MsgBox($"Erreur démarrage:\n{ex.Message}");
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var logPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Soundeck", "crash.log");
        var sw = System.Diagnostics.Stopwatch.StartNew();
        void Log(string msg) { try { File.AppendAllText(logPath, $"[{sw.ElapsedMilliseconds}ms] {msg}\n"); } catch { } }
        try
        {
            Log("OnLaunched start");
            _ = Soundeck.Services.AudioDeviceCache.WarmupAsync();
            Log("WarmupAsync fired");
            ViewModel.Initialize();
            Log("ViewModel.Initialize() done");
            MainWindow = new MainWindow();
            Log("new MainWindow() done");
            _mainWindowRef = MainWindow;

            if (StartMinimized && ViewModel.Config.MinimizeToTray && ViewModel.Config.StartMinimizedInTray)
            {
                Log("Starting hidden in tray");
                MainWindow.StartHiddenInTray();
                Log("StartHiddenInTray() done");
            }
            else
            {
                MainWindow.Activate();
                Log("MainWindow.Activate() done");
            }

            // Démarre le moteur audio (énumération WASAPI + VB-Cable) en arrière-plan
            // une fois la fenêtre déjà affichée, pour ne pas freezer le lancement.
            _ = ViewModel.InitializeAudioAsync().ContinueWith(_ =>
            {
                Log("InitializeAudioAsync() done");
                // Met à jour la bannière VB-Cable après la détection (Init() la set
                // trop tôt, avant que InitializeAudioAsync() ait fini).
                try { MainWindow.DispatcherQueue.TryEnqueue(() => MainWindow.RefreshVbBanner()); } catch { }
            });
            Log("InitializeAudioAsync fired (fire-and-forget)");
        }
        catch (Exception ex)
        {
            try { File.AppendAllText(logPath, $"[OnLaunched]\n{ex}\n"); } catch { }
            MsgBox($"Erreur OnLaunched:\n{ex.Message}");
        }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    static extern int MessageBoxW(nint h, string t, string c, uint f);
    static void MsgBox(string msg) => MessageBoxW(0, msg, "Soundeck", 0x10);
}
} // namespace Soundeck

// ─────────────────────────────────────────────────────────────────────────────

namespace Soundeck.ViewModels
{
public partial class MainViewModel : INotifyPropertyChanged, IDisposable
{
    // ── Services ──────────────────────────────────────────────────────────────
    public readonly ConfigService   ConfigSvc    = new();
    public readonly AudioEngine     Engine       = new();
    public readonly HotkeyService   HotkeySvc    = new();
    public readonly FreesoundService FreesoundSvc = new();

    // ── Data ──────────────────────────────────────────────────────────────────
    public AppConfig Config { get; private set; } = new();
    public ObservableCollection<Sound>  AllSounds      { get; } = new();
    public ObservableCollection<Sound>  FilteredSounds { get; } = new();
    public ObservableCollection<string> Collections    { get; } = new();

    private readonly Dictionary<string, string> _soundToEngineId = new();

    private Action? _toggleOverlay;
    public void SetOverlayToggle(Action toggleOverlay) => _toggleOverlay = toggleOverlay;

    /// <summary>Levé après PlaySound/StopSound/StopAllSounds pour que la View
    /// puisse rafraîchir cartes + player — notamment quand déclenché par raccourci.</summary>
    public event Action? PlaybackChanged;

    // ── UI state ──────────────────────────────────────────────────────────────
    private string _activeCollection = AppConfig.DefaultCollectionName;
    public string ActiveCollection
    {
        get => _activeCollection;
        set { _activeCollection = value; OnPropertyChanged(); RefreshFilteredSounds(); }
    }

    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set { _searchQuery = value; OnPropertyChanged(); RefreshFilteredSounds(); }
    }

    private bool _isEngineRunning;
    public bool IsEngineRunning
    {
        get => _isEngineRunning;
        set { _isEngineRunning = value; OnPropertyChanged(); }
    }

    private string _statusText = "Ready";
    public string StatusText
    {
        get => _statusText;
        set { _statusText = value; OnPropertyChanged(); }
    }

    private string _vbCableStatus = "";
    public string VbCableStatus
    {
        get => _vbCableStatus;
        set { _vbCableStatus = value; OnPropertyChanged(); }
    }

    private bool _isVbCableInstalled;
    public bool IsVbCableInstalled
    {
        get => _isVbCableInstalled;
        set { _isVbCableInstalled = value; OnPropertyChanged(); }
    }

    private DispatcherQueue? _dispatcherQueue;

    // ── Thread STA dédié à l'audio ─────────────────────────────────────────────
    // NAudio (WasapiOut/WasapiCapture) exige d'être créé sur un thread STA AVEC
    // une vraie boucle de messages Win32/COM qui tourne (un thread STA "nu" sans
    // message pump ne suffit pas : certains appels COM/WASAPI échouent en
    // silence ou ne déclenchent jamais leurs callbacks — c'est ce qui causait
    // "l'engine ne se met plus jamais actif" après le premier passage sur un
    // thread STA manuel sans pompe de messages). On utilise donc un
    // DispatcherQueueController WinUI dédié (CreateOnDedicatedThread), qui crée
    // un thread STA avec une vraie pompe de messages — exactement ce que fait
    // déjà le thread UI principal, mais sur un thread séparé pour ne jamais
    // bloquer l'UI pendant Engine.Start()/Stop().
    private DispatcherQueueController? _audioThreadController;
    private DispatcherQueue? _audioDispatcher;

    private void EnsureAudioThread()
    {
        if (_audioThreadController != null) return;
        _audioThreadController = DispatcherQueueController.CreateOnDedicatedThread();
        _audioDispatcher = _audioThreadController.DispatcherQueue;
    }

    /// <summary>Exécute une action sur le thread STA audio dédié (avec une vraie
    /// boucle de messages) et attend sa fin, sans bloquer le thread appelant
    /// (typiquement le thread UI).</summary>
    private Task RunOnAudioThreadAsync(Action action)
    {
        EnsureAudioThread();
        var tcs = new TaskCompletionSource();
        _audioDispatcher!.TryEnqueue(() =>
        {
            try { action(); tcs.SetResult(); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        return tcs.Task;
    }

    public MainViewModel()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    public void Initialize()
    {
        Config = ConfigSvc.Load();
        
        if (Config.StartWithWindows)
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
                if (key != null)
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    var arg = Config.StartMinimizedInTray ? " --minimized" : "";
                    key.DeleteValue("Soundboard", false);
                    key.SetValue("Soundeck", $"\"{exePath}\"{arg}");
                }
            }
            catch { }
        }
        
        Soundeck.Services.AudioEngine.Config = Config;
        I18n.Init(Config.Language);
        Config.Language = I18n.CurrentLanguage; // Ensure default is saved
        
        // Migrate default collection name based on current language
        string currentDefault = AppConfig.DefaultCollectionName;
        string legacyDefault = I18n.CurrentLanguage == "fr" ? "Default" : "Défaut";
        
        if (Config.Collections != null && Config.Collections.Contains(legacyDefault))
        {
            if (!Config.Collections.Contains(currentDefault))
            {
                for (int i = 0; i < Config.Collections.Count; i++)
                    if (Config.Collections[i] == legacyDefault) Config.Collections[i] = currentDefault;
            }
            else
            {
                Config.Collections.RemoveAll(c => c == legacyDefault);
            }
            
            foreach (var s in Config.Sounds)
                if (s.Collection == legacyDefault) s.Collection = currentDefault;
                
            if (Config.ActiveCollection == legacyDefault)
                Config.ActiveCollection = currentDefault;
        }

        foreach (var s in Config.Sounds)
            if (string.IsNullOrWhiteSpace(s.Collection)) s.Collection = currentDefault;

        ConfigSvc.Save(Config);

        var distinctCols = (Config.Collections ?? new List<string>())
            .Where(c => !string.IsNullOrWhiteSpace(c))
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (!distinctCols.Contains(AppConfig.DefaultCollectionName))
            distinctCols.Insert(0, AppConfig.DefaultCollectionName);

        Config.Collections = distinctCols;
        Collections.Clear();
        foreach (var col in distinctCols) Collections.Add(col);

        AllSounds.Clear();
        foreach (var s in Config.Sounds) AllSounds.Add(s);

        ActiveCollection = string.IsNullOrWhiteSpace(Config.ActiveCollection)
            ? AppConfig.DefaultCollectionName
            : Config.ActiveCollection;

        Engine.Polyphony    = Config.Polyphony;
        Engine.Normalize    = Config.Normalize;
        Engine.MasterVolume = Config.MasterVolume;
        Engine.MonitorVolume = Config.MonitorVolume;
        Engine.MicVolume    = Config.MicVolume;

        // Note : CheckVbCable() et StartEngine() ne sont plus appelés ici.
        // Ils énumèrent les périphériques WASAPI (COM, potentiellement lent au
        // premier appel) et bloquaient l'affichage de la fenêtre pendant
        // plusieurs secondes. Voir InitializeAudioAsync(), appelé juste après
        // que la fenêtre soit affichée (OnLaunched).
    }

    /// <summary>Démarre le moteur audio (détection VB-Cable + énumération des
    /// périphériques + StartEngine) après l'affichage de la fenêtre principale,
    /// pour ne pas freezer le lancement de l'app.
    /// IMPORTANT : l'énumération (lecture WASAPI sans état) passe par le cache
    /// partagé sur le thread pool. La création des objets WasapiOut/WasapiCapture
    /// (dans Engine.Start) DOIT rester sur un thread STA — NAudio s'appuie sur des
    /// composants COM qui peuvent échouer (NullReferenceException sur Play()) si
    /// initialisés depuis un thread MTA du ThreadPool — mais ce thread STA n'a pas
    /// besoin d'être le thread UI. On utilise donc le thread STA audio dédié
    /// (RunOnAudioThreadAsync), ce qui laisse l'UI parfaitement réactive pendant
    /// toute l'initialisation, aussi lente soit-elle.</summary>
    public async Task InitializeAudioAsync()
    {
        // 1. Warm up le cache audio (énumération WASAPI via le thread pool).
        //    Ceci initialise aussi le SharedEnumerator statique d'AudioEngine, ce qui
        //    rend les appels directs (IsVbCableInstalled, FindVbCableDevice) fiables.
        await AudioDeviceCache.WarmupAsync();
        var inputs  = AudioDeviceCache.Inputs;
        var outputs = AudioDeviceCache.Outputs;

        // 2. Détection VB-Cable via WASAPI direct (pas le cache) : on utilise
        //    AudioEngine.FindVbCableDevice() qui a sa propre logique de filtrage,
        //    plutôt que de chercher dans outputs (qui peut rater selon la version).
        var vbDevice   = AudioEngine.FindVbCableDevice();
        IsVbCableInstalled = vbDevice != null;
        VbCableStatus = IsVbCableInstalled ? "VB-Cable ✓" : "VB-Cable not detected";

        var vbId      = vbDevice?.ID;
        var micId     = ResolveDeviceId(Config.MicDevice,     inputs);
        var monitorId = ResolveDeviceId(Config.MonitorDevice, outputs);

        if (micId == null || monitorId == null || vbId == null)
        {
            StopEngine();
            return;
        }

        // 3. Engine.Start() est lourd (Init()/Play() WASAPI) : exécuté sur le thread
        //    STA audio dédié, donc le thread UI ne bloque jamais ici.
        bool ok = false; string? err = null;
        await RunOnAudioThreadAsync(() => (ok, err) = Engine.Start(micId, vbId, monitorId));
        IsEngineRunning = ok;
        StatusText = ok ? "Engine running" : $"Engine error: {err}";
    }

    public void CheckVbCable()
    {
        IsVbCableInstalled = AudioEngine.IsVbCableInstalled();
        VbCableStatus = IsVbCableInstalled ? "VB-Cable ✓" : "VB-Cable not detected";
    }

    private static string? ResolveDeviceId(string? name, List<AudioEngine.AudioDevice> devices)
    {
        if (string.IsNullOrEmpty(name)) return null;
        return devices.FirstOrDefault(d => d.Name == name)?.Id
            ?? devices.FirstOrDefault(d => d.Name.Contains(name, StringComparison.OrdinalIgnoreCase))?.Id;
    }

    public (bool Success, string? Error) StartEngine()
    {
        var vbDevice = AudioEngine.FindVbCableDevice();
        var vbId     = vbDevice?.ID;
        var inputs   = AudioEngine.GetInputDevices();
        var outputs  = AudioEngine.GetOutputDevices();
        var micId     = ResolveDeviceId(Config.MicDevice,     inputs);
        var monitorId = ResolveDeviceId(Config.MonitorDevice, outputs);
        
        if (micId == null || monitorId == null || vbId == null)
        {
            StopEngine();
            return (false, "Please select all audio devices in Settings.");
        }

        var (ok, err) = Engine.Start(micId, vbId, monitorId);
        IsEngineRunning = ok;
        StatusText = ok ? "Engine running" : $"Engine error: {err}";
        return (ok, err);
    }

    public void StopEngine()
    {
        Engine.Stop();
        IsEngineRunning = false;
        StatusText = "Engine stopped";
        _soundToEngineId.Clear();
    }

    public (bool Success, string? Error) RestartEngine() => StartEngine();

    /// <summary>Équivalent asynchrone de RestartEngine(), qui exécute
    /// Engine.Start() sur le thread STA audio dédié au lieu du thread UI.
    /// À utiliser partout où on n'a pas besoin du résultat immédiatement
    /// (ex: AutoSave() dans les Paramètres) pour ne jamais freezer l'UI.</summary>
    public async Task RestartEngineAsync()
    {
        var inputs    = AudioDeviceCache.IsWarm ? AudioDeviceCache.Inputs  : AudioEngine.GetInputDevices();
        var outputs   = AudioDeviceCache.IsWarm ? AudioDeviceCache.Outputs : AudioEngine.GetOutputDevices();
        var vb = outputs.FirstOrDefault(d =>
        {
            var nl = d.Name.ToLowerInvariant();
            if (nl.Contains("voicemeeter") || nl.Contains("voicemod")) return false;
            return nl.Contains("cable input") || nl.Contains("vb-audio virtual cable") || nl.Contains("vbcable");
        });
        var vbId      = vb?.Id;
        var micId     = ResolveDeviceId(Config.MicDevice,     inputs);
        var monitorId = ResolveDeviceId(Config.MonitorDevice, outputs);

        if (micId == null || monitorId == null || vbId == null)
        {
            StopEngine();
            return;
        }

        bool ok = false; string? err = null;
        await RunOnAudioThreadAsync(() => (ok, err) = Engine.Start(micId, vbId, monitorId));
        IsEngineRunning = ok;
        StatusText = ok ? "Engine running" : $"Engine error: {err}";
    }

    // ── Sound operations ──────────────────────────────────────────────────────
    public async Task PlaySoundAsync(Sound sound)
    {
        if (!Engine.IsRunning) StartEngine();

        if (!Engine.Polyphony)
        {
            foreach (var s in AllSounds.Where(x => x.IsPlaying && x.Id != sound.Id).ToList())
                { s.IsPlaying = false; (s.Tag as Action<bool>)?.Invoke(false); }
            _soundToEngineId.Clear();
        }

        if (_soundToEngineId.TryGetValue(sound.Id, out var oldId))
        {
            Engine.StopSound(oldId);
            _soundToEngineId.Remove(sound.Id);
            sound.IsPlaying = false;
            (sound.Tag as Action<bool>)?.Invoke(false);
        }
        else
        {
            var engineId = await Engine.PlaySoundAsync(sound.Filepath, sound.Volume, sound.Loop);
            if (engineId != null)
            {
                _soundToEngineId[sound.Id] = engineId;
                sound.IsPlaying = true;
                (sound.Tag as Action<bool>)?.Invoke(true);
            }
        }
        PlaybackChanged?.Invoke();
    }

    public async Task PlayFileAsync(string filePath)
    {
        if (!Engine.IsRunning) StartEngine();
        await Engine.PlaySoundAsync(filePath, Config.DefaultVolume, false);
        PlaybackChanged?.Invoke();
    }

    public void StopSound(Sound sound)
    {
        if (_soundToEngineId.TryGetValue(sound.Id, out var engineId))
        {
            Engine.StopSound(engineId);
            _soundToEngineId.Remove(sound.Id);
            sound.IsPlaying = false;
            sound.IsPaused = false;
            (sound.Tag as Action<bool>)?.Invoke(false);
            PlaybackChanged?.Invoke();
        }
    }

    public void TogglePause(Sound sound)
    {
        if (_soundToEngineId.TryGetValue(sound.Id, out var engineId))
        {
            if (sound.IsPaused)
            {
                Engine.ResumeSound(engineId);
                sound.IsPaused = false;
            }
            else
            {
                Engine.PauseSound(engineId);
                sound.IsPaused = true;
            }
            PlaybackChanged?.Invoke();
        }
    }

    public void StopAllSounds()
    {
        Engine.StopAllSounds();
        foreach (var s in AllSounds) { s.IsPlaying = false; s.IsPaused = false; (s.Tag as Action<bool>)?.Invoke(false); }
        _soundToEngineId.Clear();
        PlaybackChanged?.Invoke();
    }

    public void UpdateNormalization(bool normalize)
    {
        Config.Normalize = normalize;
        Engine.UpdateNormalization(normalize);
        Save();
    }

    /// <summary>
    /// Appelé périodiquement (tick timer) pour détecter les sons terminés naturellement.
    /// Nettoie _soundToEngineId et lève PlaybackChanged pour que la View rafraîchisse.
    /// </summary>
    public void PollPlayback()
    {
        var finished = Engine.CleanupFinishedSounds();
        if (finished.Count == 0) return;
        foreach (var (engineId, _) in finished)
        {
            // Trouver le sound correspondant par engineId
            var kvp = _soundToEngineId.FirstOrDefault(x => x.Value == engineId);
            if (kvp.Key != null)
            {
                _soundToEngineId.Remove(kvp.Key);
                var sound = AllSounds.FirstOrDefault(s => s.Id == kvp.Key);
                if (sound != null) { sound.IsPlaying = false; (sound.Tag as Action<bool>)?.Invoke(false); }
            }
        }
        PlaybackChanged?.Invoke();
    }

    public Sound AddSound(string filePath, string? name = null, string? collection = null)
    {
        var sound = new Sound
        {
            Id         = Guid.NewGuid().ToString(),
            Name       = name ?? Path.GetFileNameWithoutExtension(filePath),
            Filepath   = filePath,
            Volume     = Config.DefaultVolume,
            Collection = collection ?? (ActiveCollection == "__FAVORITES__" || ActiveCollection == "\x00all" ? AppConfig.DefaultCollectionName : ActiveCollection)
        };
        AllSounds.Add(sound);
        Config.Sounds.Add(sound);
        Save();
        RefreshFilteredSounds();
        return sound;
    }

    public void RemoveSound(Sound sound)
    {
        StopSound(sound);
        AllSounds.Remove(sound);
        Config.Sounds.Remove(sound);
        Save();
        RefreshFilteredSounds();
    }

    public void AddCollection(string name)
    {
        if (Collections.Contains(name)) return;
        Collections.Add(name);
        Config.Collections.Add(name);
        Save();
    }

    public void RemoveCollection(string name, bool deleteSounds = false)
    {
        if (name == AppConfig.DefaultCollectionName) return;
        if (deleteSounds)
        {
            var toRemove = AllSounds.Where(s => s.Collection == name).ToList();
            foreach (var s in toRemove) { StopSound(s); AllSounds.Remove(s); Config.Sounds.Remove(s); }
        }
        else
        {
            foreach (var s in AllSounds.Where(s => s.Collection == name))
                s.Collection = AppConfig.DefaultCollectionName;
        }
        Collections.Remove(name);
        Config.Collections.Remove(name);
        if (ActiveCollection == name) ActiveCollection = AppConfig.DefaultCollectionName;
        Save();
    }

    public void ReorderSound(string draggedId, string targetId)
    {
        if (ReorderSoundSilent(draggedId, targetId))
        {
            Save();
            RefreshFilteredSounds();
        }
    }

    public bool ReorderSoundSilent(string draggedId, string targetId)
    {
        if (draggedId == targetId) return false;

        var dragged = AllSounds.FirstOrDefault(s => s.Id == draggedId);
        var target = AllSounds.FirstOrDefault(s => s.Id == targetId);

        if (dragged == null || target == null) return false;

        var list = FilteredSounds.ToList();
        
        for (int i = 0; i < list.Count; i++)
        {
            if (list[i].Order == 0) list[i].Order = i + 1;
        }

        int draggedIndex = list.IndexOf(dragged);
        int targetIndex = list.IndexOf(target);

        if (draggedIndex < 0 || targetIndex < 0) return false;

        list.RemoveAt(draggedIndex);
        list.Insert(targetIndex, dragged);

        for (int i = 0; i < list.Count; i++)
        {
            list[i].Order = i + 1;
        }

        // Apply changes to FilteredSounds silently
        FilteredSounds.Clear();
        foreach (var s in list) FilteredSounds.Add(s);

        return true;
    }

    public void RefreshFilteredSounds()
    {
        FilteredSounds.Clear();
        var query = SearchQuery.Trim().ToLowerInvariant();
        IEnumerable<Sound> source = AllSounds;

        if (string.IsNullOrEmpty(query))
        {
            if (ActiveCollection == "__FAVORITES__")
                source = source.Where(s => s.Favorite);
            else
                source = source.Where(s => s.Collection == ActiveCollection || ActiveCollection == "\x00all");
        }
        else
            source = source.Where(s => s.Name.Contains(query, StringComparison.OrdinalIgnoreCase));

        foreach (var s in source.OrderByDescending(s => s.Favorite).ThenBy(s => s.Order == 0 ? int.MaxValue : s.Order).ThenBy(s => s.Name))
            FilteredSounds.Add(s);
    }

    public void Save()
    {
        Config.ActiveCollection = ActiveCollection;
        Config.Sounds           = AllSounds.ToList();
        Config.Collections      = Collections.Distinct(StringComparer.Ordinal).ToList();
        ConfigSvc.Save(Config);
    }

    // ── Hotkeys ───────────────────────────────────────────────────────────────
    public void RegisterAllHotkeys(nint hwnd)
    {
        HotkeySvc.Initialize(hwnd);
        HotkeySvc.UnregisterAll();

        if (Config.StopAllHotkey != null)
            HotkeySvc.Register(Config.StopAllHotkey, StopAllSounds);

        if (Config.SearchHotkey != null && _toggleOverlay != null)
            HotkeySvc.Register(Config.SearchHotkey, _toggleOverlay);

        foreach (var sound in AllSounds)
        {
            if (sound.Hotkey != null)
            {
                var s = sound;
                HotkeySvc.Register(s.Hotkey, async () => await PlaySoundAsync(s));
            }
        }
    }

    public void Dispose()
    {
        Save();
        Engine.Dispose();
        HotkeySvc.Dispose();
        _audioThreadController?.ShutdownQueueAsync();
        GC.SuppressFinalize(this);
    }

    // ── INotifyPropertyChanged ────────────────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => _dispatcherQueue?.TryEnqueue(() =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));
}
} // namespace Soundeck.ViewModels

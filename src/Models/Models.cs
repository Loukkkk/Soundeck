using System.ComponentModel;
using System.Runtime.CompilerServices;
using Newtonsoft.Json;

// ═══════════════════════════════════════════════════════════════
//  Models.cs  —  AppConfig  +  Sound
// ═══════════════════════════════════════════════════════════════

namespace Soundeck.Models;

public class AppConfig
{
    public static string DefaultCollectionName => Soundeck.Services.I18n.T("Défaut");

    [JsonProperty("sounds")]
    public List<Sound> Sounds { get; set; } = new();

    [JsonProperty("collections")]
    public List<string> Collections { get; set; } = new();

    [JsonProperty("mic_device")]    public string? MicDevice     { get; set; }
    [JsonProperty("virt_device")]   public string? VirtDevice    { get; set; }
    [JsonProperty("monitor_device")]public string? MonitorDevice { get; set; }

    [JsonProperty("polyphony")]   public bool  Polyphony  { get; set; } = false;
    [JsonProperty("normalize")]   public bool  Normalize  { get; set; } = true;
    [JsonProperty("master_volume")]public float MasterVolume { get; set; } = 1.0f;
    [JsonProperty("monitor_volume")]public float MonitorVolume { get; set; } = 1.0f;
    [JsonProperty("mic_volume")]   public float MicVolume    { get; set; } = 1.0f;
    [JsonProperty("mic_peak_level")]public float MicPeakLevel{ get; set; } = 0.0f;
    [JsonProperty("voice_target_ratio")]public float VoiceTargetRatio { get; set; } = 0.85f;
    
    [JsonProperty("auto_ducking")] public bool AutoDucking   { get; set; } = false;
    [JsonProperty("ducking_strength")]public float DuckingStrength { get; set; } = 0.5f;
    [JsonProperty("has_seen_ftux")]public bool HasSeenFtuxPopup { get; set; } = false;

    [JsonProperty("minimize_to_tray")]      public bool MinimizeToTray      { get; set; } = false;
    [JsonProperty("start_with_windows")]    public bool StartWithWindows    { get; set; } = false;
    
    [JsonProperty("start_minimized_in_tray")]public bool StartMinimizedInTray { get; set; } = false;

    [JsonProperty("language")]          public string? Language        { get; set; }
    [JsonProperty("stop_all_hotkey")]   public List<string>? StopAllHotkey  { get; set; }
    [JsonProperty("search_hotkey")]     public List<string>? SearchHotkey   { get; set; }
    [JsonProperty("freesound_api_key")] public string? FreesoundApiKey { get; set; }

    [JsonProperty("window_width")]  public double WindowWidth  { get; set; } = 1100;
    [JsonProperty("window_height")] public double WindowHeight { get; set; } = 720;

    [JsonProperty("active_collection")] public string? ActiveCollection { get; set; }
    [JsonProperty("default_volume")]    public float  DefaultVolume    { get; set; } = 1.0f;

    [JsonProperty("backdrop_mode")] public string BackdropMode { get; set; } = "solid";
    [JsonProperty("accent_color")]  public string AccentColor  { get; set; } = "sombre";

    [JsonProperty("auto_fetch_covers")] public bool AutoFetchCovers { get; set; } = false;
}

// ───────────────────────────────────────────────────────────────

public class Sound : INotifyPropertyChanged
{
    [JsonProperty("id")]
    public string Id { get; set; } = Guid.NewGuid().ToString();

    private string _name = "";
    [JsonProperty("name")]
    public string Name
    {
        get => _name;
        set { _name = value; OnPropertyChanged(); }
    }

    private string _filepath = "";
    [JsonProperty("filepath")]
    public string Filepath
    {
        get => _filepath;
        set { _filepath = value; OnPropertyChanged(); OnPropertyChanged(nameof(FileExists)); }
    }

    private float _volume = 1.0f;
    [JsonProperty("volume")]
    public float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 2f); OnPropertyChanged(); }
    }

    private bool _loop;
    [JsonProperty("loop")]
    public bool Loop
    {
        get => _loop;
        set { _loop = value; OnPropertyChanged(); }
    }

    private bool _favorite;
    [JsonProperty("favorite")]
    public bool Favorite
    {
        get => _favorite;
        set { _favorite = value; OnPropertyChanged(); }
    }

    [JsonProperty("order")]     public int   Order      { get; set; }

    [JsonProperty("hotkey")]    public List<string>? Hotkey     { get; set; }
    [JsonProperty("collection")]public string?       Collection { get; set; }
    [JsonProperty("cover_url")] public string?       CoverUrl   { get; set; }
    [JsonProperty("card_emoji")]public string?       CardEmoji  { get; set; }
    [JsonProperty("card_color_index")]public int?    CardColorIndex { get; set; }

    [JsonIgnore] public bool   FileExists       => File.Exists(Filepath);
    [JsonIgnore] public bool   IsPlaying        { get; set; }
    [JsonIgnore] public bool   IsPaused         { get; set; }
    [JsonIgnore] public float  PlaybackProgress { get; set; }
    /// <summary>Transient UI callback — stores the card's ApplyPlayState action.</summary>
    [JsonIgnore] public object? Tag             { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

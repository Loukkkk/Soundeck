# 🔊 Soundeck

A modern, fast, and feature-rich Windows Soundboard application. It features built-in voice calibration, auto-ducking, integration with YouTube and Freesound, overlay for quickly playing sounds from your PC, control over various sound levels, and much more.

## ✨ Features

* 🎨 **Modern Design:** Beautiful WinUI 3 interface with dark mode and Windows 11 design guidelines (Acrylic, rounded corners, fluid animations).
* 🎙️ **Smart Voice Calibration:** Calibrate your microphone once so sounds never play louder than your speaking voice.
* 🦆 **Auto-Ducking:** Automatically lowers the volume of playing sounds when you start speaking into your microphone.
* 🎚️ **Normalization:** ReplayGain-style RMS/peak volume analysis normalizes all audio files to a consistent 100% maximum level.
* ⚡ **Quick Overlay:** A global search overlay accessible from anywhere to quickly find and play sounds from your PC.
* 🌐 **Online Search:** Built-in YouTube/Freesound search to find and add sounds directly from the app.
* 📦 **Starter Pack:** A built-in one-click downloader that fetches a curated collection of 25 popular meme sounds.
* 📚 **Collections:** Organize your sounds into multiple customizable collections/tabs.
* 🌍 **Language:** Available in English and French.

---

## 🛡️ Disclaimer

This application was fully coded by AI. Since no other soundboard on the market currently offers this blend of modern design with a host of useful features (sound calibration, auto-ducking, online downloads, etc.)

> [!NOTE]
> The day a human developer creates a similar open-source application with equivalent or superior quality, this repository will be permanently deleted.

---

## 🔒 Security & Permissions

Since this is AI-generated code, transparency is key:

* **No Administrative Privileges:** This application explicitly runs with standard user permissions (`asInvoker`). It does not require, nor will it ever ask for, Administrator privileges to run. The only administrator permission you'll be asked for is related to the automatic installation of VB CABLE, which is required for the app to work properly. However, this permission applies only to the official VB CABLE executable, not to the app itself.
* **UAC Safety Indicator:** If the application ever prompts you with a Windows UAC (User Account Control) warning asking for admin rights, close it immediately—that means the binary has been altered or compromised.

---

## 🛠️ Built With

* **C# / .NET 8**
* **WinUI 3 (Windows App SDK)**
* **NAudio** - Core audio playback engine
* **yt-dlp & FFmpeg** - Audio extraction and conversion

---

## 🚀 How to Build and Run

1. Open `Soundeck.csproj` (or the solution file) in **Visual Studio**.
2. Ensure you have the **.NET Desktop Development** workload and **Windows App SDK** component installed.
3. NuGet packages will restore automatically.
4. Select the target platform (e.g., `x64`).
5. Press **F5** to build and launch!

---

## 📂 File Structure

```
Soundeck/
├── .github/                   # GitHub Actions workflows for automated releases
├── .gitignore                 # Standard Visual Studio gitignore
├── README.md                  # Project documentation
└── src/
    ├── Soundeck.csproj        # The WinUI 3 project file
    ├── app.manifest           # Windows application manifest (permissions, DPI)
    ├── icon.ico               # Application icon
    ├── AppCore.cs             # Native entry point for WinUI 3
    ├── App.xaml(.cs)          # Application lifecycle and service registration
    ├── Controls/
    │   └── WrapPanel.cs       # Custom UI layout controls
    ├── Resources/
    │   ├── Colors.xaml        # Theme color dictionaries
    │   └── Styles.xaml        # Global UI styling and templates
    ├── Models/
    │   └── Models.cs          # Core metadata, settings, sound entities
    ├── Services/
    │   ├── Services.cs        # Centralized services (AudioEngine, Downloads, yt-dlp...)
    │   └── I18n.cs            # Localization strings (FR/EN)
    └── Views/
        └── Views.cs           # Main UI, navigation, and playback bar overlay
```

---

## 🗄️ Cache & Application Data Locations

Soundeck stores its configuration and downloaded assets inside your Windows user local directory.

* Main Application Directory:
```
%LOCALAPPDATA%\Soundeck\
```
*(Equivalent to: `C:\Users\<YourUsername>\AppData\Local\Soundeck\`)*

Inside this folder, you will find:
- `config.json`: Your saved preferences, hotkeys, and collections.
- `downloads\`: Where online sounds (MyInstants, YouTube) are saved.
- `yt-dlp.exe`: Automatically downloaded for YouTube extraction.
- `ffmpeg.exe`: Automatically downloaded for audio conversion.

---

## 🗑️ Uninstall

Soundeck does not have an installer — to fully remove it:

1. Exit Soundeck (right-click the tray icon ➔ **Quit** or close the app).
2. Delete the application folder containing the `.exe` file.
3. Delete the following cache/settings folder:
   - `%LOCALAPPDATA%\Soundeck`
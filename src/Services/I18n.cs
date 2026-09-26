using System.Collections.Generic;
using System.Globalization;

namespace Soundeck.Services
{
    public static class I18n
    {
        public static string CurrentLanguage { get; set; } = "fr";

        private static readonly Dictionary<string, string> _translations = new Dictionary<string, string>
        {
            ["Volume de l'application (retour) :"] = "Application volume (feedback):",
            ["Astuce : Si vous utilisez cette option, il est conseillé de régler le \"Volume des sons par rapport à la voix\" à 100% juste au-dessus."] = "Tip: If you use this option, it is recommended to set the \"Volume of sounds relative to voice\" to 100% just above.",
            // -- Navigation & tabs ------------------------------------------
            ["Favoris"] = "Favorites",
            ["Collections"] = "Collections",
            ["Paramètres"] = "Settings",
            ["Recherche en ligne"] = "Online search",
            ["Fichiers du PC"] = "PC files",

            // -- Sound grid & playback --------------------------------------
            ["Défaut"] = "Default",
            ["Stop tout"] = "Stop all",
            ["Ajouter"] = "Add",
            ["Ajouter un son"] = "Add a sound",
            ["Ajouter le son"] = "Add sound",
            ["Aucun son en lecture"] = "No sound playing",
            ["Aucun son dans cette collection"] = "No sounds in this collection",
            ["Cliquez sur « + Ajouter » ou glissez des fichiers audio ici"] = "Click on « + Add » or drag and drop audio files here",
            ["boucle"] = "loop",
            ["Lire"] = "Play",
            ["Arrêter"] = "Stop",
            ["Pause"] = "Pause",
            ["Lecture"] = "Playback",

            // -- Context menu -----------------------------------------------
            ["Renommer"] = "Rename",
            ["Modifier le nom"] = "Edit name",
            ["Changer la couleur/icône"] = "Change color/icon",
            ["Changer le fichier"] = "Change file",
            ["Déplacer vers..."] = "Move to...",
            ["Déplacer vers."] = "Move to...",
            ["Déplacer"] = "Move",
            ["Déplacer..."] = "Move...",
            ["Supprimer"] = "Delete",
            ["sons ?"] = "sounds?",
            ["son"] = "sound",
            ["sons"] = "sounds",
            ["Les fichiers audio ne sont PAS supprimés."] = "The audio files are NOT deleted.",
            ["sons sélectionnés"] = "sounds selected",
            ["Supprimer (garder les sons)"] = "Delete (keep sounds)",
            ["Supprimer (supprimer les sons)"] = "Delete (delete sounds)",
            ["et tous ses sons ?"] = "and all its sounds?",
            ["Supprimer le son"] = "Delete sound",
            ["Favori"] = "Favorite",
            ["Boucle on/off"] = "Loop on/off",
            ["Définir raccourci"] = "Set shortcut",

            // -- Dialogs ----------------------------------------------------
            ["Confirmation"] = "Confirmation",
            ["Nouveau nom :"] = "New name:",
            ["Valider"] = "Apply",
            ["Annuler"] = "Cancel",
            ["Oui"] = "Yes",
            ["Non"] = "No",
            ["Ok"] = "Ok",
            ["Fermer"] = "Close",
            ["Créer"] = "Create",
            ["Effacer"] = "Clear",
            ["Définir"] = "Set",
            ["Voulez-vous vraiment supprimer ce son ?"] = "Are you sure you want to delete this sound?",
            ["Voulez-vous vraiment réinitialiser ? Cela supprimera TOUS vos sons et collections."] = "Are you sure you want to reset? This will delete ALL your sounds and collections.",
            ["Couleur et icône"] = "Color and icon",
            ["Choisissez une couleur :"] = "Choose a color:",

            // -- Hotkey / shortcut capture ----------------------------------
            ["Raccourci clavier"] = "Keyboard shortcut",
            ["Raccourcis globaux"] = "Global shortcuts",
            ["Cliquez pour assigner (Échap pour annuler)"] = "Click to assign (Esc to cancel)",
            ["Assigner"] = "Assign",
            ["Appuyez sur les touches..."] = "Press keys...",
            ["Appuyez sur une combinaison..."] = "Press a combination...",
            ["Appuyez sur une combinaison."] = "Press a combination...",
            ["Maintenez les modificateurs (Ctrl, Alt, Shift) puis appuyez sur la touche."] = "Hold modifiers (Ctrl, Alt, Shift) then press the key.",
            ["Relâchez les touches pour confirmer."] = "Release keys to confirm.",
            ["Stop All :"] = "Stop All:",
            ["Overlay de recherche PC :"] = "PC Search Overlay:",
            ["Stop All (Arrêter tout)"] = "Stop All",
            ["(aucun)"] = "(none)",
            ["Obtenir une clé ?"] = "Get a key ?",

            // -- Settings: Appearance ---------------------------------------
            ["Apparence"] = "Appearance",
            ["Fond :"] = "Background:",
            ["Solide"] = "Solid",
            ["Mica"] = "Mica",
            ["Mica Alt"] = "Mica Alt",
            ["Acrylique"] = "Acrylic",
            ["Couleur :"] = "Color:",

            // -- Settings: Audio devices ------------------------------------
            ["Périphériques audio"] = "Audio devices",
            ["Entrée (Micro)"] = "Input (Mic)",
            ["Sortie (Haut-parleurs)"] = "Output (Speakers)",
            ["Sortie (Câble Virtuel)"] = "Output (Virtual Cable)",
            ["Microphone source :"] = "Microphone source:",
            ["Mic virtuel (VB-Cable) :"] = "Virtual Mic (VB-Cable):",
            ["Sortie monitor :"] = "Monitor output:",
            ["Actualiser les périphériques"] = "Refresh devices",
            ["(Aucun)"] = "(None)",
            ["Activer le monitoring (entendre les sons)"] = "Enable monitoring (hear sounds)",

            // -- Settings: Volume -------------------------------------------
            ["Volume"] = "Volume",
            ["VOLUME PRINCIPAL"] = "MASTER VOLUME",
            ["VOLUME MICRO"] = "MIC VOLUME",
            ["VOLUME DÉFAUT SONS"] = "DEFAULT SOUNDS VOLUME",

            // -- Settings: Options ------------------------------------------
            ["Options"] = "Options",
            ["Polyphonie (plusieurs sons simultanés) :"] = "Polyphony (play multiple sounds at once):",
            ["Normalisation automatique du volume :"] = "Auto volume normalization:",
            ["Minimiser dans la barre des tâches à la fermeture"] = "Minimize to system tray on close",
            ["Réduire dans la zone de notification (Tray) :"] = "Minimize to system tray (Tray):",
            ["Démarrer réduit dans la zone de notification :"] = "Start minimized in system tray:",

            // -- Settings: System -------------------------------------------
            // -- Settings: System -------------------------------------------
            ["Système & Options"] = "System & Options",
            ["Lancer au démarrage de Windows :"] = "Start with Windows:",
            ["Démarrage avec Windows"] = "Start with Windows",
            ["Si le dossier de l'application est déplacé, il suffit de relancer l'application manuellement pour mettre à jour le chemin de démarrage."] = "If the application folder is moved, simply restart the application manually to update the startup path.",
            ["Démarrer réduit dans la zone de notification :"] = "Start minimized in system tray:",
            ["Réduire dans la zone de notification (Tray) :"] = "Minimize to system tray (Tray):",
            ["Langue :"] = "Language:",
            ["Langue"] = "Language",
            ["Veuillez redémarrer l'application pour appliquer le changement de langue. / Please restart the app to apply language changes."] = "Please restart the application to apply the language change.",
            ["Pochettes automatiques (iTunes) :"] = "Automatic covers (iTunes):",
            ["Quitter"] = "Quit",
            ["Ouvrir"] = "Open",

            // -- Settings: Appearance ---------------------------------------
            ["Apparence"] = "Appearance",
            ["Effet de fond :"] = "Background effect:",

            // -- Settings: Audio & Calib ------------------------------------
            ["Périphériques Audio"] = "Audio Devices",
            ["Sélectionnez vos entrées et sorties pour le routage audio."] = "Select your inputs and outputs for audio routing.",
            ["Microphone (Entrée) :"] = "Microphone (Input):",
            ["Mic virtuel (VB-Cable) :"] = "Virtual Mic (VB-Cable):",
            ["Sortie Monitor (Haut-parleurs) :"] = "Monitor Output (Speakers):",
            ["Actualiser les périphériques"] = "Refresh devices",
            
            ["Lecture & Volumes"] = "Playback & Volumes",
            
            ["Calibrage du micro"] = "Microphone Calibration",
            ["Le calibrage de la voix permet d'équilibrer tous les sons pour qu'ils ne soient jamais plus forts que vous."] = "Voice calibration balances all sounds so they are never louder than you.",
            ["Démarrer le Calibrage"] = "Start Calibration",
            ["Réinitialiser"] = "Reset",
            ["Niveau : "] = "Level: ",
            ["Volume des sons par rapport à la voix :"] = "Sounds volume relative to voice:",
            
            ["Auto-Ducking"] = "Auto-Ducking",
            ["Baisse automatiquement le volume des sons lorsque vous parlez."] = "Automatically lowers the volume of sounds when you speak.",
            ["Activer l'Auto-Ducking :"] = "Enable Auto-Ducking:",
            ["Astuce : Réglez le volume des sons par rapport à la voix sur 100% (rubrique Calibrage)."] = "Tip: Set the sounds volume relative to voice to 100% (Calibration section).",
            ["Force de l'atténuation :"] = "Ducking strength:",

            // -- Settings: Hotkeys ------------------------------------------
            ["Raccourcis globaux"] = "Global shortcuts",
            ["Stopper tous les sons :"] = "Stop all sounds:",
            ["Ouvrir la barre de recherche :"] = "Open search bar:",
            ["Définir"] = "Set",
            ["Effacer"] = "Clear",

            // -- Settings: Online search / Freesound ------------------------
            ["Intégrations (Optionnel)"] = "Integrations (Optional)",
            ["Clés API pour les recherches en ligne."] = "API keys for online searches.",
            ["Clé API Freesound :"] = "Freesound API key:",
            ["Clé API Freesound"] = "Freesound API key",
            ["Collez votre clé ici..."] = "Paste your key here...",
            ["Obtenir une clé"] = "Get a key",

            // -- Settings: Covers -------------------------------------------
            ["Pochettes des sons"] = "Sound covers",
            ["Pochettes de sons"] = "Sound covers",
            ["Télécharger automatiquement l'image..."] = "Auto-download thumbnail...",
            ["Rechercher automatiquement des pochettes en ligne (iTunes)"] = "Automatically search for covers online (iTunes)",
            ["Quand activé, une pochette d'album est automatiquement recherchée en ligne en fonction du nom de chaque son."] = "When enabled, an album cover is automatically searched online based on each sound's name.",

            // -- Settings: Starter pack / Danger zone -----------------------
            ["Contenu Additionnel"] = "Additional Content",
            ["Collection de base"] = "Starter pack",
            ["Starter Pack installé"] = "Starter Pack installed",
            ["Installer le pack"] = "Install pack",
            ["Télécharger le Starter Pack"] = "Download Starter Pack",
            ["Tout réinitialiser"] = "Reset all",
            ["Zone de danger"] = "Danger zone",

            // -- Online search panel ----------------------------------------
            ["En ligne (YouTube)"] = "Online (YouTube)",
            ["En ligne (Freesound)"] = "Online (Freesound)",
            ["PC local"] = "Local PC",
            ["Durée :"] = "Duration:",
            ["Toutes"] = "All",
            ["Courtes (< 30s)"] = "Short (< 30s)",
            ["Longues (> 30s)"] = "Long (> 30s)",
            ["Résultats :"] = "Results:",
            ["Téléchargement..."] = "Downloading...",
            ["Téléchargement en cours..."] = "Downloading...",
            ["Téléchargement en cours."] = "Downloading...",
            ["Convertir en MP3"] = "Convert to MP3",
            ["Erreur de téléchargement"] = "Download error",
            ["Réessayer le téléchargement"] = "Retry download",
            ["Aucun résultat trouvé."] = "No results found.",
            ["Recherchez des sons pour commencer"] = "Search for sounds to start",
            ["Erreur"] = "Error",
            ["Réessayer"] = "Retry",
            ["Recherche..."] = "Searching...",
            ["Rechercher en ligne"] = "Search online",
            ["Rechercher sur le PC"] = "Search on PC",
            ["Recherche sur PC"] = "PC Search",
            ["Recherche rapide — bibliothèque + PC..."] = "Quick search — library + PC...",
            ["Rechercher un son..."] = "Search for a sound...",

            // -- Collection management --------------------------------------
            ["Créer une collection"] = "Create collection",
            ["Nom de la collection :"] = "Collection name:",

            // -- Toggle states ----------------------------------------------
            ["Désactivé"] = "Off",
            ["Activé"] = "On",

            // -- Misc -------------------------------------------------------
            ["Jouer"] = "Play",
            ["Changer"] = "Change",
            ["Dossier principal :"] = "Main folder:",
            ["Trier par :"] = "Sort by:",
            ["Initialisation..."] = "Initializing...",
            ["Veuillez patienter pendant le téléchargement des outils nécessaires (yt-dlp et ffmpeg)..."] = "Please wait while downloading required tools (yt-dlp and ffmpeg)...",
            ["Les sons ont été ajoutés à la collection 'Starter Pack'"] = "Sounds were added to the 'Starter Pack' collection",
            ["Clear"] = "Clear",

            // -- Sidebar & navigation (hardcoded) --------------------------
            ["Outils"] = "Tools",
            ["Parcourir le PC"] = "Browse PC",
            ["Nouvelle collection"] = "New collection",
            ["Nouvelle collection."] = "New collection...",
            ["Engine actif"] = "Engine active",
            ["Engine arrêté"] = "Engine stopped",

            // -- Sound grid search bar -------------------------------------

            // -- PC Browser panel ------------------------------------------
            ["Rechercher un fichier audio sur ce PC..."] = "Search for an audio file on this PC...",
            ["Rescanner"] = "Rescan",
            ["Tout arrêter"] = "Stop all",
            ["En attente..."] = "Waiting...",
            ["fichier(s) trouvé(s)"] = "file(s) found",
            ["Fichier introuvable"] = "File not found",

            // -- Online search panel ---------------------------------------
            ["Rechercher des sons..."] = "Search for sounds...",
            ["Rechercher des sons... (ex: explosion, pluie, applaudissements)"] = "Search for sounds... (e.g. explosion, rain, applause)",
            ["Rechercher"] = "Search",
            ["Max 30s"] = "Max 30s",
            ["10 résultats"] = "10 results",
            ["15 résultats"] = "15 results",
            ["25 résultats"] = "25 results",
            ["50 résultats"] = "50 results",

            // -- Starter pack button states --------------------------------
            ["Starter Pack installé"] = "Starter Pack installed",
        
            // -- Newly added translations (Batch 2) -------------------------
            ["Enregistrer"] = "Save",

            ["? Relâchez les touches pour confirmer."] = "? Release keys to confirm.",
            ["Double-cliquez ou Entrée pour jouer"] = "Double-click or Enter to play",
            ["Déplacer vers"] = "Move to",







            ["Le fichier audio n'est PAS supprimé."] = "The audio file is NOT deleted.",
            ["Illimité"] = "Unlimited",

            ["Raccourci"] = "Shortcut",



            ["Coller une URL YouTube ou saisir des mots-clés..."] = "Paste a YouTube URL or enter keywords...",
        
            ["VB-Cable non installé — routage micro indisponible."] = "VB-Cable not installed — mic routing unavailable.",
            ["Installer"] = "Install",
            ["Installer VB-Cable ? (Gratuit, nécessite les droits admin)"] = "Install VB-Cable? (Free, requires admin rights)",
            ["VB-Cable installé !"] = "VB-Cable installed!",
            ["Échec"] = "Failed",
            ["Erreur inconnue"] = "Unknown error",

            ["Calibrage du micro"] = "Microphone Calibration",
            ["Démarrer le Calibrage"] = "Start Calibration",
            ["Réinitialiser"] = "Reset",
            ["Parlez dans votre micro..."] = "Speak into your microphone...",
            ["Calibrage terminé !"] = "Calibration complete!",
            ["Le calibrage de la voix permet d'équilibrer tous les sons pour qu'ils soient légèrement plus bas que vous."] = "Voice calibration balances all sounds to be slightly quieter than you.",
            ["Auto-Ducking (Baisse des sons quand vous parlez)"] = "Auto-Ducking (Lowers sounds when you speak)",
            ["Force de l'atténuation :"] = "Ducking strength:",
            ["Bienvenue sur Soundeck !"] = "Welcome to Soundeck!",
            ["Pour que le volume de vos sons soit parfaitement équilibré et n'écrase jamais votre voix, nous vous conseillons d'aller dans les Paramètres pour effectuer un Calibrage de votre micro."] = "To ensure your Soundeck volume is perfectly balanced and never overpowers your voice, we highly recommend going to Settings to perform a Microphone Calibration.",
            ["Compris, aller aux paramètres"] = "Got it, take me to settings"
        };


        public static void Init(string? configLang)
        {
            if (string.IsNullOrEmpty(configLang))
            {
                var osLang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
                CurrentLanguage = (osLang == "fr") ? "fr" : "en";
            }
            else
            {
                CurrentLanguage = configLang;
            }
        }

        public static string T(string frText)
        {
            if (CurrentLanguage == "fr") return frText;
            if (_translations.TryGetValue(frText, out var enText)) return enText;
            
            if (frText.EndsWith(" sons") || frText.EndsWith(" son")) 
            {
                return frText.Replace("sons", "sounds").Replace("son", "sound");
            }
            return frText;
        }
    }
}


using System;
using System.IO;
using System.Text.Json;

namespace SpeechToTextAI;

/// <summary>
/// Ustawienia aplikacji zapisywane do pliku JSON obok aplikacji.
/// Przezywaja restart. Plik: %LOCALAPPDATA%\SpeechToTxtAI\settings.json
/// </summary>
public sealed class AppSettings
{
    /// <summary>Czas ciszy (ms) konczacy wypowiedz i wysylajacy ja do MOSS.</summary>
    public int SilenceFlushMs { get; set; } = 800;

    /// <summary>Czy automatycznie kopiowac rozpoznany tekst do schowka.</summary>
    public bool AutoCopy { get; set; } = true;

    // ---- zapis / odczyt ----

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private static string SettingsPath
    {
        get
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "SpeechToTxtAI");
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "settings.json");
        }
    }

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                       ?? new AppSettings();
        }
        catch { /* uszkodzony plik -> domyslne */ }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch { /* brak zapisu nie jest krytyczny */ }
    }
}

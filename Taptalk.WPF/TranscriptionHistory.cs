using System.IO;
using System.Text.Json;
using Taptalk.Core;

namespace Taptalk.WPF;

/// <summary>
/// Simple persistent transcription history.
/// Stored in %LOCALAPPDATA%\Taptalk\history.json, capped to 100 items.
/// </summary>
public class HistoryItem
{
    public DateTime Timestamp { get; set; }
    public string Text { get; set; } = "";
}

public static class TranscriptionHistory
{
    private const int MaxItems = 100;

    private static string HistoryPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Taptalk", "history.json");

    public static List<HistoryItem> Load()
    {
        try
        {
            if (!File.Exists(HistoryPath)) return new List<HistoryItem>();
            var json = File.ReadAllText(HistoryPath);
            var items = JsonSerializer.Deserialize<List<HistoryItem>>(json);
            return items ?? new List<HistoryItem>();
        }
        catch (Exception ex)
        {
            DebugRecorder.Log("CFG", $"Load history failed: {ex.Message}");
            return new List<HistoryItem>();
        }
    }

    public static void Add(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        try
        {
            var items = Load();
            items.Insert(0, new HistoryItem { Timestamp = DateTime.Now, Text = text.Trim() });
            while (items.Count > MaxItems) items.RemoveAt(items.Count - 1);

            var dir = Path.GetDirectoryName(HistoryPath)!;
            Directory.CreateDirectory(dir);
            File.WriteAllText(HistoryPath, JsonSerializer.Serialize(items));
        }
        catch (Exception ex)
        {
            DebugRecorder.Log("CFG", $"Save history failed: {ex.Message}");
        }
    }

    public static void Clear()
    {
        try
        {
            if (File.Exists(HistoryPath))
                File.Delete(HistoryPath);
        }
        catch (Exception ex)
        {
            DebugRecorder.Log("CFG", $"Clear history failed: {ex.Message}");
        }
    }
}

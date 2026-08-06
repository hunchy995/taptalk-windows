namespace Taptalk.Core;

/// <summary>
/// Abstraction every STT engine implements.
/// </summary>
public interface ISttEngine : IDisposable
{
    string Name { get; }
    bool IsLoaded { get; }
    bool RequiresModelFile { get; }

    /// <summary>Load a local model file. Returns true on success.</summary>
    bool LoadModel(string modelPath);

    /// <summary>Transcribe full PCM audio (float32, 16kHz mono).</summary>
    string Transcribe(float[] audio);

    /// <summary>Transcribe partial audio during streaming (progressive buffer).</summary>
    string TranscribePartial(float[] audio);
}

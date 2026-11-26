namespace FastVRecorder.Core.Recovery;

/// <summary>
/// Sidecar for an in-progress .wav.part. The part file itself carries a real WAV
/// header, so recovery can proceed from audio bytes alone if this JSON is lost.
/// </summary>
public sealed class RecoveryMetadata
{
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public string RecordingId { get; set; } = Guid.NewGuid().ToString("N");

    public string BaseName { get; set; } = string.Empty;

    /// <summary>Final path reserved for the finished recording.</summary>
    public string IntendedOutputPath { get; set; } = string.Empty;

    public string PartPath { get; set; } = string.Empty;

    public string Format { get; set; } = "WavPcm16";

    public int SampleRate { get; set; } = 48000;

    public int Channels { get; set; } = 1;

    public int BitsPerSample { get; set; } = 16;

    public int BlockAlign { get; set; } = 2;

    public int ByteRate { get; set; } = 96000;

    public int SegmentIndex { get; set; }

    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;

    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

    public string AppVersion { get; set; } = "1.0.0";

    public DateTime? RecoveredAtUtc { get; set; }

    public string? RecoveredPath { get; set; }

    public string? RecoveryError { get; set; }
}

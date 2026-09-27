using System.Text.Json;
using System.Text.Json.Serialization;
using DexStream.Core.Rendering;
using DexStream.Core.Session;

namespace DexStream.Core.Settings;

/// <summary>User preferences, persisted as JSON next to the executable's local app data.</summary>
public sealed record AppSettings
{
    /// <summary>Longest edge to capture in pixels; 0 means the display maximum.</summary>
    public int MaxSize { get; init; }

    /// <summary>Requested refresh rate in Hz; 0 means the display maximum.</summary>
    public int TargetRefreshRate { get; init; }

    /// <summary>Encoder bitrate in bits per second; 0 means the automatic estimate.</summary>
    public int Bitrate { get; init; }

    public CodecPreference Codec { get; init; } = CodecPreference.Automatic;

    public ScalingMode Scaling { get; init; } = ScalingMode.Fit;

    public bool TurnPhoneScreenOff { get; init; } = true;

    public bool RequireDexDisplay { get; init; }

    public bool ActivateDexIfMissing { get; init; } = true;

    /// <summary>Start streaming as soon as a supported device is plugged in.</summary>
    public bool AutoStartOnConnect { get; init; } = true;

    /// <summary>Reuse <c>%USERPROFILE%\.android\adbkey</c> so the device does not re-prompt.</summary>
    public bool ReuseSystemAdbKey { get; init; } = true;

    /// <summary>Keep the window on top of other windows while streaming.</summary>
    public bool AlwaysOnTop { get; init; }

    /// <summary>Show the metrics bar beneath the stream.</summary>
    public bool ShowMetricsBar { get; init; } = true;

    public StreamSessionOptions ToSessionOptions() => new()
    {
        MaxSize = MaxSize,
        TargetRefreshRate = TargetRefreshRate,
        Bitrate = Bitrate,
        Codec = Codec,
        Scaling = Scaling,
        TurnPhoneScreenOff = TurnPhoneScreenOff,
        RequireDexDisplay = RequireDexDisplay,
        ActivateDexIfMissing = ActivateDexIfMissing,
        ReuseSystemAdbKey = ReuseSystemAdbKey,
    };
}

/// <summary>Loads and saves <see cref="AppSettings"/>, tolerating a missing or corrupt file.</summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _path;

    public SettingsStore(string path) => _path = path;

    /// <summary>The default location: <c>%LOCALAPPDATA%\DexStream\settings.json</c>.</summary>
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DexStream",
        "settings.json");

    /// <summary>The file this store reads and writes.</summary>
    public string FilePath => _path;

    /// <summary>
    /// Reads the settings file. A missing file yields defaults; a corrupt one also yields defaults
    /// rather than blocking startup, and the reason is reported through <paramref name="warning"/>.
    /// </summary>
    public AppSettings Load(out string? warning)
    {
        warning = null;

        try
        {
            if (!File.Exists(_path))
            {
                return new AppSettings();
            }

            string json = File.ReadAllText(_path);
            return JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            warning = $"Could not read settings from {_path} ({ex.Message}); using defaults.";
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        // Write to a temporary file and move it into place so a crash mid-write cannot leave a
        // truncated settings file behind.
        string temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(settings, SerializerOptions));
        File.Move(temp, _path, overwrite: true);
    }
}

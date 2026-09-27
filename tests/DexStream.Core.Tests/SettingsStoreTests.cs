using DexStream.Core.Rendering;
using DexStream.Core.Session;
using DexStream.Core.Settings;
using Xunit;

namespace DexStream.Core.Tests;

public class SettingsStoreTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), "dexstream-tests-" + Guid.NewGuid().ToString("N"));

    private string SettingsPath => Path.Combine(_directory, "settings.json");

    [Fact]
    public void Load_ReturnsDefaultsWhenTheFileIsMissing()
    {
        var store = new SettingsStore(SettingsPath);

        AppSettings settings = store.Load(out string? warning);

        Assert.Null(warning);
        Assert.Equal(0, settings.MaxSize);
        Assert.Equal(CodecPreference.Automatic, settings.Codec);
        Assert.True(settings.AutoStartOnConnect);
    }

    [Fact]
    public void SaveThenLoad_RoundTripsEveryField()
    {
        var store = new SettingsStore(SettingsPath);
        var original = new AppSettings
        {
            MaxSize = 2560,
            TargetRefreshRate = 120,
            Bitrate = 40_000_000,
            Codec = CodecPreference.H265,
            Scaling = ScalingMode.IntegerFit,
            TurnPhoneScreenOff = false,
            RequireDexDisplay = true,
            ActivateDexIfMissing = false,
            AutoStartOnConnect = false,
            ReuseSystemAdbKey = false,
            AlwaysOnTop = true,
            ShowMetricsBar = false,
        };

        store.Save(original);
        AppSettings loaded = store.Load(out string? warning);

        Assert.Null(warning);
        Assert.Equal(original, loaded);
    }

    [Fact]
    public void Save_CreatesTheDirectory()
    {
        var store = new SettingsStore(Path.Combine(_directory, "nested", "deeper", "settings.json"));

        store.Save(new AppSettings());

        Assert.True(File.Exists(store.FilePath));
    }

    [Fact]
    public void Save_LeavesNoTemporaryFileBehind()
    {
        var store = new SettingsStore(SettingsPath);

        store.Save(new AppSettings());

        Assert.False(File.Exists(SettingsPath + ".tmp"));
    }

    [Fact]
    public void Save_OverwritesAnExistingFile()
    {
        var store = new SettingsStore(SettingsPath);
        store.Save(new AppSettings { MaxSize = 1920 });

        store.Save(new AppSettings { MaxSize = 3840 });

        Assert.Equal(3840, store.Load(out _).MaxSize);
    }

    [Fact]
    public void Load_FallsBackToDefaultsAndWarnsOnCorruptJson()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "{ this is not json");
        var store = new SettingsStore(SettingsPath);

        AppSettings settings = store.Load(out string? warning);

        Assert.NotNull(warning);
        Assert.Contains("using defaults", warning);
        Assert.Equal(new AppSettings(), settings);
    }

    [Fact]
    public void Load_TreatsAJsonNullAsDefaults()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, "null");

        AppSettings settings = new SettingsStore(SettingsPath).Load(out string? warning);

        Assert.Null(warning);
        Assert.Equal(new AppSettings(), settings);
    }

    [Fact]
    public void Load_IgnoresUnknownProperties()
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, """{ "maxSize": 1280, "somethingRemoved": 42 }""");

        AppSettings settings = new SettingsStore(SettingsPath).Load(out string? warning);

        Assert.Null(warning);
        Assert.Equal(1280, settings.MaxSize);
    }

    [Fact]
    public void EnumsArePersistedByName()
    {
        var store = new SettingsStore(SettingsPath);

        store.Save(new AppSettings { Codec = CodecPreference.H265, Scaling = ScalingMode.Stretch });

        string json = File.ReadAllText(SettingsPath);
        Assert.Contains("\"H265\"", json);
        Assert.Contains("\"Stretch\"", json);
    }

    [Fact]
    public void ToSessionOptions_CarriesTheStreamingPreferences()
    {
        var settings = new AppSettings
        {
            MaxSize = 2560,
            TargetRefreshRate = 120,
            Bitrate = 30_000_000,
            Codec = CodecPreference.H265,
            Scaling = ScalingMode.Stretch,
            TurnPhoneScreenOff = false,
            RequireDexDisplay = true,
            ActivateDexIfMissing = false,
            ReuseSystemAdbKey = false,
        };

        StreamSessionOptions options = settings.ToSessionOptions();

        Assert.Equal(2560, options.MaxSize);
        Assert.Equal(120, options.TargetRefreshRate);
        Assert.Equal(30_000_000, options.Bitrate);
        Assert.Equal(CodecPreference.H265, options.Codec);
        Assert.Equal(ScalingMode.Stretch, options.Scaling);
        Assert.False(options.TurnPhoneScreenOff);
        Assert.True(options.RequireDexDisplay);
        Assert.False(options.ActivateDexIfMissing);
        Assert.False(options.ReuseSystemAdbKey);
    }

    [Fact]
    public void DefaultPath_IsUnderLocalApplicationData()
        => Assert.Contains("DexStream", SettingsStore.DefaultPath);

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }
}

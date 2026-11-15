using Xunit;
using System.Text.Json;
using KyberDash.Core.Resilience;
using KyberDash.Data.Repositories;
using KyberDash.Domain;

namespace KyberDash.Data.Tests;

/// <summary>Fail-closed validation and persistence for <see cref="SettingsRepository"/>.</summary>
public class SettingsRepositoryTests : IDisposable
{
    private readonly TempDirectory directory = new();
    private readonly CanonStoreRepository store;

    /// <summary>Creates a repository over a fresh canonical store.</summary>
    public SettingsRepositoryTests()
    {
        store = new CanonStoreRepository(Path.Combine(directory.Path, "canon.db"), new SqliteCircuitBreaker());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        directory.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void ReadSettings_FallsBackToDefaultsWhenNothingIsStored()
    {
        SettingsRepository repository = new(store);

        SettingsView settings = repository.ReadSettings();

        Assert.Equal(
            new SettingsView(false, false, SettingsRepository.DefaultRefreshCadenceMinutes, false, SettingsRepository.DefaultDashboardPort),
            settings);
    }

    [Fact]
    public void ReadSettings_FailsClosedOnUnrecognizedValues()
    {
        store.SetMetadata(SettingsRepository.JobsPausedKey, "yes please");
        store.SetMetadata(SettingsRepository.RefreshCadenceMinutesKey, "0");
        store.SetMetadata(SettingsRepository.DashboardPortKey, "65536");
        store.SetMetadata(SettingsRepository.ReceiverHostedKey, "ON");

        SettingsView settings = new SettingsRepository(store).ReadSettings();

        Assert.False(settings.JobsPaused);
        Assert.False(settings.ReceiverHosted);
        Assert.Equal(SettingsRepository.DefaultRefreshCadenceMinutes, settings.RefreshCadenceMinutes);
        Assert.Equal(SettingsRepository.DefaultDashboardPort, settings.DashboardPort);
    }

    [Fact]
    public void ReadSettings_RejectsNonDigitAndOutOfRangeIntegers()
    {
        store.SetMetadata(SettingsRepository.RefreshCadenceMinutesKey, "1e3");
        store.SetMetadata(SettingsRepository.DashboardPortKey, "-1");

        SettingsView settings = new SettingsRepository(store).ReadSettings();

        Assert.Equal(SettingsRepository.DefaultRefreshCadenceMinutes, settings.RefreshCadenceMinutes);
        Assert.Equal(SettingsRepository.DefaultDashboardPort, settings.DashboardPort);
    }

    [Fact]
    public void WriteBooleanSetting_PersistsOnOffAndIsReadBack()
    {
        SettingsRepository repository = new(store);

        repository.WriteBooleanSetting(SettingsRepository.FolderImportScheduledKey, true);
        repository.WriteBooleanSetting(SettingsRepository.JobsPausedKey, true);

        Assert.Equal("on", store.GetMetadata(SettingsRepository.FolderImportScheduledKey));
        Assert.Equal("on", store.GetMetadata(SettingsRepository.JobsPausedKey));

        SettingsView settings = repository.ReadSettings();
        Assert.True(settings.FolderImportScheduled);
        Assert.True(settings.JobsPaused);
    }

    [Fact]
    public void WriteBooleanSetting_RejectsUnknownKeys()
    {
        SettingsRepository repository = new(store);

        Assert.Throws<ArgumentException>(() => repository.WriteBooleanSetting("settings.unknown", true));
        Assert.Throws<ArgumentNullException>(() => repository.WriteBooleanSetting(null!, true));
    }

    [Fact]
    public void WriteIntSetting_PersistsInRangeValues()
    {
        SettingsRepository repository = new(store);

        repository.WriteIntSetting(SettingsRepository.RefreshCadenceMinutesKey, SettingsRepository.CadenceMaxMinutes);
        repository.WriteIntSetting(SettingsRepository.DashboardPortKey, SettingsRepository.PortMax);

        SettingsView settings = repository.ReadSettings();
        Assert.Equal(SettingsRepository.CadenceMaxMinutes, settings.RefreshCadenceMinutes);
        Assert.Equal(SettingsRepository.PortMax, settings.DashboardPort);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1441)]
    public void WriteIntSetting_RejectsOutOfRangeCadence(int value)
    {
        SettingsRepository repository = new(store);

        Assert.Throws<ArgumentOutOfRangeException>(() => repository.WriteIntSetting(SettingsRepository.RefreshCadenceMinutesKey, value));
        Assert.Null(store.GetMetadata(SettingsRepository.RefreshCadenceMinutesKey));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(65536)]
    public void WriteIntSetting_RejectsOutOfRangePort(int value)
    {
        SettingsRepository repository = new(store);

        Assert.Throws<ArgumentOutOfRangeException>(() => repository.WriteIntSetting(SettingsRepository.DashboardPortKey, value));
        Assert.Null(store.GetMetadata(SettingsRepository.DashboardPortKey));
    }

    [Fact]
    public void WriteIntSetting_RejectsBooleanKeys()
    {
        SettingsRepository repository = new(store);

        Assert.Throws<ArgumentException>(() => repository.WriteIntSetting(SettingsRepository.JobsPausedKey, 1));
    }

    [Fact]
    public void FactoryContentConsent_DefaultsToDisabledAndRoundTrips()
    {
        SettingsRepository repository = new(store);

        Assert.False(repository.ReadFactoryContentConsent(directory.Path));

        repository.WriteFactoryContentConsent(directory.Path, true);
        Assert.True(repository.ReadFactoryContentConsent(directory.Path));

        repository.WriteFactoryContentConsent(directory.Path, false);
        Assert.False(repository.ReadFactoryContentConsent(directory.Path));
        Assert.True(File.Exists(directory.Combine(SettingsRepository.FactoryPluginFile)));
    }

    [Fact]
    public void FactoryContentConsent_FailsClosedOnMalformedFile()
    {
        SettingsRepository repository = new(store);
        File.WriteAllText(directory.Combine(SettingsRepository.FactoryPluginFile), "{ not json");

        Assert.False(repository.ReadFactoryContentConsent(directory.Path));
    }

    [Fact]
    public void FactoryContentConsent_FailsClosedWhenFlagIsNotBooleanTrue()
    {
        SettingsRepository repository = new(store);
        File.WriteAllText(directory.Combine(SettingsRepository.FactoryPluginFile), "{\"contentCapture\":\"true\"}");

        Assert.False(repository.ReadFactoryContentConsent(directory.Path));
    }

    [Fact]
    public void FactoryContentConsent_CreatesMissingStateDirectory()
    {
        SettingsRepository repository = new(store);
        string nested = directory.Combine("nested/state");

        repository.WriteFactoryContentConsent(nested, true);

        Assert.True(repository.ReadFactoryContentConsent(nested));
    }

    [Fact]
    public void AppendAntigravityStatusLineEvent_WritesOneJsonLinePerCall()
    {
        SettingsRepository repository = new(store);

        repository.AppendAntigravityStatusLineEvent(directory.Path, JsonSerializer.SerializeToElement(new { turn = 1 }));
        repository.AppendAntigravityStatusLineEvent(directory.Path, JsonSerializer.SerializeToElement(new { turn = 2 }));

        string path = directory.Combine(SettingsRepository.AntigravityStatusLineRelativePath);
        string[] lines = File.ReadAllLines(path);
        Assert.Equal(2, lines.Length);
        Assert.Equal(1, JsonDocument.Parse(lines[0]).RootElement.GetProperty("turn").GetInt32());
        Assert.Equal(2, JsonDocument.Parse(lines[1]).RootElement.GetProperty("turn").GetInt32());
    }

    [Fact]
    public void AppendAntigravityStatusLineEvent_WritesEmptyObjectForUndefinedPayload()
    {
        SettingsRepository repository = new(store);

        repository.AppendAntigravityStatusLineEvent(directory.Path, default);

        Assert.Equal("{}", Assert.Single(File.ReadAllLines(directory.Combine(SettingsRepository.AntigravityStatusLineRelativePath))));
    }
}

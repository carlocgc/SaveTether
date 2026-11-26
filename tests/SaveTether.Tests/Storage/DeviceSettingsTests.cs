using SaveTether.Core.Backup;
using SaveTether.Core.Storage;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Storage;

/// <summary>Section 11: this device's settings, in one file in the local folder.</summary>
public class DeviceSettingsTests
{
    [Fact]
    public void Every_setting_has_its_safe_default_until_set_and_is_remembered()
    {
        using var sandbox = new Sandbox();
        var local = sandbox.At("local");

        var fresh = new DeviceSettings(local);
        Assert.True(fresh.Notifications);
        Assert.True(fresh.BackupBeforeActions);
        Assert.Equal(BackupStore.DefaultKeep, fresh.MaxBackupsPerFolder);

        Assert.Null(new DeviceSettings(local).SetNotifications(false));
        Assert.Null(new DeviceSettings(local).SetBackupBeforeActions(false));
        Assert.Null(new DeviceSettings(local).SetMaxBackupsPerFolder(0));

        var read = new DeviceSettings(local);
        Assert.False(read.Notifications);
        Assert.False(read.BackupBeforeActions);
        Assert.Equal(0, read.MaxBackupsPerFolder);

        Assert.Null(read.SetNotifications(true));
        Assert.False(new DeviceSettings(local).BackupBeforeActions); // setting one keeps the others
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(21)]
    public void A_max_out_of_range_is_refused_and_nothing_is_written(int count)
    {
        using var sandbox = new Sandbox();
        var local = sandbox.At("local");

        Assert.NotNull(new DeviceSettings(local).SetMaxBackupsPerFolder(count));
        Assert.False(File.Exists(Path.Combine(local, DeviceSettings.FileName)));
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{\"backup_before_actions\": \"maybe\"}")]
    [InlineData("{\"max_backups_per_folder\": 99}")]
    public void A_file_that_cannot_be_read_gives_the_defaults(string content)
    {
        using var sandbox = new Sandbox();
        var local = sandbox.At("local");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, DeviceSettings.FileName), content);

        var settings = new DeviceSettings(local);
        Assert.True(settings.BackupBeforeActions);
        Assert.Equal(BackupStore.DefaultKeep, settings.MaxBackupsPerFolder);
    }

    [Fact]
    public void Unknown_settings_from_a_newer_version_are_ignored()
    {
        using var sandbox = new Sandbox();
        var local = sandbox.At("local");
        Directory.CreateDirectory(local);
        File.WriteAllText(Path.Combine(local, DeviceSettings.FileName), "{\"backup_before_actions\": false, \"later\": 1}");

        Assert.False(new DeviceSettings(local).BackupBeforeActions);
    }

    [Fact]
    public void The_old_notifications_file_is_read_and_moved_into_the_settings_file_on_the_next_write()
    {
        using var sandbox = new Sandbox();
        var local = sandbox.At("local");
        Directory.CreateDirectory(local);
        var legacy = Path.Combine(local, DeviceSettings.LegacyNotificationsFileName);
        File.WriteAllText(legacy, "off\n");

        Assert.False(new DeviceSettings(local).Notifications);

        Assert.Null(new DeviceSettings(local).SetMaxBackupsPerFolder(3));
        Assert.False(File.Exists(legacy));
        Assert.False(new DeviceSettings(local).Notifications);
        Assert.Equal(3, new DeviceSettings(local).MaxBackupsPerFolder);
    }
}

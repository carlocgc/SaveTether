using SaveTether.Gui.Presentation;
using SaveTether.Tests.FileSystem;

namespace SaveTether.Tests.Gui;

/// <summary>Section 11: the "Desktop notifications" switch.</summary>
public class NotificationSettingTests
{
    [Fact]
    public void On_until_turned_off_and_remembered_in_the_local_folder()
    {
        using var sandbox = new Sandbox();
        var local = sandbox.At("local");

        Assert.True(new NotificationSetting(local).IsOn);
        Assert.Null(new NotificationSetting(local).Set(on: false));
        Assert.False(new NotificationSetting(local).IsOn);
        Assert.Null(new NotificationSetting(local).Set(on: true));
        Assert.True(new NotificationSetting(local).IsOn);
    }
}

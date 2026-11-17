using AudioCtrl.Helpers;

namespace AudioCtrl.Tests;

public class VersionInfoTests
{
    [Fact]
    public void CurrentVersion_IsDerivedFromAssemblyVersion()
    {
        var assemblyVersion = typeof(VersionInfo).Assembly.GetName().Version;
        Assert.NotNull(assemblyVersion);
        Assert.Equal(assemblyVersion!.ToString(3), VersionInfo.CurrentVersion);
    }
}

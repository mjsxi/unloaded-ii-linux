using ReloadedDropIn.Bootstrap.Proton;

namespace ReloadedDropIn.Tests;

public class WineDllOverridesTests
{
    [Fact]
    public void EmptyExistingYieldsJustTheRequiredOverride()
    {
        Assert.Equal("winmm=n,b", WineDllOverrides.Merge(null, "winmm=n,b"));
        Assert.Equal("winmm=n,b", WineDllOverrides.Merge("", "winmm=n,b"));
    }

    [Fact]
    public void ExistingOverridesArePreserved()
    {
        Assert.Equal("dxgi=n;winmm=n,b", WineDllOverrides.Merge("dxgi=n", "winmm=n,b"));
    }

    [Fact]
    public void ConflictingEntryForSameDllIsReplaced()
    {
        Assert.Equal("dxgi=n;winmm=n,b", WineDllOverrides.Merge("winmm=b;dxgi=n", "winmm=n,b"));
    }

    [Fact]
    public void MergeIsIdempotent()
    {
        var once = WineDllOverrides.Merge("dxgi=n", "winmm=n,b");
        Assert.Equal(once, WineDllOverrides.Merge(once, "winmm=n,b"));
    }

    [Fact]
    public void MalformedRequiredOverrideThrows()
    {
        Assert.Throws<ArgumentException>(() => WineDllOverrides.Merge(null, "winmm"));
        Assert.Throws<ArgumentException>(() => WineDllOverrides.Merge(null, "=n,b"));
    }
}

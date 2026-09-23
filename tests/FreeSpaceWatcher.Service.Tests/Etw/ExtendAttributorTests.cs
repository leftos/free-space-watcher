using FreeSpaceWatcher.Service.Etw;

namespace FreeSpaceWatcher.Service.Tests.Etw;

public sealed class ExtendAttributorTests
{
    private const int SystemPid = ExtendAttributor.SystemProcessId;
    private const int Writer = 1234;
    private const int Other = 5678;
    private const string FilePath = @"C:\data\growing.bin";

    [Fact]
    public void SystemExtend_AfterUserWrite_IsCreditedToWriter()
    {
        ExtendAttributor attributor = new(ExtendAttributor.DefaultCapacity);
        attributor.Wrote(Writer, FilePath);

        Assert.Equal(Writer, attributor.CreditExtend(SystemPid, FilePath));
    }

    [Fact]
    public void SystemExtend_PathMatchIgnoresCase()
    {
        ExtendAttributor attributor = new(ExtendAttributor.DefaultCapacity);
        attributor.Wrote(Writer, FilePath);

        Assert.Equal(Writer, attributor.CreditExtend(SystemPid, FilePath.ToUpperInvariant()));
    }

    [Fact]
    public void SystemExtend_CreditsMostRecentWriter()
    {
        ExtendAttributor attributor = new(ExtendAttributor.DefaultCapacity);
        attributor.Wrote(Writer, FilePath);
        attributor.Wrote(Other, FilePath);

        Assert.Equal(Other, attributor.CreditExtend(SystemPid, FilePath));
    }

    [Fact]
    public void SystemExtend_WithNoKnownWriter_StaysWithSystem()
    {
        ExtendAttributor attributor = new(ExtendAttributor.DefaultCapacity);
        attributor.Wrote(Writer, @"C:\data\other.bin");

        Assert.Equal(SystemPid, attributor.CreditExtend(SystemPid, FilePath));
    }

    [Fact]
    public void NonSystemExtend_IsUnchanged()
    {
        ExtendAttributor attributor = new(ExtendAttributor.DefaultCapacity);
        attributor.Wrote(Writer, FilePath);

        Assert.Equal(Other, attributor.CreditExtend(Other, FilePath));
    }

    [Fact]
    public void SystemExtend_AfterWriterEnded_FallsBackToSystem()
    {
        ExtendAttributor attributor = new(ExtendAttributor.DefaultCapacity);
        attributor.Wrote(Writer, FilePath);
        attributor.Wrote(Other, @"C:\data\other.bin");

        attributor.ProcessEnded(Writer);

        Assert.Equal(SystemPid, attributor.CreditExtend(SystemPid, FilePath));
        Assert.Equal(Other, attributor.CreditExtend(SystemPid, @"C:\data\other.bin"));
        Assert.Equal(1, attributor.Count);
    }

    [Fact]
    public void Wrote_AtCapacity_EvictsLeastRecentlyWrittenPath()
    {
        ExtendAttributor attributor = new(2);
        attributor.Wrote(Writer, @"C:\a.bin");
        attributor.Wrote(Writer, @"C:\b.bin");
        attributor.Wrote(Writer, @"C:\a.bin");

        attributor.Wrote(Other, @"C:\c.bin");

        Assert.Equal(2, attributor.Count);
        Assert.Equal(Writer, attributor.CreditExtend(SystemPid, @"C:\a.bin"));
        Assert.Equal(SystemPid, attributor.CreditExtend(SystemPid, @"C:\b.bin"));
        Assert.Equal(Other, attributor.CreditExtend(SystemPid, @"C:\c.bin"));
    }

    [Fact]
    public void Constructor_ZeroCapacity_Throws() => Assert.Throws<ArgumentOutOfRangeException>(() => new ExtendAttributor(0));
}

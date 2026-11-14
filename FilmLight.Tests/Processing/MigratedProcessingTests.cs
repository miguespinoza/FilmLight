using Xunit;

namespace FilmLight.Tests.Processing;

public sealed class MigratedProcessingTests
{
    public static IEnumerable<object[]> Cases =>
        CoreTests.GetTests().Select(test => new object[] { test.Name, test.Test })
            .Concat(IterationTwoTests.All.Select(test => new object[] { test.Name, test.Body }))
            .Concat(RgbCaptureTests.All.Select(test => new object[] { test.Name, test.Body }))
            .Concat(AdapterTests.All.Select(test => new object[] { test.Name, test.Body }));

    [Theory]
    [MemberData(nameof(Cases), DisableDiscoveryEnumeration = true)]
    public async Task ProcessingBehavior(string name, Func<ValueTask> test)
    {
        Assert.False(string.IsNullOrWhiteSpace(name));
        await test();
    }
}

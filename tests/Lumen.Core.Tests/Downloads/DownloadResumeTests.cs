using FluentAssertions;
using Lumen.Core.Downloads;

namespace Lumen.Core.Tests.Downloads;

public sealed class DownloadResumeTests
{
    [Fact]
    public void FreshDownload_WritesFromStart_WithContentLengthAsTotal()
    {
        var plan = DownloadResume.Decide(existingBytes: 0, statusCode: 200, contentLength: 1000);

        plan.Mode.Should().Be(ResumeMode.FromStart);
        plan.TotalBytes.Should().Be(1000);
    }

    [Fact]
    public void PartialWith206_Appends_AndTotalIsExistingPlusRemaining()
    {
        // 206 Content-Length is the remaining length, so total = 600 (existing) + 400.
        var plan = DownloadResume.Decide(existingBytes: 600, statusCode: 206, contentLength: 400);

        plan.Mode.Should().Be(ResumeMode.Append);
        plan.TotalBytes.Should().Be(1000);
    }

    [Fact]
    public void PartialWith200_ServerIgnoredRange_RestartsFromStart()
    {
        // The server sent the whole body despite our Range header — appending would corrupt.
        var plan = DownloadResume.Decide(existingBytes: 600, statusCode: 200, contentLength: 1000);

        plan.Mode.Should().Be(ResumeMode.FromStart);
        plan.TotalBytes.Should().Be(1000);
    }

    [Fact]
    public void RangeNotSatisfiable_RestartsFromStart_WithUnknownTotal()
    {
        var plan = DownloadResume.Decide(existingBytes: 600, statusCode: 416, contentLength: null);

        plan.Mode.Should().Be(ResumeMode.FromStart);
        plan.TotalBytes.Should().BeNull();
    }

    [Fact]
    public void Partial206_WithoutContentLength_AppendsWithUnknownTotal()
    {
        var plan = DownloadResume.Decide(existingBytes: 600, statusCode: 206, contentLength: null);

        plan.Mode.Should().Be(ResumeMode.Append);
        plan.TotalBytes.Should().BeNull();
    }

    [Fact]
    public void FreshDownload_WithoutContentLength_IndeterminateTotal()
    {
        var plan = DownloadResume.Decide(existingBytes: 0, statusCode: 200, contentLength: null);

        plan.Mode.Should().Be(ResumeMode.FromStart);
        plan.TotalBytes.Should().BeNull();
    }
}

using System.Text;
using FluentAssertions;
using Lumen.Providers.Xmltv;

namespace Lumen.Providers.Tests.Xmltv;

public sealed class PrefixedStreamTests
{
    [Fact]
    public void ReplaysPrefixBeforeInnerStream()
    {
        var inner = new MemoryStream(Encoding.ASCII.GetBytes("world"));
        using var stream = new PrefixedStream(Encoding.ASCII.GetBytes("hello "), inner);

        using var reader = new StreamReader(stream, Encoding.ASCII);
        reader.ReadToEnd().Should().Be("hello world");
    }

    [Fact]
    public async Task AsyncReads_SpanPrefixAndInner()
    {
        var inner = new MemoryStream(Encoding.ASCII.GetBytes("data"));
        using var stream = new PrefixedStream(Encoding.ASCII.GetBytes("ab"), inner);

        var buffer = new byte[1];
        (await stream.ReadAsync(buffer.AsMemory())).Should().Be(1);
        buffer[0].Should().Be((byte)'a');

        var rest = new byte[16];
        var total = 0;
        int read;
        while ((read = await stream.ReadAsync(rest.AsMemory(total))) > 0)
        {
            total += read;
        }

        Encoding.ASCII.GetString(rest, 0, total).Should().Be("bdata");
    }

    [Fact]
    public void ForwardOnlySemantics()
    {
        using var stream = new PrefixedStream([1], new MemoryStream([2]));

        stream.CanRead.Should().BeTrue();
        stream.CanSeek.Should().BeFalse();
        stream.CanWrite.Should().BeFalse();
        stream.Flush(); // no-op

        FluentActions.Invoking(() => stream.Length).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => stream.Position).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => stream.Position = 1).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => stream.Seek(0, SeekOrigin.Begin)).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => stream.SetLength(1)).Should().Throw<NotSupportedException>();
        FluentActions.Invoking(() => stream.Write([1], 0, 1)).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void ArrayOverload_ReadsCorrectly()
    {
        var inner = new MemoryStream(Encoding.ASCII.GetBytes("yz"));
        using var stream = new PrefixedStream(Encoding.ASCII.GetBytes("x"), inner);

        var buffer = new byte[8];
        var first = stream.Read(buffer, 0, 8);
        var second = stream.Read(buffer, first, 8 - first);

        Encoding.ASCII.GetString(buffer, 0, first + second).Should().Be("xyz");
    }
}

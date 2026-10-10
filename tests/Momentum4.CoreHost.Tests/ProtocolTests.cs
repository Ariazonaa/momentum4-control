using System.Text;
using System.Text.Json;
using Momentum4.CoreHost;

namespace Momentum4.CoreHost.Tests;

public sealed class ProtocolTests
{
    [Theory]
    [InlineData("{\"v\":1,\"kind\":\"request\",\"id\":1,\"op\":\"snapshot\",\"args\":{}}")]
    [InlineData("{\"v\":1,\"kind\":\"request\",\"id\":2,\"op\":\"rename\",\"args\":{\"name\":\"A\"}}")]
    [InlineData("{\"args\":{},\"id\":1,\"op\":\"snapshot\",\"kind\":\"request\",\"v\":1}")]
    public void AcceptsTypedRequest(string input)
    {
        using var document = PrivateProtocol.Parse(Encoding.UTF8.GetBytes(input));
        var request = PrivateProtocol.Request(document.RootElement);
        Assert.True(request.Id > 0);
        Assert.Equal(JsonValueKind.Object, request.Arguments.ValueKind);
    }

    [Theory]
    [InlineData("{\"v\":1,\"kind\":\"request\",\"id\":true,\"op\":\"snapshot\",\"args\":{}}")]
    [InlineData("{\"v\":1,\"kind\":\"request\",\"id\":0,\"op\":\"snapshot\",\"args\":{}}")]
    [InlineData("{\"v\":1,\"kind\":\"request\",\"id\":1,\"op\":\"snapshot\",\"args\":{},\"extra\":1}")]
    [InlineData("{\"v\":1,\"kind\":\"request\",\"id\":1,\"op\":\"snapshot\",\"args\":{\"x\":1,\"x\":2}}")]
    public void RejectsWrongFieldsAndTypes(string input)
    {
        Assert.Throws<PrivateProtocolException>(() =>
        {
            using var document = PrivateProtocol.Parse(Encoding.UTF8.GetBytes(input));
            PrivateProtocol.Request(document.RootElement);
        });
    }

    [Fact]
    public async Task RejectsOversizeBeforeParsing()
    {
        using var input = new MemoryStream(Encoding.UTF8.GetBytes(new string('a', 65537) + "\n"));
        await Assert.ThrowsAsync<PrivateProtocolException>(async () => await PrivateProtocol.ReadLineAsync(input, CancellationToken.None));
    }

    [Fact]
    public async Task FragmentedInputPreservesRecord()
    {
        using var input = new FragmentStream(Encoding.UTF8.GetBytes("{\"v\":1}\n"));
        Assert.Equal("{\"v\":1}", Encoding.UTF8.GetString((await PrivateProtocol.ReadLineAsync(input, CancellationToken.None))!));
    }

    [Fact]
    public void RejectsDeepInput()
    {
        var input = Encoding.UTF8.GetBytes(new string('[', 17) + "0" + new string(']', 17));
        Assert.Throws<PrivateProtocolException>(() => PrivateProtocol.Parse(input));
    }

    private sealed class FragmentStream(byte[] data) : MemoryStream(data)
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(1, buffer.Length)], cancellationToken);
    }
}

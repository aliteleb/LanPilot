using LanPilot.Contracts;

namespace LanPilot.Tests;

public sealed class PipeProtocolTests
{
    [Fact]
    public async Task RoundTrip_PreservesEnvelopeAndPayload()
    {
        PipeEnvelope sent = PipeProtocol.Request(PipeCommands.ControlSet, new ControlRequest(true));
        await using MemoryStream stream = new();

        await PipeProtocol.WriteAsync(stream, sent, CancellationToken.None);
        stream.Position = 0;
        PipeEnvelope received = Assert.IsType<PipeEnvelope>(
            await PipeProtocol.ReadAsync(stream, CancellationToken.None));

        Assert.Equal(sent.RequestId, received.RequestId);
        Assert.True(received.ReadPayload<ControlRequest>().Enabled);
    }

    [Fact]
    public async Task Read_RejectsOversizedFrame()
    {
        await using MemoryStream stream = new(BitConverter.GetBytes(PipeProtocol.MaxMessageBytes + 1));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await PipeProtocol.ReadAsync(stream, CancellationToken.None));
    }
}

using System.Buffers.Binary;
using NUnit.Framework;

namespace Coflnet.DiscordBot.Phone;

public class PcmuCodecTests
{
    [Test]
    public void SilenceRoundTrips()
    {
        Assert.That(PcmuCodec.Decode(PcmuCodec.Encode(0)), Is.InRange(-8, 8));
    }

    [TestCase(-30000)]
    [TestCase(-1000)]
    [TestCase(1000)]
    [TestCase(30000)]
    public void EncodingPreservesSignAndApproximateAmplitude(short sample)
    {
        var decoded = PcmuCodec.Decode(PcmuCodec.Encode(sample));
        Assert.That(Math.Sign(decoded), Is.EqualTo(Math.Sign(sample)));
        Assert.That(Math.Abs(decoded - sample), Is.LessThan(Math.Abs(sample) / 10 + 300));
    }

    [Test]
    public void TwilioFrameBecomesDiscordFortyEightKhzStereo()
    {
        var output = PcmuCodec.DecodeToDiscordPcm(new byte[160]);
        Assert.That(output, Has.Length.EqualTo(3840));
        Assert.That(
            BinaryPrimitives.ReadInt16LittleEndian(output),
            Is.EqualTo(BinaryPrimitives.ReadInt16LittleEndian(output.AsSpan(2))));
    }

    [Test]
    public void DiscordFrameDownsamplesToTwilioFrame()
    {
        var output = PcmuCodec.DownsampleDiscordPcm(new byte[3840]);
        Assert.That(output, Has.Length.EqualTo(160));
    }
}

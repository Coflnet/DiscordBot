using System.Text;
using NUnit.Framework;

namespace Coflnet.DiscordBot.Phone;

public class WavePcmReaderTests
{
    [Test]
    public void ReadsRequiredDiscordPcmFormat()
    {
        var pcm = new byte[3840];
        var wave = CreateWave(channels: 2, sampleRate: 48000, bitsPerSample: 16, pcm);

        Assert.That(WavePcmReader.Read48KhzStereo16Bit(wave), Is.EqualTo(pcm));
    }

    [TestCase(1, 48000, 16)]
    [TestCase(2, 44100, 16)]
    [TestCase(2, 48000, 24)]
    public void RejectsUnsupportedDiscordAudio(int channels, int sampleRate, int bitsPerSample)
    {
        var wave = CreateWave(channels, sampleRate, bitsPerSample, new byte[3840]);
        Assert.Throws<InvalidDataException>(() => WavePcmReader.Read48KhzStereo16Bit(wave));
    }

    [Test]
    public void RejectsLongDisclosure()
    {
        var pcm = new byte[48000 * 2 * sizeof(short) * 15 + 4];
        var wave = CreateWave(2, 48000, 16, pcm);
        Assert.Throws<InvalidDataException>(() => WavePcmReader.Read48KhzStereo16Bit(wave));
    }

    private static byte[] CreateWave(int channels, int sampleRate, int bitsPerSample, byte[] pcm)
    {
        using var output = new MemoryStream();
        using var writer = new BinaryWriter(output, Encoding.ASCII, leaveOpen: true);
        writer.Write("RIFF"u8);
        writer.Write(36 + pcm.Length);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * bitsPerSample / 8);
        writer.Write((short)(channels * bitsPerSample / 8));
        writer.Write((short)bitsPerSample);
        writer.Write("data"u8);
        writer.Write(pcm.Length);
        writer.Write(pcm);
        return output.ToArray();
    }
}

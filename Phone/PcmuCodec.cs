using System.Buffers.Binary;

namespace Coflnet.DiscordBot.Phone;

public static class PcmuCodec
{
    private const int Bias = 0x84;
    private const int Clip = 32635;

    public static byte[] DecodeToDiscordPcm(ReadOnlySpan<byte> pcmu)
    {
        var output = new byte[pcmu.Length * 6 * 2 * sizeof(short)];
        for (var sourceIndex = 0; sourceIndex < pcmu.Length; sourceIndex++)
        {
            var current = Decode(pcmu[sourceIndex]);
            var next = sourceIndex + 1 < pcmu.Length ? Decode(pcmu[sourceIndex + 1]) : current;
            for (var phase = 0; phase < 6; phase++)
            {
                var sample = (short)(current + ((next - current) * phase / 6));
                var outputIndex = (sourceIndex * 6 + phase) * 4;
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(outputIndex), sample);
                BinaryPrimitives.WriteInt16LittleEndian(output.AsSpan(outputIndex + 2), sample);
            }
        }
        return output;
    }

    public static short[] DownsampleDiscordPcm(ReadOnlySpan<byte> pcm)
    {
        var output = new short[pcm.Length / 24];
        for (var outputIndex = 0; outputIndex < output.Length; outputIndex++)
        {
            var sum = 0;
            for (var sourceFrame = 0; sourceFrame < 6; sourceFrame++)
            {
                var sourceIndex = (outputIndex * 6 + sourceFrame) * 4;
                var left = BinaryPrimitives.ReadInt16LittleEndian(pcm[sourceIndex..]);
                var right = BinaryPrimitives.ReadInt16LittleEndian(pcm[(sourceIndex + 2)..]);
                sum += (left + right) / 2;
            }
            output[outputIndex] = (short)(sum / 6);
        }
        return output;
    }

    public static byte[] Encode(ReadOnlySpan<short> pcm)
    {
        var output = new byte[pcm.Length];
        for (var i = 0; i < pcm.Length; i++)
            output[i] = Encode(pcm[i]);
        return output;
    }

    public static short Decode(byte value)
    {
        value = (byte)~value;
        var sample = ((value & 0x0f) << 3) + Bias;
        sample <<= (value & 0x70) >> 4;
        return (short)((value & 0x80) == 0 ? sample - Bias : Bias - sample);
    }

    public static byte Encode(short value)
    {
        var sample = (int)value;
        var mask = sample < 0 ? 0x7f : 0xff;
        sample = Math.Min(Math.Abs(sample), Clip) + Bias;

        var segment = 7;
        for (var threshold = 0x4000; segment > 0 && (sample & threshold) == 0; threshold >>= 1)
            segment--;

        var encoded = (segment << 4) | ((sample >> (segment + 3)) & 0x0f);
        return (byte)(encoded ^ mask);
    }
}

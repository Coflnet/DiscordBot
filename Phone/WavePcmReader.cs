using System.Buffers.Binary;
using System.Text;

namespace Coflnet.DiscordBot.Phone;

public static class WavePcmReader
{
    private const int MaximumPcmBytes = 48000 * 2 * sizeof(short) * 15;

    public static byte[] Read48KhzStereo16Bit(ReadOnlySpan<byte> wave)
    {
        if (wave.Length < 12
            || Encoding.ASCII.GetString(wave[..4]) != "RIFF"
            || Encoding.ASCII.GetString(wave[8..12]) != "WAVE")
            throw new InvalidDataException("Discord notice must be a RIFF/WAVE file");

        var validFormat = false;
        byte[]? pcm = null;
        for (var offset = 12; offset + 8 <= wave.Length;)
        {
            var chunkName = Encoding.ASCII.GetString(wave.Slice(offset, 4));
            var chunkLength = BinaryPrimitives.ReadUInt32LittleEndian(wave.Slice(offset + 4, 4));
            var dataOffset = offset + 8;
            if (chunkLength > int.MaxValue || dataOffset + (long)chunkLength > wave.Length)
                throw new InvalidDataException("Discord notice contains a truncated WAVE chunk");

            var chunk = wave.Slice(dataOffset, (int)chunkLength);
            if (chunkName == "fmt ")
            {
                validFormat = chunk.Length >= 16
                    && BinaryPrimitives.ReadUInt16LittleEndian(chunk) == 1
                    && BinaryPrimitives.ReadUInt16LittleEndian(chunk[2..]) == 2
                    && BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..]) == 48000
                    && BinaryPrimitives.ReadUInt16LittleEndian(chunk[14..]) == 16;
            }
            else if (chunkName == "data")
            {
                pcm = chunk.ToArray();
            }

            offset = dataOffset + (int)chunkLength + ((int)chunkLength & 1);
        }

        if (!validFormat || pcm is not { Length: > 0 } || pcm.Length % 4 != 0)
            throw new InvalidDataException("Discord notice must be 48 kHz, stereo, signed 16-bit PCM");
        if (pcm.Length > MaximumPcmBytes)
            throw new InvalidDataException("Discord notice must not exceed 15 seconds");
        return pcm;
    }
}

using System;

namespace LimitlessSquareEngine.Engine
{
    internal static class WavDecoder
    {
        public struct WavData
        {
            public int SampleRate;
            public int Channels;
            public int BitsPerSample;
            public int TotalSamples;
            public byte[] PcmBytes;
        }

        public static WavData Decode(byte[] fileBytes)
        {
            if (fileBytes.Length < 44)
                throw new InvalidOperationException("[X] WAV file too small for valid header.");

            ReadOnlySpan<byte> riff = fileBytes.AsSpan(0, 4);
            if (riff[0] != 'R' || riff[1] != 'I' || riff[2] != 'F' || riff[3] != 'F')
                throw new InvalidOperationException("[X] Not a valid RIFF/WAV file.");

            ReadOnlySpan<byte> wave = fileBytes.AsSpan(8, 4);
            if (wave[0] != 'W' || wave[1] != 'A' || wave[2] != 'V' || wave[3] != 'E')
                throw new InvalidOperationException("[X] Not a valid WAVE file.");

            int fmtOffset = FindChunkOffset(fileBytes, 12, "fmt ");
            if (fmtOffset < 0)
                throw new InvalidOperationException("[X] WAV fmt chunk not found.");

            ushort audioFormat = BitConverter.ToUInt16(fileBytes, fmtOffset + 8);
            if (audioFormat != 1 && audioFormat != 3)
                throw new InvalidOperationException($"[X] Unsupported WAV format: {audioFormat} (only PCM=1 and IEEE float=3 are supported).");

            ushort channels = BitConverter.ToUInt16(fileBytes, fmtOffset + 10);
            int sampleRate = BitConverter.ToInt32(fileBytes, fmtOffset + 12);
            ushort bitsPerSample = BitConverter.ToUInt16(fileBytes, fmtOffset + 22);

            if (channels < 1 || channels > 2)
                throw new InvalidOperationException($"[X] Unsupported channel count: {channels}.");

            int dataOffset = FindChunkOffset(fileBytes, 12, "data");
            if (dataOffset < 0)
                throw new InvalidOperationException("[X] WAV data chunk not found.");

            int dataSize = BitConverter.ToInt32(fileBytes, dataOffset + 4);
            int pcmStart = dataOffset + 8;
            int pcmEnd = Math.Min(pcmStart + dataSize, fileBytes.Length);
            int pcmLength = pcmEnd - pcmStart;

            if (pcmLength <= 0)
                throw new InvalidOperationException("[X] WAV contains no PCM data.");

            byte[] pcmData = new byte[pcmLength];
            Array.Copy(fileBytes, pcmStart, pcmData, 0, pcmLength);

            int bytesPerSample = bitsPerSample / 8;
            int totalSamples = pcmLength / (channels * bytesPerSample);

            if (audioFormat == 3)
            {
                bitsPerSample = 32;
                bytesPerSample = 4;
                totalSamples = pcmLength / (channels * bytesPerSample);
            }

            return new WavData
            {
                SampleRate = sampleRate,
                Channels = channels,
                BitsPerSample = bitsPerSample,
                TotalSamples = totalSamples,
                PcmBytes = pcmData
            };
        }

        private static int FindChunkOffset(byte[] data, int startOffset, string chunkId)
        {
            int offset = startOffset;
            while (offset + 8 <= data.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(data, offset, 4);
                int size = BitConverter.ToInt32(data, offset + 4);
                if (id == chunkId)
                    return offset;
                offset += 8 + size;
            }
            return -1;
        }
    }
}

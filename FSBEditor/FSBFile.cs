using Syroot.BinaryData;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace FSBEditor
{
    class FSBFile
    {
        public string magic = "FSB4", xmaMagic = "RIFF";
        public int audioStartOffset, sampleHeaderSize, numSounds;
        byte[] dataHeader = new byte[] { 0x64, 0x61, 0x74, 0x61 }, headerHash;
        public List<FSBEntry> fsbEntries;
        public List<string> entryFields;

        public FSBFile()
        {
            fsbEntries = new List<FSBEntry>();
            entryFields = new List<string>();

            FSBEntry disposableEntry = new FSBEntry();
            foreach (var item in disposableEntry.GetType().GetFields().Where(x => !x.Name.Contains("Data")).ToList())
            {
                entryFields.Add(item.Name);
            }
        }

        public void ReadFile(string path)
        {
            var bytes = File.ReadAllBytes(path);

            using (var stream = new BinaryStream(new MemoryStream(bytes)))
            {
                if (stream.ReadString(4) != magic)
                    throw new InvalidDataException("Not an FSB4 file. Please open an FSB4 file and try again.");

                numSounds = stream.ReadInt32();
                sampleHeaderSize = stream.ReadInt32();

                stream.Position = 0x1C;
                headerHash = stream.ReadBytes(24);

                stream.Position = 0x30;

                fsbEntries.Clear();

                for (int i = 0; i < numSounds; i++)
                {
                    FSBEntry entry = new FSBEntry();

                    entry.size = stream.ReadInt16();

                    entry.name = stream.ReadString(30).Replace("\0", "");
                    entry.numSamples = stream.ReadInt32();
                    entry.streamSize = stream.ReadInt32();
                    entry.loopStartSample = stream.ReadInt32();
                    entry.loopEndSample = stream.ReadInt32();

                    entry.flags = stream.ReadUInt32();

                    if ((entry.flags & 0x400000) != 0) // FSOUND_IMAADPCM
                    {
                        entry.codec = FSBCodec.ADPCM;
                    }
                    else
                    {
                        entry.codec = FSBCodec.XMA; // Default to XMA
                    }

                    entry.sampleRate = stream.ReadInt32();
                    entry.pan = stream.ReadInt16();
                    entry.defPri = stream.ReadInt16();

                    entry.blockAlign = stream.ReadInt16();

                    entry.numChannels = stream.ReadInt16();

                    stream.Position += 0x08;

                    entry.volume = stream.ReadInt32();
                    entry.unknownData = stream.ReadBytes(entry.size - 76);

                    fsbEntries.Add(entry);
                }

                stream.Position = 0x30 + sampleHeaderSize;

                foreach (FSBEntry entry in fsbEntries)
                {
                    entry.audioData = stream.ReadBytes(entry.streamSize);
                }
            }
        }

        public FSBEntry ReadXMA(string path)
        {
            FSBEntry entry = new FSBEntry();

            var bytes = File.ReadAllBytes(path);

            using (var stream = new BinaryStream(new MemoryStream(bytes)))
            {

                if (stream.ReadString(4) != xmaMagic)
                    throw new InvalidDataException("Not an XMA file. Please open an XMA file and try again.");

                string fileName = Path.GetFileNameWithoutExtension(path);

                entry.name = fileName.Substring(0, fileName.Length > 32 ? 31 : fileName.Length);
                entry.sourceFileName = Path.GetFileName(path);

                stream.Position = 0x16;

                entry.numChannels = stream.ReadInt16();
                entry.sampleRate = stream.ReadInt32();

                stream.Position += 0x1C;

                entry.numSamples = stream.ReadInt32();
                entry.loopEndSample = entry.numSamples - 1;

                stream.Position = FindSequence(bytes, dataHeader) + 4;

                entry.streamSize = stream.ReadInt32();
                entry.audioData = stream.ReadBytes(entry.streamSize);
            }

            return entry;
        }

        public FSBEntry ReadWAV(string path)
        {
            FSBEntry entry = new FSBEntry();
            using (var stream = new BinaryStream(File.OpenRead(path)))
            {
                // Read RIFF header
                if (stream.ReadString(4) != "RIFF")
                    throw new InvalidDataException("Not a WAV file. Please open a WAV file and try again.");

                stream.ReadInt32(); // File size

                if (stream.ReadString(4) != "WAVE")
                    throw new InvalidDataException("Not a WAV file. Please open a WAV file and try again.");

                bool fmtChunkFound = false;
                bool dataChunkFound = false;
                bool factChunkFound = false;

                // Robustly iterate through chunks
                while (stream.Position < stream.Length)
                {
                    string chunkId = stream.ReadString(4);
                    int chunkSize = stream.ReadInt32();
                    long chunkEnd = stream.Position + chunkSize;

                    switch (chunkId)
                    {
                        case "fmt ":
                            short audioFormat = stream.ReadInt16();
                            if (audioFormat != 0x0011) // IMA ADPCM
                                throw new InvalidDataException("WAV file is not IMA ADPCM format.");

                            entry.codec = FSBCodec.ADPCM;
                            entry.numChannels = stream.ReadInt16();
                            entry.sampleRate = stream.ReadInt32();
                            stream.Position += 4; // Skip AvgBytesPerSec
                            entry.blockAlign = stream.ReadInt16();

                            short bitsPerSample = stream.ReadInt16();
                            if (bitsPerSample != 4)
                                throw new InvalidDataException("WAV file is not 4-bit IMA ADPCM.");

                            fmtChunkFound = true;
                            break;

                        case "fact":
                            entry.numSamples = stream.ReadInt32();
                            factChunkFound = true;
                            break;

                        case "data":
                            entry.streamSize = chunkSize;
                            entry.audioData = stream.ReadBytes(chunkSize);
                            dataChunkFound = true;
                            break;
                    }

                    // Ensure the stream is positioned correctly for the next chunk, accounting for padding
                    stream.Position = chunkEnd;
                    if (stream.Position % 2 != 0)
                    {
                        stream.Position++;
                    }
                }

                if (!fmtChunkFound)
                    throw new InvalidDataException("Could not find 'fmt ' chunk in WAV file.");
                if (!dataChunkFound)
                    throw new InvalidDataException("Could not find 'data' chunk in WAV file.");

                // Recalculate numSamples if 'fact' chunk is missing
                if (!factChunkFound)
                {
                    int samplesPerBlock = (entry.blockAlign - 4 * entry.numChannels) * 8 / (4 * entry.numChannels) + 1;
                    entry.numSamples = (entry.streamSize / entry.blockAlign) * samplesPerBlock;
                }

                string fileName = Path.GetFileNameWithoutExtension(path);
                entry.name = fileName.Length > 30 ? fileName.Substring(0, 30) : fileName;
                entry.sourceFileName = Path.GetFileName(path);
                entry.loopEndSample = entry.numSamples - 1;
            }

            return entry;
        }

        public void WriteFile(string path)
        {
            using (var file = new FileStream(path, FileMode.Create))
            using (var stream = new BinaryStream(file, ByteConverter.Little))
            {
                int headerSize = 0;
                int totalDataSize = 0;

                stream.Position = 0x0;

                stream.WriteString("FSB4", StringCoding.Raw);
                stream.WriteInt32(fsbEntries.Count);

                foreach (FSBEntry entry in fsbEntries)
                {
                    headerSize += entry.size;
                    totalDataSize += entry.streamSize;
                }

                stream.WriteInt32(headerSize);
                stream.WriteInt32(totalDataSize);
                stream.WriteUInt32(262144); // Hardcoded extended version number?
                stream.WriteUInt32(64); // Hardcoded flags?
                stream.WriteBytes(headerHash); // Some sort of hash

                const uint FSOUND_STEREO = 0x40;
                const uint FSOUND_2D = 0x2000;
                const uint FSOUND_IMAADPCM = 0x400000;
                const uint FSOUND_IMAADPCMSTEREO = 0x20000000;

                foreach (FSBEntry entry in fsbEntries)
                {
                    entry.flags = FSOUND_2D;

                    if (entry.numChannels == 2)
                    {
                        entry.flags |= FSOUND_STEREO;
                    }

                    if (entry.codec == FSBCodec.ADPCM)
                    {
                        entry.flags |= FSOUND_IMAADPCM;
                        if (entry.numChannels == 2)
                        {
                            entry.flags |= FSOUND_IMAADPCMSTEREO;
                        }
                    }

                    stream.WriteInt16(entry.size);
                    stream.WriteString(entry.name, StringCoding.Raw);
                    
                    for (int i = entry.name.Length; i < 30; i++) // If name is shorter than 30 characters, pad the remainder
                    {
                        stream.WriteByte(0x0);
                    }

                    stream.WriteInt32(entry.numSamples);
                    stream.WriteInt32(entry.streamSize);
                    stream.WriteInt32(0); // Loop start sample
                    stream.WriteInt32(entry.loopEndSample);
                    stream.WriteUInt32(entry.flags);
                    stream.WriteInt32(entry.sampleRate);
                    stream.WriteInt16(entry.pan);
                    stream.WriteInt16(entry.defPri);

                    if (entry.codec == FSBCodec.ADPCM)
                    {
                        stream.WriteInt16(entry.blockAlign);
                    }
                    else
                    {
                        stream.WriteInt16(0);
                    }

                    stream.WriteInt16(entry.numChannels);
                    stream.WriteBytes(new byte[] { 0x00, 0x00, 0x80, 0x3F, 0x00, 0x40, 0x1C, 0x46 }); // Manually write bytes for two floats: 1 and 10000
                    stream.WriteInt32(entry.volume);

                    if (entry.unknownData != null)
                    {
                        int unknownInt = entry.numSamples;
                        if (entry.numChannels == 2) // Stereo
                        {
                            unknownInt += 384;
                        }
                        else // Mono
                        {
                            unknownInt += 768;
                        }

                        byte[] unknownIntBytes = BitConverter.GetBytes(unknownInt);
                        Array.Copy(unknownIntBytes, 0, entry.unknownData, entry.unknownData.Length - 4, 4);

                        stream.WriteBytes(entry.unknownData);
                    }
                }

                foreach (FSBEntry entry in fsbEntries)
                {
                    stream.WriteBytes(entry.audioData);
                }
            }
        }

        int FindSequence(byte[] source, byte[] seq)
        {
            var start = -1;
            for (var i = 0; i < source.Length - seq.Length + 1 && start == -1; i++)
            {
                var j = 0;
                for (; j < seq.Length && source[i + j] == seq[j]; j++) { }
                if (j == seq.Length) start = i;
            }
            return start;
        }
    }
}

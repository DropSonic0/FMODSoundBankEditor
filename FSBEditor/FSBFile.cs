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

                stream.Position = 0x18;
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

                    if (entry.codec == FSBCodec.ADPCM)
                    {
                        short extraDataSize = stream.ReadInt16();
                        if (extraDataSize == 2)
                        {
                            entry.samplesPerBlock = stream.ReadInt16();
                            stream.Position += 4; // Skip padding
                        }
                        else
                        {
                            // Not the format we expect, seek back and skip the 8 bytes to not break parsing
                            stream.Position -= 2;
                            stream.Position += 8;
                        }
                    }
                    else
                    {
                        stream.Position += 8; // Skip for non-ADPCM
                    }

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
            using (var stream = new BinaryReader(File.Open(path, FileMode.Open)))
            {
                // Read RIFF header
                if (new string(stream.ReadChars(4)) != "RIFF")
                    throw new InvalidDataException("Not a WAV file.");
                stream.ReadInt32(); // File size
                if (new string(stream.ReadChars(4)) != "WAVE")
                    throw new InvalidDataException("Not a WAV file.");

                // Read fmt chunk
                if (new string(stream.ReadChars(4)) != "fmt ")
                    throw new InvalidDataException("Expected 'fmt ' chunk.");
                int fmtChunkSize = stream.ReadInt32();
                short audioFormat = stream.ReadInt16();
                if (audioFormat != 0x0011) // IMA ADPCM
                    throw new InvalidDataException("WAV file is not IMA ADPCM format.");
                entry.codec = FSBCodec.ADPCM;
                entry.numChannels = stream.ReadInt16();
                entry.sampleRate = stream.ReadInt32();
                stream.ReadInt32(); // Read and discard AvgBytesPerSec
                entry.blockAlign = stream.ReadInt16();
                short bitsPerSample = stream.ReadInt16();
                if (bitsPerSample != 4)
                    throw new InvalidDataException("WAV file is not 4-bit IMA ADPCM.");
                short extraDataSize = stream.ReadInt16();
                if (extraDataSize == 2)
                {
                    entry.samplesPerBlock = stream.ReadInt16();
                }

                // Read fact chunk
                if (new string(stream.ReadChars(4)) != "fact")
                    throw new InvalidDataException("Expected 'fact' chunk.");
                stream.ReadInt32(); // Chunk size
                entry.numSamples = stream.ReadInt32();

                // Read data chunk
                if (new string(stream.ReadChars(4)) != "data")
                    throw new InvalidDataException("Expected 'data' chunk.");
                entry.streamSize = stream.ReadInt32();
                entry.audioData = stream.ReadBytes(entry.streamSize);

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

                    stream.WriteInt16(entry.blockAlign);

                    stream.WriteInt16(entry.numChannels);

                    if (entry.codec == FSBCodec.ADPCM)
                    {
                        stream.WriteInt16(2); // extraDataSize
                        stream.WriteInt16(entry.samplesPerBlock);
                        stream.WriteInt32(0); // padding
                    }
                    else
                    {
                        stream.WriteInt64(0); // padding
                    }

                    stream.WriteInt32(entry.volume);

                    if (entry.unknownData != null)
                    {
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

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
        byte[] dataHeader = new byte[] { 0x64, 0x61, 0x74, 0x61 };
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

                    stream.Position += 0x02;

                    entry.numChannels = stream.ReadInt16();

                    stream.Position += 0x08;

                    entry.volume = stream.ReadInt32();
                    entry.unknownData = stream.ReadBytes(entry.size - 80);
                    entry.unknownInt = stream.ReadInt32();

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
                int blockAlign = 0;

                // Iterate through chunks
                while (stream.Position < stream.Length)
                {
                    string chunkId = stream.ReadString(4);
                    int chunkSize = stream.ReadInt32();

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
                            entry.blockAlign = (short)stream.ReadInt16();
                            blockAlign = entry.blockAlign;
                            stream.Position += 2; // Skip bitsPerSample
                            fmtChunkFound = true;

                            // Skip extra format bytes if they exist
                            if (chunkSize > 16)
                            {
                                stream.Position += chunkSize - 16;
                            }
                            break;

                        case "data":
                            entry.streamSize = chunkSize;
                            entry.audioData = stream.ReadBytes(chunkSize);
                            dataChunkFound = true;
                            break;

                        default:
                            // Skip other chunks
                            stream.Position += chunkSize;
                            break;
                    }
                }

                if (!fmtChunkFound)
                    throw new InvalidDataException("Could not find 'fmt ' chunk in WAV file.");
                if (!dataChunkFound)
                    throw new InvalidDataException("Could not find 'data' chunk in WAV file.");

                // Calculate numSamples for ADPCM
                if (blockAlign > 0)
                {
                    entry.numSamples = (entry.streamSize / blockAlign) * (1 + (blockAlign - 4 * entry.numChannels) * 2 / entry.numChannels);
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
                    // Overwrite header sizes with new calculated one
                    entry.size = (sizeof(short) * 5) + (sizeof(int) * 15) + (sizeof(float) * 2) + 34; // 34 = 30 for char, 4 for 3 unknown + 1 codec byte
                    headerSize += entry.size;
                    totalDataSize += entry.streamSize;
                }

                stream.WriteInt32(headerSize);
                stream.WriteInt32(totalDataSize);
                stream.WriteUInt32(262144); // Hardcoded extended version number?
                stream.WriteUInt32(64); // Hardcoded flags?
                stream.WriteBytes(new byte[] { 0x75, 0x44, 0xD7, 0x47, 0x8B, 0x24, 0xCB, 0xE9, 0x53, 0xBD, 0xBA, 0xB1, 0xB6, 0x12, 0x8A, 0x4C, 0xF4, 0xE3, 0x9C, 0x9B, 0xEB, 0x57, 0x0F, 0x70 }); // Some sort of hash

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

                    /*for (int i = 0; i < 9; i++) // Write 9 unknown ints based on 4n0_ausmini_exh example
                    {
                        stream.WriteInt32(0);
                    }*/

                    stream.WriteUInt32(entry.flags);
                    stream.WriteInt32(0);
                    stream.WriteInt32(0);
                    stream.WriteInt32(0);
                    stream.WriteInt32(16);
                    stream.WriteInt32(1);
                    stream.WriteInt32(3);
                    stream.WriteInt32(0);

                    // Some unknown ints here - usually appear to be 384 or 768 more than numSamples based on mono or stereo
                    if (entry.unknownInt == 0)
                    {
                        if (entry.numChannels == 2)
                        {
                            stream.WriteInt32(entry.numSamples + 384);
                        }
                        else
                        {
                            stream.WriteInt32(entry.numSamples + 768);
                        }
                    }
                    else
                    {
                        stream.WriteInt32(entry.unknownInt);
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

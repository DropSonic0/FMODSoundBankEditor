using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace FSBEditor
{
    public enum FSBCodec : byte
    {
        XMA = 1,
        ADPCM = 7
    }

    class FSBEntry
    {
        public string name, sourceFileName;
        public short size;
        public int numSamples, streamSize, loopStartSample, loopEndSample, sampleRate, volume;
        public uint flags;
        public long startOffset;
        public short pan, defPri, numChannels, blockAlign, samplesPerBlock;
        public byte[] audioData, unknownData;
        public FSBCodec codec;

        public FSBEntry()
        {
            codec = FSBCodec.XMA;
            loopStartSample = 0;
            defPri = 128;
            pan = 255;
            volume = 112;
            numChannels = 1;
        }
    }
}

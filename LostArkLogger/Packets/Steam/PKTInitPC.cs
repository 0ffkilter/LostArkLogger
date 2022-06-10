using System;
using System.Collections.Generic;
namespace LostArkLogger
{
    public partial class PKTInitPC
    {
        public void SteamDecode(BitReader reader)
        {
            b_0 = reader.ReadByte();
            bytearraylist = reader.ReadList<Byte[]>(30);
            b_4 = reader.ReadByte();
            Name = reader.ReadString();
            u32_9 = reader.ReadUInt32();
            b_15 = reader.ReadByte();
            b_18 = reader.ReadByte();
            u16list = reader.ReadList<UInt16>();
            u16_4 = reader.ReadUInt16();
            b_19 = reader.ReadByte();
            u64_0 = reader.ReadUInt64();
            subPKTNewNpc29s = reader.ReadList<subPKTNewNpc29>();
            b_1 = reader.ReadByte();
            ClassId = reader.ReadUInt16();
            b_2 = reader.ReadByte();
            b_3 = reader.ReadByte();
            if (b_3 == 1)
                u32_0 = reader.ReadUInt32();
            GearLevel = reader.ReadUInt32();
            u64_1 = reader.ReadUInt64();
            u32_1 = reader.ReadUInt32();
            u32_2 = reader.ReadUInt32();
            u32_3 = reader.ReadUInt32();
            u32_4 = reader.ReadUInt32();
            u32_5 = reader.ReadUInt32();
            b_5 = reader.ReadByte();
            statusEffectDatas = reader.ReadList<StatusEffectData>();
            u32_6 = reader.ReadUInt32();
            u16_0 = reader.ReadUInt16();
            b_6 = reader.ReadByte();
            PlayerId = reader.ReadUInt64();
            u32_7 = reader.ReadUInt32();
            b_7 = reader.ReadByte();
            bytearray_0 = reader.ReadBytes(35);
            b_8 = reader.ReadByte();
            b_9 = reader.ReadByte();
            blist = reader.ReadList<Byte>();
            u64_2 = reader.ReadUInt64();
            statPair = reader.Read<StatPair>();
            u32_8 = reader.ReadUInt32();
            u16_1 = reader.ReadUInt16();
            b_10 = reader.ReadByte();
            u32_10 = reader.ReadUInt32();
            u16_2 = reader.ReadUInt16();
            b_11 = reader.ReadByte();
            bytearray_1 = reader.ReadBytes(25);
            u32_11 = reader.ReadUInt32();
            b_12 = reader.ReadByte();
            field0 = reader.ReadBytes(9);
            Level = reader.ReadUInt16();
            field2 = reader.ReadBytes(101);
            b_13 = reader.ReadByte();
            b_14 = reader.ReadByte();
            u16_3 = reader.ReadUInt16();
            b_16 = reader.ReadByte();
            b_17 = reader.ReadByte();
        }
    }
}

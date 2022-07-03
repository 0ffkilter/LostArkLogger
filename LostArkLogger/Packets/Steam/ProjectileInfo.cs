using System;
using System.Collections.Generic;
namespace LostArkLogger
{
    public partial class ProjectileInfo
    {
        public void SteamDecode(BitReader reader)
        {
			u32_0 = reader.ReadUInt32();
			SkillId = reader.ReadUInt32();
			b_1 = reader.ReadByte();
			bool flag = b_1 == 1;
			if (flag)
			{
				u64list = reader.ReadList<ulong>(0);
			}
			u32_2 = reader.ReadUInt32();
			b_2 = reader.ReadByte();
			bytearray = reader.ReadBytes(6);
			SkillLevel = reader.ReadByte();
			u32_3 = reader.ReadUInt32();
			b_3 = reader.ReadByte();
			bool flag2 = b_3 == 1;
			if (flag2)
			{
				u32_4 = reader.ReadUInt32();
			}
			SkillEffect = reader.ReadUInt32();
			u64_0 = reader.ReadUInt64();
			u16_0 = reader.ReadUInt16();
			b_0 = reader.ReadByte();
			ProjectileId = reader.ReadUInt64();
			u64_1 = reader.ReadUInt64();
			u32_1 = reader.ReadUInt32();
			u64_2 = reader.ReadUInt64();
			OwnerId = reader.ReadUInt64();
			u16_1 = reader.ReadUInt16();
			Tripods = reader.ReadBytes(3);
		}
    }
}

using System;
using System.Collections.Generic;
namespace LostArkLogger
{
    public partial class subPKTNewPC33
    {
        public void SteamDecode(BitReader reader)
        {
			b = reader.ReadByte();
			bool flag = b == 1;
			if (flag)
			{
				bytearray_0 = reader.ReadBytes(12);
			}
			u32_0 = reader.ReadUInt32();
			bytearray_1 = reader.ReadBytes(12);
			u32_1 = reader.ReadUInt32();
		}
    }
}

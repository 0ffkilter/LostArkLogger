using System;
using System.Collections.Generic;
namespace LostArkLogger
{
    public partial class subPKTNewNpc5
    {
        public void SteamDecode(BitReader reader)
        {
            num = reader.ReadUInt32();
            for(var i = 0; i < num; i++)
            {
                b.Add(reader.ReadByte());
            }
        }
    }
}

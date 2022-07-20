﻿using K4os.Compression.LZ4;
using LostArkLogger.Utilities;
using SharpPcap;
using Snappy;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace LostArkLogger
{
    internal class Parser : IDisposable
    {
#pragma warning disable CA2101 // Specify marshaling for P/Invoke string arguments
        [DllImport("wpcap.dll", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)] static extern IntPtr pcap_strerror(int err);
#pragma warning restore CA2101 // Specify marshaling for P/Invoke string arguments
        Machina.TCPNetworkMonitor tcp;
        ILiveDevice pcap;
        public event Action<LogInfo> onCombatEvent;
        public event Action onNewZone;
        public event Action<string> onLogAppend;
        public event Action<int> onPacketTotalCount;
        public bool enableLogging = true;
        public bool use_npcap = false;
        private object lockPacketProcessing = new object(); // needed to synchronize UI swapping devices
        public Machina.Infrastructure.NetworkMonitorType? monitorType = null;
        public List<Encounter> Encounters = new List<Encounter>();
        public Encounter currentEncounter = new Encounter();
        Byte[] fragmentedPacket = new Byte[0];
        private string _localPlayerName = "You";
        private uint _localGearLevel = 0;

        string logsPath;
        string fileName;
        StreamWriter stream;
        int writerLines = 0;

        public Parser(string customLogPath = default)
        {
            UpdateLogPath(customLogPath);

            Encounters.Add(currentEncounter);
            onCombatEvent += Parser_onDamageEvent;
            onNewZone += Parser_onNewZone;

            InstallListener();
        }

        public void UpdateLogPath(string customLogPath = default)
        {
            if (String.IsNullOrEmpty(customLogPath))
            {
                string documentsPath = System.Environment.GetFolderPath(System.Environment.SpecialFolder.MyDocuments);
                logsPath = Path.Combine(documentsPath, "Lost Ark Logs");
            }
            else
            {
                logsPath = customLogPath;
            }

            if (!Directory.Exists(logsPath)) Directory.CreateDirectory(logsPath);
            fileName = logsPath + "\\LostArk_" + DateTime.Now.ToString("yyyy-MM-dd-HH-mm-ss") + ".log";

            CreateStreamWriter();
        }

        public void CreateStreamWriter()
        {
            if (stream != null)
            {
                stream.Flush();
                stream.Close();
            }

            stream = new StreamWriter(fileName, true);
            writerLines = 0;
        }
        // UI needs to be able to ask us to reload our listener based on the current user settings
        public void InstallListener()
        {
            lock (lockPacketProcessing)
            {
                // If we have an installed listener, that needs to go away or we duplicate traffic
                UninstallListeners();

                // Reset all state related to current packet processing here that won't be valid when creating a new listener.
                fragmentedPacket = new Byte[0];

                // We default to using npcap, but the UI can also set this to false.
                if (use_npcap)
                {
                    monitorType = Machina.Infrastructure.NetworkMonitorType.WinPCap;
                    string filter = "ip and tcp port 6040";
                    bool foundAdapter = false;
                    NetworkInterface gameInterface;
                    // listening on every device results in duplicate traffic, unfortunately, so we'll find the adapter used by the game here
                    try
                    {
                        pcap_strerror(1); // verify winpcap works at all
                        gameInterface = NetworkUtil.GetAdapterUsedByProcess("LostArk");
                        foreach (var device in CaptureDeviceList.Instance)
                        {
                            if (device.MacAddress == null) continue; // SharpPcap.IPCapDevice.MacAddress is null in some cases
                            if (gameInterface.GetPhysicalAddress().ToString() == device.MacAddress.ToString())
                            {
                                try
                                {
                                    device.Open(DeviceModes.None, 1000); // todo: 1sec timeout ok?
                                    device.Filter = filter;
                                    device.OnPacketArrival += new PacketArrivalEventHandler(Device_OnPacketArrival_pcap);
                                    device.StartCapture();
                                    pcap = device;
                                    foundAdapter = true;
                                    break;
                                }
                                catch (Exception ex)
                                {
                                    var exceptionMessage = "Exception while trying to listen to NIC " + device.Name + "\n" + ex.ToString();
                                    Console.WriteLine(exceptionMessage);
                                    AppendLog(0, exceptionMessage);
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        var exceptionMessage = "Sharppcap init failed, using rawsockets instead, exception:\n" + ex.ToString();
                        Console.WriteLine(exceptionMessage);
                        AppendLog(0, exceptionMessage);
                    }
                    // If we failed to find a pcap device, fall back to rawsockets.
                    if (!foundAdapter)
                    {
                        use_npcap = false;
                        pcap = null;
                    }
                }

                if (use_npcap == false)
                {
                    // Always fall back to rawsockets
                    tcp = new Machina.TCPNetworkMonitor();
                    tcp.Config.WindowClass = "EFLaunchUnrealUWindowsClient";
                    monitorType = tcp.Config.MonitorType = Machina.Infrastructure.NetworkMonitorType.RawSocket;
                    tcp.DataReceivedEventHandler += (Machina.Infrastructure.TCPConnection connection, byte[] data) => Device_OnPacketArrival_machina(connection, data);
                    tcp.Start();
                }
            }
        }

        void ProcessDamageEvent(Entity sourceEntity, UInt32 skillId, UInt32 skillEffectId, SkillDamageEvent dmgEvent)
        {
            // damage dealer is a player
            if (!String.IsNullOrEmpty(sourceEntity.ClassName) && sourceEntity.ClassName != "UnknownClass")
            {
                // player hasn't been announced on logs before. possibly because user opened logger after they got into a zone
                if (!currentEncounter.LoggedEntities.ContainsKey(sourceEntity.EntityId))
                {
                    // classId is unknown, can be fixed
                    // level, currenthp and maxhp is unknown
                    AppendLog(3, sourceEntity.EntityId.ToString("X"), sourceEntity.Name, "0", sourceEntity.ClassName, "1", "0", "0");
                    currentEncounter.LoggedEntities.TryAdd(sourceEntity.EntityId, true);
                }
            }

            var skillName = Skill.GetSkillName(skillId, skillEffectId);
            var targetEntity = currentEncounter.Entities.GetOrAdd(dmgEvent.TargetId);
            var destinationName = targetEntity != null ? targetEntity.VisibleName : dmgEvent.TargetId.ToString("X");
            //var log = new LogInfo { Time = DateTime.Now, Source = sourceName, PC = sourceName.Contains("("), Destination = destinationName, SkillName = skillName, Crit = (dmgEvent.FlagsMaybe & 0x81) > 0, BackAttack = (dmgEvent.FlagsMaybe & 0x10) > 0, FrontAttack = (dmgEvent.FlagsMaybe & 0x20) > 0 };

            var isCrit = ((DamageModifierFlags)dmgEvent.Modifier &
                     (DamageModifierFlags.DotCrit |
                      DamageModifierFlags.SkillCrit)) > 0;

            var isBackAttack = ((DamageModifierFlags)dmgEvent.Modifier & (DamageModifierFlags.BackAttack)) > 0;
            var isFrontAttack = ((DamageModifierFlags)dmgEvent.Modifier & (DamageModifierFlags.FrontAttack)) > 0;

            var log = new LogInfo
            {
                Time = DateTime.Now,
                SourceEntity = sourceEntity,
                DestinationEntity = targetEntity,
                SkillId = skillId,
                SkillEffectId = skillEffectId,
                SkillName = skillName,
                Damage = (ulong)dmgEvent.Damage,
                Crit = isCrit,
                BackAttack = isBackAttack,
                FrontAttack = isFrontAttack
            };
            onCombatEvent?.Invoke(log);
            AppendLog(8, sourceEntity.EntityId.ToString("X"), sourceEntity.Name, skillId.ToString(), Skill.GetSkillName(skillId), skillEffectId.ToString(), Skill.GetSkillEffectName(skillEffectId), targetEntity.EntityId.ToString("X"), targetEntity.Name, dmgEvent.Damage.ToString(), dmgEvent.Modifier.ToString("X"), isCrit ? "1" : "0", isBackAttack ? "1" : "0", isFrontAttack ? "1" : "0", dmgEvent.CurHp.ToString(), dmgEvent.MaxHp.ToString());
        }
        void ProcessSkillDamage(PKTSkillDamageNotify damage)
        {
            var sourceEntity = currentEncounter.Entities.GetOrAdd(damage.SourceId);
            if (sourceEntity.Type == Entity.EntityType.Projectile)
                sourceEntity = currentEncounter.Entities.GetOrAdd(sourceEntity.OwnerId);
            if (sourceEntity.Type == Entity.EntityType.Summon)
                sourceEntity = currentEncounter.Entities.GetOrAdd(sourceEntity.OwnerId);
            var className = Skill.GetClassFromSkill(damage.SkillId);
            if (String.IsNullOrEmpty(sourceEntity.ClassName) && className != "UnknownClass")
            {
                sourceEntity.Type = Entity.EntityType.Player;
                sourceEntity.ClassName = className; // for case where we don't know user's class yet            
            }

            if (String.IsNullOrEmpty(sourceEntity.Name)) sourceEntity.Name = damage.SourceId.ToString("X");
            foreach (var dmgEvent in damage.skillDamageEvents)
                ProcessDamageEvent(sourceEntity, damage.SkillId, damage.SkillEffectId, dmgEvent);
        }

        void ProcessSkillDamage(PKTSkillDamageAbnormalMoveNotify damage)
        {
            var sourceEntity = currentEncounter.Entities.GetOrAdd(damage.SourceId);
            if (sourceEntity.Type == Entity.EntityType.Projectile)
                sourceEntity = currentEncounter.Entities.GetOrAdd(sourceEntity.OwnerId);
            if (sourceEntity.Type == Entity.EntityType.Summon)
                sourceEntity = currentEncounter.Entities.GetOrAdd(sourceEntity.OwnerId);
            var className = Skill.GetClassFromSkill(damage.SkillId);
            if (String.IsNullOrEmpty(sourceEntity.ClassName) && className != "UnknownClass")
            {
                sourceEntity.Type = Entity.EntityType.Player;
                sourceEntity.ClassName = className; // for case where we don't know user's class yet            
            }

            if (String.IsNullOrEmpty(sourceEntity.Name)) sourceEntity.Name = damage.SourceId.ToString("X");
            foreach (var dmgEvent in damage.skillDamageMoveEvents)
                ProcessDamageEvent(sourceEntity, damage.SkillId, damage.SkillEffectId, dmgEvent.skillDamageEvent);
        }

        OpCodes GetOpCode(Byte[] packets)
        {
            var opcodeVal = BitConverter.ToUInt16(packets, 2);
            var opCodeString = "";
            if (Properties.Settings.Default.Region == Region.Steam) opCodeString = ((OpCodes_Steam)opcodeVal).ToString();
            //if (Properties.Settings.Default.Region == Region.Russia) opCodeString = ((OpCodes_ru)opcodeVal).ToString();
            if (Properties.Settings.Default.Region == Region.Korea) opCodeString = ((OpCodes_Korea)opcodeVal).ToString();
            return (OpCodes)Enum.Parse(typeof(OpCodes), opCodeString);
        }
        Byte[] XorTableSteam = ObjectSerialize.Decompress(Properties.Resources.xor_Steam);
        //Byte[] XorTableRu = ObjectSerialize.Decompress(Properties.Resources.xor_ru);
        Byte[] XorTableKorea = ObjectSerialize.Decompress(Properties.Resources.xor_Korea);
        Byte[] XorTable { get { return Properties.Settings.Default.Region == Region.Steam ? XorTableSteam : XorTableKorea; } }
        void ProcessPacket(List<Byte> data)
        {
            var packets = data.ToArray();
            var packetWithTimestamp = BitConverter.GetBytes(DateTime.UtcNow.ToBinary()).ToArray().Concat(data);
            onPacketTotalCount?.Invoke(loggedPacketCount++);
            while (packets.Length > 0)
            {
                if (fragmentedPacket.Length > 0)
                {
                    packets = fragmentedPacket.Concat(packets).ToArray();
                    fragmentedPacket = new Byte[0];
                }
                if (6 > packets.Length)
                {
                    fragmentedPacket = packets.ToArray();
                    return;
                }
                var opcode = GetOpCode(packets);
                //Console.WriteLine(opcode);
                var packetSize = BitConverter.ToUInt16(packets.ToArray(), 0);
                if (packets[5] != 1 || 6 > packets.Length || packetSize < 7)
                {
                    // not sure when this happens
                    fragmentedPacket = new Byte[0];
                    return;
                }
                if (packetSize > packets.Length)
                {
                    fragmentedPacket = packets;
                    return;
                }
                var payload = packets.Skip(6).Take(packetSize - 6).ToArray();
                Xor.Cipher(payload, BitConverter.ToUInt16(packets, 2), XorTable);
                switch (packets[4])
                {
                    case 1: //LZ4
                        var buffer = new byte[0x11ff2];
                        var result = LZ4Codec.Decode(payload, 0, payload.Length, buffer, 0, 0x11ff2);
                        if (result < 1) throw new Exception("LZ4 output buffer too small");
                        payload = buffer.Take(result).ToArray(); //TODO: check LZ4 payload and see if we should skip some data
                        break;
                    case 2: //Snappy
                        //https://github.com/robertvazan/snappy.net
                        payload = SnappyCodec.Uncompress(payload.ToArray()).Skip(16).ToArray();
                        //payload = SnappyCodec.Uncompress(payload.Skip(Properties.Settings.Default.Region == Region.Russia ? 4 : 0).ToArray()).Skip(16).ToArray();
                        break;
                    case 3: //Oodle
                        payload = Oodle.Decompress(payload).Skip(16).ToArray();
                        break;
                }

                // write packets for analyzing, bypass common, useless packets
                //if (opcode != OpCodes.PKTMoveError && opcode != OpCodes.PKTMoveNotify && opcode != OpCodes.PKTMoveNotifyList && opcode != OpCodes.PKTTransitStateNotify && opcode != OpCodes.PKTPing && opcode != OpCodes.PKTPong)
                //    Console.WriteLine(opcode + " : " + opcode.ToString("X") + " : " + BitConverter.ToString(payload));

                /* Uncomment for auction house accessory sniffing
                if (opcode == OpCodes.PKTAuctionSearchResult)
                {
                    var pc = new PKTAuctionSearchResult(payload);
                    Console.WriteLine("NumItems=" + pc.NumItems.ToString());
                    Console.WriteLine("Id, Stat1, Stat2, Engraving1, Engraving2, Engraving3");
                    foreach (var item in pc.Items)
                    {
                        Console.WriteLine(item.ToString());
                    }
                }
                */
                if (opcode == OpCodes.PKTNewProjectile)
                {
                    var projectile = new PKTNewProjectile(new BitReader(payload)).projectileInfo;
                    currentEncounter.Entities.AddOrUpdate(new Entity
                    {
                        OwnerId = projectile.OwnerId,
                        EntityId = projectile.ProjectileId,
                        Type = Entity.EntityType.Projectile
                    });
                }
                else if (opcode == OpCodes.PKTInitEnv)
                {
                    var env = new PKTInitEnv(new BitReader(payload));
                    if (currentEncounter.Infos.Count == 0) Encounters.Remove(currentEncounter);
                    currentEncounter = new Encounter();
                    Encounters.Add(currentEncounter);

                    currentEncounter.Entities.AddOrUpdate(new Entity
                    {
                        EntityId = env.PlayerId,
                        Name = _localPlayerName,
                        Type = Entity.EntityType.Player,
                        GearLevel = _localGearLevel
                    });
                    onNewZone?.Invoke();
                    AppendLog(1, env.PlayerId.ToString("X"));
                }
                else if (opcode == OpCodes.PKTRaidResult // raid over
                         || opcode == OpCodes.PKTRaidBossKillNotify // boss dead, includes argos phases
                         || opcode == OpCodes.PKTTriggerBossBattleStatus) // wipe
                {
                    currentEncounter.End = DateTime.Now;
                    currentEncounter = new Encounter();
                    if (opcode == OpCodes.PKTRaidBossKillNotify || opcode == OpCodes.PKTTriggerBossBattleStatus)
                        currentEncounter.Entities = Encounters.Last().Entities; // preserve entities 
                    Encounters.Add(currentEncounter);

                    var phaseCode = "0"; // PKTRaidResult
                    if (opcode == OpCodes.PKTRaidBossKillNotify) phaseCode = "1";
                    else if (opcode == OpCodes.PKTTriggerBossBattleStatus) phaseCode = "2";
                    AppendLog(2, phaseCode);

                }
                else if (opcode == OpCodes.PKTInitPC)
                {
                    var pc = new PKTInitPC(new BitReader(payload));
                    if (currentEncounter.Infos.Count == 0) Encounters.Remove(currentEncounter);
                    currentEncounter = new Encounter();
                    Encounters.Add(currentEncounter);
                    _localPlayerName = pc.Name;
                    _localGearLevel = pc.GearLevel;
                    currentEncounter.Entities.AddOrUpdate(new Entity
                    {
                        EntityId = pc.PlayerId,
                        Name = _localPlayerName,
                        ClassName = Npc.GetPcClass(pc.ClassId),
                        Type = Entity.EntityType.Player,
                        GearLevel = _localGearLevel
                    });
                    currentEncounter.Entities.AddOrUpdate(new Entity { ClassName = Npc.GetPcClass(pc.ClassId) });
                    onNewZone?.Invoke();

                    if (!currentEncounter.LoggedEntities.ContainsKey(pc.PlayerId))
                    {
                        var gearScore = BitConverter.ToSingle(BitConverter.GetBytes(pc.GearLevel), 0).ToString("0.##");
                        AppendLog(3, pc.PlayerId.ToString("X"), pc.Name, pc.ClassId.ToString(), Npc.GetPcClass(pc.ClassId), pc.Level.ToString(), gearScore, pc.statPair.Value[pc.statPair.StatType.IndexOf((Byte)StatType.STAT_TYPE_HP)].ToString(), pc.statPair.Value[pc.statPair.StatType.IndexOf((Byte)StatType.STAT_TYPE_MAX_HP)].ToString());
                        currentEncounter.LoggedEntities.TryAdd(pc.PlayerId, true);
                    }
                }
                else if (opcode == OpCodes.PKTNewPC)
                {
                    var pc = new PKTNewPC(new BitReader(payload)).pCStruct;
                    currentEncounter.Entities.AddOrUpdate(new Entity
                    {
                        EntityId = pc.PlayerId,
                        Name = pc.Name,
                        ClassName = Npc.GetPcClass(pc.ClassId),
                        Type = Entity.EntityType.Player,
                        GearLevel = pc.GearLevel
                    });

                    if (!currentEncounter.LoggedEntities.ContainsKey(pc.PlayerId))
                    {
                        var gearScore = BitConverter.ToSingle(BitConverter.GetBytes(pc.GearLevel), 0).ToString("0.##");
                        AppendLog(3, pc.PlayerId.ToString("X"), pc.Name, pc.ClassId.ToString(), Npc.GetPcClass(pc.ClassId), pc.Level.ToString(), gearScore, pc.statPair.Value[pc.statPair.StatType.IndexOf((Byte)StatType.STAT_TYPE_HP)].ToString(), pc.statPair.Value[pc.statPair.StatType.IndexOf((Byte)StatType.STAT_TYPE_MAX_HP)].ToString());
                        currentEncounter.LoggedEntities.TryAdd(pc.PlayerId, true);
                    }
                }
                else if (opcode == OpCodes.PKTNewNpc)
                {
                    var npc = new PKTNewNpc(new BitReader(payload)).npcStruct;
                    currentEncounter.Entities.AddOrUpdate(new Entity
                    {
                        EntityId = npc.NpcId,
                        Name = Npc.GetNpcName(npc.NpcType),
                        Type = Entity.EntityType.Npc
                    });
                    AppendLog(4, npc.NpcId.ToString("X"), npc.NpcType.ToString(), Npc.GetNpcName(npc.NpcType), npc.statPair.Value[npc.statPair.StatType.IndexOf((Byte)StatType.STAT_TYPE_HP)].ToString(), npc.statPair.Value[npc.statPair.StatType.IndexOf((Byte)StatType.STAT_TYPE_MAX_HP)].ToString());
                }
                else if (opcode == OpCodes.PKTRemoveObject)
                {
                    var obj = new PKTRemoveObject(new BitReader(payload));
                    //var projectile = new PKTRemoveObject { Bytes = converted };
                    //ProjectileOwner.Remove(projectile.ProjectileId, projectile.OwnerId);
                }
                else if (opcode == OpCodes.PKTDeathNotify)
                {
                    var death = new PKTDeathNotify(new BitReader(payload));
                    AppendLog(5, death.TargetId.ToString("X"), currentEncounter.Entities.GetOrAdd(death.TargetId).Name, death.SourceId.ToString("X"), currentEncounter.Entities.GetOrAdd(death.SourceId).Name);
                }
                else if (opcode == OpCodes.PKTSkillStartNotify)
                {
                    var skill = new PKTSkillStartNotify(new BitReader(payload));
                    AppendLog(6, skill.SourceId.ToString("X"), currentEncounter.Entities.GetOrAdd(skill.SourceId).Name, skill.SkillId.ToString(), Skill.GetSkillName(skill.SkillId));
                }
                else if (opcode == OpCodes.PKTSkillStageNotify)
                {
                    /*
                       2-stage charge
                        1 start
                        5 if use, 3 if continue
                        8 if use, 4 if continue
                        7 final
                       1-stage charge
                        1 start
                        5 if use, 2 if continue
                        6 final
                       holding whirlwind
                        1 on end
                       holding perfect zone
                        4 on start
                        5 on suc 6 on fail
                    */
                    var skill = new PKTSkillStageNotify(new BitReader(payload));
                    AppendLog(7, skill.SourceId.ToString("X"), currentEncounter.Entities.GetOrAdd(skill.SourceId).Name, skill.SkillId.ToString(), Skill.GetSkillName(skill.SkillId), skill.Stage.ToString());
                }
                else if (opcode == OpCodes.PKTSkillDamageNotify)
                    ProcessSkillDamage(new PKTSkillDamageNotify(new BitReader(payload)));
                else if (opcode == OpCodes.PKTSkillDamageAbnormalMoveNotify)
                    ProcessSkillDamage(new PKTSkillDamageAbnormalMoveNotify(new BitReader(payload)));
                else if (opcode == OpCodes.PKTStatChangeOriginNotify) // heal
                {
                    var health = new PKTStatChangeOriginNotify(new BitReader(payload));
                    var entity = currentEncounter.Entities.GetOrAdd(health.ObjectId);
                    var log = new LogInfo
                    {
                        Time = DateTime.Now,
                        SourceEntity = entity,
                        DestinationEntity = entity,
                        Heal = (UInt32)health.StatPairChangedList.Value[0]
                    };
                    onCombatEvent?.Invoke(log);
                    // might push this by 1??
                    AppendLog(9, entity.EntityId.ToString("X"), entity.Name, health.StatPairChangedList.Value[0].ToString(), health.StatPairChangedList.Value[0].ToString());// need to lookup cached max hp??
                }
                else if (opcode == OpCodes.PKTStatusEffectAddNotify) // shields included
                {
                    var buff = new PKTStatusEffectAddNotify(new BitReader(payload));
                    var amount = buff.statusEffectData.hasValue == 1 ? BitConverter.ToUInt32(buff.statusEffectData.Value, 0) : 0;
                    AppendLog(10, buff.statusEffectData.SourceId.ToString("X"), currentEncounter.Entities.GetOrAdd(buff.statusEffectData.SourceId).Name, buff.statusEffectData.StatusEffectId.ToString(), SkillBuff.GetSkillBuffName(buff.statusEffectData.StatusEffectId), buff.New.ToString(), buff.ObjectId.ToString("X"), currentEncounter.Entities.GetOrAdd(buff.ObjectId).Name, amount.ToString());
                }
                /*else if (opcode == OpCodes.PKTParalyzationStateNotify)
                {
                    var stagger = new PKTParalyzationStateNotify(new BitReader(payload));
                    var enemy = currentEncounter.Entities.GetOrAdd(stagger.TargetId);
                    var lastInfo = currentEncounter.Infos.LastOrDefault(); // hope this works
                    if (lastInfo != null) // there's no way to tell what is the source, so drop it for now
                    {
                        var player = lastInfo.SourceEntity;
                        var staggerAmount = stagger.ParalyzationPoint - enemy.Stagger;
                        if (stagger.ParalyzationPoint == 0)
                            staggerAmount = stagger.ParalyzationPointMax - enemy.Stagger;
                        enemy.Stagger = stagger.ParalyzationPoint;
                        var log = new LogInfo
                        {
                            Time = DateTime.Now, SourceEntity = player, DestinationEntity = enemy,
                            SkillName = lastInfo?.SkillName, Stagger = staggerAmount
                        };
                        onCombatEvent?.Invoke(log);
                    }
                }*/
                else if (opcode == OpCodes.PKTCounterAttackNotify)
                {
                    var counter = new PKTCounterAttackNotify(new BitReader(payload));
                    var source = currentEncounter.Entities.GetOrAdd(counter.SourceId);
                    var target = currentEncounter.Entities.GetOrAdd(counter.TargetId);
                    var log = new LogInfo
                    {
                        Time = DateTime.Now,
                        SourceEntity = currentEncounter.Entities.GetOrAdd(counter.SourceId),
                        DestinationEntity = currentEncounter.Entities.GetOrAdd(counter.TargetId),
                        SkillName = "Counter",
                        Damage = 0,
                        Counter = true
                    };
                    onCombatEvent?.Invoke(log);
                    AppendLog(11, source.EntityId.ToString("X"), source.Name, target.EntityId.ToString("X"), target.Name);
                }
                else if (opcode == OpCodes.PKTNewNpcSummon)
                {
                    var npc = new PKTNewNpcSummon(new BitReader(payload));
                    currentEncounter.Entities.AddOrUpdate(new Entity
                    {
                        EntityId = npc.npcStruct.NpcId,
                        OwnerId = npc.OwnerId,
                        Type = Entity.EntityType.Summon
                    });
                }
                if (packets.Length < packetSize) throw new Exception("bad packet maybe");
                packets = packets.Skip(packetSize).ToArray();
            }
        }

        public Boolean debugLog = false;
        BinaryWriter logger;
        FileStream logStream;
        UInt32 currentIpAddr = 0xdeadbeef;
        int loggedPacketCount = 0;

        void AppendLog(LogInfo s)
        {
            if (enableLogging)
            {
                stream.WriteLine(s.ToString());

                writerLines++;
                if (writerLines % 5 == 0) stream.Flush();
            }
        }
        System.Security.Cryptography.MD5 hash = System.Security.Cryptography.MD5.Create();
        void AppendLog(int id, params string[] elements)
        {
            if (enableLogging)
            {
                var log = id + "|" + DateTime.Now.ToUniversalTime().ToString("yyyy'-'MM'-'dd'T'HH':'mm':'ss'.'fff'Z'") + "|" + String.Join("|", elements);
                var logHash = string.Concat(hash.ComputeHash(System.Text.Encoding.Unicode.GetBytes(log)).Select(x => x.ToString("x2")));
                stream.WriteLine(log + "|" + logHash);
                onLogAppend?.Invoke(log + "\n");

                writerLines++;
                if (writerLines % 5 == 0) stream.Flush();
            }
        }
        void DoDebugLog(byte[] bytes)
        {
            if (debugLog)
            {
                if (logger == null)
                {
                    logStream = new FileStream(fileName.Replace(".log", ".bin"), FileMode.Create);
                    logger = new BinaryWriter(logStream);
                }
                logger.Write(BitConverter.GetBytes(DateTime.Now.ToBinary()).Concat(BitConverter.GetBytes(bytes.Length)).Concat(bytes).ToArray());
            }
        }

        void Device_OnPacketArrival_machina(Machina.Infrastructure.TCPConnection connection, byte[] bytes)
        {
            if (tcp == null) return; // To avoid any late delegate calls causing state issues when listener uninstalled
            lock (lockPacketProcessing)
            {
                if (connection.RemotePort != 6040) return;
                var srcAddr = connection.RemoteIP;
                if (srcAddr != currentIpAddr)
                {
                    if (currentIpAddr == 0xdeadbeef || (bytes.Length > 4 && GetOpCode(bytes) == OpCodes.PKTAuthTokenResult && bytes[0] == 0x1e))
                    {
                        onNewZone?.Invoke();
                        currentIpAddr = srcAddr;
                    }
                    else return;
                }
                DoDebugLog(bytes);
                ProcessPacket(bytes.ToList());
            }
        }
        void Device_OnPacketArrival_pcap(object sender, PacketCapture evt)
        {
            if (pcap == null) return;
            lock (lockPacketProcessing)
            {
                var rawpkt = evt.GetPacket();
                var packet = PacketDotNet.Packet.ParsePacket(rawpkt.LinkLayerType, rawpkt.Data);
                var ipPacket = packet.Extract<PacketDotNet.IPPacket>();
                var tcpPacket = packet.Extract<PacketDotNet.TcpPacket>();
                var bytes = tcpPacket.PayloadData;

                if (tcpPacket != null)
                {
                    if (tcpPacket.SourcePort != 6040) return;
#pragma warning disable CS0618 // Type or member is obsolete
                    var srcAddr = (uint)ipPacket.SourceAddress.Address;
#pragma warning restore CS0618 // Type or member is obsolete
                    if (srcAddr != currentIpAddr)
                    {
                        if (currentIpAddr == 0xdeadbeef || (bytes.Length > 4 && GetOpCode(bytes) == OpCodes.PKTAuthTokenResult && bytes[0] == 0x1e))
                        {
                            onNewZone?.Invoke();
                            currentIpAddr = srcAddr;
                        }
                        else return;
                    }
                    DoDebugLog(bytes);
                    ProcessPacket(bytes.ToList());
                }
            }
        }
        private void Parser_onDamageEvent(LogInfo log)
        {
            currentEncounter.Infos.Add(log);
        }
        private void Parser_onNewZone()
        {
        }
        public void UninstallListeners()
        {
            logger?.Dispose();
            logStream?.Dispose();
            if (tcp != null) tcp.Stop();
            if (pcap != null)
            {
                try
                {
                    pcap.StopCapture();
                    pcap.Close();
                }
                catch (Exception ex)
                {
                    var exceptionMessage = "Exception while trying to stop capture on NIC " + pcap.Name + "\n" + ex.ToString();
                    Console.WriteLine(exceptionMessage);
                    AppendLog(0, exceptionMessage);
                }
            }
            tcp = null;
            pcap = null;
        }

        public void Dispose()
        {
            if (stream != null)
            {
                stream.Flush();
                stream.Close();
            }
        }
    }
}

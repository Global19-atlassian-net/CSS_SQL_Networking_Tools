﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.IO;
using System.Data;
using System.Collections;

namespace SQLNA
{

    //
    // Written by the Microsoft CSS SQL Networking Team
    //
    // Base parser routines.
    // Takes filespec and converts into an array of files if any wildcards are present
    // For each file:
    //     Opens the file and reads the magic number to determine .CAP, .PCAP, and .PCAPNG regardless of actual file extension or lack thereof. Honors .ETL extension.
    //     Selects the appropriate file parser
    //     Gets the timestamp of the first frame
    // Sorts timestamps and opens the files in timestamp order regardless of filename
    // Selects the appropriate file parser and reads each fram
    // If the frame type is Ethernet, parses the frame
    // If the address type is IPV4 or IPV6, parses it, and reads any shim protocols
    // If the protocol type is TCP or UDP, parses it
    // Tries to find SQL
    // Marks continuation frames
    // Marks retransmitted frames
    // If SQL is accidentally marked as the client-side, reverses the source and destination IP addresses and ports for every frame in the conversation
    //

    class Parser
    {

        const long BYTES_PER_FRAME = 200;
        const long BYTES_PER_CONVERSATION = 50000;

        const int BACK_COUNT_LIMIT = 20;

        public static void ParseFileSpec(string fileSpec, NetworkTrace t)
        {
            string[] files = null;
            FileInfo fi = null;
            DataTable dt = new DataTable();
            DataRow dr = null;
            DataRow[] rows = null;
            long totalSize = 0;

            dt.Columns.Add("FileName", typeof(String));
            dt.Columns.Add("FileDate", typeof(DateTime));
            dt.Columns.Add("FileSize", typeof(long));
            dt.Columns.Add("InitialTick", typeof(long));

            ActivityTimer act = new ActivityTimer();

            try
            {
                // Use the FileInfo class to get the directory name and the length of the file name with wild cards so we can split it out
                // The class does not support wild card characters, so replace * and ? with a letter
                fi = new FileInfo(fileSpec.Replace("*", "s").Replace("?", "q"));
                files = Directory.GetFiles(fi.DirectoryName, fileSpec.Substring(fileSpec.Length - fi.Name.Length));

                //Enumerate the files that match the file specification

                foreach (String f in files)
                {
                    dr = dt.NewRow();
                    fi = new FileInfo(f);
                    dr["FileName"] = f;
                    dr["FileDate"] = fi.LastWriteTime;
                    dr["FileSize"] = fi.Length;
                    dr["InitialTick"] = GetInitialTick(f);
                    totalSize = fi.Length;
                    dt.Rows.Add(dr);
                }
            }
            catch (Exception ex)
            {
                Program.logDiagnostic("Error getting file information: " + ex.Message + "\r\n" + ex.StackTrace);
                Console.WriteLine("Error getting file information: " + ex.Message + "\r\n" + ex.StackTrace);
            }

            // order by last write time - first to last
            rows = dt.Select("", "InitialTick");   // changed from FileDate - have seen cases where the files get touched by UDE
            
            // size ArrayLists based on total size of all files - a guestimate to reduce the number of times the ArrayList must be grown
            t.frames = new System.Collections.ArrayList((int)(totalSize / BYTES_PER_FRAME));
            t.conversations = new System.Collections.ArrayList((int)(totalSize / BYTES_PER_CONVERSATION));
            t.files = new System.Collections.ArrayList(rows.Length);

            Console.WriteLine("Trace file(s) folder:\n" + Path.GetDirectoryName(fileSpec) + "\n");
            // Parse each file in the list
            foreach (DataRow r in rows)
            {
                String fn = r["FileName"].ToString().ToLower();
                // add the file name to the files collection
                FileData f = new FileData();
                f.filePath = fn;
                f.fileDate = (DateTime)r["FileDate"];
                f.fileSize = (long)r["FileSize"];
                t.files.Add(f);
                act.start("Parsing " + Path.GetFileName(fn));
                ParseOneFile(fn, t);
                act.stop();
            }
        }

        public static long GetInitialTick(string filePath)
        {
            BinaryReader r = null;
            ReaderBase rb = null;
            ETLFileReader er = null;
            long initialTick = 0;

            Program.logDiagnostic("Peeking at initial tick for file " + filePath);

            try
            {
                if (filePath.ToLower().EndsWith(".etl"))  // ETL files have no magic number. Must be done by file name.
                {
                    er = new ETLFileReader(filePath);
                    initialTick = er.GetStartTime().Ticks;
                }
                else
                {
                    r = new BinaryReader(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read));
                    UInt32 magicNumber = r.ReadUInt32();

                    switch (magicNumber)
                    {
                        case 0x55424d47:  // NETMON magic number
                            {
                                rb = new NetMonReader(r);
                                break;
                            }
                        case 0xa1b2c3d4:  // PCAP normal byte order   - millisecond resolution
                        case 0xd4c3b2a1:  // PCAP reversed byte order - millisecond resolution
                        case 0xa1b23c4d:  // PCAP normal byte order   - nanosecond resolution
                        case 0x4d3cb2a1:  // PCAP reversed byte order - nanosecond resolution
                            {
                                rb = new SimplePCAPReader(r);
                                break;
                            }
                        case 0x0A0D0D0A:  // PCAPNG Section Header Block identifier. Magic number is at file offset 8.
                            {
                                rb = new PcapNGReader(r);
                                break;
                            }
                        default:
                            {
                                throw new Exception("Magic number " + magicNumber.ToString("X") + " does not correspond to a supported network trace file type.");
                            }
                    }

                    rb.Init();   // reads file header information

                    Frame frame = rb.Read();   // reads one frame; returns null at EOF

                    if (frame != null) initialTick = frame.ticks;   // extract tick information
                }

            }
            catch (Exception ex)
            {
                Program.logDiagnostic("Error reading file " + filePath + "\r\n" + ex.Message + "\r\n" + ex.StackTrace);
                Console.WriteLine("Error reading file " + filePath + "\r\n" + ex.Message + "\r\n" + ex.StackTrace);
            }
            finally
            {
                if (r != null) r.Close();
                if (er != null) er.Close();
        }

            return initialTick;
        }

        public static void ParseOneFile(string filePath, NetworkTrace t)
        {
            BinaryReader r = null;
            ReaderBase rb = null;
            ETLFileReader er = null;
            bool isETL = filePath.ToLower().EndsWith(".etl");   // ETL files have no maging number. Must be done by file name.
            Frame frame = null;

            bool f_ReportedWifi = false;
            bool f_ReportedNetEvent = false;
            bool f_ReportedOther = false;

            FileData file = (FileData)t.files[t.files.Count - 1];
            
            try
            {
                if (isETL)
                {
                    er = new ETLFileReader(filePath);
                    er.Init();
                    frame = er.Read();
                }
                else
                {
                    r = new BinaryReader(new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read));
                    UInt32 magicNumber = r.ReadUInt32();

                    switch (magicNumber)
                    {
                        case 0x55424d47:  // NETMON magic number
                            {
                                rb = new NetMonReader(r);
                                break;
                            }
                        case 0xa1b2c3d4:  // PCAP normal byte order   - millisecond resolution
                        case 0xd4c3b2a1:  // PCAP reversed byte order - millisecond resolution
                        case 0xa1b23c4d:  // PCAP normal byte order   - nanosecond resolution
                        case 0x4d3cb2a1:  // PCAP reversed byte order - nanosecond resolution
                            {
                                rb = new SimplePCAPReader(r);
                                break;
                            }
                        case 0x0A0D0D0A:  // PCAPNG Section Header Block identifier. Magic number is at file offset 8.
                            {
                                rb = new PcapNGReader(r);
                                break;
                            }
                        default:
                            {
                                throw new Exception("Magic number " + magicNumber.ToString("X") + " does not correspond to a supported network trace file type.");
                            }
                    }

                    rb.Init();   // reads file header information

                    frame = rb.Read();   // reads one frame; returns null at EOF
                }

                while (frame != null)
                {
                    if (frame.ticks >= DateTime.MinValue.Ticks && frame.ticks <= DateTime.MaxValue.Ticks)
                    {
                        FrameData f = new FrameData();
                        f.frameNo = frame.frameNumber;
                        f.file = file;
                        f.ticks = frame.ticks;

                        f.frameLength = frame.frameLength;
                        f.capturedFrameLength = frame.bytesAvailable;

                        if (file.startTick == 0) file.startTick = frame.ticks;
                        if (frame.ticks > file.endTick) file.endTick = frame.ticks;
                        file.frameCount++;

                        switch (frame.linkType)
                        {
                            case 1:  // Ethernet
                                {
                                    ParseEthernetFrame(frame.data, t, f);
                                    break;
                                }
                            case 6:  // WiFi
                                {
                                    ParseWifiFrame(frame.data, t, f); // TODO flesh this out
                                    // Test file: \Documents\Interesting Network Traces\WifiTrace\
                                    if (!f_ReportedWifi)
                                    {
                                        Program.logDiagnostic($"Frame {frame.frameNumber}: Wifi detected but not yet supported. Packet ignored.");
                                        f_ReportedWifi = true;
                                    }
                                    break;
                                }
                            case 0xFFE0:  // NetEvent (usually in ETL and parsed by now)
                                {
                                    ParseNetEventFrame(frame.data, t, f); // TODO flesh this out
                                    // Test file: \Documents\Interesting Network Traces\Filtered ETL in a CAP File - fix SQLNA\*_filtered.cap
                                    if (!f_ReportedNetEvent)
                                    {
                                        Program.logDiagnostic($"Frame {frame.frameNumber}: NetEvent detected but not yet supported. Packet ignored.");
                                        f_ReportedNetEvent = true;
                                    }
                                    break;
                                }
                            default:
                                {
                                    if (!f_ReportedOther)
                                    {
                                        Program.logDiagnostic($"Frame {frame.frameNumber}: Unknown Protocol {frame.linkType} (0x{frame.linkType.ToString("X4")}). Packet ignored.");
                                        f_ReportedOther = true;
                                    }
                                    break;
                                }
                        }
                        
                    }
                    else // corrupt packet - bad timestamp - log and drop
                    {
                        Program.logDiagnostic("Bad timestamp. Dropping frame " + frame.frameNumber + " in file " + file.filePath + ".");
                    }

                    if (isETL) frame = er.Read(); else frame = rb.Read();
                }
            }
            catch (Exception ex)
            {
                Program.logDiagnostic("Error reading file " + filePath + "\r\n" + ex.Message + "\r\n" + ex.StackTrace);
                Console.WriteLine("Error reading file " + filePath + "\r\n" + ex.Message + "\r\n" + ex.StackTrace);
            }
            finally
            {
                if (r != null) r.Close();
                if (er != null) er.Close();
            }
        }

        public static void ParseNetEventFrame(byte[] b, NetworkTrace t, FrameData f) // TODO
        {
            // Can call either ParseEthernetFrame or ParseWifiFrame depending on the link type
            // Copy code from the ETLFileReader.TraceEvent_EventCallback method
        }

        public static void ParseEthernetFrame(byte[] b, NetworkTrace t, FrameData f)
        {
            ulong sourceMAC = 0;
            ulong destMAC = 0;
            ushort NextProtocol = 0;    // IPV4 = 0x0800 (2048)    IPV6 = 0x86DD (34525)     VLAN = 0x8100 inserts 4 bytes at offset 12
            ushort NextProtocolOffset = 0;

            destMAC = utility.B2UInt48(b, 0);
            sourceMAC = utility.B2UInt48(b, 6);
            NextProtocol = utility.B2UInt16(b, 12);
            NextProtocolOffset = 14;


            // VLAN detection - original
            //if (NextProtocol == 0x8100)
            //{
            //    NextProtocol = utility.B2UInt16(b, 16);
            //    NextProtocolOffset = 18;
            //}

            // VLAN detection - may have more than one shim
            while (NextProtocol == 0x8100)
            {
                NextProtocol = utility.B2UInt16(b, NextProtocolOffset + 2);
                NextProtocolOffset += 4;
            }

            try
            {
                if (NextProtocol == 0x800)
                {
                    ParseIPV4Frame(b, NextProtocolOffset, t, f);
                }
                else if (NextProtocol == 0x86DD)
                {
                    ParseIPV6Frame(b, NextProtocolOffset, t, f);
                }
            }
            catch (IndexOutOfRangeException)
            {
                if (f.conversation != null) f.conversation.truncationErrorCount++;
            }
            catch { throw; }

            if (NextProtocol == 0x800 || NextProtocol == 0x86DD)
            {
                if (f.conversation != null)
                {
                    f.conversation.sourceMAC = sourceMAC;
                    f.conversation.destMAC = destMAC;
                    // statistical gathering
                    if (f.conversation.startTick == 0 || f.ticks < f.conversation.startTick)
                    {
                        f.conversation.startTick = f.ticks;
                    }
                    if (f.conversation.endTick < f.ticks) f.conversation.endTick = f.ticks;
                    if (f.isFromClient) f.conversation.sourceFrames++; else f.conversation.destFrames++;
                    f.conversation.totalBytes += (ulong)b.Length;
                }
            }
        }

        public static void ParseWifiFrame(byte[] b, NetworkTrace t, FrameData f)
        {
            // TODO - get the frame header details
        }

        public static void ParseIPV4Frame(byte[] b, int offset, NetworkTrace t, FrameData f)
        {
            ushort HeaderLength = 0;
            ushort Length = 0;
            byte NextProtocol = 0;     // TCP = 6    UDP = 0x11 (17)
            uint sourceIP = 0;
            uint destIP = 0;
            ushort SPort = 0;
            ushort DPort = 0;

            HeaderLength = (ushort)((b[offset] & 0xf) * 4);
            Length = utility.B2UInt16(b, offset + 2);
            NextProtocol = b[offset + 9];
            sourceIP = utility.B2UInt32(b, offset + 12);
            destIP = utility.B2UInt32(b, offset + 16);

            // determine the last element of b[] that contains IPV4 data - also the last byte of TCP payload - ethernet may extend beyond this
            if (Length == 0)
            {
                f.lastByteOffSet = (ushort)(b.Length - 1);
            }
            else
            {
                f.lastByteOffSet = (ushort)(offset + Length - 1);
            }

            if (NextProtocol == 41)   // IPV6 over IPV4 - ignore eveything but the NextProtcol; extend the IPV4 header by 40
            {
                NextProtocol = b[offset + HeaderLength + 6];
                HeaderLength += 40;           // ignore frames with IPV6 header extensions for now
            }

            if (NextProtocol == 50)   // ESP
            {
                try
                {
                    ushort ESPTrailerLength = GetESPTrailerLength(b, offset + HeaderLength, f.lastByteOffSet, ref NextProtocol);
                    f.lastByteOffSet -= ESPTrailerLength;   // account for the trailer length
                    offset += 8;                            // account for the header length
                }
                catch (Exception)
                {
                    Program.logDiagnostic("Frame " + f.frameNo + " has an unknwn ESP trailer. Ignored.");
                    NextProtocol = 0; // don't parse this frame
                }
            }

            if (NextProtocol == 51)   // AH = Authentication Header
            {
                NextProtocol = b[offset + HeaderLength];
                HeaderLength += (ushort)(b[offset + HeaderLength + 1] * 4 + 8);
            }

            if (NextProtocol == 6 || NextProtocol == 0x11)  // TCP | UDP
            {
                // sneak a peek into the TCP or UDP header for port numbers so we can add the conversation row to the frame data at this time
                // fortunately, the port numbers are in the same location for both protocols
                SPort = utility.B2UInt16(b, offset + HeaderLength);
                DPort = utility.B2UInt16(b, offset + HeaderLength + 2);
                ConversationData c = t.GetIPV4Conversation(sourceIP, SPort, destIP, DPort);
                //
                // Determine whether the TCP client port has rolled around and this should be a new conversation
                //
                // The rule is if we see a SYN packet, then is there a RESET or FIN packet already in the conversation, and is it older than 20 seconds. If so, then new conversation.
                //
                if (NextProtocol == 6) // TCP
                {
                    f.flags = b[offset + HeaderLength + 13];
                    if ((f.flags & (byte)TCPFlag.SYN) != 0 && (c.finCount > 0 || (c.resetCount > 0) && (f.ticks - ((FrameData)(c.frames[c.frames.Count - 1])).ticks) > 20 * utility.TICKS_PER_SECOND))
                    {
                        ConversationData cOld = c;
                        c = new ConversationData();  // TODO Where do we add this to the network trace ???
                        c.sourceIP = cOld.sourceIP;
                        c.sourceIPHi = cOld.sourceIPHi;
                        c.sourceIPLo = cOld.sourceIPLo;
                        c.sourcePort = cOld.sourcePort;
                        c.destMAC = cOld.destMAC;
                        c.destIP = cOld.destIP;
                        c.destIPHi = cOld.destIPHi;
                        c.destIPLo = cOld.destIPLo;
                        c.destPort = cOld.destPort;
                        c.isIPV6 = cOld.isIPV6;
                        c.startTick = f.ticks;
                        c.endTick = f.ticks;
                        if (f.isFromClient) c.sourceFrames++; else c.destFrames++;
                        c.totalBytes += (ulong)b.Length;
                        ArrayList conv = t.GetConversationList((ushort)(c.sourcePort ^ c.destPort));   // XOR the port numbers together to generate an index into conversationIndex
                        conv.Add(c);
                        t.conversations.Add(c);
                    }
                }
                c.nextProtocol = NextProtocol;
                if (c.truncatedFrameLength == 0 && f.capturedFrameLength != f.frameLength)
                {
                    c.truncatedFrameLength = f.capturedFrameLength;
                }
                f.conversation = c;
                t.frames.Add(f);
                c.frames.Add(f);


                //// determine the last element of b[] that contains IPV4 data - also the last byte of TCP payload - ethernet may extend beyond this
                //if (Length == 0)
                //{
                //    f.lastByteOffSet = (ushort)(b.Length - 1);
                //}
                //else
                //{
                //    f.lastByteOffSet = (ushort)(offset + Length - 1);
                //}


                //Is the Frame from Client or Server?
                if (sourceIP == c.sourceIP)
                    f.isFromClient = true;
             }

            if (NextProtocol == 6)
            {
                ParseTCPFrame(b, offset + HeaderLength, t, f);
            }
            else if (NextProtocol == 0x11)
            {
                ParseUDPFrame(b, offset + HeaderLength, t, f);
            };
        }

        public static void ParseIPV6Frame(byte[] b, int offset, NetworkTrace t, FrameData f)
        {
            ushort HeaderLength = 40;   // we ignore packets with header extensions right now ... http://en.wikipedia.org/wiki/IPv6_packet
            ushort PayloadLength = 0;
            byte NextProtocol = 0;     // TCP = 6    UDP = 0x11 (17)
            ulong sourceIPHi = 0;
            ulong sourceIPLo = 0;
            ulong destIPHi = 0;
            ulong destIPLo = 0;
            ushort SPort = 0;
            ushort DPort = 0;

            PayloadLength = utility.B2UInt16(b, offset + 4);
            NextProtocol = b[offset + 6];
            sourceIPHi = utility.B2UInt64(b, offset + 8);
            sourceIPLo = utility.B2UInt64(b, offset + 16);
            destIPHi = utility.B2UInt64(b, offset + 24);
            destIPLo = utility.B2UInt64(b, offset + 32);

            // determine the last element of b[] that contains IPV4 data - also the last byte of TCP payload - ethernet may extend beyond this
            if (PayloadLength == 0)
            {
                f.lastByteOffSet = (ushort)(b.Length - 1);
            }
            else
            {
                f.lastByteOffSet = (ushort)(offset + HeaderLength + PayloadLength - 1);  // added HeaderLength
            }

            if (NextProtocol == 50)   // ESP
            {
                try
                {
                    ushort ESPTrailerLength = GetESPTrailerLength(b, offset + HeaderLength, f.lastByteOffSet, ref NextProtocol);
                    f.lastByteOffSet -= ESPTrailerLength;   // account for the trailer length
                    offset += 8;                            // account for the header length
                }
                catch (Exception)
                {
                    Program.logDiagnostic("Frame " + f.frameNo + " has an unknwn ESP trailer. Ignored.");
                    NextProtocol = 0; // don't parse this frame
                }
            }

            if (NextProtocol == 51)   // AH = Authentication Header
            {
                NextProtocol = b[offset + HeaderLength];
                HeaderLength += (ushort)(b[offset + HeaderLength + 1] * 4 + 8);
            }

            if (NextProtocol == 6 || NextProtocol == 0x11)
            {
                // sneak a peek into the TCP or UDP header for port numbers so we can add the conversation row to the frame data at this time
                // fortunately, the port numbers are in the same location for both protocols
                SPort = utility.B2UInt16(b, offset + HeaderLength);
                DPort = utility.B2UInt16(b, offset + HeaderLength + 2);
                ConversationData c = t.GetIPV6Conversation(sourceIPHi, sourceIPLo, SPort, destIPHi, destIPLo, DPort);
                //
                // Determine whether the TCP client port has rolled around and this should be a new conversation
                //
                // The rule is if we see a SYN packet, then is there a RESET or FIN packet already in the conversation, and is it older than 20 seconds. If so, then new conversation.
                //
                if (NextProtocol == 6) // TCP
                {
                    f.flags = b[offset + HeaderLength + 13];
                    if ((f.flags & (byte)TCPFlag.SYN) != 0 && (c.finCount > 0 || (c.resetCount > 0) && (f.ticks - ((FrameData)(c.frames[c.frames.Count - 1])).ticks) > 20 * utility.TICKS_PER_SECOND))
                    {
                        ConversationData cOld = c;
                        c = new ConversationData();
                        c.sourceIP = cOld.sourceIP;
                        c.sourceIPHi = cOld.sourceIPHi;
                        c.sourceIPLo = cOld.sourceIPLo;
                        c.sourcePort = cOld.sourcePort;
                        c.destMAC = cOld.destMAC;
                        c.destIP = cOld.destIP;
                        c.destIPHi = cOld.destIPHi;
                        c.destIPLo = cOld.destIPLo;
                        c.destPort = cOld.destPort;
                        c.isIPV6 = cOld.isIPV6;
                        c.startTick = f.ticks;
                        c.endTick = f.ticks;
                        if (f.isFromClient) c.sourceFrames++; else c.destFrames++;
                        c.totalBytes += (ulong)b.Length;
                        ArrayList conv = t.GetConversationList((ushort)(c.sourcePort ^ c.destPort));   // XOR the port numbers together to generate an index into conversationIndex
                        conv.Add(c);
                        t.conversations.Add(c);
                    }
                }
                c.nextProtocol = NextProtocol;
                if (c.truncatedFrameLength == 0 && f.capturedFrameLength != f.frameLength)
                {
                    c.truncatedFrameLength = f.capturedFrameLength;
                }
                f.conversation = c;
                t.frames.Add(f);
                c.frames.Add(f);

                //Is the Frame from Client or Server?
                if (sourceIPHi == c.sourceIPHi && sourceIPLo == c.sourceIPLo)
                    f.isFromClient = true;
            }

            switch (NextProtocol)
            {
                case 6:   //TCP
                    {
                        ParseTCPFrame(b, offset + HeaderLength, t, f);
                        break;
                    }
                case 0x11:    // UDP
                    {
                        ParseUDPFrame(b, offset + HeaderLength, t, f);
                        break;
                    }
                case 0:
                case 60:
                case 43:
                case 44:
                case 51:
                case 135:
                    {
                        Program.logDiagnostic("Warn: IPV6 packet has extension header " + NextProtocol + ". Frame " + f.frameNo + " in " + f.file.filePath);
                        break;
                    }
                default:
                    {
                        // Program.logDiagnostic("Ignored protocol " + NextProtocol.ToString());
                        break;
                    }
            }
       }

        public static ushort GetESPTrailerLength(byte[] b, int offset, int LastByteOffset, ref byte NextProtocol)
        {
            // no direct way to tell the length of the security BLOB, it is either 12 or 16 bytes long
            ushort secLen = 12;
            NextProtocol = b[LastByteOffset - secLen];
            byte padLen = b[LastByteOffset - secLen - 1];
            if (ESPOffsetOkay(b, LastByteOffset - secLen - 2, padLen) == false)  // try 16
            {
                secLen = 16;
                NextProtocol = b[LastByteOffset - secLen];
                padLen = b[LastByteOffset - secLen - 1];
                if (ESPOffsetOkay(b, LastByteOffset - secLen - 2, padLen) == false) throw new Exception("Invalid ESP protocol security trailer.");
            }
            // Program.logDiagnostic("ESP Trailer Length = " + (secLen + 2 + padLen) + ". Next Protocol = " + NextProtocol);
            return (ushort)(secLen + 2 + padLen);
        }

        public static bool ESPOffsetOkay(byte[] b, int offset, byte padLen)
        {
            for (int i = 0; i < padLen; i++)
            {
                if (b[offset - i] != (padLen - i)) return false;  // padding is 1, 2, 3, 4, 5, ...
            }
            return true;
        }

        public static void ParseTCPFrame(byte[] b, int offset, NetworkTrace t, FrameData f)
        {
            int headerLength = (b[offset + 12] >> 4) * 4; // upper nibble * 4
            int smpLength = 0;

            // port number offsets handled in IPV4 and IPV6 parsers in order to create the ConversationData object
            f.seqNo = utility.B2UInt32(b, offset + 4);
            f.ackNo = utility.B2UInt32(b, offset + 8);
            f.flags = b[offset + 13];
            f.windowSize = utility.B2UInt16(b, offset + 14);

            // raw payload length
            int payloadLen = f.lastByteOffSet - offset - headerLength + 1;

            //TCPPayload may have SMP header before TDS - 16 bytes, begins with byte 0x53 (83 decimal)
            if ((payloadLen > 15) && (b[offset + headerLength] == 0x53))
            {
                smpLength = 16;
                f.conversation.isMARSEnabled = true;
                f.smpSession = utility.ReadUInt16(b, offset + headerLength + 2);   // so we can tell different conversations apart when parsing TDS
                // Program.logDiagnostic("Removed SMP header from frame " + f.frameNo.ToString());  // debug output
            }

            // captured payload length may be less than actual frame length
            if (f.lastByteOffSet >= b.Length) f.lastByteOffSet = (ushort)(b.Length - 1); // last element position = Length - 1

            //TCPPayload/may be TDS
            // recalculate payload length to account for possible SMP header
            payloadLen = f.lastByteOffSet - offset - headerLength - smpLength + 1;
            if (payloadLen > 0)
            {
                f.payload = new byte[payloadLen];
                Array.Copy(b, offset + headerLength + smpLength, f.payload, 0, payloadLen);
            }

            // conversation statistics
            if ((f.flags & (byte)TCPFlag.FIN) != 0)
            {
                f.conversation.finCount++;
                if (f.conversation.FinTime == 0) f.conversation.FinTime = f.ticks;
            }
            if ((f.flags & (byte)TCPFlag.SYN) != 0) f.conversation.synCount++;
            if ((f.flags & (byte)TCPFlag.RESET) != 0)
            {
                f.conversation.resetCount++;
                if (f.conversation.ResetTime == 0) f.conversation.ResetTime = f.ticks;
            }
            if ((f.flags & (byte)TCPFlag.PUSH) != 0) f.conversation.pushCount++;
            if ((f.flags & (byte)TCPFlag.ACK) != 0) f.conversation.ackCount++;

            // keep alive - ACK packet has a 1 byte payload that equals 0
            if ((f.payloadLength == 1) &&
                (f.payload[0] == 0) &&
                ((f.flags & (byte)TCPFlag.ACK) != 0) &&
                ((f.flags & (byte)(TCPFlag.FIN | TCPFlag.FIN | TCPFlag.SYN | TCPFlag.RESET | TCPFlag.PUSH)) == 0))
            {
                f.conversation.keepAliveCount++;
            }

        }

        public static void ParseUDPFrame(byte[] b, int offset, NetworkTrace t, FrameData f)
        {
            f.conversation.isUDP = true;
            f.isUDP = true;

            //if (f.conversation.sourcePort == 61591) // for debugging purposes only
            //{
            //    Console.WriteLine();
            //}

            // captured payload length may be less than actual frame length
            if (f.lastByteOffSet >= b.Length) f.lastByteOffSet = (ushort)(b.Length - 1); // last element position = Length - 1

            int payloadLen = f.lastByteOffSet - offset - 8 + 1;  // 8 is UDP header length
            if (payloadLen > 0)
            {
                f.payload = new byte[payloadLen];
                Array.Copy(b, offset + 8, f.payload, 0, payloadLen);   // 8 is UDP header length
            }

        }

        // a post processing parser - do first
        public static void ReverseBackwardConversations(NetworkTrace t)
        {
            foreach (ConversationData c in t.conversations)
            {
                FrameData f = (FrameData)c.frames[0];  // get first frame
                //
                // tests are done this way because the E flag may be set occasionally and must not let that interfere with the comparison
                //
                if (((f.flags & (byte)(TCPFlag.SYN)) != 0) && ((f.flags & (byte)(TCPFlag.ACK)) == 0) && !f.isFromClient)
                {
                    TDSParser.reverseSourceDest(c);    // if the first packet is SYN and from the server - reverse the conversation
                }
                else if (((f.flags & (byte)(TCPFlag.SYN)) != 0) && ((f.flags & (byte)(TCPFlag.ACK)) != 0) && f.isFromClient)
                {
                    TDSParser.reverseSourceDest(c);    // if the first packet is ACK+SYN and from client - reverse the conversation
                }
            }
        }

        // a post processing parser
        public static void FindRetransmits(NetworkTrace t)
        {
            int payloadLen = 0;
            int priorPayloadLen = 0;

            foreach (ConversationData c in t.conversations)
            {
                for (int i = 0; i < c.frames.Count; i++) // process the frames in the current conversation in ascending order
                {
                    FrameData f = (FrameData)c.frames[i];
                    int backCount = 0;
                    payloadLen = f.payloadLength;
                    if (payloadLen < 8) continue;   // skip non-TDS packets, especially keep-alive ACKs that have a payload length of 1 - may skip a retransmit of a continuation packet with a small residual payload

                    for (int j = i - 1; j >= 0; j--) // look in descending order for the same sequence number and payload length
                    {
                        FrameData priorFrame = (FrameData)c.frames[j];
                        if (f.isFromClient == priorFrame.isFromClient)
                        {
                            backCount++;
                            priorPayloadLen = priorFrame.payloadLength;
                            if ((payloadLen == priorPayloadLen) &&
                                ((f.seqNo == priorFrame.seqNo) || ((f.seqNo > priorFrame.seqNo) && (f.seqNo < (priorFrame.seqNo + priorPayloadLen)))))
                            {
                                f.isRetransmit = true;
                                f.conversation.rawRetransmits++;
                                if (payloadLen > 1) f.conversation.sigRetransmits++;
                                break; // each frame locates one retransmit; if retransmitted multiple times, later retransmits will get counted on their own
                            }
                            if (backCount >= BACK_COUNT_LIMIT) break;  // none found in last 20 frames from the same side of the conversation
                        }
                    }
                }
            }
        }

        // a post processing parser - must be done after finding retransmits
        public static void FindContinuationFrames(NetworkTrace t)
        {
            foreach (ConversationData c in t.conversations)
            {
                for (int i = 0; i < c.frames.Count; i++) // process the frames in the current conversation in ascending order
                {
                    FrameData f = (FrameData)c.frames[i];
                    int backCount = 0;
                    if (f.payloadLength == 0) continue;   // not checking ACK packets

                    for (int j = i - 1; j >= 0; j--) // look in descending order for the same ack number - if push flag set, abort
                    {
                        FrameData priorFrame = (FrameData)c.frames[j];
                        if ((f.isFromClient == priorFrame.isFromClient)) // continuation frames have no SMP header, so no need to match on that field
                        {
                            backCount++;
                            if ((priorFrame.flags & (byte)TCPFlag.PUSH) > 0) break; // push flag indicates end of prior continuation, if any
                            if ((priorFrame.ackNo == f.ackNo) && (priorFrame.isRetransmit == false) && (priorFrame.payloadLength > 0))
                            {
                                f.isContinuation = true;
                                break;
                            }
                            if (backCount >= BACK_COUNT_LIMIT) break;  // none found in last 20 frames from the same side of the conversation
                        }  // if
                    }  // for
                }  // for
            }  // foreach
        }

    }  // end of class
}      // end of namespace

using System;
using System.Text;
using System.Threading.Tasks;
using PCCCImageTool.Models;

namespace PCCCImageTool.Services;

public static class PlcIdentifier
{

    /// <summary>
    /// Diagnostic Status response (CMD=0x06, FNC=0x03).
    /// Response CMD = 0x46 (0x06 | 0x40), sent WITHOUT FUNC byte.
    ///
    /// Inner frame layout (WithoutFunc):
    ///   [0]=DST [1]=SRC [2]=CMD [3]=STS [4]=TNS_LO [5]=TNS_HI [6]=DATA[0] ...
    ///
    /// PCCCComm reads ProcessorType from DataPackets[rTNS][9] = inner[9] = DATA[3].
    /// payload[3] = 0x49 (SLC 5/03) → ProcessorType = 0x49.
    ///
    /// Payload layout per Publication 1770-6.5.16 Chapter 10 (1747-L532):
    ///   Byte  0    : mode/status flags — bits 0-5 = 0, bit 6 = testing edits,
    ///                bit 7 = edits in processor. NOT the mode code.
    ///   Byte  1    : 0xEE — type extender
    ///   Byte  2    : 0x34 — extended interface type (DF1 full-duplex, port 0)
    ///   Byte  3    : 0x49 — extended processor type (1747-L534 rack, SLC 5/03)
    ///   Byte  4    : series/revision
    ///   Byte  5–15 : bulletin number "5/03" in ASCII, space-padded to 11 bytes
    ///   Byte 16–17 : major error word (0x0000 = no fault)
    ///   Byte 18    : processor mode status/control low byte — mode code bits 0-4
    ///                  0x11 = local PROG   0x1E = local RUN
    ///                  0x17 = TEST-cont    0x18 = TEST-single   0x19 = TEST-step
    ///   Byte 19    : processor mode status/control high byte — fault flags
    ///   Byte 20–21 : program ID
    ///   Byte 22    : RAM size in Kbytes — 0x10 for 1747-L532E (32K)
    ///   Byte 23    : flags (bits 2-7 = program owner node, 0x3F = no owner)
    ///                bit 0 = directory file corrupted
    /// </summary>
    public static async Task<PlcInfo> IdentifyAsync(global::PCCCComm.PCCCComm df1)
    {
        try
        {
            byte[]? diag = await Task.Run(() => df1.GetDiagnosticStatusRaw());
            if (diag == null || diag.Length < 4)
                return new PlcInfo(0, $"Identify error", false, "Unknown", string.Empty, 0, 0, "UNKNOWN");

            var family = global::PCCCComm.Pccc.PCCCConstants.DetectFamily(diag);

            int procType = 0x00;
            (string name, string familyStr) = ("Unknown", "Unknown");

            if (family == global::PCCCComm.Pccc.PCCCConstants.ProcessorFamily.SlcMicroLogix)
            {
                procType = await Task.Run(() => df1.GetProcessorType());
                (name, familyStr) = procType switch
                {
                    0x49 => ("SLC 5/03",        "SLC"),
                    0x5B => ("SLC 5/04",        "SLC"),
                    0x88 => ("SLC 5/01",        "SLC"),
                    0x89 => ("SLC 5/02",        "SLC"),
                    0x8C => ("MicroLogix 1500", "MicroLogix"),
                    0x9C => ("SLC 5/05",        "SLC"),
                    0xB0 => ("SLC 5/05",        "SLC"),
                    0xB1 => ("SLC 5/05",        "SLC"),
                    0xB2 => ("SLC 5/05",        "SLC"),
                    0x0D => ("SLC 5/05",        "SLC"),
                    0x13 => ("SLC 5/05",        "SLC"),
                    0x14 => ("SLC 5/05",        "SLC"),
                    0x15 => ("SLC 5/05",        "SLC"),
                    0x58 => ("MicroLogix 1000", "MicroLogix"),
                    0xB9 => ("MicroLogix 1100", "MicroLogix"),
                    0x90 => ("MicroLogix 1400", "MicroLogix"),
                    0x9F => ("MicroLogix 1400", "MicroLogix"),
                    _    => ($"Unknown (0x{procType:X2})", "Unknown")
                };
            }
            else if (family == global::PCCCComm.Pccc.PCCCConstants.ProcessorFamily.Plc5)
            {
                // PLC-5: expansion byte at index 2
                procType = diag[2];
                (name, familyStr) = procType switch
                {
                    0x15 => ("PLC-5/40B",       "PLC5"),
                    0x22 => ("PLC-5/10",        "PLC5"),
                    0x23 => ("PLC-5/60B",       "PLC5"),
                    0x28 => ("PLC-5/40L",       "PLC5"),
                    0x29 => ("PLC-5/60L",       "PLC5"),
                    0x31 => ("PLC-5/11",        "PLC5"),
                    0x32 => ("PLC-5/20",        "PLC5"),
                    0x33 => ("PLC-5/30",        "PLC5"),
                    0x4A => ("PLC-5/20E",       "PLC5"),
                    0x4B => ("PLC-5/40E",       "PLC5"),
                    0x55 => ("PLC-5/25",        "PLC5"),
                    0x59 => ("PLC-5/80E",       "PLC5"),
                    _    => ($"Unknown (0x{procType:X2})", "Unknown")
                };
            }

            // Defaults
            string bulletin = string.Empty;
            byte seriesRev = 0;
            byte ramKb = 0;
            string modeStr = "UNKNOWN";

            // Read diagnostic DATA[] once
            try
            {
                byte[]? data = await Task.Run(() => df1.GetDiagnosticStatusRaw());
                if (data != null && data.Length >= 16)
                {
                    // Extract bulletin (bytes 5-15) – same for both families
                    int bStart = 5;
                    int bLen = Math.Min(11, data.Length - bStart);
                    if (bLen > 0)
                        bulletin = Encoding.ASCII.GetString(data, bStart, bLen).Trim();

                    seriesRev = data.Length > 4 ? data[4] : (byte)0;
                    ramKb = data.Length > 22 ? data[22] : (byte)0;

                    // Mode decoding differs by family
                    if (family == global::PCCCComm.Pccc.PCCCConstants.ProcessorFamily.Plc5)
                    {
                        // PLC-5: operating status is at byte 0 (index 0)
                        byte modeCode = data.Length > 0 ? data[0] : (byte)0;
                        modeStr = DecodePlc5ModeString(modeCode);
                    }
                    else
                    {
                        // SLC/MicroLogix: mode code at byte 18
                        byte modeCode = data.Length > 18 ? data[18] : (byte)0;
                        modeStr = DecodeSlcModeString(modeCode);
                    }
                }
            }
            catch
            {
                // ignore
            }

            bool supports = familyStr is "SLC" or "MicroLogix" or "PLC5";
            return new PlcInfo(procType, name, supports, familyStr, bulletin, seriesRev, ramKb, modeStr);
        }
        catch (Exception ex)
        {
            return new PlcInfo(0, $"Identify error: {ex.Message}", false, "Unknown", string.Empty, 0, 0, "UNKNOWN");
        }
    }

    // For SLC/MicroLogix
    public static string DecodeSlcModeString(byte modeCode) => modeCode switch
    {
        0x1E => "RUN",   // local RUN  (pub. 1770-6.5.16 §10)
        0x06 => "RUN",   // remote RUN (observed on SLC 5/03, not documented in pub)
        0x11 => "PROG",  // local PROG
        0x01 => "PROG",  // remote PROG
        0x17 => "TEST",  // TEST-continuous
        0x18 => "TEST",  // TEST-single step
        0x19 => "TEST",  // TEST-step
        0x21 => "PROG",  // remote PROG
        0x26 => "RUN",   // remote RUN
        0x31 => "PROG",  // local PROG
        0x3E => "RUN",   // local RUN
        _    => "PROG"   // default safe assumption
    };

    // For PLC-5
    public static string DecodePlc5ModeString(byte operatingStatus) => operatingStatus switch
    {
        0x02 => "RUN",   // Local Run
        0x06 => "RUN",   // Remote Run
        0x00 => "PROG",  // Program Load
        0x04 => "PROG",  // Remote Program
        _    => "PROG"
    };
}

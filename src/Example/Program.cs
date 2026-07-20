// SPDX-License-Identifier: GPL-3.0-or-later
//
// PCCCComm - PCCC Communication Library for .NET
// Copyright (c) 2026 Ketut Kumajaya
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program. If not, see <https://www.gnu.org/licenses/>.

// =============================================================================
// PCCCComm Example Client
//
// Purpose
// -------
// This program demonstrates and tests the PCCCComm library against a real PLC
// or the PCCCEmulator. It is intended as a reference for developers adopting
// PCCCComm in their own projects.
//
// Structure
// ---------
//   1. Program class — entry point, argument parsing, transport construction
//   2. Demo         — read/write showcase executed by default on startup
//   3. Stress test  — continuous read loop for throughput and stability testing
//   4. Interactive CLI — command prompt for manual exploration
//   5. Self-test suite — exhaustive pass/fail tests invoked via "selftest" CLI command
//   6. Statistics helpers — counters tracked across demo and stress test
//   7. Low-level helpers — raw PDU builder, port name normalizer, usage printers
//
// Transport modes
// ---------------
//   df1       DF1 full-duplex serial (default)
//   df1master DF1 half-duplex master over RS-485
//   eip       EtherNet/IP (PCCC-over-CIP) over TCP
//   csp       CSPv4 (Client Server Protocol) over TCP
//
// Caution — real PLC hazard
// -------------------------
// This client WRITES data to the connected device (N7, F8, B3, mode changes).
// Only connect to a real PLC if you fully understand the consequences.
// Use the PCCCEmulator for safe testing.
// =============================================================================

using System.Diagnostics;
using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using Comm = PCCCComm;

// =============================================================================
// SECTION 1 — Program entry point, argument parsing, transport construction
// =============================================================================

class Program
{
    static void Main(string[] args)
    {
        if (!TryParseArgs(args, out var cfg)) return;
        Comm.PCCCComm pccc = BuildTransport(cfg);
        try
        {
            pccc.OpenComms();
            if      (cfg.Transport == "eip") Console.WriteLine($"EIP session established with {cfg.RemoteHost}:{cfg.EipPort}");
            else if (cfg.Transport == "csp") Console.WriteLine($"CSPv4 session established with {cfg.RemoteHost}:{cfg.CspPort}");
            else                             Console.WriteLine("DF1 port opened successfully");
            Console.WriteLine();

            bool nodeOk = VerifyTargetNode(pccc, cfg);
            if (nodeOk) StartKeepalive(pccc, cfg);
            if (nodeOk && cfg.RunDemo)   RunDemo(pccc, cfg);
            if (cfg.ScanNodes)           RunNodeScan(pccc, cfg.ScanFrom, cfg.ScanTo);
            if (nodeOk && cfg.StressTest) RunStressTest(pccc, cfg);
            if (!cfg.NoInteractive)      RunInteractiveCli(pccc, cfg);
        }
        catch (Exception ex) { Console.WriteLine($"Fatal error: {ex.Message}"); }
        finally
        {
            StopKeepalive();
            pccc.CloseComms();
            pccc.Dispose();
            Console.WriteLine("\nPress Enter to exit.");
            Console.ReadLine();
        }
    }

    // ── Config record ─────────────────────────────────────────────────────────

    private sealed record Config
    {
        public string Transport          { get; init; } = "df1";
        public string PortName           { get; init; } = "COM1";
        public int    Baud               { get; init; } = 19200;
        public Parity SerialParity       { get; init; } = Parity.None;
        public string Rs485Mode          { get; init; } = "auto";
        public bool   EchoSuppression    { get; init; } = false;
        public int    Rs485AssertDelay   { get; init; } = 1;
        public int    Rs485DeassertDelay { get; init; } = 5;
        public string RemoteHost         { get; init; } = "";
        public int    EipPort            { get; init; } = 44818;
        public int    CspPort            { get; init; } = 2222;
        public byte   LsapControlByte    { get; init; } = 0x00;
        public int    TimeoutMs          { get; init; } = 5000;
        public int    TargetNode         { get; init; } = 1;
        public int    MyNode             { get; init; } = 0;
        public string Checksum           { get; init; } = "crc";
        public bool   InteractiveOnly    { get; init; } = false;
        public bool   NoInteractive      { get; init; } = false;
        public bool   RunDemo            { get; init; } = false;
        public bool   StressTest         { get; init; } = false;
        public int    StressLoopCount    { get; init; } = 0;
        public bool   ScanNodes          { get; init; } = false;
        public int    ScanFrom           { get; init; } = 1;
        public int    ScanTo             { get; init; } = 31;
    }

    // ── SelfTestContext — built once, passed to all test methods ──────────────

    /// <summary>
    /// Captures processor family, transport type, and data file directory.
    /// All SelfTest_* methods use this to skip tests that are invalid for the
    /// connected device or transport without making extra network calls.
    /// </summary>
    private sealed record SelfTestContext
    {
        public Comm.Pccc.PCCCConstants.ProcessorFamily Family { get; init; }
        public string Transport                                { get; init; } = "df1";
        public Comm.DataFileDetails[]? Files                  { get; init; }

        // Family helpers
        public bool IsPlc5   => Family == Comm.Pccc.PCCCConstants.ProcessorFamily.Plc5;
        // ML1400 is part of SlcMicroLogix family — no separate enum value in ProcessorFamily
        public bool IsSlc    => Family == Comm.Pccc.PCCCConstants.ProcessorFamily.SlcMicroLogix;

        // Transport helpers
        public bool IsEip    => Transport == "eip";
        public bool IsCsp    => Transport == "csp";
        public bool IsSerial => Transport is "df1" or "df1master";

        // Protocol capability flags
        /// <summary>
        /// Gates the SLC-style FNC 0xAB / FNC 0x26 wire format tested by SelfTest_BitReadWrite
        /// and SelfTest_ReadModifyWrite. PLC-5 does NOT use FNC 0xAB at all, and uses a
        /// different FNC 0x26 payload (PLC-5 logical binary addressing + element-sized masks,
        /// see PCCCProtocol.ReadModifyWritePlc5) — it is exercised separately by
        /// SelfTest_Plc5ReadModifyWrite, gated on IsPlc5 instead.
        /// </summary>
        public bool SupportsSlcRmw    => !IsPlc5;
        /// <summary>DH485 link parameters only meaningful on SLC serial.</summary>
        public bool SupportsLinkParams => IsSlc && IsSerial;

        // Directory helpers

        /// <summary>
        /// Finds a data file by number. Returns null if directory unavailable or file absent.
        /// Uses a boxed reference (class wrapper) so callers can null-check safely
        /// without C# nullable-struct dereference issues.
        /// </summary>
        private Comm.DataFileDetails[] SafeFiles => Files ?? Array.Empty<Comm.DataFileDetails>();

        /// <summary>True if directory was successfully read. False = ML1400/DF1 or other transport limitation.</summary>
        public bool DirectoryAvailable => Files != null;

        public int ElementCount(int fileNumber)
            => SafeFiles.FirstOrDefault(f => f.FileNumber == fileNumber).NumberOfElements;

        /// <summary>
        /// True if the file exists in the directory AND has enough elements.
        /// Returns false if directory is unavailable (ML1400/DF1) or file is too small.
        /// </summary>
        public bool CanAccess(int fileNumber, int element)
            => DirectoryAvailable && ElementCount(fileNumber) > element;

        /// <summary>
        /// Returns a human-readable skip reason for a test that needs file access.
        /// Distinguishes between "directory unavailable" and "file too small".
        /// </summary>
        public string SkipReason(int fileNumber, string fileLabel, int minElements)
        {
            if (!DirectoryAvailable)
                return $"directory not available over {Transport.ToUpperInvariant()} — use EIP for full test";
            int count = ElementCount(fileNumber);
            return count == 0
                ? $"{fileLabel} not present in directory"
                : $"{fileLabel} has only {count} element(s), need ≥ {minElements}";
        }

        /// <summary>
        /// Returns the first ST file in the directory as a boxed reference, or null if none.
        /// Callers use: if (stFile != null) { stFile.FileNumber ... }
        /// </summary>
        public StFileInfo? FindStFile()
        {
            foreach (var f in SafeFiles)
                if (f.FileType == "ST") return new StFileInfo(f.FileNumber, f.NumberOfElements);
            return null;
        }
    }

    /// <summary>Lightweight reference type wrapper for ST file info, enabling safe null checks.</summary>
    private sealed class StFileInfo
    {
        public int FileNumber      { get; }
        public int NumberOfElements { get; }
        public StFileInfo(int fileNumber, int numberOfElements)
        { FileNumber = fileNumber; NumberOfElements = numberOfElements; }
    }

    // ── Argument parser ───────────────────────────────────────────────────────

    private static bool TryParseArgs(string[] args, out Config cfg)
    {
        string transport = "df1", portName = "COM1", remoteHost = "", rs485Mode = "auto", checksum = "crc";
        int baud = 19200, eipPort = 44818, cspPort = 2222, timeoutMs = 5000;
        int targetNode = 1, myNode = 0, rs485AssertDelay = 1, rs485DeassertDelay = 5;
        int stressLoopCount = 0, scanFrom = 1, scanTo = 31;
        Parity parity = Parity.None;
        byte lsapControl = 0x00;
        bool echoSuppression = false, interactiveOnly = false, noInteractive = false;
        bool runDemo = false, stressTest = false, scanNodes = false;

        cfg = new Config();

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();
            if (i == 0 && !a.StartsWith("--")) { portName = args[i]; continue; }
            switch (a)
            {
                case "--mode"    when i+1 < args.Length: transport  = args[++i].ToLowerInvariant(); break;
                case "--baud"    when i+1 < args.Length: if (int.TryParse(args[++i], out var b))  baud       = b; break;
                case "--target"  when i+1 < args.Length: if (int.TryParse(args[++i], out var n))  targetNode = n; break;
                case "--mynode"  when i+1 < args.Length: if (int.TryParse(args[++i], out var mn)) myNode     = mn; break;
                case "--host"    when i+1 < args.Length: remoteHost  = args[++i]; break;
                case "--eip-port"  when i+1 < args.Length: if (int.TryParse(args[++i], out var p)) eipPort   = p; break;
                case "--csp-port"  when i+1 < args.Length: if (int.TryParse(args[++i], out var c)) cspPort   = c; break;
                case "--timeout"   when i+1 < args.Length: if (int.TryParse(args[++i], out var t)) timeoutMs = t; break;
                case "--lsap-control" when i+1 < args.Length:
                    if (byte.TryParse(args[++i], System.Globalization.NumberStyles.HexNumber, null, out byte lsap)) lsapControl = lsap;
                    break;
                case "--checksum"             when i+1 < args.Length: checksum  = args[++i].ToLowerInvariant(); break;
                case "--rs485-mode"           when i+1 < args.Length: rs485Mode = args[++i].ToLowerInvariant(); break;
                case "--rs485-assert-delay"   when i+1 < args.Length: if (int.TryParse(args[++i], out var ad)) rs485AssertDelay   = ad; break;
                case "--rs485-deassert-delay" when i+1 < args.Length: if (int.TryParse(args[++i], out var dd)) rs485DeassertDelay = dd; break;
                case "--echo-suppression": echoSuppression = true; break;
                case "--demo":             runDemo         = true; break;
                case "--interactive-only": interactiveOnly = true; break;
                case "--no-interactive":   noInteractive   = true; break;
                case "--stress-test":
                    stressTest = true;
                    if (i+1 < args.Length && int.TryParse(args[i+1], out var loops)) { stressLoopCount = loops; i++; }
                    break;
                case "--scan-nodes":
                    scanNodes = true;
                    if (i+1 < args.Length && int.TryParse(args[i+1], out var sf)) { scanFrom = sf; i++; }
                    if (i+1 < args.Length && int.TryParse(args[i+1], out var st)) { scanTo   = st; i++; }
                    break;
                case "--parity" when i+1 < args.Length:
                    parity = args[++i].ToLowerInvariant() switch { "odd" => Parity.Odd, "even" => Parity.Even, _ => Parity.None };
                    break;
                case "--help": case "-h": PrintUsage(); return false;
            }
        }

        if (transport is "df1" or "df1master")
        {
            try   { portName = NormalizePortName(portName); }
            catch (Exception ex) { Console.WriteLine(ex.Message); return false; }
        }

        cfg = new Config
        {
            Transport = transport, PortName = portName, Baud = baud, SerialParity = parity,
            Rs485Mode = rs485Mode, EchoSuppression = echoSuppression,
            Rs485AssertDelay = rs485AssertDelay, Rs485DeassertDelay = rs485DeassertDelay,
            RemoteHost = remoteHost, EipPort = eipPort, CspPort = cspPort,
            TimeoutMs = timeoutMs, LsapControlByte = lsapControl,
            TargetNode = targetNode, MyNode = myNode, Checksum = checksum,
            InteractiveOnly = interactiveOnly, NoInteractive = noInteractive,
            RunDemo = runDemo, StressTest = stressTest, StressLoopCount = stressLoopCount,
            ScanNodes = scanNodes, ScanFrom = scanFrom, ScanTo = scanTo,
        };
        return true;
    }

    // ── Keepalive / auto-reconnect ────────────────────────────────────────────

    private static volatile bool _keepaliveRunning = false;
    private static volatile bool _keepaliveEnabled = false;
    private static TimeSpan      _keepaliveInterval = TimeSpan.FromSeconds(5);
    private const  int           KeepaliveFailThreshold = 2;
    private static volatile bool _linkConnected = true;
    private const  int           MaxReconnectAttempts = 3;

    private static void StartKeepalive(Comm.PCCCComm pccc, Config cfg)
    {
        _keepaliveRunning = true;
        // Keepalive defaults to OFF because Echo is not supported by all PLCs
        // (e.g., some PLC-5 models). Users can enable it with 'keepalive on'.
        _keepaliveEnabled = false;
        _linkConnected    = true;
        var thread = new System.Threading.Thread(() =>
        {
            int failures = 0;
            while (_keepaliveRunning)
            {
                System.Threading.Thread.Sleep(_keepaliveInterval);
                if (!_keepaliveRunning) break;
                if (!_keepaliveEnabled) continue;
                if (!_linkConnected) continue;
                try { pccc.Echo(new byte[] { 0xAA }); failures = 0; }
                catch
                {
                    if (++failures >= KeepaliveFailThreshold)
                    {
                        _linkConnected = false; failures = 0;
                        try { pccc.CloseComms(); } catch { }
                        Console.WriteLine("\n  [keepalive] Link lost — type any command to reconnect.\nPCCC> ");
                    }
                }
            }
        }) { IsBackground = true, Name = "PCCCComm-Keepalive" };
        thread.Start();
    }

    private static void StopKeepalive() => _keepaliveRunning = false;

    private static bool EnsureConnected(Comm.PCCCComm pccc, Config cfg)
    {
        if (_linkConnected) return true;
        Console.WriteLine("  [reconnect] Link was down — reconnecting...");
        for (int attempt = 1; attempt <= MaxReconnectAttempts; attempt++)
        {
            Console.Write($"  Attempt {attempt}/{MaxReconnectAttempts}... ");
            try
            {
                System.Threading.Thread.Sleep(1000 * attempt);
                pccc.OpenComms();
                _ = pccc.GetProcessorType();
                _linkConnected = true;
                Console.WriteLine("OK — resuming.");
                return true;
            }
            catch (Exception ex) { Console.WriteLine($"failed ({ex.Message})"); try { pccc.CloseComms(); } catch { } }
        }
        Console.WriteLine($"  Could not reconnect after {MaxReconnectAttempts} attempts.");
        Console.WriteLine("  Type 'exit' to quit or try again later.");
        return false;
    }

    // ── Transport factory ─────────────────────────────────────────────────────

    private static Comm.PCCCComm BuildTransport(Config cfg)
    {
        Comm.PCCCComm pccc;
        switch (cfg.Transport)
        {
            case "eip":
                if (string.IsNullOrEmpty(cfg.RemoteHost)) throw new Exception("EIP mode requires --host <IP>");
                pccc = Comm.PCCCComm.ForEip(cfg.RemoteHost, cfg.EipPort, cfg.TimeoutMs);
                pccc.TargetNode = cfg.TargetNode; pccc.MyNode = cfg.MyNode;
                Console.WriteLine($"EIP: Connecting to {cfg.RemoteHost}:{cfg.EipPort} (timeout {cfg.TimeoutMs} ms)");
                break;
            case "csp":
                if (string.IsNullOrEmpty(cfg.RemoteHost)) throw new Exception("CSPv4 mode requires --host <IP>");
                pccc = Comm.PCCCComm.ForCsp(cfg.RemoteHost, cfg.CspPort, cfg.TimeoutMs, cfg.LsapControlByte);
                pccc.TargetNode = cfg.TargetNode; pccc.MyNode = cfg.MyNode;
                Console.WriteLine($"CSPv4: Connecting to {cfg.RemoteHost}:{cfg.CspPort} (timeout {cfg.TimeoutMs} ms)");
                break;
            case "df1master":
                pccc = new Comm.PCCCComm(cfg.PortName, cfg.Baud, cfg.SerialParity)
                {
                    Protocol = "DF1Master", TargetNode = cfg.TargetNode, SlaveAddress = cfg.TargetNode,
                    MyNode = cfg.MyNode, EchoSuppression = cfg.EchoSuppression,
                    Rs485AssertDelayMs = cfg.Rs485AssertDelay, Rs485DeassertDelayMs = cfg.Rs485DeassertDelay,
                    Rs485Mode = cfg.Rs485Mode switch
                    {
                        "rts" => Comm.Core.DF1HalfDuplexTransport.Rs485ControlMode.Rts,
                        "dtr" => Comm.Core.DF1HalfDuplexTransport.Rs485ControlMode.Dtr,
                        _     => Comm.Core.DF1HalfDuplexTransport.Rs485ControlMode.Auto,
                    },
                };
                Console.WriteLine($"DF1 Master: {cfg.PortName} @ {cfg.Baud} baud, {cfg.SerialParity} parity, RS-485={cfg.Rs485Mode}");
                Console.WriteLine($"MyNode={cfg.MyNode}, SlaveAddress={cfg.TargetNode}");
                break;
            default:
                pccc = new Comm.PCCCComm(cfg.PortName, cfg.Baud, cfg.SerialParity)
                {
                    TargetNode = cfg.TargetNode, MyNode = cfg.MyNode,
                    CheckSum = cfg.Checksum == "crc" ? Comm.Core.CheckSumOptions.Crc : Comm.Core.CheckSumOptions.Bcc,
                };
                Console.WriteLine($"DF1: Connecting to {cfg.PortName} @ {cfg.Baud} baud, {cfg.SerialParity} parity, checksum={pccc.CheckSum}");
                Console.WriteLine($"MyNode={cfg.MyNode}, TargetNode={cfg.TargetNode}");
                break;
        }
        return pccc;
    }

    // ── Target node verifier ──────────────────────────────────────────────────

    private static bool VerifyTargetNode(Comm.PCCCComm pccc, Config cfg)
    {
        Console.Write($"Verifying target node {cfg.TargetNode}... ");
        const int maxAttempts = 3;
        Exception? lastEx = null;
        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            try { Console.WriteLine($"OK  ({ProcessorTypeName(pccc)})"); Console.WriteLine(); return true; }
            catch (Exception ex) { lastEx = ex; if (attempt < maxAttempts - 1) System.Threading.Thread.Sleep(500); }
        }
        Console.WriteLine("FAILED");
        Console.WriteLine();
        Console.WriteLine($"  Error    : {lastEx?.Message ?? "unknown error"}");
        Console.WriteLine($"  Transport: {cfg.Transport.ToUpperInvariant()}");
        if (cfg.Transport == "eip")
        {
            Console.WriteLine($"  Target   : {cfg.RemoteHost}:{cfg.EipPort}");
            Console.WriteLine("  Suggestions:");
            Console.WriteLine("    - Verify the PLC or emulator is running in EIP mode.");
            Console.WriteLine($"    - Check firewall (TCP {cfg.EipPort}) and --host/--eip-port settings.");
        }
        else if (cfg.Transport == "csp")
        {
            Console.WriteLine($"  Target   : {cfg.RemoteHost}:{cfg.CspPort}");
            Console.WriteLine("  Suggestions:");
            Console.WriteLine("    - Verify the PLC or emulator is running in CSPv4 mode.");
            Console.WriteLine("    - If using RSLinx, try adding --lsap-control 05.");
        }
        else
        {
            Console.WriteLine($"  Port     : {cfg.PortName}  Baud: {cfg.Baud}  Node: {cfg.TargetNode}");
            Console.WriteLine("  Suggestions:");
            Console.WriteLine($"    - Use 'scannodes' in the interactive CLI to discover active nodes.");
            string modeFlag = cfg.Transport == "df1master" ? " --mode df1master" : "";
            string portArg  = cfg.PortName != "COM1" ? $" {cfg.PortName}" : "";
            Console.WriteLine($"        dotnet run -- {portArg}{modeFlag} --target <node>");
            Console.WriteLine("    - Verify baud rate, parity, and checksum match PLC settings.");
            Console.WriteLine("    - For RS-485: check termination resistors and cable polarity.");
        }
        Console.WriteLine();
        if (!cfg.NoInteractive) Console.WriteLine("  Entering interactive CLI so you can run 'scannodes' or 'exit'.");
        Console.WriteLine();
        return false;
    }

// =============================================================================
// SECTION 2 — Demo: quick read/write showcase
// =============================================================================

    /// <summary>
    /// Runs a brief showcase of the most common PCCCComm operations.
    /// Adapts to the connected PLC family: skips writes not supported on the
    /// device (e.g. bit RMW on PLC-5, ST writes when no string file exists).
    /// </summary>
    private static void RunDemo(Comm.PCCCComm pccc, Config cfg)
    {
        var ctx = BuildSelfTestContext(pccc, cfg);

        Console.WriteLine("--- Processor Info ---");
        int proc = Execute(() => pccc.GetProcessorType(), "GetProcessorType");
        Console.WriteLine($"Processor Type : 0x{proc:X2}");
        Console.WriteLine(Execute(() => pccc.GetRunMode(), "GetRunMode") == 1 ? "Mode           : RUN" : "Mode           : PROGRAM");

        Console.WriteLine("\n--- Data Files ---");
        Comm.DataFileDetails[]? files = Execute(() => pccc.GetDataMemory(), "GetDataMemory");
        if (files != null)
            foreach (var f in files) Console.WriteLine($"  File {f.FileNumber,3}: Type={f.FileType,-4}  Elements={f.NumberOfElements}");
        else
            Console.WriteLine("  (Failed to retrieve data files)");

        Console.WriteLine("\n--- Read Operations ---");
        Console.WriteLine($"  O0:0   = {Execute(() => pccc.ReadAny("O0:0"), "Read O0:0") ?? ""}");
        Console.WriteLine($"  I1:0   = {Execute(() => pccc.ReadAny("I1:0"), "Read I1:0") ?? ""}");
        Console.WriteLine($"  B3:0   = {Execute(() => pccc.ReadAny("B3:0"), "Read B3:0") ?? ""}");
        Console.WriteLine($"  N7:0   = {Execute(() => pccc.ReadAny("N7:0"), "Read N7:0") ?? ""}");
        Console.WriteLine($"  F8:0   = {Execute(() => pccc.ReadAny("F8:0"), "Read F8:0") ?? ""}");
        var stFile = ctx.FindStFile();
        if (stFile != null)
        {
            string stAddr = $"ST{stFile.FileNumber}:0";
            Console.WriteLine($"  {stAddr,-8} = \"{Execute(() => pccc.ReadAny(stAddr), $"Read {stAddr}") ?? ""}\"");
        }

        Console.WriteLine("\n--- Write Operations ---");
        if (ctx.CanAccess(7, 1))
        { Console.WriteLine("  Writing 999 to N7:1..."); ExecuteVoid(() => pccc.WriteData("N7:1", 999), "Write N7:1"); }
        if (ctx.CanAccess(8, 1))
        { Console.WriteLine("  Writing 2.718 to F8:1..."); ExecuteVoid(() => pccc.WriteData("F8:1", 2.718f), "Write F8:1"); }
        if (ctx.SupportsSlcRmw && ctx.CanAccess(3, 0))
        {
            ExecuteVoid(() => pccc.WriteData("B3:0", 0), "Write B3:0 reset");
            Console.WriteLine("  Setting B3:0/0 = 1..."); ExecuteVoid(() => pccc.WriteData("B3:0/0", 1), "Write B3:0/0");
            Console.WriteLine("  Setting B3:0/3 = 1..."); ExecuteVoid(() => pccc.WriteData("B3:0/3", 1), "Write B3:0/3");
        }
        if (stFile != null && stFile.NumberOfElements >= 2)
        {
            string stAddr = $"ST{stFile.FileNumber}:1";
            Console.WriteLine($"  Writing string to {stAddr}...");
            ExecuteVoid(() => pccc.WriteData(stAddr, "HELLO PCCC"), $"Write {stAddr}");
        }

        Console.WriteLine("\n--- Read-Back After Write ---");
        if (ctx.CanAccess(7, 1)) Console.WriteLine($"  N7:1   = {Execute(() => pccc.ReadAny("N7:1"), "Read N7:1") ?? ""}");
        if (ctx.CanAccess(8, 1)) Console.WriteLine($"  F8:1   = {Execute(() => pccc.ReadAny("F8:1"), "Read F8:1") ?? ""}");
        if (ctx.SupportsSlcRmw && ctx.CanAccess(3, 0))
            Console.WriteLine($"  B3:0   = {Execute(() => pccc.ReadAny("B3:0"), "Read B3:0") ?? ""}  (bits 0 and 3 set → expected 9)");

        PrintStats();
    }

// =============================================================================
// SECTION 3 — Stress test, node scanner, watch, word commands
// =============================================================================

    /// <summary>
    /// Continuous read loop. Uses N7:0 as the target (present on all families).
    /// Falls back to F8:0 only if N7 is absent.
    /// </summary>
    private static void RunStressTest(Comm.PCCCComm pccc, Config cfg)
    {
        Comm.DataFileDetails[]? files = null;
        try { files = pccc.GetDataMemory(); } catch { }
        string stressAddr = (files == null || files.Any(f => f.FileNumber == 7)) ? "N7:0" : "F8:0";
        int loopCount = cfg.StressLoopCount;

        Console.WriteLine("\n--- Stress Test Mode ---");
        Console.WriteLine(loopCount == 0
            ? $"Reading {stressAddr} continuously. Press any key to stop."
            : $"Reading {stressAddr} for {loopCount} iterations.");

        int count = 0;
        while (!Console.KeyAvailable && (loopCount == 0 || count < loopCount))
        {
            try
            {
                string[] val = pccc.ReadAny(stressAddr, 1) ?? Array.Empty<string>();
                RecordSuccess();
                if (++count % 100 == 0)
                    Console.WriteLine($"  {count,6} reads — last value: {(val.Length > 0 ? val[0] : "(null)")}");
            }
            catch (Comm.Pccc.PCCCException ex)
            {
                if      (ex.Message.Contains("NAK"))         RecordNak();
                else if (ex.Message.Contains("No Response") || ex.Message.Contains("Timeout")) RecordTimeout();
                else                                          RecordOtherError();
                Console.WriteLine($"  Error at iteration {count + 1}: {ex.Message}");
            }
            catch (Exception ex) { RecordOtherError(); Console.WriteLine($"  Unexpected error: {ex.Message}"); }
            Thread.Sleep(50);
        }
        if (Console.KeyAvailable) Console.ReadKey(true);
        PrintStats();
    }

    // ── Node scanner ──────────────────────────────────────────────────────────

    private static void RunNodeScan(Comm.PCCCComm pccc, int from, int to)
    {
        from = Math.Max(0, Math.Min(254, from));
        to   = Math.Max(from, Math.Min(254, to));
        int savedTarget = pccc.TargetNode, savedSlave = pccc.SlaveAddress, savedTimeout = pccc.ResponseTimeoutMs;
        const int probeMs = 1000, minLen = 28;
        pccc.ResponseTimeoutMs = probeMs;
        Console.WriteLine($"\n--- Node Scan (nodes {from}–{to}, timeout {probeMs} ms each) ---");

        var found = new List<(int node, string name)>();
        for (int node = from; node <= to; node++)
        {
            pccc.TargetNode = pccc.SlaveAddress = node;
            Console.Write($"  Node {node,3}: ");
            try
            {
                byte[]? raw = pccc.GetDiagnosticStatusRaw();
                if (raw == null) { Console.WriteLine("error response"); continue; }
                if (raw.Length + 6 < minLen) { Console.WriteLine($"ignored (frame too short: {raw.Length + 6} < {minLen} bytes)"); continue; }
                byte typeExt = raw[Comm.Pccc.PCCCConstants.ResponseOffsets.DiagnosticStatus.TypeExtender];
                if (typeExt != Comm.Pccc.PCCCConstants.ResponseOffsets.DiagnosticStatus.TypeExtenderSlcMl)
                { Console.WriteLine($"ignored (type extender=0x{typeExt:X2}, not SLC/ML)"); continue; }
                int procType = raw[Comm.Pccc.PCCCConstants.ResponseOffsets.DiagnosticStatus.ProcessorType];
                if (procType == 0) { Console.WriteLine("ignored (type 0x00 — likely echo)"); continue; }
                string name = SlcProcessorTypeName(procType);
                Console.WriteLine($"FOUND  type=0x{procType:X2}  ({name})");
                found.Add((node, name));
            }
            catch (Comm.Pccc.PCCCException ex) when (ex.Message.Contains("No Response") || ex.Message.Contains("Timeout") || ex.Message.Contains("NAK"))
            { Console.WriteLine("no response"); }
            catch (Exception ex) { Console.WriteLine($"error: {ex.Message}"); }
        }

        Console.WriteLine();
        if (found.Count == 0) Console.WriteLine("  No nodes found in range.");
        else { Console.WriteLine($"  {found.Count} node(s) found:"); foreach (var (n, name) in found) Console.WriteLine($"    Node {n,3}  ({name})"); }
        pccc.TargetNode = savedTarget; pccc.SlaveAddress = savedSlave; pccc.ResponseTimeoutMs = savedTimeout;
        Console.WriteLine($"\n  Target node restored to {savedTarget}.");
    }

    private static void HandleSetTarget(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out int node) || node < 0 || node > 254)
        { Console.WriteLine("Usage: settarget <node>"); return; }
        int prev = pccc.TargetNode;
        pccc.TargetNode = pccc.SlaveAddress = node;
        Console.Write($"Target node changed {prev} → {node}. Probing... ");
        try { Console.WriteLine($"OK  ({ProcessorTypeName(pccc)})"); }
        catch (Comm.Pccc.PCCCException ex)
        { Console.WriteLine($"no response  ({ex.Message})"); Console.WriteLine($"  Run 'scannodes' or 'settarget {prev}' to revert."); }
    }

    private static void HandleScanNodes(Comm.PCCCComm pccc, string[] parts)
    {
        int from = 1, to = 31;
        if (parts.Length >= 2 && !int.TryParse(parts[1], out from)) { Console.WriteLine("Usage: scannodes [from] [to]"); return; }
        if (parts.Length >= 3 && !int.TryParse(parts[2], out to))   { Console.WriteLine("Usage: scannodes [from] [to]"); return; }
        RunNodeScan(pccc, from, to);
    }

    // ── Processor name helpers ────────────────────────────────────────────────

    private static string SlcProcessorTypeName(int code) => code switch
    {
        0x25 => "SLC 5/01 (series A/B)", 0x31 => "SLC 5/02",  0x3B => "SLC 500 (fixed)",
        0x49 => "SLC 5/03",              0x4A => "SLC 5/03 (OS302)", 0x5B => "SLC 5/04",
        0x4C => "SLC 5/05",              0x88 => "MicroLogix 1000", 0x89 => "MicroLogix 1000 (series C)",
        0x9C => "MicroLogix 1100",       0x9F => "MicroLogix 1400 (series B)",
        0xA0 => "MicroLogix 1200",       0xA2 => "MicroLogix 1400",
        _    => $"SLC/MicroLogix (type 0x{code:X2})"
    };

    private static string Plc5ProcessorTypeName(int expansionByte) => expansionByte switch
    {
        0x15 => "PLC-5/40B (1785-L40B)", 0x22 => "PLC-5/10 (1785-LT4)",   0x23 => "PLC-5/60B (1785-L60B)",
        0x28 => "PLC-5/40L (1785-L40L)", 0x29 => "PLC-5/60L (1785-L60L)", 0x31 => "PLC-5/11 (1785-L11B)",
        0x32 => "PLC-5/20 (1785-L20B)",  0x33 => "PLC-5/30 (1785-L30B)",  0x4A => "PLC-5/20E (1785-L20E)",
        0x4B => "PLC-5/40E (1785-L40E)", 0x55 => "PLC-5/25 (1785-L80B)",  0x59 => "PLC-5/80E (1785-L80E)",
        _    => $"PLC-5 (expansion 0x{expansionByte:X2})"
    };

    /// <summary>
    /// Returns a human-readable processor name from a single GetDiagnosticStatusRaw call.
    /// For SLC/ML, reads processor type directly from diag[3] (no second network call).
    /// </summary>
    private static string ProcessorTypeName(Comm.PCCCComm pccc)
    {
        byte[]? diag = pccc.GetDiagnosticStatusRaw();
        if (diag == null || diag.Length < 4) return "unknown";
        var family = Comm.Pccc.PCCCConstants.DetectFamily(diag);
        return family == Comm.Pccc.PCCCConstants.ProcessorFamily.Plc5
            ? Plc5ProcessorTypeName(diag[2])
            : SlcProcessorTypeName(diag[Comm.Pccc.PCCCConstants.ResponseOffsets.DiagnosticStatus.ProcessorType]);
    }

    // ── Watch ─────────────────────────────────────────────────────────────────

    private static void HandleWatch(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 2) { Console.WriteLine("Usage: watch <address> [interval_ms]\n  Example: watch F8:0  /  watch N7:5 200\n  Press any key to stop."); return; }
        string addr = parts[1];
        int intervalMs = 500;
        const int minMs = 50, maxErr = 3;
        if (parts.Length >= 3 && (!int.TryParse(parts[2], out intervalMs) || intervalMs < minMs))
        { Console.WriteLine($"Invalid interval; must be an integer >= {minMs} ms."); return; }

        Console.WriteLine($"Watching {addr} every {intervalMs} ms. Press any key to stop.\n");
        string? lastValue = null;
        int consecErr = 0;
        long changeCount = 0, readCount = 0;
        var sw = Stopwatch.StartNew();

        while (!Console.KeyAvailable)
        {
            try
            {
                string[]? result = pccc.ReadAny(addr, 1);
                string value = result?.Length > 0 ? result[0] : "(null)";
                readCount++; consecErr = 0;
                if (value != lastValue)
                {
                    changeCount++;
                    Console.WriteLine($"  [{sw.Elapsed:hh\\:mm\\:ss\\.fff}]  {addr} = {value}" + (lastValue == null ? "  (initial)" : $"  (was: {lastValue})"));
                    lastValue = value;
                }
            }
            catch (Comm.Pccc.PCCCException ex)
            {
                Console.WriteLine($"  [{sw.Elapsed:hh\\:mm\\:ss\\.fff}]  Error: {ex.Message}");
                if (++consecErr >= maxErr) { Console.WriteLine($"  {maxErr} consecutive errors — stopping."); break; }
            }
            catch (Exception ex) { if (++consecErr >= maxErr) break; Console.WriteLine($"  Unexpected error: {ex.Message}"); }

            int slept = 0;
            while (slept < intervalMs && !Console.KeyAvailable) { Thread.Sleep(Math.Min(50, intervalMs - slept)); slept += 50; }
        }
        if (Console.KeyAvailable) Console.ReadKey(true);
        Console.WriteLine($"\n  Watch stopped. {readCount} reads, {changeCount} change(s) in {sw.Elapsed:hh\\:mm\\:ss}.");
    }

    // ── TypedRead / TypedWrite (PLC-5, FNC 0x68/0x67) ─────────────────────────
    // Exercises the typed command path (distinct from wordread/wordwrite). Supports
    // N (integer, 2 bytes) and F (float, 4 bytes) — the two types with spec/capture
    // ground truth. Typed float uses STANDARD little-endian (no high-word swap), unlike
    // Word Range. Address uses the 3-level write form (mask 0x07), matching live RSLinx
    // typed traffic.

    private static void HandleTypedRead(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 4)
        {
            Console.WriteLine("Usage: typedread <N|F> <fileNumber> <element> [count]");
            Console.WriteLine("Example: typedread F 8 0   /   typedread N 7 0 3");
            return;
        }
        string ft = parts[1].ToUpperInvariant();
        if (ft != "N" && ft != "F") { Console.WriteLine("typedread currently supports N and F only."); return; }
        if (!int.TryParse(parts[2], out int fn) || !int.TryParse(parts[3], out int elem)) { Console.WriteLine("Invalid file number/element."); return; }
        int count = 1;
        if (parts.Length >= 5 && !int.TryParse(parts[4], out count)) { Console.WriteLine("Invalid count."); return; }

        byte[] addr = Comm.Handlers.Plc5Handler.EncodePlc5WriteAddress(fn, elem);
        try
        {
            byte[] data = pccc.TypedRead(addr, count);
            int bpe = ft == "F" ? 4 : 2;
            var vals = new System.Collections.Generic.List<string>();
            for (int i = 0; i + bpe <= data.Length && i / bpe < count; i += bpe)
                vals.Add(ft == "F"
                    ? System.BitConverter.ToSingle(data, i).ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : System.BitConverter.ToInt16(data, i).ToString());
            Console.WriteLine($"Result: {string.Join(", ", vals)}");
        }
        catch (Exception ex) { Console.WriteLine($"TypedRead failed: {ex.Message}"); }
    }

    private static void HandleTypedWrite(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 5)
        {
            Console.WriteLine("Usage: typedwrite <N|F> <fileNumber> <element> <value>");
            Console.WriteLine("Example: typedwrite F 8 0 123.5   /   typedwrite N 7 0 999");
            return;
        }
        string ft = parts[1].ToUpperInvariant();
        if (ft != "N" && ft != "F") { Console.WriteLine("typedwrite currently supports N and F only."); return; }
        if (!int.TryParse(parts[2], out int fn) || !int.TryParse(parts[3], out int elem)) { Console.WriteLine("Invalid file number/element."); return; }

        byte[] addr = Comm.Handlers.Plc5Handler.EncodePlc5WriteAddress(fn, elem);
        byte[] descriptor, data;
        if (ft == "F")
        {
            if (!float.TryParse(parts[4], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f))
            { Console.WriteLine("Invalid float value."); return; }
            descriptor = new byte[] { 0x94, 0x08 };            // ID 8 (float), size 4
            data = System.BitConverter.GetBytes(f);            // standard little-endian
        }
        else
        {
            if (!short.TryParse(parts[4], out short n)) { Console.WriteLine("Invalid integer value (-32768..32767)."); return; }
            descriptor = new byte[] { 0x42 };                  // ID 4 (integer), size 2
            data = System.BitConverter.GetBytes(n);
        }
        try
        {
            pccc.TypedWrite(addr, descriptor, data, 1);
            Console.WriteLine("Typed write successful.");
        }
        catch (Exception ex) { Console.WriteLine($"TypedWrite failed: {ex.Message}"); }
    }

    // ── WordRead / WordWrite (PLC-5) ──────────────────────────────────────────

    /// <summary>Shared address parsing for wordread and wordwrite.</summary>
    private static bool TryParseWordAddress(string[] parts, int minParts,
        out string fileTypeStr, out int fileNumber, out int element, out int wordOffset, out int fileTypeCode)
    {
        fileTypeStr = ""; fileNumber = element = wordOffset = fileTypeCode = 0;
        if (parts.Length < minParts) return false;
        fileTypeStr = parts[1].ToUpperInvariant();
        if (!int.TryParse(parts[2], out fileNumber) || !int.TryParse(parts[3], out element) || !int.TryParse(parts[4], out wordOffset)) return false;
        fileTypeCode = Plc5FileTypeCode(fileTypeStr);
        return fileTypeCode != -1;
    }

    private static void HandleWordRead(Comm.PCCCComm pccc, string[] parts)
    {
        if (!TryParseWordAddress(parts, 6, out _, out int fn, out int elem, out int wo, out int _) || !int.TryParse(parts[5], out int sz))
        {
            Console.WriteLine("Usage: wordread <fileType> <fileNumber> <element> <wordOffset> <sizeWords>");
            Console.WriteLine("Example: wordread N 7 0 0 10");
            if (parts.Length >= 2 && Plc5FileTypeCode(parts[1].ToUpperInvariant()) == -1) Console.WriteLine($"Unknown file type: {parts[1]}");
            return;
        }
        try
        {
            byte[] data = pccc.WordRangeRead(Comm.Handlers.Plc5Handler.EncodePlc5LogicalAddress(fn, elem), wo, sz);
            Console.WriteLine($"Read {data.Length} bytes:"); WriteHex("  ", data, data.Length);
        }
        catch (Exception ex) { Console.WriteLine($"WordRangeRead failed: {ex.Message}"); }
    }

    private static void HandleWordWrite(Comm.PCCCComm pccc, string[] parts)
    {
        if (!TryParseWordAddress(parts, 5, out _, out int fn, out int elem, out int wo, out int _))
        {
            Console.WriteLine("Usage: wordwrite <fileType> <fileNumber> <element> <wordOffset> <dataHex...>");
            Console.WriteLine("Example: wordwrite N 7 0 0 0010 0020 0030");
            if (parts.Length >= 2 && Plc5FileTypeCode(parts[1].ToUpperInvariant()) == -1) Console.WriteLine($"Unknown file type: {parts[1]}");
            return;
        }
        var dataBytes = new List<byte>();
        for (int i = 5; i < parts.Length; i++)
        {
            string hex = parts[i];
            if (hex.Length % 2 != 0) { Console.WriteLine($"Invalid hex data: '{hex}'"); return; }
            for (int j = 0; j < hex.Length; j += 2)
            {
                if (!byte.TryParse(hex.Substring(j, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                { Console.WriteLine($"Invalid hex byte: '{hex.Substring(j, 2)}'"); return; }
                dataBytes.Add(b);
            }
        }
        if (dataBytes.Count % 2 != 0) { Console.WriteLine("Total data must be an even number of bytes."); return; }
        try
        {
            pccc.WordRangeWrite(Comm.Handlers.Plc5Handler.EncodePlc5LogicalAddress(fn, elem), wo, dataBytes.ToArray());
            Console.WriteLine($"Wrote {dataBytes.Count / 2} word(s) successfully.");
        }
        catch (Exception ex) { Console.WriteLine($"WordRangeWrite failed: {ex.Message}"); }
    }

    private static void HandleDataMemory(Comm.PCCCComm pccc)
    {
        Comm.DataFileDetails[] files;
        try { files = pccc.GetDataMemory(); }
        catch (Exception ex) { Console.WriteLine($"GetDataMemory failed: {ex.Message}"); return; }
        if (files.Length == 0) { Console.WriteLine("No data files found."); return; }
        Console.WriteLine($"{"No",4}  {"Name",-6}  {"Type",-8}  {"Elements",8}");
        Console.WriteLine(new string('-', 36));
        int seq = 1;
        foreach (var f in files) Console.WriteLine($"{seq++,4}  {f.FileType}{f.FileNumber,-5}  {f.FileType,-8}  {f.NumberOfElements,8}");
        Console.WriteLine(new string('-', 36));
        Console.WriteLine($"  {files.Length} file(s) total.");
    }

    private static void HandleKeepalive(string[] parts)
    {
        if (parts.Length < 2)
        {
            Console.WriteLine($"Keepalive is {( _keepaliveEnabled ? "ON" : "OFF")}");
            return;
        }
        string sub = parts[1].ToLowerInvariant();
        if (sub == "on")
        {
            _keepaliveEnabled = true;
            Console.WriteLine("Keepalive enabled.");
        }
        else if (sub == "off")
        {
            _keepaliveEnabled = false;
            Console.WriteLine("Keepalive disabled.");
        }
        else
            Console.WriteLine("Usage: keepalive [on|off]");
    }

    private static int Plc5FileTypeCode(string letter) => letter switch
    {
        "O" => 0x00, "I" => 0x01, "S" => 0x02, "B" => 0x03, "T" => 0x04, "C" => 0x05, "R" => 0x06, "N" => 0x07,
        "F" => 0x08, "D" => 0x09, "ST"=> 0x0A, "A" => 0x0B, "L" => 0x0C, "MG"=> 0x0D, "PD"=> 0x0E, "PLS"=> 0x0F,
        _   => -1
    };


// =============================================================================
// SECTION 4 — Interactive CLI
// =============================================================================

    private static void RunInteractiveCli(Comm.PCCCComm pccc, Config cfg)
    {
        Console.WriteLine("\n=== Interactive CLI Mode ===");
        Console.WriteLine("Type 'help' for commands, 'exit' to quit.\n");
        while (true)
        {
            Console.Write("PCCC> ");
            string input = Console.ReadLine()?.Trim() ?? "";
            if (string.IsNullOrEmpty(input)) continue;
            string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            string   cmd   = parts[0].ToLowerInvariant();
            try
            {
                if (cmd is "exit" or "quit") return;
                if (!EnsureConnected(pccc, cfg)) continue;
                switch (cmd)
                {
                    case "help":        PrintInteractiveHelp(); break;
                    case "stats":       PrintStats(); break;
                    case "resetstats":  ResetStats(); break;
                    case "read":        HandleRead(pccc, parts); break;
                    case "write":       HandleWrite(pccc, parts); break;
                    case "sendhex":     HandleSendHex(pccc, parts); break;
                    case "echo":        HandleEcho(pccc, parts); break;
                    case "mode":        Console.WriteLine(pccc.GetRunMode() == 1 ? "RUN mode" : "PROGRAM mode"); break;
                    case "setrun":      pccc.SetRunMode();     Console.WriteLine("Switched to RUN mode"); break;
                    case "setprog":     pccc.SetProgramMode(); Console.WriteLine("Switched to PROGRAM mode"); break;
                    case "type":        Console.WriteLine($"Processor Type: 0x{pccc.GetProcessorType():X2}"); break;
                    case "selftest":    RunSelfTest(pccc, parts, cfg); break;
                    case "settarget":   HandleSetTarget(pccc, parts); break;
                    case "scannodes":   HandleScanNodes(pccc, parts); break;
                    case "watch":       HandleWatch(pccc, parts); break;
                    case "wordread":    HandleWordRead(pccc, parts); break;
                    case "wordwrite":   HandleWordWrite(pccc, parts); break;
                    case "typedread":   HandleTypedRead(pccc, parts); break;
                    case "typedwrite":  HandleTypedWrite(pccc, parts); break;
                    case "datamem":     HandleDataMemory(pccc); break;
                    // Password commands — ML1100/1200/1400 only (hidden from help)
                    case "getpass":     HandlePassword(pccc, 0x0B, null,     "Password", read: true);  break;
                    case "getmaster":   HandlePassword(pccc, 0x10, null,     "Master",   read: true);  break;
                    case "setpass"    when parts.Length >= 2: HandlePassword(pccc, 0x0B, parts[1], "Password", read: false); break;
                    case "setmaster"  when parts.Length >= 2: HandlePassword(pccc, 0x10, parts[1], "Master",   read: false); break;
                    case "clearpass":   HandlePassword(pccc, 0x0B, "",       "Password", read: false); break;
                    case "clearmaster": HandlePassword(pccc, 0x10, "",       "Master",   read: false); break;
                    case "keepalive":   HandleKeepalive(parts); break;
                    default: Console.WriteLine($"Unknown command '{cmd}'. Type 'help' for list."); break;
                }
            }
            catch (Comm.Pccc.PCCCException ex) { Console.WriteLine($"PCCC Error: {ex.Message}"); }
            catch (TimeoutException ex)         { Console.WriteLine($"Timeout: {ex.Message}"); }
            catch (Exception ex)                { Console.WriteLine($"Error: {ex.Message}"); }
        }
    }

    private static void HandleRead(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 2) { Console.WriteLine("Usage: read <address> [count]\n  Example: read N7:0  /  read F8:0 5"); return; }
        int cnt = 1;
        if (parts.Length >= 3 && !int.TryParse(parts[2], out cnt)) { Console.WriteLine("Invalid count."); return; }
        Console.WriteLine($"Result: {string.Join(", ", pccc.ReadAny(parts[1], cnt) ?? Array.Empty<string>())}");
    }

    private static void HandleWrite(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 3)
        {
            Console.WriteLine("Usage: write <address> <value> [value2 ...]");
            Console.WriteLine("  Examples: write N7:0 100 200 / write F8:0 3.14 / write ST18:0 Hello World");
            return;
        }
        string addr   = parts[1];
        var    parsed = Comm.Pccc.PCCCParser.Parse(addr);
        if (parsed.FileType == 0) { Console.WriteLine($"Invalid address: '{addr}'"); return; }
        var fileType = (Comm.Pccc.PCCCConstants.SlcFileTypeCode)parsed.FileType;

        // Bit-level write (e.g. B3:0/5, N7:0/2): route to the single-value WriteData
        // overload, whose bit path performs a Read-Modify-Write so ONLY the addressed
        // bit changes. Without this, the multi-element int path below would write the
        // value as the whole word (e.g. "write B3:0/5 1" would overwrite B3:0 with 1,
        // setting bit 0 instead of bit 5 while still reporting success).
        if (parsed.BitNumber >= 0 && parsed.BitNumber < 16)
        {
            if (parts.Length != 3 || !int.TryParse(parts[2], out int bit) || (bit != 0 && bit != 1))
            { Console.WriteLine("Bit write expects a single 0 or 1, e.g. write B3:0/5 1"); return; }
            string bitRes = pccc.WriteData(addr, bit);
            Console.WriteLine(string.IsNullOrEmpty(bitRes) ? "Write successful." : $"Write failed: {bitRes}");
            return;
        }

        if (fileType == Comm.Pccc.PCCCConstants.SlcFileTypeCode.String)
        { pccc.WriteData(addr, string.Join(" ", parts.Skip(2))); Console.WriteLine("String write successful."); return; }

        if (fileType == Comm.Pccc.PCCCConstants.SlcFileTypeCode.Float)
        {
            var floats = new List<float>();
            foreach (var tok in parts.Skip(2))
            {
                if (float.TryParse(tok, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float f)) floats.Add(f);
                else if (int.TryParse(tok, out int iv)) floats.Add((float)iv);
                else { Console.WriteLine($"Invalid float value: '{tok}'"); return; }
            }
            pccc.WriteData(addr, floats.Count, floats.ToArray());
            Console.WriteLine("Float write successful."); return;
        }

        var ints = new List<int>();
        foreach (var tok in parts.Skip(2))
        {
            if (!int.TryParse(tok, out int v)) { Console.WriteLine($"Invalid integer value: '{tok}'"); return; }
            ints.Add(v);
        }
        pccc.WriteData(addr, ints.Count, ints.ToArray());
        Console.WriteLine("Write successful.");
    }

    private static void HandleEcho(Comm.PCCCComm pccc, string[] parts)
    {
        byte[] payload = parts.Length > 1
            ? parts[1..].Select(tok => {
                if (!byte.TryParse(tok, System.Globalization.NumberStyles.HexNumber, null, out byte b))
                    throw new ArgumentException($"Invalid hex byte: '{tok}'");
                return b;
            }).ToArray()
            : new byte[] { 0xDE, 0xAD, 0xBE, 0xEF };

        var sw = Stopwatch.StartNew();
        byte[] response = pccc.Echo(payload);
        sw.Stop();
        bool match = response.Length == payload.Length && response.Zip(payload).All(p => p.First == p.Second);
        Console.WriteLine($"  Sent   : {BitConverter.ToString(payload).Replace("-", " ")}");
        Console.WriteLine($"  Receive: {BitConverter.ToString(response).Replace("-", " ")}");
        Console.WriteLine($"  Match  : {(match ? "YES" : "NO — payload mismatch!")}");
        Console.WriteLine($"  RTT    : {sw.Elapsed.TotalMilliseconds:F1} ms");
    }

    private static void HandleSendHex(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 4) { Console.WriteLine("Usage: sendhex <DST> <CMD> <FNC> [data...]\n  Example: sendhex 01 06 03"); return; }
        if (!byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out byte dst)    ||
            !byte.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out byte cmdByte)||
            !byte.TryParse(parts[3], System.Globalization.NumberStyles.HexNumber, null, out byte fnc))
        { Console.WriteLine("Invalid hex values for DST, CMD, or FNC."); return; }

        var dataBytes = new List<byte>();
        for (int i = 4; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out byte b))
            { Console.WriteLine($"Invalid hex data byte: '{parts[i]}'"); return; }
            dataBytes.Add(b);
        }
        byte[] pdu = new byte[7 + dataBytes.Count];
        pdu[0] = dst; pdu[1] = 0x00; pdu[2] = cmdByte; pdu[3] = 0x00; pdu[4] = 0x00; pdu[5] = 0x00; pdu[6] = fnc;
        for (int i = 0; i < dataBytes.Count; i++) pdu[7 + i] = dataBytes[i];
        WriteHex("      TX:", pdu, pdu.Length);
        var (_, resp, _) = pccc.SendRawPduAndGetResponse(pdu);
        if (resp != null) WriteHex("      RX:", resp, resp.Length);
    }

    // ── Password management (ML1100/1200/1400 only) ───────────────────────────

    /// <summary>
    /// Unified password handler. Reads processor type from diagnostic status
    /// (single network call) to validate family before sending PCCC commands.
    /// </summary>
    private static void HandlePassword(Comm.PCCCComm pccc, int element, string? newPass, string label, bool read)
    {
        byte[]? diag = pccc.GetDiagnosticStatusRaw();
        if (diag == null || diag.Length < 4) { Console.WriteLine("Failed to read processor info."); return; }

        int procType = diag[Comm.Pccc.PCCCConstants.ResponseOffsets.DiagnosticStatus.ProcessorType];
        byte subElem = procType switch
        {
            0x9C                  => 0x02,   // ML1100
            0x9F or 0xA0 or 0xA2 => 0x03,   // ML1200/1400
            _                     => 0xFF
        };

        if (subElem == 0xFF)
        {
            Console.WriteLine($"Password commands are not supported on processor type 0x{procType:X2}.");
            Console.WriteLine("Supported: MicroLogix 1100 (0x9C), 1200 (0xA0), 1400 (0x9F/0xA2).");
            return;
        }

        if (read)
            Console.WriteLine($"{label}: {ReadPasswordRaw(pccc, element)}");
        else
            WritePasswordRaw(pccc, element, newPass ?? "", subElem, label);
    }

    private static string ReadPasswordRaw(Comm.PCCCComm pccc, int element)
    {
        byte[] pdu = new byte[11];
        pdu[0] = (byte)pccc.TargetNode; pdu[1] = (byte)pccc.MyNode;
        pdu[2] = 0x0F; pdu[3] = 0x00; pdu[4] = 0x00; pdu[5] = 0x00;
        pdu[6] = 0xA1; pdu[7] = 0x0A; pdu[8] = 0x00; pdu[9] = 0x00; pdu[10] = (byte)element;
        WriteHex("      TX:", pdu, pdu.Length);
        var (status, response, _) = pccc.SendRawPduAndGetResponse(pdu);
        if (response != null) WriteHex("      RX:", response, response.Length);
        if (status != 0 || response == null || response.Length < 6) return $"(error status=0x{status:X2})";
        int offset = (response.Length >= 6 && response[2] == 0x4F) ? 6 : 0;
        if (response[3] != 0) return $"(STS error: 0x{response[3]:X2})";
        if (response.Length < offset + 10) return "(truncated)";
        int len = 0;
        while (len < 10 && response[offset + len] != 0) len++;
        string pw = System.Text.Encoding.ASCII.GetString(response, offset, len);
        return string.IsNullOrEmpty(pw) ? "(empty)" : pw;
    }

    private static void WritePasswordRaw(Comm.PCCCComm pccc, int element, string pass, byte subElem, string label)
    {
        byte[] data = new byte[10];
        if (!string.IsNullOrEmpty(pass))
        {
            if (pass.Length > 10 || !pass.All(char.IsDigit)) { Console.WriteLine("Invalid password. Must be numeric and <= 10 characters."); return; }
            System.Text.Encoding.ASCII.GetBytes(pass).CopyTo(data, 0);
        }
        byte[] pdu = new byte[22];
        pdu[0] = (byte)pccc.TargetNode; pdu[1] = (byte)pccc.MyNode;
        pdu[2] = 0x0F; pdu[3] = 0x00; pdu[4] = 0x00; pdu[5] = 0x00;
        pdu[6] = 0xAA; pdu[7] = 0x0A; pdu[8] = 0x00; pdu[9] = subElem;
        pdu[10] = (byte)(element & 0xFF); pdu[11] = (byte)((element >> 8) & 0xFF);
        data.CopyTo(pdu, 12);
        WriteHex("      TX:", pdu, pdu.Length);
        var (status, response, _) = pccc.SendRawPduAndGetResponse(pdu);
        if (response != null) WriteHex("      RX:", response, response.Length);
        if (status != 0) { Console.WriteLine($"Failed to write {label} (STS=0x{status:X2})"); return; }
        Console.WriteLine($"{label} written. Verifying...");
        Console.WriteLine($"{label}: {ReadPasswordRaw(pccc, element)}");
    }


// =============================================================================
// SECTION 5 — Self-test suite
// =============================================================================
//
// All SelfTest_* methods receive a SelfTestContext describing what the connected
// device supports. Tests that cannot run safely emit [SKIP] rather than [FAIL].
//
// Test groups:
//   1.  ProcessorInfo         — GetProcessorType(), GetRunMode()
//   2.  DirectoryEnumeration  — GetDataMemory(), mandatory file presence
//   3.  BoundaryConditions    — read all file types, out-of-range error paths
//   4.  Latency               — RTT measurement (min/avg/max)
//   5.  IntegerReadWrite      — N7 round-trips (emulator only)
//   6.  FloatReadWrite        — F8 round-trips, element-count aware (emulator only)
//   7.  BitReadWrite          — B3 bit set/clear via FNC 0xAB (emulator, not PLC-5)
//   8.  MultiElementRead      — burst read N7 and F8 (emulator only)
//   9.  MultiElementWrite     — burst write N7 (emulator only)
//   10. ProcessorMode         — SetRunMode/SetProgramMode (emulator only)
//   11. ReadModifyWrite       — FNC 0x26 SLC-style (emulator only, not PLC-5)
//   11b.Plc5ReadModifyWrite   — FNC 0x26 PLC-5 logical binary addressing (emulator, PLC-5 only)
//   12. StringReadWrite       — ST file round-trips, file-aware (emulator only)
//   13. InitializeMemory      — FNC 0x57 (emulator only)
//   14. LinkParameters        — FNC 0x09/0x0A (emulator only, SLC serial only)
// =============================================================================

    private static int _testPass = 0;
    private static int _testFail = 0;

    private static void TestResult(string description, bool pass, string detail = "")
    {
        if (pass) { _testPass++; Console.WriteLine($"  [PASS] {description}"); }
        else       { _testFail++; Console.WriteLine($"  [FAIL] {description}{(string.IsNullOrEmpty(detail) ? "" : $"  ({detail})")}"); }
    }

    private static void TestSkip(string description, string reason)
        => Console.WriteLine($"  [SKIP] {description}  ({reason})");

    private static bool IsAddressAbsent(string msg)
        => msg.Contains("Illegal Command",    StringComparison.OrdinalIgnoreCase)
        || msg.Contains("Invalid Address",    StringComparison.OrdinalIgnoreCase)
        || msg.Contains("Addressing problem", StringComparison.OrdinalIgnoreCase);

    private static bool IsFeatureUnsupported(string msg)
        => msg.Contains("not yet implemented", StringComparison.OrdinalIgnoreCase)
        || msg.Contains("not supported",        StringComparison.OrdinalIgnoreCase)
        || msg.Contains("does not support",     StringComparison.OrdinalIgnoreCase)
        || msg.Contains("requires EIP",         StringComparison.OrdinalIgnoreCase);

    private static T? TryTest<T>(Func<T> action, out string error)
    {
        error = "";
        try   { return action(); }
        catch (Exception ex) { error = ex.Message; return default; }
    }

    /// <summary>Silently attempt a write; failure surfaces in subsequent read-back.</summary>
    private static void TryWrite(Action write) => TryTest(() => { write(); return true; }, out _);

    // ── Context builder ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a SelfTestContext from a single GetDiagnosticStatusRaw call plus
    /// an optional GetDataMemory call. Shared by RunSelfTest and RunDemo.
    /// </summary>
    private static SelfTestContext BuildSelfTestContext(Comm.PCCCComm pccc, Config cfg)
    {
        var family = Comm.Pccc.PCCCConstants.ProcessorFamily.SlcMicroLogix;
        try
        {
            byte[]? diag = pccc.GetDiagnosticStatusRaw();
            if (diag != null) family = Comm.Pccc.PCCCConstants.DetectFamily(diag);
        }
        catch { }

        Comm.DataFileDetails[]? files = null;
        try { files = pccc.GetDataMemory(); } catch { }

        return new SelfTestContext { Family = family, Transport = cfg.Transport, Files = files };
    }

    // ── Entry point ───────────────────────────────────────────────────────────

    private static void RunSelfTest(Comm.PCCCComm pccc, string[] parts, Config cfg)
    {
        bool emulatorMode = parts.Any(p => p.Equals("--emulator", StringComparison.OrdinalIgnoreCase));
        bool force        = parts.Any(p => p.Equals("--force",    StringComparison.OrdinalIgnoreCase));
        _testPass = 0; _testFail = 0;

        var ctx = BuildSelfTestContext(pccc, cfg);

        const int boxWidth = 48;

        Console.WriteLine("╔" + new string('═', boxWidth) + "╗");
        Console.WriteLine("║" + "         PCCCComm Self-Test Suite".PadRight(boxWidth) + "║");
        Console.WriteLine("╚" + new string('═', boxWidth) + "╝");
        Console.WriteLine($"  Family   : {ctx.Family}");
        Console.WriteLine($"  Transport: {cfg.Transport.ToUpperInvariant()}");
        if (emulatorMode)
        {
            Console.WriteLine("  Mode     : EMULATOR — full suite");
            Console.WriteLine("  Target   : PCCCEmulator only. NEVER use on a real PLC.");
            Console.WriteLine($"  Reads    : O0 I1 S2 B3 T4 C5 R6 N7 F8 ST (all file types)");
            Console.WriteLine($"  Writes to: N7 F8 B3 ST (element-count aware)");
            Console.WriteLine($"  Also runs: InitializeMemory (clears ALL data files)");

            // Safety confirmation — require explicit --force or interactive yes
            // to proceed with destructive tests against what may be a real PLC.
            if (!force)
            {
                Console.WriteLine();
                Console.Write("  Proceed with emulator test suite? This WRITES data and clears memory. (yes/no): ");
                string answer = Console.ReadLine()?.Trim().ToLowerInvariant() ?? "";
                if (answer != "yes" && answer != "y")
                {
                    Console.WriteLine("  Aborted. Add --force to skip this prompt.");
                    return;
                }
            }
        }
        else
        {
            Console.WriteLine("  Mode     : READ-ONLY — safe on any live PLC");
            Console.WriteLine("  Use 'selftest --emulator' for full suite (emulator only).");
        }
        Console.WriteLine();

        var sw = Stopwatch.StartNew();

        // Read-only tests — always run
        SelfTest_ProcessorInfo(pccc, ctx);
        SelfTest_DirectoryEnumeration(pccc, ctx);
        SelfTest_BoundaryConditions(pccc, ctx);
        SelfTest_Latency(pccc);

        // Emulator-only tests
        if (emulatorMode)
        {
            SelfTest_IntegerReadWrite(pccc, ctx);
            SelfTest_FloatReadWrite(pccc, ctx);
            SelfTest_BitReadWrite(pccc, ctx);
            SelfTest_MultiElementRead(pccc, ctx);
            SelfTest_MultiElementWrite(pccc, ctx);
            SelfTest_ProcessorMode(pccc);
            SelfTest_ReadModifyWrite(pccc, ctx);
            SelfTest_Plc5ReadModifyWrite(pccc, ctx);
            SelfTest_StringReadWrite(pccc, ctx);
            SelfTest_InitializeMemory(pccc, ctx);
            SelfTest_LinkParameters(pccc, ctx);
        }

        sw.Stop();
        int total = _testPass + _testFail;
        Console.WriteLine();
        Console.WriteLine("╔" + new string('═', boxWidth) + "╗");
        string line1 = $"  {_testPass}/{total} passed  —  {(_testFail == 0 ? "ALL PASS" : $"{_testFail} FAILED")}";
        string line2 = $"  Elapsed: {sw.ElapsedMilliseconds} ms";
        string line3 = $"  [SKIP] = address absent on this PLC model";
        Console.WriteLine("║" + line1.PadRight(boxWidth) + "║");
        Console.WriteLine("║" + line2.PadRight(boxWidth) + "║");
        Console.WriteLine("║" + line3.PadRight(boxWidth) + "║");
        Console.WriteLine("╚" + new string('═', boxWidth) + "╝");
    }

    // ── Test group 1: Processor identification ────────────────────────────────

    private static void SelfTest_ProcessorInfo(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── Processor Info ───────────────────────────────");
        int procType = TryTest(() => pccc.GetProcessorType(), out string e1);
        TestResult("GetProcessorType() returns non-zero", procType != 0,
                   procType != 0 ? $"0x{procType:X2}" : e1);
        int runMode = TryTest(() => pccc.GetRunMode(), out string e2);
        TestResult("GetRunMode() returns 0 or 1", runMode == 0 || runMode == 1,
                   runMode == 0 ? "PROGRAM" : runMode == 1 ? "RUN" : e2);
    }

    // ── Test group 2: Directory enumeration ──────────────────────────────────

    private static void SelfTest_DirectoryEnumeration(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── Directory Enumeration ────────────────────────");
        if (ctx.Files == null)
        {
            TryTest(() => pccc.GetDataMemory(), out string err);
            if (IsFeatureUnsupported(err)) TestSkip("GetDataMemory()", err.Split('.')[0]);
            else TestResult("GetDataMemory() returns non-null array", false, err);
            return;
        }
        TestResult("GetDataMemory() returns non-null array", true);
        TestResult("Directory contains at least 6 data files", ctx.Files.Length >= 6, $"got {ctx.Files.Length}");
        foreach (var (num, name) in new[] { (0,"O0"),(1,"I1"),(2,"S2"),(3,"B3"),(7,"N7"),(8,"F8") })
            TestResult($"Directory contains {name} (file {num})", ctx.Files.Any(f => f.FileNumber == num));
    }

    // ── Test group 3: Boundary conditions ────────────────────────────────────

    private static void SelfTest_BoundaryConditions(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── Boundary Conditions ──────────────────────────");

        (string addr, string label)[] readable =
        {
            ("O0:0",     "O0:0      output image word 0"),
            ("I1:0",     "I1:0      input image word 0"),
            ("S2:0",     "S2:0      status word 0 (fault bits)"),
            ("S2:1",     "S2:1      status word 1 (mode/type info)"),
            ("B3:0",     "B3:0      binary word 0"),
            ("T4:0.PRE", "T4:0.PRE  timer 0 preset (FNC 0xA2 sub-element)"),
            ("T4:0.ACC", "T4:0.ACC  timer 0 accumulated (FNC 0xA2 sub-element)"),
            ("C5:0.PRE", "C5:0.PRE  counter 0 preset (FNC 0xA2 sub-element)"),
            ("C5:0.ACC", "C5:0.ACC  counter 0 accumulated (FNC 0xA2 sub-element)"),
            ("R6:0.LEN", "R6:0.LEN  control 0 length (FNC 0xA2 sub-element)"),
            ("N7:0",     "N7:0      integer word 0"),
            ("F8:0",     "F8:0      float element 0"),
        };

        foreach (var (addr, label) in readable)
        {
            string? val = TryTest(() => pccc.ReadAny(addr), out string err);
            if (val != null)             TestResult($"Read {label}", true);
            else if (IsAddressAbsent(err)) TestSkip($"Read {label}", "not present in this PLC program");
            else                          TestResult($"Read {label}", false, err);
        }

        // ST — use whichever ST file exists in the directory
        var stFile = ctx.FindStFile();
        if (stFile != null)
        {
            string stAddr = $"ST{stFile.FileNumber}:0";
            string label  = $"{stAddr}    string element 0 (SLC 5/03+ and all ML)";
            string? val = TryTest(() => pccc.ReadAny(stAddr), out string err);
            if (val != null)             TestResult($"Read {label}", true);
            else if (IsAddressAbsent(err)) TestSkip($"Read {label}", "not present");
            else                          TestResult($"Read {label}", false, err);
        }
        else
            TestSkip("Read ST:0  string element 0 (SLC 5/03+ and all ML)", "no ST file in directory");

        // Out-of-range: element far beyond any realistic file size
        bool outOfRange = false;
        try { pccc.ReadAny("N7:400"); } catch { outOfRange = true; }
        TestResult("Read N7:400 (out of range) throws exception", outOfRange);

        bool notFound = false;
        try { pccc.ReadAny("N100:0"); } catch { notFound = true; }
        TestResult("Read N100:0 (non-existent file) throws exception", notFound);
    }

    // ── Test group 4: Latency ─────────────────────────────────────────────────

    private static void SelfTest_Latency(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Latency Measurement ──────────────────────────");
        const int warmup = 1, samples = 20;
        var latencies = new List<double>(samples);
        for (int i = 0; i < warmup + samples; i++)
        {
            var t = Stopwatch.StartNew();
            TryTest(() => pccc.ReadAny("N7:0"), out _);
            t.Stop();
            if (i >= warmup) latencies.Add(t.Elapsed.TotalMilliseconds);
        }
        if (latencies.Count > 0)
        {
            Console.WriteLine($"  Samples : {latencies.Count} reads of N7:0");
            Console.WriteLine($"  Min     : {latencies.Min():F1} ms");
            Console.WriteLine($"  Avg     : {latencies.Average():F1} ms");
            Console.WriteLine($"  Max     : {latencies.Max():F1} ms");
            TestResult("Average latency < 200 ms (configuration check)",
                       latencies.Average() < 200.0, $"{latencies.Average():F1} ms avg");
        }
        else TestResult("Latency measurement collected samples", false, "no samples");
    }

    // ── Test group 5: Integer read/write ─────────────────────────────────────

    private static void SelfTest_IntegerReadWrite(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── Integer Read/Write (N7) ──────────────────────");
        if (!ctx.CanAccess(7, 6)) { TestSkip("Integer Read/Write", ctx.SkipReason(7, "N7", 7)); return; }

        (string addr, int value, string label)[] cases =
        {
            ("N7:2",  1234,   "positive"),
            ("N7:3",  0,      "zero"),
            ("N7:4",  -5678,  "negative"),
            ("N7:5",  32767,  "int16 max"),
            ("N7:6",  -32768, "int16 min"),
        };
        foreach (var (addr, value, label) in cases)
        {
            TryWrite(() => pccc.WriteData(addr, value));
            string? raw = TryTest(() => pccc.ReadAny(addr), out string readErr);
            bool ok = int.TryParse(raw, out int rb) && rb == value;
            TestResult($"N7 round-trip {label} ({value})", ok,
                       ok ? $"= {rb}" : $"wrote {value}, got '{raw ?? readErr}'");
        }
    }

    // ── Test group 6: Float read/write ───────────────────────────────────────

    private static void SelfTest_FloatReadWrite(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── Float Read/Write (F8) ────────────────────────");
        int maxF8 = ctx.ElementCount(8);
        if (!ctx.DirectoryAvailable) { TestSkip("Float Read/Write", ctx.SkipReason(8, "F8", 1)); return; }
        if (maxF8 < 1) { TestSkip("Float Read/Write", "F8 not present"); return; }

        const double tol = 1e-4;
        (int elem, float value, string label)[] allCases =
        {
            (0, 3.14159f,      "pi"),
            (1, 0.0f,          "zero"),
            (2, -273.15f,      "negative"),
            (3, 1e6f,          "large positive"),
            (4, 1.175494e-38f, "near float min"),
            (5, -0.0f,         "negative zero"),
        };
        foreach (var (elem, value, label) in allCases)
        {
            if (elem >= maxF8) { TestSkip($"F8 round-trip {label}", $"F8 has only {maxF8} element(s)"); continue; }
            string addr = $"F8:{elem}";
            TryWrite(() => pccc.WriteData(addr, value));
            string? raw = TryTest(() => pccc.ReadAny(addr), out string readErr);
            bool parsed = double.TryParse(raw, System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture, out double rb);
            bool ok = parsed && Math.Abs(rb - value) <= tol;
            TestResult($"F8 round-trip {label} ({value:G6})", ok,
                       ok ? $"= {rb:G6}" : $"wrote {value:G6}, got '{raw ?? readErr}'");
        }
    }

    // ── Test group 7: Bit read/write ─────────────────────────────────────────

    private static void SelfTest_BitReadWrite(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("─── Bit Read/Write (B3, FNC=0xAB) ───────────────");
        if (!ctx.SupportsSlcRmw) { TestSkip("Bit Read/Write", "FNC 0xAB not supported on PLC-5"); return; }
        int maxB3 = ctx.ElementCount(3);
        if (!ctx.DirectoryAvailable) { TestSkip("Bit Read/Write", ctx.SkipReason(3, "B3", 1)); return; }
        if (maxB3 < 1) { TestSkip("Bit Read/Write", "B3 not present"); return; }

        // Basic bit test on B3:0 (always available)
        TryWrite(() => pccc.WriteData("B3:0", 0));
        TryWrite(() => pccc.WriteData("B3:0/0",  1));
        TryWrite(() => pccc.WriteData("B3:0/4",  1));
        TryWrite(() => pccc.WriteData("B3:0/15", 1));
        string? rawSet = TryTest(() => pccc.ReadAny("B3:0"), out _);
        int expSet = (1 << 0) | (1 << 4) | (1 << 15);
        bool okSet = int.TryParse(rawSet, out int sv) && sv == expSet;
        TestResult($"Bit set: bits 0,4,15 → word=0x{expSet:X4} ({expSet})", okSet,
                   okSet ? $"= 0x{sv:X4}" : $"got '{rawSet}'");

        TryWrite(() => pccc.WriteData("B3:0/4", 0));
        string? rawClr = TryTest(() => pccc.ReadAny("B3:0"), out _);
        int expClr = (1 << 0) | (1 << 15);
        bool okClr = int.TryParse(rawClr, out int cv) && cv == expClr;
        TestResult($"Bit clear: clear bit 4 → word=0x{expClr:X4} ({expClr})", okClr,
                   okClr ? $"= 0x{cv:X4}" : $"got '{rawClr}'");

        // All-bits test on B3:1 (only if available)
        if (maxB3 >= 2)
        {
            TryWrite(() => pccc.WriteData("B3:1", 0));
            for (int bit = 0; bit < 16; bit++) { int b = bit; TryWrite(() => pccc.WriteData($"B3:1/{b}", 1)); }
            string? rawAll = TryTest(() => pccc.ReadAny("B3:1"), out _);
            bool okAll = int.TryParse(rawAll, out int av) && (av == -1 || av == 65535);
            TestResult("All 16 bits set → word=0xFFFF (−1 signed)", okAll,
                       okAll ? $"= {av}" : $"got '{rawAll}'");
        }
        else
            TestSkip("All 16 bits set (B3:1)", $"B3 has only {maxB3} word(s)");
    }

    // ── Test group 8: Multi-element read ─────────────────────────────────────

    private static void SelfTest_MultiElementRead(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── Multi-Element Read ───────────────────────────");
        if (ctx.CanAccess(7, 6))
        {
            int[] seed = { 1234, 0, -5678, 32767, -32768 };
            for (int i = 0; i < seed.Length; i++) { int ii = i; TryWrite(() => pccc.WriteData($"N7:{2 + ii}", seed[ii])); }
            string[]? result = TryTest(() => pccc.ReadAny("N7:2", 5), out string err);
            TestResult("ReadAny(N7:2, 5) returns 5 elements", result?.Length == 5, result == null ? err : $"got {result.Length}");
            if (result?.Length == 5)
                for (int i = 0; i < seed.Length; i++)
                {
                    bool ok = int.TryParse(result[i], out int v) && v == seed[i];
                    TestResult($"  Multi-read N7 element [{i}] = {seed[i]}", ok, ok ? "" : $"got '{result[i]}'");
                }
        }
        else TestSkip("ReadAny(N7:2, 5)", ctx.SkipReason(7, "N7", 7));

        int maxF8 = ctx.ElementCount(8);
        if (maxF8 >= 5)
        {
            float[] fseed = { 1.1f, 2.2f, 3.3f };
            for (int i = 0; i < fseed.Length; i++) { int ii = i; TryWrite(() => pccc.WriteData($"F8:{2 + ii}", fseed[ii])); }
            string[]? fr = TryTest(() => pccc.ReadAny("F8:2", 3), out string ferr);
            TestResult("ReadAny(F8:2, 3) returns 3 float elements", fr?.Length == 3, fr == null ? ferr : $"got {fr.Length}");
        }
        else TestSkip($"ReadAny(F8:2, 3)", ctx.SkipReason(8, "F8", 5));
    }

    // ── Test group 9: Multi-element write ────────────────────────────────────

    private static void SelfTest_MultiElementWrite(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── Multi-Element Write ──────────────────────────");
        if (!ctx.CanAccess(7, 6)) { TestSkip("Multi-Element Write", ctx.SkipReason(7, "N7", 7)); return; }
        int[] toWrite = { 11, 22, 33, 44, 55 };
        TryWrite(() => pccc.WriteData("N7:2", toWrite.Length, toWrite));
        for (int i = 0; i < toWrite.Length; i++)
        {
            int ii = i;
            string? raw = TryTest(() => pccc.ReadAny($"N7:{2 + ii}"), out _);
            bool ok = int.TryParse(raw, out int v) && v == toWrite[ii];
            TestResult($"  Multi-write N7 element [{ii}] = {toWrite[ii]}", ok, ok ? "" : $"got '{raw}'");
        }
    }

    // ── Test group 10: Processor mode ─────────────────────────────────────────

    private static void SelfTest_ProcessorMode(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Processor Mode Switching ─────────────────────");
        int original = TryTest(() => pccc.GetRunMode(), out _);
        TryWrite(() => pccc.SetProgramMode());
        int prog = TryTest(() => pccc.GetRunMode(), out string e1);
        TestResult("SetProgramMode() → GetRunMode() = 0", prog == 0, prog == 0 ? "PROGRAM" : $"got {prog}  {e1}");
        TryWrite(() => pccc.SetRunMode());
        int run = TryTest(() => pccc.GetRunMode(), out string e2);
        TestResult("SetRunMode() → GetRunMode() = 1", run == 1, run == 1 ? "RUN" : $"got {run}  {e2}");
        if (original == 0) TryWrite(() => pccc.SetProgramMode()); else TryWrite(() => pccc.SetRunMode());
    }

    // ── Test group 11: Read-Modify-Write ─────────────────────────────────────

    private static void SelfTest_ReadModifyWrite(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── Read-Modify-Write Test ───────────────────────");
        if (!ctx.SupportsSlcRmw)
        { TestSkip("Read-Modify-Write", "FNC 0x26 SLC-style addressing not supported on PLC-5"); return; }
        if (!ctx.CanAccess(3, 1))
        { TestSkip("Read-Modify-Write", ctx.SkipReason(3, "B3", 2)); return; }

        TryWrite(() => pccc.WriteData("B3:1", 0));
        // AND=0xFFFF OR=0x0005 → set bits 0 and 2
        var (s1, _, _) = pccc.SendRawPduAndGetResponse(BuildPdu(pccc, 0x0F, 0x26, 3, 0x85, 1, 0, 0xFF, 0xFF, 0x05, 0x00));
        TestResult("RMW preparation", s1 == 0, s1 != 0 ? $"status 0x{s1:X2}" : "");
        string? val = TryTest(() => pccc.ReadAny("B3:1"), out string re1);
        bool ok = int.TryParse(val, out int iv) && iv == 5;
        TestResult("RMW set bits 0 and 2 → value 5", ok, ok ? "" : $"got '{val ?? re1}'");

        // AND=0xFFFE OR=0x0000 → clear bit 0
        var (s2, _, _) = pccc.SendRawPduAndGetResponse(BuildPdu(pccc, 0x0F, 0x26, 3, 0x85, 1, 0, 0xFE, 0xFF, 0x00, 0x00));
        TestResult("RMW clear", s2 == 0, s2 != 0 ? $"status 0x{s2:X2}" : "");
        val = TryTest(() => pccc.ReadAny("B3:1"), out string re2);
        ok = int.TryParse(val, out iv) && iv == 4;
        TestResult("RMW clear bit 0 → value 4 (bit 2 only)", ok, ok ? "" : $"got '{val ?? re2}'");
    }

    // ── Test group 11b: PLC-5 Read-Modify-Write (logical binary addressing) ──

    /// <summary>
    /// Exercises the PLC-5-specific FNC 0x26 wire format via WriteData("N7:x/n", ...), which
    /// routes through Plc5Handler.WriteData(string,int) -> PCCCProtocol.ReadModifyWritePlc5.
    /// This is a distinct code path from SelfTest_ReadModifyWrite above (SLC-style FNC 0x26,
    /// raw fileNumber/fileType/element/subElement + fixed 2-byte masks) — PLC-5 instead sends
    /// PLC-5 logical binary addressing with AND/OR masks sized to the target element. Without
    /// this test, that path had no automated coverage: both SelfTest_BitReadWrite and
    /// SelfTest_ReadModifyWrite skip entirely on PLC-5 via ctx.SupportsSlcRmw.
    /// Also covers the multi-address PCCCComm.ReadModifyWrite() facade, which now routes to
    /// Plc5Handler.ReadModifyWrite (one logical-binary FNC 0x26 per set) on PLC-5 instead of
    /// throwing NotSupported.
    /// </summary>
    private static void SelfTest_Plc5ReadModifyWrite(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── PLC-5 Read-Modify-Write Test (bit-level via N7) ──");
        if (!ctx.IsPlc5)
        { TestSkip("PLC-5 Read-Modify-Write", "only applies to PLC-5 (SLC/ML1400 covered by SelfTest_BitReadWrite / SelfTest_ReadModifyWrite instead)"); return; }
        if (!ctx.CanAccess(7, 0))
        { TestSkip("PLC-5 Read-Modify-Write", ctx.SkipReason(7, "N7", 1)); return; }

        TryWrite(() => pccc.WriteData("N7:0", 0));
        TryWrite(() => pccc.WriteData("N7:0/0", 1));
        TryWrite(() => pccc.WriteData("N7:0/4", 1));
        TryWrite(() => pccc.WriteData("N7:0/15", 1));
        string? rawSet = TryTest(() => pccc.ReadAny("N7:0"), out string errSet);
        int expSet = (1 << 0) | (1 << 4) | (1 << 15);
        short expSetSigned = (short)expSet;
        bool okSet = int.TryParse(rawSet, out int sv) && sv == expSetSigned;
        TestResult($"PLC-5 RMW set bits 0,4,15 → N7:0 = {expSetSigned}", okSet,
                   okSet ? $"= {sv}" : $"got '{rawSet ?? errSet}'");

        TryWrite(() => pccc.WriteData("N7:0/4", 0));
        string? rawClr = TryTest(() => pccc.ReadAny("N7:0"), out string errClr);
        int expClr = (1 << 0) | (1 << 15);
        short expClrSigned = (short)expClr;
        bool okClr = int.TryParse(rawClr, out int cv) && cv == expClrSigned;
        TestResult($"PLC-5 RMW clear bit 4 → N7:0 = {expClrSigned}", okClr,
                   okClr ? $"= {cv}" : $"got '{rawClr ?? errClr}'");

        // Regression guard for the AND-mask width bug class: confirm bit 0 and 15
        // (in a different byte of the 2-byte element) both survived the clear of bit 4
        // untouched — proves the mask was sized/aligned to the whole element, not just
        // clipped to a single byte.
        bool bothSurvived = okClr && cv == expClrSigned;
        TestResult("PLC-5 RMW mask width covers full element (bits 0 and 15 both survived)",
                   bothSurvived, bothSurvived ? "" : $"expected {expClrSigned}, got {cv}");

        // Coverage for the multi-address facade PCCCComm.ReadModifyWrite -> Plc5Handler.
        // ReadModifyWrite (one logical-binary FNC 0x26 per set) — a distinct entry point from
        // the WriteData bit path above. Word-level masks: AND 0xFFFF keeps all bits, OR sets
        // bits 0, 4, 15.
        TryWrite(() => pccc.WriteData("N7:0", 0));
        int facSts = -1;
        TryWrite(() => facSts = pccc.ReadModifyWrite(
            new[] { "N7:0" }, new ushort[] { 0xFFFF }, new ushort[] { (ushort)expSet }));
        string? rawFac = TryTest(() => pccc.ReadAny("N7:0"), out string errFac);
        bool okFac = facSts == 0 && int.TryParse(rawFac, out int fv) && fv == expSetSigned;
        TestResult($"PLC-5 RMW via ReadModifyWrite() facade -> N7:0 = {expSetSigned}", okFac,
                   okFac ? "" : $"status 0x{facSts:X2}, got '{rawFac ?? errFac}'");
    }

    // ── Test group 12: String read/write ─────────────────────────────────────

    private static void SelfTest_StringReadWrite(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── String Read/Write (ST file) ──────────────────");
        var stFile = ctx.FindStFile();
        if (stFile == null) { TestSkip("String Read/Write", "no ST file in directory"); return; }

        int stNum = stFile.FileNumber, maxSt = stFile.NumberOfElements;
        string? seed = TryTest(() => pccc.ReadAny($"ST{stNum}:0"), out _);
        TestResult($"ST{stNum}:0 contains emulator seed \"EMULATOR OK\"", seed == "EMULATOR OK", $"got '{seed}'");

        (int elem, string value, string label)[] cases =
        {
            (2, "Hello",                    "short ASCII"),
            (3, "",                         "empty string"),
            (4, "PCCCComm v1.0 - test OK!", "mixed chars"),
            (5, new string('A', 82),        "max length (82 chars)"),
        };
        foreach (var (elem, value, label) in cases)
        {
            if (elem >= maxSt) { TestSkip($"ST round-trip: {label}", $"ST{stNum} has only {maxSt} element(s)"); continue; }
            string addr = $"ST{stNum}:{elem}";
            TryWrite(() => pccc.WriteData(addr, value));
            string? raw = TryTest(() => pccc.ReadAny(addr), out string readErr);
            bool ok = raw == value;
            TestResult($"ST round-trip: {label}", ok,
                       ok ? $"len={value.Length}" : $"expected '{value}', got '{raw ?? readErr}'");
        }
    }

    // ── Test group 13: Initialize Memory ─────────────────────────────────────

    private static void SelfTest_InitializeMemory(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── Initialize Memory Test ───────────────────────");
        bool prepOk = true;
        try { pccc.WriteData("N7:3", 0x1234); }
        catch (Exception ex) { TestResult("InitializeMemory preparation (N7:3)", false, ex.Message); prepOk = false; }
        if (!prepOk) return;

        // Also write to ST if available for a second verification point
        var stFile = ctx.FindStFile();
        string? stAddr = null;
        if (stFile != null && stFile.NumberOfElements >= 3)
        {
            stAddr = $"ST{stFile.FileNumber}:2";
            try { pccc.WriteData(stAddr, "INIT_TEST"); } catch { stAddr = null; }
        }

        var (status, _, _) = pccc.SendRawPduAndGetResponse(BuildPdu(pccc, 0x0F, 0x57));

        // Emulator sends ACK before executing ResetToDefault(), so status should
        // always be 0 for all transports.
        TestResult("InitializeMemory() call", status == 0, status != 0 ? $"status 0x{status:X2}" : "");
        if (status != 0) return;

        // Reset executes after ACK — give the emulator a moment to finish,
        // Reset runs synchronously after ACK is enqueued. Allow sufficient time
        // for the reset to complete before reading back (PLC-5 has ~32KB flat memory).
        const int maxRetries = 3;
        string? n7val = null;
        for (int i = 0; i < maxRetries; i++)
        {
            Thread.Sleep(500);
            n7val = TryTest(() => pccc.ReadAny("N7:3"), out _);
            if (n7val != null) break;
            try { pccc.CloseComms(); pccc.OpenComms(); } catch { }
        }
        TestResult("N7:3 reset to 0 after InitializeMemory", n7val == "0",
                   n7val == "0" ? "" : $"got '{n7val ?? "(no response)"}'");

        if (stAddr != null)
        {
            string? stval = TryTest(() => pccc.ReadAny(stAddr), out string e4);
            TestResult($"{stAddr} reset to empty after InitializeMemory", stval == "",
                       stval == "" ? "" : $"got '{stval ?? e4}'");
        }
    }

    // ── Test group 14: Link Parameters ───────────────────────────────────────

    private static void SelfTest_LinkParameters(Comm.PCCCComm pccc, SelfTestContext ctx)
    {
        Console.WriteLine("── Link Parameters Test ─────────────────────────");
        if (!ctx.SupportsLinkParams)
        {
            string reason = ctx.IsPlc5    ? "not applicable to PLC-5" :
                            !ctx.IsSerial ? $"not meaningful over {ctx.Transport.ToUpperInvariant()}" :
                                            "not supported on this family";
            TestSkip("Link Parameters", reason);
            return;
        }

        var (s1, r1, _) = pccc.SendRawPduAndGetResponse(BuildPdu(pccc, 0x06, 0x09));
        byte defaultMax = (s1 == 0 && r1?.Length >= 7) ? r1[6] : (byte)0;
        TestResult("ReadLinkParameters default = 31", defaultMax == 31, defaultMax == 31 ? "" : $"got {defaultMax}");

        var (s2, _, _) = pccc.SendRawPduAndGetResponse(BuildPdu(pccc, 0x06, 0x0A, 15));
        if (s2 != 0) { TestResult("SetLinkParameters(15)", false, $"status 0x{s2:X2}"); return; }

        var (s3, r3, _) = pccc.SendRawPduAndGetResponse(BuildPdu(pccc, 0x06, 0x09));
        byte newMax = (s3 == 0 && r3?.Length >= 7) ? r3[6] : (byte)0;
        TestResult("ReadLinkParameters returns 15 after set", newMax == 15, $"got {newMax}");
    }

    // ── PDU builder ───────────────────────────────────────────────────────────

    private static byte[] BuildPdu(Comm.PCCCComm pccc, byte cmd, byte fnc, params byte[] data)
    {
        byte[] pdu = new byte[7 + data.Length];
        pdu[0] = (byte)pccc.TargetNode; pdu[1] = (byte)pccc.MyNode;
        pdu[2] = cmd; pdu[3] = 0x00; pdu[4] = 0x00; pdu[5] = 0x00; pdu[6] = fnc;
        data.CopyTo(pdu, 7);
        return pdu;
    }


// =============================================================================
// SECTION 6 — Communication statistics
// =============================================================================

    private static long _totalRequests = 0, _successRequests = 0, _timeouts = 0, _naks = 0, _otherErrors = 0;

    private static void RecordSuccess()    { Interlocked.Increment(ref _totalRequests); Interlocked.Increment(ref _successRequests); }
    private static void RecordTimeout()    { Interlocked.Increment(ref _totalRequests); Interlocked.Increment(ref _timeouts); }
    private static void RecordNak()        { Interlocked.Increment(ref _totalRequests); Interlocked.Increment(ref _naks); }
    private static void RecordOtherError() { Interlocked.Increment(ref _totalRequests); Interlocked.Increment(ref _otherErrors); }

    private static void PrintStats()
    {
        long errors = _timeouts + _naks + _otherErrors;
        Console.WriteLine("\n=== Communication Statistics ===");
        Console.WriteLine($"Total requests   : {_totalRequests}");
        Console.WriteLine($"Successful       : {_successRequests}");
        Console.WriteLine($"Timeouts         : {_timeouts}");
        Console.WriteLine($"NAK responses    : {_naks}");
        Console.WriteLine($"Other errors     : {_otherErrors}");
        if (_totalRequests > 0)
            Console.WriteLine($"Error rate       : {(double)errors / _totalRequests * 100:F2}%");
        Console.WriteLine("=================================");
    }

    private static void ResetStats()
    {
        Interlocked.Exchange(ref _totalRequests,   0);
        Interlocked.Exchange(ref _successRequests, 0);
        Interlocked.Exchange(ref _timeouts,        0);
        Interlocked.Exchange(ref _naks,            0);
        Interlocked.Exchange(ref _otherErrors,     0);
        Console.WriteLine("Statistics reset.");
    }

    private static T? Execute<T>(Func<T> action, string context = "")
    {
        try { T result = action(); RecordSuccess(); return result; }
        catch (Comm.Pccc.PCCCException ex)
        {
            if      (ex.Message.Contains("NAK"))         RecordNak();
            else if (ex.Message.Contains("No Response") || ex.Message.Contains("Timeout")) RecordTimeout();
            else                                          RecordOtherError();
            Console.WriteLine($"Error {context}: {ex.Message}");
            return default;
        }
        catch (Exception ex) { RecordOtherError(); Console.WriteLine($"Unexpected error {context}: {ex.Message}"); return default; }
    }

    private static void ExecuteVoid(Action action, string context = "")
        => Execute(() => { action(); return true; }, context);

// =============================================================================
// SECTION 7 — Low-level helpers
// =============================================================================

    private static void WriteHex(string prefix, byte[] data, int length)
    {
        if (length <= 0 || data == null) return;
        if (length > data.Length) length = data.Length;
        Console.Write($"{prefix} ");
        WriteHex(Console.Out, data, length);
        Console.WriteLine();
    }

    private static void WriteHex(TextWriter writer, byte[] data, int length)
    {
        for (int i = 0; i < length; i++) { if (i > 0) writer.Write(' '); writer.Write(data[i].ToString("X2")); }
    }

    private static string NormalizePortName(string portName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string[] available = SerialPort.GetPortNames();
            if (!available.Contains(portName, StringComparer.OrdinalIgnoreCase))
                throw new Exception($"Port '{portName}' not found. Available: {string.Join(", ", available)}");
            return portName;
        }
        else
        {
            string baseName = portName.StartsWith("/dev/") ? portName[5..] : portName;
            string fullPath = $"/dev/{baseName}";
            string[] all = Directory.GetFiles("/dev", "tty*");
            if (all.Contains(fullPath)) return fullPath;
            if (all.Contains(portName)) return portName;
            string[] likely = all.Where(p => p.StartsWith("/dev/ttyUSB") || p.StartsWith("/dev/ttyS") || p.StartsWith("/dev/ttyACM")).ToArray();
            throw new Exception($"Port '{portName}' not found. Available tty devices: " + (likely.Length > 0 ? string.Join(", ", likely) : "(none)"));
        }
    }

// =============================================================================
// SECTION 8 — Help text
// =============================================================================

    private static void PrintUsage()
    {
        Console.WriteLine("PCCCComm Example Client");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- [port] [options]");
        Console.WriteLine();
        Console.WriteLine("Transport:");
        Console.WriteLine("  --mode <df1|df1master|eip|csp>   Transport mode (default: df1)");
        Console.WriteLine("  --host <IP>                  PLC IP address (required for EIP and CSP)");
        Console.WriteLine("  --eip-port <n>               EIP TCP port (default: 44818)");
        Console.WriteLine("  --csp-port <n>               CSPv4 TCP port (default: 2222)");
        Console.WriteLine("  --lsap-control <hex>         LSAP control byte for CSPv4 (default: 00)");
        Console.WriteLine("  --timeout <ms>               Network timeout in ms (default: 5000)");
        Console.WriteLine();
        Console.WriteLine("DF1 Serial:");
        Console.WriteLine("  --baud <n>                   Baud rate (default: 19200)");
        Console.WriteLine("  --parity <none|odd|even>     Parity (default: none)");
        Console.WriteLine("  --checksum <crc|bcc>         Checksum mode (default: crc)");
        Console.WriteLine("  --target <n>                 Target node (default: 1)");
        Console.WriteLine("  --mynode <n>                 Local node (default: 0)");
        Console.WriteLine();
        Console.WriteLine("RS-485 (df1master only):");
        Console.WriteLine("  --rs485-mode <auto|rts|dtr>  Direction control (default: auto)");
        Console.WriteLine("  --echo-suppression           Discard self-echoed bytes");
        Console.WriteLine("  --rs485-assert-delay <ms>    Delay after RTS assert (default: 1)");
        Console.WriteLine("  --rs485-deassert-delay <ms>  Delay before RTS deassert (default: 5)");
        Console.WriteLine();
        Console.WriteLine("Behaviour:");
        Console.WriteLine("  --demo                       Run read/write demo before interactive CLI");
        Console.WriteLine("  --no-interactive             Skip interactive CLI");
        Console.WriteLine("  --stress-test [n]            Stress test; n = iterations (0=infinite)");
        Console.WriteLine("  --scan-nodes [from] [to]     Scan DF1 node range (default 1–31)");
        Console.WriteLine("  --help, -h                   Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- COM1");
        Console.WriteLine("  dotnet run -- COM1 --demo");
        Console.WriteLine("  dotnet run -- COM1 --stress-test 500");
        Console.WriteLine("  dotnet run -- COM1 --mode df1master --scan-nodes");
        Console.WriteLine("  dotnet run -- --mode eip --host 127.0.0.1");
        Console.WriteLine("  dotnet run -- --mode csp --host 127.0.0.1 --lsap-control 05");
    }

    private static void PrintInteractiveHelp()
    {
        Console.WriteLine("Data access:");
        Console.WriteLine("  read <addr> [count]            Read one or more elements");
        Console.WriteLine("                                 Example: read N7:0  /  read F8:0 5");
        Console.WriteLine("  write <addr> <val> [val...]    Write values (auto-detects type)");
        Console.WriteLine("                                 Examples: write N7:0 100 / write F8:0 3.14 / write ST18:0 Hello World");
        Console.WriteLine("  datamem                        List all data files configured in PLC");
        Console.WriteLine("  watch <addr> [interval_ms]     Monitor address, print on change (any key to stop)");
        Console.WriteLine();
        Console.WriteLine("Processor:");
        Console.WriteLine("  type                           Show processor type code");
        Console.WriteLine("  mode                           Show current mode (RUN/PROGRAM)");
        Console.WriteLine("  setrun                         [!] Switch to RUN mode");
        Console.WriteLine("  setprog                        [!] Switch to PROGRAM mode");
        Console.WriteLine();
        Console.WriteLine("Password management (MicroLogix 1100/1200/1400 only):");
        Console.WriteLine("  getpass / getmaster            Read password / master password");
        Console.WriteLine("  setpass <pw> / setmaster <pw>  Set password (numeric, ≤10 chars)");
        Console.WriteLine("  clearpass / clearmaster        Clear password");
        Console.WriteLine();
        Console.WriteLine("Diagnostics:");
        Console.WriteLine("  selftest                       Read-only self-test (safe on any live PLC)");
        Console.WriteLine("  selftest --emulator            [!] Full suite — PCCCEmulator only");
        Console.WriteLine("  selftest --emulator --force    [!] Skip confirmation prompt");
        Console.WriteLine("  stats / resetstats             Show or reset communication statistics");
        Console.WriteLine();
        Console.WriteLine("Node management (DF1/RS-485):");
        Console.WriteLine("  scannodes [from] [to]          Scan node range (default: 1–31)");
        Console.WriteLine("  settarget <node>               Change target node at runtime");
        Console.WriteLine("  keepalive [on|off]             Enable/disable periodic connection test (Echo)");
        Console.WriteLine();
        Console.WriteLine("Advanced:");
        Console.WriteLine("  echo [hex byte...]             Send Echo command and verify response");
        Console.WriteLine("  sendhex <DST> <CMD> <FNC> [data...]   Send raw PCCC PDU");
        Console.WriteLine("                                 Example: sendhex 01 06 03");
        Console.WriteLine("  wordread <type> <num> <elem> <offset> <words>  Word Range Read (PLC-5)");
        Console.WriteLine("  wordwrite <type> <num> <elem> <offset> <hex...>  [!] Word Range Write (PLC-5)");
        Console.WriteLine("  typedread <N|F> <num> <elem> [count]           Typed Read (PLC-5, FNC 0x68)");
        Console.WriteLine("  typedwrite <N|F> <num> <elem> <value>          [!] Typed Write (PLC-5, FNC 0x67)");
        Console.WriteLine();
        Console.WriteLine("  [!] = modifies PLC state — use with caution");
        Console.WriteLine("  exit / quit                    Leave interactive mode");
        Console.WriteLine("  help                           This reference");
    }
}

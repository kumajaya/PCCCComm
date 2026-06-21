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
// The file is divided into the following logical sections:
//
//   1. Program class — entry point, argument parsing, transport construction
//   2. Demo         — read/write showcase executed by default on startup
//   3. Stress test  — continuous read loop for throughput and stability testing
//   4. Interactive CLI — command prompt for manual exploration
//   5. Self-test suite — exhaustive pass/fail tests invoked via "selftest" CLI command
//   6. Statistics helpers — counters tracked across demo and stress test
//   7. Low-level helpers — raw PDU sender, port name normalizer, usage printers
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
// This client WRITES data to the connected device (N7, F8, B3, ST18, mode
// changes). Only connect to a real PLC if you fully understand the consequences.
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

/// <summary>
/// Example client for the PCCCComm library.
///
/// Run with --help to see all available options and interactive commands.
/// The self-test suite (interactive "selftest" command) exercises every major
/// feature of the library and reports a pass/fail result for each test case.
/// </summary>
class Program
{
    // =========================================================================
    // Entry point
    // =========================================================================

    static void Main(string[] args)
    {
        // Parse command-line arguments into a settings record.
        if (!TryParseArgs(args, out var cfg))
            return; // TryParseArgs printed usage or an error message.

        // Construct the PCCCComm instance for the requested transport.
        Comm.PCCCComm pccc = BuildTransport(cfg);

        try
        {
            pccc.OpenComms();

            if (cfg.Transport == "eip")
                Console.WriteLine($"EIP session established with {cfg.RemoteHost}:{cfg.EipPort}");
            else if (cfg.Transport == "csp")
                Console.WriteLine($"CSPv4 session established with {cfg.RemoteHost}:{cfg.CspPort}");
            else
                Console.WriteLine("DF1 port opened successfully");
            Console.WriteLine();

            // ── Verify that the target node is reachable ──────────────────────
            // OpenComms() only opens the serial port or TCP socket — it does not
            // send any PCCC traffic. A successful open does NOT mean the target
            // PLC is present or responding. For DF1 serial this is especially
            // important: the port always opens regardless of whether any device
            // is connected to the RS-485 bus or what node address it uses.
            //
            // GetProcessorType() is used as the connectivity probe because it is
            // the lightest round-trip command (CMD=0x06 FNC=0x03, no data payload)
            // and it is the same call that the library uses internally for processor
            // family detection. A failure here gives the user an actionable message
            // before any read or write operations are attempted.
            bool nodeOk = VerifyTargetNode(pccc, cfg);

            // Run the quick demo unless the user asked to skip it, or the node
            // verification failed (no point running a demo against a silent bus).
            if (nodeOk && !cfg.InteractiveOnly)
                RunDemo(pccc);

            // Scan DF1/RS-485 nodes if requested via --scan-nodes.
            // This runs before the stress test so the target node is restored
            // to the originally configured value before stress testing begins.
            // Node scan runs even when verification failed — that is its purpose.
            if (cfg.ScanNodes)
                RunNodeScan(pccc, cfg.ScanFrom, cfg.ScanTo);

            // Run the continuous stress test if requested, but only when the
            // target node is confirmed reachable.
            if (nodeOk && cfg.StressTest)
                RunStressTest(pccc, cfg.StressLoopCount);

            // Enter the interactive CLI unless the user asked to skip it.
            // The CLI is always offered even when verification failed so the
            // user can run scannodes or change the target node and retry.
            if (!cfg.NoInteractive)
                RunInteractiveCli(pccc);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
        }
        finally
        {
            pccc.CloseComms();
            pccc.Dispose();
            Console.WriteLine("\nPress Enter to exit.");
            Console.ReadLine();
        }
    }

    // =========================================================================
    // Command-line configuration record
    // =========================================================================

    /// <summary>
    /// Holds all settings parsed from command-line arguments.
    /// Using a sealed record keeps argument parsing cleanly separated from
    /// business logic and makes the configuration immutable after parsing.
    /// </summary>
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
        public bool   StressTest         { get; init; } = false;
        public int    StressLoopCount    { get; init; } = 0; // 0 = infinite
        public bool   ScanNodes         { get; init; } = false;
        public int    ScanFrom          { get; init; } = 1;
        public int    ScanTo            { get; init; } = 31;
    }

    // =========================================================================
    // Argument parser
    // =========================================================================

    /// <summary>
    /// Parses command-line arguments into a <see cref="Config"/> record.
    /// Returns false (and prints an error or usage text) if parsing should abort.
    /// </summary>
    private static bool TryParseArgs(string[] args, out Config cfg)
    {
        // Mutable locals — converted to the immutable Config at the end.
        string transport          = "df1";
        string portName           = "COM1";
        int    baud               = 19200;
        Parity parity             = Parity.None;
        string rs485Mode          = "auto";
        bool   echoSuppression    = false;
        int    rs485AssertDelay   = 1;
        int    rs485DeassertDelay = 5;
        string remoteHost         = "";
        int    eipPort            = 44818;
        int    cspPort            = 2222;
        int    timeoutMs          = 5000;
        byte   lsapControl        = 0x00;
        int    targetNode         = 1;
        int    myNode             = 0;
        string checksum           = "crc";
        bool   interactiveOnly    = false;
        bool   noInteractive      = false;
        bool   stressTest         = false;
        int    stressLoopCount    = 0;
        bool   scanNodes          = false;
        int    scanFrom           = 1;
        int    scanTo             = 31;

        cfg = new Config(); // satisfy out parameter before early returns

        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i].ToLowerInvariant();

            // Positional argument: serial port name (first arg, no leading "--")
            if (i == 0 && !a.StartsWith("--"))
            {
                portName = args[i];
                continue;
            }

            switch (a)
            {
                case "--mode"    when i + 1 < args.Length: transport  = args[++i].ToLowerInvariant(); break;
                case "--baud"    when i + 1 < args.Length: if (int.TryParse(args[++i], out var b))  baud       = b; break;
                case "--target"  when i + 1 < args.Length: if (int.TryParse(args[++i], out var n))  targetNode = n; break;
                case "--mynode"  when i + 1 < args.Length: if (int.TryParse(args[++i], out var mn)) myNode     = mn; break;
                case "--host"    when i + 1 < args.Length: remoteHost = args[++i]; break;
                case "--eip-port" when i + 1 < args.Length: if (int.TryParse(args[++i], out var p)) eipPort   = p; break;
                case "--csp-port" when i + 1 < args.Length: if (int.TryParse(args[++i], out var c)) cspPort   = c; break;
                case "--timeout"  when i + 1 < args.Length: if (int.TryParse(args[++i], out var t)) timeoutMs = t; break;
                case "--lsap-control" when i + 1 < args.Length:
                    if (byte.TryParse(args[++i], System.Globalization.NumberStyles.HexNumber, null, out byte lsap))
                        lsapControl = lsap;
                    break;
                case "--checksum" when i + 1 < args.Length: checksum  = args[++i].ToLowerInvariant(); break;
                case "--rs485-mode"           when i + 1 < args.Length: rs485Mode          = args[++i].ToLowerInvariant(); break;
                case "--rs485-assert-delay"   when i + 1 < args.Length: if (int.TryParse(args[++i], out var ad)) rs485AssertDelay   = ad; break;
                case "--rs485-deassert-delay" when i + 1 < args.Length: if (int.TryParse(args[++i], out var dd)) rs485DeassertDelay = dd; break;
                case "--echo-suppression":  echoSuppression = true; break;
                case "--interactive-only":  interactiveOnly = true; break;
                case "--no-interactive":    noInteractive   = true; break;
                case "--stress-test":
                    stressTest = true;
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var loops))
                    { stressLoopCount = loops; i++; }
                    break;
                case "--scan-nodes":
                    scanNodes = true;
                    // Optional: --scan-nodes [from] [to]  e.g. --scan-nodes 1 31
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var sf)) { scanFrom = sf; i++; }
                    if (i + 1 < args.Length && int.TryParse(args[i + 1], out var st)) { scanTo   = st; i++; }
                    break;
                case "--parity" when i + 1 < args.Length:
                    parity = args[++i].ToLowerInvariant() switch
                    {
                        "odd"  => Parity.Odd,
                        "even" => Parity.Even,
                        _      => Parity.None,
                    };
                    break;
                case "--help":
                case "-h":
                    PrintUsage();
                    return false;
            }
        }

        // Resolve and validate the serial port name only for DF1 modes.
        if (transport == "df1" || transport == "df1master")
        {
            try   { portName = NormalizePortName(portName); }
            catch (Exception ex) { Console.WriteLine(ex.Message); return false; }
        }

        cfg = new Config
        {
            Transport          = transport,
            PortName           = portName,
            Baud               = baud,
            SerialParity       = parity,
            Rs485Mode          = rs485Mode,
            EchoSuppression    = echoSuppression,
            Rs485AssertDelay   = rs485AssertDelay,
            Rs485DeassertDelay = rs485DeassertDelay,
            RemoteHost         = remoteHost,
            EipPort            = eipPort,
            CspPort            = cspPort,
            TimeoutMs          = timeoutMs,
            LsapControlByte    = lsapControl,
            TargetNode         = targetNode,
            MyNode             = myNode,
            Checksum           = checksum,
            InteractiveOnly    = interactiveOnly,
            NoInteractive      = noInteractive,
            StressTest         = stressTest,
            StressLoopCount    = stressLoopCount,
            ScanNodes          = scanNodes,
            ScanFrom           = scanFrom,
            ScanTo             = scanTo,
        };
        return true;
    }

    // =========================================================================
    // Transport factory
    // =========================================================================

    /// <summary>
    /// Constructs and configures a <see cref="Comm.PCCCComm"/> instance for the
    /// transport mode specified in <paramref name="cfg"/>.
    ///
    /// The connection is NOT opened here; callers must call <c>OpenComms()</c>
    /// after construction so that startup logging appears in the correct order.
    /// </summary>
    private static Comm.PCCCComm BuildTransport(Config cfg)
    {
        Comm.PCCCComm pccc;

        switch (cfg.Transport)
        {
            // ── EtherNet/IP (PCCC-over-CIP) ───────────────────────────────────
            // The EIP constructor takes a host, TCP port, and timeout.
            // No CheckSum property is needed: CIP encapsulation provides its own
            // integrity checking at the transport layer.
            case "eip":
                if (string.IsNullOrEmpty(cfg.RemoteHost))
                    throw new Exception("EIP mode requires --host <IP>");

                pccc = Comm.PCCCComm.ForEip(cfg.RemoteHost, cfg.EipPort, cfg.TimeoutMs);
                pccc.TargetNode = cfg.TargetNode;
                pccc.MyNode     = cfg.MyNode;
                Console.WriteLine($"EIP: Connecting to {cfg.RemoteHost}:{cfg.EipPort} (timeout {cfg.TimeoutMs} ms)");
                break;
            case "csp":
                if (string.IsNullOrEmpty(cfg.RemoteHost))
                    throw new Exception("CSPv4 mode requires --host <IP>");

                pccc = Comm.PCCCComm.ForCsp(cfg.RemoteHost, cfg.CspPort, cfg.TimeoutMs, cfg.LsapControlByte);
                pccc.TargetNode = cfg.TargetNode;
                pccc.MyNode     = cfg.MyNode;
                Console.WriteLine($"CSPv4: Connecting to {cfg.RemoteHost}:{cfg.CspPort} (timeout {cfg.TimeoutMs} ms)");
                break;
            // ── DF1 half-duplex master (RS-485 multi-drop) ────────────────────
            // Used when this machine is the master on an RS-485 bus and the
            // PCCCEmulator (or a real PLC) is configured as a DF1 slave.
            // Rs485Mode controls how the driver enable line (RTS/DTR) is managed:
            //   Auto — relies on the USB adapter's hardware auto-direction
            //   Rts  — toggles RTS manually around each transmitted frame
            //   Dtr  — same but uses the DTR line instead of RTS
            case "df1master":
                pccc = new Comm.PCCCComm(cfg.PortName, cfg.Baud, cfg.SerialParity)
                {
                    Protocol             = "DF1Master",
                    TargetNode           = cfg.TargetNode,
                    SlaveAddress         = cfg.TargetNode,
                    MyNode               = cfg.MyNode,
                    EchoSuppression      = cfg.EchoSuppression,
                    Rs485AssertDelayMs   = cfg.Rs485AssertDelay,
                    Rs485DeassertDelayMs = cfg.Rs485DeassertDelay,
                    Rs485Mode            = cfg.Rs485Mode switch
                    {
                        "rts" => Comm.Core.DF1HalfDuplexTransport.Rs485ControlMode.Rts,
                        "dtr" => Comm.Core.DF1HalfDuplexTransport.Rs485ControlMode.Dtr,
                        _     => Comm.Core.DF1HalfDuplexTransport.Rs485ControlMode.Auto,
                    },
                };
                Console.WriteLine($"DF1 Master: {cfg.PortName} @ {cfg.Baud} baud, " +
                                  $"{cfg.SerialParity} parity, RS-485={cfg.Rs485Mode}");
                Console.WriteLine($"MyNode={cfg.MyNode}, SlaveAddress={cfg.TargetNode}");
                break;

            // ── DF1 full-duplex serial (default) ──────────────────────────────
            // Standard point-to-point DF1 over RS-232 or USB-to-serial adapter.
            // CheckSum defaults to CRC-16; use --checksum bcc for older devices
            // that only support the simpler 8-bit BCC checksum.
            default:
                pccc = new Comm.PCCCComm(cfg.PortName, cfg.Baud, cfg.SerialParity)
                {
                    TargetNode = cfg.TargetNode,
                    MyNode     = cfg.MyNode,
                    CheckSum   = cfg.Checksum == "crc"
                                     ? Comm.Pccc.CheckSumOptions.Crc
                                     : Comm.Pccc.CheckSumOptions.Bcc,
                };
                Console.WriteLine($"DF1: Connecting to {cfg.PortName} @ {cfg.Baud} baud, " +
                                  $"{cfg.SerialParity} parity, checksum={pccc.CheckSum}");
                Console.WriteLine($"MyNode={cfg.MyNode}, TargetNode={cfg.TargetNode}");
                break;
        }

        return pccc;
    }

    // =========================================================================
    // Target node verifier
    // =========================================================================

    /// <summary>
    /// Verifies that the configured target node is reachable by sending a
    /// GetProcessorType probe (CMD=0x06 FNC=0x03) before any demo or test
    /// operations are executed.
    ///
    /// Why this is necessary
    /// ---------------------
    /// For DF1 serial transports, <c>OpenComms()</c> only opens the serial port
    /// or TCP socket — it does not send any PCCC traffic. A successful open does
    /// NOT guarantee that any PLC is present on the bus or that the target node
    /// address is correct. Common mistakes:
    ///
    ///   - Forgetting to pass <c>--target N</c> when the PLC node is not 1.
    ///   - PLC powered off or keyswitch in PROGRAM mode blocking some commands.
    ///   - Wrong COM port or baud rate (port opens but no data flows).
    ///   - RS-485 termination missing, causing all frames to be corrupted.
    ///
    /// Without this guard, all subsequent operations silently fail and the
    /// statistics show 100% error rate with no clear indication of the cause.
    ///
    /// Recovery hint
    /// -------------
    /// If the probe fails, the method prints the error and suggests using
    /// <c>scannodes</c> (for DF1 modes) to discover the actual node address,
    /// or checking the host and port (for EIP mode).
    ///
    /// Interactive mode is still offered after a failed probe so the user can
    /// run <c>scannodes</c> without restarting the client.
    /// </summary>
    /// <param name="pccc">Open PCCCComm instance.</param>
    /// <param name="cfg">Parsed configuration (used for transport-specific hint text).</param>
    /// <returns>True if the target node responded; false otherwise.</returns>
    private static bool VerifyTargetNode(Comm.PCCCComm pccc, Config cfg)
    {
        Console.Write($"Verifying target node {cfg.TargetNode}... ");

        try
        {
            Console.WriteLine($"OK  ({ProcessorTypeName(pccc)})");
            Console.WriteLine();
            return true;
        }
        catch (Comm.Pccc.PCCCException ex)
        {
            Console.WriteLine($"FAILED");
            Console.WriteLine();
            Console.WriteLine($"  Error    : {ex.Message}");
            Console.WriteLine($"  Transport: {cfg.Transport.ToUpperInvariant()}");

            if (cfg.Transport == "eip")
            {
                Console.WriteLine($"  Target   : {cfg.RemoteHost}:{cfg.EipPort}");
                Console.WriteLine();
                Console.WriteLine("  Suggestions:");
                Console.WriteLine("    - Verify the PLC or emulator is running in EIP mode.");
                Console.WriteLine("    - Check that firewall allows TCP port 44818.");
                Console.WriteLine($"    - Confirm --host {cfg.RemoteHost} and --eip-port {cfg.EipPort} are correct.");
            }
            else if (cfg.Transport == "csp")
            {
                Console.WriteLine($"  Target   : {cfg.RemoteHost}:{cfg.CspPort}");
                Console.WriteLine();
                Console.WriteLine("  Suggestions:");
                Console.WriteLine("    - Verify the PLC or emulator is running in CSPv4 mode.");
                Console.WriteLine("    - Check that firewall allows TCP port 2222 (default).");
                Console.WriteLine($"    - Confirm --host {cfg.RemoteHost} and --csp-port {cfg.CspPort} are correct.");
                Console.WriteLine("    - If using RSLinx, try adding --lsap-control 05.");
            }
            else
            {
                Console.WriteLine($"  Port     : {cfg.PortName}  Baud: {cfg.Baud}  Node: {cfg.TargetNode}");
                Console.WriteLine();
                Console.WriteLine("  Suggestions:");
                Console.WriteLine($"    - Use 'scannodes' in the interactive CLI to discover active nodes.");
                Console.WriteLine($"    - Or restart with the correct --target N, e.g.:");

                // Show a ready-to-paste command with the correct flags reconstructed.
                string modeFlag = cfg.Transport == "df1master" ? " --mode df1master" : "";
                string portArg  = cfg.PortName != "COM1" ? $" {cfg.PortName}" : "";
                Console.WriteLine($"        dotnet run -- {portArg}{modeFlag} --target <node>");
                Console.WriteLine();
                Console.WriteLine("    - Verify baud rate, parity, and checksum match the PLC settings.");
                Console.WriteLine("    - For RS-485: check termination resistors and cable polarity.");
            }

            Console.WriteLine();

            // Offer interactive mode so the user can run scannodes without
            // restarting the process. Return false so the demo is skipped.
            if (!cfg.NoInteractive)
            {
                Console.WriteLine("  Entering interactive CLI so you can run 'scannodes' or 'exit'.");
                Console.WriteLine();
            }

            return false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAILED (unexpected error: {ex.Message})");
            Console.WriteLine();
            return false;
        }
    }

// =============================================================================
// SECTION 2 — Demo: quick read/write showcase
// =============================================================================

    /// <summary>
    /// Runs a brief showcase of the most common PCCCComm operations.
    ///
    /// This demo is intentionally non-destructive for most file addresses:
    /// B3:0 is reset to 0 before any bit operations so the final value is
    /// deterministic. Mode is NOT changed automatically; use "setrun"/"setprog"
    /// in the interactive CLI if needed.
    ///
    /// All operations are wrapped in <see cref="Execute{T}"/> so that a single
    /// failure does not abort the rest of the sequence, and every call is
    /// counted in the communication statistics.
    ///
    /// The demo is skipped when --interactive-only is passed on the command line.
    /// </summary>
    private static void RunDemo(Comm.PCCCComm pccc)
    {
        // ── Processor identification ──────────────────────────────────────────
        // GetProcessorType() sends CMD=0x06 FNC=0x03 (Get Diagnostic Status)
        // and extracts the processor type byte from the response payload.
        // Common values: 0x49 = SLC 5/03, 0x4B = SLC 5/04, 0x4C = SLC 5/05.
        Console.WriteLine("--- Processor Info ---");
        int proc = Execute(() => pccc.GetProcessorType(), "GetProcessorType");
        Console.WriteLine($"Processor Type : 0x{proc:X2}");

        // GetRunMode() inspects the mode byte in the GetStatus response.
        // Returns 1 for RUN, 0 for PROGRAM (and PROGRAM-equivalent test modes).
        int mode = Execute(() => pccc.GetRunMode(), "GetRunMode");
        Console.WriteLine(mode == 1 ? "Mode           : RUN" : "Mode           : PROGRAM");

        // ── Data file directory ───────────────────────────────────────────────
        // GetDataMemory() reads File 0 (the directory) and returns an array of
        // DataFileDetails records, one per configured data table.
        // The file type codes in the records match AB Publication 1770-6.5.16:
        //   0x8B = Output (O), 0x8C = Input (I), 0x84 = Status (S)
        //   0x85 = Binary (B), 0x89 = Integer (N), 0x8A = Float (F), 0x8D = String (ST)
        Console.WriteLine("\n--- Data Files ---");
        Comm.DataFileDetails[]? files = Execute(() => pccc.GetDataMemory(), "GetDataMemory");
        if (files != null)
            foreach (var f in files)
                Console.WriteLine($"  File {f.FileNumber,2}: Type=0x{f.FileType:X2}  Elements={f.NumberOfElements}");
        else
            Console.WriteLine("  (Failed to retrieve data files)");

        // ── Read operations ───────────────────────────────────────────────────
        // ReadAny(address) returns a string[] where element [0] is the value.
        // Supported address formats:
        //   N7:0    integer file 7, element 0
        //   F8:0    float file 8, element 0
        //   B3:0    binary (bit) file 3, word 0
        //   O0:0    output image, slot 0 word
        //   I1:0    input image, slot 0 word
        //   ST18:0  string file 18, element 0
        Console.WriteLine("\n--- Read Operations ---");
        string o0  = Execute(() => pccc.ReadAny("O0:0"),   "Read O0:0")   ?? "";
        string i1  = Execute(() => pccc.ReadAny("I1:0"),   "Read I1:0")   ?? "";
        string b3  = Execute(() => pccc.ReadAny("B3:0"),   "Read B3:0")   ?? "";
        string n7  = Execute(() => pccc.ReadAny("N7:0"),   "Read N7:0")   ?? "";
        string f8  = Execute(() => pccc.ReadAny("F8:0"),   "Read F8:0")   ?? "";
        string st0 = Execute(() => pccc.ReadAny("ST18:0"), "Read ST18:0") ?? "";
        Console.WriteLine($"  O0:0   = {o0}");
        Console.WriteLine($"  I1:0   = {i1}");
        Console.WriteLine($"  B3:0   = {b3}");
        Console.WriteLine($"  N7:0   = {n7}");
        Console.WriteLine($"  F8:0   = {f8}");
        Console.WriteLine($"  ST18:0 = \"{st0}\"");

        // ── Write operations ──────────────────────────────────────────────────
        // WriteData() overloads accept int, float, or string:
        //   WriteData("N7:1", 999)        — write integer 999 to N7:1
        //   WriteData("F8:1", 2.718f)     — write single-precision float to F8:1
        //   WriteData("B3:0/0", 1)        — set bit 0 of B3:0 (FNC=0xAB)
        //   WriteData("ST18:1", "text")   — write ASCII string to ST18:1
        Console.WriteLine("\n--- Write Operations ---");

        // Clear B3:0 first so that the bit-write results below are predictable.
        ExecuteVoid(() => pccc.WriteData("B3:0", 0), "Write B3:0 reset");

        Console.WriteLine("  Writing 999 to N7:1...");
        ExecuteVoid(() => pccc.WriteData("N7:1", 999), "Write N7:1");

        Console.WriteLine("  Writing 2.718 to F8:1...");
        ExecuteVoid(() => pccc.WriteData("F8:1", 2.718f), "Write F8:1");

        // Bit-level write uses the FILE:WORD/BIT address format.
        // Internally this issues CMD=0x0F FNC=0xAB (Read-Modify-Write) which
        // atomically sets or clears a single bit without disturbing adjacent bits.
        Console.WriteLine("  Setting B3:0/0 = 1...");
        ExecuteVoid(() => pccc.WriteData("B3:0/0", 1), "Write B3:0/0");

        Console.WriteLine("  Setting B3:0/3 = 1...");
        ExecuteVoid(() => pccc.WriteData("B3:0/3", 1), "Write B3:0/3");

        Console.WriteLine("  Writing string to ST18:1...");
        ExecuteVoid(() => pccc.WriteData("ST18:1", "HELLO PCCC"), "Write ST18:1");

        // ── Read-back verification ────────────────────────────────────────────
        Console.WriteLine("\n--- Read-Back After Write ---");
        string n7b  = Execute(() => pccc.ReadAny("N7:1"),   "Read N7:1")   ?? "";
        string f8b  = Execute(() => pccc.ReadAny("F8:1"),   "Read F8:1")   ?? "";
        string b3b  = Execute(() => pccc.ReadAny("B3:0"),   "Read B3:0")   ?? "";
        string st1b = Execute(() => pccc.ReadAny("ST18:1"), "Read ST18:1") ?? "";
        Console.WriteLine($"  N7:1   = {n7b}");
        Console.WriteLine($"  F8:1   = {f8b}");
        // Bits 0 and 3 set → binary 0000000000001001 = decimal 9
        Console.WriteLine($"  B3:0   = {b3b}  (bits 0 and 3 set → expected 9)");
        Console.WriteLine($"  ST18:1 = \"{st1b}\"");

        PrintStats();
    }

// =============================================================================
// SECTION 3 — Stress test: continuous read loop
// =============================================================================

    /// <summary>
    /// Runs a continuous read loop against F8:0.
    ///
    /// The stress test measures sustained throughput, exposes memory leaks, and
    /// reveals intermittent communication errors. Progress is printed every 100
    /// iterations; for infinite mode (loopCount == 0) press any key to stop.
    ///
    /// After the loop ends, communication statistics are printed so the aggregate
    /// error rate over the entire run is visible at a glance.
    /// </summary>
    /// <param name="pccc">Open PCCCComm instance.</param>
    /// <param name="loopCount">Iteration limit; 0 = infinite.</param>
    private static void RunStressTest(Comm.PCCCComm pccc, int loopCount)
    {
        Console.WriteLine("\n--- Stress Test Mode ---");
        Console.WriteLine(loopCount == 0
            ? "Reading F8:0 continuously. Press any key to stop."
            : $"Reading F8:0 for {loopCount} iterations.");

        int count = 0;
        while (!Console.KeyAvailable && (loopCount == 0 || count < loopCount))
        {
            try
            {
                string[] val = pccc.ReadAny("F8:0", 1) ?? Array.Empty<string>();
                RecordSuccess();
                if (++count % 100 == 0)
                {
                    string last = val.Length > 0 ? val[0] : "(null)";
                    Console.WriteLine($"  {count,6} reads — last value: {last}");
                }
            }
            catch (Comm.Pccc.PCCCException ex)
            {
                if      (ex.Message.Contains("NAK"))          RecordNak();
                else if (ex.Message.Contains("No Response") ||
                         ex.Message.Contains("Timeout"))      RecordTimeout();
                else                                           RecordOtherError();
                Console.WriteLine($"  Error at iteration {count + 1}: {ex.Message}");
            }
            catch (Exception ex)
            {
                RecordOtherError();
                Console.WriteLine($"  Unexpected error: {ex.Message}");
            }
            Thread.Sleep(50);
        }

        if (Console.KeyAvailable) Console.ReadKey(true);
        PrintStats();
    }

// =============================================================================
// SECTION 3b — Node scanner
// =============================================================================

    /// <summary>
    /// Probes a range of DF1 node addresses by sending GetProcessorType() to
    /// each node in turn and recording which ones respond.
    ///
    /// Background
    /// ----------
    /// In a DF1 half-duplex (RS-485) network, each SLC 500 or MicroLogix PLC
    /// is assigned a unique node address (1–31 by default). There is no
    /// broadcast mechanism to discover nodes, so the only reliable method is
    /// to probe each address individually.
    ///
    /// For peer-to-peer DF1 full-duplex links the target node may not always
    /// be 1; this scan quickly identifies the actual node address without
    /// needing RSLinx or a separate DF1 sniffer.
    ///
    /// How it works
    /// ------------
    /// The method temporarily reassigns <c>pccc.TargetNode</c> and
    /// <c>pccc.SlaveAddress</c> for each probe, then restores the original
    /// values when the scan completes. The timeout for each probe is reduced
    /// to 1000 ms to keep the scan fast on sparse networks.
    ///
    /// False-positive guard
    /// --------------------
    /// A GetDiagnosticStatus response from a real SLC 500 is at minimum 28 bytes
    /// (6 header + 22 payload). Shorter frames — caused by RS-485 echo, stale
    /// packets in the receive buffer, or bus noise — are rejected:
    ///
    ///   pkt[3]  must be 0x00 (STS = no error)
    ///   pkt[1]  (SRC) must equal the probed node number — the PLC sets SRC to
    ///           its own node address in every reply, so a mismatch means the
    ///           packet came from a different node or is an echo of our own frame.
    ///   pkt[9]  (processor type) must be non-zero — type 0x00 is not a valid
    ///           SLC 500 processor code and indicates a truncated or bogus frame.
    ///   pkt.Length must be >= 28 — a complete GetStatus reply body.
    ///
    /// EIP note
    /// --------
    /// EIP sessions are addressed by IP, not by node number. If the transport
    /// is EIP this method still runs but the node number only affects the DST
    /// byte inside the PCCC PDU. This is useful for PCCC-over-CIP bridging
    /// scenarios (e.g. through a 1756-DHRIO) where multiple DF1 nodes are
    /// reachable via one EIP path.
    ///
    /// Processor type codes (partial list)
    /// ------------------------------------
    ///   0x49 = SLC 5/03      0x4B = SLC 5/04      0x4C = SLC 5/05
    ///   0x89 = MicroLogix 1000 (series C)          0x9C = MicroLogix 1100
    ///   0xA0 = MicroLogix 1200                     0xA2 = MicroLogix 1400
    /// </summary>
    /// <param name="pccc">Open PCCCComm instance.</param>
    /// <param name="from">First node address to probe (inclusive).</param>
    /// <param name="to">Last node address to probe (inclusive).</param>
    private static void RunNodeScan(Comm.PCCCComm pccc, int from, int to)
    {
        // Clamp range to valid DF1 node address space (0–254; practical limit 31).
        from = Math.Max(0, Math.Min(254, from));
        to   = Math.Max(from, Math.Min(254, to));

        int savedTarget  = pccc.TargetNode;
        int savedSlave   = pccc.SlaveAddress;
        int savedTimeout = pccc.ResponseTimeoutMs;

        // Use a shorter per-probe timeout so the scan does not take excessively
        // long on sparse networks. 1000 ms is enough for a healthy serial link;
        // increase if the baud rate is very low or the cable is very long.
        const int probeTimeoutMs = 1000;

        // Minimum valid GetDiagnosticStatus response length.
        // Layout: [DST SRC CMD STS TNS_LO TNS_HI | payload(≥22 bytes)]
        // A real SLC 500 / MicroLogix returns at least 28 bytes total.
        // Frames shorter than this are echoes, noise, or truncated responses.
        const int minValidResponseLen = 28;

        pccc.ResponseTimeoutMs = probeTimeoutMs;

        Console.WriteLine($"\n--- Node Scan (nodes {from}–{to}, timeout {probeTimeoutMs} ms each) ---");

        var found = new List<(int node, int procType)>();

        for (int node = from; node <= to; node++)
        {
            pccc.TargetNode   = node;
            pccc.SlaveAddress = node;

            Console.Write($"  Node {node,3}: ");

            try
            {
                // GetDiagnosticStatusRaw() returns the raw response payload so we
                // can inspect the full frame for false-positive detection, without
                // triggering the ProcessorType side-effect on stale data.
                byte[]? raw = pccc.GetDiagnosticStatusRaw();

                if (raw == null)
                {
                    // STS != 0 or missing packet — node sent an error reply.
                    Console.WriteLine("error response");
                    continue;
                }

                // raw is the payload after the 6-byte header, so the full packet
                // length equivalent is raw.Length + 6.
                int fullLen = raw.Length + 6;
                if (fullLen < minValidResponseLen)
                {
                    // Frame is too short to be a genuine GetStatus reply.
                    // Most likely cause: RS-485 echo of our own transmitted frame
                    // or bus noise returning a few bytes that happen to pass CRC.
                    Console.WriteLine($"ignored (frame too short: {fullLen} < {minValidResponseLen} bytes)");
                    continue;
                }

                // raw[ResponseOffsets.DiagnosticStatus.TypeExtender] (offset 1) is the
                // "type extender" byte. For all SLC 500 and MicroLogix processors this
                // byte is 0xEE. Any other value means the response is not from an
                // SLC/MicroLogix, or is a garbled frame.
                byte typeExt = raw[Comm.Pccc.PCCCConstants.ResponseOffsets.DiagnosticStatus.TypeExtender];
                if (typeExt != Comm.Pccc.PCCCConstants.ResponseOffsets.DiagnosticStatus.TypeExtenderSlcMl)
                {
                    Console.WriteLine($"ignored (type extender=0x{typeExt:X2}, expected 0xEE — not SLC/MicroLogix or echo)");
                    continue;
                }

                // raw[ResponseOffsets.DiagnosticStatus.ProcessorType] (offset 3) is the
                // processor type byte. 0x00 is not a valid SLC 500 code; reject it as
                // a remnant of an echo or zeroed frame.
                int procType = raw[Comm.Pccc.PCCCConstants.ResponseOffsets.DiagnosticStatus.ProcessorType];
                if (procType == 0x00)
                {
                    Console.WriteLine($"ignored (processor type 0x00 — likely echo or noise)");
                    continue;
                }

                string name = SlcProcessorTypeName(procType);
                Console.WriteLine($"FOUND  type=0x{procType:X2}  ({name})");
                found.Add((node, procType));
            }
            catch (Comm.Pccc.PCCCException ex) when (
                ex.Message.Contains("No Response") ||
                ex.Message.Contains("Timeout")     ||
                ex.Message.Contains("NAK"))
            {
                // No response — node is absent or powered off. This is the
                // expected result for the majority of addresses on a sparse bus.
                Console.WriteLine("no response");
            }
            catch (Exception ex)
            {
                // Unexpected error — print but continue scanning remaining nodes.
                Console.WriteLine($"error: {ex.Message}");
            }
        }

        // ── Summary ──────────────────────────────────────────────────────────
        Console.WriteLine();
        if (found.Count == 0)
        {
            Console.WriteLine("  No nodes found in range.");
        }
        else
        {
            Console.WriteLine($"  {found.Count} node(s) found:");
            foreach (var (node, procType) in found)
                Console.WriteLine($"    Node {node,3}  type=0x{procType:X2}  ({SlcProcessorTypeName(procType)})");
        }

        // Restore original settings so subsequent demo, stress test, or
        // interactive CLI operations target the originally configured node.
        pccc.TargetNode        = savedTarget;
        pccc.SlaveAddress      = savedSlave;
        pccc.ResponseTimeoutMs = savedTimeout;
        Console.WriteLine($"\n  Target node restored to {savedTarget}.");
    }

    /// <summary>
    /// Handles the "settarget" interactive CLI command.
    ///
    /// Changes <c>pccc.TargetNode</c> and <c>pccc.SlaveAddress</c> at runtime,
    /// then immediately re-runs a GetProcessorType probe to confirm the new node
    /// is reachable. The connection does not need to be closed and reopened —
    /// DF1 serial and EIP transports both support changing the target node while
    /// the port or socket remains open.
    ///
    /// Typical workflow after a failed startup probe:
    ///   PCCC&gt; scannodes          ← discover which nodes are active
    ///   PCCC&gt; settarget 3        ← switch to the found node
    ///   PCCC&gt; read N7:0          ← now works
    ///
    /// Usage: settarget &lt;node&gt;
    /// </summary>
    private static void HandleSetTarget(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 2 || !int.TryParse(parts[1], out int node) || node < 0 || node > 254)
        {
            Console.WriteLine("Usage: settarget <node>");
            Console.WriteLine("  node is a decimal address in range 0–254.");
            Console.WriteLine("  Example: settarget 3");
            return;
        }

        int previous = pccc.TargetNode;
        pccc.TargetNode   = node;
        pccc.SlaveAddress = node;
        Console.Write($"Target node changed {previous} → {node}. Probing... ");

        try
        {
            int procType = pccc.GetProcessorType();
            Console.WriteLine($"OK  ({ProcessorTypeName(pccc)})");
        }
        catch (Comm.Pccc.PCCCException ex)
        {
            // Probe failed — report the error but keep the new target set.
            // The user may have a reason to target a node that is temporarily
            // offline, or may want to try scannodes before deciding.
            Console.WriteLine($"no response  ({ex.Message})");
            Console.WriteLine($"  Node {node} did not respond. Target is set but operations may fail.");
            Console.WriteLine($"  Run 'scannodes' to find active nodes, or 'settarget {previous}' to revert.");
        }
    }
    private static void HandleScanNodes(Comm.PCCCComm pccc, string[] parts)
    {
        int from = 1, to = 31;

        if (parts.Length >= 2 && !int.TryParse(parts[1], out from))
        {
            Console.WriteLine("Usage: scannodes [from] [to]");
            Console.WriteLine("  from and to are decimal node addresses (default: 1 31)");
            return;
        }
        if (parts.Length >= 3 && !int.TryParse(parts[2], out to))
        {
            Console.WriteLine("Usage: scannodes [from] [to]");
            return;
        }

        RunNodeScan(pccc, from, to);
    }

    /// <summary>
    /// Returns a human-readable processor name for a processor type byte.
    ///
    /// The processor type byte is found at payload offset
    /// <see cref="Comm.Pccc.PCCCConstants.ResponseOffsets.DiagnosticStatus.ProcessorType"/>
    /// (= 3) in the GetDiagnosticStatus response.
    ///
    /// Source: AB Publication 1770-6.5.16, Appendix B.
    /// This list covers the most common SLC 500 and MicroLogix variants;
    /// unknown codes are returned as "(unknown)".
    /// </summary>
    private static string SlcProcessorTypeName(int code) => code switch
    {
        0x25 => "SLC 5/01 (series A/B)",
        0x49 => "SLC 5/03",
        0x4A => "SLC 5/03 (OS302)",
        0x5B => "SLC 5/04",
        0x4C => "SLC 5/05",
        0x88 => "MicroLogix 1000",
        0x89 => "MicroLogix 1000 (series C)",
        0x9C => "MicroLogix 1100",
        0xA0 => "MicroLogix 1200",
        0xA2 => "MicroLogix 1400",
        0x31 => "SLC 5/02",
        0x3B => "SLC 500 (fixed)",
        _    => $"SLC/MicroLogix (expansion 0x{code:X2})"
    };

    private static string Plc5ProcessorTypeName(int expansionByte) => expansionByte switch
    {
        0x15 => "PLC-5/40B (1785-L40B)",
        0x22 => "PLC-5/10 (1785-LT4)",
        0x23 => "PLC-5/60B (1785-L60B)",
        0x28 => "PLC-5/40L (1785-L40L)",
        0x29 => "PLC-5/60L (1785-L60L)",
        0x31 => "PLC-5/11 (1785-L11B)",
        0x32 => "PLC-5/20 (1785-L20B)",
        0x33 => "PLC-5/30 (1785-L30B)",
        0x4A => "PLC-5/20E (1785-L20E)",
        0x4B => "PLC-5/40E (1785-L40E)",
        0x55 => "PLC-5/25 (1785-L80B)",
        0x59 => "PLC-5/80E (1785-L80E)",
        _    => $"PLC-5 (expansion 0x{expansionByte:X2})"
    };

    /// <summary>
    /// Returns a human-readable processor name based on diagnostic status.
    /// Automatically detects SLC/MicroLogix vs PLC-5 families.
    /// </summary>
    private static string ProcessorTypeName(Comm.PCCCComm pccc)
    {
        byte[]? diag = pccc.GetDiagnosticStatusRaw();
        if (diag == null || diag.Length < 4)
            return "unknown";

        var family = Comm.Pccc.PCCCConstants.DetectFamily(diag);
        if (family == Comm.Pccc.PCCCConstants.ProcessorFamily.SlcMicroLogix)
        {
            int procType = pccc.GetProcessorType();
            return SlcProcessorTypeName(procType);
        }
        else if (family == Comm.Pccc.PCCCConstants.ProcessorFamily.Plc5)
        {
            // PLC-5: expansion byte is at index 2 (byte 3 of document)
            int expansionByte = diag[2];
            return Plc5ProcessorTypeName(expansionByte);
        }
        else
        {
            return "unknown processor family";
        }
    }

// =============================================================================
// SECTION 3c — Watch: live address monitor
// =============================================================================

    /// <summary>
    /// Polls a single PLC address at a configurable interval and prints the
    /// value to the console whenever it changes.
    ///
    /// Usage (interactive CLI):
    ///   watch &lt;address&gt; [interval_ms]
    ///
    /// The address format is identical to <c>ReadAny</c>:
    ///   N7:0, F8:5, B3:0, ST18:0, O0:0, I1:0, etc.
    ///
    /// The interval defaults to 500 ms. The minimum enforced interval is 50 ms
    /// to avoid overwhelming a slow serial link.
    ///
    /// Press any key to stop the watch loop.
    ///
    /// Delta detection
    /// ---------------
    /// The value is printed only when it differs from the previous reading.
    /// This makes it easy to spot the moment a value changes without the
    /// terminal scrolling continuously. The first reading is always printed.
    ///
    /// Timestamp
    /// ---------
    /// Each printed line is prefixed with the elapsed time since the watch
    /// started (HH:MM:SS.mmm) so the rate and timing of changes are visible.
    ///
    /// Error handling
    /// --------------
    /// A single read error does not stop the watch loop — it prints the error
    /// and continues polling. This is intentional: intermittent communication
    /// errors (e.g. RS-485 collisions) should not abort a long-running monitor.
    /// Three consecutive errors do stop the loop to avoid flood-printing errors
    /// on a disconnected link.
    /// </summary>
    private static void HandleWatch(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: watch <address> [interval_ms]");
            Console.WriteLine("  Example: watch F8:0");
            Console.WriteLine("           watch N7:5 200");
            Console.WriteLine("  Press any key to stop.");
            return;
        }

        string addr       = parts[1];
        int    intervalMs = 500;
        const int minIntervalMs   = 50;
        const int maxConsecErrors = 3;

        if (parts.Length >= 3)
        {
            if (!int.TryParse(parts[2], out intervalMs) || intervalMs < minIntervalMs)
            {
                Console.WriteLine($"Invalid interval; must be an integer >= {minIntervalMs} ms.");
                return;
            }
        }

        Console.WriteLine($"Watching {addr} every {intervalMs} ms. Press any key to stop.");
        Console.WriteLine();

        string? lastValue   = null;   // last successfully read value (null = not yet read)
        int     consecErr   = 0;      // consecutive error counter
        long    changeCount = 0;      // number of value changes observed
        long    readCount   = 0;      // total successful reads
        var     startTime   = Stopwatch.StartNew();

        while (!Console.KeyAvailable)
        {
            try
            {
                string[]? result = pccc.ReadAny(addr, 1);
                string    value  = result?.Length > 0 ? result[0] : "(null)";
                readCount++;
                consecErr = 0; // reset on success

                // Print only when the value changes (or on the very first read).
                if (value != lastValue)
                {
                    changeCount++;
                    TimeSpan elapsed = startTime.Elapsed;
                    Console.WriteLine(
                        $"  [{elapsed:hh\\:mm\\:ss\\.fff}]  {addr} = {value}" +
                        (lastValue == null ? "  (initial)" : $"  (was: {lastValue})"));
                    lastValue = value;
                }
            }
            catch (Comm.Pccc.PCCCException ex)
            {
                consecErr++;
                TimeSpan elapsed = startTime.Elapsed;
                Console.WriteLine($"  [{elapsed:hh\\:mm\\:ss\\.fff}]  Error: {ex.Message}");

                if (consecErr >= maxConsecErrors)
                {
                    Console.WriteLine($"  {maxConsecErrors} consecutive errors — stopping watch.");
                    break;
                }
            }
            catch (Exception ex)
            {
                consecErr++;
                Console.WriteLine($"  Unexpected error: {ex.Message}");
                if (consecErr >= maxConsecErrors) break;
            }

            // Sleep in short increments so key-press is detected promptly.
            // Sleeping the full interval in one call would make the loop
            // unresponsive to keyboard input for up to intervalMs milliseconds.
            int    slept = 0;
            while (slept < intervalMs && !Console.KeyAvailable)
            {
                Thread.Sleep(Math.Min(50, intervalMs - slept));
                slept += 50;
            }
        }

        if (Console.KeyAvailable) Console.ReadKey(true);

        Console.WriteLine();
        Console.WriteLine($"  Watch stopped. {readCount} reads, {changeCount} change(s) in {startTime.Elapsed:hh\\:mm\\:ss}.");
    }

    /// <summary>
    /// Handles "wordread" interactive command.
    /// Usage: wordread <file> <element> <wordOffset> <sizeWords>
    /// Example: wordread N7 0 0 10
    /// Reads 10 words (20 bytes) from N7:0 starting at word offset 0.
    /// </summary>
    private static void HandleWordRead(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 5)
        {
            Console.WriteLine("Usage: wordread <fileType> <fileNumber> <element> <wordOffset> <sizeWords>");
            Console.WriteLine("  fileType : N, F, B, T, C, ST, etc. (case-insensitive)");
            Console.WriteLine("  fileNumber: decimal (e.g., 7 for N7)");
            Console.WriteLine("  element   : element number (e.g., 0)");
            Console.WriteLine("  wordOffset: word offset within element (e.g., 0)");
            Console.WriteLine("  sizeWords : number of 16-bit words to read");
            Console.WriteLine("Example: wordread N 7 0 0 10");
            return;
        }

        string fileTypeStr = parts[1].ToUpperInvariant();
        if (!int.TryParse(parts[2], out int fileNumber) ||
            !int.TryParse(parts[3], out int element) ||
            !int.TryParse(parts[4], out int wordOffset) ||
            !int.TryParse(parts[5], out int sizeWords))
        {
            Console.WriteLine("Invalid numeric parameters.");
            return;
        }

        // Map file type letter to PLC-5 file type code (per 1770-6.5.16 Table 13-1)
        int fileTypeCode = Plc5FileTypeCode(fileTypeStr);

        if (fileTypeCode == 0 && fileTypeStr != "0")
        {
            Console.WriteLine($"Unknown file type: {fileTypeStr}");
            return;
        }

        // Encode logical binary address for PLC-5
        byte[] logicalAddress = Comm.Handlers.Plc5Handler.EncodePlc5LogicalAddress(fileNumber, fileTypeCode, element, 0, false);
        try
        {
            byte[] data = pccc.WordRangeRead(logicalAddress, wordOffset, sizeWords);
            Console.WriteLine($"Read {data.Length} bytes:");
            WriteHex("  ", data, data.Length);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WordRangeRead failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Handles "wordwrite" interactive command.
    /// Usage: wordwrite <file> <element> <wordOffset> <dataHex...>
    /// Example: wordwrite N7 0 0 0010 0020 0030
    /// Writes 3 words (6 bytes) to N7:0 starting at word offset 0.
    /// </summary>
    private static void HandleWordWrite(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 5)
        {
            Console.WriteLine("Usage: wordwrite <fileType> <fileNumber> <element> <wordOffset> <dataHex...>");
            Console.WriteLine("  dataHex : hex values (2 bytes per word, low byte first)");
            Console.WriteLine("Example: wordwrite N 7 0 0 0010 0020 0030");
            return;
        }

        string fileTypeStr = parts[1].ToUpperInvariant();
        if (!int.TryParse(parts[2], out int fileNumber) ||
            !int.TryParse(parts[3], out int element) ||
            !int.TryParse(parts[4], out int wordOffset))
        {
            Console.WriteLine("Invalid numeric parameters.");
            return;
        }

        // Parse hex data bytes
        var dataBytes = new List<byte>();
        for (int i = 5; i < parts.Length; i++)
        {
            string hex = parts[i];
            // Allow either "0010" (2 bytes) or "10" (1 byte)
            if (hex.Length % 2 != 0)
            {
                Console.WriteLine($"Invalid hex data: '{hex}' (must have even number of characters)");
                return;
            }
            for (int j = 0; j < hex.Length; j += 2)
            {
                if (!byte.TryParse(hex.Substring(j, 2), System.Globalization.NumberStyles.HexNumber, null, out byte b))
                {
                    Console.WriteLine($"Invalid hex byte: '{hex.Substring(j, 2)}'");
                    return;
                }
                dataBytes.Add(b);
            }
        }

        if (dataBytes.Count % 2 != 0)
        {
            Console.WriteLine("Total data must be an even number of bytes (whole words).");
            return;
        }

        // Map file type
        int fileTypeCode = Plc5FileTypeCode(fileTypeStr);

        if (fileTypeCode == 0 && fileTypeStr != "0")
        {
            Console.WriteLine($"Unknown file type: {fileTypeStr}");
            return;
        }

        byte[] logicalAddress = Comm.Handlers.Plc5Handler.EncodePlc5LogicalAddress(fileNumber, fileTypeCode, element, 0, false);
        try
        {
            pccc.WordRangeWrite(logicalAddress, wordOffset, dataBytes.ToArray());
            Console.WriteLine($"Wrote {dataBytes.Count / 2} word(s) successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"WordRangeWrite failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Translates a file type letter to PLC-5 wire file type code (1770-6.5.16 Table 13-1)
    /// </summary>
    private static int Plc5FileTypeCode(string letter) => letter switch
    {
        "O"  => 0x00, "I"  => 0x01, "S"  => 0x02, "B"  => 0x03,
        "T"  => 0x04, "C"  => 0x05, "R"  => 0x06, "N"  => 0x07,
        "F"  => 0x08, "D"  => 0x09, "ST" => 0x0A, "A"  => 0x0B,
        "L"  => 0x0C, "MG" => 0x0D, "PD" => 0x0E, "PLS"=> 0x0F,
        _    => 0x00
    };

// =============================================================================
// SECTION 4 — Interactive CLI
// =============================================================================

    /// <summary>
    /// Runs the interactive command-line interface.
    ///
    /// The CLI is the primary tool for manual exploration of a PLC or emulator.
    /// Commands are dispatched from the switch statement below. The "selftest"
    /// command runs the exhaustive test suite defined in Section 5.
    ///
    /// Note: interactive CLI commands are intentionally NOT tracked in the
    /// global communication statistics so that manual exploration does not
    /// distort the automated test numbers from the demo or stress test.
    /// </summary>
    private static void RunInteractiveCli(Comm.PCCCComm pccc)
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
                switch (cmd)
                {
                    // ── Navigation ──────────────────────────────────────────────
                    case "exit":
                    case "quit":
                        return;

                    case "help":
                        PrintInteractiveHelp();
                        break;

                    // ── Statistics ───────────────────────────────────────────────
                    case "stats":
                        PrintStats();
                        break;

                    case "resetstats":
                        ResetStats();
                        break;

                    // ── Data access ──────────────────────────────────────────────

                    // read <address> [count]
                    // Reads one or more consecutive elements starting at address.
                    // Count defaults to 1. Example: read N7:0 10
                    case "read":
                        HandleRead(pccc, parts);
                        break;

                    // write <address> <value> [value2 ...]
                    // Writes one or more integer values starting at address.
                    // Example: write N7:0 100 200 300
                    case "write":
                        HandleWrite(pccc, parts);
                        break;

                    // writestring <address> <text...>
                    // Writes a string to an ST file address.
                    // Text may contain spaces; everything after the address is joined.
                    // Example: writestring ST18:0 Hello World
                    case "writestring":
                        HandleWriteString(pccc, parts);
                        break;

                    // sendhex <DST> <CMD> <FNC> [data bytes...]
                    // Sends a raw PCCC PDU. All values are hexadecimal.
                    // SRC, STS, and TNS are filled in automatically by the library.
                    // Example: sendhex 01 0F A1 02 07 89 00
                    case "sendhex":
                        HandleSendHex(pccc, parts);
                        break;

                    // ── Processor mode ───────────────────────────────────────────
                    case "mode":
                        int cur = pccc.GetRunMode();
                        Console.WriteLine(cur == 1 ? "RUN mode" : "PROGRAM mode");
                        break;

                    case "setrun":
                        pccc.SetRunMode();
                        Console.WriteLine("Switched to RUN mode");
                        break;

                    case "setprog":
                        pccc.SetProgramMode();
                        Console.WriteLine("Switched to PROGRAM mode");
                        break;

                    case "type":
                        int pt = pccc.GetProcessorType();
                        Console.WriteLine($"Processor Type: 0x{pt:X2}");
                        break;

                    // ── Self-test suite ──────────────────────────────────────────

                    // selftest
                    // Runs the exhaustive PCCCComm self-test suite and prints a
                    // PASS/FAIL verdict for every individual test case.
                    // Safe to run against the PCCCEmulator; see the caution note in
                    // RunSelfTest() before running against a real PLC.
                    case "selftest":
                        RunSelfTest(pccc);
                        break;

                    // ── Node management ──────────────────────────────────────────

                    // settarget <node>
                    // Changes the target node address at runtime without restarting.
                    // Useful after scannodes reveals the correct node, or when switching
                    // between multiple PLCs on the same RS-485 bus.
                    // Also re-runs the connectivity probe so the result is confirmed.
                    // Example: settarget 3
                    case "settarget":
                        HandleSetTarget(pccc, parts);
                        break;

                    // scannodes [from] [to]
                    // Probes each DF1 node address in the given range (default 1–31)
                    // by sending GetProcessorType. Reports which nodes respond and
                    // their processor type codes. Useful for RS-485 bus commissioning
                    // or finding which node a PLC has been assigned.
                    // Example: scannodes        (scans nodes 1–31)
                    //          scannodes 1 8    (scans nodes 1–8 only)
                    case "scannodes":
                        HandleScanNodes(pccc, parts);
                        break;

                    // watch <address> [interval_ms]
                    // Polls the given address repeatedly and prints the value
                    // whenever it changes. Default interval is 500 ms.
                    // Press any key to stop.
                    // Example: watch F8:0
                    //          watch N7:5 200
                    case "watch":
                        HandleWatch(pccc, parts);
                        break;

                    case "wordread":
                        HandleWordRead(pccc, parts);
                        break;

                    case "wordwrite":
                        HandleWordWrite(pccc, parts);
                        break;

                    default:
                        Console.WriteLine($"Unknown command '{cmd}'. Type 'help' for list.");
                        break;
                }
            }
            catch (Comm.Pccc.PCCCException ex)
            {
                Console.WriteLine($"PCCC Error: {ex.Message}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }
        }
    }

    // ── CLI command handlers ─────────────────────────────────────────────────

    /// <summary>
    /// Handles the "read" interactive command.
    /// Usage: read &lt;address&gt; [count]
    /// </summary>
    private static void HandleRead(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 2)
        {
            Console.WriteLine("Usage: read <address> [count]");
            Console.WriteLine("  Example: read N7:0 10");
            return;
        }
        string addr = parts[1];
        int    cnt  = 1;
        if (parts.Length >= 3 && !int.TryParse(parts[2], out cnt))
        {
            Console.WriteLine("Invalid count; must be an integer.");
            return;
        }
        string[] result = pccc.ReadAny(addr, cnt) ?? Array.Empty<string>();
        Console.WriteLine($"Result: {string.Join(", ", result)}");
    }

    /// <summary>
    /// Handles the "write" interactive command.
    /// Usage: write &lt;address&gt; &lt;value&gt; [value2 ...]
    /// </summary>
    private static void HandleWrite(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 3)
        {
            Console.WriteLine("Usage: write <address> <value> [value2 ...]");
            Console.WriteLine("  Example: write N7:0 100 200 300");
            return;
        }
        string addr   = parts[1];
        var    values = new List<int>();

        for (int i = 2; i < parts.Length; i++)
        {
            if (!int.TryParse(parts[i], out int v))
            {
                Console.WriteLine($"Invalid integer value: '{parts[i]}'");
                return;
            }
            values.Add(v);
        }
        pccc.WriteData(addr, values.Count, values.ToArray());
        Console.WriteLine("Write successful.");
    }

    /// <summary>
    /// Handles the "writestring" interactive command.
    /// Usage: writestring &lt;address&gt; &lt;text...&gt;
    /// </summary>
    private static void HandleWriteString(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 3)
        {
            Console.WriteLine("Usage: writestring <address> <text>");
            Console.WriteLine("  Example: writestring ST18:0 Hello World");
            return;
        }
        string addr = parts[1];
        string text = string.Join(" ", parts, 2, parts.Length - 2);
        pccc.WriteData(addr, text);
        Console.WriteLine("String write successful.");
    }

    /// <summary>
    /// Handles the "sendhex" interactive command.
    ///
    /// Sends a raw PCCC PDU by calling the internal PrefixAndSend method via
    /// reflection (see <see cref="SendRawPduAndGetResponse"/>). This bypasses all address
    /// parsing in the library and is useful for testing undocumented commands
    /// or validating emulator behaviour at the raw protocol level.
    ///
    /// Usage: sendhex &lt;DST&gt; &lt;CMD&gt; &lt;FNC&gt; [data bytes...]
    /// All values are hexadecimal. SRC, STS, and TNS are auto-generated.
    ///
    /// Example — read 2 bytes from N7:0 (file 7, type 0x89, element 0):
    ///   PCCC&gt; sendhex 01 0F A1 02 07 89 00
    /// </summary>
    private static void HandleSendHex(Comm.PCCCComm pccc, string[] parts)
    {
        if (parts.Length < 4)
        {
            Console.WriteLine("Usage: sendhex <DST> <CMD> <FNC> [data...]");
            Console.WriteLine("  DST, CMD, FNC and data bytes are hexadecimal.");
            Console.WriteLine("  SRC, STS and TNS are filled in by the library.");
            Console.WriteLine("  Example: sendhex 01 0F A1 02 07 89 00");
            return;
        }

        if (!byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out byte dst)    ||
            !byte.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out byte cmdByte)||
            !byte.TryParse(parts[3], System.Globalization.NumberStyles.HexNumber, null, out byte fnc))
        {
            Console.WriteLine("Invalid hex values for DST, CMD, or FNC.");
            return;
        }

        var  dataBytes   = new List<byte>();
        bool parseErr    = false;
        for (int i = 4; i < parts.Length; i++)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out byte b))
            {
                Console.WriteLine($"Invalid hex data byte: '{parts[i]}'");
                parseErr = true;
                break;
            }
            dataBytes.Add(b);
        }
        if (parseErr) return;

        // Build a minimal PDU: [DST, SRC=0, CMD, STS=0, TNS_LO=0, TNS_HI=0, FNC, data...]
        // The library replaces the TNS bytes with its own auto-generated value.
        byte[] pdu = new byte[7 + dataBytes.Count];
        pdu[0] = dst;
        pdu[1] = 0x00;    // SRC — overridden by the library with MyNode
        pdu[2] = cmdByte;
        pdu[3] = 0x00;    // STS — must be 0 in requests
        pdu[4] = 0x00;    // TNS low  — overridden by the library
        pdu[5] = 0x00;    // TNS high — overridden by the library
        pdu[6] = fnc;
        for (int i = 0; i < dataBytes.Count; i++)
            pdu[7 + i] = dataBytes[i];

        WriteHex("      TX:", pdu, pdu.Length);
        var (_, resp, _) = pccc.SendRawPduAndGetResponse(pdu);
        if (resp != null) WriteHex("      RX:", resp, resp.Length);
    }

// =============================================================================
// SECTION 5 — Self-test suite
// =============================================================================
//
// Purpose
// -------
// The self-test suite exercises every major feature of the PCCCComm library and
// reports a PASS/FAIL verdict for each individual test case. It is designed to
// be run against the PCCCEmulator after any change to either the library or the
// emulator to catch regressions quickly.
//
// Invocation
// ----------
//   PCCC> selftest
//
// Test groups
// -----------
//   1.  ProcessorInfo         — GetProcessorType(), GetRunMode()
//   2.  DirectoryEnumeration  — GetDataMemory(), file list completeness
//   3.  IntegerReadWrite      — N7 round-trips (positive, zero, negative, boundaries)
//   4.  FloatReadWrite        — F8 round-trips (pi, zero, negative, large, near-min)
//   5.  BitReadWrite          — B3 bit set/clear via FNC=0xAB (Read-Modify-Write)
//   6.  MultiElementRead      — ReadAny(addr, count) burst read
//   7.  MultiElementWrite     — WriteData(addr, count, array) burst write
//   8.  StringReadWrite       — ST18 round-trips (short, empty, mixed, max length)
//   9.  BoundaryConditions    — out-of-range and non-existent file error handling
//   10. ProcessorMode         — SetRunMode() / SetProgramMode() round-trip
//   11. Latency               — per-request RTT measurement (min/avg/max)
//
// Caution — real PLC hazard
// -------------------------
// The self-test WRITES to N7:2-N7:9, F8:2-F8:7, B3:1-B3:2, ST18:2-ST18:5.
// Do not run against a real PLC unless you are certain those addresses are safe
// to modify.
//
// =============================================================================

    // ── Test result tracking ─────────────────────────────────────────────────

    private static int _testPass = 0;
    private static int _testFail = 0;

    /// <summary>
    /// Records and prints a single test result.
    /// </summary>
    /// <param name="description">Short human-readable description of the test.</param>
    /// <param name="pass">True if the test passed.</param>
    /// <param name="detail">Optional supplementary information (actual value, error, etc.).</param>
    private static void TestResult(string description, bool pass, string detail = "")
    {
        if (pass)
        {
            _testPass++;
            Console.WriteLine($"  [PASS] {description}");
        }
        else
        {
            _testFail++;
            string suffix = string.IsNullOrEmpty(detail) ? "" : $"  ({detail})";
            Console.WriteLine($"  [FAIL] {description}{suffix}");
        }
    }

    // ── Self-test entry point ────────────────────────────────────────────────

    /// <summary>
    /// Runs the full PCCCComm self-test suite and prints a summary.
    ///
    /// Each test group is independent: a failure in one group does not prevent
    /// subsequent groups from running. The final summary shows the total pass
    /// and fail counts and the elapsed time.
    /// </summary>
    private static void RunSelfTest(Comm.PCCCComm pccc)
    {
        _testPass = 0;
        _testFail = 0;

        Console.WriteLine("\n╔══════════════════════════════════════════════╗");
        Console.WriteLine("║         PCCCComm Self-Test Suite             ║");
        Console.WriteLine("╚══════════════════════════════════════════════╝");
        Console.WriteLine("  Caution: writes to N7:2-9, F8:2-7, B3:1-2,");
        Console.WriteLine("  ST18:2-5. Do not use on a live PLC.");
        Console.WriteLine();

        var sw = Stopwatch.StartNew();

        SelfTest_ProcessorInfo(pccc);
        SelfTest_DirectoryEnumeration(pccc);
        SelfTest_IntegerReadWrite(pccc);
        SelfTest_FloatReadWrite(pccc);
        SelfTest_BitReadWrite(pccc);
        SelfTest_MultiElementRead(pccc);
        SelfTest_MultiElementWrite(pccc);
        SelfTest_StringReadWrite(pccc);
        SelfTest_BoundaryConditions(pccc);
        SelfTest_ProcessorMode(pccc);
        SelfTest_InitializeMemory(pccc);
        SelfTest_LinkParameters(pccc);
        SelfTest_ReadModifyWrite(pccc);
        SelfTest_Latency(pccc);

        sw.Stop();

        // ── Summary ──────────────────────────────────────────────────────────
        int total   = _testPass + _testFail;
        string verdict = _testFail == 0 ? "ALL PASS" : $"{_testFail} FAILED";
        Console.WriteLine();
        Console.WriteLine("╔══════════════════════════════════════════════╗");
        Console.WriteLine($"║  {_testPass}/{total} passed  —  {verdict,-24}   ║");
        Console.WriteLine($"║  Elapsed: {sw.ElapsedMilliseconds} ms{new string(' ',
            Math.Max(0, 32 - sw.ElapsedMilliseconds.ToString().Length))}║");
        Console.WriteLine("╚══════════════════════════════════════════════╝");
    }

    // ── Test helper ─────────────────────────────────────────────────────────

    /// <summary>
    /// Executes a test action and returns its result.
    /// On exception, stores the message in <paramref name="error"/> and returns
    /// the type default so callers can test for null/0 without nesting try/catch.
    /// </summary>
    private static T? TryTest<T>(Func<T> action, out string error)
    {
        error = "";
        try   { return action(); }
        catch (Exception ex) { error = ex.Message; return default; }
    }

    // ── Test group 1: Processor identification ───────────────────────────────

    /// <summary>
    /// Verifies that GetProcessorType() returns a non-zero value and that
    /// GetRunMode() returns a valid mode code.
    ///
    /// These two calls are sent first in every real application startup
    /// sequence. If they fail, no other operations are likely to succeed.
    ///
    /// Expected responses:
    ///   GetProcessorType : returns the single-byte processor type code,
    ///                      e.g. 0x49 for SLC 5/03.
    ///   GetRunMode       : returns 1 (RUN) or 0 (PROGRAM / test modes).
    /// </summary>
    private static void SelfTest_ProcessorInfo(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Processor Info ───────────────────────────────");

        int procType = TryTest(() => pccc.GetProcessorType(), out string err1);
        TestResult("GetProcessorType() returns non-zero",
                   procType != 0,
                   procType != 0 ? $"0x{procType:X2}" : err1);

        int runMode  = TryTest(() => pccc.GetRunMode(), out string err2);
        bool valid   = runMode == 0 || runMode == 1;
        TestResult("GetRunMode() returns 0 or 1",
                   valid,
                   valid ? (runMode == 1 ? "RUN" : "PROGRAM") : err2);
    }

    // ── Test group 2: Directory enumeration ──────────────────────────────────

    /// <summary>
    /// Verifies that GetDataMemory() returns a non-null, non-empty array and
    /// that the mandatory file set (O0, I1, S2, B3, N7, F8, ST18) is present.
    ///
    /// GetDataMemory() reads File 0 (the directory file). A failure here
    /// indicates a problem with the directory structure in the emulator or PLC.
    ///
    /// File type codes (per AB Publication 1770-6.5.16):
    ///   0x8B = Output, 0x8C = Input, 0x84 = Status, 0x85 = Binary,
    ///   0x89 = Integer, 0x8A = Float, 0x8D = String (ST)
    /// </summary>
    private static void SelfTest_DirectoryEnumeration(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Directory Enumeration ────────────────────────");

        var files = TryTest(() => pccc.GetDataMemory(), out string err);
        TestResult("GetDataMemory() returns non-null array", files != null, err);
        if (files == null) return;

        TestResult("Directory contains at least 6 data files",
                   files.Length >= 6, $"got {files.Length}");

        // Verify that each mandatory file is present by file number.
        (int num, string name)[] required =
        {
            (0,  "O0"),
            (1,  "I1"),
            (2,  "S2"),
            (3,  "B3"),
            (7,  "N7"),
            (8,  "F8"),
            (18, "ST18"),
        };
        foreach (var (num, name) in required)
            TestResult($"Directory contains {name} (file {num})",
                       files.Any(f => f.FileNumber == num));
    }

    // ── Test group 3: Integer read/write ─────────────────────────────────────

    /// <summary>
    /// Verifies integer read/write round-trips across the full range of values
    /// that a 16-bit signed integer can represent.
    ///
    /// WriteData(addr, int) issues CMD=0x0F FNC=0xAA (Protected Typed Logical
    /// Write) with 2 bytes of data for integer files.
    ///
    /// ReadAny(addr) issues CMD=0x0F FNC=0xA1 and returns the raw 16-bit value
    /// as a signed decimal string.
    ///
    /// Test cases:
    ///   positive  — typical operational value
    ///   zero      — explicit zero write (distinguishes no-write from write-zero)
    ///   negative  — two's complement representation
    ///   int16 max — 32767  (0x7FFF)
    ///   int16 min — -32768 (0x8000)
    /// </summary>
    private static void SelfTest_IntegerReadWrite(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Integer Read/Write (N7) ──────────────────────");

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
            TryTest(() => { pccc.WriteData(addr, value); return true; }, out _);
            string? raw = TryTest(() => pccc.ReadAny(addr), out string readErr);
            bool ok = int.TryParse(raw, out int readback) && readback == value;
            TestResult($"N7 round-trip {label} ({value})",
                       ok, ok ? $"= {readback}" : $"wrote {value}, got '{raw ?? readErr}'");
        }
    }

    // ── Test group 4: Float read/write ───────────────────────────────────────

    /// <summary>
    /// Verifies floating-point read/write round-trips across the F8 file.
    ///
    /// WriteData(addr, float) encodes the value as IEEE 754 single-precision
    /// (4 bytes, little-endian) and sends it via FNC=0xAA.
    ///
    /// ReadAny(addr) returns the value as a formatted decimal string. A
    /// tolerance of 1e-4 is used for comparison to absorb rounding from the
    /// float32 → string → float64 conversion in the display path.
    ///
    /// Test cases:
    ///   pi           — irrational, exercises rounding
    ///   zero         — exact IEEE zero
    ///   negative     — sign bit set
    ///   large        — exponent near the top of the float32 range
    ///   near float min — denormalized / smallest normal float32
    ///   negative zero — IEEE -0.0 (should compare equal to +0.0)
    /// </summary>
    private static void SelfTest_FloatReadWrite(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Float Read/Write (F8) ────────────────────────");

        const double tol = 1e-4;

        (string addr, float value, string label)[] cases =
        {
            ("F8:2",  3.14159f,       "pi"),
            ("F8:3",  0.0f,           "zero"),
            ("F8:4",  -273.15f,       "negative"),
            ("F8:5",  1e6f,           "large positive"),
            ("F8:6",  1.175494e-38f,  "near float min"),
            ("F8:7",  -0.0f,          "negative zero"),
        };

        foreach (var (addr, value, label) in cases)
        {
            TryTest(() => { pccc.WriteData(addr, value); return true; }, out _);
            string? raw = TryTest(() => pccc.ReadAny(addr), out string readErr);
            bool parsed = double.TryParse(raw,
                              System.Globalization.NumberStyles.Float,
                              System.Globalization.CultureInfo.InvariantCulture,
                              out double readback);
            bool ok = parsed && Math.Abs(readback - value) <= tol;
            TestResult($"F8 round-trip {label} ({value:G6})",
                       ok, ok ? $"= {readback:G6}" : $"wrote {value:G6}, got '{raw ?? readErr}'");
        }
    }

    // ── Test group 5: Bit read/write ─────────────────────────────────────────

    /// <summary>
    /// Verifies bit-level read/write via the B3 binary file.
    ///
    /// WriteData("B3:x/n", 1 or 0) issues CMD=0x0F FNC=0xAB (Read-Modify-Write).
    /// The emulator (and real SLC 500) perform the RMW atomically under a write
    /// lock so concurrent clients cannot race on the same word.
    ///
    /// The FNC=0xAB payload contains both a mask word and a value word:
    ///   mask  — bits to change (1 = change, 0 = preserve)
    ///   value — desired bit states (only bits covered by the mask are applied)
    ///
    /// Tests:
    ///   bit set pattern   — set bits 0, 4, 15; verify word = 0x8011
    ///   bit clear         — clear bit 4; verify word = 0x8001
    ///   all bits set      — set all 16 bits; verify word = 0xFFFF (−1 signed)
    /// </summary>
    private static void SelfTest_BitReadWrite(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Bit Read/Write (B3, FNC=0xAB) ───────────────");

        // Clear B3:1 and B3:2 to establish a known starting state.
        TryTest(() => { pccc.WriteData("B3:1", 0); return true; }, out _);
        TryTest(() => { pccc.WriteData("B3:2", 0); return true; }, out _);

        // Set bits 0, 4, 15 of B3:1.
        TryTest(() => { pccc.WriteData("B3:1/0",  1); return true; }, out _);
        TryTest(() => { pccc.WriteData("B3:1/4",  1); return true; }, out _);
        TryTest(() => { pccc.WriteData("B3:1/15", 1); return true; }, out _);

        string? rawSet   = TryTest(() => pccc.ReadAny("B3:1"), out _);
        int     expSet   = (1 << 0) | (1 << 4) | (1 << 15); // 0x8011
        int     expSetSigned = (short)expSet;               // -32751
        bool    okSet    = int.TryParse(rawSet, out int setVal) && setVal == expSetSigned;
        TestResult($"Bit set: bits 0,4,15 → word=0x{expSet:X4} ({expSet})",
                   okSet, okSet ? $"= 0x{setVal:X4}" : $"got '{rawSet}'");

        // Clear bit 4; expect bits 0 and 15 only.
        TryTest(() => { pccc.WriteData("B3:1/4", 0); return true; }, out _);
        string? rawClr  = TryTest(() => pccc.ReadAny("B3:1"), out _);
        int     expClr  = (1 << 0) | (1 << 15); // 0x8001
        int     expClrSigned = (short)expClr;    // -32767
        bool    okClr   = int.TryParse(rawClr, out int clrVal) && clrVal == expClrSigned;
        TestResult($"Bit clear: clear bit 4 → word=0x{expClr:X4} ({expClr})",
                   okClr, okClr ? $"= 0x{clrVal:X4}" : $"got '{rawClr}'");

        // Set all 16 bits of B3:2, verify word = 0xFFFF (−1 as signed int16).
        for (int bit = 0; bit < 16; bit++)
            TryTest(() => { pccc.WriteData($"B3:2/{bit}", 1); return true; }, out _);
        string? rawAll  = TryTest(() => pccc.ReadAny("B3:2"), out _);
        bool    okAll   = int.TryParse(rawAll, out int allVal) && (allVal == -1 || allVal == 65535);
        TestResult("All 16 bits set → word=0xFFFF (−1 signed)",
                   okAll, okAll ? $"= {allVal}" : $"got '{rawAll}'");
    }

    // ── Test group 6: Multi-element read ─────────────────────────────────────

    /// <summary>
    /// Verifies that ReadAny(address, count) correctly returns multiple
    /// consecutive elements in a single request.
    ///
    /// The library multiplies count by the element size to compute the
    /// "bytes to read" field in the FNC=0xA1 request. Both N7 (2 bytes/elem)
    /// and F8 (4 bytes/elem) are tested to verify the element size calculation.
    ///
    /// A failure here typically indicates a response parser bug where element
    /// boundaries are computed incorrectly.
    /// </summary>
    private static void SelfTest_MultiElementRead(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Multi-Element Read ───────────────────────────");

        // Seed N7:2..N7:6 with known values before reading them as a burst.
        int[] seed = { 1234, 0, -5678, 32767, -32768 };
        for (int i = 0; i < seed.Length; i++)
            TryTest(() => { pccc.WriteData($"N7:{2 + i}", seed[i]); return true; }, out _);

        // Read back 5 elements in a single call and verify each one.
        string[]? result = TryTest(() => pccc.ReadAny("N7:2", 5), out string err);
        TestResult("ReadAny(N7:2, 5) returns 5 elements",
                   result?.Length == 5, result == null ? err : $"got {result.Length}");

        if (result?.Length == 5)
        {
            for (int i = 0; i < seed.Length; i++)
            {
                bool ok = int.TryParse(result[i], out int v) && v == seed[i];
                TestResult($"  Multi-read N7 element [{i}] = {seed[i]}",
                           ok, ok ? "" : $"got '{result[i]}'");
            }
        }

        // Also test multi-read on F8 (4 bytes per element).
        float[] fseed = { 1.1f, 2.2f, 3.3f };
        for (int i = 0; i < fseed.Length; i++)
            TryTest(() => { pccc.WriteData($"F8:{2 + i}", fseed[i]); return true; }, out _);

        string[]? fr = TryTest(() => pccc.ReadAny("F8:2", 3), out string ferr);
        TestResult("ReadAny(F8:2, 3) returns 3 float elements",
                   fr?.Length == 3, fr == null ? ferr : $"got {fr.Length}");
    }

    // ── Test group 7: Multi-element write ────────────────────────────────────

    /// <summary>
    /// Verifies that WriteData(address, count, array) writes multiple
    /// consecutive elements in a single request.
    ///
    /// The library packs all values into one FNC=0xAA frame. Sending all
    /// values together is significantly more efficient than individual writes
    /// and is the correct approach for initialising blocks of registers.
    ///
    /// Each element is then read back individually to confirm the correct
    /// value was stored at the correct offset.
    /// </summary>
    private static void SelfTest_MultiElementWrite(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Multi-Element Write ──────────────────────────");

        int[] toWrite = { 11, 22, 33, 44, 55 };

        // Write all five values in a single call starting at N7:2.
        TryTest(() => { pccc.WriteData("N7:2", toWrite.Length, toWrite); return true; }, out _);

        // Read each element back individually and compare.
        for (int i = 0; i < toWrite.Length; i++)
        {
            string? raw = TryTest(() => pccc.ReadAny($"N7:{2 + i}"), out _);
            bool ok = int.TryParse(raw, out int v) && v == toWrite[i];
            TestResult($"  Multi-write N7 element [{i}] = {toWrite[i]}",
                       ok, ok ? "" : $"got '{raw}'");
        }
    }

    // ── Test group 8: String read/write ──────────────────────────────────────

    /// <summary>
    /// Verifies ST file read/write round-trips.
    ///
    /// SLC 500 string format (AB Publication 1770-6.5.16, Chapter 7):
    ///   Bytes 0-1  : length word, little-endian, value 0-82
    ///   Bytes 2-83 : character data, one ASCII byte per byte, unused = 0x00
    ///
    /// Each ST element is 84 bytes (1 length word + 82 characters).
    ///
    /// PCCCComm write path:
    ///   WriteData(addr, string) encodes the string into the 84-byte element
    ///   layout and issues FNC=0xAA with 84 bytes of data.
    ///
    /// PCCCComm read path:
    ///   ReadAny(addr) for an ST file reads 84 bytes and reconstructs the
    ///   string from the length word, trimming trailing null bytes.
    ///
    /// Test cases:
    ///   short ASCII   — basic sanity check
    ///   empty string  — length word = 0, all char bytes = 0
    ///   mixed chars   — spaces, digits, special characters
    ///   max length    — exactly 82 characters (truncation boundary)
    ///   seed value    — emulator initialises ST18:0 with "EMULATOR OK" at startup
    /// </summary>
    private static void SelfTest_StringReadWrite(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── String Read/Write (ST18) ─────────────────────");

        (string addr, string value, string label)[] cases =
        {
            ("ST18:2", "Hello",                       "short ASCII"),
            ("ST18:3", "",                            "empty string"),
            ("ST18:4", "PCCCComm v1.0 - test OK!",   "mixed chars"),
            ("ST18:5", new string('A', 82),           "max length (82 chars)"),
        };

        foreach (var (addr, value, label) in cases)
        {
            TryTest(() => { pccc.WriteData(addr, value); return true; }, out _);
            string? raw = TryTest(() => pccc.ReadAny(addr), out string readErr);
            bool ok = raw == value;
            TestResult($"ST round-trip: {label}",
                       ok, ok ? $"len={value.Length}" : $"expected '{value}', got '{raw ?? readErr}'");
        }

        // Verify the emulator seed value written by PlcMemory.BuildDataFiles().
        // If this fails it means the PlcMemory.WriteStString() initialiser is broken.
        string? seed = TryTest(() => pccc.ReadAny("ST18:0"), out _);
        TestResult("ST18:0 contains emulator seed \"EMULATOR OK\"",
                   seed == "EMULATOR OK", $"got '{seed}'");
    }

    // ── Test group 9: Boundary conditions ────────────────────────────────────

    /// <summary>
    /// Verifies that the library and emulator handle edge cases correctly.
    ///
    /// Tests cover:
    ///   Readable at element 0 — every standard file should respond to a read
    ///     of element 0 without error. A failure here means the file was not
    ///     registered in the emulator directory.
    ///
    ///   Out-of-range element — reading N7:200 (file has only 74 elements)
    ///     must throw a PCCCException. The emulator returns STS=0x10 (illegal
    ///     address) and the library wraps it in a PCCCException.
    ///
    ///   Non-existent file — reading from file 100 (never registered) must
    ///     throw a PCCCException. The emulator returns STS=0x50 (bad address).
    /// </summary>
    private static void SelfTest_BoundaryConditions(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Boundary Conditions ──────────────────────────");

        // Files that should be readable at element 0.
        (string addr, string name)[] readable =
        {
            ("O0:0",   "O0:0   output image"),
            ("I1:0",   "I1:0   input image"),
            ("S2:0",   "S2:0   status"),
            ("B3:0",   "B3:0   binary"),
            ("N7:0",   "N7:0   integer"),
            ("F8:0",   "F8:0   float"),
            ("ST18:0", "ST18:0 string"),
        };

        foreach (var (addr, name) in readable)
        {
            string? val = TryTest(() => pccc.ReadAny(addr), out string err);
            TestResult($"Read {name}", val != null, err);
        }

        // N7 has 74 elements (N7:0 to N7:73); N7:200 must fail.
        bool outOfRange = false;
        try { pccc.ReadAny("N7:200"); }
        catch { outOfRange = true; }
        TestResult("Read N7:200 (out of range) throws exception", outOfRange);

        // File 100 does not exist; the read must fail.
        bool notFound = false;
        try { pccc.ReadAny("N100:0"); }
        catch { notFound = true; }
        TestResult("Read N100:0 (non-existent file) throws exception", notFound);
    }

    // ── Test group 10: Processor mode switching ───────────────────────────────

    /// <summary>
    /// Verifies that SetRunMode() and SetProgramMode() change the processor
    /// mode as reported by GetRunMode().
    ///
    /// Mode switching uses CMD=0x0F FNC=0x80 with a mode code byte:
    ///   0x06 = Remote Run
    ///   0x01 = Remote Program
    ///
    /// Note for real PLC use: the keyswitch must be in the REM position for
    /// remote mode changes to be accepted. The emulator accepts both codes
    /// unconditionally.
    ///
    /// The test saves the current mode before switching and restores it after,
    /// so the PLC or emulator is left in the same state it was in before the
    /// self-test was run.
    /// </summary>
    private static void SelfTest_ProcessorMode(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Processor Mode Switching ─────────────────────");

        int original = TryTest(() => pccc.GetRunMode(), out _);

        TryTest(() => { pccc.SetProgramMode(); return true; }, out _);
        int prog = TryTest(() => pccc.GetRunMode(), out string e1);
        TestResult("SetProgramMode() → GetRunMode() = 0",
                   prog == 0, prog == 0 ? "PROGRAM" : $"got {prog}  {e1}");

        TryTest(() => { pccc.SetRunMode(); return true; }, out _);
        int run  = TryTest(() => pccc.GetRunMode(), out string e2);
        TestResult("SetRunMode() → GetRunMode() = 1",
                   run == 1, run == 1 ? "RUN" : $"got {run}  {e2}");

        // Restore original mode.
        if (original == 0)
            TryTest(() => { pccc.SetProgramMode(); return true; }, out _);
        else
            TryTest(() => { pccc.SetRunMode(); return true; }, out _);
    }

    // ── Test group 11: Latency measurement ───────────────────────────────────

    /// <summary>
    /// Measures the per-request round-trip latency for a short burst of reads.
    ///
    /// This is primarily an informational test; the only pass/fail criterion is
    /// that the average latency is below 200 ms, which flags obvious
    /// configuration problems (e.g. RSLinx holding a lock, wrong baud rate).
    ///
    /// The results are most useful for:
    ///   - Comparing DF1 serial vs EIP transport overhead side by side.
    ///   - Detecting TCP Nagle delay (visible as spikes in the max column).
    ///   - Establishing a per-transport latency baseline for regression tracking.
    ///
    /// The first <c>warmup</c> reads are discarded to avoid counting TCP
    /// connection setup or serial port driver initialisation in the numbers.
    /// </summary>
    private static void SelfTest_Latency(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Latency Measurement ──────────────────────────");

        const int warmup  = 1;
        const int samples = 20;
        var latencies = new List<double>(samples);

        for (int i = 0; i < warmup + samples; i++)
        {
            var t = Stopwatch.StartNew();
            TryTest(() => pccc.ReadAny("N7:0"), out _);
            t.Stop();
            if (i >= warmup)
                latencies.Add(t.Elapsed.TotalMilliseconds);
        }

        if (latencies.Count > 0)
        {
            double min = latencies.Min();
            double max = latencies.Max();
            double avg = latencies.Average();
            Console.WriteLine($"  Samples : {latencies.Count} reads of N7:0");
            Console.WriteLine($"  Min     : {min:F1} ms");
            Console.WriteLine($"  Avg     : {avg:F1} ms");
            Console.WriteLine($"  Max     : {max:F1} ms");

            TestResult("Average latency < 200 ms (configuration check)",
                       avg < 200.0, $"{avg:F1} ms avg");
        }
        else
        {
            TestResult("Latency measurement collected samples", false, "no samples");
        }
    }

     // ── Test group 12: Initialize Memory ────────────────────────────────────
    private static void SelfTest_InitializeMemory(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Initialize Memory Test ─────────────────────────");
        // Write test values
        try
        {
            // Use reflection to call WriteData? Or we can reuse existing WriteData (public)
            // WriteData is public, so it's fine.
            pccc.WriteData("N7:3", 0x1234);
            pccc.WriteData("ST18:2", "INIT_TEST");
        }
        catch (Exception ex)
        {
            TestResult("InitializeMemory preparation", false, ex.Message);
            return;
        }

        // Send raw Initialize Memory command (0x0F/0x57)
        byte[] pdu = new byte[7];
        pdu[0] = (byte)pccc.TargetNode;
        pdu[1] = (byte)pccc.MyNode;
        pdu[2] = 0x0F;
        pdu[3] = 0x00;
        pdu[4] = 0x00;
        pdu[5] = 0x00;
        pdu[6] = 0x57; // FNC Initialize Memory

        var (status, _, _) = pccc.SendRawPduAndGetResponse(pdu);
        if (status != 0)
        {
            TestResult("InitializeMemory() call", false, $"status {status}");
            return;
        }

        // Read back using public ReadAny
        string n7val = pccc.ReadAny("N7:3");
        string st18val = pccc.ReadAny("ST18:2");
        bool n7ok = n7val == "0";
        bool st18ok = st18val == "";
        TestResult("N7:3 reset to 0 after InitializeMemory", n7ok, n7ok ? "" : $"got {n7val}");
        TestResult("ST18:2 reset to empty after InitializeMemory", st18ok, st18ok ? "" : $"got {st18val}");
    }

    // ── Test group 13: Link Parameters (DH485) ──────────────────────────────
    private static void SelfTest_LinkParameters(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Link Parameters Test ───────────────────────────");

        // Read default (should be 31)
        var (status,  response,  _) = pccc.SendRawPduAndGetResponse(BuildPdu(pccc, 0x06, 0x09));
        
        byte defaultMax = 0;
        bool readOk = status == 0 && response != null && response.Length >= 7;
        if (readOk&& response != null)
        {
            // Response inner frame: DST,SRC,CMD,STS,TNS,FUNC?,DATA
            // For CMD 0x06 reply without FUNC byte? Actually GetStatus responses have no FUNC, but Read Link Params may have.
            // Safer: data starts at offset 6
            defaultMax = response[6];
            readOk = defaultMax == 31;
        }
        TestResult("ReadLinkParameters default = 31", readOk, readOk ? "" : $"got {defaultMax}");

        // Set to 15
        var (setStatus, _, _)       = pccc.SendRawPduAndGetResponse(BuildPdu(pccc, 0x06, 0x0A, 15));
        if (setStatus != 0)
        {
            TestResult("SetLinkParameters(15)", false, $"status {setStatus}");
            return;
        }

        // Read again
        var (status2, response2, _) = pccc.SendRawPduAndGetResponse(BuildPdu(pccc, 0x06, 0x09));
        byte newMax = 0;
        if (status2 == 0 && response2 != null && response2.Length >= 7)
            newMax = response2[6];
        TestResult("ReadLinkParameters returns 15 after set", newMax == 15, $"got {newMax}");
    }

    private static byte[] BuildPdu(Comm.PCCCComm pccc, byte cmd, byte fnc, params byte[] data)
    {
        byte[] pdu = new byte[7 + data.Length];
        pdu[0] = (byte)pccc.TargetNode;
        pdu[1] = (byte)pccc.MyNode;
        pdu[2] = cmd;
        pdu[3] = 0x00;  // STS
        pdu[4] = 0x00;  // TNS lo (library replaces)
        pdu[5] = 0x00;  // TNS hi
        pdu[6] = fnc;
        data.CopyTo(pdu, 7);
        return pdu;
    }

    // ── Test group 14: Read-Modify-Write (FNC 0x26) ─────────────────────────
    private static void SelfTest_ReadModifyWrite(Comm.PCCCComm pccc)
    {
        Console.WriteLine("── Read-Modify-Write Test ─────────────────────────");

        // Clear B3:1 using public WriteData
        try
        {
            pccc.WriteData("B3:1", 0);
        }
        catch (Exception ex)
        {
            TestResult("RMW preparation", false, ex.Message);
            return;
        }

        // Build RMW request: set bits 0 and 2 (OR mask 0x0005, AND mask 0xFFFF)
        // Payload: fileNumber(1), fileType(1), element(1), subElement(1), andMask(2), orMask(2)
        byte[] payload = new byte[8];
        payload[0] = 1;     // fileNumber = 1 (B3:1 element 1? Wait B3:1 is file 3, element 1. File number is 3? Let's correct: B3:1 = fileType 0x85, fileNumber 3, element 1)
        // Actually B3 file number is 3. So fileNumber = 3
        payload[0] = 3;
        payload[1] = 0x85;  // fileType Binary
        payload[2] = 1;     // element = 1
        payload[3] = 0;     // subElement = 0
        payload[4] = 0xFF;  // andMask low
        payload[5] = 0xFF;  // andMask high
        payload[6] = 0x05;  // orMask low (bits 0 and 2)
        payload[7] = 0x00;  // orMask high

        byte[] pdu = new byte[7 + payload.Length];
        pdu[0] = (byte)pccc.TargetNode;
        pdu[1] = (byte)pccc.MyNode;
        pdu[2] = 0x0F;
        pdu[3] = 0x00;
        pdu[4] = 0x00;
        pdu[5] = 0x00;
        pdu[6] = 0x26; // FNC RMW
        Array.Copy(payload, 0, pdu, 7, payload.Length);

        var (status, _, _) = pccc.SendRawPduAndGetResponse(pdu);
        TestResult("RMW returns status 0", status == 0, status != 0 ? $"status {status}" : "");

        // Read back using public ReadAny
        string val = pccc.ReadAny("B3:1");
        bool ok = int.TryParse(val, out int intVal) && intVal == 5;
        TestResult("RMW set bits 0 and 2 → value 5", ok, ok ? "" : $"got {val}");

        // Now clear bit 0: AND mask 0xFFFE, OR mask 0
        payload[4] = 0xFE; // andMask low
        payload[5] = 0xFF; // andMask high
        payload[6] = 0x00; // orMask low
        payload[7] = 0x00; // orMask high
        Array.Copy(payload, 0, pdu, 7, payload.Length);
        var (status2, _, _) = pccc.SendRawPduAndGetResponse(pdu);
        TestResult("RMW clear returns status 0", status2 == 0, status2 != 0 ? $"status {status2}" : "");

        val = pccc.ReadAny("B3:1");
        ok = int.TryParse(val, out intVal) && intVal == 4;
        TestResult("RMW clear bit 0 → value 4 (bit2 only)", ok, ok ? "" : $"got {val}");
    }

// =============================================================================
// SECTION 6 — Communication statistics
// =============================================================================

    // Statistics are accumulated globally across the demo and stress test.
    // The interactive CLI does not increment these counters so that manual
    // exploration does not pollute the numbers from automated sequences.
    //
    // All fields use Interlocked operations for thread-safety in the unlikely
    // event that future callers invoke operations concurrently.

    private static long _totalRequests   = 0;
    private static long _successRequests = 0;
    private static long _timeouts        = 0;
    private static long _naks            = 0;
    private static long _otherErrors     = 0;

    private static void RecordSuccess()    { Interlocked.Increment(ref _totalRequests);   Interlocked.Increment(ref _successRequests); }
    private static void RecordTimeout()    { Interlocked.Increment(ref _totalRequests);   Interlocked.Increment(ref _timeouts); }
    private static void RecordNak()        { Interlocked.Increment(ref _totalRequests);   Interlocked.Increment(ref _naks); }
    private static void RecordOtherError() { Interlocked.Increment(ref _totalRequests);   Interlocked.Increment(ref _otherErrors); }

    /// <summary>Prints cumulative statistics since the last reset.</summary>
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

    /// <summary>Resets all statistic counters to zero.</summary>
    private static void ResetStats()
    {
        Interlocked.Exchange(ref _totalRequests,   0);
        Interlocked.Exchange(ref _successRequests, 0);
        Interlocked.Exchange(ref _timeouts,        0);
        Interlocked.Exchange(ref _naks,            0);
        Interlocked.Exchange(ref _otherErrors,     0);
        Console.WriteLine("Statistics reset.");
    }

    /// <summary>
    /// Wraps a PCCC operation for use in the demo and stress test.
    ///
    /// On success the return value is passed through and RecordSuccess() is
    /// called. On failure the exception is caught, the appropriate counter is
    /// incremented, the error is printed, and the type default is returned so
    /// the caller can continue without crashing.
    ///
    /// PCCCException messages are inspected to classify failures:
    ///   "NAK"        — the PLC or emulator rejected the frame
    ///   "No Response" / "Timeout" — the PLC did not reply within the timeout
    ///   anything else — unexpected protocol or application error
    /// </summary>
    private static T? Execute<T>(Func<T> action, string context = "")
    {
        try
        {
            T result = action();
            RecordSuccess();
            return result;
        }
        catch (Comm.Pccc.PCCCException ex)
        {
            if      (ex.Message.Contains("NAK"))          RecordNak();
            else if (ex.Message.Contains("No Response") ||
                     ex.Message.Contains("Timeout"))      RecordTimeout();
            else                                           RecordOtherError();
            Console.WriteLine($"Error {context}: {ex.Message}");
            return default;
        }
        catch (Exception ex)
        {
            RecordOtherError();
            Console.WriteLine($"Unexpected error {context}: {ex.Message}");
            return default;
        }
    }

    /// <summary>Convenience overload for void-returning operations.</summary>
    private static void ExecuteVoid(Action action, string context = "")
        => Execute(() => { action(); return true; }, context);

// =============================================================================
// SECTION 7 — Low-level helpers
// =============================================================================

    private static void WriteHex(string prefix, byte[] data, int length)
    {
        if (length <= 0 || data == null) return;
        if (length > data.Length) length = data.Length;
        {
            Console.Write($"{prefix} ");
            WriteHex(Console.Out, data, length);
            Console.WriteLine();
        }
    }

    private static void WriteHex(TextWriter writer, byte[] data, int length)
    {
        for (int i = 0; i < length; i++)
        {
            if (i > 0) writer.Write(' ');
            writer.Write(data[i].ToString("X2"));
        }
    }

    /// <summary>
    /// Normalises and validates a serial port name for the current platform.
    ///
    /// Windows: checks against SerialPort.GetPortNames() (case-insensitive).
    /// Linux / macOS: accepts both "ttyUSB0" and "/dev/ttyUSB0" and resolves
    ///   to the full /dev/... path. If not found, the error message lists the
    ///   ttyUSB*, ttyS*, and ttyACM* devices currently available in /dev.
    /// </summary>
    private static string NormalizePortName(string portName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string[] available = SerialPort.GetPortNames();
            if (!available.Contains(portName, StringComparer.OrdinalIgnoreCase))
                throw new Exception(
                    $"Port '{portName}' not found. Available: {string.Join(", ", available)}");
            return portName;
        }
        else
        {
            // Accept both "ttyUSB0" and "/dev/ttyUSB0" as input.
            string baseName = portName.StartsWith("/dev/") ? portName[5..] : portName;
            string fullPath = $"/dev/{baseName}";

            string[] all = Directory.GetFiles("/dev", "tty*");
            if (all.Contains(fullPath)) return fullPath;
            if (all.Contains(portName)) return portName;

            string[] likely = all.Where(p =>
                p.StartsWith("/dev/ttyUSB") ||
                p.StartsWith("/dev/ttyS")   ||
                p.StartsWith("/dev/ttyACM")).ToArray();

            throw new Exception(
                $"Port '{portName}' not found. Available tty devices: " +
                (likely.Length > 0 ? string.Join(", ", likely) : "(none)"));
        }
    }

// =============================================================================
// SECTION 8 — Help text
// =============================================================================

    /// <summary>Prints full command-line usage text to the console.</summary>
    private static void PrintUsage()
    {
        Console.WriteLine("PCCCComm Example Client");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- [port] [options]");
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  [port]                       Serial port (default: COM1 / ttyUSB0)");
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
        Console.WriteLine("  --interactive-only           Skip demo, go straight to CLI");
        Console.WriteLine("  --no-interactive             Run demo only, then exit");
        Console.WriteLine("  --stress-test [n]            Stress test; n = iterations (0=infinite)");
        Console.WriteLine("  --scan-nodes [from] [to]     Scan DF1 node range (default 1–31)");
        Console.WriteLine("  --help, -h                   Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- COM1");
        Console.WriteLine("  dotnet run -- COM1 --interactive-only");
        Console.WriteLine("  dotnet run -- COM1 --stress-test 500");
        Console.WriteLine("  dotnet run -- COM1 --mode df1master --scan-nodes");
        Console.WriteLine("  dotnet run -- COM1 --mode df1master --scan-nodes 1 8");
        Console.WriteLine("  dotnet run -- --mode eip --host 127.0.0.1");
        Console.WriteLine("  dotnet run -- --mode eip --host 127.0.0.1 --stress-test");
        Console.WriteLine("  dotnet run -- --mode csp --host 127.0.0.1");
        Console.WriteLine("  dotnet run -- --mode csp --host 127.0.0.1 --lsap-control 05");
    }

    /// <summary>Prints the interactive CLI command reference.</summary>
    private static void PrintInteractiveHelp()
    {
        Console.WriteLine("Data access:");
        Console.WriteLine("  read <addr> [count]            Read one or more elements");
        Console.WriteLine("  write <addr> <val> [val...]    Write integer(s) to address");
        Console.WriteLine("  writestring <addr> <text>      Write string to ST file");
        Console.WriteLine("  sendhex <DST> <CMD> <FNC> [data...]");
        Console.WriteLine("                                 Send raw PCCC PDU (hex bytes)");
        Console.WriteLine("  wordread <type> <num> <elem> <offset> <words>");
        Console.WriteLine("                                 Word Range Read (PLC-5)");
        Console.WriteLine("  wordwrite <type> <num> <elem> <offset> <hex...>");
        Console.WriteLine("                                 Word Range Write (PLC-5)");
        Console.WriteLine();
        Console.WriteLine("Processor:");
        Console.WriteLine("  type                           Show processor type code");
        Console.WriteLine("  mode                           Show current mode (RUN/PROGRAM)");
        Console.WriteLine("  setrun                         Switch to RUN mode");
        Console.WriteLine("  setprog                        Switch to PROGRAM mode");
        Console.WriteLine();
        Console.WriteLine("Testing:");
        Console.WriteLine("  selftest                       Run exhaustive self-test suite");
        Console.WriteLine("  stats                          Show communication statistics");
        Console.WriteLine("  resetstats                     Reset statistics counters");
        Console.WriteLine();
        Console.WriteLine("Node management:");
        Console.WriteLine("  scannodes [from] [to]          Scan DF1 node range for live PLCs");
        Console.WriteLine("                                 (default range: 1–31)");
        Console.WriteLine("  settarget <node>               Change target node at runtime and probe");
        Console.WriteLine("  watch <addr> [interval_ms]     Monitor address, print on change");
        Console.WriteLine("                                 (default interval: 500 ms)");
        Console.WriteLine();
        Console.WriteLine("  exit / quit                    Leave interactive mode");
        Console.WriteLine("  help                           This reference");
    }
}

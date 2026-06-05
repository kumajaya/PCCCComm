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

using System.IO;
using System.IO.Ports;
using System.Runtime.InteropServices;
using Comm = PCCCComm;

/// <summary>
/// Enhanced PCCC client for testing PCCCEmulator or real PLCs.
/// Supports interactive CLI, communication statistics, and stress testing.
/// </summary>
class Program
{
    // ─── Communication Statistics ─────────────────────────────────────
    private static long _totalRequests = 0;
    private static long _successfulRequests = 0;
    private static long _timeouts = 0;
    private static long _naks = 0;
    private static long _otherErrors = 0;

    private static void RecordSuccess() { Interlocked.Increment(ref _totalRequests); Interlocked.Increment(ref _successfulRequests); }
    private static void RecordTimeout() { Interlocked.Increment(ref _totalRequests); Interlocked.Increment(ref _timeouts); }
    private static void RecordNak() { Interlocked.Increment(ref _totalRequests); Interlocked.Increment(ref _naks); }
    private static void RecordOtherError() { Interlocked.Increment(ref _totalRequests); Interlocked.Increment(ref _otherErrors); }

    private static void PrintStats()
    {
        Console.WriteLine("\n=== Communication Statistics ===");
        Console.WriteLine($"Total requests   : {_totalRequests}");
        Console.WriteLine($"Successful       : {_successfulRequests}");
        Console.WriteLine($"Timeouts         : {_timeouts}");
        Console.WriteLine($"NAK responses    : {_naks}");
        Console.WriteLine($"Other errors     : {_otherErrors}");
        if (_totalRequests > 0)
        {
            double errorRate = (double)(_timeouts + _naks + _otherErrors) / _totalRequests * 100;
            Console.WriteLine($"Error rate       : {errorRate:F2}%");
        }
        Console.WriteLine("=================================");
    }

    private static void ResetStats()
    {
        _totalRequests = _successfulRequests = _timeouts = _naks = _otherErrors = 0;
        Console.WriteLine("Statistics reset.");
    }

    // ─── Helper to execute PCCC operation with statistics ──────────────
    private static T? Execute<T>(Func<T> action, string errorContext = "")
    {
        try
        {
            var result = action();
            RecordSuccess();
            return result;
        }
        catch (Comm.DF1Exception ex)
        {
            if (ex.Message.Contains("NAK")) RecordNak();
            else if (ex.Message.Contains("No Response") || ex.Message.Contains("Timeout")) RecordTimeout();
            else RecordOtherError();
            Console.WriteLine($"Error {errorContext}: {ex.Message}");
            return default;  // don't throw, continue to next command
        }
        catch (Exception ex)
        {
            RecordOtherError();
            Console.WriteLine($"Unexpected error {errorContext}: {ex.Message}");
            return default;
        }
    }

    private static void ExecuteVoid(Action action, string errorContext = "")
    {
        Execute(() => { action(); return true; }, errorContext);
    }

    /// <summary>
    /// Sends a raw PCCC PDU using reflection to call the private PrefixAndSend method.
    /// The TNS bytes in the PDU are ignored; the library generates its own TNS.
    /// </summary>
    private static int SendRawCommand(Comm.PCCCComm df1, byte[] pdu)
    {
        var method = typeof(Comm.PCCCComm).GetMethod("PrefixAndSend",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        if (method == null)
            throw new Exception("PrefixAndSend method not found");

        int command = pdu[2];
        int func    = pdu[6];
        byte[] data = pdu.Length > 7 ? pdu[7..] : Array.Empty<byte>();

        int originalTarget = df1.TargetNode;
        df1.TargetNode = pdu[0]; // DST dari user
        try
        {
            object[] args = new object[] { command, func, data, true, 0 };
            object? resultObj = method.Invoke(df1, args);
            return resultObj != null ? (int)resultObj : -1;
        }
        finally
        {
            df1.TargetNode = originalTarget; // restore
        }
    }

    /// <summary>
    /// Normalizes and validates the serial port name for the current platform.
    /// On Windows, checks against available COM ports.
    /// On Linux, accepts both "ttyUSB0" and "/dev/ttyUSB0" formats and resolves to the full path.
    /// </summary>
    static string NormalizePortName(string portName)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            // Windows: case-insensitive check against available COM ports
            var available = SerialPort.GetPortNames();
            if (!available.Contains(portName, StringComparer.OrdinalIgnoreCase))
            {
                throw new Exception(
                    $"Port '{portName}' not found. Available ports: " +
                    $"{string.Join(", ", available)}");
            }
            return portName;
        }
        else // Linux, macOS (treat like Linux for tty devices)
        {
            // Remove leading "/dev/" if present to get the base name
            string baseName = portName.Replace("/dev/", "");
            var ports = Directory.GetFiles("/dev", "tty*");
            string fullPath = $"/dev/{baseName}";

            if (ports.Contains(fullPath))
                return fullPath;
            if (ports.Contains(portName))
                return portName;

            // Provide a helpful error message listing likely serial devices
            var likelyPorts = ports.Where(p => p.StartsWith("/dev/ttyUSB")
                    || p.StartsWith("/dev/ttyS") || p.StartsWith("/dev/ttyACM"));
            throw new Exception(
                $"Port '{portName}' not found. Available tty devices: " +
                $"{string.Join(", ", likelyPorts)}");
        }
    }

    // ─── Main ─────────────────────────────────────────────────────────
    static void Main(string[] args)
    {
        string portName = "COM1";
        int baud = 19200;
        Parity parity = Parity.None;
        int targetNode = 1;
        int myNode = 0;
        string checksum = "crc";
        bool interactiveOnly = false;
        bool noInteractive = false;
        bool stressTest = false;
        int stressLoopCount = 0; // 0 = infinite until keypress

        // ── Parse arguments ────────────────────────────────────────────
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].ToLowerInvariant();

            if (i == 0 && !a.StartsWith("--"))
                portName = args[i];
            else if (a == "--baud" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var b)) baud = b;
            }
            else if (a == "--parity" && i + 1 < args.Length)
            {
                parity = args[++i].ToLowerInvariant() switch
                {
                    "odd" => Parity.Odd,
                    "even" => Parity.Even,
                    _ => Parity.None
                };
            }
            else if (a == "--target" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var n)) targetNode = n;
            }
            else if (a == "--mynode" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var n)) myNode = n;
            }
            else if (a == "--checksum" && i + 1 < args.Length)
            {
                checksum = args[++i].ToLowerInvariant();
            }
            else if (a == "--interactive-only")
            {
                interactiveOnly = true;
            }
            else if (a == "--no-interactive")
            {
                noInteractive = true;
            }
            else if (a == "--stress-test")
            {
                stressTest = true;
                if (i + 1 < args.Length && int.TryParse(args[i + 1], out var loops))
                {
                    stressLoopCount = loops;
                    i++;
                }
            }
            else if (a == "--help" || a == "-h")
            {
                PrintUsage();
                return;
            }
        }

        // After parsing portName from arguments, normalize and validate it
        portName = NormalizePortName(portName);

        var df1 = new Comm.PCCCComm(portName, baud, parity)
        {
            TargetNode = targetNode,
            MyNode = myNode,
            CheckSum = checksum == "crc" ? Comm.CheckSumOptions.Crc : Comm.CheckSumOptions.Bcc
        };

        try
        {
            df1.OpenComms();
            Console.WriteLine($"Connected on {portName}");
            Console.WriteLine($"Baud={baud}, Parity={parity}, Checksum={df1.CheckSum}");
            Console.WriteLine($"MyNode={myNode}, TargetNode={targetNode}\n");

            if (!interactiveOnly)
            {
                // ── Demo operations ─────────────────────────────────────
                int proc = Execute(() => df1.GetProcessorType(), "GetProcessorType");
                Console.WriteLine($"Processor Type: 0x{proc:X2}");

                Console.WriteLine("\n--- Read Operations (Demo) ---");
                string o0 = Execute(() => df1.ReadAny("O0:0"), "Read O0:0") ?? "";
                Console.WriteLine($"O0:0 = {o0}");
                string i1 = Execute(() => df1.ReadAny("I1:0"), "Read I1:0") ?? "";
                Console.WriteLine($"I1:0 = {i1}");
                ExecuteVoid(() => df1.WriteData("B3:0", 0), "Write B3:0 init");
                string b3 = Execute(() => df1.ReadAny("B3:0"), "Read B3:0") ?? "";
                Console.WriteLine($"B3:0 = {b3}");
                string n7 = Execute(() => df1.ReadAny("N7:0"), "Read N7:0") ?? "";
                Console.WriteLine($"N7:0 = {n7}");
                string f8 = Execute(() => df1.ReadAny("F8:0"), "Read F8:0") ?? "";
                Console.WriteLine($"F8:0 = {f8}");

                int mode = Execute(() => df1.GetRunMode(), "GetRunMode");
                Console.WriteLine(mode == 1 ? "PLC is in RUN mode" : "PLC is in PROGRAM mode");

                Console.WriteLine("\n--- Data Files ---");
                Comm.DataFileDetails[]? files = Execute(() => df1.GetDataMemory(), "GetDataMemory");
                if (files != null)
                {
                    foreach (var f in files)
                        Console.WriteLine($"File {f.FileNumber}: Type={f.FileType} Elements={f.NumberOfElements}");
                }
                else
                {
                    Console.WriteLine("  (Failed to retrieve data files)");
                }

                Console.WriteLine("\n--- Write Operations (Demo) ---");
                Console.WriteLine("Writing 999 to N7:1...");
                ExecuteVoid(() => df1.WriteData("N7:1", 999), "Write N7:1");
                Console.WriteLine("Writing 2.718 to F8:1...");
                ExecuteVoid(() => df1.WriteData("F8:1", 2.718f), "Write F8:1");
                Console.WriteLine("Setting B3:0/0 = 1...");
                ExecuteVoid(() => df1.WriteData("B3:0/0", 1), "Write B3:0/0");
                Console.WriteLine("Setting B3:0/3 = 1...");
                ExecuteVoid(() => df1.WriteData("B3:0/3", 1), "Write B3:0/3");

                Console.WriteLine(mode == 1 ? "Switching to PROGRAM mode..." : "Switching to RUN mode...");
                if (mode == 1) ExecuteVoid(() => df1.SetProgramMode(), "SetProgramMode");
                else ExecuteVoid(() => df1.SetRunMode(), "SetRunMode");

                Console.WriteLine("\n--- Read Operations After Write (Demo) ---");
                n7 = Execute(() => df1.ReadAny("N7:1"), "Read N7:1") ?? "";
                Console.WriteLine($"N7:1 = {n7}");
                f8 = Execute(() => df1.ReadAny("F8:1"), "Read F8:1") ?? "";
                Console.WriteLine($"F8:1 = {f8}");
                b3 = Execute(() => df1.ReadAny("B3:0"), "Read B3:0") ?? "";
                Console.WriteLine($"B3:0 = {b3}");
                mode = Execute(() => df1.GetRunMode(), "GetRunMode");
                Console.WriteLine(mode == 1 ? "PLC is in RUN mode" : "PLC is in PROGRAM mode");

                PrintStats();
            }

            // ── Stress test mode ────────────────────────────────────────
            if (stressTest)
            {
                Console.WriteLine("\n--- Stress Test Mode ---");
                Console.WriteLine("Reading F8:0 continuously. Press any key to stop.");
                int count = 0;
                while (!Console.KeyAvailable && (stressLoopCount == 0 || count < stressLoopCount))
                {
                    try
                    {
                        // Read simulated sine wave value from F8:0
                        string[] val = df1.ReadAny("F8:0", 1) ?? Array.Empty<string>();
                        RecordSuccess();
                        if (++count % 100 == 0)
                        {
                            string displayValue = val.Length > 0 ? val[0] : "null";
                            Console.WriteLine($"  {count} reads completed. Last value: {displayValue}");
                        }
                    }
                    catch (Comm.DF1Exception ex)
                    {
                        if (ex.Message.Contains("NAK")) RecordNak();
                        else if (ex.Message.Contains("No Response")) RecordTimeout();
                        else RecordOtherError();
                        Console.WriteLine($"  Error at read {count + 1}: {ex.Message}");
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

            // ── Interactive CLI mode (unless --no-interactive) ─────────
            if (!noInteractive)
            {
                Console.WriteLine("\n=== Interactive CLI Mode ===");
                Console.WriteLine("Type 'help' for commands, 'exit' to quit.\n");
                bool interactive = true;
                while (interactive)
                {
                    Console.Write("PCCC> ");
                    string input = Console.ReadLine()?.Trim() ?? "";
                    if (string.IsNullOrEmpty(input)) continue;
                    string[] parts = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    string cmd = parts[0].ToLowerInvariant();

                    try
                    {
                        switch (cmd)
                        {
                            case "exit":
                            case "quit":
                                interactive = false;
                                break;
                            case "help":
                                PrintInteractiveHelp();
                                break;
                            case "stats":
                                PrintStats();
                                break;
                            case "resetstats":
                                ResetStats();
                                break;
                            case "read":
                                if (parts.Length < 2) { Console.WriteLine("Usage: read <address> [count]"); break; }
                                string addr = parts[1];
                                int cnt = parts.Length >= 3 ? int.Parse(parts[2]) : 1;
                                string[] readResult = df1.ReadAny(addr, cnt) ?? Array.Empty<string>();
                                Console.WriteLine($"Result: {string.Join(", ", readResult)}");
                                break;
                            case "write":
                                if (parts.Length < 3) { Console.WriteLine("Usage: write <address> <value> [value2...]"); break; }
                                string waddr = parts[1];
                                var values = new List<int>();
                                for (int i = 2; i < parts.Length; i++)
                                    values.Add(int.Parse(parts[i]));
                                df1.WriteData(waddr, values.Count, values.ToArray());
                                Console.WriteLine("Write successful.");
                                break;
                            case "writestring":
                                if (parts.Length < 3) { Console.WriteLine("Usage: writestring <address> <text>"); break; }
                                string saddr = parts[1];
                                string text = string.Join(" ", parts, 2, parts.Length - 2);
                                df1.WriteData(saddr, text);
                                Console.WriteLine("String write successful.");
                                break;
                            case "mode":
                                int current = df1.GetRunMode();
                                Console.WriteLine(current == 1 ? "RUN mode" : "PROGRAM mode");
                                break;
                            case "setrun":
                                df1.SetRunMode();
                                Console.WriteLine("Switched to RUN mode");
                                break;
                            case "setprog":
                                df1.SetProgramMode();
                                Console.WriteLine("Switched to PROGRAM mode");
                                break;
                            case "type":
                                int procType = df1.GetProcessorType();
                                Console.WriteLine($"Processor Type: 0x{procType:X2}");
                                break;
                            case "sendhex":
                                if (parts.Length < 4)
                                {
                                    Console.WriteLine("Usage: sendhex <DST> <CMD> <FNC> [data...]");
                                    Console.WriteLine("Example: sendhex 01 0F A1 02 11 89 00");
                                    Console.WriteLine("(SRC=0, STS=0, TNS are auto-generated by library)");
                                    break;
                                }
                                // Parse DST, CMD, FNC as hex bytes
                                if (!byte.TryParse(parts[1], System.Globalization.NumberStyles.HexNumber, null, out byte dst) ||
                                    !byte.TryParse(parts[2], System.Globalization.NumberStyles.HexNumber, null, out byte cmd_byte) ||
                                    !byte.TryParse(parts[3], System.Globalization.NumberStyles.HexNumber, null, out byte fnc))
                                {
                                    Console.WriteLine("Invalid hex values for DST, CMD, or FNC.");
                                    break;
                                }
                                // Parse optional data bytes
                                List<byte> dataBytes = new List<byte>();
                                for (int i = 4; i < parts.Length; i++)
                                {
                                    if (byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out byte b))
                                        dataBytes.Add(b);
                                    else
                                    {
                                        Console.WriteLine($"Invalid hex data: {parts[i]}");
                                        break;
                                    }
                                }
                                // Build full PDU: DST, SRC=0, CMD, STS=0, TNS dummy (0,0), FNC, data...
                                byte[] pdu = new byte[7 + dataBytes.Count];
                                pdu[0] = dst;
                                pdu[1] = 0x00;      // SRC
                                pdu[2] = cmd_byte;
                                pdu[3] = 0x00;      // STS
                                pdu[4] = 0x00;      // TNS low (dummy)
                                pdu[5] = 0x00;      // TNS high (dummy)
                                pdu[6] = fnc;
                                for (int i = 0; i < dataBytes.Count; i++)
                                    pdu[7 + i] = dataBytes[i];

                                Console.WriteLine($"Sending: {BitConverter.ToString(pdu)}");
                                int replyCode = SendRawCommand(df1, pdu);
                                Console.WriteLine($"Reply status: {replyCode} (0=success, non-zero=error)");
                                break;
                            default:
                                Console.WriteLine("Unknown command. Type 'help' for list.");
                                break;
                        }
                    }
                    catch (Comm.DF1Exception ex)
                    {
                        Console.WriteLine($"PCCC Error: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"Error: {ex.Message}");
                    }
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Fatal error: {ex.Message}");
        }
        finally
        {
            df1.CloseComms();
            df1.Dispose();
            Console.WriteLine("\nPress Enter to exit.");
            Console.ReadLine();
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("PCCC Example Client - Enhanced Explorer");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- [port] [options]");
        Console.WriteLine();
        Console.WriteLine("Arguments:");
        Console.WriteLine("  [port]               Serial port name (default COM1)");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --baud <n>           Baud rate (default 19200)");
        Console.WriteLine("  --parity <none|odd|even>  Parity mode (default none)");
        Console.WriteLine("  --target <n>         Target PLC node id (default 1)");
        Console.WriteLine("  --mynode <n>         Local/master node id (default 0)");
        Console.WriteLine("  --checksum <crc|bcc> DF1 checksum mode (default crc)");
        Console.WriteLine("  --interactive-only   Skip the demo and go straight to interactive CLI");
        Console.WriteLine("  --no-interactive     Run only the demo, then exit");
        Console.WriteLine("  --stress-test [n]    Run stress test (n = loop count, default infinite)");
        Console.WriteLine("  --help, -h           Show this help");
        Console.WriteLine();
        Console.WriteLine("Interactive Commands:");
        Console.WriteLine("  read <addr> [cnt]    Read value(s) from address");
        Console.WriteLine("  write <addr> <v...>  Write integer(s) to address");
        Console.WriteLine("  writestring <addr> <text>   Write string to string address (ST)");
        Console.WriteLine("  sendhex <DST> <CMD> <FNC> [data...]   Send raw PCCC command (hex bytes)");
        Console.WriteLine("  mode                 Show current PLC mode (RUN/PROGRAM)");
        Console.WriteLine("  setrun               Switch PLC to RUN mode");
        Console.WriteLine("  setprog              Switch PLC to PROGRAM mode");
        Console.WriteLine("  type                 Show processor type code");
        Console.WriteLine("  stats                Show communication statistics");
        Console.WriteLine("  resetstats           Reset statistics counters");
        Console.WriteLine("  exit                 Exit interactive mode");
        Console.WriteLine("  help                 Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- COM1");
        Console.WriteLine("  dotnet run -- COM1 --interactive-only");
        Console.WriteLine("  dotnet run -- COM1 --stress-test 500");
    }

    static void PrintInteractiveHelp()
    {
        Console.WriteLine("Available commands:");
        Console.WriteLine("  read <address> [count]   - Read 1 or more elements");
        Console.WriteLine("  write <address> <val...> - Write integer values (supports multiple)");
        Console.WriteLine("  writestring <address> <text> - Write string to ST file");
        Console.WriteLine("  sendhex <DST> <CMD> <FNC> [data...] - Send raw PCCC command (hex)");
        Console.WriteLine("  mode                     - Show PLC mode (RUN/PROGRAM)");
        Console.WriteLine("  setrun                   - Set PLC to RUN mode");
        Console.WriteLine("  setprog                  - Set PLC to PROGRAM mode");
        Console.WriteLine("  type                     - Show processor type code");
        Console.WriteLine("  stats                    - Show communication statistics");
        Console.WriteLine("  resetstats               - Reset statistics");
        Console.WriteLine("  exit                     - Exit to demo end");
        Console.WriteLine("  help                     - This help");
    }
}

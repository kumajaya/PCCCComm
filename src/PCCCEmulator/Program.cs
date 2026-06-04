// SPDX-License-Identifier: GPL-3.0-or-later
// 
// PCCCEmulator - PCCC Engine and Transports for .NET
// Copyright (c) 2026 Ketut Kumajaya
// 
// Initial reference: DF1Comm.vb (Archie Jacobs); implementation substantially modified.
// which was released under GPLv2-or-later.
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

using System;
using System.IO.Ports;

/// <summary>
/// PCCC Engine and Transports for .NET Launcher.
/// 
/// Command line arguments:
///   <port>                   : serial port name (default COM2)
///   --baud <value>           : baud rate (default 19200)
///   --parity <none|odd|even> : parity mode (default none)
///   --node <n>               : emulator node id (default 1)
///   --checksum <crc|bcc>     : checksum mode (default crc)
///   --mode <df1|dh485|eip>   : transport mode (default df1)
///   --quiet, -q              : disable logging for maximum performance
///   --help, -h               : show usage
///
/// Example:
///   dotnet run -- COM2 --baud 19200 --checksum crc
///   dotnet run -- --mode eip --port 44818
///   dotnet run -- COM2 --quiet                          # High performance mode
/// </summary>
class Program
{
    static void Main(string[] args)
    {
        // Default values
        string portName = "COM2";
        int baud = 19200;
        Parity parity = Parity.None;
        int node = 1;
        string checksum = "crc";
        string mode = "df1";
        int eipPort = 44818;
        bool quietMode = false;

        // Parse positional port argument
        if (args.Length > 0 && !args[0].StartsWith("--"))
            portName = args[0];

        // Parse optional arguments
        for (int i = 0; i < args.Length; i++)
        {
            var a = args[i].ToLowerInvariant();

            if (a == "--baud" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var b))
                    baud = b;
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
            else if (a == "--node" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var n))
                    node = n;
            }
            else if (a == "--checksum" && i + 1 < args.Length)
            {
                checksum = args[++i].ToLowerInvariant();
            }
            else if (a == "--mode" && i + 1 < args.Length)
            {
                mode = args[++i].ToLowerInvariant();
            }
            else if (a == "--port" && i + 1 < args.Length)
            {
                if (int.TryParse(args[++i], out var p))
                    eipPort = p;
            }
            else if (a == "--quiet" || a == "-q")
            {
                quietMode = true;
            }
            else if (a == "--help" || a == "-h")
            {
                PrintUsage();
                return;
            }
        }

        // Validate mode
        var emulatorMode = mode switch
        {
            "df1" => PCCCEmulator.TransportMode.DF1,
            "dh485" => PCCCEmulator.TransportMode.DH485,
            "eip" => PCCCEmulator.TransportMode.EIP,
            _ => PCCCEmulator.TransportMode.DF1
        };

        // Warning for serial parameters when using EIP mode
        if (emulatorMode == PCCCEmulator.TransportMode.EIP && portName != "COM2")
        {
            Logger.Always(null, $"EIP mode ignores serial port '{portName}'. Using Ethernet only.");
        }

        // Create and start emulator
        using var emulator = new PCCCEmulator(portName, baud, parity, emulatorMode, eipPort)
        {
            MyNode = node,
            CheckSum = checksum == "crc" ? CheckSumOptions.Crc : CheckSumOptions.Bcc
        };

        // Disable logging if quiet mode is enabled
        if (quietMode)
            emulator.SetLoggingEnabled(false);

        try
        {
            emulator.Start();
            Logger.Always(null, $"PCCC emulator running");
            Console.WriteLine($"      Mode      : {mode.ToUpper()}");
            
            if (emulatorMode == PCCCEmulator.TransportMode.DF1)
            {
                Console.WriteLine($"      Port      : {portName}");
                Console.WriteLine($"      Baud rate : {baud}");
                Console.WriteLine($"      Parity    : {parity}");
                Console.WriteLine($"      Node ID   : {node}");
                Console.WriteLine($"      Checksum  : {emulator.CheckSum}");
            }
            else if (emulatorMode == PCCCEmulator.TransportMode.DH485)
            {
                Console.WriteLine($"      Port      : {portName}");
                Console.WriteLine($"      Baud rate : 19200 (fixed for DH485)");
                Console.WriteLine($"      Node ID   : {node}");
                Console.WriteLine($"      Status    : Not yet implemented");
            }
            else if (emulatorMode == PCCCEmulator.TransportMode.EIP)
            {
                Console.WriteLine($"      EIP Port  : {eipPort}");
                Console.WriteLine($"      Node ID   : {node}");
            }
            
            Console.WriteLine($"      Logging   : {(quietMode ? "Disabled (High Performance)" : "Enabled")}");
            Logger.Always(null, "Press Enter to stop.");
            Console.ReadLine();
            emulator.Stop();
        }
        catch (Exception ex)
        {
            Logger.Always(null, $"Error: {ex.Message}");
        }
    }

    static void PrintUsage()
    {
        Console.WriteLine("PCCC Emulator - PCCC Engine and Transports for .NET");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet run -- [port] [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --baud <n>           Baud rate (default 19200)");
        Console.WriteLine("  --parity <none|odd|even>  Parity mode (default none)");
        Console.WriteLine("  --node <n>           Emulator node ID (default 1)");
        Console.WriteLine("  --checksum <crc|bcc> Checksum mode (default crc)");
        Console.WriteLine("  --mode <df1|dh485|eip> Transport mode (default df1)");
        Console.WriteLine("  --port <n>           EIP port number (default 44818)");
        Console.WriteLine("  --quiet, -q          Disable logging for maximum performance");
        Console.WriteLine("  --help, -h           Show this help");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- COM2 --baud 19200 --checksum crc");
        Console.WriteLine("  dotnet run -- COM3 --baud 9600 --parity even --node 2");
        Console.WriteLine("  dotnet run -- --mode eip --port 44818");
        Console.WriteLine("  dotnet run -- COM2 --quiet                    # High performance mode");
        Console.WriteLine();
        Console.WriteLine("Note: Disabling logging eliminates string allocations and");
        Console.WriteLine("      significantly improves throughput under high load.");
        Console.WriteLine();
        Console.WriteLine("Transport Modes:");
        Console.WriteLine("  df1    - Serial DF1 full-duplex (default, fully implemented)");
        Console.WriteLine("  dh485  - DH485 via serial (future)");
        Console.WriteLine("  eip    - EtherNet/IP (EIP/PCCC) via TCP (implemented)");
    }
}

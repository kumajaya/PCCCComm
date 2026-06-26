# PCCC Communication Suite for .NET

[![License](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/)

**PCCC Communication Suite** is a complete, self‑contained .NET implementation for communicating with Allen‑Bradley PLCs using the **PCCC** (Programmable Controller Communications Command) protocol over **DF1 serial**, **EtherNet/IP** and **CSPv4**.

The suite includes:

- **PCCCComm** – A reusable communication library (supports DF1, EtherNet/IP, and CSPv4)
- **PCCCEmulator** – Standalone PLC emulator (SLC 5/04 or PLC‑5/40E with DF1, EtherNet/IP, and CSPv4)
- **Example** – Client example with interactive CLI
- **PCCCImageTool** – Desktop GUI for upload/download/compare PLC images

All components target .NET 8 and are licensed under GNU General Public License v3.0 or later (GPLv3+).

---

## Repository Structure

```
PCCCComm/
├── LICENSE                         (GNU GPL v3.0)
├── README.md                       (this file)
├── PCCCComm.sln                    # Visual Studio solution
└── src/
    ├── PCCCComm/                   # Core library
    │   ├── Core/                   # Transport abstractions
    │   │   ├── CSPTransport.cs
    │   │   ├── DF1BaseTransport.cs
    │   │   ├── DF1FullDuplexTransport.cs
    │   │   ├── DF1HalfDuplexTransport.cs
    │   │   ├── EIPTransport.cs
    │   │   ├── ISerialPort.cs
    │   │   ├── ITransport.cs
    │   │   └── SerialPortWrapper.cs
    │   ├── Handlers/               # PLC family protocol handlers
    │   │   ├── IHandlerContext.cs
    │   │   ├── IPlcHandler.cs
    │   │   ├── Plc5Handler.cs
    │   │   └── SlcHandler.cs
    │   ├── PCCC/                   # PCCC core (messages, constants, parser)
    │   │   ├── PCCCConstants.cs
    │   │   ├── PCCCErrors.cs
    │   │   ├── PCCCException.cs
    │   │   ├── PCCCMessage.cs
    │   │   ├── PCCCOptions.cs
    │   │   ├── PCCCParser.cs
    │   │   ├── PCCCProtocol.cs
    │   │   ├── README.md           # Full PCCC command set reference
    │   │   └── StringConverter.cs
    │   ├── Models.cs
    │   ├── PCCCComm.cs
    │   └── PCCCComm.csproj
    ├── PCCCEmulator/               # Standalone emulator
    ├── PCCCImageTool/              # Desktop GUI PLC image transfer
    └── Example/                    # Example client
```

---

## Features

### PCCCComm Library
- **DF1 full‑duplex** serial framing (DLE stuffing, CRC-16/BCC, ACK/NAK, ENQ)
- **DF1 half‑duplex master** over RS‑485 multi‑drop (polling, selective addressing)
- **EtherNet/IP (EIP)** transport over TCP (CIP Unconnected Send, Execute PCCC, port 44818)
- **CSPv4 (Client Server Protocol)** transport over TCP (legacy AB Ethernet, port 2222)
- Read/write any data type: integers, floats, bits, strings, timers, counters
- Switch processor between RUN and PROGRAM modes
- Auto‑detect DF1 communication settings (`DetectCommSettings()`)
- Retrieve data file directory (`GetDataMemory()`) for SLC and PLC‑5
- Upload/download complete program files (SLC file‑based and PLC‑5 bulk physical transfer)
- Support for SLC 5/01–5/05, MicroLogix 1000/1100/1200/1500, PLC‑5/40E, and other PCCC‑compatible PLCs

### PCCCEmulator (Standalone Tool)
- Emulates an SLC 5/04 (default) or PLC‑5/40E (`--family plc5`) with DF1, EIP, and CSPv4 interfaces
- **Validated against RSLinx with OPC access support** (detects PLC-5/40E or SLC-5/05) — consistent across all transports
- **DF1 half‑duplex slave** emulation for RS‑485 multi‑drop networks
- Loads real PLC program from embedded .bin resource (converted from APS .ACH archive)
- Full DF1 link layer: ACK/NAK, ENQ, checksum, and half‑duplex polling support
- Full EtherNet/IP server: TCP port 44818, UDP broadcast ListIdentity, Forward Open/Close, Connected/Unconnected Send
- Full CSPv4 server: TCP port 2222, connection registration, PCCC submode
- Real memory layout from hardware: SLC (32 data files), PLC‑5/40E (64 data files, 201 slots, 5572 words)
- Responds to Get Status (CMD 0x06 FNC 0x03) with realistic 24‑byte payload per family
- Handles Protected Typed Logical Read/Write (SLC) and Typed Read/Write (PLC‑5)
- Configurable node ID, checksum, baud rate, parity, RS‑485 direction control via command line
- Console hex logging for debugging

### Example Client
- Demonstrates reading processor type, data files, and specific addresses; writing integers, floats, bits; toggling RUN/PROGRAM mode
- Interactive CLI (`PCCC>` prompt) with read, write, writestring, sendhex, mode, stats, and more
- Communication statistics – total requests, successes, timeouts, NAKs, error rate
- Stress test mode – continuous read loop (`--stress-test [n]`)
- Works with real PLC or PCCCEmulator over virtual serial pair or Ethernet

### PCCCImageTool (Desktop GUI)
- Cross‑platform GUI built with Avalonia UI
- Upload entire PLC program to a binary file
- Download previously saved program back to the PLC
- Compares a backup file against current PLC program
- Supports SLC 5/01–5/05, MicroLogix 1000/1500, and PLC‑5 (bulk physical transfer)
- Automatic PLC detection and descriptive filename generation
- Progress indication during transfer (bytes‑based for PLC‑5 bulk, file‑based for SLC)

---

## Attribution

This C# library is a **refactored and enhanced version** of the original **DF1Comm.vb** written by **Archie Jacobs, Manufacturing Automation LLC**.  
The original Visual Basic code was faithfully ported to C# and then restructured to support multiple transports (DF1, EIP). All DF1 serial behaviours remain identical to the original implementation.

We thank Archie Jacobs for providing a robust, well‑tested DF1 implementation to the industrial automation community.

---

## Requirements

- **.NET 8 SDK** or later
- Windows / Linux / macOS (serial port support required for DF1)
- For testing without hardware: a **virtual serial pair** (e.g. com0com on Windows, `socat` on Linux) or Ethernet loopback

---

## Build

Clone the repository and build the whole solution:

```bash
git clone https://github.com/kumajaya/PCCCComm.git
cd PCCCComm
dotnet build -c Release PCCCComm.sln
```

Individual projects can also be built separately:

```bash
dotnet build -c Release src/PCCCComm/PCCCComm.csproj
dotnet build -c Release src/PCCCEmulator/PCCCEmulator.csproj
dotnet build -c Release src/Example/Example.csproj
dotnet build -c Release src/PCCCImageTool/PCCCImageTool.csproj
```

---

## Usage

### 1. Using the PCCCComm Library

#### 1a. Using the PCCCComm Library via DF1 serial full‑duplex transport

```csharp
using PCCCComm;

var comm = new PCCCComm("COM2", 19200, Parity.None)
{
    TargetNode = 1,
    MyNode = 0,
    CheckSum = CheckSumOptions.Crc
};

comm.OpenComms();

// Read processor type
int procType = comm.GetProcessorType();
Console.WriteLine($"Processor: 0x{procType:X2}");

// Read an integer from N7:0
string value = comm.ReadAny("N7:0");
Console.WriteLine($"N7:0 = {value}");

// Write a float to F8:1
comm.WriteData("F8:1", 3.14159f);

// Set RUN mode
comm.SetRunMode();

comm.CloseComms();
```

#### 1b. Using the PCCCComm Library via DF1 half‑duplex master for RS‑485 transport

```csharp
using PCCCComm;

var comm = new PCCCComm("COM2", 19200, Parity.None);
comm.Protocol = "DF1Master";
comm.SlaveAddress = 1;  // node address of the slave to poll
comm.Rs485Mode = DF1HalfDuplexTransport.Rs485ControlMode.Auto;
comm.EchoSuppression = false; // enable if your adapter echoes back its own transmission
comm.OpenComms();

string value = comm.ReadAny("N7:0");
comm.WriteData("N7:1", 12345);
comm.CloseComms();
```

#### 1c. Using the PCCCComm Library via EtherNet/IP transport

```csharp
using PCCCComm;

var comm = PCCCComm.ForEip("192.168.1.10", 44818); // EIP transport
comm.OpenComms();

string value = comm.ReadAny("N7:0");
Console.WriteLine(value);

comm.CloseComms();
```

#### 1d. Using the PCCCComm Library via CSPv4 transport

```csharp
using PCCCComm;

var comm = PCCCComm.ForCsp("192.168.1.10", 2222, 5000, 0x05); // lsapControlByte = 0x05 for RSLinx
comm.OpenComms();

string value = comm.ReadAny("N7:0");
Console.WriteLine(value);

comm.CloseComms();
```

### 2. Running the Emulator

#### 2a. Running the Emulator via DF1 full‑duplex transport

```bash
dotnet run --project src/PCCCEmulator -- COM2 --baud 19200 --checksum crc
```

#### 2b. Running the Emulator via DF1 half‑duplex slave transport

```bash
dotnet run --project src/PCCCEmulator -- COM2 --mode df1slave --node 1 --rs485-mode auto
```

#### 2c. Running the Emulator via EtherNet/IP transport

```bash
dotnet run --project src/PCCCEmulator -- --mode eip --port 44818
```

#### 2d. Running the Emulator via CSPv4 transport

```bash
dotnet run --project src/PCCCEmulator -- --mode csp --csp-port 2222
```

### 3. Running the Example Client

```bash
dotnet run --project src/Example -- COM1 --target 1 --checksum crc
```

### 4. Testing Emulator + Client Together (DF1)

- Create a virtual serial pair (e.g. `COM1` ↔ `COM2`).
- Start emulator on `COM2` and client on `COM1`.

### 5. Testing Emulator + Client Together (EIP)

- Start emulator: `dotnet run --project src/PCCCEmulator -- --mode eip`
- Start example client (if extended to EIP) or use any EIP client (RSLinx, libplctag, pycomm3).

### 6. Running the GUI Tool (PCCCImageTool)

```bash
dotnet run --project src/PCCCImageTool
```

---

## Protocol Reference

The implementation follows **Allen‑Bradley Publication 1770‑6.5.16** (DF1 Protocol and Command Set) and **ODVA EtherNet/IP Specification** (Volumes 1 & 2).

For a complete, up‑to‑date list of all PCCC commands and their implementation status, see the  
**[PCCC Protocol Subset Implementation Guide](src/PCCCComm/PCCC/README.md)**.

Supported transport modes:
- DF1 full‑duplex (point‑to‑point)
- DF1 half‑duplex master (RS‑485 multi‑drop)
- EtherNet/IP (PCCC‑over‑CIP)
- CSPv4 (Client Server Protocol) over TCP

DF1 checksum modes as per AB specification:
- **BCC**: two's complement of sum.
- **CRC‑16**: initial `0x0000`, polynomial `0xA001`, ETX byte `0x03` included.

EtherNet/IP encapsulation:
- **RegisterSession** (0x0065), **Unconnected Send** (0x006F), **Connected Send** (0x0070)
- CIP service **Execute PCCC** (0x4B) and **CM Unconnected Send** (0x52)

---

## Troubleshooting

| Issue | Likely solution |
|-------|------------------|
| `No response, Check COM Settings` | Verify port, baud rate, parity, and that the target is powered and connected. |
| `Checksum mismatch` | Ensure both sides use the same checksum mode (`--checksum crc` or `bcc`). |
| `Illegal Command or Format` | The target may not support the addressed file/element. Check file numbers and element bounds. |
| `Processor is in Program mode` | Normal – writes may be restricted. Use `SetRunMode()` to change. |
| `Port busy` | Only one application can open a COM port at a time. Close other programs (RSLinx, etc.). |
| `EIP or CSPv4 connection timeout` | Check firewall (TCP/UDP 44818 for EIP or TCP 2222 for CSPv4), verify emulator or PLC is reachable, and that `--mode eip` or `--mode csp` is used. |
| `No communication in half‑duplex mode` | For testing with full‑duplex serial cables or virtual pairs, enable `--echo-suppression`. For real RS‑485, disable it unless your adapter echoes. Ensure both sides use same baud rate, parity, and checksum. |

---

## Contributing

1. Fork the repository.
2. Create a feature branch (`git checkout -b feature/amazing-feature`).
3. Commit your changes (`git commit -m 'Add amazing feature'`).
4. Push to the branch (`git push origin feature/amazing-feature`).
5. Open a Pull Request.

Please keep all code **self‑contained** (avoid external dependencies except `System.IO.Ports` and `System.Net.Sockets`). Add unit tests when possible.

---

## License

**PCCC Communication Suite**  
Copyright (c) 2026 Ketut Kumajaya

The original **DF1Comm.vb** (by Archie Jacobs, Manufacturing Automation LLC) was released under the  
**GNU General Public License, version 2 or any later version**.

This C# port and its extensions are **derived works** of the original VB code.  
Therefore, they are also licensed under the **GNU General Public License v3.0 or any later version**.

```
This program is free software: you can redistribute it and/or modify
it under the terms of the GNU General Public License as published by
the Free Software Foundation, either version 3 of the License, or
(at your option) any later version.

This program is distributed in the hope that it will be useful,
but WITHOUT ANY WARRANTY; without even the implied warranty of
MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE. See the
GNU General Public License for more details.

You should have received a copy of the GNU General Public License
along with this program. If not, see <https://www.gnu.org/licenses/>.
```

Full text in [`LICENSE`](LICENSE) file.

---

### Compatibility with Other Projects

- **GPLv3 is compatible with Apache 2.0 only in one direction:** Apache 2.0 code can be combined with GPLv3 code, but the combined work must be distributed under GPLv3.  
- If you need to use this library in a proprietary or Apache‑licensed project, you must keep it as a **separate plugin/component** loaded dynamically, without merging code into your main application.

See the [GNU license compatibility FAQ](https://www.gnu.org/licenses/license-compatibility.en.html) for details.

---

## Acknowledgements

- **Archie Jacobs, Manufacturing Automation LLC** – for the original VB DF1Comm implementation.
- **Allen‑Bradley / Rockwell Automation** – for the DF1 protocol specification (Publication 1770‑6.5.16) and EtherNet/IP specification.
- **Kyle Hayes** – for libplctag, which served as reference for EIP implementation.

---

## Related Projects

- [PCCCEmulator](src/PCCCEmulator/README.md) – standalone emulator
- [Example Client](src/Example/README.md) – usage demonstration
- [PCCCImageTool](src/PCCCImageTool/README.md) – desktop GUI for upload/download

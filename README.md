# PCCC Communication Suite for .NET

[![License](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/)

**PCCC Communication Suite** is a complete, self‑contained .NET implementation for communicating with Allen‑Bradley PLCs using the **PCCC** (Programmable Controller Communications Command) protocol over **DF1 serial** and **EtherNet/IP**.

The suite includes:

- **PCCCComm** – A reusable communication library (supports DF1 and EtherNet/IP)
- **PCCCEmulator** – Standalone PLC emulator (SLC 5/03 with DF1 and EtherNet/IP)
- **Example** – Client example with interactive CLI
- **PCCCImageTool** – Desktop GUI for upload/download/compare PLC images

All components target .NET 8 and are licensed under GNU General Public License v3.0 or later (GPLv3+).

---

## Repository Structure

```
PCCCComm/
├── LICENSE                         (GNU GPL v3.0)
├── README.md                       (this file)
├── src/
│   ├── PCCCComm/                   # Core library (DF1 + EIP transport)
│   │   ├── PCCCComm.cs
│   │   ├── PCCCCommOptions.cs
│   │   ├── PCCCCommException.cs
│   │   ├── Models.cs
│   │   ├── Core/
│   │   │   ├── ITransport.cs
│   │   │   ├── DF1Transport.cs
│   │   │   ├── EIPTransport.cs
│   │   │   ├── MessageDecoder.cs
│   │   │   ├── PacketBuilder.cs
│   │   │   ├── AddressParser.cs
│   │   │   ├── StringConverter.cs
│   │   │   ├── ISerialPort.cs
│   │   │   └── SerialPortWrapper.cs
│   │   └── PCCCComm.csproj
│   ├── PCCCEmulator/               # PCCC emulator (standalone)
│   │   ├── PCCCEmulator.cs
│   │   ├── PlcMemory.cs
│   │   ├── DF1Transport.cs (emulator version)
│   │   ├── EIPTransport.cs (emulator version)
│   │   ├── EIPClient.cs
│   │   ├── ILinkTransport.cs
│   │   ├── MessageDecoder.cs
│   │   ├── Logger.cs
│   │   ├── Program.cs
│   │   ├── PCCCEmulator.csproj
│   │   └── README.md
│   ├── PCCCImageTool/             # Desktop GUI for upload/download
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Models/
│   │   ├── Services/
│   │   ├── Utilities/
│   │   ├── PCCCImageTool.csproj
│   │   └── README.md
│   └── Example/                    # Example client application
│       ├── Program.cs
│       ├── Example.csproj
│       └── README.md
└── PCCCComm.sln                     # Visual Studio solution
```

---

## Features

### PCCCComm Library
- **DF1 full‑duplex** serial framing (DLE stuffing, CRC-16/BCC, ACK/NAK, ENQ)
- **EtherNet/IP (EIP)** transport over TCP (CIP Unconnected Send, Execute PCCC)
- Read/write any data type: integers, floats, bits, strings, timers, counters
- Switch processor between RUN and PROGRAM modes
- Auto‑detect DF1 communication settings (`DetectCommSettings()`)
- Retrieve data file directory (`GetDataMemory()`)
- Upload/download complete program files (SLC style)
- Support for SLC 5/03, MicroLogix 1500, and many other PCCC‑compatible PLCs

### PCCCEmulator (Standalone Tool)
- Emulates an SLC 5/03 (processor type `0x49`) with DF1 and EIP interfaces
- Loads real PLC program from embedded .bin resource (converted from APS .ACH archive)
- Full DF1 link layer: ACK/NAK, ENQ, checksum
- Full EtherNet/IP server: TCP port 44818, UDP broadcast ListIdentity, Forward Open/Close, Connected/Unconnected Send
- In‑memory file system with pre‑defined data files (O0, I1, S2, B3, N7, F8, T4, C5, R6, and additional files up to file 31)
- Responds to Get Status (CMD 0x06 FNC 0x03) with realistic 24‑byte payload
- Handles Protected Typed Logical Read/Write (0xA1, 0xA2, 0xAA, 0xAB)
- Configurable node ID, checksum, baud rate, parity via command line
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
- Supports SLC 5/01–5/05 and MicroLogix 1000/1500
- Automatic PLC detection and descriptive filename generation
- Progress indication during transfer

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

### 1. Using the PCCCComm Library (DF1 serial)

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

### 2. Using the PCCCComm Library (EtherNet/IP)

```csharp
using PCCCComm;

var comm = new PCCCComm("192.168.1.10", 44818); // EIP transport
comm.OpenComms();

string value = comm.ReadAny("N7:0");
Console.WriteLine(value);

comm.CloseComms();
```

### 3. Running the Emulator (DF1)

```bash
dotnet run --project src/PCCCEmulator -- COM2 --baud 19200 --checksum crc
```

### 4. Running the Emulator (EtherNet/IP)

```bash
dotnet run --project src/PCCCEmulator -- --mode eip --port 44818
```

### 5. Running the Example Client

```bash
dotnet run --project src/Example -- COM1 --target 1 --checksum crc
```

### 6. Testing Emulator + Client Together (DF1)

- Create a virtual serial pair (e.g. `COM1` ↔ `COM2`).
- Start emulator on `COM2` and client on `COM1`.

### 7. Testing Emulator + Client Together (EIP)

- Start emulator: `dotnet run --project src/PCCCEmulator -- --mode eip`
- Start example client (if extended to EIP) or use any EIP client (RSLinx, libplctag, pycomm3).

### 8. Running the GUI Tool (PCCCImageTool)

```bash
dotnet run --project src/PCCCImageTool
```

---

## Protocol Reference

The implementation follows **Allen‑Bradley Publication 1770‑6.5.16** (DF1 Protocol and Command Set) and **ODVA EtherNet/IP Specification** (Volumes 1 & 2). Supported PCCC commands include:

| Command | Description |
|---------|-------------|
| `0x06` (Get Status) | Read processor type, mode, diagnostics |
| `0x0F` (Protected Typed Logical Read/Write) | Read/write data files (0xA1, 0xA2, 0xAA, 0xAB) |
| `0x01` (Reset) | Reset communication |
| `0x0B` (Set Variables) | RSLinx auto‑configure |
| `0x0A` (Diagnostic Counters) | Read modem and packet statistics |
| `0x67` (Read Modified Data) | Simplified read |
| `0x0F` (Execute Command List) | Multi‑function commands (mode change, I/O config, upload/download) |

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
| `EIP connection timeout` | Check firewall (TCP 44818), verify emulator or PLC is reachable, and that `--mode eip` is used. |

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

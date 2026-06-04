# DF1Comm – DF1 Protocol Library for .NET (C# Port)

[![License](https://img.shields.io/badge/License-GPLv3-blue.svg)](https://www.gnu.org/licenses/gpl-3.0)
[![.NET](https://img.shields.io/badge/.NET-8.0-purple)](https://dotnet.microsoft.com/)

**DF1Comm** is a complete, self‑contained C# implementation of the **Allen‑Bradley DF1 full‑duplex protocol** for .NET.  

It is a **port** of the original **DF1Comm.vb** written by **Archie Jacobs of Manufacturing Automation LLC** – a proven, widely used DF1 implementation. This C# version preserves the original logic while adding:

- A reusable **DF1 communication library** (`DF1Comm`)
- A **standalone PCCC emulator** (`PCCCEmulator`) for testing without real PLC hardware
- An **example client** that demonstrates all major library features
- A **desktop GUI tool** (`DF1ProgramTool`) for upload/download/compare PLC programs

All components target .NET 8 and are licensed under GNU General Public License v3.0 or later (GPLv3+).

---

## Repository Structure

```
DF1Comm/
├── LICENSE                         (GNU GPL v3.0)
├── README.md                       (this file)
├── src/
│   ├── DF1Comm/                    # Core library (C# port of DF1Comm.vb)
│   │   ├── CheckSumOptions.cs
│   │   ├── DF1Comm.cs
│   │   ├── DF1Exception.cs
│   │   ├── Models.cs
│   │   ├── Core/
│   │   │   ├── AddressParser.cs
│   │   │   ├── DataLink.cs
│   │   │   ├── MessageDecoder.cs
│   │   │   ├── PacketBuilder.cs
│   │   │   ├── SerialPortWrapper.cs
│   │   │   └── StringConverter.cs
│   │   └── DF1Comm.csproj
│   ├── PCCCEmulator/                # PCCC emulator (standalone)
│   │   ├── DF1Transport.cs
│   │   ├── MessageDecoder.cs
│   │   ├── EIPTransport.cs
│   │   ├── EIPClient.cs
│   │   ├── ILinkTransport.cs
│   │   ├── PlcMemory.cs
│   │   ├── PCCCEmulator.cs
│   │   ├── Program.cs
│   │   ├── PCCCEmulator.csproj
│   │   └── README.md
│   ├── DF1ProgramTool/               # Desktop GUI for upload/download
│   │   ├── Views/
│   │   ├── ViewModels/
│   │   ├── Models/
│   │   ├── Services/
│   │   ├── Utilities/
│   │   ├── DF1ProgramTool.csproj
│   │   └── README.md
│   └── Example/                    # Example client application
│       ├── Program.cs
│       ├── Example.csproj
│       └── README.md
└── DF1Comm.sln                     # Visual Studio solution
```

---

## Features

### DF1Comm Library (C# Port)
- Full‑duplex DF1 framing (DLE STX / DLE ETX with DLE stuffing)
- **BCC** and **CRC‑16** (calculation as per AB specification) checksum support
- Read/write any data type: **integers, floats, bits, strings, timers, counters**
- Switch processor between **RUN** and **PROGRAM** modes
- Auto‑detect communication settings (baud, parity, checksum) via `DetectCommSettings()`
- Retrieve data file directory (`GetDataMemory()`)
- Upload/download complete program files (SLC style)
- Support for **SLC 5/03**, **MicroLogix 1500**, and many other DF1‑compatible PLCs

### PCCCEmulator (Standalone Tool)
- Emulates an **SLC 5/03** DF1 port (processor type `0x49`)
- **Loads real PLC program** from embedded .bin resource (converted from APS .ACH archive)
- Implements the full DF1 link layer: ACK/NAK, ENQ handling, checksum validation
- Implements EtherNet/IP support: TCP port 44818, UDP broadcast ListIdentity
- In‑memory file system with pre‑defined data files (O0, I1, S2, B3, N7, F8, T4, C5, R6, and additional B/N files up to file 31)
- Responds to **Get Status** (CMD 0x06 FNC 0x03) with realistic 24‑byte payload
- Handles **Protected Typed Logical Read/Write** (0xA1, 0xA2, 0xAA, 0xAB)
- Configurable node ID, checksum, baud rate, parity via command line
- Console hex logging for debugging

### Example Client
- Demo sequence: reads processor type, data files, and specific addresses; writes integers, floats, and bits; toggles RUN/PROGRAM mode
- **Interactive CLI** (`DF1>` prompt) with read, write, writestring, sendhex, mode, stats, and more
- **Communication statistics** – total requests, successes, timeouts, NAKs, other errors, error rate
- **Stress test mode** – continuous read loop with configurable iteration count (`--stress-test [n]`)
- Can be used with **real PLC** or **PCCCEmulator** over a virtual serial pair or ethernet

### DF1ProgramTool (Desktop GUI)
- Cross‑platform GUI built with Avalonia UI
- Upload entire PLC program to a binary file
- Download previously saved program back to the PLC
- Compares a backup file against current PLC program
- Supports SLC 5/01–5/05 and MicroLogix 1000/1500
- Automatic PLC detection and descriptive filename generation
- Progress indication during transfer

---

## Attribution

This C# library is a **direct port** of the original **DF1Comm.vb** written by **Archie Jacobs, Manufacturing Automation LLC**.  

The original Visual Basic code has been faithfully translated to C#, preserving the DF1 logic, error handling, and timing behaviour. All enhancements (emulator, example client, modern .NET packaging) are built on top of that core.

We thank Archie Jacobs for providing a robust, well‑tested DF1 implementation to the industrial automation community.

---

## Requirements

- **.NET 8 SDK** or later
- Windows / Linux / macOS (serial port support required)
- For testing without hardware: a **virtual serial port or ethernet emulator** (e.g. com0com on Windows, `socat` on Linux)

---

## Build

Clone the repository and build the whole solution:

```bash
git clone https://github.com/kumajaya/DF1Comm.git
cd DF1Comm
dotnet build -c Release DF1Comm.sln
```

Individual projects can also be built separately:

```bash
dotnet build -c Release src/DF1Comm/DF1Comm.csproj
dotnet build -c Release src/PCCCEmulator/PCCCEmulator.csproj
dotnet build -c Release src/Example/Example.csproj
```

---

## Usage

### 1. Using the DF1Comm Library

```csharp
using DF1Comm;

var df1 = new DF1Comm("COM1", 19200, Parity.None)
{
    TargetNode = 1,
    MyNode = 0,
    CheckSum = CheckSumOptions.Crc
};

df1.OpenComms();

// Read processor type
int procType = df1.GetProcessorType();
Console.WriteLine($"Processor: 0x{procType:X2}");

// Read an integer from N7:0
string value = df1.ReadAny("N7:0");
Console.WriteLine($"N7:0 = {value}");

// Write a float to F8:1
df1.WriteData("F8:1", 3.14159f);

// Set RUN mode
df1.SetRunMode();

df1.CloseComms();
```

### 2. Running the Emulator

```bash
dotnet run --project src/PCCCEmulator -- COM2 --baud 19200 --checksum crc
```

See `src/PCCCEmulator/README.md` for full emulator documentation.

### 3. Running the Example Client

```bash
dotnet run --project src/Example -- COM1 --target 1 --checksum crc
```

The client runs a demo sequence, prints communication statistics, then enters an interactive CLI (`DF1>` prompt). Use `--interactive-only` to skip the demo, `--no-interactive` to skip the CLI, or `--stress-test [n]` for continuous load testing.

See `src/Example/README.md` for full client documentation.

### 4. Testing Emulator + Client Together

1. Create a virtual serial pair (e.g. `COM1` ↔ `COM2`).
2. Start emulator on `COM2`:
   ```bash
   dotnet run --project src/PCCCEmulator -- COM2 --checksum crc
   ```
3. In another terminal, start the example client on `COM1`:
   ```bash
   dotnet run --project src/Example -- COM1 --checksum crc
   ```

### 5. Running the GUI Tool (DF1ProgramTool)

```bash
dotnet run --project src/DF1ProgramTool
```

The tool presents a graphical interface where you can select the COM port, baud rate, parity, and node ID. After connecting, the processor type is automatically detected. Upload/download buttons are enabled only for supported PLC families (SLC and MicroLogix).  
See `src/DF1ProgramTool/README.md` for full documentation.

### 6. Testing Emulator + DF1ProgramTool Together

1. Create a virtual serial pair (e.g. `COM1` ↔ `COM2`).
2. Start emulator on `COM2`:
   ```bash
   dotnet run --project src/PCCCEmulator -- COM2 --checksum crc
   ```
3. Start DF1ProgramTool and connect to `COM1`.
4. Upload from the emulator, then download – the emulator will respond correctly.

---

## Protocol Reference

The implementation follows **Allen‑Bradley Publication 1770‑6.5.16** (DF1 Protocol and Command Set). Supported commands include:

| Command | Description |
|---------|-------------|
| `0x06` (Get Status) | Read processor type, mode, diagnostics |
| `0x0F` (Protected Typed Logical Read/Write) | Read/write data files (0xA1, 0xA2, 0xAA, 0xAB) |
| `0x01` (Reset) | Reset communication |
| `0x0B` (Set Variables) | Configure communication parameters (RSLinx auto‑configure) |
| `0x0A` (Diagnostic Counters) | Read modem and packet statistics |
| `0x67` (Read Modified Data) | Simplified read variant |
| `0x0F` (Execute Command List) | Multi‑function commands (mode change, I/O config, upload/download) |

Checksum modes as per AB specification:
- **BCC**: uses two's complement of sum.
- **CRC‑16**: Initial value `0x0000`, polynomial `0xA001`, ETX byte `0x03` included.

---

## Troubleshooting

| Issue | Likely solution |
|-------|------------------|
| `No response, Check COM Settings` | Verify port, baud rate, parity, and that the target is powered and connected. |
| `Checksum mismatch` | Ensure both sides use the same checksum mode (`--checksum crc` or `bcc`). |
| `Illegal Command or Format` | The target may not support the addressed file/element. Check file numbers and element bounds. |
| `Processor is in Program mode` | Normal – writes may be restricted. Use `SetRunMode()` to change. |
| `Port busy` | Only one application can open a COM port at a time. Close other programs (RSLinx, etc.). |
| `RSLinx does not see memory` | Enable verbose logging in the emulator and verify `Read File 0` requests are answered correctly. |

---

## Contributing

1. Fork the repository.
2. Create a feature branch (`git checkout -b feature/amazing-feature`).
3. Commit your changes (`git commit -m 'Add amazing feature'`).
4. Push to the branch (`git push origin feature/amazing-feature`).
5. Open a Pull Request.

Please keep all code **self‑contained** (avoid external dependencies except `System.IO.Ports`). Add unit tests when possible.

---

## License

**DF1Comm – C# port of DF1Comm.vb**  
Copyright (c) 2026 Ketut Kumajaya

The original **DF1Comm.vb** (by Archie Jacobs, Manufacturing Automation LLC) was released under the  
**GNU General Public License, version 2 or any later version**.

This C# port and the Example Client are **derived works** of the original VB code.  
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

- **Archie Jacobs, Manufacturing Automation LLC** – for the original VB DF1Comm implementation that made this port possible.
- **Allen‑Bradley / Rockwell Automation** – for the DF1 protocol specification (Publication 1770‑6.5.16).

---

## Related Projects

- [PCCCEmulator](src/PCCCEmulator/README.md) – standalone emulator
- [Example Client](src/Example/README.md) – usage demonstration
- [DF1ProgramTool](src/DF1ProgramTool/README.md) – desktop GUI for upload/download

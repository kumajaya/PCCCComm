# PCCC Protocol Subset – Implementation Guide

This document lists all PCCC (Programmable Controller Communications Command) commands as defined in Allen‑Bradley Publication 1770‑6.5.16, and indicates which commands are currently implemented in the **PCCCComm** library.

---

## Complete PCCC Command Set

The table below follows the naming and grouping conventions of the official AB specification.  
✅ = fully implemented and tested in PCCCComm  
⚠️ = partially implemented (see notes)  
❌ = not yet implemented (planned for future releases or not applicable)

| # | Command | CMD | FNC | Implemented | Notes |
|---|---------|-----|-----|-------------|-------|
| 1 | Apply Port Configuration | 0x0F | 0x8F | ✅ | |
| 2 | Bit Write (Write Bit) | 0x0F | 0xAB | ⚠️ | SLC only; PLC‑5 uses read‑modify‑write workaround |
| 3 | Change Mode (SLC 5/03+) | 0x0F | 0x80 | ✅ | |
| 4 | Change Mode (MicroLogix) | 0x0F | 0x3A | ✅ | Also used for PLC‑5 Set CPU Mode |
| 5 | Close File | 0x0F | 0x82 | ✅ | |
| 6 | Diagnostic Status | 0x06 | 0x03 | ✅ | |
| 7 | Disable Forces | 0x0F | 0x41 | ✅ | |
| 8 | Disable Outputs | 0x07 | 0x00 | ❌ | |
| 9 | Download All Request (Download) | 0x0F | 0x50 | ✅ | |
| 10 | Download Completed | 0x0F | 0x52 | ✅ | |
| 11 | Download Request (Download Privilege) | 0x0F | 0x05 | ❌ | |
| 12 | Echo | 0x0F | 0x00 | ✅ | |
| 13 | Enable Outputs | 0x07 | 0x01 | ❌ | |
| 14 | Enable PLC Scanning | 0x07 | 0x03 | ❌ | |
| 15 | Enter Download Mode | 0x07 | 0x04 | ❌ | |
| 16 | Enter Upload Mode | 0x07 | 0x06 | ❌ | |
| 17 | Exit Download/Upload Mode | 0x07 | 0x05 | ❌ | |
| 18 | File Read | 0x0F | 0xA7 | ✅ | |
| 19 | File Write | 0x0F | 0xAF | ✅ | |
| 20 | Get Edit Resource | 0x0F | 0x11 | ✅ | |
| 21 | Initialize Memory | 0x0F | 0x57 | ✅ | |
| 22 | Modify PLC‑2 Compatibility File | 0x0F | 0x5E | ❌ | |
| 23 | Open File | 0x0F | 0x81 | ✅ | |
| 24 | Physical Read (PLC‑2/1774‑PLC) | 0x04 | – | ❌ | Legacy |
| 25 | Physical Read (PLC‑3/5) | 0x0F | 0x17 | ❌ | Legacy |
| 26 | Physical Write | 0x0F | 0x08 / 0x18 | ❌ | Legacy |
| 27 | Protected Bit Write | 0x02 | – | ❌ | Legacy |
| 28 | Protected Typed File Read | 0x0F | 0xA7 | ✅ | Same as File Read |
| 29 | Protected Typed File Write | 0x0F | 0xAF | ✅ | Same as File Write |
| 30 | Protected Typed Logical Read (3 Address Fields) | 0x0F | 0xA2 | ✅ | |
| 31 | Protected Typed Logical Write (3 Address Fields) | 0x0F | 0xAA | ✅ | |
| 32 | Protected Write | 0x00 | – | ❌ | Legacy |
| 33 | Read Bytes Physical | 0x0F | 0x17 | ❌ | Legacy |
| 34 | Read Diagnostic Counters | 0x06 | 0x01 | ✅ | |
| 35 | Read Link Parameters | 0x06 | 0x09 | ✅ | |
| 36 | Read‑Modify‑Write | 0x0F | 0x26 | ⚠️ | SLC only; PLC‑5 requires logical binary addressing |
| 37 | Read‑Modify‑Write N | 0x0F | 0x79 | ❌ | |
| 38 | Read Section Size | 0x0F | 0x29 | ❌ | |
| 39 | Reset Diagnostic Counters | 0x06 | 0x07 | ✅ | |
| 40 | Restart Request (Restart) | 0x0F | 0x0A | ❌ | |
| 41 | Return Edit Resource | 0x0F | 0x12 | ✅ | |
| 42 | Set CPU Mode | 0x0F | 0x3A / 0x80 | ✅ | Same as Change Mode |
| 43 | Set Data Table Size | 0x06 | 0x08 | ❌ | |
| 44 | Set ENQs | 0x06 | 0x06 | ❌ | |
| 45 | Set Link Parameters | 0x06 | 0x0A | ✅ | |
| 46 | Set NAKs | 0x06 | 0x05 | ❌ | |
| 47 | Set Timeout | 0x06 | 0x04 | ❌ | |
| 48 | Set Variables | 0x06 | 0x02 | ❌ | |
| 49 | Shutdown | 0x0F | 0x07 | ❌ | |
| 50 | Typed Read (Read Block) | 0x0F | 0x68 | ✅ | For PLC‑5 |
| 51 | Typed Write (Write Block) | 0x0F | 0x67 | ✅ | For PLC‑5 |
| 52 | Unprotected Bit Write | 0x05 | – | ❌ | Legacy |
| 53 | Unprotected Read | 0x01 | – | ❌ | Legacy |
| 54 | Unprotected Write | 0x08 | – | ❌ | Legacy |
| 55 | Upload All Request (Upload) | 0x0F | 0x53 | ✅ | |
| 56 | Upload Completed | 0x0F | 0x55 | ✅ | |
| 57 | Upload | 0x0F | 0x06 | ❌ | |
| 58 | Word Range Read (Read Block) | 0x0F | 0x01 | ❌ | Legacy |
| 59 | Word Range Write (Write Block) | 0x0F | 0x00 | ❌ | Legacy |
| 60 | Write Bytes Physical (Physical Write) | 0x0F | 0x18 | ❌ | Legacy |

> **Notes:**  
> - Commands marked with ✅ are fully implemented, tested, and ready for use.  
> - Commands marked with ⚠️ have limitations or are implemented via workarounds (see details below).  
> - Commands marked with ❌ are either planned for future releases or are specific to legacy PLC families (PLC‑2, 1774‑PLC, early PLC‑3) which are not the primary target of PCCCComm.  
> - All read/write operations for **SLC 500, MicroLogix, and PLC‑5** are supported. For PLC‑5, data access uses **Typed Read (0x68)** and **Typed Write (0x67)** with logical binary addressing.  
> - The “Legacy” note indicates commands that are rarely used in modern applications and may be added upon request.

---

## Supported PCCC Subset – Detailed Feature List

The following sections describe the implemented commands in more detail, grouped by functionality.

### Mode Control

| Method | CMD | FNC | Description |
|--------|-----|-----|-------------|
| `SetRunMode()` | 0x0F | 0x80 (SLC) / 0x3A (PLC‑5) | Places processor in RUN mode |
| `SetProgramMode()` | 0x0F | 0x80 (SLC) / 0x3A (PLC‑5) | Places processor in PROGRAM mode |
| `SetCpuMode(byte)` | 0x0F | 0x80 / 0x3A | Generic CPU mode change |
| `GetRunMode()` | 0x06 | 0x03 | Returns 1 if in RUN mode, else 0 |

### Forces

| Method | CMD | FNC | Description |
|--------|-----|-----|-------------|
| `DisableForces()` | 0x0F | 0x41 | Disables all forces (SLC and PLC‑5) |
| `EnableForces()` | 0x0F | 0x42 | Enables forces (SLC only; PLC‑5 throws `NotSupportedException`) |
| `ClearForces()` | 0x0F | 0x43 | Clears forces (SLC only; PLC‑5 throws `NotSupportedException`) |

### Read/Write Data

#### SLC / MicroLogix (Protected Typed Logical)

| Method | CMD | FNC | Description |
|--------|-----|-----|-------------|
| `ReadAny()` | 0x0F | 0xA1 / 0xA2 | Read any data type (int, float, string, timer, counter, bit) |
| `ReadInt()` | 0x0F | 0xA1 / 0xA2 | Read integer array |
| `WriteData(int)` | 0x0F | 0xAA | Write single integer |
| `WriteData(int[])` | 0x0F | 0xAA | Write integer array |
| `WriteData(float)` | 0x0F | 0xAA | Write single float |
| `WriteData(float[])` | 0x0F | 0xAA | Write float array |
| `WriteData(string)` | 0x0F | 0xAA | Write string (ST file or word‑packed) |
| `ReadModifyWrite()` | 0x0F | 0x26 | Atomic read‑modify‑write (bitwise AND/OR) |

#### PLC‑5 (Typed Read / Typed Write)

| Method | CMD | FNC | Description |
|--------|-----|-----|-------------|
| `ReadAny()` | 0x0F | 0x68 | Read any data type using logical binary addressing |
| `ReadInt()` | 0x0F | 0x68 | Read integer array |
| `WriteData(int)` | 0x0F | 0x67 | Write single integer |
| `WriteData(int[])` | 0x0F | 0x67 | Write integer array |
| `WriteData(float)` | 0x0F | 0x67 | Write single float |
| `WriteData(float[])` | 0x0F | 0x67 | Write float array |
| `WriteData(string)` | 0x0F | 0x67 | Write string |
| `ReadModifyWrite()` | – | – | Not implemented (requires logical binary addressing) |

**Important:**  
- Bit-level writes on PLC‑5 are implemented via **read‑modify‑write workaround** (read whole word, modify bit, write back). This requires two transactions and is **not atomic**, but sufficient for most applications.  
- For multi‑bit writes or atomic operations, use `WriteData` on the word address directly.

### File‑Based Upload/Download (SLC 5/03+ and ML1100/1200/1500)

| Method | CMD | FNC | Description |
|--------|-----|-----|-------------|
| `UploadAllRequest()` | 0x0F | 0x53 | Enter upload mode, get segment info |
| `UploadCompleted()` | 0x0F | 0x55 | Exit upload mode |
| `DownloadAllRequest()` | 0x0F | 0x50 | Enter download mode |
| `DownloadCompleted()` | 0x0F | 0x52 | Exit download mode |
| `GetEditResource()` | 0x0F | 0x11 | Secure sole access |
| `ReturnEditResource()` | 0x0F | 0x12 | Release sole access |
| `OpenFile()` | 0x0F | 0x81 | Open file, return tag handle |
| `CloseFile()` | 0x0F | 0x82 | Close file |
| `FileRead()` | 0x0F | 0xA7 | Read from open file (with chunking) |
| `FileWrite()` | 0x0F | 0xAF | Write to open file (with chunking) |
| `UploadProgramData()` | – | – | High‑level upload (auto‑selects file‑based or physical) |
| `DownloadProgramData()` | – | – | High‑level download (auto‑selects) |

### I/O Configuration

| Method | CMD | FNC | Description |
|--------|-----|-----|-------------|
| `GetSlotCount()` | 0x0F | 0xA2 | Returns number of chassis slots (SLC only) |
| `GetIOConfig()` | 0x0F | 0xA2 | Returns I/O configuration per slot (SLC only) |

> For PLC‑5, I/O configuration commands are not yet implemented.

### Data Memory

| Method | CMD | FNC | Description |
|--------|-----|-----|-------------|
| `GetDataMemory()` | 0x0F | 0x94 | Returns list of data files (SLC 5/03+ only) |
| `GetML1500DataMemory()` | 0x0F | 0x94 | ML1500‑specific data file list |

> For PLC‑5, `GetDataMemory()` is not yet implemented.

### Diagnostics & Testing

| Method | CMD | FNC | Description |
|--------|-----|-----|-------------|
| `GetProcessorType()` | 0x06 | 0x03 | Returns processor type code |
| `GetDiagnosticStatusRaw()` | 0x06 | 0x03 | Returns raw diagnostic status bytes |
| `ReadDiagnosticCounters()` | 0x06 | 0x01 | Read diagnostic counters (packet stats, errors) |
| `ResetDiagnosticCounters()` | 0x06 | 0x07 | Reset diagnostic counters |
| `ReadLinkParameters()` | 0x06 | 0x09 | Read DH485 max node address |
| `SetLinkParameters(byte)` | 0x06 | 0x0A | Set DH485 max node address |
| `Echo(byte[])` | 0x0F | 0x00 | Echo test (returns same data) |
| `InitializeMemory()` | 0x0F | 0x57 | Reset processor memory (destructive) |
| `ApplyPortConfiguration()` | 0x0F | 0x8F | Apply stored port configuration |

### Legacy Physical‑Based Upload/Download (SLC 5/01, 5/02, ML1000)

The following methods are used internally when `SupportsFileBasedTransfer()` returns `false`:

- `UploadProgramDataPhysicalBased()` – reads program via physical reads
- `DownloadProgramDataPhysicalBased()` – writes program via physical writes

---

## Adding a New PCCC Subset Command

All PCCC commands follow the same pattern. To add a new command, perform these **5 steps**.

### Step 1: Add Constant in `PCCCConstants.cs`

If the command uses `CMD = 0x0F` (Protected Write), add inside `public static class Fnc`:

```csharp
/// <summary>Description (0xXX).</summary>
public const byte NewCommand = 0xXX;
```

If it uses a different `CMD`, add inside `public static class Cmd`:

```csharp
/// <summary>Description (0xXX).</summary>
public const byte NewCommand = 0xXX;
```

### Step 2: Add Factory Method in `PCCCMessage.cs`

**No data payload:**
```csharp
public static PCCCMessage CreateNewCommandRequest(ushort tns, byte myNode, byte targetNode)
{
    return new PCCCMessage(targetNode, myNode, Cmd.ProtectedWrite, 0, tns,
        Fnc.NewCommand, Array.Empty<byte>());
}
```

**With data payload:**
```csharp
public static PCCCMessage CreateNewCommandRequest(byte[] data, ushort tns, byte myNode, byte targetNode)
{
    return new PCCCMessage(targetNode, myNode, Cmd.ProtectedWrite, 0, tns,
        Fnc.NewCommand, data);
}
```

### Step 3: Add Method in `PCCCProtocol.cs`

**Void (no return data):**
```csharp
public void NewCommand(byte myNode, byte targetNode)
{
    var req = PCCCMessage.CreateNewCommandRequest(0, myNode, targetNode);
    SendRequest(req, out int sts);
    if (sts != Sts.Success)
        throw new PCCCException($"NewCommand failed: {PCCCErrors.DecodeStatus(sts)}");
}
```

**With return data:**
```csharp
public byte[] NewCommand(byte myNode, byte targetNode)
{
    var req = PCCCMessage.CreateNewCommandRequest(0, myNode, targetNode);
    var reply = SendRequest(req, out int sts);
    if (sts != Sts.Success || reply?.Data == null)
        throw new PCCCException($"NewCommand failed: {PCCCErrors.DecodeStatus(sts)}");
    return reply.Data;
}
```

### Step 4: Add Declaration in `IPlcHandler.cs`

```csharp
/// <summary>Description.</summary>
void NewCommand();   // or appropriate return type
```

### Step 5: Implement Wrapper in the Appropriate Handler

- For SLC/MicroLogix: add to `SlcHandler.cs`
- For PLC‑5: add to `Plc5Handler.cs`

```csharp
public void NewCommand()
{
    _protocol.NewCommand((byte)MyNode, (byte)TargetNode);
}
```

---

## Notes for Complex Commands

### Commands Requiring Chunking (e.g., `FileRead` / `FileWrite`)

- Use `FileReadWithChunking` and `FileWriteWithChunking` helpers in `SlcHandler`.
- Convert **byte offset** to **word offset** (`offset / 2`) before calling `_protocol.FileRead`/`FileWrite`.
- Respect `Df1Limits.MaxReadPayloadBytes` (236) and `MaxWritePayloadBytes` (164).

### Extended Addressing (Element >= 255)

The existing `EncodeReadBody` / `EncodeWriteBody` methods in `PCCCMessage` already handle extended addressing (encoding `0xFF` + two bytes). Reuse them when adding new read/write commands.

### PLC‑5 Logical Binary Addressing

For PLC‑5, the `Plc5Handler` provides `EncodePlc5LogicalAddress()` which encodes a logical binary address according to 1770‑6.5.16 Chapter 13. Use this helper when implementing new read/write commands for PLC‑5.

---

## PCCCEmulator – Testing and Simulation

The library includes a **PCCCEmulator** that can simulate both SLC and PLC‑5 processors. The emulator supports:

- DF1 full‑duplex (serial), DF1 half‑duplex slave, EtherNet/IP (EIP/PCCC)
- Full read/write access to N, B, F, T, C, ST, L files with automatic chunking
- Mode control (Run/Program) via Set CPU Mode (FNC 0x3A)
- Force management (Disable only for PLC‑5)
- Diagnostic counters and link parameters
- Echo, InitializeMemory

To run the emulator in PLC‑5 mode:

```bash
dotnet run --project src/PCCCEmulator -- COM2 --family plc5
```

To run in default SLC mode:

```bash
dotnet run --project src/PCCCEmulator -- COM2
```

For a complete self‑test of the library against the emulator, use the `Example` project:

```bash
dotnet run --project src/Example -- COM1
```

---

## Reference

- **Allen‑Bradley Publication 1770‑6.5.16** – DF1 Protocol and Command Set
- **ODVA EtherNet/IP Specification** – Volumes 1 & 2

For DF1 framing, checksum, and DLE stuffing, see `MessageDecoder.cs` and transports in `../Core/`.

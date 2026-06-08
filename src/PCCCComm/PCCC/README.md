# PCCC Protocol Subset – Implementation Guide

This folder contains the core PCCC (Programmable Controller Communications Command) implementation for the **PCCCComm** library.

---

## Supported PCCC Subset

The following PCCC commands are fully implemented and tested.

### Mode Control

| Method | CMD | FNC | Description |
|--------|-----|-----|-------------|
| `SetRunMode()` | 0x0F | 0x80 (SLC) / 0x3A (ML) | Places processor in RUN mode |
| `SetProgramMode()` | 0x0F | 0x80 (SLC) / 0x3A (ML) | Places processor in PROGRAM mode |
| `SetCpuMode(byte)` | 0x0F | 0x80 / 0x3A | Generic CPU mode change |
| `GetRunMode()` | 0x06 | 0x03 | Returns 1 if in RUN mode, else 0 |

### Forces

| Method | CMD | FNC | Description |
|--------|-----|-----|-------------|
| `DisableForces()` | 0x0F | 0x41 | Disables all forces |
| `EnableForces()` | 0x0F | 0x42 | Enables forces (if any defined) |
| `ClearForces()` | 0x0F | 0x43 | Clears all force entries |

### Read/Write Data (Protected Typed Logical)

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
| `GetSlotCount()` | 0x0F | 0xA2 | Returns number of chassis slots |
| `GetIOConfig()` | 0x0F | 0xA2 | Returns I/O configuration per slot |

### Data Memory

| Method | CMD | FNC | Description |
|--------|-----|-----|-------------|
| `GetDataMemory()` | 0x0F | 0x94 | Returns list of data files (SLC 5/03+) |
| `GetML1500DataMemory()` | 0x0F | 0x94 | ML1500‑specific data file list |

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

### Step 5: Implement Wrapper in `SlcHandler.cs`

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

---

## Reference

- **Allen‑Bradley Publication 1770‑6.5.16** – DF1 Protocol and Command Set
- **ODVA EtherNet/IP Specification** – Volumes 1 & 2

For DF1 framing, checksum, and DLE stuffing, see `MessageDecoder.cs` and transports in `../Core/`.

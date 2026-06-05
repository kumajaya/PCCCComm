# PCCCComm Example Client

**Purpose**  
Enhanced PCCC client for testing `PCCCComm` against a real PLC or the PCCCEmulator. Supports a demo sequence, interactive CLI, communication statistics, and stress testing.

> **⚠️ CAUTION – REAL PLC HAZARD**  
> This example client **writes data** to the connected PLC (N7, F8, B3, and mode switching).  
> Running the demo on a **real PLC** will modify its memory and may affect machine operation.  
> **Only use with a real PLC if you fully understand the consequences.**  
> For safe testing, use the [PCCCEmulator](../PCCCEmulator) instead.

## Features
- Reads processor type (Get Status, CMD 0x06 FNC 0x03)
- Reads/writes integers (`N7`, `O0`, `I1`, `B3`)
- Reads/writes floating‑point values (`F8`)
- Bit‑level write (`B3:0/0`, `/3`)
- Switches processor between **RUN** and **PROGRAM** mode
- Retrieves data file directory (`GetDataMemory()`)
- **Interactive CLI** (`DF1>` prompt) with read, write, writestring, sendhex, mode, stats, and more
- **Communication statistics** – total requests, successes, timeouts, NAKs, other errors, error rate
- **Stress test mode** – continuous read loop with configurable iteration count
- Configurable serial settings (port, baud, parity, node IDs, checksum)

![Example client](Images/Screenshots/Example.png)

*Example client stress test on PCCCEmulator*

## Requirements
- .NET 8 SDK or later
- `PCCCComm` library (referenced via project or DLL)
- A DF1 target – either:
  - A real SLC 5/03 or MicroLogix PLC with DF1 port
  - The **PCCCEmulator** (standalone emulator) connected via virtual serial pair

## Build

### With project reference (typical)
```bash
dotnet build -c Release Example.csproj
```

### If `PCCCComm` is a separate project in the same solution
Ensure the solution includes both `PCCCComm.csproj` and `Example.csproj` with a `ProjectReference`.

## Run

**Default** (COM1, 19200, no parity, target node 1, local node 0, CRC checksum):
```bash
dotnet run --project Example.csproj -- COM1
```

### Command line options
| Option | Description | Default |
|--------|-------------|---------|
| `[port]` | Serial port name | `COM1` |
| `--baud <n>` | Baud rate | `19200` |
| `--parity <none/odd/even>` | Parity mode | `none` |
| `--target <n>` | Target PLC node ID | `1` |
| `--mynode <n>` | Local/master node ID | `0` |
| `--checksum <crc/bcc>` | Checksum mode | `crc` |
| `--interactive-only` | Skip demo, go straight to interactive CLI | – |
| `--no-interactive` | Run demo only, then exit | – |
| `--stress-test [n]` | Run stress test; `n` = loop count (default: infinite) | – |
| `--help, -h` | Show usage | – |

### Linux-specific notes

On Linux, serial ports are typically named `/dev/ttyS0`, `/dev/ttyUSB0`, `/dev/ttyACM0`, etc.  
The example client accepts both formats: with or without the `/dev/` prefix.

```bash
# Both work:
dotnet run --project Example.csproj -- ttyUSB0
dotnet run --project Example.csproj -- /dev/ttyUSB0
```

If the specified port is not found, the client will display a list of available `tty` devices.

> **Note**: Ensure your user has read/write access to the serial device. Add yourself to the `dialout` group if needed:
> ```bash
> sudo usermod -a -G dialout $USER
> # Log out and back in for changes to take effect
> ```
```

### Example with PCCCEmulator (virtual pair)

**Windows** (using com0com):
1. Create a virtual COM pair, e.g. `COM1` ↔ `COM2`.
2. Start the emulator on `COM2`:
   ```bash
   dotnet run --project PCCCEmulator.csproj -- COM2 --checksum crc
   ```
3. In another terminal, run the example client on `COM1`:
   ```bash
   dotnet run --project Example.csproj -- COM1 --target 1 --checksum crc
   ```

**Linux** (using socat):
1. Create a virtual serial pair:
   ```bash
   socat -d -d pty,raw,echo=0,link=/dev/ttyV0 pty,raw,echo=0,link=/dev/ttyV1
   ```
2. Start the emulator on `/dev/ttyV0`:
   ```bash
   dotnet run --project ../PCCCEmulator/PCCCEmulator.csproj -- ttyV0 --checksum crc
   ```
3. In another terminal, run the example client on `/dev/ttyV1`:
   ```bash
   dotnet run --project Example.csproj -- ttyV1 --target 1 --checksum crc
   ```

### Stress test example
```bash
dotnet run --project Example.csproj -- COM1 --stress-test 500
```
Runs 500 continuous reads of `F8:0`, then prints communication statistics.

## Interactive CLI

When the demo completes (or with `--interactive-only`), the client enters an interactive prompt:

```
=== Interactive CLI Mode ===
Type 'help' for commands, 'exit' to quit.

DF1>
```

### Interactive commands
| Command | Description |
|---------|-------------|
| `read <addr> [count]` | Read one or more elements from address |
| `write <addr> <val...>` | Write one or more integers to address |
| `writestring <addr> <text>` | Write string to an ST file address |
| `sendhex <DST> <CMD> <FNC> [data...]` | Send raw DF1 command as hex bytes |
| `mode` | Show current PLC mode (RUN / PROGRAM) |
| `setrun` | Switch PLC to RUN mode |
| `setprog` | Switch PLC to PROGRAM mode |
| `type` | Show processor type code |
| `stats` | Show communication statistics |
| `resetstats` | Reset statistics counters |
| `exit` / `quit` | Exit interactive mode |
| `help` | Show command list |

#### `sendhex` detail
Sends a raw DF1 PDU. SRC and TNS are auto‑generated by the library; DST, CMD, and FNC are specified by the user.

```
DF1> sendhex 01 0F A1 02 11 89 00
```

## Expected output (successful run)

```
Connected on COM1
Baud=19200, Parity=None, Checksum=Crc
MyNode=0, TargetNode=1

Processor Type: 0x49

--- Read Operations (Demo) ---
O0:0 = 513
I1:0 = 0
B3:0 = 0
N7:0 = 123
F8:0 = 1.23
PLC is in RUN mode

--- Data Files ---
File 0: Type=1 Elements=...
File 1: Type=139 Elements=...
...

--- Write Operations (Demo) ---
Writing 999 to N7:1...
Writing 2.718 to F8:1...
Setting B3:0/0 = 1...
Setting B3:0/3 = 1...
Switching to PROGRAM mode...

--- Read Operations After Write (Demo) ---
N7:1 = 999
F8:1 = 2.718
B3:0 = 9
PLC is in PROGRAM mode

=== Communication Statistics ===
Total requests   : 14
Successful       : 14
Timeouts         : 0
NAK responses    : 0
Other errors     : 0
Error rate       : 0.00%
=================================

=== Interactive CLI Mode ===
Type 'help' for commands, 'exit' to quit.

DF1>
```

## Troubleshooting
| Issue | Solution |
|-------|----------|
| `No response, Check COM Settings` | Verify the COM port is correct, baud rate/parity match the target, and the target is powered on. |
| `Checksum mismatch` | Ensure `--checksum` matches the target's setting (both default to CRC). |
| `Illegal Command or Format` | The target may not support the addressed file/element. Check file numbers and element bounds. |
| `Processor is in Program mode` | Normal – some commands are restricted. Use `setrun` in the CLI or `SetRunMode()` to change. |
| `Access denied` | Some DF1 targets have command protection. Not supported by this example. |
| `PrefixAndSend method not found` | `sendhex` uses reflection on a private method. Ensure `PCCCComm` library version matches. |

## License
Same as the PCCCComm library.

## See also
- [PCCCEmulator](../PCCCEmulator) – standalone emulator for testing
- [PCCCComm Library Documentation](https://github.com/kumajaya/PCCCComm)

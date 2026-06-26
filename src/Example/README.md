# PCCCComm Example Client

**Purpose**  
Reference implementation and test client for the `PCCCComm` library. Demonstrates every major
library feature and verifies correct operation against a real PLC or the PCCCEmulator across all
four supported transports: DF1 full‑duplex, DF1 half‑duplex master (RS‑485), EtherNet/IP (EIP), and CSPv4.

> **⚠️ CAUTION – REAL PLC HAZARD**  
> The demo and self‑test suite **write data** to the connected PLC
> (N7:1–9, F8:1–7, B3:0–2, ST18:1–5, and mode switching).  
> Running them on a **real PLC** will modify its memory and may affect machine operation.  
> **Only connect to a real PLC if you fully understand the consequences.**  
> Use the [PCCCEmulator](../PCCCEmulator) for safe development and testing.

---

## Features

| Category | Feature |
|----------|---------|
| **Transport** | DF1 full‑duplex (RS‑232/USB), DF1 half‑duplex master (RS‑485), EtherNet/IP, and CSPv4 over TCP |
| **Data types** | Integer (N), Float (F), Binary/bit (B), Output (O), Input (I), Status (S), String (ST) |
| **Operations** | Single and multi‑element read/write, bit‑level write (FNC=0xAB), string read/write |
| **Diagnostics** | Processor type, run mode, data file directory |
| **Node management** | Target node verification on startup, runtime node switching (`settarget`), RS‑485 node scanner (`scannodes`) |
| **Monitoring** | Live address watch with delta detection (`watch`) |
| **Testing** | Exhaustive self‑test suite (`selftest`) — 26 cases (SLC/ML) or 24 cases (PLC‑5) across multiple groups |
| **Stress test** | Continuous read loop with throughput and error rate statistics |
| **Raw protocol** | Send arbitrary PCCC PDUs via `sendhex` (hex byte input) |

---

## Requirements

- .NET 8 SDK or later
- `PCCCComm` library (referenced via `ProjectReference` or compiled DLL)
- A target PLC or emulator:
  - **DF1 full‑duplex**: SLC 5/03–5/05 or MicroLogix with DF1 port, or PCCCEmulator via virtual serial pair
  - **DF1 half‑duplex master**: PCCCEmulator in `df1slave` mode, or a real PLC configured as DF1 slave on RS‑485
  - **EIP**: SLC 5/05, CompactLogix, MicroLogix 1100/1400, or PCCCEmulator in EIP mode
  - **CSPv4**: PLC-5E, SLC 5/05, SoftLogix 5, or a gateway such as the 1761-NET-ENI, or PCCCEmulator in CSP mode

---

## Build

```bash
dotnet build -c Release Example.csproj
```

If `PCCCComm` is a separate project in the same solution, ensure both `PCCCComm.csproj` and
`Example.csproj` are included with a `ProjectReference`.

---

## Run

### DF1 Full‑Duplex (default)

```bash
# COM1, 19200 baud, no parity, target node 1, CRC checksum
dotnet run --project Example.csproj -- COM1

# With explicit options
dotnet run --project Example.csproj -- COM1 --target 1 --baud 19200 --checksum crc
```

### DF1 Half‑Duplex Master (RS‑485)

```bash
# Auto RS-485 direction control (USB adapters with hardware auto-direction)
dotnet run --project Example.csproj -- COM2 --mode df1master --target 1

# Manual RTS direction control
dotnet run --project Example.csproj -- COM2 --mode df1master --target 1 --rs485-mode rts

# With echo suppression (virtual serial pair or full-duplex loopback)
dotnet run --project Example.csproj -- COM2 --mode df1master --target 1 --rs485-mode rts --echo-suppression
```

### EtherNet/IP

```bash
dotnet run --project Example.csproj -- --mode eip --host 192.168.1.10

# With custom port and timeout
dotnet run --project Example.csproj -- --mode eip --host 192.168.1.10 --eip-port 44818 --timeout 3000
```

### CSPv4

```bash
dotnet run --project Example.csproj -- --mode csp --host 192.168.1.10

# With custom port and timeout
dotnet run --project Example.csproj -- --mode csp --host 192.168.1.10 --csp-port 2222 --timeout 3000
```

### Command‑line options

| Option | Description | Default |
|--------|-------------|---------|
| `[port]` | Serial port name (DF1 modes) | `COM1` |
| `--mode <df1\|df1master\|eip\|csp>` | Transport mode | `df1` |
| `--baud <n>` | Baud rate (DF1) | `19200` |
| `--parity <none\|odd\|even>` | Parity (DF1) | `none` |
| `--target <n>` | Target PLC node address | `1` |
| `--mynode <n>` | Local / master node address | `0` |
| `--checksum <crc\|bcc>` | Checksum mode (DF1) | `crc` |
| `--rs485-mode <auto\|rts\|dtr>` | RS‑485 direction control (df1master) | `auto` |
| `--echo-suppression` | Discard self‑echoed bytes (df1master) | `false` |
| `--rs485-assert-delay <ms>` | Delay after RTS assert (df1master) | `1` |
| `--rs485-deassert-delay <ms>` | Delay before RTS deassert (df1master) | `5` |
| `--host <IP>` | PLC IP address (EIP, required) | — |
| `--eip-port <n>` | EIP TCP port | `44818` |
| `--csp-port <n>` | CSPv4 TCP port | `2222` |
| `--timeout <ms>` | EIP connection timeout | `5000` |
| `--lsap-control <hex>` | LSAP control byte for CSPv4 | `00` |
| `--interactive-only` | Skip demo, go straight to CLI | — |
| `--no-interactive` | Run demo only, then exit | — |
| `--stress-test [n]` | Stress test; `n` = iterations (0 = infinite) | — |
| `--scan-nodes [from] [to]` | Scan RS‑485 node range before CLI (default 1–31) | — |
| `--help, -h` | Show usage | — |

---

## Startup sequence

On every run the client:

1. Opens the serial port or TCP socket (`OpenComms()`).
2. **Probes the target node** with `GetProcessorType()`. If the node does not respond, the demo and
   stress test are skipped and an actionable error message with suggested fixes is printed. The
   interactive CLI is still offered so the user can run `scannodes` to find the correct node.
3. Runs the demo (unless `--interactive-only`).
4. Runs the node scan (if `--scan-nodes`).
5. Runs the stress test (if `--stress-test`).
6. Enters the interactive CLI (unless `--no-interactive`).

### Target node not 1?

If the PLC is not at node 1 (common on RS‑485 multi‑drop networks), use `--target N`:

```bash
dotnet run --project Example.csproj -- COM1 --mode df1master --target 3
```

Or discover the node address first, then switch at runtime without restarting:

```bash
dotnet run --project Example.csproj -- COM1 --mode df1master --interactive-only
```
```
Verifying target node 1... FAILED
  Suggestions:
    - Use 'scannodes' in the interactive CLI to discover active nodes.
    ...

PCCC> scannodes
  Node   3: FOUND  type=0x49  (SLC 5/03)

PCCC> settarget 3
Target node changed 1 → 3. Probing... OK  (type=0x49  SLC 5/03)

PCCC> read N7:0
Result: 0
```

---

## Setup with PCCCEmulator

### DF1 full‑duplex — virtual serial pair

**Windows** (com0com):
```bash
# 1. Create virtual pair COM1 ↔ COM2 in com0com setup utility.

# 2. Start emulator on COM2
dotnet run --project ../PCCCEmulator/PCCCEmulator.csproj -- COM2 --checksum crc

# 3. Run client on COM1
dotnet run --project Example.csproj -- COM1 --target 1 --checksum crc
```

**Linux** (socat):
```bash
# 1. Create virtual pair
socat -d -d pty,raw,echo=0,link=/dev/ttyV0 pty,raw,echo=0,link=/dev/ttyV1

# 2. Start emulator
dotnet run --project ../PCCCEmulator/PCCCEmulator.csproj -- ttyV0 --checksum crc

# 3. Run client
dotnet run --project Example.csproj -- ttyV1 --target 1 --checksum crc
```

### DF1 half‑duplex master ↔ slave

```bash
# Emulator as DF1 slave on COM2
dotnet run --project ../PCCCEmulator/PCCCEmulator.csproj -- COM2 --mode df1slave --node 1

# Client as master on COM3
dotnet run --project Example.csproj -- COM3 --mode df1master --target 1
```

> **Note:** Virtual serial pairs are full‑duplex — add `--echo-suppression` to the master side to
> discard the echo of its own transmitted frames.

### EtherNet/IP loopback

```bash
# Start emulator in EIP mode
dotnet run --project ../PCCCEmulator/PCCCEmulator.csproj -- --mode eip --port 44818

# Run client
dotnet run --project Example.csproj -- --mode eip --host 127.0.0.1
```

### CSPv4 loopback

```bash
# Start emulator in CSP mode
dotnet run --project ../PCCCEmulator/PCCCEmulator.csproj -- --mode csp --csp-port 2222

# Run client
dotnet run --project Example.csproj -- --mode csp --host 127.0.0.1 --csp-port 2222

# With RSLinx-compatible LSAP control byte
dotnet run --project Example.csproj -- --mode csp --host 127.0.0.1 --lsap-control 05
```

### Linux serial port permissions

```bash
sudo usermod -a -G dialout $USER
# Log out and back in for the change to take effect.
```

Both `/dev/ttyUSB0` and `ttyUSB0` are accepted as port arguments.

---

## Interactive CLI

After the demo (or with `--interactive-only`) the client enters the `PCCC>` prompt.

```
=== Interactive CLI Mode ===
Type 'help' for commands, 'exit' to quit.

PCCC>
```

### Command reference

#### Data access

| Command | Description |
|---------|-------------|
| `read <addr> [count]` | Read one or more consecutive elements |
| `write <addr> <val> [val…]` | Write one or more integers to address |
| `writestring <addr> <text>` | Write ASCII string to an ST file element |
| `sendhex <DST> <CMD> <FNC> [bytes…]` | Send raw PCCC PDU (all values hex) |
| `wordread <fileType> <fileNumber> <element> <wordOffset> <sizeWords>` | Word Range Read (PLC‑5 only, logical binary addressing) |
| `wordwrite <fileType> <fileNumber> <element> <wordOffset> <hex...>` | Word Range Write (PLC‑5 only, data as hex words, low byte first) |

Address format examples: `N7:0`, `F8:5`, `B3:0`, `B3:0/3` (bit), `ST18:0`, `O0:0`, `I1:0`.

#### Processor

| Command | Description |
|---------|-------------|
| `type` | Show processor type code (e.g. `0x49 = SLC 5/03`) |
| `mode` | Show current mode (RUN / PROGRAM) |
| `setrun` | Switch processor to RUN mode |
| `setprog` | Switch processor to PROGRAM mode |

#### Node management

| Command | Description |
|---------|-------------|
| `scannodes [from] [to]` | Probe each node in range for a live PLC (default 1–31) |
| `settarget <node>` | Change target node at runtime and probe immediately |
| `watch <addr> [interval_ms]` | Poll address and print on change (default 500 ms, any key stops) |

#### Testing & diagnostics

| Command | Description |
|---------|-------------|
| `selftest` | Run self‑test suite — 26 cases (SLC/ML) or 24 cases (PLC‑5), see below |
| `stats` | Show cumulative communication statistics |
| `resetstats` | Reset statistics counters |
| `exit` / `quit` | Leave interactive mode |
| `help` | Show command reference |

### `sendhex` detail

Sends a raw PCCC PDU bypassing all address parsing. SRC, STS, and TNS are auto‑generated;
DST, CMD, FNC, and data bytes are user‑supplied in hexadecimal.

```
# Read N7:0 (file 7, type 0x89, element 0, 2 bytes)
PCCC> sendhex 01 0F A1 02 07 89 00
      TX: 01 00 0F 00 00 00 A1 02 07 89 00
      RX: 00 01 4F 00 A8 00 7B 00

# Echo test (CMD=0x06, FNC=0x00) — returns the same data bytes
PCCC> sendhex 01 06 00 41 42 43
      TX: 01 00 06 00 00 00 00 41 42 43
      RX: 00 01 46 00 1B 00 00 41 42 43
```

### `wordread` and `wordwrite` detail

These commands implement **Word Range Read (0x0F/0x01)** and **Word Range Write (0x0F/0x00)** for
PLC‑5 processors. They operate at the word (16‑bit) level and require **logical binary addressing**
(per AB Publication 1770‑6.5.16 Chapter 13). The address is built from the provided file type,
file number, element number, and a word offset within that element.

```
# Read 5 words (10 bytes) from N7:0 starting at word offset 0
PCCC> wordread N 7 0 0 5
Read 10 bytes:
   00 00 E7 03 00 00 00 00 00 00

# Write three words (0x2211, 0x4433, 0x6655) to N7:0 offset 0
PCCC> wordwrite N 7 0 0 1122 3344 5566
Wrote 3 word(s) successfully.

# Read back to verify
PCCC> wordread N 7 0 0 5
Read 10 bytes:
   11 22 33 44 55 66 00 00 00 00
```

> **Note:** Data for `wordwrite` is supplied as hexadecimal bytes **low byte first** (little‑endian
> word order). For example, `1122` writes `0x22` to the low byte and `0x11` to the high byte of the
> first word. This matches the wire format used by the PCCC protocol.

**Constraints:**
- Maximum size is limited by DF1 payload (164 bytes = 82 words for write, 236 bytes = 118 words for read).
- Only **logical binary addressing** is supported — ASCII addressing (e.g. `$N7:0`) is not implemented.
- The target processor family must be PLC‑5; these commands are not available for SLC/MicroLogix.

### `scannodes` detail

Probes each address in the range by sending `GetProcessorType`. Valid responses are validated
against three criteria to eliminate RS‑485 echo and bus noise false positives:

- Response payload ≥ 22 bytes (minimum for a genuine GetDiagnosticStatus reply)
- Type extender byte = `0xEE` (SLC/MicroLogix family marker per AB Publication 1770‑6.5.16)
- Processor type byte ≠ `0x00`

```
PCCC> scannodes 1 8
--- Node Scan (nodes 1–8, timeout 1000 ms each) ---
  Node   1: no response
  Node   2: no response
  Node   3: FOUND  type=0x49  (SLC 5/03)
  Node   4: no response
  ...
  1 node(s) found:
    Node   3  type=0x49  (SLC 5/03)
  Target node restored to 3.
```

> `scannodes` temporarily sets a 1000 ms per‑probe timeout and restores the original timeout
> and target node when the scan completes.

### `watch` detail

Polls a single address and prints only when the value changes, with an elapsed‑time prefix.
Stops automatically after three consecutive read errors.

```
PCCC> watch F8:0 200
Watching F8:0 every 200 ms. Press any key to stop.

  [00:00:00.201]  F8:0 = 33.14096  (initial)
  [00:00:02.814]  F8:0 = 33.15201  (was: 33.14096)

  Watch stopped. 26 reads, 2 change(s) in 00:00:06.
```

---

## Self‑test suite

Run from the interactive CLI:

```
PCCC> selftest
```

The suite exercises every major library feature and prints a `[PASS]` / `[FAIL]` verdict for
each of 54 individual test cases. It is designed to be run against the PCCCEmulator after any
change to the library or emulator.

> **Caution:** The self‑test writes to N7:2–9, F8:2–7, B3:1–2, and ST18:2–5.
> Do not run against a real PLC unless you are certain those addresses are safe to modify.

### Test groups

| # | Group | What is tested |
|---|-------|---------------|
| 1 | Processor Info | `GetProcessorType()`, `GetRunMode()` |
| 2 | Directory Enumeration | `GetDataMemory()`, mandatory file presence (O0, I1, S2, B3, N7, F8); ST18 checked for SLC/ML only |
| 3 | Integer Read/Write | N7 round‑trips: positive, zero, negative, int16 min/max |
| 4 | Float Read/Write | F8 round‑trips: pi, zero, negative, large, near‑min, negative zero |
| 5 | Bit Read/Write | B3 via FNC=0xAB: set pattern, clear bit, all‑bits‑set |
| 6 | Multi‑Element Read | `ReadAny(addr, count)` burst — N7 (2 byte/elem) and F8 (4 byte/elem) |
| 7 | Multi‑Element Write | `WriteData(addr, count, array)` bulk write |
| 8 | String Read/Write | ST18 round‑trips: short, empty, mixed chars, max 82 chars |
| 9 | Boundary Conditions | Out‑of‑range element and non‑existent file must throw exception |
| 10 | Processor Mode | `SetRunMode()` / `SetProgramMode()` with original mode restored |
| 11 | Latency | Min / avg / max RTT over 20 samples of N7:0 |

### Verified results (real SLC 5/03 hardware)

| Transport | Node | Result | Latency avg | Elapsed |
|-----------|------|--------|-------------|---------|
| EIP (PCCCEmulator loopback) | — | 54/54 PASS | 4.2 ms | 537 ms |
| DF1 full‑duplex | 1 | 54/54 PASS | 32.7 ms | 3600 ms |
| DF1 half‑duplex master | 1 | 54/54 PASS | 32.2 ms | 3527 ms |
| DF1 half‑duplex master | 3 | 54/54 PASS | 31.9 ms | 3587 ms |

The ~32 ms DF1 latency reflects the physical limit of 19200 baud RS‑485 (≈ 30 req/s sustained),
not software overhead.

---

## Stress test

```bash
# DF1: 500 iterations
dotnet run --project Example.csproj -- COM1 --stress-test 500

# EIP: infinite (press any key to stop)
dotnet run --project Example.csproj -- --mode eip --host 192.168.1.10 --stress-test

# Combined: scan nodes first, then stress test
dotnet run --project Example.csproj -- COM1 --mode df1master --target 3 --stress-test 1000
```

Reads `F8:0` continuously, prints progress every 100 iterations, and reports statistics on exit.

---

## Expected output (EIP, successful run)

```
EIP: Connecting to 127.0.0.1:44818 (timeout 5000 ms)
EIP session established with 127.0.0.1:44818

Verifying target node 1... OK  (type=0x49  SLC 5/03)

--- Processor Info ---
Processor Type : 0x49
Mode           : RUN

--- Data Files ---
  File  0: Type=0xO   Elements=6
  File  7: Type=0xN   Elements=74
  File  8: Type=0xF   Elements=38
  File 18: Type=0xST  Elements=10
  ...

--- Read Operations ---
  N7:0   = 0
  F8:0   = 1.23
  ST18:0 = "EMULATOR OK"
  ...

--- Write Operations ---
  Writing 999 to N7:1...
  Writing 2.718 to F8:1...
  Setting B3:0/0 = 1...
  Writing string to ST18:1...

--- Read-Back After Write ---
  N7:1   = 999
  F8:1   = 2.718
  B3:0   = 9  (bits 0 and 3 set → expected 9)
  ST18:1 = "HELLO PCCC"

=== Communication Statistics ===
Total requests   : 19
Successful       : 19
Timeouts         : 0
NAK responses    : 0
Other errors     : 0
Error rate       : 0.00%
=================================

=== Interactive CLI Mode ===
Type 'help' for commands, 'exit' to quit.

PCCC>
```

---

## Troubleshooting

| Issue | Solution |
|-------|----------|
| `Verifying target node N... FAILED` | Node is unreachable. Run `scannodes` in the CLI or restart with `--target <correct node>`. |
| `No response` / timeout | Verify the COM port, baud rate, and parity match the PLC. Check cable and RS‑485 termination. |
| `Checksum mismatch` | Ensure `--checksum` matches the PLC setting (both default to CRC). |
| `Illegal Command or Format` | File or element address out of range. Check file numbers and element bounds. |
| `Processor is in Program mode` | Use `setrun` in the CLI or pass `--interactive-only` and call `SetRunMode()`. |
| `PrefixAndSend method not found` | `sendhex` uses reflection. Ensure the `PCCCComm` library version matches this client. |
| EIP connection refused / timeout | Confirm the emulator or PLC is in EIP mode, TCP 44818 is not blocked, and `--host` / `--eip-port` are correct. |
| RS‑485 half‑duplex no response | Confirm emulator is in `--mode df1slave` with matching node ID. Add `--echo-suppression` for virtual pairs. Check `--rs485-mode` and cable polarity. |
| False positives in `scannodes` | Should not occur with current validation (length + type extender + processor type checks). If seen, increase `probeTimeoutMs` or check for bus noise. |
| CSP connection refused / timeout | Confirm the emulator or PLC is in CSP mode, TCP 2222 is not blocked, and `--host` / `--csp-port` are correct. For RSLinx, add `--lsap-control 05`. |

---

## License

Same as the PCCCComm library (GPL‑3.0‑or‑later).

## See also

- [PCCCEmulator](../PCCCEmulator) — standalone emulator for safe DF1 and EIP testing
- [PCCCComm Library](https://github.com/kumajaya/PCCCComm) — source and API documentation

# Serial.Lib

Serial port I/O for .NET that does one job well: cancellable, deadline-honouring reads and writes that never
abort spuriously, a bounded open and close, typed failures, and port enumeration with a stable hardware
identity. Namespace `SharpAstro.Serial`, `net10.0`, AOT and trim compatible. Written for the astronomy devices
[TianWen](https://github.com/SharpAstro/tianwen) drives (mounts, focusers, flat panels, filter wheels), where
"it mostly works" is not good enough.

## Why

`System.IO.Ports.SerialPort` async reads are not trustworthy:

- On a CH34x USB bridge (very common on cheap devices) the first `BaseStream.ReadAsync` succeeds and every later
  one aborts with `ERROR_OPERATION_ABORTED` while the reply still arrives, so replies land one frame late.
- The "async" is a blocking read on a pool thread anyway ([dotnet/runtime#28968](https://github.com/dotnet/runtime/issues/28968)),
  and it ignores `ReadTimeout`, so a timeout built from `Task.WhenAny` leaves the read hanging.
- A write to a Bluetooth serial port with nobody on the far end never completes and ignores its token.

This library drives the port only through blocking calls, each bounded, and builds every guarantee above them once.

## Use

```csharp
using SharpAstro.Serial;

await using var port = await SerialPorts.OpenAsync("COM3", new SerialSettings(9600) { AssertDtr = true, AssertRts = true });

await port.WriteAsync(":00#"u8.ToArray());
var reply = new byte[32];
var n = await port.ReadTerminatedAsync(reply, "#"u8.ToArray());   // throws SerialTimeoutException, never returns a default
```

## The contract

- A read completes, or throws `SerialTimeoutException` at its deadline (carrying the bytes that did arrive), or
  `OperationCanceledException` for the caller's token, or another `SerialException`. Cancelling never leaves a
  read pending that could eat the next reply.
- Bytes after a reply's terminator are kept for the next read; a reply longer than the buffer is refused
  (`SerialFramingException`), never truncated.
- A write still pending at its deadline marks the port (`HasAbandonedIo`); every later write throws
  `SerialIoAbandonedException` at once instead of stranding another thread.
- A fault on a port that is no longer enumerated is `SerialPortRemovedException`, distinct from a timeout.
- Open and close are bounded; a close that cannot finish abandons the handle, not the caller.
- Every exception derives from `SerialException`, which derives from `IOException`.
- `OpenedAt` says when the open finished: opening resets many boards (every CH340 one), and some firmware saves
  state on a delay.

## Testing without hardware

`SerialLoopback.CreatePair(settings)` returns two ports wired to each other in memory: what one writes, the
other reads. They are real ports in every respect but the wire, so a test through them gets the same deadlines,
framing and carry-over as a COM port.

## Identity

`SerialPorts.Enumerate()` lists each port with what the OS knows about it: USB vendor and product id, serial
number, device instance id, and the USB socket's location path (Windows device tree; Linux sysfs and
`/dev/serial/by-path` / `by-id`). `SerialPortInfo.Identity` says what a saved configuration can key on:

| `Identity` | keyed on | survives |
|---|---|---|
| `Device` | vendor, product, serial number | moving the device to another socket |
| `Socket` | the USB location path | renames and re-enumeration, but two identical devices swapped between sockets swap identities |
| `PortName` | the OS name | nothing; a COM name follows the socket, a `/dev/ttyUSBn` name follows enumeration order |

`IdentityKey` renders the strongest one as a string (`usb:1a86:7523:SERIAL`, `socket:...`, `name:COM3`).

## Status

1.0 is the managed backend (the blocking half of `System.IO.Ports`); 1.1 adds reads with no deadline
(`Timeout.InfiniteTimeSpan`, the token alone ends them) and the loopback pair. A native Win32 backend (overlapped I/O
driven correctly) is planned as 2.0, behind the same API. Design notes: `docs/plans/serial-lib.md` in tianwen.

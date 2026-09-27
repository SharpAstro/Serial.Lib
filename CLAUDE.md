# CLAUDE.md

Serial port I/O for .NET (`SharpAstro.Serial`, package `Serial.Lib`), extracted from TianWen so it is done
properly once. The design and its phasing live in tianwen's `docs/plans/serial-lib.md`; the README states the
contract. Follows the SharpAstro library pattern (`../.github/docs/dotnet-ci-pattern.md`): the version is the one
`<VersionMajorMinor>` in `Directory.Build.props`.

## Build and test

```bash
dotnet build
dotnet test --project src/Serial.Lib.Tests                     # Microsoft.Testing.Platform (global.json)
SERIAL_LIB_BENCH_PORT=COM3 dotnet test --project src/Serial.Lib.Tests --filter "FullyQualifiedName~BenchTests"
```

The bench tests need a myFocuserPro2-protocol focuser (a Gemini Focuser Pro) on the named port and only READ
from it. They open the port once per class: every open resets a CH340 board.

## Layout

- `ISerialPort` / `SerialPorts` / `SerialSettings` / `SerialPortInfo` / the `SerialException` family: the public API.
- `SerialPortCore`: every guarantee (deadlines as a timer-driven token linked with the caller's, framing and the
  carry-over, the abandoned-write guard, the bounded close, removal classification), built ONCE over the seam.
- `Backends/ISerialBackend`: the transport seam, asynchronous. `SystemIoPortsBackend` uses only `System.IO.Ports`'
  blocking calls (its async ones are the bug), `LoopbackBackend` a channel; a native Win32 backend with overlapped
  I/O is P2 and plugs in here, never above.
- `Enumeration/`: the Windows device tree (SetupAPI + cfgmgr32, `LibraryImport`) and Linux sysfs +
  `/dev/serial/by-*`, both producing `SerialPortInfo` with an explicit identity kind.

## Rules that bite

- **Never call a `System.IO.Ports` async member.** `BaseStream.ReadAsync` is the CH34x `ERROR_OPERATION_ABORTED`
  trap this library exists for, ignores `ReadTimeout`, and is a blocking read on a pool thread anyway.
- **Nothing blocks a thread except `SystemIoPortsBackend`**, which must: `System.IO.Ports`' only reliable read and
  write are the blocking ones. It answers buffered bytes at once and blocks only to wait, in short slices that
  look at the token. Anything else waits asynchronously (a channel, a `TaskCompletionSource`), tests included.
- **A failure throws; it never returns a default.** A reply that did not come is a `SerialTimeoutException`
  carrying what did arrive. Every exception derives from `SerialException : IOException`.
- **A new guarantee goes in `SerialPortCore` with a `FakeBackend` test**, never into a backend: a backend is a
  faithful transport and nothing more.
- **Parsing is pure and platform-neutral** (`UsbInstanceId`, `DeviceStrings`, the Linux describer behind
  `ILinuxFileSystem`), so it is tested on every OS; only the P/Invoke calls are Windows-only.

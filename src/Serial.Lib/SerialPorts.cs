using SharpAstro.Serial.Backends;
using SharpAstro.Serial.Enumeration;

namespace SharpAstro.Serial;

/// <summary>Opening and finding serial ports.</summary>
public static class SerialPorts
{
    /// <summary>
    /// The ports present now, each with whatever stable hardware identity the OS offers
    /// (<see cref="SerialPortInfo.Identity"/>). Never throws for a port it cannot describe: that port is listed by
    /// name alone.
    /// </summary>
    public static IReadOnlyList<SerialPortInfo> Enumerate()
    {
        var names = PortNames.Present();
        if (OperatingSystem.IsWindows())
        {
            return WindowsPortEnumerator.Describe(names);
        }
        if (OperatingSystem.IsLinux())
        {
            return LinuxPortEnumerator.Describe(names, LinuxFileSystem.Instance);
        }
        return [.. names.Select(static n => new SerialPortInfo(n))];
    }

    /// <summary>True when the OS lists <paramref name="portName"/> now.</summary>
    public static bool Exists(string portName) => PortNames.Present().Contains(PortNames.Normalize(portName), PortNames.Comparer);

    /// <inheritdoc cref="OpenAsync(string, SerialSettings, TimeProvider, CancellationToken)"/>
    public static ValueTask<ISerialPort> OpenAsync(string portName, SerialSettings settings, CancellationToken cancellationToken = default)
        => OpenAsync(portName, settings, TimeProvider.System, cancellationToken);

    /// <summary>
    /// Opens a port within <see cref="SerialSettings.OpenTimeout"/>.
    /// </summary>
    /// <param name="portName">An OS port name: <c>COM3</c>, <c>/dev/ttyUSB0</c>, or <c>ttyUSB0</c> for <c>/dev/ttyUSB0</c>.</param>
    /// <param name="settings">How to open it.</param>
    /// <param name="timeProvider">The clock every deadline runs on.</param>
    /// <param name="cancellationToken">Cancels the open.</param>
    /// <exception cref="SerialPortNotFoundException">No such port.</exception>
    /// <exception cref="SerialPortBusyException">Another handle holds it.</exception>
    /// <exception cref="SerialTimeoutException">The open did not finish in time (its handle is closed if it ever arrives).</exception>
    public static ValueTask<ISerialPort> OpenAsync(string portName, SerialSettings settings, TimeProvider timeProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        var name = PortNames.Normalize(portName);
        return OpenCoreAsync(new SystemIoPortsBackend(name, settings), name, settings, timeProvider, Exists, cancellationToken);
    }

    internal static async ValueTask<ISerialPort> OpenCoreAsync(ISerialBackend backend, string portName, SerialSettings settings,
        TimeProvider time, Func<string, bool> portExists, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var open = Task.Run(backend.Open, CancellationToken.None);
        try
        {
            await open.WaitAsync(settings.OpenTimeout, time, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when ((ex is TimeoutException || (ex is OperationCanceledException && cancellationToken.IsCancellationRequested)) && !open.IsCompleted)
        {
            // Never leak the handle a late open may still produce.
            _ = open.ContinueWith(_ => { try { backend.Close(); } catch (Exception) { } }, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            if (ex is OperationCanceledException)
            {
                throw;
            }
            throw new SerialTimeoutException(portName, $"{portName} did not open within {settings.OpenTimeout.TotalMilliseconds:0} ms.", settings.OpenTimeout);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException && open.IsCompletedSuccessfully)
        {
            // Opened in the race with the deadline: keep it.
        }
        catch (UnauthorizedAccessException ex)
        {
            throw new SerialPortBusyException(portName, ex);
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or InvalidOperationException)
        {
            if (!portExists(portName))
            {
                throw new SerialPortNotFoundException(portName, ex);
            }
            throw new SerialException(portName, $"{portName} did not open: {ex.Message}", ex);
        }

        return new SerialPortCore(backend, portName, settings, time, portExists, time.GetUtcNow());
    }
}

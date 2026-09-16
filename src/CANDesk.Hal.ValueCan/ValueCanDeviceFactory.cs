using System.Runtime.Versioning;
using CANDesk.Hal;

namespace CANDesk.Hal.ValueCan;

[SupportedOSPlatform("windows")]
public sealed class ValueCanDeviceFactory : ICanDeviceFactory, ICanDeviceFactoryDiagnostics
{
    private static readonly string[] KnownNativeLibraryNames =
    [
        "icsneo40.dll",
        "icsneo40-64.dll",
        "icsneolegacy.dll"
    ];

    public string Vendor => "ValueCAN";
    public string LastEnumerationDiagnostics { get; private set; } = "ValueCAN neoVI native SDK binding has not been configured yet.";

    public Task<IReadOnlyList<CanDeviceDescriptor>> EnumerateAsync(CancellationToken cancellationToken = default)
    {
        var found = KnownNativeLibraryNames
            .Where(name => File.Exists(Path.Combine(AppContext.BaseDirectory, name)))
            .ToArray();

        LastEnumerationDiagnostics = found.Length == 0
            ? "ValueCAN neoVI DLL was not found next to the application. Expected one of: " + string.Join(", ", KnownNativeLibraryNames) + "."
            : "ValueCAN neoVI DLL detected (" + string.Join(", ", found) + "), but the native API binding still needs SDK header verification.";

        return Task.FromResult<IReadOnlyList<CanDeviceDescriptor>>([]);
    }

    public Task<ICanDevice> CreateAsync(string deviceId, string channelName, CancellationToken cancellationToken = default)
    {
        throw new NotSupportedException("ValueCAN support is registered for discovery diagnostics, but neoVI API open/read/write binding is not implemented yet.");
    }
}

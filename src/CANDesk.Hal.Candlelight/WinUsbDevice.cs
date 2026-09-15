using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace CANDesk.Hal.Candlelight;

[SupportedOSPlatform("windows")]
internal sealed class WinUsbDevice : IDisposable
{
    private const byte UsbRequestTypeVendorOutInterface = 0x41;
    private const byte UsbRequestTypeVendorInInterface = 0xC1;
    private const byte UsbdPipeTypeBulk = 2;
    private const uint PipeTransferTimeout = 3;
    private const uint ControlTimeoutMilliseconds = 500;
    private const uint OutTimeoutMilliseconds = 500;
    private readonly SafeFileHandle _fileHandle;
    private readonly SafeWinUsbHandle _winUsbHandle;

    private WinUsbDevice(SafeFileHandle fileHandle, SafeWinUsbHandle winUsbHandle, byte interfaceNumber, byte bulkInPipe, byte bulkOutPipe)
    {
        _fileHandle = fileHandle;
        _winUsbHandle = winUsbHandle;
        InterfaceNumber = interfaceNumber;
        BulkInPipe = bulkInPipe;
        BulkOutPipe = bulkOutPipe;
        SetPipeTimeout(BulkOutPipe, OutTimeoutMilliseconds);
    }

    public byte InterfaceNumber { get; }
    private byte BulkInPipe { get; }
    private byte BulkOutPipe { get; }

    public static WinUsbDevice Open(string devicePath)
    {
        var fileHandle = NativeMethods.CreateFile(devicePath, NativeMethods.GenericRead | NativeMethods.GenericWrite,
            shareMode: 0, IntPtr.Zero, NativeMethods.OpenExisting,
            NativeMethods.FileAttributeNormal | NativeMethods.FileFlagOverlapped, IntPtr.Zero);
        if (fileHandle.IsInvalid)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open WinUSB device '{devicePath}'.");
        }

        if (!NativeMethods.WinUsbInitialize(fileHandle, out var winUsbHandle))
        {
            var error = Marshal.GetLastWin32Error();
            fileHandle.Dispose();
            throw new Win32Exception(error, $"WinUsb_Initialize failed for '{devicePath}'.");
        }

        try
        {
            SetPipeTimeout(winUsbHandle, pipeId: 0, ControlTimeoutMilliseconds);
            if (!NativeMethods.WinUsbQueryInterfaceSettings(winUsbHandle, 0, out var interfaceDescriptor))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_QueryInterfaceSettings failed.");
            }

            byte bulkIn = 0;
            byte bulkOut = 0;
            for (byte pipeIndex = 0; pipeIndex < interfaceDescriptor.NumEndpoints; pipeIndex++)
            {
                if (!NativeMethods.WinUsbQueryPipe(winUsbHandle, 0, pipeIndex, out var pipeInfo))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUsb_QueryPipe failed.");
                }

                if (pipeInfo.PipeType != UsbdPipeTypeBulk)
                {
                    continue;
                }

                if ((pipeInfo.PipeId & 0x80) != 0)
                {
                    bulkIn = pipeInfo.PipeId;
                }
                else
                {
                    bulkOut = pipeInfo.PipeId;
                }
            }

            if (bulkIn == 0 || bulkOut == 0)
            {
                throw new InvalidOperationException("The WinUSB interface does not expose both bulk IN and bulk OUT endpoints.");
            }

            return new WinUsbDevice(fileHandle, winUsbHandle, interfaceDescriptor.InterfaceNumber, bulkIn, bulkOut);
        }
        catch
        {
            winUsbHandle.Dispose();
            fileHandle.Dispose();
            throw;
        }
    }

    public void ControlOut(byte request, ushort value, ReadOnlySpan<byte> data)
    {
        var setup = new NativeMethods.WinUsbSetupPacket
        {
            RequestType = UsbRequestTypeVendorOutInterface,
            Request = request,
            Value = value,
            Index = InterfaceNumber,
            Length = (ushort)data.Length,
        };

        var buffer = data.ToArray();
        if (!NativeMethods.WinUsbControlTransfer(_winUsbHandle, setup, buffer, buffer.Length, out var transferred, IntPtr.Zero) ||
            transferred != buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WinUSB control OUT request {request} failed.");
        }
    }

    public void ControlIn(byte request, ushort value, Span<byte> data)
    {
        var setup = new NativeMethods.WinUsbSetupPacket
        {
            RequestType = UsbRequestTypeVendorInInterface,
            Request = request,
            Value = value,
            Index = InterfaceNumber,
            Length = (ushort)data.Length,
        };

        var buffer = new byte[data.Length];
        if (!NativeMethods.WinUsbControlTransfer(_winUsbHandle, setup, buffer, buffer.Length, out var transferred, IntPtr.Zero) ||
            transferred != buffer.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WinUSB control IN request {request} failed.");
        }

        buffer.CopyTo(data);
    }

    public int ReadBulk(Span<byte> buffer)
    {
        var array = new byte[Math.Max(buffer.Length, 128)];
        var pinned = GCHandle.Alloc(array, GCHandleType.Pinned);
        var receiveEvent = NativeMethods.CreateEvent(IntPtr.Zero, manualReset: false, initialState: false, name: null);
        if (receiveEvent == IntPtr.Zero)
        {
            pinned.Free();
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateEvent failed for WinUSB bulk read.");
        }

        try
        {
            var overlapped = new NativeOverlapped { EventHandle = receiveEvent };
            var transferred = 0;
            if (!NativeMethods.WinUsbReadPipeOverlapped(_winUsbHandle, BulkInPipe, pinned.AddrOfPinnedObject(), array.Length,
                    IntPtr.Zero, ref overlapped))
            {
                var error = Marshal.GetLastWin32Error();
                if (error == NativeMethods.ErrorIoPending)
                {
                    var wait = NativeMethods.WaitForSingleObject(receiveEvent, NativeMethods.Infinite);
                    if (wait != NativeMethods.WaitObject0)
                    {
                        throw new Win32Exception(Marshal.GetLastWin32Error(), "Waiting for WinUSB bulk read failed.");
                    }

                    if (!NativeMethods.WinUsbGetOverlappedResult(_winUsbHandle, ref overlapped, out transferred, wait: false))
                    {
                        error = Marshal.GetLastWin32Error();
                        if (error == NativeMethods.ErrorOperationAborted)
                        {
                            return 0;
                        }

                        throw new Win32Exception(error, "WinUSB overlapped bulk read failed.");
                    }
                }
                else if (error == NativeMethods.ErrorOperationAborted)
                {
                    return 0;
                }
                else
                {
                    throw new Win32Exception(error, "WinUSB bulk read failed.");
                }
            }

            array.AsSpan(0, Math.Min(transferred, buffer.Length)).CopyTo(buffer);
            return transferred;
        }
        finally
        {
            NativeMethods.CloseHandle(receiveEvent);
            pinned.Free();
        }
    }

    public void WriteBulk(ReadOnlySpan<byte> buffer)
    {
        var array = buffer.ToArray();
        if (!NativeMethods.WinUsbWritePipe(_winUsbHandle, BulkOutPipe, array, array.Length, out var transferred, IntPtr.Zero) ||
            transferred != array.Length)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "WinUSB bulk write failed.");
        }
    }

    private void SetPipeTimeout(byte pipeId, uint timeoutMilliseconds) => SetPipeTimeout(_winUsbHandle, pipeId, timeoutMilliseconds);

    private static void SetPipeTimeout(SafeWinUsbHandle winUsbHandle, byte pipeId, uint timeoutMilliseconds)
    {
        var timeout = timeoutMilliseconds;
        if (!NativeMethods.WinUsbSetPipePolicy(winUsbHandle, pipeId, PipeTransferTimeout, sizeof(uint), ref timeout))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"WinUSB SetPipePolicy timeout failed for pipe 0x{pipeId:X2}.");
        }
    }

    public void AbortRead() => NativeMethods.WinUsbAbortPipe(_winUsbHandle, BulkInPipe);

    public void Dispose()
    {
        _winUsbHandle.Dispose();
        _fileHandle.Dispose();
    }
}

// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Diagnostics.NETCore.Client;
using Microsoft.Extensions.Logging;

namespace Microsoft.Diagnostics.Tools.DiagnosticsServerRouter
{

    // TODO:
    // Redo discover connect logic, since connected devices might not be paired directly.
    // Each discovered device gets connected and added to list of connected device.
    // When a devce gets disconnected its removed from list and if it was selected, reset.
    // When we need a new usbmux connection, if there is no device already selected,
    // search list for matching device, pattern matching + ispairing, log info.
    // if no device found, fail ubsmux connect.
    // if we find a device, set that as selected device, device will be selected until disconnected.
    // Fix capabilities to pass a device filter pattern when using port forwarding, maybe it should be
    // simple as appending arguments to exising parameter, like ios,*mydevice*

    internal static class USBMuxInterop
    {
        public const string CoreFoundationLibraryPath = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
        public const string MobileDeviceLibraryPath = "/System/Library/PrivateFrameworks/MobileDevice.framework/MobileDevice";
        public const string CLibraryPath = "/usr/lib/libc";
        public const string SystemLibraryPath = "/usr/lib/libSystem";

        public const int EINTR = 4;

        public struct CFRange
        {
            public nint location;
            public nint length;
        };

        public enum InterfaceType : uint
        {
            Usb = 1,
            Wifi = 2,
        }

        public enum AMDeviceNotificationMessage : uint
        {
            None = 0,
            Connected = 1,
            Disconnected = 2,
            Unsubscribed = 3
        }

        public struct AMDeviceNotificationCallbackInfo
        {
            public AMDeviceNotificationCallbackInfo(IntPtr device, AMDeviceNotificationMessage message)
            {
                am_device = device;
                this.message = message;
            }

            public IntPtr am_device;
            public AMDeviceNotificationMessage message;
        }

        public delegate void DeviceNotificationDelegate(ref AMDeviceNotificationCallbackInfo info);

        public static class CoreFoundation
        {
            private static readonly IntPtr Handle = dlopen(CoreFoundationLibraryPath, 0);
            public static readonly IntPtr kCFTypeDictionaryKeyCallBacks = dlsym(Handle, "kCFTypeDictionaryKeyCallBacks");
            public static readonly IntPtr kCFTypeDictionaryValueCallBacks = dlsym(Handle, "kCFTypeDictionaryValueCallBacks");
            public static readonly IntPtr kCFBooleanTrue = dlsym(Handle, "kCFBooleanTrue");
            public static readonly IntPtr kCFBooleanFalse = dlsym(Handle, "kCFBooleanFalse");
        }

        #region MobileDeviceLibrary
        [DllImport(MobileDeviceLibraryPath)]
        private static extern IntPtr AMDeviceCopyValue(IntPtr device, IntPtr unknown, IntPtr key);

        public static string AMDeviceGetValue(IntPtr device, string key)
        {
            IntPtr keyCFStringRef = IntPtr.Zero;
            IntPtr valueCFStringRef = IntPtr.Zero;

            try
            {
                keyCFStringRef = CFStringCreateWithCharacters(IntPtr.Zero, key);
                if (keyCFStringRef == IntPtr.Zero)
                {
                    return string.Empty;
                }

                valueCFStringRef = AMDeviceCopyValue(device, IntPtr.Zero, keyCFStringRef);
                if (valueCFStringRef == IntPtr.Zero)
                {
                    return string.Empty;
                }

                return CFStringGetCharacters(valueCFStringRef);
            }
            finally
            {
                if (valueCFStringRef != IntPtr.Zero)
                {
                    CFRelease(valueCFStringRef);
                }

                if (keyCFStringRef != IntPtr.Zero)
                {
                    CFRelease(keyCFStringRef);
                }
            }

        }

        [DllImport(MobileDeviceLibraryPath)]
        public static extern uint AMDeviceNotificationSubscribe(DeviceNotificationDelegate callback, uint unused0, uint unused1, uint unused2, out IntPtr context);

        [DllImport(MobileDeviceLibraryPath)]
        public static extern uint AMDeviceNotificationSubscribeWithOptions(DeviceNotificationDelegate callback, uint unused0, uint unused1, uint dn_unknown3, out IntPtr context, IntPtr options);


        [DllImport(MobileDeviceLibraryPath)]
        public static extern uint AMDeviceNotificationUnsubscribe(IntPtr context);

        [DllImport(MobileDeviceLibraryPath)]
        public static extern uint AMDeviceConnect(IntPtr device);

        [DllImport(MobileDeviceLibraryPath)]
        public static extern uint AMDeviceDisconnect(IntPtr device);

        [DllImport(MobileDeviceLibraryPath)]
        public static extern uint AMDeviceIsPaired(IntPtr device);

        [DllImport(MobileDeviceLibraryPath)]
        public static extern uint AMDeviceValidatePairing(IntPtr device);

        [DllImport(MobileDeviceLibraryPath)]
        public static extern uint AMDeviceGetConnectionID(IntPtr device);

        [DllImport(MobileDeviceLibraryPath)]
        public static extern int AMDeviceGetInterfaceType(IntPtr device);

        [DllImport(MobileDeviceLibraryPath)]
        private static extern IntPtr AMDeviceCopyDeviceIdentifier(IntPtr device);

        public static string AMDeviceGetDeviceIdentifier(IntPtr device)
        {
            IntPtr value = IntPtr.Zero;

            try
            {
                value = AMDeviceCopyDeviceIdentifier(device);
                if (value == IntPtr.Zero)
                {
                    return string.Empty;
                }

                return CFStringGetCharacters(value);
            }
            finally
            {
                if (value != IntPtr.Zero)
                {
                    CFRelease(value);
                }
            }
        }

        [DllImport(MobileDeviceLibraryPath)]
        public static extern uint USBMuxConnectByPort(uint connection, ushort port, out int socketHandle);
        #endregion
        #region CoreFoundationLibrary
        [DllImport(CoreFoundationLibraryPath)]
        public static extern void CFRunLoopRun();

        [DllImport(CoreFoundationLibraryPath)]
        public static extern void CFRunLoopStop(IntPtr runLoop);

        [DllImport(CoreFoundationLibraryPath)]
        public static extern IntPtr CFRunLoopGetCurrent();

        [DllImport(CoreFoundationLibraryPath)]
        public static extern IntPtr CFDictionaryCreate(IntPtr allocator, IntPtr[] keys, IntPtr[] vals, nint len, IntPtr keyCallbacks, IntPtr valCallbacks);

        [DllImport(CoreFoundationLibraryPath)]
        public static extern void CFRelease(IntPtr obj);

        [DllImport(CoreFoundationLibraryPath, CharSet = CharSet.Unicode)]
        private static extern IntPtr CFStringCreateWithCharacters(IntPtr allocator, IntPtr str, nint count);

        public static IntPtr CFStringCreateWithCharacters(IntPtr allocator, string value)
        {
            ReadOnlySpan<char> buffer = value.AsSpan();
            unsafe
            {
                fixed (char* bufferPtr = buffer)
                {
                    return CFStringCreateWithCharacters(allocator, (IntPtr)bufferPtr, value.Length);
                }
            }
        }

        [DllImport(CoreFoundationLibraryPath, CharSet = CharSet.Unicode)]
        public static extern nint CFStringGetLength(IntPtr handle);

        [DllImport(CoreFoundationLibraryPath, CharSet = CharSet.Unicode)]
        private static extern void CFStringGetCharacters(IntPtr handle, CFRange range, IntPtr buffer);

        public static string CFStringGetCharacters(IntPtr handle)
        {
            int len = (int)CFStringGetLength(handle);
            if (len == 0)
            {
                return string.Empty;
            }

            using (IMemoryOwner<char> buffer = MemoryPool<char>.Shared.Rent(len))
            {
                unsafe
                {
                    fixed (char* bufferPtr = buffer.Memory.Span)
                    {
                        CFStringGetCharacters(handle, new CFRange { location = 0, length = len }, (IntPtr)bufferPtr);
                    };
                }

                return new string(buffer.Memory.Span);
            }
        }
        #endregion
        #region LibC
        [DllImport(CLibraryPath, SetLastError = true)]
        public static extern unsafe int send(int handle, byte* buffer, IntPtr length, int flags);

        [DllImport(CLibraryPath, SetLastError = true)]
        public static extern unsafe int recv(int handle, byte* buffer, IntPtr length, int flags);

        [DllImport(CLibraryPath, SetLastError = true)]
        public static extern int close(int handle);
        #endregion
        #region SystemLibrary
        [DllImport(SystemLibraryPath)]
        public static extern int dlclose(IntPtr handle);

        [DllImport(SystemLibraryPath)]
        private static extern IntPtr dlopen(IntPtr path, int mode);

        public static IntPtr dlopen(string path, int mode)
        {
            ReadOnlySpan<char> buffer = path.AsSpan();
            unsafe
            {
                fixed (char* bufferPtr = buffer)
                {
                    return dlopen((IntPtr)bufferPtr, mode);
                }
            }
        }

        [DllImport(SystemLibraryPath)]
        private static extern IntPtr dlsym(IntPtr handle, IntPtr symbol);

        public static IntPtr dlsym(IntPtr handle, string symbol)
        {
            ReadOnlySpan<char> buffer = symbol.AsSpan();
            unsafe
            {
                fixed (char* bufferPtr = buffer)
                {
                    return dlsym(handle, (IntPtr)bufferPtr);
                }
            }
        }
        #endregion
    }

#pragma warning disable CA1844 // Provide memory-based overrides of async methods when subclassing 'Stream'
    internal sealed class USBMuxStream : Stream
#pragma warning restore CA1844 // Provide memory-based overrides of async methods when subclassing 'Stream'
    {
        private int _handle = -1;

        public USBMuxStream(int handle)
        {
            _handle = handle;
        }

        public bool IsOpen => _handle != -1;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => true;

        public override long Length => throw new NotImplementedException();

        public override long Position
        {
            get => throw new NotImplementedException();
            set => throw new NotImplementedException();
        }

        public override void Flush()
        {
        }

        public override Task FlushAsync(CancellationToken cancellationToken)
        {
            return Task.CompletedTask;
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int bytesRead = 0;

            if (offset + count > buffer.Length)
            {
                throw new InvalidOperationException("Potential write beyond end of buffer");
            }

            if (offset < 0)
            {
                throw new InvalidOperationException("Write before beginning of buffer");
            }

            if (count < 0)
            {
                throw new InvalidOperationException("Negative read count");
            }

            while (true)
            {
                if (!IsOpen)
                {
                    throw new EndOfStreamException();
                }

                unsafe
                {
                    fixed (byte* fixedBuffer = buffer)
                    {
                        bytesRead = USBMuxInterop.recv(_handle, fixedBuffer + offset, new IntPtr(count), 0);
                    }
                }

                if (bytesRead == -1 && Marshal.GetLastWin32Error() == USBMuxInterop.EINTR)
                {
                    continue;
                }

                return bytesRead;
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.Run(() => {
                int result = 0;
                using (cancellationToken.Register(() => Close()))
                {
                    try
                    {
                        result = Read(buffer, offset, count);
                    }
                    catch (Exception)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        result = 0;
                    }
                }
                return result;
            });
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            throw new NotImplementedException();
        }

        public override void SetLength(long value)
        {
            throw new NotImplementedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            bool continueWrite = true;
            int bytesToWrite = count;
            int currentBytesWritten = 0;
            int totalBytesWritten = 0;

            while (continueWrite && bytesToWrite - totalBytesWritten > 0)
            {
                if (!IsOpen)
                {
                    throw new EndOfStreamException();
                }

                unsafe
                {
                    fixed (byte* fixedBuffer = buffer)
                    {
                        currentBytesWritten = USBMuxInterop.send(_handle, fixedBuffer + totalBytesWritten, new IntPtr(bytesToWrite - totalBytesWritten), 0);
                    }
                }

                if (currentBytesWritten == -1 && Marshal.GetLastWin32Error() == USBMuxInterop.EINTR)
                {
                    continue;
                }

                continueWrite = currentBytesWritten != -1;

                if (!continueWrite)
                {
                    break;
                }

                totalBytesWritten += currentBytesWritten;
            }
        }

        public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            return Task.Run(() => {
                using (cancellationToken.Register(() => Close()))
                {
                    Write(buffer, offset, count);
                }
            }, cancellationToken);
        }

        public override void Close()
        {
            if (IsOpen)
            {
                USBMuxInterop.close(_handle);
                _handle = -1;
            }
        }

        protected override void Dispose(bool disposing)
        {
            Close();
            base.Dispose(disposing);
        }
    }

    internal sealed class USBMuxTcpClientRouterFactory : TcpClientRouterFactory
    {
        private readonly int _port;
        private IntPtr _device = IntPtr.Zero;
        private uint _deviceConnectionID;
        private IntPtr _loopingThread = IntPtr.Zero;

        public static TcpClientRouterFactory CreateUSBMuxInstance(string tcpClient, int runtimeTimeoutMs, ILogger logger)
        {
            return new USBMuxTcpClientRouterFactory(tcpClient, runtimeTimeoutMs, logger);
        }

        public USBMuxTcpClientRouterFactory(string tcpClient, int runtimeTimeoutMs, ILogger logger)
            : base(tcpClient, runtimeTimeoutMs, logger)
        {
            _port = new IpcTcpSocketEndPoint(tcpClient).EndPoint.Port;
        }

        public override async Task<Stream> ConnectTcpStreamAsync(CancellationToken token)
        {
            return await ConnectTcpStreamAsyncInternal(token, _auto_shutdown).ConfigureAwait(false);
        }

        public override async Task<Stream> ConnectTcpStreamAsync(CancellationToken token, bool retry)
        {
            return await ConnectTcpStreamAsyncInternal(token, retry).ConfigureAwait(false);
        }

        public override void Start()
        {
            // Start device subscription thread.
            StartNotificationSubscribeThread();
        }

        public override void Stop()
        {
            // Stop device subscription thread.
            StopNotificationSubscribeThread();
        }

        private async Task<Stream> ConnectTcpStreamAsyncInternal(CancellationToken token, bool retry)
        {
            int handle = -1;

            _logger?.LogDebug($"Connecting new tcp endpoint over usbmux \"{_tcpClientAddress}\".");

            using CancellationTokenSource connectTimeoutTokenSource = new();
            using CancellationTokenSource connectTokenSource = CancellationTokenSource.CreateLinkedTokenSource(token, connectTimeoutTokenSource.Token);

            connectTimeoutTokenSource.CancelAfter(TcpClientTimeoutMs);

            do
            {
                try
                {
                    handle = ConnectTcpClientOverUSBMux();
                    retry = false;
                }
                catch (Exception)
                {
                    if (connectTimeoutTokenSource.IsCancellationRequested)
                    {
                        _logger?.LogDebug("No USB stream connected, timing out.");

                        if (_auto_shutdown)
                        {
                            throw new RuntimeTimeoutException(TcpClientTimeoutMs);
                        }

                        throw new TimeoutException();
                    }

                    // If we are not doing retries when runtime is unavailable, fail right away, this will
                    // break any accepted IPC connections, making sure client is notified and could reconnect.
                    // If not, retry until succeed or time out.
                    if (!retry)
                    {
                        _logger?.LogTrace($"Failed connecting {_port} over usbmux.");
                        throw;
                    }

                    _logger?.LogTrace($"Failed connecting {_port} over usbmux, wait {TcpClientRetryTimeoutMs} ms before retrying.");

                    // If we get an error (without hitting timeout above), most likely due to unavailable device/listener.
                    // Delay execution to prevent to rapid retry attempts.
                    await Task.Delay(TcpClientRetryTimeoutMs, token).ConfigureAwait(false);
                }
            }
            while (retry);

            return new USBMuxStream(handle);
        }

        private int ConnectTcpClientOverUSBMux()
        {
            uint result = 0;
            int handle = -1;
            ushort networkPort = (ushort)IPAddress.HostToNetworkOrder(unchecked((short)_port));

            lock (this)
            {
                if (_deviceConnectionID == 0)
                {
                    _logger.LogError($"Failed to connect device over USB, no device currently connected.");
                    throw new Exception($"Failed to connect device over USB, no device currently connected.");
                }

                result = USBMuxInterop.USBMuxConnectByPort(_deviceConnectionID, networkPort, out handle);
            }

            if (result != 0)
            {
                _logger?.LogError($"Failed USBMuxConnectByPort: device = {_deviceConnectionID}, port = {_port}, result = {result}.");
                throw new Exception($"Failed to connect device over USB using connection {_deviceConnectionID} and port {_port}.");
            }

            return handle;
        }

        private bool ConnectDevice(IntPtr newDevice, string pattern)
        {
            if (_device != IntPtr.Zero)
            {
                return false;
            }

            if (USBMuxInterop.AMDeviceConnect(newDevice) == 0)
            {
                Regex deviceNameRegex = !string.IsNullOrEmpty(pattern) ? new Regex(pattern) : null;
                string deviceName = USBMuxInterop.AMDeviceGetValue(newDevice, "DeviceName");
                string deviceUUID = USBMuxInterop.AMDeviceGetValue(newDevice, "UniqueDeviceID");
                string deviceID = USBMuxInterop.AMDeviceGetDeviceIdentifier(newDevice);

                if (deviceNameRegex == null || deviceNameRegex.IsMatch(deviceName) || deviceNameRegex.IsMatch(deviceUUID) || deviceNameRegex.IsMatch(deviceID))
                {
                    _logger?.LogDebug($"Discovered new device, name={deviceName}, uuid={deviceUUID}, id={deviceID}");
                    if (USBMuxInterop.AMDeviceIsPaired(newDevice) == 1 && USBMuxInterop.AMDeviceValidatePairing(newDevice) == 0)
                    {
                        _deviceConnectionID = USBMuxInterop.AMDeviceGetConnectionID(newDevice);
                        _logger?.LogInformation($"Successfully connected new device, name={deviceName}, uuid={deviceUUID}, id={deviceID}, connection-id={_deviceConnectionID}.");
                        _device = newDevice;
                        return true;
                    }
                    else
                    {
                        _logger?.LogError($"Failed connecting new device, name={deviceName}, uuid={deviceUUID}, id={deviceID}. Device doesn't have a valid pairing.");
                    }
                }
                else
                {
                    _logger?.LogDebug($"Skipping new device, name={deviceName}, uuid={deviceUUID}, id={deviceID}. Device name|uuid|id didn't match pattern \"{pattern}\".");
                }

                USBMuxInterop.AMDeviceDisconnect(newDevice);
                return false;
            }
            else
            {
                _logger?.LogError($"Failed connecting new device.");
                return false;
            }
        }

        private bool DisconnectDevice()
        {
            if (_device != IntPtr.Zero)
            {
                if (_deviceConnectionID != 0)
                {
                    USBMuxInterop.AMDeviceDisconnect(_device);
                    _logger?.LogInformation($"Successfully disconnected device, id={_deviceConnectionID}.");
                    _deviceConnectionID = 0;
                }

                _device = IntPtr.Zero;
            }

            return true;
        }

        private void AMDeviceNotificationCallback(ref USBMuxInterop.AMDeviceNotificationCallbackInfo info)
        {
            _logger?.LogTrace($"AMDeviceNotificationInternal callback, device={info.am_device}, action={info.message}");

            try
            {
                lock (this)
                {
                    USBMuxInterop.InterfaceType interfaceType = (USBMuxInterop.InterfaceType)USBMuxInterop.AMDeviceGetInterfaceType(info.am_device);
                    bool supportedInterfaceType = interfaceType == USBMuxInterop.InterfaceType.Usb || interfaceType == USBMuxInterop.InterfaceType.Wifi;
                    switch (info.message)
                    {
                        case USBMuxInterop.AMDeviceNotificationMessage.Connected:
                            if (supportedInterfaceType && _device == IntPtr.Zero)
                            {
                                ConnectDevice(info.am_device);
                            }
                            else if (supportedInterfaceType && _device != IntPtr.Zero)
                            {
                                _logger?.LogInformation($"Discovered new device, but one is already connected, ignoring new device.");
                            }
                            else if (!supportedInterfaceType)
                            {
                                _logger?.LogInformation($"Discovered new device not connected over USB/Wifi, ignoring new device.");
                            }
                            break;
                        case USBMuxInterop.AMDeviceNotificationMessage.Disconnected:
                        case USBMuxInterop.AMDeviceNotificationMessage.Unsubscribed:
                            if (_device == info.am_device)
                            {
                                DisconnectDevice();
                            }
                            break;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed AMDeviceNotificationCallback: {ex.Message}. Failed handling device={info.am_device} using action={info.message}");
            }
        }

        private void AMDeviceNotificationSubscribeLoop()
        {
            IntPtr context = IntPtr.Zero;
            IntPtr usbDeviceCFStringRef = IntPtr.Zero;
            IntPtr wifiDeviceCFStringRef = IntPtr.Zero;
            IntPtr subscribeOptions = IntPtr.Zero;

            try
            {
                lock (this)
                {
                    if (_loopingThread != IntPtr.Zero)
                    {
                        _logger?.LogError($"AMDeviceNotificationSubscribeLoop already running.");
                        throw new Exception("AMDeviceNotificationSubscribeLoop already running.");
                    }

                    _loopingThread = USBMuxInterop.CFRunLoopGetCurrent();
                }

                _logger?.LogTrace($"Calling AMDeviceNotificationSubscribe.");

                usbDeviceCFStringRef = USBMuxInterop.CFStringCreateWithCharacters(IntPtr.Zero, "NotificationOptionSearchForPairedDevices");
                wifiDeviceCFStringRef = USBMuxInterop.CFStringCreateWithCharacters(IntPtr.Zero, "NotificationOptionSearchForWiFiPairableDevices");

                subscribeOptions = USBMuxInterop.CFDictionaryCreate(
                    IntPtr.Zero,
                    new IntPtr[] { usbDeviceCFStringRef, wifiDeviceCFStringRef },
                    new IntPtr[] { USBMuxInterop.CoreFoundation.kCFBooleanTrue, USBMuxInterop.CoreFoundation.kCFBooleanTrue },
                    2,
                    USBMuxInterop.CoreFoundation.kCFTypeDictionaryKeyCallBacks,
                    USBMuxInterop.CoreFoundation.kCFTypeDictionaryValueCallBacks);

                if (USBMuxInterop.AMDeviceNotificationSubscribeWithOptions(AMDeviceNotificationCallback, 0, 0, 0, out context, subscribeOptions) != 0)
                {
                    _logger?.LogError($"Failed AMDeviceNotificationSubscribe call.");
                    throw new Exception("Failed AMDeviceNotificationSubscribe call.");
                }

                _logger?.LogTrace($"Start dispatching notifications.");
                USBMuxInterop.CFRunLoopRun();
                _logger?.LogTrace($"Stop dispatching notifications.");
            }
            catch (Exception ex)
            {
                _logger?.LogError($"Failed running subscribe loop: {ex.Message}. Disabling detection of devices connected over USB.");
            }
            finally
            {
                lock (this)
                {
                    if (_loopingThread != IntPtr.Zero)
                    {
                        _loopingThread = IntPtr.Zero;
                    }

                    DisconnectDevice();
                }

                if (context != IntPtr.Zero)
                {
                    _logger?.LogTrace($"Calling AMDeviceNotificationUnsubscribe.");
                    USBMuxInterop.AMDeviceNotificationUnsubscribe(context);
                }

                if (subscribeOptions != IntPtr.Zero)
                {
                    USBMuxInterop.CFRelease(subscribeOptions);
                }

                if (usbDeviceCFStringRef != IntPtr.Zero)
                {
                    USBMuxInterop.CFRelease(usbDeviceCFStringRef);
                }

                if (wifiDeviceCFStringRef != IntPtr.Zero)
                {
                    USBMuxInterop.CFRelease(wifiDeviceCFStringRef);
                }
            }
        }

        private void StartNotificationSubscribeThread()
        {
            new Thread(new ThreadStart(() => AMDeviceNotificationSubscribeLoop())).Start();
        }

        private void StopNotificationSubscribeThread()
        {
            lock (this)
            {
                if (_loopingThread != IntPtr.Zero)
                {
                    USBMuxInterop.CFRunLoopStop(_loopingThread);
                }
            }
        }
    }
}

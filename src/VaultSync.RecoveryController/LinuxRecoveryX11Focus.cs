using System.Runtime.InteropServices;
using Avalonia.Controls;
using Avalonia.Threading;

namespace VaultSync.RecoveryController;

/// <summary>One activation of this controller's mapped window on its admitted, bare X server.</summary>
internal static class LinuxRecoveryX11Focus
{
    private const string Library = "libX11.so.6";
    private const string Authority = "/run/vaultsync-recovery-display/Xauthority";

    internal static async Task<bool> FocusWhenMappedAsync(RecoveryWindow window, CancellationToken token)
    {
        // Avalonia 12.1.4 X11Platform initializes Xlib threading before creating
        // any window. This later connection is ours alone and stays on the UI thread.
        if (!Dispatcher.UIThread.CheckAccess() || !OperatingSystem.IsLinux() ||
            RuntimeInformation.ProcessArchitecture != Architecture.X64 || Marshal.SizeOf<WindowAttributes>() != 136 ||
            Environment.GetEnvironmentVariable("DISPLAY") != ":0" ||
            Environment.GetEnvironmentVariable("XAUTHORITY") != Authority) return false;
        var handle = window.TryGetPlatformHandle();
        if (handle?.HandleDescriptor != "X11Window" || handle.Handle.ToInt64() is <= 1 or > uint.MaxValue) return false;
        nint target = handle.Handle;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, window.ViewModel.CancellationRequested);
        stop.CancelAfter(TimeSpan.FromSeconds(5));
        EventHandler closed = (_, _) => stop.Cancel();
        window.Closed += closed;
        nint display = 0;
        try
        {
            stop.Token.ThrowIfCancellationRequested();
            display = OpenDisplay(":0");
            if (display == 0) return false;
            while (true)
            {
                stop.Token.ThrowIfCancellationRequested();
                var current = window.TryGetPlatformHandle();
                if (!window.IsVisible || current?.HandleDescriptor != "X11Window" || current.Handle != target) return false;
                WindowAttributes attributes = default;
                if (GetWindowAttributes(display, target, ref attributes) == 0 || attributes.Class != 1 ||
                    attributes.Root != DefaultRootWindow(display)) return false;
                stop.Token.ThrowIfCancellationRequested();
                if (attributes.MapState == 2) // IsViewable: never focus an unmapped resource.
                {
                    // Exactly one request to our own handle. No grabs, VT switching,
                    // event forwarding or later focus-reclamation loop is involved.
                    _ = SetInputFocus(display, target, 2, 0); // RevertToParent, CurrentTime.
                    _ = Sync(display, 0);
                    stop.Token.ThrowIfCancellationRequested();
                    _ = GetInputFocus(display, out nint focused, out _);
                    return focused == target;
                }
                await Task.Delay(25, stop.Token);
            }
        }
        catch (Exception error) when (error is OperationCanceledException or DllNotFoundException or
            EntryPointNotFoundException or BadImageFormatException)
        {
            return false;
        }
        finally
        {
            window.Closed -= closed;
            if (display != 0) _ = CloseDisplay(display);
        }
    }

    // LP64 layout from Xlib's XWindowAttributes; also used by the pinned
    // Avalonia 12.1.4 X11Structs.cs. This controller admits Linux x64 only.
    [StructLayout(LayoutKind.Sequential)]
    private struct WindowAttributes
    {
        public int X, Y, Width, Height, BorderWidth, Depth;
        public nint Visual, Root;
        public int Class, BitGravity, WindowGravity, BackingStore;
        public nint BackingPlanes, BackingPixel;
        public int SaveUnder;
        public nint Colormap;
        public int MapInstalled, MapState;
        public nint AllEventMasks, YourEventMask, DoNotPropagateMask;
        public int OverrideRedirect;
        public nint Screen;
    }

    [DllImport(Library, EntryPoint = "XOpenDisplay", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint OpenDisplay([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Library, EntryPoint = "XDefaultRootWindow", CallingConvention = CallingConvention.Cdecl)]
    private static extern nint DefaultRootWindow(nint display);
    [DllImport(Library, EntryPoint = "XGetWindowAttributes", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetWindowAttributes(nint display, nint window, ref WindowAttributes attributes);
    [DllImport(Library, EntryPoint = "XSetInputFocus", CallingConvention = CallingConvention.Cdecl)]
    private static extern int SetInputFocus(nint display, nint window, int revertTo, nint time);
    [DllImport(Library, EntryPoint = "XSync", CallingConvention = CallingConvention.Cdecl)]
    private static extern int Sync(nint display, int discard);
    [DllImport(Library, EntryPoint = "XGetInputFocus", CallingConvention = CallingConvention.Cdecl)]
    private static extern int GetInputFocus(nint display, out nint window, out int revertTo);
    [DllImport(Library, EntryPoint = "XCloseDisplay", CallingConvention = CallingConvention.Cdecl)]
    private static extern int CloseDisplay(nint display);
}

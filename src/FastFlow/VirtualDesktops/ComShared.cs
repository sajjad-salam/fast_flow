using System.Runtime.InteropServices;

namespace FastFlow.VirtualDesktops;

// COM interfaces that are identical on every supported Windows build.

/// <summary>Documented, public API. Can read any window's desktop, but can only move windows of our own process.</summary>
[ComImport, Guid("a5cd92ff-29be-454c-8d04-d82879fb3f1b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManager
{
    bool IsWindowOnCurrentVirtualDesktop(IntPtr topLevelWindow);
    Guid GetWindowDesktopId(IntPtr topLevelWindow);
    void MoveWindowToDesktop(IntPtr topLevelWindow, ref Guid desktopId);
}

[ComImport, Guid("6D5140C1-7436-11CE-8034-00AA006009FA"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IServiceProvider10
{
    [return: MarshalAs(UnmanagedType.IUnknown)]
    object QueryService(ref Guid service, ref Guid riid);
}

[ComImport, Guid("92CA9DCD-5622-4BBA-A805-5E9F541BD8C9"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IObjectArray
{
    void GetCount(out int count);
    void GetAt(int index, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object obj);
}

/// <summary>Undocumented. Only used as an opaque handle that is passed back to the desktop manager.</summary>
[ComImport, Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationView
{
}

/// <summary>Undocumented. Only the first four vtable slots are declared; we only need GetViewForHwnd.</summary>
[ComImport, Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationViewCollection
{
    void GetViews(out IntPtr array);
    void GetViewsByZOrder(out IntPtr array);
    void GetViewsByAppUserModelId([MarshalAs(UnmanagedType.LPWStr)] string id, out IntPtr array);
    void GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
}

internal static class ComGuids
{
    public static readonly Guid CLSID_ImmersiveShell = new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    public static readonly Guid CLSID_VirtualDesktopManagerInternal = new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");
    public static readonly Guid CLSID_VirtualDesktopManager = new("AA509086-5CA9-4C25-8F95-589D3C07B48A");
}

/// <summary>Version-specific access to the undocumented shell interfaces needed to move other apps' windows.</summary>
internal interface IDesktopBackend
{
    string Name { get; }
    List<Guid> GetDesktopIds();
    Guid CreateDesktop();
    void MoveWindowToDesktop(IntPtr hwnd, Guid desktopId);
    void SwitchToDesktop(Guid desktopId);
}

/// <summary>
/// Shared plumbing for the backends. Each Windows build only differs in the IIDs (and vtable layout)
/// of IVirtualDesktop / IVirtualDesktopManagerInternal, so subclasses just wrap those two calls.
/// </summary>
internal abstract class DesktopBackendBase<TManager, TDesktop> : IDesktopBackend
    where TManager : class
    where TDesktop : class
{
    protected readonly TManager Manager;
    private readonly IApplicationViewCollection _views;

    protected DesktopBackendBase()
    {
        var shellType = Type.GetTypeFromCLSID(ComGuids.CLSID_ImmersiveShell)
            ?? throw new InvalidOperationException("ImmersiveShell is not available.");
        var shell = (IServiceProvider10)Activator.CreateInstance(shellType)!;

        var managerClsid = ComGuids.CLSID_VirtualDesktopManagerInternal;
        var managerIid = typeof(TManager).GUID;
        Manager = (TManager)shell.QueryService(ref managerClsid, ref managerIid);

        var viewsIid = typeof(IApplicationViewCollection).GUID;
        _views = (IApplicationViewCollection)shell.QueryService(ref viewsIid, ref viewsIid);
    }

    public abstract string Name { get; }
    protected abstract IObjectArray GetDesktopArray();
    protected abstract Guid GetId(TDesktop desktop);
    protected abstract TDesktop CreateDesktopCore();
    protected abstract void MoveView(IApplicationView view, TDesktop desktop);
    protected abstract void Switch(TDesktop desktop);

    private List<TDesktop> GetDesktops()
    {
        var array = GetDesktopArray();
        array.GetCount(out var count);
        var iid = typeof(TDesktop).GUID;
        var result = new List<TDesktop>(count);
        for (var i = 0; i < count; i++)
        {
            array.GetAt(i, ref iid, out var obj);
            result.Add((TDesktop)obj);
        }
        return result;
    }

    private TDesktop FindDesktop(Guid id) =>
        GetDesktops().FirstOrDefault(d => GetId(d) == id)
        ?? throw new InvalidOperationException($"Virtual desktop {id} not found.");

    public List<Guid> GetDesktopIds() => GetDesktops().Select(GetId).ToList();

    public Guid CreateDesktop() => GetId(CreateDesktopCore());

    public void MoveWindowToDesktop(IntPtr hwnd, Guid desktopId)
    {
        _views.GetViewForHwnd(hwnd, out var view);
        MoveView(view, FindDesktop(desktopId));
    }

    public void SwitchToDesktop(Guid desktopId) => Switch(FindDesktop(desktopId));
}

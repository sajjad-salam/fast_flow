using System.Runtime.InteropServices;

namespace FastFlow.VirtualDesktops.Win10;

// Undocumented shell interfaces for Windows 10 (2004 – 22H2).
// Only the vtable slots we call (and everything before them) are declared.

[ComImport, Guid("FF72FFDD-BE7E-43FC-9C03-AD81681E88E4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktop
{
    bool IsViewVisible(IApplicationView view);
    Guid GetId();
}

[ComImport, Guid("F31574D6-B682-4CDC-BD56-1827860ABEC6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktopManagerInternal
{
    int GetCount();
    void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop);
    bool CanViewMoveDesktops(IApplicationView view);
    IVirtualDesktop GetCurrentDesktop();
    void GetDesktops(out IObjectArray desktops);
    [PreserveSig]
    int GetAdjacentDesktop(IVirtualDesktop from, int direction, out IVirtualDesktop desktop);
    void SwitchDesktop(IVirtualDesktop desktop);
    IVirtualDesktop CreateDesktop();
}

internal sealed class Backend : DesktopBackendBase<IVirtualDesktopManagerInternal, IVirtualDesktop>
{
    public override string Name => "Windows 10 (2004 – 22H2)";

    protected override IObjectArray GetDesktopArray()
    {
        Manager.GetDesktops(out var desktops);
        return desktops;
    }

    protected override Guid GetId(IVirtualDesktop desktop) => desktop.GetId();
    protected override IVirtualDesktop CreateDesktopCore() => Manager.CreateDesktop();
    protected override void MoveView(IApplicationView view, IVirtualDesktop desktop) => Manager.MoveViewToDesktop(view, desktop);
    protected override void Switch(IVirtualDesktop desktop) => Manager.SwitchDesktop(desktop);
}

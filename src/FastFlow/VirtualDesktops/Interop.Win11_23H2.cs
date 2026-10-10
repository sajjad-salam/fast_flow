using System.Runtime.InteropServices;

namespace FastFlow.VirtualDesktops.Win11_23H2;

// Undocumented shell interfaces for Windows 11 22H2 (22621.2215+) / 23H2.
// Only the vtable slots we call (and everything before them) are declared.

[ComImport, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktop
{
    bool IsViewVisible(IApplicationView view);
    Guid GetId();
}

[ComImport, Guid("A3175F2D-239C-4BD2-8AA0-EEBA8B0B138E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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
    public override string Name => "Windows 11 22H2 (22621.2215+) / 23H2";

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

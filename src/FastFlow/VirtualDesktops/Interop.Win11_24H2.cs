using System.Runtime.InteropServices;

namespace FastFlow.VirtualDesktops.Win11_24H2;

// Undocumented shell interfaces for Windows 11 24H2 and later.
// Only the vtable slots we call (and everything before them) are declared.

[ComImport, Guid("3F07F4BE-B107-441A-AF0F-39D82529072C"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IVirtualDesktop
{
    bool IsViewVisible(IApplicationView view);
    Guid GetId();
}

[ComImport, Guid("53F5CA0B-158F-4124-900C-057158060B27"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
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
    void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);
    IVirtualDesktop CreateDesktop();
}

internal sealed class Backend : DesktopBackendBase<IVirtualDesktopManagerInternal, IVirtualDesktop>
{
    public override string Name => "Windows 11 24H2 and later";

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

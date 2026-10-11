using Microsoft.Win32;

namespace FastFlow.VirtualDesktops;

/// <summary>
/// Reads and manipulates Windows virtual desktops.
///
/// Reading which desktop a window is on uses the documented IVirtualDesktopManager API and works everywhere.
/// Creating desktops and moving *other* applications' windows has no public API, so it goes through
/// undocumented shell COM interfaces whose IDs change between Windows builds (see Interop.*.cs).
/// Must be used from an STA thread (the WinForms UI thread).
/// </summary>
internal sealed class VirtualDesktopService
{
    private const string VirtualDesktopsKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\VirtualDesktops";

    private readonly IVirtualDesktopManager _manager;
    private readonly IDesktopBackend? _backend;

    public VirtualDesktopService()
    {
        var type = Type.GetTypeFromCLSID(ComGuids.CLSID_VirtualDesktopManager)!;
        _manager = (IVirtualDesktopManager)Activator.CreateInstance(type)!;

        try
        {
            _backend = CreateBackend();
        }
        catch (Exception ex)
        {
            BackendError = ex.Message;
        }
    }

    /// <summary>True when windows can be moved between desktops and desktops can be created.</summary>
    public bool CanMoveWindows => _backend != null;

    public string Status => _backend != null
        ? $"Virtual desktop support: {_backend.Name}"
        : $"Virtual desktop support: read-only ({BackendError ?? "unsupported Windows build"}) – windows will open on the current desktop";

    private string? BackendError { get; set; }

    private static IDesktopBackend? CreateBackend()
    {
        var build = Environment.OSVersion.Version.Build;
        var ubr = GetUpdateBuildRevision();

        if (build >= 26100)
            return new Win11_24H2.Backend();
        if (build >= 22631 || (build == 22621 && ubr >= 2215))
            return new Win11_23H2.Backend();
        if (build is >= 19041 and <= 19045)
            return new Win10.Backend();

        throw new PlatformNotSupportedException($"Windows build {build}.{ubr} is not supported for moving windows");
    }

    private static int GetUpdateBuildRevision()
    {
        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        return key?.GetValue("UBR") is int ubr ? ubr : 0;
    }

    /// <summary>Desktop IDs in the order shown in Task View.</summary>
    public List<Guid> GetDesktopIds()
    {
        if (_backend != null)
        {
            try
            {
                return _backend.GetDesktopIds();
            }
            catch
            {
                // fall back to the registry
            }
        }

        using var key = Registry.CurrentUser.OpenSubKey(VirtualDesktopsKey);
        if (key?.GetValue("VirtualDesktopIDs") is not byte[] bytes)
            return new List<Guid>();

        var ids = new List<Guid>();
        for (var i = 0; i + 16 <= bytes.Length; i += 16)
            ids.Add(new Guid(bytes.AsSpan(i, 16)));
        return ids;
    }

    public static string GetDesktopName(Guid id, int index)
    {
        using var key = Registry.CurrentUser.OpenSubKey($@"{VirtualDesktopsKey}\Desktops\{id:B}");
        return key?.GetValue("Name") as string is { Length: > 0 } name ? name : $"Desktop {index + 1}";
    }

    public Guid? GetWindowDesktopId(IntPtr hwnd)
    {
        try
        {
            var id = _manager.GetWindowDesktopId(hwnd);
            return id == Guid.Empty ? null : id;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Makes sure at least <paramref name="count"/> desktops exist. Returns the number created.</summary>
    public int EnsureDesktopCount(int count)
    {
        if (_backend == null)
            return 0;

        var created = 0;
        while (_backend.GetDesktopIds().Count < count)
        {
            _backend.CreateDesktop();
            created++;
        }
        return created;
    }

    /// <summary>Moves any top-level window to the desktop at <paramref name="index"/>. Returns false if not possible.</summary>
    public bool MoveWindowToDesktop(IntPtr hwnd, int index, out string? error)
    {
        error = null;
        var ids = GetDesktopIds();
        if (index < 0 || index >= ids.Count)
        {
            error = $"desktop {index + 1} does not exist";
            return false;
        }

        if (GetWindowDesktopId(hwnd) == ids[index])
            return true;

        if (_backend == null)
        {
            error = "moving windows between desktops is not supported on this Windows build";
            return false;
        }

        try
        {
            _backend.MoveWindowToDesktop(hwnd, ids[index]);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Makes the desktop at <paramref name="index"/> the active one. Returns false if not possible.</summary>
    public bool SwitchToDesktop(int index)
    {
        var ids = GetDesktopIds();
        if (_backend == null || index < 0 || index >= ids.Count)
            return false;

        try
        {
            _backend.SwitchToDesktop(ids[index]);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

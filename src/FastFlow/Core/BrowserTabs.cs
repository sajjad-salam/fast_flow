using System.Windows.Automation;

namespace FastFlow.Core;

/// <summary>
/// Reads the open tabs of a browser window through UI Automation, and builds the command line that
/// reopens them in a new window.
///
/// Browsers only show the address of the active tab, so each tab is selected in turn and the address
/// bar is read; the originally active tab is selected again at the end.
/// </summary>
internal static class BrowserTabs
{
    /// <summary>Process name → whether it is Chromium-based (accepts --new-window with several URLs).</summary>
    private static readonly Dictionary<string, bool> Browsers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["chrome"] = true, ["msedge"] = true, ["brave"] = true, ["vivaldi"] = true,
        ["opera"] = true, ["chromium"] = true, ["yandex"] = true, ["thorium"] = true,
        ["firefox"] = false, ["librewolf"] = false, ["waterfox"] = false, ["floorp"] = false, ["zen"] = false,
    };

    private const int MaxDepth = 30;
    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan TabSwitchTimeout = TimeSpan.FromSeconds(2);

    public static bool IsBrowser(string processName) => Browsers.ContainsKey(processName);

    /// <summary>URLs of the window's tabs in tab-strip order (empty if they cannot be read).</summary>
    public static List<string> Capture(IntPtr hwnd)
    {
        try
        {
            var root = AutomationElement.FromHandle(hwnd);
            var (tabs, addressBar) = FindTabsAndAddressBar(root);
            if (tabs.Count == 0 || addressBar == null)
            {
                // Chromium builds its accessibility tree on the first request; give it a moment.
                Thread.Sleep(500);
                (tabs, addressBar) = FindTabsAndAddressBar(root);
            }
            if (addressBar == null)
                return new List<string>();

            var urls = new List<string>();
            var active = tabs.FirstOrDefault(IsSelected);
            // The address bar shows the tab that is selected right now. Every tab other than that one
            // (including the originally active tab, once another has been selected) has to be selected
            // and its address waited for, or the previous tab's address is read again.
            var selected = active;
            var shown = ReadValue(addressBar);
            foreach (var tab in tabs)
            {
                if (!ReferenceEquals(tab, selected))
                {
                    if (!Select(tab))
                        continue;
                    selected = tab;
                    shown = WaitForAddress(tab, addressBar, shown);
                }
                if (Normalize(shown) is { } url)
                    urls.Add(url);
            }

            if (active != null)
                Select(active);

            // Tabs could not be switched: fall back to the active tab only.
            if (urls.Count == 0 && Normalize(ReadValue(addressBar)) is { } current)
                urls.Add(current);
            return urls;
        }
        catch
        {
            return new List<string>();
        }
    }

    /// <summary>Command-line arguments that open <paramref name="urls"/> as tabs of one new window.</summary>
    public static string BuildArguments(string processName, string arguments, IReadOnlyList<string> urls)
    {
        var quoted = string.Join(" ", urls.Select(u => $"\"{u}\""));
        var newWindow = Browsers.TryGetValue(processName, out var chromium) && chromium ? "--new-window " : "";
        return $"{arguments} {newWindow}{quoted}".Trim();
    }

    /// <summary>
    /// Keeps only the switches of a captured browser command line (e.g. --profile-directory), dropping
    /// URLs it was originally started with so they are not opened again next to the saved tabs.
    /// </summary>
    public static string KeepSwitches(string arguments)
    {
        var kept = new List<string>();
        foreach (var token in Tokenize(arguments))
        {
            if (token is "--" or "--single-argument")
                break;
            if (token.StartsWith('-'))
                kept.Add(token.Contains(' ') ? $"\"{token}\"" : token);
        }
        return string.Join(" ", kept);
    }

    private static IEnumerable<string> Tokenize(string commandLine)
    {
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
                inQuotes = !inQuotes;
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                    yield return current.ToString();
                current.Clear();
            }
            else
                current.Append(c);
        }
        if (current.Length > 0)
            yield return current.ToString();
    }

    /// <summary>Walks the browser's own UI (not the web pages) for its tabs and its address bar.</summary>
    private static (List<AutomationElement> Tabs, AutomationElement? AddressBar) FindTabsAndAddressBar(AutomationElement root)
    {
        var tabs = new List<AutomationElement>();
        AutomationElement? addressBar = null;
        var walker = TreeWalker.ControlViewWalker;

        void Visit(AutomationElement element, int depth)
        {
            if (depth > MaxDepth)
                return;
            for (var child = walker.GetFirstChild(element); child != null; child = walker.GetNextSibling(child))
            {
                var type = child.Current.ControlType;
                // Web page content: may contain its own tabs and text boxes.
                if (type == ControlType.Document)
                    continue;
                if (type == ControlType.TabItem)
                {
                    tabs.Add(child);
                    continue;
                }
                if (type == ControlType.Edit)
                {
                    addressBar ??= child;
                    continue;
                }
                Visit(child, depth + 1);
            }
        }

        Visit(root, 0);
        return (tabs, addressBar);
    }

    private static bool IsSelected(AutomationElement tab)
    {
        try
        {
            return tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                   && ((SelectionItemPattern)pattern).Current.IsSelected;
        }
        catch
        {
            return false;
        }
    }

    private static bool Select(AutomationElement tab)
    {
        try
        {
            if (tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var selection))
            {
                ((SelectionItemPattern)selection).Select();
                return true;
            }
            if (tab.TryGetCurrentPattern(InvokePattern.Pattern, out var invoke))
            {
                ((InvokePattern)invoke).Invoke();
                return true;
            }
        }
        catch
        {
            // tab closed or not selectable
        }
        return false;
    }

    /// <summary>
    /// Waits until the address bar follows the newly selected <paramref name="tab"/>: the tab reports
    /// itself selected and the address differs from <paramref name="previous"/> and has stopped changing.
    /// Two tabs can show the same address, so after a timeout the current value is taken as it is.
    /// </summary>
    private static string WaitForAddress(AutomationElement tab, AutomationElement addressBar, string previous)
    {
        var deadline = DateTime.UtcNow + TabSwitchTimeout;
        var last = previous;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(PollInterval);
            var value = ReadValue(addressBar);
            if (value != previous && value == last && IsSelectedOrUnknown(tab))
                return value;
            last = value;
        }
        return ReadValue(addressBar);
    }

    /// <summary>True if the tab is selected, or if it does not report its selection state at all.</summary>
    private static bool IsSelectedOrUnknown(AutomationElement tab)
    {
        try
        {
            return !tab.TryGetCurrentPattern(SelectionItemPattern.Pattern, out var pattern)
                   || ((SelectionItemPattern)pattern).Current.IsSelected;
        }
        catch
        {
            return true;
        }
    }

    private static string ReadValue(AutomationElement edit) =>
        edit.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern)
            ? ((ValuePattern)pattern).Current.Value
            : "";

    /// <summary>Turns the address bar text into an openable URL; null for new-tab and internal pages.</summary>
    private static string? Normalize(string value)
    {
        value = value.Trim();
        if (value.Length == 0 || value.Contains(' '))
            return null;
        if (value.Contains("://"))
            return value.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                   || value.StartsWith("file", StringComparison.OrdinalIgnoreCase)
                ? value
                : null; // chrome://, edge://, brave://, about: pages
        if (value.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
            return null;
        // Chromium hides "https://" in the address bar.
        return value.Contains('.') || value.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)
            ? "https://" + value
            : null;
    }
}

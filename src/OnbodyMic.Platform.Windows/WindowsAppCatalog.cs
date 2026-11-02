namespace OnbodyMic.Platform.Windows;

using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32;

/// <summary>Live capture roots, identified by executable path and process creation time.</summary>
public static class WindowsAppCatalog
{
    public sealed record AppInstance(uint ProcessId, string ExecutablePath)
    {
        public string DisplayName { get; init; } = Path.GetFileNameWithoutExtension(ExecutablePath);
        public string Category { get; init; } = "background";
        public long StartTimeTicks { get; init; }
    }

    public static IReadOnlyList<AppInstance> FindByExecutablePath(string executablePath)
    {
        var normalized = Path.GetFullPath(executablePath);
        return List().Where(app => string.Equals(app.ExecutablePath, normalized,
            StringComparison.OrdinalIgnoreCase)).ToArray();
    }

    /// <summary>
    /// Same-executable descendants belong to their oldest live ancestor, not to another
    /// independent instance of the same executable. Taskbar/tray evidence follows that tree.
    /// </summary>
    public static IReadOnlyList<AppInstance> List(CancellationToken cancellationToken = default)
    {
        var processes = new Dictionary<uint, AppInstance>();
        using var currentProcess = Process.GetCurrentProcess();
        var sessionId = currentProcess.SessionId;
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    if (process.SessionId != sessionId || process.HasExited
                        || process.MainModule?.FileName is not { Length: > 0 } path) continue;
                    processes.Add((uint)process.Id, new AppInstance((uint)process.Id, Path.GetFullPath(path))
                    {
                        StartTimeTicks = process.StartTime.ToUniversalTime().Ticks,
                    });
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
                {
                    // Inaccessible or exited processes cannot be safely selected.
                }
            }
        }

        var parents = ReadParents();
        var roots = new Dictionary<uint, uint>();
        foreach (var app in processes.Values)
        {
            var root = app;
            while (parents.TryGetValue(root.ProcessId, out var parentId)
                && processes.TryGetValue(parentId, out var parent)
                && parent.StartTimeTicks < root.StartTimeTicks
                && string.Equals(parent.ExecutablePath, root.ExecutablePath, StringComparison.OrdinalIgnoreCase))
            {
                root = parent;
            }
            roots.Add(app.ProcessId, root.ProcessId);
        }

        var windows = ReadTaskbarWindows();
        var trayOwners = ReadTrayOwners(processes.Values, roots);
        var result = new Dictionary<uint, AppInstance>();
        foreach (var app in processes.Values)
        {
            var rootId = roots[app.ProcessId];
            if (!result.TryGetValue(rootId, out var root)) root = processes[rootId];
            if (windows.TryGetValue(app.ProcessId, out var title))
            {
                // A real taskbar window outranks notification-area evidence.
                if (root.Category != "taskbar" || app.ProcessId == rootId
                    || (!windows.ContainsKey(rootId) && StringComparer.OrdinalIgnoreCase.Compare(title, root.DisplayName) < 0))
                    root = root with { Category = "taskbar", DisplayName = title };
            }
            else if (root.Category == "background" && trayOwners.Contains(app.ProcessId))
            {
                root = root with { Category = "tray" };
            }
            result[rootId] = root;
        }
        return result.Values.OrderBy(app => CategoryOrder(app.Category))
            .ThenBy(app => app.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(app => app.ProcessId).ToArray();
    }

    public static AppInstance ReadLive(uint processId)
    {
        using var process = Process.GetProcessById(checked((int)processId));
        if (process.HasExited) throw new InvalidOperationException("selected application process has exited");
        var path = process.MainModule?.FileName;
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("selected executable path is inaccessible");
        return new AppInstance(processId, Path.GetFullPath(path))
        {
            StartTimeTicks = process.StartTime.ToUniversalTime().Ticks,
        };
    }

    public static AppInstance ResolveCaptureRoot(AppInstance selected)
    {
        var parents = ReadParents();
        var root = selected;
        while (parents.TryGetValue(root.ProcessId, out var parentId) && parentId != 0)
        {
            AppInstance parent;
            try { parent = ReadLive(parentId); }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or ArgumentException or NotSupportedException)
            {
                break;
            }
            if (parent.StartTimeTicks >= root.StartTimeTicks
                || !string.Equals(parent.ExecutablePath, root.ExecutablePath, StringComparison.OrdinalIgnoreCase)) break;
            root = parent;
        }
        return root;
    }

    private static int CategoryOrder(string category) => category switch { "taskbar" => 0, "tray" => 1, _ => 2 };

    private static Dictionary<uint, uint> ReadParents()
    {
        var snapshot = Native.CreateToolhelp32Snapshot(2, 0); // TH32CS_SNAPPROCESS
        if (snapshot == new IntPtr(-1)) throw new Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var parents = new Dictionary<uint, uint>();
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            if (!Native.Process32First(snapshot, ref entry)) throw new Win32Exception(Marshal.GetLastWin32Error());
            do { parents[entry.ProcessId] = entry.ParentProcessId; }
            while (Native.Process32Next(snapshot, ref entry));
            return parents;
        }
        finally { Native.CloseHandle(snapshot); }
    }

    private static Dictionary<uint, string> ReadTaskbarWindows()
    {
        var titles = new Dictionary<uint, string>();
        Native.EnumWindows((window, _) =>
        {
            if (!Native.IsWindowVisible(window)) return true;
            var className = new StringBuilder(128);
            Native.GetClassName(window, className, className.Capacity);
            var windowClass = className.ToString();
            if (windowClass is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
                or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland") return true;
            var style = Native.GetWindowLong(window, -20);
            var explicitlyApp = (style & 0x40000) != 0; // WS_EX_APPWINDOW
            if ((style & 0x80) != 0 || (!explicitlyApp && ((style & 0x08000000) != 0
                || Native.GetWindow(window, 4) != IntPtr.Zero))) return true; // TOOLWINDOW, NOACTIVATE, GW_OWNER
            if (Native.DwmGetWindowAttribute(window, 14, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
            var text = new StringBuilder(512);
            if (Native.GetWindowText(window, text, text.Capacity) == 0) return true;
            Native.GetWindowThreadProcessId(window, out var pid);
            if (windowClass == "ApplicationFrameWindow")
            {
                var framePid = pid;
                Native.EnumChildWindows(window, (child, _) =>
                {
                    className.Clear();
                    Native.GetClassName(child, className, className.Capacity);
                    Native.GetWindowThreadProcessId(child, out var childPid);
                    if (className.ToString() == "Windows.UI.Core.CoreWindow" && childPid != framePid) pid = childPid;
                    return true;
                }, IntPtr.Zero);
                if (pid == framePid) return true; // An unresolved frame host is not the app's audio root.
            }
            var title = text.ToString();
            // Multiple eligible windows use a stable title, not EnumWindows' z-order.
            if (!titles.TryGetValue(pid, out var previous)
                || StringComparer.OrdinalIgnoreCase.Compare(title, previous) < 0) titles[pid] = title;
            return true;
        }, IntPtr.Zero);
        return titles;
    }

    private static HashSet<uint> ReadTrayOwners(IEnumerable<AppInstance> processes, Dictionary<uint, uint> roots)
    {
        var owners = new HashSet<uint>();
        var tooltipPaths = ReadTrayTooltipPaths();
        var pathsToPids = processes.GroupBy(app => app.ExecutablePath, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(app => roots[app.ProcessId]).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        Native.EnumWindows((window, _) =>
        {
            var name = new StringBuilder(128);
            Native.GetClassName(window, name, name.Capacity);
            var shellClass = name.ToString();
            if (shellClass is not ("Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")) return true;
            if (shellClass is "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland")
                ReadAccessibleTrayOwners(window, tooltipPaths, pathsToPids, owners);
            Native.EnumChildWindows(window, (child, _) =>
            {
                name.Clear();
                Native.GetClassName(child, name, name.Capacity);
                if (name.ToString() == "TrayNotifyWnd") ReadAccessibleTrayOwners(child, tooltipPaths, pathsToPids, owners);
                if (name.ToString() == "ToolbarWindow32" && IsNotificationToolbar(child)) ReadToolbarOwners(child, owners);
                return true;
            }, IntPtr.Zero);
            return true;
        }, IntPtr.Zero);
        // A registry entry alone, or a hidden window alone, is never a live tray icon.
        // If the shell does not expose owner HWNDs or a uniquely attributable live
        // accessible icon, the process stays in background rather than being guessed.
        return owners;
    }

    private static bool IsNotificationToolbar(IntPtr toolbar)
    {
        var name = new StringBuilder(128);
        for (var parent = Native.GetParent(toolbar); parent != IntPtr.Zero; parent = Native.GetParent(parent))
        {
            name.Clear();
            Native.GetClassName(parent, name, name.Capacity);
            if (name.ToString() is "TrayNotifyWnd" or "NotifyIconOverflowWindow" or "TopLevelWindowForOverflowXamlIsland") return true;
        }
        return false;
    }

    private static Dictionary<string, string> ReadTrayTooltipPaths()
    {
        var paths = new Dictionary<string, string>(StringComparer.Ordinal);
        var ambiguous = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            using var settings = Registry.CurrentUser.OpenSubKey(@"Control Panel\NotifyIconSettings");
            if (settings is null) return paths;
            foreach (var keyName in settings.GetSubKeyNames())
            {
                using var icon = settings.OpenSubKey(keyName);
                if (icon?.GetValue("InitialTooltip") is not string { Length: > 0 } tooltip
                    || icon.GetValue("ExecutablePath") is not string { Length: > 0 } executable) continue;
                string path;
                try { path = Path.GetFullPath(Environment.ExpandEnvironmentVariables(executable)); }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException) { continue; }
                if (paths.TryGetValue(tooltip, out var previous)
                    && !string.Equals(path, previous, StringComparison.OrdinalIgnoreCase)) ambiguous.Add(tooltip);
                else paths[tooltip] = path;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            // Shell settings can be inaccessible; toolbar ownership remains independent evidence.
        }
        foreach (var tooltip in ambiguous) paths.Remove(tooltip);
        return paths;
    }

    private static void ReadAccessibleTrayOwners(IntPtr window, Dictionary<string, string> tooltipPaths,
        Dictionary<string, uint[]> pathsToPids, HashSet<uint> owners)
    {
        if (tooltipPaths.Count == 0) return;
        var iid = typeof(IAccessibleCom).GUID;
        if (Native.AccessibleObjectFromWindow(window, 0xFFFFFFFC, ref iid, out var accessible) < 0 || accessible is null) return;
        var remaining = 2048;
        try { VisitAccessible(accessible, 0, tooltipPaths, pathsToPids, owners, ref remaining); }
        finally { Marshal.ReleaseComObject(accessible); }
    }

    private static void VisitAccessible(IAccessibleCom accessible, int depth, Dictionary<string, string> tooltipPaths,
        Dictionary<string, uint[]> pathsToPids, HashSet<uint> owners, ref int remaining)
    {
        if (depth > 16 || remaining-- <= 0) return;
        ReadAccessibleIcon(accessible, 0, tooltipPaths, pathsToPids, owners);
        if (accessible.GetChildCount(out var count) < 0 || count <= 0) return;
        var children = new object[Math.Min(count, Math.Max(0, remaining))];
        if (children.Length == 0 || Native.AccessibleChildren(accessible, 0, children.Length, children, out var obtained) < 0) return;
        for (var i = 0; i < obtained; i++)
        {
            if (children[i] is IAccessibleCom child)
            {
                try { VisitAccessible(child, depth + 1, tooltipPaths, pathsToPids, owners, ref remaining); }
                finally { Marshal.ReleaseComObject(child); }
            }
            else if (children[i] is int childId)
            {
                if (remaining-- > 0) ReadAccessibleIcon(accessible, childId, tooltipPaths, pathsToPids, owners);
            }
        }
    }

    private static void ReadAccessibleIcon(IAccessibleCom accessible, int childId, Dictionary<string, string> tooltipPaths,
        Dictionary<string, uint[]> pathsToPids, HashSet<uint> owners)
    {
        if (accessible.GetRole(childId, out var role) < 0 || role is not int roleId || roleId is not (0x2B or 0x28)
            || accessible.GetName(childId, out var name) < 0 || name is null
            || !tooltipPaths.TryGetValue(name, out var path) || !pathsToPids.TryGetValue(path, out var pids)
            || pids.Length != 1) return; // A tooltip/path cannot disambiguate independent live instances.
        foreach (var pid in pids) owners.Add(pid);
    }

    // IAccessible's dual-interface vtable prefix through accRole. HRESULTs allow
    // disappearing shell elements to be ignored without losing other evidence.
    [ComImport, Guid("618736E0-3C3D-11CF-810C-00AA00389B71"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface IAccessibleCom
    {
        [PreserveSig] int GetParent([MarshalAs(UnmanagedType.IDispatch)] out object? parent);
        [PreserveSig] int GetChildCount(out int count);
        [PreserveSig] int GetChild([MarshalAs(UnmanagedType.Struct)] object childId, [MarshalAs(UnmanagedType.IDispatch)] out object? child);
        [PreserveSig] int GetName([MarshalAs(UnmanagedType.Struct)] object childId, [MarshalAs(UnmanagedType.BStr)] out string? name);
        [PreserveSig] int GetValue([MarshalAs(UnmanagedType.Struct)] object childId, [MarshalAs(UnmanagedType.BStr)] out string? value);
        [PreserveSig] int GetDescription([MarshalAs(UnmanagedType.Struct)] object childId, [MarshalAs(UnmanagedType.BStr)] out string? description);
        [PreserveSig] int GetRole([MarshalAs(UnmanagedType.Struct)] object childId, [MarshalAs(UnmanagedType.Struct)] out object? role);
    }

    private static void ReadToolbarOwners(IntPtr toolbar, HashSet<uint> owners)
    {
        if (!Native.SendMessageTimeout(toolbar, 0x418, IntPtr.Zero, IntPtr.Zero, 2, 150, out var count)) return; // TB_BUTTONCOUNT
        Native.GetWindowThreadProcessId(toolbar, out var shellPid);
        var shell = Native.OpenProcess(0x1038, false, shellPid); // QUERY_LIMITED_INFORMATION | VM_OPERATION | VM_READ | VM_WRITE
        if (shell == IntPtr.Zero) return;
        IntPtr remote = IntPtr.Zero;
        try
        {
            // TBBUTTON layout is pointer-sized; do not interpret a foreign-bitness layout.
            if (!Native.IsWow64Process(shell, out var shellWow64)
                || !Native.IsWow64Process(Native.GetCurrentProcess(), out var selfWow64) || shellWow64 != selfWow64) return;
            var buttonSize = IntPtr.Size == 8 ? 32 : 20;
            remote = Native.VirtualAllocEx(shell, IntPtr.Zero, (nuint)buttonSize, 0x3000, 4);
            if (remote == IntPtr.Zero) return;
            var button = new byte[buttonSize];
            var ownerData = new byte[IntPtr.Size];
            var buttons = (int)Math.Min(count.ToUInt64(), 4096UL);
            for (var i = 0; i < buttons; i++)
            {
                if (!Native.SendMessageTimeout(toolbar, 0x417, (IntPtr)i, remote, 2, 150, out var ok) || ok == 0
                    || !Native.ReadProcessMemory(shell, remote, button, (nuint)button.Length, out var read) || read != (nuint)button.Length) continue;
                var dataOffset = IntPtr.Size == 8 ? 16 : 12;
                var data = IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(button, dataOffset) : (IntPtr)BitConverter.ToInt32(button, dataOffset);
                if (data == IntPtr.Zero || !Native.ReadProcessMemory(shell, data, ownerData, (nuint)ownerData.Length, out read)
                    || read != (nuint)ownerData.Length) continue;
                var owner = IntPtr.Size == 8 ? (IntPtr)BitConverter.ToInt64(ownerData) : (IntPtr)BitConverter.ToInt32(ownerData);
                if (!Native.IsWindow(owner)) continue;
                Native.GetWindowThreadProcessId(owner, out var ownerPid);
                if (ownerPid != shellPid) owners.Add(ownerPid);
            }
        }
        finally
        {
            if (remote != IntPtr.Zero) Native.VirtualFreeEx(shell, remote, 0, 0x8000);
            Native.CloseHandle(shell);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string ExecutableFile;
    }

    private static class Native
    {
        public delegate bool EnumWindowProc(IntPtr window, IntPtr parameter);
        [DllImport("oleacc.dll")] public static extern int AccessibleObjectFromWindow(IntPtr window, uint objectId, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IAccessibleCom? accessible);
        [DllImport("oleacc.dll")] public static extern int AccessibleChildren(IAccessibleCom accessible, int start, int count,
            [Out, MarshalAs(UnmanagedType.LPArray, ArraySubType = UnmanagedType.Struct, SizeParamIndex = 2)] object[] children, out int obtained);
        [DllImport("kernel32.dll", SetLastError = true)] public static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint processId);
        [DllImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)] public static extern bool Process32First(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", EntryPoint = "Process32NextW")] public static extern bool Process32Next(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr handle);
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool EnumChildWindows(IntPtr parent, EnumWindowProc callback, IntPtr parameter);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr window);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr window, int index);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr window, uint command);
        [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr window);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr window, StringBuilder text, int capacity);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr window, StringBuilder name, int capacity);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr window, uint attribute, out int value, int size);
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "SendMessageTimeoutW")]
        [return: MarshalAs(UnmanagedType.Bool)] public static extern bool SendMessageTimeout(IntPtr window, uint message, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out nuint result);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
        [DllImport("kernel32.dll")] public static extern IntPtr GetCurrentProcess();
        [DllImport("kernel32.dll")] public static extern bool IsWow64Process(IntPtr process, out bool wow64);
        [DllImport("kernel32.dll")] public static extern IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nuint size, uint allocationType, uint protection);
        [DllImport("kernel32.dll")] public static extern bool VirtualFreeEx(IntPtr process, IntPtr address, nuint size, uint freeType);
        [DllImport("kernel32.dll")] public static extern bool ReadProcessMemory(IntPtr process, IntPtr address, byte[] buffer, nuint size, out nuint bytesRead);
    }
}

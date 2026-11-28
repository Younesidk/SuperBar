using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace SuperBar.Windows.Interop;

#pragma warning disable IDE1006 // Win32 naming

/// <summary>Shell COM interop for the Windows taskbar pins, app ids, shell icons and packaged-app activation.</summary>
internal static class ShellInterop
{
    public static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    public static readonly Guid IID_IPropertyStore = new("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
    public static readonly Guid CLSID_ShellLink = new("00021401-0000-0000-C000-000000000046");
    public static readonly Guid CLSID_ApplicationActivationManager = new("45BA127D-10A8-46EA-8AB7-56EA9078943C");

    /// <summary>System.AppUserModel.ID.</summary>
    public static PROPERTYKEY PKEY_AppUserModel_ID => new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    public const uint SIGDN_NORMALDISPLAY = 0x00000000;
    public const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    public const uint SIGDN_DESKTOPABSOLUTEPARSING = 0x80028000;
    public const uint SIGDN_FILESYSPATH = 0x80058000;

    public const int SIIGBF_RESIZETOFIT = 0x00;
    public const int SIIGBF_ICONONLY = 0x04;

    public const int GPS_DEFAULT = 0;
    public const ushort VT_LPWSTR = 31;
    public const int STGM_READ = 0;
    public const int AO_NONE = 0;

    public const int ERROR_SUCCESS = 0;
    public const int APPMODEL_ERROR_NO_APPLICATION = 15703;

    public const uint REG_NOTIFY_CHANGE_NAME = 0x1;
    public const uint REG_NOTIFY_CHANGE_LAST_SET = 0x4;

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHCreateItemFromIDList(nint pidl, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    public static extern int SHCreateItemFromParsingName(string pszPath, nint pbc, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHGetNameFromIDList(nint pidl, uint sigdnName, out nint ppszName);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHGetPropertyStoreFromIDList(nint pidl, int flags, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    [DllImport("shell32.dll", PreserveSig = true)]
    public static extern int SHGetPropertyStoreForWindow(nint hwnd, in Guid riid, [MarshalAs(UnmanagedType.Interface)] out IPropertyStore ppv);

    [DllImport("ole32.dll", PreserveSig = true)]
    public static extern int PropVariantClear(ref PROPVARIANT pvar);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetApplicationUserModelId(nint hProcess, ref uint applicationUserModelIdLength, char[]? applicationUserModelId);

    [DllImport("advapi32.dll")]
    public static extern int RegNotifyChangeKeyValue(SafeRegistryHandle hKey, [MarshalAs(UnmanagedType.Bool)] bool bWatchSubtree, uint dwNotifyFilter, SafeWaitHandle hEvent, [MarshalAs(UnmanagedType.Bool)] bool fAsynchronous);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern nint FindWindowEx(nint hWndParent, nint hWndChildAfter, string? lpszClass, string? lpszWindow);

    /// <summary>A shell name (SHGetNameFromIDList), or null.</summary>
    public static string? GetName(nint pidl, uint sigdn)
    {
        if (SHGetNameFromIDList(pidl, sigdn, out var name) != 0 || name == 0)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStringUni(name);
        }
        finally
        {
            Marshal.FreeCoTaskMem(name);
        }
    }

    /// <summary>A string property from a property store, or null.</summary>
    public static string? GetString(IPropertyStore store, PROPERTYKEY key)
    {
        var value = default(PROPVARIANT);
        try
        {
            if (store.GetValue(in key, out value) != 0 || value.vt != VT_LPWSTR || value.p == 0)
            {
                return null;
            }

            var text = Marshal.PtrToStringUni(value.p);
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        finally
        {
            PropVariantClear(ref value);
        }
    }

    public static void Release(object? com)
    {
        if (com is not null && Marshal.IsComObject(com))
        {
            Marshal.FinalReleaseComObject(com);
        }
    }
}

[StructLayout(LayoutKind.Sequential)]
internal readonly struct PROPERTYKEY(Guid fmtid, uint pid)
{
    public readonly Guid fmtid = fmtid;
    public readonly uint pid = pid;
}

/// <summary>Enough of PROPVARIANT to read strings: 16 bytes on x86, 24 on 64-bit, like the native union.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PROPVARIANT
{
    public ushort vt;
    public ushort wReserved1;
    public ushort wReserved2;
    public ushort wReserved3;
    public nint p;
    public nint p2;
}

[ComImport]
[Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IPropertyStore
{
    [PreserveSig]
    int GetCount(out uint cProps);

    [PreserveSig]
    int GetAt(uint iProp, out PROPERTYKEY pkey);

    [PreserveSig]
    int GetValue(in PROPERTYKEY key, out PROPVARIANT pv);

    [PreserveSig]
    int SetValue(in PROPERTYKEY key, in PROPVARIANT propvar);

    [PreserveSig]
    int Commit();
}

[ComImport]
[Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellItemImageFactory
{
    [PreserveSig]
    int GetImage(SIZE size, int flags, out nint phbm);
}

[ComImport]
[Guid("000214F9-0000-0000-C000-000000000046")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IShellLinkW
{
    [PreserveSig]
    int GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cch, nint pfd, uint fFlags);

    [PreserveSig]
    int GetIDList(out nint ppidl);

    [PreserveSig]
    int SetIDList(nint pidl);

    [PreserveSig]
    int GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cch);

    [PreserveSig]
    int SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

    [PreserveSig]
    int GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cch);

    [PreserveSig]
    int SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

    [PreserveSig]
    int GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cch);

    [PreserveSig]
    int SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

    [PreserveSig]
    int GetHotkey(out ushort pwHotkey);

    [PreserveSig]
    int SetHotkey(ushort wHotkey);

    [PreserveSig]
    int GetShowCmd(out int piShowCmd);

    [PreserveSig]
    int SetShowCmd(int iShowCmd);

    [PreserveSig]
    int GetIconLocation([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cch, out int piIcon);

    [PreserveSig]
    int SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);

    [PreserveSig]
    int SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);

    [PreserveSig]
    int Resolve(nint hwnd, uint fFlags);

    [PreserveSig]
    int SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
}

[ComImport]
[Guid("2e941141-7f97-4756-ba1d-9decde894a3d")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IApplicationActivationManager
{
    [PreserveSig]
    int ActivateApplication([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, [MarshalAs(UnmanagedType.LPWStr)] string? arguments, int options, out uint processId);

    [PreserveSig]
    int ActivateForFile([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, nint itemArray, [MarshalAs(UnmanagedType.LPWStr)] string? verb, out uint processId);

    [PreserveSig]
    int ActivateForProtocol([MarshalAs(UnmanagedType.LPWStr)] string appUserModelId, nint itemArray, out uint processId);
}

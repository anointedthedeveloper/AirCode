using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AirCode.Helpers;

/// <summary>
/// Uses the Windows IFileOpenDialog COM interface for folder selection.
/// No dependency on System.Windows.Forms.
/// </summary>
public static class FolderPicker
{
    public static string? PickFolder(string initialPath = "", Window? owner = null)
    {
        try
        {
            var dialog = (IFileOpenDialog)new FileOpenDialog();
            dialog.SetOptions(FOS.FOS_PICKFOLDERS | FOS.FOS_FORCEFILESYSTEM);

            if (!string.IsNullOrEmpty(initialPath) && Directory.Exists(initialPath))
            {
                var iid = typeof(IShellItem).GUID;
                SHCreateItemFromParsingName(initialPath, IntPtr.Zero, ref iid, out var initItem);
                if (initItem != null) dialog.SetFolder(initItem);
            }

            var hwnd = owner != null
                ? new WindowInteropHelper(owner).Handle
                : IntPtr.Zero;

            var hr = dialog.Show(hwnd);
            if (hr != 0) return null; // Cancelled

            dialog.GetResult(out var result);
            result.GetDisplayName(SIGDN.SIGDN_FILESYSPATH, out var path);
            return path;
        }
        catch
        {
            return null;
        }
    }

    // ── COM Interop ───────────────────────────────────────────────────────────

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string pszPath, IntPtr pbc, ref Guid riid, out IShellItem? ppv);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr hwndOwner);
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(FOS fos);
        void GetOptions(out FOS pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetFileName([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetResult(out IShellItem ppsi);
        void AddPlace(IShellItem psi, int alignment);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void Close([MarshalAs(UnmanagedType.Error)] int hr);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);
        void GetResults(out IntPtr ppenum);
        void GetSelectedItems(out IntPtr ppenum);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(SIGDN sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    [Flags]
    private enum FOS : uint
    {
        FOS_PICKFOLDERS = 0x20,
        FOS_FORCEFILESYSTEM = 0x40
    }

    private enum SIGDN : uint
    {
        SIGDN_FILESYSPATH = 0x80058000
    }
}

using System;
using System.Runtime.InteropServices;

namespace Monoview.Setup;

/// <summary>
/// The Windows folder picker (IFileOpenDialog with FOS_PICKFOLDERS), the same one File Explorer uses.
/// .NET Framework's FolderBrowserDialog shows the old tree dialog instead.
/// </summary>
internal static class FolderPicker
{
    private const uint FosPickFolders = 0x20, FosForceFileSystem = 0x40, SigdnFileSysPath = 0x80058000;

    /// <summary>Returns the chosen folder, or null if the user cancelled.</summary>
    public static string? Pick(IntPtr owner, string title, string startFolder)
    {
        var dialog = (IFileOpenDialog)new FileOpenDialog();
        try
        {
            dialog.SetOptions(FosPickFolders | FosForceFileSystem);
            dialog.SetTitle(title);
            // Start in the nearest folder that exists, since the default install folder usually does not yet.
            for (string? dir = startFolder; !string.IsNullOrEmpty(dir); dir = System.IO.Path.GetDirectoryName(dir))
            {
                if (!System.IO.Directory.Exists(dir)) continue;
                if (SHCreateItemFromParsingName(dir!, IntPtr.Zero, typeof(IShellItem).GUID, out var folder) == 0)
                    dialog.SetFolder(folder);
                break;
            }
            if (dialog.Show(owner) != 0) return null;
            dialog.GetResult(out var item);
            item.GetDisplayName(SigdnFileSysPath, out var path);
            return path;
        }
        finally
        {
            Marshal.ReleaseComObject(dialog);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid iid, out IShellItem item);

    [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
    private class FileOpenDialog { }

    // Methods in vtable order; only the ones used have real signatures.
    [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileOpenDialog
    {
        [PreserveSig] int Show(IntPtr owner);
        void SetFileTypes();
        void SetFileTypeIndex();
        void GetFileTypeIndex();
        void Advise();
        void Unadvise();
        void SetOptions(uint options);
        void GetOptions();
        void SetDefaultFolder();
        void SetFolder(IShellItem item);
        void GetFolder();
        void GetCurrentSelection();
        void SetFileName();
        void GetFileName();
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
        void SetOkButtonLabel();
        void SetFileNameLabel();
        void GetResult(out IShellItem item);
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler();
        void GetParent();
        void GetDisplayName(uint type, [MarshalAs(UnmanagedType.LPWStr)] out string name);
    }
}

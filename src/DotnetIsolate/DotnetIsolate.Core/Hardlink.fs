module DotnetIsolate.Core.Hardlink

open System
open System.Runtime.InteropServices

module private Native =
    [<DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)>]
    extern bool CreateHardLinkW(string lpFileName, string lpExistingFileName, IntPtr lpSecurityAttributes)

    [<DllImport("libc", SetLastError = true, EntryPoint = "link")>]
    extern int LinkUnix(string oldpath, string newpath)

/// Attempts to create a hardlink at `destination` pointing at `source`'s data (same file content,
/// two directory entries - not a copy). Ok on success; Error with the OS error code on failure
/// (e.g. cross-device link, unsupported filesystem, permissions). NTFS/ReFS on Windows and most
/// Linux/macOS filesystems support this the same way - see DESIGN.md's "Link strategy detail" for
/// why this is a filesystem capability, not an OS-based branch.
let create (source: string) (destination: string) : Result<unit, int> =
    let success =
        if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
            Native.CreateHardLinkW(destination, source, IntPtr.Zero)
        else
            Native.LinkUnix(source, destination) = 0

    if success then Ok() else Error(Marshal.GetLastWin32Error())

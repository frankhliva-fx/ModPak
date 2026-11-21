namespace ModPak.App

open System
open System.Collections.Concurrent
open System.Drawing
open System.Drawing.Imaging
open System.IO
open System.Runtime.InteropServices
open Avalonia.Media

/// Pluggable shell / theme icon source — swap per OS (Windows Shell, Linux theme, …).
type IFileIconProvider =
    /// Resolve a small icon for a file path or extension (e.g. "a.png" / ".png").
    abstract GetIcon: pathOrExtension: string -> IImage
    /// Folder / directory icon (Explorer-style).
    abstract GetFolderIcon: unit -> IImage
    /// Drive / volume icon for a root path (e.g. "C:\\") — Windows shell type.
    abstract GetDriveIcon: driveRootPath: string -> IImage

/// List row for the Explorer panel (file or directory).
type FileListItem
    (
        icon: IImage,
        name: string,
        mime: string,
        size: string,
        codec: byte,
        pathKey: string,
        canChangeCodec: bool,
        isDirectory: bool,
        isParent: bool
    ) =
    member _.Icon = icon
    member _.Name = name
    member _.Mime = mime
    member _.Size = size
    member _.Codec = codec
    member _.PathKey = pathKey
    member _.IsDirectory = isDirectory
    member _.IsParent = isParent
    member _.CanChangeCodec = canChangeCodec && not isDirectory && not isParent
    member _.HasMime = not isDirectory && not isParent && not (String.IsNullOrWhiteSpace mime)
    member _.ShowCodec = canChangeCodec && not isDirectory && not isParent
    member _.CodecLabel =
        match codec with
        | 0uy -> "none"
        | 1uy -> "lzma"
        | 2uy -> "lz4"
        | 3uy -> "zstd"
        | c -> string c

module FileListFormatting =

    let size (bytes: int) =
        let n = float bytes
        if bytes >= 1024 * 1024 then $"%.1f{n / 1024.0 / 1024.0} MB"
        elif bytes >= 1024 then $"%.1f{n / 1024.0} KB"
        else $"{bytes} B"

    let mime (value: string) =
        if String.IsNullOrWhiteSpace value then ""
        else value.Trim()

    let nextCodec (codec: byte) =
        match codec with
        | 2uy -> 1uy // lz4 -> lzma
        | 1uy -> 3uy // lzma -> zstd
        | 3uy -> 0uy // zstd -> none
        | _ -> 2uy // none/other -> lz4


module private Shell32 =

    [<Literal>]
    let SHGFI_ICON = 0x000000100u

    [<Literal>]
    let SHGFI_SMALLICON = 0x000000001u

    [<Literal>]
    let SHGFI_USEFILEATTRIBUTES = 0x000000010u

    [<Literal>]
    let FILE_ATTRIBUTE_NORMAL = 0x00000080u

    [<Literal>]
    let FILE_ATTRIBUTE_DIRECTORY = 0x00000010u

    [<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
    type SHFILEINFO =
        val mutable hIcon: nativeint
        val mutable iIcon: int
        val mutable dwAttributes: uint32
        [<MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)>]
        val mutable szDisplayName: string
        [<MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)>]
        val mutable szTypeName: string

    [<DllImport("shell32.dll", CharSet = CharSet.Unicode)>]
    extern nativeint SHGetFileInfo(
        string pszPath,
        uint32 dwFileAttributes,
        SHFILEINFO& psfi,
        uint32 cbFileInfo,
        uint32 uFlags)

    [<DllImport("user32.dll", SetLastError = true)>]
    extern bool DestroyIcon(nativeint hIcon)

    [<Literal>]
    let SEE_MASK_INVOKEIDLIST = 0x0000000Cu

    [<Literal>]
    let SW_SHOW = 5

    [<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
    type SHELLEXECUTEINFO =
        val mutable cbSize: int
        val mutable fMask: uint32
        val mutable hwnd: nativeint
        [<MarshalAs(UnmanagedType.LPWStr)>]
        val mutable lpVerb: string
        [<MarshalAs(UnmanagedType.LPWStr)>]
        val mutable lpFile: string
        [<MarshalAs(UnmanagedType.LPWStr)>]
        val mutable lpParameters: string
        [<MarshalAs(UnmanagedType.LPWStr)>]
        val mutable lpDirectory: string
        val mutable nShow: int
        val mutable hInstApp: nativeint
        val mutable lpIDList: nativeint
        [<MarshalAs(UnmanagedType.LPWStr)>]
        val mutable lpClass: string
        val mutable hkeyClass: nativeint
        val mutable dwHotKey: uint32
        val mutable hIconOrMonitor: nativeint
        val mutable hProcess: nativeint

    [<DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool ShellExecuteEx(SHELLEXECUTEINFO& lpExecInfo)

/// Open the Windows shell Properties dialog for a file or folder.
module ShellFileProperties =

    let tryShow (path: string) : bool =
        if not (OperatingSystem.IsWindows()) then
            false
        elif String.IsNullOrWhiteSpace path then
            false
        elif not (File.Exists path || Directory.Exists path) then
            false
        else
            try
                let mutable info = Shell32.SHELLEXECUTEINFO()
                info.cbSize <- Marshal.SizeOf<Shell32.SHELLEXECUTEINFO>()
                info.fMask <- Shell32.SEE_MASK_INVOKEIDLIST
                info.hwnd <- nativeint 0
                info.lpVerb <- "properties"
                info.lpFile <- path
                info.lpParameters <- null
                info.lpDirectory <- null
                info.nShow <- Shell32.SW_SHOW
                info.hInstApp <- nativeint 0
                info.lpIDList <- nativeint 0
                info.lpClass <- null
                info.hkeyClass <- nativeint 0
                info.dwHotKey <- 0u
                info.hIconOrMonitor <- nativeint 0
                info.hProcess <- nativeint 0
                Shell32.ShellExecuteEx(&info)
            with _ ->
                false

/// Windows Explorer icons via SHGetFileInfo (Windows only).
type WindowsShellFileIconProvider() =
    let cache = ConcurrentDictionary<string, IImage>(StringComparer.OrdinalIgnoreCase)
    let driveCache = ConcurrentDictionary<string, IImage>(StringComparer.OrdinalIgnoreCase)
    let mutable folderIcon: IImage = null
    let mutable folderIconLoaded = false

    let probePath (pathOrExtension: string) =
        let value = if isNull pathOrExtension then "" else pathOrExtension.Trim()
        if value = "" then "file"
        elif value.StartsWith(".", StringComparison.Ordinal) then "file" + value
        elif value.Contains('.') then value
        else "file." + value

    let extensionKey (pathOrExtension: string) =
        let value = if isNull pathOrExtension then "" else pathOrExtension.Trim()
        if value = "" then ""
        elif value.StartsWith(".", StringComparison.Ordinal) then value.ToLowerInvariant()
        else
            let ext = Path.GetExtension value
            if String.IsNullOrEmpty ext then "" else ext.ToLowerInvariant()

    let loadIconWithFlags (probe: string) (attrs: uint32) (flags: uint32) : IImage =
        let mutable info = Shell32.SHFILEINFO()
        let handle =
            Shell32.SHGetFileInfo(
                probe,
                attrs,
                &info,
                uint32 (Marshal.SizeOf<Shell32.SHFILEINFO>()),
                flags)
        if handle = nativeint 0 || info.hIcon = nativeint 0 then
            null
        else
            try
                try
                    use icon = Icon.FromHandle info.hIcon
                    use gdi = icon.ToBitmap()
                    use ms = new MemoryStream()
                    gdi.Save(ms, ImageFormat.Png)
                    ms.Position <- 0L
                    new Avalonia.Media.Imaging.Bitmap(ms) :> IImage
                with _ ->
                    null
            finally
                Shell32.DestroyIcon info.hIcon |> ignore

    let loadIcon (probe: string) (attrs: uint32) : IImage =
        let flags = Shell32.SHGFI_ICON ||| Shell32.SHGFI_SMALLICON ||| Shell32.SHGFI_USEFILEATTRIBUTES
        loadIconWithFlags probe attrs flags

    let load (pathOrExtension: string) : IImage =
        loadIcon (probePath pathOrExtension) Shell32.FILE_ATTRIBUTE_NORMAL

    let normalizeDriveRoot (driveRootPath: string) =
        let value = if isNull driveRootPath then "" else driveRootPath.Trim()
        if value = "" then ""
        else
            try
                Path.GetPathRoot(Path.GetFullPath value)
                |> fun root -> if isNull root then "" else root
            with _ ->
                if value.EndsWith("\\", StringComparison.Ordinal) then value else value + "\\"

    let loadDriveIcon (driveRootPath: string) : IImage =
        let root = normalizeDriveRoot driveRootPath
        if root = "" then
            null
        else
            // Real path + no USEFILEATTRIBUTES → Windows returns the actual drive type icon.
            let flags = Shell32.SHGFI_ICON ||| Shell32.SHGFI_SMALLICON
            match loadIconWithFlags root 0u flags with
            | null -> loadIcon root Shell32.FILE_ATTRIBUTE_DIRECTORY
            | icon -> icon

    interface IFileIconProvider with
        member _.GetIcon(pathOrExtension) =
            let key = extensionKey pathOrExtension
            cache.GetOrAdd(key, fun _ -> load pathOrExtension)

        member _.GetFolderIcon() =
            if not folderIconLoaded then
                folderIcon <- loadIcon "folder" Shell32.FILE_ATTRIBUTE_DIRECTORY
                folderIconLoaded <- true
            folderIcon

        member _.GetDriveIcon(driveRootPath) =
            let key = normalizeDriveRoot driveRootPath
            if key = "" then null
            else driveCache.GetOrAdd(key, fun root -> loadDriveIcon root)

/// Placeholder provider for non-Windows (swap for theme icons later).
type FallbackFileIconProvider() =
    interface IFileIconProvider with
        member _.GetIcon(_pathOrExtension) = null
        member _.GetFolderIcon() = null
        member _.GetDriveIcon(_driveRootPath) = null

module FileIconProvider =

    /// Create the platform provider (Windows Shell on Windows, else fallback).
    let create () : IFileIconProvider =
        if OperatingSystem.IsWindows() then
            WindowsShellFileIconProvider() :> IFileIconProvider
        else
            FallbackFileIconProvider() :> IFileIconProvider

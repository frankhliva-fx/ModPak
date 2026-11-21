namespace ModPak

open System
open System.IO

module FileMetas =

    [<Literal>]
    let DefaultPermissions = 0o644us

    let private unixMs (dt: DateTime) =
        uint64 (DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)).ToUnixTimeMilliseconds())

    let private fromUnixMs (ms: uint64) =
        if ms = 0UL then
            DateTime.UnixEpoch
        else
            DateTimeOffset.FromUnixTimeMilliseconds(int64 ms).UtcDateTime

    let ofBytes (bytes: byte[]) : FileMeta =
        let now = uint64 (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())
        {
            CreatedTime = now
            ModifiedTime = now
            AccessedTime = now
            UnpackedSize = uint64 bytes.Length
            Permissions = DefaultPermissions
            Crc32 = Hash.crc32 bytes
        }

    let ofFilePath (path: string) (bytes: byte[]) : FileMeta =
        try
            let info = FileInfo(path)
            let perms =
                try
                    if OperatingSystem.IsWindows() then
                        if info.Attributes.HasFlag FileAttributes.ReadOnly then 0o444us
                        else DefaultPermissions
                    else
                        uint16 (File.GetUnixFileMode path)
                with _ ->
                    DefaultPermissions
            {
                CreatedTime = unixMs info.CreationTimeUtc
                ModifiedTime = unixMs info.LastWriteTimeUtc
                AccessedTime = unixMs info.LastAccessTimeUtc
                UnpackedSize = uint64 bytes.Length
                Permissions = perms
                Crc32 = Hash.crc32 bytes
            }
        with _ ->
            ofBytes bytes

    let applyToDisk (path: string) (meta: FileMeta) =
        try
            if meta.CreatedTime > 0UL then
                File.SetCreationTimeUtc(path, fromUnixMs meta.CreatedTime)
            if meta.ModifiedTime > 0UL then
                File.SetLastWriteTimeUtc(path, fromUnixMs meta.ModifiedTime)
            if meta.AccessedTime > 0UL then
                File.SetLastAccessTimeUtc(path, fromUnixMs meta.AccessedTime)
            if not (OperatingSystem.IsWindows()) && meta.Permissions <> 0us then
                File.SetUnixFileMode(path, enum<UnixFileMode> (int meta.Permissions))
        with _ ->
            ()

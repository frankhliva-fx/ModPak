namespace ModPak.App

open System
open System.IO

type Config(reader: ConfigReader) =

    let normalizeDir (path: string) =
        match path.Trim() with
        | "" -> ""
        | value ->
            try
                let full = Path.GetFullPath value
                if Directory.Exists full then full else ""
            with _ ->
                ""

    /// Open ModPak (.mpx) dialog.
    member self.OpenFileFolder
        with get () =
            normalizeDir (reader.GetString([ "Dialogs"; "dialogs" ], [ "OpenFile"; "open-file"; "OpenFileFolder" ]))
        and set (value: string) =
            reader.SetString("Dialogs", "OpenFile", normalizeDir value)

    /// Add files (staging) dialog — kept separate so opening a package does not clobber it.
    member self.AddFileFolder
        with get () =
            let dedicated =
                normalizeDir (
                    reader.GetString([ "Dialogs"; "dialogs" ], [ "AddFile"; "add-file"; "AddFiles"; "AddFileFolder" ]))
            if dedicated <> "" then dedicated else self.OpenFileFolder
        and set (value: string) =
            reader.SetString("Dialogs", "AddFile", normalizeDir value)

    /// Scripts tab Open script dialog.
    member self.OpenScriptFolder
        with get () =
            normalizeDir (reader.GetString([ "Dialogs"; "dialogs" ], [ "OpenScript"; "open-script"; "OpenScriptFolder" ]))
        and set (value: string) =
            reader.SetString("Dialogs", "OpenScript", normalizeDir value)

    /// Pack (Save) dialog.
    member self.SaveFileFolder
        with get () =
            normalizeDir (reader.GetString([ "Dialogs"; "dialogs" ], [ "SaveFile"; "save-file"; "SaveFileFolder" ]))
        and set (value: string) =
            reader.SetString("Dialogs", "SaveFile", normalizeDir value)

    /// Unpack folder picker.
    member self.OpenFolder
        with get () =
            normalizeDir (reader.GetString([ "Dialogs"; "dialogs" ], [ "OpenFolder"; "open-folder" ]))
        and set (value: string) =
            reader.SetString("Dialogs", "OpenFolder", normalizeDir value)

    member self.WindowState
        with get () =
            match reader.GetString([ "MainWindow" ], [ "WindowState" ]).Trim().ToLowerInvariant() with
            | "minimized" -> "Minimized"
            | "maximized" -> "Maximized"
            | "fullscreen" -> "FullScreen"
            | _ -> "Normal"
        and set (value: string) =
            let stored =
                match value.Trim().ToLowerInvariant() with
                | "minimized" -> "Minimized"
                | "maximized" -> "Maximized"
                | "fullscreen" -> "FullScreen"
                | _ -> "Normal"
            reader.SetString("MainWindow", "WindowState", stored)

    member self.WindowWidth
        with get () = reader.GetInt([ "MainWindow" ], [ "Width"; "width" ], 980)
        and set (value: int) = reader.SetInt("MainWindow", "Width", value)

    member self.WindowHeight
        with get () = reader.GetInt([ "MainWindow" ], [ "Height"; "height" ], 720)
        and set (value: int) = reader.SetInt("MainWindow", "Height", value)

    /// Drop manifest and module drafts left in older settings files.
    member self.ForgetPackageForms() =
        let removed =
            [
                reader.RemoveSection([ "Manifest"; "manifest" ])
                reader.RemoveSection([ "Module"; "module" ])
                reader.RemoveKey([ "Pack"; "pack" ], [ "IncludeManifest"; "include-manifest"; "Manifest" ])
                reader.RemoveKey([ "Pack"; "pack" ], [ "IncludeModule"; "include-module"; "Module" ])
            ]
            |> List.exists id
        if removed then
            reader.Save()

    /// Selected right-panel tab: Package | Manifest | Scripts.
    member self.MetadataTab
        with get () =
            match reader.GetString([ "Metadata"; "metadata" ], [ "Tab"; "tab" ]).Trim().ToLowerInvariant() with
            | "scripts" | "script" -> "Scripts"
            | "manifest" | "mani" -> "Manifest"
            | _ -> "Package"
        and set (value: string) =
            let stored =
                match value.Trim().ToLowerInvariant() with
                | "scripts" | "script" -> "Scripts"
                | "manifest" | "mani" -> "Manifest"
                | _ -> "Package"
            reader.SetString("Metadata", "Tab", stored)

    /// Selected script kind in Scripts tab: JS | Wasm | MySql | PostgreSql | Sqlite | Lua.
    member self.ScriptKind
        with get () =
            match reader.GetString([ "Metadata"; "metadata" ], [ "ScriptKind"; "script-kind"; "Script" ]).Trim().ToLowerInvariant() with
            | "wasm" | "webassembly" | "web assembly" -> "Wasm"
            | "mysql" | "my-sql" | "sql" | "sqld" -> "MySql"
            | "postgresql" | "postgres" | "pgsql" -> "PostgreSql"
            | "sqlite" | "sqlite3" -> "Sqlite"
            | "lua" | "luas" -> "Lua"
            | _ -> "JS"
        and set (value: string) =
            let stored =
                match value.Trim().ToLowerInvariant() with
                | "wasm" | "webassembly" | "web assembly" -> "Wasm"
                | "mysql" | "my-sql" | "sql" | "sqld" -> "MySql"
                | "postgresql" | "postgres" | "pgsql" -> "PostgreSql"
                | "sqlite" | "sqlite3" -> "Sqlite"
                | "lua" | "luas" -> "Lua"
                | _ -> "JS"
            reader.SetString("Metadata", "ScriptKind", stored)

    /// Selected execution trigger in Scripts tab.
    member self.ExecutionTrigger
        with get () =
            match reader.GetString([ "Metadata"; "metadata" ], [ "ExecutionTrigger"; "execution-trigger"; "Trigger" ]).Trim().ToLowerInvariant() with
            | "manual" -> "Manual"
            | "beforemount" | "before-mount" | "before mount"
            | "beforeinstall" | "before-install" | "before install" -> "BeforeMount"
            | "onstart" | "on-start" | "on start"
            | "beforeupdate" | "before-update" | "before update"
            | "onupdate" | "on-update" | "on update" -> "OnStart"
            | "beforeunmount" | "before-unmount" | "before unmount"
            | "beforeuninstall" | "before-uninstall" | "before uninstall" -> "BeforeUnmount"
            | "onunmount" | "on-unmount" | "on unmount"
            | "onuninstall" | "on-uninstall" | "on uninstall" -> "OnUnmount"
            | "onmount" | "on-mount" | "on mount"
            | "oninstall" | "on-install" | "on install" -> "OnMount"
            | _ -> "OnStart"
        and set (value: string) =
            let stored =
                match value.Trim().ToLowerInvariant() with
                | "manual" -> "Manual"
                | "beforemount" | "before-mount" | "before mount"
                | "beforeinstall" | "before-install" | "before install" -> "BeforeMount"
                | "onstart" | "on-start" | "on start"
                | "beforeupdate" | "before-update" | "before update"
                | "onupdate" | "on-update" | "on update" -> "OnStart"
                | "beforeunmount" | "before-unmount" | "before unmount"
                | "beforeuninstall" | "before-uninstall" | "before uninstall" -> "BeforeUnmount"
                | "onunmount" | "on-unmount" | "on unmount"
                | "onuninstall" | "on-uninstall" | "on uninstall" -> "OnUnmount"
                | "onmount" | "on-mount" | "on mount"
                | "oninstall" | "on-install" | "on install" -> "OnMount"
                | _ -> "OnStart"
            reader.SetString("Metadata", "ExecutionTrigger", stored)

    member private self.RememberFileDir (key: string) (filePath: string) =
        try
            let dir = Path.GetDirectoryName(Path.GetFullPath filePath)
            if not (String.IsNullOrWhiteSpace dir) && Directory.Exists dir then
                reader.SetString("Dialogs", key, dir)
                reader.Save()
        with _ ->
            ()

    member self.RememberOpenFile (filePath: string) =
        self.RememberFileDir "OpenFile" filePath

    member self.RememberAddFile (filePath: string) =
        self.RememberFileDir "AddFile" filePath

    member self.RememberOpenScript (filePath: string) =
        self.RememberFileDir "OpenScript" filePath

    member self.RememberSaveFile (filePath: string) =
        self.RememberFileDir "SaveFile" filePath

    member self.RememberOpenFolder (folderPath: string) =
        let dir = normalizeDir folderPath
        if dir <> "" then
            reader.SetString("Dialogs", "OpenFolder", dir)
            reader.Save()

    /// Explorer multi-selection: Total Commander style (click toggles) when true;
    /// Windows style (Ctrl+click) when false.
    member self.ExplorerMultiSelection
        with get () =
            reader.GetBool(
                [ "Explorer"; "explorer" ],
                [ "MultiSelection"; "multi-selection"; "MultiSelect"; "multi-select" ],
                false)
        and set (value: bool) =
            reader.SetBool("Explorer", "MultiSelection", value)

    /// Package pane multi-selection (independent of Explorer).
    member self.PackageMultiSelection
        with get () =
            reader.GetBool(
                [ "Package"; "package" ],
                [ "MultiSelection"; "multi-selection"; "MultiSelect"; "multi-select" ],
                false)
        and set (value: bool) =
            reader.SetBool("Package", "MultiSelection", value)

    /// When adding files: overwrite same relative path without asking.
    member self.OverwriteExistingFiles
        with get () =
            reader.GetBool(
                [ "Pack"; "pack" ],
                [ "OverwriteExistingFiles"; "overwrite-existing-files"; "Overwrite" ],
                false)
        and set (value: bool) =
            reader.SetBool("Pack", "OverwriteExistingFiles", value)

    /// Last opened / saved .mpx package (full path).
    member self.LastPackage
        with get () =
            let raw =
                reader.GetString([ "Session"; "session" ], [ "LastPackage"; "last-package"; "LastFile"; "last-file" ]).Trim()
            if String.IsNullOrWhiteSpace raw then
                ""
            else
                try
                    Path.GetFullPath raw
                with _ ->
                    raw
        and set (value: string) =
            let stored =
                if String.IsNullOrWhiteSpace value then
                    ""
                else
                    try
                        Path.GetFullPath value
                    with _ ->
                        value.Trim()
            reader.SetString("Session", "LastPackage", stored)

    member self.RememberLastPackage (filePath: string) =
        if not (String.IsNullOrWhiteSpace filePath) then
            self.LastPackage <- filePath
            // Only session + Open (.mpx) folder — never overwrite AddFile staging folder.
            self.RememberOpenFile filePath

    member self.Refresh() = reader.Refresh()

    member self.Save() = reader.Save()

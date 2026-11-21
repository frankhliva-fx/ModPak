namespace ModPak.App

open System
open System.Collections.Concurrent
open System.Collections.ObjectModel
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading.Tasks
open Microsoft.Win32
open Avalonia
open Avalonia.Controls
open Avalonia.Controls.Shapes
open Avalonia.Input
open Avalonia.Interactivity
open Avalonia.Layout
open Avalonia.Markup.Xaml
open Avalonia.Media
open Avalonia.Media.Imaging
open Avalonia.Platform
open Avalonia.Svg.Skia
open Avalonia.Platform.Storage
open Avalonia.Threading
open Avalonia.VisualTree
open AvaloniaEdit
open AvaloniaEdit.TextMate
open FluentIcons.Avalonia
open FluentIcons.Common
open TextMateSharp.Grammars
open ModPak

module private ModPakDnD =
    /// In-app Explorer → Package path list (newline-separated full paths).
    /// Identifier may only use A–Z, a–z, 0–9, `.`, `-` (Avalonia app formats).
    let PathsFormat = DataFormat.CreateStringApplicationFormat "modpak-disk-paths"

type private ScriptKind =
    | Js
    | Wasm
    | MySql
    | PostgreSql
    | Sqlite
    | Lua

type private ScriptListRow =
    { Index: int
      Label: string
      Icon: IImage }

type MainWindow() as self =
    inherit Window()

    let config = Config(ConfigReader())
    let icons = FileIconProvider.create ()
    let scriptIconCache = ConcurrentDictionary<string, IImage>(StringComparer.OrdinalIgnoreCase)

    let mutable manifestCheck: CheckBox = null
    let mutable clearManifestButton: Button = null
    let mutable manifestFields: StackPanel = null
    let mutable manifestNameBox: TextBox = null
    let mutable manifestSlugBox: TextBox = null
    let mutable manifestRoleBox: TextBox = null
    let mutable manifestToneBox: ComboBox = null
    let mutable manifestIconImage: Image = null
    let mutable manifestSystemCheck: CheckBox = null
    let mutable manifestActiveCheck: CheckBox = null
    let mutable manifestVisibleCheck: CheckBox = null
    let mutable manifestDashboardCheck: CheckBox = null
    let mutable manifestSectionCheck: CheckBox = null
    let mutable manifestLockedCheck: CheckBox = null
    let mutable manifestLabelBox: TextBox = null
    let mutable manifestCaptionBox: TextBox = null
    let mutable metadataTabs: TabControl = null
    let mutable scriptList: ListBox = null
    let mutable scriptKindBox: ComboBox = null
    let mutable scriptTriggerBox: ComboBox = null
    let mutable scriptEditor: TextEditor = null
    let mutable scriptTextMate: TextMate.Installation = null
    let mutable scriptMateRegistry: RegistryOptions = null
    let mutable saveScriptButton: Button = null
    let mutable fileList: ListBox = null
    let mutable diskList: ListBox = null
    let mutable diskPathBox: TextBox = null
    let mutable unpackButton: Button = null
    let mutable unpackAllButton: Button = null
    let mutable saveButton: Button = null
    let mutable infoButton: Button = null
    let mutable openMenuItem: MenuItem = null
    let mutable detailsMenuItem: MenuItem = null
    let mutable renameMenuItem: MenuItem = null
    let mutable deleteMenuItem: MenuItem = null
    let mutable unpakMenuItem: MenuItem = null
    let mutable codecBox: ComboBox = null
    let mutable statusText: TextBlock = null
    let mutable package: ModPakPackage option = None
    let mutable packagePath: string option = None
    let mutable pendingPaths: string list = []
    /// Relative path (lower) -> FILE codec for staging / opened package edits.
    let mutable fileCodecs: Map<string, byte> = Map.empty
    /// Staging relative path (lower) -> renamed relative path.
    let mutable pendingRenames: Map<string, string> = Map.empty
    /// Virtual package cwd (`""` = root, `"sql/schema"` = nested).
    let mutable explorerDir = ""
    /// Windows Explorer cwd. `None` = drive list; `Some path` = folder.
    let mutable diskDir: string option = None
    let mutable suppressDiskPathUi = false
    let mutable isDirty = false
    let diskPathOkBrush = SolidColorBrush(Color.Parse "#3F3731")
    let diskPathBadBrush = SolidColorBrush(Color.Parse "#EC4899")
    let mutable suppressManifestPersist = false
    let mutable manifestId = ""
    let mutable manifestIconBytes = Array.empty<byte>
    let mutable manifestIconMime = ""
    let mutable manifestIconPath = ""
    let mutable suppressFormPersist = false
    let mutable suppressMetadataTabPersist = false
    let mutable suppressScriptLoad = false
    let mutable suppressScriptList = false
    let mutable selectedScriptIndex = -1
    let mutable packingInProgress = false
    /// Explorer → Package drag: press args kept until move threshold or release.
    let mutable diskDragPress: PointerPressedEventArgs option = None
    let mutable diskDragOrigin: Point option = None
    let mutable diskDragStarted = false

    let mutable normalWidth = config.WindowWidth
    let mutable normalHeight = config.WindowHeight

    let okBrush = SolidColorBrush(Color.Parse "#8FBF8F")
    let errBrush = SolidColorBrush(Color.Parse "#E07070")
    let muteBrush = SolidColorBrush(Color.Parse "#A89888")

    [<Literal>]
    let AppVersion = "3.6"

    do self.InitializeComponent()

    member private self.InitializeComponent() =
        AvaloniaXamlLoader.Load(self)
        manifestCheck <- self.FindControl<CheckBox>("ManifestCheck")
        clearManifestButton <- self.FindControl<Button>("ClearManifestButton")
        manifestFields <- self.FindControl<StackPanel>("ManifestFields")
        manifestNameBox <- self.FindControl<TextBox>("ManifestNameBox")
        manifestSlugBox <- self.FindControl<TextBox>("ManifestSlugBox")
        manifestRoleBox <- self.FindControl<TextBox>("ManifestRoleBox")
        manifestToneBox <- self.FindControl<ComboBox>("ManifestToneBox")
        manifestIconImage <- self.FindControl<Image>("ManifestIconImage")
        manifestSystemCheck <- self.FindControl<CheckBox>("ManifestSystemCheck")
        manifestActiveCheck <- self.FindControl<CheckBox>("ManifestActiveCheck")
        manifestVisibleCheck <- self.FindControl<CheckBox>("ManifestVisibleCheck")
        manifestDashboardCheck <- self.FindControl<CheckBox>("ManifestDashboardCheck")
        manifestSectionCheck <- self.FindControl<CheckBox>("ManifestSectionCheck")
        manifestLockedCheck <- self.FindControl<CheckBox>("ManifestLockedCheck")
        manifestLabelBox <- self.FindControl<TextBox>("ManifestLabelBox")
        manifestCaptionBox <- self.FindControl<TextBox>("ManifestCaptionBox")
        metadataTabs <- self.FindControl<TabControl>("MetadataTabs")
        scriptList <- self.FindControl<ListBox>("ScriptList")
        scriptKindBox <- self.FindControl<ComboBox>("ScriptKindBox")
        scriptTriggerBox <- self.FindControl<ComboBox>("ScriptTriggerBox")
        scriptEditor <- self.FindControl<TextEditor>("ScriptEditor")
        saveScriptButton <- self.FindControl<Button>("SaveScriptButton")
        fileList <- self.FindControl<ListBox>("FileList")
        diskList <- self.FindControl<ListBox>("DiskList")
        diskPathBox <- self.FindControl<TextBox>("DiskPathBox")
        unpackButton <- self.FindControl<Button>("UnpackButton")
        unpackAllButton <- self.FindControl<Button>("UnpackAllButton")
        saveButton <- self.FindControl<Button>("SaveButton")
        infoButton <- self.FindControl<Button>("InfoButton")
        openMenuItem <- self.FindControl<MenuItem>("OpenMenuItem")
        unpakMenuItem <- self.FindControl<MenuItem>("UnpakMenuItem")
        codecBox <- self.FindControl<ComboBox>("CodecBox")
        statusText <- self.FindControl<TextBlock>("StatusText")
        self.InitializeScriptEditor()
        if not (isNull codecBox) && codecBox.SelectedIndex < 0 then
            codecBox.SelectedIndex <- 1 // LZ4
        self.InitDiskExplorer()
        self.WireExplorerPackageDragDrop()
        self.UpdateFileActionsEnabled()
        self.UpdateSaveEnabled()

        if not (isNull manifestSlugBox) then
            let onManifestSlugPress (sender: obj) (e: PointerPressedEventArgs) =
                self.ManifestSlugBoxPointerPressed(sender, e)
            manifestSlugBox.AddHandler(InputElement.PointerPressedEvent, onManifestSlugPress, RoutingStrategies.Tunnel ||| RoutingStrategies.Bubble, true)

        config.ForgetPackageForms()
        self.ApplyManifestEnabled()
        self.RestoreMetadataUi()

        if config.WindowWidth >= 760 then
            self.Width <- float config.WindowWidth
        if config.WindowHeight >= 560 then
            self.Height <- float config.WindowHeight
        self.WindowState <-
            match config.WindowState with
            | "Minimized" -> WindowState.Minimized
            | "Maximized" -> WindowState.Maximized
            | "FullScreen" -> WindowState.FullScreen
            | _ -> WindowState.Normal

        self.PropertyChanged.Add(fun event ->
            if event.Property = Window.WidthProperty || event.Property = Window.HeightProperty then
                self.NoteSize()
            if event.Property = Window.WindowStateProperty then
                self.RememberState())
        self.Closing.Add(fun _ ->
            self.PersistMetadataUi()
            self.RememberState())
        self.Opened.Add(fun _ -> self.HandleStartupPackage() |> ignore)

    member private self.BoxText(box: TextBox) =
        match box with
        | null -> ""
        | b when isNull b.Text -> ""
        | b -> b.Text.Trim()

    member private self.SetBoxText(box: TextBox, value: string) =
        if not (isNull box) then
            box.Text <- if isNull value then "" else value

    member private self.IncludeManifest() =
        match manifestCheck with
        | null -> false
        | box -> box.IsChecked = Nullable(true)

    member private self.ApplyManifestEnabled() =
        let on = self.IncludeManifest()
        let opacity = if on then 1.0 else 0.5
        if not (isNull manifestFields) then
            manifestFields.IsEnabled <- on
            manifestFields.Opacity <- opacity
        if not (isNull clearManifestButton) then
            clearManifestButton.IsEnabled <- on

    member private self.ParseScriptKind(value: string) =
        match value.Trim().ToLowerInvariant() with
        | "wasm" | "webassembly" | "web assembly" -> Wasm
        | "mysql" | "my-sql" | "sql" | "sqld" -> MySql
        | "postgresql" | "postgres" | "pgsql" -> PostgreSql
        | "sqlite" | "sqlite3" -> Sqlite
        | "lua" | "luas" -> Lua
        | _ -> Js

    member private self.ScriptKindToConfig(kind: ScriptKind) =
        match kind with
        | Js -> "JS"
        | Wasm -> "Wasm"
        | MySql -> "MySql"
        | PostgreSql -> "PostgreSql"
        | Sqlite -> "Sqlite"
        | Lua -> "Lua"

    member private self.ScriptKindIndex(kind: ScriptKind) =
        match kind with
        | Js -> 0
        | Wasm -> 1
        | MySql -> 2
        | PostgreSql -> 3
        | Sqlite -> 4
        | Lua -> 5

    member private self.SelectedScriptKind() =
        if isNull scriptKindBox || scriptKindBox.SelectedIndex < 0 then
            self.ParseScriptKind config.ScriptKind
        else
            match scriptKindBox.SelectedItem with
            | :? ComboBoxItem as item when not (isNull item.Tag) -> self.ParseScriptKind(string item.Tag)
            | _ ->
                match scriptKindBox.SelectedIndex with
                | 1 -> Wasm
                | 2 -> MySql
                | 3 -> PostgreSql
                | 4 -> Sqlite
                | 5 -> Lua
                | _ -> Js

    member private self.ScriptKindToTechnology(kind: ScriptKind) =
        match kind with
        | Js -> ExecTechnology.Js
        | Wasm -> ExecTechnology.Wasm
        | MySql -> ExecTechnology.MySql
        | PostgreSql -> ExecTechnology.PostgreSql
        | Sqlite -> ExecTechnology.Sqlite
        | Lua -> ExecTechnology.Lua

    member private self.TechnologyToScriptKind(technology: ExecTechnology) =
        match technology with
        | ExecTechnology.Js -> Some Js
        | ExecTechnology.Wasm -> Some Wasm
        | ExecTechnology.MySql -> Some MySql
        | ExecTechnology.PostgreSql -> Some PostgreSql
        | ExecTechnology.Sqlite -> Some Sqlite
        | ExecTechnology.Lua -> Some Lua
        | ExecTechnology.Custom _ -> None

    member private self.ParseExecutionTrigger(value: string) =
        match value.Trim().ToLowerInvariant() with
        | "manual" -> Manual
        | "beforemount" | "before-mount" | "before mount"
        | "beforeinstall" | "before-install" | "before install" -> BeforeMount
        | "onstart" | "on-start" | "on start"
        | "beforeupdate" | "before-update" | "before update"
        | "onupdate" | "on-update" | "on update" -> OnStart
        | "beforeunmount" | "before-unmount" | "before unmount"
        | "beforeuninstall" | "before-uninstall" | "before uninstall" -> BeforeUnmount
        | "onunmount" | "on-unmount" | "on unmount"
        | "onuninstall" | "on-uninstall" | "on uninstall" -> OnUnmount
        | "onmount" | "on-mount" | "on mount"
        | "oninstall" | "on-install" | "on install" -> OnMount
        | _ -> OnStart

    member private self.ExecutionTriggerToConfig(trigger: ExecutionTrigger) = ExecutionTrigger.label trigger

    member private self.ExecutionTriggerIndex(trigger: ExecutionTrigger) =
        match trigger with
        | Manual -> 0
        | BeforeMount -> 1
        | OnMount -> 2
        | OnStart -> 3
        | BeforeUnmount -> 4
        | OnUnmount -> 5

    member private self.SelectedExecutionTrigger() =
        if isNull scriptTriggerBox || scriptTriggerBox.SelectedIndex < 0 then
            self.ParseExecutionTrigger config.ExecutionTrigger
        else
            match scriptTriggerBox.SelectedItem with
            | :? ComboBoxItem as item when not (isNull item.Tag) ->
                self.ParseExecutionTrigger(string item.Tag)
            | _ ->
                match scriptTriggerBox.SelectedIndex with
                | 0 -> Manual
                | 1 -> BeforeMount
                | 2 -> OnMount
                | 4 -> BeforeUnmount
                | 5 -> OnUnmount
                | _ -> OnStart

    member private self.SetScriptKindUi(kind: ScriptKind) =
        if not (isNull scriptKindBox) then
            scriptKindBox.SelectedIndex <- self.ScriptKindIndex kind

    member private self.SetScriptTriggerUi(trigger: ExecutionTrigger) =
        if not (isNull scriptTriggerBox) then
            scriptTriggerBox.SelectedIndex <- self.ExecutionTriggerIndex trigger

    /// Kind / lifecycle are chosen only when adding; locked for existing scripts.
    member private self.UpdateScriptMetaEditorsEnabled() =
        let enabled = selectedScriptIndex < 0
        if not (isNull scriptKindBox) then
            scriptKindBox.IsEnabled <- enabled
        if not (isNull scriptTriggerBox) then
            scriptTriggerBox.IsEnabled <- enabled

    member private self.ExecTechnologyLabel(technology: ExecTechnology) =
        match self.TechnologyToScriptKind technology with
        | Some kind -> self.ScriptKindLabel kind
        | None ->
            match technology with
            | ExecTechnology.Custom name when not (String.IsNullOrWhiteSpace name) -> name
            | _ -> "Custom"

    member private self.ScriptListLabel(exec: ModPakExec) =
        $"{ExecutionTrigger.displayLabel exec.Trigger} · {self.ExecTechnologyLabel exec.Technology}"

    member private self.LoadScriptSvgIcon(fileName: string) =
        try
            let uri = Uri($"avares://ModPak.App/Assets/script-icons/{fileName}")
            use stream = AssetLoader.Open uri
            use reader = new StreamReader(stream)
            let svg = reader.ReadToEnd()
            let source = SvgSource.LoadFromSvg svg
            if isNull source then
                null
            else
                let image = SvgImage()
                image.Source <- source
                image :> IImage
        with _ ->
            null

    member private self.ScriptTechnologyIcon(technology: ExecTechnology) =
        let file =
            match technology with
            | ExecTechnology.Js -> "javascript.svg"
            | ExecTechnology.Wasm -> "webassembly.svg"
            | ExecTechnology.Lua -> "lua.svg"
            | ExecTechnology.MySql
            | ExecTechnology.PostgreSql
            | ExecTechnology.Sqlite -> "database.svg"
            | ExecTechnology.Custom _ -> "tune.svg"
        match scriptIconCache.TryGetValue file with
        | true, icon -> icon
        | false, _ ->
            let icon = self.LoadScriptSvgIcon file
            if not (isNull icon) then
                scriptIconCache.[file] <- icon
            icon

    member private self.EnsurePackage() =
        match package with
        | Some pkg -> pkg
        | None ->
            let pkg = self.EmptyPackage()
            package <- Some pkg
            pkg

    member private self.UpdateScriptAt(index: int) (map: ModPakExec -> ModPakExec) =
        let pkg = self.EnsurePackage()
        if index < 0 || index >= pkg.Exec.Length then
            pkg
        else
            let execs =
                pkg.Exec
                |> List.mapi (fun i item -> if i = index then map item else item)
            let next = { pkg with Exec = execs }
            package <- Some next
            next

    member private self.RefreshScriptList() =
        if isNull scriptList then
            ()
        else
            suppressScriptList <- true
            try
                let rows =
                    match package with
                    | None -> []
                    | Some pkg ->
                        pkg.Exec
                        |> List.mapi (fun i exec ->
                            {
                                Index = i
                                Label = self.ScriptListLabel exec
                                Icon = self.ScriptTechnologyIcon exec.Technology
                            })
                scriptList.ItemsSource <- rows
                if rows.IsEmpty then
                    selectedScriptIndex <- -1
                    scriptList.SelectedIndex <- -1
                elif selectedScriptIndex >= 0 && selectedScriptIndex < rows.Length then
                    scriptList.SelectedIndex <- selectedScriptIndex
                elif selectedScriptIndex >= rows.Length then
                    selectedScriptIndex <- rows.Length - 1
                    scriptList.SelectedIndex <- selectedScriptIndex
                else
                    // Keep deselected so kind/lifecycle stay editable as the Add template.
                    scriptList.SelectedIndex <- -1
            finally
                suppressScriptList <- false

    member private self.SelectScriptIndexFromPackage(pkg: ModPakPackage) =
        let configKind = self.ParseScriptKind config.ScriptKind
        let configTrigger = self.ParseExecutionTrigger config.ExecutionTrigger
        let kindTech = self.ScriptKindToTechnology configKind
        let pick =
            pkg.Exec
            |> List.tryFindIndex (fun e -> ExecTechnology.sameKind e.Technology kindTech && e.Trigger = configTrigger)
            |> Option.orElseWith (fun () ->
                if pkg.Exec.IsEmpty then None else Some 0)
        match pick with
        | None -> selectedScriptIndex <- -1
        | Some index -> selectedScriptIndex <- index
        self.RefreshScriptList()

    member private self.SelectScriptKindFromPackage(pkg: ModPakPackage) =
        self.SelectScriptIndexFromPackage pkg

    member private self.EmptyPackage() =
        {
            Manifest = None
            Exec = []
            Entries = []
        }

    member private self.ScriptBytesToEditor(kind: ScriptKind) (bytes: byte[]) =
        match kind with
        | Wasm -> Convert.ToBase64String bytes
        | _ -> Encoding.UTF8.GetString bytes

    member private self.EditorToScriptBytes(kind: ScriptKind) (text: string) =
        let trimmed = if isNull text then "" else text
        if String.IsNullOrWhiteSpace trimmed then
            Ok None
        else
            match kind with
            | Wasm ->
                try
                    Ok(Some(Convert.FromBase64String(trimmed.Trim())))
                with ex ->
                    Error $"Web Assembly must be Base64: {ex.Message}"
            | _ -> Ok(Some(Encoding.UTF8.GetBytes trimmed))

    member private self.ScriptLanguageId(kind: ScriptKind) =
        match kind with
        | Js -> "javascript"
        | Lua -> "lua"
        | MySql | PostgreSql | Sqlite -> "sql"
        | Wasm -> "plaintext"

    member private self.ApplyScriptGrammar(kind: ScriptKind) =
        if not (isNull scriptTextMate) && not (isNull scriptMateRegistry) then
            let scope = scriptMateRegistry.GetScopeByLanguageId(self.ScriptLanguageId kind)
            if not (isNull scope) then
                scriptTextMate.SetGrammar scope

    member private self.InitializeScriptEditor() =
        if not (isNull scriptEditor) then
            scriptEditor.Options.EnableHyperlinks <- false
            scriptEditor.Options.EnableEmailHyperlinks <- false
            scriptEditor.Options.ConvertTabsToSpaces <- true
            scriptEditor.Options.IndentationSize <- 2
            scriptEditor.Options.LineHeightFactor <- 1.8
            scriptEditor.TextArea.Background <- Brushes.Transparent
            scriptMateRegistry <- RegistryOptions(ThemeName.OneDark)
            scriptTextMate <- scriptEditor.InstallTextMate(scriptMateRegistry)
            self.ApplyScriptGrammar(self.SelectedScriptKind())
            scriptEditor.TextChanged.Add(fun _ ->
                if not suppressScriptLoad then
                    self.MarkDirty())

    member private self.ScriptEditorText
        with get () =
            if isNull scriptEditor then ""
            elif isNull scriptEditor.Text then ""
            else scriptEditor.Text
        and set (value: string) =
            if not (isNull scriptEditor) then
                scriptEditor.Text <- if isNull value then "" else value

    member private self.LoadScriptEditor() =
        if not (isNull scriptEditor) then
            suppressScriptLoad <- true
            try
                match package with
                | None ->
                    self.ScriptEditorText <- ""
                    self.SetScriptKindUi(self.ParseScriptKind config.ScriptKind)
                    self.SetScriptTriggerUi(self.ParseExecutionTrigger config.ExecutionTrigger)
                    self.ApplyScriptGrammar(self.SelectedScriptKind())
                | Some pkg when selectedScriptIndex < 0 || selectedScriptIndex >= pkg.Exec.Length ->
                    self.ScriptEditorText <- ""
                    self.SetScriptKindUi(self.ParseScriptKind config.ScriptKind)
                    self.SetScriptTriggerUi(self.ParseExecutionTrigger config.ExecutionTrigger)
                    self.ApplyScriptGrammar(self.SelectedScriptKind())
                | Some pkg ->
                    let exec = pkg.Exec.[selectedScriptIndex]
                    let kind =
                        self.TechnologyToScriptKind exec.Technology
                        |> Option.defaultWith self.SelectedScriptKind
                    self.SetScriptKindUi kind
                    self.SetScriptTriggerUi exec.Trigger
                    self.ApplyScriptGrammar kind
                    self.ScriptEditorText <- self.ScriptBytesToEditor kind exec.Bytes
                self.UpdateScriptMetaEditorsEnabled()
            finally
                suppressScriptLoad <- false

    member private self.PersistMetadataUi() =
        if not suppressMetadataTabPersist then
            if not (isNull metadataTabs) then
                config.MetadataTab <-
                    match metadataTabs.SelectedIndex with
                    | 1 -> "Manifest"
                    | 2 -> "Scripts"
                    | _ -> "Package"
            // Only persist kind/trigger when unlocked (template for Add); selected scripts are read-only.
            if selectedScriptIndex < 0 then
                config.ScriptKind <- self.ScriptKindToConfig(self.SelectedScriptKind())
                config.ExecutionTrigger <- self.ExecutionTriggerToConfig(self.SelectedExecutionTrigger())
            config.Save()

    member private self.RestoreMetadataUi() =
        suppressMetadataTabPersist <- true
        suppressScriptLoad <- true
        try
            if not (isNull metadataTabs) then
                metadataTabs.SelectedIndex <-
                    match config.MetadataTab with
                    | "Manifest" -> 1
                    | "Scripts" -> 2
                    | _ -> 0
            if not (isNull scriptKindBox) then
                scriptKindBox.SelectedIndex <- self.ScriptKindIndex(self.ParseScriptKind config.ScriptKind)
            if not (isNull scriptTriggerBox) then
                scriptTriggerBox.SelectedIndex <-
                    self.ExecutionTriggerIndex(self.ParseExecutionTrigger config.ExecutionTrigger)
        finally
            suppressScriptLoad <- false
            suppressMetadataTabPersist <- false
        self.RefreshScriptList()
        self.LoadScriptEditor()

    member private self.MetadataTabChanged(_: obj, _: SelectionChangedEventArgs) =
        if not suppressMetadataTabPersist then
            self.PersistMetadataUi()

    member private self.ScriptListSelectionChanged(_: obj, e: SelectionChangedEventArgs) =
        if not suppressScriptList then
            if e.AddedItems.Count > 0 then
                match e.AddedItems.[0] with
                | :? ScriptListRow as row ->
                    if selectedScriptIndex >= 0 && selectedScriptIndex <> row.Index then
                        self.ApplyScriptToSelectedIndex() |> ignore
                    selectedScriptIndex <- row.Index
                    self.LoadScriptEditor()
                | _ -> ()
            elif e.RemovedItems.Count > 0 && e.AddedItems.Count = 0 then
                self.ApplyScriptToSelectedIndex() |> ignore
                selectedScriptIndex <- -1
                self.LoadScriptEditor()

    member private self.ScriptKindChanged(_: obj, e: SelectionChangedEventArgs) =
        // Existing scripts are locked — kind only sets the template for Add.
        if not suppressScriptLoad && e.AddedItems.Count > 0 && selectedScriptIndex < 0 then
            let newKind =
                match e.AddedItems.[0] with
                | :? ComboBoxItem as item when not (isNull item.Tag) ->
                    self.ParseScriptKind(string item.Tag)
                | _ -> self.SelectedScriptKind()
            self.ApplyScriptGrammar newKind
            config.ScriptKind <- self.ScriptKindToConfig newKind
            if not suppressMetadataTabPersist then
                config.Save()

    member private self.ScriptTriggerChanged(_: obj, e: SelectionChangedEventArgs) =
        // Existing scripts are locked — trigger only sets the template for Add.
        if not suppressScriptLoad && e.AddedItems.Count > 0 && selectedScriptIndex < 0 then
            let trigger = self.SelectedExecutionTrigger()
            config.ExecutionTrigger <- self.ExecutionTriggerToConfig trigger
            if not suppressMetadataTabPersist then
                config.Save()

    member private self.NewScript(_: obj, _: RoutedEventArgs) =
        self.ApplyScriptToSelectedIndex() |> ignore
        selectedScriptIndex <- -1
        if not (isNull scriptList) then
            suppressScriptList <- true
            try
                scriptList.SelectedIndex <- -1
            finally
                suppressScriptList <- false
        self.LoadScriptEditor()

    member private self.AddScript(_: obj, _: RoutedEventArgs) =
        self.ApplyScriptToSelectedIndex() |> ignore
        // Prefer unlocked template (config); if a script is selected, combos show that script but Add uses config.
        let fromTemplate = selectedScriptIndex < 0
        let kind =
            if fromTemplate then self.SelectedScriptKind()
            else self.ParseScriptKind config.ScriptKind
        let trigger =
            if fromTemplate then self.SelectedExecutionTrigger()
            else self.ParseExecutionTrigger config.ExecutionTrigger
        let bytes =
            if fromTemplate then
                match self.EditorToScriptBytes kind self.ScriptEditorText with
                | Ok bytesOpt -> bytesOpt |> Option.defaultValue Array.empty
                | Error _ -> Array.empty
            else
                Array.empty
        let item =
            {
                Name = None
                Technology = self.ScriptKindToTechnology kind
                Trigger = trigger
                Bytes = bytes
                Codec = Codec.Lzma
            }
        let pkg = self.EnsurePackage()
        let execs = pkg.Exec @ [ item ]
        package <- Some { pkg with Exec = execs }
        selectedScriptIndex <- execs.Length - 1
        self.RefreshScriptList()
        self.LoadScriptEditor()
        self.MarkDirty()

    member private self.RemoveScript(_: obj, _: RoutedEventArgs) =
        match package with
        | None -> ()
        | Some pkg when selectedScriptIndex < 0 || selectedScriptIndex >= pkg.Exec.Length -> ()
        | Some pkg ->
            let execs =
                pkg.Exec
                |> List.indexed
                |> List.filter (fun (i, _) -> i <> selectedScriptIndex)
                |> List.map snd
            package <- Some { pkg with Exec = execs }
            if execs.IsEmpty then
                selectedScriptIndex <- -1
            elif selectedScriptIndex >= execs.Length then
                selectedScriptIndex <- execs.Length - 1
            self.RefreshScriptList()
            self.LoadScriptEditor()
            self.MarkDirty()

    member private self.ScriptKindLabel(kind: ScriptKind) =
        match kind with
        | Js -> "JS"
        | Wasm -> "Web Assembly"
        | MySql -> "MySQL"
        | PostgreSql -> "PostgreSQL"
        | Sqlite -> "SQLite"
        | Lua -> "LUA"

    member private self.ScriptOpenFilter(kind: ScriptKind) =
        match kind with
        | Js -> FilePickerFileType("JavaScript", Patterns = [| "*.js" |])
        | Wasm -> FilePickerFileType("Web Assembly", Patterns = [| "*.wasm" |])
        | MySql | PostgreSql | Sqlite -> FilePickerFileType("SQL", Patterns = [| "*.sql" |])
        | Lua -> FilePickerFileType("Lua", Patterns = [| "*.lua" |])

    member private self.OpenScript(_: obj, _: RoutedEventArgs) =
        let kind = self.SelectedScriptKind()
        task {
            let! start = self.TryStartFolder(config.OpenScriptFolder)
            let options =
                FilePickerOpenOptions(
                    Title = $"Open {self.ScriptKindLabel kind}",
                    AllowMultiple = false,
                    FileTypeFilter =
                        [|
                            self.ScriptOpenFilter kind
                            FilePickerFileType("All files", Patterns = [| "*.*" |])
                        |]
                )
            if not (isNull start) then
                options.SuggestedStartLocation <- start
            let! files = self.StorageProvider.OpenFilePickerAsync(options)
            match files |> Seq.tryHead with
            | None -> ()
            | Some file ->
                let path = file.Path.LocalPath
                if not (String.IsNullOrWhiteSpace path) && File.Exists path then
                    config.RememberOpenScript path
                    try
                        let bytes = File.ReadAllBytes path
                        if not (isNull scriptEditor) then
                            suppressScriptLoad <- true
                            try
                                self.ApplyScriptGrammar kind
                                self.ScriptEditorText <- self.ScriptBytesToEditor kind bytes
                            finally
                                suppressScriptLoad <- false
                        if selectedScriptIndex >= 0 then
                            self.UpdateScriptAt selectedScriptIndex (fun exec -> { exec with Bytes = bytes })
                            |> ignore
                            self.RefreshScriptList()
                        self.MarkDirty()
                        self.Show($"Opened {Path.GetFileName path} ({bytes.Length} bytes).", okBrush)
                    with ex ->
                        self.Show($"Open failed: {ex.Message}", errBrush)
        }
        |> ignore

    member private self.ApplyScriptToSelectedIndex() =
        if selectedScriptIndex < 0 then
            Ok()
        else
            match package with
            | None -> Ok()
            | Some pkg when selectedScriptIndex >= pkg.Exec.Length -> Ok()
            | Some pkg ->
                let exec = pkg.Exec.[selectedScriptIndex]
                let kind =
                    self.TechnologyToScriptKind exec.Technology
                    |> Option.defaultWith self.SelectedScriptKind
                match self.EditorToScriptBytes kind self.ScriptEditorText with
                | Error msg -> Error msg
                | Ok bytesOpt ->
                    let bytes = bytesOpt |> Option.defaultValue Array.empty
                    // Keep locked Technology/Trigger; only persist editor bytes.
                    if bytes <> exec.Bytes then
                        // Do not RefreshScriptList here — resetting ItemsSource mid-click
                        // steals the new ListBox selection (feels like a double-click).
                        self.UpdateScriptAt selectedScriptIndex (fun item -> { item with Bytes = bytes })
                        |> ignore
                        self.MarkDirty()
                    Ok()

    member private self.ApplyCurrentScriptToPackage() = self.ApplyScriptToSelectedIndex()

    member private self.SlugFromName(name: string) =
        name.Trim().ToLowerInvariant().Replace(' ', '-')
        |> fun s ->
            s
            |> Seq.map (fun c ->
                if Char.IsLetterOrDigit c || c = '-' || c = '_' then c
                else '-')
            |> Array.ofSeq
            |> String
        |> fun s ->
            let mutable previousDash = false
            let chars = ResizeArray<char>()
            for c in s do
                if c = '-' then
                    if not previousDash && chars.Count > 0 then
                        chars.Add c
                        previousDash <- true
                else
                    chars.Add c
                    previousDash <- false
            String(chars.ToArray()).Trim('-')

    member private self.EnsureManifestSlug() =
        if self.BoxText manifestSlugBox = "" then
            let name = self.BoxText manifestNameBox
            if name <> "" then
                self.SetBoxText(manifestSlugBox, self.SlugFromName name)
                self.MarkDirty()

    member private self.ManifestSlugBoxGotFocus(_: obj, _: GotFocusEventArgs) =
        self.EnsureManifestSlug()

    member private self.ManifestSlugBoxPointerPressed(_: obj, e: PointerPressedEventArgs) =
        self.EnsureManifestSlug()
        e.Handled <- false

    member private self.SetCheck(box: CheckBox, value: bool) =
        if not (isNull box) then
            box.IsChecked <- Nullable(value)

    member private self.CheckOn(box: CheckBox) =
        not (isNull box) && box.IsChecked = Nullable(true)

    member private self.SelectManifestTone(tone: string) =
        if not (isNull manifestToneBox) then
            let wanted = if String.IsNullOrWhiteSpace tone then "stone" else tone
            let mutable found = false
            for i in 0 .. manifestToneBox.ItemCount - 1 do
                match manifestToneBox.Items[i] with
                | :? ComboBoxItem as item when String.Equals(string item.Tag, wanted, StringComparison.OrdinalIgnoreCase) ->
                    manifestToneBox.SelectedIndex <- i
                    found <- true
                | _ -> ()
            if not found then
                manifestToneBox.SelectedIndex <- 0

    member private self.ManifestTone() =
        match manifestToneBox with
        | null -> "stone"
        | box ->
            match box.SelectedItem with
            | :? ComboBoxItem as item when not (isNull item.Tag) -> string item.Tag
            | _ -> "stone"

    member private self.SharedTexts () =
        let label = self.BoxText manifestLabelBox
        let caption = self.BoxText manifestCaptionBox
        Manifest.locales
        |> Array.map (fun locale ->
            {
                Locale = locale
                Label = label
                SectionCaption = caption
            })

    member private self.FirstText (texts: ModPakManifestText[]) (pick: ModPakManifestText -> string) =
        let valueOf (text: ModPakManifestText) =
            let value = pick text
            if isNull value then "" else value
        if isNull (box texts) then
            ""
        else
            texts
            |> Array.tryPick (fun text ->
                let value = valueOf text
                if String.IsNullOrWhiteSpace value then None else Some value)
            |> Option.defaultValue ""

    member private self.ShowManifestIcon() =
        if not (isNull manifestIconImage) then
            if manifestIconBytes.Length = 0 then
                manifestIconImage.Source <- null
            else
                let head =
                    let count = min 256 manifestIconBytes.Length
                    Encoding.UTF8.GetString(manifestIconBytes, 0, count)
                let svg =
                    manifestIconMime.Contains("svg", StringComparison.OrdinalIgnoreCase)
                    || head.Contains("<svg", StringComparison.OrdinalIgnoreCase)
                try
                    if svg then
                        let source = SvgSource.LoadFromSvg(Encoding.UTF8.GetString manifestIconBytes)
                        if isNull source then
                            manifestIconImage.Source <- null
                        else
                            let image = SvgImage()
                            image.Source <- source
                            manifestIconImage.Source <- image
                    else
                        use stream = new MemoryStream(manifestIconBytes, writable = false)
                        manifestIconImage.Source <- new Bitmap(stream)
                with _ ->
                    manifestIconImage.Source <- null

    member private self.FillManifestForm(record: ModPakManifest) =
        manifestId <- if isNull record.Id then "" else record.Id.Trim()
        self.SetBoxText(manifestNameBox, record.Name)
        self.SetBoxText(manifestSlugBox, record.Slug)
        self.SetBoxText(manifestRoleBox, if record.MaxRoleLevel.HasValue then string record.MaxRoleLevel.Value else "")
        self.SelectManifestTone(record.Tone)
        self.SetCheck(manifestSystemCheck, record.System)
        self.SetCheck(manifestActiveCheck, record.IsActive)
        self.SetCheck(manifestVisibleCheck, record.Visible)
        self.SetCheck(manifestDashboardCheck, record.Dashboard)
        self.SetCheck(manifestSectionCheck, record.IsSection)
        self.SetCheck(manifestLockedCheck, record.Locked)
        self.SetBoxText(manifestLabelBox, self.FirstText record.Texts (fun text -> text.Label))
        self.SetBoxText(manifestCaptionBox, self.FirstText record.Texts (fun text -> text.SectionCaption))

    member private self.ReadManifestRecord() =
        let role =
            match self.BoxText manifestRoleBox with
            | "" -> Nullable<int>()
            | text ->
                match Int32.TryParse text with
                | true, value -> Nullable(value)
                | false, _ -> Nullable<int>()
        {
            Id =
                if String.IsNullOrWhiteSpace manifestId then
                    let created = Guid.NewGuid().ToString("D")
                    manifestId <- created
                    created
                else
                    manifestId
            Name = self.BoxText manifestNameBox
            Slug = self.BoxText manifestSlugBox
            Order = 0
            System = self.CheckOn manifestSystemCheck
            IsActive = self.CheckOn manifestActiveCheck
            Visible = self.CheckOn manifestVisibleCheck
            IsSection = self.CheckOn manifestSectionCheck
            Dashboard = self.CheckOn manifestDashboardCheck
            Locked = self.CheckOn manifestLockedCheck
            Icon =
                match self.BoxText manifestSlugBox with
                | "" -> ""
                | slug -> $"icons/{slug}"
            Tone = self.ManifestTone()
            IconType = if manifestIconBytes.Length = 0 then "" else manifestIconMime
            MaxRoleLevel = role
            Texts = self.SharedTexts()
        }

    member private self.RememberManifestIcon(bytes: byte[], mime: string, path: string) =
        manifestIconBytes <- if isNull bytes then [||] else bytes
        manifestIconMime <- if isNull mime then "" else mime
        manifestIconPath <- if isNull path then "" else path
        self.ShowManifestIcon()

    member private self.ApplyManifestPayload(item: ModPakManifestPayload) =
        suppressManifestPersist <- true
        try
            if not (isNull manifestCheck) then
                manifestCheck.IsChecked <- Nullable(true)
            self.FillManifestForm item.Record
            let mime =
                if not (String.IsNullOrWhiteSpace item.IconMime) then item.IconMime
                elif not (String.IsNullOrWhiteSpace item.Record.IconType) then item.Record.IconType
                else ""
            self.RememberManifestIcon(item.IconBytes, mime, "")
            self.ApplyManifestEnabled()
        finally
            suppressManifestPersist <- false

    member private self.CurrentManifestPayload() =
        if not (self.IncludeManifest()) then
            None
        else
            let mime =
                if manifestIconBytes.Length = 0 then ""
                elif String.IsNullOrWhiteSpace manifestIconMime then "application/octet-stream"
                else manifestIconMime
            let record = { self.ReadManifestRecord() with IconType = mime }
            Some {
                Record = record
                IconBytes = manifestIconBytes
                IconMime = mime
            }

    member private self.ManifestFieldChanged(_: obj, _: RoutedEventArgs) =
        if not suppressManifestPersist then
            self.MarkDirty()

    member private self.ManifestToneChanged(_: obj, _: SelectionChangedEventArgs) =
        if not suppressManifestPersist then
            self.MarkDirty()

    member private self.ManifestCheckChanged(_: obj, _: RoutedEventArgs) =
        self.ApplyManifestEnabled()
        if not suppressManifestPersist then
            self.MarkDirty()

    member private self.ClearManifestForm(_: obj, _: RoutedEventArgs) =
        suppressManifestPersist <- true
        try
            self.FillManifestForm(Manifest.empty())
            self.RememberManifestIcon([||], "", "")
        finally
            suppressManifestPersist <- false
        self.Show("Manifest form cleared.", muteBrush)
        self.MarkDirty()

    member private self.ChooseManifestIcon(_: obj, _: RoutedEventArgs) =
        task {
            let! start = self.TryStartFolder(config.OpenFileFolder)
            let options =
                FilePickerOpenOptions(
                    Title = "Manifest icon",
                    AllowMultiple = false,
                    FileTypeFilter =
                        [|
                            FilePickerFileType("Icon", Patterns = [| "*.svg"; "*.png"; "*.webp"; "*.jpg"; "*.jpeg"; "*.gif" |])
                            FilePickerFileType("All files", Patterns = [| "*.*" |])
                        |]
                )
            if not (isNull start) then
                options.SuggestedStartLocation <- start
            let! files = self.StorageProvider.OpenFilePickerAsync(options)
            match files |> Seq.tryHead with
            | None -> ()
            | Some file ->
                let path = file.Path.LocalPath
                if not (String.IsNullOrWhiteSpace path) && File.Exists path then
                    config.RememberOpenFile path
                    self.RememberManifestIcon(File.ReadAllBytes path, Mime.ofPath path, path)
                    self.MarkDirty()
        }
        |> ignore

    member private self.ClearManifestIcon(_: obj, _: RoutedEventArgs) =
        self.RememberManifestIcon([||], "", "")
        self.MarkDirty()

    member private self.NoteSize() =
        if self.WindowState = WindowState.Normal then
            let width = int (round self.Width)
            let height = int (round self.Height)
            if width >= 760 && height >= 560 then
                normalWidth <- width
                normalHeight <- height

    member private self.RememberState() =
        self.NoteSize()
        config.WindowState <-
            match self.WindowState with
            | WindowState.Minimized -> "Minimized"
            | WindowState.Maximized -> "Maximized"
            | WindowState.FullScreen -> "FullScreen"
            | _ -> "Normal"
        config.WindowWidth <- normalWidth
        config.WindowHeight <- normalHeight
        config.Save()

    member private self.SelectedCodec() =
        match codecBox.SelectedItem with
        | :? ComboBoxItem as item when not (isNull item.Tag) ->
            match Byte.TryParse(string item.Tag) with
            | true, value -> value
            | _ -> Codec.Lz4
        | _ -> Codec.Lz4

    member private self.CodecLabel(codec: byte) =
        match codec with
        | Codec.None -> "none"
        | Codec.Lz4 -> "lz4"
        | Codec.Lzma -> "lzma"
        | Codec.Zstd -> "zstd"
        | other -> string other

    member private self.Show(text: string, brush: IBrush) =
        let apply () =
            if not (isNull statusText) then
                statusText.Text <- text
                statusText.Foreground <- brush
        if Dispatcher.UIThread.CheckAccess() then
            apply ()
        else
            Dispatcher.UIThread.Post(System.Action(apply))

    /// Kingston Studio-style blue question mark disc.
    member private self.DialogQuestionMark() =
        let disc =
            Ellipse(
                Width = 50.0,
                Height = 50.0,
                Fill = SolidColorBrush(Color.Parse "#48A0DC"))
        let stem =
            Path(
                Stroke = Brushes.White,
                StrokeThickness = 2.0,
                StrokeLineCap = PenLineCap.Round,
                Data = Geometry.Parse "M18,16 c0,-3.899 3.188,-7.054 7.1,-6.999 c3.717,0.052 6.848,3.182 6.9,6.9 c0.035,2.511 -1.252,4.723 -3.21,5.986 C26.355,23.457 25,26.261 25,29.158 V32")
        let dot =
            Line(
                StartPoint = Point(25.0, 37.0),
                EndPoint = Point(25.0, 39.0),
                Stroke = Brushes.White,
                StrokeThickness = 2.0,
                StrokeLineCap = PenLineCap.Round)
        let canvas = Canvas(Width = 50.0, Height = 50.0)
        canvas.Children.Add(disc)
        canvas.Children.Add(stem)
        canvas.Children.Add(dot)
        Viewbox(
            Width = 28.0,
            Height = 28.0,
            Child = canvas,
            VerticalAlignment = VerticalAlignment.Center)

    member private self.DialogAccentButton(content: string) =
        Button(
            Content = content,
            MinWidth = 88.0,
            MinHeight = 36.0,
            Padding = Thickness(18.0, 9.0),
            CornerRadius = CornerRadius(6.0),
            FontWeight = FontWeight.SemiBold,
            Background = SolidColorBrush(Color.Parse "#5D0F39"),
            BorderBrush = SolidColorBrush(Color.Parse "#5D0F39"),
            BorderThickness = Thickness(1.0),
            Foreground = SolidColorBrush(Color.Parse "#F3EDE6"),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center)

    member private self.DialogCommandButton(content: string) =
        Button(
            Content = content,
            MinWidth = 88.0,
            MinHeight = 36.0,
            Padding = Thickness(18.0, 9.0),
            CornerRadius = CornerRadius(6.0),
            FontWeight = FontWeight.SemiBold,
            Background = SolidColorBrush(Color.Parse "#3A342F"),
            BorderBrush = SolidColorBrush(Color.Parse "#3A342F"),
            BorderThickness = Thickness(1.0),
            Foreground = SolidColorBrush(Color.Parse "#F3EDE6"),
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center)

    member private self.DialogMessageRow(message: string) =
        let mark = self.DialogQuestionMark()
        let text =
            TextBlock(
                Text = message,
                Foreground = SolidColorBrush(Color.Parse "#F3EDE6"),
                FontSize = 15.0,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = Thickness(14.0, 0.0, 0.0, 0.0))
        let row = Grid(Margin = Thickness(0.0, 0.0, 0.0, 18.0))
        row.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength.Auto))
        row.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        Grid.SetColumn(text, 1)
        row.Children.Add(mark)
        row.Children.Add(text)
        row

    member private self.DialogButtonRow(primary: Button, secondary: Button) =
        let buttons =
            StackPanel(
                Orientation = Orientation.Horizontal,
                Spacing = 8.0,
                HorizontalAlignment = HorizontalAlignment.Right)
        buttons.Children.Add(primary)
        buttons.Children.Add(secondary)
        buttons

    member private self.WrapStudioDialog(title: string, root: Control) =
        let material =
            ExperimentalAcrylicMaterial(
                BackgroundSource = AcrylicBackgroundSource.Digger,
                TintColor = Color.Parse "#14110F",
                TintOpacity = 0.15,
                MaterialOpacity = 0.15,
                FallbackColor = Color.Parse "#5914110F")
        let acrylic =
            ExperimentalAcrylicBorder(
                Material = material,
                IsHitTestVisible = false)
        let layer = Grid()
        layer.Children.Add(acrylic)
        layer.Children.Add(root)
        Window(
            Title = title,
            Width = 460.0,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Brushes.Transparent,
            TransparencyLevelHint = ResizeArray [ WindowTransparencyLevel.AcrylicBlur ],
            TransparencyBackgroundFallback = SolidColorBrush(Color.Parse "#5914110F"),
            Icon = WindowIcon(AssetLoader.Open(Uri("avares://ModPak.App/Assets/icon.ico"))),
            Content = layer)

    member private self.DialogOkButton() =
        let ok = self.DialogAccentButton("OK")
        ok.HorizontalAlignment <- HorizontalAlignment.Center
        ok.MinWidth <- 120.0
        ok.Margin <- Thickness(0.0, 8.0, 0.0, 0.0)
        ok

    member private self.ShowConfirmDialog(title: string, message: string) : Task<bool> =
        task {
            let yes = self.DialogAccentButton("Yes")
            let no = self.DialogCommandButton("No")
            let root = StackPanel(Margin = Thickness(22.0))
            root.Children.Add(self.DialogMessageRow message)
            root.Children.Add(self.DialogButtonRow(yes, no))
            let dialog = self.WrapStudioDialog(title, root)
            yes.Click.Add(fun _ -> dialog.Close(true))
            no.Click.Add(fun _ -> dialog.Close(false))
            return! dialog.ShowDialog<bool>(self)
        }

    member private self.ShowRenameDialog(currentPath: string) : Task<string option> =
        task {
            let prompt =
                TextBlock(
                    Text = "Enter a new path for this item.",
                    Foreground = SolidColorBrush(Color.Parse "#F3EDE6"),
                    FontSize = 15.0,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = Thickness(0.0, 0.0, 0.0, 14.0))
            let box =
                TextBox(
                    Text = currentPath,
                    Watermark = "relative/path.ext",
                    MinHeight = 36.0,
                    Margin = Thickness(0.0, 0.0, 0.0, 18.0))
            let rename = self.DialogAccentButton("Rename")
            let cancel = self.DialogCommandButton("Cancel")
            let root = StackPanel(Margin = Thickness(22.0))
            root.Children.Add(prompt)
            root.Children.Add(box)
            root.Children.Add(self.DialogButtonRow(rename, cancel))
            let dialog = self.WrapStudioDialog("Rename", root)
            rename.Click.Add(fun _ ->
                let value = if isNull box.Text then "" else box.Text.Trim().Replace('\\', '/')
                dialog.Close(value))
            cancel.Click.Add(fun _ -> dialog.Close(null))
            box.KeyDown.Add(fun e ->
                if e.Key = Key.Enter || e.Key = Key.Return then
                    let value = if isNull box.Text then "" else box.Text.Trim().Replace('\\', '/')
                    dialog.Close(value)
                    e.Handled <- true)
            let! result = dialog.ShowDialog<string>(self)
            if String.IsNullOrWhiteSpace result then
                return None
            else
                return Some result
        }

    member private self.ShowAlert(title: string, message: string, symbol: Symbol) =
        let icon =
            SymbolIcon(
                Symbol = symbol,
                IconVariant = IconVariant.Color,
                FontSize = 40.0,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = Thickness(0.0, 0.0, 0.0, 10.0))
        let text =
            TextBlock(
                Text = message,
                FontSize = 15.0,
                FontWeight = FontWeight.SemiBold,
                Foreground = SolidColorBrush(Color.Parse "#F0E6DC"),
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = Thickness(0.0, 0.0, 0.0, 18.0))
        let ok: Button = self.DialogOkButton()
        ok.HorizontalAlignment <- Avalonia.Layout.HorizontalAlignment.Center
        ok.MinWidth <- 140.0
        let panel = StackPanel(Spacing = 0.0)
        panel.Children.Add(icon)
        panel.Children.Add(text)
        panel.Children.Add(ok)
        let dialog =
            Window(
                Title = title,
                Width = 340.0,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = SolidColorBrush(Color.Parse "#1A1613"),
                Icon = WindowIcon(AssetLoader.Open(Uri("avares://ModPak.App/Assets/icon.ico"))),
                Content = Border(Padding = Thickness(24.0, 26.0), Child = panel))
        ok.Click.Add(fun _ -> dialog.Close())
        dialog.ShowDialog(self) |> ignore

    member private self.CanSavePackage() =
        isDirty && (packagePath.IsSome || package.IsSome || not pendingPaths.IsEmpty || self.IncludeManifest())

    member private self.UpdateSaveEnabled() =
        let on = self.CanSavePackage()
        if not (isNull saveButton) then
            saveButton.IsEnabled <- on
        if not (isNull saveScriptButton) then
            saveScriptButton.IsEnabled <- on

    member private self.MarkDirty() =
        isDirty <- true
        self.UpdateSaveEnabled()
        self.UpdateWindowTitle()

    member private self.ClearDirty() =
        isDirty <- false
        self.UpdateSaveEnabled()
        self.UpdateWindowTitle()

    member private self.UpdateWindowTitle() =
        let dirtyMark = if isDirty then " *" else ""
        self.Title <-
            match packagePath with
            | Some path when not (String.IsNullOrWhiteSpace path) ->
                $"ModPak {AppVersion} - {Path.GetFileName path}{dirtyMark}"
            | _ when isDirty -> $"ModPak {AppVersion}{dirtyMark}"
            | _ -> $"ModPak {AppVersion}"

    /// File dialogs must start on the UI thread. `task` resumes on the pool after the first await.
    member private self.Ui (work: unit -> Task<'a>) : Task<'a> =
        if Dispatcher.UIThread.CheckAccess() then
            work ()
        else
            Dispatcher.UIThread.InvokeAsync<'a>(System.Func<Task<'a>>(work))

    member private self.TryStartFolder(path: string) =
        task {
            if String.IsNullOrWhiteSpace path || not (Directory.Exists path) then
                return null
            else
                try
                    let! folder = self.StorageProvider.TryGetFolderFromPathAsync(Uri(path))
                    return folder
                with _ ->
                    return null
        }

    member private self.CodecKey(path: string) =
        Pathing.normalize(path).ToLowerInvariant()

    member private self.GetFileCodec(path: string) =
        match fileCodecs.TryFind(self.CodecKey path) with
        | Some codec -> codec
        | None -> self.SelectedCodec()

    member private self.SetFileCodec(path: string, codec: byte) =
        fileCodecs <- fileCodecs.Add(self.CodecKey path, codec)

    member private self.EnsureFileCodec(path: string) =
        let key = self.CodecKey path
        if not (fileCodecs.ContainsKey key) then
            fileCodecs <- fileCodecs.Add(key, self.SelectedCodec())

    member private self.FileRow(path: string, mime: string, bytes: int, codec: byte, canChange: bool) =
        let name =
            let normalized = Pathing.normalize path
            let slash = normalized.LastIndexOf('/')
            if slash < 0 then normalized else normalized.Substring(slash + 1)
        FileListItem(
            icons.GetIcon path,
            name,
            FileListFormatting.mime mime,
            FileListFormatting.size bytes,
            codec,
            path,
            canChange,
            false,
            false)

    member private self.DirRow(dirPath: string) =
        let name =
            let normalized = Pathing.normalize dirPath
            let slash = normalized.LastIndexOf('/')
            if slash < 0 then normalized else normalized.Substring(slash + 1)
        FileListItem(
            icons.GetFolderIcon(),
            name,
            "",
            "<DIR>",
            0uy,
            Pathing.normalize dirPath,
            false,
            true,
            false)

    member private self.ParentDirRow() =
        let parent =
            if String.IsNullOrEmpty explorerDir then
                ""
            else
                let slash = explorerDir.LastIndexOf('/')
                if slash < 0 then "" else explorerDir.Substring(0, slash)
        FileListItem(
            icons.GetFolderIcon(),
            "..",
            "",
            "<DIR>",
            0uy,
            parent,
            false,
            true,
            true)

    /// Package FILE entries merged with staged disk files (staged wins on same relative path).
    member private self.MergePackageAndPendingEntries() : ModPakResult<ModPakEntry list> =
        let existing =
            match package with
            | Some pkg -> pkg.Entries
            | None -> []
        if pendingPaths.IsEmpty then
            Ok existing
        else
            Pack.entriesFromFiles pendingPaths
            |> Result.map self.ApplyPendingRenames
            |> Result.map (fun staged ->
                let stagedKeys =
                    staged
                    |> List.map (fun entry -> self.CodecKey entry.RelativePath)
                    |> Set.ofList
                let kept =
                    existing
                    |> List.filter (fun entry ->
                        not (stagedKeys.Contains(self.CodecKey entry.RelativePath)))
                kept @ staged)

    member private self.AllExplorerFileEntries() : ModPakEntry list =
        match self.MergePackageAndPendingEntries() with
        | Ok entries -> entries
        | Error _ ->
            match package with
            | Some pkg -> pkg.Entries
            | None -> []

    member private self.BuildExplorerRows() : FileListItem list =
        let entries = self.AllExplorerFileEntries()
        let cwd = Pathing.normalize explorerDir
        let prefix = if cwd = "" then "" else cwd + "/"
        let childNames =
            entries
            |> List.map (fun e -> Pathing.normalize e.RelativePath)
            |> List.choose (fun path ->
                if cwd = "" then
                    Some path
                elif path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                    Some(path.Substring(prefix.Length))
                else
                    None)
            |> List.filter (fun rest -> rest <> "")
        let dirs =
            childNames
            |> List.choose (fun rest ->
                let slash = rest.IndexOf('/')
                if slash > 0 then Some(rest.Substring(0, slash)) else None)
            |> List.distinct
            |> List.sortBy (fun s -> s.ToLowerInvariant())
        let filesHere =
            entries
            |> List.choose (fun entry ->
                let path = Pathing.normalize entry.RelativePath
                let rest =
                    if cwd = "" then path
                    elif path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) then
                        path.Substring(prefix.Length)
                    else
                        ""
                if rest = "" || rest.Contains('/') then
                    None
                else
                    Some entry)
            |> List.sortBy (fun e -> (Pathing.normalize e.RelativePath).ToLowerInvariant())
        let parentRows = if cwd = "" then [] else [ self.ParentDirRow() ]
        let dirRows =
            dirs
            |> List.map (fun name ->
                let full = if cwd = "" then name else cwd + "/" + name
                self.DirRow full)
        let fileRows =
            filesHere
            |> List.map (fun entry ->
                self.EnsureFileCodec entry.RelativePath
                let codec =
                    match fileCodecs.TryFind(self.CodecKey entry.RelativePath) with
                    | Some c -> c
                    | None -> entry.Codec
                self.SetFileCodec(entry.RelativePath, codec)
                self.FileRow(entry.RelativePath, entry.Mime, entry.Bytes.Length, codec, true))
        parentRows @ dirRows @ fileRows

    member private self.RefreshExplorer() =
        if isNull fileList then
            ()
        else
            let rows = self.BuildExplorerRows()
            fileList.ItemsSource <- rows
            self.UpdateFileActionsEnabled()

    member private self.NavigateExplorer(dirPath: string) =
        explorerDir <- Pathing.normalize dirPath
        self.RefreshExplorer()

    member private self.SetDiskPathInputValid(ok: bool) =
        if not (isNull diskPathBox) then
            if ok then
                diskPathBox.Classes.Remove "path-invalid" |> ignore
                diskPathBox.BorderBrush <- diskPathOkBrush
            else
                if not (diskPathBox.Classes.Contains "path-invalid") then
                    diskPathBox.Classes.Add "path-invalid"
                diskPathBox.BorderBrush <- diskPathBadBrush

    member private self.IsDiskPathTextValid(text: string) =
        let trimmed = if isNull text then "" else text.Trim()
        String.IsNullOrWhiteSpace trimmed
        || trimmed.Equals("This PC", StringComparison.OrdinalIgnoreCase)
        || Directory.Exists trimmed

    member private self.SetDiskPathLabel(text: string) =
        if not (isNull diskPathBox) then
            suppressDiskPathUi <- true
            try
                diskPathBox.Text <- if isNull text then "" else text
                self.SetDiskPathInputValid true
            finally
                suppressDiskPathUi <- false

    member private self.DiskPathBoxTextChanged(_: obj, _: TextChangedEventArgs) =
        if not suppressDiskPathUi && not (isNull diskPathBox) then
            self.SetDiskPathInputValid(self.IsDiskPathTextValid diskPathBox.Text)

    member private self.ApplyDiskPathFromInput() =
        if isNull diskPathBox then
            ()
        else
            let trimmed = if isNull diskPathBox.Text then "" else diskPathBox.Text.Trim()
            if self.IsDiskPathTextValid trimmed then
                self.SetDiskPathInputValid true
                if String.IsNullOrWhiteSpace trimmed
                   || trimmed.Equals("This PC", StringComparison.OrdinalIgnoreCase) then
                    self.NavigateDisk ""
                else
                    self.NavigateDisk trimmed
            else
                self.SetDiskPathInputValid false

    member private self.ApplyDiskPathClick(_: obj, _: RoutedEventArgs) =
        self.ApplyDiskPathFromInput()

    member private self.DiskPathBoxKeyDown(_: obj, e: KeyEventArgs) =
        if e.Key = Key.Enter || e.Key = Key.Return then
            self.ApplyDiskPathFromInput()
            e.Handled <- true

    member private self.InitDiskExplorer() =
        let start = config.AddFileFolder
        if not (String.IsNullOrWhiteSpace start) && Directory.Exists start then
            diskDir <- Some(Path.GetFullPath start)
        else
            let home = Environment.GetFolderPath Environment.SpecialFolder.UserProfile
            if not (String.IsNullOrWhiteSpace home) && Directory.Exists home then
                diskDir <- Some(Path.GetFullPath home)
            else
                diskDir <- None
        self.RefreshDiskExplorer()

    member private self.BuildDiskDriveRows() : FileListItem list =
        DriveInfo.GetDrives()
        |> Array.choose (fun drive ->
            try
                if not drive.IsReady then
                    None
                else
                    let root = drive.RootDirectory.FullName
                    let label =
                        let vol =
                            try
                                if String.IsNullOrWhiteSpace drive.VolumeLabel then ""
                                else $" ({drive.VolumeLabel})"
                            with _ ->
                                ""
                        $"{root.TrimEnd('\\')}{vol}"
                    Some(
                        FileListItem(
                            icons.GetFolderIcon(),
                            label,
                            "",
                            "<DIR>",
                            0uy,
                            root,
                            false,
                            true,
                            false))
            with _ ->
                None)
        |> Array.toList
        |> List.sortBy (fun row -> row.Name.ToLowerInvariant())

    member private self.BuildDiskFolderRows(dir: string) : FileListItem list =
        let full = Path.GetFullPath dir
        let parent = Directory.GetParent full
        let parentRow =
            match parent with
            | null ->
                // Drive root → go to drive list
                [
                    FileListItem(
                        icons.GetFolderIcon(),
                        "..",
                        "",
                        "<DIR>",
                        0uy,
                        "",
                        false,
                        true,
                        true)
                ]
            | p ->
                [
                    FileListItem(
                        icons.GetFolderIcon(),
                        "..",
                        "",
                        "<DIR>",
                        0uy,
                        p.FullName,
                        false,
                        true,
                        true)
                ]
        let dirs =
            try
                Directory.EnumerateDirectories full
                |> Seq.choose (fun path ->
                    try
                        let name = Path.GetFileName path
                        if String.IsNullOrWhiteSpace name then
                            None
                        else
                            Some(
                                FileListItem(
                                    icons.GetFolderIcon(),
                                    name,
                                    "",
                                    "<DIR>",
                                    0uy,
                                    path,
                                    false,
                                    true,
                                    false))
                    with _ ->
                        None)
                |> Seq.sortBy (fun row -> row.Name.ToLowerInvariant())
                |> Seq.toList
            with _ ->
                []
        let files =
            try
                Directory.EnumerateFiles full
                |> Seq.choose (fun path ->
                    try
                        let name = Path.GetFileName path
                        let info = FileInfo path
                        Some(
                            FileListItem(
                                icons.GetIcon path,
                                name,
                                "",
                                FileListFormatting.size (int (min info.Length (int64 Int32.MaxValue))),
                                0uy,
                                path,
                                false,
                                false,
                                false))
                    with _ ->
                        None)
                |> Seq.sortBy (fun row -> row.Name.ToLowerInvariant())
                |> Seq.toList
            with _ ->
                []
        parentRow @ dirs @ files

    member private self.RefreshDiskExplorer() =
        if isNull diskList then
            ()
        else
            match diskDir with
            | None ->
                self.SetDiskPathLabel "This PC"
                diskList.ItemsSource <- self.BuildDiskDriveRows()
            | Some dir when Directory.Exists dir ->
                self.SetDiskPathLabel dir
                diskList.ItemsSource <- self.BuildDiskFolderRows dir
            | Some _ ->
                diskDir <- None
                self.SetDiskPathLabel "This PC"
                diskList.ItemsSource <- self.BuildDiskDriveRows()

    member private self.NavigateDisk(path: string) =
        if String.IsNullOrWhiteSpace path then
            diskDir <- None
        elif Directory.Exists path then
            let full = Path.GetFullPath path
            diskDir <- Some full
            config.AddFileFolder <- full
            config.Save()
        self.RefreshDiskExplorer()

    member private self.SelectedDiskItem() : FileListItem option =
        if isNull diskList || isNull diskList.SelectedItem then
            None
        else
            match diskList.SelectedItem with
            | :? FileListItem as row -> Some row
            | _ -> None

    member private self.IsDriveRoot(path: string) =
        try
            let full = Path.GetFullPath path
            let root = Path.GetPathRoot full
            not (String.IsNullOrWhiteSpace root)
            && String.Equals(
                full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase)
        with _ ->
            false

    /// Selected Explorer items: files and folders (not `..`, not drive roots).
    member private self.SelectedDiskRoots() : string list =
        if isNull diskList then
            []
        else
            [
                for item in diskList.SelectedItems do
                    match item with
                    | :? FileListItem as row when not row.IsParent ->
                        if (File.Exists row.PathKey || Directory.Exists row.PathKey)
                           && not (self.IsDriveRoot row.PathKey) then
                            yield row.PathKey
                    | _ -> ()
            ]

    member private self.SelectedDiskFilePaths() : string list =
        self.SelectedDiskRoots()
        |> List.filter File.Exists

    member private self.SelectDiskListItemUnder(source: Avalonia.Visual) =
        if isNull diskList || isNull source then
            ()
        else
            let mutable walk = source
            let mutable listItem: ListBoxItem = null
            while not (isNull walk) && isNull listItem do
                match walk with
                | :? ListBoxItem as item -> listItem <- item
                | _ -> walk <- walk.GetVisualParent()
            if not (isNull listItem) && not (isNull listItem.DataContext) then
                let row = listItem.DataContext
                let already =
                    diskList.SelectedItems
                    |> Seq.cast<obj>
                    |> Seq.exists (fun item -> obj.ReferenceEquals(item, row))
                if not already then
                    diskList.SelectedItems.Clear()
                    diskList.SelectedItems.Add(row) |> ignore

    member private self.ClearDiskDragState() =
        diskDragPress <- None
        diskDragOrigin <- None
        diskDragStarted <- false

    member private self.WireExplorerPackageDragDrop() =
        if not (isNull diskList) then
            let onPress (sender: obj) (e: PointerPressedEventArgs) =
                self.DiskListPointerPressed(sender, e)
            let onMove (sender: obj) (e: PointerEventArgs) =
                self.DiskListPointerMoved(sender, e)
            let onRelease (sender: obj) (e: PointerReleasedEventArgs) =
                self.DiskListPointerReleased(sender, e)
            diskList.AddHandler(InputElement.PointerPressedEvent, onPress, RoutingStrategies.Tunnel)
            diskList.AddHandler(InputElement.PointerMovedEvent, onMove, RoutingStrategies.Tunnel)
            diskList.AddHandler(InputElement.PointerReleasedEvent, onRelease, RoutingStrategies.Tunnel)
        if not (isNull fileList) then
            DragDrop.SetAllowDrop(fileList, true)
            DragDrop.AddDragOverHandler(fileList, fun sender e -> self.PackageListDragOver(sender, e))
            DragDrop.AddDropHandler(fileList, fun sender e -> self.PackageListDrop(sender, e))

    member private self.DiskListPointerPressed(_: obj, e: PointerPressedEventArgs) =
        if e.GetCurrentPoint(diskList).Properties.IsLeftButtonPressed then
            match e.Source with
            | :? Avalonia.Visual as visual -> self.SelectDiskListItemUnder visual
            | _ -> ()
            diskDragPress <- Some e
            diskDragOrigin <- Some(e.GetPosition diskList)
            diskDragStarted <- false

    member private self.DiskListPointerMoved(_: obj, e: PointerEventArgs) =
        match diskDragPress, diskDragOrigin with
        | Some press, Some origin when
            not diskDragStarted
            && e.GetCurrentPoint(diskList).Properties.IsLeftButtonPressed ->
            let pos = e.GetPosition diskList
            if abs (pos.X - origin.X) >= 6.0 || abs (pos.Y - origin.Y) >= 6.0 then
                let roots = self.SelectedDiskRoots()
                if roots.IsEmpty then
                    self.ClearDiskDragState()
                else
                    diskDragStarted <- true
                    diskDragPress <- None
                    diskDragOrigin <- None
                    let data = new DataTransfer()
                    data.Add(DataTransferItem.Create(ModPakDnD.PathsFormat, String.Join("\n", roots)))
                    async {
                        try
                            let! _ =
                                DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy)
                                |> Async.AwaitTask
                            ()
                        finally
                            Dispatcher.UIThread.Post(fun () -> self.ClearDiskDragState())
                    }
                    |> Async.StartImmediate
        | _ -> ()

    member private self.DiskListPointerReleased(_: obj, _: PointerReleasedEventArgs) =
        if not diskDragStarted then
            self.ClearDiskDragState()

    member private self.NormalizeDropPaths(paths: string seq) : string list =
        paths
        |> Seq.choose (fun path ->
            if String.IsNullOrWhiteSpace path then None else Some path)
        |> Seq.map Path.GetFullPath
        |> Seq.filter (fun path ->
            (File.Exists path || Directory.Exists path) && not (self.IsDriveRoot path))
        |> Seq.distinct
        |> Seq.toList

    member private self.PathsFromDataTransfer(data: IDataTransfer) : string list =
        let fromApp =
            match data.TryGetValue ModPakDnD.PathsFormat with
            | null | "" -> []
            | text ->
                text.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
                |> Array.toList
        let fromFiles =
            match data.TryGetFiles() with
            | null -> []
            | items ->
                items
                |> Array.choose (fun item ->
                    try
                        let path = item.Path.LocalPath
                        if String.IsNullOrWhiteSpace path then None else Some path
                    with _ ->
                        None)
                |> Array.toList
        self.NormalizeDropPaths (fromApp @ fromFiles)

    member private self.PackageListDragOver(_: obj, e: DragEventArgs) =
        let ok =
            e.DataTransfer.Contains DataFormat.File
            || e.DataTransfer.Contains ModPakDnD.PathsFormat
        e.DragEffects <-
            if ok then e.DragEffects &&& DragDropEffects.Copy
            else DragDropEffects.None
        e.Handled <- true

    member private self.PackageListDrop(_: obj, e: DragEventArgs) =
        let paths = self.PathsFromDataTransfer e.DataTransfer
        if paths.IsEmpty then
            self.Show("Drop files or folders from Explorer.", muteBrush)
        else
            if not (isNull metadataTabs) then
                metadataTabs.SelectedIndex <- 0
            self.CopyDiskSelectionToPackage paths
        e.Handled <- true

    /// Expand selected files/folders into (fullPath, packageRelativePath) pairs.
    member private self.CollectDiskTreeFiles(roots: string list) : (string * string) list =
        let acc = ResizeArray<string * string>()
        for root in roots do
            try
                if File.Exists root then
                    let full = Path.GetFullPath root
                    acc.Add(full, Pathing.normalize (Path.GetFileName full))
                elif Directory.Exists root then
                    let full = Path.GetFullPath root
                    let rootName =
                        let name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        if String.IsNullOrWhiteSpace name then "folder" else name
                    for file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories) do
                        let rel =
                            Path.Combine(rootName, Path.GetRelativePath(full, file))
                            |> Pathing.normalize
                        if Pathing.isSafe rel then
                            acc.Add(Path.GetFullPath file, rel)
            with _ ->
                ()
        acc
        |> Seq.distinctBy (fun (full, _) -> full.ToLowerInvariant())
        |> Seq.sortBy snd
        |> Seq.toList

    member private self.OpenSelectedDiskItem(_: obj, _: RoutedEventArgs) =
        match self.SelectedDiskItem() with
        | Some row when row.IsDirectory || row.IsParent -> self.NavigateDisk row.PathKey
        | Some row when File.Exists row.PathKey -> self.ShellOpen row.PathKey
        | _ -> self.Show("Select a disk item.", muteBrush)

    member private self.DiskListDoubleTapped(_: obj, _: TappedEventArgs) =
        self.OpenSelectedDiskItem(null, null)

    member private self.DiskListKeyDown(_: obj, e: KeyEventArgs) =
        if e.Key = Key.Enter || e.Key = Key.Return then
            self.OpenSelectedDiskItem(null, null)
            e.Handled <- true
        elif e.Key = Key.Back then
            match diskDir with
            | None -> ()
            | Some dir ->
                match Directory.GetParent dir with
                | null -> self.NavigateDisk ""
                | parent -> self.NavigateDisk parent.FullName
            e.Handled <- true

    member private self.AddSelectedDiskFiles(_: obj, _: RoutedEventArgs) =
        let roots = self.SelectedDiskRoots()
        if roots.IsEmpty then
            self.Show("Select files or folders in Explorer to add.", muteBrush)
        else
            self.CopyDiskSelectionToPackage roots

    member private self.MergeBuiltEntriesIntoPackage(entries: ModPakEntry list, absPaths: string list) =
        let pkg = self.EnsurePackage()
        let keys =
            entries
            |> List.map (fun entry -> self.CodecKey entry.RelativePath)
            |> Set.ofList
        let kept =
            pkg.Entries
            |> List.filter (fun entry -> not (keys.Contains(self.CodecKey entry.RelativePath)))
        for entry in entries do
            self.EnsureFileCodec entry.RelativePath
            self.SetFileCodec(entry.RelativePath, entry.Codec)
        package <- Some { pkg with Entries = kept @ entries }
        // Do NOT stage these into pendingPaths — Pack.entriesFromFiles would rebuild
        // relatives from a common folder root and duplicate tree children next to the folder.
        let imported =
            absPaths
            |> List.map Path.GetFullPath
            |> Set.ofList
        pendingPaths <-
            pendingPaths
            |> List.map Path.GetFullPath
            |> List.filter (fun path -> not (imported.Contains path))
        if not absPaths.IsEmpty then
            config.RememberAddFile absPaths.Head
        self.RefreshExplorer()
        self.MarkDirty()
        if not (isNull metadataTabs) then
            metadataTabs.SelectedIndex <- 0

    member private self.CopyDiskSelectionToPackage(roots: string list) =
        let logItems = ObservableCollection<string>()
        let bar =
            ProgressBar(
                Minimum = 0.0,
                Maximum = 100.0,
                Height = 12.0,
                Margin = Thickness(0.0, 0.0, 0.0, 12.0))
        let status =
            TextBlock(
                Text = "Preparing…",
                Foreground = SolidColorBrush(Color.Parse "#F3EDE6"),
                FontSize = 13.0,
                Margin = Thickness(0.0, 0.0, 0.0, 10.0),
                TextWrapping = TextWrapping.Wrap)
        let log =
            ListBox(
                ItemsSource = logItems,
                Height = 200.0,
                Margin = Thickness(0.0, 0.0, 0.0, 12.0))
        let closeBtn = self.DialogAccentButton("Close")
        closeBtn.IsEnabled <- false
        closeBtn.HorizontalAlignment <- HorizontalAlignment.Right
        let panel = StackPanel(Margin = Thickness(22.0))
        panel.Children.Add(status)
        panel.Children.Add(bar)
        panel.Children.Add(log)
        panel.Children.Add(closeBtn)
        let dialog = self.WrapStudioDialog("Adding to package", panel)
        closeBtn.Click.Add(fun _ -> dialog.Close())
        let work =
            Task.Run(fun () ->
                try
                    let files = self.CollectDiskTreeFiles roots
                    if files.IsEmpty then
                        Dispatcher.UIThread.Post(fun () ->
                            status.Text <- "No files found."
                            bar.Value <- 100.0
                            closeBtn.IsEnabled <- true)
                    else
                        let total = files.Length
                        let built = ResizeArray<ModPakEntry>()
                        let abs = ResizeArray<string>()
                        let mutable index = 0
                        for full, rel in files do
                            index <- index + 1
                            let n = index
                            Dispatcher.UIThread.Post(fun () ->
                                status.Text <- $"Copying {n} / {total}"
                                bar.Value <- float n / float total * 100.0
                                logItems.Add rel
                                if logItems.Count > 0 then
                                    log.ScrollIntoView(logItems.[logItems.Count - 1]))
                            try
                                let bytes = File.ReadAllBytes full
                                built.Add(
                                    {
                                        RelativePath = rel
                                        Mime = Mime.ofPath rel
                                        Bytes = bytes
                                        Codec = Codec.Lz4
                                        Meta = FileMetas.ofFilePath full bytes
                                    })
                                abs.Add full
                            with ex ->
                                let msg = $"! {rel}: {ex.Message}"
                                Dispatcher.UIThread.Post(fun () -> logItems.Add msg)
                        let entries = built |> Seq.toList
                        let paths = abs |> Seq.toList
                        Dispatcher.UIThread.Post(fun () ->
                            self.MergeBuiltEntriesIntoPackage(entries, paths)
                            status.Text <- $"Done — {entries.Length} file(s) added."
                            bar.Value <- 100.0
                            closeBtn.IsEnabled <- true
                            self.Show($"Added {entries.Length} file(s) to package.", okBrush))
                with ex ->
                    Dispatcher.UIThread.Post(fun () ->
                        status.Text <- $"Failed: {ex.Message}"
                        closeBtn.IsEnabled <- true
                        self.Show(ex.Message, errBrush)))
        ignore work
        dialog.ShowDialog(self) |> ignore

    member private self.CycleFileCodec(sender: obj, e: RoutedEventArgs) =
        e.Handled <- true
        match sender with
        | :? Button as button ->
            match button.DataContext with
            | :? FileListItem as item when item.CanChangeCodec ->
                let next = FileListFormatting.nextCodec item.Codec
                self.SetFileCodec(item.PathKey, next)
                self.RefreshExplorer()
                self.MarkDirty()
            | _ -> ()
        | _ -> ()

    member private self.ApplyPendingRenames(entries: ModPakEntry list) =
        entries
        |> List.map (fun entry ->
            match pendingRenames.TryFind(self.CodecKey entry.RelativePath) with
            | None -> entry
            | Some renamed ->
                let path = Pathing.normalize renamed
                {
                    entry with
                        RelativePath = path
                        Mime = Mime.ofPath path
                })

    member private self.StageFilesIntoPackage(paths: string list) =
        match paths with
        | [] -> ()
        | head :: _ ->
            config.RememberAddFile head
            // Keep existing package Entries — staged paths merge on save / explorer.
            pendingPaths <-
                (pendingPaths @ paths)
                |> List.map Path.GetFullPath
                |> List.distinct
                |> List.sort
            match Pack.entriesFromFiles paths with
            | Ok files ->
                for entry in files do
                    self.EnsureFileCodec entry.RelativePath
            | Error error -> self.Show(string error, errBrush)
            self.RefreshExplorer()
            self.MarkDirty()
            let added = paths.Length
            let total =
                match self.MergePackageAndPendingEntries() with
                | Ok entries -> entries.Length
                | Error _ -> added
            self.Show($"Added {added} file(s) — package now has {total}.", okBrush)

    member private self.RefreshPendingList() =
        match pendingPaths with
        | [] -> self.RefreshExplorer()
        | _ ->
            match Pack.entriesFromFiles pendingPaths with
            | Error error -> self.Show(string error, errBrush)
            | Ok raw ->
                for entry in self.ApplyPendingRenames raw do
                    self.EnsureFileCodec entry.RelativePath
                self.RefreshExplorer()
                self.Show($"Staging {pendingPaths.Length} new file(s) — existing package files kept.", muteBrush)

    member private self.ShowPackageFiles(_current: ModPakPackage) =
        self.RefreshExplorer()

    /// Selected package files only (not folders / `..`).
    member private self.SelectedPathKeys() =
        if isNull fileList then
            []
        else
            [
                for item in fileList.SelectedItems do
                    match item with
                    | :? FileListItem as row when not row.IsDirectory && not row.IsParent ->
                        yield row.PathKey
                    | _ -> ()
            ]

    /// Selected package files and folders (not `..`) — for rename / delete.
    member private self.SelectedEntryPathKeys() =
        if isNull fileList then
            []
        else
            [
                for item in fileList.SelectedItems do
                    match item with
                    | :? FileListItem as row when not row.IsParent ->
                        yield row.PathKey
                    | _ -> ()
            ]

    member private self.PathIsUnder(path: string, dir: string) =
        let p = Pathing.normalize path
        let d = Pathing.normalize dir
        not (String.IsNullOrWhiteSpace d)
        && (String.Equals(p, d, StringComparison.OrdinalIgnoreCase)
            || p.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))

    member private self.ReplaceDirPrefix(path: string, oldDir: string, newDir: string) =
        let p = Pathing.normalize path
        let o = Pathing.normalize oldDir
        let n = Pathing.normalize newDir
        if String.Equals(p, o, StringComparison.OrdinalIgnoreCase) then
            n
        elif p.StartsWith(o + "/", StringComparison.OrdinalIgnoreCase) then
            n + p.Substring(o.Length)
        else
            p

    member private self.PathMatchesSelection(path: string, selected: string list) =
        selected |> List.exists (fun sel -> self.PathIsUnder(path, sel))

    member private self.IsVirtualDirectory(dir: string) =
        let d = Pathing.normalize dir
        self.AllExplorerFileEntries()
        |> List.exists (fun entry ->
            let p = Pathing.normalize entry.RelativePath
            p.StartsWith(d + "/", StringComparison.OrdinalIgnoreCase))

    member private self.WouldConflictDirectoryRename(oldDir: string, newDir: string) =
        let oldP = Pathing.normalize oldDir
        let newP = Pathing.normalize newDir
        if String.Equals(oldP, newP, StringComparison.OrdinalIgnoreCase) then
            false
        else
            let entries =
                self.AllExplorerFileEntries()
                |> List.map (fun e -> Pathing.normalize e.RelativePath)
            let remapped =
                entries
                |> List.filter (fun p -> self.PathIsUnder(p, oldP))
                |> List.map (fun p -> self.ReplaceDirPrefix(p, oldP, newP))
            let staying =
                entries
                |> List.filter (fun p -> not (self.PathIsUnder(p, oldP)))
                |> List.map (fun p -> p.ToLowerInvariant())
                |> Set.ofList
            let remappedKeys = remapped |> List.map (fun p -> p.ToLowerInvariant())
            remappedKeys |> List.exists staying.Contains
            || remappedKeys.Length <> (remappedKeys |> List.distinct).Length

    member private self.EnsureExplorerDirValid() =
        let mutable cwd = Pathing.normalize explorerDir
        while cwd <> "" do
            let prefix = cwd + "/"
            let any =
                self.AllExplorerFileEntries()
                |> List.exists (fun entry ->
                    let p = Pathing.normalize entry.RelativePath
                    String.Equals(p, cwd, StringComparison.OrdinalIgnoreCase)
                    || p.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            if any then
                explorerDir <- cwd
                cwd <- ""
            else
                let slash = cwd.LastIndexOf('/')
                cwd <- if slash < 0 then "" else cwd.Substring(0, slash)
                if cwd = "" then
                    explorerDir <- ""

    member private self.SelectedExplorerItem() : FileListItem option =
        if isNull fileList || isNull fileList.SelectedItem then
            None
        else
            match fileList.SelectedItem with
            | :? FileListItem as row -> Some row
            | _ -> None

    member private self.AllUnpackPathKeys() : string list =
        self.AllExplorerFileEntries()
        |> List.map (fun entry -> entry.RelativePath)

    member private self.ShellOpen(path: string) =
        try
            let info = ProcessStartInfo(FileName = path, UseShellExecute = true)
            Process.Start(info) |> ignore
            self.Show($"Opened {Path.GetFileName path}", okBrush)
        with error ->
            self.Show(error.Message, errBrush)

    member private self.EnsureMenuItemRefs() =
        if isNull openMenuItem then
            openMenuItem <- self.FindControl<MenuItem>("OpenMenuItem")
        if isNull detailsMenuItem then
            detailsMenuItem <- self.FindControl<MenuItem>("DetailsMenuItem")
        if isNull renameMenuItem then
            renameMenuItem <- self.FindControl<MenuItem>("RenameMenuItem")
        if isNull deleteMenuItem then
            deleteMenuItem <- self.FindControl<MenuItem>("DeleteMenuItem")
        if isNull unpakMenuItem then
            unpakMenuItem <- self.FindControl<MenuItem>("UnpakMenuItem")

    member private self.UpdateFileActionsEnabled() =
        self.EnsureMenuItemRefs()
        let selectedFiles = self.SelectedPathKeys()
        let selectedEntries = self.SelectedEntryPathKeys()
        let canOpen = selectedFiles.Length = 1
        let canEdit =
            selectedEntries.Length = 1
            && (package.IsSome || not pendingPaths.IsEmpty)
        let canDelete =
            not selectedEntries.IsEmpty
            && (package.IsSome || not pendingPaths.IsEmpty)
        // Package in memory, or staged files still on disk.
        let canUnpak =
            not selectedFiles.IsEmpty
            && (package.IsSome || not pendingPaths.IsEmpty)
        let canUnpakAll = not (self.AllUnpackPathKeys().IsEmpty)
        let canOpenItem =
            match self.SelectedExplorerItem() with
            | Some _ -> true
            | None -> false
        if not (isNull openMenuItem) then
            openMenuItem.IsEnabled <- canOpenItem
        if not (isNull detailsMenuItem) then
            detailsMenuItem.IsEnabled <- canOpen
        if not (isNull renameMenuItem) then
            renameMenuItem.IsEnabled <- canEdit
        if not (isNull deleteMenuItem) then
            deleteMenuItem.IsEnabled <- canDelete
        if not (isNull unpackButton) then
            unpackButton.IsEnabled <- canUnpak
        if not (isNull unpackAllButton) then
            unpackAllButton.IsEnabled <- canUnpakAll
        if not (isNull unpakMenuItem) then
            unpakMenuItem.IsEnabled <- canUnpak
        if not (isNull infoButton) then
            infoButton.IsEnabled <- packagePath.IsSome
        self.UpdateSaveEnabled()

    member private self.SelectListItemUnder(source: Avalonia.Visual) =
        if isNull fileList || isNull source then
            ()
        else
            let mutable walk = source
            let mutable listItem: ListBoxItem = null
            while not (isNull walk) && isNull listItem do
                match walk with
                | :? ListBoxItem as item -> listItem <- item
                | _ -> walk <- walk.GetVisualParent()
            if not (isNull listItem) && not (isNull listItem.DataContext) then
                let row = listItem.DataContext
                let already =
                    fileList.SelectedItems
                    |> Seq.cast<obj>
                    |> Seq.exists (fun item -> obj.ReferenceEquals(item, row))
                if not already then
                    fileList.SelectedItems.Clear()
                    fileList.SelectedItems.Add(row) |> ignore

    member private self.FileListSelectionChanged(_: obj, _: SelectionChangedEventArgs) =
        self.UpdateFileActionsEnabled()

    member private self.FileListContextRequested(_: obj, e: ContextRequestedEventArgs) =
        match e.Source with
        | :? Avalonia.Visual as source -> self.SelectListItemUnder source
        | _ -> ()
        self.UpdateFileActionsEnabled()
        e.Handled <- false

    member private self.FileListMenuOpening(sender: obj, _: CancelEventArgs) =
        match sender with
        | :? ContextMenu as menu ->
            for item in menu.Items do
                match item with
                | :? MenuItem as mi when mi.Name = "OpenMenuItem" -> openMenuItem <- mi
                | :? MenuItem as mi when mi.Name = "DetailsMenuItem" -> detailsMenuItem <- mi
                | :? MenuItem as mi when mi.Name = "RenameMenuItem" -> renameMenuItem <- mi
                | :? MenuItem as mi when mi.Name = "DeleteMenuItem" -> deleteMenuItem <- mi
                | :? MenuItem as mi when mi.Name = "UnpakMenuItem" -> unpakMenuItem <- mi
                | _ -> ()
        | _ -> ()
        self.UpdateFileActionsEnabled()

    member private self.PathAlreadyUsed(path: string) (exceptKey: string) =
        let wanted = Pathing.normalize path
        let except = Pathing.normalize exceptKey
        let conflicts (p: string) =
            not (self.PathIsUnder(p, except))
            && (String.Equals(p, wanted, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(wanted + "/", StringComparison.OrdinalIgnoreCase)
                || wanted.StartsWith(p + "/", StringComparison.OrdinalIgnoreCase))
        let inPackage =
            match package with
            | None -> false
            | Some pkg ->
                pkg.Entries
                |> List.exists (fun e -> conflicts (Pathing.normalize e.RelativePath))
        let inStaging =
            match Pack.entriesFromFiles pendingPaths with
            | Error _ -> false
            | Ok entries ->
                self.ApplyPendingRenames entries
                |> List.exists (fun e -> conflicts (Pathing.normalize e.RelativePath))
        inPackage || inStaging

    member private self.RefreshAfterEntryEdit() =
        self.EnsureExplorerDirValid()
        if not pendingPaths.IsEmpty then
            self.RefreshPendingList()
        else
            match package with
            | Some pkg -> self.ShowPackageFiles pkg
            | None ->
                self.RefreshExplorer()
                self.UpdateFileActionsEnabled()

    member private self.RemapFileCodecs(oldDir: string, newDir: string) =
        let oldP = Pathing.normalize oldDir
        let newP = Pathing.normalize newDir
        fileCodecs <-
            fileCodecs
            |> Map.toList
            |> List.map (fun (key, codec) ->
                if self.PathIsUnder(key, oldP) then
                    self.CodecKey(self.ReplaceDirPrefix(key, oldP, newP)), codec
                else
                    key, codec)
            |> Map.ofList

    member private self.RenameSelectedFile(_: obj, _: RoutedEventArgs) =
        match self.SelectedEntryPathKeys() with
        | [ pathKey ] ->
            let isDir =
                match self.SelectedExplorerItem() with
                | Some row -> row.IsDirectory
                | None -> self.IsVirtualDirectory pathKey
            task {
                let! renamed = self.ShowRenameDialog pathKey
                match renamed with
                | None -> ()
                | Some raw ->
                    let nextPath = Pathing.normalize raw
                    if nextPath = "" || not (Pathing.isSafe nextPath) then
                        self.Show("Invalid path.", errBrush)
                    elif String.Equals(nextPath, Pathing.normalize pathKey, StringComparison.OrdinalIgnoreCase) then
                        ()
                    elif isDir && self.WouldConflictDirectoryRename(pathKey, nextPath) then
                        self.Show($"Path already used: {nextPath}", errBrush)
                    elif (not isDir) && self.PathAlreadyUsed nextPath pathKey then
                        self.Show($"Path already used: {nextPath}", errBrush)
                    else
                        let oldPath = Pathing.normalize pathKey
                        if isDir then
                            self.RemapFileCodecs(oldPath, nextPath)
                            if not pendingPaths.IsEmpty then
                                match Pack.entriesFromFiles pendingPaths with
                                | Error error -> self.Show(string error, errBrush)
                                | Ok entries ->
                                    let mutable nextRenames = pendingRenames
                                    for entry in entries do
                                        let orig = Pathing.normalize entry.RelativePath
                                        let shown =
                                            match pendingRenames.TryFind(self.CodecKey orig) with
                                            | Some renamedPath -> Pathing.normalize renamedPath
                                            | None -> orig
                                        if self.PathIsUnder(shown, oldPath) then
                                            let mapped = self.ReplaceDirPrefix(shown, oldPath, nextPath)
                                            nextRenames <-
                                                nextRenames
                                                |> Map.filter (fun k _ -> k <> self.CodecKey orig)
                                                |> Map.add (self.CodecKey orig) mapped
                                    pendingRenames <- nextRenames
                            match package with
                            | Some pkg ->
                                let entries =
                                    pkg.Entries
                                    |> List.map (fun e ->
                                        let p = Pathing.normalize e.RelativePath
                                        if self.PathIsUnder(p, oldPath) then
                                            let mapped = self.ReplaceDirPrefix(p, oldPath, nextPath)
                                            {
                                                e with
                                                    RelativePath = mapped
                                                    Mime = Mime.ofPath mapped
                                            }
                                        else
                                            e)
                                package <- Some { pkg with Entries = entries }
                            | None -> ()
                            if self.PathIsUnder(explorerDir, oldPath) then
                                explorerDir <- self.ReplaceDirPrefix(explorerDir, oldPath, nextPath)
                            self.RefreshAfterEntryEdit()
                            self.Show($"Renamed folder to {nextPath}", okBrush)
                            self.MarkDirty()
                        else
                            let oldKey = self.CodecKey pathKey
                            let codec = self.GetFileCodec pathKey
                            fileCodecs <- fileCodecs.Remove(oldKey).Add(self.CodecKey nextPath, codec)
                            if not pendingPaths.IsEmpty then
                                let sourceKey =
                                    pendingRenames
                                    |> Map.tryFindKey (fun _ value ->
                                        String.Equals(
                                            Pathing.normalize value,
                                            Pathing.normalize pathKey,
                                            StringComparison.OrdinalIgnoreCase))
                                    |> Option.defaultValue oldKey
                                pendingRenames <-
                                    pendingRenames
                                    |> Map.filter (fun k _ -> k <> sourceKey)
                                    |> Map.add sourceKey nextPath
                            match package with
                            | Some pkg ->
                                let entries =
                                    pkg.Entries
                                    |> List.map (fun e ->
                                        if String.Equals(
                                            Pathing.normalize e.RelativePath,
                                            Pathing.normalize pathKey,
                                            StringComparison.OrdinalIgnoreCase) then
                                            {
                                                e with
                                                    RelativePath = nextPath
                                                    Mime = Mime.ofPath nextPath
                                            }
                                        else
                                            e)
                                package <- Some { pkg with Entries = entries }
                            | None -> ()
                            self.RefreshAfterEntryEdit()
                            self.Show($"Renamed to {nextPath}", okBrush)
                            self.MarkDirty()
            }
            |> ignore
        | _ -> self.Show("Select one item to rename.", muteBrush)

    member private self.DeleteSelectedFiles(_: obj, _: RoutedEventArgs) =
        let selected = self.SelectedEntryPathKeys()
        if selected.IsEmpty then
            self.Show("Select an item to delete.", muteBrush)
        else
            task {
                let hasFolder =
                    match self.SelectedExplorerItem() with
                    | Some row when row.IsDirectory -> true
                    | _ ->
                        selected
                        |> List.exists self.IsVirtualDirectory
                let message =
                    if selected.Length = 1 && hasFolder then
                        "Do you really want to delete this folder and everything inside it?"
                    elif selected.Length = 1 then
                        "Do you really want to delete this item?"
                    else
                        $"Do you really want to delete these {selected.Length} items?"
                let! confirmed = self.ShowConfirmDialog("Delete", message)
                if confirmed then
                    let mutable removed = 0
                    if not pendingPaths.IsEmpty then
                        let removeFulls =
                            match Pack.entriesFromFiles pendingPaths with
                            | Error _ -> Set.empty
                            | Ok entries ->
                                entries
                                |> List.fold
                                    (fun acc entry ->
                                        let orig = Pathing.normalize entry.RelativePath
                                        let shown =
                                            match pendingRenames.TryFind(self.CodecKey orig) with
                                            | Some r -> Pathing.normalize r
                                            | None -> orig
                                        if self.PathMatchesSelection(shown, selected)
                                           || self.PathMatchesSelection(orig, selected) then
                                            match Pack.tryFullPath pendingPaths orig with
                                            | Some full -> Set.add full acc
                                            | None -> acc
                                        else
                                            acc)
                                    Set.empty
                        removed <- removed + removeFulls.Count
                        pendingPaths <-
                            pendingPaths
                            |> List.filter (fun p -> not (removeFulls.Contains(Path.GetFullPath p)))
                        pendingRenames <-
                            pendingRenames
                            |> Map.filter (fun oldKey newPath ->
                                not (self.PathMatchesSelection(oldKey, selected))
                                && not (self.PathMatchesSelection(newPath, selected)))
                        fileCodecs <-
                            fileCodecs
                            |> Map.filter (fun key _ -> not (self.PathMatchesSelection(key, selected)))
                    match package with
                    | Some pkg ->
                        let kept, dropped =
                            pkg.Entries
                            |> List.partition (fun e ->
                                not (self.PathMatchesSelection(e.RelativePath, selected)))
                        removed <- removed + dropped.Length
                        package <- Some { pkg with Entries = kept }
                        fileCodecs <-
                            fileCodecs
                            |> Map.filter (fun key _ -> not (self.PathMatchesSelection(key, selected)))
                    | None -> ()
                    if pendingPaths.IsEmpty && package.IsNone then
                        explorerDir <- ""
                    self.RefreshAfterEntryEdit()
                    let label = if removed = 1 then "item" else "items"
                    self.Show($"Deleted {removed} {label}.", okBrush)
                    self.MarkDirty()
            }
            |> ignore

    member private self.NewPackage(_: obj, _: RoutedEventArgs) =
        pendingPaths <- []
        pendingRenames <- Map.empty
        package <- None
        packagePath <- None
        fileCodecs <- Map.empty
        explorerDir <- ""
        self.ClearDirty()
        selectedScriptIndex <- -1
        self.RefreshScriptList()
        self.LoadScriptEditor()
        self.RefreshExplorer()
        self.UpdateWindowTitle()
        self.Show("New package — add files or open an existing .mpx.", muteBrush)

    member private self.SelectAllFiles(_: obj, _: RoutedEventArgs) =
        if not (isNull fileList) then
            fileList.SelectAll()
            self.UpdateFileActionsEnabled()

    member private self.UnselectAllFiles(_: obj, _: RoutedEventArgs) =
        if not (isNull fileList) then
            fileList.UnselectAll()
            self.UpdateFileActionsEnabled()

    member private self.FileListDoubleTapped(sender: obj, e: TappedEventArgs) =
        let mutable walk = e.Source :?> Avalonia.Visual
        let mutable fromCodec = false
        while not (isNull walk) && not fromCodec do
            match walk with
            | :? Button as button when button.Classes.Contains("codec-chip") -> fromCodec <- true
            | _ -> walk <- walk.GetVisualParent()
        if not fromCodec then
            self.OpenSelectedFile(sender, e)

    member private self.FileListKeyDown(sender: obj, e: KeyEventArgs) =
        if e.Key = Key.Enter || e.Key = Key.Return then
            self.OpenSelectedFile(sender, e)
            e.Handled <- true
        elif e.Key = Key.Back then
            if not (String.IsNullOrEmpty explorerDir) then
                self.NavigateExplorer(
                    let slash = explorerDir.LastIndexOf('/')
                    if slash < 0 then "" else explorerDir.Substring(0, slash))
                e.Handled <- true

    member private self.OpenSelectedFile(_: obj, _: RoutedEventArgs) =
        match self.SelectedExplorerItem() with
        | Some row when row.IsDirectory || row.IsParent ->
            self.NavigateExplorer row.PathKey
        | Some row ->
            let pathKey = row.PathKey
            match Pack.tryFullPath pendingPaths pathKey with
            | Some diskPath -> self.ShellOpen diskPath
            | None ->
                match package with
                | None -> self.Show("Nothing to open.", muteBrush)
                | Some current ->
                    try
                        let tempRoot =
                            Path.Combine(Path.GetTempPath(), "ModPak", "open", Guid.NewGuid().ToString("N"))
                        match Unpack.selectedToDirectory current [ pathKey ] tempRoot with
                        | Error error -> self.Show(string error, errBrush)
                        | Ok () ->
                            let target =
                                Path.Combine(tempRoot, pathKey.Replace('/', Path.DirectorySeparatorChar))
                            if File.Exists target then
                                self.ShellOpen target
                            else
                                self.Show($"Could not prepare {pathKey}.", errBrush)
                    with error ->
                        self.Show(error.Message, errBrush)
        | None -> self.Show("Select one item to open.", muteBrush)

    member private self.TryResolveEntry(pathKey: string) : ModPakEntry option =
        let wanted = Pathing.normalize pathKey
        let fromPackage =
            match package with
            | None -> None
            | Some current ->
                current.Entries
                |> List.tryFind (fun entry ->
                    String.Equals(Pathing.normalize entry.RelativePath, wanted, StringComparison.OrdinalIgnoreCase))
        match fromPackage with
        | Some entry -> Some entry
        | None ->
            match Pack.tryFullPath pendingPaths pathKey with
            | Some disk ->
                let bytes = File.ReadAllBytes disk
                Some {
                    RelativePath = wanted
                    Mime = Mime.ofPath wanted
                    Bytes = bytes
                    Codec = self.GetFileCodec wanted
                    Meta = FileMetas.ofFilePath disk bytes
                }
            | None -> None

    member private self.FormatUnixMs(ms: uint64) =
        if ms = 0UL then
            "—"
        else
            DateTimeOffset.FromUnixTimeMilliseconds(int64 ms).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss")

    member private self.InfoPropertyGrid(rows: (string * string) list) =
        let grid = Grid(ColumnDefinitions = ColumnDefinitions("Auto,*"), RowDefinitions = RowDefinitions())
        rows
        |> List.iteri (fun i (label, value) ->
            grid.RowDefinitions.Add(RowDefinition(Height = GridLength.Auto))
            let labelBlock =
                TextBlock(
                    Text = label,
                    FontSize = 12.0,
                    Foreground = SolidColorBrush(Color.Parse "#A89888"),
                    Margin = Thickness(0.0, 0.0, 14.0, 8.0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center)
            let valueBlock =
                TextBlock(
                    Text = value,
                    FontSize = 12.0,
                    Foreground = SolidColorBrush(Color.Parse "#F0E6DC"),
                    TextWrapping = TextWrapping.Wrap,
                    Margin = Thickness(0.0, 0.0, 0.0, 8.0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center)
            Grid.SetRow(labelBlock, i)
            Grid.SetColumn(labelBlock, 0)
            Grid.SetRow(valueBlock, i)
            Grid.SetColumn(valueBlock, 1)
            grid.Children.Add(labelBlock)
            grid.Children.Add(valueBlock))
        grid

    member private self.BuildFileInfoContent(entry: ModPakEntry) =
        let meta = entry.Meta
        let rows =
            [
                "Path", entry.RelativePath
                "MIME", (if String.IsNullOrWhiteSpace entry.Mime then "—" else entry.Mime)
                "Compression", self.CodecLabel entry.Codec
                "Size", FileListFormatting.size entry.Bytes.Length
                "UnpackedSize", string meta.UnpackedSize
                "Permissions", $"0o{Convert.ToString(int meta.Permissions, 8)}"
                "Created", self.FormatUnixMs meta.CreatedTime
                "Modified", self.FormatUnixMs meta.ModifiedTime
                "Accessed", self.FormatUnixMs meta.AccessedTime
                "CRC32", $"0x{meta.Crc32:X8}"
            ]
        let fileIcon = icons.GetIcon entry.RelativePath
        let titleText =
            TextBlock(
                Text = Path.GetFileName entry.RelativePath,
                FontSize = 16.0,
                FontWeight = FontWeight.SemiBold,
                Foreground = SolidColorBrush(Color.Parse "#F0E6DC"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap)
        let titleRow = DockPanel(LastChildFill = true, Margin = Thickness(0.0, 0.0, 0.0, 12.0))
        if not (isNull fileIcon) then
            let iconImage =
                Image(
                    Source = fileIcon,
                    Width = 36.0,
                    Height = 36.0,
                    Margin = Thickness(0.0, 0.0, 10.0, 0.0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center)
            DockPanel.SetDock(iconImage, Avalonia.Controls.Dock.Left)
            titleRow.Children.Add(iconImage)
        titleRow.Children.Add(titleText)
        let panel = StackPanel(Spacing = 4.0)
        panel.Children.Add(titleRow)
        panel.Children.Add(self.InfoPropertyGrid rows)
        panel :> Control

    member private self.TryPackageForInfo() =
        match package with
        | Some pkg -> Some pkg
        | None when not pendingPaths.IsEmpty ->
            match Pack.entriesFromFiles pendingPaths with
            | Error _ -> None
            | Ok files ->
                Some {
                    Manifest = self.CurrentManifestPayload()
                    Exec = []
                    Entries = files
                }
        | None -> None

    member private self.BuildContainerInfoContent(pkg: ModPakPackage) =
        let major, minor = self.ContainerVersion()
        let rows =
            [
                "Format", "ModPak"
                "Container", "MODPAK64"
                "Version", $"{major}.{minor}"
            ]
            @ (self.ChunkCounts pkg |> List.map (fun (id, n) -> id, string n))
        let logo =
            Image(
                Source = new Bitmap(AssetLoader.Open(Uri("avares://ModPak.App/Assets/KI.png"))),
                Width = 44.0,
                Height = 44.0,
                Margin = Thickness(0.0, 0.0, 12.0, 0.0),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center)
        let titleText =
            TextBlock(
                Text =
                    (match packagePath with
                     | Some path -> Path.GetFileName path
                     | None -> "ModPak"),
                FontSize = 16.0,
                FontWeight = FontWeight.SemiBold,
                Foreground = SolidColorBrush(Color.Parse "#F0E6DC"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                TextWrapping = TextWrapping.NoWrap)
        let titleRow = DockPanel(LastChildFill = true, Margin = Thickness(0.0, 0.0, 0.0, 12.0))
        DockPanel.SetDock(logo, Avalonia.Controls.Dock.Left)
        titleRow.Children.Add(logo)
        titleRow.Children.Add(titleText)
        let panel = StackPanel(Spacing = 4.0)
        panel.Children.Add(titleRow)
        panel.Children.Add(self.InfoPropertyGrid rows)
        panel :> Control

    member private self.ShowInfoDialog(_: obj, _: RoutedEventArgs) =
        let fileOpt =
            match self.SelectedPathKeys() with
            | [ pathKey ] -> self.TryResolveEntry pathKey
            | _ -> None
        let pkgOpt = self.TryPackageForInfo()
        match fileOpt, pkgOpt with
        | None, None -> self.Show("Open a package or select a file.", muteBrush)
        | _ ->
            let tabs = TabControl()
            match fileOpt with
            | Some entry ->
                tabs.Items.Add(TabItem(Header = "Súbor", Content = self.BuildFileInfoContent entry))
                |> ignore
            | None -> ()
            match pkgOpt with
            | Some pkg ->
                tabs.Items.Add(TabItem(Header = "Kontajner", Content = self.BuildContainerInfoContent pkg))
                |> ignore
            | None -> ()
            tabs.SelectedIndex <- 0
            let close: Button = self.DialogOkButton()
            close.HorizontalAlignment <- Avalonia.Layout.HorizontalAlignment.Right
            close.MinWidth <- 96.0
            close.MinHeight <- 32.0
            close.Padding <- Thickness(16.0, 8.0)
            close.Margin <- Thickness(0.0, 14.0, 0.0, 0.0)
            let root = DockPanel()
            DockPanel.SetDock(close, Avalonia.Controls.Dock.Bottom)
            root.Children.Add(close)
            root.Children.Add(tabs)
            let dialog =
                Window(
                    Title = "Info",
                    Width = 460.0,
                    SizeToContent = SizeToContent.Height,
                    CanResize = false,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Background = SolidColorBrush(Color.Parse "#14110F"),
                    Icon = WindowIcon(AssetLoader.Open(Uri("avares://ModPak.App/Assets/icon.ico"))),
                    Content = Border(Padding = Thickness(20.0), Child = root))
            close.Click.Add(fun _ -> dialog.Close())
            dialog.ShowDialog(self) |> ignore

    member private self.SetManifestCheck(hasManifest: bool) =
        suppressManifestPersist <- true
        try
            if not (isNull manifestCheck) then
                manifestCheck.IsChecked <- Nullable(hasManifest)
            self.ApplyManifestEnabled()
        finally
            suppressManifestPersist <- false

    member private self.ShowPackage(path: string option, next: ModPakPackage) =
        pendingPaths <- []
        pendingRenames <- Map.empty
        explorerDir <- ""
        package <- Some next
        packagePath <- path
        // Seed codecs from archive FILE entries (overwrites prior staging picks for same names).
        fileCodecs <-
            next.Entries
            |> List.fold (fun map entry -> map.Add(self.CodecKey entry.RelativePath, entry.Codec)) Map.empty
        match next.Manifest with
        | Some item -> self.ApplyManifestPayload item
        | None ->
            suppressManifestPersist <- true
            try
                if not (isNull manifestCheck) then
                    manifestCheck.IsChecked <- Nullable(false)
                self.ApplyManifestEnabled()
            finally
                suppressManifestPersist <- false
        self.ShowPackageFiles next
        self.SelectScriptKindFromPackage next
        self.LoadScriptEditor()
        self.ClearDirty()
        match path with
        | Some value when not (String.IsNullOrWhiteSpace value) ->
            config.RememberLastPackage value
        | _ -> ()
        let where =
            match path with
            | Some value -> value
            | None -> "memory"
        let scripts =
            next.Exec.Length
        let title =
            match next.Manifest with
            | Some m -> m.Record.Name
            | None -> "(no manifest)"
        self.Show($"Loaded {title} · {next.Entries.Length} files · {scripts} scripts · {where}", okBrush)

    member private self.TryLoadPackageFromPath(path: string) =
        let full =
            try
                Path.GetFullPath path
            with _ ->
                path
        if String.IsNullOrWhiteSpace full || not (File.Exists full) then
            self.Show($"File not found: {path}", errBrush)
            false
        else
            match Unpack.read full with
            | Error error ->
                self.Show(string error, errBrush)
                false
            | Ok next ->
                match Validate.package next with
                | Error error ->
                    self.Show(string error, errBrush)
                    false
                | Ok () ->
                    self.ShowPackage(Some full, next)
                    true

    member private self.StartupPackageArg() =
        Environment.GetCommandLineArgs()
        |> Array.skip 1
        |> Array.map (fun arg -> arg.Trim().Trim('"'))
        |> Array.tryFind (fun arg ->
            not (String.IsNullOrWhiteSpace arg)
            && not (arg.StartsWith("-", StringComparison.Ordinal))
            && (arg.EndsWith(".mpx", StringComparison.OrdinalIgnoreCase)
                || arg.EndsWith(".modpak", StringComparison.OrdinalIgnoreCase)
                || File.Exists arg))

    /// Per-user .mpx open → this exe "%1" (no admin).
    member private self.EnsureMpxFileAssociation() =
        if RuntimeInformation.IsOSPlatform OSPlatform.Windows then
            try
                let exe =
                    match Environment.ProcessPath with
                    | null | "" -> Process.GetCurrentProcess().MainModule.FileName
                    | path -> path
                if not (String.IsNullOrWhiteSpace exe) && File.Exists exe then
                    let command = sprintf "\"%s\" \"%%1\"" exe
                    let icon = sprintf "\"%s\",0" exe
                    use ext = Registry.CurrentUser.CreateSubKey(@"Software\Classes\.mpx")
                    if not (isNull ext) then
                        ext.SetValue("", "ModPak.Package")
                        ext.SetValue("Content Type", "application/x-modpak")
                    use progId = Registry.CurrentUser.CreateSubKey(@"Software\Classes\ModPak.Package")
                    if not (isNull progId) then
                        progId.SetValue("", "ModPak package")
                    use openCmd =
                        Registry.CurrentUser.CreateSubKey(@"Software\Classes\ModPak.Package\shell\open\command")
                    if not (isNull openCmd) then
                        openCmd.SetValue("", command)
                    use defaultIcon =
                        Registry.CurrentUser.CreateSubKey(@"Software\Classes\ModPak.Package\DefaultIcon")
                    if not (isNull defaultIcon) then
                        defaultIcon.SetValue("", icon)
            with _ ->
                ()

    member private self.HandleStartupPackage() =
        task {
            self.EnsureMpxFileAssociation()
            match self.StartupPackageArg() with
            | Some arg ->
                self.TryLoadPackageFromPath arg |> ignore
            | None ->
                let last = config.LastPackage
                if not (String.IsNullOrWhiteSpace last) && File.Exists last then
                    let name = Path.GetFileName last
                    let message = $"Open the last package?\n\n{name}\n{last}"
                    let! confirmed = self.ShowConfirmDialog("Open last package", message)
                    if confirmed then
                        self.TryLoadPackageFromPath last |> ignore
        }

    member private self.OpenPackage(_: obj, _: RoutedEventArgs) =
        task {
            let! start = self.TryStartFolder(config.OpenFileFolder)
            let options =
                FilePickerOpenOptions(
                    Title = "Open ModPak",
                    AllowMultiple = false,
                    FileTypeFilter = [| FilePickerFileType("ModPak", Patterns = ModPakFormat.OpenPatterns) |]
                )
            if not (isNull start) then
                options.SuggestedStartLocation <- start
            let! files = self.StorageProvider.OpenFilePickerAsync(options)
            match files |> Seq.tryHead with
            | None -> ()
            | Some file ->
                self.TryLoadPackageFromPath file.Path.LocalPath |> ignore
        }
        |> ignore

    member private self.AddFiles(_: obj, _: RoutedEventArgs) =
        task {
            let! start = self.TryStartFolder(config.AddFileFolder)
            let options =
                FilePickerOpenOptions(
                    Title = "Add files to package",
                    AllowMultiple = true
                )
            if not (isNull start) then
                options.SuggestedStartLocation <- start
            let! files = self.StorageProvider.OpenFilePickerAsync(options)
            let added =
                files
                |> Seq.map (fun file -> file.Path.LocalPath)
                |> Seq.filter (fun path -> not (String.IsNullOrWhiteSpace path) && File.Exists path)
                |> Seq.toList
            match added with
            | [] -> ()
            | paths -> self.StageFilesIntoPackage paths
        }
        |> ignore

    member private self.ApplyCodecs(entries: ModPakEntry list) =
        entries
        |> List.map (fun entry ->
            { entry with Codec = self.GetFileCodec entry.RelativePath })

    member private self.RebuildPackageForSave (output: string) =
        let manifestPayload = self.CurrentManifestPayload()
        let execs =
            match package with
            | Some pkg -> pkg.Exec
            | None -> []
        match self.MergePackageAndPendingEntries() with
        | Error error -> Error error
        | Ok merged when merged.IsEmpty && manifestPayload.IsNone && execs.IsEmpty ->
            Error (InvalidPath "Nothing to pak.")
        | Ok merged ->
            let entries = self.ApplyCodecs merged
            Pack.write output {
                Manifest = manifestPayload
                Exec = execs
                Entries = entries
            }

    member private self.WritePackageTo(output: string) =
        match self.ApplyCurrentScriptToPackage() with
        | Error msg -> Error(Io msg)
        | Ok () ->
            let codec = self.SelectedCodec()
            match self.RebuildPackageForSave output with
            | Error error -> Error error
            | Ok () ->
                match Unpack.read output with
                | Error error -> Error error
                | Ok next ->
                    package <- Some next
                    packagePath <- Some output
                    pendingPaths <- []
                    pendingRenames <- Map.empty
                    fileCodecs <-
                        next.Entries
                        |> List.fold (fun map entry -> map.Add(self.CodecKey entry.RelativePath, entry.Codec)) Map.empty
                    config.RememberLastPackage output
                    self.ClearDirty()
                    self.UpdateWindowTitle()
                    self.ShowPackageFiles next
                    self.SelectScriptKindFromPackage next
                    self.LoadScriptEditor()
                    let mani =
                        if next.Manifest.IsSome then " · MANI" else ""
                    Ok(next, codec, mani)

    member private self.SavePackageToPath(output: string) =
        match self.WritePackageTo output with
        | Error error -> self.Show(string error, errBrush)
        | Ok(next, codec, mani) ->
            self.Show(
                $"Saved with {self.CodecLabel codec}{mani} · {next.Entries.Length} files · {output}",
                okBrush)

    member private self.SavePackage(_: obj, _: RoutedEventArgs) =
        if packingInProgress then
            ()
        elif not isDirty then
            self.Show("No changes to save.", muteBrush)
        elif not (self.CanSavePackage()) then
            self.Show("Nothing to save.", muteBrush)
        elif self.IncludeManifest() && self.BoxText manifestNameBox = "" then
            self.Show("Name is required when Manifest is on.", errBrush)
        else
            match packagePath with
            | Some output ->
                packingInProgress <- true
                try
                    self.SavePackageToPath output
                finally
                    packingInProgress <- false
            | None ->
                // No path yet (New / in-memory) — pick a file like Pak.
                packingInProgress <- true
                let work = task {
                    try
                        try
                            let slug =
                                match self.BoxText manifestSlugBox with
                                | "" ->
                                    match self.BoxText manifestNameBox with
                                    | "" -> "package"
                                    | name -> self.SlugFromName name
                                | value -> value.ToLowerInvariant()
                            let! startSave = self.TryStartFolder(config.SaveFileFolder)
                            let saveOptions =
                                FilePickerSaveOptions(
                                    Title = "Save ModPak",
                                    SuggestedFileName = slug + ModPakFormat.Extension,
                                    DefaultExtension = "mpx",
                                    FileTypeChoices =
                                        [|
                                            FilePickerFileType("ModPak", Patterns = [| "*.mpx" |])
                                            FilePickerFileType("ModPak (all)", Patterns = ModPakFormat.OpenPatterns)
                                        |]
                                )
                            let! target =
                                self.Ui (fun () ->
                                    if not (isNull startSave) then
                                        saveOptions.SuggestedStartLocation <- startSave
                                    self.StorageProvider.SaveFilePickerAsync(saveOptions))
                            do!
                                self.Ui (fun () ->
                                    task {
                                        match target with
                                        | null -> ()
                                        | file ->
                                            let output = file.Path.LocalPath
                                            config.RememberSaveFile output
                                            self.SavePackageToPath output
                                        return ()
                                    })
                        with error ->
                            self.Show(error.Message, errBrush)
                    finally
                        packingInProgress <- false
                }
                work |> ignore

    member private self.PackPackage(_: obj, _: RoutedEventArgs) =
        if packingInProgress then
            ()
        elif pendingPaths.IsEmpty && package.IsNone && not (self.IncludeManifest()) then
            self.Show("Add files first.", muteBrush)
        elif self.IncludeManifest() && self.BoxText manifestNameBox = "" then
            self.Show("Name is required when Manifest is on.", errBrush)
        else
            packingInProgress <- true
            let work = task {
                try
                    try
                        let slug =
                            match self.BoxText manifestSlugBox with
                            | "" ->
                                match self.BoxText manifestNameBox with
                                | "" -> "package"
                                | name -> self.SlugFromName name
                            | value -> value.ToLowerInvariant()
                        let suggested =
                            match packagePath with
                            | Some path -> Path.GetFileName path
                            | None -> slug + ModPakFormat.Extension
                        let! startSave = self.TryStartFolder(config.SaveFileFolder)
                        let saveOptions =
                            FilePickerSaveOptions(
                                Title = "Pak ModPak",
                                SuggestedFileName = suggested,
                                DefaultExtension = "mpx",
                                FileTypeChoices =
                                    [|
                                        FilePickerFileType("ModPak", Patterns = [| "*.mpx" |])
                                        FilePickerFileType("ModPak (all)", Patterns = ModPakFormat.OpenPatterns)
                                    |]
                            )
                        let! target =
                            self.Ui (fun () ->
                                if not (isNull startSave) then
                                    saveOptions.SuggestedStartLocation <- startSave
                                self.StorageProvider.SaveFilePickerAsync(saveOptions))
                        do!
                            self.Ui (fun () ->
                                task {
                                    match target with
                                    | null -> ()
                                    | file ->
                                        let output = file.Path.LocalPath
                                        config.RememberSaveFile output
                                        match self.WritePackageTo output with
                                        | Error error -> self.Show(string error, errBrush)
                                        | Ok(next, codec, mani) ->
                                            self.Show(
                                                $"Paked with {self.CodecLabel codec}{mani} · {next.Entries.Length} files · {output}",
                                                okBrush)
                                    return ()
                                })
                    with error ->
                        self.Show(error.Message, errBrush)
                finally
                    packingInProgress <- false
            }
            work |> ignore

    member private self.LocalFolderPath(folder: IStorageFolder) =
        let fromApi = folder.TryGetLocalPath()
        if not (String.IsNullOrWhiteSpace fromApi) then
            fromApi
        else
            let uri = folder.Path
            if isNull uri then null
            else uri.LocalPath

    member private self.CopyStagingSelected(selected: string list, target: string) =
        try
            Directory.CreateDirectory target |> ignore
            let mutable copied = 0
            for pathKey in selected do
                match Pack.tryFullPath pendingPaths pathKey with
                | None -> ()
                | Some source ->
                    let dest =
                        Path.Combine(target, pathKey.Replace('/', Path.DirectorySeparatorChar))
                    let dir = Path.GetDirectoryName dest
                    if not (String.IsNullOrWhiteSpace dir) then
                        Directory.CreateDirectory dir |> ignore
                    File.Copy(source, dest, true)
                    copied <- copied + 1
            if copied = 0 then
                Error (Io "No staged files matched the selection.")
            else
                Ok copied
        with error ->
            Error (Io error.Message)

    member private self.UnpackPathsToFolder(paths: string list, title: string) =
        if paths.IsEmpty then
            self.Show("Nothing to unpak.", muteBrush)
        elif package.IsNone && pendingPaths.IsEmpty then
            self.Show("Open a package or add files first.", muteBrush)
        else
            task {
                let! startFolder = self.TryStartFolder(config.OpenFolder)
                let folderOptions = FolderPickerOpenOptions(Title = title, AllowMultiple = false)
                if not (isNull startFolder) then
                    folderOptions.SuggestedStartLocation <- startFolder
                let! folders = self.StorageProvider.OpenFolderPickerAsync(folderOptions)
                match folders |> Seq.tryHead with
                | None -> ()
                | Some folder ->
                    match self.LocalFolderPath folder with
                    | null | "" -> self.Show("Could not resolve the selected folder path.", errBrush)
                    | parent ->
                        let target = Path.GetFullPath parent
                        config.RememberOpenFolder target
                        let result =
                            match package with
                            | Some current when pendingPaths.IsEmpty ->
                                Unpack.selectedToDirectory current paths target
                                |> Result.map (fun () -> paths.Length)
                            | _ -> self.CopyStagingSelected(paths, target)
                        match result with
                        | Error error -> self.Show(string error, errBrush)
                        | Ok count ->
                            let label =
                                if count = 1 then
                                    Path.Combine(target, paths.Head.Replace('/', Path.DirectorySeparatorChar))
                                else
                                    $"{count} files → {target}"
                            self.Show($"Unpacked {label}", okBrush)
            }
            |> ignore

    member private self.UnpackPackage(_: obj, _: RoutedEventArgs) =
        let selected = self.SelectedPathKeys()
        if selected.IsEmpty then
            self.Show("Select files to unpak.", muteBrush)
        else
            self.UnpackPathsToFolder(selected, "Unpack into folder")

    member private self.UnpackAllPackage(_: obj, _: RoutedEventArgs) =
        self.UnpackPathsToFolder(self.AllUnpackPathKeys(), "Unpack all into folder")

    member private self.ValidatePackage(_: obj, _: RoutedEventArgs) =
        match package, packagePath with
        | None, _ -> self.Show("Open a package first.", muteBrush)
        | Some current, None ->
            match Validate.package current with
            | Error error -> self.Show(string error, errBrush)
            | Ok () ->
                self.Show("Package is valid.", okBrush)
                self.ShowAlert("Validate", "Package is valid.", Symbol.ShieldCheckmark)
        | _, Some path ->
            match Validate.archive path with
            | Error error -> self.Show(string error, errBrush)
            | Ok () ->
                self.Show("Archive is valid.", okBrush)
                self.ShowAlert("Validate", "Archive is valid.", Symbol.ShieldCheckmark)

    member private self.ChunkCounts(pkg: ModPakPackage) =
        [
            "FILE", pkg.Entries.Length
            "MANI", (if pkg.Manifest.IsSome then 1 else 0)
            "EXEC", pkg.Exec.Length
        ]
        |> List.filter (fun (_, n) -> n > 0)
        |> List.sortByDescending (fun (_, n) -> n)

    member private self.ContainerVersion() =
        match packagePath with
        | Some path ->
            match Mp64.readFile path with
            | Ok archive -> archive.Major, archive.Minor
            | Error _ -> Mp64.Major, Mp64.Minor
        | None -> Mp64.Major, Mp64.Minor

    member private self.ShowAbout(_: obj, _: RoutedEventArgs) =
        let line (text: string) (size: float) (color: string) (weight: FontWeight) =
            TextBlock(
                Text = text,
                FontSize = size,
                FontWeight = weight,
                Foreground = SolidColorBrush(Color.Parse color),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center)

        let iconUri = Uri("avares://ModPak.App/Assets/icon.ico")
        let logo =
            Image(
                Source = new Bitmap(AssetLoader.Open(Uri("avares://ModPak.App/Assets/KI.png"))),
                Width = 44.0,
                Height = 44.0,
                Margin = Thickness(0.0, 0.0, 0.0, 4.0),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center)

        let licenseUrl = Uri("https://opensource.org/licenses/BSD-3-Clause")
        let licenseButton =
            HyperlinkButton(
                Content = "BSD 3-Clause License",
                NavigateUri = licenseUrl,
                FontSize = 12.0,
                Padding = Thickness(0.0),
                MinHeight = 0.0,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                HorizontalContentAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Foreground = SolidColorBrush(Color.Parse "#7FA8D4"))
        ToolTip.SetTip(licenseButton, licenseUrl.AbsoluteUri)

        let licenseRow =
            StackPanel(
                Orientation = Avalonia.Layout.Orientation.Horizontal,
                Spacing = 4.0,
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
                Margin = Thickness(0.0, 2.0, 0.0, 0.0))
        licenseRow.Children.Add(
            TextBlock(
                Text = "Licensed under the",
                FontSize = 12.0,
                Foreground = SolidColorBrush(Color.Parse "#A89888"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center))
        licenseRow.Children.Add(licenseButton)

        let panel = StackPanel(Spacing = 4.0)
        panel.Children.Add(logo)
        panel.Children.Add(line $"ModPak {AppVersion}" 20.0 "#F0E6DC" FontWeight.SemiBold)
        panel.Children.Add(line "Frank Hliva" 13.0 "#C8B8A8" FontWeight.Normal)
        panel.Children.Add(line "Copyright © 2026" 12.0 "#A89888" FontWeight.Normal)
        panel.Children.Add(line "Kingmaker SAS" 12.0 "#A89888" FontWeight.Normal)
        panel.Children.Add(licenseRow)

        let about =
            Window(
                Title = $"About ModPak {AppVersion}",
                Width = 340.0,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = SolidColorBrush(Color.Parse "#1A1613"),
                Icon = WindowIcon(AssetLoader.Open iconUri),
                Content = Border(Padding = Thickness(22.0, 28.0), Child = panel))
        about.ShowDialog(self) |> ignore

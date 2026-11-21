namespace ModPak.App

open System
open System.Collections.Concurrent
open System.Collections.ObjectModel
open System.ComponentModel
open System.Diagnostics
open System.IO
open System.Runtime.InteropServices
open System.Text
open System.Threading
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
    /// In-app Package → Explorer relative paths (newline-separated).
    let PackagePathsFormat = DataFormat.CreateStringApplicationFormat "modpak-pkg-paths"

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
    let mutable explorerMultiSelectionCheck: CheckBox = null
    let mutable packageMultiSelectionCheck: CheckBox = null
    let mutable suppressExplorerMultiSelectionPersist = false
    let mutable unpackButton: Button = null
    let mutable unpackAllButton: Button = null
    let mutable saveButton: Button = null
    let mutable infoButton: Button = null
    let mutable openMenuItem: MenuItem = null
    let mutable detailsMenuItem: MenuItem = null
    let mutable renameMenuItem: MenuItem = null
    let mutable deleteMenuItem: MenuItem = null
    let mutable unpakMenuItem: MenuItem = null
    let mutable diskOpenMenuItem: MenuItem = null
    let mutable diskAddMenuItem: MenuItem = null
    let mutable diskInfoMenuItem: MenuItem = null
    let mutable diskRenameMenuItem: MenuItem = null
    let mutable diskDeleteMenuItem: MenuItem = null
    let mutable codecBox: ComboBox = null
    let mutable explorerSelSlot: TextBlock = null
    let mutable explorerCountSlot: TextBlock = null
    let mutable explorerStatusSlot: TextBlock = null
    let mutable packageSelSlot: TextBlock = null
    let mutable packageCountSlot: TextBlock = null
    let mutable packageStatusSlot: TextBlock = null
    /// True = Explorer (disk) pane is active; false = Package pane.
    let mutable explorerPaneActive = true
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
    /// Set after user confirms close (save / discard) so Closing can proceed.
    let mutable allowClose = false
    let mutable closePromptInFlight = false
    /// Explorer → Package drag: press args kept until move threshold or release.
    let mutable diskDragPress: PointerPressedEventArgs option = None
    let mutable diskDragOrigin: Point option = None
    let mutable diskDragStarted = false
    let mutable pkgDragPress: PointerPressedEventArgs option = None
    let mutable pkgDragOrigin: Point option = None
    let mutable pkgDragStarted = false
    /// Selection captured on press so Toggle does not drop the row being dragged.
    let mutable pkgDragKeys: string list = []
    let mutable pkgPressKept = false
    let mutable pkgPressRow: obj = null
    let mutable diskDragKeys: string list = []
    let mutable diskPressKept = false
    let mutable diskPressRow: obj = null
    /// Collapses the press-handler and Click that both fire on the first Fn-button press.
    let mutable lastFnTick = 0L

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
        match self.FindControl<Grid>("FnBar") with
        | null -> ()
        | bar ->
            bar.AddHandler(
                InputElement.PointerPressedEvent,
                (fun sender e -> self.FnBarPointerPressed(sender, e)),
                RoutingStrategies.Tunnel)
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
        explorerMultiSelectionCheck <- self.FindControl<CheckBox>("ExplorerMultiSelectionCheck")
        packageMultiSelectionCheck <- self.FindControl<CheckBox>("PackageMultiSelectionCheck")
        unpackButton <- self.FindControl<Button>("UnpackButton")
        unpackAllButton <- self.FindControl<Button>("UnpackAllButton")
        saveButton <- self.FindControl<Button>("SaveButton")
        infoButton <- self.FindControl<Button>("InfoButton")
        openMenuItem <- self.FindControl<MenuItem>("OpenMenuItem")
        unpakMenuItem <- self.FindControl<MenuItem>("UnpakMenuItem")
        diskOpenMenuItem <- self.FindControl<MenuItem>("DiskOpenMenuItem")
        diskAddMenuItem <- self.FindControl<MenuItem>("DiskAddMenuItem")
        diskInfoMenuItem <- self.FindControl<MenuItem>("DiskInfoMenuItem")
        diskRenameMenuItem <- self.FindControl<MenuItem>("DiskRenameMenuItem")
        diskDeleteMenuItem <- self.FindControl<MenuItem>("DiskDeleteMenuItem")
        codecBox <- self.FindControl<ComboBox>("CodecBox")
        explorerSelSlot <- self.FindControl<TextBlock>("ExplorerSelSlot")
        explorerCountSlot <- self.FindControl<TextBlock>("ExplorerCountSlot")
        explorerStatusSlot <- self.FindControl<TextBlock>("ExplorerStatusSlot")
        packageSelSlot <- self.FindControl<TextBlock>("PackageSelSlot")
        packageCountSlot <- self.FindControl<TextBlock>("PackageCountSlot")
        packageStatusSlot <- self.FindControl<TextBlock>("PackageStatusSlot")
        self.InitializeScriptEditor()
        if not (isNull codecBox) && codecBox.SelectedIndex < 0 then
            codecBox.SelectedIndex <- 1 // LZ4
        self.InitDiskExplorer()
        self.WireExplorerPackageDragDrop()
        self.RestoreExplorerMultiSelection()
        self.UpdateFileActionsEnabled()
        self.UpdateDiskActionsEnabled()
        self.UpdateSaveEnabled()
        self.RefreshPaneStatusSlots()
        self.Show("Add files, then Pak — or open an existing .mpx.", muteBrush)

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
        self.Closing.Add(fun e -> self.OnMainWindowClosing e)
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

    member private self.SetSlot(slot: TextBlock, text: string, ?brush: IBrush) =
        if not (isNull slot) then
            slot.Text <- text
            match brush with
            | Some b -> slot.Foreground <- b
            | None -> ()

    member private self.RefreshPaneStatusSlots() =
        let diskSel =
            if isNull diskList then 0 else diskList.SelectedItems.Count
        let itemCount (box: ListBox) =
            if isNull box then 0 else box.ItemCount
        let diskCount = itemCount diskList
        let pkgSel =
            if isNull fileList then 0 else fileList.SelectedItems.Count
        let pkgCount = itemCount fileList
        self.SetSlot(explorerSelSlot, $"{diskSel} selected")
        self.SetSlot(explorerCountSlot, $"{diskCount} items")
        self.SetSlot(packageSelSlot, $"{pkgSel} selected")
        self.SetSlot(packageCountSlot, $"{pkgCount} items")

    member private self.Show(text: string, brush: IBrush) =
        let apply () =
            if explorerPaneActive then
                self.SetSlot(explorerStatusSlot, text, brush)
            else
                self.SetSlot(packageStatusSlot, text, brush)
            self.RefreshPaneStatusSlots()
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

    member private self.DialogButtonContent(label: string, symbol: Symbol) =
        let row =
            StackPanel(
                Orientation = Orientation.Horizontal,
                Spacing = 8.0,
                HorizontalAlignment = HorizontalAlignment.Center)
        row.Children.Add(
            SymbolIcon(
                Symbol = symbol,
                IconVariant = IconVariant.Regular,
                FontSize = 16.0,
                Foreground = SolidColorBrush(Color.Parse "#F3EDE6"),
                VerticalAlignment = VerticalAlignment.Center))
        row.Children.Add(
            TextBlock(
                Text = label,
                VerticalAlignment = VerticalAlignment.Center,
                FontWeight = FontWeight.SemiBold))
        row :> obj

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

    member private self.DialogAccentIconButton(label: string, symbol: Symbol) =
        let button = self.DialogAccentButton(label)
        button.Content <- self.DialogButtonContent(label, symbol)
        button.MinWidth <- 108.0
        button

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

    member private self.DialogCommandIconButton(label: string, symbol: Symbol) =
        let button = self.DialogCommandButton(label)
        button.Content <- self.DialogButtonContent(label, symbol)
        button.MinWidth <- 108.0
        button

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

    member private self.DialogButtonRow3(ok: Button, cancel: Button, apply: Button) =
        let buttons =
            StackPanel(
                Orientation = Orientation.Horizontal,
                Spacing = 8.0,
                HorizontalAlignment = HorizontalAlignment.Right)
        buttons.Children.Add(ok)
        buttons.Children.Add(cancel)
        buttons.Children.Add(apply)
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

    member private self.ShowConfirmDialog(title: string, message: string, ?owner: Window) : Task<bool> =
        task {
            let yes = self.DialogAccentButton("Yes")
            let no = self.DialogCommandButton("No")
            let root = StackPanel(Margin = Thickness(22.0))
            root.Children.Add(self.DialogMessageRow message)
            root.Children.Add(self.DialogButtonRow(yes, no))
            let dialog = self.WrapStudioDialog(title, root)
            yes.Click.Add(fun _ -> dialog.Close(true))
            no.Click.Add(fun _ -> dialog.Close(false))
            let host = defaultArg owner (self :> Window)
            return! dialog.ShowDialog<bool>(host)
        }

    /// 0 = Save, 1 = Don't Save, 2 = Cancel
    member private self.ShowSaveChangesDialog(documentName: string) : Task<int> =
        task {
            let message =
                TextBlock(
                    Text = $"Do you want to save changes to {documentName}?",
                    Foreground = SolidColorBrush(Color.Parse "#F3EDE6"),
                    FontSize = 15.0,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = Thickness(0.0, 0.0, 0.0, 18.0))
            let saveBtn = self.DialogAccentButton("Save")
            let discardBtn = self.DialogCommandButton("Don't Save")
            discardBtn.MinWidth <- 110.0
            let cancelBtn = self.DialogCommandButton("Cancel")
            let buttons =
                StackPanel(
                    Orientation = Orientation.Horizontal,
                    Spacing = 8.0,
                    HorizontalAlignment = HorizontalAlignment.Right)
            buttons.Children.Add(saveBtn)
            buttons.Children.Add(discardBtn)
            buttons.Children.Add(cancelBtn)
            let root = StackPanel(Margin = Thickness(22.0))
            root.Children.Add(message)
            root.Children.Add(buttons)
            let dialog = self.WrapStudioDialog("ModPak", root)
            dialog.Width <- 420.0
            saveBtn.Click.Add(fun _ -> dialog.Close(0))
            discardBtn.Click.Add(fun _ -> dialog.Close(1))
            cancelBtn.Click.Add(fun _ -> dialog.Close(2))
            return! dialog.ShowDialog<int>(self)
        }

    member private self.TrySaveForClose() : Task<bool> =
        task {
            if packingInProgress then
                return false
            elif not (self.CanSavePackage()) then
                return true
            elif self.IncludeManifest() && self.BoxText manifestNameBox = "" then
                self.Show("Name is required when Manifest is on.", errBrush)
                return false
            else
                match packagePath with
                | Some output ->
                    packingInProgress <- true
                    try
                        match self.WritePackageTo output with
                        | Error error ->
                            self.Show(string error, errBrush)
                            return false
                        | Ok(next, codec, mani) ->
                            self.Show(
                                $"Saved with {self.CodecLabel codec}{mani} · {next.Entries.Length} files · {output}",
                                okBrush)
                            return true
                    finally
                        packingInProgress <- false
                | None ->
                    packingInProgress <- true
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
                            if not (isNull startSave) then
                                saveOptions.SuggestedStartLocation <- startSave
                            let! target = self.StorageProvider.SaveFilePickerAsync(saveOptions)
                            match target with
                            | null -> return false
                            | file ->
                                let output = file.Path.LocalPath
                                if String.IsNullOrWhiteSpace output then
                                    return false
                                else
                                    config.RememberSaveFile output
                                    match self.WritePackageTo output with
                                    | Error error ->
                                        self.Show(string error, errBrush)
                                        return false
                                    | Ok(next, codec, mani) ->
                                        self.Show(
                                            $"Saved with {self.CodecLabel codec}{mani} · {next.Entries.Length} files · {output}",
                                            okBrush)
                                        return true
                        with error ->
                            self.Show(error.Message, errBrush)
                            return false
                    finally
                        packingInProgress <- false
        }

    member private self.FinishCloseAfterPrompt() =
        allowClose <- true
        closePromptInFlight <- false
        self.Close()

    member private self.OnMainWindowClosing(e: WindowClosingEventArgs) =
        self.PersistMetadataUi()
        self.RememberState()
        if allowClose || not (self.CanSavePackage()) then
            ()
        elif closePromptInFlight then
            e.Cancel <- true
        else
            // Never block the UI thread with ShowDialog().GetResult() here — it deadlocks Closing.
            e.Cancel <- true
            closePromptInFlight <- true
            let documentName =
                match packagePath with
                | Some path when not (String.IsNullOrWhiteSpace path) -> Path.GetFileName path
                | _ -> "Untitled"
            async {
                let! choice = self.ShowSaveChangesDialog documentName |> Async.AwaitTask
                match choice with
                | 0 ->
                    let! saved = self.TrySaveForClose() |> Async.AwaitTask
                    if saved then
                        self.FinishCloseAfterPrompt()
                    else
                        closePromptInFlight <- false
                | 1 -> self.FinishCloseAfterPrompt()
                | _ -> closePromptInFlight <- false
            }
            |> Async.StartImmediate

    member private self.ShowPathDialog
        (
            title: string,
            promptText: string,
            initialPath: string,
            watermark: string,
            confirmLabel: string
        ) : Task<string option> =
        task {
            let prompt =
                TextBlock(
                    Text = promptText,
                    Foreground = SolidColorBrush(Color.Parse "#F3EDE6"),
                    FontSize = 15.0,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = Thickness(0.0, 0.0, 0.0, 14.0))
            let box =
                TextBox(
                    Text = initialPath,
                    Watermark = watermark,
                    MinHeight = 36.0,
                    Margin = Thickness(0.0, 0.0, 0.0, 18.0))
            let confirm = self.DialogAccentButton(confirmLabel)
            let cancel = self.DialogCommandButton("Cancel")
            let root = StackPanel(Margin = Thickness(22.0))
            root.Children.Add(prompt)
            root.Children.Add(box)
            root.Children.Add(self.DialogButtonRow(confirm, cancel))
            let dialog = self.WrapStudioDialog(title, root)
            let submit () =
                let value = if isNull box.Text then "" else box.Text.Trim()
                dialog.Close(value)
            confirm.Click.Add(fun _ -> submit ())
            cancel.Click.Add(fun _ -> dialog.Close(null))
            box.KeyDown.Add(fun e ->
                if e.Key = Key.Enter || e.Key = Key.Return then
                    submit ()
                    e.Handled <- true)
            let! result = dialog.ShowDialog<string>(self)
            if String.IsNullOrWhiteSpace result then
                return None
            else
                return Some result
        }

    member private self.ShowRenameDialog(currentPath: string) : Task<string option> =
        self.ShowPathDialog(
            "Rename",
            "Enter a new path for this item.",
            currentPath,
            "relative/path.ext",
            "Rename")

    member private self.ShowMkdirDialog() : Task<string option> =
        self.ShowPathDialog(
            "Create directory",
            "Enter the name of the new directory.",
            "",
            "Folder name",
            "Create")

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
                let fileName = Path.GetFileName path
                // Placeholder keeps empty dirs visible in the package tree.
                if String.Equals(fileName, ".keep", StringComparison.OrdinalIgnoreCase) then
                    None
                else
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
            self.RefreshPaneStatusSlots()

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
                    let icon =
                        match icons.GetDriveIcon root with
                        | null -> icons.GetFolderIcon()
                        | driveIcon -> driveIcon
                    Some(
                        FileListItem(
                            icon,
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
        self.UpdateDiskActionsEnabled()
        self.RefreshPaneStatusSlots()

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

    member private self.ExplorerMultiSelectionEnabled() =
        match explorerMultiSelectionCheck with
        | null -> config.ExplorerMultiSelection
        | box -> box.IsChecked.GetValueOrDefault(false)

    member private self.PackageMultiSelectionEnabled() =
        match packageMultiSelectionCheck with
        | null -> config.PackageMultiSelection
        | box -> box.IsChecked.GetValueOrDefault(false)

    member private self.SelectionModeFor(tcToggle: bool) =
        if tcToggle then
            SelectionMode.Multiple ||| SelectionMode.Toggle
        else
            SelectionMode.Multiple

    member private self.ApplyExplorerSelectionMode() =
        if not (isNull diskList) then
            diskList.SelectionMode <- self.SelectionModeFor(self.ExplorerMultiSelectionEnabled())

    member private self.ApplyPackageSelectionMode() =
        if not (isNull fileList) then
            fileList.SelectionMode <- self.SelectionModeFor(self.PackageMultiSelectionEnabled())

    member private self.RestoreExplorerMultiSelection() =
        suppressExplorerMultiSelectionPersist <- true
        try
            if not (isNull explorerMultiSelectionCheck) then
                explorerMultiSelectionCheck.IsChecked <- Nullable(config.ExplorerMultiSelection)
            if not (isNull packageMultiSelectionCheck) then
                packageMultiSelectionCheck.IsChecked <- Nullable(config.PackageMultiSelection)
        finally
            suppressExplorerMultiSelectionPersist <- false
        self.ApplyExplorerSelectionMode()
        self.ApplyPackageSelectionMode()

    member private self.ExplorerMultiSelectionChanged(_: obj, _: RoutedEventArgs) =
        if not suppressExplorerMultiSelectionPersist then
            let enabled = self.ExplorerMultiSelectionEnabled()
            config.ExplorerMultiSelection <- enabled
            config.Save()
            self.ApplyExplorerSelectionMode()

    member private self.PackageMultiSelectionChanged(_: obj, _: RoutedEventArgs) =
        if not suppressExplorerMultiSelectionPersist then
            let enabled = self.PackageMultiSelectionEnabled()
            config.PackageMultiSelection <- enabled
            config.Save()
            self.ApplyPackageSelectionMode()

    /// Prepare selection for drag / context menu without fighting ListBox multi-select rules.
    member private self.SelectDiskListItemUnder(source: Avalonia.Visual, ?modifiers: KeyModifiers) =
        if isNull diskList || isNull source then
            ()
        else
            let mods = defaultArg modifiers KeyModifiers.None
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
                    let tcMode = self.ExplorerMultiSelectionEnabled()
                    let multiGesture =
                        mods.HasFlag KeyModifiers.Control
                        || mods.HasFlag KeyModifiers.Shift
                    // TC Toggle / Ctrl|Shift: leave selection to ListBox (avoid double-toggle).
                    // Plain click: select the drag source now.
                    if not tcMode && not multiGesture then
                        diskList.SelectedItems.Clear()
                        diskList.SelectedItems.Add(row) |> ignore

    member private self.ClearDiskDragState() =
        diskDragPress <- None
        diskDragOrigin <- None
        diskDragStarted <- false
        diskDragKeys <- []
        diskPressKept <- false
        diskPressRow <- null

    member private self.RowUnderPointer(source: obj) =
        match source with
        | :? Avalonia.Visual as visual ->
            let mutable walk = visual
            let mutable found: obj = null
            while not (isNull walk) && isNull found do
                match walk with
                | :? ListBoxItem as item when not (isNull item.DataContext) ->
                    found <- item.DataContext
                | _ -> walk <- walk.GetVisualParent()
            if isNull found then None else Some found
        | _ -> None

    member private self.RowIsSelected(list: ListBox, row: obj) =
        not (isNull list)
        && list.SelectedItems
           |> Seq.cast<obj>
           |> Seq.exists (fun item -> obj.ReferenceEquals(item, row))

    /// Click without a drag: apply the selection change that was held back.
    member private self.ReleaseKeptRow(list: ListBox, row: obj, toggle: bool) =
        if isNull list || isNull row then
            ()
        elif toggle then
            if list.SelectedItems.Contains row then
                list.SelectedItems.Remove row |> ignore
        else
            list.SelectedItems.Clear()
            list.SelectedItems.Add row |> ignore

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
            let onPkgPress (sender: obj) (e: PointerPressedEventArgs) =
                self.FileListPointerPressed(sender, e)
            let onPkgMove (sender: obj) (e: PointerEventArgs) =
                self.FileListPointerMoved(sender, e)
            let onPkgRelease (sender: obj) (e: PointerReleasedEventArgs) =
                self.FileListPointerReleased(sender, e)
            fileList.AddHandler(InputElement.PointerPressedEvent, onPkgPress, RoutingStrategies.Tunnel)
            fileList.AddHandler(InputElement.PointerMovedEvent, onPkgMove, RoutingStrategies.Tunnel)
            fileList.AddHandler(InputElement.PointerReleasedEvent, onPkgRelease, RoutingStrategies.Tunnel)
            DragDrop.SetAllowDrop(fileList, true)
            DragDrop.AddDragOverHandler(fileList, fun sender e -> self.PackageListDragOver(sender, e))
            DragDrop.AddDropHandler(fileList, fun sender e -> self.PackageListDrop(sender, e))
        if not (isNull diskList) then
            DragDrop.SetAllowDrop(diskList, true)
            DragDrop.AddDragOverHandler(diskList, fun sender e -> self.DiskListDragOver(sender, e))
            DragDrop.AddDropHandler(diskList, fun sender e -> self.DiskListDrop(sender, e))

    member private self.DiskListPointerPressed(_: obj, e: PointerPressedEventArgs) =
        explorerPaneActive <- true
        diskPressKept <- false
        diskPressRow <- null
        diskDragKeys <- []
        if e.GetCurrentPoint(diskList).Properties.IsLeftButtonPressed then
            let mods = e.KeyModifiers
            let plain = not (mods.HasFlag KeyModifiers.Control || mods.HasFlag KeyModifiers.Shift)
            match self.RowUnderPointer e.Source with
            | Some row when plain && self.RowIsSelected(diskList, row) ->
                diskPressKept <- true
                diskPressRow <- row
                diskDragKeys <- self.SelectedDiskRoots()
                e.Handled <- true
            | _ ->
                match e.Source with
                | :? Avalonia.Visual as visual ->
                    self.SelectDiskListItemUnder(visual, e.KeyModifiers)
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
                let roots =
                    if diskDragKeys.IsEmpty then self.SelectedDiskRoots() else diskDragKeys
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
            if diskPressKept then
                self.ReleaseKeptRow(diskList, diskPressRow, self.ExplorerMultiSelectionEnabled())
            self.ClearDiskDragState()

    member private self.ClearPkgDragState() =
        pkgDragPress <- None
        pkgDragOrigin <- None
        pkgDragStarted <- false
        pkgDragKeys <- []
        pkgPressKept <- false
        pkgPressRow <- null

    member private self.PackageSelectionKeys() =
        if isNull fileList then
            []
        else
            [
                for item in fileList.SelectedItems do
                    match item with
                    | :? FileListItem as row when not row.IsParent -> yield row.PathKey
                    | _ -> ()
            ]

    member private self.FileListPointerPressed(_: obj, e: PointerPressedEventArgs) =
        explorerPaneActive <- false
        pkgPressKept <- false
        pkgPressRow <- null
        pkgDragKeys <- []
        if e.GetCurrentPoint(fileList).Properties.IsLeftButtonPressed then
            let mods = e.KeyModifiers
            let plain = not (mods.HasFlag KeyModifiers.Control || mods.HasFlag KeyModifiers.Shift)
            match self.RowUnderPointer e.Source with
            | Some row when plain && self.RowIsSelected(fileList, row) ->
                pkgPressKept <- true
                pkgPressRow <- row
                pkgDragKeys <- self.PackageSelectionKeys()
                e.Handled <- true
            | _ -> ()
            pkgDragPress <- Some e
            pkgDragOrigin <- Some(e.GetPosition fileList)
            pkgDragStarted <- false

    member private self.FileListPointerMoved(_: obj, e: PointerEventArgs) =
        match pkgDragPress, pkgDragOrigin with
        | Some press, Some origin when
            not pkgDragStarted
            && e.GetCurrentPoint(fileList).Properties.IsLeftButtonPressed ->
            let pos = e.GetPosition fileList
            if abs (pos.X - origin.X) >= 6.0 || abs (pos.Y - origin.Y) >= 6.0 then
                let keys =
                    if pkgDragKeys.IsEmpty then self.PackageSelectionKeys() else pkgDragKeys
                if keys.IsEmpty then
                    self.ClearPkgDragState()
                else
                    pkgDragStarted <- true
                    pkgDragPress <- None
                    pkgDragOrigin <- None
                    let payload = String.Join("\n", keys)
                    let data = new DataTransfer()
                    data.Add(DataTransferItem.Create(ModPakDnD.PackagePathsFormat, payload))
                    async {
                        try
                            let! _ =
                                DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy)
                                |> Async.AwaitTask
                            ()
                        finally
                            Dispatcher.UIThread.Post(fun () -> self.ClearPkgDragState())
                    }
                    |> Async.StartImmediate
        | _ -> ()

    member private self.FileListPointerReleased(_: obj, _: PointerReleasedEventArgs) =
        if not pkgDragStarted then
            if pkgPressKept then
                self.ReleaseKeptRow(fileList, pkgPressRow, self.PackageMultiSelectionEnabled())
            self.ClearPkgDragState()

    member private self.ResolveDiskDropFolder(e: DragEventArgs) =
        let fromRow =
            match e.Source with
            | :? Avalonia.Visual as visual ->
                match self.FileListItemFromVisual visual with
                | Some row when row.IsDirectory && Directory.Exists row.PathKey -> Some row.PathKey
                | _ -> None
            | _ -> None
        match fromRow with
        | Some path -> Some path
        | None ->
            match diskDir with
            | Some dir when Directory.Exists dir -> Some dir
            | _ -> None

    member private self.DiskListDragOver(_: obj, e: DragEventArgs) =
        let ok = e.DataTransfer.Contains ModPakDnD.PackagePathsFormat
        if ok then
            e.DragEffects <- e.DragEffects &&& DragDropEffects.Copy
            e.Handled <- true
        else
            e.DragEffects <- DragDropEffects.None

    member private self.DiskListDrop(_: obj, e: DragEventArgs) =
        let raw =
            match e.DataTransfer.TryGetValue ModPakDnD.PackagePathsFormat with
            | null | "" -> ""
            | text -> text
        let keys =
            raw.Split([| '\n' |], StringSplitOptions.RemoveEmptyEntries)
            |> Array.toList
            |> List.map (fun path -> path.Trim())
            |> List.filter (fun path -> path <> "")
        match self.ResolveDiskDropFolder e with
        | None -> self.Show("Open a folder in Explorer first.", muteBrush)
        | Some target -> self.DropPackagePathsOnDisk(keys, target)
        e.Handled <- true

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

    /// Place copied paths under the currently open package folder (explorerDir).
    member private self.PackageTargetRelative(rel: string) =
        let cwd = Pathing.normalize explorerDir
        let r = Pathing.normalize rel
        if cwd = "" then r
        elif r = "" then cwd
        else cwd + "/" + r

    /// Expand selected files/folders into (fullPath, packageRelativePath, emptyDirKeep).
    member private self.CollectDiskTreeFiles(roots: string list) : (string * string * bool) list =
        let acc = ResizeArray<string * string * bool>()
        for root in roots do
            try
                if File.Exists root then
                    let full = Path.GetFullPath root
                    let rel = self.PackageTargetRelative(Path.GetFileName full)
                    if Pathing.isSafe rel then
                        acc.Add(full, rel, false)
                elif Directory.Exists root then
                    let full = Path.GetFullPath root
                    let rootName =
                        let name = Path.GetFileName(full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
                        if String.IsNullOrWhiteSpace name then "folder" else name
                    for file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories) do
                        let rel =
                            Path.Combine(rootName, Path.GetRelativePath(full, file))
                            |> Pathing.normalize
                            |> self.PackageTargetRelative
                        if Pathing.isSafe rel then
                            acc.Add(Path.GetFullPath file, rel, false)
                    let dirs =
                        seq {
                            yield full
                            yield! Directory.EnumerateDirectories(full, "*", SearchOption.AllDirectories)
                        }
                    for dir in dirs do
                        let empty =
                            (Directory.EnumerateFiles dir |> Seq.isEmpty)
                            && (Directory.EnumerateDirectories dir |> Seq.isEmpty)
                        if empty then
                            let relDir =
                                if String.Equals(Path.GetFullPath dir, full, StringComparison.OrdinalIgnoreCase) then
                                    rootName
                                else
                                    Path.Combine(rootName, Path.GetRelativePath(full, dir))
                                    |> Pathing.normalize
                            let keep = self.PackageTargetRelative(relDir + "/.keep")
                            if Pathing.isSafe keep then
                                acc.Add(Path.GetFullPath dir, keep, true)
            with _ ->
                ()
        acc
        |> Seq.distinctBy (fun (full, rel, _) -> (full.ToLowerInvariant()) + "|" + rel.ToLowerInvariant())
        |> Seq.sortBy (fun (_, rel, _) -> rel)
        |> Seq.toList

    member private self.IsModPakPackagePath(path: string) =
        let ext = Path.GetExtension path
        ModPakFormat.OpenPatterns
        |> Array.exists (fun pattern ->
            let wanted = pattern.TrimStart('*')
            String.Equals(ext, wanted, StringComparison.OrdinalIgnoreCase))

    member private self.ConfirmAndLoadPackage(path: string) =
        async {
            let message = $"Do you really want to open this package?\n\n{path}"
            let! confirmed = self.ShowConfirmDialog("Open package", message) |> Async.AwaitTask
            if confirmed then
                self.TryLoadPackageFromPath path |> ignore
        }
        |> Async.StartImmediate

    member private self.FileListItemFromVisual(source: Avalonia.Visual) : FileListItem option =
        if isNull source then
            None
        else
            let mutable walk = source
            let mutable listItem: ListBoxItem = null
            while not (isNull walk) && isNull listItem do
                match walk with
                | :? ListBoxItem as item -> listItem <- item
                | _ -> walk <- walk.GetVisualParent()
            match listItem with
            | null -> None
            | item ->
                match item.DataContext with
                | :? FileListItem as row -> Some row
                | _ -> None

    member private self.OpenDiskItem(row: FileListItem) =
        if row.IsDirectory || row.IsParent then
            self.NavigateDisk row.PathKey
        elif File.Exists row.PathKey && self.IsModPakPackagePath row.PathKey then
            self.ConfirmAndLoadPackage row.PathKey
        elif File.Exists row.PathKey then
            self.ShellOpen row.PathKey
        else
            self.Show("Select a disk item.", muteBrush)

    member private self.OpenSelectedDiskItem(_: obj, _: RoutedEventArgs) =
        match self.SelectedDiskItem() with
        | Some row -> self.OpenDiskItem row
        | None -> self.Show("Select a disk item.", muteBrush)

    member private self.DiskListDoubleTapped(_: obj, e: TappedEventArgs) =
        // Toggle multi-select flips selection twice on double-click — open the row under the pointer.
        match e.Source with
        | :? Avalonia.Visual as visual ->
            match self.FileListItemFromVisual visual with
            | Some row -> self.OpenDiskItem row
            | None -> self.OpenSelectedDiskItem(null, null)
        | _ -> self.OpenSelectedDiskItem(null, null)

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

    member private self.EnsureDiskMenuItemRefs() =
        if isNull diskOpenMenuItem then
            diskOpenMenuItem <- self.FindControl<MenuItem>("DiskOpenMenuItem")
        if isNull diskAddMenuItem then
            diskAddMenuItem <- self.FindControl<MenuItem>("DiskAddMenuItem")
        if isNull diskInfoMenuItem then
            diskInfoMenuItem <- self.FindControl<MenuItem>("DiskInfoMenuItem")
        if isNull diskRenameMenuItem then
            diskRenameMenuItem <- self.FindControl<MenuItem>("DiskRenameMenuItem")
        if isNull diskDeleteMenuItem then
            diskDeleteMenuItem <- self.FindControl<MenuItem>("DiskDeleteMenuItem")

    member private self.UpdateDiskActionsEnabled() =
        self.EnsureDiskMenuItemRefs()
        let selected = self.SelectedDiskItem()
        let canOpen =
            match selected with
            | Some _ -> true
            | None -> false
        let canAdd = not (self.SelectedDiskRoots().IsEmpty)
        let canInfo =
            match selected with
            | Some row when not row.IsParent && (File.Exists row.PathKey || Directory.Exists row.PathKey) ->
                true
            | _ -> false
        let canEdit =
            match selected with
            | Some row when
                not row.IsParent
                && not (self.IsDriveRoot row.PathKey)
                && (File.Exists row.PathKey || Directory.Exists row.PathKey) ->
                true
            | _ -> false
        let canDelete = not (self.SelectedDiskRoots().IsEmpty)
        if not (isNull diskOpenMenuItem) then
            diskOpenMenuItem.IsEnabled <- canOpen
        if not (isNull diskAddMenuItem) then
            diskAddMenuItem.IsEnabled <- canAdd
        if not (isNull diskInfoMenuItem) then
            diskInfoMenuItem.IsEnabled <- canInfo
        if not (isNull diskRenameMenuItem) then
            diskRenameMenuItem.IsEnabled <- canEdit
        if not (isNull diskDeleteMenuItem) then
            diskDeleteMenuItem.IsEnabled <- canDelete
        self.RefreshPaneStatusSlots()

    member private self.DiskListGotFocus(_: obj, _: GotFocusEventArgs) =
        explorerPaneActive <- true

    member private self.DiskListSelectionChanged(_: obj, _: SelectionChangedEventArgs) =
        explorerPaneActive <- true
        self.UpdateDiskActionsEnabled()

    member private self.DiskListContextRequested(_: obj, e: ContextRequestedEventArgs) =
        match e.Source with
        | :? Avalonia.Visual as source -> self.SelectDiskListItemUnder source
        | _ -> ()
        self.UpdateDiskActionsEnabled()
        e.Handled <- false

    member private self.DiskListMenuOpening(sender: obj, _: CancelEventArgs) =
        match sender with
        | :? ContextMenu as menu ->
            for item in menu.Items do
                match item with
                | :? MenuItem as mi when mi.Name = "DiskOpenMenuItem" -> diskOpenMenuItem <- mi
                | :? MenuItem as mi when mi.Name = "DiskAddMenuItem" -> diskAddMenuItem <- mi
                | :? MenuItem as mi when mi.Name = "DiskInfoMenuItem" -> diskInfoMenuItem <- mi
                | :? MenuItem as mi when mi.Name = "DiskRenameMenuItem" -> diskRenameMenuItem <- mi
                | :? MenuItem as mi when mi.Name = "DiskDeleteMenuItem" -> diskDeleteMenuItem <- mi
                | _ -> ()
        | _ -> ()
        self.UpdateDiskActionsEnabled()

    member private self.SelectAllDiskItems(_: obj, _: RoutedEventArgs) =
        if not (isNull diskList) then
            diskList.SelectAll()
            self.UpdateDiskActionsEnabled()

    member private self.UnselectAllDiskItems(_: obj, _: RoutedEventArgs) =
        if not (isNull diskList) then
            diskList.UnselectAll()
            self.UpdateDiskActionsEnabled()

    member private self.IsSimpleFolderName(name: string) =
        not (String.IsNullOrWhiteSpace name)
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && not (name.Contains('\\') || name.Contains('/') || name.Contains(':'))
        && name <> "."
        && name <> ".."

    member private self.MkdirDiskFolder(_: obj, _: RoutedEventArgs) =
        match diskDir with
        | None -> self.Show("Open a folder first.", muteBrush)
        | Some parent when not (Directory.Exists parent) ->
            self.Show("Open a folder first.", muteBrush)
        | Some parent ->
            async {
                let! created = self.ShowMkdirDialog() |> Async.AwaitTask
                match created with
                | None -> ()
                | Some raw ->
                    let name = raw.Trim()
                    if not (self.IsSimpleFolderName name) then
                        self.Show("Enter a simple folder name.", errBrush)
                    else
                        let path = Path.Combine(parent, name)
                        if Directory.Exists path then
                            self.Show("Folder already exists.", muteBrush)
                        elif File.Exists path then
                            self.Show("A file with that name already exists.", errBrush)
                        else
                            try
                                Directory.CreateDirectory path |> ignore
                                self.RefreshDiskExplorer()
                                self.Show($"Created {name}", okBrush)
                            with error ->
                                self.Show(error.Message, errBrush)
            }
            |> Async.StartImmediate

    member private self.MkdirPackageFolder(_: obj, _: RoutedEventArgs) =
        async {
            let! created = self.ShowMkdirDialog() |> Async.AwaitTask
            match created with
            | None -> ()
            | Some raw ->
                let name = raw.Trim()
                if not (self.IsSimpleFolderName name) then
                    self.Show("Enter a simple folder name.", errBrush)
                else
                    let cwd = Pathing.normalize explorerDir
                    let dirPath = if cwd = "" then name else cwd + "/" + name
                    if not (Pathing.isSafe dirPath) then
                        self.Show("Invalid name.", errBrush)
                    elif self.IsVirtualDirectory dirPath
                         || self.PathAlreadyUsed dirPath ""
                         || self.PathAlreadyUsed (dirPath + "/.keep") "" then
                        self.Show($"Folder already exists: {name}", errBrush)
                    else
                        let keepPath = dirPath + "/.keep"
                        let bytes = Array.empty<byte>
                        let entry =
                            {
                                RelativePath = keepPath
                                Mime = "application/octet-stream"
                                Bytes = bytes
                                Codec = Codec.None
                                Meta = FileMetas.ofBytes bytes
                            }
                        let pkg = self.EnsurePackage()
                        package <- Some { pkg with Entries = pkg.Entries @ [ entry ] }
                        self.EnsureFileCodec keepPath
                        self.MarkDirty()
                        self.RefreshAfterEntryEdit()
                        self.Show($"Created {name}", okBrush)
        }
        |> Async.StartImmediate

    member private self.RenameSelectedDiskItem(_: obj, _: RoutedEventArgs) =
        match self.SelectedDiskItem() with
        | Some row when
            not row.IsParent
            && not (self.IsDriveRoot row.PathKey)
            && (File.Exists row.PathKey || Directory.Exists row.PathKey) ->
            async {
                let! renamed = self.ShowRenameDialog(Path.GetFileName row.PathKey) |> Async.AwaitTask
                match renamed with
                | None -> ()
                | Some nextName ->
                    let trimmed = nextName.Trim().Replace('/', Path.DirectorySeparatorChar)
                    if String.IsNullOrWhiteSpace trimmed
                       || trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                       || trimmed.Contains("\\") then
                        self.Show("Invalid name.", errBrush)
                    else
                        let parent = Path.GetDirectoryName row.PathKey
                        if String.IsNullOrWhiteSpace parent then
                            self.Show("Cannot rename this item.", errBrush)
                        else
                            let dest = Path.Combine(parent, trimmed)
                            if String.Equals(row.PathKey, dest, StringComparison.OrdinalIgnoreCase) then
                                ()
                            elif File.Exists dest || Directory.Exists dest then
                                self.Show("An item with that name already exists.", errBrush)
                            else
                                try
                                    if Directory.Exists row.PathKey then
                                        Directory.Move(row.PathKey, dest)
                                    else
                                        File.Move(row.PathKey, dest)
                                    self.RefreshDiskExplorer()
                                    self.Show($"Renamed to {trimmed}", okBrush)
                                with error ->
                                    self.Show(error.Message, errBrush)
            }
            |> Async.StartImmediate
        | _ -> self.Show("Select a file or folder to rename.", muteBrush)

    member private self.DeleteSelectedDiskItems(_: obj, _: RoutedEventArgs) =
        let roots = self.SelectedDiskRoots()
        if roots.IsEmpty then
            self.Show("Select files or folders to delete.", muteBrush)
        else
            async {
                let label = if roots.Length = 1 then "item" else "items"
                let! confirmed =
                    self.ShowConfirmDialog("Delete", $"Delete {roots.Length} {label} from disk?")
                    |> Async.AwaitTask
                if confirmed then
                    let mutable removed = 0
                    let mutable lastError = ""
                    for path in roots do
                        try
                            if Directory.Exists path then
                                Directory.Delete(path, true)
                                removed <- removed + 1
                            elif File.Exists path then
                                File.Delete path
                                removed <- removed + 1
                        with error ->
                            lastError <- error.Message
                    self.RefreshDiskExplorer()
                    self.UpdateDiskActionsEnabled()
                    if removed = 0 && lastError <> "" then
                        self.Show(lastError, errBrush)
                    elif lastError <> "" then
                        self.Show($"Deleted {removed}; last error: {lastError}", errBrush)
                    else
                        self.Show($"Deleted {removed} {label}.", okBrush)
            }
            |> Async.StartImmediate

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

    member private self.ExistingPackagePathKeys() : Set<string> =
        self.AllExplorerFileEntries()
        |> List.map (fun entry -> self.CodecKey entry.RelativePath)
        |> Set.ofList

    /// Asked from the copy worker. Must not block the UI thread — ShowDialog + GetResult deadlocks clicks.
    member private self.AskOverwriteOnUi(owner: Window, rel: string) : bool =
        if Dispatcher.UIThread.CheckAccess() then
            false
        else
            let gate = TaskCompletionSource<bool>()
            Dispatcher.UIThread.Post(fun () ->
                task {
                    try
                        let! answer =
                            self.ShowConfirmDialog(
                                "Overwrite",
                                $"Overwrite existing file?\n\n{rel}",
                                owner)
                        gate.TrySetResult answer |> ignore
                    with _ ->
                        gate.TrySetResult false |> ignore
                }
                |> ignore)
            gate.Task.GetAwaiter().GetResult()

    member private self.DeleteDiskRootsQuiet(roots: string list) =
        let mutable removed = 0
        for path in roots do
            try
                if Directory.Exists path then
                    Directory.Delete(path, true)
                    removed <- removed + 1
                elif File.Exists path then
                    File.Delete path
                    removed <- removed + 1
            with _ ->
                ()
        if removed > 0 then
            self.RefreshDiskExplorer()
        removed

    member private self.CopyDiskSelectionToPackage(roots: string list, ?deleteSources: bool) =
        let moveMode = defaultArg deleteSources false
        let files = self.CollectDiskTreeFiles roots
        if files.IsEmpty then
            self.Show("No files found to add.", muteBrush)
        else
            let logItems = ObservableCollection<string>()
            for _, rel, emptyDir in files do
                let shown =
                    if emptyDir && rel.EndsWith("/.keep", StringComparison.OrdinalIgnoreCase) then
                        rel.Substring(0, rel.Length - 6)
                    else
                        rel
                logItems.Add shown
            let bar =
                ProgressBar(
                    Minimum = 0.0,
                    Maximum = 100.0,
                    Height = 12.0,
                    Value = 0.0,
                    Margin = Thickness(0.0, 0.0, 0.0, 12.0))
            let actionLabel = if moveMode then "Move" else "Copy"
            let status =
                TextBlock(
                    Text = $"{files.Length} file(s) ready — press {actionLabel}.",
                    Foreground = SolidColorBrush(Color.Parse "#F3EDE6"),
                    FontSize = 13.0,
                    Margin = Thickness(0.0, 0.0, 0.0, 10.0),
                    TextWrapping = TextWrapping.Wrap)
            let log =
                ListBox(
                    ItemsSource = logItems,
                    Height = 200.0,
                    Margin = Thickness(0.0, 0.0, 0.0, 12.0))
            let overwriteCheck =
                CheckBox(
                    Content = "Overwrite existing files",
                    IsChecked = Nullable(config.OverwriteExistingFiles),
                    Margin = Thickness(0.0, 0.0, 0.0, 14.0))
            overwriteCheck.IsCheckedChanged.Add(fun _ ->
                let enabled = overwriteCheck.IsChecked.GetValueOrDefault(false)
                config.OverwriteExistingFiles <- enabled
                config.Save())
            let cancelBtn = self.DialogCommandIconButton("Cancel", Symbol.Dismiss)
            let copyBtn =
                self.DialogAccentIconButton(
                    actionLabel,
                    if moveMode then Symbol.ArrowRight else Symbol.Copy)
            let buttons =
                DockPanel(LastChildFill = false, Margin = Thickness(0.0, 4.0, 0.0, 0.0))
            DockPanel.SetDock(cancelBtn, Dock.Left)
            DockPanel.SetDock(copyBtn, Dock.Right)
            buttons.Children.Add(cancelBtn)
            buttons.Children.Add(copyBtn)
            let panel = StackPanel(Margin = Thickness(22.0))
            panel.Children.Add(status)
            panel.Children.Add(bar)
            panel.Children.Add(log)
            panel.Children.Add(overwriteCheck)
            panel.Children.Add(buttons)
            let dialogTitle = if moveMode then "Moving to package" else "Adding to package"
            let dialog = self.WrapStudioDialog(dialogTitle, panel)
            dialog.Width <- 480.0
            let cts = new CancellationTokenSource()
            let mutable copying = false
            let mutable finished = false
            cancelBtn.Click.Add(fun _ ->
                if copying && not finished then
                    cts.Cancel()
                    status.Text <- "Cancelling…"
                    cancelBtn.IsEnabled <- false
                else
                    dialog.Close())
            copyBtn.Click.Add(fun _ ->
                if copying then
                    ()
                else
                    copying <- true
                    copyBtn.IsEnabled <- false
                    overwriteCheck.IsEnabled <- false
                    status.Text <- if moveMode then "Moving…" else "Copying…"
                    let overwrite = overwriteCheck.IsChecked.GetValueOrDefault(false)
                    let existingKeys = self.ExistingPackagePathKeys()
                    ignore (
                        Task.Run(fun () ->
                            try
                                let total = files.Length
                                let built = ResizeArray<ModPakEntry>()
                                let abs = ResizeArray<string>()
                                let mutable index = 0
                                let mutable skipped = 0
                                let mutable cancelled = false
                                for full, rel, emptyDir in files do
                                    if cts.IsCancellationRequested then
                                        cancelled <- true
                                    elif cancelled then
                                        ()
                                    else
                                        index <- index + 1
                                        let n = index
                                        let key = self.CodecKey rel
                                        let exists = existingKeys.Contains key
                                        let take =
                                            if not exists then
                                                true
                                            elif overwrite then
                                                true
                                            else
                                                self.AskOverwriteOnUi(dialog, rel)
                                        if cts.IsCancellationRequested then
                                            cancelled <- true
                                        else
                                            Dispatcher.UIThread.Post(fun () ->
                                                status.Text <-
                                                    if take then
                                                        if moveMode then $"Moving {n} / {total}"
                                                        else $"Copying {n} / {total}"
                                                    else $"Skipping {n} / {total}"
                                                bar.Value <- float n / float total * 100.0
                                                if logItems.Count > 0 then
                                                    let focus = min (n - 1) (logItems.Count - 1)
                                                    log.ScrollIntoView(logItems.[focus]))
                                            if not take then
                                                skipped <- skipped + 1
                                                Dispatcher.UIThread.Post(fun () ->
                                                    logItems.Add($"· skipped {rel}"))
                                            else
                                                try
                                                    let bytes, codec, meta =
                                                        if emptyDir then
                                                            Array.empty<byte>, Codec.None, FileMetas.ofBytes Array.empty
                                                        else
                                                            let bytes = File.ReadAllBytes full
                                                            bytes, Codec.Lz4, FileMetas.ofFilePath full bytes
                                                    if cts.IsCancellationRequested then
                                                        cancelled <- true
                                                    else
                                                        built.Add(
                                                            {
                                                                RelativePath = rel
                                                                Mime = if emptyDir then "application/octet-stream" else Mime.ofPath rel
                                                                Bytes = bytes
                                                                Codec = codec
                                                                Meta = meta
                                                            })
                                                        if not emptyDir then
                                                            abs.Add full
                                                        Dispatcher.UIThread.Post(fun () ->
                                                            let label =
                                                                if emptyDir
                                                                   && rel.EndsWith("/.keep", StringComparison.OrdinalIgnoreCase) then
                                                                    rel.Substring(0, rel.Length - 6)
                                                                else
                                                                    rel
                                                            logItems.Add($"✓ {label}"))
                                                with ex ->
                                                    Dispatcher.UIThread.Post(fun () ->
                                                        logItems.Add($"! {rel}: {ex.Message}"))
                                let entries = built |> Seq.toList
                                let paths = abs |> Seq.toList
                                Dispatcher.UIThread.Post(fun () ->
                                    finished <- true
                                    cancelBtn.IsEnabled <- true
                                    cancelBtn.Content <- self.DialogButtonContent("Close", Symbol.Dismiss)
                                    if cancelled then
                                        status.Text <- "Cancelled — nothing added."
                                        bar.Value <- 100.0
                                    else
                                        self.MergeBuiltEntriesIntoPackage(entries, paths)
                                        if moveMode && entries.Length > 0 then
                                            let deleted = self.DeleteDiskRootsQuiet roots
                                            status.Text <-
                                                $"Done — {entries.Length} file(s) moved, {skipped} skipped, {deleted} source(s) removed."
                                            self.Show($"Moved {entries.Length} file(s) into package.", okBrush)
                                        else
                                            status.Text <-
                                                $"Done — {entries.Length} file(s) added, {skipped} skipped."
                                            self.Show($"Added {entries.Length} file(s) to package.", okBrush)
                                        bar.Value <- 100.0
                                        dialog.Close())
                            with ex ->
                                Dispatcher.UIThread.Post(fun () ->
                                    finished <- true
                                    cancelBtn.IsEnabled <- true
                                    cancelBtn.Content <- self.DialogButtonContent("Close", Symbol.Dismiss)
                                    status.Text <- $"Failed: {ex.Message}"
                                    self.Show(ex.Message, errBrush)))))
            dialog.Closed.Add(fun _ -> cts.Dispose())
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
        | _ -> self.CopyDiskSelectionToPackage paths

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

    /// File paths to unpack/copy for the current Package selection (folders expand to contained files).
    member private self.SelectedUnpackPathKeys() =
        let selected = self.SelectedEntryPathKeys()
        if selected.IsEmpty then
            []
        else
            self.AllExplorerFileEntries()
            |> List.map (fun entry -> Pathing.normalize entry.RelativePath)
            |> List.filter (fun path -> self.PathMatchesSelection(path, selected))
            |> List.distinct

    /// Last list pane the user worked in (Fn buttons steal focus — keep this, don't infer from leftover selection).
    member private self.FnSourceIsExplorer() = explorerPaneActive

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
            not selectedEntries.IsEmpty
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
        self.RefreshPaneStatusSlots()

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

    member private self.FileListGotFocus(_: obj, _: GotFocusEventArgs) =
        explorerPaneActive <- false

    member private self.FileListSelectionChanged(_: obj, _: SelectionChangedEventArgs) =
        explorerPaneActive <- false
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

    member private self.RemoveSelectedEntries(selected: string list) =
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
        self.MarkDirty()
        removed

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
                    let removed = self.RemoveSelectedEntries selected
                    let label = if removed = 1 then "item" else "items"
                    self.Show($"Deleted {removed} {label}.", okBrush)
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
        let mutable walk =
            match e.Source with
            | :? Avalonia.Visual as visual -> visual
            | _ -> null
        let mutable fromCodec = false
        while not (isNull walk) && not fromCodec do
            match walk with
            | :? Button as button when button.Classes.Contains("codec-chip") -> fromCodec <- true
            | _ -> walk <- walk.GetVisualParent()
        if not fromCodec then
            match e.Source with
            | :? Avalonia.Visual as visual ->
                match self.FileListItemFromVisual visual with
                | Some row -> self.OpenExplorerItem row
                | None -> self.OpenSelectedFile(sender, e)
            | _ -> self.OpenSelectedFile(sender, e)

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

    member private self.OpenExplorerItem(row: FileListItem) =
        if row.IsDirectory || row.IsParent then
            self.NavigateExplorer row.PathKey
        else
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

    member private self.OpenSelectedFile(_: obj, _: RoutedEventArgs) =
        match self.SelectedExplorerItem() with
        | Some row -> self.OpenExplorerItem row
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
            DateTimeOffset.FromUnixTimeMilliseconds(int64 ms).ToLocalTime().ToString("dddd d MMMM yyyy, HH:mm:ss")

    member private self.FormatBytesDetailed(bytes: int64) =
        let n = float bytes
        let human =
            if bytes >= 1024L * 1024L then $"%.1f{n / 1024.0 / 1024.0} MB"
            elif bytes >= 1024L then $"%.1f{n / 1024.0} KB"
            else $"{bytes} B"
        $"{human} ({bytes:N0} bytes)"

    member private self.FileTypeLabel(path: string, mime: string) =
        let ext = Path.GetExtension path
        let extPart =
            if String.IsNullOrWhiteSpace ext then ""
            else $" ({ext.ToLowerInvariant()})"
        if not (String.IsNullOrWhiteSpace mime) then
            $"{mime}{extPart}"
        elif String.IsNullOrWhiteSpace ext then
            "File"
        else
            $"{ext.TrimStart('.').ToUpperInvariant()} File{extPart}"

    member private self.FormatUnixPermissions(mode: uint16) =
        let bit mask ch = if (mode &&& mask) <> 0us then ch else '-'
        let symbolic =
            System.String [|
                bit 0o400us 'r'
                bit 0o200us 'w'
                bit 0o100us 'x'
                bit 0o040us 'r'
                bit 0o020us 'w'
                bit 0o010us 'x'
                bit 0o004us 'r'
                bit 0o002us 'w'
                bit 0o001us 'x'
            |]
        $"0o{Convert.ToString(int mode, 8)} (-{symbolic})"

    member private self.PropertiesSeparator() =
        Border(
            Height = 1.0,
            Background = SolidColorBrush(Color.Parse "#3F3731"),
            Margin = Thickness(0.0, 10.0, 0.0, 10.0),
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Stretch)

    member private self.PropertiesField(label: string, value: string) =
        let grid = Grid(Margin = Thickness(0.0, 0.0, 0.0, 6.0))
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(118.0)))
        grid.ColumnDefinitions.Add(ColumnDefinition(Width = GridLength(1.0, GridUnitType.Star)))
        let labelBlock =
            TextBlock(
                Text = label,
                FontSize = 12.0,
                Foreground = SolidColorBrush(Color.Parse "#A89888"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top)
        let valueBlock =
            TextBlock(
                Text = value,
                FontSize = 12.0,
                Foreground = SolidColorBrush(Color.Parse "#F0E6DC"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top)
        Grid.SetColumn(valueBlock, 1)
        grid.Children.Add(labelBlock)
        grid.Children.Add(valueBlock)
        grid :> Control

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

    member private self.BuildContainerTabContent(pkg: ModPakPackage) =
        let major, minor = self.ContainerVersion()
        let panel = StackPanel(Spacing = 0.0, Margin = Thickness(4.0, 8.0, 4.0, 0.0))
        panel.Children.Add(self.PropertiesField("Format:", "ModPak"))
        panel.Children.Add(self.PropertiesField("Container:", "MODPAK64"))
        panel.Children.Add(self.PropertiesField("Version:", $"{major}.{minor}"))
        panel.Children.Add(self.PropertiesSeparator())
        for id, n in self.ChunkCounts pkg do
            panel.Children.Add(self.PropertiesField($"{id}:", string n))
        panel :> Control

    member private self.ShowPropertiesDialog
        (
            title: string,
            icon: IImage,
            initialName: string,
            typeLabel: string,
            location: string,
            sizeText: string,
            sizeOnDiskText: string,
            created: string,
            modified: string,
            accessed: string,
            readOnly: bool,
            hidden: bool,
            extraRows: (string * string) list,
            canEditAttrs: bool,
            onApply: (string * bool * bool) -> bool,
            ?unixPermissions: string,
            ?containerTab: Control
        ) =
        let nameBox =
            TextBox(
                Text = initialName,
                MinHeight = 34.0,
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center)
        let header = DockPanel(LastChildFill = true, Margin = Thickness(0.0, 0.0, 0.0, 14.0))
        if not (isNull icon) then
            let iconImage =
                Image(
                    Source = icon,
                    Width = 48.0,
                    Height = 48.0,
                    Margin = Thickness(0.0, 0.0, 14.0, 0.0),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center)
            DockPanel.SetDock(iconImage, Avalonia.Controls.Dock.Left)
            header.Children.Add(iconImage)
        header.Children.Add(nameBox)

        let body = StackPanel(Spacing = 0.0)
        body.Children.Add(header)
        body.Children.Add(self.PropertiesField("Type of file:", typeLabel))
        body.Children.Add(self.PropertiesField("Location:", location))
        body.Children.Add(self.PropertiesField("Size:", sizeText))
        body.Children.Add(self.PropertiesField("Size on disk:", sizeOnDiskText))
        body.Children.Add(self.PropertiesSeparator())
        body.Children.Add(self.PropertiesField("Created:", created))
        body.Children.Add(self.PropertiesField("Modified:", modified))
        body.Children.Add(self.PropertiesField("Accessed:", accessed))
        for label, value in extraRows do
            body.Children.Add(self.PropertiesField(label, value))
        body.Children.Add(self.PropertiesSeparator())

        let readOnlyBox =
            CheckBox(
                Content = "Read-only",
                IsChecked = Nullable(readOnly),
                IsEnabled = canEditAttrs,
                Margin = Thickness(0.0, 0.0, 16.0, 0.0))
        let hiddenBox =
            CheckBox(
                Content = "Hidden",
                IsChecked = Nullable(hidden),
                IsEnabled = canEditAttrs)
        match unixPermissions with
        | Some perms ->
            body.Children.Add(self.PropertiesField("Permissions:", perms))
        | None when canEditAttrs ->
            let attrsRow = StackPanel(Orientation = Orientation.Horizontal, Spacing = 8.0)
            let attrsLabel =
                TextBlock(
                    Text = "Attributes:",
                    FontSize = 12.0,
                    Foreground = SolidColorBrush(Color.Parse "#A89888"),
                    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
                    Width = 118.0)
            attrsRow.Children.Add(attrsLabel)
            attrsRow.Children.Add(readOnlyBox)
            attrsRow.Children.Add(hiddenBox)
            body.Children.Add(attrsRow)
        | None -> ()

        let ok = self.DialogAccentButton("OK")
        let cancel = self.DialogCommandButton("Cancel")
        let apply = self.DialogCommandButton("Apply")
        apply.IsEnabled <- false
        let buttons = self.DialogButtonRow3(ok, cancel, apply)
        buttons.Margin <- Thickness(0.0, 18.0, 0.0, 0.0)

        let markDirty () =
            let nameNow = if isNull nameBox.Text then "" else nameBox.Text.Trim()
            let roNow = readOnlyBox.IsChecked.GetValueOrDefault(false)
            let hidNow = hiddenBox.IsChecked.GetValueOrDefault(false)
            apply.IsEnabled <-
                not (String.Equals(nameNow, initialName, StringComparison.Ordinal))
                || (canEditAttrs
                    && (roNow <> readOnly || hidNow <> hidden))

        nameBox.TextChanged.Add(fun _ -> markDirty ())
        if canEditAttrs then
            readOnlyBox.IsCheckedChanged.Add(fun _ -> markDirty ())
            hiddenBox.IsCheckedChanged.Add(fun _ -> markDirty ())

        let tryApply () =
            let nameNow = if isNull nameBox.Text then "" else nameBox.Text.Trim()
            let roNow = readOnlyBox.IsChecked.GetValueOrDefault(false)
            let hidNow = hiddenBox.IsChecked.GetValueOrDefault(false)
            if onApply (nameNow, roNow, hidNow) then
                apply.IsEnabled <- false
                true
            else
                false

        let mainContent: Control =
            match containerTab with
            | None -> ScrollViewer(Content = body, MaxHeight = 520.0) :> Control
            | Some tabBody ->
                let tabs = TabControl(Margin = Thickness(0.0, 0.0, 0.0, 0.0))
                tabs.Items.Add(
                    TabItem(
                        Header = "General",
                        Content = ScrollViewer(Content = body, MaxHeight = 480.0)))
                |> ignore
                tabs.Items.Add(
                    TabItem(
                        Header = "Container",
                        Content = ScrollViewer(Content = tabBody, MaxHeight = 480.0)))
                |> ignore
                tabs.SelectedIndex <- 0
                tabs :> Control

        let root = DockPanel()
        DockPanel.SetDock(buttons, Avalonia.Controls.Dock.Bottom)
        root.Children.Add(buttons)
        root.Children.Add(mainContent)

        let dialog =
            Window(
                Title = title,
                Width = 420.0,
                SizeToContent = SizeToContent.Height,
                CanResize = false,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                Background = SolidColorBrush(Color.Parse "#1A1613"),
                Icon = WindowIcon(AssetLoader.Open(Uri("avares://ModPak.App/Assets/icon.ico"))),
                Content = Border(Padding = Thickness(20.0), Child = root))
        ok.Click.Add(fun _ ->
            if (not apply.IsEnabled) || tryApply () then
                dialog.Close())
        cancel.Click.Add(fun _ -> dialog.Close())
        apply.Click.Add(fun _ -> tryApply () |> ignore)
        dialog.ShowDialog(self) |> ignore

    member private self.ShowEntryProperties(entry: ModPakEntry) =
        let meta = entry.Meta
        let name = Path.GetFileName entry.RelativePath
        let location =
            match Path.GetDirectoryName entry.RelativePath with
            | null | "" -> "/"
            | dir ->
                let normalized = Pathing.normalize dir
                if String.IsNullOrWhiteSpace normalized then "/" else normalized
        let sizeBytes = int64 entry.Bytes.Length
        let unpacked = int64 meta.UnpackedSize
        let container =
            self.TryPackageForInfo()
            |> Option.map self.BuildContainerTabContent
        self.ShowPropertiesDialog(
            $"{name} Properties",
            icons.GetIcon entry.RelativePath,
            name,
            self.FileTypeLabel(entry.RelativePath, entry.Mime),
            location,
            self.FormatBytesDetailed sizeBytes,
            self.FormatBytesDetailed (if unpacked > 0L then unpacked else sizeBytes),
            self.FormatUnixMs meta.CreatedTime,
            self.FormatUnixMs meta.ModifiedTime,
            self.FormatUnixMs meta.AccessedTime,
            false,
            false,
            [
                "Compression:", self.CodecLabel entry.Codec
                "CRC32:", $"0x{meta.Crc32:X8}"
                "Path:", entry.RelativePath
            ],
            false,
            (fun (nextName, _, _) ->
                if String.Equals(nextName, name, StringComparison.Ordinal) then
                    true
                elif String.IsNullOrWhiteSpace nextName
                     || nextName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                     || nextName.Contains('/')
                     || nextName.Contains('\\') then
                    self.Show("Invalid name.", errBrush)
                    false
                else
                    let parentPath =
                        match Path.GetDirectoryName entry.RelativePath with
                        | null | "" -> ""
                        | dir -> Pathing.normalize dir
                    let nextPath =
                        if parentPath = "" then nextName
                        else $"{parentPath}/{nextName}"
                    let pathKey = entry.RelativePath
                    if self.PathAlreadyUsed nextPath pathKey then
                        self.Show("Path already used in this package.", errBrush)
                        false
                    else
                        match package with
                        | Some pkg ->
                            let nextEntries =
                                pkg.Entries
                                |> List.map (fun e ->
                                    if String.Equals(e.RelativePath, pathKey, StringComparison.OrdinalIgnoreCase) then
                                        { e with
                                            RelativePath = nextPath
                                            Mime = Mime.ofPath nextPath }
                                    else
                                        e)
                            package <- Some { pkg with Entries = nextEntries }
                            self.RemapFileCodecs(pathKey, nextPath)
                            self.MarkDirty()
                            self.RefreshAfterEntryEdit()
                            self.Show($"Renamed to {nextPath}", okBrush)
                            true
                        | None ->
                            pendingRenames <-
                                pendingRenames
                                    .Remove(self.CodecKey pathKey)
                                    .Add(self.CodecKey pathKey, nextPath)
                            self.RemapFileCodecs(pathKey, nextPath)
                            self.MarkDirty()
                            self.RefreshAfterEntryEdit()
                            self.Show($"Renamed to {nextPath}", okBrush)
                            true),
            unixPermissions = self.FormatUnixPermissions meta.Permissions,
            ?containerTab = container)

    member private self.ShowContainerProperties(pkg: ModPakPackage) =
        let name =
            match packagePath with
            | Some path -> Path.GetFileNameWithoutExtension path
            | None -> "ModPak"
        let titleName =
            match packagePath with
            | Some path -> Path.GetFileName path
            | None -> "ModPak"
        let location =
            match packagePath with
            | Some path ->
                let dir = Path.GetDirectoryName path
                if String.IsNullOrWhiteSpace dir then path else dir
            | None -> "(unsaved)"
        let sizeBytes, created, modified, accessed =
            match packagePath with
            | Some path when File.Exists path ->
                let info = FileInfo path
                int64 info.Length,
                info.CreationTime.ToString("dddd d MMMM yyyy, HH:mm:ss"),
                info.LastWriteTime.ToString("dddd d MMMM yyyy, HH:mm:ss"),
                info.LastAccessTime.ToString("dddd d MMMM yyyy, HH:mm:ss")
            | _ ->
                let total =
                    pkg.Entries
                    |> List.sumBy (fun e -> int64 e.Bytes.Length)
                total, "—", "—", "—"
        let logo = new Bitmap(AssetLoader.Open(Uri("avares://ModPak.App/Assets/KI.png"))) :> IImage
        self.ShowPropertiesDialog(
            $"{titleName} Properties",
            logo,
            name,
            "ModPak package (.mpx)",
            location,
            self.FormatBytesDetailed sizeBytes,
            self.FormatBytesDetailed sizeBytes,
            created,
            modified,
            accessed,
            false,
            false,
            [],
            false,
            (fun _ -> true),
            containerTab = self.BuildContainerTabContent pkg)

    member private self.ShowDiskInfoDialog(_: obj, _: RoutedEventArgs) =
        match self.SelectedDiskItem() with
        | Some row when not row.IsParent && (File.Exists row.PathKey || Directory.Exists row.PathKey) ->
            if not (ShellFileProperties.tryShow row.PathKey) then
                self.Show("Could not open system properties for this item.", errBrush)
        | _ -> self.Show("Select a file or folder in Explorer.", muteBrush)

    member private self.ShowInfoDialog(_: obj, _: RoutedEventArgs) =
        let fileOpt =
            match self.SelectedPathKeys() with
            | [ pathKey ] -> self.TryResolveEntry pathKey
            | _ -> None
        match fileOpt with
        | Some entry -> self.ShowEntryProperties entry
        | None ->
            match self.TryPackageForInfo() with
            | Some pkg -> self.ShowContainerProperties pkg
            | None -> self.Show("Open a package or select a file.", muteBrush)

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
                    let message = $"Do you really want to open this package?\n\n{last}"
                    let! confirmed = self.ShowConfirmDialog("Open package", message)
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
            | paths ->
                if not (isNull metadataTabs) then
                    metadataTabs.SelectedIndex <- 0
                self.CopyDiskSelectionToPackage paths
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

    member private self.CopyPackageKeysToExplorer(fileKeys: string list) =
        match diskDir with
        | None -> self.Show("Open a folder in Explorer first.", muteBrush)
        | Some target when not (Directory.Exists target) ->
            self.Show("Open a folder in Explorer first.", muteBrush)
        | Some target -> self.StartPackageExport(fileKeys, target, false, [])

    member private self.UnpackPackage(_: obj, _: RoutedEventArgs) =
        let entryKeys = self.SelectedEntryPathKeys()
        let selected = self.SelectedUnpackPathKeys()
        if entryKeys.IsEmpty then
            self.Show("Select items in Package first.", muteBrush)
        else
            self.CopyPackageKeysToExplorer selected

    member private self.UnpackAllPackage(_: obj, _: RoutedEventArgs) =
        self.CopyPackageKeysToExplorer(self.AllUnpackPathKeys())

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

        let logo =
            Image(
                Source = new Bitmap(AssetLoader.Open(Uri("avares://ModPak.App/Assets/KI.png"))),
                Width = 112.0,
                Height = 112.0,
                Margin = Thickness(0.0, 0.0, 0.0, 12.0),
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
                Margin = Thickness(0.0, 2.0, 0.0, 18.0))
        licenseRow.Children.Add(
            TextBlock(
                Text = "Licensed under the",
                FontSize = 12.0,
                Foreground = SolidColorBrush(Color.Parse "#C8B8A8"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center))
        licenseRow.Children.Add(licenseButton)

        let ok = self.DialogOkButton()
        ok.HorizontalAlignment <- Avalonia.Layout.HorizontalAlignment.Center
        ok.MinWidth <- 140.0

        let panel = StackPanel(Margin = Thickness(22.0), Spacing = 4.0)
        panel.Children.Add(logo)
        panel.Children.Add(line $"ModPak {AppVersion}" 20.0 "#F0E6DC" FontWeight.SemiBold)
        panel.Children.Add(line "Frank Hliva" 13.0 "#F3EDE6" FontWeight.Normal)
        panel.Children.Add(line "Copyright © 2026" 12.0 "#C8B8A8" FontWeight.Normal)
        panel.Children.Add(line "Kingmaker SAS" 12.0 "#C8B8A8" FontWeight.Normal)
        panel.Children.Add(licenseRow)
        panel.Children.Add(ok)

        let about = self.WrapStudioDialog($"About ModPak {AppVersion}", panel)
        about.Width <- 420.0
        ok.Click.Add(fun _ -> about.Close())
        about.ShowDialog(self) |> ignore

    member private self.IsTypingFocus() =
        match self.FocusManager with
        | null -> false
        | fm ->
            match fm.GetFocusedElement() with
            | :? TextBox
            | :? AvaloniaEdit.TextEditor -> true
            | _ -> false

    member private self.IsKeepRelative(path: string) =
        String.Equals(Path.GetFileName(Pathing.normalize path), ".keep", StringComparison.OrdinalIgnoreCase)

    member private self.ExpandPackageKeys(keys: string list) =
        if keys.IsEmpty then
            []
        else
            self.AllExplorerFileEntries()
            |> List.map (fun entry -> Pathing.normalize entry.RelativePath)
            |> List.filter (fun path -> self.PathMatchesSelection(path, keys))
            |> List.distinct

    /// Progress dialog + background copy, same as Explorer → Package.
    member private self.StartPackageExport
        (
            fileKeys: string list,
            target: string,
            moveMode: bool,
            removeKeys: string list
        ) =
        if package.IsNone && pendingPaths.IsEmpty then
            self.Show("Open a package or add files first.", muteBrush)
        elif not (Directory.Exists target) then
            self.Show("Open a folder in Explorer first.", muteBrush)
        else
            let rows =
                let entries = self.AllExplorerFileEntries()
                self.ExpandPackageKeys fileKeys
                |> List.choose (fun rel ->
                    let norm = Pathing.normalize rel
                    if self.IsKeepRelative norm then
                        Some(norm, Array.empty<byte>, true)
                    else
                        entries
                        |> List.tryFind (fun entry ->
                            String.Equals(
                                Pathing.normalize entry.RelativePath,
                                norm,
                                StringComparison.OrdinalIgnoreCase))
                        |> Option.map (fun entry -> norm, entry.Bytes, false))
            if rows.IsEmpty then
                self.Show("Nothing to copy.", muteBrush)
            else
                let shownOf (rel: string) isKeep =
                    if isKeep && rel.EndsWith("/.keep", StringComparison.OrdinalIgnoreCase) then
                        rel.Substring(0, rel.Length - 6)
                    else
                        rel
                let logItems = ObservableCollection<string>()
                for rel, _, isKeep in rows do
                    logItems.Add(shownOf rel isKeep)
                let bar =
                    ProgressBar(
                        Minimum = 0.0,
                        Maximum = 100.0,
                        Height = 12.0,
                        Value = 0.0,
                        Margin = Thickness(0.0, 0.0, 0.0, 12.0))
                let actionLabel = if moveMode then "Move" else "Copy"
                let status =
                    TextBlock(
                        Text = $"{rows.Length} file(s) ready — press {actionLabel}.",
                        Foreground = SolidColorBrush(Color.Parse "#F3EDE6"),
                        FontSize = 13.0,
                        Margin = Thickness(0.0, 0.0, 0.0, 10.0),
                        TextWrapping = TextWrapping.Wrap)
                let log =
                    ListBox(
                        ItemsSource = logItems,
                        Height = 200.0,
                        Margin = Thickness(0.0, 0.0, 0.0, 12.0))
                let overwriteCheck =
                    CheckBox(
                        Content = "Overwrite existing files",
                        IsChecked = Nullable(config.OverwriteExistingFiles),
                        Margin = Thickness(0.0, 0.0, 0.0, 14.0))
                overwriteCheck.IsCheckedChanged.Add(fun _ ->
                    let enabled = overwriteCheck.IsChecked.GetValueOrDefault(false)
                    config.OverwriteExistingFiles <- enabled
                    config.Save())
                let cancelBtn = self.DialogCommandIconButton("Cancel", Symbol.Dismiss)
                let copyBtn =
                    self.DialogAccentIconButton(
                        actionLabel,
                        if moveMode then Symbol.ArrowRight else Symbol.Copy)
                let buttons =
                    DockPanel(LastChildFill = false, Margin = Thickness(0.0, 4.0, 0.0, 0.0))
                DockPanel.SetDock(cancelBtn, Dock.Left)
                DockPanel.SetDock(copyBtn, Dock.Right)
                buttons.Children.Add(cancelBtn)
                buttons.Children.Add(copyBtn)
                let panel = StackPanel(Margin = Thickness(22.0))
                panel.Children.Add(status)
                panel.Children.Add(bar)
                panel.Children.Add(log)
                panel.Children.Add(overwriteCheck)
                panel.Children.Add(buttons)
                let dialogTitle =
                    if moveMode then "Moving to Explorer" else "Copying to Explorer"
                let dialog = self.WrapStudioDialog(dialogTitle, panel)
                dialog.Width <- 480.0
                let cts = new CancellationTokenSource()
                let mutable copying = false
                let mutable finished = false
                cancelBtn.Click.Add(fun _ ->
                    if copying && not finished then
                        cts.Cancel()
                        status.Text <- "Cancelling…"
                        cancelBtn.IsEnabled <- false
                    else
                        dialog.Close())
                copyBtn.Click.Add(fun _ ->
                    if copying then
                        ()
                    else
                        copying <- true
                        copyBtn.IsEnabled <- false
                        overwriteCheck.IsEnabled <- false
                        status.Text <- if moveMode then "Moving…" else "Copying…"
                        let overwrite = overwriteCheck.IsChecked.GetValueOrDefault(false)
                        ignore (
                            Task.Run(fun () ->
                                try
                                    let total = rows.Length
                                    let mutable index = 0
                                    let mutable written = 0
                                    let mutable skipped = 0
                                    let mutable cancelled = false
                                    for rel, bytes, isKeep in rows do
                                        if cts.IsCancellationRequested then
                                            cancelled <- true
                                        elif cancelled then
                                            ()
                                        else
                                            index <- index + 1
                                            let n = index
                                            let label = shownOf rel isKeep
                                            let destRel = label.Replace('/', Path.DirectorySeparatorChar)
                                            let dest = Path.Combine(target, destRel)
                                            let exists =
                                                if isKeep then Directory.Exists dest
                                                else File.Exists dest
                                            let take =
                                                if not exists then true
                                                elif isKeep then true
                                                elif overwrite then true
                                                else self.AskOverwriteOnUi(dialog, label)
                                            if cts.IsCancellationRequested then
                                                cancelled <- true
                                            else
                                                Dispatcher.UIThread.Post(fun () ->
                                                    status.Text <-
                                                        if take then
                                                            if moveMode then $"Moving {n} / {total}"
                                                            else $"Copying {n} / {total}"
                                                        else $"Skipping {n} / {total}"
                                                    bar.Value <- float n / float total * 100.0
                                                    if logItems.Count > 0 then
                                                        let focus = min (n - 1) (logItems.Count - 1)
                                                        log.ScrollIntoView(logItems.[focus]))
                                                if not take then
                                                    skipped <- skipped + 1
                                                    Dispatcher.UIThread.Post(fun () ->
                                                        logItems.Add($"· skipped {label}"))
                                                else
                                                    try
                                                        if isKeep then
                                                            if dest <> "" then
                                                                Directory.CreateDirectory dest |> ignore
                                                        else
                                                            let dir = Path.GetDirectoryName dest
                                                            if not (String.IsNullOrWhiteSpace dir) then
                                                                Directory.CreateDirectory dir |> ignore
                                                            File.WriteAllBytes(dest, bytes)
                                                        written <- written + 1
                                                        Dispatcher.UIThread.Post(fun () ->
                                                            logItems.Add($"✓ {label}"))
                                                    with ex ->
                                                        Dispatcher.UIThread.Post(fun () ->
                                                            logItems.Add($"! {label}: {ex.Message}"))
                                    Dispatcher.UIThread.Post(fun () ->
                                        finished <- true
                                        cancelBtn.IsEnabled <- true
                                        cancelBtn.Content <-
                                            self.DialogButtonContent("Close", Symbol.Dismiss)
                                        if cancelled then
                                            status.Text <- "Cancelled."
                                            bar.Value <- 100.0
                                        else
                                            self.RefreshDiskExplorer()
                                            if moveMode && written > 0 && not removeKeys.IsEmpty then
                                                let removed = self.RemoveSelectedEntries removeKeys
                                                status.Text <-
                                                    $"Done — {written} written, {skipped} skipped, {removed} removed from package."
                                                self.Show(
                                                    $"Moved {written} → {target}",
                                                    okBrush)
                                            else
                                                status.Text <-
                                                    $"Done — {written} written, {skipped} skipped."
                                                self.Show($"Copied {written} → {target}", okBrush)
                                            bar.Value <- 100.0
                                            dialog.Close())
                                with ex ->
                                    Dispatcher.UIThread.Post(fun () ->
                                        finished <- true
                                        cancelBtn.IsEnabled <- true
                                        cancelBtn.Content <-
                                            self.DialogButtonContent("Close", Symbol.Dismiss)
                                        status.Text <- $"Failed: {ex.Message}"
                                        self.Show(ex.Message, errBrush)))))
                dialog.Closed.Add(fun _ -> cts.Dispose())
                dialog.ShowDialog(self) |> ignore

    member private self.DropPackagePathsOnDisk(keys: string list, target: string) =
        self.StartPackageExport(keys, target, false, [])

    member private self.TransferPackageSelectionToDisk(moveMode: bool) =
        let selected = self.SelectedUnpackPathKeys()
        let entryKeys = self.SelectedEntryPathKeys()
        if entryKeys.IsEmpty then
            self.Show("Select items in Package first.", muteBrush)
        elif selected.IsEmpty then
            self.Show("No files under the Package selection.", muteBrush)
        else
            match diskDir with
            | None -> self.Show("Open a folder in Explorer first.", muteBrush)
            | Some target when not (Directory.Exists target) ->
                self.Show("Open a folder in Explorer first.", muteBrush)
            | Some target ->
                self.StartPackageExport(
                    selected,
                    target,
                    moveMode,
                    if moveMode then entryKeys else [])

    /// True once per click. PointerPressed and Click both reach these handlers.
    member private self.AcceptFn() =
        let now = Environment.TickCount64
        if now - lastFnTick < 400L then
            false
        else
            lastFnTick <- now
            true

    member private self.FnBarPointerPressed(sender: obj, e: PointerPressedEventArgs) =
        let root =
            match sender with
            | :? Avalonia.Visual as visual -> visual
            | _ -> null
        if not (isNull root) && e.GetCurrentPoint(root).Properties.IsLeftButtonPressed then
            let mutable walk: Avalonia.Visual =
                match e.Source with
                | :? Avalonia.Visual as visual -> visual
                | _ -> null
            let mutable button: Button = null
            while not (isNull walk) && isNull button do
                match walk with
                | :? Button as found when
                    not (String.IsNullOrEmpty found.Name) && found.Name.StartsWith("Fn") ->
                    button <- found
                | _ -> walk <- walk.GetVisualParent()
            if not (isNull button) then
                match button.Name with
                | "FnInfoButton" -> self.FnInfo(button, null)
                | "FnCopyButton" -> self.FnCopy(button, null)
                | "FnMoveButton" -> self.FnMove(button, null)
                | "FnMkdirButton" -> self.FnMkdir(button, null)
                | "FnDeleteButton" -> self.FnDelete(button, null)
                | "FnQuitButton" -> self.FnQuit(button, null)
                | _ -> ()

    member private self.FnInfo(_: obj, _: RoutedEventArgs) =
        if self.AcceptFn() then
            if self.FnSourceIsExplorer() then
                self.ShowDiskInfoDialog(null, null)
            else
                self.ShowInfoDialog(null, null)

    member private self.FnCopy(_: obj, _: RoutedEventArgs) =
        if self.AcceptFn() then
            if self.FnSourceIsExplorer() then
                let roots = self.SelectedDiskRoots()
                if roots.IsEmpty then
                    self.Show("Select files or folders in Explorer to copy.", muteBrush)
                else
                    self.CopyDiskSelectionToPackage roots
            else
                self.TransferPackageSelectionToDisk false

    member private self.FnMove(_: obj, _: RoutedEventArgs) =
        if self.AcceptFn() then
            if self.FnSourceIsExplorer() then
                let roots = self.SelectedDiskRoots()
                if roots.IsEmpty then
                    self.Show("Select files or folders in Explorer to move.", muteBrush)
                else
                    self.CopyDiskSelectionToPackage(roots, deleteSources = true)
            else
                self.TransferPackageSelectionToDisk true

    member private self.FnMkdir(_: obj, _: RoutedEventArgs) =
        if self.AcceptFn() then
            if self.FnSourceIsExplorer() then
                self.MkdirDiskFolder(null, null)
            else
                self.MkdirPackageFolder(null, null)

    member private self.FnDelete(_: obj, _: RoutedEventArgs) =
        if self.AcceptFn() then
            if self.FnSourceIsExplorer() then
                self.DeleteSelectedDiskItems(null, null)
            else
                self.DeleteSelectedFiles(null, null)

    member private self.FnQuit(_: obj, _: RoutedEventArgs) =
        if self.AcceptFn() then
            self.Close()

    member private self.MainWindowKeyDown(_: obj, e: KeyEventArgs) =
        if self.IsTypingFocus() then
            ()
        else
            let alt = e.KeyModifiers.HasFlag KeyModifiers.Alt
            match e.Key with
            | Key.F1 when not alt ->
                self.FnInfo(null, null)
                e.Handled <- true
            | Key.F5 when not alt ->
                self.FnCopy(null, null)
                e.Handled <- true
            | Key.F6 when not alt ->
                self.FnMove(null, null)
                e.Handled <- true
            | Key.F7 when not alt ->
                self.FnMkdir(null, null)
                e.Handled <- true
            | Key.F8 when not alt ->
                self.FnDelete(null, null)
                e.Handled <- true
            | Key.F4 when alt ->
                self.FnQuit(null, null)
                e.Handled <- true
            | _ -> ()

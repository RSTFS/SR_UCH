# SR_UCH 契约面（自动生成）

> 由 `build/gen-contract-doc.ps1` 从 `SR_UCH/Contracts/*.cs` 生成，**不要手工编辑**。
> 契约破坏性版本 `SRContract.Version = 2`；门面 API 等级 `ApiLevel = 4`。

## 文件与公开类型

### `Contracts.cs`

- 类型：`MainThread`、`Slots`、`SRContract`、`SrTimeouts`、`Features`
- 静态成员：`Assert`、`Describe`、`Enable`、`Find`、`FromType`、`Provide`、`Rebuild`、`Register`、`Retract`

### `SRApi.cs`

- 类型：`SRApi`、`TableDragWatchdogFn`、`TableWheelInputFn`、`TableScrollbarInputFn`、`TableAutoHeightFn`、`TableActionButtonsFn`、`TableColMoveFn`、`TableScrollbarFn`、`TableAutoFitFn`、`TableAfterDrawFn`、`TableScrollbarForFn`、`RowFilterFn`、`RowCompanionFn`、`RowSliderFn`、`RowComboWidthHook`、`RowEnumFilterHook`
- 静态成员：`_blockInput`、`_blockWindowDrag`、`_canControl`、`_gateHost`、`_gateMaster`、`_hasServer`、`_uiOpen`、`_wheelFrame`、`CanDoHost`、`Cat`、`ClearableTextField`、`ComboKeyDown`、`ComboKeyHeld`、`Diag`、`DrawFill`、`DrawLine`、`EnumNameZh`、`FeatureAllowed`、`FlushConfig`、`ForceResetUiState`、`GateModeAllows`、`HasPage`、`HotkeyAction`、`KeyComboMod`、`LocDesc`、`LocDynamic`、`LocEnum`、`LocKey`、`LocPair`、`LocSec`、`MarkWheelUsed`、`MissingSinks`、`Nav`、`NavHide`、`NotifyT`、`NotifyTag`、`OnShutdown`、`Owners`、`RegisterCameraView`、`RegisterDismissHook`、`RegisterFooterButton`、`RegisterHostOverride`、`RegisterInputGateHold`、`RegisterKey`、`RegisterModeOverride`、`RegisterPage`、`RegisterPageChrome`、`RegisterPauseProvider`、`RegisterSceneHook`、`RegisterTabPrefix`、`RegisterTick`、`RegisterTypingProvider`、`RegisterWheelHook`、`RenderSectionEntries`、`SectionBegin`、`SectionEnd`、`SectionEntries`、`SelectedPageId`、`SetDefaultCombo`、`SetExSinks`、`SetExtSinks`、`SetFeatureAllowedSink`、`SetGateSinks`、`SetKeyActionSink`、`SetLangSinks`、`SetLocDynamicSink`、`SetMiscSinks`、`SetStateSinks`、`SetTableActionButtonsSink`、`SetTableActionColumnWidthSink`、`SetTableAfterDrawSink`、`SetTableAutoFitSink`、`SetTableAutoHeightSink`、`SetTableColMoveSink`、`SetTableScrollbarForSink`、`SetTableScrollbarSink`、`SetTableSinks`、`SetTypingSink`、`SetUiSinks`、`SinkCount`、`SinkTotal`、`T`、`TableActionButtons`、`TableActionColumnWidth`、`TableAfterDraw`、`TableAutoFit`、`TableAutoHeight`、`TableColMove`、`TableDragWatchdog`、`TableDraw`、`TableScrollbar`、`TableScrollbarFor`、`TableScrollbarInput`、`TableWheelInput`、`TakeWheelEvent`、`WireSection`

### `SRApi.Ctl.cs`

- 类型：`SRApi`、`Ctl`、`ComboBoxFn`、`TableHeightGripFn`、`HotkeyButtonNoHintFn`、`SelfToggleButtonFn`、`ColorEditFn`、`ColorSwatchFn`、`ColorPanelFn`、`ColorResetFn`
- 静态成员：`AddFillHandler`、`Button`、`Checkbox`、`CleanTip`、`Color`、`ColorEdit`、`ColorPanel`、`ColorSwatch`、`ComboBox`、`Content`、`Disabled`、`DrawEntryRow`、`DrawSlider`、`FillScrollAll`、`FilterCombo`、`FindEntry`、`HotkeyActionButton`、`HotkeyButton`、`HotkeyButtonNoHint`、`LabelAuto`、`LabelColumnWidth`、`LabeledRow`、`ListTableHeight`、`MarkConfigDirty`、`NoteEntryBound`、`PreviewColor`、`RegisterFillScroll`、`RenderControl`、`RenderEntryRow`、`ResetColor`、`RestoreLabel`、`Row`、`RowCombo`、`RowSlider`、`Sc`、`ScrollMaxY`、`ScrollRowsH`、`ScrollToBottom`、`SectionEntries`、`SectionTitle`、`SelfToggleButton`、`SetButtonSink`、`SetCheckboxSink`、`SetCheckStylesSink`、`SetFillScrollSink`、`SetForceZhSink`、`SetHotkeyButtonNoHintSink`、`SetLabelColumnSink`、`SetLabeledRowSink`、`SetRowComboSink`、`SetRowSliderSink`、`SetSelfToggleButtonSink`、`SetSelItemSink`、`SetSinks`、`SetTableHeightGripSink`、`SetTableHighlight`、`SetTableHighlightSink`、`SetValue`、`SetWheelFallbackSink`、`SetWheelPollSink`、`SidebarWidth`、`Style`、`TableAfterDraw`、`TableAutoFit`、`TableAutoHeight`、`TableColumnWidths`、`TableFitHeight`、`TableGripGap`、`TableGripH`、`TableHeaderH`、`TableHeightGrip`、`TableHeightGripBar`、`TableMaxScrollY`、`TableMinH`、`TableRowH`、`TableRowsH`、`TableScrollbarFor`、`TableScrollbarW`、`TextHeight`、`Tint`、`WheelFallback`、`WheelPoll`、`WheelTake`、`WireColorPicker`、`WireSection`

### `SRApi.Misc.cs`

- 类型：`SRApi`、`SlotKeys`、`Migration`、`Env`、`ColorHex`、`Cache`、`Ref`、`NetMagic`、`Sinks`、`Guard`
- 静态成员：`Clear`、`Conflicts`、`Field`、`FromColor32`、`Log`、`Method`、`MissingStates`、`Move`、`Override`、`ParseHex`、`Recent`、`Register`、`Remove`、`SetSinks`、`State`、`ToColor32`、`ToHex`、`Try`

### `SRApi.Notify.cs`

- 类型：`SRApi`、`NotifyExFn`
- 静态成员：`CloseMenu`、`DispatchPageWheel`、`DrawPanel`、`LogError`、`LogInfo`、`LogWarn`、`Notify`、`NotifyEx`、`OpenMenu`、`RegisterOverlay`、`RegisterPageWheel`、`SetCloseMenuSink`、`SetLogSinks`、`SetNotifyExSink`、`SetNotifySink`、`SetOpenMenuSink`、`SetOverlaySink`、`SetRoundedSink`、`SetWheelLayerSink`、`SetWheelTakeSink`、`Warn`、`WheelArea`、`WheelTake`

### `SRApi.Storage.cs`

- 类型：`SRApi`、`Storage`
- 静态成员：`BackupPath`、`Delete`、`DirOf`、`Exists`、`IsNullOrEmpty`、`PathOf`、`Read`、`ReadPath`、`SetRoot`、`WriteAtomic`、`WriteAtomicPath`

### `SRApi.Table.cs`

- 类型：`SRApi`
- 静态成员：`Create`

## 使用约定

- 功能层只允许使用契约层 `public` 成员；白名单由 `guards-text.ps1` 从契约源码自动生成（`build/api_surface.txt` 仅作显式登记/禁用）。
- 新增 public API 时 `SRContract.ApiLevel` +1；删除或改签名属于破坏性变更，必须抬 `SRContract.Version`。
- 槽位名从 `SRApi.SlotKeys` 取；网络魔数用 `SRApi.NetMagic.Register` 登记，自检会检查唯一性。

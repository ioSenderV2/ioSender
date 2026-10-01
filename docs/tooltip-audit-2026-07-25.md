# Tooltip Audit — Missing Tooltips

Generated 2026-07-25. Scanned 141 XAML files, 976 interactive controls (Button, ToggleButton, RepeatButton, CheckBox, RadioButton, DataGrid + column headers, TextBox, NumericField, ComboBox, TabItem, GroupBox, Expander, Slider, ToggleControl).

- **OWN tooltip**: 364
- **INHERITED** (a containing panel/tab/group has one, so hover still shows *something*, just not control-specific): 63
- **MISSING** (no tooltip anywhere in the ancestor chain — pure silence on hover): 549

Caveat: a handful of controls (~14, in FeedsAndSpeedsView, ThreadingWizard, JogBaseControl, ProbingView, KeyMapEditor, OffsetView, RenderControl, PortDialog) get their ToolTip via a Style Setter, mostly for validation-error display — those are flagged MISSING below since the scanner checks per-element attributes only, but they're not meaningfully 'silent' controls, just a different wiring path. Worth a manual glance at those 8 files before treating their counts as gospel.

## By file

### CNC Controls Camera\CNC Controls Camera\CameraControl.xaml (5)
- **ComboBox** — Uid=cbxCamera Name=cbxCamera
- **Slider** — Uid=sldcircle Name=sldcircle
- **Button** — Uid=btn_moveOffset Name=btnMove Content=Move offset
- **CheckBox** — Uid=lbl_cameraToSpindlePosition Name=chkCameraToSpindlePosition Content=camera to spindle
- **Button** — Uid=btn_useAsProbe Name=btnUseAsProbe Content=Use as probe position

### CNC Controls Camera\CNC Controls Camera\ConfigControl.xaml (9)
- **GroupBox** — Uid=grp_camera Name=grpCamera Header=Camera
- **ComboBox** — Uid=cbxDevice Name=cbxDevice
- **Button** — Uid=btn_cameraConnect Name=btnCameraConnect Content=Connect
- **CheckBox** — Uid=chk_moveToSpindle Content=Inital move to spindle
- **CheckBox** — Uid=chk_confirmMove Content=Confirm move
- **NumericField** — Uid=fld_xOffset Name=xOffset
- **NumericField** — Uid=fld_yOffset Name=yOffset
- **ComboBox** — Uid=comboBox Name=comboBox
- **Button** — Uid=btn_getPosition Name=getPosition Content=Get current position

### CNC Controls Dragknife\DragKnifeDialog.xaml (11)
- **GroupBox** — Uid=grp_knife Header=Knife
- **NumericField** — Uid=fld_tipOffset
- **NumericField** — Uid=fld_cutDepth
- **NumericField** — Uid=fld_swivelAngle
- **NumericField** — Uid=fld_dentLength
- **GroupBox** — Uid=grp_retract Header=Retract
- **CheckBox** — Uid=lbl_retractEnable Content=Retract enable
- **NumericField** — Uid=fld_retractAngle
- **NumericField** — Uid=fld_retractDepth
- **Button** — Uid=btn_ok Name=btnOk Content=Ok
- **Button** — Uid=btn_cancel Name=btnCancel Content=Cancel

### CNC Controls Lathe\CNC Controls Lathe\ConfigControl.xaml (5)
- **GroupBox** — Name=grpLathe Header=Lathe
- **ComboBox**
- **ComboBox**
- **NumericField**
- **NumericField**

### CNC Controls Lathe\CNC Controls Lathe\CssControl.xaml (3)
- **CheckBox** — Uid=chkCSS Name=chkCSS Content=CSS (Constand Surface Speed)
- **NumericField** — Name=data
- **ComboBox** — Uid=cbxSpindleDir Name=cbxSpindleDir

### CNC Controls Lathe\CNC Controls Lathe\FacingWizard.xaml (12)
- **NumericField** — Name=cvStart
- **NumericField** — Name=cvTarget
- **NumericField** — Name=cvClearanceZ
- **GroupBox** — Name=groupBox Header=Diameter
- **NumericField** — Name=cvTargetX
- **NumericField** — Name=cvClearanceX
- **GroupBox** — Name=groupBox2 Header=Cut depths and feed rates
- **NumericField** — Name=cvFeedRate
- **NumericField** — Name=cvPassDepthLast
- **NumericField** — Name=cvFeedRateLast
- **Button** — Uid=btnCalculate Name=btnCalculate Content=Calculate
- **TextBox** — Uid=txtGCode Name=txtGCode

### CNC Controls Lathe\CNC Controls Lathe\LatheWizardsView.xaml (4)
- **TabItem** — Uid=tab_turning Name=tabTurning Header=Turning
- **TabItem** — Uid=tab_parting Name=tabParting Header=Parting
- **TabItem** — Uid=tab_facing Name=tabFacing Header=Facing
- **TabItem** — Uid=tab_threading Name=tabThreading Header=Threading

### CNC Controls Lathe\CNC Controls Lathe\PartingWizard.xaml (10)
- **NumericField** — Name=cvStart
- **GroupBox** — Name=groupBox Header=Diameter
- **NumericField** — Name=cvTargetX
- **NumericField** — Name=cvClearanceX
- **GroupBox** — Name=groupBox2 Header=Cut depths and feed rates
- **NumericField** — Name=cvFeedRate
- **NumericField** — Name=cvPassDepthLast
- **NumericField** — Name=cvFeedRateLast
- **Button** — Uid=btnCalculate Name=btnCalculate Content=Calculate
- **TextBox** — Uid=txtGCode Name=txtGCode

### CNC Controls Lathe\CNC Controls Lathe\ProfileControl.xaml (2)
- **ComboBox** — Uid=cbxProfile Name=cbxProfile
- **Button** — Uid=btnAddProfile Name=btnAddProfile

### CNC Controls Lathe\CNC Controls Lathe\ProfileDialog.xaml (18)
- **ComboBox** — Uid=cbxProfile Name=cbxProfile
- **Button** — Uid=btnAddProfile Name=btnAddProfile Content=Add
- **GroupBox** — Name=grpCutDepths Header=Cut depths
- **NumericField** — Name=cvFirstCut
- **NumericField** — Name=cvFeedRate
- **NumericField** — Name=cvLastCut
- **NumericField** — Name=cvFeedRateLast
- **NumericField** — Name=cvMinCut
- **GroupBox** — Name=grpClearance Header=Clearance
- **NumericField** — Name=cvXClearance
- **NumericField** — Name=cvZClearance
- **NumericField** — Name=cvRPM
- **NumericField** — Name=cvCSSMaxRPM
- **GroupBox** — Name=grpXMode Header=X-axis
- **RadioButton** — Uid=btnRadius Name=btnRadius Content=Radius mode
- **RadioButton** — Uid=btnDiameter Name=btnDiameter Content=Diameter mode
- **Button** — Uid=btnOk Name=btnOk Content=Ok
- **Button** — Uid=btnCancel Name=btnCancel Content=Cancel

### CNC Controls Lathe\CNC Controls Lathe\SpringPassControl.xaml (2)
- **CheckBox** — Uid=chkSpringPasses Name=chkSpringPasses Content=Spring passes:
- **NumericField** — Name=data

### CNC Controls Lathe\CNC Controls Lathe\TaperControl.xaml (2)
- **CheckBox** — Uid=chkTaper Name=chkTaper Content=Taper:
- **NumericField** — Name=data

### CNC Controls Lathe\CNC Controls Lathe\ThreadingWizard.xaml (37)
- **ComboBox** — Uid=cbxThreadType Name=cbxThreadType
- **ComboBox** — Uid=cbxThreadSize Name=cbxThreadSize
- **RadioButton** — Uid=btnOutside Name=btnOutside Content=Outside
- **RadioButton** — Uid=btnInside Name=btnInside Content=Inside
- **GroupBox** — Header=Dimensions
- **NumericField** — Name=cvLead
- **NumericField** — Name=cvStarts
- **NumericField** — Name=cvSize
- **NumericField** — Name=cvTPI
- **NumericField** — Name=cvZStart
- **NumericField** — Name=cvLength
- **GroupBox** — Header=Tool
- **RadioButton** — Uid=btnChamfer Name=btnChamfer Content=Chamfer a
- **RadioButton** — Uid=btnRadius Name=btnRadius Content=Radius r
- **NumericField** — Name=cvTool
- **NumericField** — Name=cvTooltipMax
- **NumericField** — Name=cvTooltipMin
- **NumericField** — Name=cvAngle
- **GroupBox** — Header=Thread values
- **NumericField** — Name=txtOutsideTol
- **NumericField** — Name=txtPitchTol
- **NumericField** — Name=cvMaxDiameter
- **NumericField** — Name=cvMPos
- **NumericField** — Name=cvTaper
- **GroupBox** — Name=grpOptionsLinuxCNC Header=Options: linuxCNC
- **ComboBox** — Uid=cbxTapertype Name=cbxTapertype
- **GroupBox** — Name=grpOptionsMach3 Header=Options: Mach3
- **NumericField** — Name=cvRetract
- **NumericField** — Name=cvPasses
- **NumericField** — Name=cvPassesExecuted
- **GroupBox** — Header=Cut
- **NumericField** — Name=cvCutDepth
- **NumericField** — Name=cvSpindleRPM
- **ComboBox** — Uid=cbxSpindleDir Name=cbxSpindleDir
- **Button** — Uid=btnCalculate Name=btnCalculate Content=Calculate
- **TextBox** — Uid=txtPasses Name=txtPasses
- **TextBox** — Uid=txtGCode Name=txtGCode

### CNC Controls Lathe\CNC Controls Lathe\TurningWizard.xaml (11)
- **NumericField** — Name=cvStart
- **NumericField** — Name=cvLength
- **GroupBox** — Name=groupBox Header=Diameter
- **NumericField** — Name=cvTargetX
- **NumericField** — Name=cvClearanceX
- **GroupBox** — Name=groupBox2 Header=Cut depths and feed rates
- **NumericField** — Name=cvFeedRate
- **NumericField** — Name=cvPassDepthLast
- **NumericField** — Name=cvFeedRateLast
- **Button** — Uid=btnCalculate Name=btnCalculate Content=Calculate
- **TextBox** — Uid=txtGCode Name=txtGCode

### CNC Controls Probing\CNC Controls Probing\CenterFinderControl.xaml (8)
- **GroupBox** — Uid=grp_dimensions Header=Workpiece dimensions
- **NumericField** — Uid=fld_xSize
- **NumericField** — Uid=fld_ySize
- **CheckBox** — Uid=lbl_preview Content=Preview
- **Button** — Uid=btn_start Content=Start
- **Button** — Uid=btn_stop Content=Stop
- **Button** — Uid=btn_useCamera Content=Use camera positions
- **TextBox**

### CNC Controls Probing\CNC Controls Probing\ConfigControl.xaml (1)
- **GroupBox** — Uid=grp_probeConfig Header=Probing

### CNC Controls Probing\CNC Controls Probing\CsSelectControl.xaml (6)
- **GroupBox** — Uid=grp_action Header=Action
- **RadioButton** — Uid=set_coord Content=Set coordinate system:
- **ComboBox** — Uid=cbxOffset Name=cbxOffset
- **RadioButton** — Uid=btn_setG92 Content=Set offset (G92)
- **Button** — Uid=btn_clear Content=Clear
- **RadioButton** — Uid=btn_setMeasure Content=Measure

### CNC Controls Probing\CNC Controls Probing\EdgeFinderControl.xaml (5)
- **CheckBox** — Uid=lbl_probeZ Content=Probe Z
- **CheckBox** — Uid=lbl_preview Content=Preview
- **Button** — Uid=btn_start Content=Start
- **Button** — Uid=btn_stop Content=Stop
- **TextBox**

### CNC Controls Probing\CNC Controls Probing\EdgeFinderIntControl.xaml (5)
- **CheckBox** — Uid=lbl_probeZ Content=Probe Z
- **CheckBox** — Uid=lbl_preview Content=Preview
- **Button** — Uid=btn_start Content=Start
- **Button** — Uid=btn_stop Content=Stop
- **TextBox**

### CNC Controls Probing\CNC Controls Probing\HeightMapControl.xaml (18)
- **GroupBox** — Uid=grp_probeArea Header=Area to probe
- **NumericField**
- **NumericField** — Uid=lbl_width
- **NumericField**
- **NumericField** — Uid=lbl_height
- **Button** — Uid=btn_fromLimits Content=Set from program limits
- **GroupBox** — Uid=grp_gridSize Header=Gridsize
- **NumericField**
- **NumericField**
- **CheckBox** — Uid=lbl_lock Content=Lock
- **CheckBox** — Uid=lbl_pause Content=Pause before probing
- **CheckBox** — Uid=lbl_setZ0AtX0Y0 Content=Set Z = 0 at X0Y0:
- **Button** — Uid=btn_start Content=Start
- **Button** — Uid=btn_probe Content=_Probe
- **Button** — Uid=btn_stop Content=Stop
- **Button** — Uid=btn_load Content=_Load
- **Button** — Uid=btn_save Content=_Save
- **Button** — Uid=btn_apply Content=_Apply

### CNC Controls Probing\CNC Controls Probing\MacroDialog.xaml (3)
- **ComboBox** — Uid=cbxMacro Name=cbxMacro
- **CheckBox** — Uid=chk_runonce Content=Run once
- **Button** — Uid=btn_close Content=Close

### CNC Controls Probing\CNC Controls Probing\ProbingView.xaml (5)
- **ComboBox** — Uid=cbxProbe Name=cbxProbe
- **TabItem** — Uid=tab_toolOffset Name=tabToolOffset Header=Tool length offset
- **TabItem** — Uid=tab_edgeInternal Name=tabEdgeExternal Header=Edge finder, external
- **TabItem** — Uid=tab_edgeExternal Name=tabEdgeInternal Header=Edge finder, internal
- **TabItem** — Uid=tab_Center Name=tabCenter Header=Center finder

### CNC Controls Probing\CNC Controls Probing\RotationControl.xaml (5)
- **CheckBox** — Uid=lbl_preview Content=Preview
- **Button** — Uid=btn_start Content=Start
- **Button** — Uid=btn_stop Content=Stop
- **Button** — Uid=btn_apply Content=_Apply
- **TextBox**

### CNC Controls Probing\CNC Controls Probing\StartJobControl.xaml (11)
- **GroupBox** — Uid=grp_lsstock Header=Stock (approximate)
- **NumericField** — Uid=lbl_lswidth
- **NumericField** — Uid=lbl_lsheight
- **NumericField** — Uid=lbl_lswpheight
- **CheckBox** — Uid=lbl_lsProbeZ Content=Probe Z (top surface)
- **CheckBox** — Uid=lbl_lsPreview Content=Preview
- **GroupBox** — Uid=grp_lsresult Header=Result
- **GroupBox** — Header=Preview
- **TextBox**
- **Button** — Uid=btn_lsstart Content=Start
- **Button** — Uid=btn_lsstop Content=Stop

### CNC Controls Probing\CNC Controls Probing\ToolLengthControl.xaml (7)
- **CheckBox** — Uid=lbl_probeFixture Content=Probe fixture @ G59.3
- **CheckBox** — Uid=lbl_setRefernecOffset Content=Establish reference offset
- **Button** — Uid=lbl_clearToolOffset Content=Clear tool length offset
- **GroupBox** — Uid=grp_fixtureAction Header=Fixture
- **CheckBox** — Uid=lbl_setCoordOrOffset Content=Set coordinate system or offset
- **Button** — Uid=btn_start Content=Start
- **Button** — Uid=btn_stop Content=Stop

### CNC Controls\CNC Controls\About.xaml (3)
- **GroupBox** — Name=grpGrbl Header=grbl
- **TextBox**
- **Button** — Uid=btn_ok Name=btnOk Content=Ok

### CNC Controls\CNC Controls\AppMessageBox.xaml (4)
- **Button** — Name=btnYes Content=Yes
- **Button** — Name=btnNo Content=No
- **Button** — Name=btnOk Content=OK
- **Button** — Name=btnCancel Content=Cancel

### CNC Controls\CNC Controls\AutoSquareWizard.xaml (7)
- **NumericField** — Uid=fld_asoffset
- **NumericField** — Uid=fld_asbit
- **NumericField** — Uid=fld_asdepth
- **NumericField** — Uid=fld_aspeck
- **NumericField** — Uid=fld_asplunge
- **NumericField** — Uid=fld_asrpm
- **NumericField** — Uid=fld_assafez

### CNC Controls\CNC Controls\BasicConfigControl.xaml (3)
- **GroupBox** — Uid=grb_mainConfig Name=grpBasic Header=Main
- **Slider** — Uid=sld_uiScale Name=sliderUiScale
- **Button** — Uid=btn_aiKey Name=btnAiKey Content=Set AI key

### CNC Controls\CNC Controls\CoordValueSetControl.xaml (2)
- **NumericField** — Name=cvValue
- **Button** — Uid=btn_set Name=btnSet Content=Set

### CNC Controls\CNC Controls\DROBaseControl.xaml (1)
- **Button** — Uid=btnScaled Name=btnScaled

### CNC Controls\CNC Controls\FeedsAndSpeedsView.xaml (21)
- **TabItem** — Uid=tab_fsIntro Name=tabIntro Header=Intro
- **TabItem** — Uid=tab_fsLoad Name=tabLoad Header=Load / Import
- **Button** — Uid=btn_fsLoadLatest Content=Load latest export
- **ComboBox** — Name=cbxMaterial
- **TextBox** — Name=txtLoadStatus
- **TabItem** — Uid=tab_fsResults Name=tabResults Header=Results
- **Button** — Uid=btn_fsAskAi Name=btnAskAi Content=Ask AI to review
- **ComboBox** — Name=cbxAiModel
- **Button** — Uid=btn_fsSelectAllAi Name=btnSelectAllAi Content=Prefer AI for all
- **Button** — Uid=btn_fsWriteApply Content=Write apply file
- **DataGrid** — Name=dgrResults
- **DataGridTextColumn** — Uid=col_fsOperation Header=Operation
- **DataGridTextColumn** — Uid=col_fsParameter Header=Parameter
- **DataGridTextColumn** — Uid=col_fsCurrent Header=Current
- **DataGridTextColumn** — Uid=col_fsRecommended Header=Recommended
- **DataGridTextColumn** — Uid=col_fsMachineLimit Header=Machine limit
- **DataGridTextColumn** — Uid=col_fsVerdict Header=Verdict
- **DataGridTextColumn** — Uid=col_fsAiRecommended Name=colAiRecommended Header=AI Says
- **DataGridCheckBoxColumn** — Uid=col_fsPreferAi Name=colPreferAi Header=Prefer AI
- **DataGridTextColumn** — Uid=col_fsNotes Header=Notes
- **TextBox** — Name=txtStatus

### CNC Controls\CNC Controls\FixtureEditDialog.xaml (5)
- **Button** — Uid=fxd_ok Name=btnOk Content=OK
- **Button** — Uid=fxd_cancel Name=btnCancel Content=Cancel
- **TextBox** — Uid=txtFxName Name=txtName
- **ComboBox** — Uid=cbxKind Name=cbxKind
- **RadioButton** — Uid=fxd_rbProbe3d Name=rbFxProbe3d Content=3D Probe

### CNC Controls\CNC Controls\GCodeListControl.xaml (5)
- **DataGrid** — Uid=grid_gcode Name=grdGCode
- **DataGridTextColumn** — Uid=hdr_block Header=Block
- **DataGridTextColumn**
- **DataGridTextColumn** — Uid=hdr_data Header=Data
- **Expander**

### CNC Controls\CNC Controls\GCodeRotateDialog.xaml (3)
- **NumericField** — Uid=fld_angle Name=Angle
- **Button** — Uid=btn_ok Name=btnOk Content=Ok
- **Button** — Uid=btn_cancel Name=btnCancel Content=Cancel

### CNC Controls\CNC Controls\GCodeWrapDialog.xaml (5)
- **NumericField** — Uid=fld_diameter Name=Diameter
- **ComboBox**
- **ComboBox**
- **Button** — Uid=btn_ok Name=btnOk Content=Ok
- **Button** — Uid=btn_cancel Name=btnCancel Content=Cancel

### CNC Controls\CNC Controls\GotoBaseControl.xaml (1)
- **ComboBox**

### CNC Controls\CNC Controls\GotoControl.xaml (1)
- **GroupBox** — Uid=grp_goto Name=grpGoto Header=Goto

### CNC Controls\CNC Controls\GotoFlyoutControl.xaml (1)
- **Button** — Content=×

### CNC Controls\CNC Controls\GrblAlarmList.xaml (3)
- **DataGrid** — Uid=dgrAlarms Name=dgrAlarms
- **DataGridTextColumn** — Uid=hdr_alarmCode Header=Code
- **DataGridTextColumn** — Uid=hdr_alarmMessage Header=Message

### CNC Controls\CNC Controls\GrblConfigControl.xaml (6)
- **DataGrid** — Uid=dgrSettings Name=dgrSettings
- **DataGridTextColumn** — Uid=hdr_settingId Header=Id
- **DataGridTextColumn** — Uid=hdr_settingValue Header=Value
- **DataGridTextColumn** — Uid=hdr_settingUnit Header=Unit
- **DataGridTextColumn** — Uid=hdr_settingName Header=Name
- **TextBox** — Uid=txtDescription Name=txtDescription

### CNC Controls\CNC Controls\GrblConfigView.xaml (1)
- **Button** — Uid=btn_saveSettings Name=btnSave Content=Save settings

### CNC Controls\CNC Controls\GrblErrorList.xaml (3)
- **DataGrid** — Uid=dgrErrors Name=dgrErrors
- **DataGridTextColumn** — Uid=hdr_errorCode Header=Code
- **DataGridTextColumn** — Uid=hdr_errorMessage Header=Message

### CNC Controls\CNC Controls\JobControl.xaml (1)
- **Button** — Uid=btn_rewind Name=btnRewind Content=Rewind

### CNC Controls\CNC Controls\JogBaseControl.xaml (12)
- **GroupBox** — Uid=grp_jogDistance Header=Distance
- **RadioButton** — Content={Binding Path=JogData.Distance3, RelativeSource={RelativeSource AncestorType=UserControl}}
- **RadioButton** — Content={Binding Path=JogData.Distance2, RelativeSource={RelativeSource AncestorType=UserControl}}
- **RadioButton** — Content={Binding Path=JogData.Distance1, RelativeSource={RelativeSource AncestorType=UserControl}}
- **RadioButton** — Content={Binding Path=JogData.Distance0, RelativeSource={RelativeSource AncestorType=UserControl}}
- **RadioButton** — Uid=lbl_jogContinuous Content=Continuous
- **GroupBox** — Uid=grp_jogFeedrate Header=Feed rate
- **RadioButton** — Content={Binding Path=JogData.Feedrate3, RelativeSource={RelativeSource AncestorType=UserControl}}
- **RadioButton** — Content={Binding Path=JogData.Feedrate2, RelativeSource={RelativeSource AncestorType=UserControl}}
- **RadioButton** — Content={Binding Path=JogData.Feedrate1, RelativeSource={RelativeSource AncestorType=UserControl}}
- **RadioButton** — Content={Binding Path=JogData.Feedrate0, RelativeSource={RelativeSource AncestorType=UserControl}}
- **Button**

### CNC Controls\CNC Controls\JogConfigControl.xaml (1)
- **GroupBox** — Uid=grp_kbdJogging Name=grpJog Header=Keyboard jogging

### CNC Controls\CNC Controls\JogFlyoutControl.xaml (1)
- **Button** — Content=×

### CNC Controls\CNC Controls\KbdJogGridControl.xaml (3)
- **GroupBox** — Uid=grp_kbdJogging Header=Keyboard jogging
- **Button** — Uid=s0 Name=s0
- **Button** — Uid=s1 Name=s1

### CNC Controls\CNC Controls\KeyMapEditor.xaml (15)
- **TabItem** — Uid=tab_keyboard Header=Keyboard
- **DataGrid** — Uid=grid Name=grid
- **DataGridTextColumn** — Uid=col_action Header=Action
- **DataGridTemplateColumn** — Uid=col_shortcut Header=Shortcut
- **Button** — Content={Binding DisplayText}
- **Expander**
- **TabItem** — Uid=tab_controller Header=Controller
- **GroupBox** — Uid=grp_buttons Header=Buttons
- **Button** — Uid=btn_restoreDefaults Name=btnRestoreDefaults Content=Restore Defaults
- **DataGrid** — Uid=gridController Name=gridController
- **DataGridTemplateColumn**
- **DataGridTextColumn** — Uid=col_buttonCtrl Header=Button
- **DataGridTemplateColumn** — Uid=col_actionCtrl Header=Action
- **ComboBox**
- **GroupBox** — Uid=grp_analog Header=Analog stick jog (left stick X/Y, triggers Z)

### CNC Controls\CNC Controls\LEDControl.xaml (1)
- **Button** — Uid=btnLED Name=btnLED

### CNC Controls\CNC Controls\LimitsControl.xaml (1)
- **GroupBox** — Uid=grp_programLimits Name=grpLimits Header=Program limits

### CNC Controls\CNC Controls\MachinePositionControl.xaml (7)
- **GroupBox** — Uid=grp_machinePos Name=grpMachinePos Header=Machine Position
- **NumericField**
- **NumericField**
- **NumericField**
- **NumericField**
- **NumericField**
- **NumericField**

### CNC Controls\CNC Controls\MachinePositionFlyout.xaml (7)
- **NumericField**
- **NumericField**
- **NumericField**
- **NumericField**
- **NumericField**
- **NumericField**
- **Button** — Content=×

### CNC Controls\CNC Controls\MacroEditor.xaml (3)
- **TextBox** — Uid=textBox Name=textBox
- **Button** — Uid=btn_ok Content=Ok
- **Button** — Uid=btn_cancel Content=Cancel

### CNC Controls\CNC Controls\MacroExecuteControl.xaml (1)
- **Button**

### CNC Controls\CNC Controls\MacroManagerDialog.xaml (5)
- **DataGrid** — Uid=grdMacros Name=grdMacros
- **DataGridTextColumn** — Uid=col_macroName Header=Name
- **DataGridTemplateColumn** — Uid=col_macroFile Header=File
- **DataGridTemplateColumn** — Uid=col_macroPrompt Header=Prompt
- **DataGridTemplateColumn** — Uid=col_macroKey Header=Key

### CNC Controls\CNC Controls\MacroToolbarControl.xaml (1)
- **Button**

### CNC Controls\CNC Controls\MainPageEditor.xaml (3)
- **TabItem** — Uid=tab_panels Header=Panels
- **TabItem** — Uid=tab_tabs Header=Tabs
- **TabItem** — Uid=tab_unavailable Header=Unavailable

### CNC Controls\CNC Controls\MPGPending.xaml (2)
- **Button** — Uid=btn_mpgContinue Name=btnContinue Content=Continue
- **Button** — Uid=btn_mpgDisconnect Name=btnDisconnect Content=Disconnect

### CNC Controls\CNC Controls\OffsetView.xaml (16)
- **DataGrid** — Uid=dgrOffsets Name=dgrOffsets
- **DataGridTextColumn** — Uid=hdr_offset Header=Offset
- **DataGridTemplateColumn** — Header=X
- **NumericField**
- **DataGridTemplateColumn** — Header=Y
- **NumericField**
- **DataGridTemplateColumn** — Header=Z
- **NumericField**
- **DataGridTemplateColumn** — Header=Clear
- **DataGridTemplateColumn** — Header=Get MPos
- **DataGridTextColumn**
- **DataGridTextColumn**
- **DataGridTextColumn**
- **DataGridTextColumn** — Header=U
- **DataGridTextColumn** — Header=V
- **DataGridTextColumn** — Header=W

### CNC Controls\CNC Controls\OutlineBaseControl.xaml (1)
- **NumericField** — Uid=lbl_feedrate

### CNC Controls\CNC Controls\OutlineControl.xaml (1)
- **GroupBox** — Uid=grp_outline Name=grpOutline Header=Outline

### CNC Controls\CNC Controls\OutlineFlyout.xaml (1)
- **Button** — Content=×

### CNC Controls\CNC Controls\OverrideControl.xaml (3)
- **RepeatButton**
- **RepeatButton**
- **TextBox** — Uid=txtOverride Name=txtOverride

### CNC Controls\CNC Controls\PendingChangesDialog.xaml (8)
- **Button** — Uid=pc_close Content=Close
- **Button** — Uid=pc_ok Name=btnOk Content=Restore settings
- **Button** — Uid=pc_cancel Content=Cancel
- **DataGrid** — Uid=grd Name=grd
- **DataGridTextColumn** — Uid=pc_setting Header=Setting
- **DataGridTextColumn** — Uid=pc_name Header=Name
- **DataGridTextColumn** — Uid=pc_current Header=Current
- **DataGridTextColumn** — Uid=pc_new Header=New

### CNC Controls\CNC Controls\PortDialog.xaml (8)
- **TabItem** — Uid=tab_serial Name=tabSerial Header=Serial
- **ComboBox**
- **ComboBox**
- **ComboBox**
- **TabItem** — Uid=tab_network Name=tabNetwork Header=Network
- **TabItem** — Uid=tab_simulator Name=tabSimulator Header=Simulator
- **Button** — Uid=btn_ok Name=btnOk Content=Ok
- **Button** — Uid=btn_cancel Name=btnCancel Content=Cancel

### CNC Controls\CNC Controls\ProbeDefinitionEditDialog.xaml (4)
- **ComboBox** — Uid=cbxType Name=cbxType
- **Button** — Uid=pd_motion Name=btnMotion Content=Edit motion params…
- **Button** — Uid=pd_ok Name=btnOk Content=OK
- **Button** — Uid=pd_cancel Name=btnCancel Content=Cancel

### CNC Controls\CNC Controls\ProbeDefinitionsDialog.xaml (9)
- **DataGrid** — Uid=grd Name=grd
- **DataGridTextColumn** — Uid=pdl_name Header=Name
- **DataGridTextColumn** — Uid=pdl_type Header=Type
- **DataGridTextColumn** — Uid=pdl_dia Header=Tip
- **DataGridTextColumn** — Uid=pdl_search Header=Approach
- **DataGridTextColumn** — Uid=pdl_latch Header=Re-touch
- **DataGridTextColumn** — Uid=pdl_dist Header=Search dist.
- **DataGridTextColumn** — Uid=pdl_clr Header=Standoff
- **Button** — Uid=pdl_close Name=btnClose Content=Close

### CNC Controls\CNC Controls\ProbeMotionParamsDialog.xaml (1)
- **Button** — Uid=pm_close Name=btnClose Content=Close

### CNC Controls\CNC Controls\ReleasePickerDialog.xaml (3)
- **Button** — Uid=btn_ok Name=btnOk Content=Ok
- **Button** — Uid=btn_cancel Name=btnCancel Content=Cancel
- **ComboBox** — Name=cbxRelease

### CNC Controls\CNC Controls\ResetReproDialog.xaml (6)
- **NumericField** — Uid=fld_reset_repro_dwell Name=Dwell
- **NumericField** — Uid=fld_reset_repro_travel Name=Travel
- **NumericField** — Uid=fld_reset_repro_feed Name=Feed
- **NumericField** — Uid=fld_reset_repro_calls Name=Calls
- **Button** — Uid=btn_reset_repro_generate Name=btnGenerate Content=Generate...
- **Button** — Uid=btn_close Name=btnClose Content=Close

### CNC Controls\CNC Controls\RestorePointDialog.xaml (5)
- **DataGrid** — Uid=dgrSnapshots Name=dgrSnapshots
- **DataGridTextColumn** — Uid=col_rpSaved Header=Saved
- **Button** — Uid=btn_rpBrowse Name=btnBrowse Content=Browse...
- **Button** — Uid=btn_rpRestore Name=btnRestore Content=Restore
- **Button** — Uid=btn_rpCancel Name=btnCancel Content=Cancel

### CNC Controls\CNC Controls\ScenarioNameDialog.xaml (3)
- **Button** — Name=btnOk Content=OK
- **Button** — Name=btnCancel Content=Cancel
- **TextBox** — Name=txtName

### CNC Controls\CNC Controls\SecretPromptDialog.xaml (2)
- **Button** — Name=btnOk Content=OK
- **Button** — Name=btnCancel Content=Cancel

### CNC Controls\CNC Controls\SignalControl.xaml (1)
- **Button** — Uid=btnLED Name=btnLED

### CNC Controls\CNC Controls\SignalsControl.xaml (1)
- **GroupBox** — Name=grpSignals

### CNC Controls\CNC Controls\SimulatorConfigView.xaml (2)
- **ComboBox** — Uid=cbx_simAxes Name=cbxAxes
- **TextBox** — Uid=txt_simPath Name=txtPath

### CNC Controls\CNC Controls\StepperCalibrationProbeWizard.xaml (9)
- **RadioButton** — Uid=rb_scpAxisXY Name=rbAxisXY Content=XY steppers
- **RadioButton** — Uid=rb_scpAxisZ Name=rbAxisZ Content=Z stepper (1-2-3 block)
- **NumericField** — Uid=fld_scpTrueW Name=fldTrueWidth
- **NumericField** — Uid=fld_scpTrueH Name=fldTrueHeight
- **RadioButton** — Uid=rb_scpGaugeMm Name=rbGaugeMm Content=mm
- **RadioButton** — Uid=rb_scpGaugeIn Name=rbGaugeIn Content=in
- **NumericField** — Uid=fld_scpGauge1 Name=fldGauge1
- **NumericField** — Uid=fld_scpGauge2 Name=fldGauge2
- **NumericField** — Uid=fld_scpGauge3 Name=fldGauge3

### CNC Controls\CNC Controls\StepperCalibrationScratchWizard.xaml (19)
- **NumericField** — Uid=fld_sccurrent
- **NumericField** — Uid=fld_scspan
- **NumericField** — Uid=fld_scdelta
- **NumericField** — Uid=fld_scpoints
- **NumericField** — Uid=fld_scdepth
- **NumericField** — Uid=fld_scplunge
- **NumericField** — Uid=fld_scfeed
- **NumericField** — Uid=fld_scsafez
- **NumericField** — Uid=fld_sclinelen
- **NumericField** — Uid=fld_scrowspacing
- **NumericField** — Uid=fld_scmargin
- **NumericField** — Uid=fld_scrpm
- **NumericField** — Uid=fld_scnewres
- **DataGrid** — Uid=dgResults Name=dgResults
- **DataGridTextColumn** — Uid=col_scPair Header=Pair
- **DataGridTextColumn** — Uid=col_scCandidate Header=Candidate steps/mm
- **DataGridTextColumn** — Uid=col_scCommanded Header=Commanded (mm)
- **DataGridTextColumn** — Uid=col_scMeasured Header=Measured (mm)
- **DataGridTextColumn** — Uid=col_scImplied Header=Implied steps/mm

### CNC Controls\CNC Controls\StepperCalibrationWizard.xaml (4)
- **NumericField** — Uid=fld_distance
- **NumericField** — Uid=fld_measured
- **NumericField** — Uid=fld_resolution
- **TextBox** — Uid=txtInstructions Name=txtInstructions

### CNC Controls\CNC Controls\SurfaceSpoilboardWizard.xaml (12)
- **NumericField** — Uid=fld_sbdiameter
- **NumericField** — Uid=fld_sbmaxrpm
- **NumericField** — Uid=fld_sbrpm
- **NumericField** — Uid=fld_sboverlap
- **NumericField** — Uid=fld_sbstepover
- **NumericField** — Uid=fld_sbfeed
- **NumericField** — Uid=fld_sbplunge
- **NumericField** — Uid=fld_sbdoc
- **NumericField** — Uid=fld_sbtotal
- **NumericField** — Uid=fld_sbsafez
- **NumericField** — Uid=fld_sbwidth
- **NumericField** — Uid=fld_sbheight

### CNC Controls\CNC Controls\THCMonitorControl.xaml (1)
- **Button** — Content=×

### CNC Controls\CNC Controls\ToggleControl.xaml (1)
- **ToggleButton** — Uid=tsw Name=tsw

### CNC Controls\CNC Controls\ToolView.xaml (7)
- **DataGrid** — Uid=grd_tools Name=dgrTools
- **DataGridTextColumn** — Uid=hdr_tool Header=Tool
- **DataGridTextColumn** — Header=X
- **DataGridTextColumn** — Header=Y
- **DataGridTextColumn** — Header=Z
- **DataGridTextColumn** — Header=Name
- **TextBox** — Uid=txtTool Name=txtTool

### CNC Controls\CNC Controls\TrinamicView.xaml (4)
- **GroupBox** — Uid=grp_stallGuard Header=stallGuard
- **Slider**
- **TextBox**
- **TextBox**

### CNC Controls\CNC Controls\UIJoggingControl.xaml (1)
- **CheckBox** — Content=Continuous

### CNC Controls\CNC Controls\UIJogGridControl.xaml (10)
- **GroupBox** — Uid=grp_uiJogging Header=UI Jogging
- **Button** — Uid=d0 Name=d0 Content={Binding JogUiMetric.Distance0, Source={x:Static local:AppConfig.Settings}}
- **Button** — Uid=d1 Name=d1 Content={Binding JogUiMetric.Distance1, Source={x:Static local:AppConfig.Settings}}
- **Button** — Uid=d2 Name=d2 Content={Binding JogUiMetric.Distance2, Source={x:Static local:AppConfig.Settings}}
- **Button** — Uid=d3 Name=d3 Content={Binding JogUiMetric.Distance3, Source={x:Static local:AppConfig.Settings}}
- **Button** — Uid=f0 Name=f0 Content={Binding JogUiMetric.Feedrate0, Source={x:Static local:AppConfig.Settings}}
- **Button** — Uid=f1 Name=f1 Content={Binding JogUiMetric.Feedrate1, Source={x:Static local:AppConfig.Settings}}
- **Button** — Uid=f2 Name=f2 Content={Binding JogUiMetric.Feedrate2, Source={x:Static local:AppConfig.Settings}}
- **Button** — Uid=f3 Name=f3 Content={Binding JogUiMetric.Feedrate3, Source={x:Static local:AppConfig.Settings}}
- **CheckBox** — Uid=chk_continuous Content=Continuous

### CNC Controls\CNC Controls\WorkParametersControl.xaml (3)
- **GroupBox** — Uid=grp_workparams Name=grpWorkParams Header=Work Parameters
- **ComboBox** — Uid=cbxOffset Name=cbxOffset
- **ComboBox** — Uid=cbxProbe Name=cbxProbe

### CNC Converters\JobParametersDialog.xaml (13)
- **GroupBox** — Uid=grp_zPosition Header=Z positions (relative to workpiece surface)
- **NumericField** — Uid=fld_toolChange
- **NumericField** — Uid=fld_rapids
- **NumericField** — Uid=fld_cutDepth
- **GroupBox** — Uid=grp_spindle_feed Header=Spindle and feed
- **NumericField** — Uid=fld_rpm
- **NumericField** — Uid=fld_feedrate
- **NumericField** — Uid=fld_plungeRate
- **GroupBox** — Uid=grp_scalingFactors Header=Scaling factors
- **GroupBox** — Uid=grp_tool Header=Tool
- **NumericField**
- **Button** — Uid=btn_ok Name=btnOk Content=Ok
- **Button** — Uid=bn_cancel Name=btnCancel Content=Cancel

### CNC GCodeViewer\CNC GCodeViewer\CarveView.xaml (3)
- **Button** — Uid=btn_carvePause Content=Pause
- **Button** — Uid=btn_carveStop Content=Stop
- **Button** — Uid=btn_carveReset Content=Reset view

### CNC GCodeViewer\CNC GCodeViewer\ColorPicker.xaml (3)
- **Button** — Uid=cbut Name=cbut
- **Button** — Content=v
- **Button** — Content=×

### CNC GCodeViewer\CNC GCodeViewer\ConfigControl.xaml (14)
- **GroupBox** — Uid=grp_gcodeViewer Name=grpGCodeViewer Header=GCode Viewer
- **CheckBox** — Uid=lbl_enable Content=Enable
- **NumericField** — Uid=fld_arcResolution
- **NumericField** — Uid=fld_minDistance
- **CheckBox** — Uid=lbl_blackBackgroud Content=Black background
- **CheckBox** — Uid=lbl_showGrid Content=Show grid
- **CheckBox** — Uid=lbl_showAxes Content=Show axes
- **CheckBox** — Content=Show work envelope
- **CheckBox** — Uid=lbl_showBbox Content=Show bounding box
- **CheckBox** — Uid=lbl_showViewCube Content=Show ViewCube
- **CheckBox** — Uid=lbl_showCoordSys Content=Show coordinate system
- **CheckBox** — Uid=lbl_showOverlay Content=Show text overlay
- **CheckBox** — Uid=lbl_toolAutoScale Content=Auto scale tool
- **CheckBox** — Uid=lbl_highlightCompleted Content=Highlight completed cuts

### CNC GCodeViewer\CNC GCodeViewer\RenderControl.xaml (2)
- **ComboBox**
- **ComboBox**

### CNC GCodeViewer\CNC GCodeViewer\Viewer.xaml (1)
- **Button** — Uid=btn_resetView Content=Reset view

### ioSender XL\ioSender XL\HeightMapView.xaml (15)
- **GroupBox** — Uid=hm_grpProbe Header=Probe
- **ComboBox** — Uid=cbxProbe Name=cbxProbe
- **GroupBox** — Uid=hm_grpArea Header=Area to probe
- **NumericField**
- **NumericField** — Uid=hm_width
- **NumericField**
- **NumericField** — Uid=hm_height
- **Button** — Uid=hm_fromLimits Content=Set from program limits
- **GroupBox** — Uid=hm_grpGrid Header=Grid size
- **NumericField**
- **NumericField**
- **CheckBox** — Uid=hm_lock Content=Lock
- **GroupBox** — Uid=hm_grpProbing Header=Probing
- **GroupBox** — Uid=hm_grpSteps Header=Steps
- **GroupBox** — Uid=hm_grpView Header=Surface

### ioSender XL\ioSender XL\StartJobView.xaml (8)
- **GroupBox** — Uid=ls_grpSetup Header=Setup
- **ComboBox** — Uid=cbxProbeType Name=cbxProbeType
- **GroupBox** — Uid=ls_grpStock Name=grpStock Header=Stock
- **NumericField** — Uid=ls_width Name=fldWidth
- **NumericField** — Uid=ls_height Name=fldHeight
- **NumericField** — Uid=ls_thickness Name=fldThickness
- **GroupBox** — Uid=ls_grpActions Header=Actions
- **GroupBox** — Uid=ls_grpDrawing Header=Stock



---
name: native-winui-control
description: Control WinPool and other WinUI 3 desktop windows from PowerShell using UIAutomation and user32 P/Invoke. Use for window discovery, button clicks, tab selection, real-mouse input, and screenshot verification when native WinUI controls cannot be driven through browser automation.
---

# Native WinUI Control

Use this skill to control native WinUI 3 applications, especially WinPool, from
PowerShell. It is intended for local desktop automation where browser-based
Playwright is not applicable.

## Verified environment

- Windows PowerShell or PowerShell 7 on Windows.
- .NET `UIAutomationClient` and `UIAutomationTypes` assemblies.
- `System.Drawing` for screen capture.
- `user32.dll` P/Invoke for foreground, cursor, and mouse events.

Verified against WinPool V0.38:

- Main window class: `WinUIDesktopWin32WindowClass`.
- Main window title: `WinPool`.
- Title-bar tabs are UIA `ListItem` elements and support `SelectionItemPattern`.
- Welcome dialog buttons support `InvokePattern`.
- Real mouse clicking on a tab correctly changes selection.

## Workflow

### 1. Load dependencies

```powershell
Add-Type -AssemblyName UIAutomationClient,UIAutomationTypes
Add-Type -AssemblyName System.Drawing

$user32 = Add-Type -MemberDefinition @'
[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetProcessDPIAware();

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern bool SetForegroundWindow(System.IntPtr h);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void SetCursorPos(int x, int y);

[System.Runtime.InteropServices.DllImport("user32.dll")]
public static extern void mouse_event(int flags, int dx, int dy, int data, System.IntPtr extraInfo);
'@ -Name NativeWin32 -PassThru

[void]$user32::SetProcessDPIAware()
```

### 2. Find the target window

Do not use the first WinUI window blindly. Multiple WinUI applications can share
the same class name, so filter by `Current.Name`.

```powershell
$root = [System.Windows.Automation.AutomationElement]::RootElement
$classCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ClassNameProperty,
    'WinUIDesktopWin32WindowClass')

$candidates = $root.FindAll(
    [System.Windows.Automation.TreeScope]::Children,
    $classCondition)

$window = @(
    $candidates |
        Where-Object { $_.Current.Name -like 'WinPool*' }
)[0]

if ($null -eq $window) {
    throw 'WinPool window was not found.'
}
```

### 3. Inspect the accessibility tree before acting

Element indexes and coordinates are valid only for the latest observation.
Refresh after every action that can change layout or selection.

```powershell
$elements = $window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    [System.Windows.Automation.Condition]::TrueCondition)

foreach ($element in $elements) {
    [pscustomobject]@{
        Type        = $element.Current.ControlType.ProgrammaticName
        Name        = $element.Current.Name
        AutomationId = $element.Current.AutomationId
        Patterns    = $element.GetSupportedPatterns() -join ','
        Enabled     = $element.Current.IsEnabled
    }
}
```

Use the output to decide whether to use UIA patterns or real-mouse input.

### 4. Click a button with InvokePattern

```powershell
$buttonCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::Button)

$buttons = $window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    $buttonCondition)

$button = @($buttons | Where-Object { $_.Current.Name -eq '我知道啦' })[0]
$invoke = $button.GetCurrentPattern(
    [System.Windows.Automation.InvokePattern]::Pattern)
$invoke.Invoke()
```

Prefer `TryGetCurrentPattern` when the control type or pattern support is not
certain.

### 5. Select a title-bar tab

WinPool title tabs are `ListItem` elements. Their order is:

```text
0 管理
1 编辑
2 测试
3 监控
4 开发
5 设置
```

```powershell
$listCondition = New-Object System.Windows.Automation.PropertyCondition(
    [System.Windows.Automation.AutomationElement]::ControlTypeProperty,
    [System.Windows.Automation.ControlType]::ListItem)

$listItems = $window.FindAll(
    [System.Windows.Automation.TreeScope]::Descendants,
    $listCondition)

$tabItems = @($listItems)[0..5]
$settingsTab = $tabItems[5]
$selection = $settingsTab.GetCurrentPattern(
    [System.Windows.Automation.SelectionItemPattern]::Pattern)
$selection.Select()
```

After selection, refresh the UIA tree before using new indexes.

### 6. Real mouse input

Use real mouse input for controls that do not expose a usable UIA pattern, such
as topology nodes or right-click menus.

```powershell
$bounds = $element.Current.BoundingRectangle
$centerX = [int]($bounds.X + $bounds.Width / 2)
$centerY = [int]($bounds.Y + $bounds.Height / 2)

[void]$user32::SetForegroundWindow(
    [System.IntPtr]$window.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 600

$user32::SetCursorPos($centerX, $centerY)

# Left click
$user32::mouse_event(0x0002, 0, 0, 0, [System.IntPtr]::Zero)
$user32::mouse_event(0x0004, 0, 0, 0, [System.IntPtr]::Zero)

# Right click
$user32::mouse_event(0x0008, 0, 0, 0, [System.IntPtr]::Zero)
$user32::mouse_event(0x0010, 0, 0, 0, [System.IntPtr]::Zero)
```

Always get a fresh `BoundingRectangle` from the latest UIA observation.

### 7. Screenshot verification

```powershell
[void]$user32::SetForegroundWindow(
    [System.IntPtr]$window.Current.NativeWindowHandle)
Start-Sleep -Milliseconds 700

$bounds = $window.Current.BoundingRectangle
$bitmap = New-Object System.Drawing.Bitmap(
    [int]$bounds.Width,
    [int]$bounds.Height)

$graphics = [System.Drawing.Graphics]::FromImage($bitmap)
$graphics.CopyFromScreen(
    [int]$bounds.X,
    [int]$bounds.Y,
    0,
    0,
    $bitmap.Size)

$bitmap.Save('C:\path\to\shot.png', [System.Drawing.Imaging.ImageFormat]::Png)
$graphics.Dispose()
$bitmap.Dispose()
```

Call `SetProcessDPIAware` before capture. On high-DPI systems, missing DPI
awareness can produce wrong coordinates or an offset screenshot.

## Important rules

- Use `[System.Windows.Automation.TreeScope]::Children` rather than the string
  `'Children'` where practical.
- Keep dropdown expansion and item selection in the same PowerShell process.
- Do not reuse element indexes, screenshot IDs, or coordinates after the UI
  changes.
- Reobserve after every action.
- Do not assume a WinUI control supports `ValuePattern`; inspect
  `GetSupportedPatterns()` first.
- Use `SetForegroundWindow` before mouse and screenshot actions.
- Do not use this skill for real storage-structure mutation. WinPool automation
  remains subject to the product safety boundary.

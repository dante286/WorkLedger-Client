# WorkLedger Theme System Usage Guide

## Overview
The WorkLedger application now implements a comprehensive light and dark theme system that automatically detects and applies your system's theme preference.

## How It Works

### Automatic Theme Detection
- **On startup**, the `ThemeService` detects your system's theme preference using Avalonia's `ActualThemeVariant`
- If your system is set to **Dark Mode**, the dark theme is applied automatically
- If your system is set to **Light Mode**, the light theme is applied automatically
- No manual configuration needed!

### Theme Service
Located in `Services/ThemeService.cs`, it provides:
- `DarkMode` property - Get or set the current theme state
- `SetTheme(string themeName)` - Manually switch to "Dark" or "Light"
- `ToggleTheme()` - Switch between themes

## Color Tokens (Available in XAML)

### Primary Colors
```xaml
<!-- Teal - Primary accent for active/running states -->
<Color x:Key="TealActive">#0d9488</Color>
<Color x:Key="TealGlow">#2dd4bf</Color>  <!-- or #14b8a8 in light -->
<Color x:Key="TealDark">#0f766e</Color>

<!-- Semantic Colors -->
<Color x:Key="AmberWarning">#fbbf24</Color>  <!-- #d97706 in light -->
<Color x:Key="RedDanger">#f87171</Color>     <!-- #ef4444 in light -->
```

### Surface & Background Colors
```xaml
<!-- Core component colors - automatically adapt to theme -->
<Color x:Key="Background">  <!-- App background -->
<Color x:Key="Surface">     <!-- Cards, panels -->
<Color x:Key="Surface2">    <!-- Secondary surfaces -->
<Color x:Key="Border">      <!-- Border and dividers -->
<Color x:Key="Border2">     <!-- Secondary borders -->
```

### Text Colors (Hierarchy)
```xaml
<Color x:Key="TextPrimary">     <!-- Primary headings and body text -->
<Color x:Key="TextSecondary">   <!-- Secondary labels, descriptions -->
<Color x:Key="TextTertiary">    <!-- Tertiary text, metadata -->
<Color x:Key="TextQuaternary">  <!-- Muted, disabled states -->
```

### Slate Scale (Full Range - Same in Both Themes)
```xaml
<!-- 11-step neutral scale for precise control -->
<Color x:Key="Slate950">#0a0d12</Color>  <!-- Darkest -->
<Color x:Key="Slate900">#0f1318</Color>
<Color x:Key="Slate800">#161c26</Color>
<Color x:Key="Slate700">#1e2736</Color>
<Color x:Key="Slate600">#2c3a50</Color>
<Color x:Key="Slate500">#3d5166</Color>
<Color x:Key="Slate400">#5a7190</Color>
<Color x:Key="Slate300">#8ba3be</Color>
<Color x:Key="Slate200">#bdd0e3</Color>
<Color x:Key="Slate100">#dce8f2</Color>
<Color x:Key="Slate050">#f0f5fa</Color>  <!-- Lightest -->
```

## Font Resources

### Font Families
```xaml
<!-- Manrope - Display/Headings -->
<FontFamily x:Key="FontDisplay">Manrope</FontFamily>

<!-- DM Sans - Body & UI text -->
<FontFamily x:Key="FontBody">DM Sans</FontFamily>

<!-- DM Mono - Time values, data, monospaced -->
<FontFamily x:Key="FontMono">DM Mono</FontFamily>
```

### Font Sizes
```xaml
<x:Double x:Key="FontSizeSmall">11</x:Double>      <!-- Small labels -->
<x:Double x:Key="FontSizeBase">12</x:Double>       <!-- Standard UI -->
<x:Double x:Key="FontSizeMedium">13</x:Double>     <!-- Body text -->
<x:Double x:Key="FontSizeLarge">16</x:Double>      <!-- Headers -->
<x:Double x:Key="FontSizeXLarge">20</x:Double>     <!-- Larger headers -->
<x:Double x:Key="FontSizeTitle">28</x:Double>      <!-- Page title -->
```

## Spacing & Layout Tokens

### Spacing Scale
```xaml
<Thickness x:Key="SpacingSmall">4</Thickness>      <!-- 4px -->
<Thickness x:Key="SpacingBase">8</Thickness>       <!-- 8px -->
<Thickness x:Key="SpacingMedium">12</Thickness>    <!-- 12px -->
<Thickness x:Key="SpacingLarge">16</Thickness>     <!-- 16px (gutters) -->
<Thickness x:Key="SpacingXLarge">24</Thickness>    <!-- 24px -->
<Thickness x:Key="SpacingXXLarge">32</Thickness>   <!-- 32px (sections) -->
```

### Border Radius
```xaml
<CornerRadius x:Key="CornerRadiusSmall">4</CornerRadius>      <!-- Badges -->
<CornerRadius x:Key="CornerRadiusMedium">7</CornerRadius>     <!-- Buttons -->
<CornerRadius x:Key="CornerRadiusLarge">10</CornerRadius>     <!-- Cards -->
<CornerRadius x:Key="CornerRadiusXLarge">12</CornerRadius>    <!-- App icon -->
```

## Usage Examples in XAML

### Using Colors
```xaml
<!-- Background -->
<Panel Background="{DynamicResource Background}" />

<!-- Text with adaptive color -->
<TextBlock 
    Text="Project Name"
    Foreground="{DynamicResource TextPrimary}"
    FontFamily="{DynamicResource FontDisplay}"
    FontSize="{DynamicResource FontSizeLarge}" />

<!-- Border -->
<Border 
    BorderBrush="{DynamicResource Border}"
    BorderThickness="1"
    CornerRadius="{DynamicResource CornerRadiusLarge}"
    Padding="{DynamicResource SpacingLarge}" />
```

### Active/Running State
```xaml
<!-- Running project indicator -->
<Panel Background="{DynamicResource TealActive}" Opacity="0.12" />
<TextBlock Foreground="{DynamicResource TealActive}" Text="Running" />
```

### Timer Display
```xaml
<!-- Large monospace timer (always stays teal when running) -->
<TextBlock
    Text="02:47:33"
    FontFamily="{DynamicResource FontMono}"
    FontSize="28"
    FontWeight="Medium"
    Foreground="{DynamicResource TealActive}" />
```

## Programmatic Theme Switching

### In Code-Behind or ViewModel
```csharp
// Inject ThemeService
private readonly ThemeService _themeService;

public MyViewModel(ThemeService themeService)
{
    _themeService = themeService;
}

// Toggle theme
_themeService.ToggleTheme();

// Set specific theme
_themeService.SetTheme("Dark");
_themeService.SetTheme("Light");

// Check current theme
bool isDark = _themeService.DarkMode;
```

## Theme Comparison

| Element | Dark Theme | Light Theme |
|---------|-----------|------------|
| Background | #0f1318 | #f4f6f9 |
| Surface | #161c26 | #ffffff |
| Text Primary | #dce8f2 | #0e1117 |
| Accent | #0d9488 | #0d9488 |
| Warning | #fbbf24 | #d97706 |
| Danger | #f87171 | #ef4444 |

## Design Principles

1. **Consistent Accent** - Teal (#0d9488) is the ONLY accent color - never used decoratively, only for:
   - Active/running states
   - CTAs (Call-to-action buttons)
   - Links and focus states
   - Timer displays

2. **Semantic Colors**
   - **Amber** = Warnings (sync conflicts, unsaved state, timer running)
   - **Red** = Destructive actions (delete, stop)
   - **Teal** = Positive/active (running, success)

3. **Typography Hierarchy**
   - **Manrope 800** = Page titles
   - **Manrope 600** = Section headers
   - **DM Sans 400** = Body text & UI labels
   - **DM Mono 500** = Timer displays & data
   - **DM Mono 400** = Badges, metadata

4. **Theme Independence**
   - Always use `{DynamicResource}` bindings
   - Never hardcode colors
   - Both themes maintain identical information hierarchy

## Files Modified
- `Assets/themes/DarkTheme.xaml` - Dark theme resource dictionary
- `Assets/themes/LightTheme.xaml` - Light theme resource dictionary
- `Services/ThemeService.cs` - Theme detection and switching logic
- `App.axaml.cs` - Already properly wired to use ThemeService

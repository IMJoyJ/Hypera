using MudBlazor;

namespace Hypera.Web.UI.Theming;

public static class HyperaTheme
{
    public static MudTheme Theme { get; } = new()
    {
        PaletteDark = new PaletteDark
        {
            Primary = "#2A9D8F",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#64748B",
            SecondaryContrastText = "#F8FAFC",
            Tertiary = "#38BDF8",
            TertiaryContrastText = "#0F172A",
            Info = "#38BDF8",
            Success = "#10B981",
            Warning = "#D97706",
            Error = "#E11D48",
            Background = "#14191F",
            BackgroundGray = "#10151A",
            Surface = "#1C222B",
            DrawerBackground = "#161C24",
            DrawerText = "#CBD5E1",
            DrawerIcon = "#94A3B8",
            AppbarBackground = "#10151A",
            AppbarText = "#E2E8F0",
            TextPrimary = "#E2E8F0",
            TextSecondary = "#94A3B8",
            TextDisabled = "rgba(148, 163, 184, 0.45)",
            ActionDefault = "#94A3B8",
            LinesDefault = "#293240",
            LinesInputs = "#3B4758",
            TableLines = "#25303D",
            TableStriped = "rgba(148, 163, 184, 0.05)",
            Divider = "#26303D",
            DividerLight = "#1E2631",
            Dark = "#0F1419",
            Black = "#0B0F14",
        },
        PaletteLight = new PaletteLight
        {
            Primary = "#0F766E",
            PrimaryContrastText = "#FFFFFF",
            Secondary = "#475569",
            SecondaryContrastText = "#FFFFFF",
            Tertiary = "#0284C7",
            TertiaryContrastText = "#FFFFFF",
            Info = "#0284C7",
            Success = "#059669",
            Warning = "#B45309",
            Error = "#BE123C",
            Background = "#F6F8FA",
            BackgroundGray = "#EEF2F6",
            Surface = "#FFFFFF",
            DrawerBackground = "#F1F5F9",
            DrawerText = "#334155",
            DrawerIcon = "#475569",
            AppbarBackground = "#0F4C5C",
            AppbarText = "#F8FAFC",
            TextPrimary = "#0F172A",
            TextSecondary = "#475569",
            TextDisabled = "rgba(71, 85, 105, 0.45)",
            ActionDefault = "#475569",
            LinesDefault = "#E2E8F0",
            LinesInputs = "#CBD5E1",
            TableLines = "#E2E8F0",
            TableStriped = "rgba(15, 23, 42, 0.025)",
            Divider = "#E2E8F0",
            DividerLight = "#F1F5F9",
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "6px",
            AppbarHeight = "52px",
            DrawerWidthLeft = "220px",
        },
    };
}

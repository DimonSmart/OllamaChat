using MudBlazor;

namespace ChatClient.Api.Client.Styling;

internal static class AppTheme
{
    public static MudTheme Create(PaletteLight lightPalette, PaletteDark darkPalette) => new()
    {
        PaletteLight = lightPalette,
        PaletteDark = darkPalette,
        Typography = new Typography
        {
            Default = new DefaultTypography { FontSize = "0.85rem", LineHeight = "1.3" },
            Body1 = new Body1Typography { FontSize = "0.875rem", LineHeight = "1.35" },
            Body2 = new Body2Typography { FontSize = "0.8125rem", LineHeight = "1.3" },
            Button = new ButtonTypography { FontSize = "0.8125rem", LineHeight = "1.3" },
            Subtitle1 = new Subtitle1Typography { FontSize = "0.9375rem", LineHeight = "1.35" },
            Subtitle2 = new Subtitle2Typography { FontSize = "0.875rem", LineHeight = "1.3" },
            H6 = new H6Typography { FontSize = "1.05rem", LineHeight = "1.3" }
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "3px",
            AppbarHeight = "52px"
        }
    };
}

using Avalonia.Data.Converters;
using Avalonia.Media;
using Encspect.Gui.ViewModels;

namespace Encspect.Gui.Views;

// Outline icons on a 16 by 16 grid.
public static class Icons
{
    public static Geometry Folder { get; } = Geometry.Parse("F0 M1,3 L6,3 L7.5,4.5 L15,4.5 L15,13.5 L1,13.5 Z M2.2,5.7 L2.2,12.3 L13.8,12.3 L13.8,5.7 Z");

    public static Geometry Home { get; } = Geometry.Parse("F0 M8,1.2 L15.2,7.6 L13.4,7.6 L13.4,14.2 L2.6,14.2 L2.6,7.6 L0.8,7.6 Z M3.8,6.9 L3.8,13 L6.6,13 L6.6,9.2 L9.4,9.2 L9.4,13 L12.2,13 L12.2,6.9 L8,3.1 Z");

    public static Geometry Music { get; } = Geometry.Parse("M6,3 L14,1.3 L14,11.3 A2.2,1.9 0 1 1 12.8,9.6 L12.8,4.6 L7.2,5.8 L7.2,12.8 A2.2,1.9 0 1 1 6,11.1 Z");

    public static Geometry Desktop { get; } = Geometry.Parse("F0 M1,2 L15,2 L15,11.5 L9.3,11.5 L9.3,13.2 L11.5,13.2 L11.5,14.4 L4.5,14.4 L4.5,13.2 L6.7,13.2 L6.7,11.5 L1,11.5 Z M2.2,3.2 L2.2,10.3 L13.8,10.3 L13.8,3.2 Z");

    public static Geometry Volume { get; } = Geometry.Parse("F0 M1,4.5 L15,4.5 L15,12 L1,12 Z M2.2,5.7 L2.2,10.8 L13.8,10.8 L13.8,5.7 Z M11,7.6 L12.6,7.6 L12.6,8.9 L11,8.9 Z");

    public static Geometry Chevron { get; } = Geometry.Parse("M5,2.5 L10.5,8 L5,13.5 L3.8,12.3 L8.1,8 L3.8,3.7 Z");

    public static Geometry Up { get; } = Geometry.Parse("M2.5,10.5 L8,5 L13.5,10.5 L12.3,11.7 L8,7.4 L3.7,11.7 Z");

    public static Geometry Down { get; } = Geometry.Parse("M2.5,5.5 L8,11 L13.5,5.5 L12.3,4.3 L8,8.6 L3.7,4.3 Z");

    public static IValueConverter ForFolderKind { get; } = new FuncValueConverter<FolderKind, Geometry?>(kind => kind switch
    {
        FolderKind.Home => Home,
        FolderKind.Music => Music,
        FolderKind.Desktop => Desktop,
        FolderKind.Volume => Volume,
        FolderKind.Placeholder => null,
        _ => Folder,
    });
}

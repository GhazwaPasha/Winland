using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Winland.Models;

/// <summary>
/// A dropped file reference on the notch's Shelf tab — path only, never a
/// copy, so removing it from the shelf never touches the real file.
/// </summary>
public sealed record ShelfItem(string Path, string DisplayName, ImageSource? Icon)
{
    /// <summary>
    /// Builds a shelf entry for a path that's already confirmed to exist.
    /// Icon extraction uses the same <see cref="System.Drawing.Icon.ExtractAssociatedIcon"/>
    /// API already used for the tray icon in MainWindow.xaml.cs — best
    /// effort; a shelf entry with no icon (extraction failed) still works,
    /// it just shows a bare file name.
    /// </summary>
    public static ShelfItem Create(string path)
    {
        var displayName = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(displayName))
        {
            displayName = path;
        }

        ImageSource? icon = null;
        try
        {
            using var associatedIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (associatedIcon is not null)
            {
                var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                    associatedIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmapSource.Freeze(); // cross-thread safe once frozen, same as MediaService's thumbnail
                icon = bitmapSource;
            }
        }
        catch
        {
            // No icon — the chip just shows the file name.
        }

        return new ShelfItem(path, displayName, icon);
    }
}

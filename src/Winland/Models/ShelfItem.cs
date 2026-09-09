using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Winland.Interop;

namespace Winland.Models;

/// <summary>
/// A dropped file reference on the notch's Shelf tab — path only, never a
/// copy, so removing it from the shelf never touches the real file.
///
/// Icon lookup (<see cref="LoadIcon"/>) is COM/Shell interop and can take a
/// noticeable moment — a network path, a large video, a cold thumbnail
/// cache. Doing that synchronously on the UI thread when a file lands on
/// the shelf either stalls the drop for that whole time or, doing it eagerly
/// before the chip exists at all, leaves the chip simply not appearing yet
/// with no indication anything is happening. So chips are always created via
/// <see cref="CreatePending"/> first — <see cref="IsIconLoading"/> true,
/// <see cref="Icon"/> null — and the view model swaps in a second record
/// (from <see cref="LoadIcon"/>, run on a background thread) once the lookup
/// finishes, whatever it finds.
/// </summary>
public sealed record ShelfItem(string Path, string DisplayName, ImageSource? Icon, bool IsIconLoading)
{
    /// <summary>Immediately-available chip for a path already confirmed to exist — icon fetch happens after.</summary>
    public static ShelfItem CreatePending(string path)
    {
        var displayName = System.IO.Path.GetFileName(path);
        if (string.IsNullOrEmpty(displayName))
        {
            displayName = path;
        }

        return new ShelfItem(path, displayName, Icon: null, IsIconLoading: true);
    }

    /// <summary>
    /// The actual icon lookup — call this off the UI thread (<c>Task.Run</c>).
    /// Tries a real Shell thumbnail first — via <see cref="ShellThumbnailProvider"/>,
    /// the same generator Explorer itself uses — so a photo or video shows
    /// actual content instead of a generic file-type icon; only for file
    /// types the Shell has no thumbnail handler for (or if that lookup
    /// fails outright) does this fall back to the plain associated icon via
    /// <see cref="System.Drawing.Icon.ExtractAssociatedIcon"/>, the same API
    /// already used for the tray icon in MainWindow.xaml.cs. Best effort
    /// either way — returning null (neither worked) still leaves a usable
    /// chip, it just shows a bare file name.
    /// </summary>
    public static ImageSource? LoadIcon(string path)
    {
        var icon = ShellThumbnailProvider.TryGetThumbnail(path, 64);
        if (icon is not null)
        {
            return icon;
        }

        try
        {
            using var associatedIcon = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (associatedIcon is null)
            {
                return null;
            }

            var bitmapSource = Imaging.CreateBitmapSourceFromHIcon(
                associatedIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            bitmapSource.Freeze(); // cross-thread safe once frozen, same as MediaService's thumbnail
            return bitmapSource;
        }
        catch
        {
            // No icon either — the chip just shows the file name.
            return null;
        }
    }
}

using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Winland.Interop;

/// <summary>
/// Wraps the Windows Shell's own thumbnail generator
/// (<c>IShellItemImageFactory</c>) — the exact same one Explorer uses — so a
/// shelf chip for a photo or video shows real content instead of just the
/// file type's generic icon. Works for anything the Shell knows how to
/// thumbnail (images, video frames, PDFs, Office docs, ...); callers should
/// fall back to a plain associated-icon lookup for whatever this fails on.
/// </summary>
internal static class ShellThumbnailProvider
{
    private static readonly Guid IidImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

    // SIIGBF (shobjidl_core.h) — SIIGBF_THUMBNAILONLY deliberately fails
    // rather than silently handing back the generic file-type icon, so a
    // failed call here reliably means "fall back to the icon" rather than
    // "got the icon dressed up as a thumbnail".
    private const int SiigbfResizeToFit = 0x00000000;
    private const int SiigbfThumbnailOnly = 0x00000008;

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, nint pbc, ref Guid riid, out IShellItemImageFactory ppv);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(nint hObject);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        void GetImage(Size size, int flags, out nint phbm);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Size
    {
        public int Cx;
        public int Cy;
        public Size(int cx, int cy) { Cx = cx; Cy = cy; }
    }

    /// <summary>
    /// Best-effort — returns null for anything from "not a real
    /// filesystem-resolvable path" to "Shell has no thumbnail handler for
    /// this file type", never throws.
    /// </summary>
    public static BitmapSource? TryGetThumbnail(string path, int pixelSize)
    {
        try
        {
            var riid = IidImageFactory;
            if (SHCreateItemFromParsingName(path, 0, ref riid, out var factory) != 0 || factory is null)
            {
                return null;
            }

            factory.GetImage(new Size(pixelSize, pixelSize), SiigbfThumbnailOnly | SiigbfResizeToFit, out var hBitmap);
            if (hBitmap == 0)
            {
                return null;
            }

            try
            {
                var bitmap = System.Windows.Interop.Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap, nint.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze(); // cross-thread safe once frozen, same pattern as ShelfItem's icon extraction
                return bitmap;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        catch
        {
            return null;
        }
    }
}

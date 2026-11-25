using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace SwVault.Agent;

/// <summary>
/// The preview saved with a version when the client sent none (a check-in from outside SOLIDWORKS,
/// an import, or a file SOLIDWORKS couldn't picture): Windows' own thumbnail of the file, the one
/// Explorer shows. SOLIDWORKS installs the handler for its files; images, PDFs and others have theirs.
/// </summary>
internal static class ShellPreview
{
    /// <summary>The same size the add-in saves (Thumbnails.CheckInSize).</summary>
    private const int Size = 256;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan Pause = TimeSpan.FromMinutes(5);
    private static int _timeouts;
    private static long _pausedUntil;

    /// <summary>The file's thumbnail as a JPEG, or null when Windows has none. Never throws.</summary>
    public static byte[]? Capture(string path, FileLog log)
    {
        // A thumbnail handler that hangs must not hold up every file of a large check-in.
        if (Environment.TickCount64 < Interlocked.Read(ref _pausedUntil)) return null;
        byte[]? result = null;
        Exception? error = null;
        // Thumbnail handlers are apartment-threaded; the agent's own threads are not.
        var thread = new Thread(() =>
        {
            try { result = Read(path); }
            catch (Exception ex) { error = ex; }
        }) { IsBackground = true, Name = "SwVault preview" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(Timeout))
        {
            log.Warn($"No preview for {path}: Windows took longer than {Timeout.TotalSeconds:0} s to make its thumbnail.");
            if (Interlocked.Increment(ref _timeouts) >= 2)
            {
                Interlocked.Exchange(ref _timeouts, 0);
                Interlocked.Exchange(ref _pausedUntil, Environment.TickCount64 + (long)Pause.TotalMilliseconds);
                log.Warn($"Previews are skipped for {Pause.TotalMinutes:0} minutes. Update Preview (right-click a file in SOLIDWORKS) saves them later.");
            }
            return null;
        }
        Interlocked.Exchange(ref _timeouts, 0);
        if (error != null) log.Warn($"No preview for {path}: {error.Message}");
        return result;
    }

    private const int SIIGBF_BIGGERSIZEOK = 0x01;
    private const int SIIGBF_THUMBNAILONLY = 0x08; // never the file type's icon

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    private static byte[]? Read(string path)
    {
        if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out var factory) != 0 || factory == null) return null;
        try
        {
            if (factory.GetImage(new NativeSize { Width = Size, Height = Size }, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out var hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
            try
            {
                using var raw = FromHbitmap(hbitmap);
                using var square = OnWhite(raw);
                return ToJpeg(square);
            }
            finally
            {
                DeleteObject(hbitmap);
            }
        }
        finally
        {
            Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>
    /// Image.FromHbitmap drops the alpha channel, which turns transparent backgrounds black.
    /// Re-reads the same pixels as ARGB when the bitmap actually carries alpha.
    /// </summary>
    private static Bitmap FromHbitmap(IntPtr hbitmap)
    {
        var bitmap = Image.FromHbitmap(hbitmap);
        if (Image.GetPixelFormatSize(bitmap.PixelFormat) < 32) return bitmap;
        var data = bitmap.LockBits(new Rectangle(0, 0, bitmap.Width, bitmap.Height), ImageLockMode.ReadOnly, bitmap.PixelFormat);
        try
        {
            // Bottom-up bitmaps (what SOLIDWORKS' handler returns for some files) have a negative
            // stride: Scan0 is then the top row and the rows go backwards in memory.
            var row = new byte[Math.Abs(data.Stride)];
            var hasAlpha = false;
            for (var y = 0; y < data.Height && !hasAlpha; y++)
            {
                Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                for (var x = 3; x < data.Width * 4; x += 4)
                    if (row[x] != 0) { hasAlpha = true; break; }
            }
            if (!hasAlpha) return bitmap;
            using var argb = new Bitmap(data.Width, data.Height, data.Stride, PixelFormat.Format32bppArgb, data.Scan0);
            var copy = new Bitmap(argb);
            bitmap.UnlockBits(data);
            data = null;
            bitmap.Dispose();
            return copy;
        }
        finally
        {
            if (data != null) bitmap.UnlockBits(data);
        }
    }

    /// <summary>The image scaled to fit a white square.</summary>
    private static Bitmap OnWhite(Image source)
    {
        var result = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(result);
        g.Clear(Color.White);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        var scale = Math.Min((float)Size / source.Width, (float)Size / source.Height);
        var w = source.Width * scale;
        var h = source.Height * scale;
        g.DrawImage(source, (Size - w) / 2, (Size - h) / 2, w, h);
        return result;
    }

    private static byte[] ToJpeg(Image image)
    {
        var codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid)!;
        using var stream = new MemoryStream();
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
        image.Save(stream, codec, parameters);
        return stream.ToArray();
    }
}

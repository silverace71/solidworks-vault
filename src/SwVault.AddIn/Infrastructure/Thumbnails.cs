using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SwVault.AddIn.Infrastructure
{
    /// <summary>
    /// File thumbnails for the browser. Windows' thumbnail handler (installed with SOLIDWORKS and
    /// eDrawings) does the work on a background STA thread; when it has nothing, SOLIDWORKS' own
    /// stored preview is read on the main thread. Results are cached as PNGs under
    /// %LOCALAPPDATA%\SwVault\thumbs, keyed by path, size and modified time.
    /// </summary>
    internal sealed class Thumbnails : IDisposable
    {
        public const int Size = 128;

        private static readonly string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwVault", "thumbs");

        private readonly BlockingCollection<Request> _queue = new BlockingCollection<Request>();
        private readonly Func<string, string, bool> _savePreview;
        private readonly Thread _worker;
        private int _generation;

        private sealed class Request
        {
            public string Path;
            public int Generation;
            public Action<Image> Done;
        }

        /// <param name="savePreview">(file, bmpFile) =&gt; wrote a preview; called on SOLIDWORKS' main thread.</param>
        public Thumbnails(Func<string, string, bool> savePreview)
        {
            _savePreview = savePreview;
            _worker = new Thread(Work) { IsBackground = true, Name = "SwVault thumbnails" };
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }

        /// <summary>Queues a thumbnail; <paramref name="done"/> runs on the UI thread, with null when there is none.</summary>
        public void Get(string localPath, Action<Image> done)
        {
            if (string.IsNullOrEmpty(localPath) || _queue.IsAddingCompleted) return;
            _queue.Add(new Request { Path = localPath, Generation = Volatile.Read(ref _generation), Done = done });
        }

        /// <summary>Drops queued requests (a new search replaced the results they were for).</summary>
        public void CancelPending() => Interlocked.Increment(ref _generation);

        private void Work()
        {
            foreach (var request in _queue.GetConsumingEnumerable())
            {
                if (request.Generation != Volatile.Read(ref _generation)) continue;
                try
                {
                    var file = new FileInfo(request.Path);
                    if (!file.Exists)
                    {
                        Deliver(request, null);
                        continue;
                    }
                    var cached = CachePath(file);
                    if (File.Exists(cached))
                    {
                        Deliver(request, LoadUnlocked(cached));
                        continue;
                    }
                    var image = TryFromShell(file.FullName, Size);
                    if (image != null)
                    {
                        Save(image, cached);
                        Deliver(request, image);
                    }
                    else
                    {
                        UiThread.Post(() => FromSolidWorks(request, cached));
                    }
                }
                catch (Exception ex)
                {
                    Log.Warn("Thumbnail for " + request.Path + ": " + ex.Message);
                    Deliver(request, null);
                }
            }
        }

        private void FromSolidWorks(Request request, string cached)
        {
            Image image = null;
            var bmp = Path.Combine(Path.GetTempPath(), "swvault-preview-" + Guid.NewGuid().ToString("N") + ".bmp");
            try
            {
                if (request.Generation == Volatile.Read(ref _generation) && _savePreview(request.Path, bmp))
                {
                    using (var raw = LoadUnlocked(bmp)) image = Normalize(raw);
                    Save(image, cached);
                }
            }
            catch (Exception ex)
            {
                Log.Warn("SOLIDWORKS preview for " + request.Path + ": " + ex.Message);
            }
            finally
            {
                TryDelete(bmp);
            }
            request.Done(image);
        }

        private static void Deliver(Request request, Image image) => UiThread.Post(() => request.Done(image));

        private static string CachePath(FileInfo file)
        {
            var key = file.FullName.ToLowerInvariant() + "|" + file.Length + "|" + file.LastWriteTimeUtc.Ticks + "|" + Size;
            using (var sha = SHA1.Create())
            {
                var hash = BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(key))).Replace("-", "").ToLowerInvariant();
                return Path.Combine(Folder, hash.Substring(0, 2), hash + ".png");
            }
        }

        private static void Save(Image image, string path)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                image.Save(path, ImageFormat.Png);
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException || ex is ExternalException)
            {
                Log.Warn("Could not cache thumbnail: " + ex.Message);
            }
        }

        /// <summary>Loads an image without keeping the file open.</summary>
        private static Image LoadUnlocked(string path)
        {
            using (var stream = new MemoryStream(File.ReadAllBytes(path)))
            using (var image = Image.FromStream(stream))
                return new Bitmap(image);
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>Size of the preview saved with each version at check-in.</summary>
        public const int CheckInSize = 256;

        /// <summary>
        /// The preview saved with a new version: the file's thumbnail as a base64 JPEG, or null when
        /// there is none. Runs on SOLIDWORKS' main thread, after the file was saved.
        /// </summary>
        public static string CaptureForCheckIn(string path, Func<string, string, bool> savePreview)
        {
            try
            {
                Image image = TryFromShell(path, CheckInSize);
                if (image == null)
                {
                    var bmp = Path.Combine(Path.GetTempPath(), "swvault-preview-" + Guid.NewGuid().ToString("N") + ".bmp");
                    try
                    {
                        if (savePreview(path, bmp))
                            using (var raw = LoadUnlocked(bmp)) image = Normalize(raw, CheckInSize);
                    }
                    finally
                    {
                        TryDelete(bmp);
                    }
                }
                if (image == null)
                {
                    Log.Warn("No check-in thumbnail for " + path + ": neither Windows nor SOLIDWORKS has a preview of it.");
                    return null;
                }
                using (image)
                    return Convert.ToBase64String(ToJpeg(image));
            }
            catch (Exception ex)
            {
                Log.Warn("No check-in thumbnail for " + path + ": " + ex.Message);
                return null;
            }
        }

        private static byte[] ToJpeg(Image image)
        {
            var codec = Array.Find(ImageCodecInfo.GetImageEncoders(), c => c.FormatID == ImageFormat.Jpeg.Guid);
            using (var stream = new MemoryStream())
            using (var parameters = new EncoderParameters(1))
            {
                parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 85L);
                image.Save(stream, codec, parameters);
                return stream.ToArray();
            }
        }

        /// <summary>A saved image (from the agent) at browser size, or null when it can't be read.</summary>
        public static Image FromBase64(string base64)
        {
            if (string.IsNullOrEmpty(base64)) return null;
            try
            {
                using (var stream = new MemoryStream(Convert.FromBase64String(base64)))
                using (var image = Image.FromStream(stream))
                    return new Bitmap(image);
            }
            catch (Exception ex) when (ex is FormatException || ex is ArgumentException)
            {
                return null;
            }
        }

        /// <summary>The image scaled to fit a white square of <paramref name="size"/> pixels.</summary>
        internal static Bitmap Normalize(Image source, int size = Size)
        {
            var result = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(result))
            {
                g.Clear(Color.White);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                var scale = Math.Min((float)size / source.Width, (float)size / source.Height);
                var w = source.Width * scale;
                var h = source.Height * scale;
                g.DrawImage(source, (size - w) / 2, (size - h) / 2, w, h);
            }
            return result;
        }

        // ------------------------------------------------------------ Windows shell

        private const int SIIGBF_BIGGERSIZEOK = 0x01;
        private const int SIIGBF_THUMBNAILONLY = 0x08;

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
        private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid, out IShellItemImageFactory factory);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr handle);

        /// <summary>The shell's thumbnail, or null when there is none or reading it failed (the caller then asks SOLIDWORKS).</summary>
        private static Image TryFromShell(string path, int size)
        {
            try
            {
                return FromShell(path, size);
            }
            catch (Exception ex)
            {
                // Everything, OutOfMemoryException too: that is what GDI+ and a bad bitmap size throw,
                // and SOLIDWORKS' own preview is still worth asking for.
                Log.Warn("Windows thumbnail for " + path + ": " + ex.Message);
                return null;
            }
        }

        /// <summary>The shell's thumbnail (never the generic file icon), or null.</summary>
        private static Image FromShell(string path, int size = Size)
        {
            IShellItemImageFactory factory;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory) != 0 || factory == null) return null;
            try
            {
                IntPtr hbitmap;
                if (factory.GetImage(new NativeSize { Width = size, Height = size }, SIIGBF_THUMBNAILONLY | SIIGBF_BIGGERSIZEOK, out hbitmap) != 0 || hbitmap == IntPtr.Zero) return null;
                try
                {
                    using (var raw = FromHbitmap(hbitmap)) return Normalize(raw, size);
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
            var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
            var data = bitmap.LockBits(rect, ImageLockMode.ReadOnly, bitmap.PixelFormat);
            try
            {
                var hasAlpha = false;
                // Bottom-up bitmaps (what SOLIDWORKS' thumbnail handler returns for some files) have a
                // negative stride: Scan0 is then the top row and the rows go backwards in memory.
                var row = new byte[Math.Abs(data.Stride)];
                for (var y = 0; y < data.Height && !hasAlpha; y++)
                {
                    Marshal.Copy(data.Scan0 + y * data.Stride, row, 0, row.Length);
                    for (var x = 3; x < data.Width * 4; x += 4)
                        if (row[x] != 0) { hasAlpha = true; break; }
                }
                if (!hasAlpha) return bitmap;
                using (var argb = new Bitmap(data.Width, data.Height, data.Stride, PixelFormat.Format32bppArgb, data.Scan0))
                {
                    var copy = new Bitmap(argb);
                    bitmap.UnlockBits(data);
                    data = null;
                    bitmap.Dispose();
                    return copy;
                }
            }
            finally
            {
                if (data != null) bitmap.UnlockBits(data);
            }
        }

        public void Dispose()
        {
            CancelPending();
            _queue.CompleteAdding();
        }
    }
}

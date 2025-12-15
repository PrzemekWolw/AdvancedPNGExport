using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Text;
using PaintDotNet;
using PaintDotNet.IndirectUI;
using PaintDotNet.PropertySystem;

namespace AdvancedPNGExport
{
    // Factory so Paint.NET can instantiate the file type
    public sealed class PngAdvancedFactory : IFileTypeFactory
    {
        public FileType[] GetFileTypeInstances() => new[] { new PngAdvancedPlugin() };
    }

    // Info shown in PDN's Settings > Plugins
    public sealed class PNGadvancedSupportInfo : IPluginSupportInfo
    {
        public string Author => "Przemysław (Car_Killer) Wolny";
        public string Copyright => "© 2025 Przemysław (Car_Killer) Wolny";
        public string DisplayName => "PNG (advanced)";
        public Version Version => GetType().Assembly.GetName().Version ?? new Version(0, 1, 0, 0);
        public Uri WebsiteUri => new Uri("https://example.com");
    }

    [PluginSupportInfo(typeof(PNGadvancedSupportInfo), DisplayName = "PNG (advanced)")]
    internal sealed class PngAdvancedPlugin : PropertyBasedFileType
    {
        // Preview caches (used when PDN's preview stream is empty or canceled)
        private static byte[] s_lastEncodedPng;
        private static byte[] s_lastPremulBgra; // length = w*h*4
        private static int s_lastW, s_lastH;

        internal PngAdvancedPlugin()
            : base(
                "PNG (advanced)",
                FileTypeFlags.SupportsSaving, // save-only; we still implement OnLoad for preview
                new[] { ".png", ".color.png", ".data.png", ".normal.png" })
        {
        }

        private enum PropertyNames { Mode, CompositeOnWhite, Compression }
        private enum ModeOptions { RGBA8, RGB8, Gray8 }

        public override PropertyCollection OnCreateSavePropertyCollection()
        {
            var props = new Property[]
            {
                StaticListChoiceProperty.CreateForEnum<ModeOptions>(PropertyNames.Mode, (int)ModeOptions.RGBA8, false),
                new BooleanProperty(PropertyNames.CompositeOnWhite, false),
                new Int32Property(PropertyNames.Compression, 6, 0, 9) // 0..9
            };
            return new PropertyCollection(props);
        }

        public override ControlInfo OnCreateSaveConfigUI(PropertyCollection props)
        {
            var ui = CreateDefaultSaveConfigUI(props);

            // Mode (radio buttons)
            ui.SetPropertyControlValue(PropertyNames.Mode, ControlInfoPropertyNames.DisplayName, "Output");
            ui.SetPropertyControlType(PropertyNames.Mode, PropertyControlType.RadioButton);
            var ctl = ui.FindControlForPropertyName(PropertyNames.Mode);
            ctl.SetValueDisplayName((Enum)Enum.ToObject(typeof(ModeOptions), (int)ModeOptions.RGBA8), "RGBA (8-bit)");
            ctl.SetValueDisplayName((Enum)Enum.ToObject(typeof(ModeOptions), (int)ModeOptions.RGB8), "RGB (8-bit)");
            ctl.SetValueDisplayName((Enum)Enum.ToObject(typeof(ModeOptions), (int)ModeOptions.Gray8), "Grayscale (8-bit)");

            // Composite checkbox
            ui.SetPropertyControlValue(PropertyNames.CompositeOnWhite, ControlInfoPropertyNames.DisplayName, string.Empty);
            ui.SetPropertyControlValue(PropertyNames.CompositeOnWhite, ControlInfoPropertyNames.Description, "When dropping alpha (RGB/Gray), composite on white");

            // Compression slider
            ui.SetPropertyControlType(PropertyNames.Compression, PropertyControlType.Slider);
            ui.SetPropertyControlValue(PropertyNames.Compression, ControlInfoPropertyNames.DisplayName, "Compression");
            ui.SetPropertyControlValue(PropertyNames.Compression, ControlInfoPropertyNames.Description, "0 = fastest/largest, 9 = smallest/slowest");
            ui.SetPropertyControlValue(PropertyNames.Compression, ControlInfoPropertyNames.SliderSmallChange, 1);
            ui.SetPropertyControlValue(PropertyNames.Compression, ControlInfoPropertyNames.SliderLargeChange, 2);

            return ui;
        }

        // PDN 4.x save entry point
        protected override unsafe void OnSaveT(Document input, Stream output, PropertyBasedSaveConfigToken token, Surface scratchSurface, ProgressEventHandler progressCallback)
        {
            var mode = (ModeOptions)(int)token.GetProperty<StaticListChoiceProperty>(PropertyNames.Mode).Value;
            bool compWhite = token.GetProperty<BooleanProperty>(PropertyNames.CompositeOnWhite).Value;
            int compLevel01 = token.GetProperty<Int32Property>(PropertyNames.Compression).Value;
            var z = MapCompressionLevel(compLevel01);

            // Flatten to scratch
            using (var ra = new RenderArgs(scratchSurface))
            {
                input.Render(ra, true); // clear then render
            }

            // Cache premultiplied BGRA pixels for preview fallback
            int w = scratchSurface.Width, h = scratchSurface.Height;
            var premul = new byte[w * h * 4];
            for (int y = 0; y < h; y++)
            {
                ColorBgra* src = scratchSurface.GetRowAddressUnchecked(y);
                int rowOff = y * w * 4;
                for (int x = 0; x < w; x++)
                {
                    var c = src[x]; // premultiplied in PDN 4.x
                    int o = rowOff + x * 4;
                    premul[o + 0] = c.B;
                    premul[o + 1] = c.G;
                    premul[o + 2] = c.R;
                    premul[o + 3] = c.A;
                }
            }
            s_lastPremulBgra = premul;
            s_lastW = w; s_lastH = h;

            // Encode to a byte[] (single write helps size compute and lets us cache for preview)
            byte[] png;
            switch (mode)
            {
                case ModeOptions.RGBA8:
                    png = Encode_RGBA8(scratchSurface, z, progressCallback);
                    break;
                case ModeOptions.RGB8:
                    png = Encode_RGB8(scratchSurface, compWhite, z, progressCallback);
                    break;
                case ModeOptions.Gray8:
                    png = Encode_Gray8(scratchSurface, compWhite, z, progressCallback);
                    break;
                default:
                    throw new InvalidOperationException();
            }

            // Cache for preview fallback
            s_lastEncodedPng = png;

            // Write once; swallow cancellation (preview cancels aggressively)
            try
            {
                output.Write(png, 0, png.Length);
                progressCallback?.Invoke(null, new ProgressEventArgs(1.0));
            }
            catch (OperationCanceledException)
            {
                // Let PDN spin a new preview without showing "(error)"
                return;
            }
        }

        // PDN 4.x will call OnLoad to preview the bytes it just encoded; be robust
        protected override unsafe Document OnLoad(Stream input)
        {
            byte[] blob = null;

            // 1) Try the stream PDN gives us
            try
            {
                if (input != null)
                {
                    try { if (input.CanSeek) input.Position = 0; } catch { /* ignore */ }
                    using (var ms = new MemoryStream())
                    {
                        input.CopyTo(ms);
                        blob = ms.ToArray();
                    }
                }
            }
            catch
            {
                // ignore
            }

            // 2) If empty, use last encoded PNG
            if (blob == null || blob.Length == 0)
                blob = s_lastEncodedPng;

            // 3) Try to decode with GDI+
            if (blob != null && blob.Length > 8)
            {
                try
                {
                    using (var ms = new MemoryStream(blob, writable: false))
                    using (var bmp = new Bitmap(ms))
                    {
                        return FromBitmapPremultiplied(bmp);
                    }
                }
                catch
                {
                    // fall through to pixel fallback
                }
            }

            // 4) Fallback: build a document from the last premultiplied pixels (no decode)
            if (s_lastPremulBgra != null && s_lastW > 0 && s_lastH > 0 &&
                s_lastPremulBgra.Length == s_lastW * s_lastH * 4)
            {
                var doc = new Document(s_lastW, s_lastH);
                var layer = new BitmapLayer(s_lastW, s_lastH);
                unsafe
                {
                    fixed (byte* p = s_lastPremulBgra)
                    {
                        for (int y = 0; y < s_lastH; y++)
                        {
                            ColorBgra* dst = layer.Surface.GetRowAddressUnchecked(y);
                            byte* src = p + (y * s_lastW * 4);
                            for (int x = 0; x < s_lastW; x++)
                            {
                                int o = x * 4;
                                dst[x] = ColorBgra.FromBgra(src[o + 0], src[o + 1], src[o + 2], src[o + 3]);
                            }
                        }
                    }
                }
                doc.Layers.Add(layer);
                return doc;
            }

            // 5) As a last resort, return a 1x1 placeholder to keep the dialog stable
            var ph = new Document(1, 1);
            ph.Layers.Add(new BitmapLayer(1, 1));
            return ph;
        }

        private static unsafe Document FromBitmapPremultiplied(Bitmap bmp)
        {
            var doc = new Document(bmp.Width, bmp.Height);
            var layer = new BitmapLayer(bmp.Width, bmp.Height);

            var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
            var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                for (int y = 0; y < bmp.Height; y++)
                {
                    byte* src = (byte*)data.Scan0 + (y * data.Stride);
                    ColorBgra* dst = layer.Surface.GetRowAddressUnchecked(y);
                    for (int x = 0; x < bmp.Width; x++)
                    {
                        int o = x * 4;
                        byte b = src[o + 0];
                        byte g = src[o + 1];
                        byte r = src[o + 2];
                        byte a = src[o + 3];

                        // GDI+ gives straight alpha; PDN 4 expects premultiplied
                        if (a != 255)
                        {
                            b = (byte)((b * a + 127) / 255);
                            g = (byte)((g * a + 127) / 255);
                            r = (byte)((r * a + 127) / 255);
                        }

                        dst[x] = ColorBgra.FromBgra(b, g, r, a);
                    }
                }
            }
            finally
            {
                bmp.UnlockBits(data);
            }

            doc.Layers.Add(layer);
            return doc;
        }

        private static CompressionLevel MapCompressionLevel(int v)
        {
            if (v <= 0) return CompressionLevel.NoCompression;
            if (v <= 3) return CompressionLevel.Fastest;
            return CompressionLevel.Optimal; // Framework DeflateStream has Optimal/Fastest/NoCompression
        }

        // Premultiplied-alpha helpers (PDN 4.x surfaces are premultiplied)
        private static void Unpremultiply(byte rPm, byte gPm, byte bPm, byte a, out byte r, out byte g, out byte b)
        {
            if (a == 0) { r = g = b = 0; }
            else if (a == 255) { r = rPm; g = gPm; b = bPm; }
            else
            {
                r = (byte)Math.Min(255, (rPm * 255 + (a / 2)) / a);
                g = (byte)Math.Min(255, (gPm * 255 + (a / 2)) / a);
                b = (byte)Math.Min(255, (bPm * 255 + (a / 2)) / a);
            }
        }

        private static byte CompositeOnWhiteFromPm(byte cPm, byte a)
        {
            int v = cPm + (255 - a); // c_out = c_pm + (1 - a)*255
            return (byte)(v > 255 ? 255 : v);
        }

        // Encoders return byte[] so we can cache for preview
        private static unsafe byte[] Encode_RGBA8(Surface s, CompressionLevel z, ProgressEventHandler progress)
        {
            int w = s.Width, h = s.Height;
            return PngWriter.EncodeToArray(w, h, 4, 6, z,
                (y, row, rowDataOffset, rawRowBytes) =>
                {
                    ColorBgra* src = s.GetRowAddressUnchecked(y);
                    int i = rowDataOffset;
                    for (int x = 0; x < w; x++)
                    {
                        var c = src[x]; // premultiplied BGRA
                        byte r, g, b;
                        Unpremultiply(c.R, c.G, c.B, c.A, out r, out g, out b);
                        row[i++] = r;
                        row[i++] = g;
                        row[i++] = b;
                        row[i++] = c.A;
                    }
                    if ((y & 63) == 0 && progress != null)
                        progress(null, new ProgressEventArgs(0.05 + 0.45 * (double)y / Math.Max(1, h)));
                },
                p => { if (progress != null) progress(null, new ProgressEventArgs(0.5 + 0.5 * p)); });
        }

        private static unsafe byte[] Encode_RGB8(Surface s, bool compWhite, CompressionLevel z, ProgressEventHandler progress)
        {
            int w = s.Width, h = s.Height;
            return PngWriter.EncodeToArray(w, h, 3, 2, z,
                (y, row, rowDataOffset, rawRowBytes) =>
                {
                    ColorBgra* src = s.GetRowAddressUnchecked(y);
                    int i = rowDataOffset;
                    for (int x = 0; x < w; x++)
                    {
                        var c = src[x]; // premultiplied BGRA

                        if (compWhite)
                        {
                            row[i++] = CompositeOnWhiteFromPm(c.R, c.A);
                            row[i++] = CompositeOnWhiteFromPm(c.G, c.A);
                            row[i++] = CompositeOnWhiteFromPm(c.B, c.A);
                        }
                        else
                        {
                            byte r, g, b;
                            Unpremultiply(c.R, c.G, c.B, c.A, out r, out g, out b);
                            row[i++] = r; row[i++] = g; row[i++] = b;
                        }
                    }
                    if ((y & 63) == 0 && progress != null)
                        progress(null, new ProgressEventArgs(0.05 + 0.45 * (double)y / Math.Max(1, h)));
                },
                p => { if (progress != null) progress(null, new ProgressEventArgs(0.5 + 0.5 * p)); });
        }

        private static unsafe byte[] Encode_Gray8(Surface s, bool compWhite, CompressionLevel z, ProgressEventHandler progress)
        {
            int w = s.Width, h = s.Height;
            return PngWriter.EncodeToArray(w, h, 1, 0, z,
                (y, row, rowDataOffset, rawRowBytes) =>
                {
                    ColorBgra* src = s.GetRowAddressUnchecked(y);
                    int i = rowDataOffset;
                    for (int x = 0; x < w; x++)
                    {
                        var c = src[x];

                        byte r, g, b;
                        if (compWhite)
                        {
                            r = CompositeOnWhiteFromPm(c.R, c.A);
                            g = CompositeOnWhiteFromPm(c.G, c.A);
                            b = CompositeOnWhiteFromPm(c.B, c.A);
                        }
                        else
                        {
                            Unpremultiply(c.R, c.G, c.B, c.A, out r, out g, out b);
                        }

                        int y8 = (int)(0.299 * r + 0.587 * g + 0.114 * b);
                        if (y8 < 0) y8 = 0; else if (y8 > 255) y8 = 255;
                        row[i++] = (byte)y8;
                    }
                    if ((y & 63) == 0 && progress != null)
                        progress(null, new ProgressEventArgs(0.05 + 0.45 * (double)y / Math.Max(1, h)));
                },
                p => { if (progress != null) progress(null, new ProgressEventArgs(0.5 + 0.5 * p)); });
        }

        // Minimal PNG writer for .NET Framework (correct zlib header, DeflateStream, Adler32)
        private static class PngWriter
        {
            private static readonly byte[] PngSig = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            private static readonly uint[] CrcTable = BuildCrcTable();

            internal delegate void RowFiller(int y, byte[] row, int rowDataOffset, int rawRowBytes);
            internal delegate void ProgressReporter(double progress);

            public static byte[] EncodeToArray(int width, int height, int bytesPerPixel, int colorType,
                                               CompressionLevel zLevel,
                                               RowFiller fillRow,
                                               ProgressReporter onCompressProgress)
            {
                using (var ms = new MemoryStream(Math.Max(1024, width * height * bytesPerPixel / 2)))
                {
                    Write(ms, width, height, bytesPerPixel, colorType, zLevel, fillRow, onCompressProgress);
                    return ms.ToArray();
                }
            }

            public static void Write(Stream output, int width, int height, int bytesPerPixel, int colorType,
                                     CompressionLevel zLevel, RowFiller fillRow, ProgressReporter onCompressProgress)
            {
                if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("image size must be positive");
                if (bytesPerPixel <= 0) throw new ArgumentOutOfRangeException("bytesPerPixel");
                if (!(colorType == 0 || colorType == 2 || colorType == 6)) throw new ArgumentOutOfRangeException("colorType");

                output.Write(PngSig, 0, PngSig.Length);

                var ihdr = new byte[13];
                WriteBE32(ihdr, 0, (uint)width);
                WriteBE32(ihdr, 4, (uint)height);
                ihdr[8] = 8;
                ihdr[9] = (byte)colorType;
                ihdr[10] = 0;
                ihdr[11] = 0;
                ihdr[12] = 0;
                WriteChunk(output, "IHDR", ihdr);

                int rawRowBytes = checked(width * bytesPerPixel);
                int scanRowBytes = rawRowBytes + 1;
                var row = new byte[scanRowBytes];

                using (var idatMem = new MemoryStream())
                {
                    // Canonical zlib header (FDICT=0)
                    WriteZlibHeader(idatMem);

                    uint adlerA = 1, adlerB = 0;
                    using (var deflate = new DeflateStream(idatMem, zLevel, leaveOpen: true))
                    {
                        int reportStep = Math.Max(1, height / 48);
                        for (int y = 0; y < height; y++)
                        {
                            row[0] = 0; // filter None
                            fillRow(y, row, 1, rawRowBytes);

                            deflate.Write(row, 0, scanRowBytes);

                            UpdateAdler32(row, 0, scanRowBytes, ref adlerA, ref adlerB);

                            if ((y % reportStep) == 0 || y == height - 1)
                                onCompressProgress?.Invoke(Math.Min(0.999, (double)(y + 1) / height));
                        }
                    }

                    // zlib trailer: Adler-32 big-endian
                    uint adler = (adlerB << 16) | adlerA;
                    var adlerBytes = new byte[4];
                    WriteBE32(adlerBytes, 0, adler);
                    idatMem.Write(adlerBytes, 0, 4);

                    WriteChunk(output, "IDAT", idatMem.ToArray());
                }

                WriteChunk(output, "IEND", new byte[0]);
            }

            private static void WriteChunk(Stream s, string type, byte[] data)
            {
                var len = new byte[4];
                WriteBE32(len, 0, (uint)(data != null ? data.Length : 0));
                s.Write(len, 0, 4);

                var typeBytes = Encoding.ASCII.GetBytes(type);
                s.Write(typeBytes, 0, 4);

                if (data != null && data.Length > 0) s.Write(data, 0, data.Length);

                uint crc = 0xFFFFFFFFu;
                crc = UpdateCrc(crc, typeBytes, 0, 4);
                if (data != null && data.Length > 0) crc = UpdateCrc(crc, data, 0, data.Length);
                crc ^= 0xFFFFFFFFu;

                var crcBytes = new byte[4];
                WriteBE32(crcBytes, 0, crc);
                s.Write(crcBytes, 0, 4);
            }

            // Always use 0x78 0x9C (default compression) for maximum decoder compatibility
            private static void WriteZlibHeader(Stream s)
            {
                s.WriteByte(0x78);
                s.WriteByte(0x9C);
            }

            private static void WriteBE32(byte[] buf, int offset, uint value)
            {
                buf[offset + 0] = (byte)(value >> 24);
                buf[offset + 1] = (byte)(value >> 16);
                buf[offset + 2] = (byte)(value >> 8);
                buf[offset + 3] = (byte)(value);
            }

            private static uint UpdateCrc(uint crc, byte[] data, int offset, int count)
            {
                for (int i = 0; i < count; i++)
                    crc = CrcTable[(crc ^ data[offset + i]) & 0xFF] ^ (crc >> 8);
                return crc;
            }

            private static uint[] BuildCrcTable()
            {
                var tbl = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                        c = ((c & 1) != 0) ? (0xEDB88320u ^ (c >> 1)) : (c >> 1);
                    tbl[n] = c;
                }
                return tbl;
            }

            private const uint AdlerMod = 65521;
            private static void UpdateAdler32(byte[] data, int offset, int count, ref uint a, ref uint b)
            {
                for (int i = 0; i < count; i++)
                {
                    a = (a + data[offset + i]) % AdlerMod;
                    b = (b + a) % AdlerMod;
                }
            }
        }
    }
}

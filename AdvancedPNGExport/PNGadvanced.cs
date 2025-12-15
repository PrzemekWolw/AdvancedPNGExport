using System;
using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Text;
using PaintDotNet;
using PaintDotNet.ComponentModel;
using PaintDotNet.IndirectUI;
using PaintDotNet.PropertySystem;
using PaintDotNet.Rendering;

namespace AdvancedPNGExport
{
    public sealed class PngAdvancedFactory : IFileTypeFactory
    {
        public FileType[] GetFileTypeInstances() => new[] { new PngAdvancedPlugin() };
    }

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
        internal PngAdvancedPlugin()
            : base("PNG (advanced)", new FileTypeOptions
            {
                LoadExtensions = Array.Empty<string>(),
                SaveExtensions = new[] { ".png",".color.png",".data.png",".normal.png" },
                SupportsLayers = false,
                SupportsCancellation = true
            })
        { }

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

            // Mode
            ui.SetPropertyControlValue(PropertyNames.Mode, ControlInfoPropertyNames.DisplayName, "Output");
            ui.SetPropertyControlType(PropertyNames.Mode, PropertyControlType.RadioButton);
            var ctl = ui.FindControlForPropertyName(PropertyNames.Mode);
            ctl.SetValueDisplayName((Enum)Enum.ToObject(typeof(ModeOptions), (int)ModeOptions.RGBA8), "RGBA (8-bit)");
            ctl.SetValueDisplayName((Enum)Enum.ToObject(typeof(ModeOptions), (int)ModeOptions.RGB8), "RGB (8-bit)");
            ctl.SetValueDisplayName((Enum)Enum.ToObject(typeof(ModeOptions), (int)ModeOptions.Gray8), "Grayscale (8-bit)");
            ui.SetPropertyControlValue(PropertyNames.Mode, ControlInfoPropertyNames.ShowHeaderLine, false);

            // Composite on white (when dropping alpha)
            ui.SetPropertyControlValue(PropertyNames.CompositeOnWhite, ControlInfoPropertyNames.DisplayName, string.Empty);
            ui.SetPropertyControlValue(PropertyNames.CompositeOnWhite, ControlInfoPropertyNames.Description, "When dropping alpha (RGB/Gray), composite on white");
            ui.SetPropertyControlValue(PropertyNames.CompositeOnWhite, ControlInfoPropertyNames.ShowHeaderLine, false);

            // Compression slider 0..9
            ui.SetPropertyControlType(PropertyNames.Compression, PropertyControlType.Slider);
            ui.SetPropertyControlValue(PropertyNames.Compression, ControlInfoPropertyNames.DisplayName, "Compression");
            ui.SetPropertyControlValue(PropertyNames.Compression, ControlInfoPropertyNames.Description, "0 = fastest/largest, 9 = smallest/slowest");
            ui.SetPropertyControlValue(PropertyNames.Compression, ControlInfoPropertyNames.SliderSmallChange, 1);
            ui.SetPropertyControlValue(PropertyNames.Compression, ControlInfoPropertyNames.SliderLargeChange, 2);
            // If your PDN build exposes UpDownIncrement, you can also set it:
            // ui.SetPropertyControlValue(PropertyNames.Compression, ControlInfoPropertyNames.UpDownIncrement, 1);
            ui.SetPropertyControlValue(PropertyNames.Compression, ControlInfoPropertyNames.ShowHeaderLine, false);
            return ui;
        }

        protected override unsafe void OnSaveT(Document input, Stream output, PropertyBasedSaveConfigToken token, Surface scratch, ProgressEventHandler progress)
        {
            var mode = (ModeOptions)(int)token.GetProperty<StaticListChoiceProperty>(PropertyNames.Mode).Value;
            bool compWhite = token.GetProperty<BooleanProperty>(PropertyNames.CompositeOnWhite).Value;
            int compLevel01 = token.GetProperty<Int32Property>(PropertyNames.Compression).Value;
            var zLevel = MapCompressionLevel(compLevel01);

            // Render document to the scratch surface
            input.CreateRenderer().Render(scratch.AsRegionPtr(), Point2Int32.Zero);

            // Encode PNG using our in-file writer
            switch (mode)
            {
                case ModeOptions.RGBA8:
                    SavePng_RGBA8(scratch, output, zLevel, progress);
                    break;

                case ModeOptions.RGB8:
                    SavePng_RGB8(scratch, output, compWhite, zLevel, progress);
                    break;

                case ModeOptions.Gray8:
                    SavePng_Gray8(scratch, output, compWhite, zLevel, progress);
                    break;
            }
        }

        private static CompressionLevel MapCompressionLevel(int v)
        {
            if (v <= 0) return CompressionLevel.NoCompression;
            if (v <= 3) return CompressionLevel.Fastest;
            if (v <= 7) return CompressionLevel.Optimal;
            return CompressionLevel.SmallestSize;
        }

        // RGB (24bpp), color type 2
        private static unsafe void SavePng_RGB8(Surface s, Stream output, bool compWhite, CompressionLevel z, ProgressEventHandler progress)
        {
            int w = s.Width, h = s.Height;
            int bpp = 3;

            PngWriter.Write(output, w, h, bpp, colorType: 2, z, (y, dstRow) =>
            {
                // dstRow is exactly width*bpp bytes (no filter byte)
                ColorBgra* src = s.GetRowPointerUnchecked(y);
                int i = 0;
                for (int x = 0; x < w; x++)
                {
                    var c = src[x];
                    if (compWhite && c.A < 255)
                    {
                        byte a = c.A, inv = (byte)(255 - a);
                        dstRow[i++] = (byte)((c.R * a + 255 * inv) / 255);
                        dstRow[i++] = (byte)((c.G * a + 255 * inv) / 255);
                        dstRow[i++] = (byte)((c.B * a + 255 * inv) / 255);
                    }
                    else
                    {
                        dstRow[i++] = c.R; dstRow[i++] = c.G; dstRow[i++] = c.B;
                    }
                }

                if ((y & 63) == 0 && progress is not null)
                    progress(null, new ProgressEventArgs(0.05 + 0.45 * (double)y / Math.Max(1, h)));
            },
            onCompressProgress: p => progress?.Invoke(null, new ProgressEventArgs(0.5 + 0.5 * p)));
        }

        // RGBA (32bpp), color type 6
        private static unsafe void SavePng_RGBA8(Surface s, Stream output, CompressionLevel z, ProgressEventHandler progress)
        {
            int w = s.Width, h = s.Height;
            int bpp = 4;

            PngWriter.Write(output, w, h, bpp, colorType: 6, z, (y, dstRow) =>
            {
                ColorBgra* src = s.GetRowPointerUnchecked(y);
                int i = 0;
                for (int x = 0; x < w; x++)
                {
                    var c = src[x];
                    dstRow[i++] = c.R;
                    dstRow[i++] = c.G;
                    dstRow[i++] = c.B;
                    dstRow[i++] = c.A; // PNG expects straight alpha, PDN stores straight alpha
                }

                if ((y & 63) == 0 && progress is not null)
                    progress(null, new ProgressEventArgs(0.05 + 0.45 * (double)y / Math.Max(1, h)));
            },
            onCompressProgress: p => progress?.Invoke(null, new ProgressEventArgs(0.5 + 0.5 * p)));
        }

        // Grayscale (8bpp), color type 0
        private static unsafe void SavePng_Gray8(Surface s, Stream output, bool compWhite, CompressionLevel z, ProgressEventHandler progress)
        {
            int w = s.Width, h = s.Height;
            int bpp = 1;

            PngWriter.Write(output, w, h, bpp, colorType: 0, z, (y, dstRow) =>
            {
                // dstRow has width bytes
                ColorBgra* src = s.GetRowPointerUnchecked(y);
                for (int x = 0; x < w; x++)
                {
                    var c = src[x];
                    byte g = (byte)Math.Clamp(0.299 * c.R + 0.587 * c.G + 0.114 * c.B, 0, 255);
                    if (compWhite && c.A < 255)
                    {
                        byte inv = (byte)(255 - c.A);
                        g = (byte)((g * c.A + 255 * inv) / 255);
                    }
                    dstRow[x] = g;
                }

                if ((y & 63) == 0 && progress is not null)
                    progress(null, new ProgressEventArgs(0.05 + 0.45 * (double)y / Math.Max(1, h)));
            },
            onCompressProgress: p => progress?.Invoke(null, new ProgressEventArgs(0.5 + 0.5 * p)));
        }

        protected override Document OnLoad(Stream input) => new Document(1, 1);

        // Minimal PNG writer that supports:
        // - color type 0 (Gray8), 2 (RGB8), 6 (RGBA8)
        // - filter type 0 (None)
        // - single IDAT chunk (compressed with ZLibStream)
        private static class PngWriter
        {
            private static readonly byte[] PngSig = new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 };
            private static readonly uint[] CrcTable = BuildCrcTable();

            // fillRow: caller fills pixel bytes for the row (without filter byte)
            public static void Write(Stream output, int width, int height, int bytesPerPixel, int colorType,
                                     CompressionLevel zLevel,
                                     Action<int, Span<byte>> fillRow,
                                     Action<double>? onCompressProgress = null)
            {
                if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException("image size must be positive");
                if (bytesPerPixel <= 0) throw new ArgumentOutOfRangeException(nameof(bytesPerPixel));
                if (colorType is not (0 or 2 or 6)) throw new ArgumentOutOfRangeException(nameof(colorType));

                // Write PNG signature
                output.Write(PngSig, 0, PngSig.Length);

                // IHDR
                Span<byte> ihdr = stackalloc byte[13];
                BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
                BinaryPrimitives.WriteUInt32BigEndian(ihdr.Slice(4, 4), (uint)height);
                ihdr[8] = 8;             // bit depth (8)
                ihdr[9] = (byte)colorType;
                ihdr[10] = 0;             // compression method
                ihdr[11] = 0;             // filter method
                ihdr[12] = 0;             // interlace method (none)
                WriteChunk(output, "IHDR", ihdr);

                // Compress scanlines: each row is [filter=0][raw row bytes]
                int rawRowBytes = checked(width * bytesPerPixel);
                int scanRowBytes = rawRowBytes + 1;

                using var mem = new MemoryStream();
                using (var z = new ZLibStream(mem, zLevel, leaveOpen: true))
                {
                    var row = new byte[scanRowBytes];
                    for (int y = 0; y < height; y++)
                    {
                        row[0] = 0; // filter type 0
                        fillRow(y, row.AsSpan(1, rawRowBytes));
                        z.Write(row, 0, scanRowBytes);

                        if (onCompressProgress != null && (y % Math.Max(1, height / 48) == 0 || y == height - 1))
                        {
                            onCompressProgress(Math.Min(0.999, (double)(y + 1) / height));
                        }
                    }
                }
                byte[] idat = mem.ToArray();
                WriteChunk(output, "IDAT", idat);

                // IEND
                WriteChunk(output, "IEND", ReadOnlySpan<byte>.Empty);
            }

            private static void WriteChunk(Stream s, string type, ReadOnlySpan<byte> data)
            {
                Span<byte> len = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(len, (uint)data.Length);
                s.Write(len);

                Span<byte> typeBytes = stackalloc byte[4];
                Encoding.ASCII.GetBytes(type, typeBytes);
                s.Write(typeBytes);

                if (!data.IsEmpty)
                {
                    s.Write(data);
                }

                uint crc = 0xFFFFFFFFu;
                crc = UpdateCrc(crc, typeBytes);
                if (!data.IsEmpty) crc = UpdateCrc(crc, data);
                crc ^= 0xFFFFFFFFu;

                Span<byte> crcBytes = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(crcBytes, crc);
                s.Write(crcBytes);
            }

            private static uint UpdateCrc(uint crc, ReadOnlySpan<byte> data)
            {
                foreach (byte b in data)
                {
                    crc = CrcTable[(crc ^ b) & 0xFF] ^ (crc >> 8);
                }
                return crc;
            }

            private static uint[] BuildCrcTable()
            {
                var tbl = new uint[256];
                for (uint n = 0; n < 256; n++)
                {
                    uint c = n;
                    for (int k = 0; k < 8; k++)
                    {
                        c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : (c >> 1);
                    }
                    tbl[n] = c;
                }
                return tbl;
            }
        }
    }
}
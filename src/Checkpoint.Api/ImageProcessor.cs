using System.Diagnostics;
using System.Reflection;
using NetVips;

namespace Checkpoint.Api;

public sealed class ImageSettings
{
    public int MaxFileBytes { get; set; } = 10 * 1024 * 1024;
    public long MaxPixels { get; set; } = 40_000_000;
    public int Concurrency { get; set; } = 2;
}
public sealed class RateLimitSettings
{
    public int RequestsPerMinute { get; set; } = 1800;
    public int VotesPerMinute { get; set; } = 300;
    public int UploadsPerMinute { get; set; } = 60;
}
public sealed record ProcessedImage(string MediaType, byte[] Thumbnail);

public sealed class ImageProcessor : IDisposable
{
    // The only accepted formats, matched by the loader libvips picks from the file content.
    // Anything else libvips can read (GIF, TIFF, BMP, PSD, SVG, PDF, ...) is refused.
    private static readonly Dictionary<string, string> Loaders = new()
    {
        ["VipsForeignLoadJpegBuffer"] = "image/jpeg",
        ["VipsForeignLoadPngBuffer"] = "image/png",
        ["VipsForeignLoadWebpBuffer"] = "image/webp",
        ["VipsForeignLoadHeifBuffer"] = "image/heic", // refined to image/avif by compression
        ["VipsForeignLoadJp2kBuffer"] = "image/jp2",
    };

    private readonly SemaphoreSlim slots;
    private readonly ImageSettings settings;
    private readonly ILogger<ImageProcessor> logger;
    public bool Ready { get; private set; }
    /// <summary>True only when a real HEVC HEIC decoded successfully at startup (needs libheif with libde265).</summary>
    public bool SupportsHeic { get; private set; }

    public ImageProcessor(ImageSettings settings, ILogger<ImageProcessor> logger)
    {
        this.settings = settings;
        this.logger = logger;
        if (settings.Concurrency is < 1 or > 16 || settings.MaxFileBytes < 1 || settings.MaxPixels < 1)
            throw new InvalidOperationException("Invalid image resource limits.");
        slots = new(settings.Concurrency);
        NetVips.NetVips.Concurrency = settings.Concurrency;
        Cache.Max = 0;
        Cache.MaxMem = 32 * 1024 * 1024;
    }

    public void Probe()
    {
        try
        {
            using var input = Image.Black(8, 8, bands: 3);
            var bytes = Encode(input);
            using var decoded = Image.NewFromBuffer(bytes);
            Ready = decoded.Width == 8 && decoded.Height == 8;
            SupportsHeic = Ready && CanDecodeHeic();
            logger.LogInformation("Image codec libvips {Version}; AVIF encoder ready {Ready}; HEIC (HEVC) decoding {Heic}",
                $"{NetVips.NetVips.Version(0)}.{NetVips.NetVips.Version(1)}.{NetVips.NetVips.Version(2)}", Ready, SupportsHeic);
            if (Ready && !SupportsHeic)
                logger.LogWarning("HEIC uploads will be rejected: this libvips/libheif build has no HEVC decoder (install libheif-plugin-libde265).");
        }
        catch (Exception ex) when (ex is VipsException or DllNotFoundException or TypeInitializationException)
        {
            Ready = false;
            logger.LogError("AVIF codec initialization failed ({ErrorType})", ex.GetType().Name);
        }
    }

    private static bool CanDecodeHeic()
    {
        try
        {
            using var stream = typeof(ImageProcessor).Assembly.GetManifestResourceStream("heic-probe.heic")!;
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            using var image = Image.NewFromBuffer(memory.ToArray(), failOn: Enums.FailOn.Error);
            using var pixels = image.CopyMemory();
            return pixels.Width > 0;
        }
        catch (Exception ex) when (ex is VipsException) { return false; }
    }

    public async Task<ProcessedImage> Process(byte[] bytes, CancellationToken ct)
    {
        if (bytes.Length > settings.MaxFileBytes) throw new ApiException(413, "FILE_TOO_LARGE", "Arquivo maior que o limite permitido.");
        if (!Ready) throw new ApiException(503, "SERVICE_UNAVAILABLE", "Processamento de imagens indisponível.");
        if (!await slots.WaitAsync(0, ct)) throw new ApiException(503, "PROCESSING_BUSY", "Tente enviar a foto novamente em instantes.");
        try
        {
            return await Task.Run(() => ProcessCore(bytes), ct);
        }
        finally { slots.Release(); }
    }

    private string Detect(byte[] bytes)
    {
        string? loader;
        try { loader = Image.FindLoadBuffer(bytes); }
        catch (VipsException) { loader = null; }
        if (loader is not null && Loaders.TryGetValue(loader, out var mediaType))
        {
            if (loader == "VipsForeignLoadHeifBuffer" && !SupportsHeic && !IsAvifBrand(bytes))
                throw new ApiException(415, "UNSUPPORTED_IMAGE_FORMAT", "Este servidor não consegue ler HEIC. Envie JPEG, PNG, WebP ou AVIF.");
            return mediaType;
        }
        throw new ApiException(415, "UNSUPPORTED_IMAGE_FORMAT", "Formato de imagem não suportado. Use JPEG, PNG, HEIC, WebP, AVIF ou JPEG 2000.");
    }

    private static bool IsAvifBrand(byte[] bytes)
    {
        if (bytes.Length < 16 || System.Text.Encoding.ASCII.GetString(bytes, 4, 4) != "ftyp") return false;
        var size = (int)Math.Min((uint)bytes.Length, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes));
        for (var offset = 8; offset + 4 <= size; offset += 4)
            if (offset != 12 && System.Text.Encoding.ASCII.GetString(bytes, offset, 4) is "avif" or "avis") return true;
        return false;
    }

    private ProcessedImage ProcessCore(byte[] bytes)
    {
        var mediaType = Detect(bytes);
        var timer = Stopwatch.StartNew();
        try
        {
            // FailOn.Error rejects corrupt/truncated files but tolerates benign warnings (e.g. the nclx profile note on iPhone HEIC).
            // Only the first frame is read, so animated WebP and multi-image HEIC use their primary image.
            using var input = Image.NewFromBuffer(bytes, access: Enums.Access.Sequential, failOn: Enums.FailOn.Error);
            if ((long)input.Width * input.Height > settings.MaxPixels)
                throw new ApiException(422, "IMAGE_DIMENSIONS_EXCEEDED", "A imagem excede o limite de pixels.");
            if (mediaType == "image/heic" && input.GetTypeOf("heif-compression") != 0 && (string)input.Get("heif-compression") == "av1")
                mediaType = "image/avif";
            // Force a complete decode, including areas that the cover crop might otherwise skip.
            using var decoded = input.CopyMemory();
            using var oriented = decoded.Autorot();
            using var thumbnail = oriented.ThumbnailImage(360, height: 330, size: Enums.Size.Both, crop: Enums.Interesting.Centre);
            using var profiled = ToSrgb(thumbnail);
            using var flat = profiled.HasAlpha() ? profiled.Flatten(background: [255.0, 255.0, 255.0]) : profiled.Copy();
            using var rgb = flat.Interpretation == Enums.Interpretation.Srgb ? flat.Copy() : flat.Colourspace(Enums.Interpretation.Srgb);
            var output = Encode(rgb);
            logger.LogInformation("AVIF thumbnail from {MediaType}: {ElapsedMs} ms, {Bytes} bytes, 360x330", mediaType, timer.ElapsedMilliseconds, output.Length);
            return new(mediaType, output);
        }
        catch (VipsException)
        {
            throw new ApiException(422, "INVALID_IMAGE", "Não foi possível decodificar a imagem completa.");
        }
    }

    // Applies an embedded ICC profile (Display P3 from iPhones, Adobe RGB, ...) and then discards it. Done after the
    // reduction so it only touches 360x330 pixels. thumbnail_image has no profile option before libvips 8.16.
    private static Image ToSrgb(Image image)
    {
        if (image.GetTypeOf("icc-profile-data") == 0) return image.Copy();
        try { return image.IccTransform("srgb", embedded: true); }
        catch (VipsException) { return image.Copy(); } // unusable profile: keep the pixels unchanged
    }

    // Compression is explicit: libvips defaults heifsave to HEVC, which many builds cannot encode.
    public static byte[] Encode(Image image) => image.WriteToBuffer(".avif", new VOption { { "compression", Enums.ForeignHeifCompression.Av1 }, { "Q", 60 }, { "effort", 4 }, { "strip", true } });

    public void Dispose() => slots.Dispose();
}

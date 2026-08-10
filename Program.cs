using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddOpenApi();

builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp", policy =>
            policy.WithOrigins(
                    "http://localhost:5173",  // local dev
                    "https://darkroom-livid.vercel.app"
                )
                .AllowAnyHeader()
                .AllowAnyMethod());
});

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
    app.UseSwaggerUI(options =>
    {
        options.SwaggerEndpoint("/openapi/v1.json", "Darkroom API");
    });
}

app.UseHttpsRedirection();
app.UseCors("AllowReactApp");

app.MapPost("/resize", async ([FromForm] ResizeRequest request) =>
{
    using var stream = request.File.OpenReadStream();
    using var bitmap = SkiaSharp.SKBitmap.Decode(stream);

    var mode = (request.Mode ?? "fit").ToLowerInvariant();

    using var resized = ResizeBitmap(bitmap, request.Width, request.Height, mode);
    using var image = SkiaSharp.SKImage.FromBitmap(resized);
    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);
    var fileName = Path.GetFileNameWithoutExtension(request.File.FileName);

    return Results.File(data.ToArray(), "image/png", fileName + "-resized.png");
})
.DisableAntiforgery();

app.MapPost("/compress", async ([FromForm] CompressRequest request) =>
{
    using var stream = request.File.OpenReadStream();
    using var bitmap = SkiaSharp.SKBitmap.Decode(stream);
    using var image = SkiaSharp.SKImage.FromBitmap(bitmap);
    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Jpeg, request.Quality);

    var fileName = Path.GetFileNameWithoutExtension(request.File.FileName);

    return Results.File(data.ToArray(), "image/jpeg", fileName + "-compressed.jpg");
})
.DisableAntiforgery();

app.MapPost("/convert", async ([FromForm] ConvertRequest request) =>
{
    var format = (request.Format ?? "png").ToLowerInvariant();

    if (format is "avif")
    {
        return Results.BadRequest(new
        {
            error = "AVIF isn't supported by the server's SkiaSharp build. Supported formats: jpeg, png, webp."
        });
    }

    if (format is not ("jpeg" or "jpg" or "png" or "webp"))
    {
        return Results.BadRequest(new { error = $"Unsupported format '{format}'. Supported formats: jpeg, png, webp." });
    }

    using var stream = request.File.OpenReadStream();
    using var bitmap = SkiaSharp.SKBitmap.Decode(stream);
    using var image = SkiaSharp.SKImage.FromBitmap(bitmap);

    var quality = request.Quality is > 0 and <= 100 ? request.Quality!.Value : 90;

    var (encodedFormat, contentType, extension) = format switch
    {
        "jpeg" or "jpg" => (SkiaSharp.SKEncodedImageFormat.Jpeg, "image/jpeg", "jpg"),
        "webp" => (SkiaSharp.SKEncodedImageFormat.Webp, "image/webp", "webp"),
        _ => (SkiaSharp.SKEncodedImageFormat.Png, "image/png", "png"),
    };

    using var data = image.Encode(encodedFormat, quality);
    var fileName = Path.GetFileNameWithoutExtension(request.File.FileName);

    return Results.File(data.ToArray(), contentType, $"{fileName}-converted.{extension}");
})
.DisableAntiforgery();

app.MapPost("/add-filter", async ([FromForm] FilterRequest request) =>
{
    var filter = (request.Filter ?? "").ToLowerInvariant();
    var matrix = GetFilterMatrix(filter);

    if (matrix is null)
    {
        return Results.BadRequest(new
        {
            error = $"Unsupported filter '{filter}'. Supported filters: grayscale, sepia, vintage, invert, blackandwhite."
        });
    }

    using var stream = request.File.OpenReadStream();
    using var bitmap = SkiaSharp.SKBitmap.Decode(stream);
    using var filtered = ApplyColorMatrix(bitmap, matrix);
    using var image = SkiaSharp.SKImage.FromBitmap(filtered);
    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

    var fileName = Path.GetFileNameWithoutExtension(request.File.FileName);
    return Results.File(data.ToArray(), "image/png", $"{fileName}-{filter}.png");
})
.DisableAntiforgery();

app.MapPost("/watermark", async ([FromForm] WatermarkRequest request) =>
{
    if (string.IsNullOrWhiteSpace(request.Text) && request.WatermarkFile is null)
    {
        return Results.BadRequest(new { error = "Provide either Text or a WatermarkFile." });
    }

    using var stream = request.File.OpenReadStream();
    using var bitmap = SkiaSharp.SKBitmap.Decode(stream);

    SkiaSharp.SKBitmap? watermarkBitmap = null;
    if (request.WatermarkFile is not null)
    {
        using var wmStream = request.WatermarkFile.OpenReadStream();
        watermarkBitmap = SkiaSharp.SKBitmap.Decode(wmStream);
    }

    var position = (request.Position ?? "bottomright").ToLowerInvariant();
    var opacity = request.Opacity is >= 0 and <= 100 ? request.Opacity!.Value : 60;

    using var result = ApplyWatermark(bitmap, request.Text, watermarkBitmap, position, opacity, request.FontSize ?? 32);
    watermarkBitmap?.Dispose();

    using var image = SkiaSharp.SKImage.FromBitmap(result);
    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

    var fileName = Path.GetFileNameWithoutExtension(request.File.FileName);
    return Results.File(data.ToArray(), "image/png", $"{fileName}-watermarked.png");
})
.DisableAntiforgery();

app.MapPost("/rotate", async ([FromForm] RotateRequest request) =>
{
    using var stream = request.File.OpenReadStream();
    using var bitmap = SkiaSharp.SKBitmap.Decode(stream);
    using var rotated = RotateBitmap(bitmap, request.Degrees);
    using var image = SkiaSharp.SKImage.FromBitmap(rotated);
    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

    var fileName = Path.GetFileNameWithoutExtension(request.File.FileName);
    return Results.File(data.ToArray(), "image/png", $"{fileName}-rotated.png");
})
.DisableAntiforgery();

app.MapPost("/round-corners", async ([FromForm] RoundCornersRequest request) =>
{
    using var stream = request.File.OpenReadStream();
    using var bitmap = SkiaSharp.SKBitmap.Decode(stream);
    using var rounded = RoundCorners(bitmap, Math.Max(0, request.Radius));
    using var image = SkiaSharp.SKImage.FromBitmap(rounded);
    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

    var fileName = Path.GetFileNameWithoutExtension(request.File.FileName);
    return Results.File(data.ToArray(), "image/png", $"{fileName}-rounded.png");
})
.DisableAntiforgery();

app.MapPost("/add-background", async ([FromForm] AddBackgroundRequest request) =>
{
    if (!TryParseHexColor(request.Color, out var color))
    {
        return Results.BadRequest(new { error = $"Invalid color '{request.Color}'. Use a hex value like #FFFFFF or #FF0000FF." });
    }

    using var stream = request.File.OpenReadStream();
    using var bitmap = SkiaSharp.SKBitmap.Decode(stream);
    using var result = AddBackground(bitmap, color);
    using var image = SkiaSharp.SKImage.FromBitmap(result);
    using var data = image.Encode(SkiaSharp.SKEncodedImageFormat.Png, 100);

    var fileName = Path.GetFileNameWithoutExtension(request.File.FileName);
    return Results.File(data.ToArray(), "image/png", $"{fileName}-with-bg.png");
})
.DisableAntiforgery();

app.Run();

static SkiaSharp.SKBitmap ResizeBitmap(SkiaSharp.SKBitmap bitmap, int width, int height, string mode)
{
    width = Math.Max(1, width);
    height = Math.Max(1, height);

    switch (mode)
    {
        case "exact":
        {
            return bitmap.Resize(
                new SkiaSharp.SKImageInfo(width, height),
                SkiaSharp.SKSamplingOptions.Default);
        }

        case "crop":
        {
            var widthScale = (double)width / bitmap.Width;
            var heightScale = (double)height / bitmap.Height;
            var scale = Math.Max(widthScale, heightScale);

            var scaledWidth = Math.Max(1, (int)Math.Ceiling(bitmap.Width * scale));
            var scaledHeight = Math.Max(1, (int)Math.Ceiling(bitmap.Height * scale));

            using var scaled = bitmap.Resize(
                new SkiaSharp.SKImageInfo(scaledWidth, scaledHeight),
                SkiaSharp.SKSamplingOptions.Default);

            // Crop the overflow evenly from the center
            var cropX = Math.Max(0, (scaledWidth - width) / 2);
            var cropY = Math.Max(0, (scaledHeight - height) / 2);
            var sourceRect = SkiaSharp.SKRectI.Create(cropX, cropY, width, height);
            var destRect = SkiaSharp.SKRect.Create(0, 0, width, height);

            var cropped = new SkiaSharp.SKBitmap(width, height);
            using (var canvas = new SkiaSharp.SKCanvas(cropped))
            {
                canvas.Clear(SkiaSharp.SKColors.Transparent);
                canvas.DrawBitmap(scaled, sourceRect, destRect, SkiaSharp.SKSamplingOptions.Default);
            }

            return cropped;
        }

        case "fit":
        default:
        {
            var widthScale = (double)width / bitmap.Width;
            var heightScale = (double)height / bitmap.Height;
            var scale = Math.Min(widthScale, heightScale);

            var newWidth = Math.Max(1, (int)(bitmap.Width * scale));
            var newHeight = Math.Max(1, (int)(bitmap.Height * scale));

            return bitmap.Resize(
                new SkiaSharp.SKImageInfo(newWidth, newHeight),
                SkiaSharp.SKSamplingOptions.Default);
        }
    }
}

// Returns a 4x5 color matrix (row-major, as SkiaSharp expects) for a named filter,
// or null if the filter name isn't recognized.
static float[]? GetFilterMatrix(string filter) => filter switch
{
    "grayscale" or "blackandwhite" => new float[]
    {
        0.21f, 0.72f, 0.07f, 0, 0,
        0.21f, 0.72f, 0.07f, 0, 0,
        0.21f, 0.72f, 0.07f, 0, 0,
        0,     0,     0,     1, 0,
    },
    "sepia" => new float[]
    {
        0.393f, 0.769f, 0.189f, 0, 0,
        0.349f, 0.686f, 0.168f, 0, 0,
        0.272f, 0.534f, 0.131f, 0, 0,
        0,      0,      0,      1, 0,
    },
    "vintage" => new float[]
    {
        0.35f, 0.65f, 0.16f, 0, 8,
        0.30f, 0.58f, 0.14f, 0, 8,
        0.24f, 0.45f, 0.11f, 0, 8,
        0,     0,     0,     1, 0,
    },
    "invert" => new float[]
    {
        -1, 0, 0, 0, 255,
        0, -1, 0, 0, 255,
        0, 0, -1, 0, 255,
        0, 0, 0, 1, 0,
    },
    _ => null,
};

static SkiaSharp.SKBitmap ApplyColorMatrix(SkiaSharp.SKBitmap bitmap, float[] matrix)
{
    var result = new SkiaSharp.SKBitmap(bitmap.Width, bitmap.Height);
    using var canvas = new SkiaSharp.SKCanvas(result);
    using var paint = new SkiaSharp.SKPaint
    {
        ColorFilter = SkiaSharp.SKColorFilter.CreateColorMatrix(matrix),
    };

    canvas.Clear(SkiaSharp.SKColors.Transparent);
    canvas.DrawBitmap(bitmap, 0, 0, new SkiaSharp.SKSamplingOptions(), paint);
    return result;
}

// Draws a semi-transparent text and/or image watermark near one corner (or center) of the image.
static SkiaSharp.SKBitmap ApplyWatermark(
    SkiaSharp.SKBitmap bitmap,
    string? text,
    SkiaSharp.SKBitmap? watermarkImage,
    string position,
    int opacityPercent,
    float fontSize)
{
    var result = new SkiaSharp.SKBitmap(bitmap.Width, bitmap.Height);
    using var canvas = new SkiaSharp.SKCanvas(result);
    canvas.Clear(SkiaSharp.SKColors.Transparent);
    canvas.DrawBitmap(bitmap, 0, 0, new SkiaSharp.SKSamplingOptions());

    var alpha = (byte)Math.Clamp(opacityPercent * 255 / 100, 0, 255);
    const float margin = 24f;

    if (watermarkImage is not null)
    {
        // Scale the watermark image to at most 25% of the base image's width.
        var maxWidth = bitmap.Width * 0.25f;
        var scale = Math.Min(1f, maxWidth / watermarkImage.Width);
        var wmWidth = watermarkImage.Width * scale;
        var wmHeight = watermarkImage.Height * scale;

        var (x, y) = GetPosition(position, bitmap.Width, bitmap.Height, wmWidth, wmHeight, margin);

        using var paint = new SkiaSharp.SKPaint { Color = SkiaSharp.SKColors.White.WithAlpha(alpha) };
        var destRect = SkiaSharp.SKRect.Create(x, y, wmWidth, wmHeight);
        var sampling = new SkiaSharp.SKSamplingOptions(SkiaSharp.SKFilterMode.Linear, SkiaSharp.SKMipmapMode.None);
        canvas.DrawBitmap(watermarkImage, destRect, sampling, paint);
    }

    if (!string.IsNullOrWhiteSpace(text))
    {
        using var font = new SkiaSharp.SKFont(SkiaSharp.SKTypeface.Default, fontSize);
        using var textPaint = new SkiaSharp.SKPaint
        {
            Color = SkiaSharp.SKColors.White.WithAlpha(alpha),
            IsAntialias = true,
        };

        var textWidth = font.MeasureText(text);
        var textHeight = fontSize;
        var (x, y) = GetPosition(position, bitmap.Width, bitmap.Height, textWidth, textHeight, margin);

        canvas.DrawText(text, x, y + textHeight, SkiaSharp.SKTextAlign.Left, font, textPaint);
    }

    return result;
}

static (float x, float y) GetPosition(string position, int canvasWidth, int canvasHeight, float itemWidth, float itemHeight, float margin) => position switch
{
    "topleft" => (margin, margin),
    "topright" => (canvasWidth - itemWidth - margin, margin),
    "bottomleft" => (margin, canvasHeight - itemHeight - margin),
    "center" => ((canvasWidth - itemWidth) / 2f, (canvasHeight - itemHeight) / 2f),
    _ /* bottomright */ => (canvasWidth - itemWidth - margin, canvasHeight - itemHeight - margin),
};

// Rotates a bitmap by an arbitrary angle (degrees, clockwise), expanding the canvas
// so nothing is clipped, filling the new corners with transparency.
static SkiaSharp.SKBitmap RotateBitmap(SkiaSharp.SKBitmap bitmap, float degrees)
{
    var radians = degrees * Math.PI / 180.0;
    var cos = Math.Abs(Math.Cos(radians));
    var sin = Math.Abs(Math.Sin(radians));

    var newWidth = (int)Math.Ceiling(bitmap.Width * cos + bitmap.Height * sin);
    var newHeight = (int)Math.Ceiling(bitmap.Width * sin + bitmap.Height * cos);
    newWidth = Math.Max(1, newWidth);
    newHeight = Math.Max(1, newHeight);

    var result = new SkiaSharp.SKBitmap(newWidth, newHeight);
    using var canvas = new SkiaSharp.SKCanvas(result);
    canvas.Clear(SkiaSharp.SKColors.Transparent);

    canvas.Translate(newWidth / 2f, newHeight / 2f);
    canvas.RotateDegrees(degrees);
    canvas.Translate(-bitmap.Width / 2f, -bitmap.Height / 2f);

    using var paint = new SkiaSharp.SKPaint { IsAntialias = true };
    canvas.DrawBitmap(bitmap, 0, 0, new SkiaSharp.SKSamplingOptions(), paint);

    return result;
}

// Clips the image to a rounded-rectangle mask of the given corner radius.
static SkiaSharp.SKBitmap RoundCorners(SkiaSharp.SKBitmap bitmap, float radius)
{
    var result = new SkiaSharp.SKBitmap(bitmap.Width, bitmap.Height);
    using var canvas = new SkiaSharp.SKCanvas(result);
    canvas.Clear(SkiaSharp.SKColors.Transparent);

    var rect = SkiaSharp.SKRect.Create(0, 0, bitmap.Width, bitmap.Height);
    using var builder = new SkiaSharp.SKPathBuilder();
    builder.AddRoundRect(rect, radius, radius);
    using var path = builder.Detach();

    canvas.ClipPath(path, antialias: true);
    canvas.DrawBitmap(bitmap, 0, 0, new SkiaSharp.SKSamplingOptions());

    return result;
}

// Draws a solid color behind the image — useful for images that have transparency (e.g. PNGs).
static SkiaSharp.SKBitmap AddBackground(SkiaSharp.SKBitmap bitmap, SkiaSharp.SKColor color)
{
    var result = new SkiaSharp.SKBitmap(bitmap.Width, bitmap.Height);
    using var canvas = new SkiaSharp.SKCanvas(result);
    canvas.Clear(color);
    canvas.DrawBitmap(bitmap, 0, 0, new SkiaSharp.SKSamplingOptions());
    return result;
}

// Parses hex colors in #RGB, #RRGGBB, or #RRGGBBAA form.
static bool TryParseHexColor(string? hex, out SkiaSharp.SKColor color)
{
    color = SkiaSharp.SKColors.White;
    if (string.IsNullOrWhiteSpace(hex)) return false;

    return SkiaSharp.SKColor.TryParse(hex, out color);
}

public class ResizeRequest
{
    public IFormFile File { get; set; } = default!;
    public int Width { get; set; }
    public int Height { get; set; }
    public string? Mode { get; set; } // "fit" | "crop" | "exact" — defaults to "fit"
}

public class CompressRequest
{
    public IFormFile File { get; set; } = default!;
    public int Quality { get; set; } // 1-100, lower = smaller file, worse quality
}

public class ConvertRequest
{
    public IFormFile File { get; set; } = default!;
    public string? Format { get; set; } // "jpeg" | "png" | "webp" — "avif" returns 400
    public int? Quality { get; set; } // 1-100, defaults to 90 (ignored for png)
}

public class FilterRequest
{
    public IFormFile File { get; set; } = default!;
    public string? Filter { get; set; } // "grayscale" | "sepia" | "vintage" | "invert" | "blackandwhite"
}

public class WatermarkRequest
{
    public IFormFile File { get; set; } = default!;
    public string? Text { get; set; } // text watermark; provide this and/or WatermarkFile
    public IFormFile? WatermarkFile { get; set; } // image/logo watermark
    public string? Position { get; set; } // "topleft" | "topright" | "bottomleft" | "bottomright" | "center" — defaults to "bottomright"
    public int? Opacity { get; set; } // 0-100, defaults to 60
    public float? FontSize { get; set; } // defaults to 32
}

public class RotateRequest
{
    public IFormFile File { get; set; } = default!;
    public float Degrees { get; set; } // clockwise; canvas expands to avoid clipping
}

public class RoundCornersRequest
{
    public IFormFile File { get; set; } = default!;
    public float Radius { get; set; } // corner radius in pixels
}

public class AddBackgroundRequest
{
    public IFormFile File { get; set; } = default!;
    public string Color { get; set; } = "#FFFFFF"; // hex: #RGB, #RRGGBB, or #RRGGBBAA
}
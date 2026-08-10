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

app.Run();

// Resizes a bitmap into exactly the requested width/height box according to mode:
//   "fit"   - scale to fit entirely inside the box, aspect ratio preserved
//             (output may be smaller than the box on one axis; no crop, no stretch)
//   "crop"  - scale to fill the box, aspect ratio preserved, then crop the
//             overflow from the center so the output is exactly width x height
//   "exact" - stretch/squash to exactly width x height, ignoring aspect ratio
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
                canvas.DrawBitmap(scaled, sourceRect, destRect);
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
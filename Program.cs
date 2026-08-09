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

    // Calculate scale factor that fits within the requested bounds
    var widthScale = (double)request.Width / bitmap.Width;
    var heightScale = (double)request.Height / bitmap.Height;
    var scale = Math.Min(widthScale, heightScale);

    var newWidth = (int)(bitmap.Width * scale);
    var newHeight = (int)(bitmap.Height * scale);

    using var resized = bitmap.Resize(new SkiaSharp.SKImageInfo(newWidth, newHeight), SkiaSharp.SKSamplingOptions.Default);
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

public class ResizeRequest
{
    public IFormFile File { get; set; } = default!;
    public int Width { get; set; }
    public int Height { get; set; }
}

public class CompressRequest
{
    public IFormFile File { get; set; } = default!;
    public int Quality { get; set; } // 1-100, lower = smaller file, worse quality
}
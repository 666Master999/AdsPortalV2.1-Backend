// ImageService.cs
using System;
using System.IO;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace AdsPortalV2.Services
{
    public class ImageService
    {
        public string GenerateShortFileName(string? ext)
        {
            if (string.IsNullOrEmpty(ext))
            {
                return Guid.NewGuid().ToString("N");
            }

            return Guid.NewGuid().ToString("N") + ext;
        }

        public async Task<string> SaveCompressedImageAsync(Stream inputStream, string uploadsFolder, string fileName,
            int targetKb = 50, int minQuality = 1, int maxQuality = 100)
        {
            if (inputStream == null) throw new ArgumentNullException(nameof(inputStream));
            if (string.IsNullOrEmpty(uploadsFolder)) throw new ArgumentException("Uploads folder path is required.", nameof(uploadsFolder));
            if (string.IsNullOrEmpty(fileName)) throw new ArgumentException("File name is required.", nameof(fileName));

            Directory.CreateDirectory(uploadsFolder);
            var filePath = Path.Combine(uploadsFolder, fileName);
            int targetBytes = targetKb * 1024;

            // Исправленный вызов Image.Load
            using var srcImage = Image.Load<Rgba32>(inputStream);

            async Task<byte[]> EncodeImageAsync(Image<Rgba32> img, int quality)
            {
                await using var ms = new MemoryStream();
                var encoder = new JpegEncoder { Quality = quality };
                img.Save(ms, encoder);
                return ms.ToArray();
            }

            async Task<byte[]?> FindByQualityAsync(Image<Rgba32> img)
            {
                int lo = minQuality;
                int hi = maxQuality;
                byte[]? bestUnder = null;

                var hiBytes = await EncodeImageAsync(img, hi);
                if (hiBytes.Length <= targetBytes) return hiBytes;

                int iter = 0;
                while (lo <= hi && iter < 8)
                {
                    iter++;
                    int mid = (lo + hi) / 2;
                    var midBytes = await EncodeImageAsync(img, mid);
                    if (midBytes.Length <= targetBytes)
                    {
                        bestUnder = midBytes;
                        lo = mid + 1;
                    }
                    else
                    {
                        hi = mid - 1;
                    }
                }

                return bestUnder;
            }

            var result = await FindByQualityAsync(srcImage);
            if (result != null)
            {
                var tmp = filePath + ".tmp";
                await File.WriteAllBytesAsync(tmp, result);
                if (File.Exists(filePath)) File.Delete(filePath);
                File.Move(tmp, filePath);
                return filePath;
            }

            float scale = 0.9f;
            int maxResizes = 6;
            using var working = srcImage.Clone();

            for (int i = 0; i < maxResizes; i++)
            {
                int newW = Math.Max(1, (int)(working.Width * scale));
                int newH = Math.Max(1, (int)(working.Height * scale));
                working.Mutate(x => x.Resize(newW, newH));

                var byts = await FindByQualityAsync(working);
                if (byts != null)
                {
                    var tmp = filePath + ".tmp";
                    await File.WriteAllBytesAsync(tmp, byts);
                    if (File.Exists(filePath)) File.Delete(filePath);
                    File.Move(tmp, filePath);
                    return filePath;
                }

                scale *= 0.9f;
            }

            var finalBytes = await EncodeImageAsync(working, minQuality);
            var finalTmp = filePath + ".tmp";
            await File.WriteAllBytesAsync(finalTmp, finalBytes);
            if (File.Exists(filePath)) File.Delete(filePath);
            File.Move(finalTmp, filePath);

            return filePath;
        }
    }
}
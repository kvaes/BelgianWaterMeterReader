using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using LibVLCSharp.Shared;
using System.Threading;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Advanced;
using SixLabors.ImageSharp.Processing;
using System.Collections.Concurrent;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

// Many things borrowed from https://code.videolan.org/mfkl/libvlcsharp-samples/-/blob/master/PreviewThumbnailExtractor/Program.cs?ref_type=heads
namespace WaterMeterReader
{
    public static class GetMeterData
    {
        private static MemoryMappedFile CurrentMappedFile;
        private static MemoryMappedViewAccessor CurrentMappedViewAccessor;
        private static readonly ConcurrentQueue<(MemoryMappedFile file, MemoryMappedViewAccessor accessor)> FilesToProcess = new ConcurrentQueue<(MemoryMappedFile file, MemoryMappedViewAccessor accessor)>();
        private static long FrameCounter = 0;
        private static uint Lines;
        private static uint Pitch;
        private const uint BytePerPixel = 4;
        private const uint Width = 800;
        private const uint Height = 600;

        [FunctionName("GetMeterData")]
        public static async Task<IActionResult> Run(
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", "post", Route = null)] HttpRequest req,
            ILogger log)
        {
            log.LogInformation("Connecting to media source");
            string inputSource = Environment.GetEnvironmentVariable("SourceURI");
            Uri inputSourceUri = new Uri(inputSource);

            var libvlc = new LibVLC();
            var mediaPlayer = new MediaPlayer(libvlc);
            
            Pitch = Align(Width * BytePerPixel);
            Lines = Align(Height);
            
            var processingCancellationTokenSource = new CancellationTokenSource();
            mediaPlayer.Stopped += (s, e) => processingCancellationTokenSource.CancelAfter(1);

            var media = new Media(libvlc, new Uri(inputSource));
            media.AddOption(":no-audio");

            mediaPlayer.SetVideoFormat("RV32", Width, Height, Pitch);
            mediaPlayer.SetVideoCallbacks(Lock, null, Display);

            mediaPlayer.Play(media);

            var destination = System.IO.Path.GetTempPath();
            log.LogInformation(destination);
  
            //await ProcessThumbnailAsync(destination, processingCancellationTokenSource.Token, log);
            string snapshot = ProcessThumbnail(destination, processingCancellationTokenSource.Token, log);
            log.LogInformation("Snapshot " + snapshot + " taken!");

            return new OkObjectResult(snapshot);
        }

        static string ProcessThumbnail(string destination, CancellationToken token, ILogger log)
        {
            var frameNumber = 0;
            while (!token.IsCancellationRequested)
            {
                if (FilesToProcess.TryDequeue(out var file))
                {
                    string snapshot = Guid.NewGuid().ToString() + ".jpg";
                    var fileName = Path.Combine(destination, snapshot);
                    using (var image = new Image<SixLabors.ImageSharp.PixelFormats.Bgra32>((int)(Pitch / BytePerPixel), (int)Lines))
                    using (var sourceStream = file.file.CreateViewStream())
                    {
                        var mg = image.GetPixelMemoryGroup();
                        for (int i = 0; i < mg.Count; i++)
                        {
                            sourceStream.Read(MemoryMarshal.AsBytes(mg[i].Span));
                        }
                        
                        using (var outputFile = File.Open(fileName, FileMode.Create))
                        {
                            image.Mutate(ctx => ctx.Crop((int)Width, (int)Height));
                            image.SaveAsJpeg(outputFile);
                            log.LogInformation("File " + snapshot + " was written!");
                        }
                    }
                    file.accessor.Dispose();
                    file.file.Dispose();
                    frameNumber++;
                    return snapshot;
                }
            }
            return "";
        }

        static async Task ProcessThumbnailsAsync(string destination, CancellationToken token)
        {
            var frameNumber = 0;
            while (!token.IsCancellationRequested)
            {
                if (FilesToProcess.TryDequeue(out var file))
                {
                    using (var image = new Image<SixLabors.ImageSharp.PixelFormats.Bgra32>((int)(Pitch / BytePerPixel), (int)Lines))
                    using (var sourceStream = file.file.CreateViewStream())
                    {
                        var mg = image.GetPixelMemoryGroup();
                        for (int i = 0; i < mg.Count; i++)
                        {
                            sourceStream.Read(MemoryMarshal.AsBytes(mg[i].Span));
                        }

                        Console.WriteLine($"Writing {frameNumber:0000}.jpg");
                        var fileName = Path.Combine(destination, $"{frameNumber:0000}.jpg");
                        using (var outputFile = File.Open(fileName, FileMode.Create))
                        {
                            image.Mutate(ctx => ctx.Crop((int)Width, (int)Height));
                            image.SaveAsJpeg(outputFile);
                        }
                    }
                    file.accessor.Dispose();
                    file.file.Dispose();
                    frameNumber++;
                }
                else
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
            }
        }

        static uint Align(uint size)
        {
            if (size % 32 == 0)
            {
                return size;
            }

            return ((size / 32) + 1) * 32;// Align on the next multiple of 32
        }

        static IntPtr Lock(IntPtr opaque, IntPtr planes)
        {
            CurrentMappedFile = MemoryMappedFile.CreateNew(null, Pitch * Lines);
            CurrentMappedViewAccessor = CurrentMappedFile.CreateViewAccessor();
            Marshal.WriteIntPtr(planes, CurrentMappedViewAccessor.SafeMemoryMappedViewHandle.DangerousGetHandle());
            return IntPtr.Zero;
        }

        static void Display(IntPtr opaque, IntPtr picture)
        {
            if (FrameCounter % 100 == 0)
            {
                FilesToProcess.Enqueue((CurrentMappedFile, CurrentMappedViewAccessor));
                CurrentMappedFile = null;
                CurrentMappedViewAccessor = null;
            }
            else
            {
                CurrentMappedViewAccessor.Dispose();
                CurrentMappedFile.Dispose();
                CurrentMappedFile = null;
                CurrentMappedViewAccessor = null;
            }
            FrameCounter++;
        }

    }
}

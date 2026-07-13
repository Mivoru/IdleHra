using System;
using System.IO;
using System.Threading.Tasks;
using Unity.Collections;
using Unity.Collections.LowLevel.Unsafe;
using UnityEngine.Networking;

namespace FolkIdle.Client.Engine
{
    public static class PartialContentDownloader
    {
        public static async Task DownloadChunkAsync(string url, string destinationPath, long startByte, long endByte)
        {
            using UnityWebRequest request = UnityWebRequest.Get(url);
            request.SetRequestHeader("Range", $"bytes={startByte}-{endByte}");

            var operation = request.SendWebRequest();
            while (!operation.isDone)
            {
                await Task.Yield();
            }

            if (request.result != UnityWebRequest.Result.Success)
            {
                throw new Exception($"Failed to download chunk from {startByte} to {endByte}: {request.error}");
            }

            byte[] data = request.downloadHandler.data;
            if (data == null || data.Length == 0)
            {
                return;
            }

            // Ingest data chunks into unmanaged NativeArray allocated via Allocator.Persistent
            NativeArray<byte> nativeBuffer = new NativeArray<byte>(data.Length, Allocator.Persistent, NativeArrayOptions.UninitializedMemory);
            
            try
            {
                // Copy data to NativeArray
                NativeArray<byte>.Copy(data, nativeBuffer, data.Length);

                // Pipe unmanaged memory straight to local device storage using async non-blocking disk I/O WriteAsync
                using FileStream fs = new FileStream(destinationPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 4096, useAsync: true);
                fs.Seek(startByte, SeekOrigin.Begin);

                // In .NET Standard 2.1 / Unity, we can write Memory/ReadOnlyMemory directly, 
                // but since NativeArray is unmanaged, we can use an unmanaged memory stream or pointer
                unsafe
                {
                    using var unmanagedStream = new UnmanagedMemoryStream((byte*)nativeBuffer.GetUnsafeReadOnlyPtr(), nativeBuffer.Length);
                    await unmanagedStream.CopyToAsync(fs);
                }
            }
            finally
            {
                if (nativeBuffer.IsCreated)
                {
                    nativeBuffer.Dispose();
                }
            }
        }
    }
}

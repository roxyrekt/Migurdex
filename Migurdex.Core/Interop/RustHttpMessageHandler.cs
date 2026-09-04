using Migurdex.Shared.Infrastructure;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;

namespace Migurdex.Core.Interop;

public class RustHttpMessageHandler : HttpMessageHandler
{
    private static readonly ConcurrentDictionary<long, TaskCompletionSource<HttpResponseMessage>> _pendingRequests =
        new();

    private static long _taskIdCounter;

    private static readonly unsafe delegate* unmanaged<FfiResponse, void> _onResponseCallback = &OnResponseReceived;

    [UnmanagedCallersOnly]
    private static unsafe void OnResponseReceived(FfiResponse response)
    {
        if (!_pendingRequests.TryRemove(response.TaskId, out var tcs))
        {
            if (response.ErrorPtr != null && response.ErrorLen > 0)
            {
                RustBridge.FreeBuffer(response.ErrorPtr, response.ErrorLen);
            }

            if (response.Headers != null && response.HeadersLen > 0)
            {
                RustBridge.FreeHeaders(response.Headers, response.HeadersLen);
            }

            if (response.BodyPtr != null && response.BodyLen > 0)
            {
                RustBridge.FreeBuffer(response.BodyPtr, response.BodyLen);
            }

            return;
        }

        try
        {
            if (response.ErrorPtr != null && response.ErrorLen > 0)
            {
                var err = Encoding.UTF8.GetString(
                    new ReadOnlySpan<byte>(response.ErrorPtr, (int) response.ErrorLen));
                tcs.SetException(new HttpRequestException($"Rust side error: {err}"));
                RustBridge.FreeBuffer(response.ErrorPtr, response.ErrorLen);

                return;
            }

            var httpResponse = new HttpResponseMessage((HttpStatusCode) response.Status);

            if (response.BodyPtr != null && response.BodyLen > 0)
            {
                var stream =
                    new RustNativeStream(response.BodyPtr, response.BodyLen, &RustBridge.FreeBufferUnmanaged);

                httpResponse.Content = new StreamContent(stream);
            }
            else
            {
                httpResponse.Content = new EmptyContent();
            }

            if (response.Headers != null && response.HeadersLen > 0)
            {
                var headersSpan = new ReadOnlySpan<FfiHeader>(response.Headers, (int) response.HeadersLen);
                for (var i = 0; i < headersSpan.Length; i++)
                {
                    var h   = headersSpan[i];
                    var key = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(h.KeyPtr, (int) h.KeyLen));
                    var val = Encoding.UTF8.GetString(new ReadOnlySpan<byte>(h.ValuePtr, (int) h.ValueLen));

                    if (!httpResponse.Headers.TryAddWithoutValidation(key, val))
                    {
                        httpResponse.Content.Headers.TryAddWithoutValidation(key, val);
                    }
                }

                RustBridge.FreeHeaders(response.Headers, response.HeadersLen);
            }

            tcs.SetResult(httpResponse);
        }
        catch (Exception ex)
        {
            if (response.ErrorPtr != null && response.ErrorLen > 0)
            {
                RustBridge.FreeBuffer(response.ErrorPtr, response.ErrorLen);
            }

            if (response.Headers != null && response.HeadersLen > 0)
            {
                RustBridge.FreeHeaders(response.Headers, response.HeadersLen);
            }

            if (response.BodyPtr != null && response.BodyLen > 0)
            {
                RustBridge.FreeBuffer(response.BodyPtr, response.BodyLen);
            }

            tcs.SetException(ex);
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
        CancellationToken                                                           cancellationToken)
    {
        var taskId = Interlocked.Increment(ref _taskIdCounter);
        var tcs    = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingRequests[taskId] = tcs;

        var bodyBytes = Array.Empty<byte>();
        if (request.Content != null)
        {
            bodyBytes = await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        }

        var urlBytes    = Encoding.UTF8.GetBytes(request.RequestUri?.AbsoluteUri ?? string.Empty);
        var methodBytes = Encoding.UTF8.GetBytes(request.Method.Method);

        var requestHeaders = new List<(byte[] Key, byte[] Val)>();

        ExtractHeaders(request.Headers, requestHeaders);

        if (request.Content != null)
        {
            ExtractHeaders(request.Content.Headers, requestHeaders);
        }

        var noFollow = false;
        if (request.Options.TryGetValue(RustHttpOptions.NoFollowKey, out var nfValue))
        {
            noFollow = nfValue;
        }
        else if (request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("NoFollow"), out var legacyNf))
        {
            noFollow = legacyNf;
        }

        var skipCertVerify = false;
        if (request.Options.TryGetValue(RustHttpOptions.SkipCertVerifyKey, out var scvValue))
        {
            skipCertVerify = scvValue;
        }
        else if (request.Options.TryGetValue(new HttpRequestOptionsKey<bool>("SkipCertVerify"), out var legacyScv))
        {
            skipCertVerify = legacyScv;
        }

        string? emulation = null;
        if (request.Options.TryGetValue(RustHttpOptions.EmulationKey, out var emuValue))
        {
            emulation = emuValue;
        }
        else if (request.Options.TryGetValue(new HttpRequestOptionsKey<string>("Emulation"), out var legacyEmu))
        {
            emulation = legacyEmu;
        }

        var emuBytes = !string.IsNullOrEmpty(emulation) ? Encoding.UTF8.GetBytes(emulation) : Array.Empty<byte>();

        unsafe
        {
            fixed (byte* urlPtr = urlBytes)
            fixed (byte* methodPtr = methodBytes)
            fixed (byte* bodyPtr = bodyBytes)
            fixed (byte* emuPtr = emuBytes)
            {
                var ffiHeaders = new FfiHeader[requestHeaders.Count];
                var handles    = new GCHandle[requestHeaders.Count * 2];

                try
                {
                    for (var i = 0; i < requestHeaders.Count; i++)
                    {
                        var hKey = requestHeaders[i].Key;
                        var hVal = requestHeaders[i].Val;

                        var pinKey = GCHandle.Alloc(hKey, GCHandleType.Pinned);
                        var pinVal = GCHandle.Alloc(hVal, GCHandleType.Pinned);

                        handles[i * 2]       = pinKey;
                        handles[(i * 2) + 1] = pinVal;

                        ffiHeaders[i] = new FfiHeader
                        {
                            KeyPtr   = (byte*) pinKey.AddrOfPinnedObject(),
                            KeyLen   = (nuint) hKey.Length,
                            ValuePtr = (byte*) pinVal.AddrOfPinnedObject(),
                            ValueLen = (nuint) hVal.Length
                        };
                    }

                    var reqOptions = new FfiRequestOptions[]
                    {
                        new()
                        {
                            NoFollow       = noFollow,
                            SkipCertVerify = skipCertVerify,
                            EmulationPtr   = emuBytes.Length > 0 ? emuPtr : (byte*) 0,
                            EmulationLen   = (nuint) emuBytes.Length
                        }
                    };

                    fixed (FfiHeader* ffiHeadersPtr = ffiHeaders)
                    fixed (FfiRequestOptions* optionsPtr = reqOptions)
                    {
                        RustBridge.SendAsync(
                            taskId,
                            urlPtr,
                            (nuint) urlBytes.Length,
                            methodPtr,
                            (nuint) methodBytes.Length,
                            ffiHeadersPtr,
                            (nuint) ffiHeaders.Length,
                            bodyBytes.Length > 0 ? bodyPtr : (byte*) 0,
                            (nuint) bodyBytes.Length,
                            optionsPtr,
                            _onResponseCallback
                        );
                    }
                }
                finally
                {
                    foreach (var handle in handles)
                    {
                        if (handle.IsAllocated)
                        {
                            handle.Free();
                        }
                    }
                }
            }
        }

        await using (cancellationToken.Register(() =>
                     {
                         if (_pendingRequests.TryRemove(taskId, out var pendingTcs))
                         {
                             pendingTcs.TrySetCanceled(cancellationToken);
                         }
                     }))
        {
            return await tcs.Task.ConfigureAwait(false);
        }
    }

    private static void ExtractHeaders(HttpHeaders? headers,
        List<(byte[] Key, byte[] Val)>              targetList)
    {
        if (headers == null)
        {
            return;
        }

        foreach (var header in headers.NonValidated)
        {
            var keyBytes = Encoding.UTF8.GetBytes(header.Key);

            foreach (var value in header.Value)
            {
                targetList.Add((keyBytes, Encoding.UTF8.GetBytes(value)));
            }
        }
    }

    private class EmptyContent : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            return Task.CompletedTask;
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;

            return true;
        }
    }
}

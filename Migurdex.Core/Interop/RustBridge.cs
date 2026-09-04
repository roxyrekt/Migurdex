using System.Runtime.InteropServices;
using System.Text;

namespace Migurdex.Core.Interop;

[StructLayout(LayoutKind.Sequential)]
public unsafe struct NativeBuffer
{
    public byte* Ptr;
    public nuint Len;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FfiHeader
{
    public byte* KeyPtr;
    public nuint KeyLen;
    public byte* ValuePtr;
    public nuint ValueLen;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FfiRequestOptions
{
    [MarshalAs(UnmanagedType.I1)]
    public bool NoFollow;

    [MarshalAs(UnmanagedType.I1)]
    public bool SkipCertVerify;

    public byte* EmulationPtr;
    public nuint EmulationLen;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct FfiResponse
{
    public long       TaskId;
    public ushort     Status;
    public FfiHeader* Headers;
    public nuint      HeadersLen;
    public byte*      BodyPtr;
    public nuint      BodyLen;
    public byte*      ErrorPtr;
    public nuint      ErrorLen;
}

[StructLayout(LayoutKind.Sequential)]
public unsafe struct RustApi
{
    public delegate* unmanaged<byte*, byte*, NativeBuffer>        FetchUrl;
    public delegate* unmanaged<byte*, byte*, byte*, NativeBuffer> FetchUrlPost;
    public delegate* unmanaged<byte*, nuint, void>                RustFree;
    public delegate* unmanaged<nuint, byte*>                      RustAlloc;
    public delegate* unmanaged<byte*, byte*, NativeBuffer>        FetchUrlNoFollow;
    public delegate* unmanaged<byte*, NativeBuffer>               FetchBatch;

    public delegate* unmanaged<long, byte*, nuint, byte*, nuint, FfiHeader*, nuint, byte*, nuint, FfiRequestOptions*,
        delegate*
        unmanaged<FfiResponse, void>, void> RustSendAsync;

    public delegate* unmanaged<FfiHeader*, nuint, void> RustFreeHeaders;
    public delegate* unmanaged<byte*, NativeBuffer>     RustJsUnpack;
    public delegate* unmanaged<byte*, byte*, double>    RustFuzzySimilarity;
}

public static unsafe class RustBridge
{
    private static RustApi* _api;
    private static IntPtr   _libHandle;

    public static bool IsInitialized => _api != null;

    public static void Initialize(string libPath)
    {
        if (_api != null)
        {
            return;
        }

        _libHandle = NativeLibrary.Load(libPath);
        var getApiPtr = (delegate* unmanaged<RustApi*>) NativeLibrary.GetExport(_libHandle, "get_rust_api");
        _api = getApiPtr();
    }

    public static void SendAsync(
        long                                   taskId,
        byte*                                  urlPtr,
        nuint                                  urlLen,
        byte*                                  methodPtr,
        nuint                                  methodLen,
        FfiHeader*                             headers,
        nuint                                  headersLen,
        byte*                                  bodyPtr,
        nuint                                  bodyLen,
        FfiRequestOptions*                     options,
        delegate* unmanaged<FfiResponse, void> callback)
    {
        EnsureInitialized();
        _api->RustSendAsync(taskId,
                            urlPtr,
                            urlLen,
                            methodPtr,
                            methodLen,
                            headers,
                            headersLen,
                            bodyPtr,
                            bodyLen,
                            options,
                            callback);
    }

    [UnmanagedCallersOnly]
    public static void FreeBufferUnmanaged(byte* ptr, nuint len)
    {
        FreeBuffer(ptr, len);
    }

    public static void FreeBuffer(byte* ptr, nuint len)
    {
        if (_api == null || ptr == null || len == 0)
        {
            return;
        }

        _api->RustFree(ptr, len);
    }

    public static void FreeHeaders(FfiHeader* ptr, nuint len)
    {
        if (_api == null || ptr == null || len == 0)
        {
            return;
        }

        _api->RustFreeHeaders(ptr, len);
    }

    public static string? UnpackJs(string html)
    {
        if (_api == null || _api->RustJsUnpack == null)
        {
            return null;
        }

        var htmlBytes = Encoding.UTF8.GetBytes(html + "\0");
        fixed (byte* ptr = htmlBytes)
        {
            var buffer = _api->RustJsUnpack(ptr);
            var result = ProcessBuffer(buffer);
            return string.IsNullOrEmpty(result) ? null : result;
        }
    }

    public static double? CalculateFuzzySimilarity(string str1, string str2)
    {
        if (_api == null || _api->RustFuzzySimilarity == null)
        {
            return null;
        }

        var str1Bytes = Encoding.UTF8.GetBytes(str1 + "\0");
        var str2Bytes = Encoding.UTF8.GetBytes(str2 + "\0");

        fixed (byte* ptr1 = str1Bytes)
        fixed (byte* ptr2 = str2Bytes)
        {
            return _api->RustFuzzySimilarity(ptr1, ptr2);
        }
    }

    private static string ProcessBuffer(NativeBuffer buffer)
    {
        if (buffer.Ptr == null || buffer.Len == 0)
        {
            return string.Empty;
        }

        try
        {
            return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(buffer.Ptr, (int) buffer.Len));
        }
        finally
        {
            _api->RustFree(buffer.Ptr, buffer.Len);
        }
    }

    private static void EnsureInitialized()
    {
        if (_api == null)
        {
            throw new InvalidOperationException("RustBridge is not initialized. Call RustBridge.Initialize first.");
        }
    }
}

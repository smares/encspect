// Media Foundation and DMO declarations for the probe. The interfaces list their methods in vtable
// order; a method that is never called is declared only to keep the later ones at their slot.
using System.Runtime.InteropServices;

namespace WmaDecodeProbe;

[ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaType
{
}

[ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSample
{
    // IMFAttributes, 30 methods.
    void A0();
    void A1();
    void A2();
    void A3();
    void A4();
    void A5();
    void A6();
    void A7();
    void A8();
    void A9();
    void A10();
    void A11();
    void A12();
    void A13();
    void A14();
    void A15();
    void A16();
    void A17();
    void A18();
    void A19();
    void A20();
    void A21();
    void A22();
    void A23();
    void A24();
    void A25();
    void A26();
    [PreserveSig] int GetCount(out uint count);
    [PreserveSig] int GetItemByIndex(uint index, out Guid key, IntPtr value);
    void A29();

    [PreserveSig] int GetSampleFlags(out uint flags);
    void SetSampleFlags(uint flags);
    [PreserveSig] int GetSampleTime(out long time);
    void SetSampleTime(long time);
    [PreserveSig] int GetSampleDuration(out long duration);
    void SetSampleDuration(long duration);
    [PreserveSig] int GetBufferCount(out uint count);
    [PreserveSig] int GetBufferByIndex(uint index, out IMFMediaBuffer buffer);
    [PreserveSig] int ConvertToContiguousBuffer(out IMFMediaBuffer buffer);
    [PreserveSig] int AddBuffer(IMFMediaBuffer buffer);
    void RemoveBufferByIndex();
    void RemoveAllBuffers();
    [PreserveSig] int GetTotalLength(out uint length);
    void CopyToBuffer();
}

[ComImport, Guid("045fa593-8799-42b8-bc8d-8968c6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFMediaBuffer
{
    [PreserveSig] int Lock(out IntPtr buffer, out uint maxLength, out uint currentLength);
    [PreserveSig] int Unlock();
    [PreserveSig] int GetCurrentLength(out uint length);
    [PreserveSig] int SetCurrentLength(uint length);
    [PreserveSig] int GetMaxLength(out uint length);
}

[StructLayout(LayoutKind.Sequential)]
public struct MftInputStreamInfo
{
    public long MaxLatency;
    public uint Flags;
    public uint Size;
    public uint MaxLookahead;
    public uint Alignment;
}

[StructLayout(LayoutKind.Sequential)]
public struct MftOutputStreamInfo
{
    public uint Flags;
    public uint Size;
    public uint Alignment;
}

[StructLayout(LayoutKind.Sequential)]
public struct MftOutputDataBuffer
{
    public uint StreamId;
    public IntPtr Sample;
    public uint Status;
    public IntPtr Events;
}

[ComImport, Guid("bf94c121-5b05-4e6f-8000-ba598961414d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFTransform
{
    [PreserveSig] int GetStreamLimits(out uint inputMin, out uint inputMax, out uint outputMin, out uint outputMax);
    [PreserveSig] int GetStreamCount(out uint inputs, out uint outputs);
    [PreserveSig] int GetStreamIDs(uint inputSize, IntPtr inputIds, uint outputSize, IntPtr outputIds);
    [PreserveSig] int GetInputStreamInfo(uint stream, out MftInputStreamInfo info);
    [PreserveSig] int GetOutputStreamInfo(uint stream, out MftOutputStreamInfo info);
    [PreserveSig] int GetAttributes(out IntPtr attributes);
    [PreserveSig] int GetInputStreamAttributes(uint stream, out IntPtr attributes);
    [PreserveSig] int GetOutputStreamAttributes(uint stream, out IntPtr attributes);
    [PreserveSig] int DeleteInputStream(uint stream);
    [PreserveSig] int AddInputStreams(uint count, IntPtr ids);
    [PreserveSig] int GetInputAvailableType(uint stream, uint index, out IMFMediaType type);
    [PreserveSig] int GetOutputAvailableType(uint stream, uint index, out IMFMediaType type);
    [PreserveSig] int SetInputType(uint stream, IMFMediaType type, uint flags);
    [PreserveSig] int SetOutputType(uint stream, IMFMediaType type, uint flags);
    [PreserveSig] int GetInputCurrentType(uint stream, out IMFMediaType type);
    [PreserveSig] int GetOutputCurrentType(uint stream, out IMFMediaType type);
    [PreserveSig] int GetInputStatus(uint stream, out uint flags);
    [PreserveSig] int GetOutputStatus(out uint flags);
    [PreserveSig] int SetOutputBounds(long lower, long upper);
    [PreserveSig] int ProcessEvent(uint stream, IntPtr evt);
    [PreserveSig] int ProcessMessage(uint message, IntPtr parameter);
    [PreserveSig] int ProcessInput(uint stream, IMFSample sample, uint flags);
    [PreserveSig] int ProcessOutput(uint flags, uint count, ref MftOutputDataBuffer buffers, out uint status);
}

// IMFActivate: the 30 methods of IMFAttributes in vtable order, of which three are called, then its own.
[ComImport, Guid("7fee9e9a-4a89-47a6-899c-b6a53a70fb67"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFActivate
{
    void GetItem();
    void GetItemType();
    void CompareItem();
    void Compare();
    void GetUINT32();
    void GetUINT64();
    void GetDouble();
    [PreserveSig] int GetGUID(ref Guid key, out Guid value);
    void GetStringLength();
    void GetString();
    [PreserveSig] int GetAllocatedString(ref Guid key, out IntPtr value, out uint length);
    [PreserveSig] int GetBlobSize(ref Guid key, out uint size);
    [PreserveSig] int GetBlob(ref Guid key, IntPtr buffer, uint size, out uint written);
    void GetAllocatedBlob();
    void GetUnknown();
    void SetItem();
    void DeleteItem();
    void DeleteAllItems();
    void SetUINT32();
    void SetUINT64();
    void SetDouble();
    void SetGUID();
    void SetString();
    void SetBlob();
    void SetUnknown();
    void LockStore();
    void UnlockStore();
    void GetCount();
    void GetItemByIndex();
    void CopyAllItems();
    void ActivateObject();
    void ShutdownObject();
    void DetachObject();
}

[ComImport, Guid("70ae66f2-c809-4e4f-8915-bdcb406b7993"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMFSourceReader
{
    [PreserveSig] int GetStreamSelection(uint stream, out int selected);
    [PreserveSig] int SetStreamSelection(uint stream, int selected);
    [PreserveSig] int GetNativeMediaType(uint stream, uint index, out IMFMediaType type);
    [PreserveSig] int GetCurrentMediaType(uint stream, out IMFMediaType type);
    [PreserveSig] int SetCurrentMediaType(uint stream, IntPtr reserved, IMFMediaType type);
    void SetCurrentPosition();
    [PreserveSig] int ReadSample(uint stream, uint controlFlags, out uint actualStream, out uint streamFlags, out long timestamp, out IMFSample? sample);
    [PreserveSig] int Flush(uint stream);
    void GetServiceForStream();
    void GetPresentationAttribute();
}

[ComImport, Guid("59eff8b9-938c-4a26-82f2-95cb84cdc837"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMediaBuffer
{
    [PreserveSig] int SetLength(uint length);
    [PreserveSig] int GetMaxLength(out uint maxLength);
    [PreserveSig] int GetBufferAndLength(out IntPtr buffer, out uint length);
}

[StructLayout(LayoutKind.Sequential)]
public struct DmoMediaType
{
    public Guid MajorType;
    public Guid SubType;
    public int FixedSizeSamples;
    public int TemporalCompression;
    public uint SampleSize;
    public Guid FormatType;
    public IntPtr Unknown;
    public uint FormatSize;
    public IntPtr Format;
}

[StructLayout(LayoutKind.Sequential)]
public struct DmoOutputBuffer
{
    public IntPtr Buffer;
    public uint Status;
    public long Timestamp;
    public long TimeLength;
}

[StructLayout(LayoutKind.Sequential)]
public struct DmoPartialType
{
    public Guid Type;
    public Guid SubType;
}

[ComImport, Guid("d8ad0f58-5494-4102-97c5-ec798e59bcf4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IMediaObject
{
    [PreserveSig] int GetStreamCount(out uint inputs, out uint outputs);
    [PreserveSig] int GetInputStreamInfo(uint stream, out uint flags);
    [PreserveSig] int GetOutputStreamInfo(uint stream, out uint flags);
    [PreserveSig] int GetInputType(uint stream, uint index, out DmoMediaType type);
    [PreserveSig] int GetOutputType(uint stream, uint index, out DmoMediaType type);
    [PreserveSig] int SetInputType(uint stream, ref DmoMediaType type, uint flags);
    [PreserveSig] int SetOutputType(uint stream, ref DmoMediaType type, uint flags);
    [PreserveSig] int GetInputCurrentType(uint stream, out DmoMediaType type);
    [PreserveSig] int GetOutputCurrentType(uint stream, out DmoMediaType type);
    [PreserveSig] int GetInputSizeInfo(uint stream, out uint size, out uint maxLookahead, out uint alignment);
    [PreserveSig] int GetOutputSizeInfo(uint stream, out uint size, out uint alignment);
    [PreserveSig] int GetInputMaxLatency(uint stream, out long latency);
    [PreserveSig] int SetInputMaxLatency(uint stream, long latency);
    [PreserveSig] int Flush();
    [PreserveSig] int Discontinuity(uint stream);
    [PreserveSig] int AllocateStreamingResources();
    [PreserveSig] int FreeStreamingResources();
    [PreserveSig] int GetInputStatus(uint stream, out uint flags);
    [PreserveSig] int ProcessInput(uint stream, IMediaBuffer buffer, uint flags, long timestamp, long timeLength);
    [PreserveSig] int ProcessOutput(uint flags, uint count, ref DmoOutputBuffer buffers, out uint status);
    [PreserveSig] int Lock(int @lock);
}

[ComImport, Guid("2c3cd98a-2bfa-4a53-9c27-5249ba64ba0f"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
public interface IEnumDMO
{
    [PreserveSig]
    int Next(uint count,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 16)] Guid[] clsids,
        [Out, MarshalAs(UnmanagedType.LPArray, SizeConst = 16)] IntPtr[] names,
        out uint fetched);
    [PreserveSig] int Skip(uint count);
    [PreserveSig] int Reset();
    [PreserveSig] int Clone(out IEnumDMO clone);
}

// A buffer for the DMO that lives in native memory until Dispose, since the DMO may keep an
// input buffer past the call that handed it over.
[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class NativeMediaBuffer : IMediaBuffer, IDisposable
{
    private readonly IntPtr memory;
    private readonly uint capacity;
    private uint length;

    public NativeMediaBuffer(uint capacity, byte[]? data = null)
    {
        this.capacity = Math.Max(capacity, 1u);
        memory = Marshal.AllocHGlobal((int)this.capacity);
        if (data is { Length: > 0 })
        {
            Marshal.Copy(data, 0, memory, data.Length);
            length = (uint)data.Length;
        }
    }

    public uint Length => length;

    public byte[] Bytes()
    {
        var bytes = new byte[length];
        Marshal.Copy(memory, bytes, 0, (int)length);
        return bytes;
    }

    public void Reset() => length = 0;

    public int SetLength(uint newLength)
    {
        if (newLength > capacity)
        {
            return unchecked((int)0x80070057);
        }

        length = newLength;
        return 0;
    }

    public int GetMaxLength(out uint maxLength)
    {
        maxLength = capacity;
        return 0;
    }

    public int GetBufferAndLength(out IntPtr buffer, out uint bufferLength)
    {
        buffer = memory;
        bufferLength = length;
        return 0;
    }

    public void Dispose() => Marshal.FreeHGlobal(memory);
}

public static class Native
{
    public const uint MfVersion = 0x00020070;
    public const uint FirstAudioStream = 0xFFFFFFFD;
    public const uint AllStreams = 0xFFFFFFFE;

    [DllImport("mfplat.dll")]
    public static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll")]
    public static extern int MFShutdown();

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMediaType(out IMFMediaType type);

    [DllImport("mfplat.dll")]
    public static extern int MFInitMediaTypeFromWaveFormatEx(IMFMediaType type, byte[] format, uint size);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateWaveFormatExFromMFMediaType(IMFMediaType type, out IntPtr format, out uint size, uint flags);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateSample(out IMFSample sample);

    [DllImport("mfplat.dll")]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IMFMediaBuffer buffer);

    [DllImport("mfplat.dll")]
    public static extern int MFTEnumEx(Guid category, uint flags, IntPtr inputType, IntPtr outputType, out IntPtr activates, out uint count);

    [DllImport("mfreadwrite.dll", CharSet = CharSet.Unicode)]
    public static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IMFSourceReader reader);

    [DllImport("msdmo.dll")]
    public static extern int DMOEnum(ref Guid category, uint flags, uint inCount,
        [In, MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 2)] DmoPartialType[] inTypes,
        uint outCount, IntPtr outTypes, out IEnumDMO enumerator);

    [DllImport("ole32.dll")]
    public static extern int PropVariantClear(IntPtr variant);

    [DllImport("ole32.dll")]
    public static extern void CoTaskMemFree(IntPtr memory);
}

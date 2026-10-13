// A small COM wrapper of the Windows Media Format SDK's writer (wmvcore.dll), which encodes the
// project's WMA fixtures on a Windows runner: WMA 9.2 standard, WMA 10 Professional, WMA 9.2
// Lossless and WMA Voice 9. It is compiled by Windows PowerShell 5.1's Add-Type, whose compiler
// speaks C# 5: no string interpolation, no null-conditional operators, no expression-bodied members.
//
// The interfaces are declared in the order of their vtables (the SDK's wmsdkidl.h); a method
// that is never called is declared only to keep the later ones at the right slot.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace Encspect
{
    [ComImport, Guid("d16679f2-6ca0-472d-8d31-2f5d55aee155"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWMProfileManager
    {
        void CreateEmptyProfile(uint version, out IWMProfile profile);
        void LoadProfileByID(ref Guid id, out IWMProfile profile);
        void LoadProfileByData([MarshalAs(UnmanagedType.LPWStr)] string data, out IWMProfile profile);
        void SaveProfile(IWMProfile profile, IntPtr data, ref uint length);
        void GetSystemProfileCount(out uint count);
        void LoadSystemProfile(uint index, out IWMProfile profile);
    }

    // IWMCodecInfo, IWMCodecInfo2 and IWMCodecInfo3 in vtable order.
    [ComImport, Guid("7e51f487-4d93-4f98-8ab4-27d0565adc51"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWMCodecInfo3
    {
        void GetCodecInfoCount(ref Guid type, out uint count);
        void GetCodecFormatCount(ref Guid type, uint codec, out uint count);
        void GetCodecFormat(ref Guid type, uint codec, uint format, out IWMStreamConfig config);
        void GetCodecName(ref Guid type, uint codec, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, ref uint length);
        void GetCodecFormatDesc(ref Guid type, uint codec, uint format, out IWMStreamConfig config, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder desc, ref uint length);
        void GetCodecFormatProp(ref Guid type, uint codec, uint format, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint dataType, [MarshalAs(UnmanagedType.LPArray)] byte[] value, ref uint size);
        void GetCodecProp(ref Guid type, uint codec, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint dataType, [MarshalAs(UnmanagedType.LPArray)] byte[] value, ref uint size);
        void SetCodecEnumerationSetting(ref Guid type, uint codec, [MarshalAs(UnmanagedType.LPWStr)] string name, uint dataType, [MarshalAs(UnmanagedType.LPArray)] byte[] value, uint size);
        void GetCodecEnumerationSetting(ref Guid type, uint codec, [MarshalAs(UnmanagedType.LPWStr)] string name, out uint dataType, [MarshalAs(UnmanagedType.LPArray)] byte[] value, ref uint size);
    }

    [ComImport, Guid("96406BDC-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWMStreamConfig
    {
        void GetStreamType(out Guid type);
        void GetStreamNumber(out ushort number);
        void SetStreamNumber(ushort number);
        void GetStreamName(IntPtr name, ref ushort length);
        void SetStreamName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetConnectionName(IntPtr name, ref ushort length);
        void SetConnectionName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetBitrate(out uint bitrate);
        void SetBitrate(uint bitrate);
        void GetBufferWindow(out uint window);
        void SetBufferWindow(uint window);
    }

    [ComImport, Guid("96406BCE-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWMMediaProps
    {
        void GetMajorType(out Guid type);
        void GetMediaType(IntPtr type, ref uint size);
        void SetMediaType(IntPtr type);
    }

    [ComImport, Guid("96406BD5-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWMInputMediaProps
    {
        void GetMajorType(out Guid type);
        void GetMediaType(IntPtr type, ref uint size);
        void SetMediaType(IntPtr type);
        void GetConnectionName(IntPtr name, ref ushort length);
        void GetGroupName(IntPtr name, ref ushort length);
    }

    [ComImport, Guid("72995A79-5090-42a4-9C8C-D9D0B6D34BE5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWMPropertyVault
    {
        void GetPropertyCount(out uint count);
        void GetPropertyByName([MarshalAs(UnmanagedType.LPWStr)] string name, out uint dataType, [MarshalAs(UnmanagedType.LPArray)] byte[] value, ref uint size);
        void SetProperty([MarshalAs(UnmanagedType.LPWStr)] string name, uint dataType, [MarshalAs(UnmanagedType.LPArray)] byte[] value, uint size);
    }

    [ComImport, Guid("96406BDB-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWMProfile
    {
        void GetVersion(out uint version);
        void GetName(IntPtr name, ref uint length);
        void SetName([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetDescription(IntPtr description, ref uint length);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string description);
        void GetStreamCount(out uint count);
        void GetStream(uint index, out IWMStreamConfig config);
        void GetStreamByNumber(ushort number, out IWMStreamConfig config);
        void RemoveStream(IWMStreamConfig config);
        void RemoveStreamByNumber(ushort number);
        void AddStream(IWMStreamConfig config);
    }

    [ComImport, Guid("96406BD4-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWMWriter
    {
        void SetProfileByID(ref Guid id);
        void SetProfile(IWMProfile profile);
        void SetOutputFilename([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetInputCount(out uint count);
        void GetInputProps(uint input, out IWMInputMediaProps props);
        void SetInputProps(uint input, IWMInputMediaProps props);
        void GetInputFormatCount(uint input, out uint count);
        void GetInputFormat(uint input, uint format, out IWMInputMediaProps props);
        void BeginWriting();
        void EndWriting();
        void AllocateSample(uint size, out INSSBuffer sample);
        void WriteSample(uint input, ulong time, uint flags, INSSBuffer sample);
        void Flush();
    }

    // wmsdkidl.h: SetAttribute(WORD stream, LPCWSTR name, WMT_ATTR_DATATYPE type, const BYTE* value, WORD length),
    // the length in bytes. The three methods before it keep it at its slot.
    [ComImport, Guid("96406BDA-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWMHeaderInfo
    {
        void GetAttributeCount(ushort stream, out ushort count);
        void GetAttributeByIndex(ushort index, ref ushort stream, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, ref ushort nameLength, out int type, [MarshalAs(UnmanagedType.LPArray)] byte[] value, ref ushort valueLength);
        void GetAttributeByName(ref ushort stream, [MarshalAs(UnmanagedType.LPWStr)] string name, out int type, [MarshalAs(UnmanagedType.LPArray)] byte[] value, ref ushort valueLength);
        void SetAttribute(ushort stream, [MarshalAs(UnmanagedType.LPWStr)] string name, int type, [MarshalAs(UnmanagedType.LPArray)] byte[] value, ushort valueLength);
    }

    [ComImport, Guid("96406BD9-2B2B-11d3-B36B-00C04F6108FF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IWMMetadataEditor
    {
        void Open([MarshalAs(UnmanagedType.LPWStr)] string file);
        void Close();
        void Flush();
    }

    [ComImport, Guid("E1CD3524-03D7-11d2-9EED-006097D2D7CF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface INSSBuffer
    {
        void GetLength(out uint length);
        void SetLength(uint length);
        void GetMaxLength(out uint length);
        void GetBuffer(out IntPtr buffer);
        void GetBufferAndLength(out IntPtr buffer, out uint length);
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WmMediaType
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

    // One format a codec offers, as the SDK enumerates it.
    public class WmaFormat
    {
        public uint Codec;
        public uint Index;
        public string Description;
        public int Tag;
        public int Channels;
        public int Rate;
        public int Bits;
        public int Bitrate;
        public IWMStreamConfig Config;

        public string Summary()
        {
            return string.Format("format {0}: \"{1}\" (tag 0x{2:X4}, {3} channels, {4} Hz, {5} bits, {6} bps)",
                Index, Description, Tag, Channels, Rate, Bits, Bitrate);
        }
    }

    // What one encode asks for: a format picked by the codec's exact name, the mode, the channel
    // count, the rate, the bits of the format and the quality or bitrate, and the PCM it is fed.
    public class EncodeRequest
    {
        public string Codec;
        public bool Vbr;
        public int Channels;
        public int[] Rates;
        public int Bits;
        public int Quality;
        public int Bitrate;
        public string InputPath;
        public string OutputPath;
        public string Title;
        public string Author;
    }

    public class EncodeResult
    {
        public string CodecName;
        public string FormatDescription;
        public int FormatIndex;
        public int FormatTag;
        public int Channels;
        public int Rate;
        public int FormatBits;
        public int Bitrate;
        public uint ConfigBitrate;
        public uint BufferWindow;
        public int InputBits;
        public string InputType;
        public long Frames;
        public long Bytes;
        public string Log;
        public bool TagsVerified;
    }

    public static class Wmf
    {
        static readonly Guid Audio = new Guid("73647561-0000-0010-8000-00AA00389B71");
        static readonly Guid Pcm = new Guid("00000001-0000-0010-8000-00AA00389B71");
        static readonly Guid WaveFormatEx = new Guid("05589f81-c356-11ce-bf01-00aa0055595a");
        const uint TypeDword = 0, TypeBool = 3;
        // WMT_ATTR_DATATYPE: DWORD 0, STRING 1, BINARY 2, BOOL 3, QWORD 4, WORD 5, GUID 6.
        const int TypeString = 1;
        const uint Version9 = 0x00090000;

        [DllImport("wmvcore.dll", PreserveSig = false)]
        static extern void WMCreateProfileManager(out IWMProfileManager manager);

        [DllImport("wmvcore.dll", PreserveSig = false)]
        static extern void WMCreateWriter(IntPtr reserved, out IWMWriter writer);

        [DllImport("wmvcore.dll", PreserveSig = false)]
        static extern void WMCreateEditor(out IWMMetadataEditor editor);

        // Reads one string attribute from the written file, which is what a reader will see.
        static string ReadString(IWMHeaderInfo header, string name)
        {
            ushort stream = 0, length = 0;
            int type;
            header.GetAttributeByName(ref stream, name, out type, null, ref length);
            byte[] value = new byte[length];
            header.GetAttributeByName(ref stream, name, out type, value, ref length);
            if (type != TypeString)
            {
                throw new InvalidDataException(string.Format("{0} has type {1}, not a string", name, type));
            }
            return Encoding.Unicode.GetString(value, 0, length).TrimEnd('\0');
        }

        static bool VerifyTags(EncodeRequest request, StringBuilder log)
        {
            IWMMetadataEditor editor = null;
            try
            {
                WMCreateEditor(out editor);
                editor.Open(request.OutputPath);
                IWMHeaderInfo header = (IWMHeaderInfo)editor;
                bool ok = true;
                string[][] expected = new[] { new[] { "Title", request.Title }, new[] { "Author", request.Author } };
                foreach (string[] pair in expected)
                {
                    if (string.IsNullOrEmpty(pair[1])) continue;
                    string actual = ReadString(header, pair[0]);
                    bool same = actual == pair[1];
                    log.AppendLine(string.Format("  read back {0}: \"{1}\" {2}", pair[0], actual, same ? "matches" : "DIFFERS from \"" + pair[1] + "\""));
                    ok &= same;
                }
                return ok;
            }
            catch (Exception e)
            {
                log.AppendLine("  reading the tags back failed: " + Hr(e));
                return false;
            }
            finally
            {
                if (editor != null)
                {
                    try { editor.Close(); } catch (Exception) { }
                    Marshal.ReleaseComObject(editor);
                }
            }
        }

        static string Hr(Exception e)
        {
            return string.Format("0x{0:X8} {1}", e.HResult, e.Message);
        }

        public static string[] CodecNames()
        {
            IWMProfileManager manager;
            WMCreateProfileManager(out manager);
            IWMCodecInfo3 info = (IWMCodecInfo3)manager;
            Guid audio = Audio;
            uint count;
            info.GetCodecInfoCount(ref audio, out count);
            string[] names = new string[count];
            for (uint c = 0; c < count; c++)
            {
                names[c] = CodecName(info, c);
            }
            return names;
        }

        static string CodecName(IWMCodecInfo3 info, uint codec)
        {
            Guid audio = Audio;
            StringBuilder name = new StringBuilder(512);
            uint length = 512;
            info.GetCodecName(ref audio, codec, name, ref length);
            return name.ToString();
        }

        static int FindCodec(IWMCodecInfo3 info, string name)
        {
            Guid audio = Audio;
            uint count;
            info.GetCodecInfoCount(ref audio, out count);
            int contains = -1;
            for (uint c = 0; c < count; c++)
            {
                string actual = CodecName(info, c);
                if (string.Equals(actual, name, StringComparison.OrdinalIgnoreCase)) { return (int)c; }
                if (contains < 0 && actual.IndexOf(name, StringComparison.OrdinalIgnoreCase) >= 0) { contains = (int)c; }
            }
            return contains;
        }

        // Every format of a codec in one mode (variable bitrate or not), with what its media type says.
        public static List<WmaFormat> Formats(IWMProfileManager manager, string codecName, bool vbr, StringBuilder log)
        {
            IWMCodecInfo3 info = (IWMCodecInfo3)manager;
            Guid audio = Audio;
            int codec = FindCodec(info, codecName);
            if (codec < 0)
            {
                log.AppendLine("no codec named " + codecName);
                return new List<WmaFormat>();
            }

            // Which formats the codec lists depends on the enumeration settings: _VBRENABLED and
            // _PASSESUSED (one pass: quality-based VBR or one-pass CBR; two passes: the two-pass
            // modes). The first combination that lists formats of the mode asked for is used, and
            // every attempt is logged, so a codec that lists no such format shows why.
            int[] passOptions = vbr ? new int[] { 0, 1 } : new int[] { 1, 2, 0 };
            List<WmaFormat> last = new List<WmaFormat>();
            foreach (int passes in passOptions)
            {
                string result = TrySet(info, audio, (uint)codec, "_VBRENABLED", TypeBool, vbr ? 1 : 0);
                if (passes > 0) { result += " " + TrySet(info, audio, (uint)codec, "_PASSESUSED", TypeDword, passes); }
                List<WmaFormat> listed = Enumerate(info, audio, codec, log);
                int variable = 0;
                foreach (WmaFormat f in listed) { if (IsVariable(f)) { variable++; } }
                log.AppendLine(string.Format("  attempt vbr={0} passes={1}: {2}; {3} formats, {4} with VBR in the name", vbr, passes == 0 ? "unset" : passes.ToString(), result.Trim(), listed.Count, variable));
                last = listed;
                if (vbr ? variable > 0 : variable < listed.Count && listed.Count > 0)
                {
                    // For constant bitrate only the formats that are not variable are kept.
                    if (vbr) { return listed; }
                    List<WmaFormat> constant = new List<WmaFormat>();
                    foreach (WmaFormat f in listed) { if (!IsVariable(f)) { constant.Add(f); } }
                    return constant;
                }
            }
            return vbr ? last : new List<WmaFormat>();
        }

        static bool IsVariable(WmaFormat f)
        {
            return f.Description.IndexOf("VBR", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string CheckProperty(IWMPropertyVault vault, string name, uint expected)
        {
            uint dataType, size = 4;
            byte[] back = new byte[4];
            vault.GetPropertyByName(name, out dataType, back, ref size);
            uint actual = BitConverter.ToUInt32(back, 0);
            return string.Format("  {0}={1} {2}, read back {3}", name, expected, actual == expected ? "ok" : "MISMATCH", actual);
        }

        static string TrySet(IWMCodecInfo3 info, Guid audio, uint codec, string name, uint type, int value)
        {
            try
            {
                info.SetCodecEnumerationSetting(ref audio, codec, name, type, BitConverter.GetBytes(value), 4);
                // A setting that did not arrive as sent (a byte[] marshaled as a SAFEARRAY once did that) fails the run.
                uint dataType, size = 4;
                byte[] back = new byte[4];
                info.GetCodecEnumerationSetting(ref audio, codec, name, out dataType, back, ref size);
                int actual = BitConverter.ToInt32(back, 0);
                return actual == value ? name + "=" + value + " ok, read back " + actual : name + "=" + value + " MISMATCH, read back " + actual;
            }
            catch (Exception e)
            {
                return name + "=" + value + " failed " + Hr(e);
            }
        }

        static List<WmaFormat> Enumerate(IWMCodecInfo3 info, Guid audio, int codec, StringBuilder log)
        {
            List<WmaFormat> formats = new List<WmaFormat>();
            uint count;
            info.GetCodecFormatCount(ref audio, (uint)codec, out count);
            for (uint f = 0; f < count; f++)
            {
                try
                {
                    IWMStreamConfig config;
                    StringBuilder desc = new StringBuilder(512);
                    uint length = 512;
                    info.GetCodecFormatDesc(ref audio, (uint)codec, f, out config, desc, ref length);
                    WmaFormat format = new WmaFormat();
                    format.Codec = (uint)codec;
                    format.Index = f;
                    format.Description = desc.ToString();
                    format.Config = config;
                    ReadFormat((IWMMediaProps)config, format);
                    uint bitrate;
                    config.GetBitrate(out bitrate);
                    format.Bitrate = (int)bitrate;
                    formats.Add(format);
                }
                catch (Exception e)
                {
                    log.AppendLine(string.Format("  format {0} failed: {1}", f, Hr(e)));
                }
            }
            return formats;
        }

        public static string ListFormats(string codecName, bool vbr)
        {
            StringBuilder log = new StringBuilder();
            IWMProfileManager manager;
            WMCreateProfileManager(out manager);
            log.AppendLine(string.Format("{0}, {1}:", codecName, vbr ? "variable bitrate" : "constant bitrate"));
            foreach (WmaFormat format in Formats(manager, codecName, vbr, log))
            {
                log.AppendLine("  " + format.Summary());
            }
            return log.ToString();
        }

        // The format closest to the request among those that match it exactly in channels, bits
        // (when given), and quality (variable bitrate), preferring the first of the rates that has
        // one and then the bitrate nearest to the one asked for.
        static WmaFormat Select(List<WmaFormat> formats, EncodeRequest request)
        {
            foreach (int rate in request.Rates)
            {
                WmaFormat best = null;
                foreach (WmaFormat f in formats)
                {
                    if (f.Channels != request.Channels || f.Rate != rate) { continue; }
                    if (request.Bits != 0 && f.Bits != request.Bits) { continue; }
                    if (!request.Vbr && IsVariable(f)) { continue; }
                    if (request.Vbr)
                    {
                        if (!System.Text.RegularExpressions.Regex.IsMatch(f.Description, "Quality " + request.Quality + @"\b")) { continue; }
                        if (best == null) { best = f; }
                    }
                    else
                    {
                        if (best == null || Math.Abs(f.Bitrate - request.Bitrate) < Math.Abs(best.Bitrate - request.Bitrate)) { best = f; }
                    }
                }
                if (best != null) { return best; }
            }
            return null;
        }

        // The format Encode would use, without encoding, so the caller can write the PCM that fits it.
        public static EncodeResult Preview(EncodeRequest request)
        {
            EncodeResult result = new EncodeResult();
            StringBuilder log = new StringBuilder();
            try
            {
                IWMProfileManager manager;
                WMCreateProfileManager(out manager);
                WmaFormat chosen = Select(Formats(manager, request.Codec, request.Vbr, log), request);
                if (chosen == null)
                {
                    log.AppendLine("no format of " + request.Codec + " matches");
                }
                else
                {
                    result.CodecName = CodecName((IWMCodecInfo3)manager, chosen.Codec);
                    result.FormatDescription = chosen.Description;
                    result.FormatIndex = (int)chosen.Index;
                    result.FormatTag = chosen.Tag;
                    result.Channels = chosen.Channels;
                    result.Rate = chosen.Rate;
                    result.FormatBits = chosen.Bits;
                    result.Bitrate = chosen.Bitrate;
                    log.AppendLine("would use " + chosen.Summary());
                }
            }
            catch (Exception e)
            {
                log.AppendLine("failed: " + Hr(e));
            }

            result.Log = log.ToString();
            return result;
        }

        public static EncodeResult Encode(EncodeRequest request)
        {
            EncodeResult result = new EncodeResult();
            StringBuilder log = new StringBuilder();
            result.Log = "";
            string phase = "listing formats";
            try
            {
                IWMProfileManager manager;
                WMCreateProfileManager(out manager);
                List<WmaFormat> formats = Formats(manager, request.Codec, request.Vbr, log);
                WmaFormat chosen = Select(formats, request);
                if (chosen == null)
                {
                    log.AppendLine("no format of " + request.Codec + " matches");
                    result.Log = log.ToString();
                    return result;
                }

                IWMCodecInfo3 info = (IWMCodecInfo3)manager;
                result.CodecName = CodecName(info, chosen.Codec);
                result.FormatDescription = chosen.Description;
                result.FormatIndex = (int)chosen.Index;
                result.FormatTag = chosen.Tag;
                result.Channels = chosen.Channels;
                result.Rate = chosen.Rate;
                result.FormatBits = chosen.Bits;
                result.Bitrate = chosen.Bitrate;
                log.AppendLine("chosen " + chosen.Summary());

                IWMStreamConfig config = chosen.Config;
                config.SetStreamNumber(1);
                config.SetStreamName("Audio");
                config.SetConnectionName("Audio");
                uint configBitrate, window;
                config.GetBitrate(out configBitrate);
                config.GetBufferWindow(out window);
                result.ConfigBitrate = configBitrate;
                result.BufferWindow = window;

                // The properties on the stream's own vault, as the probe found them to work;
                // _PASSESUSED fails with 0xC00D0BCD, so one pass is whatever the format says.
                IWMPropertyVault vault = (IWMPropertyVault)config;
                try
                {
                    if (request.Vbr)
                    {
                        vault.SetProperty("_VBRENABLED", TypeBool, BitConverter.GetBytes(1), 4);
                        vault.SetProperty("_VBRQUALITY", TypeDword, BitConverter.GetBytes((uint)request.Quality), 4);
                        log.AppendLine(CheckProperty(vault, "_VBRENABLED", 1));
                        log.AppendLine(CheckProperty(vault, "_VBRQUALITY", (uint)request.Quality));
                    }
                }
                catch (Exception e)
                {
                    log.AppendLine("  vault properties failed: " + Hr(e));
                }

                phase = "creating the profile";
                IWMProfile profile;
                manager.CreateEmptyProfile(Version9, out profile);
                profile.AddStream(config);

                phase = "creating the writer";
                IWMWriter writer;
                WMCreateWriter(IntPtr.Zero, out writer);
                writer.SetProfile(profile);
                phase = "setting the output";
                writer.SetOutputFilename(request.OutputPath);

                byte[] wav = File.ReadAllBytes(request.InputPath);
                int dataOffset, dataLength, channels, rate, bits;
                ReadWav(wav, out dataOffset, out dataLength, out channels, out rate, out bits);
                int frameSize = channels * (bits / 8);
                result.InputBits = bits;

                phase = "setting the input format";
                IWMInputMediaProps input;
                writer.GetInputProps(0, out input);
                string inputType = SetInput(writer, input, channels, rate, bits, log);
                result.InputType = inputType;

                if (!string.IsNullOrEmpty(request.Title))
                {
                    try
                    {
                        IWMHeaderInfo header = (IWMHeaderInfo)writer;
                        byte[] title = Encoding.Unicode.GetBytes(request.Title + "\0");
                        header.SetAttribute(0, "Title", TypeString, title, (ushort)title.Length);
                        if (!string.IsNullOrEmpty(request.Author))
                        {
                            byte[] author = Encoding.Unicode.GetBytes(request.Author + "\0");
                            header.SetAttribute(0, "Author", TypeString, author, (ushort)author.Length);
                        }
                    }
                    catch (Exception e)
                    {
                        log.AppendLine("  header attributes failed: " + Hr(e));
                    }
                }

                phase = "BeginWriting";
                writer.BeginWriting();
                phase = "WriteSample";
                int chunk = (rate / 10) * frameSize;
                long frames = 0;
                for (int offset = 0; offset < dataLength; offset += chunk)
                {
                    int size = Math.Min(chunk, dataLength - offset);
                    INSSBuffer sample;
                    writer.AllocateSample((uint)size, out sample);
                    IntPtr buffer;
                    sample.GetBuffer(out buffer);
                    Marshal.Copy(wav, dataOffset + offset, buffer, size);
                    sample.SetLength((uint)size);
                    writer.WriteSample(0, (ulong)(frames * 10000000L / rate), 0, sample);
                    Marshal.ReleaseComObject(sample);
                    frames += size / frameSize;
                }
                phase = "EndWriting";
                writer.EndWriting();
                result.Frames = frames;
                result.Bytes = new FileInfo(request.OutputPath).Length;
                if (!string.IsNullOrEmpty(request.Title))
                {
                    result.TagsVerified = VerifyTags(request, log);
                }
                log.AppendLine(string.Format("wrote {0} frames to {1}, {2} bytes", frames, Path.GetFileName(request.OutputPath), result.Bytes));
            }
            catch (Exception e)
            {
                log.AppendLine("failed while " + phase + ": " + Hr(e));
            }

            result.Log = log.ToString();
            return result;
        }

        // The input is the PCM of the WAV: tried as an extensible format where it has more than two
        // channels or more than 16 bits, else as a plain one; the format that was accepted is
        // returned.
        static string SetInput(IWMWriter writer, IWMInputMediaProps input, int channels, int rate, int bits, StringBuilder log)
        {
            bool extensible = channels > 2 || bits > 16;
            string[] order = extensible ? new string[] { "extensible", "plain" } : new string[] { "plain", "extensible" };
            foreach (string kind in order)
            {
                IntPtr format;
                IntPtr type = PcmType(channels, rate, bits, kind == "extensible", out format);
                try
                {
                    input.SetMediaType(type);
                    writer.SetInputProps(0, input);
                    log.AppendLine("  input accepted as " + kind + " PCM");
                    return kind;
                }
                catch (Exception e)
                {
                    log.AppendLine(string.Format("  input as {0} PCM refused: {1}", kind, Hr(e)));
                }
                finally
                {
                    Marshal.FreeCoTaskMem(type);
                    Marshal.FreeCoTaskMem(format);
                }
            }
            throw new InvalidOperationException("the writer accepted no input format");
        }

        static void ReadFormat(IWMMediaProps props, WmaFormat format)
        {
            uint size = 0;
            props.GetMediaType(IntPtr.Zero, ref size);
            IntPtr buffer = Marshal.AllocCoTaskMem((int)size);
            try
            {
                props.GetMediaType(buffer, ref size);
                WmMediaType type = (WmMediaType)Marshal.PtrToStructure(buffer, typeof(WmMediaType));
                format.Tag = Marshal.ReadInt16(type.Format, 0) & 0xFFFF;
                format.Channels = Marshal.ReadInt16(type.Format, 2);
                format.Rate = Marshal.ReadInt32(type.Format, 4);
                format.Bits = Marshal.ReadInt16(type.Format, 14);
            }
            finally
            {
                Marshal.FreeCoTaskMem(buffer);
            }
        }

        static IntPtr PcmType(int channels, int rate, int bits, bool extensible, out IntPtr format)
        {
            int align = channels * (bits / 8);
            byte[] wave = new byte[extensible ? 40 : 18];
            BitConverter.GetBytes((ushort)(extensible ? 0xFFFE : 1)).CopyTo(wave, 0);
            BitConverter.GetBytes((ushort)channels).CopyTo(wave, 2);
            BitConverter.GetBytes(rate).CopyTo(wave, 4);
            BitConverter.GetBytes(rate * align).CopyTo(wave, 8);
            BitConverter.GetBytes((ushort)align).CopyTo(wave, 12);
            BitConverter.GetBytes((ushort)bits).CopyTo(wave, 14);
            if (extensible)
            {
                BitConverter.GetBytes((ushort)22).CopyTo(wave, 16);
                BitConverter.GetBytes((ushort)bits).CopyTo(wave, 18);
                BitConverter.GetBytes(ChannelMask(channels)).CopyTo(wave, 20);
                Pcm.ToByteArray().CopyTo(wave, 24);
            }
            format = Marshal.AllocCoTaskMem(wave.Length);
            Marshal.Copy(wave, 0, format, wave.Length);
            WmMediaType type = new WmMediaType();
            type.MajorType = Audio;
            type.SubType = Pcm;
            type.FixedSizeSamples = 1;
            type.TemporalCompression = 0;
            type.SampleSize = (uint)align;
            type.FormatType = WaveFormatEx;
            type.Unknown = IntPtr.Zero;
            type.FormatSize = (uint)wave.Length;
            type.Format = format;
            IntPtr pointer = Marshal.AllocCoTaskMem(Marshal.SizeOf(typeof(WmMediaType)));
            Marshal.StructureToPtr(type, pointer, false);
            return pointer;
        }

        // The WAVE channel masks of the common layouts: front center, front left and right, 5.1.
        static int ChannelMask(int channels)
        {
            switch (channels)
            {
                case 1: return 0x4;
                case 2: return 0x3;
                case 4: return 0x33;
                case 6: return 0x3F;
                case 8: return 0x63F;
                default: return 0;
            }
        }

        // The data chunk of a plain PCM WAV, as Python's wave module writes it.
        static void ReadWav(byte[] wav, out int dataOffset, out int dataLength, out int channels, out int rate, out int bits)
        {
            dataOffset = 0;
            dataLength = 0;
            channels = 0;
            rate = 0;
            bits = 0;
            int position = 12;
            while (position + 8 <= wav.Length)
            {
                string id = Encoding.ASCII.GetString(wav, position, 4);
                int size = BitConverter.ToInt32(wav, position + 4);
                if (id == "fmt ")
                {
                    channels = BitConverter.ToUInt16(wav, position + 10);
                    rate = BitConverter.ToInt32(wav, position + 12);
                    bits = BitConverter.ToUInt16(wav, position + 22);
                }
                else if (id == "data")
                {
                    dataOffset = position + 8;
                    dataLength = Math.Min(size, wav.Length - dataOffset);
                    return;
                }
                position += 8 + size + (size & 1);
            }
            throw new InvalidDataException("the WAV has no data chunk");
        }
    }
}

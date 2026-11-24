// Helpers of make_windows_tak_fixtures.ps1, compiled by Add-Type under Windows PowerShell 5.1: this is C# 5 (no string
// interpolation, no ?. , no expression-bodied members, no out var).
//
// TakRun starts a program with the arguments as one string, optionally writing a file's bytes to its standard input
// (a pipe; PowerShell's own pipeline would re-encode them) and collecting its output and exit code.
// TakSdk opens a TAK file with the 64-bit decoder library (tak_deco_lib.dll 2.3.3, a black box) and reads what the SDK
// documents: the stream info, the encoder info, the wave data sizes and the MD5. ReadAll reads all the audio of a file through
// tak_SSD_ReadAudio (signature in the SDK header tak_deco_lib.h and ssd_ref.html), call by call, with the damage callback. The structures are packed, as the SDK asks
// (Sdk.html: "Structures have to be packed"), and the functions are cdecl.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Encspect
{
    public class TakRunResult
    {
        public int ExitCode;
        public string Stdout;
        public string Stderr;
        public bool TimedOut;
        public string Error;
    }

    public static class TakRun
    {
        public static TakRunResult Run(string exe, string arguments, string stdinPath, string workingDirectory, int timeoutMs)
        {
            TakRunResult result = new TakRunResult();
            result.ExitCode = -1;
            result.Stdout = "";
            result.Stderr = "";
            ProcessStartInfo start = new ProcessStartInfo(exe, arguments);
            start.UseShellExecute = false;
            start.RedirectStandardOutput = true;
            start.RedirectStandardError = true;
            // PowerShell hands an empty string for a $null string argument.
            bool hasInput = !string.IsNullOrEmpty(stdinPath);
            start.RedirectStandardInput = hasInput;
            start.CreateNoWindow = true;
            if (!string.IsNullOrEmpty(workingDirectory))
            {
                start.WorkingDirectory = workingDirectory;
            }

            StringBuilder output = new StringBuilder();
            StringBuilder error = new StringBuilder();
            object gate = new object();
            using (Process process = new Process())
            {
                process.StartInfo = start;
                process.OutputDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) { lock (gate) { output.AppendLine(e.Data); } }
                };
                process.ErrorDataReceived += delegate (object sender, DataReceivedEventArgs e)
                {
                    if (e.Data != null) { lock (gate) { error.AppendLine(e.Data); } }
                };
                try
                {
                    process.Start();
                }
                catch (Exception e)
                {
                    result.Error = e.Message;
                    return result;
                }

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();
                if (hasInput)
                {
                    try
                    {
                        byte[] bytes = File.ReadAllBytes(stdinPath);
                        Stream stdin = process.StandardInput.BaseStream;
                        int offset = 0;
                        while (offset < bytes.Length)
                        {
                            int n = Math.Min(65536, bytes.Length - offset);
                            stdin.Write(bytes, offset, n);
                            offset += n;
                        }

                        stdin.Flush();
                        stdin.Close();
                    }
                    catch (IOException e)
                    {
                        // The program closed its input early; its exit code and output say why.
                        result.Error = "writing to standard input: " + e.Message;
                    }
                }

                if (!process.WaitForExit(timeoutMs))
                {
                    result.TimedOut = true;
                    try { process.Kill(); } catch (Exception) { }
                }

                process.WaitForExit();
                result.ExitCode = process.ExitCode;
            }

            lock (gate)
            {
                result.Stdout = output.ToString();
                result.Stderr = error.ToString();
            }

            return result;
        }

        // The offset and length of the data chunk's bytes of a RIFF/WAVE file (a length that runs past the end, or the
        // 0xFFFFFFFF of a streamed file, is cut at the end of the file); null when there is no data chunk.
        public static long[] WavData(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            if (b.Length < 12 || b[0] != 'R' || b[1] != 'I' || b[2] != 'F' || b[3] != 'F')
            {
                return null;
            }

            long position = 12;
            while (position + 8 <= b.Length)
            {
                long size = BitConverter.ToUInt32(b, (int)position + 4);
                if (b[position] == 'd' && b[position + 1] == 'a' && b[position + 2] == 't' && b[position + 3] == 'a')
                {
                    long length = Math.Min(size, b.Length - position - 8);
                    return new long[] { position + 8, length };
                }

                position += 8 + size + (size & 1);
            }

            return null;
        }

        public static bool SameBytes(string pathA, long offsetA, string pathB, long offsetB, long length)
        {
            byte[] a = File.ReadAllBytes(pathA);
            byte[] b = File.ReadAllBytes(pathB);
            if (offsetA + length > a.Length || offsetB + length > b.Length)
            {
                return false;
            }

            for (long i = 0; i < length; i++)
            {
                if (a[offsetA + i] != b[offsetB + i])
                {
                    return false;
                }
            }

            return true;
        }
    }

    public static class TakSdk
    {
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct SsdOptions { public int Cpu; public int Flags; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct StreamInfoV22
        {
            public int Codec;
            public int Profile;
            public int FrameSize;
            public int FrameSizeInSamples;
            public long SampleNum;
            public int DataType;
            public int SampleRate;
            public int SampleBits;
            public int ChannelNum;
            public int BlockSize;
            public int HasExtension;
            public int ValidBitsPerSample;
            public int HasSpeakerAssignment;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
            public byte[] SpeakerAssignment;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct EncoderInfo { public int Version; public int Preset; public int Evaluation; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WaveDesc { public int HeadSize; public int TailSize; }

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct StateInfo
        {
            public int OpenResult;
            public int SumResult;
            public long StreamSampleNum;
            public long ReadSampleNum;
            public long DamagedSampleNum;
            public int SkippedDataBlockNum;
        }

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int GetLibraryVersionFn(out int version, out int compatibility);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr CreateFromFileFn([MarshalAs(UnmanagedType.LPStr)] string path, ref SsdOptions options, IntPtr callback, IntPtr user);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int DecoderFn(IntPtr decoder);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DestroyFn(IntPtr decoder);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int StreamInfoFn(IntPtr decoder, ref StreamInfoV22 info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int EncoderInfoFn(IntPtr decoder, ref EncoderInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int WaveDescFn(IntPtr decoder, ref WaveDesc desc);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int Md5Fn(IntPtr decoder, byte[] md5);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int StateInfoFn(IntPtr decoder, ref StateInfo info);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int TextFn(int value, byte[] text, int size);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int ReadAudioFn(IntPtr decoder, byte[] samples, int sampleNum, out int readNum);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate long ReadPosFn(IntPtr decoder);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate void DamageCallbackFn(IntPtr user, IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int ErrorStringFn(int error, byte[] text, int size);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryW(string path);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, SetLastError = true)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);

        static T Function<T>(IntPtr module, string name) where T : class
        {
            IntPtr address = GetProcAddress(module, name);
            if (address == IntPtr.Zero)
            {
                throw new InvalidOperationException(name + " is not exported");
            }

            return (T)(object)Marshal.GetDelegateForFunctionPointer(address, typeof(T));
        }

        static string Text(TextFn function, int value)
        {
            byte[] buffer = new byte[128];
            int result = function(value, buffer, buffer.Length - 1);
            int length = 0;
            while (length < buffer.Length && buffer[length] != 0) { length++; }
            return result == 0 ? Encoding.ASCII.GetString(buffer, 0, length) : "(result " + result + ")";
        }

        static string Version(int v)
        {
            return ((v >> 16) & 255) + "." + ((v >> 8) & 255) + "." + (v & 255);
        }

        public static int LibrarySize()
        {
            return Marshal.SizeOf(typeof(StreamInfoV22));
        }

        // What the library says about the file: the structures of the SDK header's V22 functions. A value the SDK does not
        // return (no encoder info in a TAK 1.0 file) is the result code of its function.
        public static OrderedDictionary Probe(string library, string file)
        {
            OrderedDictionary r = new OrderedDictionary();
            r["structSize"] = Marshal.SizeOf(typeof(StreamInfoV22));
            if (Marshal.SizeOf(typeof(StreamInfoV22)) != 72)
            {
                r["error"] = "Ttak_str_StreamInfo_V22 packs to " + Marshal.SizeOf(typeof(StreamInfoV22)) + " bytes, not 72";
                return r;
            }

            IntPtr module = LoadLibraryW(library);
            if (module == IntPtr.Zero)
            {
                r["error"] = "LoadLibrary failed: " + Marshal.GetLastWin32Error();
                return r;
            }

            GetLibraryVersionFn getVersion = Function<GetLibraryVersionFn>(module, "tak_GetLibraryVersion");
            CreateFromFileFn create = Function<CreateFromFileFn>(module, "tak_SSD_Create_FromFile");
            DecoderFn valid = Function<DecoderFn>(module, "tak_SSD_Valid");
            DecoderFn state = Function<DecoderFn>(module, "tak_SSD_State");
            DecoderFn frameSize = Function<DecoderFn>(module, "tak_SSD_GetFrameSize");
            DestroyFn destroy = Function<DestroyFn>(module, "tak_SSD_Destroy");
            StreamInfoFn streamInfo = Function<StreamInfoFn>(module, "tak_SSD_GetStreamInfo_V22");
            EncoderInfoFn encoderInfo = Function<EncoderInfoFn>(module, "tak_SSD_GetEncoderInfo");
            WaveDescFn waveDesc = Function<WaveDescFn>(module, "tak_SSD_GetSimpleWaveDataDesc");
            Md5Fn md5 = Function<Md5Fn>(module, "tak_SSD_GetMD5");
            StateInfoFn stateInfo = Function<StateInfoFn>(module, "tak_SSD_GetStateInfo");
            TextFn codecName = Function<TextFn>(module, "tak_GetCodecName");

            int version;
            int compatibility;
            r["libraryVersionResult"] = getVersion(out version, out compatibility);
            r["libraryVersion"] = "0x" + version.ToString("X8");
            r["libraryCompatibility"] = "0x" + compatibility.ToString("X8");

            SsdOptions options = new SsdOptions();
            options.Cpu = 0x3FF; // tak_Cpu_Any
            options.Flags = 0;
            IntPtr decoder = create(file, ref options, IntPtr.Zero, IntPtr.Zero);
            if (decoder == IntPtr.Zero)
            {
                r["error"] = "tak_SSD_Create_FromFile returned NULL";
                return r;
            }

            try
            {
                r["valid"] = valid(decoder);
                r["state"] = state(decoder);

                StreamInfoV22 si = new StreamInfoV22();
                si.SpeakerAssignment = new byte[16];
                int siResult = streamInfo(decoder, ref si);
                r["streamInfoResult"] = siResult;
                if (siResult == 0)
                {
                    r["codec"] = si.Codec;
                    r["codecName"] = Text(codecName, si.Codec);
                    r["profile"] = si.Profile;
                    r["frameSizeType"] = si.FrameSize;
                    r["frameSizeInSamples"] = si.FrameSizeInSamples;
                    r["sampleCount"] = si.SampleNum;
                    r["dataType"] = si.DataType;
                    r["sampleRate"] = si.SampleRate;
                    r["bitsPerSample"] = si.SampleBits;
                    r["channels"] = si.ChannelNum;
                    r["blockSize"] = si.BlockSize;
                    r["hasExtension"] = si.HasExtension;
                    r["validBitsPerSample"] = si.ValidBitsPerSample;
                    r["hasSpeakerAssignment"] = si.HasSpeakerAssignment;
                    List<int> speakers = new List<int>();
                    for (int i = 0; i < si.ChannelNum && i < 16; i++) { speakers.Add(si.SpeakerAssignment[i]); }
                    r["speakerAssignment"] = speakers.ToArray();
                }

                EncoderInfo ei = new EncoderInfo();
                int eiResult = encoderInfo(decoder, ref ei);
                r["encoderInfoResult"] = eiResult;
                if (eiResult == 0)
                {
                    r["encoderVersion"] = Version(ei.Version);
                    r["encoderVersionRaw"] = "0x" + ei.Version.ToString("X6");
                    r["preset"] = ei.Preset;
                    r["evaluation"] = ei.Evaluation;
                }

                WaveDesc wd = new WaveDesc();
                int wdResult = waveDesc(decoder, ref wd);
                r["waveDataResult"] = wdResult;
                if (wdResult == 0)
                {
                    r["waveHeaderSize"] = wd.HeadSize;
                    r["waveFooterSize"] = wd.TailSize;
                }

                byte[] sum = new byte[16];
                int md5Result = md5(decoder, sum);
                r["md5Result"] = md5Result;
                if (md5Result == 0)
                {
                    StringBuilder hex = new StringBuilder();
                    for (int i = 0; i < 16; i++) { hex.Append(sum[i].ToString("x2")); }
                    r["md5"] = hex.ToString();
                }

                r["frameSize"] = frameSize(decoder);
                StateInfo st = new StateInfo();
                int stResult = stateInfo(decoder, ref st);
                r["stateInfoResult"] = stResult;
                if (stResult == 0)
                {
                    r["openResult"] = st.OpenResult;
                    r["sumResult"] = st.SumResult;
                    r["streamSampleCount"] = st.StreamSampleNum;
                }
            }
            finally
            {
                destroy(decoder);
            }

            return r;
        }

        // Reads all audio of the file with tak_SSD_ReadAudio, one frame (tak_SSD_GetFrameSize samples) per call as the SDK suggests for a sequential read, until a call
        // reads no samples, fails fatally (tak_res_ssd_FatalErrorFirst, 4, and above) or 100000 calls have been made. Per call: the position before it, the samples asked
        // and returned, the result code (tak_res_ssd_FrameDamaged is 3, "no fatal error") and the position after it. The damage callback's items (position and size in samples)
        // are listed apart, and tak_SSD_GetStateInfo is read at the end. pcmMd5 is the MD5 of the samples returned in total, in the canonical layout of the manifests
        // (signed, little-endian, whole bytes; 8-bit data, which the library gives unsigned, is converted).
        public static OrderedDictionary ReadAll(string library, string file)
        {
            OrderedDictionary r = new OrderedDictionary();
            IntPtr module = LoadLibraryW(library);
            if (module == IntPtr.Zero)
            {
                r["error"] = "LoadLibrary failed: " + Marshal.GetLastWin32Error();
                return r;
            }

            CreateFromFileFn create = Function<CreateFromFileFn>(module, "tak_SSD_Create_FromFile");
            DecoderFn valid = Function<DecoderFn>(module, "tak_SSD_Valid");
            DecoderFn frameSize = Function<DecoderFn>(module, "tak_SSD_GetFrameSize");
            DestroyFn destroy = Function<DestroyFn>(module, "tak_SSD_Destroy");
            StreamInfoFn streamInfo = Function<StreamInfoFn>(module, "tak_SSD_GetStreamInfo_V22");
            StateInfoFn stateInfo = Function<StateInfoFn>(module, "tak_SSD_GetStateInfo");
            ReadAudioFn readAudio = Function<ReadAudioFn>(module, "tak_SSD_ReadAudio");
            ReadPosFn readPos = Function<ReadPosFn>(module, "tak_SSD_GetReadPos");
            ErrorStringFn errorString = Function<ErrorStringFn>(module, "tak_SSD_GetErrorString");

            List<OrderedDictionary> damage = new List<OrderedDictionary>();
            // TtakSSDDamageItem: SamplePosition and SampleSize, both Int64.
            DamageCallbackFn callback = delegate (IntPtr user, IntPtr item)
            {
                OrderedDictionary d = new OrderedDictionary();
                d["position"] = Marshal.ReadInt64(item, 0);
                d["samples"] = Marshal.ReadInt64(item, 8);
                damage.Add(d);
            };
            IntPtr callbackPointer = Marshal.GetFunctionPointerForDelegate(callback);

            SsdOptions options = new SsdOptions();
            options.Cpu = 0x3FF; // tak_Cpu_Any
            options.Flags = 0;
            IntPtr decoder = create(file, ref options, callbackPointer, IntPtr.Zero);
            if (decoder == IntPtr.Zero)
            {
                r["error"] = "tak_SSD_Create_FromFile returned NULL";
                return r;
            }

            try
            {
                r["valid"] = valid(decoder);
                StreamInfoV22 si = new StreamInfoV22();
                si.SpeakerAssignment = new byte[16];
                int siResult = streamInfo(decoder, ref si);
                r["streamInfoResult"] = siResult;
                if (siResult != 0 || valid(decoder) == 0 || si.ChannelNum < 1 || si.SampleBits < 1)
                {
                    r["error"] = "the library does not take the file as valid, or gives no stream info";
                    return r;
                }

                int bytesPerSample = (si.SampleBits + 7) / 8;
                int sampleFrame = si.ChannelNum * bytesPerSample;
                int perCall = frameSize(decoder);
                if (perCall < 1 || perCall > 1 << 20)
                {
                    perCall = 4096;
                }

                r["samplesPerCall"] = perCall;
                byte[] buffer = new byte[perCall * sampleFrame];
                MD5 md5 = MD5.Create();
                md5.Initialize();
                long total = 0;
                int fatal = 0;
                List<OrderedDictionary> calls = new List<OrderedDictionary>();
                for (int call = 0; call < 100000; call++)
                {
                    OrderedDictionary c = new OrderedDictionary();
                    c["position"] = readPos(decoder);
                    int read;
                    int result = readAudio(decoder, buffer, perCall, out read);
                    c["asked"] = perCall;
                    c["read"] = read;
                    c["result"] = result;
                    if (result != 0)
                    {
                        byte[] text = new byte[128];
                        int ok = errorString(result, text, text.Length - 1);
                        int length = 0;
                        while (length < text.Length && text[length] != 0) { length++; }
                        c["resultText"] = ok == 0 ? Encoding.ASCII.GetString(text, 0, length) : "(result " + ok + ")";
                    }

                    c["positionAfter"] = readPos(decoder);
                    calls.Add(c);
                    if (read > 0 && read <= perCall)
                    {
                        int count = read * sampleFrame;
                        if (si.SampleBits <= 8)
                        {
                            for (int i = 0; i < count; i++) { buffer[i] = (byte)((buffer[i] - 128) & 255); }
                        }

                        md5.TransformBlock(buffer, 0, count, null, 0);
                        total += read;
                    }

                    if (result >= 4) { fatal = result; break; }
                    if (read <= 0) { break; }
                }

                md5.TransformFinalBlock(new byte[0], 0, 0);
                StringBuilder hex = new StringBuilder();
                foreach (byte b in md5.Hash) { hex.Append(b.ToString("x2")); }
                r["calls"] = calls.ToArray();
                r["callCount"] = calls.Count;
                r["samples"] = total;
                r["pcmMd5"] = hex.ToString();
                r["fatalResult"] = fatal;
                r["damage"] = damage.ToArray();
                StateInfo st = new StateInfo();
                int stResult = stateInfo(decoder, ref st);
                r["stateInfoResult"] = stResult;
                if (stResult == 0)
                {
                    r["openResult"] = st.OpenResult;
                    r["sumResult"] = st.SumResult;
                    r["streamSampleCount"] = st.StreamSampleNum;
                    r["readSampleCount"] = st.ReadSampleNum;
                    r["damagedSampleCount"] = st.DamagedSampleNum;
                    r["skippedDataBlockCount"] = st.SkippedDataBlockNum;
                }
            }
            finally
            {
                destroy(decoder);
                GC.KeepAlive(callback);
            }

            return r;
        }
    }
}

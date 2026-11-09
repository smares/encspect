// Helpers of make_windows_tak_fixtures.ps1, compiled by Add-Type under Windows PowerShell 5.1: this is C# 5 (no string
// interpolation, no ?. , no expression-bodied members, no out var).
//
// TakRun starts a program with the arguments as one string, optionally writing a file's bytes to its standard input
// (a pipe; PowerShell's own pipeline would re-encode them) and collecting its output and exit code.
// TakSdk opens a TAK file with the 64-bit decoder library (tak_deco_lib.dll 2.3.3, a black box) and reads what the SDK
// documents: the stream info, the encoder info, the wave data sizes and the MD5. The structures are packed, as the SDK asks
// (Sdk.html: "Structures have to be packed"), and the functions are cdecl.
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
    }
}

using System.Runtime.CompilerServices;
using Encspect.Core;

namespace Encspect.Formats.Ac3.Decoding;

// Transient pre-noise processing (A/52:2018 Annex E 3.7): where a frame states a transient and a length for a channel, the decoded PCM before the transient is overwritten with a copy
// of the PCM before the pre-noise, cross-faded in and out. The correction reaches back into earlier samples and up to the transient, which can lie in a later frame, so the program's
// output waits in this buffer: a sample leaves it when no correction that is known or could still come may change it (a later frame's corrections start no more than Reach samples
// before the frame), and the samples the corrections read are kept for as long as they might be read.
//
// The cross-fade windows are the Hanning windows the specification names ("nearly any pair of constant amplitude cross-fade windows may be used ... standard Hanning windows have been
// shown to provide good results", 3.7.2): raised cosines that sum to 1, sampled at the middle of each step. They are not normative, so another decoder's samples near a corrected
// transient differ slightly from these.
internal sealed class PreNoise
{
    private const int Tc1 = 256;
    private const int Tc2 = 128;
    private const int BlockSamples = 256;

    // The furthest a correction of a frame starts before the frame: the transient lies at or after the frame's start, its pre-noise is below two blocks (PN < 512) and its length is
    // below 256 (8 bits), so the correction starts at most PN + length + TC1 < 1,024 samples before the transient.
    private const int Reach = 1024;

    // And the furthest back that it reads: 2 * TC1 + 2 * PN samples before the transient.
    private const int ReadReach = (2 * Tc1) + (2 * 512);

    private static readonly float[] FadeIn1 = Window(Tc1);
    private static readonly float[] FadeIn2 = Window(Tc2);

    private readonly int _channels;
    private readonly Action<long, float[], int> _emit;
    private readonly Action<long> _charge;
    private readonly List<(int Channel, long Location, int Length)> _pending = [];
    private float[][] _buffer;
    private long _base;
    private int _length;
    private long _emitted;

    // emit gets the first position, the planar samples and their count per channel; charge the work of a correction, in samples it touches.
    public PreNoise(int channels, Action<long, float[], int> emit, Action<long> charge)
    {
        _channels = channels;
        _emit = emit;
        _charge = charge;
        _buffer = [.. Enumerable.Range(0, channels).Select(_ => new float[8192])];
    }

    private static float[] Window(int length)
    {
        var window = new float[length];
        for (int n = 0; n < length; n++)
        {
            window[n] = (float)(0.5 * (1.0 - DetMath.Cos(Math.PI * (n + 0.5) / length)));
        }

        return window;
    }

    // A channel's transient of a frame, at the sample position location, with the time scaling length.
    public void Add(int channel, long location, int length) => _pending.Add((channel, location, length));

    // The next samples of the program (planar, count for each channel). A run that does not follow the one before it ends that one.
    public void Append(long first, float[] planar, int count)
    {
        if (_length > 0 && first != _base + _length)
        {
            Flush();
        }

        if (_length == 0)
        {
            _base = first;
            _emitted = first;
        }

        EnsureCapacity(_length + count);
        for (int c = 0; c < _channels; c++)
        {
            Array.Copy(planar, c * count, _buffer[c], _length, count);
        }

        _length += count;
        Apply();
        Release(force: false);
    }

    // Everything that is left leaves, the corrections that cannot be made (their transient is not there, or what they read is gone) are dropped, and the next run starts anew.
    public void Flush()
    {
        Apply();
        Release(force: true);
        _pending.Clear();
        _length = 0;
    }

    private void EnsureCapacity(int needed)
    {
        if (needed <= _buffer[0].Length)
        {
            return;
        }

        for (int c = 0; c < _channels; c++)
        {
            Array.Resize(ref _buffer[c], Math.Max(needed, _buffer[c].Length * 2));
        }
    }

    // The corrections whose transient has arrived.
    private void Apply()
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            (int channel, long location, int length) = _pending[i];
            if (location > _base + _length)
            {
                continue;
            }

            _pending.RemoveAt(i--);
            Correct(channel, location, length);
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Correct(int channel, long location, int length)
    {
        // The audio coding block before the one that holds the transient starts the pre-noise (3.7.2, Figure E3.2b).
        long blockStart = ((location / BlockSamples) - 1) * BlockSamples;
        int pn = (int)(location - blockStart);
        int total = pn + length + Tc1;
        long start = location - total;
        long source = location - ((2 * Tc1) + (2 * pn));
        if (channel >= _channels || source < _base || start < _emitted || length >= Tc1)
        {
            return;
        }

        _charge(4L * (((2 * Tc1) + pn) + total));
        float[] pcm = _buffer[channel];
        var synth = new float[(2 * Tc1) + pn];
        Array.Copy(pcm, (int)(source - _base), synth, 0, synth.Length);
        int at = (int)(start - _base);
        for (int n = 0; n < Tc1; n++)
        {
            pcm[at + n] = (pcm[at + n] * (1.0f - FadeIn1[n])) + (synth[n] * FadeIn1[n]);
        }

        for (int n = Tc1; n < total - Tc2; n++)
        {
            pcm[at + n] = synth[n];
        }

        for (int n = total - Tc2; n < total; n++)
        {
            float into = FadeIn2[n - (total - Tc2)];
            pcm[at + n] = (pcm[at + n] * into) + (synth[n] * (1.0f - into));
        }
    }

    // The samples no correction can change leave; the history the corrections read stays.
    private void Release(bool force)
    {
        long end = _base + _length;
        long limit = force ? end : Math.Min(end, end - Reach);
        foreach ((_, long location, int length) in _pending)
        {
            long blockStart = ((location / BlockSamples) - 1) * BlockSamples;
            limit = Math.Min(limit, location - ((int)(location - blockStart) + length + Tc1));
        }

        if (limit > _emitted)
        {
            int count = (int)(limit - _emitted);
            var planar = new float[_channels * count];
            for (int c = 0; c < _channels; c++)
            {
                Array.Copy(_buffer[c], (int)(_emitted - _base), planar, c * count, count);
            }

            _emit(_emitted, planar, count);
            _emitted = limit;
        }

        long keep = _emitted - ReadReach;
        if (!force && keep - _base > 8192)
        {
            int drop = (int)(keep - _base);
            for (int c = 0; c < _channels; c++)
            {
                Array.Copy(_buffer[c], drop, _buffer[c], 0, _length - drop);
            }

            _base += drop;
            _length -= drop;
        }
    }
}

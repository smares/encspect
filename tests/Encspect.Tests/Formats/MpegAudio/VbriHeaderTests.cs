using Encspect.Formats.MpegAudio;

namespace Encspect.Tests;

// A VBRI header is taken only after zero side information of the frame's own length (VbrHeader.TryRead): in MPEG-1 stereo that is 32 bytes, up to the header at
// byte 36 (less with a CRC), and in MPEG-2 and in mono fewer, so bytes between the side information and the header may be set, as Nero's mp3PRO plug-in sets
// three of them (the fixture external-nero-rom-mp3pro-vbr-low.mp3).
public class VbriHeaderTests
{
    private const byte Mpeg1 = 0xFB;
    private const byte Mpeg1WithCrc = 0xFA;
    private const byte Mpeg2 = 0xF3;
    private const byte Stereo = 0x04;
    private const byte Mono = 0xC4;

    private static (byte[] Frame, MpegHeader Header) Frame(byte version, byte mode, params (int At, byte Value)[] set)
    {
        byte[] frame = new byte[200];
        frame[0] = 0xFF;
        frame[1] = version;
        frame[2] = 0x90;
        frame[3] = mode;
        "VBRI"u8.CopyTo(frame.AsSpan(36));
        frame[40 + 1] = 1; // version 1
        frame[40 + 9] = 100; // byte count
        frame[40 + 13] = 3; // frame count
        frame[40 + 19] = 1; // entry size
        foreach ((int at, byte value) in set)
        {
            frame[at] = value;
        }

        return (frame, new MpegHeader((uint)((frame[0] << 24) | (frame[1] << 16) | (frame[2] << 8) | frame[3])));
    }

    private static VbrHeader? Read(byte version, byte mode, params (int At, byte Value)[] set)
    {
        (byte[] frame, MpegHeader header) = Frame(version, mode, set);
        return VbrHeader.TryRead(frame, header);
    }

    [Fact]
    public void ZeroBytesBeforeTheHeaderAreAccepted()
    {
        VbrHeader? vbri = Read(Mpeg1, Stereo);
        Assert.NotNull(vbri);
        Assert.True(vbri.IsVbri);
        Assert.Equal(3u, vbri.FrameCount);
    }

    [Fact]
    public void Mpeg2StereoAcceptsSetBytesAfterItsSeventeenBytesOfSideInformation()
    {
        Assert.NotNull(Read(Mpeg2, Stereo, (21, 0xC0), (22, 0x1E), (23, 0xE8)));
        Assert.NotNull(Read(Mpeg2, Stereo, (35, 1)));
    }

    [Fact]
    public void Mpeg2StereoRejectsASetByteInsideItsSideInformation()
    {
        Assert.Null(Read(Mpeg2, Stereo, (4, 1)));
        Assert.Null(Read(Mpeg2, Stereo, (20, 1)));
    }

    [Fact]
    public void Mpeg1StereoRejectsASetByteInsideItsThirtyTwoBytesOfSideInformation()
    {
        Assert.Null(Read(Mpeg1, Stereo, (4, 1)));
        Assert.Null(Read(Mpeg1, Stereo, (21, 1)));
        Assert.Null(Read(Mpeg1, Stereo, (35, 1)));
    }

    [Fact]
    public void Mpeg1MonoAcceptsSetBytesAfterItsSeventeenBytesOfSideInformation()
    {
        Assert.NotNull(Read(Mpeg1, Mono, (21, 1), (30, 7)));
        Assert.Null(Read(Mpeg1, Mono, (20, 1)));
    }

    [Fact]
    public void ACrcDoesNotCountAsSideInformationAndShortensTheSpanInMpeg1Stereo()
    {
        Assert.NotNull(Read(Mpeg1WithCrc, Stereo, (4, 0xAB), (5, 0xCD)));
        Assert.Null(Read(Mpeg1WithCrc, Stereo, (35, 1)));
    }
}

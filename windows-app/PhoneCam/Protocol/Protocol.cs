using System.Buffers.Binary;

namespace PhoneCam.Protocol;

public static class WireProtocol
{
    public const ushort Magic = 0x5043; // "PC"
    public const byte Version = 1;
    public const int HeaderSize = 24;
    public const int MaxPayloadBytes = 16 * 1024 * 1024;
    public const int DefaultPort = 18420;
    public const int DiscoveryPort = 18421;
    public const string DiscoveryProbe = "PHONECAM_DISCOVER_V1";
    public const string DiscoveryReplyPrefix = "PHONECAM_HERE_V1|";

    // Phone -> PC
    public const byte TypeVideoFrame = 0x01;
    public const byte TypeCodecConfig = 0x02;
    public const byte TypeStreamInfo = 0x03;
    public const byte TypeAudioFrame = 0x04;

    // PC -> Phone
    public const byte TypeRequestKeyframe = 0x10;
    public const byte TypePing = 0x11;
    public const byte TypePong = 0x12;
    public const byte TypeSetConfig = 0x13;
    public const byte TypeAudioControl = 0x14;

    public const int AudioFrameHeaderSize = 4;

    public const byte CameraFacingBack = 0;
    public const byte CameraFacingFront = 1;
    public const byte CameraFacingUnchanged = 0xFF;

    public const ushort FlagKeyframe = 0x0001;
    public const ushort FlagEos = 0x0002;

    public readonly record struct PacketHeader(
        ushort Magic,
        byte Version,
        byte Type,
        ushort Flags,
        ushort Reserved,
        uint Sequence,
        uint PayloadLength,
        long TimestampUs);

    public readonly record struct StreamInfo(
        int Width,
        int Height,
        int Fps,
        byte Codec,
        int RotationQuarters,
        uint Bitrate);

    public static PacketHeader ReadHeader(ReadOnlySpan<byte> src) =>
        new(
            Magic: BinaryPrimitives.ReadUInt16BigEndian(src[0..2]),
            Version: src[2],
            Type: src[3],
            Flags: BinaryPrimitives.ReadUInt16BigEndian(src[4..6]),
            Reserved: BinaryPrimitives.ReadUInt16BigEndian(src[6..8]),
            Sequence: BinaryPrimitives.ReadUInt32BigEndian(src[8..12]),
            PayloadLength: BinaryPrimitives.ReadUInt32BigEndian(src[12..16]),
            TimestampUs: BinaryPrimitives.ReadInt64BigEndian(src[16..24])
        );

    public static void WriteHeader(
        Span<byte> dst,
        byte type,
        ushort flags,
        uint sequence,
        uint payloadLength,
        long timestampUs)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst[0..2], Magic);
        dst[2] = Version;
        dst[3] = type;
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..6], flags);
        BinaryPrimitives.WriteUInt16BigEndian(dst[6..8], 0);
        BinaryPrimitives.WriteUInt32BigEndian(dst[8..12], sequence);
        BinaryPrimitives.WriteUInt32BigEndian(dst[12..16], payloadLength);
        BinaryPrimitives.WriteInt64BigEndian(dst[16..24], timestampUs);
    }

    public static StreamInfo ReadStreamInfo(ReadOnlySpan<byte> payload) =>
        new(
            Width: (int)BinaryPrimitives.ReadUInt32BigEndian(payload[0..4]),
            Height: (int)BinaryPrimitives.ReadUInt32BigEndian(payload[4..8]),
            Fps: BinaryPrimitives.ReadUInt16BigEndian(payload[8..10]),
            Codec: payload[10],
            RotationQuarters: payload[11],
            Bitrate: BinaryPrimitives.ReadUInt32BigEndian(payload[12..16])
        );

    public static void ReadAudioFrameHeader(ReadOnlySpan<byte> payload, out int sampleRate, out int channels, out int bitsPerSample)
    {
        sampleRate = BinaryPrimitives.ReadUInt16BigEndian(payload[0..2]);
        channels = payload[2];
        bitsPerSample = payload[3];
    }

    public static void WriteAudioControl(Span<byte> dst, bool enabled)
    {
        dst[0] = enabled ? (byte)1 : (byte)0;
        dst[1] = 0;
        dst[2] = 0;
        dst[3] = 0;
    }

    public static void WriteSetConfig(
        Span<byte> dst,
        ushort width = 0,
        ushort height = 0,
        ushort fps = 0,
        uint bitrate = 0,
        byte cameraFacing = CameraFacingUnchanged,
        byte flags = 0,
        ushort zoomX100 = 0)
    {
        BinaryPrimitives.WriteUInt16BigEndian(dst[0..2], width);
        BinaryPrimitives.WriteUInt16BigEndian(dst[2..4], height);
        BinaryPrimitives.WriteUInt16BigEndian(dst[4..6], fps);
        BinaryPrimitives.WriteUInt32BigEndian(dst[6..10], bitrate);
        dst[10] = cameraFacing;
        dst[11] = flags;
        BinaryPrimitives.WriteUInt16BigEndian(dst[12..14], zoomX100);
        BinaryPrimitives.WriteUInt16BigEndian(dst[14..16], 0);
    }
}


namespace Migurdex.Core.Utils;

public static class H264SpsParser
{
    public static (int Width, int Height)? TryParseFromTs(byte[]? data)
    {
        if (data == null)
        {
            return null;
        }

        return TryParseFromTs(data.AsSpan());
    }

    private static (int Width, int Height)? TryParseFromTs(ReadOnlySpan<byte> data)
    {
        if (data.Length < 32)
        {
            return null;
        }

        // search for NAL unit start prefix + NAL type 7 (SPS)
        for (var i = 0; i < data.Length - 5; i++)
        {
            int offset;

            switch (data[i])
            {
                case 0 when data[i + 1] == 0 && data[i + 2] == 1:
                    offset = i + 3;
                    break;
                case 0 when data[i + 1] == 0 && data[i + 2] == 0 && data[i + 3] == 1:
                    offset = i + 4;
                    break;
                default:
                    continue;
            }

            var nalType = data[offset] & 0x1F;
            if (nalType == 7) // SPS
            {
                var res = ParseSps(data[offset..]);
                if (res != null)
                {
                    return res;
                }
            }
        }

        return null;
    }

    public static string ToQualityString(int height)
    {
        return height switch
        {
            >= 2160 => "4K",
            >= 1440 => "1440p",
            >= 1000 => "1080p",
            >= 700  => "720p",
            >= 450  => "480p",
            >= 340  => "360p",
            >= 200  => "240p",
            _       => $"{height}p"
        };
    }

    private static (int Width, int Height)? ParseSps(ReadOnlySpan<byte> spsBytes)
    {
        try
        {
            var rbsp       = spsBytes.Length <= 512 ? stackalloc byte[spsBytes.Length] : new byte[spsBytes.Length];
            var rbspLength = 0;

            for (var i = 0; i < spsBytes.Length; i++)
            {
                if (i + 2 < spsBytes.Length && spsBytes[i] == 0 && spsBytes[i + 1] == 0 && spsBytes[i + 2] == 3)
                {
                    rbsp[rbspLength++] =  spsBytes[i];
                    rbsp[rbspLength++] =  spsBytes[i + 1];
                    i                  += 2;
                }
                else
                {
                    rbsp[rbspLength++] = spsBytes[i];
                }
            }

            var reader = new BitReader(rbsp[..rbspLength]);
            reader.ReadBits(8);

            var profileIdc         = reader.ReadBits(8);
            var constraintSetFlags = reader.ReadBits(8);
            var levelIdc           = reader.ReadBits(8);
            var spsId              = reader.ReadUe();

            if (profileIdc is 100 or 110 or 122 or 244 or 44 or 83 or 86 or 118 or 128 or 138 or 139 or 134 or 135)
            {
                var chromaFormatIdc = reader.ReadUe();
                if (chromaFormatIdc == 3)
                {
                    reader.ReadBit(); // separate_colour_plane_flag
                }

                reader.ReadUe(); // bit_depth_luma_minus8
                reader.ReadUe(); // bit_depth_chroma_minus8
                reader.ReadBit(); // qpprime_y_zero_transform_bypass_flag
                var seqScalingMatrixPresent = reader.ReadBit() != 0;
                if (seqScalingMatrixPresent)
                {
                    var count = chromaFormatIdc != 3 ? 8 : 12;
                    for (var j = 0; j < count; j++)
                    {
                        var seqScalingListPresent = reader.ReadBit() != 0;
                        if (seqScalingListPresent)
                        {
                            var size      = j < 6 ? 16 : 64;
                            var lastScale = 8;
                            var nextScale = 8;
                            for (var k = 0; k < size; k++)
                            {
                                if (nextScale != 0)
                                {
                                    var deltaScale = reader.ReadSe();
                                    nextScale = (lastScale + deltaScale + 256) % 256;
                                }

                                lastScale = nextScale != 0 ? nextScale : lastScale;
                            }
                        }
                    }
                }
            }

            reader.ReadUe(); // log2_max_frame_num_minus4
            var picOrderCntType = reader.ReadUe();
            if (picOrderCntType == 0)
            {
                reader.ReadUe(); // log2_max_pic_order_cnt_lsb_minus4
            }
            else if (picOrderCntType == 1)
            {
                reader.ReadBit(); // delta_pic_order_always_zero_flag
                reader.ReadSe(); // offset_for_non_ref_pic
                reader.ReadSe(); // offset_for_top_to_bottom_field
                var numRefFramesInPicOrderCntCycle = reader.ReadUe();
                for (var j = 0; j < numRefFramesInPicOrderCntCycle; j++)
                {
                    reader.ReadSe();
                }
            }

            reader.ReadUe(); // max_num_ref_frames
            reader.ReadBit(); // gaps_in_frame_num_value_allowed_flag

            var picWidthInMbsMinus1       = reader.ReadUe();
            var picHeightInMapUnitsMinus1 = reader.ReadUe();
            var frameMbsOnlyFlag          = reader.ReadBit() != 0;
            if (!frameMbsOnlyFlag)
            {
                reader.ReadBit(); // mb_adaptive_frame_field_flag
            }

            reader.ReadBit(); // direct_8x8_inference_flag
            var frameCroppingFlag = reader.ReadBit() != 0;
            var cropLeft          = 0;
            var cropRight         = 0;
            var cropTop           = 0;
            var cropBottom        = 0;
            if (frameCroppingFlag)
            {
                cropLeft   = reader.ReadUe();
                cropRight  = reader.ReadUe();
                cropTop    = reader.ReadUe();
                cropBottom = reader.ReadUe();
            }

            var width = ((picWidthInMbsMinus1 + 1) * 16) - ((cropLeft + cropRight) * 2);
            var height = ((2 - (frameMbsOnlyFlag ? 1 : 0)) * (picHeightInMapUnitsMinus1 + 1) * 16)
                         - ((cropTop + cropBottom) * 2);

            if (width > 0 && height > 0)
            {
                return (width, height);
            }
        }
        catch
        {
            // ignored
        }

        return null;
    }

    private ref struct BitReader
    {
        private readonly ReadOnlySpan<byte> _buffer;
        private          int                _bitPosition;

        public BitReader(ReadOnlySpan<byte> buffer)
        {
            _buffer      = buffer;
            _bitPosition = 0;
        }

        public int ReadBit()
        {
            var byteIndex = _bitPosition / 8;
            var bitIndex  = 7 - (_bitPosition % 8);
            _bitPosition++;

            if (byteIndex >= _buffer.Length)
            {
                return 0;
            }

            return (_buffer[byteIndex] >> bitIndex) & 1;
        }

        public int ReadBits(int n)
        {
            var value = 0;
            for (var i = 0; i < n; i++)
            {
                value = (value << 1) | ReadBit();
            }

            return value;
        }

        public int ReadUe()
        {
            var leadingZeros = 0;
            while (ReadBit() == 0 && leadingZeros < 32)
            {
                leadingZeros++;
            }

            if (leadingZeros == 0)
            {
                return 0;
            }

            var info = ReadBits(leadingZeros);

            return (1 << leadingZeros) - 1 + info;
        }

        public int ReadSe()
        {
            var ue = ReadUe();
            if (ue % 2 == 0)
            {
                return -(ue / 2);
            }

            return (ue + 1) / 2;
        }
    }
}

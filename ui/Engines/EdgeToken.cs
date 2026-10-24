using System.Buffers.Binary;
using System.Globalization;
using System.Numerics;
using System.Text;

namespace ViaSonic.Engines;

/// <summary>
/// Edge 朗读接口的 Sec-MS-GEC 令牌（2026 年 10 月起的第 2 版），格式「CS64 校验 16 位-SHA256 摘要 64 位」，十六进制大写。
/// 被签名的文字和第 1 版一样：Windows 文件时间（向下取整到 5 分钟）拼上 TrustedClientToken。
/// 摘要是 SHA-256 的变体：消息前面有 57 字节固定的隐藏内容，所以第一块从第 14 轮的中间状态接着算，
/// 隐藏内容对消息扩展的贡献已经算好放在 ScheduleBase 里，另有几处常量被改过（*Fix）。
/// 算法和常量参考 rany2/edge-tts 的分析，这里按算法重新实现。
/// </summary>
internal static class EdgeToken
{
    private const int Hidden = 57;

    public static string Make(double unixSeconds, string clientToken)
    {
        double ticks = unixSeconds + 11644473600;
        ticks -= ticks % 300;
        ticks *= 1e7;
        var data = Encoding.ASCII.GetBytes(ticks.ToString("F0", CultureInfo.InvariantCulture) + clientToken);
        var digest = Digest(data);
        var (c0, c1) = Cs64(data, digest);
        var check = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(check, c0);
        BinaryPrimitives.WriteUInt32LittleEndian(check.AsSpan(4), c1);
        return Convert.ToHexString(check) + "-" + Convert.ToHexString(digest);
    }

    // ------------------------------------------------------------------ SHA-256 变体
    private static readonly uint[] K =
    [
        0x428A2F98, 0x71374491, 0xB5C0FBCF, 0xE9B5DBA5, 0x3956C25B, 0x59F111F1, 0x923F82A4, 0xAB1C5ED5,
        0xD807AA98, 0x12835B01, 0x243185BE, 0x550C7DC3, 0x72BE5D74, 0x80DEB1FE, 0x9BDC06A7, 0xC19BF174,
        0xE49B69C1, 0xEFBE4786, 0x0FC19DC6, 0x240CA1CC, 0x2DE92C6F, 0x4A7484AA, 0x5CB0A9DC, 0x76F988DA,
        0x983E5152, 0xA831C66D, 0xB00327C8, 0xBF597FC7, 0xC6E00BF3, 0xD5A79147, 0x06CA6351, 0x14292967,
        0x27B70A85, 0x2E1B2138, 0x4D2C6DFC, 0x53380D13, 0x650A7354, 0x766A0ABB, 0x81C2C92E, 0x92722C85,
        0xA2BFE8A1, 0xA81A664B, 0xC24B8B70, 0xC76C51A3, 0xD192E819, 0xD6990624, 0xF40E3585, 0x106AA070,
        0x19A4C116, 0x1E376C08, 0x2748774C, 0x34B0BCB5, 0x391C0CB3, 0x4ED8AA4A, 0x5B9CCA4F, 0x682E6FF3,
        0x748F82EE, 0x78A5636F, 0x84C87814, 0x8CC70208, 0x90BEFFFA, 0xA4506CEB, 0xBEF9A3F7, 0xC67178F2,
    ];

    /// <summary>标准初始值。</summary>
    private static readonly uint[] H0 = [0x6A09E667, 0xBB67AE85, 0x3C6EF372, 0xA54FF53A, 0x510E527F, 0x9B05688C, 0x1F83D9AB, 0x5BE0CD19];

    /// <summary>第一块跑完前 14 轮后的 a~h。</summary>
    private static readonly uint[] Round14 = [0xFD9FBEA2, 0x91243CE7, 0xBE499094, 0xCD2E1E0A, 0x6C006F49, 0x04BD35AB, 0xFEB0A7E4, 0x03EFFBD2];

    /// <summary>第一块 w[16..29] 里来自隐藏内容的部分。</summary>
    private static readonly uint[] ScheduleBase =
    [
        0xEB24BF9E, 0x1558691F, 0xA0C09EE2, 0x0CFA32BA, 0xA622EE8C, 0xD1DD9491, 0xC0DD25A0,
        0xF9E70AB6, 0xF0C31573, 0x7058CE4E, 0x7188E606, 0x00E5AB33, 0xD258B060, 0x72766564,
    ];

    private const uint K14Fix = 0xC9DC06A7;   // 第一块第 14 轮用的常量
    private const uint Sig0Fix = 0x059C0B80;  // w[29] 的 σ0 项要异或的值
    private const uint Sig1Fix = 0x000B92C0;  // w[16] 的 σ1 项要异或的值

    private static uint Sig0(uint x) => BitOperations.RotateRight(x, 7) ^ BitOperations.RotateRight(x, 18) ^ (x >> 3);
    private static uint Sig1(uint x) => BitOperations.RotateRight(x, 17) ^ BitOperations.RotateRight(x, 19) ^ (x >> 10);

    private static byte[] Digest(byte[] data)
    {
        // 隐藏内容 + data 的前 7 字节正好是第一块，剩下的照常补位，长度按整条消息算
        int rest = data.Length - 7;
        var msg = new byte[(rest + 9 + 63) / 64 * 64];
        data.AsSpan(7).CopyTo(msg);
        msg[rest] = 0x80;
        BinaryPrimitives.WriteUInt64BigEndian(msg.AsSpan(msg.Length - 8), (ulong)(Hidden + data.Length) * 8);

        var state = FirstBlock(data);
        var w = new uint[64];
        for (int off = 0; off < msg.Length; off += 64)
        {
            for (int i = 0; i < 16; i++) w[i] = BinaryPrimitives.ReadUInt32BigEndian(msg.AsSpan(off + i * 4));
            for (int i = 16; i < 64; i++) w[i] = Sig1(w[i - 2]) + w[i - 7] + Sig0(w[i - 15]) + w[i - 16];
            var work = Rounds(state, w, 0, i => K[i]);
            for (int i = 0; i < 8; i++) state[i] += work[i];
        }
        var digest = new byte[32];
        for (int i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32BigEndian(digest.AsSpan(i * 4), state[i]);
        return digest;
    }

    /// <summary>第一块（隐藏内容 + data 前 7 字节）处理完后的状态。</summary>
    private static uint[] FirstBlock(byte[] data)
    {
        var w = new uint[64];
        w[14] = (uint)(data[0] << 16 | data[1] << 8 | data[2]);
        w[15] = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(3));
        for (int i = 16; i < 30; i++)
        {
            uint v = ScheduleBase[i - 16] + (i == 16 ? Sig1(w[i - 2]) ^ Sig1Fix : Sig1(w[i - 2]));
            if (i >= 21) v += w[i - 7];
            if (i >= 29) v += i == 29 ? Sig0(w[i - 15]) ^ Sig0Fix : Sig0(w[i - 15]);
            w[i] = v;
        }
        w[30] = Sig1(w[28]) + w[23] + Sig0(w[15]) + w[14] + K14Fix - K[14];
        for (int i = 31; i < 64; i++) w[i] = Sig1(w[i - 2]) + w[i - 7] + Sig0(w[i - 15]) + w[i - 16];
        var work = Rounds(Round14, w, 14, i => i == 14 ? K14Fix : K[i]);
        var state = new uint[8];
        for (int i = 0; i < 8; i++) state[i] = H0[i] + work[i];
        return state;
    }

    /// <summary>标准 SHA-256 轮函数，从 first 轮跑到第 63 轮，返回 a~h。</summary>
    private static uint[] Rounds(uint[] start, uint[] w, int first, Func<int, uint> k)
    {
        uint a = start[0], b = start[1], c = start[2], d = start[3], e = start[4], f = start[5], g = start[6], h = start[7];
        for (int i = first; i < 64; i++)
        {
            uint s1 = BitOperations.RotateRight(e, 6) ^ BitOperations.RotateRight(e, 11) ^ BitOperations.RotateRight(e, 25);
            uint t1 = h + s1 + (e & f ^ ~e & g) + k(i) + w[i];
            uint s0 = BitOperations.RotateRight(a, 2) ^ BitOperations.RotateRight(a, 13) ^ BitOperations.RotateRight(a, 22);
            uint t2 = s0 + (a & b ^ a & c ^ b & c);
            h = g; g = f; f = e; e = d + t1;
            d = c; c = b; b = a; a = t1 + t2;
        }
        return [a, b, c, d, e, f, g, h];
    }

    // ------------------------------------------------------------------ CS64 校验
    /// <summary>14 组 (系数, 偏移)：系数乘上由摘要得到的因子作为前 14 项，偏移原样使用。</summary>
    private static readonly (uint Coef, uint Offset)[] Cs64Seed =
    [
        (0xE82E91B1, 0xB32700D0), (0x44DE24C6, 0x88DFF51D), (0x3BEB65BC, 0x29514038), (0xC89C5933, 0xB328CBDF),
        (0x8D1EC211, 0xD405B4D5), (0x314EF5D5, 0x26E55F3C), (0xE92491B1, 0x607801C6), (0xF0A5B9D5, 0x82B2C1FB),
        (0x06064CF3, 0xFA51A3DA), (0xDA79FA60, 0x207CC464), (0xBED20560, 0x3C3C74F3), (0x89F9C9F8, 0xD2D14B61),
        (0x35FA38C6, 0xBB04A2C1), (0xCB49E3C6, 0xC5626167),
    ];

    private static uint Rol16(uint x) => BitOperations.RotateLeft(x, 16);

    private static (uint, uint) Cs64(byte[] data, byte[] digest)
    {
        uint keyA = BinaryPrimitives.ReadUInt32LittleEndian(digest) | 1;
        uint keyB = BinaryPrimitives.ReadUInt32LittleEndian(digest.AsSpan(4)) | 1;
        uint[] factor = [keyA * 0x82893933, keyB * 0x82893933];

        var terms = new List<uint>();
        var offsets = new List<uint>();
        for (int i = 0; i < Cs64Seed.Length; i++)
        {
            terms.Add(Cs64Seed[i].Coef * factor[i % 2]);
            offsets.Add(Cs64Seed[i].Offset);
        }
        // 前面补一个 0 字节，按小端 32 位分组，不足补零，组数补成偶数
        var bytes = new byte[(data.Length + 1 + 3) / 4 * 4];
        data.CopyTo(bytes, 1);
        int words = bytes.Length / 4;
        for (int i = 0; i < words + words % 2; i++)
        {
            uint word = i < words ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(i * 4)) : 0;
            bool even = i % 2 == 0;
            uint term = word * (even ? keyA : keyB);
            uint offset = Rol16(word) * (even ? 0xEF0569FB : 0xC31713DB);
            if (i == 0)
            {
                term += 0xF2F90B1A * factor[0];
                offset += 0x0B1A0000;
            }
            terms.Add(term);
            offsets.Add(offset);
        }

        uint mixSum = 0, mixLast = 0, state = 0, sum = 0;
        for (int i = 0; i < terms.Count; i += 2)
        {
            uint x = terms[i], y = terms[i + 1];
            uint q = Rol16(offsets[i] + x) * 0x76F50F34;
            mixSum += Rol16(q) * 0xC3EFEA97 + q;
            q = Rol16(offsets[i + 1] + y) * 0x1881CE3C;
            mixLast = Rol16(q) * 0x35BD1EC9 + q;
            mixSum += mixLast;

            state = state * keyA + x;
            state = Rol16(state) * 0xCF98B111;
            state = Rol16(state) * 0x87085B9F;
            state = Rol16(state) * 0x12CEB96D;
            state = Rol16(state) * 0x257E1D83;
            sum += state;
            uint t = state * keyB + y;
            t = Rol16(t) * 0xA27416F5;
            t = Rol16(t) * 0xD38396FF;
            state = Rol16(Rol16(t) * 0x7C932B89) * 0xBFA49F69;
            sum += state;
        }
        return (mixLast ^ state, mixSum ^ sum);
    }
}

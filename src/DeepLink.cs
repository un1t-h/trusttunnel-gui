using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TrustTunnelGui
{
    /// <summary>
    /// Ссылки tt://?... по спецификации DEEP_LINK.md (версия 2):
    /// base64url без паддинга, внутри TLV-поток с varint (RFC 9000 §16).
    /// </summary>
    public static class DeepLink
    {
        public const int MaxVersion = 2;
        const string Prefix = "tt://?";

        public static bool LooksLikeLink(string s)
        {
            return s != null && s.Trim().StartsWith("tt://", StringComparison.OrdinalIgnoreCase);
        }

        public static Profile Decode(string uri)
        {
            uri = (uri ?? "").Trim();
            if (!uri.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
                throw new FormatException("Ссылка должна начинаться с tt://?");
            byte[] data = Base64UrlDecode(uri.Substring(Prefix.Length));

            var p = new Profile();
            p.Addresses.Clear();
            int version = 0;
            bool hasHost = false, hasUser = false, hasPass = false;
            int pos = 0;
            while (pos < data.Length)
            {
                ulong tag = ReadVarint(data, ref pos);
                ulong len = ReadVarint(data, ref pos);
                if (len > (ulong)(data.Length - pos)) throw new FormatException("Повреждённая ссылка: длина поля выходит за границы");
                int n = (int)len;
                byte[] v = new byte[n];
                Buffer.BlockCopy(data, pos, v, 0, n);
                pos += n;

                switch (tag)
                {
                    case 0x00: { int i = 0; version = (int)ReadVarint(v, ref i); break; }
                    case 0x01: p.Hostname = Str(v); hasHost = true; break;
                    case 0x02: p.Addresses.Add(Str(v)); break;
                    case 0x03: p.CustomSni = Str(v); break;
                    case 0x04: p.HasIpv6 = Bool(v); break;
                    case 0x05: p.Username = Str(v); hasUser = true; break;
                    case 0x06: p.Password = Str(v); hasPass = true; break;
                    case 0x07: p.SkipVerification = Bool(v); break;
                    case 0x08: p.Certificate = DerToPem(v); break;
                    case 0x09:
                        {
                            int i = 0;
                            ulong proto = ReadVarint(v, ref i);
                            p.UpstreamProtocol = proto == 2 ? "http3" : "http2";
                            break;
                        }
                    case 0x0A: p.AntiDpi = Bool(v); break;
                    case 0x0B: p.ClientRandom = Str(v); break;
                    case 0x0C: p.Name = Str(v); break;
                    case 0x0D: p.DnsUpstreams = StrArray(v); break;
                    case 0x0E: p.SubscriptionUrl = Str(v); break;
                    case 0x0F: p.ClientRandomAuthKey = Str(v); break;
                    default: break; // неизвестные теги игнорируются
                }
            }

            if (version > MaxVersion)
                throw new FormatException("Ссылка версии " + version + " не поддерживается (максимум " + MaxVersion + ")");
            bool hasSub = !string.IsNullOrEmpty(p.SubscriptionUrl);
            if (hasSub && !p.SubscriptionUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                throw new FormatException("subscription_url должен начинаться с https://");
            if (!hasSub && (!hasHost || p.Addresses.Count == 0 || !hasUser || !hasPass))
                throw new FormatException("В ссылке нет обязательных полей (hostname, addresses, username, password)");
            if (!hasHost || p.Addresses.Count == 0)
                throw new FormatException("Ссылка содержит только subscription_url — такой формат пока не поддерживается, нужна полная ссылка с адресом сервера");
            return p;
        }

        public static string Encode(Profile p)
        {
            var ms = new MemoryStream();
            WriteTlv(ms, 0x00, Varint(MaxVersion));
            WriteStr(ms, 0x01, p.Hostname);
            foreach (var a in p.Addresses) WriteStr(ms, 0x02, a);
            if (!string.IsNullOrEmpty(p.CustomSni)) WriteStr(ms, 0x03, p.CustomSni);
            if (!p.HasIpv6) WriteTlv(ms, 0x04, new byte[] { 0 });
            WriteStr(ms, 0x05, p.Username);
            WriteStr(ms, 0x06, p.Password);
            if (p.SkipVerification) WriteTlv(ms, 0x07, new byte[] { 1 });
            if (!string.IsNullOrWhiteSpace(p.Certificate)) WriteTlv(ms, 0x08, PemToDer(p.Certificate));
            WriteTlv(ms, 0x09, Varint(p.UpstreamProtocol == "http3" ? 2UL : 1UL));
            if (p.AntiDpi) WriteTlv(ms, 0x0A, new byte[] { 1 });
            if (!string.IsNullOrEmpty(p.ClientRandom)) WriteStr(ms, 0x0B, p.ClientRandom);
            if (!string.IsNullOrEmpty(p.Name)) WriteStr(ms, 0x0C, p.Name);
            if (p.DnsUpstreams.Count > 0)
            {
                var arr = new MemoryStream();
                foreach (var d in p.DnsUpstreams)
                {
                    var b = Encoding.UTF8.GetBytes(d);
                    var l = Varint((ulong)b.Length);
                    arr.Write(l, 0, l.Length);
                    arr.Write(b, 0, b.Length);
                }
                WriteTlv(ms, 0x0D, arr.ToArray());
            }
            if (!string.IsNullOrEmpty(p.SubscriptionUrl)) WriteStr(ms, 0x0E, p.SubscriptionUrl);
            if (!string.IsNullOrEmpty(p.ClientRandomAuthKey)) WriteStr(ms, 0x0F, p.ClientRandomAuthKey);
            return Prefix + Base64UrlEncode(ms.ToArray());
        }

        // ---- varint ----

        public static ulong ReadVarint(byte[] b, ref int pos)
        {
            if (pos >= b.Length) throw new FormatException("Повреждённая ссылка: неожиданный конец данных");
            int first = b[pos];
            int len = 1 << (first >> 6);
            if (pos + len > b.Length) throw new FormatException("Повреждённая ссылка: неполный varint");
            ulong v = (ulong)(first & 0x3F);
            for (int i = 1; i < len; i++) v = (v << 8) | b[pos + i];
            pos += len;
            return v;
        }

        public static byte[] Varint(ulong v)
        {
            if (v < 64) return new[] { (byte)v };
            if (v < 16384) return new[] { (byte)(0x40 | (v >> 8)), (byte)v };
            if (v < (1UL << 30))
                return new[] { (byte)(0x80 | (v >> 24)), (byte)(v >> 16), (byte)(v >> 8), (byte)v };
            var r = new byte[8];
            for (int i = 7; i >= 0; i--) { r[i] = (byte)v; v >>= 8; }
            r[0] |= 0xC0;
            return r;
        }

        static void WriteTlv(Stream s, ulong tag, byte[] value)
        {
            var t = Varint(tag);
            var l = Varint((ulong)value.Length);
            s.Write(t, 0, t.Length);
            s.Write(l, 0, l.Length);
            s.Write(value, 0, value.Length);
        }

        static void WriteStr(Stream s, ulong tag, string value)
        {
            WriteTlv(s, tag, Encoding.UTF8.GetBytes(value ?? ""));
        }

        // ---- значения ----

        static string Str(byte[] v) { return Encoding.UTF8.GetString(v); }

        static bool Bool(byte[] v)
        {
            if (v.Length != 1 || v[0] > 1) throw new FormatException("Повреждённая ссылка: некорректное логическое значение");
            return v[0] == 1;
        }

        static List<string> StrArray(byte[] v)
        {
            var list = new List<string>();
            int pos = 0;
            while (pos < v.Length)
            {
                int n = (int)ReadVarint(v, ref pos);
                if (pos + n > v.Length) throw new FormatException("Повреждённая ссылка: список DNS");
                list.Add(Encoding.UTF8.GetString(v, pos, n));
                pos += n;
            }
            return list;
        }

        // ---- base64url ----

        public static byte[] Base64UrlDecode(string s)
        {
            s = s.Trim().Replace('-', '+').Replace('_', '/').TrimEnd('=');
            switch (s.Length % 4)
            {
                case 1: throw new FormatException("Некорректная длина base64url");
                case 2: s += "=="; break;
                case 3: s += "="; break;
            }
            return Convert.FromBase64String(s);
        }

        public static string Base64UrlEncode(byte[] b)
        {
            return Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        }

        // ---- сертификаты ----

        public static string DerToPem(byte[] der)
        {
            var sb = new StringBuilder();
            int pos = 0;
            while (pos < der.Length)
            {
                int len = DerElementLength(der, pos);
                var b64 = Convert.ToBase64String(der, pos, len);
                sb.Append("-----BEGIN CERTIFICATE-----\n");
                for (int i = 0; i < b64.Length; i += 64)
                    sb.Append(b64.Substring(i, Math.Min(64, b64.Length - i))).Append('\n');
                sb.Append("-----END CERTIFICATE-----\n");
                pos += len;
            }
            return sb.ToString();
        }

        static int DerElementLength(byte[] b, int pos)
        {
            if (pos + 2 > b.Length || b[pos] != 0x30) return b.Length - pos; // не SEQUENCE — берём остаток целиком
            int lb = b[pos + 1];
            if (lb < 0x80) return Math.Min(2 + lb, b.Length - pos);
            int n = lb & 0x7F;
            if (n == 0 || n > 4 || pos + 2 + n > b.Length) return b.Length - pos;
            int len = 0;
            for (int i = 0; i < n; i++) len = (len << 8) | b[pos + 2 + i];
            return Math.Min(2 + n + len, b.Length - pos);
        }

        public static byte[] PemToDer(string pem)
        {
            const string begin = "-----BEGIN CERTIFICATE-----";
            const string end = "-----END CERTIFICATE-----";
            var ms = new MemoryStream();
            int idx = 0;
            while (true)
            {
                int b = pem.IndexOf(begin, idx, StringComparison.Ordinal);
                if (b < 0) break;
                int e = pem.IndexOf(end, b, StringComparison.Ordinal);
                if (e < 0) break;
                var body = pem.Substring(b + begin.Length, e - b - begin.Length);
                var sb = new StringBuilder();
                foreach (char c in body) if (!char.IsWhiteSpace(c)) sb.Append(c);
                var der = Convert.FromBase64String(sb.ToString());
                ms.Write(der, 0, der.Length);
                idx = e + end.Length;
            }
            return ms.ToArray();
        }
    }
}

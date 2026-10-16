using System.Security.Cryptography;
using System.Text;

namespace CleanPDF.Engine.Tests;

/// <summary>
/// Writes a one-page PDF protected with the standard security handler, revision 2: 40-bit RC4,
/// user password <see cref="Password"/>. Old, but every PDF reader supports it.
/// </summary>
internal static class EncryptedPdf
{
    public const string Password = "secret";
    public const string Text = "Protected page";

    private static readonly byte[] Pad =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    public static string Write(string path)
    {
        const int permissions = -44;
        byte[] id = MD5.HashData(Encoding.ASCII.GetBytes(path));
        byte[] padded = Padded(Password);

        // Owner key: same password as the user's, for simplicity.
        byte[] ownerKey = MD5.HashData(padded)[..5];
        byte[] o = Rc4(ownerKey, padded);

        byte[] fileKey = MD5.HashData([.. padded, .. o, .. BitConverter.GetBytes(permissions), .. id])[..5];
        byte[] u = Rc4(fileKey, Pad);

        string content = $"BT /Helvetica 24 Tf 72 700 Td ({Text}) Tj ET\n";
        byte[] stream = Rc4(ObjectKey(fileKey, 4), Encoding.Latin1.GetBytes(content));

        var parts = new List<(int Id, byte[] Body)>
        {
            (1, Latin("<< /Type /Catalog /Pages 2 0 R >>")),
            (2, Latin("<< /Type /Pages /Kids [3 0 R] /Count 1 >>")),
            (3, Latin("<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /Helvetica 5 0 R >> >> /Contents 4 0 R >>")),
            (4, [.. Latin($"<< /Length {stream.Length} >>\nstream\n"), .. stream, .. Latin("\nendstream")]),
            (5, Latin("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>")),
            (6, Latin($"<< /Filter /Standard /V 1 /R 2 /Length 40 /P {permissions} /O <{Hex(o)}> /U <{Hex(u)}> >>")),
        };

        using var fs = File.Create(path);
        var offsets = new long[parts.Count + 1];
        fs.Write(Latin("%PDF-1.4\n"));
        foreach (var (objId, body) in parts)
        {
            offsets[objId] = fs.Position;
            fs.Write(Latin($"{objId} 0 obj\n"));
            fs.Write(body);
            fs.Write(Latin("\nendobj\n"));
        }
        long xref = fs.Position;
        var sb = new StringBuilder($"xref\n0 {parts.Count + 1}\n0000000000 65535 f \n");
        for (int i = 1; i <= parts.Count; i++) sb.Append($"{offsets[i]:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {parts.Count + 1} /Root 1 0 R /Encrypt 6 0 R /ID [<{Hex(id)}> <{Hex(id)}>] >>\nstartxref\n{xref}\n%%EOF\n");
        fs.Write(Latin(sb.ToString()));
        return path;
    }

    private static byte[] Padded(string password)
    {
        var bytes = Encoding.Latin1.GetBytes(password);
        return [.. bytes.Take(32), .. Pad.Take(32 - Math.Min(32, bytes.Length))];
    }

    private static byte[] ObjectKey(byte[] fileKey, int objectId) =>
        MD5.HashData([.. fileKey, (byte)objectId, (byte)(objectId >> 8), (byte)(objectId >> 16), 0, 0])[..10];

    private static byte[] Rc4(byte[] key, byte[] data)
    {
        var s = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        var output = new byte[data.Length];
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }
        return output;
    }

    private static byte[] Latin(string s) => Encoding.Latin1.GetBytes(s);
    private static string Hex(byte[] bytes) => Convert.ToHexString(bytes);
}

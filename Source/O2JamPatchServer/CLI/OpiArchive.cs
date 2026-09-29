using System.Text;

namespace O2JamPatchServer.CLI;

public static class OpiArchive
{
    private const int NameSize = 128;

    public static bool IsPackage(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() is ".opi" or ".opa";
    }

    public static byte[] Pack(string directory)
    {
        if (Directory.EnumerateDirectories(directory).Any())
            throw new InvalidDataException($"{directory} cannot contain directories");

        string[] files = Directory.GetFiles(directory);
        if (files.Length == 0)
            throw new InvalidDataException($"{directory} contains no files");

        Array.Sort(files, StringComparer.OrdinalIgnoreCase);

        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);

        writer.Write(Path.GetExtension(directory).Equals(".opa", StringComparison.OrdinalIgnoreCase) ? 1 : 2);
        writer.Write(files.Length);
        writer.Write(0L);

        var headers = new List<(byte[] Name, int Offset, int Size)>();
        foreach (string file in files)
        {
            string name = Path.GetFileName(file);
            if (!Ascii.IsValid(name) || name.Length >= NameSize)
                throw new InvalidDataException($"{file}: entry names must be ASCII and shorter than {NameSize} characters");

            byte[] data = File.ReadAllBytes(file);
            byte[] nameBytes = new byte[NameSize];
            Encoding.ASCII.GetBytes(name, nameBytes);

            headers.Add((nameBytes, checked((int)stream.Position), data.Length));
            writer.Write(data);
        }

        foreach (var (name, offset, size) in headers)
        {
            writer.Write(1);
            writer.Write(name);
            writer.Write(offset);
            writer.Write(size);
            writer.Write(size);
            writer.Write(0L);
        }

        writer.Flush();
        return stream.ToArray();
    }
}

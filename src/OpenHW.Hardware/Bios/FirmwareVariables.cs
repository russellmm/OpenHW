namespace OpenHW.Hardware;

/// <summary>
/// The machine's UEFI variables, read-only. BIOS Setup keeps its settings in a few of them ("Setup",
/// "VARSTORE_OCMR_SETTINGS_NAME", "AmdSetupSHP" …) as byte arrays at fixed offsets, so reading them gives every Setup
/// option's current value. Writing is deliberately not offered yet: a bad Setup variable can stop the board booting
/// until CMOS is cleared; a guarded writer would sit behind this same interface.
/// </summary>
public interface IFirmwareVariables
{
    /// <summary>The variable's data (without the attribute word), or null when it doesn't exist or can't be read.</summary>
    byte[]? Read(string name, string guid);

    /// <summary>Names of the boot entries (Boot0000 …) in BootOrder order, as the firmware describes them.</summary>
    IReadOnlyList<string> BootEntries();
}

/// <summary>Linux efivarfs (/sys/firmware/efi/efivars): files are "&lt;Name&gt;-&lt;guid&gt;", 4 attribute bytes, then the data.</summary>
public sealed class LinuxFirmwareVariables(string root = LinuxFirmwareVariables.DefaultRoot) : IFirmwareVariables
{
    public const string DefaultRoot = "/sys/firmware/efi/efivars";

    public bool Available => Directory.Exists(root);

    public byte[]? Read(string name, string guid)
    {
        if (name.IndexOfAny(['/', '\0']) >= 0 || guid.IndexOfAny(['/', '\0']) >= 0) return null;
        try
        {
            var bytes = File.ReadAllBytes(Path.Combine(root, $"{name}-{guid.ToLowerInvariant()}"));
            return bytes.Length >= 4 ? bytes[4..] : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    private const string GlobalGuid = "8be4df61-93ca-11d2-aa0d-00e098032b8c";

    public IReadOnlyList<string> BootEntries()
    {
        var order = Read("BootOrder", GlobalGuid);
        if (order is null) return [];
        var names = new List<string>();
        for (var i = 0; i + 1 < order.Length; i += 2)
        {
            var number = order[i] | order[i + 1] << 8;
            if (Read($"Boot{number:X4}", GlobalGuid) is { } entry && Describe(entry) is { Length: > 0 } text) names.Add(text);
        }
        return names;
    }

    /// <summary>EFI_LOAD_OPTION: UINT32 attributes, UINT16 file-path length, then the NUL-terminated UCS-2 description.</summary>
    public static string? Describe(byte[] option)
    {
        if (option.Length < 8) return null;
        var end = 6;
        while (end + 1 < option.Length && (option[end] | option[end + 1]) != 0) end += 2;
        return System.Text.Encoding.Unicode.GetString(option, 6, end - 6).Trim();
    }
}

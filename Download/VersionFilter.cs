using SFTLauncher.Models;

namespace SFTLauncher.Download;

public static class VersionFilter
{
    public static List<MinecraftVersion> Apply(List<MinecraftVersion> versions, string filter)
    {
        return filter switch
        {
            "release"  => [.. versions.Where(v => v.Type == "release")],
            "snapshot" => [.. versions.Where(v => v.Type == "snapshot")],
            "old"      => [.. versions.Where(v => v.Type is "old_beta" or "old_alpha")],
            _          => versions
        };
    }
}

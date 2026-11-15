namespace DW2ModLauncher.Loader;

public static class LiveChannels
{
    public static readonly Dictionary<string, Delegate> Channels = new();
    public static Delegate? Get(string consumer, string channel, int contract, Type expected) =>
        Channels.TryGetValue(channel, out var value) && expected.IsInstanceOfType(value) ? value : null;
}

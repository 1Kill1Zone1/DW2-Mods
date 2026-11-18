namespace DW2ModLauncher.Loader;

public static class LiveChannels
{
    public static readonly Dictionary<string, Delegate> Channels = new();
    private static readonly Dictionary<string,string> Owners = new();
    public static Delegate? Provide(string provider,string channel,int contract,Delegate handler)
    {
        if(contract!=1)throw new ArgumentOutOfRangeException(nameof(contract));
        var previous=Channels.GetValueOrDefault(channel);
        Channels[channel]=handler;
        Owners[channel]=provider;
        return previous;
    }
    public static string? ProviderOf(string channel)=>Owners.GetValueOrDefault(channel);
    public static Delegate? Get(string consumer, string channel, int contract, Type expected) =>
        contract==1 && Channels.TryGetValue(channel, out var value) && expected.IsInstanceOfType(value) ? value : null;
}

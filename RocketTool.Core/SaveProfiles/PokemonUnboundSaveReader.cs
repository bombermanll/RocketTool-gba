namespace RocketTool.Core;

internal static class PokemonUnboundSaveReader
{
    public static Gen3SaveDocument Open(string path, GameProfile profile)
        => Gen3SaveReader.OpenUnbound(path, profile);
}

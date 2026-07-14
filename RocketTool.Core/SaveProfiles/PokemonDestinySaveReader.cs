namespace RocketTool.Core;

internal static class PokemonDestinySaveReader
{
    public static Gen3SaveDocument Open(string path, GameProfile profile)
        => Gen3SaveReader.OpenDestiny(path, profile);
}

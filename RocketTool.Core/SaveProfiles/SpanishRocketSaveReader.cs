namespace RocketTool.Core;

internal static class SpanishRocketSaveReader
{
    public static Gen3SaveDocument Open(string path, GameProfile profile)
        => Gen3SaveReader.OpenSpanishRocket(path, profile);
}

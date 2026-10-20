namespace CardViewer;

public sealed record EmbeddedJpeg(long Offset, int Length, int Width, int Height);
public sealed record HifIndex(EmbeddedJpeg[] Previews, int Rotation, int? Mirror);


namespace CardViewer;

public interface IThumbnailDecoder
{
    byte[] Decode(byte[] source);
}

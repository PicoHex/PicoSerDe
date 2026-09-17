using PicoSerDe.Core;

namespace CollisionNs.Sub
{
    [PicoSerializable]
    public class Inner
    {
        public int V { get; set; }
    }
}

namespace CollisionNs
{
    [PicoSerializable]
    public class Sub_Inner
    {
        public int W { get; set; }
    }
}
